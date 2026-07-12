using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Novelist.Contracts.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationQualityFixtureTests
{
    [Fact]
    public void MaterializationQualityFixturesKeepCalibrationAndHoldoutPhysicallySeparate()
    {
        using var calibration = LoadFixture("materialization-quality-calibration-v1.json");
        using var holdout = LoadFixture("materialization-quality-holdout-v1.json");

        Assert.Equal("reference-materialization-quality-fixture-v1", calibration.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("calibration", calibration.RootElement.GetProperty("split").GetString());
        Assert.Equal("holdout", holdout.RootElement.GetProperty("split").GetString());
        Assert.NotEmpty(calibration.RootElement.GetProperty("cases").EnumerateArray());
        Assert.NotEmpty(holdout.RootElement.GetProperty("cases").EnumerateArray());
    }

    [Fact]
    public void MaterializationQualityFixturesContainGroundedDecisionAnnotationsAcrossRequiredCases()
    {
        using var calibration = LoadFixture("materialization-quality-calibration-v1.json");
        using var holdout = LoadFixture("materialization-quality-holdout-v1.json");
        var calibrationIndex = MaterializationQualityFixtureContract.Validate(calibration.RootElement, "calibration");
        var holdoutIndex = MaterializationQualityFixtureContract.Validate(holdout.RootElement, "holdout");

        Assert.Empty(calibrationIndex.CaseIds.Intersect(holdoutIndex.CaseIds, StringComparer.Ordinal));
        Assert.Empty(calibrationIndex.NodeIds.Intersect(holdoutIndex.NodeIds, StringComparer.Ordinal));
        var allCategories = calibrationIndex.Categories.Concat(holdoutIndex.Categories).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(
            allCategories,
            MaterializationQualityFixtureContract.RequiredCategories.ToHashSet(StringComparer.Ordinal));
        Assert.True(MaterializationQualityFixtureContract.RequiredCategories.All(allCategories.Contains));
        var allStyles = calibrationIndex.Styles.Concat(holdoutIndex.Styles).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["suspense", "urban", "xianxia"], allStyles.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void MaterializationQualityFixtureContractRejectsDuplicateIdsUnknownEnumsAndInvalidSpans()
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath("materialization-quality-calibration-v1.json")))!.AsObject();
        root["cases"]!.AsArray().Add(root["cases"]![0]!.DeepClone());
        Assert.Throws<InvalidOperationException>(() => MaterializationQualityFixtureContract.Validate(
            JsonDocument.Parse(root.ToJsonString()).RootElement,
            "calibration"));

        root = JsonNode.Parse(File.ReadAllText(FixturePath("materialization-quality-calibration-v1.json")))!.AsObject();
        root["cases"]![0]!["expected"]!["reason_codes"]![0] = "unknown_reason";
        Assert.Throws<InvalidOperationException>(() => MaterializationQualityFixtureContract.Validate(
            JsonDocument.Parse(root.ToJsonString()).RootElement,
            "calibration"));

        root = JsonNode.Parse(File.ReadAllText(FixturePath("materialization-quality-calibration-v1.json")))!.AsObject();
        root["cases"]![0]!["expected"]!["source_spans"]![0]!["end"] = 99;
        Assert.Throws<InvalidOperationException>(() => MaterializationQualityFixtureContract.Validate(
            JsonDocument.Parse(root.ToJsonString()).RootElement,
            "calibration"));
    }

    private static JsonDocument LoadFixture(string fileName)
    {
        return JsonDocument.Parse(File.ReadAllText(FixturePath(fileName)));
    }

    private static string FixturePath(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "corpus-driven-writing",
        fileName);
}

internal static class MaterializationQualityFixtureContract
{
    public static IReadOnlyList<string> RequiredCategories { get; } =
    [
        "short_noise",
        "short_valuable",
        "dialogue_merge",
        "action_reaction_merge",
        "emotion_window",
        "transition_noise",
        "overlap_merge",
        "safety_negative"
    ];

