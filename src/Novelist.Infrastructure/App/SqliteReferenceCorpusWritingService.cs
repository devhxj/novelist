using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceCorpusWritingService : IReferenceCorpusWritingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppInitializationOptions _options;
    private readonly IReferenceCorpusService _corpus;
    private readonly IChapterContentService _chapters;
    private readonly IReferenceCorpusQueryContextParser _parser;
    private readonly IReferenceCorpusBlueprintAssembler _blueprints;
    private readonly IReferenceCorpusBlueprintCandidateAssembler _blueprintCandidates;
    private readonly IReferenceCorpusTextAssembler _textAssembler;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceCorpusWritingService(
        AppInitializationOptions? options = null,
        IReferenceCorpusService? corpus = null,
        IChapterContentService? chapters = null,
        IReferenceCorpusQueryContextParser? parser = null,
        IReferenceCorpusBlueprintAssembler? blueprints = null,
        IReferenceCorpusBlueprintCandidateAssembler? blueprintCandidates = null,
        IReferenceCorpusSlotResolver? slots = null,
        IReferenceCorpusTransitionResolver? transitionResolver = null,
        IReferenceCorpusTextAssembler? textAssembler = null)
    {
        _options = options ?? new AppInitializationOptions();
        _corpus = corpus ?? new SqliteReferenceCorpusService(_options);
        _chapters = chapters ?? new FileSystemChapterContentService(_options);
        _parser = parser ?? new DeterministicReferenceCorpusQueryContextParser();
        _blueprints = blueprints ?? new SingleBeatReferenceCorpusBlueprintAssembler();
        _blueprintCandidates = blueprintCandidates ?? new MultiStrategyReferenceCorpusBlueprintCandidateAssembler();
        _textAssembler = textAssembler ?? new PreservingReferenceCorpusTextAssembler(
            slots ?? new HeuristicReferenceCorpusSlotResolver(),
            transitionResolver ?? new HeuristicReferenceCorpusTransitionResolver());
    }

public async ValueTask<ReferenceCorpusBlueprintCandidatesPayload> GenerateBlueprintCandidatesAsync(
        GenerateReferenceCorpusBlueprintCandidatesPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateBlueprintCandidatesInput(input);

        var queryContext = await ParseQueryContextAsync(
            input.NaturalLanguageGoal,
            input.ChapterContext,
            input.Scope,
            cancellationToken);
        var requestedCount = NormalizeRequestedBlueprintCount(input.RequestedCount);
        var feedback = HasFeedback(input.Feedback) ? input.Feedback : null;
        var feedbackFilters = BuildCandidateSearchFilters(input.NaturalLanguageGoal, feedback);
        var diagnosticGapReasons = new List<string>();
        var candidates = await SearchCandidatesAsync(
            queryContext,
            requestedCount * 20,
            feedbackFilters,
            cancellationToken);
        if (feedback is not null && candidates.Items.Count == 0 && feedbackFilters is not null)
        {
            var baseFilters = BuildCandidateSearchFilters(input.NaturalLanguageGoal, feedback: null);
            if (!SameFilters(feedbackFilters, baseFilters))
            {
                diagnosticGapReasons.Add("feedback_filters_no_matches");
                candidates = await SearchCandidatesAsync(
                    queryContext,
                    requestedCount * 20,
                    baseFilters,
                    cancellationToken);
                diagnosticGapReasons.Add(candidates.Items.Count == 0
                    ? "fallback_base_no_candidates"
                    : "fallback_to_base_filters");
            }
        }

        var feedbackCandidateFilter = ApplyBlueprintFeedback(candidates.Items, feedback);
        diagnosticGapReasons.AddRange(feedbackCandidateFilter.GapReasons);
        var blueprintGapReasons = NormalizeDiagnosticGapReasons(diagnosticGapReasons);
        var historicalFeedback = feedback is null
            ? await ReadHistoricalFeedbackProfileAsync(input.ChapterContext.NovelId, cancellationToken)
            : ReferenceCorpusHistoricalFeedbackProfile.Empty;
 var assembledBlueprints = await _blueprintCandidates.AssembleCandidatesAsync(
            new ReferenceCorpusBlueprintCandidateAssemblyRequest(
                queryContext,
                feedbackCandidateFilter.Candidates,
                requestedCount,
                feedback,
                blueprintGapReasons,
                feedback is null ? "initial_candidate" : DescribeFeedback(feedback, blueprintGapReasons),
                historicalFeedback),
cancellationToken);
 var blueprints = ApplyBlueprintDifferenceAudit(assembledBlueprints);
        await PersistBlueprintFeedbackAsync(input, feedback, blueprintGapReasons, cancellationToken);
        await PersistBlueprintCandidatesAsync(queryContext, blueprints, cancellationToken);

return new ReferenceCorpusBlueprintCandidatesPayload(
queryContext,
blueprints,
feedback is not null,
DescribeFeedback(feedback, blueprintGapReasons),
 BuildBlueprintIteration(feedback, blueprints),
 [ReferenceOrchestrationStages.GoalParsing, ReferenceOrchestrationStages.CorpusRetrieval, ReferenceOrchestrationStages.BlueprintAssembly]);
}

 public async ValueTask<ReferenceCorpusBlueprintCandidatePayload> GenerateChapterBlueprintAsync(
 GenerateReferenceCorpusBlueprintCandidatesPayload input,
 CancellationToken cancellationToken)
 {
 var candidates = await GenerateBlueprintCandidatesAsync(
 input with { RequestedCount = Math.Max(1, input.RequestedCount) },
 cancellationToken);
 return candidates.Candidates.FirstOrDefault()
 ?? throw new InvalidOperationException("No reference corpus blueprint could be assembled for the current goal and scope.");
 }

    public async ValueTask<ReferenceCorpusInsertionDraftPayload> GenerateInsertionDraftAsync(
        GenerateReferenceCorpusInsertionDraftPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);

 ReferenceCorpusQueryContextPayload queryContext;
 ReferenceCorpusInsertionBlueprintPayload blueprint;
 if (input.SelectedBlueprint is null)
 {
 var blueprintCandidates = await GenerateBlueprintCandidatesAsync(
 new GenerateReferenceCorpusBlueprintCandidatesPayload(
 input.NaturalLanguageGoal,
 input.ChapterContext,
 input.Scope,
 RequestedCount: 3),
 cancellationToken);
 var selectedCandidate = blueprintCandidates.Candidates.FirstOrDefault(candidate =>
 candidate.DifferenceAudit?.Passed is not false)
 ?? throw new InvalidOperationException("No audited reference corpus blueprint could be selected for draft generation.");
 queryContext = blueprintCandidates.QueryContext;
 blueprint = selectedCandidate.Blueprint;
 }
 else
 {
 queryContext = await ParseQueryContextAsync(
 input.NaturalLanguageGoal,
 input.ChapterContext,
 input.Scope,
 cancellationToken);
 blueprint = input.SelectedBlueprint;
 }

var candidates = await SearchCandidatesAsync(
queryContext,
 80,
 filters: null,
cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var chapterBefore = await CurrentChapterTextAsync(input.ChapterContext, cancellationToken);

            return await BuildInsertionDraftAsync(
                connection,
                queryContext,
                blueprint,
                candidates.Items,
input.ChapterContext,
input.SlotValues,
chapterBefore,
 ReferenceCorpusTransitionStrategies.Default,
cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceCorpusInsertionDraftCandidatesPayload> GenerateInsertionDraftCandidatesAsync(
        GenerateReferenceCorpusInsertionDraftCandidatesPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateDraftCandidatesInput(input);

        var queryContext = await ParseQueryContextAsync(
            input.NaturalLanguageGoal,
            input.ChapterContext,
            input.Scope,
            cancellationToken);
        var requestedCount = NormalizeRequestedDraftCandidateCount(input.RequestedCount);
        var candidates = await SearchCandidatesAsync(queryContext, 120, filters: null, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var chapterBefore = await CurrentChapterTextAsync(input.ChapterContext, cancellationToken);
 var variants = await BuildDraftCandidateBlueprintVariantsAsync(
connection,
input.SelectedBlueprint,
requestedCount,
input.SlotValues,
input.SlotValueVariants,
 input.TransitionStrategyVariants,
candidates.Items,
                input.ChapterContext,
                cancellationToken);
            var draftCandidates = new List<ReferenceCorpusInsertionDraftCandidatePayload>(variants.Count);
            var emittedBlueprintKeys = new HashSet<string>(StringComparer.Ordinal);
            var variantsByCandidateId = new Dictionary<string, DraftCandidateBlueprintVariant>(StringComparer.Ordinal);

            foreach (var variant in variants)
            {
                var draft = await BuildInsertionDraftAsync(
                    connection,
                    queryContext,
                    variant.Blueprint,
                    candidates.Items,
                    input.ChapterContext,
                    variant.SlotValues,
 chapterBefore,
 variant.TransitionStrategy,
cancellationToken);
                var nextAction = BuildDraftCandidateNextAction(input.SelectedBlueprint, draft);

if (AddDraftCandidateIfNew(draftCandidates, emittedBlueprintKeys, variant, draft, nextAction))
                {
                    variantsByCandidateId[variant.CandidateId] = variant;
                }
            }

            if (draftCandidates.Count == 0)
            {
                foreach (var variant in variants)
                {
                    var draft = await BuildInsertionDraftAsync(
                        connection,
                        queryContext,
                        variant.Blueprint,
                        candidates.Items,
                        input.ChapterContext,
                        variant.SlotValues,
 chapterBefore,
 variant.TransitionStrategy,
cancellationToken);
                    draftCandidates.Add(new ReferenceCorpusInsertionDraftCandidatePayload(
                        CandidateId: variant.CandidateId,
                        Strategy: variant.Strategy,
                        Explanation: variant.Explanation,
                        Draft: draft,
                        NextAction: BuildDraftCandidateNextAction(input.SelectedBlueprint, draft)));
                    variantsByCandidateId[variant.CandidateId] = variant;
                    break;
                }
            }

ApplyDraftCandidateSetAudit(
draftCandidates,
variantsByCandidateId,
 input.SelectedBlueprint,
chapterBefore);

 return new ReferenceCorpusInsertionDraftCandidatesPayload(
queryContext,
input.SelectedBlueprint,
 draftCandidates,
 BuildDraftCandidateSetAudit(input.SelectedBlueprint, draftCandidates));
        }
        finally
        {
            _mutex.Release();
        }
    }

    private ValueTask<ReferenceCorpusQueryContextPayload> ParseQueryContextAsync(
        string naturalLanguageGoal,
        CurrentChapterContextPayload chapterContext,
        ReferenceCorpusScopePayload scope,
        CancellationToken cancellationToken)
    {
        return _parser.ParseAsync(
            new ReferenceCorpusQueryParsingRequest(
                naturalLanguageGoal,
                chapterContext,
                scope),
            cancellationToken);
    }

    private ValueTask<PageResultPayload<ReferenceCorpusCandidatePayload>> SearchCandidatesAsync(
        ReferenceCorpusQueryContextPayload queryContext,
        int pageSize,
        IReadOnlyDictionary<string, string>? filters,
        CancellationToken cancellationToken)
    {
        var pageFilters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["node_type"] = ReferenceCorpusNodeTypes.Sentence
        };
        if (filters is not null)
        {
            foreach (var filter in filters)
            {
                if (!string.IsNullOrWhiteSpace(filter.Key) &&
                    !string.IsNullOrWhiteSpace(filter.Value))
                {
                    pageFilters[filter.Key.Trim()] = filter.Value.Trim();
                }
            }
        }

        return _corpus.SearchCandidatesAsync(
            new SearchReferenceCorpusCandidatesPayload(
                queryContext,
                new PageRequestPayload(
                    Cursor: null,
                    PageSize: Math.Clamp(pageSize, 1, 200),
                    SortBy: "score",
                    SortDir: "desc",
                    Filters: pageFilters)),
            cancellationToken);
    }

private async ValueTask<ReferenceCorpusInsertionDraftPayload> BuildInsertionDraftAsync(
        SqliteConnection connection,
        ReferenceCorpusQueryContextPayload queryContext,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        CurrentChapterContextPayload chapterContext,
IReadOnlyDictionary<string, string> slotValues,
string chapterBefore,
 string transitionStrategy,
CancellationToken cancellationToken)
    {
        if (blueprint.Beats.Count == 0)
        {
            return EmptyResult(queryContext, blueprint, chapterBefore, "no_candidates");
        }

        var sourcePieces = await ReadSourcePiecesAsync(connection, blueprint, candidates, cancellationToken);
        if (sourcePieces.Count != RequestedSourceNodeReferenceCount(blueprint))
        {
            return EmptyResult(queryContext, blueprint, chapterBefore, "source_node_missing");
        }

        await UpsertBeatPiecesAsync(connection, blueprint, sourcePieces, cancellationToken);
        var textResult = await _textAssembler.AssembleAsync(
            new ReferenceCorpusTextAssemblyRequest(
                blueprint,
sourcePieces.Select(piece => piece.ToSourcePiece()).ToArray(),
chapterContext,
 slotValues,
 transitionStrategy),
            cancellationToken);
        var gate = EvaluateGate(sourcePieces, textResult.Pieces);
        var audit = EvaluateDraftAudit(sourcePieces, textResult.Pieces, textResult.Transitions, textResult.AssembledText);
        var ready = gate.Passed && audit.Passed;
        var chapterAfter = ready
            ? InsertAtOffset(chapterBefore, chapterContext.InsertionOffset, textResult.AssembledText)
            : chapterBefore;

        return new ReferenceCorpusInsertionDraftPayload(
            queryContext,
            blueprint,
            textResult.Pieces,
            textResult.SlotReplacements,
            textResult.Transitions,
            textResult.AssembledText,
            chapterAfter,
            ready,
            gate,
            audit);
    }

    private static void ValidateInput(GenerateReferenceCorpusInsertionDraftPayload input)
    {
        if (string.IsNullOrWhiteSpace(input.NaturalLanguageGoal))
        {
            throw new ArgumentException("Natural language goal is required.", nameof(input));
        }

        ArgumentNullException.ThrowIfNull(input.ChapterContext);
        if (input.ChapterContext.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ChapterContext.NovelId, "Novel id must be positive.");
        }

        if (input.ChapterContext.ChapterNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ChapterContext.ChapterNumber, "Chapter number must be positive.");
        }

        ArgumentNullException.ThrowIfNull(input.Scope);
    }

    private static void ValidateDraftCandidatesInput(GenerateReferenceCorpusInsertionDraftCandidatesPayload input)
    {
        if (string.IsNullOrWhiteSpace(input.NaturalLanguageGoal))
        {
            throw new ArgumentException("Natural language goal is required.", nameof(input));
        }

        ArgumentNullException.ThrowIfNull(input.ChapterContext);
        if (input.ChapterContext.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ChapterContext.NovelId, "Novel id must be positive.");
        }

        if (input.ChapterContext.ChapterNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ChapterContext.ChapterNumber, "Chapter number must be positive.");
        }

        ArgumentNullException.ThrowIfNull(input.Scope);
        ArgumentNullException.ThrowIfNull(input.SelectedBlueprint);
    }

    private static void ValidateBlueprintCandidatesInput(GenerateReferenceCorpusBlueprintCandidatesPayload input)
    {
        if (string.IsNullOrWhiteSpace(input.NaturalLanguageGoal))
        {
            throw new ArgumentException("Natural language goal is required.", nameof(input));
        }

        ArgumentNullException.ThrowIfNull(input.ChapterContext);
        if (input.ChapterContext.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ChapterContext.NovelId, "Novel id must be positive.");
        }

        if (input.ChapterContext.ChapterNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ChapterContext.ChapterNumber, "Chapter number must be positive.");
        }

        ArgumentNullException.ThrowIfNull(input.Scope);
    }

    private static int NormalizeRequestedBlueprintCount(int requestedCount)
    {
        return Math.Clamp(requestedCount <= 0 ? 3 : requestedCount, 1, 6);
    }

    private static int NormalizeRequestedDraftCandidateCount(int requestedCount)
    {
        return Math.Clamp(requestedCount <= 0 ? 3 : requestedCount, 1, 6);
    }

    private static bool AddDraftCandidateIfNew(
        List<ReferenceCorpusInsertionDraftCandidatePayload> draftCandidates,
        HashSet<string> emittedBlueprintKeys,
        DraftCandidateBlueprintVariant variant,
        ReferenceCorpusInsertionDraftPayload draft,
        ReferenceCorpusDraftCandidateNextActionPayload? nextAction)
    {
 var key = BlueprintNodeKey(variant.Blueprint) + "\u001f" + variant.SlotValueKey + "\u001f" + variant.TransitionStrategy;
        if (!emittedBlueprintKeys.Add(key))
        {
            return false;
        }

        draftCandidates.Add(new ReferenceCorpusInsertionDraftCandidatePayload(
            CandidateId: variant.CandidateId,
            Strategy: variant.Strategy,
            Explanation: variant.Explanation,
            Draft: draft,
            NextAction: nextAction));
        return true;
    }

private static void ApplyDraftCandidateSetAudit(
List<ReferenceCorpusInsertionDraftCandidatePayload> draftCandidates,
IReadOnlyDictionary<string, DraftCandidateBlueprintVariant> variantsByCandidateId,
 ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
string chapterBefore)
{
 var selectedNodeSet = BuildBlueprintNodeSet(BuildPrimarySourceBlueprint(selectedBlueprint));
 for (var index = 0; index < draftCandidates.Count; index++)
{
 var candidate = draftCandidates[index];
 if (!selectedNodeSet.SetEquals(BuildDraftNodeSet(candidate.Draft)))
{
 draftCandidates[index] = BlockDraftCandidateForCandidateSetViolation(
 candidate,
 candidate.Draft.Pieces.FirstOrDefault(),
 "draft_candidate_set_source_boundary_changed",
 $"Draft candidate {candidate.CandidateId} changed the selected blueprint source boundary.",
 chapterBefore);
continue;
}

 if (!candidate.Draft.Audit.Passed ||
 !variantsByCandidateId.TryGetValue(candidate.CandidateId, out var variant))
{
 continue;
}

 if (FindUndeclaredCandidateSlotReplacement(candidate.Draft, variant.SlotValues) is { } violation)
{
draftCandidates[index] = BlockDraftCandidateForCandidateSetViolation(
candidate,
 violation.Piece,
 "draft_candidate_set_non_slot_difference",
 $"Draft candidate {candidate.CandidateId} changed source value '{violation.Replacement.SourceValue}' outside the selected slot mapping.",
chapterBefore);
}
 }

 var seenAssembledText = new Dictionary<string, string>(StringComparer.Ordinal);
 for (var index = 0; index < draftCandidates.Count; index++)
 {
 var candidate = draftCandidates[index];
 if (!candidate.Draft.Audit.Passed)
 {
 continue;
 }

 var textKey = NormalizeDraftCandidateTextKey(candidate.Draft.AssembledText);
 if (textKey.Length == 0 || seenAssembledText.TryAdd(textKey, candidate.CandidateId))
 {
 continue;
 }

 draftCandidates[index] = BlockDraftCandidateForCandidateSetViolation(
 candidate,
 candidate.Draft.Pieces.FirstOrDefault(),
 "draft_candidate_set_duplicate_text",
 $"Draft candidate {candidate.CandidateId} duplicates assembled text already emitted by candidate {seenAssembledText[textKey]}.",
 chapterBefore);
 }
 }

