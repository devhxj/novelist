using System.Text.Json;
using Microsoft.Extensions.AI;
using Novelist.Agent;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Tests;

public sealed class MafToolRegistryTests
{
    private const string FullReferenceMaterialLeakSentinel = "__FULL_REFERENCE_MATERIAL_SHOULD_NOT_REACH_AGENT__";

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
    public void CreateToolsIncludesOnlyActiveReferenceToolsWhenServicesAreConfigured()
    {
        var withoutReferenceServices = new NovelistMafToolRegistry(new RecordingStoryMemorySearchService());
        Assert.DoesNotContain(
            withoutReferenceServices.CreateTools(new NovelistMafToolContext(17)),
            tool => tool.Name.Contains("reference", StringComparison.Ordinal));

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
            referenceMaterials: new RecordingReferenceMaterialSearch());

        var referenceTools = registry.CreateTools(new NovelistMafToolContext(17))
            .Where(tool => tool.Name.Contains("reference", StringComparison.Ordinal))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["get_reference_anchors", "search_reference_materials"],
            referenceTools.Select(tool => tool.Name));
        foreach (var tool in referenceTools)
        {
            Assert.True(tool.JsonSchema.TryGetProperty("properties", out var properties), tool.Name);
            Assert.False(properties.TryGetProperty("novel_id", out _), tool.Name);
            Assert.False(properties.TryGetProperty("source_path", out _), tool.Name);
            Assert.False(properties.TryGetProperty("path", out _), tool.Name);
        }
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
            referenceMaterials: new RecordingReferenceMaterialSearch());

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
    public async Task ReferenceMaterialToolsInjectNovelContext()
    {
        var anchors = new RecordingReferenceAnchorService();
        var materials = new RecordingReferenceMaterialSearch();
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
            referenceAnchors: anchors,
            referenceMaterials: materials));

        var anchorsResult = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall("call_reference_anchors", "get_reference_anchors", "{}"),
            CancellationToken.None);
        var searchResult = await executor.ExecuteAsync(
            new ChatToolExecutionContext(23, "sess_reference", 1),
            new ChatToolCall(
                "call_reference_search",
                "search_reference_materials",
                """{"query":"雨夜对峙","max_results":4}"""),
            CancellationToken.None);

        Assert.True(anchorsResult.Success, anchorsResult.Error);
        Assert.True(searchResult.Success, searchResult.Error);
        Assert.Equal(23, anchors.LastNovelId);
        Assert.NotNull(materials.LastSearch);
        Assert.Equal(23, materials.LastSearch.NovelId);
        Assert.Equal("雨夜对峙", materials.LastSearch.Query);
        Assert.Equal(4, materials.LastSearch.MaxResults);
        Assert.Equal(
            "material-1",
            searchResult.Data!.Value[0].GetProperty("materialId").GetString());
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

    private static void AssertReferenceToolResultDoesNotExposeSensitiveText(JsonElement result)
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
            FullReferenceMaterialLeakSentinel
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("redacted", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static string UnsafeReferenceDiagnosticText()
    {
        return @"source_path: D:\private\reference.md; source_text: secret source; candidate_text: generated candidate; prompt: hidden prompt; {""source_text"":""json secret source"",""candidate_text"":""json generated candidate"",""prompt"":""json hidden prompt"",""api_key"":""non-sk-secret-value"",""token"":""plain-token-value"",""authorization"":""Bearer jsonauthorizationtokenabcdefghijklmnopqrstuvwxyz""}; C:/Users/private/reference.md; \\server\share\secret.md; file://D:/private/reference.md; /Users/private/reference.md; token=dirty-token-abcdefghijklmnopqrstuvwxyz; api_key=sk-proj-dirtyabcdefghijklmnopqrstuvwxyz1234567890; Bearer dirtytokenabcdefghijklmnopqrstuvwxyz; " + FullReferenceMaterialLeakSentinel;
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


    private sealed class RecordingReferenceMaterialSearch : IReferenceMaterialSearch
    {
        public ReferenceMaterialSearchRequest? LastSearch { get; private set; }

        public ValueTask<ReferenceMaterialListPage> ListAsync(
            ReferenceMaterialListRequest input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchAsync(
            ReferenceMaterialSearchRequest input,
            CancellationToken cancellationToken)
        {
            LastSearch = input;
            IReadOnlyList<ReferenceMaterialSearchHit> hits =
            [
                new ReferenceMaterialSearchHit(
                    "material-1",
                    "generation-1",
                    7,
                    1,
                    0,
                    "dialogue_exchange",
                    "她没有回答。",
                    "用于承接对峙。",
                    ["dialogue"],
                    "text-hash",
                    0.12)
            ];
            return ValueTask.FromResult(hits);
        }
    }

    private sealed class RecordingReferenceAnchorService : IReferenceAnchorService
    {
        public long LastNovelId { get; private set; }

        public ValueTask<ReferenceAnchorPayload> RegisterMaterializationSourceAsync(
            RegisterReferenceMaterializationSourcePayload input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Anchor(input.NovelId, input.Title));

        public ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
            long novelId,
            CancellationToken cancellationToken)
        {
            LastNovelId = novelId;
            return ValueTask.FromResult<IReadOnlyList<ReferenceAnchorPayload>>(
                [Anchor(novelId, "参考书")]);
        }

        public ValueTask DeleteAnchorAsync(
            long novelId,
            long anchorId,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteAnchorsAsync(
            DeleteReferenceAnchorsPayload input,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
            UpdateReferenceAnchorMetadataPayload input,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(Anchor(input.NovelId, input.Title));

        private static ReferenceAnchorPayload Anchor(long novelId, string title) => new(
            7,
            novelId,
            title,
            "作者",
            string.Empty,
            "markdown",
            "user_provided",
            "hash",
            "whole-chapter-v1",
            ReferenceAnchorBuildStates.Ready,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
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
