using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceCorpusWritingService : IReferenceCorpusWritingService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppInitializationOptions _options;
    private readonly IReferenceCorpusService _corpus;
    private readonly IChapterContentService _chapters;
    private readonly IReferenceCorpusQueryContextParser _parser;
    private readonly IReferenceCorpusBlueprintAssembler _blueprints;
    private readonly IReferenceCorpusTextAssembler _textAssembler;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceCorpusWritingService(
        AppInitializationOptions? options = null,
        IReferenceCorpusService? corpus = null,
        IChapterContentService? chapters = null,
        IReferenceCorpusQueryContextParser? parser = null,
        IReferenceCorpusBlueprintAssembler? blueprints = null,
        IReferenceCorpusSlotResolver? slots = null,
        IReferenceCorpusTextAssembler? textAssembler = null)
    {
        _options = options ?? new AppInitializationOptions();
        _corpus = corpus ?? new SqliteReferenceCorpusService(_options);
        _chapters = chapters ?? new FileSystemChapterContentService(_options);
        _parser = parser ?? new DeterministicReferenceCorpusQueryContextParser();
        _blueprints = blueprints ?? new SingleBeatReferenceCorpusBlueprintAssembler();
        _textAssembler = textAssembler ?? new PreservingReferenceCorpusTextAssembler(
            slots ?? new HeuristicReferenceCorpusSlotResolver());
    }

    public async ValueTask<ReferenceCorpusInsertionDraftPayload> GenerateInsertionDraftAsync(
        GenerateReferenceCorpusInsertionDraftPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);

        var queryContext = await _parser.ParseAsync(
            new ReferenceCorpusQueryParsingRequest(
                input.NaturalLanguageGoal,
                input.ChapterContext,
                input.Scope),
            cancellationToken);
        var candidates = await _corpus.SearchCandidatesAsync(
            new SearchReferenceCorpusCandidatesPayload(
                queryContext,
                new PageRequestPayload(
                    Cursor: null,
                    PageSize: 20,
                    SortBy: "score",
                    SortDir: "desc",
                    Filters: new Dictionary<string, string>
                    {
                        ["node_type"] = ReferenceCorpusNodeTypes.Sentence
                    })),
            cancellationToken);
        var blueprint = await _blueprints.AssembleAsync(
            new ReferenceCorpusBlueprintAssemblyRequest(queryContext, candidates.Items),
            cancellationToken);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);

            if (blueprint.Beats.Count == 0)
            {
                var chapterText = await CurrentChapterTextAsync(input.ChapterContext, cancellationToken);
                return EmptyResult(queryContext, blueprint, chapterText, "no_candidates");
            }

            var sourcePieces = await ReadSourcePiecesAsync(connection, blueprint, candidates.Items, cancellationToken);
            if (sourcePieces.Count == 0)
            {
                var chapterText = await CurrentChapterTextAsync(input.ChapterContext, cancellationToken);
                return EmptyResult(queryContext, blueprint, chapterText, "source_node_missing");
            }

            await UpsertBeatPiecesAsync(connection, blueprint, sourcePieces, cancellationToken);
            var textResult = await _textAssembler.AssembleAsync(
                new ReferenceCorpusTextAssemblyRequest(
                    blueprint,
                    sourcePieces.Select(piece => piece.ToSourcePiece()).ToArray(),
                    input.ChapterContext,
                    input.SlotValues),
                cancellationToken);
            var gate = EvaluateGate(sourcePieces, textResult.Pieces);
            var chapterBefore = await CurrentChapterTextAsync(input.ChapterContext, cancellationToken);
            var ready = gate.Passed && textResult.Pieces.All(piece => piece.PreservedHashMatches);
            var chapterAfter = ready
                ? InsertAtOffset(chapterBefore, input.ChapterContext.InsertionOffset, textResult.AssembledText)
                : chapterBefore;

            return new ReferenceCorpusInsertionDraftPayload(
                queryContext,
                blueprint,
                textResult.Pieces,
                textResult.SlotReplacements,
                textResult.AssembledText,
                chapterAfter,
                ready,
                gate);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static void ValidateInput(GenerateReferenceCorpusInsertionDraftPayload input)
    {
        if (string.IsNullOrWhiteSpace(input.NaturalLanguageGoal))
        {
            throw new ArgumentException("Natural language goal is required.", nameof(input));
        }

        ArgumentNullException.ThrowIfNull(input.ChapterContext);
        if (input.ChapterContext.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ChapterContext.NovelId, "Novel id must be positive.");
        }

        if (input.ChapterContext.ChapterNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.ChapterContext.ChapterNumber, "Chapter number must be positive.");
        }

        ArgumentNullException.ThrowIfNull(input.Scope);
        if (input.Scope.LibraryIds.Count == 0)
        {
            throw new ArgumentException("At least one corpus library is required.", nameof(input));
        }
    }

    private async ValueTask<string> CurrentChapterTextAsync(
        CurrentChapterContextPayload chapterContext,
        CancellationToken cancellationToken)
    {
        if (chapterContext.CurrentDraftText is not null)
        {
            return chapterContext.CurrentDraftText;
        }

        var chapters = await _chapters.GetChaptersAsync(chapterContext.NovelId, cancellationToken);
        var chapter = chapters.FirstOrDefault(item => item.ChapterNumber == chapterContext.ChapterNumber);
        return chapter is null
            ? string.Empty
            : await _chapters.GetContentAsync(chapterContext.NovelId, chapter.FilePath, cancellationToken);
    }

    private async ValueTask<string> DatabasePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "reference-anchor",
            "index.sqlite");
    }

    private static async ValueTask EnsureSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async ValueTask<IReadOnlyList<LicensedSourcePiece>> ReadSourcePiecesAsync(
        SqliteConnection connection,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        CancellationToken cancellationToken)
    {
        var candidatesByNode = candidates.ToDictionary(item => item.NodeId, StringComparer.Ordinal);
        var requested = blueprint.Beats
            .SelectMany(beat => beat.NodeIds.Select((nodeId, index) => new RequestedPiece(beat, nodeId, index)))
            .ToArray();
        if (requested.Length == 0)
        {
            return [];
        }

        var nodeIds = requested.Select(item => item.NodeId).Distinct(StringComparer.Ordinal).ToArray();
        var builder = new StringBuilder("""
            SELECT n.node_id,
                   n.text,
                   n.text_hash,
                   n.anchor_id,
                   lic.license_state,
                   lic.reuse_policy,
                   lic.max_verbatim_ratio,
                   lic.cleared_for_insertion
            FROM reference_text_nodes n
            JOIN reference_source_license lic ON lic.anchor_id = n.anchor_id
            WHERE 1 = 1
            """);
        var parameters = new List<(string Name, object Value)>();
        AppendInClause(builder, parameters, "n.node_id", nodeIds, "node_id");

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var rows = new Dictionary<string, SourceNodeRow>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows[reader.GetString(0)] = new SourceNodeRow(
                NodeId: reader.GetString(0),
                Text: reader.GetString(1),
                TextHash: reader.GetString(2),
                AnchorId: reader.GetInt64(3),
                LicenseState: reader.GetString(4),
                ReusePolicy: reader.GetString(5),
                MaxVerbatimRatio: reader.IsDBNull(6) ? null : reader.GetDouble(6),
                ClearedForInsertion: reader.GetInt32(7) != 0);
        }

        var pieces = new List<LicensedSourcePiece>(requested.Length);
        foreach (var request in requested)
        {
            if (!rows.TryGetValue(request.NodeId, out var row) ||
                !candidatesByNode.TryGetValue(request.NodeId, out var candidate))
            {
                continue;
            }

            pieces.Add(new LicensedSourcePiece(
                PieceId: "piece-" + StableHash(request.Beat.BeatId, request.NodeId)[..16],
                BeatId: request.Beat.BeatId,
                CandidateId: candidate.CandidateId,
                NodeId: request.NodeId,
                AnchorId: row.AnchorId,
                LibraryId: candidate.LibraryId,
                TextHash: row.TextHash,
                LicenseState: row.LicenseState,
                ReusePolicy: row.ReusePolicy,
                SourceText: row.Text,
                ClearedForInsertion: row.ClearedForInsertion,
                MaxVerbatimRatio: row.MaxVerbatimRatio,
                SequenceIndex: request.SequenceIndex));
        }

        return pieces;
    }

    private static async ValueTask UpsertBeatPiecesAsync(
        SqliteConnection connection,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        IReadOnlyList<LicensedSourcePiece> sourcePieces,
        CancellationToken cancellationToken)
    {
        var piecesByBeatAndNode = sourcePieces.ToDictionary(
            item => (item.BeatId, item.NodeId),
            item => item,
            ValueTupleComparer.Instance);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var beat in blueprint.Beats)
        {
            for (var index = 0; index < beat.NodeIds.Count; index++)
            {
                var nodeId = beat.NodeIds[index];
                if (!piecesByBeatAndNode.ContainsKey((beat.BeatId, nodeId)))
                {
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO reference_blueprint_beat_pieces
                      (beat_id, node_id, observation_id, role_in_beat, sequence_index)
                    VALUES
                      ($beat_id, $node_id, NULL, $role_in_beat, $sequence_index)
                    ON CONFLICT(beat_id, node_id) DO UPDATE SET
                      role_in_beat = excluded.role_in_beat,
                      sequence_index = excluded.sequence_index;
                    """;
                command.Parameters.AddWithValue("$beat_id", beat.BeatId);
                command.Parameters.AddWithValue("$node_id", nodeId);
                command.Parameters.AddWithValue("$role_in_beat", beat.RoleInBeat);
                command.Parameters.AddWithValue("$sequence_index", index);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static ReferenceCorpusInsertionGatePayload EvaluateGate(
        IReadOnlyList<LicensedSourcePiece> sourcePieces,
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> assembledPieces)
    {
        var sources = sourcePieces.ToDictionary(item => item.PieceId, StringComparer.Ordinal);
        var errors = new List<string>();
        var gatePieces = new List<ReferenceCorpusInsertionGatePiecePayload>(assembledPieces.Count);
        foreach (var piece in assembledPieces)
        {
            if (!sources.TryGetValue(piece.PieceId, out var source))
            {
                errors.Add($"source_missing:{piece.PieceId}");
                continue;
            }

            if (!source.ClearedForInsertion)
            {
                errors.Add($"license_not_cleared:{piece.NodeId}");
            }

            if (source.ReusePolicy is not (ReferenceCorpusReusePolicies.VerbatimOk or ReferenceCorpusReusePolicies.AdaptedOnly))
            {
                errors.Add($"reuse_policy_not_insertable:{piece.NodeId}");
            }

            if (!piece.PreservedHashMatches)
            {
                errors.Add($"preserved_hash_mismatch:{piece.NodeId}");
            }

            var policy = BuildSimilarityPolicy(source);
            var similarity = ReferenceCorpusSimilarityGate.Evaluate(
                new ReferenceCorpusSimilarityPiece(
                    piece.PieceId,
                    piece.NodeId,
                    source.SourceText,
                    piece.OutputText),
                policy);
            gatePieces.Add(new ReferenceCorpusInsertionGatePiecePayload(
                piece.PieceId,
                piece.NodeId,
                similarity.ShouldBlock,
                Math.Round(similarity.FourGramContainmentRatio, 6),
                Math.Round(similarity.LongestCommonSubstringRatio, 6),
                similarity.Violations
                    .Select(violation => new ReferenceCorpusInsertionGateViolationPayload(
                        violation.Metric,
                        Math.Round(violation.Actual, 6),
                        Math.Round(violation.Threshold, 6)))
                    .ToArray()));
        }

        var passed = errors.Count == 0 && gatePieces.All(piece => !piece.ShouldBlock);
        return new ReferenceCorpusInsertionGatePayload(
            passed,
            passed ? "passed" : "blocked",
            errors,
            gatePieces);
    }

    private static ReferenceCorpusSimilarityPolicy BuildSimilarityPolicy(LicensedSourcePiece source)
    {
        if (source.MaxVerbatimRatio is { } ratio)
        {
            var bounded = Math.Clamp(ratio, 0, 1);
            return new ReferenceCorpusSimilarityPolicy(bounded, bounded);
        }

        return source.ReusePolicy == ReferenceCorpusReusePolicies.VerbatimOk
            ? ReferenceCorpusSimilarityPolicy.VerbatimOkDefault
            : ReferenceCorpusSimilarityPolicy.AdaptedOnlyDefault;
    }

    private static string InsertAtOffset(string chapterText, int insertionOffset, string insertionText)
    {
        if (string.IsNullOrWhiteSpace(insertionText))
        {
            return chapterText;
        }

        var offset = Math.Clamp(insertionOffset, 0, chapterText.Length);
        var prefix = chapterText[..offset];
        var suffix = chapterText[offset..];
        var builder = new StringBuilder(prefix);
        if (builder.Length > 0 && !EndsWithLineBreak(builder.ToString()))
        {
            builder.Append(Environment.NewLine);
        }

        builder.Append(insertionText.Trim());
        if (suffix.Length > 0 && !EndsWithLineBreak(builder.ToString()))
        {
            builder.Append(Environment.NewLine);
        }

        builder.Append(suffix);
        return builder.ToString();
    }

    private static bool EndsWithLineBreak(string value)
    {
        return value.EndsWith('\n') || value.EndsWith('\r');
    }

    private static ReferenceCorpusInsertionDraftPayload EmptyResult(
        ReferenceCorpusQueryContextPayload queryContext,
        ReferenceCorpusInsertionBlueprintPayload blueprint,
        string chapterText,
        string status)
    {
        return new ReferenceCorpusInsertionDraftPayload(
            queryContext,
            blueprint,
            Pieces: [],
            SlotReplacements: [],
            AssembledText: string.Empty,
            ChapterTextAfterInsertion: chapterText,
            ReadyForInsertion: false,
            Gate: new ReferenceCorpusInsertionGatePayload(
                Passed: false,
                Status: status,
                Errors: [status],
                Pieces: []));
    }

    private static void AppendInClause<T>(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        string columnName,
        IReadOnlyCollection<T> values,
        string parameterPrefix)
        where T : notnull
    {
        if (values.Count == 0)
        {
            builder.AppendLine(" AND 1 = 0");
            return;
        }

        var names = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values)
        {
            var name = "$" + parameterPrefix + "_" + index.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add((name, value));
            index++;
        }

        builder.Append(" AND ");
        builder.Append(columnName);
        builder.Append(" IN (");
        builder.Append(string.Join(", ", names));
        builder.AppendLine(")");
    }

    private static string StableHash(params string[] parts)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\u001f', parts));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private sealed record SourceNodeRow(
        string NodeId,
        string Text,
        string TextHash,
        long AnchorId,
        string LicenseState,
        string ReusePolicy,
        double? MaxVerbatimRatio,
        bool ClearedForInsertion);

    private sealed record RequestedPiece(
        ReferenceCorpusInsertionBlueprintBeatPayload Beat,
        string NodeId,
        int SequenceIndex);

    private sealed record LicensedSourcePiece(
        string PieceId,
        string BeatId,
        string CandidateId,
        string NodeId,
        long AnchorId,
        string LibraryId,
        string TextHash,
        string LicenseState,
        string ReusePolicy,
        string SourceText,
        bool ClearedForInsertion,
        double? MaxVerbatimRatio,
        int SequenceIndex)
    {
        public ReferenceCorpusSourcePiece ToSourcePiece()
        {
            return new ReferenceCorpusSourcePiece(
                PieceId,
                BeatId,
                CandidateId,
                NodeId,
                AnchorId,
                LibraryId,
                TextHash,
                LicenseState,
                ReusePolicy,
                SourceText);
        }
    }

    private sealed class ValueTupleComparer : IEqualityComparer<(string BeatId, string NodeId)>
    {
        public static ValueTupleComparer Instance { get; } = new();

        public bool Equals((string BeatId, string NodeId) x, (string BeatId, string NodeId) y)
        {
            return string.Equals(x.BeatId, y.BeatId, StringComparison.Ordinal) &&
                string.Equals(x.NodeId, y.NodeId, StringComparison.Ordinal);
        }

        public int GetHashCode((string BeatId, string NodeId) obj)
        {
            return HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.BeatId),
                StringComparer.Ordinal.GetHashCode(obj.NodeId));
        }
    }
}

internal sealed class DeterministicReferenceCorpusQueryContextParser : IReferenceCorpusQueryContextParser
{
    public ValueTask<ReferenceCorpusQueryContextPayload> ParseAsync(
        ReferenceCorpusQueryParsingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var goal = request.NaturalLanguageGoal ?? string.Empty;
        var sceneType = ContainsAny(goal, "门口", "门里", "门外", "门缝", "对峙")
            ? "doorway_confrontation"
            : "scene_continuation";
        var emotionTarget = ContainsAny(goal, "压住", "克制", "怒", "不立刻开口", "没有立刻开口")
            ? "restrained_pressure"
            : "controlled_emotion";
        var pacingTarget = ContainsAny(goal, "慢", "压", "不立刻", "悬")
            ? "slow_tension"
            : "steady";
        var commercial = ContainsAny(goal, "不立刻", "悬念", "钩子", "对峙")
            ? "withheld-answer-hook"
            : "continuity";
        var functions = new HashSet<string>(StringComparer.Ordinal)
        {
            "support_current_chapter"
        };
        if (ContainsAny(goal, "对峙", "压住", "门口"))
        {
            functions.Add("raise_pressure");
        }

        if (ContainsAny(goal, "不立刻开口", "没有立刻开口"))
        {
            functions.Add("withhold_answer");
        }

        return ValueTask.FromResult(new ReferenceCorpusQueryContextPayload(
            SceneType: sceneType,
            EmotionTarget: emotionTarget,
            PacingTarget: pacingTarget,
            NarrativePosition: "current_insertion",
            CommercialMechanic: commercial,
            CharacterStates: request.ChapterContext.CharacterSnapshots
                .Select(snapshot => (snapshot.Character + " " + snapshot.State).Trim())
                .Where(value => value.Length > 0)
                .ToArray(),
            RequiredNarrativeFunctions: functions.ToArray(),
            ChapterContext: request.ChapterContext,
            Scope: request.Scope));
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));
    }
}

internal sealed class SingleBeatReferenceCorpusBlueprintAssembler : IReferenceCorpusBlueprintAssembler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<ReferenceCorpusInsertionBlueprintPayload> AssembleAsync(
        ReferenceCorpusBlueprintAssemblyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var selected = SelectCandidate(request.QueryContext, request.Candidates);
        var queryHash = StableHash(JsonSerializer.Serialize(request.QueryContext, JsonOptions));
        if (selected is null)
        {
            return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
                "corpus-blueprint-" + queryHash[..16],
                queryHash,
                "single_beat_m1",
                []));
        }

        var beatId = "corpus-beat-" + StableHash(queryHash, selected.NodeId)[..16];
        return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "corpus-blueprint-" + StableHash(queryHash, selected.NodeId)[..16],
            QueryContextHash: queryHash,
            Strategy: "single_beat_m1",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: beatId,
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: request.QueryContext.RequiredNarrativeFunctions.FirstOrDefault() ?? "support_current_chapter",
                    NodeIds: [selected.NodeId])
            ]));
    }

    private static ReferenceCorpusCandidatePayload? SelectCandidate(
        ReferenceCorpusQueryContextPayload queryContext,
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var preferWithheldAnswer = queryContext.RequiredNarrativeFunctions.Contains("withhold_answer", StringComparer.Ordinal) ||
            queryContext.CommercialMechanic.Contains("withheld", StringComparison.OrdinalIgnoreCase);
        if (preferWithheldAnswer)
        {
            var withheld = candidates.FirstOrDefault(candidate =>
                candidate.TextPreview.Contains("没有立刻开口", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("不立刻开口", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("不开口", StringComparison.Ordinal));
            if (withheld is not null)
            {
                return withheld;
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
            .First();
    }

    private static string StableHash(params string[] parts)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\u001f', parts));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}

internal sealed class HeuristicReferenceCorpusSlotResolver : IReferenceCorpusSlotResolver
{
    public ValueTask<ReferenceCorpusSlotResolutionResult> ResolveAsync(
        ReferenceCorpusSlotResolutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var replacements = new List<ReferenceCorpusSlotReplacementPayload>();
        AddExplicitReplacements(request, replacements);
        if (replacements.Count == 0)
        {
            AddPronounReplacement(request, replacements);
        }

        return ValueTask.FromResult(new ReferenceCorpusSlotResolutionResult(
            NormalizeReplacements(replacements)));
    }

    private static void AddExplicitReplacements(
        ReferenceCorpusSlotResolutionRequest request,
        List<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        foreach (var pair in request.ExplicitSlotValues)
        {
            var sourceValue = pair.Key?.Trim();
            var replacement = pair.Value?.Trim();
            if (string.IsNullOrWhiteSpace(sourceValue) || string.IsNullOrWhiteSpace(replacement))
            {
                continue;
            }

            var index = request.SourceText.IndexOf(sourceValue, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            replacements.Add(new ReferenceCorpusSlotReplacementPayload(
                SlotName: "explicit",
                SourceValue: sourceValue,
                ReplacementValue: replacement,
                SourceStart: index,
                SourceEnd: index + sourceValue.Length,
                OutputStart: 0,
                OutputEnd: 0));
        }
    }

    private static void AddPronounReplacement(
        ReferenceCorpusSlotResolutionRequest request,
        List<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        var character = request.ChapterContext.CharacterSnapshots
            .Select(snapshot => snapshot.Character.Trim())
            .FirstOrDefault(value => value.Length > 0);
        if (string.IsNullOrWhiteSpace(character))
        {
            return;
        }

        foreach (var pronoun in new[] { "她", "他" })
        {
            var index = request.SourceText.IndexOf(pronoun, StringComparison.Ordinal);
            if (index < 0 || index > 2)
            {
                continue;
            }

            replacements.Add(new ReferenceCorpusSlotReplacementPayload(
                SlotName: "character",
                SourceValue: pronoun,
                ReplacementValue: character,
                SourceStart: index,
                SourceEnd: index + pronoun.Length,
                OutputStart: 0,
                OutputEnd: 0));
            return;
        }
    }

    private static IReadOnlyList<ReferenceCorpusSlotReplacementPayload> NormalizeReplacements(
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        var result = new List<ReferenceCorpusSlotReplacementPayload>(replacements.Count);
        var lastEnd = 0;
        foreach (var replacement in replacements.OrderBy(item => item.SourceStart))
        {
            if (replacement.SourceStart < lastEnd ||
                replacement.SourceStart < 0 ||
                replacement.SourceEnd <= replacement.SourceStart)
            {
                continue;
            }

            result.Add(replacement);
            lastEnd = replacement.SourceEnd;
        }

        return result;
    }
}

internal sealed class PreservingReferenceCorpusTextAssembler : IReferenceCorpusTextAssembler
{
    private readonly IReferenceCorpusSlotResolver _slots;

    public PreservingReferenceCorpusTextAssembler(IReferenceCorpusSlotResolver slots)
    {
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
    }

    public async ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
        ReferenceCorpusTextAssemblyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var pieces = new List<ReferenceCorpusInsertionPiecePayload>(request.SourcePieces.Count);
        var allReplacements = new List<ReferenceCorpusSlotReplacementPayload>();
        foreach (var source in request.SourcePieces)
        {
            var resolved = await _slots.ResolveAsync(
                new ReferenceCorpusSlotResolutionRequest(
                    source.SourceText,
                    request.ChapterContext,
                    request.ExplicitSlotValues),
                cancellationToken);
            var applied = ApplyReplacements(source.SourceText, resolved.Replacements);
            var preservedSource = RemoveSourceSpans(source.SourceText, resolved.Replacements);
            var preservedOutput = RemoveOutputSpans(applied.OutputText, applied.Replacements);
            var preservedHash = StableHash(preservedOutput);
            var preservedMatches = string.Equals(
                StableHash(preservedSource),
                preservedHash,
                StringComparison.Ordinal);
            var payload = new ReferenceCorpusInsertionPiecePayload(
                PieceId: source.PieceId,
                BeatId: source.BeatId,
                CandidateId: source.CandidateId,
                NodeId: source.NodeId,
                AnchorId: source.AnchorId,
                LibraryId: source.LibraryId,
                SourceTextHash: source.TextHash,
                ReusePolicy: source.ReusePolicy,
                LicenseState: source.LicenseState,
                OutputText: applied.OutputText,
                PreservedTextHash: preservedHash,
                PreservedHashMatches: preservedMatches,
                SlotReplacements: applied.Replacements);
            pieces.Add(payload);
            allReplacements.AddRange(applied.Replacements);
        }

        return new ReferenceCorpusTextAssemblyResult(
            pieces,
            allReplacements,
            string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText.Trim()).Where(text => text.Length > 0)));
    }

    private static AppliedReplacementResult ApplyReplacements(
        string sourceText,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        if (replacements.Count == 0)
        {
            return new AppliedReplacementResult(sourceText, []);
        }

        var builder = new StringBuilder(sourceText.Length);
        var applied = new List<ReferenceCorpusSlotReplacementPayload>(replacements.Count);
        var cursor = 0;
        foreach (var replacement in replacements.OrderBy(item => item.SourceStart))
        {
            if (replacement.SourceStart < cursor ||
                replacement.SourceEnd > sourceText.Length)
            {
                continue;
            }

            builder.Append(sourceText, cursor, replacement.SourceStart - cursor);
            var outputStart = builder.Length;
            builder.Append(replacement.ReplacementValue);
            var outputEnd = builder.Length;
            applied.Add(replacement with
            {
                OutputStart = outputStart,
                OutputEnd = outputEnd
            });
            cursor = replacement.SourceEnd;
        }

        builder.Append(sourceText, cursor, sourceText.Length - cursor);
        return new AppliedReplacementResult(builder.ToString(), applied);
    }

    private static string RemoveSourceSpans(
        string text,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        return RemoveSpans(text, replacements.Select(item => (item.SourceStart, item.SourceEnd)).ToArray());
    }

    private static string RemoveOutputSpans(
        string text,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        return RemoveSpans(text, replacements.Select(item => (item.OutputStart, item.OutputEnd)).ToArray());
    }

    private static string RemoveSpans(string text, IReadOnlyList<(int Start, int End)> spans)
    {
        if (spans.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var span in spans.OrderBy(item => item.Start))
        {
            if (span.Start < cursor || span.End > text.Length || span.End <= span.Start)
            {
                continue;
            }

            builder.Append(text, cursor, span.Start - cursor);
            cursor = span.End;
        }

        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    private static string StableHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record AppliedReplacementResult(
        string OutputText,
        IReadOnlyList<ReferenceCorpusSlotReplacementPayload> Replacements);
}