private static ReferenceCorpusDraftCandidateSetAuditPayload BuildDraftCandidateSetAudit(
 ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
IReadOnlyList<ReferenceCorpusInsertionDraftCandidatePayload> candidates)
 {
 if (candidates.Count == 0)
 {
 return new ReferenceCorpusDraftCandidateSetAuditPayload(
 Passed: false,
 CandidateCount: 0,
 ReadyCandidateCount: 0,
 DistinctTextCount: 0,
 Differences: [],
 Errors: ["draft_candidate_set_empty"]);
 }

 var baseline = candidates.FirstOrDefault(candidate => candidate.Draft.ReadyForInsertion) ?? candidates[0];
 var selectedNodeSet = BuildBlueprintNodeSet(BuildPrimarySourceBlueprint(selectedBlueprint));
var baselinePieceKeys = BuildDraftPieceKeys(baseline.Draft);
 var baselinePieceInvariantKeys = BuildDraftPieceInvariantKeys(baseline.Draft);
 var baselineSlotKeys = BuildDraftSlotReplacementKeys(baseline.Draft);
 var baselineTransitionKeys = BuildDraftTransitionKeys(baseline.Draft);
 var baselineText = NormalizeDraftCandidateTextKey(baseline.Draft.AssembledText);
 var differences = new List<ReferenceCorpusDraftCandidateDifferencePayload>(candidates.Count);
 var errors = new HashSet<string>(StringComparer.Ordinal);

 foreach (var candidate in candidates)
 {
 var nodeSet = BuildDraftNodeSet(candidate.Draft);
var pieceKeys = BuildDraftPieceKeys(candidate.Draft);
 var pieceInvariantKeys = BuildDraftPieceInvariantKeys(candidate.Draft);
 var slotKeys = BuildDraftSlotReplacementKeys(candidate.Draft);
 var transitionKeys = BuildDraftTransitionKeys(candidate.Draft);
 var sameBlueprintNodeSet = selectedNodeSet.SetEquals(nodeSet);
var samePieceOutputs = baselinePieceKeys.SequenceEqual(pieceKeys, StringComparer.Ordinal);
 var slotDifferenceCount = CountSymmetricDifference(baselineSlotKeys, slotKeys);
 var transitionDifferenceCount = CountSymmetricDifference(baselineTransitionKeys, transitionKeys);
 var duplicateText = !string.Equals(candidate.CandidateId, baseline.CandidateId, StringComparison.Ordinal) &&
 string.Equals(baselineText, NormalizeDraftCandidateTextKey(candidate.Draft.AssembledText), StringComparison.Ordinal);
 var pieceDifferenceExplainedBySlots = baselinePieceInvariantKeys.SequenceEqual(pieceInvariantKeys, StringComparer.Ordinal);
 var onlyAllowedDifferences = sameBlueprintNodeSet && pieceDifferenceExplainedBySlots &&
 candidate.Draft.Audit.Passed && !duplicateText;
 var diagnostics = new List<string>();

 if (!sameBlueprintNodeSet)
 {
 diagnostics.Add("draft_candidate_set_source_boundary_changed");
 errors.Add("draft_candidate_set_source_boundary_changed");
 }

 if (!pieceDifferenceExplainedBySlots)
 {
 diagnostics.Add("draft_candidate_set_untracked_piece_difference");
 errors.Add("draft_candidate_set_untracked_piece_difference");
 }

 if (duplicateText)
 {
 diagnostics.Add("draft_candidate_set_duplicate_text");
 errors.Add("draft_candidate_set_duplicate_text");
 }

 if (!candidate.Draft.Audit.Passed)
 {
 diagnostics.Add("draft_candidate_audit_failed");
 errors.Add("draft_candidate_audit_failed");
 }

 differences.Add(new ReferenceCorpusDraftCandidateDifferencePayload(
 CandidateId: candidate.CandidateId,
 BaselineCandidateId: baseline.CandidateId,
 SameBlueprintNodeSet: sameBlueprintNodeSet,
 SamePieceOutputs: samePieceOutputs,
 SlotDifferenceCount: slotDifferenceCount,
 TransitionDifferenceCount: transitionDifferenceCount,
 OnlyAllowedDifferences: onlyAllowedDifferences,
 DuplicateText: duplicateText,
 Diagnostics: diagnostics));
 }

 var distinctTextCount = candidates
 .Select(candidate => NormalizeDraftCandidateTextKey(candidate.Draft.AssembledText))
 .Where(value => value.Length > 0)
 .Distinct(StringComparer.Ordinal)
 .Count();
 return new ReferenceCorpusDraftCandidateSetAuditPayload(
 Passed: errors.Count == 0 && differences.All(difference => difference.OnlyAllowedDifferences),
 CandidateCount: candidates.Count,
 ReadyCandidateCount: candidates.Count(candidate => candidate.Draft.ReadyForInsertion),
 DistinctTextCount: distinctTextCount,
 Differences: differences,
 Errors: errors.OrderBy(value => value, StringComparer.Ordinal).ToArray());
 }

private static HashSet<string> BuildDraftNodeSet(ReferenceCorpusInsertionDraftPayload draft)
{
return draft.Blueprint.Beats
.SelectMany(beat => beat.NodeIds)
.ToHashSet(StringComparer.Ordinal);
}

 private static HashSet<string> BuildBlueprintNodeSet(ReferenceCorpusInsertionBlueprintPayload blueprint)
 {
 return blueprint.Beats
 .SelectMany(beat => beat.NodeIds)
 .ToHashSet(StringComparer.Ordinal);
 }

 private static IReadOnlyList<string> BuildDraftPieceInvariantKeys(ReferenceCorpusInsertionDraftPayload draft)
 {
 return draft.Pieces
 .Select(piece => StableHash(
 piece.PieceId,
 piece.BeatId,
 piece.NodeId,
 piece.SourceTextHash,
 BuildSlotMaskedPieceOutput(piece)))
 .ToArray();
 }

 private static string BuildSlotMaskedPieceOutput(ReferenceCorpusInsertionPiecePayload piece)
 {
 var builder = new StringBuilder(piece.OutputText.Length);
 var cursor = 0;
 foreach (var replacement in piece.SlotReplacements
 .OrderBy(item => item.OutputStart)
 .ThenBy(item => item.OutputEnd))
 {
 if (replacement.OutputStart < cursor ||
 replacement.OutputEnd < replacement.OutputStart ||
 replacement.OutputEnd > piece.OutputText.Length)
 {
 return "invalid-slot-range";
 }

 builder.Append(piece.OutputText, cursor, replacement.OutputStart - cursor);
 builder.Append("<slot:")
 .Append(NormalizeTransferSlotName(replacement.SlotName) ?? string.Empty)
 .Append(':')
 .Append(replacement.SourceValue)
 .Append(':')
 .Append(replacement.SourceStart.ToString(CultureInfo.InvariantCulture))
 .Append(':')
 .Append(replacement.SourceEnd.ToString(CultureInfo.InvariantCulture))
 .Append('>');
 cursor = replacement.OutputEnd;
 }

 builder.Append(piece.OutputText, cursor, piece.OutputText.Length - cursor);
 return builder.ToString();
 }

 private static IReadOnlyList<string> BuildDraftPieceKeys(ReferenceCorpusInsertionDraftPayload draft)
 {
 return draft.Pieces
 .Select(piece => StableHash(
 piece.PieceId,
 piece.BeatId,
 piece.NodeId,
 piece.SourceTextHash,
 piece.OutputText,
 string.Join('|', piece.PreservedSpans.Select(span => string.Join(':', span.SpanId, span.SourceTextHash, span.OutputTextHash, span.Matches))),
 string.Join('|', piece.LockedSpans.Select(span => string.Join(':', span.SpanId, span.SourceTextHash, span.OutputTextHash, span.Matches)))))
 .ToArray();
 }

 private static HashSet<string> BuildDraftSlotReplacementKeys(ReferenceCorpusInsertionDraftPayload draft)
 {
 return draft.Pieces
 .SelectMany(piece => piece.SlotReplacements.Select(replacement => StableHash(
 piece.PieceId,
 replacement.SlotName,
 replacement.SourceValue,
 replacement.ReplacementValue,
 replacement.SourceStart.ToString(CultureInfo.InvariantCulture),
 replacement.SourceEnd.ToString(CultureInfo.InvariantCulture),
 replacement.OutputStart.ToString(CultureInfo.InvariantCulture),
 replacement.OutputEnd.ToString(CultureInfo.InvariantCulture))))
 .ToHashSet(StringComparer.Ordinal);
 }

 private static HashSet<string> BuildDraftTransitionKeys(ReferenceCorpusInsertionDraftPayload draft)
 {
 return draft.Transitions
 .Select(transition => StableHash(
 transition.GapId,
 transition.AfterPieceId,
 transition.BeforePieceId,
 transition.Decision,
 transition.Strategy,
 transition.Text,
 transition.ReplacementPieceId ?? string.Empty,
 transition.ReplacementNodeId ?? string.Empty))
 .ToHashSet(StringComparer.Ordinal);
 }

 private static int CountSymmetricDifference(IReadOnlySet<string> left, IReadOnlySet<string> right)
 {
 return left.Except(right, StringComparer.Ordinal).Count() + right.Except(left, StringComparer.Ordinal).Count();
 }

