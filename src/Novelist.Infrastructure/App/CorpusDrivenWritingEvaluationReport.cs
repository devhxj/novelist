using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Novelist.Infrastructure.App;

internal static class CorpusDrivenWritingEvaluationReport
{
    public const string FixtureSchemaVersion = "corpus-writing-evaluation-fixtures-v1";
    public const string ReportSchemaVersion = "corpus-writing-evaluation-report-v1";
    public const long MaximumFixtureBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false
    };

    private static readonly Regex SafeIdentifierPattern = new(
        "^[a-z0-9][a-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Sha256Pattern = new(
        "^sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly ISet<string> ReasonCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "goal_match",
        "context_match",
        "technique_match",
        "structured_observation_match",
        "licensed",
        "deduplicated",
        "source_diversity",
        "other_observed"
    };

    private static readonly ISet<string> RootProperties = NewPropertySet(
        "schema_version",
        "dataset_id",
        "dataset_revision",
        "dataset_kind",
        "query_cases",
        "blueprint_cases",
        "insertion_cases");

    private static readonly ISet<string> QueryCaseProperties = NewPropertySet(
        "case_id",
        "query_hash",
        "top_k",
        "relevant_node_ids",
        "ranked_node_ids",
        "expected_reason_codes",
        "returned_reason_codes",
        "latency_ms");

    private static readonly ISet<string> BlueprintCaseProperties = NewPropertySet(
        "case_id",
        "goal_hash",
        "selected_candidate_id",
        "feedback_improved",
        "candidates");

    private static readonly ISet<string> BlueprintCandidateProperties = NewPropertySet(
        "candidate_id",
        "source_node_ids");

    private static readonly ISet<string> InsertionCaseProperties = NewPropertySet(
        "case_id",
        "chapter_hash",
        "candidate_character_count",
        "user_edited_character_count",
        "iteration_count",
        "accepted",
        "source_piece_count",
        "preserved_piece_count",
        "human_scores");

    private static readonly ISet<string> HumanScoreProperties = NewPropertySet(
        "reviewer_id_hash",
        "fidelity",
        "plot_fit",
        "naturalness");

    public static async Task<CorpusDrivenWritingEvaluationReportResult> EvaluateFileAsync(
        string fixturePath,
        string outputDirectory,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var fixtureInfo = new FileInfo(fixturePath);
        if (!fixtureInfo.Exists)
        {
            throw new FileNotFoundException("Evaluation fixture does not exist.");
        }

        if (fixtureInfo.Length > MaximumFixtureBytes)
        {
            throw new InvalidDataException($"Evaluation fixture exceeds the {MaximumFixtureBytes} byte maximum.");
        }

        var fixtureJson = await File.ReadAllTextAsync(fixturePath, cancellationToken);
        CorpusWritingEvaluationFixture fixture;
        try
        {
            using var document = JsonDocument.Parse(fixtureJson);
            ValidateJsonShape(document.RootElement);
            fixture = JsonSerializer.Deserialize<CorpusWritingEvaluationFixture>(
                document.RootElement.GetRawText(),
                JsonOptions)
                ?? throw new InvalidDataException("Evaluation fixture is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Evaluation fixture is not valid JSON.", exception);
        }

        ValidateFixture(fixture);
        var report = BuildReport(fixture, generatedAt.ToUniversalTime());

        Directory.CreateDirectory(outputDirectory);
        await WriteAtomicallyAsync(
            Path.Combine(outputDirectory, "corpus-writing-evaluation-report.json"),
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken);
        await WriteAtomicallyAsync(
            Path.Combine(outputDirectory, "corpus-writing-evaluation-report.md"),
            BuildMarkdown(report),
            cancellationToken);

        return report;
    }

    private static CorpusDrivenWritingEvaluationReportResult BuildReport(
        CorpusWritingEvaluationFixture fixture,
        DateTimeOffset generatedAt)
    {
        var queryCases = fixture.QueryCases!;
        var blueprintCases = fixture.BlueprintCases!;
        var insertionCases = fixture.InsertionCases!;

        var retrieval = new CorpusWritingRetrievalMetrics(
            Round(queryCases.Average(CalculateRecallAtK)),
            Round(queryCases.Average(CalculateNdcgAtK)),
            Round(queryCases.Average(CalculateReasonAccuracy)),
            Round(Percentile(queryCases.Select(item => item.LatencyMilliseconds).ToArray(), 0.50)),
            Round(Percentile(queryCases.Select(item => item.LatencyMilliseconds).ToArray(), 0.95)));

        var sourceSetPairs = blueprintCases
            .SelectMany(BuildSourceSetPairs)
            .ToArray();
        var blueprints = new CorpusWritingBlueprintMetrics(
            blueprintCases.Sum(item => item.Candidates!.Count),
            Round(sourceSetPairs.Length == 0 ? 0 : sourceSetPairs.Average(item => 1d - JaccardSimilarity(item.Left, item.Right))),
            Round(sourceSetPairs.Length == 0 ? 0 : sourceSetPairs.Count(item => item.Left.SetEquals(item.Right)) / (double)sourceSetPairs.Length),
            Round(blueprintCases.Count(item => item.FeedbackImproved) / (double)blueprintCases.Count));

        var scores = insertionCases.SelectMany(item => item.HumanScores!).ToArray();
        var sourcePieces = insertionCases.Sum(item => item.SourcePieceCount);
        var candidateCharacters = insertionCases.Sum(item => item.CandidateCharacterCount);
        var prose = new CorpusWritingProseMetrics(
            Round(insertionCases.Sum(item => item.PreservedPieceCount) / (double)sourcePieces),
            Round(scores.Average(item => item.Fidelity)),
            Round(scores.Average(item => item.PlotFit)),
            Round(scores.Average(item => item.Naturalness)),
            Round(insertionCases.Count(item => item.Accepted) / (double)insertionCases.Count),
            Round(insertionCases.Sum(item => item.UserEditedCharacterCount) / (double)candidateCharacters),
            Round(insertionCases.Average(item => item.IterationCount)),
            scores.Length);

        return new CorpusDrivenWritingEvaluationReportResult(
            ReportSchemaVersion,
            fixture.DatasetId!,
            fixture.DatasetRevision!,
            fixture.DatasetKind!,
            generatedAt,
            queryCases.Count,
            blueprintCases.Count,
            insertionCases.Count,
            retrieval,
            blueprints,
            prose);
    }

    private static IEnumerable<SourceSetPair> BuildSourceSetPairs(CorpusWritingBlueprintCase fixture)
    {
        var candidates = fixture.Candidates!;
        for (var index = 0; index < candidates.Count; index++)
        {
            for (var otherIndex = index + 1; otherIndex < candidates.Count; otherIndex++)
            {
                yield return new SourceSetPair(
                    candidates[index].SourceNodeIds!.ToHashSet(StringComparer.Ordinal),
                    candidates[otherIndex].SourceNodeIds!.ToHashSet(StringComparer.Ordinal));
            }
        }
    }

    private static double CalculateRecallAtK(CorpusWritingQueryCase fixture)
    {
        var relevant = fixture.RelevantNodeIds!.ToHashSet(StringComparer.Ordinal);
        var hits = fixture.RankedNodeIds!
            .Take(fixture.TopK)
            .Count(relevant.Contains);
        return hits / (double)relevant.Count;
    }

    private static double CalculateNdcgAtK(CorpusWritingQueryCase fixture)
    {
        var relevant = fixture.RelevantNodeIds!.ToHashSet(StringComparer.Ordinal);
        var dcg = fixture.RankedNodeIds!
            .Take(fixture.TopK)
            .Select((nodeId, index) => relevant.Contains(nodeId) ? 1d / Math.Log2(index + 2) : 0d)
            .Sum();
        var ideal = Enumerable.Range(0, Math.Min(fixture.TopK, relevant.Count))
            .Sum(index => 1d / Math.Log2(index + 2));
        return ideal == 0 ? 0 : dcg / ideal;
    }

    private static double CalculateReasonAccuracy(CorpusWritingQueryCase fixture)
    {
        var expected = fixture.ExpectedReasonCodes!.ToHashSet(StringComparer.Ordinal);
        var returned = fixture.ReturnedReasonCodes!.ToHashSet(StringComparer.Ordinal);
        return expected.Count(item => returned.Contains(item)) / (double)expected.Count;
    }

    private static double JaccardSimilarity(ISet<string> left, ISet<string> right)
    {
        var intersection = left.Count(right.Contains);
        var union = left.Count + right.Count - intersection;
        return union == 0 ? 1d : intersection / (double)union;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string BuildMarkdown(CorpusDrivenWritingEvaluationReportResult report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Corpus Writing Evaluation Report");
        builder.AppendLine();
        builder.AppendLine($"- Dataset: `{report.DatasetId}`");
        builder.AppendLine($"- Revision: `{report.DatasetRevision}`");
        builder.AppendLine($"- Dataset kind: `{report.DatasetKind}`");
        builder.AppendLine($"- Generated at: `{report.GeneratedAt:O}`");
        builder.AppendLine();
        builder.AppendLine("## Coverage");
        builder.AppendLine();
        builder.AppendLine($"- Query cases: `{report.QueryCaseCount}`");
        builder.AppendLine($"- Blueprint cases: `{report.BlueprintCaseCount}`");
        builder.AppendLine($"- Insertion cases: `{report.InsertionCaseCount}`");
        builder.AppendLine();
        builder.AppendLine("## Metrics");
        builder.AppendLine();
        builder.AppendLine("| Area | Metric | Value |");
        builder.AppendLine("|---|---|---:|");
        AppendMetric(builder, "Retrieval", "Recall@K", report.Retrieval.RecallAtK);
        AppendMetric(builder, "Retrieval", "nDCG@K", report.Retrieval.NdcgAtK);
        AppendMetric(builder, "Retrieval", "Reason accuracy", report.Retrieval.ReasonAccuracy);
        AppendMetric(builder, "Retrieval", "Latency P50 ms", report.Retrieval.LatencyP50Milliseconds);
        AppendMetric(builder, "Retrieval", "Latency P95 ms", report.Retrieval.LatencyP95Milliseconds);
        AppendMetric(builder, "Blueprint", "Source-set distinctness", report.Blueprints.SourceSetDistinctness);
        AppendMetric(builder, "Blueprint", "Source-set repeat rate", report.Blueprints.SourceSetRepeatRate);
        AppendMetric(builder, "Blueprint", "Feedback improvement rate", report.Blueprints.FeedbackImprovementRate);
        AppendMetric(builder, "Prose", "Source fidelity rate", report.Prose.SourceFidelityRate);
        AppendMetric(builder, "Prose", "Plot-fit average", report.Prose.PlotFitAverage);
        AppendMetric(builder, "Prose", "Naturalness average", report.Prose.NaturalnessAverage);
        AppendMetric(builder, "Prose", "Accepted rate", report.Prose.AcceptedRate);
        AppendMetric(builder, "Prose", "User edit ratio", report.Prose.UserEditRatio);
        AppendMetric(builder, "Prose", "Average iterations", report.Prose.AverageIterations);
        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string area, string metric, double value)
    {
        builder.AppendLine($"| {area} | {metric} | {value.ToString("0.######", CultureInfo.InvariantCulture)} |");
    }

    private static void ValidateJsonShape(JsonElement root)
    {
        ValidateObject(root, RootProperties, "$", "schema_version", "dataset_id", "dataset_revision", "dataset_kind", "query_cases", "blueprint_cases", "insertion_cases");

        foreach (var queryCase in ValidateObjectArray(root.GetProperty("query_cases"), "$.query_cases"))
        {
            ValidateObject(queryCase, QueryCaseProperties, "$.query_cases[]", QueryCaseProperties.ToArray());
        }

        foreach (var blueprintCase in ValidateObjectArray(root.GetProperty("blueprint_cases"), "$.blueprint_cases"))
        {
            ValidateObject(blueprintCase, BlueprintCaseProperties, "$.blueprint_cases[]", BlueprintCaseProperties.ToArray());
            foreach (var candidate in ValidateObjectArray(blueprintCase.GetProperty("candidates"), "$.blueprint_cases[].candidates"))
            {
                ValidateObject(candidate, BlueprintCandidateProperties, "$.blueprint_cases[].candidates[]", BlueprintCandidateProperties.ToArray());
            }
        }

        foreach (var insertionCase in ValidateObjectArray(root.GetProperty("insertion_cases"), "$.insertion_cases"))
        {
            ValidateObject(insertionCase, InsertionCaseProperties, "$.insertion_cases[]", InsertionCaseProperties.ToArray());
            foreach (var score in ValidateObjectArray(insertionCase.GetProperty("human_scores"), "$.insertion_cases[].human_scores"))
            {
                ValidateObject(score, HumanScoreProperties, "$.insertion_cases[].human_scores[]", HumanScoreProperties.ToArray());
            }
        }
    }

    private static IEnumerable<JsonElement> ValidateObjectArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path} must be an array.");
        }

        return element.EnumerateArray().ToArray();
    }

    private static void ValidateObject(JsonElement element, ISet<string> allowed, string path, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path} must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"Unexpected field '{property.Name}' at {path}; evaluation fixtures only permit redacted fields.");
            }

            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"Duplicate field '{property.Name}' at {path}.");
            }
        }

        foreach (var property in required)
        {
            if (!seen.Contains(property))
            {
                throw new InvalidDataException($"Missing required field '{property}' at {path}.");
            }
        }
    }

    private static void ValidateFixture(CorpusWritingEvaluationFixture fixture)
    {
        if (!string.Equals(fixture.SchemaVersion, FixtureSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported evaluation fixture schema '{fixture.SchemaVersion}'.");
        }

        ValidateSafeIdentifier(fixture.DatasetId, "dataset_id");
        ValidateSafeIdentifier(fixture.DatasetRevision, "dataset_revision");
        if (fixture.DatasetKind is not ("contract" or "human"))
        {
            throw new InvalidDataException("dataset_kind must be 'contract' or 'human'.");
        }

        RequireNonEmpty(fixture.QueryCases, "query_cases");
        RequireNonEmpty(fixture.BlueprintCases, "blueprint_cases");
        RequireNonEmpty(fixture.InsertionCases, "insertion_cases");

        if (fixture.DatasetKind == "human")
        {
            RequireCount(fixture.QueryCases!, 50, 100, "human query_cases");
            RequireCount(fixture.BlueprintCases!, 20, 30, "human blueprint_cases");
            RequireCount(fixture.InsertionCases!, 20, 30, "human insertion_cases");
        }

        RequireDistinct(fixture.QueryCases!, item => item.CaseId, "query_cases");
        RequireDistinct(fixture.BlueprintCases!, item => item.CaseId, "blueprint_cases");
        RequireDistinct(fixture.InsertionCases!, item => item.CaseId, "insertion_cases");

        foreach (var queryCase in fixture.QueryCases!)
        {
            ValidateQueryCase(queryCase);
        }

        foreach (var blueprintCase in fixture.BlueprintCases!)
        {
            ValidateBlueprintCase(blueprintCase);
        }

        foreach (var insertionCase in fixture.InsertionCases!)
        {
            ValidateInsertionCase(insertionCase);
        }
    }

    private static void ValidateQueryCase(CorpusWritingQueryCase fixture)
    {
        ValidateSafeIdentifier(fixture.CaseId, "query_cases.case_id");
        ValidateSha256(fixture.QueryHash, "query_cases.query_hash");
        if (fixture.TopK <= 0)
        {
            throw new InvalidDataException("query_cases.top_k must be positive.");
        }

        RequireNonEmpty(fixture.RelevantNodeIds, "query_cases.relevant_node_ids");
        RequireNonEmpty(fixture.RankedNodeIds, "query_cases.ranked_node_ids");
        RequireNonEmpty(fixture.ExpectedReasonCodes, "query_cases.expected_reason_codes");
        if (fixture.TopK > fixture.RankedNodeIds!.Count)
        {
            throw new InvalidDataException("query_cases.top_k cannot exceed ranked_node_ids count.");
        }

        ValidateDistinctIdentifiers(fixture.RelevantNodeIds!, "query_cases.relevant_node_ids");
        ValidateDistinctIdentifiers(fixture.RankedNodeIds!, "query_cases.ranked_node_ids");
        ValidateDistinctReasonCodes(fixture.ExpectedReasonCodes!, "query_cases.expected_reason_codes");
        ValidateDistinctReasonCodes(fixture.ReturnedReasonCodes ?? [], "query_cases.returned_reason_codes");
        if (fixture.LatencyMilliseconds < 0)
        {
            throw new InvalidDataException("query_cases.latency_ms cannot be negative.");
        }
    }

    private static void ValidateBlueprintCase(CorpusWritingBlueprintCase fixture)
    {
        ValidateSafeIdentifier(fixture.CaseId, "blueprint_cases.case_id");
        ValidateSha256(fixture.GoalHash, "blueprint_cases.goal_hash");
        ValidateSafeIdentifier(fixture.SelectedCandidateId, "blueprint_cases.selected_candidate_id");
        RequireCount(fixture.Candidates, 2, int.MaxValue, "blueprint_cases.candidates");
        RequireDistinct(fixture.Candidates!, item => item.CandidateId, "blueprint_cases.candidates");

        foreach (var candidate in fixture.Candidates!)
        {
            ValidateSafeIdentifier(candidate.CandidateId, "blueprint_cases.candidates.candidate_id");
            RequireNonEmpty(candidate.SourceNodeIds, "blueprint_cases.candidates.source_node_ids");
            ValidateDistinctIdentifiers(candidate.SourceNodeIds!, "blueprint_cases.candidates.source_node_ids");
        }

        if (!fixture.Candidates.Any(item => string.Equals(item.CandidateId, fixture.SelectedCandidateId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("blueprint_cases.selected_candidate_id must identify a candidate in the same case.");
        }
    }

    private static void ValidateInsertionCase(CorpusWritingInsertionCase fixture)
    {
        ValidateSafeIdentifier(fixture.CaseId, "insertion_cases.case_id");
        ValidateSha256(fixture.ChapterHash, "insertion_cases.chapter_hash");
        if (fixture.CandidateCharacterCount <= 0 || fixture.UserEditedCharacterCount < 0 || fixture.IterationCount <= 0)
        {
            throw new InvalidDataException("insertion character counts and iteration_count must be valid positive values.");
        }

        if (fixture.SourcePieceCount <= 0 || fixture.PreservedPieceCount < 0 || fixture.PreservedPieceCount > fixture.SourcePieceCount)
        {
            throw new InvalidDataException("insertion source and preserved piece counts are invalid.");
        }

        RequireNonEmpty(fixture.HumanScores, "insertion_cases.human_scores");
        RequireDistinct(fixture.HumanScores!, item => item.ReviewerIdHash, "insertion_cases.human_scores");
        foreach (var score in fixture.HumanScores!)
        {
            ValidateSha256(score.ReviewerIdHash, "insertion_cases.human_scores.reviewer_id_hash");
            ValidateScore(score.Fidelity, "fidelity");
            ValidateScore(score.PlotFit, "plot_fit");
            ValidateScore(score.Naturalness, "naturalness");
        }
    }

    private static void ValidateScore(int value, string name)
    {
        if (value is < 1 or > 5)
        {
            throw new InvalidDataException($"human score '{name}' must be between 1 and 5.");
        }
    }

    private static void ValidateDistinctReasonCodes(IReadOnlyList<string> codes, string name)
    {
        ValidateDistinctIdentifiers(codes, name);
        if (codes.Any(code => !ReasonCodes.Contains(code)))
        {
            throw new InvalidDataException($"{name} must use a code from the fixed evaluation codebook.");
        }
    }

    private static void RequireDistinct<T>(IReadOnlyList<T> values, Func<T, string?> keySelector, string name)
    {
        var keys = values.Select(keySelector).ToArray();
        if (keys.Any(string.IsNullOrWhiteSpace) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            throw new InvalidDataException($"{name} contains duplicate or empty identifiers.");
        }
    }

    private static void ValidateDistinctIdentifiers(IReadOnlyList<string> values, string name)
    {
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
        {
            throw new InvalidDataException($"{name} contains duplicate identifiers.");
        }

        foreach (var value in values)
        {
            ValidateSafeIdentifier(value, name);
        }
    }

    private static void RequireNonEmpty<T>(IReadOnlyList<T>? values, string name)
    {
        if (values is null || values.Count == 0)
        {
            throw new InvalidDataException($"{name} must not be empty.");
        }
    }

    private static void RequireCount<T>(IReadOnlyList<T>? values, int minimum, int maximum, string name)
    {
        RequireNonEmpty(values, name);
        if (values!.Count < minimum || values.Count > maximum)
        {
            throw new InvalidDataException($"{name} must contain between {minimum} and {maximum} items.");
        }
    }

    private static void ValidateSafeIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifierPattern.IsMatch(value))
        {
            throw new InvalidDataException($"{name} must be a redacted lowercase identifier.");
        }
    }

    private static void ValidateSha256(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Sha256Pattern.IsMatch(value))
        {
            throw new InvalidDataException($"{name} must be a sha256 hash.");
        }
    }

    private static ISet<string> NewPropertySet(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record SourceSetPair(ISet<string> Left, ISet<string> Right);
}

