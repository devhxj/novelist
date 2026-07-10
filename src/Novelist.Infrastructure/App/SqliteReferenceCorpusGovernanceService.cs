using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceCorpusGovernanceService : IReferenceCorpusGovernanceService
{
 private readonly AppInitializationOptions _options;
 private readonly SemaphoreSlim _mutex = new(1, 1);

 public SqliteReferenceCorpusGovernanceService(AppInitializationOptions options) => _options = options;

 public async ValueTask<ReferenceCorpusGovernancePayload> GetGovernanceAsync(GetReferenceCorpusGovernancePayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 return await LockedAsync(connection => ReadAsync(connection, input.SessionId, cancellationToken), cancellationToken);
 }

 public async ValueTask<ReferenceCorpusGovernancePayload> SetSessionLibraryBindingAsync(SetReferenceCorpusSessionLibraryBindingPayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 var sessionId = Required(input.SessionId, nameof(input.SessionId));
 var libraryId = Required(input.LibraryId, nameof(input.LibraryId));
return await LockedAsync(async connection =>
{
 await ExecuteAsync(connection,
 "INSERT INTO reference_session_library_scope_state(session_id,is_explicit,updated_at) VALUES($session,1,$time) ON CONFLICT(session_id) DO UPDATE SET is_explicit=1,updated_at=excluded.updated_at;",
 cancellationToken, ("$session", sessionId), ("$time", DateTimeOffset.UtcNow.ToString("O")));
await ExecuteAsync(connection, input.Enabled
 ? "INSERT INTO reference_session_library_binding(session_id, library_id) VALUES ($session, $library) ON CONFLICT(session_id, library_id) DO NOTHING;"
 : "DELETE FROM reference_session_library_binding WHERE session_id = $session AND library_id = $library;",
 cancellationToken, ("$session", sessionId), ("$library", libraryId));
 return await ReadAsync(connection, sessionId, cancellationToken);
 }, cancellationToken);
 }

 public async ValueTask<ReferenceCorpusGovernancePayload> UpdateLibraryMemberAsync(UpdateReferenceCorpusLibraryMemberPayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.AnchorId <= 0) throw new ArgumentOutOfRangeException(nameof(input.AnchorId));
 var reason = input.Enabled ? null : Required(input.DisabledReason, nameof(input.DisabledReason));
 return await LockedAsync(async connection =>
 {
 var changed = await ExecuteAsync(connection,
 "UPDATE reference_library_members SET enabled=$enabled, source_quality=$quality, disabled_reason=$reason WHERE library_id=$library AND anchor_id=$anchor;",
 cancellationToken, ("$enabled", input.Enabled ? 1 : 0), ("$quality", Db(input.SourceQuality)),
 ("$reason", Db(reason)), ("$library", Required(input.LibraryId, nameof(input.LibraryId))), ("$anchor", input.AnchorId));
 if (changed != 1) throw new InvalidOperationException("The corpus library member does not exist.");
 return await ReadAsync(connection, null, cancellationToken);
 }, cancellationToken);
 }

 public async ValueTask<ReferenceCorpusGovernancePayload> UpdateLicenseAsync(UpdateReferenceCorpusLicensePayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 ValidateLicense(input);
 return await LockedAsync(async connection =>
 {
 await ExecuteAsync(connection,
 "INSERT INTO reference_source_license(anchor_id,license_state,authorization_evidence,reuse_policy,max_verbatim_ratio,cleared_for_insertion,reviewed_at) VALUES($anchor,$state,$evidence,$policy,$ratio,$cleared,$time) ON CONFLICT(anchor_id) DO UPDATE SET license_state=excluded.license_state,authorization_evidence=excluded.authorization_evidence,reuse_policy=excluded.reuse_policy,max_verbatim_ratio=excluded.max_verbatim_ratio,cleared_for_insertion=excluded.cleared_for_insertion,reviewed_at=excluded.reviewed_at;",
 cancellationToken, ("$anchor", input.AnchorId), ("$state", input.LicenseState), ("$evidence", Db(input.AuthorizationEvidence)),
 ("$policy", input.ReusePolicy), ("$ratio", input.MaxVerbatimRatio is null ? DBNull.Value : input.MaxVerbatimRatio.Value),
 ("$cleared", input.ClearedForInsertion ? 1 : 0), ("$time", DateTimeOffset.UtcNow.ToString("O")));
 return await ReadAsync(connection, null, cancellationToken);
 }, cancellationToken);
 }

