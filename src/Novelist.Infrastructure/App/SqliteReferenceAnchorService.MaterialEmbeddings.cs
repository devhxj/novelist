using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceAnchorService
{
 private async ValueTask<int> ProvisionCanonicalMaterialVectorsAsync(
 string databasePath,
 long anchorId,
 EmbeddingRequestOptions embeddingOptions,
 CancellationToken cancellationToken)
 {
 var dimensions = embeddingOptions.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
 await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
 await EnsureMaterialEmbeddingSchemaAsync(connection, cancellationToken);
 var novelId = await ReadMaterialAnchorNovelIdAsync(connection, anchorId, cancellationToken);
 var sources = await ReadMaterialEmbeddingSourcesAsync(connection, novelId, [anchorId], cancellationToken);
 if (sources.Count == 0)
 {
 return 0;
 }

 var existing = await ReadMaterialEmbeddingRowsAsync(
 connection, sources, embeddingOptions.ProviderKey, embeddingOptions.ModelId, dimensions, cancellationToken);
 var missing = sources.ToArray();
 foreach (var row in await BuildMaterialEmbeddingRowsAsync(
 connection, missing, embeddingOptions, dimensions, cancellationToken))
 {
 existing[row.MaterialId] = row;
 }

 return await ProvisionMaterialEmbeddingProjectionsAsync(
 databasePath, connection, sources, existing, dimensions, cancellationToken);
 }

 private static async ValueTask<long> ReadMaterialAnchorNovelIdAsync(
 SqliteConnection connection,
 long anchorId,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT COALESCE(novel_id, 0) FROM reference_anchors WHERE anchor_id = $anchor_id;";
 command.Parameters.AddWithValue("$anchor_id", anchorId);
 var result = await command.ExecuteScalarAsync(cancellationToken);
 return result is long value ? value : Convert.ToInt64(result, CultureInfo.InvariantCulture);
 }

 public async ValueTask<ReferenceMaterialEmbeddingBackfillPayload> BackfillMaterialEmbeddingsAsync(
 BackfillReferenceMaterialEmbeddingsPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 ValidateNovelId(input.NovelId);
 var requestedAnchorIds = (input.AnchorIds ?? [])
 .Distinct()
 .OrderBy(value => value)
 .ToArray();
 foreach (var anchorId in requestedAnchorIds)
 {
 ValidateAnchorId(anchorId);
 }

 var embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken)
 ?? throw new InvalidOperationException("Reference material embedding configuration is not available.");
 var dimensions = embeddingOptions.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
 if (dimensions <= 0)
 {
 throw new InvalidOperationException("Reference material embedding dimensions must be positive.");
 }

 var databasePath = await DatabasePathAsync(cancellationToken);
 await _mutex.WaitAsync(cancellationToken);
 try
 {
 await EnsureSchemaAsync(databasePath, cancellationToken);
 await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
 await EnsureMaterialEmbeddingSchemaAsync(connection, cancellationToken);
 var alignment = await AlignLegacyMaterialsToTextNodesAsync(
 connection,
 input.NovelId,
 requestedAnchorIds,
 cancellationToken);
 var materials = await ReadMaterialEmbeddingSourcesAsync(
 connection,
 input.NovelId,
 requestedAnchorIds,
 cancellationToken);
 var existing = await ReadMaterialEmbeddingRowsAsync(
 connection,
 materials,
 embeddingOptions.ProviderKey,
 embeddingOptions.ModelId,
 dimensions,
 cancellationToken);
 var missing = materials
 .Where(material => !existing.TryGetValue(material.MaterialId, out var row) ||
 !string.Equals(row.MaterialHash, material.MaterialHash, StringComparison.Ordinal) ||
 !string.Equals(row.NodeTextHash, material.NodeTextHash, StringComparison.Ordinal))
 .ToArray();

 var built = await BuildMaterialEmbeddingRowsAsync(
 connection,
 missing,
 embeddingOptions,
 dimensions,
 cancellationToken);
 foreach (var row in built)
 {
 existing[row.MaterialId] = row;
 }

 var projectionCount = await ProvisionMaterialEmbeddingProjectionsAsync(
 databasePath,
 connection,
 materials,
 existing,
 dimensions,
 cancellationToken);
 var builtIds = built.Select(row => row.MaterialId).ToHashSet(StringComparer.Ordinal);
 var items = materials.Select(material =>
 {
 var row = existing[material.MaterialId];
 return new ReferenceMaterialEmbeddingInspectionPayload(
 MaterialId: material.MaterialId,
 AnchorId: material.AnchorId,
 NodeId: material.NodeId,
 ProviderKey: row.ProviderKey,
 ModelId: row.ModelId,
 Dimensions: row.Dimensions,
 MaterialHash: row.MaterialHash,
 NodeTextHash: row.NodeTextHash,
 EmbeddingHash: row.EmbeddingHash,
 Status: builtIds.Contains(material.MaterialId) ? "built" : "reused",
 UpdatedAt: row.UpdatedAt);
 }).ToArray();

 return new ReferenceMaterialEmbeddingBackfillPayload(
 ProviderKey: embeddingOptions.ProviderKey,
 ModelId: embeddingOptions.ModelId,
 Dimensions: dimensions,
 MaterialCount: materials.Count,
 BuiltCount: built.Count,
 ReusedCount: materials.Count - built.Count,
 AlignedSourceSegmentCount: alignment.SourceSegmentCount,
 AlignedMaterialCount: alignment.MaterialCount,
 ProjectionCount: projectionCount,
 Items: items);
 }
 finally
 {
 _mutex.Release();
 }
 }

 private static async ValueTask EnsureMaterialEmbeddingSchemaAsync(
 SqliteConnection connection,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = """
 CREATE TABLE IF NOT EXISTS reference_material_embeddings (
 embedding_id TEXT PRIMARY KEY,
 material_id TEXT NOT NULL,
 anchor_id INTEGER NOT NULL,
 node_id TEXT NOT NULL,
 provider_key TEXT NOT NULL,
 model_id TEXT NOT NULL,
 dimensions INTEGER NOT NULL,
 material_hash TEXT NOT NULL,
 node_text_hash TEXT NOT NULL,
 embedding_hash TEXT NOT NULL,
 embedding_json TEXT NOT NULL,
 updated_at TEXT NOT NULL,
 FOREIGN KEY(material_id) REFERENCES reference_materials(material_id) ON DELETE CASCADE,
 FOREIGN KEY(anchor_id) REFERENCES reference_anchors(anchor_id) ON DELETE CASCADE,
 FOREIGN KEY(node_id) REFERENCES reference_text_nodes(node_id) ON DELETE CASCADE
 );

 CREATE UNIQUE INDEX IF NOT EXISTS ux_reference_material_embeddings_generation
 ON reference_material_embeddings(material_id, provider_key, model_id, dimensions);

 CREATE INDEX IF NOT EXISTS idx_reference_material_embeddings_inspection
 ON reference_material_embeddings(provider_key, model_id, dimensions, anchor_id, material_hash, node_text_hash);
 """;
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async ValueTask<MaterialNodeAlignmentResult> AlignLegacyMaterialsToTextNodesAsync(
 SqliteConnection connection,
 long novelId,
 IReadOnlyList<long> anchorIds,
 CancellationToken cancellationToken)
 {
 var anchorFilter = BuildAnchorFilterSql(anchorIds, "alignment_anchor");
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
 var sourceSegmentCount = 0;
 await using (var segments = connection.CreateCommand())
 {
 segments.Transaction = transaction;
 segments.CommandText = $"""
 UPDATE reference_source_segments AS segment
 SET node_id = (
 SELECT node.node_id
 FROM reference_text_nodes AS node
 WHERE node.anchor_id = segment.anchor_id
 AND node.start_offset = segment.start_offset
 AND node.end_offset = segment.end_offset
 AND node.text_hash = segment.text_hash
 ORDER BY CASE node.node_type
 WHEN 'sentence' THEN 0 WHEN 'clause' THEN 1 WHEN 'passage' THEN 2 ELSE 3 END,
 node.sequence_index ASC
 LIMIT 1
 )
 WHERE segment.node_id IS NULL
 AND segment.anchor_id IN (
 SELECT anchor.anchor_id FROM reference_anchors AS anchor
 WHERE (anchor.novel_id = $novel_id OR
 ((anchor.novel_id IS NULL OR anchor.novel_id = $workspace_novel_id) AND anchor.corpus_visibility = $workspace_visibility))
 {anchorFilter}
 )
 AND EXISTS (
 SELECT 1 FROM reference_text_nodes AS node
 WHERE node.anchor_id = segment.anchor_id
 AND node.start_offset = segment.start_offset
 AND node.end_offset = segment.end_offset
 AND node.text_hash = segment.text_hash
 );
 """;
 AddMaterialScopeParameters(segments, novelId);
 AddAnchorFilterParameters(segments, anchorIds, "alignment_anchor");
 sourceSegmentCount = await segments.ExecuteNonQueryAsync(cancellationToken);
 }

 var materialCount = 0;
 await using (var materials = connection.CreateCommand())
 {
 materials.Transaction = transaction;
 materials.CommandText = $"""
 UPDATE reference_materials AS material
 SET node_id = (
 SELECT segment.node_id
 FROM reference_source_segments AS segment
 WHERE segment.segment_id = material.source_segment_id
 AND segment.anchor_id = material.anchor_id
 AND segment.node_id IS NOT NULL
 )
 WHERE material.node_id IS NULL
 AND material.anchor_id IN (
 SELECT anchor.anchor_id FROM reference_anchors AS anchor
 WHERE (anchor.novel_id = $novel_id OR
 ((anchor.novel_id IS NULL OR anchor.novel_id = $workspace_novel_id) AND anchor.corpus_visibility = $workspace_visibility))
 {anchorFilter}
 )
 AND EXISTS (
 SELECT 1 FROM reference_source_segments AS segment
 WHERE segment.segment_id = material.source_segment_id
 AND segment.anchor_id = material.anchor_id
 AND segment.node_id IS NOT NULL
 );
 """;
 AddMaterialScopeParameters(materials, novelId);
 AddAnchorFilterParameters(materials, anchorIds, "alignment_anchor");
 materialCount = await materials.ExecuteNonQueryAsync(cancellationToken);
 }

 await transaction.CommitAsync(cancellationToken);
 return new MaterialNodeAlignmentResult(sourceSegmentCount, materialCount);
 }

 private static async ValueTask<IReadOnlyList<MaterialEmbeddingSource>> ReadMaterialEmbeddingSourcesAsync(
 SqliteConnection connection,
 long novelId,
 IReadOnlyList<long> anchorIds,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 var anchorFilter = BuildAnchorFilterSql(anchorIds, "source_anchor");
 command.CommandText = $"""
 SELECT material.material_id, material.anchor_id, material.node_id, material.text,
 material.source_hash, node.text_hash, material.rowid
 FROM reference_materials AS material
 INNER JOIN reference_anchors AS anchor ON anchor.anchor_id = material.anchor_id
 INNER JOIN reference_text_nodes AS node
 ON node.node_id = material.node_id AND node.anchor_id = material.anchor_id
 WHERE material.archived_at IS NULL
 AND (anchor.novel_id = $novel_id OR
 ((anchor.novel_id IS NULL OR anchor.novel_id = $workspace_novel_id) AND anchor.corpus_visibility = $workspace_visibility))
 {anchorFilter}
 ORDER BY material.anchor_id ASC, material.material_id ASC;
 """;
AddMaterialScopeParameters(command, novelId);
 AddAnchorFilterParameters(command, anchorIds, "source_anchor");
 var result = new List<MaterialEmbeddingSource>();
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 result.Add(new MaterialEmbeddingSource(
 reader.GetString(0),
 reader.GetInt64(1),
 reader.GetString(2),
 reader.GetString(3),
 reader.GetString(4),
 reader.GetString(5),
 reader.GetInt64(6)));
 }

 return result;
 }

 private static async ValueTask<Dictionary<string, MaterialEmbeddingRow>> ReadMaterialEmbeddingRowsAsync(
 SqliteConnection connection,
 IReadOnlyList<MaterialEmbeddingSource> materials,
 string providerKey,
 string modelId,
 int dimensions,
 CancellationToken cancellationToken)
 {
 var result = new Dictionary<string, MaterialEmbeddingRow>(StringComparer.Ordinal);
 if (materials.Count == 0)
 {
 return result;
 }

 await using var command = connection.CreateCommand();
 var names = new List<string>(materials.Count);
 for (var index = 0; index < materials.Count; index++)
 {
 var name = "$material_" + index.ToString(CultureInfo.InvariantCulture);
 names.Add(name);
 command.Parameters.AddWithValue(name, materials[index].MaterialId);
 }

 command.CommandText = $"""
 SELECT material_id, anchor_id, node_id, provider_key, model_id, dimensions,
 material_hash, node_text_hash, embedding_hash, embedding_json, updated_at
 FROM reference_material_embeddings
 WHERE provider_key = $provider_key AND model_id = $model_id AND dimensions = $dimensions
 AND material_id IN ({string.Join(", ", names)});
 """;
 command.Parameters.AddWithValue("$provider_key", providerKey);
 command.Parameters.AddWithValue("$model_id", modelId);
 command.Parameters.AddWithValue("$dimensions", dimensions);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 var row = new MaterialEmbeddingRow(
 reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
 reader.GetString(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
 reader.GetString(8), reader.GetString(9), ParseTimestamp(reader.GetString(10)));
 result[row.MaterialId] = row;
 }

 return result;
 }

 private async ValueTask<IReadOnlyList<MaterialEmbeddingRow>> BuildMaterialEmbeddingRowsAsync(
 SqliteConnection connection,
 IReadOnlyList<MaterialEmbeddingSource> materials,
 EmbeddingRequestOptions options,
 int dimensions,
 CancellationToken cancellationToken)
 {
 var result = new List<MaterialEmbeddingRow>(materials.Count);
 for (var offset = 0; offset < materials.Count; offset += EmbeddingBatchSize)
 {
 var batch = materials.Skip(offset).Take(EmbeddingBatchSize).ToArray();
 var response = await _embeddings.EmbedAsync(
 batch.Select(material => material.Text).ToArray(),
 options with { Dimensions = dimensions, InputKind = BuiltinOnnxEmbeddingModel.DocumentInputKind },
 cancellationToken);
 if (response.Items.Count != batch.Length || response.Dimensions != dimensions)
 {
 throw new InvalidOperationException("Reference material embedding response does not match the requested batch generation.");
 }

 foreach (var item in response.Items.OrderBy(item => item.Index))
 {
 if (item.Index < 0 || item.Index >= batch.Length || item.Vector.Count != dimensions)
 {
 throw new InvalidOperationException("Reference material embedding response contains an invalid item.");
 }

 var source = batch[item.Index];
 var embeddingJson = JsonSerializer.Serialize(item.Vector);
 var updatedAt = DateTimeOffset.UtcNow;
 var row = new MaterialEmbeddingRow(
 source.MaterialId, source.AnchorId, source.NodeId, options.ProviderKey, options.ModelId,
 dimensions, source.MaterialHash, source.NodeTextHash, HashMaterialEmbedding(embeddingJson),
 embeddingJson, updatedAt);
 await UpsertMaterialEmbeddingRowAsync(connection, row, cancellationToken);
 result.Add(row);
 }
 }

 return result;
 }

 private static async ValueTask UpsertMaterialEmbeddingRowAsync(
 SqliteConnection connection,
 MaterialEmbeddingRow row,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT INTO reference_material_embeddings
 (embedding_id, material_id, anchor_id, node_id, provider_key, model_id, dimensions,
 material_hash, node_text_hash, embedding_hash, embedding_json, updated_at)
 VALUES
 ($embedding_id, $material_id, $anchor_id, $node_id, $provider_key, $model_id, $dimensions,
 $material_hash, $node_text_hash, $embedding_hash, $embedding_json, $updated_at)
 ON CONFLICT(material_id, provider_key, model_id, dimensions) DO UPDATE SET
 anchor_id = excluded.anchor_id,
 node_id = excluded.node_id,
 material_hash = excluded.material_hash,
 node_text_hash = excluded.node_text_hash,
 embedding_hash = excluded.embedding_hash,
 embedding_json = excluded.embedding_json,
 updated_at = excluded.updated_at;
 """;
 command.Parameters.AddWithValue("$embedding_id", "material-embedding-" + HashMaterialEmbedding(string.Join('\u001f', row.MaterialId, row.ProviderKey, row.ModelId, row.Dimensions.ToString(CultureInfo.InvariantCulture)))[..24]);
 command.Parameters.AddWithValue("$material_id", row.MaterialId);
 command.Parameters.AddWithValue("$anchor_id", row.AnchorId);
 command.Parameters.AddWithValue("$node_id", row.NodeId);
 command.Parameters.AddWithValue("$provider_key", row.ProviderKey);
 command.Parameters.AddWithValue("$model_id", row.ModelId);
 command.Parameters.AddWithValue("$dimensions", row.Dimensions);
 command.Parameters.AddWithValue("$material_hash", row.MaterialHash);
 command.Parameters.AddWithValue("$node_text_hash", row.NodeTextHash);
 command.Parameters.AddWithValue("$embedding_hash", row.EmbeddingHash);
 command.Parameters.AddWithValue("$embedding_json", row.EmbeddingJson);
 command.Parameters.AddWithValue("$updated_at", row.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private async ValueTask<int> ProvisionMaterialEmbeddingProjectionsAsync(
 string databasePath,
 SqliteConnection connection,
 IReadOnlyList<MaterialEmbeddingSource> materials,
 IReadOnlyDictionary<string, MaterialEmbeddingRow> rows,
 int dimensions,
 CancellationToken cancellationToken)
 {
 var total = 0;
 foreach (var group in materials.GroupBy(material => material.AnchorId).OrderBy(group => group.Key))
 {
 var vectors = group.Select(material => new SqliteVecVectorRecord(
 material.RowId,
 material.MaterialId,
 JsonSerializer.Deserialize<float[]>(rows[material.MaterialId].EmbeddingJson) ?? [])).ToArray();
 if (vectors.Any(vector => vector.Vector.Count != dimensions))
 {
 throw new InvalidOperationException("Stored reference material embedding dimensions are inconsistent.");
 }

 var tableName = SqliteVecTableProvisioner.BuildReferenceAnchorVectorTableName(group.Key, dimensions);
 await _vecProvisioner.ProvisionAsync(
 databasePath,
 new SqliteVecProvisionRequest(
 tableName,
 dimensions,
 SqliteVecTableProvisioner.BuildCreateTableSql(tableName, dimensions),
 vectors),
 cancellationToken);
 total += vectors.Length;
 }

 return total;
 }

 private static string BuildAnchorFilterSql(IReadOnlyList<long> anchorIds, string prefix)
 {
 if (anchorIds.Count == 0)
 {
 return string.Empty;
 }

 var names = new List<string>(anchorIds.Count);
 for (var index = 0; index < anchorIds.Count; index++)
 {
 var name = "$" + prefix + "_" + index.ToString(CultureInfo.InvariantCulture);
 names.Add(name);
 }

 return "AND anchor.anchor_id IN (" + string.Join(", ", names) + ")";
 }

 private static void AddAnchorFilterParameters(
 SqliteCommand command,
 IReadOnlyList<long> anchorIds,
 string prefix)
 {
 for (var index = 0; index < anchorIds.Count; index++)
 {
 var name = "$" + prefix + "_" + index.ToString(CultureInfo.InvariantCulture);
 command.Parameters.AddWithValue(name, anchorIds[index]);
 }
 }

 private static void AddMaterialScopeParameters(SqliteCommand command, long novelId)
 {
 command.Parameters.AddWithValue("$novel_id", novelId);
 command.Parameters.AddWithValue("$workspace_novel_id", WorkspaceCorpusNovelId);
 command.Parameters.AddWithValue("$workspace_visibility", ReferenceCorpusVisibilities.Workspace);
 }

 private static string HashMaterialEmbedding(string value)
 {
 return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
 }

 private sealed record MaterialNodeAlignmentResult(int SourceSegmentCount, int MaterialCount);

 private sealed record MaterialEmbeddingSource(
 string MaterialId,
 long AnchorId,
 string NodeId,
 string Text,
 string MaterialHash,
 string NodeTextHash,
 long RowId);

 private sealed record MaterialEmbeddingRow(
 string MaterialId,
 long AnchorId,
 string NodeId,
 string ProviderKey,
 string ModelId,
 int Dimensions,
 string MaterialHash,
 string NodeTextHash,
 string EmbeddingHash,
 string EmbeddingJson,
 DateTimeOffset UpdatedAt);
}
