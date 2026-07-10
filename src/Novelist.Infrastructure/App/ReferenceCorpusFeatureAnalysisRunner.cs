using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed class ReferenceCorpusFeatureAnalysisRunner
{
    private const double LowConfidenceReviewThreshold = 0.70;

    private readonly IReferenceCorpusFeatureFamilyAnalyzer _analyzer;

    public ReferenceCorpusFeatureAnalysisRunner(IReferenceCorpusFeatureFamilyAnalyzer analyzer)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    }

    public async ValueTask<ReferenceCorpusFeatureAnalysisRunResult> RunAsync(
        SqliteConnection connection,
        ReferenceCorpusFeatureAnalysisRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = new List<string>();
        var nodes = await ReadNodesAsync(connection, request.AnchorId, request.NodeType, diagnostics, cancellationToken);
        var workItems = BuildWorkItems(nodes, request.Families);
        var existingState = await ReadRunStateAsync(connection, request.RunId, cancellationToken);
        var state = BuildInitialState(existingState, request);
        var processed = 0;

        await UpsertRunAsync(connection, request, state, observationCount: existingState?.ObservationCount ?? 0, completedAt: null, cancellationToken);
        var startIndex = ResolveStartIndex(workItems, request.Resume ? state.ResumeCursor : null);

for (var index = startIndex; index < workItems.Count; index++)
{
 var executionAction = await request.ExecutionControl.CheckpointAsync(
 request.RunId,
 state.ResumeCursor,
 cancellationToken);
 if (!string.Equals(executionAction, ReferenceCorpusAnalysisExecutionActions.Proceed, StringComparison.Ordinal))
 {
 state = ApplyExecutionAction(state, executionAction);
 await UpsertRunAsync(
 connection,
 request,
 state,
 await CountObservationsAsync(connection, request.RunId, cancellationToken),
 completedAt: null,
 cancellationToken);
 return BuildResult(
 request.RunId,
 state,
 await CountObservationsAsync(connection, request.RunId, cancellationToken),
 processed,
 diagnostics);
 }

var workItem = workItems[index];
            var schema = ReferenceCorpusFeatureFamilySchemaRegistry.Get(workItem.Family);
            var previousCursor = state.ResumeCursor;
            ReferenceCorpusFeatureFamilyValidationResult? validation = null;
            var acceptedOutputTokens = 0;
            var accepted = false;

            for (var attempt = 1; attempt <= request.MaxValidationAttempts; attempt++)
            {
                var output = await _analyzer.AnalyzeAsync(
                    new ReferenceCorpusFeatureFamilyAnalysisInput(
                        request.RunId,
                        request.AnchorId,
                        workItem.Node.NodeId,
                        request.NodeType,
                        workItem.Node.Text,
                        workItem.Family,
                        schema)
                    {
                        Context = workItem.Node.Context
                    },
                    cancellationToken);

                validation = ReferenceCorpusFeatureFamilyOutputValidator.Validate(
                    output.ModelOutputJson,
                    workItem.Family,
                    request.NodeType,
                    workItem.Node.Text.Length);
                diagnostics.AddRange(validation.Diagnostics);

                if (IsAcceptedValidation(validation))
                {
                    acceptedOutputTokens = Math.Max(0, output.TokensSpent);
                    accepted = true;
                    break;
                }

                state = await RecordRunTokensAsync(
                    connection,
                    request,
                    state,
                    Math.Max(0, output.TokensSpent),
                    previousCursor,
                    cancellationToken);

                if (string.Equals(state.Status, ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, StringComparison.Ordinal))
                {
                    return BuildResult(
                        request.RunId,
                        state,
                        await CountObservationsAsync(connection, request.RunId, cancellationToken),
                        processed,
                        diagnostics);
                }

                if (attempt < request.MaxValidationAttempts)
                {
                    diagnostics.Add($"Validation failed for {workItem.Cursor} attempt {attempt}; retrying.");
                }
            }

            if (!accepted || validation is null)
            {
                diagnostics.Add($"Validation retry limit reached for {workItem.Cursor}.");
                state = ReferenceCorpusAnalysisRunStateMachine.Fail(state);
                await UpsertRunAsync(
                    connection,
                    request,
                    state,
                    await CountObservationsAsync(connection, request.RunId, cancellationToken),
                    completedAt: request.StartedAt,
                    cancellationToken);
                return BuildResult(request.RunId, state, await CountObservationsAsync(connection, request.RunId, cancellationToken), processed, diagnostics);
            }

            await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
            {
                foreach (var candidate in validation.AcceptedObservations)
                {
                    var identity = await ReferenceCorpusObservationWriter.UpsertAsync(
                        connection,
                        transaction,
                        ToObservation(request, workItem.Node, candidate),
                        cancellationToken);
                    await SyncProjectionAsync(
                        connection,
                        transaction,
                        identity.ObservationId,
                        workItem.Node.NodeId,
                        request.AnchorId,
                        candidate,
                        cancellationToken);
                }

                state = ReferenceCorpusAnalysisRunStateMachine.RecordProgress(
                    state,
                    acceptedOutputTokens,
                    workItem.Cursor);
                await UpsertRunAsync(
                    connection,
                    transaction,
                    request,
                    state,
                    await CountObservationsAsync(connection, transaction, request.RunId, cancellationToken),
                    completedAt: null,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            processed++;
            if (string.Equals(state.Status, ReferenceCorpusAnalysisRunStatuses.BudgetExhausted, StringComparison.Ordinal) &&
                index < workItems.Count - 1)
            {
                return BuildResult(request.RunId, state, await CountObservationsAsync(connection, request.RunId, cancellationToken), processed, diagnostics);
            }
        }

        state = ReferenceCorpusAnalysisRunStateMachine.Complete(state);
        await UpsertRunAsync(
            connection,
            request,
            state,
            await CountObservationsAsync(connection, request.RunId, cancellationToken),
            completedAt: request.StartedAt,
            cancellationToken);
        return BuildResult(request.RunId, state, await CountObservationsAsync(connection, request.RunId, cancellationToken), processed, diagnostics);
    }

private static bool IsAcceptedValidation(ReferenceCorpusFeatureFamilyValidationResult validation)
    {
        return validation.Status is
            ReferenceCorpusFeatureFamilyValidationStatuses.Passed or
            ReferenceCorpusFeatureFamilyValidationStatuses.Partial;
    }

    private static async ValueTask<ReferenceCorpusAnalysisRunState> RecordRunTokensAsync(
        SqliteConnection connection,
        ReferenceCorpusFeatureAnalysisRunRequest request,
        ReferenceCorpusAnalysisRunState state,
        int additionalTokensSpent,
        string? resumeCursor,
        CancellationToken cancellationToken)
    {
        if (additionalTokensSpent == 0)
        {
            return state;
        }

        var updated = ReferenceCorpusAnalysisRunStateMachine.RecordProgress(
            state,
            additionalTokensSpent,
            resumeCursor);
        await UpsertRunAsync(
            connection,
            request,
            updated,
            await CountObservationsAsync(connection, request.RunId, cancellationToken),
            completedAt: null,
            cancellationToken);
        return updated;
    }

    private static void ValidateRequest(ReferenceCorpusFeatureAnalysisRunRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunId))
        {
            throw new ArgumentException("Run id is required.", nameof(request));
        }

        if (request.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.AnchorId, "Anchor id must be positive.");
        }

        if (request.NodeType is not ReferenceCorpusNodeTypes.Sentence and not ReferenceCorpusNodeTypes.Passage)
        {
            throw new ArgumentException("Feature analysis node_type must be sentence or passage.", nameof(request));
        }

        if (request.Families.Count == 0)
        {
            throw new ArgumentException("At least one feature family is required.", nameof(request));
        }

        if (request.MaxValidationAttempts is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxValidationAttempts, "Validation attempts must be between 1 and 5.");
        }

        foreach (var family in request.Families)
        {
            var schema = ReferenceCorpusFeatureFamilySchemaRegistry.Get(family);
            if (!string.Equals(schema.NodeType, request.NodeType, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Feature family '{family}' expects node_type '{schema.NodeType}'.", nameof(request));
            }
        }
    }

    private static ReferenceCorpusAnalysisRunState BuildInitialState(
        PersistedRunState? existingState,
        ReferenceCorpusFeatureAnalysisRunRequest request)
    {
        if (!request.Resume || existingState is null)
        {
            return ReferenceCorpusAnalysisRunStateMachine.Start(request.TokenBudget);
        }

        var state = new ReferenceCorpusAnalysisRunState(
            existingState.Status,
            existingState.TokenBudget,
            existingState.TokensSpent,
            existingState.ResumeCursor);
        if (string.Equals(state.Status, ReferenceCorpusAnalysisRunStatuses.Running, StringComparison.Ordinal))
        {
            return state with { TokenBudget = request.TokenBudget ?? state.TokenBudget };
        }

        return ReferenceCorpusAnalysisRunStateMachine.Resume(state, request.TokenBudget);
    }

    private static IReadOnlyList<FeatureAnalysisWorkItem> BuildWorkItems(
        IReadOnlyList<FeatureAnalysisNode> nodes,
        IReadOnlyList<string> families)
    {
        var result = new List<FeatureAnalysisWorkItem>(nodes.Count * families.Count);
        foreach (var node in nodes)
        {
            foreach (var family in families)
            {
                result.Add(new FeatureAnalysisWorkItem(node, family, BuildCursor(node.NodeId, family)));
            }
        }

        return result;
    }

    private static int ResolveStartIndex(IReadOnlyList<FeatureAnalysisWorkItem> workItems, string? resumeCursor)
    {
        if (string.IsNullOrWhiteSpace(resumeCursor))
        {
            return 0;
        }

        for (var index = 0; index < workItems.Count; index++)
        {
            if (string.Equals(workItems[index].Cursor, resumeCursor, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return 0;
    }

    private static ReferenceCorpusFeatureObservation ToObservation(
        ReferenceCorpusFeatureAnalysisRunRequest request,
        FeatureAnalysisNode node,
        ReferenceCorpusFeatureObservationCandidate candidate)
    {
        return new ReferenceCorpusFeatureObservation(
            NodeId: node.NodeId,
            NodeType: node.NodeType,
            RunId: request.RunId,
            AnchorId: request.AnchorId,
            FeatureFamily: candidate.FeatureFamily,
            FeatureKey: candidate.FeatureKey,
            ValueKind: candidate.ValueKind,
            ValueText: candidate.ValueText,
            ValueNum: candidate.ValueNum,
            ValueBool: candidate.ValueBool,
            ValueJson: candidate.ValueJson,
            Intensity: candidate.Intensity,
            Confidence: candidate.Confidence,
            EvidenceStart: candidate.EvidenceStart,
            EvidenceEnd: candidate.EvidenceEnd,
            Explanation: candidate.Explanation,
            ReviewState: ResolveInitialReviewState(candidate.Confidence),
            ValidityState: "active",
            SupersededByRunId: null,
            CreatedAt: request.StartedAt);
    }

    private static string ResolveInitialReviewState(double confidence)
    {
        return confidence < LowConfidenceReviewThreshold
            ? ReferenceCorpusFeatureObservationReviewStates.LowConfidence
            : ReferenceCorpusFeatureObservationReviewStates.Unverified;
    }

    private static async ValueTask SyncProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string observationId,
        string nodeId,
        long anchorId,
        ReferenceCorpusFeatureObservationCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(candidate.FeatureFamily, ReferenceCorpusFeatureFamilies.Sensory, StringComparison.Ordinal))
        {
            return;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_obs_sensory WHERE observation_id = $observation_id;";
            delete.Parameters.AddWithValue("$observation_id", observationId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(candidate.ValueJson))
        {
            return;
        }

        using var document = JsonDocument.Parse(candidate.ValueJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("sense", out var senseElement) ||
                senseElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(senseElement.GetString()) ||
                !item.TryGetProperty("intensity", out var intensityElement) ||
                intensityElement.ValueKind != JsonValueKind.Number ||
                !intensityElement.TryGetDouble(out var intensity))
            {
                continue;
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reference_obs_sensory
                  (observation_id, node_id, anchor_id, sense, intensity)
                VALUES
                  ($observation_id, $node_id, $anchor_id, $sense, $intensity);
                """;
            insert.Parameters.AddWithValue("$observation_id", observationId);
            insert.Parameters.AddWithValue("$node_id", nodeId);
            insert.Parameters.AddWithValue("$anchor_id", anchorId);
            insert.Parameters.AddWithValue("$sense", senseElement.GetString()!);
            insert.Parameters.AddWithValue("$intensity", intensity);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask<IReadOnlyList<FeatureAnalysisNode>> ReadNodesAsync(
        SqliteConnection connection,
        long anchorId,
        string nodeType,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (string.Equals(nodeType, ReferenceCorpusNodeTypes.Passage, StringComparison.Ordinal))
        {
            return await ReadParagraphPassageNodesAsync(connection, anchorId, diagnostics, cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, node_type, sequence_index, text
            FROM reference_text_nodes
            WHERE anchor_id = $anchor_id
              AND node_type = $node_type
            ORDER BY chapter_index, start_offset, sequence_index, node_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$node_type", nodeType);

        var result = new List<FeatureAnalysisNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new FeatureAnalysisNode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                ReferenceCorpusFeatureAnalysisContext.Empty));
        }

        return result;
    }

    private static async ValueTask<IReadOnlyList<FeatureAnalysisNode>> ReadParagraphPassageNodesAsync(
        SqliteConnection connection,
        long anchorId,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!await HasSourceSegmentNodeIdAsync(connection, cancellationToken))
        {
            diagnostics.Add("Passage feature analysis skipped: reference_source_segments.node_id is unavailable; rebuild or backfill reference source segments before Task B analysis.");
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.node_id, n.node_type, n.sequence_index, n.text,
                   n.parent_node_id, n.chapter_index, n.start_offset, n.end_offset, n.text_hash,
                   (
                     SELECT s.segment_id
                     FROM reference_source_segments s
                     WHERE s.anchor_id = n.anchor_id
                       AND s.node_id = n.node_id
                       AND s.segment_type = 'paragraph'
                     ORDER BY s.chapter_index, s.segment_index, s.segment_id
                     LIMIT 1
                   ) AS source_segment_id,
                   'paragraph' AS source_segment_type
            FROM reference_text_nodes n
            WHERE n.anchor_id = $anchor_id
              AND n.node_type = 'passage'
              AND EXISTS (
                SELECT 1
                FROM reference_source_segments s
                WHERE s.anchor_id = n.anchor_id
                  AND s.node_id = n.node_id
                  AND s.segment_type = 'paragraph'
              )
            ORDER BY n.chapter_index, n.start_offset, n.sequence_index, n.node_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);

        var result = new List<FeatureAnalysisNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var node = new FeatureAnalysisNode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                ReferenceCorpusFeatureAnalysisContext.Empty);
            var context = await BuildPassageContextAsync(
                connection,
                anchorId,
                node.NodeId,
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(9),
                reader.GetString(10),
                cancellationToken);
            result.Add(node with { Context = context });
        }

        if (result.Count == 0 && await CountNodesAsync(connection, anchorId, ReferenceCorpusNodeTypes.Passage, cancellationToken) > 0)
        {
            diagnostics.Add("Passage feature analysis found passage nodes but no linked paragraph source segments; rebuild or backfill reference source segments before Task B analysis.");
        }

        return result;
    }

    private static async ValueTask<ReferenceCorpusFeatureAnalysisContext> BuildPassageContextAsync(
        SqliteConnection connection,
        long anchorId,
        string nodeId,
        string? parentNodeId,
        int? chapterIndex,
        int startOffset,
        int endOffset,
        string sourceSegmentId,
        string sourceSegmentType,
        CancellationToken cancellationToken)
    {
        var parent = string.IsNullOrWhiteSpace(parentNodeId)
            ? null
            : await ReadContextNodeByIdAsync(connection, anchorId, parentNodeId, cancellationToken);
        var chapter = parent is { NodeType: ReferenceCorpusNodeTypes.Chapter }
            ? parent
            : await ReadChapterContextNodeAsync(connection, anchorId, chapterIndex, cancellationToken);
        var containingScene = await ReadContainingSceneContextNodeAsync(
            connection,
            anchorId,
            chapterIndex,
            startOffset,
            endOffset,
            cancellationToken);
        var previousParagraph = await ReadSiblingParagraphContextNodeAsync(
            connection,
            anchorId,
            nodeId,
            chapterIndex,
            startOffset,
            previous: true,
            cancellationToken);
        var nextParagraph = await ReadSiblingParagraphContextNodeAsync(
            connection,
            anchorId,
            nodeId,
            chapterIndex,
            startOffset,
            previous: false,
            cancellationToken);

        return new ReferenceCorpusFeatureAnalysisContext(
            sourceSegmentId,
            sourceSegmentType,
            parent,
            chapter,
            containingScene,
            previousParagraph,
            nextParagraph);
    }

    private static async ValueTask<ReferenceCorpusFeatureAnalysisContextNode?> ReadContextNodeByIdAsync(
        SqliteConnection connection,
        long anchorId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.node_id, n.node_type, s.segment_id, s.segment_type,
                   n.chapter_index, n.start_offset, n.end_offset, n.text_hash, n.text
            FROM reference_text_nodes n
            LEFT JOIN reference_source_segments s ON s.node_id = n.node_id
            WHERE n.anchor_id = $anchor_id
              AND n.node_id = $node_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        return await ReadSingleContextNodeAsync(command, cancellationToken);
    }

    private static async ValueTask<ReferenceCorpusFeatureAnalysisContextNode?> ReadChapterContextNodeAsync(
        SqliteConnection connection,
        long anchorId,
        int? chapterIndex,
        CancellationToken cancellationToken)
    {
        if (chapterIndex is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.node_id, n.node_type, s.segment_id, s.segment_type,
                   n.chapter_index, n.start_offset, n.end_offset, n.text_hash, n.text
            FROM reference_text_nodes n
            LEFT JOIN reference_source_segments s ON s.node_id = n.node_id
            WHERE n.anchor_id = $anchor_id
              AND n.node_type = 'chapter'
              AND n.chapter_index = $chapter_index
            ORDER BY n.start_offset, n.sequence_index, n.node_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex.Value);
        return await ReadSingleContextNodeAsync(command, cancellationToken);
    }

    private static async ValueTask<ReferenceCorpusFeatureAnalysisContextNode?> ReadContainingSceneContextNodeAsync(
        SqliteConnection connection,
        long anchorId,
        int? chapterIndex,
        int startOffset,
        int endOffset,
        CancellationToken cancellationToken)
    {
        if (chapterIndex is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.node_id, n.node_type, s.segment_id, s.segment_type,
                   n.chapter_index, n.start_offset, n.end_offset, n.text_hash, n.text
            FROM reference_source_segments s
            INNER JOIN reference_text_nodes n ON n.node_id = s.node_id
            WHERE s.anchor_id = $anchor_id
              AND s.segment_type = 'scene'
              AND s.chapter_index = $chapter_index
              AND s.start_offset <= $start_offset
              AND s.end_offset >= $end_offset
            ORDER BY (s.end_offset - s.start_offset), s.start_offset, s.segment_index
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex.Value);
        command.Parameters.AddWithValue("$start_offset", startOffset);
        command.Parameters.AddWithValue("$end_offset", endOffset);
        return await ReadSingleContextNodeAsync(command, cancellationToken);
    }

    private static async ValueTask<ReferenceCorpusFeatureAnalysisContextNode?> ReadSiblingParagraphContextNodeAsync(
        SqliteConnection connection,
        long anchorId,
        string nodeId,
        int? chapterIndex,
        int startOffset,
        bool previous,
        CancellationToken cancellationToken)
    {
        if (chapterIndex is null)
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = previous
            ? """
            SELECT n.node_id, n.node_type, s.segment_id, s.segment_type,
                   n.chapter_index, n.start_offset, n.end_offset, n.text_hash, n.text
            FROM reference_source_segments s
            INNER JOIN reference_text_nodes n ON n.node_id = s.node_id
            WHERE s.anchor_id = $anchor_id
              AND s.segment_type = 'paragraph'
              AND s.chapter_index = $chapter_index
              AND s.node_id <> $node_id
              AND s.start_offset < $start_offset
            ORDER BY s.start_offset DESC, s.segment_index DESC, s.segment_id DESC
            LIMIT 1;
            """
            : """
            SELECT n.node_id, n.node_type, s.segment_id, s.segment_type,
                   n.chapter_index, n.start_offset, n.end_offset, n.text_hash, n.text
            FROM reference_source_segments s
            INNER JOIN reference_text_nodes n ON n.node_id = s.node_id
            WHERE s.anchor_id = $anchor_id
              AND s.segment_type = 'paragraph'
              AND s.chapter_index = $chapter_index
              AND s.node_id <> $node_id
              AND s.start_offset > $start_offset
            ORDER BY s.start_offset ASC, s.segment_index ASC, s.segment_id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex.Value);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$start_offset", startOffset);
        return await ReadSingleContextNodeAsync(command, cancellationToken);
    }

    private static async ValueTask<ReferenceCorpusFeatureAnalysisContextNode?> ReadSingleContextNodeAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReferenceCorpusFeatureAnalysisContextNode(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetString(7),
            TruncatePreview(reader.GetString(8)));
    }

    private static async ValueTask<bool> HasSourceSegmentNodeIdAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(reference_source_segments);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), "node_id", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<int> CountNodesAsync(
        SqliteConnection connection,
        long anchorId,
        string nodeType,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_text_nodes
            WHERE anchor_id = $anchor_id
              AND node_type = $node_type;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$node_type", nodeType);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static string TruncatePreview(string text)
    {
        const int maxPreviewChars = 320;
        var normalized = text.Trim();
        return normalized.Length <= maxPreviewChars ? normalized : normalized[..maxPreviewChars];
    }

    private static async ValueTask<PersistedRunState?> ReadRunStateAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, token_budget, tokens_spent, resume_cursor, observation_count
            FROM reference_analysis_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PersistedRunState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4));
    }

    private static async ValueTask UpsertRunAsync(
        SqliteConnection connection,
        ReferenceCorpusFeatureAnalysisRunRequest request,
        ReferenceCorpusAnalysisRunState state,
        int observationCount,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken)
    {
        await UpsertRunAsync(connection, null, request, state, observationCount, completedAt, cancellationToken);
    }

    private static async ValueTask UpsertRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ReferenceCorpusFeatureAnalysisRunRequest request,
        ReferenceCorpusAnalysisRunState state,
        int observationCount,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ($run_id, $anchor_id, $analyzer_version, $schema_version, $model_provider, $model_id,
               $scope, $status, $token_budget, $tokens_spent, $resume_cursor, $started_at, $completed_at, $observation_count)
            ON CONFLICT(run_id) DO UPDATE SET
              analyzer_version = excluded.analyzer_version,
              schema_version = excluded.schema_version,
              model_provider = excluded.model_provider,
              model_id = excluded.model_id,
              scope = excluded.scope,
              status = excluded.status,
              token_budget = excluded.token_budget,
              tokens_spent = excluded.tokens_spent,
              resume_cursor = excluded.resume_cursor,
              completed_at = excluded.completed_at,
              observation_count = excluded.observation_count;
            """;
        command.Parameters.AddWithValue("$run_id", request.RunId);
        command.Parameters.AddWithValue("$anchor_id", request.AnchorId);
        command.Parameters.AddWithValue("$analyzer_version", request.AnalyzerVersion);
        command.Parameters.AddWithValue("$schema_version", ReferenceCorpusFeatureFamilySchemaVersions.V1);
        command.Parameters.AddWithValue("$model_provider", request.ModelProvider);
        command.Parameters.AddWithValue("$model_id", request.ModelId);
        command.Parameters.AddWithValue("$scope", request.NodeType);
        command.Parameters.AddWithValue("$status", state.Status);
        command.Parameters.AddWithValue("$token_budget", state.TokenBudget is null ? DBNull.Value : state.TokenBudget.Value);
        command.Parameters.AddWithValue("$tokens_spent", state.TokensSpent);
        command.Parameters.AddWithValue("$resume_cursor", state.ResumeCursor is null ? DBNull.Value : state.ResumeCursor);
        command.Parameters.AddWithValue("$started_at", FormatTimestamp(request.StartedAt));
        command.Parameters.AddWithValue("$completed_at", completedAt is null ? DBNull.Value : FormatTimestamp(completedAt.Value));
        command.Parameters.AddWithValue("$observation_count", observationCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<int> CountObservationsAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken cancellationToken)
    {
        return await CountObservationsAsync(connection, null, runId, cancellationToken);
    }

    private static async ValueTask<int> CountObservationsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_feature_observations
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static ReferenceCorpusFeatureAnalysisRunResult BuildResult(
        string runId,
        ReferenceCorpusAnalysisRunState state,
        int observationCount,
        int processed,
        IReadOnlyList<string> diagnostics)
    {
        return new ReferenceCorpusFeatureAnalysisRunResult(
            runId,
            state.Status,
            state.TokensSpent,
            state.ResumeCursor,
            observationCount,
            processed,
            diagnostics);
    }

    private static string BuildCursor(string nodeId, string family)
    {
        return nodeId + "|" + family;
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private sealed record FeatureAnalysisNode(
        string NodeId,
        string NodeType,
        int SequenceIndex,
        string Text,
        ReferenceCorpusFeatureAnalysisContext Context);

    private sealed record FeatureAnalysisWorkItem(
        FeatureAnalysisNode Node,
        string Family,
        string Cursor);

private sealed record PersistedRunState(
string Status,
int? TokenBudget,
int TokensSpent,
string? ResumeCursor,
int ObservationCount);

 private static ReferenceCorpusAnalysisRunState ApplyExecutionAction(
 ReferenceCorpusAnalysisRunState state,
 string action)
 {
 return action switch
 {
 ReferenceCorpusAnalysisExecutionActions.Pause => ReferenceCorpusAnalysisRunStateMachine.Pause(state),
 ReferenceCorpusAnalysisExecutionActions.Cancel => ReferenceCorpusAnalysisRunStateMachine.MarkPartialCompleted(state),
 _ => throw new InvalidOperationException($"Unknown analysis execution action '{action}'.")
 };
 }
}
