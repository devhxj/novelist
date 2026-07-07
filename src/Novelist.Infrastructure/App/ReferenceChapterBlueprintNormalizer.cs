using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;

namespace Novelist.Infrastructure.App;

internal sealed record ReferenceChapterBlueprintContextPack(
    long NovelId,
    int ChapterNumber,
    string? SourcePlanScope,
    string? SourcePlanContent,
    string? ChapterGoal,
    IReadOnlyList<long>? AnchorIds,
    IReadOnlyList<string>? KnownFacts,
    IReadOnlyList<string>? ForbiddenFacts);

internal static class ReferenceChapterBlueprintNormalizer
{
    private static readonly JsonSerializerOptions JsonOptions = BridgeJson.SerializerOptions;

    public static string ComputeContextHash(ReferenceChapterBlueprintContextPack contextPack)
    {
        ArgumentNullException.ThrowIfNull(contextPack);
        return HashText(NormalizeContextPackJson(contextPack));
    }

    public static string ComputeSourcePlanHash(string? sourcePlanScope, string? sourcePlanContent)
    {
        var scope = string.IsNullOrWhiteSpace(sourcePlanScope) ? "next" : sourcePlanScope;
        return HashText(scope + "\n" + (sourcePlanContent ?? string.Empty));
    }

    public static string NormalizeContextPackJson(ReferenceChapterBlueprintContextPack contextPack)
    {
        ArgumentNullException.ThrowIfNull(contextPack);
        var normalized = new
        {
            contextPack.NovelId,
            contextPack.ChapterNumber,
            SourcePlanScope = NormalizeString(contextPack.SourcePlanScope),
            SourcePlanContent = NormalizeString(contextPack.SourcePlanContent),
            ChapterGoal = NormalizeString(contextPack.ChapterGoal),
            AnchorIds = NormalizeAnchorIds(contextPack.AnchorIds),
            KnownFacts = NormalizeContextStringList(contextPack.KnownFacts),
            ForbiddenFacts = NormalizeContextStringList(contextPack.ForbiddenFacts)
        };
        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    public static string ComputeAnalysisContractHash(ReferenceChapterBlueprintPayload blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        return HashText(NormalizeAnalysisContractJson(blueprint));
    }

    public static string NormalizeAnalysisContractJson(ReferenceChapterBlueprintPayload blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var contract = new
        {
            LogicAnalysis = NormalizeTrack(blueprint.LogicAnalysis),
            EmotionAnalysis = NormalizeTrack(blueprint.EmotionAnalysis),
            NarrationAnalysis = NormalizeTrack(blueprint.NarrationAnalysis),
            CharacterAnalysis = NormalizeTrack(blueprint.CharacterAnalysis),
            ReferenceAnalysis = NormalizeTrack(blueprint.ReferenceAnalysis),
            TransitionPlan = NormalizeTrack(blueprint.TransitionPlan),
            ExecutionContract = NormalizeExecutionTrack(blueprint.ExecutionContract),
            KnownFacts = NormalizeStringList(blueprint.KnownFacts),
            ForbiddenFacts = NormalizeStringList(blueprint.ForbiddenFacts),
            beats = (blueprint.Beats ?? [])
                .Where(beat => beat is not null)
                .Select(beat => new
                {
                    BeatId = NormalizeString(beat.BeatId),
                    beat.BeatIndex,
                    NarrativeFunction = NormalizeString(beat.NarrativeFunction),
                    CausalityIn = NormalizeString(beat.CausalityIn),
                    CausalityOut = NormalizeString(beat.CausalityOut),
                    TransitionIn = NormalizeString(beat.TransitionIn),
                    TransitionOut = NormalizeString(beat.TransitionOut),
                    PovCharacter = NormalizeString(beat.PovCharacter),
                    NarrativeDistance = NormalizeString(beat.NarrativeDistance),
                    ViewpointAllowedKnowledge = NormalizeStringList(beat.ViewpointAllowedKnowledge),
                    ViewpointForbiddenKnowledge = NormalizeStringList(beat.ViewpointForbiddenKnowledge),
                    CharacterStatesBefore = NormalizeStringList(beat.CharacterStatesBefore),
                    CharacterStatesAfter = NormalizeStringList(beat.CharacterStatesAfter),
                    EmotionTrigger = NormalizeString(beat.EmotionTrigger),
                    EmotionBefore = NormalizeString(beat.EmotionBefore),
                    EmotionAfter = NormalizeString(beat.EmotionAfter),
                    SuppressedReaction = NormalizeString(beat.SuppressedReaction),
                    ExternalEvidence = NormalizeString(beat.ExternalEvidence),
                    NarrationStrategy = NormalizeString(beat.NarrationStrategy),
                    RhythmStrategy = NormalizeString(beat.RhythmStrategy),
                    ParagraphIntention = NormalizeString(beat.ParagraphIntention),
                    ExecutionMode = NormalizeString(beat.ExecutionMode),
                    AntiScreenplayDuty = NormalizeString(beat.AntiScreenplayDuty),
                    SensoryAnchorTarget = NormalizeString(beat.SensoryAnchorTarget),
                    SubtextPlan = NormalizeString(beat.SubtextPlan),
                    SourceBackedDetailTarget = NormalizeString(beat.SourceBackedDetailTarget),
                    CandidateRejectionRule = NormalizeString(beat.CandidateRejectionRule),
                    SceneFacts = NormalizeStringList(beat.SceneFacts),
                    ForbiddenFacts = NormalizeStringList(beat.ForbiddenFacts),
                    ReferenceQuery = NormalizeQuery(beat.ReferenceQuery),
                    RequiredMaterialTypes = NormalizeStringList(beat.RequiredMaterialTypes),
                    MaxRewriteLevel = NormalizeString(beat.MaxRewriteLevel),
                    SlotPlan = NormalizeSlotPlan(beat.SlotPlan),
                    LockedPhrasePolicy = NormalizeString(beat.LockedPhrasePolicy),
                    NoReuseReason = NormalizeString(beat.NoReuseReason),
                    ProseDuties = NormalizeStringList(beat.ProseDuties),
                    StyleContract = NormalizeStyleContract(beat.StyleContract)
                })
                .ToArray()
        };
        return JsonSerializer.Serialize(contract, JsonOptions);
    }

    private static ReferenceChapterBlueprintAnalysisTrackPayload NormalizeTrack(
        ReferenceChapterBlueprintAnalysisTrackPayload? track)
    {
        return track is null
            ? new ReferenceChapterBlueprintAnalysisTrackPayload(string.Empty, string.Empty, [])
            : new ReferenceChapterBlueprintAnalysisTrackPayload(
                NormalizeString(track.Track),
                NormalizeString(track.Summary),
                NormalizeStringList(track.Points));
    }

    private static ReferenceChapterBlueprintExecutionTrackPayload NormalizeExecutionTrack(
        ReferenceChapterBlueprintExecutionTrackPayload? track)
    {
        return track is null
            ? new ReferenceChapterBlueprintExecutionTrackPayload(string.Empty, string.Empty, [], [], [], [], [])
            : new ReferenceChapterBlueprintExecutionTrackPayload(
                NormalizeString(track.Track),
                NormalizeString(track.Summary),
                NormalizeStringList(track.ParagraphIntentions),
                NormalizeStringList(track.ExecutionModes),
                NormalizeStringList(track.AntiScreenplayDuties),
                NormalizeStringList(track.SourceBackedDetailTargets),
                NormalizeStringList(track.CandidateRejectionRules));
    }

    private static ReferenceMaterialQueryPayload NormalizeQuery(ReferenceMaterialQueryPayload? query)
    {
        return query is null
            ? new ReferenceMaterialQueryPayload(string.Empty, [], [], [], [], [], 0)
            : new ReferenceMaterialQueryPayload(
                NormalizeString(query.Query),
                NormalizeStringList(query.MaterialTypes),
                NormalizeStringList(query.EmotionTags),
                NormalizeStringList(query.FunctionTags),
                NormalizeStringList(query.PovTags),
                NormalizeStringList(query.TechniqueTags),
                query.MaxResults);
    }

    private static IReadOnlyList<ReferenceSlotValuePayload> NormalizeSlotPlan(
        IReadOnlyList<ReferenceSlotValuePayload>? slotPlan)
    {
        return slotPlan?
            .Where(slot => slot is not null)
            .Select(slot => new ReferenceSlotValuePayload(
                NormalizeString(slot.SlotName),
                NormalizeString(slot.Value)))
            .Where(slot => !string.IsNullOrWhiteSpace(slot.SlotName) || !string.IsNullOrWhiteSpace(slot.Value))
            .ToArray() ?? [];
    }

    private static ReferenceBlueprintStyleContractPayload? NormalizeStyleContract(
        ReferenceBlueprintStyleContractPayload? contract)
    {
        if (contract is null)
        {
            return null;
        }

        var profileIds = contract.StyleProfileIds?
            .Where(id => id > 0)
            .Distinct()
            .ToArray() ?? [];
        var dimensions = NormalizeStringList(contract.StyleDimensions);
        var evidenceTypes = NormalizeStringList(contract.RequiredEvidenceTypes);
        var forbiddenRisks = NormalizeStringList(contract.ForbiddenStyleRisks);
        var intensity = NormalizeString(contract.ImitationIntensity);
        var allowedCloseness = NormalizeString(contract.AllowedCloseness);
        var minStyleFit = double.IsNaN(contract.MinStyleFit) || double.IsInfinity(contract.MinStyleFit)
            ? 0
            : Math.Max(0, Math.Round(contract.MinStyleFit, 4));

        if (profileIds.Length == 0 &&
            dimensions.Count == 0 &&
            evidenceTypes.Count == 0 &&
            forbiddenRisks.Count == 0 &&
            intensity.Length == 0 &&
            allowedCloseness.Length == 0 &&
            minStyleFit <= 0)
        {
            return null;
        }

        return new ReferenceBlueprintStyleContractPayload(
            profileIds,
            dimensions,
            intensity,
            minStyleFit,
            allowedCloseness,
            evidenceTypes,
            forbiddenRisks);
    }

    private static IReadOnlyList<string> NormalizeStringList(IReadOnlyList<string>? values)
    {
        return values?
            .Select(NormalizeString)
            .Where(value => value.Length > 0)
            .ToArray() ?? [];
    }

    private static IReadOnlyList<string> NormalizeContextStringList(IReadOnlyList<string>? values)
    {
        return values?
            .Select(NormalizeString)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static IReadOnlyList<long> NormalizeAnchorIds(IReadOnlyList<long>? values)
    {
        return values?
            .Where(value => value > 0)
            .Distinct()
            .ToArray() ?? [];
    }

    private static string NormalizeString(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.ReplaceLineEndings("\n").Trim();
    }

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
