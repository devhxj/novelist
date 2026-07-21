using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    private const long MaxMaterializationSourceBytes = 20L * 1024L * 1024L;

    public async ValueTask<ReferenceChapterMaterializationWorkItem> ReadChapterWorkItemAsync(
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        if (chapterIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex));
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var snapshot = await ReadChapterSnapshotAsync(
            connection,
            transaction: null,
            normalizedRunId,
            chapterIndex,
            cancellationToken)
            ?? throw new ArgumentException("Materialization chapter does not exist.", nameof(chapterIndex));
        if (snapshot.RunStatus != ReferenceMaterializationRunStates.Extracting ||
            snapshot.ChapterStatus != ReferenceMaterializationChapterStates.Pending)
        {
            throw new InvalidOperationException("Materialization chapter is not pending extraction.");
        }

        var source = await ReadFrozenSourceAsync(snapshot.SourcePath, snapshot.SourceHash, cancellationToken);
        var chapterText = ReadAndValidateChapterText(source, snapshot);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE reference_materialization_chapter_progress
            SET status = $extracting,
                current_stage = $extracting,
                model_call_count = model_call_count + 1,
                started_at = COALESCE(started_at, $started_at),
                row_version = row_version + 1
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index
              AND status = $pending;
            """;
        update.Parameters.AddWithValue("$extracting", ReferenceMaterializationChapterStates.Extracting);
        update.Parameters.AddWithValue("$started_at", FormatTimestamp(DateTimeOffset.UtcNow));
        update.Parameters.AddWithValue("$run_id", normalizedRunId);
        update.Parameters.AddWithValue("$chapter_index", chapterIndex);
        update.Parameters.AddWithValue("$pending", ReferenceMaterializationChapterStates.Pending);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Materialization chapter changed before extraction started.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReferenceChapterMaterializationWorkItem(
            snapshot.RunId,
            snapshot.GenerationId,
            new ReferenceMaterializationLlmSelection(snapshot.ModelProvider, snapshot.ModelId, string.Empty),
            new ReferenceMaterializationEmbeddingModel(
                snapshot.EmbeddingProvider,
                snapshot.EmbeddingModelId,
                snapshot.EmbeddingDimensions),
            snapshot.AnchorId,
            snapshot.ChapterIndex,
            snapshot.ChapterTitle,
            chapterText,
            snapshot.ChapterTextHash);
    }

    public async ValueTask MarkBatchEmbeddingAsync(
        string runId,
        IReadOnlyList<ReferenceChapterMaterializationWorkItem> workItems,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId) || workItems is null || workItems.Count == 0 ||
            workItems.Any(workItem => !string.Equals(workItem.RunId, runId, StringComparison.Ordinal)) ||
            workItems.Select(workItem => workItem.ChapterIndex).Distinct().Count() != workItems.Count)
        {
            throw new ArgumentException("Materialization batch work items are invalid.", nameof(workItems));
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var chapterParameters = workItems.Select((_, index) => "$chapter_" + index).ToArray();
        command.CommandText = $"""
            UPDATE reference_materialization_chapter_progress
            SET status = $embedding,
                current_stage = $embedding,
                row_version = row_version + 1
            WHERE run_id = $run_id
              AND status = $extracting
              AND chapter_index IN ({string.Join(", ", chapterParameters)});
            """;
        command.Parameters.AddWithValue("$embedding", ReferenceMaterializationChapterStates.Embedding);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$extracting", ReferenceMaterializationChapterStates.Extracting);
        for (var index = 0; index < workItems.Count; index++)
        {
            command.Parameters.AddWithValue(chapterParameters[index], workItems[index].ChapterIndex);
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) != workItems.Count)
        {
            throw new InvalidOperationException("Materialization batch changed before embedding started.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask PersistChapterAsync(
        ReferenceChapterMaterializationWorkItem workItem,
        IReadOnlyList<PreparedReferenceMaterial> materials,
        ReferenceMaterializationEmbeddingResult embeddingResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(embeddingResult);
        ValidatePreparedMaterials(workItem, materials);
        var embeddings = ValidateEmbeddings(workItem, materials, embeddingResult);

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var snapshot = await ReadChapterSnapshotAsync(
            connection,
            transaction: null,
            workItem.RunId,
            workItem.ChapterIndex,
            cancellationToken)
            ?? throw new InvalidOperationException("Materialization chapter disappeared before persistence.");
        var source = await ReadFrozenSourceAsync(snapshot.SourcePath, snapshot.SourceHash, cancellationToken);
        var currentChapterText = ReadAndValidateChapterText(source, snapshot);
        if (!string.Equals(currentChapterText, workItem.ChapterText, StringComparison.Ordinal) ||
            !string.Equals(snapshot.GenerationId, workItem.GenerationId, StringComparison.Ordinal))
        {
            throw SourceChanged();
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var current = await ReadChapterSnapshotAsync(
            connection,
            transaction,
            workItem.RunId,
            workItem.ChapterIndex,
            cancellationToken)
            ?? throw new InvalidOperationException("Materialization chapter disappeared before persistence.");
        if (current.RunStatus != ReferenceMaterializationRunStates.Extracting ||
            current.ChapterStatus != ReferenceMaterializationChapterStates.Embedding ||
            !string.Equals(current.GenerationId, workItem.GenerationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Materialization chapter changed before persistence.");
        }

        await EnsureNoGenerationTextDuplicatesAsync(
            connection,
            transaction,
            workItem.GenerationId,
            materials,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var material in materials)
        {
            await InsertMaterialAsync(connection, transaction, workItem, material, now, cancellationToken);
            await InsertEmbeddingAsync(
                connection,
                transaction,
                workItem,
                material,
                embeddings[material.MaterialId],
                now,
                cancellationToken);
        }

        await using (var progress = connection.CreateCommand())
        {
            progress.Transaction = transaction;
            progress.CommandText = """
                UPDATE reference_materialization_chapter_progress
                SET status = $completed,
                    current_stage = $completed,
                    material_count = $material_count,
                    vector_count = $vector_count,
                    completed_at = $completed_at,
                    row_version = row_version + 1
                WHERE run_id = $run_id
                  AND chapter_index = $chapter_index
                  AND status = $embedding;
                """;
            progress.Parameters.AddWithValue("$completed", ReferenceMaterializationChapterStates.Completed);
            progress.Parameters.AddWithValue("$material_count", materials.Count);
            progress.Parameters.AddWithValue("$vector_count", embeddings.Count);
            progress.Parameters.AddWithValue("$completed_at", FormatTimestamp(now));
            progress.Parameters.AddWithValue("$run_id", workItem.RunId);
            progress.Parameters.AddWithValue("$chapter_index", workItem.ChapterIndex);
            progress.Parameters.AddWithValue("$embedding", ReferenceMaterializationChapterStates.Embedding);
            if (await progress.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("Materialization chapter changed while persisting its result.");
            }
        }

        await RefreshRunCountsAsync(connection, transaction, workItem.RunId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public static IReadOnlyList<PreparedReferenceMaterial> PrepareMaterials(
        ReferenceChapterMaterializationWorkItem workItem,
        ReferenceChapterMaterialExtractionResult result)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Materials is null || result.Materials.Count == 0)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.NoMaterials,
                "The chapter material extractor returned no materials.");
        }

        var prepared = new List<PreparedReferenceMaterial>(result.Materials.Count);
        var seenText = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < result.Materials.Count; ordinal++)
        {
            var source = result.Materials[ordinal];
            if (source is null ||
                !IsIdentifier(source.MaterialType) ||
                !IsRequiredText(source.Text) ||
                !IsRequiredText(source.Description, 1_000) ||
                source.Tags is null ||
                source.Tags.Count > 16 ||
                source.Tags.Any(tag => !IsIdentifier(tag)) ||
                source.Tags.Distinct(StringComparer.Ordinal).Count() != source.Tags.Count)
            {
                throw InvalidModelOutput("Chapter material extraction returned invalid material fields.");
            }

            if (!workItem.ChapterText.Contains(source.Text, StringComparison.Ordinal))
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.SourceTextMismatch,
                    "Chapter material text does not match the frozen chapter source.");
            }

            if (!seenText.Add(source.Text))
            {
                throw InvalidModelOutput("Chapter material extraction returned duplicate source text.");
            }

            var textHash = HashValue(source.Text);
            prepared.Add(new PreparedReferenceMaterial(
                CreateMaterialId(workItem.GenerationId, workItem.ChapterIndex, ordinal, textHash),
                ordinal,
                source.MaterialType,
                source.Text,
                source.Description,
                source.Tags.ToArray(),
                textHash));
        }

        return prepared;
    }

    private static async ValueTask<ChapterSnapshot?> ReadChapterSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run.run_id, run.generation_id, run.anchor_id, run.status,
                   run.model_provider, run.model_id,
                   run.embedding_provider, run.embedding_model_id, run.embedding_dimensions,
                   anchor.source_path, profile.source_hash,
                   boundary.title, boundary.content_start, boundary.content_end, boundary.text_hash,
                   progress.chapter_index, progress.status
            FROM reference_materialization_runs run
            JOIN reference_chapter_split_profiles profile
              ON profile.split_profile_id = run.split_profile_id
            JOIN reference_chapter_split_boundaries boundary
              ON boundary.split_profile_id = run.split_profile_id
             AND boundary.chapter_index = $chapter_index
            JOIN reference_materialization_chapter_progress progress
              ON progress.run_id = run.run_id
             AND progress.chapter_index = boundary.chapter_index
            JOIN reference_anchors anchor ON anchor.anchor_id = run.anchor_id
            WHERE run.run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ChapterSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetString(14),
                reader.GetInt32(15),
                reader.GetString(16))
            : null;
    }

    private static async ValueTask<string> ReadFrozenSourceAsync(
        string sourcePath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxMaterializationSourceBytes)
        {
            throw SourceChanged();
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw SourceChanged();
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    private static string ReadAndValidateChapterText(string source, ChapterSnapshot snapshot)
    {
        if (snapshot.ContentStart < 0 ||
            snapshot.ContentEnd <= snapshot.ContentStart ||
            snapshot.ContentEnd > source.Length)
        {
            throw SourceChanged();
        }

        var chapterText = source[snapshot.ContentStart..snapshot.ContentEnd];
        if (string.IsNullOrWhiteSpace(chapterText) ||
            !string.Equals(HashValue(chapterText), snapshot.ChapterTextHash, StringComparison.Ordinal))
        {
            throw SourceChanged();
        }

        return chapterText;
    }

    private static void ValidatePreparedMaterials(
        ReferenceChapterMaterializationWorkItem workItem,
        IReadOnlyList<PreparedReferenceMaterial> materials)
    {
        if (materials.Count == 0 ||
            materials.Select(material => material.MaterialId).Distinct(StringComparer.Ordinal).Count() != materials.Count ||
            materials.Select(material => material.Text).Distinct(StringComparer.Ordinal).Count() != materials.Count)
        {
            throw InvalidModelOutput("Prepared chapter materials are incomplete or duplicated.");
        }

        for (var index = 0; index < materials.Count; index++)
        {
            var material = materials[index];
            if (material.Ordinal != index ||
                !string.Equals(
                    material.MaterialId,
                    CreateMaterialId(workItem.GenerationId, workItem.ChapterIndex, index, material.TextHash),
                    StringComparison.Ordinal) ||
                !string.Equals(HashValue(material.Text), material.TextHash, StringComparison.Ordinal) ||
                !workItem.ChapterText.Contains(material.Text, StringComparison.Ordinal))
            {
                throw InvalidModelOutput("Prepared chapter material identity is invalid.");
            }
        }
    }

    private static IReadOnlyDictionary<string, ReferenceMaterializationMaterialEmbedding> ValidateEmbeddings(
        ReferenceChapterMaterializationWorkItem workItem,
        IReadOnlyList<PreparedReferenceMaterial> materials,
        ReferenceMaterializationEmbeddingResult result)
    {
        if (result.Embeddings is null || result.Embeddings.Count != materials.Count)
        {
            throw InvalidEmbedding("Embedding response did not contain one vector per material.");
        }

        var expected = materials.Select(material => material.MaterialId).ToHashSet(StringComparer.Ordinal);
        var actual = new Dictionary<string, ReferenceMaterializationMaterialEmbedding>(StringComparer.Ordinal);
        foreach (var embedding in result.Embeddings)
        {
            if (embedding is null ||
                !expected.Contains(embedding.MaterialId) ||
                !actual.TryAdd(embedding.MaterialId, embedding) ||
                embedding.Vector is null ||
                embedding.Vector.Count != workItem.EmbeddingModel.Dimensions ||
                embedding.Vector.Any(value => float.IsNaN(value) || float.IsInfinity(value)))
            {
                throw InvalidEmbedding("Embedding response is incomplete or invalid.");
            }
        }

        return actual;
    }

    private static async ValueTask EnsureNoGenerationTextDuplicatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string generationId,
        IReadOnlyList<PreparedReferenceMaterial> materials,
        CancellationToken cancellationToken)
    {
        foreach (var material in materials)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EXISTS(
                  SELECT 1
                  FROM reference_materials
                  WHERE generation_id = $generation_id
                    AND text_hash = $text_hash
                    AND text = $text);
                """;
            command.Parameters.AddWithValue("$generation_id", generationId);
            command.Parameters.AddWithValue("$text_hash", material.TextHash);
            command.Parameters.AddWithValue("$text", material.Text);
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
            {
                throw InvalidModelOutput("Materialization generation contains duplicate source text.");
            }
        }
    }

    private static async ValueTask InsertMaterialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceChapterMaterializationWorkItem workItem,
        PreparedReferenceMaterial material,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materials (
              material_id, generation_id, run_id, anchor_id, chapter_index, ordinal,
              material_type, text, description, tags_json, text_hash, created_at)
            VALUES (
              $material_id, $generation_id, $run_id, $anchor_id, $chapter_index, $ordinal,
              $material_type, $text, $description, $tags_json, $text_hash, $created_at);
            """;
        command.Parameters.AddWithValue("$material_id", material.MaterialId);
        command.Parameters.AddWithValue("$generation_id", workItem.GenerationId);
        command.Parameters.AddWithValue("$run_id", workItem.RunId);
        command.Parameters.AddWithValue("$anchor_id", workItem.AnchorId);
        command.Parameters.AddWithValue("$chapter_index", workItem.ChapterIndex);
        command.Parameters.AddWithValue("$ordinal", material.Ordinal);
        command.Parameters.AddWithValue("$material_type", material.MaterialType);
        command.Parameters.AddWithValue("$text", material.Text);
        command.Parameters.AddWithValue("$description", material.Description);
        command.Parameters.AddWithValue("$tags_json", JsonSerializer.Serialize(material.Tags));
        command.Parameters.AddWithValue("$text_hash", material.TextHash);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertEmbeddingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ReferenceChapterMaterializationWorkItem workItem,
        PreparedReferenceMaterial material,
        ReferenceMaterializationMaterialEmbedding embedding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var blob = SerializeVector(embedding.Vector);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_material_embeddings (
              material_id, generation_id, provider, model_id, dimensions,
              embedding_hash, embedding_blob, created_at)
            VALUES (
              $material_id, $generation_id, $provider, $model_id, $dimensions,
              $embedding_hash, $embedding_blob, $created_at);
            """;
        command.Parameters.AddWithValue("$material_id", material.MaterialId);
        command.Parameters.AddWithValue("$generation_id", workItem.GenerationId);
        command.Parameters.AddWithValue("$provider", workItem.EmbeddingModel.Provider);
        command.Parameters.AddWithValue("$model_id", workItem.EmbeddingModel.ModelId);
        command.Parameters.AddWithValue("$dimensions", workItem.EmbeddingModel.Dimensions);
        command.Parameters.AddWithValue("$embedding_hash", Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant());
        command.Parameters.AddWithValue("$embedding_blob", blob);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] SerializeVector(IReadOnlyList<float> vector)
    {
        var bytes = new byte[checked(vector.Count * sizeof(float))];
        for (var index = 0; index < vector.Count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(vector[index]));
        }

        return bytes;
    }

    internal static float[] DeserializeVector(byte[] bytes, int dimensions)
    {
        if (bytes.Length != checked(dimensions * sizeof(float)))
        {
            throw InvalidEmbedding("Stored material embedding has invalid dimensions.");
        }

        var vector = new float[dimensions];
        for (var index = 0; index < dimensions; index++)
        {
            vector[index] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float))));
            if (float.IsNaN(vector[index]) || float.IsInfinity(vector[index]))
            {
                throw InvalidEmbedding("Stored material embedding contains a non-finite value.");
            }
        }

        return vector;
    }

    private static string CreateMaterialId(string generationId, int chapterIndex, int ordinal, string textHash) =>
        "material-" + HashValue($"{generationId}|{chapterIndex}|{ordinal}|{textHash}")[..32];

    private static string HashValue(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool IsRequiredText(string? value, int maximumLength = int.MaxValue) =>
        value is not null &&
        value.Length is > 0 &&
        value.Length <= maximumLength &&
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Contains('\0');

    private static bool IsIdentifier(string? value)
    {
        if (value is null || value.Length is 0 or > 64 || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        return value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }

    private static ReferenceMaterializationException SourceChanged() =>
        new(
            ReferenceMaterializationErrorCodes.SourceChanged,
            "The frozen reference source changed during materialization.");

    private static ReferenceMaterializationException InvalidModelOutput(string message) =>
        new(ReferenceMaterializationErrorCodes.LlmOutputInvalid, message);

    private static ReferenceMaterializationException InvalidEmbedding(string message) =>
        new(ReferenceMaterializationErrorCodes.EmbeddingInvalid, message);

    private sealed record ChapterSnapshot(
        string RunId,
        string GenerationId,
        long AnchorId,
        string RunStatus,
        string ModelProvider,
        string ModelId,
        string EmbeddingProvider,
        string EmbeddingModelId,
        int EmbeddingDimensions,
        string SourcePath,
        string SourceHash,
        string ChapterTitle,
        int ContentStart,
        int ContentEnd,
        string ChapterTextHash,
        int ChapterIndex,
        string ChapterStatus);
}

internal sealed record ReferenceChapterMaterializationWorkItem(
    string RunId,
    string GenerationId,
    ReferenceMaterializationLlmSelection Model,
    ReferenceMaterializationEmbeddingModel EmbeddingModel,
    long AnchorId,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterText,
    string ChapterTextHash);

internal sealed record PreparedReferenceMaterial(
    string MaterialId,
    int Ordinal,
    string MaterialType,
    string Text,
    string Description,
    IReadOnlyList<string> Tags,
    string TextHash);
