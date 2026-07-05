using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests.Bridge;

public sealed class ReferenceBridgeHandlerRoutingTests
{
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
            "user_provided"));
        await AssertOkAsync(dispatcher, "GetReferenceAnchors", 42L);
        await AssertOkAsync(dispatcher, "DeleteReferenceAnchor", 42L, 99L);
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
            10));
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
                @"CreateAnchor:42:Anchor:Author:D:\reference.md:markdown:user_provided",
                "GetAnchors:42",
                "DeleteAnchor:42:99",
                "RebuildAnchor:42:99",
                "GetBuildStatus:42:99",
                "SearchMaterials:42:99:fog:passage:unease:interiority:close:afterbeat:2:10",
                "UpdateMaterialTags:42:material-1:interiority:unease:threshold:close:afterbeat:user:verified",
                "AdaptMaterial:42:material-1:object=door:L2:door exists",
                "AuditCandidate:42:material-1:candidate text:L2:door exists",
                "RecordUserFeedback:42:reuse_candidate:candidate-1:edited:material-1:candidate-1:501:beat-1:too_ai_flavored:kept pressure image:edited text:user",
                "GetUserFeedback:42:reuse_candidate:candidate-1:5"
            ],
            service.Calls);
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
        await AssertOkAsync(dispatcher, "AuditReferenceAnchoredDraft", new AuditReferenceAnchoredDraftPayload(42, 501, ["candidate-1"]));
        await AssertOkAsync(dispatcher, "StartReferenceOrchestrationRun", new StartReferenceOrchestrationRunPayload(
            42,
            7,
            "tighten the reveal",
            ["known clue"],
            ["culprit identity"],
            null,
            new ReferenceCorpusSearchPolicyPayload("story_context", 3, ["user_provided"], [99], []),
            SourceConfirmed: false));
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
                "AuditDraftAgainstBlueprint:42:501:candidate-1",
                "StartOrchestrationRun:42:7:tighten the reveal:known clue:culprit identity:<null>:story_context:3:user_provided:99::<false>",
                "GetOrchestrationRuns:42:7",
                "GetOrchestrationRun:42:run-1",
                "GetOrchestrationRunEvents:42:run-1",
                "ResumeOrchestrationRun:42:run-1:approve_blueprint:review-1",
                "CancelOrchestrationRun:42:run-1:user cancelled"
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
        var result = await dispatcher.DispatchAsync(Request(method, args));
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));

        using var json = JsonDocument.Parse(result.OutboundJson);
        Assert.Equal("response", json.RootElement.GetProperty("kind").GetString());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
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

            Calls.Add($"CreateAnchor:{input.NovelId}:{input.Title}:{input.Author}:{input.SourcePath}:{input.SourceKind}:{input.LicenseStatus}");
            return ValueTask.FromResult<ReferenceAnchorPayload>(null!);
        }

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
            long novelId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetAnchors:{novelId}");
            IReadOnlyList<ReferenceAnchorPayload> anchors = [];
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

        public ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"RebuildAnchor:{novelId}:{anchorId}");
            return ValueTask.FromResult<ReferenceAnchorBuildStatusPayload>(null!);
        }

        public ValueTask<ReferenceAnchorBuildStatusPayload?> GetBuildStatusAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"GetBuildStatus:{novelId}:{anchorId}");
            return ValueTask.FromResult<ReferenceAnchorBuildStatusPayload?>(null);
        }

        public ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(
            SearchReferenceMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                $"SearchMaterials:{input.NovelId}:{string.Join(',', input.AnchorIds)}:{input.Query}:{string.Join(',', input.MaterialTypes)}:{string.Join(',', input.EmotionTags)}:{string.Join(',', input.FunctionTags)}:{string.Join(',', input.PovTags)}:{string.Join(',', input.TechniqueTags)}:{input.Page}:{input.Size}");
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialPayload>([], 0, input.Page, input.Size, 0));
        }

        public ValueTask<ReferenceMaterialPayload> UpdateMaterialTagsAsync(
            UpdateReferenceMaterialTagsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"UpdateMaterialTags:{input.NovelId}:{input.MaterialId}:{input.FunctionTag}:{input.EmotionTag}:{input.SceneTag}:{input.PovTag}:{input.TechniqueTag}:{input.Origin}:{input.Note}");
            return ValueTask.FromResult<ReferenceMaterialPayload>(null!);
        }

        public ValueTask<AdaptReferenceMaterialResultPayload> AdaptMaterialAsync(
            AdaptReferenceMaterialPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"AdaptMaterial:{input.NovelId}:{input.MaterialId}:{FormatSlots(input.SlotValues)}:{input.MaxRewriteLevel}:{string.Join(',', input.SceneFacts)}");
            return ValueTask.FromResult<AdaptReferenceMaterialResultPayload>(null!);
        }

        public ValueTask<ReferenceReuseAuditPayload> AuditCandidateAsync(
            AuditReferenceReusePayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"AuditCandidate:{input.NovelId}:{input.MaterialId}:{input.CandidateText}:{input.MaxRewriteLevel}:{string.Join(',', input.SceneFacts)}");
            return ValueTask.FromResult<ReferenceReuseAuditPayload>(null!);
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

    private sealed class RecordingReferenceAnchoredDraftService : IReferenceAnchoredDraftService
    {
        public List<string> Calls { get; } = [];

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

        public ValueTask<ReferenceAnchoredDraftAuditPayload> AuditDraftAgainstBlueprintAsync(
            AuditReferenceAnchoredDraftPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add($"AuditDraftAgainstBlueprint:{input.NovelId}:{input.BlueprintId}:{string.Join(',', input.CandidateIds)}");
            return ValueTask.FromResult<ReferenceAnchoredDraftAuditPayload>(null!);
        }

        public ValueTask<ReferenceOrchestrationRunPayload> StartOrchestrationRunAsync(
            StartReferenceOrchestrationRunPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(
                $"StartOrchestrationRun:{input.NovelId}:{input.ChapterNumber}:{input.ChapterGoal}:{string.Join(',', input.KnownFacts)}:{string.Join(',', input.ForbiddenFacts)}:{FormatNullableLongs(input.AnchorIds)}:{input.CorpusSearchPolicy.Mode}:{input.CorpusSearchPolicy.MaxResultsPerBeat}:{string.Join(',', input.CorpusSearchPolicy.LicenseStatuses)}:{string.Join(',', input.CorpusSearchPolicy.IncludeAnchorIds)}:{string.Join(',', input.CorpusSearchPolicy.ExcludeAnchorIds)}:<{input.SourceConfirmed.ToString().ToLowerInvariant()}>");
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
}
