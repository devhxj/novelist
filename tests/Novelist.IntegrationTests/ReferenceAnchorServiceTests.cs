using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;
using System.Text.Json;

namespace Novelist.IntegrationTests;

public sealed class ReferenceAnchorServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAnchorImportsSourceSegmentsAndPersistsBuildStatus()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("锚定测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "anchor.md",
            """
            # 第一章 雨夜

            他在门口停了很久。

            雨声压低了整条街的呼吸。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "雨夜参考",
                "作者",
                sourcePath,
                "markdown",
                "user_provided"),
            CancellationToken.None);

        Assert.Equal(novel.Id, anchor.NovelId);
        Assert.Equal("雨夜参考", anchor.Title);
        Assert.Equal("作者", anchor.Author);
        Assert.Equal(Path.GetFullPath(sourcePath), anchor.SourcePath);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status);
        Assert.False(string.IsNullOrWhiteSpace(anchor.SourceFileHash));

        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        var listed = Assert.Single(anchors);
        Assert.Equal(anchor.AnchorId, listed.AnchorId);

        var status = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, status.Status);
        Assert.Equal("ready", status.Stage);
        Assert.True(status.SourceSegmentCount >= 3);
        Assert.True(status.MaterialCount >= 2);
        Assert.Equal(0, status.SlotCount);
        Assert.True(string.IsNullOrEmpty(status.LastError));

        var reloaded = new SqliteReferenceAnchorService(options, novels);
        var reloadedStatus = await reloaded.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.Equal(status.SourceSegmentCount, reloadedStatus?.SourceSegmentCount);
    }

    [Fact]
    public async Task RebuildAnchorIsIdempotentForUnchangedSource()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("重建测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.txt", "第一句。\n\n第二句。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "参考", null, sourcePath, "text", "user_provided"),
            CancellationToken.None);

        var first = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        var rebuilt = await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, rebuilt.Status);
        Assert.Equal(first?.SourceSegmentCount, rebuilt.SourceSegmentCount);
        Assert.Equal(first?.MaterialCount, rebuilt.MaterialCount);
        Assert.True(string.IsNullOrEmpty(rebuilt.LastError));
    }

    [Fact]
    public async Task SearchMaterialsReturnsPagedDeterministicSentenceAndPassageMatches()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "anchor.md",
            """
            # 第一章

            他在门口停了很久。雨声压低了整条街的呼吸。

            她说：“你终于来了。”
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "材料参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var result = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "门口",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.True(result.Total >= 1);
        Assert.Equal(1, result.Page);
        Assert.Equal(10, result.Size);
        Assert.Contains(result.Items, item =>
            item.MaterialType == ReferenceMaterialTypes.Sentence &&
            item.Text.Contains("门口", StringComparison.Ordinal));
        Assert.All(result.Items, item => Assert.Equal(anchor.AnchorId, item.AnchorId));

        var dialogue = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "",
                MaterialTypes: [],
                EmotionTags: [],
                FunctionTags: ["dialogue"],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Contains(dialogue.Items, item => item.FunctionTag == "dialogue");
    }

    [Fact]
    public async Task AdaptMaterialAppliesDeclaredSlotsAndAuditsRewriteLevel()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("改写测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "他握住{{object}}，没有立刻说话。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "可替换参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "{{object}}",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var material = Assert.Single(materials.Items);

        var adapted = await service.AdaptMaterialAsync(
            new AdaptReferenceMaterialPayload(
                novel.Id,
                material.MaterialId,
                [new ReferenceSlotValuePayload("object", "门把手")],
                ReferenceRewriteLevels.L1,
                SceneFacts: ["门把手"]),
            CancellationToken.None);

        Assert.Equal(ReferenceRewriteLevels.L1, adapted.RewriteLevel);
        Assert.Equal("他握住门把手，没有立刻说话。", adapted.Text);
        Assert.Single(adapted.ChangedSlots);
        Assert.Equal("passed", adapted.Audit.Status);
        Assert.Empty(adapted.Audit.RequiredFixes);

        var status = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.True(status.SlotCount >= 1);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.AdaptMaterialAsync(
                new AdaptReferenceMaterialPayload(
                    novel.Id,
                    material.MaterialId,
                    [new ReferenceSlotValuePayload("undeclared", "钥匙")],
                    ReferenceRewriteLevels.L1,
                    SceneFacts: ["钥匙"]),
                CancellationToken.None));
    }

    [Fact]
    public async Task AuditCandidateFailsWhenRewriteLevelExceedsMaximum()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("审计测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "他在门口停了很久。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var material = Assert.Single(materials.Items);

        var audit = await service.AuditCandidateAsync(
            new AuditReferenceReusePayload(
                novel.Id,
                material.MaterialId,
                "他在门口停了片刻，复杂的情绪让命运的齿轮开始转动。",
                ReferenceRewriteLevels.L1,
                SceneFacts: []),
            CancellationToken.None);

        Assert.Equal("failed", audit.Status);
        Assert.True(audit.RewriteLevel is ReferenceRewriteLevels.L3 or ReferenceRewriteLevels.L4);
        Assert.NotEmpty(audit.RequiredFixes);
        Assert.NotEmpty(audit.AiProseRisks);
    }

    [Fact]
    public async Task CreateAnchorRejectsUnsupportedSourceFiles()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("校验测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.pdf", "not a text source");
        var service = new SqliteReferenceAnchorService(options, novels);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(novel.Id, "坏参考", null, sourcePath, "pdf", "user_provided"),
                CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAnchorRemovesAnchorAndStatus()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("删除测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "第一句。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        await service.DeleteAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Empty(await service.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Null(await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None));
    }

    [Fact]
    public async Task BridgeReferenceAnchorHandlersCreateAndListAnchors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("桥接测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "第一句。\n\n第二句。");
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchorHandlers(new SqliteReferenceAnchorService(options, novels));

        using var createJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_create_anchor",
              "method": "CreateReferenceAnchor",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "title": "桥接参考",
                    "author": null,
                    "source_path": {{JsonSerializer.Serialize(sourcePath)}},
                    "source_kind": "markdown",
                    "license_status": "user_provided"
                  }
                ]
              }
            }
            """));

        Assert.True(createJson.RootElement.GetProperty("ok").GetBoolean());
        var anchorId = createJson.RootElement.GetProperty("result").GetProperty("anchor_id").GetInt64();

        using var listJson = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_list_anchor",
              "method": "GetReferenceAnchors",
              "payload": { "args": [{{novel.Id}}] }
            }
            """));

        var anchors = listJson.RootElement.GetProperty("result");
        var anchor = Assert.Single(anchors.EnumerateArray());
        Assert.Equal(anchorId, anchor.GetProperty("anchor_id").GetInt64());
        Assert.Equal("桥接参考", anchor.GetProperty("title").GetString());
    }

    [Fact]
    public async Task BridgeReferenceAnchorHandlersAdaptAndAuditMaterials()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("桥接改写测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "他握住{{object}}，没有立刻说话。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "桥接材料", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "{{object}}",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var material = Assert.Single(materials.Items);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchorHandlers(service);

        using var adapted = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_adapt_reference",
              "method": "AdaptReferenceMaterial",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "material_id": {{JsonSerializer.Serialize(material.MaterialId)}},
                    "slot_values": [{ "slot_name": "object", "value": "门把手" }],
                    "max_rewrite_level": "L1",
                    "scene_facts": ["门把手"]
                  }
                ]
              }
            }
            """));

        Assert.True(adapted.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("L1", adapted.RootElement.GetProperty("result").GetProperty("rewrite_level").GetString());
        Assert.Equal("passed", adapted.RootElement.GetProperty("result").GetProperty("audit").GetProperty("status").GetString());

        using var audit = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_audit_reference",
              "method": "AuditReferenceReuse",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "material_id": {{JsonSerializer.Serialize(material.MaterialId)}},
                    "candidate_text": "他握住门把手，没有立刻说话。",
                    "max_rewrite_level": "L3",
                    "scene_facts": ["门把手"]
                  }
                ]
              }
            }
            """));

        Assert.True(audit.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("passed", audit.RootElement.GetProperty("result").GetProperty("status").GetString());
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

    private string CreateSourceFile(string fileName, string content)
    {
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
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
}
