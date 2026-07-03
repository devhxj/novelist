using System.Net;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class RagIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StandardEmbeddingClientPostsBatchInputsAndParsesDimensionsUsage()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "object": "list",
                  "model": "embed-v1",
                  "data": [
                    { "object": "embedding", "index": 1, "embedding": [0.4, 0.5, 0.6] },
                    { "object": "embedding", "index": 0, "embedding": [0.1, 0.2, 0.3] }
                  ],
                  "usage": { "prompt_tokens": 9, "total_tokens": 9 }
                }
                """)
        });
        var client = new StandardEmbeddingClient(new HttpClient(handler));

        var result = await client.EmbedAsync(
            ["第一段", "第二段"],
            new EmbeddingRequestOptions(
                ProviderKey: "custom",
                EndpointUrl: "https://api.example.com/v1",
                ApiKey: "sk-secret",
                ModelId: "embed-v1",
                Dimensions: 3,
                User: "novelist-test"),
            CancellationToken.None);

        Assert.Equal("embed-v1", result.Model);
        Assert.Equal(3, result.Dimensions);
        Assert.Equal(9, result.Usage.TotalTokens);
        Assert.Equal([0.1f, 0.2f, 0.3f], result.Items[0].Vector);
        Assert.Equal([0.4f, 0.5f, 0.6f], result.Items[1].Vector);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.example.com/v1/embeddings", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", request.Headers.Authorization?.ToString());

        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.Equal("embed-v1", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("float", body.RootElement.GetProperty("encoding_format").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal("novelist-test", body.RootElement.GetProperty("user").GetString());
        Assert.Equal("第一段", body.RootElement.GetProperty("input")[0].GetString());
        Assert.Equal("第二段", body.RootElement.GetProperty("input")[1].GetString());
    }

    [Fact]
    public async Task StandardEmbeddingClientRejectsEmptyInputsAndRedactsProviderErrors()
    {
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage((HttpStatusCode)429)
        {
            Content = JsonContent("""{"error":{"message":"bad key sk-secret"}}""")
        });
        var client = new StandardEmbeddingClient(new HttpClient(handler));
        var options = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "sk-secret",
            "embed-v1",
            Dimensions: null,
            User: null);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.EmbedAsync(["ok", ""], options, CancellationToken.None));

        var error = await Assert.ThrowsAsync<BridgeRequestException>(async () =>
            await client.EmbedAsync(["ok"], options, CancellationToken.None));

        Assert.Equal(BridgeErrorCodes.LlmProviderError, error.Code);
        Assert.True(error.Retryable);
        Assert.DoesNotContain("sk-secret", error.Message);
    }

    [Fact]
    public async Task RebuildNovelIndexChunksChapterContentPersistsStateAndProvisionsSqliteVecTable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("向量测试", "", ""), CancellationToken.None);
        var first = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, first.FilePath, "第一段内容。\n\n第二段内容包含更多线索。"),
            CancellationToken.None);

        var provisioner = new RecordingSqliteVecTableProvisioner();
        var index = new SqliteRagIndexService(
            options,
            novels,
            chapters,
            new StaticEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "custom",
                "https://api.example.com/v1/embeddings",
                "sk-secret",
                "embed-v1",
                3,
                null)),
            new DeterministicEmbeddingClient(dimensions: 3),
            provisioner);

        var state = await index.RebuildNovelAsync(novel.Id, CancellationToken.None);

        Assert.Equal("ready", state.Status);
        Assert.Equal(novel.Id, state.NovelId);
        Assert.Equal("custom", state.ProviderKey);
        Assert.Equal("embed-v1", state.ModelId);
        Assert.Equal(3, state.Dimensions);
        Assert.Equal("paragraph-v1", state.ChunkerVersion);
        Assert.Equal("vec_novel_1_3", state.VectorTable);
        Assert.Equal(2, state.ChunkCount);
        Assert.True(string.IsNullOrEmpty(state.LastError));

        var chunks = await index.GetIndexedChunksAsync(novel.Id, CancellationToken.None);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, chunk => Assert.Equal(novel.Id, chunk.NovelId));
        Assert.Contains(chunks, chunk => chunk.Content == "第一段内容。");
        Assert.Contains(chunks, chunk => chunk.Content == "第二段内容包含更多线索。");

        var provision = Assert.Single(provisioner.Provisions);
        Assert.Equal("vec_novel_1_3", provision.TableName);
        Assert.Equal(3, provision.Dimensions);
        Assert.Equal(2, provision.Vectors.Count);
    }

    [Fact]
    public async Task RebuildNovelIndexRecordsMissingConfigurationInsteadOfCallingEmbeddingApi()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("无配置", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "需要建立索引的内容。"),
            CancellationToken.None);
        var embedding = new DeterministicEmbeddingClient(dimensions: 3);
        var index = new SqliteRagIndexService(
            options,
            novels,
            chapters,
            new StaticEmbeddingConfigurationService(null),
            embedding,
            new RecordingSqliteVecTableProvisioner());

        var state = await index.RebuildNovelAsync(novel.Id, CancellationToken.None);

        Assert.Equal("missing_config", state.Status);
        Assert.Equal(1, state.ChunkCount);
        Assert.Equal(0, state.Dimensions);
        Assert.Contains("Embedding provider is not configured", state.LastError, StringComparison.Ordinal);
        Assert.Empty(embedding.Requests);
    }

    [Fact]
    public async Task WorkspaceSearchRebuildUsesInjectedRagIndexService()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        var chapters = new FileSystemChapterContentService(options, novels);
        var world = new FileSystemWorldEntityService(options, novels);
        var planning = new FileSystemPlanningService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("桥接索引", "", ""), CancellationToken.None);
        var index = new RecordingRagIndexService();
        var search = new FileSystemWorkspaceSearchService(options, novels, chapters, world, planning, index);

        await search.RebuildNovelIndexAsync(novel.Id, CancellationToken.None);

        Assert.Equal([novel.Id], index.RebuiltNovelIds);
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

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class StaticEmbeddingConfigurationService : IEmbeddingConfigurationService
    {
        private readonly EmbeddingRequestOptions? _options;

        public StaticEmbeddingConfigurationService(EmbeddingRequestOptions? options)
        {
            _options = options;
        }

        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_options);
        }
    }

    private sealed class DeterministicEmbeddingClient : IEmbeddingClient
    {
        private readonly int _dimensions;

        public DeterministicEmbeddingClient(int dimensions)
        {
            _dimensions = dimensions;
        }

        public List<IReadOnlyList<string>> Requests { get; } = [];

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            Requests.Add(inputs.ToArray());
            var items = inputs
                .Select((input, index) => new EmbeddingItemResult(
                    index,
                    Enumerable.Range(0, _dimensions)
                        .Select(offset => (float)(input.Length + offset))
                        .ToArray()))
                .ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                _dimensions,
                items,
                new EmbeddingUsage(0, inputs.Sum(input => input.Length))));
        }
    }

    private sealed class RecordingSqliteVecTableProvisioner : ISqliteVecTableProvisioner
    {
        public List<SqliteVecProvisionRequest> Provisions { get; } = [];

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Provisions.Add(request);
            Assert.Contains("create virtual table", request.CreateTableSql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"embedding float[{request.Dimensions}]", request.CreateTableSql, StringComparison.Ordinal);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRagIndexService : IRagIndexService
    {
        public List<long> RebuiltNovelIds { get; } = [];

        public ValueTask<RagIndexStatePayload?> GetIndexStateAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<RagIndexStatePayload?>(null);
        }

        public ValueTask<IReadOnlyList<RagChunkPayload>> GetIndexedChunksAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<RagChunkPayload>>([]);
        }

        public ValueTask<RagIndexStatePayload> RebuildNovelAsync(long novelId, CancellationToken cancellationToken)
        {
            RebuiltNovelIds.Add(novelId);
            return ValueTask.FromResult(new RagIndexStatePayload(
                novelId,
                "",
                "",
                0,
                "paragraph-v1",
                "ready",
                0,
                "",
                "",
                DateTimeOffset.UtcNow));
        }
    }
}
