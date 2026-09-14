using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class CorpusNeedBuilderTests
{
    [Fact]
    public void MapsKeywordsToNarrativeAndProseDuties()
    {
        var need = CorpusNeedBuilder.FromPlanText("本 beat：通过内心独白推进，并用环境描写施加压力。");

        Assert.Contains("interiority", need.NarrativeDuties);
        Assert.Contains("source_backed_detail", need.ProseDuties);
        Assert.Empty(need.ProseDuties.Where(duty => need.NarrativeDuties.Contains(duty)));
    }

    [Fact]
    public void TruncatesQueryAndNormalizesLineBreaks()
    {
        var longPlan = "第一行\n第二行 " + new string('x', CorpusNeedBuilder.PlanQueryMaxLength + 50);

        var need = CorpusNeedBuilder.FromPlanText(longPlan);

        Assert.Equal(CorpusNeedBuilder.PlanQueryMaxLength, need.Query.Length);
        Assert.DoesNotContain('\n', need.Query);
    }

    [Fact]
    public void EmptyPlanProducesEmptyNeed()
    {
        var need = CorpusNeedBuilder.FromPlanText(null);

        Assert.Equal(string.Empty, need.Query);
        Assert.Empty(need.NarrativeDuties);
        Assert.Empty(need.ProseDuties);
    }

    [Fact]
    public void BeatNeedUsesShorterCap()
    {
        var need = CorpusNeedBuilder.FromBeat(new string('雨', CorpusNeedBuilder.BeatQueryMaxLength + 20));

        Assert.Equal(CorpusNeedBuilder.BeatQueryMaxLength, need.Query.Length);
    }
}
