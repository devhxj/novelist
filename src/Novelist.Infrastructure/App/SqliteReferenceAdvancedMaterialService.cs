using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

// 高级写作素材（L2）读取与复核：只读 reference_advanced_materials（统一外壳表）。
// evidence_refs_json 解析出的证据必须落到真实 reference_text_nodes，解析失败即视为无效证据。
public sealed class SqliteReferenceAdvancedMaterialService : IReferenceAdvancedMaterialService
{
    private static readonly PageRequestPolicy ListPagePolicy = new(
        AllowedSortFields: ["created_at", "confidence", "family", "layer", "material_id"],
        DefaultSortBy: "created_at",
        StableTieBreakers: ["material_id"]);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReferenceCorpusDatabasePathResolver _pathResolver;

    public SqliteReferenceAdvancedMaterialService(IReferenceCorpusDatabasePathResolver pathResolver)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    public async ValueTask<PageResultPayload<ReferenceAdvancedMaterialSummaryPayload>> ListAsync(
        ListReferenceAdvancedMaterialsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentException("anchor_id must be positive.", nameof(input));
        }

        ValidateOptionalFamily(input.Family);
        ValidateOptionalLayer(input.Layer);
        ValidateOptionalReviewState(input.ReviewState);

        var page = PageRequestNormalizer.Normalize(
            input.PageRequest ?? new PageRequestPayload(null, 50, "created_at", "desc"),
            ListPagePolicy);
        var offset = DecodeOffsetCursor(page.Cursor);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        var (whereSql, parameters) = BuildListFilter(input);
        var total = await CountAsync(connection, whereSql, parameters, cancellationToken);
        var orderBy = BuildOrderBy(page);

