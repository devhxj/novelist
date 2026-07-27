using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<ReferenceMaterializationStatusPayload> ResumeAllAsync(
        string runId,
        long anchorId,
        string splitProfileId,
        ReferenceMaterializationModelPreflightResult models,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(models);
        var normalizedRunId = NormalizeRunId(runId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadResumableRunAsync(connection, transaction, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(runId));
        if (run.AnchorId != anchorId || !string.Equals(run.SplitProfileId, splitProfileId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Materialization run does not belong to the requested source and split profile.", nameof(runId));
        }

        EnsureFrozenModelsMatch(run, models);
        await EnsureCompletedChaptersAreCommittedAsync(
            connection,
            transaction,
            normalizedRunId,
            cancellationToken);
        if (run.Status == ReferenceMaterializationRunStates.Completed)
        {
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(normalizedRunId, cancellationToken)
                ?? throw new InvalidOperationException("Completed materialization run disappeared.");
        }

        if (run.Status is not (ReferenceMaterializationRunStates.Failed or ReferenceMaterializationRunStates.Paused))
        {
            throw new InvalidOperationException("Materialization run is already active.");
        }

        if (await HasActiveRunAsync(connection, transaction, anchorId, cancellationToken))
        {
            throw new InvalidOperationException("Reference source already has an active materialization run.");
        }

        var chapterIndex = await FindFirstIncompleteChapterIndexAsync(
            connection,
            transaction,
            normalizedRunId,
            cancellationToken)
            ?? throw new InvalidOperationException("Failed materialization run has no incomplete chapter.");
        ReferenceMaterializationRunStateMachine.EnsureCanTransition(run.Status, ReferenceMaterializationRunStates.Queued);
        var chapter = await ReadChapterCursorAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken)
            ?? throw new InvalidOperationException("Incomplete materialization chapter does not exist.");
        if (chapter.Status == ReferenceMaterializationChapterStates.Failed)
        {
            ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
                ReferenceMaterializationChapterStates.Failed,
                ReferenceMaterializationChapterStates.Pending);
            await ResetCurrentChapterAsync(
                connection,
                transaction,
                normalizedRunId,
                chapterIndex,
                cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $queued,
                    current_chapter_index = $chapter_index,
                    requested_chapter_index = NULL,
                    last_error_code = NULL,
                    last_error_message = NULL,
                    completed_at = NULL
                WHERE run_id = $run_id
                  AND status IN ($failed, $paused);
                """;
            command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
            command.Parameters.AddWithValue("$chapter_index", chapterIndex);
            command.Parameters.AddWithValue("$run_id", normalizedRunId);
            command.Parameters.AddWithValue("$failed", ReferenceMaterializationRunStates.Failed);
            command.Parameters.AddWithValue("$paused", ReferenceMaterializationRunStates.Paused);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Materialization run changed before it could resume.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(normalizedRunId, cancellationToken)
            ?? throw new InvalidOperationException("Resumed materialization run disappeared.");
    }

    public async ValueTask<ReferenceMaterializationStatusPayload> RunChapterAsync(
        string runId,
        long anchorId,
        int chapterIndex,
        ReferenceMaterializationModelPreflightResult models,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (chapterIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex));
        }

        var normalizedRunId = NormalizeRunId(runId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadResumableRunAsync(connection, transaction, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(runId));
        if (run.AnchorId != anchorId)
        {
            throw new ArgumentException("Materialization run does not belong to the requested source.", nameof(runId));
        }

        EnsureFrozenModelsMatch(run, models);
        if (run.Status is not (ReferenceMaterializationRunStates.Failed or
            ReferenceMaterializationRunStates.Paused or
            ReferenceMaterializationRunStates.Completed))
        {
            throw new InvalidOperationException("Materialization run is already active.");
        }

        if (await HasActiveRunAsync(connection, transaction, anchorId, cancellationToken))
        {
            throw new InvalidOperationException("Reference source already has an active materialization run.");
        }

        var chapter = await ReadChapterCursorAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken)
            ?? throw new ArgumentException("Materialization chapter does not exist.", nameof(chapterIndex));
        ReferenceMaterializationRunStateMachine.EnsureCanTransition(run.Status, ReferenceMaterializationRunStates.Queued);
        if (chapter.Status is ReferenceMaterializationChapterStates.Completed or ReferenceMaterializationChapterStates.Failed)
        {
            ReferenceMaterializationChapterStateMachine.EnsureCanTransition(
                chapter.Status,
                ReferenceMaterializationChapterStates.Pending);
        }

        await DeactivateGenerationAsync(connection, transaction, run, cancellationToken);
        await ResetCurrentChapterAsync(connection, transaction, normalizedRunId, chapterIndex, cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $queued,
                    current_chapter_index = $chapter_index,
                    requested_chapter_index = $chapter_index,
                    last_error_code = NULL,
                    last_error_message = NULL,
                    completed_at = NULL
                WHERE run_id = $run_id
                  AND status IN ($failed, $paused, $completed);
                """;
            command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
            command.Parameters.AddWithValue("$chapter_index", chapterIndex);
            command.Parameters.AddWithValue("$run_id", normalizedRunId);
            command.Parameters.AddWithValue("$failed", ReferenceMaterializationRunStates.Failed);
            command.Parameters.AddWithValue("$paused", ReferenceMaterializationRunStates.Paused);
            command.Parameters.AddWithValue("$completed", ReferenceMaterializationRunStates.Completed);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Materialization run changed before the chapter could start.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetAsync(normalizedRunId, cancellationToken)
            ?? throw new InvalidOperationException("Materialization run disappeared after scheduling its chapter.");
    }

    public async ValueTask<string?> ReadNextRunnableRunIdAsync(CancellationToken cancellationToken)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id
            FROM reference_materialization_runs
            WHERE (status = $queued AND current_chapter_index IS NOT NULL)
               OR (status IN ($extracting, $embedding, $indexing)
                   AND (current_chapter_index IS NOT NULL OR processed_chapters = total_chapters))
            ORDER BY started_at, run_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
        command.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
        command.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
        command.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async ValueTask<ReferenceMaterializationChapterClaim?> ClaimCurrentChapterAsync(
        string runId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be between zero and thirty minutes.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadRunCursorAsync(connection, transaction, normalizedRunId, cancellationToken);
        if (run is null || run.CurrentChapterIndex is null ||
            run.Status is not (ReferenceMaterializationRunStates.Queued or
                ReferenceMaterializationRunStates.Extracting or
                ReferenceMaterializationRunStates.Embedding or
                ReferenceMaterializationRunStates.Indexing))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid().ToString("N");
        var leaseAcquired = await TryAcquireLeaseAsync(
            connection,
            transaction,
            normalizedRunId,
            token,
            now,
            leaseDuration,
            cancellationToken);
        if (!leaseAcquired)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (run.Status == ReferenceMaterializationRunStates.Queued)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Queued,
                ReferenceMaterializationRunStates.Extracting);
            await StartRunAsync(connection, transaction, normalizedRunId, cancellationToken);
        }

        var chapter = await ReadChapterCursorAsync(
            connection,
            transaction,
            normalizedRunId,
            run.CurrentChapterIndex.Value,
            cancellationToken);
        if (chapter is null)
        {
            await DeleteLeaseAsync(connection, transaction, normalizedRunId, token, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var requiresProcessing = chapter.Status == ReferenceMaterializationChapterStates.Pending;
        var canResumeIndexing =
            chapter.Status == ReferenceMaterializationChapterStates.Embedding &&
            chapter.MaterialCount > 0 &&
            chapter.VectorCount == chapter.MaterialCount;
        if (!requiresProcessing && !canResumeIndexing)
        {
            await ResetCurrentChapterAsync(
                connection,
                transaction,
                normalizedRunId,
                run.CurrentChapterIndex.Value,
                cancellationToken);
            requiresProcessing = true;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReferenceMaterializationChapterClaim(
            normalizedRunId,
            run.CurrentChapterIndex.Value,
            token,
            requiresProcessing);
    }

    public async ValueTask ReleaseChapterLeaseAsync(
        ReferenceMaterializationChapterClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await DeleteLeaseAsync(connection, transaction, NormalizeRunId(claim.RunId), claim.LeaseToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<bool> RenewChapterLeaseAsync(
        ReferenceMaterializationChapterClaim claim,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be between zero and thirty minutes.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_materialization_run_leases
            SET lease_expires_at = $lease_expires_at
            WHERE run_id = $run_id
              AND lease_token = $lease_token
              AND lease_expires_at > $now;
            """;
        command.Parameters.AddWithValue("$lease_expires_at", FormatTimestamp(now.Add(leaseDuration)));
        command.Parameters.AddWithValue("$run_id", NormalizeRunId(claim.RunId));
        command.Parameters.AddWithValue("$lease_token", claim.LeaseToken);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask MarkCurrentChapterEmbeddingAsync(
        ReferenceMaterializationChapterClaim claim,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadRunCursorAsync(connection, transaction, NormalizeRunId(claim.RunId), cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(claim));
        if (!await IsClaimLeaseOwnedAsync(connection, transaction, claim, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Materialization worker lost the current chapter lease.");
        }

        if (run.Status == ReferenceMaterializationRunStates.Extracting)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(
                ReferenceMaterializationRunStates.Extracting,
                ReferenceMaterializationRunStates.Embedding);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $embedding
                WHERE run_id = $run_id
                  AND status = $extracting
                  AND current_chapter_index = $chapter_index;
                """;
            update.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
            update.Parameters.AddWithValue("$run_id", run.RunId);
            update.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
            update.Parameters.AddWithValue("$chapter_index", claim.ChapterIndex);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Materialization run changed before vector indexing.");
            }
        }
        else if (run.Status is not (ReferenceMaterializationRunStates.Embedding or ReferenceMaterializationRunStates.Indexing))
        {
            throw new InvalidOperationException("Materialization run is not ready for vector indexing.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask FailCurrentChapterAsync(
        ReferenceMaterializationChapterClaim claim,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var normalizedRunId = NormalizeRunId(claim.RunId);
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 128 ||
            string.IsNullOrWhiteSpace(errorMessage) || errorMessage.Length > 1_200)
        {
            throw new ArgumentException("Materialization failure details are invalid.", nameof(errorCode));
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var run = await ReadRunCursorAsync(connection, transaction, normalizedRunId, cancellationToken)
            ?? throw new ArgumentException("Materialization run does not exist.", nameof(claim));
        if (!await IsClaimLeaseOwnedAsync(connection, transaction, claim, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        if (run.Status is ReferenceMaterializationRunStates.Queued or
            ReferenceMaterializationRunStates.Extracting or
            ReferenceMaterializationRunStates.Embedding or
            ReferenceMaterializationRunStates.Indexing)
        {
            ReferenceMaterializationRunStateMachine.EnsureCanTransition(run.Status, ReferenceMaterializationRunStates.Failed);
        }

        await DeleteChapterResultAsync(
            connection,
            transaction,
            normalizedRunId,
            claim.ChapterIndex,
            cancellationToken);

        await using (var chapter = connection.CreateCommand())
        {
            chapter.Transaction = transaction;
            chapter.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET status = $failed,
                    material_count = 0,
                    vector_count = 0,
                    last_error_code = $error_code,
                    last_error_message = $error_message
                WHERE run_id = $run_id
                  AND chapter_index = $chapter_index
                  AND status <> $completed;
                """;
            chapter.Parameters.AddWithValue("$failed", ReferenceMaterializationChapterStates.Failed);
            chapter.Parameters.AddWithValue("$error_code", errorCode);
            chapter.Parameters.AddWithValue("$error_message", errorMessage);
            chapter.Parameters.AddWithValue("$run_id", normalizedRunId);
            chapter.Parameters.AddWithValue("$chapter_index", claim.ChapterIndex);
            chapter.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            await chapter.ExecuteNonQueryAsync(cancellationToken);
        }

        await RefreshRunCountsAsync(connection, transaction, normalizedRunId, cancellationToken);
        await MarkVectorIndexBuildingAsync(connection, transaction, normalizedRunId, cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $failed,
                    last_error_code = $error_code,
                    last_error_message = $error_message,
                    completed_at = $completed_at
                WHERE run_id = $run_id
                  AND status IN ($queued, $extracting, $embedding, $indexing);
                """;
            command.Parameters.AddWithValue("$failed", ReferenceMaterializationRunStates.Failed);
            command.Parameters.AddWithValue("$error_code", errorCode);
            command.Parameters.AddWithValue("$error_message", errorMessage);
            command.Parameters.AddWithValue("$completed_at", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$run_id", normalizedRunId);
            command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
            command.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
            command.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
            command.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await DeleteLeaseAsync(connection, transaction, normalizedRunId, claim.LeaseToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask<RunCursor?> ReadRunCursorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, generation_id, status, current_chapter_index
            FROM reference_materialization_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RunCursor(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3))
            : null;
    }

    private static async ValueTask<ResumableRun?> ReadResumableRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT anchor_id, split_profile_id, generation_id, status,
                   model_provider, model_id,
                   embedding_provider, embedding_model_id, embedding_dimensions
            FROM reference_materialization_runs
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ResumableRun(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8))
            : null;
    }

    private static async ValueTask DeactivateGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ResumableRun run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_anchor_materialization_state
            SET active_generation_id = NULL
            WHERE anchor_id = $anchor_id
              AND active_generation_id = $generation_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", run.AnchorId);
        command.Parameters.AddWithValue("$generation_id", run.GenerationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void EnsureFrozenModelsMatch(
        ResumableRun run,
        ReferenceMaterializationModelPreflightResult models)
    {
        if (!string.Equals(run.ModelProvider, models.Llm.Provider, StringComparison.Ordinal) ||
            !string.Equals(run.ModelId, models.Llm.ModelId, StringComparison.Ordinal))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.LlmHealthCheckFailed,
                "The active language model no longer matches the materialization run.");
        }

        if (!string.Equals(run.EmbeddingProvider, models.Embedding.Provider, StringComparison.Ordinal) ||
            !string.Equals(run.EmbeddingModelId, models.Embedding.ModelId, StringComparison.Ordinal) ||
            run.EmbeddingDimensions != models.Embedding.Dimensions)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                "The active embedding model no longer matches the materialization run.");
        }
    }

    private static async ValueTask EnsureCompletedChaptersAreCommittedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT progress.chapter_index
            FROM reference_materialization_chapter_progress progress
            WHERE progress.run_id = $run_id
              AND progress.status = $completed
              AND (
                progress.material_count <= 0 OR
                progress.vector_count <> progress.material_count OR
                progress.material_count <> (
                  SELECT COUNT(*)
                  FROM reference_materials material
                  WHERE material.run_id = progress.run_id
                    AND material.chapter_index = progress.chapter_index) OR
                progress.vector_count <> (
                  SELECT COUNT(*)
                  FROM reference_materials material
                  JOIN reference_material_embeddings embedding
                    ON embedding.material_id = material.material_id
                  WHERE material.run_id = progress.run_id
                    AND material.chapter_index = progress.chapter_index))
            ORDER BY progress.chapter_index
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        var invalidChapter = await command.ExecuteScalarAsync(cancellationToken);
        if (invalidChapter is not null)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                $"Chapter {Convert.ToInt32(invalidChapter)} is marked completed but its committed material data is missing. Run that chapter again before continuing.");
        }
    }

    private static async ValueTask<bool> TryAcquireLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT lease_expires_at
                FROM reference_materialization_run_leases
                WHERE run_id = $run_id;
                """;
            existing.Parameters.AddWithValue("$run_id", runId);
            var expiresAt = (string?)await existing.ExecuteScalarAsync(cancellationToken);
            if (expiresAt is not null && DateTimeOffset.Parse(expiresAt) > now)
            {
                return false;
            }
        }

        return await ReplaceLeaseAsync(
            connection,
            transaction,
            runId,
            leaseToken,
            now,
            leaseDuration,
            cancellationToken);
    }

    private static async ValueTask<bool> ReplaceLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string leaseToken,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM reference_materialization_run_leases WHERE run_id = $run_id;";
            delete.Parameters.AddWithValue("$run_id", runId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO reference_materialization_run_leases (
              run_id, lease_token, lease_expires_at)
            VALUES ($run_id, $lease_token, $lease_expires_at);
            """;
        insert.Parameters.AddWithValue("$run_id", runId);
        insert.Parameters.AddWithValue("$lease_token", leaseToken);
        insert.Parameters.AddWithValue("$lease_expires_at", FormatTimestamp(now.Add(leaseDuration)));
        return await insert.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async ValueTask ResetCurrentChapterAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await DeleteChapterResultAsync(connection, transaction, runId, chapterIndex, cancellationToken);

        await using (var chapter = connection.CreateCommand())
        {
            chapter.Transaction = transaction;
            chapter.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET status = $pending,
                    material_count = 0,
                    vector_count = 0,
                    started_at = NULL,
                    completed_at = NULL,
                    last_error_code = NULL,
                    last_error_message = NULL
                WHERE run_id = $run_id
                  AND chapter_index = $chapter_index;
                """;
            chapter.Parameters.AddWithValue("$pending", ReferenceMaterializationChapterStates.Pending);
            chapter.Parameters.AddWithValue("$run_id", runId);
            chapter.Parameters.AddWithValue("$chapter_index", chapterIndex);
            if (await chapter.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Materialization chapter does not exist.");
            }
        }

        await RefreshRunCountsAsync(connection, transaction, runId, cancellationToken);
        await using (var run = connection.CreateCommand())
        {
            run.Transaction = transaction;
            run.CommandText = """
                UPDATE reference_materialization_runs
                SET status = $extracting
                WHERE run_id = $run_id
                  AND status IN ($extracting, $embedding, $indexing);
                """;
            run.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
            run.Parameters.AddWithValue("$embedding", ReferenceMaterializationRunStates.Embedding);
            run.Parameters.AddWithValue("$indexing", ReferenceMaterializationRunStates.Indexing);
            run.Parameters.AddWithValue("$run_id", runId);
            await run.ExecuteNonQueryAsync(cancellationToken);
        }

        await MarkVectorIndexBuildingAsync(connection, transaction, runId, cancellationToken);
    }

    private static async ValueTask MarkVectorIndexBuildingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_vector_indexes
            SET status = 'building'
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask DeleteChapterResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using (var embeddings = connection.CreateCommand())
        {
            embeddings.Transaction = transaction;
            embeddings.CommandText = """
                DELETE FROM reference_material_embeddings
                WHERE material_id IN (
                  SELECT material_id
                  FROM reference_materials
                  WHERE run_id = $run_id
                    AND chapter_index = $chapter_index);
                """;
            embeddings.Parameters.AddWithValue("$run_id", runId);
            embeddings.Parameters.AddWithValue("$chapter_index", chapterIndex);
            await embeddings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var materials = connection.CreateCommand();
        materials.Transaction = transaction;
        materials.CommandText = """
            DELETE FROM reference_materials
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index;
            """;
        materials.Parameters.AddWithValue("$run_id", runId);
        materials.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await materials.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask RefreshRunCountsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_runs
            SET material_count = (
                    SELECT COALESCE(SUM(material_count), 0)
                    FROM reference_materialization_chapter_progress
                    WHERE run_id = $run_id),
                vector_count = (
                    SELECT COALESCE(SUM(vector_count), 0)
                    FROM reference_materialization_chapter_progress
                    WHERE run_id = $run_id),
                processed_chapters = (
                    SELECT COUNT(*)
                    FROM reference_materialization_chapter_progress
                    WHERE run_id = $run_id
                      AND status = $completed)
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask StartRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materialization_runs
            SET status = $extracting
            WHERE run_id = $run_id
              AND status = $queued;
            """;
        command.Parameters.AddWithValue("$extracting", ReferenceMaterializationRunStates.Extracting);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$queued", ReferenceMaterializationRunStates.Queued);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization run changed while starting its current chapter.");
        }
    }

    private static async ValueTask<ChapterCursor?> ReadChapterCursorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, material_count, vector_count
            FROM reference_materialization_chapter_progress
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ChapterCursor(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2))
            : null;
    }

    private static async ValueTask DeleteLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string leaseToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM reference_materialization_run_leases
            WHERE run_id = $run_id
              AND lease_token = $lease_token;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$lease_token", leaseToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<bool> IsClaimLeaseOwnedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceMaterializationChapterClaim claim,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM reference_materialization_run_leases
            WHERE run_id = $run_id
              AND lease_token = $lease_token
              AND lease_expires_at > $now;
            """;
        command.Parameters.AddWithValue("$run_id", NormalizeRunId(claim.RunId));
        command.Parameters.AddWithValue("$lease_token", claim.LeaseToken);
        command.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private sealed record RunCursor(string RunId, string GenerationId, string Status, int? CurrentChapterIndex);
    private sealed record ChapterCursor(string Status, int MaterialCount, int VectorCount);
    private sealed record ResumableRun(
        long AnchorId,
        string SplitProfileId,
        string GenerationId,
        string Status,
        string ModelProvider,
        string ModelId,
        string EmbeddingProvider,
        string EmbeddingModelId,
        int EmbeddingDimensions);
}

internal sealed record ReferenceMaterializationChapterClaim(
    string RunId,
    int ChapterIndex,
    string LeaseToken,
    bool RequiresProcessing);
