using Novelist.Core.App;

namespace Novelist.Core.App;

public interface IReferenceChapterMaterialExtractor
{
    ValueTask<ReferenceChapterExtractionResult> ExtractChapterMaterialsAsync(
        ReferenceChapterExtractionRequest request,
        CancellationToken cancellationToken);
}

public sealed record ReferenceChapterExtractionRequest(
    long AnchorId,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterText,
    ReferenceMaterializationLlmSelection Model);

public sealed record ReferenceChapterExtractedMaterial(
    string Excerpt,
    string MaterialType,
    ReferenceMaterializationQualificationTags Tags,
    ReferenceMaterializationQualityScores Scores,
    double Confidence,
    IReadOnlyList<string> ReasonCodes);

public sealed record ReferenceChapterExtractionResult(
    IReadOnlyList<ReferenceChapterExtractedMaterial> Materials,
    int ModelCallCount = 1);

public sealed record ReferenceChapterExtractionWorkItem(
    long AnchorId,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterText,
    int ContentStart,
    int ContentEnd,
    ReferenceMaterializationLlmSelection Model);

public sealed record ReferenceChapterExtractionPersistenceResult(
    int ChapterIndex,
    int CandidateCount,
    int AcceptedCount,
    int ReviewCount,
    int SkippedCount);
