using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<bool> PromoteIfReadyAsync(string runId, CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadPromotionRunAsync(connection, transaction, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(runId));
        if (run.Status == ReferenceMaterializationRunStates.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        if (run.Status != ReferenceMaterializationRunStates.Running || run.CurrentBatchIndex is not null ||
            !await IsGenerationReadyForPromotionAsync(connection, transaction, run, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var materials = await ReadAcceptedMaterialsAsync(connection, transaction, run, cancellationToken);
        if (materials.Count != run.AcceptedCount)
        {
            throw new InvalidOperationException("Materialization generation accepted-material projection is incomplete.");
        }

        foreach (var material in materials)
        {
            await InsertMaterialAsync(connection, transaction, run, material, cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        await ActivateGenerationAsync(connection, transaction, run, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async ValueTask<PromotionRun?> ReadPromotionRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, anchor_id, generation_id, status, current_batch_index,
                   total_chapters, processed_chapters, accepted_count, vector_count,
                   embedding_provider, embedding_model_id, embedding_dimensions
            FROM reference_materialization_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PromotionRun(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetInt32(11))
            : null;
    }

    private static async ValueTask<bool> IsGenerationReadyForPromotionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PromotionRun run,
        CancellationToken cancellationToken)
    {
        if (run.ProcessedChapters != run.TotalChapters || run.VectorCount != run.AcceptedCount)
        {
            return false;
        }

        await using (var chapters = connection.CreateCommand())
        {
            chapters.Transaction = transaction;
            chapters.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN status = $completed AND vector_count = accepted_count THEN 1 ELSE 0 END), 0)
                FROM reference_materialization_chapter_progress
                WHERE run_id = $run_id;
                """;
            chapters.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            chapters.Parameters.AddWithValue("$run_id", run.RunId);
            await using var reader = await chapters.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != run.TotalChapters || reader.GetInt32(1) != run.TotalChapters)
            {
                return false;
            }
        }

        await using var index = connection.CreateCommand();
        index.Transaction = transaction;
        index.CommandText = """
            SELECT vector_count
            FROM reference_materialization_vector_indexes
            WHERE generation_id = $generation_id
              AND run_id = $run_id
              AND provider = $provider
              AND model_id = $model_id
              AND dimensions = $dimensions
              AND status = 'ready';
            """;
        index.Parameters.AddWithValue("$generation_id", run.GenerationId);
        index.Parameters.AddWithValue("$run_id", run.RunId);
        index.Parameters.AddWithValue("$provider", run.EmbeddingProvider);
        index.Parameters.AddWithValue("$model_id", run.EmbeddingModelId);
        index.Parameters.AddWithValue("$dimensions", run.EmbeddingDimensions);
        var vectorCount = await index.ExecuteScalarAsync(cancellationToken);
        return vectorCount is not null && Convert.ToInt32(vectorCount) == run.AcceptedCount;
    }

    private static async ValueTask<IReadOnlyList<PromotableMaterial>> ReadAcceptedMaterialsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PromotionRun run,
        CancellationToken cancellationToken)
    {
        var candidates = new List<PromotionCandidate>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT candidate_id, candidate_type, text_hash, quality_score, confidence,
                       scores_json, tags_json, reason_codes_json
                FROM reference_material_candidates
                WHERE run_id = $run_id
                  AND decision = $accepted
                ORDER BY candidate_id;
                """;
            command.Parameters.AddWithValue("$run_id", run.RunId);
            command.Parameters.AddWithValue("$accepted", ReferenceMaterializationCandidateDecisions.Accepted);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new PromotionCandidate(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetString(7)));
            }
        }

        var materials = new List<PromotableMaterial>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var nodes = await ReadPromotableNodesAsync(connection, transaction, candidate.CandidateId, cancellationToken);
            if (nodes.Count == 0)
            {
                throw new InvalidOperationException("Accepted materialization candidate has no source evidence.");
            }

            var text = string.Join("\n", nodes.Select(node => node.Text[node.EvidenceStart..node.EvidenceEnd]));
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new InvalidOperationException("Accepted materialization candidate has empty projected text.");
            }

            materials.Add(new PromotableMaterial(candidate, text, nodes));
        }

        return materials;
    }

    private static async ValueTask<IReadOnlyList<PromotableNode>> ReadPromotableNodesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string candidateId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT candidate_node.node_id, candidate_node.ordinal,
                   candidate_node.evidence_start, candidate_node.evidence_end,
                   candidate_node.text_hash, node.text
            FROM reference_material_candidate_nodes candidate_node
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            WHERE candidate_node.candidate_id = $candidate_id
            ORDER BY candidate_node.ordinal;
            """;
        command.Parameters.AddWithValue("$candidate_id", candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var nodes = new List<PromotableNode>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var text = reader.GetString(5);
            var start = reader.GetInt32(2);
            var end = reader.GetInt32(3);
            if (start < 0 || end <= start || end > text.Length)
            {
                throw new InvalidOperationException("Materialization evidence offsets are invalid during promotion.");
            }

            nodes.Add(new PromotableNode(
                reader.GetString(0),
                reader.GetInt32(1),
                start,
                end,
                reader.GetString(4),
                text));
        }

        return nodes;
    }

    private static async ValueTask InsertMaterialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PromotionRun run,
        PromotableMaterial material,
        CancellationToken cancellationToken)
    {
        var materialId = "materialization-material-" + HashPromotionValue(run.GenerationId + "|" + material.Candidate.CandidateId)[..24];
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_materialization_materials (
                  material_id, generation_id, run_id, candidate_id, anchor_id, material_type, text, text_hash,
                  quality_score, confidence, scores_json, tags_json, reason_codes_json, created_at)
                VALUES (
                  $material_id, $generation_id, $run_id, $candidate_id, $anchor_id, $material_type, $text, $text_hash,
                  $quality_score, $confidence, $scores_json, $tags_json, $reason_codes_json, $created_at)
                ON CONFLICT(generation_id, candidate_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$material_id", materialId);
            command.Parameters.AddWithValue("$generation_id", run.GenerationId);
            command.Parameters.AddWithValue("$run_id", run.RunId);
            command.Parameters.AddWithValue("$candidate_id", material.Candidate.CandidateId);
            command.Parameters.AddWithValue("$anchor_id", run.AnchorId);
            command.Parameters.AddWithValue("$material_type", material.Candidate.CandidateType);
            command.Parameters.AddWithValue("$text", material.Text);
            command.Parameters.AddWithValue("$text_hash", HashPromotionValue(material.Text));
            command.Parameters.AddWithValue("$quality_score", material.Candidate.QualityScore);
            command.Parameters.AddWithValue("$confidence", material.Candidate.Confidence);
            command.Parameters.AddWithValue("$scores_json", material.Candidate.ScoresJson);
            command.Parameters.AddWithValue("$tags_json", material.Candidate.TagsJson);
            command.Parameters.AddWithValue("$reason_codes_json", material.Candidate.ReasonCodesJson);
            command.Parameters.AddWithValue("$created_at", FormatTimestamp(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var node in material.Nodes)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reference_materialization_material_nodes (
                  material_id, node_id, ordinal, evidence_start, evidence_end, text_hash)
                VALUES ($material_id, $node_id, $ordinal, $evidence_start, $evidence_end, $text_hash)
                ON CONFLICT(material_id, ordinal) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$material_id", materialId);
            command.Parameters.AddWithValue("$node_id", node.NodeId);
            command.Parameters.AddWithValue("$ordinal", node.Ordinal);
            command.Parameters.AddWithValue("$evidence_start", node.EvidenceStart);
            command.Parameters.AddWithValue("$evidence_end", node.EvidenceEnd);
            command.Parameters.AddWithValue("$text_hash", node.TextHash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask ActivateGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PromotionRun run,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO reference_anchor_materialization_state (
                  anchor_id, active_generation_id, previous_generation_id, row_version, updated_at)
                VALUES ($anchor_id, $generation_id, NULL, 0, $updated_at)
                ON CONFLICT(anchor_id) DO UPDATE SET
                  previous_generation_id = CASE
                    WHEN reference_anchor_materialization_state.active_generation_id = excluded.active_generation_id
                    THEN reference_anchor_materialization_state.previous_generation_id
                    ELSE reference_anchor_materialization_state.active_generation_id
                  END,
                  active_generation_id = excluded.active_generation_id,
                  row_version = reference_anchor_materialization_state.row_version + 1,
                  updated_at = excluded.updated_at;
                """;
            state.Parameters.AddWithValue("$anchor_id", run.AnchorId);
            state.Parameters.AddWithValue("$generation_id", run.GenerationId);
            state.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        ReferenceMaterializationRunStateMachine.EnsureCanTransition(
            ReferenceMaterializationRunStates.Running,
            ReferenceMaterializationRunStates.Completed);
        await using var runCommand = connection.CreateCommand();
        runCommand.Transaction = transaction;
        runCommand.CommandText = """
            UPDATE reference_materialization_runs
            SET status = $completed,
                completed_at = $completed_at,
                activated_at = $activated_at
            WHERE run_id = $run_id
              AND status = $running;
            """;
        runCommand.Parameters.AddWithValue("$completed", ReferenceMaterializationRunStates.Completed);
        runCommand.Parameters.AddWithValue("$completed_at", FormatTimestamp(now));
        runCommand.Parameters.AddWithValue("$activated_at", FormatTimestamp(now));
        runCommand.Parameters.AddWithValue("$run_id", run.RunId);
        runCommand.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
        if (await runCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization run changed while promoting its generation.");
        }
    }

    private static string HashPromotionValue(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record PromotionRun(
        string RunId,
        long AnchorId,
        string GenerationId,
        string Status,
        int? CurrentBatchIndex,
        int TotalChapters,
        int ProcessedChapters,
        int AcceptedCount,
        int VectorCount,
        string EmbeddingProvider,
        string EmbeddingModelId,
        int EmbeddingDimensions);

    private sealed record PromotionCandidate(
        string CandidateId,
        string CandidateType,
        string CandidateTextHash,
        double QualityScore,
        double Confidence,
        string ScoresJson,
        string TagsJson,
        string ReasonCodesJson);

    private sealed record PromotableMaterial(
        PromotionCandidate Candidate,
        string Text,
        IReadOnlyList<PromotableNode> Nodes);

    private sealed record PromotableNode(
        string NodeId,
        int Ordinal,
        int EvidenceStart,
        int EvidenceEnd,
        string TextHash,
        string Text);
}
