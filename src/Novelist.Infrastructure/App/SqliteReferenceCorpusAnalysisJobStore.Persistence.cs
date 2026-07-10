using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 private const string JobColumns = """
 job_id, run_id, input_snapshot_id, novel_id, anchor_id, job_kind, input_json, input_hash,
 dependency_job_id, priority_class, priority_value, status, total_nodes, total_work_items,
 processed_work_items, succeeded_work_items, skipped_work_items, failed_work_items,
 retrying_work_items, token_budget, tokens_spent, resume_cursor, current_stage, current_chapter,
 attempt_count, max_attempts, next_attempt_at, lease_owner, lease_token, lease_acquired_at,
 lease_expires_at, heartbeat_at, pause_requested_at, cancel_requested_at, queued_at, started_at,
 completed_at, updated_at, last_error_code, last_error_message, row_version
 """;

 private static void ValidateEnqueue(
 ReferenceCorpusAnalysisInputSnapshot snapshot,
 IReadOnlyList<ReferenceCorpusAnalysisWorkItemSnapshot> workItems,
 ReferenceCorpusAnalysisJobEnqueue request)
 {
 ArgumentNullException.ThrowIfNull(snapshot);
 ArgumentNullException.ThrowIfNull(workItems);
 ArgumentNullException.ThrowIfNull(request);
 ValidateId(snapshot.InputSnapshotId, nameof(snapshot.InputSnapshotId));
 ValidateId(request.JobId, nameof(request.JobId));
 ValidateId(request.RunId, nameof(request.RunId));
 ValidateId(request.InputSnapshotId, nameof(request.InputSnapshotId));
 if (request.DependencyJobId is { } dependencyJobId)
 {
 ValidateId(dependencyJobId, nameof(request.DependencyJobId));
 if (string.Equals(dependencyJobId, request.JobId, StringComparison.Ordinal))
 {
 throw new ArgumentException("Analysis job cannot depend on itself.", nameof(request));
 }
 }

 if (snapshot.AnchorId <= 0 || request.AnchorId <= 0 || request.NovelId <= 0)
 {
 throw new ArgumentOutOfRangeException(nameof(request), "Novel and anchor identifiers must be positive.");
 }
 if (!string.Equals(snapshot.InputSnapshotId, request.InputSnapshotId, StringComparison.Ordinal)
 || snapshot.AnchorId != request.AnchorId
 || !string.Equals(snapshot.AnalysisStage, request.CurrentStage, StringComparison.Ordinal)
 || snapshot.TotalNodes != request.TotalNodes
 || snapshot.TotalWorkItems != request.TotalWorkItems
 || workItems.Count != request.TotalWorkItems)
 {
 throw new ArgumentException("Job, input snapshot, and work-item totals must describe the same frozen input.", nameof(request));
 }
 if (request.TotalNodes < 1 || request.TotalWorkItems < 1 || request.TotalNodes > request.TotalWorkItems)
 {
 throw new ArgumentOutOfRangeException(nameof(request), "Analysis jobs require at least one frozen node and work item.");
 }
 if (!ReferenceCorpusAnalysisJobKinds.All.Contains(request.JobKind, StringComparer.Ordinal))
 {
 throw new ArgumentOutOfRangeException(nameof(request), request.JobKind, "Unknown analysis job kind.");
 }
 if (!ReferenceCorpusAnalysisPriorityClasses.All.Contains(request.PriorityClass, StringComparer.Ordinal))
 {
 throw new ArgumentOutOfRangeException(nameof(request), request.PriorityClass, "Unknown analysis priority class.");
 }
 if (request.MaxAttempts is < 1 or > 20 || request.TokenBudget is < 0)
 {
 throw new ArgumentOutOfRangeException(nameof(request), "Attempt and token budgets are outside supported bounds.");
 }
 ValidateText(snapshot.AnalysisStage, nameof(snapshot.AnalysisStage), 64);
 ValidateText(snapshot.Scope, nameof(snapshot.Scope), 64);
 ValidateText(snapshot.NodeSetHash, nameof(snapshot.NodeSetHash), 256);
 ValidateText(snapshot.SchemaVersion, nameof(snapshot.SchemaVersion), 128);
 ValidateText(snapshot.AnalyzerVersion, nameof(snapshot.AnalyzerVersion), 128);
 ValidateText(snapshot.ModelProvider, nameof(snapshot.ModelProvider), 128);
 ValidateText(snapshot.ModelId, nameof(snapshot.ModelId), 256);
 ValidateText(request.InputHash, nameof(request.InputHash), 256);
 ValidateJson(snapshot.FamilySetJson, nameof(snapshot.FamilySetJson));
 ValidateJson(request.InputJson, nameof(request.InputJson));

 var uniqueWorkItems = new HashSet<(string NodeId, string Family)>(workItems.Count);
 var uniqueNodes = new HashSet<string>(StringComparer.Ordinal);
 for (var index = 0; index < workItems.Count; index++)
 {
 var item = workItems[index];
 if (item.Ordinal != index)
 {
 throw new ArgumentException("Work-item ordinals must be contiguous and zero based.", nameof(workItems));
 }
 ValidateId(item.NodeId, nameof(workItems));
 if (item.ChapterNodeId is { } chapterNodeId)
 {
 ValidateId(chapterNodeId, nameof(workItems));
 }
ValidateText(item.FeatureFamily, nameof(workItems), 128);
ValidateText(item.NodeTextHash, nameof(workItems), 256);
 ValidateJson(item.InputPayloadJson, nameof(workItems));
 ValidateText(item.InputPayloadHash, nameof(workItems), 64);
 var canonicalPayloadHash = ComputeInputPayloadHash(item.InputPayloadJson);
 if (!string.Equals(item.InputPayloadHash, canonicalPayloadHash, StringComparison.Ordinal))
 {
 throw new ArgumentException(
 "Work-item input payload hash must be the lowercase SHA-256 of canonical JSON.", nameof(workItems));
 }
 if (!uniqueWorkItems.Add((item.NodeId, item.FeatureFamily)))
 {
 throw new ArgumentException("Frozen work items cannot repeat a node and feature family.", nameof(workItems));
 }
 uniqueNodes.Add(item.NodeId);
 }
if (uniqueNodes.Count != request.TotalNodes)
{
 throw new ArgumentException("Frozen node count does not match the job total.", nameof(workItems));
}
}

 internal static string ComputeInputPayloadHash(string inputPayloadJson)
 {
 ArgumentException.ThrowIfNullOrWhiteSpace(inputPayloadJson);
 using var document = JsonDocument.Parse(inputPayloadJson);
 using var stream = new MemoryStream();
 using (var writer = new Utf8JsonWriter(stream))
 {
 WriteCanonicalJson(writer, document.RootElement);
 }
 return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
 }

 private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
 {
 switch (element.ValueKind)
 {
 case JsonValueKind.Object:
 writer.WriteStartObject();
 foreach (var property in element.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
 {
 writer.WritePropertyName(property.Name);
 WriteCanonicalJson(writer, property.Value);
 }
 writer.WriteEndObject();
 break;
 case JsonValueKind.Array:
 writer.WriteStartArray();
 foreach (var item in element.EnumerateArray()) WriteCanonicalJson(writer, item);
 writer.WriteEndArray();
 break;
 case JsonValueKind.String:
 writer.WriteStringValue(element.GetString());
 break;
 case JsonValueKind.Number:
 writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
 break;
 case JsonValueKind.True:
 writer.WriteBooleanValue(true);
 break;
 case JsonValueKind.False:
 writer.WriteBooleanValue(false);
 break;
 case JsonValueKind.Null:
 writer.WriteNullValue();
 break;
 default:
 throw new JsonException($"Unsupported JSON value kind '{element.ValueKind}'.");
 }
 }

 private static async ValueTask<ReferenceCorpusAnalysisJob?> ReadJobAsync(
 SqliteConnection connection,
 SqliteTransaction? transaction,
 string jobId,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = $"SELECT {JobColumns} FROM reference_analysis_jobs WHERE job_id = $job_id;";
 command.Parameters.AddWithValue("$job_id", jobId);
 var jobs = await ReadJobsAsync(command, cancellationToken);
 return jobs.Count == 0 ? null : jobs[0];
 }

 private static async ValueTask<IReadOnlyList<ReferenceCorpusAnalysisJob>> ReadJobsAsync(
 SqliteCommand command,
 CancellationToken cancellationToken)
 {
 var jobs = new List<ReferenceCorpusAnalysisJob>();
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 jobs.Add(new ReferenceCorpusAnalysisJob(
 reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
 reader.GetInt64(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
 GetNullableString(reader, 8), reader.GetString(9), reader.GetInt32(10), reader.GetString(11),
 reader.GetInt32(12), reader.GetInt32(13), reader.GetInt32(14), reader.GetInt32(15),
 reader.GetInt32(16), reader.GetInt32(17), reader.GetInt32(18), GetNullableInt32(reader, 19),
 reader.GetInt32(20), GetNullableString(reader, 21), reader.GetString(22), GetNullableInt32(reader, 23),
 reader.GetInt32(24), reader.GetInt32(25), GetNullableTimestamp(reader, 26),
 GetNullableString(reader, 27), GetNullableString(reader, 28), GetNullableTimestamp(reader, 29),
 GetNullableTimestamp(reader, 30), GetNullableTimestamp(reader, 31), GetNullableTimestamp(reader, 32),
 GetNullableTimestamp(reader, 33), ParseTimestamp(reader.GetString(34)), GetNullableTimestamp(reader, 35),
 GetNullableTimestamp(reader, 36), ParseTimestamp(reader.GetString(37)), GetNullableString(reader, 38),
 GetNullableString(reader, 39), reader.GetInt64(40)));
 }
 return jobs;
 }

 private static void ValidateText(string value, string parameterName, int maxLength)
 {
 if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
 {
 throw new ArgumentException($"Value must contain 1-{maxLength} non-control characters.", parameterName);
 }
 }

 private static void ValidateJson(string value, string parameterName)
 {
 try
 {
 using var _ = JsonDocument.Parse(value);
 }
 catch (JsonException exception)
 {
 throw new ArgumentException("Value must be valid JSON.", parameterName, exception);
 }
 }

 private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
 reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

 private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
 reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

 private static DateTimeOffset? GetNullableTimestamp(SqliteDataReader reader, int ordinal) =>
 reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

 private static DateTimeOffset ParseTimestamp(string value) =>
 DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

 private static async ValueTask InsertSnapshotAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisInputSnapshot snapshot,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 INSERT INTO reference_analysis_input_snapshots
 (input_snapshot_id, anchor_id, analysis_stage, scope, node_set_hash, family_set_json,
 schema_version, analyzer_version, model_provider, model_id, total_nodes, total_work_items, created_at)
 VALUES
 ($id, $anchor_id, $stage, $scope, $node_hash, $families, $schema, $analyzer,
 $provider, $model, $total_nodes, $total_work_items, $created_at);
 """;
 Add(command, "$id", snapshot.InputSnapshotId);
 Add(command, "$anchor_id", snapshot.AnchorId);
 Add(command, "$stage", snapshot.AnalysisStage);
 Add(command, "$scope", snapshot.Scope);
 Add(command, "$node_hash", snapshot.NodeSetHash);
 Add(command, "$families", snapshot.FamilySetJson);
 Add(command, "$schema", snapshot.SchemaVersion);
 Add(command, "$analyzer", snapshot.AnalyzerVersion);
 Add(command, "$provider", snapshot.ModelProvider);
 Add(command, "$model", snapshot.ModelId);
 Add(command, "$total_nodes", snapshot.TotalNodes);
 Add(command, "$total_work_items", snapshot.TotalWorkItems);
 Add(command, "$created_at", ToDb(snapshot.CreatedAt));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async ValueTask InsertWorkItemsAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 string snapshotId,
 IReadOnlyList<ReferenceCorpusAnalysisWorkItemSnapshot> workItems,
 CancellationToken cancellationToken)
 {
 foreach (var item in workItems)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 INSERT INTO reference_analysis_work_items
 (input_snapshot_id, ordinal, node_id, chapter_node_id, feature_family, node_text_hash,
 input_payload_json, input_payload_hash)
 VALUES
 ($snapshot_id, $ordinal, $node_id, $chapter_node_id, $family, $text_hash,
 $input_payload_json, $input_payload_hash);
 """;
 Add(command, "$snapshot_id", snapshotId);
 Add(command, "$ordinal", item.Ordinal);
 Add(command, "$node_id", item.NodeId);
 Add(command, "$chapter_node_id", item.ChapterNodeId);
Add(command, "$family", item.FeatureFamily);
Add(command, "$text_hash", item.NodeTextHash);
 Add(command, "$input_payload_json", item.InputPayloadJson);
 Add(command, "$input_payload_hash", item.InputPayloadHash);
 await command.ExecuteNonQueryAsync(cancellationToken);
 }
 }

 private static async ValueTask InsertJobAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusAnalysisJobEnqueue request,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = """
 INSERT INTO reference_analysis_jobs
 (job_id, run_id, input_snapshot_id, novel_id, anchor_id, job_kind, input_json, input_hash,
 dependency_job_id, priority_class, priority_value, status, total_nodes, total_work_items,
 token_budget, current_stage, current_chapter, max_attempts, queued_at, updated_at)
 VALUES
 ($job_id, $run_id, $snapshot_id, $novel_id, $anchor_id, $job_kind, $input_json, $input_hash,
 $dependency, $priority_class, $priority_value, 'queued', $total_nodes, $total_work_items,
 $token_budget, $stage, $chapter, $max_attempts, $queued_at, $queued_at);
 """;
 Add(command, "$job_id", request.JobId);
 Add(command, "$run_id", request.RunId);
 Add(command, "$snapshot_id", request.InputSnapshotId);
 Add(command, "$novel_id", request.NovelId);
 Add(command, "$anchor_id", request.AnchorId);
 Add(command, "$job_kind", request.JobKind);
 Add(command, "$input_json", request.InputJson);
 Add(command, "$input_hash", request.InputHash);
 Add(command, "$dependency", request.DependencyJobId);
 Add(command, "$priority_class", request.PriorityClass);
 Add(command, "$priority_value", request.PriorityValue);
 Add(command, "$total_nodes", request.TotalNodes);
 Add(command, "$total_work_items", request.TotalWorkItems);
 Add(command, "$token_budget", request.TokenBudget);
 Add(command, "$stage", request.CurrentStage);
 Add(command, "$chapter", request.CurrentChapter);
 Add(command, "$max_attempts", request.MaxAttempts);
 Add(command, "$queued_at", ToDb(request.QueuedAt));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static void Add(SqliteCommand command, string name, object? value)
 {
 command.Parameters.AddWithValue(name, value ?? DBNull.Value);
 }

 private static string ToDb(DateTimeOffset value) => value.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
}
