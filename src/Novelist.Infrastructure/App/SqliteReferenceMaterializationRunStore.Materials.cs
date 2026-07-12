using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<PageResultPayload<ReferenceMaterializationMaterialPayload>> ListActiveMaterialsAsync(
        long anchorId,
        int page,
        int size,
        string? query,
        CancellationToken cancellationToken)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId));
        }

        if (page <= 0 || size is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Material list pagination is invalid.");
        }

        var normalizedQuery = NormalizeQuery(query);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var total = await CountActiveMaterialsAsync(connection, anchorId, normalizedQuery, cancellationToken);
        var offset = checked((page - 1) * size);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material.material_id, material.anchor_id, material.generation_id, material.material_type,
                   material.text, material.quality_score, material.confidence, material.tags_json, material.reason_codes_json
            FROM reference_materialization_materials material
            JOIN reference_anchor_materialization_state state
              ON state.anchor_id = material.anchor_id
             AND state.active_generation_id = material.generation_id
            WHERE material.anchor_id = $anchor_id
              AND ($query = '' OR instr(material.text, $query) > 0)
            ORDER BY material.quality_score DESC, material.confidence DESC, material.material_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$query", normalizedQuery);
        command.Parameters.AddWithValue("$limit", size);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ReferenceMaterializationMaterialPayload>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ReferenceMaterializationMaterialPayload(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                ParseTags(reader.GetString(7)),
                ParseStringArray(reader.GetString(8), 12)));
        }

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
        return new PageResultPayload<ReferenceMaterializationMaterialPayload>(items, total, page, size, totalPages);
    }

    private static async ValueTask<int> CountActiveMaterialsAsync(
        SqliteConnection connection,
        long anchorId,
        string query,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materialization_materials material
            JOIN reference_anchor_materialization_state state
              ON state.anchor_id = material.anchor_id
             AND state.active_generation_id = material.generation_id
            WHERE material.anchor_id = $anchor_id
              AND ($query = '' OR instr(material.text, $query) > 0);
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$query", query);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
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

    private static IReadOnlyList<string> ReadArray(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString() ?? string.Empty)
                .Where(value => value.Length > 0)
                .Take(12)
                .ToArray()
            : [];
    }

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

    private static string NormalizeQuery(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Material search query is invalid.", nameof(value));
        }

        return normalized;
    }
}
