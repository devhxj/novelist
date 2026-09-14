using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceAdvancedMaterialAnalysisParserTests
{
    [Fact]
    public void ParsesGroundedObservationsAndBackfillsNodeId()
    {
        const string json = """
            {"schema_version":"reference-advanced-material-v1","family":"craft","observations":[
              {"feature_key":"information_delivery","value":"drip_fed","explanation":"逐点放料","confidence":0.8,"evidence":[{"start":0,"end":10}]}
            ]}
            """;

        var drafts = ReferenceAdvancedMaterialAnalysisParser.ParseObservations(json, "node-1", ReferenceAdvancedMaterialFamilies.Craft);

        var draft = Assert.Single(drafts);
        Assert.Equal(ReferenceAdvancedMaterialFamilies.Craft, draft.Family);
        Assert.Equal("information_delivery", draft.FeatureKey);
        Assert.Equal("drip_fed", draft.Value);
        Assert.Equal("逐点放料", draft.Explanation);
        Assert.Equal(0.8, draft.Confidence);
        var evidence = Assert.Single(draft.Evidence);
        Assert.Equal("node-1", evidence.NodeId);
        Assert.Equal(0, evidence.StartOffset);
        Assert.Equal(10, evidence.EndOffset);
    }

    [Fact]
    public void ReturnsEmptyWhenFamilyDoesNotMatch()
    {
        const string json = """
            {"family":"world","observations":[{"feature_key":"world_introduction","value":"action_embedded","confidence":0.5,"evidence":[{"start":0,"end":4}]}]}
            """;

        var drafts = ReferenceAdvancedMaterialAnalysisParser.ParseObservations(json, "node-1", ReferenceAdvancedMaterialFamilies.Craft);

        Assert.Empty(drafts);
    }

    [Fact]
    public void DropsItemsMissingRequiredFields()
    {
        const string json = """
            {"family":"structure","observations":[
              {"value":"cliffhanger","confidence":0.5,"evidence":[{"start":0,"end":4}]},
              {"feature_key":"hook_type","value":"cliffhanger","confidence":0.5,"evidence":[]},
              {"feature_key":"hook_type","value":"cliffhanger","confidence":0.5,"evidence":[{"start":0,"end":4}]}
            ]}
            """;

        var drafts = ReferenceAdvancedMaterialAnalysisParser.ParseObservations(json, "node-2", ReferenceAdvancedMaterialFamilies.Structure);

        var draft = Assert.Single(drafts);
        Assert.Equal("hook_type", draft.FeatureKey);
        Assert.Equal("node-2", draft.Evidence[0].NodeId);
    }

    [Fact]
    public void ReturnsEmptyOnMalformedJsonOrEmptyObservations()
    {
        Assert.Empty(ReferenceAdvancedMaterialAnalysisParser.ParseObservations("not json", "node-1", ReferenceAdvancedMaterialFamilies.Craft));
        Assert.Empty(ReferenceAdvancedMaterialAnalysisParser.ParseObservations(
            """{"family":"craft","observations":[]}""",
            "node-1",
            ReferenceAdvancedMaterialFamilies.Craft));
    }
}
