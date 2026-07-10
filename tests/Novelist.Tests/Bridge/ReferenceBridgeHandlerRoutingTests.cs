using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceBridgeHandlerRoutingTests
{
    private const string FullMaterialLeakSentinel = "__FULL_MATERIAL_SHOULD_NOT_LEAVE_SERVICE__";

    [Fact]
    public async Task ReferenceAnchorHandlersRouteEveryMethodToServiceOperations()
    {
        var service = new RecordingReferenceAnchorService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        await AssertOkAsync(dispatcher, "CreateReferenceAnchor", new CreateReferenceAnchorPayload(
            42,
            "Anchor",
            "Author",
            @"D:\reference.md",
            "markdown",
            "user_provided",
            Visibility: ReferenceCorpusVisibilities.Workspace,
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["rain", "threshold"]));
        await AssertOkAsync(dispatcher, "CreateReferenceAnchors", new CreateReferenceAnchorsPayload(
            [
                new CreateReferenceAnchorPayload(42, "Bulk Anchor One", null, @"D:\bulk-one.md", "markdown", "user_provided"),
                new CreateReferenceAnchorPayload(
                    42,
                    "Bulk Anchor Two",
                    "Author",
                    @"D:\bulk-two.md",
                    "markdown",
                    "user_provided",
                    Visibility: ReferenceCorpusVisibilities.Workspace)
            ]));
        await AssertOkAsync(dispatcher, "CreateReferenceAnchorsWithResult", new CreateReferenceAnchorsPayload(
            [
                new CreateReferenceAnchorPayload(42, "Bulk Result One", null, @"D:\bulk-result-one.md", "markdown", "user_provided"),
                new CreateReferenceAnchorPayload(42, "Bulk Result Two", null, @"D:\bulk-result-two.md", "markdown", "user_provided")
            ]));
        await AssertOkAsync(dispatcher, "GetReferenceAnchors", 42L);
        await AssertOkAsync(dispatcher, "DeleteReferenceAnchor", 42L, 99L);
        await AssertOkAsync(dispatcher, "DeleteReferenceAnchors", new DeleteReferenceAnchorsPayload(42, [100, 101]));
        await AssertOkAsync(dispatcher, "DeleteReferenceMaterials", new DeleteReferenceMaterialsPayload(42, ["material-4", "material-5"]));
        await AssertOkAsync(dispatcher, "RestoreReferenceMaterials", new RestoreReferenceMaterialsPayload(42, ["material-4", "material-5"]));
        await AssertOkAsync(dispatcher, "PromoteReferenceAnchorToWorkspaceCorpus", new PromoteReferenceAnchorToWorkspaceCorpusPayload(
            42,
            99,
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["migrated", "shared"]));
        await AssertOkAsync(dispatcher, "PromoteReferenceAnchorsToWorkspaceCorpus", new PromoteReferenceAnchorsToWorkspaceCorpusPayload(
            42,
            [100, 101],
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["bulk", "shared"]));
        await AssertOkAsync(dispatcher, "UpdateReferenceAnchorMetadata", new UpdateReferenceAnchorMetadataPayload(
            42,
            99,
            "Updated Anchor",
            "Updated Author",
            "licensed",
            ReferenceCorpusVisibilities.Workspace,
            ReferenceSourceTrustLevels.UserVerified,
            ["curated", "rain"]));
        await AssertOkAsync(dispatcher, "RebuildReferenceAnchor", 42L, 99L);
        await AssertOkAsync(dispatcher, "GetReferenceAnchorBuildStatus", 42L, 99L);
        await AssertOkAsync(dispatcher, "SearchReferenceMaterials", new SearchReferenceMaterialsPayload(
            42,
            [99],
            "fog",
            [ReferenceMaterialTypes.Passage],
            ["unease"],
            ["interiority"],
            ["close"],
            ["afterbeat"],
            2,
            10,
            ProseDuties: ["source_backed_detail"],
            ArchiveFilter: ReferenceMaterialArchiveFilters.Archived));
        await AssertOkAsync(dispatcher, "GetReferenceMaterialTagReviewQueue", new GetReferenceMaterialTagReviewQueuePayload(
            42,
            [99],
            3,
            25,
            ReferenceMaterialArchiveFilters.Active));
        await AssertOkAsync(dispatcher, "GetReferenceMaterialDetail", new GetReferenceMaterialDetailPayload(
            42,
            "material-1"));
        await AssertOkAsync(dispatcher, "GetReferenceSourceSegmentDetail", new GetReferenceSourceSegmentDetailPayload(
            42,
            99,
            "segment-1"));
        await AssertOkAsync(dispatcher, "GetReferenceSourceProcessingDetail", new GetReferenceSourceProcessingDetailPayload(
            42,
            99));
        await AssertOkAsync(dispatcher, "UpdateReferenceMaterialTags", new UpdateReferenceMaterialTagsPayload(
            42,
            "material-1",
            "interiority",
            "unease",
            "threshold",
            "close",
            "afterbeat",
            "user",
            "verified"));
        await AssertOkAsync(dispatcher, "UpdateReferenceMaterialsTags", new UpdateReferenceMaterialsTagsPayload(
            42,
            ["material-2", "material-3"],
            "object_subtext",
            "contained_tension",
            "rain_threshold",
            "limited_close",
            "delayed_reaction",
            "ui",
            "bulk verified"));
        await AssertOkAsync(dispatcher, "AdaptReferenceMaterial", new AdaptReferenceMaterialPayload(
            42,
            "material-1",
            [new ReferenceSlotValuePayload("object", "door")],
            ReferenceRewriteLevels.L2,
            ["door exists"]));
        await AssertOkAsync(dispatcher, "AuditReferenceReuse", new AuditReferenceReusePayload(
            42,
            "material-1",
            "candidate text",
            ReferenceRewriteLevels.L2,
            ["door exists"]));
        await AssertOkAsync(dispatcher, "RecordReferenceUserFeedback", new RecordReferenceUserFeedbackPayload(
            42,
            ReferenceFeedbackTargetTypes.ReuseCandidate,
            "candidate-1",
            ReferenceFeedbackDecisions.Edited,
            "material-1",
            "candidate-1",
            501,
            "beat-1",
            ["too_ai_flavored"],
            "kept pressure image",
            "edited text",
            "user"));
        await AssertOkAsync(dispatcher, "GetReferenceUserFeedback", new GetReferenceUserFeedbackPayload(
            42,
            ReferenceFeedbackTargetTypes.ReuseCandidate,
            "candidate-1",
            5));

        Assert.Equal(
            [
                @"CreateAnchor:42:Anchor:Author:D:\reference.md:markdown:user_provided:workspace:imported:rain,threshold",
                @"CreateAnchors:Bulk Anchor One,Bulk Anchor Two",
                @"CreateAnchorsWithResult:Bulk Result One,Bulk Result Two",
                "GetAnchors:42",
                "DeleteAnchor:42:99",
                "DeleteAnchors:42:100,101",
                "DeleteMaterials:42:material-4,material-5",
                "RestoreMaterials:42:material-4,material-5",
                "PromoteAnchorToWorkspaceCorpus:42:99:imported:migrated,shared",
                "PromoteAnchorsToWorkspaceCorpus:42:100,101:imported:bulk,shared",
                "UpdateAnchorMetadata:42:99:Updated Anchor:Updated Author:licensed:workspace:user_verified:curated,rain",
                "RebuildAnchor:42:99",
                "GetBuildStatus:42:99",
                "SearchMaterials:42:99:fog:passage:unease:interiority:close:afterbeat:2:10:source_backed_detail:archived",
                "GetMaterialTagReviewQueue:42:99:3:25:active",
                "GetMaterialDetail:42:material-1",
                "GetSourceSegmentDetail:42:99:segment-1",
                "GetSourceProcessingDetail:42:99",
                "UpdateMaterialTags:42:material-1:interiority:unease:threshold:close:afterbeat:user:verified",
                "UpdateMaterialsTags:42:material-2,material-3:object_subtext:contained_tension:rain_threshold:limited_close:delayed_reaction:ui:bulk verified",
                "AdaptMaterial:42:material-1:object=door:L2:door exists",
                "AuditCandidate:42:material-1:candidate text:L2:door exists",
                "RecordUserFeedback:42:reuse_candidate:candidate-1:edited:material-1:candidate-1:501:beat-1:too_ai_flavored:kept pressure image:edited text:user",
                "GetUserFeedback:42:reuse_candidate:candidate-1:5"
            ],
            service.Calls);
    }

    [Fact]
    public async Task ReferenceAnchorHandlersRedactSourcePathsFromBridgeResults()
    {
        var service = new RecordingReferenceAnchorService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        using var created = await AssertOkJsonAsync(dispatcher, "CreateReferenceAnchor", new CreateReferenceAnchorPayload(
            42,
            "Anchor",
            "Author",
            @"D:\private\reference.md",
            "markdown",
            "user_provided"));
        AssertPathRedacted(created.RootElement.GetProperty("result"), @"D:\private\reference.md");

        using var bulk = await AssertOkJsonAsync(dispatcher, "CreateReferenceAnchors", new CreateReferenceAnchorsPayload(
            [
                new CreateReferenceAnchorPayload(42, "Bulk One", null, @"D:\private\bulk-one.md", "markdown", "user_provided"),
                new CreateReferenceAnchorPayload(42, "Bulk Two", null, @"D:\private\bulk-two.md", "markdown", "user_provided")
            ]));
        var bulkItems = bulk.RootElement.GetProperty("result").EnumerateArray().ToArray();
        Assert.Equal(2, bulkItems.Length);
        AssertPathRedacted(bulkItems[0], @"D:\private\bulk-one.md");
        AssertPathRedacted(bulkItems[1], @"D:\private\bulk-two.md");

        using var bulkWithResult = await AssertOkJsonAsync(dispatcher, "CreateReferenceAnchorsWithResult", new CreateReferenceAnchorsPayload(
            [
                new CreateReferenceAnchorPayload(42, "Bulk Result One", null, @"D:\private\bulk-result-one.md", "markdown", "user_provided"),
                new CreateReferenceAnchorPayload(42, "Bulk Result Two", null, @"D:\private\bulk-result-two.md", "markdown", "user_provided")
            ]));
        var bulkResult = bulkWithResult.RootElement.GetProperty("result");
        var succeededItems = bulkResult.GetProperty("succeeded").EnumerateArray().ToArray();
        Assert.Equal(2, succeededItems.Length);
        AssertPathRedacted(succeededItems[0], @"D:\private\bulk-result-one.md");
        AssertPathRedacted(succeededItems[1], @"D:\private\bulk-result-two.md");
        var failedItem = Assert.Single(bulkResult.GetProperty("failed").EnumerateArray());
        Assert.True(failedItem.GetProperty("retry_available").GetBoolean());
        Assert.False(failedItem.TryGetProperty("source_path", out _));
        AssertReferenceDetailDoesNotExposeSensitiveText(failedItem);
        Assert.DoesNotContain(@"D:\private", failedItem.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var anchors = await AssertOkJsonAsync(dispatcher, "GetReferenceAnchors", 42L);
        var listed = Assert.Single(anchors.RootElement.GetProperty("result").EnumerateArray());
        AssertPathRedacted(listed, @"D:\private\listed.md");

        using var promoted = await AssertOkJsonAsync(dispatcher, "PromoteReferenceAnchorToWorkspaceCorpus", new PromoteReferenceAnchorToWorkspaceCorpusPayload(42, 99));
        AssertPathRedacted(promoted.RootElement.GetProperty("result"), @"D:\private\promoted.md");

        using var promotedBulk = await AssertOkJsonAsync(dispatcher, "PromoteReferenceAnchorsToWorkspaceCorpus", new PromoteReferenceAnchorsToWorkspaceCorpusPayload(42, [100, 101]));
        foreach (var item in promotedBulk.RootElement.GetProperty("result").EnumerateArray())
        {
            AssertPathRedacted(item, @"D:\private\promoted-bulk.md");
        }

        using var updated = await AssertOkJsonAsync(dispatcher, "UpdateReferenceAnchorMetadata", new UpdateReferenceAnchorMetadataPayload(
            42,
            99,
            "Updated",
            "Author",
            "licensed",
            ReferenceCorpusVisibilities.Private,
            ReferenceSourceTrustLevels.UserVerified,
            []));
        AssertPathRedacted(updated.RootElement.GetProperty("result"), @"D:\private\updated.md");
    }

    [Fact]
    public async Task ReferenceAnchorHandlersSanitizeAnchorFreeTextFields()
    {
        var service = new RecordingReferenceAnchorService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        using var created = await AssertOkJsonAsync(dispatcher, "CreateReferenceAnchor", new CreateReferenceAnchorPayload(
            42,
            "Anchor C:/Users/private/reference.md",
            "api_key=\"plain-secret-value\"",
            @"\\server\share\reference.md",
            "markdown",
            "user_provided",
            UserTags: ["safe-tag", "token=\"plain-token-value\""]));
        var raw = created.RootElement.GetProperty("result").GetRawText();

        Assert.DoesNotContain("C:/Users/private/reference.md", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share\reference.md", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plain-secret-value", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plain-token-value", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe-tag", raw, StringComparison.Ordinal);
        AssertPathRedacted(created.RootElement.GetProperty("result"), @"\\server\share\reference.md");
    }

    [Fact]
    public async Task SearchReferenceMaterialsReturnsBoundedPreviewWithoutFullText()
    {
        var service = new RecordingReferenceAnchorService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        using var search = await AssertOkJsonAsync(dispatcher, "SearchReferenceMaterials", new SearchReferenceMaterialsPayload(
            42,
            [99],
            "rain",
            [],
            [],
            [],
            [],
            [],
            1,
            10));

        var item = Assert.Single(search.RootElement.GetProperty("result").GetProperty("items").EnumerateArray());
        var raw = item.GetRawText();
        Assert.False(item.TryGetProperty("text", out _), "Search results must not expose full material text.");
        Assert.Equal("material-1", item.GetProperty("material_id").GetString());
        Assert.True(item.GetProperty("text_truncated").GetBoolean());
        Assert.Contains("雨声压低了门口", item.GetProperty("text_preview").GetString());
        Assert.DoesNotContain(FullMaterialLeakSentinel, raw, StringComparison.Ordinal);

        using var updated = await AssertOkJsonAsync(dispatcher, "UpdateReferenceMaterialTags", new UpdateReferenceMaterialTagsPayload(
            42,
            "material-1",
            "environment",
            null,
            null,
            null,
            null,
            "ui",
            "verified"));
        AssertMaterialSummaryDoesNotExposeFullText(updated.RootElement.GetProperty("result"));

        using var updatedBulk = await AssertOkJsonAsync(dispatcher, "UpdateReferenceMaterialsTags", new UpdateReferenceMaterialsTagsPayload(
            42,
            ["material-1", "material-2"],
            "environment",
            null,
            null,
            null,
            null,
            "ui",
            "bulk verified"));
        foreach (var updatedItem in updatedBulk.RootElement.GetProperty("result").EnumerateArray())
        {
            AssertMaterialSummaryDoesNotExposeFullText(updatedItem);
        }
    }

    [Fact]
    public async Task GetReferenceMaterialTagReviewQueueReturnsBoundedPreviewWithoutFullText()
    {
        var service = new RecordingReferenceAnchorService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        using var queue = await AssertOkJsonAsync(dispatcher, "GetReferenceMaterialTagReviewQueue", new GetReferenceMaterialTagReviewQueuePayload(
            42,
            [],
            1,
            10));

        var item = Assert.Single(queue.RootElement.GetProperty("result").GetProperty("items").EnumerateArray());
        var material = item.GetProperty("material");
        AssertMaterialSummaryDoesNotExposeFullText(material);
        Assert.False(material.TryGetProperty("source_path", out _));
        Assert.Equal(ReferenceMaterialTagReviewIssueCodes.Unverified, item.GetProperty("issues")[0].GetProperty("code").GetString());
        var raw = item.GetRawText();
        Assert.DoesNotContain("source_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FullMaterialLeakSentinel, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdaptReferenceMaterialReturnsBoundedPreviewWithoutFullMaterialText()
    {
        var service = new RecordingReferenceAnchorService
        {
            AdaptMaterialResult = UnsafeAdaptMaterialResultPayload("material-unsafe")
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        using var adapted = await AssertOkJsonAsync(dispatcher, "AdaptReferenceMaterial", new AdaptReferenceMaterialPayload(
            42,
            "material-unsafe",
            [],
            ReferenceRewriteLevels.L1,
            []));

        var result = adapted.RootElement.GetProperty("result");
        var text = result.GetProperty("text").GetString() ?? string.Empty;
        Assert.Equal("candidate-unsafe", result.GetProperty("candidate_id").GetString());
        Assert.True(text.Length <= 803, "Adapted bridge text must be a bounded preview.");
        AssertReferenceDetailDoesNotExposeSensitiveText(result);
        Assert.DoesNotContain("tail-that-proves-unbounded-text", result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditReferenceReuseReturnsRedactedBoundedDiagnostics()
    {
        var service = new RecordingReferenceAnchorService
        {
            ReuseAuditResult = UnsafeReuseAuditPayload()
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        using var audit = await AssertOkJsonAsync(dispatcher, "AuditReferenceReuse", new AuditReferenceReusePayload(
            42,
            "material-unsafe",
            "candidate text",
            ReferenceRewriteLevels.L1,
            ["fact"]));

        var result = audit.RootElement.GetProperty("result");
        Assert.Equal("audit-unsafe", result.GetProperty("audit_id").GetString());
        AssertReferenceDetailDoesNotExposeSensitiveText(result);
        Assert.DoesNotContain("tail-that-proves-unbounded-text", result.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceDetailHandlersRedactDirtyServiceDiagnostics()
    {
        var service = new RecordingReferenceAnchorService
        {
            MaterialDetailResult = UnsafeMaterialDetailPayload(42, "material-unsafe"),
            SourceSegmentDetailResult = UnsafeSourceSegmentDetailPayload(42, 99, "segment-unsafe"),
            SourceProcessingDetailResult = UnsafeSourceProcessingDetailPayload(42, 99)
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        using var materialDetail = await AssertOkJsonAsync(dispatcher, "GetReferenceMaterialDetail", new GetReferenceMaterialDetailPayload(
            42,
            "material-unsafe"));
        AssertReferenceDetailDoesNotExposeSensitiveText(materialDetail.RootElement.GetProperty("result"));
        var materialResult = materialDetail.RootElement.GetProperty("result");
        Assert.Equal("第一章", materialResult.GetProperty("segments")[0].GetProperty("chapter_title").GetString());
        Assert.Equal("safe-tag", materialResult.GetProperty("source").GetProperty("user_tags")[0].GetString());

        using var sourceSegment = await AssertOkJsonAsync(dispatcher, "GetReferenceSourceSegmentDetail", new GetReferenceSourceSegmentDetailPayload(
            42,
            99,
            "segment-unsafe"));
        AssertReferenceDetailDoesNotExposeSensitiveText(sourceSegment.RootElement.GetProperty("result"));
        var sourceSegmentResult = sourceSegment.RootElement.GetProperty("result");
        Assert.Equal("segment-unsafe", sourceSegmentResult.GetProperty("segment").GetProperty("segment_id").GetString());
        Assert.Contains("redacted", sourceSegmentResult.GetProperty("segment").GetProperty("text_preview").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(sourceSegmentResult.GetProperty("segment").TryGetProperty("text", out _));

        using var sourceProcessing = await AssertOkJsonAsync(dispatcher, "GetReferenceSourceProcessingDetail", new GetReferenceSourceProcessingDetailPayload(
            42,
            99));
        AssertReferenceDetailDoesNotExposeSensitiveText(sourceProcessing.RootElement.GetProperty("result"));
        var sourceProcessingResult = sourceProcessing.RootElement.GetProperty("result");
        Assert.Equal(1, sourceProcessingResult.GetProperty("current_status").GetProperty("source_segment_count").GetInt32());
        Assert.Equal(2, sourceProcessingResult.GetProperty("attempt_count").GetInt32());
        Assert.Equal("attempt-unsafe", sourceProcessingResult.GetProperty("current_attempt").GetProperty("attempt_id").GetString());
        Assert.Equal("prior-attempt-unsafe", sourceProcessingResult.GetProperty("prior_attempts")[0].GetProperty("attempt_id").GetString());
    }

    [Fact]
    public async Task ReferenceBuildStatusHandlersRedactDirtyDiagnostics()
    {
        var service = new RecordingReferenceAnchorService
        {
            RebuildStatusResult = UnsafeBuildStatusPayload(42, 99),
            BuildStatusResult = UnsafeBuildStatusPayload(42, 99)
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);

        using var rebuild = await AssertOkJsonAsync(dispatcher, "RebuildReferenceAnchor", 42L, 99L);
        AssertReferenceDetailDoesNotExposeSensitiveText(rebuild.RootElement.GetProperty("result"));
        Assert.Equal(3, rebuild.RootElement.GetProperty("result").GetProperty("source_segment_count").GetInt32());

        using var status = await AssertOkJsonAsync(dispatcher, "GetReferenceAnchorBuildStatus", 42L, 99L);
        AssertReferenceDetailDoesNotExposeSensitiveText(status.RootElement.GetProperty("result"));
        Assert.Equal(2, status.RootElement.GetProperty("result").GetProperty("material_count").GetInt32());
    }

    [Fact]
    public async Task GetReferenceDraftCandidatesReturnsBoundedRedactedText()
    {
        var service = new RecordingReferenceAnchoredDraftService
        {
            DraftCandidatesResult = [UnsafeDraftCandidatePayload(501, "candidate-unsafe")]
        };
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchoredDraftHandlers(service);

        using var json = await AssertOkJsonAsync(dispatcher, "GetReferenceDraftCandidates", new GetReferenceDraftCandidatesPayload(
            42,
            501,
            ["candidate-unsafe"]));
        var candidate = Assert.Single(json.RootElement.GetProperty("result").EnumerateArray());

        Assert.Equal("candidate-unsafe", candidate.GetProperty("candidate_id").GetString());
        Assert.Equal(501, candidate.GetProperty("blueprint_id").GetInt64());
        AssertReferenceDetailDoesNotExposeSensitiveText(candidate);
        Assert.DoesNotContain("tail-that-proves-unbounded-text", candidate.GetRawText(), StringComparison.Ordinal);
        Assert.False(candidate.TryGetProperty("source_text", out _));
        Assert.False(candidate.TryGetProperty("candidate_text", out _));
        Assert.False(candidate.TryGetProperty("prompt", out _));
        Assert.False(candidate.TryGetProperty("source_path", out _));
        Assert.False(candidate.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task ReferenceAnchoredDraftHandlersRouteEveryMethodToServiceOperations()
    {
        var service = new RecordingReferenceAnchoredDraftService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchoredDraftHandlers(service);

        await AssertOkAsync(dispatcher, "GenerateReferenceChapterBlueprint", new GenerateReferenceChapterBlueprintPayload(
            42,
            7,
            "Blueprint",
            "tighten the reveal",
            [99],
            ["known clue"],
            ["culprit identity"]));
        await AssertOkAsync(dispatcher, "GetReferenceChapterBlueprints", 42L, null);
        await AssertOkAsync(dispatcher, "GetReferenceChapterBlueprint", 42L, 501L);
        await AssertOkAsync(dispatcher, "ReviewReferenceChapterBlueprint", new ReviewReferenceChapterBlueprintPayload(42, 501));
        await AssertOkAsync(dispatcher, "ReviseReferenceChapterBlueprint", new ReviseReferenceChapterBlueprintPayload(
            42,
            501,
            [new ReferenceBlueprintRevisionChangePayload("beat:beat-1:paragraph_intention", "linger on the threshold")],
            "user",
            "tighten execution"));
        await AssertOkAsync(dispatcher, "ApproveReferenceChapterBlueprint", new ApproveReferenceChapterBlueprintPayload(42, 501, "review-1"));
        await AssertOkAsync(dispatcher, "BindReferenceBlueprintMaterials", new BindReferenceBlueprintMaterialsPayload(42, 501, 3, SelectTopCandidate: true));
        await AssertOkAsync(dispatcher, "GenerateReferenceAnchoredDraft", new GenerateReferenceAnchoredDraftPayload(42, 501, ["beat-1", "beat-2"]));
        await AssertOkAsync(dispatcher, "GetReferenceDraftCandidates", new GetReferenceDraftCandidatesPayload(42, 501, ["candidate-1"]));
        await AssertOkAsync(dispatcher, "AuditReferenceAnchoredDraft", new AuditReferenceAnchoredDraftPayload(42, 501, ["candidate-1"]));
        await AssertOkAsync(dispatcher, "GetReferenceAnchoredDraftAudits", new GetReferenceAnchoredDraftAuditsPayload(42, 501, ["candidate-1"], 10));
        await AssertOkAsync(dispatcher, "GetReferenceStyleAuditFindings", new GetReferenceStyleAuditFindingsPayload(
            42,
            501,
            ["candidate-1"],
            ["source_leak"],
            10));
        await AssertOkAsync(dispatcher, "StartReferenceOrchestrationRun", new StartReferenceOrchestrationRunPayload(
            42,
            7,
            "tighten the reveal",
            ["known clue"],
            ["culprit identity"],
            null,
            new ReferenceCorpusSearchPolicyPayload("story_context", 3, ["user_provided"], [99], []),
            SourceConfirmed: false,
            StylePolicy: new ReferenceOrchestrationStylePolicyPayload(
                [301],
                ["dialogue_ratio", "sensory_ratio"],
                ReferenceStyleImitationIntensities.Strong,
                0.8,
                "moderate",
                ["dialogue_exchange"],
                ["source_leak", "style_distance"])));
        await AssertOkAsync(dispatcher, "GetReferenceOrchestrationRuns", 42L, 7);
        await AssertOkAsync(dispatcher, "GetReferenceOrchestrationRun", 42L, "run-1");
        await AssertOkAsync(dispatcher, "GetReferenceOrchestrationRunEvents", 42L, "run-1");
        await AssertOkAsync(dispatcher, "ResumeReferenceOrchestrationRun", new ResumeReferenceOrchestrationRunPayload(
            42,
            "run-1",
            ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
            "review-1"));
        await AssertOkAsync(dispatcher, "CancelReferenceOrchestrationRun", new CancelReferenceOrchestrationRunPayload(
            42,
            "run-1",
            "user cancelled"));

        Assert.Equal(
            [
                "GenerateChapterBlueprint:42:7:Blueprint:tighten the reveal:99:known clue:culprit identity",
                "GetChapterBlueprints:42:<null>",
                "GetChapterBlueprint:42:501",
                "ReviewChapterBlueprint:42:501",
                "ReviseChapterBlueprint:42:501:beat:beat-1:paragraph_intention=linger on the threshold:user:tighten execution",
                "ApproveChapterBlueprint:42:501:review-1",
                "BindBlueprintMaterials:42:501:3:True",
                "GenerateDraftFromBlueprint:42:501:beat-1,beat-2",
                "GetDraftCandidates:42:501:candidate-1",
                "AuditDraftAgainstBlueprint:42:501:candidate-1",
                "GetDraftAudits:42:501:candidate-1:10",
                "GetStyleAuditFindings:42:501:candidate-1:source_leak:10",
                "StartOrchestrationRun:42:7:tighten the reveal:known clue:culprit identity:<null>:story_context:3:user_provided:99::<false>:301:dialogue_ratio,sensory_ratio:strong:0.8:moderate:dialogue_exchange:source_leak,style_distance",
                "GetOrchestrationRuns:42:7",
                "GetOrchestrationRun:42:run-1",
                "GetOrchestrationRunEvents:42:run-1",
                "ResumeOrchestrationRun:42:run-1:approve_blueprint:review-1",
                "CancelOrchestrationRun:42:run-1:user cancelled"
            ],
            service.Calls);
    }

    [Fact]
    public async Task ReferenceStyleProfileHandlersRouteEveryMethodToServiceOperations()
    {
        var service = new RecordingReferenceStyleProfileService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceStyleProfileHandlers(service);

        await AssertOkAsync(dispatcher, "BuildReferenceStyleProfile", new BuildReferenceStyleProfilePayload(
            42,
            "雨夜克制风格",
            "deterministic baseline",
            [99, 100],
            ["user_provided", "licensed"],
            [ReferenceSourceTrustLevels.UserVerified, ReferenceSourceTrustLevels.Imported]));
        await AssertOkAsync(dispatcher, "GetReferenceStyleProfiles", new GetReferenceStyleProfilesPayload(42, IncludeArchived: true));
        await AssertOkAsync(dispatcher, "GetReferenceStyleProfile", 42L, 501L);
        await AssertOkAsync(dispatcher, "GetReferenceStyleProfileBuildStatus", new GetReferenceStyleProfileBuildStatusPayload(
            42,
            "style-build-1"));
        await AssertOkAsync(dispatcher, "CancelReferenceStyleProfileBuild", new CancelReferenceStyleProfileBuildPayload(
            42,
            "style-build-1"));
        await AssertOkAsync(dispatcher, "ArchiveReferenceStyleProfile", new ArchiveReferenceStyleProfilePayload(42, 501));
        await AssertOkAsync(dispatcher, "RestoreReferenceStyleProfile", new RestoreReferenceStyleProfilePayload(42, 501));
        await AssertOkAsync(dispatcher, "CompareReferenceStyleProfiles", new CompareReferenceStyleProfilesPayload(42, 501, 502));

        Assert.Equal(
            [
                "BuildStyleProfile:42:雨夜克制风格:deterministic baseline:99,100:user_provided,licensed:user_verified,imported",
                "GetStyleProfiles:42:True",
                "GetStyleProfile:42:501",
                "GetStyleProfileBuildStatus:42:style-build-1",
                "CancelStyleProfileBuild:42:style-build-1",
                "ArchiveStyleProfile:42:501",
                "RestoreStyleProfile:42:501",
                "CompareStyleProfiles:42:501:502"
            ],
            service.Calls);
    }

    [Fact]
    public async Task ReferenceAnchorHandlersPreserveExistingBridgeErrorSemantics()
    {
        var service = new RecordingReferenceAnchorService();
        var dispatcher = new BridgeDispatcher().RegisterReferenceAnchorHandlers(service);
        var input = new CreateReferenceAnchorPayload(
            42,
            "Anchor",
            null,
            "../source.md",
            "markdown",
            "user_provided");

        service.CreateAnchorException = new AppNotInitializedException();
        using var appNotInitialized = await AssertErrorAsync(
            dispatcher,
            "CreateReferenceAnchor",
            BridgeErrorCodes.AppNotInitialized,
            input);
        Assert.Equal("Application is not initialized.", appNotInitialized.RootElement.GetProperty("error").GetProperty("message").GetString());

        service.CreateAnchorException = new InvalidContentPathException(
            "../source.md",
            "Parent-directory and empty path segments are not allowed.");
        using var invalidPath = await AssertErrorAsync(
            dispatcher,
            "CreateReferenceAnchor",
            BridgeErrorCodes.InvalidPath,
            input);
        var details = invalidPath.RootElement.GetProperty("error").GetProperty("details");
        Assert.Equal("Parent-directory and empty path segments are not allowed.", details.GetProperty("path").GetString());
        Assert.Equal("../source.md", details.GetProperty("value").GetString());
    }

    private static async Task AssertOkAsync(BridgeDispatcher dispatcher, string method, params object?[] args)
    {
        using var json = await AssertOkJsonAsync(dispatcher, method, args);
    }

    private static async Task<JsonDocument> AssertOkJsonAsync(BridgeDispatcher dispatcher, string method, params object?[] args)
    {
        var result = await dispatcher.DispatchAsync(Request(method, args));
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));

        var json = JsonDocument.Parse(result.OutboundJson);
        Assert.Equal("response", json.RootElement.GetProperty("kind").GetString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        return json;
    }

    private static void AssertPathRedacted(JsonElement anchor, string originalPath)
    {
        Assert.True(anchor.TryGetProperty("source_path", out var sourcePath), "Anchor payload must preserve the compatibility field.");
        Assert.Equal(string.Empty, sourcePath.GetString());
        Assert.DoesNotContain(originalPath, anchor.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertMaterialSummaryDoesNotExposeFullText(JsonElement material)
    {
        var raw = material.GetRawText();
        Assert.False(material.TryGetProperty("text", out _), "Material summary responses must not expose full material text.");
        Assert.True(material.TryGetProperty("text_preview", out _), "Material summary responses must expose bounded text_preview.");
        Assert.True(material.TryGetProperty("text_truncated", out _), "Material summary responses must expose text_truncated.");
        Assert.DoesNotContain(FullMaterialLeakSentinel, raw, StringComparison.Ordinal);
    }

    private static void AssertReferenceDetailDoesNotExposeSensitiveText(JsonElement result)
    {
        var raw = result.GetRawText();
        foreach (var forbidden in new[]
        {
            @"D:\private",
            "C:/Users/private",
            @"\\server\share",
            "file://",
            "/Users/private",
            "source_path",
            "source_text",
            "candidate_text",
            "prompt",
            "sk-proj",
            "Bearer dirty",
            "json secret source",
            "json generated candidate",
            "json hidden prompt",
            "non-sk-secret-value",
            "plain-token-value",
            "jsonauthorizationtoken",
            FullMaterialLeakSentinel
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("redacted", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static ReferenceMaterialDetailPayload UnsafeMaterialDetailPayload(long novelId, string materialId)
    {
        var unsafeText = UnsafeDiagnosticText();
        return new ReferenceMaterialDetailPayload(
            new ReferenceMaterialSummaryPayload(
                materialId,
                99,
                "seg-unsafe",
                ReferenceMaterialTypes.Passage,
                "environment",
                "pressure",
                "rain",
                "close",
                "sensory",
                0.8,
                0.7,
                0.9,
                unsafeText,
                TextTruncated: true,
                "hash",
                "test",
                UserVerified: false,
                DateTimeOffset.UtcNow,
                ScoreComponents: new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["source_path_score"] = 1
                }),
            new ReferenceMaterialSourceSummaryPayload(
                99,
                novelId,
                "Unsafe Source",
                "Author",
                "markdown",
                "user_provided",
                "hash",
                "test",
                ReferenceAnchorBuildStates.Ready,
                ReferenceCorpusVisibilities.Private,
                ReferenceSourceTrustLevels.UserVerified,
                ["safe-tag"],
                ReferenceAnchorOwnerScopes.Novel,
                novelId),
            [
                new ReferenceMaterialSegmentPreviewPayload(
                    "seg-unsafe",
                    "paragraph",
                    1,
                    "第一章",
                    0,
                    unsafeText,
                    TextTruncated: true,
                    "seg-hash")
            ],
            [
                new ReferenceMaterialSlotPreviewPayload("object", unsafeText, 0, 4)
            ],
            [
                new ReferenceMaterialProcessingNotePayload(
                    "extract",
                    ReferenceAnchorBuildStates.FailedExtraction,
                    unsafeText,
                    DateTimeOffset.UtcNow,
                    SourceSegmentCount: 1,
                    MaterialCount: 1,
                    SlotCount: 1,
                    VectorCount: 0,
                    AffectedSourceId: @"D:\private\reference.md",
                    AffectedMaterialId: materialId,
                    AffectedSegmentId: "seg-unsafe",
                    AffectedSlotId: "slot-unsafe")
            ]);
    }

    private static ReferenceSourceSegmentDetailPayload UnsafeSourceSegmentDetailPayload(
        long novelId,
        long anchorId,
        string segmentId)
    {
        var unsafeText = UnsafeDiagnosticText();
        return new ReferenceSourceSegmentDetailPayload(
            new ReferenceMaterialSourceSummaryPayload(
                anchorId,
                novelId,
                "Unsafe Source",
                "Author",
                "markdown",
                "user_provided",
                "hash",
                "test",
                ReferenceAnchorBuildStates.FailedExtraction,
                ReferenceCorpusVisibilities.Private,
                ReferenceSourceTrustLevels.Imported,
                ["safe-tag"],
                ReferenceAnchorOwnerScopes.Novel,
                novelId),
            new ReferenceSourceSegmentPreviewPayload(
                anchorId,
                segmentId,
                "paragraph",
                1,
                unsafeText,
                0,
                @"file://D:/private/parent",
                0,
                128,
                unsafeText,
                TextTruncated: true,
                @"D:\private\segment-hash"),
            [
                new ReferenceMaterialProcessingNotePayload(
                    "extract",
                    ReferenceAnchorBuildStates.FailedExtraction,
                    unsafeText,
                    DateTimeOffset.UtcNow,
                    SourceSegmentCount: 1,
                    MaterialCount: 0,
                    SlotCount: 0,
                    VectorCount: 0,
                    AffectedSourceId: @"D:\private\reference.md",
                    AffectedSegmentId: segmentId)
            ]);
    }

    private static ReferenceSourceProcessingDetailPayload UnsafeSourceProcessingDetailPayload(long novelId, long anchorId)
    {
        var unsafeText = UnsafeDiagnosticText();
        return new ReferenceSourceProcessingDetailPayload(
            new ReferenceMaterialSourceSummaryPayload(
                anchorId,
                novelId,
                "Unsafe Source",
                "Author",
                "markdown",
                "user_provided",
                "hash",
                "test",
                ReferenceAnchorBuildStates.FailedExtraction,
                ReferenceCorpusVisibilities.Private,
                ReferenceSourceTrustLevels.Imported,
                ["safe-tag"],
                ReferenceAnchorOwnerScopes.Novel,
                novelId),
            new ReferenceSourceProcessingStatusPayload(
                "extract",
                ReferenceAnchorBuildStates.FailedExtraction,
                unsafeText,
                DateTimeOffset.UtcNow,
                SourceSegmentCount: 1,
                MaterialCount: 1,
                SlotCount: 1,
                VectorCount: 0),
            [
                new ReferenceSourceProcessingEventPayload(
                    "event-unsafe",
                    "extract",
                    ReferenceAnchorBuildStates.FailedExtraction,
                    unsafeText,
                    DateTimeOffset.UtcNow,
                    SourceSegmentCount: 1,
                    MaterialCount: 1,
                    SlotCount: 1,
                    VectorCount: 0,
                    AffectedSourceId: @"file://D:/private/reference.md",
                    AffectedMaterialId: "material-unsafe",
                    AffectedSegmentId: "seg-unsafe",
                    AffectedSlotId: "slot-unsafe")
            ],
            RetryAvailable: true,
            RebuildAvailable: true,
            AttemptCount: 2,
            CurrentAttempt: new ReferenceSourceProcessingAttemptPayload(
                AttemptId: "attempt-unsafe",
                AttemptNumber: 2,
                BuildId: "build-unsafe",
                BuildVersion: "test",
                Stage: "extract",
                Status: ReferenceAnchorBuildStates.FailedExtraction,
                StartedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                CompletedAt: DateTimeOffset.UtcNow,
                EventCount: 1,
                SourceSegmentCount: 1,
                MaterialCount: 1,
                SlotCount: 1,
                VectorCount: 0,
                RecoveredFromAttemptId: @"file://D:/private/attempt",
                RecoveredFromBuildId: @"D:\private\build",
                BlockedReason: unsafeText),
            PriorAttempts:
            [
                new ReferenceSourceProcessingAttemptPayload(
                    AttemptId: "prior-attempt-unsafe",
                    AttemptNumber: 1,
                    BuildId: "prior-build-unsafe",
                    BuildVersion: "test",
                    Stage: "extract",
                    Status: ReferenceAnchorBuildStates.FailedExtraction,
                    StartedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    CompletedAt: DateTimeOffset.UtcNow,
                    EventCount: 1,
                    SourceSegmentCount: 1,
                    MaterialCount: 1,
                    SlotCount: 1,
                    VectorCount: 0,
                    RecoveredFromAttemptId: "",
                    RecoveredFromBuildId: "",
                    BlockedReason: unsafeText)
            ],
            RecoveredFromAttemptId: @"file://D:/private/attempt",
            RecoveredFromBuildId: @"D:\private\build",
            BlockedReason: unsafeText);
    }

    private static ReferenceAnchorBuildStatusPayload UnsafeBuildStatusPayload(long novelId, long anchorId)
    {
        return new ReferenceAnchorBuildStatusPayload(
            novelId,
            anchorId,
            ReferenceAnchorBuildStates.FailedExtraction,
            "extract",
            SourceSegmentCount: 3,
            MaterialCount: 2,
            SlotCount: 1,
            VectorCount: 0,
            LastError: UnsafeDiagnosticText(),
            UpdatedAt: DateTimeOffset.UtcNow);
    }

    private static AdaptReferenceMaterialResultPayload UnsafeAdaptMaterialResultPayload(string materialId)
    {
        var unsafeText = UnsafeDiagnosticText() + new string('长', 2_000) + "tail-that-proves-unbounded-text";
        return new AdaptReferenceMaterialResultPayload(
            "candidate-unsafe",
            materialId,
            ReferenceRewriteLevels.L1,
            unsafeText,
            [new ReferenceSlotValuePayload("object", unsafeText)],
            [unsafeText],
            new ReferenceReuseAuditPayload(
                "audit-unsafe",
                "passed",
                ReferenceRewriteLevels.L1,
                [unsafeText],
                [unsafeText],
                [unsafeText],
                [unsafeText],
                [unsafeText],
                DateTimeOffset.UtcNow));
    }

    private static ReferenceReuseAuditPayload UnsafeReuseAuditPayload()
    {
        var unsafeText = UnsafeDiagnosticText() + new string('审', 2_000) + "tail-that-proves-unbounded-text";
        return new ReferenceReuseAuditPayload(
            "audit-unsafe",
            "failed",
            ReferenceRewriteLevels.L1,
            [unsafeText],
            [unsafeText],
            [unsafeText],
            [unsafeText],
            [unsafeText],
            DateTimeOffset.UtcNow);
    }

    private static ReferenceDraftParagraphCandidatePayload UnsafeDraftCandidatePayload(long blueprintId, string candidateId)
    {
        var unsafeText = UnsafeDiagnosticText() + new string('候', 5_000) + "tail-that-proves-unbounded-text";
        return new ReferenceDraftParagraphCandidatePayload(
            candidateId,
            blueprintId,
            "beat-unsafe",
            "material-unsafe",
            ReferenceRewriteLevels.L1,
            unsafeText,
            [new ReferenceSlotValuePayload("object", unsafeText)],
            [unsafeText],
            "passed",
            DateTimeOffset.UtcNow,
            [
                new ReferenceDraftStyleAttemptPayload(
                    [301],
                    ["dialogue_ratio"],
                    ReferenceStyleImitationIntensities.Moderate,
                    0.8,
                    "moderate",
                    ["dialogue_exchange"],
                    ["source_leak"],
                    0.9,
                    SelectedMaterialLowConfidence: false,
                    "attempted")
            ]);
    }

    private static string UnsafeDiagnosticText()
    {
        return @"source_path: D:\private\reference.md; source_text: secret source; candidate_text: generated candidate; prompt: hidden prompt; {""source_text"":""json secret source"",""candidate_text"":""json generated candidate"",""prompt"":""json hidden prompt"",""api_key"":""non-sk-secret-value"",""token"":""plain-token-value"",""authorization"":""Bearer jsonauthorizationtokenabcdefghijklmnopqrstuvwxyz""}; C:/Users/private/reference.md; \\server\share\secret.md; file://D:/private/reference.md; /Users/private/reference.md; token=dirty-token-abcdefghijklmnopqrstuvwxyz; api_key=sk-proj-dirtyabcdefghijklmnopqrstuvwxyz1234567890; Bearer dirtytokenabcdefghijklmnopqrstuvwxyz; " + FullMaterialLeakSentinel;
    }

    private static async Task<JsonDocument> AssertErrorAsync(
        BridgeDispatcher dispatcher,
        string method,
        string expectedCode,
        params object?[] args)
    {
        var result = await dispatcher.DispatchAsync(Request(method, args));
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));

        var json = JsonDocument.Parse(result.OutboundJson);
        Assert.Equal("response", json.RootElement.GetProperty("kind").GetString());
        Assert.Equal($"req_{method}", json.RootElement.GetProperty("id").GetString());
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(json.RootElement.GetProperty("error").GetProperty("retryable").GetBoolean());
        return json;
    }

    private static string Request(string method, object?[] args)
    {
        return JsonSerializer.Serialize(
            new
            {
                kind = "request",
                id = $"req_{method}",
                method,
                payload = new { args }
            },
            BridgeJson.SerializerOptions);
    }

    private sealed class RecordingReferenceAnchorService : IReferenceAnchorService
    {
        public List<string> Calls { get; } = [];

        public Exception? CreateAnchorException { get; set; }
        public ReferenceMaterialDetailPayload? MaterialDetailResult { get; set; }
        public ReferenceSourceSegmentDetailPayload? SourceSegmentDetailResult { get; set; }
        public ReferenceSourceProcessingDetailPayload? SourceProcessingDetailResult { get; set; }
        public ReferenceAnchorBuildStatusPayload? RebuildStatusResult { get; set; }
        public ReferenceAnchorBuildStatusPayload? BuildStatusResult { get; set; }
        public AdaptReferenceMaterialResultPayload? AdaptMaterialResult { get; set; }

        public ReferenceReuseAuditPayload? ReuseAuditResult { get; set; }

        private static ReferenceAnchorPayload CreateAnchorPayload(
            long anchorId,
            long novelId,
            string title,
            string author,
            string sourcePath,
            string sourceKind,
            string licenseStatus,
            string visibility = ReferenceCorpusVisibilities.Private,
            string sourceTrust = ReferenceSourceTrustLevels.UserVerified,
            IReadOnlyList<string>? userTags = null)
        {
            return new ReferenceAnchorPayload(
                anchorId,
                novelId,
                title,
                author,
                sourcePath,
                sourceKind,
                licenseStatus,
                "hash",
                "test",
                ReferenceAnchorBuildStates.Ready,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                visibility,
                sourceTrust,
                userTags ?? []);
        }

        private static ReferenceMaterialPayload CreateMaterialPayload(string materialId)
        {
            return new ReferenceMaterialPayload(
                materialId,
                99,
                "segment-1",
                ReferenceMaterialTypes.Passage,
                "environment",
                "restrained",
                "rain_threshold",
                "close",
                "delayed_reaction",
                0.91,
                0.88,
                0.9,
                "雨声压低了门口，林岚只看见杯底半圈水痕，没有急着给出判断。" +
                    "这是一段用于验证 bridge 列表响应必须截断的完整素材正文，不能透出到 UI 或 agent 搜索结果。" +
                    "它继续补充窗台潮气、墙根泥点、杯沿缺口和门后停顿，让正文长度超过列表预览上限。" +
                    "额外补充一段更长的材料说明，确保测试不会因为字符长度临界值而误判为未截断。" +
                    FullMaterialLeakSentinel,
                "hash-material-1",
                "test-extractor",
                false,
                DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["lexical"] = 0.92,
                    ["prose_duty"] = 0.86
                });
        }

        public ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
            CreateReferenceAnchorPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CreateAnchorException is { } exception)
            {
                CreateAnchorException = null;
                throw exception;
            }

            Calls.Add($"CreateAnchor:{input.NovelId}:{input.Title}:{input.Author}:{input.SourcePath}:{input.SourceKind}:{input.LicenseStatus}:{input.Visibility}:{input.SourceTrust}:{string.Join(",", input.UserTags ?? [])}");
            return ValueTask.FromResult(CreateAnchorPayload(
                anchorId: 99,
                novelId: input.NovelId,
                title: input.Title,
                author: input.Author ?? string.Empty,
                sourcePath: input.SourcePath,
                sourceKind: input.SourceKind,
                licenseStatus: input.LicenseStatus,
                visibility: input.Visibility ?? ReferenceCorpusVisibilities.Private,
                sourceTrust: input.SourceTrust ?? ReferenceSourceTrustLevels.UserVerified,
                userTags: input.UserTags ?? []));
        }

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> CreateAnchorsAsync(
            CreateReferenceAnchorsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"CreateAnchors:{string.Join(",", input.Anchors.Select(anchor => anchor.Title))}");
            return ValueTask.FromResult<IReadOnlyList<ReferenceAnchorPayload>>(
                input.Anchors
                    .Select((anchor, index) => CreateAnchorPayload(
                        100 + index,
                        anchor.NovelId,
                        anchor.Title,
                        anchor.Author ?? string.Empty,
                        anchor.SourcePath,
                        anchor.SourceKind,
                        anchor.LicenseStatus,
                        anchor.Visibility ?? ReferenceCorpusVisibilities.Private,
                        anchor.SourceTrust ?? ReferenceSourceTrustLevels.UserVerified,
                        anchor.UserTags ?? []))
                    .ToArray());
        }

        public ValueTask<CreateReferenceAnchorsResultPayload> CreateAnchorsWithResultAsync(
            CreateReferenceAnchorsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"CreateAnchorsWithResult:{string.Join(",", input.Anchors.Select(anchor => anchor.Title))}");
            var succeeded = input.Anchors
                .Select((anchor, index) => CreateAnchorPayload(
                    200 + index,
                    anchor.NovelId,
                    anchor.Title,
                    anchor.Author ?? string.Empty,
                    anchor.SourcePath,
                    anchor.SourceKind,
                    anchor.LicenseStatus,
                    anchor.Visibility ?? ReferenceCorpusVisibilities.Private,
                    anchor.SourceTrust ?? ReferenceSourceTrustLevels.UserVerified,
                    anchor.UserTags ?? []))
                .ToArray();
            IReadOnlyList<CreateReferenceAnchorFailurePayload> failed =
            [
                new CreateReferenceAnchorFailurePayload(
                    input.Anchors.Count,
                    @"Failed D:\private\failed.md",
                    "markdown",
                    "source:failed-source",
                    UnsafeDiagnosticText(),
                    RetryAvailable: true)
            ];
            return ValueTask.FromResult(new CreateReferenceAnchorsResultPayload(
                succeeded,
                failed,
                input.Anchors.Count + failed.Count,
                succeeded.Length,
                failed.Count));
        }

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
            long novelId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetAnchors:{novelId}");
            IReadOnlyList<ReferenceAnchorPayload> anchors =
            [
                CreateAnchorPayload(
                    99,
                    novelId,
                    "Listed Anchor",
                    "Author",
                    @"D:\private\listed.md",
                    "markdown",
                    "user_provided")
            ];
            return ValueTask.FromResult(anchors);
        }

        public ValueTask DeleteAnchorAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"DeleteAnchor:{novelId}:{anchorId}");
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAnchorsAsync(
            DeleteReferenceAnchorsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"DeleteAnchors:{input.NovelId}:{string.Join(",", input.AnchorIds)}");
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteMaterialsAsync(
            DeleteReferenceMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"DeleteMaterials:{input.NovelId}:{string.Join(",", input.MaterialIds)}");
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreMaterialsAsync(
            RestoreReferenceMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"RestoreMaterials:{input.NovelId}:{string.Join(",", input.MaterialIds)}");
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReferenceAnchorPayload> PromoteAnchorToWorkspaceCorpusAsync(
            PromoteReferenceAnchorToWorkspaceCorpusPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"PromoteAnchorToWorkspaceCorpus:{input.NovelId}:{input.AnchorId}:{input.SourceTrust}:{string.Join(",", input.UserTags ?? [])}");
            return ValueTask.FromResult(CreateAnchorPayload(
                input.AnchorId,
                0,
                "Promoted Anchor",
                "Author",
                @"D:\private\promoted.md",
                "markdown",
                "user_provided",
                ReferenceCorpusVisibilities.Workspace,
                input.SourceTrust ?? ReferenceSourceTrustLevels.UserVerified,
                input.UserTags ?? []));
        }

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> PromoteAnchorsToWorkspaceCorpusAsync(
            PromoteReferenceAnchorsToWorkspaceCorpusPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"PromoteAnchorsToWorkspaceCorpus:{input.NovelId}:{string.Join(",", input.AnchorIds)}:{input.SourceTrust}:{string.Join(",", input.UserTags ?? [])}");
            return ValueTask.FromResult<IReadOnlyList<ReferenceAnchorPayload>>(
                input.AnchorIds
                    .Select(anchorId => CreateAnchorPayload(
                        anchorId,
                        0,
                        "Promoted Bulk Anchor",
                        "Author",
                        @"D:\private\promoted-bulk.md",
                        "markdown",
                        "user_provided",
                        ReferenceCorpusVisibilities.Workspace,
                        input.SourceTrust ?? ReferenceSourceTrustLevels.UserVerified,
                        input.UserTags ?? []))
                    .ToArray());
        }

        public ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
            UpdateReferenceAnchorMetadataPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"UpdateAnchorMetadata:{input.NovelId}:{input.AnchorId}:{input.Title}:{input.Author}:{input.LicenseStatus}:{input.Visibility}:{input.SourceTrust}:{string.Join(",", input.UserTags)}");
            return ValueTask.FromResult(CreateAnchorPayload(
                input.AnchorId,
                input.NovelId,
                input.Title,
                input.Author ?? string.Empty,
                @"D:\private\updated.md",
                "markdown",
                input.LicenseStatus,
                input.Visibility,
                input.SourceTrust,
                input.UserTags));
        }

        public ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"RebuildAnchor:{novelId}:{anchorId}");
            return ValueTask.FromResult(RebuildStatusResult ?? new ReferenceAnchorBuildStatusPayload(
                novelId,
                anchorId,
                ReferenceAnchorBuildStates.Ready,
                "ready",
                1,
                1,
                0,
                0,
                string.Empty,
                DateTimeOffset.UtcNow));
        }

public ValueTask<ReferenceAnchorBuildStatusPayload?> GetBuildStatusAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetBuildStatus:{novelId}:{anchorId}");
return ValueTask.FromResult<ReferenceAnchorBuildStatusPayload?>(BuildStatusResult);
}

 public ValueTask<ReferenceMaterialEmbeddingBackfillPayload> BackfillMaterialEmbeddingsAsync(
 BackfillReferenceMaterialEmbeddingsPayload input,
 CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 return ValueTask.FromResult(EmptyMaterialEmbeddingBackfill());
 }

 private static ReferenceMaterialEmbeddingBackfillPayload EmptyMaterialEmbeddingBackfill() =>
 new("test", "test", 1, 0, 0, 0, 0, 0, 0, []);

        public ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(
            SearchReferenceMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                $"SearchMaterials:{input.NovelId}:{string.Join(',', input.AnchorIds)}:{input.Query}:{string.Join(',', input.MaterialTypes)}:{string.Join(',', input.EmotionTags)}:{string.Join(',', input.FunctionTags)}:{string.Join(',', input.PovTags)}:{string.Join(',', input.TechniqueTags)}:{input.Page}:{input.Size}:{string.Join(',', input.ProseDuties ?? [])}:{input.ArchiveFilter}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialPayload>(
                [CreateMaterialPayload("material-1")],
                1,
                input.Page,
                input.Size,
                1));
        }

        public ValueTask<PageResultPayload<ReferenceMaterialTagReviewItemPayload>> GetMaterialTagReviewQueueAsync(
            GetReferenceMaterialTagReviewQueuePayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetMaterialTagReviewQueue:{input.NovelId}:{string.Join(',', input.AnchorIds)}:{input.Page}:{input.Size}:{input.ArchiveFilter}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialTagReviewItemPayload>(
                [
                    new ReferenceMaterialTagReviewItemPayload(
                        CreateMaterialSummaryPayload("material-1"),
                        [
                            new ReferenceMaterialTagReviewIssuePayload(
                                ReferenceMaterialTagReviewIssueCodes.Unverified,
                                "未校正",
                                "review")
                        ])
                ],
                1,
                input.Page,
                input.Size,
                1));
        }

        private static ReferenceMaterialSummaryPayload CreateMaterialSummaryPayload(string materialId)
        {
            var material = CreateMaterialPayload(materialId);
            return new ReferenceMaterialSummaryPayload(
                material.MaterialId,
                material.AnchorId,
                material.SourceSegmentId,
                material.MaterialType,
                material.FunctionTag,
                material.EmotionTag,
                material.SceneTag,
                material.PovTag,
                material.TechniqueTag,
                material.FunctionConfidence,
                material.EmotionConfidence,
                material.PovConfidence,
                "雨声压低了门口，林岚只看见杯底半圈水痕...",
                true,
                material.SourceHash,
                material.ExtractorVersion,
                material.UserVerified,
                material.CreatedAt,
                ReferenceMaterialArchiveFilters.Active,
                ScoreComponents: material.ScoreComponents);
        }

        public ValueTask<ReferenceMaterialDetailPayload?> GetMaterialDetailAsync(
            GetReferenceMaterialDetailPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetMaterialDetail:{input.NovelId}:{input.MaterialId}");
            return ValueTask.FromResult(MaterialDetailResult);
        }

        public ValueTask<ReferenceSourceSegmentDetailPayload?> GetSourceSegmentDetailAsync(
            GetReferenceSourceSegmentDetailPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetSourceSegmentDetail:{input.NovelId}:{input.AnchorId}:{input.SegmentId}");
            return ValueTask.FromResult(SourceSegmentDetailResult);
        }

        public ValueTask<ReferenceSourceProcessingDetailPayload?> GetSourceProcessingDetailAsync(
            GetReferenceSourceProcessingDetailPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetSourceProcessingDetail:{input.NovelId}:{input.AnchorId}");
            return ValueTask.FromResult(SourceProcessingDetailResult);
        }

        public ValueTask<ReferenceMaterialPayload> UpdateMaterialTagsAsync(
            UpdateReferenceMaterialTagsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"UpdateMaterialTags:{input.NovelId}:{input.MaterialId}:{input.FunctionTag}:{input.EmotionTag}:{input.SceneTag}:{input.PovTag}:{input.TechniqueTag}:{input.Origin}:{input.Note}");
            return ValueTask.FromResult(CreateMaterialPayload(input.MaterialId));
        }

        public ValueTask<IReadOnlyList<ReferenceMaterialPayload>> UpdateMaterialsTagsAsync(
            UpdateReferenceMaterialsTagsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"UpdateMaterialsTags:{input.NovelId}:{string.Join(',', input.MaterialIds)}:{input.FunctionTag}:{input.EmotionTag}:{input.SceneTag}:{input.PovTag}:{input.TechniqueTag}:{input.Origin}:{input.Note}");
            return ValueTask.FromResult<IReadOnlyList<ReferenceMaterialPayload>>(
                input.MaterialIds.Select(CreateMaterialPayload).ToArray());
        }

        public ValueTask<AdaptReferenceMaterialResultPayload> AdaptMaterialAsync(
            AdaptReferenceMaterialPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"AdaptMaterial:{input.NovelId}:{input.MaterialId}:{FormatSlots(input.SlotValues)}:{input.MaxRewriteLevel}:{string.Join(',', input.SceneFacts)}");
            return ValueTask.FromResult(AdaptMaterialResult!);
        }

        public ValueTask<ReferenceReuseAuditPayload> AuditCandidateAsync(
            AuditReferenceReusePayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"AuditCandidate:{input.NovelId}:{input.MaterialId}:{input.CandidateText}:{input.MaxRewriteLevel}:{string.Join(',', input.SceneFacts)}");
            return ValueTask.FromResult(ReuseAuditResult ?? new ReferenceReuseAuditPayload(
                "audit-1",
                "passed",
                ReferenceRewriteLevels.L1,
                [],
                [],
                [],
                [],
                [],
                DateTimeOffset.UtcNow));
        }

        public ValueTask<ReferenceUserFeedbackPayload> RecordUserFeedbackAsync(
            RecordReferenceUserFeedbackPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"RecordUserFeedback:{input.NovelId}:{input.TargetType}:{input.TargetId}:{input.Decision}:{input.MaterialId}:{input.CandidateId}:{input.BlueprintId}:{input.BeatId}:{string.Join(',', input.FeedbackTags)}:{input.Note}:{input.EditedText}:{input.Origin}");
            return ValueTask.FromResult<ReferenceUserFeedbackPayload>(null!);
        }

        public ValueTask<IReadOnlyList<ReferenceUserFeedbackPayload>> GetUserFeedbackAsync(
            GetReferenceUserFeedbackPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetUserFeedback:{input.NovelId}:{input.TargetType}:{input.TargetId}:{input.Limit}");
            IReadOnlyList<ReferenceUserFeedbackPayload> feedback = [];
            return ValueTask.FromResult(feedback);
        }
    }

    private sealed class RecordingReferenceStyleProfileService : IReferenceStyleProfileService
    {
        public List<string> Calls { get; } = [];

        public ValueTask<ReferenceStyleProfilePayload> BuildStyleProfileAsync(
            BuildReferenceStyleProfilePayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"BuildStyleProfile:{input.NovelId}:{input.Title}:{input.Description}:{string.Join(',', input.AnchorIds)}:{string.Join(',', input.AllowedLicenseStatuses)}:{string.Join(',', input.AllowedSourceTrustLevels)}");
            return ValueTask.FromResult<ReferenceStyleProfilePayload>(null!);
        }

        public ValueTask<IReadOnlyList<ReferenceStyleProfileSummaryPayload>> GetStyleProfilesAsync(
            GetReferenceStyleProfilesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetStyleProfiles:{input.NovelId}:{input.IncludeArchived}");
            IReadOnlyList<ReferenceStyleProfileSummaryPayload> profiles = [];
            return ValueTask.FromResult(profiles);
        }

        public ValueTask<ReferenceStyleProfilePayload?> GetStyleProfileAsync(
            long novelId,
            long profileId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetStyleProfile:{novelId}:{profileId}");
            return ValueTask.FromResult<ReferenceStyleProfilePayload?>(null);
        }

        public ValueTask<ReferenceStyleProfileBuildStatusPayload?> GetStyleProfileBuildStatusAsync(
            GetReferenceStyleProfileBuildStatusPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetStyleProfileBuildStatus:{input.NovelId}:{input.BuildId}");
            return ValueTask.FromResult<ReferenceStyleProfileBuildStatusPayload?>(null);
        }

        public ValueTask<ReferenceStyleProfileBuildStatusPayload> CancelStyleProfileBuildAsync(
            CancelReferenceStyleProfileBuildPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"CancelStyleProfileBuild:{input.NovelId}:{input.BuildId}");
            return ValueTask.FromResult<ReferenceStyleProfileBuildStatusPayload>(null!);
        }

        public ValueTask<ReferenceStyleProfilePayload> ArchiveStyleProfileAsync(
            ArchiveReferenceStyleProfilePayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"ArchiveStyleProfile:{input.NovelId}:{input.ProfileId}");
            return ValueTask.FromResult<ReferenceStyleProfilePayload>(null!);
        }

        public ValueTask<ReferenceStyleProfilePayload> RestoreStyleProfileAsync(
            RestoreReferenceStyleProfilePayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"RestoreStyleProfile:{input.NovelId}:{input.ProfileId}");
            return ValueTask.FromResult<ReferenceStyleProfilePayload>(null!);
        }

        public ValueTask<ReferenceStyleProfileComparisonPayload> CompareStyleProfilesAsync(
            CompareReferenceStyleProfilesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"CompareStyleProfiles:{input.NovelId}:{input.LeftProfileId}:{input.RightProfileId}");
            return ValueTask.FromResult<ReferenceStyleProfileComparisonPayload>(null!);
        }
    }

    private sealed class RecordingReferenceAnchoredDraftService : IReferenceAnchoredDraftService
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<ReferenceDraftParagraphCandidatePayload> DraftCandidatesResult { get; set; } = [];

        public ValueTask<ReferenceChapterBlueprintPayload> GenerateChapterBlueprintAsync(
            GenerateReferenceChapterBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GenerateChapterBlueprint:{input.NovelId}:{input.ChapterNumber}:{input.Title}:{input.ChapterGoal}:{string.Join(',', input.AnchorIds)}:{string.Join(',', input.KnownFacts)}:{string.Join(',', input.ForbiddenFacts)}");
            return ValueTask.FromResult<ReferenceChapterBlueprintPayload>(null!);
        }

        public ValueTask<IReadOnlyList<ReferenceChapterBlueprintSummaryPayload>> GetChapterBlueprintsAsync(
            long novelId,
            int? chapterNumber,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetChapterBlueprints:{novelId}:{chapterNumber?.ToString() ?? "<null>"}");
            IReadOnlyList<ReferenceChapterBlueprintSummaryPayload> summaries = [];
            return ValueTask.FromResult(summaries);
        }

        public ValueTask<ReferenceChapterBlueprintPayload?> GetChapterBlueprintAsync(
            long novelId,
            long blueprintId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetChapterBlueprint:{novelId}:{blueprintId}");
            return ValueTask.FromResult<ReferenceChapterBlueprintPayload?>(null);
        }

        public ValueTask<ReferenceChapterBlueprintReviewPayload> ReviewChapterBlueprintAsync(
            ReviewReferenceChapterBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"ReviewChapterBlueprint:{input.NovelId}:{input.BlueprintId}");
            return ValueTask.FromResult<ReferenceChapterBlueprintReviewPayload>(null!);
        }

        public ValueTask<ReferenceChapterBlueprintPayload> ReviseChapterBlueprintAsync(
            ReviseReferenceChapterBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"ReviseChapterBlueprint:{input.NovelId}:{input.BlueprintId}:{FormatRevisionChanges(input.Changes)}:{input.Origin}:{input.RevisionReason}");
            return ValueTask.FromResult<ReferenceChapterBlueprintPayload>(null!);
        }

        public ValueTask<ReferenceChapterBlueprintPayload> ApproveChapterBlueprintAsync(
            ApproveReferenceChapterBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"ApproveChapterBlueprint:{input.NovelId}:{input.BlueprintId}:{input.ReviewId}");
            return ValueTask.FromResult<ReferenceChapterBlueprintPayload>(null!);
        }

        public ValueTask<ReferenceBlueprintMaterialBindingResultPayload> BindBlueprintMaterialsAsync(
            BindReferenceBlueprintMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"BindBlueprintMaterials:{input.NovelId}:{input.BlueprintId}:{input.MaxResultsPerBeat}:{input.SelectTopCandidate}");
            return ValueTask.FromResult<ReferenceBlueprintMaterialBindingResultPayload>(null!);
        }

        public ValueTask<ReferenceAnchoredDraftPayload> GenerateDraftFromBlueprintAsync(
            GenerateReferenceAnchoredDraftPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GenerateDraftFromBlueprint:{input.NovelId}:{input.BlueprintId}:{string.Join(',', input.BeatIds)}");
            return ValueTask.FromResult<ReferenceAnchoredDraftPayload>(null!);
        }

        public ValueTask<IReadOnlyList<ReferenceDraftParagraphCandidatePayload>> GetDraftCandidatesAsync(
            GetReferenceDraftCandidatesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetDraftCandidates:{input.NovelId}:{input.BlueprintId}:{string.Join(',', input.CandidateIds)}");
            return ValueTask.FromResult(DraftCandidatesResult);
        }

        public ValueTask<ReferenceAnchoredDraftAuditPayload> AuditDraftAgainstBlueprintAsync(
            AuditReferenceAnchoredDraftPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"AuditDraftAgainstBlueprint:{input.NovelId}:{input.BlueprintId}:{string.Join(',', input.CandidateIds)}");
            return ValueTask.FromResult<ReferenceAnchoredDraftAuditPayload>(null!);
        }

        public ValueTask<IReadOnlyList<ReferenceAnchoredDraftAuditPayload>> GetDraftAuditsAsync(
            GetReferenceAnchoredDraftAuditsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetDraftAudits:{input.NovelId}:{input.BlueprintId}:{string.Join(',', input.CandidateIds ?? [])}:{input.Limit}");
            IReadOnlyList<ReferenceAnchoredDraftAuditPayload> audits = [];
            return ValueTask.FromResult(audits);
        }

        public ValueTask<IReadOnlyList<ReferenceStyleAuditFindingPayload>> GetStyleAuditFindingsAsync(
            GetReferenceStyleAuditFindingsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetStyleAuditFindings:{input.NovelId}:{input.BlueprintId}:{string.Join(',', input.CandidateIds ?? [])}:{string.Join(',', input.RiskTypes ?? [])}:{input.Limit}");
            IReadOnlyList<ReferenceStyleAuditFindingPayload> findings = [];
            return ValueTask.FromResult(findings);
        }

        public ValueTask<ReferenceOrchestrationRunPayload> StartOrchestrationRunAsync(
            StartReferenceOrchestrationRunPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                $"StartOrchestrationRun:{input.NovelId}:{input.ChapterNumber}:{input.ChapterGoal}:{string.Join(',', input.KnownFacts)}:{string.Join(',', input.ForbiddenFacts)}:{FormatNullableLongs(input.AnchorIds)}:{input.CorpusSearchPolicy.Mode}:{input.CorpusSearchPolicy.MaxResultsPerBeat}:{string.Join(',', input.CorpusSearchPolicy.LicenseStatuses)}:{string.Join(',', input.CorpusSearchPolicy.IncludeAnchorIds)}:{string.Join(',', input.CorpusSearchPolicy.ExcludeAnchorIds)}:<{input.SourceConfirmed.ToString().ToLowerInvariant()}>:{FormatStylePolicy(input.StylePolicy)}");
            return ValueTask.FromResult<ReferenceOrchestrationRunPayload>(null!);
        }

        public ValueTask<IReadOnlyList<ReferenceOrchestrationRunPayload>> GetOrchestrationRunsAsync(
            long novelId,
            int? chapterNumber,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetOrchestrationRuns:{novelId}:{chapterNumber?.ToString() ?? "<null>"}");
            IReadOnlyList<ReferenceOrchestrationRunPayload> runs = [];
            return ValueTask.FromResult(runs);
        }

        public ValueTask<ReferenceOrchestrationRunPayload?> GetOrchestrationRunAsync(
            long novelId,
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetOrchestrationRun:{novelId}:{runId}");
            return ValueTask.FromResult<ReferenceOrchestrationRunPayload?>(null);
        }

        public ValueTask<IReadOnlyList<ReferenceOrchestrationRunEventPayload>> GetOrchestrationRunEventsAsync(
            long novelId,
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetOrchestrationRunEvents:{novelId}:{runId}");
            IReadOnlyList<ReferenceOrchestrationRunEventPayload> events = [];
            return ValueTask.FromResult(events);
        }

        public ValueTask<ReferenceOrchestrationRunPayload> ResumeOrchestrationRunAsync(
            ResumeReferenceOrchestrationRunPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"ResumeOrchestrationRun:{input.NovelId}:{input.RunId}:{input.DecisionType}:{input.DecisionPayload}");
            return ValueTask.FromResult<ReferenceOrchestrationRunPayload>(null!);
        }

        public ValueTask<ReferenceOrchestrationRunPayload> CancelOrchestrationRunAsync(
            CancelReferenceOrchestrationRunPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"CancelOrchestrationRun:{input.NovelId}:{input.RunId}:{input.Reason}");
            return ValueTask.FromResult<ReferenceOrchestrationRunPayload>(null!);
        }
    }

    private static string FormatSlots(IReadOnlyList<ReferenceSlotValuePayload> slots)
    {
        return string.Join(',', slots.Select(slot => $"{slot.SlotName}={slot.Value}"));
    }

    private static string FormatRevisionChanges(IReadOnlyList<ReferenceBlueprintRevisionChangePayload> changes)
    {
        return string.Join(',', changes.Select(change => $"{change.FieldPath}={change.NewValue}"));
    }

    private static string FormatNullableLongs(IReadOnlyList<long>? values)
    {
        return values is null ? "<null>" : string.Join(',', values);
    }

    private static string FormatStylePolicy(ReferenceOrchestrationStylePolicyPayload? stylePolicy)
    {
        return stylePolicy is null
            ? "<null>"
            : string.Join(':',
                string.Join(',', stylePolicy.StyleProfileIds),
                string.Join(',', stylePolicy.StyleDimensions),
                stylePolicy.ImitationIntensity,
                stylePolicy.MinStyleFit.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                stylePolicy.AllowedCloseness,
                string.Join(',', stylePolicy.RequiredEvidenceTypes),
                string.Join(',', stylePolicy.ForbiddenStyleRisks));
    }
}
