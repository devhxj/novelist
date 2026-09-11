using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Novelist.Infrastructure.App;

/// <summary>
/// 把某个代次的材料化素材投影进素材库（reference_materials）。
/// 库视图——总览覆盖度、素材检索、风格画像、语料包——全部只读素材库，而材料化只写代次表；
/// 没有这一步，材料化跑得再成功，界面上也永远是 0 条（2026-09-11 的"语料处理好了但看不到"）。
/// 晋升（新代次）与启动恢复（升级前已完成、从未投影过的代次）共用这一份实现，避免两处漂移。
/// </summary>
internal static class SqliteReferenceMaterializationLibraryProjection
{
    internal const string ExtractorVersion = "reference-materialization-v1";

    /// <summary>
    /// 归档上一代的素材库行：库视图默认只读"活跃"（archived_at IS NULL），
    /// 不归档会让新旧两代素材同时可见。只动材料化写入的行（materialization_generation_id 非空），
    /// 锚点构建管线写入的素材不受影响。
    /// </summary>
    internal static async ValueTask<int> ArchivePreviousGenerationsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long anchorId,
        string generationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reference_materials
            SET archived_at = $archived_at
            WHERE anchor_id = $anchor_id
              AND materialization_generation_id IS NOT NULL
              AND materialization_generation_id <> $generation_id
              AND archived_at IS NULL;
            """;
        command.Parameters.AddWithValue("$archived_at", now.ToString("O"));
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 投影一代素材：幂等（按 material_id upsert），找不到所属章节片段的素材跳过，
    /// 避免写入非法外键行。返回写入/更新的行数。
    /// </summary>
    internal static async ValueTask<int> ProjectGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long anchorId,
        string generationId,
        CancellationToken cancellationToken)
    {
        var materials = await ReadGenerationMaterialsAsync(connection, transaction, anchorId, generationId, cancellationToken);
        var projected = 0;
        foreach (var material in materials)
        {
            var segmentId = await ResolveChapterSegmentAsync(connection, transaction, anchorId, material.NodeId, cancellationToken);
            if (segmentId is null)
            {
                continue;
            }

            await UpsertLibraryMaterialAsync(connection, transaction, anchorId, generationId, material, segmentId, cancellationToken);
            projected++;
        }

        return projected;
    }

    internal static async ValueTask<int> CountProjectedRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long anchorId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materials
            WHERE anchor_id = $anchor_id
              AND materialization_generation_id = $generation_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record GenerationMaterial(
        string MaterialId,
        string MaterialType,
        string Text,
        string TextHash,
        double Confidence,
        string TagsJson,
        string NodeId);

    private static async ValueTask<IReadOnlyList<GenerationMaterial>> ReadGenerationMaterialsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long anchorId,
        string generationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT material.material_id, material.material_type, material.text, material.text_hash,
                   material.confidence, material.tags_json,
                   (SELECT link.node_id
                    FROM reference_materialization_material_nodes link
                    WHERE link.material_id = material.material_id
                    ORDER BY link.ordinal
                    LIMIT 1)
            FROM reference_materialization_materials material
            WHERE material.anchor_id = $anchor_id
              AND material.generation_id = $generation_id
            ORDER BY material.material_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var materials = new List<GenerationMaterial>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.IsDBNull(6) ? null : reader.GetString(6);
            if (string.IsNullOrWhiteSpace(nodeId))
            {
                continue;
            }

            materials.Add(new GenerationMaterial(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.GetString(5),
                nodeId));
        }

        return materials;
    }

    // 素材的来源片段就是**章节粒度**：材料化的证据按章挂靠，库视图（风格画像、片段明细）用
    // source_segment_id 连接章节片段取章节偏移与文本哈希。这是契约，不是精度折衷——不要改成
    // 句级片段：句级片段只在锚点构建管线里存在，与代次素材对不上号。
    // 找不到章节片段的素材跳过该行（代次表与检索仍完整）。
    private static async ValueTask<string?> ResolveChapterSegmentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long anchorId,
        string nodeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT (
                SELECT segment.segment_id
                FROM reference_source_segments segment
                JOIN reference_text_nodes node ON node.node_id = $node_id
                WHERE segment.anchor_id = $anchor_id
                  AND segment.chapter_index = node.chapter_index
                  AND segment.segment_type = 'chapter'
                ORDER BY segment.segment_index
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        var located = await command.ExecuteScalarAsync(cancellationToken);
        return located is string value && !string.IsNullOrWhiteSpace(value) ? value : null;
    }

    private static async ValueTask UpsertLibraryMaterialAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long anchorId,
        string generationId,
        GenerationMaterial material,
        string segmentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_materials (
              material_id, anchor_id, source_segment_id, material_type,
              function_tag, emotion_tag, scene_tag, pov_tag, technique_tag,
              function_confidence, emotion_confidence, pov_confidence,
              text, source_hash, extractor_version, user_verified, created_at, archived_at, node_id,
              materialization_generation_id)
            VALUES (
              $material_id, $anchor_id, $source_segment_id, $material_type,
              $function_tag, $emotion_tag, $scene_tag, $pov_tag, $technique_tag,
              $confidence, $confidence, $confidence,
              $text, $source_hash, $extractor_version, 0, $created_at, NULL, $node_id,
              $generation_id)
            ON CONFLICT(material_id) DO UPDATE SET
              anchor_id = excluded.anchor_id,
              source_segment_id = excluded.source_segment_id,
              material_type = excluded.material_type,
              function_tag = excluded.function_tag,
              emotion_tag = excluded.emotion_tag,
              scene_tag = excluded.scene_tag,
              pov_tag = excluded.pov_tag,
              technique_tag = excluded.technique_tag,
              function_confidence = excluded.function_confidence,
              emotion_confidence = excluded.emotion_confidence,
              pov_confidence = excluded.pov_confidence,
              text = excluded.text,
              source_hash = excluded.source_hash,
              extractor_version = excluded.extractor_version,
              created_at = excluded.created_at,
              archived_at = NULL,
              node_id = excluded.node_id,
              materialization_generation_id = excluded.materialization_generation_id;
            """;
        command.Parameters.AddWithValue("$material_id", material.MaterialId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$source_segment_id", segmentId);
        command.Parameters.AddWithValue("$material_type", material.MaterialType);
        command.Parameters.AddWithValue("$function_tag", FirstTag(material.TagsJson, "narrative_functions"));
        command.Parameters.AddWithValue("$emotion_tag", FirstTag(material.TagsJson, "emotion_mechanics"));
        command.Parameters.AddWithValue("$scene_tag", FirstTag(material.TagsJson, "scene_beat_roles"));
        command.Parameters.AddWithValue("$pov_tag", FirstTag(material.TagsJson, "pov"));
        command.Parameters.AddWithValue("$technique_tag", FirstTag(material.TagsJson, "techniques"));
        command.Parameters.AddWithValue("$confidence", material.Confidence);
        command.Parameters.AddWithValue("$text", material.Text);
        command.Parameters.AddWithValue("$source_hash", material.TextHash);
        command.Parameters.AddWithValue("$extractor_version", ExtractorVersion);
        command.Parameters.AddWithValue("$created_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$node_id", material.NodeId);
        command.Parameters.AddWithValue("$generation_id", generationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 单标签列取素材标签数组的首项（词表与库视图同源）；三个置信度取候选的整体置信度：
    /// 同一份判定产出的标签共享同一个置信度，不编造逐标签的数值。
    /// </summary>
    private static string FirstTag(string tagsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(tagsJson);
            if (!document.RootElement.TryGetProperty(propertyName, out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    return item.GetString()!.Trim();
                }
            }
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return string.Empty;
    }
}
