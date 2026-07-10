using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceCorpusBlueprintIterationCoordinator : IReferenceCorpusBlueprintIterationCoordinator
{
 private const string Origin = "corpus_blueprint_session";
 private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

 private readonly AppInitializationOptions _options;
 private readonly IReferenceCorpusWritingService _writing;
 private readonly SemaphoreSlim _mutex = new(1, 1);

 public SqliteReferenceCorpusBlueprintIterationCoordinator(
 IReferenceCorpusWritingService writing,
 AppInitializationOptions? options = null)
 {
 _writing = writing ?? throw new ArgumentNullException(nameof(writing));
 _options = options ?? new AppInitializationOptions();
 }

 public async ValueTask<ReferenceCorpusBlueprintSessionPayload?> GetAsync(
 GetReferenceCorpusBlueprintSessionPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 ValidateSessionIdentity(input.NovelId, input.ChapterNumber, input.SessionId);

 await _mutex.WaitAsync(cancellationToken);
 try
 {
 await using var connection = await OpenConnectionAsync(cancellationToken);
 return await ReadLatestSessionAsync(
 connection,
 input.NovelId,
 input.ChapterNumber,
 input.SessionId,
 cancellationToken);
 }
 finally
 {
 _mutex.Release();
 }
 }

 public async ValueTask<ReferenceCorpusBlueprintSessionPayload> AdvanceAsync(
 AdvanceReferenceCorpusBlueprintSessionPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 ValidateAdvance(input);
 var generationInput = input.GenerationInput;
 var novelId = generationInput?.ChapterContext.NovelId ?? 0;
 var chapterNumber = generationInput?.ChapterContext.ChapterNumber ?? 0;

 await _mutex.WaitAsync(cancellationToken);
 try
 {
 await using var connection = await OpenConnectionAsync(cancellationToken);
 var requestEventId = EventId(input.SessionId, input.RequestId);
 var idempotent = await ReadByEventIdAsync(connection, requestEventId, cancellationToken);
 if (idempotent is not null)
 {
 return idempotent;
 }

 ReferenceCorpusBlueprintSessionPayload? current = null;
 if (generationInput is not null)
 {
 ValidateSessionIdentity(novelId, chapterNumber, input.SessionId);
 current = await ReadLatestSessionAsync(
 connection,
 novelId,
 chapterNumber,
 input.SessionId,
 cancellationToken);
 }
 else
 {
 current = await ReadLatestSessionByIdAsync(connection, input.SessionId, cancellationToken);
 if (current is null)
 {
 throw new InvalidOperationException("Blueprint session does not exist.");
 }

 novelId = current.NovelId;
 chapterNumber = current.ChapterNumber;
 }

 if (string.Equals(current?.Status, ReferenceCorpusBlueprintSessionStatuses.Accepted, StringComparison.Ordinal))
 {
 throw new InvalidOperationException("Accepted blueprint sessions are terminal.");
 }

 var next = input.Action switch
 {
 ReferenceCorpusBlueprintSessionActions.Generate => await GenerateAsync(input, generationInput!, current, cancellationToken),
 ReferenceCorpusBlueprintSessionActions.Select => Select(input, current!),
 ReferenceCorpusBlueprintSessionActions.Revise => await ReviseAsync(input, generationInput!, current, cancellationToken),
 ReferenceCorpusBlueprintSessionActions.Accept => Accept(input, current!),
 _ => throw new InvalidOperationException("Unsupported blueprint session action.")
 };

 await PersistEventAsync(connection, requestEventId, input.Action, next, cancellationToken);
 return next;
 }
 finally
 {
 _mutex.Release();
 }
 }

 private async ValueTask<ReferenceCorpusBlueprintSessionPayload> GenerateAsync(
 AdvanceReferenceCorpusBlueprintSessionPayload input,
 GenerateReferenceCorpusBlueprintCandidatesPayload generationInput,
 ReferenceCorpusBlueprintSessionPayload? current,
 CancellationToken cancellationToken)
 {
 if (current is not null)
 {
 throw new InvalidOperationException("Use revise for an existing blueprint session.");
 }

 var candidates = await _writing.GenerateBlueprintCandidatesAsync(
 generationInput with { Feedback = null },
 cancellationToken);
 return BuildSession(
 input.SessionId,
 generationInput,
 candidates,
 iteration: 1,
 checklist: [],
 selectedBlueprintId: string.Empty,
 rejectedBlueprintIds: []);
 }

 private async ValueTask<ReferenceCorpusBlueprintSessionPayload> ReviseAsync(
 AdvanceReferenceCorpusBlueprintSessionPayload input,
 GenerateReferenceCorpusBlueprintCandidatesPayload generationInput,
 ReferenceCorpusBlueprintSessionPayload? current,
 CancellationToken cancellationToken)
 {
 if (current is null)
 {
 throw new InvalidOperationException("Generate the blueprint session before revising it.");
 }

 var checklist = NormalizeChecklist(input.Checklist);
 if (checklist.Count != ReferenceCorpusBlueprintChecklistDimensions.All.Count ||
 checklist.All(item => string.Equals(item.Decision, ReferenceCorpusBlueprintChecklistDecisions.Accepted, StringComparison.Ordinal)))
 {
 throw new InvalidOperationException("Revision requires a complete checklist with at least one revise decision.");
 }

 var selected = FindCandidate(current, input.SelectedBlueprintId);
 var feedback = new ReferenceCorpusBlueprintFeedbackPayload(
 RejectedBlueprintIds: [selected.Blueprint.BlueprintId],
 RejectedNodeIds: selected.Blueprint.Beats.SelectMany(beat => beat.NodeIds).Distinct(StringComparer.Ordinal).ToArray(),
 AvoidLibraryIds: selected.SourceDistribution.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).ToArray(),
 AvoidAnchorIds: selected.SourceDistribution.Select(source => source.AnchorId).Distinct().ToArray(),
 ProblemTags: checklist
 .Where(item => string.Equals(item.Decision, ReferenceCorpusBlueprintChecklistDecisions.Revise, StringComparison.Ordinal))
 .SelectMany(item => item.ProblemTags.Append("checklist:" + item.Dimension))
 .Distinct(StringComparer.Ordinal)
 .ToArray(),
 Notes: string.Join(" | ", checklist.Select(item => item.Notes).Where(note => !string.IsNullOrWhiteSpace(note))));
 var candidates = await _writing.GenerateBlueprintCandidatesAsync(
 generationInput with { Feedback = feedback },
 cancellationToken);
 return BuildSession(
 input.SessionId,
 generationInput,
 candidates,
 current.Iteration + 1,
 checklist,
 selectedBlueprintId: string.Empty,
 rejectedBlueprintIds: [selected.Blueprint.BlueprintId]);
}

 private static ReferenceCorpusBlueprintSessionPayload Select(
 AdvanceReferenceCorpusBlueprintSessionPayload input,
 ReferenceCorpusBlueprintSessionPayload current)
 {
 var selected = FindCandidate(current, input.SelectedBlueprintId);
 return current with
 {
 SelectedBlueprintId = selected.Blueprint.BlueprintId,
 UpdatedAt = DateTimeOffset.UtcNow
 };
 }

 private static ReferenceCorpusBlueprintSessionPayload Accept(
 AdvanceReferenceCorpusBlueprintSessionPayload input,
 ReferenceCorpusBlueprintSessionPayload current)
 {
 var checklist = NormalizeChecklist(input.Checklist);
 if (checklist.Count != ReferenceCorpusBlueprintChecklistDimensions.All.Count ||
 checklist.Any(item => !string.Equals(item.Decision, ReferenceCorpusBlueprintChecklistDecisions.Accepted, StringComparison.Ordinal)))
 {
 throw new InvalidOperationException("Accept requires every expert checklist dimension to be accepted.");
 }

 var selected = FindCandidate(current, input.SelectedBlueprintId);
 return current with
 {
 Status = ReferenceCorpusBlueprintSessionStatuses.Accepted,
 SelectedBlueprintId = selected.Blueprint.BlueprintId,
 AcceptedBlueprintId = selected.Blueprint.BlueprintId,
 Checklist = checklist,
 UpdatedAt = DateTimeOffset.UtcNow
 };
 }

 private static ReferenceCorpusBlueprintSessionPayload BuildSession(
 string sessionId,
 GenerateReferenceCorpusBlueprintCandidatesPayload input,
 ReferenceCorpusBlueprintCandidatesPayload candidates,
 int iteration,
 IReadOnlyList<ReferenceCorpusBlueprintChecklistItemPayload> checklist,
 string selectedBlueprintId,
 IReadOnlyList<string> rejectedBlueprintIds)
 {
 var normalizedCandidates = candidates with
 {
 Iteration = new ReferenceCorpusBlueprintIterationPayload(
 iteration,
 ReferenceCorpusBlueprintSessionStatuses.AwaitingFeedback,
 iteration > 1,
 candidates.Candidates.Count,
 candidates.Candidates.Select(candidate => candidate.Blueprint.BlueprintId).Distinct(StringComparer.Ordinal).Count(),
 rejectedBlueprintIds,
 candidates.Candidates.Count > 0,
 candidates.Candidates.Count > 0)
 };
 return new ReferenceCorpusBlueprintSessionPayload(
 sessionId,
 input.ChapterContext.NovelId,
 input.ChapterContext.ChapterNumber,
 ReferenceCorpusBlueprintSessionStatuses.AwaitingFeedback,
 iteration,
 selectedBlueprintId,
 string.Empty,
 checklist,
 candidates.Candidates.Select(candidate => candidate.Blueprint.Strategy).Distinct(StringComparer.Ordinal).ToArray(),
 normalizedCandidates,
 DateTimeOffset.UtcNow)
 {
 NaturalLanguageGoal = input.NaturalLanguageGoal
 };
}

 private static ReferenceCorpusBlueprintCandidatePayload FindCandidate(
 ReferenceCorpusBlueprintSessionPayload session,
 string? blueprintId)
 {
 var normalized = (blueprintId ?? string.Empty).Trim();
 return session.Candidates.Candidates.FirstOrDefault(candidate =>
 string.Equals(candidate.Blueprint.BlueprintId, normalized, StringComparison.Ordinal))
 ?? throw new InvalidOperationException("Selected blueprint is not part of the current session iteration.");
 }

 private static IReadOnlyList<ReferenceCorpusBlueprintChecklistItemPayload> NormalizeChecklist(
 IReadOnlyList<ReferenceCorpusBlueprintChecklistItemPayload>? checklist)
 {
 return (checklist ?? [])
 .Select(item => item with
 {
 Dimension = (item.Dimension ?? string.Empty).Trim().ToLowerInvariant(),
 Decision = (item.Decision ?? string.Empty).Trim().ToLowerInvariant(),
 ProblemTags = (item.ProblemTags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).Distinct(StringComparer.Ordinal).ToArray(),
 Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes.Trim()
 })
 .Where(item => ReferenceCorpusBlueprintChecklistDimensions.All.Contains(item.Dimension, StringComparer.Ordinal))
 .Where(item => ReferenceCorpusBlueprintChecklistDecisions.All.Contains(item.Decision, StringComparer.Ordinal))
 .GroupBy(item => item.Dimension, StringComparer.Ordinal)
 .Select(group => group.Last())
 .OrderBy(item => ChecklistDimensionOrder(item.Dimension))
.ToArray();
}

 private static int ChecklistDimensionOrder(string dimension) => dimension switch
 {
 ReferenceCorpusBlueprintChecklistDimensions.EmotionArc => 0,
 ReferenceCorpusBlueprintChecklistDimensions.Rhythm => 1,
 ReferenceCorpusBlueprintChecklistDimensions.TechniqueDiversity => 2,
 ReferenceCorpusBlueprintChecklistDimensions.SceneTemplate => 3,
 ReferenceCorpusBlueprintChecklistDimensions.SourceDistribution => 4,
 _ => int.MaxValue
 };

 private static void ValidateAdvance(AdvanceReferenceCorpusBlueprintSessionPayload input)
 {
 if (string.IsNullOrWhiteSpace(input.SessionId) || string.IsNullOrWhiteSpace(input.RequestId))
 {
 throw new ArgumentException("SessionId and RequestId are required.", nameof(input));
 }

 if (!ReferenceCorpusBlueprintSessionActions.All.Contains(input.Action, StringComparer.Ordinal))
 {
 throw new ArgumentException("Action is invalid.", nameof(input));
 }

 if (input.Action is ReferenceCorpusBlueprintSessionActions.Generate or ReferenceCorpusBlueprintSessionActions.Revise &&
 input.GenerationInput is null)
 {
 throw new ArgumentException("GenerationInput is required for generate and revise.", nameof(input));
 }
 }

 private static void ValidateSessionIdentity(long novelId, int chapterNumber, string sessionId)
 {
 if (novelId <= 0 || chapterNumber <= 0 || string.IsNullOrWhiteSpace(sessionId))
 {
 throw new ArgumentException("NovelId, ChapterNumber, and SessionId are required.");
 }
 }

 private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
 {
 var databasePath = Path.Combine(
 await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
 "reference-anchor",
 "index.sqlite");
 Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
 var connection = new SqliteConnection(
 new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
 await connection.OpenAsync(cancellationToken);
 await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
 return connection;
 }

 private static async ValueTask PersistEventAsync(
 SqliteConnection connection,
 string eventId,
 string action,
 ReferenceCorpusBlueprintSessionPayload session,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = """
 INSERT OR IGNORE INTO reference_user_feedback
 (feedback_id, novel_id, target_type, target_id, decision, material_id, candidate_id,
 blueprint_id, beat_id, feedback_tags_json, note, edited_text_hash, origin, created_at)
 VALUES
 ($feedback_id, $novel_id, $target_type, $target_id, $decision, '', $candidate_id,
 0, '', $payload_json, $note, '', $origin, $created_at);
 """;
 command.Parameters.AddWithValue("$feedback_id", eventId);
 command.Parameters.AddWithValue("$novel_id", session.NovelId);
 command.Parameters.AddWithValue("$target_type", ReferenceFeedbackTargetTypes.Blueprint);
 command.Parameters.AddWithValue("$target_id", SessionTargetId(session.ChapterNumber, session.SessionId));
 command.Parameters.AddWithValue("$decision", action == ReferenceCorpusBlueprintSessionActions.Accept
 ? ReferenceFeedbackDecisions.Accepted
 : ReferenceFeedbackDecisions.Edited);
 command.Parameters.AddWithValue("$candidate_id", eventId);
 command.Parameters.AddWithValue("$payload_json", JsonSerializer.Serialize(session, JsonOptions));
 command.Parameters.AddWithValue("$note", action);
 command.Parameters.AddWithValue("$origin", Origin);
 command.Parameters.AddWithValue("$created_at", session.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 private static async ValueTask<ReferenceCorpusBlueprintSessionPayload?> ReadByEventIdAsync(
 SqliteConnection connection,
 string eventId,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT feedback_tags_json
 FROM reference_user_feedback
 WHERE feedback_id = $feedback_id AND origin = $origin
 LIMIT 1;
 """;
 command.Parameters.AddWithValue("$feedback_id", eventId);
 command.Parameters.AddWithValue("$origin", Origin);
 return DeserializeSession(await command.ExecuteScalarAsync(cancellationToken));
 }

 private static async ValueTask<ReferenceCorpusBlueprintSessionPayload?> ReadLatestSessionAsync(
 SqliteConnection connection,
 long novelId,
 int chapterNumber,
 string sessionId,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT feedback_tags_json
 FROM reference_user_feedback
 WHERE novel_id = $novel_id
 AND target_type = $target_type
 AND target_id = $target_id
 AND origin = $origin
 ORDER BY created_at DESC, feedback_id DESC
 LIMIT 1;
 """;
 command.Parameters.AddWithValue("$novel_id", novelId);
 command.Parameters.AddWithValue("$target_type", ReferenceFeedbackTargetTypes.Blueprint);
 command.Parameters.AddWithValue("$target_id", SessionTargetId(chapterNumber, sessionId));
 command.Parameters.AddWithValue("$origin", Origin);
 return DeserializeSession(await command.ExecuteScalarAsync(cancellationToken));
 }

 private static async ValueTask<ReferenceCorpusBlueprintSessionPayload?> ReadLatestSessionByIdAsync(
 SqliteConnection connection,
 string sessionId,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = """
 SELECT feedback_tags_json
 FROM reference_user_feedback
 WHERE target_type = $target_type
 AND target_id LIKE $target_id_suffix
 AND origin = $origin
 ORDER BY created_at DESC, feedback_id DESC
 LIMIT 1;
 """;
 command.Parameters.AddWithValue("$target_type", ReferenceFeedbackTargetTypes.Blueprint);
 command.Parameters.AddWithValue("$target_id_suffix", "chapter-%:" + sessionId);
 command.Parameters.AddWithValue("$origin", Origin);
 return DeserializeSession(await command.ExecuteScalarAsync(cancellationToken));
 }

 private static ReferenceCorpusBlueprintSessionPayload? DeserializeSession(object? value)
 {
 return value is string json
 ? JsonSerializer.Deserialize<ReferenceCorpusBlueprintSessionPayload>(json, JsonOptions)
 : null;
 }

 private static string SessionTargetId(int chapterNumber, string sessionId) =>
 "chapter-" + chapterNumber.ToString(CultureInfo.InvariantCulture) + ":" + sessionId;

 private static string EventId(string sessionId, string requestId)
 {
 var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.Trim() + "\n" + requestId.Trim()));
 return "corpus-blueprint-session-" + Convert.ToHexString(bytes).ToLowerInvariant()[..24];
 }
}
