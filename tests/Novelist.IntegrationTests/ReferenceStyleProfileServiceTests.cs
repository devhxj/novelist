using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceStyleProfileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-style-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BuildStyleProfileCreatesDeterministicBaselineWithoutCopyingSourceText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("风格画像测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "style-source.md",
            """
            # 第一章 雨夜

            雨声压低了整条街的呼吸。林岚在门口停了很久，指节慢慢发紧。

            她说：“你终于来了。”

            后来灯光暗下去，他没有回答，只把钥匙放回桌面。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "雨夜参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                Visibility: ReferenceCorpusVisibilities.Workspace,
                SourceTrust: ReferenceSourceTrustLevels.UserVerified),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);

        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "雨夜克制风格",
                "deterministic baseline",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.True(profile.ProfileId > 0);
        Assert.Equal(ReferenceStyleProfileStatuses.Active, profile.Status);
        Assert.Equal("reference-style-deterministic-v1", profile.AnalyzerVersion);
        Assert.Equal(ReferenceStyleFeatureSchemaVersions.V1, profile.FeatureSchemaVersion);
        Assert.Equal(ReferenceStyleAnalyzerSources.DeterministicBaseline, profile.AnalyzerSource);
        Assert.Equal([anchor.AnchorId], profile.SourceAnchorIds);
        Assert.Equal([anchor.SourceFileHash], profile.SourceHashes);
        Assert.InRange(profile.AggregateConfidence, 0.5, 1.0);
        Assert.NotEmpty(profile.EvidenceSpans);
        Assert.Contains(profile.Features.NumericFeatures, feature => feature.FeatureKey == "average_sentence_chars" && feature.Value > 0);
        Assert.Contains(profile.Features.NumericFeatures, feature => feature.FeatureKey == "dialogue_ratio" && feature.Value > 0);
        Assert.Contains(profile.Features.DistributionFeatures, feature => feature.FeatureKey == "sentence_length_distribution");
        Assert.Contains(profile.Features.CategoricalFeatures, feature => feature.FeatureKey == "dominant_technique");
        Assert.All(profile.EvidenceSpans, evidence =>
        {
            Assert.Equal(profile.ProfileId, evidence.ProfileId);
            Assert.Equal(anchor.AnchorId, evidence.AnchorId);
            Assert.False(string.IsNullOrWhiteSpace(evidence.SourceSegmentId));
            Assert.False(string.IsNullOrWhiteSpace(evidence.TextHash));
            Assert.True(evidence.EndOffset >= evidence.StartOffset);
        });

        var reloaded = await styleService.GetStyleProfileAsync(novel.Id, profile.ProfileId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(profile.ProfileId, reloaded.ProfileId);
        Assert.Equal(profile.EvidenceSpans.Count, reloaded.EvidenceSpans.Count);

        var persisted = await ReadPersistedStyleProfileAsync(options, profile.ProfileId);
        Assert.DoesNotContain("雨声压低", persisted.FeatureVectorJson, StringComparison.Ordinal);
        Assert.DoesNotContain("你终于来了", persisted.FeatureVectorJson, StringComparison.Ordinal);
        Assert.DoesNotContain("text", persisted.EvidenceColumns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildStyleProfileMigratesPrePhase14DatabaseWithoutChangingReferenceIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像迁移测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "migration-source.md",
            """
            # 第一章

            第一句压住节奏。

            第二句转入沉默。第三句留下钩子？
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "迁移参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var segmentsBefore = await ReadSegmentSignaturesAsync(options, anchor.AnchorId);
        var materialsBefore = await ReadMaterialSignaturesAsync(options, anchor.AnchorId);

        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "迁移画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.Equal(segmentsBefore, await ReadSegmentSignaturesAsync(options, anchor.AnchorId));
        Assert.Equal(materialsBefore, await ReadMaterialSignaturesAsync(options, anchor.AnchorId));
        Assert.True(await TableExistsAsync(options, "reference_style_profiles"));
        Assert.True(await TableExistsAsync(options, "reference_style_profile_evidence"));
        Assert.True(await TableExistsAsync(options, "reference_material_style_tags"));
    }

    [Fact]
    public async Task RebuildingStyleProfileFromSameSourceProducesStableFeatureValues()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像复现测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "repro-source.md",
            """
            # 第一章

            她说：“先别开门。”

            他心里明白，雨声已经压住了脚步。门外忽然安静下来？
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "复现参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);

        var first = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "复现画像 A",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var second = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "复现画像 B",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.Equal(first.SourceHashes, second.SourceHashes);
        Assert.Equal(first.AnalyzerVersion, second.AnalyzerVersion);
        Assert.Equal(FeatureSignature(first.Features), FeatureSignature(second.Features));
        Assert.NotEqual(first.ProfileId, second.ProfileId);
        Assert.NotEqual(
            first.EvidenceSpans.Select(evidence => evidence.EvidenceId).Order(StringComparer.Ordinal),
            second.EvidenceSpans.Select(evidence => evidence.EvidenceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ArchivingOrHardDeletingMaterialDoesNotSilentlyOrphanStyleEvidence()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像溯源测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "provenance-source.md",
            """
            # 第一章

            她说：“先别开门。”

            他停了一下，指尖贴着冰冷的门把。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "溯源参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "溯源画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var evidenceMaterialId = profile.EvidenceSpans
            .Select(evidence => evidence.MaterialId)
            .FirstOrDefault(materialId => !string.IsNullOrWhiteSpace(materialId));
        Assert.False(string.IsNullOrWhiteSpace(evidenceMaterialId));

        await anchorService.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [evidenceMaterialId!]),
            CancellationToken.None);

        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, evidenceMaterialId!));
        var afterArchive = await styleService.GetStyleProfileAsync(novel.Id, profile.ProfileId, CancellationToken.None);
        Assert.NotNull(afterArchive);
        Assert.Contains(afterArchive.EvidenceSpans, evidence => evidence.MaterialId == evidenceMaterialId);

        await Assert.ThrowsAsync<SqliteException>(async () => await HardDeleteMaterialAsync(options, evidenceMaterialId!));
        var afterHardDeleteAttempt = await styleService.GetStyleProfileAsync(novel.Id, profile.ProfileId, CancellationToken.None);
        Assert.NotNull(afterHardDeleteAttempt);
        Assert.Contains(afterHardDeleteAttempt.EvidenceSpans, evidence => evidence.MaterialId == evidenceMaterialId);
    }

    [Fact]
    public async Task SearchMaterialsUsesStyleProfileEvidenceWithoutCrossNovelBypass()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("风格检索测试", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("其他风格画像", "", ""), CancellationToken.None);
        var dialogueSourcePath = CreateSourceFile(
            "style-dialogue.md",
            """
            # 第一章

            她说：“门口别停。”雨声压住门口。
            """);
        var neutralSourcePath = CreateSourceFile(
            "style-neutral.md",
            """
            # 第一章

            他在门口停住。雨声落在门口。
            """);
        var otherSourcePath = CreateSourceFile(
            "style-other.md",
            """
            # 第一章

            她说：“门口还有人。”后来灯光暗下去。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var dialogueAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "对话风格", null, dialogueSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var neutralAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "叙述风格", null, neutralSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var otherAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(otherNovel.Id, "其他小说风格", null, otherSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "对话证据画像",
                "",
                [dialogueAnchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var otherProfile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                otherNovel.Id,
                "跨小说画像",
                "",
                [otherAnchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        var styled = await anchorService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [dialogueAnchor.AnchorId, neutralAnchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10,
                StyleProfileIds: [profile.ProfileId],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong),
            CancellationToken.None);

        Assert.True(styled.Total >= 2);
        var first = styled.Items[0];
        Assert.Equal(dialogueAnchor.AnchorId, first.AnchorId);
        Assert.NotNull(first.ScoreComponents);
        Assert.True(first.ScoreComponents["style_fit"] > 0);
        Assert.True(first.ScoreComponents["source_risk_penalty"] < 0);
        Assert.DoesNotContain(styled.Items.Where(item => item.AnchorId == neutralAnchor.AnchorId), item =>
            item.ScoreComponents?.ContainsKey("style_fit") == true);

        await Assert.ThrowsAsync<ArgumentException>(async () => await anchorService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [dialogueAnchor.AnchorId, neutralAnchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10,
                StyleProfileIds: [otherProfile.ProfileId],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong),
            CancellationToken.None));
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

    private static async ValueTask<PersistedStyleProfile> ReadPersistedStyleProfileAsync(
        AppInitializationOptions options,
        long profileId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT feature_vector_json
            FROM reference_style_profiles
            WHERE profile_id = $profile_id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var featureVectorJson = Assert.IsType<string>(await command.ExecuteScalarAsync());

        await using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(reference_style_profile_evidence);";
        var columns = new List<string>();
        await using var reader = await info.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return new PersistedStyleProfile(featureVectorJson, columns);
    }

    private static IReadOnlyList<string> FeatureSignature(ReferenceStyleFeatureVectorPayload features)
    {
        var numeric = features.NumericFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => $"n:{feature.FeatureKey}:{feature.Value}:{feature.Unit}:{feature.Confidence}");
        var distributions = features.DistributionFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => "d:" + feature.FeatureKey + ":" + string.Join(
                ";",
                feature.Buckets.Select(bucket => $"{bucket.Label}:{bucket.Min}:{bucket.Max}:{bucket.Weight}")));
        var categories = features.CategoricalFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => $"c:{feature.FeatureKey}:{feature.Label}:{feature.Weight}:{feature.Confidence}");
        return numeric.Concat(distributions).Concat(categories).ToArray();
    }

    private static async ValueTask<IReadOnlyList<string>> ReadSegmentSignaturesAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, segment_type, segment_index, text_hash
            FROM reference_source_segments
            WHERE anchor_id = $anchor_id
            ORDER BY segment_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<string>> ReadMaterialSignaturesAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id, source_segment_id, material_type, source_hash, extractor_version
            FROM reference_materials
            WHERE anchor_id = $anchor_id
            ORDER BY material_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    private static async ValueTask<bool> TableExistsAsync(AppInitializationOptions options, string tableName)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $table_name;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async ValueTask<string?> ReadMaterialArchivedAtAsync(
        AppInitializationOptions options,
        string materialId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT archived_at
            FROM reference_materials
            WHERE material_id = $material_id;
            """;
        command.Parameters.AddWithValue("$material_id", materialId);
        var archivedAt = await command.ExecuteScalarAsync();
        return archivedAt is null || archivedAt == DBNull.Value ? null : Assert.IsType<string>(archivedAt);
    }

    private static async ValueTask HardDeleteMaterialAsync(AppInitializationOptions options, string materialId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reference_materials WHERE material_id = $material_id;";
        command.Parameters.AddWithValue("$material_id", materialId);
        await command.ExecuteNonQueryAsync();
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

    private sealed record PersistedStyleProfile(
        string FeatureVectorJson,
        IReadOnlyList<string> EvidenceColumns);
}
