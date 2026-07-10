using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceCorpusWritingService
{
    ValueTask<ReferenceCorpusBlueprintCandidatesPayload> GenerateBlueprintCandidatesAsync(
        GenerateReferenceCorpusBlueprintCandidatesPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceCorpusInsertionDraftPayload> GenerateInsertionDraftAsync(
        GenerateReferenceCorpusInsertionDraftPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceCorpusInsertionDraftCandidatesPayload> GenerateInsertionDraftCandidatesAsync(
        GenerateReferenceCorpusInsertionDraftCandidatesPayload input,
        CancellationToken cancellationToken);
}

public interface IReferenceCorpusQueryContextParser
{
    ValueTask<ReferenceCorpusQueryContextPayload> ParseAsync(
        ReferenceCorpusQueryParsingRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceCorpusBlueprintAssembler
{
    ValueTask<ReferenceCorpusInsertionBlueprintPayload> AssembleAsync(
        ReferenceCorpusBlueprintAssemblyRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceCorpusBlueprintCandidateAssembler
{
    ValueTask<IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload>> AssembleCandidatesAsync(
        ReferenceCorpusBlueprintCandidateAssemblyRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceCorpusSlotResolver
{
    ValueTask<ReferenceCorpusSlotResolutionResult> ResolveAsync(
        ReferenceCorpusSlotResolutionRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceCorpusTextAssembler
{
    ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
        ReferenceCorpusTextAssemblyRequest request,
        CancellationToken cancellationToken);
}

public interface IReferenceCorpusTransitionResolver
{
    ValueTask<ReferenceCorpusTransitionResolutionResult> ResolveAsync(
        ReferenceCorpusTransitionResolutionRequest request,
        CancellationToken cancellationToken);
}

public sealed record ReferenceCorpusQueryParsingRequest(
    string NaturalLanguageGoal,
    CurrentChapterContextPayload ChapterContext,
    ReferenceCorpusScopePayload Scope);

public sealed record ReferenceCorpusBlueprintAssemblyRequest(
    ReferenceCorpusQueryContextPayload QueryContext,
    IReadOnlyList<ReferenceCorpusCandidatePayload> Candidates);

public sealed record ReferenceCorpusBlueprintCandidateAssemblyRequest(
    ReferenceCorpusQueryContextPayload QueryContext,
    IReadOnlyList<ReferenceCorpusCandidatePayload> Candidates,
    int RequestedCount,
    ReferenceCorpusBlueprintFeedbackPayload? Feedback,
    IReadOnlyList<string> DiagnosticGapReasons,
    string FeedbackReason,
    ReferenceCorpusHistoricalFeedbackProfile HistoricalFeedback);

public sealed record ReferenceCorpusHistoricalFeedbackProfile(
    IReadOnlySet<string> NodeHashes,
    IReadOnlySet<string> LibraryHashes,
    IReadOnlySet<long> AnchorIds)
{
    public static ReferenceCorpusHistoricalFeedbackProfile Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<long>());

    public bool IsEmpty => NodeHashes.Count == 0 && LibraryHashes.Count == 0 && AnchorIds.Count == 0;
}

public sealed record ReferenceCorpusSlotResolutionRequest(
    string SourceText,
    CurrentChapterContextPayload ChapterContext,
    IReadOnlyDictionary<string, string> ExplicitSlotValues);

public sealed record ReferenceCorpusSlotResolutionResult(
    IReadOnlyList<ReferenceCorpusSlotReplacementPayload> Replacements,
    IReadOnlyList<ReferenceCorpusLockedSourceSpan> LockedSpans);

public sealed record ReferenceCorpusLockedSourceSpan(
    int SourceStart,
    int SourceEnd,
    string Reason);

public sealed record ReferenceCorpusTextAssemblyRequest(
    ReferenceCorpusInsertionBlueprintPayload Blueprint,
    IReadOnlyList<ReferenceCorpusSourcePiece> SourcePieces,
    CurrentChapterContextPayload ChapterContext,
    IReadOnlyDictionary<string, string> ExplicitSlotValues);

public sealed record ReferenceCorpusTextAssemblyResult(
    IReadOnlyList<ReferenceCorpusInsertionPiecePayload> Pieces,
    IReadOnlyList<ReferenceCorpusSlotReplacementPayload> SlotReplacements,
    IReadOnlyList<ReferenceCorpusTransitionPayload> Transitions,
    string AssembledText);

public sealed record ReferenceCorpusTransitionResolutionRequest(
    ReferenceCorpusInsertionBlueprintPayload Blueprint,
    IReadOnlyList<ReferenceCorpusInsertionPiecePayload> Pieces,
    IReadOnlyList<ReferenceCorpusTransitionGapPayload> Gaps,
    CurrentChapterContextPayload ChapterContext);

public sealed record ReferenceCorpusTransitionGapPayload(
    string GapId,
    int GapIndex,
    string AfterPieceId,
    string BeforePieceId,
    string AfterBeatId,
    string BeforeBeatId);

public sealed record ReferenceCorpusTransitionResolutionResult(
    IReadOnlyList<ReferenceCorpusTransitionPayload> Transitions);

public sealed record ReferenceCorpusSourcePiece(
    string PieceId,
    string BeatId,
    string CandidateId,
    string NodeId,
    long AnchorId,
    string LibraryId,
    string TextHash,
    string LicenseState,
    string ReusePolicy,
    string SourceText);
