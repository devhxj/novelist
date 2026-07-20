namespace Novelist.Core.App;

public interface IReferenceMaterialSearch
{
    ValueTask<ReferenceMaterialListPage> ListAsync(
        ReferenceMaterialListRequest input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchAsync(
        ReferenceMaterialSearchRequest input,
        CancellationToken cancellationToken);
}

public sealed record ReferenceMaterialListRequest(
    long NovelId,
    long AnchorId,
    int Page,
    int Size);

public sealed record ReferenceMaterialListPage(
    IReadOnlyList<ReferenceMaterialListItem> Items,
    long Total,
    int Page,
    int Size,
    int TotalPages);

public sealed record ReferenceMaterialListItem(
    string MaterialId,
    string GenerationId,
    long AnchorId,
    int ChapterIndex,
    int Ordinal,
    string MaterialType,
    string Text,
    string Description,
    IReadOnlyList<string> Tags,
    string TextHash);

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
