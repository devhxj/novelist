using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class ReferenceMaterializationRunStateMachine
{
    public static bool CanTransition(string current, string next)
    {
        return current switch
        {
            ReferenceMaterializationRunStates.Queued =>
                next is ReferenceMaterializationRunStates.Running or
                ReferenceMaterializationRunStates.Failed or
                ReferenceMaterializationRunStates.Cancelled,
            ReferenceMaterializationRunStates.Running =>
                next is ReferenceMaterializationRunStates.Failed or
                ReferenceMaterializationRunStates.Completed or
                ReferenceMaterializationRunStates.Cancelled,
            ReferenceMaterializationRunStates.Failed or ReferenceMaterializationRunStates.Cancelled =>
                next == ReferenceMaterializationRunStates.Running,
            _ => false
        };
    }

    public static void EnsureCanTransition(string current, string next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"Reference materialization run cannot transition from '{current}' to '{next}'.");
        }
    }
}

public static class ReferenceMaterializationChapterStateMachine
{
    public static bool CanTransition(string current, string next)
    {
        return current switch
        {
            ReferenceMaterializationChapterStates.Pending =>
                next is ReferenceMaterializationChapterStates.BuildingCandidates or
                ReferenceMaterializationChapterStates.Failed or
                ReferenceMaterializationChapterStates.Cancelled,
            ReferenceMaterializationChapterStates.BuildingCandidates =>
                next is ReferenceMaterializationChapterStates.LlmQualifying or
                ReferenceMaterializationChapterStates.Failed or
                ReferenceMaterializationChapterStates.Cancelled,
            ReferenceMaterializationChapterStates.LlmQualifying =>
                next is ReferenceMaterializationChapterStates.Embedding or
                ReferenceMaterializationChapterStates.Failed or
                ReferenceMaterializationChapterStates.Cancelled,
            ReferenceMaterializationChapterStates.Embedding =>
                next is ReferenceMaterializationChapterStates.Indexing or
                ReferenceMaterializationChapterStates.Failed or
                ReferenceMaterializationChapterStates.Cancelled,
            ReferenceMaterializationChapterStates.Indexing =>
                next is ReferenceMaterializationChapterStates.Completed or
                ReferenceMaterializationChapterStates.Failed or
                ReferenceMaterializationChapterStates.Cancelled,
            ReferenceMaterializationChapterStates.Failed or ReferenceMaterializationChapterStates.Cancelled =>
                next == ReferenceMaterializationChapterStates.BuildingCandidates,
            _ => false
        };
    }

    public static void EnsureCanTransition(string current, string next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"Reference materialization chapter cannot transition from '{current}' to '{next}'.");
        }
    }
}
