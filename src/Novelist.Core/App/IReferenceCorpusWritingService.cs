using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IReferenceCorpusWritingService
{
    ValueTask<ReferenceCorpusInsertionDraftPayload> GenerateInsertionDraftAsync(
        GenerateReferenceCorpusInsertionDraftPayload input,
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

public sealed record ReferenceCorpusQueryParsingRequest(
    string NaturalLanguageGoal,
    CurrentChapterContextPayload ChapterContext,
    ReferenceCorpusScopePayload Scope);

public sealed record ReferenceCorpusBlueprintAssemblyRequest(
    ReferenceCorpusQueryContextPayload QueryContext,
    IReadOnlyList<ReferenceCorpusCandidatePayload> Candidates);

public sealed record ReferenceCorpusSlotResolutionRequest(
    string SourceText,
    CurrentChapterContextPayload ChapterContext,
    IReadOnlyDictionary<string, string> ExplicitSlotValues);

public sealed record ReferenceCorpusSlotResolutionResult(
    IReadOnlyList<ReferenceCorpusSlotReplacementPayload> Replacements);

public sealed record ReferenceCorpusTextAssemblyRequest(
    ReferenceCorpusInsertionBlueprintPayload Blueprint,
    IReadOnlyList<ReferenceCorpusSourcePiece> SourcePieces,
    CurrentChapterContextPayload ChapterContext,
    IReadOnlyDictionary<string, string> ExplicitSlotValues);

public sealed record ReferenceCorpusTextAssemblyResult(
    IReadOnlyList<ReferenceCorpusInsertionPiecePayload> Pieces,
    IReadOnlyList<ReferenceCorpusSlotReplacementPayload> SlotReplacements,
    string AssembledText);

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
