using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceMaterializationWorker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultIdleDelay = TimeSpan.FromSeconds(1);
    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly IReferenceMaterializationQualifier _qualifier;
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
        TimeSpan? idleDelay = null)
    {
        _databasePathResolver = databasePathResolver ?? throw new ArgumentNullException(nameof(databasePathResolver));
        _qualifier = qualifier ?? throw new ArgumentNullException(nameof(qualifier));
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

        try
        {
            using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var builtCandidates = new List<ReferenceCandidateBuildResult>(claim.ChapterIndexes.Count);
            Task[] tasks = [];
            try
            {
                // SQLite writes are short but serialized; stage them before the model calls so every chapter
                // in the frozen batch can qualify concurrently without holding an open database transaction.
                foreach (var chapterIndex in claim.ChapterIndexes)
                {
                    builtCandidates.Add(await store.BuildCandidatesForChapterAsync(
                        claim.RunId,
                        chapterIndex,
                        batchCancellation.Token));
                }

                tasks = builtCandidates
                    .Select(candidateBuild => ProcessPreparedChapterAsync(
                        store,
                        claim.RunId,
                        candidateBuild.ChapterIndex,
                        candidateBuild.CandidateCount,
                        batchCancellation.Token))
                    .ToArray();
                await Task.WhenAll(tasks);
            }
            catch
            {
                batchCancellation.Cancel();
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch
                {
                }

                throw;
            }

            var indexed = await _indexer.IndexCurrentBatchAsync(claim.RunId, cancellationToken);
            await store.ReleaseBatchLeaseAsync(claim, cancellationToken);
            if (indexed.NextBatchIndex is null)
            {
                await store.PromoteIfReadyAsync(claim.RunId, cancellationToken);
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await store.ReleaseBatchLeaseAsync(claim, CancellationToken.None);
            throw;
        }
        catch (ReferenceMaterializationException exception)
        {
            await store.FailCurrentBatchAsync(claim, exception.ErrorCode, Sanitize(exception.Message), CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            await store.FailCurrentBatchAsync(
                claim,
                ReferenceMaterializationErrorCodes.LlmRequestFailed,
                Sanitize(exception.Message),
                CancellationToken.None);
            return true;
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

    private async Task ProcessPreparedChapterAsync(
        SqliteReferenceMaterializationRunStore store,
        string runId,
        int chapterIndex,
        int candidateCount,
        CancellationToken cancellationToken)
    {
        if (candidateCount == 0)
        {
            await store.CompleteEmptyQualificationAsync(runId, chapterIndex, cancellationToken);
            await store.CompleteEmptyEmbeddingAsync(runId, chapterIndex, cancellationToken);
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

    private static string Sanitize(string value)
    {
        var normalized = value?.Replace('\r', ' ').Replace('\n', ' ').Trim() ?? string.Empty;
        return normalized.Length <= 1_200 ? normalized : normalized[..1_200];
    }
}
