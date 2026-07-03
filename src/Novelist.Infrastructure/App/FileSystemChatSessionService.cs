using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemChatSessionService : IChatSessionService
{
    private const int MaxSessionIdLength = 512;
    private const int MaxMessageLength = 200_000;
    private const int MaxProviderNameLength = 128;
    private const int MaxModelIdLength = 256;
    private const int MaxReasoningEffortLength = 128;
    private const int MaxSearchLength = 512;
    private const int MaxTitleLength = 30;
    private const int MaxEventChunkChars = 8 * 1024;
    private const int MaxToolLoopCount = 8;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IAppSettingsService _settings;
    private readonly ILlmConfigurationService _llm;
    private readonly IChatCompletionClient _completion;
    private readonly IBridgeEventSink _events;
    private readonly IApprovalCoordinator? _approvals;
    private readonly IChatToolExecutor? _toolExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveChatOperation> _activeChats = new(StringComparer.Ordinal);

    public FileSystemChatSessionService(
        AppInitializationOptions? options,
        INovelService novels,
        IAppSettingsService settings,
        ILlmConfigurationService llm,
        IChatCompletionClient completion,
        IBridgeEventSink? events = null,
        IApprovalCoordinator? approvals = null,
        IChatToolExecutor? toolExecutor = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels;
        _settings = settings;
        _llm = llm;
        _completion = completion;
        _events = events ?? new NullBridgeEventSink();
        _approvals = approvals;
        _toolExecutor = toolExecutor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<PageResultPayload<SessionMetaPayload>> GetSessionsAsync(
        GetSessionsPayload input,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(input.NovelId);
        var page = input.Page < 1 ? 1 : input.Page;
        var size = input.Size is < 1 or > 100 ? 20 : input.Size;
        var search = NormalizeOptionalText(input.Search, nameof(input.Search), MaxSearchLength);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            IEnumerable<ChatSessionDocument> query = store.Sessions
                .Where(session => session.NovelId == input.NovelId);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var matchingSessionIds = store.Messages
                    .Where(message =>
                        message.SessionId.Length > 0 &&
                        message.Content.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .Select(message => message.SessionId)
                    .ToHashSet(StringComparer.Ordinal);
                query = query.Where(session => matchingSessionIds.Contains(session.SessionId));
            }

            var ordered = query
                .OrderByDescending(session => session.UpdatedAt)
                .ThenByDescending(session => session.CreatedAt)
                .ThenBy(session => session.SessionId, StringComparer.Ordinal)
                .ToArray();
            var total = ordered.LongLength;
            var items = ordered
                .Skip((page - 1) * size)
                .Take(size)
                .Select(ToSessionMetaPayload)
                .ToArray();
            var totalPages = size > 0
                ? (int)(total / size + (total % size == 0 ? 0 : 1))
                : 0;

            return new PageResultPayload<SessionMetaPayload>(items, total, page, size, totalPages);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<SessionDetailPayload> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId, allowEmpty: false);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, normalizedSessionId)
                ?? throw new ArgumentException($"Session '{normalizedSessionId}' does not exist.", nameof(sessionId));
            return ToSessionDetailPayload(session);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<SessionMessagePayload>> GetSessionMessagesAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId, allowEmpty: false);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.Messages
                .Where(message => string.Equals(message.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                    message.ToFrontend)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(ToMessagePayload)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ChatResultPayload> ChatAsync(
        ChatInputPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = NormalizeChatInput(input);

        await EnsureNovelExistsAsync(normalized.NovelId, cancellationToken);
        await EnsureModelConfiguredAsync(normalized.ProviderName, normalized.ModelId, cancellationToken);

        ChatSessionDocument session;
        bool isNew;
        int turnId;
        IReadOnlyList<ChatCompletionMessage> apiMessages;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var existingSession = FindSession(store, normalized.SessionId);
            isNew = existingSession is null;
            session = existingSession ?? CreateSession(normalized);
            if (isNew)
            {
                store.Sessions.Add(session);
            }

            turnId = checked(session.LastTurnId + 1);
            session.LastTurnId = turnId;
            session.UpdatedAt = UtcNow();

            store.Messages.Add(CreateMessage(
                store,
                session.SessionId,
                turnId,
                role: "user",
                content: normalized.Message,
                thinkingContent: null,
                version: session.ActiveVersion,
                toApi: true,
                toFrontend: true,
                eventType: null,
                agentType: "main"));

            apiMessages = store.Messages
                .Where(message => string.Equals(message.SessionId, session.SessionId, StringComparison.Ordinal) &&
                    message.ToApi &&
                    message.Version == session.ActiveVersion)
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id)
                .Select(ToCompletionMessage)
                .ToArray();

            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        if (isNew)
        {
            await _events.EmitAsync("chat:session_created", ToSessionDetailPayload(session), cancellationToken);
        }

        await _settings.SetLastSessionAsync(session.SessionId, cancellationToken);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ActiveChatOperation(linkedCancellation);
        RegisterActiveChat(session.SessionId, active);

        var finalText = string.Empty;
        JsonElement? usage = null;
        var seq = 0;
        var loopMessages = apiMessages.ToList();
        var toolDefinitions = _toolExecutor?.GetToolDefinitions(normalized.NovelId) ?? [];

        try
        {
            await _events.EmitAsync(
                "chat:started",
                new { session_id = session.SessionId, turn_id = turnId },
                linkedCancellation.Token);

            for (var loop = 0; loop < MaxToolLoopCount; loop++)
            {
                var roundText = new StringBuilder();
                var roundThinkingText = new StringBuilder();
                var toolOutputs = new List<ExecutedChatToolCall>();

                await foreach (var streamEvent in _completion.StreamChatAsync(
                    new ChatCompletionRequest(
                        normalized.ProviderName,
                        normalized.ModelId,
                        normalized.ReasoningEffort,
                        loopMessages,
                        toolDefinitions),
                    linkedCancellation.Token).WithCancellation(linkedCancellation.Token))
                {
                    switch (streamEvent.Kind)
                    {
                        case ChatCompletionStreamEventKind.Thinking:
                            roundThinkingText.Append(streamEvent.Data);
                            seq = await EmitAgentEventAsync(
                                turnId,
                                seq,
                                type: 0,
                                data: streamEvent.Data,
                                usage: null,
                                error: null,
                                linkedCancellation.Token);
                            break;
                        case ChatCompletionStreamEventKind.Content:
                            roundText.Append(streamEvent.Data);
                            seq = await EmitAgentEventAsync(
                                turnId,
                                seq,
                                type: 2,
                                data: streamEvent.Data,
                                usage: null,
                                error: null,
                                linkedCancellation.Token);
                            break;
                        case ChatCompletionStreamEventKind.Usage:
                            if (streamEvent.Usage is not null)
                            {
                                usage = streamEvent.Usage.Value.Clone();
                                seq = await EmitAgentEventAsync(
                                    turnId,
                                    seq,
                                    type: 4,
                                    data: null,
                                    usage: usage,
                                    error: null,
                                    linkedCancellation.Token);
                            }

                            break;
                        case ChatCompletionStreamEventKind.ToolCall:
                            if (streamEvent.ToolCall is null)
                            {
                                break;
                            }

                            var output = await ExecuteToolCallAsync(
                                normalized.NovelId,
                                session.SessionId,
                                turnId,
                                seq,
                                streamEvent.ToolCall,
                                linkedCancellation.Token);
                            seq = output.Seq;
                            toolOutputs.Add(output);
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported chat stream event kind '{streamEvent.Kind}'.");
                    }
                }

                if (toolOutputs.Count == 0)
                {
                    finalText = roundText.ToString();
                    await PersistAssistantMessageAsync(
                        session.SessionId,
                        turnId,
                        finalText,
                        roundThinkingText.ToString(),
                        usage,
                        cancellationToken);
                    break;
                }

                await PersistToolRoundAsync(
                    session.SessionId,
                    turnId,
                    roundText.ToString(),
                    roundThinkingText.ToString(),
                    usage,
                    toolOutputs,
                    cancellationToken);

                loopMessages.Add(new ChatCompletionMessage(
                    "assistant",
                    roundText.ToString(),
                    NullIfEmpty(roundThinkingText.ToString()),
                    toolOutputs.Select(output => output.Call).ToArray()));
                foreach (var output in toolOutputs)
                {
                    loopMessages.Add(new ChatCompletionMessage(
                        "tool",
                        FormatToolResultJson(output.Result),
                        ToolCallId: output.Call.Id,
                        ToolName: output.Call.Name));
                }

                if (loop == MaxToolLoopCount - 1)
                {
                    throw new BridgeRequestException(
                        BridgeErrorCodes.LlmProviderError,
                        "工具调用轮次过多，已中止。",
                        retryable: false);
                }
            }

            if (isNew)
            {
                await GenerateAndPersistTitleAsync(
                    session.SessionId,
                    normalized,
                    cancellationToken);
            }

            return new ChatResultPayload(session.SessionId, turnId, finalText);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            await PersistSystemEventAsync(
                session.SessionId,
                turnId,
                active.UserCancelled ? "user_stopped" : "system_interrupted",
                active.UserCancelled ? "对话已停止" : "对话被中断",
                CancellationToken.None);
            throw;
        }
        catch (BridgeRequestException ex)
        {
            await PersistAndEmitErrorAsync(session.SessionId, turnId, seq, ex.Message, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await PersistAndEmitErrorAsync(session.SessionId, turnId, seq, ex.Message, CancellationToken.None);
            throw new BridgeRequestException(
                BridgeErrorCodes.LlmProviderError,
                $"LLM 调用失败: {ex.Message}",
                retryable: true);
        }
        finally
        {
            if (_activeChats.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, active))
            {
                _activeChats.TryRemove(session.SessionId, out _);
            }
        }
    }

    public async ValueTask CancelChatAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedSessionId = NormalizeSessionId(sessionId, allowEmpty: true);
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        if (_activeChats.TryGetValue(normalizedSessionId, out var active))
        {
            active.UserCancelled = true;
            active.Cancellation.Cancel();
        }

        if (_approvals is not null)
        {
            await _approvals.CancelSessionAsync(normalizedSessionId, cancellationToken);
        }
    }

    private void RegisterActiveChat(string sessionId, ActiveChatOperation active)
    {
        if (_activeChats.TryRemove(sessionId, out var previous))
        {
            previous.UserCancelled = true;
            previous.Cancellation.Cancel();
        }

        _activeChats[sessionId] = active;
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private async ValueTask EnsureModelConfiguredAsync(
        string providerName,
        string modelId,
        CancellationToken cancellationToken)
    {
        var key = $"{providerName}/{modelId}";
        var models = await _llm.GetModelsAsync(cancellationToken);
        if (!models.Any(model => string.Equals(model.Key, key, StringComparison.Ordinal)))
        {
            throw new BridgeRequestException(
                BridgeErrorCodes.LlmProviderError,
                $"模型未找到或未配置: {key}",
                retryable: false,
                details: new { provider_name = providerName, model_id = modelId });
        }
    }

    private async ValueTask<int> EmitAgentEventAsync(
        int turnId,
        int currentSeq,
        int type,
        string? data,
        JsonElement? usage,
        string? error,
        CancellationToken cancellationToken)
    {
        var seq = currentSeq;
        foreach (var chunk in SplitEventData(data))
        {
            seq++;
            await _events.EmitAsync(
                $"agent:{turnId.ToString(CultureInfo.InvariantCulture)}",
                new AgentEventPayload
                {
                    TurnId = turnId,
                    Seq = seq,
                    Type = type,
                    Data = chunk,
                    Usage = usage,
                    Error = error,
                    Timestamp = UtcNow()
                },
                cancellationToken);
        }

        if (data is null)
        {
            seq++;
            await _events.EmitAsync(
                $"agent:{turnId.ToString(CultureInfo.InvariantCulture)}",
                new AgentEventPayload
                {
                    TurnId = turnId,
                    Seq = seq,
                    Type = type,
                    Usage = usage,
                    Error = error,
                    Timestamp = UtcNow()
                },
                cancellationToken);
        }

        return seq;
    }

    private async ValueTask<ExecutedChatToolCall> ExecuteToolCallAsync(
        long novelId,
        string sessionId,
        int turnId,
        int currentSeq,
        ChatToolCall call,
        CancellationToken cancellationToken)
    {
        var args = ParseToolArguments(call.ArgumentsJson);
        var activeDisplay = ToolDisplay.For(call.Name, active: true);
        var seq = await EmitToolEventAsync(
            turnId,
            currentSeq,
            call,
            phase: "selected",
            args: null,
            success: null,
            error: null,
            display: activeDisplay,
            cancellationToken);
        seq = await EmitToolEventAsync(
            turnId,
            seq,
            call,
            phase: "executing",
            args,
            success: null,
            error: null,
            display: activeDisplay,
            cancellationToken);

        ChatToolExecutionResult result;
        if (_toolExecutor is null)
        {
            result = ChatToolExecutionResult.Failure("Tool execution is not configured.");
        }
        else
        {
            try
            {
                result = await _toolExecutor.ExecuteAsync(
                    new ChatToolExecutionContext(novelId, sessionId, turnId),
                    call,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = ChatToolExecutionResult.Failure(ex.Message);
            }
        }

        var completedDisplay = ToolDisplay.For(call.Name, active: false);
        seq = await EmitToolEventAsync(
            turnId,
            seq,
            call,
            result.Success ? "completed" : "failed",
            args,
            result.Success,
            result.Error,
            completedDisplay,
            cancellationToken);

        return new ExecutedChatToolCall(
            call,
            result,
            completedDisplay.DisplayText,
            completedDisplay.ActivityKind,
            seq);
    }

    private async ValueTask<int> EmitToolEventAsync(
        int turnId,
        int currentSeq,
        ChatToolCall call,
        string phase,
        JsonElement? args,
        bool? success,
        string? error,
        ToolDisplay display,
        CancellationToken cancellationToken)
    {
        var seq = currentSeq + 1;
        await _events.EmitAsync(
            $"agent:{turnId.ToString(CultureInfo.InvariantCulture)}",
            new AgentEventPayload
            {
                TurnId = turnId,
                Seq = seq,
                Type = 3,
                ToolName = call.Name,
                ToolId = call.Id,
                Phase = phase,
                ToolArgs = args,
                Success = success,
                Error = string.IsNullOrWhiteSpace(error) ? null : error,
                DisplayText = display.DisplayText,
                ActivityKind = display.ActivityKind,
                Timestamp = UtcNow()
            },
            cancellationToken);
        return seq;
    }

    private async ValueTask PersistToolRoundAsync(
        string sessionId,
        int turnId,
        string assistantText,
        string thinkingText,
        JsonElement? usage,
        IReadOnlyList<ExecutedChatToolCall> toolOutputs,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during chat.");
            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "assistant",
                content: assistantText,
                thinkingContent: string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                version: session.ActiveVersion,
                toApi: true,
                toFrontend: true,
                eventType: null,
                agentType: "main",
                extraMetadata: BuildToolRoundMetadata(toolOutputs)));

            foreach (var output in toolOutputs)
            {
                store.Messages.Add(CreateMessage(
                    store,
                    sessionId,
                    turnId,
                    role: "tool",
                    content: FormatToolResultJson(output.Result),
                    thinkingContent: null,
                    version: session.ActiveVersion,
                    toApi: true,
                    toFrontend: true,
                    eventType: null,
                    agentType: "main",
                    extraMetadata: JsonSerializer.Serialize(
                        new Dictionary<string, object?>
                        {
                            ["tool_call_id"] = output.Call.Id,
                            ["tool_name"] = output.Call.Name
                        },
                        BridgeJson.SerializerOptions)));
            }

            if (usage is not null)
            {
                session.UsageJson = usage.Value.GetRawText();
            }

            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static string BuildToolRoundMetadata(IReadOnlyList<ExecutedChatToolCall> outputs)
    {
        var toolCalls = outputs.Select(output => new Dictionary<string, object?>
        {
            ["id"] = output.Call.Id,
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = output.Call.Name,
                ["arguments"] = string.IsNullOrWhiteSpace(output.Call.ArgumentsJson) ? "{}" : output.Call.ArgumentsJson
            }
        }).ToArray();

        var toolDisplays = outputs.Select(output => new Dictionary<string, object?>
        {
            ["tool_id"] = output.Call.Id,
            ["tool_name"] = output.Call.Name,
            ["display_text"] = output.DisplayText,
            ["activity_kind"] = output.ActivityKind,
            ["phase"] = output.Result.Success ? "completed" : "failed"
        }).ToArray();

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["tool_calls"] = toolCalls,
                ["tool_displays"] = toolDisplays
            },
            BridgeJson.SerializerOptions);
    }

    private static string FormatToolResultJson(ChatToolExecutionResult result)
    {
        var payload = new Dictionary<string, object?>
        {
            ["success"] = result.Success
        };
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            payload["error"] = result.Error;
        }

        if (result.Data is not null)
        {
            payload["data"] = result.Data.Value;
        }

        return JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
    }

    private static JsonElement ParseToolArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : JsonSerializer.SerializeToElement(new { }, BridgeJson.SerializerOptions);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { }, BridgeJson.SerializerOptions);
        }
    }

    private async ValueTask PersistAssistantMessageAsync(
        string sessionId,
        int turnId,
        string finalText,
        string thinkingText,
        JsonElement? usage,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during chat.");
            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "assistant",
                content: finalText,
                thinkingContent: string.IsNullOrEmpty(thinkingText) ? null : thinkingText,
                version: session.ActiveVersion,
                toApi: true,
                toFrontend: true,
                eventType: null,
                agentType: "main"));

            if (usage is not null)
            {
                session.UsageJson = usage.Value.GetRawText();
            }

            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask PersistSystemEventAsync(
        string sessionId,
        int turnId,
        string eventType,
        string content,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during chat.");
            store.Messages.Add(CreateMessage(
                store,
                sessionId,
                turnId,
                role: "system",
                content: content,
                thinkingContent: null,
                version: session.ActiveVersion,
                toApi: false,
                toFrontend: true,
                eventType: eventType,
                agentType: "main"));
            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask PersistAndEmitErrorAsync(
        string sessionId,
        int turnId,
        int seq,
        string message,
        CancellationToken cancellationToken)
    {
        await PersistSystemEventAsync(sessionId, turnId, "system_interrupted", message, cancellationToken);
        await EmitAgentEventAsync(
            turnId,
            seq,
            type: 5,
            data: null,
            usage: null,
            error: message,
            cancellationToken);
    }

    private async ValueTask GenerateAndPersistTitleAsync(
        string sessionId,
        ChatInputPayload input,
        CancellationToken cancellationToken)
    {
        var title = string.Empty;
        try
        {
            using var titleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            titleTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            title = await _completion.GenerateTextAsync(
                new ChatCompletionRequest(
                    input.ProviderName,
                    input.ModelId,
                    input.ReasoningEffort,
                    [
                        new ChatCompletionMessage(
                            "system",
                            "基于用户消息，生成一个不超过10个字的对话标题。只需输出标题文本，不要添加引号、标点或者额外解释。"),
                        new ChatCompletionMessage("user", input.Message)
                    ]),
                titleTimeout.Token);
        }
        catch
        {
            title = string.Empty;
        }

        title = NormalizeTitle(title);
        if (title.Length == 0)
        {
            title = NormalizeTitle(input.Message);
        }

        if (title.Length == 0)
        {
            return;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var session = FindSession(store, sessionId)
                ?? throw new InvalidOperationException($"Session '{sessionId}' disappeared during title generation.");
            if (session.Title.Length > 0)
            {
                return;
            }

            session.Title = title;
            session.UpdatedAt = UtcNow();
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        await _events.EmitAsync(
            "chat:title_updated",
            new { session_id = sessionId, title },
            cancellationToken);
    }

    private async ValueTask<ChatSessionStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new ChatSessionStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<ChatSessionStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Chat session store is empty or malformed.");
        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(ChatSessionStoreDocument store, CancellationToken cancellationToken)
    {
        ValidateStore(store);
        var path = await StorePathAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "sessions",
            "index.json");
    }

    private ChatSessionDocument CreateSession(ChatInputPayload input)
    {
        var now = UtcNow();
        return new ChatSessionDocument
        {
            SessionId = $"sess_{input.NovelId.ToString(CultureInfo.InvariantCulture)}_{now.ToUnixTimeMilliseconds():x}_{Guid.NewGuid():N}",
            NovelId = input.NovelId,
            Title = string.Empty,
            Model = input.ModelId,
            ReasoningEffort = input.ReasoningEffort,
            ActiveVersion = 1,
            LastTurnId = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private ChatMessageDocument CreateMessage(
        ChatSessionStoreDocument store,
        string sessionId,
        int turnId,
        string role,
        string content,
        string? thinkingContent,
        int version,
        bool toApi,
        bool toFrontend,
        string? eventType,
        string agentType,
        string? extraMetadata = null)
    {
        var id = AllocateMessageId(store);
        return new ChatMessageDocument
        {
            Id = id,
            SessionId = sessionId,
            TurnId = turnId,
            Role = role,
            Content = content,
            ThinkingContent = thinkingContent,
            Version = version,
            ToApi = toApi,
            ToFrontend = toFrontend,
            EventType = eventType,
            AgentType = agentType,
            ExtraMetadata = extraMetadata,
            CreatedAt = UtcNow()
        };
    }

    private static long AllocateMessageId(ChatSessionStoreDocument store)
    {
        var maxExisting = store.Messages.Count == 0 ? 0 : store.Messages.Max(message => message.Id);
        var next = Math.Max(store.NextMessageId, maxExisting + 1);
        if (next <= 0 || next == long.MaxValue)
        {
            throw new InvalidOperationException("Chat message id allocation is exhausted.");
        }

        store.NextMessageId = checked(next + 1);
        return next;
    }

    private static ChatSessionDocument? FindSession(ChatSessionStoreDocument store, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return store.Sessions.SingleOrDefault(session =>
            string.Equals(session.SessionId, sessionId, StringComparison.Ordinal));
    }

    private static ChatInputPayload NormalizeChatInput(ChatInputPayload input)
    {
        return input with
        {
            SessionId = NormalizeSessionId(input.SessionId, allowEmpty: true),
            Message = NormalizeRequiredText(input.Message, nameof(input.Message), MaxMessageLength, allowLineBreaks: true),
            ProviderName = NormalizeProviderName(input.ProviderName),
            ModelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), MaxModelIdLength, allowLineBreaks: false),
            ReasoningEffort = NormalizeOptionalText(input.ReasoningEffort, nameof(input.ReasoningEffort), MaxReasoningEffortLength)
        };
    }

    private static string NormalizeSessionId(string? value, bool allowEmpty)
    {
        var normalized = NormalizeOptionalText(value, nameof(value), MaxSessionIdLength);
        if (!allowEmpty && normalized.Length == 0)
        {
            throw new ArgumentException("Session id is required.", nameof(value));
        }

        if (normalized.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)))
        {
            throw new ArgumentException("Session id must not contain whitespace or control characters.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeProviderName(string? value)
    {
        var providerName = NormalizeRequiredText(
            value,
            nameof(value),
            MaxProviderNameLength,
            allowLineBreaks: false).ToLowerInvariant();
        if (providerName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider name may only contain letters, digits, hyphen, underscore, and dot.", nameof(value));
        }

        return providerName;
    }

    private static string NormalizeRequiredText(
        string? value,
        string name,
        int maxLength,
        bool allowLineBreaks)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        if (normalized.Any(ch => IsDisallowedControl(ch, allowLineBreaks)))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(ch => IsDisallowedControl(ch, allowLineBreaks: true)))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static bool IsDisallowedControl(char value, bool allowLineBreaks)
    {
        return char.IsControl(value) &&
            (!allowLineBreaks || value is not ('\r' or '\n' or '\t'));
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static string NormalizeTitle(string value)
    {
        var title = value.Trim().Trim('"', '\'', '“', '”', '‘', '’', '。', '，', ',', '.', '!', '！', '?', '？', ':', '：');
        if (title.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var rune in title.EnumerateRunes())
        {
            if (builder.Length >= MaxTitleLength)
            {
                break;
            }

            builder.Append(rune);
        }

        return builder.ToString();
    }

    private static IEnumerable<string?> SplitEventData(string? data)
    {
        if (data is null)
        {
            yield break;
        }

        if (data.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        for (var index = 0; index < data.Length; index += MaxEventChunkChars)
        {
            yield return data.Substring(index, Math.Min(MaxEventChunkChars, data.Length - index));
        }
    }

    private DateTimeOffset UtcNow()
    {
        return _timeProvider.GetUtcNow();
    }

    private static string FormatLegacyTime(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static SessionMetaPayload ToSessionMetaPayload(ChatSessionDocument session)
    {
        return new SessionMetaPayload(
            session.SessionId,
            session.Title,
            FormatLegacyTime(session.UpdatedAt));
    }

    private static SessionDetailPayload ToSessionDetailPayload(ChatSessionDocument session)
    {
        return new SessionDetailPayload(
            session.SessionId,
            session.NovelId,
            session.Title,
            session.Model,
            session.ReasoningEffort,
            session.ActiveVersion,
            session.LastTurnId,
            ParseUsage(session.UsageJson),
            FormatLegacyTime(session.CreatedAt),
            FormatLegacyTime(session.UpdatedAt));
    }

    private static SessionMessagePayload ToMessagePayload(ChatMessageDocument message)
    {
        return new SessionMessagePayload(
            message.Id,
            message.SessionId,
            message.TurnId,
            message.Role,
            message.Content,
            NullIfEmpty(message.ThinkingContent),
            message.TokenCount,
            NullIfEmpty(message.ExtraMetadata),
            message.Version,
            message.ToApi,
            message.ToFrontend,
            NullIfEmpty(message.EventType),
            message.AgentType,
            NullIfEmpty(message.SubTaskId),
            message.CreatedAt);
    }

    private static ChatCompletionMessage ToCompletionMessage(ChatMessageDocument message)
    {
        var metadata = ParseExtraMetadata(message.ExtraMetadata);
        return new ChatCompletionMessage(
            message.Role,
            message.Content,
            NullIfEmpty(message.ThinkingContent),
            ParseToolCalls(metadata),
            ReadString(metadata, "tool_call_id"),
            ReadString(metadata, "tool_name"));
    }

    private static JsonElement? ParseExtraMetadata(string? extraMetadata)
    {
        if (string.IsNullOrWhiteSpace(extraMetadata))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(extraMetadata);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ChatToolCall>? ParseToolCalls(JsonElement? metadata)
    {
        if (metadata is null ||
            !metadata.Value.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var results = new List<ChatToolCall>();
        foreach (var call in calls.EnumerateArray())
        {
            if (!call.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !call.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object ||
                !function.TryGetProperty("name", out var name) ||
                name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var arguments = "{}";
            if (function.TryGetProperty("arguments", out var args))
            {
                arguments = args.ValueKind == JsonValueKind.String
                    ? args.GetString() ?? "{}"
                    : args.GetRawText();
            }

            results.Add(new ChatToolCall(
                id.GetString() ?? string.Empty,
                name.GetString() ?? string.Empty,
                string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
        }

        return results.Count == 0 ? null : results;
    }

    private static string? ReadString(JsonElement? metadata, string propertyName)
    {
        return metadata is not null &&
            metadata.Value.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? NullIfEmpty(value.GetString())
                : null;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static JsonElement? ParseUsage(string? usageJson)
    {
        if (string.IsNullOrWhiteSpace(usageJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(usageJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateStore(ChatSessionStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported chat session store version '{store.Version}'.");
        }

        if (store.NextMessageId <= 0)
        {
            throw new InvalidOperationException("Chat session store next_message_id must be positive.");
        }

        if (store.Sessions.Select(session => session.SessionId).Distinct(StringComparer.Ordinal).Count() != store.Sessions.Count)
        {
            throw new InvalidOperationException("Chat session store contains duplicate session ids.");
        }

        if (store.Messages.Any(message => message.Id <= 0))
        {
            throw new InvalidOperationException("Chat session store contains an invalid message id.");
        }

        if (store.Messages.Select(message => message.Id).Distinct().Count() != store.Messages.Count)
        {
            throw new InvalidOperationException("Chat session store contains duplicate message ids.");
        }
    }

    private sealed record ExecutedChatToolCall(
        ChatToolCall Call,
        ChatToolExecutionResult Result,
        string DisplayText,
        string ActivityKind,
        int Seq);

    private sealed record ToolDisplay(string DisplayText, string ActivityKind)
    {
        public static ToolDisplay For(string toolName, bool active)
        {
            var (text, kind) = toolName switch
            {
                "search_story_memory" => ("搜索故事记忆", "memory"),
                _ => (toolName, "general")
            };
            return new ToolDisplay(active ? $"正在{text}" : text, kind);
        }
    }

    private sealed class ActiveChatOperation
    {
        public ActiveChatOperation(CancellationTokenSource cancellation)
        {
            Cancellation = cancellation;
        }

        public CancellationTokenSource Cancellation { get; }

        public bool UserCancelled { get; set; }
    }

    private sealed class ChatSessionStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_message_id")]
        public long NextMessageId { get; set; } = 1;

        [JsonPropertyName("sessions")]
        public List<ChatSessionDocument> Sessions { get; set; } = [];

        [JsonPropertyName("messages")]
        public List<ChatMessageDocument> Messages { get; set; } = [];
    }

    private sealed class ChatSessionDocument
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("novel_id")]
        public long NovelId { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("reasoning_effort")]
        public string ReasoningEffort { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("pending_changes")]
        public string PendingChanges { get; set; } = string.Empty;

        [JsonPropertyName("extra_metadata")]
        public string ExtraMetadata { get; set; } = string.Empty;

        [JsonPropertyName("active_version")]
        public int ActiveVersion { get; set; } = 1;

        [JsonPropertyName("last_turn_id")]
        public int LastTurnId { get; set; }

        [JsonPropertyName("usage")]
        public string UsageJson { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ChatMessageDocument
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("turn_id")]
        public int TurnId { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("thinking_content")]
        public string? ThinkingContent { get; set; }

        [JsonPropertyName("token_count")]
        public int TokenCount { get; set; }

        [JsonPropertyName("extra_metadata")]
        public string? ExtraMetadata { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("to_api")]
        public bool ToApi { get; set; }

        [JsonPropertyName("to_frontend")]
        public bool ToFrontend { get; set; }

        [JsonPropertyName("event_type")]
        public string? EventType { get; set; }

        [JsonPropertyName("agent_type")]
        public string AgentType { get; set; } = "main";

        [JsonPropertyName("sub_task_id")]
        public string? SubTaskId { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
