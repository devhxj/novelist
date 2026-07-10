using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IChatSessionService
{
    ValueTask<PageResultPayload<SessionMetaPayload>> GetSessionsAsync(
        GetSessionsPayload input,
        CancellationToken cancellationToken);

    ValueTask<SessionDetailPayload> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SessionMessagePayload>> GetSessionMessagesAsync(
        string sessionId,
        CancellationToken cancellationToken);

    ValueTask<ChatResultPayload> ChatAsync(
        ChatInputPayload input,
        CancellationToken cancellationToken);

    ValueTask<CompressResultPayload> CompressContextAsync(
        CompressInputPayload input,
        CancellationToken cancellationToken);

    ValueTask CancelChatAsync(
        string sessionId,
        CancellationToken cancellationToken);
}
