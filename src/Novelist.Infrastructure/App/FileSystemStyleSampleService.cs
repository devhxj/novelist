using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemStyleSampleService : IStyleSampleService
{
    private const string StatsSchemaVersion = "style_sample_stats_v1";
    private const int PreviewLength = 120;
    private const int MaxNameLength = 160;
    private const int MaxContentLength = 200_000;
    private const int MaxTagCount = 32;
    private const int MaxTagLength = 64;
    private const int MaxQueryLength = 256;
    private const int MaxSourceTypeLength = 64;
    private const int MaxSourceIdLength = 256;
    private const int MaxSourceHashLength = 128;
    private const int MaxPageSize = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] InteriorityMarkers =
    [
        "想", "心里", "意识到", "明白", "知道", "觉得", "以为", "记得", "念头", "犹豫", "不该"
    ];

    private static readonly string[] SensoryMarkers =
    [
        "雨", "风", "声", "光", "灯", "冷", "热", "潮", "气味", "味", "疼", "痛", "滑", "针", "铁锈", "窗"
    ];

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemStyleSampleService(
        AppInitializationOptions? options = null,
        INovelService? novels = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<StyleSamplePayload> CreateSampleAsync(
        CreateStyleSamplePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = await NormalizeInputAsync(
            input.NovelId,
            input.IsGlobal,
            input.Name,
            input.Content,
            input.Tags,
            input.SourceMetadata,
            cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var id = AllocateId(store);
            var now = DateTimeOffset.UtcNow;
            var item = new StyleSampleStoreItem
            {
                SampleId = id,
                NovelId = normalized.NovelId,
                IsGlobal = input.IsGlobal,
                Name = normalized.Name,
                Content = normalized.Content,
                Preview = BuildPreview(normalized.Content),
                Tags = [.. normalized.Tags],
                StatsSchemaVersion = StatsSchemaVersion,
                Stats = CalculateStats(normalized.Content),
                SourceMetadata = normalized.SourceMetadata,
                CreatedAt = now,
                UpdatedAt = now
            };

            store.Items.Add(item);
            store.NextId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
            return ToSummary(item);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<StyleSamplePayload> UpdateSampleAsync(
        UpdateStyleSamplePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateSampleId(input.SampleId);
        var normalized = await NormalizeInputAsync(
            input.NovelId,
            input.IsGlobal,
            input.Name,
            input.Content,
            input.Tags,
            input.SourceMetadata,
            cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindSampleIndex(store, input.SampleId);
            var current = store.Items[index];
            var updated = new StyleSampleStoreItem
            {
                SampleId = current.SampleId,
                NovelId = normalized.NovelId,
                IsGlobal = input.IsGlobal,
                Name = normalized.Name,
                Content = normalized.Content,
                Preview = BuildPreview(normalized.Content),
                Tags = [.. normalized.Tags],
                StatsSchemaVersion = StatsSchemaVersion,
                Stats = CalculateStats(normalized.Content),
                SourceMetadata = normalized.SourceMetadata,
                CreatedAt = current.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            store.Items[index] = updated;
            await SaveAsync(store, cancellationToken);
            return ToSummary(updated);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteSampleAsync(DeleteStyleSamplePayload input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateSampleId(input.SampleId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            if (store.Items.RemoveAll(item => item.SampleId == input.SampleId) > 0)
            {
                await SaveAsync(store, cancellationToken);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<StyleSampleDetailPayload?> GetSampleAsync(
        GetStyleSamplePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateSampleId(input.SampleId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var item = store.Items.FirstOrDefault(sample => sample.SampleId == input.SampleId);
            return item is null ? null : ToDetail(item);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<PageResultPayload<StyleSamplePayload>> SearchSamplesAsync(
        SearchStyleSamplesPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var novelId = input.NovelId;
        if (novelId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input.NovelId), novelId, "Novel id must be positive.");
        }

        if (novelId is not null)
        {
            await EnsureNovelExistsAsync(novelId.Value, cancellationToken);
        }

        var query = NormalizeOptionalText(input.Query, nameof(input.Query), MaxQueryLength, allowLineBreaks: false);
        var tags = NormalizeTags(input.Tags);
        var page = NormalizePage(input.Page);
        var size = NormalizePageSize(input.Size);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var filtered = store.Items
                .Where(item => MatchesScope(item, novelId, input.IncludeGlobal))
                .Where(item => MatchesQuery(item, query))
                .Where(item => MatchesTags(item, tags))
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.SampleId)
                .ToArray();
            var total = filtered.LongLength;
            var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
            var items = filtered
                .Skip((page - 1) * size)
                .Take(size)
                .Select(ToSummary)
                .ToArray();
            return new PageResultPayload<StyleSamplePayload>(items, total, page, size, totalPages);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<NormalizedStyleSampleInput> NormalizeInputAsync(
        long? novelId,
        bool isGlobal,
        string? name,
        string? content,
        IReadOnlyList<string>? tags,
        StyleSampleSourceMetadataPayload? sourceMetadata,
        CancellationToken cancellationToken)
    {
        var normalizedNovelId = await NormalizeScopeAsync(novelId, isGlobal, cancellationToken);
        return new NormalizedStyleSampleInput(
            normalizedNovelId,
            NormalizeRequiredText(name, nameof(name), MaxNameLength, allowLineBreaks: false),
            NormalizeRequiredText(content, nameof(content), MaxContentLength, allowLineBreaks: true),
            NormalizeTags(tags),
            NormalizeSourceMetadata(sourceMetadata));
    }

    private async ValueTask<long?> NormalizeScopeAsync(long? novelId, bool isGlobal, CancellationToken cancellationToken)
    {
        if (isGlobal)
        {
            if (novelId is not null)
            {
                throw new ArgumentException("Global style samples must not be tied to a novel.", nameof(novelId));
            }

            return null;
        }

        if (novelId is null)
        {
            throw new ArgumentException("Per-novel style samples require a novel id.", nameof(novelId));
        }

        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }

        await EnsureNovelExistsAsync(novelId.Value, cancellationToken);
        return novelId.Value;
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private async ValueTask<StyleSampleStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new StyleSampleStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<StyleSampleStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Style sample store is empty or malformed.");

        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(StyleSampleStoreDocument store, CancellationToken cancellationToken)
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
            "style_samples",
            "index.json");
    }

    private static long AllocateId(StyleSampleStoreDocument store)
    {
        var maxExisting = store.Items.Count == 0 ? 0 : store.Items.Max(item => item.SampleId);
        var nextId = Math.Max(store.NextId, maxExisting + 1);
        if (nextId <= 0 || nextId == long.MaxValue)
        {
            throw new InvalidOperationException("Style sample id allocation is exhausted.");
        }

        return nextId;
    }

    private static int FindSampleIndex(StyleSampleStoreDocument store, long sampleId)
    {
        var index = store.Items.FindIndex(item => item.SampleId == sampleId);
        if (index < 0)
        {
            throw new ArgumentException($"Style sample '{sampleId}' does not exist.", nameof(sampleId));
        }

        return index;
    }

    private static bool MatchesScope(StyleSampleStoreItem item, long? novelId, bool includeGlobal)
    {
        if (item.IsGlobal)
        {
            return includeGlobal;
        }

        return novelId is not null && item.NovelId == novelId;
    }

    private static bool MatchesQuery(StyleSampleStoreItem item, string query)
    {
        return query.Length == 0 ||
            item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Content.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            item.Tags.Any(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesTags(StyleSampleStoreItem item, IReadOnlyList<string> tags)
    {
        return tags.Count == 0 ||
            tags.All(required => item.Tags.Any(tag => string.Equals(tag, required, StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength, bool allowLineBreaks)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength, allowLineBreaks);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(string? value, string name, int maxLength, bool allowLineBreaks)
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

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        if (tags.Count > MaxTagCount)
        {
            throw new ArgumentOutOfRangeException(nameof(tags), tags.Count, $"At most {MaxTagCount} tags are allowed.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var tag in tags)
        {
            var value = NormalizeOptionalText(tag, nameof(tags), MaxTagLength, allowLineBreaks: false);
            if (value.Length > 0 && seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private static StyleSampleSourceMetadataPayload? NormalizeSourceMetadata(StyleSampleSourceMetadataPayload? source)
    {
        if (source is null)
        {
            return null;
        }

        return new StyleSampleSourceMetadataPayload(
            NormalizeRequiredText(source.SourceType, nameof(source.SourceType), MaxSourceTypeLength, allowLineBreaks: false),
            NormalizeRequiredText(source.SourceId, nameof(source.SourceId), MaxSourceIdLength, allowLineBreaks: false),
            NormalizeRequiredText(source.SourceHash, nameof(source.SourceHash), MaxSourceHashLength, allowLineBreaks: false));
    }

    private static int NormalizePage(int page)
    {
        if (page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Page must be positive.");
        }

        return page;
    }

    private static int NormalizePageSize(int size)
    {
        if (size is <= 0 or > MaxPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"Page size must be between 1 and {MaxPageSize}.");
        }

        return size;
    }

    private static void ValidateSampleId(long sampleId)
    {
        if (sampleId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleId), sampleId, "Style sample id must be positive.");
        }
    }

    private static void ValidateStore(StyleSampleStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported style sample store version '{store.Version}'.");
        }

        if (store.NextId <= 0)
        {
            throw new InvalidOperationException("Style sample store next_id must be positive.");
        }

        if (store.Items.Any(item =>
            item.SampleId <= 0 ||
            (item.IsGlobal && item.NovelId is not null) ||
            (!item.IsGlobal && item.NovelId is null or <= 0)))
        {
            throw new InvalidOperationException("Style sample store contains invalid ids.");
        }

        if (store.Items.Select(item => item.SampleId).Distinct().Count() != store.Items.Count)
        {
            throw new InvalidOperationException("Style sample store contains duplicate ids.");
        }
    }

    private static StyleSamplePayload ToSummary(StyleSampleStoreItem item)
    {
        return new StyleSamplePayload(
            item.SampleId,
            item.NovelId,
            item.IsGlobal,
            item.Name,
            item.Preview,
            item.Tags,
            item.StatsSchemaVersion,
            item.Stats,
            item.SourceMetadata,
            item.CreatedAt,
            item.UpdatedAt);
    }

    private static StyleSampleDetailPayload ToDetail(StyleSampleStoreItem item)
    {
        return new StyleSampleDetailPayload(
            item.SampleId,
            item.NovelId,
            item.IsGlobal,
            item.Name,
            item.Content,
            item.Tags,
            item.StatsSchemaVersion,
            item.Stats,
            item.SourceMetadata,
            item.CreatedAt,
            item.UpdatedAt);
    }

    private static string BuildPreview(string content)
    {
        var result = new List<char>(Math.Min(content.Length, PreviewLength));
        var previousWasWhitespace = false;
        foreach (var ch in content)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (result.Count > 0 && !previousWasWhitespace)
                {
                    result.Add(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                result.Add(ch);
                previousWasWhitespace = false;
            }

            if (result.Count >= PreviewLength)
            {
                break;
            }
        }

        return new string([.. result]).Trim();
    }

    private static StyleSampleStatsPayload CalculateStats(string content)
    {
        var characterCount = CountTextCharacters(content);
        if (characterCount == 0)
        {
            return new StyleSampleStatsPayload(0, 0, 0, 0, 0, 0, 0);
        }

        var sentences = SplitSentences(content);
        var sentenceCount = Math.Max(1, sentences.Count);
        var punctuationCount = content.Count(char.IsPunctuation);
        return new StyleSampleStatsPayload(
            characterCount,
            sentenceCount,
            Round(characterCount / (double)sentenceCount),
            Ratio(CountDialogueCharacters(content), characterCount),
            Ratio(CountMarkerSentenceCharacters(sentences, InteriorityMarkers), characterCount),
            Ratio(CountMarkerSentenceCharacters(sentences, SensoryMarkers), characterCount),
            Round(punctuationCount / (double)characterCount * 100));
    }

    private static int CountTextCharacters(string value)
    {
        return value.Count(ch => !char.IsWhiteSpace(ch));
    }

    private static IReadOnlyList<string> SplitSentences(string content)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (!IsSentenceTerminator(content[i]))
            {
                continue;
            }

            AddSentence(content[start..(i + 1)]);
            start = i + 1;
        }

        if (start < content.Length)
        {
            AddSentence(content[start..]);
        }

        return sentences;

        void AddSentence(string sentence)
        {
            var normalized = sentence.Trim();
            if (normalized.Length > 0)
            {
                sentences.Add(normalized);
            }
        }
    }

    private static bool IsSentenceTerminator(char ch)
    {
        return ch is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '\n';
    }

    private static int CountDialogueCharacters(string content)
    {
        var count = 0;
        var inDialogue = false;
        foreach (var ch in content)
        {
            if (ch is '“' or '「' or '『')
            {
                inDialogue = true;
                continue;
            }

            if (ch is '”' or '」' or '』')
            {
                inDialogue = false;
                continue;
            }

            if (inDialogue && !char.IsWhiteSpace(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static int CountMarkerSentenceCharacters(IReadOnlyList<string> sentences, IReadOnlyList<string> markers)
    {
        return sentences
            .Where(sentence => markers.Any(marker => sentence.Contains(marker, StringComparison.Ordinal)))
            .Sum(CountTextCharacters);
    }

    private static double Ratio(int numerator, int denominator)
    {
        return Round(Math.Min(1, Math.Max(0, numerator / (double)denominator)));
    }

    private static double Round(double value)
    {
        return Math.Round(value, 4, MidpointRounding.AwayFromZero);
    }

    private sealed record NormalizedStyleSampleInput(
        long? NovelId,
        string Name,
        string Content,
        IReadOnlyList<string> Tags,
        StyleSampleSourceMetadataPayload? SourceMetadata);

    private sealed class StyleSampleStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<StyleSampleStoreItem> Items { get; set; } = [];
    }

    private sealed class StyleSampleStoreItem
    {
        [JsonPropertyName("sample_id")]
        public long SampleId { get; set; }

        [JsonPropertyName("novel_id")]
        public long? NovelId { get; set; }

        [JsonPropertyName("is_global")]
        public bool IsGlobal { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("preview")]
        public string Preview { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public IReadOnlyList<string> Tags { get; set; } = [];

        [JsonPropertyName("stats_schema_version")]
        public string StatsSchemaVersion { get; set; } = FileSystemStyleSampleService.StatsSchemaVersion;

        [JsonPropertyName("stats")]
        public StyleSampleStatsPayload Stats { get; set; } = new(0, 0, 0, 0, 0, 0, 0);

        [JsonPropertyName("source_metadata")]
        public StyleSampleSourceMetadataPayload? SourceMetadata { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
