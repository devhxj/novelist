using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<ReferenceMaterializationCandidateReviewMutation> ReviewCandidateAsync(
        string runId,
        string candidateId,
        string action,
        long expectedVersion,
        IReadOnlyList<ReferenceMaterializationCandidateSourceSpanPayload>? sourceSpans,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        var normalizedCandidateId = NormalizeCandidateId(candidateId);
        var normalizedAction = NormalizeReviewAction(action);
        if (expectedVersion < 0)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Materialization candidate review version is invalid.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureNoActiveReviewLeaseAsync(connection, transaction, normalizedRunId, cancellationToken);
        var target = await ReadReviewTargetAsync(connection, transaction, normalizedRunId, normalizedCandidateId, cancellationToken)
            ?? throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Materialization candidate does not exist in this run.");
        EnsureReviewTarget(target, expectedVersion);
        var nodes = await ReadReviewNodesAsync(connection, transaction, normalizedCandidateId, cancellationToken);
        ValidateReviewSpans(normalizedAction, sourceSpans, nodes);
        var now = DateTimeOffset.UtcNow;

        if (normalizedAction == ReferenceMaterializationCandidateReviewActions.Reject)
        {
            await RejectCandidateAsync(connection, transaction, normalizedCandidateId, expectedVersion, now, cancellationToken);
            await RefreshChapterCountsAsync(connection, transaction, normalizedRunId, target.ChapterIndex, cancellationToken);
            await RefreshRunCountsAsync(connection, transaction, normalizedRunId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReferenceMaterializationCandidateReviewMutation(
                normalizedCandidateId,
                ReferenceMaterializationCandidateDecisions.Rejected,
                expectedVersion + 1,
                RequalificationQueued: false);
        }

        if (normalizedAction == ReferenceMaterializationCandidateReviewActions.AdjustBoundary)
        {
            await PersistReviewSpansAsync(connection, transaction, normalizedCandidateId, sourceSpans!, cancellationToken);
        }

        await RequeueCandidateForQualificationAsync(
            connection,
            transaction,
            normalizedCandidateId,
            expectedVersion,
            normalizedAction,
            now,
            cancellationToken);
        await ReopenCandidateChapterAsync(connection, transaction, normalizedRunId, target, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReferenceMaterializationCandidateReviewMutation(
            normalizedCandidateId,
            ReferenceMaterializationCandidateDecisions.Pending,
            expectedVersion + 1,
            RequalificationQueued: true);
    }

    private static string NormalizeCandidateId(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 128 || normalized.Any(char.IsControl))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Materialization candidate id is invalid.");
        }

        return normalized;
    }

    private static string NormalizeReviewAction(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!ReferenceMaterializationCandidateReviewActions.All.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Materialization candidate review action is invalid.");
        }

        return normalized;
    }

    private static async ValueTask EnsureNoActiveReviewLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT lease_expires_at
            FROM reference_materialization_run_leases
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        var leaseExpiresAt = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (leaseExpiresAt is not null && DateTimeOffset.Parse(leaseExpiresAt) > DateTimeOffset.UtcNow)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Materialization candidate cannot be reviewed while its run has an active worker lease.");
        }

        if (leaseExpiresAt is not null)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_materialization_run_leases WHERE run_id = $run_id;";
            delete.Parameters.AddWithValue("$run_id", runId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask<CandidateReviewTarget?> ReadReviewTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT candidate.decision, candidate.row_version,
                   run.status, progress.chapter_index, progress.batch_index,
                   run.chapter_batch_size, run.total_chapters
            FROM reference_material_candidates candidate
            JOIN reference_materialization_runs run ON run.run_id = candidate.run_id
            JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            JOIN reference_chapter_split_boundaries boundary ON boundary.split_profile_id = run.split_profile_id
            JOIN reference_materialization_chapter_progress progress
              ON progress.run_id = run.run_id
             AND progress.chapter_index = boundary.chapter_index
            WHERE candidate.run_id = $run_id
              AND candidate.candidate_id = $candidate_id
              AND node.start_offset >= boundary.content_start
              AND node.end_offset <= boundary.content_end
            ORDER BY progress.chapter_index
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CandidateReviewTarget(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6))
            : null;
    }

    private static void EnsureReviewTarget(CandidateReviewTarget target, long expectedVersion)
    {
        if (target.RowVersion != expectedVersion)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewConflict,
                "Materialization candidate changed since it was opened. Refresh it before submitting a review.");
        }

        if (target.Decision != ReferenceMaterializationCandidateDecisions.ReviewRequired ||
            target.RunStatus != ReferenceMaterializationRunStates.Completed)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Only review-required candidates from a completed materialization run can be reviewed.");
        }
    }

    private static async ValueTask<IReadOnlyList<CandidateReviewNode>> ReadReviewNodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT candidate_node.node_id, node.text,
                   candidate_node.evidence_start, candidate_node.evidence_end
            FROM reference_material_candidate_nodes candidate_node
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            WHERE candidate_node.candidate_id = $candidate_id
            ORDER BY candidate_node.ordinal;
            """;
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = new List<CandidateReviewNode>();
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new CandidateReviewNode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3)));
        }

        if (nodes.Count == 0)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Materialization candidate has no source-node evidence.");
        }

        return nodes;
    }

    private static void ValidateReviewSpans(
        string action,
        IReadOnlyList<ReferenceMaterializationCandidateSourceSpanPayload>? sourceSpans,
        IReadOnlyList<CandidateReviewNode> nodes)
    {
        if (action != ReferenceMaterializationCandidateReviewActions.AdjustBoundary)
        {
            if (sourceSpans is { Count: > 0 })
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                    "Source spans are only valid for a boundary adjustment review.");
            }

            return;
        }

        if (sourceSpans is null || sourceSpans.Count != nodes.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                "Boundary adjustment must provide exactly one span for every source node.");
        }

        var nodesById = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var spanNodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in sourceSpans)
        {
            if (span is null ||
                !nodesById.TryGetValue(span.NodeId, out var node) ||
                !spanNodeIds.Add(span.NodeId) ||
                span.Start < 0 || span.End <= span.Start || span.End > node.Text.Length)
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.CandidateReviewInvalid,
                    "Boundary adjustment contains an invalid source span.");
            }
        }
    }

    private static async ValueTask RejectCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        long expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_material_candidates
            SET decision = $rejected,
                decision_origin = 'user_review_rejected',
                reviewed_at = $reviewed_at,
                row_version = row_version + 1
            WHERE candidate_id = $candidate_id
              AND decision = $review_required
              AND row_version = $expected_version;
            """;
        command.Parameters.AddWithValue("$rejected", ReferenceMaterializationCandidateDecisions.Rejected);
        command.Parameters.AddWithValue("$reviewed_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        command.Parameters.AddWithValue("$review_required", ReferenceMaterializationCandidateDecisions.ReviewRequired);
        command.Parameters.AddWithValue("$expected_version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewConflict,
                "Materialization candidate changed while applying the review.");
        }
    }

    private static async ValueTask PersistReviewSpansAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        IReadOnlyList<ReferenceMaterializationCandidateSourceSpanPayload> sourceSpans,
        CancellationToken cancellationToken)
    {
        foreach (var span in sourceSpans)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE reference_material_candidate_nodes
                SET evidence_start = $evidence_start,
                    evidence_end = $evidence_end
                WHERE candidate_id = $candidate_id
                  AND node_id = $node_id;
                """;
            command.Parameters.AddWithValue("$evidence_start", span.Start);
            command.Parameters.AddWithValue("$evidence_end", span.End);
            command.Parameters.AddWithValue("$candidate_id", candidateId);
            command.Parameters.AddWithValue("$node_id", span.NodeId);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.CandidateReviewConflict,
                    "Materialization candidate evidence changed while applying the review.");
            }
        }
    }

    private static async ValueTask RequeueCandidateForQualificationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        long expectedVersion,
        string action,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_material_candidates
            SET decision = $pending,
                decision_origin = $decision_origin,
                quality_score = NULL,
                confidence = NULL,
                scores_json = '{}',
                tags_json = '{}',
                reason_codes_json = '[]',
                reviewed_at = $reviewed_at,
                row_version = row_version + 1
            WHERE candidate_id = $candidate_id
              AND decision = $review_required
              AND row_version = $expected_version;
            """;
        command.Parameters.AddWithValue("$pending", ReferenceMaterializationCandidateDecisions.Pending);
        command.Parameters.AddWithValue("$decision_origin", action == ReferenceMaterializationCandidateReviewActions.AdjustBoundary
            ? "user_boundary_requalification"
            : "user_confirmed_requalification");
        command.Parameters.AddWithValue("$reviewed_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        command.Parameters.AddWithValue("$review_required", ReferenceMaterializationCandidateDecisions.ReviewRequired);
        command.Parameters.AddWithValue("$expected_version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.CandidateReviewConflict,
                "Materialization candidate changed while queuing requalification.");
        }

        await using var deleteEmbeddings = connection.CreateCommand();
        deleteEmbeddings.Transaction = transaction;
        deleteEmbeddings.CommandText = "DELETE FROM reference_materialization_candidate_embeddings WHERE candidate_id = $candidate_id;";
        deleteEmbeddings.Parameters.AddWithValue("$candidate_id", candidateId);
        await deleteEmbeddings.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask ReopenCandidateChapterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CandidateReviewTarget target,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
            ReferenceMaterializationChapterStates.Completed,
            ReferenceMaterializationChapterStates.LlmQualifying);
        var counts = await ReadChapterCountsAsync(connection, transaction, runId, target.ChapterIndex, cancellationToken);
        await using (var chapter = connection.CreateCommand())
        {
            chapter.Transaction = transaction;
            chapter.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET status = $qualifying,
                    current_stage = $qualifying,
                    candidate_count = $candidate_count,
                    decided_count = $decided_count,
                    accepted_count = $accepted_count,
                    rejected_count = $rejected_count,
                    review_count = $review_count,
                    vector_count = $vector_count,
                    started_at = $started_at,
                    completed_at = NULL,
                    last_error_code = NULL,
                    last_error_message = NULL,
                    row_version = row_version + 1
                WHERE run_id = $run_id
                  AND chapter_index = $chapter_index
                  AND status = $completed;
                """;
            chapter.Parameters.AddWithValue("$qualifying", ReferenceMaterializationChapterStates.LlmQualifying);
            chapter.Parameters.AddWithValue("$candidate_count", counts.CandidateCount);
            chapter.Parameters.AddWithValue("$decided_count", counts.DecidedCount);
            chapter.Parameters.AddWithValue("$accepted_count", counts.AcceptedCount);
            chapter.Parameters.AddWithValue("$rejected_count", counts.RejectedCount);
            chapter.Parameters.AddWithValue("$review_count", counts.ReviewCount);
            chapter.Parameters.AddWithValue("$vector_count", counts.VectorCount);
            chapter.Parameters.AddWithValue("$started_at", FormatTimestamp(now));
            chapter.Parameters.AddWithValue("$run_id", runId);
            chapter.Parameters.AddWithValue("$chapter_index", target.ChapterIndex);
            chapter.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            if (await chapter.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.CandidateReviewConflict,
                    "Materialization chapter changed while queuing requalification.");
            }
        }

        await RefreshRunCountsAsync(connection, transaction, runId, cancellationToken);
        var completedBatchCount = await CountCompletedBatchesAsync(connection, transaction, runId, cancellationToken);
        var completedChapterCount = await CountCompletedChaptersAsync(connection, transaction, runId, cancellationToken);
        ReferenceMaterializationRunStateMachine.EnsureCanTransition(
            ReferenceMaterializationRunStates.Completed,
            ReferenceMaterializationRunStates.Running);
        await using (var run = connection.CreateCommand())
        {
            run.Transaction = transaction;
            run.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $running,
                    processed_chapters = $processed_chapters,
                    completed_chapter_batches = $completed_chapter_batches,
                    current_batch_index = $current_batch_index,
                    current_batch_start_chapter = $current_batch_start_chapter,
                    current_batch_end_chapter = $current_batch_end_chapter,
                    completed_at = NULL,
                    last_error_code = NULL,
                    last_error_message = NULL
                WHERE run_id = $run_id
                  AND status = $completed;
                """;
            run.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
            run.Parameters.AddWithValue("$processed_chapters", completedChapterCount);
            run.Parameters.AddWithValue("$completed_chapter_batches", completedBatchCount);
            run.Parameters.AddWithValue("$current_batch_index", target.BatchIndex);
            run.Parameters.AddWithValue("$current_batch_start_chapter", target.BatchIndex * target.ChapterBatchSize + 1);
            run.Parameters.AddWithValue("$current_batch_end_chapter", Math.Min((target.BatchIndex + 1) * target.ChapterBatchSize, target.TotalChapters));
            run.Parameters.AddWithValue("$run_id", runId);
            run.Parameters.AddWithValue("$completed", ReferenceMaterializationRunStates.Completed);
            if (await run.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.CandidateReviewConflict,
                    "Materialization run changed while queuing requalification.");
            }
        }

        await using var index = connection.CreateCommand();
        index.Transaction = transaction;
        index.CommandText = """
            UPDATE reference_materialization_vector_indexes
            SET status = 'building',
                updated_at = $updated_at
            WHERE run_id = $run_id;
            """;
        index.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        index.Parameters.AddWithValue("$run_id", runId);
        await index.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask RefreshChapterCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        var counts = await ReadChapterCountsAsync(connection, transaction, runId, chapterIndex, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET candidate_count = $candidate_count,
                decided_count = $decided_count,
                accepted_count = $accepted_count,
                rejected_count = $rejected_count,
                review_count = $review_count,
                vector_count = $vector_count,
                row_version = row_version + 1
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$candidate_count", counts.CandidateCount);
        command.Parameters.AddWithValue("$decided_count", counts.DecidedCount);
        command.Parameters.AddWithValue("$accepted_count", counts.AcceptedCount);
        command.Parameters.AddWithValue("$rejected_count", counts.RejectedCount);
        command.Parameters.AddWithValue("$review_count", counts.ReviewCount);
        command.Parameters.AddWithValue("$vector_count", counts.VectorCount);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<ChapterCounts> ReadChapterCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN decision <> $pending THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN decision = $accepted THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN decision = $rejected THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN decision = $review THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN decision = $accepted AND has_embedding = 1 THEN 1 ELSE 0 END), 0)
            FROM (
              SELECT candidate.candidate_id, candidate.decision,
                     MAX(CASE WHEN embedding.candidate_id IS NULL THEN 0 ELSE 1 END) AS has_embedding
              FROM reference_material_candidates candidate
              JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
              JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
              JOIN reference_materialization_runs run ON run.run_id = candidate.run_id
              JOIN reference_chapter_split_boundaries boundary ON boundary.split_profile_id = run.split_profile_id
              LEFT JOIN reference_materialization_candidate_embeddings embedding
                ON embedding.run_id = candidate.run_id
               AND embedding.candidate_id = candidate.candidate_id
              WHERE candidate.run_id = $run_id
                AND boundary.chapter_index = $chapter_index
                AND node.start_offset >= boundary.content_start
                AND node.end_offset <= boundary.content_end
              GROUP BY candidate.candidate_id, candidate.decision
            ) candidate;
            """;
        command.Parameters.AddWithValue("$pending", ReferenceMaterializationCandidateDecisions.Pending);
        command.Parameters.AddWithValue("$accepted", ReferenceMaterializationCandidateDecisions.Accepted);
        command.Parameters.AddWithValue("$rejected", ReferenceMaterializationCandidateDecisions.Rejected);
        command.Parameters.AddWithValue("$review", ReferenceMaterializationCandidateDecisions.ReviewRequired);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Materialization chapter counts are unavailable.");
        }

        return new ChapterCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5));
    }

    private sealed record CandidateReviewTarget(
        string Decision,
        long RowVersion,
        string RunStatus,
        int ChapterIndex,
        int BatchIndex,
        int ChapterBatchSize,
        int TotalChapters);

    private sealed record CandidateReviewNode(
        string NodeId,
        string Text,
        int EvidenceStart,
        int EvidenceEnd);

    private sealed record ChapterCounts(
        int CandidateCount,
        int DecidedCount,
        int AcceptedCount,
        int RejectedCount,
        int ReviewCount,
        int VectorCount);
}

internal sealed record ReferenceMaterializationCandidateReviewMutation(
    string CandidateId,
    string Decision,
    long RowVersion,
    bool RequalificationQueued);
