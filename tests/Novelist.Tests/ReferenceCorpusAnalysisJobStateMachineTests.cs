using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceCorpusAnalysisJobStateMachineTests
{
 [Theory]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Queued, ReferenceCorpusAnalysisJobStatuses.Paused)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Running, ReferenceCorpusAnalysisJobStatuses.PauseRequested)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Paused, ReferenceCorpusAnalysisJobStatuses.Paused)]
 public void RequestPauseIsIdempotentAndPreservesBoundarySemantics(string status, string expected)
 {
 Assert.Equal(expected, ReferenceCorpusAnalysisJobStateMachine.RequestPause(status));
 }

 [Theory]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Queued, ReferenceCorpusAnalysisJobStatuses.Cancelled)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Running, ReferenceCorpusAnalysisJobStatuses.CancelRequested)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Cancelled, ReferenceCorpusAnalysisJobStatuses.Cancelled)]
 public void RequestCancelSeparatesIntentFromTerminalCancellation(string status, string expected)
 {
 Assert.Equal(expected, ReferenceCorpusAnalysisJobStateMachine.RequestCancel(status));
 }

 [Fact]
 public void ResumeBudgetExhaustedRequiresIncreasedTotalBudget()
 {
 Assert.Throws<InvalidOperationException>(() =>
 ReferenceCorpusAnalysisJobStateMachine.Resume(
 ReferenceCorpusAnalysisJobStatuses.BudgetExhausted,
 tokenBudget: 100,
 tokensSpent: 100));

 Assert.Equal(
 ReferenceCorpusAnalysisJobStatuses.Queued,
 ReferenceCorpusAnalysisJobStateMachine.Resume(
 ReferenceCorpusAnalysisJobStatuses.BudgetExhausted,
 tokenBudget: 101,
 tokensSpent: 100));
 }

 [Fact]
public void TerminalJobsCannotBeResumed()
 {
 Assert.Throws<InvalidOperationException>(() =>
 ReferenceCorpusAnalysisJobStateMachine.Resume(
 ReferenceCorpusAnalysisJobStatuses.Cancelled,
 tokenBudget: null,
 tokensSpent: 0));
}

 [Theory]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Running, ReferenceCorpusAnalysisRunStatuses.Completed, ReferenceCorpusAnalysisJobStatuses.Completed)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Running, ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, ReferenceCorpusAnalysisJobStatuses.BudgetExhausted)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.PauseRequested, ReferenceCorpusAnalysisRunStatuses.Paused, ReferenceCorpusAnalysisJobStatuses.Paused)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.CancelRequested, ReferenceCorpusAnalysisRunStatuses.PartialCompleted, ReferenceCorpusAnalysisJobStatuses.Cancelled)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Running, ReferenceCorpusAnalysisRunStatuses.Failed, ReferenceCorpusAnalysisJobStatuses.Failed)]
 public void RunnerOutcomeMapsToCompatibleJobState(string jobStatus, string runStatus, string expected)
 {
 Assert.Equal(expected, ReferenceCorpusAnalysisJobStateMachine.ApplyRunOutcome(jobStatus, runStatus));
 }

 [Theory]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Running, ReferenceCorpusAnalysisRunStatuses.Paused)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.Running, ReferenceCorpusAnalysisRunStatuses.PartialCompleted)]
 [InlineData(ReferenceCorpusAnalysisJobStatuses.CancelRequested, ReferenceCorpusAnalysisRunStatuses.Completed)]
 public void RunnerOutcomeRejectsContradictoryJobState(string jobStatus, string runStatus)
 {
 Assert.Throws<InvalidOperationException>(() =>
 ReferenceCorpusAnalysisJobStateMachine.ApplyRunOutcome(jobStatus, runStatus));
 }
}
