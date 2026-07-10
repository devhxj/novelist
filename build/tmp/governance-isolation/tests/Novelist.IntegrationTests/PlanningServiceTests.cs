using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class PlanningServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ChapterPlansAndTimelineEntriesPersistWithRangeOrdering()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var service = new FileSystemPlanningService(options, novelService);

        var emptyPlans = await service.GetChapterPlansAsync(novel.Id, CancellationToken.None);
        Assert.Equal(["next", "near", "far"], emptyPlans.Select(plan => plan.Scope));
        Assert.All(emptyPlans, plan => Assert.Equal("", plan.Content));

        await service.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("near", "追查旧城线索"),
            CancellationToken.None);
        await service.UpdateChapterPlanAsync(
            novel.Id,
            new UpdateChapterPlanPayload("next", "林岚进入旧档案馆"),
            CancellationToken.None);

        await service.CreateTimelineEntryAsync(
            novel.Id,
            new CreateTimelineEntryPayload("foreshadowing", "低优先级", "内容A", "{}", 10, 2, 1, "ai"),
            CancellationToken.None);
        await service.CreateTimelineEntryAsync(
            novel.Id,
            new CreateTimelineEntryPayload("user_directive", "高优先级", "内容B", "", 10, 5, 0, ""),
            CancellationToken.None);
        var future = await service.CreateTimelineEntryAsync(
            novel.Id,
            new CreateTimelineEntryPayload("foreshadowing", "未来回收", "", "", 25, 3, 0, "user"),
            CancellationToken.None);

        await service.UpdateTimelineEntryAsync(
            novel.Id,
            future.Id,
            new UpdateTimelineEntryPayload(Status: "resolved", ResolvedChapterId: 25),
            CancellationToken.None);

        var reloaded = new FileSystemPlanningService(options, novelService);
        var plans = await reloaded.GetChapterPlansAsync(novel.Id, CancellationToken.None);
        Assert.Equal(["next", "near", "far"], plans.Select(plan => plan.Scope));
        Assert.Equal("林岚进入旧档案馆", plans.Single(plan => plan.Scope == "next").Content);
        Assert.Equal("追查旧城线索", plans.Single(plan => plan.Scope == "near").Content);

        var ranged = await reloaded.GetTimelineEntriesAsync(novel.Id, 10, 10, CancellationToken.None);
        Assert.Equal(["高优先级", "低优先级"], ranged.Select(entry => entry.Title));
        Assert.Equal("user", ranged[0].Source);

        var all = await reloaded.GetTimelineEntriesAsync(novel.Id, 0, 0, CancellationToken.None);
        Assert.Equal(["高优先级", "低优先级", "未来回收"], all.Select(entry => entry.Title));
        Assert.Equal("resolved", all.Single(entry => entry.Id == future.Id).Status);
        Assert.Equal(25, all.Single(entry => entry.Id == future.Id).ResolvedChapterId);

        await reloaded.DeleteTimelineEntryAsync(novel.Id, future.Id, CancellationToken.None);
        Assert.DoesNotContain(await reloaded.GetTimelineEntriesAsync(novel.Id, 0, 0, CancellationToken.None), entry => entry.Id == future.Id);
    }

    [Fact]
    public async Task StoryArcsAndNodesPersistWithOrderingAndCascadeDelete()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("群星边境", "", ""), CancellationToken.None);
        var service = new FileSystemPlanningService(options, novelService);

        var sub = await service.CreateStoryArcAsync(
            novel.Id,
            new CreateStoryArcPayload("支线", "sub", "边境补给线", 2),
            CancellationToken.None);
        var main = await service.CreateStoryArcAsync(
            novel.Id,
            new CreateStoryArcPayload("主线", "main", "舰队失踪", 5),
            CancellationToken.None);

        await service.UpdateStoryArcAsync(
            novel.Id,
            sub.Id,
            new UpdateStoryArcPayload(Status: "paused", ReactivateAt: "舰队抵达边境站"),
            CancellationToken.None);

        var nodeB = await service.CreateArcNodeAsync(
            novel.Id,
            new CreateArcNodePayload(main.Id, "发现残骸", "第一处证据", 12),
            CancellationToken.None);
        var nodeA = await service.CreateArcNodeAsync(
            novel.Id,
            new CreateArcNodePayload(main.Id, "收到求救信号", "", 8),
            CancellationToken.None);
        await service.CreateArcNodeAsync(
            novel.Id,
            new CreateArcNodePayload(sub.Id, "补给站断电", "", 6),
            CancellationToken.None);

        await service.UpdateArcNodeAsync(
            novel.Id,
            nodeA.Id,
            new UpdateArcNodePayload(Status: "completed", ActualChapter: 9),
            CancellationToken.None);

        var reloaded = new FileSystemPlanningService(options, novelService);
        var arcs = await reloaded.GetStoryArcsAsync(novel.Id, CancellationToken.None);
        Assert.Equal(["主线", "支线"], arcs.Select(arc => arc.Name));
        Assert.Equal("paused", arcs.Single(arc => arc.Id == sub.Id).Status);
        Assert.Equal("舰队抵达边境站", arcs.Single(arc => arc.Id == sub.Id).ReactivateAt);

        var rangedNodes = await reloaded.GetArcNodesAsync(novel.Id, 8, 12, CancellationToken.None);
        Assert.Equal([nodeA.Id, nodeB.Id], rangedNodes.Where(node => node.StoryArcId == main.Id).Select(node => node.Id));
        Assert.Equal("completed", rangedNodes.Single(node => node.Id == nodeA.Id).Status);
        Assert.Equal(9, rangedNodes.Single(node => node.Id == nodeA.Id).ActualChapter);

        await reloaded.DeleteStoryArcAsync(novel.Id, main.Id, CancellationToken.None);
        Assert.DoesNotContain(await reloaded.GetStoryArcsAsync(novel.Id, CancellationToken.None), arc => arc.Id == main.Id);
        Assert.DoesNotContain(await reloaded.GetArcNodesAsync(novel.Id, 0, 0, CancellationToken.None), node => node.StoryArcId == main.Id);
    }

    [Fact]
    public async Task ReaderPerspectivesPersistWithStoreOrderingAndCrud()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("误导叙事", "", ""), CancellationToken.None);
        var service = new FileSystemPlanningService(options, novelService);

        await service.CreateReaderPerspectiveAsync(
            novel.Id,
            new CreateReaderPerspectivePayload("suspense", "谁打开了门", 5, "阿七", 0),
            CancellationToken.None);
        var knownLater = await service.CreateReaderPerspectiveAsync(
            novel.Id,
            new CreateReaderPerspectivePayload("known", "林岚是记者", 3, "", 0),
            CancellationToken.None);
        var knownEarlier = await service.CreateReaderPerspectiveAsync(
            novel.Id,
            new CreateReaderPerspectivePayload("known", "旧城停电", 1, "", 0),
            CancellationToken.None);

        await service.UpdateReaderPerspectiveAsync(
            novel.Id,
            knownLater.Id,
            new UpdateReaderPerspectivePayload(RelatedTruth: "她也是当事人", RevealedChapter: 6),
            CancellationToken.None);

        var reloaded = new FileSystemPlanningService(options, novelService);
        var entries = await reloaded.GetReaderPerspectivesAsync(novel.Id, CancellationToken.None);
        Assert.Equal([knownEarlier.Id, knownLater.Id], entries.Where(entry => entry.Type == "known").Select(entry => entry.Id));
        Assert.Equal("她也是当事人", entries.Single(entry => entry.Id == knownLater.Id).RelatedTruth);
        Assert.Equal(6, entries.Single(entry => entry.Id == knownLater.Id).RevealedChapter);

        await reloaded.DeleteReaderPerspectiveAsync(novel.Id, knownEarlier.Id, CancellationToken.None);
        Assert.DoesNotContain(await reloaded.GetReaderPerspectivesAsync(novel.Id, CancellationToken.None), entry => entry.Id == knownEarlier.Id);
    }

    [Fact]
    public async Task BridgePlanningHandlersUseLegacyCompatibleArgumentShapes()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("桥接测试", "", ""), CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterPlanningHandlers(new FileSystemPlanningService(options, novelService));

        using var plan = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_plan",
              "method": "UpdateChapterPlan",
              "payload": { "args": [{{novel.Id}}, { "scope": "next", "content": "下一章计划" }] }
            }
            """));
        Assert.True(plan.RootElement.GetProperty("ok").GetBoolean());

        using var createTimeline = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_timeline",
              "method": "CreateTimelineEntry",
              "payload": { "args": [{{novel.Id}}, { "category": "foreshadowing", "title": "门后脚印", "target_chapter": 4 }] }
            }
            """));
        Assert.Equal("pending", createTimeline.RootElement.GetProperty("result").GetProperty("status").GetString());

        using var createArc = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_arc",
              "method": "CreateStoryArc",
              "payload": { "args": [{{novel.Id}}, { "name": "主线", "arc_type": "main" }] }
            }
            """));
        var arcId = createArc.RootElement.GetProperty("result").GetProperty("id").GetInt64();

        using var createNode = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_node",
              "method": "CreateArcNode",
              "payload": { "args": [{{novel.Id}}, { "story_arc_id": {{arcId}}, "title": "找到线索", "target_chapter": 6 }] }
            }
            """));
        Assert.Equal("pending", createNode.RootElement.GetProperty("result").GetProperty("status").GetString());

        using var createReader = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_reader",
              "method": "CreateReaderPerspective",
              "payload": { "args": [{{novel.Id}}, { "type": "suspense", "content": "凶手是谁", "planted_chapter": 2 }] }
            }
            """));
        var readerId = createReader.RootElement.GetProperty("result").GetProperty("id").GetInt64();

        using var updateReader = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_update_reader",
              "method": "UpdateReaderPerspective",
              "payload": { "args": [{{readerId}}, {{novel.Id}}, { "revealed_chapter": 7 }] }
            }
            """));
        Assert.True(updateReader.RootElement.GetProperty("ok").GetBoolean());

        using var deleteReader = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_delete_reader",
              "method": "DeleteReaderPerspective",
              "payload": { "args": [{{readerId}}, {{novel.Id}}] }
            }
            """));
        Assert.True(deleteReader.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task BridgePlanningHandlersReturnStableErrors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novelService = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("错误测试", "", ""), CancellationToken.None);
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterPlanningHandlers(new FileSystemPlanningService(options, novelService));

        using var invalidTimeline = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bad_timeline",
              "method": "CreateTimelineEntry",
              "payload": { "args": [{{novel.Id}}, { "category": "foreshadowing", "title": "   ", "target_chapter": 1 }] }
            }
            """));
        AssertBridgeError(invalidTimeline.RootElement, "req_bad_timeline", BridgeErrorCodes.ValidationError);

        using var invalidPlan = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_bad_plan",
              "method": "UpdateChapterPlan",
              "payload": { "args": [{{novel.Id}}, { "scope": "unknown", "content": "x" }] }
            }
            """));
        AssertBridgeError(invalidPlan.RootElement, "req_bad_plan", BridgeErrorCodes.ValidationError);
    }

    [Fact]
    public async Task BridgePlanningHandlersReturnStableErrorWhenAppIsNotInitialized()
    {
        var options = CreateOptions();
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterPlanningHandlers(new FileSystemPlanningService(
                options,
                new FileSystemNovelService(options, new FileSystemAppSettingsService(options))));

        var result = await dispatcher.DispatchAsync("""
            {
              "kind": "request",
              "id": "req_plans",
              "method": "GetChapterPlans",
              "payload": { "args": [1] }
            }
            """);

        using var json = ParseOutbound(result);
        AssertBridgeError(json.RootElement, "req_plans", BridgeErrorCodes.AppNotInitialized);
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

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static JsonDocument ParseOutbound(BridgeDispatchResult result)
    {
        Assert.Null(result.CancelRequestId);
        Assert.False(string.IsNullOrWhiteSpace(result.OutboundJson));
        return JsonDocument.Parse(result.OutboundJson);
    }

    private static void AssertBridgeError(JsonElement root, string expectedId, string expectedCode)
    {
        Assert.Equal("response", root.GetProperty("kind").GetString());
        Assert.Equal(expectedId, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal(expectedCode, root.GetProperty("error").GetProperty("code").GetString());
    }
}