internal sealed record CorpusWritingEvaluationFixture(
    [property: JsonPropertyName("schema_version")] string? SchemaVersion,
    [property: JsonPropertyName("dataset_id")] string? DatasetId,
    [property: JsonPropertyName("dataset_revision")] string? DatasetRevision,
    [property: JsonPropertyName("dataset_kind")] string? DatasetKind,
    [property: JsonPropertyName("query_cases")] IReadOnlyList<CorpusWritingQueryCase>? QueryCases,
    [property: JsonPropertyName("blueprint_cases")] IReadOnlyList<CorpusWritingBlueprintCase>? BlueprintCases,
    [property: JsonPropertyName("insertion_cases")] IReadOnlyList<CorpusWritingInsertionCase>? InsertionCases);

internal sealed record CorpusWritingQueryCase(
    [property: JsonPropertyName("case_id")] string? CaseId,
    [property: JsonPropertyName("query_hash")] string? QueryHash,
    [property: JsonPropertyName("top_k")] int TopK,
    [property: JsonPropertyName("relevant_node_ids")] IReadOnlyList<string>? RelevantNodeIds,
    [property: JsonPropertyName("ranked_node_ids")] IReadOnlyList<string>? RankedNodeIds,
    [property: JsonPropertyName("expected_reason_codes")] IReadOnlyList<string>? ExpectedReasonCodes,
    [property: JsonPropertyName("returned_reason_codes")] IReadOnlyList<string>? ReturnedReasonCodes,
    [property: JsonPropertyName("latency_ms")] double LatencyMilliseconds);

