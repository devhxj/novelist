using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemNarrativePatternExtractionService : INarrativePatternExtractionService
{
    private const int MaxTaskIdLength = 160;
    private const int MaxProviderNameLength = 128;
    private const int MaxModelIdLength = 160;
    private const int MaxReasoningEffortLength = 64;
    private const int MaxSkillNameLength = 160;
    private const int MaxSkillPreviewLength = 200_000;
    private const int MaxStageLength = 128;
    private const int MaxReasonLength = 1_000;
    private const int MaxTraceIdLength = 160;
    private const int MaxHashLength = 160;
    private const int MaxRangeCount = 256;
    private const int MaxDiagnostics = 128;
    private const int MaxDiagnosticCodeLength = 128;
    private const int MaxDiagnosticMessageLength = 500;
    private const int MaxDiagnosticDetailLength = 4_000;
    private const int MaxDiagnosticOperationLength = 160;

    private const string StatusRunning = "running";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";
    private const string StatusCancelled = "cancelled";
    private const string InitialStage = "queued";
    private const string CancelledStage = "cancelled";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemNarrativePatternExtractionService(
        AppInitializationOptions? options = null,
        INovelService? novels = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<NarrativePatternRunPayload> StartExtractionAsync(
        StartNarrativePatternExtractionPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var novelId = await NormalizeNovelIdAsync(input.NovelId, cancellationToken);
        var ranges = NormalizeChapterRanges(input.ChapterRanges);
        var providerName = NormalizeRequiredText(input.ProviderName, nameof(input.ProviderName), MaxProviderNameLength, allowLineBreaks: false);
        var modelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), MaxModelIdLength, allowLineBreaks: false);
        var reasoningEffort = NormalizeOptionalText(input.ReasoningEffort, nameof(input.ReasoningEffort), MaxReasoningEffortLength, allowLineBreaks: false);
        var skillName = NormalizeRequiredText(input.SkillName, nameof(input.SkillName), MaxSkillNameLength, allowLineBreaks: false);
        var progressTotal = CountSelectedChapters(ranges);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            if (store.Runs.Any(run => string.Equals(run.TaskId, taskId, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Narrative pattern run '{taskId}' already exists.", nameof(input.TaskId));
            }

            var now = DateTimeOffset.UtcNow;
            var run = new NarrativePatternRunStoreItem
            {
                TaskId = taskId,
                NovelId = novelId,
                Status = StatusRunning,
                Stage = InitialStage,
                ProgressCompleted = 0,
                ProgressTotal = progressTotal,
                ChapterRanges = ranges.Select(ToStoreRange).ToList(),
                SelectedChapterIds = [],
                ProviderName = providerName,
                ModelId = modelId,
                ReasoningEffort = reasoningEffort,
                SkillName = skillName,
                SkillPreview = string.Empty,
                GeneratedSkill = new NarrativePatternGeneratedSkillStoreItem
                {
                    Name = skillName,
                    Preview = string.Empty,
                    Status = "pending",
                    UpdatedAt = now
                },
                Diagnostics = [],
                Trace = [],
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = null,
                CancelledAt = null,
                FailedAt = null,
                Error = null
            };

            store.Runs.Add(run);
            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NarrativePatternRunPayload> CancelExtractionAsync(
        CancelNarrativePatternExtractionPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var reason = NormalizeRequiredText(input.Reason, nameof(input.Reason), MaxReasonLength, allowLineBreaks: true);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            if (run.Status == StatusCancelled)
            {
                return ToPayload(run);
            }

            EnsureNotTerminal(run);
            var now = DateTimeOffset.UtcNow;
            var diagnostic = new CopyableDiagnosticPayload(
                Code: "pattern.cancelled",
                Message: "叙事模式抽取已取消。",
                Detail: reason,
                Operation: "CancelNarrativePatternExtraction",
                TaskId: taskId,
                RunId: null,
                BridgeMethod: "CancelNarrativePatternExtraction",
                Timestamp: now);

            run.Status = StatusCancelled;
            run.Stage = CancelledStage;
            run.Diagnostics = AppendDiagnostic(run.Diagnostics, diagnostic, taskId);
            run.Error = diagnostic;
            run.CompletedAt = now;
            run.CancelledAt = now;
            run.UpdatedAt = now;
            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NarrativePatternRunPayload?> GetRunAsync(
        GetNarrativePatternRunPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = store.Runs.FirstOrDefault(item => string.Equals(item.TaskId, taskId, StringComparison.Ordinal));
            return run is null ? null : ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NarrativePatternTracePayload?> GetTraceAsync(
        GetNarrativePatternRunPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = store.Runs.FirstOrDefault(item => string.Equals(item.TaskId, taskId, StringComparison.Ordinal));
            return run is null ? null : ToTracePayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NarrativePatternRunPayload> UpdateRunAsync(
        NarrativePatternRunUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var taskId = NormalizeRequiredText(update.TaskId, nameof(update.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var status = NormalizeRequiredText(update.Status, nameof(update.Status), MaxStageLength, allowLineBreaks: false);
        if (status != StatusRunning)
        {
            throw new ArgumentException("Narrative pattern run updates must keep status running.", nameof(update.Status));
        }

        var stage = NormalizeRequiredText(update.Stage, nameof(update.Stage), MaxStageLength, allowLineBreaks: false);
        var skillPreview = NormalizeOptionalText(update.SkillPreview, nameof(update.SkillPreview), MaxSkillPreviewLength, allowLineBreaks: true);
        var diagnostics = NormalizeDiagnostics(update.Diagnostics, taskId);
        ValidateProgress(update.ProgressCompleted, update.ProgressTotal);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            EnsureNotTerminal(run);

            run.Status = status;
            run.Stage = stage;
            run.ProgressCompleted = update.ProgressCompleted;
            run.ProgressTotal = update.ProgressTotal;
            if (update.SkillPreview is not null)
            {
                run.SkillPreview = skillPreview;
                run.GeneratedSkill.Preview = skillPreview;
                run.GeneratedSkill.Status = skillPreview.Length == 0 ? "pending" : "drafting";
                run.GeneratedSkill.UpdatedAt = DateTimeOffset.UtcNow;
            }

            run.Diagnostics = diagnostics.ToList();
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NarrativePatternRunPayload> CompleteRunAsync(
        NarrativePatternRunCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var taskId = NormalizeRequiredText(completion.TaskId, nameof(completion.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var stage = NormalizeRequiredText(completion.Stage, nameof(completion.Stage), MaxStageLength, allowLineBreaks: false);
        var skillPreview = NormalizeRequiredText(completion.SkillPreview, nameof(completion.SkillPreview), MaxSkillPreviewLength, allowLineBreaks: true);
        var diagnostics = NormalizeDiagnostics(completion.Diagnostics, taskId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            EnsureNotTerminal(run);
            var now = DateTimeOffset.UtcNow;

            run.Status = StatusCompleted;
            run.Stage = stage;
            run.ProgressCompleted = Math.Max(run.ProgressCompleted, run.ProgressTotal);
            run.SkillPreview = skillPreview;
            run.GeneratedSkill.Name = run.SkillName;
            run.GeneratedSkill.Preview = skillPreview;
            run.GeneratedSkill.Status = "preview_ready";
            run.GeneratedSkill.UpdatedAt = now;
            run.Diagnostics = diagnostics.ToList();
            run.CompletedAt = now;
            run.UpdatedAt = now;
            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NarrativePatternRunPayload> FailRunAsync(
        NarrativePatternRunFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var taskId = NormalizeRequiredText(failure.TaskId, nameof(failure.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var stage = NormalizeRequiredText(failure.Stage, nameof(failure.Stage), MaxStageLength, allowLineBreaks: false);
        var error = NormalizeDiagnostic(failure.Error, taskId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            EnsureNotTerminal(run);
            var now = DateTimeOffset.UtcNow;

            run.Status = StatusFailed;
            run.Stage = stage;
            run.Diagnostics = AppendDiagnostic(run.Diagnostics, error, taskId);
            run.Error = error;
            run.CompletedAt = now;
            run.FailedAt = now;
            run.UpdatedAt = now;
            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NarrativePatternTracePayload> AppendTraceAsync(
        NarrativePatternTraceAppend append,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(append);
        var taskId = NormalizeRequiredText(append.TaskId, nameof(append.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var entry = NormalizeTraceEntry(append.Entry, taskId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            EnsureNotTerminal(run);
            if (run.Trace.Any(existing => string.Equals(existing.TraceId, entry.TraceId, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Narrative pattern trace '{entry.TraceId}' already exists.", nameof(append.Entry));
            }

            run.Trace.Add(ToStoreTrace(entry));
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(store, cancellationToken);
            return ToTracePayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<long> NormalizeNovelIdAsync(long novelId, CancellationToken cancellationToken)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }

        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }

        return novelId;
    }

    private async ValueTask<NarrativePatternStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new NarrativePatternStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<NarrativePatternStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Narrative pattern store is empty or malformed.");

        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(NarrativePatternStoreDocument store, CancellationToken cancellationToken)
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
            "narrative_patterns",
            "runs.json");
    }

    private static NarrativePatternRunStoreItem FindRun(NarrativePatternStoreDocument store, string taskId)
    {
        return store.Runs.FirstOrDefault(run => string.Equals(run.TaskId, taskId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Narrative pattern run '{taskId}' does not exist.", nameof(taskId));
    }

    private static IReadOnlyList<ChapterRangePayload> NormalizeChapterRanges(IReadOnlyList<ChapterRangePayload>? ranges)
    {
        if (ranges is null || ranges.Count == 0)
        {
            throw new ArgumentException("At least one chapter range is required.", nameof(ranges));
        }

        if (ranges.Count > MaxRangeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ranges), ranges.Count, $"At most {MaxRangeCount} ranges are allowed.");
        }

        var normalized = new List<ChapterRangePayload>(ranges.Count);
        var previousEnd = 0;
        foreach (var range in ranges)
        {
            if (range.StartChapter <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ranges), range.StartChapter, "Range start chapter must be positive.");
            }

            if (range.EndChapter < range.StartChapter)
            {
                throw new ArgumentException("Range end chapter must be greater than or equal to the start chapter.", nameof(ranges));
            }

            if (range.StartChapter <= previousEnd)
            {
                throw new ArgumentException("Chapter ranges must be ascending and non-overlapping.", nameof(ranges));
            }

            normalized.Add(range);
            previousEnd = range.EndChapter;
        }

        return normalized;
    }

    private static int CountSelectedChapters(IReadOnlyList<ChapterRangePayload> ranges)
    {
        var count = 0;
        foreach (var range in ranges)
        {
            checked
            {
                count += range.EndChapter - range.StartChapter + 1;
            }
        }

        return count;
    }

    private static void ValidateProgress(int completed, int total)
    {
        if (completed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), completed, "Completed progress must be non-negative.");
        }

        if (total <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total), total, "Total progress must be positive.");
        }

        if (completed > total)
        {
            throw new ArgumentOutOfRangeException(nameof(completed), completed, "Completed progress must not exceed total progress.");
        }
    }

    private static void EnsureNotTerminal(NarrativePatternRunStoreItem run)
    {
        if (run.Status is StatusCompleted or StatusFailed or StatusCancelled)
        {
            throw new InvalidOperationException($"Narrative pattern run '{run.TaskId}' is already {run.Status}.");
        }
    }

    private static bool IsSupportedStatus(string status)
    {
        return status is StatusRunning or StatusCompleted or StatusFailed or StatusCancelled;
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

    private static IReadOnlyList<CopyableDiagnosticPayload> NormalizeDiagnostics(
        IReadOnlyList<CopyableDiagnosticPayload>? diagnostics,
        string taskId)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return [];
        }

        if (diagnostics.Count > MaxDiagnostics)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnostics), diagnostics.Count, $"At most {MaxDiagnostics} diagnostics are allowed.");
        }

        return diagnostics.Select(diagnostic => NormalizeDiagnostic(diagnostic, taskId)).ToArray();
    }

    private static CopyableDiagnosticPayload NormalizeDiagnostic(CopyableDiagnosticPayload? diagnostic, string taskId)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var diagnosticTaskId = diagnostic.TaskId is null
            ? null
            : NormalizeRequiredText(diagnostic.TaskId, nameof(diagnostic.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        if (diagnosticTaskId is not null && !string.Equals(diagnosticTaskId, taskId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Diagnostic task id must match the narrative pattern run.", nameof(diagnostic.TaskId));
        }

        var runId = diagnostic.RunId is null
            ? null
            : NormalizeRequiredText(diagnostic.RunId, nameof(diagnostic.RunId), MaxTaskIdLength, allowLineBreaks: false);
        var bridgeMethod = diagnostic.BridgeMethod is null
            ? null
            : NormalizeRequiredText(diagnostic.BridgeMethod, nameof(diagnostic.BridgeMethod), MaxDiagnosticOperationLength, allowLineBreaks: false);

        return new CopyableDiagnosticPayload(
            Code: NormalizeRequiredText(diagnostic.Code, nameof(diagnostic.Code), MaxDiagnosticCodeLength, allowLineBreaks: false),
            Message: NormalizeRequiredText(diagnostic.Message, nameof(diagnostic.Message), MaxDiagnosticMessageLength, allowLineBreaks: false),
            Detail: NormalizeOptionalText(diagnostic.Detail, nameof(diagnostic.Detail), MaxDiagnosticDetailLength, allowLineBreaks: true),
            Operation: NormalizeRequiredText(diagnostic.Operation, nameof(diagnostic.Operation), MaxDiagnosticOperationLength, allowLineBreaks: false),
            TaskId: diagnosticTaskId ?? taskId,
            RunId: runId,
            BridgeMethod: bridgeMethod,
            Timestamp: diagnostic.Timestamp);
    }

    private static List<CopyableDiagnosticPayload> AppendDiagnostic(
        IReadOnlyList<CopyableDiagnosticPayload> existing,
        CopyableDiagnosticPayload diagnostic,
        string taskId)
    {
        var normalized = NormalizeDiagnostics(existing, taskId).ToList();
        if (normalized.Count >= MaxDiagnostics)
        {
            throw new ArgumentOutOfRangeException(nameof(existing), existing.Count, $"At most {MaxDiagnostics} diagnostics are allowed.");
        }

        normalized.Add(NormalizeDiagnostic(diagnostic, taskId));
        return normalized;
    }

    private static NarrativePatternTraceEntryPayload NormalizeTraceEntry(
        NarrativePatternTraceEntryPayload? entry,
        string taskId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new NarrativePatternTraceEntryPayload(
            TraceId: NormalizeRequiredText(entry.TraceId, nameof(entry.TraceId), MaxTraceIdLength, allowLineBreaks: false),
            Stage: NormalizeRequiredText(entry.Stage, nameof(entry.Stage), MaxStageLength, allowLineBreaks: false),
            InputHash: NormalizeRequiredText(entry.InputHash, nameof(entry.InputHash), MaxHashLength, allowLineBreaks: false),
            OutputHash: NormalizeRequiredText(entry.OutputHash, nameof(entry.OutputHash), MaxHashLength, allowLineBreaks: false),
            Diagnostics: NormalizeDiagnostics(entry.Diagnostics, taskId),
            CreatedAt: entry.CreatedAt);
    }

    private static void ValidateStore(NarrativePatternStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported narrative pattern store version '{store.Version}'.");
        }

        if (store.Runs is null || store.Runs.Any(run =>
            run is null ||
            string.IsNullOrWhiteSpace(run.TaskId) ||
            run.NovelId <= 0 ||
            !IsSupportedStatus(run.Status) ||
            string.IsNullOrWhiteSpace(run.Stage) ||
            run.ProgressCompleted < 0 ||
            run.ProgressTotal <= 0 ||
            run.ProgressCompleted > run.ProgressTotal ||
            run.ChapterRanges is null ||
            run.ChapterRanges.Count == 0 ||
            run.SelectedChapterIds is null ||
            run.SelectedChapterIds.Any(id => id <= 0) ||
            run.SelectedChapterIds.Distinct().Count() != run.SelectedChapterIds.Count ||
            string.IsNullOrWhiteSpace(run.ProviderName) ||
            string.IsNullOrWhiteSpace(run.ModelId) ||
            string.IsNullOrWhiteSpace(run.SkillName) ||
            run.GeneratedSkill is null ||
            string.IsNullOrWhiteSpace(run.GeneratedSkill.Name) ||
            string.IsNullOrWhiteSpace(run.GeneratedSkill.Status) ||
            run.Diagnostics is null ||
            run.Trace is null ||
            (run.Status == StatusRunning && run.CompletedAt is not null) ||
            (run.Status != StatusRunning && run.CompletedAt is null) ||
            (run.CompletedAt is not null && run.CompletedAt < run.CreatedAt) ||
            (run.CancelledAt is not null && run.Status != StatusCancelled) ||
            (run.Status == StatusCancelled && run.CancelledAt is null) ||
            (run.FailedAt is not null && run.Status != StatusFailed) ||
            (run.Status == StatusFailed && run.FailedAt is null)))
        {
            throw new InvalidOperationException("Narrative pattern store contains invalid run state.");
        }

        if (store.Runs.Select(run => run.TaskId).Distinct(StringComparer.Ordinal).Count() != store.Runs.Count)
        {
            throw new InvalidOperationException("Narrative pattern store contains duplicate task ids.");
        }

        foreach (var run in store.Runs)
        {
            _ = NormalizeChapterRanges(run.ChapterRanges.Select(ToPayloadRange).ToArray());
            foreach (var trace in run.Trace)
            {
                if (string.IsNullOrWhiteSpace(trace.TraceId) ||
                    string.IsNullOrWhiteSpace(trace.Stage) ||
                    string.IsNullOrWhiteSpace(trace.InputHash) ||
                    string.IsNullOrWhiteSpace(trace.OutputHash))
                {
                    throw new InvalidOperationException("Narrative pattern store contains invalid trace state.");
                }
            }

            if (run.Trace.Select(trace => trace.TraceId).Distinct(StringComparer.Ordinal).Count() != run.Trace.Count)
            {
                throw new InvalidOperationException("Narrative pattern store contains duplicate trace ids.");
            }
        }
    }

    private static NarrativePatternRunPayload ToPayload(NarrativePatternRunStoreItem run)
    {
        return new NarrativePatternRunPayload(
            TaskId: run.TaskId,
            NovelId: run.NovelId,
            Status: run.Status,
            Stage: run.Stage,
            ProgressCompleted: run.ProgressCompleted,
            ProgressTotal: run.ProgressTotal,
            ChapterRanges: run.ChapterRanges.Select(ToPayloadRange).ToArray(),
            SkillName: run.SkillName,
            SkillPreview: run.SkillPreview,
            Diagnostics: run.Diagnostics.ToArray(),
            CreatedAt: run.CreatedAt,
            UpdatedAt: run.UpdatedAt,
            CompletedAt: run.CompletedAt);
    }

    private static NarrativePatternTracePayload ToTracePayload(NarrativePatternRunStoreItem run)
    {
        return new NarrativePatternTracePayload(
            TaskId: run.TaskId,
            Entries: run.Trace
                .OrderBy(entry => entry.CreatedAt)
                .ThenBy(entry => entry.TraceId, StringComparer.Ordinal)
                .Select(ToPayloadTrace)
                .ToArray());
    }

    private static NarrativePatternTraceEntryPayload ToPayloadTrace(NarrativePatternTraceStoreItem entry)
    {
        return new NarrativePatternTraceEntryPayload(
            TraceId: entry.TraceId,
            Stage: entry.Stage,
            InputHash: entry.InputHash,
            OutputHash: entry.OutputHash,
            Diagnostics: entry.Diagnostics.ToArray(),
            CreatedAt: entry.CreatedAt);
    }

    private static NarrativePatternChapterRangeStoreItem ToStoreRange(ChapterRangePayload range)
    {
        return new NarrativePatternChapterRangeStoreItem
        {
            StartChapter = range.StartChapter,
            EndChapter = range.EndChapter
        };
    }

    private static ChapterRangePayload ToPayloadRange(NarrativePatternChapterRangeStoreItem range)
    {
        return new ChapterRangePayload(range.StartChapter, range.EndChapter);
    }

    private static NarrativePatternTraceStoreItem ToStoreTrace(NarrativePatternTraceEntryPayload entry)
    {
        return new NarrativePatternTraceStoreItem
        {
            TraceId = entry.TraceId,
            Stage = entry.Stage,
            InputHash = entry.InputHash,
            OutputHash = entry.OutputHash,
            Diagnostics = entry.Diagnostics.ToList(),
            CreatedAt = entry.CreatedAt
        };
    }

    private sealed class NarrativePatternStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("runs")]
        public List<NarrativePatternRunStoreItem> Runs { get; set; } = [];
    }

    private sealed class NarrativePatternRunStoreItem
    {
        [JsonPropertyName("task_id")]
        public string TaskId { get; set; } = string.Empty;

        [JsonPropertyName("novel_id")]
        public long NovelId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = StatusRunning;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = InitialStage;

        [JsonPropertyName("progress_completed")]
        public int ProgressCompleted { get; set; }

        [JsonPropertyName("progress_total")]
        public int ProgressTotal { get; set; }

        [JsonPropertyName("chapter_ranges")]
        public List<NarrativePatternChapterRangeStoreItem> ChapterRanges { get; set; } = [];

        [JsonPropertyName("selected_chapter_ids")]
        public List<long> SelectedChapterIds { get; set; } = [];

        [JsonPropertyName("provider_name")]
        public string ProviderName { get; set; } = string.Empty;

        [JsonPropertyName("model_id")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("reasoning_effort")]
        public string ReasoningEffort { get; set; } = string.Empty;

        [JsonPropertyName("skill_name")]
        public string SkillName { get; set; } = string.Empty;

        [JsonPropertyName("skill_preview")]
        public string SkillPreview { get; set; } = string.Empty;

        [JsonPropertyName("generated_skill")]
        public NarrativePatternGeneratedSkillStoreItem GeneratedSkill { get; set; } = new();

        [JsonPropertyName("diagnostics")]
        public List<CopyableDiagnosticPayload> Diagnostics { get; set; } = [];

        [JsonPropertyName("error")]
        public CopyableDiagnosticPayload? Error { get; set; }

        [JsonPropertyName("trace")]
        public List<NarrativePatternTraceStoreItem> Trace { get; set; } = [];

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public DateTimeOffset? CompletedAt { get; set; }

        [JsonPropertyName("cancelled_at")]
        public DateTimeOffset? CancelledAt { get; set; }

        [JsonPropertyName("failed_at")]
        public DateTimeOffset? FailedAt { get; set; }
    }

    private sealed class NarrativePatternGeneratedSkillStoreItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("preview")]
        public string Preview { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "pending";

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class NarrativePatternChapterRangeStoreItem
    {
        [JsonPropertyName("start_chapter")]
        public int StartChapter { get; set; }

        [JsonPropertyName("end_chapter")]
        public int EndChapter { get; set; }
    }

    private sealed class NarrativePatternTraceStoreItem
    {
        [JsonPropertyName("trace_id")]
        public string TraceId { get; set; } = string.Empty;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = string.Empty;

        [JsonPropertyName("input_hash")]
        public string InputHash { get; set; } = string.Empty;

        [JsonPropertyName("output_hash")]
        public string OutputHash { get; set; } = string.Empty;

        [JsonPropertyName("diagnostics")]
        public List<CopyableDiagnosticPayload> Diagnostics { get; set; } = [];

        [JsonPropertyName("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
