using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceAnchoredDraftService : IReferenceAnchoredDraftService
{
    private const string BuildVersion = "reference-blueprint-v1";
    private const string ProseLikePlanNotice = "provided source text appears to be final prose; convert it into structured causality, emotion, POV, and prose duties before drafting";
    private static readonly JsonSerializerOptions JsonOptions = BridgeJson.SerializerOptions;

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly IPlanningService _planning;
    private readonly IReferenceAnchorService? _referenceAnchors;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceAnchoredDraftService(
        AppInitializationOptions? options = null,
        INovelService? novels = null,
        IPlanningService? planning = null,
        IReferenceAnchorService? referenceAnchors = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
        _planning = planning ?? new FileSystemPlanningService(_options, _novels);
        _referenceAnchors = referenceAnchors;
    }

    public async ValueTask<ReferenceChapterBlueprintPayload> GenerateChapterBlueprintAsync(
        GenerateReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateChapterNumber(input.ChapterNumber);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var plans = await _planning.GetChapterPlansAsync(input.NovelId, cancellationToken);
        var sourcePlan = plans.FirstOrDefault(plan => string.Equals(plan.Scope, "next", StringComparison.Ordinal))
            ?? plans.FirstOrDefault()
            ?? new ChapterPlanPayload(input.NovelId, "next", string.Empty);
        var planText = string.IsNullOrWhiteSpace(sourcePlan.Content) ? input.ChapterGoal ?? string.Empty : sourcePlan.Content;
        var sourcePlanHash = ReferenceChapterBlueprintNormalizer.ComputeSourcePlanHash(sourcePlan.Scope, sourcePlan.Content);
        var knownFacts = NormalizeList(input.KnownFacts);
        var forbiddenFacts = NormalizeList(input.ForbiddenFacts);
        var anchorIds = NormalizeAnchorIds(input.AnchorIds);
        var contextHash = ReferenceChapterBlueprintNormalizer.ComputeContextHash(
            new ReferenceChapterBlueprintContextPack(
                input.NovelId,
                input.ChapterNumber,
                sourcePlan.Scope,
                sourcePlan.Content,
                input.ChapterGoal,
                anchorIds,
                knownFacts,
                forbiddenFacts));
        var now = DateTimeOffset.UtcNow;
        var title = NormalizeOptional(input.Title, "Chapter " + input.ChapterNumber.ToString(CultureInfo.InvariantCulture), 200);
        var chapterFunction = NormalizeBlueprintInstruction(input.ChapterGoal, "establish a reviewable chapter blueprint before prose generation", 2_000);
        var primaryAnchorId = anchorIds.FirstOrDefault();
        var blueprint = BuildDeterministicBlueprint(
            0,
            input.NovelId,
            input.ChapterNumber,
            title,
            sourcePlan.Scope,
            sourcePlanHash,
            contextHash,
            primaryAnchorId,
            chapterFunction,
            planText,
            knownFacts,
            forbiddenFacts,
            now);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var blueprintId = await InsertBlueprintAsync(connection, transaction, blueprint, cancellationToken);
            var persisted = blueprint with
            {
                BlueprintId = blueprintId,
                Beats = blueprint.Beats
                    .Select(beat => beat with { BeatId = BuildBeatId(blueprintId, beat.BeatIndex) })
                    .ToArray()
            };
            await ReplaceBeatsAsync(connection, transaction, persisted.BlueprintId, persisted.Beats, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return persisted;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReferenceChapterBlueprintSummaryPayload>> GetChapterBlueprintsAsync(
        long novelId,
        int? chapterNumber,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        if (chapterNumber is not null)
        {
            ValidateChapterNumber(chapterNumber.Value);
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = chapterNumber is null
                ? """
                  SELECT blueprint_id, novel_id, chapter_number, title, status, source_plan_scope, source_plan_hash, updated_at
                  FROM reference_chapter_blueprints
                  WHERE novel_id = $novel_id
                  ORDER BY chapter_number ASC, updated_at DESC, blueprint_id DESC;
                  """
                : """
                  SELECT blueprint_id, novel_id, chapter_number, title, status, source_plan_scope, source_plan_hash, updated_at
                  FROM reference_chapter_blueprints
                  WHERE novel_id = $novel_id AND chapter_number = $chapter_number
                  ORDER BY updated_at DESC, blueprint_id DESC;
                  """;
            command.Parameters.AddWithValue("$novel_id", novelId);
            if (chapterNumber is not null)
            {
                command.Parameters.AddWithValue("$chapter_number", chapterNumber.Value);
            }

            var rows = new List<BlueprintSummaryRow>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add(new BlueprintSummaryRow(
                        new ReferenceChapterBlueprintSummaryPayload(
                            reader.GetInt64(0),
                            reader.GetInt64(1),
                            reader.GetInt32(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(6),
                            ParseTimestamp(reader.GetString(7))),
                        reader.GetString(5)));
                }
            }

            var items = new List<ReferenceChapterBlueprintSummaryPayload>();
            foreach (var row in rows)
            {
                items.Add(await ApplySummaryStalenessAsync(
                    connection,
                    row.Summary,
                    row.SourcePlanScope,
                    cancellationToken));
            }

            return items;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceChapterBlueprintPayload?> GetChapterBlueprintAsync(
        long novelId,
        long blueprintId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ValidateBlueprintId(blueprintId);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            return await ReadBlueprintAsync(connection, novelId, blueprintId, cancellationToken, required: false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceChapterBlueprintReviewPayload> ReviewChapterBlueprintAsync(
        ReviewReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateBlueprintId(input.BlueprintId);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var blueprint = await ReadBlueprintAsync(connection, input.NovelId, input.BlueprintId, cancellationToken, required: true)
                ?? throw new InvalidOperationException("Required blueprint was not loaded.");
            if (string.Equals(blueprint.Status, ReferenceBlueprintStates.Stale, StringComparison.Ordinal))
            {
                throw new ArgumentException("Stale blueprint must be regenerated before review.", nameof(input));
            }

            if (blueprint.LatestReview is not null &&
                ReferenceAnchoredDraftPreflight.ReviewMatchesBlueprint(blueprint, blueprint.LatestReview))
            {
                return blueprint.LatestReview;
            }

            var review = ReferenceChapterBlueprintReviewer.BuildReview(blueprint, DateTimeOffset.UtcNow);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await InsertReviewAsync(connection, transaction, review, cancellationToken);
            await UpdateBlueprintStatusAsync(
                connection,
                transaction,
                blueprint.BlueprintId,
                review.Status == ReferenceBlueprintReviewStatuses.Passed
                    ? ReferenceBlueprintStates.ReviewPassed
                    : ReferenceBlueprintStates.ReviewFailed,
                approvedReviewId: string.Empty,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return review;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceChapterBlueprintPayload> ReviseChapterBlueprintAsync(
        ReviseReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateBlueprintId(input.BlueprintId);
        if (input.Changes.Count == 0)
        {
            throw new ArgumentException("At least one blueprint revision change is required.", nameof(input));
        }

        var origin = NormalizeOptional(input.Origin, "user", 80);
        var reason = NormalizeOptional(input.RevisionReason, "blueprint revision", 500);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var blueprint = await ReadBlueprintAsync(connection, input.NovelId, input.BlueprintId, cancellationToken, required: true)
                ?? throw new InvalidOperationException("Required blueprint was not loaded.");
            if (string.Equals(blueprint.Status, ReferenceBlueprintStates.Stale, StringComparison.Ordinal))
            {
                throw new ArgumentException("Stale blueprint must be regenerated before revision.", nameof(input));
            }

            var changedBeats = blueprint.Beats.ToDictionary(beat => beat.BeatId, StringComparer.Ordinal);
            var revisionRows = new List<BlueprintRevisionRow>();
            var revised = blueprint;
            foreach (var change in input.Changes)
            {
                revised = ApplyRevisionChange(revised, changedBeats, change, revisionRows, origin, reason, blueprint.LatestReview?.ReviewId);
            }

            revised = revised with
            {
                Status = ReferenceBlueprintStates.Draft,
                Beats = revised.Beats.Select(beat => changedBeats[beat.BeatId]).ToArray(),
                LatestReview = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            revised = revised with
            {
                AnalysisContractHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(revised)
            };

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await UpdateBlueprintAfterRevisionAsync(connection, transaction, revised, cancellationToken);
            await ReplaceBeatsAsync(connection, transaction, revised.BlueprintId, revised.Beats, cancellationToken);
            await InsertRevisionRowsAsync(connection, transaction, revised.BlueprintId, revisionRows, cancellationToken);
            await MarkBlueprintMaterialLinksStaleAsync(connection, transaction, revised.BlueprintId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await ReadBlueprintAsync(connection, input.NovelId, input.BlueprintId, cancellationToken, required: true)
                ?? throw new InvalidOperationException("Revised blueprint could not be loaded.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceChapterBlueprintPayload> ApproveChapterBlueprintAsync(
        ApproveReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateBlueprintId(input.BlueprintId);
        if (string.IsNullOrWhiteSpace(input.ReviewId))
        {
            throw new ArgumentException("Review id is required.", nameof(input));
        }

        var approverOrigin = NormalizeApproverOrigin(input.ApproverOrigin);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var blueprint = await ReadBlueprintAsync(connection, input.NovelId, input.BlueprintId, cancellationToken, required: true)
                ?? throw new InvalidOperationException("Required blueprint was not loaded.");
            if (string.Equals(blueprint.Status, ReferenceBlueprintStates.Stale, StringComparison.Ordinal))
            {
                throw new ArgumentException("Stale blueprint must be regenerated before approval.", nameof(input));
            }

            var review = await ReadReviewAsync(connection, input.BlueprintId, input.ReviewId, cancellationToken)
                ?? throw new ArgumentException("Review does not exist for this blueprint.", nameof(input));
            if (!string.Equals(review.Status, ReferenceBlueprintReviewStatuses.Passed, StringComparison.Ordinal))
            {
                throw new ArgumentException("Blueprint approval requires a passing review.", nameof(input));
            }

            if (!ReferenceAnchoredDraftPreflight.ReviewMatchesBlueprint(blueprint, review))
            {
                throw new ArgumentException("Blueprint approval requires a current passing review for this exact blueprint contract.", nameof(input));
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await InsertBlueprintApprovalAsync(
                connection,
                transaction,
                input.BlueprintId,
                review,
                approverOrigin,
                DateTimeOffset.UtcNow,
                cancellationToken);
            await UpdateBlueprintStatusAsync(connection, transaction, input.BlueprintId, ReferenceBlueprintStates.Approved, review.ReviewId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await ReadBlueprintAsync(connection, input.NovelId, input.BlueprintId, cancellationToken, required: true)
                ?? throw new InvalidOperationException("Approved blueprint could not be loaded.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceBlueprintMaterialBindingResultPayload> BindBlueprintMaterialsAsync(
        BindReferenceBlueprintMaterialsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNovelId(input.NovelId);
        ValidateBlueprintId(input.BlueprintId);
        var referenceAnchors = _referenceAnchors
            ?? throw new ArgumentException("Reference material binding requires a configured reference anchor service.", nameof(input));
        var maxResultsPerBeat = Math.Clamp(input.MaxResultsPerBeat <= 0 ? 3 : input.MaxResultsPerBeat, 1, 20);
        var blueprint = await GetChapterBlueprintAsync(input.NovelId, input.BlueprintId, cancellationToken)
            ?? throw new ArgumentException("Blueprint does not exist.", nameof(input));
        if (string.Equals(blueprint.Status, ReferenceBlueprintStates.Stale, StringComparison.Ordinal))
        {
            throw new ArgumentException("Stale blueprint must be regenerated before material binding.", nameof(input));
        }

        if (!string.Equals(blueprint.Status, ReferenceBlueprintStates.Approved, StringComparison.Ordinal))
        {
            throw new ArgumentException("Material binding requires an approved blueprint.", nameof(input));
        }

        ReferenceAnchoredDraftPreflight.EnsureCurrentPassingReview(blueprint, "Material binding");

        var now = DateTimeOffset.UtcNow;
        var acceptedFeedbackMaterialIds = await LoadAcceptedFeedbackMaterialIdsAsync(
            referenceAnchors,
            input.NovelId,
            cancellationToken);
        var boundLinks = new List<ScoredMaterialLink>();
        foreach (var beat in blueprint.Beats.OrderBy(item => item.BeatIndex))
        {
            if (!string.IsNullOrWhiteSpace(beat.NoReuseReason))
            {
                continue;
            }

            var materials = await SearchMaterialsForBeatAsync(
                referenceAnchors,
                input.NovelId,
                blueprint.PrimaryAnchorId,
                beat,
                maxResultsPerBeat,
                cancellationToken);
            var scored = materials
                .Select(material => ScoreMaterialForBeat(blueprint.BlueprintId, blueprint.AnalysisContractHash, beat, material, acceptedFeedbackMaterialIds, now))
                .Where(item => item.Score > 0 && item.HasFunctionalFit)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Link.MaterialId, StringComparer.Ordinal)
                .Take(maxResultsPerBeat)
                .ToArray();

            for (var index = 0; index < scored.Length; index++)
            {
                boundLinks.Add(scored[index] with { Link = scored[index].Link with { Selected = input.SelectTopCandidate && index == 0 } });
            }
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await ReplaceBlueprintMaterialLinksAsync(connection, transaction, blueprint.BlueprintId, boundLinks, cancellationToken);
            await UpdateBlueprintStatusAsync(
                connection,
                transaction,
                blueprint.BlueprintId,
                boundLinks.Any(item => item.Link.Selected)
                    ? ReferenceBlueprintStates.MaterialBound
                    : ReferenceBlueprintStates.Approved,
                blueprint.LatestReview?.ReviewId ?? string.Empty,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        return new ReferenceBlueprintMaterialBindingResultPayload(
            blueprint.BlueprintId,
            boundLinks.Select(item => item.Link).ToArray());
    }

    public async ValueTask<ReferenceAnchoredDraftPayload> GenerateDraftFromBlueprintAsync(
        GenerateReferenceAnchoredDraftPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNovelId(input.NovelId);
        ValidateBlueprintId(input.BlueprintId);
        var blueprint = await GetChapterBlueprintAsync(input.NovelId, input.BlueprintId, cancellationToken)
            ?? throw new ArgumentException("Blueprint does not exist.", nameof(input));
        ReferenceAnchoredDraftPreflight.EnsureDraftGenerationAllowed(blueprint);
        var targetBeats = ReferenceAnchoredDraftPreflight.SelectTargetBeats(blueprint, input.BeatIds);
        var selectedLinks = await EnsureSelectedMaterialLinksAsync(input.NovelId, blueprint, targetBeats, cancellationToken);
        var candidates = await GenerateBeatCandidatesAsync(
            input.NovelId,
            blueprint,
            targetBeats,
            selectedLinks,
            cancellationToken);
        ReferenceAnchoredDraftPreflight.EnsureCandidateProvenance(targetBeats, selectedLinks, candidates);
        var audit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(blueprint, candidates, DateTimeOffset.UtcNow);
        await PersistDraftCandidatesAsync(blueprint.BlueprintId, candidates, cancellationToken);

        return new ReferenceAnchoredDraftPayload(blueprint.BlueprintId, candidates, audit);
    }

    public async ValueTask<ReferenceAnchoredDraftAuditPayload> AuditDraftAgainstBlueprintAsync(
        AuditReferenceAnchoredDraftPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateNovelId(input.NovelId);
        ValidateBlueprintId(input.BlueprintId);
        var blueprint = await GetChapterBlueprintAsync(input.NovelId, input.BlueprintId, cancellationToken)
            ?? throw new ArgumentException("Blueprint does not exist.", nameof(input));
        ReferenceAnchoredDraftPreflight.EnsureCurrentPassingReview(blueprint, "Reference-anchored draft audit");

        var candidateIds = NormalizeList(input.CandidateIds);
        if (candidateIds.Count == 0)
        {
            throw new ArgumentException("Draft audit requires at least one candidate id.", nameof(input));
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var candidates = await ReadDraftCandidatesAsync(
                connection,
                input.BlueprintId,
                candidateIds,
                cancellationToken);
            var missing = candidateIds
                .Where(id => candidates.All(candidate => !string.Equals(candidate.CandidateId, id, StringComparison.Ordinal)))
                .ToArray();
            if (missing.Length > 0)
            {
                var provenanceAudit = ReferenceAnchoredDraftAuditor.BuildDraftAudit(blueprint, candidates, DateTimeOffset.UtcNow);
                return provenanceAudit with
                {
                    Status = "failed",
                    ProvenanceErrors = provenanceAudit.ProvenanceErrors
                        .Concat(missing.Select(id => $"Draft candidate does not exist for this blueprint: {id}"))
                        .ToArray()
                };
            }

            return ReferenceAnchoredDraftAuditor.BuildDraftAudit(blueprint, candidates, DateTimeOffset.UtcNow);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceOrchestrationRunPayload> StartOrchestrationRunAsync(
        StartReferenceOrchestrationRunPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateChapterNumber(input.ChapterNumber);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var chapterGoal = NormalizeOptional(input.ChapterGoal, string.Empty, 2_000);
        var knownFacts = NormalizeList(input.KnownFacts);
        var forbiddenFacts = NormalizeList(input.ForbiddenFacts);
        var anchorIds = NormalizeAnchorIds(input.AnchorIds);
        var policy = NormalizeCorpusSearchPolicy(input.CorpusSearchPolicy);
        var status = input.SourceConfirmed
            ? ReferenceOrchestrationRunStatuses.Running
            : ReferenceOrchestrationRunStatuses.WaitingForUser;
        var stage = input.SourceConfirmed
            ? ReferenceOrchestrationStages.BlueprintGeneration
            : ReferenceOrchestrationStages.SourceConfirmation;
        var currentDecision = input.SourceConfirmed
            ? null
            : BuildSourceConfirmationDecision(chapterGoal, knownFacts, forbiddenFacts, policy);
        var run = new ReferenceOrchestrationRunPayload(
            "run-" + Guid.NewGuid().ToString("N"),
            input.NovelId,
            input.ChapterNumber,
            status,
            stage,
            chapterGoal,
            knownFacts,
            forbiddenFacts,
            anchorIds,
            policy,
            BlueprintId: 0,
            ReviewId: string.Empty,
            CandidateIds: [],
            currentDecision,
            input.SourceConfirmed ? string.Empty : ReferenceOrchestrationStopReasons.SourceConfirmationRequired,
            ErrorMessage: string.Empty,
            now,
            now);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await InsertOrchestrationRunAsync(connection, run, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        return ShouldRunOrchestrationSafeStages(run)
            ? await AdvanceOrchestrationSafeStagesAsync(run, cancellationToken)
            : run;
    }

    public async ValueTask<IReadOnlyList<ReferenceOrchestrationRunPayload>> GetOrchestrationRunsAsync(
        long novelId,
        int? chapterNumber,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        if (chapterNumber is not null)
        {
            ValidateChapterNumber(chapterNumber.Value);
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            return await ReadOrchestrationRunsAsync(connection, novelId, chapterNumber, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceOrchestrationRunPayload?> GetOrchestrationRunAsync(
        long novelId,
        string runId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var normalizedRunId = NormalizeRunId(runId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            return await ReadOrchestrationRunAsync(connection, novelId, normalizedRunId, cancellationToken, required: false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceOrchestrationRunPayload> ResumeOrchestrationRunAsync(
        ResumeReferenceOrchestrationRunPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var runId = NormalizeRunId(input.RunId);
        var decisionType = NormalizeOrchestrationDecisionType(input.DecisionType);

        ReferenceOrchestrationRunPayload updated;
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var run = await ReadOrchestrationRunAsync(connection, input.NovelId, runId, cancellationToken, required: true)
                ?? throw new InvalidOperationException("Required orchestration run was not loaded.");
            if (string.Equals(run.Status, ReferenceOrchestrationRunStatuses.Cancelled, StringComparison.Ordinal) ||
                string.Equals(run.Status, ReferenceOrchestrationRunStatuses.Completed, StringComparison.Ordinal))
            {
                throw new ArgumentException("Orchestration run is not resumable.", nameof(input));
            }

            if (run.CurrentDecision is null)
            {
                throw new ArgumentException("Orchestration run has no pending decision.", nameof(input));
            }

            if (!string.Equals(run.CurrentDecision.DecisionType, decisionType, StringComparison.Ordinal))
            {
                throw new ArgumentException("Decision type does not match the pending orchestration decision.", nameof(input));
            }

            updated = run with
            {
                Status = ReferenceOrchestrationRunStatuses.Running,
                Stage = NextStageAfterDecision(decisionType),
                CurrentDecision = null,
                LastStopReason = string.Empty,
                ErrorMessage = string.Empty,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await UpdateOrchestrationRunAsync(connection, updated, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }

        return ShouldRunOrchestrationSafeStages(updated)
            ? await AdvanceOrchestrationSafeStagesAsync(updated, cancellationToken)
            : updated;
    }

    public async ValueTask<ReferenceOrchestrationRunPayload> CancelOrchestrationRunAsync(
        CancelReferenceOrchestrationRunPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var runId = NormalizeRunId(input.RunId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var run = await ReadOrchestrationRunAsync(connection, input.NovelId, runId, cancellationToken, required: true)
                ?? throw new InvalidOperationException("Required orchestration run was not loaded.");
            var updated = run with
            {
                Status = ReferenceOrchestrationRunStatuses.Cancelled,
                CurrentDecision = null,
                LastStopReason = ReferenceOrchestrationStopReasons.Cancelled,
                ErrorMessage = NormalizeOptional(input.Reason, "cancelled", 500),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await UpdateOrchestrationRunAsync(connection, updated, cancellationToken);
            return updated;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<ReferenceOrchestrationRunPayload> AdvanceOrchestrationSafeStagesAsync(
        ReferenceOrchestrationRunPayload run,
        CancellationToken cancellationToken)
    {
        var current = run;
        try
        {
            if (string.Equals(current.Stage, ReferenceOrchestrationStages.BlueprintGeneration, StringComparison.Ordinal))
            {
                var blueprint = await GenerateChapterBlueprintAsync(
                    new GenerateReferenceChapterBlueprintPayload(
                        current.NovelId,
                        current.ChapterNumber,
                        Title: null,
                        current.ChapterGoal,
                        current.AnchorIds,
                        current.KnownFacts,
                        current.ForbiddenFacts),
                    cancellationToken);
                current = await PersistOrchestrationRunAsync(
                    current with
                    {
                        Stage = ReferenceOrchestrationStages.BlueprintReview,
                        BlueprintId = blueprint.BlueprintId,
                        UpdatedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);

                var review = await ReviewChapterBlueprintAsync(
                    new ReviewReferenceChapterBlueprintPayload(current.NovelId, blueprint.BlueprintId),
                    cancellationToken);
                current = BuildPostReviewOrchestrationRun(current, blueprint, review);
                return await PersistOrchestrationRunAsync(current, cancellationToken);
            }

            if (string.Equals(current.Stage, ReferenceOrchestrationStages.MaterialBinding, StringComparison.Ordinal))
            {
                var approved = await ApproveChapterBlueprintAsync(
                    new ApproveReferenceChapterBlueprintPayload(
                        current.NovelId,
                        current.BlueprintId,
                        current.ReviewId),
                    cancellationToken);
                current = await PersistOrchestrationRunAsync(
                    current with
                    {
                        Stage = ReferenceOrchestrationStages.MaterialBinding,
                        UpdatedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);

                await BindBlueprintMaterialsAsync(
                    new BindReferenceBlueprintMaterialsPayload(
                        current.NovelId,
                        approved.BlueprintId,
                        current.CorpusSearchPolicy.MaxResultsPerBeat,
                        SelectTopCandidate: true),
                    cancellationToken);
                current = await PersistOrchestrationRunAsync(
                    current with
                    {
                        Stage = ReferenceOrchestrationStages.DraftGeneration,
                        UpdatedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);

                var draft = await GenerateDraftFromBlueprintAsync(
                    new GenerateReferenceAnchoredDraftPayload(current.NovelId, approved.BlueprintId, BeatIds: []),
                    cancellationToken);
                var candidateIds = draft.Candidates.Select(candidate => candidate.CandidateId).ToArray();
                current = await PersistOrchestrationRunAsync(
                    current with
                    {
                        Stage = ReferenceOrchestrationStages.DraftAudit,
                        CandidateIds = candidateIds,
                        UpdatedAt = DateTimeOffset.UtcNow
                    },
                    cancellationToken);

                var audit = await AuditDraftAgainstBlueprintAsync(
                    new AuditReferenceAnchoredDraftPayload(current.NovelId, approved.BlueprintId, candidateIds),
                    cancellationToken);
                current = BuildPostDraftAuditOrchestrationRun(current, approved, audit);
                return await PersistOrchestrationRunAsync(current, cancellationToken);
            }

            return current;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failed = current with
            {
                Status = ReferenceOrchestrationRunStatuses.Failed,
                CurrentDecision = null,
                LastStopReason = string.Empty,
                ErrorMessage = NormalizeOptional(exception.Message, "orchestration failed", 1_000),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return await PersistOrchestrationRunAsync(failed, cancellationToken);
        }
    }

    private async ValueTask<ReferenceOrchestrationRunPayload> PersistOrchestrationRunAsync(
        ReferenceOrchestrationRunPayload run,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await UpdateOrchestrationRunAsync(connection, run, cancellationToken);
            return run;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<IReadOnlyList<ReferenceDraftParagraphCandidatePayload>> GenerateBeatCandidatesAsync(
        long novelId,
        ReferenceChapterBlueprintPayload blueprint,
        IReadOnlyList<ReferenceChapterBlueprintBeatPayload> targetBeats,
        IReadOnlyDictionary<string, ReferenceBlueprintMaterialLinkPayload> selectedLinks,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ReferenceDraftParagraphCandidatePayload>();
        foreach (var beat in targetBeats.OrderBy(item => item.BeatIndex))
        {
            if (!selectedLinks.TryGetValue(beat.BeatId, out var link))
            {
                if (!string.IsNullOrWhiteSpace(beat.NoReuseReason))
                {
                    candidates.Add(BuildNoReuseDraftCandidate(blueprint, beat));
                }

                continue;
            }

            var referenceAnchors = _referenceAnchors
                ?? throw new ArgumentException("Reference-anchored draft generation requires a configured reference anchor service.", nameof(_referenceAnchors));
            var adapted = await referenceAnchors.AdaptMaterialAsync(
                new AdaptReferenceMaterialPayload(
                    novelId,
                    link.MaterialId,
                    beat.SlotPlan,
                    beat.MaxRewriteLevel,
                    beat.SceneFacts.Concat(beat.ViewpointAllowedKnowledge).Distinct(StringComparer.Ordinal).ToArray()),
                cancellationToken);
            var candidate = new ReferenceDraftParagraphCandidatePayload(
                "draft-" + Guid.NewGuid().ToString("N"),
                blueprint.BlueprintId,
                beat.BeatId,
                link.MaterialId,
                adapted.RewriteLevel,
                adapted.Text,
                adapted.ChangedSlots,
                adapted.NonSlotEdits,
                adapted.Audit.Status,
                DateTimeOffset.UtcNow);
            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
        {
            throw new ArgumentException("Reference-anchored draft generation produced no auditable candidates.", nameof(targetBeats));
        }

        return candidates;
    }

    private static ReferenceDraftParagraphCandidatePayload BuildNoReuseDraftCandidate(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var text = BuildNoReuseDraftText(blueprint, beat);
        return new ReferenceDraftParagraphCandidatePayload(
            "draft-" + Guid.NewGuid().ToString("N"),
            blueprint.BlueprintId,
            beat.BeatId,
            ReferenceDraftProvenanceIds.BuildNoReuseMaterialId(beat.BeatId),
            ReferenceRewriteLevels.L0,
            text,
            Array.Empty<ReferenceSlotValuePayload>(),
            Array.Empty<string>(),
            "passed",
            DateTimeOffset.UtcNow);
    }

    private static string BuildNoReuseDraftText(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintBeatPayload beat)
    {
        var premise = FirstNonEmpty(beat.LogicPremise, blueprint.ChapterFunction, "这一段压力");
        var approvedEvidence = ReferenceAnchoredDraftAuditor.ExtractRequiredProsePhrases(beat)
            .Concat(ReferenceAnchoredDraftAuditor.ExtractRequiredEmotionEvidence(beat))
            .Concat(ReferenceAnchoredDraftAuditor.ExtractPlannedEmotionMechanics(beat))
            .Concat(beat.SceneFacts)
            .Concat(beat.ViewpointAllowedKnowledge)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();
        var text = "因为" + TrimForSentence(premise, 48) +
            "，他在原地停住，心里意识到压力仍然压着呼吸。只是那阵沉默没有散开，指尖发凉，直到下一步被推到眼前。";
        if (approvedEvidence.Length > 0)
        {
            text += "这一段只使用已审批蓝图内容：" + string.Join("，", approvedEvidence) + "。";
        }

        return text;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static string TrimForSentence(string value, int maxLength)
    {
        var normalized = value.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim(' ', '\t', '。', '.', '！', '!', '？', '?', '；', ';', '：', ':', '，', ',');
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..maxLength].Trim();
    }

    private async ValueTask<IReadOnlyDictionary<string, ReferenceBlueprintMaterialLinkPayload>> EnsureSelectedMaterialLinksAsync(
        long novelId,
        ReferenceChapterBlueprintPayload blueprint,
        IReadOnlyList<ReferenceChapterBlueprintBeatPayload> targetBeats,
        CancellationToken cancellationToken)
    {
        var requiredBeatIds = ReferenceAnchoredDraftPreflight.RequiredMaterialBeatIds(targetBeats);
        if (requiredBeatIds.Count == 0)
        {
            return new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal);
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var links = await ReadSelectedMaterialLinksAsync(
                connection,
                novelId,
                blueprint.BlueprintId,
                blueprint.AnalysisContractHash,
                requiredBeatIds,
                cancellationToken);
            return ReferenceAnchoredDraftPreflight.EnsureSelectedMaterialLinksForTargetBeats(targetBeats, links);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterialPayload>> SearchMaterialsForBeatAsync(
        IReferenceAnchorService referenceAnchors,
        long novelId,
        long primaryAnchorId,
        ReferenceChapterBlueprintBeatPayload beat,
        int maxResultsPerBeat,
        CancellationToken cancellationToken)
    {
        var anchorIds = primaryAnchorId > 0 ? new[] { primaryAnchorId } : Array.Empty<long>();
        var query = beat.ReferenceQuery;
        var size = Math.Clamp(maxResultsPerBeat * 5, maxResultsPerBeat, 100);
        var result = await referenceAnchors.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novelId,
                anchorIds,
                query.Query,
                query.MaterialTypes.Count > 0 ? query.MaterialTypes : beat.RequiredMaterialTypes,
                query.EmotionTags,
                query.FunctionTags,
                query.PovTags,
                query.TechniqueTags,
                Page: 1,
                Size: size),
            cancellationToken);

        if (result.Items.Count > 0 || string.IsNullOrWhiteSpace(query.Query))
        {
            return result.Items;
        }

        var fallback = await referenceAnchors.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novelId,
                anchorIds,
                string.Empty,
                query.MaterialTypes.Count > 0 ? query.MaterialTypes : beat.RequiredMaterialTypes,
                query.EmotionTags,
                query.FunctionTags,
                query.PovTags,
                query.TechniqueTags,
                Page: 1,
                Size: size),
            cancellationToken);
        return fallback.Items;
    }

    private static async ValueTask<IReadOnlySet<string>> LoadAcceptedFeedbackMaterialIdsAsync(
        IReferenceAnchorService referenceAnchors,
        long novelId,
        CancellationToken cancellationToken)
    {
        var feedback = await referenceAnchors.GetUserFeedbackAsync(
            new GetReferenceUserFeedbackPayload(
                novelId,
                TargetType: string.Empty,
                TargetId: string.Empty,
                Limit: 500),
            cancellationToken);
        return feedback
            .Where(item => string.Equals(item.Decision, ReferenceFeedbackDecisions.Accepted, StringComparison.Ordinal))
            .Select(item => string.IsNullOrWhiteSpace(item.MaterialId) &&
                    string.Equals(item.TargetType, ReferenceFeedbackTargetTypes.Material, StringComparison.Ordinal)
                        ? item.TargetId
                        : item.MaterialId)
            .Where(materialId => !string.IsNullOrWhiteSpace(materialId))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static ScoredMaterialLink ScoreMaterialForBeat(
        long blueprintId,
        string analysisContractHash,
        ReferenceChapterBlueprintBeatPayload beat,
        ReferenceMaterialPayload material,
        IReadOnlySet<string> acceptedFeedbackMaterialIds,
        DateTimeOffset now)
    {
        var components = new Dictionary<string, double>(StringComparer.Ordinal);
        var functionFit = ContainsTag(beat.ReferenceQuery.FunctionTags, material.FunctionTag);
        var emotionFit = ContainsTag(beat.ReferenceQuery.EmotionTags, material.EmotionTag);
        var povFit = ContainsTag(beat.ReferenceQuery.PovTags, material.PovTag);
        var proseDutyFit = HasProseDutyFit(beat.ProseDuties, material);
        var materialTypeFit = ContainsTag(beat.RequiredMaterialTypes, material.MaterialType) ||
            ContainsTag(beat.ReferenceQuery.MaterialTypes, material.MaterialType);

        AddScore(components, "material_type", materialTypeFit ? 1.5 : 0);
        AddScore(components, "function", functionFit ? 3.0 : 0);
        AddScore(components, "emotion", emotionFit ? 2.0 : 0);
        AddScore(components, "pov", povFit ? 1.5 : 0);
        AddScore(components, "prose_duty", proseDutyFit ? 2.0 : 0);
        AddScore(components, "lexical", SearchOrLocalLexicalScore(beat.ReferenceQuery.Query, material));
        AddScore(components, "confidence", material.FunctionConfidence + material.EmotionConfidence * 0.25 + material.PovConfidence * 0.25);
        AddScore(components, "user_verified", material.UserVerified ? 1.0 : 0);
        AddScore(components, "accepted_feedback", acceptedFeedbackMaterialIds.Contains(material.MaterialId) ? 2.0 : 0);
        var embeddingFit = TryGetScoreComponent(material, "embedding", out var embeddingScore);
        AddScore(components, "embedding", embeddingScore);

        var hasFunctionalFit = functionFit || emotionFit || povFit || proseDutyFit;
        var score = components.Values.Sum();
        if (!hasFunctionalFit)
        {
            score = 0;
        }

        var roundedScore = Math.Round(score, 4);
        var link = new ReferenceBlueprintMaterialLinkPayload(
            BuildMaterialLinkId(blueprintId, beat.BeatId, material.MaterialId),
            blueprintId,
            beat.BeatId,
            material.MaterialId,
            beat.NarrativeFunction,
            beat.MaxRewriteLevel,
            Selected: false,
            roundedScore,
            components,
            BuildMaterialFitExplanation(
                beat,
                material,
                functionFit,
                emotionFit,
                povFit,
                proseDutyFit,
                materialTypeFit,
                acceptedFeedbackMaterialIds.Contains(material.MaterialId),
                embeddingFit),
            now);
        return new ScoredMaterialLink(link, analysisContractHash, components, hasFunctionalFit);
    }

    private static string BuildMaterialFitExplanation(
        ReferenceChapterBlueprintBeatPayload beat,
        ReferenceMaterialPayload material,
        bool functionFit,
        bool emotionFit,
        bool povFit,
        bool proseDutyFit,
        bool materialTypeFit,
        bool acceptedFeedbackFit,
        bool embeddingFit)
    {
        var matches = new List<string>();
        if (materialTypeFit)
        {
            matches.Add("material type " + material.MaterialType);
        }

        if (functionFit)
        {
            matches.Add("function " + material.FunctionTag);
        }

        if (emotionFit)
        {
            matches.Add("emotion " + material.EmotionTag);
        }

        if (povFit)
        {
            matches.Add("POV " + material.PovTag);
        }

        if (proseDutyFit)
        {
            matches.Add("prose duty");
        }

        if (acceptedFeedbackFit)
        {
            matches.Add("accepted feedback");
        }

        if (embeddingFit)
        {
            matches.Add("embedding rank");
        }

        var fit = matches.Count == 0 ? "lexical and confidence only" : string.Join(", ", matches);
        var intendedUse = TruncateForExplanation(beat.NarrativeFunction, maxLength: 96);
        return string.IsNullOrWhiteSpace(intendedUse)
            ? $"Beat {beat.BeatIndex} fit: {fit}."
            : $"Beat {beat.BeatIndex} fit: {fit}. Intended use: {intendedUse}";
    }

    private static string TruncateForExplanation(string value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength].TrimEnd() + "...";
    }

    private static bool ContainsTag(IReadOnlyList<string> tags, string value)
    {
        return tags.Any(tag => string.Equals(tag, value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasProseDutyFit(IReadOnlyList<string> proseDuties, ReferenceMaterialPayload material)
    {
        foreach (var duty in proseDuties)
        {
            if ((string.Equals(duty, "interiority", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(material.FunctionTag, "interiority", StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(duty, "external_evidence", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(material.FunctionTag, "action", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(material.FunctionTag, "environment", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(material.FunctionTag, "emotion_evidence", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(material.TechniqueTag, "external_evidence", StringComparison.OrdinalIgnoreCase))) ||
                (string.Equals(duty, "transition", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(material.FunctionTag, "transition", StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(duty, "sensory", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(material.TechniqueTag, "sensory_detail", StringComparison.OrdinalIgnoreCase)) ||
                (string.Equals(duty, "subtext", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(material.FunctionTag, "dialogue", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(material.FunctionTag, "interiority", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(material.FunctionTag, "emotion_evidence", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(material.TechniqueTag, "external_evidence", StringComparison.OrdinalIgnoreCase))) ||
                (string.Equals(duty, "causality", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(material.FunctionTag, "dialogue", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static double LexicalScore(string query, string text)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return 0;
        }

        var index = text.IndexOf(normalized, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? 0 : Math.Max(0.5, 3.0 - index / 50.0);
    }

    private static double SearchOrLocalLexicalScore(string query, ReferenceMaterialPayload material)
    {
        return TryGetScoreComponent(material, "lexical", out var searchScore)
            ? searchScore
            : LexicalScore(query, material.Text);
    }

    private static void AddScore(IDictionary<string, double> components, string name, double value)
    {
        if (value > 0)
        {
            components[name] = Math.Round(value, 4);
        }
    }

    private static bool TryGetScoreComponent(ReferenceMaterialPayload material, string name, out double value)
    {
        value = 0;
        if (material.ScoreComponents is null ||
            !material.ScoreComponents.TryGetValue(name, out var component) ||
            component <= 0 ||
            double.IsNaN(component) ||
            double.IsInfinity(component))
        {
            return false;
        }

        value = component;
        return true;
    }

    private static string BuildMaterialLinkId(long blueprintId, string beatId, string materialId)
    {
        return "link-" + HashText(string.Create(CultureInfo.InvariantCulture, $"{blueprintId}|{beatId}|{materialId}"))[..24];
    }

    private static ReferenceChapterBlueprintPayload BuildDeterministicBlueprint(
        long blueprintId,
        long novelId,
        int chapterNumber,
        string title,
        string sourcePlanScope,
        string sourcePlanHash,
        string contextHash,
        long primaryAnchorId,
        string chapterFunction,
        string planText,
        IReadOnlyList<string> knownFacts,
        IReadOnlyList<string> forbiddenFacts,
        DateTimeOffset now)
    {
        var summary = BuildBlueprintPremise(planText, chapterFunction);
        var beat = new ReferenceChapterBlueprintBeatPayload(
            BeatId: BuildBeatId(blueprintId, 1),
            BeatIndex: 1,
            SceneIndex: 1,
            BeatType: ReferenceBlueprintBeatTypes.Interiority,
            NarrativeFunction: "convert chapter goal into reviewable prose duties before drafting",
            LogicPremise: summary,
            ConflictPressure: chapterFunction,
            CausalityIn: "chapter starts from the provided plan or chapter goal",
            CausalityOut: "chapter must reach the declared final hook without adding forbidden facts",
            TransitionIn: "transition pressure continues from previous known state",
            TransitionOut: "carry pressure into the final hook",
            PovCharacter: "unspecified",
            NarrativeDistance: "close",
            ViewpointAllowedKnowledge: knownFacts,
            ViewpointForbiddenKnowledge: forbiddenFacts,
            CharacterStatesBefore: ["state before chapter must be inferred from known facts"],
            CharacterStatesAfter: ["state after chapter must follow from the beat consequence"],
            CharacterGoals: ["pursue chapter goal"],
            CharacterMisbeliefs: ["unknown until user or plan provides a stronger constraint"],
            RelationshipPressure: ["relationship pressure must be made explicit before drafting"],
            EmotionTrigger: "chapter goal creates pressure",
            EmotionBefore: "controlled",
            EmotionAfter: "pressured",
            SuppressedReaction: "emotion should be shown through controlled external evidence",
            ExternalEvidence: "visible pause or changed action demonstrates the emotional shift",
            NarrationStrategy: "close narration with interiority, external evidence, and transition",
            RhythmStrategy: "alternate reflective sentence with physical afterbeat",
            ParagraphIntention: "dwell on the pressure before the next action",
            ExecutionMode: "dwell",
            AntiScreenplayDuty: "show pressure through interiority, external evidence, and transition rather than action/dialogue blocking",
            SensoryAnchorTarget: "one source-backed physical detail from the scene",
            SubtextPlan: "let restraint and delayed reaction carry the emotion",
            SourceBackedDetailTarget: "source-backed concrete pressure detail",
            CandidateRejectionRule: "reject action-only or dialogue-only prose for this beat",
            SceneFacts: knownFacts,
            ForbiddenFacts: forbiddenFacts,
            ReferenceQuery: new ReferenceMaterialQueryPayload(
                Query: chapterFunction,
                MaterialTypes: [ReferenceMaterialTypes.Passage, ReferenceMaterialTypes.Sentence],
                EmotionTags: [],
                FunctionTags: ["interiority", "environment", "narration"],
                PovTags: ["close", "unknown"],
                TechniqueTags: [],
                MaxResults: 5),
            RequiredMaterialTypes: [ReferenceMaterialTypes.Passage, ReferenceMaterialTypes.Sentence],
            MaxRewriteLevel: ReferenceRewriteLevels.L2,
            SlotPlan: [],
            LockedPhrasePolicy: "preserve reference cadence only when rewrite level allows it",
            NoReuseReason: string.Empty,
            ProseDuties: ["causality", "interiority", "external_evidence", "transition"],
            RiskFlags: ["needs_user_review"]);

        var payload = new ReferenceChapterBlueprintPayload(
            blueprintId,
            novelId,
            chapterNumber,
            title,
            ReferenceBlueprintStates.Draft,
            sourcePlanScope,
            sourcePlanHash,
            contextHash,
            AnalysisContractHash: string.Empty,
            BlueprintVersion: 1,
            ParentBlueprintId: 0,
            primaryAnchorId,
            chapterFunction,
            new ReferenceChapterBlueprintAnalysisTrackPayload("logic", "chapter function must be causally connected to the hook", ["premise", "conflict", "consequence", "hook"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("emotion", "emotional movement must have trigger and visible evidence", ["before", "trigger", "suppressed reaction", "after"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("narration", "prose must not collapse into action/dialogue beats", ["POV boundary", "distance", "interiority", "rhythm"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("character", "character state must change through pressure", ["goal", "misbelief", "leverage", "state delta"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("reference", "reference material must fit function, emotion, POV, prose duty, and rewrite budget", ["query intent", "material type", "rewrite budget", "no-reuse policy"]),
            new ReferenceChapterBlueprintAnalysisTrackPayload("transition", "scene movement must be causally motivated", ["transition in", "transition out"]),
            new ReferenceChapterBlueprintExecutionTrackPayload(
                "execution",
                "paragraph execution must stay novelistic before prose generation",
                ["dwell on the pressure before the next action"],
                ["dwell"],
                ["interiority, external evidence, and transition before action/dialogue"],
                ["source-backed concrete pressure detail"],
                ["reject action-only or dialogue-only prose"]),
            "previous state is bounded by known facts",
            "final state follows the chapter function",
            "final hook must be derived from the beat consequence",
            "unspecified",
            "close",
            knownFacts,
            forbiddenFacts,
            ["deterministic_blueprint"],
            [beat],
            LatestReview: null,
            now,
            now);
        return payload with
        {
            AnalysisContractHash = ReferenceChapterBlueprintNormalizer.ComputeAnalysisContractHash(payload),
            BuildVersion = BuildVersion
        };
    }

    private async ValueTask<long> InsertBlueprintAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceChapterBlueprintPayload blueprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_chapter_blueprints
              (novel_id, chapter_number, primary_anchor_id, title, status, source_plan_scope,
               source_plan_hash, context_hash, analysis_contract_hash, blueprint_version, parent_blueprint_id,
               chapter_function, logic_analysis_json, emotion_analysis_json, narration_analysis_json,
               character_analysis_json, reference_analysis_json, transition_plan_json, execution_contract_json, previous_state, final_state, final_hook,
               global_pov, global_narrative_distance, known_facts_json, forbidden_facts_json,
               risk_flags_json, build_version, approved_review_id, created_at, updated_at, approved_at)
            VALUES
              ($novel_id, $chapter_number, $primary_anchor_id, $title, $status, $source_plan_scope,
               $source_plan_hash, $context_hash, $analysis_contract_hash, $blueprint_version, $parent_blueprint_id,
               $chapter_function, $logic_analysis_json, $emotion_analysis_json, $narration_analysis_json,
               $character_analysis_json, $reference_analysis_json, $transition_plan_json, $execution_contract_json, $previous_state, $final_state, $final_hook,
               $global_pov, $global_narrative_distance, $known_facts_json, $forbidden_facts_json,
               $risk_flags_json, $build_version, '', $created_at, $updated_at, '')
            RETURNING blueprint_id;
            """;
        command.Parameters.AddWithValue("$novel_id", blueprint.NovelId);
        command.Parameters.AddWithValue("$chapter_number", blueprint.ChapterNumber);
        command.Parameters.AddWithValue("$primary_anchor_id", blueprint.PrimaryAnchorId);
        command.Parameters.AddWithValue("$title", blueprint.Title);
        command.Parameters.AddWithValue("$status", blueprint.Status);
        command.Parameters.AddWithValue("$source_plan_scope", blueprint.SourcePlanScope);
        command.Parameters.AddWithValue("$source_plan_hash", blueprint.SourcePlanHash);
        command.Parameters.AddWithValue("$context_hash", blueprint.ContextHash);
        command.Parameters.AddWithValue("$analysis_contract_hash", blueprint.AnalysisContractHash);
        command.Parameters.AddWithValue("$blueprint_version", blueprint.BlueprintVersion);
        command.Parameters.AddWithValue("$parent_blueprint_id", blueprint.ParentBlueprintId);
        command.Parameters.AddWithValue("$chapter_function", blueprint.ChapterFunction);
        command.Parameters.AddWithValue("$logic_analysis_json", JsonSerializer.Serialize(blueprint.LogicAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$emotion_analysis_json", JsonSerializer.Serialize(blueprint.EmotionAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$narration_analysis_json", JsonSerializer.Serialize(blueprint.NarrationAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$character_analysis_json", JsonSerializer.Serialize(blueprint.CharacterAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$reference_analysis_json", JsonSerializer.Serialize(blueprint.ReferenceAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$transition_plan_json", JsonSerializer.Serialize(blueprint.TransitionPlan, JsonOptions));
        command.Parameters.AddWithValue("$execution_contract_json", JsonSerializer.Serialize(blueprint.ExecutionContract, JsonOptions));
        command.Parameters.AddWithValue("$previous_state", blueprint.PreviousState);
        command.Parameters.AddWithValue("$final_state", blueprint.FinalState);
        command.Parameters.AddWithValue("$final_hook", blueprint.FinalHook);
        command.Parameters.AddWithValue("$global_pov", blueprint.GlobalPov);
        command.Parameters.AddWithValue("$global_narrative_distance", blueprint.GlobalNarrativeDistance);
        command.Parameters.AddWithValue("$known_facts_json", JsonSerializer.Serialize(blueprint.KnownFacts, JsonOptions));
        command.Parameters.AddWithValue("$forbidden_facts_json", JsonSerializer.Serialize(blueprint.ForbiddenFacts, JsonOptions));
        command.Parameters.AddWithValue("$risk_flags_json", JsonSerializer.Serialize(blueprint.RiskFlags, JsonOptions));
        command.Parameters.AddWithValue("$build_version", BuildVersion);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(blueprint.CreatedAt));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(blueprint.UpdatedAt));
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("SQLite did not return a blueprint id."));
    }

    private static async ValueTask ReplaceBeatsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long blueprintId,
        IReadOnlyList<ReferenceChapterBlueprintBeatPayload> beats,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_chapter_blueprint_beats WHERE blueprint_id = $blueprint_id;";
            delete.Parameters.AddWithValue("$blueprint_id", blueprintId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var beat in beats)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_chapter_blueprint_beats
                  (beat_id, blueprint_id, beat_index, scene_index, beat_type, narrative_function,
                   logic_premise, conflict_pressure, causality_in, causality_out, transition_in,
                   transition_out, pov_character, narrative_distance, viewpoint_allowed_knowledge_json,
                   viewpoint_forbidden_knowledge_json, character_states_before_json,
                   character_states_after_json, character_goals_json, character_misbeliefs_json,
                   relationship_pressure_json, emotion_trigger, emotion_before, emotion_after,
                   suppressed_reaction, external_evidence, narration_strategy, rhythm_strategy,
                   paragraph_intention, execution_mode, anti_screenplay_duty, sensory_anchor_target,
                   subtext_plan, source_backed_detail_target, candidate_rejection_rule,
                   scene_facts_json, forbidden_facts_json, reference_query_json,
                   required_material_types_json, max_rewrite_level, slot_plan_json,
                   locked_phrase_policy, no_reuse_reason, prose_duties_json, risk_flags_json)
                VALUES
                  ($beat_id, $blueprint_id, $beat_index, $scene_index, $beat_type, $narrative_function,
                   $logic_premise, $conflict_pressure, $causality_in, $causality_out, $transition_in,
                   $transition_out, $pov_character, $narrative_distance, $viewpoint_allowed_knowledge_json,
                   $viewpoint_forbidden_knowledge_json, $character_states_before_json,
                   $character_states_after_json, $character_goals_json, $character_misbeliefs_json,
                   $relationship_pressure_json, $emotion_trigger, $emotion_before, $emotion_after,
                   $suppressed_reaction, $external_evidence, $narration_strategy, $rhythm_strategy,
                   $paragraph_intention, $execution_mode, $anti_screenplay_duty, $sensory_anchor_target,
                   $subtext_plan, $source_backed_detail_target, $candidate_rejection_rule,
                   $scene_facts_json, $forbidden_facts_json, $reference_query_json,
                   $required_material_types_json, $max_rewrite_level, $slot_plan_json,
                   $locked_phrase_policy, $no_reuse_reason, $prose_duties_json, $risk_flags_json);
                """;
            command.Parameters.AddWithValue("$beat_id", beat.BeatId);
            command.Parameters.AddWithValue("$blueprint_id", blueprintId);
            command.Parameters.AddWithValue("$beat_index", beat.BeatIndex);
            command.Parameters.AddWithValue("$scene_index", beat.SceneIndex);
            command.Parameters.AddWithValue("$beat_type", beat.BeatType);
            command.Parameters.AddWithValue("$narrative_function", beat.NarrativeFunction);
            command.Parameters.AddWithValue("$logic_premise", beat.LogicPremise);
            command.Parameters.AddWithValue("$conflict_pressure", beat.ConflictPressure);
            command.Parameters.AddWithValue("$causality_in", beat.CausalityIn);
            command.Parameters.AddWithValue("$causality_out", beat.CausalityOut);
            command.Parameters.AddWithValue("$transition_in", beat.TransitionIn);
            command.Parameters.AddWithValue("$transition_out", beat.TransitionOut);
            command.Parameters.AddWithValue("$pov_character", beat.PovCharacter);
            command.Parameters.AddWithValue("$narrative_distance", beat.NarrativeDistance);
            command.Parameters.AddWithValue("$viewpoint_allowed_knowledge_json", JsonSerializer.Serialize(beat.ViewpointAllowedKnowledge, JsonOptions));
            command.Parameters.AddWithValue("$viewpoint_forbidden_knowledge_json", JsonSerializer.Serialize(beat.ViewpointForbiddenKnowledge, JsonOptions));
            command.Parameters.AddWithValue("$character_states_before_json", JsonSerializer.Serialize(beat.CharacterStatesBefore, JsonOptions));
            command.Parameters.AddWithValue("$character_states_after_json", JsonSerializer.Serialize(beat.CharacterStatesAfter, JsonOptions));
            command.Parameters.AddWithValue("$character_goals_json", JsonSerializer.Serialize(beat.CharacterGoals, JsonOptions));
            command.Parameters.AddWithValue("$character_misbeliefs_json", JsonSerializer.Serialize(beat.CharacterMisbeliefs, JsonOptions));
            command.Parameters.AddWithValue("$relationship_pressure_json", JsonSerializer.Serialize(beat.RelationshipPressure, JsonOptions));
            command.Parameters.AddWithValue("$emotion_trigger", beat.EmotionTrigger);
            command.Parameters.AddWithValue("$emotion_before", beat.EmotionBefore);
            command.Parameters.AddWithValue("$emotion_after", beat.EmotionAfter);
            command.Parameters.AddWithValue("$suppressed_reaction", beat.SuppressedReaction);
            command.Parameters.AddWithValue("$external_evidence", beat.ExternalEvidence);
            command.Parameters.AddWithValue("$narration_strategy", beat.NarrationStrategy);
            command.Parameters.AddWithValue("$rhythm_strategy", beat.RhythmStrategy);
            command.Parameters.AddWithValue("$paragraph_intention", beat.ParagraphIntention);
            command.Parameters.AddWithValue("$execution_mode", beat.ExecutionMode);
            command.Parameters.AddWithValue("$anti_screenplay_duty", beat.AntiScreenplayDuty);
            command.Parameters.AddWithValue("$sensory_anchor_target", beat.SensoryAnchorTarget);
            command.Parameters.AddWithValue("$subtext_plan", beat.SubtextPlan);
            command.Parameters.AddWithValue("$source_backed_detail_target", beat.SourceBackedDetailTarget);
            command.Parameters.AddWithValue("$candidate_rejection_rule", beat.CandidateRejectionRule);
            command.Parameters.AddWithValue("$scene_facts_json", JsonSerializer.Serialize(beat.SceneFacts, JsonOptions));
            command.Parameters.AddWithValue("$forbidden_facts_json", JsonSerializer.Serialize(beat.ForbiddenFacts, JsonOptions));
            command.Parameters.AddWithValue("$reference_query_json", JsonSerializer.Serialize(beat.ReferenceQuery, JsonOptions));
            command.Parameters.AddWithValue("$required_material_types_json", JsonSerializer.Serialize(beat.RequiredMaterialTypes, JsonOptions));
            command.Parameters.AddWithValue("$max_rewrite_level", beat.MaxRewriteLevel);
            command.Parameters.AddWithValue("$slot_plan_json", JsonSerializer.Serialize(beat.SlotPlan, JsonOptions));
            command.Parameters.AddWithValue("$locked_phrase_policy", beat.LockedPhrasePolicy);
            command.Parameters.AddWithValue("$no_reuse_reason", beat.NoReuseReason);
            command.Parameters.AddWithValue("$prose_duties_json", JsonSerializer.Serialize(beat.ProseDuties, JsonOptions));
            command.Parameters.AddWithValue("$risk_flags_json", JsonSerializer.Serialize(beat.RiskFlags, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async ValueTask<ReferenceChapterBlueprintPayload?> ReadBlueprintAsync(
        SqliteConnection connection,
        long novelId,
        long blueprintId,
        CancellationToken cancellationToken,
        bool required)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT blueprint_id, novel_id, chapter_number, title, status, source_plan_scope,
                   source_plan_hash, context_hash, analysis_contract_hash, blueprint_version, parent_blueprint_id,
                   primary_anchor_id, chapter_function, logic_analysis_json, emotion_analysis_json,
                   narration_analysis_json, character_analysis_json, reference_analysis_json, transition_plan_json, execution_contract_json,
                   previous_state, final_state, final_hook, global_pov, global_narrative_distance,
                   known_facts_json, forbidden_facts_json, risk_flags_json, build_version, created_at, updated_at
            FROM reference_chapter_blueprints
            WHERE novel_id = $novel_id AND blueprint_id = $blueprint_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            if (required)
            {
                throw new ArgumentException("Blueprint does not exist.", nameof(blueprintId));
            }

            return null;
        }

        var row = new BlueprintRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt32(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetString(12),
            ReadJson<ReferenceChapterBlueprintAnalysisTrackPayload>(reader.GetString(13)),
            ReadJson<ReferenceChapterBlueprintAnalysisTrackPayload>(reader.GetString(14)),
            ReadJson<ReferenceChapterBlueprintAnalysisTrackPayload>(reader.GetString(15)),
            ReadJson<ReferenceChapterBlueprintAnalysisTrackPayload>(reader.GetString(16)),
            ReadJson<ReferenceChapterBlueprintAnalysisTrackPayload>(reader.GetString(17)),
            ReadJson<ReferenceChapterBlueprintAnalysisTrackPayload>(reader.GetString(18)),
            ReadJson<ReferenceChapterBlueprintExecutionTrackPayload>(reader.GetString(19)),
            reader.GetString(20),
            reader.GetString(21),
            reader.GetString(22),
            reader.GetString(23),
            reader.GetString(24),
            ReadJson<IReadOnlyList<string>>(reader.GetString(25)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(26)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(27)),
            reader.GetString(28),
            ParseTimestamp(reader.GetString(29)),
            ParseTimestamp(reader.GetString(30)));

        var beats = await ReadBeatsAsync(connection, blueprintId, cancellationToken);
        var latestReview = await ReadLatestReviewAsync(connection, blueprintId, row.AnalysisContractHash, cancellationToken);
        var blueprint = new ReferenceChapterBlueprintPayload(
            row.BlueprintId,
            row.NovelId,
            row.ChapterNumber,
            row.Title,
            row.Status,
            row.SourcePlanScope,
            row.SourcePlanHash,
            row.ContextHash,
            row.AnalysisContractHash,
            row.BlueprintVersion,
            row.ParentBlueprintId,
            row.PrimaryAnchorId,
            row.ChapterFunction,
            row.LogicAnalysis,
            row.EmotionAnalysis,
            row.NarrationAnalysis,
            row.CharacterAnalysis,
            row.ReferenceAnalysis,
            row.TransitionPlan,
            row.ExecutionContract,
            row.PreviousState,
            row.FinalState,
            row.FinalHook,
            row.GlobalPov,
            row.GlobalNarrativeDistance,
            row.KnownFacts,
            row.ForbiddenFacts,
            row.RiskFlags,
            beats,
            latestReview,
            row.CreatedAt,
            row.UpdatedAt)
        {
            BuildVersion = row.BuildVersion
        };
        return await ApplyBlueprintStalenessAsync(connection, blueprint, cancellationToken);
    }

    private async ValueTask<ReferenceChapterBlueprintPayload> ApplyBlueprintStalenessAsync(
        SqliteConnection connection,
        ReferenceChapterBlueprintPayload blueprint,
        CancellationToken cancellationToken)
    {
        if (IsStalenessExempt(blueprint.Status))
        {
            return blueprint;
        }

        var currentHash = await CurrentSourcePlanHashAsync(blueprint.NovelId, blueprint.SourcePlanScope, cancellationToken);
        if (string.Equals(currentHash, blueprint.SourcePlanHash, StringComparison.Ordinal))
        {
            return blueprint;
        }

        var now = DateTimeOffset.UtcNow;
        await SetBlueprintStatusAsync(connection, blueprint.BlueprintId, ReferenceBlueprintStates.Stale, now, cancellationToken);
        return blueprint with
        {
            Status = ReferenceBlueprintStates.Stale,
            UpdatedAt = now
        };
    }

    private async ValueTask<ReferenceChapterBlueprintSummaryPayload> ApplySummaryStalenessAsync(
        SqliteConnection connection,
        ReferenceChapterBlueprintSummaryPayload summary,
        string sourcePlanScope,
        CancellationToken cancellationToken)
    {
        if (IsStalenessExempt(summary.Status))
        {
            return summary;
        }

        var currentHash = await CurrentSourcePlanHashAsync(summary.NovelId, sourcePlanScope, cancellationToken);
        if (string.Equals(currentHash, summary.SourcePlanHash, StringComparison.Ordinal))
        {
            return summary;
        }

        var now = DateTimeOffset.UtcNow;
        await SetBlueprintStatusAsync(connection, summary.BlueprintId, ReferenceBlueprintStates.Stale, now, cancellationToken);
        return summary with
        {
            Status = ReferenceBlueprintStates.Stale,
            UpdatedAt = now
        };
    }

    private async ValueTask<string> CurrentSourcePlanHashAsync(
        long novelId,
        string sourcePlanScope,
        CancellationToken cancellationToken)
    {
        var scope = string.IsNullOrWhiteSpace(sourcePlanScope) ? "next" : sourcePlanScope;
        var plans = await _planning.GetChapterPlansAsync(novelId, cancellationToken);
        var plan = plans.FirstOrDefault(item => string.Equals(item.Scope, scope, StringComparison.Ordinal));
        return ReferenceChapterBlueprintNormalizer.ComputeSourcePlanHash(scope, plan?.Content ?? string.Empty);
    }

    private static bool IsStalenessExempt(string status)
    {
        return string.Equals(status, ReferenceBlueprintStates.Stale, StringComparison.Ordinal) ||
            string.Equals(status, ReferenceBlueprintStates.Superseded, StringComparison.Ordinal);
    }

    private static async ValueTask SetBlueprintStatusAsync(
        SqliteConnection connection,
        long blueprintId,
        string status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_chapter_blueprints
            SET status = $status,
                updated_at = $updated_at
            WHERE blueprint_id = $blueprint_id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(updatedAt));
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<IReadOnlyList<ReferenceChapterBlueprintBeatPayload>> ReadBeatsAsync(
        SqliteConnection connection,
        long blueprintId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT beat_id, beat_index, scene_index, beat_type, narrative_function, logic_premise,
                   conflict_pressure, causality_in, causality_out, transition_in, transition_out,
                   pov_character, narrative_distance, viewpoint_allowed_knowledge_json,
                   viewpoint_forbidden_knowledge_json, character_states_before_json,
                   character_states_after_json, character_goals_json, character_misbeliefs_json,
                   relationship_pressure_json, emotion_trigger, emotion_before, emotion_after,
                   suppressed_reaction, external_evidence, narration_strategy, rhythm_strategy,
                   paragraph_intention, execution_mode, anti_screenplay_duty, sensory_anchor_target,
                   subtext_plan, source_backed_detail_target, candidate_rejection_rule,
                   scene_facts_json, forbidden_facts_json, reference_query_json,
                   required_material_types_json, max_rewrite_level, slot_plan_json,
                   locked_phrase_policy, no_reuse_reason, prose_duties_json, risk_flags_json
            FROM reference_chapter_blueprint_beats
            WHERE blueprint_id = $blueprint_id
            ORDER BY beat_index ASC;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        var beats = new List<ReferenceChapterBlueprintBeatPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            beats.Add(new ReferenceChapterBlueprintBeatPayload(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetString(12),
                ReadJson<IReadOnlyList<string>>(reader.GetString(13)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(14)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(15)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(16)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(17)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(18)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(19)),
                reader.GetString(20),
                reader.GetString(21),
                reader.GetString(22),
                reader.GetString(23),
                reader.GetString(24),
                reader.GetString(25),
                reader.GetString(26),
                reader.GetString(27),
                reader.GetString(28),
                reader.GetString(29),
                reader.GetString(30),
                reader.GetString(31),
                reader.GetString(32),
                reader.GetString(33),
                ReadJson<IReadOnlyList<string>>(reader.GetString(34)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(35)),
                ReadJson<ReferenceMaterialQueryPayload>(reader.GetString(36)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(37)),
                reader.GetString(38),
                ReadJson<IReadOnlyList<ReferenceSlotValuePayload>>(reader.GetString(39)),
                reader.GetString(40),
                reader.GetString(41),
                ReadJson<IReadOnlyList<string>>(reader.GetString(42)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(43))));
        }

        return beats;
    }

    private static async ValueTask<IReadOnlyDictionary<string, ReferenceBlueprintMaterialLinkPayload>> ReadSelectedMaterialLinksAsync(
        SqliteConnection connection,
        long novelId,
        long blueprintId,
        string analysisContractHash,
        IReadOnlyList<string> beatIds,
        CancellationToken cancellationToken)
    {
        if (beatIds.Count == 0)
        {
            return new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal);
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>(beatIds.Count);
        for (var index = 0; index < beatIds.Count; index++)
        {
            var parameterName = "$beat_id_" + index.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, beatIds[index]);
        }

        command.CommandText = $$"""
            SELECT l.link_id, l.blueprint_id, l.beat_id, l.material_id, l.intended_use,
                   l.max_rewrite_level, l.selected, l.score, l.score_components_json, l.fit_explanation, l.created_at
            FROM reference_blueprint_material_links l
            INNER JOIN reference_chapter_blueprints b ON b.blueprint_id = l.blueprint_id
            WHERE b.novel_id = $novel_id
              AND l.blueprint_id = $blueprint_id
              AND l.analysis_contract_hash = $analysis_contract_hash
              AND l.selected = 1
              AND l.status = 'active'
              AND l.beat_id IN ({{string.Join(", ", parameterNames)}});
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        command.Parameters.AddWithValue("$analysis_contract_hash", analysisContractHash);
        var linked = new Dictionary<string, ReferenceBlueprintMaterialLinkPayload>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var link = new ReferenceBlueprintMaterialLinkPayload(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6) != 0,
                reader.GetDouble(7),
                ReadJson<IReadOnlyDictionary<string, double>>(reader.GetString(8)),
                reader.GetString(9),
                ParseTimestamp(reader.GetString(10)));
            linked[link.BeatId] = link;
        }

        return linked;
    }

    private static async ValueTask ReplaceBlueprintMaterialLinksAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long blueprintId,
        IReadOnlyList<ScoredMaterialLink> links,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_blueprint_material_links WHERE blueprint_id = $blueprint_id;";
            delete.Parameters.AddWithValue("$blueprint_id", blueprintId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in links)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_blueprint_material_links
                  (link_id, blueprint_id, analysis_contract_hash, beat_id, material_id, intended_use, max_rewrite_level,
                   selected, score, score_components_json, fit_explanation, status, created_at)
                VALUES
                  ($link_id, $blueprint_id, $analysis_contract_hash, $beat_id, $material_id, $intended_use, $max_rewrite_level,
                   $selected, $score, $score_components_json, $fit_explanation, $status, $created_at);
                """;
            command.Parameters.AddWithValue("$link_id", item.Link.LinkId);
            command.Parameters.AddWithValue("$blueprint_id", item.Link.BlueprintId);
            command.Parameters.AddWithValue("$analysis_contract_hash", item.AnalysisContractHash);
            command.Parameters.AddWithValue("$beat_id", item.Link.BeatId);
            command.Parameters.AddWithValue("$material_id", item.Link.MaterialId);
            command.Parameters.AddWithValue("$intended_use", item.Link.IntendedUse);
            command.Parameters.AddWithValue("$max_rewrite_level", item.Link.MaxRewriteLevel);
            command.Parameters.AddWithValue("$selected", item.Link.Selected ? 1 : 0);
            command.Parameters.AddWithValue("$score", item.Link.Score);
            command.Parameters.AddWithValue("$score_components_json", JsonSerializer.Serialize(item.ScoreComponents, JsonOptions));
            command.Parameters.AddWithValue("$fit_explanation", item.Link.FitExplanation);
            command.Parameters.AddWithValue("$status", "active");
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(item.Link.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask MarkBlueprintMaterialLinksStaleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long blueprintId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_blueprint_material_links
            SET status = 'stale'
            WHERE blueprint_id = $blueprint_id AND status = 'active';
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask PersistDraftCandidatesAsync(
        long blueprintId,
        IReadOnlyList<ReferenceDraftParagraphCandidatePayload> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var candidate in candidates)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO reference_draft_paragraph_candidates
                      (candidate_id, blueprint_id, beat_id, material_id, rewrite_level, text,
                       changed_slots_json, non_slot_edits_json, audit_status, created_at)
                    VALUES
                      ($candidate_id, $blueprint_id, $beat_id, $material_id, $rewrite_level, $text,
                       $changed_slots_json, $non_slot_edits_json, $audit_status, $created_at);
                    """;
                command.Parameters.AddWithValue("$candidate_id", candidate.CandidateId);
                command.Parameters.AddWithValue("$blueprint_id", blueprintId);
                command.Parameters.AddWithValue("$beat_id", candidate.BeatId);
                command.Parameters.AddWithValue("$material_id", candidate.MaterialId);
                command.Parameters.AddWithValue("$rewrite_level", candidate.RewriteLevel);
                command.Parameters.AddWithValue("$text", candidate.Text);
                command.Parameters.AddWithValue("$changed_slots_json", JsonSerializer.Serialize(candidate.ChangedSlots, JsonOptions));
                command.Parameters.AddWithValue("$non_slot_edits_json", JsonSerializer.Serialize(candidate.NonSlotEdits, JsonOptions));
                command.Parameters.AddWithValue("$audit_status", candidate.AuditStatus);
                command.Parameters.AddWithValue("$created_at", FormatTimestamp(candidate.CreatedAt));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static async ValueTask<IReadOnlyList<ReferenceDraftParagraphCandidatePayload>> ReadDraftCandidatesAsync(
        SqliteConnection connection,
        long blueprintId,
        IReadOnlyList<string> candidateIds,
        CancellationToken cancellationToken)
    {
        if (candidateIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>(candidateIds.Count);
        for (var index = 0; index < candidateIds.Count; index++)
        {
            var parameterName = "$candidate_id_" + index.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, candidateIds[index]);
        }

        command.CommandText = $$"""
            SELECT candidate_id, blueprint_id, beat_id, material_id, rewrite_level, text,
                   changed_slots_json, non_slot_edits_json, audit_status, created_at
            FROM reference_draft_paragraph_candidates
            WHERE blueprint_id = $blueprint_id
              AND candidate_id IN ({{string.Join(", ", parameterNames)}})
            ORDER BY created_at ASC, candidate_id ASC;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        var candidates = new List<ReferenceDraftParagraphCandidatePayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new ReferenceDraftParagraphCandidatePayload(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ReadJson<IReadOnlyList<ReferenceSlotValuePayload>>(reader.GetString(6)),
                ReadJson<IReadOnlyList<string>>(reader.GetString(7)),
                reader.GetString(8),
                ParseTimestamp(reader.GetString(9))));
        }

        return candidates;
    }

    private static async ValueTask UpdateBlueprintAfterRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceChapterBlueprintPayload blueprint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_chapter_blueprints
            SET status = $status,
                analysis_contract_hash = $analysis_contract_hash,
                logic_analysis_json = $logic_analysis_json,
                emotion_analysis_json = $emotion_analysis_json,
                narration_analysis_json = $narration_analysis_json,
                character_analysis_json = $character_analysis_json,
                reference_analysis_json = $reference_analysis_json,
                transition_plan_json = $transition_plan_json,
                execution_contract_json = $execution_contract_json,
                known_facts_json = $known_facts_json,
                forbidden_facts_json = $forbidden_facts_json,
                approved_review_id = '',
                approved_at = '',
                updated_at = $updated_at
            WHERE blueprint_id = $blueprint_id;
            """;
        command.Parameters.AddWithValue("$status", blueprint.Status);
        command.Parameters.AddWithValue("$analysis_contract_hash", blueprint.AnalysisContractHash);
        command.Parameters.AddWithValue("$logic_analysis_json", JsonSerializer.Serialize(blueprint.LogicAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$emotion_analysis_json", JsonSerializer.Serialize(blueprint.EmotionAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$narration_analysis_json", JsonSerializer.Serialize(blueprint.NarrationAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$character_analysis_json", JsonSerializer.Serialize(blueprint.CharacterAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$reference_analysis_json", JsonSerializer.Serialize(blueprint.ReferenceAnalysis, JsonOptions));
        command.Parameters.AddWithValue("$transition_plan_json", JsonSerializer.Serialize(blueprint.TransitionPlan, JsonOptions));
        command.Parameters.AddWithValue("$execution_contract_json", JsonSerializer.Serialize(blueprint.ExecutionContract, JsonOptions));
        command.Parameters.AddWithValue("$known_facts_json", JsonSerializer.Serialize(blueprint.KnownFacts, JsonOptions));
        command.Parameters.AddWithValue("$forbidden_facts_json", JsonSerializer.Serialize(blueprint.ForbiddenFacts, JsonOptions));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(blueprint.UpdatedAt));
        command.Parameters.AddWithValue("$blueprint_id", blueprint.BlueprintId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertRevisionRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long blueprintId,
        IReadOnlyList<BlueprintRevisionRow> rows,
        CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_chapter_blueprint_revisions
                  (revision_id, blueprint_id, parent_blueprint_id, changed_field_path,
                   previous_value_hash, new_value_hash, origin, revision_reason,
                   invalidated_review_id, created_at)
                VALUES
                  ($revision_id, $blueprint_id, 0, $changed_field_path,
                   $previous_value_hash, $new_value_hash, $origin, $revision_reason,
                   $invalidated_review_id, $created_at);
                """;
            command.Parameters.AddWithValue("$revision_id", row.RevisionId);
            command.Parameters.AddWithValue("$blueprint_id", blueprintId);
            command.Parameters.AddWithValue("$changed_field_path", row.ChangedFieldPath);
            command.Parameters.AddWithValue("$previous_value_hash", row.PreviousValueHash);
            command.Parameters.AddWithValue("$new_value_hash", row.NewValueHash);
            command.Parameters.AddWithValue("$origin", row.Origin);
            command.Parameters.AddWithValue("$revision_reason", row.RevisionReason);
            command.Parameters.AddWithValue("$invalidated_review_id", row.InvalidatedReviewId);
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(row.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask InsertReviewAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceChapterBlueprintReviewPayload review,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_chapter_blueprint_reviews
              (review_id, blueprint_id, context_hash, source_plan_hash, analysis_contract_hash, review_version,
               status, score, logic_errors_json, causality_errors_json,
               emotion_errors_json, narration_errors_json, execution_errors_json, character_state_errors_json, pov_errors_json,
               continuity_errors_json, transition_errors_json, forbidden_fact_errors_json,
               reference_binding_errors_json, material_fit_errors_json, screenplay_drift_risks_json,
               ai_prose_risks_json, novelistic_narration_errors_json, required_fixes_json, defects_json, reviewed_at)
            VALUES
              ($review_id, $blueprint_id, $context_hash, $source_plan_hash, $analysis_contract_hash, $review_version,
               $status, $score, $logic_errors_json, $causality_errors_json,
               $emotion_errors_json, $narration_errors_json, $execution_errors_json, $character_state_errors_json, $pov_errors_json,
               $continuity_errors_json, $transition_errors_json, $forbidden_fact_errors_json,
               $reference_binding_errors_json, $material_fit_errors_json, $screenplay_drift_risks_json,
               $ai_prose_risks_json, $novelistic_narration_errors_json, $required_fixes_json, $defects_json, $reviewed_at);
            """;
        AddReviewParameters(command, review);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertBlueprintApprovalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long blueprintId,
        ReferenceChapterBlueprintReviewPayload review,
        string approverOrigin,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_chapter_blueprint_approvals
              (approval_id, blueprint_id, review_id, context_hash, source_plan_hash,
               analysis_contract_hash, review_version, approver_origin, approved_at)
            VALUES
              ($approval_id, $blueprint_id, $review_id, $context_hash, $source_plan_hash,
               $analysis_contract_hash, $review_version, $approver_origin, $approved_at);
            """;
        command.Parameters.AddWithValue("$approval_id", "approval-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        command.Parameters.AddWithValue("$review_id", review.ReviewId);
        command.Parameters.AddWithValue("$context_hash", review.ContextHash);
        command.Parameters.AddWithValue("$source_plan_hash", review.SourcePlanHash);
        command.Parameters.AddWithValue("$analysis_contract_hash", review.AnalysisContractHash);
        command.Parameters.AddWithValue("$review_version", review.ReviewVersion);
        command.Parameters.AddWithValue("$approver_origin", approverOrigin);
        command.Parameters.AddWithValue("$approved_at", FormatTimestamp(approvedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<ReferenceChapterBlueprintReviewPayload?> ReadLatestReviewAsync(
        SqliteConnection connection,
        long blueprintId,
        string analysisContractHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT review_id, blueprint_id, context_hash, source_plan_hash, analysis_contract_hash, review_version,
                   status, score, logic_errors_json, causality_errors_json,
                   emotion_errors_json, narration_errors_json, execution_errors_json, character_state_errors_json, pov_errors_json,
                   continuity_errors_json, transition_errors_json, forbidden_fact_errors_json,
                   reference_binding_errors_json, material_fit_errors_json, screenplay_drift_risks_json,
                   ai_prose_risks_json, novelistic_narration_errors_json, required_fixes_json, defects_json, reviewed_at
            FROM reference_chapter_blueprint_reviews
            WHERE blueprint_id = $blueprint_id
              AND analysis_contract_hash = $analysis_contract_hash
            ORDER BY reviewed_at DESC, review_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        command.Parameters.AddWithValue("$analysis_contract_hash", analysisContractHash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReview(reader) : null;
    }

    private static async ValueTask<ReferenceChapterBlueprintReviewPayload?> ReadReviewAsync(
        SqliteConnection connection,
        long blueprintId,
        string reviewId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT review_id, blueprint_id, context_hash, source_plan_hash, analysis_contract_hash, review_version,
                   status, score, logic_errors_json, causality_errors_json,
                   emotion_errors_json, narration_errors_json, execution_errors_json, character_state_errors_json, pov_errors_json,
                   continuity_errors_json, transition_errors_json, forbidden_fact_errors_json,
                   reference_binding_errors_json, material_fit_errors_json, screenplay_drift_risks_json,
                   ai_prose_risks_json, novelistic_narration_errors_json, required_fixes_json, defects_json, reviewed_at
            FROM reference_chapter_blueprint_reviews
            WHERE blueprint_id = $blueprint_id AND review_id = $review_id;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        command.Parameters.AddWithValue("$review_id", reviewId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadReview(reader) : null;
    }

    private static ReferenceChapterBlueprintReviewPayload ReadReview(SqliteDataReader reader)
    {
        return new ReferenceChapterBlueprintReviewPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetDouble(7),
            ReadJson<IReadOnlyList<string>>(reader.GetString(8)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(9)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(10)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(11)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(12)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(13)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(14)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(15)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(16)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(17)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(18)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(19)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(20)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(21)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(22)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(23)),
            ReadJson<IReadOnlyList<ReferenceChapterBlueprintReviewDefectPayload>>(reader.GetString(24)),
            ParseTimestamp(reader.GetString(25)));
    }

    private static void AddReviewParameters(SqliteCommand command, ReferenceChapterBlueprintReviewPayload review)
    {
        command.Parameters.AddWithValue("$review_id", review.ReviewId);
        command.Parameters.AddWithValue("$blueprint_id", review.BlueprintId);
        command.Parameters.AddWithValue("$context_hash", review.ContextHash);
        command.Parameters.AddWithValue("$source_plan_hash", review.SourcePlanHash);
        command.Parameters.AddWithValue("$analysis_contract_hash", review.AnalysisContractHash);
        command.Parameters.AddWithValue("$review_version", review.ReviewVersion);
        command.Parameters.AddWithValue("$status", review.Status);
        command.Parameters.AddWithValue("$score", review.Score);
        command.Parameters.AddWithValue("$logic_errors_json", JsonSerializer.Serialize(review.LogicErrors, JsonOptions));
        command.Parameters.AddWithValue("$causality_errors_json", JsonSerializer.Serialize(review.CausalityErrors, JsonOptions));
        command.Parameters.AddWithValue("$emotion_errors_json", JsonSerializer.Serialize(review.EmotionErrors, JsonOptions));
        command.Parameters.AddWithValue("$narration_errors_json", JsonSerializer.Serialize(review.NarrationErrors, JsonOptions));
        command.Parameters.AddWithValue("$execution_errors_json", JsonSerializer.Serialize(review.ExecutionErrors, JsonOptions));
        command.Parameters.AddWithValue("$character_state_errors_json", JsonSerializer.Serialize(review.CharacterStateErrors, JsonOptions));
        command.Parameters.AddWithValue("$pov_errors_json", JsonSerializer.Serialize(review.PovErrors, JsonOptions));
        command.Parameters.AddWithValue("$continuity_errors_json", JsonSerializer.Serialize(review.ContinuityErrors, JsonOptions));
        command.Parameters.AddWithValue("$transition_errors_json", JsonSerializer.Serialize(review.TransitionErrors, JsonOptions));
        command.Parameters.AddWithValue("$forbidden_fact_errors_json", JsonSerializer.Serialize(review.ForbiddenFactErrors, JsonOptions));
        command.Parameters.AddWithValue("$reference_binding_errors_json", JsonSerializer.Serialize(review.ReferenceBindingErrors, JsonOptions));
        command.Parameters.AddWithValue("$material_fit_errors_json", JsonSerializer.Serialize(review.MaterialFitErrors, JsonOptions));
        command.Parameters.AddWithValue("$screenplay_drift_risks_json", JsonSerializer.Serialize(review.ScreenplayDriftRisks, JsonOptions));
        command.Parameters.AddWithValue("$ai_prose_risks_json", JsonSerializer.Serialize(review.AiProseRisks, JsonOptions));
        command.Parameters.AddWithValue("$novelistic_narration_errors_json", JsonSerializer.Serialize(review.NovelisticNarrationErrors, JsonOptions));
        command.Parameters.AddWithValue("$required_fixes_json", JsonSerializer.Serialize(review.RequiredFixes, JsonOptions));
        command.Parameters.AddWithValue("$defects_json", JsonSerializer.Serialize(review.Defects, JsonOptions));
        command.Parameters.AddWithValue("$reviewed_at", FormatTimestamp(review.ReviewedAt));
    }

    private static async ValueTask UpdateBlueprintStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long blueprintId,
        string status,
        string approvedReviewId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_chapter_blueprints
            SET status = $status,
                updated_at = $updated_at,
                approved_at = CASE WHEN $status = 'approved' THEN $updated_at ELSE approved_at END,
                approved_review_id = CASE
                    WHEN $status = 'approved' OR $status = 'material_bound' THEN $approved_review_id
                    ELSE approved_review_id
                END
            WHERE blueprint_id = $blueprint_id;
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$approved_review_id", approvedReviewId);
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertOrchestrationRunAsync(
        SqliteConnection connection,
        ReferenceOrchestrationRunPayload run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_orchestration_runs
              (run_id, novel_id, chapter_number, status, stage, chapter_goal,
               known_facts_json, forbidden_facts_json, anchor_ids_json, corpus_search_policy_json,
               blueprint_id, review_id, candidate_ids_json, current_decision_json,
               last_stop_reason, error_message, created_at, updated_at)
            VALUES
              ($run_id, $novel_id, $chapter_number, $status, $stage, $chapter_goal,
               $known_facts_json, $forbidden_facts_json, $anchor_ids_json, $corpus_search_policy_json,
               $blueprint_id, $review_id, $candidate_ids_json, $current_decision_json,
               $last_stop_reason, $error_message, $created_at, $updated_at);
            """;
        AddOrchestrationRunParameters(command, run);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask UpdateOrchestrationRunAsync(
        SqliteConnection connection,
        ReferenceOrchestrationRunPayload run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_orchestration_runs
            SET status = $status,
                stage = $stage,
                chapter_goal = $chapter_goal,
                known_facts_json = $known_facts_json,
                forbidden_facts_json = $forbidden_facts_json,
                anchor_ids_json = $anchor_ids_json,
                corpus_search_policy_json = $corpus_search_policy_json,
                blueprint_id = $blueprint_id,
                review_id = $review_id,
                candidate_ids_json = $candidate_ids_json,
                current_decision_json = $current_decision_json,
                last_stop_reason = $last_stop_reason,
                error_message = $error_message,
                updated_at = $updated_at
            WHERE novel_id = $novel_id AND run_id = $run_id;
            """;
        AddOrchestrationRunParameters(command, run);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddOrchestrationRunParameters(
        SqliteCommand command,
        ReferenceOrchestrationRunPayload run)
    {
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$novel_id", run.NovelId);
        command.Parameters.AddWithValue("$chapter_number", run.ChapterNumber);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$stage", run.Stage);
        command.Parameters.AddWithValue("$chapter_goal", run.ChapterGoal);
        command.Parameters.AddWithValue("$known_facts_json", JsonSerializer.Serialize(run.KnownFacts, JsonOptions));
        command.Parameters.AddWithValue("$forbidden_facts_json", JsonSerializer.Serialize(run.ForbiddenFacts, JsonOptions));
        command.Parameters.AddWithValue("$anchor_ids_json", JsonSerializer.Serialize(run.AnchorIds, JsonOptions));
        command.Parameters.AddWithValue("$corpus_search_policy_json", JsonSerializer.Serialize(run.CorpusSearchPolicy, JsonOptions));
        command.Parameters.AddWithValue("$blueprint_id", run.BlueprintId);
        command.Parameters.AddWithValue("$review_id", run.ReviewId);
        command.Parameters.AddWithValue("$candidate_ids_json", JsonSerializer.Serialize(run.CandidateIds, JsonOptions));
        command.Parameters.AddWithValue("$current_decision_json", run.CurrentDecision is null ? string.Empty : JsonSerializer.Serialize(run.CurrentDecision, JsonOptions));
        command.Parameters.AddWithValue("$last_stop_reason", run.LastStopReason);
        command.Parameters.AddWithValue("$error_message", run.ErrorMessage);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(run.CreatedAt));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(run.UpdatedAt));
    }

    private static async ValueTask<IReadOnlyList<ReferenceOrchestrationRunPayload>> ReadOrchestrationRunsAsync(
        SqliteConnection connection,
        long novelId,
        int? chapterNumber,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = chapterNumber is null
            ? """
              SELECT run_id, novel_id, chapter_number, status, stage, chapter_goal,
                     known_facts_json, forbidden_facts_json, anchor_ids_json, corpus_search_policy_json,
                     blueprint_id, review_id, candidate_ids_json, current_decision_json,
                     last_stop_reason, error_message, created_at, updated_at
              FROM reference_orchestration_runs
              WHERE novel_id = $novel_id
              ORDER BY updated_at DESC, run_id DESC;
              """
            : """
              SELECT run_id, novel_id, chapter_number, status, stage, chapter_goal,
                     known_facts_json, forbidden_facts_json, anchor_ids_json, corpus_search_policy_json,
                     blueprint_id, review_id, candidate_ids_json, current_decision_json,
                     last_stop_reason, error_message, created_at, updated_at
              FROM reference_orchestration_runs
              WHERE novel_id = $novel_id AND chapter_number = $chapter_number
              ORDER BY updated_at DESC, run_id DESC;
              """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        if (chapterNumber is not null)
        {
            command.Parameters.AddWithValue("$chapter_number", chapterNumber.Value);
        }

        var runs = new List<ReferenceOrchestrationRunPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(ReadOrchestrationRun(reader));
        }

        return runs;
    }

    private static async ValueTask<ReferenceOrchestrationRunPayload?> ReadOrchestrationRunAsync(
        SqliteConnection connection,
        long novelId,
        string runId,
        CancellationToken cancellationToken,
        bool required)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, novel_id, chapter_number, status, stage, chapter_goal,
                   known_facts_json, forbidden_facts_json, anchor_ids_json, corpus_search_policy_json,
                   blueprint_id, review_id, candidate_ids_json, current_decision_json,
                   last_stop_reason, error_message, created_at, updated_at
            FROM reference_orchestration_runs
            WHERE novel_id = $novel_id AND run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadOrchestrationRun(reader);
        }

        if (required)
        {
            throw new ArgumentException("Orchestration run does not exist.", nameof(runId));
        }

        return null;
    }

    private static ReferenceOrchestrationRunPayload ReadOrchestrationRun(SqliteDataReader reader)
    {
        var decisionJson = reader.GetString(13);
        return new ReferenceOrchestrationRunPayload(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            ReadJson<IReadOnlyList<string>>(reader.GetString(6)),
            ReadJson<IReadOnlyList<string>>(reader.GetString(7)),
            ReadJson<IReadOnlyList<long>>(reader.GetString(8)),
            ReadJson<ReferenceCorpusSearchPolicyPayload>(reader.GetString(9)),
            reader.GetInt64(10),
            reader.GetString(11),
            ReadJson<IReadOnlyList<string>>(reader.GetString(12)),
            string.IsNullOrWhiteSpace(decisionJson)
                ? null
                : ReadJson<ReferenceOrchestrationRequiredDecisionPayload>(decisionJson),
            reader.GetString(14),
            reader.GetString(15),
            ParseTimestamp(reader.GetString(16)),
            ParseTimestamp(reader.GetString(17)));
    }

    private async ValueTask EnsureSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS reference_chapter_blueprints (
              blueprint_id INTEGER PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              chapter_number INTEGER NOT NULL,
              primary_anchor_id INTEGER NOT NULL,
              title TEXT NOT NULL,
              status TEXT NOT NULL,
              source_plan_scope TEXT NOT NULL,
              source_plan_hash TEXT NOT NULL,
              context_hash TEXT NOT NULL,
              analysis_contract_hash TEXT NOT NULL,
              blueprint_version INTEGER NOT NULL,
              parent_blueprint_id INTEGER NOT NULL,
              chapter_function TEXT NOT NULL,
              logic_analysis_json TEXT NOT NULL,
              emotion_analysis_json TEXT NOT NULL,
              narration_analysis_json TEXT NOT NULL,
              character_analysis_json TEXT NOT NULL,
              reference_analysis_json TEXT NOT NULL,
              transition_plan_json TEXT NOT NULL,
              execution_contract_json TEXT NOT NULL,
              previous_state TEXT NOT NULL,
              final_state TEXT NOT NULL,
              final_hook TEXT NOT NULL,
              global_pov TEXT NOT NULL,
              global_narrative_distance TEXT NOT NULL,
              known_facts_json TEXT NOT NULL,
              forbidden_facts_json TEXT NOT NULL,
              risk_flags_json TEXT NOT NULL,
              build_version TEXT NOT NULL,
              approved_review_id TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              approved_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reference_chapter_blueprint_beats (
              beat_id TEXT PRIMARY KEY,
              blueprint_id INTEGER NOT NULL,
              beat_index INTEGER NOT NULL,
              scene_index INTEGER NOT NULL,
              beat_type TEXT NOT NULL,
              narrative_function TEXT NOT NULL,
              logic_premise TEXT NOT NULL,
              conflict_pressure TEXT NOT NULL,
              causality_in TEXT NOT NULL,
              causality_out TEXT NOT NULL,
              transition_in TEXT NOT NULL,
              transition_out TEXT NOT NULL,
              pov_character TEXT NOT NULL,
              narrative_distance TEXT NOT NULL,
              viewpoint_allowed_knowledge_json TEXT NOT NULL,
              viewpoint_forbidden_knowledge_json TEXT NOT NULL,
              character_states_before_json TEXT NOT NULL,
              character_states_after_json TEXT NOT NULL,
              character_goals_json TEXT NOT NULL,
              character_misbeliefs_json TEXT NOT NULL,
              relationship_pressure_json TEXT NOT NULL,
              emotion_trigger TEXT NOT NULL,
              emotion_before TEXT NOT NULL,
              emotion_after TEXT NOT NULL,
              suppressed_reaction TEXT NOT NULL,
              external_evidence TEXT NOT NULL,
              narration_strategy TEXT NOT NULL,
              rhythm_strategy TEXT NOT NULL,
              paragraph_intention TEXT NOT NULL,
              execution_mode TEXT NOT NULL,
              anti_screenplay_duty TEXT NOT NULL,
              sensory_anchor_target TEXT NOT NULL,
              subtext_plan TEXT NOT NULL,
              source_backed_detail_target TEXT NOT NULL,
              candidate_rejection_rule TEXT NOT NULL,
              scene_facts_json TEXT NOT NULL,
              forbidden_facts_json TEXT NOT NULL,
              reference_query_json TEXT NOT NULL,
              required_material_types_json TEXT NOT NULL,
              max_rewrite_level TEXT NOT NULL,
              slot_plan_json TEXT NOT NULL,
              locked_phrase_policy TEXT NOT NULL,
              no_reuse_reason TEXT NOT NULL,
              prose_duties_json TEXT NOT NULL,
              risk_flags_json TEXT NOT NULL,
              FOREIGN KEY(blueprint_id) REFERENCES reference_chapter_blueprints(blueprint_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_chapter_blueprint_reviews (
              review_id TEXT PRIMARY KEY,
              blueprint_id INTEGER NOT NULL,
              context_hash TEXT NOT NULL,
              source_plan_hash TEXT NOT NULL,
              analysis_contract_hash TEXT NOT NULL,
              review_version INTEGER NOT NULL,
              status TEXT NOT NULL,
              score REAL NOT NULL,
              logic_errors_json TEXT NOT NULL,
              causality_errors_json TEXT NOT NULL,
              emotion_errors_json TEXT NOT NULL,
              narration_errors_json TEXT NOT NULL,
              execution_errors_json TEXT NOT NULL,
              character_state_errors_json TEXT NOT NULL,
              pov_errors_json TEXT NOT NULL,
              continuity_errors_json TEXT NOT NULL,
              transition_errors_json TEXT NOT NULL,
              forbidden_fact_errors_json TEXT NOT NULL,
              reference_binding_errors_json TEXT NOT NULL,
              material_fit_errors_json TEXT NOT NULL,
              screenplay_drift_risks_json TEXT NOT NULL,
              ai_prose_risks_json TEXT NOT NULL,
              novelistic_narration_errors_json TEXT NOT NULL,
              required_fixes_json TEXT NOT NULL,
              defects_json TEXT NOT NULL,
              reviewed_at TEXT NOT NULL,
              FOREIGN KEY(blueprint_id) REFERENCES reference_chapter_blueprints(blueprint_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_chapter_blueprint_approvals (
              approval_id TEXT PRIMARY KEY,
              blueprint_id INTEGER NOT NULL,
              review_id TEXT NOT NULL,
              context_hash TEXT NOT NULL,
              source_plan_hash TEXT NOT NULL,
              analysis_contract_hash TEXT NOT NULL,
              review_version INTEGER NOT NULL,
              approver_origin TEXT NOT NULL,
              approved_at TEXT NOT NULL,
              FOREIGN KEY(blueprint_id) REFERENCES reference_chapter_blueprints(blueprint_id) ON DELETE CASCADE,
              FOREIGN KEY(review_id) REFERENCES reference_chapter_blueprint_reviews(review_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_chapter_blueprint_revisions (
              revision_id TEXT PRIMARY KEY,
              blueprint_id INTEGER NOT NULL,
              parent_blueprint_id INTEGER NOT NULL,
              changed_field_path TEXT NOT NULL,
              previous_value_hash TEXT NOT NULL,
              new_value_hash TEXT NOT NULL,
              origin TEXT NOT NULL,
              revision_reason TEXT NOT NULL,
              invalidated_review_id TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(blueprint_id) REFERENCES reference_chapter_blueprints(blueprint_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_blueprint_material_links (
              link_id TEXT PRIMARY KEY,
              blueprint_id INTEGER NOT NULL,
              analysis_contract_hash TEXT NOT NULL,
              beat_id TEXT NOT NULL,
              material_id TEXT NOT NULL,
              intended_use TEXT NOT NULL,
              max_rewrite_level TEXT NOT NULL,
              selected INTEGER NOT NULL,
              score REAL NOT NULL,
              score_components_json TEXT NOT NULL,
              fit_explanation TEXT NOT NULL,
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(blueprint_id) REFERENCES reference_chapter_blueprints(blueprint_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_draft_paragraph_candidates (
              candidate_id TEXT PRIMARY KEY,
              blueprint_id INTEGER NOT NULL,
              beat_id TEXT NOT NULL,
              material_id TEXT NOT NULL,
              rewrite_level TEXT NOT NULL,
              text TEXT NOT NULL,
              changed_slots_json TEXT NOT NULL,
              non_slot_edits_json TEXT NOT NULL,
              audit_status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              FOREIGN KEY(blueprint_id) REFERENCES reference_chapter_blueprints(blueprint_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS reference_orchestration_runs (
              run_id TEXT PRIMARY KEY,
              novel_id INTEGER NOT NULL,
              chapter_number INTEGER NOT NULL,
              status TEXT NOT NULL,
              stage TEXT NOT NULL,
              chapter_goal TEXT NOT NULL,
              known_facts_json TEXT NOT NULL,
              forbidden_facts_json TEXT NOT NULL,
              anchor_ids_json TEXT NOT NULL,
              corpus_search_policy_json TEXT NOT NULL,
              blueprint_id INTEGER NOT NULL,
              review_id TEXT NOT NULL,
              candidate_ids_json TEXT NOT NULL,
              current_decision_json TEXT NOT NULL,
              last_stop_reason TEXT NOT NULL,
              error_message TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_reference_blueprints_novel_chapter
              ON reference_chapter_blueprints(novel_id, chapter_number);

            CREATE INDEX IF NOT EXISTS idx_reference_blueprint_beats_blueprint
              ON reference_chapter_blueprint_beats(blueprint_id, beat_index);

            CREATE INDEX IF NOT EXISTS idx_reference_blueprint_reviews_blueprint
              ON reference_chapter_blueprint_reviews(blueprint_id, reviewed_at);

            CREATE INDEX IF NOT EXISTS idx_reference_blueprint_approvals_blueprint
              ON reference_chapter_blueprint_approvals(blueprint_id, approved_at);

            CREATE INDEX IF NOT EXISTS idx_reference_blueprint_revisions_blueprint
              ON reference_chapter_blueprint_revisions(blueprint_id, created_at);

            CREATE INDEX IF NOT EXISTS idx_reference_blueprint_links_beat
              ON reference_blueprint_material_links(beat_id);

            CREATE INDEX IF NOT EXISTS idx_reference_draft_candidates_blueprint
              ON reference_draft_paragraph_candidates(blueprint_id, beat_id, created_at);

            CREATE INDEX IF NOT EXISTS idx_reference_orchestration_runs_novel_chapter
              ON reference_orchestration_runs(novel_id, chapter_number, updated_at);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprints",
            "analysis_contract_hash",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprints",
            "reference_analysis_json",
            "TEXT NOT NULL DEFAULT '{\"track\":\"reference\",\"summary\":\"legacy reference analysis\",\"points\":[]}'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprints",
            "execution_contract_json",
            "TEXT NOT NULL DEFAULT '{\"track\":\"execution\",\"summary\":\"legacy execution contract\",\"paragraph_intentions\":[],\"execution_modes\":[],\"anti_screenplay_duties\":[],\"source_backed_detail_targets\":[],\"candidate_rejection_rules\":[]}'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprints",
            "approved_review_id",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_beats",
            "paragraph_intention",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_beats",
            "execution_mode",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_beats",
            "anti_screenplay_duty",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_beats",
            "sensory_anchor_target",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_beats",
            "subtext_plan",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_beats",
            "source_backed_detail_target",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_beats",
            "candidate_rejection_rule",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_reviews",
            "context_hash",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_reviews",
            "source_plan_hash",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_reviews",
            "analysis_contract_hash",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_reviews",
            "review_version",
            "INTEGER NOT NULL DEFAULT 1",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_reviews",
            "execution_errors_json",
            "TEXT NOT NULL DEFAULT '[]'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_reviews",
            "novelistic_narration_errors_json",
            "TEXT NOT NULL DEFAULT '[]'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_chapter_blueprint_reviews",
            "defects_json",
            "TEXT NOT NULL DEFAULT '[]'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_blueprint_material_links",
            "analysis_contract_hash",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_blueprint_material_links",
            "score_components_json",
            "TEXT NOT NULL DEFAULT '{}'",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_blueprint_material_links",
            "fit_explanation",
            "TEXT NOT NULL DEFAULT ''",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "reference_blueprint_material_links",
            "status",
            "TEXT NOT NULL DEFAULT 'active'",
            cancellationToken);
    }

    private static async ValueTask EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition,
        CancellationToken cancellationToken)
    {
        await using (var inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA table_info(" + tableName + ");";
            await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE " + tableName + " ADD COLUMN " + columnName + " " + columnDefinition + ";";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException($"Novel '{novelId}' does not exist.", nameof(novelId));
        }
    }

    private async ValueTask<string> DatabasePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "reference-anchor",
            "index.sqlite");
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static IReadOnlyList<string> NormalizeList(IReadOnlyList<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
    }

    private static string NormalizeApproverOrigin(string? origin)
    {
        return string.IsNullOrWhiteSpace(origin) ? "user" : origin.Trim();
    }

    private static IReadOnlyList<long> NormalizeAnchorIds(IReadOnlyList<long>? values)
    {
        return values?
            .Where(value => value > 0)
            .Distinct()
            .ToArray() ?? [];
    }

    private static ReferenceCorpusSearchPolicyPayload NormalizeCorpusSearchPolicy(
        ReferenceCorpusSearchPolicyPayload? policy)
    {
        if (policy is null)
        {
            return new ReferenceCorpusSearchPolicyPayload(
                "story_context",
                MaxResultsPerBeat: 3,
                LicenseStatuses: ["user_provided"],
                IncludeAnchorIds: [],
                ExcludeAnchorIds: []);
        }

        var mode = NormalizeOptional(policy.Mode, "story_context", 80);
        var maxResultsPerBeat = Math.Clamp(policy.MaxResultsPerBeat <= 0 ? 3 : policy.MaxResultsPerBeat, 1, 20);
        var licenseStatuses = NormalizeList(policy.LicenseStatuses);
        return new ReferenceCorpusSearchPolicyPayload(
            mode,
            maxResultsPerBeat,
            licenseStatuses.Count == 0 ? ["user_provided"] : licenseStatuses,
            NormalizeAnchorIds(policy.IncludeAnchorIds),
            NormalizeAnchorIds(policy.ExcludeAnchorIds));
    }

    private static string NormalizeRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("Orchestration run id is required.", nameof(runId));
        }

        return runId.Trim();
    }

    private static string NormalizeOrchestrationDecisionType(string? decisionType)
    {
        var normalized = NormalizeOptional(decisionType, string.Empty, 120);
        if (!ReferenceOrchestrationDecisionTypes.All.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("Unsupported orchestration decision type.", nameof(decisionType));
        }

        return normalized;
    }

    private static string NextStageAfterDecision(string decisionType)
    {
        return decisionType switch
        {
            ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts => ReferenceOrchestrationStages.BlueprintGeneration,
            ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision => ReferenceOrchestrationStages.BlueprintReview,
            ReferenceOrchestrationDecisionTypes.ApproveBlueprint => ReferenceOrchestrationStages.MaterialBinding,
            ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion => ReferenceOrchestrationStages.FinalInsertion,
            _ => ReferenceOrchestrationStages.SourceConfirmation
        };
    }

    private static bool ShouldRunOrchestrationSafeStages(ReferenceOrchestrationRunPayload run)
    {
        return string.Equals(run.Status, ReferenceOrchestrationRunStatuses.Running, StringComparison.Ordinal) &&
            run.CurrentDecision is null &&
            (string.Equals(run.Stage, ReferenceOrchestrationStages.BlueprintGeneration, StringComparison.Ordinal) ||
                string.Equals(run.Stage, ReferenceOrchestrationStages.MaterialBinding, StringComparison.Ordinal));
    }

    private static ReferenceOrchestrationRunPayload BuildPostReviewOrchestrationRun(
        ReferenceOrchestrationRunPayload run,
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewPayload review)
    {
        var reviewPassed = string.Equals(review.Status, ReferenceBlueprintReviewStatuses.Passed, StringComparison.Ordinal);
        var stopReason = reviewPassed
            ? ReferenceOrchestrationStopReasons.BlueprintApprovalRequired
            : ReferenceOrchestrationStopReasons.BlueprintRevisionApprovalRequired;
        return run with
        {
            Status = ReferenceOrchestrationRunStatuses.WaitingForUser,
            Stage = reviewPassed
                ? ReferenceOrchestrationStages.BlueprintApproval
                : ReferenceOrchestrationStages.BlueprintReview,
            BlueprintId = blueprint.BlueprintId,
            ReviewId = review.ReviewId,
            CurrentDecision = reviewPassed
                ? BuildBlueprintApprovalDecision(blueprint, review)
                : BuildBlueprintRevisionDecision(blueprint, review),
            LastStopReason = stopReason,
            ErrorMessage = string.Empty,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ReferenceOrchestrationRunPayload BuildPostDraftAuditOrchestrationRun(
        ReferenceOrchestrationRunPayload run,
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceAnchoredDraftAuditPayload audit)
    {
        var auditPassed = string.Equals(audit.Status, "passed", StringComparison.Ordinal);
        if (auditPassed)
        {
            return run with
            {
                Status = ReferenceOrchestrationRunStatuses.WaitingForUser,
                Stage = ReferenceOrchestrationStages.FinalInsertion,
                CurrentDecision = BuildFinalInsertionDecision(blueprint, audit),
                LastStopReason = ReferenceOrchestrationStopReasons.FinalInsertionRequired,
                ErrorMessage = string.Empty,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        return run with
        {
            Status = ReferenceOrchestrationRunStatuses.Failed,
            Stage = ReferenceOrchestrationStages.DraftAudit,
            CurrentDecision = null,
            LastStopReason = ReferenceOrchestrationStopReasons.DraftAuditFailed,
            ErrorMessage = BuildDraftAuditFailureMessage(audit),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static ReferenceOrchestrationRequiredDecisionPayload BuildSourceConfirmationDecision(
        string chapterGoal,
        IReadOnlyList<string> knownFacts,
        IReadOnlyList<string> forbiddenFacts,
        ReferenceCorpusSearchPolicyPayload policy)
    {
        var summary = "Confirm source trust and chapter fact boundaries before reference orchestration runs safe stages.";
        var factBoundaryChanges = knownFacts
            .Select(fact => "known: " + fact)
            .Concat(forbiddenFacts.Select(fact => "forbidden: " + fact))
            .Take(20)
            .ToArray();
        return new ReferenceOrchestrationRequiredDecisionPayload(
            ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts,
            ReferenceOrchestrationStopReasons.SourceConfirmationRequired,
            summary,
            ["confirm_source", "confirm_known_facts", "confirm_forbidden_facts"],
            new ReferenceOrchestrationApprovalSummaryPayload(
                string.IsNullOrWhiteSpace(chapterGoal) ? "chapter goal not provided" : chapterGoal,
                "not selected",
                factBoundaryChanges,
                "pending blueprint generation",
                "search policy: " + policy.Mode,
                "L2",
                []));
    }

    private static ReferenceOrchestrationRequiredDecisionPayload BuildBlueprintApprovalDecision(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewPayload review)
    {
        return new ReferenceOrchestrationRequiredDecisionPayload(
            ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
            ReferenceOrchestrationStopReasons.BlueprintApprovalRequired,
            "Deterministic blueprint review passed. Approve the compact blueprint and risk summary before material binding and draft generation.",
            ["inspect_blueprint_summary", "approve_blueprint"],
            BuildBlueprintApprovalSummary(blueprint, review));
    }

    private static ReferenceOrchestrationRequiredDecisionPayload BuildBlueprintRevisionDecision(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewPayload review)
    {
        return new ReferenceOrchestrationRequiredDecisionPayload(
            ReferenceOrchestrationDecisionTypes.ApplyBlueprintRevision,
            ReferenceOrchestrationStopReasons.BlueprintRevisionApprovalRequired,
            "Deterministic blueprint review failed. Inspect required fixes and approve a blueprint revision before orchestration can continue.",
            ["inspect_review", "revise_blueprint", "approve_blueprint_revision"],
            BuildBlueprintApprovalSummary(blueprint, review));
    }

    private static ReferenceOrchestrationRequiredDecisionPayload BuildFinalInsertionDecision(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceAnchoredDraftAuditPayload audit)
    {
        return new ReferenceOrchestrationRequiredDecisionPayload(
            ReferenceOrchestrationDecisionTypes.ApproveFinalInsertion,
            ReferenceOrchestrationStopReasons.FinalInsertionRequired,
            "Draft candidates passed deterministic audit. Review or edit the candidates before final chapter insertion.",
            ["review_candidates", "edit_or_select_candidate", "approve_final_insertion"],
            BuildDraftAuditApprovalSummary(blueprint, audit));
    }

    private static ReferenceOrchestrationApprovalSummaryPayload BuildBlueprintApprovalSummary(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceChapterBlueprintReviewPayload review)
    {
        var factBoundaryChanges = blueprint.KnownFacts
            .Select(fact => "known: " + fact)
            .Concat(blueprint.ForbiddenFacts.Select(fact => "forbidden: " + fact))
            .Take(20)
            .ToArray();
        var emotionalTrajectory = blueprint.Beats.Count == 0
            ? blueprint.EmotionAnalysis.Summary
            : blueprint.Beats[0].EmotionBefore + " -> " + blueprint.Beats[^1].EmotionAfter;
        var materialTypes = blueprint.Beats
            .SelectMany(beat => beat.RequiredMaterialTypes)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var materialUsePlan = materialTypes.Length == 0
            ? blueprint.ReferenceAnalysis.Summary
            : blueprint.ReferenceAnalysis.Summary + "; required material types: " + string.Join(", ", materialTypes);
        var rewriteBudget = blueprint.Beats
            .Select(beat => beat.MaxRewriteLevel)
            .OrderByDescending(RewriteLevelRank)
            .FirstOrDefault() ?? ReferenceRewriteLevels.L0;
        var highRiskFindings = review.Defects
            .Select(defect => string.IsNullOrWhiteSpace(defect.BeatId)
                ? $"{defect.Category}: {defect.Reason}"
                : $"{defect.Category}:{defect.BeatId}: {defect.Reason}")
            .Concat(review.ScreenplayDriftRisks.Select(risk => "screenplay: " + risk))
            .Concat(review.AiProseRisks.Select(risk => "ai_prose: " + risk))
            .Concat(review.RequiredFixes.Select(fix => "required_fix: " + fix))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();

        return new ReferenceOrchestrationApprovalSummaryPayload(
            blueprint.ChapterFunction,
            string.IsNullOrWhiteSpace(blueprint.GlobalPov) ? "not selected" : blueprint.GlobalPov,
            factBoundaryChanges,
            emotionalTrajectory,
            materialUsePlan,
            rewriteBudget,
            highRiskFindings);
    }

    private static ReferenceOrchestrationApprovalSummaryPayload BuildDraftAuditApprovalSummary(
        ReferenceChapterBlueprintPayload blueprint,
        ReferenceAnchoredDraftAuditPayload audit)
    {
        var factBoundaryChanges = blueprint.KnownFacts
            .Select(fact => "known: " + fact)
            .Concat(blueprint.ForbiddenFacts.Select(fact => "forbidden: " + fact))
            .Take(20)
            .ToArray();
        var emotionalTrajectory = blueprint.Beats.Count == 0
            ? blueprint.EmotionAnalysis.Summary
            : blueprint.Beats[0].EmotionBefore + " -> " + blueprint.Beats[^1].EmotionAfter;
        var highRiskFindings = audit.ProvenanceErrors
            .Select(error => "provenance: " + error)
            .Concat(audit.BlueprintErrors.Select(error => "blueprint: " + error))
            .Concat(audit.UnsupportedFactErrors.Select(error => "unsupported_fact: " + error))
            .Concat(audit.PovErrors.Select(error => "pov: " + error))
            .Concat(audit.AiProseRisks.Select(risk => "ai_prose: " + risk))
            .Concat(audit.RequiredFixes.Select(fix => "required_fix: " + fix))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToArray();

        return new ReferenceOrchestrationApprovalSummaryPayload(
            blueprint.ChapterFunction,
            string.IsNullOrWhiteSpace(blueprint.GlobalPov) ? "not selected" : blueprint.GlobalPov,
            factBoundaryChanges,
            emotionalTrajectory,
            "selected candidates audited against bound reference materials",
            audit.RewriteLevel,
            highRiskFindings);
    }

    private static string BuildDraftAuditFailureMessage(ReferenceAnchoredDraftAuditPayload audit)
    {
        var failures = audit.RequiredFixes
            .Concat(audit.ProvenanceErrors)
            .Concat(audit.BlueprintErrors)
            .Concat(audit.UnsupportedFactErrors)
            .Concat(audit.PovErrors)
            .Concat(audit.AiProseRisks)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
        return NormalizeOptional(
            string.Join("; ", failures),
            "Draft audit failed.",
            1_000);
    }

    private static string BuildBlueprintPremise(string? planText, string chapterFunction)
    {
        var normalized = NormalizeOptional(planText, chapterFunction, 2_000);
        if (!LooksLikeFinalProseParagraph(normalized))
        {
            return normalized;
        }

        var premise = string.Equals(chapterFunction, ProseLikePlanNotice, StringComparison.Ordinal)
            ? ProseLikePlanNotice
            : ProseLikePlanNotice + "; chapter function: " + chapterFunction;
        return premise.Length <= 2_000 ? premise : premise[..2_000];
    }

    private static string NormalizeBlueprintInstruction(string? value, string fallback, int maxLength)
    {
        var normalized = NormalizeOptional(value, fallback, maxLength);
        return LooksLikeFinalProseParagraph(normalized)
            ? ProseLikePlanNotice
            : normalized;
    }

    private static string NormalizeOptional(string? value, string fallback, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static bool LooksLikeFinalProseParagraph(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.ReplaceLineEndings("\n").Trim();
        if (normalized.Length < 100 || ContainsBlueprintPlanningSignal(normalized))
        {
            return false;
        }

        var terminators = normalized.Count(IsSentenceTerminator);
        return terminators >= 3 ||
            (normalized.Contains('\n') && terminators >= 2) ||
            (normalized.Any(IsDialogueMarker) && terminators >= 2);
    }

    private static bool ContainsBlueprintPlanningSignal(string value)
    {
        return ContainsAny(
            value,
            [
                "chapter goal", "chapter plan", "outline", "blueprint", "beat",
                "must", "should", "needs to", "plan:",
                "\u672c\u7ae0\u76ee\u6807", "\u672c\u7ae0\u8ba1\u5212",
                "\u5927\u7eb2", "\u84dd\u56fe", "\u9700\u8981", "\u5e94\u8be5"
            ]);
    }

    private static bool ContainsAny(string value, IReadOnlyList<string> needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSentenceTerminator(char value)
    {
        return value is '.' or '!' or '?' or '\u3002' or '\uff01' or '\uff1f';
    }

    private static bool IsDialogueMarker(char value)
    {
        return value is '"' or '\'' or '\u201c' or '\u201d' or '\u300c' or '\u300d' or '\u300e' or '\u300f';
    }

    private static ReferenceChapterBlueprintPayload ApplyRevisionChange(
        ReferenceChapterBlueprintPayload blueprint,
        IDictionary<string, ReferenceChapterBlueprintBeatPayload> beats,
        ReferenceBlueprintRevisionChangePayload change,
        ICollection<BlueprintRevisionRow> revisionRows,
        string origin,
        string reason,
        string? invalidatedReviewId)
    {
        if (string.IsNullOrWhiteSpace(change.FieldPath))
        {
            throw new ArgumentException("Revision field path is required.", nameof(change));
        }

        var fieldPath = change.FieldPath.Trim();
        const string beatPrefix = "beat:";
        if (!fieldPath.StartsWith(beatPrefix, StringComparison.Ordinal))
        {
            return ApplyBlueprintRevisionChange(blueprint, fieldPath, change, revisionRows, origin, reason, invalidatedReviewId);
        }

        var beatAndField = fieldPath[beatPrefix.Length..];
        var fieldSeparator = beatAndField.LastIndexOf(':');
        if (fieldSeparator <= 0 || fieldSeparator == beatAndField.Length - 1)
        {
            throw new ArgumentException("Revision currently supports field paths like 'beat:{beat_id}:paragraph_intention'.", nameof(change));
        }

        var beatId = beatAndField[..fieldSeparator];
        var fieldName = beatAndField[(fieldSeparator + 1)..];
        if (!beats.TryGetValue(beatId, out var beat))
        {
            throw new ArgumentException("Revision beat id does not exist.", nameof(change));
        }

        var previousValue = GetRevisableBeatField(beat, fieldName);
        var updated = SetRevisableBeatField(beat, fieldName, change.NewValue);
        var newValue = GetRevisableBeatField(updated, fieldName);
        beats[beatId] = updated;
        revisionRows.Add(new BlueprintRevisionRow(
            "revision-" + Guid.NewGuid().ToString("N"),
            change.FieldPath,
            HashText(previousValue),
            HashText(newValue),
            origin,
            reason,
            invalidatedReviewId ?? string.Empty,
            DateTimeOffset.UtcNow));
        return blueprint;
    }

    private static ReferenceChapterBlueprintPayload ApplyBlueprintRevisionChange(
        ReferenceChapterBlueprintPayload blueprint,
        string fieldPath,
        ReferenceBlueprintRevisionChangePayload change,
        ICollection<BlueprintRevisionRow> revisionRows,
        string origin,
        string reason,
        string? invalidatedReviewId)
    {
        switch (fieldPath)
        {
            case "known_facts":
            {
                var newValues = ParseRevisionStringList(change.NewValue);
                AddRevisionRow(
                    revisionRows,
                    change.FieldPath,
                    JsonSerializer.Serialize(blueprint.KnownFacts, JsonOptions),
                    JsonSerializer.Serialize(newValues, JsonOptions),
                    origin,
                    reason,
                    invalidatedReviewId);
                return blueprint with { KnownFacts = newValues };
            }

            case "forbidden_facts":
            {
                var newValues = ParseRevisionStringList(change.NewValue);
                AddRevisionRow(
                    revisionRows,
                    change.FieldPath,
                    JsonSerializer.Serialize(blueprint.ForbiddenFacts, JsonOptions),
                    JsonSerializer.Serialize(newValues, JsonOptions),
                    origin,
                    reason,
                    invalidatedReviewId);
                return blueprint with { ForbiddenFacts = newValues };
            }

            default:
                if (TryApplyAnalysisTrackRevision(
                    blueprint,
                    fieldPath,
                    change,
                    revisionRows,
                    origin,
                    reason,
                    invalidatedReviewId,
                    out var analysisRevised))
                {
                    return analysisRevised;
                }

                if (TryApplyExecutionContractRevision(
                    blueprint,
                    fieldPath,
                    change,
                    revisionRows,
                    origin,
                    reason,
                    invalidatedReviewId,
                    out var executionRevised))
                {
                    return executionRevised;
                }

                throw new ArgumentException("Unsupported revisable blueprint field.", nameof(change));
        }
    }

    private static bool TryApplyAnalysisTrackRevision(
        ReferenceChapterBlueprintPayload blueprint,
        string fieldPath,
        ReferenceBlueprintRevisionChangePayload change,
        ICollection<BlueprintRevisionRow> revisionRows,
        string origin,
        string reason,
        string? invalidatedReviewId,
        out ReferenceChapterBlueprintPayload revised)
    {
        revised = blueprint;
        var separator = fieldPath.LastIndexOf('.');
        if (separator <= 0 || separator == fieldPath.Length - 1)
        {
            return false;
        }

        var trackName = fieldPath[..separator];
        var fieldName = fieldPath[(separator + 1)..];
        if (!TryGetAnalysisTrack(blueprint, trackName, out var track))
        {
            return false;
        }

        var updated = fieldName switch
        {
            "summary" => track with { Summary = NormalizeOptional(change.NewValue, string.Empty, 2_000) },
            "points" => track with { Points = ParseRevisionStringList(change.NewValue) },
            _ => throw new ArgumentException("Unsupported revisable analysis track field.", nameof(change))
        };
        var previousValue = fieldName == "points"
            ? JsonSerializer.Serialize(track.Points, JsonOptions)
            : track.Summary;
        var newValue = fieldName == "points"
            ? JsonSerializer.Serialize(updated.Points, JsonOptions)
            : updated.Summary;
        AddRevisionRow(revisionRows, change.FieldPath, previousValue, newValue, origin, reason, invalidatedReviewId);
        revised = SetAnalysisTrack(blueprint, trackName, updated);
        return true;
    }

    private static bool TryGetAnalysisTrack(
        ReferenceChapterBlueprintPayload blueprint,
        string trackName,
        out ReferenceChapterBlueprintAnalysisTrackPayload track)
    {
        switch (trackName)
        {
            case "logic_analysis":
                track = blueprint.LogicAnalysis;
                return true;
            case "emotion_analysis":
                track = blueprint.EmotionAnalysis;
                return true;
            case "narration_analysis":
                track = blueprint.NarrationAnalysis;
                return true;
            case "character_analysis":
                track = blueprint.CharacterAnalysis;
                return true;
            case "reference_analysis":
                track = blueprint.ReferenceAnalysis;
                return true;
            case "transition_plan":
                track = blueprint.TransitionPlan;
                return true;
            default:
                track = new ReferenceChapterBlueprintAnalysisTrackPayload(string.Empty, string.Empty, []);
                return false;
        }
    }

    private static ReferenceChapterBlueprintPayload SetAnalysisTrack(
        ReferenceChapterBlueprintPayload blueprint,
        string trackName,
        ReferenceChapterBlueprintAnalysisTrackPayload track)
    {
        return trackName switch
        {
            "logic_analysis" => blueprint with { LogicAnalysis = track },
            "emotion_analysis" => blueprint with { EmotionAnalysis = track },
            "narration_analysis" => blueprint with { NarrationAnalysis = track },
            "character_analysis" => blueprint with { CharacterAnalysis = track },
            "reference_analysis" => blueprint with { ReferenceAnalysis = track },
            "transition_plan" => blueprint with { TransitionPlan = track },
            _ => throw new ArgumentException("Unsupported analysis track.", nameof(trackName))
        };
    }

    private static bool TryApplyExecutionContractRevision(
        ReferenceChapterBlueprintPayload blueprint,
        string fieldPath,
        ReferenceBlueprintRevisionChangePayload change,
        ICollection<BlueprintRevisionRow> revisionRows,
        string origin,
        string reason,
        string? invalidatedReviewId,
        out ReferenceChapterBlueprintPayload revised)
    {
        revised = blueprint;
        const string prefix = "execution_contract.";
        if (!fieldPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var fieldName = fieldPath[prefix.Length..];
        var contract = blueprint.ExecutionContract;
        var previousValue = GetExecutionContractField(contract, fieldName);
        var updated = SetExecutionContractField(contract, fieldName, change.NewValue);
        var newValue = GetExecutionContractField(updated, fieldName);
        AddRevisionRow(revisionRows, change.FieldPath, previousValue, newValue, origin, reason, invalidatedReviewId);
        revised = blueprint with { ExecutionContract = updated };
        return true;
    }

    private static string GetExecutionContractField(
        ReferenceChapterBlueprintExecutionTrackPayload contract,
        string fieldName)
    {
        return fieldName switch
        {
            "summary" => contract.Summary,
            "paragraph_intentions" => JsonSerializer.Serialize(contract.ParagraphIntentions, JsonOptions),
            "execution_modes" => JsonSerializer.Serialize(contract.ExecutionModes, JsonOptions),
            "anti_screenplay_duties" => JsonSerializer.Serialize(contract.AntiScreenplayDuties, JsonOptions),
            "source_backed_detail_targets" => JsonSerializer.Serialize(contract.SourceBackedDetailTargets, JsonOptions),
            "candidate_rejection_rules" => JsonSerializer.Serialize(contract.CandidateRejectionRules, JsonOptions),
            _ => throw new ArgumentException("Unsupported revisable execution contract field.", nameof(fieldName))
        };
    }

    private static ReferenceChapterBlueprintExecutionTrackPayload SetExecutionContractField(
        ReferenceChapterBlueprintExecutionTrackPayload contract,
        string fieldName,
        string value)
    {
        return fieldName switch
        {
            "summary" => contract with { Summary = NormalizeOptional(value, string.Empty, 2_000) },
            "paragraph_intentions" => contract with { ParagraphIntentions = ParseRevisionStringList(value) },
            "execution_modes" => contract with { ExecutionModes = ParseRevisionStringList(value) },
            "anti_screenplay_duties" => contract with { AntiScreenplayDuties = ParseRevisionStringList(value) },
            "source_backed_detail_targets" => contract with { SourceBackedDetailTargets = ParseRevisionStringList(value) },
            "candidate_rejection_rules" => contract with { CandidateRejectionRules = ParseRevisionStringList(value) },
            _ => throw new ArgumentException("Unsupported revisable execution contract field.", nameof(fieldName))
        };
    }

    private static void AddRevisionRow(
        ICollection<BlueprintRevisionRow> revisionRows,
        string fieldPath,
        string previousValue,
        string newValue,
        string origin,
        string reason,
        string? invalidatedReviewId)
    {
        revisionRows.Add(new BlueprintRevisionRow(
            "revision-" + Guid.NewGuid().ToString("N"),
            fieldPath,
            HashText(previousValue),
            HashText(newValue),
            origin,
            reason,
            invalidatedReviewId ?? string.Empty,
            DateTimeOffset.UtcNow));
    }

    private static IReadOnlyList<string> ParseRevisionStringList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                return NormalizeList(JsonSerializer.Deserialize<IReadOnlyList<string>>(trimmed, JsonOptions));
            }
            catch (JsonException exception)
            {
                throw new ArgumentException(
                    "Revision list value must be a JSON string array or a newline-separated list.",
                    nameof(value),
                    exception);
            }
        }

        return NormalizeList(trimmed.Split(
            ['\r', '\n', ';', '；'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string GetRevisableBeatField(ReferenceChapterBlueprintBeatPayload beat, string fieldName)
    {
        return fieldName switch
        {
            "narrative_function" => beat.NarrativeFunction,
            "logic_premise" => beat.LogicPremise,
            "conflict_pressure" => beat.ConflictPressure,
            "causality_in" => beat.CausalityIn,
            "causality_out" => beat.CausalityOut,
            "transition_in" => beat.TransitionIn,
            "transition_out" => beat.TransitionOut,
            "pov_character" => beat.PovCharacter,
            "narrative_distance" => beat.NarrativeDistance,
            "viewpoint_allowed_knowledge" => JsonSerializer.Serialize(beat.ViewpointAllowedKnowledge, JsonOptions),
            "viewpoint_forbidden_knowledge" => JsonSerializer.Serialize(beat.ViewpointForbiddenKnowledge, JsonOptions),
            "character_states_before" => JsonSerializer.Serialize(beat.CharacterStatesBefore, JsonOptions),
            "character_states_after" => JsonSerializer.Serialize(beat.CharacterStatesAfter, JsonOptions),
            "character_goals" => JsonSerializer.Serialize(beat.CharacterGoals, JsonOptions),
            "character_misbeliefs" => JsonSerializer.Serialize(beat.CharacterMisbeliefs, JsonOptions),
            "relationship_pressure" => JsonSerializer.Serialize(beat.RelationshipPressure, JsonOptions),
            "emotion_trigger" => beat.EmotionTrigger,
            "emotion_before" => beat.EmotionBefore,
            "emotion_after" => beat.EmotionAfter,
            "suppressed_reaction" => beat.SuppressedReaction,
            "external_evidence" => beat.ExternalEvidence,
            "narration_strategy" => beat.NarrationStrategy,
            "rhythm_strategy" => beat.RhythmStrategy,
            "paragraph_intention" => beat.ParagraphIntention,
            "execution_mode" => beat.ExecutionMode,
            "anti_screenplay_duty" => beat.AntiScreenplayDuty,
            "sensory_anchor_target" => beat.SensoryAnchorTarget,
            "subtext_plan" => beat.SubtextPlan,
            "source_backed_detail_target" => beat.SourceBackedDetailTarget,
            "candidate_rejection_rule" => beat.CandidateRejectionRule,
            "scene_facts" => JsonSerializer.Serialize(beat.SceneFacts, JsonOptions),
            "forbidden_facts" => JsonSerializer.Serialize(beat.ForbiddenFacts, JsonOptions),
            "required_material_types" => JsonSerializer.Serialize(beat.RequiredMaterialTypes, JsonOptions),
            "max_rewrite_level" => beat.MaxRewriteLevel,
            "slot_plan" => JsonSerializer.Serialize(beat.SlotPlan, JsonOptions),
            "locked_phrase_policy" => beat.LockedPhrasePolicy,
            "no_reuse_reason" => beat.NoReuseReason,
            "prose_duties" => JsonSerializer.Serialize(beat.ProseDuties, JsonOptions),
            "reference_query.query" => beat.ReferenceQuery.Query,
            "reference_query.material_types" => JsonSerializer.Serialize(beat.ReferenceQuery.MaterialTypes, JsonOptions),
            "reference_query.emotion_tags" => JsonSerializer.Serialize(beat.ReferenceQuery.EmotionTags, JsonOptions),
            "reference_query.function_tags" => JsonSerializer.Serialize(beat.ReferenceQuery.FunctionTags, JsonOptions),
            "reference_query.pov_tags" => JsonSerializer.Serialize(beat.ReferenceQuery.PovTags, JsonOptions),
            "reference_query.technique_tags" => JsonSerializer.Serialize(beat.ReferenceQuery.TechniqueTags, JsonOptions),
            "reference_query.max_results" => beat.ReferenceQuery.MaxResults.ToString(CultureInfo.InvariantCulture),
            _ => throw new ArgumentException("Unsupported revisable beat field.", nameof(fieldName))
        };
    }

    private static ReferenceChapterBlueprintBeatPayload SetRevisableBeatField(
        ReferenceChapterBlueprintBeatPayload beat,
        string fieldName,
        string value)
    {
        var normalized = NormalizeOptional(value, string.Empty, 2_000);
        return fieldName switch
        {
            "narrative_function" => beat with { NarrativeFunction = normalized },
            "logic_premise" => beat with { LogicPremise = normalized },
            "conflict_pressure" => beat with { ConflictPressure = normalized },
            "causality_in" => beat with { CausalityIn = normalized },
            "causality_out" => beat with { CausalityOut = normalized },
            "transition_in" => beat with { TransitionIn = normalized },
            "transition_out" => beat with { TransitionOut = normalized },
            "pov_character" => beat with { PovCharacter = normalized },
            "narrative_distance" => beat with { NarrativeDistance = normalized },
            "viewpoint_allowed_knowledge" => beat with { ViewpointAllowedKnowledge = ParseRevisionStringList(value) },
            "viewpoint_forbidden_knowledge" => beat with { ViewpointForbiddenKnowledge = ParseRevisionStringList(value) },
            "character_states_before" => beat with { CharacterStatesBefore = ParseRevisionStringList(value) },
            "character_states_after" => beat with { CharacterStatesAfter = ParseRevisionStringList(value) },
            "character_goals" => beat with { CharacterGoals = ParseRevisionStringList(value) },
            "character_misbeliefs" => beat with { CharacterMisbeliefs = ParseRevisionStringList(value) },
            "relationship_pressure" => beat with { RelationshipPressure = ParseRevisionStringList(value) },
            "emotion_trigger" => beat with { EmotionTrigger = normalized },
            "emotion_before" => beat with { EmotionBefore = normalized },
            "emotion_after" => beat with { EmotionAfter = normalized },
            "suppressed_reaction" => beat with { SuppressedReaction = normalized },
            "external_evidence" => beat with { ExternalEvidence = normalized },
            "narration_strategy" => beat with { NarrationStrategy = normalized },
            "rhythm_strategy" => beat with { RhythmStrategy = normalized },
            "paragraph_intention" => beat with { ParagraphIntention = normalized },
            "execution_mode" => beat with { ExecutionMode = normalized },
            "anti_screenplay_duty" => beat with { AntiScreenplayDuty = normalized },
            "sensory_anchor_target" => beat with { SensoryAnchorTarget = normalized },
            "subtext_plan" => beat with { SubtextPlan = normalized },
            "source_backed_detail_target" => beat with { SourceBackedDetailTarget = normalized },
            "candidate_rejection_rule" => beat with { CandidateRejectionRule = normalized },
            "scene_facts" => beat with { SceneFacts = ParseRevisionStringList(value) },
            "forbidden_facts" => beat with { ForbiddenFacts = ParseRevisionStringList(value) },
            "required_material_types" => beat with { RequiredMaterialTypes = ParseRevisionStringList(value) },
            "max_rewrite_level" => beat with { MaxRewriteLevel = normalized },
            "slot_plan" => beat with { SlotPlan = ParseRevisionSlotPlan(value) },
            "locked_phrase_policy" => beat with { LockedPhrasePolicy = normalized },
            "no_reuse_reason" => beat with { NoReuseReason = normalized },
            "prose_duties" => beat with { ProseDuties = ParseRevisionStringList(value) },
            "reference_query.query" => beat with { ReferenceQuery = beat.ReferenceQuery with { Query = normalized } },
            "reference_query.material_types" => beat with { ReferenceQuery = beat.ReferenceQuery with { MaterialTypes = ParseRevisionStringList(value) } },
            "reference_query.emotion_tags" => beat with { ReferenceQuery = beat.ReferenceQuery with { EmotionTags = ParseRevisionStringList(value) } },
            "reference_query.function_tags" => beat with { ReferenceQuery = beat.ReferenceQuery with { FunctionTags = ParseRevisionStringList(value) } },
            "reference_query.pov_tags" => beat with { ReferenceQuery = beat.ReferenceQuery with { PovTags = ParseRevisionStringList(value) } },
            "reference_query.technique_tags" => beat with { ReferenceQuery = beat.ReferenceQuery with { TechniqueTags = ParseRevisionStringList(value) } },
            "reference_query.max_results" => beat with { ReferenceQuery = beat.ReferenceQuery with { MaxResults = ParseRevisionPositiveInt(value, 1, 50) } },
            _ => throw new ArgumentException("Unsupported revisable beat field.", nameof(fieldName))
        };
    }

    private static IReadOnlyList<ReferenceSlotValuePayload> ParseRevisionSlotPlan(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Revision slot_plan value must be a JSON array of { slot_name, value } objects.",
                nameof(value));
        }

        try
        {
            var slots = JsonSerializer.Deserialize<IReadOnlyList<ReferenceSlotValuePayload>>(trimmed, JsonOptions);
            return NormalizeSlotPlan(slots);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Revision slot_plan value must be a JSON array of { slot_name, value } objects.",
                nameof(value),
                exception);
        }
    }

    private static IReadOnlyList<ReferenceSlotValuePayload> NormalizeSlotPlan(
        IReadOnlyList<ReferenceSlotValuePayload>? slotPlan)
    {
        return slotPlan?
            .Where(slot => slot is not null)
            .Select(slot => new ReferenceSlotValuePayload(
                NormalizeOptional(slot.SlotName, string.Empty, 200),
                NormalizeOptional(slot.Value, string.Empty, 500)))
            .Where(slot => !string.IsNullOrWhiteSpace(slot.SlotName) || !string.IsNullOrWhiteSpace(slot.Value))
            .ToArray() ?? [];
    }

    private static int ParseRevisionPositiveInt(string value, int minValue, int maxValue)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ArgumentException("Revision value must be an integer.", nameof(value));
        }

        return Math.Clamp(parsed, minValue, maxValue);
    }

    private static string BuildBeatId(long blueprintId, int beatIndex)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{blueprintId}:beat:{beatIndex}");
    }

    private static string HashText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static int RewriteLevelRank(string rewriteLevel)
    {
        return rewriteLevel switch
        {
            ReferenceRewriteLevels.L0 => 0,
            ReferenceRewriteLevels.L1 => 1,
            ReferenceRewriteLevels.L2 => 2,
            ReferenceRewriteLevels.L3 => 3,
            ReferenceRewriteLevels.L4 => 4,
            _ => 99
        };
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private static T ReadJson<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Stored JSON payload is empty.");
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id must be positive.");
        }
    }

    private static void ValidateChapterNumber(int chapterNumber)
    {
        if (chapterNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterNumber), chapterNumber, "Chapter number must be positive.");
        }
    }

    private static void ValidateBlueprintId(long blueprintId)
    {
        if (blueprintId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blueprintId), blueprintId, "Blueprint id must be positive.");
        }
    }

    private sealed record BlueprintRow(
        long BlueprintId,
        long NovelId,
        int ChapterNumber,
        string Title,
        string Status,
        string SourcePlanScope,
        string SourcePlanHash,
        string ContextHash,
        string AnalysisContractHash,
        int BlueprintVersion,
        long ParentBlueprintId,
        long PrimaryAnchorId,
        string ChapterFunction,
        ReferenceChapterBlueprintAnalysisTrackPayload LogicAnalysis,
        ReferenceChapterBlueprintAnalysisTrackPayload EmotionAnalysis,
        ReferenceChapterBlueprintAnalysisTrackPayload NarrationAnalysis,
        ReferenceChapterBlueprintAnalysisTrackPayload CharacterAnalysis,
        ReferenceChapterBlueprintAnalysisTrackPayload ReferenceAnalysis,
        ReferenceChapterBlueprintAnalysisTrackPayload TransitionPlan,
        ReferenceChapterBlueprintExecutionTrackPayload ExecutionContract,
        string PreviousState,
        string FinalState,
        string FinalHook,
        string GlobalPov,
        string GlobalNarrativeDistance,
        IReadOnlyList<string> KnownFacts,
        IReadOnlyList<string> ForbiddenFacts,
        IReadOnlyList<string> RiskFlags,
        string BuildVersion,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record BlueprintSummaryRow(
        ReferenceChapterBlueprintSummaryPayload Summary,
        string SourcePlanScope);

    private sealed record ScoredMaterialLink(
        ReferenceBlueprintMaterialLinkPayload Link,
        string AnalysisContractHash,
        IReadOnlyDictionary<string, double> ScoreComponents,
        bool HasFunctionalFit)
    {
        public double Score => Link.Score;
    }

    private sealed record BlueprintRevisionRow(
        string RevisionId,
        string ChangedFieldPath,
        string PreviousValueHash,
        string NewValueHash,
        string Origin,
        string RevisionReason,
        string InvalidatedReviewId,
        DateTimeOffset CreatedAt);
}
