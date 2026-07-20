namespace Novelist.Core.App;

public interface IReferenceMaterialSearch
{
    ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchAsync(
        ReferenceMaterialSearchRequest input,
        CancellationToken cancellationToken);
}

public sealed record ReferenceMaterialSearchRequest(
    string Query,
    int MaxResults,
    long? NovelId = null,
    string? SessionId = null,
    IReadOnlyList<string>? LibraryIds = null,
    IReadOnlyList<long>? AnchorIds = null);

public sealed record ReferenceMaterialSearchHit(
    string MaterialId,
    string GenerationId,
    long AnchorId,
    int ChapterIndex,
    int Ordinal,
    string MaterialType,
    string Text,
    string Description,
    IReadOnlyList<string> Tags,
    string TextHash,
    double VectorDistance);
