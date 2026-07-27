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
            ReferenceMaterializationRunStates.Indexing,
            ReferenceMaterializationRunStates.Paused));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Paused,
            ReferenceMaterializationRunStates.Queued));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Failed,
            ReferenceMaterializationRunStates.Queued));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Completed,
            ReferenceMaterializationRunStates.Queued));
        Assert.True(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Embedding,
            ReferenceMaterializationRunStates.Failed));
        Assert.False(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Queued,
            ReferenceMaterializationRunStates.Completed));
        Assert.False(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Completed,
            ReferenceMaterializationRunStates.Failed));
        Assert.False(ReferenceMaterializationRunStateMachine.CanTransition(
            ReferenceMaterializationRunStates.Failed,
            ReferenceMaterializationRunStates.Extracting));
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
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Failed,
            ReferenceMaterializationChapterStates.Pending));
        Assert.True(ReferenceMaterializationChapterStateMachine.CanTransition(
            ReferenceMaterializationChapterStates.Completed,
            ReferenceMaterializationChapterStates.Pending));
    }

    [Fact]
    public void MaterializationContractsDoNotExposeBatchScheduling()
    {
        Assert.DoesNotContain(
            typeof(EnqueueReferenceMaterializationPayload).GetProperties(),
            property => property.Name.Contains("Batch", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(ReferenceMaterializationStatusPayload).GetProperties(),
            property => property.Name.Contains("Batch", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(ReferenceMaterializationStatusPayload).GetProperties(),
            property => property.Name == "NextAction");
        Assert.DoesNotContain(
            typeof(ReferenceMaterializationChapterProgressPayload).GetProperties(),
            property => property.Name.Contains("Batch", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(ReferenceMaterializationChapterProgressPayload).GetProperties(),
            property => property.Name == "RowVersion");
    }
}
