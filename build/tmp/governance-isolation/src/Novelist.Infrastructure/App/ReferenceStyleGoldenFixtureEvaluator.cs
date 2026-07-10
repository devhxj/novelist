using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;

namespace Novelist.Infrastructure.App;

internal static class ReferenceStyleGoldenFixtureEvaluator
{
    public const string FixtureSchemaVersion = "reference-style-golden-fixtures-v1";
    public const string ReportSchemaVersion = "reference-style-golden-evaluation-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(BridgeJson.SerializerOptions)
    {
        WriteIndented = true
    };

    private static readonly IReadOnlyList<string> RequiredCategories =
    [
        "dialogue-heavy",
        "introspective",
        "sensory",
        "action",
        "suspense",
        "emotional-restraint",
        "high-tempo-web-fiction",
        "slow-burn-literary-prose"
    ];

    private static readonly IReadOnlyList<string> RequiredRubricDimensions =
    [
        "style_fit",
        "readability",
        "originality_distance",
        "fact_safety",
        "pov_safety",
        "author_usefulness"
    ];

    public static async ValueTask<ReferenceStyleGoldenEvaluationReport> EvaluateFileAsync(
        string fixturePath,
        string outputDirectory,
        DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        if (!File.Exists(fixturePath))
        {
            throw new FileNotFoundException("Reference style golden fixture file was not found.");
        }

        var json = await File.ReadAllTextAsync(fixturePath, cancellationToken).ConfigureAwait(false);
        var document = JsonSerializer.Deserialize<ReferenceStyleGoldenFixtureDocument>(json, BridgeJson.SerializerOptions)
            ?? throw new InvalidDataException("Reference style golden fixture file is empty.");

        ValidateDocumentShape(document);

        var results = new List<ReferenceStyleGoldenFixtureEvaluationResult>(document.Fixtures!.Count);
        foreach (var fixture in document.Fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(EvaluateFixture(fixture));
        }

        var report = new ReferenceStyleGoldenEvaluationReport(
            ReportSchemaVersion,
            FixtureSchemaVersion,
            ReferenceStyleTaxonomy.Version,
            evaluatedAt,
            results.Count,
            results.Count(result => string.Equals(result.Status, "passed", StringComparison.Ordinal)),
            results.Count(result => !string.Equals(result.Status, "passed", StringComparison.Ordinal)),
            results,
            []);

        var jsonReport = JsonSerializer.Serialize(report, JsonOptions);
        var markdownReport = BuildMarkdownReport(report);
        EnsureNoSensitiveTextLeak(document, jsonReport, markdownReport);

        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "reference-style-golden-report.json"),
            jsonReport,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "reference-style-golden-report.md"),
            markdownReport,
            cancellationToken).ConfigureAwait(false);

        return report;
    }

    private static ReferenceStyleGoldenFixtureEvaluationResult EvaluateFixture(
        ReferenceStyleGoldenFixtureSpec fixture)
    {
        var diagnostics = new List<string>();
        ValidateFixtureShape(fixture, diagnostics);

        ReferenceStyleBaseline? baseline = null;
        if (diagnostics.Count == 0)
        {
            baseline = ReferenceStyleDeterministicBaselineExtractor.Build(
                fixture.ProfileId,
                fixture.Materials!.Select(material => ToMaterialSample(fixture, material)).ToArray());
        }

        var numericChecks = baseline is null
            ? []
            : EvaluateNumericChecks(baseline, fixture.ExpectedNumericRanges!, diagnostics);
        var labelChecks = EvaluateAdvancedLabelChecks(fixture.ExpectedAdvancedLabels ?? [], diagnostics);
        var candidateEvaluation = EvaluateCandidate(fixture.Candidate, diagnostics);
        var passed = diagnostics.Count == 0 &&
            numericChecks.All(check => check.Passed) &&
            labelChecks.All(check => check.Passed) &&
            candidateEvaluation.Passed;

        return new ReferenceStyleGoldenFixtureEvaluationResult(
            fixture.FixtureId ?? "missing-fixture-id",
            fixture.Category ?? "missing-category",
            passed ? "passed" : "failed",
            baseline?.AggregateConfidence ?? 0,
            numericChecks,
            labelChecks,
            candidateEvaluation,
            diagnostics);
    }

    private static IReadOnlyList<ReferenceStyleGoldenNumericCheckResult> EvaluateNumericChecks(
        ReferenceStyleBaseline baseline,
        IReadOnlyList<ReferenceStyleGoldenNumericRangeSpec> expectedRanges,
        List<string> diagnostics)
    {
        var numericByKey = baseline.Features.NumericFeatures
            .ToDictionary(feature => feature.FeatureKey, StringComparer.Ordinal);
        var checks = new List<ReferenceStyleGoldenNumericCheckResult>(expectedRanges.Count);

        foreach (var expected in expectedRanges)
        {
            if (string.IsNullOrWhiteSpace(expected.FeatureKey))
            {
                diagnostics.Add("expected numeric range has an empty feature key");
                continue;
            }

            if (expected.Min > expected.Max)
            {
                diagnostics.Add($"numeric range for {expected.FeatureKey} has min greater than max");
                checks.Add(new ReferenceStyleGoldenNumericCheckResult(
                    expected.FeatureKey,
                    0,
                    expected.Min,
                    expected.Max,
                    "unknown",
                    false));
                continue;
            }

            if (!numericByKey.TryGetValue(expected.FeatureKey, out var observed))
            {
                diagnostics.Add($"numeric feature {expected.FeatureKey} was not produced by the deterministic baseline");
                checks.Add(new ReferenceStyleGoldenNumericCheckResult(
                    expected.FeatureKey,
                    0,
                    expected.Min,
                    expected.Max,
                    "missing",
                    false));
                continue;
            }

            var passed = observed.Value >= expected.Min && observed.Value <= expected.Max;
            checks.Add(new ReferenceStyleGoldenNumericCheckResult(
                expected.FeatureKey,
                observed.Value,
                expected.Min,
                expected.Max,
                observed.Unit,
                passed));
            if (!passed)
            {
                diagnostics.Add(
                    $"numeric feature {expected.FeatureKey} value {observed.Value.ToString(CultureInfo.InvariantCulture)} is outside the expected range");
            }
        }

        return checks;
    }

    private static IReadOnlyList<ReferenceStyleGoldenAdvancedLabelCheckResult> EvaluateAdvancedLabelChecks(
        IReadOnlyList<ReferenceStyleGoldenAdvancedLabelSpec> labels,
        List<string> diagnostics)
    {
        var checks = new List<ReferenceStyleGoldenAdvancedLabelCheckResult>(labels.Count);
        foreach (var expected in labels)
        {
            if (string.IsNullOrWhiteSpace(expected.FeatureKey) || string.IsNullOrWhiteSpace(expected.Label))
            {
                diagnostics.Add("expected advanced label has an empty feature key or label");
                checks.Add(new ReferenceStyleGoldenAdvancedLabelCheckResult(
                    expected.FeatureKey ?? "missing-feature-key",
                    expected.Label ?? "missing-label",
                    "missing",
                    false));
                continue;
            }

            var supported = ReferenceStyleTaxonomy.IsSupportedFeatureKey(expected.FeatureKey) &&
                ReferenceStyleTaxonomy.IsSupportedLabel(expected.FeatureKey, expected.Label);
            var category = supported
                ? ReferenceStyleTaxonomy.GetFeature(expected.FeatureKey).Category
                : "unsupported";
            checks.Add(new ReferenceStyleGoldenAdvancedLabelCheckResult(
                expected.FeatureKey,
                expected.Label,
                category,
                supported));
            if (!supported)
            {
                diagnostics.Add($"advanced label {expected.FeatureKey}:{expected.Label} is not supported by {ReferenceStyleTaxonomy.Version}");
            }
        }

        return checks;
    }

    private static ReferenceStyleGoldenCandidateEvaluationResult EvaluateCandidate(
        ReferenceStyleGoldenCandidateSpec? candidate,
        List<string> diagnostics)
    {
        if (candidate is null)
        {
            diagnostics.Add("candidate evaluation is missing");
            return new ReferenceStyleGoldenCandidateEvaluationResult(
                "missing-candidate",
                0,
                false,
                0,
                []);
        }

        if (string.IsNullOrWhiteSpace(candidate.CandidateId))
        {
            diagnostics.Add("candidate id is missing");
        }

        if (string.IsNullOrWhiteSpace(candidate.Text))
        {
            diagnostics.Add("candidate body is missing");
        }

        var scores = candidate.RubricScores ?? [];
        var dimensions = scores
            .Select(score => score.Dimension)
            .Where(dimension => !string.IsNullOrWhiteSpace(dimension))
            .ToArray();
        var duplicateDimensions = dimensions
            .GroupBy(dimension => dimension!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var duplicate in duplicateDimensions)
        {
            diagnostics.Add($"candidate rubric dimension {duplicate} is duplicated");
        }

        var missingDimensions = RequiredRubricDimensions
            .Except(dimensions!, StringComparer.Ordinal)
            .ToArray();
        foreach (var missing in missingDimensions)
        {
            diagnostics.Add($"candidate rubric dimension {missing} is missing");
        }

        var unexpectedDimensions = dimensions!
            .Except(RequiredRubricDimensions, StringComparer.Ordinal)
            .ToArray();
        foreach (var unexpected in unexpectedDimensions)
        {
            diagnostics.Add($"candidate rubric dimension {unexpected} is not part of the golden rubric");
        }

        var scoreResults = new List<ReferenceStyleGoldenRubricScoreResult>(scores.Count);
        foreach (var score in scores)
        {
            var dimension = string.IsNullOrWhiteSpace(score.Dimension)
                ? "missing-dimension"
                : score.Dimension!;
            var maxScore = score.MaxScore <= 0 ? 5 : score.MaxScore;
            var minScore = score.MinScore <= 0 ? 3 : score.MinScore;
            var passed = score.Score >= minScore && score.Score <= maxScore;
            if (minScore > maxScore)
            {
                diagnostics.Add($"candidate rubric dimension {dimension} has min greater than max");
                passed = false;
            }

            if (score.Score < 0 || score.Score > maxScore)
            {
                diagnostics.Add($"candidate rubric dimension {dimension} has an out-of-range score");
                passed = false;
            }

            scoreResults.Add(new ReferenceStyleGoldenRubricScoreResult(
                dimension,
                score.Score,
                minScore,
                maxScore,
                passed));
        }

        var scoreCount = scoreResults.Count;
        var averageScore = scoreCount == 0
            ? 0
            : Math.Round(scoreResults.Average(score => score.Score), 4);
        var passedCandidate = scoreCount == RequiredRubricDimensions.Count &&
            missingDimensions.Length == 0 &&
            unexpectedDimensions.Length == 0 &&
            duplicateDimensions.Length == 0 &&
            scoreResults.All(score => score.Passed);

        return new ReferenceStyleGoldenCandidateEvaluationResult(
            candidate.CandidateId ?? "missing-candidate",
            scoreCount,
            passedCandidate,
            averageScore,
            scoreResults);
    }

    private static void ValidateDocumentShape(ReferenceStyleGoldenFixtureDocument document)
    {
        if (!string.Equals(document.SchemaVersion, FixtureSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unsupported reference style golden fixture schema version.");
        }

        if (document.Fixtures is null || document.Fixtures.Count == 0)
        {
            throw new InvalidDataException("Reference style golden fixture file must contain fixtures.");
        }

        if (document.Fixtures.Count != RequiredCategories.Count)
        {
            throw new InvalidDataException("Reference style golden fixture file must contain exactly eight fixtures.");
        }

        var ids = document.Fixtures
            .Select(fixture => fixture.FixtureId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        if (ids.Length != ids.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Reference style golden fixture ids must be unique.");
        }

        var categories = document.Fixtures
            .Select(fixture => fixture.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .ToArray();
        if (categories.Length != categories.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Reference style golden fixture categories must be unique.");
        }

        var missingCategories = RequiredCategories
            .Except(categories!, StringComparer.Ordinal)
            .ToArray();
        if (missingCategories.Length > 0)
        {
            throw new InvalidDataException("Reference style golden fixture file is missing required categories.");
        }

        var unexpectedCategories = categories!
            .Except(RequiredCategories, StringComparer.Ordinal)
            .ToArray();
        if (unexpectedCategories.Length > 0)
        {
            throw new InvalidDataException("Reference style golden fixture file contains unsupported categories.");
        }
    }

    private static void ValidateFixtureShape(
        ReferenceStyleGoldenFixtureSpec fixture,
        List<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(fixture.FixtureId))
        {
            diagnostics.Add("fixture id is missing");
        }

        if (string.IsNullOrWhiteSpace(fixture.Category))
        {
            diagnostics.Add("fixture category is missing");
        }

        if (fixture.ProfileId <= 0)
        {
            diagnostics.Add("fixture profile id must be positive");
        }

        if (fixture.Materials is null || fixture.Materials.Count == 0)
        {
            diagnostics.Add("fixture materials are missing");
        }
        else
        {
            var materialIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var material in fixture.Materials)
            {
                if (string.IsNullOrWhiteSpace(material.MaterialId))
                {
                    diagnostics.Add("fixture material id is missing");
                }
                else if (!materialIds.Add(material.MaterialId))
                {
                    diagnostics.Add($"fixture material id {material.MaterialId} is duplicated");
                }

                if (!ReferenceMaterialTypes.All.Contains(material.MaterialType ?? string.Empty, StringComparer.Ordinal))
                {
                    diagnostics.Add($"fixture material {material.MaterialId ?? "missing-material"} has an unsupported material type");
                }

                if (string.IsNullOrWhiteSpace(material.Text))
                {
                    diagnostics.Add($"fixture material {material.MaterialId ?? "missing-material"} body is missing");
                }
            }
        }

        if (fixture.ExpectedNumericRanges is null || fixture.ExpectedNumericRanges.Count == 0)
        {
            diagnostics.Add("fixture expected numeric ranges are missing");
        }

        if (fixture.ExpectedAdvancedLabels is null || fixture.ExpectedAdvancedLabels.Count == 0)
        {
            diagnostics.Add("fixture expected advanced labels are missing");
        }
    }

    private static ReferenceStyleMaterialSample ToMaterialSample(
        ReferenceStyleGoldenFixtureSpec fixture,
        ReferenceStyleGoldenMaterialSpec material)
    {
        var text = material.Text!;
        var materialId = material.MaterialId!;
        return new ReferenceStyleMaterialSample(
            materialId,
            material.AnchorId <= 0 ? fixture.ProfileId : material.AnchorId,
            string.IsNullOrWhiteSpace(material.SourceSegmentId) ? materialId + "-segment" : material.SourceSegmentId!,
            material.MaterialType!,
            DefaultTag(material.FunctionTag, "narration"),
            DefaultTag(material.EmotionTag, "neutral"),
            DefaultTag(material.SceneTag, "scene"),
            DefaultTag(material.PovTag, "unknown"),
            DefaultTag(material.TechniqueTag, "plain"),
            ClampConfidence(material.FunctionConfidence),
            ClampConfidence(material.EmotionConfidence),
            ClampConfidence(material.PovConfidence),
            text,
            string.IsNullOrWhiteSpace(material.SourceHash) ? HashText(fixture.FixtureId ?? "fixture") : material.SourceHash!,
            material.StartOffset < 0 ? 0 : material.StartOffset,
            material.EndOffset > material.StartOffset ? material.EndOffset : text.Length,
            string.IsNullOrWhiteSpace(material.TextHash) ? HashText(text) : material.TextHash!);
    }

    private static void EnsureNoSensitiveTextLeak(
        ReferenceStyleGoldenFixtureDocument document,
        string jsonReport,
        string markdownReport)
    {
        var sensitiveTexts = document.Fixtures!
            .SelectMany(fixture => (fixture.Materials ?? []).Select(material => material.Text)
                .Concat([fixture.Candidate?.Text]))
            .Where(text => !string.IsNullOrWhiteSpace(text) && text.Length >= 6)
            .Concat(document.LeakSentinels ?? [])
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var sensitiveText in sensitiveTexts)
        {
            if (jsonReport.Contains(sensitiveText!, StringComparison.Ordinal) ||
                markdownReport.Contains(sensitiveText!, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Reference style golden evaluation report would expose fixture body text.");
            }
        }
    }

    private static string BuildMarkdownReport(ReferenceStyleGoldenEvaluationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Reference Style Golden Evaluation");
        builder.AppendLine();
        builder.AppendLine($"- Schema: `{report.SchemaVersion}`");
        builder.AppendLine($"- Fixture schema: `{report.FixtureSchemaVersion}`");
        builder.AppendLine($"- Taxonomy: `{report.TaxonomyVersion}`");
        builder.AppendLine($"- Evaluated at: `{report.EvaluatedAt:O}`");
        builder.AppendLine($"- Fixtures: `{report.FixtureCount}`");
        builder.AppendLine($"- Passed: `{report.PassedCount}`");
        builder.AppendLine($"- Failed: `{report.FailedCount}`");
        builder.AppendLine();
        builder.AppendLine("| Fixture | Category | Status | Numeric checks | Label checks | Candidate rubric |");
        builder.AppendLine("|---|---|---|---:|---:|---:|");

        foreach (var result in report.Results)
        {
            builder.AppendLine(
                $"| `{EscapeMarkdown(result.FixtureId)}` | `{EscapeMarkdown(result.Category)}` | `{EscapeMarkdown(result.Status)}` | {PassedCount(result.NumericChecks)}/{result.NumericChecks.Count} | {PassedCount(result.AdvancedLabelChecks)}/{result.AdvancedLabelChecks.Count} | {(result.CandidateEvaluation.Passed ? "passed" : "failed")} |");
        }

        foreach (var result in report.Results)
        {
            builder.AppendLine();
            builder.AppendLine($"## {EscapeMarkdown(result.FixtureId)}");
            builder.AppendLine();
            builder.AppendLine($"- Category: `{EscapeMarkdown(result.Category)}`");
            builder.AppendLine($"- Status: `{EscapeMarkdown(result.Status)}`");
            builder.AppendLine($"- Aggregate confidence: `{result.AggregateConfidence.ToString("0.####", CultureInfo.InvariantCulture)}`");
            builder.AppendLine($"- Candidate average score: `{result.CandidateEvaluation.AverageScore.ToString("0.####", CultureInfo.InvariantCulture)}`");
            builder.AppendLine();
            builder.AppendLine("### Numeric Checks");
            builder.AppendLine();
            builder.AppendLine("| Feature | Observed | Expected | Unit | Status |");
            builder.AppendLine("|---|---:|---:|---|---|");
            foreach (var check in result.NumericChecks)
            {
                builder.AppendLine(
                    $"| `{EscapeMarkdown(check.FeatureKey)}` | {check.ObservedValue.ToString("0.####", CultureInfo.InvariantCulture)} | {check.Min.ToString("0.####", CultureInfo.InvariantCulture)}-{check.Max.ToString("0.####", CultureInfo.InvariantCulture)} | `{EscapeMarkdown(check.Unit)}` | {(check.Passed ? "passed" : "failed")} |");
            }

            builder.AppendLine();
            builder.AppendLine("### Advanced Label Checks");
            builder.AppendLine();
            builder.AppendLine("| Feature | Label | Category | Status |");
            builder.AppendLine("|---|---|---|---|");
            foreach (var check in result.AdvancedLabelChecks)
            {
                builder.AppendLine(
                    $"| `{EscapeMarkdown(check.FeatureKey)}` | `{EscapeMarkdown(check.Label)}` | `{EscapeMarkdown(check.Category)}` | {(check.Passed ? "passed" : "failed")} |");
            }
        }

        return builder.ToString();
    }

    private static int PassedCount<T>(IReadOnlyList<T> checks)
        where T : IReferenceStyleGoldenCheckResult
    {
        return checks.Count(check => check.Passed);
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal);
    }

    private static string DefaultTag(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static double ClampConfidence(double value)
    {
        return value <= 0 ? 0.7 : Math.Round(Math.Clamp(value, 0.1, 1.0), 4);
    }

    private static string HashText(string text)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed class ReferenceStyleGoldenFixtureDocument
    {
        [JsonPropertyName("schema_version")]
        public string? SchemaVersion { get; init; }

        [JsonPropertyName("fixtures")]
        public List<ReferenceStyleGoldenFixtureSpec>? Fixtures { get; init; }

        [JsonPropertyName("leak_sentinels")]
        public List<string>? LeakSentinels { get; init; }
    }

    private sealed class ReferenceStyleGoldenFixtureSpec
    {
        [JsonPropertyName("fixture_id")]
        public string? FixtureId { get; init; }

        [JsonPropertyName("category")]
        public string? Category { get; init; }

        [JsonPropertyName("profile_id")]
        public long ProfileId { get; init; }

        [JsonPropertyName("materials")]
        public List<ReferenceStyleGoldenMaterialSpec>? Materials { get; init; }

        [JsonPropertyName("expected_numeric_ranges")]
        public List<ReferenceStyleGoldenNumericRangeSpec>? ExpectedNumericRanges { get; init; }

        [JsonPropertyName("expected_advanced_labels")]
        public List<ReferenceStyleGoldenAdvancedLabelSpec>? ExpectedAdvancedLabels { get; init; }

        [JsonPropertyName("candidate")]
        public ReferenceStyleGoldenCandidateSpec? Candidate { get; init; }
    }

    private sealed class ReferenceStyleGoldenMaterialSpec
    {
        [JsonPropertyName("material_id")]
        public string? MaterialId { get; init; }

        [JsonPropertyName("anchor_id")]
        public long AnchorId { get; init; }

        [JsonPropertyName("source_segment_id")]
        public string? SourceSegmentId { get; init; }

        [JsonPropertyName("material_type")]
        public string? MaterialType { get; init; }

        [JsonPropertyName("function_tag")]
        public string? FunctionTag { get; init; }

        [JsonPropertyName("emotion_tag")]
        public string? EmotionTag { get; init; }

        [JsonPropertyName("scene_tag")]
        public string? SceneTag { get; init; }

        [JsonPropertyName("pov_tag")]
        public string? PovTag { get; init; }

        [JsonPropertyName("technique_tag")]
        public string? TechniqueTag { get; init; }

        [JsonPropertyName("function_confidence")]
        public double FunctionConfidence { get; init; }

        [JsonPropertyName("emotion_confidence")]
        public double EmotionConfidence { get; init; }

        [JsonPropertyName("pov_confidence")]
        public double PovConfidence { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("source_hash")]
        public string? SourceHash { get; init; }

        [JsonPropertyName("start_offset")]
        public int StartOffset { get; init; }

        [JsonPropertyName("end_offset")]
        public int EndOffset { get; init; }

        [JsonPropertyName("text_hash")]
        public string? TextHash { get; init; }
    }

    private sealed class ReferenceStyleGoldenNumericRangeSpec
    {
        [JsonPropertyName("feature_key")]
        public string? FeatureKey { get; init; }

        [JsonPropertyName("min")]
        public double Min { get; init; }

        [JsonPropertyName("max")]
        public double Max { get; init; }
    }

    private sealed class ReferenceStyleGoldenAdvancedLabelSpec
    {
        [JsonPropertyName("feature_key")]
        public string? FeatureKey { get; init; }

        [JsonPropertyName("label")]
        public string? Label { get; init; }
    }

    private sealed class ReferenceStyleGoldenCandidateSpec
    {
        [JsonPropertyName("candidate_id")]
        public string? CandidateId { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("rubric_scores")]
        public List<ReferenceStyleGoldenRubricScoreSpec>? RubricScores { get; init; }
    }

    private sealed class ReferenceStyleGoldenRubricScoreSpec
    {
        [JsonPropertyName("dimension")]
        public string? Dimension { get; init; }

        [JsonPropertyName("score")]
        public double Score { get; init; }

        [JsonPropertyName("min_score")]
        public double MinScore { get; init; }

        [JsonPropertyName("max_score")]
        public double MaxScore { get; init; }
    }
}

internal sealed record ReferenceStyleGoldenEvaluationReport(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("fixture_schema_version")] string FixtureSchemaVersion,
    [property: JsonPropertyName("taxonomy_version")] string TaxonomyVersion,
    [property: JsonPropertyName("evaluated_at")] DateTimeOffset EvaluatedAt,
    [property: JsonPropertyName("fixture_count")] int FixtureCount,
    [property: JsonPropertyName("passed_count")] int PassedCount,
    [property: JsonPropertyName("failed_count")] int FailedCount,
    [property: JsonPropertyName("results")] IReadOnlyList<ReferenceStyleGoldenFixtureEvaluationResult> Results,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);

internal sealed record ReferenceStyleGoldenFixtureEvaluationResult(
    [property: JsonPropertyName("fixture_id")] string FixtureId,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("aggregate_confidence")] double AggregateConfidence,
    [property: JsonPropertyName("numeric_checks")] IReadOnlyList<ReferenceStyleGoldenNumericCheckResult> NumericChecks,
    [property: JsonPropertyName("advanced_label_checks")] IReadOnlyList<ReferenceStyleGoldenAdvancedLabelCheckResult> AdvancedLabelChecks,
    [property: JsonPropertyName("candidate_evaluation")] ReferenceStyleGoldenCandidateEvaluationResult CandidateEvaluation,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<string> Diagnostics);

internal interface IReferenceStyleGoldenCheckResult
{
    bool Passed { get; }
}

internal sealed record ReferenceStyleGoldenNumericCheckResult(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("observed_value")] double ObservedValue,
    [property: JsonPropertyName("min")] double Min,
    [property: JsonPropertyName("max")] double Max,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("passed")] bool Passed) : IReferenceStyleGoldenCheckResult;

internal sealed record ReferenceStyleGoldenAdvancedLabelCheckResult(
    [property: JsonPropertyName("feature_key")] string FeatureKey,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("passed")] bool Passed) : IReferenceStyleGoldenCheckResult;

internal sealed record ReferenceStyleGoldenCandidateEvaluationResult(
    [property: JsonPropertyName("candidate_id")] string CandidateId,
    [property: JsonPropertyName("score_count")] int ScoreCount,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("average_score")] double AverageScore,
    [property: JsonPropertyName("scores")] IReadOnlyList<ReferenceStyleGoldenRubricScoreResult> Scores);

internal sealed record ReferenceStyleGoldenRubricScoreResult(
    [property: JsonPropertyName("dimension")] string Dimension,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("min_score")] double MinScore,
    [property: JsonPropertyName("max_score")] double MaxScore,
    [property: JsonPropertyName("passed")] bool Passed);
