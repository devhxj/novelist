using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<ReferenceMaterializationVectorIndexWorkItem> ReadCurrentBatchVectorIndexWorkItemAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var run = await ReadVectorIndexRunAsync(connection, null, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(runId));
        if (run.Status != ReferenceMaterializationRunStates.Running || run.CurrentBatchIndex is null)
        {
            throw new InvalidOperationException("Materialization run has no active batch to index.");
        }

        await EnsureBatchReadyForIndexAsync(connection, null, run, cancellationToken);
        var vectors = await ReadGenerationVectorsAsync(connection, null, run, cancellationToken);
        var materialCount = await CountGenerationMaterialsAsync(connection, null, run, cancellationToken);
        if (materialCount == 0 || vectors.Count != materialCount)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Materialization generation does not have a complete non-empty vector set.");
        }

        return new ReferenceMaterializationVectorIndexWorkItem(
            run.RunId,
            run.GenerationId,
            run.CurrentBatchIndex.Value,
            run.EmbeddingProvider,
            run.EmbeddingModelId,
            run.EmbeddingDimensions,
            SqliteVecTableProvisioner.BuildReferenceMaterializationVectorTableName(
                run.GenerationId,
                run.EmbeddingDimensions),
            vectors);
    }

    public async ValueTask<ReferenceMaterializationVectorIndexResult> CompleteCurrentBatchIndexAsync(
        ReferenceMaterializationVectorIndexWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadVectorIndexRunAsync(connection, transaction, NormalizeRunId(workItem.RunId), cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(workItem));
        ValidateIndexWorkItem(run, workItem);
        await EnsureBatchReadyForIndexAsync(connection, transaction, run, cancellationToken);
        var vectors = await ReadGenerationVectorsAsync(connection, transaction, run, cancellationToken);
        var materialCount = await CountGenerationMaterialsAsync(connection, transaction, run, cancellationToken);
        if (materialCount == 0 || vectors.Count != materialCount || vectors.Count != workItem.Vectors.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Materialization generation changed before vector index completion.");
        }

        var now = DateTimeOffset.UtcNow;
        await UpsertVectorIndexMetadataAsync(connection, transaction, run, workItem, now, cancellationToken);
        var completedBatchCount = await CountCompletedBatchesAsync(connection, transaction, run.RunId, cancellationToken);
        var processedChapterCount = await CountCompletedChaptersAsync(connection, transaction, run.RunId, cancellationToken);
        var nextBatchIndex = await FindFirstIncompleteBatchIndexAsync(connection, transaction, run.RunId, cancellationToken);
        await AdvanceRunBatchAsync(
            connection,
            transaction,
            run,
            nextBatchIndex,
            completedBatchCount,
            processedChapterCount,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReferenceMaterializationVectorIndexResult(
            workItem.BatchIndex,
            await CountBatchChaptersAsync(connection, workItem.RunId, workItem.BatchIndex, cancellationToken),
            vectors.Count,
            nextBatchIndex);
    }

    private static async ValueTask<VectorIndexRun?> ReadVectorIndexRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, generation_id, status,
                   embedding_provider, embedding_model_id, embedding_dimensions,
                   chapter_batch_size, total_chapters, current_batch_index
            FROM reference_materialization_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new VectorIndexRun(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8))
            : null;
    }

    private static async ValueTask EnsureBatchReadyForIndexAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        VectorIndexRun run,
        CancellationToken cancellationToken)
    {
        if (run.CurrentBatchIndex is null)
        {
            throw new InvalidOperationException("Materialization run has no active batch to index.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN status = $completed
                                          AND material_count > 0
                                          AND vector_count = material_count
                                     THEN 1 ELSE 0 END), 0)
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND batch_index = $batch_index;
            """;
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$batch_index", run.CurrentBatchIndex.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt32(0) == 0 ||
            reader.GetInt32(0) != reader.GetInt32(1))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Materialization chapter batch is not ready for vector indexing.");
        }
    }

    private static async ValueTask<IReadOnlyList<SqliteVecVectorRecord>> ReadGenerationVectorsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        VectorIndexRun run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT embedding.rowid, embedding.material_id, embedding.embedding_blob
            FROM reference_materialization_material_embeddings embedding
            JOIN reference_materialization_materials material
              ON material.material_id = embedding.material_id
            WHERE embedding.generation_id = $generation_id
              AND material.run_id = $run_id
              AND embedding.provider = $provider
              AND embedding.model_id = $model_id
              AND embedding.dimensions = $dimensions
            ORDER BY embedding.rowid;
            """;
        command.Parameters.AddWithValue("$generation_id", run.GenerationId);
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$provider", run.EmbeddingProvider);
        command.Parameters.AddWithValue("$model_id", run.EmbeddingModelId);
        command.Parameters.AddWithValue("$dimensions", run.EmbeddingDimensions);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var vectors = new List<SqliteVecVectorRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            vectors.Add(new SqliteVecVectorRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                DeserializeVector((byte[])reader[2], run.EmbeddingDimensions)));
        }

        return vectors;
    }

    private static async ValueTask<int> CountGenerationMaterialsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        VectorIndexRun run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materialization_materials
            WHERE run_id = $run_id
              AND generation_id = $generation_id;
            """;
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$generation_id", run.GenerationId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async ValueTask UpsertVectorIndexMetadataAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VectorIndexRun run,
        ReferenceMaterializationVectorIndexWorkItem workItem,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_vector_indexes (
              generation_id, run_id, table_name, provider, model_id, dimensions,
              vector_count, status, created_at, updated_at)
            VALUES (
              $generation_id, $run_id, $table_name, $provider, $model_id, $dimensions,
              $vector_count, 'ready', $created_at, $updated_at)
            ON CONFLICT(generation_id) DO UPDATE SET
              table_name = excluded.table_name,
              provider = excluded.provider,
              model_id = excluded.model_id,
              dimensions = excluded.dimensions,
              vector_count = excluded.vector_count,
              status = excluded.status,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$generation_id", run.GenerationId);
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$table_name", workItem.TableName);
        command.Parameters.AddWithValue("$provider", run.EmbeddingProvider);
        command.Parameters.AddWithValue("$model_id", run.EmbeddingModelId);
        command.Parameters.AddWithValue("$dimensions", run.EmbeddingDimensions);
        command.Parameters.AddWithValue("$vector_count", workItem.Vectors.Count);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<int> CountCompletedBatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM (
              SELECT batch_index
              FROM reference_materialization_chapter_progress
              WHERE run_id = $run_id
              GROUP BY batch_index
              HAVING SUM(CASE WHEN status = $completed THEN 0 ELSE 1 END) = 0
            );
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async ValueTask<int> CountCompletedChaptersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND status = $completed;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async ValueTask<int?> FindFirstIncompleteBatchIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MIN(batch_index)
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND status <> $completed;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async ValueTask AdvanceRunBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VectorIndexRun run,
        int? nextBatchIndex,
        int completedBatchCount,
        int processedChapterCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_runs
            SET processed_chapters = $processed_chapters,
                completed_chapter_batches = $completed_chapter_batches,
                current_batch_index = $current_batch_index,
                current_batch_start_chapter = $current_batch_start_chapter,
                current_batch_end_chapter = $current_batch_end_chapter
            WHERE run_id = $run_id
              AND status = $running
              AND current_batch_index = $expected_batch_index;
            """;
        command.Parameters.AddWithValue("$processed_chapters", processedChapterCount);
        command.Parameters.AddWithValue("$completed_chapter_batches", completedBatchCount);
        command.Parameters.AddWithValue("$current_batch_index", nextBatchIndex is null ? DBNull.Value : nextBatchIndex.Value);
        command.Parameters.AddWithValue("$current_batch_start_chapter", nextBatchIndex is null
            ? DBNull.Value
            : nextBatchIndex.Value * run.ChapterBatchSize + 1);
        command.Parameters.AddWithValue("$current_batch_end_chapter", nextBatchIndex is null
            ? DBNull.Value
            : Math.Min((nextBatchIndex.Value + 1) * run.ChapterBatchSize, run.TotalChapters));
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$running", ReferenceMaterializationRunStates.Running);
        command.Parameters.AddWithValue("$expected_batch_index", run.CurrentBatchIndex!.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization run changed while completing vector indexing.");
        }
    }

    private static async ValueTask<int> CountBatchChaptersAsync(
        SqliteConnection connection,
        string runId,
        int batchIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND batch_index = $batch_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$batch_index", batchIndex);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void ValidateIndexWorkItem(
        VectorIndexRun run,
        ReferenceMaterializationVectorIndexWorkItem workItem)
    {
        if (run.Status != ReferenceMaterializationRunStates.Running ||
            run.CurrentBatchIndex is null ||
            !string.Equals(run.RunId, workItem.RunId, StringComparison.Ordinal) ||
            !string.Equals(run.GenerationId, workItem.GenerationId, StringComparison.Ordinal) ||
            run.CurrentBatchIndex.Value != workItem.BatchIndex ||
            !string.Equals(run.EmbeddingProvider, workItem.Provider, StringComparison.Ordinal) ||
            !string.Equals(run.EmbeddingModelId, workItem.ModelId, StringComparison.Ordinal) ||
            run.EmbeddingDimensions != workItem.Dimensions ||
            !string.Equals(
                SqliteVecTableProvisioner.BuildReferenceMaterializationVectorTableName(
                    run.GenerationId,
                    run.EmbeddingDimensions),
                workItem.TableName,
                StringComparison.Ordinal) ||
            workItem.Vectors.Count == 0 ||
            workItem.Vectors.Any(vector =>
                vector.Vector.Count != run.EmbeddingDimensions ||
                vector.Vector.Any(value => float.IsNaN(value) || float.IsInfinity(value))))
        {
            throw new InvalidOperationException("Materialization vector index work item is invalid.");
        }
    }

    private sealed record VectorIndexRun(
        string RunId,
        string GenerationId,
        string Status,
        string EmbeddingProvider,
        string EmbeddingModelId,
        int EmbeddingDimensions,
        int ChapterBatchSize,
        int TotalChapters,
        int? CurrentBatchIndex);
}

internal sealed record ReferenceMaterializationVectorIndexWorkItem(
    string RunId,
    string GenerationId,
    int BatchIndex,
    string Provider,
    string ModelId,
    int Dimensions,
    string TableName,
    IReadOnlyList<SqliteVecVectorRecord> Vectors);

public sealed record ReferenceMaterializationVectorIndexResult(
    int BatchIndex,
    int CompletedChapterCount,
    int VectorCount,
    int? NextBatchIndex);