private static string NormalizeDraftCandidateTextKey(string value)
    {
        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static (ReferenceCorpusInsertionPiecePayload Piece, ReferenceCorpusSlotReplacementPayload Replacement)?
        FindUndeclaredCandidateSlotReplacement(
            ReferenceCorpusInsertionDraftPayload draft,
            IReadOnlyDictionary<string, string> slotValues)
    {
        foreach (var piece in draft.Pieces)
        {
            foreach (var replacement in piece.SlotReplacements)
            {
                if (!IsDeclaredCandidateSlotReplacement(replacement, slotValues))
                {
                    return (piece, replacement);
                }
            }
        }

        return null;
    }

    private static bool IsDeclaredCandidateSlotReplacement(
        ReferenceCorpusSlotReplacementPayload replacement,
        IReadOnlyDictionary<string, string> slotValues)
    {
        if (slotValues.Count == 0)
        {
            return false;
        }

        var replacementSlotName = NormalizeTransferSlotName(replacement.SlotName);
        foreach (var pair in NormalizeSlotValues(slotValues))
        {
            var parsed = ParseCandidateSlotValueKey(pair.Key);
            if (parsed.SourceValue.Length == 0 ||
                !string.Equals(parsed.SourceValue, replacement.SourceValue.Trim(), StringComparison.Ordinal) ||
                !string.Equals(pair.Value, replacement.ReplacementValue.Trim(), StringComparison.Ordinal))
            {
                continue;
            }

            if (parsed.SlotName is null)
            {
                return true;
            }

            return replacementSlotName is not null &&
                string.Equals(parsed.SlotName, replacementSlotName, StringComparison.Ordinal);
        }

        return false;
    }

    private static (string? SlotName, string SourceValue) ParseCandidateSlotValueKey(string key)
    {
        var normalized = key.Trim();
        if (normalized.Length == 0)
        {
            return (null, string.Empty);
        }

        var separatorIndex = normalized.IndexOfAny([':', '：']);
        if (separatorIndex <= 0 || separatorIndex + 1 >= normalized.Length)
        {
            return (null, normalized);
        }

        return (
            NormalizeTransferSlotName(normalized[..separatorIndex]),
            normalized[(separatorIndex + 1)..].Trim());
    }

    private static ReferenceCorpusInsertionDraftCandidatePayload BlockDraftCandidateForCandidateSetViolation(
        ReferenceCorpusInsertionDraftCandidatePayload candidate,
 ReferenceCorpusInsertionPiecePayload? piece,
        string code,
        string message,
        string chapterBefore)
    {
 var auditPieces = candidate.Draft.Audit.Pieces.ToList();
 if (piece is not null)
{
 var pieceIndex = auditPieces.FindIndex(item => string.Equals(item.PieceId, piece.PieceId, StringComparison.Ordinal));
 if (pieceIndex < 0)
{
 var violations = new List<ReferenceCorpusDraftAuditViolationPayload>();
 AddAuditViolation(violations, piece, spanId: null, code, message);
 auditPieces.Add(new ReferenceCorpusDraftAuditPiecePayload(
 PieceId: piece.PieceId,
 NodeId: piece.NodeId,
 Passed: false,
 PreservedSpanCount: piece.PreservedSpans.Count,
 MismatchedSpanCount: piece.PreservedSpans.Count(span => !span.Matches),
 Violations: violations));
 }
 else
 {
 var auditPiece = auditPieces[pieceIndex];
 var violations = auditPiece.Violations.ToList();
 AddAuditViolation(violations, piece, spanId: null, code, message);
 auditPieces[pieceIndex] = auditPiece with
 {
 Passed = false,
 Violations = violations
 };
 }
}

var errors = candidate.Draft.Audit.Errors.ToList();
 var error = piece is null ? code : $"{code}:{piece.NodeId}";
        if (!errors.Contains(error, StringComparer.Ordinal))
        {
            errors.Add(error);
        }

        var audit = candidate.Draft.Audit with
        {
            Passed = false,
            Status = "blocked",
            Errors = errors,
            Pieces = auditPieces
        };
        var draft = candidate.Draft with
        {
            ReadyForInsertion = false,
            ChapterTextAfterInsertion = chapterBefore,
            Audit = audit
        };
        return candidate with
        {
            Draft = draft
        };
    }

    private static async ValueTask<IReadOnlyList<DraftCandidateBlueprintVariant>> BuildDraftCandidateBlueprintVariantsAsync(
        SqliteConnection connection,
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
int requestedCount,
IReadOnlyDictionary<string, string> baseSlotValues,
IReadOnlyList<ReferenceCorpusDraftSlotValueVariantPayload>? slotValueVariants,
 IReadOnlyList<string>? transitionStrategyVariants,
IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        CurrentChapterContextPayload chapterContext,
        CancellationToken cancellationToken)
    {
var slotVariants = BuildSlotValueDraftVariants(selectedBlueprint, requestedCount, baseSlotValues, slotValueVariants);
if (slotVariants.Count > 0)
{
 return ExpandTransitionStrategyVariants(
 selectedBlueprint,
 requestedCount,
 slotVariants,
 transitionStrategyVariants);
}

var transitionVariants = BuildTransitionStrategyDraftVariants(
 selectedBlueprint,
 requestedCount,
 baseSlotValues,
 transitionStrategyVariants);
 if (transitionVariants.Count > 0)
 {
 return transitionVariants;
 }

        var primaryBlueprint = BuildPrimarySourceBlueprint(selectedBlueprint);
        var primarySourcePieces = await ReadSourcePiecesAsync(connection, primaryBlueprint, candidates, cancellationToken);
        if (primarySourcePieces.Count == RequestedSourceNodeReferenceCount(primaryBlueprint))
        {
            var autoTransferSlotVariants = BuildAutoTransferSlotDraftVariants(
                selectedBlueprint,
                requestedCount,
                baseSlotValues,
                primaryBlueprint,
                primarySourcePieces,
                chapterContext);
            if (autoTransferSlotVariants.Count > 0)
            {
                return autoTransferSlotVariants;
            }
        }

 return BuildSelectedBlueprintDraftVariant(selectedBlueprint, baseSlotValues);
}

 private static IReadOnlyList<DraftCandidateBlueprintVariant> BuildSelectedBlueprintDraftVariant(
ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
IReadOnlyDictionary<string, string> baseSlotValues)
{
var normalizedSlotValues = NormalizeSlotValues(baseSlotValues);
var slotValueKey = SlotValueKey(normalizedSlotValues);
 var primaryBlueprint = BuildPrimarySourceBlueprint(selectedBlueprint);
 if (primaryBlueprint.Beats.SelectMany(beat => beat.NodeIds).Any())
{
 var fingerprint = StableHash(selectedBlueprint.BlueprintId, BlueprintNodeKey(primaryBlueprint), slotValueKey)[..16];
 return
 [
 new DraftCandidateBlueprintVariant(
 CandidateId: "corpus-draft-candidate-selected-" + fingerprint,
 Strategy: "selected_blueprint_primary",
 Explanation: "Uses the accepted selected blueprint primary source nodes; additional drafts require slot or transition variants.",
 Blueprint: primaryBlueprint with
 {
 BlueprintId = $"{selectedBlueprint.BlueprintId}:selected-{fingerprint}",
 Strategy = $"{selectedBlueprint.Strategy}:selected_blueprint_primary"
 },
 SlotValues: normalizedSlotValues,
 SlotValueKey: slotValueKey)
 ];
}

 var emptyFingerprint = StableHash(selectedBlueprint.BlueprintId, "empty")[..16];
 return
 [
 new DraftCandidateBlueprintVariant(
 CandidateId: "corpus-draft-candidate-" + emptyFingerprint,
Strategy: "selected_blueprint_empty",
Explanation: "The selected blueprint has no source nodes; returns a blocked draft for UI diagnostics.",
Blueprint: selectedBlueprint,
SlotValues: normalizedSlotValues,
 SlotValueKey: slotValueKey)
 ];
}

    private static IReadOnlyList<DraftCandidateBlueprintVariant> BuildAutoTransferSlotDraftVariants(
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
        int requestedCount,
        IReadOnlyDictionary<string, string> baseSlotValues,
        ReferenceCorpusInsertionBlueprintPayload primaryBlueprint,
        IReadOnlyList<LicensedSourcePiece> primarySourcePieces,
        CurrentChapterContextPayload chapterContext)
    {
        if (requestedCount <= 0 ||
            primarySourcePieces.Count == 0 ||
            primarySourcePieces.All(piece => piece.AllowedTransferSlotNames.Count == 0))
        {
            return [];
        }

        var baseValues = NormalizeSlotValues(baseSlotValues);
        var variants = new List<DraftCandidateBlueprintVariant>(requestedCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        AddAutoCharacterSlotVariants(
            selectedBlueprint,
            requestedCount,
            primaryBlueprint,
            primarySourcePieces,
            chapterContext,
            baseValues,
            variants,
            seen);

        if (variants.Count == 0 &&
            ContainsDeclaredTransferSlotValue(baseValues, primarySourcePieces))
        {
            AddAutoTransferSlotVariant(
                selectedBlueprint,
                requestedCount,
                primaryBlueprint,
                baseValues,
                "request_slot_values",
                "request slot values",
                variants,
                seen);
        }

        return variants;
    }

    private static void AddAutoCharacterSlotVariants(
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
        int requestedCount,
        ReferenceCorpusInsertionBlueprintPayload primaryBlueprint,
        IReadOnlyList<LicensedSourcePiece> primarySourcePieces,
        CurrentChapterContextPayload chapterContext,
        IReadOnlyDictionary<string, string> baseValues,
        List<DraftCandidateBlueprintVariant> variants,
        HashSet<string> seen)
    {
        var sourceValues = primarySourcePieces
            .Where(piece => piece.AllowedTransferSlotNames.Contains("character"))
            .SelectMany(piece => DetectCharacterTransferSourceValues(piece.SourceText))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sourceValues.Length == 0)
        {
            return;
        }

        foreach (var character in chapterContext.CharacterSnapshots
            .Select(snapshot => snapshot.Character.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal))
        {
            if (variants.Count >= requestedCount)
            {
                break;
            }

            var values = new Dictionary<string, string>(baseValues, StringComparer.Ordinal);
            foreach (var sourceValue in sourceValues)
            {
                if (!string.Equals(sourceValue, character, StringComparison.Ordinal))
                {
                    values["character:" + sourceValue] = character;
                }
            }

            if (values.Count == baseValues.Count)
            {
                continue;
            }

            AddAutoTransferSlotVariant(
                selectedBlueprint,
                requestedCount,
                primaryBlueprint,
                values,
                "character:" + character,
                "character " + character,
                variants,
                seen);
        }
    }

    private static void AddAutoTransferSlotVariant(
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
        int requestedCount,
        ReferenceCorpusInsertionBlueprintPayload primaryBlueprint,
        IReadOnlyDictionary<string, string> values,
        string variantSeed,
        string label,
        List<DraftCandidateBlueprintVariant> variants,
        HashSet<string> seen)
    {
        if (variants.Count >= requestedCount)
        {
            return;
        }

        var slotValueKey = SlotValueKey(values);
        var dedupeKey = BlueprintNodeKey(primaryBlueprint) + "\u001f" + slotValueKey;
        if (!seen.Add(dedupeKey))
        {
            return;
        }

        var suffix = (variants.Count + 1).ToString(CultureInfo.InvariantCulture);
        var fingerprint = StableHash(selectedBlueprint.BlueprintId, "auto_transfer_slot", variantSeed, slotValueKey)[..16];
        var blueprint = primaryBlueprint with
        {
            BlueprintId = $"{selectedBlueprint.BlueprintId}:auto-transfer-slot-{fingerprint}",
            Strategy = $"{selectedBlueprint.Strategy}:auto_transfer_slot_{suffix}"
        };
        variants.Add(new DraftCandidateBlueprintVariant(
            CandidateId: "corpus-draft-candidate-auto-slot-" + fingerprint,
            Strategy: "auto_transfer_slot_" + suffix,
            Explanation: "Uses active technique transfer_slots to derive a same-source slot mapping from current chapter context: " + label,
            Blueprint: blueprint,
            SlotValues: values,
            SlotValueKey: slotValueKey));
    }

    private static IReadOnlyList<string> DetectCharacterTransferSourceValues(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return [];
        }

        var result = new List<string>(2);
        foreach (var pronoun in new[] { "她", "他" })
        {
            var index = sourceText.IndexOf(pronoun, StringComparison.Ordinal);
            if (index is >= 0 and <= 2)
            {
                result.Add(pronoun);
            }
        }

        return result;
    }

    private static bool ContainsDeclaredTransferSlotValue(
        IReadOnlyDictionary<string, string> slotValues,
        IReadOnlyList<LicensedSourcePiece> primarySourcePieces)
    {
        if (slotValues.Count == 0)
        {
            return false;
        }

        var allowedSlots = primarySourcePieces
            .SelectMany(piece => piece.AllowedTransferSlotNames)
            .ToHashSet(StringComparer.Ordinal);
        if (allowedSlots.Count == 0)
        {
            return false;
        }

        return slotValues.Keys.Any(key =>
            TryReadExplicitTransferSlotName(key) is { } slotName &&
            allowedSlots.Contains(slotName));
    }

    private static string? TryReadExplicitTransferSlotName(string? key)
    {
        var value = key?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return null;
        }

        var separatorIndex = value.IndexOfAny([':', '：']);
        if (separatorIndex <= 0)
        {
            return null;
        }

        return NormalizeTransferSlotName(value[..separatorIndex]);
    }

    private static IReadOnlyList<DraftCandidateBlueprintVariant> BuildSlotValueDraftVariants(
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
        int requestedCount,
        IReadOnlyDictionary<string, string> baseSlotValues,
        IReadOnlyList<ReferenceCorpusDraftSlotValueVariantPayload>? slotValueVariants)
    {
        if (slotValueVariants is null || slotValueVariants.Count == 0)
        {
            return [];
        }

        var variants = new List<DraftCandidateBlueprintVariant>(Math.Min(requestedCount, slotValueVariants.Count));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var primaryBlueprint = BuildPrimarySourceBlueprint(selectedBlueprint);
        var baseValues = NormalizeSlotValues(baseSlotValues);
        var index = 0;
        foreach (var slotVariant in slotValueVariants)
        {
            if (variants.Count >= requestedCount)
            {
                break;
            }

            var values = MergeSlotValues(baseValues, slotVariant.SlotValues);
            var slotValueKey = SlotValueKey(values);
            var dedupeKey = BlueprintNodeKey(primaryBlueprint) + "\u001f" + slotValueKey;
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            index++;
            var suffix = index.ToString(CultureInfo.InvariantCulture);
            var variantId = string.IsNullOrWhiteSpace(slotVariant.VariantId)
                ? "slot-variant-" + suffix
                : slotVariant.VariantId.Trim();
            var fingerprint = StableHash(selectedBlueprint.BlueprintId, variantId, slotValueKey)[..16];
            var label = string.IsNullOrWhiteSpace(slotVariant.Label)
                ? "slot variant " + suffix
                : slotVariant.Label.Trim();
            var blueprint = primaryBlueprint with
            {
                BlueprintId = $"{selectedBlueprint.BlueprintId}:slot-{fingerprint}",
                Strategy = $"{selectedBlueprint.Strategy}:slot_variant_{suffix}"
            };
            variants.Add(new DraftCandidateBlueprintVariant(
                CandidateId: "corpus-draft-candidate-slot-" + fingerprint,
                Strategy: "slot_variant_" + suffix,
                Explanation: "Uses the same selected source nodes with slot mapping variant: " + label,
                Blueprint: blueprint,
                SlotValues: values,
                SlotValueKey: slotValueKey));
        }

        return variants;
    }

    private static ReferenceCorpusInsertionBlueprintPayload BuildPrimarySourceBlueprint(
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint)
    {
        return selectedBlueprint with
        {
            Beats = selectedBlueprint.Beats
                .Select(beat => beat with
                {
                    NodeIds = beat.NodeIds
                        .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
                        .Take(1)
                        .ToArray()
                })
                .ToArray()
        };
    }

    private static IReadOnlyDictionary<string, string> MergeSlotValues(
        IReadOnlyDictionary<string, string> baseSlotValues,
        IReadOnlyDictionary<string, string> overrides)
    {
        var result = new Dictionary<string, string>(baseSlotValues, StringComparer.Ordinal);
        foreach (var pair in NormalizeSlotValues(overrides))
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> NormalizeSlotValues(
        IReadOnlyDictionary<string, string>? slotValues)
    {
        if (slotValues is null || slotValues.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in slotValues)
        {
            var key = pair.Key?.Trim();
            var value = pair.Value?.Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    private static string SlotValueKey(IReadOnlyDictionary<string, string> slotValues)
    {
        if (slotValues.Count == 0)
        {
            return "slots:none";
        }

        return string.Join(
            "\u001e",
            slotValues
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "\u001f" + pair.Value));
    }

    private static IReadOnlyList<string> DraftCandidateNodeOptions(
        ReferenceCorpusInsertionBlueprintBeatPayload beat)
    {
        return beat.NodeIds
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static int RequestedSourceNodeReferenceCount(ReferenceCorpusInsertionBlueprintPayload blueprint)
    {
        return blueprint.Beats
            .SelectMany(beat => beat.NodeIds)
            .Count(nodeId => !string.IsNullOrWhiteSpace(nodeId));
    }

    private static ReferenceCorpusInsertionBlueprintBeatPayload BuildDraftCandidateBeatVariant(
        ReferenceCorpusInsertionBlueprintBeatPayload beat,
        IReadOnlyList<string> nodeOptions,
        int variantIndex)
    {
        if (nodeOptions.Count <= 1)
        {
            return beat;
        }

        var selectedNodeId = nodeOptions[(variantIndex + beat.BeatIndex) % nodeOptions.Count];
        return beat with
        {
            NodeIds = [selectedNodeId]
        };
    }

    private static DraftCandidateBlueprintVariant? BuildTransitionRepairVariant(
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
        DraftCandidateBlueprintVariant variant,
        ReferenceCorpusInsertionDraftPayload draft)
    {
        var transition = draft.Transitions.FirstOrDefault(transition =>
            string.Equals(transition.Decision, ReferenceCorpusTransitionDecisions.ReplacePiece, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(transition.ReplacementPieceId) &&
            !string.IsNullOrWhiteSpace(transition.ReplacementNodeId));
        if (transition is null ||
            transition.ReplacementPieceId is null ||
            string.IsNullOrWhiteSpace(transition.ReplacementNodeId))
        {
            return null;
        }

        var currentPiece = draft.Pieces.FirstOrDefault(piece =>
            string.Equals(piece.PieceId, transition.ReplacementPieceId, StringComparison.Ordinal));
        if (currentPiece is null)
        {
            return null;
        }

        var replacementNodeId = transition.ReplacementNodeId.Trim();
        var selectedBeat = selectedBlueprint.Beats.FirstOrDefault(beat =>
            string.Equals(beat.BeatId, currentPiece.BeatId, StringComparison.Ordinal));
        if (selectedBeat is null ||
            !selectedBeat.NodeIds.Contains(replacementNodeId, StringComparer.Ordinal) ||
            string.Equals(currentPiece.NodeId, replacementNodeId, StringComparison.Ordinal))
        {
            return null;
        }

        var repaired = false;
        var beats = variant.Blueprint.Beats
            .Select(beat =>
            {
                if (!string.Equals(beat.BeatId, currentPiece.BeatId, StringComparison.Ordinal))
                {
                    return beat;
                }

                repaired = true;
                return beat with
                {
                    NodeIds = [replacementNodeId]
                };
            })
            .ToArray();
        if (!repaired)
        {
            return null;
        }

        var originalKey = BlueprintNodeKey(variant.Blueprint);
        var repairedKey = string.Join('|', beats.SelectMany(beat => beat.NodeIds));
        if (string.Equals(originalKey, repairedKey, StringComparison.Ordinal))
        {
            return null;
        }

        var fingerprint = StableHash(
            selectedBlueprint.BlueprintId,
            variant.CandidateId,
            transition.TransitionId,
            currentPiece.PieceId,
            replacementNodeId)[..16];
        var blueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: $"{selectedBlueprint.BlueprintId}:transition-repair-{fingerprint}",
            QueryContextHash: selectedBlueprint.QueryContextHash,
            Strategy: $"{variant.Blueprint.Strategy}:transition_repair",
            Beats: beats);
        return new DraftCandidateBlueprintVariant(
            CandidateId: "corpus-draft-candidate-transition-repair-" + fingerprint,
            Strategy: "transition_repair",
            Explanation: "Rebuilds the selected blueprint with an allowed replacement source node requested by transition analysis.",
            Blueprint: blueprint,
            SlotValues: variant.SlotValues,
            SlotValueKey: variant.SlotValueKey);
    }

    private static ReferenceCorpusDraftCandidateNextActionPayload? BuildDraftCandidateNextAction(
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
        ReferenceCorpusInsertionDraftPayload draft)
    {
        var transition = draft.Transitions.FirstOrDefault(transition =>
            string.Equals(transition.Decision, ReferenceCorpusTransitionDecisions.ReplacePiece, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(transition.ReplacementPieceId));
        if (transition is null || transition.ReplacementPieceId is null)
        {
            return null;
        }

        var rejectedPiece = draft.Pieces.FirstOrDefault(piece =>
            string.Equals(piece.PieceId, transition.ReplacementPieceId, StringComparison.Ordinal));
        if (rejectedPiece is null)
        {
            return null;
        }

        var replacementNodeId = transition.ReplacementNodeId?.Trim();
        var selectedBeat = selectedBlueprint.Beats.FirstOrDefault(beat =>
            string.Equals(beat.BeatId, rejectedPiece.BeatId, StringComparison.Ordinal));
        var replacementInsideSelectedBeat = !string.IsNullOrWhiteSpace(replacementNodeId) &&
            selectedBeat?.NodeIds.Contains(replacementNodeId, StringComparer.Ordinal) == true;
        var reasonCode = replacementInsideSelectedBeat
            ? "transition_repair_failed"
            : "transition_replacement_outside_selected_blueprint";
        var feedback = new ReferenceCorpusBlueprintFeedbackPayload(
            RejectedBlueprintIds: [selectedBlueprint.BlueprintId],
            RejectedNodeIds: [rejectedPiece.NodeId],
            AvoidLibraryIds: [],
            AvoidAnchorIds: [],
            ProblemTags: ["transition_replacement_required", reasonCode],
            Notes: string.IsNullOrWhiteSpace(replacementNodeId)
                ? $"Transition {transition.TransitionId} rejected source node {rejectedPiece.NodeId}; regenerate blueprint with a compatible source."
                : $"Transition {transition.TransitionId} rejected source node {rejectedPiece.NodeId}; requested replacement node {replacementNodeId}.");
        return new ReferenceCorpusDraftCandidateNextActionPayload(
            Action: ReferenceCorpusDraftCandidateNextActions.RegenerateBlueprint,
            ReasonCode: reasonCode,
            Message: replacementInsideSelectedBeat
                ? "Transition repair remained blocked; regenerate the blueprint with this source node rejected."
                : "The transition resolver requested a replacement outside the selected blueprint; regenerate blueprint candidates.",
            TransitionId: transition.TransitionId,
            RejectedPieceId: rejectedPiece.PieceId,
            RejectedNodeId: rejectedPiece.NodeId,
            ReplacementNodeId: string.IsNullOrWhiteSpace(replacementNodeId) ? null : replacementNodeId,
            Feedback: feedback);
    }

private static string BlueprintNodeKey(ReferenceCorpusInsertionBlueprintPayload blueprint)
{
return string.Join('|', blueprint.Beats.SelectMany(beat => beat.NodeIds));
}

 private static IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload> ApplyBlueprintDifferenceAudit(
 IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload> candidates)
 {
 const double minimumDifferenceRatio = 0.34d;
 if (candidates.Count == 0)
 {
 return [];
 }

 var nodeSets = candidates
 .Select(candidate => candidate.Blueprint.Beats
 .SelectMany(beat => beat.NodeIds)
 .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
 .ToHashSet(StringComparer.Ordinal))
 .ToArray();
 var sourceKeys = candidates.Select(BuildBlueprintSourceDistributionKey).ToArray();
 var result = new ReferenceCorpusBlueprintCandidatePayload[candidates.Count];

 for (var index = 0; index < candidates.Count; index++)
 {
 var closestIndex = -1;
 var closestDifference = 1d;
 for (var otherIndex = 0; otherIndex < candidates.Count; otherIndex++)
 {
 if (otherIndex == index)
 {
 continue;
 }

 var unionCount = nodeSets[index].Union(nodeSets[otherIndex], StringComparer.Ordinal).Count();
 var intersectionCount = nodeSets[index].Intersect(nodeSets[otherIndex], StringComparer.Ordinal).Count();
 var difference = unionCount == 0 ? 0d : 1d - ((double)intersectionCount / unionCount);
 if (difference < closestDifference)
 {
 closestDifference = difference;
 closestIndex = otherIndex;
 }
 }

 var sourceDistributionDiffers = closestIndex < 0 ||
 !string.Equals(sourceKeys[index], sourceKeys[closestIndex], StringComparison.Ordinal);
 var strategyDiffers = closestIndex < 0 ||
 !string.Equals(candidates[index].Blueprint.Strategy, candidates[closestIndex].Blueprint.Strategy, StringComparison.Ordinal);
 var passed = closestIndex < 0 || closestDifference >= minimumDifferenceRatio ||
 (sourceDistributionDiffers && strategyDiffers);
 var diagnostics = new List<string>();
 if (closestIndex >= 0 && closestDifference < minimumDifferenceRatio)
 {
 diagnostics.Add("blueprint_node_set_too_similar");
 }

 if (!sourceDistributionDiffers)
 {
 diagnostics.Add("blueprint_source_distribution_unchanged");
 }

 if (!strategyDiffers)
 {
 diagnostics.Add("blueprint_strategy_unchanged");
 }

 result[index] = candidates[index] with
 {
 DifferenceAudit = new ReferenceCorpusBlueprintDifferenceAuditPayload(
 Passed: passed,
 NodeSetHash: StableHash(nodeSets[index].OrderBy(value => value, StringComparer.Ordinal).ToArray()),
 MinimumNodeDifferenceRatio: minimumDifferenceRatio,
 ClosestBlueprintId: closestIndex < 0 ? null : candidates[closestIndex].Blueprint.BlueprintId,
 ClosestNodeDifferenceRatio: closestIndex < 0 ? 1d : closestDifference,
 SourceDistributionDiffers: sourceDistributionDiffers,
 StrategyDiffers: strategyDiffers,
 Diagnostics: diagnostics)
 };
 }

 return result;
 }

 private static string BuildBlueprintSourceDistributionKey(ReferenceCorpusBlueprintCandidatePayload candidate)
 {
 return string.Join('|', candidate.SourceDistribution
 .OrderBy(source => source.LibraryId, StringComparer.Ordinal)
 .ThenBy(source => source.AnchorId)
 .Select(source => string.Join(':', source.LibraryId, source.AnchorId, source.NodeCount)));
 }

 private static ReferenceCorpusBlueprintIterationPayload BuildBlueprintIteration(
 ReferenceCorpusBlueprintFeedbackPayload? feedback,
 IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload> candidates)
 {
 var rejectedBlueprintIds = feedback?.RejectedBlueprintIds
 .Where(value => !string.IsNullOrWhiteSpace(value))
 .Select(value => value.Trim())
 .Distinct(StringComparer.Ordinal)
 .OrderBy(value => value, StringComparer.Ordinal)
 .ToArray() ?? [];
 var distinctCandidateCount = candidates
 .Select(candidate => candidate.DifferenceAudit?.NodeSetHash ?? StableHash(
 candidate.Blueprint.Beats
 .SelectMany(beat => beat.NodeIds)
 .OrderBy(value => value, StringComparer.Ordinal)
 .ToArray()))
 .Distinct(StringComparer.Ordinal)
 .Count();

 return new ReferenceCorpusBlueprintIterationPayload(
 Iteration: feedback is null ? 1 : 2,
 State: candidates.Count == 0
 ? "blocked"
 : feedback is null ? "awaiting_selection" : "feedback_applied",
 FeedbackApplied: feedback is not null,
 CandidateCount: candidates.Count,
 DistinctCandidateCount: distinctCandidateCount,
 RejectedBlueprintIds: rejectedBlueprintIds,
 CanIterate: candidates.Count > 0,
 CanSelect: candidates.Any(candidate => candidate.DifferenceAudit?.Passed != false));
 }

    private static BlueprintCandidateFilterResult ApplyBlueprintFeedback(
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        ReferenceCorpusBlueprintFeedbackPayload? feedback)
    {
        if (feedback is null)
        {
            return new BlueprintCandidateFilterResult(candidates, []);
        }

        var rejectedNodeIds = NormalizeTextSet(feedback.RejectedNodeIds);
        var avoidedLibraryIds = NormalizeTextSet(feedback.AvoidLibraryIds);
        var avoidedAnchorIds = NormalizeLongSet(feedback.AvoidAnchorIds);
        var rejectedFiltered = candidates
            .Where(candidate => !rejectedNodeIds.Contains(candidate.NodeId))
            .ToArray();
        var filtered = rejectedFiltered
            .Where(candidate => !avoidedLibraryIds.Contains(candidate.LibraryId))
            .Where(candidate => !avoidedAnchorIds.Contains(candidate.AnchorId))
            .ToArray();

        if (filtered.Length > 0)
        {
            return new BlueprintCandidateFilterResult(filtered, []);
        }

        if (rejectedFiltered.Length > 0 &&
            (avoidedLibraryIds.Count > 0 || avoidedAnchorIds.Count > 0))
        {
            return new BlueprintCandidateFilterResult(
                rejectedFiltered,
                ["avoid_sources_no_alternatives", "fallback_ignored_avoid_sources"]);
        }

        if (candidates.Count > 0 &&
            rejectedFiltered.Length == 0 &&
            rejectedNodeIds.Count > 0)
        {
            return new BlueprintCandidateFilterResult(
                rejectedFiltered,
                ["rejected_nodes_exhausted_candidates"]);
        }

        return new BlueprintCandidateFilterResult(rejectedFiltered, []);
    }

    private static IReadOnlyDictionary<string, string>? BuildCandidateSearchFilters(
        string naturalLanguageGoal,
        ReferenceCorpusBlueprintFeedbackPayload? feedback)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        var goal = naturalLanguageGoal ?? string.Empty;
        var problemTags = NormalizeTextSet(feedback?.ProblemTags);
        var featureFilterIndex = 0;
        var wantsActionAsEmotionCarrier =
            ContainsAny(goal, "动作替代心理", "动作代替心理", "动作承载情绪", "动作承载愤怒") ||
            (goal.Contains("动作", StringComparison.Ordinal) &&
                ContainsAny(goal, "心理描写", "心理", "不直说", "不直白", "表现愤怒", "表现怒意"));
        if (wantsActionAsEmotionCarrier ||
            problemTags.Contains("too_direct_emotion"))
        {
            AddFeatureFilter(
                filters,
                ref featureFilterIndex,
                family: "action",
                featureKey: "emotion_carrier",
                valueText: "action_over_psychology");
        }

        if (WantsSlowerRhythm(feedback))
        {
            AddFeatureFilter(
                filters,
                ref featureFilterIndex,
                family: "rhythm",
                featureKey: "length_band",
                valueNumMin: "16");
        }

        if (ContainsAny(goal, "触觉", "拳", "指节", "掌心", "手指", "骨"))
        {
            filters["sensory_sense"] = "tactile";
            filters["sensory_min_intensity"] = "0.8";
        }

        return filters.Count == 0 ? null : filters;
    }

    private static bool WantsSlowerRhythm(ReferenceCorpusBlueprintFeedbackPayload? feedback)
    {
        var problemTags = NormalizeTextSet(feedback?.ProblemTags);
        return problemTags.Contains("too_fast") ||
            ContainsAny(feedback?.Notes ?? string.Empty, "节奏太快", "节奏太急", "太快", "放慢", "慢一点");
    }

    private static bool SameFilters(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || left.Count == 0)
        {
            return right is null || right.Count == 0;
        }

        if (right is null || left.Count != right.Count)
        {
            return false;
        }

        return left.All(item =>
            right.TryGetValue(item.Key, out var value) &&
            string.Equals(item.Value, value, StringComparison.Ordinal));
    }

    private static void AddFeatureFilter(
        Dictionary<string, string> filters,
        ref int index,
        string family,
        string featureKey,
        string? valueText = null,
        string? valueNumMin = null,
        string? valueNumMax = null)
    {
        var prefix = "feature_filter_" + index.ToString(CultureInfo.InvariantCulture) + "_";
        filters[prefix + "family"] = family;
        filters[prefix + "key"] = featureKey;
        if (!string.IsNullOrWhiteSpace(valueText))
        {
            filters[prefix + "value_text"] = valueText;
        }

        if (!string.IsNullOrWhiteSpace(valueNumMin))
        {
            filters[prefix + "value_num_min"] = valueNumMin;
        }

        if (!string.IsNullOrWhiteSpace(valueNumMax))
        {
            filters[prefix + "value_num_max"] = valueNumMax;
        }

        index++;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> NormalizeDiagnosticGapReasons(IEnumerable<string> reasons)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reason in reasons)
        {
            var value = reason.Trim();
            if (value.Length > 0 && seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private static string DescribeFeedback(
        ReferenceCorpusBlueprintFeedbackPayload? feedback,
        IReadOnlyList<string>? diagnosticGapReasons = null)
    {
        if (feedback is null)
        {
            return "none";
        }

        var parts = new List<string>();
        var rejectedBlueprintIds = NormalizeTextSet(feedback.RejectedBlueprintIds);
        var rejectedNodeIds = NormalizeTextSet(feedback.RejectedNodeIds);
        var avoidLibraryIds = NormalizeTextSet(feedback.AvoidLibraryIds);
        var avoidAnchorIds = NormalizeLongSet(feedback.AvoidAnchorIds);
        var problemTags = NormalizeTextSet(feedback.ProblemTags);
        if (rejectedBlueprintIds.Count > 0)
        {
            parts.Add("rejected_blueprints:" + rejectedBlueprintIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (rejectedNodeIds.Count > 0)
        {
            parts.Add("rejected_nodes:" + rejectedNodeIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (avoidLibraryIds.Count > 0)
        {
            parts.Add("avoid_libraries:" + avoidLibraryIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (avoidAnchorIds.Count > 0)
        {
            parts.Add("avoid_anchors:" + avoidAnchorIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (problemTags.Count > 0)
        {
            parts.Add("problems:" + string.Join(',', problemTags.Order(StringComparer.Ordinal)));
        }

        var fallbackReasons = NormalizeDiagnosticGapReasons(diagnosticGapReasons ?? []);
        if (fallbackReasons.Count > 0)
        {
            parts.Add("fallback:" + string.Join(',', fallbackReasons));
        }

        return parts.Count == 0 ? "feedback_present" : string.Join(';', parts);
    }

    private static bool HasFeedback(ReferenceCorpusBlueprintFeedbackPayload? feedback)
    {
        if (feedback is null)
        {
            return false;
        }

        return NormalizeTextSet(feedback.RejectedBlueprintIds).Count > 0 ||
            NormalizeTextSet(feedback.RejectedNodeIds).Count > 0 ||
            NormalizeTextSet(feedback.AvoidLibraryIds).Count > 0 ||
            NormalizeLongSet(feedback.AvoidAnchorIds).Count > 0 ||
            NormalizeTextSet(feedback.ProblemTags).Count > 0 ||
            !string.IsNullOrWhiteSpace(feedback.Notes);
    }

    private async ValueTask<ReferenceCorpusHistoricalFeedbackProfile> ReadHistoricalFeedbackProfileAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT feedback_tags_json
                FROM reference_user_feedback
                WHERE novel_id = $novel_id
                  AND target_type = $target_type
                  AND decision = $decision
                  AND origin = $origin
                ORDER BY created_at DESC, feedback_id DESC
                LIMIT 100;
                """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            command.Parameters.AddWithValue("$target_type", ReferenceFeedbackTargetTypes.Blueprint);
            command.Parameters.AddWithValue("$decision", ReferenceFeedbackDecisions.Rejected);
            command.Parameters.AddWithValue("$origin", "corpus_blueprint_feedback");

            var nodeHashes = new HashSet<string>(StringComparer.Ordinal);
            var libraryHashes = new HashSet<string>(StringComparer.Ordinal);
            var anchorIds = new HashSet<long>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                foreach (var tag in ReadFeedbackTags(reader.GetString(0)))
                {
                    if (tag.StartsWith("node_hash:", StringComparison.Ordinal))
                    {
                        nodeHashes.Add(tag["node_hash:".Length..]);
                    }
                    else if (tag.StartsWith("library_hash:", StringComparison.Ordinal))
                    {
                        libraryHashes.Add(tag["library_hash:".Length..]);
                    }
                    else if (tag.StartsWith("anchor:", StringComparison.Ordinal) &&
                        long.TryParse(tag["anchor:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var anchorId))
                    {
                        anchorIds.Add(anchorId);
                    }
                }
            }

            return new ReferenceCorpusHistoricalFeedbackProfile(nodeHashes, libraryHashes, anchorIds);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask PersistBlueprintCandidatesAsync(
        ReferenceCorpusQueryContextPayload queryContext,
        IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var existingNodeIds = await ReadExistingTextNodeIdsAsync(
                connection,
                transaction,
                candidates
                    .SelectMany(candidate => candidate.Blueprint.Beats)
                    .SelectMany(beat => beat.NodeIds)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                cancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (var candidate in candidates)
            {
                await UpsertCorpusBlueprintAsync(connection, transaction, queryContext, candidate, now, cancellationToken);
                await UpsertCorpusBlueprintBeatsAsync(connection, transaction, candidate.Blueprint, cancellationToken);
                await UpsertBlueprintBeatPiecesAsync(connection, transaction, candidate.Blueprint, existingNodeIds, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async ValueTask UpsertCorpusBlueprintAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceCorpusQueryContextPayload queryContext,
        ReferenceCorpusBlueprintCandidatePayload candidate,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_corpus_blueprints
              (blueprint_id, novel_id, chapter_number, query_context_hash, assembly_strategy,
               coverage_score, gap_reasons_json, gap_positions_json, query_context_json,
               source_distribution_json, feedback_reason, created_at, updated_at)
            VALUES
              ($blueprint_id, $novel_id, $chapter_number, $query_context_hash, $assembly_strategy,
               $coverage_score, $gap_reasons_json, $gap_positions_json, $query_context_json,
               $source_distribution_json, $feedback_reason, $created_at, $updated_at)
            ON CONFLICT(blueprint_id) DO UPDATE SET
              novel_id = excluded.novel_id,
              chapter_number = excluded.chapter_number,
              query_context_hash = excluded.query_context_hash,
              assembly_strategy = excluded.assembly_strategy,
              coverage_score = excluded.coverage_score,
              gap_reasons_json = excluded.gap_reasons_json,
              gap_positions_json = excluded.gap_positions_json,
              query_context_json = excluded.query_context_json,
              source_distribution_json = excluded.source_distribution_json,
              feedback_reason = excluded.feedback_reason,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$blueprint_id", candidate.Blueprint.BlueprintId);
        command.Parameters.AddWithValue("$novel_id", queryContext.ChapterContext.NovelId);
        command.Parameters.AddWithValue("$chapter_number", queryContext.ChapterContext.ChapterNumber);
        command.Parameters.AddWithValue("$query_context_hash", candidate.Blueprint.QueryContextHash);
        command.Parameters.AddWithValue("$assembly_strategy", candidate.Blueprint.Strategy);
        command.Parameters.AddWithValue("$coverage_score", candidate.CoverageScore);
        command.Parameters.AddWithValue("$gap_reasons_json", JsonSerializer.Serialize(candidate.GapReasons, JsonOptions));
        command.Parameters.AddWithValue("$gap_positions_json", JsonSerializer.Serialize(candidate.GapPositions, JsonOptions));
        command.Parameters.AddWithValue("$query_context_json", JsonSerializer.Serialize(queryContext, JsonOptions));
        command.Parameters.AddWithValue("$source_distribution_json", JsonSerializer.Serialize(candidate.SourceDistribution, JsonOptions));
        command.Parameters.AddWithValue("$feedback_reason", candidate.FeedbackReason);
        command.Parameters.AddWithValue("$created_at", timestamp);
        command.Parameters.AddWithValue("$updated_at", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<HashSet<string>> ReadExistingTextNodeIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> nodeIds,
        CancellationToken cancellationToken)
    {
        var normalized = nodeIds
            .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (normalized.Length == 0)
        {
            return existing;
        }

        var builder = new StringBuilder("""
            SELECT node_id
            FROM reference_text_nodes
            WHERE 1 = 1
            """);
        var parameters = new List<(string Name, object Value)>();
        AppendInClause(builder, parameters, "node_id", normalized, "persist_node_id");

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            existing.Add(reader.GetString(0));
        }

        return existing;
    }

    private static async ValueTask UpsertCorpusBlueprintBeatsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        CancellationToken cancellationToken)
    {
        foreach (var beat in blueprint.Beats)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_corpus_blueprint_beats
                  (blueprint_id, beat_id, beat_index, role_in_beat, narrative_function)
                VALUES
                  ($blueprint_id, $beat_id, $beat_index, $role_in_beat, $narrative_function)
                ON CONFLICT(blueprint_id, beat_id) DO UPDATE SET
                  beat_index = excluded.beat_index,
                  role_in_beat = excluded.role_in_beat,
                  narrative_function = excluded.narrative_function;
                """;
            command.Parameters.AddWithValue("$blueprint_id", blueprint.BlueprintId);
            command.Parameters.AddWithValue("$beat_id", beat.BeatId);
            command.Parameters.AddWithValue("$beat_index", beat.BeatIndex);
            command.Parameters.AddWithValue("$role_in_beat", beat.RoleInBeat);
            command.Parameters.AddWithValue("$narrative_function", beat.NarrativeFunction);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask UpsertBlueprintBeatPiecesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        IReadOnlySet<string> existingNodeIds,
        CancellationToken cancellationToken)
    {
        foreach (var beat in blueprint.Beats)
        {
            for (var index = 0; index < beat.NodeIds.Count; index++)
            {
                var nodeId = beat.NodeIds[index];
                if (!existingNodeIds.Contains(nodeId))
                {
                    continue;
                }

                await UpsertBlueprintBeatPieceAsync(
                    connection,
                    transaction,
                    beat.BeatId,
                    nodeId,
                    beat.RoleInBeat,
                    index,
                    cancellationToken);
            }
        }
    }

    private async ValueTask PersistBlueprintFeedbackAsync(
        GenerateReferenceCorpusBlueprintCandidatesPayload input,
        ReferenceCorpusBlueprintFeedbackPayload? feedback,
        IReadOnlyList<string> diagnosticGapReasons,
        CancellationToken cancellationToken)
    {
        if (!HasFeedback(feedback))
        {
            return;
        }

        var targetId = BlueprintFeedbackTargetId(input, feedback!);
        var feedbackTags = BlueprintFeedbackTags(feedback!, diagnosticGapReasons);
        var feedbackId = "corpus-feedback-" + StableHash(
            input.ChapterContext.NovelId.ToString(CultureInfo.InvariantCulture),
            input.ChapterContext.ChapterNumber.ToString(CultureInfo.InvariantCulture),
            targetId,
            JsonSerializer.Serialize(feedback, JsonOptions),
            string.Join('|', diagnosticGapReasons))[..24];

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO reference_user_feedback
                  (feedback_id, novel_id, target_type, target_id, decision, material_id, candidate_id,
                   blueprint_id, beat_id, feedback_tags_json, note, edited_text_hash, origin, created_at)
                VALUES
                  ($feedback_id, $novel_id, $target_type, $target_id, $decision, '', '',
                   0, '', $feedback_tags_json, $note, '', $origin, $created_at);
                """;
            command.Parameters.AddWithValue("$feedback_id", feedbackId);
            command.Parameters.AddWithValue("$novel_id", input.ChapterContext.NovelId);
            command.Parameters.AddWithValue("$target_type", ReferenceFeedbackTargetTypes.Blueprint);
            command.Parameters.AddWithValue("$target_id", targetId);
            command.Parameters.AddWithValue("$decision", ReferenceFeedbackDecisions.Rejected);
            command.Parameters.AddWithValue("$feedback_tags_json", JsonSerializer.Serialize(feedbackTags, JsonOptions));
            command.Parameters.AddWithValue("$note", NormalizeFeedbackNote(feedback!.Notes));
            command.Parameters.AddWithValue("$origin", "corpus_blueprint_feedback");
            command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static string BlueprintFeedbackTargetId(
        GenerateReferenceCorpusBlueprintCandidatesPayload input,
        ReferenceCorpusBlueprintFeedbackPayload feedback)
    {
        var rejectedBlueprintId = NormalizeTextSet(feedback.RejectedBlueprintIds).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(rejectedBlueprintId))
        {
            return rejectedBlueprintId;
        }

        return "chapter-" +
            input.ChapterContext.NovelId.ToString(CultureInfo.InvariantCulture) +
            "-" +
            input.ChapterContext.ChapterNumber.ToString(CultureInfo.InvariantCulture) +
            "-blueprint-feedback-" +
            StableHash(JsonSerializer.Serialize(feedback, JsonOptions))[..16];
    }

    private static IReadOnlyList<string> BlueprintFeedbackTags(
        ReferenceCorpusBlueprintFeedbackPayload feedback,
        IReadOnlyList<string> diagnosticGapReasons)
    {
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string value)
        {
            var normalized = value.Trim();
            if (normalized.Length > 0 && normalized.Length <= 128 && seen.Add(normalized))
            {
                tags.Add(normalized);
            }
        }

        var rejectedBlueprintIds = NormalizeTextSet(feedback.RejectedBlueprintIds);
        var rejectedNodeIds = NormalizeTextSet(feedback.RejectedNodeIds);
        var avoidLibraryIds = NormalizeTextSet(feedback.AvoidLibraryIds);
        var avoidAnchorIds = NormalizeLongSet(feedback.AvoidAnchorIds);
        foreach (var tag in NormalizeTextSet(feedback.ProblemTags).Order(StringComparer.Ordinal))
        {
            Add(tag);
        }

        if (rejectedBlueprintIds.Count > 0)
        {
            Add("rejected_blueprints:" + rejectedBlueprintIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (rejectedNodeIds.Count > 0)
        {
            Add("rejected_nodes:" + rejectedNodeIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (avoidLibraryIds.Count > 0)
        {
            Add("avoid_libraries:" + avoidLibraryIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (avoidAnchorIds.Count > 0)
        {
            Add("avoid_anchors:" + avoidAnchorIds.Count.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var nodeId in rejectedNodeIds.Order(StringComparer.Ordinal))
        {
            Add("node_hash:" + FeedbackNodeHash(nodeId));
        }

        foreach (var libraryId in avoidLibraryIds.Order(StringComparer.Ordinal))
        {
            Add("library_hash:" + FeedbackLibraryHash(libraryId));
        }

        foreach (var anchorId in avoidAnchorIds.Order())
        {
            Add("anchor:" + anchorId.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var reason in diagnosticGapReasons)
        {
            Add("fallback:" + reason);
        }

        return tags;
    }

    private static IReadOnlyList<string> ReadFeedbackTags(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(value, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string FeedbackNodeHash(string nodeId)
    {
        return StableHash(nodeId)[..16];
    }

    private static string FeedbackLibraryHash(string libraryId)
    {
        return StableHash(libraryId)[..16];
    }

    private static string NormalizeFeedbackNote(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= 2_000 ? normalized : normalized[..2_000];
    }

    private static bool IsHookLike(
        ReferenceCorpusQueryContextPayload queryContext,
        ReferenceCorpusCandidatePayload candidate)
    {
        if (queryContext.RequiredNarrativeFunctions.Contains("withhold_answer", StringComparer.Ordinal) ||
            queryContext.CommercialMechanic.Contains("withheld", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.TextPreview.Contains("没有立刻", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("不立刻", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("不开口", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("回头", StringComparison.Ordinal);
        }

        return false;
    }

    private static HashSet<string> NormalizeTextSet(IReadOnlyList<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.Ordinal) ?? [];
    }

    private static HashSet<long> NormalizeLongSet(IReadOnlyList<long>? values)
    {
        return values?.ToHashSet() ?? [];
    }

    private async ValueTask<string> CurrentChapterTextAsync(
        CurrentChapterContextPayload chapterContext,
        CancellationToken cancellationToken)
    {
        if (chapterContext.CurrentDraftText is not null)
        {
            return chapterContext.CurrentDraftText;
        }

        var chapters = await _chapters.GetChaptersAsync(chapterContext.NovelId, cancellationToken);
        var chapter = chapters.FirstOrDefault(item => item.ChapterNumber == chapterContext.ChapterNumber);
        return chapter is null
            ? string.Empty
            : await _chapters.GetContentAsync(chapterContext.NovelId, chapter.FilePath, cancellationToken);
    }

    private async ValueTask<string> DatabasePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "reference-anchor",
            "index.sqlite");
    }

    private static async ValueTask EnsureSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async ValueTask<IReadOnlyList<LicensedSourcePiece>> ReadSourcePiecesAsync(
        SqliteConnection connection,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        CancellationToken cancellationToken)
    {
        var candidatesByNode = candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.NodeId))
            .GroupBy(item => item.NodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Score).First(),
                StringComparer.Ordinal);
        var requested = blueprint.Beats
            .SelectMany(beat => beat.NodeIds.Select((nodeId, index) => new RequestedPiece(beat, nodeId, index)))
            .ToArray();
        if (requested.Length == 0)
        {
            return [];
        }

        var nodeIds = requested.Select(item => item.NodeId).Distinct(StringComparer.Ordinal).ToArray();
        var builder = new StringBuilder("""
            SELECT n.node_id,
                   n.text,
                   n.text_hash,
                   n.anchor_id,
                   lic.license_state,
                   lic.reuse_policy,
                   lic.max_verbatim_ratio,
                   lic.cleared_for_insertion
            FROM reference_text_nodes n
            JOIN reference_source_license lic ON lic.anchor_id = n.anchor_id
            WHERE 1 = 1
            """);
        var parameters = new List<(string Name, object Value)>();
        AppendInClause(builder, parameters, "n.node_id", nodeIds, "node_id");

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var rows = new Dictionary<string, SourceNodeRow>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetString(0)] = new SourceNodeRow(
                NodeId: reader.GetString(0),
                Text: reader.GetString(1),
                TextHash: reader.GetString(2),
                AnchorId: reader.GetInt64(3),
                LicenseState: reader.GetString(4),
                ReusePolicy: reader.GetString(5),
                MaxVerbatimRatio: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                ClearedForInsertion: reader.GetInt32(7) != 0);
        }

        var transferSlotConstraints = await ReadTransferSlotConstraintsAsync(connection, nodeIds, cancellationToken);
        var pieces = new List<LicensedSourcePiece>(requested.Length);
        foreach (var request in requested)
        {
            if (!rows.TryGetValue(request.NodeId, out var row) ||
                !candidatesByNode.TryGetValue(request.NodeId, out var candidate))
            {
                continue;
            }

            pieces.Add(new LicensedSourcePiece(
                PieceId: "piece-" + StableHash(request.Beat.BeatId, request.NodeId)[..16],
                BeatId: request.Beat.BeatId,
                CandidateId: candidate.CandidateId,
                NodeId: request.NodeId,
                AnchorId: row.AnchorId,
                LibraryId: candidate.LibraryId,
                TextHash: row.TextHash,
                LicenseState: row.LicenseState,
                ReusePolicy: row.ReusePolicy,
                SourceText: row.Text,
                ClearedForInsertion: row.ClearedForInsertion,
                MaxVerbatimRatio: row.MaxVerbatimRatio,
                AllowedTransferSlotNames: transferSlotConstraints.TryGetValue(request.NodeId, out var allowedSlots)
                    ? allowedSlots
                    : FrozenSet<string>.Empty,
                SequenceIndex: request.SequenceIndex));
        }

        return pieces;
    }

    private static async ValueTask<IReadOnlyDictionary<string, IReadOnlySet<string>>> ReadTransferSlotConstraintsAsync(
        SqliteConnection connection,
        IReadOnlyCollection<string> nodeIds,
        CancellationToken cancellationToken)
    {
        if (nodeIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        }

        var builder = new StringBuilder("""
            SELECT source_node_id,
                   transfer_slots_json
            FROM reference_technique_specimens
            WHERE validity_state = 'active'
              AND review_state <> 'rejected'
            """);
        var parameters = new List<(string Name, object Value)>();
        AppendInClause(builder, parameters, "source_node_id", nodeIds, "transfer_slot_node_id");

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.GetString(0);
            var slotNames = ReadTransferSlotNames(reader.GetString(1));
            if (slotNames.Count == 0)
            {
                continue;
            }

            if (!result.TryGetValue(nodeId, out var existing))
            {
                existing = new HashSet<string>(StringComparer.Ordinal);
                result[nodeId] = existing;
            }

            foreach (var slotName in slotNames)
            {
                existing.Add(slotName);
            }
        }

        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlySet<string>)item.Value.ToFrozenSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> ReadTransferSlotNames(string transferSlotsJson)
    {
        if (string.IsNullOrWhiteSpace(transferSlotsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(transferSlotsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                string? rawSlotName = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("slot_name", out var slotNameElement) &&
                        slotNameElement.ValueKind == JsonValueKind.String => slotNameElement.GetString(),
                    _ => null
                };
                if (NormalizeTransferSlotName(rawSlotName) is { } slotName)
                {
                    result.Add(slotName);
                }
            }

            return result
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? NormalizeTransferSlotName(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return normalized switch
        {
            "character" or "person" or "role" or "角色" or "人名" => "character",
            "place" or "location" or "scene_place" or "地点" or "地名" or "场景" => "place",
            "honorific" or "appellation" or "relationship_title" or "称谓" or "专属称谓" => "honorific",
            "plot_object" or "prop" or "object" or "item" or "道具" or "情节道具" => "plot_object",
            _ => null
        };
    }

    private static async ValueTask UpsertBeatPiecesAsync(
        SqliteConnection connection,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        IReadOnlyList<LicensedSourcePiece> sourcePieces,
        CancellationToken cancellationToken)
    {
        var piecesByBeatAndNode = sourcePieces.ToDictionary(
            item => (item.BeatId, item.NodeId),
            item => item,
            ValueTupleComparer.Instance);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var beat in blueprint.Beats)
        {
            for (var index = 0; index < beat.NodeIds.Count; index++)
            {
                var nodeId = beat.NodeIds[index];
                if (!piecesByBeatAndNode.ContainsKey((beat.BeatId, nodeId)))
                {
                    continue;
                }

                await UpsertBlueprintBeatPieceAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    beat.BeatId,
                    nodeId,
                    beat.RoleInBeat,
                    index,
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask UpsertBlueprintBeatPieceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string beatId,
        string nodeId,
        string roleInBeat,
        int sequenceIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_blueprint_beat_pieces
              (beat_id, node_id, observation_id, role_in_beat, sequence_index)
            VALUES
              ($beat_id, $node_id, NULL, $role_in_beat, $sequence_index)
            ON CONFLICT(beat_id, node_id) DO UPDATE SET
              role_in_beat = excluded.role_in_beat,
              sequence_index = excluded.sequence_index;
            """;
        command.Parameters.AddWithValue("$beat_id", beatId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$role_in_beat", roleInBeat);
        command.Parameters.AddWithValue("$sequence_index", sequenceIndex);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ReferenceCorpusInsertionGatePayload EvaluateGate(
        IReadOnlyList<LicensedSourcePiece> sourcePieces,
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> assembledPieces)
    {
        var sources = sourcePieces.ToDictionary(item => item.PieceId, StringComparer.Ordinal);
        var errors = new List<string>();
        var gatePieces = new List<ReferenceCorpusInsertionGatePiecePayload>(assembledPieces.Count);
        foreach (var piece in assembledPieces)
        {
            if (!sources.TryGetValue(piece.PieceId, out var source))
            {
                errors.Add($"source_missing:{piece.PieceId}");
                continue;
            }

            if (!source.ClearedForInsertion)
            {
                errors.Add($"license_not_cleared:{piece.NodeId}");
            }

            if (source.ReusePolicy is not (ReferenceCorpusReusePolicies.VerbatimOk or ReferenceCorpusReusePolicies.AdaptedOnly))
            {
                errors.Add($"reuse_policy_not_insertable:{piece.NodeId}");
            }

            var policy = BuildSimilarityPolicy(source);
            var similarity = ReferenceCorpusSimilarityGate.Evaluate(
                new ReferenceCorpusSimilarityPiece(
                    piece.PieceId,
                    piece.NodeId,
                    source.SourceText,
                    piece.OutputText),
                policy);
            gatePieces.Add(new ReferenceCorpusInsertionGatePiecePayload(
                piece.PieceId,
                piece.NodeId,
                similarity.ShouldBlock,
                Math.Round(similarity.FourGramContainmentRatio, 6),
                Math.Round(similarity.LongestCommonSubstringRatio, 6),
                similarity.Violations
                    .Select(violation => new ReferenceCorpusInsertionGateViolationPayload(
                        violation.Metric,
                        Math.Round(violation.Actual, 6),
                        Math.Round(violation.Threshold, 6)))
                    .ToArray()));
        }

        var passed = errors.Count == 0 && gatePieces.All(piece => !piece.ShouldBlock);
        return new ReferenceCorpusInsertionGatePayload(
            passed,
            passed ? "passed" : "blocked",
            errors,
            gatePieces);
    }

    private static ReferenceCorpusDraftAuditPayload EvaluateDraftAudit(
        IReadOnlyList<LicensedSourcePiece> sourcePieces,
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> assembledPieces,
        IReadOnlyList<ReferenceCorpusTransitionPayload> transitions,
        string assembledText)
    {
        var sources = sourcePieces.ToDictionary(item => item.PieceId, StringComparer.Ordinal);
        var emittedPieceCounts = assembledPieces
            .GroupBy(piece => piece.PieceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var errors = new List<string>();
        var auditPieces = new List<ReferenceCorpusDraftAuditPiecePayload>(assembledPieces.Count);
        foreach (var piece in assembledPieces)
        {
            var violations = new List<ReferenceCorpusDraftAuditViolationPayload>();
            if (!sources.TryGetValue(piece.PieceId, out var source))
            {
                AddAuditViolation(
                    violations,
                    piece,
                    spanId: null,
                    code: "source_missing",
                    message: "The draft piece no longer has a readable source node.");
            }
            else
            {
                if (emittedPieceCounts.TryGetValue(piece.PieceId, out var count) && count > 1)
                {
                    AddAuditViolation(
                        violations,
                        piece,
                        spanId: null,
                        code: "piece_duplicate",
                        message: "The same selected source piece was emitted more than once.");
                }

                if (!string.Equals(piece.NodeId, source.NodeId, StringComparison.Ordinal) ||
                    !string.Equals(piece.BeatId, source.BeatId, StringComparison.Ordinal) ||
                    !string.Equals(piece.CandidateId, source.CandidateId, StringComparison.Ordinal))
                {
                    AddAuditViolation(
                        violations,
                        piece,
                        spanId: null,
                        code: "piece_source_identity_mismatch",
                        message: "The assembled piece metadata does not match the selected blueprint source.");
                }

                if (!string.Equals(piece.SourceTextHash, source.TextHash, StringComparison.Ordinal))
                {
                    AddAuditViolation(
                        violations,
                        piece,
                        spanId: null,
                        code: "piece_source_hash_mismatch",
                        message: "The assembled piece source hash does not match the selected blueprint source.");
                }

                if (!piece.PreservedHashMatches)
                {
                    AddAuditViolation(
                        violations,
                        piece,
                        spanId: null,
                        code: "preserved_text_hash_mismatch",
                        message: "The non-slot text hash differs between source and output.");
                }

                foreach (var span in piece.PreservedSpans)
                {
                    AuditPreservedSpan(source.SourceText, piece, span, violations);
                }

                foreach (var span in piece.LockedSpans)
                {
                    AuditLockedSpan(source.SourceText, piece, span, violations);
                }

                AuditSlotReplacements(source.SourceText, source.AllowedTransferSlotNames, piece, violations);
                AuditPieceOutputCoverage(piece, violations);
            }

            var mismatchedSpanCount = piece.PreservedSpans.Count(span => !span.Matches);
            foreach (var violation in violations)
            {
                errors.Add($"{violation.Code}:{violation.NodeId}" + (violation.SpanId is null ? string.Empty : $":{violation.SpanId}"));
            }

            auditPieces.Add(new ReferenceCorpusDraftAuditPiecePayload(
                PieceId: piece.PieceId,
                NodeId: piece.NodeId,
                Passed: violations.Count == 0,
                PreservedSpanCount: piece.PreservedSpans.Count,
                MismatchedSpanCount: mismatchedSpanCount,
                Violations: violations));
        }

        var auditTransitions = AuditTransitions(assembledPieces, transitions, assembledText, errors, auditPieces);
        AuditAssembledTextEnvelope(assembledPieces, transitions, assembledText, errors, auditPieces);

        foreach (var source in sourcePieces)
        {
            if (emittedPieceCounts.ContainsKey(source.PieceId))
            {
                continue;
            }

            var violations = new List<ReferenceCorpusDraftAuditViolationPayload>();
            AddAuditViolation(
                violations,
                source.PieceId,
                source.NodeId,
                spanId: null,
                code: "piece_missing",
                message: "A selected blueprint source piece was not emitted by the draft assembler.");
            foreach (var violation in violations)
            {
                errors.Add($"{violation.Code}:{violation.NodeId}");
            }

            auditPieces.Add(new ReferenceCorpusDraftAuditPiecePayload(
                PieceId: source.PieceId,
                NodeId: source.NodeId,
                Passed: false,
                PreservedSpanCount: 0,
                MismatchedSpanCount: 0,
                Violations: violations));
        }

        var passed = errors.Count == 0 &&
            auditPieces.All(piece => piece.Passed) &&
            auditTransitions.All(transition => transition.Passed);
        return new ReferenceCorpusDraftAuditPayload(
            Passed: passed,
            Status: passed ? "passed" : "blocked",
            Errors: errors,
            Pieces: auditPieces,
            Transitions: auditTransitions);
    }

    private static void AuditSlotReplacements(
        string sourceText,
        IReadOnlySet<string> allowedTransferSlotNames,
        ReferenceCorpusInsertionPiecePayload piece,
        List<ReferenceCorpusDraftAuditViolationPayload> violations)
    {
        var ordered = piece.SlotReplacements
            .OrderBy(replacement => replacement.SourceStart)
            .ToArray();
        var lastSourceEnd = 0;
        var lastOutputEnd = 0;
        foreach (var replacement in ordered)
        {
            if (replacement.SourceStart < 0 ||
                replacement.SourceEnd <= replacement.SourceStart ||
                replacement.SourceEnd > sourceText.Length ||
                replacement.OutputStart < 0 ||
                replacement.OutputEnd < replacement.OutputStart ||
                replacement.OutputEnd > piece.OutputText.Length ||
                replacement.SourceStart < lastSourceEnd ||
                replacement.OutputStart < lastOutputEnd)
            {
                AddAuditViolation(
                    violations,
                    piece,
                    spanId: null,
                    code: "slot_replacement_range_invalid",
                    message: "The slot replacement range is invalid or overlaps another replacement.");
                continue;
            }

            var sourceValue = sourceText[replacement.SourceStart..replacement.SourceEnd];
            var outputValue = piece.OutputText[replacement.OutputStart..replacement.OutputEnd];
            if (!string.Equals(sourceValue, replacement.SourceValue, StringComparison.Ordinal) ||
                !string.Equals(outputValue, replacement.ReplacementValue, StringComparison.Ordinal))
            {
                AddAuditViolation(
                    violations,
                    piece,
                    spanId: null,
                    code: "slot_replacement_value_mismatch",
                    message: "The slot replacement value does not match the recorded source or output range.");
            }

            if (IsUnsafeSlotReplacement(sourceText, replacement))
            {
                AddAuditViolation(
                    violations,
                    piece,
                    spanId: null,
                    code: "slot_replacement_unsafe_range",
                    message: "The slot replacement consumes too much source text for a safe named-slot transfer.");
            }

            if (allowedTransferSlotNames.Count > 0 &&
                NormalizeTransferSlotName(replacement.SlotName) is not { } normalizedSlotName)
            {
                AddAuditViolation(
                    violations,
                    piece,
                    spanId: null,
                    code: "slot_replacement_transfer_slot_disallowed",
                    message: $"The slot replacement '{replacement.SlotName}' is not a recognized technique transfer slot.");
            }
            else if (allowedTransferSlotNames.Count > 0 &&
                NormalizeTransferSlotName(replacement.SlotName) is { } slotName &&
                !allowedTransferSlotNames.Contains(slotName))
            {
                AddAuditViolation(
                    violations,
                    piece,
                    spanId: null,
                    code: "slot_replacement_transfer_slot_disallowed",
                    message: $"The slot replacement '{slotName}' is not declared by the active technique specimen transfer_slots.");
            }

            if (piece.LockedSpans.Any(span =>
                    replacement.SourceStart < span.SourceEnd &&
                    replacement.SourceEnd > span.SourceStart))
            {
                AddAuditViolation(
                    violations,
                    piece,
                    spanId: null,
                    code: "slot_replacement_locked_range",
                    message: "The slot replacement overlaps a locked source range.");
            }

            lastSourceEnd = Math.Max(lastSourceEnd, replacement.SourceEnd);
            lastOutputEnd = Math.Max(lastOutputEnd, replacement.OutputEnd);
        }
    }

    private static void AuditPieceOutputCoverage(
        ReferenceCorpusInsertionPiecePayload piece,
        List<ReferenceCorpusDraftAuditViolationPayload> violations)
    {
        if (piece.OutputText.Length == 0)
        {
            return;
        }

        var coveredRanges = piece.PreservedSpans
            .Select(span => (span.OutputStart, span.OutputEnd))
            .Concat(piece.SlotReplacements.Select(replacement => (replacement.OutputStart, replacement.OutputEnd)))
            .Where(range =>
                range.OutputStart >= 0 &&
                range.OutputEnd > range.OutputStart &&
                range.OutputEnd <= piece.OutputText.Length)
            .OrderBy(range => range.OutputStart)
            .ThenBy(range => range.OutputEnd)
            .ToArray();
        var cursor = 0;
        foreach (var range in coveredRanges)
        {
            if (range.OutputStart > cursor)
            {
                AddAuditViolation(
                    violations,
                    piece,
                    spanId: null,
                    code: "piece_output_untracked_range",
                    message: "The assembled piece contains output text not covered by preserved spans or slot replacements.");
                return;
            }

            cursor = Math.Max(cursor, range.OutputEnd);
        }

        if (cursor < piece.OutputText.Length)
        {
            AddAuditViolation(
                violations,
                piece,
                spanId: null,
                code: "piece_output_untracked_range",
                message: "The assembled piece contains output text not covered by preserved spans or slot replacements.");
        }
    }

    private static bool IsUnsafeSlotReplacement(
        string sourceText,
        ReferenceCorpusSlotReplacementPayload replacement)
    {
        var sourceLength = replacement.SourceEnd - replacement.SourceStart;
        if (sourceLength <= 0)
        {
            return true;
        }

        var trimmedSource = sourceText.Trim();
        if (string.Equals(replacement.SourceValue.Trim(), trimmedSource, StringComparison.Ordinal))
        {
            return true;
        }

        if (sourceLength >= Math.Max(8, (int)Math.Ceiling(sourceText.Length * 0.5)))
        {
            return true;
        }

        return replacement.SourceValue.Any(static ch => ch is '。' or '！' or '？' or '\n' or '\r');
    }

    private static void AuditAssembledTextEnvelope(
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> assembledPieces,
        IReadOnlyList<ReferenceCorpusTransitionPayload> transitions,
        string assembledText,
        List<string> errors,
        List<ReferenceCorpusDraftAuditPiecePayload> auditPieces)
    {
        var expected = ComposeAssembledText(assembledPieces, transitions);
        if (string.Equals(assembledText.Trim(), expected.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        var target = auditPieces.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        var violations = target.Violations.ToList();
        AddAuditViolation(
            violations,
            target.PieceId,
            target.NodeId,
            spanId: null,
            code: "assembled_text_untracked_output",
            message: "The final assembled text contains output not covered by audited pieces.");
        errors.Add($"assembled_text_untracked_output:{target.NodeId}");
        var index = auditPieces.IndexOf(target);
        auditPieces[index] = target with
        {
            Passed = false,
            Violations = violations
        };
    }

    private static IReadOnlyList<ReferenceCorpusDraftAuditTransitionPayload> AuditTransitions(
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> assembledPieces,
        IReadOnlyList<ReferenceCorpusTransitionPayload> transitions,
        string assembledText,
        List<string> errors,
        List<ReferenceCorpusDraftAuditPiecePayload> auditPieces)
    {
        var expectedGaps = BuildExpectedTransitionGaps(assembledPieces);
        var auditTransitions = new List<ReferenceCorpusDraftAuditTransitionPayload>(Math.Max(transitions.Count, expectedGaps.Count));
        if (expectedGaps.Count == 0 && transitions.Count == 0)
        {
            return auditTransitions;
        }

        var pieceIds = assembledPieces.Select(piece => piece.PieceId).ToHashSet(StringComparer.Ordinal);
        var pieceIndexById = assembledPieces
            .Select((piece, index) => (piece.PieceId, Index: index))
            .ToDictionary(item => item.PieceId, item => item.Index, StringComparer.Ordinal);
        var transitionIds = transitions
            .GroupBy(transition => transition.TransitionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var transitionPairs = transitions
            .GroupBy(transition => (transition.AfterPieceId, transition.BeforePieceId))
            .ToDictionary(group => group.Key, group => group.Count());
        var expectedGapByPair = expectedGaps
            .ToDictionary(gap => (gap.AfterPieceId, gap.BeforePieceId), gap => gap);
        foreach (var transition in transitions)
        {
            var violations = new List<ReferenceCorpusDraftAuditViolationPayload>();
            var targetPiece = assembledPieces.FirstOrDefault(piece =>
                string.Equals(piece.PieceId, transition.AfterPieceId, StringComparison.Ordinal) ||
                string.Equals(piece.PieceId, transition.BeforePieceId, StringComparison.Ordinal)) ??
                assembledPieces.FirstOrDefault();
            if (targetPiece is null)
            {
                continue;
            }

            if (transitionIds.TryGetValue(transition.TransitionId, out var transitionIdCount) && transitionIdCount > 1)
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_duplicate_id",
                    message: "The same transition id was emitted more than once.",
                    transitionId: transition.TransitionId);
            }

            if (transitionPairs.TryGetValue((transition.AfterPieceId, transition.BeforePieceId), out var pairCount) && pairCount > 1)
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_duplicate_pair",
                    message: "More than one transition was emitted for the same piece gap.",
                    transitionId: transition.TransitionId);
            }

            if (!pieceIds.Contains(transition.AfterPieceId) ||
                !pieceIds.Contains(transition.BeforePieceId) ||
                string.Equals(transition.AfterPieceId, transition.BeforePieceId, StringComparison.Ordinal))
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_piece_reference_invalid",
                    message: "The transition does not point to two distinct assembled pieces.",
                    transitionId: transition.TransitionId);
            }
            else if (pieceIndexById[transition.AfterPieceId] + 1 != pieceIndexById[transition.BeforePieceId])
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_piece_pair_not_adjacent",
                    message: "The transition does not point to adjacent assembled pieces.",
                    transitionId: transition.TransitionId);
            }
            else if (!expectedGapByPair.TryGetValue((transition.AfterPieceId, transition.BeforePieceId), out var expectedGap) ||
                !string.Equals(transition.GapId, expectedGap.GapId, StringComparison.Ordinal))
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_gap_id_mismatch",
                    message: "The transition gap id does not match its adjacent piece pair.",
                    transitionId: transition.TransitionId);
            }

            var expectedHash = StableTextHash(transition.Text);
            if (!string.Equals(transition.TextHash, expectedHash, StringComparison.Ordinal))
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_text_hash_mismatch",
                    message: "The transition text hash does not match its text.",
                    transitionId: transition.TransitionId);
            }

            if (!transition.Approved)
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_not_approved",
                    message: "The transition was not approved by the transition resolver.",
                    transitionId: transition.TransitionId);
            }

            if (transition.Decision == ReferenceCorpusTransitionDecisions.ReplacePiece)
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_piece_replacement_required",
                    message: "The transition resolver requested source replacement; this draft must be regenerated instead of silently replacing a selected piece.",
                    transitionId: transition.TransitionId);
            }
            else if (transition.Decision != ReferenceCorpusTransitionDecisions.DirectJoin &&
                transition.Decision != ReferenceCorpusTransitionDecisions.InsertTransition)
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_decision_unknown",
                    message: "The transition decision is not supported by the draft assembler.",
                    transitionId: transition.TransitionId);
            }

            if (IsUnsafeTransition(transition))
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_text_unsafe",
                    message: "The transition text is outside the allowed minimal transition boundary.",
                    transitionId: transition.TransitionId);
            }

            if (transition.OutputStart < 0 ||
                transition.OutputEnd < transition.OutputStart ||
                transition.OutputEnd > assembledText.Length)
            {
                AddAuditViolation(
                    violations,
                    targetPiece.PieceId,
                    targetPiece.NodeId,
                    spanId: null,
                    code: "transition_output_range_invalid",
                    message: "The transition output range is outside the assembled text.",
                    transitionId: transition.TransitionId);
            }
            else
            {
                var outputText = assembledText[transition.OutputStart..transition.OutputEnd];
                if (!string.Equals(outputText, transition.Text, StringComparison.Ordinal))
                {
                    AddAuditViolation(
                        violations,
                        targetPiece.PieceId,
                        targetPiece.NodeId,
                        spanId: null,
                        code: "transition_output_range_mismatch",
                        message: "The transition output range does not match the transition text.",
                        transitionId: transition.TransitionId);
                }
            }

            foreach (var violation in violations)
            {
                errors.Add($"{violation.Code}:{violation.NodeId}:{transition.TransitionId}");
            }

            var index = auditPieces.FindIndex(piece => string.Equals(piece.PieceId, targetPiece.PieceId, StringComparison.Ordinal));
            if (index >= 0 && violations.Count > 0)
            {
                var auditPiece = auditPieces[index];
                auditPieces[index] = auditPiece with
                {
                    Passed = false,
                    Violations = auditPiece.Violations.Concat(violations).ToArray()
                };
            }

            auditTransitions.Add(new ReferenceCorpusDraftAuditTransitionPayload(
                TransitionId: transition.TransitionId,
                GapId: transition.GapId,
                AfterPieceId: transition.AfterPieceId,
                BeforePieceId: transition.BeforePieceId,
                Decision: transition.Decision,
                Passed: violations.Count == 0,
                Violations: violations));
        }

        var transitionPairsPresent = transitions
            .Select(transition => (transition.AfterPieceId, transition.BeforePieceId))
            .ToHashSet();
        foreach (var gap in expectedGaps)
        {
            if (transitionPairsPresent.Contains((gap.AfterPieceId, gap.BeforePieceId)))
            {
                continue;
            }

            var targetPiece = assembledPieces.First(piece => string.Equals(piece.PieceId, gap.AfterPieceId, StringComparison.Ordinal));
            var transitionId = ReferenceCorpusTransitionGaps.CreateMissingTransitionId(gap.GapId, gap.AfterPieceId, gap.BeforePieceId);
            var violations = new List<ReferenceCorpusDraftAuditViolationPayload>();
            AddAuditViolation(
                violations,
                targetPiece.PieceId,
                targetPiece.NodeId,
                spanId: null,
                code: "missing_transition_decision",
                message: "Every adjacent source-backed piece gap must have an explicit transition decision.",
                transitionId: transitionId);
            foreach (var violation in violations)
            {
                errors.Add($"{violation.Code}:{violation.NodeId}:{transitionId}");
            }

            var auditPieceIndex = auditPieces.FindIndex(piece => string.Equals(piece.PieceId, targetPiece.PieceId, StringComparison.Ordinal));
            if (auditPieceIndex >= 0)
            {
                var auditPiece = auditPieces[auditPieceIndex];
                auditPieces[auditPieceIndex] = auditPiece with
                {
                    Passed = false,
                    Violations = auditPiece.Violations.Concat(violations).ToArray()
                };
            }

            auditTransitions.Add(new ReferenceCorpusDraftAuditTransitionPayload(
                TransitionId: transitionId,
                GapId: gap.GapId,
                AfterPieceId: gap.AfterPieceId,
                BeforePieceId: gap.BeforePieceId,
                Decision: "missing",
                Passed: false,
                Violations: violations));
        }

        return auditTransitions;
    }

    private static IReadOnlyList<ReferenceCorpusTransitionGapPayload> BuildExpectedTransitionGaps(
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> pieces)
    {
        if (pieces.Count < 2)
        {
            return [];
        }

        var gaps = new List<ReferenceCorpusTransitionGapPayload>(pieces.Count - 1);
        for (var index = 0; index + 1 < pieces.Count; index++)
        {
            var current = pieces[index];
            var next = pieces[index + 1];
            gaps.Add(new ReferenceCorpusTransitionGapPayload(
                GapId: ReferenceCorpusTransitionGaps.CreateGapId(current.PieceId, next.PieceId),
                GapIndex: index,
                AfterPieceId: current.PieceId,
                BeforePieceId: next.PieceId,
                AfterBeatId: current.BeatId,
                BeforeBeatId: next.BeatId));
        }

        return gaps;
    }

    private static bool IsUnsafeTransition(ReferenceCorpusTransitionPayload transition)
    {
        if (transition.Decision == ReferenceCorpusTransitionDecisions.DirectJoin)
        {
            return transition.Text.Length != 0;
        }

        if (transition.Decision != ReferenceCorpusTransitionDecisions.InsertTransition)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(transition.Text))
        {
            return true;
        }

        if (transition.Text.Length > 80 ||
            transition.Text.Contains('\n', StringComparison.Ordinal) ||
            transition.Text.Contains('\r', StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string ComposeAssembledText(
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> pieces,
        IReadOnlyList<ReferenceCorpusTransitionPayload> transitions)
    {
        var transitionByPair = transitions
            .GroupBy(transition => (transition.AfterPieceId, transition.BeforePieceId))
            .ToDictionary(group => group.Key, group => group.First());
        var parts = new List<string>();
        for (var i = 0; i < pieces.Count; i++)
        {
            var pieceText = pieces[i].OutputText.Trim();
            if (pieceText.Length > 0)
            {
                parts.Add(pieceText);
            }

            if (i + 1 >= pieces.Count)
            {
                continue;
            }

            if (transitionByPair.TryGetValue((pieces[i].PieceId, pieces[i + 1].PieceId), out var transition))
            {
                var transitionText = transition.Text.Trim();
                if (transitionText.Length > 0)
                {
                    parts.Add(transitionText);
                }
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static void AuditPreservedSpan(
        string sourceText,
        ReferenceCorpusInsertionPiecePayload piece,
        ReferenceCorpusPreservedSpanPayload span,
        List<ReferenceCorpusDraftAuditViolationPayload> violations)
    {
        if (span.SourceStart < 0 ||
            span.SourceEnd <= span.SourceStart ||
            span.SourceEnd > sourceText.Length)
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "preserved_span_source_range_invalid",
                "The preserved span source range is outside the source node.");
            return;
        }

        if (span.OutputStart < 0 ||
            span.OutputEnd <= span.OutputStart ||
            span.OutputEnd > piece.OutputText.Length)
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "preserved_span_output_range_invalid",
                "The preserved span output range is outside the assembled piece.");
            return;
        }

        var sourceHash = StableTextHash(sourceText[span.SourceStart..span.SourceEnd]);
        var outputHash = StableTextHash(piece.OutputText[span.OutputStart..span.OutputEnd]);
        if (!string.Equals(span.SourceTextHash, sourceHash, StringComparison.Ordinal))
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "preserved_span_source_hash_inconsistent",
                "The preserved span source hash does not match its recorded source range.");
        }

        if (!string.Equals(span.OutputTextHash, outputHash, StringComparison.Ordinal))
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "preserved_span_output_hash_inconsistent",
                "The preserved span output hash does not match its recorded output range.");
        }

        if (!span.Matches ||
            !string.Equals(sourceHash, outputHash, StringComparison.Ordinal) ||
            !string.Equals(span.SourceTextHash, span.OutputTextHash, StringComparison.Ordinal))
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "preserved_span_hash_mismatch",
                "A non-slot preserved span changed between source and output.");
        }
    }

    private static void AuditLockedSpan(
        string sourceText,
        ReferenceCorpusInsertionPiecePayload piece,
        ReferenceCorpusLockedSpanPayload span,
        List<ReferenceCorpusDraftAuditViolationPayload> violations)
    {
        if (span.SourceStart < 0 ||
            span.SourceEnd <= span.SourceStart ||
            span.SourceEnd > sourceText.Length ||
            span.OutputStart < 0 ||
            span.OutputEnd <= span.OutputStart ||
            span.OutputEnd > piece.OutputText.Length)
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "locked_span_range_invalid",
                "The locked span range is invalid.");
            return;
        }

        var sourceHash = StableTextHash(sourceText[span.SourceStart..span.SourceEnd]);
        var outputHash = StableTextHash(piece.OutputText[span.OutputStart..span.OutputEnd]);
        if (!string.Equals(span.SourceTextHash, sourceHash, StringComparison.Ordinal))
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "locked_span_source_hash_inconsistent",
                "The locked span source hash does not match its recorded source range.");
        }

        if (!string.Equals(span.OutputTextHash, outputHash, StringComparison.Ordinal))
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "locked_span_output_hash_inconsistent",
                "The locked span output hash does not match its recorded output range.");
        }

        if (!span.Matches ||
            !string.Equals(sourceHash, outputHash, StringComparison.Ordinal) ||
            !string.Equals(span.SourceTextHash, span.OutputTextHash, StringComparison.Ordinal))
        {
            AddAuditViolation(
                violations,
                piece,
                span.SpanId,
                "locked_span_hash_mismatch",
                "A locked source span changed between source and output.");
        }
    }

    private static void AddAuditViolation(
        List<ReferenceCorpusDraftAuditViolationPayload> violations,
        ReferenceCorpusInsertionPiecePayload piece,
        string? spanId,
        string code,
        string message)
    {
        AddAuditViolation(
            violations,
            piece.PieceId,
            piece.NodeId,
            spanId,
            code,
            message,
            transitionId: null);
    }

    private static void AddAuditViolation(
        List<ReferenceCorpusDraftAuditViolationPayload> violations,
        string pieceId,
        string nodeId,
        string? spanId,
        string code,
        string message,
        string? transitionId = null)
    {
        var violationId = "draft-audit-violation-" + StableHash(
            pieceId,
            nodeId,
            spanId ?? string.Empty,
            transitionId ?? string.Empty,
            code,
            violations.Count.ToString(CultureInfo.InvariantCulture))[..16];
        violations.Add(new ReferenceCorpusDraftAuditViolationPayload(
            ViolationId: violationId,
            Code: code,
            Severity: "error",
            PieceId: pieceId,
            NodeId: nodeId,
            SpanId: spanId,
            Message: message,
            TransitionId: transitionId));
    }

    private static ReferenceCorpusSimilarityPolicy BuildSimilarityPolicy(LicensedSourcePiece source)
    {
        if (source.MaxVerbatimRatio is { } ratio)
        {
            var bounded = Math.Clamp(ratio, 0, 1);
            return new ReferenceCorpusSimilarityPolicy(bounded, bounded);
        }

        return source.ReusePolicy == ReferenceCorpusReusePolicies.VerbatimOk
            ? ReferenceCorpusSimilarityPolicy.VerbatimOkDefault
            : ReferenceCorpusSimilarityPolicy.AdaptedOnlyDefault;
    }

    private static string InsertAtOffset(string chapterText, int insertionOffset, string insertionText)
    {
        if (string.IsNullOrWhiteSpace(insertionText))
        {
            return chapterText;
        }

        var offset = Math.Clamp(insertionOffset, 0, chapterText.Length);
        var prefix = chapterText[..offset];
        var suffix = chapterText[offset..];
        var builder = new StringBuilder(prefix);
        if (builder.Length > 0 && !EndsWithLineBreak(builder.ToString()))
        {
            builder.Append(Environment.NewLine);
        }

        builder.Append(insertionText.Trim());
        if (suffix.Length > 0 && !EndsWithLineBreak(builder.ToString()))
        {
            builder.Append(Environment.NewLine);
        }

        builder.Append(suffix);
        return builder.ToString();
    }

    private static bool EndsWithLineBreak(string value)
    {
        return value.EndsWith('\n') || value.EndsWith('\r');
    }

    private static ReferenceCorpusInsertionDraftPayload EmptyResult(
        ReferenceCorpusQueryContextPayload queryContext,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        string chapterText,
        string status)
    {
        return new ReferenceCorpusInsertionDraftPayload(
            queryContext,
            blueprint,
            Pieces: [],
            SlotReplacements: [],
            Transitions: [],
            AssembledText: string.Empty,
            ChapterTextAfterInsertion: chapterText,
            ReadyForInsertion: false,
            Gate: new ReferenceCorpusInsertionGatePayload(
                Passed: false,
                Status: status,
                Errors: [status],
                Pieces: []),
            Audit: new ReferenceCorpusDraftAuditPayload(
                Passed: false,
                Status: status,
                Errors: [status],
                Pieces: [],
                Transitions: []));
    }

    private static void AppendInClause<T>(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        string columnName,
        IReadOnlyCollection<T> values,
        string parameterPrefix)
        where T : notnull
    {
        if (values.Count == 0)
        {
            builder.AppendLine(" AND 1 = 0");
            return;
        }

        var names = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values)
        {
            var name = "$" + parameterPrefix + "_" + index.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add((name, value));
            index++;
        }

        builder.Append(" AND ");
        builder.Append(columnName);
        builder.Append(" IN (");
        builder.Append(string.Join(", ", names));
        builder.AppendLine(")");
    }

    private static string StableHash(params string[] parts)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\u001f', parts));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string StableTextHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record SourceNodeRow(
        string NodeId,
        string Text,
        string TextHash,
        long AnchorId,
        string LicenseState,
        string ReusePolicy,
        double? MaxVerbatimRatio,
        bool ClearedForInsertion);

    private sealed record RequestedPiece(
        ReferenceCorpusInsertionBlueprintBeatPayload Beat,
        string NodeId,
        int SequenceIndex);

    private sealed record BlueprintCandidateFilterResult(
        IReadOnlyList<ReferenceCorpusCandidatePayload> Candidates,
        IReadOnlyList<string> GapReasons);

