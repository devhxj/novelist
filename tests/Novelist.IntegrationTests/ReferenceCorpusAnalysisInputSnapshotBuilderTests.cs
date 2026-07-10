using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusAnalysisInputSnapshotBuilderTests
{
 [Fact]
 public async Task BuildFeatureSentenceFreezesTextAndExpandsEverySentenceFamily()
 {
 await using var connection = await OpenFixtureAsync();
 var result = await new ReferenceCorpusAnalysisInputSnapshotBuilder().BuildFeatureAsync(
 connection,
 new("snapshot-feature", "run-feature", 101, ReferenceCorpusNodeTypes.Sentence, "feature-v1", "fake", "model-a", "medium", new(4, 512, 512, 4096), DateTimeOffset.Parse("2026-07-10T00:00:00Z")),
 CancellationToken.None);

 Assert.Equal(1, result.Snapshot.TotalNodes);
 Assert.Equal(ReferenceCorpusFeatureFamilies.SentenceFamilies.Count, result.WorkItems.Count);
 Assert.Equal(Enumerable.Range(0, result.WorkItems.Count), result.WorkItems.Select(item => item.Ordinal));
 foreach (var workItem in result.WorkItems)
 {
 var payload = ReferenceCorpusAnalysisFrozenInputCodec.Deserialize<ReferenceCorpusFrozenFeatureWorkItem>(workItem.InputPayloadJson, workItem.InputPayloadHash);
 Assert.Equal("原始冻结句子。", payload.NodeText);
 Assert.Equal("hash-sentence", payload.NodeTextHash);
 Assert.Equal(workItem.FeatureFamily, payload.FeatureFamily);
 Assert.Equal("model-a", payload.Model.ModelId);
 }
 }

 [Fact]
 public async Task BuildTechniqueKeepsFrozenDependencyEvidenceAfterLiveObservationChanges()
 {
 await using var connection = await OpenFixtureAsync();
 var builder = new ReferenceCorpusAnalysisInputSnapshotBuilder();
 var result = await builder.BuildTechniqueAsync(
 connection,
 new("snapshot-technique", "run-technique", 101, ReferenceCorpusNodeTypes.Sentence, 0.7, "technique-v1", "fake", "model-a", "medium", new(4, 512, 512, 4096), DateTimeOffset.Parse("2026-07-10T00:00:00Z"), "feature-job", "source-run", "snapshot-feature"),
 CancellationToken.None);
 var workItem = Assert.Single(result.WorkItems);

 await ExecuteAsync(connection, "UPDATE reference_feature_observations SET value_text='changed',review_state='rejected'; UPDATE reference_text_nodes SET text='changed live text',text_hash='changed-hash' WHERE node_id='node-sentence';");

 var payload = ReferenceCorpusAnalysisFrozenInputCodec.Deserialize<ReferenceCorpusFrozenTechniqueWorkItem>(workItem.InputPayloadJson, workItem.InputPayloadHash);
 Assert.Equal("原始冻结句子。", payload.NodeText);
 Assert.Equal(["obs-emotion", "obs-rhetoric"], payload.Observations.Select(item => item.ObservationId));
 Assert.Equal("suppressed", payload.Observations[0].ValueText);
 Assert.Equal("feature-job", payload.DependencyJobId);
 Assert.Equal("source-run", payload.DependencyRunId);
 Assert.Equal("snapshot-feature", payload.DependencyInputSnapshotId);
Assert.Equal(ReferenceCorpusAnalysisFrozenInputCodec.ComputeEvidenceSetHash(payload.Observations), payload.EvidenceSetHash);
}

 [Fact]
 public async Task BuildFeaturePassageFreezesParentChapterSceneAndSiblingContext()
 {
 await using var connection = await OpenPassageFixtureAsync();
 var result = await new ReferenceCorpusAnalysisInputSnapshotBuilder().BuildFeatureAsync(
 connection,
 new("snapshot-passage", "run-passage", 101, ReferenceCorpusNodeTypes.Passage, "feature-v1",
 "fake", "model-a", "medium", new(4, 512, 512, 4096), DateTimeOffset.Parse("2026-07-10T00:00:00Z")),
 CancellationToken.None);
 var workItem = result.WorkItems.First(item => item.NodeId == "node-target");

 await ExecuteAsync(connection, """
 UPDATE reference_text_nodes SET text='changed',text_hash='changed'
 WHERE node_id IN ('node-scene','node-chapter','node-prev','node-target','node-next');
 """);

 var payload = ReferenceCorpusAnalysisFrozenInputCodec.Deserialize<ReferenceCorpusFrozenFeatureWorkItem>(
 workItem.InputPayloadJson,
 workItem.InputPayloadHash);
 Assert.Equal("目标段落", payload.NodeText);
 Assert.Equal("segment-target", payload.Context.SourceSegmentId);
 Assert.Equal("node-scene", payload.Context.Parent!.NodeId);
 Assert.Equal("场景全文", payload.Context.Parent.TextPreview);
 Assert.Equal("node-chapter", payload.Context.Chapter!.NodeId);
 Assert.Equal("第一章", payload.Context.Chapter.TextPreview);
 Assert.Equal("node-scene", payload.Context.ContainingScene!.NodeId);
 Assert.Equal("上一段", payload.Context.PreviousParagraph!.TextPreview);
 Assert.Equal("下一段", payload.Context.NextParagraph!.TextPreview);
 }

private static async ValueTask<SqliteConnection> OpenFixtureAsync()
 {
 var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
await connection.OpenAsync();
await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
await ExecuteAsync(connection, """
 INSERT INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,source_file_hash,build_version,status,created_at,updated_at)
 VALUES (101,1,'Book','Author','book.txt','txt','allowed','source-hash','v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES
 ('node-chapter',101,NULL,'chapter',0,0,1,0,100,100,'hash-chapter','第一章','2026-07-10T00:00:00Z'),
 ('node-sentence',101,'node-chapter','sentence',1,1,1,10,18,8,'hash-sentence','原始冻结句子。','2026-07-10T00:00:00Z');
 INSERT INTO reference_analysis_runs
 (run_id,anchor_id,analyzer_version,schema_version,model_provider,model_id,scope,status,tokens_spent,started_at,observation_count)
 VALUES ('source-run',101,'feature-v1','v1','fake','model-a','sentence','completed',10,'2026-07-10T00:00:00Z',2);
 INSERT INTO reference_feature_observations
 (observation_id,node_id,node_type,run_id,anchor_id,feature_family,feature_key,value_kind,value_text,confidence,evidence_start,evidence_end,review_state,validity_state,created_at)
 VALUES
 ('obs-emotion','node-sentence','sentence','source-run',101,'emotion','emotion_mode','text','suppressed',0.9,0,4,'confirmed','active','2026-07-10T00:00:00Z'),
 ('obs-rhetoric','node-sentence','sentence','source-run',101,'rhetoric','ellipsis','text','silence',0.85,4,8,'unverified','active','2026-07-10T00:00:00Z');
 """);
return connection;
}

 private static async ValueTask<SqliteConnection> OpenPassageFixtureAsync()
 {
 var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
 await connection.OpenAsync();
 await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
await ExecuteAsync(connection, """
 CREATE TABLE reference_source_segments (
 segment_id TEXT PRIMARY KEY,anchor_id INTEGER NOT NULL,chapter_index INTEGER NOT NULL,
 chapter_title TEXT NOT NULL,segment_type TEXT NOT NULL,segment_index INTEGER NOT NULL,
 parent_segment_id TEXT NOT NULL,start_offset INTEGER NOT NULL,end_offset INTEGER NOT NULL,
 text TEXT NOT NULL,text_hash TEXT NOT NULL,node_id TEXT);
 INSERT INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,source_file_hash,build_version,status,created_at,updated_at)
 VALUES (101,1,'Book','Author','book.txt','txt','allowed','source-hash','v1','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES
 ('node-chapter',101,NULL,'chapter',0,0,1,0,120,120,'hash-chapter','第一章','2026-07-10T00:00:00Z'),
 ('node-scene',101,'node-chapter','scene',1,1,1,0,120,120,'hash-scene','场景全文','2026-07-10T00:00:00Z'),
 ('node-prev',101,'node-scene','passage',2,2,1,10,30,20,'hash-prev','上一段','2026-07-10T00:00:00Z'),
 ('node-target',101,'node-scene','passage',3,2,1,31,60,29,'hash-target','目标段落','2026-07-10T00:00:00Z'),
 ('node-next',101,'node-scene','passage',4,2,1,61,90,29,'hash-next','下一段','2026-07-10T00:00:00Z');
 INSERT INTO reference_source_segments
 (segment_id,anchor_id,chapter_index,chapter_title,segment_type,segment_index,parent_segment_id,start_offset,end_offset,text,text_hash,node_id)
 VALUES
 ('segment-scene',101,1,'第一章','scene',0,'root',0,120,'场景全文','hash-scene','node-scene'),
 ('segment-prev',101,1,'第一章','paragraph',1,'segment-scene',10,30,'上一段','hash-prev','node-prev'),
 ('segment-target',101,1,'第一章','paragraph',2,'segment-scene',31,60,'目标段落','hash-target','node-target'),
 ('segment-next',101,1,'第一章','paragraph',3,'segment-scene',61,90,'下一段','hash-next','node-next');
 """);
 return connection;
 }

 private static async ValueTask ExecuteAsync(SqliteConnection connection, string sql)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = sql;
 await command.ExecuteNonQueryAsync();
 }
}
