using System.Security.Cryptography;
using System.Text;

namespace Novelist.Core.App;

public sealed record ReferenceCorpusObservationIdentity(
    string ObservationId,
    string RunId,
    string NodeId,
    string FeatureFamily,
    string FeatureKey,
    int NormalizedEvidenceStart,
    int NormalizedEvidenceEnd)
{
    private const int NullEvidenceSentinel = -1;

    public static ReferenceCorpusObservationIdentity Create(
        string runId,
        string nodeId,
        string featureFamily,
        string featureKey,
        int? evidenceStart,
        int? evidenceEnd)
    {
        EnsureNotBlank(runId, nameof(runId));
        EnsureNotBlank(nodeId, nameof(nodeId));
        EnsureNotBlank(featureFamily, nameof(featureFamily));
        EnsureNotBlank(featureKey, nameof(featureKey));
        EnsureValidEvidenceBounds(evidenceStart, evidenceEnd);

        var normalizedEvidenceStart = evidenceStart ?? NullEvidenceSentinel;
        var normalizedEvidenceEnd = evidenceEnd ?? NullEvidenceSentinel;
        var generationKey = string.Join(
            '\u001F',
            runId,
            nodeId,
            featureFamily,
            featureKey,
            normalizedEvidenceStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
            normalizedEvidenceEnd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(generationKey))).ToLowerInvariant();

        return new ReferenceCorpusObservationIdentity(
            "obs_" + hash,
            runId,
            nodeId,
            featureFamily,
            featureKey,
            normalizedEvidenceStart,
            normalizedEvidenceEnd);
    }

    private static void EnsureNotBlank(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Observation generation key parts cannot be blank.", parameterName);
        }
    }

    private static void EnsureValidEvidenceBounds(int? evidenceStart, int? evidenceEnd)
    {
        if (evidenceStart < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceStart), evidenceStart, "Evidence start cannot be negative.");
        }

        if (evidenceEnd < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceEnd), evidenceEnd, "Evidence end cannot be negative.");
        }

        if (evidenceStart is { } start && evidenceEnd is { } end && end < start)
        {
            throw new ArgumentOutOfRangeException(nameof(evidenceEnd), evidenceEnd, "Evidence end cannot be before evidence start.");
        }
    }
}
