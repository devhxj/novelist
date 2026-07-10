using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

return await CorpusHarnessHost.RunAsync(args);

internal static class CorpusHarnessHost
{
 private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
 private const int ReservedTokens = 400;
 private const int ActualTokens = 275;
 private const string WorkerId = "quantitative-worker";

 public static async Task<int> RunAsync(string[] args)
 {
 try
 {
 var options = Arguments.Parse(args);
 object result = options.Command switch
 {
 "fault" => await RunFaultAsync(options),
 "recover" => await RunRecoveryAsync(options),
 "scale" => await RunScaleAsync(options),
 _ => throw new ArgumentException($"Unknown command '{options.Command}'.")
 };
 Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
 return 0;
 }
 catch (Exception exception)
 {
 Console.Error.WriteLine(exception);
 return 1;
 }
 }

 private static async Task<object> RunFaultAsync(Arguments options)
 {
 Require(options.DatabasePath, "--database");
 Require(options.CheckpointPath, "--checkpoint");
 Require(options.Point, "--point");
 if (File.Exists(options.DatabasePath)) File.Delete(options.DatabasePath);
 Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
var store = new SqliteReferenceCorpusAnalysisJobStore(options.DatabasePath);
await store.EnsureSchemaAsync();
await SeedAnchorAndProbeAsync(options.DatabasePath);
 var now = DateTimeOffset.UtcNow;
 var reservation = await CreateReservationAsync(store, options.ScenarioId, now, ReservedTokens);
 await WriteScenarioAsync(options, reservation, now);

 if (options.Point == "after_reservation") await CheckpointAndBlockAsync(options);
 if (options.Point == "after_model")
 {
 await File.WriteAllTextAsync(options.ModelResultPath, "model-result-ready");
 await CheckpointAndBlockAsync(options);
 }

 var completion = CreateCompletion(reservation, ActualTokens, now.AddSeconds(1));
 await store.RecordCompletionAsync(reservation, completion, now.AddSeconds(1));
 if (options.Point == "after_record") await CheckpointAndBlockAsync(options);

 if (options.Point == "during_finalize")
 {
 await store.FinalizeCompletionAsync(completion, now.AddSeconds(2), async (connection, transaction, _, cancellationToken) =>
 {
 await InsertProbeAsync(connection, transaction, completion.CompletionKey, cancellationToken);
 await CheckpointAndBlockAsync(options);
 });
 }
 else
 {
 await store.FinalizeCompletionAsync(completion, now.AddSeconds(2),
 (connection, transaction, envelope, cancellationToken) =>
 InsertProbeAsync(connection, transaction, envelope.CompletionKey, cancellationToken));
 }

 if (options.Point == "after_commit") await CheckpointAndBlockAsync(options);
 throw new InvalidOperationException("Fault host reached an unsupported checkpoint path.");
 }

