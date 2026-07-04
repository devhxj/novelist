using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class StandardEmbeddingClient : IEmbeddingClient
{
    private const int MaxBatchSize = 2048;
    private const int MaxInputLength = 200_000;
    private const int MaxEndpointLength = 2048;
    private const int MaxProviderKeyLength = 128;
    private const int MaxModelIdLength = 256;
    private const int MaxApiKeyLength = 4096;
    private const int MaxUserLength = 256;
    private const int MaxDimensions = 1_000_000;
    private const int ResponseReadLimitBytes = 32 * 1024 * 1024;
    private const int MaxAttempts = 3;
    private const string ProviderTypeApi = "api";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    public StandardEmbeddingClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public async ValueTask<EmbeddingBatchResult> EmbedAsync(
        IReadOnlyList<string> inputs,
        EmbeddingRequestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        var normalizedInputs = NormalizeInputs(inputs);
        var normalizedOptions = NormalizeOptions(options);

        using var request = CreateRequest(normalizedInputs, normalizedOptions);
        using var response = await SendWithRetryAsync(request, normalizedOptions.ApiKey, cancellationToken);
        var body = await ReadContentLimitedAsync(response.Content, cancellationToken);
        if ((int)response.StatusCode >= 400)
        {
            throw ProviderError(
                FormatProviderError(response.StatusCode, body, normalizedOptions.ApiKey),
                Retryable(response.StatusCode));
        }

        return ParseResponse(body, normalizedInputs.Count, normalizedOptions.ModelId);
    }

    private static IReadOnlyList<string> NormalizeInputs(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
        {
            throw new ArgumentException("At least one embedding input is required.", nameof(inputs));
        }

        if (inputs.Count > MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(inputs), inputs.Count, $"Embedding batch size must be at most {MaxBatchSize}.");
        }

        var normalized = new List<string>(inputs.Count);
        foreach (var input in inputs)
        {
            var value = input?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Embedding inputs must not be empty.", nameof(inputs));
            }

            if (value.Length > MaxInputLength)
            {
                throw new ArgumentOutOfRangeException(nameof(inputs), value.Length, $"Embedding input must be at most {MaxInputLength} characters.");
            }

            if (value.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
            {
                throw new ArgumentException("Embedding inputs must not contain unsupported control characters.", nameof(inputs));
            }

            normalized.Add(value);
        }

        return normalized;
    }

    private static EmbeddingRequestOptions NormalizeOptions(EmbeddingRequestOptions options)
    {
        var providerType = (options.ProviderType ?? string.Empty).Trim().ToLowerInvariant();
        if (providerType.Length > 0 && providerType is not (ProviderTypeApi or "online" or "remote"))
        {
            throw new ArgumentException("Standard embedding client only supports api provider type.", nameof(options));
        }

        var providerKey = NormalizeRequiredText(options.ProviderKey, nameof(options.ProviderKey), MaxProviderKeyLength).ToLowerInvariant();
        if (providerKey.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider key may only contain letters, digits, hyphen, underscore, and dot.", nameof(options.ProviderKey));
        }

        var endpoint = EmbeddingEndpoint.NormalizeEndpointUrl(options.EndpointUrl, MaxEndpointLength);
        var apiKey = NormalizeRequiredText(options.ApiKey, nameof(options.ApiKey), MaxApiKeyLength);
        var modelId = NormalizeRequiredText(options.ModelId, nameof(options.ModelId), MaxModelIdLength);
        if (options.Dimensions is <= 0 or > MaxDimensions)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Dimensions), options.Dimensions, $"Dimensions must be between 1 and {MaxDimensions}.");
        }

        var user = string.IsNullOrWhiteSpace(options.User)
            ? null
            : NormalizeRequiredText(options.User, nameof(options.User), MaxUserLength);

        return options with
        {
            ProviderKey = providerKey,
            EndpointUrl = endpoint,
            ApiKey = apiKey,
            ModelId = modelId,
            User = user,
            ProviderType = ProviderTypeApi
        };
    }

    private static HttpRequestMessage CreateRequest(
        IReadOnlyList<string> inputs,
        EmbeddingRequestOptions options)
    {
        var payload = new EmbeddingRequestBody
        {
            Model = options.ModelId,
            Input = inputs,
            Dimensions = options.Dimensions,
            User = options.User
        };

        var request = new HttpRequestMessage(HttpMethod.Post, options.EndpointUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return request;
    }

    private async ValueTask<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage originalRequest,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var body = originalRequest.Content is null
            ? Array.Empty<byte>()
            : await originalRequest.Content.ReadAsByteArrayAsync(cancellationToken);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var request = CloneRequest(originalRequest, body);
            try
            {
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode < 400 || !Retryable(response.StatusCode) || attempt == MaxAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw ProviderError($"请求失败: {ex.Message.Replace(apiKey, "[redacted]", StringComparison.Ordinal)}", retryable: true);
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken);
        }

        throw ProviderError("Embedding request failed after retries.", retryable: true);
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage source, byte[] body)
    {
        var request = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body);
            foreach (var header in source.Content!.Headers)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return request;
    }

    private static async ValueTask DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), cancellationToken);
    }

    private static EmbeddingBatchResult ParseResponse(byte[] body, int expectedCount, string fallbackModel)
    {
        EmbeddingResponseBody? response;
        try
        {
            response = JsonSerializer.Deserialize<EmbeddingResponseBody>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw ProviderError($"解析 Embeddings 响应失败: {ex.Message}", retryable: false);
        }

        if (response is null || response.Data.Count != expectedCount)
        {
            throw ProviderError("Embeddings 响应数量与输入数量不一致。", retryable: false);
        }

        var items = response.Data
            .OrderBy(item => item.Index)
            .Select(item => new EmbeddingItemResult(item.Index, item.Embedding))
            .ToArray();
        if (items.Select(item => item.Index).Distinct().Count() != expectedCount ||
            items.First().Index != 0 ||
            items.Last().Index != expectedCount - 1)
        {
            throw ProviderError("Embeddings 响应索引不连续。", retryable: false);
        }

        var dimensions = items[0].Vector.Count;
        if (dimensions <= 0 || items.Any(item => item.Vector.Count != dimensions))
        {
            throw ProviderError("Embeddings 响应向量维度不一致。", retryable: false);
        }

        return new EmbeddingBatchResult(
            string.IsNullOrWhiteSpace(response.Model) ? fallbackModel : response.Model,
            dimensions,
            items,
            new EmbeddingUsage(response.Usage?.PromptTokens ?? 0, response.Usage?.TotalTokens ?? 0));
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

            if (target.Length + read > ResponseReadLimitBytes)
            {
                throw ProviderError("服务商响应过大，已拒绝处理。", retryable: false);
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return target.ToArray();
    }

    private static string FormatProviderError(HttpStatusCode statusCode, byte[] body, string apiKey)
    {
        var code = (int)statusCode;
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => $"API Key 无效或未配置 ({code})",
            HttpStatusCode.Forbidden => $"无权访问该端点 ({code})",
            HttpStatusCode.NotFound => $"该端点不支持当前请求 ({code})",
            (HttpStatusCode)429 => $"请求过于频繁，请稍后重试 ({code}): {SanitizeBody(body, apiKey)}",
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

    private static string NormalizeRequiredText(string? value, string name, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

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

    private sealed class EmbeddingRequestBody
    {
        [JsonPropertyName("model")]
        public string Model { get; init; } = string.Empty;

        [JsonPropertyName("input")]
        public IReadOnlyList<string> Input { get; init; } = [];

        [JsonPropertyName("encoding_format")]
        public string EncodingFormat { get; init; } = "float";

        [JsonPropertyName("dimensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Dimensions { get; init; }

        [JsonPropertyName("user")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? User { get; init; }
    }

    private sealed class EmbeddingResponseBody
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public List<EmbeddingResponseItem> Data { get; set; } = [];

        [JsonPropertyName("usage")]
        public EmbeddingUsageBody? Usage { get; set; }
    }

    private sealed class EmbeddingResponseItem
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("embedding")]
        public List<float> Embedding { get; set; } = [];
    }

    private sealed class EmbeddingUsageBody
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
