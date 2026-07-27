using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceMaterializationWorker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultIdleDelay = TimeSpan.FromSeconds(1);
    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly IReferenceChapterMaterialExtractor _extractor;
    private readonly IReferenceMaterializationEmbedder _embedder;
    private readonly ReferenceMaterializationVectorIndexer _indexer;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _idleDelay;
    private readonly Action<string, Exception?>? _writeLog;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _disposed;

    public ReferenceMaterializationWorker(
        IReferenceCorpusDatabasePathResolver databasePathResolver,
        IReferenceChapterMaterialExtractor extractor,
        IReferenceMaterializationEmbedder embedder,
        ReferenceMaterializationVectorIndexer indexer,
        TimeSpan? leaseDuration = null,
        TimeSpan? idleDelay = null,
        Action<string, Exception?>? writeLog = null)
    {
        _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;
        if (_leaseDuration <= TimeSpan.Zero || _leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        _idleDelay = idleDelay ?? DefaultIdleDelay;
        if (_idleDelay <= TimeSpan.Zero || _idleDelay > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(idleDelay));
        }

        _writeLog = writeLog;
    }

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is { IsCompleted: false })
            {
                return;
            }

            _loopCancellation?.Dispose();
            _loopCancellation = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_loopCancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            loop = _loopTask;
            if (loop is null)
            {
                return;
            }

            _loopCancellation!.Cancel();
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await loop.WaitAsync(cancellationToken);
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (ReferenceEquals(loop, _loopTask))
            {
                _loopTask = null;
                _loopCancellation?.Dispose();
                _loopCancellation = null;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask<bool> PumpOnceAsync(CancellationToken cancellationToken)
    {
        await _pumpGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var store = new SqliteReferenceMaterializationRunStore(_databasePathResolver);
            var runId = await store.ReadNextRunnableRunIdAsync(cancellationToken);
            return runId is not null && await ProcessRunOnceAsync(runId, cancellationToken);
        }
        finally
        {
            _pumpGate.Release();
        }
    }

    public async ValueTask<bool> ProcessRunOnceAsync(string runId, CancellationToken cancellationToken)
    {
        var store = new SqliteReferenceMaterializationRunStore(_databasePathResolver);
        var claim = await store.ClaimCurrentChapterAsync(runId, _leaseDuration, cancellationToken);
        if (claim is null)
        {
            return await store.PromoteIfReadyAsync(runId, cancellationToken);
        }

        WriteLog(
            $"Reference materialization chapter processing started: run_id={claim.RunId}, chapter={claim.ChapterIndex}.");

        using var leaseLost = new CancellationTokenSource();
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = MaintainLeaseAsync(store, claim, leaseLost, heartbeatStop.Token);
        try
        {
            using var chapterCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseLost.Token);
            if (claim.RequiresProcessing)
            {
                var preparedChapter = await PrepareChapterAsync(
                    store,
                    claim,
                    chapterCancellation.Token);
                ThrowIfLeaseLost(leaseLost);
                await store.MarkChapterEmbeddingAsync(claim, preparedChapter.WorkItem, chapterCancellation.Token);
                ThrowIfLeaseLost(leaseLost);
                var embeddedChapter = await EmbedChapterAsync(preparedChapter, chapterCancellation.Token);
                ThrowIfLeaseLost(leaseLost);
                await store.PersistChapterAsync(
                    claim,
                    embeddedChapter.WorkItem,
                    embeddedChapter.Materials,
                    embeddedChapter.Embeddings,
                    chapterCancellation.Token);
            }

            ThrowIfLeaseLost(leaseLost);
            await store.MarkCurrentChapterEmbeddingAsync(claim, chapterCancellation.Token);
            ThrowIfLeaseLost(leaseLost);
            var indexed = await _indexer.IndexCurrentChapterAsync(claim, chapterCancellation.Token);
            ThrowIfLeaseLost(leaseLost);
            if (indexed.NextChapterIndex is null)
            {
                await store.PromoteIfReadyAsync(claim.RunId, cancellationToken);
            }
            await store.ReleaseChapterLeaseAsync(claim, cancellationToken);
            WriteLog($"Reference materialization chapter completed: run_id={claim.RunId}, chapter={claim.ChapterIndex}.");
            return true;
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.ReleaseChapterLeaseAsync(claim, CancellationToken.None);
            throw;
        }
        catch (ReferenceMaterializationException exception)
        {
            if (leaseLost.IsCancellationRequested)
            {
                return false;
            }
            var message = Sanitize(exception.Message);
            WriteLog(
                $"Reference materialization chapter failed: run_id={claim.RunId}, chapter={claim.ChapterIndex}, error_code={exception.ErrorCode}, message={message}",
                exception);
            await store.FailCurrentChapterAsync(claim, exception.ErrorCode, message, CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            if (leaseLost.IsCancellationRequested)
            {
                return false;
            }
            var message = Sanitize(exception.Message);
            WriteLog(
                $"Reference materialization chapter failed: run_id={claim.RunId}, chapter={claim.ChapterIndex}, error_code={ReferenceMaterializationErrorCodes.LlmRequestFailed}, message={message}",
                exception);
            await store.FailCurrentChapterAsync(
                claim,
                ReferenceMaterializationErrorCodes.LlmRequestFailed,
                message,
                CancellationToken.None);
            return true;
        }
        finally
        {
            heartbeatStop.Cancel();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException) when (heartbeatStop.IsCancellationRequested)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _lifecycleGate.WaitAsync();
        try
        {
            _disposed = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await _pumpGate.WaitAsync();
        _pumpGate.Release();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await PumpOnceAsync(cancellationToken))
                {
                    await Task.Delay(_idleDelay, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                WriteLog("Reference materialization worker loop failed.", exception);
                try
                {
                    await Task.Delay(_idleDelay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task<PreparedChapter> PrepareChapterAsync(
        SqliteReferenceMaterializationRunStore store,
        ReferenceMaterializationChapterClaim claim,
        CancellationToken cancellationToken)
    {
        try
        {
            WriteLog($"Reference materialization chapter started: run_id={claim.RunId}, chapter={claim.ChapterIndex}.");
            var workItem = await store.ReadChapterWorkItemAsync(claim, cancellationToken);
            var extraction = await _extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    workItem.Model,
                    workItem.AnchorId,
                    workItem.ChapterIndex,
                    workItem.ChapterTitle,
                    workItem.ChapterText),
                cancellationToken);
            var materials = SqliteReferenceMaterializationRunStore.PrepareMaterials(workItem, extraction);
            WriteLog($"Reference materialization chapter extracted: run_id={claim.RunId}, chapter={claim.ChapterIndex}, materials={materials.Count}.");
            return new PreparedChapter(workItem, materials);
        }
        catch (ReferenceMaterializationException exception)
        {
            throw new ReferenceMaterializationException(
                exception.ErrorCode,
                $"Chapter {claim.ChapterIndex}: {exception.Message}");
        }
    }

    private async Task<EmbeddedChapter> EmbedChapterAsync(
        PreparedChapter chapter,
        CancellationToken cancellationToken)
    {
        try
        {
            var embeddings = await _embedder.EmbedAsync(
                new ReferenceMaterializationEmbeddingRequest(
                    chapter.WorkItem.EmbeddingModel,
                    chapter.Materials.Select(material => new ReferenceMaterializationEmbeddingItem(
                        material.MaterialId,
                        BuildEmbeddingInput(material))).ToArray()),
                cancellationToken);
            return new EmbeddedChapter(chapter.WorkItem, chapter.Materials, embeddings);
        }
        catch (ReferenceMaterializationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingRequestFailed,
                $"Chapter {chapter.WorkItem.ChapterIndex}: Materialization embedding request failed.",
                exception);
        }
    }

    internal static string BuildEmbeddingInput(PreparedReferenceMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var metadata = material.Metadata;
        var sections = new List<string> { $"原文:\n{material.Text}", $"素材类型:\n{metadata.SourceKind}" };
        if (metadata.Entities.Count > 0)
        {
            sections.Add("实体:\n" + string.Join("、", metadata.Entities.Select(entity => $"{entity.Name}（{entity.Kind}）")));
        }

        if (metadata.Setting is not null)
        {
            var setting = new List<string>();
            if (metadata.Setting.Location is not null) setting.Add($"地点：{metadata.Setting.Location}");
            if (metadata.Setting.Time is not null) setting.Add($"时间：{metadata.Setting.Time}");
            if (metadata.Setting.Environment is not null) setting.Add($"环境：{metadata.Setting.Environment}");
            if (setting.Count > 0) sections.Add("场景:\n" + string.Join("；", setting));
        }

        if (metadata.Perspective is not null)
        {
            var perspective = metadata.Perspective.FocusEntity is null
                ? metadata.Perspective.Mode
                : $"{metadata.Perspective.Mode}：{metadata.Perspective.FocusEntity}";
            sections.Add("叙述视角:\n" + perspective);
        }

        AddOptionalEmbeddingSection(sections, "事件", metadata.Event);
        if (metadata.Facts.Count > 0)
        {
            sections.Add("事实要点:\n" + string.Join("；", metadata.Facts.Select(fact =>
                fact.Subject is null ? fact.Content : $"{fact.Subject}：{fact.Content}")));
        }

        if (metadata.Causality is not null)
        {
            var causality = new List<string>();
            if (metadata.Causality.Cause is not null) causality.Add($"原因：{metadata.Causality.Cause}");
            if (metadata.Causality.Consequence is not null) causality.Add($"结果：{metadata.Causality.Consequence}");
            sections.Add("因果:\n" + string.Join("；", causality));
        }

        if (metadata.StateChanges.Count > 0)
        {
            sections.Add("状态变化:\n" + string.Join("；", metadata.StateChanges.Select(change =>
                $"{change.Subject}：{change.Before}→{change.After}")));
        }

        AddOptionalEmbeddingSection(sections, "人物动态", metadata.CharacterDynamics);
        if (metadata.Conflict is not null)
        {
            var conflict = new List<string>();
            if (metadata.Conflict.Pressure is not null) conflict.Add($"压力：{metadata.Conflict.Pressure}");
            if (metadata.Conflict.Cost is not null) conflict.Add($"代价：{metadata.Conflict.Cost}");
            sections.Add("冲突与代价:\n" + string.Join("；", conflict));
        }

        if (metadata.Information is not null)
        {
            var information = new List<string>();
            if (metadata.Information.Role is not null) information.Add($"角色：{metadata.Information.Role}");
            if (metadata.Information.Content is not null) information.Add($"内容：{metadata.Information.Content}");
            sections.Add("信息:\n" + string.Join("；", information));
        }

        if (metadata.Emotion is not null)
        {
            var emotion = new List<string>();
            if (metadata.Emotion.Tone is not null) emotion.Add($"情绪：{metadata.Emotion.Tone}");
            if (metadata.Emotion.Subtext is not null) emotion.Add($"潜台词：{metadata.Emotion.Subtext}");
            sections.Add("情绪与潜台词:\n" + string.Join("；", emotion));
        }

        if (metadata.NarrativeFunctions.Count > 0) sections.Add("叙事作用:\n" + string.Join("、", metadata.NarrativeFunctions));
        if (metadata.Foreshadowing.Count > 0)
        {
            sections.Add("伏笔线:\n" + string.Join("；", metadata.Foreshadowing.Select(item => $"{item.Phase}：{item.Target}")));
        }

        if (metadata.Motifs.Count > 0) sections.Add("意象与母题:\n" + string.Join("、", metadata.Motifs));
        if (metadata.ExpressionTechniques.Count > 0) sections.Add("表达技法:\n" + string.Join("、", metadata.ExpressionTechniques));
        AddOptionalEmbeddingSection(sections, "复用提示", metadata.ReuseHint);
        return string.Join("\n\n", sections);
    }

    private static void AddOptionalEmbeddingSection(ICollection<string> sections, string label, string? value)
    {
        if (value is not null)
        {
            sections.Add($"{label}:\n{value}");
        }
    }

    private async Task MaintainLeaseAsync(
        SqliteReferenceMaterializationRunStore store,
        ReferenceMaterializationChapterClaim claim,
        CancellationTokenSource leaseLost,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(LeaseHeartbeatInterval(), stoppingToken);
                if (!await store.RenewChapterLeaseAsync(claim, _leaseDuration, stoppingToken))
                {
                    leaseLost.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch
        {
            leaseLost.Cancel();
        }
    }

    private TimeSpan LeaseHeartbeatInterval()
    {
        var interval = TimeSpan.FromTicks(_leaseDuration.Ticks / 3);
        return interval < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100)
            : interval;
    }

    private static void ThrowIfLeaseLost(CancellationTokenSource leaseLost)
    {
        if (leaseLost.IsCancellationRequested)
        {
            throw new OperationCanceledException("Materialization worker lost the current chapter lease.");
        }
    }

    private static string Sanitize(string value)
    {
        var normalized = value?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;
        return normalized.Length <= 1_200 ? normalized : normalized[..1_200];
    }

    private void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            _writeLog?.Invoke(message, exception);
        }
        catch
        {
        }
    }

    private sealed record PreparedChapter(
        ReferenceChapterMaterializationWorkItem WorkItem,
        IReadOnlyList<PreparedReferenceMaterial> Materials);

    private sealed record EmbeddedChapter(
        ReferenceChapterMaterializationWorkItem WorkItem,
        IReadOnlyList<PreparedReferenceMaterial> Materials,
        ReferenceMaterializationEmbeddingResult Embeddings);
}
