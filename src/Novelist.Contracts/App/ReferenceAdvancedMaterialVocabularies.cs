namespace Novelist.Contracts.App;

// 高级写作素材（L2）各 family 的 feature_key 词表（封闭，冻结于 2026-09-14）。
// technique 复用材料化既有技法词表；style 由 ReferenceStyleTaxonomy 直接派生。
public sealed record ReferenceAdvancedMaterialFeatureDefinition(
    string FeatureKey,
    IReadOnlyList<string> Values,
    string Description);

public static class ReferenceAdvancedMaterialFeatureVocabulary
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ReferenceAdvancedMaterialFeatureDefinition>> Definitions =
        new Dictionary<string, IReadOnlyList<ReferenceAdvancedMaterialFeatureDefinition>>(StringComparer.Ordinal)
        {
            [ReferenceAdvancedMaterialFamilies.World] =
            [
                new("world_introduction",
                    ["action_embedded", "dialogue_revealed", "narration_aside", "environmental_detail", "document_artifact", "delayed_reveal"],
                    "设定以何种方式被引入。"),
                new("world_pressure",
                    ["constraint_on_choice", "cost_of_action", "social_consequence", "physical_limit", "information_asymmetry"],
                    "设定如何对人物施加压力。"),
                new("world_consistency",
                    ["rule_stated_then_shown", "rule_shown_only", "rule_broken_with_reason"],
                    "设定规则的一致性与呈现方式。")
            ],
            [ReferenceAdvancedMaterialFamilies.Craft] =
            [
                new("information_delivery",
                    ["front_loaded", "drip_fed", "deferred", "withheld", "revealed_through_action"],
                    "信息投放策略。"),
                new("viewpoint_control",
                    ["tight_internal", "limited_external", "camera_pull_back", "head_hop_risk"],
                    "视角控制方式。"),
                new("scene_progression",
                    ["escalate", "oscillate", "compress", "slow_burn", "cut_away"],
                    "场景推进策略。"),
                new("emotion_externalization",
                    ["body_afterbeat", "object_handling", "silence", "displaced_dialogue", "interiority"],
                    "情感外化策略。")
            ],
            // 与 ReferenceMaterializationChatCompletionQualifier.AllowedTechniques 保持一致，
            // 两端词表扩展时须同步（对齐 corpusTaxonomy.ts 的双向同步约定）。
            [ReferenceAdvancedMaterialFamilies.Technique] =
            [
                new("technique_kind",
                    ["callback", "contrast", "delayed_reaction", "dialogue_turn", "foreshadowing", "free_indirect_discourse", "rhythm_shift", "sensory_detail", "subtext", "withholding"],
                    "可点名的具体技法。")
            ],
            [ReferenceAdvancedMaterialFamilies.Structure] =
            [
                new("hook_type",
                    ["question_tail", "threat_arrival", "reversal_tail", "promise_open", "cliffhanger"],
                    "钩子类型。"),
                new("payoff_type",
                    ["answer_reveal", "emotional_release", "tactical_gain", "promise_fulfilled"],
                    "兑现类型。"),
                new("pacing_shape",
                    ["accelerating", "decelerating", "wave", "flat_then_spike"],
                    "节奏形态。"),
                new("transition_mode",
                    ["time_jump", "causal_bridge", "scene_cut", "tonal_bridge"],
                    "过渡方式。")
            ],
            // style 由既有 taxonomy 派生：feature_key = taxonomy 键，values = 该键允许的标签。
            [ReferenceAdvancedMaterialFamilies.Style] = ReferenceStyleTaxonomy.Features
                .Select(feature => new ReferenceAdvancedMaterialFeatureDefinition(
                    feature.FeatureKey,
                    feature.Labels,
                    feature.Description))
                .ToArray()
        };

    private static readonly IReadOnlyDictionary<string, ReferenceAdvancedMaterialFeatureDefinition> ByFeatureKey =
        Definitions
            .SelectMany(pair => pair.Value)
            .GroupBy(definition => definition.FeatureKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    public static IReadOnlyList<ReferenceAdvancedMaterialFeatureDefinition> ForFamily(string family)
    {
        if (!ReferenceAdvancedMaterialFamilies.IsSupported(family))
        {
            throw new ArgumentException($"Unsupported advanced material family '{family}'.", nameof(family));
        }

        return Definitions[family];
    }

    public static IReadOnlyList<string> FeatureKeys(string family) =>
        ForFamily(family).Select(definition => definition.FeatureKey).ToArray();

    public static bool IsSupportedFeatureKey(string family, string? featureKey)
    {
        if (!ReferenceAdvancedMaterialFamilies.IsSupported(family) || string.IsNullOrWhiteSpace(featureKey))
        {
            return false;
        }

        return Definitions[family].Any(definition => string.Equals(definition.FeatureKey, featureKey, StringComparison.Ordinal));
    }

    public static bool IsSupportedValue(string family, string featureKey, string? value)
    {
        if (!ReferenceAdvancedMaterialFamilies.IsSupported(family) || string.IsNullOrWhiteSpace(featureKey))
        {
            return false;
        }

        var definition = Definitions[family].FirstOrDefault(item =>
            string.Equals(item.FeatureKey, featureKey, StringComparison.Ordinal));
        return definition is not null &&
            !string.IsNullOrWhiteSpace(value) &&
            definition.Values.Contains(value, StringComparer.Ordinal);
    }

    internal static ReferenceAdvancedMaterialFeatureDefinition? Find(string featureKey)
    {
        return ByFeatureKey.TryGetValue(featureKey, out var definition) ? definition : null;
    }
}
