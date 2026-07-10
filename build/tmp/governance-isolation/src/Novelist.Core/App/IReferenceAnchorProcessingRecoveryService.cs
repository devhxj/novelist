namespace Novelist.Core.App;

public interface IReferenceAnchorProcessingRecoveryService
{
    ValueTask ReconcileRecoverableProcessingAsync(CancellationToken cancellationToken);
}
