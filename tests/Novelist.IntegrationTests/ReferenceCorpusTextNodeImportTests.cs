using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;
using Novelist.IntegrationTests.TestDoubles;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusTextNodeImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
public async Task CreateAnchorPopulatesStableTextNodesAndLinksSegmentsAndMaterials()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("节点导入测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "nodes.md",
            """
            # 第一章 雨门

            雨声贴着门缝往里挤。

 她没有立刻开口，只把钥匙扣在掌心。
""");
        var service = new SqliteReferenceAnchorService(options, novels);

        var anchor = await service.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "雨门节点参考",
                null,
                sourcePath,
                "markdown",
                "user_provided"),
            CancellationToken.None);

        var nodes = await ReadTextNodesAsync(options, anchor.AnchorId);
        Assert.Contains(nodes, node => node.NodeType == ReferenceCorpusNodeTypes.Chapter && node.Text.Contains("雨声贴着门缝", StringComparison.Ordinal));
        Assert.Contains(nodes, node => node.NodeType == ReferenceCorpusNodeTypes.Passage && node.Text == "雨声贴着门缝往里挤。");
        var firstSentence = Assert.Single(nodes, node => node.NodeType == ReferenceCorpusNodeTypes.Sentence && node.Text == "雨声贴着门缝往里挤。");
        var firstPassage = Assert.Single(nodes, node => node.NodeId == firstSentence.ParentNodeId);
        Assert.Equal(ReferenceCorpusNodeTypes.Passage, firstPassage.NodeType);
        Assert.Equal(firstSentence.TextHash, await ReadSourceSegmentNodeTextHashAsync(options, firstSentence.NodeId));
        Assert.True(await MaterialNodeExistsAsync(options, firstSentence.NodeId));
        Assert.True(await ObservationExistsAsync(options, firstSentence.NodeId, "rhythm", "length_band"));
Assert.True(await ObservationExistsAsync(options, firstSentence.NodeId, "sensory", "senses"));
 var compoundSentence = Assert.Single(nodes, node =>
 node.NodeType == ReferenceCorpusNodeTypes.Sentence && node.Text == "她没有立刻开口，只把钥匙扣在掌心。");
 var clauses = nodes
 .Where(node => node.NodeType == ReferenceCorpusNodeTypes.Clause && node.ParentNodeId == compoundSentence.NodeId)
 .OrderBy(node => node.SequenceIndex)
 .ToArray();
 Assert.Equal(["她没有立刻开口，", "只把钥匙扣在掌心。"], clauses.Select(node => node.Text));
 Assert.True(clauses[0].SequenceIndex < clauses[1].SequenceIndex);
 var sourceText = await File.ReadAllTextAsync(sourcePath);
 Assert.All(clauses, clause =>
 {
 Assert.Equal(clause.Text, sourceText[clause.StartOffset..clause.EndOffset]);
 Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(clause.Text))).ToLowerInvariant(), clause.TextHash);
 });

 await service.RebuildAnchorAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
 var rebuiltClauses = (await ReadTextNodesAsync(options, anchor.AnchorId))
 .Where(node => node.NodeType == ReferenceCorpusNodeTypes.Clause)
 .OrderBy(node => node.SequenceIndex)
 .Select(node => node.NodeId)
 .ToArray();
 Assert.Equal(nodes.Where(node => node.NodeType == ReferenceCorpusNodeTypes.Clause).OrderBy(node => node.SequenceIndex).Select(node => node.NodeId), rebuiltClauses);

        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        Assert.Equal("project:" + novel.Id + ":default", libraryId);
        var license = await ReadSourceLicenseAsync(options, anchor.AnchorId);
        Assert.Equal(ReferenceCorpusLicenseStates.Authorized, license.LicenseState);
        Assert.Equal(ReferenceCorpusReusePolicies.AdaptedOnly, license.ReusePolicy);
        Assert.True(license.ClearedForInsertion);

        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var search = await corpus.SearchCandidatesAsync(
            BuildSearchPayload(novel.Id, libraryId),
            CancellationToken.None);

        var candidate = Assert.Single(search.Items, item => item.NodeId == firstSentence.NodeId);
