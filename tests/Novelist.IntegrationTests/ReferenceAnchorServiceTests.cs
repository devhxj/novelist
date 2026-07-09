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
        Assert.Equal(ReferenceAnchorOwnerScopes.Novel, anchor.OwnerScope);
        Assert.Equal(novel.Id, anchor.OwnerNovelId);
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
    public async Task CreateAnchorValidatesNovelId()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
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
    }

    [Fact]
    public async Task CreateAnchorRecordsInitialFailedImportWithInspectableProcessingDetail()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("初始失败导入测试", "", ""), CancellationToken.None);
        var missingSourcePath = Path.Combine(_root, "sources", "initial-missing.md");
        var service = new SqliteReferenceAnchorService(options, novels);
        var input = new CreateReferenceAnchorPayload(
            novel.Id,
            "初始缺失参考",
            null,
            missingSourcePath,
            "markdown",
            "user_provided");

        var failed = await service.CreateAnchorAsync(input, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failed.Status);
        Assert.Equal(Path.GetFullPath(missingSourcePath), failed.SourcePath);
        Assert.StartsWith("unavailable:", failed.SourceFileHash, StringComparison.Ordinal);

        var status = await service.GetBuildStatusAsync(novel.Id, failed.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, status.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, status.Stage);
        Assert.Equal(0, status.SourceSegmentCount);
        Assert.Equal(0, status.MaterialCount);
        Assert.Equal(0, status.SlotCount);
        Assert.Equal(0, status.VectorCount);
        Assert.False(string.IsNullOrWhiteSpace(status.LastError));
        Assert.DoesNotContain(missingSourcePath, status.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, status.LastError, StringComparison.OrdinalIgnoreCase);

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failed.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, detail.CurrentStatus.Status);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.Contains(detail.Events, item =>
            item.Status == ReferenceAnchorBuildStates.FailedImport &&
            item.MaterialCount == 0 &&
            item.SourceSegmentCount == 0);

        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        var listed = Assert.Single(anchors);
        Assert.Equal(failed.AnchorId, listed.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, listed.Status);

        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [failed.AnchorId],
                "任何内容",
                [],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.Empty(materials.Items);

        Directory.CreateDirectory(Path.GetDirectoryName(missingSourcePath)!);
        await File.WriteAllTextAsync(missingSourcePath, "第一句。\n\n第二句。", CancellationToken.None);

        var duplicate = await service.CreateAnchorAsync(input, CancellationToken.None);
        Assert.Equal(failed.AnchorId, duplicate.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, duplicate.Status);

        var rebuilt = await service.RebuildAnchorAsync(novel.Id, failed.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, rebuilt.Status);
        Assert.True(rebuilt.MaterialCount > 0);
        var afterRebuild = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        Assert.Single(afterRebuild);
        Assert.Equal(failed.AnchorId, afterRebuild[0].AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, afterRebuild[0].Status);
    }

    [Fact]
    public async Task RebuildAnchorInitialFailedImportRetryFailureUpdatesDiagnosticWithoutFakeOutput()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("初始导入失败重试仍失败测试", "", ""), CancellationToken.None);
        var missingSourcePath = Path.Combine(_root, "sources", "initial-missing-retry.md");
        var service = new SqliteReferenceAnchorService(options, novels);
        var input = new CreateReferenceAnchorPayload(
            novel.Id,
            "初始缺失重试参考",
            null,
            missingSourcePath,
            "markdown",
            "user_provided");
        var failedAnchor = await service.CreateAnchorAsync(input, CancellationToken.None);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failedAnchor.Status);
        Assert.StartsWith("unavailable:", failedAnchor.SourceFileHash, StringComparison.Ordinal);
        Assert.Empty(await ReadSourceSegmentsAsync(options, failedAnchor.AnchorId));
        Assert.Empty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));
        await UpdateBuildStateErrorAsync(
            options,
            failedAnchor.AnchorId,
            "stale failed import diagnostic with prompt: hidden source_text: forbidden full_content: secret");

        var failedAgain = await service.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failedAgain.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failedAgain.Stage);
        Assert.Equal(0, failedAgain.SourceSegmentCount);
        Assert.Equal(0, failedAgain.MaterialCount);
        Assert.Equal(0, failedAgain.SlotCount);
        Assert.Equal(0, failedAgain.VectorCount);
        Assert.False(string.IsNullOrWhiteSpace(failedAgain.LastError));
        Assert.DoesNotContain("stale failed import diagnostic", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingSourcePath, failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(missingSourcePath), failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadSourceSegmentsAsync(options, failedAnchor.AnchorId));
        Assert.Empty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));
        var search = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [failedAnchor.AnchorId], "", [], [], [], [], [], 1, 10),
            CancellationToken.None);
        Assert.Empty(search.Items);

        var status = await service.GetBuildStatusAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, status.Status);
        Assert.Equal(failedAgain.LastError, status.LastError);
        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        var listed = Assert.Single(anchors);
        Assert.Equal(failedAnchor.AnchorId, listed.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, listed.Status);
        Assert.Equal(failedAnchor.SourceFileHash, listed.SourceFileHash);

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, detail.CurrentStatus.Status);
        Assert.Equal(failedAgain.LastError, detail.CurrentStatus.Diagnostic);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, detail.CurrentAttempt.Status);
        Assert.True(detail.CurrentAttempt.AttemptNumber >= 2);
        Assert.NotNull(detail.PriorAttempts);
        Assert.Contains(detail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedImport);
        Assert.True(detail.Events.Count(item => item.Status == ReferenceAnchorBuildStates.FailedImport) >= 2);
        var latestFailedEvent = detail.Events
            .Where(item => item.Status == ReferenceAnchorBuildStates.FailedImport)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.EventId, StringComparer.Ordinal)
            .First();
        Assert.Equal(0, latestFailedEvent.SourceSegmentCount);
        Assert.Equal(0, latestFailedEvent.MaterialCount);
        Assert.Empty(latestFailedEvent.AffectedMaterialId);
        Assert.Empty(latestFailedEvent.AffectedSegmentId);
        var detailSerialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingSourcePath, detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(missingSourcePath), detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, detailSerialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAnchorsWithResultReportsPerSourceFailuresAndKeepsSuccessfulImportsUsable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("批量局部失败测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("batch-ok.md", "第一句。\n\n第二句。");
        var missingSourcePath = Path.Combine(_root, "sources", "batch-missing.md");
        var service = new SqliteReferenceAnchorService(options, novels);
        var input = new CreateReferenceAnchorsPayload(
            [
                new CreateReferenceAnchorPayload(novel.Id, "成功参考", null, sourcePath, "markdown", "user_provided"),
                new CreateReferenceAnchorPayload(novel.Id, "缺失参考", null, missingSourcePath, "markdown", "user_provided")
            ]);

        var result = await service.CreateAnchorsWithResultAsync(input, CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        var succeeded = Assert.Single(result.Succeeded);
        Assert.Equal("成功参考", succeeded.Title);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, succeeded.Status);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(1, failure.Index);
        Assert.Equal("缺失参考", failure.Title);
        Assert.Equal("markdown", failure.SourceKind);
        Assert.StartsWith("source:", failure.SourceIdentity, StringComparison.Ordinal);
        Assert.True(failure.RetryAvailable);
        Assert.False(string.IsNullOrWhiteSpace(failure.Diagnostic));
        Assert.DoesNotContain(missingSourcePath, failure.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, failure.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(missingSourcePath, failure.SourceIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, failure.SourceIdentity, StringComparison.OrdinalIgnoreCase);

        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [succeeded.AnchorId],
                "第一句",
                [],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.NotEmpty(materials.Items);

        Directory.CreateDirectory(Path.GetDirectoryName(missingSourcePath)!);
        await File.WriteAllTextAsync(missingSourcePath, "第三句。\n\n第四句。", CancellationToken.None);

        var retry = await service.CreateAnchorsWithResultAsync(input, CancellationToken.None);

        Assert.Equal(2, retry.TotalCount);
        Assert.Equal(1, retry.SucceededCount);
        Assert.Equal(1, retry.FailedCount);
        Assert.Contains(retry.Succeeded, anchor => anchor.AnchorId == succeeded.AnchorId);
        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        Assert.Equal(2, anchors.Count);
        Assert.Equal(2, anchors.Select(anchor => anchor.AnchorId).Distinct().Count());
        var failedAnchor = Assert.Single(anchors, anchor => anchor.Status == ReferenceAnchorBuildStates.FailedImport);

        var rebuilt = await service.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, rebuilt.Status);

        var afterExplicitRetry = await service.CreateAnchorsWithResultAsync(input, CancellationToken.None);
        Assert.Equal(2, afterExplicitRetry.TotalCount);
        Assert.Equal(2, afterExplicitRetry.SucceededCount);
        Assert.Equal(0, afterExplicitRetry.FailedCount);
        Assert.Empty(afterExplicitRetry.Failed);
        var afterRetryAnchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        Assert.Equal(2, afterRetryAnchors.Count);
        Assert.All(afterRetryAnchors, anchor => Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status));
    }

    [Fact]
    public async Task RebuildAnchorRecordsFailedImportStatusWithRedactedError()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("失败导入测试", "", ""), CancellationToken.None);
        const string sourceText = "第一句。\n\n第二句。\n\n第三句。";
        var sourcePath = CreateSourceFile("anchor.md", sourceText);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.True(beforeRows.Count >= 2);
        var correctedMaterialId = beforeRows[0].MaterialId;
        var archivedMaterialId = beforeRows[^1].MaterialId;
        await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "failed import rebuild must preserve correction"),
            CancellationToken.None);
        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [archivedMaterialId]),
            CancellationToken.None);
        beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var correctedBefore = Assert.Single(beforeRows, row => row.MaterialId == correctedMaterialId);
        Assert.True(correctedBefore.UserVerified);
        Assert.Equal("interiority", correctedBefore.FunctionTag);

        var beforeActive = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.NotEmpty(beforeActive.Items);
        Assert.Contains(beforeActive.Items, item => item.MaterialId == correctedMaterialId);
        Assert.DoesNotContain(beforeActive.Items, item => item.MaterialId == archivedMaterialId);
        File.Delete(sourcePath);

        var failed = await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failed.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failed.Stage);
        Assert.Equal(beforeRows.Count, failed.MaterialCount);
        Assert.True(failed.SourceSegmentCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(failed.LastError));
        Assert.DoesNotContain(sourcePath, failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", failed.LastError, StringComparison.OrdinalIgnoreCase);

        var persisted = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, persisted.Status);
        Assert.Equal(failed.LastError, persisted.LastError);
        Assert.Equal(failed.SourceSegmentCount, persisted.SourceSegmentCount);
        Assert.Equal(failed.MaterialCount, persisted.MaterialCount);

        var afterRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        Assert.Equal(afterRows.Count, afterRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var correctedAfter = Assert.Single(afterRows, row => row.MaterialId == correctedMaterialId);
        Assert.True(correctedAfter.UserVerified);
        Assert.Equal("interiority", correctedAfter.FunctionTag);
        Assert.Equal("unease", correctedAfter.EmotionTag);
        Assert.Equal("close", correctedAfter.PovTag);
        Assert.Equal("afterbeat", correctedAfter.TechniqueTag);

        var afterActive = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.Equal(beforeActive.Items.Select(item => item.MaterialId), afterActive.Items.Select(item => item.MaterialId));
        Assert.Contains(afterActive.Items, item => item.MaterialId == correctedMaterialId);
        Assert.DoesNotContain(afterActive.Items, item => item.MaterialId == archivedMaterialId);

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(failed.SourceSegmentCount, detail.CurrentStatus.SourceSegmentCount);
        Assert.Equal(failed.MaterialCount, detail.CurrentStatus.MaterialCount);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
        var failedEvent = Assert.Single(
            detail.Events,
            item => item.Status == ReferenceAnchorBuildStates.FailedImport &&
                item.SourceSegmentCount == failed.SourceSegmentCount &&
                item.MaterialCount == failed.MaterialCount);
        Assert.NotEmpty(failedEvent.AffectedMaterialId);
        Assert.NotEmpty(failedEvent.AffectedSegmentId);
        Assert.Contains(afterRows, row => row.MaterialId == failedEvent.AffectedMaterialId);
        var affectedDetail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, failedEvent.AffectedMaterialId),
            CancellationToken.None);
        Assert.NotNull(affectedDetail);
        Assert.Equal(failedEvent.AffectedSegmentId, affectedDetail.Material.SourceSegmentId);
        Assert.Equal(failedEvent.AffectedMaterialId, affectedDetail.Material.MaterialId);
        Assert.NotEmpty(affectedDetail.ProcessingNotes);
        var archivedDetail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, archivedMaterialId),
            CancellationToken.None);
        Assert.NotNull(archivedDetail);
        Assert.Equal(ReferenceMaterialArchiveFilters.Archived, archivedDetail.Material.ArchiveState);
        Assert.NotNull(archivedDetail.Material.ArchivedAt);
        var sourceSegmentDetail = await service.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(novel.Id, anchor.AnchorId, failedEvent.AffectedSegmentId),
            CancellationToken.None);
        Assert.NotNull(sourceSegmentDetail);
        Assert.Equal(failedEvent.AffectedSegmentId, sourceSegmentDetail.Segment.SegmentId);
        var serialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sourcePath, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_root, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RebuildAnchorRecoversFailedImportAfterSourceRestoredAndKeepsHistory()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("失败导入恢复测试", "", ""), CancellationToken.None);
        const string sourceText = "第一句。\n\n第二句。";
        var sourcePath = CreateSourceFile("failed-import-recovery.md", sourceText);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "失败导入恢复参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.NotEmpty(beforeRows);

        File.Delete(sourcePath);
        var failed = await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failed.Status);
        File.WriteAllText(sourcePath, sourceText);

        var recovered = await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Stage);
        Assert.True(string.IsNullOrEmpty(recovered.LastError));
        Assert.Equal(beforeRows.Count, recovered.MaterialCount);
        var afterRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        Assert.Equal(afterRows.Count, afterRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        var status = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, status.Status);
        Assert.True(string.IsNullOrEmpty(status.LastError));

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, detail.CurrentStatus.Status);
        Assert.False(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, detail.CurrentAttempt.Status);
        Assert.True(detail.CurrentAttempt.AttemptNumber >= 2);
        Assert.NotEmpty(detail.CurrentAttempt.AttemptId);
        Assert.NotEmpty(detail.CurrentAttempt.BuildId);
        Assert.NotEmpty(detail.CurrentAttempt.RecoveredFromAttemptId);
        Assert.NotEmpty(detail.CurrentAttempt.RecoveredFromBuildId);
        Assert.NotNull(detail.PriorAttempts);
        Assert.Contains(detail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedImport);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedImport);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
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

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        var readyEvent = Assert.Single(
            detail.Events,
            item => item.Status == ReferenceAnchorBuildStates.Ready && item.VectorCount == status.VectorCount);
        Assert.NotEmpty(readyEvent.AffectedMaterialId);
        Assert.NotEmpty(readyEvent.AffectedSegmentId);
        Assert.Equal(anchor.AnchorId.ToString(System.Globalization.CultureInfo.InvariantCulture), readyEvent.AffectedSourceId);
        var affectedDetail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, readyEvent.AffectedMaterialId),
            CancellationToken.None);
        Assert.NotNull(affectedDetail);
        Assert.Equal(readyEvent.AffectedMaterialId, affectedDetail.Material.MaterialId);
        Assert.Equal(readyEvent.AffectedSegmentId, affectedDetail.Material.SourceSegmentId);
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

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        var failedEvent = Assert.Single(
            detail.Events,
            item => item.Status == ReferenceAnchorBuildStates.FailedEmbedding);
        Assert.NotEmpty(failedEvent.AffectedMaterialId);
        Assert.NotEmpty(failedEvent.AffectedSegmentId);
        var failedAffectedDetail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, failedEvent.AffectedMaterialId),
            CancellationToken.None);
        Assert.NotNull(failedAffectedDetail);
        Assert.Equal(failedEvent.AffectedSegmentId, failedAffectedDetail.Material.SourceSegmentId);

        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "雨声",
                [],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.NotEmpty(materials.Items);

        var recoveringEmbeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var recoveringProvisioner = new RecordingSqliteVecTableProvisioner();
        var duplicateService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(new EmbeddingRequestOptions(
                "custom",
                "https://api.example.com/v1/embeddings",
                "test-key",
                "embed-v1",
                3,
                null)),
            recoveringEmbeddings,
            recoveringProvisioner);
        var duplicate = await duplicateService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "向量失败重复导入", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(anchor.AnchorId, duplicate.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, duplicate.Status);
        Assert.Empty(recoveringEmbeddings.Requests);
        Assert.Empty(recoveringProvisioner.Provisions);
    }

    [Fact]
    public async Task CreateAnchorRecoversInterruptedEmbeddingStageForDuplicateImportAfterRestart()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("向量中断恢复测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-vector-interrupted.md",
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
        var interruptedService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new CancelingEmbeddingClient(),
            new RecordingSqliteVecTableProvisioner());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await interruptedService.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(novel.Id, "中断向量参考", null, sourcePath, "markdown", "user_provided"),
                CancellationToken.None));

        var interruptedAnchor = Assert.Single(await interruptedService.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(ReferenceAnchorBuildStates.Embedding, interruptedAnchor.Status);
        var rowsBeforeRecovery = await ReadMaterialRowsAsync(options, interruptedAnchor.AnchorId);
        Assert.NotEmpty(rowsBeforeRecovery);
        var correctedMaterialId = rowsBeforeRecovery[0].MaterialId;
        await interruptedService.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "restart recovery must preserve manual correction"),
            CancellationToken.None);

        var embeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var provisioner = new RecordingSqliteVecTableProvisioner();
        var restartedNovels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var restartedService = new SqliteReferenceAnchorService(
            options,
            restartedNovels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            embeddings,
            provisioner);

        var recovered = await restartedService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "重复导入恢复", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(interruptedAnchor.AnchorId, recovered.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Status);
        var recoveredStatus = await restartedService.GetBuildStatusAsync(novel.Id, recovered.AnchorId, CancellationToken.None);
        Assert.NotNull(recoveredStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredStatus.Status);
        Assert.Equal(recoveredStatus.MaterialCount, recoveredStatus.VectorCount);
        var rowsAfterRecovery = await ReadMaterialRowsAsync(options, recovered.AnchorId);
        Assert.Equal(rowsBeforeRecovery.Select(row => row.MaterialId), rowsAfterRecovery.Select(row => row.MaterialId));
        var corrected = Assert.Single(rowsAfterRecovery, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);
        Assert.Single(await restartedService.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Single(provisioner.Provisions);
        Assert.Single(embeddings.Requests);
    }

    [Theory]
    [InlineData(ReferenceAnchorBuildStates.SegmentsBuilt)]
    [InlineData(ReferenceAnchorBuildStates.ExtractingMaterials)]
    [InlineData(ReferenceAnchorBuildStates.MaterialsExtracted)]
    [InlineData(ReferenceAnchorBuildStates.DetectingSlots)]
    [InlineData(ReferenceAnchorBuildStates.SlotsDetected)]
    [InlineData(ReferenceAnchorBuildStates.Stale)]
    public async Task CreateAnchorRecoversPreEmbeddingStageForDuplicateImportWithoutDuplicateMaterials(string recoverableStatus)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload($"重复导入阶段恢复 {recoverableStatus}", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            $"duplicate-pre-embedding-{recoverableStatus}.md",
            """
            # 第一章

            雨声压低了门口。

            林岚握住钥匙，没有立刻说话。

            她把那句解释咽回去，只听街灯在水里发颤。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, $"重复导入恢复 {recoverableStatus}", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status);
        var initialRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var initialSentenceRows = initialRows
            .Where(row => row.MaterialType == ReferenceMaterialTypes.Sentence)
            .ToArray();
        Assert.True(initialSentenceRows.Length >= 2);
        var correctedMaterialId = initialSentenceRows[0].MaterialId;
        var archivedMaterialId = initialSentenceRows[^1].MaterialId;
        await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "duplicate import recovery must preserve correction"),
            CancellationToken.None);
        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [archivedMaterialId]),
            CancellationToken.None);
        var beforeRecoveryRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Equal(initialRows.Select(row => row.MaterialId), beforeRecoveryRows.Select(row => row.MaterialId));
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        await UpdateAnchorBuildStatusAsync(options, anchor.AnchorId, recoverableStatus, recoverableStatus);

        var duplicateService = new SqliteReferenceAnchorService(options, novels);

        var recovered = await duplicateService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, $"重复导入触发恢复 {recoverableStatus}", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(anchor.AnchorId, recovered.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Status);
        var recoveredStatus = await duplicateService.GetBuildStatusAsync(novel.Id, recovered.AnchorId, CancellationToken.None);
        Assert.NotNull(recoveredStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredStatus.Status);
        Assert.True(string.IsNullOrEmpty(recoveredStatus.LastError));
        var afterRecoveryRows = await ReadMaterialRowsAsync(options, recovered.AnchorId);
        Assert.Equal(beforeRecoveryRows.Select(row => row.MaterialId), afterRecoveryRows.Select(row => row.MaterialId));
        Assert.Equal(afterRecoveryRows.Count, afterRecoveryRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var corrected = Assert.Single(afterRecoveryRows, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);

        var defaultSearch = await duplicateService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                AnchorIds: [],
                Query: "",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 20),
            CancellationToken.None);
        Assert.Contains(defaultSearch.Items, item => item.MaterialId == correctedMaterialId);
        Assert.DoesNotContain(defaultSearch.Items, item => item.MaterialId == archivedMaterialId);
        Assert.Single(await duplicateService.GetAnchorsAsync(novel.Id, CancellationToken.None));

        var detail = await duplicateService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, recovered.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, detail.CurrentStatus.Status);
        Assert.Contains(detail.Events, item => item.Status == recoverableStatus);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, detail.CurrentAttempt.Status);
        Assert.True(detail.CurrentAttempt.AttemptNumber >= 1);
        Assert.True(detail.CurrentAttempt.EventCount > 0);
    }

    [Fact]
    public async Task StartupInitializationRecoversInterruptedEmbeddingStageWithoutDuplicateImport()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("启动主动恢复测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-vector-startup-recovery.md",
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
        var interruptedService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new CancelingEmbeddingClient(),
            new RecordingSqliteVecTableProvisioner());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await interruptedService.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(novel.Id, "启动恢复参考", null, sourcePath, "markdown", "user_provided"),
                CancellationToken.None));

        var interruptedAnchor = Assert.Single(await interruptedService.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(ReferenceAnchorBuildStates.Embedding, interruptedAnchor.Status);
        var rowsBeforeRecovery = await ReadMaterialRowsAsync(options, interruptedAnchor.AnchorId);
        Assert.NotEmpty(rowsBeforeRecovery);
        var correctedMaterialId = rowsBeforeRecovery[0].MaterialId;
        await interruptedService.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "startup recovery must preserve manual correction"),
            CancellationToken.None);

        var startupEmbeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var startupProvisioner = new RecordingSqliteVecTableProvisioner();
        var startupRecovery = new SqliteReferenceAnchorService(
            options,
            new FileSystemNovelService(options, new FileSystemAppSettingsService(options)),
            new StaticEmbeddingConfigurationService(embeddingOptions),
            startupEmbeddings,
            startupProvisioner);
        var initialization = new FileSystemAppInitializationService(
            options,
            referenceAnchorRecovery: startupRecovery);

        Assert.True(await initialization.IsInitializedAsync(CancellationToken.None));

        var recoveredAnchor = Assert.Single(await startupRecovery.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(interruptedAnchor.AnchorId, recoveredAnchor.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredAnchor.Status);
        var recoveredStatus = await startupRecovery.GetBuildStatusAsync(novel.Id, recoveredAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(recoveredStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredStatus.Status);
        Assert.Equal(recoveredStatus.MaterialCount, recoveredStatus.VectorCount);
        var rowsAfterRecovery = await ReadMaterialRowsAsync(options, recoveredAnchor.AnchorId);
        Assert.Equal(rowsBeforeRecovery.Select(row => row.MaterialId), rowsAfterRecovery.Select(row => row.MaterialId));
        var corrected = Assert.Single(rowsAfterRecovery, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);
        Assert.Single(startupProvisioner.Provisions);
        Assert.Single(startupEmbeddings.Requests);
        var detail = await startupRecovery.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, recoveredAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Embedding);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
    }

    [Fact]
    public async Task StartupInitializationRecoversWorkspaceCorpusEmbeddingStageAndKeepsSearchableState()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("启动恢复共享语料来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("启动恢复共享语料使用方", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "workspace-startup-recovery.md",
            """
            # 第一章

            雨声压低了门口。

            林岚握住钥匙，没有立刻说话。

            她把那句解释咽回去，只听街灯在水里发颤。
            """);
        var embeddingOptions = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "test-key",
            "embed-v1",
            3,
            null);
        var interruptedService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new CancelingEmbeddingClient(),
            new RecordingSqliteVecTableProvisioner());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await interruptedService.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(
                    ownerNovel.Id,
                    "启动恢复共享参考",
                    null,
                    sourcePath,
                    "markdown",
                    "user_provided",
                    ReferenceCorpusVisibilities.Workspace),
                CancellationToken.None));

        var interruptedAnchor = Assert.Single(await interruptedService.GetAnchorsAsync(consumingNovel.Id, CancellationToken.None));
        Assert.Equal(0, interruptedAnchor.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, interruptedAnchor.OwnerScope);
        Assert.Equal(ReferenceAnchorBuildStates.Embedding, interruptedAnchor.Status);
        Assert.Null(await ReadAnchorStoredNovelIdAsync(options, interruptedAnchor.AnchorId));
        var sentenceMaterials = await interruptedService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [interruptedAnchor.AnchorId],
                Query: "",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.True(sentenceMaterials.Items.Count >= 2);
        var archivedMaterialId = sentenceMaterials.Items[0].MaterialId;
        var correctedMaterialId = sentenceMaterials.Items[1].MaterialId;

        await interruptedService.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(consumingNovel.Id, [archivedMaterialId]),
            CancellationToken.None);
        await interruptedService.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                consumingNovel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "workspace startup recovery must preserve correction"),
            CancellationToken.None);
        var rowsBeforeRecovery = await ReadMaterialRowsAsync(options, interruptedAnchor.AnchorId);
        Assert.NotEmpty(rowsBeforeRecovery);
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));

        var startupEmbeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var startupProvisioner = new RecordingSqliteVecTableProvisioner();
        var startupRecovery = new SqliteReferenceAnchorService(
            options,
            new FileSystemNovelService(options, new FileSystemAppSettingsService(options)),
            new StaticEmbeddingConfigurationService(embeddingOptions),
            startupEmbeddings,
            startupProvisioner);
        var initialization = new FileSystemAppInitializationService(
            options,
            referenceAnchorRecovery: startupRecovery);

        Assert.True(await initialization.IsInitializedAsync(CancellationToken.None));

        var recoveredAnchor = Assert.Single(await startupRecovery.GetAnchorsAsync(consumingNovel.Id, CancellationToken.None));
        Assert.Equal(interruptedAnchor.AnchorId, recoveredAnchor.AnchorId);
        Assert.Equal(0, recoveredAnchor.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, recoveredAnchor.OwnerScope);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredAnchor.Status);
        var recoveredStatus = await startupRecovery.GetBuildStatusAsync(consumingNovel.Id, recoveredAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(recoveredStatus);
        Assert.Equal(0, recoveredStatus.NovelId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredStatus.Status);
        var rowsAfterRecovery = await ReadMaterialRowsAsync(options, recoveredAnchor.AnchorId);
        Assert.Equal(rowsBeforeRecovery.Select(row => row.MaterialId), rowsAfterRecovery.Select(row => row.MaterialId));
        Assert.Equal(rowsAfterRecovery.Count, rowsAfterRecovery.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var corrected = Assert.Single(rowsAfterRecovery, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);
        var activeMaterialCount = 0;
        foreach (var row in rowsAfterRecovery)
        {
            if (await ReadMaterialArchivedAtAsync(options, row.MaterialId) is null)
            {
                activeMaterialCount++;
            }
        }

        Assert.Equal(activeMaterialCount, recoveredStatus.VectorCount);
        Assert.Single(startupProvisioner.Provisions);
        Assert.Single(startupEmbeddings.Requests);
        Assert.Equal(activeMaterialCount, startupEmbeddings.Requests[0].Count);
        var defaultSearch = await startupRecovery.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [],
                Query: "",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Contains(defaultSearch.Items, item => item.MaterialId == correctedMaterialId);
        Assert.DoesNotContain(defaultSearch.Items, item => item.MaterialId == archivedMaterialId);
        Assert.All(defaultSearch.Items, item => Assert.Equal(recoveredAnchor.AnchorId, item.AnchorId));

        var detail = await startupRecovery.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(consumingNovel.Id, recoveredAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Embedding);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
    }

    [Theory]
    [InlineData(ReferenceAnchorBuildStates.Created)]
    [InlineData(ReferenceAnchorBuildStates.Importing)]
    [InlineData(ReferenceAnchorBuildStates.SourceImported)]
    [InlineData(ReferenceAnchorBuildStates.Segmenting)]
    [InlineData(ReferenceAnchorBuildStates.SegmentsBuilt)]
    [InlineData(ReferenceAnchorBuildStates.ExtractingMaterials)]
    [InlineData(ReferenceAnchorBuildStates.MaterialsExtracted)]
    [InlineData(ReferenceAnchorBuildStates.DetectingSlots)]
    [InlineData(ReferenceAnchorBuildStates.SlotsDetected)]
    [InlineData(ReferenceAnchorBuildStates.Stale)]
    public async Task StartupInitializationRecoversRecoverablePreEmbeddingStageWithoutDuplicateMaterials(string recoverableStatus)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload($"启动阶段恢复-{recoverableStatus}", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            $"startup-recovery-{recoverableStatus}.md",
            """
            # 第一章

            雨声压低了门口。

            他在门口停住。

            他把伞收紧，没有回头。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, $"启动阶段恢复 {recoverableStatus}", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var initialRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.True(initialRows.Count >= 2);
        var correctedMaterialId = initialRows[0].MaterialId;
        var archivedMaterialId = initialRows[^1].MaterialId;
        await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "startup stage recovery must preserve correction"),
            CancellationToken.None);
        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [archivedMaterialId]),
            CancellationToken.None);
        var rowsBeforeRecovery = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Equal(initialRows.Select(row => row.MaterialId), rowsBeforeRecovery.Select(row => row.MaterialId));
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        await UpdateAnchorBuildStatusAsync(options, anchor.AnchorId, recoverableStatus, recoverableStatus);

        var startupRecovery = new SqliteReferenceAnchorService(
            options,
            new FileSystemNovelService(options, new FileSystemAppSettingsService(options)));
        var initialization = new FileSystemAppInitializationService(
            options,
            referenceAnchorRecovery: startupRecovery);

        Assert.True(await initialization.IsInitializedAsync(CancellationToken.None));

        var recoveredAnchor = Assert.Single(await startupRecovery.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(anchor.AnchorId, recoveredAnchor.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredAnchor.Status);
        var recoveredStatus = await startupRecovery.GetBuildStatusAsync(novel.Id, recoveredAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(recoveredStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredStatus.Status);
        var rowsAfterRecovery = await ReadMaterialRowsAsync(options, recoveredAnchor.AnchorId);
        Assert.Equal(rowsBeforeRecovery.Select(row => row.MaterialId), rowsAfterRecovery.Select(row => row.MaterialId));
        Assert.Equal(rowsAfterRecovery.Count, rowsAfterRecovery.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var corrected = Assert.Single(rowsAfterRecovery, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);
        var activeMaterialCount = 0;
        foreach (var row in rowsAfterRecovery)
        {
            if (await ReadMaterialArchivedAtAsync(options, row.MaterialId) is null)
            {
                activeMaterialCount++;
            }
        }

        var defaultSearch = await startupRecovery.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                AnchorIds: [],
                Query: "",
                MaterialTypes: [],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 100),
            CancellationToken.None);
        Assert.Equal(activeMaterialCount, defaultSearch.Items.Count);
        Assert.Contains(defaultSearch.Items, item => item.MaterialId == correctedMaterialId);
        Assert.DoesNotContain(defaultSearch.Items, item => item.MaterialId == archivedMaterialId);
        var detail = await startupRecovery.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, recoveredAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.Status == recoverableStatus);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
    }

    [Theory]
    [InlineData(ReferenceAnchorBuildStates.Created)]
    [InlineData(ReferenceAnchorBuildStates.Importing)]
    [InlineData(ReferenceAnchorBuildStates.SourceImported)]
    [InlineData(ReferenceAnchorBuildStates.Segmenting)]
    public async Task StartupInitializationRecoversEarlyRecoverableStageWithoutPriorOutput(string recoverableStatus)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload($"启动早期恢复-{recoverableStatus}", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            $"startup-early-recovery-{recoverableStatus}.md",
            """
            # 第一章

            雨声压低了门口。

            他在门口停住。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, $"启动早期恢复 {recoverableStatus}", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.NotEmpty(await ReadSourceSegmentsAsync(options, anchor.AnchorId));
        Assert.NotEmpty(await ReadMaterialRowsAsync(options, anchor.AnchorId));
        await RemoveAnchorOutputAndSetBuildStatusAsync(options, anchor.AnchorId, recoverableStatus, recoverableStatus);
        Assert.Empty(await ReadSourceSegmentsAsync(options, anchor.AnchorId));
        Assert.Empty(await ReadMaterialRowsAsync(options, anchor.AnchorId));

        var startupRecovery = new SqliteReferenceAnchorService(
            options,
            new FileSystemNovelService(options, new FileSystemAppSettingsService(options)));
        var initialization = new FileSystemAppInitializationService(
            options,
            referenceAnchorRecovery: startupRecovery);

        Assert.True(await initialization.IsInitializedAsync(CancellationToken.None));

        var recoveredAnchor = Assert.Single(await startupRecovery.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(anchor.AnchorId, recoveredAnchor.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredAnchor.Status);
        var recoveredStatus = await startupRecovery.GetBuildStatusAsync(novel.Id, recoveredAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(recoveredStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredStatus.Status);
        Assert.True(recoveredStatus.SourceSegmentCount > 0);
        Assert.True(recoveredStatus.MaterialCount > 0);
        var recoveredSegments = await ReadSourceSegmentsAsync(options, recoveredAnchor.AnchorId);
        var recoveredRows = await ReadMaterialRowsAsync(options, recoveredAnchor.AnchorId);
        Assert.NotEmpty(recoveredSegments);
        Assert.NotEmpty(recoveredRows);
        Assert.Equal(recoveredRows.Count, recoveredRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        var search = await startupRecovery.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                AnchorIds: [],
                Query: "门口",
                MaterialTypes: [],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.NotEmpty(search.Items);
        Assert.All(search.Items, item => Assert.Equal(recoveredAnchor.AnchorId, item.AnchorId));
        var detail = await startupRecovery.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, recoveredAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Contains(detail.Events, item => item.Status == recoverableStatus);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
    }

    [Fact]
    public async Task CreateAnchorRecordsCancelledEmbeddingAndDuplicateImportDoesNotAutoRecover()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("向量取消测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("reference-vector-cancelled.md", "雨声压低了整条街的呼吸。");
        var embeddingOptions = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "test-key",
            "embed-v1",
            3,
            null);
        using var cancellation = new CancellationTokenSource();
        var service = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new RequestedCancellationEmbeddingClient(cancellation),
            new RecordingSqliteVecTableProvisioner());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await service.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(novel.Id, "取消向量参考", null, sourcePath, "markdown", "user_provided"),
                cancellation.Token));

        var cancelledAnchor = Assert.Single(await service.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(ReferenceAnchorBuildStates.Cancelled, cancelledAnchor.Status);
        var cancelledStatus = await service.GetBuildStatusAsync(novel.Id, cancelledAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(cancelledStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Cancelled, cancelledStatus.Status);

        var recoveringEmbeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var recoveringProvisioner = new RecordingSqliteVecTableProvisioner();
        var duplicateService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            recoveringEmbeddings,
            recoveringProvisioner);
        var duplicate = await duplicateService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "取消后重复导入", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(cancelledAnchor.AnchorId, duplicate.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.Cancelled, duplicate.Status);
        Assert.Empty(recoveringEmbeddings.Requests);
        Assert.Empty(recoveringProvisioner.Provisions);
    }

    [Fact]
    public async Task RebuildAnchorRecoversCancelledEmbeddingAndKeepsHistory()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("取消后显式恢复测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("reference-vector-cancelled-rebuild.md", "雨声压低了整条街的呼吸。");
        var embeddingOptions = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "test-key",
            "embed-v1",
            3,
            null);
        using var cancellation = new CancellationTokenSource();
        var cancelledService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new RequestedCancellationEmbeddingClient(cancellation),
            new RecordingSqliteVecTableProvisioner());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await cancelledService.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(novel.Id, "取消后恢复参考", null, sourcePath, "markdown", "user_provided"),
                cancellation.Token));

        var cancelledAnchor = Assert.Single(await cancelledService.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(ReferenceAnchorBuildStates.Cancelled, cancelledAnchor.Status);
        var beforeRows = await ReadMaterialRowsAsync(options, cancelledAnchor.AnchorId);
        Assert.NotEmpty(beforeRows);
        var correctedMaterialId = beforeRows[0].MaterialId;
        await cancelledService.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "explicit rebuild should preserve cancellation corrections"),
            CancellationToken.None);

        var recoveryService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new DeterministicEmbeddingClient(dimensions: 3),
            new RecordingSqliteVecTableProvisioner());

        var recovered = await recoveryService.RebuildAnchorAsync(novel.Id, cancelledAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Stage);
        Assert.Equal(recovered.MaterialCount, recovered.VectorCount);
        var afterRows = await ReadMaterialRowsAsync(options, cancelledAnchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        var corrected = Assert.Single(afterRows, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);
        var detail = await recoveryService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, cancelledAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, detail.CurrentStatus.Status);
        Assert.False(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Cancelled);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
    }

    [Fact]
    public async Task RebuildAnchorCancelledEmbeddingFailureUpdatesDiagnosticAndPreservesMaterials()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("取消后显式恢复失败测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-vector-cancelled-rebuild-failure.md",
            """
            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var embeddingOptions = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "test-key",
            "embed-v1",
            3,
            null);
        using var cancellation = new CancellationTokenSource();
        var cancelledService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new RequestedCancellationEmbeddingClient(cancellation),
            new RecordingSqliteVecTableProvisioner());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await cancelledService.CreateAnchorAsync(
                new CreateReferenceAnchorPayload(novel.Id, "取消后恢复失败参考", null, sourcePath, "markdown", "user_provided"),
                cancellation.Token));

        var cancelledAnchor = Assert.Single(await cancelledService.GetAnchorsAsync(novel.Id, CancellationToken.None));
        Assert.Equal(ReferenceAnchorBuildStates.Cancelled, cancelledAnchor.Status);
        var beforeRows = await ReadMaterialRowsAsync(options, cancelledAnchor.AnchorId);
        Assert.True(beforeRows.Count >= 2);
        var correctedMaterialId = beforeRows[0].MaterialId;
        var archivedMaterialId = beforeRows[^1].MaterialId;
        await cancelledService.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "cancelled rebuild failure must preserve correction"),
            CancellationToken.None);
        await cancelledService.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [archivedMaterialId]),
            CancellationToken.None);
        beforeRows = await ReadMaterialRowsAsync(options, cancelledAnchor.AnchorId);
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));

        var failingRecovery = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new DeterministicEmbeddingClient(dimensions: 3),
            new FailingSqliteVecTableProvisioner(
                $"sqlite-vec unavailable during cancelled rebuild at {Path.GetFullPath(sourcePath)} with prompt: hidden source_text: forbidden"));

        var failed = await failingRecovery.RebuildAnchorAsync(novel.Id, cancelledAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, failed.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, failed.Stage);
        Assert.Equal(beforeRows.Count, failed.MaterialCount);
        Assert.Equal(0, failed.VectorCount);
        Assert.Contains("sqlite-vec unavailable during cancelled rebuild", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failed.LastError, StringComparison.OrdinalIgnoreCase);
        var afterRows = await ReadMaterialRowsAsync(options, cancelledAnchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        Assert.Equal(afterRows.Count, afterRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var corrected = Assert.Single(afterRows, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);

        var search = await failingRecovery.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [cancelledAnchor.AnchorId], "雨声", [], [], [], [], [], 1, 100),
            CancellationToken.None);
        Assert.NotEmpty(search.Items);
        Assert.DoesNotContain(search.Items, item => item.MaterialId == archivedMaterialId);

        var detail = await failingRecovery.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, cancelledAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, detail.CurrentStatus.Status);
        Assert.Contains("sqlite-vec unavailable during cancelled rebuild", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Cancelled);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedEmbedding);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, detail.CurrentAttempt.Status);
        Assert.NotNull(detail.PriorAttempts);
        Assert.Contains(detail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.Cancelled);
        var latestFailedEvent = detail.Events
            .Where(item => item.Status == ReferenceAnchorBuildStates.FailedEmbedding)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.EventId, StringComparer.Ordinal)
            .First();
        Assert.NotEmpty(latestFailedEvent.AffectedMaterialId);
        Assert.NotEmpty(latestFailedEvent.AffectedSegmentId);
        Assert.Contains(afterRows, row => row.MaterialId == latestFailedEvent.AffectedMaterialId);
        var serialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RebuildAnchorRecoversFailedEmbeddingAndKeepsFailureHistory()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("向量失败重试恢复测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("reference-vector-retry-success.md", "雨声压低了整条街的呼吸。");
        var embeddingOptions = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "test-key",
            "embed-v1",
            3,
            null);
        var failingService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new DeterministicEmbeddingClient(dimensions: 3),
            new FailingSqliteVecTableProvisioner("sqlite-vec native extension is unavailable for first attempt."));
        var failedAnchor = await failingService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "向量失败后重试参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, failedAnchor.Status);

        var retryEmbeddings = new DeterministicEmbeddingClient(dimensions: 3);
        var retryProvisioner = new RecordingSqliteVecTableProvisioner();
        var retryService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            retryEmbeddings,
            retryProvisioner);

        var recovered = await retryService.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Stage);
        Assert.Equal(recovered.MaterialCount, recovered.VectorCount);
        Assert.Single(retryProvisioner.Provisions);
        Assert.Single(retryEmbeddings.Requests);
        var detail = await retryService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, detail.CurrentStatus.Status);
        Assert.False(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedEmbedding);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
        Assert.True(detail.AttemptCount >= 2);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, detail.CurrentAttempt.Status);
        Assert.True(detail.CurrentAttempt.AttemptNumber >= 2);
        Assert.NotEmpty(detail.CurrentAttempt.AttemptId);
        Assert.NotEmpty(detail.CurrentAttempt.BuildId);
        Assert.Equal(detail.Source.BuildVersion, detail.CurrentAttempt.BuildVersion);
        Assert.NotEmpty(detail.CurrentAttempt.RecoveredFromAttemptId);
        Assert.NotEmpty(detail.CurrentAttempt.RecoveredFromBuildId);
        Assert.NotNull(detail.PriorAttempts);
        Assert.Contains(detail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedEmbedding);
        Assert.Contains(detail.PriorAttempts, item => item.BlockedReason.Contains("first attempt", StringComparison.OrdinalIgnoreCase));

        var persistedAttempts = await ReadProcessingAttemptRowsAsync(options, failedAnchor.AnchorId);
        Assert.Equal(detail.AttemptCount, persistedAttempts.Count);
        Assert.Contains(persistedAttempts, item => item.AttemptId == detail.CurrentAttempt.AttemptId &&
            item.BuildId == detail.CurrentAttempt.BuildId &&
            item.AttemptNumber == detail.CurrentAttempt.AttemptNumber &&
            item.Status == ReferenceAnchorBuildStates.Ready &&
            item.RecoveredFromAttemptId == detail.CurrentAttempt.RecoveredFromAttemptId &&
            item.RecoveredFromBuildId == detail.CurrentAttempt.RecoveredFromBuildId);
        Assert.Contains(persistedAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedEmbedding &&
            item.BlockedReason.Contains("first attempt", StringComparison.OrdinalIgnoreCase));

        var eventAttemptRefs = await ReadProcessingEventAttemptRefsAsync(options, failedAnchor.AnchorId);
        Assert.NotEmpty(eventAttemptRefs);
        Assert.All(eventAttemptRefs, item =>
        {
            Assert.NotEmpty(item.AttemptId);
            Assert.NotEmpty(item.BuildId);
            Assert.True(item.AttemptNumber > 0);
            Assert.Contains(persistedAttempts, attempt => attempt.AttemptId == item.AttemptId &&
                attempt.BuildId == item.BuildId &&
                attempt.AttemptNumber == item.AttemptNumber);
        });
    }

    [Fact]
    public async Task RebuildAnchorFailedEmbeddingRetryFailureUpdatesDiagnosticAndPreservesMaterials()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("向量失败重试仍失败测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-vector-retry-failure.md",
            """
            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var embeddingOptions = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "test-key",
            "embed-v1",
            3,
            null);
        var firstFailureService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new DeterministicEmbeddingClient(dimensions: 3),
            new FailingSqliteVecTableProvisioner("sqlite-vec native extension is unavailable for first failure."));
        var failedAnchor = await firstFailureService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "向量重试仍失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, failedAnchor.Status);
        var beforeRows = await ReadMaterialRowsAsync(options, failedAnchor.AnchorId);
        Assert.NotEmpty(beforeRows);

        var retryFailureService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new DeterministicEmbeddingClient(dimensions: 3),
            new FailingSqliteVecTableProvisioner("sqlite-vec native extension is unavailable for second failure."));

        var failedAgain = await retryFailureService.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, failedAgain.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, failedAgain.Stage);
        Assert.Contains("second failure", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeRows.Count, failedAgain.MaterialCount);
        Assert.Equal(0, failedAgain.VectorCount);
        var afterRows = await ReadMaterialRowsAsync(options, failedAnchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        Assert.Equal(afterRows.Count, afterRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        var search = await retryFailureService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [failedAnchor.AnchorId],
                "雨声",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.NotEmpty(search.Items);

        var detail = await retryFailureService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, detail.CurrentStatus.Status);
        Assert.Contains("second failure", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.True(detail.Events.Count(item => item.Status == ReferenceAnchorBuildStates.FailedEmbedding) >= 2);
    }

    [Fact]
    public async Task CreateAnchorFailedSegmentingPersistsTerminalFailureAndExplicitRebuildRecovers()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("分段失败恢复测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("reference-segmenting-recovery.md", "雨声压低了门口的呼吸。");
        var failingService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            DefaultReferenceMaterialSlotDetector.Instance,
            new FailingReferenceAnchorProcessingStageProbe(
                ReferenceAnchorBuildStates.Segmenting,
                $"segmenting failed at {Path.GetFullPath(sourcePath)} with source_text: forbidden"));

        var failedAnchor = await failingService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "分段失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, failedAnchor.Status);
        var status = await failingService.GetBuildStatusAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, status.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, status.Stage);
        Assert.Equal(0, status.SourceSegmentCount);
        Assert.Equal(0, status.MaterialCount);
        Assert.Equal(0, status.SlotCount);
        Assert.Equal(0, status.VectorCount);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), status.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", status.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadSourceSegmentsAsync(options, failedAnchor.AnchorId));
        Assert.Empty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));

        var detail = await failingService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, detail.CurrentStatus.Status);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        var failedEvent = Assert.Single(detail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedSegmenting);
        Assert.Empty(failedEvent.AffectedMaterialId);
        Assert.Empty(failedEvent.AffectedSegmentId);

        var duplicate = await failingService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "分段失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.Equal(failedAnchor.AnchorId, duplicate.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, duplicate.Status);

        var recovered = await new SqliteReferenceAnchorService(options, novels)
            .RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Status);
        Assert.True(recovered.SourceSegmentCount > 0);
        Assert.True(recovered.MaterialCount > 0);
        Assert.NotEmpty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));
        var recoveredDetail = await failingService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(recoveredDetail);
        Assert.NotNull(recoveredDetail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredDetail.CurrentAttempt.Status);
        Assert.True(recoveredDetail.CurrentAttempt.AttemptNumber >= 2);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.AttemptId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.BuildId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.RecoveredFromAttemptId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.RecoveredFromBuildId);
        Assert.NotNull(recoveredDetail.PriorAttempts);
        Assert.Contains(recoveredDetail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedSegmenting);
        Assert.Contains(recoveredDetail.PriorAttempts, item => item.BlockedReason.Contains("segmenting failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(recoveredDetail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedSegmenting);
        Assert.Contains(recoveredDetail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
    }

    [Fact]
    public async Task RebuildAnchorFailedSegmentingRetryFailureUpdatesDiagnosticWithoutFakeOutput()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("分段失败重试仍失败测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("reference-segmenting-retry-failure.md", "雨声压低了门口的呼吸。");
        var firstFailureService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            DefaultReferenceMaterialSlotDetector.Instance,
            new FailingReferenceAnchorProcessingStageProbe(
                ReferenceAnchorBuildStates.Segmenting,
                $"first segmenting failure at {Path.GetFullPath(sourcePath)} with source_text: forbidden prompt: hidden"));

        var failedAnchor = await firstFailureService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "分段重试仍失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, failedAnchor.Status);
        var firstStatus = await firstFailureService.GetBuildStatusAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(firstStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, firstStatus.Status);
        Assert.Equal(0, firstStatus.SourceSegmentCount);
        Assert.Equal(0, firstStatus.MaterialCount);
        Assert.Empty(await ReadSourceSegmentsAsync(options, failedAnchor.AnchorId));
        Assert.Empty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));

        var retryFailureService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            DefaultReferenceMaterialSlotDetector.Instance,
            new FailingReferenceAnchorProcessingStageProbe(
                ReferenceAnchorBuildStates.Segmenting,
                $"second segmenting failure at {Path.GetFullPath(sourcePath)} with source_text: forbidden prompt: hidden full_content: secret"));

        var failedAgain = await retryFailureService.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, failedAgain.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, failedAgain.Stage);
        Assert.Equal(0, failedAgain.SourceSegmentCount);
        Assert.Equal(0, failedAgain.MaterialCount);
        Assert.Equal(0, failedAgain.SlotCount);
        Assert.Equal(0, failedAgain.VectorCount);
        Assert.Contains("second segmenting failure", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await ReadSourceSegmentsAsync(options, failedAnchor.AnchorId));
        Assert.Empty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));
        var search = await retryFailureService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [failedAnchor.AnchorId], "", [], [], [], [], [], 1, 10),
            CancellationToken.None);
        Assert.Empty(search.Items);

        var detail = await retryFailureService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, detail.CurrentStatus.Status);
        Assert.Contains("second segmenting failure", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSegmenting, detail.CurrentAttempt.Status);
        Assert.True(detail.CurrentAttempt.AttemptNumber >= 2);
        Assert.Contains("second segmenting failure", detail.CurrentAttempt.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(detail.PriorAttempts);
        Assert.Contains(detail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedSegmenting);
        Assert.True(detail.Events.Count(item => item.Status == ReferenceAnchorBuildStates.FailedSegmenting) >= 2);
        var latestFailedEvent = detail.Events
            .Where(item => item.Status == ReferenceAnchorBuildStates.FailedSegmenting)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.EventId, StringComparer.Ordinal)
            .First();
        Assert.Empty(latestFailedEvent.AffectedMaterialId);
        Assert.Empty(latestFailedEvent.AffectedSegmentId);
        Assert.Empty(latestFailedEvent.AffectedSlotId);
        var detailSerialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), detailSerialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAnchorFailedExtractionPreservesSegmentsAndExplicitRebuildRecovers()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("提取失败恢复测试", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("隔离作品", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-extraction-recovery.md",
            """
            雨声压低了整条街的呼吸。
            他在门口停了很久，手指贴着伞柄。
            """);
        var failingService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            DefaultReferenceMaterialSlotDetector.Instance,
            new FailingReferenceAnchorProcessingStageProbe(
                ReferenceAnchorBuildStates.ExtractingMaterials,
                $"extraction failed at {Path.GetFullPath(sourcePath)} with prompt: hidden"));

        var failedAnchor = await failingService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "提取失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, failedAnchor.Status);
        var status = await failingService.GetBuildStatusAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, status.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, status.Stage);
        Assert.True(status.SourceSegmentCount > 0);
        Assert.Equal(0, status.MaterialCount);
        Assert.Equal(0, status.SlotCount);
        Assert.Equal(0, status.VectorCount);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), status.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", status.LastError, StringComparison.OrdinalIgnoreCase);
        var segments = await ReadSourceSegmentsAsync(options, failedAnchor.AnchorId);
        Assert.NotEmpty(segments);
        Assert.Empty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));

        var detail = await failingService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, detail.CurrentStatus.Status);
        Assert.True(detail.RetryAvailable);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.SegmentsBuilt);
        var failedEvent = Assert.Single(detail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedExtraction);
        Assert.Empty(failedEvent.AffectedMaterialId);
        Assert.NotEmpty(failedEvent.AffectedSegmentId);
        Assert.Contains(segments, item => item.SegmentId == failedEvent.AffectedSegmentId);

        var segmentDetail = await failingService.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(novel.Id, failedAnchor.AnchorId, failedEvent.AffectedSegmentId),
            CancellationToken.None);
        Assert.NotNull(segmentDetail);
        Assert.Equal(failedAnchor.AnchorId, segmentDetail.Source.AnchorId);
        Assert.Equal(failedEvent.AffectedSegmentId, segmentDetail.Segment.SegmentId);
        Assert.Equal(failedAnchor.AnchorId, segmentDetail.Segment.AnchorId);
        Assert.True(segmentDetail.Segment.TextPreview.Length <= 243);
        Assert.Contains(segmentDetail.ProcessingNotes, item => item.Status == ReferenceAnchorBuildStates.FailedExtraction);
        var segmentSerialized = JsonSerializer.Serialize(segmentDetail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sourcePath, segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), segmentSerialized, StringComparison.OrdinalIgnoreCase);

        var isolatedSegment = await failingService.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(otherNovel.Id, failedAnchor.AnchorId, failedEvent.AffectedSegmentId),
            CancellationToken.None);
        Assert.Null(isolatedSegment);
        var missingSegment = await failingService.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(novel.Id, failedAnchor.AnchorId, "missing-segment"),
            CancellationToken.None);
        Assert.Null(missingSegment);

        var duplicate = await failingService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "提取失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.Equal(failedAnchor.AnchorId, duplicate.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, duplicate.Status);

        var recoveryService = new SqliteReferenceAnchorService(options, novels);
        var recovered = await recoveryService.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Status);
        Assert.True(recovered.MaterialCount > 0);
        var recoveredRows = await ReadMaterialRowsAsync(options, failedAnchor.AnchorId);
        Assert.NotEmpty(recoveredRows);
        var recoveredDetail = await recoveryService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(recoveredDetail);
        Assert.NotNull(recoveredDetail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredDetail.CurrentAttempt.Status);
        Assert.True(recoveredDetail.CurrentAttempt.AttemptNumber >= 2);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.AttemptId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.BuildId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.RecoveredFromAttemptId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.RecoveredFromBuildId);
        Assert.Contains(recoveredDetail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedExtraction);
        Assert.Contains(recoveredDetail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
        Assert.NotNull(recoveredDetail.PriorAttempts);
        Assert.Contains(recoveredDetail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedExtraction);
        Assert.Contains(recoveredDetail.PriorAttempts, item => item.BlockedReason.Contains("extraction failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RebuildAnchorFailedExtractionRetryFailureUpdatesDiagnosticAndPreservesSourceSegments()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("提取失败重试仍失败测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-extraction-retry-failure.md",
            """
            雨声压低了整条街的呼吸。

            他在门口停了很久，手指贴着伞柄。
            """);
        var firstFailureService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            DefaultReferenceMaterialSlotDetector.Instance,
            new FailingReferenceAnchorProcessingStageProbe(
                ReferenceAnchorBuildStates.ExtractingMaterials,
                $"first extraction failure at {Path.GetFullPath(sourcePath)} with prompt: hidden source_text: forbidden"));

        var failedAnchor = await firstFailureService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "提取重试仍失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, failedAnchor.Status);
        var firstStatus = await firstFailureService.GetBuildStatusAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(firstStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, firstStatus.Status);
        Assert.True(firstStatus.SourceSegmentCount > 0);
        Assert.Equal(0, firstStatus.MaterialCount);
        var beforeSegments = await ReadSourceSegmentsAsync(options, failedAnchor.AnchorId);
        Assert.NotEmpty(beforeSegments);
        Assert.Empty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));

        var retryFailureService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            DefaultReferenceMaterialSlotDetector.Instance,
            new FailingReferenceAnchorProcessingStageProbe(
                ReferenceAnchorBuildStates.ExtractingMaterials,
                $"second extraction failure at {Path.GetFullPath(sourcePath)} with prompt: hidden source_text: forbidden full_content: secret"));

        var failedAgain = await retryFailureService.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, failedAgain.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, failedAgain.Stage);
        Assert.Equal(firstStatus.SourceSegmentCount, failedAgain.SourceSegmentCount);
        Assert.Equal(0, failedAgain.MaterialCount);
        Assert.Equal(0, failedAgain.SlotCount);
        Assert.Equal(0, failedAgain.VectorCount);
        Assert.Contains("second extraction failure", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        var afterSegments = await ReadSourceSegmentsAsync(options, failedAnchor.AnchorId);
        Assert.Equal(beforeSegments.Select(row => row.Signature), afterSegments.Select(row => row.Signature));
        Assert.Empty(await ReadMaterialRowsAsync(options, failedAnchor.AnchorId));

        var detail = await retryFailureService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, detail.CurrentStatus.Status);
        Assert.Contains("second extraction failure", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.FailedExtraction, detail.CurrentAttempt.Status);
        Assert.True(detail.CurrentAttempt.AttemptNumber >= 2);
        Assert.Contains("second extraction failure", detail.CurrentAttempt.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(detail.PriorAttempts);
        Assert.Contains(detail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedExtraction);
        Assert.True(detail.Events.Count(item => item.Status == ReferenceAnchorBuildStates.FailedExtraction) >= 2);
        var latestFailedEvent = detail.Events
            .Where(item => item.Status == ReferenceAnchorBuildStates.FailedExtraction)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.EventId, StringComparer.Ordinal)
            .First();
        Assert.Empty(latestFailedEvent.AffectedMaterialId);
        Assert.NotEmpty(latestFailedEvent.AffectedSegmentId);
        Assert.Contains(afterSegments, row => row.SegmentId == latestFailedEvent.AffectedSegmentId);

        var segmentDetail = await retryFailureService.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(novel.Id, failedAnchor.AnchorId, latestFailedEvent.AffectedSegmentId),
            CancellationToken.None);
        Assert.NotNull(segmentDetail);
        Assert.Equal(failedAnchor.AnchorId, segmentDetail.Source.AnchorId);
        Assert.Equal(latestFailedEvent.AffectedSegmentId, segmentDetail.Segment.SegmentId);
        Assert.True(segmentDetail.Segment.TextPreview.Length <= 243);
        Assert.Contains(segmentDetail.ProcessingNotes, item => item.Status == ReferenceAnchorBuildStates.FailedExtraction);
        var detailSerialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        var segmentSerialized = JsonSerializer.Serialize(segmentDetail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), detailSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_path", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("full_content", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), segmentSerialized, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ReferenceAnchorBuildStates.Segmenting, ReferenceAnchorBuildStates.FailedSegmenting)]
    [InlineData(ReferenceAnchorBuildStates.ExtractingMaterials, ReferenceAnchorBuildStates.FailedExtraction)]
    public async Task RebuildAnchorSegmentingOrExtractionFailurePreservesPreviousSearchableCorpus(
        string failingStage,
        string expectedFailureStatus)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload($"重建{expectedFailureStatus}保留旧语料", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            $"reference-rebuild-{expectedFailureStatus}.md",
            """
            旧雨声压低了整条街的呼吸。
            林岚在门口停住，手指贴着伞柄。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, $"重建{expectedFailureStatus}参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status);
        var beforeStatus = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(beforeStatus);
        var beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var beforeSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.NotEmpty(beforeRows);
        Assert.NotEmpty(beforeSegments);

        await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(novel.Id, beforeRows[0].MaterialId, "manual_turn", null, null, null, null, null, null),
            CancellationToken.None);
        beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Contains(beforeRows, row => row.MaterialId == beforeRows[0].MaterialId && row.UserVerified);

        await File.WriteAllTextAsync(
            sourcePath,
            """
            新失败语料只应留在源文件中。
            如果重建失败，它不应替换旧的可搜索素材。
            """);
        var failingService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            DefaultReferenceMaterialSlotDetector.Instance,
            new FailingReferenceAnchorProcessingStageProbe(
                failingStage,
                $"rebuild {expectedFailureStatus} failed at {Path.GetFullPath(sourcePath)} with prompt: hidden"));

        var failed = await failingService.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Equal(expectedFailureStatus, failed.Status);
        Assert.Equal(expectedFailureStatus, failed.Stage);
        var failedAnchor = Assert.Single(await failingService.GetAnchorsAsync(novel.Id, CancellationToken.None), item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(anchor.SourceFileHash, failedAnchor.SourceFileHash);
        Assert.Equal(beforeStatus.SourceSegmentCount, failed.SourceSegmentCount);
        Assert.Equal(beforeStatus.MaterialCount, failed.MaterialCount);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failed.LastError, StringComparison.OrdinalIgnoreCase);
        var afterRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        Assert.Equal(afterRows.Count, afterRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(afterRows, row => row.MaterialId == beforeRows[0].MaterialId && row.UserVerified);
        var oldSearch = await failingService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [anchor.AnchorId], "旧雨声", [], [], [], [], [], 1, 10),
            CancellationToken.None);
        Assert.NotEmpty(oldSearch.Items);
        var newSearch = await failingService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [anchor.AnchorId], "新失败语料", [], [], [], [], [], 1, 10),
            CancellationToken.None);
        Assert.Empty(newSearch.Items);

        var detail = await failingService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(expectedFailureStatus, detail.CurrentStatus.Status);
        Assert.True(detail.RetryAvailable);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
        Assert.Contains(detail.Events, item => item.Status == expectedFailureStatus);

        var duplicate = await failingService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, $"重建{expectedFailureStatus}参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.NotEqual(anchor.AnchorId, duplicate.AnchorId);
        Assert.Equal(expectedFailureStatus, duplicate.Status);
        Assert.Equal(afterRows.Select(row => row.MaterialId), (await ReadMaterialRowsAsync(options, anchor.AnchorId)).Select(row => row.MaterialId));
        Assert.Empty(await ReadMaterialRowsAsync(options, duplicate.AnchorId));
    }

    [Fact]
    public async Task RebuildAnchorSlottingFailurePreservesPreviousSearchableCorpus()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("重建槽位失败保留旧语料", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-rebuild-failed-slotting.md",
            """
            旧雨声压低了整条街的呼吸。
            林岚握住 {{old_memory}}，没有立刻说话。
            """);
        var embeddingOptions = new EmbeddingRequestOptions(
            "custom",
            "https://api.example.com/v1/embeddings",
            "test-key",
            "embed-v1",
            3,
            null);
        var initialProvisioner = new RecordingSqliteVecTableProvisioner();
        var service = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new DeterministicEmbeddingClient(dimensions: 3),
            initialProvisioner,
            initialProvisioner);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "重建槽位失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, anchor.Status);
        var beforeStatus = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(beforeStatus);
        Assert.True(beforeStatus.VectorCount > 0);
        Assert.Equal(beforeStatus.MaterialCount, beforeStatus.VectorCount);
        Assert.Single(initialProvisioner.Provisions);
        var beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.True(beforeRows.Count >= 2);
        Assert.Contains(beforeRows, row => row.SlotCount > 0);
        var correctedMaterialId = beforeRows[0].MaterialId;
        var archivedMaterialId = beforeRows[^1].MaterialId;
        await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "failed slotting rebuild must preserve correction"),
            CancellationToken.None);
        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [archivedMaterialId]),
            CancellationToken.None);
        beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));

        await File.WriteAllTextAsync(
            sourcePath,
            """
            新槽位失败语料只应留在源文件中。
            如果 slotting 失败，它不应替换旧的可搜索素材 {{new_memory}}。
            """);
        var failingProvisioner = new RecordingSqliteVecTableProvisioner();
        var failingService = new SqliteReferenceAnchorService(
            options,
            novels,
            new StaticEmbeddingConfigurationService(embeddingOptions),
            new DeterministicEmbeddingClient(dimensions: 3),
            failingProvisioner,
            failingProvisioner,
            new FailingReferenceMaterialSlotDetector(
                $"slotting rebuild failed at {Path.GetFullPath(sourcePath)} with prompt: hidden source_text: forbidden"));

        var failed = await failingService.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failed.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failed.Stage);
        Assert.Equal(beforeStatus.SourceSegmentCount, failed.SourceSegmentCount);
        Assert.Equal(beforeStatus.MaterialCount, failed.MaterialCount);
        Assert.Equal(beforeStatus.SlotCount, failed.SlotCount);
        Assert.Equal(beforeStatus.VectorCount, failed.VectorCount);
        Assert.Empty(failingProvisioner.Provisions);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failed.LastError, StringComparison.OrdinalIgnoreCase);
        var failedAnchor = Assert.Single(await failingService.GetAnchorsAsync(novel.Id, CancellationToken.None), item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(anchor.SourceFileHash, failedAnchor.SourceFileHash);
        var afterRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        Assert.Equal(afterRows.Count, afterRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var corrected = Assert.Single(afterRows, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);
        Assert.Contains(afterRows, row => row.SlotCount > 0);

        var oldSearch = await failingService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [anchor.AnchorId], "旧雨声", [], [], [], [], [], 1, 10),
            CancellationToken.None);
        Assert.NotEmpty(oldSearch.Items);
        var newSearch = await failingService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [anchor.AnchorId], "新槽位失败语料", [], [], [], [], [], 1, 10),
            CancellationToken.None);
        Assert.Empty(newSearch.Items);
        var archivedSearch = await failingService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [anchor.AnchorId], "", [], [], [], [], [], 1, 100),
            CancellationToken.None);
        Assert.DoesNotContain(archivedSearch.Items, item => item.MaterialId == archivedMaterialId);

        var detail = await failingService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, detail.CurrentStatus.Status);
        Assert.True(detail.RetryAvailable);
        Assert.Contains(detail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
        var failedEvent = Assert.Single(detail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedSlotting);
        Assert.NotEmpty(failedEvent.AffectedMaterialId);
        Assert.NotEmpty(failedEvent.AffectedSegmentId);
        Assert.Contains(afterRows, row => row.MaterialId == failedEvent.AffectedMaterialId);
    }

    [Fact]
    public async Task CreateAnchorFailedSlottingPreservesMaterialDetailAndExplicitRebuildRecovers()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("槽位失败恢复测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-slotting-recovery.md",
            """
            雨声压低了整条街的呼吸，门口的灯影像一枚迟迟不肯落下的针。
            他停在门槛前，想起那句 {{memory_anchor}}，然后把伞柄握得更紧。
            """);
        var failingService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            new FailingReferenceMaterialSlotDetector(
                $"slot failed at {Path.GetFullPath(sourcePath)} with prompt: hidden source_text: forbidden"));

        var failedAnchor = await failingService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "槽位失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failedAnchor.Status);
        var failedStatus = await failingService.GetBuildStatusAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(failedStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failedStatus.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failedStatus.Stage);
        Assert.True(failedStatus.SourceSegmentCount > 0);
        Assert.True(failedStatus.MaterialCount > 0);
        Assert.Equal(0, failedStatus.SlotCount);
        Assert.Equal(0, failedStatus.VectorCount);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failedStatus.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failedStatus.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failedStatus.LastError, StringComparison.OrdinalIgnoreCase);

        var failedRows = await ReadMaterialRowsAsync(options, failedAnchor.AnchorId);
        Assert.NotEmpty(failedRows);
        Assert.All(failedRows, row => Assert.Equal(0, row.SlotCount));
        Assert.Equal(failedRows.Count, failedRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());

        var failedSearch = await failingService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [failedAnchor.AnchorId],
                "雨声",
                [],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.NotEmpty(failedSearch.Items);

        var failedDetail = await failingService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(failedDetail);
        Assert.NotNull(failedDetail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failedDetail.CurrentStatus.Status);
        Assert.True(failedDetail.RetryAvailable);
        Assert.True(failedDetail.RebuildAvailable);
        var failedEvent = Assert.Single(
            failedDetail.Events,
            item => item.Status == ReferenceAnchorBuildStates.FailedSlotting);
        Assert.NotEmpty(failedEvent.AffectedMaterialId);
        Assert.NotEmpty(failedEvent.AffectedSegmentId);
        Assert.Empty(failedEvent.AffectedSlotId);

        var materialDetail = await failingService.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, failedEvent.AffectedMaterialId),
            CancellationToken.None);
        Assert.NotNull(materialDetail);
        Assert.Equal(failedEvent.AffectedMaterialId, materialDetail.Material.MaterialId);
        Assert.Contains(materialDetail.ProcessingNotes, item => item.Status == ReferenceAnchorBuildStates.FailedSlotting);

        var duplicate = await failingService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "槽位失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        Assert.Equal(failedAnchor.AnchorId, duplicate.AnchorId);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, duplicate.Status);
        Assert.Equal(
            failedRows.Select(row => row.MaterialId),
            (await ReadMaterialRowsAsync(options, failedAnchor.AnchorId)).Select(row => row.MaterialId));

        await failingService.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(novel.Id, failedRows[0].MaterialId, "manual_turn", null, null, null, null, null, null),
            CancellationToken.None);

        var recoveryService = new SqliteReferenceAnchorService(options, novels);
        var recovered = await recoveryService.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recovered.Stage);
        Assert.True(recovered.SlotCount > 0);
        var recoveredRows = await ReadMaterialRowsAsync(options, failedAnchor.AnchorId);
        Assert.Equal(failedRows.Select(row => row.MaterialId), recoveredRows.Select(row => row.MaterialId));
        Assert.Equal(recoveredRows.Count, recoveredRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(recoveredRows, row => row.MaterialId == failedRows[0].MaterialId && row.UserVerified);
        Assert.Contains(recoveredRows, row => row.SlotCount > 0);

        var recoveredDetail = await recoveryService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(recoveredDetail);
        Assert.NotNull(recoveredDetail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredDetail.CurrentStatus.Status);
        Assert.False(recoveredDetail.RetryAvailable);
        Assert.Contains(recoveredDetail.Events, item => item.Status == ReferenceAnchorBuildStates.FailedSlotting);
        Assert.Contains(recoveredDetail.Events, item => item.Status == ReferenceAnchorBuildStates.Ready);
        Assert.NotNull(recoveredDetail.CurrentAttempt);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, recoveredDetail.CurrentAttempt.Status);
        Assert.True(recoveredDetail.CurrentAttempt.AttemptNumber >= 2);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.AttemptId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.BuildId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.RecoveredFromAttemptId);
        Assert.NotEmpty(recoveredDetail.CurrentAttempt.RecoveredFromBuildId);
        Assert.NotNull(recoveredDetail.PriorAttempts);
        Assert.Contains(recoveredDetail.PriorAttempts, item => item.Status == ReferenceAnchorBuildStates.FailedSlotting);
        Assert.Contains(recoveredDetail.PriorAttempts, item => item.BlockedReason.Contains("slot failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RebuildAnchorFailedSlottingRetryFailureUpdatesDiagnosticAndPreservesMaterials()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("槽位失败重试仍失败测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "reference-slotting-retry-failure.md",
            """
            雨声压低了整条街的呼吸，门口的灯影像一枚迟迟不肯落下的针。

            他停在门槛前，想起那句 {{memory_anchor}}，然后把伞柄握得更紧。
            """);
        var firstFailureService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            new FailingReferenceMaterialSlotDetector(
                $"first slot failure at {Path.GetFullPath(sourcePath)} with prompt: hidden source_text: forbidden"));

        var failedAnchor = await firstFailureService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "槽位重试仍失败参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failedAnchor.Status);
        var firstStatus = await firstFailureService.GetBuildStatusAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(firstStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, firstStatus.Status);
        var beforeRows = await ReadMaterialRowsAsync(options, failedAnchor.AnchorId);
        Assert.True(beforeRows.Count >= 2);
        var correctedMaterialId = beforeRows[0].MaterialId;
        var archivedMaterialId = beforeRows[^1].MaterialId;
        await firstFailureService.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "slotting retry failure must preserve correction"),
            CancellationToken.None);
        await firstFailureService.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [archivedMaterialId]),
            CancellationToken.None);
        beforeRows = await ReadMaterialRowsAsync(options, failedAnchor.AnchorId);
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));

        var retryFailureService = new SqliteReferenceAnchorService(
            options,
            novels,
            null,
            null,
            null,
            null,
            new FailingReferenceMaterialSlotDetector(
                $"second slot failure at {Path.GetFullPath(sourcePath)} with prompt: hidden source_text: forbidden"));

        var failedAgain = await retryFailureService.RebuildAnchorAsync(novel.Id, failedAnchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failedAgain.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, failedAgain.Stage);
        Assert.Equal(firstStatus.SourceSegmentCount, failedAgain.SourceSegmentCount);
        Assert.Equal(firstStatus.MaterialCount, failedAgain.MaterialCount);
        Assert.Equal(firstStatus.SlotCount, failedAgain.SlotCount);
        Assert.Equal(firstStatus.VectorCount, failedAgain.VectorCount);
        Assert.Contains("second slot failure", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failedAgain.LastError, StringComparison.OrdinalIgnoreCase);
        var afterRows = await ReadMaterialRowsAsync(options, failedAnchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        Assert.Equal(afterRows.Count, afterRows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var corrected = Assert.Single(afterRows, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);

        var search = await retryFailureService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(novel.Id, [failedAnchor.AnchorId], "雨声", [], [], [], [], [], 1, 100),
            CancellationToken.None);
        Assert.NotEmpty(search.Items);
        Assert.DoesNotContain(search.Items, item => item.MaterialId == archivedMaterialId);

        var detail = await retryFailureService.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, failedAnchor.AnchorId),
            CancellationToken.None);
        Assert.NotNull(detail);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedSlotting, detail.CurrentStatus.Status);
        Assert.Contains("second slot failure", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.True(detail.Events.Count(item => item.Status == ReferenceAnchorBuildStates.FailedSlotting) >= 2);
        var latestFailedEvent = detail.Events
            .Where(item => item.Status == ReferenceAnchorBuildStates.FailedSlotting)
            .OrderByDescending(item => item.CreatedAt)
            .First();
        Assert.NotEmpty(latestFailedEvent.AffectedMaterialId);
        Assert.NotEmpty(latestFailedEvent.AffectedSegmentId);
        Assert.Contains(afterRows, row => row.MaterialId == latestFailedEvent.AffectedMaterialId);
    }

    [Fact]
    public async Task RebuildAnchorFailedEmbeddingPreservesPreviousSearchableCorpus()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("向量重建失败回滚测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("reference-vector-rebuild-rollback.md", "旧材料仍应可搜索。");
        var initialService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await initialService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "向量失败回滚参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.NotEmpty(beforeRows);
        var correctedMaterialId = beforeRows[0].MaterialId;
        await initialService.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                correctedMaterialId,
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "failed rebuild must preserve manual correction"),
            CancellationToken.None);
        beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);

        File.WriteAllText(sourcePath, "新材料不应在失败后替换旧语料。");
        var rebuildService = new SqliteReferenceAnchorService(
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
            new FailingSqliteVecTableProvisioner("sqlite-vec native extension is unavailable during rebuild."));

        var failed = await rebuildService.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, failed.Status);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, failed.Stage);
        Assert.Equal(0, failed.VectorCount);
        var oldSearch = await rebuildService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "旧材料",
                [],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.NotEmpty(oldSearch.Items);
        var newSearch = await rebuildService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "新材料",
                [],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.Empty(newSearch.Items);
        var afterRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        var corrected = Assert.Single(afterRows, row => row.MaterialId == correctedMaterialId);
        Assert.True(corrected.UserVerified);
        Assert.Equal("interiority", corrected.FunctionTag);
        Assert.Equal("unease", corrected.EmotionTag);
        Assert.Equal("close", corrected.PovTag);
        Assert.Equal("afterbeat", corrected.TechniqueTag);
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
    public async Task GetMaterialDetailReturnsBoundedPreviewWithoutSourcePathOrFullText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("材料明细测试", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("隔离小说", "", ""), CancellationToken.None);
        var longSentence = "他在门口停了很久，" + string.Concat(Enumerable.Repeat("雨声压低街道的呼吸，", 40)) + "直到灯光熄灭。";
        var sourcePath = CreateSourceFile(
            "material-detail.md",
            $"""
            # 第一章 雨夜

            {longSentence}

            她说：“你终于来了。”
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "雨夜材料", "作者", sourcePath, "markdown", "user_provided"),
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
        var material = Assert.Single(
            materials.Items,
            item => item.Text.Contains("门口", StringComparison.Ordinal));

        var detail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, material.MaterialId),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(material.MaterialId, detail.Material.MaterialId);
        Assert.Equal(anchor.AnchorId, detail.Source.AnchorId);
        Assert.Equal("雨夜材料", detail.Source.Title);
        Assert.Equal("markdown", detail.Source.SourceKind);
        Assert.Equal(ReferenceAnchorOwnerScopes.Novel, detail.Source.OwnerScope);
        Assert.Equal(novel.Id, detail.Source.OwnerNovelId);
        Assert.Equal(ReferenceMaterialArchiveFilters.Active, detail.Material.ArchiveState);
        Assert.Null(detail.Material.ArchivedAt);
        Assert.True(detail.Material.ScoreComponents is null || detail.Material.ScoreComponents.Count == 0);
        Assert.DoesNotContain(sourcePath, detail.Material.TextPreview, StringComparison.OrdinalIgnoreCase);
        Assert.True(detail.Material.TextTruncated);
        Assert.NotEqual(material.Text, detail.Material.TextPreview);
        Assert.DoesNotContain(longSentence, detail.Material.TextPreview, StringComparison.Ordinal);
        Assert.NotEmpty(detail.Segments);
        Assert.All(detail.Segments, segment =>
        {
            Assert.True(segment.TextPreview.Length <= 243);
            Assert.DoesNotContain(longSentence, segment.TextPreview, StringComparison.Ordinal);
        });
        Assert.True(detail.Slots.Count <= 20);
        var note = detail.ProcessingNotes.First(item => item.Status == ReferenceAnchorBuildStates.Ready);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, note.Status);
        Assert.Contains("materials=", note.Message, StringComparison.Ordinal);
        Assert.True(note.SourceSegmentCount > 0);
        Assert.True(note.MaterialCount > 0);
        Assert.True(note.SlotCount >= 0);
        Assert.True(note.VectorCount >= 0);
        Assert.Equal(anchor.AnchorId.ToString(), note.AffectedSourceId);

        var uncPath = @"\\server\share\reference\material-detail.md";
        var fileUri = "file://server/share/reference/material-detail.md";
        var sensitiveBuildError = $"Failed to open {Path.GetFullPath(sourcePath)} and {uncPath} from {fileUri} with authorization: Bearer live-error-token-abcdefghijklmnopqrstuvwxyz; source_text: {longSentence}; api_key: sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890";
        await UpdateBuildStateErrorAsync(options, anchor.AnchorId, sensitiveBuildError);
        var failedDetail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, material.MaterialId),
            CancellationToken.None);
        Assert.NotNull(failedDetail);
        Assert.True(failedDetail.ProcessingNotes.Count >= 2);
        var failedNote = failedDetail.ProcessingNotes[0];
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, failedNote.Status);
        Assert.Contains("[REDACTED", failedNote.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, failedNote.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failedNote.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(uncPath, failedNote.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileUri, failedNote.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer live-error-token", failedNote.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-proj-error", failedNote.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failedNote.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", failedNote.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(longSentence, failedNote.Message, StringComparison.Ordinal);
        var failedSerialized = JsonSerializer.Serialize(failedDetail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain(sourcePath, failedSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), failedSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(uncPath, failedSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileUri, failedSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer live-error-token", failedSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-proj-error", failedSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", failedSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", failedSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(longSentence, failedSerialized, StringComparison.Ordinal);

        var serialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sourcePath, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(longSentence, serialized, StringComparison.Ordinal);

        var isolated = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(otherNovel.Id, material.MaterialId),
            CancellationToken.None);
        Assert.Null(isolated);

        var sourceSegmentDetail = await service.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(novel.Id, anchor.AnchorId, detail.Material.SourceSegmentId),
            CancellationToken.None);
        Assert.NotNull(sourceSegmentDetail);
        Assert.Equal(anchor.AnchorId, sourceSegmentDetail.Source.AnchorId);
        Assert.Equal(detail.Material.SourceSegmentId, sourceSegmentDetail.Segment.SegmentId);
        Assert.Equal(anchor.AnchorId, sourceSegmentDetail.Segment.AnchorId);
        Assert.True(sourceSegmentDetail.Segment.TextPreview.Length <= 243);
        Assert.DoesNotContain(longSentence, sourceSegmentDetail.Segment.TextPreview, StringComparison.Ordinal);
        var segmentSerialized = JsonSerializer.Serialize(sourceSegmentDetail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sourcePath, segmentSerialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), segmentSerialized, StringComparison.OrdinalIgnoreCase);

        var isolatedSegment = await service.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(otherNovel.Id, anchor.AnchorId, detail.Material.SourceSegmentId),
            CancellationToken.None);
        Assert.Null(isolatedSegment);

        var missingSegment = await service.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(novel.Id, anchor.AnchorId, "missing-segment"),
            CancellationToken.None);
        Assert.Null(missingSegment);

        var missing = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, "missing-material"),
            CancellationToken.None);
        Assert.Null(missing);

        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [material.MaterialId]),
            CancellationToken.None);
        var archivedDetail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, material.MaterialId),
            CancellationToken.None);
        Assert.NotNull(archivedDetail);
        Assert.Equal(material.MaterialId, archivedDetail.Material.MaterialId);
        Assert.Equal(ReferenceMaterialArchiveFilters.Archived, archivedDetail.Material.ArchiveState);
        Assert.NotNull(archivedDetail.Material.ArchivedAt);

        var unknownSourcePath = CreateSourceFile(
            "material-detail-unknown.md",
            "他在门口停住，" + string.Concat(Enumerable.Repeat("雨水顺着伞骨落下，", 20)) + "仍然没有抬头。");
        var unknownAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "未知授权材料", "作者", unknownSourcePath, "markdown", "unknown"),
            CancellationToken.None);
        var unknownMaterials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [unknownAnchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var unknownMaterial = Assert.Single(
            unknownMaterials.Items,
            item => item.Text.Contains("门口", StringComparison.Ordinal));
        var unknownDetail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, unknownMaterial.MaterialId),
            CancellationToken.None);
        Assert.NotNull(unknownDetail);
        Assert.Equal("unknown", unknownDetail.Source.LicenseStatus);
        Assert.True(unknownDetail.Material.TextPreview.Length <= 51);
        Assert.True(unknownDetail.Material.TextTruncated);
        Assert.All(unknownDetail.Segments, segment =>
        {
            Assert.True(segment.TextPreview.Length <= 51);
            Assert.True(segment.TextTruncated);
        });
        var unknownSegmentDetail = await service.GetSourceSegmentDetailAsync(
            new GetReferenceSourceSegmentDetailPayload(novel.Id, unknownAnchor.AnchorId, unknownMaterial.SourceSegmentId),
            CancellationToken.None);
        Assert.NotNull(unknownSegmentDetail);
        Assert.Equal("unknown", unknownSegmentDetail.Source.LicenseStatus);
        Assert.True(unknownSegmentDetail.Segment.TextPreview.Length <= 51);
        Assert.True(unknownSegmentDetail.Segment.TextTruncated);
    }

    [Fact]
    public async Task MaterialDetailProcessingNotesIncludeImportAndRebuildHistory()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("处理记录历史", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "processing-history.md",
            """
            雨声压住门口，林岚停了一下。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "处理记录参考", null, sourcePath, "markdown", "user_provided"),
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

        await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        var detail = await service.GetMaterialDetailAsync(
            new GetReferenceMaterialDetailPayload(novel.Id, material.MaterialId),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.True(detail.ProcessingNotes.Count >= 2);
        Assert.Contains(detail.ProcessingNotes, note => note.Status == ReferenceAnchorBuildStates.SegmentsBuilt);
        Assert.All(detail.ProcessingNotes, note =>
        {
            Assert.Equal(anchor.AnchorId.ToString(), note.AffectedSourceId);
            Assert.True(note.SourceSegmentCount > 0);
            if (note.Status != ReferenceAnchorBuildStates.SegmentsBuilt)
            {
                Assert.True(note.MaterialCount > 0);
            }

            Assert.DoesNotContain(sourcePath, note.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task SourceProcessingDetailReturnsRedactedHistoryAndAccessScopedSourceSummary()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("来源处理记录", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("其他来源处理记录", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "source-processing-detail.md",
            """
            雨声压住门口，林岚停了一下。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "来源处理参考", "作者", sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        var uncPath = @"\\server\share\reference\source-processing-detail.md";
        var fileUri = "file://server/share/reference/source-processing-detail.md";
        var sensitiveBuildError = $"Failed {Path.GetFullPath(sourcePath)} via {uncPath} and {fileUri} authorization=Bearer source-processing-token-abcdefghijklmnopqrstuvwxyz; prompt: hidden; candidate_text: forbidden; api_key: sk-procabcdefghijklmnopqrstuvwxyz1234567890";
        await UpdateBuildStateErrorAsync(options, anchor.AnchorId, sensitiveBuildError);

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(anchor.AnchorId, detail.Source.AnchorId);
        Assert.Equal("来源处理参考", detail.Source.Title);
        Assert.Equal(ReferenceAnchorOwnerScopes.Novel, detail.Source.OwnerScope);
        Assert.Equal(novel.Id, detail.Source.OwnerNovelId);
        Assert.True(detail.RetryAvailable);
        Assert.True(detail.RebuildAvailable);
        Assert.NotNull(detail.CurrentStatus);
        Assert.Equal(ReferenceAnchorBuildStates.FailedImport, detail.CurrentStatus.Status);
        Assert.Contains("[REDACTED", detail.CurrentStatus.Diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(uncPath, detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileUri, detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer source-processing-token", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-proc", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", detail.CurrentStatus.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.True(detail.Events.Count >= 2);
        Assert.All(detail.Events, item =>
        {
            Assert.Equal(anchor.AnchorId.ToString(), item.AffectedSourceId);
            Assert.True(item.SourceSegmentCount >= 0);
            Assert.True(item.MaterialCount >= 0);
            Assert.DoesNotContain(sourcePath, item.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFullPath(sourcePath), item.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(uncPath, item.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fileUri, item.Message, StringComparison.OrdinalIgnoreCase);
        });

        var serialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        Assert.DoesNotContain("source_path", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sourcePath, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.GetFullPath(sourcePath), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(uncPath, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileUri, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);

        var inaccessible = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(otherNovel.Id, anchor.AnchorId),
            CancellationToken.None);
        Assert.Null(inaccessible);
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

        var anchors = await service.GetAnchorsAsync(targetNovel.Id, CancellationToken.None);
        Assert.Contains(anchors, item => item.AnchorId == workspaceAnchor.AnchorId && item.NovelId == 0);
        Assert.DoesNotContain(anchors, item => item.AnchorId == privateAnchor.AnchorId);
        var status = await service.GetBuildStatusAsync(targetNovel.Id, workspaceAnchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(0, status.NovelId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, status.Status);

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
    public async Task WorkspaceCorpusMaterialsCanBeSearchedFromDifferentNovelsWithoutDuplicatingImport()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var firstNovel = await novels.CreateNovelAsync(new CreateNovelPayload("共享语料小说甲", "", ""), CancellationToken.None);
        var secondNovel = await novels.CreateNovelAsync(new CreateNovelPayload("共享语料小说乙", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "workspace-corpus-single-import.md",
            """
            # 第一章

            雨声压低街道，主角在门口停住。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(firstNovel.Id, "一次导入共享参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, anchor.AnchorId);
        var importedMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var importedSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        var firstSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                firstNovel.Id,
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
        var secondSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                secondNovel.Id,
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

        var firstMaterial = Assert.Single(firstSearch.Items, item => item.AnchorId == anchor.AnchorId);
        var secondMaterial = Assert.Single(secondSearch.Items, item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(firstMaterial.MaterialId, secondMaterial.MaterialId);
        Assert.Equal(firstMaterial.SourceSegmentId, secondMaterial.SourceSegmentId);
        Assert.Equal(firstMaterial.SourceHash, secondMaterial.SourceHash);
        var firstAnchorView = (await service.GetAnchorsAsync(firstNovel.Id, CancellationToken.None))
            .Single(item => item.AnchorId == anchor.AnchorId);
        var secondAnchorView = (await service.GetAnchorsAsync(secondNovel.Id, CancellationToken.None))
            .Single(item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(0, firstAnchorView.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, firstAnchorView.OwnerScope);
        Assert.Null(firstAnchorView.OwnerNovelId);
        Assert.Equal(0, secondAnchorView.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, secondAnchorView.OwnerScope);
        Assert.Null(secondAnchorView.OwnerNovelId);

        var currentMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var currentSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.Equal(importedMaterials.Select(item => item.MaterialId), currentMaterials.Select(item => item.MaterialId));
        Assert.Equal(importedSegments.Select(item => item.SegmentId), currentSegments.Select(item => item.SegmentId));
    }

    [Fact]
    public async Task CreateWorkspaceVisibleAnchorStoresAsSharedCorpusWithoutManualReparenting()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var sourceNovel = await novels.CreateNovelAsync(new CreateNovelPayload("创建共享语料来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("创建共享语料消费", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "create-workspace-corpus.md",
            """
            # 第一章

            雨声压低街道，主角在门口停住。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                sourceNovel.Id,
                "直接创建共享参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                Visibility: ReferenceCorpusVisibilities.Workspace,
                SourceTrust: ReferenceSourceTrustLevels.UserVerified,
                UserTags: ["shared"]),
            CancellationToken.None);
        var sourceView = Assert.Single(await service.GetAnchorsAsync(sourceNovel.Id, CancellationToken.None), item => item.AnchorId == anchor.AnchorId);
        var consumingView = Assert.Single(await service.GetAnchorsAsync(consumingNovel.Id, CancellationToken.None), item => item.AnchorId == anchor.AnchorId);

        Assert.Equal(0, anchor.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, anchor.OwnerScope);
        Assert.Null(anchor.OwnerNovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, sourceView.OwnerScope);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, consumingView.OwnerScope);
        Assert.Equal(["shared"], anchor.UserTags);

        var search = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
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
        var material = Assert.Single(search.Items, item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(anchor.AnchorId, material.AnchorId);
    }

    [Fact]
    public async Task CreateAnchorsImportsWorkspaceCorpusSourcesWithoutLosingMaterialIdentity()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var sourceNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量导入共享语料来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量导入共享语料消费", "", ""), CancellationToken.None);
        var firstPath = CreateSourceFile(
            "bulk-import-rain.md",
            """
            # 第一章

            雨声压低街道，主角在门口停住。
            """);
        var secondPath = CreateSourceFile(
            "bulk-import-cup.md",
            """
            # 第一章

            杯沿碰到木桌，声音很轻。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchors = await service.CreateAnchorsAsync(
            new CreateReferenceAnchorsPayload(
                [
                    new CreateReferenceAnchorPayload(
                        sourceNovel.Id,
                        "批量共享参考一",
                        null,
                        firstPath,
                        "markdown",
                        "user_provided",
                        Visibility: ReferenceCorpusVisibilities.Workspace,
                        SourceTrust: ReferenceSourceTrustLevels.Imported,
                        UserTags: ["bulk", "rain"]),
                    new CreateReferenceAnchorPayload(
                        sourceNovel.Id,
                        "批量共享参考二",
                        "参考作者",
                        secondPath,
                        "markdown",
                        "user_provided",
                        Visibility: ReferenceCorpusVisibilities.Workspace,
                        SourceTrust: ReferenceSourceTrustLevels.Imported,
                        UserTags: ["bulk", "cup"])
                ]),
            CancellationToken.None);

        Assert.Equal(["批量共享参考一", "批量共享参考二"], anchors.Select(anchor => anchor.Title).ToArray());
        Assert.Equal(2, anchors.Select(anchor => anchor.AnchorId).Distinct().Count());
        Assert.All(anchors, anchor =>
        {
            Assert.Equal(0, anchor.NovelId);
            Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, anchor.OwnerScope);
            Assert.Null(anchor.OwnerNovelId);
            Assert.Equal(ReferenceCorpusVisibilities.Workspace, anchor.Visibility);
            Assert.Equal(ReferenceSourceTrustLevels.Imported, anchor.SourceTrust);
        });

        var firstMaterials = await ReadMaterialRowsAsync(options, anchors[0].AnchorId);
        var secondMaterials = await ReadMaterialRowsAsync(options, anchors[1].AnchorId);
        Assert.NotEmpty(firstMaterials);
        Assert.NotEmpty(secondMaterials);
        Assert.All(firstMaterials, item => Assert.StartsWith(anchors[0].AnchorId + ":material:", item.MaterialId, StringComparison.Ordinal));
        Assert.All(secondMaterials, item => Assert.StartsWith(anchors[1].AnchorId + ":material:", item.MaterialId, StringComparison.Ordinal));
        Assert.Empty(firstMaterials.Select(item => item.MaterialId).Intersect(secondMaterials.Select(item => item.MaterialId), StringComparer.Ordinal));

        var consumingAnchors = await service.GetAnchorsAsync(consumingNovel.Id, CancellationToken.None);
        Assert.Contains(consumingAnchors, item => item.AnchorId == anchors[0].AnchorId && item.OwnerScope == ReferenceAnchorOwnerScopes.WorkspaceCorpus);
        Assert.Contains(consumingAnchors, item => item.AnchorId == anchors[1].AnchorId && item.OwnerScope == ReferenceAnchorOwnerScopes.WorkspaceCorpus);

        var rainSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
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
        var cupSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [],
                Query: "杯沿",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Contains(rainSearch.Items, item => item.AnchorId == anchors[0].AnchorId);
        Assert.Contains(cupSearch.Items, item => item.AnchorId == anchors[1].AnchorId);
    }

    [Fact]
    public async Task CreateAnchorIsIdempotentForSameSourcePathAndPreservesMaterialState()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("幂等导入", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "idempotent-import.md",
            """
            雨声压住门口，林岚停了一下。

            杯沿碰到桌面，声音很轻。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var first = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "初次导入", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var beforeRows = await ReadMaterialRowsAsync(options, first.AnchorId);
        Assert.True(beforeRows.Count >= 2);
        var archivedMaterialId = beforeRows[0].MaterialId;
        var verifiedMaterialId = beforeRows[1].MaterialId;

        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [archivedMaterialId]),
            CancellationToken.None);
        await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                novel.Id,
                verifiedMaterialId,
                FunctionTag: "environment",
                EmotionTag: "suspense",
                SceneTag: null,
                PovTag: null,
                TechniqueTag: null,
                Origin: "user",
                Note: "manual correction"),
            CancellationToken.None);

        var duplicate = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "重复导入不应覆盖", "新作者", sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var batch = await service.CreateAnchorsAsync(
            new CreateReferenceAnchorsPayload(
                [
                    new CreateReferenceAnchorPayload(novel.Id, "批量重复一", null, sourcePath, "markdown", "user_provided"),
                    new CreateReferenceAnchorPayload(novel.Id, "批量重复二", null, sourcePath, "markdown", "user_provided")
                ]),
            CancellationToken.None);

        Assert.Equal(first.AnchorId, duplicate.AnchorId);
        Assert.Equal([first.AnchorId, first.AnchorId], batch.Select(anchor => anchor.AnchorId).ToArray());
        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        Assert.Single(anchors, anchor => anchor.SourcePath == Path.GetFullPath(sourcePath));

        var afterRows = await ReadMaterialRowsAsync(options, first.AnchorId);
        Assert.Equal(beforeRows.Select(row => row.MaterialId), afterRows.Select(row => row.MaterialId));
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        var verified = Assert.Single(afterRows, row => row.MaterialId == verifiedMaterialId);
        Assert.True(verified.UserVerified);
        Assert.Equal("environment", verified.FunctionTag);
        Assert.Equal("suspense", verified.EmotionTag);
    }

    [Fact]
    public async Task ConcurrentDuplicateImportsAcrossServiceInstancesReuseSingleAnchor()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("并发重复导入", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "concurrent-duplicate-import.md",
            """
            雨声压住门口，林岚停了一下。

            杯沿碰到桌面，声音很轻。
            """);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var imports = Enumerable.Range(0, 8)
            .Select(index =>
            {
                var service = new SqliteReferenceAnchorService(options, novels);
                return Task.Run(async () =>
                {
                    await start.Task;
                    return await service.CreateAnchorAsync(
                        new CreateReferenceAnchorPayload(novel.Id, $"并发导入 {index}", null, sourcePath, "markdown", "user_provided"),
                        CancellationToken.None);
                });
            })
            .ToArray();

        start.SetResult();
        var anchors = await Task.WhenAll(imports);

        var anchorId = Assert.Single(anchors.Select(anchor => anchor.AnchorId).Distinct());
        Assert.All(anchors, anchor => Assert.Equal(anchorId, anchor.AnchorId));
        var service = new SqliteReferenceAnchorService(options, novels);
        var listed = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        Assert.Single(listed, anchor => anchor.SourcePath == Path.GetFullPath(sourcePath));
        var rows = await ReadMaterialRowsAsync(options, anchorId);
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Select(row => row.MaterialId).Distinct(StringComparer.Ordinal).Count(), rows.Count);
        var search = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchorId],
                "雨声",
                [],
                [],
                [],
                [],
                [],
                1,
                20),
            CancellationToken.None);
        Assert.NotEmpty(search.Items);
        Assert.All(search.Items, item => Assert.Equal(anchorId, item.AnchorId));
    }

    [Fact]
    public async Task DuplicateImportIdentityIsRejectedAtDatabaseLevel()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("数据库唯一导入", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "database-duplicate-import.md",
            """
            雨声压住门口，林岚停了一下。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "数据库唯一参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => InsertRawDuplicateAnchorIdentityAsync(options, 900_001, anchor, storedNovelId: novel.Id));

        Assert.Equal(19, exception.SqliteErrorCode);
        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        Assert.Single(anchors, item => item.SourcePath == anchor.SourcePath && item.SourceFileHash == anchor.SourceFileHash);
    }

    [Fact]
    public async Task WorkspaceDuplicateImportIdentityTreatsNullAndZeroNovelIdAsSameDatabaseScope()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("工作区数据库唯一导入", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "workspace-database-duplicate-import.md",
            """
            杯沿碰到桌面，声音很轻。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "工作区数据库唯一参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);

        Assert.Null(await ReadAnchorStoredNovelIdAsync(options, anchor.AnchorId));
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => InsertRawDuplicateAnchorIdentityAsync(options, 900_002, anchor, storedNovelId: 0));

        Assert.Equal(19, exception.SqliteErrorCode);
        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        Assert.Single(anchors, item => item.SourcePath == anchor.SourcePath && item.SourceFileHash == anchor.SourceFileHash);
    }

    [Fact]
    public async Task SchemaUpgradeQuarantinesLegacyDuplicateImportIdentitiesBeforeCreatingUniqueIndex()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("历史重复来源迁移", "", ""), CancellationToken.None);
        var sourcePath = Path.GetFullPath(CreateSourceFile(
            "legacy-duplicate-import.md",
            """
            旧版本可能已经写入重复来源。
            """));
        await CreateLegacyDuplicateImportIdentityAnchorsAsync(options, novel.Id, sourcePath);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);

        var imported = anchors
            .Where(anchor => anchor.SourcePath == sourcePath)
            .OrderBy(anchor => anchor.AnchorId)
            .ToArray();
        Assert.Equal([910_001L, 910_002L], imported.Select(anchor => anchor.AnchorId).ToArray());
        Assert.Equal("legacy-source-hash", imported[0].SourceFileHash);
        Assert.Equal("legacy-duplicate:910002:legacy-source-hash", imported[1].SourceFileHash);
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => InsertRawDuplicateAnchorIdentityAsync(options, 910_003, imported[0], storedNovelId: novel.Id));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task SchemaUpgradeBackfillsProcessingAttemptsAndEventReferences()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("历史处理尝试迁移", "", ""), CancellationToken.None);
        await CreateLegacyProcessingEventsWithoutAttemptMetadataAsync(options, novel.Id);
        var service = new SqliteReferenceAnchorService(options, novels);

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, 920_001),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(2, detail.AttemptCount);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(2, detail.CurrentAttempt.AttemptNumber);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, detail.CurrentAttempt.Status);
        Assert.NotEmpty(detail.CurrentAttempt.RecoveredFromAttemptId);
        Assert.NotEmpty(detail.CurrentAttempt.RecoveredFromBuildId);
        Assert.NotNull(detail.PriorAttempts);
        var prior = Assert.Single(detail.PriorAttempts);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, prior.Status);
        Assert.Contains("legacy first failure", prior.BlockedReason, StringComparison.OrdinalIgnoreCase);

        var persistedAttempts = await ReadProcessingAttemptRowsAsync(options, 920_001);
        Assert.Equal(2, persistedAttempts.Count);
        var eventRefs = await ReadProcessingEventAttemptRefsAsync(options, 920_001);
        Assert.Equal(2, eventRefs.Count);
        Assert.All(eventRefs, item => Assert.Contains(persistedAttempts, attempt => attempt.AttemptId == item.AttemptId));

        _ = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, 920_001),
            CancellationToken.None);
        Assert.Equal(2, (await ReadProcessingAttemptRowsAsync(options, 920_001)).Count);
    }

    [Fact]
    public async Task ProcessingAttemptBackfillRebuildsMixedPersistedAndLegacyEventHistory()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("混合处理历史回填", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("mixed-processing-history.md", "雨声压住门口。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "混合处理历史参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var initialAttempts = await ReadProcessingAttemptRowsAsync(options, anchor.AnchorId);
        Assert.Single(initialAttempts);

        await UpdateAnchorBuildStatusAsync(
            options,
            anchor.AnchorId,
            ReferenceAnchorBuildStates.FailedEmbedding,
            ReferenceAnchorBuildStates.FailedEmbedding);

        var detail = await service.GetSourceProcessingDetailAsync(
            new GetReferenceSourceProcessingDetailPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(2, detail.AttemptCount);
        Assert.NotNull(detail.CurrentAttempt);
        Assert.Equal(2, detail.CurrentAttempt.AttemptNumber);
        Assert.Equal(ReferenceAnchorBuildStates.FailedEmbedding, detail.CurrentAttempt.Status);
        var persistedAttempts = await ReadProcessingAttemptRowsAsync(options, anchor.AnchorId);
        Assert.Equal([1, 2], persistedAttempts.Select(item => item.AttemptNumber).ToArray());
        Assert.Equal(2, persistedAttempts.Select(item => item.AttemptId).Distinct(StringComparer.Ordinal).Count());
        var eventRefs = await ReadProcessingEventAttemptRefsAsync(options, anchor.AnchorId);
        Assert.All(eventRefs, item =>
        {
            Assert.NotEmpty(item.AttemptId);
            Assert.NotEmpty(item.BuildId);
            Assert.Contains(persistedAttempts, attempt => attempt.AttemptId == item.AttemptId);
        });
    }

    [Fact]
    public async Task CreateAnchorKeepsDistinctSourcePathsWithIdenticalContent()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("同内容不同来源", "", ""), CancellationToken.None);
        var firstPath = CreateSourceFile(
            "same-content-first.md",
            """
            雨声压住门口，林岚停了一下。
            """);
        var secondPath = CreateSourceFile(
            "same-content-second.md",
            """
            雨声压住门口，林岚停了一下。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);

        var first = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "第一份来源", null, firstPath, "markdown", "user_provided"),
            CancellationToken.None);
        var second = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "第二份来源", null, secondPath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.NotEqual(first.AnchorId, second.AnchorId);
        Assert.Equal(first.SourceFileHash, second.SourceFileHash);
        var anchors = await service.GetAnchorsAsync(novel.Id, CancellationToken.None);
        Assert.Contains(anchors, anchor => anchor.AnchorId == first.AnchorId && anchor.SourcePath == Path.GetFullPath(firstPath));
        Assert.Contains(anchors, anchor => anchor.AnchorId == second.AnchorId && anchor.SourcePath == Path.GetFullPath(secondPath));
    }

    [Fact]
    public async Task CreateAnchorDoesNotReturnStaleMaterialsWhenSourcePathContentChanges()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("同路径内容变更", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "same-path-content-change.md",
            """
            雨声压住门口，林岚停了一下。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var first = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "初始来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        File.WriteAllText(
            sourcePath,
            """
            杯沿碰到桌面，声音很轻。
            """);

        var changed = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "变更后来源", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        Assert.NotEqual(first.SourceFileHash, changed.SourceFileHash);
        var changedMaterials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [changed.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.Contains(changedMaterials.Items, material => material.Text.Contains("杯沿", StringComparison.Ordinal));
        Assert.DoesNotContain(changedMaterials.Items, material => material.Text.Contains("雨声", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateAnchorsValidatesBatchSize()
    {
        var options = CreateOptions();
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var service = new SqliteReferenceAnchorService(options, novels);

        var empty = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAnchorsAsync(new CreateReferenceAnchorsPayload([]), CancellationToken.None));
        Assert.Contains("At least one reference anchor", empty.Message, StringComparison.Ordinal);

        var tooManyInputs = Enumerable.Range(0, 51)
            .Select(index => new CreateReferenceAnchorPayload(1, $"参考 {index}", null, "missing.md", "markdown", "user_provided"))
            .ToArray();
        var tooMany = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.CreateAnchorsAsync(new CreateReferenceAnchorsPayload(tooManyInputs), CancellationToken.None));
        Assert.Contains("At most 50 reference anchors", tooMany.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PromotePerNovelAnchorToWorkspaceCorpusPreservesMaterialIdentityAndFeedbackScope()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var sourceNovel = await novels.CreateNovelAsync(new CreateNovelPayload("提升共享语料来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("提升共享语料消费", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "promote-workspace-corpus.md",
            """
            # 第一章

            雨声压低街道，主角在门口停住。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(sourceNovel.Id, "待提升共享参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var importedMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var importedSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        var sourceMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                sourceNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "门口",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None)).Items);
        var correctedMaterial = await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                sourceNovel.Id,
                sourceMaterial.MaterialId,
                FunctionTag: "interiority",
                EmotionTag: "restrained",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "user verified before promotion"),
            CancellationToken.None);
        var adapted = await service.AdaptMaterialAsync(
            new AdaptReferenceMaterialPayload(
                sourceNovel.Id,
                correctedMaterial.MaterialId,
                [],
                ReferenceRewriteLevels.L1,
                SceneFacts: ["门口"]),
            CancellationToken.None);
        Assert.Equal("passed", adapted.Audit.Status);
        await service.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                sourceNovel.Id,
                ReferenceFeedbackTargetTypes.Material,
                correctedMaterial.MaterialId,
                ReferenceFeedbackDecisions.Accepted,
                correctedMaterial.MaterialId,
                CandidateId: string.Empty,
                BlueprintId: 0,
                BeatId: string.Empty,
                FeedbackTags: ["source_novel_usage"],
                Note: "source novel accepted the material before promotion",
                EditedText: string.Empty,
                Origin: "user"),
            CancellationToken.None);

        var hiddenBeforePromotion = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
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
        Assert.Empty(hiddenBeforePromotion.Items);

        var promoted = await service.PromoteAnchorToWorkspaceCorpusAsync(
            new PromoteReferenceAnchorToWorkspaceCorpusPayload(
                sourceNovel.Id,
                anchor.AnchorId,
                SourceTrust: ReferenceSourceTrustLevels.Imported,
                UserTags: ["migrated", "shared"]),
            CancellationToken.None);

        Assert.Equal(0, promoted.NovelId);
        Assert.Equal(ReferenceCorpusVisibilities.Workspace, promoted.Visibility);
        Assert.Equal(ReferenceSourceTrustLevels.Imported, promoted.SourceTrust);
        Assert.Equal(["migrated", "shared"], promoted.UserTags);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, promoted.OwnerScope);
        Assert.Null(promoted.OwnerNovelId);

        var consumingSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
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
        var consumingMaterial = Assert.Single(consumingSearch.Items, item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(sourceMaterial.MaterialId, consumingMaterial.MaterialId);
        Assert.Equal(sourceMaterial.SourceSegmentId, consumingMaterial.SourceSegmentId);
        Assert.Equal(sourceMaterial.SourceHash, consumingMaterial.SourceHash);
        Assert.True(consumingMaterial.UserVerified);
        Assert.Equal("interiority", consumingMaterial.FunctionTag);
        Assert.Equal("restrained", consumingMaterial.EmotionTag);
        Assert.Equal("close", consumingMaterial.PovTag);
        Assert.Equal("afterbeat", consumingMaterial.TechniqueTag);

        var currentMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var currentSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.Equal(importedMaterials.Select(item => item.MaterialId), currentMaterials.Select(item => item.MaterialId));
        Assert.Equal(importedSegments.Select(item => item.SegmentId), currentSegments.Select(item => item.SegmentId));
        var currentMaterial = Assert.Single(currentMaterials, item => item.MaterialId == correctedMaterial.MaterialId);
        Assert.Equal(sourceMaterial.SourceHash, currentMaterial.SourceHash);
        Assert.True(currentMaterial.UserVerified);

        var reuseProvenance = await ReadReuseProvenanceAsync(options, adapted.CandidateId);
        Assert.Equal(correctedMaterial.MaterialId, reuseProvenance.CandidateMaterialId);
        var audit = Assert.Single(reuseProvenance.AuditRows);
        Assert.Equal(adapted.Audit.AuditId, audit.AuditId);
        Assert.Equal(correctedMaterial.MaterialId, audit.MaterialId);
        Assert.Equal("passed", audit.Status);

        var sourceFeedback = await service.GetUserFeedbackAsync(
            new GetReferenceUserFeedbackPayload(
                sourceNovel.Id,
                ReferenceFeedbackTargetTypes.Material,
                correctedMaterial.MaterialId,
                10),
            CancellationToken.None);
        var feedback = Assert.Single(sourceFeedback);
        Assert.Equal(sourceNovel.Id, feedback.NovelId);
        Assert.Equal(correctedMaterial.MaterialId, feedback.MaterialId);

        var consumingFeedback = await service.GetUserFeedbackAsync(
            new GetReferenceUserFeedbackPayload(
                consumingNovel.Id,
                ReferenceFeedbackTargetTypes.Material,
                correctedMaterial.MaterialId,
                10),
            CancellationToken.None);
        Assert.Empty(consumingFeedback);
    }

    [Fact]
    public async Task PromoteAnchorRequiresCurrentNovelOwnership()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("提升所有者", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("提升非所有者", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("private-promote-boundary.md", "只有所有者可以提升。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(ownerNovel.Id, "私有边界参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.PromoteAnchorToWorkspaceCorpusAsync(
                new PromoteReferenceAnchorToWorkspaceCorpusPayload(otherNovel.Id, anchor.AnchorId),
                CancellationToken.None));

        var otherSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                otherNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "所有者",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Empty(otherSearch.Items);
    }

    [Fact]
    public async Task PromoteAnchorPreservesExistingCorpusMetadataWhenOptionalFieldsAreOmitted()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("提升保留元数据", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("promote-preserve-metadata.md", "雨声压住门外的街。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "保留元数据参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                Visibility: ReferenceCorpusVisibilities.Private,
                SourceTrust: ReferenceSourceTrustLevels.Imported,
                UserTags: ["seed", "verified"]),
            CancellationToken.None);

        var promoted = await service.PromoteAnchorToWorkspaceCorpusAsync(
            new PromoteReferenceAnchorToWorkspaceCorpusPayload(novel.Id, anchor.AnchorId),
            CancellationToken.None);

        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, promoted.OwnerScope);
        Assert.Equal(ReferenceSourceTrustLevels.Imported, promoted.SourceTrust);
        Assert.Equal(["seed", "verified"], promoted.UserTags);
    }

    [Fact]
    public async Task LegacyPerNovelWorkspaceRowsAutoMigrateToNullableWorkspaceOwnership()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var sourceNovel = await novels.CreateNovelAsync(new CreateNovelPayload("自动迁移来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("自动迁移消费", "", ""), CancellationToken.None);
        var workspacePath = CreateSourceFile("auto-migrate-workspace.md", "旧工作区语料应该自动变成共享材料。");
        var privatePath = CreateSourceFile("auto-migrate-private.md", "私有语料仍只属于原小说。");
        var restrictedPath = CreateSourceFile("auto-migrate-restricted.md", "受限语料仍只属于原小说。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var workspaceAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(sourceNovel.Id, "旧工作区正数所有者", null, workspacePath, "markdown", "user_provided"),
            CancellationToken.None);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(sourceNovel.Id, "旧私有正数所有者", null, privatePath, "markdown", "user_provided"),
            CancellationToken.None);
        var restrictedAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(sourceNovel.Id, "旧受限正数所有者", null, restrictedPath, "markdown", "user_provided"),
            CancellationToken.None);
        var beforeMaterials = await ReadMaterialRowsAsync(options, workspaceAnchor.AnchorId);
        var beforeSegments = await ReadSourceSegmentsAsync(options, workspaceAnchor.AnchorId);
        await SetAnchorVisibilityOnlyAsync(options, workspaceAnchor.AnchorId, ReferenceCorpusVisibilities.Workspace);
        await SetAnchorVisibilityOnlyAsync(options, restrictedAnchor.AnchorId, ReferenceCorpusVisibilities.Restricted);
        Assert.Equal(sourceNovel.Id, await ReadAnchorStoredNovelIdAsync(options, workspaceAnchor.AnchorId));
        Assert.Equal(sourceNovel.Id, await ReadAnchorStoredNovelIdAsync(options, privateAnchor.AnchorId));
        Assert.Equal(sourceNovel.Id, await ReadAnchorStoredNovelIdAsync(options, restrictedAnchor.AnchorId));

        var migratedService = new SqliteReferenceAnchorService(options, novels);
        var consumingAnchors = await migratedService.GetAnchorsAsync(consumingNovel.Id, CancellationToken.None);

        var migrated = Assert.Single(consumingAnchors, item => item.AnchorId == workspaceAnchor.AnchorId);
        Assert.Equal(0, migrated.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, migrated.OwnerScope);
        Assert.Null(migrated.OwnerNovelId);
        Assert.Equal(ReferenceCorpusVisibilities.Workspace, migrated.Visibility);
        Assert.DoesNotContain(consumingAnchors, item => item.AnchorId == privateAnchor.AnchorId);
        Assert.DoesNotContain(consumingAnchors, item => item.AnchorId == restrictedAnchor.AnchorId);
        Assert.Null(await ReadAnchorStoredNovelIdAsync(options, workspaceAnchor.AnchorId));
        Assert.Equal(sourceNovel.Id, await ReadAnchorStoredNovelIdAsync(options, privateAnchor.AnchorId));
        Assert.Equal(sourceNovel.Id, await ReadAnchorStoredNovelIdAsync(options, restrictedAnchor.AnchorId));
        Assert.Equal(beforeMaterials.Select(item => item.MaterialId), (await ReadMaterialRowsAsync(options, workspaceAnchor.AnchorId)).Select(item => item.MaterialId));
        Assert.Equal(beforeSegments.Select(item => item.SegmentId), (await ReadSourceSegmentsAsync(options, workspaceAnchor.AnchorId)).Select(item => item.SegmentId));

        var consumingSearch = await migratedService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [],
                Query: "共享材料",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Contains(consumingSearch.Items, item => item.AnchorId == workspaceAnchor.AnchorId);
        Assert.DoesNotContain(consumingSearch.Items, item => item.AnchorId == privateAnchor.AnchorId);
        Assert.DoesNotContain(consumingSearch.Items, item => item.AnchorId == restrictedAnchor.AnchorId);
    }

    [Fact]
    public async Task PromoteAnchorsToWorkspaceCorpusPromotesOwnedRowsAtomically()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量提升来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量提升消费", "", ""), CancellationToken.None);
        var firstPath = CreateSourceFile("bulk-promote-first.md", "雨声压低街道，主角在门口停住。");
        var secondPath = CreateSourceFile("bulk-promote-second.md", "杯沿碰到木桌，声音很轻。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var first = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(ownerNovel.Id, "批量参考一", null, firstPath, "markdown", "user_provided"),
            CancellationToken.None);
        var second = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(ownerNovel.Id, "批量参考二", null, secondPath, "markdown", "user_provided"),
            CancellationToken.None);
        var firstMaterials = await ReadMaterialRowsAsync(options, first.AnchorId);
        var secondMaterials = await ReadMaterialRowsAsync(options, second.AnchorId);

        var promoted = await service.PromoteAnchorsToWorkspaceCorpusAsync(
            new PromoteReferenceAnchorsToWorkspaceCorpusPayload(
                ownerNovel.Id,
                [first.AnchorId, second.AnchorId],
                SourceTrust: ReferenceSourceTrustLevels.Imported,
                UserTags: ["bulk", "workspace"]),
            CancellationToken.None);

        Assert.Equal([first.AnchorId, second.AnchorId], promoted.Select(anchor => anchor.AnchorId).ToArray());
        Assert.All(promoted, anchor =>
        {
            Assert.Equal(0, anchor.NovelId);
            Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, anchor.OwnerScope);
            Assert.Equal(ReferenceCorpusVisibilities.Workspace, anchor.Visibility);
            Assert.Equal(ReferenceSourceTrustLevels.Imported, anchor.SourceTrust);
            Assert.Equal(["bulk", "workspace"], anchor.UserTags);
        });
        Assert.Equal(firstMaterials.Select(item => item.MaterialId), (await ReadMaterialRowsAsync(options, first.AnchorId)).Select(item => item.MaterialId));
        Assert.Equal(secondMaterials.Select(item => item.MaterialId), (await ReadMaterialRowsAsync(options, second.AnchorId)).Select(item => item.MaterialId));

        var firstConsumingSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
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
        var secondConsumingSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [],
                Query: "杯沿",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Contains(firstConsumingSearch.Items, item => item.AnchorId == first.AnchorId);
        Assert.Contains(secondConsumingSearch.Items, item => item.AnchorId == second.AnchorId);
    }

    [Fact]
    public async Task UpdateAnchorMetadataCanPromoteToWorkspaceCorpusWithoutChangingMaterialIdentity()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("元数据编辑来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("元数据编辑消费", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("update-anchor-metadata.md", "雨声压低街道，主角在门口停住。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(ownerNovel.Id, "待编辑参考", "旧作者", sourcePath, "markdown", "unknown"),
            CancellationToken.None);
        var beforeMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var beforeSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);

        var updated = await service.UpdateAnchorMetadataAsync(
            new UpdateReferenceAnchorMetadataPayload(
                ownerNovel.Id,
                anchor.AnchorId,
                "已整理共享参考",
                "新作者",
                "licensed",
                ReferenceCorpusVisibilities.Workspace,
                ReferenceSourceTrustLevels.Imported,
                ["curated", "rain"]),
            CancellationToken.None);

        Assert.Equal(0, updated.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, updated.OwnerScope);
        Assert.Null(updated.OwnerNovelId);
        Assert.Equal("已整理共享参考", updated.Title);
        Assert.Equal("新作者", updated.Author);
        Assert.Equal("licensed", updated.LicenseStatus);
        Assert.Equal(ReferenceCorpusVisibilities.Workspace, updated.Visibility);
        Assert.Equal(ReferenceSourceTrustLevels.Imported, updated.SourceTrust);
        Assert.Equal(["curated", "rain"], updated.UserTags);
        Assert.Equal(anchor.SourceFileHash, updated.SourceFileHash);

        var afterMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var afterSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeMaterials.Select(item => item.MaterialId), afterMaterials.Select(item => item.MaterialId));
        Assert.Equal(beforeSegments.Select(item => item.SegmentId), afterSegments.Select(item => item.SegmentId));

        var consumingSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
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
        var material = Assert.Single(consumingSearch.Items, item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(beforeMaterials.Select(item => item.MaterialId), afterMaterials.Select(item => item.MaterialId));
        Assert.Equal(beforeMaterials.First(item => item.MaterialId == material.MaterialId).SourceSegmentId, material.SourceSegmentId);
    }

    [Fact]
    public async Task UpdateAnchorMetadataCannotBypassOtherNovelPrivateOrWorkspaceRestrictedVisibility()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("元数据私有所有者", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("元数据私有外部", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchorService(options, novels);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                ownerNovel.Id,
                "私有参考",
                null,
                CreateSourceFile("private-update-anchor-metadata.md", "只有所有者可以编辑。"),
                "markdown",
                "user_provided"),
            CancellationToken.None);
        var restrictedAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                ownerNovel.Id,
                "受限共享参考",
                null,
                CreateSourceFile("restricted-update-anchor-metadata.md", "受限共享不能被外部小说编辑。"),
                "markdown",
                "user_provided",
                Visibility: ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);
        await MarkAnchorAsNullableWorkspaceCorpusAsync(options, restrictedAnchor.AnchorId, ReferenceCorpusVisibilities.Restricted);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateAnchorMetadataAsync(
                new UpdateReferenceAnchorMetadataPayload(
                    otherNovel.Id,
                    privateAnchor.AnchorId,
                    "外部编辑私有参考",
                    "",
                    "licensed",
                    ReferenceCorpusVisibilities.Workspace,
                    ReferenceSourceTrustLevels.Imported,
                    ["blocked"]),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateAnchorMetadataAsync(
                new UpdateReferenceAnchorMetadataPayload(
                    otherNovel.Id,
                    restrictedAnchor.AnchorId,
                    "外部编辑受限参考",
                    "",
                    "licensed",
                    ReferenceCorpusVisibilities.Workspace,
                    ReferenceSourceTrustLevels.Imported,
                    ["blocked"]),
                CancellationToken.None));

        var otherSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                otherNovel.Id,
                AnchorIds: [privateAnchor.AnchorId, restrictedAnchor.AnchorId],
                Query: "编辑",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Empty(otherSearch.Items);
    }

    [Fact]
    public async Task NullableWorkspaceCorpusMaterialsCanBeSearchedFromDifferentNovelsWithoutDuplicatingImport()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var firstNovel = await novels.CreateNovelAsync(new CreateNovelPayload("空所有者共享语料小说甲", "", ""), CancellationToken.None);
        var secondNovel = await novels.CreateNovelAsync(new CreateNovelPayload("空所有者共享语料小说乙", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "nullable-workspace-corpus.md",
            """
            # 第一章

            雨声压低街道，主角在门口停住。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(firstNovel.Id, "空所有者共享参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsNullableWorkspaceCorpusAsync(options, anchor.AnchorId);
        var importedMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var importedSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);

        var firstSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                firstNovel.Id,
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
        var secondSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                secondNovel.Id,
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

        var firstMaterial = Assert.Single(firstSearch.Items, item => item.AnchorId == anchor.AnchorId);
        var secondMaterial = Assert.Single(secondSearch.Items, item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(firstMaterial.MaterialId, secondMaterial.MaterialId);
        Assert.Equal(firstMaterial.SourceSegmentId, secondMaterial.SourceSegmentId);
        Assert.Equal(firstMaterial.SourceHash, secondMaterial.SourceHash);
        var firstAnchorView = (await service.GetAnchorsAsync(firstNovel.Id, CancellationToken.None))
            .Single(item => item.AnchorId == anchor.AnchorId);
        var secondAnchorView = (await service.GetAnchorsAsync(secondNovel.Id, CancellationToken.None))
            .Single(item => item.AnchorId == anchor.AnchorId);
        Assert.Equal(0, firstAnchorView.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, firstAnchorView.OwnerScope);
        Assert.Null(firstAnchorView.OwnerNovelId);
        Assert.Equal(0, secondAnchorView.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, secondAnchorView.OwnerScope);
        Assert.Null(secondAnchorView.OwnerNovelId);

        var status = await service.GetBuildStatusAsync(secondNovel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(0, status.NovelId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, status.Status);
        var currentMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var currentSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.Equal(importedMaterials.Select(item => item.MaterialId), currentMaterials.Select(item => item.MaterialId));
        Assert.Equal(importedSegments.Select(item => item.SegmentId), currentSegments.Select(item => item.SegmentId));
    }

    [Fact]
    public async Task NullableWorkspaceCorpusVisibilityCannotBeBypassedWithExplicitAnchorIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("空所有者可见性目标", "", ""), CancellationToken.None);
        var visibleSourcePath = CreateSourceFile("nullable-workspace-visible.md", "他握住{{object}}，把话咽回去，只听雨声压住门外的街。");
        var privateSourcePath = CreateSourceFile("nullable-workspace-private.md", "他握住{{object}}，提到了不应泄露的私有线索。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var visibleAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(targetNovel.Id, "空所有者可见参考", null, visibleSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(targetNovel.Id, "空所有者私有参考", null, privateSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsNullableWorkspaceCorpusAsync(options, visibleAnchor.AnchorId, ReferenceCorpusVisibilities.Workspace);
        await MarkAnchorAsNullableWorkspaceCorpusAsync(options, privateAnchor.AnchorId, ReferenceCorpusVisibilities.Private);

        var anchors = await service.GetAnchorsAsync(targetNovel.Id, CancellationToken.None);
        Assert.Contains(anchors, item => item.AnchorId == visibleAnchor.AnchorId && item.NovelId == 0);
        Assert.DoesNotContain(anchors, item => item.AnchorId == privateAnchor.AnchorId);

        var explicitPrivateSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [privateAnchor.AnchorId],
                Query: "{{object}}",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.Empty(explicitPrivateSearch.Items);
        Assert.Null(await service.GetBuildStatusAsync(targetNovel.Id, privateAnchor.AnchorId, CancellationToken.None));
    }

    [Fact]
    public async Task WorkspaceCorpusVisibilityFiltersAnchorsBeforeSearchAdaptAuditTagAndFeedback()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("共享语料可见性目标", "", ""), CancellationToken.None);
        var visibleSourcePath = CreateSourceFile("workspace-visible.md", "他握住{{object}}，把话咽回去，只听雨声压住门外的街。");
        var privateSourcePath = CreateSourceFile("workspace-private.md", "他握住{{object}}，提到了另一部小说的私有线索。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var visibleAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(targetNovel.Id, "工作区可见参考", null, visibleSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(targetNovel.Id, "工作区私有参考", null, privateSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var privateMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
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
        await MarkAnchorAsWorkspaceCorpusAsync(options, visibleAnchor.AnchorId, "workspace");
        await MarkAnchorAsWorkspaceCorpusAsync(options, privateAnchor.AnchorId, "private");

        var anchors = await service.GetAnchorsAsync(targetNovel.Id, CancellationToken.None);
        Assert.Contains(anchors, item =>
            item.AnchorId == visibleAnchor.AnchorId &&
            item.NovelId == 0 &&
            item.Visibility == ReferenceCorpusVisibilities.Workspace &&
            item.SourceTrust == ReferenceSourceTrustLevels.UserVerified &&
            item.UserTags.Count == 0);
        Assert.DoesNotContain(anchors, item => item.AnchorId == privateAnchor.AnchorId);

        var defaultSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [],
                Query: "{{object}}",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        var visibleMaterial = Assert.Single(defaultSearch.Items);
        Assert.Equal(visibleAnchor.AnchorId, visibleMaterial.AnchorId);

        var explicitPrivateSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [privateAnchor.AnchorId],
                Query: "{{object}}",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Empty(explicitPrivateSearch.Items);

        var adapted = await service.AdaptMaterialAsync(
            new AdaptReferenceMaterialPayload(
                targetNovel.Id,
                visibleMaterial.MaterialId,
                [new ReferenceSlotValuePayload("object", "门把手")],
                ReferenceRewriteLevels.L1,
                SceneFacts: ["门把手"]),
            CancellationToken.None);
        Assert.Equal("passed", adapted.Audit.Status);
        var feedback = await service.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                targetNovel.Id,
                ReferenceFeedbackTargetTypes.ReuseCandidate,
                adapted.CandidateId,
                ReferenceFeedbackDecisions.Accepted,
                visibleMaterial.MaterialId,
                adapted.CandidateId,
                BlueprintId: 0,
                BeatId: string.Empty,
                FeedbackTags: ["workspace_visible_usage"],
                Note: "visible corpus material can be used by this novel",
                EditedText: string.Empty,
                Origin: "user"),
            CancellationToken.None);
        Assert.Equal(visibleMaterial.MaterialId, feedback.MaterialId);
        var audit = await service.AuditCandidateAsync(
            new AuditReferenceReusePayload(
                targetNovel.Id,
                visibleMaterial.MaterialId,
                visibleMaterial.Text,
                ReferenceRewriteLevels.L0,
                SceneFacts: []),
            CancellationToken.None);
        Assert.Equal("passed", audit.Status);
        var updated = await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                targetNovel.Id,
                visibleMaterial.MaterialId,
                FunctionTag: "interiority",
                EmotionTag: null,
                SceneTag: null,
                PovTag: null,
                TechniqueTag: null,
                Origin: "user",
                Note: "visible corpus correction"),
            CancellationToken.None);
        Assert.True(updated.UserVerified);

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
                    "他握住门把手。",
                    ReferenceRewriteLevels.L1,
                    SceneFacts: ["门把手"]),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateMaterialTagsAsync(
                new UpdateReferenceMaterialTagsPayload(
                    targetNovel.Id,
                    privateMaterial.MaterialId,
                    FunctionTag: "interiority",
                    EmotionTag: null,
                    SceneTag: null,
                    PovTag: null,
                    TechniqueTag: null,
                    Origin: "user",
                    Note: "private corpus correction must be blocked"),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.RecordUserFeedbackAsync(
                new RecordReferenceUserFeedbackPayload(
                    targetNovel.Id,
                    ReferenceFeedbackTargetTypes.Material,
                    privateMaterial.MaterialId,
                    ReferenceFeedbackDecisions.Accepted,
                    privateMaterial.MaterialId,
                    CandidateId: string.Empty,
                    BlueprintId: 0,
                    BeatId: string.Empty,
                    FeedbackTags: ["blocked"],
                    Note: "private corpus material must not accept feedback from another novel scope",
                    EditedText: string.Empty,
                    Origin: "user"),
                CancellationToken.None));
    }

    [Fact]
    public async Task LegacyWorkspaceCorpusRowsMigrateToWorkspaceVisibleWithoutLosingMaterialIdentity()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("旧共享语料目标", "", ""), CancellationToken.None);
        await CreateLegacyWorkspaceCorpusAnchorAsync(options);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchors = await service.GetAnchorsAsync(targetNovel.Id, CancellationToken.None);

        var anchor = Assert.Single(anchors, item => item.AnchorId == 7001);
        Assert.Equal(0, anchor.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, anchor.OwnerScope);
        Assert.Null(anchor.OwnerNovelId);
        Assert.Equal("legacy-source-hash", anchor.SourceFileHash);
        Assert.Equal(ReferenceCorpusVisibilities.Workspace, anchor.Visibility);
        Assert.Equal(ReferenceSourceTrustLevels.UserVerified, anchor.SourceTrust);
        Assert.Empty(anchor.UserTags);
        var status = await service.GetBuildStatusAsync(targetNovel.Id, 7001, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(0, status.NovelId);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, status.Status);

        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [],
                Query: "旧共享语料",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        var material = Assert.Single(materials.Items);
        Assert.Equal("7001:material:sentence:0:legacy", material.MaterialId);
        Assert.Equal(7001, material.AnchorId);
        Assert.Equal("7001:0:sentence:0:legacy", material.SourceSegmentId);
        Assert.Equal("legacy-material-hash", material.SourceHash);
    }

    [Fact]
    public async Task LegacyReferenceAnchorSchemaAllowsMigratingWorkspaceCorpusRowsToNullableOwnership()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("旧 schema 空所有者迁移目标", "", ""), CancellationToken.None);
        await CreateLegacyWorkspaceCorpusAnchorAsync(options);
        var service = new SqliteReferenceAnchorService(options, novels);

        _ = await service.GetAnchorsAsync(targetNovel.Id, CancellationToken.None);
        await MarkAnchorAsNullableWorkspaceCorpusAsync(options, 7001);
        var anchors = await service.GetAnchorsAsync(targetNovel.Id, CancellationToken.None);

        var anchor = Assert.Single(anchors, item => item.AnchorId == 7001);
        Assert.Equal(0, anchor.NovelId);
        Assert.Equal(ReferenceAnchorOwnerScopes.WorkspaceCorpus, anchor.OwnerScope);
        Assert.Null(anchor.OwnerNovelId);
        Assert.Equal(ReferenceCorpusVisibilities.Workspace, anchor.Visibility);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                AnchorIds: [],
                Query: "旧共享语料",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        var material = Assert.Single(materials.Items);
        Assert.Equal("7001:material:sentence:0:legacy", material.MaterialId);
        Assert.Equal("7001:0:sentence:0:legacy", material.SourceSegmentId);
        Assert.Equal("legacy-material-hash", material.SourceHash);
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
        var updated = await service.UpdateMaterialTagsAsync(
            new UpdateReferenceMaterialTagsPayload(
                targetNovel.Id,
                workspaceMaterial.MaterialId,
                FunctionTag: "interiority",
                EmotionTag: "restrained",
                SceneTag: null,
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "correct shared corpus tag"),
            CancellationToken.None);
        Assert.True(updated.UserVerified);
        Assert.Equal("interiority", updated.FunctionTag);
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
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateMaterialTagsAsync(
                new UpdateReferenceMaterialTagsPayload(
                    targetNovel.Id,
                    privateMaterial.MaterialId,
                    FunctionTag: "interiority",
                    EmotionTag: null,
                    SceneTag: null,
                    PovTag: null,
                    TechniqueTag: null,
                    Origin: "user",
                    Note: "must not cross private anchor boundary"),
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
                [],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        var rainMaterial = Assert.Single(
            discovered.Items,
            item => item.Text == "雨声压低了门口。" && item.MaterialType == ReferenceMaterialTypes.Sentence);
        var doorwayMaterial = Assert.Single(
            discovered.Items,
            item => item.Text == "他在门口停住。" && item.MaterialType == ReferenceMaterialTypes.Sentence);
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
    public async Task SearchMaterialsFiltersAndScoresByProseDutyStoryContext()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("文体职责搜索测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "search-prose-duty.md",
            """
            # 第一章

            雨声压低了街面。

            她只把杯子推远。

            她说：不用。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "文体职责参考", null, sourcePath, "markdown", "user_provided"),
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
                ProseDuties: ["source_backed_detail"]),
            CancellationToken.None);

        var material = Assert.Single(result.Items);
        Assert.Equal("雨声压低了街面。", material.Text);
        Assert.Equal("environment", material.FunctionTag);
        Assert.Equal("sensory_detail", material.TechniqueTag);
        Assert.NotNull(material.ScoreComponents);
        Assert.True(material.ScoreComponents["prose_duty"] > 0);
    }

    [Fact]
    public async Task SearchMaterialsBoostsAcceptedMaterialFeedbackOnlyForCurrentNovel()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var firstNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料搜索反馈目标", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料搜索反馈隔离", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "search-feedback-boost.md",
            """
            # 第一章

            雨声压低街道，主角站在门口。

            主角在门口停住。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(firstNovel.Id, "搜索反馈参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, anchor.AnchorId);

        var baseline = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                firstNovel.Id,
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
        Assert.Equal(2L, baseline.Total);
        var boostedTarget = Assert.Single(baseline.Items, item => item.Text == "雨声压低街道，主角站在门口。");
        Assert.DoesNotContain("accepted_feedback", boostedTarget.ScoreComponents?.Keys ?? []);

        await service.RecordUserFeedbackAsync(
            new RecordReferenceUserFeedbackPayload(
                firstNovel.Id,
                ReferenceFeedbackTargetTypes.Material,
                boostedTarget.MaterialId,
                ReferenceFeedbackDecisions.Accepted,
                boostedTarget.MaterialId,
                CandidateId: "",
                BlueprintId: 0,
                BeatId: "",
                FeedbackTags: ["useful_reference"],
                Note: "prefer this scene pressure material",
                EditedText: "",
                Origin: "user"),
            CancellationToken.None);

        var boosted = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                firstNovel.Id,
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

        Assert.Equal(boostedTarget.MaterialId, boosted.Items[0].MaterialId);
        var boostedComponents = boosted.Items[0].ScoreComponents ?? throw new InvalidOperationException("Expected score components.");
        Assert.True(boostedComponents["accepted_feedback"] > 0);

        var isolated = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                otherNovel.Id,
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
        var isolatedTarget = Assert.Single(isolated.Items, item => item.MaterialId == boostedTarget.MaterialId);
        Assert.DoesNotContain("accepted_feedback", isolatedTarget.ScoreComponents?.Keys ?? []);
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
    public async Task UpdateMaterialsTagsBulkMarksSelectedMaterialsAsUserVerified()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("批量标签校正测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "bulk-tags.md",
            """
            他在门口停了很久。

            雨声压低了整条街的呼吸。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "批量标签参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var selectedMaterialIds = materials.Items.Take(2).Select(material => material.MaterialId).ToArray();
        Assert.Equal(2, selectedMaterialIds.Length);

        var updated = await service.UpdateMaterialsTagsAsync(
            new UpdateReferenceMaterialsTagsPayload(
                novel.Id,
                selectedMaterialIds,
                FunctionTag: "environment",
                EmotionTag: "contained_tension",
                SceneTag: "rain_threshold",
                PovTag: "limited_close",
                TechniqueTag: "sensory_detail",
                Origin: "user",
                Note: "current page bulk correction"),
            CancellationToken.None);

        Assert.Equal(selectedMaterialIds, updated.Select(material => material.MaterialId).ToArray());
        Assert.All(updated, material =>
        {
            Assert.Equal("environment", material.FunctionTag);
            Assert.Equal("contained_tension", material.EmotionTag);
            Assert.Equal("rain_threshold", material.SceneTag);
            Assert.Equal("limited_close", material.PovTag);
            Assert.Equal("sensory_detail", material.TechniqueTag);
            Assert.True(material.UserVerified);
        });

        var corrected = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                EmotionTags: ["contained_tension"],
                FunctionTags: ["environment"],
                PovTags: ["limited_close"],
                TechniqueTags: ["sensory_detail"],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Equal(selectedMaterialIds.Order(StringComparer.Ordinal), corrected.Items.Select(material => material.MaterialId).Order(StringComparer.Ordinal));
        Assert.All(corrected.Items, material => Assert.True(material.UserVerified));
    }

    [Fact]
    public async Task UpdateMaterialsTagsBulkRollsBackWhenAnyMaterialIsNotAccessible()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量标签目标", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量标签其他小说", "", ""), CancellationToken.None);
        var targetPath = CreateSourceFile("bulk-tags-target.md", "目标小说材料。");
        var privatePath = CreateSourceFile("bulk-tags-private.md", "其他小说私有材料。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var targetAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(targetNovel.Id, "目标参考", null, targetPath, "markdown", "user_provided"),
            CancellationToken.None);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(otherNovel.Id, "其他私有参考", null, privatePath, "markdown", "user_provided"),
            CancellationToken.None);
        var targetMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                [targetAnchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None)).Items);
        var privateMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                otherNovel.Id,
                [privateAnchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None)).Items);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateMaterialsTagsAsync(
                new UpdateReferenceMaterialsTagsPayload(
                    targetNovel.Id,
                    [targetMaterial.MaterialId, privateMaterial.MaterialId],
                    FunctionTag: "interiority",
                    EmotionTag: "unease",
                    SceneTag: null,
                    PovTag: null,
                    TechniqueTag: null,
                    Origin: "user",
                    Note: "must roll back"),
                CancellationToken.None));

        var unchanged = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                [targetAnchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None)).Items);
        Assert.Equal(targetMaterial.FunctionTag, unchanged.FunctionTag);
        Assert.Equal(targetMaterial.EmotionTag, unchanged.EmotionTag);
        Assert.False(unchanged.UserVerified);
    }

    [Fact]
    public async Task MaterialTagReviewQueueIsServerOwnedPagedAndUpdatesAfterCorrection()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("服务端标签队列测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "tag-review-queue.md",
            """
            他在门口停了很久。

            雨声压低了整条街的呼吸。

            她没有回头，只把钥匙放回桌面。

            杯底半圈水痕慢慢散开。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "服务端标签队列参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = (await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                20),
            CancellationToken.None)).Items;
        Assert.True(materials.Count >= 4);

        var firstPage = await service.GetMaterialTagReviewQueueAsync(
            new GetReferenceMaterialTagReviewQueuePayload(
                novel.Id,
                [anchor.AnchorId],
                Page: 1,
                Size: 2),
            CancellationToken.None);
        var secondPage = await service.GetMaterialTagReviewQueueAsync(
            new GetReferenceMaterialTagReviewQueuePayload(
                novel.Id,
                [anchor.AnchorId],
                Page: 2,
                Size: 2),
            CancellationToken.None);

        Assert.True(firstPage.Total >= 4);
        Assert.Equal(1, firstPage.Page);
        Assert.Equal(2, firstPage.Size);
        Assert.Equal((int)Math.Ceiling(firstPage.Total / 2.0), firstPage.TotalPages);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Empty(firstPage.Items.Select(item => item.Material.MaterialId).Intersect(secondPage.Items.Select(item => item.Material.MaterialId)));
        Assert.All(firstPage.Items.Concat(secondPage.Items), item =>
        {
            Assert.NotEmpty(item.Issues);
            Assert.Contains(item.Issues, issue => issue.Code == ReferenceMaterialTagReviewIssueCodes.Unverified);
            Assert.True(item.Material.TextPreview.Length <= 163);
            Assert.True(item.Material.TextTruncated || item.Material.TextPreview.Length <= 160);
        });

        var correctedFromQueue = firstPage.Items[0].Material.MaterialId;
        await service.UpdateMaterialsTagsAsync(
            new UpdateReferenceMaterialsTagsPayload(
                novel.Id,
                [correctedFromQueue],
                FunctionTag: "interiority",
                EmotionTag: "unease",
                SceneTag: "room",
                PovTag: "close",
                TechniqueTag: "sensory_detail",
                Origin: "test",
                Note: "queue correction removes material from server-owned queue"),
            CancellationToken.None);

        var afterCorrection = await service.GetMaterialTagReviewQueueAsync(
            new GetReferenceMaterialTagReviewQueuePayload(
                novel.Id,
                [anchor.AnchorId],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Equal(firstPage.Total - 1, afterCorrection.Total);
        Assert.DoesNotContain(afterCorrection.Items, item => item.Material.MaterialId == correctedFromQueue);
    }

    [Fact]
    public async Task MaterialTagReviewQueueKeepsVerifiedMaterialsWithRemainingLowConfidence()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("已校正低置信队列测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "verified-low-confidence-review.md",
            """
            雨声压低了街面。

            林岚把钥匙放回桌面。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "已校正低置信参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = (await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                20),
            CancellationToken.None)).Items;
        var targetMaterialId = Assert.Single(materials.Select(material => material.MaterialId).Take(1));

        var databasePath = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        await using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using (var verifyAll = connection.CreateCommand())
            {
                verifyAll.CommandText = """
                    UPDATE reference_materials
                    SET function_tag = 'interiority',
                        emotion_tag = 'restrained',
                        scene_tag = 'room',
                        pov_tag = 'close',
                        technique_tag = 'sensory_detail',
                        function_confidence = 1.0,
                        emotion_confidence = 1.0,
                        pov_confidence = 1.0,
                        user_verified = 1
                    WHERE anchor_id = $anchor_id;
                    """;
                verifyAll.Parameters.AddWithValue("$anchor_id", anchor.AnchorId);
                Assert.True(await verifyAll.ExecuteNonQueryAsync() > 0);
            }

            await using var lowerOneDimension = connection.CreateCommand();
            lowerOneDimension.CommandText = """
                UPDATE reference_materials
                SET emotion_confidence = 0.5
                WHERE material_id = $material_id;
                """;
            lowerOneDimension.Parameters.AddWithValue("$material_id", targetMaterialId);
            Assert.Equal(1, await lowerOneDimension.ExecuteNonQueryAsync());
        }

        var queue = await service.GetMaterialTagReviewQueueAsync(
            new GetReferenceMaterialTagReviewQueuePayload(
                novel.Id,
                [anchor.AnchorId],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        var item = Assert.Single(queue.Items);
        Assert.Equal(targetMaterialId, item.Material.MaterialId);
        Assert.True(item.Material.UserVerified);
        Assert.Contains(item.Issues, issue => issue.Code == ReferenceMaterialTagReviewIssueCodes.LowConfidence);
        Assert.DoesNotContain(item.Issues, issue => issue.Code == ReferenceMaterialTagReviewIssueCodes.Unverified);
        Assert.DoesNotContain(item.Issues, issue => issue.Code == ReferenceMaterialTagReviewIssueCodes.UnknownTag);
    }

    [Fact]
    public async Task MaterialTagReviewQueueUsesBoundedPreviewForUnknownLicenseSources()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("未知授权标签队列测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "tag-review-unknown-license.md",
            "雨声压低了整条街的呼吸，林岚在门口停住，先把杯底水痕记下，然后把更长的环境细节留在素材库内部。墙根泥点、杯沿缺口、门后停顿和窗台潮气继续延展，确保未知授权预览必须截断。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "未知授权队列参考", null, sourcePath, "markdown", "unknown"),
            CancellationToken.None);

        var queue = await service.GetMaterialTagReviewQueueAsync(
            new GetReferenceMaterialTagReviewQueuePayload(
                novel.Id,
                [anchor.AnchorId],
                Page: 1,
                Size: 10),
            CancellationToken.None);

        Assert.NotEmpty(queue.Items);
        Assert.All(queue.Items, item =>
        {
            Assert.True(item.Material.TextPreview.Length <= 51);
        });
    }

    [Fact]
    public async Task MaterialTagReviewQueueRejectsExcessiveExplicitAnchorIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("标签队列过滤上限测试", "", ""), CancellationToken.None);
        var service = new SqliteReferenceAnchorService(options, novels);
        var tooManyAnchorIds = Enumerable.Range(1, 257).Select(id => (long)id).ToArray();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.GetMaterialTagReviewQueueAsync(
            new GetReferenceMaterialTagReviewQueuePayload(
                novel.Id,
                tooManyAnchorIds,
                Page: 1,
                Size: 10),
            CancellationToken.None).AsTask());

        Assert.Contains("At most 256 anchor ids", exception.Message, StringComparison.Ordinal);
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
    public async Task RebuildAnchorPreservesArchivedUserVerifiedTagsWhenMaterialHashIsUnchanged()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("归档校正重建保留", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "archived-verified-rebuild.md",
            """
            雨声压住门外的街。

            林岚握住钥匙，没有立刻说话。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "归档校正参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "钥匙",
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
                FunctionTag: "object_focus",
                EmotionTag: "restraint",
                SceneTag: "doorway",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                Origin: "user",
                Note: "先校正再归档"),
            CancellationToken.None);
        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [material.MaterialId]),
            CancellationToken.None);

        await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, material.MaterialId));
        var rows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var rebuilt = Assert.Single(rows, row => row.MaterialId == material.MaterialId);
        Assert.True(rebuilt.UserVerified);
        Assert.Equal("object_focus", rebuilt.FunctionTag);
        Assert.Equal("restraint", rebuilt.EmotionTag);
        Assert.Equal("close", rebuilt.PovTag);
        Assert.Equal("afterbeat", rebuilt.TechniqueTag);
    }

    [Fact]
    public async Task RebuildAnchorDoesNotArchiveEveryDuplicateHashMaterial()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("重复文本归档回套", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "duplicate-hash-rebuild.md",
            """
            他在门口停住。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "重复文本参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materials = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "停住",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        var material = Assert.Single(materials.Items);
        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [material.MaterialId]),
            CancellationToken.None);
        File.WriteAllText(
            sourcePath,
            """
            他在门口停住。

            他在门口停住。
            """);

        await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        var rows = (await ReadMaterialRowsAsync(options, anchor.AnchorId))
            .Where(row => row.MaterialType == ReferenceMaterialTypes.Sentence && row.Text == "他在门口停住。")
            .ToArray();
        Assert.Equal(2, rows.Length);
        var archivedCount = 0;
        foreach (var row in rows)
        {
            if (await ReadMaterialArchivedAtAsync(options, row.MaterialId) is not null)
            {
                archivedCount += 1;
            }
        }

        Assert.Equal(1, archivedCount);
        var active = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "停住",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.Single(active.Items);
    }

    [Fact]
    public async Task RebuildAnchorPreservesReuseProvenanceWhenMaterialIdIsUnchanged()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("重建保留候选审计", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("rebuild-provenance.md", "他握住{{object}}，没有立刻说话。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "候选审计参考", null, sourcePath, "markdown", "user_provided"),
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
        var before = await ReadReuseProvenanceAsync(options, adapted.CandidateId);

        await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        var after = await ReadReuseProvenanceAsync(options, adapted.CandidateId);
        Assert.Equal(before.CandidateMaterialId, after.CandidateMaterialId);
        Assert.Equal(before.AuditRows.Select(row => row.AuditId), after.AuditRows.Select(row => row.AuditId));
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
    public async Task AuditCandidateFailsNearCopyEvenWhenRewriteLevelIsAllowed()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("近复制审计测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("near-copy.md", "雨声压低了整条街的呼吸，林岚在门口停住，指节慢慢发紧。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "近复制参考", null, sourcePath, "text", "user_provided"),
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
                "雨声压低了整条街的呼吸，林岚在门口停住，指节慢慢发紧，然后他把钥匙放下。",
                ReferenceRewriteLevels.L3,
                SceneFacts: ["钥匙"]),
            CancellationToken.None);

        Assert.NotEqual(ReferenceRewriteLevels.L4, audit.RewriteLevel);
        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("source-leak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("n-gram", StringComparison.OrdinalIgnoreCase) || item.Contains("source-span", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuditCandidateFailsHighCandidateSourceSimilarityWithoutLeakingSourceText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("整体相似度审计测试", "", ""), CancellationToken.None);
        const string sourceText = "雨声压低了整条街的呼吸，林岚在门口停住，指尖慢慢发紧，仍把钥匙压回掌心，灯影从窗边退开，杯沿留着一圈冷水。";
        const string copiedShape = "雨声压低了整条街的呼吸，林岚在门口停住，指尖慢慢发紧";
        const string candidateText = "雨声放低了整片街的呼吸，林岚于门前停住，指尖缓缓发紧，仍将钥匙压回掌中，灯影从窗侧退开，杯沿留下一圈凉水。";
        var sourcePath = CreateSourceFile("candidate-source-similarity.md", sourceText);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "整体相似度参考", null, sourcePath, "text", "user_provided"),
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
                candidateText,
                ReferenceRewriteLevels.L3,
                SceneFacts: ["钥匙"]),
            CancellationToken.None);

        Assert.Equal("failed", audit.Status);
        Assert.Contains(audit.RequiredFixes, item => item.Contains("source-leak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.RequiredFixes, item => item.Contains("candidate/source similarity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(audit.RequiredFixes, item => item.Contains(copiedShape, StringComparison.Ordinal));
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
    public async Task DeleteWorkspaceCorpusAnchorArchivesWithoutDeletingMaterialProvenance()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("归档来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("归档使用方", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("workspace-archive.md", "雨声压住门外的街，林岚握住钥匙，没有立刻说话。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                ownerNovel.Id,
                "待归档共享参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);
        var beforeMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var beforeSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.NotEmpty(beforeMaterials);
        Assert.NotEmpty(beforeSegments);

        await service.DeleteAnchorAsync(consumingNovel.Id, anchor.AnchorId, CancellationToken.None);

        Assert.DoesNotContain(
            await service.GetAnchorsAsync(consumingNovel.Id, CancellationToken.None),
            item => item.AnchorId == anchor.AnchorId);
        var search = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "雨声 钥匙",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Empty(search.Items);
        Assert.Null(await service.GetBuildStatusAsync(consumingNovel.Id, anchor.AnchorId, CancellationToken.None));

        var afterMaterials = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var afterSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeMaterials.Select(item => item.MaterialId), afterMaterials.Select(item => item.MaterialId));
        Assert.Equal(beforeSegments.Select(item => item.SegmentId), afterSegments.Select(item => item.SegmentId));
        Assert.Equal(ReferenceCorpusVisibilities.Restricted, await ReadAnchorVisibilityAsync(options, anchor.AnchorId));
    }

    [Fact]
    public async Task DeleteAnchorsBulkDeletesPrivateAnchorsAndArchivesWorkspaceCorpusRows()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量归档来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量归档使用方", "", ""), CancellationToken.None);
        var privateSourcePath = CreateSourceFile("bulk-delete-private.md", "私有参考只属于当前小说。");
        var workspaceSourcePath = CreateSourceFile("bulk-delete-workspace.md", "共享雨声压住门外的街，钥匙没有立刻转动。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(ownerNovel.Id, "私有待删除参考", null, privateSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var workspaceAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                ownerNovel.Id,
                "共享待归档参考",
                null,
                workspaceSourcePath,
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);
        var workspaceMaterialsBefore = await ReadMaterialRowsAsync(options, workspaceAnchor.AnchorId);
        var workspaceSegmentsBefore = await ReadSourceSegmentsAsync(options, workspaceAnchor.AnchorId);
        Assert.NotEmpty(workspaceMaterialsBefore);
        Assert.NotEmpty(workspaceSegmentsBefore);

        await service.DeleteAnchorsAsync(
            new DeleteReferenceAnchorsPayload(ownerNovel.Id, [privateAnchor.AnchorId, workspaceAnchor.AnchorId]),
            CancellationToken.None);

        Assert.DoesNotContain(
            await service.GetAnchorsAsync(ownerNovel.Id, CancellationToken.None),
            item => item.AnchorId == privateAnchor.AnchorId);
        Assert.Null(await service.GetBuildStatusAsync(ownerNovel.Id, privateAnchor.AnchorId, CancellationToken.None));
        Assert.Empty(await ReadMaterialRowsAsync(options, privateAnchor.AnchorId));
        Assert.DoesNotContain(
            await service.GetAnchorsAsync(consumingNovel.Id, CancellationToken.None),
            item => item.AnchorId == workspaceAnchor.AnchorId);
        Assert.Null(await service.GetBuildStatusAsync(consumingNovel.Id, workspaceAnchor.AnchorId, CancellationToken.None));
        Assert.Equal(ReferenceCorpusVisibilities.Restricted, await ReadAnchorVisibilityAsync(options, workspaceAnchor.AnchorId));
        Assert.Equal(workspaceMaterialsBefore.Select(item => item.MaterialId), (await ReadMaterialRowsAsync(options, workspaceAnchor.AnchorId)).Select(item => item.MaterialId));
        Assert.Equal(workspaceSegmentsBefore.Select(item => item.SegmentId), (await ReadSourceSegmentsAsync(options, workspaceAnchor.AnchorId)).Select(item => item.SegmentId));
    }

    [Fact]
    public async Task DeleteAnchorsBulkRollsBackWhenAnyAnchorCannotBeProcessed()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("批量回滚来源", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile("bulk-delete-rollback.md", "共享语料在失败回滚后仍应可见。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var workspaceAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                ownerNovel.Id,
                "回滚共享参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.DeleteAnchorsAsync(
                new DeleteReferenceAnchorsPayload(ownerNovel.Id, [workspaceAnchor.AnchorId, 999_999]),
                CancellationToken.None));

        Assert.Contains(
            await service.GetAnchorsAsync(ownerNovel.Id, CancellationToken.None),
            item => item.AnchorId == workspaceAnchor.AnchorId);
        Assert.NotNull(await service.GetBuildStatusAsync(ownerNovel.Id, workspaceAnchor.AnchorId, CancellationToken.None));
        Assert.Equal(ReferenceCorpusVisibilities.Workspace, await ReadAnchorVisibilityAsync(options, workspaceAnchor.AnchorId));
    }

    [Fact]
    public async Task DeleteMaterialsArchivesSelectedMaterialsWithoutDeletingProvenance()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料归档来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料归档使用方", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "material-archive.md",
            """
            雨声压住门外的街。

            林岚握住钥匙，没有立刻说话。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                ownerNovel.Id,
                "共享材料待归档参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);
        var beforeRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var beforeSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.True(beforeRows.Count >= 2);
        var beforeSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.True(beforeSearch.Items.Count >= 2);
        var archivedMaterialId = beforeSearch.Items[0].MaterialId;

        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(consumingNovel.Id, [archivedMaterialId]),
            CancellationToken.None);

        var search = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.DoesNotContain(search.Items, material => material.MaterialId == archivedMaterialId);
        Assert.Contains(search.Items, material => material.MaterialId != archivedMaterialId);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.UpdateMaterialTagsAsync(
                new UpdateReferenceMaterialTagsPayload(
                    consumingNovel.Id,
                    archivedMaterialId,
                    FunctionTag: "environment",
                    EmotionTag: null,
                    SceneTag: null,
                    PovTag: null,
                    TechniqueTag: null,
                    Origin: "user",
                    Note: "should not update archived material"),
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.AdaptMaterialAsync(
                new AdaptReferenceMaterialPayload(
                    consumingNovel.Id,
                    archivedMaterialId,
                    SlotValues: [],
                    MaxRewriteLevel: ReferenceRewriteLevels.L2,
                    SceneFacts: []),
                CancellationToken.None));

        var afterRows = await ReadMaterialRowsAsync(options, anchor.AnchorId);
        var afterSegments = await ReadSourceSegmentsAsync(options, anchor.AnchorId);
        Assert.Equal(beforeRows.Select(item => item.MaterialId), afterRows.Select(item => item.MaterialId));
        Assert.Equal(beforeSegments.Select(item => item.SegmentId), afterSegments.Select(item => item.SegmentId));
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
        foreach (var row in afterRows.Where(item => item.MaterialId != archivedMaterialId))
        {
            Assert.Null(await ReadMaterialArchivedAtAsync(options, row.MaterialId));
        }

        await service.RebuildAnchorAsync(ownerNovel.Id, anchor.AnchorId, CancellationToken.None);
        var rebuiltSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.DoesNotContain(rebuiltSearch.Items, material => material.MaterialId == archivedMaterialId);
        Assert.Equal(afterRows.Select(item => item.MaterialId), (await ReadMaterialRowsAsync(options, anchor.AnchorId)).Select(item => item.MaterialId));
        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, archivedMaterialId));
    }

    [Fact]
    public async Task DeleteMaterialsRollsBackWhenAnyMaterialIsNotAccessible()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料归档目标", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料归档其他", "", ""), CancellationToken.None);
        var targetPath = CreateSourceFile("material-archive-target.md", "目标共享材料。");
        var privatePath = CreateSourceFile("material-archive-private.md", "其他小说私有材料。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var targetAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                targetNovel.Id,
                "目标共享参考",
                null,
                targetPath,
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(otherNovel.Id, "其他私有参考", null, privatePath, "markdown", "user_provided"),
            CancellationToken.None);
        var targetMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                [targetAnchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None)).Items);
        var privateMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                otherNovel.Id,
                [privateAnchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None)).Items);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.DeleteMaterialsAsync(
                new DeleteReferenceMaterialsPayload(targetNovel.Id, [targetMaterial.MaterialId, privateMaterial.MaterialId]),
                CancellationToken.None));

        Assert.Null(await ReadMaterialArchivedAtAsync(options, targetMaterial.MaterialId));
        Assert.Contains(
            (await service.SearchMaterialsAsync(
                new SearchReferenceMaterialsPayload(
                    targetNovel.Id,
                    [targetAnchor.AnchorId],
                    "",
                    [ReferenceMaterialTypes.Sentence],
                    [],
                    [],
                    [],
                    [],
                    1,
                    10),
                CancellationToken.None)).Items,
            material => material.MaterialId == targetMaterial.MaterialId);
    }

    [Fact]
    public async Task RestoreMaterialsMakesArchivedMaterialsSearchableAgain()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var ownerNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料恢复来源", "", ""), CancellationToken.None);
        var consumingNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料恢复使用方", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "material-restore.md",
            """
            雨声压住门外的街。

            林岚把杯子推远，没有立刻说话。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                ownerNovel.Id,
                "共享材料待恢复参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);
        var beforeSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "杯子",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        var materialId = Assert.Single(beforeSearch.Items).MaterialId;

        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(consumingNovel.Id, [materialId]),
            CancellationToken.None);

        var defaultSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "杯子",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Empty(defaultSearch.Items);

        var archivedSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "杯子",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10,
                ArchiveFilter: ReferenceMaterialArchiveFilters.Archived),
            CancellationToken.None);
        Assert.Contains(archivedSearch.Items, material => material.MaterialId == materialId);

        await service.RestoreMaterialsAsync(
            new RestoreReferenceMaterialsPayload(consumingNovel.Id, [materialId]),
            CancellationToken.None);

        Assert.Null(await ReadMaterialArchivedAtAsync(options, materialId));
        var restoredSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                consumingNovel.Id,
                AnchorIds: [anchor.AnchorId],
                Query: "杯子",
                MaterialTypes: [ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: [],
                PovTags: [],
                TechniqueTags: [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        Assert.Contains(restoredSearch.Items, material => material.MaterialId == materialId);

        var adapted = await service.AdaptMaterialAsync(
            new AdaptReferenceMaterialPayload(
                consumingNovel.Id,
                materialId,
                SlotValues: [],
                MaxRewriteLevel: ReferenceRewriteLevels.L2,
                SceneFacts: []),
            CancellationToken.None);
        Assert.Equal(materialId, adapted.MaterialId);
    }

    [Fact]
    public async Task RestoreMaterialsRollsBackWhenAnyMaterialIsNotAccessible()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var targetNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料恢复目标", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("材料恢复其他", "", ""), CancellationToken.None);
        var targetPath = CreateSourceFile("material-restore-target.md", "目标共享材料。");
        var privatePath = CreateSourceFile("material-restore-private.md", "其他小说私有材料。");
        var service = new SqliteReferenceAnchorService(options, novels);
        var targetAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                targetNovel.Id,
                "目标共享恢复参考",
                null,
                targetPath,
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace),
            CancellationToken.None);
        var privateAnchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(otherNovel.Id, "其他私有恢复参考", null, privatePath, "markdown", "user_provided"),
            CancellationToken.None);
        var targetMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                [targetAnchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None)).Items);
        var privateMaterial = Assert.Single((await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                otherNovel.Id,
                [privateAnchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None)).Items);
        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(targetNovel.Id, [targetMaterial.MaterialId]),
            CancellationToken.None);
        await service.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(otherNovel.Id, [privateMaterial.MaterialId]),
            CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await service.RestoreMaterialsAsync(
                new RestoreReferenceMaterialsPayload(targetNovel.Id, [targetMaterial.MaterialId, privateMaterial.MaterialId]),
                CancellationToken.None));

        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, targetMaterial.MaterialId));
        var targetDefaultSearch = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                targetNovel.Id,
                [targetAnchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                1,
                10),
            CancellationToken.None);
        Assert.DoesNotContain(targetDefaultSearch.Items, material => material.MaterialId == targetMaterial.MaterialId);
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
                   m.emotion_confidence, m.pov_confidence, m.text, s.segment_id, COUNT(sl.slot_id),
                   m.source_hash, m.user_verified
            FROM reference_materials m
            INNER JOIN reference_source_segments s ON s.segment_id = m.source_segment_id
            LEFT JOIN reference_material_slots sl ON sl.material_id = m.material_id
            WHERE m.anchor_id = $anchor_id
            GROUP BY m.material_id, m.source_segment_id, m.material_type, m.function_tag,
                     m.emotion_tag, m.pov_tag, m.technique_tag, m.function_confidence,
                     m.emotion_confidence, m.pov_confidence, m.text, s.segment_id,
                     m.source_hash, m.user_verified
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
                reader.GetInt64(12),
                reader.GetString(13),
                reader.GetInt64(14) != 0));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<ReferenceProcessingAttemptRow>> ReadProcessingAttemptRowsAsync(
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
            SELECT attempt_id, build_id, attempt_number, build_version, status, stage,
                   event_count, recovered_from_attempt_id, recovered_from_build_id, blocked_reason
            FROM reference_anchor_processing_attempts
            WHERE anchor_id = $anchor_id
            ORDER BY attempt_number ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<ReferenceProcessingAttemptRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ReferenceProcessingAttemptRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<ReferenceProcessingEventAttemptRefRow>> ReadProcessingEventAttemptRefsAsync(
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
            SELECT event_id, attempt_id, build_id, attempt_number
            FROM reference_anchor_processing_events
            WHERE anchor_id = $anchor_id
            ORDER BY created_at ASC, event_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<ReferenceProcessingEventAttemptRefRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new ReferenceProcessingEventAttemptRefRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3)));
        }

        return rows;
    }

    private static async Task InsertRawDuplicateAnchorIdentityAsync(
        AppInitializationOptions options,
        long duplicateAnchorId,
        ReferenceAnchorPayload source,
        long? storedNovelId)
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
            INSERT INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at,
               corpus_visibility, source_trust, user_tags_json)
            VALUES
              ($anchor_id, $novel_id, $title, $author, $source_path, $source_kind, $license_status,
               $source_file_hash, $build_version, $status, $created_at, $updated_at,
               $corpus_visibility, $source_trust, $user_tags_json);
            """;
        command.Parameters.AddWithValue("$anchor_id", duplicateAnchorId);
        command.Parameters.AddWithValue("$novel_id", storedNovelId.HasValue ? storedNovelId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$title", "直接插入重复来源");
        command.Parameters.AddWithValue("$author", string.Empty);
        command.Parameters.AddWithValue("$source_path", source.SourcePath);
        command.Parameters.AddWithValue("$source_kind", source.SourceKind);
        command.Parameters.AddWithValue("$license_status", source.LicenseStatus);
        command.Parameters.AddWithValue("$source_file_hash", source.SourceFileHash);
        command.Parameters.AddWithValue("$build_version", source.BuildVersion);
        command.Parameters.AddWithValue("$status", ReferenceAnchorBuildStates.Ready);
        command.Parameters.AddWithValue("$created_at", source.CreatedAt.AddSeconds(1).ToString("O"));
        command.Parameters.AddWithValue("$updated_at", source.UpdatedAt.AddSeconds(1).ToString("O"));
        command.Parameters.AddWithValue("$corpus_visibility", source.Visibility);
        command.Parameters.AddWithValue("$source_trust", source.SourceTrust);
        command.Parameters.AddWithValue("$user_tags_json", "[]");
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask CreateLegacyDuplicateImportIdentityAnchorsAsync(
        AppInitializationOptions options,
        long novelId,
        string sourcePath)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE reference_anchors (
                  anchor_id INTEGER PRIMARY KEY,
                  novel_id INTEGER,
                  title TEXT NOT NULL,
                  author TEXT NOT NULL,
                  source_path TEXT NOT NULL,
                  source_kind TEXT NOT NULL,
                  license_status TEXT NOT NULL,
                  source_file_hash TEXT NOT NULL,
                  build_version TEXT NOT NULL,
                  status TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  corpus_visibility TEXT NOT NULL DEFAULT 'private',
                  source_trust TEXT NOT NULL DEFAULT 'user_verified',
                  user_tags_json TEXT NOT NULL DEFAULT '[]'
                );
                """;
            await schema.ExecuteNonQueryAsync();
        }

        const string timestamp = "2026-07-04T00:00:00.0000000+00:00";
        for (var index = 0; index < 2; index++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO reference_anchors
                  (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at,
                   corpus_visibility, source_trust, user_tags_json)
                VALUES
                  ($anchor_id, $novel_id, $title, '', $source_path, 'markdown', 'user_provided',
                   'legacy-source-hash', 'reference-anchor-v1', 'ready', $created_at, $updated_at,
                   'private', 'user_verified', '[]');
                """;
            insert.Parameters.AddWithValue("$anchor_id", 910_001 + index);
            insert.Parameters.AddWithValue("$novel_id", novelId);
            insert.Parameters.AddWithValue("$title", index == 0 ? "历史重复参考" : "历史重复参考副本");
            insert.Parameters.AddWithValue("$source_path", sourcePath);
            insert.Parameters.AddWithValue("$created_at", timestamp);
            insert.Parameters.AddWithValue("$updated_at", timestamp);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async ValueTask CreateLegacyProcessingEventsWithoutAttemptMetadataAsync(
        AppInitializationOptions options,
        long novelId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE reference_anchors (
                  anchor_id INTEGER PRIMARY KEY,
                  novel_id INTEGER,
                  title TEXT NOT NULL,
                  author TEXT NOT NULL,
                  source_path TEXT NOT NULL,
                  source_kind TEXT NOT NULL,
                  license_status TEXT NOT NULL,
                  source_file_hash TEXT NOT NULL,
                  build_version TEXT NOT NULL,
                  status TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL,
                  corpus_visibility TEXT NOT NULL DEFAULT 'private',
                  source_trust TEXT NOT NULL DEFAULT 'user_verified',
                  user_tags_json TEXT NOT NULL DEFAULT '[]'
                );

                CREATE TABLE reference_anchor_build_state (
                  anchor_id INTEGER PRIMARY KEY,
                  status TEXT NOT NULL,
                  stage TEXT NOT NULL,
                  source_segment_count INTEGER NOT NULL,
                  material_count INTEGER NOT NULL,
                  slot_count INTEGER NOT NULL,
                  vector_count INTEGER NOT NULL,
                  last_error TEXT NOT NULL,
                  updated_at TEXT NOT NULL
                );

                CREATE TABLE reference_anchor_processing_events (
                  event_id TEXT PRIMARY KEY,
                  anchor_id INTEGER NOT NULL,
                  stage TEXT NOT NULL,
                  status TEXT NOT NULL,
                  source_segment_count INTEGER NOT NULL,
                  material_count INTEGER NOT NULL,
                  slot_count INTEGER NOT NULL,
                  vector_count INTEGER NOT NULL,
                  last_error TEXT NOT NULL,
                  affected_source_id TEXT NOT NULL,
                  affected_material_id TEXT NOT NULL,
                  affected_segment_id TEXT NOT NULL,
                  affected_slot_id TEXT NOT NULL,
                  created_at TEXT NOT NULL
                );
                """;
            await schema.ExecuteNonQueryAsync();
        }

        const string firstTimestamp = "2026-07-04T00:00:00.0000000+00:00";
        const string secondTimestamp = "2026-07-04T00:01:00.0000000+00:00";
        await using (var insertAnchor = connection.CreateCommand())
        {
            insertAnchor.CommandText = """
                INSERT INTO reference_anchors
                  (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at,
                   corpus_visibility, source_trust, user_tags_json)
                VALUES
                  (920001, $novel_id, '历史处理记录参考', '', 'legacy-processing.md', 'markdown',
                   'user_provided', 'legacy-processing-hash', 'reference-anchor-v1', 'ready',
                   $created_at, $updated_at, 'private', 'user_verified', '[]');
                """;
            insertAnchor.Parameters.AddWithValue("$novel_id", novelId);
            insertAnchor.Parameters.AddWithValue("$created_at", firstTimestamp);
            insertAnchor.Parameters.AddWithValue("$updated_at", secondTimestamp);
            await insertAnchor.ExecuteNonQueryAsync();
        }

        await using (var insertStatus = connection.CreateCommand())
        {
            insertStatus.CommandText = """
                INSERT INTO reference_anchor_build_state
                  (anchor_id, status, stage, source_segment_count, material_count, slot_count,
                   vector_count, last_error, updated_at)
                VALUES
                  (920001, 'ready', 'ready', 1, 1, 0, 1, '', $updated_at);
                """;
            insertStatus.Parameters.AddWithValue("$updated_at", secondTimestamp);
            await insertStatus.ExecuteNonQueryAsync();
        }

        await using (var firstEvent = connection.CreateCommand())
        {
            firstEvent.CommandText = """
                INSERT INTO reference_anchor_processing_events
                  (event_id, anchor_id, stage, status, source_segment_count, material_count,
                   slot_count, vector_count, last_error, affected_source_id, affected_material_id,
                   affected_segment_id, affected_slot_id, created_at)
                VALUES
                  ('legacy-event-failed', 920001, 'failed_embedding', 'failed_embedding',
                   1, 1, 0, 0, 'legacy first failure', '920001', '', '', '', $created_at);
                """;
            firstEvent.Parameters.AddWithValue("$created_at", firstTimestamp);
            await firstEvent.ExecuteNonQueryAsync();
        }

        await using (var secondEvent = connection.CreateCommand())
        {
            secondEvent.CommandText = """
                INSERT INTO reference_anchor_processing_events
                  (event_id, anchor_id, stage, status, source_segment_count, material_count,
                   slot_count, vector_count, last_error, affected_source_id, affected_material_id,
                   affected_segment_id, affected_slot_id, created_at)
                VALUES
                  ('legacy-event-ready', 920001, 'ready', 'ready',
                   1, 1, 0, 1, '', '920001', '', '', '', $created_at);
                """;
            secondEvent.Parameters.AddWithValue("$created_at", secondTimestamp);
            await secondEvent.ExecuteNonQueryAsync();
        }
    }

    private static async ValueTask UpdateBuildStateErrorAsync(
        AppInitializationOptions options,
        long anchorId,
        string lastError)
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
            UPDATE reference_anchor_build_state
            SET status = $status,
                stage = $stage,
                last_error = $last_error
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$status", ReferenceAnchorBuildStates.FailedImport);
        command.Parameters.AddWithValue("$stage", ReferenceAnchorBuildStates.FailedImport);
        command.Parameters.AddWithValue("$last_error", lastError);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask UpdateAnchorBuildStatusAsync(
        AppInitializationOptions options,
        long anchorId,
        string status,
        string stage)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        var updatedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using (var anchor = connection.CreateCommand())
        {
            anchor.Transaction = transaction;
            anchor.CommandText = """
                UPDATE reference_anchors
                SET status = $status,
                    updated_at = $updated_at
                WHERE anchor_id = $anchor_id;
                """;
            anchor.Parameters.AddWithValue("$status", status);
            anchor.Parameters.AddWithValue("$updated_at", updatedAt);
            anchor.Parameters.AddWithValue("$anchor_id", anchorId);
            await anchor.ExecuteNonQueryAsync();
        }

        await using (var build = connection.CreateCommand())
        {
            build.Transaction = transaction;
            build.CommandText = """
                UPDATE reference_anchor_build_state
                SET status = $status,
                    stage = $stage,
                    last_error = '',
                    updated_at = $updated_at
                WHERE anchor_id = $anchor_id;
                """;
            build.Parameters.AddWithValue("$status", status);
            build.Parameters.AddWithValue("$stage", stage);
            build.Parameters.AddWithValue("$updated_at", updatedAt);
            build.Parameters.AddWithValue("$anchor_id", anchorId);
            await build.ExecuteNonQueryAsync();
        }

        await using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO reference_anchor_processing_events
                  (event_id, anchor_id, stage, status, source_segment_count, material_count,
                   slot_count, vector_count, last_error, affected_source_id, affected_material_id,
                   affected_segment_id, affected_slot_id, created_at)
                SELECT $event_id, $anchor_id, $stage, $status,
                       COALESCE(s.source_segment_count, 0),
                       COALESCE(s.material_count, 0),
                       COALESCE(s.slot_count, 0),
                       COALESCE(s.vector_count, 0),
                       '', $affected_source_id, '', '', '', $updated_at
                FROM reference_anchor_build_state s
                WHERE s.anchor_id = $anchor_id;
                """;
            history.Parameters.AddWithValue("$event_id", Guid.NewGuid().ToString("N"));
            history.Parameters.AddWithValue("$anchor_id", anchorId);
            history.Parameters.AddWithValue("$stage", stage);
            history.Parameters.AddWithValue("$status", status);
            history.Parameters.AddWithValue("$affected_source_id", anchorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            history.Parameters.AddWithValue("$updated_at", updatedAt);
            await history.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask RemoveAnchorOutputAndSetBuildStatusAsync(
        AppInitializationOptions options,
        long anchorId,
        string status,
        string stage)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        var updatedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        await using (var slots = connection.CreateCommand())
        {
            slots.Transaction = transaction;
            slots.CommandText = """
                DELETE FROM reference_material_slots
                WHERE material_id IN (
                  SELECT material_id
                  FROM reference_materials
                  WHERE anchor_id = $anchor_id
                );
                """;
            slots.Parameters.AddWithValue("$anchor_id", anchorId);
            await slots.ExecuteNonQueryAsync();
        }

        await using (var materials = connection.CreateCommand())
        {
            materials.Transaction = transaction;
            materials.CommandText = """
                DELETE FROM reference_materials
                WHERE anchor_id = $anchor_id;
                """;
            materials.Parameters.AddWithValue("$anchor_id", anchorId);
            await materials.ExecuteNonQueryAsync();
        }

        await using (var segments = connection.CreateCommand())
        {
            segments.Transaction = transaction;
            segments.CommandText = """
                DELETE FROM reference_source_segments
                WHERE anchor_id = $anchor_id;
                """;
            segments.Parameters.AddWithValue("$anchor_id", anchorId);
            await segments.ExecuteNonQueryAsync();
        }

        await using (var anchor = connection.CreateCommand())
        {
            anchor.Transaction = transaction;
            anchor.CommandText = """
                UPDATE reference_anchors
                SET status = $status,
                    updated_at = $updated_at
                WHERE anchor_id = $anchor_id;
                """;
            anchor.Parameters.AddWithValue("$status", status);
            anchor.Parameters.AddWithValue("$updated_at", updatedAt);
            anchor.Parameters.AddWithValue("$anchor_id", anchorId);
            await anchor.ExecuteNonQueryAsync();
        }

        await using (var build = connection.CreateCommand())
        {
            build.Transaction = transaction;
            build.CommandText = """
                UPDATE reference_anchor_build_state
                SET status = $status,
                    stage = $stage,
                    source_segment_count = 0,
                    material_count = 0,
                    slot_count = 0,
                    vector_count = 0,
                    last_error = '',
                    updated_at = $updated_at
                WHERE anchor_id = $anchor_id;
                """;
            build.Parameters.AddWithValue("$status", status);
            build.Parameters.AddWithValue("$stage", stage);
            build.Parameters.AddWithValue("$updated_at", updatedAt);
            build.Parameters.AddWithValue("$anchor_id", anchorId);
            await build.ExecuteNonQueryAsync();
        }

        await using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO reference_anchor_processing_events
                  (event_id, anchor_id, stage, status, source_segment_count, material_count,
                   slot_count, vector_count, last_error, affected_source_id, affected_material_id,
                   affected_segment_id, affected_slot_id, created_at)
                VALUES
                  ($event_id, $anchor_id, $stage, $status, 0, 0, 0, 0, '',
                   $affected_source_id, '', '', '', $updated_at);
                """;
            history.Parameters.AddWithValue("$event_id", Guid.NewGuid().ToString("N"));
            history.Parameters.AddWithValue("$anchor_id", anchorId);
            history.Parameters.AddWithValue("$stage", stage);
            history.Parameters.AddWithValue("$status", status);
            history.Parameters.AddWithValue("$affected_source_id", anchorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            history.Parameters.AddWithValue("$updated_at", updatedAt);
            await history.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask<ReferenceReuseProvenanceRow> ReadReuseProvenanceAsync(
        AppInitializationOptions options,
        string candidateId)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();

        await using var candidateCommand = connection.CreateCommand();
        candidateCommand.CommandText = """
            SELECT material_id
            FROM reference_reuse_candidates
            WHERE candidate_id = $candidate_id;
            """;
        candidateCommand.Parameters.AddWithValue("$candidate_id", candidateId);
        var candidateMaterialId = Assert.IsType<string>(await candidateCommand.ExecuteScalarAsync());

        await using var auditCommand = connection.CreateCommand();
        auditCommand.CommandText = """
            SELECT audit_id, material_id, status
            FROM reference_reuse_audits
            WHERE candidate_id = $candidate_id
            ORDER BY audit_id ASC;
            """;
        auditCommand.Parameters.AddWithValue("$candidate_id", candidateId);
        var audits = new List<ReferenceReuseAuditRow>();
        await using var reader = await auditCommand.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            audits.Add(new ReferenceReuseAuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return new ReferenceReuseProvenanceRow(candidateMaterialId, audits);
    }

    private static async ValueTask<string> ReadAnchorVisibilityAsync(
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
            SELECT corpus_visibility
            FROM reference_anchors
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var visibility = await command.ExecuteScalarAsync();
        return Assert.IsType<string>(visibility);
    }

    private static async ValueTask<long?> ReadAnchorStoredNovelIdAsync(
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
            SELECT novel_id
            FROM reference_anchors
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var storedNovelId = await command.ExecuteScalarAsync();
        if (storedNovelId is null || storedNovelId == DBNull.Value)
        {
            return null;
        }

        return Assert.IsType<long>(storedNovelId);
    }

    private static async ValueTask<string?> ReadMaterialArchivedAtAsync(
        AppInitializationOptions options,
        string materialId)
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
            SELECT archived_at
            FROM reference_materials
            WHERE material_id = $material_id;
            """;
        command.Parameters.AddWithValue("$material_id", materialId);
        var archivedAt = await command.ExecuteScalarAsync();
        if (archivedAt is null || archivedAt == DBNull.Value)
        {
            return null;
        }

        return Assert.IsType<string>(archivedAt);
    }

    private static async ValueTask MarkAnchorAsWorkspaceCorpusAsync(
        AppInitializationOptions options,
        long anchorId,
        string visibility = ReferenceCorpusVisibilities.Workspace)
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
            SET novel_id = 0,
                corpus_visibility = $corpus_visibility
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$corpus_visibility", visibility);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, updated);
    }

    private static async ValueTask SetAnchorVisibilityOnlyAsync(
        AppInitializationOptions options,
        long anchorId,
        string visibility)
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
            SET corpus_visibility = $corpus_visibility
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$corpus_visibility", visibility);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, updated);
    }

    private static async ValueTask MarkAnchorAsNullableWorkspaceCorpusAsync(
        AppInitializationOptions options,
        long anchorId,
        string visibility = ReferenceCorpusVisibilities.Workspace)
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
            SET novel_id = NULL,
                corpus_visibility = $corpus_visibility
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$corpus_visibility", visibility);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, updated);
    }

    private static async ValueTask CreateLegacyWorkspaceCorpusAnchorAsync(AppInitializationOptions options)
    {
        var databasePath = Path.Combine(
            options.DefaultDataDirectory,
            "reference-anchor",
            "index.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE reference_anchors (
                  anchor_id INTEGER PRIMARY KEY,
                  novel_id INTEGER NOT NULL,
                  title TEXT NOT NULL,
                  author TEXT NOT NULL,
                  source_path TEXT NOT NULL,
                  source_kind TEXT NOT NULL,
                  license_status TEXT NOT NULL,
                  source_file_hash TEXT NOT NULL,
                  build_version TEXT NOT NULL,
                  status TEXT NOT NULL,
                  created_at TEXT NOT NULL,
                  updated_at TEXT NOT NULL
                );

                CREATE TABLE reference_anchor_build_state (
                  anchor_id INTEGER PRIMARY KEY,
                  status TEXT NOT NULL,
                  stage TEXT NOT NULL,
                  source_segment_count INTEGER NOT NULL,
                  material_count INTEGER NOT NULL,
                  slot_count INTEGER NOT NULL,
                  vector_count INTEGER NOT NULL,
                  last_error TEXT NOT NULL,
                  updated_at TEXT NOT NULL
                );

                CREATE TABLE reference_source_segments (
                  segment_id TEXT PRIMARY KEY,
                  anchor_id INTEGER NOT NULL,
                  chapter_index INTEGER NOT NULL,
                  chapter_title TEXT NOT NULL,
                  segment_type TEXT NOT NULL,
                  segment_index INTEGER NOT NULL,
                  parent_segment_id TEXT NOT NULL,
                  start_offset INTEGER NOT NULL,
                  end_offset INTEGER NOT NULL,
                  text TEXT NOT NULL,
                  text_hash TEXT NOT NULL
                );

                CREATE TABLE reference_materials (
                  material_id TEXT PRIMARY KEY,
                  anchor_id INTEGER NOT NULL,
                  source_segment_id TEXT NOT NULL,
                  material_type TEXT NOT NULL,
                  function_tag TEXT NOT NULL,
                  emotion_tag TEXT NOT NULL,
                  scene_tag TEXT NOT NULL,
                  pov_tag TEXT NOT NULL,
                  technique_tag TEXT NOT NULL,
                  function_confidence REAL NOT NULL,
                  emotion_confidence REAL NOT NULL,
                  pov_confidence REAL NOT NULL,
                  text TEXT NOT NULL,
                  source_hash TEXT NOT NULL,
                  extractor_version TEXT NOT NULL,
                  user_verified INTEGER NOT NULL,
                  created_at TEXT NOT NULL
                );
                """;
            await schema.ExecuteNonQueryAsync();
        }

        const string timestamp = "2026-07-04T00:00:00.0000000+00:00";
        await using (var insertAnchor = connection.CreateCommand())
        {
            insertAnchor.CommandText = """
                INSERT INTO reference_anchors
                  (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                   source_file_hash, build_version, status, created_at, updated_at)
                VALUES
                  (7001, 0, '旧工作区共享参考', '', 'legacy.md', 'markdown', 'user_provided',
                   'legacy-source-hash', 'reference-anchor-v1', 'ready', $created_at, $updated_at);
                """;
            insertAnchor.Parameters.AddWithValue("$created_at", timestamp);
            insertAnchor.Parameters.AddWithValue("$updated_at", timestamp);
            await insertAnchor.ExecuteNonQueryAsync();
        }

        await using (var insertStatus = connection.CreateCommand())
        {
            insertStatus.CommandText = """
                INSERT INTO reference_anchor_build_state
                  (anchor_id, status, stage, source_segment_count, material_count, slot_count,
                   vector_count, last_error, updated_at)
                VALUES
                  (7001, 'ready', 'ready', 1, 1, 0, 0, '', $updated_at);
                """;
            insertStatus.Parameters.AddWithValue("$updated_at", timestamp);
            await insertStatus.ExecuteNonQueryAsync();
        }

        await using (var insertSegment = connection.CreateCommand())
        {
            insertSegment.CommandText = """
                INSERT INTO reference_source_segments
                  (segment_id, anchor_id, chapter_index, chapter_title, segment_type,
                   segment_index, parent_segment_id, start_offset, end_offset, text, text_hash)
                VALUES
                  ('7001:0:sentence:0:legacy', 7001, 0, '', 'sentence',
                   0, '', 0, 15, '旧共享语料仍然可见。', 'legacy-segment-hash');
                """;
            await insertSegment.ExecuteNonQueryAsync();
        }

        await using (var insertMaterial = connection.CreateCommand())
        {
            insertMaterial.CommandText = """
                INSERT INTO reference_materials
                  (material_id, anchor_id, source_segment_id, material_type, function_tag,
                   emotion_tag, scene_tag, pov_tag, technique_tag, function_confidence,
                   emotion_confidence, pov_confidence, text, source_hash, extractor_version,
                   user_verified, created_at)
                VALUES
                  ('7001:material:sentence:0:legacy', 7001, '7001:0:sentence:0:legacy', 'sentence',
                   'interiority', 'restrained', 'workspace', 'close', 'afterbeat', 1.0,
                   1.0, 1.0, '旧共享语料仍然可见。', 'legacy-material-hash',
                   'legacy-extractor', 1, $created_at);
                """;
            insertMaterial.Parameters.AddWithValue("$created_at", timestamp);
            await insertMaterial.ExecuteNonQueryAsync();
        }
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
        long SlotCount,
        string SourceHash,
        bool UserVerified);

    private sealed record ReferenceProcessingAttemptRow(
        string AttemptId,
        string BuildId,
        int AttemptNumber,
        string BuildVersion,
        string Status,
        string Stage,
        int EventCount,
        string RecoveredFromAttemptId,
        string RecoveredFromBuildId,
        string BlockedReason);

    private sealed record ReferenceProcessingEventAttemptRefRow(
        string EventId,
        string AttemptId,
        string BuildId,
        int AttemptNumber);

    private sealed record ReferenceReuseProvenanceRow(
        string CandidateMaterialId,
        IReadOnlyList<ReferenceReuseAuditRow> AuditRows);

    private sealed record ReferenceReuseAuditRow(
        string AuditId,
        string MaterialId,
        string Status);

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

    private sealed class FailingReferenceMaterialSlotDetector : IReferenceMaterialSlotDetector
    {
        private readonly string _message;

        public FailingReferenceMaterialSlotDetector(string message)
        {
            _message = message;
        }

        public IReadOnlyList<ReferenceMaterialSlot> Detect(ReferenceMaterialPayload material)
        {
            throw new InvalidOperationException(_message);
        }
    }

    private sealed class FailingReferenceAnchorProcessingStageProbe : IReferenceAnchorProcessingStageProbe
    {
        private readonly string _stage;
        private readonly string _message;

        public FailingReferenceAnchorProcessingStageProbe(string stage, string message)
        {
            _stage = stage;
            _message = message;
        }

        public void BeforeStage(string stage, long anchorId, string sourceFileHash)
        {
            if (string.Equals(stage, _stage, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(_message);
            }
        }
    }

    private sealed class CancelingEmbeddingClient : IEmbeddingClient
    {
        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class RequestedCancellationEmbeddingClient(CancellationTokenSource cancellation) : IEmbeddingClient
    {
        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
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
