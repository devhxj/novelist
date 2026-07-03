using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IAppSettingsService
{
    ValueTask<AppSettingsPayload> GetSettingsAsync(CancellationToken cancellationToken);

    ValueTask SaveSettingsAsync(CancellationToken cancellationToken);

    ValueTask SetSelectedModelAsync(string selectedModelKey, string reasoningEffort, CancellationToken cancellationToken);

    ValueTask SetReasoningEffortAsync(string reasoningEffort, CancellationToken cancellationToken);

    ValueTask SetChatPanelWidthAsync(int width, CancellationToken cancellationToken);

    ValueTask SetLastSessionAsync(string sessionId, CancellationToken cancellationToken);

    ValueTask SetLastNovelAsync(long novelId, CancellationToken cancellationToken);

    ValueTask SetApprovalModeAsync(string mode, CancellationToken cancellationToken);

    ValueTask SaveUserNameAsync(string name, CancellationToken cancellationToken);

    ValueTask SaveAvatarAsync(byte[] data, CancellationToken cancellationToken);
}
