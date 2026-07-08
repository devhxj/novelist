using System.Text.Json;
using Microsoft.Extensions.AI;
using Novelist.Agent;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests;

public sealed class MafToolRegistryTests
{
    [Fact]
    public void CreateToolsIncludesSearchStoryMemoryWithFlatSchema()
    {
        var registry = new NovelistMafToolRegistry(new RecordingStoryMemorySearchService());

        var function = Assert.Single(registry.CreateTools(new NovelistMafToolContext(17)));

        Assert.Equal("search_story_memory", function.Name);
        Assert.Contains("语义检索小说记忆", function.Description, StringComparison.Ordinal);
        Assert.True(function.JsonSchema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("query", out _));
        Assert.True(properties.TryGetProperty("top_k", out _));
        Assert.True(properties.TryGetProperty("min_relevance", out _));
        Assert.True(properties.TryGetProperty("chapter_numbers", out _));
        Assert.True(properties.TryGetProperty("chunk_types", out _));
        Assert.False(properties.TryGetProperty("novel_id", out _));
        Assert.False(properties.TryGetProperty("input", out _));
    }

    [Fact]
    public async Task SearchStoryMemoryFunctionInvokesServiceWithNovelContext()
    {
        var memory = new RecordingStoryMemorySearchService();
        var registry = new NovelistMafToolRegistry(memory);
        var function = Assert.Single(registry.CreateTools(new NovelistMafToolContext(42)));

        var raw = await function.InvokeAsync(
            new AIFunctionArguments
            {
                ["query"] = "旧城门暗号",
                ["top_k"] = 3,
                ["min_relevance"] = 0.6,
                ["chapter_numbers"] = new[] { 1, 3 },
                ["chunk_types"] = new[] { "content" }
            },
            CancellationToken.None);

        Assert.NotNull(memory.LastInput);
        var input = memory.LastInput;
        Assert.Equal(42, input.NovelId);
        Assert.Equal("旧城门暗号", input.Query);
        Assert.Equal(3, input.TopK);
        Assert.Equal(0.6, input.MinRelevance);
        Assert.Equal([1, 3], input.ChapterNumbers);
        Assert.Equal(["content"], input.ChunkTypes);

        var json = Assert.IsType<JsonElement>(raw);
        Assert.Equal("旧城门暗号", json.GetProperty("query").GetString());
        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Contains("林岚发现暗号", json.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatToolExecutorInvokesMafFunctionByName()
    {
        var memory = new RecordingStoryMemorySearchService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(memory));

        var definition = Assert.Single(executor.GetToolDefinitions(9));
        Assert.Equal("search_story_memory", definition.Name);
        Assert.True(definition.ParametersSchema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("query", out _));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(9, "sess_1", 1),
            new ChatToolCall(
                "call_1",
                "search_story_memory",
                """{"query":"旧城门暗号","top_k":2,"chapter_numbers":[1],"chunk_types":["content"]}"""),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(9, memory.LastInput!.NovelId);
        Assert.Equal("旧城门暗号", memory.LastInput.Query);
        Assert.Equal(2, memory.LastInput.TopK);
        Assert.Equal([1], memory.LastInput.ChapterNumbers);
        Assert.Equal(["content"], memory.LastInput.ChunkTypes);
        Assert.Equal("林岚发现暗号", result.Data.Value.GetProperty("content").GetString());
    }

    [Fact]
    public void CreateToolsIncludesReadAndEditWhenWorkspaceServicesAreConfigured()
    {
        var events = new RecordingBridgeEventSink();
        var registry = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            new RecordingChapterContentService(),
            new ToolApprovalCoordinator(events),
            events);

        var names = registry.CreateTools(new NovelistMafToolContext(17))
            .Select(tool => tool.Name)
            .ToArray();

        Assert.Equal(["search_story_memory", "get_chapter_list", "read", "edit"], names);
    }

    [Fact]
    public void CreateToolsIncludesStructuredNovelToolsWithoutSessionScopedSchemaFields()
    {
        var events = new RecordingBridgeEventSink();
        var registry = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            new RecordingChapterContentService(),
            new ToolApprovalCoordinator(events),
            events,
            subagents: null,
            preferences: new RecordingPreferenceService(),
            world: new RecordingWorldEntityService(),
            planning: new RecordingPlanningService());

        var tools = registry.CreateTools(new NovelistMafToolContext(17));
        var names = tools.Select(tool => tool.Name).ToArray();

        Assert.Contains("get_chapter_list", names);
        Assert.Contains("get_preferences", names);
        Assert.Contains("create_character", names);
        Assert.Contains("update_character_relationship", names);
        Assert.Contains("create_location_relation", names);
        Assert.Contains("get_timeline", names);
        Assert.Contains("create_story_arc", names);
        Assert.Contains("get_reader_perspective", names);
        Assert.Contains("delete_record", names);

        foreach (var tool in tools.Where(tool => tool.Name is not "search_story_memory"))
        {
            Assert.True(tool.JsonSchema.TryGetProperty("properties", out var properties), tool.Name);
            Assert.False(properties.TryGetProperty("novel_id", out _), tool.Name);
            Assert.False(properties.TryGetProperty("session_id", out _), tool.Name);
            Assert.False(properties.TryGetProperty("turn_id", out _), tool.Name);
            Assert.False(properties.TryGetProperty("tool_id", out _), tool.Name);
        }

