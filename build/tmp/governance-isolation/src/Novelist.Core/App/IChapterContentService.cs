using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IChapterContentService
{
    ValueTask<IReadOnlyList<ChapterPayload>> GetChaptersAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<int> GetMaxChapterNumberAsync(long novelId, CancellationToken cancellationToken);

    ValueTask<ChapterPayload> CreateChapterAsync(CreateChapterPayload input, CancellationToken cancellationToken);

    ValueTask UpdateChapterTitleAsync(long novelId, int chapterNumber, string title, CancellationToken cancellationToken);

    ValueTask<string> GetContentAsync(long novelId, string path, CancellationToken cancellationToken);

    ValueTask SaveContentAsync(SaveContentPayload input, CancellationToken cancellationToken);
}
