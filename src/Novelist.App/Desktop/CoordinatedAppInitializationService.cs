using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.App.Desktop;

internal sealed class CoordinatedAppInitializationService : IAppInitializationService
{
 private readonly IAppInitializationService _inner;
 private readonly ReferenceCorpusAnalysisWorker _analysisWorker;
 private readonly ReferenceCorpusTechniqueVectorMaintenanceLoop _techniqueVectorMaintenanceLoop;
 private readonly SemaphoreSlim _gate = new(1, 1);

 public CoordinatedAppInitializationService(
 IAppInitializationService inner,
 ReferenceCorpusAnalysisWorker analysisWorker,
 ReferenceCorpusTechniqueVectorMaintenanceLoop techniqueVectorMaintenanceLoop)
 {
 _inner = inner ?? throw new ArgumentNullException(nameof(inner));
 _analysisWorker = analysisWorker ?? throw new ArgumentNullException(nameof(analysisWorker));
 _techniqueVectorMaintenanceLoop = techniqueVectorMaintenanceLoop ?? throw new ArgumentNullException(nameof(techniqueVectorMaintenanceLoop));
 }

 public ValueTask<bool> IsInitializedAsync(CancellationToken cancellationToken) =>
 _inner.IsInitializedAsync(cancellationToken);

 public async ValueTask StartBackgroundServicesIfInitializedAsync(CancellationToken cancellationToken)
 {
 await _gate.WaitAsync(cancellationToken);
 try
 {
 if (await _inner.IsInitializedAsync(cancellationToken))
 {
 await StartBackgroundServicesAsync(cancellationToken);
 }
 }
 finally
 {
 _gate.Release();
 }
 }

 public ValueTask InitializeAsync(string dataDirectory, CancellationToken cancellationToken) =>
 RebindAsync(
 token => _inner.InitializeAsync(dataDirectory, token),
 restartPreviousOnFailure: true,
 cancellationToken);

 public ValueTask UpdateDataDirectoryAsync(string dataDirectory, CancellationToken cancellationToken) =>
RebindAsync(
token => _inner.UpdateDataDirectoryAsync(dataDirectory, token),
 restartPreviousOnFailure: true,
 cancellationToken);

 public ValueTask<AppConfigPayload> GetAppConfigAsync(CancellationToken cancellationToken) =>
 _inner.GetAppConfigAsync(cancellationToken);

 public ValueTask<PlatformPayload> GetPlatformAsync(CancellationToken cancellationToken) =>
 _inner.GetPlatformAsync(cancellationToken);

 private async ValueTask RebindAsync(
 Func<CancellationToken, ValueTask> updateAsync,
 bool restartPreviousOnFailure,
 CancellationToken cancellationToken)
 {
 await _gate.WaitAsync(cancellationToken);
 try
 {
 var wasInitialized = await _inner.IsInitializedAsync(cancellationToken);
 await StopBackgroundServicesAsync(cancellationToken);
 try
 {
 await updateAsync(cancellationToken);
 await StartBackgroundServicesAsync(cancellationToken);
 }
 catch
 {
 if (restartPreviousOnFailure && wasInitialized)
 {
 using var restart = new CancellationTokenSource(TimeSpan.FromSeconds(10));
 await StartBackgroundServicesAsync(restart.Token);
 }

 throw;
 }
 }
 finally
 {
 _gate.Release();
 }
 }

 private async ValueTask StartBackgroundServicesAsync(CancellationToken cancellationToken)
 {
 await _analysisWorker.StartAsync(cancellationToken);
 await _techniqueVectorMaintenanceLoop.StartAsync(cancellationToken);
 }

 private async ValueTask StopBackgroundServicesAsync(CancellationToken cancellationToken)
 {
 try
 {
 await _techniqueVectorMaintenanceLoop.StopAsync(cancellationToken);
 }
 finally
 {
 await _analysisWorker.StopAsync(cancellationToken);
 }
 }
}
