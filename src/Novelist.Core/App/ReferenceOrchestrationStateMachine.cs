using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class ReferenceOrchestrationStateMachine
{
    public static ReferenceOrchestrationRunPayload ResumeAfterDecision(
        ReferenceOrchestrationRunPayload run,
        string decisionType,
        DateTimeOffset updatedAt)
    {
        if (string.Equals(decisionType, ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, StringComparison.Ordinal))
        {
            return run with
            {
                Status = ReferenceOrchestrationRunStatuses.Failed,
                Stage = run.Stage,
                CurrentDecision = null,
                LastStopReason = run.LastStopReason,
                ErrorMessage = run.ErrorMessage,
                UpdatedAt = updatedAt
            };
        }

        return run with
        {
            Status = ReferenceOrchestrationRunStatuses.Running,
            Stage = NextStageAfterDecision(decisionType, run.Stage),
            CurrentDecision = null,
            LastStopReason = string.Empty,
            ErrorMessage = string.Empty,
            UpdatedAt = updatedAt
        };
    }

    public static string NextStageAfterDecision(string decisionType, string currentStage)
    {
        return decisionType switch
        {
 ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts => ReferenceOrchestrationStages.GoalParsing,
            ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision => ReferenceOrchestrationStages.BlueprintReview,
            ReferenceOrchestrationDecisionTypes.ApproveBlueprint => ReferenceOrchestrationStages.MaterialBinding,
            ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop => currentStage,
            ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion => ReferenceOrchestrationStages.FinalInsertion,
            _ => ReferenceOrchestrationStages.SourceConfirmation
        };
    }

    public static bool ShouldRunSafeStages(ReferenceOrchestrationRunPayload run)
    {
        return string.Equals(run.Status, ReferenceOrchestrationRunStatuses.Running, StringComparison.Ordinal) &&
            run.CurrentDecision is null &&
 (string.Equals(ReferenceOrchestrationStages.NormalizeForExecution(run.Stage), ReferenceOrchestrationStages.GoalParsing, StringComparison.Ordinal) ||
 string.Equals(run.Stage, ReferenceOrchestrationStages.CorpusRetrieval, StringComparison.Ordinal) ||
 string.Equals(run.Stage, ReferenceOrchestrationStages.BlueprintAssembly, StringComparison.Ordinal) ||
string.Equals(run.Stage, ReferenceOrchestrationStages.MaterialBinding, StringComparison.Ordinal));
    }
}
