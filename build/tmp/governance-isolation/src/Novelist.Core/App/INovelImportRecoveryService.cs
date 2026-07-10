using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface INovelImportRecoveryService
{
    ValueTask<NovelImportReconciliationResultPayload> ReconcileAsync(CancellationToken cancellationToken);
}
