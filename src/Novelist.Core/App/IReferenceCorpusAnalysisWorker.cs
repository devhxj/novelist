namespace Novelist.Core.App;

public interface IReferenceCorpusAnalysisWorker : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
