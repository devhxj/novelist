using System.Security.Cryptography;
using System.Text;

namespace Novelist.Core.App;

public sealed record ReferenceCorpusTechniqueSpecimenIdentity(
 string SpecimenId,
 string RunId,
 string SourceNodeId,
 string TechniqueFamily)
{
 public static ReferenceCorpusTechniqueSpecimenIdentity Create(
 string runId,
 string sourceNodeId,
 string techniqueFamily)
 {
 EnsureCanonical(runId, nameof(runId));
 EnsureCanonical(sourceNodeId, nameof(sourceNodeId));
 EnsureCanonical(techniqueFamily, nameof(techniqueFamily));

 var key = string.Join('\u001F', runId, sourceNodeId, techniqueFamily);
 var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
 return new ReferenceCorpusTechniqueSpecimenIdentity(
 "spec_" + hash,
 runId,
 sourceNodeId,
 techniqueFamily);
 }

 private static void EnsureCanonical(string value, string parameterName)
 {
 if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
 {
 throw new ArgumentException("Technique specimen generation key parts must be nonblank and canonical.", parameterName);
 }
 }
}
