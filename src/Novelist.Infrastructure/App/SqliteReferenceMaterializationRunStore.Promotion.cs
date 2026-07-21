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

        if (run.Status != ReferenceMaterializationRunStates.Indexing ||
            run.CurrentBatchIndex is not null ||
            !await IsGenerationReadyForPromotionAsync(connection, transaction, run, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        var previousGenerationId = await ReadActiveGenerationAsync(
            connection,
            transaction,
            run.AnchorId,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await ActivateGenerationAsync(connection, transaction, run, now, cancellationToken);
        if (previousGenerationId is not null &&
            !string.Equals(previousGenerationId, run.GenerationId, StringComparison.Ordinal))
        {
            await DeleteGenerationAsync(connection, transaction, previousGenerationId, cancellationToken);
        }

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
                   total_chapters, processed_chapters, material_count, vector_count,
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
        if (run.ProcessedChapters != run.TotalChapters ||
            run.MaterialCount <= 0 ||
            run.VectorCount != run.MaterialCount)
        {
            return false;
        }

        await using (var chapters = connection.CreateCommand())
        {
            chapters.Transaction = transaction;
            chapters.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN status = $completed
                                              AND material_count > 0
                                              AND vector_count = material_count
                                         THEN 1 ELSE 0 END), 0)
                FROM reference_materialization_chapter_progress
                WHERE run_id = $run_id;
                """;
            chapters.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            chapters.Parameters.AddWithValue("$run_id", run.RunId);
            await using var reader = await chapters.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                reader.GetInt32(0) != run.TotalChapters ||
                reader.GetInt32(1) != run.TotalChapters)
            {
                return false;
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT vector_count,
                   (SELECT COUNT(*)
                    FROM reference_materials
                    WHERE generation_id = $generation_id),
                   (SELECT COUNT(*)
                    FROM reference_material_embeddings
                    WHERE generation_id = $generation_id)
            FROM reference_materialization_vector_indexes
            WHERE generation_id = $generation_id
              AND run_id = $run_id
              AND provider = $provider
              AND model_id = $model_id
              AND dimensions = $dimensions
              AND status = 'ready';
            """;
        command.Parameters.AddWithValue("$generation_id", run.GenerationId);
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$provider", run.EmbeddingProvider);
        command.Parameters.AddWithValue("$model_id", run.EmbeddingModelId);
        command.Parameters.AddWithValue("$dimensions", run.EmbeddingDimensions);
        await using var counts = await command.ExecuteReaderAsync(cancellationToken);
        return await counts.ReadAsync(cancellationToken) &&
               counts.GetInt32(0) == run.MaterialCount &&
               counts.GetInt32(1) == run.MaterialCount &&
               counts.GetInt32(2) == run.MaterialCount;
    }

    private static async ValueTask<string?> ReadActiveGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT active_generation_id
            FROM reference_anchor_materialization_state
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (string)value;
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
                  anchor_id, active_generation_id, row_version, updated_at)
                VALUES ($anchor_id, $generation_id, 0, $updated_at)
                ON CONFLICT(anchor_id) DO UPDATE SET
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
            ReferenceMaterializationRunStates.Indexing,
            ReferenceMaterializationRunStates.Completed);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_runs
            SET status = $completed,
                completed_at = $completed_at,
                activated_at = $activated_at
            WHERE run_id = $run_id
              AND status = $indexing;
            """;
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationRunStates.Completed);
        command.Parameters.AddWithValue("$completed_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$activated_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization run changed while activating its generation.");
        }
    }

    private static async ValueTask DeleteGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using (var embeddings = connection.CreateCommand())
        {
            embeddings.Transaction = transaction;
            embeddings.CommandText = """
                DELETE FROM reference_material_embeddings
                WHERE generation_id = $generation_id;
                """;
            embeddings.Parameters.AddWithValue("$generation_id", generationId);
            await embeddings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var materials = connection.CreateCommand())
        {
            materials.Transaction = transaction;
            materials.CommandText = """
                DELETE FROM reference_materials
                WHERE generation_id = $generation_id;
                """;
            materials.Parameters.AddWithValue("$generation_id", generationId);
            await materials.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var index = connection.CreateCommand();
        index.Transaction = transaction;
        index.CommandText = """
            DELETE FROM reference_materialization_vector_indexes
            WHERE generation_id = $generation_id;
            """;
        index.Parameters.AddWithValue("$generation_id", generationId);
        await index.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record PromotionRun(
        string RunId,
        long AnchorId,
        string GenerationId,
        string Status,
        int? CurrentBatchIndex,
        int TotalChapters,
        int ProcessedChapters,
        int MaterialCount,
        int VectorCount,
        string EmbeddingProvider,
        string EmbeddingModelId,
        int EmbeddingDimensions);
}
