using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class DeepSeekWebSearchOptions
{
    public Uri Endpoint { get; init; } = new("https://api.deepseek.com/anthropic/v1/messages", UriKind.Absolute);

    public string DefaultModel { get; init; } = "deepseek-v4-flash";

    public string AnthropicVersion { get; init; } = "2023-06-01";

    public string ToolType { get; init; } = "web_search_20260209";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;

    public int MaxPromptChars { get; init; } = 500;

    public int MaxTokens { get; init; } = 16_384;
}

public sealed class DeepSeekWebSearchService : IWebSearchService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILlmConfigurationService _configuration;
    private readonly HttpClient _httpClient;
    private readonly DeepSeekWebSearchOptions _options;
    private readonly bool _ownsHttpClient;

    public DeepSeekWebSearchService(
        ILlmConfigurationService configuration,
        HttpClient? httpClient = null,
        DeepSeekWebSearchOptions? options = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttpClient = httpClient is null;
        _options = options ?? new DeepSeekWebSearchOptions();
    }

    public async ValueTask<WebSearchResultPayload> SearchAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        var normalizedPrompt = NormalizePrompt(prompt);
        var provider = await ResolveDeepSeekProviderAsync(cancellationToken);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = ResolveModel(provider),
            ["max_tokens"] = _options.MaxTokens,
            ["stream"] = false,
            ["messages"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = normalizedPrompt
                }
            },
            ["tools"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = _options.ToolType,
                    ["name"] = "web_search"
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-api-key", provider.ApiKey.Trim());
        request.Headers.TryAddWithoutValidation("anthropic-version", _options.AnthropicVersion);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        using var response = await SendAsync(request, timeout.Token);
        var body = await ReadContentLimitedAsync(response.Content, _options.MaxResponseBytes, timeout.Token);
        if ((int)response.StatusCode >= 400)
        {
            throw new InvalidOperationException(FormatProviderError(response.StatusCode, body, provider.ApiKey));
        }

        return ExtractSearchResult(body);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async ValueTask<ProviderViewPayload> ResolveDeepSeekProviderAsync(CancellationToken cancellationToken)
    {
        var config = await _configuration.GetConfigAsync(cancellationToken);
        var provider = config.Providers.FirstOrDefault(item =>
            string.Equals(item.Key, "deepseek", StringComparison.Ordinal));
        if (provider is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new InvalidOperationException("网络搜索未启用：请先在 LLM 设置中配置 DeepSeek");
        }

        return provider;
    }

    private string ResolveModel(ProviderViewPayload provider)
    {
        var models = provider.BuiltinModels.Concat(provider.CustomModels).ToArray();
        if (models.Any(model => string.Equals(model.Id, _options.DefaultModel, StringComparison.Ordinal)))
        {
            return _options.DefaultModel;
        }

        return models.FirstOrDefault()?.Id ?? _options.DefaultModel;
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
            throw new InvalidOperationException($"请求失败: {ex.Message}", ex);
        }
    }

    private static WebSearchResultPayload ExtractSearchResult(byte[] body)
    {
        AnthropicSearchResponse response;
        try
        {
            response = JsonSerializer.Deserialize<AnthropicSearchResponse>(body, JsonOptions)
                ?? throw new InvalidOperationException("搜索响应为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"解析搜索响应失败: {ex.Message}", ex);
        }

        var queries = new List<string>();
        var sources = new List<WebSearchSourcePayload>();
        var summary = new StringBuilder();

        foreach (var block in response.Content ?? [])
        {
            if (!block.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (typeElement.GetString())
            {
                case "server_tool_use":
                    if (block.TryGetProperty("input", out var input) &&
                        input.TryGetProperty("query", out var query) &&
                        query.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(query.GetString()))
                    {
                        queries.Add(query.GetString()!.Trim());
                    }

                    break;
                case "web_search_tool_result":
                    if (block.TryGetProperty("content", out var results) &&
                        results.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in results.EnumerateArray())
                        {
                            var title = ReadString(item, "title");
                            var url = ReadString(item, "url");
                            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                                IsHttpOrHttps(uri))
                            {
                                sources.Add(new WebSearchSourcePayload(title, uri.ToString()));
                            }
                        }
                    }

                    break;
                case "text":
                    var text = ReadString(block, "text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        summary.Append(text.Trim());
                    }

                    break;
            }
        }

        if (sources.Count == 0 && summary.Length == 0)
        {
            throw new InvalidOperationException("搜索未返回有效结果");
        }

        return new WebSearchResultPayload(queries, summary.ToString(), sources);
    }

    private static string ReadString(JsonElement item, string propertyName)
    {
        return item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static bool IsHttpOrHttps(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<byte[]> ReadContentLimitedAsync(
        HttpContent content,
        int limit,
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

            if (target.Length + read > limit)
            {
                throw new InvalidOperationException("搜索响应过大，已拒绝处理。");
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
            HttpStatusCode.Unauthorized => $"DeepSeek API Key 无效或未配置 ({code})",
            HttpStatusCode.Forbidden => $"DeepSeek 拒绝访问 web_search 端点 ({code})",
            HttpStatusCode.NotFound => $"DeepSeek web_search 端点不可用 ({code})",
            (HttpStatusCode)429 => $"DeepSeek 搜索请求过于频繁，请稍后重试 ({code})",
            _ => $"[{code}] {ParseAnthropicError(body, apiKey)}"
        };
        return message;
    }

    private static string ParseAnthropicError(byte[] body, string apiKey)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                return Sanitize(message.GetString() ?? string.Empty, apiKey);
            }
        }
        catch (JsonException)
        {
            // Fall through to raw body sanitization.
        }

        return Sanitize(Encoding.UTF8.GetString(body).Trim(), apiKey);
    }

    private static string Sanitize(string text, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            text = text.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(text) ? "服务商返回错误，但响应体为空。" : text;
    }

    private string NormalizePrompt(string prompt)
    {
        var normalized = (prompt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("prompt 不能为空。", nameof(prompt));
        }

        if (normalized.Length > _options.MaxPromptChars)
        {
            throw new ArgumentOutOfRangeException(nameof(prompt), normalized.Length, $"prompt 不能超过 {_options.MaxPromptChars} 字符。");
        }

        if (normalized.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException("prompt 不能包含不支持的控制字符。", nameof(prompt));
        }

        return normalized;
    }

    private sealed class AnthropicSearchResponse
    {
        [JsonPropertyName("content")]
        public IReadOnlyList<JsonElement> Content { get; set; } = [];
    }
}