Assert.Contains(candidate.Evidence, item => item.FeatureFamily == "sensory" && item.FeatureKey == "senses");
}

 [Fact]
 public async Task NodeWindowReturnsAdjacentChaptersAndSceneSiblingsWithoutRetrieval()
 {
 var (options, novelId, anchorId) = await CreateWindowFixtureAsync();
 var nodes = await ReadTextNodesAsync(options, anchorId);
 var focus = Assert.Single(nodes, node => node.NodeType == ReferenceCorpusNodeTypes.Sentence && node.Text.Contains("第二章焦点", StringComparison.Ordinal));
 var corpus = new SqliteReferenceCorpusService(options);

 var window = await corpus.GetNodeWindowAsync(
 new GetReferenceCorpusNodeWindowPayload(anchorId, focus.NodeId, 1, 1, true, 200),
 CancellationToken.None);

 Assert.NotNull(window);
 Assert.Equal(focus.NodeId, window.FocusNodeId);
 Assert.Equal(2, window.FocusChapterIndex);
 Assert.Contains(window.ChapterNodes, node => node.ChapterIndex == 1);
 Assert.Contains(window.ChapterNodes, node => node.ChapterIndex == 2);
 Assert.Contains(window.ChapterNodes, node => node.ChapterIndex == 3);
 Assert.NotNull(window.SceneNodeId);
 Assert.NotEmpty(window.SceneSiblings);
 Assert.All(window.SceneSiblings, node => Assert.Equal(window.SceneNodeId, node.ParentNodeId));
 Assert.False(window.Truncated);
 Assert.True(novelId > 0);
 }

 [Fact]
 public async Task SensoryProjectionRebuildRestoresActiveRowsAndIsIdempotent()
 {
 var (options, _, anchorId) = await CreateWindowFixtureAsync();
 await using (var connection = await OpenReferenceConnectionAsync(options))
 {
 await using var delete = connection.CreateCommand();
 delete.CommandText = "DELETE FROM reference_obs_sensory WHERE anchor_id=$anchor_id;";
 delete.Parameters.AddWithValue("$anchor_id", anchorId);
 await delete.ExecuteNonQueryAsync();
 }

 var corpus = new SqliteReferenceCorpusService(options);
 var first = await corpus.RebuildSensoryProjectionAsync(new(anchorId), CancellationToken.None);
 var second = await corpus.RebuildSensoryProjectionAsync(new(anchorId), CancellationToken.None);

 Assert.True(first.ObservationCount > 0);
 Assert.True(first.ProjectionRowCount > 0);
 Assert.Equal(0, first.InvalidObservationCount);
 Assert.Equal(first, second);
 Assert.Equal(first.ProjectionRowCount, await CountSensoryProjectionRowsAsync(options, anchorId));
 }

 [Fact]
 public async Task SchemaUpgradeBackfillsLegacyTextNodeProjectionAndIsIdempotent()
 {
 var (options, novelId, anchorId) = await CreateWindowFixtureAsync();
 var before = await ReadTextNodesAsync(options, anchorId);
Assert.NotEmpty(before);
await using (var connection = await OpenReferenceConnectionAsync(options))
{
 await using (var disableForeignKeys = connection.CreateCommand())
 {
 disableForeignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
 await disableForeignKeys.ExecuteNonQueryAsync();
 }

await using var command = connection.CreateCommand();
command.CommandText = """
UPDATE reference_source_segments SET node_id = NULL WHERE anchor_id=$anchor_id;
UPDATE reference_materials SET node_id = NULL WHERE anchor_id=$anchor_id;
DELETE FROM reference_text_nodes WHERE anchor_id=$anchor_id;
""";
command.Parameters.AddWithValue("$anchor_id", anchorId);
await command.ExecuteNonQueryAsync();

 await using var enableForeignKeys = connection.CreateCommand();
 enableForeignKeys.CommandText = "PRAGMA foreign_keys = ON;";
 await enableForeignKeys.ExecuteNonQueryAsync();
}

 var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
 var service = new SqliteReferenceAnchorService(options, novels);
 _ = await service.GetAnchorsAsync(novelId, CancellationToken.None);
 var migrated = await ReadTextNodesAsync(options, anchorId);
 _ = await service.GetAnchorsAsync(novelId, CancellationToken.None);
 var migratedAgain = await ReadTextNodesAsync(options, anchorId);

 Assert.Equal(before.Select(node => node.NodeId), migrated.Select(node => node.NodeId));
 Assert.Equal(migrated.Select(node => node.NodeId), migratedAgain.Select(node => node.NodeId));
 Assert.All(migrated, node => Assert.Equal(node.Text, before.Single(original => original.NodeId == node.NodeId).Text));
 Assert.Equal(0, await CountMissingNodeLinksAsync(options, anchorId));
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
        // 原始字符串字面量会嵌入 .cs 文件本身的换行符（Windows 检出为 CRLF）；
        // 统一写成 LF，保证文本树 offset 与源文件切片在任何检出环境都一致。
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return path;
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static async ValueTask<IReadOnlyList<TextNodeRow>> ReadTextNodesAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
 command.CommandText =
 "SELECT node_id, parent_node_id, node_type, sequence_index, start_offset, end_offset, text_hash, text " +
 "FROM reference_text_nodes WHERE anchor_id = $anchor_id " +
 "ORDER BY start_offset, depth, sequence_index, node_id;";
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var nodes = new List<TextNodeRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            nodes.Add(new TextNodeRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
reader.GetString(2),
reader.GetInt32(3),
 reader.GetInt32(4),
 reader.GetInt32(5),
 reader.GetString(6),
 reader.GetString(7)));
        }

        return nodes;
    }

    private static async ValueTask<string> ReadSourceSegmentNodeTextHashAsync(
        AppInitializationOptions options,
        string nodeId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.text_hash
            FROM reference_source_segments s
            JOIN reference_text_nodes n ON n.node_id = s.node_id
            WHERE s.node_id = $node_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$node_id", nodeId);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async ValueTask<bool> MaterialNodeExistsAsync(
        AppInitializationOptions options,
        string nodeId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_materials
            WHERE node_id = $node_id;
            """;
        command.Parameters.AddWithValue("$node_id", nodeId);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async ValueTask<bool> ObservationExistsAsync(
        AppInitializationOptions options,
        string nodeId,
        string featureFamily,
        string featureKey)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_feature_observations
            WHERE node_id = $node_id
              AND feature_family = $feature_family
              AND feature_key = $feature_key
              AND validity_state = 'active';
            """;
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$feature_family", featureFamily);
        command.Parameters.AddWithValue("$feature_key", featureKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async ValueTask<string> ReadDefaultLibraryIdAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT library_id
            FROM reference_library_members
            WHERE anchor_id = $anchor_id
            ORDER BY library_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async ValueTask<SourceLicenseRow> ReadSourceLicenseAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT license_state, reuse_policy, cleared_for_insertion
            FROM reference_source_license
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new SourceLicenseRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2) != 0);
    }

    private static EmbeddingRequestOptions CreateEmbeddingOptions()
    {
        return new EmbeddingRequestOptions(
            ProviderKey: "fake",
            EndpointUrl: string.Empty,
            ApiKey: string.Empty,
            ModelId: "hash-model",
            Dimensions: 8,
            User: null,
            NormalizeEmbeddings: true);
    }

    private static SearchReferenceCorpusCandidatesPayload BuildSearchPayload(
        long novelId,
        string libraryId)
    {
        return new SearchReferenceCorpusCandidatesPayload(
            new ReferenceCorpusQueryContextPayload(
                SceneType: "doorway_confrontation",
                EmotionTarget: "restrained_pressure",
                PacingTarget: "slow_tension",
                NarrativePosition: "pre-reveal",
                CommercialMechanic: "withheld-answer-hook",
                CharacterStates: ["林岚 guarded"],
                RequiredNarrativeFunctions: ["raise_pressure"],
                ChapterContext: new CurrentChapterContextPayload(
                    NovelId: novelId,
                    ChapterNumber: 1,
                    CurrentDraftText: "林岚停在门里，指尖还按着锁。",
                    InsertionOffset: 8,
                    PreviousChapterSummary: "有人在雨夜靠近。",
                    CharacterSnapshots: []),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [libraryId],
                    ReusePolicies: [ReferenceCorpusReusePolicies.AdaptedOnly],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: [])),
            new PageRequestPayload(
                Cursor: null,
                PageSize: 10,
                SortBy: "score",
                SortDir: "desc",
                Filters: new Dictionary<string, string> { ["node_type"] = ReferenceCorpusNodeTypes.Sentence }));
    }

    private static async ValueTask<SqliteConnection> OpenReferenceConnectionAsync(AppInitializationOptions options)
    {
        var databasePath = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private sealed record TextNodeRow(
        string NodeId,
string? ParentNodeId,
string NodeType,
int SequenceIndex,
 int StartOffset,
 int EndOffset,
string TextHash,
        string Text);

    private sealed record SourceLicenseRow(
        string LicenseState,
        string ReusePolicy,
        bool ClearedForInsertion);

    private sealed class StaticEmbeddingConfigurationService : IEmbeddingConfigurationService
    {
        private readonly EmbeddingRequestOptions _options;

        public StaticEmbeddingConfigurationService(EmbeddingRequestOptions options)
        {
            _options = options;
        }

        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<EmbeddingRequestOptions?>(_options);
 }
}

 private async ValueTask<(AppInitializationOptions Options, long NovelId, long AnchorId)> CreateWindowFixtureAsync()
 {
 var options = CreateOptions();
 await InitializeAsync(options);
 var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
 var novel = await novels.CreateNovelAsync(new CreateNovelPayload("窗口与迁移测试", "", ""), CancellationToken.None);
 var sourcePath = CreateSourceFile(
 "window.md",
 """
 # 第一章

 第一章雨声贴着门缝。

 # 第二章

 第二章焦点停在门边，手指压住锁扣。

 第二章同场景的另一拍。

 # 第三章

 第三章灯火从长街尽头亮起。
 """);
 var service = new SqliteReferenceAnchorService(options, novels);
 var anchor = await service.CreateAnchorAsync(
 new CreateReferenceAnchorPayload(novel.Id, "窗口参考", null, sourcePath, "markdown", "user_provided"),
 CancellationToken.None);
 return (options, novel.Id, anchor.AnchorId);
 }

 private static async ValueTask<int> CountSensoryProjectionRowsAsync(AppInitializationOptions options, long anchorId)
 {
 await using var connection = await OpenReferenceConnectionAsync(options);
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT COUNT(*) FROM reference_obs_sensory WHERE anchor_id=$anchor_id;";
 command.Parameters.AddWithValue("$anchor_id", anchorId);
 return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
 }

 private static async ValueTask<int> CountMissingNodeLinksAsync(AppInitializationOptions options, long anchorId)
 {
 await using var connection = await OpenReferenceConnectionAsync(options);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT
 (SELECT COUNT(*) FROM reference_source_segments WHERE anchor_id=$anchor_id AND node_id IS NULL) +
 (SELECT COUNT(*) FROM reference_materials WHERE anchor_id=$anchor_id AND node_id IS NULL);
 """;
 command.Parameters.AddWithValue("$anchor_id", anchorId);
return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
}

}
