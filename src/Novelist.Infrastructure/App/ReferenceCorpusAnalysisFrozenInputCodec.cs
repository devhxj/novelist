using System.Security.Cryptography;
using System.Text.Json;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceCorpusAnalysisFrozenInputCodec
{
 private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

 public static (string Json, string Hash) Serialize<T>(T payload)
 {
 ArgumentNullException.ThrowIfNull(payload);
 var json = JsonSerializer.Serialize(payload, JsonOptions);
 return (json, SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(json));
 }

 public static T Deserialize<T>(string json, string expectedHash)
 {
 ArgumentException.ThrowIfNullOrWhiteSpace(json);
 ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
 var actualHash = SqliteReferenceCorpusAnalysisJobStore.ComputeInputPayloadHash(json);
 if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
 {
 throw new ReferenceCorpusAnalysisJobConflictException("analysis_snapshot_corrupt: frozen payload hash does not match.");
 }

 return JsonSerializer.Deserialize<T>(json, JsonOptions)
 ?? throw new ReferenceCorpusAnalysisJobConflictException("analysis_snapshot_corrupt: frozen payload is empty.");
 }

 public static string ComputeEvidenceSetHash(IReadOnlyList<ReferenceCorpusTechniqueObservationEvidence> observations)
 {
 ArgumentNullException.ThrowIfNull(observations);
 if (observations.Count == 0)
 {
 throw new ArgumentException("Technique evidence set cannot be empty.", nameof(observations));
 }

 var canonical = observations
 .OrderBy(item => item.ObservationId, StringComparer.Ordinal)
 .ToArray();
 var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions);
 return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
 }
}