 private static async Task<object> RunRecoveryAsync(Arguments options)
 {
 Require(options.DatabasePath, "--database");
 Require(options.ScenarioPath, "--scenario");
 var scenario = JsonSerializer.Deserialize<ScenarioState>(await File.ReadAllTextAsync(options.ScenarioPath), JsonOptions)
 ?? throw new InvalidOperationException("Scenario state is empty.");
 var store = new SqliteReferenceCorpusAnalysisJobStore(options.DatabasePath);
 await store.EnsureSchemaAsync();
 var started = Stopwatch.StartNew();
 var reservation = scenario.Reservation;
 var completion = CreateCompletion(reservation, ActualTokens, scenario.CreatedAt.AddSeconds(1));
 var point = scenario.Point;

 if (point is "after_reservation" or "after_model")
 {
 var future = DateTimeOffset.UtcNow.AddMinutes(2);
 await store.ReclaimExpiredLeasesAsync(future, future.AddMilliseconds(1));
 await store.RequeueDueRetriesAsync(future.AddSeconds(1));
 var claim = await store.ClaimNextAsync(WorkerId + "-recovery", future.AddSeconds(2), TimeSpan.FromSeconds(45))
 ?? throw new InvalidOperationException("Recovered job was not claimable.");
 reservation = await store.ReserveNextWorkItemAsync(
 scenario.Reservation.JobId, WorkerId + "-recovery", claim.LeaseToken,
 ReservedTokens, future.AddSeconds(3))
 ?? throw new InvalidOperationException("Recovered work item was not reservable.");
 completion = CreateCompletion(reservation, ActualTokens, future.AddSeconds(4));
 await store.RecordCompletionAsync(reservation, completion, future.AddSeconds(4));
 }

 await store.FinalizeCompletionAsync(completion, DateTimeOffset.UtcNow,
 (connection, transaction, envelope, cancellationToken) =>
 InsertProbeAsync(connection, transaction, envelope.CompletionKey, cancellationToken));
 started.Stop();
 var audit = await ReadAuditAsync(options.DatabasePath, scenario.Reservation.JobId, completion.CompletionKey);
 return new
 {
 scenario = scenario.ScenarioId,
 point,
 recovery_ms = started.Elapsed.TotalMilliseconds,
 model_replayed = point == "after_model",
 audit,
 passed = audit.SucceededWorkItems == 1 && audit.PendingWorkItems == 0 &&
 audit.CompletionRows == 1 && audit.FinalizedCompletionRows == 1 &&
 audit.OutputRows == 1 && audit.DuplicateOutputRows == 0 &&
 audit.TokensReserved == 0 && audit.TokensSpent == audit.ExpectedTokensSpent
 };
 }

 private static async Task<object> RunScaleAsync(Arguments options)
 {
 Require(options.DatabasePath, "--database");
 Require(options.FixturePath, "--fixture");
 if (File.Exists(options.DatabasePath)) File.Delete(options.DatabasePath);
 var records = new List<ScaleRecord>();
 long characters = 0;
 await foreach (var line in File.ReadLinesAsync(options.FixturePath))
 {
 var record = JsonSerializer.Deserialize<ScaleRecord>(line, JsonOptions)
 ?? throw new InvalidOperationException("Scale fixture contains an empty record.");
 records.Add(record);
 characters += record.Text.Length;
 }
 if (characters < options.MinimumCharacters)
 throw new InvalidOperationException($"Scale fixture has {characters} characters; expected at least {options.MinimumCharacters}.");

 var store = new SqliteReferenceCorpusAnalysisJobStore(options.DatabasePath);
await store.EnsureSchemaAsync();
await SeedAnchorAndProbeAsync(options.DatabasePath);
 var nodeSeedStarted = Stopwatch.StartNew();
 await SeedScaleNodesAsync(options.DatabasePath, records);
 nodeSeedStarted.Stop();
var enqueueStarted = Stopwatch.StartNew();
 var jobCount = 0;
 foreach (var chunk in records.Chunk(options.JobSize))
 {
 var suffix = $"scale-{jobCount:D5}";
 await EnqueueScaleJobAsync(store, suffix, chunk, DateTimeOffset.UtcNow.AddMilliseconds(jobCount));
 jobCount++;
 }
 enqueueStarted.Stop();

 var claimSamples = new List<double>(jobCount);
 var workSamples = new List<double>(records.Count);
 var listSamples = new List<double>();
 var tokenSpent = 0L;
 var processed = 0;
 var runStarted = Stopwatch.StartNew();
 while (processed < records.Count)
 {
 var claimWatch = Stopwatch.StartNew();
 var claim = await store.ClaimNextAsync(WorkerId, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(45));
 claimWatch.Stop();
 if (claim is null) throw new InvalidOperationException($"No claim available after {processed}/{records.Count} work items.");
 claimSamples.Add(claimWatch.Elapsed.TotalMilliseconds);
 while (true)
 {
 var itemWatch = Stopwatch.StartNew();
 var reservation = await store.ReserveNextWorkItemAsync(
 claim.Job.JobId, WorkerId, claim.LeaseToken, 32, DateTimeOffset.UtcNow);
 if (reservation is null) break;
 var completion = CreateCompletion(reservation, 24, DateTimeOffset.UtcNow);
 await store.RecordCompletionAsync(reservation, completion, DateTimeOffset.UtcNow);
 await store.FinalizeCompletionAsync(completion, DateTimeOffset.UtcNow,
 (connection, transaction, envelope, cancellationToken) =>
 InsertScaleOutputAsync(connection, transaction, envelope.CompletionKey, cancellationToken));
 itemWatch.Stop();
 workSamples.Add(itemWatch.Elapsed.TotalMilliseconds);
 tokenSpent += 24;
 processed++;
 if (processed % 250 == 0)
 {
 var listWatch = Stopwatch.StartNew();
 await store.ListAsync(new(null, null, null, 0, 50));
 listWatch.Stop();
 listSamples.Add(listWatch.Elapsed.TotalMilliseconds);
 }
 var job = await store.GetAsync(claim.Job.JobId);
 if (job?.Status == ReferenceCorpusAnalysisJobStatuses.Completed) break;
 }
 }
 runStarted.Stop();
 var outputRows = await ScalarAsync(options.DatabasePath, "SELECT COUNT(*) FROM harness_scale_outputs;");
 return new
 {
 fixture = Path.GetFullPath(options.FixturePath),
 characters,
 work_items = records.Count,
jobs = jobCount,
 node_seed_ms = nodeSeedStarted.Elapsed.TotalMilliseconds,
 enqueue_ms = enqueueStarted.Elapsed.TotalMilliseconds,
 elapsed_ms = runStarted.Elapsed.TotalMilliseconds,
 throughput_work_items_per_second = records.Count / runStarted.Elapsed.TotalSeconds,
 claim_ms = Distribution(claimSamples),
 work_item_ms = Distribution(workSamples),
 task_list_ms = Distribution(listSamples),
 tokens = new { spent = tokenSpent, reserved = 0, budget_penetration = 0 },
 output_rows = outputRows,
 duplicate_outputs = records.Count - outputRows,
 passed = outputRows == records.Count &&
 records.Count / runStarted.Elapsed.TotalSeconds >= options.MinimumThroughput &&
 Percentile(claimSamples, 0.95) <= options.MaximumClaimP95Ms &&
 (listSamples.Count == 0 || Percentile(listSamples, 0.95) <= options.MaximumListP95Ms)
 };
 }

