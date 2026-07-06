using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Agent;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class MafStructuredToolIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StructuredToolsCallRealDomainServicesThroughMafExecutor()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "旧城疑案", "悬疑"), CancellationToken.None);
        var chapters = new FileSystemChapterContentService(options, novelService);
        await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        var preferences = new FileSystemPreferenceService(options, novelService);
        var world = new FileSystemWorldEntityService(options, novelService);
        var planning = new FileSystemPlanningService(options, novelService);
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            chapters,
            approvals: null,
            events: null,
            subagents: null,
            preferences,
            world,
            planning));

        var names = executor.GetToolDefinitions(novel.Id).Select(tool => tool.Name).ToArray();
        Assert.Contains("create_character", names);
        Assert.Contains("get_timeline", names);
        Assert.Contains("delete_record", names);

        var createCharacters = await ExecuteAsync(
            executor,
            novel.Id,
            "create_character",
            """
            {"characters":[{"name":"林岚","description":"旧城记者","personality":"{\"role\":\"主角\"}","abilities":"[\"追踪\"]"},{"name":"阿七","description":"线人","abilities":"[]"}]}
            """);
        var characterIds = createCharacters.GetProperty("ids").EnumerateArray().Select(item => item.GetInt64()).ToArray();
        Assert.Equal(2, characterIds.Length);

        var relation = await ExecuteAsync(
            executor,
            novel.Id,
            "update_character_relationship",
            $$"""
            {"source_character_id":{{characterIds[0]}},"target_character_id":{{characterIds[1]}},"relation_describe":"互相试探","description":"交换情报","chapter_id":1}
            """);
        Assert.Equal("evolve", relation.GetProperty("action").GetString());

        var characterRelations = await world.GetCharacterRelationsAsync(novel.Id, CancellationToken.None);
        Assert.Single(characterRelations);
        Assert.Equal("互相试探", characterRelations[0].RelationDescribe);

        var createTimeline = await ExecuteAsync(
            executor,
            novel.Id,
            "create_timeline_entry",
            """
            {"entries":[{"category":"foreshadowing","title":"旧城门暗号","content":"林岚听到暗号","target_chapter":3,"importance":4}]}
            """);
        var timelineId = Assert.Single(createTimeline.GetProperty("ids").EnumerateArray()).GetInt64();

        var timeline = await ExecuteAsync(
            executor,
            novel.Id,
            "get_timeline",
            """{"current_chapter":2}""");
        Assert.Contains("旧城门暗号", timeline.GetProperty("content").GetString(), StringComparison.Ordinal);

        var updateTimeline = await ExecuteAsync(
            executor,
            novel.Id,
            "update_timeline_entry",
            $$"""{"entry_id":{{timelineId}},"status":"resolved","resolved_chapter_id":1}""");
        Assert.Equal(timelineId, updateTimeline.GetProperty("id").GetInt64());

        var createPreference = await ExecuteAsync(
            executor,
            novel.Id,
            "create_preference",
            """
            {"preferences":[{"category":"风格","content":"保持冷峻克制。","is_global":false}]}
            """);
        Assert.Equal(1, createPreference.GetProperty("count").GetInt32());

        var getPreferences = await ExecuteAsync(executor, novel.Id, "get_preferences", "{}");
        Assert.Contains("保持冷峻克制", getPreferences.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteRecordRequestsApprovalAndRejectsImpactedRecords()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "旧城疑案", "悬疑"), CancellationToken.None);
        var chapters = new FileSystemChapterContentService(options, novelService);
        var preferences = new FileSystemPreferenceService(options, novelService);
        var world = new FileSystemWorldEntityService(options, novelService);
        var planning = new FileSystemPlanningService(options, novelService);
        var events = new RecordingBridgeEventSink();
        var approvals = new ToolApprovalCoordinator(events);
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            chapters,
            approvals,
            events,
            subagents: null,
            preferences,
            world,
            planning));

        var character = await world.CreateCharacterAsync(
            novel.Id,
            new CreateCharacterPayload("林岚", "记者", "", "[]"),
            CancellationToken.None);
        var target = await world.CreateCharacterAsync(
            novel.Id,
            new CreateCharacterPayload("阿七", "线人", "", "[]"),
            CancellationToken.None);
        var relation = await world.UpdateCharacterRelationshipAsync(
            novel.Id,
            new UpdateCharacterRelationshipPayload(
                SourceCharacterId: character.Id,
                TargetCharacterId: target.Id,
                RelationDescribe: "互相试探"),
            CancellationToken.None);

        var impacted = await executor.ExecuteAsync(
            new ChatToolExecutionContext(novel.Id, "sess_delete", 5),
            new ChatToolCall("call_delete_impacted", "delete_record", $$"""{"table":"character","id":{{character.Id}}}"""),
            CancellationToken.None);
        Assert.False(impacted.Success);
        Assert.Contains("角色关系", impacted.Error, StringComparison.Ordinal);
        Assert.Equal(0, approvals.PendingCount);

        var deleteTask = executor.ExecuteAsync(
            new ChatToolExecutionContext(novel.Id, "sess_delete", 5),
            new ChatToolCall("call_delete_relation", "delete_record", $$"""{"table":"character_relation","id":{{relation.Id}}}"""),
            CancellationToken.None).AsTask();

        var approvalEvent = await events.WaitForEventAsync(
            "agent:5",
            item => item.Payload.TryGetProperty("phase", out var phase) &&
                phase.GetString() == "awaiting_approval",
            TimeSpan.FromSeconds(3));
        Assert.Equal("delete_record", approvalEvent.Payload.GetProperty("tool_name").GetString());
        Assert.Equal("delete", approvalEvent.Payload.GetProperty("metadata").GetProperty("approval_type").GetString());
        Assert.Equal("character_relation", approvalEvent.Payload.GetProperty("metadata").GetProperty("payload").GetProperty("table").GetString());

        await approvals.CompleteAsync(new ToolApprovalDecisionPayload("call_delete_relation", true, "确认删除"), CancellationToken.None);
        var deleted = await deleteTask;

        Assert.True(deleted.Success, deleted.Error);
        Assert.Empty(await world.GetAllCharacterRelationsAsync(novel.Id, CancellationToken.None));
        Assert.Equal("确认删除", deleted.Data!.Value.GetProperty("feedback").GetString());
    }

    [Fact]
    public async Task ReferenceDraftToolsCallRealServicesThroughMafExecutor()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("锚定工具测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novelService);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novelService);
        var sourcePath = CreateSourceFile(
            "maf-reference-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。

            他在门口停了很久。
            """);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "雨夜锚定参考",
                null,
                sourcePath,
                "markdown",
                "user_provided"),
            CancellationToken.None);
        var referenceDrafts = new SqliteReferenceAnchoredDraftService(options, novelService, planning, referenceAnchors);
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning,
            webFetch: null,
            webSearch: null,
            referenceAnchors,
            referenceDrafts));

        var names = executor.GetToolDefinitions(novel.Id).Select(tool => tool.Name).ToArray();
        Assert.Contains("generate_reference_chapter_blueprint", names);
        Assert.Contains("review_reference_chapter_blueprint", names);
        Assert.Contains("approve_reference_chapter_blueprint", names);
        Assert.DoesNotContain(
            executor.GetToolDefinitions(novel.Id).Single(tool => tool.Name == "generate_reference_chapter_blueprint").ParametersSchema.GetProperty("properties").EnumerateObject(),
            property => property.Name == "novel_id");

        var blueprint = await ExecuteAsync(
            executor,
            novel.Id,
            "generate_reference_chapter_blueprint",
            $$"""
            {"chapter_number":4,"title":"第四章蓝图","chapter_goal":"雨声压低了整条街的呼吸","known_facts":["雨声压低了整条街的呼吸","主角在门口"],"forbidden_facts":["凶手身份"],"anchor_ids":[{{anchor.AnchorId}}]}
            """);
        var blueprintId = blueprint.GetProperty("blueprint_id").GetInt64();
        Assert.Equal("reference", blueprint.GetProperty("reference_analysis").GetProperty("track").GetString());
        Assert.Equal(novel.Id, blueprint.GetProperty("novel_id").GetInt64());

        var review = await ExecuteAsync(
            executor,
            novel.Id,
            "review_reference_chapter_blueprint",
            $$"""{"blueprint_id":{{blueprintId}}}""");
        Assert.Equal("passed", review.GetProperty("status").GetString());

        var approved = await ExecuteAsync(
            executor,
            novel.Id,
            "approve_reference_chapter_blueprint",
            $$"""{"blueprint_id":{{blueprintId}},"review_id":{{JsonSerializer.Serialize(review.GetProperty("review_id").GetString())}}}""");
        Assert.Equal("approved", approved.GetProperty("status").GetString());

        var binding = await ExecuteAsync(
            executor,
            novel.Id,
            "bind_reference_blueprint_materials",
            $$"""{"blueprint_id":{{blueprintId}},"max_results_per_beat":3,"select_top_candidate":true}""");
        var selectedLink = Assert.Single(
            binding.GetProperty("links").EnumerateArray(),
            link => link.GetProperty("selected").GetBoolean());
        Assert.Equal(blueprintId, selectedLink.GetProperty("blueprint_id").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(selectedLink.GetProperty("material_id").GetString()));

        var draft = await ExecuteAsync(
            executor,
            novel.Id,
            "generate_reference_anchored_draft",
            $$"""{"blueprint_id":{{blueprintId}},"beat_ids":[]}""");
        Assert.Equal(blueprintId, draft.GetProperty("blueprint_id").GetInt64());
        var candidate = Assert.Single(draft.GetProperty("candidates").EnumerateArray());
        var candidateId = candidate.GetProperty("candidate_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(candidateId));
        Assert.Equal(selectedLink.GetProperty("material_id").GetString(), candidate.GetProperty("material_id").GetString());
        Assert.Equal("passed", draft.GetProperty("audit").GetProperty("status").GetString());

        var audit = await ExecuteAsync(
            executor,
            novel.Id,
            "audit_reference_anchored_draft",
            $$"""{"blueprint_id":{{blueprintId}},"candidate_ids":[{{JsonSerializer.Serialize(candidateId)}}]}""");
        Assert.Equal("passed", audit.GetProperty("status").GetString());
        Assert.Empty(audit.GetProperty("provenance_errors").EnumerateArray());
        Assert.Empty(audit.GetProperty("blueprint_errors").EnumerateArray());

        var revised = await ExecuteAsync(
            executor,
            novel.Id,
            "revise_reference_chapter_blueprint",
            $$"""
            {"blueprint_id":{{blueprintId}},"changes":[{"field_path":"beat:{{blueprint.GetProperty("beats")[0].GetProperty("beat_id").GetString()}}:paragraph_intention","new_value":"linger on rain pressure before action"}],"origin":"agent","revision_reason":"prove approval invalidation through MAF"}
            """);
        Assert.Equal("draft", revised.GetProperty("status").GetString());

        var rejectedDraft = await ExecuteFailureAsync(
            executor,
            novel.Id,
            "generate_reference_anchored_draft",
            $$"""{"blueprint_id":{{blueprintId}},"beat_ids":[]}""");
        Assert.Contains("approved", rejectedDraft, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReferenceMaterialToolCannotBypassWorkspaceCorpusVisibilityWithExplicitAnchorIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("MAF 共享语料可见性", "", ""), CancellationToken.None);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novelService);
        var visibleSourcePath = CreateSourceFile("maf-visible-workspace.md", "雨声压低街道，主角在门口停住。");
        var privateSourcePath = CreateSourceFile("maf-private-workspace.md", "私有共享语料不应被 agent 显式 id 读到。");
        var visibleAnchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "MAF 可见共享参考", null, visibleSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var privateAnchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "MAF 私有共享参考", null, privateSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, visibleAnchor.AnchorId, ReferenceCorpusVisibilities.Workspace);
        await MarkAnchorAsWorkspaceCorpusAsync(options, privateAnchor.AnchorId, ReferenceCorpusVisibilities.Private);
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors));

        var defaultSearch = await ExecuteAsync(
            executor,
            novel.Id,
            "search_reference_materials",
            """{"query":"门口","material_types":["sentence"],"page":1,"size":10}""");
        var defaultItems = defaultSearch.GetProperty("items").EnumerateArray().ToArray();
        var defaultItem = Assert.Single(defaultItems);
        Assert.Equal(visibleAnchor.AnchorId, defaultItem.GetProperty("anchor_id").GetInt64());

        var explicitPrivateSearch = await ExecuteAsync(
            executor,
            novel.Id,
            "search_reference_materials",
            $$"""{"anchor_ids":[{{privateAnchor.AnchorId}}],"query":"私有共享语料","material_types":["sentence"],"page":1,"size":10}""");
        Assert.Empty(explicitPrivateSearch.GetProperty("items").EnumerateArray());

        var explicitVisibleSearch = await ExecuteAsync(
            executor,
            novel.Id,
            "search_reference_materials",
            $$"""{"anchor_ids":[{{visibleAnchor.AnchorId}}],"query":"门口","material_types":["sentence"],"page":1,"size":10}""");
        var visibleItem = Assert.Single(explicitVisibleSearch.GetProperty("items").EnumerateArray());
        Assert.Equal(visibleAnchor.AnchorId, visibleItem.GetProperty("anchor_id").GetInt64());
    }

    [Fact]
    public async Task ReferenceOrchestrationAgentToolDefaultsToWorkspaceCorpusWithoutAnchorIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("MAF 默认共享语料编排", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novelService);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "雨声压低街道，主角在门口停住。"),
            CancellationToken.None);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novelService);
        var sourcePath = CreateSourceFile(
            "maf-orchestration-workspace.md",
            """
            # 第一章

            雨声压低街道，主角在门口停住，心里意识到压力仍然压着呼吸。
            """);
        var workspaceAnchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "MAF 编排共享参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        await MarkAnchorAsWorkspaceCorpusAsync(options, workspaceAnchor.AnchorId, ReferenceCorpusVisibilities.Workspace);
        var referenceDrafts = new SqliteReferenceAnchoredDraftService(options, novelService, planning, referenceAnchors);
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning,
            webFetch: null,
            webSearch: null,
            referenceAnchors,
            referenceDrafts));
        var startTool = executor.GetToolDefinitions(novel.Id).Single(tool => tool.Name == "start_reference_orchestration_run");
        var startProperties = startTool.ParametersSchema.GetProperty("properties");
        Assert.False(startProperties.TryGetProperty("anchor_ids", out _));
        Assert.True(startProperties.TryGetProperty("include_anchor_ids", out _));
        Assert.True(startProperties.TryGetProperty("exclude_anchor_ids", out _));

        var started = await ExecuteAsync(
            executor,
            novel.Id,
            "start_reference_orchestration_run",
            """
            {"chapter_number":7,"chapter_goal":"雨声压低街道，主角在门口停住","known_facts":["雨声压低街道","主角在门口停住"],"forbidden_facts":["凶手身份"],"license_statuses":["user_provided"],"max_results_per_beat":3}
            """);

        Assert.Empty(started.GetProperty("anchor_ids").EnumerateArray());
        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, started.GetProperty("status").GetString());
        Assert.Equal(ReferenceOrchestrationStages.SourceConfirmation, started.GetProperty("stage").GetString());
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts, started.GetProperty("current_decision").GetProperty("decision_type").GetString());
        Assert.Equal("story_context", started.GetProperty("corpus_search_policy").GetProperty("mode").GetString());

        var runId = started.GetProperty("run_id").GetString() ?? throw new InvalidOperationException("Expected run id.");
        var completedSafeStages = await referenceDrafts.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                runId,
                ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts,
                "user confirmed source and fact boundary"),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, completedSafeStages.Status);
        Assert.Equal(ReferenceOrchestrationStages.BlueprintApproval, completedSafeStages.Stage);
        Assert.Empty(completedSafeStages.AnchorIds);

        var finished = await referenceDrafts.ResumeOrchestrationRunAsync(
            new ResumeReferenceOrchestrationRunPayload(
                novel.Id,
                runId,
                ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                completedSafeStages.ReviewId),
            CancellationToken.None);

        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, finished.Status);
        Assert.Equal(ReferenceOrchestrationStages.FinalInsertion, finished.Stage);
        Assert.NotEmpty(finished.CandidateIds);
    }

    [Fact]
    public async Task ReferenceDraftAuditReportsForbiddenFactThroughMafExecutor()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("锚定审计工具测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novelService);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novelService);
        var sourcePath = CreateSourceFile(
            "maf-forbidden-reference-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸，凶手身份在门后闪了一下。
            """);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "禁止事实锚定参考",
                null,
                sourcePath,
                "markdown",
                "user_provided"),
            CancellationToken.None);
        var referenceDrafts = new SqliteReferenceAnchoredDraftService(options, novelService, planning, referenceAnchors);
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning,
            webFetch: null,
            webSearch: null,
            referenceAnchors,
            referenceDrafts));

        var blueprint = await ExecuteAsync(
            executor,
            novel.Id,
            "generate_reference_chapter_blueprint",
            $$"""
            {"chapter_number":5,"title":"第五章蓝图","chapter_goal":"雨声压低了整条街的呼吸","known_facts":["雨声压低了整条街的呼吸"],"forbidden_facts":["凶手身份"],"anchor_ids":[{{anchor.AnchorId}}]}
            """);
        var blueprintId = blueprint.GetProperty("blueprint_id").GetInt64();

        var review = await ExecuteAsync(
            executor,
            novel.Id,
            "review_reference_chapter_blueprint",
            $$"""{"blueprint_id":{{blueprintId}}}""");
        Assert.Equal("passed", review.GetProperty("status").GetString());

        await ExecuteAsync(
            executor,
            novel.Id,
            "approve_reference_chapter_blueprint",
            $$"""{"blueprint_id":{{blueprintId}},"review_id":{{JsonSerializer.Serialize(review.GetProperty("review_id").GetString())}}}""");
        await ExecuteAsync(
            executor,
            novel.Id,
            "bind_reference_blueprint_materials",
            $$"""{"blueprint_id":{{blueprintId}},"max_results_per_beat":3,"select_top_candidate":true}""");

        var draft = await ExecuteAsync(
            executor,
            novel.Id,
            "generate_reference_anchored_draft",
            $$"""{"blueprint_id":{{blueprintId}},"beat_ids":[]}""");
        var candidate = Assert.Single(draft.GetProperty("candidates").EnumerateArray());
        var candidateId = candidate.GetProperty("candidate_id").GetString();
        Assert.Contains("凶手身份", candidate.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("failed", draft.GetProperty("audit").GetProperty("status").GetString());
        Assert.Contains(
            draft.GetProperty("audit").GetProperty("unsupported_fact_errors").EnumerateArray(),
            item => item.GetString()?.Contains("凶手身份", StringComparison.Ordinal) == true);

        var audit = await ExecuteAsync(
            executor,
            novel.Id,
            "audit_reference_anchored_draft",
            $$"""{"blueprint_id":{{blueprintId}},"candidate_ids":[{{JsonSerializer.Serialize(candidateId)}}]}""");
        Assert.Equal("failed", audit.GetProperty("status").GetString());
        Assert.Contains(
            audit.GetProperty("unsupported_fact_errors").EnumerateArray(),
            item => item.GetString()?.Contains("凶手身份", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ReferenceDraftToolsRejectStaleBlueprintThroughMafExecutor()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("锚定失效工具测试", "", ""), CancellationToken.None);
        var planning = new FileSystemPlanningService(options, novelService);
        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角先在雨夜门口等待。"),
            CancellationToken.None);
        var referenceAnchors = new SqliteReferenceAnchorService(options, novelService);
        var sourcePath = CreateSourceFile(
            "maf-stale-reference-anchor.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。
            """);
        var anchor = await referenceAnchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "失效锚定参考",
                null,
                sourcePath,
                "markdown",
                "user_provided"),
            CancellationToken.None);
        var referenceDrafts = new SqliteReferenceAnchoredDraftService(options, novelService, planning, referenceAnchors);
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning,
            webFetch: null,
            webSearch: null,
            referenceAnchors,
            referenceDrafts));

        var blueprint = await ExecuteAsync(
            executor,
            novel.Id,
            "generate_reference_chapter_blueprint",
            $$"""
            {"chapter_number":6,"title":"第六章蓝图","chapter_goal":"雨夜等待","known_facts":["主角在门口"],"forbidden_facts":[],"anchor_ids":[{{anchor.AnchorId}}]}
            """);
        var blueprintId = blueprint.GetProperty("blueprint_id").GetInt64();
        var review = await ExecuteAsync(
            executor,
            novel.Id,
            "review_reference_chapter_blueprint",
            $$"""{"blueprint_id":{{blueprintId}}}""");
        await ExecuteAsync(
            executor,
            novel.Id,
            "approve_reference_chapter_blueprint",
            $$"""{"blueprint_id":{{blueprintId}},"review_id":{{JsonSerializer.Serialize(review.GetProperty("review_id").GetString())}}}""");

        await planning.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "主角改为直接进入屋内。"),
            CancellationToken.None);

        var rejectedBinding = await ExecuteFailureAsync(
            executor,
            novel.Id,
            "bind_reference_blueprint_materials",
            $$"""{"blueprint_id":{{blueprintId}},"max_results_per_beat":3}""");
        Assert.Contains("stale", rejectedBinding, StringComparison.OrdinalIgnoreCase);

        var rejectedDraft = await ExecuteFailureAsync(
            executor,
            novel.Id,
            "generate_reference_anchored_draft",
            $$"""{"blueprint_id":{{blueprintId}},"beat_ids":[]}""");
        Assert.Contains("stale", rejectedDraft, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async ValueTask<JsonElement> ExecuteAsync(
        NovelistMafChatToolExecutor executor,
        long novelId,
        string name,
        string argumentsJson)
    {
        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(novelId, "sess_structured", 1),
            new ChatToolCall($"call_{name}_{Guid.NewGuid():N}", name, argumentsJson),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Data);
        return result.Data.Value;
    }

    private async ValueTask<string> ExecuteFailureAsync(
        NovelistMafChatToolExecutor executor,
        long novelId,
        string name,
        string argumentsJson)
    {
        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(novelId, "sess_structured", 1),
            new ChatToolCall($"call_{name}_{Guid.NewGuid():N}", name, argumentsJson),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        return result.Error;
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

    private static async ValueTask MarkAnchorAsWorkspaceCorpusAsync(
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
            SET novel_id = 0,
                corpus_visibility = $corpus_visibility
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$corpus_visibility", visibility);
        var updated = await command.ExecuteNonQueryAsync();
        Assert.Equal(1, updated);
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private sealed class EmptyStoryMemorySearchService : IStoryMemorySearchService
    {
        public ValueTask<SearchStoryMemoryResultPayload> SearchAsync(SearchStoryMemoryPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new SearchStoryMemoryResultPayload(input.Query, 0, "empty", "0", string.Empty, []));
        }
    }

    private sealed record RecordedBridgeEvent(string Name, JsonElement Payload);

    private sealed class RecordingBridgeEventSink : IBridgeEventSink
    {
        private readonly List<RecordedBridgeEvent> _events = [];

        public IReadOnlyList<RecordedBridgeEvent> Events => _events;

        public ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken)
        {
            _events.Add(new RecordedBridgeEvent(name, JsonSerializer.SerializeToElement(payload ?? new { }, BridgeJson.SerializerOptions)));
            return ValueTask.CompletedTask;
        }

        public async ValueTask<RecordedBridgeEvent> WaitForEventAsync(
            string name,
            Func<RecordedBridgeEvent, bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var match = _events.FirstOrDefault(item => item.Name == name && predicate(item));
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException($"Timed out waiting for event '{name}'.");
        }
    }
}