private sealed record DraftCandidateBlueprintVariant(
        string CandidateId,
        string Strategy,
        string Explanation,
        ReferenceCorpusInsertionBlueprintPayload Blueprint,
        IReadOnlyDictionary<string, string> SlotValues,
 string SlotValueKey,
 string TransitionStrategy = ReferenceCorpusTransitionStrategies.Default);

    private sealed record LicensedSourcePiece(
        string PieceId,
        string BeatId,
        string CandidateId,
        string NodeId,
        long AnchorId,
        string LibraryId,
        string TextHash,
        string LicenseState,
        string ReusePolicy,
        string SourceText,
        bool ClearedForInsertion,
        double? MaxVerbatimRatio,
        IReadOnlySet<string> AllowedTransferSlotNames,
        int SequenceIndex)
    {
        public ReferenceCorpusSourcePiece ToSourcePiece()
        {
            return new ReferenceCorpusSourcePiece(
                PieceId,
                BeatId,
                CandidateId,
                NodeId,
                AnchorId,
                LibraryId,
                TextHash,
                LicenseState,
                ReusePolicy,
                SourceText);
        }
    }

    private sealed class ValueTupleComparer : IEqualityComparer<(string BeatId, string NodeId)>
    {
        public static ValueTupleComparer Instance { get; } = new();

        public bool Equals((string BeatId, string NodeId) x, (string BeatId, string NodeId) y)
        {
            return string.Equals(x.BeatId, y.BeatId, StringComparison.Ordinal) &&
                string.Equals(x.NodeId, y.NodeId, StringComparison.Ordinal);
        }

        public int GetHashCode((string BeatId, string NodeId) obj)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.BeatId),
                StringComparer.Ordinal.GetHashCode(obj.NodeId));
        }
 }

 private static IReadOnlyList<DraftCandidateBlueprintVariant> BuildTransitionStrategyDraftVariants(
 ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
 int requestedCount,
 IReadOnlyDictionary<string, string> slotValues,
 IReadOnlyList<string>? transitionStrategyVariants)
 {
 var strategies = NormalizeTransitionStrategies(transitionStrategyVariants);
 if (strategies.Count == 0)
 {
 return [];
 }

 var primary = BuildPrimarySourceBlueprint(selectedBlueprint);
 var normalizedSlots = NormalizeSlotValues(slotValues);
 var slotKey = SlotValueKey(normalizedSlots);
 return strategies
 .Take(requestedCount)
 .Select((strategy, index) => BuildTransitionStrategyVariant(
 selectedBlueprint,
 primary,
 normalizedSlots,
 slotKey,
 strategy,
 index + 1))
 .ToArray();
 }

 private static IReadOnlyList<DraftCandidateBlueprintVariant> ExpandTransitionStrategyVariants(
 ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
 int requestedCount,
 IReadOnlyList<DraftCandidateBlueprintVariant> slotVariants,
 IReadOnlyList<string>? transitionStrategyVariants)
 {
 var strategies = NormalizeTransitionStrategies(transitionStrategyVariants);
 if (strategies.Count == 0)
 {
 return slotVariants;
 }

 var result = new List<DraftCandidateBlueprintVariant>(requestedCount);
 foreach (var slotVariant in slotVariants)
 {
 foreach (var strategy in strategies)
 {
 if (result.Count >= requestedCount)
 {
 return result;
 }

 result.Add(BuildTransitionStrategyVariant(
 selectedBlueprint,
 slotVariant.Blueprint,
 slotVariant.SlotValues,
 slotVariant.SlotValueKey,
 strategy,
 result.Count + 1));
 }
 }

 return result;
 }

 private static DraftCandidateBlueprintVariant BuildTransitionStrategyVariant(
 ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
 ReferenceCorpusInsertionBlueprintPayload blueprint,
 IReadOnlyDictionary<string, string> slotValues,
 string slotValueKey,
 string transitionStrategy,
 int ordinal)
 {
 var suffix = ordinal.ToString(CultureInfo.InvariantCulture);
 var fingerprint = StableHash(
 selectedBlueprint.BlueprintId,
 BlueprintNodeKey(blueprint),
 slotValueKey,
 transitionStrategy,
 suffix)[..16];
 var variantBlueprint = blueprint with
 {
 BlueprintId = selectedBlueprint.BlueprintId + ":transition-" + fingerprint,
 Strategy = selectedBlueprint.Strategy + ":transition_variant_" + suffix
 };
 return new DraftCandidateBlueprintVariant(
 CandidateId: "corpus-draft-candidate-transition-" + fingerprint,
 Strategy: "transition_variant_" + suffix,
 Explanation: "Uses the same selected blueprint pieces and slot mapping with transition strategy: " + transitionStrategy,
 Blueprint: variantBlueprint,
 SlotValues: slotValues,
 SlotValueKey: slotValueKey,
 TransitionStrategy: transitionStrategy);
 }

 private static IReadOnlyList<string> NormalizeTransitionStrategies(IReadOnlyList<string>? values)
 {
 if (values is null)
 {
 return [];
 }

 return values
 .Select(value => (value ?? string.Empty).Trim().ToLowerInvariant())
 .Where(value => value is ReferenceCorpusTransitionStrategies.Default or ReferenceCorpusTransitionStrategies.DirectJoin)
.Distinct(StringComparer.Ordinal)
.ToArray();
}

}

