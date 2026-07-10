using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusGovernanceServiceTests : IDisposable
{
 private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-governance-tests", Guid.NewGuid().ToString("N"));

 [Fact]
 public async Task GovernsBindingsMembersLicensesAndDedupGroups()
 {
 var (options, service) = await CreateServiceAsync();
 await SeedAsync(options);

 var bound = await service.SetSessionLibraryBindingAsync(new("project:7:default", "library-project", true), CancellationToken.None);
 Assert.True(Assert.Single(bound.Libraries, library => library.LibraryId == "library-project").BoundToSession);

 var disabled = await service.UpdateLibraryMemberAsync(new("library-project", 1, false, "low", "duplicate source"), CancellationToken.None);
 Assert.False(Assert.Single(disabled.Libraries, library => library.LibraryId == "library-project").Members.Single(member => member.AnchorId == 1).Enabled);

 await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.UpdateLicenseAsync(new(1, "unknown", null, "reference_only", null, true), CancellationToken.None));
 var licensed = await service.UpdateLicenseAsync(new(1, "authorized", "contract", "adapted_only", 0.35, true), CancellationToken.None);
 Assert.True(Assert.Single(licensed.Libraries.SelectMany(library => library.Members), member => member.AnchorId == 1).ClearedForInsertion);

 var dedup = await service.RebuildDedupGroupsAsync(new(null), CancellationToken.None);
 Assert.Equal(2, dedup.MembersScanned);
 Assert.Equal(1, dedup.GroupsAssigned);
 }

 [Fact]
 public async Task BuildsAggregatesWithProvenanceAndMarksThemStaleOnRerun()
 {
 var (options, service) = await CreateServiceAsync();
 await SeedAsync(options);
 await SeedAnalysisAsync(options);

 var aggregates = await service.BuildAggregatesAsync(new(["library-project"], "run-old"), CancellationToken.None);
 Assert.Equal(4, aggregates.Count);
 Assert.Contains(aggregates, aggregate => aggregate.AggregateType == "style_profile" && aggregate.SampleCount > 0 && aggregate.AnchorIds.Contains(1));

 var reconciled = await service.ReconcileRunAsync(new(1, "run-new"), CancellationToken.None);
 Assert.True(reconciled.SupersededObservations > 0);
 Assert.True(reconciled.AggregatesMarkedStale > 0);
 Assert.Contains(await service.ListAggregatesAsync(new(null), CancellationToken.None), aggregate => aggregate.ValidityState == "stale");
 }

 [Fact]
 public async Task QueuesLowConfidenceObservationsAndSupportsPagedBatchReview()
 {
 var (options, service) = await CreateServiceAsync();
 await SeedAsync(options);
 await SeedAnalysisAsync(options);

 Assert.True(await service.RefreshReviewQueueAsync(new(0.7), CancellationToken.None) > 0);
 var page = await service.ListReviewQueueAsync(new(new(null, 1, "created_at", "asc")), CancellationToken.None);
 var item = Assert.Single(page.Items);
 Assert.True(page.HasMore);

 Assert.Equal(1, await service.ReviewItemsAsync(new([item.QueueId], "confirmed"), CancellationToken.None));
 var next = await service.ListReviewQueueAsync(new(new(page.NextCursor, 10, "created_at", "asc")), CancellationToken.None);
 Assert.DoesNotContain(next.Items, queued => queued.QueueId == item.QueueId);
 }

 private async ValueTask<(AppInitializationOptions Options, SqliteReferenceCorpusGovernanceService Service)> CreateServiceAsync()
 {
 var options = new AppInitializationOptions { ConfigDirectory = Path.Combine(_root, "config"), DefaultDataDirectory = Path.Combine(_root, "data") };
 await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
 return (options, new SqliteReferenceCorpusGovernanceService(options));
 }

private static async ValueTask SeedAsync(AppInitializationOptions options)
{
await using var connection = await OpenAsync(options);
 await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS reference_anchors(anchor_id INTEGER PRIMARY KEY,novel_id INTEGER,title TEXT NOT NULL,author TEXT NOT NULL,source_path TEXT NOT NULL,source_kind TEXT NOT NULL,license_status TEXT NOT NULL,source_file_hash TEXT NOT NULL,build_version TEXT NOT NULL,status TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,corpus_visibility TEXT NOT NULL DEFAULT 'private',source_trust TEXT NOT NULL DEFAULT 'user_verified',user_tags_json TEXT NOT NULL DEFAULT '[]');");
 await ExecuteAsync(connection, "INSERT INTO reference_anchors(anchor_id,novel_id,title,author,source_path,source_kind,license_status,source_file_hash,build_version,status,created_at,updated_at) VALUES(1,7,'A','author','a.txt','txt','unknown','same-hash','v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z'),(2,7,'B','author','b.txt','txt','unknown','same-hash','v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');");
 await ExecuteAsync(connection, "INSERT INTO reference_corpus_libraries(library_id,scope,novel_id,name,created_at) VALUES('library-project','project',7,'Project Library','2026-07-10T00:00:00Z'); INSERT INTO reference_library_members(library_id,anchor_id) VALUES('library-project',1),('library-project',2);");
 }

 private static async ValueTask SeedAnalysisAsync(AppInitializationOptions options)
 {
 await using var connection = await OpenAsync(options);
 await ExecuteAsync(connection, "INSERT INTO reference_text_nodes(node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,start_offset,end_offset,char_len,text_hash,text,created_at) VALUES('node-1',1,NULL,'sentence',1,1,1,0,4,4,'hash','text','2026-07-10T00:00:00Z');");
 await ExecuteAsync(connection, "INSERT INTO reference_analysis_runs(run_id,anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,started_at) VALUES('run-old',1,'v1','v1','fake','fake','sentence','completed','2026-07-10T00:00:00Z'),('run-new',1,'v1','v1','fake','fake','sentence','completed','2026-07-10T01:00:00Z');");
 await ExecuteAsync(connection, "INSERT INTO reference_feature_observations(observation_id,node_id,node_type,run_id,anchor_id,feature_family,feature_key,value_kind,value_text,confidence,review_state,validity_state,created_at) VALUES('obs-low','node-1','sentence','run-old',1,'syntax','shape','text','short',0.4,'unverified','active','2026-07-10T00:00:00Z'),('obs-new','node-1','sentence','run-new',1,'syntax','shape','text','long',0.9,'unverified','active','2026-07-10T01:00:00Z');");
 }

 private static async ValueTask<SqliteConnection> OpenAsync(AppInitializationOptions options)
 {
 var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync();
 await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None); return connection;
 }
 private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
 public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
