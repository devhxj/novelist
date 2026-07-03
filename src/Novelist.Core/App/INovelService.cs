using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface INovelService
{
    ValueTask<IReadOnlyList<NovelPayload>> GetNovelsAsync(CancellationToken cancellationToken);

    ValueTask<NovelPayload> CreateNovelAsync(CreateNovelPayload input, CancellationToken cancellationToken);

    ValueTask<NovelPayload> UpdateNovelAsync(long novelId, UpdateNovelPayload input, CancellationToken cancellationToken);

    ValueTask DeleteNovelAsync(long novelId, CancellationToken cancellationToken);

    ValueTask SetActiveNovelAsync(long novelId, CancellationToken cancellationToken);
}
