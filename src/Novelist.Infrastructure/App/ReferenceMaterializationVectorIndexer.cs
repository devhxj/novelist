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

    internal async ValueTask<ReferenceMaterializationVectorIndexResult> IndexCurrentChapterAsync(
        ReferenceMaterializationChapterClaim claim,
        CancellationToken cancellationToken)
    {
        var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
        var store = new SqliteReferenceMaterializationRunStore(_databasePathResolver);
        var workItem = await store.ReadCurrentChapterVectorIndexWorkItemAsync(claim, cancellationToken);
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
                "Materialization vector index creation failed.",
                exception);
        }

        return await store.CompleteCurrentChapterIndexAsync(claim, workItem, cancellationToken);
    }
}
