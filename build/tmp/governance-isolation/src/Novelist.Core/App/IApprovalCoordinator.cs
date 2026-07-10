using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface IApprovalCoordinator
{
    int PendingCount { get; }

    ValueTask<ToolApprovalResultPayload> RequestApprovalAsync(
        ToolApprovalRequestPayload request,
        CancellationToken cancellationToken);

    ValueTask<bool> CompleteAsync(
        ToolApprovalDecisionPayload decision,
        CancellationToken cancellationToken);

    ValueTask<bool> CancelToolAsync(
        string toolId,
        CancellationToken cancellationToken);

    ValueTask<int> CancelSessionAsync(
        string sessionId,
        CancellationToken cancellationToken);
}