internal sealed class DeterministicReferenceCorpusQueryContextParser : IReferenceCorpusQueryContextParser
{
    public ValueTask<ReferenceCorpusQueryContextPayload> ParseAsync(
        ReferenceCorpusQueryParsingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var goal = request.NaturalLanguageGoal ?? string.Empty;
        var sceneType = ContainsAny(goal, "门口", "门里", "门外", "门缝", "对峙")
            ? "doorway_confrontation"
            : "scene_continuation";
        var emotionTarget = ContainsAny(goal, "压住", "克制", "怒", "不立刻开口", "没有立刻开口")
            ? "restrained_pressure"
            : "controlled_emotion";
        var pacingTarget = ContainsAny(goal, "慢", "压", "不立刻", "悬")
            ? "slow_tension"
            : "steady";
        var commercial = ContainsAny(goal, "不立刻", "悬念", "钩子", "对峙")
            ? "withheld-answer-hook"
            : "continuity";
        var functions = new HashSet<string>(StringComparer.Ordinal)
        {
            "support_current_chapter"
        };
        if (ContainsAny(goal, "对峙", "压住", "门口"))
        {
            functions.Add("raise_pressure");
        }

        if (ContainsAny(goal, "不立刻开口", "没有立刻开口"))
        {
            functions.Add("withhold_answer");
        }

        return ValueTask.FromResult(new ReferenceCorpusQueryContextPayload(
            SceneType: sceneType,
            EmotionTarget: emotionTarget,
            PacingTarget: pacingTarget,
            NarrativePosition: "current_insertion",
            CommercialMechanic: commercial,
            CharacterStates: request.ChapterContext.CharacterSnapshots
                .Select(snapshot => (snapshot.Character + " " + snapshot.State).Trim())
                .Where(value => value.Length > 0)
                .ToArray(),
            RequiredNarrativeFunctions: functions.ToArray(),
            ChapterContext: request.ChapterContext,
            Scope: request.Scope));
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));
    }
}

