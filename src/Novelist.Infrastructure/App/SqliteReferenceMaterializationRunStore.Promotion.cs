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

        // 零候选守卫：整轮完成但一个候选都没有，几乎必然是模型侧故障
        //（空摘录 / 全部未通过逐字校验）。标记为 failed 并给出重新材料化的指引，
        // 而不是静默显示"已完成 · 全 0"。
        if (run.CandidateCount == 0)
        {
            await MarkPromotionRunFailedAsync(
                connection,
                transaction,
                normalizedRunId,
                ReferenceMaterializationErrorCodes.LlmOutputInvalid,
                "材料化完成但未产生任何候选材料：模型没有摘录有效内容（或摘录均未通过逐字校验）。请更换模型或调整后重新材料化。",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        // 判定丢失守卫：仍有 pending 候选却已经全部章节收尾，只可能是判定结果被抹掉了。
        // 两条流水线的收尾条件都要求 pending 归零（判定阶段 isComplete、提取阶段从不写
        // pending），因此这里看到 pending 必然是中断恢复造成的丢失。放行的话会用一个缺
        // 材料的代次顶掉上一代可用材料，且进度看起来完全正常——必须明确失败并指引重做。
        // 判定依据取候选表本身，不依赖进度表汇总（进度表可能被中断恢复清零）。
        if (await CountPendingCandidatesAsync(connection, transaction, normalizedRunId, cancellationToken) > 0)
        {
            await MarkPromotionRunFailedAsync(
                connection,
                transaction,
                normalizedRunId,
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "材料化完成但仍有未判定的候选材料：判定结果在中断恢复中丢失，放行的话会丢料。请重新材料化本书。",
                cancellationToken);
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

    // 晋升守卫统一落库：running -> failed，并写回错误码与用户可见的重做指引。
    private static async ValueTask MarkPromotionRunFailedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        ReferenceMaterializationRunStateMachine.EnsureCanTransition(
            ReferenceMaterializationRunStates.Running,
            ReferenceMaterializationRunStates.Failed);
        await using var failure = connection.CreateCommand();
        failure.Transaction = transaction;
        failure.CommandText = """
            UPDATE reference_materialization_runs
            SET status = $failed,
                last_error_code = $error_code,
                last_error_message = $error_message,
                completed_at = $completed_at
            WHERE run_id = $run_id AND status = $running;
            """;
        failure.Parameters.AddWithValue("$failed", ReferenceMaterializationRunStates.Failed);
        failure.Parameters.AddWithValue("$error_code", errorCode);
        failure.Parameters.AddWithValue("$error_message", errorMessage);
        failure.Parameters.AddWithValue("$completed_at", FormatTimestamp(DateTimeOffset.UtcNow));
        failure.Parameters.AddWithValue("$run_id", runId);
        failure.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
        await failure.ExecuteNonQueryAsync(cancellationToken);
    }

    // 仍未判定的候选数：整轮收尾时用来识别"判定丢失"。
    private static async ValueTask<int> CountPendingCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_material_candidates
            WHERE run_id = $run_id
              AND decision = $pending;
            """;
        command.Parameters.AddWithValue("$pending", ReferenceMaterializationCandidateDecisions.Pending);
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
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
                   embedding_provider, embedding_model_id, embedding_dimensions, candidate_count
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
                reader.GetInt32(11),
                reader.GetInt32(12))
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
        int EmbeddingDimensions,
        int CandidateCount);

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
