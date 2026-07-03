using System.Text.Json;
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

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
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