        var items = new List<ReferenceAdvancedMaterialSummaryPayload>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT material_id, anchor_id, layer, family, feature_key, value_text,
                       confidence, review_state, validity_state, created_at
                FROM reference_advanced_materials
                {whereSql}
                ORDER BY {orderBy}
                LIMIT $limit OFFSET $offset;
                """;
            AddParameters(command, parameters);
            command.Parameters.AddWithValue("$limit", page.PageSize);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new ReferenceAdvancedMaterialSummaryPayload(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetDouble(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    ParseTimestamp(reader.GetString(9))));
            }
        }

        var pageNumber = page.PageSize <= 0 ? 1 : (offset / page.PageSize) + 1;
        var totalPages = page.PageSize <= 0 ? 0 : (int)((total + page.PageSize - 1) / page.PageSize);
        var nextOffset = offset + items.Count;
        var hasMore = items.Count > 0 && nextOffset < total;
        return new PageResultPayload<ReferenceAdvancedMaterialSummaryPayload>(
            items,
            total,
            pageNumber,
            page.PageSize,
            totalPages,
            hasMore ? EncodeOffsetCursor(nextOffset) : null,
            hasMore);
    }

    public async ValueTask<ReferenceAdvancedMaterialDetailPayload?> GetAsync(
        GetReferenceAdvancedMaterialDetailPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentException("anchor_id must be positive.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.MaterialId))
        {
            throw new ArgumentException("material_id is required.", nameof(input));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);

        string? evidenceJson = null;
        string extractorVersion;
        double confidence;
        string reviewState;
        string validityState;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT layer, family, feature_key, source_ref, value_text, value_json,
                       rationale_json, boundary_json, transfer_template, transfer_slots_json,
                       evidence_refs_json, confidence, review_state, validity_state,
                       extractor_version, created_at, updated_at
                FROM reference_advanced_materials
                WHERE anchor_id = $anchor_id AND material_id = $material_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$anchor_id", input.AnchorId);
            command.Parameters.AddWithValue("$material_id", input.MaterialId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var layer = reader.GetString(0);
            var family = reader.GetString(1);
            var featureKey = reader.GetString(2);
            var sourceRef = reader.GetString(3);
            var valueText = reader.IsDBNull(4) ? null : reader.GetString(4);
            var valueJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            var rationaleJson = reader.IsDBNull(6) ? null : reader.GetString(6);
            var boundaryJson = reader.IsDBNull(7) ? null : reader.GetString(7);
            var transferTemplate = reader.IsDBNull(8) ? null : reader.GetString(8);
            var transferSlotsJson = reader.IsDBNull(9) ? null : reader.GetString(9);
            evidenceJson = reader.GetString(10);
            confidence = reader.GetDouble(11);
            reviewState = reader.GetString(12);
            validityState = reader.GetString(13);
            extractorVersion = reader.GetString(14);
            createdAt = ParseTimestamp(reader.GetString(15));
            updatedAt = ParseTimestamp(reader.GetString(16));

            var evidence = await ReadEvidenceAsync(connection, evidenceJson, cancellationToken);

            return new ReferenceAdvancedMaterialDetailPayload(
                input.MaterialId,
                input.AnchorId,
                layer,
                family,
                featureKey,
                sourceRef,
                valueText,
                valueJson,
                rationaleJson,
                boundaryJson,
                transferTemplate,
                transferSlotsJson,
                confidence,
                reviewState,
                validityState,
                extractorVersion,
                evidence,
                createdAt,
                updatedAt);
        }
    }

    public async ValueTask<ReferenceAdvancedMaterialReviewResultPayload> ReviewAsync(
        ReviewReferenceAdvancedMaterialPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentException("anchor_id must be positive.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.MaterialId))
        {
            throw new ArgumentException("material_id is required.", nameof(input));
        }

        var reviewState = input.Decision switch
        {
            ReferenceAdvancedMaterialReviewDecisions.Confirm => ReferenceAdvancedMaterialReviewStates.Confirmed,
            ReferenceAdvancedMaterialReviewDecisions.Reject => ReferenceAdvancedMaterialReviewStates.Rejected,
            _ => throw new ArgumentException(
                $"decision must be one of {string.Join(", ", ReferenceAdvancedMaterialReviewDecisions.All)}.",
                nameof(input))
        };

        var reviewedAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE reference_advanced_materials
                SET review_state = $review_state, updated_at = $updated_at
                WHERE anchor_id = $anchor_id AND material_id = $material_id;
                """;
            command.Parameters.AddWithValue("$review_state", reviewState);
            command.Parameters.AddWithValue("$updated_at", FormatTimestamp(reviewedAt));
            command.Parameters.AddWithValue("$anchor_id", input.AnchorId);
            command.Parameters.AddWithValue("$material_id", input.MaterialId);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                throw new KeyNotFoundException($"Advanced material '{input.MaterialId}' was not found.");
            }
        }

        return new ReferenceAdvancedMaterialReviewResultPayload(input.MaterialId, reviewState, reviewedAt);
    }

    private static async ValueTask<IReadOnlyList<ReferenceAdvancedMaterialEvidencePayload>> ReadEvidenceAsync(
        SqliteConnection connection,
        string evidenceJson,
        CancellationToken cancellationToken)
    {
        var refs = ParseEvidenceRefs(evidenceJson);
        if (refs.Count == 0)
        {
            return [];
        }

        var results = new List<ReferenceAdvancedMaterialEvidencePayload>(refs.Count);
        foreach (var reference in refs)
        {
            string? text = null;
            if (!string.IsNullOrWhiteSpace(reference.NodeId) &&
                reference.StartOffset >= 0 &&
                reference.EndOffset > reference.StartOffset)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT substr(text, $start + 1, $length)
                    FROM reference_text_nodes
                    WHERE node_id = $node_id
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$start", reference.StartOffset);
                command.Parameters.AddWithValue("$length", reference.EndOffset - reference.StartOffset);
                command.Parameters.AddWithValue("$node_id", reference.NodeId);
                text = await command.ExecuteScalarAsync(cancellationToken) as string;
            }

            results.Add(new ReferenceAdvancedMaterialEvidencePayload(
                reference.NodeId,
                reference.MaterialId,
                reference.StartOffset,
                reference.EndOffset,
                text));
        }

        return results;
    }

    private static IReadOnlyList<EvidenceRef> ParseEvidenceRefs(string evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<EvidenceRef>>(evidenceJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static (string WhereSql, List<(string Name, object Value)> Parameters) BuildListFilter(
        ListReferenceAdvancedMaterialsPayload input)
    {
        var predicates = new List<string> { "anchor_id = $anchor_id" };
        var parameters = new List<(string, object)> { ("$anchor_id", input.AnchorId) };

        if (!string.IsNullOrWhiteSpace(input.Family))
        {
            predicates.Add("family = $family");
            parameters.Add(("$family", input.Family));
        }

        if (!string.IsNullOrWhiteSpace(input.Layer))
        {
            predicates.Add("layer = $layer");
            parameters.Add(("$layer", input.Layer));
        }

        if (!string.IsNullOrWhiteSpace(input.ReviewState))
        {
            predicates.Add("review_state = $review_state");
            parameters.Add(("$review_state", input.ReviewState));
        }

        if (!input.IncludeSuperseded)
        {
            predicates.Add("validity_state = 'active'");
        }

        return ("WHERE " + string.Join(" AND ", predicates), parameters);
    }

    private static async ValueTask<long> CountAsync(
        SqliteConnection connection,
        string whereSql,
        List<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM reference_advanced_materials {whereSql};";
        AddParameters(command, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static string BuildOrderBy(NormalizedPageRequest page)
    {
        var columns = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["created_at"] = "created_at",
            ["confidence"] = "confidence",
            ["family"] = "family",
            ["layer"] = "layer",
            ["material_id"] = "material_id"
        };
        var direction = string.Equals(page.SortDir, "asc", StringComparison.Ordinal) ? "ASC" : "DESC";
        var parts = new List<string>();
        foreach (var field in page.StableSortFields)
        {
            if (columns.TryGetValue(field, out var column))
            {
                parts.Add(column + " " + direction);
            }
        }

        if (parts.Count == 0)
        {
            parts.Add("created_at DESC");
            parts.Add("material_id ASC");
        }

        return string.Join(", ", parts);
    }

    private static void AddParameters(SqliteCommand command, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static int DecodeOffsetCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        return int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) && offset >= 0
            ? offset
            : 0;
    }

    private static string EncodeOffsetCursor(int offset) => offset.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static void ValidateOptionalFamily(string? family)
    {
        if (!string.IsNullOrWhiteSpace(family) && !ReferenceAdvancedMaterialFamilies.IsSupported(family))
        {
            throw new ArgumentException(
                $"family must be one of {string.Join(", ", ReferenceAdvancedMaterialFamilies.All)}.",
                nameof(family));
        }
    }

    private static void ValidateOptionalLayer(string? layer)
    {
        if (!string.IsNullOrWhiteSpace(layer) && !ReferenceAdvancedMaterialLayers.IsSupported(layer))
        {
            throw new ArgumentException(
                $"layer must be one of {string.Join(", ", ReferenceAdvancedMaterialLayers.All)}.",
                nameof(layer));
        }
    }

    private static void ValidateOptionalReviewState(string? reviewState)
    {
        if (!string.IsNullOrWhiteSpace(reviewState) &&
            !ReferenceAdvancedMaterialReviewStates.All.Contains(reviewState, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"review_state must be one of {string.Join(", ", ReferenceAdvancedMaterialReviewStates.All)}.",
                nameof(reviewState));
        }
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var databasePath = await _pathResolver.ResolveAsync(cancellationToken);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private sealed record EvidenceRef(
        [property: JsonPropertyName("node_id")] string NodeId,
        [property: JsonPropertyName("material_id")] string? MaterialId,
        [property: JsonPropertyName("start_offset")] int StartOffset,
        [property: JsonPropertyName("end_offset")] int EndOffset);
}