public async ValueTask<ReferenceCorpusDedupResultPayload> RebuildDedupGroupsAsync(RebuildReferenceCorpusDedupGroupsPayload input, CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 return await LockedAsync(async connection =>
 {
 var scanned = await ExecuteAsync(connection,
 "UPDATE reference_library_members AS member SET dedup_group_id=(SELECT 'source:' || anchor.source_file_hash FROM reference_anchors AS anchor WHERE anchor.anchor_id=member.anchor_id) WHERE ($library IS NULL OR member.library_id=$library);",
 cancellationToken, ("$library", Db(input.LibraryId)));
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT COUNT(DISTINCT dedup_group_id) FROM reference_library_members WHERE dedup_group_id IS NOT NULL AND ($library IS NULL OR library_id=$library);";
 command.Parameters.AddWithValue("$library", Db(input.LibraryId));
 return new ReferenceCorpusDedupResultPayload(scanned, Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)));
}, cancellationToken);
}



 private async ValueTask<T> LockedAsync<T>(Func<SqliteConnection, ValueTask<T>> action, CancellationToken cancellationToken)
 {
 await _mutex.WaitAsync(cancellationToken);
 try { await using var connection = await OpenAsync(cancellationToken); return await action(connection); }
 finally { _mutex.Release(); }
 }

 private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
 {
 var path = Path.Combine(await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken), "reference-anchor", "index.sqlite");
 Directory.CreateDirectory(Path.GetDirectoryName(path)!);
 var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
 await connection.OpenAsync(cancellationToken);
await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
 await EnsureGovernanceTablesAsync(connection, cancellationToken);
return connection;
 }

private static async ValueTask<int> ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
 {
 await using var command = connection.CreateCommand(); command.CommandText = sql;
 foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
 return await command.ExecuteNonQueryAsync(cancellationToken);
}

 private static async ValueTask<ReferenceCorpusGovernancePayload> ReadAsync(SqliteConnection connection, string? sessionId, CancellationToken cancellationToken)
 {
 var rows = new List<Row>();
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT library.library_id,library.scope,library.novel_id,library.name,CASE WHEN binding.library_id IS NULL THEN 0 ELSE 1 END,member.anchor_id,anchor.title,member.enabled,member.source_quality,member.disabled_reason,member.dedup_group_id,COALESCE(license.license_state,'unknown'),COALESCE(license.reuse_policy,'forbidden'),license.max_verbatim_ratio,COALESCE(license.cleared_for_insertion,0) FROM reference_corpus_libraries AS library LEFT JOIN reference_session_library_binding AS binding ON binding.library_id=library.library_id AND binding.session_id=$session LEFT JOIN reference_library_members AS member ON member.library_id=library.library_id LEFT JOIN reference_anchors AS anchor ON anchor.anchor_id=member.anchor_id LEFT JOIN reference_source_license AS license ON license.anchor_id=member.anchor_id ORDER BY library.scope,library.name,member.anchor_id;";
 command.Parameters.AddWithValue("$session", sessionId ?? string.Empty);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 rows.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3), reader.GetInt32(4) != 0,
 reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) || reader.GetInt32(7) != 0,
 reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
 reader.GetString(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetDouble(13), reader.GetInt32(14) != 0));

 var libraries = rows.GroupBy(row => new { row.LibraryId, row.Scope, row.NovelId, row.Name, row.Bound })
 .Select(group => new ReferenceCorpusGovernanceLibraryPayload(group.Key.LibraryId, group.Key.Scope, group.Key.NovelId, group.Key.Name, group.Key.Bound,
 group.Where(row => row.AnchorId is not null).Select(row => new ReferenceCorpusGovernanceMemberPayload(row.AnchorId!.Value,
 row.Title ?? $"Anchor {row.AnchorId}", row.Enabled, row.SourceQuality, row.DisabledReason, row.DedupGroupId,
 row.LicenseState, row.ReusePolicy, row.MaxVerbatimRatio, row.Cleared)).ToArray())).ToArray();
 var pending = await ScalarCountAsync(connection, "SELECT COUNT(*) FROM reference_review_queue WHERE resolved_at IS NULL;", cancellationToken);
 var stale = await ScalarCountAsync(connection, "SELECT COUNT(*) FROM reference_aggregates WHERE validity_state='stale';", cancellationToken);
 var audits = await ScalarCountAsync(connection, "SELECT COUNT(*) FROM reference_insertion_audits;", cancellationToken);
 return new ReferenceCorpusGovernancePayload(sessionId, libraries, pending, stale, audits);
}

 private sealed record Row(string LibraryId, string Scope, long? NovelId, string Name, bool Bound, long? AnchorId, string? Title,
 bool Enabled, string? SourceQuality, string? DisabledReason, string? DedupGroupId, string LicenseState, string ReusePolicy,
 double? MaxVerbatimRatio, bool Cleared);

private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
 private static async ValueTask<int> ScalarCountAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken) { await using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)); }
 private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new ArgumentException("Value is required.", name);

 private static void ValidateLicense(UpdateReferenceCorpusLicensePayload input)
 {
 if (input.AnchorId <= 0) throw new ArgumentOutOfRangeException(nameof(input.AnchorId));
 if (!ReferenceCorpusLicenseStates.All.Contains(input.LicenseState)) throw new ArgumentOutOfRangeException(nameof(input.LicenseState));
 if (!ReferenceCorpusReusePolicies.All.Contains(input.ReusePolicy)) throw new ArgumentOutOfRangeException(nameof(input.ReusePolicy));
 if (input.MaxVerbatimRatio is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(input.MaxVerbatimRatio));
 if (input.ClearedForInsertion && (input.LicenseState is "unknown" or "forbidden" or "restricted" || input.ReusePolicy is "forbidden" or "reference_only"))
 throw new InvalidOperationException("This license cannot be cleared for insertion.");
 }
}
