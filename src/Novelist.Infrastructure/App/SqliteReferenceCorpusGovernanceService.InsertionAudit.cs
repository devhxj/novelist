using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceCorpusGovernanceService
{
 public async ValueTask<bool> RecordInsertionAuditAsync(
 RecordReferenceCorpusInsertionAuditPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.NovelId <= 0 || input.ChapterNumber <= 0)
 {
 throw new ArgumentOutOfRangeException(nameof(input));
 }

 var draft = input.Draft
 ?? throw new InvalidOperationException("The insertion draft is required for server-side audit.");
 if (!draft.ReadyForInsertion || !draft.Gate.Passed || !draft.Audit.Passed)
 {
 throw new InvalidOperationException("The insertion draft has not passed its generation audit.");
 }

 return await LockedAsync(
 connection => RecordTrustedInsertionAuditAsync(connection, input, draft, cancellationToken),
 cancellationToken);
 }

 private static async ValueTask<bool> RecordTrustedInsertionAuditAsync(
 SqliteConnection connection,
 RecordReferenceCorpusInsertionAuditPayload input,
 ReferenceCorpusInsertionDraftPayload draft,
 CancellationToken cancellationToken)
 {
 var recomposedText = RecomposeDraft(draft.Pieces, draft.Transitions);
 if (!string.Equals(recomposedText, draft.AssembledText, StringComparison.Ordinal))
 {
 throw new InvalidOperationException("The assembled draft does not match its audited pieces and transitions.");
 }

 var diagnostics = new List<string>();
 var anchorIds = new HashSet<long>();
 var maxSimilarity = 0d;
 foreach (var piece in draft.Pieces)
 {
 var source = await ReadLicensedSourceAsync(connection, Required(input.SessionId, nameof(input.SessionId)), piece, cancellationToken);
 var policy = source.MaxVerbatimRatio is { } ratio
 ? new ReferenceCorpusSimilarityPolicy(Math.Clamp(ratio, 0, 1), Math.Clamp(ratio, 0, 1))
 : source.ReusePolicy == ReferenceCorpusReusePolicies.VerbatimOk
 ? ReferenceCorpusSimilarityPolicy.VerbatimOkDefault
 : ReferenceCorpusSimilarityPolicy.AdaptedOnlyDefault;
 var result = ReferenceCorpusSimilarityGate.Evaluate(
 new ReferenceCorpusSimilarityPiece(piece.PieceId, piece.NodeId, source.Text, piece.OutputText),
 policy);
 maxSimilarity = Math.Max(maxSimilarity, Math.Max(result.FourGramContainmentRatio, result.LongestCommonSubstringRatio));
 diagnostics.AddRange(result.Violations.Select(violation =>
 $"{piece.PieceId}:{violation.Metric}:{violation.Actual:0.####}>{violation.Threshold:0.####}"));
 anchorIds.Add(source.AnchorId);
 }

 if (diagnostics.Count > 0)
 {
 throw new InvalidOperationException("The insertion draft failed the server-side similarity gate: " + string.Join(", ", diagnostics));
 }

 var auditId = Required(input.AuditId, nameof(input.AuditId));
 var candidateId = Required(input.CandidateId, nameof(input.CandidateId));
 var assembledHash = StableAuditHash(draft.AssembledText);
 if (await AuditAlreadyRecordedAsync(connection, auditId, candidateId, assembledHash, cancellationToken))
 {
 return true;
 }

 return await ExecuteAsync(
 connection,
 "INSERT INTO reference_insertion_audits(audit_id,session_id,novel_id,chapter_number,candidate_id,assembled_text_hash,source_anchor_ids_json,max_similarity,gate_passed,diagnostics_json,created_at) VALUES($id,$session,$novel,$chapter,$candidate,$hash,$anchors,$similarity,1,$diagnostics,$time);",
 cancellationToken,
 ("$id", auditId),
 ("$session", input.SessionId),
 ("$novel", input.NovelId),
 ("$chapter", input.ChapterNumber),
 ("$candidate", candidateId),
 ("$hash", assembledHash),
 ("$anchors", JsonSerializer.Serialize(anchorIds.Order())),
 ("$similarity", maxSimilarity),
 ("$diagnostics", JsonSerializer.Serialize(diagnostics)),
 ("$time", DateTimeOffset.UtcNow.ToString("O"))) > 0;
 }

 private static async ValueTask<LicensedAuditSource> ReadLicensedSourceAsync(
 SqliteConnection connection,
 string sessionId,
 ReferenceCorpusInsertionPiecePayload piece,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT node.anchor_id,node.text,node.text_hash,member.enabled,license.license_state,license.reuse_policy,license.max_verbatim_ratio,license.cleared_for_insertion FROM reference_text_nodes AS node JOIN reference_library_members AS member ON member.anchor_id=node.anchor_id AND member.library_id=$library JOIN reference_session_library_binding AS binding ON binding.library_id=member.library_id AND binding.session_id=$session LEFT JOIN reference_source_license AS license ON license.anchor_id=node.anchor_id WHERE node.node_id=$node;";
 command.Parameters.AddWithValue("$library", piece.LibraryId);
 command.Parameters.AddWithValue("$session", sessionId);
 command.Parameters.AddWithValue("$node", piece.NodeId);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 if (!await reader.ReadAsync(cancellationToken))
 {
 throw new InvalidOperationException($"Source node {piece.NodeId} is outside the active corpus scope.");
 }

 var anchorId = reader.GetInt64(0);
 var sourceHash = reader.GetString(2);
 var enabled = reader.GetInt32(3) != 0;
 var licenseState = reader.IsDBNull(4) ? "unknown" : reader.GetString(4);
 var reusePolicy = reader.IsDBNull(5) ? "forbidden" : reader.GetString(5);
 double? maxVerbatimRatio = reader.IsDBNull(6) ? null : reader.GetDouble(6);
 var cleared = !reader.IsDBNull(7) && reader.GetInt32(7) != 0;
 if (anchorId != piece.AnchorId || !string.Equals(sourceHash, piece.SourceTextHash, StringComparison.Ordinal))
 {
 throw new InvalidOperationException($"Source identity changed for node {piece.NodeId}.");
 }

 if (!enabled || !cleared || licenseState is "unknown" or "forbidden" or "restricted" ||
 reusePolicy is "forbidden" or "reference_only")
 {
 throw new InvalidOperationException($"Source node {piece.NodeId} is not licensed for insertion.");
 }

 return new LicensedAuditSource(anchorId, reader.GetString(1), reusePolicy, maxVerbatimRatio);
 }

 private static async ValueTask<bool> AuditAlreadyRecordedAsync(
 SqliteConnection connection,
 string auditId,
 string candidateId,
 string assembledHash,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT assembled_text_hash,candidate_id FROM reference_insertion_audits WHERE audit_id=$id;";
 command.Parameters.AddWithValue("$id", auditId);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 if (!await reader.ReadAsync(cancellationToken))
 {
 return false;
 }

 if (!string.Equals(reader.GetString(0), assembledHash, StringComparison.Ordinal) ||
 !string.Equals(reader.GetString(1), candidateId, StringComparison.Ordinal))
 {
 throw new InvalidOperationException("The insertion audit id is already bound to another candidate.");
 }

 return true;
 }

 private static string RecomposeDraft(
 IReadOnlyList<ReferenceCorpusInsertionPiecePayload> pieces,
 IReadOnlyList<ReferenceCorpusTransitionPayload> transitions)
 {
 var transitionsByPair = transitions
 .GroupBy(transition => (transition.AfterPieceId, transition.BeforePieceId))
 .ToDictionary(group => group.Key, group => group.First());
 var parts = new List<string>();
 for (var index = 0; index < pieces.Count; index++)
 {
 if (!string.IsNullOrWhiteSpace(pieces[index].OutputText)) parts.Add(pieces[index].OutputText.Trim());
 if (index + 1 < pieces.Count &&
 transitionsByPair.TryGetValue((pieces[index].PieceId, pieces[index + 1].PieceId), out var transition) &&
 !string.IsNullOrWhiteSpace(transition.Text))
 {
 parts.Add(transition.Text.Trim());
 }
 }

 return string.Join(Environment.NewLine, parts);
 }

 private static string StableAuditHash(string value) =>
 Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

 private sealed record LicensedAuditSource(long AnchorId, string Text, string ReusePolicy, double? MaxVerbatimRatio);
}
