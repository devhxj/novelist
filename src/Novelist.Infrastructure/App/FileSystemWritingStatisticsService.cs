using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemWritingStatisticsService : IWritingStatisticsService, IWritingDeltaRecorder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemWritingStatisticsService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask RecordWordDeltaAsync(
        long novelId,
        long chapterId,
        int wordDelta,
        CancellationToken cancellationToken)
    {
        if (wordDelta == 0)
        {
            return;
        }

        ValidateIds(novelId, chapterId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var id = AllocateId(store);
            store.Items.Add(new WritingLogRecord
            {
                Id = id,
                Date = TodayString(),
                NovelId = novelId,
                ChapterId = chapterId,
                WordDelta = wordDelta,
                CreatedAt = _timeProvider.GetUtcNow()
            });
            store.NextId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<DailyActivityPayload>> GetWritingActivityAsync(
        int months,
        CancellationToken cancellationToken)
    {
        if (months <= 0)
        {
            months = 12;
        }

        var cutoff = Today().AddMonths(-months);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.Items
                .Where(item => item.WordDelta > 0 && TryParseDate(item.Date, out var date) && date >= cutoff)
                .GroupBy(item => item.Date, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new DailyActivityPayload(group.Key, group.Sum(item => item.WordDelta)))
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<WritingStatsPayload> GetWritingStatsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<WritingLogRecord> items;
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            items = store.Items.ToArray();
        }
        finally
        {
            _mutex.Release();
        }

        var positive = items.Where(item => item.WordDelta > 0).ToArray();
        var activeDates = positive
            .Select(item => TryParseDate(item.Date, out var date) ? date : (DateOnly?)null)
            .Where(date => date is not null)
            .Select(date => date!.Value)
            .Distinct()
            .Order()
            .ToArray();
        var (current, longest) = ComputeStreaks(activeDates);
        var novels = await _novels.GetNovelsAsync(cancellationToken);

        return new WritingStatsPayload(
            positive.Sum(item => item.WordDelta),
            activeDates.Length,
            current,
            longest,
            novels.Count,
            await CountChaptersAsync(novels, cancellationToken));
    }

    private async ValueTask<long> CountChaptersAsync(
        IReadOnlyList<NovelPayload> novels,
        CancellationToken cancellationToken)
    {
        var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
        long count = 0;
        foreach (var novel in novels)
        {
            var path = SafeChildPath(
                Path.Combine(dataDirectory, "novels"),
                $"{novel.Id.ToString(CultureInfo.InvariantCulture)}/metadata/chapters.json");
            if (!File.Exists(path))
            {
                continue;
            }

            await using var stream = File.OpenRead(path);
            var store = await JsonSerializer.DeserializeAsync<ChapterStoreDocument>(stream, JsonOptions, cancellationToken);
            count += store?.Items.Count ?? 0;
        }

        return count;
    }

    private async ValueTask<WritingLogDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new WritingLogDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<WritingLogDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Writing log store is empty or malformed.");
        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(WritingLogDocument store, CancellationToken cancellationToken)
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
        return SafeChildPath(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken), "writing/log.json");
    }

    private static long AllocateId(WritingLogDocument store)
    {
        var maxExisting = store.Items.Count == 0 ? 0 : store.Items.Max(item => item.Id);
        var nextId = Math.Max(store.NextId, maxExisting + 1);
        if (nextId <= 0 || nextId == long.MaxValue)
        {
            throw new InvalidOperationException("Writing log id allocation is exhausted.");
        }

        return nextId;
    }

    private (int Current, int Longest) ComputeStreaks(IReadOnlyList<DateOnly> dates)
    {
        if (dates.Count == 0)
        {
            return (0, 0);
        }

        var longest = 1;
        var running = 1;
        for (var i = 1; i < dates.Count; i++)
        {
            if (dates[i].DayNumber - dates[i - 1].DayNumber == 1)
            {
                running++;
                longest = Math.Max(longest, running);
            }
            else if (dates[i].DayNumber - dates[i - 1].DayNumber > 1)
            {
                running = 1;
            }
        }

        var today = Today();
        var latest = dates[^1];
        var current = 0;
        if (latest == today || latest == today.AddDays(-1))
        {
            current = 1;
            for (var i = dates.Count - 1; i > 0; i--)
            {
                if (dates[i].DayNumber - dates[i - 1].DayNumber != 1)
                {
                    break;
                }

                current++;
            }
        }

        return (current, longest);
    }

    private string TodayString()
    {
        return Today().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private DateOnly Today()
    {
        return DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
    }

    private static bool TryParseDate(string value, out DateOnly date)
    {
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static void ValidateIds(long novelId, long chapterId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }

        if (chapterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterId), chapterId, "Chapter id must be positive.");
        }
    }

    private static void ValidateStore(WritingLogDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported writing log store version '{store.Version}'.");
        }

        if (store.NextId <= 0)
        {
            throw new InvalidOperationException("Writing log store next_id must be positive.");
        }

        if (store.Items.Any(item => item.Id <= 0 || item.NovelId <= 0 || item.ChapterId <= 0))
        {
            throw new InvalidOperationException("Writing log store contains invalid ids.");
        }

        if (store.Items.Select(item => item.Id).Distinct().Count() != store.Items.Count)
        {
            throw new InvalidOperationException("Writing log store contains duplicate ids.");
        }
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
            throw new InvalidOperationException("Resolved path escapes the novelist data directory.");
        }

        return fullPath;
    }

    private sealed class WritingLogDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_id")]
        public long NextId { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<WritingLogRecord> Items { get; set; } = [];
    }

    private sealed class WritingLogRecord
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("novel_id")]
        public long NovelId { get; set; }

        [JsonPropertyName("chapter_id")]
        public long ChapterId { get; set; }

        [JsonPropertyName("word_delta")]
        public int WordDelta { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }

    private sealed class ChapterStoreDocument
    {
        [JsonPropertyName("items")]
        public List<ChapterPayload> Items { get; set; } = [];
    }
}
