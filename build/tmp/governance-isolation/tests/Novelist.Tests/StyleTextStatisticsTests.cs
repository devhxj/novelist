using System.Text.Json;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class StyleTextStatisticsTests
{
    [Fact]
    public void AnalyzeHandlesChineseEnglishMixedTextDeterministically()
    {
        var stats = StyleTextStatistics.Analyze("雨停了。He waited.\n\n风很冷！She left?");

        Assert.Equal("style_sample_stats_v2", stats.SchemaVersion);
        Assert.Equal(25, stats.CharacterCount);
        Assert.Equal(10, stats.WordCount);
        Assert.Equal(4, stats.SentenceCount);
        Assert.Equal([4, 9, 4, 8], stats.SentenceLengthDistribution);
        Assert.Equal(6.25, stats.AverageSentenceChars);
        Assert.Equal(2.2776, stats.SentenceLengthStdDev);
        Assert.Equal(2, stats.ParagraphCount);
        Assert.Equal(12.5, stats.AverageParagraphChars);
        Assert.Equal(16, stats.PunctuationPer100Chars);
        Assert.Equal(
            JsonSerializer.Serialize(stats),
            JsonSerializer.Serialize(StyleTextStatistics.Analyze("雨停了。He waited.\n\n风很冷！She left?")));
    }

    [Fact]
    public void AnalyzeReportsQuoteDensityParagraphRhythmAndMarkerRatios()
    {
        var stats = StyleTextStatistics.Analyze("“别走。”她想。\n雨声贴着窗。");

        Assert.Equal(14, stats.CharacterCount);
        Assert.Equal(9, stats.WordCount);
        Assert.Equal(3, stats.SentenceCount);
        Assert.Equal([5, 3, 6], stats.SentenceLengthDistribution);
        Assert.Equal(2, stats.ParagraphCount);
        Assert.Equal(7, stats.AverageParagraphChars);
        Assert.Equal(14.2857, stats.QuoteDensity);
        Assert.Equal(0.2143, stats.DialogueRatio);
        Assert.True(stats.InteriorityRatio > 0);
        Assert.True(stats.SensoryRatio > 0);
    }

    [Fact]
    public void AnalyzeReturnsZeroStatsForEmptyText()
    {
        var stats = StyleTextStatistics.Analyze(" \r\n\t ");

        Assert.Equal("style_sample_stats_v2", stats.SchemaVersion);
        Assert.Equal(0, stats.CharacterCount);
        Assert.Equal(0, stats.WordCount);
        Assert.Equal(0, stats.SentenceCount);
        Assert.Empty(stats.SentenceLengthDistribution);
        Assert.Equal(0, stats.AverageSentenceChars);
        Assert.Equal(0, stats.SentenceLengthStdDev);
        Assert.Equal(0, stats.PunctuationPer100Chars);
        Assert.Equal(0, stats.QuoteDensity);
        Assert.Equal(0, stats.ParagraphCount);
        Assert.Equal(0, stats.AverageParagraphChars);
    }
}
