using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceCorpusTechniqueVectorMaintenanceLoop : IAsyncDisposable
{
 private readonly IReferenceCorpusService _service;
 private readonly string _workerId;
 private readonly TimeSpan _idleDelay;
 private readonly SemaphoreSlim _gate = new(1, 1);
 private CancellationTokenSource? _cancellation;
 private Task? _loop;

 public ReferenceCorpusTechniqueVectorMaintenanceLoop(
 IReferenceCorpusService service,
 string? workerId = null,
 TimeSpan? idleDelay = null)
 {
 _service = service ?? throw new ArgumentNullException(nameof(service));
 _workerId = string.IsNullOrWhiteSpace(workerId)
 ? $"m3-vector-worker:{Environment.ProcessId}:{Guid.NewGuid():N}"
 : workerId.Trim();
 _idleDelay = idleDelay ?? TimeSpan.FromSeconds(5);
 if (_idleDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleDelay));
 }

 public bool IsRunning => _loop is { IsCompleted: false };

 public async ValueTask StartAsync(CancellationToken cancellationToken = default)
 {
 await _gate.WaitAsync(cancellationToken);
 try
 {
 if (_loop is { IsCompleted: false }) return;
 _cancellation?.Dispose();
 _cancellation = new CancellationTokenSource();
 _loop = RunAsync(_cancellation.Token);
 }
 finally
 {
 _gate.Release();
 }
 }

 public async ValueTask StopAsync(CancellationToken cancellationToken = default)
 {
 Task? loop;
 await _gate.WaitAsync(cancellationToken);
 try
 {
 if (_loop is null) return;
 _cancellation!.Cancel();
 loop = _loop;
 }
 finally
 {
 _gate.Release();
 }
 await loop.WaitAsync(cancellationToken);
 await _gate.WaitAsync(cancellationToken);
 try
 {
 if (!ReferenceEquals(_loop, loop)) return;
 _loop = null;
 _cancellation?.Dispose();
 _cancellation = null;
 }
 finally
 {
 _gate.Release();
 }
 }

 private async Task RunAsync(CancellationToken cancellationToken)
 {
 while (!cancellationToken.IsCancellationRequested)
 {
 try
 {
 var result = await _service.PumpTechniqueVectorMaintenanceAsync(
 new PumpReferenceCorpusTechniqueVectorMaintenancePayload(_workerId, 120),
 cancellationToken);
 if (!result.Processed) await Task.Delay(_idleDelay, cancellationToken);
 }
 catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
 {
 break;
 }
 catch
 {
 await Task.Delay(_idleDelay, cancellationToken);
 }
 }
 }

 public async ValueTask DisposeAsync()
 {
 await StopAsync();
 _gate.Dispose();
 }
}
