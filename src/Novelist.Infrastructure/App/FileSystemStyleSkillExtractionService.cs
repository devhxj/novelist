using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemStyleSkillExtractionService : IStyleSkillExtractionService
{
    private const string ProgressEventName = "style_skill_extraction:progress";
    private const int MaxTaskIdLength = 160;
    private const int MaxProviderNameLength = 128;
    private const int MaxModelIdLength = 160;
    private const int MaxReasoningEffortLength = 64;
    private const int MaxSkillNameLength = 128;
    private const int MaxReasonLength = 1_000;
    private const int MaxSampleCount = 16;
    private const int MaxSampleExcerptChars = 8_000;
    private const int MaxPromptChars = 80_000;
    private const int MaxSkillPreviewLength = 200_000;
    private const int MaxDiagnosticDetailLength = 4_000;

    private const string StatusRunning = "running";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";
    private const string StatusCancelled = "cancelled";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IStyleSampleService _samples;
    private readonly IChatCompletionClient _chat;
    private readonly IBridgeEventSink _events;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveStyleSkillExtraction> _active = new(StringComparer.Ordinal);

    public FileSystemStyleSkillExtractionService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IStyleSampleService? samples = null,
        IChatCompletionClient? chat = null,
        IBridgeEventSink? events = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _samples = samples ?? new FileSystemStyleSampleService(_options, _novels);
        _chat = chat ?? new StandardChatCompletionClient(new FileSystemLlmConfigurationService(_options));
        _events = events ?? new NullBridgeEventSink();
    }

    public async ValueTask<StyleSkillExtractionRunPayload> StartExtractionAsync(
        StartStyleSkillExtractionPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var novelId = await NormalizeNovelIdAsync(input.NovelId, cancellationToken);
        var sampleIds = NormalizeSampleIds(input.SampleIds);
        var providerName = NormalizeRequiredText(input.ProviderName, nameof(input.ProviderName), MaxProviderNameLength, allowLineBreaks: false);
        var modelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), MaxModelIdLength, allowLineBreaks: false);
        var reasoningEffort = NormalizeOptionalText(input.ReasoningEffort, nameof(input.ReasoningEffort), MaxReasoningEffortLength, allowLineBreaks: false);
        var requestedSkillName = StyleSkillDocument.NormalizeSkillName(
            NormalizeRequiredText(input.SkillName, nameof(input.SkillName), MaxSkillNameLength, allowLineBreaks: false));
        var selectedSamples = await LoadAuthorizedSamplesAsync(novelId, sampleIds, cancellationToken);

        try
        {
            var run = await CreateRunAsync(
                taskId,
                novelId,
                sampleIds,
                providerName,
                modelId,
                reasoningEffort,
                requestedSkillName,
                cancellationToken);
            await EmitProgressAsync(run, "风格技能抽取已排队。");

            var activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var active = new ActiveStyleSkillExtraction(activeCancellation);
            if (!_active.TryAdd(taskId, active))
            {
                activeCancellation.Dispose();
                throw new ArgumentException($"Style skill extraction run '{taskId}' is already active.", nameof(input.TaskId));
            }

            run = await TryGetRunAsync(taskId, cancellationToken) ?? run;
            if (run.Status != StatusRunning)
            {
                return run;
            }

            run = await UpdateRunningRunAsync(taskId, "prompt_building", 0, selectedSamples.Count, cancellationToken);
            await EmitProgressAsync(run, "正在整理样本统计与受限文本。");

            var messages = BuildExtractionMessages(requestedSkillName, selectedSamples);
            run = await UpdateRunningRunAsync(taskId, "model_call", selectedSamples.Count, selectedSamples.Count, cancellationToken);
            await EmitProgressAsync(run, "正在调用模型生成技能草稿。");

            string rawOutput;
            try
            {
                rawOutput = await _chat.GenerateTextAsync(
                    new ChatCompletionRequest(
                        providerName,
                        modelId,
                        reasoningEffort,
                        messages),
                    activeCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                return await MarkCancelledAsync(taskId, "风格技能抽取已取消。", "ExtractStyleSkillFromSamples", CancellationToken.None);
            }

            try
            {
                var skill = StyleSkillDocument.ParseStrict(rawOutput);
                var destination = await ResolveDestinationAsync(novelId, skill.Name, cancellationToken);
                var preview = BuildSkillPreview(skill, destination.SkillName, selectedSamples);
                var diagnostics = new[]
                {
                    Diagnostic(
                        "style_skill.preview_ready",
                        "风格技能预览已生成。",
                        $"skill_file_path={destination.FilePath}",
                        "ExtractStyleSkillFromSamples",
                        taskId)
                };

                run = await CompleteRunAsync(taskId, destination.SkillName, destination.FilePath, preview, diagnostics, cancellationToken);
                await EmitProgressAsync(run, "风格技能预览已生成。");
                return run;
            }
            catch (StyleSkillValidationException ex)
            {
                var diagnostic = Diagnostic(
                    "style_skill.invalid_frontmatter",
                    "模型返回的技能 Markdown 未通过校验。",
                    ex.Message,
                    "ExtractStyleSkillFromSamples",
                    taskId);
                run = await FailRunAsync(taskId, "skill_validation", diagnostic, CancellationToken.None);
                await EmitProgressAsync(run, "模型返回的技能 Markdown 未通过校验。");
                return run;
            }
            catch (ArgumentException ex)
            {
                var diagnostic = Diagnostic(
                    "style_skill.invalid_filename",
                    "模型返回的技能名称无法作为安全文件名。",
                    ex.Message,
                    "ExtractStyleSkillFromSamples",
                    taskId);
                run = await FailRunAsync(taskId, "skill_validation", diagnostic, CancellationToken.None);
                await EmitProgressAsync(run, "模型返回的技能名称无法保存。");
                return run;
            }
        }
        catch (OperationCanceledException)
        {
            return await MarkCancelledAsync(taskId, "风格技能抽取已取消。", "ExtractStyleSkillFromSamples", CancellationToken.None);
        }
        catch
        {
            if (await TryGetRunAsync(taskId, CancellationToken.None) is { Status: StatusRunning } running)
            {
                var diagnostic = Diagnostic(
                    "style_skill.extraction_failed",
                    "风格技能抽取失败。",
                    "抽取流程在模型调用或持久化阶段失败。",
                    "ExtractStyleSkillFromSamples",
                    taskId);
                var failed = await FailRunAsync(running.TaskId, "failed", diagnostic, CancellationToken.None);
                await EmitProgressAsync(failed, "风格技能抽取失败。");
                return failed;
            }

            throw;
        }
        finally
        {
            if (_active.TryRemove(taskId, out var removed))
            {
                if (!ReferenceEquals(removed.Cancellation, null))
                {
                    removed.Cancellation.Dispose();
                }
            }
        }
    }

    public async ValueTask<StyleSkillExtractionRunPayload> CancelExtractionAsync(
        CancelStyleSkillExtractionPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var reason = NormalizeRequiredText(input.Reason, nameof(input.Reason), MaxReasonLength, allowLineBreaks: true);

        if (_active.TryGetValue(taskId, out var active))
        {
            active.Cancellation.Cancel();
        }

        var run = await MarkCancelledAsync(taskId, reason, "CancelStyleSkillExtraction", cancellationToken);
        await EmitProgressAsync(run, "风格技能抽取已取消。");
        return run;
    }

    public async ValueTask<StyleSkillExtractionRunPayload?> GetRunAsync(
        GetNovelImportRunPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        return await TryGetRunAsync(taskId, cancellationToken);
    }

    private async ValueTask<long?> NormalizeNovelIdAsync(long? novelId, CancellationToken cancellationToken)
    {
        if (novelId is null)
        {
            return null;
        }

        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }

        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId.Value))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }

        return novelId.Value;
    }

    private static IReadOnlyList<long> NormalizeSampleIds(IReadOnlyList<long>? sampleIds)
    {
        if (sampleIds is null || sampleIds.Count == 0)
        {
            throw new ArgumentException("At least one style sample is required.", nameof(sampleIds));
        }

        if (sampleIds.Count > MaxSampleCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleIds), sampleIds.Count, $"At most {MaxSampleCount} style samples can be extracted at once.");
        }

        if (sampleIds.Any(id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleIds), "Style sample ids must be positive.");
        }

        if (sampleIds.Distinct().Count() != sampleIds.Count)
        {
            throw new ArgumentException("Style sample ids must be unique.", nameof(sampleIds));
        }

        return sampleIds.ToArray();
    }

    private async ValueTask<IReadOnlyList<StyleSampleDetailPayload>> LoadAuthorizedSamplesAsync(
        long? novelId,
        IReadOnlyList<long> sampleIds,
        CancellationToken cancellationToken)
    {
        var result = new List<StyleSampleDetailPayload>(sampleIds.Count);
        foreach (var sampleId in sampleIds)
        {
            var sample = await _samples.GetSampleAsync(new GetStyleSamplePayload(sampleId), cancellationToken)
                ?? throw new ArgumentException($"Style sample '{sampleId}' does not exist.", nameof(sampleIds));
            if (!sample.IsGlobal && (novelId is null || sample.NovelId != novelId))
            {
                throw new ArgumentException($"Style sample '{sampleId}' is not authorized for this extraction scope.", nameof(sampleIds));
            }

            result.Add(sample);
        }

        return result;
    }

    private async ValueTask<StyleSkillExtractionRunPayload> CreateRunAsync(
        string taskId,
        long? novelId,
        IReadOnlyList<long> sampleIds,
        string providerName,
        string modelId,
        string reasoningEffort,
        string skillName,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            if (store.Runs.Any(run => string.Equals(run.TaskId, taskId, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Style skill extraction run '{taskId}' already exists.", nameof(taskId));
            }

            var now = DateTimeOffset.UtcNow;
            var run = new StyleSkillExtractionRunStoreItem
            {
                TaskId = taskId,
                NovelId = novelId,
                Status = StatusRunning,
                Stage = "queued",
                ProgressCompleted = 0,
                ProgressTotal = sampleIds.Count,
                SampleIds = sampleIds.ToList(),
                ProviderName = providerName,
                ModelId = modelId,
                ReasoningEffort = reasoningEffort,
                SkillName = skillName,
                SkillPreview = string.Empty,
                SkillFilePath = string.Empty,
                Diagnostics = [],
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = null,
                CancelledAt = null,
                FailedAt = null
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

    private async ValueTask<StyleSkillExtractionRunPayload> UpdateRunningRunAsync(
        string taskId,
        string stage,
        int progressCompleted,
        int progressTotal,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            if (run.Status != StatusRunning)
            {
                return ToPayload(run);
            }

            run.Stage = stage;
            run.ProgressCompleted = progressCompleted;
            run.ProgressTotal = progressTotal;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<StyleSkillExtractionRunPayload> CompleteRunAsync(
        string taskId,
        string skillName,
        string skillFilePath,
        string skillPreview,
        IReadOnlyList<CopyableDiagnosticPayload> diagnostics,
        CancellationToken cancellationToken)
    {
        var preview = NormalizeRequiredText(skillPreview, nameof(skillPreview), MaxSkillPreviewLength, allowLineBreaks: true);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            if (run.Status != StatusRunning)
            {
                return ToPayload(run);
            }

            var now = DateTimeOffset.UtcNow;
            run.Status = StatusCompleted;
            run.Stage = "skill_preview";
            run.ProgressCompleted = run.ProgressTotal;
            run.SkillName = skillName;
            run.SkillFilePath = skillFilePath;
            run.SkillPreview = preview;
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

    private async ValueTask<StyleSkillExtractionRunPayload> FailRunAsync(
        string taskId,
        string stage,
        CopyableDiagnosticPayload diagnostic,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            if (run.Status != StatusRunning)
            {
                return ToPayload(run);
            }

            var now = DateTimeOffset.UtcNow;
            run.Status = StatusFailed;
            run.Stage = stage;
            run.Diagnostics.Add(TruncateDiagnostic(diagnostic));
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

    private async ValueTask<StyleSkillExtractionRunPayload> MarkCancelledAsync(
        string taskId,
        string reason,
        string bridgeMethod,
        CancellationToken cancellationToken)
    {
        var normalizedReason = NormalizeOptionalText(reason, nameof(reason), MaxReasonLength, allowLineBreaks: true);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            if (run.Status == StatusCancelled)
            {
                return ToPayload(run);
            }

            if (run.Status != StatusRunning)
            {
                return ToPayload(run);
            }

            var now = DateTimeOffset.UtcNow;
            run.Status = StatusCancelled;
            run.Stage = "cancelled";
            run.Diagnostics.Add(Diagnostic(
                "style_skill.cancelled",
                "风格技能抽取已取消。",
                normalizedReason,
                bridgeMethod,
                taskId));
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

    private async ValueTask<StyleSkillExtractionRunPayload?> TryGetRunAsync(
        string taskId,
        CancellationToken cancellationToken)
    {
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

    private async ValueTask<StyleSkillExtractionStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new StyleSkillExtractionStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<StyleSkillExtractionStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Style skill extraction store is empty or malformed.");
        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(
        StyleSkillExtractionStoreDocument store,
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

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "style_skill_extractions",
            "runs.json");
    }

    private async ValueTask<ResolvedSkillDestination> ResolveDestinationAsync(
        long? novelId,
        string skillName,
        CancellationToken cancellationToken)
    {
        var dataDirectory = await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken);
        var directory = novelId is null
            ? Path.Combine(dataDirectory, "skills")
            : Path.Combine(
                dataDirectory,
                "novels",
                novelId.Value.ToString(CultureInfo.InvariantCulture),
                "skills");
        var pathPrefix = novelId is null ? "~/.novelist/skills" : "skills";
        var uniqueName = UniqueSkillName(directory, skillName);
        return new ResolvedSkillDestination(uniqueName, $"{pathPrefix}/{uniqueName}.md");
    }

    private static string UniqueSkillName(string directory, string skillName)
    {
        var baseName = StyleSkillDocument.NormalizeSkillName(skillName);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var existing = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(comparison)
            : new HashSet<string>(comparison);

        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var index = 2; index < 10_000; index++)
        {
            var candidate = StyleSkillDocument.NormalizeSkillName($"{baseName}-{index.ToString(CultureInfo.InvariantCulture)}");
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to allocate a collision-free skill filename.");
    }

    private static IReadOnlyList<ChatCompletionMessage> BuildExtractionMessages(
        string requestedSkillName,
        IReadOnlyList<StyleSampleDetailPayload> samples)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("请根据用户选择的风格样本生成一个可复用的 Novelist 技能 Markdown。");
        prompt.AppendLine("必须只归纳抽象写作模式，不得复制样本文本中的具体句子作为正文模板。");
        prompt.AppendLine("输出必须是完整 Markdown，开头必须包含 YAML frontmatter。");
        prompt.AppendLine("frontmatter 必须包含且只能使用单行值：name, description, category, mode, author, version。");
        prompt.AppendLine("mode 只能是 auto、manual 或 always；version 必须是正整数。");
        prompt.AppendLine(CultureInfo.InvariantCulture, $"建议技能名称：{requestedSkillName}");
        prompt.AppendLine();
        prompt.AppendLine("样本清单如下，包含确定性统计和经过长度限制的样本文本：");

        foreach (var sample in samples)
        {
            var stats = JsonSerializer.Serialize(sample.Stats, JsonOptions);
            prompt.AppendLine();
            prompt.AppendLine(CultureInfo.InvariantCulture, $"## sample_id={sample.SampleId}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"name: {sample.Name}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"scope: {(sample.IsGlobal ? "global" : $"novel:{sample.NovelId}")}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"tags: {string.Join(", ", sample.Tags)}");
            prompt.AppendLine("stats:");
            prompt.AppendLine(stats);
            prompt.AppendLine("bounded_text:");
            prompt.AppendLine("```text");
            prompt.AppendLine(LimitText(sample.Content, MaxSampleExcerptChars));
            prompt.AppendLine("```");

            if (prompt.Length > MaxPromptChars)
            {
                prompt.AppendLine();
                prompt.AppendLine("[其余样本因 prompt 长度上限被省略。]");
                break;
            }
        }

        return
        [
            new ChatCompletionMessage("system", StyleSkillSystemPrompt),
            new ChatCompletionMessage("user", prompt.ToString())
        ];
    }

    private static string LimitText(string content, int maxChars)
    {
        var normalized = (content ?? string.Empty).Trim();
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return normalized[..maxChars] + "\n[样本文本已截断]";
    }

    private static string BuildSkillPreview(
        StyleSkillDocument skill,
        string finalSkillName,
        IReadOnlyList<StyleSampleDetailPayload> samples)
    {
        var ids = string.Join(",", samples.Select(sample => sample.SampleId.ToString(CultureInfo.InvariantCulture)));
        var hashes = string.Join(",", samples.Select(sample =>
            string.IsNullOrWhiteSpace(sample.SourceMetadata?.SourceHash)
                ? $"style-sample:{sample.SampleId.ToString(CultureInfo.InvariantCulture)}"
                : sample.SourceMetadata.SourceHash));
        var lines = new List<string>
        {
            "---",
            $"name: {finalSkillName}",
            $"description: {skill.Description}",
            $"category: {skill.Category}",
            $"mode: {skill.Mode}",
            $"author: {skill.Author}",
            $"version: {skill.Version.ToString(CultureInfo.InvariantCulture)}",
            $"source_sample_ids: {ids}",
            $"source_sample_hashes: {hashes}",
            "generated_by: style_sample_extraction",
            "---",
            string.Empty,
            skill.Body.Trim()
        };

        return string.Join('\n', lines).TrimEnd() + "\n";
    }

    private async ValueTask EmitProgressAsync(
        StyleSkillExtractionRunPayload run,
        string message)
    {
        var payload = new StyleSkillExtractionProgressPayload(
            run.TaskId,
            run.Status,
            run.Stage,
            run.ProgressCompleted,
            run.ProgressTotal,
            message,
            DateTimeOffset.UtcNow);

        try
        {
            await _events.EmitAsync(ProgressEventName, payload, CancellationToken.None);
        }
        catch
        {
            // Progress events must not make a completed extraction fail.
        }
    }

    private static StyleSkillExtractionRunStoreItem FindRun(
        StyleSkillExtractionStoreDocument store,
        string taskId)
    {
        return store.Runs.FirstOrDefault(run => string.Equals(run.TaskId, taskId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Style skill extraction run '{taskId}' does not exist.", nameof(taskId));
    }

    private static void ValidateStore(StyleSkillExtractionStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported style skill extraction store version '{store.Version}'.");
        }

        if (store.Runs is null ||
            store.Runs.Any(run =>
                run is null ||
                string.IsNullOrWhiteSpace(run.TaskId) ||
                run.SampleIds is null ||
                run.SampleIds.Count == 0 ||
                run.SampleIds.Any(id => id <= 0) ||
                run.SampleIds.Distinct().Count() != run.SampleIds.Count ||
                !IsSupportedStatus(run.Status) ||
                string.IsNullOrWhiteSpace(run.Stage) ||
                run.ProgressCompleted < 0 ||
                run.ProgressTotal <= 0 ||
                run.ProgressCompleted > run.ProgressTotal ||
                string.IsNullOrWhiteSpace(run.ProviderName) ||
                string.IsNullOrWhiteSpace(run.ModelId) ||
                string.IsNullOrWhiteSpace(run.SkillName) ||
                run.Diagnostics is null ||
                (run.Status == StatusRunning && run.CompletedAt is not null) ||
                (run.Status != StatusRunning && run.CompletedAt is null) ||
                (run.Status == StatusCompleted && string.IsNullOrWhiteSpace(run.SkillPreview)) ||
                (run.Status == StatusCompleted && string.IsNullOrWhiteSpace(run.SkillFilePath)) ||
                (run.Status == StatusCancelled && run.CancelledAt is null) ||
                (run.CancelledAt is not null && run.Status != StatusCancelled) ||
                (run.Status == StatusFailed && run.FailedAt is null) ||
                (run.FailedAt is not null && run.Status != StatusFailed)))
        {
            throw new InvalidOperationException("Style skill extraction store contains invalid run state.");
        }

        if (store.Runs.Select(run => run.TaskId).Distinct(StringComparer.Ordinal).Count() != store.Runs.Count)
        {
            throw new InvalidOperationException("Style skill extraction store contains duplicate task ids.");
        }
    }

    private static bool IsSupportedStatus(string status)
    {
        return status is StatusRunning or StatusCompleted or StatusFailed or StatusCancelled;
    }

    private static StyleSkillExtractionRunPayload ToPayload(StyleSkillExtractionRunStoreItem run)
    {
        return new StyleSkillExtractionRunPayload(
            run.TaskId,
            run.Status,
            run.Stage,
            run.ProgressCompleted,
            run.ProgressTotal,
            run.SampleIds.ToArray(),
            run.SkillName,
            run.SkillPreview,
            run.SkillFilePath,
            run.Diagnostics.ToArray(),
            run.CreatedAt,
            run.UpdatedAt,
            run.CompletedAt);
    }

    private static CopyableDiagnosticPayload Diagnostic(
        string code,
        string message,
        string detail,
        string operation,
        string taskId)
    {
        return TruncateDiagnostic(new CopyableDiagnosticPayload(
            code,
            message,
            detail,
            operation,
            taskId,
            RunId: null,
            BridgeMethod: operation,
            Timestamp: DateTimeOffset.UtcNow));
    }

    private static CopyableDiagnosticPayload TruncateDiagnostic(CopyableDiagnosticPayload diagnostic)
    {
        var detail = diagnostic.Detail ?? string.Empty;
        if (detail.Length > MaxDiagnosticDetailLength)
        {
            detail = detail[..MaxDiagnosticDetailLength] + "\n[diagnostic truncated]";
        }

        return diagnostic with { Detail = detail };
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

    private sealed record ActiveStyleSkillExtraction(CancellationTokenSource Cancellation);

    private sealed record ResolvedSkillDestination(string SkillName, string FilePath);

    private sealed class StyleSkillExtractionStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("runs")]
        public List<StyleSkillExtractionRunStoreItem> Runs { get; set; } = [];
    }

    private sealed class StyleSkillExtractionRunStoreItem
    {
        [JsonPropertyName("task_id")]
        public string TaskId { get; set; } = string.Empty;

        [JsonPropertyName("novel_id")]
        public long? NovelId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = StatusRunning;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = "queued";

        [JsonPropertyName("progress_completed")]
        public int ProgressCompleted { get; set; }

        [JsonPropertyName("progress_total")]
        public int ProgressTotal { get; set; }

        [JsonPropertyName("sample_ids")]
        public List<long> SampleIds { get; set; } = [];

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

        [JsonPropertyName("skill_file_path")]
        public string SkillFilePath { get; set; } = string.Empty;

        [JsonPropertyName("diagnostics")]
        public List<CopyableDiagnosticPayload> Diagnostics { get; set; } = [];

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

    private const string StyleSkillSystemPrompt = """
        你是一位负责创建 Novelist 技能文档的写作风格分析师。
        你只能基于用户提供的 style sample 摘要、确定性统计和受限样本文本归纳抽象写作规律。
        不要引入未提供的小说内容、章节事实、角色设定或外部资料。
        不要输出解释性前言；只输出一个可保存的 Markdown 技能文档。
        技能正文应该给出可操作的仿写指导，但不得复制样本文本中的原句。
        """;
}
