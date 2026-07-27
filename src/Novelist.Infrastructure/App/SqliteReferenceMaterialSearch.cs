using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceMaterialSearch : IReferenceMaterialSearch
{
    private const int MaxQueryCharacters = 256;
    private const int MaxResults = 100;
    private const int MaxPageSize = 100;
    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly IEmbeddingConfigurationService _embeddingConfiguration;
    private readonly IEmbeddingClient _embeddings;
    private readonly ISqliteVecQueryProvider _vectors;

    public SqliteReferenceMaterialSearch(
        AppInitializationOptions? options = null,
        IReferenceCorpusDatabasePathResolver? databasePathResolver = null,
        IEmbeddingConfigurationService? embeddingConfiguration = null,
        IEmbeddingClient? embeddings = null,
        ISqliteVecQueryProvider? vectors = null)
    {
        var initializationOptions = options ?? new AppInitializationOptions();
        _databasePathResolver = databasePathResolver ?? new ReferenceCorpusDatabasePathResolver(initializationOptions);
        _embeddingConfiguration = embeddingConfiguration ?? new FileSystemEmbeddingSettingsService(initializationOptions);
        _embeddings = embeddings ?? new HybridEmbeddingClient();
        _vectors = vectors ?? new SqliteVecTableProvisioner();
    }

    public async ValueTask<ReferenceMaterialListPage> ListAsync(
        ReferenceMaterialListRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.NovelId <= 0 || input.AnchorId <= 0 || input.Page <= 0 || input.Size is <= 0 or > MaxPageSize)
        {
            throw new ArgumentException("Reference material list request is invalid.", nameof(input));
        }

        var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        var generationId = await ReadListGenerationAsync(connection, input, cancellationToken)
            ?? throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "The selected reference source does not have an active material generation.");
        var total = await CountListMaterialsAsync(connection, input.AnchorId, generationId, cancellationToken);
        if (total <= 0)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "The selected active material generation is empty.");
        }

        var offset = ((long)input.Page - 1) * input.Size;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id, generation_id, anchor_id, chapter_index, ordinal,
                   text, metadata_json, text_hash
            FROM reference_materials
            WHERE anchor_id = $anchor_id
              AND generation_id = $generation_id
            ORDER BY chapter_index, ordinal
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$anchor_id", input.AnchorId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        command.Parameters.AddWithValue("$limit", input.Size);
        command.Parameters.AddWithValue("$offset", offset);
        var items = new List<ReferenceMaterialListItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new ReferenceMaterialListItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetString(5),
                    ParseMetadata(reader.GetString(6)),
                    reader.GetString(7)));
            }
        }

        await EnsureListGenerationStillActiveAsync(connection, input.AnchorId, generationId, cancellationToken);
        return new ReferenceMaterialListPage(
            items,
            total,
            input.Page,
            input.Size,
            (int)Math.Ceiling(total / (double)input.Size));
    }

    public async ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchAsync(
        ReferenceMaterialSearchRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var query = NormalizeQuery(input.Query);
        if (input.MaxResults is <= 0 or > MaxResults)
        {
            throw new ArgumentOutOfRangeException(nameof(input), $"Material search max results must be between 1 and {MaxResults}.");
        }

        ValidateScope(input);
        var options = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken)
            ?? throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingNotConfigured,
                "Reference material search requires a configured embedding model.");
        var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        var anchorIds = await ResolveAnchorScopeAsync(connection, input, cancellationToken);
        if (anchorIds.Count == 0)
        {
            return [];
        }

        var snapshots = await ReadActiveSnapshotsAsync(connection, anchorIds, cancellationToken);
        ValidateSnapshots(snapshots, anchorIds, options);
        var queryVector = await EmbedQueryAsync(query, options, snapshots[0].Dimensions, cancellationToken);
        var hits = new List<ReferenceMaterialSearchHit>();
        foreach (var snapshot in snapshots)
        {
            IReadOnlyList<SqliteVecSearchRecord> vectorResults;
            try
            {
                vectorResults = await _vectors.SearchAsync(
                    databasePath,
                    new SqliteVecSearchRequest(
                        snapshot.TableName,
                        snapshot.Dimensions,
                        queryVector,
                        input.MaxResults),
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
                    "Reference material vector search failed.");
            }

            ValidateVectorResults(vectorResults, input.MaxResults);
            hits.AddRange(await ReadHitsAsync(connection, snapshot, vectorResults, cancellationToken));
        }

        await EnsureGenerationsStillActiveAsync(connection, snapshots, cancellationToken);
        return hits
            .OrderBy(hit => hit.VectorDistance)
            .ThenBy(hit => hit.AnchorId)
            .ThenBy(hit => hit.ChapterIndex)
            .ThenBy(hit => hit.Ordinal)
            .Take(input.MaxResults)
            .ToArray();
    }

    private static void ValidateScope(ReferenceMaterialSearchRequest input)
    {
        var scopes = 0;
        scopes += input.NovelId.HasValue ? 1 : 0;
        scopes += string.IsNullOrWhiteSpace(input.SessionId) ? 0 : 1;
        scopes += input.LibraryIds is { Count: > 0 } ? 1 : 0;
        scopes += input.AnchorIds is { Count: > 0 } ? 1 : 0;
        if (scopes != 1)
        {
            throw new ArgumentException("Reference material search requires exactly one scope.", nameof(input));
        }

        if (input.NovelId is <= 0 ||
            input.AnchorIds is { Count: > 100 } ||
            input.AnchorIds?.Any(anchorId => anchorId <= 0) == true ||
            input.LibraryIds is { Count: > 100 } ||
            input.LibraryIds?.Any(value => !IsBoundedIdentifier(value)) == true ||
            (!string.IsNullOrWhiteSpace(input.SessionId) && !IsBoundedIdentifier(input.SessionId)))
        {
            throw new ArgumentException("Reference material search scope is invalid.", nameof(input));
        }
    }

    private static async ValueTask<string?> ReadListGenerationAsync(
        SqliteConnection connection,
        ReferenceMaterialListRequest input,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT state.active_generation_id
            FROM reference_anchor_materialization_state state
            JOIN reference_anchors anchor ON anchor.anchor_id = state.anchor_id
            JOIN reference_source_license license ON license.anchor_id = anchor.anchor_id
            WHERE anchor.anchor_id = $anchor_id
              AND (
                anchor.novel_id = $novel_id OR
                ((anchor.novel_id IS NULL OR anchor.novel_id = 0) AND anchor.corpus_visibility = 'workspace')
              )
              AND license.license_state IN ($public_domain, $creative_commons, $authorized)
              AND license.reuse_policy <> $forbidden
              AND EXISTS (
                SELECT 1
                FROM reference_library_members member
                WHERE member.anchor_id = anchor.anchor_id
                  AND member.enabled = 1
              );
            """;
        command.Parameters.AddWithValue("$anchor_id", input.AnchorId);
        command.Parameters.AddWithValue("$novel_id", input.NovelId);
        command.Parameters.AddWithValue("$public_domain", ReferenceCorpusLicenseStates.PublicDomain);
        command.Parameters.AddWithValue("$creative_commons", ReferenceCorpusLicenseStates.CreativeCommons);
        command.Parameters.AddWithValue("$authorized", ReferenceCorpusLicenseStates.Authorized);
        command.Parameters.AddWithValue("$forbidden", ReferenceCorpusReusePolicies.Forbidden);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async ValueTask<long> CountListMaterialsAsync(
        SqliteConnection connection,
        long anchorId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materials
            WHERE anchor_id = $anchor_id
              AND generation_id = $generation_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async ValueTask EnsureListGenerationStillActiveAsync(
        SqliteConnection connection,
        long anchorId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT active_generation_id
            FROM reference_anchor_materialization_state
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var current = await command.ExecuteScalarAsync(cancellationToken);
        if (current is not string value || !string.Equals(value, generationId, StringComparison.Ordinal))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "The active reference material generation changed while it was being listed.");
        }
    }

    private static async ValueTask<IReadOnlyList<long>> ResolveAnchorScopeAsync(
        SqliteConnection connection,
        ReferenceMaterialSearchRequest input,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        string scopeClause;
        if (input.AnchorIds is { Count: > 0 })
        {
            var parameters = AddParameters(command, "$anchor", input.AnchorIds.Cast<object>().ToArray());
            scopeClause = $"anchor.anchor_id IN ({string.Join(", ", parameters)})";
        }
        else if (input.LibraryIds is { Count: > 0 })
        {
            var parameters = AddParameters(command, "$library", input.LibraryIds.Cast<object>().ToArray());
            scopeClause = $"member.library_id IN ({string.Join(", ", parameters)})";
        }
        else if (!string.IsNullOrWhiteSpace(input.SessionId))
        {
            command.Parameters.AddWithValue("$session_id", input.SessionId!.Trim());
            scopeClause = "binding.session_id = $session_id";
        }
        else
        {
            command.Parameters.AddWithValue("$novel_id", input.NovelId!.Value);
            scopeClause = "(library.scope = 'global' OR (library.scope = 'project' AND library.novel_id = $novel_id))";
        }

        command.CommandText = $"""
            SELECT DISTINCT anchor.anchor_id
            FROM reference_anchors anchor
            JOIN reference_library_members member
              ON member.anchor_id = anchor.anchor_id
             AND member.enabled = 1
            JOIN reference_corpus_libraries library
              ON library.library_id = member.library_id
            LEFT JOIN reference_session_library_binding binding
              ON binding.library_id = member.library_id
            JOIN reference_source_license license
              ON license.anchor_id = anchor.anchor_id
            WHERE {scopeClause}
              AND license.license_state IN ($public_domain, $creative_commons, $authorized)
              AND license.reuse_policy <> $forbidden
            ORDER BY anchor.anchor_id;
            """;
        command.Parameters.AddWithValue("$public_domain", ReferenceCorpusLicenseStates.PublicDomain);
        command.Parameters.AddWithValue("$creative_commons", ReferenceCorpusLicenseStates.CreativeCommons);
        command.Parameters.AddWithValue("$authorized", ReferenceCorpusLicenseStates.Authorized);
        command.Parameters.AddWithValue("$forbidden", ReferenceCorpusReusePolicies.Forbidden);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var anchorIds = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            anchorIds.Add(reader.GetInt64(0));
        }

        return anchorIds;
    }

    private static async ValueTask<IReadOnlyList<ActiveSnapshot>> ReadActiveSnapshotsAsync(
        SqliteConnection connection,
        IReadOnlyList<long> anchorIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var parameters = AddParameters(command, "$anchor", anchorIds.Cast<object>().ToArray());
        command.CommandText = $"""
            SELECT state.anchor_id, state.active_generation_id,
                   vector.table_name, vector.provider, vector.model_id,
                   vector.dimensions, vector.vector_count,
                   (SELECT COUNT(*)
                    FROM reference_materials material
                    WHERE material.anchor_id = state.anchor_id
                      AND material.generation_id = state.active_generation_id) AS material_count,
                   (SELECT COUNT(*)
                    FROM reference_material_embeddings embedding
                    WHERE embedding.generation_id = state.active_generation_id) AS embedding_count
            FROM reference_anchor_materialization_state state
            JOIN reference_materialization_vector_indexes vector
              ON vector.generation_id = state.active_generation_id
             AND vector.status = 'ready'
            WHERE state.anchor_id IN ({string.Join(", ", parameters)})
            ORDER BY state.anchor_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var snapshots = new List<ActiveSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(new ActiveSnapshot(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt32(8)));
        }

        return snapshots;
    }

    private static void ValidateSnapshots(
        IReadOnlyList<ActiveSnapshot> snapshots,
        IReadOnlyList<long> anchorIds,
        EmbeddingRequestOptions options)
    {
        if (snapshots.Count != anchorIds.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Every selected reference source must have an active material generation.");
        }

        foreach (var snapshot in snapshots)
        {
            if (!anchorIds.Contains(snapshot.AnchorId) ||
                snapshot.Dimensions <= 0 ||
                snapshot.MaterialCount <= 0 ||
                snapshot.MaterialCount != snapshot.EmbeddingCount ||
                snapshot.MaterialCount != snapshot.VectorCount ||
                !string.Equals(snapshot.Provider, options.ProviderKey, StringComparison.Ordinal) ||
                !string.Equals(snapshot.ModelId, options.ModelId, StringComparison.Ordinal) ||
                snapshot.Dimensions != options.Dimensions ||
                !string.Equals(
                    snapshot.TableName,
                    SqliteVecTableProvisioner.BuildReferenceMaterializationVectorTableName(
                        snapshot.GenerationId,
                        snapshot.Dimensions),
                    StringComparison.Ordinal))
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.GenerationIncomplete,
                    "Selected reference material generation is not vector-complete for the active embedding model.");
            }
        }

        if (snapshots.Select(snapshot => (snapshot.Provider, snapshot.ModelId, snapshot.Dimensions)).Distinct().Count() != 1)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingHealthCheckFailed,
                "Selected reference generations use incompatible embedding models.");
        }
    }

    private async ValueTask<IReadOnlyList<float>> EmbedQueryAsync(
        string query,
        EmbeddingRequestOptions options,
        int dimensions,
        CancellationToken cancellationToken)
    {
        EmbeddingBatchResult result;
        try
        {
            result = await _embeddings.EmbedAsync(
                [query],
                options with
                {
                    Dimensions = dimensions,
                    InputKind = BuiltinOnnxEmbeddingModel.QueryInputKind
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingRequestFailed,
                "Reference material search embedding request failed.");
        }

        if (result is null ||
            result.Dimensions != dimensions ||
            result.Items is null ||
            result.Items.Count != 1 ||
            result.Items[0].Index != 0 ||
            result.Items[0].Vector.Count != dimensions ||
            result.Items[0].Vector.Any(value => !float.IsFinite(value)))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.EmbeddingInvalid,
                "Reference material search embedding response is invalid.");
        }

        return result.Items[0].Vector.ToArray();
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> ReadHitsAsync(
        SqliteConnection connection,
        ActiveSnapshot snapshot,
        IReadOnlyList<SqliteVecSearchRecord> vectorResults,
        CancellationToken cancellationToken)
    {
        if (vectorResults.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var rowParameters = AddParameters(
            command,
            "$row",
            vectorResults.Select(result => (object)result.RowId).ToArray());
        command.CommandText = $"""
            SELECT embedding.rowid,
                   material.material_id, material.generation_id, material.anchor_id,
                   material.chapter_index, material.ordinal, material.text, material.metadata_json,
                   material.text_hash
            FROM reference_material_embeddings embedding
            JOIN reference_materials material
              ON material.material_id = embedding.material_id
            JOIN reference_anchor_materialization_state state
              ON state.anchor_id = material.anchor_id
             AND state.active_generation_id = material.generation_id
            WHERE embedding.rowid IN ({string.Join(", ", rowParameters)})
              AND material.anchor_id = $anchor_id
              AND material.generation_id = $generation_id
              AND embedding.provider = $provider
              AND embedding.model_id = $model_id
              AND embedding.dimensions = $dimensions;
            """;
        command.Parameters.AddWithValue("$anchor_id", snapshot.AnchorId);
        command.Parameters.AddWithValue("$generation_id", snapshot.GenerationId);
        command.Parameters.AddWithValue("$provider", snapshot.Provider);
        command.Parameters.AddWithValue("$model_id", snapshot.ModelId);
        command.Parameters.AddWithValue("$dimensions", snapshot.Dimensions);
        var distanceByRowId = vectorResults.ToDictionary(result => result.RowId, result => result.Distance);
        var hits = new List<ReferenceMaterialSearchHit>(vectorResults.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rowId = reader.GetInt64(0);
            hits.Add(new ReferenceMaterialSearchHit(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6),
                ParseMetadata(reader.GetString(7)),
                reader.GetString(8),
                distanceByRowId[rowId]));
        }

        if (hits.Count != vectorResults.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.VectorIndexFailed,
                "Reference material vector index returned rows outside the active generation.");
        }

        return hits;
    }

    private static async ValueTask EnsureGenerationsStillActiveAsync(
        SqliteConnection connection,
        IReadOnlyList<ActiveSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        foreach (var snapshot in snapshots)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT active_generation_id
                FROM reference_anchor_materialization_state
                WHERE anchor_id = $anchor_id;
                """;
            command.Parameters.AddWithValue("$anchor_id", snapshot.AnchorId);
            var generationId = await command.ExecuteScalarAsync(cancellationToken);
            if (generationId is not string value ||
                !string.Equals(value, snapshot.GenerationId, StringComparison.Ordinal))
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.GenerationIncomplete,
                    "Active reference material generation changed during search.");
            }
        }
    }

    private static void ValidateVectorResults(IReadOnlyList<SqliteVecSearchRecord> results, int maximumCount)
    {
        if (results is null ||
            results.Count > maximumCount ||
            results.Select(result => result.RowId).Distinct().Count() != results.Count ||
            results.Any(result =>
                result.RowId <= 0 ||
                !double.IsFinite(result.Distance) ||
                result.Distance < 0))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.VectorIndexFailed,
                "Reference material vector search returned invalid rows.");
        }
    }

    private static ReferenceMaterialMetadata ParseMetadata(string json)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<ReferenceMaterialMetadata>(json);
            if (!ReferenceMaterialMetadataValidator.TryValidate(metadata, out _))
            {
                throw new InvalidOperationException("Stored reference material metadata is invalid.");
            }

            return metadata!;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored reference material metadata is invalid.", exception);
        }
    }

    private static IReadOnlyList<string> AddParameters(
        SqliteCommand command,
        string prefix,
        IReadOnlyList<object> values)
    {
        var names = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var name = prefix + index;
            command.Parameters.AddWithValue(name, values[index]);
            names.Add(name);
        }

        return names;
    }

    private static string NormalizeQuery(string value)
    {
        var query = value?.Trim() ?? string.Empty;
        if (query.Length is 0 or > MaxQueryCharacters || query.Any(char.IsControl))
        {
            throw new ArgumentException("Reference material search query is invalid.", nameof(value));
        }

        return query;
    }

    private static bool IsBoundedIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        !value.Any(char.IsControl);

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
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

    private sealed record ActiveSnapshot(
        long AnchorId,
        string GenerationId,
        string TableName,
        string Provider,
        string ModelId,
        int Dimensions,
        int VectorCount,
        int MaterialCount,
        int EmbeddingCount);
}
