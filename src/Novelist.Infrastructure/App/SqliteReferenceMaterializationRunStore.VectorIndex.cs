using System.Text.Json;
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
        var run = await ReadVectorIndexRunAsync(connection, transaction: null, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(runId));
        var currentBatchIndex = run.CurrentBatchIndex
            ?? throw new InvalidOperationException("Materialization run has no active chapter batch to index.");

        await EnsureBatchReadyForIndexAsync(connection, transaction: null, run, cancellationToken);
        var vectors = await ReadGenerationVectorsAsync(connection, transaction: null, run, cancellationToken);
        var acceptedCount = await CountAcceptedCandidatesAsync(connection, transaction: null, run.RunId, cancellationToken);
        if (vectors.Count != acceptedCount)
        {
            throw new InvalidOperationException("Materialization generation does not have a complete embedding set.");
        }

        return new ReferenceMaterializationVectorIndexWorkItem(
            run.RunId,
            run.GenerationId,
            currentBatchIndex,
            run.EmbeddingProvider,
            run.EmbeddingModelId,
            run.EmbeddingDimensions,
            SqliteVecTableProvisioner.BuildReferenceMaterializationVectorTableName(run.GenerationId, run.EmbeddingDimensions),
            vectors);
    }

    public async ValueTask<ReferenceMaterializationVectorIndexResult> CompleteCurrentBatchIndexAsync(
        ReferenceMaterializationVectorIndexWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        var normalizedRunId = NormalizeRunId(workItem.RunId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadVectorIndexRunAsync(connection, transaction, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(workItem));
        ValidateIndexWorkItem(run, workItem);
        var currentBatchIndex = run.CurrentBatchIndex
            ?? throw new InvalidOperationException("Materialization run has no active chapter batch to index.");
        await EnsureBatchReadyForIndexAsync(connection, transaction, run, cancellationToken);
        var vectors = await ReadGenerationVectorsAsync(connection, transaction, run, cancellationToken);
        var acceptedCount = await CountAcceptedCandidatesAsync(connection, transaction, run.RunId, cancellationToken);
        if (vectors.Count != acceptedCount || vectors.Count != workItem.Vectors.Count)
        {
            throw new InvalidOperationException("Materialization generation vectors changed before index completion.");
        }

        var now = DateTimeOffset.UtcNow;
        await UpsertVectorIndexMetadataAsync(connection, transaction, run, workItem, now, cancellationToken);
        var completedChapters = await CompleteBatchChaptersAsync(connection, transaction, run, now, cancellationToken);
        var completedBatchCount = await CountCompletedBatchesAsync(connection, transaction, run.RunId, cancellationToken);
        var processedChapterCount = await CountCompletedChaptersAsync(connection, transaction, run.RunId, cancellationToken);
        var nextBatchIndex = currentBatchIndex + 1 < run.TotalChapterBatches
            ? currentBatchIndex + 1
            : (int?)null;
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
            currentBatchIndex,
            completedChapters,
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
            SELECT run_id, generation_id, embedding_provider, embedding_model_id, embedding_dimensions,
                   chapter_batch_size, total_chapters, total_chapter_batches, current_batch_index
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
                reader.GetInt32(4),
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
            throw new InvalidOperationException("Materialization run has no active chapter batch to index.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN status = $indexing AND current_stage = $indexing
                                      AND vector_count = accepted_count THEN 1 ELSE 0 END), 0)
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND batch_index = $batch_index;
            """;
        command.Parameters.AddWithValue("$indexing", ReferenceMaterializationChapterStates.Indexing);
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$batch_index", run.CurrentBatchIndex.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Materialization chapter batch does not exist.");
        }

        var total = reader.GetInt32(0);
        var ready = reader.GetInt32(1);
        if (total == 0 || total != ready)
        {
            throw new InvalidOperationException("Materialization chapter batch is not ready for vector index creation.");
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
            SELECT embedding.rowid, embedding.candidate_id, embedding.embedding_json
            FROM reference_materialization_candidate_embeddings embedding
            JOIN reference_material_candidates candidate ON candidate.candidate_id = embedding.candidate_id
            WHERE embedding.run_id = $run_id
              AND embedding.generation_id = $generation_id
              AND embedding.provider = $provider
              AND embedding.model_id = $model_id
              AND embedding.dimensions = $dimensions
              AND candidate.decision = $accepted
            ORDER BY embedding.rowid;
            """;
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$generation_id", run.GenerationId);
        command.Parameters.AddWithValue("$provider", run.EmbeddingProvider);
        command.Parameters.AddWithValue("$model_id", run.EmbeddingModelId);
        command.Parameters.AddWithValue("$dimensions", run.EmbeddingDimensions);
        command.Parameters.AddWithValue("$accepted", ReferenceMaterializationCandidateDecisions.Accepted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var vectors = new List<SqliteVecVectorRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var vector = JsonSerializer.Deserialize<float[]>(reader.GetString(2));
            if (vector is null || vector.Length != run.EmbeddingDimensions ||
                vector.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            {
                throw new InvalidOperationException("Stored materialization embedding is invalid.");
            }

            vectors.Add(new SqliteVecVectorRecord(reader.GetInt64(0), reader.GetString(1), vector));
        }

        return vectors;
    }

    private static async ValueTask<int> CountAcceptedCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_material_candidates
            WHERE run_id = $run_id
              AND decision = $accepted;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$accepted", ReferenceMaterializationCandidateDecisions.Accepted);
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
              generation_id, run_id, table_name, provider, model_id, dimensions, vector_count, status, created_at, updated_at)
            VALUES (
              $generation_id, $run_id, $table_name, $provider, $model_id, $dimensions, $vector_count, 'ready', $created_at, $updated_at)
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

    private static async ValueTask<int> CompleteBatchChaptersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VectorIndexRun run,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
            ReferenceMaterializationChapterStates.Indexing,
            ReferenceMaterializationChapterStates.Completed);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET status = $completed,
                current_stage = $completed,
                completed_at = $completed_at,
                row_version = row_version + 1
            WHERE run_id = $run_id
              AND batch_index = $batch_index
              AND status = $indexing
              AND current_stage = $indexing
              AND vector_count = accepted_count;
            """;
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        command.Parameters.AddWithValue("$completed_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$batch_index", run.CurrentBatchIndex!.Value);
        command.Parameters.AddWithValue("$indexing", ReferenceMaterializationChapterStates.Indexing);
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
              AND current_batch_index = $expected_batch_index;
            """;
        command.Parameters.AddWithValue("$processed_chapters", processedChapterCount);
        command.Parameters.AddWithValue("$completed_chapter_batches", completedBatchCount);
        command.Parameters.AddWithValue("$current_batch_index", nextBatchIndex is null ? DBNull.Value : nextBatchIndex.Value);
        command.Parameters.AddWithValue("$current_batch_start_chapter", nextBatchIndex is null ? DBNull.Value : nextBatchIndex.Value * run.ChapterBatchSize + 1);
        command.Parameters.AddWithValue("$current_batch_end_chapter", nextBatchIndex is null
            ? DBNull.Value
            : Math.Min((nextBatchIndex.Value + 1) * run.ChapterBatchSize, run.TotalChapters));
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$expected_batch_index", run.CurrentBatchIndex!.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization run changed while completing the vector index.");
        }
    }

    private static void ValidateIndexWorkItem(
        VectorIndexRun run,
        ReferenceMaterializationVectorIndexWorkItem workItem)
    {
        if (run.CurrentBatchIndex is null ||
            !string.Equals(run.RunId, workItem.RunId, StringComparison.Ordinal) ||
            !string.Equals(run.GenerationId, workItem.GenerationId, StringComparison.Ordinal) ||
            run.CurrentBatchIndex.Value != workItem.BatchIndex ||
            !string.Equals(run.EmbeddingProvider, workItem.Provider, StringComparison.Ordinal) ||
            !string.Equals(run.EmbeddingModelId, workItem.ModelId, StringComparison.Ordinal) ||
            run.EmbeddingDimensions != workItem.Dimensions ||
            !string.Equals(
                SqliteVecTableProvisioner.BuildReferenceMaterializationVectorTableName(run.GenerationId, run.EmbeddingDimensions),
                workItem.TableName,
                StringComparison.Ordinal) ||
            workItem.Vectors.Any(vector => vector.Vector.Count != run.EmbeddingDimensions ||
                vector.Vector.Any(value => float.IsNaN(value) || float.IsInfinity(value))))
        {
            throw new InvalidOperationException("Materialization vector index work item is invalid.");
        }
    }

    private sealed record VectorIndexRun(
        string RunId,
        string GenerationId,
        string EmbeddingProvider,
        string EmbeddingModelId,
        int EmbeddingDimensions,
        int ChapterBatchSize,
        int TotalChapters,
        int TotalChapterBatches,
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
