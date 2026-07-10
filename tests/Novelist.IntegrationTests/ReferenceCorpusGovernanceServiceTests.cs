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
await using (var connection = await OpenAsync(options))
 await ExecuteAsync(connection, "CREATE TABLE IF NOT EXISTS reference_style_profiles(profile_id TEXT PRIMARY KEY,novel_id INTEGER NOT NULL,title TEXT NOT NULL,description TEXT NOT NULL,status TEXT NOT NULL,analyzer_version TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL); INSERT INTO reference_source_license(anchor_id,license_state,reuse_policy,cleared_for_insertion) VALUES(1,'authorized','adapted_only',1);");

 var aggregates = await service.BuildAggregatesAsync(new(["library-project"], "run-old"), CancellationToken.None);
 Assert.Equal(4, aggregates.Count);
 Assert.Contains(aggregates, aggregate => aggregate.AggregateType == "style_profile" && aggregate.SampleCount > 0 && aggregate.AnchorIds.Contains(1));

 var reconciled = await service.ReconcileRunAsync(new(1, "run-new"), CancellationToken.None);
 Assert.True(reconciled.SupersededObservations > 0);
 Assert.True(reconciled.AggregatesMarkedStale > 0);
Assert.Contains(await service.ListAggregatesAsync(new(null), CancellationToken.None), aggregate => aggregate.ValidityState == "stale");
 await using var verify = await OpenAsync(options);
 await using var command = verify.CreateCommand();
 command.CommandText = "SELECT COUNT(*) FROM reference_corpus_style_aggregates;";
Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
}

 [Fact]
 public async Task BuildAggregatesExcludesIneligibleAndDuplicateSources()
 {
 var (options, service) = await CreateServiceAsync();
 await SeedAggregateEligibilityMatrixAsync(options);

 var aggregates = await service.BuildAggregatesAsync(new(["library-project"], null), CancellationToken.None);

 var style = Assert.Single(aggregates, aggregate => aggregate.AggregateType == "style_profile");
Assert.Equal(1, style.SampleCount);
Assert.Equal([1L], style.AnchorIds);
Assert.Equal(["library-project"], style.LibraryIds);
 var dialogue = Assert.Single(aggregates, aggregate => aggregate.AggregateType == "dialogue_technique");
 Assert.Equal(1, dialogue.SampleCount);
 Assert.Equal([1L], dialogue.AnchorIds);
 }

 [Fact]
 public async Task BuildAggregatesRollsBackAllTablesWhenProjectionWriteFails()
 {
 var (options, service) = await CreateServiceAsync();
 await SeedAsync(options);
 await SeedAnalysisAsync(options);
 await using (var connection = await OpenAsync(options))
 await ExecuteAsync(connection, "INSERT INTO reference_source_license(anchor_id,license_state,reuse_policy,cleared_for_insertion) VALUES(1,'authorized','adapted_only',1);");
 await service.BuildAggregatesAsync(new(["library-project"], "run-old"), CancellationToken.None);

 await using (var connection = await OpenAsync(options))
 await ExecuteAsync(connection, "INSERT INTO reference_feature_observations(observation_id,node_id,node_type,run_id,anchor_id,feature_family,feature_key,value_kind,value_text,confidence,review_state,validity_state,created_at) VALUES('obs-extra','node-1','sentence','run-old',1,'rhythm','mode','text','urgent',0.8,'unverified','active','2026-07-10T02:00:00Z'); CREATE TRIGGER fail_scene_projection BEFORE UPDATE ON reference_corpus_scene_aggregates BEGIN SELECT RAISE(ABORT, 'projection failure'); END;");

 await Assert.ThrowsAsync<SqliteException>(async () =>
 await service.BuildAggregatesAsync(new(["library-project"], "run-old"), CancellationToken.None));

 await using var verify = await OpenAsync(options);
 Assert.Equal(1, await ScalarAsync(verify, "SELECT sample_count FROM reference_aggregates WHERE aggregate_type='style_profile';"));
 Assert.Equal(1, await ScalarAsync(verify, "SELECT sample_count FROM reference_corpus_style_aggregates;"));
 Assert.Equal(1, await ScalarAsync(verify, "SELECT COUNT(*) FROM reference_aggregate_provenance WHERE aggregate_id=(SELECT aggregate_id FROM reference_aggregates WHERE aggregate_type='style_profile');"));
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

 [Fact]
 public async Task RecomputesInsertionAuditAndRejectsUnlicensedSources()
 {
 var (options, service) = await CreateServiceAsync();
 await SeedAsync(options);
 await SeedInsertionNodeAsync(options);
 await service.SetSessionLibraryBindingAsync(new("project:7:default", "library-project", true), CancellationToken.None);
 await service.UpdateLicenseAsync(new(1, "authorized", "contract", "adapted_only", 0.95, true), CancellationToken.None);

 var input = BuildInsertionAudit("audit-valid", "他推门进屋，外面的雨声跟着涌了进来。");
 Assert.True(await service.RecordInsertionAuditAsync(input, CancellationToken.None));
 Assert.True(await service.RecordInsertionAuditAsync(input, CancellationToken.None));

 await service.UpdateLicenseAsync(new(1, "forbidden", null, "forbidden", null, false), CancellationToken.None);
 await Assert.ThrowsAsync<InvalidOperationException>(async () =>
 await service.RecordInsertionAuditAsync(BuildInsertionAudit("audit-forbidden", "他推门进屋，外面的雨声跟着涌了进来。"), CancellationToken.None));
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

 private static async ValueTask SeedAggregateEligibilityMatrixAsync(AppInitializationOptions options)
 {
 await using var connection = await OpenAsync(options);
 await ExecuteAsync(connection, """
 INSERT INTO reference_anchors(anchor_id,novel_id,title,author,source_path,source_kind,license_status,source_file_hash,build_version,status,created_at,updated_at)
 SELECT value,7,'A'||value,'author','a'||value||'.txt','txt','unknown','hash-'||value,'v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z' FROM json_each('[1,2,3,4,5,6,7,8]');
 INSERT INTO reference_corpus_libraries(library_id,scope,novel_id,name,created_at) VALUES('library-project','project',7,'Project Library','2026-07-10T00:00:00Z');
 INSERT INTO reference_library_members(library_id,anchor_id,enabled,source_quality,dedup_group_id) VALUES
 ('library-project',1,1,'trusted','duplicate'),('library-project',2,1,'normal','duplicate'),('library-project',3,0,'trusted','disabled'),('library-project',4,1,'trusted','unauthorized'),('library-project',5,1,'trusted','not-cleared'),('library-project',6,1,'trusted','rejected'),('library-project',7,1,'trusted','invalid'),('library-project',8,1,'trusted','superseded');
 INSERT INTO reference_source_license(anchor_id,license_state,reuse_policy,cleared_for_insertion) VALUES
 (1,'authorized','adapted_only',1),(2,'authorized','adapted_only',1),(3,'authorized','adapted_only',1),(4,'unknown','reference_only',0),(5,'authorized','adapted_only',0),(6,'authorized','adapted_only',1),(7,'authorized','adapted_only',1),(8,'authorized','adapted_only',1);
 INSERT INTO reference_text_nodes(node_id,anchor_id,node_type,sequence_index,depth,start_offset,end_offset,char_len,text_hash,text,created_at)
 SELECT 'node-'||value,value,'sentence',0,0,0,4,4,'node-hash-'||value,'text-'||value,'2026-07-10T00:00:00Z' FROM json_each('[1,2,3,4,5,6,7,8]');
 INSERT INTO reference_analysis_runs(run_id,anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,started_at)
 SELECT 'run-'||value,value,'v1','v1','fake','fake','sentence','completed','2026-07-10T00:00:00Z' FROM json_each('[1,2,3,4,5,6,7,8]');
INSERT INTO reference_feature_observations(observation_id,node_id,node_type,run_id,anchor_id,feature_family,feature_key,value_kind,value_text,confidence,review_state,validity_state,superseded_by_run_id,created_at) VALUES
('obs-1','node-1','sentence','run-1',1,'syntax','shape','text','eligible',0.9,'confirmed','active',NULL,'2026-07-10T00:00:00Z'),('obs-2','node-2','sentence','run-2',2,'syntax','shape','text','duplicate',0.9,'confirmed','active',NULL,'2026-07-10T00:00:00Z'),('obs-3','node-3','sentence','run-3',3,'syntax','shape','text','disabled',0.9,'confirmed','active',NULL,'2026-07-10T00:00:00Z'),('obs-4','node-4','sentence','run-4',4,'syntax','shape','text','unauthorized',0.9,'confirmed','active',NULL,'2026-07-10T00:00:00Z'),('obs-5','node-5','sentence','run-5',5,'syntax','shape','text','not-cleared',0.9,'confirmed','active',NULL,'2026-07-10T00:00:00Z'),('obs-6','node-6','sentence','run-6',6,'syntax','shape','text','rejected',0.9,'rejected','active',NULL,'2026-07-10T00:00:00Z'),('obs-7','node-7','sentence','run-7',7,'syntax','shape','text','invalid',0.9,'confirmed','invalid',NULL,'2026-07-10T00:00:00Z'),('obs-8','node-8','sentence','run-8',8,'syntax','shape','text','superseded',0.9,'confirmed','active','run-new','2026-07-10T00:00:00Z');
 INSERT INTO reference_technique_specimens(specimen_id,source_node_id,source_anchor_id,analysis_run_id,technique_family,technique_abstract,trigger_context,transfer_template,transfer_slots_json,effect_on_reader,applicability_conditions,failure_modes,anti_patterns,why_it_works_json,confidence,review_state,validity_state,superseded_by_run_id,created_at) VALUES
 ('spec-1','node-1',1,'run-1','dialogue-restraint','eligible','scene','template','[]','effect','conditions','failures','anti','{}',0.9,'confirmed','active',NULL,'2026-07-10T00:00:00Z'),
 ('spec-2','node-2',2,'run-2','dialogue-restraint','duplicate','scene','template','[]','effect','conditions','failures','anti','{}',0.9,'confirmed','active',NULL,'2026-07-10T00:00:00Z'),
 ('spec-6','node-6',6,'run-6','dialogue-restraint','rejected','scene','template','[]','effect','conditions','failures','anti','{}',0.9,'rejected','active',NULL,'2026-07-10T00:00:00Z'),
 ('spec-7','node-7',7,'run-7','dialogue-restraint','invalid','scene','template','[]','effect','conditions','failures','anti','{}',0.9,'confirmed','invalid',NULL,'2026-07-10T00:00:00Z'),
 ('spec-8','node-8',8,'run-8','dialogue-restraint','superseded','scene','template','[]','effect','conditions','failures','anti','{}',0.9,'confirmed','active','run-new','2026-07-10T00:00:00Z');
""");
 }

 private static async ValueTask<SqliteConnection> OpenAsync(AppInitializationOptions options)
 {
 var path = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); await connection.OpenAsync();
 await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None); return connection;
 }
