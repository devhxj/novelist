using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed record ReferenceCorpusAnalysisSnapshotBuildResult(
 ReferenceCorpusAnalysisInputSnapshot Snapshot,
 IReadOnlyList<ReferenceCorpusAnalysisWorkItemSnapshot> WorkItems);

internal sealed record ReferenceCorpusFeatureSnapshotBuildRequest(
 string SnapshotId,
 string RunId,
 long AnchorId,
 string Scope,
string AnalyzerVersion,
string ModelProvider,
string ModelId,
string ReasoningEffort,
ReferenceCorpusFrozenTokenPolicy TokenPolicy,
DateTimeOffset CreatedAt);

internal sealed record ReferenceCorpusTechniqueSnapshotBuildRequest(
 string SnapshotId,
 string RunId,
 long AnchorId,
 string SourceNodeType,
 double MinObservationConfidence,
string AnalyzerVersion,
string ModelProvider,
string ModelId,
string ReasoningEffort,
ReferenceCorpusFrozenTokenPolicy TokenPolicy,
DateTimeOffset CreatedAt,
string? DependencyJobId = null,
string? DependencyRunId = null,
string? DependencyInputSnapshotId = null);

internal sealed class ReferenceCorpusAnalysisInputSnapshotBuilder
{
 public async ValueTask<ReferenceCorpusAnalysisSnapshotBuildResult> BuildFeatureAsync(
 SqliteConnection connection,
 ReferenceCorpusFeatureSnapshotBuildRequest request,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(connection);
 ArgumentNullException.ThrowIfNull(request);
ValidateFeature(request);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var families = request.Scope == ReferenceCorpusNodeTypes.Sentence
 ? ReferenceCorpusFeatureFamilies.SentenceFamilies
 : ReferenceCorpusFeatureFamilies.PassageFamilies;
 var nodes = await ReadFeatureNodesAsync(connection, transaction, request.AnchorId, request.Scope, cancellationToken);
 if (nodes.Count == 0)
 {
 throw new InvalidOperationException($"No eligible {request.Scope} nodes were available for a frozen analysis snapshot.");
 }

 var workItems = new List<ReferenceCorpusAnalysisWorkItemSnapshot>(nodes.Count * families.Count);
 foreach (var node in nodes)
 {
 foreach (var family in families)
 {
 var schema = ReferenceCorpusFeatureFamilySchemaRegistry.Get(family);
 var payload = new ReferenceCorpusFrozenFeatureWorkItem(
 ReferenceCorpusAnalysisFrozenInputVersions.FeatureV1,
 request.RunId,
 request.AnchorId,
 node.NodeId,
 node.ChapterNodeId,
 node.NodeType,
 node.Text,
 node.TextHash,
 family,
 node.Context,
request.AnalyzerVersion,
schema.SchemaVersion,
 new(request.ModelProvider, request.ModelId, request.ReasoningEffort),
 request.TokenPolicy);
 var encoded = ReferenceCorpusAnalysisFrozenInputCodec.Serialize(payload);
 workItems.Add(new(
 workItems.Count,
 node.NodeId,
 node.ChapterNodeId,
 family,
 node.TextHash,
 encoded.Json,
 encoded.Hash));
 }
 }

 var result = BuildResult(
 request.SnapshotId,
 request.AnchorId,
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis,
 request.Scope,
 nodes.Select(node => (node.NodeId, node.TextHash)),
 families,
 ReferenceCorpusFeatureFamilySchemaVersions.V1,
 request.AnalyzerVersion,
 request.ModelProvider,
 request.ModelId,
 nodes.Count,
workItems,
request.CreatedAt);
 await transaction.CommitAsync(cancellationToken);
 return result;
 }

