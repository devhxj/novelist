using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<ReferenceCandidateBuildResult> BuildCandidatesForChapterAsync(
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        if (chapterIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex), "Chapter index must be positive.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var chapter = await ReadCandidateBuildChapterAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken)
            ?? throw new ArgumentException("Materialization chapter progress does not exist.", nameof(chapterIndex));
        if (chapter.Status == ReferenceMaterializationChapterStates.LlmQualifying)
        {
            var existingCount = await CountChapterCandidatesAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken);
            var existingDecisions = await ReadChapterDecisionCountsAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReferenceCandidateBuildResult(
                chapterIndex,
                existingCount,
                existingDecisions.PendingCount,
                existingDecisions.AcceptedCount,
                WasAlreadyBuilt: true);
        }

        ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
            chapter.Status,
            ReferenceMaterializationChapterStates.BuildingCandidates);
        var nodes = await ReadCandidateSourceNodesAsync(connection, transaction, chapter, cancellationToken);
        var windows = _candidateWindowBuilder.Build(new ReferenceCandidateChapterInput(
            chapter.AnchorId,
            chapter.ChapterIndex,
            chapter.ContentStart,
            chapter.ContentEnd,
            nodes));
        await InsertCandidateWindowsAsync(connection, transaction, normalizedRunId, chapter.AnchorId, windows, cancellationToken);
        ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
            ReferenceMaterializationChapterStates.BuildingCandidates,
            ReferenceMaterializationChapterStates.LlmQualifying);
        var candidateCount = await CountChapterCandidatesAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken);
        var decisionCounts = await ReadChapterDecisionCountsAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken);
        if (decisionCounts.PendingCount == 0)
        {
            ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
                ReferenceMaterializationChapterStates.LlmQualifying,
                ReferenceMaterializationChapterStates.Embedding);
        }

        await UpdateChapterCandidateProgressAsync(
            connection,
            transaction,
            normalizedRunId,
            chapterIndex,
            candidateCount,
            decisionCounts,
            cancellationToken);
        await RefreshRunCountsAsync(connection, transaction, normalizedRunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReferenceCandidateBuildResult(
            chapterIndex,
            candidateCount,
            decisionCounts.PendingCount,
            decisionCounts.AcceptedCount,
            WasAlreadyBuilt: false);
    }

    private static async ValueTask<CandidateBuildChapter?> ReadCandidateBuildChapterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run.anchor_id, boundary.chapter_index, boundary.content_start, boundary.content_end,
                   progress.status
            FROM reference_materialization_runs run
            JOIN reference_chapter_split_boundaries boundary ON boundary.split_profile_id = run.split_profile_id
            JOIN reference_materialization_chapter_progress progress
              ON progress.run_id = run.run_id
             AND progress.chapter_index = boundary.chapter_index
            WHERE run.run_id = $run_id
              AND boundary.chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CandidateBuildChapter(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4))
            : null;
    }

    private static async ValueTask<IReadOnlyList<ReferenceCandidateSourceNode>> ReadCandidateSourceNodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CandidateBuildChapter chapter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT node.node_id, segment.segment_type, node.start_offset, node.end_offset, node.text, node.text_hash
            FROM reference_text_nodes node
            JOIN reference_source_segments segment ON segment.node_id = node.node_id
            WHERE node.anchor_id = $anchor_id
              AND segment.segment_type IN ('paragraph', 'sentence')
              AND node.start_offset >= $content_start
              AND node.end_offset <= $content_end
            ORDER BY node.start_offset, node.end_offset, node.node_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", chapter.AnchorId);
        command.Parameters.AddWithValue("$content_start", chapter.ContentStart);
        command.Parameters.AddWithValue("$content_end", chapter.ContentEnd);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = new List<ReferenceCandidateSourceNode>();
        while (await reader.ReadAsync(cancellationToken))
        {
            nodes.Add(new ReferenceCandidateSourceNode(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return nodes;
    }

    private static async ValueTask InsertCandidateWindowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        long anchorId,
        IReadOnlyList<ReferenceMaterialCandidateWindow> windows,
        CancellationToken cancellationToken)
    {
        var now = FormatTimestamp(DateTimeOffset.UtcNow);
        foreach (var window in windows)
        {
            var candidateId = HashText(runId + "|" + window.CandidateKey);
            await using (var insertCandidate = connection.CreateCommand())
            {
                insertCandidate.Transaction = transaction;
                insertCandidate.CommandText = """
                    INSERT OR IGNORE INTO reference_material_candidates (
                      candidate_id, candidate_key, run_id, anchor_id, candidate_type, text_hash,
                      decision, decision_origin, scores_json, tags_json, reason_codes_json, created_at)
                    VALUES (
                      $candidate_id, $candidate_key, $run_id, $anchor_id, $candidate_type, $text_hash,
                      $decision, $decision_origin, '{}', '{}', $reason_codes_json, $created_at);
                    """;
                insertCandidate.Parameters.AddWithValue("$candidate_id", candidateId);
                insertCandidate.Parameters.AddWithValue("$candidate_key", window.CandidateKey);
                insertCandidate.Parameters.AddWithValue("$run_id", runId);
                insertCandidate.Parameters.AddWithValue("$anchor_id", anchorId);
                insertCandidate.Parameters.AddWithValue("$candidate_type", window.CandidateType);
                insertCandidate.Parameters.AddWithValue("$text_hash", window.TextHash);
                insertCandidate.Parameters.AddWithValue("$decision", window.InitialDecision);
                insertCandidate.Parameters.AddWithValue("$decision_origin", window.DecisionOrigin);
                insertCandidate.Parameters.AddWithValue("$reason_codes_json", JsonSerializer.Serialize(window.ReasonCodes));
                insertCandidate.Parameters.AddWithValue("$created_at", now);
                await insertCandidate.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var ordinal = 0; ordinal < window.SourceNodes.Count; ordinal++)
            {
                var node = window.SourceNodes[ordinal];
                await using var insertNode = connection.CreateCommand();
                insertNode.Transaction = transaction;
                insertNode.CommandText = """
                    INSERT OR IGNORE INTO reference_material_candidate_nodes (
                      candidate_id, node_id, ordinal, evidence_start, evidence_end, text_hash)
                    VALUES (
                      $candidate_id, $node_id, $ordinal, 0, $evidence_end, $text_hash);
                    """;
                insertNode.Parameters.AddWithValue("$candidate_id", candidateId);
                insertNode.Parameters.AddWithValue("$node_id", node.NodeId);
                insertNode.Parameters.AddWithValue("$ordinal", ordinal);
                insertNode.Parameters.AddWithValue("$evidence_end", node.Text.Length);
                insertNode.Parameters.AddWithValue("$text_hash", node.TextHash);
                await insertNode.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async ValueTask<int> CountChapterCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(DISTINCT candidate.candidate_id)
            FROM reference_material_candidates candidate
            JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            JOIN reference_materialization_runs run ON run.run_id = candidate.run_id
            JOIN reference_chapter_split_boundaries boundary ON boundary.split_profile_id = run.split_profile_id
            WHERE candidate.run_id = $run_id
              AND boundary.chapter_index = $chapter_index
              AND node.start_offset >= boundary.content_start
              AND node.end_offset <= boundary.content_end;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async ValueTask<CandidateDecisionCounts> ReadChapterDecisionCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
              COALESCE(SUM(CASE WHEN candidate.decision = $pending THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN candidate.decision = $accepted THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN candidate.decision = $rejected THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN candidate.decision = $review THEN 1 ELSE 0 END), 0)
            FROM (
              SELECT candidate.candidate_id, candidate.decision
              FROM reference_material_candidates candidate
              JOIN reference_material_candidate_nodes candidate_node ON candidate_node.candidate_id = candidate.candidate_id
              JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
              JOIN reference_materialization_runs run ON run.run_id = candidate.run_id
              JOIN reference_chapter_split_boundaries boundary ON boundary.split_profile_id = run.split_profile_id
              WHERE candidate.run_id = $run_id
                AND boundary.chapter_index = $chapter_index
                AND node.anchor_id = run.anchor_id
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
            throw new InvalidOperationException("Materialization candidate counts are unavailable.");
        }

        return new CandidateDecisionCounts(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3));
    }

    private static async ValueTask UpdateChapterCandidateProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        int candidateCount,
        CandidateDecisionCounts decisionCounts,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET status = $status,
                current_stage = $current_stage,
                candidate_count = $candidate_count,
                decided_count = $decided_count,
                accepted_count = $accepted_count,
                rejected_count = $rejected_count,
                review_count = $review_count,
                row_version = row_version + 1,
                started_at = COALESCE(started_at, $started_at)
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index;
            """;
        var nextStage = decisionCounts.PendingCount == 0
            ? ReferenceMaterializationChapterStates.Embedding
            : ReferenceMaterializationChapterStates.LlmQualifying;
        command.Parameters.AddWithValue("$status", nextStage);
        command.Parameters.AddWithValue("$current_stage", nextStage);
        command.Parameters.AddWithValue("$candidate_count", candidateCount);
        command.Parameters.AddWithValue("$decided_count", candidateCount - decisionCounts.PendingCount);
        command.Parameters.AddWithValue("$accepted_count", decisionCounts.AcceptedCount);
        command.Parameters.AddWithValue("$rejected_count", decisionCounts.RejectedCount);
        command.Parameters.AddWithValue("$review_count", decisionCounts.ReviewCount);
        command.Parameters.AddWithValue("$started_at", FormatTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CandidateBuildChapter(
        long AnchorId,
        int ChapterIndex,
        int ContentStart,
        int ContentEnd,
        string Status);

    private sealed record CandidateDecisionCounts(
        int PendingCount,
        int AcceptedCount,
        int RejectedCount,
        int ReviewCount);
}

internal sealed record ReferenceCandidateBuildResult(
    int ChapterIndex,
    int CandidateCount,
    int PendingCandidateCount,
    int AcceptedCandidateCount,
    bool WasAlreadyBuilt);