internal sealed class SingleBeatReferenceCorpusBlueprintAssembler : IReferenceCorpusBlueprintAssembler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<ReferenceCorpusInsertionBlueprintPayload> AssembleAsync(
        ReferenceCorpusBlueprintAssemblyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selected = SelectCandidate(request.QueryContext, request.Candidates);
        var queryHash = StableHash(JsonSerializer.Serialize(request.QueryContext, JsonOptions));
        if (selected is null)
        {
            return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
                "corpus-blueprint-" + queryHash[..16],
                queryHash,
                "single_beat_m1",
                []));
        }

        var beatId = "corpus-beat-" + StableHash(queryHash, selected.NodeId)[..16];
        return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "corpus-blueprint-" + StableHash(queryHash, selected.NodeId)[..16],
            QueryContextHash: queryHash,
            Strategy: "single_beat_m1",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: beatId,
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: request.QueryContext.RequiredNarrativeFunctions.FirstOrDefault() ?? "support_current_chapter",
                    NodeIds: [selected.NodeId])
            ]));
    }

    private static ReferenceCorpusCandidatePayload? SelectCandidate(
        ReferenceCorpusQueryContextPayload queryContext,
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var preferWithheldAnswer = queryContext.RequiredNarrativeFunctions.Contains("withhold_answer", StringComparer.Ordinal) ||
            queryContext.CommercialMechanic.Contains("withheld", StringComparison.OrdinalIgnoreCase);
        if (preferWithheldAnswer)
        {
            var withheld = candidates.FirstOrDefault(candidate =>
                candidate.TextPreview.Contains("没有立刻开口", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("不立刻开口", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("不开口", StringComparison.Ordinal));
            if (withheld is not null)
            {
                return withheld;
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
            .First();
    }

    private static string StableHash(params string[] parts)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\u001f', parts));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}

internal sealed class HeuristicReferenceCorpusSlotResolver : IReferenceCorpusSlotResolver
{
    private static readonly string[] ProtectedRangePairs = ["《》", "“”", "「」", "『』", "\"\""];
    private static readonly HashSet<string> CharacterPronouns = new(["她", "他"], StringComparer.Ordinal);
    private static readonly HashSet<string> HonorificSlots = new(
        ["师兄", "师姐", "师父", "师妹", "先生", "小姐", "掌柜", "大人", "殿下", "将军", "夫人"],
        StringComparer.Ordinal);
    private static readonly string[] PlaceSuffixes = ["市集", "旧宅", "宅", "城", "宫", "府", "院", "楼", "店", "巷", "村", "镇", "门"];
    private static readonly string[] PlotObjectKeywords = ["钥匙", "门栓", "杯", "剑", "刀", "匕首", "玉佩", "令牌", "账册", "火折子", "匣"];

    public ValueTask<ReferenceCorpusSlotResolutionResult> ResolveAsync(
        ReferenceCorpusSlotResolutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var replacements = new List<ReferenceCorpusSlotReplacementPayload>();
        var protectedRanges = FindProtectedRanges(request.SourceText);
        AddExplicitReplacements(request, protectedRanges, replacements);
        AddPronounReplacement(request, protectedRanges, replacements);

        return ValueTask.FromResult(new ReferenceCorpusSlotResolutionResult(
            NormalizeReplacements(replacements),
            protectedRanges
                .Select(range => new ReferenceCorpusLockedSourceSpan(range.Start, range.End, "quoted_text"))
                .ToArray()));
    }

    private static void AddExplicitReplacements(
        ReferenceCorpusSlotResolutionRequest request,
        IReadOnlyList<(int Start, int End)> protectedRanges,
        List<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        foreach (var pair in request.ExplicitSlotValues)
        {
            var parsed = ParseExplicitSlotKey(pair.Key);
            var sourceValue = parsed.SourceValue;
            var replacement = pair.Value?.Trim();
            if (string.IsNullOrWhiteSpace(sourceValue) || string.IsNullOrWhiteSpace(replacement))
            {
                continue;
            }

            foreach (var index in FindAllOccurrences(request.SourceText, sourceValue))
            {
                if (IntersectsProtectedRange(index, index + sourceValue.Length, protectedRanges))
                {
                    continue;
                }

                replacements.Add(new ReferenceCorpusSlotReplacementPayload(
                    SlotName: parsed.SlotName ?? InferSlotName(sourceValue, replacement),
                    SourceValue: sourceValue,
                    ReplacementValue: replacement,
                    SourceStart: index,
                    SourceEnd: index + sourceValue.Length,
                    OutputStart: 0,
                    OutputEnd: 0));
            }
        }
    }

    private static void AddPronounReplacement(
        ReferenceCorpusSlotResolutionRequest request,
        IReadOnlyList<(int Start, int End)> protectedRanges,
        List<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        var character = request.ChapterContext.CharacterSnapshots
            .Select(snapshot => snapshot.Character.Trim())
            .FirstOrDefault(value => value.Length > 0);
        if (string.IsNullOrWhiteSpace(character))
        {
            return;
        }

        foreach (var pronoun in new[] { "她", "他" })
        {
            var index = request.SourceText.IndexOf(pronoun, StringComparison.Ordinal);
            if (index < 0 ||
                index > 2 ||
                IntersectsProtectedRange(index, index + pronoun.Length, protectedRanges))
            {
                continue;
            }

            replacements.Add(new ReferenceCorpusSlotReplacementPayload(
                SlotName: "character",
                SourceValue: pronoun,
                ReplacementValue: character,
                SourceStart: index,
                SourceEnd: index + pronoun.Length,
                OutputStart: 0,
                OutputEnd: 0));
            return;
        }
    }

    private static (string? SlotName, string SourceValue) ParseExplicitSlotKey(string? key)
    {
        var value = key?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return (null, string.Empty);
        }

        var separatorIndex = value.IndexOfAny([':', '：']);
        if (separatorIndex <= 0 || separatorIndex + 1 >= value.Length)
        {
            return (null, value);
        }

        var prefix = NormalizeSlotName(value[..separatorIndex].Trim());
        var source = value[(separatorIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(prefix) ? (null, value) : (prefix, source);
    }

    private static string? NormalizeSlotName(string value)
    {
        return value switch
        {
            "character" or "person" or "角色" or "人名" => "character",
            "place" or "location" or "地点" or "地名" or "场景" => "place",
            "honorific" or "appellation" or "称谓" or "专属称谓" => "honorific",
            "plot_object" or "prop" or "object" or "道具" or "情节道具" => "plot_object",
            _ => null
        };
    }

    private static string InferSlotName(string sourceValue, string replacementValue)
    {
        if (CharacterPronouns.Contains(sourceValue) ||
            LooksLikeCharacterName(sourceValue) ||
            LooksLikeCharacterName(replacementValue))
        {
            return "character";
        }

        if (HonorificSlots.Contains(sourceValue))
        {
            return "honorific";
        }

        if (PlotObjectKeywords.Any(keyword => sourceValue.Contains(keyword, StringComparison.Ordinal)))
        {
            return "plot_object";
        }

        if (PlaceSuffixes.Any(suffix => sourceValue.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return "place";
        }

        return "explicit";
    }

    private static bool LooksLikeCharacterName(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length is >= 2 and <= 4 &&
            trimmed.All(static ch => ch >= '\u4e00' && ch <= '\u9fff') &&
            !PlaceSuffixes.Any(suffix => trimmed.EndsWith(suffix, StringComparison.Ordinal)) &&
            !PlotObjectKeywords.Any(keyword => trimmed.Contains(keyword, StringComparison.Ordinal)) &&
            !HonorificSlots.Contains(trimmed);
    }

    private static IReadOnlyList<int> FindAllOccurrences(string text, string sourceValue)
    {
        var indexes = new List<int>();
        var index = 0;
        while (index < text.Length)
        {
            var next = text.IndexOf(sourceValue, index, StringComparison.Ordinal);
            if (next < 0)
            {
                break;
            }

            indexes.Add(next);
            index = next + Math.Max(1, sourceValue.Length);
        }

        return indexes;
    }

    private static IReadOnlyList<(int Start, int End)> FindProtectedRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        foreach (var pair in ProtectedRangePairs)
        {
            var open = pair[0];
            var close = pair[1];
            var searchStart = 0;
            while (searchStart < text.Length)
            {
                var start = text.IndexOf(open, searchStart);
                if (start < 0)
                {
                    break;
                }

                var closeStart = close == open ? start + 1 : start + 1;
                var end = text.IndexOf(close, closeStart);
                if (end < 0)
                {
                    ranges.Add((start, text.Length));
                    break;
                }

                ranges.Add((start, end + 1));
                searchStart = end + 1;
            }
        }

        return ranges
            .OrderBy(range => range.Start)
            .ThenBy(range => range.End)
            .ToArray();
    }

    private static bool IntersectsProtectedRange(
        int start,
        int end,
        IReadOnlyList<(int Start, int End)> protectedRanges)
    {
        return protectedRanges.Any(range => start < range.End && end > range.Start);
    }

    private static IReadOnlyList<ReferenceCorpusSlotReplacementPayload> NormalizeReplacements(
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        var result = new List<ReferenceCorpusSlotReplacementPayload>(replacements.Count);
        var seenRanges = new HashSet<string>(StringComparer.Ordinal);
        var lastEnd = 0;
        foreach (var replacement in replacements
            .OrderBy(item => item.SourceStart)
            .ThenByDescending(item => item.SourceEnd)
            .ThenBy(item => item.SlotName, StringComparer.Ordinal))
        {
            if (replacement.SourceStart < lastEnd ||
                replacement.SourceStart < 0 ||
                replacement.SourceEnd <= replacement.SourceStart)
            {
                continue;
            }

            var key = replacement.SourceStart.ToString(CultureInfo.InvariantCulture) + ":" + replacement.SourceEnd.ToString(CultureInfo.InvariantCulture);
            if (!seenRanges.Add(key))
            {
                continue;
            }

            result.Add(replacement);
            lastEnd = replacement.SourceEnd;
        }

        return result;
    }
}

internal sealed class HeuristicReferenceCorpusTransitionResolver : IReferenceCorpusTransitionResolver
{
    public ValueTask<ReferenceCorpusTransitionResolutionResult> ResolveAsync(
        ReferenceCorpusTransitionResolutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Pieces.Count < 2)
        {
            return ValueTask.FromResult(new ReferenceCorpusTransitionResolutionResult([]));
        }

        var piecesById = request.Pieces.ToDictionary(piece => piece.PieceId, StringComparer.Ordinal);
        var beatsById = request.Blueprint.Beats.ToDictionary(beat => beat.BeatId, StringComparer.Ordinal);
        var transitions = new List<ReferenceCorpusTransitionPayload>(request.Gaps.Count);
        foreach (var gap in request.Gaps)
        {
            if (!piecesById.TryGetValue(gap.AfterPieceId, out var afterPiece) ||
                !piecesById.TryGetValue(gap.BeforePieceId, out var beforePiece))
            {
                continue;
            }

            var afterBeat = beatsById.GetValueOrDefault(afterPiece.BeatId);
            var beforeBeat = beatsById.GetValueOrDefault(beforePiece.BeatId);
            if (ShouldReplaceDuplicateAdjacentPiece(afterPiece, beforePiece))
            {
                transitions.Add(new ReferenceCorpusTransitionPayload(
                    TransitionId: ReferenceCorpusTransitionGaps.CreateReplaceTransitionId(gap.GapId, gap.AfterPieceId, gap.BeforePieceId),
                    GapId: gap.GapId,
                    AfterPieceId: gap.AfterPieceId,
                    BeforePieceId: gap.BeforePieceId,
                    Decision: ReferenceCorpusTransitionDecisions.ReplacePiece,
                    Strategy: "replace_piece",
                    Text: string.Empty,
                    TextHash: ReferenceCorpusTransitionGaps.CreateTextHash(string.Empty),
                    OutputStart: 0,
                    OutputEnd: 0,
                    Approved: false,
                    Reason: "duplicate adjacent source piece must be regenerated instead of joined",
                    ReplacementPieceId: beforePiece.PieceId,
                    ReplacementNodeId: null));
                continue;
            }

            if (ShouldInsertBridge(afterBeat, beforeBeat))
            {
                var text = BuildBridgeText(afterBeat, beforeBeat);
                transitions.Add(new ReferenceCorpusTransitionPayload(
                    TransitionId: ReferenceCorpusTransitionGaps.CreateInsertTransitionId(gap.GapId, gap.AfterPieceId, gap.BeforePieceId, text),
                    GapId: gap.GapId,
                    AfterPieceId: gap.AfterPieceId,
                    BeforePieceId: gap.BeforePieceId,
                    Decision: ReferenceCorpusTransitionDecisions.InsertTransition,
                    Strategy: "heuristic_bridge_sentence",
                    Text: text,
                    TextHash: ReferenceCorpusTransitionGaps.CreateTextHash(text),
                    OutputStart: 0,
                    OutputEnd: 0,
                    Approved: true,
                    Reason: "bridge pressure beat into withheld-answer beat"));
                continue;
            }

 transitions.Add(CreateDirectJoin(gap));
        }

        return ValueTask.FromResult(new ReferenceCorpusTransitionResolutionResult(transitions));
    }

    private static bool ShouldReplaceDuplicateAdjacentPiece(
        ReferenceCorpusInsertionPiecePayload afterPiece,
        ReferenceCorpusInsertionPiecePayload beforePiece)
    {
        return string.Equals(afterPiece.NodeId, beforePiece.NodeId, StringComparison.Ordinal) ||
            string.Equals(afterPiece.SourceTextHash, beforePiece.SourceTextHash, StringComparison.Ordinal) ||
            string.Equals(afterPiece.OutputText.Trim(), beforePiece.OutputText.Trim(), StringComparison.Ordinal);
    }

    private static bool ShouldInsertBridge(
        ReferenceCorpusInsertionBlueprintBeatPayload? afterBeat,
        ReferenceCorpusInsertionBlueprintBeatPayload? beforeBeat)
    {
        if (afterBeat is null || beforeBeat is null)
        {
            return false;
        }

        return string.Equals(afterBeat.NarrativeFunction, "raise_pressure", StringComparison.Ordinal) &&
            string.Equals(beforeBeat.NarrativeFunction, "withhold_answer", StringComparison.Ordinal);
    }

    private static string BuildBridgeText(
        ReferenceCorpusInsertionBlueprintBeatPayload? afterBeat,
        ReferenceCorpusInsertionBlueprintBeatPayload? beforeBeat)
    {
        if (string.Equals(afterBeat?.NarrativeFunction, "raise_pressure", StringComparison.Ordinal) &&
            string.Equals(beforeBeat?.NarrativeFunction, "withhold_answer", StringComparison.Ordinal))
        {
            return "沉默在两人之间又压低了一寸。";
        }

        return "两段情绪在这里短暂合拢。";
    }

 internal static ReferenceCorpusTransitionPayload CreateDirectJoin(ReferenceCorpusTransitionGapPayload gap)
    {
        return new ReferenceCorpusTransitionPayload(
            TransitionId: ReferenceCorpusTransitionGaps.CreateDirectTransitionId(gap.GapId, gap.AfterPieceId, gap.BeforePieceId),
            GapId: gap.GapId,
            AfterPieceId: gap.AfterPieceId,
            BeforePieceId: gap.BeforePieceId,
            Decision: ReferenceCorpusTransitionDecisions.DirectJoin,
            Strategy: "direct_join",
            Text: string.Empty,
            TextHash: ReferenceCorpusTransitionGaps.CreateTextHash(string.Empty),
            OutputStart: 0,
            OutputEnd: 0,
            Approved: true,
            Reason: "direct join between selected blueprint pieces");
    }
}

internal static class ReferenceCorpusTransitionGaps
{
    public static string CreateGapId(string afterPieceId, string beforePieceId)
    {
        return "transition-gap-" + StableHash(afterPieceId, beforePieceId)[..16];
    }

    public static string CreateDirectTransitionId(string gapId, string afterPieceId, string beforePieceId)
    {
        return "transition-direct-" + StableHash(gapId, afterPieceId, beforePieceId)[..16];
    }

    public static string CreateInsertTransitionId(string gapId, string afterPieceId, string beforePieceId, string text)
    {
        return "transition-insert-" + StableHash(gapId, afterPieceId, beforePieceId, text)[..16];
    }

    public static string CreateReplaceTransitionId(string gapId, string afterPieceId, string beforePieceId)
    {
        return "transition-replace-" + StableHash(gapId, afterPieceId, beforePieceId)[..16];
    }

    public static string CreateMissingTransitionId(string gapId, string afterPieceId, string beforePieceId)
    {
        return "transition-missing-" + StableHash(gapId, afterPieceId, beforePieceId)[..16];
    }

    public static string CreateTextHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string StableHash(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder
                .Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

internal sealed class PreservingReferenceCorpusTextAssembler : IReferenceCorpusTextAssembler
{
    private readonly IReferenceCorpusSlotResolver _slots;
    private readonly IReferenceCorpusTransitionResolver _transitions;

    public PreservingReferenceCorpusTextAssembler(
        IReferenceCorpusSlotResolver slots,
        IReferenceCorpusTransitionResolver transitions)
    {
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
    }

    public async ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
        ReferenceCorpusTextAssemblyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var pieces = new List<ReferenceCorpusInsertionPiecePayload>(request.SourcePieces.Count);
        var allReplacements = new List<ReferenceCorpusSlotReplacementPayload>();
        foreach (var source in request.SourcePieces)
        {
            var resolved = await _slots.ResolveAsync(
                new ReferenceCorpusSlotResolutionRequest(
                    source.SourceText,
                    request.ChapterContext,
                    request.ExplicitSlotValues),
                cancellationToken);
            var applied = ApplyReplacements(source.SourceText, resolved.Replacements);
            var preservedSource = RemoveSourceSpans(source.SourceText, resolved.Replacements);
            var preservedOutput = RemoveOutputSpans(applied.OutputText, applied.Replacements);
            var preservedHash = StableHash(preservedOutput);
            var preservedMatches = string.Equals(
                StableHash(preservedSource),
                preservedHash,
                StringComparison.Ordinal);
            var preservedSpans = BuildPreservedSpans(source.PieceId, source.SourceText, applied.OutputText, applied.Replacements);
            var lockedSpans = BuildLockedSpans(source.PieceId, source.SourceText, applied.OutputText, applied.Replacements, resolved.LockedSpans);
            var payload = new ReferenceCorpusInsertionPiecePayload(
                PieceId: source.PieceId,
                BeatId: source.BeatId,
                CandidateId: source.CandidateId,
                NodeId: source.NodeId,
                AnchorId: source.AnchorId,
                LibraryId: source.LibraryId,
                SourceTextHash: source.TextHash,
                ReusePolicy: source.ReusePolicy,
                LicenseState: source.LicenseState,
                OutputText: applied.OutputText,
                PreservedTextHash: preservedHash,
                PreservedHashMatches: preservedMatches,
                PreservedSpans: preservedSpans,
                LockedSpans: lockedSpans,
                SlotReplacements: applied.Replacements);
            pieces.Add(payload);
            allReplacements.AddRange(applied.Replacements);
        }

 var gaps = BuildTransitionGaps(pieces);
 var resolvedTransitions = string.Equals(
 request.TransitionStrategy,
 ReferenceCorpusTransitionStrategies.DirectJoin,
 StringComparison.Ordinal)
 ? new ReferenceCorpusTransitionResolutionResult(
 gaps.Select(HeuristicReferenceCorpusTransitionResolver.CreateDirectJoin).ToArray())
 : await _transitions.ResolveAsync(
 new ReferenceCorpusTransitionResolutionRequest(
 request.Blueprint,
 pieces,
 gaps,
 request.ChapterContext),
 cancellationToken);
        var composed = ComposeAssembledText(pieces, resolvedTransitions.Transitions);

        return new ReferenceCorpusTextAssemblyResult(
            pieces,
            allReplacements,
            composed.Transitions,
            composed.Text);
    }

    private static ReferenceCorpusComposedText ComposeAssembledText(
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> pieces,
        IReadOnlyList<ReferenceCorpusTransitionPayload> transitions)
    {
        var transitionByPair = transitions
            .GroupBy(transition => (transition.AfterPieceId, transition.BeforePieceId))
            .ToDictionary(group => group.Key, group => group.First());
        var resolvedTransitions = new List<ReferenceCorpusTransitionPayload>(transitions.Count);
        var builder = new StringBuilder();
        var usedTransitionIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < pieces.Count; i++)
        {
            var pieceText = pieces[i].OutputText.Trim();
            if (pieceText.Length > 0)
            {
                AppendPart(builder, pieceText);
            }

            if (i + 1 >= pieces.Count)
            {
                continue;
            }

            if (transitionByPair.TryGetValue((pieces[i].PieceId, pieces[i + 1].PieceId), out var transition))
            {
                var transitionText = transition.Text.Trim();
                var outputStart = builder.Length;
                if (transitionText.Length > 0)
                {
                    AppendPart(builder, transitionText);
                    outputStart = builder.Length - transitionText.Length;
                }

                resolvedTransitions.Add(transition with
                {
                    Text = transitionText,
                    TextHash = StableHash(transitionText),
                    OutputStart = outputStart,
                    OutputEnd = outputStart + transitionText.Length
                });
                usedTransitionIds.Add(transition.TransitionId);
            }
        }

        foreach (var transition in transitions)
        {
            if (usedTransitionIds.Contains(transition.TransitionId))
            {
                continue;
            }

            var transitionText = transition.Text.Trim();
            resolvedTransitions.Add(transition with
            {
                Text = transitionText,
                TextHash = StableHash(transitionText)
            });
        }

        return new ReferenceCorpusComposedText(builder.ToString(), resolvedTransitions);
    }

    private static IReadOnlyList<ReferenceCorpusTransitionGapPayload> BuildTransitionGaps(
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> pieces)
    {
        if (pieces.Count < 2)
        {
            return [];
        }

        var gaps = new List<ReferenceCorpusTransitionGapPayload>(pieces.Count - 1);
        for (var i = 0; i + 1 < pieces.Count; i++)
        {
            var current = pieces[i];
            var next = pieces[i + 1];
            gaps.Add(new ReferenceCorpusTransitionGapPayload(
                GapId: ReferenceCorpusTransitionGaps.CreateGapId(current.PieceId, next.PieceId),
                GapIndex: i,
                AfterPieceId: current.PieceId,
                BeforePieceId: next.PieceId,
                AfterBeatId: current.BeatId,
                BeforeBeatId: next.BeatId));
        }

        return gaps;
    }

    private static void AppendPart(StringBuilder builder, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append(Environment.NewLine);
        }

        builder.Append(value);
    }

    private static AppliedReplacementResult ApplyReplacements(
        string sourceText,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        if (replacements.Count == 0)
        {
            return new AppliedReplacementResult(sourceText, []);
        }

        var builder = new StringBuilder(sourceText.Length);
        var applied = new List<ReferenceCorpusSlotReplacementPayload>(replacements.Count);
        var cursor = 0;
        foreach (var replacement in replacements.OrderBy(item => item.SourceStart))
        {
            if (replacement.SourceStart < cursor ||
                replacement.SourceEnd > sourceText.Length)
            {
                continue;
            }

            builder.Append(sourceText, cursor, replacement.SourceStart - cursor);
            var outputStart = builder.Length;
            builder.Append(replacement.ReplacementValue);
            var outputEnd = builder.Length;
            applied.Add(replacement with
            {
                OutputStart = outputStart,
                OutputEnd = outputEnd
            });
            cursor = replacement.SourceEnd;
        }

        builder.Append(sourceText, cursor, sourceText.Length - cursor);
        return new AppliedReplacementResult(builder.ToString(), applied);
    }

    private static string RemoveSourceSpans(
        string text,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        return RemoveSpans(text, replacements.Select(item => (item.SourceStart, item.SourceEnd)).ToArray());
    }

    private static string RemoveOutputSpans(
        string text,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        return RemoveSpans(text, replacements.Select(item => (item.OutputStart, item.OutputEnd)).ToArray());
    }

    private static string RemoveSpans(string text, IReadOnlyList<(int Start, int End)> spans)
    {
        if (spans.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var span in spans.OrderBy(item => item.Start))
        {
            if (span.Start < cursor || span.End > text.Length || span.End <= span.Start)
            {
                continue;
            }

            builder.Append(text, cursor, span.Start - cursor);
            cursor = span.End;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static IReadOnlyList<ReferenceCorpusPreservedSpanPayload> BuildPreservedSpans(
        string pieceId,
        string sourceText,
        string outputText,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        var spans = new List<ReferenceCorpusPreservedSpanPayload>();
        var sourceCursor = 0;
        var outputCursor = 0;
        foreach (var replacement in replacements.OrderBy(item => item.SourceStart))
        {
            if (replacement.SourceStart < sourceCursor ||
                replacement.OutputStart < outputCursor ||
                replacement.SourceEnd > sourceText.Length ||
                replacement.OutputEnd > outputText.Length)
            {
                continue;
            }

            AddPreservedSpan(
                spans,
                pieceId,
                sourceText,
                outputText,
                sourceCursor,
                replacement.SourceStart,
                outputCursor,
                replacement.OutputStart);
            sourceCursor = replacement.SourceEnd;
            outputCursor = replacement.OutputEnd;
        }

        AddPreservedSpan(
            spans,
            pieceId,
            sourceText,
            outputText,
            sourceCursor,
            sourceText.Length,
            outputCursor,
            outputText.Length);
        return spans;
    }

    private static IReadOnlyList<ReferenceCorpusLockedSpanPayload> BuildLockedSpans(
        string pieceId,
        string sourceText,
        string outputText,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements,
        IReadOnlyList<ReferenceCorpusLockedSourceSpan> lockedSpans)
    {
        if (lockedSpans.Count == 0)
        {
            return [];
        }

        var result = new List<ReferenceCorpusLockedSpanPayload>(lockedSpans.Count);
        foreach (var locked in lockedSpans
            .Where(span => span.SourceStart >= 0 && span.SourceEnd > span.SourceStart && span.SourceEnd <= sourceText.Length)
            .OrderBy(span => span.SourceStart)
            .ThenBy(span => span.SourceEnd))
        {
            var outputStart = TranslateSourceOffsetToOutput(locked.SourceStart, replacements);
            var outputEnd = TranslateSourceOffsetToOutput(locked.SourceEnd, replacements);
            outputStart = Math.Clamp(outputStart, 0, outputText.Length);
            outputEnd = Math.Clamp(outputEnd, outputStart, outputText.Length);
            var sourceSegment = sourceText[locked.SourceStart..locked.SourceEnd];
            var outputSegment = outputText[outputStart..outputEnd];
            var sourceHash = StableHash(sourceSegment);
            var outputHash = StableHash(outputSegment);
            result.Add(new ReferenceCorpusLockedSpanPayload(
                SpanId: "locked-span-" + StableHash(
                    pieceId,
                    locked.SourceStart.ToString(CultureInfo.InvariantCulture),
                    locked.SourceEnd.ToString(CultureInfo.InvariantCulture),
                    locked.Reason)[..16],
                SourceStart: locked.SourceStart,
                SourceEnd: locked.SourceEnd,
                OutputStart: outputStart,
                OutputEnd: outputEnd,
                SourceTextHash: sourceHash,
                OutputTextHash: outputHash,
                Matches: string.Equals(sourceHash, outputHash, StringComparison.Ordinal),
                Reason: locked.Reason));
        }

        return result;
    }

    private static int TranslateSourceOffsetToOutput(
        int sourceOffset,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        var delta = 0;
        foreach (var replacement in replacements.OrderBy(item => item.SourceStart))
        {
            if (sourceOffset < replacement.SourceStart)
            {
                break;
            }

            if (sourceOffset == replacement.SourceStart)
            {
                return replacement.OutputStart;
            }

            if (sourceOffset <= replacement.SourceEnd)
            {
                return replacement.OutputEnd;
            }

            delta += (replacement.OutputEnd - replacement.OutputStart) -
                (replacement.SourceEnd - replacement.SourceStart);
        }

        return sourceOffset + delta;
    }

    private static void AddPreservedSpan(
        List<ReferenceCorpusPreservedSpanPayload> spans,
        string pieceId,
        string sourceText,
        string outputText,
        int sourceStart,
        int sourceEnd,
        int outputStart,
        int outputEnd)
    {
        if (sourceEnd <= sourceStart && outputEnd <= outputStart)
        {
            return;
        }

        var sourceSegment = sourceText[sourceStart..sourceEnd];
        var outputSegment = outputText[outputStart..outputEnd];
        var sourceHash = StableHash(sourceSegment);
        var outputHash = StableHash(outputSegment);
        var spanId = "preserved-span-" + StableHash(
            pieceId,
            sourceStart.ToString(CultureInfo.InvariantCulture),
            sourceEnd.ToString(CultureInfo.InvariantCulture),
            outputStart.ToString(CultureInfo.InvariantCulture),
            outputEnd.ToString(CultureInfo.InvariantCulture))[..16];
        spans.Add(new ReferenceCorpusPreservedSpanPayload(
            SpanId: spanId,
            SourceStart: sourceStart,
            SourceEnd: sourceEnd,
            OutputStart: outputStart,
            OutputEnd: outputEnd,
            SourceTextHash: sourceHash,
            OutputTextHash: outputHash,
            Matches: string.Equals(sourceHash, outputHash, StringComparison.Ordinal)));
    }

    private static string StableHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string StableHash(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (var value in values)
        {
            builder
                .Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(value);
        }

        return StableHash(builder.ToString());
    }

    private sealed record AppliedReplacementResult(
        string OutputText,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> Replacements);

    private sealed record ReferenceCorpusComposedText(
        string Text,
        IReadOnlyList<ReferenceCorpusTransitionPayload> Transitions);
}
