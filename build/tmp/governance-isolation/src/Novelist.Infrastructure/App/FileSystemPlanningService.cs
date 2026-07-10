using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemPlanningService : IPlanningService
{
    private const int MaxNameLength = 200;
    private const int MaxShortTextLength = 128;
    private const int MaxLongTextLength = 20_000;
    private const int MaxChapterNumber = 999_999;

    private static readonly string[] PlanScopes = ["next", "near", "far"];
    private static readonly string[] TimelineCategories = ["foreshadowing", "user_directive"];
    private static readonly string[] TimelineStatuses = ["pending", "resolved", "abandoned"];
    private static readonly string[] TimelineSources = ["ai", "user"];
    private static readonly string[] StoryArcTypes = ["main", "sub", "character", "background"];
    private static readonly string[] StoryArcStatuses = ["active", "paused", "completed", "abandoned"];
    private static readonly string[] ArcNodeStatuses = ["pending", "completed", "abandoned"];
    private static readonly string[] ReaderPerspectiveTypes = ["known", "suspense", "misconception"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemPlanningService(
        AppInitializationOptions? options = null,
        INovelService? novels = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<IReadOnlyList<ChapterPlanPayload>> GetChapterPlansAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return PlanScopes
                .Select(scope =>
                {
                    var stored = store.ChapterPlans.SingleOrDefault(plan =>
                        plan.NovelId == novelId &&
                        string.Equals(plan.Scope, scope, StringComparison.Ordinal));
                    return stored ?? new ChapterPlanPayload(novelId, scope, string.Empty);
                })
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask UpdateChapterPlanAsync(
        long novelId,
        UpdateChapterPlanPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        var scope = NormalizeEnum(input.Scope, nameof(input.Scope), PlanScopes);
        var content = NormalizeOptionalText(input.Content, nameof(input.Content), MaxLongTextLength, allowLineBreaks: true);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = store.ChapterPlans.FindIndex(plan =>
                plan.NovelId == novelId &&
                string.Equals(plan.Scope, scope, StringComparison.Ordinal));
            var plan = new ChapterPlanPayload(novelId, scope, content);
            if (index < 0)
            {
                store.ChapterPlans.Add(plan);
            }
            else
            {
                store.ChapterPlans[index] = plan;
            }

            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<TimelineEntryPayload>> GetTimelineEntriesAsync(
        long novelId,
        int fromChapter,
        int toChapter,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        ValidateOptionalChapterBoundary(fromChapter, nameof(fromChapter));
        ValidateOptionalChapterBoundary(toChapter, nameof(toChapter));

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.TimelineEntries
                .Where(entry => entry.NovelId == novelId)
                .Where(entry => fromChapter <= 0 || entry.TargetChapter >= fromChapter)
                .Where(entry => toChapter <= 0 || entry.TargetChapter <= toChapter)
                .OrderBy(entry => entry.TargetChapter)
                .ThenByDescending(entry => entry.Importance)
                .ThenBy(entry => entry.Id)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<TimelineEntryPayload> CreateTimelineEntryAsync(
        long novelId,
        CreateTimelineEntryPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        var category = NormalizeEnum(input.Category, nameof(input.Category), TimelineCategories);
        var title = NormalizeRequiredText(input.Title, nameof(input.Title), MaxNameLength, allowLineBreaks: false);
        var content = NormalizeOptionalText(input.Content, nameof(input.Content), MaxLongTextLength, allowLineBreaks: true);
        var detailJson = NormalizeOptionalText(input.DetailJson, nameof(input.DetailJson), MaxLongTextLength, allowLineBreaks: true);
        var targetChapter = ValidateRequiredChapter(input.TargetChapter, nameof(input.TargetChapter));
        var importance = ValidateImportance(input.Importance ?? 3, nameof(input.Importance));
        var sourceChapterId = ValidateNonNegativeId(input.SourceChapterId ?? 0, nameof(input.SourceChapterId));
        var source = string.IsNullOrWhiteSpace(input.Source)
            ? "user"
            : NormalizeEnum(input.Source, nameof(input.Source), TimelineSources);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var id = AllocateId(store.NextTimelineEntryId, store.TimelineEntries.Select(entry => entry.Id), "Timeline entry");
            var now = DateTimeOffset.UtcNow;
            var entry = new TimelineEntryPayload(
                id,
                novelId,
                category,
                "pending",
                title,
                content,
                detailJson,
                targetChapter,
                importance,
                sourceChapterId,
                source,
                0,
                now,
                now);

            store.TimelineEntries.Add(entry);
            store.NextTimelineEntryId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
            return entry;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask UpdateTimelineEntryAsync(
        long novelId,
        long entryId,
        UpdateTimelineEntryPayload input,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(entryId, nameof(entryId));
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindTimelineEntryIndex(store, novelId, entryId);
            var current = store.TimelineEntries[index];
            store.TimelineEntries[index] = current with
            {
                Title = input.Title is not null
                    ? NormalizeRequiredText(input.Title, nameof(input.Title), MaxNameLength, allowLineBreaks: false)
                    : current.Title,
                Content = input.Content is not null
                    ? NormalizeOptionalText(input.Content, nameof(input.Content), MaxLongTextLength, allowLineBreaks: true)
                    : current.Content,
                DetailJson = input.DetailJson is not null
                    ? NormalizeOptionalText(input.DetailJson, nameof(input.DetailJson), MaxLongTextLength, allowLineBreaks: true)
                    : current.DetailJson,
                TargetChapter = input.TargetChapter is not null
                    ? ValidateRequiredChapter(input.TargetChapter.Value, nameof(input.TargetChapter))
                    : current.TargetChapter,
                Importance = input.Importance is not null
                    ? ValidateImportance(input.Importance.Value, nameof(input.Importance))
                    : current.Importance,
                Status = input.Status is not null
                    ? NormalizeEnum(input.Status, nameof(input.Status), TimelineStatuses)
                    : current.Status,
                ResolvedChapterId = input.ResolvedChapterId is not null
                    ? ValidateNonNegativeId(input.ResolvedChapterId.Value, nameof(input.ResolvedChapterId))
                    : current.ResolvedChapterId,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteTimelineEntryAsync(
        long novelId,
        long entryId,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(entryId, nameof(entryId));
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindTimelineEntryIndex(store, novelId, entryId);
            store.TimelineEntries.RemoveAll(entry => entry.NovelId == novelId && entry.Id == entryId);
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<StoryArcPayload>> GetStoryArcsAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.StoryArcs
                .Where(arc => arc.NovelId == novelId)
                .OrderByDescending(arc => arc.Importance)
                .ThenBy(arc => arc.CreatedAt)
                .ThenBy(arc => arc.Id)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<StoryArcPayload> CreateStoryArcAsync(
        long novelId,
        CreateStoryArcPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        var name = NormalizeRequiredText(input.Name, nameof(input.Name), MaxNameLength, allowLineBreaks: false);
        var arcType = NormalizeEnum(input.ArcType, nameof(input.ArcType), StoryArcTypes);
        var description = NormalizeOptionalText(input.Description, nameof(input.Description), MaxLongTextLength, allowLineBreaks: true);
        var importance = ValidateImportance(input.Importance ?? 1, nameof(input.Importance));

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var id = AllocateId(store.NextStoryArcId, store.StoryArcs.Select(arc => arc.Id), "Story arc");
            var now = DateTimeOffset.UtcNow;
            var arc = new StoryArcPayload(
                id,
                novelId,
                name,
                description,
                arcType,
                importance,
                "active",
                string.Empty,
                now,
                now);

            store.StoryArcs.Add(arc);
            store.NextStoryArcId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
            return arc;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask UpdateStoryArcAsync(
        long novelId,
        long arcId,
        UpdateStoryArcPayload input,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(arcId, nameof(arcId));
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindStoryArcIndex(store, novelId, arcId);
            var current = store.StoryArcs[index];
            store.StoryArcs[index] = current with
            {
                Name = input.Name is not null
                    ? NormalizeRequiredText(input.Name, nameof(input.Name), MaxNameLength, allowLineBreaks: false)
                    : current.Name,
                Description = input.Description is not null
                    ? NormalizeOptionalText(input.Description, nameof(input.Description), MaxLongTextLength, allowLineBreaks: true)
                    : current.Description,
                ArcType = input.ArcType is not null
                    ? NormalizeEnum(input.ArcType, nameof(input.ArcType), StoryArcTypes)
                    : current.ArcType,
                Importance = input.Importance is not null
                    ? ValidateImportance(input.Importance.Value, nameof(input.Importance))
                    : current.Importance,
                Status = input.Status is not null
                    ? NormalizeEnum(input.Status, nameof(input.Status), StoryArcStatuses)
                    : current.Status,
                ReactivateAt = input.ReactivateAt is not null
                    ? NormalizeOptionalText(input.ReactivateAt, nameof(input.ReactivateAt), MaxLongTextLength, allowLineBreaks: true)
                    : current.ReactivateAt,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteStoryArcAsync(
        long novelId,
        long arcId,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(arcId, nameof(arcId));
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindStoryArcIndex(store, novelId, arcId);
            store.ArcNodes.RemoveAll(node => node.NovelId == novelId && node.StoryArcId == arcId);
            store.StoryArcs.RemoveAll(arc => arc.NovelId == novelId && arc.Id == arcId);
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ArcNodePayload>> GetArcNodesAsync(
        long novelId,
        int fromChapter,
        int toChapter,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);
        ValidateOptionalChapterBoundary(fromChapter, nameof(fromChapter));
        ValidateOptionalChapterBoundary(toChapter, nameof(toChapter));

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.ArcNodes
                .Where(node => node.NovelId == novelId)
                .Where(node => fromChapter <= 0 || node.TargetChapter >= fromChapter)
                .Where(node => toChapter <= 0 || node.TargetChapter <= toChapter)
                .OrderBy(node => node.StoryArcId)
                .ThenBy(node => node.TargetChapter)
                .ThenBy(node => node.Id)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ArcNodePayload> CreateArcNodeAsync(
        long novelId,
        CreateArcNodePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        var storyArcId = ValidateEntityId(input.StoryArcId, nameof(input.StoryArcId));
        var title = NormalizeRequiredText(input.Title, nameof(input.Title), MaxNameLength, allowLineBreaks: false);
        var description = NormalizeOptionalText(input.Description, nameof(input.Description), MaxLongTextLength, allowLineBreaks: true);
        var targetChapter = ValidateRequiredChapter(input.TargetChapter, nameof(input.TargetChapter));

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindStoryArcIndex(store, novelId, storyArcId);
            var id = AllocateId(store.NextArcNodeId, store.ArcNodes.Select(node => node.Id), "Arc node");
            var now = DateTimeOffset.UtcNow;
            var node = new ArcNodePayload(
                id,
                novelId,
                storyArcId,
                title,
                description,
                targetChapter,
                0,
                "pending",
                now,
                now);

            store.ArcNodes.Add(node);
            store.NextArcNodeId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
            return node;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask UpdateArcNodeAsync(
        long novelId,
        long nodeId,
        UpdateArcNodePayload input,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(nodeId, nameof(nodeId));
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindArcNodeIndex(store, novelId, nodeId);
            var current = store.ArcNodes[index];
            store.ArcNodes[index] = current with
            {
                Title = input.Title is not null
                    ? NormalizeRequiredText(input.Title, nameof(input.Title), MaxNameLength, allowLineBreaks: false)
                    : current.Title,
                Description = input.Description is not null
                    ? NormalizeOptionalText(input.Description, nameof(input.Description), MaxLongTextLength, allowLineBreaks: true)
                    : current.Description,
                TargetChapter = input.TargetChapter is not null
                    ? ValidateRequiredChapter(input.TargetChapter.Value, nameof(input.TargetChapter))
                    : current.TargetChapter,
                ActualChapter = input.ActualChapter is not null
                    ? ValidateOptionalChapterBoundary(input.ActualChapter.Value, nameof(input.ActualChapter))
                    : current.ActualChapter,
                Status = input.Status is not null
                    ? NormalizeEnum(input.Status, nameof(input.Status), ArcNodeStatuses)
                    : current.Status,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteArcNodeAsync(
        long novelId,
        long nodeId,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(nodeId, nameof(nodeId));
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindArcNodeIndex(store, novelId, nodeId);
            store.ArcNodes.RemoveAll(node => node.NovelId == novelId && node.Id == nodeId);
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReaderPerspectivePayload>> GetReaderPerspectivesAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            return store.ReaderPerspectives
                .Where(item => item.NovelId == novelId)
                .OrderBy(item => item.Type, StringComparer.Ordinal)
                .ThenBy(item => item.PlantedChapter)
                .ThenBy(item => item.Id)
                .ToArray();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReaderPerspectivePayload> CreateReaderPerspectiveAsync(
        long novelId,
        CreateReaderPerspectivePayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        var type = NormalizeEnum(input.Type, nameof(input.Type), ReaderPerspectiveTypes);
        var content = NormalizeRequiredText(input.Content, nameof(input.Content), MaxLongTextLength, allowLineBreaks: true);
        var plantedChapter = ValidateRequiredChapter(input.PlantedChapter, nameof(input.PlantedChapter));
        var relatedTruth = NormalizeOptionalText(input.RelatedTruth, nameof(input.RelatedTruth), MaxLongTextLength, allowLineBreaks: true);
        var revealedChapter = ValidateOptionalChapterBoundary(input.RevealedChapter ?? 0, nameof(input.RevealedChapter));

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var id = AllocateId(
                store.NextReaderPerspectiveId,
                store.ReaderPerspectives.Select(item => item.Id),
                "Reader perspective");
            var item = new ReaderPerspectivePayload(
                id,
                novelId,
                type,
                content,
                relatedTruth,
                plantedChapter,
                revealedChapter,
                DateTimeOffset.UtcNow);

            store.ReaderPerspectives.Add(item);
            store.NextReaderPerspectiveId = checked(id + 1);
            await SaveAsync(store, cancellationToken);
            return item;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask UpdateReaderPerspectiveAsync(
        long novelId,
        long perspectiveId,
        UpdateReaderPerspectivePayload input,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(perspectiveId, nameof(perspectiveId));
        ArgumentNullException.ThrowIfNull(input);
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var index = FindReaderPerspectiveIndex(store, novelId, perspectiveId);
            var current = store.ReaderPerspectives[index];
            store.ReaderPerspectives[index] = current with
            {
                Type = input.Type is not null
                    ? NormalizeEnum(input.Type, nameof(input.Type), ReaderPerspectiveTypes)
                    : current.Type,
                Content = input.Content is not null
                    ? NormalizeRequiredText(input.Content, nameof(input.Content), MaxLongTextLength, allowLineBreaks: true)
                    : current.Content,
                RelatedTruth = input.RelatedTruth is not null
                    ? NormalizeOptionalText(input.RelatedTruth, nameof(input.RelatedTruth), MaxLongTextLength, allowLineBreaks: true)
                    : current.RelatedTruth,
                PlantedChapter = input.PlantedChapter is not null
                    ? ValidateRequiredChapter(input.PlantedChapter.Value, nameof(input.PlantedChapter))
                    : current.PlantedChapter,
                RevealedChapter = input.RevealedChapter is not null
                    ? ValidateOptionalChapterBoundary(input.RevealedChapter.Value, nameof(input.RevealedChapter))
                    : current.RevealedChapter
            };

            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteReaderPerspectiveAsync(
        long novelId,
        long perspectiveId,
        CancellationToken cancellationToken)
    {
        ValidateEntityId(perspectiveId, nameof(perspectiveId));
        await EnsureNovelExistsAsync(novelId, cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            _ = FindReaderPerspectiveIndex(store, novelId, perspectiveId);
            store.ReaderPerspectives.RemoveAll(item => item.NovelId == novelId && item.Id == perspectiveId);
            await SaveAsync(store, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<PlanningStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new PlanningStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<PlanningStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Planning store is empty or malformed.");

        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(
        PlanningStoreDocument store,
        CancellationToken cancellationToken)
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

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken), "planning", "index.json");
    }

    private static int FindTimelineEntryIndex(PlanningStoreDocument store, long novelId, long entryId)
    {
        var index = store.TimelineEntries.FindIndex(entry => entry.NovelId == novelId && entry.Id == entryId);
        if (index < 0)
        {
            throw new ArgumentException($"Timeline entry '{entryId}' does not exist.", nameof(entryId));
        }

        return index;
    }

    private static int FindStoryArcIndex(PlanningStoreDocument store, long novelId, long arcId)
    {
        var index = store.StoryArcs.FindIndex(arc => arc.NovelId == novelId && arc.Id == arcId);
        if (index < 0)
        {
            throw new ArgumentException($"Story arc '{arcId}' does not exist.", nameof(arcId));
        }

        return index;
    }

    private static int FindArcNodeIndex(PlanningStoreDocument store, long novelId, long nodeId)
    {
        var index = store.ArcNodes.FindIndex(node => node.NovelId == novelId && node.Id == nodeId);
        if (index < 0)
        {
            throw new ArgumentException($"Arc node '{nodeId}' does not exist.", nameof(nodeId));
        }

        return index;
    }

    private static int FindReaderPerspectiveIndex(PlanningStoreDocument store, long novelId, long perspectiveId)
    {
        var index = store.ReaderPerspectives.FindIndex(item => item.NovelId == novelId && item.Id == perspectiveId);
        if (index < 0)
        {
            throw new ArgumentException($"Reader perspective '{perspectiveId}' does not exist.", nameof(perspectiveId));
        }

        return index;
    }

    private static long AllocateId(long nextId, IEnumerable<long> existingIds, string label)
    {
        var ids = existingIds.ToArray();
        var maxExisting = ids.Length == 0 ? 0 : ids.Max();
        var allocated = Math.Max(nextId, maxExisting + 1);
        if (allocated <= 0 || allocated == long.MaxValue)
        {
            throw new InvalidOperationException($"{label} id allocation is exhausted.");
        }

        return allocated;
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

        if (normalized.Any(ch => IsDisallowedControl(ch, allowLineBreaks)))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static string NormalizeEnum(string? value, string name, IReadOnlyCollection<string> allowedValues)
    {
        var normalized = NormalizeRequiredText(value, name, MaxShortTextLength, allowLineBreaks: false);
        if (!allowedValues.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Value must be one of: {string.Join(", ", allowedValues)}.",
                name);
        }

        return normalized;
    }

    private static int ValidateImportance(int value, string name)
    {
        if (value is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(name, value, "Importance must be between 1 and 5.");
        }

        return value;
    }

    private static int ValidateRequiredChapter(int value, string name)
    {
        if (value is <= 0 or > MaxChapterNumber)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"Chapter number must be between 1 and {MaxChapterNumber}.");
        }

        return value;
    }

    private static int ValidateOptionalChapterBoundary(int value, string name)
    {
        if (value is < 0 or > MaxChapterNumber)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"Chapter number must be between 0 and {MaxChapterNumber}.");
        }

        return value;
    }

    private static long ValidateNonNegativeId(long value, string name)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Identifier must not be negative.");
        }

        return value;
    }

    private static long ValidateEntityId(long entityId, string argumentName)
    {
        if (entityId <= 0)
        {
            throw new ArgumentOutOfRangeException(argumentName, entityId, "Entity id must be positive.");
        }

        return entityId;
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static bool IsDisallowedControl(char value, bool allowLineBreaks)
    {
        return char.IsControl(value) &&
            (!allowLineBreaks || value is not ('\r' or '\n' or '\t'));
    }

    private static void ValidateStore(PlanningStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported planning store version '{store.Version}'.");
        }

        ValidateNextId(store.NextTimelineEntryId, nameof(store.NextTimelineEntryId));
        ValidateNextId(store.NextStoryArcId, nameof(store.NextStoryArcId));
        ValidateNextId(store.NextArcNodeId, nameof(store.NextArcNodeId));
        ValidateNextId(store.NextReaderPerspectiveId, nameof(store.NextReaderPerspectiveId));

        if (store.ChapterPlans.Any(plan =>
                plan.NovelId <= 0 ||
                !PlanScopes.Contains(plan.Scope, StringComparer.Ordinal)) ||
            store.TimelineEntries.Any(entry =>
                entry.Id <= 0 ||
                entry.NovelId <= 0 ||
                entry.TargetChapter is <= 0 or > MaxChapterNumber ||
                entry.Importance is < 1 or > 5 ||
                entry.SourceChapterId < 0 ||
                entry.ResolvedChapterId < 0 ||
                !TimelineCategories.Contains(entry.Category, StringComparer.Ordinal) ||
                !TimelineStatuses.Contains(entry.Status, StringComparer.Ordinal) ||
                !TimelineSources.Contains(entry.Source, StringComparer.Ordinal)) ||
            store.StoryArcs.Any(arc =>
                arc.Id <= 0 ||
                arc.NovelId <= 0 ||
                arc.Importance is < 1 or > 5 ||
                !StoryArcTypes.Contains(arc.ArcType, StringComparer.Ordinal) ||
                !StoryArcStatuses.Contains(arc.Status, StringComparer.Ordinal)) ||
            store.ArcNodes.Any(node =>
                node.Id <= 0 ||
                node.NovelId <= 0 ||
                node.StoryArcId <= 0 ||
                node.TargetChapter is <= 0 or > MaxChapterNumber ||
                node.ActualChapter is < 0 or > MaxChapterNumber ||
                !ArcNodeStatuses.Contains(node.Status, StringComparer.Ordinal)) ||
            store.ReaderPerspectives.Any(item =>
                item.Id <= 0 ||
                item.NovelId <= 0 ||
                item.PlantedChapter is <= 0 or > MaxChapterNumber ||
                item.RevealedChapter is < 0 or > MaxChapterNumber ||
                !ReaderPerspectiveTypes.Contains(item.Type, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Planning store contains invalid values.");
        }

        EnsureUnique(store.ChapterPlans.Select(plan => $"{plan.NovelId}:{plan.Scope}"), "chapter plan");
        EnsureUnique(store.TimelineEntries.Select(entry => entry.Id), "timeline entry");
        EnsureUnique(store.StoryArcs.Select(arc => arc.Id), "story arc");
        EnsureUnique(store.ArcNodes.Select(node => node.Id), "arc node");
        EnsureUnique(store.ReaderPerspectives.Select(item => item.Id), "reader perspective");
    }

    private static void ValidateNextId(long value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidOperationException($"{name} must be positive.");
        }
    }

    private static void EnsureUnique<T>(IEnumerable<T> values, string label)
        where T : notnull
    {
        var items = values.ToArray();
        if (items.Distinct().Count() != items.Length)
        {
            throw new InvalidOperationException($"Planning store contains duplicate {label} ids.");
        }
    }

    private sealed class PlanningStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("next_timeline_entry_id")]
        public long NextTimelineEntryId { get; set; } = 1;

        [JsonPropertyName("next_story_arc_id")]
        public long NextStoryArcId { get; set; } = 1;

        [JsonPropertyName("next_arc_node_id")]
        public long NextArcNodeId { get; set; } = 1;

        [JsonPropertyName("next_reader_perspective_id")]
        public long NextReaderPerspectiveId { get; set; } = 1;

        [JsonPropertyName("chapter_plans")]
        public List<ChapterPlanPayload> ChapterPlans { get; set; } = [];

        [JsonPropertyName("timeline_entries")]
        public List<TimelineEntryPayload> TimelineEntries { get; set; } = [];

        [JsonPropertyName("story_arcs")]
        public List<StoryArcPayload> StoryArcs { get; set; } = [];

        [JsonPropertyName("arc_nodes")]
        public List<ArcNodePayload> ArcNodes { get; set; } = [];

        [JsonPropertyName("reader_perspectives")]
        public List<ReaderPerspectivePayload> ReaderPerspectives { get; set; } = [];
    }
}
