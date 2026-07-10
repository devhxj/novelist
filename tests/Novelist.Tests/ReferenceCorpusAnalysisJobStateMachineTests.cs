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
}