 public async ValueTask<ReferenceCorpusAnalysisSnapshotBuildResult> BuildTechniqueAsync(
 SqliteConnection connection,
 ReferenceCorpusTechniqueSnapshotBuildRequest request,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(connection);
 ArgumentNullException.ThrowIfNull(request);
ValidateTechnique(request);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var nodes = await ReadTechniqueNodesAsync(connection, transaction, request, cancellationToken);
 if (nodes.Count == 0)
 {
 throw new InvalidOperationException("No eligible technique evidence was available for a frozen analysis snapshot.");
 }

 var workItems = new List<ReferenceCorpusAnalysisWorkItemSnapshot>(nodes.Count);
 foreach (var node in nodes)
 {
 var payload = new ReferenceCorpusFrozenTechniqueWorkItem(
 ReferenceCorpusAnalysisFrozenInputVersions.TechniqueV1,
 request.RunId,
 request.AnchorId,
 node.NodeId,
 node.ChapterNodeId,
 node.NodeType,
 node.Text,
 node.TextHash,
 node.Observations,
 ReferenceCorpusAnalysisFrozenInputCodec.ComputeEvidenceSetHash(node.Observations),
request.AnalyzerVersion,
ReferenceCorpusTechniqueSpecimenSchemaVersions.V1,
new(request.ModelProvider, request.ModelId, request.ReasoningEffort),
 request.TokenPolicy,
 request.DependencyJobId,
 request.DependencyRunId,
 request.DependencyInputSnapshotId);
 var encoded = ReferenceCorpusAnalysisFrozenInputCodec.Serialize(payload);
 workItems.Add(new(
 workItems.Count,
 node.NodeId,
 node.ChapterNodeId,
 "technique_specimen",
 node.TextHash,
 encoded.Json,
 encoded.Hash));
 }

 var result = BuildResult(
 request.SnapshotId,
 request.AnchorId,
 ReferenceCorpusAnalysisJobKinds.TechniqueSpecimen,
 request.SourceNodeType,
 nodes.Select(node => (node.NodeId, node.TextHash)),
 ["technique_specimen"],
 ReferenceCorpusTechniqueSpecimenSchemaVersions.V1,
 request.AnalyzerVersion,
 request.ModelProvider,
 request.ModelId,
 nodes.Count,
workItems,
request.CreatedAt);
 await transaction.CommitAsync(cancellationToken);
 return result;
 }

