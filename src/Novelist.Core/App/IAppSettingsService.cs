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

public interface IPhase15AppSettingsService : IAppSettingsService
{
    ValueTask<GitAuthorSettingsPayload> GetGitAuthorSettingsAsync(CancellationToken cancellationToken);

    ValueTask<GitAuthorSettingsPayload> SaveGitAuthorSettingsAsync(
        SaveGitAuthorSettingsPayload input,
        CancellationToken cancellationToken);

    ValueTask<UpdateCheckSettingsPayload> GetUpdateCheckSettingsAsync(CancellationToken cancellationToken);

    ValueTask<UpdateCheckSettingsPayload> SaveUpdateCheckSettingsAsync(
        SaveUpdateCheckSettingsPayload input,
        CancellationToken cancellationToken);

    ValueTask SetUpdateCheckLastCheckedAtAsync(DateTimeOffset? checkedAt, CancellationToken cancellationToken);

    ValueTask<LayoutSettingsPayload> GetLayoutSettingsAsync(CancellationToken cancellationToken);

    ValueTask<LayoutSettingsPayload> SaveLayoutSettingsAsync(
        SaveLayoutSettingsPayload input,
        CancellationToken cancellationToken);

    ValueTask<WindowSettingsPayload> GetWindowSettingsAsync(CancellationToken cancellationToken);

    ValueTask<WindowSettingsPayload> SaveWindowSettingsAsync(
        SaveWindowSettingsPayload input,
        CancellationToken cancellationToken);
}
