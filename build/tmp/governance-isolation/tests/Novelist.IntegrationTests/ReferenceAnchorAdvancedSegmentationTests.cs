using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceAnchorAdvancedSegmentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-advanced-segments", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportCreatesParentedMultiScaleSegmentsAndMaterials()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("高级分段测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "advanced.md",
            """
            # 第一章 雨夜

            雨声压低了街道，林岚在门口停住，指节慢慢发紧。

            她说：“你终于来了。”他没有回答，只把钥匙放回桌面。

            后来灯光暗下去，他终于明白那封信不是威胁，而是答案。

            门外忽然安静下来？
            """);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "高级分段参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var segments = await ReadSegmentsAsync(options, anchor.AnchorId);
        var byId = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        Assert.Contains(segments, segment => segment.SegmentType == "scene");
        Assert.Contains(segments, segment => segment.SegmentType == "beat");
        Assert.Contains(segments, segment => segment.SegmentType == "dialogue_exchange");
        Assert.Contains(segments, segment => segment.SegmentType == "action_afterbeat");
        Assert.Contains(segments, segment => segment.SegmentType == "image_motif");
        Assert.Contains(segments, segment => segment.SegmentType == "hook");
        Assert.Contains(segments, segment => segment.SegmentType == "payoff");
        Assert.Contains(segments, segment => segment.SegmentType == "transition");

        foreach (var segment in segments.Where(segment => segment.SegmentType is not "chapter"))
        {
            Assert.False(string.IsNullOrWhiteSpace(segment.ParentSegmentId));
            Assert.True(byId.ContainsKey(segment.ParentSegmentId), $"Missing parent for {segment.SegmentType}:{segment.SegmentId}");
            Assert.True(segment.EndOffset > segment.StartOffset);
            Assert.False(string.IsNullOrWhiteSpace(segment.TextHash));
        }

        Assert.All(segments.Where(segment => segment.SegmentType == "scene"), segment =>
            Assert.Equal("chapter", byId[segment.ParentSegmentId].SegmentType));
        Assert.All(segments.Where(segment => segment.SegmentType == "beat"), segment =>
            Assert.Equal("scene", byId[segment.ParentSegmentId].SegmentType));
        Assert.All(
            segments.Where(segment => segment.SegmentType is "dialogue_exchange" or "action_afterbeat" or "image_motif" or "hook" or "payoff" or "transition"),
            segment => Assert.Equal("beat", byId[segment.ParentSegmentId].SegmentType));

        var materials = await ReadMaterialsAsync(options, anchor.AnchorId);
        Assert.Contains(materials, material => material.MaterialType == ReferenceMaterialTypes.Scene);
        Assert.Contains(materials, material => material.MaterialType == ReferenceMaterialTypes.Beat);
        Assert.Contains(materials, material => material.MaterialType == ReferenceMaterialTypes.DialogueExchange);
        Assert.Contains(materials, material => material.MaterialType == ReferenceMaterialTypes.ActionAfterbeat);
        Assert.Contains(materials, material => material.MaterialType == ReferenceMaterialTypes.ImageMotif);
        Assert.Contains(materials, material => material.MaterialType == ReferenceMaterialTypes.Hook);
        Assert.Contains(materials, material => material.MaterialType == ReferenceMaterialTypes.Payoff);
        Assert.Contains(materials, material => material.MaterialType == ReferenceMaterialTypes.Transition);
        Assert.All(materials, material =>
        {
            var segment = byId[material.SourceSegmentId];
            Assert.Equal(segment.TextHash, material.SourceHash);
        });
    }

    [Fact]
    public async Task RebuildPreservesCoreAndAdvancedSegmentIdsWhenSourceIsUnchanged()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("高级分段稳定测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "stable.md",
            """
            # 第一章

            第一句压住节奏。

            她说：“先别开门。”他停了一下，掌心贴着冰冷的门把。

            直到灯光熄灭，他才明白答案藏在钥匙背面。
            """);
        var service = new SqliteReferenceAnchorService(options, novels);
        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "稳定参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var beforeSegments = await ReadSegmentsAsync(options, anchor.AnchorId);
        var beforeMaterials = await ReadMaterialsAsync(options, anchor.AnchorId);
        var coreBefore = beforeSegments
            .Where(segment => segment.SegmentType is "chapter" or "paragraph" or "sentence")
            .Select(segment => segment.Signature)
            .ToArray();
        var advancedBefore = beforeSegments
            .Where(segment => segment.SegmentType is not "chapter" and not "paragraph" and not "sentence")
            .Select(segment => segment.Signature)
            .ToArray();
        var materialBefore = beforeMaterials.Select(material => material.Signature).ToArray();

        await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);

        var afterSegments = await ReadSegmentsAsync(options, anchor.AnchorId);
        var afterMaterials = await ReadMaterialsAsync(options, anchor.AnchorId);
        Assert.Equal(coreBefore, afterSegments
            .Where(segment => segment.SegmentType is "chapter" or "paragraph" or "sentence")
            .Select(segment => segment.Signature)
            .ToArray());
        Assert.Equal(advancedBefore, afterSegments
            .Where(segment => segment.SegmentType is not "chapter" and not "paragraph" and not "sentence")
            .Select(segment => segment.Signature)
            .ToArray());
        Assert.Equal(materialBefore, afterMaterials.Select(material => material.Signature).ToArray());
    }

    [Fact]
    public async Task TenMbSourceImportsAdvancedMaterialsAndSearchRemainsPaged()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("大语料高级分段测试", "", ""), CancellationToken.None);
        var source = BuildLargeChineseSource(minUtf8Bytes: 10_000_000);
        Assert.InRange(Encoding.UTF8.GetByteCount(source), 10_000_000, 19_500_000);
        var sourcePath = CreateSourceFile("large-advanced.md", source);
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "10MB高级参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);

        var status = await service.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, status.Status);
        Assert.True(status.SourceSegmentCount > 500);
        Assert.True(status.MaterialCount > 500);

        var firstPage = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Hook],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10),
            CancellationToken.None);
        var secondPage = await service.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "",
                [ReferenceMaterialTypes.Hook],
                [],
                [],
                [],
                [],
                Page: 2,
                Size: 10),
            CancellationToken.None);

        Assert.True(firstPage.Total > 10);
        Assert.Equal(10, firstPage.Items.Count);
        Assert.Equal(10, secondPage.Items.Count);
        Assert.DoesNotContain(firstPage.Items.Select(item => item.MaterialId), id =>
            secondPage.Items.Any(item => item.MaterialId == id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private string CreateSourceFile(string fileName, string content)
    {
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static string BuildLargeChineseSource(int minUtf8Bytes)
    {
        var paragraph = string.Concat(
            Enumerable.Repeat("雨声压低了整条街的呼吸，门外忽然安静下来，他终于明白答案就在灯光背后", 650)) + "？";
        var builder = new StringBuilder("# 第一章 大雨\n\n");
        while (Encoding.UTF8.GetByteCount(builder.ToString()) < minUtf8Bytes)
        {
            builder.Append(paragraph);
            builder.Append("\n\n");
        }

        return builder.ToString();
    }

    private static async ValueTask<IReadOnlyList<SegmentRow>> ReadSegmentsAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, anchor_id, segment_type, segment_index, parent_segment_id,
                   start_offset, end_offset, text_hash, text
            FROM reference_source_segments
            WHERE anchor_id = $anchor_id
            ORDER BY segment_type ASC, segment_index ASC, segment_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<SegmentRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new SegmentRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<MaterialRow>> ReadMaterialsAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id, anchor_id, source_segment_id, material_type, function_tag,
                   emotion_tag, pov_tag, technique_tag, source_hash
            FROM reference_materials
            WHERE anchor_id = $anchor_id
            ORDER BY material_type ASC, material_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<MaterialRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new MaterialRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return rows;
    }

    private static async ValueTask<SqliteConnection> OpenReferenceConnectionAsync(AppInitializationOptions options)
    {
        var databasePath = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private sealed record SegmentRow(
        string SegmentId,
        long AnchorId,
        string SegmentType,
        int SegmentIndex,
        string ParentSegmentId,
        int StartOffset,
        int EndOffset,
        string TextHash,
        string Text)
    {
        public string Signature => string.Join('|', SegmentId, AnchorId, SegmentType, SegmentIndex, ParentSegmentId, StartOffset, EndOffset, TextHash);
    }

    private sealed record MaterialRow(
        string MaterialId,
        long AnchorId,
        string SourceSegmentId,
        string MaterialType,
        string FunctionTag,
        string EmotionTag,
        string PovTag,
        string TechniqueTag,
        string SourceHash)
    {
        public string Signature => string.Join('|', MaterialId, AnchorId, SourceSegmentId, MaterialType, FunctionTag, EmotionTag, PovTag, TechniqueTag, SourceHash);
    }
}
