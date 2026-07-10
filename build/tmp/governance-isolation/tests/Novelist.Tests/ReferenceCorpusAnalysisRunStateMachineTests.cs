using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceCorpusAnalysisRunStateMachineTests
{
    [Fact]
    public void RecordProgressTurnsBudgetLimitIntoBudgetExhaustedInsteadOfFailed()
    {
        var running = ReferenceCorpusAnalysisRunStateMachine.Start(tokenBudget: 100);

        var updated = ReferenceCorpusAnalysisRunStateMachine.RecordProgress(
            running,
            additionalTokensSpent: 100,
            resumeCursor: "node-7:rhythm");

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, updated.Status);
        Assert.Equal(100, updated.TokensSpent);
        Assert.Equal("node-7:rhythm", updated.ResumeCursor);
        Assert.True(ReferenceCorpusAnalysisRunStateMachine.CanResume(updated));
        Assert.False(ReferenceCorpusAnalysisRunStateMachine.IsTerminal(updated));
    }

    [Fact]
    public void ResumeBudgetExhaustedRunRequiresBudgetBeyondTokensAlreadySpent()
    {
        var exhausted = new ReferenceCorpusAnalysisRunState(
            ReferenceCorpusAnalysisRunStatuses.BudgetExhausted,
            TokenBudget: 100,
            TokensSpent: 100,
            ResumeCursor: "node-7:rhythm");

        Assert.Throws<InvalidOperationException>(() =>
            ReferenceCorpusAnalysisRunStateMachine.Resume(exhausted));

        var resumed = ReferenceCorpusAnalysisRunStateMachine.Resume(
            exhausted,
            newTokenBudget: 160);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Running, resumed.Status);
        Assert.Equal(160, resumed.TokenBudget);
        Assert.Equal(100, resumed.TokensSpent);
        Assert.Equal("node-7:rhythm", resumed.ResumeCursor);
    }

    [Theory]
    [InlineData(ReferenceCorpusAnalysisRunStatuses.Paused)]
    [InlineData(ReferenceCorpusAnalysisRunStatuses.PartialCompleted)]
    public void ResumeKeepsCursorForNonFailedRecoverableStops(string status)
    {
        var stopped = new ReferenceCorpusAnalysisRunState(
            status,
            TokenBudget: null,
            TokensSpent: 42,
            ResumeCursor: "node-3:syntax");

        var resumed = ReferenceCorpusAnalysisRunStateMachine.Resume(stopped);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Running, resumed.Status);
        Assert.Equal("node-3:syntax", resumed.ResumeCursor);
        Assert.Equal(42, resumed.TokensSpent);
    }

    [Theory]
    [InlineData(ReferenceCorpusAnalysisRunStatuses.Completed)]
    [InlineData(ReferenceCorpusAnalysisRunStatuses.Failed)]
    public void ResumeRejectsTerminalRuns(string status)
    {
        var terminal = new ReferenceCorpusAnalysisRunState(
            status,
            TokenBudget: 100,
            TokensSpent: 80,
            ResumeCursor: "node-3:syntax");

        Assert.Throws<InvalidOperationException>(() =>
            ReferenceCorpusAnalysisRunStateMachine.Resume(terminal, newTokenBudget: 200));
    }

    [Fact]
    public void PauseAndPartialCompletedPreserveResumeCursor()
    {
        var running = ReferenceCorpusAnalysisRunStateMachine.RecordProgress(
            ReferenceCorpusAnalysisRunStateMachine.Start(tokenBudget: null),
            additionalTokensSpent: 25,
            resumeCursor: "node-2:narrative");

        var paused = ReferenceCorpusAnalysisRunStateMachine.Pause(running);
        var partial = ReferenceCorpusAnalysisRunStateMachine.MarkPartialCompleted(running);

        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.Paused, paused.Status);
        Assert.Equal("node-2:narrative", paused.ResumeCursor);
        Assert.Equal(ReferenceCorpusAnalysisRunStatuses.PartialCompleted, partial.Status);
        Assert.Equal("node-2:narrative", partial.ResumeCursor);
    }
}