 private static ReferenceCorpusAnalysisSnapshotBuildResult BuildResult(
 string snapshotId,
 long anchorId,
 string stage,
 string scope,
 IEnumerable<(string NodeId, string TextHash)> nodes,
 IReadOnlyList<string> families,
 string schemaVersion,
 string analyzerVersion,
 string modelProvider,
 string modelId,
 int totalNodes,
 IReadOnlyList<ReferenceCorpusAnalysisWorkItemSnapshot> workItems,
 DateTimeOffset createdAt)
 {
 var nodeSetHash = HashJson(nodes.OrderBy(item => item.NodeId, StringComparer.Ordinal).ToArray());
 var familySetJson = JsonSerializer.Serialize(families.Order(StringComparer.Ordinal));
 return new(
 new(
 snapshotId,
 anchorId,
 stage,
 scope,
 nodeSetHash,
 familySetJson,
 schemaVersion,
 analyzerVersion,
 modelProvider,
 modelId,
 totalNodes,
 workItems.Count,
 createdAt),
 workItems);
 }

private static async ValueTask<IReadOnlyList<FrozenFeatureNode>> ReadFeatureNodesAsync(
SqliteConnection connection,
 SqliteTransaction transaction,
 long anchorId,
 string scope,
 CancellationToken cancellationToken)
 {
 var rows = await ReadNodeRowsAsync(connection, transaction, anchorId, scope, cancellationToken);
 var result = new List<FrozenFeatureNode>(rows.Count);
 foreach (var row in rows)
 {
 var context = scope == ReferenceCorpusNodeTypes.Passage
 ? await ReadPassageContextAsync(connection, transaction, anchorId, row, cancellationToken)
 : ReferenceCorpusFeatureAnalysisContext.Empty;
 result.Add(new(row.NodeId, row.ChapterNodeId, row.NodeType, row.Text, row.TextHash, context));
 }
 return result;
 }

private static async ValueTask<IReadOnlyList<NodeRow>> ReadNodeRowsAsync(
SqliteConnection connection,
 SqliteTransaction transaction,
 long anchorId,
 string nodeType,
 CancellationToken cancellationToken)
 {
await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = nodeType == ReferenceCorpusNodeTypes.Passage
 ? """
 SELECT n.node_id,n.parent_node_id,n.node_type,n.chapter_index,n.start_offset,n.end_offset,n.text_hash,n.text,
 c.node_id,s.segment_id,s.segment_type
 FROM reference_text_nodes n
 LEFT JOIN reference_text_nodes c ON c.anchor_id=n.anchor_id AND c.node_type='chapter' AND c.chapter_index=n.chapter_index
 INNER JOIN reference_source_segments s ON s.anchor_id=n.anchor_id AND s.node_id=n.node_id AND s.segment_type='paragraph'
 WHERE n.anchor_id=$anchor_id AND n.node_type='passage'
 ORDER BY n.chapter_index,n.start_offset,n.sequence_index,n.node_id;
 """
 : """
 SELECT n.node_id,n.parent_node_id,n.node_type,n.chapter_index,n.start_offset,n.end_offset,n.text_hash,n.text,
 c.node_id,NULL,NULL
 FROM reference_text_nodes n
 LEFT JOIN reference_text_nodes c ON c.anchor_id=n.anchor_id AND c.node_type='chapter' AND c.chapter_index=n.chapter_index
 WHERE n.anchor_id=$anchor_id AND n.node_type=$node_type
 ORDER BY n.chapter_index,n.start_offset,n.sequence_index,n.node_id;
 """;
 command.Parameters.AddWithValue("$anchor_id", anchorId);
 command.Parameters.AddWithValue("$node_type", nodeType);
 var result = new List<NodeRow>();
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 result.Add(new(
 reader.GetString(0),
 reader.IsDBNull(1) ? null : reader.GetString(1),
 reader.GetString(2),
 reader.IsDBNull(3) ? null : reader.GetInt32(3),
 reader.GetInt32(4),
 reader.GetInt32(5),
 reader.GetString(6),
 reader.GetString(7),
 reader.IsDBNull(8) ? null : reader.GetString(8),
 reader.IsDBNull(9) ? null : reader.GetString(9),
 reader.IsDBNull(10) ? null : reader.GetString(10)));
 }
 return result;
 }

private static async ValueTask<ReferenceCorpusFeatureAnalysisContext> ReadPassageContextAsync(
SqliteConnection connection,
 SqliteTransaction transaction,
 long anchorId,
 NodeRow row,
 CancellationToken cancellationToken)
 {
 var parent = row.ParentNodeId is null ? null : await ReadContextNodeAsync(connection, transaction, "n.node_id=$value", anchorId, row.ParentNodeId, cancellationToken);
 var chapter = row.ChapterNodeId is null ? null : await ReadContextNodeAsync(connection, transaction, "n.node_id=$value", anchorId, row.ChapterNodeId, cancellationToken);
 var scene = await ReadContextNodeAsync(
connection,
 transaction,
 "s.segment_type='scene' AND s.chapter_index=$chapter_index AND s.start_offset<=$start_offset AND s.end_offset>=$end_offset",
 anchorId,
 null,
 cancellationToken,
 row.ChapterIndex,
 row.StartOffset,
 row.EndOffset,
 "(s.end_offset-s.start_offset),s.start_offset");
 var previous = await ReadSiblingParagraphAsync(connection, transaction, anchorId, row, previous: true, cancellationToken);
 var next = await ReadSiblingParagraphAsync(connection, transaction, anchorId, row, previous: false, cancellationToken);
 return new(row.SourceSegmentId, row.SourceSegmentType, parent, chapter, scene, previous, next);
 }

