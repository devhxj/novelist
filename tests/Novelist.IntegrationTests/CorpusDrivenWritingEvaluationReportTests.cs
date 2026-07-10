using System.Text.Json;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class CorpusDrivenWritingEvaluationReportTests
{
    [Fact]
    public async Task CommandWritesSanitizedSummaryForValidFixture()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-evaluation-contract.json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N"));

        try
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();

            var exitCode = await CorpusDrivenWritingEvaluationCommand.RunAsync(
                ["--fixture", fixturePath, "--output", outputDirectory],
                standardOutput,
                standardError,
                new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Empty(standardError.ToString());
            Assert.True(File.Exists(Path.Combine(outputDirectory, "corpus-writing-evaluation-report.json")));
            using var summary = JsonDocument.Parse(standardOutput.ToString());
            Assert.Equal("corpus-writing-evaluation-report-v1", summary.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal("contract", summary.RootElement.GetProperty("dataset_kind").GetString());
            Assert.DoesNotContain(fixturePath, standardOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(outputDirectory, standardOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ContractFixtureProducesStableRedactedEvaluationReport()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-evaluation-contract.json");
        var outputRoot = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N"));

        try
        {
            var generatedAt = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
            var first = await CorpusDrivenWritingEvaluationReport.EvaluateFileAsync(
                fixturePath,
                Path.Combine(outputRoot, "first"),
                generatedAt,
                CancellationToken.None);
            var second = await CorpusDrivenWritingEvaluationReport.EvaluateFileAsync(
                fixturePath,
                Path.Combine(outputRoot, "second"),
                generatedAt,
                CancellationToken.None);

            Assert.Equal("contract", first.DatasetKind);
            Assert.Equal(3, first.QueryCaseCount);
            Assert.Equal(3, first.BlueprintCaseCount);
            Assert.Equal(3, first.InsertionCaseCount);
            Assert.Equal(0.833333, first.Retrieval.RecallAtK, 6);
            Assert.Equal(0.833333, first.Retrieval.ReasonAccuracy, 6);
            Assert.Equal(15d, first.Retrieval.LatencyP50Milliseconds);
            Assert.Equal(25d, first.Retrieval.LatencyP95Milliseconds);
            Assert.Equal(0.666667, first.Blueprints.FeedbackImprovementRate, 6);
            Assert.Equal(0.818182, first.Prose.SourceFidelityRate, 6);
            Assert.Equal(0.1d, first.Prose.UserEditRatio, 6);
            Assert.Equal(2d, first.Prose.AverageIterations);

            var firstJsonPath = Path.Combine(outputRoot, "first", "corpus-writing-evaluation-report.json");
            var secondJsonPath = Path.Combine(outputRoot, "second", "corpus-writing-evaluation-report.json");
            var firstMarkdownPath = Path.Combine(outputRoot, "first", "corpus-writing-evaluation-report.md");
            var secondMarkdownPath = Path.Combine(outputRoot, "second", "corpus-writing-evaluation-report.md");

            Assert.True(File.Exists(firstJsonPath));
            Assert.True(File.Exists(firstMarkdownPath));
            Assert.Equal(await File.ReadAllTextAsync(firstJsonPath), await File.ReadAllTextAsync(secondJsonPath));
            Assert.Equal(await File.ReadAllTextAsync(firstMarkdownPath), await File.ReadAllTextAsync(secondMarkdownPath));

            using var reportJson = JsonDocument.Parse(await File.ReadAllTextAsync(firstJsonPath));
            Assert.Equal("corpus-writing-evaluation-report-v1", reportJson.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal("contract", reportJson.RootElement.GetProperty("dataset_kind").GetString());

            var reportText = await File.ReadAllTextAsync(firstJsonPath) + await File.ReadAllTextAsync(firstMarkdownPath);
            foreach (var forbiddenValue in new[]
            {
                "雨声贴着窗缝",
                "候选正文不应出现在报告里",
                "C:\\private\\novel.txt",
                outputRoot
            })
            {
                Assert.DoesNotContain(forbiddenValue, reportText, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EvaluatorRejectsRawSourceAndLocalPathFields()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-evaluation-contract.json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N"));
        var malformedPath = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var fixture = await File.ReadAllTextAsync(fixturePath);
            fixture = fixture.Replace(
                "\"dataset_kind\": \"contract\",",
                "\"dataset_kind\": \"contract\",\n  \"source_text\": \"雨声贴着窗缝\",\n  \"source_path\": \"C:\\\\private\\\\novel.txt\",");
            await File.WriteAllTextAsync(malformedPath, fixture);

            await Assert.ThrowsAsync<InvalidDataException>(() => CorpusDrivenWritingEvaluationReport.EvaluateFileAsync(
                malformedPath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
        }
        finally
        {
            if (File.Exists(malformedPath))
            {
                File.Delete(malformedPath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("\"goal_match\"", "\"unregistered_reason_code\"")]
    public async Task EvaluatorRejectsReasonCodesOutsideTheFixedCodebook(string existingCode, string replacementCode)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-evaluation-contract.json");
        var malformedPath = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N") + ".json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = await File.ReadAllTextAsync(fixturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
            await File.WriteAllTextAsync(malformedPath, fixture.Replace(existingCode, replacementCode, StringComparison.Ordinal));

            await Assert.ThrowsAsync<InvalidDataException>(() => CorpusDrivenWritingEvaluationReport.EvaluateFileAsync(
                malformedPath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
        }
        finally
        {
            if (File.Exists(malformedPath))
            {
                File.Delete(malformedPath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EvaluatorRejectsDuplicateReviewersWithinOneInsertionCase()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-evaluation-contract.json");
        var malformedPath = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N") + ".json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N"));
        const string reviewer = "{ \"reviewer_id_hash\": \"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\", \"fidelity\": 5, \"plot_fit\": 4, \"naturalness\": 4 }";

        try
        {
            var fixture = await File.ReadAllTextAsync(fixturePath);
            fixture = fixture.Replace(reviewer, reviewer + ",\n        " + reviewer, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
            await File.WriteAllTextAsync(malformedPath, fixture);

            await Assert.ThrowsAsync<InvalidDataException>(() => CorpusDrivenWritingEvaluationReport.EvaluateFileAsync(
                malformedPath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
        }
        finally
        {
            if (File.Exists(malformedPath))
            {
                File.Delete(malformedPath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EvaluatorRejectsFixtureLargerThanItsInputBudget()
    {
        var oversizedFixturePath = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N") + ".json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(oversizedFixturePath)!);
            await using (var stream = new FileStream(oversizedFixturePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(CorpusDrivenWritingEvaluationReport.MaximumFixtureBytes + 1L);
            }

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => CorpusDrivenWritingEvaluationReport.EvaluateFileAsync(
                oversizedFixturePath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None));

            Assert.Contains("maximum", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(oversizedFixturePath))
            {
                File.Delete(oversizedFixturePath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HumanDatasetRejectsInsufficientSampleCounts()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-evaluation-contract.json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N"));
        var humanFixturePath = Path.Combine(Path.GetTempPath(), "novelist-corpus-writing-evaluation", Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var fixture = await File.ReadAllTextAsync(fixturePath);
            await File.WriteAllTextAsync(humanFixturePath, fixture.Replace("\"dataset_kind\": \"contract\"", "\"dataset_kind\": \"human\""));

            await Assert.ThrowsAsync<InvalidDataException>(() => CorpusDrivenWritingEvaluationReport.EvaluateFileAsync(
                humanFixturePath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
        }
        finally
        {
            if (File.Exists(humanFixturePath))
            {
                File.Delete(humanFixturePath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}
