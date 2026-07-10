namespace Novelist.Core.App;

public static class ReferenceCorpusAnalysisRunStateMachine
{
    public static ReferenceCorpusAnalysisRunState Start(int? tokenBudget)
    {
        ValidateTokenBudget(tokenBudget, tokensSpent: 0);
        return new ReferenceCorpusAnalysisRunState(
            ReferenceCorpusAnalysisRunStatuses.Running,
            tokenBudget,
            TokensSpent: 0,
            ResumeCursor: null);
    }

    public static ReferenceCorpusAnalysisRunState RecordProgress(
        ReferenceCorpusAnalysisRunState state,
        int additionalTokensSpent,
        string? resumeCursor)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (additionalTokensSpent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(additionalTokensSpent), additionalTokensSpent, "Token delta cannot be negative.");
        }

        EnsureStatusKnown(state.Status);
        EnsureCanMutateActiveRun(state);

        var tokensSpent = checked(state.TokensSpent + additionalTokensSpent);
        ValidateTokenBudget(state.TokenBudget, tokensSpent);
        var status = state.TokenBudget is { } budget && tokensSpent >= budget
            ? ReferenceCorpusAnalysisRunStatuses.BudgetExhausted
            : ReferenceCorpusAnalysisRunStatuses.Running;

        return state with
        {
            Status = status,
            TokensSpent = tokensSpent,
            ResumeCursor = resumeCursor
        };
    }

    public static ReferenceCorpusAnalysisRunState Pause(ReferenceCorpusAnalysisRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureStatusKnown(state.Status);
        EnsureCanMutateActiveRun(state);

        return state with { Status = ReferenceCorpusAnalysisRunStatuses.Paused };
    }

    public static ReferenceCorpusAnalysisRunState MarkPartialCompleted(ReferenceCorpusAnalysisRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureStatusKnown(state.Status);
        EnsureCanMutateActiveRun(state);

        return state with { Status = ReferenceCorpusAnalysisRunStatuses.PartialCompleted };
    }

    public static ReferenceCorpusAnalysisRunState Complete(ReferenceCorpusAnalysisRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureStatusKnown(state.Status);
        EnsureCanMutateActiveRun(state);

        return state with { Status = ReferenceCorpusAnalysisRunStatuses.Completed };
    }

    public static ReferenceCorpusAnalysisRunState Fail(ReferenceCorpusAnalysisRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureStatusKnown(state.Status);

        return state with { Status = ReferenceCorpusAnalysisRunStatuses.Failed };
    }

    public static ReferenceCorpusAnalysisRunState Resume(
        ReferenceCorpusAnalysisRunState state,
        int? newTokenBudget = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureStatusKnown(state.Status);
        if (!CanResume(state))
        {
            throw new InvalidOperationException($"Cannot resume analysis run from status '{state.Status}'.");
        }

        var tokenBudget = newTokenBudget ?? state.TokenBudget;
        ValidateTokenBudget(tokenBudget, state.TokensSpent);
        if (string.Equals(state.Status, ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, StringComparison.Ordinal) &&
            tokenBudget is { } budget &&
            budget <= state.TokensSpent)
        {
            throw new InvalidOperationException("Budget-exhausted analysis runs require a token budget greater than tokens already spent.");
        }

        return state with
        {
            Status = ReferenceCorpusAnalysisRunStatuses.Running,
            TokenBudget = tokenBudget
        };
    }

    public static bool CanResume(ReferenceCorpusAnalysisRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Status is
            ReferenceCorpusAnalysisRunStatuses.Paused or
            ReferenceCorpusAnalysisRunStatuses.BudgetExhausted or
            ReferenceCorpusAnalysisRunStatuses.PartialCompleted;
    }

    public static bool IsTerminal(ReferenceCorpusAnalysisRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Status is
            ReferenceCorpusAnalysisRunStatuses.Completed or
            ReferenceCorpusAnalysisRunStatuses.Failed;
    }

    private static void EnsureCanMutateActiveRun(ReferenceCorpusAnalysisRunState state)
    {
        if (IsTerminal(state))
        {
            throw new InvalidOperationException($"Cannot mutate terminal analysis run status '{state.Status}'.");
        }
    }

    private static void EnsureStatusKnown(string status)
    {
        if (!ReferenceCorpusAnalysisRunStatuses.All.Contains(status, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown reference corpus analysis run status.");
        }
    }

    private static void ValidateTokenBudget(int? tokenBudget, int tokensSpent)
    {
        if (tokenBudget < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), tokenBudget, "Token budget cannot be negative.");
        }

        if (tokensSpent < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokensSpent), tokensSpent, "Tokens spent cannot be negative.");
        }
    }
}

public sealed record ReferenceCorpusAnalysisRunState(
    string Status,
    int? TokenBudget,
    int TokensSpent,
    string? ResumeCursor);

public static class ReferenceCorpusAnalysisRunStatuses
{
    public const string Running = "running";
    public const string Paused = "paused";
    public const string BudgetExhausted = "budget_exhausted";
    public const string PartialCompleted = "partial_completed";
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static IReadOnlyList<string> All { get; } =
    [
        Running,
        Paused,
        BudgetExhausted,
        PartialCompleted,
        Completed,
        Failed
    ];
}