    private static readonly IReadOnlySet<string> ReasonCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "ambiguous_boundary",
        "complete_exchange",
        "contains_state_change",
        "context_dependent",
        "duplicate_overlap",
        "fragment",
        "generic_action",
        "high_information_density",
        "low_transferability",
        "noise",
        "requires_review",
        "standalone_reveal"
    };

    public static MaterializationQualityFixtureIndex Validate(JsonElement root, string expectedSplit)
    {
        Require(root.ValueKind == JsonValueKind.Object, "Fixture root must be an object.");
        Require(root.GetProperty("schema_version").GetString() == "reference-materialization-quality-fixture-v1", "Fixture schema is invalid.");
        Require(root.GetProperty("split").GetString() == expectedSplit, "Fixture split is invalid.");
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        var styles = new HashSet<string>(StringComparer.Ordinal);
        var cases = root.GetProperty("cases");
        Require(cases.ValueKind == JsonValueKind.Array && cases.GetArrayLength() >= 10, "Fixture must contain at least ten annotated cases.");
        foreach (var item in cases.EnumerateArray())
        {
            var caseId = RequiredString(item, "case_id");
            Require(caseIds.Add(caseId), "Fixture has a duplicate case id.");
            categories.Add(RequiredString(item, "category"));
            styles.Add(RequiredString(item, "style"));
            var candidateType = RequiredString(item, "candidate_type");
            Require(ReferenceMaterializationCandidateTypes.All.Contains(candidateType, StringComparer.Ordinal), "Fixture has an unknown candidate type.");
            var nodes = item.GetProperty("source_nodes");
            Require(nodes.ValueKind == JsonValueKind.Array && nodes.GetArrayLength() > 0, "Fixture case has no source nodes.");
            var nodeLengths = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var node in nodes.EnumerateArray())
            {
                var nodeId = RequiredString(node, "node_id");
                Require(nodeIds.Add(nodeId), "Fixture has a duplicate node id.");
                var text = RequiredString(node, "text");
                Require(Hash(text) == RequiredString(node, "text_hash"), "Fixture node text hash is invalid.");
                nodeLengths.Add(nodeId, text.Length);
            }

            var expected = item.GetProperty("expected");
            Require(expected.ValueKind == JsonValueKind.Object, "Fixture case has no expected annotation.");
            Require(!expected.EnumerateObject().Any(property => property.Name is "text" or "source_text" or "model_output"), "Expected annotations must not contain prose or model output.");
            var decision = RequiredString(expected, "decision");
            Require(ReferenceMaterializationCandidateDecisions.All.Contains(decision, StringComparer.Ordinal) && decision != ReferenceMaterializationCandidateDecisions.Pending, "Fixture has an invalid expected decision.");
            var spans = expected.GetProperty("source_spans");
            Require(spans.ValueKind == JsonValueKind.Array && spans.GetArrayLength() == nodeLengths.Count, "Fixture spans must cover every source node exactly once.");
            var spannedNodeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var span in spans.EnumerateArray())
            {
                var nodeId = RequiredString(span, "node_id");
                Require(nodeLengths.TryGetValue(nodeId, out var length) && spannedNodeIds.Add(nodeId), "Fixture span node is invalid.");
                var start = span.GetProperty("start").GetInt32();
                var end = span.GetProperty("end").GetInt32();
                Require(start >= 0 && end > start && end <= length, "Fixture span is out of bounds.");
            }

            var reasonCodes = expected.GetProperty("reason_codes");
            Require(reasonCodes.ValueKind == JsonValueKind.Array && reasonCodes.GetArrayLength() > 0, "Fixture must include reason codes.");
            foreach (var reason in reasonCodes.EnumerateArray())
            {
                Require(reason.ValueKind == JsonValueKind.String && ReasonCodes.Contains(reason.GetString() ?? string.Empty), "Fixture has an unknown reason code.");
            }

            var tags = expected.GetProperty("tags");
            Require(tags.ValueKind == JsonValueKind.Object, "Fixture tags must be an object.");
        }

        return new MaterializationQualityFixtureIndex(caseIds, nodeIds, categories, styles);
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        Require(value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()), "Fixture string value is required.");
        return value.GetString()!;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal sealed record MaterializationQualityFixtureIndex(
    IReadOnlySet<string> CaseIds,
    IReadOnlySet<string> NodeIds,
    IReadOnlySet<string> Categories,
    IReadOnlySet<string> Styles);
