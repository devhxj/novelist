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
    public void EnqueueContractUsesABatchSizeAllowedBySchema()
    {
        // 批大小受 reference_materialization_runs 上的
        // CHECK (chapter_batch_size IN (1, 5, 10)) 约束，取值必须落在其中。
        //
        // M1 起默认走并发批（一个租约批覆盖多章，worker 按自适应并发度并行处理）；
        // 但逐章值必须保留：既要能回滚，也要兼容旧库里已按 1 划分的 run。
        Assert.Contains(ReferenceMaterializationBatchSizes.Default, new[] { 1, 5, 10 });
        Assert.Equal(1, ReferenceMaterializationBatchSizes.ChapterWise);
        Assert.Equal(ReferenceMaterializationBatchSizes.Concurrent, ReferenceMaterializationBatchSizes.Default);
    }
}
