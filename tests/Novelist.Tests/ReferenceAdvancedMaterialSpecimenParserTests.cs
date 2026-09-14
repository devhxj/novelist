using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceAdvancedMaterialSpecimenParserTests
{
    [Fact]
    public void ParsesMechanismDraftAndBackfillsNodeId()
    {
        const string json = """
            {"schema_version":"reference-advanced-material-v1","specimens":[{
              "family":"technique","feature_key":"technique_kind","abstract":"用听觉细节压住场景",
              "trigger_context":"推门后安静","why_it_works":["先动作后感官更贴近视角"],
              "effect_on_reader":"读者感到压迫","transfer_template":"主体[动作]后接[感官回响]",
              "transfer_slots":{"action":"推门"},"world_context_dependencies":["依赖雨夜设定"],
              "failure_modes":["描写过长"],"anti_patterns":["堆砌感官"],"confidence":0.75,
              "evidence":[{"start":0,"end":17}]}]}
            """;

        var drafts = ReferenceAdvancedMaterialSpecimenAnalysisParser.ParseSpecimens(json, "node-1");

        var draft = Assert.Single(drafts);
        Assert.Equal(ReferenceAdvancedMaterialFamilies.Technique, draft.Family);
        Assert.Equal("technique_kind", draft.FeatureKey);
        Assert.Equal("主体[动作]后接[感官回响]", draft.TransferTemplate);
        Assert.Equal("推门", draft.TransferSlots["action"]);
        Assert.Equal(["描写过长"], draft.FailureModes);
        Assert.Equal(["依赖雨夜设定"], draft.WorldContextDependencies);
        Assert.Equal("node-1", draft.Evidence[0].NodeId);
        Assert.Equal(17, draft.Evidence[0].EndOffset);
    }

    [Fact]
    public void DropsUnsupportedFamilyAndMissingCoreFields()
    {
        const string json = """
            {"specimens":[
              {"family":"not_real","feature_key":"technique_kind","abstract":"a","transfer_template":"t","confidence":0.5,"evidence":[{"start":0,"end":4}]},
              {"family":"craft","abstract":"a","transfer_template":"t","confidence":0.5,"evidence":[{"start":0,"end":4}]},
              {"family":"craft","feature_key":"information_delivery","abstract":"a","confidence":0.5,"evidence":[{"start":0,"end":4}]}
            ]}
            """;

        var drafts = ReferenceAdvancedMaterialSpecimenAnalysisParser.ParseSpecimens(json, "node-2");

        Assert.Empty(drafts);
    }

    [Fact]
    public void ReturnsEmptyOnMalformedJson()
    {
        Assert.Empty(ReferenceAdvancedMaterialSpecimenAnalysisParser.ParseSpecimens("not json", "node-1"));
    }
}
