using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusCascadeImpactTests : IDisposable
{
 private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-cascade-tests", Guid.NewGuid().ToString("N"));

 [Fact]
 public async Task ReturnsDistinctReadOnlySpecimenBeatAndBlueprintImpact()
 {
 var options = new AppInitializationOptions
 {
 ConfigDirectory = Path.Combine(_root, "config"),
 DefaultDataDirectory = Path.Combine(_root, "data")
};
await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
var databasePath = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
 Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
await using (var connection = await OpenAsync(databasePath))
 {
 await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT INTO reference_anchors(anchor_id,novel_id,title,author,source_path,source_kind,license_status,source_file_hash,build_version,status,created_at,updated_at)
 VALUES(1,7,'A','author','a.txt','txt','user_owned','hash','v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT INTO reference_text_nodes(node_id,anchor_id,node_type,sequence_index,depth,start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES('node-1',1,'sentence',0,0,0,4,4,'node-hash','text','2026-07-10T00:00:00Z');
 INSERT INTO reference_analysis_runs(run_id,anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,started_at)
 VALUES('run-1',1,'v1','v1','fake','fake','sentence','completed','2026-07-10T00:00:00Z');
 INSERT INTO reference_feature_observations(observation_id,node_id,node_type,run_id,anchor_id,feature_family,feature_key,value_kind,value_text,confidence,review_state,validity_state,created_at)
 VALUES('obs-1','node-1','sentence','run-1',1,'syntax','shape','text','short',0.9,'confirmed','active','2026-07-10T00:00:00Z'),
 ('obs-2','node-1','sentence','run-1',1,'emotion','mode','text','tense',0.8,'confirmed','active','2026-07-10T00:00:00Z');
 INSERT INTO reference_technique_specimens(specimen_id,source_node_id,source_anchor_id,analysis_run_id,technique_family,technique_abstract,trigger_context,transfer_template,transfer_slots_json,effect_on_reader,applicability_conditions,failure_modes,anti_patterns,world_context_dependencies,why_it_works_json,confidence,review_state,validity_state,created_at)
 VALUES('specimen-a','node-1',1,'run-1','syntax','a','t','x','[]','e','[]','[]','[]','[]','{}',0.9,'confirmed','active','2026-07-10T00:00:00Z'),
 ('specimen-b','node-1',1,'run-1','emotion','b','t','x','[]','e','[]','[]','[]','[]','{}',0.8,'confirmed','active','2026-07-10T00:00:00Z');
 INSERT INTO reference_specimen_evidence(specimen_id,observation_id)
 VALUES('specimen-a','obs-1'),('specimen-b','obs-1'),('specimen-b','obs-2');
 INSERT INTO reference_corpus_blueprints(blueprint_id,novel_id,chapter_number,query_context_hash,assembly_strategy,coverage_score,gap_reasons_json,gap_positions_json,query_context_json,source_distribution_json,feedback_reason,created_at,updated_at)
 VALUES('blueprint-a',7,1,'q1','s',1,'[]','[]','{}','{}','','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z'),
 ('blueprint-b',7,1,'q2','s',1,'[]','[]','{}','{}','','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT INTO reference_corpus_blueprint_beats(blueprint_id,beat_id,beat_index,role_in_beat,narrative_function)
 VALUES('blueprint-a','beat-a',0,'setup','setup'),('blueprint-b','beat-b',0,'turn','turn');
 INSERT INTO reference_blueprint_beat_pieces(beat_id,node_id,observation_id,role_in_beat,sequence_index)
 VALUES('beat-a','node-1','obs-1','setup',0),('beat-b','node-1','obs-2','turn',0);
 """;
 await command.ExecuteNonQueryAsync();
 }

 var service = new SqliteReferenceCorpusService(options);
 var before = await CountAsync(databasePath, "reference_specimen_evidence") + await CountAsync(databasePath, "reference_blueprint_beat_pieces");
 var result = await service.GetCascadeImpactAsync(new(["obs-2", "obs-1", "obs-1", "missing"]), CancellationToken.None);
 var after = await CountAsync(databasePath, "reference_specimen_evidence") + await CountAsync(databasePath, "reference_blueprint_beat_pieces");

 Assert.Equal(["missing", "obs-1", "obs-2"], result.ObservationIds);
 Assert.Equal(["specimen-a", "specimen-b"], result.SpecimenIds);
 Assert.Equal(["beat-a", "beat-b"], result.BeatIds);
 Assert.Equal(["blueprint-a", "blueprint-b"], result.BlueprintIds);
 Assert.Equal(before, after);
 }

 private static async Task<SqliteConnection> OpenAsync(string path)
 {
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
 await connection.OpenAsync();
 return connection;
 }

 private static async Task<long> CountAsync(string path, string table)
 {
 await using var connection = await OpenAsync(path);
 await using var command = connection.CreateCommand();
 command.CommandText = $"SELECT COUNT(*) FROM {table};";
 return Convert.ToInt64(await command.ExecuteScalarAsync());
 }

 public void Dispose()
 {
 if (Directory.Exists(_root)) Directory.Delete(_root, true);
 }
}
