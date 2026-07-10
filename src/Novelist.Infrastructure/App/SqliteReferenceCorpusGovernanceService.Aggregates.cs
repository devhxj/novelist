using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceCorpusGovernanceService
{
 private static readonly string[] AggregateTypes = ["style_profile", "scene_template", "world_model", "dialogue_technique"];

 public async ValueTask<IReadOnlyList<ReferenceCorpusAggregatePayload>> BuildAggregatesAsync(BuildReferenceCorpusAggregatesPayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.LibraryIds.Count == 0) throw new ArgumentException("At least one library is required.", nameof(input.LibraryIds));
return await LockedAsync(async connection =>
{
 await using var transaction = connection.BeginTransaction();
 try
 {
 var sources = await ReadAggregateSourcesAsync(connection, transaction, input.LibraryIds, input.RunId, cancellationToken);
var now = DateTimeOffset.UtcNow;
 foreach (var aggregateType in AggregateTypes)
 {
 var aggregateId = "aggregate-" + Hash(aggregateType + "|" + string.Join("|", input.LibraryIds.Order()));
 var matching = sources.Where(source => MatchesAggregate(aggregateType, source.FeatureFamily, source.TechniqueFamily)).ToArray();
 var summary = BuildSummary(aggregateType, matching);
await ExecuteAggregateAsync(connection, transaction,
 "INSERT INTO reference_aggregates(aggregate_id,aggregate_type,name,summary,sample_count,validity_state,updated_at) VALUES($id,$type,$name,$summary,$count,'active',$time) ON CONFLICT(aggregate_id) DO UPDATE SET summary=excluded.summary,sample_count=excluded.sample_count,validity_state='active',updated_at=excluded.updated_at;",
 cancellationToken, ("$id", aggregateId), ("$type", aggregateType), ("$name", AggregateName(aggregateType)),
("$summary", summary), ("$count", matching.Length), ("$time", now.ToString("O")));
 await UpsertProjectionAsync(connection, transaction, aggregateType, aggregateId, summary, matching.Length, now, cancellationToken);
 await ExecuteAggregateAsync(connection, transaction, "DELETE FROM reference_aggregate_provenance WHERE aggregate_id=$id;", cancellationToken, ("$id", aggregateId));
 foreach (var source in matching.DistinctBy(source => (source.LibraryId, source.AnchorId, source.RunId)))
 {
await ExecuteAggregateAsync(connection, transaction,
"INSERT INTO reference_aggregate_provenance(aggregate_id,aggregate_kind,library_id,anchor_id,run_id) VALUES($id,$kind,$library,$anchor,$run);",
 cancellationToken, ("$id", aggregateId), ("$kind", aggregateType), ("$library", source.LibraryId), ("$anchor", source.AnchorId), ("$run", source.RunId));
}
}
await transaction.CommitAsync(cancellationToken);
}
catch
 {
await transaction.RollbackAsync(CancellationToken.None);
throw;
}
 return await ListAggregatesCoreAsync(connection, null, cancellationToken);
}, cancellationToken);
 }

 public async ValueTask<IReadOnlyList<ReferenceCorpusAggregatePayload>> ListAggregatesAsync(ListReferenceCorpusAggregatesPayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 return await LockedAsync(connection => ListAggregatesCoreAsync(connection, input.AggregateType, cancellationToken), cancellationToken);
 }

 private static async ValueTask<IReadOnlyList<ReferenceCorpusAggregatePayload>> ListAggregatesCoreAsync(SqliteConnection connection, string? type, CancellationToken cancellationToken)
 {
 var rows = new List<AggregateRow>();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT aggregate.aggregate_id,aggregate.aggregate_type,aggregate.name,aggregate.summary,aggregate.sample_count,aggregate.validity_state,aggregate.updated_at,provenance.library_id,provenance.anchor_id FROM reference_aggregates AS aggregate LEFT JOIN reference_aggregate_provenance AS provenance ON provenance.aggregate_id=aggregate.aggregate_id WHERE ($type IS NULL OR aggregate.aggregate_type=$type) ORDER BY aggregate.aggregate_type,aggregate.aggregate_id;";
 command.Parameters.AddWithValue("$type", Db(type));
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken)) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetInt64(8)));
 return rows.GroupBy(row => new { row.Id, row.Type, row.Name, row.Summary, row.Count, row.State, row.UpdatedAt })
 .Select(group => new ReferenceCorpusAggregatePayload(group.Key.Id, group.Key.Type, group.Key.Name, group.Key.Summary, group.Key.Count,
 group.Key.State, group.Where(row => row.LibraryId is not null).Select(row => row.LibraryId!).Distinct().ToArray(),
 group.Where(row => row.AnchorId is not null).Select(row => row.AnchorId!.Value).Distinct().ToArray(), group.Key.UpdatedAt)).ToArray();
 }

 private static async ValueTask<IReadOnlyList<AggregateSource>> ReadAggregateSourcesAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<string> libraryIds, string? runId, CancellationToken cancellationToken)
 {
var sources = new List<AggregateSource>();
await using var command = connection.CreateCommand();
 command.Transaction = transaction;
var names = libraryIds.Select((_, index) => "$library" + index).ToArray();
 command.CommandText = $$"""
 WITH eligible_members AS (
 SELECT library_id,anchor_id
 FROM (
 SELECT member.library_id,member.anchor_id,
 ROW_NUMBER() OVER (
 PARTITION BY COALESCE(NULLIF(TRIM(member.dedup_group_id),''),'anchor:' || member.anchor_id)
 ORDER BY CASE COALESCE(member.source_quality,'') WHEN 'trusted' THEN 3 WHEN 'normal' THEN 2 WHEN 'low' THEN 1 ELSE 0 END DESC,
 member.library_id,member.anchor_id) AS dedup_rank
 FROM reference_library_members AS member
 JOIN reference_source_license AS license ON license.anchor_id=member.anchor_id
 WHERE member.library_id IN ({{string.Join(",", names)}})
 AND member.enabled=1
 AND license.license_state IN ('public_domain','cc','authorized')
 AND license.reuse_policy IN ('verbatim_ok','adapted_only')
 AND license.cleared_for_insertion=1
 )
 WHERE dedup_rank=1
 )
 SELECT member.library_id,member.anchor_id,observation.run_id,observation.feature_family,NULL,observation.feature_key,
 observation.value_text,observation.value_num,observation.confidence,observation.node_id,node.text,NULL,NULL,NULL
 FROM eligible_members AS member
 JOIN reference_feature_observations AS observation ON observation.anchor_id=member.anchor_id
 JOIN reference_analysis_runs AS run ON run.run_id=observation.run_id
 JOIN reference_text_nodes AS node ON node.node_id=observation.node_id
 WHERE observation.validity_state='active'
 AND observation.review_state<>'rejected'
 AND observation.superseded_by_run_id IS NULL
 AND run.status IN ('completed','partial_completed')
 AND ($run IS NULL OR observation.run_id=$run)
 UNION ALL
 SELECT member.library_id,member.anchor_id,specimen.analysis_run_id,NULL,specimen.technique_family,NULL,NULL,NULL,
 specimen.confidence,specimen.source_node_id,node.text,specimen.technique_abstract,specimen.transfer_template,specimen.world_context_dependencies
 FROM eligible_members AS member
 JOIN reference_technique_specimens AS specimen ON specimen.source_anchor_id=member.anchor_id
 JOIN reference_analysis_runs AS run ON run.run_id=specimen.analysis_run_id
 JOIN reference_text_nodes AS node ON node.node_id=specimen.source_node_id
 WHERE specimen.validity_state='active'
 AND specimen.review_state<>'rejected'
 AND specimen.superseded_by_run_id IS NULL
 AND run.status IN ('completed','partial_completed')
 AND ($run IS NULL OR specimen.analysis_run_id=$run);
 """;
for (var index = 0; index < libraryIds.Count; index++) command.Parameters.AddWithValue(names[index], libraryIds[index]);
 command.Parameters.AddWithValue("$run", Db(runId));
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken)) sources.Add(new(
 reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
 reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetDouble(7),
 reader.IsDBNull(8) ? 0 : reader.GetDouble(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
 reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
 return sources;
 }

 private static bool MatchesAggregate(string type, string? family, string? technique) => type switch
 {
 "style_profile" => family is "syntax" or "rhythm" or "pov" or "rhetoric",
 "scene_template" => family is "narrative" or "emotion" or "action",
 "world_model" => family is "character" or "sensory",
 "dialogue_technique" => technique?.Contains("dialogue", StringComparison.OrdinalIgnoreCase) == true || family == "commercial",
 _ => false
 };

 private static string BuildSummary(string type, IReadOnlyList<AggregateSource> sources) => type switch
 {
 "style_profile" => BuildStyleProfileSummary(sources),
 "scene_template" => BuildSceneTemplateSummary(sources),
 "world_model" => BuildWorldModelSummary(sources),
 "dialogue_technique" => BuildDialogueTechniqueSummary(sources),
 _ => BuildCoverageSummary(type, sources)
 };

 private static string BuildStyleProfileSummary(IReadOnlyList<AggregateSource> sources)
 {
 var numeric = sources.Where(source => source.ValueNum is not null).GroupBy(source => source.FeatureKey ?? source.FeatureFamily ?? "unknown")
 .OrderBy(group => group.Key).Select(group => $"{group.Key}={group.Average(source => source.ValueNum!.Value):0.###}").ToArray();
 var categorical = sources.Where(source => !string.IsNullOrWhiteSpace(source.ValueText))
 .GroupBy(source => (source.FeatureKey ?? source.FeatureFamily ?? "unknown", source.ValueText!))
 .OrderByDescending(group => group.Count()).ThenBy(group => group.Key.Item1).Take(5)
 .Select(group => $"{group.Key.Item1}:{group.Key.Item2}×{group.Count()}").ToArray();
 return $"{BuildCoverageSummary("style_profile", sources)} 统计特征[{string.Join("；", numeric)}]；高频模式[{string.Join("；", categorical)}]。";
 }

 private static string BuildSceneTemplateSummary(IReadOnlyList<AggregateSource> sources)
 {
 var templates = sources.Where(source => !string.IsNullOrWhiteSpace(source.ValueText))
 .GroupBy(source => (source.FeatureKey ?? source.FeatureFamily ?? "scene", source.ValueText!)).Where(group => group.Count() >= 3)
 .OrderByDescending(group => group.Count()).Select(group => $"{group.Key.Item1}/{group.Key.Item2}×{group.Count()}（例：{group.First().NodeText}）").ToArray();
 return templates.Length == 0 ? $"{BuildCoverageSummary("scene_template", sources)} 尚无达到 3 次阈值的稳定场景模板。"
 : $"{BuildCoverageSummary("scene_template", sources)} 稳定模板[{string.Join("；", templates)}]。";
 }

 private static string BuildWorldModelSummary(IReadOnlyList<AggregateSource> sources)
 {
 var facts = sources.SelectMany(source => new[] { source.ValueText, source.WorldContextDependencies })
 .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Take(12).ToArray();
 return $"{BuildCoverageSummary("world_model", sources)} 世界依赖[{string.Join("；", facts)}]。";
 }

 private static string BuildDialogueTechniqueSummary(IReadOnlyList<AggregateSource> sources)
 {
 var techniques = sources.Where(source => !string.IsNullOrWhiteSpace(source.TechniqueAbstract) || !string.IsNullOrWhiteSpace(source.TransferTemplate))
 .GroupBy(source => source.TechniqueFamily ?? "dialogue").OrderByDescending(group => group.Count())
 .Select(group => $"{group.Key}×{group.Count()}（{group.Select(source => source.TransferTemplate ?? source.TechniqueAbstract).First(value => !string.IsNullOrWhiteSpace(value))}）").ToArray();
 return $"{BuildCoverageSummary("dialogue_technique", sources)} 可迁移技法[{string.Join("；", techniques)}]。";
 }

 private static string BuildCoverageSummary(string type, IReadOnlyList<AggregateSource> sources) =>
 $"{AggregateName(type)}：覆盖 {sources.Select(source => source.AnchorId).Distinct().Count()} 个来源，共 {sources.Count} 条有效证据，平均置信度 {(sources.Count == 0 ? 0 : sources.Average(source => source.Confidence)):0.###}。";

 private static async ValueTask UpsertProjectionAsync(SqliteConnection connection, SqliteTransaction transaction, string type, string id, string summary, int count, DateTimeOffset now, CancellationToken cancellationToken)
 {
 var table = type switch { "style_profile" => "reference_corpus_style_aggregates", "scene_template" => "reference_corpus_scene_aggregates", "world_model" => "reference_corpus_world_aggregates", _ => "reference_corpus_dialogue_aggregates" };
 await ExecuteAggregateAsync(connection, transaction, $"INSERT INTO {table}(aggregate_id,summary,sample_count,validity_state,updated_at) VALUES($id,$summary,$count,'active',$time) ON CONFLICT(aggregate_id) DO UPDATE SET summary=excluded.summary,sample_count=excluded.sample_count,validity_state='active',updated_at=excluded.updated_at;", cancellationToken,
 ("$id", id), ("$summary", summary), ("$count", count), ("$time", now.ToString("O")));
 }
 private static string AggregateName(string type) => type switch { "style_profile" => "作者风格画像", "scene_template" => "场景模板库", "world_model" => "世界观摘要", _ => "对话技法" };
private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
 private static async ValueTask<int> ExecuteAggregateAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = sql;
 foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
 return await command.ExecuteNonQueryAsync(cancellationToken);
 }
private sealed record AggregateSource(
 string LibraryId, long AnchorId, string RunId, string? FeatureFamily, string? TechniqueFamily, string? FeatureKey,
 string? ValueText, double? ValueNum, double Confidence, string? NodeId, string? NodeText,
 string? TechniqueAbstract, string? TransferTemplate, string? WorldContextDependencies);
 private sealed record AggregateRow(string Id, string Type, string Name, string Summary, int Count, string State, DateTimeOffset UpdatedAt, string? LibraryId, long? AnchorId);
}
