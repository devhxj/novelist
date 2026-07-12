using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceMaterializationV1BaselineReport
{
    public const string ReportSchemaVersion = "reference-materialization-v1-baseline-report-v1";
    private const string FixtureSchemaVersion = "reference-materialization-quality-fixture-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<ReferenceMaterializationV1BaselineReportResult> EvaluateAsync(
        string calibrationFixturePath,
        string holdoutFixturePath,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationFixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(holdoutFixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var calibration = await ReadFixtureAsync(calibrationFixturePath, "calibration", cancellationToken);
        var holdout = await ReadFixtureAsync(holdoutFixturePath, "holdout", cancellationToken);
        if (calibration.CaseIds.Overlaps(holdout.CaseIds) || calibration.NodeIds.Overlaps(holdout.NodeIds))
        {
            throw new InvalidDataException("Calibration and holdout fixtures must not share case or node identifiers.");
        }

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "novelist-materialization-v1-baseline", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new AppInitializationOptions
            {
                ConfigDirectory = Path.Combine(temporaryRoot, "config"),
                DefaultDataDirectory = Path.Combine(temporaryRoot, "data"),
                EnableLegacyMigration = false
            };
            await new FileSystemAppInitializationService(options).InitializeAsync(options.DefaultDataDirectory, cancellationToken);
            var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
            var novel = await novels.CreateNovelAsync(new CreateNovelPayload("Materialization V1 Baseline", "", ""), cancellationToken);
            var anchors = new SqliteReferenceAnchorService(options, novels);
            var calibrationReport = await EvaluateSplitAsync(options, novel.Id, anchors, calibration, temporaryRoot, cancellationToken);
            var holdoutReport = await EvaluateSplitAsync(options, novel.Id, anchors, holdout, temporaryRoot, cancellationToken);
            var report = new ReferenceMaterializationV1BaselineReportResult(
                ReportSchemaVersion,
                "legacy_deterministic_v1_all_supported_segments",
                calibration.FixtureHash,
                holdout.FixtureHash,
                calibrationReport,
                holdoutReport);
            await WriteAtomicJsonAsync(outputDirectory, "reference-materialization-v1-baseline-report.json", report, cancellationToken);
            return report;
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static async Task<ReferenceMaterializationV1BaselineSplitReport> EvaluateSplitAsync(
        AppInitializationOptions options,
        long novelId,
        SqliteReferenceAnchorService anchors,
        BaselineFixture fixture,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        var sourceDirectory = Path.Combine(temporaryRoot, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, fixture.Split + ".md");
        await File.WriteAllTextAsync(
            sourcePath,
            "# V1 baseline\n\n" + string.Join("\n\n", fixture.Cases.SelectMany(item => item.Nodes).Select(node => node.Text)),
            cancellationToken);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novelId, "V1 baseline " + fixture.Split, null, sourcePath, "markdown", "user_provided"),
            cancellationToken);
        await using var connection = await OpenConnectionAsync(options, cancellationToken);
        var rawNodeCount = await CountAsync(connection, "SELECT COUNT(*) FROM reference_text_nodes WHERE anchor_id = $anchor_id;", anchor.AnchorId, cancellationToken);
        var materialCount = await CountAsync(connection, "SELECT COUNT(*) FROM reference_materials WHERE anchor_id = $anchor_id AND archived_at IS NULL;", anchor.AnchorId, cancellationToken);
        var uniqueSpanCount = await CountAsync(connection, """
            SELECT COUNT(*)
            FROM (
                SELECT DISTINCT segment.start_offset, segment.end_offset
                FROM reference_materials material
                INNER JOIN reference_source_segments segment ON segment.segment_id = material.source_segment_id
                WHERE material.anchor_id = $anchor_id AND material.archived_at IS NULL
            );
            """, anchor.AnchorId, cancellationToken);
        var materialTexts = await ReadMaterialTextsAsync(connection, anchor.AnchorId, cancellationToken);
        var materialSpans = await ReadMaterialSpansAsync(connection, anchor.AnchorId, cancellationToken);
        var predictedAccepted = 0;
        var expectedAcceptedMaterialized = 0;
        var shortNoiseTotal = 0;
        var shortNoiseRejected = 0;
        var shortValuableTotal = 0;
        var shortValuableAccepted = 0;
        foreach (var item in fixture.Cases)
        {
            var materialized = item.Nodes.All(node => materialTexts.Contains(node.Text, StringComparer.Ordinal));
            if (materialized)
            {
                predictedAccepted++;
            }
            if (item.Decision == ReferenceMaterializationCandidateDecisions.Accepted && materialized)
            {
                expectedAcceptedMaterialized++;
            }

            if (item.Category is "short_noise" or "transition_noise")
            {
                shortNoiseTotal++;
                if (!materialized)
                {
                    shortNoiseRejected++;
                }
            }
            if (item.Category == "short_valuable")
            {
                shortValuableTotal++;
                if (materialized)
                {
                    shortValuableAccepted++;
                }
            }
        }

        var expectedAccepted = fixture.Cases.Count(item => item.Decision == ReferenceMaterializationCandidateDecisions.Accepted);
        return new ReferenceMaterializationV1BaselineSplitReport(
            fixture.Split,
            fixture.Cases.Count,
            rawNodeCount,
            materialCount,
            uniqueSpanCount,
            predictedAccepted,
            expectedAccepted,
            Ratio(expectedAccepted, predictedAccepted),
            Ratio(expectedAcceptedMaterialized, expectedAccepted),
            Ratio(shortNoiseRejected, shortNoiseTotal),
            Ratio(shortValuableAccepted, shortValuableTotal),
            OverlapPairRate(materialSpans),
            CandidateSpanIouMedian: null,
            ActiveSearchResultCount: 0,
            ActiveSearchEvaluated: false);
    }

    private static async Task<BaselineFixture> ReadFixtureAsync(string fixturePath, string expectedSplit, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(fixturePath, cancellationToken);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema_version", out var schemaVersion) || schemaVersion.GetString() != FixtureSchemaVersion ||
            !root.TryGetProperty("split", out var split) || split.GetString() != expectedSplit ||
            !root.TryGetProperty("cases", out var casesElement) || casesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Materialization quality fixture is invalid.");
        }

        var cases = new List<BaselineCase>();
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in casesElement.EnumerateArray())
        {
            var caseId = RequiredString(item, "case_id");
            if (!caseIds.Add(caseId))
            {
                throw new InvalidDataException("Materialization quality fixture has duplicate case ids.");
            }

            var category = RequiredString(item, "category");
            var expected = item.GetProperty("expected");
            var decision = RequiredString(expected, "decision");
            var nodes = new List<BaselineNode>();
            foreach (var node in item.GetProperty("source_nodes").EnumerateArray())
            {
                var nodeId = RequiredString(node, "node_id");
                var text = RequiredString(node, "text");
                var textHash = RequiredString(node, "text_hash");
                if (!nodeIds.Add(nodeId) || !string.Equals(Hash(text), textHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Materialization quality fixture node is invalid.");
                }

                nodes.Add(new BaselineNode(nodeId, text));
            }

            if (nodes.Count == 0 || decision == ReferenceMaterializationCandidateDecisions.Pending)
            {
                throw new InvalidDataException("Materialization quality fixture case is invalid.");
            }

            cases.Add(new BaselineCase(caseId, category, decision, nodes));
        }

        return new BaselineFixture(expectedSplit, Hash(content), cases, caseIds, nodeIds);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(AppInitializationOptions options, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql, long anchorId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<string>> ReadMaterialTextsAsync(SqliteConnection connection, long anchorId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM reference_materials WHERE anchor_id = $anchor_id AND archived_at IS NULL ORDER BY material_id;";
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var texts = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            texts.Add(reader.GetString(0));
        }

        return texts;
    }

    private static double? Ratio(int numerator, int denominator) => denominator == 0 ? null : Math.Round(numerator / (double)denominator, 6);

    private static async Task<IReadOnlyList<ReferenceMaterialSourceSpan>> ReadMaterialSpansAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment.start_offset, segment.end_offset
            FROM reference_materials material
            INNER JOIN reference_source_segments segment ON segment.segment_id = material.source_segment_id
            WHERE material.anchor_id = $anchor_id AND material.archived_at IS NULL
            ORDER BY material.material_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var spans = new List<ReferenceMaterialSourceSpan>();
        while (await reader.ReadAsync(cancellationToken))
        {
            spans.Add(new ReferenceMaterialSourceSpan(reader.GetInt32(0), reader.GetInt32(1)));
        }

        return spans;
    }

    private static double? OverlapPairRate(IReadOnlyList<ReferenceMaterialSourceSpan> spans)
    {
        var pairs = 0;
        var overlaps = 0;
        for (var left = 0; left < spans.Count; left++)
        {
            for (var right = left + 1; right < spans.Count; right++)
            {
                pairs++;
                if (spans[left].StartOffset < spans[right].EndOffset &&
                    spans[right].StartOffset < spans[left].EndOffset)
                {
                    overlaps++;
                }
            }
        }

        return Ratio(overlaps, pairs);
    }

    private static async Task WriteAtomicJsonAsync(string outputDirectory, string fileName, object report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var target = Path.Combine(outputDirectory, fileName);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine, cancellationToken);
        File.Move(temporary, target, overwrite: true);
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException("Materialization quality fixture string is invalid.");
        }

        return value.GetString()!;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record BaselineFixture(string Split, string FixtureHash, IReadOnlyList<BaselineCase> Cases, IReadOnlySet<string> CaseIds, IReadOnlySet<string> NodeIds);
    private sealed record BaselineCase(string CaseId, string Category, string Decision, IReadOnlyList<BaselineNode> Nodes);
    private sealed record BaselineNode(string NodeId, string Text);
    private sealed record ReferenceMaterialSourceSpan(int StartOffset, int EndOffset);
}

