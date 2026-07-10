namespace Novelist.Core.App;

public interface IReferenceCorpusAnalysisExecutionControl
{
 ValueTask<string> CheckpointAsync(
 string runId,
 string? resumeCursor,
 CancellationToken cancellationToken);
}

public static class ReferenceCorpusAnalysisExecutionActions
{
 public const string Proceed = "continue";
 public const string Pause = "pause";
 public const string Cancel = "cancel";

 public static IReadOnlyList<string> All { get; } = [Proceed, Pause, Cancel];
}

public sealed class ContinueReferenceCorpusAnalysisExecutionControl : IReferenceCorpusAnalysisExecutionControl
{
 public static ContinueReferenceCorpusAnalysisExecutionControl Instance { get; } = new();

 private ContinueReferenceCorpusAnalysisExecutionControl()
 {
 }

 public ValueTask<string> CheckpointAsync(
 string runId,
 string? resumeCursor,
 CancellationToken cancellationToken)
 {
 cancellationToken.ThrowIfCancellationRequested();
 return ValueTask.FromResult(ReferenceCorpusAnalysisExecutionActions.Proceed);
 }
}
