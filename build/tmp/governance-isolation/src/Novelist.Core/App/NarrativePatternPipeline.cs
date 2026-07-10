using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class NarrativePatternPipeline
{
    public const string SchemaVersion = "narrative-pattern-v1";

    public const int MinimumChapterCount = 3;

    public const int MinimumTotalContentChars = 1_200;

    public const int DefaultContextWindowTokens = 32_000;

    public const int ReservedOutputTokens = 4_000;

    public const int MaxCompressionRounds = 4;

    public const int MaxEmptyPhaseRetries = 2;

    private const int MaxBoundaryCount = 128;
    private const int MaxSummaryCount = 2_000;
    private const int MaxPhaseCount = 256;
    private const int MaxBatchCount = 512;
    private const int MaxTitleLength = 200;
    private const int MaxTextLength = 8_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static NarrativePatternChapterSelection ResolveChapterSelection(
        IReadOnlyList<ChapterPayload> chapters,
        IReadOnlyList<ChapterRangePayload>? ranges,
        IReadOnlyList<long>? selectedChapterIds = null)
    {
        ArgumentNullException.ThrowIfNull(chapters);

        var ordered = chapters
            .OrderBy(chapter => chapter.ChapterNumber)
            .ToArray();
        if (ordered.Length == 0)
        {
            throw new NarrativePatternValidationException(
                "pattern.no_chapters",
                "作品暂无可分析章节。");
        }

        var byNumber = ordered.ToDictionary(chapter => chapter.ChapterNumber);
        var byId = ordered.ToDictionary(chapter => chapter.Id);
        var selectedNumbers = new SortedSet<int>();

        if (selectedChapterIds is { Count: > 0 })
        {
            foreach (var id in selectedChapterIds)
            {
                if (id <= 0 || !byId.TryGetValue(id, out var chapter))
                {
                    throw new NarrativePatternValidationException(
                        "pattern.invalid_chapter_id",
                        $"Selected chapter id '{id.ToString(CultureInfo.InvariantCulture)}' does not exist.");
                }

                selectedNumbers.Add(chapter.ChapterNumber);
            }
        }

        if (ranges is { Count: > 0 })
        {
            foreach (var range in NormalizeRanges(ranges, byNumber))
            {
                for (var number = range.StartChapter; number <= range.EndChapter; number++)
                {
                    if (byNumber.ContainsKey(number))
                    {
                        selectedNumbers.Add(number);
                    }
                }
            }
        }

        var selectionMode = selectedNumbers.Count == 0 ? "all" : "custom";
        var selected = selectionMode == "all"
            ? ordered
            : selectedNumbers.Select(number => byNumber[number]).ToArray();
        var normalizedRanges = NumbersToRanges(selected.Select(chapter => chapter.ChapterNumber)).ToArray();
        var selectedIds = selected.Select(chapter => chapter.Id).ToArray();

        return new NarrativePatternChapterSelection(
            selectionMode,
            selected,
            normalizedRanges,
            selectedIds);
    }

    public static IReadOnlyList<NarrativePatternChapterDocument> BuildChapterDocuments(
        NarrativePatternChapterSelection selection,
        IReadOnlyDictionary<string, string> contentByPath)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(contentByPath);

        var documents = new List<NarrativePatternChapterDocument>(selection.Chapters.Count);
        foreach (var chapter in selection.Chapters)
        {
            if (!contentByPath.TryGetValue(chapter.FilePath, out var content))
            {
                throw new NarrativePatternValidationException(
                    "pattern.missing_chapter_content",
                    $"Chapter '{chapter.ChapterNumber.ToString(CultureInfo.InvariantCulture)}' content is missing.");
            }

            var normalized = NormalizeMultiline(content);
            documents.Add(new NarrativePatternChapterDocument(
                chapter.Id,
                chapter.ChapterNumber,
                NormalizeText(chapter.Title, nameof(chapter.Title), MaxTitleLength),
                chapter.FilePath,
                normalized,
                EstimateTokens(normalized),
                Sha256Hex(normalized)));
        }

        ValidateMinimumViableInput(documents);
        return documents;
    }

    public static void ValidateMinimumViableInput(IReadOnlyList<NarrativePatternChapterDocument> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);

        if (chapters.Count < MinimumChapterCount)
        {
            throw new NarrativePatternValidationException(
                "pattern.insufficient_chapters",
                $"Narrative pattern extraction requires at least {MinimumChapterCount.ToString(CultureInfo.InvariantCulture)} selected chapters.");
        }

        var totalChars = chapters.Sum(chapter => chapter.Content.Trim().Length);
        if (totalChars < MinimumTotalContentChars)
        {
            throw new NarrativePatternValidationException(
                "pattern.insufficient_content",
                $"Narrative pattern extraction requires at least {MinimumTotalContentChars.ToString(CultureInfo.InvariantCulture)} selected content characters.");
        }

        var previous = 0;
        foreach (var chapter in chapters)
        {
            if (chapter.ChapterNumber <= previous)
            {
                throw new NarrativePatternValidationException(
                    "pattern.chapter_order_invalid",
                    "Selected chapters must be ordered by ascending chapter number.");
            }

            if (string.IsNullOrWhiteSpace(chapter.Content))
            {
                throw new NarrativePatternValidationException(
                    "pattern.empty_chapter",
                    $"Chapter '{chapter.ChapterNumber.ToString(CultureInfo.InvariantCulture)}' has no readable content.");
            }

            previous = chapter.ChapterNumber;
        }
    }

    public static IReadOnlyList<NarrativePatternBoundary> ParseBoundaries(
        string rawJson,
        IReadOnlyList<NarrativePatternChapterDocument> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        using var document = ParseObject(rawJson, "pattern.invalid_boundary_json");
        var root = document.RootElement;
        RequireSchema(root, "pattern.invalid_boundary_schema");

        if (!TryReadArray(root, "boundaries", out var boundariesElement))
        {
            throw new NarrativePatternValidationException(
                "pattern.invalid_boundary_schema",
                "Boundary JSON requires a boundaries array.");
        }

        var chapterNumbers = chapters.Select(chapter => chapter.ChapterNumber).ToHashSet();
        var result = new List<NarrativePatternBoundary>();
        var previousEnd = 0;
        foreach (var item in boundariesElement.EnumerateArray())
        {
            if (result.Count >= MaxBoundaryCount)
            {
                throw new NarrativePatternValidationException(
                    "pattern.boundary_limit_exceeded",
                    $"Boundary output can contain at most {MaxBoundaryCount.ToString(CultureInfo.InvariantCulture)} items.");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_boundary_schema",
                    "Each boundary must be an object.");
            }

            var start = ReadPositiveInt(item, "start_chapter", "pattern.invalid_boundary_range");
            var end = ReadPositiveInt(item, "end_chapter", "pattern.invalid_boundary_range");
            if (start > end)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_boundary_range",
                    "Boundary start_chapter must be less than or equal to end_chapter.");
            }

            if (start <= previousEnd)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_boundary_order",
                    "Boundaries must be ascending and non-overlapping.");
            }

            if (!chapterNumbers.Contains(start) || !chapterNumbers.Contains(end))
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_boundary_range",
                    "Boundary start and end chapters must exist in the selected chapters.");
            }

            EnsureCoveredRangeExists(chapterNumbers, start, end, "pattern.invalid_boundary_range");
            result.Add(new NarrativePatternBoundary(
                start,
                end,
                NormalizeText(ReadString(item, "label", "pattern.invalid_boundary_schema"), "label", MaxTitleLength),
                NormalizeText(ReadString(item, "function", "pattern.invalid_boundary_schema"), "function", MaxTextLength),
                NormalizeText(ReadString(item, "evidence", "pattern.invalid_boundary_schema"), "evidence", MaxTextLength)));
            previousEnd = end;
        }

        EnsureFullCoverage(chapterNumbers, result.Select(item => (item.StartChapter, item.EndChapter)), "pattern.boundary_coverage_gap");
        return result;
    }

    public static IReadOnlyList<NarrativePatternChapterSummary> ParseChapterSummaries(
        string rawJson,
        IReadOnlyList<NarrativePatternChapterDocument> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        using var document = ParseObject(rawJson, "pattern.invalid_summary_json");
        var root = document.RootElement;
        RequireSchema(root, "pattern.invalid_summary_schema");

        if (!TryReadArray(root, "summaries", out var summariesElement))
        {
            throw new NarrativePatternValidationException(
                "pattern.invalid_summary_schema",
                "Summary JSON requires a summaries array.");
        }

        var byNumber = chapters.ToDictionary(chapter => chapter.ChapterNumber);
        var result = new List<NarrativePatternChapterSummary>();
        var seen = new HashSet<int>();
        foreach (var item in summariesElement.EnumerateArray())
        {
            if (result.Count >= MaxSummaryCount)
            {
                throw new NarrativePatternValidationException(
                    "pattern.summary_limit_exceeded",
                    $"Summary output can contain at most {MaxSummaryCount.ToString(CultureInfo.InvariantCulture)} items.");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_summary_schema",
                    "Each summary must be an object.");
            }

            var chapterNumber = ReadPositiveInt(item, "chapter_number", "pattern.invalid_summary_chapter");
            if (!byNumber.TryGetValue(chapterNumber, out var chapter))
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_summary_chapter",
                    "Summary chapter_number must exist in the selected chapters.");
            }

            if (!seen.Add(chapterNumber))
            {
                throw new NarrativePatternValidationException(
                    "pattern.duplicate_summary",
                    $"Duplicate summary for chapter '{chapterNumber.ToString(CultureInfo.InvariantCulture)}'.");
            }

            var contentHash = ReadString(item, "content_hash", "pattern.invalid_summary_schema");
            if (!string.Equals(contentHash, chapter.ContentHash, StringComparison.Ordinal))
            {
                throw new NarrativePatternValidationException(
                    "pattern.stale_summary",
                    $"Summary for chapter '{chapterNumber.ToString(CultureInfo.InvariantCulture)}' does not match the current content hash.");
            }

            result.Add(new NarrativePatternChapterSummary(
                chapter.ChapterId,
                chapterNumber,
                contentHash,
                NormalizeText(ReadString(item, "summary", "pattern.invalid_summary_schema"), "summary", MaxTextLength),
                ReadStringArray(item, "turning_points", "pattern.invalid_summary_schema", maxItems: 24)));
        }

        var missing = byNumber.Keys.Except(seen).Order().ToArray();
        if (missing.Length > 0)
        {
            throw new NarrativePatternValidationException(
                "pattern.summary_coverage_gap",
                $"Missing summaries for chapters: {string.Join(", ", missing)}.");
        }

        return result.OrderBy(item => item.ChapterNumber).ToArray();
    }

    public static IReadOnlyList<NarrativePatternPhase> ParsePhases(
        string rawJson,
        IReadOnlyList<NarrativePatternChapterSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        using var document = ParseObject(rawJson, "pattern.invalid_phase_json");
        var root = document.RootElement;
        RequireSchema(root, "pattern.invalid_phase_schema");

        if (!TryReadArray(root, "phases", out var phasesElement))
        {
            throw new NarrativePatternValidationException(
                "pattern.invalid_phase_schema",
                "Phase JSON requires a phases array.");
        }

        var chapterNumbers = summaries.Select(summary => summary.ChapterNumber).ToHashSet();
        var result = new List<NarrativePatternPhase>();
        var previousEnd = 0;
        foreach (var item in phasesElement.EnumerateArray())
        {
            if (result.Count >= MaxPhaseCount)
            {
                throw new NarrativePatternValidationException(
                    "pattern.phase_limit_exceeded",
                    $"Phase output can contain at most {MaxPhaseCount.ToString(CultureInfo.InvariantCulture)} items.");
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_phase_schema",
                    "Each phase must be an object.");
            }

            var start = ReadPositiveInt(item, "start_chapter", "pattern.invalid_phase_range");
            var end = ReadPositiveInt(item, "end_chapter", "pattern.invalid_phase_range");
            if (start > end)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_phase_range",
                    "Phase start_chapter must be less than or equal to end_chapter.");
            }

            if (start <= previousEnd)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_phase_order",
                    "Phases must be ascending and non-overlapping.");
            }

            if (!chapterNumbers.Contains(start) || !chapterNumbers.Contains(end))
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_phase_range",
                    "Phase start and end chapters must exist in summarized chapters.");
            }

            EnsureCoveredRangeExists(chapterNumbers, start, end, "pattern.invalid_phase_range");
            result.Add(new NarrativePatternPhase(
                start,
                end,
                NormalizeText(ReadString(item, "phase_name", "pattern.invalid_phase_schema"), "phase_name", MaxTitleLength),
                NormalizeText(ReadString(item, "narrative_function", "pattern.invalid_phase_schema"), "narrative_function", MaxTextLength),
                NormalizeText(ReadString(item, "guidance", "pattern.invalid_phase_schema"), "guidance", MaxTextLength)));
            previousEnd = end;
        }

        if (result.Count == 0)
        {
            throw new NarrativePatternValidationException(
                "pattern.empty_phase_output",
                "Model returned no narrative phases.");
        }

        EnsureFullCoverage(chapterNumbers, result.Select(item => (item.StartChapter, item.EndChapter)), "pattern.phase_coverage_gap");
        return result;
    }

    public static IReadOnlyList<NarrativePatternBatch<T>> CreateTokenBatches<T>(
        IReadOnlyList<T> items,
        Func<T, int> tokenSelector,
        int contextWindowTokens = DefaultContextWindowTokens,
        int reservedOutputTokens = ReservedOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(tokenSelector);

        var budget = Math.Max(512, contextWindowTokens - reservedOutputTokens);
        var batches = new List<NarrativePatternBatch<T>>();
        var current = new List<T>();
        var currentTokens = 0;

        foreach (var item in items)
        {
            var tokens = Math.Max(1, tokenSelector(item));
            if (tokens > budget)
            {
                if (current.Count > 0)
                {
                    AddBatch(batches, current, currentTokens);
                    current = [];
                    currentTokens = 0;
                }

                AddBatch(batches, [item], tokens);
                continue;
            }

            if (current.Count > 0 && currentTokens + tokens > budget)
            {
                AddBatch(batches, current, currentTokens);
                current = [];
                currentTokens = 0;
            }

            current.Add(item);
            currentTokens += tokens;
        }

        if (current.Count > 0)
        {
            AddBatch(batches, current, currentTokens);
        }

        if (batches.Count > MaxBatchCount)
        {
            throw new NarrativePatternValidationException(
                "pattern.batch_limit_exceeded",
                $"Token batching produced more than {MaxBatchCount.ToString(CultureInfo.InvariantCulture)} batches.");
        }

        return batches;
    }

    public static NarrativePatternCompressionDecision EvaluateCompressionProgress(
        int round,
        int previousPhaseCount,
        int currentPhaseCount,
        int targetPhaseCount,
        int maxRounds = MaxCompressionRounds)
    {
        if (round <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(round), round, "Compression round must be positive.");
        }

        if (previousPhaseCount <= 0 || currentPhaseCount <= 0 || targetPhaseCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentPhaseCount), "Compression counts must be positive.");
        }

        if (currentPhaseCount <= targetPhaseCount)
        {
            return new NarrativePatternCompressionDecision(true, false, "target_reached");
        }

        if (round >= maxRounds)
        {
            return new NarrativePatternCompressionDecision(true, true, "max_rounds_reached");
        }

        if (currentPhaseCount >= previousPhaseCount)
        {
            return new NarrativePatternCompressionDecision(true, true, "compression_stalled");
        }

        return new NarrativePatternCompressionDecision(false, false, "continue");
    }

    public static int EstimateTokens(string? text)
    {
        var length = (text ?? string.Empty).Length;
        return Math.Max(1, (int)Math.Ceiling(length / 3.0));
    }

    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static IReadOnlyList<ChapterRangePayload> NormalizeRanges(
        IReadOnlyList<ChapterRangePayload> ranges,
        IReadOnlyDictionary<int, ChapterPayload> chaptersByNumber)
    {
        var normalized = new List<ChapterRangePayload>();
        foreach (var range in ranges)
        {
            if (range.StartChapter <= 0 || range.EndChapter <= 0)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_chapter_range",
                    "Chapter range values must be positive.");
            }

            if (range.StartChapter > range.EndChapter)
            {
                throw new NarrativePatternValidationException(
                    "pattern.invalid_chapter_range",
                    "Chapter range start must be less than or equal to end.");
            }

            if (!chaptersByNumber.ContainsKey(range.StartChapter) || !chaptersByNumber.ContainsKey(range.EndChapter))
            {
                throw new NarrativePatternValidationException(
                    "pattern.chapter_range_out_of_bounds",
                    "Chapter range start and end must exist in the novel.");
            }

            var missing = Enumerable
                .Range(range.StartChapter, range.EndChapter - range.StartChapter + 1)
                .Where(number => !chaptersByNumber.ContainsKey(number))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new NarrativePatternValidationException(
                    "pattern.chapter_range_gap",
                    $"Chapter range includes missing chapter numbers: {string.Join(", ", missing)}.");
            }

            normalized.Add(range);
        }

        return NumbersToRanges(normalized.SelectMany(range =>
            Enumerable.Range(range.StartChapter, range.EndChapter - range.StartChapter + 1))).ToArray();
    }

    private static IEnumerable<ChapterRangePayload> NumbersToRanges(IEnumerable<int> chapterNumbers)
    {
        var ordered = chapterNumbers.Distinct().Order().ToArray();
        if (ordered.Length == 0)
        {
            yield break;
        }

        var start = ordered[0];
        var end = ordered[0];
        for (var index = 1; index < ordered.Length; index++)
        {
            var number = ordered[index];
            if (number == end + 1)
            {
                end = number;
                continue;
            }

            yield return new ChapterRangePayload(start, end);
            start = number;
            end = number;
        }

        yield return new ChapterRangePayload(start, end);
    }

    private static JsonDocument ParseObject(string rawJson, string code)
    {
        try
        {
            var document = JsonDocument.Parse(rawJson ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new NarrativePatternValidationException(code, "Model output must be a JSON object.");
            }

            return document;
        }
        catch (JsonException ex)
        {
            throw new NarrativePatternValidationException(code, $"Model output must be valid JSON: {ex.Message}");
        }
    }

    private static void RequireSchema(JsonElement root, string code)
    {
        var schema = ReadString(root, "schema_version", code);
        if (!string.Equals(schema, SchemaVersion, StringComparison.Ordinal))
        {
            throw new NarrativePatternValidationException(
                code,
                $"Model output schema_version must be '{SchemaVersion}'.");
        }
    }

    private static bool TryReadArray(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static int ReadPositiveInt(JsonElement element, string propertyName, string code)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number) ||
            number <= 0)
        {
            throw new NarrativePatternValidationException(
                code,
                $"Property '{propertyName}' must be a positive integer.");
        }

        return number;
    }

    private static string ReadString(JsonElement element, string propertyName, string code)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new NarrativePatternValidationException(
                code,
                $"Property '{propertyName}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName, string code, int maxItems)
    {
        if (!TryReadArray(element, propertyName, out var array))
        {
            throw new NarrativePatternValidationException(
                code,
                $"Property '{propertyName}' must be an array.");
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (values.Count >= maxItems)
            {
                throw new NarrativePatternValidationException(
                    code,
                    $"Property '{propertyName}' can contain at most {maxItems.ToString(CultureInfo.InvariantCulture)} items.");
            }

            if (item.ValueKind != JsonValueKind.String)
            {
                throw new NarrativePatternValidationException(
                    code,
                    $"Property '{propertyName}' must contain strings only.");
            }

            values.Add(NormalizeText(item.GetString() ?? string.Empty, propertyName, MaxTextLength));
        }

        if (values.Count == 0)
        {
            throw new NarrativePatternValidationException(
                code,
                $"Property '{propertyName}' must not be empty.");
        }

        return values;
    }

    private static string NormalizeText(string value, string name, int maxLength)
    {
        var normalized = NormalizeMultiline(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new NarrativePatternValidationException(
                "pattern.empty_text",
                $"Property '{name}' must be non-empty.");
        }

        if (normalized.Length > maxLength)
        {
            throw new NarrativePatternValidationException(
                "pattern.text_too_long",
                $"Property '{name}' must be at most {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        if (normalized.Any(ch => char.IsControl(ch) && ch is not ('\r' or '\n' or '\t')))
        {
            throw new NarrativePatternValidationException(
                "pattern.invalid_text",
                $"Property '{name}' contains unsupported control characters.");
        }

        return normalized;
    }

    private static string NormalizeMultiline(string? value)
    {
        return (value ?? string.Empty).Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void EnsureCoveredRangeExists(HashSet<int> chapterNumbers, int start, int end, string code)
    {
        for (var number = start; number <= end; number++)
        {
            if (!chapterNumbers.Contains(number))
            {
                throw new NarrativePatternValidationException(
                    code,
                    $"Range includes missing chapter '{number.ToString(CultureInfo.InvariantCulture)}'.");
            }
        }
    }

    private static void EnsureFullCoverage(
        HashSet<int> chapterNumbers,
        IEnumerable<(int StartChapter, int EndChapter)> ranges,
        string code)
    {
        var covered = new HashSet<int>();
        foreach (var (start, end) in ranges)
        {
            for (var number = start; number <= end; number++)
            {
                covered.Add(number);
            }
        }

        var missing = chapterNumbers.Except(covered).Order().ToArray();
        if (missing.Length > 0)
        {
            throw new NarrativePatternValidationException(
                code,
                $"Model output does not cover chapters: {string.Join(", ", missing)}.");
        }
    }

    private static void AddBatch<T>(
        List<NarrativePatternBatch<T>> batches,
        IReadOnlyList<T> items,
        int estimatedTokens)
    {
        batches.Add(new NarrativePatternBatch<T>(
            batches.Count,
            batches.Count + 1,
            items.ToArray(),
            estimatedTokens));
    }
}

public sealed record NarrativePatternChapterSelection(
    string SelectionMode,
    IReadOnlyList<ChapterPayload> Chapters,
    IReadOnlyList<ChapterRangePayload> ChapterRanges,
    IReadOnlyList<long> SelectedChapterIds);

public sealed record NarrativePatternChapterDocument(
    long ChapterId,
    int ChapterNumber,
    string Title,
    string FilePath,
    string Content,
    int EstimatedTokens,
    string ContentHash);

public sealed record NarrativePatternBoundary(
    int StartChapter,
    int EndChapter,
    string Label,
    string Function,
    string Evidence);

public sealed record NarrativePatternChapterSummary(
    long ChapterId,
    int ChapterNumber,
    string ContentHash,
    string Summary,
    IReadOnlyList<string> TurningPoints);

public sealed record NarrativePatternPhase(
    int StartChapter,
    int EndChapter,
    string PhaseName,
    string NarrativeFunction,
    string Guidance);

public sealed record NarrativePatternBatch<T>(
    int BatchIndex,
    int BatchNumber,
    IReadOnlyList<T> Items,
    int EstimatedTokens);

public sealed record NarrativePatternCompressionDecision(
    bool Stop,
    bool Stalled,
    string Reason);

public sealed class NarrativePatternValidationException : ArgumentException
{
    public NarrativePatternValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
