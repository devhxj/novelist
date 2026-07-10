using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceOrchestrationStateMachineTests
{
    [Theory]
 [InlineData(ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts, ReferenceOrchestrationStages.SourceConfirmation, ReferenceOrchestrationStages.GoalParsing)]
    [InlineData(ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision, ReferenceOrchestrationStages.BlueprintReview, ReferenceOrchestrationStages.BlueprintReview)]
    [InlineData(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, ReferenceOrchestrationStages.BlueprintApproval, ReferenceOrchestrationStages.MaterialBinding)]
    public void ResumeAfterDecisionMovesApprovedHumanDecisionsIntoRunningSafeStage(
        string decisionType,
        string currentStage,
        string expectedStage)
    {
        var updatedAt = DateTimeOffset.Parse("2026-07-06T12:00:00Z");
        var resumed = ReferenceOrchestrationStateMachine.ResumeAfterDecision(
            BuildRun(currentStage, BuildDecision(decisionType)),
            decisionType,
            updatedAt);

        Assert.Equal(ReferenceOrchestrationRunStatuses.Running, resumed.Status);
        Assert.Equal(expectedStage, resumed.Stage);
        Assert.Null(resumed.CurrentDecision);
        Assert.Equal(string.Empty, resumed.LastStopReason);
        Assert.Equal(string.Empty, resumed.ErrorMessage);
        Assert.Equal(updatedAt, resumed.UpdatedAt);
    }

    [Fact]
    public void ResumeAfterDecisionTurnsHighRiskResolutionIntoTerminalFailure()
    {
        var updatedAt = DateTimeOffset.Parse("2026-07-06T13:00:00Z");
        var run = BuildRun(
            ReferenceOrchestrationStages.DraftAudit,
            BuildDecision(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop)) with
        {
            LastStopReason = ReferenceOrchestrationStopReasons.DraftAuditFailed,
            ErrorMessage = "unsupported_fact: forbidden reveal"
        };

        var resumed = ReferenceOrchestrationStateMachine.ResumeAfterDecision(
            run,
            ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop,
            updatedAt);

        Assert.Equal(ReferenceOrchestrationRunStatuses.Failed, resumed.Status);
        Assert.Equal(ReferenceOrchestrationStages.DraftAudit, resumed.Stage);
        Assert.Null(resumed.CurrentDecision);
        Assert.Equal(ReferenceOrchestrationStopReasons.DraftAuditFailed, resumed.LastStopReason);
        Assert.Equal("unsupported_fact: forbidden reveal", resumed.ErrorMessage);
        Assert.Equal(updatedAt, resumed.UpdatedAt);
    }

 [Theory]
 [InlineData(ReferenceOrchestrationStages.BlueprintGeneration, true)]
 [InlineData(ReferenceOrchestrationStages.GoalParsing, true)]
 [InlineData(ReferenceOrchestrationStages.CorpusRetrieval, true)]
 [InlineData(ReferenceOrchestrationStages.BlueprintAssembly, true)]
 [InlineData(ReferenceOrchestrationStages.MaterialBinding, true)]
    [InlineData(ReferenceOrchestrationStages.BlueprintReview, false)]
    [InlineData(ReferenceOrchestrationStages.BlueprintApproval, false)]
    [InlineData(ReferenceOrchestrationStages.DraftGeneration, false)]
    [InlineData(ReferenceOrchestrationStages.DraftAudit, false)]
    [InlineData(ReferenceOrchestrationStages.FinalInsertion, false)]
    public void ShouldRunSafeStagesOnlyAllowsRunningAutomaticStages(string stage, bool expected)
    {
        var run = BuildRun(stage, currentDecision: null) with
        {
            Status = ReferenceOrchestrationRunStatuses.Running
        };

        Assert.Equal(expected, ReferenceOrchestrationStateMachine.ShouldRunSafeStages(run));
    }

    [Fact]
 public void ShouldRunSafeStagesDoesNotRunWhenUserDecisionIsPending()
    {
        var run = BuildRun(
            ReferenceOrchestrationStages.BlueprintGeneration,
            BuildDecision(ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts)) with
        {
            Status = ReferenceOrchestrationRunStatuses.Running
        };

 Assert.False(ReferenceOrchestrationStateMachine.ShouldRunSafeStages(run));
 }

 [Fact]
 public void LegacyBlueprintGenerationStageRemainsReadableAndNormalizesOnlyForExecution()
 {
 var run = BuildRun(ReferenceOrchestrationStages.BlueprintGeneration, currentDecision: null) with
 {
 Status = ReferenceOrchestrationRunStatuses.Running
 };

 Assert.Contains(ReferenceOrchestrationStages.BlueprintGeneration, ReferenceOrchestrationStages.All);
 Assert.Contains(ReferenceOrchestrationStages.GoalParsing, ReferenceOrchestrationStages.All);
 Assert.True(ReferenceOrchestrationStages.IsLegacyStage(run.Stage));
 Assert.Equal(ReferenceOrchestrationStages.GoalParsing, ReferenceOrchestrationStages.NormalizeForExecution(run.Stage));
 Assert.Equal(ReferenceOrchestrationStages.BlueprintGeneration, run.Stage);
 Assert.True(ReferenceOrchestrationStateMachine.ShouldRunSafeStages(run));
 }

    private static ReferenceOrchestrationRunPayload BuildRun(
        string stage,
        ReferenceOrchestrationRequiredDecisionPayload? currentDecision)
    {
        var now = DateTimeOffset.Parse("2026-07-06T10:00:00Z");
        return new ReferenceOrchestrationRunPayload(
            "run-state-machine",
            NovelId: 42,
            ChapterNumber: 7,
            ReferenceOrchestrationRunStatuses.WaitingForUser,
            stage,
            ChapterGoal: "keep the reveal bounded",
            KnownFacts: ["known clue"],
            ForbiddenFacts: ["forbidden reveal"],
            AnchorIds: [],
            new ReferenceCorpusSearchPolicyPayload(
                "story_context",
                MaxResultsPerBeat: 3,
                LicenseStatuses: ["user_provided"],
                IncludeAnchorIds: [],
                ExcludeAnchorIds: []),
            BlueprintId: 100,
            ReviewId: "review-1",
            CandidateIds: [],
            currentDecision,
            ReferenceOrchestrationStopReasons.BlueprintApprovalRequired,
            ErrorMessage: "previous stop",
            now,
            now);
    }

    private static ReferenceOrchestrationRequiredDecisionPayload BuildDecision(string decisionType)
    {
        return new ReferenceOrchestrationRequiredDecisionPayload(
            decisionType,
            ReferenceOrchestrationStopReasons.BlueprintApprovalRequired,
            "summary",
            ["inspect"],
            new ReferenceOrchestrationApprovalSummaryPayload(
                "function",
                "pov",
                [],
                "emotion",
                "material plan",
                "L2",
                []));
    }
}
