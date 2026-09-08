using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class StandardChatCompletionClient : IChatCompletionClient
{
    private const int ErrorBodyLimitBytes = 64 * 1024;
    private const int SseLineLimitChars = 2 * 1024 * 1024;
    // 请求起步间隔：突发保护按单位时间请求量触发，同一供应商的请求起点至少间隔 2 秒，
    // 材料化并行批次的并发起点在这里自动排队，从源头避免触发保护。失败一律立即上抛，
    // 不做机器重试（冷却型保护越重试越糟）；是否重试由用户通过界面按钮决定。
    private const int MinProviderStartGapMs = 2_000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProviderStartGates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> ProviderLastRequestStart = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILlmConfigurationService _configuration;
    private readonly HttpClient _httpClient;

    public StandardChatCompletionClient(
        ILlmConfigurationService configuration,
        HttpClient? httpClient = null)
    {
        _configuration = configuration;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateMaxOutputTokens(request);
        var provider = await ResolveProviderAsync(request, cancellationToken);
        var payload = BuildPayload(provider, request, stream: true, titleGeneration: false);
        await PaceProviderStartAsync(provider.Key, cancellationToken);
        await foreach (var item in StreamAttemptAsync(provider, payload, cancellationToken))
        {
            if (item.Event is { } streamEvent)
            {
                yield return streamEvent;
                continue;
            }

            throw item.Failure ?? ProviderError("模型服务返回流式错误。", retryable: true);
        }
    }

    private async IAsyncEnumerable<AttemptItem> StreamAttemptAsync(
        ResolvedProvider provider,
        Dictionary<string, object?> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = CreateHttpRequest(provider, payload);
        HttpResponseMessage response;
        BridgeRequestException? transportFailure = null;
        try
        {
            response = await SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            response = null!;
            transportFailure = ProviderError($"请求失败: {ex.Message}", retryable: true);
        }

        if (transportFailure is not null)
        {
            yield return AttemptItem.Fail(transportFailure);
            yield break;
        }

        using (response)
        {
            if ((int)response.StatusCode >= 400)
            {
                var body = await ReadContentLimitedAsync(response.Content, cancellationToken);
                var failure = ProviderError(
                    FormatProviderError(response.StatusCode, body, provider.ApiKey),
                    Retryable(response.StatusCode));
                yield return AttemptItem.Fail(WithBurstProtectionGuidance(failure));
                yield break;
            }

            if (provider.EndpointType == LlmEndpoint.Responses)
            {
                await foreach (var item in ParseResponsesStreamItemsAsync(response.Content, cancellationToken))
                {
                    yield return item;
                }

                yield break;
            }

            var toolCalls = new Dictionary<int, StreamingToolCall>();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);
            while (true)
            {
                var line = await ReadLineOrFailOnIdleAsync(reader, cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (line.Length > SseLineLimitChars)
                {
                    yield return AttemptItem.Fail(ProviderError("服务商返回的 SSE 行过大，已拒绝处理。", retryable: false));
                    yield break;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line["data:".Length..].TrimStart();
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                {
                    foreach (var call in FlushToolCalls(toolCalls))
                    {
                        yield return AttemptItem.Item(new ChatCompletionStreamEvent(
                            ChatCompletionStreamEventKind.ToolCall,
                            ToolCall: call));
                    }

                    break;
                }

                foreach (var item in ParseSseData(data, toolCalls))
                {
                    yield return AttemptItem.Item(item);
                }
            }

            foreach (var call in FlushToolCalls(toolCalls))
            {
                yield return AttemptItem.Item(new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.ToolCall,
                    ToolCall: call));
            }
        }
    }

    // 流空闲看门狗：HTTP 客户端为流式设了无限超时，但静默断连（代理/NAT 掐断后
    // 既无数据也不关连接）会让 ReadLineAsync 永久阻塞——无人值守的材料化会整夜
    // 挂在 running。连续 StreamIdleTimeout 无任何字节即判定死连并快速失败。
    internal static TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    private static async Task<string?> ReadLineOrFailOnIdleAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = reader.ReadLineAsync(idle.Token).AsTask();
        var timer = Task.Delay(StreamIdleTimeout, CancellationToken.None);
        var completed = await Task.WhenAny(read, timer);
        if (completed != read)
        {
            idle.Cancel();
            throw ProviderError(
                $"模型服务连接已超过 {(int)StreamIdleTimeout.TotalSeconds} 秒没有任何数据（连接可能被静默断开），请重试。",
                retryable: true);
        }

        // 读取已完成（正常行、流结束或外部取消）；idle.Cancel 只用于释放计时关联。
        idle.Cancel();
        return await read;
    }

    private static async Task PaceProviderStartAsync(string providerKey, CancellationToken cancellationToken)
    {
        var gate = ProviderStartGates.GetOrAdd(providerKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var lastStart = ProviderLastRequestStart.TryGetValue(providerKey, out var value)
                ? value
                : DateTimeOffset.MinValue;
            var waitUntil = lastStart.AddMilliseconds(MinProviderStartGapMs);
            if (waitUntil > DateTimeOffset.UtcNow)
            {
                await Task.Delay(waitUntil - DateTimeOffset.UtcNow, cancellationToken);
            }

            ProviderLastRequestStart[providerKey] = DateTimeOffset.UtcNow;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<string> GenerateTextAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateMaxOutputTokens(request);
        var provider = await ResolveProviderAsync(request, cancellationToken);
        var payload = BuildPayload(provider, request, stream: false, titleGeneration: true);
        await PaceProviderStartAsync(provider.Key, cancellationToken);
        using var httpRequest = CreateHttpRequest(provider, payload);

        HttpResponseMessage response;
        try
        {
            response = await SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderError($"请求失败: {ex.Message}", retryable: true);
        }

        var body = await ReadContentLimitedAsync(response.Content, cancellationToken);
        if ((int)response.StatusCode >= 400)
        {
            throw WithBurstProtectionGuidance(ProviderError(
                FormatProviderError(response.StatusCode, body, provider.ApiKey),
                Retryable(response.StatusCode)));
        }

        return ParseGeneratedText(provider, body);
    }

    private static string ParseGeneratedText(ResolvedProvider provider, byte[] body)
    {
        if (provider.EndpointType == LlmEndpoint.Responses)
        {
            return ParseResponsesText(body);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                throw ProviderError("服务商返回了空的标题生成结果。", retryable: true);
            }

            var message = choices[0].GetProperty("message");
            return message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException ex)
        {
            throw ProviderError($"解析标题生成响应失败: {ex.Message}", retryable: false);
        }
    }

    private async ValueTask<ResolvedProvider> ResolveProviderAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        var providerName = NormalizeProviderName(request.ProviderName);
        var modelId = NormalizeRequiredText(request.ModelId, nameof(request.ModelId), maxLength: 256);
        var config = await _configuration.GetConfigAsync(cancellationToken);
        var provider = config.Providers.SingleOrDefault(item =>
            string.Equals(item.Key, providerName, StringComparison.Ordinal));
        if (provider is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw ProviderError($"模型供应商未配置 API Key: {providerName}", retryable: false);
        }

        var models = provider.BuiltinModels.Concat(provider.CustomModels).ToArray();
        var model = models.SingleOrDefault(item => string.Equals(item.Id, modelId, StringComparison.Ordinal));
        if (model is null)
        {
            throw ProviderError($"模型未找到: {providerName}/{modelId}", retryable: false);
        }

        var endpointType = LlmEndpoint.NormalizeEndpointType(provider.EndpointType);
        var baseUrl = LlmEndpoint.NormalizeBaseUrl(
            string.IsNullOrWhiteSpace(provider.BaseUrl) ? provider.ChatUrl : provider.BaseUrl,
            requireValue: true);
        var endpointUrl = LlmEndpoint.BuildEndpointUrl(baseUrl, endpointType);
        return new ResolvedProvider(
            providerName,
            endpointType,
            endpointUrl,
            provider.ApiKey.Trim(),
            provider.Temperature,
            model);
    }

    private static Dictionary<string, object?> BuildPayload(
        ResolvedProvider provider,
        ChatCompletionRequest request,
        bool stream,
        bool titleGeneration)
    {
        if (provider.EndpointType == LlmEndpoint.Responses)
        {
            return BuildResponsesPayload(provider, request, stream, titleGeneration);
        }

        var messages = request.Messages.Select(message =>
        {
            var item = new Dictionary<string, object?>
            {
                ["role"] = NormalizeRequiredText(message.Role, nameof(message.Role), 32)
            };

            if (message.Role == "tool")
            {
                item["content"] = message.Content ?? string.Empty;
                item["tool_call_id"] = NormalizeRequiredText(message.ToolCallId, nameof(message.ToolCallId), 512);
                return item;
            }

            item["content"] = message.Content ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(message.ThinkingContent))
            {
                item["reasoning_content"] = message.ThinkingContent;
            }

            if (message.ToolCalls is { Count: > 0 })
            {
                item["tool_calls"] = message.ToolCalls.Select(ToOpenAiToolCall).ToArray();
            }

            return item;
        }).ToArray();

        var maxOutputTokens = ResolveMaxOutputTokens(provider, request, titleGeneration);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = provider.Model.Id,
            ["messages"] = messages,
            ["stream"] = stream,
            ["temperature"] = ResolveTemperature(provider, request),
            ["max_tokens"] = titleGeneration
 ? maxOutputTokens
 : maxOutputTokens
        };

        if (stream)
        {
            payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        }

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(ToOpenAiToolDefinition).ToArray();
            payload["tool_choice"] = request.RequireToolCall ? "required" : "auto";
        }

        if (provider.Model.SupportsThinking)
        {
            payload["thinking"] = new Dictionary<string, object?> { ["type"] = "enabled" };
            var reasoningEffort = NormalizeOptionalText(request.ReasoningEffort, nameof(request.ReasoningEffort), 128);
            if (reasoningEffort.Length == 0 && provider.Model.ReasoningLevels is { Count: > 0 })
            {
                reasoningEffort = provider.Model.ReasoningLevels[0];
            }

            if (reasoningEffort.Length > 0)
            {
                payload["reasoning_effort"] = reasoningEffort;
            }
        }

        return ApplyProviderRequestAdapter(provider.Key, payload);
    }

    private static Dictionary<string, object?> BuildResponsesPayload(
        ResolvedProvider provider,
        ChatCompletionRequest request,
        bool stream,
        bool titleGeneration)
    {
        var maxOutputTokens = ResolveMaxOutputTokens(provider, request, titleGeneration);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = provider.Model.Id,
            ["input"] = ToResponsesInput(request.Messages),
            ["stream"] = stream,
            ["temperature"] = ResolveTemperature(provider, request),
            ["max_output_tokens"] = titleGeneration
 ? maxOutputTokens
 : maxOutputTokens
        };

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(ToResponsesToolDefinition).ToArray();
            payload["tool_choice"] = request.RequireToolCall ? "required" : "auto";
        }

        if (provider.Model.SupportsThinking)
        {
            var reasoningEffort = NormalizeOptionalText(request.ReasoningEffort, nameof(request.ReasoningEffort), 128);
            if (reasoningEffort.Length == 0 && provider.Model.ReasoningLevels is { Count: > 0 })
            {
                reasoningEffort = provider.Model.ReasoningLevels[0];
            }

            if (reasoningEffort.Length > 0)
            {
                payload["reasoning"] = new Dictionary<string, object?> { ["effort"] = reasoningEffort };
            }
        }

        return payload;
    }

    private static IReadOnlyList<Dictionary<string, object?>> ToResponsesInput(
 IReadOnlyList<ChatCompletionMessage> messages)
    {
        var input = new List<Dictionary<string, object?>>();
        foreach (var message in messages)
        {
            var role = NormalizeRequiredText(message.Role, nameof(message.Role), 32);
            if (role == "tool")
            {
                input.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = NormalizeRequiredText(message.ToolCallId, nameof(message.ToolCallId), 512),
                    ["output"] = message.Content ?? string.Empty
                });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                input.Add(new Dictionary<string, object?>
                {
                    ["role"] = role,
                    ["content"] = message.Content
                });
            }

            if (message.ToolCalls is not { Count: > 0 })
            {
                continue;
            }

            foreach (var call in message.ToolCalls)
            {
                input.Add(new Dictionary<string, object?>
                {
                    ["type"] = "function_call",
                    ["call_id"] = NormalizeRequiredText(call.Id, nameof(call.Id), 512),
                    ["name"] = NormalizeRequiredText(call.Name, nameof(call.Name), 128),
                    ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
                });
            }
        }

        return input;
    }

    private HttpRequestMessage CreateHttpRequest(
        ResolvedProvider provider,
        Dictionary<string, object?> payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, provider.EndpointUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var headers = ApplyProviderHeaderAdapter(provider.Key, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {provider.ApiKey}"
        });
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }

    private async ValueTask<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static IEnumerable<ChatCompletionStreamEvent> ParseSseData(
        string data,
        Dictionary<int, StreamingToolCall> toolCalls)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                yield return new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Usage,
                    string.Empty,
                    usage.Clone());
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                yield break;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta) ||
                delta.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            if (delta.TryGetProperty("reasoning_content", out var reasoning) &&
                reasoning.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(reasoning.GetString()))
            {
                yield return new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Thinking,
                    reasoning.GetString()!);
            }

            if (delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(content.GetString()))
            {
                yield return new ChatCompletionStreamEvent(
                    ChatCompletionStreamEventKind.Content,
                    content.GetString()!);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCallsElement) &&
                toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                AccumulateToolCalls(toolCallsElement, toolCalls);
            }
        }
    }

    private static void AccumulateToolCalls(
        JsonElement toolCallsElement,
        Dictionary<int, StreamingToolCall> toolCalls)
    {
        foreach (var item in toolCallsElement.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var indexElement) ||
                indexElement.ValueKind != JsonValueKind.Number ||
                !indexElement.TryGetInt32(out var index))
            {
                index = toolCalls.Count;
            }

            if (!toolCalls.TryGetValue(index, out var call))
            {
                call = new StreamingToolCall();
                toolCalls[index] = call;
            }

            if (item.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(id.GetString()))
            {
                call.Id = id.GetString()!;
            }

            if (!item.TryGetProperty("function", out var function) ||
                function.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (function.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(name.GetString()))
            {
                call.Name = name.GetString()!;
            }

            if (function.TryGetProperty("arguments", out var arguments) &&
                arguments.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(arguments.GetString()))
            {
                call.Arguments.Append(arguments.GetString());
            }
        }
    }

    private static IReadOnlyList<ChatToolCall> FlushToolCalls(Dictionary<int, StreamingToolCall> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return [];
        }

        var calls = toolCalls
            .OrderBy(item => item.Key)
            .Select(item => item.Value)
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select((item, index) => new ChatToolCall(
                string.IsNullOrWhiteSpace(item.Id) ? $"call_{index + 1}" : item.Id,
                item.Name,
                item.Arguments.Length == 0 ? "{}" : item.Arguments.ToString()))
            .ToArray();
        toolCalls.Clear();
        return calls;
    }

    private static Dictionary<string, object?> ToOpenAiToolDefinition(ChatToolDefinition tool)
    {
        var function = new Dictionary<string, object?>
        {
            ["name"] = NormalizeRequiredText(tool.Name, nameof(tool.Name), 128),
            ["description"] = NormalizeOptionalText(tool.Description, nameof(tool.Description), 4096),
            ["parameters"] = tool.ParametersSchema.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }, JsonOptions)
                : tool.ParametersSchema
        };
        if (tool.Strict)
        {
            function["strict"] = true;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = function
        };
    }

    private static Dictionary<string, object?> ToResponsesToolDefinition(ChatToolDefinition tool)
    {
        var definition = new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["name"] = NormalizeRequiredText(tool.Name, nameof(tool.Name), 128),
            ["description"] = NormalizeOptionalText(tool.Description, nameof(tool.Description), 4096),
            ["parameters"] = tool.ParametersSchema.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }, JsonOptions)
                : tool.ParametersSchema
        };
        if (tool.Strict)
        {
            definition["strict"] = true;
        }

        return definition;
    }

    private static Dictionary<string, object?> ToOpenAiToolCall(ChatToolCall call)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = NormalizeRequiredText(call.Id, nameof(call.Id), 512),
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = NormalizeRequiredText(call.Name, nameof(call.Name), 128),
                ["arguments"] = string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson
            }
        };
    }

    private static async ValueTask<byte[]> ReadContentLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        await using var target = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (target.Length + read > ErrorBodyLimitBytes)
            {
                throw ProviderError("服务商响应过大，已拒绝处理。", retryable: false);
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return target.ToArray();
    }

    private static async IAsyncEnumerable<AttemptItem> ParseResponsesStreamItemsAsync(
        HttpContent content,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);
        while (true)
        {
            var line = await ReadLineOrFailOnIdleAsync(reader, cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length > SseLineLimitChars)
            {
                yield return AttemptItem.Fail(ProviderError("服务商返回的 SSE 行过大，已拒绝处理。", retryable: false));
                yield break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].TrimStart();
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            foreach (var item in ParseResponsesSseData(data))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<AttemptItem> ParseResponsesSseData(string data)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            var type = ReadString(root, "type");
            switch (type)
            {
                case "response.output_text.delta":
                    if (ReadString(root, "delta") is { Length: > 0 } delta)
                    {
                        yield return AttemptItem.Item(new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, delta));
                    }

                    break;
                case "response.reasoning_text.delta":
                case "response.reasoning_summary_text.delta":
                    if (ReadString(root, "delta") is { Length: > 0 } reasoning)
                    {
                        yield return AttemptItem.Item(new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Thinking, reasoning));
                    }

                    break;
                case "response.output_item.done":
                    if (TryReadFunctionCall(root, out var call))
                    {
                        yield return AttemptItem.Item(new ChatCompletionStreamEvent(
                            ChatCompletionStreamEventKind.ToolCall,
                            ToolCall: call));
                    }

                    break;
                case "response.completed":
                    if (TryReadProperty(root, "response", out var completed) &&
                        TryReadProperty(completed, "usage", out var completedUsage) &&
                        completedUsage.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        yield return AttemptItem.Item(new ChatCompletionStreamEvent(
                            ChatCompletionStreamEventKind.Usage,
                            string.Empty,
                            completedUsage.Clone()));
                    }

                    break;
                case "response.incomplete":
                    // 不抛异常就只剩"无声结束"，分析端只能报笼统的 invalid structured output；
                    // 这里把服务商给出的终止原因转成可行动的错误。预算耗尽是确定性的，不重试。
                    var incompleteReason = "unknown";
                    if (TryReadProperty(root, "response", out var incomplete) &&
                        TryReadProperty(incomplete, "incomplete_details", out var details) &&
                        TryReadProperty(details, "reason", out var reason) &&
                        reason.ValueKind == JsonValueKind.String)
                    {
                        incompleteReason = reason.GetString() ?? incompleteReason;
                    }

                    yield return AttemptItem.Fail(ProviderError(
                        // DeepSeek 风格的 Responses 端点用 chat-completions 的 "length"
                        // 表示预算耗尽，与 OpenAI 的 "max_output_tokens" 同义。
                        incompleteReason is "max_output_tokens" or "length"
                            ? $"模型输出预算耗尽（{incompleteReason}）；请降低推理力度或减小输出需求后重试。"
                            : $"模型响应不完整（{incompleteReason}），请重试。",
                        retryable: false));
                    break;
                case "response.failed":
                    var failedMessage = "模型服务返回失败。";
                    if (TryReadProperty(root, "response", out var failedResponse) &&
                        TryReadProperty(failedResponse, "error", out var failedError))
                    {
                        var errorText = ReadString(failedError, "message");
                        if (!string.IsNullOrWhiteSpace(errorText))
                        {
                            failedMessage = failedMessage + " " + errorText;
                        }
                    }

                    yield return AttemptItem.Fail(WithBurstProtectionGuidance(ProviderError(failedMessage, retryable: true)));
                    break;
                case "error":
                    // 请求频率保护（burst）属于冷却窗口：重试会不断重置窗口，必须立即失败；
                    // WithBurstProtectionGuidance 命中签名时改为不可重试并给出等待指引。
                    var streamErrorMessage = ReadString(root, "message") ?? "模型服务返回流式错误。";
                    yield return AttemptItem.Fail(WithBurstProtectionGuidance(ProviderError(streamErrorMessage, retryable: true)));
                    break;
            }
        }
    }

    private static string ParseResponsesText(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (ReadString(root, "output_text") is { Length: > 0 } outputText)
            {
                return outputText;
            }

            if (!TryReadProperty(root, "output", out var output) || output.ValueKind != JsonValueKind.Array)
            {
                throw ProviderError("LLM 返回为空，未能生成文本。", retryable: true);
            }

            var builder = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (!TryReadProperty(item, "content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (ReadString(contentItem, "text") is { Length: > 0 } text)
                    {
                        builder.Append(text);
                    }
                }
            }

            return builder.Length == 0
                ? throw ProviderError("LLM 返回为空，未能生成文本。", retryable: true)
                : builder.ToString();
        }
        catch (JsonException ex)
        {
            throw ProviderError($"解析 Responses 响应失败: {ex.Message}", retryable: false);
        }
    }

    private static bool TryReadFunctionCall(JsonElement root, out ChatToolCall call)
    {
        call = default!;
        if (!TryReadProperty(root, "item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!string.Equals(ReadString(item, "type"), "function_call", StringComparison.Ordinal))
        {
            return false;
        }

        var name = ReadString(item, "name");
        var arguments = ReadString(item, "arguments");
        var callId = ReadString(item, "call_id") ?? ReadString(item, "id");
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        call = new ChatToolCall(
            string.IsNullOrWhiteSpace(callId) ? $"call_{Guid.NewGuid():N}" : callId,
            name,
            string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return TryReadProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryReadProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static Dictionary<string, object?> ApplyProviderRequestAdapter(
        string providerKey,
        Dictionary<string, object?> payload)
    {
        switch (providerKey)
        {
            case "qwen":
                payload.Remove("thinking");
                payload.Remove("reasoning_effort");
                if (payload.TryGetValue("stream", out var stream) && stream is true)
                {
                    payload["enable_thinking"] = true;
                }

                break;
            case "minimax":
                payload["reasoning_split"] = true;
                break;
            case "moonshot":
                payload.Remove("temperature");
                payload.Remove("reasoning_effort");
                if (payload.TryGetValue("model", out var model) &&
                    model is string modelId &&
                    modelId.StartsWith("kimi-k2.7-code", StringComparison.Ordinal))
                {
                    payload.Remove("thinking");
                }

                break;
        }

        return payload;
    }

    private static Dictionary<string, string> ApplyProviderHeaderAdapter(
        string providerKey,
        Dictionary<string, string> headers)
    {
        if (providerKey == "mimo" &&
            headers.TryGetValue("Authorization", out var authorization))
        {
            headers["api-key"] = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                ? authorization["Bearer ".Length..]
                : authorization;
            headers.Remove("Authorization");
        }

        return headers;
    }

    private static string FormatProviderError(HttpStatusCode statusCode, byte[] body, string apiKey)
    {
        var code = (int)statusCode;
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => $"API Key 无效或未配置 ({code})",
            HttpStatusCode.Forbidden => $"无权访问该端点 ({code})",
            HttpStatusCode.NotFound => $"该端点不支持当前请求 ({code})",
            (HttpStatusCode)429 => $"请求过于频繁，请稍后重试 ({code})",
            _ => $"[{code}] {SanitizeBody(body, apiKey)}"
        };
        return message;
    }

    private static string SanitizeBody(byte[] body, string apiKey)
    {
        var text = Encoding.UTF8.GetString(body).Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            text = text.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        }

        return text.Length == 0 ? "服务商返回错误，但响应体为空。" : text;
    }

    private static bool Retryable(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is 408 or 429 || code is >= 500 and < 600;
    }

    private static BridgeRequestException ProviderError(string message, bool retryable)
    {
        return new BridgeRequestException(
            BridgeErrorCodes.LlmProviderError,
            message,
            retryable: retryable);
    }

    private const string BurstProtectionGuidance =
        "服务商触发了请求频率保护：请等待 1-2 分钟后再点重试，连续快速重试会延长保护窗口。";

    private static bool IsBurstProtectionMessage(string message)
    {
        return message.Contains("request burst", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("system protection", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 突发保护是冷却窗口型错误：冷却期内每次重试都会重置窗口，机器重试只会帮倒忙。
    /// 命中签名时改为不可机器重试，并在消息里给出可行动的等待指引。
    /// </summary>
    private static BridgeRequestException WithBurstProtectionGuidance(BridgeRequestException failure)
    {
        if (!failure.Retryable || !IsBurstProtectionMessage(failure.Message))
        {
            return failure;
        }

        return new BridgeRequestException(
            failure.Code,
            $"{BurstProtectionGuidance}（服务商原话：{failure.Message}）",
            failure.Details,
            retryable: false);
    }

    private static string NormalizeProviderName(string? value)
    {
        var providerName = NormalizeRequiredText(value, nameof(value), 128).ToLowerInvariant();
        if (providerName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider name may only contain letters, digits, hyphen, underscore, and dot.", nameof(value));
        }

        return providerName;
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
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

        if (normalized.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private sealed record ResolvedProvider(
 string Key,
 string EndpointType,
 Uri EndpointUrl,
 string ApiKey,
 double Temperature,
 ModelInfoPayload Model);

    private static int ResolveMaxOutputTokens(
 ResolvedProvider provider,
 ChatCompletionRequest request,
 bool titleGeneration)
    {
        ValidateMaxOutputTokens(request);

        var modelLimit = provider.Model.MaxOutputTokens > 0
            ? provider.Model.MaxOutputTokens
            : 4096;
        if (request.MaxOutputTokens is { } requestLimit)
        {
            return Math.Min(requestLimit, modelLimit);
        }

        return titleGeneration ? 64 : modelLimit;
    }

    private static double ResolveTemperature(ResolvedProvider provider, ChatCompletionRequest request)
    {
        var temperature = request.TemperatureOverride ?? provider.Temperature;
        if (double.IsNaN(temperature) || double.IsInfinity(temperature) || temperature < 0 || temperature > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.TemperatureOverride),
                temperature,
                "Temperature must be between 0 and 2.");
        }

        return temperature;
    }

    private static void ValidateMaxOutputTokens(ChatCompletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MaxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.MaxOutputTokens),
                request.MaxOutputTokens,
                message: null);
        }
    }

    private sealed class StreamingToolCall
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>
    /// 单次流式尝试的产出：正常事件流经 <see cref="Event"/>；终结性失败经 <see cref="Failure"/>
    /// 返回（由外层决定重试还是抛出），这样迭代器内部不需要 try/catch 包裹 yield。
    /// </summary>
    private sealed record AttemptItem(ChatCompletionStreamEvent? Event, BridgeRequestException? Failure)
    {
        public static AttemptItem Item(ChatCompletionStreamEvent eventItem) => new(eventItem, null);

        public static AttemptItem Fail(BridgeRequestException failure) => new(null, failure);
    }
}