internal sealed record CorpusWritingBlueprintCase(
    [property: JsonPropertyName("case_id")] string? CaseId,
    [property: JsonPropertyName("goal_hash")] string? GoalHash,
    [property: JsonPropertyName("selected_candidate_id")] string? SelectedCandidateId,
    [property: JsonPropertyName("feedback_improved")] bool FeedbackImproved,
    [property: JsonPropertyName("candidates")] IReadOnlyList<CorpusWritingBlueprintCandidate>? Candidates);

internal sealed record CorpusWritingBlueprintCandidate(
    [property: JsonPropertyName("candidate_id")] string? CandidateId,
    [property: JsonPropertyName("source_node_ids")] IReadOnlyList<string>? SourceNodeIds);

internal sealed record CorpusWritingInsertionCase(
    [property: JsonPropertyName("case_id")] string? CaseId,
    [property: JsonPropertyName("chapter_hash")] string? ChapterHash,
    [property: JsonPropertyName("candidate_character_count")] int CandidateCharacterCount,
    [property: JsonPropertyName("user_edited_character_count")] int UserEditedCharacterCount,
    [property: JsonPropertyName("iteration_count")] int IterationCount,
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("source_piece_count")] int SourcePieceCount,
    [property: JsonPropertyName("preserved_piece_count")] int PreservedPieceCount,
    [property: JsonPropertyName("human_scores")] IReadOnlyList<CorpusWritingHumanScore>? HumanScores);

