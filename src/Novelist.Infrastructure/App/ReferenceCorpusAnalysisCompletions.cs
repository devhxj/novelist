using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceCorpusAnalysisCompletionKinds
{
 public const string FeatureObservations = "feature_observations";
 public const string TechniqueSpecimen = "technique_specimen";
}

internal sealed record ReferenceCorpusFeatureCompletionPayload(
 string RunId,
 long AnchorId,
 string NodeId,
 string NodeType,
 DateTimeOffset CreatedAt,
 IReadOnlyList<ReferenceCorpusFeatureObservationCandidate> Observations);

internal sealed record ReferenceCorpusTechniqueCompletionPayload(
 string RunId,
 long AnchorId,
 DateTimeOffset CreatedAt,
 ReferenceCorpusTechniqueSpecimenCandidate Candidate);

internal sealed record ReferenceCorpusAnalysisCompletionEnvelope(
 string CompletionKey,
 string JobId,
 string RunId,
 string InputSnapshotId,
 int Ordinal,
 int InvocationNumber,
 int AttemptNumber,
 int ReservedTokens,
 string OutputKind,
 string OutputPayloadJson,
 string OutputPayloadHash,
 int TokensSpent,
 string DiagnosticsJson,
 DateTimeOffset ModelCompletedAt);

internal static class ReferenceCorpusAnalysisCompletionCodec
{
 private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

 public static string CreateKey(string inputSnapshotId, int ordinal, int invocationNumber)
 {
 var canonical = string.Join('\u001f', inputSnapshotId, ordinal, invocationNumber);
 return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
 }

 public static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, JsonOptions);

 public static T Deserialize<T>(string json) =>
 JsonSerializer.Deserialize<T>(json, JsonOptions)
 ?? throw new ReferenceCorpusAnalysisJobConflictException(
 "analysis_completion_corrupt: completion payload is empty.");

 public static string Hash(string json) =>
 SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(json);
}
