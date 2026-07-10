using System.Text.Json;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceStyleGoldenFixtureEvaluatorTests
{
    [Fact]
    public async Task GoldenStyleFixturesProduceReportsWithoutSourceOrCandidateText()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference-style-golden-fixtures.json");
        var outputDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "output", "reference-style-eval"));

        var report = await ReferenceStyleGoldenFixtureEvaluator.EvaluateFileAsync(
            fixturePath,
            outputDirectory,
            new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal(8, report.FixtureCount);
        Assert.Equal(8, report.PassedCount);
        Assert.All(report.Results, result =>
        {
            Assert.Equal("passed", result.Status);
            Assert.NotEmpty(result.NumericChecks);
            Assert.NotEmpty(result.AdvancedLabelChecks);
            Assert.Equal(6, result.CandidateEvaluation.ScoreCount);
            Assert.True(result.CandidateEvaluation.Passed);
            Assert.DoesNotContain("source_text", result.Diagnostics, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("candidate_text", result.Diagnostics, StringComparer.OrdinalIgnoreCase);
        });

        var jsonPath = Path.Combine(outputDirectory, "reference-style-golden-report.json");
        var markdownPath = Path.Combine(outputDirectory, "reference-style-golden-report.md");
        Assert.True(File.Exists(jsonPath));
        Assert.True(File.Exists(markdownPath));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
        Assert.Equal("reference-style-golden-evaluation-v1", json.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(8, json.RootElement.GetProperty("fixture_count").GetInt32());
        Assert.Equal(8, json.RootElement.GetProperty("passed_count").GetInt32());

        var jsonText = await File.ReadAllTextAsync(jsonPath);
        var markdownText = await File.ReadAllTextAsync(markdownPath);
        foreach (var leakedText in new[]
        {
            "她说：“别回头。”",
            "雨声贴着窗缝",
            "候选正文不应出现在报告里"
        })
        {
            Assert.DoesNotContain(leakedText, jsonText, StringComparison.Ordinal);
            Assert.DoesNotContain(leakedText, markdownText, StringComparison.Ordinal);
        }
    }
}
