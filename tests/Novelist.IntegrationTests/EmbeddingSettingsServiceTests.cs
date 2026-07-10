using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

[CollectionDefinition("onnx-runtime", DisableParallelization = true)]
public sealed class OnnxRuntimeCollection;

[Collection("onnx-runtime")]
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
        Assert.Equal("api", saved.ProviderType);
        Assert.Equal("https://api.example.com/v1/embeddings", saved.EndpointUrl);
        Assert.Equal("sk-secret", saved.ApiKey);
        Assert.Equal("text-embedding-small", saved.ModelId);
        Assert.Equal(1024, saved.Dimensions);

        var active = await reloaded.GetActiveEmbeddingOptionsAsync(CancellationToken.None);
        Assert.NotNull(active);
        Assert.Equal("custom", active.ProviderKey);
        Assert.Equal("api", active.ProviderType);
        Assert.Equal("https://api.example.com/v1/embeddings", active.EndpointUrl);
        Assert.Equal("sk-secret", active.ApiKey);

        await service.TestConnectionAsync(saved, CancellationToken.None);
        Assert.Equal(["novelist embedding test"], embeddings.Requests.Single());
    }

    [Fact]
    public async Task EmbeddingConfigNormalizesKnownEndpointSuffixesAndRejectsQueryFragments()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var service = new FileSystemEmbeddingSettingsService(
            options,
            new RecordingEmbeddingClient(dimensions: 3),
            new PackagedSqliteVecExtensionResolver(baseDirectory: _root, runtimeIdentifier: "win-x64"));

        foreach (var endpoint in new[]
        {
            "api.example.com/v1/responses",
            "https://api.example.com/v1/models",
            "https://api.example.com/v1/embeddings/",
            "https://api.example.com/v1/chat/completions"
        })
        {
            await service.SaveConfigAsync(
                new EmbeddingConfigPayload(
                    "Custom",
                    endpoint,
                    "sk-secret",
                    "text-embedding-small",
                    1024,
                    ""),
                CancellationToken.None);

            var saved = await service.GetConfigAsync(CancellationToken.None);
            Assert.Equal("https://api.example.com/v1/embeddings", saved.EndpointUrl);
        }

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.SaveConfigAsync(
                new EmbeddingConfigPayload(
                    "Custom",
                    "https://api.example.com/v1/models?api-version=1",
                    "sk-secret",
                    "text-embedding-small",
                    1024,
                    ""),
                CancellationToken.None));
    }

    [Fact]
    public async Task OnnxEmbeddingConfigUsesFixedBuiltinModelWithoutApiSecretOrEndpoint()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var embeddings = new RecordingEmbeddingClient(dimensions: 3);
        var service = new FileSystemEmbeddingSettingsService(
            options,
            embeddings,
            new PackagedSqliteVecExtensionResolver(baseDirectory: _root, runtimeIdentifier: "win-x64"));

        await service.SaveConfigAsync(
            new EmbeddingConfigPayload(
                ProviderKey: "onnx",
                EndpointUrl: "https://api.example.com/v1/embeddings",
                ApiKey: "sk-secret",
                ModelId: "text2vec-base-chinese",
                Dimensions: 768,
                User: "ignored",
                ProviderType: "onnx",
                OnnxModelPath: "",
                OnnxVocabPath: "",
                MaxSequenceLength: 128,
                NormalizeEmbeddings: false),
            CancellationToken.None);

        var saved = await service.GetConfigAsync(CancellationToken.None);
        Assert.Equal("onnx", saved.ProviderType);
        Assert.Equal("onnx", saved.ProviderKey);
        Assert.Equal("", saved.EndpointUrl);
        Assert.Equal("", saved.ApiKey);
        Assert.Equal("", saved.User);
        Assert.Equal(BuiltinOnnxEmbeddingModel.ModelId, saved.ModelId);
        Assert.Equal(BuiltinOnnxEmbeddingModel.Dimensions, saved.Dimensions);
        Assert.Equal("", saved.OnnxModelPath);
        Assert.Equal("", saved.OnnxVocabPath);
        Assert.Equal(512, saved.MaxSequenceLength);
        Assert.True(saved.NormalizeEmbeddings);

        var active = await service.GetActiveEmbeddingOptionsAsync(CancellationToken.None);
        Assert.NotNull(active);
        Assert.Equal("onnx", active.ProviderType);
        Assert.Equal("", active.EndpointUrl);
        Assert.Equal("", active.ApiKey);
        Assert.Equal(BuiltinOnnxEmbeddingModel.ModelId, active.ModelId);
        Assert.Equal(BuiltinOnnxEmbeddingModel.Dimensions, active.Dimensions);
        Assert.Equal("", active.OnnxModelPath);
        Assert.Equal(BuiltinOnnxEmbeddingModel.DocumentInputKind, active.InputKind);

        await service.TestConnectionAsync(saved, CancellationToken.None);
        Assert.Equal("onnx", embeddings.Options.Single().ProviderType);
        Assert.Equal(BuiltinOnnxEmbeddingModel.ModelId, embeddings.Options.Single().ModelId);
    }

    [Fact]
    public async Task HybridEmbeddingClientRoutesOnnxWithoutCallingApiClient()
    {
        var api = new RecordingEmbeddingClient(dimensions: 3);
        var onnx = new RecordingEmbeddingClient(dimensions: 5);
        var client = new HybridEmbeddingClient(api, onnx);

        var result = await client.EmbedAsync(
            ["本地向量"],
            new EmbeddingRequestOptions(
                ProviderKey: "onnx",
                EndpointUrl: "",
                ApiKey: "",
                ModelId: "local-model",
                Dimensions: 5,
                User: null,
                ProviderType: "onnx",
                OnnxModelPath: "model.onnx",
                OnnxVocabPath: "vocab.txt"),
            CancellationToken.None);

        Assert.Equal(5, result.Dimensions);
        Assert.Empty(api.Requests);
        Assert.Equal(["本地向量"], onnx.Requests.Single());
    }

    [Fact]
    public async Task LocalOnnxEmbeddingClientTokenizesRunsMeanPoolingAndNormalizes()
    {
        var modelPath = Path.Combine(_root, "model.onnx");
        var vocabPath = Path.Combine(_root, "vocab.txt");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(modelPath, "fake", CancellationToken.None);
        await File.WriteAllTextAsync(
            vocabPath,
            """
            [PAD]
            [UNK]
            [CLS]
            [SEP]
            你
            好
            world
            !
            """,
            CancellationToken.None);
        var runner = new RecordingLocalOnnxRunner();
        var client = new LocalOnnxEmbeddingClient(new StaticLocalOnnxRunnerFactory(runner));

        var result = await client.EmbedAsync(
            ["你好 world!"],
            new EmbeddingRequestOptions(
                ProviderKey: "onnx",
                EndpointUrl: "",
                ApiKey: "",
                ModelId: "local-model",
                Dimensions: null,
                User: null,
                ProviderType: "onnx",
                OnnxModelPath: modelPath,
                OnnxVocabPath: vocabPath,
                MaxSequenceLength: 8,
                NormalizeEmbeddings: true),
            CancellationToken.None);

        Assert.Equal(2, result.Dimensions);
        Assert.Equal(8, runner.LastInputs!.SequenceLength);
        Assert.Equal(new long[] { 2, 4, 5, 6, 7, 3, 0, 0 }, runner.LastInputs.InputIds);
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 0, 0 }, runner.LastInputs.AttentionMask);
        var vector = result.Items.Single().Vector;
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(value => value * value)), precision: 5);
    }

    [Fact]
    public async Task LocalOnnxEmbeddingClientDisposesCachedDisposableRunner()
    {
        var modelPath = Path.Combine(_root, "disposable-model.onnx");
        var vocabPath = Path.Combine(_root, "disposable-vocab.txt");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(modelPath, "fake", CancellationToken.None);
        await File.WriteAllTextAsync(vocabPath, "[PAD]\n[UNK]\n[CLS]\n[SEP]\n你", CancellationToken.None);
        var runner = new RecordingLocalOnnxRunner();
        var client = new LocalOnnxEmbeddingClient(new StaticLocalOnnxRunnerFactory(runner));
        var options = new EmbeddingRequestOptions(
            ProviderKey: "onnx",
            EndpointUrl: "",
            ApiKey: "",
            ModelId: "local-model",
            Dimensions: null,
            User: null,
            ProviderType: "onnx",
            OnnxModelPath: modelPath,
            OnnxVocabPath: vocabPath,
            MaxSequenceLength: 4);

        await client.EmbedAsync(["你"], options, CancellationToken.None);

        Assert.IsAssignableFrom<IDisposable>(client).Dispose();

        Assert.True(runner.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await client.EmbedAsync(["你"], options, CancellationToken.None));
    }

    [Fact]
    public async Task LocalOnnxEmbeddingClientAcceptsPooledEmbeddingOutput()
    {
        var modelPath = Path.Combine(_root, "pooled-model.onnx");
        var vocabPath = Path.Combine(_root, "pooled-vocab.txt");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(modelPath, "fake", CancellationToken.None);
        await File.WriteAllTextAsync(
            vocabPath,
            """
            [PAD]
            [UNK]
            [CLS]
            [SEP]
            你
            好
            world
            """,
            CancellationToken.None);
        var runner = new PooledLocalOnnxRunner(hiddenSize: 3);
        var client = new LocalOnnxEmbeddingClient(new StaticLocalOnnxRunnerFactory(runner));

        var result = await client.EmbedAsync(
            ["你好", "world"],
            new EmbeddingRequestOptions(
                ProviderKey: "onnx",
                EndpointUrl: "",
                ApiKey: "",
                ModelId: "pooled-local-model",
                Dimensions: 3,
                User: null,
                ProviderType: "onnx",
                OnnxModelPath: modelPath,
                OnnxVocabPath: vocabPath,
                MaxSequenceLength: 6,
                NormalizeEmbeddings: false),
            CancellationToken.None);

        Assert.Equal(3, result.Dimensions);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal([1f, 2f, 3f], result.Items[0].Vector);
        Assert.Equal([4f, 5f, 6f], result.Items[1].Vector);
    }

    [Fact]
    public async Task BuiltinBgeOnnxUsesQueryInstructionAndClsPooling()
    {
        var modelPath = Path.Combine(_root, "bge-model.onnx");
        var vocabPath = Path.Combine(_root, "bge-vocab.txt");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(modelPath, "fake", CancellationToken.None);
        await File.WriteAllTextAsync(
            vocabPath,
            """
            [PAD]
            [UNK]
            [CLS]
            [SEP]
            为
            你
            """,
            CancellationToken.None);
        var runner = new BgeLocalOnnxRunner();
        var client = new LocalOnnxEmbeddingClient(new StaticLocalOnnxRunnerFactory(runner));

        var result = await client.EmbedAsync(
            ["你"],
            new EmbeddingRequestOptions(
                ProviderKey: "onnx",
                EndpointUrl: "",
                ApiKey: "",
                ModelId: BuiltinOnnxEmbeddingModel.ModelId,
                Dimensions: null,
                User: null,
                ProviderType: "onnx",
                OnnxModelPath: modelPath,
                OnnxVocabPath: vocabPath,
                InputKind: BuiltinOnnxEmbeddingModel.QueryInputKind),
            CancellationToken.None);

        Assert.Equal(BuiltinOnnxEmbeddingModel.Dimensions, result.Dimensions);
        Assert.Equal(BuiltinOnnxEmbeddingModel.MaxSequenceLength, runner.LastInputs!.SequenceLength);
        Assert.Equal(4, runner.LastInputs.InputIds[1]);
        var vector = result.Items.Single().Vector;
        Assert.Equal(1f, vector[0]);
        Assert.Equal(0f, vector[1]);
        Assert.All(vector.Skip(2), value => Assert.Equal(0f, value));
    }

    [Fact]
    public async Task LocalOnnxEmbeddingClientRunsBundledBgeModelWhenRuntimeAssetsExist()
    {
        var modelPath = FindBundledOnnxModelFile("model.onnx");
        var vocabPath = FindBundledOnnxModelFile("vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
        {
            return;
        }

        using var client = new LocalOnnxEmbeddingClient();

        var result = await client.EmbedAsync(
            ["雨声压低了整条街的呼吸。", "旧城门下发现暗号。"],
            new EmbeddingRequestOptions(
                ProviderKey: "onnx",
                EndpointUrl: "",
                ApiKey: "",
                ModelId: BuiltinOnnxEmbeddingModel.ModelId,
                Dimensions: BuiltinOnnxEmbeddingModel.Dimensions,
                User: null,
                ProviderType: "onnx",
                OnnxModelPath: modelPath,
                OnnxVocabPath: vocabPath,
                MaxSequenceLength: BuiltinOnnxEmbeddingModel.MaxSequenceLength,
                NormalizeEmbeddings: BuiltinOnnxEmbeddingModel.NormalizeEmbeddings),
            CancellationToken.None);

        Assert.Equal(BuiltinOnnxEmbeddingModel.ModelId, result.Model);
        Assert.Equal(BuiltinOnnxEmbeddingModel.Dimensions, result.Dimensions);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item =>
        {
            Assert.Equal(BuiltinOnnxEmbeddingModel.Dimensions, item.Vector.Count);
            Assert.All(item.Vector, value => Assert.False(float.IsNaN(value) || float.IsInfinity(value)));
            Assert.Equal(1.0, Math.Sqrt(item.Vector.Sum(value => value * value)), precision: 3);
        });
        Assert.NotEqual(result.Items[0].Vector, result.Items[1].Vector);
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

    private static string FindBundledOnnxModelFile(string fileName)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "build", "runtime", "models", fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return string.Empty;
    }

    private sealed class RecordingEmbeddingClient : IEmbeddingClient
    {
        private readonly int _dimensions;

        public RecordingEmbeddingClient(int dimensions)
        {
            _dimensions = dimensions;
        }

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public List<EmbeddingRequestOptions> Options { get; } = [];

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(inputs.ToArray());
            Options.Add(options);
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

    private sealed class StaticLocalOnnxRunnerFactory : ILocalOnnxEmbeddingRunnerFactory
    {
        private readonly ILocalOnnxEmbeddingRunner _runner;

        public StaticLocalOnnxRunnerFactory(ILocalOnnxEmbeddingRunner runner)
        {
            _runner = runner;
        }

        public ILocalOnnxEmbeddingRunner Create(LocalOnnxEmbeddingOptions options)
        {
            return _runner;
        }
    }

    private sealed class RecordingLocalOnnxRunner : ILocalOnnxEmbeddingRunner, IDisposable
    {
        public LocalOnnxTensorInputs? LastInputs { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask<LocalOnnxTensorOutput> RunAsync(
            LocalOnnxTensorInputs inputs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastInputs = inputs;
            var values = new float[inputs.BatchSize * inputs.SequenceLength * 2];
            for (var batch = 0; batch < inputs.BatchSize; batch++)
            {
                for (var token = 0; token < inputs.SequenceLength; token++)
                {
                    var offset = ((batch * inputs.SequenceLength) + token) * 2;
                    values[offset] = token + 1;
                    values[offset + 1] = (token + 1) * 2;
                }
            }

            return ValueTask.FromResult(new LocalOnnxTensorOutput(
                values,
                inputs.BatchSize,
                inputs.SequenceLength,
                2));
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class PooledLocalOnnxRunner : ILocalOnnxEmbeddingRunner
    {
        private readonly int _hiddenSize;

        public PooledLocalOnnxRunner(int hiddenSize)
        {
            _hiddenSize = hiddenSize;
        }

        public ValueTask<LocalOnnxTensorOutput> RunAsync(
            LocalOnnxTensorInputs inputs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = Enumerable.Range(1, inputs.BatchSize * _hiddenSize)
                .Select(value => (float)value)
                .ToArray();
            return ValueTask.FromResult(new LocalOnnxTensorOutput(
                values,
                inputs.BatchSize,
                SequenceLength: 1,
                _hiddenSize,
                IsPooledOutput: true));
        }
    }

    private sealed class BgeLocalOnnxRunner : ILocalOnnxEmbeddingRunner
    {
        public LocalOnnxTensorInputs? LastInputs { get; private set; }

        public ValueTask<LocalOnnxTensorOutput> RunAsync(
            LocalOnnxTensorInputs inputs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastInputs = inputs;
            var values = new float[inputs.BatchSize * inputs.SequenceLength * BuiltinOnnxEmbeddingModel.Dimensions];
            values[0] = 3;
            values[BuiltinOnnxEmbeddingModel.Dimensions + 1] = 3;
            return ValueTask.FromResult(new LocalOnnxTensorOutput(
                values,
                inputs.BatchSize,
                inputs.SequenceLength,
                BuiltinOnnxEmbeddingModel.Dimensions));
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
