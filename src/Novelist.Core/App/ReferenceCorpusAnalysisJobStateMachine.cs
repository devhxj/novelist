namespace Novelist.Core.App;

public static class ReferenceCorpusAnalysisJobStateMachine
{
 public static string RequestPause(string status) => status switch
 {
 ReferenceCorpusAnalysisJobStatuses.Queued => ReferenceCorpusAnalysisJobStatuses.Paused,
 ReferenceCorpusAnalysisJobStatuses.Running => ReferenceCorpusAnalysisJobStatuses.PauseRequested,
 ReferenceCorpusAnalysisJobStatuses.PauseRequested or ReferenceCorpusAnalysisJobStatuses.Paused => status,
 _ => throw Invalid("pause", status)
 };

 public static string RequestCancel(string status) => status switch
 {
 ReferenceCorpusAnalysisJobStatuses.Queued or
 ReferenceCorpusAnalysisJobStatuses.Paused or
 ReferenceCorpusAnalysisJobStatuses.RetryWait or
 ReferenceCorpusAnalysisJobStatuses.BudgetExhausted => ReferenceCorpusAnalysisJobStatuses.Cancelled,
 ReferenceCorpusAnalysisJobStatuses.Running or
 ReferenceCorpusAnalysisJobStatuses.PauseRequested => ReferenceCorpusAnalysisJobStatuses.CancelRequested,
 ReferenceCorpusAnalysisJobStatuses.CancelRequested or ReferenceCorpusAnalysisJobStatuses.Cancelled => status,
 _ => throw Invalid("cancel", status)
 };

 public static string Resume(string status, int? tokenBudget, int tokensSpent) => status switch
 {
 ReferenceCorpusAnalysisJobStatuses.Paused or
 ReferenceCorpusAnalysisJobStatuses.RetryWait => ReferenceCorpusAnalysisJobStatuses.Queued,
 ReferenceCorpusAnalysisJobStatuses.BudgetExhausted when tokenBudget is { } budget && budget > tokensSpent =>
 ReferenceCorpusAnalysisJobStatuses.Queued,
 ReferenceCorpusAnalysisJobStatuses.BudgetExhausted =>
 throw new InvalidOperationException("Budget-exhausted jobs require a total token budget greater than tokens already spent."),
 ReferenceCorpusAnalysisJobStatuses.Queued => ReferenceCorpusAnalysisJobStatuses.Queued,
 _ => throw Invalid("resume", status)
 };

 public static bool IsTerminal(string status) => status is
 ReferenceCorpusAnalysisJobStatuses.Cancelled or
 ReferenceCorpusAnalysisJobStatuses.Completed or
 ReferenceCorpusAnalysisJobStatuses.Failed;

 private static InvalidOperationException Invalid(string action, string status) =>
 new($"Cannot {action} reference corpus analysis job from status '{status}'.");
}
