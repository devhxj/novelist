using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceMaterializationStateMachineTests
{
    [Fact]
    public void RunStateMachineUsesOnlyWholeChapterPipelineStages()
    {
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Queued,
            ReferenceMaterializationRunStates.Extracting));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Extracting,
            ReferenceMaterializationRunStates.Embedding));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Embedding,
            ReferenceMaterializationRunStates.Indexing));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Indexing,
            ReferenceMaterializationRunStates.Completed));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Indexing,
            ReferenceMaterializationRunStates.Extracting));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Embedding,
            ReferenceMaterializationRunStates.Failed));
        Assert.False(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Queued,
            ReferenceMaterializationRunStates.Completed));
        Assert.False(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Completed,
            ReferenceMaterializationRunStates.Failed));
    }

    [Fact]
    public void ChapterStateMachineUsesOnlyExtractionAndEmbeddingStages()
    {
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Pending,
            ReferenceMaterializationChapterStates.Extracting));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Extracting,
            ReferenceMaterializationChapterStates.Embedding));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Embedding,
            ReferenceMaterializationChapterStates.Completed));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Extracting,
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
