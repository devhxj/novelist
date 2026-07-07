using Novelist.Contracts.App;

namespace Novelist.Core.App;

public interface INarrativePatternExtractionService
{
    ValueTask<NarrativePatternRunPayload> StartExtractionAsync(
        StartNarrativePatternExtractionPayload input,
        CancellationToken cancellationToken);

    ValueTask<NarrativePatternRunPayload> CancelExtractionAsync(
        CancelNarrativePatternExtractionPayload input,
        CancellationToken cancellationToken);

    ValueTask<NarrativePatternRunPayload?> GetRunAsync(
        GetNarrativePatternRunPayload input,
        CancellationToken cancellationToken);

    ValueTask<NarrativePatternTracePayload?> GetTraceAsync(
        GetNarrativePatternRunPayload input,
        CancellationToken cancellationToken);

    ValueTask<NarrativePatternRunPayload> UpdateRunAsync(
        NarrativePatternRunUpdate update,
        CancellationToken cancellationToken);

    ValueTask<NarrativePatternRunPayload> CompleteRunAsync(
        NarrativePatternRunCompletion completion,
        CancellationToken cancellationToken);

    ValueTask<NarrativePatternRunPayload> FailRunAsync(
        NarrativePatternRunFailure failure,
        CancellationToken cancellationToken);

    ValueTask<NarrativePatternTracePayload> AppendTraceAsync(
        NarrativePatternTraceAppend append,
        CancellationToken cancellationToken);
}

public sealed record NarrativePatternRunUpdate(
    string TaskId,
    string Status,
    string Stage,
    int ProgressCompleted,
    int ProgressTotal,
    string? SkillPreview,
    IReadOnlyList<CopyableDiagnosticPayload> Diagnostics);

public sealed record NarrativePatternRunCompletion(
    string TaskId,
    string Stage,
    string SkillPreview,
    IReadOnlyList<CopyableDiagnosticPayload> Diagnostics);

public sealed record NarrativePatternRunFailure(
    string TaskId,
    string Stage,
    CopyableDiagnosticPayload Error);

public sealed record NarrativePatternTraceAppend(
    string TaskId,
    NarrativePatternTraceEntryPayload Entry);
