using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceCorpusDatabasePathResolver : IReferenceCorpusDatabasePathResolver
{
 private readonly AppInitializationOptions _options;

 public ReferenceCorpusDatabasePathResolver(AppInitializationOptions? options = null)
 {
 _options = options ?? new AppInitializationOptions();
 }

 public async ValueTask<string> ResolveAsync(CancellationToken cancellationToken)
 {
 var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
 return Path.GetFullPath(Path.Combine(dataDirectory, "reference-anchor", "index.sqlite"));
 }
}
