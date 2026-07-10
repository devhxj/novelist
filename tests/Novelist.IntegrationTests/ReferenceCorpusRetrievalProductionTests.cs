using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;
using Novelist.IntegrationTests.TestDoubles;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusRetrievalProductionTests : IAsyncLifetime
{
 private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-retrieval-production", Guid.NewGuid().ToString("N"));
 private AppInitializationOptions Options => new()
 {
 ConfigDirectory = Path.Combine(_root, "config"),
 DefaultDataDirectory = Path.Combine(_root, "data")
 };

 [Fact]
 public async Task RebuildSensoryProjectionUsesOnlyActiveNonSupersededObservations()
 {
 await SeedAsync();
 var service = CreateService();

 var result = await service.RebuildSensoryProjectionAsync(new(AnchorId: 101), CancellationToken.None);

 Assert.Equal(2, result.ObservationCount);
 Assert.Equal(2, result.ProjectionRowCount);
 Assert.Equal(1, result.InvalidObservationCount);
 await using var connection = await OpenAsync();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT observation_id,sense,intensity FROM reference_obs_sensory ORDER BY observation_id,sense;";
 await using var reader = await command.ExecuteReaderAsync();
 Assert.True(await reader.ReadAsync());
 Assert.Equal("obs-active", reader.GetString(0));
 Assert.Equal("sound", reader.GetString(1));
 Assert.Equal(0.8, reader.GetDouble(2), 3);
 Assert.True(await reader.ReadAsync());
 Assert.Equal("obs-active", reader.GetString(0));
 Assert.Equal("touch", reader.GetString(1));
 Assert.False(await reader.ReadAsync());
 }

 [Fact]
 public async Task GetNodeWindowReturnsBoundedStableChapterAndSceneOrder()
 {
 await SeedAsync();
 var result = await CreateService().GetNodeWindowAsync(
 new(101, "sentence-2", PreviousChapterCount: 1, NextChapterCount: 1, MaxNodes: 4),
 CancellationToken.None);

 Assert.NotNull(result);
 Assert.Equal(2, result.FocusChapterIndex);
 Assert.Equal("scene-2", result.SceneNodeId);
 Assert.Equal(["chapter-1", "sentence-1", "chapter-2", "scene-2"], result.ChapterNodes.Select(item => item.NodeId));
 Assert.True(result.Truncated);
 Assert.Equal(["sentence-2", "sentence-2b"], result.SceneSiblings.Select(item => item.NodeId));
 }

 [Fact]
 public async Task SearchCandidatesUsesStableOpaqueCursorAndReturnsPerformanceDiagnostics()
 {
 await SeedAsync();
 var service = CreateService();
 var request = SearchRequest(pageSize: 2);

 var first = await service.SearchCandidatesAsync(request, CancellationToken.None);
 var second = await service.SearchCandidatesAsync(
 request with { PageRequest = request.PageRequest with { Cursor = first.NextCursor } },
 CancellationToken.None);
 var all = await service.SearchCandidatesAsync(SearchRequest(pageSize: 20), CancellationToken.None);

 Assert.True(first.HasMore);
 Assert.NotNull(first.NextCursor);
 Assert.DoesNotContain(first.Items.Select(item => item.NodeId), id => second.Items.Any(item => item.NodeId == id));
 Assert.Equal(all.Items.Select(item => item.NodeId).Take(first.Items.Count + second.Items.Count),
 first.Items.Concat(second.Items).Select(item => item.NodeId));
 Assert.All(first.Items, item =>
 {
 Assert.NotNull(item.RetrievalDiagnostics);
 Assert.Equal(first.Total, item.RetrievalDiagnostics!.CandidatePoolSize);
 Assert.True(item.RetrievalDiagnostics.NodeEmbeddingCount >= first.Items.Count);
 Assert.True(item.RetrievalDiagnostics.ElapsedMilliseconds >= 0);
 });

 var mismatched = request with
 {
 QueryContext = request.QueryContext with { EmotionTarget = "different-query" },
 PageRequest = request.PageRequest with { Cursor = first.NextCursor }
 };
 var exception = await Assert.ThrowsAsync<PageRequestValidationException>(async () =>
 await service.SearchCandidatesAsync(mismatched, CancellationToken.None));
 Assert.Equal(PageRequestErrorCodes.InvalidCursor, exception.Code);
 }

 private SqliteReferenceCorpusService CreateService() => new(
 Options,
 new StaticEmbeddingConfigurationService(new(
 "fake", string.Empty, string.Empty, "hash-model", 8, null)),
 new DeterministicHashEmbeddingClient(defaultDimensions: 8));

 private static SearchReferenceCorpusCandidatesPayload SearchRequest(int pageSize) => new(
 new(
 "confrontation",
 "restrained pressure",
 "slow tension",
 "middle",
 "withheld answer",
 ["guarded"],
 ["raise pressure"],
 new(3001, 2, "雨声贴着门缝。", 3, "有人靠近。", []),
 new(["library-1"], [ReferenceCorpusReusePolicies.AdaptedOnly], [], [])),
 new(null, pageSize, "score", "desc",
 new Dictionary<string, string> { ["node_type"] = ReferenceCorpusNodeTypes.Sentence }));

 private async ValueTask SeedAsync()
 {
 await new FileSystemAppInitializationService(Options).InitializeAsync(Options.DefaultDataDirectory, CancellationToken.None);
 await using var connection = await OpenAsync();
 await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,source_file_hash,build_version,status,created_at,updated_at)
 VALUES (101,3001,'Book','Author','book.txt','txt','allowed','hash','v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT INTO reference_corpus_libraries(library_id,scope,novel_id,name,created_at)
 VALUES ('library-1','global',NULL,'Library','2026-07-10T00:00:00Z');
 INSERT INTO reference_library_members(library_id,anchor_id,enabled,source_quality)
 VALUES ('library-1',101,1,'high');
 INSERT INTO reference_source_license(anchor_id,license_state,reuse_policy,cleared_for_insertion)
 VALUES (101,'authorized','adapted_only',1);
 INSERT INTO reference_analysis_runs
 (run_id,anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,started_at)
 VALUES ('run-1',101,'v1','v1','fake','model','sentence','completed','2026-07-10T00:00:00Z');
 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES
 ('chapter-1',101,NULL,'chapter',0,0,1,0,20,20,'h-c1','第一章','2026-07-10T00:00:00Z'),
 ('sentence-1',101,'chapter-1','sentence',1,1,1,1,10,9,'h-s1','雨声贴着门缝。','2026-07-10T00:00:00Z'),
 ('chapter-2',101,NULL,'chapter',2,0,2,21,80,59,'h-c2','第二章','2026-07-10T00:00:00Z'),
 ('scene-2',101,'chapter-2','scene',3,1,2,22,79,57,'h-sc2','门前场景','2026-07-10T00:00:00Z'),
 ('sentence-2',101,'scene-2','sentence',4,2,2,23,35,12,'h-s2','她把钥匙扣在掌心。','2026-07-10T00:00:00Z'),
 ('sentence-2b',101,'scene-2','sentence',5,2,2,36,48,12,'h-s2b','脚步停在门外。','2026-07-10T00:00:00Z'),
 ('chapter-3',101,NULL,'chapter',6,0,3,81,120,39,'h-c3','第三章','2026-07-10T00:00:00Z'),
 ('sentence-3',101,'chapter-3','sentence',7,1,3,82,95,13,'h-s3','回答被雨声压住。','2026-07-10T00:00:00Z');
 INSERT INTO reference_feature_observations
 (observation_id,node_id,node_type,run_id,anchor_id,feature_family,feature_key,value_kind,value_json,confidence,validity_state,created_at)
 VALUES
 ('obs-active','sentence-2','sentence','run-1',101,'sensory','senses','array','[{"sense":"sound","intensity":0.8},{"sense":"touch","intensity":0.5}]',0.9,'active','2026-07-10T00:00:00Z'),
 ('obs-invalid','sentence-2b','sentence','run-1',101,'sensory','senses','array','{}',0.9,'active','2026-07-10T00:00:00Z'),
 ('obs-superseded','sentence-1','sentence','run-1',101,'sensory','senses','array','[{"sense":"sight","intensity":0.4}]',0.9,'superseded','2026-07-10T00:00:00Z');
 INSERT INTO reference_obs_sensory(observation_id,node_id,anchor_id,sense,intensity)
 VALUES ('obs-superseded','sentence-1',101,'stale',1.0);
 """;
 await command.ExecuteNonQueryAsync();
 }

 private async ValueTask<SqliteConnection> OpenAsync()
 {
 var path = Path.Combine(Options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
 Directory.CreateDirectory(Path.GetDirectoryName(path)!);
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
 await connection.OpenAsync();
 return connection;
 }

 public Task InitializeAsync() => Task.CompletedTask;

 public Task DisposeAsync()
 {
 if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
 return Task.CompletedTask;
 }

 private sealed class StaticEmbeddingConfigurationService(EmbeddingRequestOptions options) : IEmbeddingConfigurationService
 {
 public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken) =>
 ValueTask.FromResult<EmbeddingRequestOptions?>(options);
 }
}
