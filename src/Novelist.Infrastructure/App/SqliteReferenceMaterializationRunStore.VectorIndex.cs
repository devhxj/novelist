using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<ReferenceMaterializationVectorIndexWorkItem> ReadCurrentChapterVectorIndexWorkItemAsync(
        ReferenceMaterializationChapterClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var normalizedRunId = NormalizeRunId(claim.RunId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadVectorIndexRunAsync(connection, transaction, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(claim));
        if (!await IsClaimLeaseOwnedAsync(connection, transaction, claim, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Materialization worker lost the current chapter lease.");
        }

        if (run.Status is not (ReferenceMaterializationRunStates.Embedding or ReferenceMaterializationRunStates.Indexing) ||
            run.CurrentChapterIndex != claim.ChapterIndex)
        {
            throw new InvalidOperationException("Materialization run has no active chapter to index.");
        }

        await EnsureChapterReadyForIndexAsync(connection, transaction, run, cancellationToken);
        var vectors = await ReadGenerationVectorsAsync(connection, transaction, run, cancellationToken);
        var materialCount = await CountGenerationMaterialsAsync(connection, transaction, run, cancellationToken);
        if (materialCount == 0 || vectors.Count != materialCount)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Materialization generation does not have a complete non-empty vector set.");
        }

        if (run.Status == ReferenceMaterializationRunStates.Embedding)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Embedding,
                ReferenceMaterializationRunStates.Indexing);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $indexing
                WHERE run_id = $run_id
                  AND status = $embedding
                  AND current_chapter_index = $chapter_index;
                """;
            update.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
            update.Parameters.AddWithValue("$run_id", run.RunId);
            update.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
            update.Parameters.AddWithValue("$chapter_index", run.CurrentChapterIndex.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Materialization run changed before vector indexing.");
            }

            run = run with { Status = ReferenceMaterializationRunStates.Indexing };
        }

        await transaction.CommitAsync(cancellationToken);

        return new ReferenceMaterializationVectorIndexWorkItem(
            run.RunId,
            run.GenerationId,
            run.CurrentChapterIndex.Value,
            run.EmbeddingProvider,
            run.EmbeddingModelId,
            run.EmbeddingDimensions,
            SqliteVecTableProvisioner.BuildReferenceMaterializationVectorTableName(
                run.GenerationId,
                run.EmbeddingDimensions),
            vectors);
    }

    public async ValueTask<ReferenceMaterializationVectorIndexResult> CompleteCurrentChapterIndexAsync(
        ReferenceMaterializationChapterClaim claim,
        ReferenceMaterializationVectorIndexWorkItem workItem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(workItem);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadVectorIndexRunAsync(connection, transaction, NormalizeRunId(workItem.RunId), cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(workItem));
        if (!await IsClaimLeaseOwnedAsync(connection, transaction, claim, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Materialization worker lost the current chapter lease.");
        }

        ValidateIndexWorkItem(run, workItem);
        await EnsureChapterReadyForIndexAsync(connection, transaction, run, cancellationToken);
        var vectors = await ReadGenerationVectorsAsync(connection, transaction, run, cancellationToken);
        var materialCount = await CountGenerationMaterialsAsync(connection, transaction, run, cancellationToken);
        if (materialCount == 0 || vectors.Count != materialCount || vectors.Count != workItem.Vectors.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Materialization generation changed before vector index completion.");
        }

        var now = DateTimeOffset.UtcNow;
        await UpsertVectorIndexMetadataAsync(connection, transaction, run, workItem, cancellationToken);
        await CompleteChapterAsync(connection, transaction, run, now, cancellationToken);
        var processedChapterCount = await CountCompletedChaptersAsync(connection, transaction, run.RunId, cancellationToken);
        var firstIncompleteChapterIndex = await FindFirstIncompleteChapterIndexAsync(
            connection,
            transaction,
            run.RunId,
            cancellationToken);
        var pauseAfterChapter = run.RequestedChapterIndex is not null && firstIncompleteChapterIndex is not null;
        var nextChapterIndex = pauseAfterChapter ? null : firstIncompleteChapterIndex;
        await AdvanceRunChapterAsync(
            connection,
            transaction,
            run,
            nextChapterIndex,
            processedChapterCount,
            pauseAfterChapter,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReferenceMaterializationVectorIndexResult(
            workItem.ChapterIndex,
            vectors.Count,
            nextChapterIndex);
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
                   current_chapter_index, requested_chapter_index
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
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7))
            : null;
    }

    private static async ValueTask EnsureChapterReadyForIndexAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        VectorIndexRun run,
        CancellationToken cancellationToken)
    {
        if (run.CurrentChapterIndex is null)
        {
            throw new InvalidOperationException("Materialization run has no active chapter to index.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, material_count, vector_count
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$chapter_index", run.CurrentChapterIndex.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            !string.Equals(reader.GetString(0), ReferenceMaterializationChapterStates.Embedding, StringComparison.Ordinal) ||
            reader.GetInt32(1) <= 0 ||
            reader.GetInt32(1) != reader.GetInt32(2))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Materialization chapter is not ready for vector indexing.");
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
            FROM reference_material_embeddings embedding
            JOIN reference_materials material
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
            FROM reference_materials
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
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materialization_vector_indexes (
              generation_id, run_id, table_name, provider, model_id, dimensions,
              vector_count, status)
            VALUES (
              $generation_id, $run_id, $table_name, $provider, $model_id, $dimensions,
              $vector_count, 'ready')
            ON CONFLICT(generation_id) DO UPDATE SET
              table_name = excluded.table_name,
              provider = excluded.provider,
              model_id = excluded.model_id,
              dimensions = excluded.dimensions,
              vector_count = excluded.vector_count,
              status = excluded.status;
            """;
        command.Parameters.AddWithValue("$generation_id", run.GenerationId);
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$table_name", workItem.TableName);
        command.Parameters.AddWithValue("$provider", run.EmbeddingProvider);
        command.Parameters.AddWithValue("$model_id", run.EmbeddingModelId);
        command.Parameters.AddWithValue("$dimensions", run.EmbeddingDimensions);
        command.Parameters.AddWithValue("$vector_count", workItem.Vectors.Count);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static async ValueTask CompleteChapterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VectorIndexRun run,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
            ReferenceMaterializationChapterStates.Embedding,
            ReferenceMaterializationChapterStates.Completed);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET status = $completed,
                completed_at = $completed_at
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index
              AND status = $embedding;
            """;
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        command.Parameters.AddWithValue("$completed_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$chapter_index", run.CurrentChapterIndex!.Value);
        command.Parameters.AddWithValue("$embedding", ReferenceMaterializationChapterStates.Embedding);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization chapter changed while completing vector indexing.");
        }
    }

    private static async ValueTask<int?> FindFirstIncompleteChapterIndexAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MIN(chapter_index)
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND status <> $completed;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private static async ValueTask AdvanceRunChapterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        VectorIndexRun run,
        int? nextChapterIndex,
        int processedChapterCount,
        bool pauseAfterChapter,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_runs
            SET status = $next_status,
                processed_chapters = $processed_chapters,
                current_chapter_index = $current_chapter_index,
                requested_chapter_index = NULL
            WHERE run_id = $run_id
              AND status = $indexing
              AND current_chapter_index = $expected_chapter_index;
            """;
        var nextStatus = pauseAfterChapter
            ? ReferenceMaterializationRunStates.Paused
            : nextChapterIndex is null
            ? ReferenceMaterializationRunStates.Indexing
            : ReferenceMaterializationRunStates.Extracting;
        if (pauseAfterChapter)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Indexing,
                ReferenceMaterializationRunStates.Paused);
        }
        else if (nextChapterIndex is not null)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Indexing,
                ReferenceMaterializationRunStates.Extracting);
        }

        command.Parameters.AddWithValue("$next_status", nextStatus);
        command.Parameters.AddWithValue("$processed_chapters", processedChapterCount);
        command.Parameters.AddWithValue("$current_chapter_index", nextChapterIndex is null ? DBNull.Value : nextChapterIndex.Value);
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
        command.Parameters.AddWithValue("$expected_chapter_index", run.CurrentChapterIndex!.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization run changed while completing vector indexing.");
        }
    }

    private static void ValidateIndexWorkItem(
        VectorIndexRun run,
        ReferenceMaterializationVectorIndexWorkItem workItem)
    {
        if (run.Status != ReferenceMaterializationRunStates.Indexing ||
            run.CurrentChapterIndex is null ||
            (run.RequestedChapterIndex is not null && run.RequestedChapterIndex != run.CurrentChapterIndex) ||
            !string.Equals(run.RunId, workItem.RunId, StringComparison.Ordinal) ||
            !string.Equals(run.GenerationId, workItem.GenerationId, StringComparison.Ordinal) ||
            run.CurrentChapterIndex.Value != workItem.ChapterIndex ||
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
        int? CurrentChapterIndex,
        int? RequestedChapterIndex);
}

internal sealed record ReferenceMaterializationVectorIndexWorkItem(
    string RunId,
    string GenerationId,
    int ChapterIndex,
    string Provider,
    string ModelId,
    int Dimensions,
    string TableName,
    IReadOnlyList<SqliteVecVectorRecord> Vectors);

public sealed record ReferenceMaterializationVectorIndexResult(
    int ChapterIndex,
    int VectorCount,
    int? NextChapterIndex);
