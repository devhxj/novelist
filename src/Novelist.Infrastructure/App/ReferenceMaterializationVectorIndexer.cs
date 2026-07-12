using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceMaterializationVectorIndexer
{
    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly ISqliteVecTableProvisioner _vecProvisioner;

    public ReferenceMaterializationVectorIndexer(
        IReferenceCorpusDatabasePathResolver databasePathResolver,
        ISqliteVecTableProvisioner vecProvisioner)
    {
        _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
        _vecProvisioner = vecProvisioner ?? throw new ArgumentNullException(nameof(vecProvisioner));
    }

    public async ValueTask<ReferenceMaterializationVectorIndexResult> IndexCurrentBatchAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
        var store = new SqliteReferenceMaterializationRunStore(_databasePathResolver);
        var workItem = await store.ReadCurrentBatchVectorIndexWorkItemAsync(runId, cancellationToken);
        try
        {
            await _vecProvisioner.ProvisionAsync(
                databasePath,
                new SqliteVecProvisionRequest(
                    workItem.TableName,
                    workItem.Dimensions,
                    SqliteVecTableProvisioner.BuildCreateTableSql(workItem.TableName, workItem.Dimensions),
                    workItem.Vectors),
                cancellationToken);
        }
        catch (ReferenceMaterializationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.VectorIndexFailed,
                "Materialization vector index creation failed.");
        }

        return await store.CompleteCurrentBatchIndexAsync(workItem, cancellationToken);
    }
}