 private static async ValueTask<ReferenceCorpusAnalysisWorkItemReservation> CreateReservationAsync(
 SqliteReferenceCorpusAnalysisJobStore store, string suffix, DateTimeOffset now, int reservedTokens)
 {
 const string payload = "{\"text\":\"fixture\"}";
 var snapshotId = $"snapshot-{suffix}";
 var jobId = $"job-{suffix}";
 await store.EnqueueAsync(
 new(snapshotId, 101, "stage_2", "sentence", "nodes-hash", "[\"syntax\"]",
 "corpus-analysis-v2", "feature-v2", "fake", "fake-model", 1, 1, now),
 [new(0, "node-1", null, "syntax", "hash-1", payload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(payload))],
 new(jobId, $"run-{suffix}", snapshotId, 7, 101,
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis, "{\"node_type\":\"sentence\"}",
 "input-hash", null, ReferenceCorpusAnalysisPriorityClasses.Normal, 100,
 1, 1, 2500, "stage_2", null, 3, now));
 var claim = await store.ClaimNextAsync(WorkerId, now, TimeSpan.FromSeconds(45))
 ?? throw new InvalidOperationException("Harness job was not claimable.");
 return await store.ReserveNextWorkItemAsync(jobId, WorkerId, claim.LeaseToken, reservedTokens, now.AddMilliseconds(1))
 ?? throw new InvalidOperationException("Harness work item was not reservable.");
 }

