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
    public async Task UpdateMaterialTagsMarksMaterialAsUserVerifiedAndSearchesByCorrectedTags()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("标签校正测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "他在门口停了很久。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "标签参考", null, sourcePath, "markdown", "user_provided"),
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

        var updated = await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                material.MaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "门口停顿其实用于近距离内心戏"),
            CancellationToken.None);

        Assert.Equal(material.MaterialId, updated.MaterialId);
        Assert.Equal("interiority", updated.FunctionTag);
        Assert.Equal("unease", updated.EmotionTag);
        Assert.Equal("threshold", updated.SceneTag);
        Assert.Equal("close", updated.PovTag);
        Assert.Equal("afterbeat", updated.TechniqueTag);
        Assert.Equal(1, updated.FunctionConfidence);
        Assert.Equal(1, updated.EmotionConfidence);
        Assert.Equal(1, updated.PovConfidence);
        Assert.True(updated.UserVerified);

        var reloaded = new SqliteReferenceAnchorService(options, novels);
        var corrected = await reloaded.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                EmotionTags: ["unease"],
                FunctionTags: ["interiority"],
                PovTags: ["close"],
                TechniqueTags: ["afterbeat"],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        var correctedMaterial = Assert.Single(corrected.Items);
        Assert.Equal(material.MaterialId, correctedMaterial.MaterialId);
        Assert.True(correctedMaterial.UserVerified);
    }

    [Fact]
    public async Task RebuildAnchorPreservesUserVerifiedTagsWhenMaterialHashIsUnchanged()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("重建保留校正测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "anchor.md",
            """
            前奏。

            他在门口停了很久。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "标签保留参考", null, sourcePath, "markdown", "user_provided"),
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

        await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                material.MaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "重建后仍应保留"),
            CancellationToken.None);
        File.WriteAllText(
            sourcePath,
            """
            新的开场。

            前奏。

            他在门口停了很久。
            """);

        await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        var corrected = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                EmotionTags: ["unease"],
                FunctionTags: ["interiority"],
                PovTags: ["close"],
                TechniqueTags: ["afterbeat"],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        var correctedMaterial = Assert.Single(corrected.Items);
        Assert.NotEqual(material.MaterialId, correctedMaterial.MaterialId);
        Assert.Equal("他在门口停了很久。", correctedMaterial.Text);
        Assert.True(correctedMaterial.UserVerified);
        Assert.Equal(1, correctedMaterial.FunctionConfidence);
        Assert.Equal(1, correctedMaterial.EmotionConfidence);
        Assert.Equal(1, correctedMaterial.PovConfidence);
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
    public async Task AuditCandidateReportsL2NonSlotEdits()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("L2报告测试", "", ""), CancellationToken.None);
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
                "他却在门口停了很久。",
                ReferenceRewriteLevels.L2,
                SceneFacts: []),
            CancellationToken.None);

        Assert.Equal("passed", audit.Status);
        Assert.Equal(ReferenceRewriteLevels.L2, audit.RewriteLevel);
        var edit = Assert.Single(audit.NonSlotEdits);
        Assert.Contains("却", edit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserFeedbackPersistsAcceptRejectAndEditDecisions()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("反馈测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "他握住{{object}}，没有立刻说话。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "反馈参考", null, sourcePath, "markdown", "user_provided"),
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

        var accepted = await service.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                novel.Id,
                ReferenceFeedbackTargetTypes.Material,
                material.MaterialId,
                ReferenceFeedbackDecisions.Accepted,
                material.MaterialId,
                CandidateId: "",
                BlueprintId: 0,
                BeatId: "",
                FeedbackTags: ["useful_reference"],
                Note: "可作为雨夜停顿参考",
                EditedText: "",
                Origin: "user"),
            CancellationToken.None);
        var rejected = await service.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                novel.Id,
                ReferenceFeedbackTargetTypes.ReuseCandidate,
                adapted.CandidateId,
                ReferenceFeedbackDecisions.Rejected,
                material.MaterialId,
                adapted.CandidateId,
                BlueprintId: 0,
                BeatId: "",
                FeedbackTags: ["too_ai_flavored"],
                Note: "节奏太像说明句",
                EditedText: "",
                Origin: "user"),
            CancellationToken.None);
        var edited = await service.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                novel.Id,
                ReferenceFeedbackTargetTypes.ReuseCandidate,
                adapted.CandidateId,
                ReferenceFeedbackDecisions.Edited,
                material.MaterialId,
                adapted.CandidateId,
                BlueprintId: 0,
                BeatId: "",
                FeedbackTags: ["manual_edit"],
                Note: "保留动作，改短后半句",
                EditedText: "他握住门把手。\n没有马上说话。",
                Origin: "user"),
            CancellationToken.None);

        Assert.Equal(ReferenceFeedbackDecisions.Accepted, accepted.Decision);
        Assert.Equal(ReferenceFeedbackDecisions.Rejected, rejected.Decision);
        Assert.Equal(ReferenceFeedbackDecisions.Edited, edited.Decision);
        Assert.True(string.IsNullOrEmpty(rejected.EditedTextHash));
        Assert.False(string.IsNullOrWhiteSpace(edited.EditedTextHash));

        var reloaded = new SqliteReferenceAnchorService(options, novels);
        var all = await reloaded.GetUserFeedbackAsync(
            new GetReferenceUserFeedbackPayload(novel.Id, TargetType: "", TargetId: "", Limit: 10),
            CancellationToken.None);

        Assert.Equal(3, all.Count);
        Assert.Contains(all, item => item.Decision == ReferenceFeedbackDecisions.Accepted && item.TargetId == material.MaterialId);
        Assert.Contains(all, item => item.Decision == ReferenceFeedbackDecisions.Rejected && item.TargetId == adapted.CandidateId);
        Assert.Contains(all, item => item.Decision == ReferenceFeedbackDecisions.Edited && item.EditedTextHash == edited.EditedTextHash);

        var candidateFeedback = await reloaded.GetUserFeedbackAsync(
            new GetReferenceUserFeedbackPayload(
                novel.Id,
                ReferenceFeedbackTargetTypes.ReuseCandidate,
                adapted.CandidateId,
                Limit: 10),
            CancellationToken.None);

        Assert.Equal(2, candidateFeedback.Count);
        Assert.All(candidateFeedback, item => Assert.Equal(adapted.CandidateId, item.TargetId));
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

    [Fact]
    public async Task BridgeReferenceAnchorHandlersRecordAndListUserFeedback()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("桥接反馈测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "他握住{{object}}，没有立刻说话。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "反馈参考", null, sourcePath, "markdown", "user_provided"),
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
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchorHandlers(service);

        using var recorded = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_record_feedback",
              "method": "RecordReferenceUserFeedback",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "target_type": "reuse_candidate",
                    "target_id": {{JsonSerializer.Serialize(adapted.CandidateId)}},
                    "decision": "edited",
                    "material_id": {{JsonSerializer.Serialize(material.MaterialId)}},
                    "candidate_id": {{JsonSerializer.Serialize(adapted.CandidateId)}},
                    "blueprint_id": 0,
                    "beat_id": "",
                    "feedback_tags": ["manual_edit"],
                    "note": "桥接记录一次人工修订",
                    "edited_text": "他握住门把手，没有马上说话。",
                    "origin": "user"
                  }
                ]
              }
            }
            """));

        Assert.True(recorded.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("edited", recorded.RootElement.GetProperty("result").GetProperty("decision").GetString());
        Assert.False(string.IsNullOrWhiteSpace(recorded.RootElement.GetProperty("result").GetProperty("edited_text_hash").GetString()));

        using var listed = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_get_feedback",
              "method": "GetReferenceUserFeedback",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "target_type": "reuse_candidate",
                    "target_id": {{JsonSerializer.Serialize(adapted.CandidateId)}},
                    "limit": 10
                  }
                ]
              }
            }
            """));

        Assert.True(listed.RootElement.GetProperty("ok").GetBoolean());
        var feedback = Assert.Single(listed.RootElement.GetProperty("result").EnumerateArray());
        Assert.Equal(recorded.RootElement.GetProperty("result").GetProperty("feedback_id").GetString(), feedback.GetProperty("feedback_id").GetString());
        Assert.Equal("manual_edit", feedback.GetProperty("feedback_tags")[0].GetString());
    }

    [Fact]
    public async Task BridgeReferenceAnchorHandlersUpdateMaterialTags()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("桥接标签测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "他在门口停了很久。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "标签参考", null, sourcePath, "markdown", "user_provided"),
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
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchorHandlers(service);

        using var updated = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_update_material_tags",
              "method": "UpdateReferenceMaterialTags",
              "payload": {
                "args": [
                  {
                    "novel_id": {{novel.Id}},
                    "material_id": {{JsonSerializer.Serialize(material.MaterialId)}},
                    "function_tag": "interiority",
                    "emotion_tag": "unease",
                    "scene_tag": "threshold",
                    "pov_tag": "close",
                    "technique_tag": "afterbeat",
                    "origin": "user",
                    "note": "bridge tag correction"
                  }
                ]
              }
            }
            """));

        Assert.True(updated.RootElement.GetProperty("ok").GetBoolean());
        var result = updated.RootElement.GetProperty("result");
        Assert.Equal(material.MaterialId, result.GetProperty("material_id").GetString());
        Assert.Equal("interiority", result.GetProperty("function_tag").GetString());
        Assert.Equal("unease", result.GetProperty("emotion_tag").GetString());
        Assert.True(result.GetProperty("user_verified").GetBoolean());
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
