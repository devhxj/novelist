using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceMaterializationStateMachineTests
{
    [Fact]
    public void RunStateMachineAllowsOnlyOrderedTerminalTransitions()
    {
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Queued,
            ReferenceMaterializationRunStates.Running));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Running,
            ReferenceMaterializationRunStates.Failed));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Running,
            ReferenceMaterializationRunStates.Cancelled));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Running,
            ReferenceMaterializationRunStates.Completed));
        Assert.False(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Queued,
            ReferenceMaterializationRunStates.Completed));
        Assert.False(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Completed,
            ReferenceMaterializationRunStates.Failed));
    }

    [Fact]
    public void ChapterStateMachineRequiresQualificationAndVectorStagesBeforeCompletion()
    {
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Pending,
            ReferenceMaterializationChapterStates.BuildingCandidates));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.BuildingCandidates,
            ReferenceMaterializationChapterStates.LlmQualifying));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.LlmQualifying,
            ReferenceMaterializationChapterStates.Embedding));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Embedding,
            ReferenceMaterializationChapterStates.Indexing));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Indexing,
            ReferenceMaterializationChapterStates.Completed));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.LlmQualifying,
            ReferenceMaterializationChapterStates.Failed));
        Assert.False(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Pending,
            ReferenceMaterializationChapterStates.Completed));
        Assert.False(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Completed,
            ReferenceMaterializationChapterStates.Failed));
    }

    [Fact]
    public void EnqueueContractExposesOnlyTheFrozenFiveOrTenChapterBatchChoice()
    {
        Assert.Equal([5, 10], ReferenceMaterializationBatchSizes.All);
        Assert.Equal(5, ReferenceMaterializationBatchSizes.Default);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReferenceMaterializationBatchSizes.Validate(7));
    }
}
