using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceMaterializationWorker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultIdleDelay = TimeSpan.FromSeconds(1);
    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly IReferenceMaterializationQualifier _qualifier;
    private readonly IReferenceChapterMaterialExtractor? _chapterMaterialExtractor;
    private readonly IReferenceMaterializationEmbedder _embedder;
    private readonly ReferenceMaterializationVectorIndexer _indexer;
    private readonly string _workerId;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _idleDelay;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _pumpGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _disposed;

    public ReferenceMaterializationWorker(
        IReferenceCorpusDatabasePathResolver databasePathResolver,
        IReferenceMaterializationQualifier qualifier,
        IReferenceMaterializationEmbedder embedder,
        ReferenceMaterializationVectorIndexer indexer,
        string? workerId = null,
        TimeSpan? leaseDuration = null,
        TimeSpan? idleDelay = null,
        IReferenceChapterMaterialExtractor? chapterMaterialExtractor = null)
    {
        _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
        _qualifier = qualifier ?? throw new ArgumentNullException(nameof(qualifier));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _chapterMaterialExtractor = chapterMaterialExtractor;
        _workerId = string.IsNullOrWhiteSpace(workerId)
            ? $"materialization-worker:{Environment.ProcessId}:{Guid.NewGuid():N}"
            : workerId;
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

            // 启动清扫：应用崩溃/退出后遗留的租约要等自然过期（最长一个租约期），
            // 这段时间 run 显示 running 却零进展——表象与挂起无异。workerId 内嵌
            // 持锁进程 PID：进程已死的租约立即过期，重启后可马上回收续跑；
            // 存活进程（如并行实例）的租约不动。
            await ExpireLeasesOfDeadWorkersAsync(cancellationToken);

            _loopCancellation?.Dispose();
            _loopCancellation = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_loopCancellation.Token);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal static bool TryParseWorkerProcessId(string workerId, out int processId)
    {
        // workerId 形如 materialization-worker:{pid}:{guid}；非本格式（自定义 id）不判定。
        processId = 0;
        var segments = workerId.Split(':');
        return segments.Length == 3 &&
            string.Equals(segments[0], "materialization-worker", StringComparison.Ordinal) &&
            int.TryParse(segments[1], out processId) &&
            processId > 0;
    }

    internal async ValueTask ExpireLeasesOfDeadWorkersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var store = new SqliteReferenceMaterializationRunStore(_databasePathResolver);
            foreach (var (runId, workerId) in await store.ListActiveLeasesAsync(cancellationToken))
            {
                if (string.Equals(workerId, _workerId, StringComparison.Ordinal) ||
                    !TryParseWorkerProcessId(workerId, out var pid) ||
                    IsProcessAlive(pid))
                {
                    continue;
                }

                await store.ExpireLeaseForWorkerAsync(runId, workerId, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 清扫尽力而为：失败时退回自然过期语义，不影响启动。
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
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
        var claim = await store.ClaimCurrentBatchAsync(runId, _workerId, _leaseDuration, cancellationToken);
        if (claim is null)
        {
            return await store.PromoteIfReadyAsync(runId, cancellationToken);
        }

        using var leaseLost = new CancellationTokenSource();
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = MaintainLeaseAsync(store, claim, leaseLost, heartbeatStop.Token);
        try
        {
            using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseLost.Token);
            // legacy 窗口管线需要先落候选（SQLite 写短但串行），预构建结果直接传入章节处理；
            // 章节级提取路径自带文本，无需预构建。
            var legacyBuilds = new Dictionary<int, ReferenceCandidateBuildResult>(claim.ChapterIndexes.Count);
            foreach (var chapterIndex in claim.ChapterIndexes)
            {
                if (_chapterMaterialExtractor is not null &&
                    await store.HasChapterSourceSegmentAsync(claim.RunId, chapterIndex, batchCancellation.Token))
                {
                    continue;
                }

                legacyBuilds[chapterIndex] = await store.BuildCandidatesForChapterAsync(
                    claim.RunId,
                    chapterIndex,
                    batchCancellation.Token);
            }

            // 批内章节依次调用模型：并发整章请求会直接触发服务商限流（429），
            // 且先失败的一章会取消其余章节的在途请求；串行让请求节奏跟随服务商限额。
            foreach (var chapterIndex in claim.ChapterIndexes)
            {
                ThrowIfLeaseLost(leaseLost);
                await ProcessChapterAsync(
                    store,
                    claim.RunId,
                    chapterIndex,
                    legacyBuilds.GetValueOrDefault(chapterIndex),
                    batchCancellation.Token);
            }

            ThrowIfLeaseLost(leaseLost);
            var indexed = await _indexer.IndexCurrentBatchAsync(claim.RunId, batchCancellation.Token);
            ThrowIfLeaseLost(leaseLost);
            await store.ReleaseBatchLeaseAsync(claim, cancellationToken);
            if (indexed.NextBatchIndex is null)
            {
                await store.PromoteIfReadyAsync(claim.RunId, cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.ReleaseBatchLeaseAsync(claim, CancellationToken.None);
            throw;
        }
        catch (ReferenceMaterializationException exception)
        {
            if (leaseLost.IsCancellationRequested)
            {
                return false;
            }
            await store.FailCurrentBatchAsync(claim, exception.ErrorCode, Sanitize(exception.Message), CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            if (leaseLost.IsCancellationRequested)
            {
                return false;
            }
            await store.FailCurrentBatchAsync(
                claim,
                ReferenceMaterializationErrorCodes.LlmRequestFailed,
                Sanitize(exception.Message),
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
            catch
            {
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

    // 章节处理分派：有章节级文本分段（材料化入队补建）走"整章直接提取"，
    // 否则回退 legacy 窗口切分 + 逐个打分管线。
    private async Task ProcessChapterAsync(
        SqliteReferenceMaterializationRunStore store,
        string runId,
        int chapterIndex,
        ReferenceCandidateBuildResult? legacyBuild,
        CancellationToken cancellationToken)
    {
        if (_chapterMaterialExtractor is not null &&
            await store.HasChapterSourceSegmentAsync(runId, chapterIndex, cancellationToken))
        {
            var work = await store.BeginChapterExtractionAsync(runId, chapterIndex, cancellationToken);
            if (work is not null)
            {
                // 计划分趟提取：先让模型把六种素材类型划分成若干趟（计划落库，进度有
                // 分母"第 X/N 趟"），每趟通读全章只戴一副镜头——每趟材料到达即落库
                //（候选+计数立即可见），失败从已完成的趟继续，不重复已付的模型费。
                // 旧格式计划（字符区间）读取时作废，返回 null 走重规划；已落库候选
                // 凭 upsert 幂等保留，已提取的摘录不白费。
                var request = new ReferenceChapterExtractionRequest(
                    work.AnchorId,
                    work.ChapterIndex,
                    work.ChapterTitle,
                    work.ChapterText,
                    work.Model);
                var plan = await store.ReadExtractionPlanAsync(runId, chapterIndex, cancellationToken);
                if (plan is null)
                {
                    var rounds = await _chapterMaterialExtractor.PlanChapterExtractionAsync(request, cancellationToken);
                    await store.SaveExtractionPlanAsync(runId, chapterIndex, rounds, cancellationToken);
                    plan = new SqliteReferenceMaterializationRunStore.ExtractionPlanState(rounds, 0);
                }

                var requestCount = 0;
                var extractedCount = await store.CountChapterExtractionCandidatesAsync(runId, chapterIndex, cancellationToken);
                for (; plan.RoundIndex < plan.Rounds.Count; plan = plan with { RoundIndex = plan.RoundIndex + 1 })
                {
                    if (extractedCount >= ReferenceMaterializationChatCompletionQualifier.MaxExtractedMaterialsPerChapter)
                    {
                        break;
                    }

                    var round = plan.Rounds[plan.RoundIndex];
                    var roundResult = await _chapterMaterialExtractor.ExtractChapterRoundAsync(request, round, cancellationToken);
                    requestCount++;
                    var persisted = await store.PersistExtractionRoundAsync(
                        runId, chapterIndex, roundResult.Materials, cancellationToken);
                    await store.AdvanceExtractionRoundAsync(runId, chapterIndex, cancellationToken);
                    extractedCount += persisted.PersistedExcerpts.Count;
                }

                var completed = await store.CompleteExtractionAsync(
                    runId,
                    chapterIndex,
                    requestCount,
                    cancellationToken);
                if (completed.AcceptedCount == 0)
                {
                    await store.CompleteEmptyEmbeddingAsync(runId, chapterIndex, cancellationToken);
                    return;
                }

                var embeddingWork = await store.ReadEmbeddingWorkItemAsync(runId, chapterIndex, cancellationToken);
                var embeddings = await _embedder.EmbedAsync(embeddingWork.Request, cancellationToken);
                await store.PersistEmbeddingsAsync(runId, chapterIndex, embeddings, cancellationToken);
                return;
            }

            // work 为 null 且章节分段存在：提取已持久化（embedding/indexing 阶段）。
            // 崩溃/失败后修复从这里恢复——embedding 重做嵌入，indexing 等批次索引器收尾；
            // 不再回到 legacy 窗口管线重新提取。
            var stage = await store.ReadChapterStageAsync(runId, chapterIndex, cancellationToken);
            if (stage == ReferenceMaterializationChapterStates.Embedding)
            {
                try
                {
                    var resumeWork = await store.ReadEmbeddingWorkItemAsync(runId, chapterIndex, cancellationToken);
                    var resumeEmbeddings = await _embedder.EmbedAsync(resumeWork.Request, cancellationToken);
                    await store.PersistEmbeddingsAsync(runId, chapterIndex, resumeEmbeddings, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // 无可嵌入候选（accepted=0）的 embedding 章节走空章收尾；其他 IOE 原样上抛。
                    if (await store.ReadChapterStageAsync(runId, chapterIndex, cancellationToken) ==
                            ReferenceMaterializationChapterStates.Embedding &&
                        await store.ReadChapterAcceptedCountAsync(runId, chapterIndex, cancellationToken) == 0)
                    {
                        await store.CompleteEmptyEmbeddingAsync(runId, chapterIndex, cancellationToken);
                        return;
                    }

                    throw;
                }

                return;
            }

            if (stage == ReferenceMaterializationChapterStates.Indexing)
            {
                return;
            }
        }

        var built = legacyBuild ?? await store.BuildCandidatesForChapterAsync(runId, chapterIndex, cancellationToken);
        await ProcessPreparedChapterAsync(
            store,
            runId,
            built.ChapterIndex,
            built.PendingCandidateCount,
            built.AcceptedCandidateCount,
            cancellationToken);
    }

    private async Task ProcessPreparedChapterAsync(
        SqliteReferenceMaterializationRunStore store,
        string runId,
        int chapterIndex,
        int pendingCandidateCount,
        int acceptedCandidateCount,
        CancellationToken cancellationToken)
    {
        if (pendingCandidateCount == 0)
        {
            if (acceptedCandidateCount == 0)
            {
                await store.CompleteEmptyEmbeddingAsync(runId, chapterIndex, cancellationToken);
                return;
            }

            var prequalifiedEmbeddingWork = await store.ReadEmbeddingWorkItemAsync(runId, chapterIndex, cancellationToken);
            var prequalifiedEmbeddings = await _embedder.EmbedAsync(prequalifiedEmbeddingWork.Request, cancellationToken);
            await store.PersistEmbeddingsAsync(runId, chapterIndex, prequalifiedEmbeddings, cancellationToken);
            return;
        }

        ReferenceMaterializationQualificationPersistenceResult persistedQualification;
        do
        {
            var qualificationWork = await store.ReadQualificationWorkItemAsync(runId, chapterIndex, cancellationToken);
            var qualification = await _qualifier.QualifyAsync(qualificationWork.Request, cancellationToken);
            persistedQualification = await store.PersistQualificationAsync(runId, chapterIndex, qualification, cancellationToken);
        }
        while (!persistedQualification.IsComplete);
        if (persistedQualification.AcceptedCount == 0)
        {
            await store.CompleteEmptyEmbeddingAsync(runId, chapterIndex, cancellationToken);
            return;
        }

        var embeddingWork = await store.ReadEmbeddingWorkItemAsync(runId, chapterIndex, cancellationToken);
        var embeddings = await _embedder.EmbedAsync(embeddingWork.Request, cancellationToken);
        await store.PersistEmbeddingsAsync(runId, chapterIndex, embeddings, cancellationToken);
    }

    private async Task MaintainLeaseAsync(
        SqliteReferenceMaterializationRunStore store,
        ReferenceMaterializationBatchClaim claim,
        CancellationTokenSource leaseLost,
        CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(LeaseHeartbeatInterval(), stoppingToken);
                if (!await store.RenewBatchLeaseAsync(claim, _leaseDuration, stoppingToken))
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
            throw new OperationCanceledException("Materialization worker lost the current batch lease.");
        }
    }

    private static string Sanitize(string value)
    {
        var normalized = value?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;
        return normalized.Length <= 1_200 ? normalized : normalized[..1_200];
    }
}
