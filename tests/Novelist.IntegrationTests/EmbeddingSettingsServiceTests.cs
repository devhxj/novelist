using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class EmbeddingSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EmbeddingConfigPersistsEncryptedAndProvidesActiveOptions()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var embeddings = new RecordingEmbeddingClient(dimensions: 3);
        var service = new FileSystemEmbeddingSettingsService(
            options,
            embeddings,
            new PackagedSqliteVecExtensionResolver(baseDirectory: _root, runtimeIdentifier: "win-x64"));

        var initial = await service.GetConfigAsync(CancellationToken.None);
        Assert.Equal("", initial.ApiKey);
        Assert.Null(await service.GetActiveEmbeddingOptionsAsync(CancellationToken.None));

        var input = new EmbeddingConfigPayload(
            "Custom",
            "api.example.com/v1/chat/completions",
            "sk-secret",
            "text-embedding-small",
            1024,
            "novelist-test");

        await service.SaveConfigAsync(input, CancellationToken.None);

        var encryptedPath = Path.Combine(options.DefaultDataDirectory, "embedding", "config.enc");
        Assert.True(File.Exists(encryptedPath));
        Assert.DoesNotContain("sk-secret", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(encryptedPath)));

        var reloaded = new FileSystemEmbeddingSettingsService(
            options,
            embeddings,
            new PackagedSqliteVecExtensionResolver(baseDirectory: _root, runtimeIdentifier: "win-x64"));
        var saved = await reloaded.GetConfigAsync(CancellationToken.None);
        Assert.Equal("custom", saved.ProviderKey);
        Assert.Equal("https://api.example.com/v1/embeddings", saved.EndpointUrl);
        Assert.Equal("sk-secret", saved.ApiKey);
        Assert.Equal("text-embedding-small", saved.ModelId);
        Assert.Equal(1024, saved.Dimensions);

        var active = await reloaded.GetActiveEmbeddingOptionsAsync(CancellationToken.None);
        Assert.NotNull(active);
        Assert.Equal("custom", active.ProviderKey);
        Assert.Equal("https://api.example.com/v1/embeddings", active.EndpointUrl);
        Assert.Equal("sk-secret", active.ApiKey);

        await service.TestConnectionAsync(saved, CancellationToken.None);
        Assert.Equal(["novelist embedding test"], embeddings.Requests.Single());
    }

    [Fact]
    public async Task BridgeEmbeddingHandlersPersistConfigTestConnectionAndExposeSqliteVecStatus()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var nativeDirectory = Path.Combine(_root, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDirectory);
        await File.WriteAllTextAsync(Path.Combine(nativeDirectory, "vec0.dll"), "fake", CancellationToken.None);
        var resolver = new PackagedSqliteVecExtensionResolver(baseDirectory: _root, runtimeIdentifier: "win-x64");
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterEmbeddingConfigurationHandlers(new FileSystemEmbeddingSettingsService(
                options,
                new RecordingEmbeddingClient(dimensions: 3),
                resolver));

        using var saved = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_save_embedding",
              "method": "SaveEmbeddingConfig",
              "payload": {
                "args": [
                  {
                    "provider_key": "custom",
                    "endpoint_url": "https://api.example.com/v1",
                    "api_key": "sk-secret",
                    "model_id": "embed-v1",
                    "dimensions": 3,
                    "user": ""
                  }
                ]
              }
            }
            """));
        Assert.True(saved.RootElement.GetProperty("ok").GetBoolean());

        using var config = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_get_embedding",
              "method": "GetEmbeddingConfig"
            }
            """));
        Assert.Equal("embed-v1", config.RootElement.GetProperty("result").GetProperty("model_id").GetString());
        Assert.Equal("sk-secret", config.RootElement.GetProperty("result").GetProperty("api_key").GetString());

        using var tested = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_test_embedding",
              "method": "TestEmbeddingConnection",
              "payload": {
                "args": [
                  {
                    "provider_key": "custom",
                    "endpoint_url": "https://api.example.com/v1",
                    "api_key": "sk-secret",
                    "model_id": "embed-v1",
                    "dimensions": 3,
                    "user": ""
                  }
                ]
              }
            }
            """));
        Assert.True(tested.RootElement.GetProperty("ok").GetBoolean());

        using var sqliteVec = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_vec",
              "method": "GetSqliteVecStatus"
            }
            """));
        var status = sqliteVec.RootElement.GetProperty("result");
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal("win-x64", status.GetProperty("runtime_identifier").GetString());
        Assert.Equal("vec0.dll", status.GetProperty("file_name").GetString());
        Assert.DoesNotContain(_root, status.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PackagedSqliteVecResolverFindsRuntimeNativeCandidateAndReportsMissingState()
    {
        var nativeDirectory = Path.Combine(_root, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nativeDirectory);
        var nativePath = Path.Combine(nativeDirectory, "vec0.dll");
        File.WriteAllText(nativePath, "fake");

        var found = new PackagedSqliteVecExtensionResolver(
            baseDirectory: _root,
            runtimeIdentifier: "win-x64").Resolve();

        Assert.True(found.Available);
        Assert.Equal(Path.GetFullPath(nativePath), found.ExtensionPath);
        Assert.Equal("win-x64", found.RuntimeIdentifier);

        var missing = new PackagedSqliteVecExtensionResolver(
            baseDirectory: Path.Combine(_root, "missing"),
            runtimeIdentifier: "win-x64").Resolve();

        Assert.False(missing.Available);
        Assert.Equal("not_found", missing.Status);
        Assert.Contains("win-x64", missing.Error, StringComparison.Ordinal);
        Assert.Equal("", missing.ExtensionPath);
    }

    [Fact]
    public async Task SqliteVecProvisionerUsesResolverAndFailsBeforeOpeningMissingExtension()
    {
        var provisioner = new SqliteVecTableProvisioner(new MissingSqliteVecExtensionResolver());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provisioner.ProvisionAsync(
                Path.Combine(_root, "rag.sqlite"),
                new SqliteVecProvisionRequest(
                    "vec_novel_1_3",
                    3,
                    SqliteVecTableProvisioner.BuildCreateTableSql("vec_novel_1_3", 3),
                    []),
                CancellationToken.None));

        Assert.Contains("sqlite-vec", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "rag.sqlite")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private sealed class RecordingEmbeddingClient : IEmbeddingClient
    {
        private readonly int _dimensions;

        public RecordingEmbeddingClient(int dimensions)
        {
            _dimensions = dimensions;
        }

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(inputs.ToArray());
            var items = inputs.Select((_, index) => new EmbeddingItemResult(
                index,
                Enumerable.Range(0, _dimensions).Select(offset => (float)(index + offset + 1)).ToArray())).ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                _dimensions,
                items,
                new EmbeddingUsage(1, 1)));
        }
    }

    private sealed class MissingSqliteVecExtensionResolver : ISqliteVecExtensionResolver
    {
        public SqliteVecExtensionResolution Resolve()
        {
            return new SqliteVecExtensionResolution(
                Available: false,
                ExtensionPath: string.Empty,
                RuntimeIdentifier: "test-rid",
                Status: "not_found",
                Error: "sqlite-vec test extension is missing.");
        }
    }
}
