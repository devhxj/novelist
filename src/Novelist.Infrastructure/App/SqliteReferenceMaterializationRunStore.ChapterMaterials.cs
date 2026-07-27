using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    public async ValueTask<PageResultPayload<ReferenceMaterialListItemPayload>> ListChapterMaterialsAsync(
        string runId,
        int chapterIndex,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        if (chapterIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chapterIndex), "Chapter index must be positive.");
        }

        if (page <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be positive.");
        }

        if (size is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Page size must be between 1 and 100.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        if (!await ChapterExistsAsync(connection, normalizedRunId, chapterIndex, cancellationToken))
        {
            throw new ArgumentException("Materialization chapter does not exist.", nameof(chapterIndex));
        }

        var total = await CountChapterMaterialsAsync(connection, normalizedRunId, chapterIndex, cancellationToken);
        var offset = checked((page - 1) * size);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id, generation_id, anchor_id, chapter_index, ordinal,
                   text, metadata_json, text_hash
            FROM reference_materials
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index
            ORDER BY ordinal ASC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$run_id", normalizedRunId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        command.Parameters.AddWithValue("$limit", size);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<ReferenceMaterialListItemPayload>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new ReferenceMaterialListItemPayload(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5),
                ToPayload(ParseMetadata(reader.GetString(6))),
                reader.GetString(7)));
        }

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
        return new PageResultPayload<ReferenceMaterialListItemPayload>(items, total, page, size, totalPages);
    }

    private static async ValueTask<bool> ChapterExistsAsync(
        SqliteConnection connection,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1
              FROM reference_materialization_chapter_progress
              WHERE run_id = $run_id
                AND chapter_index = $chapter_index);
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async ValueTask<int> CountChapterMaterialsAsync(
        SqliteConnection connection,
        string runId,
        int chapterIndex,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materials
            WHERE run_id = $run_id
              AND chapter_index = $chapter_index;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$chapter_index", chapterIndex);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
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

    private static ReferenceMaterialMetadataPayload ToPayload(ReferenceMaterialMetadata metadata) =>
        new(
            new ReferenceMaterialSourceSpanPayload(metadata.SourceSpan.StartLine, metadata.SourceSpan.EndLine),
            metadata.SourceKind,
            metadata.Entities.Select(entity => new ReferenceMaterialEntityPayload(entity.Name, entity.Kind)).ToArray(),
            metadata.Setting is null ? null : new ReferenceMaterialSettingPayload(
                metadata.Setting.Location,
                metadata.Setting.Time,
                metadata.Setting.Environment),
            metadata.Perspective is null ? null : new ReferenceMaterialPerspectivePayload(
                metadata.Perspective.Mode,
                metadata.Perspective.FocusEntity),
            metadata.Event,
            metadata.Facts.Select(fact => new ReferenceMaterialFactPayload(fact.Content, fact.Subject)).ToArray(),
            metadata.Causality is null ? null : new ReferenceMaterialCausalityPayload(
                metadata.Causality.Cause,
                metadata.Causality.Consequence),
            metadata.StateChanges.Select(change => new ReferenceMaterialStateChangePayload(
                change.Subject,
                change.Before,
                change.After)).ToArray(),
            metadata.CharacterDynamics,
            metadata.Conflict is null ? null : new ReferenceMaterialConflictPayload(
                metadata.Conflict.Pressure,
                metadata.Conflict.Cost),
            metadata.Information is null ? null : new ReferenceMaterialInformationPayload(
                metadata.Information.Role,
                metadata.Information.Content),
            metadata.Emotion is null ? null : new ReferenceMaterialEmotionPayload(
                metadata.Emotion.Tone,
                metadata.Emotion.Subtext),
            metadata.NarrativeFunctions,
            metadata.Foreshadowing.Select(item => new ReferenceMaterialForeshadowingPayload(item.Phase, item.Target)).ToArray(),
            metadata.Motifs,
            metadata.ExpressionTechniques,
            metadata.ReuseHint);
}
