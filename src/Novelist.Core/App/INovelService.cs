using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface INovelService
{
    ValueTask<IReadOnlyList<NovelPayload>> GetNovelsAsync(CancellationToken cancellationToken);

    ValueTask<NovelPayload> CreateNovelAsync(CreateNovelPayload input, CancellationToken cancellationToken);

    ValueTask<NovelPayload> UpdateNovelAsync(long novelId, UpdateNovelPayload input, CancellationToken cancellationToken);

    ValueTask DeleteNovelAsync(long novelId, CancellationToken cancellationToken);

    ValueTask SetActiveNovelAsync(long novelId, CancellationToken cancellationToken);

    ValueTask SaveCoverAsync(long novelId, IReadOnlyList<byte> data, CancellationToken cancellationToken);

    ValueTask DeleteCoverAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<NovelCoverFile?> GetCoverAsync(long novelId, CancellationToken cancellationToken);
}

public sealed record NovelCoverFile(
    string LocalPath,
    string ContentType,
    long Length,
    DateTimeOffset LastModified);

public static class NovelCoverConstraints
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxBridgeBytes = 2 * 1024 * 1024;
}