        var createCharacter = tools.Single(tool => tool.Name == "create_character");
        Assert.True(createCharacter.JsonSchema.GetProperty("properties").TryGetProperty("characters", out var characters));
        Assert.Equal("array", characters.GetProperty("type").GetString());
    }

    [Fact]
    public void CreateToolsIncludesReferenceToolsOnlyWhenServicesAreConfigured()
    {
        var withoutReferenceServices = new NovelistMafToolRegistry(new RecordingStoryMemorySearchService());
        Assert.DoesNotContain(
            withoutReferenceServices.CreateTools(new NovelistMafToolContext(17)),
            tool => tool.Name.StartsWith("search_reference", StringComparison.Ordinal) ||
                tool.Name.StartsWith("get_reference_style", StringComparison.Ordinal) ||
                tool.Name.StartsWith("generate_reference", StringComparison.Ordinal));

        var withOnlyReferenceAnchors = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: new RecordingReferenceAnchorService());
        var materialToolNames = withOnlyReferenceAnchors.CreateTools(new NovelistMafToolContext(17))
            .Select(tool => tool.Name)
            .ToArray();
        Assert.Contains("get_reference_anchors", materialToolNames);
        Assert.Contains("search_reference_materials", materialToolNames);
        Assert.Contains("adapt_reference_material", materialToolNames);
        Assert.Contains("audit_reference_reuse", materialToolNames);
        Assert.DoesNotContain("get_reference_style_profiles", materialToolNames);
        Assert.DoesNotContain("get_reference_style_profile", materialToolNames);
        Assert.DoesNotContain("generate_reference_chapter_blueprint", materialToolNames);
        Assert.DoesNotContain("generate_reference_anchored_draft", materialToolNames);

        var withOnlyReferenceDrafts = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceDrafts: new RecordingReferenceAnchoredDraftService());
        var draftToolNames = withOnlyReferenceDrafts.CreateTools(new NovelistMafToolContext(17))
            .Select(tool => tool.Name)
            .ToArray();
        Assert.DoesNotContain("get_reference_anchors", draftToolNames);
        Assert.DoesNotContain("search_reference_materials", draftToolNames);
        Assert.DoesNotContain("get_reference_style_profiles", draftToolNames);
        Assert.DoesNotContain("get_reference_style_profile", draftToolNames);
        Assert.Contains("generate_reference_chapter_blueprint", draftToolNames);
        Assert.Contains("generate_reference_anchored_draft", draftToolNames);
        Assert.Contains("get_reference_draft_audits", draftToolNames);
        Assert.Contains("get_reference_style_audit_findings", draftToolNames);

        var withOnlyStyleProfiles = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: null,
            referenceDrafts: null,
            referenceStyleProfiles: new RecordingReferenceStyleProfileService());
        var styleProfileToolNames = withOnlyStyleProfiles.CreateTools(new NovelistMafToolContext(17))
            .Select(tool => tool.Name)
            .ToArray();
        Assert.Contains("get_reference_style_profiles", styleProfileToolNames);
        Assert.Contains("get_reference_style_profile", styleProfileToolNames);
        Assert.DoesNotContain("build_reference_style_profile", styleProfileToolNames);
        Assert.DoesNotContain("import_reference_style_profile", styleProfileToolNames);
        Assert.DoesNotContain("approve_reference_style_contract", styleProfileToolNames);
        Assert.DoesNotContain("insert_style_imitation_candidate", styleProfileToolNames);
        Assert.DoesNotContain("search_reference_materials", styleProfileToolNames);
        Assert.DoesNotContain("generate_reference_anchored_draft", styleProfileToolNames);

        var registry = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: new RecordingReferenceAnchorService(),
            referenceDrafts: new RecordingReferenceAnchoredDraftService(),
            referenceStyleProfiles: new RecordingReferenceStyleProfileService());

        var tools = registry.CreateTools(new NovelistMafToolContext(17));
        var names = tools.Select(tool => tool.Name).ToArray();

        Assert.Contains("get_reference_anchors", names);
        Assert.Contains("search_reference_materials", names);
        Assert.Contains("adapt_reference_material", names);
        Assert.Contains("audit_reference_reuse", names);
        Assert.Contains("generate_reference_chapter_blueprint", names);
        Assert.Contains("review_reference_chapter_blueprint", names);
        Assert.Contains("revise_reference_chapter_blueprint", names);
        Assert.Contains("approve_reference_chapter_blueprint", names);
        Assert.Contains("bind_reference_blueprint_materials", names);
        Assert.Contains("generate_reference_anchored_draft", names);
        Assert.Contains("audit_reference_anchored_draft", names);
        Assert.Contains("get_reference_draft_audits", names);
        Assert.Contains("get_reference_style_audit_findings", names);
        Assert.Contains("start_reference_orchestration_run", names);
        Assert.Contains("get_reference_orchestration_runs", names);
        Assert.Contains("get_reference_orchestration_run", names);
        Assert.Contains("get_reference_orchestration_run_events", names);
        Assert.Contains("cancel_reference_orchestration_run", names);
        Assert.Contains("get_reference_style_profiles", names);
        Assert.Contains("get_reference_style_profile", names);
        Assert.DoesNotContain("resume_reference_orchestration_run", names);
        Assert.DoesNotContain("approve_reference_orchestration_decision", names);
        Assert.DoesNotContain("apply_reference_blueprint_revision", names);
        Assert.DoesNotContain("insert_reference_anchored_draft", names);
        Assert.DoesNotContain("build_reference_style_profile", names);
        Assert.DoesNotContain("approve_reference_style_contract", names);
        Assert.DoesNotContain("import_reference_style_profile", names);
        Assert.DoesNotContain("insert_style_imitation_candidate", names);

        foreach (var tool in tools.Where(tool => tool.Name.Contains("reference", StringComparison.Ordinal)))
        {
            Assert.True(tool.JsonSchema.TryGetProperty("properties", out var properties), tool.Name);
            Assert.False(properties.TryGetProperty("novel_id", out _), tool.Name);
            Assert.False(properties.TryGetProperty("session_id", out _), tool.Name);
            Assert.False(properties.TryGetProperty("turn_id", out _), tool.Name);
            Assert.False(properties.TryGetProperty("tool_id", out _), tool.Name);
        }

        var generateDraft = tools.Single(tool => tool.Name == "generate_reference_anchored_draft");
        Assert.Contains("approved", generateDraft.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SaveContent", generateDraft.Description, StringComparison.Ordinal);
        Assert.True(generateDraft.JsonSchema.TryGetProperty("properties", out var generateDraftProperties));
        Assert.True(generateDraftProperties.TryGetProperty("blueprint_id", out _));
        Assert.True(generateDraftProperties.TryGetProperty("beat_ids", out _));
        Assert.True(generateDraftProperties.TryGetProperty("style_intensities", out _));
        Assert.True(generateDraftProperties.TryGetProperty("candidates_per_beat", out _));
        Assert.False(generateDraftProperties.TryGetProperty("content", out _));
        Assert.False(generateDraftProperties.TryGetProperty("text", out _));
        Assert.False(generateDraftProperties.TryGetProperty("path", out _));
        Assert.False(generateDraftProperties.TryGetProperty("chapter_path", out _));
        Assert.DoesNotContain("SaveContent", generateDraftProperties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);

        var bindMaterials = tools.Single(tool => tool.Name == "bind_reference_blueprint_materials");
        Assert.Contains("select_top_candidate", bindMaterials.Description, StringComparison.Ordinal);
        Assert.True(bindMaterials.JsonSchema.TryGetProperty("properties", out var bindMaterialsProperties));
        Assert.True(bindMaterialsProperties.TryGetProperty("blueprint_id", out _));
        Assert.True(bindMaterialsProperties.TryGetProperty("max_results_per_beat", out _));
        Assert.True(bindMaterialsProperties.TryGetProperty("select_top_candidate", out _));

        var getDraftAudits = tools.Single(tool => tool.Name == "get_reference_draft_audits");
        AssertToolDescriptionContains(getDraftAudits, "只读", "不返回候选正文", "不返回源文本", "不能批准", "不能写章节");
        Assert.True(getDraftAudits.JsonSchema.TryGetProperty("properties", out var getDraftAuditsProperties));
        Assert.True(getDraftAuditsProperties.TryGetProperty("blueprint_id", out _));
        Assert.True(getDraftAuditsProperties.TryGetProperty("candidate_ids", out _));
        Assert.True(getDraftAuditsProperties.TryGetProperty("limit", out _));
        Assert.False(getDraftAuditsProperties.TryGetProperty("novel_id", out _));
        Assert.False(getDraftAuditsProperties.TryGetProperty("content", out _));
        Assert.False(getDraftAuditsProperties.TryGetProperty("text", out _));
        Assert.False(getDraftAuditsProperties.TryGetProperty("candidate_text", out _));
        Assert.False(getDraftAuditsProperties.TryGetProperty("source_text", out _));
        Assert.False(getDraftAuditsProperties.TryGetProperty("prompt", out _));
        Assert.False(getDraftAuditsProperties.TryGetProperty("path", out _));

        var getStyleAuditFindings = tools.Single(tool => tool.Name == "get_reference_style_audit_findings");
        AssertToolDescriptionContains(getStyleAuditFindings, "只读", "style/source-leak", "不返回候选正文", "不返回源文本", "不能批准", "不能写章节");
        Assert.True(getStyleAuditFindings.JsonSchema.TryGetProperty("properties", out var getStyleAuditProperties));
        Assert.True(getStyleAuditProperties.TryGetProperty("blueprint_id", out _));
        Assert.True(getStyleAuditProperties.TryGetProperty("candidate_ids", out _));
        Assert.True(getStyleAuditProperties.TryGetProperty("risk_types", out _));
        Assert.True(getStyleAuditProperties.TryGetProperty("limit", out _));
        Assert.False(getStyleAuditProperties.TryGetProperty("novel_id", out _));
        Assert.False(getStyleAuditProperties.TryGetProperty("content", out _));
        Assert.False(getStyleAuditProperties.TryGetProperty("text", out _));
        Assert.False(getStyleAuditProperties.TryGetProperty("candidate_text", out _));
        Assert.False(getStyleAuditProperties.TryGetProperty("source_text", out _));
        Assert.False(getStyleAuditProperties.TryGetProperty("prompt", out _));
        Assert.False(getStyleAuditProperties.TryGetProperty("path", out _));

        var searchMaterials = tools.Single(tool => tool.Name == "search_reference_materials");
        AssertToolDescriptionContains(searchMaterials, "story context", "license", "score_components", "不直接写章节");
        Assert.True(searchMaterials.JsonSchema.TryGetProperty("properties", out var searchMaterialsProperties));
        Assert.True(searchMaterialsProperties.TryGetProperty("narrative_duties", out _));
        Assert.True(searchMaterialsProperties.TryGetProperty("emotion_transitions", out _));
        Assert.True(searchMaterialsProperties.TryGetProperty("prose_duties", out _));
        Assert.True(searchMaterialsProperties.TryGetProperty("style_profile_ids", out _));
        Assert.True(searchMaterialsProperties.TryGetProperty("style_dimensions", out _));
        Assert.True(searchMaterialsProperties.TryGetProperty("imitation_intensity", out _));

        var listStyleProfiles = tools.Single(tool => tool.Name == "get_reference_style_profiles");
        AssertToolDescriptionContains(listStyleProfiles, "只读", "不能构建", "不能导入", "不能审批", "不能写章节");
        Assert.True(listStyleProfiles.JsonSchema.TryGetProperty("properties", out var listStyleProfileProperties));
        Assert.True(listStyleProfileProperties.TryGetProperty("include_archived", out _));
        Assert.False(listStyleProfileProperties.TryGetProperty("anchor_ids", out _));
        Assert.False(listStyleProfileProperties.TryGetProperty("source_path", out _));

        var getStyleProfile = tools.Single(tool => tool.Name == "get_reference_style_profile");
        AssertToolDescriptionContains(getStyleProfile, "evidence", "不返回源文本", "只读", "不能审批");
        Assert.True(getStyleProfile.JsonSchema.TryGetProperty("properties", out var getStyleProfileProperties));
        Assert.True(getStyleProfileProperties.TryGetProperty("profile_id", out _));
        Assert.False(getStyleProfileProperties.TryGetProperty("source_text", out _));
        Assert.False(getStyleProfileProperties.TryGetProperty("content", out _));

        var approveBlueprint = tools.Single(tool => tool.Name == "approve_reference_chapter_blueprint");
        AssertToolDescriptionContains(approveBlueprint, "style_contract", "用户显式审批", "agent 不能批准 style contract");

        var startOrchestration = tools.Single(tool => tool.Name == "start_reference_orchestration_run");
        AssertToolDescriptionContains(
            startOrchestration,
            "source/fact",
            "blueprint revision",
            "final insertion",
            "作者");
        Assert.True(startOrchestration.JsonSchema.TryGetProperty("properties", out var startOrchestrationProperties));
        Assert.True(startOrchestrationProperties.TryGetProperty("chapter_number", out _));
        Assert.True(startOrchestrationProperties.TryGetProperty("chapter_goal", out _));
        Assert.True(startOrchestrationProperties.TryGetProperty("known_facts", out _));
        Assert.True(startOrchestrationProperties.TryGetProperty("forbidden_facts", out _));
        Assert.True(startOrchestrationProperties.TryGetProperty("include_anchor_ids", out _));
        Assert.True(startOrchestrationProperties.TryGetProperty("exclude_anchor_ids", out _));
        Assert.True(startOrchestrationProperties.TryGetProperty("license_statuses", out _));
        Assert.False(startOrchestrationProperties.TryGetProperty("anchor_ids", out _));
        Assert.False(startOrchestrationProperties.TryGetProperty("source_confirmed", out _));
        Assert.False(startOrchestrationProperties.TryGetProperty("decision_type", out _));
        Assert.False(startOrchestrationProperties.TryGetProperty("decision_payload", out _));
        Assert.False(startOrchestrationProperties.TryGetProperty("content", out _));
        Assert.False(startOrchestrationProperties.TryGetProperty("text", out _));

        var getRuns = tools.Single(tool => tool.Name == "get_reference_orchestration_runs");
        Assert.True(getRuns.JsonSchema.GetProperty("properties").TryGetProperty("chapter_number", out _));

        var getRun = tools.Single(tool => tool.Name == "get_reference_orchestration_run");
        Assert.True(getRun.JsonSchema.GetProperty("properties").TryGetProperty("run_id", out _));

        var getEvents = tools.Single(tool => tool.Name == "get_reference_orchestration_run_events");
        AssertToolDescriptionContains(getEvents, "只读", "不批准", "不恢复", "不写章节");
        Assert.True(getEvents.JsonSchema.GetProperty("properties").TryGetProperty("run_id", out _));
        Assert.False(getEvents.JsonSchema.GetProperty("properties").TryGetProperty("decision_type", out _));
        Assert.False(getEvents.JsonSchema.GetProperty("properties").TryGetProperty("decision_payload", out _));

        var cancelRun = tools.Single(tool => tool.Name == "cancel_reference_orchestration_run");
        Assert.True(cancelRun.JsonSchema.GetProperty("properties").TryGetProperty("run_id", out _));
        Assert.True(cancelRun.JsonSchema.GetProperty("properties").TryGetProperty("reason", out _));
    }

    [Fact]
    public void ReferenceAgentToolsCannotImportCorpusSourcesOrReadArbitraryFiles()
    {
        var registry = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: new RecordingReferenceAnchorService(),
            referenceDrafts: new RecordingReferenceAnchoredDraftService(),
            referenceStyleProfiles: new RecordingReferenceStyleProfileService());

        var referenceTools = registry.CreateTools(new NovelistMafToolContext(17))
            .Where(tool => tool.Name.Contains("reference", StringComparison.Ordinal))
            .ToArray();
        var names = referenceTools.Select(tool => tool.Name).ToArray();

        Assert.DoesNotContain("create_reference_anchor", names);
        Assert.DoesNotContain("import_reference_source", names);
        Assert.DoesNotContain("pick_reference_source_file", names);
        Assert.DoesNotContain("promote_reference_anchor_to_workspace_corpus", names);
        Assert.DoesNotContain("update_reference_anchor_metadata", names);
        Assert.DoesNotContain("delete_reference_anchor", names);
        Assert.DoesNotContain("build_reference_style_profile", names);
        Assert.DoesNotContain("archive_reference_style_profile", names);
        Assert.DoesNotContain("update_reference_style_profile", names);

        foreach (var tool in referenceTools)
        {
            Assert.True(tool.JsonSchema.TryGetProperty("properties", out var properties), tool.Name);
            Assert.False(properties.TryGetProperty("source_path", out _), tool.Name);
            Assert.False(properties.TryGetProperty("file_path", out _), tool.Name);
            Assert.False(properties.TryGetProperty("absolute_path", out _), tool.Name);
            Assert.False(properties.TryGetProperty("path", out _), tool.Name);
            Assert.False(properties.TryGetProperty("source_file", out _), tool.Name);
            Assert.False(properties.TryGetProperty("source_uri", out _), tool.Name);
            Assert.False(properties.TryGetProperty("source_url", out _), tool.Name);
            Assert.False(properties.TryGetProperty("import_path", out _), tool.Name);
        }

        AssertToolDescriptionContains(
            referenceTools.Single(tool => tool.Name == "get_reference_anchors"),
            "已导入",
            "不能导入",
            "不能读取任意文件");
        AssertToolDescriptionContains(
            referenceTools.Single(tool => tool.Name == "search_reference_materials"),
            "已导入",
            "license/visibility",
            "不能导入",
            "不能读取任意文件");
        AssertToolDescriptionContains(
            referenceTools.Single(tool => tool.Name == "start_reference_orchestration_run"),
            "已导入",
            "license/visibility",
            "不能导入",
            "不能读取任意文件");
        AssertToolDescriptionContains(
            referenceTools.Single(tool => tool.Name == "get_reference_style_profiles"),
            "已存在",
            "只读",
            "不能构建",
            "不能导入");
    }

    [Fact]
    public void Phase15AgentBoundaryDoesNotExposeUnsafeDesktopOrMutationTools()
    {
        var events = new RecordingBridgeEventSink();
        var registry = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            new RecordingChapterContentService(),
            new ToolApprovalCoordinator(events),
            events,
            subagents: new RecordingSubagentRunner(),
            preferences: new RecordingPreferenceService(),
            world: new RecordingWorldEntityService(),
            planning: new RecordingPlanningService(),
            webFetch: new RecordingWebFetchService(),
            webSearch: new RecordingWebSearchService(),
            referenceAnchors: new RecordingReferenceAnchorService(),
            referenceDrafts: new RecordingReferenceAnchoredDraftService(),
            referenceStyleProfiles: new RecordingReferenceStyleProfileService());

        var tools = registry.CreateTools(new NovelistMafToolContext(17));
        var names = tools.Select(tool => tool.Name).ToArray();

        foreach (var forbidden in ForbiddenPhase15AgentToolNames)
        {
            Assert.DoesNotContain(forbidden, names);
        }

        Assert.DoesNotContain(names, name => name.Contains("import", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("picker", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("pick_file", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("external_url", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("open_release", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("update_check", StringComparison.OrdinalIgnoreCase));

        AssertToolDescriptionContains(
            tools.Single(tool => tool.Name == "read"),
            "读取小说文件或技能文件",
            "chapters/",
            "skills/",
            "内置技能，只读");
        AssertToolDescriptionContains(
            tools.Single(tool => tool.Name == "web_fetch"),
            "SSRF",
            "只读取网页内容",
            "不执行页面脚本");
    }

    [Fact]
    public void ReferenceStyleAgentToolAuthorityMatrixAllowsOnlySearchInspectAndCandidatePreparation()
    {
        var registry = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: new RecordingReferenceAnchorService(),
            referenceDrafts: new RecordingReferenceAnchoredDraftService(),
            referenceStyleProfiles: new RecordingReferenceStyleProfileService());

        var referenceTools = registry.CreateTools(new NovelistMafToolContext(17))
            .Where(tool => tool.Name.Contains("reference", StringComparison.Ordinal))
            .ToDictionary(tool => tool.Name);
        var names = referenceTools.Keys.ToArray();

        string[] allowedStyleSurface =
        [
            "generate_reference_chapter_blueprint",
            "search_reference_materials",
            "get_reference_style_profiles",
            "get_reference_style_profile",
            "revise_reference_chapter_blueprint",
            "review_reference_chapter_blueprint",
            "approve_reference_chapter_blueprint",
            "bind_reference_blueprint_materials",
            "generate_reference_anchored_draft",
            "audit_reference_anchored_draft",
            "get_reference_draft_audits",
            "get_reference_style_audit_findings"
        ];

        foreach (var allowed in allowedStyleSurface)
        {
            Assert.Contains(allowed, names);
        }

        string[] forbiddenStyleSurface =
        [
            "build_reference_style_profile",
            "import_reference_style_profile",
            "archive_reference_style_profile",
            "restore_reference_style_profile",
            "update_reference_style_profile",
            "delete_reference_style_profile",
            "approve_reference_style_contract",
            "approve_reference_orchestration_decision",
            "resume_reference_orchestration_run",
            "apply_reference_blueprint_revision",
            "insert_reference_anchored_draft",
            "insert_style_imitation_candidate",
            "save_reference_anchored_draft",
            "save_style_candidate",
            "save_content",
            "SaveContent"
        ];

        foreach (var forbidden in forbiddenStyleSurface)
        {
            Assert.DoesNotContain(forbidden, names);
        }

        foreach (var toolName in allowedStyleSurface)
        {
            var tool = referenceTools[toolName];
            Assert.True(tool.JsonSchema.TryGetProperty("properties", out var properties), tool.Name);
            foreach (var forbiddenProperty in ForbiddenReferenceStyleToolProperties)
            {
                Assert.False(properties.TryGetProperty(forbiddenProperty, out _), $"{tool.Name} exposes {forbiddenProperty}");
            }
        }

        AssertToolDescriptionContains(
            referenceTools["approve_reference_chapter_blueprint"],
            "style_contract",
            "用户显式审批",
            "agent 不能批准 style contract");
        AssertToolDescriptionContains(
            referenceTools["get_reference_style_audit_findings"],
            "只读",
            "style/source-leak",
            "不能批准",
            "不能写章节");
    }

    [Fact]
    public void ReferenceDraftToolDescriptionsEnforceBlueprintWorkflowOrder()
    {
        var registry = new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: new RecordingReferenceAnchorService(),
            referenceDrafts: new RecordingReferenceAnchoredDraftService());

        var tools = registry.CreateTools(new NovelistMafToolContext(17))
            .Where(tool => tool.Name.Contains("reference", StringComparison.Ordinal))
            .ToDictionary(tool => tool.Name);

        AssertToolDescriptionContains(
            tools["generate_reference_chapter_blueprint"],
            "review_reference_chapter_blueprint",
            "不生成正文");
        AssertToolDescriptionContains(
            tools["review_reference_chapter_blueprint"],
            "generate_reference_chapter_blueprint",
            "revise_reference_chapter_blueprint",
            "approve_reference_chapter_blueprint");
        AssertToolDescriptionContains(
            tools["approve_reference_chapter_blueprint"],
            "review_reference_chapter_blueprint",
            "bind_reference_blueprint_materials");
        AssertToolDescriptionContains(
            tools["bind_reference_blueprint_materials"],
            "approve_reference_chapter_blueprint",
            "select_top_candidate=true",
            "generate_reference_anchored_draft");
        AssertToolDescriptionContains(
            tools["generate_reference_anchored_draft"],
            "generate_reference_chapter_blueprint",
            "review_reference_chapter_blueprint",
            "approve_reference_chapter_blueprint",
            "bind_reference_blueprint_materials",
            "select_top_candidate=true",
            "audit_reference_anchored_draft",
            "SaveContent");
        AssertToolDescriptionContains(
            tools["audit_reference_anchored_draft"],
            "generate_reference_anchored_draft",
            "纯检查");
    }

    [Fact]
    public async Task ReferenceMaterialToolInjectsNovelContext()
    {
        var anchors = new RecordingReferenceAnchorService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: anchors));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall(
                "call_reference_search",
                "search_reference_materials",
                """{"query":"雨夜压迫感","anchor_ids":[7],"material_types":["sentence"],"narrative_duties":["external_evidence"],"emotion_transitions":["neutral->pressure"],"prose_duties":["source_backed_detail"],"style_profile_ids":[99],"style_dimensions":["dialogue_ratio"],"imitation_intensity":"strong","page":1,"size":5}"""),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(anchors.LastSearch);
        Assert.Equal(23, anchors.LastSearch.NovelId);
        Assert.Equal("雨夜压迫感", anchors.LastSearch.Query);
        Assert.Equal([7], anchors.LastSearch.AnchorIds);
        Assert.Equal(["sentence"], anchors.LastSearch.MaterialTypes);
        Assert.Equal(["external_evidence"], anchors.LastSearch.NarrativeDuties);
        Assert.Equal(["neutral->pressure"], anchors.LastSearch.EmotionTransitions);
        Assert.Equal(["source_backed_detail"], anchors.LastSearch.ProseDuties);
        Assert.Equal([99], anchors.LastSearch.StyleProfileIds);
        Assert.Equal(["dialogue_ratio"], anchors.LastSearch.StyleDimensions);
        Assert.Equal(ReferenceStyleImitationIntensities.Strong, anchors.LastSearch.ImitationIntensity);
        var material = result.Data!.Value.GetProperty("items")[0];
        Assert.Equal("mat-1", material.GetProperty("material_id").GetString());
        var components = material.GetProperty("score_components");
        Assert.Equal(2.5, components.GetProperty("story_context").GetDouble());
        Assert.Equal(1.25, components.GetProperty("prose_duty").GetDouble());
    }

    [Fact]
    public async Task ReferenceStyleProfileToolsInjectNovelContext()
    {
        var styleProfiles = new RecordingReferenceStyleProfileService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: null,
            referenceDrafts: null,
            referenceStyleProfiles: styleProfiles));

        var listResult = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall(
                "call_reference_style_profiles",
                "get_reference_style_profiles",
                """{"include_archived":true}"""),
            CancellationToken.None);

        Assert.True(listResult.Success, listResult.Error);
        Assert.NotNull(styleProfiles.LastList);
        Assert.Equal(23, styleProfiles.LastList.NovelId);
        Assert.True(styleProfiles.LastList.IncludeArchived);
        var summary = listResult.Data!.Value[0];
        Assert.Equal(99, summary.GetProperty("profile_id").GetInt64());
        Assert.Equal(23, summary.GetProperty("novel_id").GetInt64());

        var detailResult = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 2),
            new ChatToolCall(
                "call_reference_style_profile",
                "get_reference_style_profile",
                """{"profile_id":99}"""),
            CancellationToken.None);

        Assert.True(detailResult.Success, detailResult.Error);
        Assert.Equal(23, styleProfiles.LastGetNovelId);
        Assert.Equal(99, styleProfiles.LastGetProfileId);
        var profile = detailResult.Data!.Value;
        Assert.Equal(99, profile.GetProperty("profile_id").GetInt64());
        Assert.True(profile.TryGetProperty("features", out var features));
        Assert.Equal("dialogue_ratio", features.GetProperty("numeric_features")[0].GetProperty("feature_key").GetString());
        Assert.True(profile.TryGetProperty("evidence_spans", out var evidenceSpans));
        Assert.Equal("ev-1", evidenceSpans[0].GetProperty("evidence_id").GetString());
        Assert.False(profile.TryGetProperty("source_text", out _));
        Assert.False(profile.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task ReferenceDraftBindToolInjectsSelectionIntent()
    {
        var drafts = new RecordingReferenceAnchoredDraftService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: null,
            referenceDrafts: drafts));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall(
                "call_reference_bind",
                "bind_reference_blueprint_materials",
                """{"blueprint_id":501,"max_results_per_beat":4,"select_top_candidate":true}"""),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(drafts.LastBind);
        Assert.Equal(23, drafts.LastBind.NovelId);
        Assert.Equal(501, drafts.LastBind.BlueprintId);
        Assert.Equal(4, drafts.LastBind.MaxResultsPerBeat);
        Assert.True(drafts.LastBind.SelectTopCandidate);
    }

    [Fact]
    public async Task ReferenceDraftAuditInspectionToolInjectsNovelContextAndReturnsOnlyReports()
    {
        var drafts = new RecordingReferenceAnchoredDraftService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: null,
            referenceDrafts: drafts));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall(
                "call_reference_get_draft_audits",
                "get_reference_draft_audits",
                """{"blueprint_id":501,"candidate_ids":["candidate-1"],"limit":2}"""),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(drafts.LastGetAudits);
        Assert.Equal(23, drafts.LastGetAudits.NovelId);
        Assert.Equal(501, drafts.LastGetAudits.BlueprintId);
        Assert.Equal(["candidate-1"], drafts.LastGetAudits.CandidateIds);
        Assert.Equal(2, drafts.LastGetAudits.Limit);

        var audits = result.Data!.Value.EnumerateArray().ToArray();
        var audit = Assert.Single(audits);
        Assert.Equal("draft-audit-1", audit.GetProperty("audit_id").GetString());
        Assert.Equal("candidate-1", audit.GetProperty("candidate_ids")[0].GetString());
        Assert.Equal("Persisted draft audit failed for 1 candidate.", audit.GetProperty("readable_report").GetProperty("summary").GetString());
        Assert.False(audit.TryGetProperty("candidate_text", out _));
        Assert.False(audit.TryGetProperty("source_text", out _));
        Assert.False(audit.TryGetProperty("prompt", out _));
        Assert.False(audit.TryGetProperty("content", out _));
        Assert.False(audit.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task ReferenceStyleAuditInspectionToolInjectsNovelContextAndReturnsOnlyStyleFindings()
    {
        var drafts = new RecordingReferenceAnchoredDraftService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: null,
            referenceDrafts: drafts));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall(
                "call_reference_get_style_audit_findings",
                "get_reference_style_audit_findings",
                """{"blueprint_id":501,"candidate_ids":["candidate-1"],"risk_types":["source_leak"],"limit":2}"""),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(drafts.LastGetStyleAuditFindings);
        Assert.Equal(23, drafts.LastGetStyleAuditFindings.NovelId);
        Assert.Equal(501, drafts.LastGetStyleAuditFindings.BlueprintId);
        Assert.Equal(["candidate-1"], drafts.LastGetStyleAuditFindings.CandidateIds);
        Assert.Equal(["source_leak"], drafts.LastGetStyleAuditFindings.RiskTypes);
        Assert.Equal(2, drafts.LastGetStyleAuditFindings.Limit);

        var findings = result.Data!.Value.EnumerateArray().ToArray();
        var finding = Assert.Single(findings);
        Assert.Equal("draft-audit-1", finding.GetProperty("audit_id").GetString());
        Assert.Equal("source_leak", finding.GetProperty("risk_type").GetString());
        Assert.Equal("candidate-1", finding.GetProperty("candidate_ids")[0].GetString());
        Assert.Contains("Source-leak risk", finding.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(finding.TryGetProperty("candidate_text", out _));
        Assert.False(finding.TryGetProperty("source_text", out _));
        Assert.False(finding.TryGetProperty("prompt", out _));
        Assert.False(finding.TryGetProperty("content", out _));
        Assert.False(finding.TryGetProperty("path", out _));
    }

    [Fact]
    public async Task ReferenceOrchestrationAgentToolStartsRunWithoutApprovingHumanDecisions()
    {
        var drafts = new RecordingReferenceAnchoredDraftService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: null,
            referenceDrafts: drafts));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall(
                "call_reference_orchestration_start",
                "start_reference_orchestration_run",
                """
                {
                  "chapter_number": 8,
                  "chapter_goal": "雨夜逼问后让主角确认盟友隐瞒了港口线索",
                  "known_facts": ["主角已经知道港口暗号"],
                  "forbidden_facts": ["不能揭露内鬼身份"],
                  "include_anchor_ids": [7],
                  "exclude_anchor_ids": [9],
                  "license_statuses": ["user_provided"],
                  "max_results_per_beat": 4
                }
                """),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(drafts.LastStart);
        Assert.Equal(23, drafts.LastStart.NovelId);
        Assert.Equal(8, drafts.LastStart.ChapterNumber);
        Assert.Equal("雨夜逼问后让主角确认盟友隐瞒了港口线索", drafts.LastStart.ChapterGoal);
        Assert.Equal(["主角已经知道港口暗号"], drafts.LastStart.KnownFacts);
        Assert.Equal(["不能揭露内鬼身份"], drafts.LastStart.ForbiddenFacts);
        Assert.Null(drafts.LastStart.AnchorIds);
        Assert.False(drafts.LastStart.SourceConfirmed);
        Assert.Equal("story_context", drafts.LastStart.CorpusSearchPolicy.Mode);
        Assert.Equal(4, drafts.LastStart.CorpusSearchPolicy.MaxResultsPerBeat);
        Assert.Equal(["user_provided"], drafts.LastStart.CorpusSearchPolicy.LicenseStatuses);
        Assert.Equal([7], drafts.LastStart.CorpusSearchPolicy.IncludeAnchorIds);
        Assert.Equal([9], drafts.LastStart.CorpusSearchPolicy.ExcludeAnchorIds);
        Assert.Null(drafts.LastStart.StylePolicy);

        var data = result.Data!.Value;
        Assert.Equal(ReferenceOrchestrationRunStatuses.WaitingForUser, data.GetProperty("status").GetString());
        Assert.Equal(ReferenceOrchestrationStages.SourceConfirmation, data.GetProperty("stage").GetString());
        Assert.Equal(
            ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts,
            data.GetProperty("current_decision").GetProperty("decision_type").GetString());
    }

    [Fact]
    public async Task ReferenceOrchestrationAgentToolReadsRunEventsWithoutApprovingHumanDecisions()
    {
        var drafts = new RecordingReferenceAnchoredDraftService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: null,
            webSearch: null,
            referenceAnchors: null,
            referenceDrafts: drafts));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall(
                "call_reference_orchestration_events",
                "get_reference_orchestration_run_events",
                """{"run_id":"run-7"}"""),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(23, drafts.LastGetEventsNovelId);
        Assert.Equal("run-7", drafts.LastGetEventsRunId);

        var events = result.Data!.Value.EnumerateArray().ToArray();
        var item = Assert.Single(events);
        Assert.Equal("run-7", item.GetProperty("run_id").GetString());
        Assert.Equal("required_decision", item.GetProperty("event_type").GetString());
        Assert.Equal(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, item.GetProperty("decision_type").GetString());
    }

    [Fact]
    public async Task StructuredToolSupportsComplexArrayArgumentsAndInjectsNovelContext()
    {
        var world = new RecordingWorldEntityService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world,
            planning: null));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_1", 2),
            new ChatToolCall(
                "call_create_character",
                "create_character",
                """
                {"characters":[{"name":"林岚","description":"记者","personality":"{\"role\":\"主角\"}","abilities":"[\"追踪\"]"}]}
                """),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Data);
        Assert.Equal(23, world.LastCreateCharacterNovelId);
        Assert.Equal("林岚", world.LastCreateCharacterInput!.Name);
        Assert.Equal([101], result.Data.Value.GetProperty("ids").EnumerateArray().Select(item => item.GetInt64()).ToArray());
        Assert.Equal(1, result.Data.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task RunSubagentToolInvokesRunnerWithParentChatContext()
    {
        var runner = new RecordingSubagentRunner();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: runner));

        var names = executor.GetToolDefinitions(11).Select(tool => tool.Name).ToArray();
        Assert.Equal(["search_story_memory", "run_subagent"], names);
        var definition = executor.GetToolDefinitions(11).Single(tool => tool.Name == "run_subagent");
        Assert.True(definition.ParametersSchema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("agent_type", out _));
        Assert.True(properties.TryGetProperty("instruction", out _));
        Assert.False(properties.TryGetProperty("session_id", out _));
        Assert.False(properties.TryGetProperty("turn_id", out _));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(11, "sess_sub", 3, "test", "model-a", "high", 8),
            new ChatToolCall(
                "call_sub_1",
                "run_subagent",
                """{"agent_type":"review","instruction":"审第 3 章"}"""),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal(11, runner.LastRequest.NovelId);
        Assert.Equal("sess_sub", runner.LastRequest.SessionId);
        Assert.Equal(3, runner.LastRequest.TurnId);
        Assert.Equal("call_sub_1", runner.LastRequest.ToolId);
        Assert.Equal("review", runner.LastRequest.AgentType);
        Assert.Equal("审第 3 章", runner.LastRequest.Instruction);
        Assert.Equal("test", runner.LastRequest.ProviderName);
        Assert.Equal("model-a", runner.LastRequest.ModelId);
        Assert.Equal("high", runner.LastRequest.ReasoningEffort);
        Assert.Equal(8, runner.LastRequest.StartSequence);
        Assert.Equal("review", result.Data!.Value.GetProperty("agent_type").GetString());
        Assert.Equal("审稿报告", result.Data.Value.GetProperty("report").GetString());
    }

    [Fact]
    public async Task ReadToolReturnsSafeLineNumberedWorkspaceContent()
    {
        var content = new RecordingChapterContentService
        {
            Files = { ["chapters/001.md"] = "第一行\n第二行\n第三行" }
        };
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            content,
            approvals: null,
            events: null));

        var result = await executor.ExecuteAsync(
            new ChatToolExecutionContext(5, "sess_1", 1),
            new ChatToolCall(
                "call_read_1",
                "read",
                """{"path":"chapters/001.md","start_line":2,"end_line":3}"""),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(5, content.LastNovelId);
        Assert.Equal("chapters/001.md", content.LastPath);
        Assert.NotNull(result.Data);
        var data = result.Data.Value;
        Assert.Equal("chapters/001.md", data.GetProperty("path").GetString());
        Assert.Equal("第1章", data.GetProperty("display").GetString());
        Assert.Equal("2|第二行\n3|第三行", data.GetProperty("content").GetString());
        Assert.Equal(3, data.GetProperty("total_lines").GetInt32());
        Assert.Equal(2, data.GetProperty("start_line").GetInt32());
        Assert.Equal(3, data.GetProperty("end_line").GetInt32());
    }

    [Fact]
    public async Task WebToolsExposeLegacySchemaAndInvokeConfiguredServices()
    {
        var fetch = new RecordingWebFetchService();
        var search = new RecordingWebSearchService();
        var executor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new RecordingStoryMemorySearchService(),
            chapterContent: null,
            approvals: null,
            events: null,
            subagents: null,
            preferences: null,
            world: null,
            planning: null,
            webFetch: fetch,
            webSearch: search));

        var definitions = executor.GetToolDefinitions(17);
        var webFetch = definitions.Single(tool => tool.Name == "web_fetch");
        var webSearch = definitions.Single(tool => tool.Name == "web_search");

        Assert.True(webFetch.ParametersSchema.GetProperty("properties").TryGetProperty("url", out _));
        Assert.False(webFetch.ParametersSchema.GetProperty("properties").TryGetProperty("novel_id", out _));
        Assert.True(webSearch.ParametersSchema.GetProperty("properties").TryGetProperty("prompt", out _));
        Assert.False(webSearch.ParametersSchema.GetProperty("properties").TryGetProperty("session_id", out _));

        var fetchResult = await executor.ExecuteAsync(
            new ChatToolExecutionContext(17, "sess_web", 1),
            new ChatToolCall("call_fetch", "web_fetch", """{"url":"https://example.test/story"}"""),
            CancellationToken.None);

        Assert.True(fetchResult.Success, fetchResult.Error);
        Assert.Equal("https://example.test/story", fetch.LastUrl);
        Assert.Equal("网页标题", fetchResult.Data!.Value.GetProperty("title").GetString());
        Assert.Equal("正文", fetchResult.Data.Value.GetProperty("text").GetString());

        var searchResult = await executor.ExecuteAsync(
            new ChatToolExecutionContext(17, "sess_web", 1),
            new ChatToolCall("call_search", "web_search", """{"prompt":"检索 DeepSeek web search 文档"}"""),
            CancellationToken.None);

        Assert.True(searchResult.Success, searchResult.Error);
        Assert.Equal("检索 DeepSeek web search 文档", search.LastPrompt);
        Assert.Equal("检索 DeepSeek web search 文档", searchResult.Data!.Value.GetProperty("queries")[0].GetString());
        Assert.Equal("综合摘要", searchResult.Data.Value.GetProperty("summary").GetString());
        Assert.Equal("https://example.test/source", searchResult.Data.Value.GetProperty("sources")[0].GetProperty("url").GetString());
    }

    private static void AssertToolDescriptionContains(AIFunction tool, params string[] expectedParts)
    {
        foreach (var expected in expectedParts)
        {
            Assert.Contains(expected, tool.Description, StringComparison.Ordinal);
        }
    }

    private static readonly string[] ForbiddenReferenceStyleToolProperties =
    [
        "novel_id",
        "session_id",
        "turn_id",
        "tool_id",
        "content",
        "text",
        "candidate_text",
        "source_text",
        "prompt",
        "path",
        "chapter_path",
        "source_path",
        "file_path",
        "absolute_path",
        "source_file",
        "source_uri",
        "source_url",
        "import_path",
        "approval_id",
        "approved",
        "restore",
        "save",
        "SaveContent"
    ];

    private static readonly string[] ForbiddenPhase15AgentToolNames =
    [
        "start_novel_import",
        "cancel_novel_import",
        "reconcile_novel_import_runs",
        "pick_novel_import_file",
        "pick_reference_source_file",
        "create_reference_anchor",
        "create_reference_anchors",
        "promote_reference_anchor_to_workspace_corpus",
        "promote_reference_anchors_to_workspace_corpus",
        "update_reference_anchor_metadata",
        "delete_reference_anchor",
        "delete_reference_anchors",
        "rebuild_reference_anchor",
        "build_reference_style_profile",
        "import_reference_style_profile",
        "archive_reference_style_profile",
        "restore_reference_style_profile",
        "update_reference_style_profile",
        "delete_reference_style_profile",
        "approve_reference_style_contract",
        "resume_reference_orchestration_run",
        "approve_reference_orchestration_decision",
        "apply_reference_blueprint_revision",
        "insert_reference_anchored_draft",
        "save_reference_anchored_draft",
        "insert_style_imitation_candidate",
        "search_git_history",
        "get_git_commits",
        "get_git_commit_files",
        "get_git_file_diff",
        "git_commit",
        "git_stage",
        "git_reset",
        "git_checkout",
        "git_restore",
        "git_revert",
        "git_cherry_pick",
        "check_for_updates",
        "open_release_page",
        "open_external_url",
        "runtime.shell.openExternal",
        "search_style_samples",
        "get_style_sample",
        "create_style_sample",
        "update_style_sample",
        "delete_style_sample",
        "extract_style_skill_from_samples",
        "cancel_style_skill_extraction",
        "start_narrative_pattern_extraction",
        "cancel_narrative_pattern_extraction",
        "save_generated_pattern_skill",
        "save_generated_style_skill"
    ];

    private sealed class RecordingStoryMemorySearchService : IStoryMemorySearchService
    {
        public SearchStoryMemoryPayload? LastInput { get; private set; }

        public ValueTask<SearchStoryMemoryResultPayload> SearchAsync(
            SearchStoryMemoryPayload input,
            CancellationToken cancellationToken)
        {
            LastInput = input;
            return ValueTask.FromResult(new SearchStoryMemoryResultPayload(
                input.Query,
                Total: 1,
                Message: string.Empty,
                MaxRelevance: "0.91",
                Content: "林岚发现暗号",
                Results:
                [
                    new StoryMemoryHitPayload(
                        "chunk-1",
                        ChapterNumber: 1,
                        ChapterTitle: "雾中来信",
                        ChunkType: "content",
                        Relevance: 0.91,
                        Content: "林岚发现暗号")
            ]));
        }
    }

    private sealed class RecordingChapterContentService : IChapterContentService
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public long LastNovelId { get; private set; }

        public string LastPath { get; private set; } = string.Empty;

        public ValueTask<IReadOnlyList<ChapterPayload>> GetChaptersAsync(
            long novelId,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ChapterPayload>>([]);
        }

        public ValueTask<int> GetMaxChapterNumberAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(0);
        }

        public ValueTask<ChapterPayload> CreateChapterAsync(
            CreateChapterPayload input,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask UpdateChapterTitleAsync(
            long novelId,
            int chapterNumber,
            string title,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<string> GetContentAsync(
            long novelId,
            string path,
            CancellationToken cancellationToken)
        {
            LastNovelId = novelId;
            LastPath = path;
            return ValueTask.FromResult(Files.GetValueOrDefault(path, string.Empty));
        }

        public ValueTask SaveContentAsync(
            SaveContentPayload input,
            CancellationToken cancellationToken)
        {
            Files[input.Path] = input.Content;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSubagentRunner : ISubagentRunner
    {
        public SubagentRunRequest? LastRequest { get; private set; }

        public ValueTask<SubagentRunResult> RunAsync(
            SubagentRunRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(new SubagentRunResult(request.AgentType, "审稿报告"));
        }
    }

    private sealed class RecordingWebFetchService : IWebFetchService
    {
        public string LastUrl { get; private set; } = string.Empty;

        public ValueTask<WebFetchResultPayload> FetchAsync(string url, CancellationToken cancellationToken)
        {
            LastUrl = url;
            return ValueTask.FromResult(new WebFetchResultPayload(url, "网页标题", "正文"));
        }
    }

    private sealed class RecordingWebSearchService : IWebSearchService
    {
        public string LastPrompt { get; private set; } = string.Empty;

        public ValueTask<WebSearchResultPayload> SearchAsync(string prompt, CancellationToken cancellationToken)
        {
            LastPrompt = prompt;
            return ValueTask.FromResult(new WebSearchResultPayload(
                [prompt],
                "综合摘要",
                [new WebSearchSourcePayload("来源", "https://example.test/source")]));
        }
    }

    private sealed class RecordingReferenceStyleProfileService : IReferenceStyleProfileService
    {
        public GetReferenceStyleProfilesPayload? LastList { get; private set; }

        public long LastGetNovelId { get; private set; }

        public long LastGetProfileId { get; private set; }

        public ValueTask<ReferenceStyleProfilePayload> BuildStyleProfileAsync(
            BuildReferenceStyleProfilePayload input,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("MAF tools must not build style profiles.");
        }

        public ValueTask<IReadOnlyList<ReferenceStyleProfileSummaryPayload>> GetStyleProfilesAsync(
            GetReferenceStyleProfilesPayload input,
            CancellationToken cancellationToken)
        {
            LastList = input;
            IReadOnlyList<ReferenceStyleProfileSummaryPayload> profiles =
            [
                new ReferenceStyleProfileSummaryPayload(
                    99,
                    input.NovelId,
                    "雨夜克制风格",
                    "deterministic baseline",
                    ReferenceStyleProfileStatuses.Active,
                    ReferenceStyleAnalyzerVersions.DeterministicV1,
                    ReferenceStyleFeatureSchemaVersions.V1,
                    ReferenceStyleAnalyzerSources.DeterministicBaseline,
                    [7],
                    ["hash"],
                    0.91,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    ArchivedAt: null)
            ];
            return ValueTask.FromResult(profiles);
        }

        public ValueTask<ReferenceStyleProfilePayload?> GetStyleProfileAsync(
            long novelId,
            long profileId,
            CancellationToken cancellationToken)
        {
            LastGetNovelId = novelId;
            LastGetProfileId = profileId;
            return ValueTask.FromResult<ReferenceStyleProfilePayload?>(new ReferenceStyleProfilePayload(
                profileId,
                novelId,
                "雨夜克制风格",
                "deterministic baseline",
                ReferenceStyleProfileStatuses.Active,
                ReferenceStyleAnalyzerVersions.DeterministicV1,
                ReferenceStyleFeatureSchemaVersions.V1,
                ReferenceStyleAnalyzerSources.DeterministicBaseline,
                [7],
                ["hash"],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified],
                0.91,
                new ReferenceStyleFeatureVectorPayload(
                    [
                        new ReferenceStyleNumericFeaturePayload(
                            "dialogue_ratio",
                            0.42,
                            "ratio",
                            0.9,
                            ["ev-1"])
                    ],
                    [],
                    []),
                [
                    new ReferenceStyleEvidenceSpanPayload(
                        "ev-1",
                        profileId,
                        7,
                        "seg-1",
                        "mat-1",
                        "dialogue_ratio",
                        "dialogue",
                        0,
                        12,
                        "span-hash",
                        0.9,
                        ReferenceStyleAnalyzerSources.DeterministicBaseline)
                ],
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ArchivedAt: null));
        }

        public ValueTask<ReferenceStyleProfileBuildStatusPayload?> GetStyleProfileBuildStatusAsync(
            GetReferenceStyleProfileBuildStatusPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ReferenceStyleProfileBuildStatusPayload?>(null);
        }

        public ValueTask<ReferenceStyleProfileBuildStatusPayload> CancelStyleProfileBuildAsync(
            CancelReferenceStyleProfileBuildPayload input,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("MAF tools must not cancel style profile builds.");
        }

        public ValueTask<ReferenceStyleProfilePayload> ArchiveStyleProfileAsync(
            ArchiveReferenceStyleProfilePayload input,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("MAF tools must not archive style profiles.");
        }

        public ValueTask<ReferenceStyleProfilePayload> RestoreStyleProfileAsync(
            RestoreReferenceStyleProfilePayload input,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("MAF tools must not restore style profiles.");
        }

        public ValueTask<ReferenceStyleProfileComparisonPayload> CompareStyleProfilesAsync(
            CompareReferenceStyleProfilesPayload input,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("MAF tools must not compare style profiles.");
        }
    }

    private sealed class RecordingReferenceAnchorService : IReferenceAnchorService
    {
        public SearchReferenceMaterialsPayload? LastSearch { get; private set; }

        public ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
            CreateReferenceAnchorPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceAnchorPayload(
                7,
                input.NovelId,
                input.Title,
                input.Author ?? string.Empty,
                input.SourcePath,
                input.SourceKind,
                input.LicenseStatus,
                "hash",
                "test",
                ReferenceAnchorBuildStates.Ready,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        public async ValueTask<IReadOnlyList<ReferenceAnchorPayload>> CreateAnchorsAsync(
            CreateReferenceAnchorsPayload input,
            CancellationToken cancellationToken)
        {
            var anchors = new List<ReferenceAnchorPayload>(input.Anchors.Count);
            foreach (var anchor in input.Anchors)
            {
                anchors.Add(await CreateAnchorAsync(anchor, cancellationToken));
            }

            return anchors;
        }

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ReferenceAnchorPayload>>(
            [
                new ReferenceAnchorPayload(
                    7,
                    novelId,
                    "参考书",
                    "作者",
                    string.Empty,
                    "markdown",
                    "user_provided",
                    "hash",
                    "test",
                    ReferenceAnchorBuildStates.Ready,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow)
            ]);
        }

        public ValueTask<ReferenceAnchorPayload> PromoteAnchorToWorkspaceCorpusAsync(
            PromoteReferenceAnchorToWorkspaceCorpusPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceAnchorPayload(
                input.AnchorId,
                0,
                "参考书",
                "作者",
                string.Empty,
                "markdown",
                "user_provided",
                "hash",
                "test",
                ReferenceAnchorBuildStates.Ready,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                ReferenceCorpusVisibilities.Workspace,
                input.SourceTrust ?? ReferenceSourceTrustLevels.UserVerified,
                input.UserTags ?? []));
        }

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> PromoteAnchorsToWorkspaceCorpusAsync(
            PromoteReferenceAnchorsToWorkspaceCorpusPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ReferenceAnchorPayload>>(
                input.AnchorIds
                    .Select(anchorId => new ReferenceAnchorPayload(
                        anchorId,
                        0,
                        "参考书",
                        "作者",
                        string.Empty,
                        "markdown",
                        "user_provided",
                        "hash",
                        "test",
                        ReferenceAnchorBuildStates.Ready,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        ReferenceCorpusVisibilities.Workspace,
                        input.SourceTrust ?? ReferenceSourceTrustLevels.UserVerified,
                        input.UserTags ?? []))
                    .ToArray());
        }

        public ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
            UpdateReferenceAnchorMetadataPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceAnchorPayload(
                input.AnchorId,
                input.Visibility == ReferenceCorpusVisibilities.Workspace ? 0 : input.NovelId,
                input.Title,
                input.Author ?? string.Empty,
                string.Empty,
                "markdown",
                input.LicenseStatus,
                "hash",
                "test",
                ReferenceAnchorBuildStates.Ready,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                input.Visibility,
                input.SourceTrust,
                input.UserTags));
        }

        public ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceAnchorBuildStatusPayload(
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
            return ValueTask.FromResult<ReferenceAnchorBuildStatusPayload?>(new ReferenceAnchorBuildStatusPayload(
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

        public ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(
            SearchReferenceMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            LastSearch = input;
            return ValueTask.FromResult(new PageResultPayload<ReferenceMaterialPayload>(
                [
                    new ReferenceMaterialPayload(
                        "mat-1",
                        7,
                        "seg-1",
                        ReferenceMaterialTypes.Sentence,
                        "environment",
                        "pressure",
                        "rain",
                        "close",
                        "sensory",
                        1,
                        1,
                        1,
                        "雨声压低了整条街的呼吸。",
                        "hash",
                        "test",
                        false,
                        DateTimeOffset.UtcNow,
                        new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["story_context"] = 2.5,
                            ["prose_duty"] = 1.25
                        })
                ],
                Total: 1,
                Page: input.Page,
                Size: input.Size,
                TotalPages: 1));
        }

        public ValueTask<ReferenceMaterialDetailPayload?> GetMaterialDetailAsync(
            GetReferenceMaterialDetailPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ReferenceMaterialDetailPayload?>(null);
        }

        public ValueTask<ReferenceMaterialPayload> UpdateMaterialTagsAsync(
            UpdateReferenceMaterialTagsPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceMaterialPayload(
                input.MaterialId,
                7,
                "seg-1",
                ReferenceMaterialTypes.Sentence,
                input.FunctionTag ?? "environment",
                input.EmotionTag ?? "pressure",
                input.SceneTag ?? "rain",
                input.PovTag ?? "close",
                input.TechniqueTag ?? "sensory",
                1,
                1,
                1,
                "雨声压低了整条街的呼吸。",
                "hash",
                "test",
                true,
                DateTimeOffset.UtcNow));
        }

        public ValueTask<IReadOnlyList<ReferenceMaterialPayload>> UpdateMaterialsTagsAsync(
            UpdateReferenceMaterialsTagsPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ReferenceMaterialPayload>>(
                input.MaterialIds
                    .Select(materialId => new ReferenceMaterialPayload(
                        materialId,
                        7,
                        "seg-1",
                        ReferenceMaterialTypes.Sentence,
                        input.FunctionTag ?? "environment",
                        input.EmotionTag ?? "pressure",
                        input.SceneTag ?? "rain",
                        input.PovTag ?? "close",
                        input.TechniqueTag ?? "sensory",
                        1,
                        1,
                        1,
                        "雨声压低了整条街的呼吸。",
                        "hash",
                        "test",
                        true,
                        DateTimeOffset.UtcNow))
                    .ToArray());
        }

        public ValueTask<AdaptReferenceMaterialResultPayload> AdaptMaterialAsync(
            AdaptReferenceMaterialPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new AdaptReferenceMaterialResultPayload(
                "candidate-1",
                input.MaterialId,
                ReferenceRewriteLevels.L1,
                "雨声压低了整条街的呼吸。",
                input.SlotValues,
                [],
                new ReferenceReuseAuditPayload(
                    "audit-1",
                    "passed",
                    ReferenceRewriteLevels.L1,
                    [],
                    [],
                    [],
                    [],
                    [],
                    DateTimeOffset.UtcNow)));
        }

        public ValueTask<ReferenceReuseAuditPayload> AuditCandidateAsync(
            AuditReferenceReusePayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceReuseAuditPayload(
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
            return ValueTask.FromResult(new ReferenceUserFeedbackPayload(
                "feedback-1",
                input.NovelId,
                input.TargetType,
                input.TargetId,
                input.Decision,
                input.MaterialId,
                input.CandidateId,
                input.BlueprintId,
                input.BeatId,
                input.FeedbackTags,
                input.Note,
                string.IsNullOrWhiteSpace(input.EditedText) ? string.Empty : "edited-hash",
                input.Origin,
                DateTimeOffset.UtcNow));
        }

        public ValueTask<IReadOnlyList<ReferenceUserFeedbackPayload>> GetUserFeedbackAsync(
            GetReferenceUserFeedbackPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ReferenceUserFeedbackPayload>>([]);
        }

        public ValueTask DeleteAnchorAsync(long novelId, long anchorId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAnchorsAsync(
            DeleteReferenceAnchorsPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteMaterialsAsync(
            DeleteReferenceMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreMaterialsAsync(
            RestoreReferenceMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingReferenceAnchoredDraftService : IReferenceAnchoredDraftService
    {
        public BindReferenceBlueprintMaterialsPayload? LastBind { get; private set; }

        public GetReferenceAnchoredDraftAuditsPayload? LastGetAudits { get; private set; }

        public GetReferenceStyleAuditFindingsPayload? LastGetStyleAuditFindings { get; private set; }

        public StartReferenceOrchestrationRunPayload? LastStart { get; private set; }

        public long LastGetEventsNovelId { get; private set; }

        public string? LastGetEventsRunId { get; private set; }

        public ValueTask<ReferenceChapterBlueprintPayload> GenerateChapterBlueprintAsync(
            GenerateReferenceChapterBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new ReferenceChapterBlueprintPayload(
                101,
                input.NovelId,
                input.ChapterNumber,
                input.Title ?? "蓝图",
                ReferenceBlueprintStates.Draft,
                "next",
                "plan-hash",
                "context-hash",
                "analysis-hash",
                1,
                0,
                input.AnchorIds.FirstOrDefault(),
                input.ChapterGoal ?? string.Empty,
                new ReferenceChapterBlueprintAnalysisTrackPayload("logic", "logic", ["premise"]),
                new ReferenceChapterBlueprintAnalysisTrackPayload("emotion", "emotion", ["trigger"]),
                new ReferenceChapterBlueprintAnalysisTrackPayload("narration", "narration", ["distance"]),
                new ReferenceChapterBlueprintAnalysisTrackPayload("character", "character", ["goal"]),
                new ReferenceChapterBlueprintAnalysisTrackPayload("reference", "reference", ["query"]),
                new ReferenceChapterBlueprintAnalysisTrackPayload("transition", "transition", ["reason"]),
                new ReferenceChapterBlueprintExecutionTrackPayload("execution", "execution", ["intend"], ["dwell"], ["not script"], ["detail"], ["reject"]),
                "previous",
                "final",
                "hook",
                "pov",
                "close",
                input.KnownFacts,
                input.ForbiddenFacts,
                [],
                [],
                null,
                now,
                now));
        }

        public ValueTask<IReadOnlyList<ReferenceChapterBlueprintSummaryPayload>> GetChapterBlueprintsAsync(long novelId, int? chapterNumber, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ReferenceChapterBlueprintSummaryPayload>>([]);
        }

        public ValueTask<ReferenceChapterBlueprintPayload?> GetChapterBlueprintAsync(long novelId, long blueprintId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ReferenceChapterBlueprintPayload?>(null);
        }

        public ValueTask<ReferenceChapterBlueprintReviewPayload> ReviewChapterBlueprintAsync(
            ReviewReferenceChapterBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceChapterBlueprintReviewPayload(
                "review-1",
                input.BlueprintId,
                "context-hash",
                "plan-hash",
                "analysis-hash",
                1,
                ReferenceBlueprintReviewStatuses.Passed,
                1,
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                [],
                DateTimeOffset.UtcNow));
        }

        public ValueTask<ReferenceChapterBlueprintPayload> ReviseChapterBlueprintAsync(
            ReviseReferenceChapterBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            return GenerateChapterBlueprintAsync(
                new GenerateReferenceChapterBlueprintPayload(input.NovelId, 1, "修订蓝图", input.RevisionReason, [], [], []),
                cancellationToken);
        }

        public ValueTask<ReferenceChapterBlueprintPayload> ApproveChapterBlueprintAsync(
            ApproveReferenceChapterBlueprintPayload input,
            CancellationToken cancellationToken)
        {
            return GenerateChapterBlueprintAsync(
                new GenerateReferenceChapterBlueprintPayload(input.NovelId, 1, "批准蓝图", "approved", [], [], []),
                cancellationToken);
        }

        public ValueTask<ReferenceBlueprintMaterialBindingResultPayload> BindBlueprintMaterialsAsync(
            BindReferenceBlueprintMaterialsPayload input,
            CancellationToken cancellationToken)
        {
            LastBind = input;
            return ValueTask.FromResult(new ReferenceBlueprintMaterialBindingResultPayload(input.BlueprintId, []));
        }

        public ValueTask<ReferenceAnchoredDraftPayload> GenerateDraftFromBlueprintAsync(
            GenerateReferenceAnchoredDraftPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceAnchoredDraftPayload(input.BlueprintId, [], null));
        }

        public ValueTask<ReferenceAnchoredDraftAuditPayload> AuditDraftAgainstBlueprintAsync(
            AuditReferenceAnchoredDraftPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReferenceAnchoredDraftAuditPayload(
                "draft-audit-1",
                input.BlueprintId,
                "passed",
                ReferenceRewriteLevels.L1,
                [],
                [],
                [],
                [],
                [],
                [],
                DateTimeOffset.UtcNow));
        }

        public ValueTask<IReadOnlyList<ReferenceAnchoredDraftAuditPayload>> GetDraftAuditsAsync(
            GetReferenceAnchoredDraftAuditsPayload input,
            CancellationToken cancellationToken)
        {
            LastGetAudits = input;
            IReadOnlyList<ReferenceAnchoredDraftAuditPayload> audits =
            [
                new ReferenceAnchoredDraftAuditPayload(
                    "draft-audit-1",
                    input.BlueprintId,
                    "failed",
                    ReferenceRewriteLevels.L2,
                    ["weak provenance"],
                    [],
                    [],
                    [],
                    [],
                    ["Bind stronger reference material."],
                    DateTimeOffset.UtcNow,
                    ["candidate-1"],
                    new ReferenceDraftAuditReadableReportPayload(
                        "Persisted draft audit failed for 1 candidate.",
                        ["candidate-1"],
                        [
                            new ReferenceDraftAuditReadableFindingPayload(
                                "provenance",
                                "error",
                                ["candidate-1"],
                                "Candidate candidate-1 uses weak provenance.",
                                "Bind stronger reference material.")
                        ]))
            ];
            return ValueTask.FromResult(audits);
        }

        public ValueTask<IReadOnlyList<ReferenceStyleAuditFindingPayload>> GetStyleAuditFindingsAsync(
            GetReferenceStyleAuditFindingsPayload input,
            CancellationToken cancellationToken)
        {
            LastGetStyleAuditFindings = input;
            IReadOnlyList<ReferenceStyleAuditFindingPayload> findings =
            [
                new ReferenceStyleAuditFindingPayload(
                    "draft-audit-1",
                    input.BlueprintId,
                    "failed",
                    ReferenceRewriteLevels.L2,
                    ["candidate-1"],
                    "source_leak",
                    "required_fix",
                    "action",
                    "Source-leak risk for candidate candidate-1: longest exact shared phrase length 20 exceeded threshold.",
                    "Resolve source-leak risk before insertion.",
                    DateTimeOffset.UtcNow)
            ];
            return ValueTask.FromResult(findings);
        }

        public ValueTask<ReferenceOrchestrationRunPayload> StartOrchestrationRunAsync(
            StartReferenceOrchestrationRunPayload input,
            CancellationToken cancellationToken)
        {
            LastStart = input;
            return ValueTask.FromResult(BuildRun(input.NovelId, input.ChapterNumber, input.ChapterGoal ?? string.Empty));
        }

        public ValueTask<IReadOnlyList<ReferenceOrchestrationRunPayload>> GetOrchestrationRunsAsync(
            long novelId,
            int? chapterNumber,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<ReferenceOrchestrationRunPayload> runs = [BuildRun(novelId, chapterNumber ?? 1, "goal")];
            return ValueTask.FromResult(runs);
        }

        public ValueTask<ReferenceOrchestrationRunPayload?> GetOrchestrationRunAsync(
            long novelId,
            string runId,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ReferenceOrchestrationRunPayload?>(BuildRun(novelId, 1, "goal") with { RunId = runId });
        }

        public ValueTask<IReadOnlyList<ReferenceOrchestrationRunEventPayload>> GetOrchestrationRunEventsAsync(
            long novelId,
            string runId,
            CancellationToken cancellationToken)
        {
            LastGetEventsNovelId = novelId;
            LastGetEventsRunId = runId;
            IReadOnlyList<ReferenceOrchestrationRunEventPayload> events =
            [
                new ReferenceOrchestrationRunEventPayload(
                    17,
                    runId,
                    novelId,
                    "required_decision",
                    ReferenceOrchestrationStages.BlueprintApproval,
                    ReferenceOrchestrationRunStatuses.WaitingForUser,
                    ReferenceOrchestrationStopReasons.BlueprintApprovalRequired,
                    ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
                    "blueprint approval required",
                    DateTimeOffset.UtcNow)
            ];
            return ValueTask.FromResult(events);
        }

        public ValueTask<ReferenceOrchestrationRunPayload> ResumeOrchestrationRunAsync(
            ResumeReferenceOrchestrationRunPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(BuildRun(input.NovelId, 1, "goal") with
            {
                RunId = input.RunId,
                Status = ReferenceOrchestrationRunStatuses.Running,
                CurrentDecision = null
            });
        }

        public ValueTask<ReferenceOrchestrationRunPayload> CancelOrchestrationRunAsync(
            CancelReferenceOrchestrationRunPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(BuildRun(input.NovelId, 1, "goal") with
            {
                RunId = input.RunId,
                Status = ReferenceOrchestrationRunStatuses.Cancelled,
                LastStopReason = ReferenceOrchestrationStopReasons.Cancelled
            });
        }

        private static ReferenceOrchestrationRunPayload BuildRun(long novelId, int chapterNumber, string chapterGoal)
        {
            var now = DateTimeOffset.UtcNow;
            return new ReferenceOrchestrationRunPayload(
                "run-1",
                novelId,
                chapterNumber,
                ReferenceOrchestrationRunStatuses.WaitingForUser,
                ReferenceOrchestrationStages.SourceConfirmation,
                chapterGoal,
                [],
                [],
                [],
                new ReferenceCorpusSearchPolicyPayload("story_context", 3, ["user_provided"], [], []),
                0,
                string.Empty,
                [],
                new ReferenceOrchestrationRequiredDecisionPayload(
                    ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts,
                    ReferenceOrchestrationStopReasons.SourceConfirmationRequired,
                    "confirm source",
                    ["confirm_source", "confirm_license_status", "confirm_known_facts", "confirm_forbidden_facts"],
                    new ReferenceOrchestrationApprovalSummaryPayload("function", "pov", [], "emotion", "materials", "L2", [])),
                ReferenceOrchestrationStopReasons.SourceConfirmationRequired,
                string.Empty,
                now,
                now);
        }
    }

    private sealed class RecordingPreferenceService : IPreferenceService
    {
        public ValueTask<PreferenceResultPayload> GetPreferencesAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new PreferenceResultPayload([], []));
        }

        public ValueTask<PreferenceItemPayload> CreatePreferenceAsync(
            long novelId,
            CreatePreferencePayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new PreferenceItemPayload(1, novelId, input.IsGlobal, input.Category ?? string.Empty, input.Content, DateTimeOffset.UtcNow));
        }

        public ValueTask<PreferenceItemPayload> UpdatePreferenceAsync(
            long preferenceId,
            UpdatePreferencePayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new PreferenceItemPayload(preferenceId, 17, input.IsGlobal ?? false, input.Category ?? string.Empty, input.Content ?? "偏好", DateTimeOffset.UtcNow));
        }

        public ValueTask DeletePreferenceAsync(long preferenceId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWorldEntityService : IWorldEntityService
    {
        public long LastCreateCharacterNovelId { get; private set; }

        public CreateCharacterPayload? LastCreateCharacterInput { get; private set; }

        public ValueTask<IReadOnlyList<CharacterPayload>> GetCharactersAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<CharacterPayload>>([]);
        }

        public ValueTask<IReadOnlyList<CharacterRelationPayload>> GetCharacterRelationsAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<CharacterRelationPayload>>([]);
        }

        public ValueTask<IReadOnlyList<CharacterRelationPayload>> GetAllCharacterRelationsAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<CharacterRelationPayload>>([]);
        }

        public ValueTask<CharacterRelationPayload> UpdateCharacterRelationshipAsync(
            long novelId,
            UpdateCharacterRelationshipPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new CharacterRelationPayload(
                301,
                novelId,
                input.SourceCharacterId,
                input.TargetCharacterId,
                input.RelationDescribe ?? "关系",
                input.Description ?? string.Empty,
                input.ChapterId ?? 0,
                IsCurrent: true,
                DateTimeOffset.UtcNow));
        }

        public ValueTask<CharacterPayload> CreateCharacterAsync(
            long novelId,
            CreateCharacterPayload input,
            CancellationToken cancellationToken)
        {
            LastCreateCharacterNovelId = novelId;
            LastCreateCharacterInput = input;
            return ValueTask.FromResult(new CharacterPayload(
                101,
                novelId,
                input.Name,
                input.Description ?? string.Empty,
                input.Personality ?? string.Empty,
                input.Abilities ?? string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        public ValueTask UpdateCharacterAsync(long novelId, long characterId, UpdateCharacterPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteCharacterAsync(long novelId, long characterId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteCharacterRelationAsync(long novelId, long relationId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<LocationPayload>> GetLocationsAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<LocationPayload>>([]);
        }

        public ValueTask<IReadOnlyList<LocationRelationPayload>> GetLocationRelationsAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<LocationRelationPayload>>([]);
        }

        public ValueTask<LocationRelationPayload> CreateLocationRelationAsync(
            long novelId,
            CreateLocationRelationPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new LocationRelationPayload(
                401,
                novelId,
                input.LocationAId,
                input.LocationBId,
                input.RelationType,
                input.Description ?? string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }

        public ValueTask<LocationRelationPayload> UpdateLocationRelationAsync(
            long novelId,
            long relationId,
            UpdateLocationRelationPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new LocationRelationPayload(relationId, novelId, 1, 2, input.RelationType ?? "相邻", input.Description ?? string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public ValueTask<LocationPayload> CreateLocationAsync(long novelId, CreateLocationPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new LocationPayload(201, novelId, input.Name, input.LocationType ?? string.Empty, input.Description ?? string.Empty, input.DetailJson ?? string.Empty, input.ParentLocationId, input.Tags ?? string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public ValueTask UpdateLocationAsync(long novelId, long locationId, UpdateLocationPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteLocationAsync(long novelId, long locationId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteLocationRelationAsync(long novelId, long relationId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPlanningService : IPlanningService
    {
        public ValueTask<IReadOnlyList<ChapterPlanPayload>> GetChapterPlansAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ChapterPlanPayload>>([]);
        }

        public ValueTask UpdateChapterPlanAsync(long novelId, UpdateChapterPlanPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<TimelineEntryPayload>> GetTimelineEntriesAsync(long novelId, int fromChapter, int toChapter, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<TimelineEntryPayload>>([]);
        }

        public ValueTask<TimelineEntryPayload> CreateTimelineEntryAsync(long novelId, CreateTimelineEntryPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new TimelineEntryPayload(1, novelId, input.Category, "pending", input.Title, input.Content ?? string.Empty, input.DetailJson ?? string.Empty, input.TargetChapter, input.Importance ?? 3, input.SourceChapterId ?? 0, input.Source ?? "ai", 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public ValueTask UpdateTimelineEntryAsync(long novelId, long entryId, UpdateTimelineEntryPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteTimelineEntryAsync(long novelId, long entryId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<StoryArcPayload>> GetStoryArcsAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<StoryArcPayload>>([]);
        }

        public ValueTask<StoryArcPayload> CreateStoryArcAsync(long novelId, CreateStoryArcPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new StoryArcPayload(1, novelId, input.Name, input.Description ?? string.Empty, input.ArcType, input.Importance ?? 1, "active", string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public ValueTask UpdateStoryArcAsync(long novelId, long arcId, UpdateStoryArcPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteStoryArcAsync(long novelId, long arcId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<ArcNodePayload>> GetArcNodesAsync(long novelId, int fromChapter, int toChapter, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ArcNodePayload>>([]);
        }

        public ValueTask<ArcNodePayload> CreateArcNodeAsync(long novelId, CreateArcNodePayload input, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ArcNodePayload(1, novelId, input.StoryArcId, input.Title, input.Description ?? string.Empty, input.TargetChapter, 0, "pending", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        public ValueTask UpdateArcNodeAsync(long novelId, long nodeId, UpdateArcNodePayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteArcNodeAsync(long novelId, long nodeId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<ReaderPerspectivePayload>> GetReaderPerspectivesAsync(long novelId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ReaderPerspectivePayload>>([]);
        }

        public ValueTask<ReaderPerspectivePayload> CreateReaderPerspectiveAsync(long novelId, CreateReaderPerspectivePayload input, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new ReaderPerspectivePayload(1, novelId, input.Type, input.Content, input.RelatedTruth ?? string.Empty, input.PlantedChapter, input.RevealedChapter ?? 0, DateTimeOffset.UtcNow));
        }

        public ValueTask UpdateReaderPerspectiveAsync(long novelId, long perspectiveId, UpdateReaderPerspectivePayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteReaderPerspectiveAsync(long novelId, long perspectiveId, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed record RecordedBridgeEvent(string Name, JsonElement Payload);

    private sealed class RecordingBridgeEventSink : IBridgeEventSink
    {
        public List<RecordedBridgeEvent> Events { get; } = [];

        public ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken)
        {
            Events.Add(new RecordedBridgeEvent(
                name,
                JsonSerializer.SerializeToElement(payload ?? new { })));
            return ValueTask.CompletedTask;
        }
    }
}
