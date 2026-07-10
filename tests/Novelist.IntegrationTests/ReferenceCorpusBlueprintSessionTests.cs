using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusBlueprintSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-blueprint-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BlueprintSessionPersistsGoalIterationAndAcceptedSelectionAcrossCoordinatorInstances()
    {
        var options = new AppInitializationOptions
        {
            DefaultDataDirectory = _root,
            ConfigDirectory = Path.Combine(_root, "config")
        };
        await new FileSystemAppInitializationService(options)
            .InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
        var writing = new SessionWritingService();
        var coordinator = new SqliteReferenceCorpusBlueprintIterationCoordinator(writing, options);
        var generation = CreateGenerationInput("让门外的压力持续升级");
        const string sessionId = "chapter:42:3";

        var generated = await coordinator.AdvanceAsync(
            new AdvanceReferenceCorpusBlueprintSessionPayload(
                sessionId,
                "generate-1",
                ReferenceCorpusBlueprintSessionActions.Generate,
                generation),
            CancellationToken.None);

        Assert.Equal("让门外的压力持续升级", generated.NaturalLanguageGoal);
        Assert.Equal(1, generated.Iteration);
        Assert.Equal(ReferenceCorpusBlueprintSessionStatuses.AwaitingFeedback, generated.Status);

        var selected = await coordinator.AdvanceAsync(
            new AdvanceReferenceCorpusBlueprintSessionPayload(
                sessionId,
                "select-1",
                ReferenceCorpusBlueprintSessionActions.Select,
                SelectedBlueprintId: generated.Candidates.Candidates[0].Blueprint.BlueprintId),
            CancellationToken.None);

        Assert.Equal(generated.Candidates.Candidates[0].Blueprint.BlueprintId, selected.SelectedBlueprintId);

        var revised = await coordinator.AdvanceAsync(
            new AdvanceReferenceCorpusBlueprintSessionPayload(
                sessionId,
                "revise-1",
                ReferenceCorpusBlueprintSessionActions.Revise,
                generation,
                selected.SelectedBlueprintId,
                CreateChecklist(ReferenceCorpusBlueprintChecklistDecisions.Revise)),
            CancellationToken.None);

        Assert.Equal("让门外的压力持续升级", revised.NaturalLanguageGoal);
        Assert.Equal(2, revised.Iteration);
        Assert.True(revised.Candidates.FeedbackApplied);
        Assert.Equal(string.Empty, revised.SelectedBlueprintId);
        Assert.Contains(selected.SelectedBlueprintId, revised.Candidates.Iteration!.RejectedBlueprintIds);

        var selectedRevision = await coordinator.AdvanceAsync(
            new AdvanceReferenceCorpusBlueprintSessionPayload(
                sessionId,
                "select-2",
                ReferenceCorpusBlueprintSessionActions.Select,
                SelectedBlueprintId: revised.Candidates.Candidates[0].Blueprint.BlueprintId),
            CancellationToken.None);

        var accepted = await coordinator.AdvanceAsync(
            new AdvanceReferenceCorpusBlueprintSessionPayload(
                sessionId,
                "accept-1",
                ReferenceCorpusBlueprintSessionActions.Accept,
                SelectedBlueprintId: selectedRevision.SelectedBlueprintId,
                Checklist: CreateChecklist(ReferenceCorpusBlueprintChecklistDecisions.Accepted)),
            CancellationToken.None);

        Assert.Equal(ReferenceCorpusBlueprintSessionStatuses.Accepted, accepted.Status);
        Assert.Equal(revised.Candidates.Candidates[0].Blueprint.BlueprintId, accepted.AcceptedBlueprintId);

        var restored = await new SqliteReferenceCorpusBlueprintIterationCoordinator(
            new SessionWritingService(),
            options).GetAsync(
                new GetReferenceCorpusBlueprintSessionPayload(42, 3, sessionId),
                CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal("让门外的压力持续升级", restored!.NaturalLanguageGoal);
        Assert.Equal(2, restored.Iteration);
        Assert.Equal(ReferenceCorpusBlueprintSessionStatuses.Accepted, restored.Status);
        Assert.Equal(accepted.AcceptedBlueprintId, restored.AcceptedBlueprintId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static GenerateReferenceCorpusBlueprintCandidatesPayload CreateGenerationInput(string goal) =>
        new(
            goal,
            new CurrentChapterContextPayload(42, 3, "林岚停在门前。", 7, null, []),
            new ReferenceCorpusScopePayload([], ["verbatim_ok"], [], [], "project:42:default"),
            3);

    private static IReadOnlyList<ReferenceCorpusBlueprintChecklistItemPayload> CreateChecklist(string decision) =>
        ReferenceCorpusBlueprintChecklistDimensions.All
            .Select(dimension => new ReferenceCorpusBlueprintChecklistItemPayload(
                dimension,
                dimension == ReferenceCorpusBlueprintChecklistDimensions.SourceDistribution
                    ? decision
                    : ReferenceCorpusBlueprintChecklistDecisions.Accepted,
                dimension == ReferenceCorpusBlueprintChecklistDimensions.SourceDistribution &&
                decision == ReferenceCorpusBlueprintChecklistDecisions.Revise
                    ? ["source_repetition"]
                    : [],
                null))
            .ToArray();

    private sealed class SessionWritingService : IReferenceCorpusWritingService
    {
        private int _generationCount;

        public ValueTask<ReferenceCorpusBlueprintCandidatePayload> GenerateChapterBlueprintAsync(
            GenerateReferenceCorpusBlueprintCandidatesPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferenceCorpusBlueprintCandidatesPayload> GenerateBlueprintCandidatesAsync(
            GenerateReferenceCorpusBlueprintCandidatesPayload input,
            CancellationToken cancellationToken)
        {
            var generation = Interlocked.Increment(ref _generationCount);
            var blueprint = new ReferenceCorpusInsertionBlueprintPayload(
                $"blueprint-{generation}",
                $"query-{generation}",
                "session-test",
                [new ReferenceCorpusInsertionBlueprintBeatPayload(
                    $"beat-{generation}",
                    0,
                    "pressure",
                    "raise_pressure",
                    [$"node-{generation}"])]);
            var candidate = new ReferenceCorpusBlueprintCandidatePayload(
                blueprint,
                [new ReferenceCorpusBlueprintSourcePayload("library-1", 101, 1)],
                0.9,
                [],
                input.Feedback is null ? "initial_candidate" : "feedback_applied");
            var queryContext = new ReferenceCorpusQueryContextPayload(
                "doorway_confrontation",
                "restrained_pressure",
                "slow_tension",
                "pre_reveal",
                "withheld_answer",
                [],
                ["raise_pressure"],
                input.ChapterContext,
                input.Scope);
            return ValueTask.FromResult(new ReferenceCorpusBlueprintCandidatesPayload(
                queryContext,
                [candidate],
                input.Feedback is not null,
                input.Feedback is null ? "none" : "feedback_applied"));
        }

        public ValueTask<ReferenceCorpusInsertionDraftPayload> GenerateInsertionDraftAsync(
            GenerateReferenceCorpusInsertionDraftPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ReferenceCorpusInsertionDraftCandidatesPayload> GenerateInsertionDraftCandidatesAsync(
            GenerateReferenceCorpusInsertionDraftCandidatesPayload input,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
