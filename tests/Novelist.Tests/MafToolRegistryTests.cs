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

        Assert.Equal(["search_story_memory", "read", "edit"], names);
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
