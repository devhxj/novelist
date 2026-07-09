using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;
using Novelist.IntegrationTests.TestDoubles;

namespace Novelist.IntegrationTests;

public sealed class ReferenceCorpusWritingServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions GoldenJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateInsertionDraftRunsCorpusClosedLoopAndPersistsBeatPieces()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料闭环测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里，指尖还按着锁。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "rain-doorway.md",
            """
            # 第一章 雨门

            雨声贴着门缝往里挤。

            她没有立刻开口，只把钥匙扣在掌心。
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "雨门语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var embeddings = new DeterministicHashEmbeddingClient(defaultDimensions: 8);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            embeddings);
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写雨夜门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "雨夜有人靠近门口。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["门外有人靠近"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.True(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.Equal("doorway_confrontation", result.QueryContext.SceneType);
        Assert.Equal("restrained_pressure", result.QueryContext.EmotionTarget);
        Assert.Single(result.Blueprint.Beats);
        var piece = Assert.Single(result.Pieces);
        Assert.Equal(result.Blueprint.Beats[0].BeatId, piece.BeatId);
        Assert.Contains("秦砚没有立刻开口", result.AssembledText, StringComparison.Ordinal);
        Assert.DoesNotContain("她没有立刻开口", result.AssembledText, StringComparison.Ordinal);
        Assert.Equal(
            currentDraft + Environment.NewLine + result.AssembledText,
            result.ChapterTextAfterInsertion);
        Assert.Contains(result.SlotReplacements, replacement =>
            replacement.SourceValue == "她" && replacement.ReplacementValue == "秦砚");
        Assert.True(piece.PreservedHashMatches);
        Assert.True(await BlueprintBeatPieceExistsAsync(options, result.Blueprint.Beats[0].BeatId, piece.NodeId));
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenLicenseGateOrSimilarityGateFails()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料闸门测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "restricted-doorway.md",
            """
            # 第一章

            他没有立刻开口，只把钥匙扣在掌心。
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "受限语料",
                null,
                sourcePath,
                "markdown",
                "user_provided"),
            CancellationToken.None);
        await TightenAdaptedOnlyGateAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口压住情绪，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    null,
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", [], [])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.AdaptedOnly],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.False(result.Gate.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        Assert.Contains(result.Gate.Pieces, piece => piece.ShouldBlock);
    }

    [Fact]
    public async Task GenerateInsertionDraftFromGoldenBookAndFixedOutlineMatchesGoldenJson()
    {
        using var document = LoadCorpusDrivenWritingFixture("m15-insertion-draft-golden.json");
        var fixture = document.RootElement.GetProperty("fixtures")[0];
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novelSpec = fixture.GetProperty("novel");
        var chapterSpec = fixture.GetProperty("current_chapter");
        var sourceSpec = fixture.GetProperty("source");
        var requestSpec = fixture.GetProperty("request");

        var novel = await novels.CreateNovelAsync(
            new CreateNovelPayload(
                novelSpec.GetProperty("title").GetString() ?? "语料 golden",
                novelSpec.GetProperty("description").GetString() ?? string.Empty,
                string.Empty),
            CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(
            new CreateChapterPayload(
                novel.Id,
                chapterSpec.GetProperty("title").GetString() ?? "第一章"),
            CancellationToken.None);
        var currentDraft = chapterSpec.GetProperty("current_draft_text").GetString() ?? string.Empty;
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);

        var sourcePath = CreateSourceFile(
            sourceSpec.GetProperty("file_name").GetString() ?? "golden-source.md",
            sourceSpec.GetProperty("content").GetString() ?? string.Empty);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                sourceSpec.GetProperty("title").GetString() ?? "golden source",
                null,
                sourcePath,
                sourceSpec.GetProperty("format").GetString() ?? "markdown",
                sourceSpec.GetProperty("license_status").GetString() ?? "public_domain"),
            CancellationToken.None);
        await ApplyLicenseGateAsync(options, anchor.AnchorId, sourceSpec.GetProperty("license_gate"));
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: requestSpec.GetProperty("natural_language_goal").GetString() ?? string.Empty,
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    chapterSpec.GetProperty("insertion_offset").GetInt32(),
                    requestSpec.GetProperty("previous_chapter_summary").GetString(),
                    ReadCharacterSnapshots(requestSpec.GetProperty("character_snapshots"))),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    ReadStringArray(requestSpec.GetProperty("reuse_policies")),
                    [],
                    []),
                SlotValues: ReadStringDictionary(requestSpec.GetProperty("slot_values"))),
            CancellationToken.None);

        var expected = JsonNode.Parse(fixture.GetProperty("expected_draft").GetRawText());
        var actual = NormalizeDraftForGolden(result);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            "M1 corpus-driven-writing golden draft mismatch." +
            Environment.NewLine +
            "Actual normalized draft:" +
            Environment.NewLine +
            actual.ToJsonString(GoldenJsonOptions));
        var piece = Assert.Single(result.Pieces);
        Assert.True(await BlueprintBeatPieceExistsAsync(options, result.Blueprint.Beats[0].BeatId, piece.NodeId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppInitializationOptions CreateOptions()
    {
        return new AppInitializationOptions
        {
            ConfigDirectory = Path.Combine(_root, "config"),
            DefaultDataDirectory = Path.Combine(_root, "data")
        };
    }

    private string CreateSourceFile(string fileName, string content)
    {
        var sourceDirectory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(sourceDirectory);
        var path = Path.Combine(sourceDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static async ValueTask InitializeAsync(AppInitializationOptions options)
    {
        var initialization = new FileSystemAppInitializationService(options);
        await initialization.InitializeAsync(options.DefaultDataDirectory, CancellationToken.None);
    }

    private static EmbeddingRequestOptions CreateEmbeddingOptions()
    {
        return new EmbeddingRequestOptions(
            ProviderKey: "fake",
            EndpointUrl: string.Empty,
            ApiKey: string.Empty,
            ModelId: "hash-model",
            Dimensions: 8,
            User: null,
            NormalizeEmbeddings: true);
    }

    private static async ValueTask<string> ReadDefaultLibraryIdAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT library_id
            FROM reference_library_members
            WHERE anchor_id = $anchor_id
            ORDER BY library_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async ValueTask AllowVerbatimInsertionAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_source_license
            SET reuse_policy = 'verbatim_ok',
                license_state = 'public_domain',
                max_verbatim_ratio = 1.0,
                cleared_for_insertion = 1
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask ApplyLicenseGateAsync(
        AppInitializationOptions options,
        long anchorId,
        JsonElement gate)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_source_license
            SET reuse_policy = $reuse_policy,
                license_state = $license_state,
                max_verbatim_ratio = $max_verbatim_ratio,
                cleared_for_insertion = $cleared_for_insertion
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$reuse_policy", gate.GetProperty("reuse_policy").GetString() ?? ReferenceCorpusReusePolicies.ReferenceOnly);
        command.Parameters.AddWithValue("$license_state", gate.GetProperty("license_state").GetString() ?? ReferenceCorpusLicenseStates.Unknown);
        command.Parameters.AddWithValue("$max_verbatim_ratio", gate.GetProperty("max_verbatim_ratio").GetDouble());
        command.Parameters.AddWithValue("$cleared_for_insertion", gate.GetProperty("cleared_for_insertion").GetBoolean() ? 1 : 0);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask TightenAdaptedOnlyGateAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_source_license
            SET reuse_policy = 'adapted_only',
                license_state = 'authorized',
                max_verbatim_ratio = 0.1,
                cleared_for_insertion = 1
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask<bool> BlueprintBeatPieceExistsAsync(
        AppInitializationOptions options,
        string beatId,
        string nodeId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_blueprint_beat_pieces
            WHERE beat_id = $beat_id
              AND node_id = $node_id;
            """;
        command.Parameters.AddWithValue("$beat_id", beatId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async ValueTask<SqliteConnection> OpenReferenceConnectionAsync(AppInitializationOptions options)
    {
        var databasePath = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static JsonDocument LoadCorpusDrivenWritingFixture(string fileName)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            fileName);
        return JsonDocument.Parse(File.ReadAllText(fixturePath));
    }

    private static IReadOnlyList<CharacterStateSnapshotPayload> ReadCharacterSnapshots(JsonElement snapshots)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<CharacterStateSnapshotPayload>>(
            snapshots.GetRawText(),
            GoldenJsonOptions) ?? [];
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement values)
    {
        return values.EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(JsonElement values)
    {
        return values.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString() ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static JsonObject NormalizeDraftForGolden(ReferenceCorpusInsertionDraftPayload draft)
    {
        var nodeAliases = AliasMap(draft.Blueprint.Beats
            .SelectMany(beat => beat.NodeIds)
            .Concat(draft.Pieces.Select(piece => piece.NodeId)));
        var beatAliases = AliasMap(draft.Blueprint.Beats.Select(beat => beat.BeatId));
        var pieceAliases = AliasMap(draft.Pieces.Select(piece => piece.PieceId));
        var candidateAliases = AliasMap(draft.Pieces.Select(piece => piece.CandidateId));
        var libraryAliases = AliasMap(draft.QueryContext.Scope.LibraryIds.Concat(draft.Pieces.Select(piece => piece.LibraryId)));

        return new JsonObject
        {
            ["query_context"] = new JsonObject
            {
                ["scene_type"] = draft.QueryContext.SceneType,
                ["emotion_target"] = draft.QueryContext.EmotionTarget,
                ["pacing_target"] = draft.QueryContext.PacingTarget,
                ["narrative_position"] = draft.QueryContext.NarrativePosition,
                ["commercial_mechanic"] = draft.QueryContext.CommercialMechanic,
                ["character_states"] = StringArray(draft.QueryContext.CharacterStates),
                ["required_narrative_functions"] = StringArray(draft.QueryContext.RequiredNarrativeFunctions),
                ["chapter_context"] = new JsonObject
                {
                    ["chapter_number"] = draft.QueryContext.ChapterContext.ChapterNumber,
                    ["current_draft_text"] = NormalizeLineEndings(draft.QueryContext.ChapterContext.CurrentDraftText ?? string.Empty),
                    ["insertion_offset"] = draft.QueryContext.ChapterContext.InsertionOffset,
                    ["previous_chapter_summary"] = draft.QueryContext.ChapterContext.PreviousChapterSummary,
                    ["character_snapshots"] = CharacterSnapshotsArray(draft.QueryContext.ChapterContext.CharacterSnapshots)
                },
                ["scope"] = new JsonObject
                {
                    ["library_ids"] = StringArray(draft.QueryContext.Scope.LibraryIds.Select(id => libraryAliases[id])),
                    ["reuse_policies"] = StringArray(draft.QueryContext.Scope.ReusePolicies),
                    ["include_anchor_ids_count"] = draft.QueryContext.Scope.IncludeAnchorIds.Count,
                    ["exclude_anchor_ids_count"] = draft.QueryContext.Scope.ExcludeAnchorIds.Count
                }
            },
            ["blueprint"] = new JsonObject
            {
                ["strategy"] = draft.Blueprint.Strategy,
                ["beats"] = BlueprintBeatsArray(draft.Blueprint.Beats, beatAliases, nodeAliases)
            },
            ["pieces"] = PiecesArray(draft.Pieces, pieceAliases, beatAliases, candidateAliases, nodeAliases, libraryAliases),
            ["slot_replacements"] = SlotReplacementsArray(draft.SlotReplacements),
            ["assembled_text"] = NormalizeLineEndings(draft.AssembledText),
            ["chapter_text_after_insertion"] = NormalizeLineEndings(draft.ChapterTextAfterInsertion),
            ["ready_for_insertion"] = draft.ReadyForInsertion,
            ["gate"] = GateObject(draft.Gate, pieceAliases, nodeAliases)
        };
    }

    private static IReadOnlyDictionary<string, string> AliasMap(IEnumerable<string> values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
        {
            result[value] = $"item-{result.Count + 1}";
        }

        return result;
    }

    private static JsonArray BlueprintBeatsArray(
        IReadOnlyList<ReferenceCorpusInsertionBlueprintBeatPayload> beats,
        IReadOnlyDictionary<string, string> beatAliases,
        IReadOnlyDictionary<string, string> nodeAliases)
    {
        var result = new JsonArray();
        foreach (var beat in beats)
        {
            result.Add(new JsonObject
            {
                ["beat_alias"] = beatAliases[beat.BeatId],
                ["beat_index"] = beat.BeatIndex,
                ["role_in_beat"] = beat.RoleInBeat,
                ["narrative_function"] = beat.NarrativeFunction,
                ["node_aliases"] = StringArray(beat.NodeIds.Select(nodeId => nodeAliases[nodeId]))
            });
        }

        return result;
    }

    private static JsonArray PiecesArray(
        IReadOnlyList<ReferenceCorpusInsertionPiecePayload> pieces,
        IReadOnlyDictionary<string, string> pieceAliases,
        IReadOnlyDictionary<string, string> beatAliases,
        IReadOnlyDictionary<string, string> candidateAliases,
        IReadOnlyDictionary<string, string> nodeAliases,
        IReadOnlyDictionary<string, string> libraryAliases)
    {
        var result = new JsonArray();
        foreach (var piece in pieces)
        {
            result.Add(new JsonObject
            {
                ["piece_alias"] = pieceAliases[piece.PieceId],
                ["beat_alias"] = beatAliases[piece.BeatId],
                ["candidate_alias"] = candidateAliases[piece.CandidateId],
                ["node_alias"] = nodeAliases[piece.NodeId],
                ["library_alias"] = libraryAliases[piece.LibraryId],
                ["text_hash"] = piece.SourceTextHash,
                ["reuse_policy"] = piece.ReusePolicy,
                ["license_state"] = piece.LicenseState,
                ["output_text"] = NormalizeLineEndings(piece.OutputText),
                ["preserved_text_hash"] = piece.PreservedTextHash,
                ["preserved_hash_matches"] = piece.PreservedHashMatches,
                ["slot_replacements"] = SlotReplacementsArray(piece.SlotReplacements)
            });
        }

        return result;
    }

    private static JsonObject GateObject(
        ReferenceCorpusInsertionGatePayload gate,
        IReadOnlyDictionary<string, string> pieceAliases,
        IReadOnlyDictionary<string, string> nodeAliases)
    {
        var pieces = new JsonArray();
        foreach (var piece in gate.Pieces)
        {
            pieces.Add(new JsonObject
            {
                ["piece_alias"] = pieceAliases[piece.PieceId],
                ["node_alias"] = nodeAliases[piece.NodeId],
                ["should_block"] = piece.ShouldBlock,
                ["four_gram_containment_ratio"] = piece.FourGramContainmentRatio,
                ["longest_common_substring_ratio"] = piece.LongestCommonSubstringRatio,
                ["violations"] = GateViolationsArray(piece.Violations)
            });
        }

        return new JsonObject
        {
            ["passed"] = gate.Passed,
            ["status"] = gate.Status,
            ["errors"] = StringArray(gate.Errors),
            ["pieces"] = pieces
        };
    }

    private static JsonArray GateViolationsArray(IReadOnlyList<ReferenceCorpusInsertionGateViolationPayload> violations)
    {
        var result = new JsonArray();
        foreach (var violation in violations)
        {
            result.Add(new JsonObject
            {
                ["metric"] = violation.Metric,
                ["actual"] = violation.Actual,
                ["threshold"] = violation.Threshold
            });
        }

        return result;
    }

    private static JsonArray SlotReplacementsArray(IReadOnlyList<ReferenceCorpusSlotReplacementPayload> replacements)
    {
        var result = new JsonArray();
        foreach (var replacement in replacements)
        {
            result.Add(new JsonObject
            {
                ["slot_name"] = replacement.SlotName,
                ["source_value"] = replacement.SourceValue,
                ["replacement_value"] = replacement.ReplacementValue,
                ["source_start"] = replacement.SourceStart,
                ["source_end"] = replacement.SourceEnd,
                ["output_start"] = replacement.OutputStart,
                ["output_end"] = replacement.OutputEnd
            });
        }

        return result;
    }

    private static JsonArray CharacterSnapshotsArray(IReadOnlyList<CharacterStateSnapshotPayload> snapshots)
    {
        var result = new JsonArray();
        foreach (var snapshot in snapshots)
        {
            result.Add(new JsonObject
            {
                ["character"] = snapshot.Character,
                ["state"] = snapshot.State,
                ["allowed_knowledge"] = StringArray(snapshot.AllowedKnowledge),
                ["forbidden_knowledge"] = StringArray(snapshot.ForbiddenKnowledge)
            });
        }

        return result;
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private sealed class StaticEmbeddingConfigurationService : IEmbeddingConfigurationService
    {
        private readonly EmbeddingRequestOptions _options;

        public StaticEmbeddingConfigurationService(EmbeddingRequestOptions options)
        {
            _options = options;
        }

        public ValueTask<EmbeddingRequestOptions?> GetActiveEmbeddingOptionsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<EmbeddingRequestOptions?>(_options);
        }
    }
}