private static async ValueTask<ReferenceCorpusFeatureAnalysisContextNode?> ReadSiblingParagraphAsync(
SqliteConnection connection,
 SqliteTransaction transaction,
 long anchorId,
 NodeRow row,
 bool previous,
 CancellationToken cancellationToken)
 {
 if (row.ChapterIndex is null) return null;
 var comparison = previous ? "<" : ">";
 var order = previous ? "s.start_offset DESC,s.segment_index DESC" : "s.start_offset,s.segment_index";
 return await ReadContextNodeAsync(
connection,
 transaction,
 $"s.segment_type='paragraph' AND s.chapter_index=$chapter_index AND n.node_id<>$node_id AND s.start_offset{comparison}$start_offset",
 anchorId,
 row.NodeId,
 cancellationToken,
 row.ChapterIndex,
 row.StartOffset,
 null,
 order);
 }

private static async ValueTask<ReferenceCorpusFeatureAnalysisContextNode?> ReadContextNodeAsync(
SqliteConnection connection,
 SqliteTransaction transaction,
 string predicate,
 long anchorId,
 string? value,
 CancellationToken cancellationToken,
 int? chapterIndex = null,
 int? startOffset = null,
 int? endOffset = null,
 string orderBy = "n.start_offset,n.sequence_index,n.node_id")
 {
await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = $"SELECT n.node_id,n.node_type,s.segment_id,s.segment_type,n.chapter_index,n.start_offset,n.end_offset,n.text_hash,n.text FROM reference_text_nodes n LEFT JOIN reference_source_segments s ON s.node_id=n.node_id WHERE n.anchor_id=$anchor_id AND {predicate} ORDER BY {orderBy} LIMIT 1;";
 command.Parameters.AddWithValue("$anchor_id", anchorId);
 command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
 command.Parameters.AddWithValue("$node_id", (object?)value ?? DBNull.Value);
 command.Parameters.AddWithValue("$chapter_index", (object?)chapterIndex ?? DBNull.Value);
 command.Parameters.AddWithValue("$start_offset", (object?)startOffset ?? DBNull.Value);
 command.Parameters.AddWithValue("$end_offset", (object?)endOffset ?? DBNull.Value);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 if (!await reader.ReadAsync(cancellationToken)) return null;
 var text = reader.GetString(8).Trim();
 return new(
 reader.GetString(0),
 reader.GetString(1),
 reader.IsDBNull(2) ? null : reader.GetString(2),
 reader.IsDBNull(3) ? null : reader.GetString(3),
 reader.IsDBNull(4) ? null : reader.GetInt32(4),
 reader.GetInt32(5),
 reader.GetInt32(6),
 reader.GetString(7),
 text.Length <= 320 ? text : text[..320]);
 }

