using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceMaterializationSemanticSearch : IReferenceMaterializationSemanticSearch
{
    private const int MaxQueryCharacters = 256;
    private const int MaxResults = 100;

    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly IEmbeddingConfigurationService _embeddingConfiguration;
    private readonly IEmbeddingClient _embeddings;
    private readonly ISqliteVecQueryProvider _vecQuery;

    public SqliteReferenceMaterializationSemanticSearch(
        AppInitializationOptions? options = null,
        IReferenceCorpusDatabasePathResolver? databasePathResolver = null,
        IEmbeddingConfigurationService? embeddingConfiguration = null,
        IEmbeddingClient? embeddings = null,
        ISqliteVecQueryProvider? vecQuery = null)
    {
        var initializationOptions = options ?? new AppInitializationOptions();
        _databasePathResolver = databasePathResolver ?? new ReferenceCorpusDatabasePathResolver(initializationOptions);
        _embeddingConfiguration = embeddingConfiguration ?? new FileSystemEmbeddingSettingsService(initializationOptions);
        _embeddings = embeddings ?? new HybridEmbeddingClient();
        _vecQuery = vecQuery ?? new SqliteVecTableProvisioner();
    }

    public async ValueTask<IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload>> SearchAsync(
        long anchorId,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId));
        }

        var normalizedQuery = NormalizeQuery(query);
        if (normalizedQuery.Length == 0)
        {
            return [];
        }

        if (maxResults is <= 0 or > MaxResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResults), $"Semantic material search max_results must be between 1 and {MaxResults}.");
        }

        var embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken)
            ?? throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingNotConfigured,
                "Active-generation material search requires a configured embedding model.");
        var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        var snapshot = await ReadActiveGenerationSnapshotAsync(connection, anchorId, cancellationToken);
        if (snapshot is null)
        {
            return [];
        }

        ValidateFrozenEmbedding(snapshot, embeddingOptions);
        var queryVector = await EmbedQueryAsync(normalizedQuery, embeddingOptions, snapshot, cancellationToken);
        IReadOnlyList<SqliteVecSearchRecord> vectorResults;
        try
        {
            vectorResults = await _vecQuery.SearchAsync(
                databasePath,
                new SqliteVecSearchRequest(snapshot.TableName, snapshot.Dimensions, queryVector, maxResults),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.VectorIndexFailed,
                "Active-generation material vector search failed.");
        }

        ValidateVectorResults(vectorResults, maxResults);
        if (vectorResults.Count == 0)
        {
            return [];
        }

        var materialsByEmbeddingRowId = await ReadActiveMaterialsByEmbeddingRowIdAsync(
            connection,
            anchorId,
            snapshot,
            vectorResults.Select(result => result.RowId).ToArray(),
            cancellationToken);
        if (materialsByEmbeddingRowId.Count != vectorResults.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Active-generation vector rows no longer match the promoted materials.");
        }

        return vectorResults
            .OrderBy(result => result.Distance)
            .ThenBy(result => result.RowId)
            .Select(result => new ReferenceMaterializationSemanticSearchHitPayload(
                materialsByEmbeddingRowId[result.RowId],
                Math.Round(Math.Clamp(1.0 - result.Distance, 0, 1), 6)))
            .ToArray();
    }

    private static async ValueTask<ActiveGenerationSnapshot?> ReadActiveGenerationSnapshotAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        string? generationId;
        await using (var state = connection.CreateCommand())
        {
            state.CommandText = """
                SELECT active_generation_id
                FROM reference_anchor_materialization_state
                WHERE anchor_id = $anchor_id;
                """;
            state.Parameters.AddWithValue("$anchor_id", anchorId);
            generationId = (string?)await state.ExecuteScalarAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(generationId))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT index_metadata.run_id, index_metadata.table_name, index_metadata.provider,
                   index_metadata.model_id, index_metadata.dimensions, index_metadata.vector_count,
                   COUNT(material.material_id) AS material_count,
                   COUNT(embedding.candidate_id) AS embedding_count,
                   COUNT(DISTINCT material.run_id) AS material_run_count,
                   MAX(material.run_id) AS material_run_id
            FROM reference_materialization_vector_indexes index_metadata
            LEFT JOIN reference_materialization_materials material
              ON material.anchor_id = $anchor_id
             AND material.generation_id = index_metadata.generation_id
            LEFT JOIN reference_materialization_candidate_embeddings embedding
              ON embedding.candidate_id = material.candidate_id
             AND embedding.generation_id = material.generation_id
             AND embedding.run_id = material.run_id
             AND embedding.provider = index_metadata.provider
             AND embedding.model_id = index_metadata.model_id
             AND embedding.dimensions = index_metadata.dimensions
            WHERE index_metadata.generation_id = $generation_id
              AND index_metadata.status = 'ready'
            GROUP BY index_metadata.run_id, index_metadata.table_name, index_metadata.provider,
                     index_metadata.model_id, index_metadata.dimensions, index_metadata.vector_count;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "The active materialization generation has no ready vector index.");
        }

        var snapshot = new ActiveGenerationSnapshot(
            generationId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
        if (snapshot.Dimensions <= 0 ||
            snapshot.VectorCount != snapshot.MaterialCount ||
            snapshot.EmbeddingCount != snapshot.MaterialCount ||
            (snapshot.MaterialCount > 0 &&
             (snapshot.MaterialRunCount != 1 ||
              !string.Equals(snapshot.RunId, snapshot.MaterialRunId, StringComparison.Ordinal))) ||
            !string.Equals(
                snapshot.TableName,
                SqliteVecTableProvisioner.BuildReferenceMaterializationVectorTableName(snapshot.GenerationId, snapshot.Dimensions),
                StringComparison.Ordinal))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "The active materialization generation is not vector-complete.");
        }

        return snapshot;
    }

    private static void ValidateFrozenEmbedding(
        ActiveGenerationSnapshot snapshot,
        EmbeddingRequestOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProviderKey) ||
            string.IsNullOrWhiteSpace(options.ModelId) ||
            options.Dimensions != snapshot.Dimensions ||
            !string.Equals(options.ProviderKey, snapshot.Provider, StringComparison.Ordinal) ||
            !string.Equals(options.ModelId, snapshot.ModelId, StringComparison.Ordinal))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                "The active embedding configuration no longer matches the frozen materialization generation.");
        }
    }

    private async ValueTask<IReadOnlyList<float>> EmbedQueryAsync(
        string query,
        EmbeddingRequestOptions options,
        ActiveGenerationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        EmbeddingBatchResult response;
        try
        {
            response = await _embeddings.EmbedAsync(
                [query],
                options with
                {
                    Dimensions = snapshot.Dimensions,
                    InputKind = BuiltinOnnxEmbeddingModel.QueryInputKind
                },
                cancellationToken);
        }
        catch (ReferenceMaterializationException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingRequestFailed,
                "Active-generation material search embedding request failed.");
        }

        if (response is null || response.Dimensions != snapshot.Dimensions || response.Items is null ||
            response.Items.Count != 1 || response.Items[0] is null || response.Items[0].Index != 0 ||
            response.Items[0].Vector is null || response.Items[0].Vector.Count != snapshot.Dimensions ||
            response.Items[0].Vector.Any(value => !float.IsFinite(value)))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingInvalid,
                "Active-generation material search embedding response did not match the frozen generation.");
        }

        return response.Items[0].Vector.ToArray();
    }

    private static async ValueTask<IReadOnlyDictionary<long, ReferenceMaterializationMaterialPayload>> ReadActiveMaterialsByEmbeddingRowIdAsync(
        SqliteConnection connection,
        long anchorId,
        ActiveGenerationSnapshot snapshot,
        IReadOnlyList<long> rowIds,
        CancellationToken cancellationToken)
    {
        var parameterNames = new List<string>(rowIds.Count);
        await using var command = connection.CreateCommand();
        for (var index = 0; index < rowIds.Count; index++)
        {
            var parameterName = "$row_id_" + index;
            command.Parameters.AddWithValue(parameterName, rowIds[index]);
            parameterNames.Add(parameterName);
        }

        command.CommandText = $"""
            SELECT embedding.rowid, material.material_id, material.anchor_id, material.generation_id,
                   material.material_type, material.text, material.quality_score, material.confidence,
                   material.tags_json, material.reason_codes_json
            FROM reference_materialization_materials material
            JOIN reference_anchor_materialization_state state
              ON state.anchor_id = material.anchor_id
             AND state.active_generation_id = material.generation_id
            JOIN reference_materialization_candidate_embeddings embedding
              ON embedding.candidate_id = material.candidate_id
             AND embedding.generation_id = material.generation_id
             AND embedding.run_id = material.run_id
             AND embedding.provider = $provider
             AND embedding.model_id = $model_id
             AND embedding.dimensions = $dimensions
            WHERE material.anchor_id = $anchor_id
              AND material.generation_id = $generation_id
              AND embedding.rowid IN ({string.Join(", ", parameterNames)});
            """;
        command.Parameters.AddWithValue("$provider", snapshot.Provider);
        command.Parameters.AddWithValue("$model_id", snapshot.ModelId);
        command.Parameters.AddWithValue("$dimensions", snapshot.Dimensions);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$generation_id", snapshot.GenerationId);

        var materials = new Dictionary<long, ReferenceMaterializationMaterialPayload>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rowId = reader.GetInt64(0);
            if (!materials.TryAdd(rowId, new ReferenceMaterializationMaterialPayload(
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    ParseTags(reader.GetString(8)),
                    ParseStringArray(reader.GetString(9), 12))))
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.GenerationIncomplete,
                    "Active-generation material embeddings are not unique.");
            }
        }

        return materials;
    }

    private static void ValidateVectorResults(IReadOnlyList<SqliteVecSearchRecord> results, int maxResults)
    {
        if (results is null || results.Count > maxResults ||
            results.Any(result => result.RowId <= 0 || double.IsNaN(result.Distance) || double.IsInfinity(result.Distance) || result.Distance < 0) ||
            results.Select(result => result.RowId).Distinct().Count() != results.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.VectorIndexFailed,
                "Active-generation material vector search returned invalid rows.");
        }
    }

    private static string NormalizeQuery(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > MaxQueryCharacters || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Semantic material search query is invalid.", nameof(value));
        }

        return normalized;
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static ReferenceMaterializationMaterialTagsPayload ParseTags(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            return new ReferenceMaterializationMaterialTagsPayload(
                ReadArray(root, "narrative_functions"),
                ReadArray(root, "emotion_mechanics"),
                ReadArray(root, "pov"),
                ReadArray(root, "techniques"));
        }
        catch (JsonException)
        {
            return new ReferenceMaterializationMaterialTagsPayload([], [], [], []);
        }
    }

    private static IReadOnlyList<string> ReadArray(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Take(12)
                .ToArray()
            : [];

    private static IReadOnlyList<string> ParseStringArray(string value, int maximumCount)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(item => item.Length > 0)
                    .Take(maximumCount)
                    .ToArray()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record ActiveGenerationSnapshot(
        string GenerationId,
        string RunId,
        string TableName,
        string Provider,
        string ModelId,
        int Dimensions,
        int VectorCount,
        int MaterialCount,
        int EmbeddingCount,
        int MaterialRunCount,
        string? MaterialRunId);
}
