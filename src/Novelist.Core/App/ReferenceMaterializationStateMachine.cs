using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class ReferenceMaterializationRunStateMachine
{
    public static bool CanTransition(string current, string next)
    {
        return current switch
        {
            ReferenceMaterializationRunStates.Queued =>
                next is ReferenceMaterializationRunStates.Extracting or
                ReferenceMaterializationRunStates.Failed,
            ReferenceMaterializationRunStates.Extracting =>
                next is ReferenceMaterializationRunStates.Embedding or
                ReferenceMaterializationRunStates.Failed,
            ReferenceMaterializationRunStates.Embedding =>
                next is ReferenceMaterializationRunStates.Indexing or
                ReferenceMaterializationRunStates.Failed,
            ReferenceMaterializationRunStates.Indexing =>
                next is ReferenceMaterializationRunStates.Extracting or
                ReferenceMaterializationRunStates.Paused or
                ReferenceMaterializationRunStates.Completed or
                ReferenceMaterializationRunStates.Failed,
            ReferenceMaterializationRunStates.Paused =>
                next is ReferenceMaterializationRunStates.Queued,
            ReferenceMaterializationRunStates.Failed =>
                next is ReferenceMaterializationRunStates.Queued,
            ReferenceMaterializationRunStates.Completed =>
                next is ReferenceMaterializationRunStates.Queued,
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
                next is ReferenceMaterializationChapterStates.Extracting or
                ReferenceMaterializationChapterStates.Failed,
            ReferenceMaterializationChapterStates.Extracting =>
                next is ReferenceMaterializationChapterStates.Embedding or
                ReferenceMaterializationChapterStates.Failed,
            ReferenceMaterializationChapterStates.Embedding =>
                next is ReferenceMaterializationChapterStates.Completed or
                ReferenceMaterializationChapterStates.Failed,
            ReferenceMaterializationChapterStates.Failed =>
                next is ReferenceMaterializationChapterStates.Pending,
            ReferenceMaterializationChapterStates.Completed =>
                next is ReferenceMaterializationChapterStates.Pending,
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
