using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Novelist.Agent;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ChatSessionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ChatCreatesSessionPersistsMessagesUpdatesSettingsAndEmitsOrderedEvents()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var completion = new ScriptedChatCompletionClient(
            [
                new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Thinking, "先构思"),
                new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "雾起"),
                new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "旧城。"),
                new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Usage,
                    string.Empty,
                    JsonSerializer.SerializeToElement(new { total_tokens = 7 }))
            ],
            title: "雾中旧城");
        var service = CreateService(options, novelService, settings, completion, events);

        var result = await service.ChatAsync(
            new ChatInputPayload(
                SessionId: "",
                NovelId: novel.Id,
                Message: "写一个开场",
                ProviderName: "test",
                ModelId: "model-a",
                ReasoningEffort: "high"),
            CancellationToken.None);

        Assert.StartsWith($"sess_{novel.Id}_", result.SessionId, StringComparison.Ordinal);
        Assert.Equal(1, result.TurnId);
        Assert.Equal("雾起旧城。", result.FinalText);

        var storedSettings = await settings.GetSettingsAsync(CancellationToken.None);
        Assert.Equal(result.SessionId, storedSettings.LastSessionId);

        var sessions = await service.GetSessionsAsync(new GetSessionsPayload(novel.Id, 1, 20, ""), CancellationToken.None);
        var meta = Assert.Single(sessions.Items);
        Assert.Equal(result.SessionId, meta.SessionId);
        Assert.Equal("雾中旧城", meta.Title);
        Assert.Equal(1, sessions.Total);
        Assert.Equal(1, sessions.TotalPages);

        var detail = await service.GetSessionAsync(result.SessionId, CancellationToken.None);
        Assert.Equal(novel.Id, detail.NovelId);
        Assert.Equal("model-a", detail.Model);
        Assert.Equal("high", detail.ReasoningEffort);
        Assert.Equal(1, detail.LastTurnId);
        Assert.NotNull(detail.Usage);
        Assert.Equal(7, detail.Usage.Value.GetProperty("total_tokens").GetInt32());

        var messages = await service.GetSessionMessagesAsync(result.SessionId, CancellationToken.None);
        Assert.Collection(
            messages,
            user =>
            {
                Assert.Equal("user", user.Role);
                Assert.Equal("写一个开场", user.Content);
                Assert.True(user.ToFrontend);
                Assert.True(user.ToApi);
            },
            assistant =>
            {
                Assert.Equal("assistant", assistant.Role);
                Assert.Equal("雾起旧城。", assistant.Content);
                Assert.Equal("先构思", assistant.ThinkingContent);
                Assert.True(assistant.ToFrontend);
                Assert.True(assistant.ToApi);
            });

        Assert.Equal(
            ["chat:session_created", "chat:started", "agent:1", "agent:1", "agent:1", "agent:1", "chat:title_updated"],
            events.Events.Select(item => item.Name).ToArray());
        Assert.Equal(1, events.Events[1].Payload.GetProperty("turn_id").GetInt32());
        Assert.Equal(1, events.Events[2].Payload.GetProperty("seq").GetInt32());
        Assert.Equal(0, events.Events[2].Payload.GetProperty("type").GetInt32());
        Assert.Equal(2, events.Events[3].Payload.GetProperty("type").GetInt32());
        Assert.Equal(4, events.Events[5].Payload.GetProperty("type").GetInt32());

        Assert.Single(completion.Requests);
        Assert.Equal("test", completion.Requests[0].ProviderName);
        Assert.Equal("model-a", completion.Requests[0].ModelId);
        Assert.Contains(completion.Requests[0].Messages, message =>
            message.Role == "user" && message.Content == "写一个开场");
    }

    [Fact]
    public async Task ChatCommitsPendingUserAndAiWorkspaceChanges()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var versionControl = new GitVersionControlService(options);
        var novelService = new FileSystemNovelService(options, settings, versionControl);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("版本对话", "", ""), CancellationToken.None);
        var workspace = Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(Path.Combine(workspace, "goink.md"), "user changed before chat");
        var completion = new WorkspaceMutatingCompletionClient(
            Path.Combine(workspace, "goink.md"),
            "ai changed during chat");
        var service = CreateService(
            options,
            novelService,
            settings,
            completion,
            new RecordingBridgeEventSink(),
            versionControl: versionControl);

        var result = await service.ChatAsync(
            new ChatInputPayload("", novel.Id, "继续", "test", "model-a", ""),
            CancellationToken.None);

        Assert.Equal(1, result.TurnId);
        var log = await versionControl.GetLogAsync(novel.Id, null, 10, CancellationToken.None);
        Assert.Contains(log, commit => commit.Message == "turn 1: user manual changes");
        Assert.Contains(log, commit => commit.Message == "turn 1: AI changes");
    }

    [Fact]
    public async Task ChatInjectsInitialSystemSkillContextAndSlashSkillPrompt()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "旧城疑案", "悬疑"), CancellationToken.None);
        var content = new FileSystemChapterContentService(options, novelService);
        await content.SaveContentAsync(
            new SaveContentPayload(novel.Id, "goink.md", "## 当前进展\n林岚正在调查旧城门。"),
            CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "skills"));
        await File.WriteAllTextAsync(
            Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "skills", "always-style.md"),
            """
            ---
            name: always-style
            description: 常驻风格
            mode: always
            ---

            保持冷峻克制的叙述风格。
            """);
        await File.WriteAllTextAsync(
            Path.Combine(options.DefaultDataDirectory, "novels", novel.Id.ToString(), "skills", "scene-tool.md"),
            """
            ---
            name: scene-tool
            description: 场景推进方法
            mode: auto
            ---

            用场景目标推进剧情。
            """);
        Directory.CreateDirectory(Path.Combine(options.DefaultDataDirectory, "skills"));
        await File.WriteAllTextAsync(
            Path.Combine(options.DefaultDataDirectory, "skills", "manual-review.md"),
            """
            ---
            name: manual-review
            description: 手动审稿指令
            mode: manual
            ---

            逐条检查动机、线索和章末钩子。
            """);

        var completion = new ScriptedChatCompletionClient(
            [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "收到。")],
            title: "技能上下文");
        var service = CreateService(
            options,
            novelService,
            settings,
            completion,
            new RecordingBridgeEventSink(),
            chapterContent: content);

        var result = await service.ChatAsync(
            new ChatInputPayload("", novel.Id, "/manual-review 请审一下第一章", "test", "model-a", ""),
            CancellationToken.None);

        var request = Assert.Single(completion.Requests);
        Assert.Contains(request.Messages, message =>
            message.Role == "system" &&
            message.Content.Contains("【常驻技能】", StringComparison.Ordinal) &&
            message.Content.Contains("保持冷峻克制", StringComparison.Ordinal));
        var catalog = Assert.Single(request.Messages, message =>
            message.Role == "system" &&
            message.Content.Contains("<available_skills>", StringComparison.Ordinal));
        Assert.Contains("scene-tool: 场景推进方法", catalog.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("manual-review", catalog.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("always-style", catalog.Content, StringComparison.Ordinal);
        Assert.Contains(request.Messages, message =>
            message.Role == "system" &&
            message.Content.Contains("书名：长夜档案", StringComparison.Ordinal) &&
            message.Content.Contains("林岚正在调查旧城门", StringComparison.Ordinal));

        var userMessages = request.Messages.Where(message => message.Role == "user").ToArray();
        Assert.Equal(2, userMessages.Length);
        Assert.Contains("用户启用了快捷指令「manual-review」", userMessages[0].Content, StringComparison.Ordinal);
        Assert.Contains("逐条检查动机", userMessages[0].Content, StringComparison.Ordinal);
        Assert.Equal("/manual-review 请审一下第一章", userMessages[1].Content);

        var frontendMessages = await service.GetSessionMessagesAsync(result.SessionId, CancellationToken.None);
        Assert.DoesNotContain(frontendMessages, message =>
            message.Content.Contains("逐条检查动机", StringComparison.Ordinal));
        Assert.Contains(frontendMessages, message =>
            message.Role == "user" &&
            message.Content == "/manual-review 请审一下第一章" &&
            message.ToFrontend);
    }

    [Fact]
    public async Task CompressContextRebuildsActiveVersionAndNextChatUsesSummary()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var completion = new ScriptedChatCompletionClient(
            [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "继续写。")],
            title: "压缩摘要");
        var service = CreateService(
            options,
            novelService,
            settings,
            completion,
            new RecordingBridgeEventSink());

        var first = await service.ChatAsync(
            new ChatInputPayload("", novel.Id, "第一轮设定", "test", "model-a", ""),
            CancellationToken.None);
        var compressed = await service.CompressContextAsync(
            new CompressInputPayload(first.SessionId, "test", "model-a"),
            CancellationToken.None);

        Assert.Equal(2, compressed.TurnId);
        var detail = await service.GetSessionAsync(first.SessionId, CancellationToken.None);
        Assert.Equal(2, detail.ActiveVersion);
        Assert.Equal(2, detail.LastTurnId);
        var messages = await service.GetSessionMessagesAsync(first.SessionId, CancellationToken.None);
        Assert.Contains(messages, message =>
            message.TurnId == 2 &&
            message.Role == "system" &&
            message.EventType == "compression" &&
            message.ToFrontend);

        await service.ChatAsync(
            new ChatInputPayload(first.SessionId, novel.Id, "继续", "test", "model-a", ""),
            CancellationToken.None);
        var nextRequest = completion.Requests.Last();
        Assert.Contains(nextRequest.Messages, message =>
            message.Role == "user" &&
            message.Content.Contains("上下文已压缩", StringComparison.Ordinal));
        Assert.Contains(nextRequest.Messages, message =>
            message.Role == "user" &&
            message.Content.Contains("压缩摘要", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatAutoCompressesWhenPreviousUsageExceedsBudget()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var completion = new ScriptedChatCompletionClient(
            [
                new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "收到。"),
                new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Usage,
                    string.Empty,
                    JsonSerializer.SerializeToElement(new { total_tokens = 30_000 }))
            ],
            title: "自动摘要");
        var service = CreateService(options, novelService, settings, completion, events);

        var first = await service.ChatAsync(
            new ChatInputPayload("", novel.Id, "第一轮", "test", "model-a", ""),
            CancellationToken.None);
        var detail = await service.GetSessionAsync(first.SessionId, CancellationToken.None);
        Assert.NotNull(detail.Usage);
        Assert.Equal(32_000, detail.Usage.Value.GetProperty("context_window").GetInt32());
        Assert.True(detail.Usage.Value.GetProperty("usage_ratio").GetDouble() > 80);

        await service.ChatAsync(
            new ChatInputPayload(first.SessionId, novel.Id, "第二轮", "test", "model-a", ""),
            CancellationToken.None);

        Assert.Contains(events.Events, item =>
            item.Name == "agent:2" &&
            item.Payload.GetProperty("type").GetInt32() == 6 &&
            item.Payload.GetProperty("compression_phase").GetString() == "compressing");
        Assert.Contains(events.Events, item =>
            item.Name == "agent:2" &&
            item.Payload.GetProperty("type").GetInt32() == 6 &&
            item.Payload.GetProperty("compression_phase").GetString() == "done" &&
            item.Payload.GetProperty("summary").GetString() == "自动摘要");
        Assert.Contains(completion.Requests.Last().Messages, message =>
            message.Role == "user" &&
            message.Content.Contains("自动摘要", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSessionsPaginatesAndSearchesMessageContent()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("群星边境", "", ""), CancellationToken.None);
        var service = CreateService(
            options,
            novelService,
            settings,
            new ScriptedChatCompletionClient([new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "收到")], "标题"),
            new RecordingBridgeEventSink());

        var first = await service.ChatAsync(new ChatInputPayload("", novel.Id, "alpha needle", "test", "model-a", ""), CancellationToken.None);
        var second = await service.ChatAsync(new ChatInputPayload("", novel.Id, "beta", "test", "model-a", ""), CancellationToken.None);

        var page = await service.GetSessionsAsync(new GetSessionsPayload(novel.Id, 1, 1, ""), CancellationToken.None);
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(second.SessionId, page.Items[0].SessionId);

        var search = await service.GetSessionsAsync(new GetSessionsPayload(novel.Id, 1, 20, "needle"), CancellationToken.None);
        var found = Assert.Single(search.Items);
        Assert.Equal(first.SessionId, found.SessionId);
    }

    [Fact]
    public async Task CancelChatStopsActiveStreamAndPersistsUserStoppedMarker()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("无声城", "", ""), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var service = CreateService(
            options,
            novelService,
            settings,
            new BlockingChatCompletionClient(),
            events);

        var chatTask = service.ChatAsync(
            new ChatInputPayload("", novel.Id, "持续生成", "test", "model-a", ""),
            CancellationToken.None).AsTask();

        var started = await events.WaitForEventAsync("chat:started", TimeSpan.FromSeconds(3));
        var sessionId = started.Payload.GetProperty("session_id").GetString()!;
        await service.CancelChatAsync(sessionId, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await chatTask);

        var messages = await service.GetSessionMessagesAsync(sessionId, CancellationToken.None);
        Assert.Contains(messages, message =>
            message.Role == "system" &&
            message.EventType == "user_stopped" &&
            message.ToFrontend);
    }

    [Fact]
    public async Task ChatExecutesToolCallsThroughMafExecutorAndPersistsLegacyToolMetadata()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var tools = new RecordingChatToolExecutor();
        var completion = new ToolLoopChatCompletionClient(
            [
                [
                    new ChatCompletionStreamEvent(
                        ChatCompletionStreamEventKind.ToolCall,
                        ToolCall: new ChatToolCall(
                            "call_memory_1",
                            "search_story_memory",
                            """{"query":"旧城门暗号","top_k":1}"""))
                ],
                [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "根据记忆继续。")]
            ],
            title: "暗号");
        var service = CreateService(options, novelService, settings, completion, events, tools);

        var result = await service.ChatAsync(
            new ChatInputPayload("", novel.Id, "查一下旧城门暗号", "test", "model-a", ""),
            CancellationToken.None);

        Assert.Equal("根据记忆继续。", result.FinalText);
        Assert.Equal(2, completion.Requests.Count);
        var toolDefinition = Assert.Single(completion.Requests[0].Tools!);
        Assert.Equal("search_story_memory", toolDefinition.Name);
        Assert.Contains("语义检索", toolDefinition.Description, StringComparison.Ordinal);

        Assert.NotNull(tools.LastCall);
        Assert.Equal(novel.Id, tools.LastNovelId);
        Assert.Equal("call_memory_1", tools.LastCall.Id);
        Assert.Equal("search_story_memory", tools.LastCall.Name);

        var toolMessage = Assert.Single(completion.Requests[1].Messages, message => message.Role == "tool");
        Assert.Equal("call_memory_1", toolMessage.ToolCallId);
        using (var toolContent = JsonDocument.Parse(toolMessage.Content))
        {
            Assert.Equal(
                "林岚发现暗号",
                toolContent.RootElement.GetProperty("data").GetProperty("content").GetString());
        }

        var toolEvents = events.Events
            .Where(item => item.Name == "agent:1" &&
                item.Payload.GetProperty("type").GetInt32() == 3)
            .Select(item => item.Payload)
            .ToArray();
        Assert.Collection(
            toolEvents,
            selected =>
            {
                Assert.Equal("selected", selected.GetProperty("phase").GetString());
                Assert.Equal("search_story_memory", selected.GetProperty("tool_name").GetString());
            },
            executing =>
            {
                Assert.Equal("executing", executing.GetProperty("phase").GetString());
                Assert.Equal("旧城门暗号", executing.GetProperty("tool_args").GetProperty("query").GetString());
            },
            completed =>
            {
                Assert.Equal("completed", completed.GetProperty("phase").GetString());
                Assert.True(completed.GetProperty("success").GetBoolean());
                Assert.Equal("搜索故事记忆", completed.GetProperty("display_text").GetString());
            });

        var messages = await service.GetSessionMessagesAsync(result.SessionId, CancellationToken.None);
        Assert.Collection(
            messages,
            user => Assert.Equal("user", user.Role),
            assistantToolCall =>
            {
                Assert.Equal("assistant", assistantToolCall.Role);
                using var metadata = JsonDocument.Parse(assistantToolCall.ExtraMetadata!);
                var call = metadata.RootElement.GetProperty("tool_calls")[0];
                Assert.Equal("call_memory_1", call.GetProperty("id").GetString());
                Assert.Equal("search_story_memory", call.GetProperty("function").GetProperty("name").GetString());
                Assert.Equal(
                    """{"query":"旧城门暗号","top_k":1}""",
                    call.GetProperty("function").GetProperty("arguments").GetString());
                var display = metadata.RootElement.GetProperty("tool_displays")[0];
                Assert.Equal("completed", display.GetProperty("phase").GetString());
                Assert.Equal("memory", display.GetProperty("activity_kind").GetString());
            },
            persistedTool =>
            {
                Assert.Equal("tool", persistedTool.Role);
                using var content = JsonDocument.Parse(persistedTool.Content);
                Assert.Equal("林岚发现暗号", content.RootElement.GetProperty("data").GetProperty("content").GetString());
                using var metadata = JsonDocument.Parse(persistedTool.ExtraMetadata!);
                Assert.Equal("call_memory_1", metadata.RootElement.GetProperty("tool_call_id").GetString());
                Assert.Equal("search_story_memory", metadata.RootElement.GetProperty("tool_name").GetString());
            },
            finalAssistant =>
            {
                Assert.Equal("assistant", finalAssistant.Role);
                Assert.Equal("根据记忆继续。", finalAssistant.Content);
            });
    }

    [Fact]
    public async Task ChatEditToolApprovedPersistsContentAndEmitsLegacyApprovalAndFileChangedEvents()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var approvals = new ToolApprovalCoordinator(events);
        var ragRefresh = new RecordingRagIndexRefreshNotifier();
        var content = new FileSystemChapterContentService(options, novelService, ragRefreshNotifier: ragRefresh);
        var chapter = await content.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        await content.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "林岚记下旧暗号"),
            CancellationToken.None);
        ragRefresh.Notifications.Clear();

        var completion = new ToolLoopChatCompletionClient(
            [
                [
                    new ChatCompletionStreamEvent(
                        ChatCompletionStreamEventKind.ToolCall,
                        ToolCall: new ChatToolCall(
                            "call_edit_1",
                            "edit",
                            """
                            {"path":"chapters/001.md","change_type":"search_replace","search_text":"旧暗号","new_content":"新暗号","reason":"补强伏笔"}
                            """))
                ],
                [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "已改好。")]
            ],
            title: "改暗号");
        var tools = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            content,
            approvals,
            events));
        var service = CreateService(options, novelService, settings, completion, events, tools, approvals);

        var chatTask = service.ChatAsync(
            new ChatInputPayload("", novel.Id, "把旧暗号改成新暗号", "test", "model-a", ""),
            CancellationToken.None).AsTask();

        var approvalEvent = await events.WaitForEventAsync(
            "agent:1",
            item => item.Payload.TryGetProperty("phase", out var phase) &&
                phase.GetString() == "awaiting_approval",
            TimeSpan.FromSeconds(3));
        Assert.Equal("edit", approvalEvent.Payload.GetProperty("tool_name").GetString());
        Assert.Equal("call_edit_1", approvalEvent.Payload.GetProperty("tool_id").GetString());
        Assert.Equal("file_edit", approvalEvent.Payload.GetProperty("activity_kind").GetString());
        var metadata = approvalEvent.Payload.GetProperty("metadata");
        Assert.Equal("file_edit", metadata.GetProperty("approval_type").GetString());
        var payload = metadata.GetProperty("payload");
        Assert.Equal("chapters/001.md", payload.GetProperty("path").GetString());
        Assert.Equal("search_replace", payload.GetProperty("change_type").GetString());
        Assert.Equal("补强伏笔", payload.GetProperty("reason").GetString());
        Assert.Equal("林岚记下旧暗号", payload.GetProperty("original").GetString());
        Assert.Equal("林岚记下新暗号", payload.GetProperty("modified").GetString());

        await approvals.CompleteAsync(
            new ToolApprovalDecisionPayload("call_edit_1", true, "可以"),
            CancellationToken.None);
        var result = await chatTask;

        Assert.Equal("已改好。", result.FinalText);
        Assert.Equal(
            "林岚记下新暗号",
            await content.GetContentAsync(novel.Id, "chapters/001.md", CancellationToken.None));
        Assert.Contains(events.Events, item =>
            item.Name == "file:changed" &&
            item.Payload.GetProperty("novel_id").GetInt64() == novel.Id &&
            item.Payload.GetProperty("path").GetString() == "chapters/001.md");
        var updatedChapter = Assert.Single(await content.GetChaptersAsync(novel.Id, CancellationToken.None));
        Assert.True(updatedChapter.WordCount > 0);
        var stale = Assert.Single(ragRefresh.Notifications);
        Assert.Equal(novel.Id, stale.NovelId);
        Assert.Contains("chapters/001.md", stale.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatEditToolRejectedReturnsFeedbackToModelWithoutWriting()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var approvals = new ToolApprovalCoordinator(events);
        var content = new FileSystemChapterContentService(options, novelService);
        var chapter = await content.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        await content.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "林岚记下旧暗号"),
            CancellationToken.None);
        var completion = new ToolLoopChatCompletionClient(
            [
                [
                    new ChatCompletionStreamEvent(
                        ChatCompletionStreamEventKind.ToolCall,
                        ToolCall: new ChatToolCall(
                            "call_edit_reject",
                            "edit",
                            """
                            {"path":"chapters/001.md","change_type":"search_replace","search_text":"旧暗号","new_content":"新暗号","reason":"补强伏笔"}
                            """))
                ],
                [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "我会按反馈重试。")]
            ],
            title: "拒绝修改");
        var tools = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            content,
            approvals,
            events));
        var service = CreateService(options, novelService, settings, completion, events, tools, approvals);

        var chatTask = service.ChatAsync(
            new ChatInputPayload("", novel.Id, "修改暗号", "test", "model-a", ""),
            CancellationToken.None).AsTask();
        await events.WaitForEventAsync(
            "agent:1",
            item => item.Payload.TryGetProperty("phase", out var phase) &&
                phase.GetString() == "awaiting_approval",
            TimeSpan.FromSeconds(3));

        await approvals.CompleteAsync(
            new ToolApprovalDecisionPayload("call_edit_reject", false, "不要改暗号"),
            CancellationToken.None);
        var result = await chatTask;

        Assert.Equal("我会按反馈重试。", result.FinalText);
        Assert.Equal(
            "林岚记下旧暗号",
            await content.GetContentAsync(novel.Id, "chapters/001.md", CancellationToken.None));
        Assert.DoesNotContain(events.Events, item => item.Name == "file:changed");
        var toolMessage = Assert.Single(completion.Requests[1].Messages, message => message.Role == "tool");
        using var toolContent = JsonDocument.Parse(toolMessage.Content);
        Assert.False(toolContent.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("审批未通过", toolContent.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("不要改暗号", toolContent.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelChatClearsPendingFileEditApproval()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var approvals = new ToolApprovalCoordinator(events);
        var content = new FileSystemChapterContentService(options, novelService);
        var chapter = await content.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        await content.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "林岚记下旧暗号"),
            CancellationToken.None);
        var completion = new ToolLoopChatCompletionClient(
            [
                [
                    new ChatCompletionStreamEvent(
                        ChatCompletionStreamEventKind.ToolCall,
                        ToolCall: new ChatToolCall(
                            "call_edit_cancel",
                            "edit",
                            """
                            {"path":"chapters/001.md","change_type":"search_replace","search_text":"旧暗号","new_content":"新暗号","reason":"补强伏笔"}
                            """))
                ],
                [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "不应执行")]
            ],
            title: "取消修改");
        var tools = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            content,
            approvals,
            events));
        var service = CreateService(options, novelService, settings, completion, events, tools, approvals);

        var chatTask = service.ChatAsync(
            new ChatInputPayload("", novel.Id, "修改暗号", "test", "model-a", ""),
            CancellationToken.None).AsTask();
        var started = await events.WaitForEventAsync("chat:started", TimeSpan.FromSeconds(3));
        var sessionId = started.Payload.GetProperty("session_id").GetString()!;
        await events.WaitForEventAsync(
            "agent:1",
            item => item.Payload.TryGetProperty("phase", out var phase) &&
                phase.GetString() == "awaiting_approval",
            TimeSpan.FromSeconds(3));

        await service.CancelChatAsync(sessionId, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await chatTask);
        Assert.Equal(0, approvals.PendingCount);
        Assert.Equal(
            "林岚记下旧暗号",
            await content.GetContentAsync(novel.Id, "chapters/001.md", CancellationToken.None));
    }

    [Fact]
    public async Task ChatRunSubagentStreamsNestedEventsPersistsMessagesAndReturnsReportToParentModel()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "旧城疑案", "悬疑"), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var content = new FileSystemChapterContentService(options, novelService);
        var chapter = await content.CreateChapterAsync(new CreateChapterPayload(novel.Id, "雾中来信"), CancellationToken.None);
        await content.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, "林岚在旧城门听见暗号。"),
            CancellationToken.None);
        await content.SaveContentAsync(
            new SaveContentPayload(novel.Id, "goink.md", "## 当前进展\n林岚正在调查旧城门。"),
            CancellationToken.None);

        var completion = new ToolLoopChatCompletionClient(
            [
                [
                    new ChatCompletionStreamEvent(
                        ChatCompletionStreamEventKind.ToolCall,
                        ToolCall: new ChatToolCall(
                            "call_sub_1",
                            "run_subagent",
                            """{"agent_type":"review","instruction":"审读第 1 章并指出风险"}"""))
                ],
                [
                    new ChatCompletionStreamEvent(
                        ChatCompletionStreamEventKind.ToolCall,
                        ToolCall: new ChatToolCall(
                            "call_read_sub_1",
                            "read",
                            """{"path":"chapters/001.md","include_lines":false}"""))
                ],
                [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "审稿报告：暗号动机需要补强。")],
                [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "已根据审稿报告整理。")]
            ],
            title: "审稿");
        var subagents = new DeferredSubagentRunner();
        var tools = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            content,
            approvals: null,
            events,
            subagents));
        var service = CreateService(
            options,
            novelService,
            settings,
            completion,
            events,
            tools,
            approvals: null,
            chapterContent: content);
        subagents.SetTarget(service);

        var result = await service.ChatAsync(
            new ChatInputPayload("", novel.Id, "请启动审稿子 Agent", "test", "model-a", ""),
            CancellationToken.None);

        Assert.Equal("已根据审稿报告整理。", result.FinalText);
        Assert.Equal(4, completion.Requests.Count);
        Assert.Contains(completion.Requests[0].Tools!, tool => tool.Name == "run_subagent");
        Assert.DoesNotContain(completion.Requests[1].Tools!, tool => tool.Name == "run_subagent");
        Assert.DoesNotContain(completion.Requests[1].Tools!, tool => tool.Name == "edit");
        Assert.Contains(completion.Requests[1].Tools!, tool => tool.Name == "read");
        Assert.Contains(completion.Requests[1].Messages, message =>
            message.Role == "system" &&
            message.Content.Contains("审稿 Agent", StringComparison.Ordinal));
        Assert.Contains(completion.Requests[1].Messages, message =>
            message.Role == "system" &&
            message.Content.Contains("林岚正在调查旧城门", StringComparison.Ordinal));
        Assert.Contains(completion.Requests[1].Messages, message =>
            message.Role == "user" &&
            message.Content == "审读第 1 章并指出风险");

        var subagentToolMessage = Assert.Single(completion.Requests[2].Messages, message => message.Role == "tool");
        Assert.Equal("call_read_sub_1", subagentToolMessage.ToolCallId);
        using (var readResult = JsonDocument.Parse(subagentToolMessage.Content))
        {
            Assert.Equal(
                "林岚在旧城门听见暗号。",
                readResult.RootElement.GetProperty("data").GetProperty("content").GetString());
        }

        var parentToolMessage = Assert.Single(completion.Requests[3].Messages, message => message.Role == "tool");
        Assert.Equal("call_sub_1", parentToolMessage.ToolCallId);
        using (var subagentResult = JsonDocument.Parse(parentToolMessage.Content))
        {
            Assert.True(subagentResult.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(
                "review",
                subagentResult.RootElement.GetProperty("data").GetProperty("agent_type").GetString());
            Assert.Equal(
                "审稿报告：暗号动机需要补强。",
                subagentResult.RootElement.GetProperty("data").GetProperty("report").GetString());
        }

        var runSubagentExecuting = events.Events.Single(item =>
            item.Name == "agent:1" &&
            item.Payload.TryGetProperty("tool_name", out var toolName) &&
            toolName.GetString() == "run_subagent" &&
            item.Payload.GetProperty("phase").GetString() == "executing");
        Assert.Equal(
            "review",
            runSubagentExecuting.Payload.GetProperty("metadata").GetProperty("agent_type").GetString());

        Assert.Contains(events.Events, item =>
            item.Name == "agent:1" &&
            item.Payload.TryGetProperty("sub_task_id", out var subTaskId) &&
            subTaskId.GetString() == "call_sub_1" &&
            item.Payload.GetProperty("type").GetInt32() == 3 &&
            item.Payload.GetProperty("tool_name").GetString() == "read");
        Assert.Contains(events.Events, item =>
            item.Name == "agent:1" &&
            item.Payload.TryGetProperty("sub_task_id", out var subTaskId) &&
            subTaskId.GetString() == "call_sub_1" &&
            item.Payload.GetProperty("type").GetInt32() == 2 &&
            item.Payload.GetProperty("data").GetString() == "审稿报告：暗号动机需要补强。");

        var messages = await service.GetSessionMessagesAsync(result.SessionId, CancellationToken.None);
        Assert.Contains(messages, message =>
            message.Role == "assistant" &&
            message.AgentType == "review" &&
            message.SubTaskId == "call_sub_1" &&
            message.Content == "审稿报告：暗号动机需要补强。");
        Assert.Contains(messages, message =>
            message.Role == "assistant" &&
            message.AgentType == "review" &&
            message.SubTaskId == "call_sub_1" &&
            message.ExtraMetadata?.Contains("call_read_sub_1", StringComparison.Ordinal) == true);
        Assert.Contains(messages, message =>
            message.Role == "assistant" &&
            message.AgentType == "main" &&
            message.ExtraMetadata?.Contains("run_subagent", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task SubagentToolDefinitionsAllowReadOnlyStructuredToolsOnly()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "旧城疑案", "悬疑"), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var content = new FileSystemChapterContentService(options, novelService);
        var preferences = new FileSystemPreferenceService(options, novelService);
        var world = new FileSystemWorldEntityService(options, novelService);
        var planning = new FileSystemPlanningService(options, novelService);
        var completion = new ToolLoopChatCompletionClient(
            [
                [
                    new ChatCompletionStreamEvent(
                        ChatCompletionStreamEventKind.ToolCall,
                        ToolCall: new ChatToolCall(
                            "call_memory_1",
                            "run_subagent",
                            """{"agent_type":"memory","instruction":"整理人物和地点"}"""))
                ],
                [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "记忆报告")],
                [new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "完成")]
            ],
            title: "子任务");
        var subagents = new DeferredSubagentRunner();
        var tools = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            content,
            approvals: null,
            events,
            subagents,
            preferences,
            world,
            planning,
            webFetch: new StaticWebFetchService(),
            webSearch: new StaticWebSearchService()));
        var service = CreateService(
            options,
            novelService,
            settings,
            completion,
            events,
            tools,
            approvals: null,
            chapterContent: content);
        subagents.SetTarget(service);

        await service.ChatAsync(
            new ChatInputPayload("", novel.Id, "启动记忆子任务", "test", "model-a", ""),
            CancellationToken.None);

        Assert.Contains(completion.Requests[0].Tools!, tool => tool.Name == "create_character");
        Assert.Contains(completion.Requests[0].Tools!, tool => tool.Name == "delete_record");
        Assert.Contains(completion.Requests[0].Tools!, tool => tool.Name == "web_fetch");
        Assert.Contains(completion.Requests[0].Tools!, tool => tool.Name == "web_search");

        var subagentTools = completion.Requests[1].Tools!.Select(tool => tool.Name).ToArray();
        Assert.Contains("get_chapter_list", subagentTools);
        Assert.Contains("get_preferences", subagentTools);
        Assert.Contains("get_characters", subagentTools);
        Assert.Contains("get_locations", subagentTools);
        Assert.Contains("get_timeline", subagentTools);
        Assert.Contains("get_story_arcs", subagentTools);
        Assert.Contains("get_reader_perspective", subagentTools);
        Assert.DoesNotContain("edit", subagentTools);
        Assert.DoesNotContain("web_fetch", subagentTools);
        Assert.DoesNotContain("web_search", subagentTools);
        Assert.DoesNotContain("run_subagent", subagentTools);
        Assert.DoesNotContain("create_character", subagentTools);
        Assert.DoesNotContain("update_character", subagentTools);
        Assert.DoesNotContain("create_timeline_entry", subagentTools);
        Assert.DoesNotContain("delete_record", subagentTools);
    }

    [Fact]
    public async Task CancelChatStopsActiveSubagentRunAndPersistsUserStoppedMarker()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("长夜档案", "", ""), CancellationToken.None);
        var events = new RecordingBridgeEventSink();
        var content = new FileSystemChapterContentService(options, novelService);
        var subagents = new DeferredSubagentRunner();
        var tools = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
            new EmptyStoryMemorySearchService(),
            content,
            approvals: null,
            events,
            subagents));
        var completion = new SubagentBlockingChatCompletionClient();
        var service = CreateService(
            options,
            novelService,
            settings,
            completion,
            events,
            tools,
            approvals: null,
            chapterContent: content);
        subagents.SetTarget(service);

        var chatTask = service.ChatAsync(
            new ChatInputPayload("", novel.Id, "启动长任务子 Agent", "test", "model-a", ""),
            CancellationToken.None).AsTask();
        var started = await events.WaitForEventAsync("chat:started", TimeSpan.FromSeconds(3));
        var sessionId = started.Payload.GetProperty("session_id").GetString()!;
        await events.WaitForEventAsync(
            "agent:1",
            item => item.Payload.TryGetProperty("sub_task_id", out var subTaskId) &&
                subTaskId.GetString() == "call_sub_blocking" &&
                item.Payload.GetProperty("type").GetInt32() == 2,
            TimeSpan.FromSeconds(3));

        await service.CancelChatAsync(sessionId, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await chatTask);
        var messages = await service.GetSessionMessagesAsync(sessionId, CancellationToken.None);
        Assert.Contains(messages, message =>
            message.Role == "system" &&
            message.EventType == "user_stopped" &&
            message.ToFrontend);
    }

    [Fact]
    public async Task BridgeChatHandlersDispatchRepresentativeMethodsAndValidationErrors()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var settings = new FileSystemAppSettingsService(options);
        var novelService = new FileSystemNovelService(options, settings);
        var novel = await novelService.CreateNovelAsync(new CreateNovelPayload("桥接测试", "", ""), CancellationToken.None);
        var service = CreateService(
            options,
            novelService,
            settings,
            new ScriptedChatCompletionClient([new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "桥接成功")], "桥接"),
            new RecordingBridgeEventSink());
        var dispatcher = new BridgeDispatcher()
            .RegisterCompatibilityAppMethodHandlers()
            .RegisterChatSessionHandlers(service);

        using var chat = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_chat",
              "method": "Chat",
              "payload": {
                "args": [
                  {
                    "session_id": "",
                    "novel_id": {{novel.Id}},
                    "message": "测试",
                    "provider_name": "test",
                    "model_id": "model-a",
                    "reasoning_effort": ""
                  }
                ]
              }
            }
            """));
        Assert.True(chat.RootElement.GetProperty("ok").GetBoolean());
        var sessionId = chat.RootElement.GetProperty("result").GetProperty("session_id").GetString();

        using var messages = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_messages",
              "method": "GetSessionMessages",
              "payload": { "args": ["{{sessionId}}"] }
            }
            """));
        Assert.Equal(2, messages.RootElement.GetProperty("result").GetArrayLength());

        using var invalid = ParseOutbound(await dispatcher.DispatchAsync($$"""
            {
              "kind": "request",
              "id": "req_invalid_chat",
              "method": "Chat",
              "payload": {
                "args": [
                  {
                    "session_id": "",
                    "novel_id": {{novel.Id}},
                    "message": "",
                    "provider_name": "test",
                    "model_id": "model-a",
                    "reasoning_effort": ""
                  }
                ]
              }
            }
            """));
        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            BridgeErrorCodes.ValidationError,
            invalid.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StandardChatCompletionClientPostsOpenAICompatibleStreamAndParsesSse()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var llm = new FileSystemLlmConfigurationService(options);
        await llm.SaveConfigAsync(
            new LlmConfigViewPayload([
                new ProviderViewPayload(
                    "custom",
                    "Custom",
                    "https://api.example.com/v1",
                    "chat",
                    "https://api.example.com/v1/chat/completions",
                    "sk-secret",
                    "",
                    "",
                    0.4,
                    "custom",
                    [],
                    [new ModelInfoPayload("model-a", "Model A", 32_000, 2_048, true, ["high"], false)])
            ]),
            CancellationToken.None);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                data: {"choices":[{"delta":{"reasoning_content":"想"}}]}
                data: {"choices":[{"delta":{"content":"你"}}]}
                data: {"usage":{"total_tokens":3},"choices":[]}
                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        });
        var client = new StandardChatCompletionClient(llm, new HttpClient(handler));

        var events = new List<ChatCompletionStreamEvent>();
        await foreach (var item in client.StreamChatAsync(
            new ChatCompletionRequest(
                "custom",
                "model-a",
                "high",
                [new ChatCompletionMessage("user", "hi")]),
            CancellationToken.None))
        {
            events.Add(item);
        }

        Assert.Collection(
            events,
            thinking =>
            {
                Assert.Equal(ChatCompletionStreamEventKind.Thinking, thinking.Kind);
                Assert.Equal("想", thinking.Data);
            },
            content =>
            {
                Assert.Equal(ChatCompletionStreamEventKind.Content, content.Kind);
                Assert.Equal("你", content.Data);
            },
            usage =>
            {
                Assert.Equal(ChatCompletionStreamEventKind.Usage, usage.Kind);
                Assert.Equal(3, usage.Usage!.Value.GetProperty("total_tokens").GetInt32());
            });

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.example.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", request.Headers.Authorization?.ToString());
        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("model-a", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hi", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task StandardChatCompletionClientPostsResponsesStreamAndParsesEvents()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var llm = new FileSystemLlmConfigurationService(options);
        await llm.SaveConfigAsync(
            new LlmConfigViewPayload([
                new ProviderViewPayload(
                    "custom",
                    "Custom",
                    "https://api.example.com/v1",
                    "responses",
                    "https://api.example.com/v1/responses",
                    "sk-secret",
                    "",
                    "",
                    0.4,
                    "custom",
                    [],
                    [new ModelInfoPayload("model-a", "Model A", 32_000, 2_048, false, [], false)])
            ]),
            CancellationToken.None);
        var handler = new RecordingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                data: {"type":"response.output_text.delta","delta":"你"}
                data: {"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_lookup","name":"lookup","arguments":"{\"q\":\"x\"}"}}
                data: {"type":"response.completed","response":{"usage":{"total_tokens":5}}}
                data: [DONE]

                """,
                Encoding.UTF8,
                "text/event-stream")
        });
        var client = new StandardChatCompletionClient(llm, new HttpClient(handler));

        var events = new List<ChatCompletionStreamEvent>();
        await foreach (var item in client.StreamChatAsync(
            new ChatCompletionRequest(
                "custom",
                "model-a",
                "",
                [new ChatCompletionMessage("user", "hi")],
                [
                    new ChatToolDefinition(
                        "lookup",
                        "Lookup",
                        JsonSerializer.SerializeToElement(new { type = "object" }))
                ]),
            CancellationToken.None))
        {
            events.Add(item);
        }

        Assert.Collection(
            events,
            content =>
            {
                Assert.Equal(ChatCompletionStreamEventKind.Content, content.Kind);
                Assert.Equal("你", content.Data);
            },
            tool =>
            {
                Assert.Equal(ChatCompletionStreamEventKind.ToolCall, tool.Kind);
                Assert.Equal("call_lookup", tool.ToolCall!.Id);
                Assert.Equal("lookup", tool.ToolCall.Name);
                Assert.Equal("""{"q":"x"}""", tool.ToolCall.ArgumentsJson);
            },
            usage =>
            {
                Assert.Equal(ChatCompletionStreamEventKind.Usage, usage.Kind);
                Assert.Equal(5, usage.Usage!.Value.GetProperty("total_tokens").GetInt32());
            });

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.example.com/v1/responses", request.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.RequestBodies.Single());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("model-a", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("hi", body.RootElement.GetProperty("input")[0].GetProperty("content").GetString());
        Assert.Equal("function", body.RootElement.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("messages", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileSystemChatSessionService CreateService(
        AppInitializationOptions options,
        INovelService novelService,
        IAppSettingsService settings,
        IChatCompletionClient completion,
        IBridgeEventSink events,
        IChatToolExecutor? tools = null,
        IApprovalCoordinator? approvals = null,
        IChapterContentService? chapterContent = null,
        IVersionControlService? versionControl = null)
    {
        return new FileSystemChatSessionService(
            options,
            novelService,
            settings,
            new StaticLlmConfigurationService(),
            completion,
            events,
            approvals,
            toolExecutor: tools,
            chapterContent: chapterContent,
            versionControl: versionControl);
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

    private sealed record RecordedBridgeEvent(string Name, JsonElement Payload);

    private sealed class RecordingBridgeEventSink : IBridgeEventSink
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, List<TaskCompletionSource<RecordedBridgeEvent>>> _waiters = new(StringComparer.Ordinal);

        public List<RecordedBridgeEvent> Events { get; } = [];

        public ValueTask EmitAsync(string name, object? payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = JsonSerializer.SerializeToElement(payload ?? new { }, BridgeJson.SerializerOptions);
            var recorded = new RecordedBridgeEvent(name, element);
            List<TaskCompletionSource<RecordedBridgeEvent>>? waiters = null;
            lock (_sync)
            {
                Events.Add(recorded);
                if (_waiters.Remove(name, out var pending))
                {
                    waiters = pending;
                }
            }

            if (waiters is not null)
            {
                foreach (var waiter in waiters)
                {
                    waiter.TrySetResult(recorded);
                }
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<RecordedBridgeEvent> WaitForEventAsync(string name, TimeSpan timeout)
        {
            Task<RecordedBridgeEvent> task;
            lock (_sync)
            {
                var existing = Events.FirstOrDefault(item => item.Name == name);
                if (existing is not null)
                {
                    return existing;
                }

                var source = new TaskCompletionSource<RecordedBridgeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_waiters.TryGetValue(name, out var waiters))
                {
                    waiters = [];
                    _waiters[name] = waiters;
                }

                waiters.Add(source);
                task = source.Task;
            }

            return await WaitAsync(task, timeout);
        }

        public async ValueTask<RecordedBridgeEvent> WaitForEventAsync(
            string name,
            Func<RecordedBridgeEvent, bool> predicate,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (_sync)
                {
                    var existing = Events.FirstOrDefault(item => item.Name == name && predicate(item));
                    if (existing is not null)
                    {
                        return existing;
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }

            throw new TimeoutException("Timed out waiting for matching bridge event.");
        }

        private static async ValueTask<RecordedBridgeEvent> WaitAsync(Task<RecordedBridgeEvent> task, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var completed = await Task.WhenAny(task, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
            if (completed != task)
            {
                throw new TimeoutException("Timed out waiting for bridge event.");
            }

            return await task;
        }
    }

    private sealed class StaticLlmConfigurationService : ILlmConfigurationService
    {
        public ValueTask<LlmConfigViewPayload> GetConfigAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new LlmConfigViewPayload([]));
        }

        public ValueTask SaveConfigAsync(LlmConfigViewPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<AvailableModelPayload>> GetModelsAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<AvailableModelPayload>>([
                new AvailableModelPayload("test/model-a", "Test", "Model A", 32_000, 2_048, true, ["high"], false)
            ]);
        }

        public ValueTask<IReadOnlyList<ModelInfoPayload>> DiscoverModelsAsync(
            string chatUrl,
            string apiKey,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<ModelInfoPayload>>([]);
        }

        public ValueTask TestConnectionAsync(TestConnectionPayload input, CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedChatCompletionClient : IChatCompletionClient
    {
        private readonly IReadOnlyList<ChatCompletionStreamEvent> _events;
        private readonly string _title;

        public ScriptedChatCompletionClient(IReadOnlyList<ChatCompletionStreamEvent> events, string title)
        {
            _events = events;
            _title = title;
        }

        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            foreach (var item in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_title);
        }
    }

    private sealed class WorkspaceMutatingCompletionClient : IChatCompletionClient
    {
        private readonly string _path;
        private readonly string _content;

        public WorkspaceMutatingCompletionClient(string path, string content)
        {
            _path = path;
            _content = content;
        }

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(_path, _content, cancellationToken);
            yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "完成");
        }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult("版本提交");
        }
    }

    private sealed class BlockingChatCompletionClient : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "开始");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult("不会执行");
        }
    }

    private sealed class ToolLoopChatCompletionClient : IChatCompletionClient
    {
        private readonly Queue<IReadOnlyList<ChatCompletionStreamEvent>> _turns;
        private readonly string _title;

        public ToolLoopChatCompletionClient(
            IEnumerable<IReadOnlyList<ChatCompletionStreamEvent>> turns,
            string title)
        {
            _turns = new Queue<IReadOnlyList<ChatCompletionStreamEvent>>(turns);
            _title = title;
        }

        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var events = _turns.Dequeue();
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_title);
        }
    }

    private sealed class SubagentBlockingChatCompletionClient : IChatCompletionClient
    {
        private int _streamCount;

        public List<ChatCompletionRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
            ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var count = Interlocked.Increment(ref _streamCount);
            if (count == 1)
            {
                yield return new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.ToolCall,
                    ToolCall: new ChatToolCall(
                        "call_sub_blocking",
                        "run_subagent",
                        """{"agent_type":"memory","instruction":"持续检索"}"""));
                yield break;
            }

            yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, "开始检索");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<string> GenerateTextAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult("不会执行");
        }
    }

    private sealed class RecordingChatToolExecutor : IChatToolExecutor
    {
        public long LastNovelId { get; private set; }

        public ChatToolCall? LastCall { get; private set; }

        public IReadOnlyList<ChatToolDefinition> GetToolDefinitions(long novelId)
        {
            return
            [
                new ChatToolDefinition(
                    "search_story_memory",
                    "语义检索小说记忆",
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string" }
                        },
                        required = new[] { "query" }
                    }))
            ];
        }

        public ValueTask<ChatToolExecutionResult> ExecuteAsync(
            ChatToolExecutionContext context,
            ChatToolCall call,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastNovelId = context.NovelId;
            LastCall = call;
            var data = JsonSerializer.SerializeToElement(new
            {
                query = "旧城门暗号",
                total = 1,
                content = "林岚发现暗号"
            });
            return ValueTask.FromResult(ChatToolExecutionResult.Succeeded(data));
        }
    }

    private sealed class StaticWebFetchService : IWebFetchService
    {
        public ValueTask<WebFetchResultPayload> FetchAsync(string url, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new WebFetchResultPayload(url, "网页", "正文"));
        }
    }

    private sealed class StaticWebSearchService : IWebSearchService
    {
        public ValueTask<WebSearchResultPayload> SearchAsync(string prompt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new WebSearchResultPayload([prompt], "摘要", []));
        }
    }

    private sealed class EmptyStoryMemorySearchService : IStoryMemorySearchService
    {
        public ValueTask<SearchStoryMemoryResultPayload> SearchAsync(
            SearchStoryMemoryPayload input,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new SearchStoryMemoryResultPayload(
                input.Query,
                Total: 0,
                Message: "未找到相关记忆",
                MaxRelevance: "0.00",
                Content: string.Empty,
                Results: []));
        }
    }

    private sealed class DeferredSubagentRunner : ISubagentRunner
    {
        private ISubagentRunner? _target;

        public void SetTarget(ISubagentRunner target)
        {
            _target = target;
        }

        public ValueTask<SubagentRunResult> RunAsync(
            SubagentRunRequest request,
            CancellationToken cancellationToken)
        {
            var target = _target ?? throw new InvalidOperationException("Subagent runner is not configured.");
            return target.RunAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingRagIndexRefreshNotifier : IRagIndexRefreshNotifier
    {
        public List<StaleNotification> Notifications { get; } = [];

        public ValueTask MarkNovelIndexStaleAsync(
            long novelId,
            string reason,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Notifications.Add(new StaleNotification(novelId, reason));
            return ValueTask.CompletedTask;
        }
    }

    private sealed record StaleNotification(long NovelId, string Reason);

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> RequestBodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }
}
