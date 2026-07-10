namespace Novelist.Core.App;

public interface IReferenceCorpusDatabasePathResolver
{
 ValueTask<string> ResolveAsync(CancellationToken cancellationToken);
}