internal sealed record CorpusWritingHumanScore(
    [property: JsonPropertyName("reviewer_id_hash")] string? ReviewerIdHash,
    [property: JsonPropertyName("fidelity")] int Fidelity,
    [property: JsonPropertyName("plot_fit")] int PlotFit,
    [property: JsonPropertyName("naturalness")] int Naturalness);

internal sealed record CorpusDrivenWritingEvaluationReportResult(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("dataset_id")] string DatasetId,
    [property: JsonPropertyName("dataset_revision")] string DatasetRevision,
    [property: JsonPropertyName("dataset_kind")] string DatasetKind,
    [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("query_case_count")] int QueryCaseCount,
    [property: JsonPropertyName("blueprint_case_count")] int BlueprintCaseCount,
    [property: JsonPropertyName("insertion_case_count")] int InsertionCaseCount,
    [property: JsonPropertyName("retrieval")] CorpusWritingRetrievalMetrics Retrieval,
    [property: JsonPropertyName("blueprints")] CorpusWritingBlueprintMetrics Blueprints,
    [property: JsonPropertyName("prose")] CorpusWritingProseMetrics Prose);

internal sealed record CorpusWritingRetrievalMetrics(
    [property: JsonPropertyName("recall_at_k")] double RecallAtK,
    [property: JsonPropertyName("ndcg_at_k")] double NdcgAtK,
    [property: JsonPropertyName("reason_accuracy")] double ReasonAccuracy,
    [property: JsonPropertyName("latency_p50_ms")] double LatencyP50Milliseconds,
    [property: JsonPropertyName("latency_p95_ms")] double LatencyP95Milliseconds);

internal sealed record CorpusWritingBlueprintMetrics(
    [property: JsonPropertyName("candidate_count")] int CandidateCount,
    [property: JsonPropertyName("source_set_distinctness")] double SourceSetDistinctness,
    [property: JsonPropertyName("source_set_repeat_rate")] double SourceSetRepeatRate,
    [property: JsonPropertyName("feedback_improvement_rate")] double FeedbackImprovementRate);

internal sealed record CorpusWritingProseMetrics(
    [property: JsonPropertyName("source_fidelity_rate")] double SourceFidelityRate,
    [property: JsonPropertyName("fidelity_average")] double FidelityAverage,
    [property: JsonPropertyName("plot_fit_average")] double PlotFitAverage,
    [property: JsonPropertyName("naturalness_average")] double NaturalnessAverage,
    [property: JsonPropertyName("accepted_rate")] double AcceptedRate,
    [property: JsonPropertyName("user_edit_ratio")] double UserEditRatio,
    [property: JsonPropertyName("average_iterations")] double AverageIterations,
    [property: JsonPropertyName("human_score_count")] int HumanScoreCount);