internal sealed record ReferenceMaterializationV1BaselineReportResult(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("baseline_kind")] string BaselineKind,
    [property: JsonPropertyName("calibration_fixture_hash")] string CalibrationFixtureHash,
    [property: JsonPropertyName("holdout_fixture_hash")] string HoldoutFixtureHash,
    [property: JsonPropertyName("calibration")] ReferenceMaterializationV1BaselineSplitReport Calibration,
    [property: JsonPropertyName("holdout")] ReferenceMaterializationV1BaselineSplitReport Holdout);

internal sealed record ReferenceMaterializationV1BaselineSplitReport(
    [property: JsonPropertyName("split")] string Split,
    [property: JsonPropertyName("case_count")] int CaseCount,
    [property: JsonPropertyName("raw_node_count")] int RawNodeCount,
    [property: JsonPropertyName("material_count")] int MaterialCount,
    [property: JsonPropertyName("unique_source_span_count")] int UniqueSourceSpanCount,
    [property: JsonPropertyName("predicted_accepted_case_count")] int PredictedAcceptedCaseCount,
    [property: JsonPropertyName("expected_accepted_case_count")] int ExpectedAcceptedCaseCount,
    [property: JsonPropertyName("accepted_material_precision")] double? AcceptedMaterialPrecision,
    [property: JsonPropertyName("valuable_unit_recall")] double? ValuableUnitRecall,
    [property: JsonPropertyName("short_noise_rejection_recall")] double? ShortNoiseRejectionRecall,
    [property: JsonPropertyName("short_valuable_recall")] double? ShortValuableRecall,
    [property: JsonPropertyName("material_overlap_pair_rate")] double? MaterialOverlapPairRate,
    [property: JsonPropertyName("candidate_span_iou_median")] double? CandidateSpanIouMedian,
    [property: JsonPropertyName("active_search_result_count")] int ActiveSearchResultCount,
    [property: JsonPropertyName("active_search_evaluated")] bool ActiveSearchEvaluated);