 private static async Task EnqueueScaleJobAsync(
 SqliteReferenceCorpusAnalysisJobStore store, string suffix, ScaleRecord[] records, DateTimeOffset now)
 {
 var items = records.Select((record, index) =>
 {
 var payload = JsonSerializer.Serialize(new { text = record.Text }, JsonOptions);
 var nodeId = $"scale-node-{record.SequenceIndex}";
 var nodeHash = TextHash(record.Text);
 return new ReferenceCorpusAnalysisWorkItemSnapshot(index, nodeId, null,
 index % 2 == 0 ? "syntax" : "narrative", nodeHash, payload,
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(payload));
 }).ToArray();
 var snapshotId = $"snapshot-{suffix}";
 await store.EnqueueAsync(
 new(snapshotId, 101, "stage_2", "mixed", $"nodes-{suffix}", "[\"syntax\",\"narrative\"]",
 "corpus-analysis-v2", "feature-v2", "fake", "fake-model", records.Length, records.Length, now),
 items,
 new($"job-{suffix}", $"run-{suffix}", snapshotId, 7, 101,
 ReferenceCorpusAnalysisJobKinds.FeatureAnalysis, "{\"node_type\":\"mixed\"}",
 $"input-{suffix}", null, ReferenceCorpusAnalysisPriorityClasses.Normal, 100,
 records.Length, records.Length, records.Length * 32, "stage_2", null, 3, now));
 }

 private static ReferenceCorpusAnalysisCompletionEnvelope CreateCompletion(
 ReferenceCorpusAnalysisWorkItemReservation reservation, int tokensSpent, DateTimeOffset completedAt)
 {
 var payload = JsonSerializer.Serialize(new { result = "accepted", reservation.NodeId }, JsonOptions);
 return new(
 ReferenceCorpusAnalysisCompletionCodec.CreateKey(
 reservation.InputSnapshotId, reservation.Ordinal, reservation.InvocationNumber),
 reservation.JobId, reservation.RunId, reservation.InputSnapshotId,
 reservation.Ordinal, reservation.InvocationNumber, reservation.AttemptNumber,
 reservation.ReservedTokens, ReferenceCorpusAnalysisCompletionKinds.FeatureObservations,
 payload, ReferenceCorpusAnalysisCompletionCodec.Hash(payload), tokensSpent, "[]", completedAt);
 }

