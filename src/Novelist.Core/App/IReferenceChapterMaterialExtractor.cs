namespace Novelist.Core.App;

public interface IReferenceChapterMaterialExtractor
{
    ValueTask<ReferenceChapterMaterialExtractionResult> ExtractAsync(
        ReferenceChapterMaterialExtractionRequest input,
        CancellationToken cancellationToken);
}

public sealed record ReferenceMaterializationLlmSelection(
    string ProviderName,
    string ModelId,
    string ReasoningEffort);

public sealed record ReferenceChapterMaterialExtractionRequest(
    ReferenceMaterializationLlmSelection Model,
    long AnchorId,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterText);

public sealed record ReferenceChapterMaterialExtractionResult(
    IReadOnlyList<ExtractedReferenceMaterial> Materials);

public sealed record ExtractedReferenceMaterial(
    string Text,
    ReferenceMaterialMetadata Metadata);
