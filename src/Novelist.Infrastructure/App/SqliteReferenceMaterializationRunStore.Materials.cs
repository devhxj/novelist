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

        var normalizedQuery = NormalizeMaterialQuery(query);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var total = await CountActiveMaterialsAsync(connection, anchorId, normalizedQuery, cancellationToken);
        var offset = checked((page - 1) * size);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material.material_id, material.anchor_id, material.generation_id,
                   material.material_type, material.text, material.tags_json
            FROM reference_materialization_materials material
            JOIN reference_anchor_materialization_state state
              ON state.anchor_id = material.anchor_id
             AND state.active_generation_id = material.generation_id
            WHERE material.anchor_id = $anchor_id
              AND ($query = ''
                   OR instr(material.text, $query) > 0
                   OR instr(material.description, $query) > 0)
            ORDER BY material.chapter_index, material.ordinal
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
                1,
                1,
                new ReferenceMaterializationMaterialTagsPayload(
                    ParseMaterialTags(reader.GetString(5)),
                    [],
                    [],
                    []),
                []));
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
              AND ($query = ''
                   OR instr(material.text, $query) > 0
                   OR instr(material.description, $query) > 0);
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$query", query);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static IReadOnlyList<string> ParseMaterialTags(string value)
    {
        try
        {
            var tags = JsonSerializer.Deserialize<string[]>(value);
            if (tags is null ||
                tags.Length > 16 ||
                tags.Any(tag => string.IsNullOrWhiteSpace(tag)))
            {
                throw new InvalidOperationException("Stored material tags are invalid.");
            }

            return tags;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored material tags are invalid.", exception);
        }
    }

    private static string NormalizeMaterialQuery(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Material list query is invalid.", nameof(value));
        }

        return normalized;
    }
}