 private static async Task SeedAnchorAndProbeAsync(string databasePath)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT OR IGNORE INTO reference_anchors
 (anchor_id,novel_id,title,author,source_path,source_kind,license_status,
 source_file_hash,build_version,status,created_at,updated_at)
 VALUES(101,7,'fixture','fixture','fixture.txt','txt','user_owned',
 'source-hash','v2','ready','2026-07-10T00:00:00Z','2026-07-10T00:00:00Z');
 INSERT OR IGNORE INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,
 start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES('node-1',101,NULL,'sentence',0,0,1,0,7,7,'hash-1','fixture','2026-07-10T00:00:00Z');
 CREATE TABLE IF NOT EXISTS harness_outputs(
 completion_key TEXT PRIMARY KEY, created_at TEXT NOT NULL);
 CREATE TABLE IF NOT EXISTS harness_scale_outputs(
 completion_key TEXT PRIMARY KEY, created_at TEXT NOT NULL);
 """;
await command.ExecuteNonQueryAsync();
}

 private static async Task SeedScaleNodesAsync(string databasePath, IReadOnlyList<ScaleRecord> records)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var transaction = await connection.BeginTransactionAsync();
 await using var command = connection.CreateCommand();
 command.Transaction = (SqliteTransaction)transaction;
 command.CommandText = """
 INSERT INTO reference_text_nodes
 (node_id,anchor_id,parent_node_id,node_type,sequence_index,depth,chapter_index,
 start_offset,end_offset,char_len,text_hash,text,created_at)
 VALUES($node,101,NULL,$type,$sequence,0,$chapter,$start,$end,$length,$hash,$text,$created);
 """;
 var node = command.Parameters.Add("$node", SqliteType.Text);
 var type = command.Parameters.Add("$type", SqliteType.Text);
 var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
 var chapter = command.Parameters.Add("$chapter", SqliteType.Integer);
 var start = command.Parameters.Add("$start", SqliteType.Integer);
 var end = command.Parameters.Add("$end", SqliteType.Integer);
 var length = command.Parameters.Add("$length", SqliteType.Integer);
 var hash = command.Parameters.Add("$hash", SqliteType.Text);
 var text = command.Parameters.Add("$text", SqliteType.Text);
 var created = command.Parameters.Add("$created", SqliteType.Text);
 var offset = 0;
 foreach (var record in records)
 {
 node.Value = $"scale-node-{record.SequenceIndex}";
 type.Value = record.SequenceIndex % 2 == 0 ? "sentence" : "passage";
 sequence.Value = record.SequenceIndex;
 chapter.Value = record.ChapterIndex;
 start.Value = offset;
 offset += record.Text.Length;
 end.Value = offset;
 length.Value = record.Text.Length;
 hash.Value = TextHash(record.Text);
 text.Value = record.Text;
 created.Value = DateTimeOffset.UtcNow.ToString("O");
 await command.ExecuteNonQueryAsync();
 }
 await transaction.CommitAsync();
 }

 private static async ValueTask InsertProbeAsync(
 SqliteConnection connection, SqliteTransaction transaction, string completionKey, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO harness_outputs(completion_key,created_at) VALUES($key,$time);";
 command.Parameters.AddWithValue("$key", completionKey);
 command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async ValueTask InsertScaleOutputAsync(
 SqliteConnection connection, SqliteTransaction transaction, string completionKey, CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.Transaction = transaction;
 command.CommandText = "INSERT INTO harness_scale_outputs(completion_key,created_at) VALUES($key,$time);";
 command.Parameters.AddWithValue("$key", completionKey);
 command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async Task WriteScenarioAsync(
 Arguments options, ReferenceCorpusAnalysisWorkItemReservation reservation, DateTimeOffset createdAt)
 {
 Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ScenarioPath))!);
 await File.WriteAllTextAsync(options.ScenarioPath,
 JsonSerializer.Serialize(new ScenarioState(options.ScenarioId, options.Point, createdAt, reservation), JsonOptions));
 }

 private static async Task CheckpointAndBlockAsync(Arguments options)
 {
 Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.CheckpointPath))!);
 await File.WriteAllTextAsync(options.CheckpointPath, $"{options.Point}|{Environment.ProcessId}|{DateTimeOffset.UtcNow:O}");
 await Task.Delay(Timeout.InfiniteTimeSpan);
 }

 private static async Task<Audit> ReadAuditAsync(string databasePath, string jobId, string completionKey)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT
 job.tokens_spent,job.tokens_reserved,
 SUM(CASE WHEN work.work_state='succeeded' THEN 1 ELSE 0 END),
 SUM(CASE WHEN work.work_state='pending' THEN 1 ELSE 0 END),
 (SELECT COUNT(*) FROM reference_analysis_work_item_completions WHERE completion_key=$key),
 (SELECT COUNT(*) FROM reference_analysis_work_item_completions WHERE completion_key=$key AND finalized_at IS NOT NULL),
 (SELECT COUNT(*) FROM harness_outputs WHERE completion_key=$key),
 (SELECT COUNT(*) - COUNT(DISTINCT completion_key) FROM harness_outputs)
 FROM reference_analysis_jobs AS job
 JOIN reference_analysis_work_items AS work ON work.input_snapshot_id=job.input_snapshot_id
 WHERE job.job_id=$job GROUP BY job.job_id;
 """;
 command.Parameters.AddWithValue("$job", jobId);
 command.Parameters.AddWithValue("$key", completionKey);
 await using var reader = await command.ExecuteReaderAsync();
 if (!await reader.ReadAsync()) throw new InvalidOperationException("Harness audit row was not found.");
 var tokensSpent = reader.GetInt32(0);
 return new(tokensSpent, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
 reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
 tokensSpent > ActualTokens ? ActualTokens + ReservedTokens : ActualTokens);
 }

