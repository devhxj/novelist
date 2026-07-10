using System.Text.Json;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusDatabasePathResolverTests : IAsyncLifetime
{
 private readonly string _root = Path.Combine(Path.GetTempPath(), $"novelist-corpus-path-{Guid.NewGuid():N}");

 [Fact]
 public async Task ResolveAsyncReadsCurrentPointerOnEveryBinding()
 {
 var options = new AppInitializationOptions
 {
 ConfigDirectory = Path.Combine(_root, "config"),
 DefaultDataDirectory = Path.Combine(_root, "default")
 };
 Directory.CreateDirectory(options.ConfigDirectory);
 var resolver = new ReferenceCorpusDatabasePathResolver(options);
 var first = Path.Combine(_root, "data-a");
 var second = Path.Combine(_root, "data-b");

 await WritePointerAsync(options.ConfigDirectory, first);
 Assert.Equal(Path.Combine(Path.GetFullPath(first), "reference-anchor", "index.sqlite"),
 await resolver.ResolveAsync(CancellationToken.None));

 await WritePointerAsync(options.ConfigDirectory, second);
 Assert.Equal(Path.Combine(Path.GetFullPath(second), "reference-anchor", "index.sqlite"),
 await resolver.ResolveAsync(CancellationToken.None));
 }

 [Fact]
 public async Task ResolveAsyncRejectsUninitializedApplication()
 {
 var options = new AppInitializationOptions
 {
 ConfigDirectory = Path.Combine(_root, "missing-config"),
 DefaultDataDirectory = Path.Combine(_root, "default")
 };
 var resolver = new ReferenceCorpusDatabasePathResolver(options);

 await Assert.ThrowsAsync<AppNotInitializedException>(async () =>
 await resolver.ResolveAsync(CancellationToken.None));
 }

 private static async ValueTask WritePointerAsync(string configDirectory, string dataDirectory)
 {
 Directory.CreateDirectory(configDirectory);
 await File.WriteAllTextAsync(
 Path.Combine(configDirectory, "config.json"),
 JsonSerializer.Serialize(new Dictionary<string, string> { ["data_dir"] = dataDirectory }));
 }

 public Task InitializeAsync()
 {
 Directory.CreateDirectory(_root);
 return Task.CompletedTask;
 }

 public Task DisposeAsync()
 {
 if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
 return Task.CompletedTask;
 }
}
