using System.Collections.Concurrent;
using System.Diagnostics;
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

    // 受控并发（M1）：起步 2，连续无故障推进则 +1，上限 4；任一章失败立即 -1
    //（下限 1）并进入冷却，避免在限流边缘来回抖动。
    //
    // 全部是内部自适应，不暴露为设置项——AGENTS.md 明确禁止扩张专家控制面。
    // 上限压在 4 而非更高，是因为源码注释实测过：并发整章请求会直接触发
    // 服务商 429，且网关约 2 分钟会掐断长生成。
    private const int MinimumConcurrency = 1;
    private const int InitialConcurrency = 2;
    private const int MaximumConcurrency = 4;
    private const int SuccessesBeforeRaise = 4;
    private static readonly TimeSpan ConcurrencyCooldown = TimeSpan.FromSeconds(30);

    // 章级失败后的全局冷却：批内多章同时失败几乎必然是瞬时突发撞了限流，
    // 此时只降并发是不够的——worker 会立刻开始下一批，继续以同样节奏撞上去，
    // 于是"处理十几章就限流"变成持续雪崩。冷却期内整轮暂停，给配额恢复的时间。
    private static readonly TimeSpan ChapterFailureCooldown = TimeSpan.FromSeconds(60);
    private readonly object _concurrencyGate = new();
    private int _concurrency = InitialConcurrency;
    private int _consecutiveSuccesses;
    private DateTimeOffset _raiseBlockedUntil;
    private DateTimeOffset _coolDownUntil;

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
        // 先等冷却结束再领批：失败后立即领下一批等于以同样节奏再撞一次限流。
        // 放在领租约之前，冷却期间不占用租约。
        await WaitForFailureCoolDownAsync(cancellationToken);
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

            // 批内章节受控并发：并发度由 AIMD 自适应决定，不再固定串行。
            // 单章失败只挂该章（FailChapterAsync），不牵连同批其余章——这正是
            // 当年退回逐章处理的原因，上并发前必须先消除它。
            var sinks = new ConcurrentBag<(int ChapterIndex, ReferenceMaterializationChapterDiagnosticsSink Sink)>();
            var concurrency = CurrentConcurrency();
            using var concurrencyGate = new SemaphoreSlim(concurrency, concurrency);
            var chapterTasks = new List<Task>(claim.ChapterIndexes.Count);
            foreach (var chapterIndex in claim.ChapterIndexes)
            {
                chapterTasks.Add(ProcessChapterWithIsolationAsync(
                    store,
                    claim,
                    chapterIndex,
                    legacyBuilds.GetValueOrDefault(chapterIndex),
                    sinks,
                    concurrencyGate,
                    leaseLost,
                    batchCancellation.Token));
            }

            await Task.WhenAll(chapterTasks);
            ThrowIfLeaseLost(leaseLost);
            var chapterSinks = sinks.ToArray();
            var indexStarted = Stopwatch.GetTimestamp();
            var indexed = await _indexer.IndexCurrentBatchAsync(claim.RunId, batchCancellation.Token);
            var indexMs = Stopwatch.GetElapsedTime(indexStarted).TotalMilliseconds;

            // 索引是批级的（一批多章），按实际参与的章数均摊，
            // 才能与章级阶段放在同一尺度上比较。
            if (chapterSinks.Length > 0)
            {
                var perChapterIndexMs = indexMs / chapterSinks.Length;
                var databasePath = await _databasePathResolver.ResolveAsync(CancellationToken.None);
                foreach (var (chapterIndex, sink) in chapterSinks)
                {
                    sink.AddIndex(perChapterIndexMs);
                    ReferenceMaterializationDiagnosticsWriter.Append(
                        databasePath,
                        sink.Complete(claim.RunId, chapterIndex));
                }
            }

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

    private int CurrentConcurrency()
    {
        lock (_concurrencyGate)
        {
            return _concurrency;
        }
    }

    // 趟次并发数由章级并发反推，两者共享同一个总预算（MaximumConcurrency）：
    // 章级 1 → 趟次 4，章级 2 → 趟次 2，章级 3~4 → 趟次 1。
    //
    // 这样"同时在途的整章请求"始终不超过预算。否则章级 3 × 趟次 3 = 9 个并发请求，
    // 又会重现"一批同时打出去撞 429、然后整批一起失败"的雪崩。
    private int CurrentRoundConcurrency()
    {
        lock (_concurrencyGate)
        {
            return Math.Max(1, MaximumConcurrency / Math.Max(1, _concurrency));
        }
    }

    private void RecordChapterSuccess()
    {
        lock (_concurrencyGate)
        {
            if (DateTimeOffset.UtcNow < _raiseBlockedUntil)
            {
                return;
            }

            if (++_consecutiveSuccesses < SuccessesBeforeRaise || _concurrency >= MaximumConcurrency)
            {
                return;
            }

            _concurrency++;
            _consecutiveSuccesses = 0;
        }
    }

    private void RecordChapterFailure()
    {
        lock (_concurrencyGate)
        {
            if (_concurrency > MinimumConcurrency)
            {
                _concurrency--;
            }

            _consecutiveSuccesses = 0;
            _raiseBlockedUntil = DateTimeOffset.UtcNow.Add(ConcurrencyCooldown);

            // 冷却从"最后一次失败"起算：连续失败会不断延后，直到真正打住。
            var until = DateTimeOffset.UtcNow.Add(ChapterFailureCooldown);
            if (until > _coolDownUntil)
            {
                _coolDownUntil = until;
            }
        }
    }

    // 冷却期内整轮让路：这样才不会"降了并发但节奏不变"地继续撞限流。
    private async ValueTask WaitForFailureCoolDownAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (_concurrencyGate)
            {
                remaining = _coolDownUntil - DateTimeOffset.UtcNow;
            }

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining, cancellationToken);
        }
    }

    // 单章执行 + 故障隔离：任一章出错只把这一章标记为 failed，同批其余章继续跑。
    // 租约丢失/外部取消例外——那属于批级收尾，不在这里吞掉。
    private async Task ProcessChapterWithIsolationAsync(
        SqliteReferenceMaterializationRunStore store,
        ReferenceMaterializationBatchClaim claim,
        int chapterIndex,
        ReferenceCandidateBuildResult? legacyBuild,
        ConcurrentBag<(int ChapterIndex, ReferenceMaterializationChapterDiagnosticsSink Sink)> sinks,
        SemaphoreSlim concurrencyGate,
        CancellationTokenSource leaseLost,
        CancellationToken cancellationToken)
    {
        await concurrencyGate.WaitAsync(CancellationToken.None);
        // 每章独立 store：store 每次操作都新开连接，本就无共享连接，
        // 但仍按章隔离实例，杜绝任何实例级状态在并发下被踩到。
        var chapterStore = new SqliteReferenceMaterializationRunStore(_databasePathResolver);
        try
        {
            var sink = new ReferenceMaterializationChapterDiagnosticsSink();
            sinks.Add((chapterIndex, sink));
            // 把这一章的整条执行流标记为后台：其内部所有模型调用都会先给
            // 作者正在等待的前台请求（写作/聊天/高级素材分析）让出配额。
            // 标记沿异步流下传，子调用无需感知。
            using (ModelRequestArbiter.BeginBackground())
            {
                await ProcessChapterAsync(chapterStore, claim.RunId, chapterIndex, legacyBuild, sink, cancellationToken);
            }

            RecordChapterSuccess();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReferenceMaterializationException exception)
        {
            RecordChapterFailure();
            await chapterStore.FailChapterAsync(
                claim, chapterIndex, exception.ErrorCode, Sanitize(exception.Message), CancellationToken.None);
        }
        catch (Exception exception)
        {
            RecordChapterFailure();
            await chapterStore.FailChapterAsync(
                claim,
                chapterIndex,
                ReferenceMaterializationErrorCodes.LlmRequestFailed,
                Sanitize(exception.Message),
                CancellationToken.None);
        }
        finally
        {
            concurrencyGate.Release();
        }
    }

    // 章节处理分派：有章节级文本分段（材料化入队补建）走"整章直接提取"，
    // 否则回退 legacy 窗口切分 + 逐个打分管线。
    private async Task ProcessChapterAsync(
        SqliteReferenceMaterializationRunStore store,
        string runId,
        int chapterIndex,
        ReferenceCandidateBuildResult? legacyBuild,
        ReferenceMaterializationChapterDiagnosticsSink sink,
        CancellationToken cancellationToken)
    {
        if (_chapterMaterialExtractor is not null &&
            await store.HasChapterSourceSegmentAsync(runId, chapterIndex, cancellationToken))
        {
            var work = await store.BeginChapterExtractionAsync(runId, chapterIndex, cancellationToken);
            if (work is not null)
            {
                // 防御深度：前置拆分保证章非空，但坏数据（空/全空白文本）不该把
                // 整批判死——没有文本就没有素材。复用零候选路径收尾：完成提取
                //（llm_qualifying→embedding，终值计数为 0）再走空章收尾。
                if (string.IsNullOrWhiteSpace(work.ChapterText))
                {
                    await store.CompleteExtractionAsync(runId, chapterIndex, 0, cancellationToken);
                    await store.CompleteEmptyEmbeddingAsync(runId, chapterIndex, cancellationToken);
                    return;
                }

                // 判定丢失 / 人工重准入收尾：本章仍有未判定候选（历史版本把判定抹回
                // pending，或复核把候选打回 pending 等重准入）时补跑判定阶段，而不是
                // 重跑提取趟——已付的模型费不重复支付，复核者调整过的证据边界也不会
                // 被模型重新裁切。趟次已跑完的章节在这里天然只走这一步。
                var (decidedCount, undecidedCount) = await store.CountChapterExtractionCandidateDecisionsAsync(
                    runId, chapterIndex, cancellationToken);
                if (undecidedCount > 0)
                {
                    await QualifyAndEmbedChapterAsync(store, runId, chapterIndex, sink, cancellationToken);
                    return;
                }

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
                    var planStarted = Stopwatch.GetTimestamp();
                    var rounds = await _chapterMaterialExtractor.PlanChapterExtractionAsync(request, cancellationToken);
                    sink.AddPlan(Stopwatch.GetElapsedTime(planStarted).TotalMilliseconds);
                    sink.AddModelCalls(1);
                    await store.SaveExtractionPlanAsync(runId, chapterIndex, rounds, cancellationToken);
                    plan = new SqliteReferenceMaterializationRunStore.ExtractionPlanState(rounds, 0);
                }

                var requestCount = 0;
                var extractedCount = decidedCount;

                // 趟次并发：同一章的各趟只是"通读全章的不同镜头"，彼此没有数据依赖，
                // 串行执行等于把埋点里 round 那 80% 的时间白白等掉。
                //
                // 落库仍按序串行：PersistExtractionRound / AdvanceExtractionRound 会推进
                // 章节的趟次状态，并发写会互相踩踏。先并发拿到全部结果，再按序落库，
                // 既拿到提速又不碰状态推进的并发安全。
                var pendingRounds = extractedCount >= ReferenceMaterializationChatCompletionQualifier.MaxExtractedMaterialsPerChapter
                    ? []
                    : plan.Rounds.Skip(plan.RoundIndex).ToArray();

                if (pendingRounds.Length > 0)
                {
                    var roundResults = new ReferenceChapterExtractionResult[pendingRounds.Length];
                    var roundConcurrency = Math.Min(pendingRounds.Length, CurrentRoundConcurrency());
                    using var roundGate = new SemaphoreSlim(roundConcurrency, roundConcurrency);
                    var roundTasks = new List<Task>(pendingRounds.Length);
                    for (var position = 0; position < pendingRounds.Length; position++)
                    {
                        var slot = position;
                        roundTasks.Add(Task.Run(async () =>
                        {
                            await roundGate.WaitAsync(cancellationToken);
                            try
                            {
                                var roundStarted = Stopwatch.GetTimestamp();
                                var result = await _chapterMaterialExtractor.ExtractChapterRoundAsync(
                                    request, pendingRounds[slot], cancellationToken);
                                sink.AddRound(Stopwatch.GetElapsedTime(roundStarted).TotalMilliseconds);
                                // 一趟可能因为掐流续跑成多次请求：按实际调用数记账，
                                // 否则界面上的"模型调用"会少报，作者看到的成本是错的。
                                sink.AddModelCalls(Math.Max(1, result.ModelCallCount));
                                roundResults[slot] = result;
                            }
                            finally
                            {
                                roundGate.Release();
                            }
                        }, cancellationToken));
                    }

                    await Task.WhenAll(roundTasks);

                    foreach (var roundResult in roundResults)
                    {
                        requestCount += Math.Max(1, roundResult.ModelCallCount);
                        var persisted = await store.PersistExtractionRoundAsync(
                            runId, chapterIndex, roundResult.Materials, cancellationToken);
                        await store.AdvanceExtractionRoundAsync(runId, chapterIndex, cancellationToken);
                        extractedCount += persisted.PersistedExcerpts.Count;
                    }
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
                var embedStarted = Stopwatch.GetTimestamp();
                var embeddings = await _embedder.EmbedAsync(embeddingWork.Request, cancellationToken);
                sink.AddEmbed(Stopwatch.GetElapsedTime(embedStarted).TotalMilliseconds);
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
            sink,
            cancellationToken);
    }

    private async Task ProcessPreparedChapterAsync(
        SqliteReferenceMaterializationRunStore store,
        string runId,
        int chapterIndex,
        int pendingCandidateCount,
        int acceptedCandidateCount,
        ReferenceMaterializationChapterDiagnosticsSink sink,
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
            var prequalifiedStarted = Stopwatch.GetTimestamp();
            var prequalifiedEmbeddings = await _embedder.EmbedAsync(prequalifiedEmbeddingWork.Request, cancellationToken);
            sink.AddEmbed(Stopwatch.GetElapsedTime(prequalifiedStarted).TotalMilliseconds);
            await store.PersistEmbeddingsAsync(runId, chapterIndex, prequalifiedEmbeddings, cancellationToken);
            return;
        }

        await QualifyAndEmbedChapterAsync(store, runId, chapterIndex, sink, cancellationToken);
    }

    // 判定阶段收尾：把章节内未判定候选交给判定模型打分，随后推进到嵌入；
    // 整章零接纳时走空收尾，让批次索引器正常关账。
    private async Task QualifyAndEmbedChapterAsync(
        SqliteReferenceMaterializationRunStore store,
        string runId,
        int chapterIndex,
        ReferenceMaterializationChapterDiagnosticsSink sink,
        CancellationToken cancellationToken)
    {
        ReferenceMaterializationQualificationPersistenceResult persistedQualification;
        do
        {
            var qualificationWork = await store.ReadQualificationWorkItemAsync(runId, chapterIndex, cancellationToken);
            var qualifyStarted = Stopwatch.GetTimestamp();
            var qualification = await _qualifier.QualifyAsync(qualificationWork.Request, cancellationToken);
            sink.AddQualify(Stopwatch.GetElapsedTime(qualifyStarted).TotalMilliseconds);
            sink.AddModelCalls(1);
            persistedQualification = await store.PersistQualificationAsync(runId, chapterIndex, qualification, cancellationToken);
        }
        while (!persistedQualification.IsComplete);
        if (persistedQualification.AcceptedCount == 0)
        {
            await store.CompleteEmptyEmbeddingAsync(runId, chapterIndex, cancellationToken);
            return;
        }

        var embeddingWork = await store.ReadEmbeddingWorkItemAsync(runId, chapterIndex, cancellationToken);
        var embedStarted = Stopwatch.GetTimestamp();
        var embeddings = await _embedder.EmbedAsync(embeddingWork.Request, cancellationToken);
        sink.AddEmbed(Stopwatch.GetElapsedTime(embedStarted).TotalMilliseconds);
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
