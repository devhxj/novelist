using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemLlmConfigurationService : ILlmConfigurationService
{
    private const int MaxProviderKeyLength = 128;
    private const int MaxDisplayNameLength = 200;
    private const int MaxUrlLength = 2_048;
    private const int MaxApiKeyLength = 4_096;
    private const int MaxModelIdLength = 256;
    private const int MaxModelNameLength = 256;
    private const int MaxTokenLimit = 4_000_000;
    private const int ResponseReadLimitBytes = 64 * 1024;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly byte[] AppKey =
    [
        0x7a, 0x3f, 0x71, 0xe2, 0x5c, 0x9d, 0x0b, 0x46,
        0x1a, 0x5f, 0x33, 0xc8, 0x6e, 0x22, 0x4d, 0x0f,
        0x85, 0xce, 0x1c, 0x29, 0x3f, 0xa7, 0x80, 0xf4,
        0x2e, 0x9c, 0x17, 0xd5, 0x4a, 0x8e, 0xd2, 0x06
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemLlmConfigurationService(
        AppInitializationOptions? options = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? new AppInitializationOptions();
        _httpClient = httpClient ?? new HttpClient();
    }

    public async ValueTask<LlmConfigViewPayload> GetConfigAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var user = await LoadUserConfigAsync(cancellationToken);
            return BuildConfigView(user);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask SaveConfigAsync(LlmConfigViewPayload input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var user = ToUserConfig(input);
            await SaveUserConfigAsync(user, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<AvailableModelPayload>> GetModelsAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var user = await LoadUserConfigAsync(cancellationToken);
            return MergeConfiguredProviders(user)
                .SelectMany(provider => provider.Models.Select(model => new AvailableModelPayload(
                    $"{provider.Key}/{model.Id}",
                    provider.DisplayName,
                    model.Name,
                    model.ContextWindow,
                    model.MaxOutputTokens,
                    model.SupportsThinking,
                    model.ReasoningLevels ?? [],
                    model.SupportsVision)))
                .OrderBy(model => model.ProviderName, StringComparer.Ordinal)
                .ThenBy(model => model.ModelName, StringComparer.Ordinal)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ModelInfoPayload>> DiscoverModelsAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var normalizedBaseUrl = LlmEndpoint.NormalizeBaseUrl(baseUrl, requireValue: true, MaxUrlLength);
        var key = NormalizeApiKey(apiKey);
        var modelsUrl = LlmEndpoint.BuildModelsUrl(normalizedBaseUrl);

        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadContentLimitedAsync(response.Content, cancellationToken);
        EnsureSuccess(response.StatusCode, body, key);

        if (LooksLikeHtml(body))
        {
            throw ProviderError("该端点不支持自动发现（服务端返回了网页而非 JSON）", retryable: false);
        }

        DiscoverModelsResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<DiscoverModelsResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw ProviderError($"解析模型列表失败（该端点可能不支持 /models）: {ex.Message}", retryable: false);
        }

        return (result?.Data ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .Select(item => new ModelInfoPayload(
                item.Id.Trim(),
                ModelIdToName(item.Id.Trim()),
                Math.Max(0, item.ContextLength),
                0,
                item.SupportsReasoning ?? false,
                null,
                item.SupportsImageIn ?? item.SupportsVideoIn ?? false))
            .ToArray();
    }

    public async ValueTask TestConnectionAsync(TestConnectionPayload input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var providerKey = NormalizeProviderKey(input.ProviderName, nameof(input.ProviderName));
        var apiKey = NormalizeApiKey(input.ApiKey);
        var modelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), MaxModelIdLength, allowLineBreaks: false);

        BuiltinProviders.TryGetValue(providerKey, out var definition);
        var endpointType = LlmEndpoint.NormalizeEndpointType(input.EndpointType, definition?.EndpointType ?? LlmEndpoint.Chat);
        var baseUrl = ResolveInputBaseUrl(input.BaseUrl, input.ChatUrl, definition);
        var endpointUrl = LlmEndpoint.BuildEndpointUrl(baseUrl, endpointType);

        var payload = endpointType == LlmEndpoint.Responses
            ? new Dictionary<string, object?>
            {
                ["model"] = modelId,
                ["input"] = "hi",
                ["max_output_tokens"] = 1
            }
            : new Dictionary<string, object?>
            {
                ["model"] = modelId,
                ["messages"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = "hi"
                    }
                },
                ["max_tokens"] = 1
            };
        if (endpointType == LlmEndpoint.Chat && definition?.BuildRequest is not null)
        {
            payload = definition.BuildRequest(payload);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {apiKey}"
        };
        if (definition?.BuildHeaders is not null)
        {
            headers = definition.BuildHeaders(headers);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadContentLimitedAsync(response.Content, cancellationToken);
        EnsureSuccess(response.StatusCode, body, apiKey);
    }

    private async ValueTask<UserLlmConfigDocument> LoadUserConfigAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            return new UserLlmConfigDocument();
        }

        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        var plain = Decrypt(encrypted);
        var config = JsonSerializer.Deserialize<UserLlmConfigDocument>(plain, JsonOptions)
            ?? throw new InvalidOperationException("LLM config is empty or malformed.");
        ValidateUserConfig(config);
        return config;
    }

    private async ValueTask SaveUserConfigAsync(
        UserLlmConfigDocument config,
        CancellationToken cancellationToken)
    {
        ValidateUserConfig(config);

        var path = await StorePathAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(config, JsonOptions);
        var encrypted = Encrypt(plain);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await File.WriteAllBytesAsync(tempPath, encrypted, cancellationToken);
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

    private LlmConfigViewPayload BuildConfigView(UserLlmConfigDocument user)
    {
        ValidateUserConfig(user);
        var providers = new List<ProviderViewPayload>();

        foreach (var definition in BuiltinProviders.Values.OrderBy(provider => provider.Key, StringComparer.Ordinal))
        {
            var configured = user.Providers.SingleOrDefault(provider =>
                string.Equals(provider.Key, definition.Key, StringComparison.Ordinal));
            var endpointType = ResolveEndpointType(configured?.EndpointType, definition);
            var baseUrl = ResolveConfiguredBaseUrl(configured, definition);
            providers.Add(new ProviderViewPayload(
                definition.Key,
                definition.DisplayName,
                baseUrl,
                endpointType,
                BuildEndpointUrlOrEmpty(baseUrl, endpointType),
                configured?.ApiKey ?? string.Empty,
                definition.PlatformUrl,
                definition.HelpText,
                configured?.Temperature ?? definition.Temperature,
                "builtin",
                definition.Models,
                configured?.Models ?? []));
        }

        providers.AddRange(user.Providers
            .Where(provider => !BuiltinProviders.ContainsKey(provider.Key))
            .OrderBy(provider => provider.Key, StringComparer.Ordinal)
            .Select(provider =>
            {
                var endpointType = ResolveEndpointType(provider.EndpointType, null);
                var baseUrl = ResolveConfiguredBaseUrl(provider, null);
                return new ProviderViewPayload(
                    provider.Key,
                    string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Key : provider.DisplayName,
                    baseUrl,
                    endpointType,
                    BuildEndpointUrlOrEmpty(baseUrl, endpointType),
                    provider.ApiKey,
                    string.Empty,
                    string.Empty,
                    provider.Temperature ?? 0.7,
                    "custom",
                    [],
                    provider.Models);
            }));

        return new LlmConfigViewPayload(providers);
    }

    private static UserLlmConfigDocument ToUserConfig(LlmConfigViewPayload view)
    {
        var providers = new List<UserProviderDocument>();
        foreach (var provider in view.Providers ?? [])
        {
            var key = NormalizeProviderKey(provider.Key, nameof(provider.Key));
            var apiKey = (provider.ApiKey ?? string.Empty).Trim();
            if (apiKey.Length == 0)
            {
                continue;
            }

            if (apiKey.Length > MaxApiKeyLength || apiKey.Any(ch => IsDisallowedControl(ch, allowLineBreaks: false)))
            {
                throw new ArgumentException("API key is invalid.", nameof(provider.ApiKey));
            }

            var source = NormalizeSource(provider.Source);
            var isBuiltin = BuiltinProviders.TryGetValue(key, out var definition);
            var displayName = source == "builtin" && isBuiltin
                ? definition!.DisplayName
                : NormalizeRequiredText(provider.Name, nameof(provider.Name), MaxDisplayNameLength, allowLineBreaks: false);
            var baseUrl = source == "builtin"
                ? NormalizeBuiltinBaseUrl(provider.BaseUrl, provider.ChatUrl, definition)
                : LlmEndpoint.NormalizeBaseUrl(FirstNonEmpty(provider.BaseUrl, provider.ChatUrl), requireValue: true, MaxUrlLength);
            var endpointType = LlmEndpoint.NormalizeEndpointType(provider.EndpointType, definition?.EndpointType ?? LlmEndpoint.Chat);
            var temperature = ValidateTemperature(provider.Temperature, nameof(provider.Temperature));
            var models = NormalizeModels(provider.CustomModels, definition?.Models ?? []);

            providers.Add(new UserProviderDocument
            {
                Key = key,
                DisplayName = displayName,
                BaseUrl = baseUrl,
                EndpointType = endpointType == definition?.EndpointType ? string.Empty : endpointType,
                ChatUrl = string.IsNullOrWhiteSpace(baseUrl)
                    ? string.Empty
                    : LlmEndpoint.BuildEndpointUrl(
                        string.IsNullOrWhiteSpace(baseUrl) ? definition?.BaseUrl ?? string.Empty : baseUrl,
                        endpointType).ToString(),
                ApiKey = apiKey,
                Temperature = temperature,
                Models = models
            });
        }

        return new UserLlmConfigDocument { Providers = providers };
    }

    private static IReadOnlyList<ConfiguredProvider> MergeConfiguredProviders(UserLlmConfigDocument user)
    {
        ValidateUserConfig(user);
        var result = new List<ConfiguredProvider>();
        foreach (var configured in user.Providers.Where(provider => !string.IsNullOrWhiteSpace(provider.ApiKey)))
        {
            BuiltinProviders.TryGetValue(configured.Key, out var definition);
            var models = new List<ModelInfoPayload>();
            if (definition is not null)
            {
                models.AddRange(definition.Models);
            }

            foreach (var model in configured.Models)
            {
                if (!models.Any(existing => string.Equals(existing.Id, model.Id, StringComparison.Ordinal)))
                {
                    models.Add(model);
                }
            }

            result.Add(new ConfiguredProvider(
                configured.Key,
                definition?.DisplayName ??
                    (string.IsNullOrWhiteSpace(configured.DisplayName) ? configured.Key : configured.DisplayName),
                ResolveConfiguredBaseUrl(configured, definition),
                ResolveEndpointType(configured.EndpointType, definition),
                configured.ApiKey,
                configured.Temperature ?? definition?.Temperature ?? 0.7,
                models));
        }

        return result;
    }

    private async ValueTask<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ProviderError("请求超时，请检查服务商地址和网络连接。", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw ProviderError($"请求失败: {ex.Message}", retryable: true);
        }
    }

    private static void EnsureSuccess(HttpStatusCode statusCode, byte[] body, string apiKey)
    {
        if ((int)statusCode < 400)
        {
            return;
        }

        var retryable = statusCode is HttpStatusCode.RequestTimeout or
            (HttpStatusCode)429 or
            >= HttpStatusCode.InternalServerError;
        var code = (int)statusCode;
        var message = statusCode switch
        {
            HttpStatusCode.Unauthorized => $"API Key 无效或未配置 ({code})",
            HttpStatusCode.Forbidden when LooksLikeHtml(body) => $"服务端拒绝访问，可能被防火墙拦截，该端点不支持自动发现 ({code})",
            HttpStatusCode.Forbidden => $"无权访问该端点 ({code})",
            HttpStatusCode.NotFound => $"该端点不支持当前请求 ({code})",
            (HttpStatusCode)429 => $"请求过于频繁，请稍后重试 ({code})",
            _ => $"[{code}] {SanitizeProviderBody(body, apiKey)}"
        };

        throw ProviderError(message, retryable);
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

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken), "llm", "config.enc");
    }

    private static byte[] Encrypt(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(AppKey, TagSize);
        aes.Encrypt(nonce, plain, ciphertext, tag);

        var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);
        return result;
    }

    private static byte[] Decrypt(byte[] data)
    {
        if (data.Length < NonceSize + TagSize)
        {
            throw new InvalidOperationException("LLM config ciphertext is too short.");
        }

        var nonce = data[..NonceSize];
        var ciphertext = data[NonceSize..^TagSize];
        var tag = data[^TagSize..];
        var plain = new byte[ciphertext.Length];
        using var aes = new AesGcm(AppKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plain);
        return plain;
    }

    private static string ResolveInputBaseUrl(
        string? baseUrl,
        string? legacyChatUrl,
        ProviderDefinition? definition)
    {
        var input = FirstNonEmpty(baseUrl, legacyChatUrl);
        if (!string.IsNullOrWhiteSpace(input))
        {
            var normalized = LlmEndpoint.NormalizeBaseUrl(input, requireValue: true, MaxUrlLength);
            return ResolveLegacyBaseUrl(normalized, definition);
        }

        return definition?.BaseUrl ?? string.Empty;
    }

    private static string BuildEndpointUrlOrEmpty(string baseUrl, string endpointType)
    {
        return string.IsNullOrWhiteSpace(baseUrl)
            ? string.Empty
            : LlmEndpoint.BuildEndpointUrl(baseUrl, endpointType).ToString();
    }

    private static string ResolveConfiguredBaseUrl(
        UserProviderDocument? provider,
        ProviderDefinition? definition)
    {
        if (provider is null)
        {
            return definition?.BaseUrl ?? string.Empty;
        }

        var input = FirstNonEmpty(provider.BaseUrl, provider.ChatUrl);
        if (string.IsNullOrWhiteSpace(input))
        {
            return definition?.BaseUrl ?? string.Empty;
        }

        var normalized = LlmEndpoint.NormalizeBaseUrl(input, requireValue: true, MaxUrlLength);
        return ResolveLegacyBaseUrl(normalized, definition);
    }

    private static string NormalizeBuiltinBaseUrl(
        string? baseUrl,
        string? legacyChatUrl,
        ProviderDefinition? definition)
    {
        var input = FirstNonEmpty(baseUrl, legacyChatUrl);
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalized = LlmEndpoint.NormalizeBaseUrl(input, requireValue: true, MaxUrlLength);
        if (definition is not null &&
            string.Equals(ResolveLegacyBaseUrl(normalized, definition), definition.BaseUrl, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return normalized;
    }

    private static string ResolveLegacyBaseUrl(string normalizedBaseUrl, ProviderDefinition? definition)
    {
        if (definition is null)
        {
            return normalizedBaseUrl;
        }

        return definition.AllBaseUrls().Any(candidate =>
            string.Equals(candidate, normalizedBaseUrl, StringComparison.OrdinalIgnoreCase))
                ? definition.BaseUrl
                : normalizedBaseUrl;
    }

    private static string ResolveEndpointType(string? value, ProviderDefinition? definition)
    {
        return LlmEndpoint.NormalizeEndpointType(value, definition?.EndpointType ?? LlmEndpoint.Chat);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string NormalizeProviderKey(string? value, string name)
    {
        var normalized = NormalizeRequiredText(value, name, MaxProviderKeyLength, allowLineBreaks: false).ToLowerInvariant();
        if (normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            throw new ArgumentException("Provider key may only contain letters, digits, hyphen, underscore, and dot.", name);
        }

        return normalized;
    }

    private static string NormalizeSource(string? value)
    {
        var source = NormalizeRequiredText(value, nameof(value), MaxDisplayNameLength, allowLineBreaks: false);
        if (source is not ("builtin" or "custom"))
        {
            throw new ArgumentException("Provider source must be builtin or custom.", nameof(value));
        }

        return source;
    }

    private static string NormalizeApiKey(string? value)
    {
        var normalized = NormalizeRequiredText(value, nameof(value), MaxApiKeyLength, allowLineBreaks: false);
        return normalized;
    }

    private static IReadOnlyList<ModelInfoPayload> NormalizeModels(
        IReadOnlyList<ModelInfoPayload>? models,
        IReadOnlyList<ModelInfoPayload> builtinModels)
    {
        var result = new List<ModelInfoPayload>();
        foreach (var model in models ?? [])
        {
            var id = NormalizeRequiredText(model.Id, nameof(model.Id), MaxModelIdLength, allowLineBreaks: false);
            if (builtinModels.Any(existing => string.Equals(existing.Id, id, StringComparison.Ordinal)) ||
                result.Any(existing => string.Equals(existing.Id, id, StringComparison.Ordinal)))
            {
                continue;
            }

            result.Add(new ModelInfoPayload(
                id,
                NormalizeRequiredText(model.Name, nameof(model.Name), MaxModelNameLength, allowLineBreaks: false),
                ValidateTokenLimit(model.ContextWindow, nameof(model.ContextWindow), allowZero: true),
                ValidateTokenLimit(model.MaxOutputTokens, nameof(model.MaxOutputTokens), allowZero: true),
                model.SupportsThinking,
                model.ReasoningLevels?.Select(level =>
                    NormalizeRequiredText(level, nameof(model.ReasoningLevels), MaxDisplayNameLength, allowLineBreaks: false)).ToArray(),
                model.SupportsVision));
        }

        return result;
    }

    private static int ValidateTokenLimit(int value, string name, bool allowZero)
    {
        if ((!allowZero && value <= 0) || (allowZero && value < 0) || value > MaxTokenLimit)
        {
            throw new ArgumentOutOfRangeException(name, value, $"Token limit must be between {(allowZero ? 0 : 1)} and {MaxTokenLimit}.");
        }

        return value;
    }

    private static double ValidateTemperature(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(name, value, "Temperature must be between 0 and 2.");
        }

        return Math.Round(value, 2);
    }

    private static string NormalizeRequiredText(
        string? value,
        string name,
        int maxLength,
        bool allowLineBreaks)
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

        if (normalized.Any(ch => IsDisallowedControl(ch, allowLineBreaks)))
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

    private static bool LooksLikeHtml(byte[] body)
    {
        foreach (var value in body)
        {
            if (char.IsWhiteSpace((char)value))
            {
                continue;
            }

            return value is not ((byte)'{' or (byte)'[');
        }

        return false;
    }

    private static string SanitizeProviderBody(byte[] body, string apiKey)
    {
        if (LooksLikeHtml(body))
        {
            return "服务端返回了网页，该端点可能不支持当前请求";
        }

        var text = Encoding.UTF8.GetString(body).Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            text = text.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        }

        return text.Length == 0 ? "服务商返回错误，但响应体为空。" : text;
    }

    private static string ModelIdToName(string id)
    {
        var value = id.Replace("-", " ", StringComparison.Ordinal);
        return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static BridgeRequestException ProviderError(string message, bool retryable)
    {
        return new BridgeRequestException(BridgeErrorCodes.LlmProviderError, message, retryable: retryable);
    }

    private static void ValidateUserConfig(UserLlmConfigDocument config)
    {
        foreach (var provider in config.Providers)
        {
            _ = NormalizeProviderKey(provider.Key, nameof(provider.Key));
            if (!string.IsNullOrWhiteSpace(provider.DisplayName))
            {
                _ = NormalizeRequiredText(provider.DisplayName, nameof(provider.DisplayName), MaxDisplayNameLength, allowLineBreaks: false);
            }

            if (!string.IsNullOrWhiteSpace(FirstNonEmpty(provider.BaseUrl, provider.ChatUrl)))
            {
                _ = LlmEndpoint.NormalizeBaseUrl(
                    FirstNonEmpty(provider.BaseUrl, provider.ChatUrl),
                    requireValue: true,
                    MaxUrlLength);
            }

            _ = ResolveEndpointType(provider.EndpointType, BuiltinProviders.GetValueOrDefault(provider.Key));
            _ = NormalizeApiKey(provider.ApiKey);
            if (provider.Temperature is not null)
            {
                _ = ValidateTemperature(provider.Temperature.Value, nameof(provider.Temperature));
            }

            _ = NormalizeModels(provider.Models, BuiltinProviders.GetValueOrDefault(provider.Key)?.Models ?? []);
        }
    }

    private static Dictionary<string, object?> QwenBuildRequest(Dictionary<string, object?> payload)
    {
        payload.Remove("thinking");
        payload.Remove("reasoning_effort");
        if (payload.TryGetValue("stream", out var stream) && stream is true)
        {
            payload["enable_thinking"] = true;
        }

        return payload;
    }

    private static Dictionary<string, object?> MiniMaxBuildRequest(Dictionary<string, object?> payload)
    {
        payload["reasoning_split"] = true;
        return payload;
    }

    private static Dictionary<string, object?> MoonshotBuildRequest(Dictionary<string, object?> payload)
    {
        payload.Remove("temperature");
        payload.Remove("reasoning_effort");
        if (payload.TryGetValue("model", out var model) &&
            model is string modelId &&
            modelId.StartsWith("kimi-k2.7-code", StringComparison.Ordinal))
        {
            payload.Remove("thinking");
        }

        return payload;
    }

    private static Dictionary<string, string> MimoBuildHeaders(Dictionary<string, string> headers)
    {
        if (headers.TryGetValue("Authorization", out var authorization))
        {
            headers["api-key"] = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                ? authorization["Bearer ".Length..]
                : authorization;
            headers.Remove("Authorization");
        }

        return headers;
    }

    private static readonly IReadOnlyDictionary<string, ProviderDefinition> BuiltinProviders =
        new SortedDictionary<string, ProviderDefinition>(StringComparer.Ordinal)
        {
            ["deepseek"] = new(
                "deepseek",
                "DeepSeek",
                "https://api.deepseek.com",
                LlmEndpoint.Chat,
                "https://platform.deepseek.com",
                "使用邮箱或手机号注册。完成实名认证后，进入「API Keys」创建密钥。预付费模式，最低充值 ¥1。",
                0.7,
                [
                    new("deepseek-v4-flash", "DeepSeek V4 Flash", 1_000_000, 384_000, true, ["high", "max"], false),
                    new("deepseek-v4-pro", "DeepSeek V4 Pro", 1_000_000, 384_000, true, ["high", "max"], false)
                ],
                LegacyBaseUrls: ["https://api.deepseek.com/v1"]),
            ["doubao"] = new(
                "doubao",
                "Doubao",
                "https://ark.cn-beijing.volces.com/api/v3",
                LlmEndpoint.Chat,
                "https://console.volcengine.com/ark/",
                "注册火山引擎并实名认证，进入火山方舟控制台。① 在「API 密钥管理」创建 API Key；② 在「开通管理」开通所需模型，每个模型有试用额度。",
                0.1,
                [
                    new("doubao-seed-2-1-pro-260628", "Seed 2.1 Pro", 256_000, 256_000, true, null, true),
                    new("doubao-seed-2-1-turbo-260628", "Seed 2.1 Turbo", 256_000, 256_000, true, null, true),
                    new("doubao-seed-2-0-lite-260428", "Seed 2.0 Lite", 256_000, 128_000, true, null, true),
                    new("doubao-seed-2-0-code-preview-260215", "Seed 2.0 Code Preview", 256_000, 128_000, true, null, true),
                    new("doubao-seed-character-260628", "Seed Character", 128_000, 32_000, true, null, true)
                ]),
            ["minimax"] = new(
                "minimax",
                "MiniMax",
                "https://api.minimaxi.com/v1",
                LlmEndpoint.Chat,
                "https://platform.minimaxi.com",
                "手机号注册。赠送 ¥15 体验金。点击控制台 →「接口管理」→「创建 API Key」获取密钥。按量付费需在「余额」页面充值。",
                1.0,
                [
                    new("MiniMax-M3", "MiniMax M3", 1_000_000, 128_000, true, null, true),
                    new("MiniMax-M2.7", "MiniMax M2.7", 204_800, 128_000, true, null, false),
                    new("MiniMax-M2.5", "MiniMax M2.5", 204_800, 128_000, true, null, false)
                ],
                BuildRequest: MiniMaxBuildRequest),
            ["mimo"] = new(
                "mimo",
                "MiMo",
                "https://api.xiaomimimo.com/v1",
                LlmEndpoint.Chat,
                "https://platform.xiaomimimo.com",
                "手机号注册。在控制台左侧「邀请有礼」输入邀请码领取 ¥10 体验金（有效期 40 天），然后在「API Keys」创建密钥。",
                1.0,
                [
                    new("mimo-v2.5-pro", "MiMo V2.5 Pro", 1_000_000, 128_000, true, null, false),
                    new("mimo-v2.5", "MiMo V2.5", 1_000_000, 128_000, true, null, false)
                ],
                BuildHeaders: MimoBuildHeaders),
            ["moonshot"] = new(
                "moonshot",
                "Kimi",
                "https://api.moonshot.ai/v1",
                LlmEndpoint.Chat,
                "https://platform.moonshot.ai",
                "手机号或微信登录。在控制台「API Key 管理」创建密钥。新用户赠送 ¥15 体验金，初始 RPM=3。累计充值 ¥50 升 Tier 1（RPM=200）。",
                0.7,
                [
                    new("kimi-k2.7-code", "Kimi K2.7 Code", 262_144, 128_000, true, null, true),
                    new("kimi-k2.6", "Kimi K2.6", 262_144, 128_000, true, null, true),
                    new("kimi-k2.5", "Kimi K2.5", 262_144, 128_000, true, null, true)
                ],
                BuildRequest: MoonshotBuildRequest,
                LegacyBaseUrls: ["https://api.moonshot.cn/v1"]),
            ["qwen"] = new(
                "qwen",
                "Qwen",
                "https://dashscope.aliyuncs.com/compatible-mode/v1",
                LlmEndpoint.Chat,
                "https://platform.qianwenai.com",
                "注册后在「工作台 → API Key 管理」创建密钥。新用户有免费额度，API 地址统一使用 dashscope.aliyuncs.com，无需区分地域。如使用阿里云百炼平台，需自行将上方 Base URL 替换为百炼地址。",
                0.7,
                [
                    new("qwen3.7-max", "Qwen3.7 Max", 1_000_000, 64_000, true, null, true),
                    new("qwen3.7-plus", "Qwen3.7 Plus", 1_000_000, 64_000, true, null, true),
                    new("qwen3.6-plus", "Qwen3.6 Plus", 1_000_000, 64_000, true, null, true),
                    new("qwen3.6-flash", "Qwen3.6 Flash", 1_000_000, 64_000, true, null, true)
                ],
                BuildRequest: QwenBuildRequest),
            ["zhipu"] = new(
                "zhipu",
                "GLM",
                "https://open.bigmodel.cn/api/paas/v4",
                LlmEndpoint.Chat,
                "https://open.bigmodel.cn",
                "手机号或微信注册。注册后点击「控制台」→「API Keys」→「新建」创建密钥。注册赠送体验额度。",
                1.0,
                [
                    new("glm-5.1", "GLM-5.1", 200_000, 128_000, true, null, false),
                    new("glm-5", "GLM-5", 200_000, 128_000, true, null, false),
                    new("glm-5-turbo", "GLM-5-Turbo", 200_000, 128_000, true, null, false),
                    new("glm-4.7", "GLM-4.7", 200_000, 128_000, true, null, false),
                    new("glm-4.7-flashx", "GLM-4.7-FlashX", 200_000, 128_000, true, null, false),
                    new("glm-4.7-flash", "GLM-4.7 Flash", 200_000, 128_000, true, null, false)
                ])
        };

    private sealed record ProviderDefinition(
        string Key,
        string DisplayName,
        string BaseUrl,
        string EndpointType,
        string PlatformUrl,
        string HelpText,
        double Temperature,
        IReadOnlyList<ModelInfoPayload> Models,
        Func<Dictionary<string, object?>, Dictionary<string, object?>>? BuildRequest = null,
        Func<Dictionary<string, string>, Dictionary<string, string>>? BuildHeaders = null,
        IReadOnlyList<string>? LegacyBaseUrls = null)
    {
        public IEnumerable<string> AllBaseUrls()
        {
            yield return BaseUrl;
            foreach (var url in LegacyBaseUrls ?? [])
            {
                yield return url;
            }
        }
    }

    private sealed record ConfiguredProvider(
        string Key,
        string DisplayName,
        string BaseUrl,
        string EndpointType,
        string ApiKey,
        double Temperature,
        IReadOnlyList<ModelInfoPayload> Models);

    private sealed class UserLlmConfigDocument
    {
        [JsonPropertyName("providers")]
        public List<UserProviderDocument> Providers { get; set; } = [];
    }

    private sealed class UserProviderDocument
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("base_url")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("endpoint_type")]
        public string EndpointType { get; set; } = string.Empty;

        [JsonPropertyName("chat_url")]
        public string ChatUrl { get; set; } = string.Empty;

        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }

        [JsonPropertyName("models")]
        public IReadOnlyList<ModelInfoPayload> Models { get; set; } = [];
    }

    private sealed class DiscoverModelsResponse
    {
        [JsonPropertyName("data")]
        public IReadOnlyList<DiscoveredModelItem> Data { get; set; } = [];
    }

    private sealed class DiscoveredModelItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("context_length")]
        public int ContextLength { get; set; }

        [JsonPropertyName("supports_image_in")]
        public bool? SupportsImageIn { get; set; }

        [JsonPropertyName("supports_video_in")]
        public bool? SupportsVideoIn { get; set; }

        [JsonPropertyName("supports_reasoning")]
        public bool? SupportsReasoning { get; set; }
    }
}
