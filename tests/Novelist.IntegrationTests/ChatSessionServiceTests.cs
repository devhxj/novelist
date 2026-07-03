using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await chatTask);

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
        IBridgeEventSink events)
    {
        return new FileSystemChatSessionService(
            options,
            novelService,
            settings,
            new StaticLlmConfigurationService(),
            completion,
            events);
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
