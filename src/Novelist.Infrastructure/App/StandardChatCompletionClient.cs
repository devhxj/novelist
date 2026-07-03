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
        var provider = await ResolveProviderAsync(request, cancellationToken);
        var payload = BuildPayload(provider, request, stream: true, titleGeneration: false);
        using var httpRequest = CreateHttpRequest(provider, payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        if ((int)response.StatusCode >= 400)
        {
            var body = await ReadContentLimitedAsync(response.Content, cancellationToken);
            throw ProviderError(FormatProviderError(response.StatusCode, body, provider.ApiKey), Retryable(response.StatusCode));
        }

        var toolCalls = new Dictionary<int, StreamingToolCall>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length > SseLineLimitChars)
            {
                throw ProviderError("服务商返回的 SSE 行过大，已拒绝处理。", retryable: false);
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
                    yield return new ChatCompletionStreamEvent(
                        ChatCompletionStreamEventKind.ToolCall,
                        ToolCall: call);
                }

                break;
            }

            foreach (var item in ParseSseData(data, toolCalls))
            {
                yield return item;
            }
        }

        foreach (var call in FlushToolCalls(toolCalls))
        {
            yield return new ChatCompletionStreamEvent(
                ChatCompletionStreamEventKind.ToolCall,
                ToolCall: call);
        }
    }

    public async ValueTask<string> GenerateTextAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        var provider = await ResolveProviderAsync(request, cancellationToken);
        var payload = BuildPayload(provider, request, stream: false, titleGeneration: true);
        using var httpRequest = CreateHttpRequest(provider, payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        var body = await ReadContentLimitedAsync(response.Content, cancellationToken);
        if ((int)response.StatusCode >= 400)
        {
            throw ProviderError(FormatProviderError(response.StatusCode, body, provider.ApiKey), Retryable(response.StatusCode));
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

        var chatUrl = NormalizeUrl(provider.ChatUrl);
        return new ResolvedProvider(
            providerName,
            chatUrl,
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

        var payload = new Dictionary<string, object?>
        {
            ["model"] = provider.Model.Id,
            ["messages"] = messages,
            ["stream"] = stream,
            ["temperature"] = provider.Temperature,
            ["max_tokens"] = titleGeneration
                ? 64
                : provider.Model.MaxOutputTokens > 0
                    ? provider.Model.MaxOutputTokens
                    : 4096
        };

        if (stream)
        {
            payload["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true };
        }

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(ToOpenAiToolDefinition).ToArray();
            payload["tool_choice"] = "auto";
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

    private HttpRequestMessage CreateHttpRequest(
        ResolvedProvider provider,
        Dictionary<string, object?> payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, provider.ChatUrl)
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
        catch (HttpRequestException ex)
        {
            throw ProviderError($"请求失败: {ex.Message}", retryable: true);
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
        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = NormalizeRequiredText(tool.Name, nameof(tool.Name), 128),
                ["description"] = NormalizeOptionalText(tool.Description, nameof(tool.Description), 4096),
                ["parameters"] = tool.ParametersSchema.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }, JsonOptions)
                    : tool.ParametersSchema
            }
        };
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

    private static Uri NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Chat URL must be an absolute http:// or https:// URL.", nameof(value));
        }

        return uri;
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
        Uri ChatUrl,
        string ApiKey,
        double Temperature,
        ModelInfoPayload Model);

    private sealed class StreamingToolCall
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();
    }
}
