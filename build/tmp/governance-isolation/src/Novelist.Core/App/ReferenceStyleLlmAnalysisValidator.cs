using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class ReferenceStyleLlmAnalysisValidator
{
    private const int MaxLabels = 64;
    private const int MaxEvidencePerLabel = 8;
    private const double MaxLlmConfidence = 0.95;

    private static readonly HashSet<string> AllowedRootProperties = new(StringComparer.Ordinal)
    {
        "schema_version",
        "labels"
    };

    private static readonly HashSet<string> AllowedLabelProperties = new(StringComparer.Ordinal)
    {
        "feature_key",
        "label",
        "confidence",
        "evidence"
    };

    private static readonly HashSet<string> AllowedEvidenceProperties = new(StringComparer.Ordinal)
    {
        "source_segment_id",
        "material_id",
        "start_offset",
        "end_offset"
    };

    public static IReadOnlyList<string> SupportedFeatureKeys { get; } = ReferenceStyleTaxonomy.FeatureKeys;

    public static ReferenceStyleLlmAnalysisValidationResultPayload Validate(
        long profileId,
        string modelOutputJson,
        IReadOnlyList<ReferenceStyleAnalysisWindowPayload> windows)
    {
        if (profileId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Style profile id must be positive.");
        }

        var normalizedWindows = NormalizeWindows(windows);
        if (normalizedWindows.Count == 0)
        {
            throw new ArgumentException("At least one bounded source window is required.", nameof(windows));
        }

        using var document = TryParse(modelOutputJson, out var parseDiagnostics);
        if (document is null)
        {
            return new ReferenceStyleLlmAnalysisValidationResultPayload(
                ReferenceStyleLlmAnalysisValidationStatuses.InvalidJson,
                [],
                [],
                parseDiagnostics);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return InvalidSchema("Model output must be a JSON object.");
        }

        if (HasUnexpectedProperties(root, AllowedRootProperties, out var unexpectedRoot))
        {
            return InvalidSchema($"Model output contains unsupported property '{unexpectedRoot}'.");
        }

        if (!root.TryGetProperty("schema_version", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.String ||
            schemaVersion.GetString() != ReferenceStyleLlmAnalysisSchemaVersions.V1)
        {
            return InvalidSchema($"Model output schema_version must be '{ReferenceStyleLlmAnalysisSchemaVersions.V1}'.");
        }

        if (!root.TryGetProperty("labels", out var labels) ||
            labels.ValueKind != JsonValueKind.Array)
        {
            return InvalidSchema("Model output labels must be an array.");
        }

        if (labels.GetArrayLength() > MaxLabels)
        {
            return InvalidSchema($"Model output labels must contain at most {MaxLabels} items.");
        }

        var evidenceSpans = new List<ReferenceStyleEvidenceSpanPayload>();
        var rejected = new List<ReferenceStyleLlmAnalysisRejectedLabelPayload>();
        var diagnostics = new List<string>();
        var index = 0;
        foreach (var labelElement in labels.EnumerateArray())
        {
            ValidateLabel(
                profileId,
                labelElement,
                index,
                normalizedWindows,
                evidenceSpans,
                rejected,
                diagnostics);
            index++;
        }

        var status = evidenceSpans.Count switch
        {
            > 0 when rejected.Count == 0 => ReferenceStyleLlmAnalysisValidationStatuses.Passed,
            > 0 => ReferenceStyleLlmAnalysisValidationStatuses.Partial,
            _ => ReferenceStyleLlmAnalysisValidationStatuses.Rejected
        };

        if (diagnostics.Count == 0)
        {
            diagnostics.Add(status == ReferenceStyleLlmAnalysisValidationStatuses.Passed
                ? "LLM style analysis output accepted."
                : "LLM style analysis output produced no accepted grounded labels.");
        }

        return new ReferenceStyleLlmAnalysisValidationResultPayload(status, evidenceSpans, rejected, diagnostics);
    }

    private static void ValidateLabel(
        long profileId,
        JsonElement labelElement,
        int labelIndex,
        IReadOnlyList<ReferenceStyleAnalysisWindowPayload> windows,
        List<ReferenceStyleEvidenceSpanPayload> evidenceSpans,
        List<ReferenceStyleLlmAnalysisRejectedLabelPayload> rejected,
        List<string> diagnostics)
    {
        if (labelElement.ValueKind != JsonValueKind.Object)
        {
            rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(string.Empty, string.Empty, "Label must be an object."));
            return;
        }

        if (HasUnexpectedProperties(labelElement, AllowedLabelProperties, out var unexpectedLabel))
        {
            rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(string.Empty, string.Empty, $"Label contains unsupported property '{unexpectedLabel}'."));
            return;
        }

        var featureKey = ReadString(labelElement, "feature_key", maxLength: 128);
        var label = ReadString(labelElement, "label", maxLength: 128);
        if (string.IsNullOrWhiteSpace(featureKey) || string.IsNullOrWhiteSpace(label))
        {
            rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(featureKey, label, "Label requires non-empty feature_key and label."));
            return;
        }

        if (!ReferenceStyleTaxonomy.IsSupportedFeatureKey(featureKey))
        {
            rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(featureKey, label, "Unsupported style feature key."));
            return;
        }

        if (!ReferenceStyleTaxonomy.IsSupportedLabel(featureKey, label))
        {
            rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(featureKey, label, "Unsupported style label for feature key."));
            return;
        }

        if (!labelElement.TryGetProperty("confidence", out var confidenceElement) ||
            confidenceElement.ValueKind != JsonValueKind.Number ||
            !confidenceElement.TryGetDouble(out var confidence) ||
            double.IsNaN(confidence) ||
            confidence < 0)
        {
            rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(featureKey, label, "Label confidence must be a non-negative number."));
            return;
        }

        if (confidence > MaxLlmConfidence)
        {
            confidence = MaxLlmConfidence;
            diagnostics.Add($"Label '{featureKey}:{label}' confidence downgraded to {MaxLlmConfidence.ToString("0.##", CultureInfo.InvariantCulture)}.");
        }

        if (!labelElement.TryGetProperty("evidence", out var evidenceArray) ||
            evidenceArray.ValueKind != JsonValueKind.Array ||
            evidenceArray.GetArrayLength() == 0)
        {
            rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(featureKey, label, "Label requires at least one grounded evidence span."));
            return;
        }

        if (evidenceArray.GetArrayLength() > MaxEvidencePerLabel)
        {
            rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(featureKey, label, $"Label can cite at most {MaxEvidencePerLabel} evidence spans."));
            return;
        }

        var acceptedForLabel = new List<ReferenceStyleEvidenceSpanPayload>();
        var evidenceIndex = 0;
        foreach (var evidenceElement in evidenceArray.EnumerateArray())
        {
            if (!TryBuildEvidence(
                profileId,
                featureKey,
                label,
                confidence,
                labelIndex,
                evidenceIndex,
                evidenceElement,
                windows,
                out var evidence,
                out var reason))
            {
                rejected.Add(new ReferenceStyleLlmAnalysisRejectedLabelPayload(featureKey, label, reason));
                diagnostics.Add($"Rejected ungrounded label '{featureKey}:{label}': {reason}");
                return;
            }

            acceptedForLabel.Add(evidence);
            evidenceIndex++;
        }

        evidenceSpans.AddRange(acceptedForLabel);
    }

    private static bool TryBuildEvidence(
        long profileId,
        string featureKey,
        string label,
        double confidence,
        int labelIndex,
        int evidenceIndex,
        JsonElement evidenceElement,
        IReadOnlyList<ReferenceStyleAnalysisWindowPayload> windows,
        out ReferenceStyleEvidenceSpanPayload evidence,
        out string reason)
    {
        evidence = null!;
        reason = string.Empty;
        if (evidenceElement.ValueKind != JsonValueKind.Object)
        {
            reason = "Evidence must be an object.";
            return false;
        }

        if (HasUnexpectedProperties(evidenceElement, AllowedEvidenceProperties, out var unexpectedEvidence))
        {
            reason = $"Evidence contains unsupported property '{unexpectedEvidence}'.";
            return false;
        }

        var sourceSegmentId = ReadString(evidenceElement, "source_segment_id", maxLength: 256);
        var materialId = ReadOptionalString(evidenceElement, "material_id", maxLength: 256);
        if (string.IsNullOrWhiteSpace(sourceSegmentId))
        {
            reason = "Evidence requires source_segment_id.";
            return false;
        }

        if (!TryReadInt(evidenceElement, "start_offset", out var startOffset) ||
            !TryReadInt(evidenceElement, "end_offset", out var endOffset) ||
            startOffset < 0 ||
            endOffset <= startOffset)
        {
            reason = "Evidence offsets must be positive and increasing.";
            return false;
        }

        var window = windows.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceSegmentId, sourceSegmentId, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(materialId) ||
                string.Equals(candidate.MaterialId, materialId, StringComparison.Ordinal)) &&
            startOffset >= candidate.StartOffset &&
            endOffset <= candidate.EndOffset);
        if (window is null)
        {
            reason = "Evidence span is ungrounded or outside bounded source window.";
            return false;
        }

        evidence = new ReferenceStyleEvidenceSpanPayload(
            BuildEvidenceId(profileId, featureKey, label, labelIndex, evidenceIndex, sourceSegmentId, startOffset, endOffset),
            profileId,
            window.AnchorId,
            window.SourceSegmentId,
            string.IsNullOrWhiteSpace(materialId) ? window.MaterialId : materialId,
            featureKey,
            label,
            startOffset,
            endOffset,
            window.TextHash,
            Math.Round(confidence, 4),
            ReferenceStyleAnalyzerSources.LlmAssisted);
        return true;
    }

    private static List<ReferenceStyleAnalysisWindowPayload> NormalizeWindows(IReadOnlyList<ReferenceStyleAnalysisWindowPayload>? windows)
    {
        var normalized = new List<ReferenceStyleAnalysisWindowPayload>();
        foreach (var window in windows ?? [])
        {
            if (window.AnchorId <= 0 ||
                string.IsNullOrWhiteSpace(window.WindowId) ||
                string.IsNullOrWhiteSpace(window.SourceSegmentId) ||
                string.IsNullOrWhiteSpace(window.TextHash) ||
                window.StartOffset < 0 ||
                window.EndOffset <= window.StartOffset)
            {
                throw new ArgumentException("Style analysis windows must be bounded source spans with positive provenance ids.", nameof(windows));
            }

            normalized.Add(window with
            {
                WindowId = window.WindowId.Trim(),
                SourceSegmentId = window.SourceSegmentId.Trim(),
                MaterialId = string.IsNullOrWhiteSpace(window.MaterialId) ? null : window.MaterialId.Trim(),
                TextHash = window.TextHash.Trim()
            });
        }

        return normalized;
    }

    private static JsonDocument? TryParse(string modelOutputJson, out IReadOnlyList<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(modelOutputJson))
        {
            diagnostics = ["Model output must be valid JSON and must not be empty."];
            return null;
        }

        try
        {
            diagnostics = [];
            return JsonDocument.Parse(modelOutputJson);
        }
        catch (JsonException exception)
        {
            diagnostics = [$"Model output must be valid JSON. {exception.Message}"];
            return null;
        }
    }

    private static ReferenceStyleLlmAnalysisValidationResultPayload InvalidSchema(string diagnostic)
    {
        return new ReferenceStyleLlmAnalysisValidationResultPayload(
            ReferenceStyleLlmAnalysisValidationStatuses.InvalidSchema,
            [],
            [],
            [diagnostic]);
    }

    private static bool HasUnexpectedProperties(JsonElement element, HashSet<string> allowed, out string unexpected)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                unexpected = property.Name;
                return true;
            }
        }

        unexpected = string.Empty;
        return false;
    }

    private static string ReadString(JsonElement element, string propertyName, int maxLength)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return NormalizeText(value.GetString(), maxLength);
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName, int maxLength)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? NormalizeText(value.GetString(), maxLength)
            : string.Empty;
    }

    private static string NormalizeText(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
        {
            normalized = normalized[..maxLength];
        }

        return normalized.Any(char.IsControl) ? string.Empty : normalized;
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int result)
    {
        result = 0;
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out result);
    }

    private static string BuildEvidenceId(
        long profileId,
        string featureKey,
        string label,
        int labelIndex,
        int evidenceIndex,
        string sourceSegmentId,
        int startOffset,
        int endOffset)
    {
        var raw = string.Join('|', profileId, featureKey, label, labelIndex, evidenceIndex, sourceSegmentId, startOffset, endOffset);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var builder = new StringBuilder("llm-style-");
        for (var i = 0; i < 8; i++)
        {
            builder.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
