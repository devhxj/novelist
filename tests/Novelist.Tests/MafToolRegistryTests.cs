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
        Assert.Contains("generate_reference_chapter_blueprint", draftToolNames);
        Assert.Contains("generate_reference_anchored_draft", draftToolNames);

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
                """{"query":"雨夜压迫感","anchor_ids":[7],"material_types":["sentence"],"narrative_duties":["external_evidence"],"emotion_transitions":["neutral->pressure"],"page":1,"size":5}"""),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(anchors.LastSearch);
        Assert.Equal(23, anchors.LastSearch.NovelId);
        Assert.Equal("雨夜压迫感", anchors.LastSearch.Query);
        Assert.Equal([7], anchors.LastSearch.AnchorIds);
        Assert.Equal(["sentence"], anchors.LastSearch.MaterialTypes);
        Assert.Equal(["external_evidence"], anchors.LastSearch.NarrativeDuties);
        Assert.Equal(["neutral->pressure"], anchors.LastSearch.EmotionTransitions);
        Assert.Equal("mat-1", result.Data!.Value.GetProperty("items")[0].GetProperty("material_id").GetString());
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
                        DateTimeOffset.UtcNow)
                ],
                Total: 1,
                Page: input.Page,
                Size: input.Size,
                TotalPages: 1));
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
    }

    private sealed class RecordingReferenceAnchoredDraftService : IReferenceAnchoredDraftService
    {
        public BindReferenceBlueprintMaterialsPayload? LastBind { get; private set; }

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
