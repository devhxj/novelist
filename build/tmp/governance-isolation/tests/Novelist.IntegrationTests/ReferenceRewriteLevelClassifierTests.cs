using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceRewriteLevelClassifierTests
{
    [Fact]
    public void ClassifyReturnsL0ForExactReuse()
    {
        var level = ReferenceRewriteLevelClassifier.Classify(
            "他在门口停了很久。",
            "他在门口停了很久。");

        Assert.Equal(ReferenceRewriteLevels.L0, level);
    }

    [Fact]
    public void ClassifyReturnsL1ForDeclaredSlotOnlyReplacement()
    {
        var level = ReferenceRewriteLevelClassifier.Classify(
            "他握住{{object}}，没有立刻说话。",
            "他握住门把手，没有立刻说话。",
            [new ReferenceSlotValuePayload("object", "门把手")]);

        Assert.Equal(ReferenceRewriteLevels.L1, level);
    }

    [Fact]
    public void ClassifyReturnsL2ForWhitespaceAndPunctuationOnlyChange()
    {
        var level = ReferenceRewriteLevelClassifier.Classify(
            "他在门口停了很久。",
            "他在门口 停了很久。");

        Assert.Equal(ReferenceRewriteLevels.L2, level);
    }

    [Fact]
    public void ClassifyReturnsL2ForLightConnectorEdit()
    {
        var level = ReferenceRewriteLevelClassifier.Classify(
            "他在门口停了很久。",
            "他却在门口停了很久。");

        Assert.Equal(ReferenceRewriteLevels.L2, level);
    }

    [Fact]
    public void ReportNonSlotEditsIgnoresDeclaredSlotReplacementAndReportsConnectorEdit()
    {
        var edits = ReferenceNonSlotEditReporter.Report(
            "他握住{{object}}，没有立刻说话。",
            "他却握住门把手，没有立刻说话。",
            [new ReferenceSlotValuePayload("object", "门把手")]);

        var edit = Assert.Single(edits);
        Assert.Contains("inserted", edit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("却", edit, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportNonSlotEditsReturnsEmptyForDeclaredSlotOnlyReplacement()
    {
        var edits = ReferenceNonSlotEditReporter.Report(
            "他握住{{object}}，没有立刻说话。",
            "他握住门把手，没有立刻说话。",
            [new ReferenceSlotValuePayload("object", "门把手")]);

        Assert.Empty(edits);
    }

    [Fact]
    public void ClassifyReturnsL3ForLooseStructuralRewrite()
    {
        var level = ReferenceRewriteLevelClassifier.Classify(
            "雨声压低了整条街的呼吸，他在门口停了很久。",
            "雨声仍压着街面，他迟迟停在门口，没有马上进去。");

        Assert.Equal(ReferenceRewriteLevels.L3, level);
    }

    [Fact]
    public void ClassifyReturnsL4ForUnrelatedRewrite()
    {
        var level = ReferenceRewriteLevelClassifier.Classify(
            "雨声压低了整条街的呼吸，他在门口停了很久。",
            "飞船越过晨昏线，舰桥上的警报把所有人都惊醒了。");

        Assert.Equal(ReferenceRewriteLevels.L4, level);
    }
}
