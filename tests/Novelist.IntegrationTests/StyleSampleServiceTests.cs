using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class StyleSampleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StyleSamplesPersistSearchUpdateAndDeleteAcrossServiceRecreation()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novel = await CreateNovelAsync(options);
        var service = new FileSystemStyleSampleService(options, new FileSystemNovelService(options));

        var global = await service.CreateSampleAsync(
            new CreateStyleSamplePayload(
                NovelId: null,
                IsGlobal: true,
                Name: "冷雨对白",
                Content: "“你听见雨了吗？”她想，门外的潮气像铁锈。\n灯光很冷，风声贴着窗。",
                Tags: ["雨夜", "对白", "雨夜 "],
                SourceMetadata: new StyleSampleSourceMetadataPayload("chapter", "42:1", "hash-global-001")),
            CancellationToken.None);
        var local = await service.CreateSampleAsync(
            new CreateStyleSamplePayload(
                NovelId: novel.Id,
                IsGlobal: false,
                Name: "近身内心",
                Content: "他知道自己不该回头。雨水从领口滑进去，像一根细冷的针。",
                Tags: ["雨夜", "内心"],
                SourceMetadata: null),
            CancellationToken.None);

        Assert.True(global.SampleId > 0);
        Assert.Null(global.NovelId);
        Assert.True(global.IsGlobal);
        Assert.Equal(["雨夜", "对白"], global.Tags);
        Assert.DoesNotContain('\n', global.Preview);
        Assert.True(global.Preview.Length <= 120);
        Assert.Equal("style_sample_stats_v1", global.StatsSchemaVersion);
        Assert.True(global.Stats.CharacterCount > 0);
        Assert.True(global.Stats.SentenceCount >= 2);
        Assert.True(global.Stats.DialogueRatio > 0);
        Assert.True(global.Stats.InteriorityRatio > 0);
        Assert.True(global.Stats.SensoryRatio > 0);
        Assert.True(global.Stats.PunctuationPer100Chars > 0);

        var reloaded = new FileSystemStyleSampleService(options, new FileSystemNovelService(options));
        var detail = await reloaded.GetSampleAsync(new GetStyleSamplePayload(global.SampleId), CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("冷雨对白", detail.Name);
        Assert.Equal("“你听见雨了吗？”她想，门外的潮气像铁锈。\n灯光很冷，风声贴着窗。", detail.Content);
        Assert.Equal(global.Stats, detail.Stats);
        Assert.Equal("hash-global-001", detail.SourceMetadata?.SourceHash);

        var novelAndGlobal = await reloaded.SearchSamplesAsync(
            new SearchStyleSamplesPayload(novel.Id, IncludeGlobal: true, Query: "雨", Tags: ["雨夜"], Page: 1, Size: 10),
            CancellationToken.None);
        Assert.Equal(2, novelAndGlobal.Total);
        Assert.Contains(novelAndGlobal.Items, item => item.SampleId == global.SampleId);
        Assert.Contains(novelAndGlobal.Items, item => item.SampleId == local.SampleId);
        Assert.All(novelAndGlobal.Items, item => Assert.IsType<StyleSamplePayload>(item));

        var novelOnly = await reloaded.SearchSamplesAsync(
            new SearchStyleSamplesPayload(novel.Id, IncludeGlobal: false, Query: "", Tags: [], Page: 1, Size: 10),
            CancellationToken.None);
        Assert.Equal([local.SampleId], novelOnly.Items.Select(item => item.SampleId));

        var globalOnly = await reloaded.SearchSamplesAsync(
            new SearchStyleSamplesPayload(null, IncludeGlobal: true, Query: "", Tags: [], Page: 1, Size: 10),
            CancellationToken.None);
        Assert.Equal([global.SampleId], globalOnly.Items.Select(item => item.SampleId));

        var updated = await reloaded.UpdateSampleAsync(
            new UpdateStyleSamplePayload(
                SampleId: local.SampleId,
                NovelId: novel.Id,
                IsGlobal: false,
                Name: "近身内心修订",
                Content: "他没有回头，只把手按在门把上。心里那点犹豫，像潮湿木头里没灭的火。",
                Tags: ["内心", "克制"],
                SourceMetadata: new StyleSampleSourceMetadataPayload("manual", "note-1", "hash-local-002")),
            CancellationToken.None);
        Assert.Equal(local.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= local.CreatedAt);
        Assert.Equal(["内心", "克制"], updated.Tags);
        Assert.NotEqual(local.Stats, updated.Stats);

        await reloaded.DeleteSampleAsync(new DeleteStyleSamplePayload(updated.SampleId), CancellationToken.None);

        Assert.Null(await reloaded.GetSampleAsync(new GetStyleSamplePayload(updated.SampleId), CancellationToken.None));
        var afterDelete = await reloaded.SearchSamplesAsync(
            new SearchStyleSamplesPayload(novel.Id, IncludeGlobal: true, Query: "", Tags: [], Page: 1, Size: 10),
            CancellationToken.None);
        Assert.DoesNotContain(afterDelete.Items, item => item.SampleId == updated.SampleId);
    }

    [Fact]
    public async Task StyleSampleValidationRejectsUnsafeScopeAndPayloads()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novel = await CreateNovelAsync(options);
        var service = new FileSystemStyleSampleService(options, new FileSystemNovelService(options));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateSampleAsync(
                new CreateStyleSamplePayload(novel.Id, IsGlobal: true, Name: "bad", Content: "content", Tags: [], SourceMetadata: null),
                CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateSampleAsync(
                new CreateStyleSamplePayload(null, IsGlobal: false, Name: "bad", Content: "content", Tags: [], SourceMetadata: null),
                CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateSampleAsync(
                new CreateStyleSamplePayload(novel.Id, IsGlobal: false, Name: "", Content: "content", Tags: [], SourceMetadata: null),
                CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateSampleAsync(
                new CreateStyleSamplePayload(novel.Id, IsGlobal: false, Name: "bad", Content: "content\u0001", Tags: [], SourceMetadata: null),
                CancellationToken.None));

        await Assert.ThrowsAnyAsync<ArgumentException>(async () =>
            await service.SearchSamplesAsync(
                new SearchStyleSamplesPayload(novel.Id, IncludeGlobal: true, Query: "", Tags: [], Page: 0, Size: 10),
                CancellationToken.None));
    }

    [Fact]
    public async Task BridgeStyleSampleHandlersPersistAndValidatePayloads()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novel = await CreateNovelAsync(options);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterStyleSampleHandlers(new FileSystemStyleSampleService(options, new FileSystemNovelService(options)));

        using var createJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_create_style_sample",
              "method": "CreateStyleSample",
              "payload": {
                "args": [{
                  "novel_id": {{novel.Id}},
                  "is_global": false,
                  "name": "桥接样本",
                  "content": "“别出声。”她想。雨落在窗上，细密得像针。",
                  "tags": ["桥接", "雨夜"],
                  "source_metadata": { "source_type": "manual", "source_id": "bridge-1", "source_hash": "hash-bridge-001" }
                }]
              }
            }
            """));
        var created = createJson.RootElement.GetProperty("result");
        var sampleId = created.GetProperty("sample_id").GetInt64();
        Assert.True(sampleId > 0);
        Assert.Equal("桥接样本", created.GetProperty("name").GetString());
        Assert.False(created.TryGetProperty("content", out _));

        using var searchJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_search_style_samples",
              "method": "SearchStyleSamples",
              "payload": { "args": [{ "novel_id": {{novel.Id}}, "include_global": false, "query": "桥接", "tags": ["雨夜"], "page": 1, "size": 5 }] }
            }
            """));
        var page = searchJson.RootElement.GetProperty("result");
        Assert.Equal(1, page.GetProperty("total").GetInt64());
        Assert.Equal(sampleId, page.GetProperty("items")[0].GetProperty("sample_id").GetInt64());

        using var detailJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_get_style_sample",
              "method": "GetStyleSample",
              "payload": { "args": [{ "sample_id": {{sampleId}} }] }
            }
            """));
        Assert.Contains("别出声", detailJson.RootElement.GetProperty("result").GetProperty("content").GetString());

        using var invalidJson = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_style_sample",
              "method": "CreateStyleSample",
              "payload": { "args": [{ "novel_id": null, "is_global": false, "name": "bad", "content": "", "tags": [] }] }
            }
            """));
        AssertBridgeError(invalidJson.RootElement, "req_bad_style_sample", BridgeErrorCodes.ValidationError);
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

    private async ValueTask<NovelPayload> CreateNovelAsync(AppInitializationOptions options)
    {
        var settings = new FileSystemAppSettingsService(options);
        var novels = new FileSystemNovelService(options, settings);
        return await novels.CreateNovelAsync(new CreateNovelPayload("雨城档案", "测试作品", "悬疑"), CancellationToken.None);
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

    private static void AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
    }
}
