using System.Text.Json;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class ReferenceAdvancedMaterialAnalysisSchemaVersions
{
    public const string V1 = "reference-advanced-material-v1";
}

public sealed record ReferenceAdvancedMaterialAnalysisInput(
    long AnchorId,
    string NodeId,
    string NodeType,
    string Family,
    string NodeText,
    int? MaxOutputTokens = null);

public sealed record ReferenceAdvancedMaterialAnalysisOutput(
    string Json,
    int TokensSpent);

// 观测层分析器接口：给定一个节点文本与 family，产出该 family 的观测草稿 JSON。
// 证据偏移必须落在 node_text 内；node_id 由调用方回填（模型不产出 node_id）。
public interface IReferenceAdvancedMaterialAnalyzer
{
    ValueTask<ReferenceAdvancedMaterialAnalysisOutput> AnalyzeAsync(
        ReferenceAdvancedMaterialAnalysisInput input,
        CancellationToken cancellationToken);
}

public static class ReferenceAdvancedMaterialAnalysisParser
{
    public static IReadOnlyList<AdvancedMaterialObservationDraft> ParseObservations(
        string json,
        string nodeId,
        string expectedFamily)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(nodeId))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("family", out var family) ||
                family.ValueKind != JsonValueKind.String ||
                !string.Equals(family.GetString(), expectedFamily, StringComparison.Ordinal) ||
                !root.TryGetProperty("observations", out var observations) ||
                observations.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var drafts = new List<AdvancedMaterialObservationDraft>();
            foreach (var item in observations.EnumerateArray())
            {
                var draft = ParseObservation(item, nodeId, expectedFamily);
                if (draft is not null)
                {
                    drafts.Add(draft);
                }
            }

            return drafts;
        }
    }

    private static AdvancedMaterialObservationDraft? ParseObservation(JsonElement item, string nodeId, string family)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("feature_key", out var featureKey) ||
            featureKey.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !item.TryGetProperty("confidence", out var confidence) ||
            confidence.ValueKind != JsonValueKind.Number ||
            !confidence.TryGetDouble(out var confidenceValue) ||
            !item.TryGetProperty("evidence", out var evidence) ||
            evidence.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var evidenceDrafts = new List<AdvancedMaterialEvidenceDraft>();
        foreach (var span in evidence.EnumerateArray())
        {
            if (span.ValueKind != JsonValueKind.Object ||
                !span.TryGetProperty("start", out var start) ||
                !start.TryGetInt32(out var startValue) ||
                !span.TryGetProperty("end", out var end) ||
                !end.TryGetInt32(out var endValue))
            {
                return null;
            }

            evidenceDrafts.Add(new AdvancedMaterialEvidenceDraft(nodeId, startValue, endValue));
        }

        if (evidenceDrafts.Count == 0)
        {
            return null;
        }

        var explanation = item.TryGetProperty("explanation", out var explanationElement) &&
            explanationElement.ValueKind == JsonValueKind.String
            ? explanationElement.GetString()
            : null;

        return new AdvancedMaterialObservationDraft(
            Family: family,
            FeatureKey: featureKey.GetString()!,
            Value: value.GetString()!,
            Explanation: explanation,
            Confidence: confidenceValue,
            Evidence: evidenceDrafts);
    }
}
