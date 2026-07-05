using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;
using Microsoft.Data.Sqlite;
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
    public async Task RebuildAnchorPreservesStableSourceSegmentIdsAndHashes()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("分段稳定测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "segments.md",
            """
            # 第一章

            第一句。

            第二句。第三句。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "分段参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var before = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.Contains(before, segment => segment.SegmentType == "chapter" && segment.Text.Contains("第一句", StringComparison.Ordinal));
        Assert.Contains(before, segment => segment.SegmentType == "paragraph" && segment.Text.Contains("第二句", StringComparison.Ordinal));
        Assert.Contains(before, segment => segment.SegmentType == "sentence" && segment.Text == "第三句。");
        var beforeSignature = before.Select(segment => segment.Signature).ToArray();

        await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        var after = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeSignature, after.Select(segment => segment.Signature).ToArray());
    }

    [Fact]
    public async Task CreateAnchorValidatesNovelIdAndSourceFile()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("校验测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchorService(options, novels);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await service.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(
                    0,
                    "参考",
                    null,
                    CreateSourceFile("valid.md", "第一句。"),
                    "markdown",
                    "user_provided"),
                CancellationToken.None));

        var missingSourcePath = Path.Combine(_root, "sources", "missing.md");
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(
                    novel.Id,
                    "缺失参考",
                    null,
                    missingSourcePath,
                    "markdown",
                    "user_provided"),
                CancellationToken.None));
        Assert.Contains("does not exist", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RebuildAnchorRecordsFailedImportStatusWithRedactedError()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("失败导入测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "第一句。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        File.Delete(sourcePath);

        var failed = await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failed.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failed.Stage);
        Assert.Equal(0, failed.SourceSegmentCount);
        Assert.Equal(0, failed.MaterialCount);
        Assert.Equal(0, failed.SlotCount);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));
        Assert.DoesNotContain(sourcePath, failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, failed.LastError, StringComparison.OrdinalIgnoreCase);

        var persisted = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, persisted.Status);
        Assert.Equal(failed.LastError, persisted.LastError);
    }

    [Fact]
    public async Task CreateAnchorProvisionsReferenceSpecificVectorsWhenEmbeddingConfigExists()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("参考向量测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-vector.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var embeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var provisioner = new RecordingSqliteVecTableProvisioner();
        var service = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "custom",
                "https://api.example.com/v1/embeddings",
                "test-key",
                "embed-v1",
                3,
                null)),
            embeddings,
            provisioner);

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "向量参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var status = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, status.Status);
        Assert.Equal("ready", status.Stage);
        Assert.True(status.MaterialCount > 0);
        Assert.Equal(status.MaterialCount, status.VectorCount);
        Assert.True(string.IsNullOrWhiteSpace(status.LastError));

        var request = Assert.Single(embeddings.Requests);
        Assert.Equal(status.MaterialCount, request.Count);
        Assert.Contains(request, text => text.Contains("雨声压低", StringComparison.Ordinal));
        Assert.Equal(BuiltinOnnxEmbeddingModel.DocumentInputKind, Assert.Single(embeddings.Options).InputKind);

        var provision = Assert.Single(provisioner.Provisions);
        Assert.Equal($"vec_reference_anchor_{anchor.AnchorId}_3", provision.TableName);
        Assert.Equal(3, provision.Dimensions);
        Assert.Equal(status.MaterialCount, provision.Vectors.Count);
        Assert.All(provision.Vectors, vector => Assert.StartsWith(anchor.AnchorId + ":material:", vector.ChunkId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAnchorRecordsFailedEmbeddingWhenSqliteVecUnavailable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("参考向量失败测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("reference-vector-failure.md", "雨声压低了整条街的呼吸。");
        var service = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "custom",
                "https://api.example.com/v1/embeddings",
                "test-key",
                "embed-v1",
                3,
                null)),
            new DeterministicEmbeddingClient(dimensions: 3),
            new FailingSqliteVecTableProvisioner("sqlite-vec native extension is unavailable."));

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "向量失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var status = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, anchor.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, status.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, status.Stage);
        Assert.True(status.MaterialCount > 0);
        Assert.Equal(0, status.VectorCount);
        Assert.Contains("sqlite-vec", status.LastError, StringComparison.OrdinalIgnoreCase);

        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "雨声",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.NotEmpty(materials.Items);
    }

    [Fact]
    public async Task RebuildAnchorReprovisionsReferenceVectorsWhenEmbeddingConfigExists()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("参考向量重建测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("reference-vector-rebuild.md", "第一句。");
        var embeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var provisioner = new RecordingSqliteVecTableProvisioner();
        var service = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "custom",
                "https://api.example.com/v1/embeddings",
                "test-key",
                "embed-v1",
                3,
                null)),
            embeddings,
            provisioner);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "重建向量参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        File.WriteAllText(sourcePath, "第一句。\n\n第二句带来新的雨声。");

        var rebuilt = await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, rebuilt.Status);
        Assert.True(rebuilt.MaterialCount > 0);
        Assert.Equal(rebuilt.MaterialCount, rebuilt.VectorCount);
        Assert.Equal(2, provisioner.Provisions.Count);
        Assert.All(provisioner.Provisions, provision => Assert.Equal($"vec_reference_anchor_{anchor.AnchorId}_3", provision.TableName));
        Assert.Equal(2, embeddings.Requests.Count);
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
    public async Task SearchMaterialsIncludesWorkspaceCorpusAnchorsWithoutLeakingOtherNovelPrivateAnchors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("共享语料目标", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("其他小说私有参考", "", ""), CancellationToken.None);
        var workspaceSourcePath = CreateSourceFile(
            "workspace-corpus.md",
            """
            # 第一章

            雨声压低了街道，他在门口停住，把那口气慢慢咽回去。
            """);
        var privateSourcePath = CreateSourceFile(
            "private-anchor.md",
            """
            # 第一章

            雨声压低了街道，但这里是另一部小说的私有参考。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var workspaceAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(targetNovel.Id, "工作区共享参考", null, workspaceSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(otherNovel.Id, "其他小说参考", null, privateSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, workspaceAnchor.AnchorId);

        var defaultSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [],
                Query: "门口",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Contains(defaultSearch.Items, item => item.AnchorId == workspaceAnchor.AnchorId);
        Assert.DoesNotContain(defaultSearch.Items, item => item.AnchorId == privateAnchor.AnchorId);

        var explicitWorkspaceSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [workspaceAnchor.AnchorId],
                Query: "门口",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Contains(explicitWorkspaceSearch.Items, item => item.AnchorId == workspaceAnchor.AnchorId);

        var explicitPrivateSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [privateAnchor.AnchorId],
                Query: "私有参考",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Empty(explicitPrivateSearch.Items);
    }

    [Fact]
    public async Task AdaptAndAuditCanUseWorkspaceCorpusMaterialsWithoutReadingOtherNovelPrivateMaterials()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("共享材料消费目标", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("隔离小说", "", ""), CancellationToken.None);
        var workspaceSourcePath = CreateSourceFile("workspace-slots.md", "他握住{{object}}，没有立刻说话。");
        var privateSourcePath = CreateSourceFile("private-slots.md", "他握住{{object}}，说出了另一部小说的秘密。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var workspaceAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(targetNovel.Id, "工作区槽位参考", null, workspaceSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(otherNovel.Id, "私有槽位参考", null, privateSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, workspaceAnchor.AnchorId);
        var workspaceMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [workspaceAnchor.AnchorId],
                Query: "{{object}}",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None)).Items);
        var privateMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                otherNovel.Id,
                AnchorIds: [privateAnchor.AnchorId],
                Query: "{{object}}",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None)).Items);

        var adapted = await service.AdaptMaterialAsync(
            new AdaptReferenceMaterialPayload(
                targetNovel.Id,
                workspaceMaterial.MaterialId,
                [new ReferenceSlotValuePayload("object", "门把手")],
                ReferenceRewriteLevels.L1,
                SceneFacts: ["门把手"]),
            CancellationToken.None);
        var audit = await service.AuditCandidateAsync(
            new AuditReferenceReusePayload(
                targetNovel.Id,
                workspaceMaterial.MaterialId,
                workspaceMaterial.Text,
                ReferenceRewriteLevels.L0,
                SceneFacts: []),
            CancellationToken.None);

        Assert.Equal("passed", adapted.Audit.Status);
        Assert.Equal("passed", audit.Status);
        var feedback = await service.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                targetNovel.Id,
                ReferenceFeedbackTargetTypes.ReuseCandidate,
                adapted.CandidateId,
                ReferenceFeedbackDecisions.Accepted,
                workspaceMaterial.MaterialId,
                adapted.CandidateId,
                BlueprintId: 0,
                BeatId: string.Empty,
                FeedbackTags: ["workspace_corpus_usage"],
                Note: "current novel accepts a workspace corpus candidate",
                EditedText: string.Empty,
                Origin: "user"),
            CancellationToken.None);
        Assert.Equal(targetNovel.Id, feedback.NovelId);
        Assert.Equal(workspaceMaterial.MaterialId, feedback.MaterialId);
        Assert.Equal(adapted.CandidateId, feedback.CandidateId);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.AdaptMaterialAsync(
                new AdaptReferenceMaterialPayload(
                    targetNovel.Id,
                    privateMaterial.MaterialId,
                    [new ReferenceSlotValuePayload("object", "门把手")],
                    ReferenceRewriteLevels.L1,
                    SceneFacts: ["门把手"]),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.AuditCandidateAsync(
                new AuditReferenceReusePayload(
                    targetNovel.Id,
                    privateMaterial.MaterialId,
                    "他握住门把手，说出了另一部小说的秘密。",
                    ReferenceRewriteLevels.L2,
                    SceneFacts: ["门把手"]),
                CancellationToken.None));
    }

    [Fact]
    public async Task SearchMaterialsTruncatesUnknownLicenseSourcePreviewButKeepsStoredText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("未知授权预览测试", "", ""), CancellationToken.None);
        var longSentence = "雨声压低了整条街的呼吸，周鸣在门口停了很久，钥匙在掌心硌出一点冷意，走廊尽头的灯反复闪烁，他没有立刻敲门，只把那口气慢慢咽回去。";
        var sourcePath = CreateSourceFile(
            "unknown-license.md",
            $$"""
            # 第一章

            {{longSentence}}
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "未知授权参考", null, sourcePath, "markdown", "unknown"),
            CancellationToken.None);

        var result = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "周鸣",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        var preview = Assert.Single(result.Items, item => item.MaterialType == ReferenceMaterialTypes.Sentence);
        Assert.NotEqual(longSentence, preview.Text);
        Assert.StartsWith("雨声压低了整条街的呼吸", preview.Text, StringComparison.Ordinal);
        Assert.EndsWith("...", preview.Text, StringComparison.Ordinal);
        Assert.True(preview.Text.Length < longSentence.Length);

        var stored = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Contains(stored, row => row.MaterialType == ReferenceMaterialTypes.Sentence && row.Text == longSentence);
    }

    [Fact]
    public async Task SearchMaterialsRanksLexicalMatchesAndBoundsPaginationWithoutEmbeddings()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("搜索排序分页测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "search-ranking.md",
            """
            # 第一章

            雨声压低了门口。

            他在门口停住。

            她说：别动。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "搜索排序参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var firstPage = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 1),
            CancellationToken.None);
        var secondPage = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 2,
                Size: 1),
            CancellationToken.None);
        var bounded = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 500),
            CancellationToken.None);

        Assert.Equal(2L, firstPage.Total);
        Assert.Equal(2, firstPage.TotalPages);
        var firstItem = Assert.Single(firstPage.Items);
        Assert.Equal("他在门口停住。", firstItem.Text);
        Assert.NotNull(firstItem.ScoreComponents);
        Assert.True(firstItem.ScoreComponents["lexical"] > 0);
        Assert.True(firstItem.ScoreComponents["material_type"] > 0);
        Assert.True(firstItem.ScoreComponents["confidence"] > 0);
        Assert.Equal("雨声压低了门口。", Assert.Single(secondPage.Items).Text);
        Assert.Equal(100, bounded.Size);
        Assert.Equal(2L, bounded.Total);
        Assert.Equal(2, bounded.Items.Count);
    }

    [Fact]
    public async Task SearchMaterialsUsesEmbeddingScoreWhenVectorIndexIsReady()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("向量材料搜索测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "search-embedding-ranking.md",
            """
            # 第一章

            雨声压低了门口。

            他在门口停住。
            """);
        var embeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var vec = new RecordingSqliteVecTableProvisioner();
        var service = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "custom",
                "https://api.example.com/v1/embeddings",
                "test-key",
                "embed-v1",
                3,
                null)),
            embeddings,
            vec);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "向量搜索参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var discovered = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        var rainMaterial = Assert.Single(discovered.Items, item => item.Text == "雨声压低了门口。");
        var doorwayMaterial = Assert.Single(discovered.Items, item => item.Text == "他在门口停住。");
        var vectors = Assert.Single(vec.Provisions).Vectors;
        var rainRowId = vectors.Single(vector => vector.ChunkId == rainMaterial.MaterialId).RowId;
        var doorwayRowId = vectors.Single(vector => vector.ChunkId == doorwayMaterial.MaterialId).RowId;
        vec.SearchRecords.Add(new SqliteVecSearchRecord(rainRowId, 0.02));
        vec.SearchRecords.Add(new SqliteVecSearchRecord(doorwayRowId, 0.60));

        var result = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Equal(2L, result.Total);
        Assert.Equal("雨声压低了门口。", result.Items[0].Text);
        var firstComponents = result.Items[0].ScoreComponents ?? throw new InvalidOperationException("Expected score components.");
        var secondComponents = result.Items[1].ScoreComponents ?? throw new InvalidOperationException("Expected score components.");
        Assert.True(firstComponents["embedding"] > secondComponents["embedding"]);
        Assert.Equal(BuiltinOnnxEmbeddingModel.QueryInputKind, embeddings.Options.Last().InputKind);
        Assert.Single(vec.SearchRequests);
    }

    [Fact]
    public async Task SearchMaterialsFallsBackWhenEmbeddingQueryFails()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("向量搜索降级测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "search-embedding-fallback.md",
            """
            # 第一章

            雨声压低了门口。

            他在门口停住。
            """);
        var embeddingOptions = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "test-key",
            "embed-v1",
            3,
            null);
        var vec = new RecordingSqliteVecTableProvisioner();
        var buildService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new DeterministicEmbeddingClient(dimensions: 3),
            vec);
        var anchor = await buildService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "向量降级参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var searchService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new FailingEmbeddingClient(),
            vec);

        var result = await searchService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Equal(2L, result.Total);
        Assert.Equal("他在门口停住。", result.Items[0].Text);
        var components = result.Items[0].ScoreComponents ?? throw new InvalidOperationException("Expected score components.");
        Assert.True(components["lexical"] > 0);
        Assert.DoesNotContain("embedding", components.Keys);
        Assert.Empty(vec.SearchRequests);
    }

    [Fact]
    public async Task SearchMaterialsDoesNotRequestEmbeddingWithoutReadyVectorIndex()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("无向量搜索测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "search-no-vector-index.md",
            """
            # 第一章

            雨声压低了门口。

            他在门口停住。
            """);
        var embeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var service = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "custom",
                "https://api.example.com/v1/embeddings",
                "test-key",
                "embed-v1",
                3,
                null)),
            embeddings,
            new FailingSqliteVecTableProvisioner("sqlite-vec native extension is unavailable."));
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "无向量参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var result = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Equal(2L, result.Total);
        Assert.Single(embeddings.Requests);
        var components = result.Items[0].ScoreComponents ?? throw new InvalidOperationException("Expected score components.");
        Assert.DoesNotContain("embedding", components.Keys);
    }

    [Fact]
    public async Task SearchMaterialsFiltersByNarrativeDutyEmotionTransitionPovTechniqueAndType()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("职责搜索过滤测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "search-duty-filters.md",
            """
            # 第一章

            雨声压低了街面！

            他心里记得那枚钥匙。

            她说：别回头。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "职责搜索参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var result = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                ["unknown"],
                ["sensory_detail"],
                Page: 1,
                Size: 10,
                NarrativeDuties: ["external_evidence"],
                EmotionTransitions: ["neutral->heightened"]),
            CancellationToken.None);

        var material = Assert.Single(result.Items);
        Assert.Equal("雨声压低了街面！", material.Text);
        Assert.Equal("environment", material.FunctionTag);
        Assert.Equal("heightened", material.EmotionTag);
        Assert.Equal("unknown", material.PovTag);
        Assert.Equal("sensory_detail", material.TechniqueTag);
        Assert.NotNull(material.ScoreComponents);
        Assert.True(material.ScoreComponents["narrative_duty"] > 0);
        Assert.True(material.ScoreComponents["emotion_transition"] > 0);
    }

    [Fact]
    public async Task SearchMaterialsMatchesSubtextDutyForObjectBasedExternalEvidence()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("潜台词外显证据测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "subtext-external-evidence.md",
            """
            # 第一章

            她只把杯子推远。

            她说：不用。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "潜台词外显证据参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var result = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10,
                NarrativeDuties: ["subtext"],
                EmotionTransitions: ["neutral->restrained"]),
            CancellationToken.None);

        var material = Assert.Single(result.Items);
        Assert.Equal("她只把杯子推远。", material.Text);
        Assert.Equal("emotion_evidence", material.FunctionTag);
        Assert.Equal("restrained", material.EmotionTag);
        Assert.Equal("external_evidence", material.TechniqueTag);
        Assert.NotNull(material.ScoreComponents);
        Assert.True(material.ScoreComponents["narrative_duty"] > 0);
        Assert.True(material.ScoreComponents["emotion_transition"] > 0);
    }

    [Fact]
    public async Task SearchMaterialsMatchesSubtextDutyForRestrainedObjectActionEvidence()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("克制物件动作证据测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "restrained-object-action.md",
            """
            # 第一章

            她只把钥匙放回桌面。

            她说：不用。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "克制物件动作参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var result = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10,
                NarrativeDuties: ["subtext"],
                EmotionTransitions: ["neutral->restrained"]),
            CancellationToken.None);

        var material = Assert.Single(result.Items);
        Assert.Equal("她只把钥匙放回桌面。", material.Text);
        Assert.Equal("emotion_evidence", material.FunctionTag);
        Assert.Equal("restrained", material.EmotionTag);
        Assert.Equal("external_evidence", material.TechniqueTag);
        Assert.NotNull(material.ScoreComponents);
        Assert.True(material.ScoreComponents["narrative_duty"] > 0);
        Assert.True(material.ScoreComponents["emotion_transition"] > 0);
    }

    [Fact]
    public async Task CreateAnchorPersistsMaterialProvenanceTagsAndSlots()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料来源测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "material-provenance.md",
            """
            # 第一章

            他心里记得{{object}}。

            雨声压低了街面。

            她说：“走吧。”
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "材料来源参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var rows = await ReadMaterialRowsAsync(options, anchor.AnchorId);

        Assert.NotEmpty(rows);
        Assert.All(rows, row =>
        {
            Assert.Equal(row.SourceSegmentId, row.JoinedSegmentId);
            Assert.False(string.IsNullOrWhiteSpace(row.MaterialId));
            Assert.False(string.IsNullOrWhiteSpace(row.FunctionTag));
            Assert.False(string.IsNullOrWhiteSpace(row.EmotionTag));
            Assert.False(string.IsNullOrWhiteSpace(row.PovTag));
            Assert.False(string.IsNullOrWhiteSpace(row.TechniqueTag));
            Assert.InRange(row.FunctionConfidence, 0, 1);
            Assert.InRange(row.EmotionConfidence, 0, 1);
            Assert.InRange(row.PovConfidence, 0, 1);
        });
        Assert.Contains(rows, row => row.MaterialType == ReferenceMaterialTypes.Sentence);
        Assert.Contains(rows, row => row.MaterialType == ReferenceMaterialTypes.Passage);
        Assert.Contains(rows, row => row.FunctionTag == "dialogue" && row.TechniqueTag == "dialogue_exchange");
        Assert.Contains(rows, row => row.PovTag == "close" && row.EmotionTag == "reflective");
        Assert.Contains(rows, row => row.TechniqueTag == "sensory_detail");
        Assert.Contains(rows, row => row.Text.Contains("{{object}}", StringComparison.Ordinal) && row.SlotCount > 0);
    }

    [Fact]
    public async Task MaterialTaggingAndAdaptationRemainDeterministicWithoutModelAssistedConfiguration()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("确定性材料测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "deterministic-material.md",
            """
            # 第一章

            林岚没有回答，喉咙却发紧。

            他握住{{object}}，没有立刻说话。
            """);
        var service = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(null),
            new FailingEmbeddingClient());

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "确定性材料参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status);
        var status = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(0, status.VectorCount);

        var rows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "林岚没有回答，喉咙却发紧。" &&
            row.FunctionTag == "emotion_evidence" &&
            row.EmotionTag == "restrained" &&
            row.TechniqueTag == "external_evidence");
        var slottedMaterial = Assert.Single(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "他握住{{object}}，没有立刻说话。");
        Assert.Equal(1, slottedMaterial.SlotCount);

        var adapted = await service.AdaptMaterialAsync(
            new AdaptReferenceMaterialPayload(
                novel.Id,
                slottedMaterial.MaterialId,
                [new ReferenceSlotValuePayload("object", "门把手")],
                ReferenceRewriteLevels.L1,
                SceneFacts: ["门把手"]),
            CancellationToken.None);

        Assert.Equal(ReferenceRewriteLevels.L1, adapted.RewriteLevel);
        Assert.Equal("他握住门把手，没有立刻说话。", adapted.Text);
        Assert.Empty(adapted.NonSlotEdits);
        Assert.Equal("passed", adapted.Audit.Status);
    }

    [Fact]
    public async Task CreateAnchorClassifiesChinesePunctuationDialogueParagraphAndNarrativeTags()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("中文提取标签测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "tag-cases.md",
            """
            # 第一章

            门外是谁？雨声压低了街面！

            他心里记得那枚钥匙。这时，脚步停在门口。

            她说：别回头。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "中文标签参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var rows = await ReadMaterialRowsAsync(options, anchor.AnchorId);

        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Passage &&
            row.Text == "门外是谁？雨声压低了街面！");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "门外是谁？" &&
            row.FunctionTag == "narration" &&
            row.EmotionTag == "uncertain" &&
            row.PovTag == "unknown");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "雨声压低了街面！" &&
            row.FunctionTag == "environment" &&
            row.EmotionTag == "heightened" &&
            row.TechniqueTag == "sensory_detail");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "他心里记得那枚钥匙。" &&
            row.FunctionTag == "interiority" &&
            row.EmotionTag == "reflective" &&
            row.PovTag == "close" &&
            row.TechniqueTag == "interiority");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "这时，脚步停在门口。" &&
            row.FunctionTag == "transition" &&
            row.TechniqueTag == "transition");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "她说：别回头。" &&
            row.FunctionTag == "dialogue" &&
            row.EmotionTag == "spoken" &&
            row.TechniqueTag == "dialogue_exchange");
    }

    [Fact]
    public async Task CreateAnchorClassifiesChineseEmotionEvidenceLimitedPovAndAfterbeatTags()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("中文情绪视角标签测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "emotion-pov-tag-cases.md",
            """
            # 第一章

            林岚没有回答，喉咙却发紧。

            周鸣看不见她袖口下的手指正慢慢发凉。

            林岚背对着门，没有回头。

            他移开目光，指尖在门框上停了一下。

            她欲言又止，指节慢慢扣紧。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "中文情绪视角参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var rows = await ReadMaterialRowsAsync(options, anchor.AnchorId);

        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "林岚没有回答，喉咙却发紧。" &&
            row.FunctionTag == "emotion_evidence" &&
            row.EmotionTag == "restrained" &&
            row.TechniqueTag == "external_evidence");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "周鸣看不见她袖口下的手指正慢慢发凉。" &&
            row.PovTag == "limited" &&
            row.TechniqueTag == "limited_pov");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "林岚背对着门，没有回头。" &&
            row.PovTag == "limited" &&
            row.TechniqueTag == "limited_pov");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "他移开目光，指尖在门框上停了一下。" &&
            row.FunctionTag == "action" &&
            row.TechniqueTag == "afterbeat");
        Assert.Contains(rows, row =>
            row.MaterialType == ReferenceMaterialTypes.Sentence &&
            row.Text == "她欲言又止，指节慢慢扣紧。" &&
            row.FunctionTag == "emotion_evidence" &&
            row.EmotionTag == "restrained" &&
            row.TechniqueTag == "external_evidence");
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
        var changedSlot = Assert.Single(adapted.ChangedSlots);
        Assert.Equal("object", changedSlot.SlotName);
        Assert.Equal("门把手", changedSlot.Value);
        Assert.Empty(adapted.NonSlotEdits);
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
    public async Task AdaptMaterialL1PreservesNonSlotPhrasesAndSourceOrder()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("多槽位改写测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "anchor.md",
            "他先握住{{object}}，旧誓言没有变，又把{{evidence}}压在掌心。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "多槽位参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "旧誓言",
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
                [
                    new ReferenceSlotValuePayload("evidence", "线索纸"),
                    new ReferenceSlotValuePayload("object", "门把手")
                ],
                ReferenceRewriteLevels.L1,
                SceneFacts: ["门把手", "线索纸"]),
            CancellationToken.None);

        Assert.Equal(ReferenceRewriteLevels.L1, adapted.RewriteLevel);
        Assert.Equal("他先握住门把手，旧誓言没有变，又把线索纸压在掌心。", adapted.Text);
        Assert.Contains("旧誓言没有变", adapted.Text, StringComparison.Ordinal);
        Assert.True(adapted.Text.IndexOf("门把手", StringComparison.Ordinal) < adapted.Text.IndexOf("线索纸", StringComparison.Ordinal));
        Assert.Empty(adapted.NonSlotEdits);
        Assert.Equal("passed", adapted.Audit.Status);
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
    public async Task AuditCandidateBlocksL3UnlessRequested()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("L3门禁测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "雨声压低了整条街的呼吸，林岚在门口停住。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "林岚",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var material = Assert.Single(materials.Items);
        const string l3Candidate = "雨声压着街口，林岚停在门前，迟迟没有开口。";

        var blocked = await service.AuditCandidateAsync(
            new AuditReferenceReusePayload(
                novel.Id,
                material.MaterialId,
                l3Candidate,
                ReferenceRewriteLevels.L1,
                SceneFacts: []),
            CancellationToken.None);
        var requested = await service.AuditCandidateAsync(
            new AuditReferenceReusePayload(
                novel.Id,
                material.MaterialId,
                l3Candidate,
                ReferenceRewriteLevels.L3,
                SceneFacts: []),
            CancellationToken.None);

        Assert.Equal(ReferenceRewriteLevels.L3, blocked.RewriteLevel);
        Assert.Equal("failed", blocked.Status);
        Assert.Contains(blocked.RequiredFixes, item => item.Contains("exceeds max rewrite level L1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ReferenceRewriteLevels.L3, requested.RewriteLevel);
        Assert.Equal("passed", requested.Status);
        Assert.Empty(requested.RequiredFixes);
    }

    [Fact]
    public async Task AuditCandidateFailsL4EvenWhenMaximumAllowsL4()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("L4门禁测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("anchor.md", "雨声压低了整条街的呼吸，林岚在门口停住。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "林岚",
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
                "桌上的茶已经凉透。",
                ReferenceRewriteLevels.L4,
                SceneFacts: []),
            CancellationToken.None);

        Assert.Equal(ReferenceRewriteLevels.L4, audit.RewriteLevel);
        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("L4 rewrite cannot pass", StringComparison.OrdinalIgnoreCase));
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
    public async Task BridgeReferenceAnchorHandlersReturnStableValidationErrorForInvalidPayload()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterReferenceAnchorHandlers(new SqliteReferenceAnchorService(options, novels));

        using var invalid = ParseOutbound(await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_bad_reference_anchor_args",
              "method": "GetReferenceAnchors",
              "payload": { "args": ["not-a-novel-id"] }
            }
            """));

        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());
        var error = invalid.RootElement.GetProperty("error");
        Assert.Equal(BridgeErrorCodes.ValidationError, error.GetProperty("code").GetString());
        Assert.Equal("Value must be an integer.", error.GetProperty("details").GetProperty("novelId").GetString());
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

    private static async ValueTask<IReadOnlyList<ReferenceSourceSegmentRow>> ReadSourceSegmentsAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, segment_type, segment_index, text_hash, text
            FROM reference_source_segments
            WHERE anchor_id = $anchor_id
            ORDER BY segment_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<ReferenceSourceSegmentRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ReferenceSourceSegmentRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterialRow>> ReadMaterialRowsAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.material_id, m.source_segment_id, m.material_type, m.function_tag,
                   m.emotion_tag, m.pov_tag, m.technique_tag, m.function_confidence,
                   m.emotion_confidence, m.pov_confidence, m.text, s.segment_id, COUNT(sl.slot_id)
            FROM reference_materials m
            INNER JOIN reference_source_segments s ON s.segment_id = m.source_segment_id
            LEFT JOIN reference_material_slots sl ON sl.material_id = m.material_id
            WHERE m.anchor_id = $anchor_id
            GROUP BY m.material_id, m.source_segment_id, m.material_type, m.function_tag,
                     m.emotion_tag, m.pov_tag, m.technique_tag, m.function_confidence,
                     m.emotion_confidence, m.pov_confidence, m.text, s.segment_id
            ORDER BY m.material_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<ReferenceMaterialRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ReferenceMaterialRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.GetDouble(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt64(12)));
        }

        return rows;
    }

    private static async ValueTask MarkAnchorAsWorkspaceCorpusAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_anchors
            SET novel_id = 0
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, updated);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private sealed record ReferenceSourceSegmentRow(
        string SegmentId,
        string SegmentType,
        int SegmentIndex,
        string TextHash,
        string Text)
    {
        public string Signature => string.Join('|', SegmentId, SegmentType, SegmentIndex, TextHash, Text);
    }

    private sealed record ReferenceMaterialRow(
        string MaterialId,
        string SourceSegmentId,
        string MaterialType,
        string FunctionTag,
        string EmotionTag,
        string PovTag,
        string TechniqueTag,
        double FunctionConfidence,
        double EmotionConfidence,
        double PovConfidence,
        string Text,
        string JoinedSegmentId,
        long SlotCount);

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

        public List<EmbeddingRequestOptions> Options { get; } = [];

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(inputs.ToArray());
            Options.Add(options);
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

    private sealed class FailingEmbeddingClient : IEmbeddingClient
    {
        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("query embedding failed");
        }
    }

    private sealed class RecordingSqliteVecTableProvisioner : ISqliteVecTableProvisioner, ISqliteVecQueryProvider
    {
        public List<SqliteVecProvisionRequest> Provisions { get; } = [];

        public List<SqliteVecSearchRequest> SearchRequests { get; } = [];

        public List<SqliteVecSearchRecord> SearchRecords { get; } = [];

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

        public ValueTask<IReadOnlyList<SqliteVecSearchRecord>> SearchAsync(
            string databasePath,
            SqliteVecSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchRequests.Add(request);
            return ValueTask.FromResult<IReadOnlyList<SqliteVecSearchRecord>>(
                SearchRecords.Take(request.TopK).ToArray());
        }
    }

    private sealed class FailingSqliteVecTableProvisioner : ISqliteVecTableProvisioner
    {
        private readonly string _message;

        public FailingSqliteVecTableProvisioner(string message)
        {
            _message = message;
        }

        public ValueTask ProvisionAsync(
            string databasePath,
            SqliteVecProvisionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(_message);
        }
    }
}