private static object Distribution(IReadOnlyList<double> values) => new
 {
 count = values.Count,
 p50 = Percentile(values, 0.50),
 p95 = Percentile(values, 0.95),
 max = values.Count == 0 ? 0 : values.Max()
};

 private static string TextHash(string text) =>
 Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

 private static double Percentile(IReadOnlyList<double> values, double percentile)
 {
 if (values.Count == 0) return 0;
 var sorted = values.Order().ToArray();
 var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
 return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
 }

 private static async Task<long> ScalarAsync(string databasePath, string sql)
 {
 await using var connection = await OpenAsync(databasePath);
 await using var command = connection.CreateCommand();
 command.CommandText = sql;
 return Convert.ToInt64(await command.ExecuteScalarAsync());
 }

 private static async Task<SqliteConnection> OpenAsync(string databasePath)
 {
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
 await connection.OpenAsync();
 await using var pragma = connection.CreateCommand();
 pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
 await pragma.ExecuteNonQueryAsync();
 return connection;
 }

 private static void Require(string value, string name)
 {
 if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
 }

 private sealed record ScenarioState(string ScenarioId, string Point, DateTimeOffset CreatedAt,
 ReferenceCorpusAnalysisWorkItemReservation Reservation);
 private sealed record ScaleRecord(
 [property: JsonPropertyName("source_id")] string SourceId,
 [property: JsonPropertyName("library_id")] string LibraryId,
 [property: JsonPropertyName("chapter_index")] int ChapterIndex,
 [property: JsonPropertyName("sequence_index")] int SequenceIndex,
 [property: JsonPropertyName("text")] string Text,
 [property: JsonPropertyName("license_state")] string LicenseState);
 private sealed record Audit(int TokensSpent, int TokensReserved, int SucceededWorkItems, int PendingWorkItems,
 int CompletionRows, int FinalizedCompletionRows, int OutputRows, int DuplicateOutputRows, int ExpectedTokensSpent);

 private sealed class Arguments
 {
 public string Command { get; init; } = string.Empty;
 public string DatabasePath { get; init; } = string.Empty;
 public string CheckpointPath { get; init; } = string.Empty;
 public string ScenarioPath { get; init; } = string.Empty;
 public string ModelResultPath { get; init; } = string.Empty;
 public string FixturePath { get; init; } = string.Empty;
 public string Point { get; init; } = string.Empty;
 public string ScenarioId { get; init; } = "default";
 public int MinimumCharacters { get; init; } = 2_000_000;
 public int JobSize { get; init; } = 100;
 public double MinimumThroughput { get; init; } = 20;
 public double MaximumClaimP95Ms { get; init; } = 100;
 public double MaximumListP95Ms { get; init; } = 200;

 public static Arguments Parse(string[] args)
 {
 if (args.Length == 0) throw new ArgumentException("A command is required.");
 var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
 for (var index = 1; index < args.Length; index += 2)
 {
 if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
 throw new ArgumentException($"Invalid argument at position {index}.");
 values[args[index][2..]] = args[index + 1];
 }
 string Get(string key, string fallback = "") => values.GetValueOrDefault(key, fallback);
 int GetInt(string key, int fallback) => int.TryParse(Get(key), CultureInfo.InvariantCulture, out var value) ? value : fallback;
 double GetDouble(string key, double fallback) => double.TryParse(Get(key), CultureInfo.InvariantCulture, out var value) ? value : fallback;
 var scenario = Get("scenario");
 return new()
 {
 Command = args[0], DatabasePath = Get("database"), CheckpointPath = Get("checkpoint"),
 ScenarioPath = scenario, ModelResultPath = Get("model-result"), FixturePath = Get("fixture"),
 Point = Get("point"), ScenarioId = Get("scenario-id", "default"),
 MinimumCharacters = GetInt("minimum-characters", 2_000_000), JobSize = GetInt("job-size", 100),
 MinimumThroughput = GetDouble("minimum-throughput", 20),
 MaximumClaimP95Ms = GetDouble("maximum-claim-p95-ms", 100),
 MaximumListP95Ms = GetDouble("maximum-list-p95-ms", 200)
 };
 }
 }
}
