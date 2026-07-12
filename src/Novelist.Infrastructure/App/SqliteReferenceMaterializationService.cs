using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceMaterializationService : IReferenceMaterializationService
{
    private const int ChapterSplitSampleMaxChars = 50_000;
    private const int ChapterSplitPreviewLimit = 20;
    private const long MaxSourceBytes = 20L * 1024L * 1024L;
    private const string WorkspaceCorpusVisibility = "workspace";
    private static readonly Regex MarkdownHeadingPattern = new(
        @"^\s{0,3}#{1,6}\s+(?<title>.+?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly AppInitializationOptions _options;
    private readonly IReferenceChapterSplitAnalyzer _analyzer;
    private readonly IReferenceCorpusDatabasePathResolver _databasePathResolver;
    private readonly IReferenceMaterializationModelPreflight _modelPreflight;
    private readonly SqliteReferenceMaterializationRunStore _runStore;
    private readonly IReferenceMaterializationSemanticSearch _semanticSearch;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceMaterializationService(
        AppInitializationOptions? options = null,
        IReferenceChapterSplitAnalyzer? analyzer = null,
        IReferenceCorpusDatabasePathResolver? databasePathResolver = null,
        IReferenceMaterializationModelPreflight? modelPreflight = null,
        IReferenceMaterializationSemanticSearch? semanticSearch = null)
    {
        _options = options ?? new AppInitializationOptions();
        _analyzer = analyzer ?? new ReferenceChapterSplitChatCompletionAnalyzer(
            new FileSystemAppSettingsService(_options),
            new StandardChatCompletionClient(new FileSystemLlmConfigurationService(_options)));
        _databasePathResolver = databasePathResolver ?? new ReferenceCorpusDatabasePathResolver(_options);
        _modelPreflight = modelPreflight ?? new ReferenceMaterializationModelPreflight(
            new FileSystemAppSettingsService(_options),
            new StandardChatCompletionClient(new FileSystemLlmConfigurationService(_options)),
            new FileSystemEmbeddingSettingsService(_options),
            new HybridEmbeddingClient());
        _runStore = new SqliteReferenceMaterializationRunStore(_databasePathResolver);
        _semanticSearch = semanticSearch ?? new SqliteReferenceMaterializationSemanticSearch(
            _options,
            _databasePathResolver,
            new FileSystemEmbeddingSettingsService(_options),
            new HybridEmbeddingClient(),
            new SqliteVecTableProvisioner());
    }

    public async ValueTask<ReferenceChapterSplitProfilePayload> AnalyzeChapterSplitAsync(
        AnalyzeReferenceChapterSplitPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        var source = await ReadCurrentSourceAsync(input.NovelId, input.AnchorId, cancellationToken);
        var sample = source.NormalizedText[..Math.Min(ChapterSplitSampleMaxChars, source.NormalizedText.Length)];
        var modelResult = await _analyzer.AnalyzeAsync(
            new ReferenceChapterSplitModelRequest(input.AnchorId, source.Hash, sample),
            cancellationToken);
        ValidateModelResult(modelResult, sample.Length);
        ValidateModelEvidence(sample, modelResult);
        var boundaries = BuildBoundaries(source.NormalizedText, modelResult.PatternKind, modelResult.DelimiterTemplate);

        await EnsureSourceDidNotChangeAsync(source, input.NovelId, input.AnchorId, cancellationToken);
        return await PersistProfileAsync(
            input.NovelId,
            input.AnchorId,
            source,
            ReferenceChapterSplitModes.Auto,
            modelResult.PatternKind,
            modelResult.DelimiterTemplate,
            sample.Length,
            JsonSerializer.Serialize(new
            {
                evidence_offsets = modelResult.EvidenceOffsets
            }),
            modelResult.ProviderName,
            modelResult.ModelId,
            modelResult.Confidence,
            boundaries,
            cancellationToken);
    }

    public async ValueTask<ReferenceChapterSplitProfilePayload> PreviewChapterSplitAsync(
        PreviewReferenceChapterSplitPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        var template = NormalizeTemplate(input.DelimiterTemplate);
        var source = await ReadCurrentSourceAsync(input.NovelId, input.AnchorId, cancellationToken);
        var patternKind = ResolveManualPatternKind(template);
        var boundaries = BuildBoundaries(source.NormalizedText, patternKind, template);

        return await PersistProfileAsync(
            input.NovelId,
            input.AnchorId,
            source,
            ReferenceChapterSplitModes.Manual,
            patternKind,
            template,
            sampleCharCount: 0,
            patternJson: "{}",
            modelProvider: null,
            modelId: null,
            confidence: null,
            boundaries,
            cancellationToken);
    }

    public async ValueTask<ReferenceChapterSplitProfilePayload> ConfirmChapterSplitAsync(
        ConfirmReferenceChapterSplitPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateReferenceInput(input.NovelId, input.AnchorId);
        var profileId = NormalizeProfileId(input.SplitProfileId);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var profile = await ReadProfileAsync(connection, input.NovelId, input.AnchorId, profileId, cancellationToken)
                ?? throw new ArgumentException("Chapter split profile does not exist.", nameof(input));
            var source = await ReadCurrentSourceAsync(
                input.NovelId,
                input.AnchorId,
                cancellationToken,
                requireAnchorSourceHash: false);
            if (!string.Equals(profile.SourceHash, source.Hash, StringComparison.Ordinal))
            {
                await MarkProfileStaleAsync(connection, profileId, cancellationToken);
                throw new InvalidOperationException("Reference source changed after chapter split preview. Rebuild the source and analyze chapters again.");
            }

            if (profile.Status is ReferenceChapterSplitProfileStates.Stale or ReferenceChapterSplitProfileStates.Confirmed)
            {
                throw new InvalidOperationException("Chapter split profile is not available for confirmation.");
            }

            await using (var update = connection.CreateCommand())
            {
                update.CommandText = """
                    UPDATE reference_chapter_split_profiles
                    SET status = $status,
                        confirmed_at = $confirmed_at
                    WHERE split_profile_id = $split_profile_id;
                    """;
                update.Parameters.AddWithValue("$status", ReferenceChapterSplitProfileStates.Confirmed);
                update.Parameters.AddWithValue("$confirmed_at", FormatTimestamp(DateTimeOffset.UtcNow));
                update.Parameters.AddWithValue("$split_profile_id", profileId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            var boundaries = await ReadBoundariesAsync(connection, profileId, cancellationToken);
            return ToPayload(profile with { Status = ReferenceChapterSplitProfileStates.Confirmed }, boundaries);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<ReferenceChapterSplitProfilePayload> PersistProfileAsync(
        long novelId,
        long anchorId,
        SourceSnapshot source,
        string splitMode,
        string patternKind,
        string delimiterTemplate,
        int sampleCharCount,
        string patternJson,
        string? modelProvider,
        string? modelId,
        double? confidence,
        IReadOnlyList<ChapterBoundary> boundaries,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var current = await ReadCurrentSourceAsync(novelId, anchorId, cancellationToken);
            if (!string.Equals(current.Hash, source.Hash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Reference source changed while chapter split analysis was running.");
            }

            var profileId = Guid.NewGuid().ToString("N");
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var insertProfile = connection.CreateCommand())
            {
                insertProfile.Transaction = transaction;
                insertProfile.CommandText = """
                    INSERT INTO reference_chapter_split_profiles (
                      split_profile_id, anchor_id, source_hash, split_mode, sample_char_count,
                      sample_hash, pattern_kind, delimiter_template, pattern_json,
                      model_provider, model_id, confidence, status, chapter_count, created_at)
                    VALUES (
                      $split_profile_id, $anchor_id, $source_hash, $split_mode, $sample_char_count,
                      $sample_hash, $pattern_kind, $delimiter_template, $pattern_json,
                      $model_provider, $model_id, $confidence, $status, $chapter_count, $created_at);
                    """;
                insertProfile.Parameters.AddWithValue("$split_profile_id", profileId);
                insertProfile.Parameters.AddWithValue("$anchor_id", anchorId);
                insertProfile.Parameters.AddWithValue("$source_hash", source.Hash);
                insertProfile.Parameters.AddWithValue("$split_mode", splitMode);
                insertProfile.Parameters.AddWithValue("$sample_char_count", sampleCharCount);
                insertProfile.Parameters.AddWithValue("$sample_hash", HashText(source.NormalizedText[..sampleCharCount]));
                insertProfile.Parameters.AddWithValue("$pattern_kind", patternKind);
                insertProfile.Parameters.AddWithValue("$delimiter_template", delimiterTemplate);
                insertProfile.Parameters.AddWithValue("$pattern_json", patternJson);
                insertProfile.Parameters.AddWithValue("$model_provider", (object?)modelProvider ?? DBNull.Value);
                insertProfile.Parameters.AddWithValue("$model_id", (object?)modelId ?? DBNull.Value);
                insertProfile.Parameters.AddWithValue("$confidence", (object?)confidence ?? DBNull.Value);
                insertProfile.Parameters.AddWithValue("$status", ReferenceChapterSplitProfileStates.Validated);
                insertProfile.Parameters.AddWithValue("$chapter_count", boundaries.Count);
                insertProfile.Parameters.AddWithValue("$created_at", FormatTimestamp(DateTimeOffset.UtcNow));
                await insertProfile.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var boundary in boundaries)
            {
                await using var insertBoundary = connection.CreateCommand();
                insertBoundary.Transaction = transaction;
                insertBoundary.CommandText = """
                    INSERT INTO reference_chapter_split_boundaries (
                      split_profile_id, chapter_index, title, heading_start, content_start, content_end, text_hash)
                    VALUES (
                      $split_profile_id, $chapter_index, $title, $heading_start, $content_start, $content_end, $text_hash);
                    """;
                insertBoundary.Parameters.AddWithValue("$split_profile_id", profileId);
                insertBoundary.Parameters.AddWithValue("$chapter_index", boundary.ChapterIndex);
                insertBoundary.Parameters.AddWithValue("$title", boundary.Title);
                insertBoundary.Parameters.AddWithValue("$heading_start", boundary.HeadingStart);
                insertBoundary.Parameters.AddWithValue("$content_start", boundary.ContentStart);
                insertBoundary.Parameters.AddWithValue("$content_end", boundary.ContentEnd);
                insertBoundary.Parameters.AddWithValue("$text_hash", boundary.TextHash);
                await insertBoundary.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var profile = new StoredProfile(
                profileId,
                anchorId,
                source.Hash,
                splitMode,
                patternKind,
                delimiterTemplate,
                sampleCharCount,
                ReferenceChapterSplitProfileStates.Validated,
                boundaries.Count,
                modelProvider,
                modelId,
                confidence);
            return ToPayload(profile, boundaries);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<SourceSnapshot> ReadCurrentSourceAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken,
        bool requireAnchorSourceHash = true)
    {
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var anchor = await ReadAnchorSourceAsync(connection, novelId, anchorId, cancellationToken)
            ?? throw new ArgumentException("Reference source does not exist or is not accessible.", nameof(anchorId));
        var source = await ReadSourceFileAsync(anchor.SourcePath, cancellationToken);
        if (requireAnchorSourceHash && !string.Equals(anchor.SourceHash, source.Hash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reference source changed. Rebuild the source before configuring chapter split.");
        }

        return source;
    }

    private async ValueTask EnsureSourceDidNotChangeAsync(
        SourceSnapshot source,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentSourceAsync(novelId, anchorId, cancellationToken);
        if (!string.Equals(source.Hash, current.Hash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reference source changed while chapter split analysis was running.");
        }
    }

    private async ValueTask<string> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var databasePath = await _databasePathResolver.ResolveAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        return databasePath;
    }

    private static async ValueTask<AnchorSource?> ReadAnchorSourceAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, source_file_hash
            FROM reference_anchors
            WHERE anchor_id = $anchor_id
              AND (
                novel_id = $novel_id OR
                ((novel_id IS NULL OR novel_id = 0) AND corpus_visibility = $workspace_visibility)
              );
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_visibility", WorkspaceCorpusVisibility);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AnchorSource(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async ValueTask<StoredProfile?> ReadProfileAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.split_profile_id, p.anchor_id, p.source_hash, p.split_mode, p.pattern_kind,
                   p.delimiter_template, p.sample_char_count, p.status, p.chapter_count,
                   p.model_provider, p.model_id, p.confidence
            FROM reference_chapter_split_profiles p
            JOIN reference_anchors a ON a.anchor_id = p.anchor_id
            WHERE p.split_profile_id = $split_profile_id
              AND p.anchor_id = $anchor_id
              AND (
                a.novel_id = $novel_id OR
                ((a.novel_id IS NULL OR a.novel_id = 0) AND a.corpus_visibility = $workspace_visibility)
              );
            """;
        command.Parameters.AddWithValue("$split_profile_id", profileId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_visibility", WorkspaceCorpusVisibility);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredProfile(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetDouble(11));
    }

    private static async ValueTask<IReadOnlyList<ChapterBoundary>> ReadBoundariesAsync(
        SqliteConnection connection,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT chapter_index, title, heading_start, content_start, content_end, text_hash
            FROM reference_chapter_split_boundaries
            WHERE split_profile_id = $split_profile_id
            ORDER BY chapter_index ASC;
            """;
        command.Parameters.AddWithValue("$split_profile_id", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var boundaries = new List<ChapterBoundary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            boundaries.Add(new ChapterBoundary(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetString(5)));
        }

        return boundaries;
    }

    private static async ValueTask MarkProfileStaleAsync(
        SqliteConnection connection,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_chapter_split_profiles
            SET status = $status
            WHERE split_profile_id = $split_profile_id;
            """;
        command.Parameters.AddWithValue("$status", ReferenceChapterSplitProfileStates.Stale);
        command.Parameters.AddWithValue("$split_profile_id", profileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<ChapterBoundary> BuildBoundaries(
        string source,
        string patternKind,
        string delimiterTemplate)
    {
        var headings = FindHeadings(source, patternKind, delimiterTemplate);
        if (headings.Count < 2)
        {
            throw new ArgumentException("Chapter split must produce at least two valid chapter boundaries.", nameof(delimiterTemplate));
        }

        var boundaries = new List<ChapterBoundary>(headings.Count);
        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var nextStart = index + 1 < headings.Count ? headings[index + 1].HeadingStart : source.Length;
            var contentStart = SkipLeadingWhitespace(source, heading.LineEnd);
            var contentEnd = TrimTrailingWhitespace(source, nextStart);
            if (contentEnd <= contentStart)
            {
                throw new ArgumentException("Chapter split produced an empty chapter.", nameof(delimiterTemplate));
            }

            boundaries.Add(new ChapterBoundary(
                index + 1,
                heading.Title,
                heading.HeadingStart,
                contentStart,
                contentEnd,
                HashText(source[contentStart..contentEnd])));
        }

        return boundaries;
    }

    private static List<Heading> FindHeadings(string source, string patternKind, string delimiterTemplate)
    {
        return patternKind switch
        {
            "markdown_heading" => FindMarkdownHeadings(source),
            "chapter_template" => FindTemplateHeadings(source, delimiterTemplate),
            "literal" => FindLiteralDelimiters(source, delimiterTemplate),
            _ => throw new ArgumentException("Unsupported chapter split pattern kind.", nameof(patternKind))
        };
    }

    private static List<Heading> FindMarkdownHeadings(string source)
    {
        var headings = new List<Heading>();
        foreach (var line in EnumerateLines(source))
        {
            var match = MarkdownHeadingPattern.Match(source.Substring(line.Start, line.End - line.Start));
            if (!match.Success)
            {
                continue;
            }

            headings.Add(new Heading(line.Start, line.NextStart, NormalizeTitle(match.Groups["title"].Value)));
        }

        return headings;
    }

    private static List<Heading> FindTemplateHeadings(string source, string template)
    {
        var expression = BuildTemplateExpression(template);
        var headings = new List<Heading>();
        foreach (var line in EnumerateLines(source))
        {
            var match = expression.Match(source.Substring(line.Start, line.End - line.Start));
            if (!match.Success)
            {
                continue;
            }

            var title = match.Groups["title"].Success
                ? NormalizeTitle(match.Groups["title"].Value)
                : $"第{headings.Count + 1}章";
            headings.Add(new Heading(line.Start, line.NextStart, title));
        }

        return headings;
    }

    private static List<Heading> FindLiteralDelimiters(string source, string template)
    {
        var delimiter = template["literal:".Length..].Trim();
        if (delimiter.Length == 0)
        {
            throw new ArgumentException("Literal chapter delimiter must not be empty.", nameof(template));
        }

        var headings = new List<Heading>();
        foreach (var line in EnumerateLines(source))
        {
            if (string.Equals(source[line.Start..line.End].Trim(), delimiter, StringComparison.Ordinal))
            {
                headings.Add(new Heading(line.Start, line.NextStart, $"第{headings.Count + 1}章"));
            }
        }

        return headings;
    }

    private static Regex BuildTemplateExpression(string template)
    {
        if (!template.Contains("{number}", StringComparison.Ordinal) &&
            !template.Contains("{title}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Chapter template must contain {number} or {title}.", nameof(template));
        }

        var builder = new StringBuilder("^\\s*");
        for (var offset = 0; offset < template.Length;)
        {
            if (template.AsSpan(offset).StartsWith("{number}", StringComparison.Ordinal))
            {
                builder.Append("(?<number>[0-9０-９一二三四五六七八九十百千万零〇]+)");
                offset += "{number}".Length;
            }
            else if (template.AsSpan(offset).StartsWith("{title}", StringComparison.Ordinal))
            {
                builder.Append("(?<title>.+?)");
                offset += "{title}".Length;
            }
            else
            {
                var nextToken = FindNextTemplateToken(template, offset);
                builder.Append(Regex.Escape(template[offset..nextToken]));
                offset = nextToken;
            }
        }

        builder.Append("\\s*$");
        return new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static int FindNextTemplateToken(string template, int offset)
    {
        var number = template.IndexOf("{number}", offset, StringComparison.Ordinal);
        var title = template.IndexOf("{title}", offset, StringComparison.Ordinal);
        var next = new[] { number, title }.Where(index => index >= 0).DefaultIfEmpty(template.Length).Min();
        return next;
    }

    private static IEnumerable<SourceLine> EnumerateLines(string source)
    {
        var start = 0;
        while (start < source.Length)
        {
            var newline = source.IndexOf('\n', start);
            if (newline < 0)
            {
                yield return new SourceLine(start, source.Length, source.Length);
                yield break;
            }

            yield return new SourceLine(start, newline, newline + 1);
            start = newline + 1;
        }
    }

    private static int SkipLeadingWhitespace(string source, int offset)
    {
        while (offset < source.Length && char.IsWhiteSpace(source[offset]))
        {
            offset++;
        }

        return offset;
    }

    private static int TrimTrailingWhitespace(string source, int offset)
    {
        while (offset > 0 && char.IsWhiteSpace(source[offset - 1]))
        {
            offset--;
        }

        return offset;
    }

    private static string ResolveManualPatternKind(string template)
    {
        if (template.StartsWith("literal:", StringComparison.Ordinal))
        {
            return "literal";
        }

        return string.Equals(template, "# {title}", StringComparison.Ordinal)
            ? "markdown_heading"
            : "chapter_template";
    }

    private static void ValidateModelResult(ReferenceChapterSplitModelResult result, int sampleLength)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.PatternKind is not ("markdown_heading" or "chapter_template") ||
            string.IsNullOrWhiteSpace(result.DelimiterTemplate) ||
            result.DelimiterTemplate.Length > 160 ||
            string.IsNullOrWhiteSpace(result.ProviderName) ||
            string.IsNullOrWhiteSpace(result.ModelId) ||
            double.IsNaN(result.Confidence) ||
            double.IsInfinity(result.Confidence) ||
            result.Confidence < 0 || result.Confidence > 1 ||
            result.EvidenceOffsets is null ||
            result.EvidenceOffsets.Count == 0 ||
            result.EvidenceOffsets.Any(offset => offset < 0 || offset >= sampleLength))
        {
            throw new InvalidOperationException("Chapter split analysis returned invalid structured output.");
        }
    }

    private static void ValidateModelEvidence(string sample, ReferenceChapterSplitModelResult result)
    {
        var headingOffsets = FindHeadings(sample, result.PatternKind, result.DelimiterTemplate)
            .Select(heading => heading.HeadingStart)
            .ToHashSet();
        if (headingOffsets.Count == 0 || result.EvidenceOffsets.Any(offset => !headingOffsets.Contains(offset)))
        {
            throw new InvalidOperationException("Chapter split analysis returned evidence that does not match the source sample.");
        }
    }

    private static void ValidateReferenceInput(long novelId, long anchorId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), "Novel id must be positive.");
        }

        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId), "Anchor id must be positive.");
        }
    }

    private static string NormalizeTemplate(string value)
    {
        var template = value?.Trim() ?? string.Empty;
        if (template.Length == 0 || template.Length > 160 || template.Contains('\r') || template.Contains('\n'))
        {
            throw new ArgumentException("Chapter delimiter template must contain 1-160 non-line-break characters.", nameof(value));
        }

        return template;
    }

    private static string NormalizeProfileId(string value)
    {
        var profileId = value?.Trim() ?? string.Empty;
        if (profileId.Length is 0 or > 128)
        {
            throw new ArgumentException("Chapter split profile id is required.", nameof(value));
        }

        return profileId;
    }

    private static string NormalizeTitle(string value)
    {
        var title = value.Trim();
        if (title.Length == 0 || title.Length > 200)
        {
            throw new ArgumentException("Chapter title is invalid.", nameof(value));
        }

        return title;
    }

    private static async ValueTask<SourceSnapshot> ReadSourceFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxSourceBytes)
        {
            throw new InvalidOperationException("Reference source is unavailable for chapter split.");
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        var normalized = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Reference source is empty after normalization.");
        }

        return new SourceSnapshot(normalized, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string HashText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private static ReferenceChapterSplitProfilePayload ToPayload(
        StoredProfile profile,
        IReadOnlyList<ChapterBoundary> boundaries)
    {
        return new ReferenceChapterSplitProfilePayload(
            profile.ProfileId,
            profile.AnchorId,
            profile.SourceHash,
            profile.SplitMode,
            profile.PatternKind,
            profile.DelimiterTemplate,
            profile.SampleCharCount,
            profile.Status,
            profile.ChapterCount,
            boundaries.Take(ChapterSplitPreviewLimit).Select(boundary => new ReferenceChapterSplitBoundaryPayload(
                boundary.ChapterIndex,
                boundary.Title,
                boundary.HeadingStart,
                boundary.ContentStart,
                boundary.ContentEnd,
                boundary.TextHash)).ToArray(),
            profile.ModelProvider,
            profile.ModelId,
            profile.Confidence);
    }

    private sealed record AnchorSource(string SourcePath, string SourceHash);
    private sealed record SourceSnapshot(string NormalizedText, string Hash);
    private sealed record Heading(int HeadingStart, int LineEnd, string Title);
    private sealed record SourceLine(int Start, int End, int NextStart);
    private sealed record ChapterBoundary(
        int ChapterIndex,
        string Title,
        int HeadingStart,
        int ContentStart,
        int ContentEnd,
        string TextHash);
    private sealed record StoredProfile(
        string ProfileId,
        long AnchorId,
        string SourceHash,
        string SplitMode,
        string PatternKind,
        string DelimiterTemplate,
        int SampleCharCount,
        string Status,
        int ChapterCount,
        string? ModelProvider,
        string? ModelId,
        double? Confidence);
}
