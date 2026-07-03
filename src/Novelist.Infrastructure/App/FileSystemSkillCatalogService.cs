using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemSkillCatalogService : ISkillCatalogService
{
    private const int MaxSampleLength = 200_000;
    private const int ResponseReadLimitBytes = 512 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly Lazy<IReadOnlyList<ParsedSkillDocument>> BuiltinSkills = new(SkillDocuments.LoadBuiltin);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly ILlmConfigurationService _llm;
    private readonly HttpClient _httpClient;

    public FileSystemSkillCatalogService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        ILlmConfigurationService? llm = null,
        HttpClient? httpClient = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _llm = llm ?? new FileSystemLlmConfigurationService(_options);
        _httpClient = httpClient ?? new HttpClient();
    }

    public async ValueTask<IReadOnlyList<SkillMetaPayload>> ListSkillsAsync(
        ListSkillsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
        var novel = SkillDocuments.ScanDirectory(NovelSkillsDirectory(dataDirectory, input.NovelId), "user");
        var user = SkillDocuments.ScanDirectory(UserSkillsDirectory(dataDirectory), "user");
        var builtin = BuiltinSkills.Value;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SkillMetaPayload>();
        AddLayer(novel, "novel");
        AddLayer(user, "user");
        AddLayer(builtin, "builtin");
        return result;

        void AddLayer(IReadOnlyList<ParsedSkillDocument> skills, string source)
        {
            foreach (var skill in skills.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (!seen.Add(skill.Name))
                {
                    continue;
                }

                result.Add(skill.ToMeta(source));
            }
        }
    }

    public async ValueTask<IReadOnlyList<SlashCommandPayload>> ListSlashCommandsAsync(
        ListSlashCommandsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var skills = await ListSkillsAsync(new ListSkillsPayload(input.NovelId), cancellationToken);
        return skills
            .Select(skill => new SlashCommandPayload(skill.Name, skill.Description, skill.Mode))
            .ToArray();
    }

    public async ValueTask DeleteSkillAsync(DeleteSkillPayload input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var source = NormalizeSource(input.Source);
        var name = SkillDocuments.NormalizeSkillName(input.Name);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
        var directory = source switch
        {
            "novel" => NovelSkillsDirectory(dataDirectory, input.NovelId),
            "user" => UserSkillsDirectory(dataDirectory),
            _ => throw new ArgumentException("Only novel and user skills can be deleted.", nameof(input.Source))
        };

        var path = SafeChildPath(directory, $"{name}.md");
        if (!File.Exists(path))
        {
            throw new ArgumentException($"Skill '{name}' does not exist.", nameof(input.Name));
        }

        File.Delete(path);
    }

    public async ValueTask<ExtractStyleResultPayload> ExtractStyleAsync(
        ExtractStylePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var sample = NormalizeRequiredText(input.Sample, nameof(input.Sample), MaxSampleLength, allowLineBreaks: true);
        var providerName = NormalizeRequiredText(input.ProviderName, nameof(input.ProviderName), 128, allowLineBreaks: false);
        var modelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), 256, allowLineBreaks: false);
        var reasoningEffort = NormalizeOptionalText(input.ReasoningEffort, nameof(input.ReasoningEffort), 128, allowLineBreaks: false);

        var config = await _llm.GetConfigAsync(cancellationToken);
        var provider = config.Providers.FirstOrDefault(item =>
            string.Equals(item.Key, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null || string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw ProviderError("模型供应商未配置 API Key，无法提取写作风格。", retryable: false);
        }

        if (string.IsNullOrWhiteSpace(provider.ChatUrl))
        {
            throw ProviderError("模型供应商未配置 Chat Completions 地址。", retryable: false);
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = BuildExtractMessages(sample),
            ["temperature"] = provider.Temperature,
            ["stream"] = false
        };
        ApplyProviderRequestAdjustments(provider.Key, payload, reasoningEffort);

        using var request = new HttpRequestMessage(HttpMethod.Post, provider.ChatUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        ApplyProviderHeaders(provider.Key, request, provider.ApiKey);

        using var response = await SendAsync(request, cancellationToken);
        var body = await ReadContentLimitedAsync(response.Content, cancellationToken);
        EnsureSuccess(response.StatusCode, body, provider.ApiKey);

        var text = ExtractMessageContent(body, provider.ApiKey);
        var skill = SkillDocuments.Parse(text, "ai");
        var fileName = SkillDocuments.NormalizeSkillName(skill.Name);
        return new ExtractStyleResultPayload(skill.Name, skill.Description, skill.RawContent, $"skills/{fileName}.md");
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

    private static IReadOnlyList<Dictionary<string, string>> BuildExtractMessages(string sample)
    {
        return
        [
            new Dictionary<string, string>
            {
                ["role"] = "system",
                ["content"] = ExtractSystemPrompt
            },
            new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = $"请分析以下文本的写作风格：\n\n```\n{sample}\n```"
            }
        ];
    }

    private static void ApplyProviderRequestAdjustments(
        string providerKey,
        Dictionary<string, object?> payload,
        string reasoningEffort)
    {
        if (!string.IsNullOrWhiteSpace(reasoningEffort) &&
            providerKey is not ("qwen" or "moonshot"))
        {
            payload["reasoning_effort"] = reasoningEffort;
        }

        if (string.Equals(providerKey, "minimax", StringComparison.OrdinalIgnoreCase))
        {
            payload["reasoning_split"] = true;
        }
    }

    private static void ApplyProviderHeaders(
        string providerKey,
        HttpRequestMessage request,
        string apiKey)
    {
        if (string.Equals(providerKey, "mimo", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("api-key", apiKey);
            return;
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    private static void EnsureSuccess(HttpStatusCode statusCode, byte[] body, string apiKey)
    {
        if ((int)statusCode < 400)
        {
            return;
        }

        var retryable = statusCode is HttpStatusCode.RequestTimeout or
            (HttpStatusCode)429 or
            >= HttpStatusCode.InternalServerError;
        throw ProviderError($"[{(int)statusCode}] {SanitizeProviderBody(body, apiKey)}", retryable);
    }

    private static string ExtractMessageContent(byte[] body, string apiKey)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
            var content = response?.Choices.FirstOrDefault()?.Message.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw ProviderError("LLM 返回为空，未能生成写作风格。", retryable: true);
            }

            return content;
        }
        catch (JsonException ex)
        {
            throw ProviderError($"解析 LLM 响应失败: {SanitizeProviderBody(body, apiKey)} ({ex.Message})", retryable: false);
        }
    }

    private static string NormalizeSource(string? source)
    {
        return NormalizeRequiredText(source, nameof(source), 32, allowLineBreaks: false) switch
        {
            "novel" => "novel",
            "user" => "user",
            "builtin" => "builtin",
            _ => throw new ArgumentException("Skill source must be novel, user, or builtin.", nameof(source))
        };
    }

    private static string NormalizeRequiredText(
        string? value,
        string name,
        int maxLength,
        bool allowLineBreaks)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength, allowLineBreaks);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(
        string? value,
        string name,
        int maxLength,
        bool allowLineBreaks)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(ch => char.IsControl(ch) && (!allowLineBreaks || ch is not ('\r' or '\n' or '\t'))))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static string NovelSkillsDirectory(string dataDirectory, long novelId)
    {
        return SafeChildPath(Path.Combine(dataDirectory, "novels"), $"{novelId}/skills");
    }

    private static string UserSkillsDirectory(string dataDirectory)
    {
        return SafeChildPath(dataDirectory, "skills");
    }

    private static string SafeChildPath(string parentDirectory, string relativePath)
    {
        var parent = Path.GetFullPath(parentDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(parent, relativePath));
        var parentWithSeparator = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(parentWithSeparator, comparison))
        {
            throw new InvalidContentPathException(relativePath, "Resolved path escapes the novelist data directory.");
        }

        return fullPath;
    }

    private static string SanitizeProviderBody(byte[] body, string apiKey)
    {
        var text = Encoding.UTF8.GetString(body).Trim();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            text = text.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        }

        return text.Length == 0 ? "服务商返回错误，但响应体为空。" : text;
    }

    private static BridgeRequestException ProviderError(string message, bool retryable)
    {
        return new BridgeRequestException(BridgeErrorCodes.LlmProviderError, message, retryable: retryable);
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public IReadOnlyList<Choice> Choices { get; set; } = [];
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")]
        public Message Message { get; set; } = new();
    }

    private sealed class Message
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private const string ExtractSystemPrompt = """
        你是一位专业的写作风格分析师。请分析用户提供的文本，从以下六个维度拆解其写作风格：

        1. **句式特征**：句子长度分布、长短句搭配模式、句式变化程度
        2. **用词习惯**：词汇量级、口语/书面语倾向、高频词类型、成语俗语使用
        3. **修辞手法**：常用修辞（比喻、拟人、排比、反复、对比等）及其使用频率
        4. **节奏控制**：段落组织方式、标点使用偏好、断句节奏
        5. **叙事视角与距离**：人称选择、叙事者与内容的距离感
        6. **氛围与语调**：情绪基调、幽默/严肃/温暖/冷峻、语言温度

        请根据分析结果为这个风格起一个贴切的中文名称，并严格按以下 Markdown 格式输出（YAML frontmatter 必须包含在开头的 --- 和结尾的 --- 之间）：

        ---
        name: {风格名称}
        description: {一句话简要描述该风格，描述何时使用}
        category: 风格仿写
        mode: auto
        author: ai
        version: 1
        ---

        # {风格名称}

        ## 风格概述
        简要概括该风格的整体特点。

        ## 句式特征
        详细分析句式特点、长短句搭配等。

        ## 用词习惯
        详细分析用词偏好、词汇选择倾向等。

        ## 修辞手法
        详细分析使用的修辞手法及其特点。

        ## 节奏控制
        详细分析段落组织、断句节奏等。

        ## 叙事视角与距离
        详细分析叙事者位置、与内容的距离感。

        ## 氛围与语调
        详细分析情绪基调、语言温度等。

        ## 仿写要点
        提炼 3-5 条可操作的仿写指导原则。

        注意：你提取的是写作风格的模式和规律，而非原文的具体内容。分析时请归纳句式结构、用词偏好、修辞模式等抽象特征，不要直接照搬原文的句子和表达。
        """;
}