private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
 private static async ValueTask<int> ScalarAsync(SqliteConnection connection, string sql) { await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt32(await command.ExecuteScalarAsync()); }
private static async ValueTask SeedInsertionNodeAsync(AppInitializationOptions options)
{
const string text = "他推开门，雨声一下灌进屋里。";
var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
await using var connection = await OpenAsync(options);
await using var command = connection.CreateCommand();
command.CommandText = "INSERT INTO reference_text_nodes(node_id,anchor_id,node_type,sequence_index,depth,start_offset,end_offset,char_len,text_hash,text,created_at) VALUES($node,1,'sentence',0,0,0,$length,$length,$hash,$text,'2026-07-10T00:00:00Z');";
command.Parameters.AddWithValue("$node", "node-1");
command.Parameters.AddWithValue("$length", text.Length);
command.Parameters.AddWithValue("$hash", hash);
command.Parameters.AddWithValue("$text", text);
await command.ExecuteNonQueryAsync();
}

private static RecordReferenceCorpusInsertionAuditPayload BuildInsertionAudit(string auditId, string output)
{
const string source = "他推开门，雨声一下灌进屋里。";
var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
var piece = new ReferenceCorpusInsertionPiecePayload("piece-1", "beat-1", "source-1", "node-1", 1, "library-project", sourceHash, "adapted_only", "authorized", output, "hash", true, [], [], []);
var chapterContext = new CurrentChapterContextPayload(7, 1, output, output.Length, null, []);
var scope = new ReferenceCorpusScopePayload(["library-project"], ["adapted_only"], [1], [], "project:7:default");
var query = new ReferenceCorpusQueryContextPayload("scene", "goal", "pacing", "opening", "hook", [], [], chapterContext, scope);
var blueprint = new ReferenceCorpusInsertionBlueprintPayload("blueprint-1", "query-hash", "strategy", []);
var draft = new ReferenceCorpusInsertionDraftPayload(query, blueprint, [piece], [], [], output, output, true, new(true, "passed", [], []), new(true, "passed", [], [], []));
return new RecordReferenceCorpusInsertionAuditPayload(auditId, "project:7:default", 7, 1, "candidate-1", draft);
}

 public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
