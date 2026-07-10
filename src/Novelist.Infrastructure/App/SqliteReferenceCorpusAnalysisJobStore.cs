using System.Globalization;
using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceCorpusAnalysisJobStore
{
 private const int MaxPageSize = 200;
 private const int MaxErrorCodeLength = 128;
 private const int MaxErrorMessageLength = 1_200;
 private readonly string _databasePath;

 public SqliteReferenceCorpusAnalysisJobStore(string databasePath)
 {
 ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
 _databasePath = Path.GetFullPath(databasePath);
 }

 public async ValueTask EnsureSchemaAsync(CancellationToken cancellationToken = default)
 {
 Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
 }

 public async ValueTask<ReferenceCorpusAnalysisJob> EnqueueAsync(
 ReferenceCorpusAnalysisInputSnapshot snapshot,
 IReadOnlyList<ReferenceCorpusAnalysisWorkItemSnapshot> workItems,
 ReferenceCorpusAnalysisJobEnqueue request,
 CancellationToken cancellationToken = default)
 {
 ValidateEnqueue(snapshot, workItems, request);
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
try
{
 await EnsureCanonicalRunAsync(connection, transaction, snapshot, request, cancellationToken);
await InsertSnapshotAsync(connection, transaction, snapshot, cancellationToken);
 await InsertWorkItemsAsync(connection, transaction, snapshot.InputSnapshotId, workItems, cancellationToken);
 await InsertJobAsync(connection, transaction, request, cancellationToken);
 await transaction.CommitAsync(cancellationToken);
 }
 catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
 {
 throw new ReferenceCorpusAnalysisJobConflictException(
 $"Analysis job '{request.JobId}' conflicts with an existing job, run, snapshot, dependency, or anchor.");
 }

 return await GetRequiredAsync(request.JobId, cancellationToken);
 }

 public async ValueTask<ReferenceCorpusAnalysisJob?> GetAsync(
 string jobId,
 CancellationToken cancellationToken = default)
 {
 ValidateId(jobId, nameof(jobId));
 await using var connection = await OpenConnectionAsync(cancellationToken);
 return await ReadJobAsync(connection, transaction: null, jobId, cancellationToken);
 }

 public async ValueTask<IReadOnlyList<ReferenceCorpusAnalysisJob>> ListAsync(
 ReferenceCorpusAnalysisJobListRequest request,
 CancellationToken cancellationToken = default)
 {
 ArgumentNullException.ThrowIfNull(request);
 if (request.Offset < 0 || request.Limit is < 1 or > MaxPageSize)
 {
 throw new ArgumentOutOfRangeException(nameof(request));
 }

 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var command = connection.CreateCommand();
 var predicates = new List<string>();
 if (request.NovelId is { } novelId)
 {
 predicates.Add("novel_id = $novel_id");
 command.Parameters.AddWithValue("$novel_id", novelId);
 }

 if (request.AnchorId is { } anchorId)
 {
 predicates.Add("anchor_id = $anchor_id");
 command.Parameters.AddWithValue("$anchor_id", anchorId);
 }

 if (!string.IsNullOrWhiteSpace(request.Status))
 {
 if (!ReferenceCorpusAnalysisJobStatuses.All.Contains(request.Status, StringComparer.Ordinal))
 {
 throw new ArgumentOutOfRangeException(nameof(request), request.Status, "Unknown analysis job status.");
 }

 predicates.Add("status = $status");
 command.Parameters.AddWithValue("$status", request.Status);
 }

 command.CommandText = $"""
 SELECT {JobColumns}
 FROM reference_analysis_jobs
 {(predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates))}
 ORDER BY updated_at DESC, job_id
 LIMIT $limit OFFSET $offset;
 """;
 command.Parameters.AddWithValue("$limit", request.Limit);
 command.Parameters.AddWithValue("$offset", request.Offset);
 return await ReadJobsAsync(command, cancellationToken);
 }

 public async ValueTask<long> CountAsync(
 long? novelId,
 long? anchorId,
 string? status,
 CancellationToken cancellationToken = default)
 {
 await using var connection = await OpenConnectionAsync(cancellationToken);
 await using var command = connection.CreateCommand();
 var predicates = new List<string>();
 if (novelId is { } novel)
 {
 predicates.Add("novel_id = $novel_id");
 command.Parameters.AddWithValue("$novel_id", novel);
 }

 if (anchorId is { } anchor)
 {
 predicates.Add("anchor_id = $anchor_id");
 command.Parameters.AddWithValue("$anchor_id", anchor);
 }

 if (!string.IsNullOrWhiteSpace(status))
 {
 predicates.Add("status = $status");
 command.Parameters.AddWithValue("$status", status);
 }

 command.CommandText = $"SELECT COUNT(*) FROM reference_analysis_jobs {(predicates.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", predicates))};";
 return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
 }

 private async ValueTask<ReferenceCorpusAnalysisJob> GetRequiredAsync(
 string jobId,
 CancellationToken cancellationToken)
 {
 return await GetAsync(jobId, cancellationToken)
 ?? throw new InvalidOperationException($"Analysis job '{jobId}' disappeared after persistence.");
 }

 private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
 {
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder
 {
 DataSource = _databasePath,
 Pooling = false,
 DefaultTimeout = 10
 }.ToString());
 await connection.OpenAsync(cancellationToken);
 await using var pragma = connection.CreateCommand();
 pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
 await pragma.ExecuteNonQueryAsync(cancellationToken);
 return connection;
 }

 private static void ValidateId(string value, string parameterName)
 {
 if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
 {
 throw new ArgumentException("Identifier must contain 1-128 non-control characters.", parameterName);
 }
 }
}
