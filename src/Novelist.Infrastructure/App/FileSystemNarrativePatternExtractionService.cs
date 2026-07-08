using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemNarrativePatternExtractionService : INarrativePatternExtractionService
{
    private const string ProgressEventName = "narrative_pattern_extraction:progress";
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
    private readonly IChapterContentService _chapters;
    private readonly IChatCompletionClient _chat;
    private readonly ILlmConfigurationService _llmConfiguration;
    private readonly IBridgeEventSink _events;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly ConcurrentDictionary<string, ActiveNarrativePatternExtraction> _active = new(StringComparer.Ordinal);

    public FileSystemNarrativePatternExtractionService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IChapterContentService? chapters = null,
        IChatCompletionClient? chat = null,
        ILlmConfigurationService? llmConfiguration = null,
        IBridgeEventSink? events = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _llmConfiguration = llmConfiguration ?? new FileSystemLlmConfigurationService(_options);
        _chapters = chapters ?? new FileSystemChapterContentService(_options, _novels);
        _chat = chat ?? new StandardChatCompletionClient(_llmConfiguration);
        _events = events ?? new NullBridgeEventSink();
    }

    public async ValueTask<NarrativePatternRunPayload> StartExtractionAsync(
        StartNarrativePatternExtractionPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var novelId = await NormalizeNovelIdAsync(input.NovelId, cancellationToken);
        var providerName = NormalizeRequiredText(input.ProviderName, nameof(input.ProviderName), MaxProviderNameLength, allowLineBreaks: false);
        var modelId = NormalizeRequiredText(input.ModelId, nameof(input.ModelId), MaxModelIdLength, allowLineBreaks: false);
        var reasoningEffort = NormalizeOptionalText(input.ReasoningEffort, nameof(input.ReasoningEffort), MaxReasoningEffortLength, allowLineBreaks: false);
        var skillName = StyleSkillDocument.NormalizeSkillName(
            NormalizeRequiredText(input.SkillName, nameof(input.SkillName), MaxSkillNameLength, allowLineBreaks: false));
        var selectedChapterIds = NormalizeSelectedChapterIds(input.SelectedChapterIds);
        var allChapters = await _chapters.GetChaptersAsync(novelId, cancellationToken);
        var selection = NarrativePatternPipeline.ResolveChapterSelection(
            allChapters,
            input.ChapterRanges,
            selectedChapterIds);

        var run = await CreateRunAsync(
            taskId,
            novelId,
            selection.ChapterRanges,
            selection.SelectedChapterIds,
            providerName,
            modelId,
            reasoningEffort,
            skillName,
            cancellationToken);
        await EmitProgressAsync(run, "叙事模式抽取已排队。");

        var activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var active = new ActiveNarrativePatternExtraction(activeCancellation);
        if (!_active.TryAdd(taskId, active))
        {
            activeCancellation.Dispose();
            throw new ArgumentException($"Narrative pattern run '{taskId}' is already active.", nameof(input.TaskId));
        }

        try
        {
            return await RunPipelineAsync(
                run,
                providerName,
                modelId,
                reasoningEffort,
                skillName,
                selection,
                activeCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            var cancelled = await MarkCancelledAsync(
                taskId,
                "叙事模式抽取已取消。",
                "StartNarrativePatternExtraction",
                CancellationToken.None);
            await EmitProgressAsync(cancelled, "叙事模式抽取已取消。");
            return cancelled;
        }
        catch (NarrativePatternValidationException ex)
        {
            var failed = await FailRunAsync(
                new NarrativePatternRunFailure(
                    taskId,
                    "model_json_validation",
                    Diagnostic(
                        ex.Code,
                        "叙事模式抽取输出未通过校验。",
                        ex.Message,
                        "StartNarrativePatternExtraction",
                        taskId)),
                CancellationToken.None);
            await EmitProgressAsync(failed, "叙事模式抽取输出未通过校验。");
            return failed;
        }
        catch (StyleSkillValidationException ex)
        {
            var failed = await FailRunAsync(
                new NarrativePatternRunFailure(
                    taskId,
                    "skill_validation",
                    Diagnostic(
                        "pattern.invalid_skill",
                        "模型返回的叙事模式技能 Markdown 未通过校验。",
                        ex.Message,
                        "StartNarrativePatternExtraction",
                        taskId)),
                CancellationToken.None);
            await EmitProgressAsync(failed, "模型返回的叙事模式技能 Markdown 未通过校验。");
            return failed;
        }
        catch
        {
            if (await TryGetRunAsync(taskId, CancellationToken.None) is { Status: StatusRunning } running)
            {
                var failed = await FailRunAsync(
                    new NarrativePatternRunFailure(
                        running.TaskId,
                        "failed",
                        Diagnostic(
                            "pattern.extraction_failed",
                            "叙事模式抽取失败。",
                            "抽取流程在章节读取、模型调用或持久化阶段失败。",
                            "StartNarrativePatternExtraction",
                            running.TaskId)),
                    CancellationToken.None);
                await EmitProgressAsync(failed, "叙事模式抽取失败。");
                return failed;
            }

            throw;
        }
        finally
        {
            if (_active.TryRemove(taskId, out var removed))
            {
                removed.Cancellation.Dispose();
            }
        }
    }

    public async ValueTask<NarrativePatternRunPayload> CancelExtractionAsync(
        CancelNarrativePatternExtractionPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var reason = NormalizeRequiredText(input.Reason, nameof(input.Reason), MaxReasonLength, allowLineBreaks: true);

        if (_active.TryGetValue(taskId, out var active))
        {
            active.Cancellation.Cancel();
        }

        var run = await MarkCancelledAsync(
            taskId,
            reason,
            "CancelNarrativePatternExtraction",
            cancellationToken);
        await EmitProgressAsync(run, "叙事模式抽取已取消。");
        return run;
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

    private async ValueTask<NarrativePatternRunPayload> CreateRunAsync(
        string taskId,
        long novelId,
        IReadOnlyList<ChapterRangePayload> ranges,
        IReadOnlyList<long> selectedChapterIds,
        string providerName,
        string modelId,
        string reasoningEffort,
        string skillName,
        CancellationToken cancellationToken)
    {
        var normalizedRanges = NormalizeChapterRanges(ranges);
        var normalizedChapterIds = NormalizeSelectedChapterIds(selectedChapterIds);
        var progressTotal = CountSelectedChapters(normalizedRanges) + 4;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            if (store.Runs.Any(run => string.Equals(run.TaskId, taskId, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Narrative pattern run '{taskId}' already exists.", nameof(taskId));
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
                ChapterRanges = normalizedRanges.Select(ToStoreRange).ToList(),
                SelectedChapterIds = normalizedChapterIds.ToList(),
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

    private async ValueTask<NarrativePatternRunPayload> RunPipelineAsync(
        NarrativePatternRunPayload run,
        string providerName,
        string modelId,
        string reasoningEffort,
        string requestedSkillName,
        NarrativePatternChapterSelection selection,
        CancellationToken cancellationToken)
    {
        var taskId = run.TaskId;
        var contextWindowTokens = await ResolveContextWindowTokensAsync(providerName, modelId, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        run = await UpdateRunningRunAsync(
            taskId,
            "chapter_loading",
            0,
            run.ProgressTotal,
            cancellationToken);
        await EmitProgressAsync(
            run,
            "正在读取并校验章节内容。",
            llmStatus: "idle",
            tokenEstimate: selection.Chapters.Sum(chapter => Math.Max(1, chapter.WordCount)));
        var documents = await LoadChapterDocumentsAsync(selection, cancellationToken);
        await AppendTraceForOutputAsync(
            taskId,
            "chapter_loading",
            string.Join('|', documents.Select(item => item.ContentHash)),
            JsonSerializer.Serialize(documents.Select(item => new
            {
                item.ChapterNumber,
                item.Title,
                item.ContentHash,
                item.EstimatedTokens
            }), JsonOptions),
            []);

        cancellationToken.ThrowIfCancellationRequested();
        run = await UpdateRunningRunAsync(taskId, "boundary_detection", 1, run.ProgressTotal, cancellationToken);
        await EmitProgressAsync(
            run,
            "正在识别叙事结构边界。",
            llmStatus: "calling",
            tokenEstimate: documents.Sum(item => item.EstimatedTokens));
        var boundaryPrompt = BuildBoundaryMessages(documents, contextWindowTokens);
        var boundaryJson = await InvokeStructuredJsonAsync(
            providerName,
            modelId,
            reasoningEffort,
            boundaryPrompt,
            BoundaryToolDefinition(),
            cancellationToken);
        var boundaries = NarrativePatternPipeline.ParseBoundaries(boundaryJson, documents);
        await AppendTraceForOutputAsync(taskId, "boundary_detection", PromptHash(boundaryPrompt), boundaryJson, []);
        await EmitProgressAsync(
            run,
            "叙事结构边界已生成。",
            llmStatus: "completed",
            tokenEstimate: NarrativePatternPipeline.EstimateTokens(boundaryJson),
            boundaryCount: boundaries.Count);

        cancellationToken.ThrowIfCancellationRequested();
        var summaryBatches = NarrativePatternPipeline.CreateTokenBatches(
            documents,
            item => item.EstimatedTokens,
            contextWindowTokens);
        var summaries = new List<NarrativePatternChapterSummary>();
        for (var batchIndex = 0; batchIndex < summaryBatches.Count; batchIndex++)
        {
            var batch = summaryBatches[batchIndex];
            run = await UpdateRunningRunAsync(
                taskId,
                "chapter_summary",
                Math.Min(run.ProgressTotal - 3, 2 + summaries.Count),
                run.ProgressTotal,
                cancellationToken);
            await EmitProgressAsync(
                run,
                $"正在提取章节摘要：批次 {(batchIndex + 1).ToString(CultureInfo.InvariantCulture)}/{summaryBatches.Count.ToString(CultureInfo.InvariantCulture)}。",
                llmStatus: "calling",
                batchIndex: batchIndex + 1,
                batchTotal: summaryBatches.Count,
                tokenEstimate: batch.EstimatedTokens,
                boundaryCount: boundaries.Count,
                summaryCount: summaries.Count);

            var summaryPrompt = BuildSummaryMessages(batch.Items);
            var summaryJson = await InvokeStructuredJsonAsync(
                providerName,
                modelId,
                reasoningEffort,
                summaryPrompt,
                SummaryToolDefinition(),
                cancellationToken);
            var parsed = NarrativePatternPipeline.ParseChapterSummaries(summaryJson, batch.Items);
            summaries.AddRange(parsed);
            await AppendTraceForOutputAsync(taskId, "chapter_summary", PromptHash(summaryPrompt), summaryJson, []);
        }

        summaries = summaries.OrderBy(item => item.ChapterNumber).ToList();
        run = await UpdateRunningRunAsync(
            taskId,
            "chapter_summary",
            Math.Min(run.ProgressTotal - 2, 2 + summaries.Count),
            run.ProgressTotal,
            cancellationToken);
        await EmitProgressAsync(
            run,
            "章节摘要已生成。",
            llmStatus: "completed",
            boundaryCount: boundaries.Count,
            summaryCount: summaries.Count);

        cancellationToken.ThrowIfCancellationRequested();
        var phases = await CompressPhasesAsync(
            run,
            providerName,
            modelId,
            reasoningEffort,
            boundaries,
            summaries,
            contextWindowTokens,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        run = await UpdateRunningRunAsync(
            taskId,
            "skill_generation",
            run.ProgressTotal - 1,
            run.ProgressTotal,
            cancellationToken);
        await EmitProgressAsync(
            run,
            "正在生成叙事模式技能预览。",
            llmStatus: "calling",
            boundaryCount: boundaries.Count,
            summaryCount: summaries.Count,
            phaseCount: phases.Count);
        var skillPrompt = BuildSkillMessages(requestedSkillName, documents, boundaries, summaries, phases);
        var rawSkill = await InvokeTextAsync(
            providerName,
            modelId,
            reasoningEffort,
            skillPrompt,
            cancellationToken);
        var skill = StyleSkillDocument.ParseStrict(rawSkill);
        var preview = BuildNarrativeSkillPreview(skill, requestedSkillName, selection, documents);
        await AppendTraceForOutputAsync(taskId, "skill_generation", PromptHash(skillPrompt), rawSkill, []);

        var completed = await CompleteRunAsync(
            new NarrativePatternRunCompletion(
                taskId,
                "skill_preview",
                preview,
                [
                    Diagnostic(
                        "pattern.skill.preview_ready",
                        "叙事模式技能预览已生成。",
                        $"chapters={documents.Count.ToString(CultureInfo.InvariantCulture)}; boundaries={boundaries.Count.ToString(CultureInfo.InvariantCulture)}; summaries={summaries.Count.ToString(CultureInfo.InvariantCulture)}; phases={phases.Count.ToString(CultureInfo.InvariantCulture)}",
                        "StartNarrativePatternExtraction",
                        taskId)
                ]),
            cancellationToken);
        await EmitProgressAsync(
            completed,
            "叙事模式技能预览已生成。",
            llmStatus: "completed",
            boundaryCount: boundaries.Count,
            summaryCount: summaries.Count,
            phaseCount: phases.Count);
        return completed;
    }

    private async ValueTask<IReadOnlyList<NarrativePatternChapterDocument>> LoadChapterDocumentsAsync(
        NarrativePatternChapterSelection selection,
        CancellationToken cancellationToken)
    {
        var contentByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chapter in selection.Chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            contentByPath[chapter.FilePath] = await _chapters.GetContentAsync(
                chapter.NovelId,
                chapter.FilePath,
                cancellationToken);
        }

        return NarrativePatternPipeline.BuildChapterDocuments(selection, contentByPath);
    }

    private async ValueTask<IReadOnlyList<NarrativePatternPhase>> CompressPhasesAsync(
        NarrativePatternRunPayload run,
        string providerName,
        string modelId,
        string reasoningEffort,
        IReadOnlyList<NarrativePatternBoundary> boundaries,
        IReadOnlyList<NarrativePatternChapterSummary> summaries,
        int contextWindowTokens,
        CancellationToken cancellationToken)
    {
        var taskId = run.TaskId;
        var targetPhaseCount = Math.Max(1, Math.Min(8, boundaries.Count));
        var previousPhaseCount = summaries.Count;
        IReadOnlyList<NarrativePatternPhase> current = [];

        for (var round = 1; round <= NarrativePatternPipeline.MaxCompressionRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            run = await UpdateRunningRunAsync(
                taskId,
                "phase_compression",
                Math.Max(0, run.ProgressTotal - 2),
                run.ProgressTotal,
                cancellationToken);
            var compressionInput = current.Count == 0
                ? summaries.Select(summary => new NarrativePatternCompressionInput(
                    summary.ChapterNumber,
                    summary.ChapterNumber,
                    $"第{summary.ChapterNumber.ToString(CultureInfo.InvariantCulture)}章",
                    summary.Summary,
                    summary.TurningPoints)).ToArray()
                : current.Select(phase => new NarrativePatternCompressionInput(
                    phase.StartChapter,
                    phase.EndChapter,
                    phase.PhaseName,
                    phase.NarrativeFunction,
                    [phase.Guidance])).ToArray();

            var batches = NarrativePatternPipeline.CreateTokenBatches(
                compressionInput,
                item => NarrativePatternPipeline.EstimateTokens(item.Summary) +
                    item.Points.Sum(NarrativePatternPipeline.EstimateTokens),
                contextWindowTokens);
            var next = new List<NarrativePatternPhase>();
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                var batch = batches[batchIndex];
                await EmitProgressAsync(
                    run,
                    $"正在递归压缩叙事阶段：第 {round.ToString(CultureInfo.InvariantCulture)} 轮，批次 {(batchIndex + 1).ToString(CultureInfo.InvariantCulture)}/{batches.Count.ToString(CultureInfo.InvariantCulture)}。",
                    llmStatus: "calling",
                    round: round,
                    batchIndex: batchIndex + 1,
                    batchTotal: batches.Count,
                    tokenEstimate: batch.EstimatedTokens,
                    boundaryCount: boundaries.Count,
                    summaryCount: summaries.Count,
                    phaseCount: current.Count);

                var prompt = BuildPhaseCompressionMessages(boundaries, summaries, batch.Items, targetPhaseCount);
                var phaseJson = await InvokePhaseJsonWithRetryAsync(
                    providerName,
                    modelId,
                    reasoningEffort,
                    prompt,
                    cancellationToken);
                var coveredSummaries = summaries
                    .Where(summary => batch.Items.Any(item =>
                        summary.ChapterNumber >= item.StartChapter &&
                        summary.ChapterNumber <= item.EndChapter))
                    .ToArray();
                next.AddRange(NarrativePatternPipeline.ParsePhases(phaseJson, coveredSummaries));
                await AppendTraceForOutputAsync(taskId, "phase_compression", PromptHash(prompt), phaseJson, []);
            }

            current = next.OrderBy(item => item.StartChapter).ThenBy(item => item.EndChapter).ToArray();
            var decision = NarrativePatternPipeline.EvaluateCompressionProgress(
                round,
                previousPhaseCount,
                current.Count,
                targetPhaseCount);
            await EmitProgressAsync(
                run,
                $"叙事阶段压缩第 {round.ToString(CultureInfo.InvariantCulture)} 轮完成。",
                llmStatus: "completed",
                round: round,
                boundaryCount: boundaries.Count,
                summaryCount: summaries.Count,
                phaseCount: current.Count);

            if (decision.Stalled)
            {
                throw new NarrativePatternValidationException(
                    "pattern.compression_stalled",
                    $"Narrative phase compression stopped without convergence: {decision.Reason}.");
            }

            if (decision.Stop)
            {
                return current;
            }

            previousPhaseCount = current.Count;
        }

        throw new NarrativePatternValidationException(
            "pattern.compression_stalled",
            "Narrative phase compression exceeded the maximum round limit.");
    }

    private async ValueTask<string> InvokePhaseJsonWithRetryAsync(
        string providerName,
        string modelId,
        string reasoningEffort,
        IReadOnlyList<ChatCompletionMessage> messages,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= NarrativePatternPipeline.MaxEmptyPhaseRetries; attempt++)
        {
            var output = await InvokeStructuredJsonAsync(
                providerName,
                modelId,
                reasoningEffort,
                messages,
                PhaseToolDefinition(),
                cancellationToken);
            try
            {
                _ = NarrativePatternPipeline.ParsePhases(
                    output,
                    [new NarrativePatternChapterSummary(1, 1, "sha256:placeholder", "placeholder", ["placeholder"])]);
            }
            catch (NarrativePatternValidationException ex) when (ex.Code == "pattern.empty_phase_output" &&
                attempt < NarrativePatternPipeline.MaxEmptyPhaseRetries)
            {
                continue;
            }
            catch (NarrativePatternValidationException ex) when (ex.Code != "pattern.empty_phase_output")
            {
                // The real caller validates against its covered chapter set; only empty output is retried here.
            }

            return output;
        }

        throw new NarrativePatternValidationException(
            "pattern.empty_phase_output",
            "Model returned no narrative phases after bounded retries.");
    }

    private async ValueTask<string> InvokeStructuredJsonAsync(
        string providerName,
        string modelId,
        string reasoningEffort,
        IReadOnlyList<ChatCompletionMessage> messages,
        ChatToolDefinition toolDefinition,
        CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest(
            providerName,
            modelId,
            reasoningEffort,
            messages,
            [toolDefinition]);
        var content = new StringBuilder();
        var toolArguments = new StringBuilder();

        try
        {
            await foreach (var item in _chat.StreamChatAsync(request, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Kind == ChatCompletionStreamEventKind.Content && !string.IsNullOrEmpty(item.Data))
                {
                    content.Append(item.Data);
                }

                if (item.Kind == ChatCompletionStreamEventKind.ToolCall &&
                    item.ToolCall is not null &&
                    string.Equals(item.ToolCall.Name, toolDefinition.Name, StringComparison.Ordinal))
                {
                    toolArguments.Append(item.ToolCall.ArgumentsJson);
                }
            }
        }
        catch (NotSupportedException)
        {
            return await _chat.GenerateTextAsync(request, cancellationToken);
        }

        return toolArguments.Length > 0 ? toolArguments.ToString() : content.ToString();
    }

    private async ValueTask<string> InvokeTextAsync(
        string providerName,
        string modelId,
        string reasoningEffort,
        IReadOnlyList<ChatCompletionMessage> messages,
        CancellationToken cancellationToken)
    {
        var request = new ChatCompletionRequest(providerName, modelId, reasoningEffort, messages);
        var content = new StringBuilder();
        try
        {
            await foreach (var item in _chat.StreamChatAsync(request, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Kind == ChatCompletionStreamEventKind.Content && !string.IsNullOrEmpty(item.Data))
                {
                    content.Append(item.Data);
                }
            }
        }
        catch (NotSupportedException)
        {
            return await _chat.GenerateTextAsync(request, cancellationToken);
        }

        return content.ToString();
    }

    private async ValueTask<NarrativePatternRunPayload> UpdateRunningRunAsync(
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

            run.Stage = NormalizeRequiredText(stage, nameof(stage), MaxStageLength, allowLineBreaks: false);
            run.ProgressCompleted = Math.Clamp(progressCompleted, 0, progressTotal);
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

    private async ValueTask<NarrativePatternRunPayload?> TryGetRunAsync(
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

    private async ValueTask<NarrativePatternRunPayload> MarkCancelledAsync(
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
            var diagnostic = Diagnostic(
                "pattern.cancelled",
                "叙事模式抽取已取消。",
                normalizedReason,
                bridgeMethod,
                taskId);

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

    private async ValueTask AppendTraceForOutputAsync(
        string taskId,
        string stage,
        string input,
        string output,
        IReadOnlyList<CopyableDiagnosticPayload> diagnostics)
    {
        await AppendTraceAsync(
            new NarrativePatternTraceAppend(
                taskId,
                new NarrativePatternTraceEntryPayload(
                    TraceId: $"{stage}-{Guid.NewGuid():N}",
                    Stage: stage,
                    InputHash: NarrativePatternPipeline.Sha256Hex(input),
                    OutputHash: NarrativePatternPipeline.Sha256Hex(output),
                    Diagnostics: diagnostics,
                    CreatedAt: DateTimeOffset.UtcNow)),
            CancellationToken.None);
    }

    private async ValueTask EmitProgressAsync(
        NarrativePatternRunPayload run,
        string message,
        string llmStatus = "",
        int? round = null,
        int? batchIndex = null,
        int? batchTotal = null,
        int? tokenEstimate = null,
        int? boundaryCount = null,
        int? summaryCount = null,
        int? phaseCount = null)
    {
        var payload = new NarrativePatternProgressPayload(
            run.TaskId,
            run.Status,
            run.Stage,
            run.ProgressCompleted,
            run.ProgressTotal,
            message,
            DateTimeOffset.UtcNow,
            llmStatus,
            round,
            batchIndex,
            batchTotal,
            tokenEstimate,
            boundaryCount,
            summaryCount,
            phaseCount);

        try
        {
            await _events.EmitAsync(ProgressEventName, payload, CancellationToken.None);
        }
        catch
        {
            // Progress delivery must not make a finished extraction fail.
        }
    }

    private async ValueTask<int> ResolveContextWindowTokensAsync(
        string providerName,
        string modelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var config = await _llmConfiguration.GetConfigAsync(cancellationToken);
            var provider = config.Providers.FirstOrDefault(item =>
                string.Equals(item.Key, providerName, StringComparison.OrdinalIgnoreCase));
            var model = provider?.BuiltinModels.Concat(provider.CustomModels).FirstOrDefault(item =>
                string.Equals(item.Id, modelId, StringComparison.Ordinal));
            return model is { ContextWindow: > 0 }
                ? model.ContextWindow
                : NarrativePatternPipeline.DefaultContextWindowTokens;
        }
        catch
        {
            return NarrativePatternPipeline.DefaultContextWindowTokens;
        }
    }

    private static IReadOnlyList<ChatCompletionMessage> BuildBoundaryMessages(
        IReadOnlyList<NarrativePatternChapterDocument> chapters,
        int contextWindowTokens)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("请识别所选章节的叙事结构边界。");
        prompt.AppendLine("必须调用工具或返回严格 JSON；不得输出解释性前言。");
        prompt.AppendLine("JSON schema_version 必须为 narrative-pattern-v1。");
        prompt.AppendLine("boundaries 必须覆盖全部所选章节，按章节升序且不得重叠。");
        prompt.AppendLine();
        AppendChapterCapsules(prompt, chapters, contextWindowTokens);

        return
        [
            new ChatCompletionMessage("system", NarrativePatternSystemPrompt),
            new ChatCompletionMessage("user", prompt.ToString())
        ];
    }

    private static IReadOnlyList<ChatCompletionMessage> BuildSummaryMessages(
        IReadOnlyList<NarrativePatternChapterDocument> chapters)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("请为下列章节分别提取叙事摘要。");
        prompt.AppendLine("必须调用工具或返回严格 JSON；summaries 必须逐章覆盖输入章节。");
        prompt.AppendLine("每条 summary 必须携带原样 content_hash，用于证明摘要新鲜度。");
        prompt.AppendLine();
        foreach (var chapter in chapters)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"## chapter_number={chapter.ChapterNumber}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"title: {chapter.Title}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"content_hash: {chapter.ContentHash}");
            prompt.AppendLine("content:");
            prompt.AppendLine("```text");
            prompt.AppendLine(LimitText(chapter.Content, 12_000));
            prompt.AppendLine("```");
        }

        return
        [
            new ChatCompletionMessage("system", NarrativePatternSystemPrompt),
            new ChatCompletionMessage("user", prompt.ToString())
        ];
    }

    private static IReadOnlyList<ChatCompletionMessage> BuildPhaseCompressionMessages(
        IReadOnlyList<NarrativePatternBoundary> boundaries,
        IReadOnlyList<NarrativePatternChapterSummary> summaries,
        IReadOnlyList<NarrativePatternCompressionInput> input,
        int targetPhaseCount)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("请把输入叙事单元递归压缩成更少的叙事阶段。");
        prompt.AppendLine("必须调用工具或返回严格 JSON；phases 必须覆盖输入单元涉及的全部章节，按升序且不得重叠。");
        prompt.AppendLine(CultureInfo.InvariantCulture, $"目标阶段数上限：{targetPhaseCount}");
        prompt.AppendLine();
        prompt.AppendLine("结构边界参考：");
        foreach (var boundary in boundaries)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"- {boundary.StartChapter}-{boundary.EndChapter}: {boundary.Label}; {boundary.Function}");
        }

        prompt.AppendLine();
        prompt.AppendLine("输入单元：");
        foreach (var item in input)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"## unit chapters={item.StartChapter}-{item.EndChapter}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"name: {item.Name}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"summary: {item.Summary}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"points: {string.Join(" / ", item.Points)}");
        }

        prompt.AppendLine();
        prompt.AppendLine("原始章节摘要索引：");
        foreach (var summary in summaries)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"- chapter {summary.ChapterNumber}: {summary.Summary}");
        }

        return
        [
            new ChatCompletionMessage("system", NarrativePatternSystemPrompt),
            new ChatCompletionMessage("user", prompt.ToString())
        ];
    }

    private static IReadOnlyList<ChatCompletionMessage> BuildSkillMessages(
        string requestedSkillName,
        IReadOnlyList<NarrativePatternChapterDocument> chapters,
        IReadOnlyList<NarrativePatternBoundary> boundaries,
        IReadOnlyList<NarrativePatternChapterSummary> summaries,
        IReadOnlyList<NarrativePatternPhase> phases)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine("请根据叙事结构边界、章节摘要和压缩阶段，生成一个可复用的 Novelist 技能 Markdown。");
        prompt.AppendLine("输出必须是完整 Markdown，开头必须包含 YAML frontmatter。");
        prompt.AppendLine("frontmatter 必须包含且只能使用单行值：name, description, category, mode, author, version。");
        prompt.AppendLine("mode 只能是 auto、manual 或 always；version 必须是正整数。");
        prompt.AppendLine("技能正文只能给出抽象叙事指导，不得复制章节原文。");
        prompt.AppendLine(CultureInfo.InvariantCulture, $"建议技能名称：{requestedSkillName}");
        prompt.AppendLine();
        prompt.AppendLine("章节来源：");
        foreach (var chapter in chapters)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"- chapter {chapter.ChapterNumber}: {chapter.Title}; hash={chapter.ContentHash}");
        }

        prompt.AppendLine();
        prompt.AppendLine("结构边界：");
        foreach (var boundary in boundaries)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"- {boundary.StartChapter}-{boundary.EndChapter}: {boundary.Label}; {boundary.Function}; evidence={boundary.Evidence}");
        }

        prompt.AppendLine();
        prompt.AppendLine("章节摘要：");
        foreach (var summary in summaries)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"- chapter {summary.ChapterNumber}: {summary.Summary}; turning_points={string.Join(" / ", summary.TurningPoints)}");
        }

        prompt.AppendLine();
        prompt.AppendLine("最终阶段：");
        foreach (var phase in phases)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"- {phase.StartChapter}-{phase.EndChapter}: {phase.PhaseName}; {phase.NarrativeFunction}; guidance={phase.Guidance}");
        }

        return
        [
            new ChatCompletionMessage("system", NarrativePatternSkillSystemPrompt),
            new ChatCompletionMessage("user", prompt.ToString())
        ];
    }

    private static string BuildNarrativeSkillPreview(
        StyleSkillDocument skill,
        string requestedSkillName,
        NarrativePatternChapterSelection selection,
        IReadOnlyList<NarrativePatternChapterDocument> documents)
    {
        var finalName = StyleSkillDocument.NormalizeSkillName(
            string.IsNullOrWhiteSpace(skill.Name) ? requestedSkillName : skill.Name);
        var ranges = string.Join(",", selection.ChapterRanges.Select(range =>
            $"{range.StartChapter.ToString(CultureInfo.InvariantCulture)}-{range.EndChapter.ToString(CultureInfo.InvariantCulture)}"));
        var hashes = string.Join(",", documents.Select(document => document.ContentHash));
        var lines = new List<string>
        {
            "---",
            $"name: {finalName}",
            $"description: {skill.Description}",
            $"category: {skill.Category}",
            $"mode: {skill.Mode}",
            $"author: {skill.Author}",
            $"version: {skill.Version.ToString(CultureInfo.InvariantCulture)}",
            "generated_by: narrative_pattern_extraction",
            $"source_chapter_ranges: {ranges}",
            $"source_chapter_hashes: {hashes}",
            "---",
            string.Empty,
            skill.Body.Trim()
        };

        var preview = string.Join('\n', lines).TrimEnd() + "\n";
        _ = StyleSkillDocument.ParseStrict(preview);
        return preview;
    }

    private static void AppendChapterCapsules(
        StringBuilder prompt,
        IReadOnlyList<NarrativePatternChapterDocument> chapters,
        int contextWindowTokens)
    {
        var maxChars = Math.Max(1_200, (contextWindowTokens - NarrativePatternPipeline.ReservedOutputTokens) * 3 / Math.Max(1, chapters.Count));
        maxChars = Math.Min(maxChars, 6_000);
        foreach (var chapter in chapters)
        {
            prompt.AppendLine(CultureInfo.InvariantCulture, $"## chapter_number={chapter.ChapterNumber}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"title: {chapter.Title}");
            prompt.AppendLine(CultureInfo.InvariantCulture, $"content_hash: {chapter.ContentHash}");
            prompt.AppendLine("bounded_excerpt:");
            prompt.AppendLine("```text");
            prompt.AppendLine(LimitText(chapter.Content, maxChars));
            prompt.AppendLine("```");
        }
    }

    private static ChatToolDefinition BoundaryToolDefinition()
    {
        return new ChatToolDefinition(
            "submit_narrative_boundaries",
            "Return validated narrative structure boundaries for selected chapters.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                required = new[] { "schema_version", "boundaries" },
                additionalProperties = false,
                properties = new
                {
                    schema_version = new { type = "string" },
                    boundaries = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            required = new[] { "start_chapter", "end_chapter", "label", "function", "evidence" },
                            additionalProperties = false,
                            properties = new
                            {
                                start_chapter = new { type = "integer", minimum = 1 },
                                end_chapter = new { type = "integer", minimum = 1 },
                                label = new { type = "string" },
                                function = new { type = "string" },
                                evidence = new { type = "string" }
                            }
                        }
                    }
                }
            }, JsonOptions));
    }

    private static ChatToolDefinition SummaryToolDefinition()
    {
        return new ChatToolDefinition(
            "submit_chapter_summaries",
            "Return content-hash-bound chapter summaries for selected chapters.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                required = new[] { "schema_version", "summaries" },
                additionalProperties = false,
                properties = new
                {
                    schema_version = new { type = "string" },
                    summaries = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            required = new[] { "chapter_number", "content_hash", "summary", "turning_points" },
                            additionalProperties = false,
                            properties = new
                            {
                                chapter_number = new { type = "integer", minimum = 1 },
                                content_hash = new { type = "string" },
                                summary = new { type = "string" },
                                turning_points = new { type = "array", items = new { type = "string" } }
                            }
                        }
                    }
                }
            }, JsonOptions));
    }

    private static ChatToolDefinition PhaseToolDefinition()
    {
        return new ChatToolDefinition(
            "submit_narrative_phases",
            "Return compressed narrative phases covering all source chapters.",
            JsonSerializer.SerializeToElement(new
            {
                type = "object",
                required = new[] { "schema_version", "phases" },
                additionalProperties = false,
                properties = new
                {
                    schema_version = new { type = "string" },
                    phases = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            required = new[] { "start_chapter", "end_chapter", "phase_name", "narrative_function", "guidance" },
                            additionalProperties = false,
                            properties = new
                            {
                                start_chapter = new { type = "integer", minimum = 1 },
                                end_chapter = new { type = "integer", minimum = 1 },
                                phase_name = new { type = "string" },
                                narrative_function = new { type = "string" },
                                guidance = new { type = "string" }
                            }
                        }
                    }
                }
            }, JsonOptions));
    }

    private static string PromptHash(IReadOnlyList<ChatCompletionMessage> messages)
    {
        return string.Join('\n', messages.Select(message => $"{message.Role}:{message.Content}"));
    }

    private static string LimitText(string content, int maxChars)
    {
        var normalized = (content ?? string.Empty).Trim();
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        var headLength = Math.Max(1, maxChars / 2);
        var tailLength = Math.Max(1, maxChars - headLength);
        return normalized[..headLength] +
            "\n[content truncated for context-window budget]\n" +
            normalized[^tailLength..];
    }

    private static CopyableDiagnosticPayload Diagnostic(
        string code,
        string message,
        string detail,
        string operation,
        string taskId)
    {
        return new CopyableDiagnosticPayload(
            Code: code,
            Message: message,
            Detail: detail.Length > MaxDiagnosticDetailLength
                ? detail[..MaxDiagnosticDetailLength] + "\n[diagnostic truncated]"
                : detail,
            Operation: operation,
            TaskId: taskId,
            RunId: null,
            BridgeMethod: operation,
            Timestamp: DateTimeOffset.UtcNow);
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

    private static IReadOnlyList<long> NormalizeSelectedChapterIds(IReadOnlyList<long>? selectedChapterIds)
    {
        if (selectedChapterIds is null || selectedChapterIds.Count == 0)
        {
            return [];
        }

        if (selectedChapterIds.Count > MaxRangeCount * 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedChapterIds),
                selectedChapterIds.Count,
                $"At most {(MaxRangeCount * 2).ToString(CultureInfo.InvariantCulture)} selected chapter ids are allowed.");
        }

        if (selectedChapterIds.Any(id => id <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(selectedChapterIds), "Selected chapter ids must be positive.");
        }

        var normalized = selectedChapterIds.ToArray();
        if (normalized.Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException("Selected chapter ids must be unique.", nameof(selectedChapterIds));
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
            SelectedChapterIds: run.SelectedChapterIds.ToArray(),
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

    private sealed record ActiveNarrativePatternExtraction(CancellationTokenSource Cancellation);

    private sealed record NarrativePatternCompressionInput(
        int StartChapter,
        int EndChapter,
        string Name,
        string Summary,
        IReadOnlyList<string> Points);

    private const string NarrativePatternSystemPrompt = """
        你是一位叙事结构分析器。
        你只能依据用户提供的章节摘录、章节号、标题和内容哈希进行分析。
        不要引入未提供的设定、角色关系或外部资料。
        输出必须是严格 JSON 或工具调用参数，schema_version 必须为 narrative-pattern-v1。
        范围必须覆盖输入章节，且按章节升序排列，不得重叠或跳章。
        """;

    private const string NarrativePatternSkillSystemPrompt = """
        你是一位负责创建 Novelist 叙事模式技能文档的写作结构分析师。
        你只能基于用户提供的边界、摘要和阶段压缩结果生成抽象叙事指导。
        不要复制章节原文，不要生成可直接插入章节的正文。
        不要输出解释性前言；只输出一个可保存的 Markdown 技能文档。
        """;
}
