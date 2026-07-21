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
        IReferenceChapterMaterialExtractor extractor,
        IReferenceMaterializationEmbedder embedder,
        ReferenceMaterializationVectorIndexer indexer,
        string? workerId = null,
        TimeSpan? leaseDuration = null,
        TimeSpan? idleDelay = null)
    {
        _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
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
            Task<PreparedChapter>[] extractionTasks = [];
            try
            {
                extractionTasks = claim.ChapterIndexes
                    .Select(chapterIndex => PrepareChapterAsync(
                        store,
                        claim.RunId,
                        chapterIndex,
                        batchCancellation.Token))
                    .ToArray();
                var preparedChapters = await Task.WhenAll(extractionTasks);

                ThrowIfLeaseLost(leaseLost);
                await store.MarkBatchEmbeddingAsync(
                    claim.RunId,
                    preparedChapters.Select(chapter => chapter.WorkItem).ToArray(),
                    batchCancellation.Token);
                ThrowIfLeaseLost(leaseLost);
                var embeddedChapters = await Task.WhenAll(
                    preparedChapters.Select(chapter => EmbedChapterAsync(chapter, batchCancellation.Token)));
                foreach (var chapter in embeddedChapters)
                {
                    await store.PersistChapterAsync(
                        chapter.WorkItem,
                        chapter.Materials,
                        chapter.Embeddings,
                        batchCancellation.Token);
                }
            }
            catch
            {
                batchCancellation.Cancel();
                try
                {
                    await Task.WhenAll(extractionTasks);
                }
                catch
                {
                }

                throw;
            }

            ThrowIfLeaseLost(leaseLost);
            await store.MarkCurrentBatchEmbeddingAsync(claim, batchCancellation.Token);
            ThrowIfLeaseLost(leaseLost);
            var indexed = await _indexer.IndexCurrentBatchAsync(claim.RunId, batchCancellation.Token);
            ThrowIfLeaseLost(leaseLost);
            if (indexed.NextBatchIndex is null)
            {
                await store.PromoteIfReadyAsync(claim.RunId, cancellationToken);
            }
            await store.ReleaseBatchLeaseAsync(claim, cancellationToken);
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

    private async Task<PreparedChapter> PrepareChapterAsync(
        SqliteReferenceMaterializationRunStore store,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            var workItem = await store.ReadChapterWorkItemAsync(runId, chapterIndex, cancellationToken);
            var extraction = await _extractor.ExtractAsync(
                new ReferenceChapterMaterialExtractionRequest(
                    workItem.Model,
                    workItem.AnchorId,
                    workItem.ChapterIndex,
                    workItem.ChapterTitle,
                    workItem.ChapterText),
                cancellationToken);
            var materials = SqliteReferenceMaterializationRunStore.PrepareMaterials(workItem, extraction);
            return new PreparedChapter(workItem, materials);
        }
        catch (ReferenceMaterializationException exception)
        {
            throw new ReferenceMaterializationException(
                exception.ErrorCode,
                $"Chapter {chapterIndex}: {exception.Message}");
        }
    }

    private async Task<EmbeddedChapter> EmbedChapterAsync(
        PreparedChapter chapter,
        CancellationToken cancellationToken)
    {
        var embeddings = await _embedder.EmbedAsync(
            new ReferenceMaterializationEmbeddingRequest(
                chapter.WorkItem.EmbeddingModel,
                chapter.Materials.Select(material => new ReferenceMaterializationEmbeddingItem(
                    material.MaterialId,
                    material.Text)).ToArray()),
            cancellationToken);
        return new EmbeddedChapter(chapter.WorkItem, chapter.Materials, embeddings);
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

    private sealed record PreparedChapter(
        ReferenceChapterMaterializationWorkItem WorkItem,
        IReadOnlyList<PreparedReferenceMaterial> Materials);

    private sealed record EmbeddedChapter(
        ReferenceChapterMaterializationWorkItem WorkItem,
        IReadOnlyList<PreparedReferenceMaterial> Materials,
        ReferenceMaterializationEmbeddingResult Embeddings);
}