private static async ValueTask<IReadOnlyList<FrozenTechniqueNode>> ReadTechniqueNodesAsync(
SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusTechniqueSnapshotBuildRequest request,
 CancellationToken cancellationToken)
 {
await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 SELECT n.node_id,n.node_type,n.text,n.text_hash,c.node_id,
 o.observation_id,o.feature_family,o.feature_key,o.value_kind,o.value_text,o.value_num,
 o.value_bool,o.value_json,o.intensity,o.confidence,o.evidence_start,o.evidence_end,o.explanation
 FROM reference_text_nodes n
 LEFT JOIN reference_text_nodes c ON c.anchor_id=n.anchor_id AND c.node_type='chapter' AND c.chapter_index=n.chapter_index
 INNER JOIN reference_feature_observations o ON o.node_id=n.node_id
 WHERE n.anchor_id=$anchor_id AND n.node_type=$node_type
 AND o.anchor_id=n.anchor_id AND o.node_type=n.node_type
 AND o.run_id=$dependency_run_id
 AND o.validity_state='active' AND o.review_state<>'rejected'
 AND o.superseded_by_run_id IS NULL AND o.confidence>=$confidence
 ORDER BY n.chapter_index,n.start_offset,n.sequence_index,n.node_id,o.feature_family,o.feature_key,o.observation_id;
 """;
 command.Parameters.AddWithValue("$anchor_id", request.AnchorId);
command.Parameters.AddWithValue("$node_type", request.SourceNodeType);
 command.Parameters.AddWithValue("$dependency_run_id", request.DependencyRunId!);
command.Parameters.AddWithValue("$confidence", request.MinObservationConfidence);
 var builders = new Dictionary<string, TechniqueNodeBuilder>(StringComparer.Ordinal);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 var nodeId = reader.GetString(0);
 if (!builders.TryGetValue(nodeId, out var builder))
 {
 builder = new(nodeId, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
 builders.Add(nodeId, builder);
 }
 builder.Observations.Add(new(
 reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
 reader.IsDBNull(9) ? null : reader.GetString(9),
 reader.IsDBNull(10) ? null : reader.GetDouble(10),
 reader.IsDBNull(11) ? null : reader.GetInt32(11) != 0,
 reader.IsDBNull(12) ? null : reader.GetString(12),
 reader.IsDBNull(13) ? null : reader.GetDouble(13),
 reader.GetDouble(14),
 reader.IsDBNull(15) ? null : reader.GetInt32(15),
 reader.IsDBNull(16) ? null : reader.GetInt32(16),
 reader.IsDBNull(17) ? null : reader.GetString(17)));
 }
 return builders.Values.Select(item => new FrozenTechniqueNode(item.NodeId, item.ChapterNodeId, item.NodeType, item.Text, item.TextHash, item.Observations)).ToArray();
 }

 private static string HashJson<T>(T value)
 {
 var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
 return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
 }

 private static void ValidateFeature(ReferenceCorpusFeatureSnapshotBuildRequest request)
 {
 if (request.AnchorId <= 0 || request.Scope is not ReferenceCorpusNodeTypes.Sentence and not ReferenceCorpusNodeTypes.Passage)
 throw new ArgumentException("Feature snapshot request is invalid.", nameof(request));
 }

 private static void ValidateTechnique(ReferenceCorpusTechniqueSnapshotBuildRequest request)
 {
 if (request.AnchorId <= 0 || request.SourceNodeType is not ReferenceCorpusNodeTypes.Sentence and not ReferenceCorpusNodeTypes.Passage || request.MinObservationConfidence is < 0 or > 1 ||
 string.IsNullOrWhiteSpace(request.DependencyJobId) || string.IsNullOrWhiteSpace(request.DependencyRunId) || string.IsNullOrWhiteSpace(request.DependencyInputSnapshotId))
throw new ArgumentException("Technique snapshot request is invalid.", nameof(request));
 }

 private sealed record NodeRow(string NodeId,string? ParentNodeId,string NodeType,int? ChapterIndex,int StartOffset,int EndOffset,string TextHash,string Text,string? ChapterNodeId,string? SourceSegmentId,string? SourceSegmentType);
 private sealed record FrozenFeatureNode(string NodeId,string? ChapterNodeId,string NodeType,string Text,string TextHash,ReferenceCorpusFeatureAnalysisContext Context);
 private sealed record FrozenTechniqueNode(string NodeId,string? ChapterNodeId,string NodeType,string Text,string TextHash,IReadOnlyList<ReferenceCorpusTechniqueObservationEvidence> Observations);
 private sealed record TechniqueNodeBuilder(string NodeId,string NodeType,string Text,string TextHash,string? ChapterNodeId)
 {
 public List<ReferenceCorpusTechniqueObservationEvidence> Observations { get; } = [];
 }
}
