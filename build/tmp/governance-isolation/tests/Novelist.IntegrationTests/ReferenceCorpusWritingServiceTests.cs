using System.Security.Cryptography;
using System.Text;
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

        Assert.True(
            result.ReadyForInsertion,
            $"gate={result.Gate.Status}:{string.Join("|", result.Gate.Errors)}; audit={result.Audit.Status}:{string.Join("|", result.Audit.Errors)}; assembled={result.AssembledText}");
        Assert.True(result.Gate.Passed, string.Join("|", result.Gate.Errors));
        Assert.True(result.Audit.Passed, string.Join("|", result.Audit.Errors));
        Assert.Equal("passed", result.Audit.Status);
        Assert.Empty(result.Audit.Errors);
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
        Assert.NotEmpty(piece.PreservedSpans);
        Assert.All(piece.PreservedSpans, span =>
        {
            Assert.False(string.IsNullOrWhiteSpace(span.SpanId));
            Assert.True(span.Matches);
            Assert.Equal(span.SourceEnd - span.SourceStart, span.OutputEnd - span.OutputStart);
            Assert.False(string.IsNullOrWhiteSpace(span.SourceTextHash));
            Assert.Equal(span.SourceTextHash, span.OutputTextHash);
        });
        Assert.Contains(piece.PreservedSpans, span =>
            span.SourceStart == 1 &&
            span.SourceEnd == "她没有立刻开口，只把钥匙扣在掌心。".Length &&
            span.OutputStart == 2 &&
            span.OutputEnd == piece.OutputText.Length);
        var auditPiece = Assert.Single(result.Audit.Pieces);
        Assert.True(auditPiece.Passed);
        Assert.Equal(piece.PieceId, auditPiece.PieceId);
        Assert.Equal(piece.NodeId, auditPiece.NodeId);
        Assert.Equal(piece.PreservedSpans.Count, auditPiece.PreservedSpanCount);
        Assert.Equal(0, auditPiece.MismatchedSpanCount);
        Assert.Empty(auditPiece.Violations);
        Assert.True(await BlueprintBeatPieceExistsAsync(options, result.Blueprint.Beats[0].BeatId, piece.NodeId));
    }

    [Fact]
    public async Task GenerateInsertionDraftAppliesTypedSlotsWithoutReplacingProtectedQuotedText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料槽位类型测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在黑塔门前。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        const string sourceSentence = "她在旧市集门口没有立刻开口，只叫了一声师兄，把钥匙扣在掌心，《旧市集门口师兄钥匙案》没有改。";
        const string protectedTitle = "《旧市集门口师兄钥匙案》";
        var sourcePath = CreateSourceFile(
            "typed-slots-protected.md",
            $$"""
            # 第一章 槽位

            {{sourceSentence}}
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "槽位保护语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        const string nodeId = "node-typed-slots-protected-s1";
        await InsertReferenceTextNodeAsync(options, anchor.AnchorId, nodeId, sourceSentence);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-typed-slots-protected",
            QueryContextHash: "query-typed-slots-protected",
            Strategy: "selected_slot_fixture",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-typed-slots-protected-beat",
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: [nodeId])
            ]);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，秦砚压住怒意，没有立刻开口，只叫队长，把铜令扣在掌心。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "黑塔门前，秦砚需要压住情绪交出铜令。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["队长在场"], ["铜令真正用途"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>
                {
                    ["character:她"] = "秦砚",
                    ["place:旧市集门口"] = "黑塔门前",
                    ["honorific:师兄"] = "队长",
                    ["plot_object:钥匙"] = "铜令"
                },
                SelectedBlueprint: selectedBlueprint),
            CancellationToken.None);

        Assert.True(
            result.ReadyForInsertion,
            $"gate={result.Gate.Status}:{string.Join("|", result.Gate.Errors)}; audit={result.Audit.Status}:{string.Join("|", result.Audit.Errors)}; assembled={result.AssembledText}");
        Assert.True(result.Gate.Passed, string.Join("|", result.Gate.Errors));
        Assert.True(result.Audit.Passed, string.Join("|", result.Audit.Errors));
        Assert.DoesNotContain(result.Audit.Errors, error => error.StartsWith("slot_replacement_unsafe_range:", StringComparison.Ordinal));
        var piece = Assert.Single(result.Pieces);
        Assert.Equal(
            "秦砚在黑塔门前没有立刻开口，只叫了一声队长，把铜令扣在掌心，《旧市集门口师兄钥匙案》没有改。",
            piece.OutputText);
        Assert.Contains(protectedTitle, piece.OutputText, StringComparison.Ordinal);
        Assert.DoesNotContain("《黑塔门前队长铜令案》", piece.OutputText, StringComparison.Ordinal);
        Assert.Contains(piece.SlotReplacements, replacement =>
            replacement.SlotName == "character" &&
            replacement.SourceValue == "她" &&
            replacement.ReplacementValue == "秦砚");
        Assert.Contains(piece.SlotReplacements, replacement =>
            replacement.SlotName == "place" &&
            replacement.SourceValue == "旧市集门口" &&
            replacement.ReplacementValue == "黑塔门前");
        Assert.Contains(piece.SlotReplacements, replacement =>
            replacement.SlotName == "honorific" &&
            replacement.SourceValue == "师兄" &&
            replacement.ReplacementValue == "队长");
        Assert.Contains(piece.SlotReplacements, replacement =>
            replacement.SlotName == "plot_object" &&
            replacement.SourceValue == "钥匙" &&
            replacement.ReplacementValue == "铜令");
        Assert.Equal(
            4,
            piece.SlotReplacements
                .Select(replacement => replacement.SlotName)
                .Distinct(StringComparer.Ordinal)
                .Count());

        var protectedStart = sourceSentence.IndexOf(protectedTitle, StringComparison.Ordinal);
        var protectedEnd = protectedStart + protectedTitle.Length;
        Assert.DoesNotContain(piece.SlotReplacements, replacement =>
            replacement.SourceStart < protectedEnd && replacement.SourceEnd > protectedStart);
        Assert.Contains(piece.PreservedSpans, span =>
            piece.OutputText[span.OutputStart..span.OutputEnd].Contains(protectedTitle, StringComparison.Ordinal));
        Assert.True(piece.PreservedHashMatches);
        Assert.All(piece.PreservedSpans, span => Assert.True(span.Matches));
        var auditPiece = Assert.Single(result.Audit.Pieces);
        Assert.True(auditPiece.Passed);
        Assert.Empty(auditPiece.Violations);
        Assert.Equal(currentDraft + Environment.NewLine + result.AssembledText, result.ChapterTextAfterInsertion);
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksSlotReplacementInsideLockedProtectedText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料锁定槽位负例测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在黑塔门前。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        const string sourceSentence = "她在旧市集门口没有立刻开口，《旧市集门口钥匙案》没有改。";
        var sourcePath = CreateSourceFile(
            "locked-slot-negative.md",
            $$"""
            # 第一章 锁定负例

            {{sourceSentence}}
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "锁定负例语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        const string nodeId = "node-locked-slot-negative-s1";
        await InsertReferenceTextNodeAsync(options, anchor.AnchorId, nodeId, sourceSentence);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            slots: new LockedRangeReplacingSlotResolver("旧市集门口", "黑塔门前"));
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-locked-slot-negative",
            QueryContextHash: "query-locked-slot-negative",
            Strategy: "selected_slot_negative_fixture",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-locked-slot-negative-beat",
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: [nodeId])
            ]);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，秦砚压住怒意，没有立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "黑塔门前，秦砚需要压住情绪。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", [], [])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>(),
                SelectedBlueprint: selectedBlueprint),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed, string.Join("|", result.Gate.Errors));
        Assert.False(result.Audit.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var piece = Assert.Single(result.Pieces);
        Assert.Contains("《黑塔门前钥匙案》", piece.OutputText, StringComparison.Ordinal);
        var auditPiece = Assert.Single(result.Audit.Pieces);
        Assert.Contains(auditPiece.Violations, violation => violation.Code == "slot_replacement_locked_range");
        Assert.Contains(auditPiece.Violations, violation => violation.Code == "locked_span_hash_mismatch");
        Assert.Contains(result.Audit.Errors, error => error.StartsWith("slot_replacement_locked_range:", StringComparison.Ordinal));
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
    public async Task GenerateInsertionDraftBlocksWhenDraftAuditFindsPreservedSpanMismatch()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "audit-doorway.md",
            """
            # 第一章

            她没有立刻开口。
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            textAssembler: new MutatingPreservedSpanTextAssembler());

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "门外有人靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["门外有人靠近"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal("blocked", result.Audit.Status);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var auditPiece = Assert.Single(result.Audit.Pieces);
        Assert.False(auditPiece.Passed);
        Assert.Equal(1, auditPiece.PreservedSpanCount);
        Assert.Equal(1, auditPiece.MismatchedSpanCount);
        Assert.Contains(result.Audit.Errors, error => error.StartsWith("preserved_text_hash_mismatch:", StringComparison.Ordinal));
        Assert.Contains(result.Audit.Errors, error => error.StartsWith("preserved_span_hash_mismatch:", StringComparison.Ordinal));
        Assert.Contains(auditPiece.Violations, violation => violation.Code == "preserved_span_hash_mismatch");
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenAssemblerDropsSelectedBlueprintPiece()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料缺拍审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "audit-missing-piece.md",
            """
            # 第一章

            她没有立刻开口。

            雨声贴着门缝往里挤。
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "缺拍审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            blueprints: new FirstTwoSourceBlueprintAssembler(),
            textAssembler: new DroppingLastPieceTextAssembler());

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口，雨声逼近。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "门外有人靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["门外有人靠近"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal("blocked", result.Audit.Status);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        Assert.Equal(2, result.Blueprint.Beats.Count);
        Assert.Single(result.Pieces);
        Assert.Equal(2, result.Audit.Pieces.Count);
        Assert.Contains(result.Audit.Errors, error => error.StartsWith("piece_missing:", StringComparison.Ordinal));
        var missingAuditPiece = Assert.Single(result.Audit.Pieces, piece => piece.Violations.Any(violation => violation.Code == "piece_missing"));
        Assert.False(missingAuditPiece.Passed);
        Assert.Equal(0, missingAuditPiece.PreservedSpanCount);
        Assert.Equal(0, missingAuditPiece.MismatchedSpanCount);
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenExplicitSlotReplacementConsumesWholeSourceSentence()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料槽位审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        const string sourceSentence = "她没有立刻开口。";
        var sourcePath = CreateSourceFile(
            "audit-whole-slot.md",
            $$"""
            # 第一章

            {{sourceSentence}}
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "整句槽位审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "门外有人靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["门外有人靠近"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>
                {
                    [sourceSentence] = "错误正文直接塞进章节。"
                }),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var auditPiece = Assert.Single(result.Audit.Pieces);
        Assert.Contains(result.Audit.Errors, error => error.StartsWith("slot_replacement_unsafe_range:", StringComparison.Ordinal));
        Assert.Contains(auditPiece.Violations, violation => violation.Code == "slot_replacement_unsafe_range");
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenAssembledTextContainsUnauditedOutput()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料正文包络审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "audit-envelope.md",
            """
            # 第一章

            她没有立刻开口。
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "正文包络审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            textAssembler: new AppendingUnauditedTextAssembler());

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "门外有人靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["门外有人靠近"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var auditPiece = Assert.Single(result.Audit.Pieces);
        Assert.Contains(result.Audit.Errors, error => error.StartsWith("assembled_text_untracked_output:", StringComparison.Ordinal));
        Assert.Contains(auditPiece.Violations, violation => violation.Code == "assembled_text_untracked_output");
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenPieceOutputContainsUnauditedRange()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料片段包络审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "audit-piece-untracked-output.md",
            """
            # 第一章

            她没有立刻开口。
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "片段包络审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            textAssembler: new AppendingUnauditedRangeInsidePieceTextAssembler());

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "门外有人靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["门外有人靠近"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>()),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var auditPiece = Assert.Single(result.Audit.Pieces);
        Assert.Contains(result.Audit.Errors, error => error.StartsWith("piece_output_untracked_range:", StringComparison.Ordinal));
        Assert.Contains(auditPiece.Violations, violation => violation.Code == "piece_output_untracked_range");
    }

    [Fact]
    public async Task GenerateInsertionDraftAllowsAuditedTransitionBetweenSelectedBlueprintPieces()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料过渡审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "audit-transition.md",
            """
            # 第一章

            她没有立刻开口。

            雨声贴着门缝往里挤。
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "过渡审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var transitionText = "门外的雨声又近了一寸。";
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            blueprints: new FirstTwoSourceBlueprintAssembler(),
            transitionResolver: new FixedBridgeTransitionResolver(transitionText));

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口，雨声逼近。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "门外有人靠近。",
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
        Assert.True(result.Audit.Passed);
        Assert.Equal(2, result.Pieces.Count);
        var transition = Assert.Single(result.Transitions);
        Assert.Equal(ReferenceCorpusTransitionDecisions.InsertTransition, transition.Decision);
        Assert.Equal("bridge_sentence", transition.Strategy);
        Assert.Equal(transitionText, transition.Text);
        Assert.True(transition.Approved);
        Assert.Equal(result.Pieces[0].PieceId, transition.AfterPieceId);
        Assert.Equal(result.Pieces[1].PieceId, transition.BeforePieceId);
        Assert.Equal(transitionText, result.AssembledText[transition.OutputStart..transition.OutputEnd]);
        var auditTransition = Assert.Single(result.Audit.Transitions);
        Assert.True(auditTransition.Passed);
        Assert.Equal(transition.TransitionId, auditTransition.TransitionId);
        Assert.Equal(transition.GapId, auditTransition.GapId);
        Assert.Empty(auditTransition.Violations);
        Assert.Contains(
            result.Pieces[0].OutputText + Environment.NewLine + transitionText + Environment.NewLine + result.Pieces[1].OutputText,
            result.AssembledText,
            StringComparison.Ordinal);
        Assert.Equal(currentDraft + Environment.NewLine + result.AssembledText, result.ChapterTextAfterInsertion);
    }

    [Fact]
    public async Task GenerateInsertionDraftAllowsAuditedDirectJoinBetweenSelectedBlueprintPieces()
    {
        var (result, currentDraft) = await GenerateTwoPieceTransitionAuditDraftAsync(textAssembler: null);

        Assert.True(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.True(result.Audit.Passed);
        Assert.Equal(2, result.Pieces.Count);
        var transition = Assert.Single(result.Transitions);
        Assert.Equal(ReferenceCorpusTransitionDecisions.DirectJoin, transition.Decision);
        Assert.Equal("direct_join", transition.Strategy);
        Assert.Equal(string.Empty, transition.Text);
        Assert.Equal(transition.OutputStart, transition.OutputEnd);
        var auditTransition = Assert.Single(result.Audit.Transitions);
        Assert.True(auditTransition.Passed);
        Assert.Empty(auditTransition.Violations);
        Assert.Equal(
            string.Join(Environment.NewLine, result.Pieces.Select(piece => piece.OutputText.Trim())),
            result.AssembledText);
        Assert.Equal(currentDraft + Environment.NewLine + result.AssembledText, result.ChapterTextAfterInsertion);
    }

    [Fact]
    public async Task GenerateInsertionDraftUsesDefaultTransitionResolverToBridgePressureIntoWithheldAnswer()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("默认转场桥接测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-default-transition-bridge-blueprint",
            QueryContextHash: "default-transition-bridge-query",
            Strategy: "selected_default_transition_bridge_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-default-transition-bridge-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-doorway-s1"]),
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-default-transition-bridge-beat-2",
                    BeatIndex: 1,
                    RoleInBeat: "supporting_source_sentence",
                    NarrativeFunction: "withhold_answer",
                    NodeIds: ["node-workspace-market-s1"])
            ]);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint),
            CancellationToken.None);

        Assert.True(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.True(result.Audit.Passed);
        var transition = Assert.Single(result.Transitions);
        Assert.Equal(ReferenceCorpusTransitionDecisions.InsertTransition, transition.Decision);
        Assert.Equal("heuristic_bridge_sentence", transition.Strategy);
        Assert.Equal("沉默在两人之间又压低了一寸。", transition.Text);
        Assert.True(transition.Approved);
        Assert.Equal(transition.Text, result.AssembledText[transition.OutputStart..transition.OutputEnd]);
        var auditTransition = Assert.Single(result.Audit.Transitions);
        Assert.True(auditTransition.Passed);
        Assert.Empty(auditTransition.Violations);
        Assert.Contains(
            result.Pieces[0].OutputText + Environment.NewLine + transition.Text + Environment.NewLine + result.Pieces[1].OutputText,
            result.AssembledText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenDefaultTransitionResolverRequiresDuplicateSourceReplacement()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("默认转场重复源阻断测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-default-transition-duplicate-blueprint",
            QueryContextHash: "default-transition-duplicate-query",
            Strategy: "selected_default_transition_duplicate_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-default-transition-duplicate-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-doorway-s1"]),
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-default-transition-duplicate-beat-2",
                    BeatIndex: 1,
                    RoleInBeat: "supporting_source_sentence",
                    NarrativeFunction: "withhold_answer",
                    NodeIds: ["node-project-doorway-s1"])
            ]);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var transition = Assert.Single(result.Transitions);
        Assert.Equal(ReferenceCorpusTransitionDecisions.ReplacePiece, transition.Decision);
        Assert.Equal("replace_piece", transition.Strategy);
        Assert.False(transition.Approved);
        Assert.Equal(result.Pieces[1].PieceId, transition.ReplacementPieceId);
        Assert.Null(transition.ReplacementNodeId);
        var auditTransition = Assert.Single(result.Audit.Transitions);
        Assert.False(auditTransition.Passed);
        Assert.Contains(auditTransition.Violations, violation =>
            violation.Code == "transition_piece_replacement_required" &&
            violation.TransitionId == transition.TransitionId);
    }

    [Theory]
    [InlineData(TransitionAuditFailureMode.HashMismatch, "transition_text_hash_mismatch")]
    [InlineData(TransitionAuditFailureMode.UnknownPiece, "transition_piece_reference_invalid")]
    [InlineData(TransitionAuditFailureMode.OutputRangeMismatch, "transition_output_range_mismatch")]
    [InlineData(TransitionAuditFailureMode.ReplacePiece, "transition_piece_replacement_required")]
    public async Task GenerateInsertionDraftBlocksWhenTransitionAuditFails(
        TransitionAuditFailureMode failureMode,
        string expectedViolationCode)
    {
        var (result, currentDraft) = await GenerateTwoPieceTransitionAuditDraftAsync(
            new MaliciousTransitionTextAssembler(failureMode));

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal("blocked", result.Audit.Status);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var auditTransition = Assert.Single(
            result.Audit.Transitions,
            transition => transition.Violations.Any(violation => violation.Code == expectedViolationCode));
        Assert.False(auditTransition.Passed);
        Assert.Contains(result.Audit.Errors, error => error.StartsWith(expectedViolationCode + ":", StringComparison.Ordinal));
        Assert.Contains(auditTransition.Violations, violation =>
            violation.Code == expectedViolationCode &&
            violation.TransitionId == auditTransition.TransitionId);
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenTransitionReferencesNonAdjacentPieces()
    {
        var (result, currentDraft) = await GenerateTransitionAuditDraftAsync(
            textAssembler: new MaliciousTransitionTextAssembler(TransitionAuditFailureMode.NonAdjacentPieces),
            blueprintAssembler: new FirstThreeSourceBlueprintAssembler(),
            sourceParagraphs:
            [
                "她没有立刻开口。",
                "雨声贴着门缝往里挤。",
                "钥匙在掌心硌出一点冷意。"
            ]);

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var auditTransition = Assert.Single(
            result.Audit.Transitions,
            transition => transition.Violations.Any(violation => violation.Code == "transition_piece_pair_not_adjacent"));
        Assert.False(auditTransition.Passed);
        Assert.Contains(auditTransition.Violations, violation =>
            violation.Code == "transition_piece_pair_not_adjacent" &&
            violation.TransitionId == auditTransition.TransitionId);
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenAdjacentPiecesHaveNoTransitionDecision()
    {
        var (result, currentDraft) = await GenerateTwoPieceTransitionAuditDraftAsync(new MissingTransitionDecisionTextAssembler());

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var auditTransition = Assert.Single(result.Audit.Transitions);
        Assert.False(auditTransition.Passed);
        Assert.Equal("missing_transition_decision", auditTransition.Violations.Single().Code);
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksWhenTransitionGapIdDoesNotMatchAdjacentPair()
    {
        var (result, currentDraft) = await GenerateTwoPieceTransitionAuditDraftAsync(
            new MaliciousTransitionTextAssembler(TransitionAuditFailureMode.WrongGapId));

        Assert.False(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.False(result.Audit.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var auditTransition = Assert.Single(result.Audit.Transitions);
        Assert.False(auditTransition.Passed);
        Assert.Contains(auditTransition.Violations, violation =>
            violation.Code == "transition_gap_id_mismatch" &&
            violation.TransitionId == auditTransition.TransitionId);
    }

    [Fact]
    public async Task GenerateInsertionDraftBlocksSelectedBlueprintWhenSourceIsNotClearedForInsertion()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料授权闸门测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "not-cleared-doorway.md",
            """
            # 第一章

            他没有立刻开口，只把钥匙扣在掌心。
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "未清权语料",
                null,
                sourcePath,
                "markdown",
                "user_provided"),
            CancellationToken.None);
        await SetInsertionClearanceAsync(options, anchor.AnchorId, clearedForInsertion: false);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var nodeId = await ReadFirstSentenceNodeIdAsync(options, anchor.AnchorId);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-not-cleared-blueprint",
            QueryContextHash: "manual-test-query",
            Strategy: "manual_selected_source",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-not-cleared-beat",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: [nodeId])
            ]);
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
                SlotValues: new Dictionary<string, string>
                {
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint),
            CancellationToken.None);

        Assert.False(result.ReadyForInsertion);
        Assert.False(result.Gate.Passed);
        Assert.Equal(currentDraft, result.ChapterTextAfterInsertion);
        var piece = Assert.Single(result.Pieces);
        Assert.Equal(nodeId, piece.NodeId);
        Assert.Equal(ReferenceCorpusLicenseStates.Authorized, piece.LicenseState);
        Assert.Equal(ReferenceCorpusReusePolicies.AdaptedOnly, piece.ReusePolicy);
        Assert.Contains($"license_not_cleared:{nodeId}", result.Gate.Errors);
        Assert.DoesNotContain(result.Gate.Pieces, gatePiece => gatePiece.ShouldBlock);
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

    [Fact]
    public async Task GenerateCrossLibraryBlueprintAndDraftCandidatesMatchesGoldenJson()
    {
        using var document = LoadCorpusDrivenWritingFixture("m15-cross-library-closed-loop-golden.json");
        var fixture = document.RootElement.GetProperty("fixtures")[0];
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novelSpec = fixture.GetProperty("novel");
        var chapterSpec = fixture.GetProperty("current_chapter");
        var requestSpec = fixture.GetProperty("request");

        var novel = await novels.CreateNovelAsync(
            new CreateNovelPayload(
                novelSpec.GetProperty("title").GetString() ?? "跨库语料 golden",
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
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id, fixture.GetProperty("corpus"));
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryGoldenBlueprintCandidatesPayload(
            novel.Id,
            chapter.ChapterNumber,
            chapterSpec,
            requestSpec);

        var firstRound = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);
        var selectedCrossLibraryBlueprint = firstRound.Candidates
            .First(candidate => candidate.SourceDistribution
                .Select(source => source.LibraryId)
                .Distinct(StringComparer.Ordinal)
                .Count() >= 2)
            .Blueprint;
        var enabledDraft = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                request.NaturalLanguageGoal,
                request.ChapterContext,
                request.Scope,
                ReadStringDictionary(requestSpec.GetProperty("slot_values")),
                selectedCrossLibraryBlueprint),
            CancellationToken.None);
        var rejected = firstRound.Candidates[0];
        var feedback = BuildCrossLibraryFeedbackPayload(
            fixture.GetProperty("feedback"),
            rejected,
            novel.Id);
        var secondRound = await service.GenerateBlueprintCandidatesAsync(
            request with { Feedback = feedback },
            CancellationToken.None);
        var draftCandidates = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                request.NaturalLanguageGoal,
                request.ChapterContext,
                request.Scope,
                ReadStringDictionary(requestSpec.GetProperty("slot_values")),
                selectedCrossLibraryBlueprint,
                requestSpec.GetProperty("requested_draft_count").GetInt32()),
            CancellationToken.None);

        var disabledLibraryId = RenderFixtureId(
            fixture.GetProperty("disabled_library_id").GetString() ?? string.Empty,
            novel.Id);
        await SetLibraryMembersEnabledAsync(options, disabledLibraryId, enabled: false);
        var disabledRound = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);
        var disabledBlueprint = disabledRound.Candidates[0].Blueprint;
        var disabledDraft = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                request.NaturalLanguageGoal,
                request.ChapterContext,
                request.Scope,
                ReadStringDictionary(requestSpec.GetProperty("slot_values")),
                disabledBlueprint),
            CancellationToken.None);

        Assert.True(firstRound.Candidates.Count >= 2);
        Assert.True(enabledDraft.ReadyForInsertion);
        Assert.Contains(enabledDraft.Pieces, piece => piece.LibraryId == disabledLibraryId);
        Assert.Contains(enabledDraft.Pieces, piece => piece.LibraryId != disabledLibraryId);
        Assert.True(secondRound.FeedbackApplied);
        Assert.All(secondRound.Candidates, candidate =>
            Assert.DoesNotContain(candidate.SourceDistribution, source => source.LibraryId == disabledLibraryId));
        Assert.NotEmpty(draftCandidates.Candidates);
        Assert.All(draftCandidates.Candidates, candidate => Assert.True(candidate.Draft.Gate.Passed));
        Assert.All(disabledDraft.Pieces, piece => Assert.NotEqual(disabledLibraryId, piece.LibraryId));
        Assert.NotEqual(enabledDraft.AssembledText, disabledDraft.AssembledText);

        var expected = JsonNode.Parse(fixture.GetProperty("expected_closed_loop").GetRawText());
        var actual = NormalizeCrossLibraryClosedLoopForGolden(
            firstRound,
            secondRound,
            enabledDraft,
            disabledRound,
            disabledDraft,
            draftCandidates);
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            "M1 corpus-driven-writing cross-library golden mismatch." +
            Environment.NewLine +
            "Actual normalized closed loop:" +
            Environment.NewLine +
            actual.ToJsonString(GoldenJsonOptions));
    }

    [Fact]
    public async Task GenerateInsertionDraftUsesDefaultSessionScopeAcrossProjectAndWorkspaceLibraries()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("跨库语料闭环测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var blueprintAssembler = new TwoSourceBlueprintAssembler();
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            blueprints: blueprintAssembler);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                }),
            CancellationToken.None);

        Assert.True(result.ReadyForInsertion);
        Assert.True(result.Gate.Passed);
        Assert.Equal(2, result.Blueprint.Beats.Count);
        Assert.Equal(2, result.Pieces.Count);
        Assert.True(blueprintAssembler.SawMultipleLibraries);
        Assert.True(result.Pieces.Select(piece => piece.LibraryId).Distinct(StringComparer.Ordinal).Count() >= 2);
        Assert.True(result.Pieces.Select(piece => piece.AnchorId).Distinct().Count() >= 2);
        Assert.Contains(result.Pieces, piece => piece.LibraryId == $"project:{novel.Id}:default");
        Assert.Contains(result.Pieces, piece => piece.LibraryId == "global:workspace");
        Assert.Contains("秦砚没有立刻开口", result.AssembledText, StringComparison.Ordinal);
        Assert.Contains("秦砚没有立刻回头", result.AssembledText, StringComparison.Ordinal);
        foreach (var piece in result.Pieces)
        {
            Assert.True(piece.PreservedHashMatches);
            Assert.True(await BlueprintBeatPieceExistsAsync(options, piece.BeatId, piece.NodeId));
        }
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesSupportsFeedbackIterationAndSelectedBlueprintDraft()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("多蓝图语料闭环测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(novel.Id, chapter.ChapterNumber, currentDraft);

        var firstRound = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        Assert.True(firstRound.Candidates.Count >= 2);
        Assert.Contains(firstRound.Candidates, item => item.SourceDistribution.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).Count() >= 2);
        Assert.Contains(firstRound.Candidates, item => item.Blueprint.Strategy == "score_focus_m1");
        var firstNodeIds = firstRound.Candidates
            .SelectMany(candidate => candidate.Blueprint.Beats)
            .SelectMany(beat => beat.NodeIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("node-workspace-market-s1", firstNodeIds);
        var rejected = firstRound.Candidates[0];
        var rejectedNodes = rejected.Blueprint.Beats.SelectMany(beat => beat.NodeIds).ToArray();

        var secondRound = await service.GenerateBlueprintCandidatesAsync(request with
        {
            Feedback = new ReferenceCorpusBlueprintFeedbackPayload(
                RejectedBlueprintIds: [rejected.Blueprint.BlueprintId],
                RejectedNodeIds: rejectedNodes,
                AvoidLibraryIds: ["global:workspace"],
                AvoidAnchorIds: [202],
                ProblemTags: ["too_fast"],
                Notes: "prefer project corpus for this pass")
        }, CancellationToken.None);

        Assert.True(secondRound.FeedbackApplied);
        Assert.Contains("rejected_blueprints:1", secondRound.FeedbackSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(secondRound.Candidates.SelectMany(candidate => candidate.Blueprint.Beats).SelectMany(beat => beat.NodeIds), rejectedNodes.Contains);
        Assert.All(secondRound.Candidates, candidate =>
            Assert.DoesNotContain(candidate.SourceDistribution, source => source.LibraryId == "global:workspace"));
        Assert.All(secondRound.Candidates, candidate =>
            Assert.Contains("single_anchor_source", candidate.GapReasons));
        Assert.Equal("rhythm_slow_m1", secondRound.Candidates[0].Blueprint.Strategy);

        var selected = secondRound.Candidates[0].Blueprint;
        var draft = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: request.NaturalLanguageGoal,
                ChapterContext: request.ChapterContext,
                Scope: request.Scope,
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selected),
            CancellationToken.None);

        Assert.True(draft.ReadyForInsertion);
        Assert.True(draft.Gate.Passed);
        Assert.Equal(selected.BlueprintId, draft.Blueprint.BlueprintId);
        Assert.Equal(selected.Beats.Count, draft.Pieces.Count);
        Assert.All(draft.Pieces, piece =>
        {
            Assert.True(piece.PreservedHashMatches);
            Assert.Contains(piece.NodeId, selected.Beats.SelectMany(beat => beat.NodeIds));
        });
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesRejectedBlueprintIdAloneDoesNotRegenerateSameNodeSet()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("拒绝蓝图去重测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(novel.Id, chapter.ChapterNumber, currentDraft) with
        {
            RequestedCount = 3
        };

        var firstRound = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);
        var rejected = firstRound.Candidates[0];
        var rejectedNodeSet = BlueprintNodeSetKey(rejected.Blueprint);

        var secondRound = await service.GenerateBlueprintCandidatesAsync(request with
        {
            Feedback = new ReferenceCorpusBlueprintFeedbackPayload(
                RejectedBlueprintIds: [rejected.Blueprint.BlueprintId],
                RejectedNodeIds: [],
                AvoidLibraryIds: [],
                AvoidAnchorIds: [],
                ProblemTags: [],
                Notes: "这个方案不要")
        }, CancellationToken.None);

        Assert.True(secondRound.FeedbackApplied);
        Assert.Contains("rejected_blueprints:1", secondRound.FeedbackSummary, StringComparison.Ordinal);
        Assert.NotEmpty(secondRound.Candidates);
        Assert.DoesNotContain(secondRound.Candidates, candidate =>
            string.Equals(BlueprintNodeSetKey(candidate.Blueprint), rejectedNodeSet, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesPersistsBlueprintFeedbackForReuse()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("蓝图反馈持久化测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(novel.Id, chapter.ChapterNumber, currentDraft);
        var firstRound = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);
        var rejected = firstRound.Candidates[0];
        var rejectedNodeIds = rejected.Blueprint.Beats.SelectMany(beat => beat.NodeIds).ToArray();
        var rejectedLibraryIds = rejected.SourceDistribution.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).ToArray();
        var rejectedAnchorIds = rejected.SourceDistribution.Select(source => source.AnchorId).Distinct().ToArray();

        var feedback = new ReferenceCorpusBlueprintFeedbackPayload(
            RejectedBlueprintIds: [rejected.Blueprint.BlueprintId],
            RejectedNodeIds: rejectedNodeIds,
            AvoidLibraryIds: rejectedLibraryIds,
            AvoidAnchorIds: rejectedAnchorIds,
            ProblemTags: ["source_repetition", "too_fast"],
            Notes: "这一版来源和节奏都不合适");
        await service.GenerateBlueprintCandidatesAsync(request with { Feedback = feedback }, CancellationToken.None);
        await service.GenerateBlueprintCandidatesAsync(request with { Feedback = feedback }, CancellationToken.None);

        var records = await ReadReferenceUserFeedbackRowsAsync(options, novel.Id, rejected.Blueprint.BlueprintId);

        var record = Assert.Single(records);
        Assert.Equal(ReferenceFeedbackTargetTypes.Blueprint, record.TargetType);
        Assert.Equal(rejected.Blueprint.BlueprintId, record.TargetId);
        Assert.Equal(ReferenceFeedbackDecisions.Rejected, record.Decision);
        Assert.Equal("corpus_blueprint_feedback", record.Origin);
        Assert.Equal("这一版来源和节奏都不合适", record.Note);
        Assert.Contains("source_repetition", record.FeedbackTags);
        Assert.Contains("too_fast", record.FeedbackTags);
        Assert.Contains("rejected_blueprints:1", record.FeedbackTags);
        Assert.Contains("rejected_nodes:" + rejectedNodeIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), record.FeedbackTags);
        Assert.Contains("avoid_libraries:" + rejectedLibraryIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), record.FeedbackTags);
        Assert.Contains("avoid_anchors:" + rejectedAnchorIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), record.FeedbackTags);
        Assert.Equal(
            rejectedNodeIds.Length,
            record.FeedbackTags.Count(tag => tag.StartsWith("node_hash:", StringComparison.Ordinal)));
        Assert.Equal(
            rejectedLibraryIds.Length,
            record.FeedbackTags.Count(tag => tag.StartsWith("library_hash:", StringComparison.Ordinal)));
        Assert.Equal(
            rejectedAnchorIds.Length,
            record.FeedbackTags.Count(tag => tag.StartsWith("anchor:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesHistoricalFeedbackDownranksRejectedNodeSet()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("历史反馈重排测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(novel.Id, chapter.ChapterNumber, currentDraft);
        var firstRound = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);
        var rejected = firstRound.Candidates[0];
        var rejectedNodeSet = BlueprintNodeSetKey(rejected.Blueprint);
        var rejectedNodeIds = rejected.Blueprint.Beats.SelectMany(beat => beat.NodeIds).ToArray();
        var rejectedLibraryIds = rejected.SourceDistribution.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).ToArray();
        var rejectedAnchorIds = rejected.SourceDistribution.Select(source => source.AnchorId).Distinct().ToArray();

        await service.GenerateBlueprintCandidatesAsync(request with
        {
            Feedback = new ReferenceCorpusBlueprintFeedbackPayload(
                RejectedBlueprintIds: [rejected.Blueprint.BlueprintId],
                RejectedNodeIds: rejectedNodeIds,
                AvoidLibraryIds: rejectedLibraryIds,
                AvoidAnchorIds: rejectedAnchorIds,
                ProblemTags: ["too_fast"],
                Notes: "这组句子不要再优先给我")
        }, CancellationToken.None);

        var nextRound = await service.GenerateBlueprintCandidatesAsync(request with
        {
            Feedback = new ReferenceCorpusBlueprintFeedbackPayload(
                RejectedBlueprintIds: [],
                RejectedNodeIds: [],
                AvoidLibraryIds: [],
                AvoidAnchorIds: [],
                ProblemTags: [],
                Notes: null)
        }, CancellationToken.None);

        Assert.False(nextRound.FeedbackApplied);
        Assert.NotEmpty(nextRound.Candidates);
        Assert.NotEqual(rejectedNodeSet, BlueprintNodeSetKey(nextRound.Candidates[0].Blueprint));
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesSourceRepetitionFeedbackPrioritizesCrossSourceBlueprint()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("来源重复反馈测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        await AddSourceRepetitionSearchFixtureAsync(options);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(novel.Id, chapter.ChapterNumber, currentDraft) with
        {
            Feedback = new ReferenceCorpusBlueprintFeedbackPayload(
                RejectedBlueprintIds: [],
                RejectedNodeIds: [],
                AvoidLibraryIds: [],
                AvoidAnchorIds: [],
                ProblemTags: ["source_repetition"],
                Notes: "来源太重复，换几本参考")
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        Assert.True(result.FeedbackApplied);
        Assert.Contains("problems:source_repetition", result.FeedbackSummary, StringComparison.Ordinal);
        Assert.NotEmpty(result.Candidates);
        var firstCandidate = result.Candidates[0];
        Assert.Equal("source_repetition_diversity_m1", firstCandidate.Blueprint.Strategy);
        Assert.True(
            firstCandidate.SourceDistribution.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).Count() >= 2 ||
            firstCandidate.SourceDistribution.Select(source => source.AnchorId).Distinct().Count() >= 2,
            "source_repetition feedback should make the first regenerated blueprint cross library or cross anchor when alternatives exist.");
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesFeedbackFilterFallbackAddsDiagnosticGapReasons()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("反馈回退诊断测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(novel.Id, chapter.ChapterNumber, currentDraft) with
        {
            Feedback = new ReferenceCorpusBlueprintFeedbackPayload(
                RejectedBlueprintIds: [],
                RejectedNodeIds: [],
                AvoidLibraryIds: [],
                AvoidAnchorIds: [],
                ProblemTags: ["too_fast"],
                Notes: "节奏太快，但当前语料还没有 rhythm observation")
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        Assert.True(result.FeedbackApplied);
        Assert.NotEmpty(result.Candidates);
        Assert.Contains("fallback:feedback_filters_no_matches,fallback_to_base_filters", result.FeedbackSummary, StringComparison.Ordinal);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Contains("feedback_filters_no_matches", candidate.GapReasons);
            Assert.Contains("fallback_to_base_filters", candidate.GapReasons);
            Assert.Contains("fallback:feedback_filters_no_matches,fallback_to_base_filters", candidate.FeedbackReason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesAvoidSourceFallbackAddsDiagnosticGapReasons()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("避开来源回退诊断测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var projectLibraryId = "project:" + novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default";
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(novel.Id, chapter.ChapterNumber, currentDraft) with
        {
            Feedback = new ReferenceCorpusBlueprintFeedbackPayload(
                RejectedBlueprintIds: [],
                RejectedNodeIds: [],
                AvoidLibraryIds: [projectLibraryId, "global:workspace"],
                AvoidAnchorIds: [201, 202],
                ProblemTags: ["source_repetition"],
                Notes: "来源重复，但当前启用范围没有第三个来源")
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        Assert.True(result.FeedbackApplied);
        Assert.NotEmpty(result.Candidates);
        Assert.Contains("fallback:avoid_sources_no_alternatives,fallback_ignored_avoid_sources", result.FeedbackSummary, StringComparison.Ordinal);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.Contains("avoid_sources_no_alternatives", candidate.GapReasons);
            Assert.Contains("fallback_ignored_avoid_sources", candidate.GapReasons);
            Assert.Contains("fallback:avoid_sources_no_alternatives,fallback_ignored_avoid_sources", candidate.FeedbackReason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesUsesStructuredObservationFiltersFromGoal()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("结构化检索蓝图测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        await AddStructuredTechniqueSearchFixtureAsync(options);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(
            novel.Id,
            chapter.ChapterNumber,
            currentDraft) with
        {
            NaturalLanguageGoal = "写旧市集门口对峙，用动作替代心理描写表现愤怒，带触觉压迫，不直说生气。"
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        var allowedNodeIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "node-project-action-anger-s1",
            "node-project-action-anger-s2",
            "node-workspace-action-anger-s1"
        };
        var selectedNodeIds = result.Candidates
            .SelectMany(candidate => candidate.Blueprint.Beats)
            .SelectMany(beat => beat.NodeIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(result.Candidates);
        Assert.All(selectedNodeIds, nodeId => Assert.Contains(nodeId, allowedNodeIds));
        Assert.Contains("node-project-action-anger-s1", selectedNodeIds);
        Assert.Contains("node-workspace-action-anger-s1", selectedNodeIds);
        Assert.DoesNotContain("node-project-direct-anger-s1", selectedNodeIds);
        Assert.Contains(result.Candidates, candidate =>
            candidate.SourceDistribution.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).Count() >= 2);

        var draft = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                request.NaturalLanguageGoal,
                request.ChapterContext,
                request.Scope,
                new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                result.Candidates[0].Blueprint),
            CancellationToken.None);

        Assert.True(draft.ReadyForInsertion);
        Assert.All(draft.Pieces, piece => Assert.Contains(piece.NodeId, allowedNodeIds));
        Assert.Contains(draft.Pieces, piece => piece.LibraryId == "global:workspace");
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesProducesM4StrategyVariantsFromFeatureSignals()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("M4 蓝图策略测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        await AddM4BlueprintStrategyFixtureAsync(options);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(
            novel.Id,
            chapter.ChapterNumber,
            currentDraft) with
        {
            RequestedCount = 4,
            NaturalLanguageGoal = "写旧市集门口对峙，压住怒意，慢压迫，用动作替代心理描写，场景留白。"
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        var strategies = result.Candidates.Select(candidate => candidate.Blueprint.Strategy).ToArray();
        Assert.Equal(4, result.Candidates.Count);
        Assert.Contains("emotion_priority_m4", strategies);
        Assert.Contains("rhythm_priority_m4", strategies);
        Assert.Contains("technique_diversity_m4", strategies);
        Assert.Contains("scene_template_m4", strategies);
        Assert.Equal(
            result.Candidates.Count,
            result.Candidates.Select(candidate => BlueprintNodeSetKey(candidate.Blueprint)).Distinct(StringComparer.Ordinal).Count());
        Assert.All(result.Candidates, candidate =>
        {
            Assert.True(candidate.CoverageScore > 0);
            Assert.DoesNotContain("insufficient_beats", candidate.GapReasons);
            Assert.DoesNotContain("single_library_source", candidate.GapReasons);
            Assert.DoesNotContain("single_anchor_source", candidate.GapReasons);
            Assert.True(candidate.SourceDistribution.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).Count() >= 2);
        });
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesPersistsM4BlueprintMetadataAndBeatPieces()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("M4 蓝图持久化测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        await AddM4BlueprintStrategyFixtureAsync(options);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(
            novel.Id,
            chapter.ChapterNumber,
            currentDraft) with
        {
            RequestedCount = 4,
            NaturalLanguageGoal = "写旧市集门口对峙，压住怒意，慢压迫，用动作替代心理描写，场景留白。"
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        Assert.Equal(4, result.Candidates.Count);
        var rows = await ReadCorpusBlueprintRowsAsync(options, novel.Id, chapter.ChapterNumber);
        Assert.Equal(result.Candidates.Count, rows.Count);
        foreach (var candidate in result.Candidates)
        {
            var row = Assert.Single(rows, item => item.BlueprintId == candidate.Blueprint.BlueprintId);
            Assert.Equal(candidate.Blueprint.QueryContextHash, row.QueryContextHash);
            Assert.Equal(candidate.Blueprint.Strategy, row.AssemblyStrategy);
            Assert.Equal(candidate.CoverageScore, row.CoverageScore, precision: 6);
            Assert.Contains("\"chapter_context\"", row.QueryContextJson, StringComparison.Ordinal);
            Assert.Equal(
                candidate.GapReasons,
                JsonSerializer.Deserialize<IReadOnlyList<string>>(row.GapReasonsJson, GoldenJsonOptions) ?? []);
            Assert.Equal(
                candidate.GapPositions.Count,
                JsonSerializer.Deserialize<IReadOnlyList<ReferenceCorpusBlueprintGapPositionPayload>>(row.GapPositionsJson, GoldenJsonOptions)?.Count ?? 0);
            Assert.Equal(candidate.FeedbackReason, row.FeedbackReason);
            Assert.Equal(
                candidate.SourceDistribution.Count,
                JsonSerializer.Deserialize<IReadOnlyList<ReferenceCorpusBlueprintSourcePayload>>(row.SourceDistributionJson, GoldenJsonOptions)?.Count ?? 0);
            var beatRows = await ReadCorpusBlueprintBeatRowsAsync(options, candidate.Blueprint.BlueprintId);
            Assert.Equal(candidate.Blueprint.Beats.Count, beatRows.Count);

            foreach (var beat in candidate.Blueprint.Beats)
            {
                var beatRow = Assert.Single(beatRows, item => item.BeatId == beat.BeatId);
                Assert.Equal(candidate.Blueprint.BlueprintId, beatRow.BlueprintId);
                Assert.Equal(beat.BeatIndex, beatRow.BeatIndex);
                Assert.Equal(beat.RoleInBeat, beatRow.RoleInBeat);
                Assert.Equal(beat.NarrativeFunction, beatRow.NarrativeFunction);

                for (var index = 0; index < beat.NodeIds.Count; index++)
                {
                    Assert.True(
                        await BlueprintBeatPieceExistsAsync(options, beat.BeatId, beat.NodeIds[index]),
                        $"Expected beat piece {beat.BeatId}/{beat.NodeIds[index]} to be persisted for blueprint candidate {candidate.Blueprint.BlueprintId}.");
                }
            }
        }
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesUsesInjectedCandidateAssemblerAndPersistsResult()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("候选 assembler 边界测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        var candidateAssembler = new RecordingBlueprintCandidateAssembler();
        var service = new SqliteReferenceCorpusWritingService(
            options,
            new StaticReferenceCorpusService([
                M4DiagnosticCandidate("assembler-node-a", 101, "library-a", 0.91, ["emotion"], 0.4),
                M4DiagnosticCandidate("assembler-node-b", 202, "library-b", 0.89, ["rhythm"], 0.5)
            ]),
            chapters,
            blueprintCandidates: candidateAssembler);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(
            novel.Id,
            chapter.ChapterNumber,
            currentDraft) with
        {
            RequestedCount = 3,
            NaturalLanguageGoal = "写旧市集门口对峙，压住怒意，慢压迫。"
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        Assert.Equal(1, candidateAssembler.Calls);
        Assert.NotNull(candidateAssembler.LastRequest);
        Assert.Equal(3, candidateAssembler.LastRequest.RequestedCount);
        Assert.Equal(2, candidateAssembler.LastRequest.Candidates.Count);
        Assert.Equal("initial_candidate", candidateAssembler.LastRequest.FeedbackReason);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("candidate_assembler_boundary_test", candidate.Blueprint.Strategy);
        Assert.Equal(0.77, candidate.CoverageScore, precision: 6);
        Assert.Equal("initial_candidate", candidate.FeedbackReason);

        var rows = await ReadCorpusBlueprintRowsAsync(options, novel.Id, chapter.ChapterNumber);
        var row = Assert.Single(rows);
        Assert.Equal(candidate.Blueprint.BlueprintId, row.BlueprintId);
        Assert.Equal("candidate_assembler_boundary_test", row.AssemblyStrategy);
        Assert.Equal(candidate.FeedbackReason, row.FeedbackReason);
        var beatRows = await ReadCorpusBlueprintBeatRowsAsync(options, candidate.Blueprint.BlueprintId);
        Assert.Equal(candidate.Blueprint.Beats.Count, beatRows.Count);
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesScoresM4CoverageByRequiredDimensionEvidenceAndReportsGaps()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("M4 覆盖率诊断测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        var service = new SqliteReferenceCorpusWritingService(
            options,
            new StaticReferenceCorpusService([
                M4DiagnosticCandidate(
                    "node-m4-incomplete-project-s1",
                    301,
                    "project:" + novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default",
                    0.99,
                    ["emotion"],
                    techniqueFit: 0),
                M4DiagnosticCandidate(
                    "node-m4-incomplete-workspace-s1",
                    302,
                    "global:workspace",
                    0.98,
                    ["emotion", "action"],
                    techniqueFit: 0),
                M4DiagnosticCandidate(
                    "node-m4-incomplete-project-s2",
                    303,
                    "project:" + novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default",
                    0.97,
                    ["emotion"],
                    techniqueFit: 0),
                M4DiagnosticCandidate(
                    "node-m4-complete-project-s1",
                    401,
                    "project:" + novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default",
                    0.61,
                    ["emotion", "rhythm", "narrative"],
                    techniqueFit: 0.92),
                M4DiagnosticCandidate(
                    "node-m4-complete-workspace-s1",
                    402,
                    "global:workspace",
                    0.60,
                    ["emotion", "rhythm", "narrative"],
                    techniqueFit: 0.93),
                M4DiagnosticCandidate(
                    "node-m4-complete-project-s2",
                    403,
                    "project:" + novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default",
                    0.59,
                    ["emotion", "rhythm", "narrative"],
                    techniqueFit: 0.91)
            ]),
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(
            novel.Id,
            chapter.ChapterNumber,
            currentDraft) with
        {
            RequestedCount = 5,
            NaturalLanguageGoal = "写旧市集门口对峙，压住怒意，慢压迫，用动作替代心理描写，场景留白。"
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        var complete = Assert.Single(result.Candidates, candidate => candidate.Blueprint.Strategy == "emotion_priority_m4");
        var incomplete = Assert.Single(result.Candidates, candidate => candidate.Blueprint.Strategy == "score_focus_m1");
        Assert.True(
            complete.CoverageScore > incomplete.CoverageScore,
            $"Complete M4 evidence should outrank text-score-only candidates. complete={complete.CoverageScore}, incomplete={incomplete.CoverageScore}");
        Assert.Contains("missing_rhythm_evidence", incomplete.GapReasons);
        Assert.Contains("missing_narrative_evidence", incomplete.GapReasons);
        Assert.Contains("missing_technique_coverage", incomplete.GapReasons);
        Assert.DoesNotContain("missing_rhythm_evidence", complete.GapReasons);
        Assert.DoesNotContain("missing_narrative_evidence", complete.GapReasons);
        Assert.DoesNotContain("missing_technique_coverage", complete.GapReasons);
        Assert.Empty(complete.GapPositions);
        Assert.Equal(3, incomplete.GapPositions.Count);
        var firstGap = incomplete.GapPositions[0];
        Assert.Equal(0, firstGap.BeatIndex);
        Assert.Equal(["node-m4-incomplete-project-s1"], firstGap.NodeIds);
        Assert.Equal(["emotion"], firstGap.CoveredDimensions);
        Assert.Equal(["rhythm", "narrative", "technique"], firstGap.MissingDimensions);
        Assert.Contains("missing_rhythm_evidence", firstGap.GapReasons);
        Assert.Contains("missing_narrative_evidence", firstGap.GapReasons);
        Assert.Contains("missing_technique_coverage", firstGap.GapReasons);
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesBackfillsM4StrategyCandidatesWithCoverageEvidence()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("M4 主动补齐覆盖测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        var projectLibraryId = "project:" + novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default";
        var service = new SqliteReferenceCorpusWritingService(
            options,
            new StaticReferenceCorpusService([
                M4DiagnosticCandidate(
                    "node-m4-emotion-only-project-s1",
                    501,
                    projectLibraryId,
                    0.99,
                    ["emotion"],
                    techniqueFit: 0),
                M4DiagnosticCandidate(
                    "node-m4-emotion-only-workspace-s1",
                    502,
                    "global:workspace",
                    0.98,
                    ["emotion"],
                    techniqueFit: 0),
                M4DiagnosticCandidate(
                    "node-m4-emotion-only-project-s2",
                    503,
                    projectLibraryId,
                    0.97,
                    ["emotion"],
                    techniqueFit: 0),
                M4DiagnosticCandidate(
                    "node-m4-rhythm-coverage-workspace-s1",
                    601,
                    "global:workspace",
                    0.42,
                    ["rhythm"],
                    techniqueFit: 0),
                M4DiagnosticCandidate(
                    "node-m4-narrative-technique-project-s1",
                    602,
                    projectLibraryId,
                    0.41,
                    ["narrative"],
                    techniqueFit: 0.95)
            ]),
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(
            novel.Id,
            chapter.ChapterNumber,
            currentDraft) with
        {
            RequestedCount = 1,
            NaturalLanguageGoal = "写旧市集门口对峙，压住怒意，慢压迫，用动作替代心理描写，场景留白。"
        };

        var result = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("emotion_priority_m4", candidate.Blueprint.Strategy);
        var nodeIds = candidate.Blueprint.Beats.SelectMany(beat => beat.NodeIds).ToArray();
        Assert.Contains("node-m4-rhythm-coverage-workspace-s1", nodeIds);
        Assert.Contains("node-m4-narrative-technique-project-s1", nodeIds);
        Assert.DoesNotContain("missing_emotion_evidence", candidate.GapReasons);
        Assert.DoesNotContain("missing_rhythm_evidence", candidate.GapReasons);
        Assert.DoesNotContain("missing_narrative_evidence", candidate.GapReasons);
        Assert.DoesNotContain("missing_technique_coverage", candidate.GapReasons);
    }

    [Fact]
    public async Task GenerateBlueprintCandidatesMapsTooFastFeedbackToSlowRhythmRetrieval()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("反馈节奏检索测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        await AddFeedbackRhythmSearchFixtureAsync(options);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var request = BuildCrossLibraryBlueprintCandidatesPayload(
            novel.Id,
            chapter.ChapterNumber,
            currentDraft) with
        {
            RequestedCount = 2,
            NaturalLanguageGoal = "写旧市集门口对峙，秦砚压住怒意，不立刻开口。"
        };

        var firstRound = await service.GenerateBlueprintCandidatesAsync(request, CancellationToken.None);
        var firstNodeIds = firstRound.Candidates
            .SelectMany(candidate => candidate.Blueprint.Beats)
            .SelectMany(beat => beat.NodeIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("node-feedback-fast-market-s1", firstNodeIds);

        var secondRound = await service.GenerateBlueprintCandidatesAsync(request with
        {
            Feedback = new ReferenceCorpusBlueprintFeedbackPayload(
                RejectedBlueprintIds: [],
                RejectedNodeIds: [],
                AvoidLibraryIds: [],
                AvoidAnchorIds: [],
                ProblemTags: ["too_fast"],
                Notes: "节奏太急，换成慢压迫")
        }, CancellationToken.None);

        var slowNodeIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "node-feedback-slow-project-s1",
            "node-feedback-slow-workspace-s1"
        };
        var secondNodeIds = secondRound.Candidates
            .SelectMany(candidate => candidate.Blueprint.Beats)
            .SelectMany(beat => beat.NodeIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(secondRound.FeedbackApplied);
        Assert.Contains("problems:too_fast", secondRound.FeedbackSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback:", secondRound.FeedbackSummary, StringComparison.Ordinal);
        Assert.NotEmpty(secondRound.Candidates);
        Assert.All(secondNodeIds, nodeId => Assert.Contains(nodeId, slowNodeIds));
        Assert.DoesNotContain("node-feedback-fast-market-s1", secondNodeIds);
        var firstFeedbackCandidate = secondRound.Candidates[0];
        var firstFeedbackNodeIds = firstFeedbackCandidate.Blueprint.Beats
            .SelectMany(beat => beat.NodeIds)
            .ToArray();
        Assert.Equal("rhythm_slow_m1", firstFeedbackCandidate.Blueprint.Strategy);
        Assert.Contains("node-feedback-slow-project-s1", firstFeedbackNodeIds);
        Assert.Contains("node-feedback-slow-workspace-s1", firstFeedbackNodeIds);
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesReusesSelectedBlueprintSourceVariantsThroughGate()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文多候选语料闭环测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-cross-library-blueprint",
            QueryContextHash: "cross-library-query",
            Strategy: "selected_multi_source_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-cross-library-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-doorway-s1", "node-project-doorway-s2"]),
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-cross-library-beat-2",
                    BeatIndex: 1,
                    RoleInBeat: "supporting_source_sentence",
                    NarrativeFunction: "withhold_answer",
                    NodeIds: ["node-workspace-market-s1", "node-workspace-market-s2"])
            ]);

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 3),
            CancellationToken.None);

        Assert.Equal(selectedBlueprint.BlueprintId, result.SelectedBlueprint.BlueprintId);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(result.Candidates.Count, result.Candidates.Select(candidate => candidate.CandidateId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(result.Candidates.Count, result.Candidates.Select(candidate => candidate.Draft.AssembledText).Distinct(StringComparer.Ordinal).Count());
        var allowedNodeIds = selectedBlueprint.Beats
            .SelectMany(beat => beat.NodeIds)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in result.Candidates)
        {
            Assert.False(string.IsNullOrWhiteSpace(candidate.Explanation));
            Assert.True(candidate.Draft.ReadyForInsertion);
            Assert.True(candidate.Draft.Gate.Passed);
            Assert.Equal(selectedBlueprint.Beats.Count, candidate.Draft.Pieces.Count);
            Assert.StartsWith(selectedBlueprint.BlueprintId + ":draft-", candidate.Draft.Blueprint.BlueprintId, StringComparison.Ordinal);
            Assert.All(candidate.Draft.Pieces, piece =>
            {
                Assert.Contains(piece.NodeId, allowedNodeIds);
                Assert.True(piece.PreservedHashMatches);
            });
            AssertDraftPiecesStayWithinSelectedBeats(selectedBlueprint, candidate.Draft);

            foreach (var piece in candidate.Draft.Pieces)
            {
                Assert.True(await BlueprintBeatPieceExistsAsync(options, piece.BeatId, piece.NodeId));
            }
        }

        Assert.Contains(result.Candidates, candidate =>
            candidate.Draft.Pieces.Select(piece => piece.NodeId).SequenceEqual(
                ["node-project-doorway-s1", "node-workspace-market-s2"]));
        Assert.Contains(result.Candidates, candidate =>
            candidate.Draft.Pieces.Select(piece => piece.NodeId).SequenceEqual(
                ["node-project-doorway-s2", "node-workspace-market-s1"]));
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesCanProduceSlotOnlyVariantsFromSameSelectedBlueprint()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文槽位多草稿测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在黑塔门前。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        const string sourceSentence = "她在旧市集门口没有立刻开口，只叫了一声师兄，把钥匙扣在掌心，《旧市集门口师兄钥匙案》没有改。";
        var sourcePath = CreateSourceFile(
            "slot-only-draft-variants.md",
            $$"""
            # 第一章 槽位多草稿

            {{sourceSentence}}
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "槽位多草稿语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        const string nodeId = "node-slot-only-variants-s1";
        await InsertReferenceTextNodeAsync(options, anchor.AnchorId, nodeId, sourceSentence);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-slot-only-variants",
            QueryContextHash: "query-slot-only-variants",
            Strategy: "selected_slot_only_fixture",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-slot-only-variants-beat",
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: [nodeId])
            ]);

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写门口对峙，秦砚压住怒意，没有立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "黑塔门前，秦砚需要压住情绪。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["队长在场"], ["铜令真正用途"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>(),
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 2,
                SlotValueVariants:
                [
                    new ReferenceCorpusDraftSlotValueVariantPayload(
                        VariantId: "strict-current-scene",
                        Label: "黑塔队长铜令",
                        SlotValues: new Dictionary<string, string>
                        {
                            ["character:她"] = "秦砚",
                            ["place:旧市集门口"] = "黑塔门前",
                            ["honorific:师兄"] = "队长",
                            ["plot_object:钥匙"] = "铜令"
                        }),
                    new ReferenceCorpusDraftSlotValueVariantPayload(
                        VariantId: "alternate-current-scene",
                        Label: "废站组长门卡",
                        SlotValues: new Dictionary<string, string>
                        {
                            ["character:她"] = "秦砚",
                            ["place:旧市集门口"] = "废站门前",
                            ["honorific:师兄"] = "组长",
                            ["plot_object:钥匙"] = "门卡"
                        })
                ]),
            CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(["slot_variant_1", "slot_variant_2"], result.Candidates.Select(candidate => candidate.Strategy).ToArray());
        Assert.Equal(result.Candidates.Count, result.Candidates.Select(candidate => candidate.CandidateId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(result.Candidates.Count, result.Candidates.Select(candidate => candidate.Draft.AssembledText).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("黑塔门前", result.Candidates[0].Draft.AssembledText, StringComparison.Ordinal);
        Assert.Contains("铜令", result.Candidates[0].Draft.AssembledText, StringComparison.Ordinal);
        Assert.Contains("废站门前", result.Candidates[1].Draft.AssembledText, StringComparison.Ordinal);
        Assert.Contains("门卡", result.Candidates[1].Draft.AssembledText, StringComparison.Ordinal);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.True(candidate.Draft.ReadyForInsertion);
            Assert.True(candidate.Draft.Gate.Passed);
            Assert.True(candidate.Draft.Audit.Passed);
            Assert.Single(candidate.Draft.Pieces);
            Assert.Equal([nodeId], candidate.Draft.Pieces.Select(piece => piece.NodeId).ToArray());
            Assert.Equal([nodeId], candidate.Draft.Blueprint.Beats.SelectMany(beat => beat.NodeIds).ToArray());
            Assert.Contains("《旧市集门口师兄钥匙案》", candidate.Draft.AssembledText, StringComparison.Ordinal);
            Assert.DoesNotContain("《黑塔门前队长铜令案》", candidate.Draft.AssembledText, StringComparison.Ordinal);
            Assert.DoesNotContain("《废站门前组长门卡案》", candidate.Draft.AssembledText, StringComparison.Ordinal);
            Assert.All(candidate.Draft.Pieces[0].LockedSpans, span => Assert.True(span.Matches));
        });
        var firstPiece = result.Candidates[0].Draft.Pieces[0];
        var secondPiece = result.Candidates[1].Draft.Pieces[0];
        Assert.Equal(firstPiece.NodeId, secondPiece.NodeId);
        Assert.Equal(firstPiece.SourceTextHash, secondPiece.SourceTextHash);
        Assert.Equal(firstPiece.PreservedTextHash, secondPiece.PreservedTextHash);
        Assert.Equal(
            firstPiece.PreservedSpans.Select(span => (span.SourceStart, span.SourceEnd)).ToArray(),
            secondPiece.PreservedSpans.Select(span => (span.SourceStart, span.SourceEnd)).ToArray());
        Assert.Equal(
            firstPiece.LockedSpans.Select(span => (span.SourceStart, span.SourceEnd, span.SourceTextHash)).ToArray(),
            secondPiece.LockedSpans.Select(span => (span.SourceStart, span.SourceEnd, span.SourceTextHash)).ToArray());
        Assert.NotEqual(
            firstPiece.SlotReplacements.Select(replacement => replacement.ReplacementValue).ToArray(),
            secondPiece.SlotReplacements.Select(replacement => replacement.ReplacementValue).ToArray());
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesBlocksNonSlotDifferencesAcrossSlotVariants()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文多草稿差异审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在黑塔门前。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        const string sourceSentence = "她没有立刻开口，指尖压住钥匙。";
        var sourcePath = CreateSourceFile(
            "slot-candidate-set-diff-audit.md",
            $$"""
            # 第一章 多草稿差异审计

            {{sourceSentence}}
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "多草稿差异审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        const string nodeId = "node-candidate-set-diff-s1";
        await InsertReferenceTextNodeAsync(options, anchor.AnchorId, nodeId, sourceSentence);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            textAssembler: new CandidateSetNonSlotDriftTextAssembler());
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-candidate-set-diff",
            QueryContextHash: "query-candidate-set-diff",
            Strategy: "selected_candidate_set_diff_fixture",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-candidate-set-diff-beat",
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: [nodeId])
            ]);

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写门口对峙，人物压住情绪，没有立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "黑塔门前，人物需要压住情绪。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["队长在场"], ["钥匙用途"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>(),
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 2,
                SlotValueVariants:
                [
                    new ReferenceCorpusDraftSlotValueVariantPayload(
                        VariantId: "slot-safe",
                        Label: "只替换人物",
                        SlotValues: new Dictionary<string, string>
                        {
                            ["character:她"] = "秦砚"
                        }),
                    new ReferenceCorpusDraftSlotValueVariantPayload(
                        VariantId: "slot-drift",
                        Label: "偷偷改非槽位动作",
                        SlotValues: new Dictionary<string, string>
                        {
                            ["character:她"] = "沈照"
                        })
                ]),
            CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("掌心压住钥匙", result.Candidates[1].Draft.AssembledText, StringComparison.Ordinal);
        Assert.True(result.Candidates[0].Draft.ReadyForInsertion);
        Assert.True(result.Candidates[0].Draft.Audit.Passed);
        Assert.False(result.Candidates[1].Draft.ReadyForInsertion);
        Assert.True(result.Candidates[1].Draft.Gate.Passed);
        Assert.False(result.Candidates[1].Draft.Audit.Passed);
        Assert.Equal(currentDraft, result.Candidates[1].Draft.ChapterTextAfterInsertion);
        Assert.Contains(
            $"draft_candidate_set_non_slot_difference:{nodeId}",
            result.Candidates[1].Draft.Audit.Errors,
            StringComparer.Ordinal);
        var pieceAudit = Assert.Single(result.Candidates[1].Draft.Audit.Pieces);
        Assert.Contains(
            pieceAudit.Violations,
            violation => violation.Code == "draft_candidate_set_non_slot_difference");
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesBlocksDuplicateTextAcrossSlotVariants()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文重复候选审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在黑塔门前。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        const string sourceSentence = "她没有立刻开口，指尖压住钥匙。";
        var sourcePath = CreateSourceFile(
            "slot-candidate-set-duplicate-audit.md",
            $$"""
            # 第一章 重复候选审计

            {{sourceSentence}}
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "重复候选审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        const string nodeId = "node-candidate-set-duplicate-s1";
        await InsertReferenceTextNodeAsync(options, anchor.AnchorId, nodeId, sourceSentence);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-candidate-set-duplicate",
            QueryContextHash: "query-candidate-set-duplicate",
            Strategy: "selected_candidate_set_duplicate_fixture",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-candidate-set-duplicate-beat",
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: [nodeId])
            ]);

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写门口对峙，人物压住情绪，没有立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "黑塔门前，人物需要压住情绪。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["队长在场"], ["钥匙用途"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>(),
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 2,
                SlotValueVariants:
                [
                    new ReferenceCorpusDraftSlotValueVariantPayload(
                        VariantId: "slot-used",
                        Label: "命中原文人物",
                        SlotValues: new Dictionary<string, string>
                        {
                            ["character:她"] = "秦砚"
                        }),
                    new ReferenceCorpusDraftSlotValueVariantPayload(
                        VariantId: "slot-unused",
                        Label: "未命中原文参数",
                        SlotValues: new Dictionary<string, string>
                        {
                            ["character:她"] = "秦砚",
                            ["place:旧市集"] = "黑塔"
                        })
                ]),
            CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(result.Candidates[0].Draft.AssembledText, result.Candidates[1].Draft.AssembledText);
        Assert.True(result.Candidates[0].Draft.ReadyForInsertion);
        Assert.True(result.Candidates[0].Draft.Audit.Passed);
        Assert.False(result.Candidates[1].Draft.ReadyForInsertion);
        Assert.True(result.Candidates[1].Draft.Gate.Passed);
        Assert.False(result.Candidates[1].Draft.Audit.Passed);
        Assert.Equal(currentDraft, result.Candidates[1].Draft.ChapterTextAfterInsertion);
        Assert.Contains(
            $"draft_candidate_set_duplicate_text:{nodeId}",
            result.Candidates[1].Draft.Audit.Errors,
            StringComparer.Ordinal);
        var pieceAudit = Assert.Single(result.Candidates[1].Draft.Audit.Pieces);
        Assert.Contains(
            pieceAudit.Violations,
            violation => violation.Code == "draft_candidate_set_duplicate_text");
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesHonorsTransferSlotConstraintsFromTechniqueSpecimens()
    {
        var fixture = await CreateSlotTransferConstraintFixtureAsync(
            "正文槽位约束合法测试",
            """
            [
              {"slot_name":"character","purpose":"当前承压角色","constraints":"必须来自当前章节人物"},
              {"slot_name":"place","purpose":"当前场景地点","constraints":"必须匹配插入位置"},
              {"slot_name":"honorific","purpose":"当前关系称谓","constraints":"必须符合人物关系"},
              {"slot_name":"plot_object","purpose":"当前剧情道具","constraints":"必须已在当前章节出现或即将揭示"}
            ]
            """);

        var result = await fixture.Service.GenerateInsertionDraftCandidatesAsync(
            BuildSlotTransferConstraintRequest(fixture, includePlotObject: true),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("slot_variant_1", candidate.Strategy);
        Assert.True(candidate.Draft.ReadyForInsertion);
        Assert.True(candidate.Draft.Gate.Passed);
        Assert.True(candidate.Draft.Audit.Passed);
        Assert.Contains("黑塔门前", candidate.Draft.AssembledText, StringComparison.Ordinal);
        Assert.Contains("铜令", candidate.Draft.AssembledText, StringComparison.Ordinal);
        Assert.All(candidate.Draft.Audit.Pieces, piece => Assert.DoesNotContain(
            piece.Violations,
            violation => violation.Code == "slot_replacement_transfer_slot_disallowed"));
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesBlocksSlotReplacementOutsideTransferSlots()
    {
        var fixture = await CreateSlotTransferConstraintFixtureAsync(
            "正文槽位约束阻断测试",
            """
            [
              {"slot_name":"character","purpose":"当前承压角色","constraints":"必须来自当前章节人物"},
              {"slot_name":"place","purpose":"当前场景地点","constraints":"必须匹配插入位置"},
              {"slot_name":"honorific","purpose":"当前关系称谓","constraints":"必须符合人物关系"}
            ]
            """);

        var result = await fixture.Service.GenerateInsertionDraftCandidatesAsync(
            BuildSlotTransferConstraintRequest(fixture, includePlotObject: true),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.Draft.ReadyForInsertion);
        Assert.True(candidate.Draft.Gate.Passed);
        Assert.False(candidate.Draft.Audit.Passed);
        Assert.Equal(fixture.CurrentDraft, candidate.Draft.ChapterTextAfterInsertion);
        var pieceAudit = Assert.Single(candidate.Draft.Audit.Pieces);
        var violation = Assert.Single(
            pieceAudit.Violations,
            item => item.Code == "slot_replacement_transfer_slot_disallowed");
        Assert.Equal(fixture.NodeId, violation.NodeId);
        Assert.Contains("plot_object", violation.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"slot_replacement_transfer_slot_disallowed:{fixture.NodeId}",
            candidate.Draft.Audit.Errors,
            StringComparer.Ordinal);
        Assert.Contains("铜令", candidate.Draft.AssembledText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesIgnoresRejectedTechniqueSpecimenTransferSlots()
    {
        var fixture = await CreateSlotTransferConstraintFixtureAsync(
            "正文槽位约束拒绝状态测试",
            """
            [
              {"slot_name":"character","purpose":"当前承压角色","constraints":"必须来自当前章节人物"},
              {"slot_name":"place","purpose":"当前场景地点","constraints":"必须匹配插入位置"}
            ]
            """,
            reviewState: "rejected");

        var result = await fixture.Service.GenerateInsertionDraftCandidatesAsync(
            BuildSlotTransferConstraintRequest(fixture, includePlotObject: true),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.True(candidate.Draft.ReadyForInsertion);
        Assert.True(candidate.Draft.Gate.Passed);
        Assert.True(candidate.Draft.Audit.Passed);
        Assert.Contains("铜令", candidate.Draft.AssembledText, StringComparison.Ordinal);
        Assert.All(candidate.Draft.Audit.Pieces, piece => Assert.DoesNotContain(
            piece.Violations,
            violation => violation.Code == "slot_replacement_transfer_slot_disallowed"));
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesAutoDerivesCharacterTransferSlotVariants()
    {
        var fixture = await CreateSlotTransferConstraintFixtureAsync(
            "正文自动槽位候选测试",
            """
            [
              {"slot_name":"character","purpose":"当前承压角色","constraints":"必须来自当前章节人物"}
            ]
            """);

        var result = await fixture.Service.GenerateInsertionDraftCandidatesAsync(
            BuildSlotTransferConstraintAutoRequest(
                fixture,
                requestedCount: 2,
                characterSnapshots:
                [
                    new CharacterStateSnapshotPayload("秦砚", "guarded", ["队长在场"], ["铜令真正用途"]),
                    new CharacterStateSnapshotPayload("沈照", "watching", ["秦砚压住怒意"], [])
                ]),
            CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(["auto_transfer_slot_1", "auto_transfer_slot_2"], result.Candidates.Select(candidate => candidate.Strategy).ToArray());
        Assert.Equal(result.Candidates.Count, result.Candidates.Select(candidate => candidate.CandidateId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(result.Candidates.Count, result.Candidates.Select(candidate => candidate.Draft.AssembledText).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("秦砚在旧市集门口没有立刻开口", result.Candidates[0].Draft.AssembledText, StringComparison.Ordinal);
        Assert.Contains("沈照在旧市集门口没有立刻开口", result.Candidates[1].Draft.AssembledText, StringComparison.Ordinal);
        Assert.All(result.Candidates, candidate =>
        {
            Assert.True(candidate.Draft.ReadyForInsertion);
            Assert.True(candidate.Draft.Gate.Passed);
            Assert.True(candidate.Draft.Audit.Passed);
            Assert.Single(candidate.Draft.Pieces);
            Assert.Equal([fixture.NodeId], candidate.Draft.Pieces.Select(piece => piece.NodeId).ToArray());
            Assert.Equal([fixture.NodeId], candidate.Draft.Blueprint.Beats.SelectMany(beat => beat.NodeIds).ToArray());
            Assert.Contains("钥匙", candidate.Draft.AssembledText, StringComparison.Ordinal);
            Assert.DoesNotContain("铜令", candidate.Draft.AssembledText, StringComparison.Ordinal);
            Assert.All(candidate.Draft.Audit.Pieces, piece => Assert.DoesNotContain(
                piece.Violations,
                violation => violation.Code == "slot_replacement_transfer_slot_disallowed"));
        });

        var firstPiece = result.Candidates[0].Draft.Pieces[0];
        var secondPiece = result.Candidates[1].Draft.Pieces[0];
        Assert.Equal(firstPiece.NodeId, secondPiece.NodeId);
        Assert.Equal(firstPiece.SourceTextHash, secondPiece.SourceTextHash);
        Assert.Equal(firstPiece.PreservedTextHash, secondPiece.PreservedTextHash);
        Assert.Equal(
            firstPiece.PreservedSpans.Select(span => (span.SourceStart, span.SourceEnd)).ToArray(),
            secondPiece.PreservedSpans.Select(span => (span.SourceStart, span.SourceEnd)).ToArray());
        Assert.NotEqual(
            firstPiece.SlotReplacements.Select(replacement => replacement.ReplacementValue).ToArray(),
            secondPiece.SlotReplacements.Select(replacement => replacement.ReplacementValue).ToArray());
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesDoesNotAutoDeriveRejectedTransferSlots()
    {
        var fixture = await CreateSlotTransferConstraintFixtureAsync(
            "正文自动槽位候选拒绝状态测试",
            """
            [
              {"slot_name":"character","purpose":"当前承压角色","constraints":"必须来自当前章节人物"}
            ]
            """,
            reviewState: "rejected");

        var result = await fixture.Service.GenerateInsertionDraftCandidatesAsync(
            BuildSlotTransferConstraintAutoRequest(
                fixture,
                requestedCount: 2,
                characterSnapshots:
                [
                    new CharacterStateSnapshotPayload("秦砚", "guarded", ["队长在场"], ["铜令真正用途"]),
                    new CharacterStateSnapshotPayload("沈照", "watching", ["秦砚压住怒意"], [])
                ]),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("source_variant_1", candidate.Strategy);
        Assert.True(candidate.Draft.ReadyForInsertion);
        Assert.Equal([fixture.NodeId], candidate.Draft.Pieces.Select(piece => piece.NodeId).ToArray());
        Assert.Contains("秦砚在旧市集门口没有立刻开口", candidate.Draft.AssembledText, StringComparison.Ordinal);
        Assert.DoesNotContain("沈照在旧市集门口没有立刻开口", candidate.Draft.AssembledText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesDoNotSubstituteNodesOutsideSelectedBlueprint()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文候选不换料测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-fixed-node-blueprint",
            QueryContextHash: "fixed-node-query",
            Strategy: "selected_fixed_node_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-fixed-node-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-doorway-s1"]),
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-fixed-node-beat-2",
                    BeatIndex: 1,
                    RoleInBeat: "supporting_source_sentence",
                    NarrativeFunction: "withhold_answer",
                    NodeIds: ["node-workspace-market-s1"])
            ]);
        var selectedNodeIds = selectedBlueprint.Beats
            .SelectMany(beat => beat.NodeIds)
            .ToArray();

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 3),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(selectedNodeIds, candidate.Draft.Pieces.Select(piece => piece.NodeId).ToArray());
        Assert.Equal(selectedNodeIds, candidate.Draft.Blueprint.Beats.SelectMany(beat => beat.NodeIds).ToArray());
        AssertDraftPiecesStayWithinSelectedBeats(selectedBlueprint, candidate.Draft);
        Assert.DoesNotContain("node-project-doorway-s2", candidate.Draft.Pieces.Select(piece => piece.NodeId));
        Assert.DoesNotContain("node-workspace-market-s2", candidate.Draft.Pieces.Select(piece => piece.NodeId));
        Assert.True(candidate.Draft.ReadyForInsertion);
        Assert.True(candidate.Draft.Gate.Passed);
        Assert.True(candidate.Draft.Audit.Passed);
        Assert.All(candidate.Draft.Pieces, piece => Assert.True(piece.PreservedHashMatches));
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesRebuildsAllowedBlueprintVariantWhenTransitionRequiresReplacement()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文候选转场换源重组测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            transitionResolver: new ReplacementRequestingTransitionResolver(
                rejectedNodeId: "node-project-doorway-s1",
                replacementNodeId: "node-project-doorway-s2"));
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-transition-repair-blueprint",
            QueryContextHash: "transition-repair-query",
            Strategy: "selected_transition_repair_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-transition-repair-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-doorway-s1", "node-project-doorway-s2"]),
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-transition-repair-beat-2",
                    BeatIndex: 1,
                    RoleInBeat: "supporting_source_sentence",
                    NarrativeFunction: "withhold_answer",
                    NodeIds: ["node-workspace-market-s1"])
            ]);

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 1),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("transition_repair", candidate.Strategy);
        Assert.Contains("replacement", candidate.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.True(candidate.Draft.ReadyForInsertion);
        Assert.True(candidate.Draft.Audit.Passed);
        Assert.True(candidate.Draft.Gate.Passed);
        Assert.Equal(
            ["node-project-doorway-s2", "node-workspace-market-s1"],
            candidate.Draft.Pieces.Select(piece => piece.NodeId).ToArray());
        AssertDraftPiecesStayWithinSelectedBeats(selectedBlueprint, candidate.Draft);
        Assert.DoesNotContain("transition_piece_replacement_required", candidate.Draft.Audit.Errors, StringComparer.Ordinal);
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesBlocksTransitionReplacementOutsideSelectedBlueprint()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文候选转场禁止蓝图外换源测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            transitionResolver: new ReplacementRequestingTransitionResolver(
                rejectedNodeId: "node-project-doorway-s1",
                replacementNodeId: "node-project-doorway-s2"));
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-transition-outside-repair-blueprint",
            QueryContextHash: "transition-outside-repair-query",
            Strategy: "selected_transition_outside_repair_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-transition-outside-repair-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-doorway-s1"]),
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-transition-outside-repair-beat-2",
                    BeatIndex: 1,
                    RoleInBeat: "supporting_source_sentence",
                    NarrativeFunction: "withhold_answer",
                    NodeIds: ["node-workspace-market-s1"])
            ]);
        var chapterContext = new CurrentChapterContextPayload(
            novel.Id,
            chapter.ChapterNumber,
            currentDraft,
            currentDraft.Length,
            "旧市集起火，有人在火光里靠近。",
            [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]);
        var scope = new ReferenceCorpusScopePayload(
            LibraryIds: [],
            ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
            IncludeAnchorIds: [],
            ExcludeAnchorIds: []);

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: chapterContext,
                Scope: scope,
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 1),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("source_variant_1", candidate.Strategy);
        Assert.False(candidate.Draft.ReadyForInsertion);
        Assert.False(candidate.Draft.Audit.Passed);
        Assert.Equal(currentDraft, candidate.Draft.ChapterTextAfterInsertion);
        Assert.Contains(candidate.Draft.Audit.Errors, error =>
            error.StartsWith("transition_piece_replacement_required:", StringComparison.Ordinal));
        Assert.Equal(
            ["node-project-doorway-s1", "node-workspace-market-s1"],
            candidate.Draft.Pieces.Select(piece => piece.NodeId).ToArray());
        AssertDraftPiecesStayWithinSelectedBeats(selectedBlueprint, candidate.Draft);
        Assert.DoesNotContain("node-project-doorway-s2", candidate.Draft.Pieces.Select(piece => piece.NodeId));

        Assert.NotNull(candidate.NextAction);
        var nextAction = candidate.NextAction!;
        Assert.Equal(ReferenceCorpusDraftCandidateNextActions.RegenerateBlueprint, nextAction.Action);
        Assert.Equal("transition_replacement_outside_selected_blueprint", nextAction.ReasonCode);
        Assert.Equal("node-project-doorway-s1", nextAction.RejectedNodeId);
        Assert.Equal("node-project-doorway-s2", nextAction.ReplacementNodeId);
        Assert.Contains("transition_replacement_required", nextAction.Feedback.ProblemTags);
        Assert.Contains("transition_replacement_outside_selected_blueprint", nextAction.Feedback.ProblemTags);
        Assert.Contains("node-project-doorway-s1", nextAction.Feedback.RejectedNodeIds);
        Assert.Contains(selectedBlueprint.BlueprintId, nextAction.Feedback.RejectedBlueprintIds);

        var regenerated = await service.GenerateBlueprintCandidatesAsync(
            new GenerateReferenceCorpusBlueprintCandidatesPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: chapterContext,
                Scope: scope,
                RequestedCount: 3,
                Feedback: nextAction.Feedback),
            CancellationToken.None);

        Assert.True(regenerated.FeedbackApplied);
        Assert.Contains("problems:transition_replacement_outside_selected_blueprint,transition_replacement_required", regenerated.FeedbackSummary, StringComparison.Ordinal);
        Assert.All(regenerated.Candidates, blueprintCandidate =>
            Assert.DoesNotContain(
                "node-project-doorway-s1",
                blueprintCandidate.Blueprint.Beats.SelectMany(beat => beat.NodeIds)));

        var regeneratedBlueprint = regenerated.Candidates
            .Select(candidate => candidate.Blueprint)
            .First(blueprint => blueprint.Beats.SelectMany(beat => beat.NodeIds).Any());
        var regeneratedDrafts = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: chapterContext,
                Scope: scope,
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: regeneratedBlueprint,
                RequestedCount: 3),
            CancellationToken.None);

        var regeneratedReadyDraft = regeneratedDrafts.Candidates.FirstOrDefault(candidate => candidate.Draft.ReadyForInsertion);
        Assert.NotNull(regeneratedReadyDraft);
        Assert.True(regeneratedReadyDraft!.Draft.Gate.Passed);
        Assert.True(regeneratedReadyDraft.Draft.Audit.Passed);
        Assert.Null(regeneratedReadyDraft.NextAction);
        Assert.DoesNotContain(
            "node-project-doorway-s1",
            regeneratedReadyDraft.Draft.Pieces.Select(piece => piece.NodeId));
        Assert.All(regeneratedReadyDraft.Draft.Pieces, piece =>
        {
            var beat = regeneratedBlueprint.Beats.Single(item => string.Equals(item.BeatId, piece.BeatId, StringComparison.Ordinal));
            Assert.Contains(piece.NodeId, beat.NodeIds);
        });
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesKeepsOriginalBlockedCandidateWhenTransitionRepairStillFails()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文候选转场重组失败回退测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            transitionResolver: new ReplacementRequestingTransitionResolver(
                rejectedNodeIds: ["node-project-doorway-s1", "node-project-doorway-s2"],
                replacementNodeId: "node-project-doorway-s2"));
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-transition-repair-fails-blueprint",
            QueryContextHash: "transition-repair-fails-query",
            Strategy: "selected_transition_repair_fails_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-transition-repair-fails-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-doorway-s1", "node-project-doorway-s2"]),
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-transition-repair-fails-beat-2",
                    BeatIndex: 1,
                    RoleInBeat: "supporting_source_sentence",
                    NarrativeFunction: "withhold_answer",
                    NodeIds: ["node-workspace-market-s1"])
            ]);

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 1),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("source_variant_1", candidate.Strategy);
        Assert.False(candidate.Draft.ReadyForInsertion);
        Assert.False(candidate.Draft.Audit.Passed);
        Assert.Equal(currentDraft, candidate.Draft.ChapterTextAfterInsertion);
        Assert.Equal(
            ["node-project-doorway-s1", "node-workspace-market-s1"],
            candidate.Draft.Pieces.Select(piece => piece.NodeId).ToArray());
        Assert.Contains(candidate.Draft.Audit.Errors, error =>
            error.StartsWith("transition_piece_replacement_required:", StringComparison.Ordinal));
        Assert.NotNull(candidate.NextAction);
        var nextAction = candidate.NextAction!;
        Assert.Equal(ReferenceCorpusDraftCandidateNextActions.RegenerateBlueprint, nextAction.Action);
        Assert.Equal("transition_repair_failed", nextAction.ReasonCode);
        Assert.Equal("node-project-doorway-s1", nextAction.RejectedNodeId);
        Assert.Equal("node-project-doorway-s2", nextAction.ReplacementNodeId);
        Assert.Contains("transition_repair_failed", nextAction.Feedback.ProblemTags);
        Assert.Contains("transition_replacement_required", nextAction.Feedback.ProblemTags);
    }

    [Fact]
    public async Task GenerateInsertionDraftCandidatesBlockWhenSelectedBlueprintSourceIsUnavailable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("正文候选缺源阻断测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        var projectLibraryId = "project:" + novel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default";
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TopicEmbeddingClient(defaultDimensions: 3));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-unavailable-source-blueprint",
            QueryContextHash: "unavailable-source-query",
            Strategy: "selected_unavailable_source_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-unavailable-source-beat-1",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-doorway-s1"]),
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-unavailable-source-beat-2",
                    BeatIndex: 1,
                    RoleInBeat: "supporting_source_sentence",
                    NarrativeFunction: "withhold_answer",
                    NodeIds: ["node-workspace-market-s1"])
            ]);

        var result = await service.GenerateInsertionDraftCandidatesAsync(
            new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [projectLibraryId],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: [202]),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint,
                RequestedCount: 2),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.Draft.ReadyForInsertion);
        Assert.False(candidate.Draft.Gate.Passed);
        Assert.False(candidate.Draft.Audit.Passed);
        Assert.Equal("source_node_missing", candidate.Draft.Gate.Status);
        Assert.Equal("source_node_missing", candidate.Draft.Audit.Status);
        Assert.Contains("source_node_missing", candidate.Draft.Gate.Errors);
        Assert.Contains("source_node_missing", candidate.Draft.Audit.Errors);
        Assert.Empty(candidate.Draft.Pieces);
        Assert.Equal(currentDraft, candidate.Draft.ChapterTextAfterInsertion);
        Assert.DoesNotContain("node-project-doorway-s2", candidate.Draft.AssembledText, StringComparison.Ordinal);
        Assert.DoesNotContain("node-workspace-market-s2", candidate.Draft.AssembledText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateInsertionDraftFromSelectedBlueprintKeepsFarTechniqueNodeAvailable()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("远位置技法蓝图正文测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在旧市集边缘。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        await SeedCrossLibraryWritingFixtureAsync(options, novel.Id);
        await AddFarTechniqueSelectedBlueprintFixtureAsync(options);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new TechniqueIntentEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-far-technique-blueprint",
            QueryContextHash: "far-technique-query",
            Strategy: "selected_far_technique_test",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-far-technique-beat",
                    BeatIndex: 0,
                    RoleInBeat: "opening_source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: ["node-project-far-technique"])
            ]);

        var draft = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写旧市集门口对峙，用动作替代心理描写表现愤怒，不直说生气。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "旧市集起火，有人在火光里靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    LibraryIds: [],
                    ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                    IncludeAnchorIds: [],
                    ExcludeAnchorIds: []),
                SlotValues: new Dictionary<string, string>
                {
                    ["她"] = "秦砚",
                    ["他"] = "秦砚"
                },
                SelectedBlueprint: selectedBlueprint),
            CancellationToken.None);

        Assert.True(draft.ReadyForInsertion);
        var piece = Assert.Single(draft.Pieces);
        Assert.Equal("node-project-far-technique", piece.NodeId);
        Assert.True(piece.PreservedHashMatches);
        Assert.True(draft.Gate.Passed);
        Assert.DoesNotContain("source_node_missing", draft.Gate.Errors, StringComparer.Ordinal);
        Assert.True(await BlueprintBeatPieceExistsAsync(options, "selected-far-technique-beat", "node-project-far-technique"));
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

    private ValueTask<(ReferenceCorpusInsertionDraftPayload Result, string CurrentDraft)> GenerateTwoPieceTransitionAuditDraftAsync(
        IReferenceCorpusTextAssembler? textAssembler)
    {
        return GenerateTransitionAuditDraftAsync(
            textAssembler,
            new FirstTwoSourceBlueprintAssembler(),
            [
                "她没有立刻开口。",
                "雨声贴着门缝往里挤。"
            ]);
    }

    private async ValueTask<(ReferenceCorpusInsertionDraftPayload Result, string CurrentDraft)> GenerateTransitionAuditDraftAsync(
        IReferenceCorpusTextAssembler? textAssembler,
        IReferenceCorpusBlueprintAssembler blueprintAssembler,
        IReadOnlyList<string> sourceParagraphs)
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("语料过渡负例审计测试", "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在门里。";
        await chapters.SaveContentAsync(new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "audit-transition-negative.md",
            "# 第一章" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, sourceParagraphs));
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "过渡负例审计语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(
            options,
            corpus,
            chapters,
            blueprints: blueprintAssembler,
            textAssembler: textAssembler);

        var result = await service.GenerateInsertionDraftAsync(
            new GenerateReferenceCorpusInsertionDraftPayload(
                NaturalLanguageGoal: "写门口对峙，压住怒意，不立刻开口，雨声逼近。",
                ChapterContext: new CurrentChapterContextPayload(
                    novel.Id,
                    chapter.ChapterNumber,
                    currentDraft,
                    currentDraft.Length,
                    "门外有人靠近。",
                    [new CharacterStateSnapshotPayload("秦砚", "guarded", ["门外有人靠近"], ["对方真实目的"])]),
                Scope: new ReferenceCorpusScopePayload(
                    [libraryId],
                    [ReferenceCorpusReusePolicies.VerbatimOk],
                    [],
                    []),
                SlotValues: new Dictionary<string, string>()),
            CancellationToken.None);
        return (result, currentDraft);
    }

    private static GenerateReferenceCorpusBlueprintCandidatesPayload BuildCrossLibraryBlueprintCandidatesPayload(
        long novelId,
        int chapterNumber,
        string currentDraft)
    {
        return new GenerateReferenceCorpusBlueprintCandidatesPayload(
            NaturalLanguageGoal: "写旧市集门口对峙，秦砚压住怒意，不立刻开口。",
            ChapterContext: new CurrentChapterContextPayload(
                novelId,
                chapterNumber,
                currentDraft,
                currentDraft.Length,
                "旧市集起火，有人在火光里靠近。",
                [new CharacterStateSnapshotPayload("秦砚", "guarded", ["市集起火"], ["对方真实目的"])]),
            Scope: new ReferenceCorpusScopePayload(
                LibraryIds: [],
                ReusePolicies: [ReferenceCorpusReusePolicies.VerbatimOk],
                IncludeAnchorIds: [],
                ExcludeAnchorIds: []),
            RequestedCount: 3,
            Feedback: null);
    }

    private static GenerateReferenceCorpusBlueprintCandidatesPayload BuildCrossLibraryGoldenBlueprintCandidatesPayload(
        long novelId,
        int chapterNumber,
        JsonElement chapterSpec,
        JsonElement requestSpec)
    {
        return new GenerateReferenceCorpusBlueprintCandidatesPayload(
            NaturalLanguageGoal: requestSpec.GetProperty("natural_language_goal").GetString() ?? string.Empty,
            ChapterContext: new CurrentChapterContextPayload(
                novelId,
                chapterNumber,
                chapterSpec.GetProperty("current_draft_text").GetString() ?? string.Empty,
                chapterSpec.GetProperty("insertion_offset").GetInt32(),
                requestSpec.GetProperty("previous_chapter_summary").GetString(),
                ReadCharacterSnapshots(requestSpec.GetProperty("character_snapshots"))),
            Scope: new ReferenceCorpusScopePayload(
                LibraryIds: [],
                ReusePolicies: ReadStringArray(requestSpec.GetProperty("reuse_policies")),
                IncludeAnchorIds: [],
                ExcludeAnchorIds: []),
            RequestedCount: requestSpec.GetProperty("requested_blueprint_count").GetInt32(),
            Feedback: null);
    }

    private static ReferenceCorpusBlueprintFeedbackPayload BuildCrossLibraryFeedbackPayload(
        JsonElement feedbackSpec,
        ReferenceCorpusBlueprintCandidatePayload rejected,
        long novelId)
    {
        return new ReferenceCorpusBlueprintFeedbackPayload(
            RejectedBlueprintIds: [rejected.Blueprint.BlueprintId],
            RejectedNodeIds: rejected.Blueprint.Beats.SelectMany(beat => beat.NodeIds).ToArray(),
            AvoidLibraryIds: ReadRenderedStringArray(feedbackSpec.GetProperty("avoid_library_ids"), novelId),
            AvoidAnchorIds: ReadLongArray(feedbackSpec.GetProperty("avoid_anchor_ids")),
            ProblemTags: ReadStringArray(feedbackSpec.GetProperty("problem_tags")),
            Notes: feedbackSpec.GetProperty("notes").GetString());
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

    private static async ValueTask InsertReferenceTextNodeAsync(
        AppInitializationOptions options,
        long anchorId,
        string nodeId,
        string text)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ($node_id, $anchor_id, NULL, 'sentence', 1, 1,
               1, 0, $end_offset, $char_len, $text_hash, $text, '2026-07-09T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$end_offset", text.Length);
        command.Parameters.AddWithValue("$char_len", text.Length);
        command.Parameters.AddWithValue("$text_hash", StableTextHash(text));
        command.Parameters.AddWithValue("$text", text);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async ValueTask<SlotTransferConstraintFixture> CreateSlotTransferConstraintFixtureAsync(
        string title,
        string transferSlotsJson,
        string reviewState = "confirmed")
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var chapters = new FileSystemChapterContentService(options, novels);
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload(title, "", ""), CancellationToken.None);
        var chapter = await chapters.CreateChapterAsync(new CreateChapterPayload(novel.Id, "第一章"), CancellationToken.None);
        const string currentDraft = "秦砚停在黑塔门前。";
        await chapters.SaveContentAsync(
            new SaveContentPayload(novel.Id, chapter.FilePath, currentDraft),
            CancellationToken.None);
        const string sourceSentence = "她在旧市集门口没有立刻开口，只叫了一声师兄，把钥匙扣在掌心，《旧市集门口师兄钥匙案》没有改。";
        var sourcePath = CreateSourceFile(
            "slot-transfer-constraints-" + StableTextHash(title)[..12] + ".md",
            $$"""
            # 第一章 槽位约束

            {{sourceSentence}}
            """);
        var anchors = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchors.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                title + "语料",
                null,
                sourcePath,
                "markdown",
                "public_domain"),
            CancellationToken.None);
        await AllowVerbatimInsertionAsync(options, anchor.AnchorId);
        var libraryId = await ReadDefaultLibraryIdAsync(options, anchor.AnchorId);
        var nodeId = "node-slot-transfer-" + StableTextHash(title)[..16];
        await InsertReferenceTextNodeAsync(options, anchor.AnchorId, nodeId, sourceSentence);
        await InsertTechniqueSpecimenTransferSlotsAsync(options, anchor.AnchorId, nodeId, transferSlotsJson, reviewState);
        var corpus = new SqliteReferenceCorpusService(
            options,
            new StaticEmbeddingConfigurationService(CreateEmbeddingOptions()),
            new DeterministicHashEmbeddingClient(defaultDimensions: 8));
        var service = new SqliteReferenceCorpusWritingService(options, corpus, chapters);
        var selectedBlueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "selected-slot-transfer-" + StableTextHash(title)[..12],
            QueryContextHash: "query-slot-transfer-" + StableTextHash(title)[..12],
            Strategy: "selected_slot_transfer_fixture",
            Beats:
            [
                new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "selected-slot-transfer-beat-" + StableTextHash(title)[..12],
                    BeatIndex: 0,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: "raise_pressure",
                    NodeIds: [nodeId])
            ]);

        return new SlotTransferConstraintFixture(
            NovelId: novel.Id,
            ChapterNumber: chapter.ChapterNumber,
            CurrentDraft: currentDraft,
            LibraryId: libraryId,
            NodeId: nodeId,
            SelectedBlueprint: selectedBlueprint,
            Service: service);
    }

    private static GenerateReferenceCorpusInsertionDraftCandidatesPayload BuildSlotTransferConstraintRequest(
        SlotTransferConstraintFixture fixture,
        bool includePlotObject)
    {
        var slots = new Dictionary<string, string>
        {
            ["character:她"] = "秦砚",
            ["place:旧市集门口"] = "黑塔门前",
            ["honorific:师兄"] = "队长"
        };
        if (includePlotObject)
        {
            slots["plot_object:钥匙"] = "铜令";
        }

        return new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
            NaturalLanguageGoal: "写门口对峙，秦砚压住怒意，没有立刻开口。",
            ChapterContext: new CurrentChapterContextPayload(
                fixture.NovelId,
                fixture.ChapterNumber,
                fixture.CurrentDraft,
                fixture.CurrentDraft.Length,
                "黑塔门前，秦砚需要压住情绪。",
                [new CharacterStateSnapshotPayload("秦砚", "guarded", ["队长在场"], ["铜令真正用途"])]),
            Scope: new ReferenceCorpusScopePayload(
                [fixture.LibraryId],
                [ReferenceCorpusReusePolicies.VerbatimOk],
                [],
                []),
            SlotValues: new Dictionary<string, string>(),
            SelectedBlueprint: fixture.SelectedBlueprint,
            RequestedCount: 1,
            SlotValueVariants:
            [
                new ReferenceCorpusDraftSlotValueVariantPayload(
                    VariantId: "strict-current-scene",
                    Label: "黑塔队长铜令",
                    SlotValues: slots)
            ]);
    }

    private static GenerateReferenceCorpusInsertionDraftCandidatesPayload BuildSlotTransferConstraintAutoRequest(
        SlotTransferConstraintFixture fixture,
        int requestedCount,
        IReadOnlyList<CharacterStateSnapshotPayload> characterSnapshots)
    {
        return new GenerateReferenceCorpusInsertionDraftCandidatesPayload(
            NaturalLanguageGoal: "写门口对峙，当前人物压住怒意，没有立刻开口。",
            ChapterContext: new CurrentChapterContextPayload(
                fixture.NovelId,
                fixture.ChapterNumber,
                fixture.CurrentDraft,
                fixture.CurrentDraft.Length,
                "黑塔门前，当前人物需要压住情绪。",
                characterSnapshots),
            Scope: new ReferenceCorpusScopePayload(
                [fixture.LibraryId],
                [ReferenceCorpusReusePolicies.VerbatimOk],
                [],
                []),
            SlotValues: new Dictionary<string, string>(),
            SelectedBlueprint: fixture.SelectedBlueprint,
            RequestedCount: requestedCount,
            SlotValueVariants: null);
    }

    private static async ValueTask InsertTechniqueSpecimenTransferSlotsAsync(
        AppInitializationOptions options,
        long anchorId,
        string nodeId,
        string transferSlotsJson,
        string reviewState = "confirmed")
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        var runId = "run-transfer-slots-" + StableTextHash(nodeId)[..16];
        var specimenId = "specimen-transfer-slots-" + StableTextHash(nodeId, transferSlotsJson)[..16];
        command.CommandText = """
            INSERT OR IGNORE INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ($run_id, $anchor_id, 'fake-transfer-slots-v1', 'corpus-v1', 'fake', 'fake-model',
               'technique_specimen', 'completed', 100, 8, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 0);

            INSERT OR REPLACE INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ($specimen_id, $node_id, $anchor_id, $run_id, 'slot_transfer_contract',
               '用槽位边界约束可迁移文本，避免把未声明语义成分随意替换',
               '当前章节需要复用原句结构但只能替换声明槽位',
               '[character]在[place]没有立刻开口，只叫了一声[honorific]。',
               $transfer_slots_json,
               '让正文候选保留原句情绪和叙述稳定性',
               '["槽位已明确声明"]',
               '["替换未声明槽位会破坏技法边界"]',
               '["把任意名词都当作可替换槽位"]',
               NULL,
               '{"contributing_factors":[]}',
               0.97, $review_state, 'active', NULL,
               'transfer_slots 写作侧约束 fixture。',
               '2026-07-09T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$specimen_id", specimenId);
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        command.Parameters.AddWithValue("$transfer_slots_json", transferSlotsJson);
        command.Parameters.AddWithValue("$review_state", reviewState);
        await command.ExecuteNonQueryAsync();
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

    private static async ValueTask SetInsertionClearanceAsync(
        AppInitializationOptions options,
        long anchorId,
        bool clearedForInsertion)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_source_license
            SET reuse_policy = 'adapted_only',
                license_state = 'authorized',
                max_verbatim_ratio = 1.0,
                cleared_for_insertion = $cleared_for_insertion
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$cleared_for_insertion", clearedForInsertion ? 1 : 0);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async ValueTask<string> ReadFirstSentenceNodeIdAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id
            FROM reference_text_nodes
            WHERE anchor_id = $anchor_id
              AND node_type = 'sentence'
            ORDER BY sequence_index, node_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static async ValueTask SeedCrossLibraryWritingFixtureAsync(
        AppInitializationOptions options,
        long novelId)
    {
        Directory.CreateDirectory(Path.Combine(options.DefaultDataDirectory, "reference-anchor"));
        await using var connection = await OpenReferenceConnectionAsync(options);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using var command = connection.CreateCommand();
        var projectLibraryId = "project:" + novelId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default";
        command.CommandText = """
            INSERT OR IGNORE INTO reference_anchors
              (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
               source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
            VALUES
              (201, $novel_id, '项目门口语料', '', 'project-doorway.md', 'markdown', 'public_domain',
               'source-hash-201', 'corpus-writing-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'private'),
              (202, NULL, '工作区火市语料', '', 'workspace-market.md', 'markdown', 'public_domain',
               'source-hash-202', 'corpus-writing-fixture', 'ready', '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', 'workspace');

            INSERT OR IGNORE INTO reference_corpus_libraries
              (library_id, scope, novel_id, name, created_at)
            VALUES
              ($project_library_id, 'project', $novel_id, 'Project corpus', '2026-07-09T00:00:00Z'),
              ('global:workspace', 'global', NULL, 'Workspace corpus', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_session_library_binding
              (session_id, library_id)
            VALUES
              ($project_library_id, $project_library_id),
              ($project_library_id, 'global:workspace');

            INSERT OR IGNORE INTO reference_library_members
              (library_id, anchor_id, enabled, source_quality, dedup_group_id)
            VALUES
              ($project_library_id, 201, 1, 'trusted', 'project-doorway'),
              ('global:workspace', 202, 1, 'trusted', 'workspace-market');

            INSERT OR IGNORE INTO reference_source_license
              (anchor_id, license_state, authorization_evidence, reuse_policy,
               max_verbatim_ratio, cleared_for_insertion, reviewed_at)
            VALUES
              (201, 'public_domain', 'fixture', 'verbatim_ok', 1.00, 1, '2026-07-09T00:00:00Z'),
              (202, 'public_domain', 'fixture', 'verbatim_ok', 1.00, 1, '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-project-doorway-s1', 201, NULL, 'sentence', 1, 1,
               1, 0, 18, 18, 'sha256-project-doorway-s1', '她没有立刻开口，只把钥匙扣在掌心。', '2026-07-09T00:00:00Z'),
              ('node-project-doorway-s2', 201, NULL, 'sentence', 2, 1,
               1, 19, 34, 15, 'sha256-project-doorway-s2', '她把门栓慢慢推回去。', '2026-07-09T00:00:00Z'),
              ('node-project-doorway-s3', 201, NULL, 'sentence', 3, 1,
               1, 35, 51, 16, 'sha256-project-doorway-s3', '她的影子贴在门槛内侧。', '2026-07-09T00:00:00Z'),
              ('node-project-doorway-s4', 201, NULL, 'sentence', 4, 1,
               1, 52, 69, 17, 'sha256-project-doorway-s4', '她只垂眼看着掌心旧痕。', '2026-07-09T00:00:00Z'),
              ('node-workspace-market-s1', 202, NULL, 'sentence', 1, 1,
               1, 0, 21, 21, 'sha256-workspace-market-s1', '火光在旧市集尽头一压，他没有立刻回头。', '2026-07-09T00:00:00Z'),
              ('node-workspace-market-s2', 202, NULL, 'sentence', 2, 1,
               1, 22, 39, 17, 'sha256-workspace-market-s2', '摊棚的灰烬沿着鞋边滚。', '2026-07-09T00:00:00Z');
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$project_library_id", projectLibraryId);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask AddStructuredTechniqueSearchFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-project-action-anger-s1', 201, NULL, 'sentence', 11, 1,
               1, 100, 119, 19, 'sha256-project-action-anger-s1', '她捏紧拳骨，没有把怒意说出口。', '2026-07-09T00:00:00Z'),
              ('node-project-action-anger-s2', 201, NULL, 'sentence', 12, 1,
               1, 120, 136, 16, 'sha256-project-action-anger-s2', '她把掌心旧痕压进袖口。', '2026-07-09T00:00:00Z'),
              ('node-project-direct-anger-s1', 201, NULL, 'sentence', 13, 1,
               1, 137, 153, 16, 'sha256-project-direct-anger-s1', '她心里很生气，几乎想喊。', '2026-07-09T00:00:00Z'),
              ('node-workspace-action-anger-s1', 202, NULL, 'sentence', 11, 1,
               1, 100, 120, 20, 'sha256-workspace-action-anger-s1', '他按住裂开的指节，声音反而低下去。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ('run-structured-technique-201', 201, 'fake-m3-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 18, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 5),
              ('run-structured-technique-202', 202, 'fake-m3-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 8, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 2);

            INSERT OR IGNORE INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ('obs-project-action-carrier-1', 'node-project-action-anger-s1', 'sentence',
               'run-structured-technique-201', 201, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{"emotion":"suppressed_anger","carrier":"fist_clench"}', 0.92, 0.96,
               0, 5, '用捏紧拳骨替代心理描写表现怒意。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-project-touch-1', 'node-project-action-anger-s1', 'sentence',
               'run-structured-technique-201', 201, 'sensory', 'senses',
               'array', 'tactile', NULL, NULL, '[{"sense":"tactile","intensity":0.9}]', 0.90, 0.94,
               0, 5, '拳骨触觉承载压抑怒意。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-project-action-carrier-2', 'node-project-action-anger-s2', 'sentence',
               'run-structured-technique-201', 201, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{"emotion":"suppressed_anger","carrier":"scar_pressure"}', 0.84, 0.91,
               0, 9, '用掌心旧痕动作替代直白愤怒。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-project-touch-2', 'node-project-action-anger-s2', 'sentence',
               'run-structured-technique-201', 201, 'sensory', 'senses',
               'array', 'tactile', NULL, NULL, '[{"sense":"tactile","intensity":0.84}]', 0.84, 0.90,
               0, 9, '掌心旧痕提供触觉压力。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-project-direct-anger-1', 'node-project-direct-anger-s1', 'sentence',
               'run-structured-technique-201', 201, 'emotion', 'emotion_state',
               'enum', 'direct_anger', NULL, NULL, '{"surface":"angry","mode":"direct"}', 0.55, 0.92,
               3, 8, '直白说明生气，不是动作承载。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-workspace-action-carrier-1', 'node-workspace-action-anger-s1', 'sentence',
               'run-structured-technique-202', 202, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{"emotion":"suppressed_anger","carrier":"finger_joint_pressure"}', 0.90, 0.95,
               0, 9, '用按住指节替代心理描写。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-workspace-touch-1', 'node-workspace-action-anger-s1', 'sentence',
               'run-structured-technique-202', 202, 'sensory', 'senses',
               'array', 'tactile', NULL, NULL, '[{"sense":"tactile","intensity":0.88}]', 0.88, 0.93,
               0, 9, '裂开的指节提供触觉压力。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_obs_sensory
              (observation_id, node_id, anchor_id, sense, intensity)
            VALUES
              ('obs-project-touch-1', 'node-project-action-anger-s1', 201, 'tactile', 0.90),
              ('obs-project-touch-2', 'node-project-action-anger-s2', 201, 'tactile', 0.84),
              ('obs-workspace-touch-1', 'node-workspace-action-anger-s1', 202, 'tactile', 0.88);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask AddFeedbackRhythmSearchFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-feedback-fast-market-s1', 202, NULL, 'sentence', 3, 1,
               1, 40, 49, 9, 'sha256-feedback-fast-market-s1', '旧市集火光一闪。', '2026-07-09T00:00:00Z'),
              ('node-feedback-slow-project-s1', 201, NULL, 'sentence', 21, 1,
               1, 220, 246, 26, 'sha256-feedback-slow-project-s1', '旧市集尽头的火光慢慢压到门槛前。', '2026-07-09T00:00:00Z'),
              ('node-feedback-slow-workspace-s1', 202, NULL, 'sentence', 21, 1,
               1, 220, 250, 30, 'sha256-feedback-slow-workspace-s1', '旧市集的灰光一层层落下来，他迟迟没有开口。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ('run-feedback-rhythm-201', 201, 'fake-m4-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 6, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 1),
              ('run-feedback-rhythm-202', 202, 'fake-m4-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 12, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 2);

            INSERT OR IGNORE INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ('obs-feedback-fast-rhythm', 'node-feedback-fast-market-s1', 'sentence',
               'run-feedback-rhythm-202', 202, 'rhythm', 'length_band',
               'number', 'short', 9, NULL, '{"feature_key":"length_band","label":"short","char_count":9,"cadence":"steady"}', NULL, 0.95,
               0, 9, '短句节奏偏急。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-feedback-slow-project-rhythm', 'node-feedback-slow-project-s1', 'sentence',
               'run-feedback-rhythm-201', 201, 'rhythm', 'length_band',
               'number', 'medium', 26, NULL, '{"feature_key":"length_band","label":"medium","char_count":26,"cadence":"flowing"}', NULL, 0.96,
               0, 26, '中长句拖慢压迫感。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-feedback-slow-workspace-rhythm', 'node-feedback-slow-workspace-s1', 'sentence',
               'run-feedback-rhythm-202', 202, 'rhythm', 'length_band',
               'number', 'medium', 30, NULL, '{"feature_key":"length_band","label":"medium","char_count":30,"cadence":"flowing"}', NULL, 0.96,
               0, 30, '中长句与迟迟不开口形成慢压迫。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask AddM4BlueprintStrategyFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-m4-emotion-project-s1', 201, NULL, 'sentence', 101, 1,
               1, 2100, 2124, 24, 'sha256-m4-emotion-project-s1', '她把怒意压回喉间，只低头扣紧掌心。', '2026-07-09T00:00:00Z'),
              ('node-m4-emotion-workspace-s1', 202, NULL, 'sentence', 101, 1,
               1, 2100, 2125, 25, 'sha256-m4-emotion-workspace-s1', '他没有争辩，只把裂开的指节藏进袖里。', '2026-07-09T00:00:00Z'),
              ('node-m4-rhythm-project-s1', 201, NULL, 'sentence', 102, 1,
               1, 2130, 2162, 32, 'sha256-m4-rhythm-project-s1', '门槛外的火光一寸寸压过来，她始终没有抬眼。', '2026-07-09T00:00:00Z'),
              ('node-m4-rhythm-workspace-s1', 202, NULL, 'sentence', 102, 1,
               1, 2130, 2163, 33, 'sha256-m4-rhythm-workspace-s1', '旧市集的灰风绕过摊棚，他隔了很久才动一下手指。', '2026-07-09T00:00:00Z'),
              ('node-m4-technique-project-s1', 201, NULL, 'sentence', 103, 1,
               1, 2170, 2194, 24, 'sha256-m4-technique-project-s1', '她转了转杯沿，怒意只落在指腹上。', '2026-07-09T00:00:00Z'),
              ('node-m4-technique-workspace-s1', 202, NULL, 'sentence', 103, 1,
               1, 2170, 2196, 26, 'sha256-m4-technique-workspace-s1', '他把袖扣慢慢按平，屋里没人再问第二句。', '2026-07-09T00:00:00Z'),
              ('node-m4-scene-project-s1', 201, NULL, 'sentence', 104, 1,
               1, 2200, 2225, 25, 'sha256-m4-scene-project-s1', '门里门外隔着一道影子，答案被压在火光后。', '2026-07-09T00:00:00Z'),
              ('node-m4-scene-workspace-s1', 202, NULL, 'sentence', 104, 1,
               1, 2200, 2226, 26, 'sha256-m4-scene-workspace-s1', '旧市集忽然静下去，所有人都等他先回头。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ('run-m4-strategy-201', 201, 'fake-m4-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 18, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 8),
              ('run-m4-strategy-202', 202, 'fake-m4-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 18, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 8);

            INSERT OR IGNORE INTO reference_feature_observations
              (observation_id, node_id, node_type, run_id, anchor_id, feature_family, feature_key,
               value_kind, value_text, value_num, value_bool, value_json, intensity, confidence,
               evidence_start, evidence_end, explanation, review_state, validity_state,
               superseded_by_run_id, created_at)
            VALUES
              ('obs-m4-action-emotion-project', 'node-m4-emotion-project-s1', 'sentence',
               'run-m4-strategy-201', 201, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{}', 0.92, 0.91,
               0, 8, '动作承载压抑怒意。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-emotion-project', 'node-m4-emotion-project-s1', 'sentence',
               'run-m4-strategy-201', 201, 'emotion', 'emotion_state',
               'enum', 'restrained_anger', NULL, NULL, '{}', 0.94, 0.99,
               0, 8, '克制怒意明确。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-action-emotion-workspace', 'node-m4-emotion-workspace-s1', 'sentence',
               'run-m4-strategy-202', 202, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{}', 0.90, 0.90,
               0, 8, '动作承载压抑怒意。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-emotion-workspace', 'node-m4-emotion-workspace-s1', 'sentence',
               'run-m4-strategy-202', 202, 'emotion', 'emotion_state',
               'enum', 'restrained_anger', NULL, NULL, '{}', 0.93, 0.98,
               0, 8, '克制怒意明确。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),

              ('obs-m4-action-rhythm-project', 'node-m4-rhythm-project-s1', 'sentence',
               'run-m4-strategy-201', 201, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{}', 0.88, 0.89,
               0, 8, '动作压住情绪。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-rhythm-project', 'node-m4-rhythm-project-s1', 'sentence',
               'run-m4-strategy-201', 201, 'rhythm', 'length_band',
               'number', 'long', 32, NULL, '{}', NULL, 0.99,
               0, 32, '长句形成慢压迫。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-action-rhythm-workspace', 'node-m4-rhythm-workspace-s1', 'sentence',
               'run-m4-strategy-202', 202, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{}', 0.88, 0.89,
               0, 8, '动作压住情绪。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-rhythm-workspace', 'node-m4-rhythm-workspace-s1', 'sentence',
               'run-m4-strategy-202', 202, 'rhythm', 'length_band',
               'number', 'long', 33, NULL, '{}', NULL, 0.98,
               0, 33, '长句形成慢压迫。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),

              ('obs-m4-action-technique-project', 'node-m4-technique-project-s1', 'sentence',
               'run-m4-strategy-201', 201, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{}', 0.91, 0.90,
               0, 8, '细节动作承载情绪。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-action-technique-workspace', 'node-m4-technique-workspace-s1', 'sentence',
               'run-m4-strategy-202', 202, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{}', 0.91, 0.90,
               0, 8, '细节动作承载情绪。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),

              ('obs-m4-action-scene-project', 'node-m4-scene-project-s1', 'sentence',
               'run-m4-strategy-201', 201, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{}', 0.82, 0.88,
               0, 8, '动作压在场景留白里。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-scene-project', 'node-m4-scene-project-s1', 'sentence',
               'run-m4-strategy-201', 201, 'narrative', 'narrative_function',
               'enum', 'withhold_answer', NULL, NULL, '{}', 0.95, 0.99,
               0, 8, '场景留白扣住答案。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-action-scene-workspace', 'node-m4-scene-workspace-s1', 'sentence',
               'run-m4-strategy-202', 202, 'action', 'emotion_carrier',
               'enum', 'action_over_psychology', NULL, NULL, '{}', 0.82, 0.88,
               0, 8, '动作压在场景留白里。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z'),
              ('obs-m4-scene-workspace', 'node-m4-scene-workspace-s1', 'sentence',
               'run-m4-strategy-202', 202, 'narrative', 'narrative_function',
               'enum', 'withhold_answer', NULL, NULL, '{}', 0.95, 0.98,
               0, 8, '场景留白扣住答案。', 'confirmed', 'active', NULL, '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ('specimen-m4-technique-project', 'node-m4-technique-project-s1', 201, 'run-m4-strategy-201',
               'emotion_carrier',
               '用细节动作承载压抑愤怒，以沉默留白替代直接心理描写',
               '旧市集门口对峙，角色必须压住怒意',
               '[角色] [身体细节动作]，[场面留白]。',
               '["角色","身体细节动作","场面留白"]',
               '让读者从动作判断情绪，而不是读到直接说明',
               '["已有冲突压力"]',
               '["动作与冲突无关"]',
               '["直接写他很愤怒"]',
               NULL,
               '{"contributing_factors":[]}',
               0.98, 'confirmed', 'active', NULL,
               'M4 技法多样性策略 fixture。',
               '2026-07-09T00:00:00Z'),
              ('specimen-m4-technique-workspace', 'node-m4-technique-workspace-s1', 202, 'run-m4-strategy-202',
               'emotion_carrier',
               '用身体细节动作承载压抑愤怒，不直接说明情绪',
               '旧市集门口对峙，角色必须压住怒意',
               '[角色] [身体细节动作]，[场面留白]。',
               '["角色","身体细节动作","场面留白"]',
               '让读者从动作判断情绪，而不是读到直接说明',
               '["已有冲突压力"]',
               '["动作与冲突无关"]',
               '["直接写他很愤怒"]',
               NULL,
               '{"contributing_factors":[]}',
               0.98, 'confirmed', 'active', NULL,
               'M4 技法多样性策略 fixture。',
               '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask AddSourceRepetitionSearchFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              ('node-source-repeat-workspace-s1', 202, NULL, 'sentence', 31, 1,
               1, 620, 642, 22, 'sha256-source-repeat-workspace-s1', '旧市集的火光压着门口，他仍旧没有回头。', '2026-07-09T00:00:00Z'),
              ('node-source-repeat-workspace-s2', 202, NULL, 'sentence', 32, 1,
               1, 643, 664, 21, 'sha256-source-repeat-workspace-s2', '旧市集火光又暗了一寸，他只扣紧袖口。', '2026-07-09T00:00:00Z'),
              ('node-source-repeat-workspace-s3', 202, NULL, 'sentence', 33, 1,
               1, 665, 686, 21, 'sha256-source-repeat-workspace-s3', '火光从旧市集棚顶落下，他迟迟不开口。', '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask AddFarTechniqueSelectedBlueprintFixtureAsync(AppInitializationOptions options)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        var fillerNodes = string.Join(
            "," + Environment.NewLine,
            Enumerable.Range(20, 340).Select(index =>
                $"('node-project-far-filler-{index}', 201, NULL, 'sentence', {index}, 1, 1, {index * 20}, {index * 20 + 12}, 12, 'sha256-project-far-filler-{index}', '门口低压铺垫第{index}句。', '2026-07-09T00:00:00Z')"));
        command.CommandText = $$"""
            INSERT OR IGNORE INTO reference_text_nodes
              (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
               chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
            VALUES
              {{fillerNodes}},
              ('node-project-far-technique', 201, NULL, 'sentence', 400, 1,
               1, 8000, 8024, 24, 'sha256-project-far-technique', '她把杯沿转了半圈，屋里谁都没有作声。', '2026-07-09T00:00:00Z');

            INSERT OR IGNORE INTO reference_analysis_runs
              (run_id, anchor_id, analyzer_version, schema_version, model_provider, model_id,
               scope, status, token_budget, tokens_spent, resume_cursor, started_at, completed_at, observation_count)
            VALUES
              ('run-far-technique-201', 201, 'fake-m3-v1', 'corpus-v1', 'fake', 'fake-model',
               'sentence', 'completed', 100, 8, NULL, '2026-07-09T00:00:00Z', '2026-07-09T00:00:01Z', 0);

            INSERT OR IGNORE INTO reference_technique_specimens
              (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
               technique_abstract, trigger_context, transfer_template, transfer_slots_json,
               effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
               world_context_dependencies, why_it_works_json, confidence, review_state,
               validity_state, superseded_by_run_id, mastery_notes, created_at)
            VALUES
              ('specimen-writing-far-action-over-psychology', 'node-project-far-technique', 201, 'run-far-technique-201',
               'emotion_carrier',
               '用细节动作承载压抑愤怒，以沉默留白替代直接心理描写',
               '角色不能直接爆发，但场面需要让读者感到怒意已经压到临界点',
               '[角色] [身体细节动作]，[场面留白]。',
               '["角色","身体细节动作","场面留白"]',
               '把情绪判断交给读者完成，减少直白解释，增强压抑张力',
               '["已有冲突压力","动作能承载角色欲望或克制"]',
               '["动作与冲突无关时会显得空泛"]',
               '["直接补一句他很愤怒"]',
               NULL,
               '{"contributing_factors":[]}',
               0.97, 'confirmed', 'active', NULL,
               '远位置技法标本必须能在 selected blueprint 正文生成阶段读到。',
               '2026-07-09T00:00:00Z');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask SeedCrossLibraryWritingFixtureAsync(
        AppInitializationOptions options,
        long novelId,
        JsonElement corpusSpec)
    {
        Directory.CreateDirectory(Path.Combine(options.DefaultDataDirectory, "reference-anchor"));
        await using var connection = await OpenReferenceConnectionAsync(options);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, CancellationToken.None);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        foreach (var librarySpec in corpusSpec.GetProperty("libraries").EnumerateArray())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO reference_corpus_libraries
                  (library_id, scope, novel_id, name, created_at)
                VALUES
                  ($library_id, $scope, $novel_id, $name, '2026-07-09T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$library_id", RenderFixtureId(librarySpec.GetProperty("library_id").GetString() ?? string.Empty, novelId));
            command.Parameters.AddWithValue("$scope", librarySpec.GetProperty("scope").GetString() ?? "global");
            command.Parameters.AddWithValue("$novel_id", NovelIdParameter(librarySpec.GetProperty("novel_id_scope").GetString(), novelId));
            command.Parameters.AddWithValue("$name", librarySpec.GetProperty("name").GetString() ?? "Golden corpus");
            await command.ExecuteNonQueryAsync();
        }

        foreach (var bindingSpec in corpusSpec.GetProperty("session_bindings").EnumerateArray())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO reference_session_library_binding
                  (session_id, library_id)
                VALUES
                  ($session_id, $library_id);
                """;
            command.Parameters.AddWithValue("$session_id", RenderFixtureId(bindingSpec.GetProperty("session_id").GetString() ?? string.Empty, novelId));
            command.Parameters.AddWithValue("$library_id", RenderFixtureId(bindingSpec.GetProperty("library_id").GetString() ?? string.Empty, novelId));
            await command.ExecuteNonQueryAsync();
        }

        foreach (var sourceSpec in corpusSpec.GetProperty("sources").EnumerateArray())
        {
            var anchorId = sourceSpec.GetProperty("anchor_id").GetInt64();
            var libraryId = RenderFixtureId(sourceSpec.GetProperty("library_id").GetString() ?? string.Empty, novelId);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO reference_anchors
                      (anchor_id, novel_id, title, author, source_path, source_kind, license_status,
                       source_file_hash, build_version, status, created_at, updated_at, corpus_visibility)
                    VALUES
                      ($anchor_id, $novel_id, $title, '', $source_path, $source_kind, $license_status,
                       $source_file_hash, 'corpus-writing-golden-fixture', 'ready',
                       '2026-07-09T00:00:00Z', '2026-07-09T00:00:00Z', $corpus_visibility);
                    """;
                command.Parameters.AddWithValue("$anchor_id", anchorId);
                command.Parameters.AddWithValue("$novel_id", NovelIdParameter(sourceSpec.GetProperty("novel_id_scope").GetString(), novelId));
                command.Parameters.AddWithValue("$title", sourceSpec.GetProperty("title").GetString() ?? "golden source");
                command.Parameters.AddWithValue("$source_path", sourceSpec.GetProperty("source_path").GetString() ?? string.Empty);
                command.Parameters.AddWithValue("$source_kind", sourceSpec.GetProperty("source_kind").GetString() ?? "markdown");
                command.Parameters.AddWithValue("$license_status", sourceSpec.GetProperty("license_status").GetString() ?? "public_domain");
                command.Parameters.AddWithValue("$source_file_hash", "source-hash-" + anchorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$corpus_visibility", sourceSpec.GetProperty("corpus_visibility").GetString() ?? "workspace");
                await command.ExecuteNonQueryAsync();
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO reference_library_members
                      (library_id, anchor_id, enabled, source_quality, dedup_group_id)
                    VALUES
                      ($library_id, $anchor_id, 1, $source_quality, $dedup_group_id);
                    """;
                command.Parameters.AddWithValue("$library_id", libraryId);
                command.Parameters.AddWithValue("$anchor_id", anchorId);
                command.Parameters.AddWithValue("$source_quality", sourceSpec.GetProperty("source_quality").GetString() ?? "trusted");
                command.Parameters.AddWithValue("$dedup_group_id", sourceSpec.GetProperty("dedup_group_id").GetString() ?? "anchor:" + anchorId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync();
            }

            await using (var command = connection.CreateCommand())
            {
                var gate = sourceSpec.GetProperty("license_gate");
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO reference_source_license
                      (anchor_id, license_state, authorization_evidence, reuse_policy,
                       max_verbatim_ratio, cleared_for_insertion, reviewed_at)
                    VALUES
                      ($anchor_id, $license_state, 'fixture', $reuse_policy,
                       $max_verbatim_ratio, $cleared_for_insertion, '2026-07-09T00:00:00Z');
                    """;
                command.Parameters.AddWithValue("$anchor_id", anchorId);
                command.Parameters.AddWithValue("$license_state", gate.GetProperty("license_state").GetString() ?? ReferenceCorpusLicenseStates.Unknown);
                command.Parameters.AddWithValue("$reuse_policy", gate.GetProperty("reuse_policy").GetString() ?? ReferenceCorpusReusePolicies.ReferenceOnly);
                command.Parameters.AddWithValue("$max_verbatim_ratio", gate.GetProperty("max_verbatim_ratio").GetDouble());
                command.Parameters.AddWithValue("$cleared_for_insertion", gate.GetProperty("cleared_for_insertion").GetBoolean() ? 1 : 0);
                await command.ExecuteNonQueryAsync();
            }

            foreach (var nodeSpec in sourceSpec.GetProperty("nodes").EnumerateArray())
            {
                var text = nodeSpec.GetProperty("text").GetString() ?? string.Empty;
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR REPLACE INTO reference_text_nodes
                      (node_id, anchor_id, parent_node_id, node_type, sequence_index, depth,
                       chapter_index, start_offset, end_offset, char_len, text_hash, text, created_at)
                    VALUES
                      ($node_id, $anchor_id, NULL, 'sentence', $sequence_index, 1,
                       $chapter_index, $start_offset, $end_offset, $char_len, $text_hash, $text, '2026-07-09T00:00:00Z');
                    """;
                command.Parameters.AddWithValue("$node_id", nodeSpec.GetProperty("node_id").GetString() ?? string.Empty);
                command.Parameters.AddWithValue("$anchor_id", anchorId);
                command.Parameters.AddWithValue("$sequence_index", nodeSpec.GetProperty("sequence_index").GetInt32());
                command.Parameters.AddWithValue("$chapter_index", nodeSpec.GetProperty("chapter_index").GetInt32());
                command.Parameters.AddWithValue("$start_offset", nodeSpec.GetProperty("start_offset").GetInt32());
                command.Parameters.AddWithValue("$end_offset", nodeSpec.GetProperty("end_offset").GetInt32());
                command.Parameters.AddWithValue("$char_len", text.Length);
                command.Parameters.AddWithValue("$text_hash", nodeSpec.GetProperty("text_hash").GetString() ?? string.Empty);
                command.Parameters.AddWithValue("$text", text);
                await command.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }

    private static async ValueTask SetLibraryMembersEnabledAsync(
        AppInitializationOptions options,
        string libraryId,
        bool enabled)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_library_members
            SET enabled = $enabled,
                disabled_reason = CASE WHEN $enabled = 1 THEN NULL ELSE 'golden_disabled_variant' END
            WHERE library_id = $library_id;
            """;
        command.Parameters.AddWithValue("$library_id", libraryId);
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        Assert.True(await command.ExecuteNonQueryAsync() > 0);
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

    private sealed record CorpusBlueprintPersistenceRow(
        string BlueprintId,
        string QueryContextHash,
        string AssemblyStrategy,
        double CoverageScore,
        string GapReasonsJson,
        string GapPositionsJson,
        string QueryContextJson,
        string SourceDistributionJson,
        string FeedbackReason);

    private sealed record CorpusBlueprintBeatPersistenceRow(
        string BlueprintId,
        string BeatId,
        int BeatIndex,
        string RoleInBeat,
        string NarrativeFunction);

    private static async ValueTask<IReadOnlyList<CorpusBlueprintPersistenceRow>> ReadCorpusBlueprintRowsAsync(
        AppInitializationOptions options,
        long novelId,
        int chapterNumber)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT blueprint_id,
                   query_context_hash,
                   assembly_strategy,
                   coverage_score,
                   gap_reasons_json,
                   gap_positions_json,
                   query_context_json,
                   source_distribution_json,
                   feedback_reason
            FROM reference_corpus_blueprints
            WHERE novel_id = $novel_id
              AND chapter_number = $chapter_number
            ORDER BY created_at, blueprint_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$chapter_number", chapterNumber);

        var records = new List<CorpusBlueprintPersistenceRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            records.Add(new CorpusBlueprintPersistenceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return records;
    }

    private static async ValueTask<IReadOnlyList<CorpusBlueprintBeatPersistenceRow>> ReadCorpusBlueprintBeatRowsAsync(
        AppInitializationOptions options,
        string blueprintId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT blueprint_id,
                   beat_id,
                   beat_index,
                   role_in_beat,
                   narrative_function
            FROM reference_corpus_blueprint_beats
            WHERE blueprint_id = $blueprint_id
            ORDER BY beat_index, beat_id;
            """;
        command.Parameters.AddWithValue("$blueprint_id", blueprintId);

        var records = new List<CorpusBlueprintBeatPersistenceRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            records.Add(new CorpusBlueprintBeatPersistenceRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return records;
    }

    private sealed record ReferenceUserFeedbackRow(
        string TargetType,
        string TargetId,
        string Decision,
        IReadOnlyList<string> FeedbackTags,
        string Note,
        string Origin);

    private static async ValueTask<IReadOnlyList<ReferenceUserFeedbackRow>> ReadReferenceUserFeedbackRowsAsync(
        AppInitializationOptions options,
        long novelId,
        string targetId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_type, target_id, decision, feedback_tags_json, note, origin
            FROM reference_user_feedback
            WHERE novel_id = $novel_id
              AND target_id = $target_id
            ORDER BY created_at, feedback_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$target_id", targetId);
        var records = new List<ReferenceUserFeedbackRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            records.Add(new ReferenceUserFeedbackRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(3), GoldenJsonOptions) ?? [],
                reader.GetString(4),
                reader.GetString(5)));
        }

        return records;
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

    private static IReadOnlyList<string> ReadRenderedStringArray(JsonElement values, long novelId)
    {
        return values.EnumerateArray()
            .Select(item => RenderFixtureId(item.GetString() ?? string.Empty, novelId))
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<long> ReadLongArray(JsonElement values)
    {
        return values.EnumerateArray()
            .Select(item => item.GetInt64())
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

    private static string BlueprintNodeSetKey(ReferenceCorpusInsertionBlueprintPayload blueprint)
    {
        return string.Join(
            "|",
            blueprint.Beats
                .SelectMany(beat => beat.NodeIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
    }

    private static string RenderFixtureId(string value, long novelId)
    {
        return value.Replace(
            "{novel_id}",
            novelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static object NovelIdParameter(string? novelIdScope, long novelId)
    {
        return string.Equals(novelIdScope, "project", StringComparison.Ordinal)
            ? novelId
            : DBNull.Value;
    }

    private static JsonObject NormalizeCrossLibraryClosedLoopForGolden(
        ReferenceCorpusBlueprintCandidatesPayload enabledBlueprintCandidates,
        ReferenceCorpusBlueprintCandidatesPayload feedbackBlueprintCandidates,
        ReferenceCorpusInsertionDraftPayload enabledDraft,
        ReferenceCorpusBlueprintCandidatesPayload disabledBlueprintCandidates,
        ReferenceCorpusInsertionDraftPayload disabledDraft,
        ReferenceCorpusInsertionDraftCandidatesPayload draftCandidates)
    {
        return new JsonObject
        {
            ["enabled_blueprint_candidates"] = NormalizeBlueprintCandidatesForGolden(enabledBlueprintCandidates),
            ["feedback_blueprint_candidates"] = NormalizeBlueprintCandidatesForGolden(feedbackBlueprintCandidates),
            ["enabled_selected_draft"] = NormalizeDraftForGolden(enabledDraft),
            ["disabled_blueprint_candidates"] = NormalizeBlueprintCandidatesForGolden(disabledBlueprintCandidates),
            ["disabled_selected_draft"] = NormalizeDraftForGolden(disabledDraft),
            ["draft_candidates"] = NormalizeDraftCandidatesForGolden(draftCandidates)
        };
    }

    private static JsonObject NormalizeBlueprintCandidatesForGolden(ReferenceCorpusBlueprintCandidatesPayload payload)
    {
        var nodeAliases = AliasMap(payload.Candidates
            .SelectMany(candidate => candidate.Blueprint.Beats)
            .SelectMany(beat => beat.NodeIds));
        var beatAliases = AliasMap(payload.Candidates
            .SelectMany(candidate => candidate.Blueprint.Beats)
            .Select(beat => beat.BeatId));
        var blueprintAliases = AliasMap(payload.Candidates.Select(candidate => candidate.Blueprint.BlueprintId));
        var libraryAliases = AliasMap(payload.QueryContext.Scope.LibraryIds
            .Concat(payload.Candidates.SelectMany(candidate => candidate.SourceDistribution.Select(source => source.LibraryId))));
        var candidates = new JsonArray();
        foreach (var candidate in payload.Candidates)
        {
            candidates.Add(new JsonObject
            {
                ["blueprint_alias"] = blueprintAliases[candidate.Blueprint.BlueprintId],
                ["strategy"] = candidate.Blueprint.Strategy,
                ["beats"] = BlueprintBeatsArray(candidate.Blueprint.Beats, beatAliases, nodeAliases),
                ["source_distribution"] = SourceDistributionArray(candidate.SourceDistribution, libraryAliases),
                ["coverage_score"] = candidate.CoverageScore,
                ["gap_reasons"] = StringArray(candidate.GapReasons),
                ["feedback_reason"] = candidate.FeedbackReason
            });
        }

        return new JsonObject
        {
            ["query_context"] = QueryContextObject(payload.QueryContext, libraryAliases),
            ["feedback_applied"] = payload.FeedbackApplied,
            ["feedback_summary"] = payload.FeedbackSummary,
            ["candidates"] = candidates
        };
    }

    private static JsonObject NormalizeDraftCandidatesForGolden(ReferenceCorpusInsertionDraftCandidatesPayload payload)
    {
        var nodeAliases = AliasMap(payload.SelectedBlueprint.Beats
            .SelectMany(beat => beat.NodeIds)
            .Concat(payload.Candidates.SelectMany(candidate => candidate.Draft.Pieces.Select(piece => piece.NodeId))));
        var beatAliases = AliasMap(payload.SelectedBlueprint.Beats.Select(beat => beat.BeatId));
        var candidateAliases = AliasMap(payload.Candidates.Select(candidate => candidate.CandidateId));
        var libraryAliases = AliasMap(payload.QueryContext.Scope.LibraryIds
            .Concat(payload.Candidates.SelectMany(candidate => candidate.Draft.Pieces.Select(piece => piece.LibraryId))));
        var candidates = new JsonArray();
        foreach (var candidate in payload.Candidates)
        {
            candidates.Add(new JsonObject
            {
                ["candidate_alias"] = candidateAliases[candidate.CandidateId],
                ["strategy"] = candidate.Strategy,
                ["explanation"] = candidate.Explanation,
                ["draft"] = NormalizeDraftForGolden(candidate.Draft)
            });
        }

        return new JsonObject
        {
            ["query_context"] = QueryContextObject(payload.QueryContext, libraryAliases),
            ["selected_blueprint"] = new JsonObject
            {
                ["strategy"] = payload.SelectedBlueprint.Strategy,
                ["beats"] = BlueprintBeatsArray(payload.SelectedBlueprint.Beats, beatAliases, nodeAliases)
            },
            ["candidates"] = candidates
        };
    }

    private static void AssertDraftPiecesStayWithinSelectedBeats(
        ReferenceCorpusInsertionBlueprintPayload selectedBlueprint,
        ReferenceCorpusInsertionDraftPayload draft)
    {
        var allowedNodesByBeat = selectedBlueprint.Beats.ToDictionary(
            beat => beat.BeatId,
            beat => beat.NodeIds.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var piece in draft.Pieces)
        {
            Assert.True(
                allowedNodesByBeat.TryGetValue(piece.BeatId, out var allowedNodeIds),
                $"Draft piece beat {piece.BeatId} is not in the selected blueprint.");
            Assert.Contains(piece.NodeId, allowedNodeIds);
        }

        foreach (var beat in draft.Blueprint.Beats)
        {
            Assert.True(
                allowedNodesByBeat.TryGetValue(beat.BeatId, out var allowedNodeIds),
                $"Draft blueprint beat {beat.BeatId} is not in the selected blueprint.");
            Assert.All(beat.NodeIds, nodeId => Assert.Contains(nodeId, allowedNodeIds));
        }
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
        var spanAliases = AliasMap(draft.Pieces.SelectMany(piece => piece.PreservedSpans.Select(span => span.SpanId)));
        var transitionAliases = AliasMap(draft.Transitions.Select(transition => transition.TransitionId)
            .Concat(draft.Audit.Transitions.Select(transition => transition.TransitionId))
            .Concat(draft.Audit.Pieces.SelectMany(piece => piece.Violations.Select(violation => violation.TransitionId ?? string.Empty)))
            .Concat(draft.Audit.Transitions.SelectMany(transition => transition.Violations.Select(violation => violation.TransitionId ?? string.Empty))));
        var gapAliases = AliasMap(draft.Transitions.Select(transition => transition.GapId)
            .Concat(draft.Audit.Transitions.Select(transition => transition.GapId)));
        var auditViolationAliases = AliasMap(draft.Audit.Pieces.SelectMany(piece => piece.Violations.Select(violation => violation.ViolationId))
            .Concat(draft.Audit.Transitions.SelectMany(transition => transition.Violations.Select(violation => violation.ViolationId))));

        return new JsonObject
        {
            ["query_context"] = QueryContextObject(draft.QueryContext, libraryAliases),
            ["blueprint"] = new JsonObject
            {
                ["strategy"] = draft.Blueprint.Strategy,
                ["beats"] = BlueprintBeatsArray(draft.Blueprint.Beats, beatAliases, nodeAliases)
            },
            ["pieces"] = PiecesArray(draft.Pieces, pieceAliases, beatAliases, candidateAliases, nodeAliases, libraryAliases, spanAliases),
            ["slot_replacements"] = SlotReplacementsArray(draft.SlotReplacements),
            ["transitions"] = TransitionsArray(draft.Transitions, transitionAliases, gapAliases, pieceAliases, nodeAliases),
            ["assembled_text"] = NormalizeLineEndings(draft.AssembledText),
            ["chapter_text_after_insertion"] = NormalizeLineEndings(draft.ChapterTextAfterInsertion),
            ["ready_for_insertion"] = draft.ReadyForInsertion,
            ["gate"] = GateObject(draft.Gate, pieceAliases, nodeAliases),
            ["audit"] = AuditObject(draft.Audit, pieceAliases, nodeAliases, spanAliases, transitionAliases, gapAliases, auditViolationAliases)
        };
    }

    private static JsonObject QueryContextObject(
        ReferenceCorpusQueryContextPayload queryContext,
        IReadOnlyDictionary<string, string> libraryAliases)
    {
        return new JsonObject
        {
            ["scene_type"] = queryContext.SceneType,
            ["emotion_target"] = queryContext.EmotionTarget,
            ["pacing_target"] = queryContext.PacingTarget,
            ["narrative_position"] = queryContext.NarrativePosition,
            ["commercial_mechanic"] = queryContext.CommercialMechanic,
            ["character_states"] = StringArray(queryContext.CharacterStates),
            ["required_narrative_functions"] = StringArray(queryContext.RequiredNarrativeFunctions),
            ["chapter_context"] = new JsonObject
            {
                ["chapter_number"] = queryContext.ChapterContext.ChapterNumber,
                ["current_draft_text"] = NormalizeLineEndings(queryContext.ChapterContext.CurrentDraftText ?? string.Empty),
                ["insertion_offset"] = queryContext.ChapterContext.InsertionOffset,
                ["previous_chapter_summary"] = queryContext.ChapterContext.PreviousChapterSummary,
                ["character_snapshots"] = CharacterSnapshotsArray(queryContext.ChapterContext.CharacterSnapshots)
            },
            ["scope"] = new JsonObject
            {
                ["library_ids"] = StringArray(queryContext.Scope.LibraryIds.Select(id => libraryAliases[id])),
                ["reuse_policies"] = StringArray(queryContext.Scope.ReusePolicies),
                ["include_anchor_ids_count"] = queryContext.Scope.IncludeAnchorIds.Count,
                ["exclude_anchor_ids_count"] = queryContext.Scope.ExcludeAnchorIds.Count
            }
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

    private static JsonArray SourceDistributionArray(
        IReadOnlyList<ReferenceCorpusBlueprintSourcePayload> sources,
        IReadOnlyDictionary<string, string> libraryAliases)
    {
        var result = new JsonArray();
        foreach (var source in sources)
        {
            result.Add(new JsonObject
            {
                ["library_alias"] = libraryAliases[source.LibraryId],
                ["anchor_id"] = source.AnchorId,
                ["node_count"] = source.NodeCount
            });
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
        IReadOnlyDictionary<string, string> libraryAliases,
        IReadOnlyDictionary<string, string> spanAliases)
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
                ["preserved_spans"] = PreservedSpansArray(piece.PreservedSpans, spanAliases),
                ["slot_replacements"] = SlotReplacementsArray(piece.SlotReplacements)
            });
        }

        return result;
    }

    private static JsonArray PreservedSpansArray(
        IReadOnlyList<ReferenceCorpusPreservedSpanPayload> spans,
        IReadOnlyDictionary<string, string> spanAliases)
    {
        var result = new JsonArray();
        foreach (var span in spans)
        {
            result.Add(new JsonObject
            {
                ["span_alias"] = spanAliases[span.SpanId],
                ["source_start"] = span.SourceStart,
                ["source_end"] = span.SourceEnd,
                ["output_start"] = span.OutputStart,
                ["output_end"] = span.OutputEnd,
                ["source_text_hash"] = span.SourceTextHash,
                ["output_text_hash"] = span.OutputTextHash,
                ["matches"] = span.Matches
            });
        }

        return result;
    }

    private static JsonArray TransitionsArray(
        IReadOnlyList<ReferenceCorpusTransitionPayload> transitions,
        IReadOnlyDictionary<string, string> transitionAliases,
        IReadOnlyDictionary<string, string> gapAliases,
        IReadOnlyDictionary<string, string> pieceAliases,
        IReadOnlyDictionary<string, string> nodeAliases)
    {
        var result = new JsonArray();
        foreach (var transition in transitions)
        {
            result.Add(new JsonObject
            {
                ["transition_alias"] = AliasOrRaw(transitionAliases, transition.TransitionId),
                ["gap_alias"] = AliasOrRaw(gapAliases, transition.GapId),
                ["after_piece_alias"] = AliasOrRaw(pieceAliases, transition.AfterPieceId),
                ["before_piece_alias"] = AliasOrRaw(pieceAliases, transition.BeforePieceId),
                ["decision"] = transition.Decision,
                ["strategy"] = transition.Strategy,
                ["text"] = NormalizeLineEndings(transition.Text),
                ["text_hash"] = transition.TextHash,
                ["output_start"] = transition.OutputStart,
                ["output_end"] = transition.OutputEnd,
                ["approved"] = transition.Approved,
                ["reason"] = transition.Reason,
                ["replacement_piece_alias"] = transition.ReplacementPieceId is null
                    ? null
                    : AliasOrRaw(pieceAliases, transition.ReplacementPieceId),
                ["replacement_node_alias"] = transition.ReplacementNodeId is null
                    ? null
                    : AliasOrRaw(nodeAliases, transition.ReplacementNodeId)
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

    private static JsonObject AuditObject(
        ReferenceCorpusDraftAuditPayload audit,
        IReadOnlyDictionary<string, string> pieceAliases,
        IReadOnlyDictionary<string, string> nodeAliases,
        IReadOnlyDictionary<string, string> spanAliases,
        IReadOnlyDictionary<string, string> transitionAliases,
        IReadOnlyDictionary<string, string> gapAliases,
        IReadOnlyDictionary<string, string> violationAliases)
    {
        var pieces = new JsonArray();
        foreach (var piece in audit.Pieces)
        {
            pieces.Add(new JsonObject
            {
                ["piece_alias"] = pieceAliases[piece.PieceId],
                ["node_alias"] = nodeAliases[piece.NodeId],
                ["passed"] = piece.Passed,
                ["preserved_span_count"] = piece.PreservedSpanCount,
                ["mismatched_span_count"] = piece.MismatchedSpanCount,
                ["violations"] = AuditViolationsArray(piece.Violations, pieceAliases, nodeAliases, spanAliases, transitionAliases, violationAliases)
            });
        }

        return new JsonObject
        {
            ["passed"] = audit.Passed,
            ["status"] = audit.Status,
            ["errors"] = StringArray(audit.Errors),
            ["pieces"] = pieces,
            ["transitions"] = AuditTransitionsArray(audit.Transitions, transitionAliases, gapAliases, pieceAliases, nodeAliases, spanAliases, violationAliases)
        };
    }

    private static JsonArray AuditTransitionsArray(
        IReadOnlyList<ReferenceCorpusDraftAuditTransitionPayload> transitions,
        IReadOnlyDictionary<string, string> transitionAliases,
        IReadOnlyDictionary<string, string> gapAliases,
        IReadOnlyDictionary<string, string> pieceAliases,
        IReadOnlyDictionary<string, string> nodeAliases,
        IReadOnlyDictionary<string, string> spanAliases,
        IReadOnlyDictionary<string, string> violationAliases)
    {
        var result = new JsonArray();
        foreach (var transition in transitions)
        {
            result.Add(new JsonObject
            {
                ["transition_alias"] = AliasOrRaw(transitionAliases, transition.TransitionId),
                ["gap_alias"] = AliasOrRaw(gapAliases, transition.GapId),
                ["after_piece_alias"] = AliasOrRaw(pieceAliases, transition.AfterPieceId),
                ["before_piece_alias"] = AliasOrRaw(pieceAliases, transition.BeforePieceId),
                ["decision"] = transition.Decision,
                ["passed"] = transition.Passed,
                ["violations"] = AuditViolationsArray(transition.Violations, pieceAliases, nodeAliases, spanAliases, transitionAliases, violationAliases)
            });
        }

        return result;
    }

    private static JsonArray AuditViolationsArray(
        IReadOnlyList<ReferenceCorpusDraftAuditViolationPayload> violations,
        IReadOnlyDictionary<string, string> pieceAliases,
        IReadOnlyDictionary<string, string> nodeAliases,
        IReadOnlyDictionary<string, string> spanAliases,
        IReadOnlyDictionary<string, string> transitionAliases,
        IReadOnlyDictionary<string, string> violationAliases)
    {
        var result = new JsonArray();
        foreach (var violation in violations)
        {
            result.Add(new JsonObject
            {
                ["violation_alias"] = violationAliases[violation.ViolationId],
                ["code"] = violation.Code,
                ["severity"] = violation.Severity,
                ["piece_alias"] = AliasOrRaw(pieceAliases, violation.PieceId),
                ["node_alias"] = AliasOrRaw(nodeAliases, violation.NodeId),
                ["span_alias"] = violation.SpanId is null ? null : AliasOrRaw(spanAliases, violation.SpanId),
                ["transition_alias"] = violation.TransitionId is null ? null : AliasOrRaw(transitionAliases, violation.TransitionId),
                ["message"] = violation.Message
            });
        }

        return result;
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

    private static string AliasOrRaw(IReadOnlyDictionary<string, string> aliases, string value)
    {
        return aliases.TryGetValue(value, out var alias) ? alias : value;
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

    private sealed class TopicEmbeddingClient : IEmbeddingClient
    {
        private readonly int _defaultDimensions;

        public TopicEmbeddingClient(int defaultDimensions)
        {
            _defaultDimensions = defaultDimensions;
        }

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dimensions = options.Dimensions ?? _defaultDimensions;
            var items = inputs
                .Select((input, index) => new EmbeddingItemResult(index, VectorFor(input, dimensions)))
                .ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                dimensions,
                items,
                new EmbeddingUsage(inputs.Count, inputs.Count)));
        }

        private static IReadOnlyList<float> VectorFor(string input, int dimensions)
        {
            var vector = new float[dimensions];
            if (dimensions == 0)
            {
                return vector;
            }

            var topic = input.Contains("火光", StringComparison.Ordinal) ||
                input.Contains("旧市集", StringComparison.Ordinal)
                    ? 0
                    : Math.Min(1, dimensions - 1);
            vector[topic] = 1f;
            return vector;
        }
    }

    private sealed class TechniqueIntentEmbeddingClient : IEmbeddingClient
    {
        private readonly int _defaultDimensions;

        public TechniqueIntentEmbeddingClient(int defaultDimensions)
        {
            _defaultDimensions = defaultDimensions;
        }

        public ValueTask<EmbeddingBatchResult> EmbedAsync(
            IReadOnlyList<string> inputs,
            EmbeddingRequestOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dimensions = options.Dimensions ?? _defaultDimensions;
            var items = inputs
                .Select((input, index) => new EmbeddingItemResult(index, VectorFor(input, dimensions)))
                .ToArray();
            return ValueTask.FromResult(new EmbeddingBatchResult(
                options.ModelId,
                dimensions,
                items,
                new EmbeddingUsage(inputs.Count, inputs.Count)));
        }

        private static IReadOnlyList<float> VectorFor(string input, int dimensions)
        {
            var vector = new float[dimensions];
            if (dimensions <= 0)
            {
                return vector;
            }

            var topic = input.Contains("动作替代心理", StringComparison.Ordinal) ||
                input.Contains("细节动作承载压抑愤怒", StringComparison.Ordinal) ||
                input.Contains("身体细节动作", StringComparison.Ordinal) ||
                input.Contains("不直接说明情绪", StringComparison.Ordinal)
                    ? 0
                    : Math.Min(1, dimensions - 1);
            vector[topic] = 1f;
            return vector;
        }
    }

    private static ReferenceCorpusCandidatePayload M4DiagnosticCandidate(
        string nodeId,
        long anchorId,
        string libraryId,
        double score,
        IReadOnlyList<string> evidenceFamilies,
        double techniqueFit)
    {
        var evidence = evidenceFamilies
            .Select((family, index) => new ReferenceCorpusCandidateEvidencePayload(
                ObservationId: $"obs-{nodeId}-{family}",
                FeatureFamily: family,
                FeatureKey: family switch
                {
                    "rhythm" => "length_band",
                    "narrative" => "narrative_function",
                    "action" => "emotion_carrier",
                    _ => "emotion_state"
                },
                Confidence: Math.Max(0.80, 0.98 - index * 0.02)))
            .ToArray();
        return new ReferenceCorpusCandidatePayload(
            CandidateId: "corpus-node:" + nodeId,
            NodeId: nodeId,
            AnchorId: anchorId,
            LibraryId: libraryId,
            NodeType: ReferenceCorpusNodeTypes.Sentence,
            TextPreview: nodeId,
            TextHash: "sha256-" + nodeId,
            LicenseState: ReferenceCorpusLicenseStates.PublicDomain,
            ReusePolicy: ReferenceCorpusReusePolicies.VerbatimOk,
            Score: score,
            ScoreComponents: new Dictionary<string, double>
            {
                ["semantic"] = score,
                ["chapter_fit"] = score,
                ["technique_fit"] = techniqueFit,
                ["observation_fit"] = evidence.Length == 0 ? 0 : evidence.Average(item => item.Confidence),
                ["position_fit"] = 0.1,
                ["source_quality"] = 1.0
            },
            FitExplanation: "M4 diagnostic test candidate",
            Evidence: evidence);
    }

    private sealed class StaticReferenceCorpusService : IReferenceCorpusService
    {
        private readonly IReadOnlyList<ReferenceCorpusCandidatePayload> _items;

        public StaticReferenceCorpusService(IReadOnlyList<ReferenceCorpusCandidatePayload> items)
        {
            _items = items;
        }

        public ValueTask<PageResultPayload<ReferenceCorpusCandidatePayload>> SearchCandidatesAsync(
            SearchReferenceCorpusCandidatesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageSize = Math.Clamp(input.PageRequest.PageSize <= 0 ? _items.Count : input.PageRequest.PageSize, 1, 200);
            var items = _items.Take(pageSize).ToArray();
            return ValueTask.FromResult(new PageResultPayload<ReferenceCorpusCandidatePayload>(
                items,
                _items.Count,
                Page: 1,
                Size: pageSize,
                TotalPages: 1,
                NextCursor: null,
                HasMore: _items.Count > pageSize,
                TotalEstimate: _items.Count));
        }

        public ValueTask<ReferenceCorpusTechniqueVectorIndexBackfillPayload> BackfillTechniqueVectorIndexAsync(
            BackfillReferenceCorpusTechniqueVectorIndexPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ReferenceCorpusTechniqueVectorIndexBackfillPayload(
                ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Skipped,
                IndexScopeKey: null,
                TableName: null,
                ProviderKey: null,
                ModelId: null,
                Dimensions: 0,
                SourceCount: 0,
                VectorCount: 0,
                SkippedVectorCount: 0,
                Rebuilt: false,
                Diagnostics: ["static_test_double"]));
        }
    }

    private sealed class TwoSourceBlueprintAssembler : IReferenceCorpusBlueprintAssembler
    {
        public bool SawMultipleLibraries { get; private set; }

        public ValueTask<ReferenceCorpusInsertionBlueprintPayload> AssembleAsync(
            ReferenceCorpusBlueprintAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = request.Candidates
                .GroupBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(candidate => candidate.TextPreview.Contains("没有立刻", StringComparison.Ordinal) ? 1 : 0)
                    .ThenByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                    .First())
                .OrderBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            SawMultipleLibraries = selected.Length >= 2;
            if (selected.Length < 2)
            {
                return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
                    "corpus-blueprint-cross-library-empty",
                    "cross-library-empty",
                    "two_source_cross_library_test",
                    []));
            }

            return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
                "corpus-blueprint-cross-library",
                "cross-library-query",
                "two_source_cross_library_test",
                selected.Select((candidate, index) => new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "corpus-beat-cross-library-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    BeatIndex: index,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: request.QueryContext.RequiredNarrativeFunctions.FirstOrDefault() ?? "support_current_chapter",
                    NodeIds: [candidate.NodeId])).ToArray()));
        }
    }

    private sealed class FirstTwoSourceBlueprintAssembler : IReferenceCorpusBlueprintAssembler
    {
        public ValueTask<ReferenceCorpusInsertionBlueprintPayload> AssembleAsync(
            ReferenceCorpusBlueprintAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = request.Candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (selected.Length < 2)
            {
                return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
                    "corpus-blueprint-first-two-empty",
                    "first-two-empty",
                    "first_two_source_test",
                    []));
            }

            return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
                "corpus-blueprint-first-two",
                "first-two-query",
                "first_two_source_test",
                selected.Select((candidate, index) => new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "corpus-beat-first-two-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    BeatIndex: index,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: request.QueryContext.RequiredNarrativeFunctions.FirstOrDefault() ?? "support_current_chapter",
                    NodeIds: [candidate.NodeId])).ToArray()));
        }
    }

    private sealed class FirstThreeSourceBlueprintAssembler : IReferenceCorpusBlueprintAssembler
    {
        public ValueTask<ReferenceCorpusInsertionBlueprintPayload> AssembleAsync(
            ReferenceCorpusBlueprintAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = request.Candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                .Take(3)
                .ToArray();
            if (selected.Length < 3)
            {
                return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
                    "corpus-blueprint-first-three-empty",
                    "first-three-empty",
                    "first_three_source_test",
                    []));
            }

            return ValueTask.FromResult(new ReferenceCorpusInsertionBlueprintPayload(
                "corpus-blueprint-first-three",
                "first-three-query",
                "first_three_source_test",
                selected.Select((candidate, index) => new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "corpus-beat-first-three-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    BeatIndex: index,
                    RoleInBeat: "source_sentence",
                    NarrativeFunction: request.QueryContext.RequiredNarrativeFunctions.FirstOrDefault() ?? "support_current_chapter",
                    NodeIds: [candidate.NodeId])).ToArray()));
        }
    }

    private sealed class RecordingBlueprintCandidateAssembler : IReferenceCorpusBlueprintCandidateAssembler
    {
        public int Calls { get; private set; }

        public ReferenceCorpusBlueprintCandidateAssemblyRequest? LastRequest { get; private set; }

        public ValueTask<IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload>> AssembleCandidatesAsync(
            ReferenceCorpusBlueprintCandidateAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastRequest = request;
            var selected = request.Candidates.Take(2).ToArray();
            var beats = selected
                .Select((candidate, index) => new ReferenceCorpusInsertionBlueprintBeatPayload(
                    BeatId: "candidate-assembler-beat-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    BeatIndex: index,
                    RoleInBeat: index == 0 ? "opening_source_sentence" : "supporting_source_sentence",
                    NarrativeFunction: request.QueryContext.RequiredNarrativeFunctions.ElementAtOrDefault(index) ??
                        "support_current_chapter",
                    NodeIds: [candidate.NodeId]))
                .ToArray();
            var blueprint = new ReferenceCorpusInsertionBlueprintPayload(
                BlueprintId: "candidate-assembler-blueprint",
                QueryContextHash: "candidate-assembler-query",
                Strategy: "candidate_assembler_boundary_test",
                Beats: beats);
            var sourceDistribution = selected
                .GroupBy(candidate => (candidate.LibraryId, candidate.AnchorId))
                .Select(group => new ReferenceCorpusBlueprintSourcePayload(
                    group.Key.LibraryId,
                    group.Key.AnchorId,
                    group.Count()))
                .ToArray();
            IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload> result =
            [
                new ReferenceCorpusBlueprintCandidatePayload(
                    blueprint,
                    sourceDistribution,
                    CoverageScore: 0.77,
                    GapReasons: request.DiagnosticGapReasons,
                    FeedbackReason: request.FeedbackReason,
                    GapPositions: [])
            ];
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MutatingPreservedSpanTextAssembler : IReferenceCorpusTextAssembler
    {
        public ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
            ReferenceCorpusTextAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = request.SourcePieces.Select(source =>
            {
                var output = source.SourceText.StartsWith('她')
                    ? "他" + source.SourceText[1..]
                    : source.SourceText + "改";
                var span = new ReferenceCorpusPreservedSpanPayload(
                    SpanId: "audit-mismatch-span-" + source.NodeId,
                    SourceStart: 0,
                    SourceEnd: source.SourceText.Length,
                    OutputStart: 0,
                    OutputEnd: Math.Min(source.SourceText.Length, output.Length),
                    SourceTextHash: StableTextHash(source.SourceText),
                    OutputTextHash: StableTextHash(output[..Math.Min(source.SourceText.Length, output.Length)]),
                    Matches: false);
                return new ReferenceCorpusInsertionPiecePayload(
                    PieceId: source.PieceId,
                    BeatId: source.BeatId,
                    CandidateId: source.CandidateId,
                    NodeId: source.NodeId,
                    AnchorId: source.AnchorId,
                    LibraryId: source.LibraryId,
                    SourceTextHash: source.TextHash,
                    ReusePolicy: source.ReusePolicy,
                    LicenseState: source.LicenseState,
                    OutputText: output,
                    PreservedTextHash: StableTextHash(output),
                    PreservedHashMatches: false,
                    PreservedSpans: [span],
                    LockedSpans: [],
                    SlotReplacements: []);
            }).ToArray();

            return ValueTask.FromResult(new ReferenceCorpusTextAssemblyResult(
                Pieces: pieces,
                SlotReplacements: [],
                Transitions: [],
                AssembledText: string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText))));
        }
    }

    private sealed class DroppingLastPieceTextAssembler : IReferenceCorpusTextAssembler
    {
        public ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
            ReferenceCorpusTextAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = request.SourcePieces
                .Take(Math.Max(0, request.SourcePieces.Count - 1))
                .Select(source =>
                {
                    var span = new ReferenceCorpusPreservedSpanPayload(
                        SpanId: "audit-kept-span-" + source.NodeId,
                        SourceStart: 0,
                        SourceEnd: source.SourceText.Length,
                        OutputStart: 0,
                        OutputEnd: source.SourceText.Length,
                        SourceTextHash: StableTextHash(source.SourceText),
                        OutputTextHash: StableTextHash(source.SourceText),
                        Matches: true);
                    return new ReferenceCorpusInsertionPiecePayload(
                        PieceId: source.PieceId,
                        BeatId: source.BeatId,
                        CandidateId: source.CandidateId,
                        NodeId: source.NodeId,
                        AnchorId: source.AnchorId,
                        LibraryId: source.LibraryId,
                        SourceTextHash: source.TextHash,
                        ReusePolicy: source.ReusePolicy,
                        LicenseState: source.LicenseState,
                        OutputText: source.SourceText,
                        PreservedTextHash: StableTextHash(source.SourceText),
                        PreservedHashMatches: true,
                        PreservedSpans: [span],
                        LockedSpans: [],
                        SlotReplacements: []);
                })
                .ToArray();

            return ValueTask.FromResult(new ReferenceCorpusTextAssemblyResult(
                Pieces: pieces,
                SlotReplacements: [],
                Transitions: [],
                AssembledText: string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText))));
        }
    }

    private sealed class AppendingUnauditedTextAssembler : IReferenceCorpusTextAssembler
    {
        public ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
            ReferenceCorpusTextAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = request.SourcePieces.Select(source =>
            {
                var span = new ReferenceCorpusPreservedSpanPayload(
                    SpanId: "audit-envelope-span-" + source.NodeId,
                    SourceStart: 0,
                    SourceEnd: source.SourceText.Length,
                    OutputStart: 0,
                    OutputEnd: source.SourceText.Length,
                    SourceTextHash: StableTextHash(source.SourceText),
                    OutputTextHash: StableTextHash(source.SourceText),
                    Matches: true);
                return new ReferenceCorpusInsertionPiecePayload(
                    PieceId: source.PieceId,
                    BeatId: source.BeatId,
                    CandidateId: source.CandidateId,
                    NodeId: source.NodeId,
                    AnchorId: source.AnchorId,
                    LibraryId: source.LibraryId,
                    SourceTextHash: source.TextHash,
                    ReusePolicy: source.ReusePolicy,
                    LicenseState: source.LicenseState,
                    OutputText: source.SourceText,
                    PreservedTextHash: StableTextHash(source.SourceText),
                    PreservedHashMatches: true,
                    PreservedSpans: [span],
                    LockedSpans: [],
                    SlotReplacements: []);
            }).ToArray();

            var expected = string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText));
            return ValueTask.FromResult(new ReferenceCorpusTextAssemblyResult(
                Pieces: pieces,
                SlotReplacements: [],
                Transitions: [],
                AssembledText: expected + Environment.NewLine + "错误正文直接塞进章节。"));
        }
    }

    private sealed class AppendingUnauditedRangeInsidePieceTextAssembler : IReferenceCorpusTextAssembler
    {
        public ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
            ReferenceCorpusTextAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = request.SourcePieces.Select(source =>
            {
                var output = source.SourceText + "错误正文直接塞进片段。";
                var span = new ReferenceCorpusPreservedSpanPayload(
                    SpanId: "audit-piece-envelope-span-" + source.NodeId,
                    SourceStart: 0,
                    SourceEnd: source.SourceText.Length,
                    OutputStart: 0,
                    OutputEnd: source.SourceText.Length,
                    SourceTextHash: StableTextHash(source.SourceText),
                    OutputTextHash: StableTextHash(source.SourceText),
                    Matches: true);
                return new ReferenceCorpusInsertionPiecePayload(
                    PieceId: source.PieceId,
                    BeatId: source.BeatId,
                    CandidateId: source.CandidateId,
                    NodeId: source.NodeId,
                    AnchorId: source.AnchorId,
                    LibraryId: source.LibraryId,
                    SourceTextHash: source.TextHash,
                    ReusePolicy: source.ReusePolicy,
                    LicenseState: source.LicenseState,
                    OutputText: output,
                    PreservedTextHash: StableTextHash(source.SourceText),
                    PreservedHashMatches: true,
                    PreservedSpans: [span],
                    LockedSpans: [],
                    SlotReplacements: []);
            }).ToArray();

            return ValueTask.FromResult(new ReferenceCorpusTextAssemblyResult(
                Pieces: pieces,
                SlotReplacements: [],
                Transitions: [],
                AssembledText: string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText))));
        }
    }

    private sealed class CandidateSetNonSlotDriftTextAssembler : IReferenceCorpusTextAssembler
    {
        public ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
            ReferenceCorpusTextAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = request.SourcePieces.Select(source => BuildPiece(source, request.ExplicitSlotValues)).ToArray();
            return ValueTask.FromResult(new ReferenceCorpusTextAssemblyResult(
                Pieces: pieces,
                SlotReplacements: pieces.SelectMany(piece => piece.SlotReplacements).ToArray(),
                Transitions: [],
                AssembledText: string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText))));
        }

        private static ReferenceCorpusInsertionPiecePayload BuildPiece(
            ReferenceCorpusSourcePiece source,
            IReadOnlyDictionary<string, string> explicitSlotValues)
        {
            var planned = new List<(string SlotName, string SourceValue, string ReplacementValue, int SourceStart, int SourceEnd)>();
            foreach (var pair in explicitSlotValues)
            {
                var parsed = ParseExplicitSlotKey(pair.Key);
                var replacement = pair.Value.Trim();
                if (parsed.SourceValue.Length == 0 || replacement.Length == 0)
                {
                    continue;
                }

                var sourceStart = source.SourceText.IndexOf(parsed.SourceValue, StringComparison.Ordinal);
                if (sourceStart < 0)
                {
                    continue;
                }

                planned.Add((
                    parsed.SlotName,
                    parsed.SourceValue,
                    replacement,
                    sourceStart,
                    sourceStart + parsed.SourceValue.Length));
            }

            if (explicitSlotValues.Values.Any(value => string.Equals(value.Trim(), "沈照", StringComparison.Ordinal)))
            {
                var driftStart = source.SourceText.IndexOf("指尖", StringComparison.Ordinal);
                if (driftStart >= 0)
                {
                    planned.Add(("explicit", "指尖", "掌心", driftStart, driftStart + "指尖".Length));
                }
            }

            planned = planned
                .OrderBy(item => item.SourceStart)
                .ThenBy(item => item.SourceEnd)
                .ToList();

            var output = new StringBuilder();
            var preservedSegments = new StringBuilder();
            var spans = new List<ReferenceCorpusPreservedSpanPayload>();
            var replacements = new List<ReferenceCorpusSlotReplacementPayload>();
            var sourceCursor = 0;
            foreach (var item in planned)
            {
                if (item.SourceStart < sourceCursor)
                {
                    continue;
                }

                if (item.SourceStart > sourceCursor)
                {
                    AddPreservedSpan(source, sourceCursor, item.SourceStart, output, preservedSegments, spans);
                }

                var outputStart = output.Length;
                output.Append(item.ReplacementValue);
                replacements.Add(new ReferenceCorpusSlotReplacementPayload(
                    item.SlotName,
                    item.SourceValue,
                    item.ReplacementValue,
                    item.SourceStart,
                    item.SourceEnd,
                    outputStart,
                    output.Length));
                sourceCursor = item.SourceEnd;
            }

            if (sourceCursor < source.SourceText.Length)
            {
                AddPreservedSpan(source, sourceCursor, source.SourceText.Length, output, preservedSegments, spans);
            }

            return new ReferenceCorpusInsertionPiecePayload(
                PieceId: source.PieceId,
                BeatId: source.BeatId,
                CandidateId: source.CandidateId,
                NodeId: source.NodeId,
                AnchorId: source.AnchorId,
                LibraryId: source.LibraryId,
                SourceTextHash: source.TextHash,
                ReusePolicy: source.ReusePolicy,
                LicenseState: source.LicenseState,
                OutputText: output.ToString(),
                PreservedTextHash: StableTextHash(preservedSegments.ToString()),
                PreservedHashMatches: true,
                PreservedSpans: spans,
                LockedSpans: [],
                SlotReplacements: replacements);
        }

        private static void AddPreservedSpan(
            ReferenceCorpusSourcePiece source,
            int sourceStart,
            int sourceEnd,
            StringBuilder output,
            StringBuilder preservedSegments,
            List<ReferenceCorpusPreservedSpanPayload> spans)
        {
            var segment = source.SourceText[sourceStart..sourceEnd];
            var outputStart = output.Length;
            output.Append(segment);
            preservedSegments.Append(segment);
            spans.Add(new ReferenceCorpusPreservedSpanPayload(
                SpanId: "candidate-set-preserved-" + source.NodeId + "-" + spans.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceStart: sourceStart,
                SourceEnd: sourceEnd,
                OutputStart: outputStart,
                OutputEnd: output.Length,
                SourceTextHash: StableTextHash(segment),
                OutputTextHash: StableTextHash(segment),
                Matches: true));
        }

        private static (string SlotName, string SourceValue) ParseExplicitSlotKey(string key)
        {
            var separator = key.IndexOfAny([':', '：']);
            if (separator <= 0 || separator + 1 >= key.Length)
            {
                return ("explicit", key.Trim());
            }

            return (key[..separator].Trim(), key[(separator + 1)..].Trim());
        }
    }

    public enum TransitionAuditFailureMode
    {
        HashMismatch,
        UnknownPiece,
        OutputRangeMismatch,
        ReplacePiece,
        NonAdjacentPieces,
        WrongGapId
    }

    private sealed class MaliciousTransitionTextAssembler : IReferenceCorpusTextAssembler
    {
        private const string TransitionText = "门外的雨声又近了一寸。";
        private readonly TransitionAuditFailureMode _failureMode;

        public MaliciousTransitionTextAssembler(TransitionAuditFailureMode failureMode)
        {
            _failureMode = failureMode;
        }

        public ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
            ReferenceCorpusTextAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = BuildIdentityPieces(request.SourcePieces);
            if (pieces.Count < 2)
            {
                return ValueTask.FromResult(new ReferenceCorpusTextAssemblyResult(
                    Pieces: pieces,
                    SlotReplacements: [],
                    Transitions: [],
                    AssembledText: string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText))));
            }

            var first = pieces[0];
            var before = _failureMode == TransitionAuditFailureMode.NonAdjacentPieces && pieces.Count > 2
                ? pieces[2]
                : pieces[1];
            var text = _failureMode == TransitionAuditFailureMode.ReplacePiece ? string.Empty : TransitionText;
            var assembledText = BuildAssembledText(pieces, text);
            var outputStart = text.Length == 0
                ? 0
                : assembledText.IndexOf(text, StringComparison.Ordinal);
            var afterPieceId = _failureMode == TransitionAuditFailureMode.UnknownPiece
                ? "missing-after-piece"
                : first.PieceId;
            var beforePieceId = _failureMode == TransitionAuditFailureMode.UnknownPiece
                ? pieces[1].PieceId
                : before.PieceId;
            var transition = new ReferenceCorpusTransitionPayload(
                TransitionId: "transition-malicious-" + _failureMode.ToString().ToLowerInvariant(),
                GapId: _failureMode == TransitionAuditFailureMode.WrongGapId
                    ? "transition-gap-does-not-match-pair"
                    : "transition-gap-malicious-" + _failureMode.ToString().ToLowerInvariant(),
                AfterPieceId: afterPieceId,
                BeforePieceId: beforePieceId,
                Decision: _failureMode == TransitionAuditFailureMode.ReplacePiece
                    ? ReferenceCorpusTransitionDecisions.ReplacePiece
                    : ReferenceCorpusTransitionDecisions.InsertTransition,
                Strategy: _failureMode == TransitionAuditFailureMode.ReplacePiece ? "replace_piece" : "bridge_sentence",
                Text: text,
                TextHash: _failureMode == TransitionAuditFailureMode.HashMismatch ? "sha256-transition-mismatch" : StableTextHash(text),
                OutputStart: _failureMode == TransitionAuditFailureMode.OutputRangeMismatch ? 0 : Math.Max(0, outputStart),
                OutputEnd: _failureMode == TransitionAuditFailureMode.OutputRangeMismatch
                    ? Math.Min(text.Length, assembledText.Length)
                    : Math.Max(0, outputStart) + text.Length,
                Approved: _failureMode != TransitionAuditFailureMode.ReplacePiece,
                Reason: "malicious transition audit fixture",
                ReplacementPieceId: _failureMode == TransitionAuditFailureMode.ReplacePiece ? first.PieceId : null,
                ReplacementNodeId: _failureMode == TransitionAuditFailureMode.ReplacePiece ? first.NodeId : null);

            return ValueTask.FromResult(new ReferenceCorpusTextAssemblyResult(
                Pieces: pieces,
                SlotReplacements: [],
                Transitions: [transition],
                AssembledText: assembledText));
        }

        private string BuildAssembledText(IReadOnlyList<ReferenceCorpusInsertionPiecePayload> pieces, string transitionText)
        {
            if (_failureMode == TransitionAuditFailureMode.ReplacePiece || transitionText.Length == 0)
            {
                return string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText.Trim()));
            }

            if (_failureMode == TransitionAuditFailureMode.NonAdjacentPieces && pieces.Count > 2)
            {
                return string.Join(
                    Environment.NewLine,
                    pieces[0].OutputText.Trim(),
                    pieces[1].OutputText.Trim(),
                    transitionText,
                    pieces[2].OutputText.Trim());
            }

            return string.Join(
                Environment.NewLine,
                pieces[0].OutputText.Trim(),
                transitionText,
                pieces[1].OutputText.Trim());
        }
    }

    private sealed class MissingTransitionDecisionTextAssembler : IReferenceCorpusTextAssembler
    {
        public ValueTask<ReferenceCorpusTextAssemblyResult> AssembleAsync(
            ReferenceCorpusTextAssemblyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pieces = BuildIdentityPieces(request.SourcePieces);
            return ValueTask.FromResult(new ReferenceCorpusTextAssemblyResult(
                Pieces: pieces,
                SlotReplacements: [],
                Transitions: [],
                AssembledText: string.Join(Environment.NewLine, pieces.Select(piece => piece.OutputText.Trim()))));
        }
    }

    private sealed class FixedBridgeTransitionResolver : IReferenceCorpusTransitionResolver
    {
        private readonly string _text;

        public FixedBridgeTransitionResolver(string text)
        {
            _text = text;
        }

        public ValueTask<ReferenceCorpusTransitionResolutionResult> ResolveAsync(
            ReferenceCorpusTransitionResolutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Pieces.Count < 2)
            {
                return ValueTask.FromResult(new ReferenceCorpusTransitionResolutionResult([]));
            }

            var first = request.Pieces[0];
            var second = request.Pieces[1];
            var gap = request.Gaps.FirstOrDefault(gap =>
                string.Equals(gap.AfterPieceId, first.PieceId, StringComparison.Ordinal) &&
                string.Equals(gap.BeforePieceId, second.PieceId, StringComparison.Ordinal));
            return ValueTask.FromResult(new ReferenceCorpusTransitionResolutionResult(
            [
                new ReferenceCorpusTransitionPayload(
                    TransitionId: "transition-test-bridge",
                    GapId: gap?.GapId ?? "transition-gap-test-bridge",
                    AfterPieceId: first.PieceId,
                    BeforePieceId: second.PieceId,
                    Decision: ReferenceCorpusTransitionDecisions.InsertTransition,
                    Strategy: "bridge_sentence",
                    Text: _text,
                    TextHash: StableTextHash(_text),
                    OutputStart: 0,
                    OutputEnd: 0,
                    Approved: true,
                    Reason: "test bridge transition")
            ]));
        }
    }

    private sealed class LockedRangeReplacingSlotResolver : IReferenceCorpusSlotResolver
    {
        private readonly string _sourceValue;
        private readonly string _replacementValue;

        public LockedRangeReplacingSlotResolver(string sourceValue, string replacementValue)
        {
            _sourceValue = sourceValue;
            _replacementValue = replacementValue;
        }

        public ValueTask<ReferenceCorpusSlotResolutionResult> ResolveAsync(
            ReferenceCorpusSlotResolutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lockedStart = request.SourceText.IndexOf('《');
            var lockedEnd = lockedStart < 0
                ? -1
                : request.SourceText.IndexOf('》', lockedStart + 1);
            if (lockedStart < 0 || lockedEnd < 0)
            {
                return ValueTask.FromResult(new ReferenceCorpusSlotResolutionResult([], []));
            }

            lockedEnd += 1;
            var sourceStart = request.SourceText.IndexOf(
                _sourceValue,
                lockedStart,
                lockedEnd - lockedStart,
                StringComparison.Ordinal);
            if (sourceStart < 0)
            {
                return ValueTask.FromResult(new ReferenceCorpusSlotResolutionResult(
                    [],
                    [new ReferenceCorpusLockedSourceSpan(lockedStart, lockedEnd, "quoted_text")]));
            }

            return ValueTask.FromResult(new ReferenceCorpusSlotResolutionResult(
                [
                    new ReferenceCorpusSlotReplacementPayload(
                        SlotName: "place",
                        SourceValue: _sourceValue,
                        ReplacementValue: _replacementValue,
                        SourceStart: sourceStart,
                        SourceEnd: sourceStart + _sourceValue.Length,
                        OutputStart: 0,
                        OutputEnd: 0)
                ],
                [new ReferenceCorpusLockedSourceSpan(lockedStart, lockedEnd, "quoted_text")]));
        }
    }

    private sealed class ReplacementRequestingTransitionResolver : IReferenceCorpusTransitionResolver
    {
        private readonly IReadOnlySet<string> _rejectedNodeIds;
        private readonly string _replacementNodeId;

        public ReplacementRequestingTransitionResolver(string rejectedNodeId, string replacementNodeId)
            : this([rejectedNodeId], replacementNodeId)
        {
        }

        public ReplacementRequestingTransitionResolver(IReadOnlyList<string> rejectedNodeIds, string replacementNodeId)
        {
            _rejectedNodeIds = rejectedNodeIds
                .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
                .ToHashSet(StringComparer.Ordinal);
            _replacementNodeId = replacementNodeId;
        }

        public ValueTask<ReferenceCorpusTransitionResolutionResult> ResolveAsync(
            ReferenceCorpusTransitionResolutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transitions = new List<ReferenceCorpusTransitionPayload>(request.Gaps.Count);
            foreach (var gap in request.Gaps)
            {
                var after = request.Pieces.First(piece => string.Equals(piece.PieceId, gap.AfterPieceId, StringComparison.Ordinal));
                var before = request.Pieces.First(piece => string.Equals(piece.PieceId, gap.BeforePieceId, StringComparison.Ordinal));
                var rejected = _rejectedNodeIds.Contains(after.NodeId)
                    ? after
                    : _rejectedNodeIds.Contains(before.NodeId)
                        ? before
                        : null;
                if (rejected is not null)
                {
                    transitions.Add(new ReferenceCorpusTransitionPayload(
                        TransitionId: "transition-replace-" + StableTextHash(gap.GapId)[..16],
                        GapId: gap.GapId,
                        AfterPieceId: gap.AfterPieceId,
                        BeforePieceId: gap.BeforePieceId,
                        Decision: ReferenceCorpusTransitionDecisions.ReplacePiece,
                        Strategy: "replace_piece",
                        Text: string.Empty,
                        TextHash: StableTextHash(string.Empty),
                        OutputStart: 0,
                        OutputEnd: 0,
                        Approved: false,
                        Reason: "test resolver requires replacing an incompatible source piece",
                        ReplacementPieceId: rejected.PieceId,
                        ReplacementNodeId: _replacementNodeId));
                    continue;
                }

                transitions.Add(new ReferenceCorpusTransitionPayload(
                    TransitionId: "transition-direct-" + StableTextHash(gap.GapId)[..16],
                    GapId: gap.GapId,
                    AfterPieceId: gap.AfterPieceId,
                    BeforePieceId: gap.BeforePieceId,
                    Decision: ReferenceCorpusTransitionDecisions.DirectJoin,
                    Strategy: "direct_join",
                    Text: string.Empty,
                    TextHash: StableTextHash(string.Empty),
                    OutputStart: 0,
                    OutputEnd: 0,
                    Approved: true,
                    Reason: "test resolver direct join"));
            }

            return ValueTask.FromResult(new ReferenceCorpusTransitionResolutionResult(transitions));
        }
    }

    private sealed record SlotTransferConstraintFixture(
        long NovelId,
        int ChapterNumber,
        string CurrentDraft,
        string LibraryId,
        string NodeId,
        ReferenceCorpusInsertionBlueprintPayload SelectedBlueprint,
        SqliteReferenceCorpusWritingService Service);

    private static IReadOnlyList<ReferenceCorpusInsertionPiecePayload> BuildIdentityPieces(
        IReadOnlyList<ReferenceCorpusSourcePiece> sourcePieces)
    {
        return sourcePieces.Select(BuildIdentityPiece).ToArray();
    }

    private static ReferenceCorpusInsertionPiecePayload BuildIdentityPiece(ReferenceCorpusSourcePiece source)
    {
        var span = new ReferenceCorpusPreservedSpanPayload(
            SpanId: "audit-identity-span-" + source.NodeId,
            SourceStart: 0,
            SourceEnd: source.SourceText.Length,
            OutputStart: 0,
            OutputEnd: source.SourceText.Length,
            SourceTextHash: StableTextHash(source.SourceText),
            OutputTextHash: StableTextHash(source.SourceText),
            Matches: true);
        return new ReferenceCorpusInsertionPiecePayload(
            PieceId: source.PieceId,
            BeatId: source.BeatId,
            CandidateId: source.CandidateId,
            NodeId: source.NodeId,
            AnchorId: source.AnchorId,
            LibraryId: source.LibraryId,
            SourceTextHash: source.TextHash,
            ReusePolicy: source.ReusePolicy,
            LicenseState: source.LicenseState,
            OutputText: source.SourceText,
            PreservedTextHash: StableTextHash(source.SourceText),
            PreservedHashMatches: true,
            PreservedSpans: [span],
            LockedSpans: [],
            SlotReplacements: []);
    }

    private static string StableTextHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string StableTextHash(params string[] values)
    {
        return StableTextHash(string.Join('\u001f', values));
    }
}
