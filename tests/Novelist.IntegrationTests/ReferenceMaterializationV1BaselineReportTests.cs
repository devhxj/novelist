using System.Text.Json;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationV1BaselineReportTests
{
    [Fact]
    public async Task LegacyBaselineCommandWritesRedactedSummary()
    {
        var root = Path.Combine(Path.GetTempPath(), "novelist-materialization-v1-baseline-command", Guid.NewGuid().ToString("N"));
        try
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();
            var exitCode = await ReferenceMaterializationV1BaselineCommand.RunAsync(
                [
                    "--calibration", FixturePath("materialization-quality-calibration-v1.json"),
                    "--holdout", FixturePath("materialization-quality-holdout-v1.json"),
                    "--output", root
                ],
                standardOutput,
                standardError,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Empty(standardError.ToString());
            using var summary = JsonDocument.Parse(standardOutput.ToString());
            Assert.Equal(ReferenceMaterializationV1BaselineReport.ReportSchemaVersion, summary.RootElement.GetProperty("schema_version").GetString());
            Assert.False(summary.RootElement.GetProperty("holdout_short_noise_rejection_recall").GetDouble() > 0);
            Assert.DoesNotContain(root, standardOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("嗯。", standardOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LegacyBaselineReportUsesActualMaterialProjectionAndWritesStableRedactedJson()
    {
        var calibration = FixturePath("materialization-quality-calibration-v1.json");
        var holdout = FixturePath("materialization-quality-holdout-v1.json");
        var root = Path.Combine(Path.GetTempPath(), "novelist-materialization-v1-baseline-report", Guid.NewGuid().ToString("N"));
        try
        {
            var first = await ReferenceMaterializationV1BaselineReport.EvaluateAsync(
                calibration,
                holdout,
                Path.Combine(root, "first"),
                CancellationToken.None);
            var second = await ReferenceMaterializationV1BaselineReport.EvaluateAsync(
                calibration,
                holdout,
                Path.Combine(root, "second"),
                CancellationToken.None);

            Assert.Equal(ReferenceMaterializationV1BaselineReport.ReportSchemaVersion, first.SchemaVersion);
            Assert.Equal("legacy_deterministic_v1_all_supported_segments", first.BaselineKind);
            Assert.True(first.Calibration.RawNodeCount > 0);
            Assert.True(first.Calibration.MaterialCount > first.Calibration.CaseCount);
            Assert.True(first.Calibration.UniqueSourceSpanCount < first.Calibration.MaterialCount);
            Assert.True(first.Calibration.MaterialOverlapPairRate > 0d);
            Assert.Equal(0d, first.Holdout.ShortNoiseRejectionRecall);
            Assert.Equal(1d, first.Holdout.ShortValuableRecall);
            Assert.False(first.Holdout.ActiveSearchEvaluated);
            Assert.Null(first.Holdout.CandidateSpanIouMedian);
            Assert.Equal(first, second);

            var firstReportPath = Path.Combine(root, "first", "reference-materialization-v1-baseline-report.json");
            var secondReportPath = Path.Combine(root, "second", "reference-materialization-v1-baseline-report.json");
            var firstJson = await File.ReadAllTextAsync(firstReportPath);
            Assert.Equal(firstJson, await File.ReadAllTextAsync(secondReportPath));
            using var report = JsonDocument.Parse(firstJson);
            Assert.Equal(ReferenceMaterializationV1BaselineReport.ReportSchemaVersion, report.RootElement.GetProperty("schema_version").GetString());
            Assert.DoesNotContain("嗯。", firstJson, StringComparison.Ordinal);
            Assert.DoesNotContain("quality-baseline.md", firstJson, StringComparison.Ordinal);
            Assert.DoesNotContain(root, firstJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string FixturePath(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "corpus-driven-writing",
        fileName);
}
