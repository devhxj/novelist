using System.Text.Json;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public sealed record ReferenceAdvancedMaterialSpecimenAnalysisInput(
    long AnchorId,
    string NodeId,
    string NodeType,
    string NodeText,
    int? MaxOutputTokens = null);

// 机理层分析器：给定一个节点文本，产出"为什么这样写"的机理草稿 JSON。
// 硬门（boundary 四件套、迁移骨架）在摄入层强制执行，解析器只负责形状与回填 node_id。
public interface IReferenceAdvancedMaterialSpecimenAnalyzer
{
    ValueTask<ReferenceAdvancedMaterialAnalysisOutput> AnalyzeAsync(
        ReferenceAdvancedMaterialSpecimenAnalysisInput input,
        CancellationToken cancellationToken);
}

public static class ReferenceAdvancedMaterialSpecimenAnalysisParser
{
    public static IReadOnlyList<AdvancedMaterialSpecimenDraft> ParseSpecimens(string json, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(nodeId))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("specimens", out var specimens) ||
                specimens.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var drafts = new List<AdvancedMaterialSpecimenDraft>();
            foreach (var item in specimens.EnumerateArray())
            {
                var draft = ParseSpecimen(item, nodeId);
                if (draft is not null)
                {
                    drafts.Add(draft);
                }
            }

            return drafts;
        }
    }

    private static AdvancedMaterialSpecimenDraft? ParseSpecimen(JsonElement item, string nodeId)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !TryReadString(item, "family", out var family) ||
            !ReferenceAdvancedMaterialFamilies.IsSupported(family) ||
            !TryReadString(item, "feature_key", out var featureKey) ||
            !TryReadString(item, "abstract", out var abstractText) ||
            !TryReadString(item, "transfer_template", out var transferTemplate) ||
            !item.TryGetProperty("confidence", out var confidence) ||
            confidence.ValueKind != JsonValueKind.Number ||
            !confidence.TryGetDouble(out var confidenceValue) ||
            !item.TryGetProperty("evidence", out var evidence) ||
            evidence.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var evidenceDrafts = ParseEvidence(evidence, nodeId);
        if (evidenceDrafts.Count == 0)
        {
            return null;
        }

        return new AdvancedMaterialSpecimenDraft(
            Family: family,
            FeatureKey: featureKey,
            Abstract: abstractText,
            TriggerContext: ReadStringOrDefault(item, "trigger_context"),
            WhyItWorks: ReadStringArray(item, "why_it_works"),
            EffectOnReader: ReadStringOrDefault(item, "effect_on_reader"),
            TransferTemplate: transferTemplate,
            TransferSlots: ReadStringMap(item, "transfer_slots"),
            WorldContextDependencies: ReadStringArray(item, "world_context_dependencies"),
            FailureModes: ReadStringArray(item, "failure_modes"),
            AntiPatterns: ReadStringArray(item, "anti_patterns"),
            Confidence: confidenceValue,
            Evidence: evidenceDrafts);
    }

    private static IReadOnlyList<AdvancedMaterialEvidenceDraft> ParseEvidence(JsonElement evidence, string nodeId)
    {
        var drafts = new List<AdvancedMaterialEvidenceDraft>();
        foreach (var span in evidence.EnumerateArray())
        {
            if (span.ValueKind != JsonValueKind.Object ||
                !span.TryGetProperty("start", out var start) ||
                !start.TryGetInt32(out var startValue) ||
                !span.TryGetProperty("end", out var end) ||
                !end.TryGetInt32(out var endValue))
            {
                return [];
            }

            drafts.Add(new AdvancedMaterialEvidenceDraft(nodeId, startValue, endValue));
        }

        return drafts;
    }

    private static bool TryReadString(JsonElement item, string property, out string value)
    {
        value = string.Empty;
        if (item.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(element.GetString()))
        {
            value = element.GetString()!;
            return true;
        }

        return false;
    }

    private static string ReadStringOrDefault(JsonElement item, string property) =>
        TryReadString(item, property, out var value) ? value : string.Empty;

    private static IReadOnlyList<string> ReadStringArray(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return element
            .EnumerateArray()
            .Where(entry => entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
            .Select(entry => entry.GetString()!)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement item, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!item.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String)
            {
                result[entry.Name] = entry.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }
}
