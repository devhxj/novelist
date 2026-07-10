using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class NarrativePatternPipelineTests
{
    [Fact]
    public void ResolveChapterSelectionTreatsEmptyRangesAsAllChapters()
    {
        var chapters = Chapters(5);

        var selection = NarrativePatternPipeline.ResolveChapterSelection(chapters, ranges: []);

        Assert.Equal("all", selection.SelectionMode);
        Assert.Equal([1, 2, 3, 4, 5], selection.Chapters.Select(chapter => chapter.ChapterNumber).ToArray());
        Assert.Equal([new ChapterRangePayload(1, 5)], selection.ChapterRanges);
    }

    [Fact]
    public void ResolveChapterSelectionNormalizesRangesAndExplicitIds()
    {
        var chapters = Chapters(8);

        var selection = NarrativePatternPipeline.ResolveChapterSelection(
            chapters,
            ranges: [new ChapterRangePayload(2, 4), new ChapterRangePayload(6, 6)],
            selectedChapterIds: [chapters[6].Id]);

        Assert.Equal("custom", selection.SelectionMode);
        Assert.Equal([2, 3, 4, 6, 7], selection.Chapters.Select(chapter => chapter.ChapterNumber).ToArray());
        Assert.Equal([new ChapterRangePayload(2, 4), new ChapterRangePayload(6, 7)], selection.ChapterRanges);
        Assert.Equal([chapters[1].Id, chapters[2].Id, chapters[3].Id, chapters[5].Id, chapters[6].Id], selection.SelectedChapterIds);
    }

    [Fact]
    public void BuildChapterDocumentsRejectsInsufficientChaptersAndContent()
    {
        var tooFew = NarrativePatternPipeline.ResolveChapterSelection(Chapters(2), []);
        var tooFewContent = tooFew.Chapters.ToDictionary(chapter => chapter.FilePath, _ => LongText());
        var tooFewError = Assert.Throws<NarrativePatternValidationException>(() =>
            NarrativePatternPipeline.BuildChapterDocuments(tooFew, tooFewContent));
        Assert.Equal("pattern.insufficient_chapters", tooFewError.Code);

        var enoughChapters = NarrativePatternPipeline.ResolveChapterSelection(Chapters(3), []);
        var shortContent = enoughChapters.Chapters.ToDictionary(chapter => chapter.FilePath, _ => "短。");
        var shortError = Assert.Throws<NarrativePatternValidationException>(() =>
            NarrativePatternPipeline.BuildChapterDocuments(enoughChapters, shortContent));
        Assert.Equal("pattern.insufficient_content", shortError.Code);
    }

    [Fact]
    public void ParseBoundariesAcceptsOrderedCoverageAndRejectsCoverageGap()
    {
        var documents = Documents(6);

        var boundaries = NarrativePatternPipeline.ParseBoundaries(
            """
            {
              "schema_version": "narrative-pattern-v1",
              "boundaries": [
                { "start_chapter": 1, "end_chapter": 2, "label": "开局", "function": "建立压力", "evidence": "雨夜与失踪案并置" },
                { "start_chapter": 3, "end_chapter": 6, "label": "推进", "function": "扩大疑点", "evidence": "线索反复反转" }
              ]
            }
            """,
            documents);

        Assert.Equal(2, boundaries.Count);
        Assert.Equal("开局", boundaries[0].Label);

        var error = Assert.Throws<NarrativePatternValidationException>(() =>
            NarrativePatternPipeline.ParseBoundaries(
                """
                {
                  "schema_version": "narrative-pattern-v1",
                  "boundaries": [
                    { "start_chapter": 1, "end_chapter": 2, "label": "开局", "function": "建立压力", "evidence": "证据" },
                    { "start_chapter": 4, "end_chapter": 6, "label": "推进", "function": "扩大疑点", "evidence": "证据" }
                  ]
                }
                """,
                documents));
        Assert.Equal("pattern.boundary_coverage_gap", error.Code);
    }

    [Fact]
    public void ParseBoundariesRejectsInvalidJsonAndOverlaps()
    {
        var invalid = Assert.Throws<NarrativePatternValidationException>(() =>
            NarrativePatternPipeline.ParseBoundaries("{not json", Documents(3)));
        Assert.Equal("pattern.invalid_boundary_json", invalid.Code);

        var overlap = Assert.Throws<NarrativePatternValidationException>(() =>
            NarrativePatternPipeline.ParseBoundaries(
                """
                {
                  "schema_version": "narrative-pattern-v1",
                  "boundaries": [
                    { "start_chapter": 1, "end_chapter": 2, "label": "A", "function": "A", "evidence": "A" },
                    { "start_chapter": 2, "end_chapter": 3, "label": "B", "function": "B", "evidence": "B" }
                  ]
                }
                """,
                Documents(3)));
        Assert.Equal("pattern.invalid_boundary_order", overlap.Code);
    }

    [Fact]
    public void ParseChapterSummariesRequiresFreshContentHashesAndFullCoverage()
    {
        var documents = Documents(3);
        var summaries = NarrativePatternPipeline.ParseChapterSummaries(
            $$"""
            {
              "schema_version": "narrative-pattern-v1",
              "summaries": [
                { "chapter_number": 1, "content_hash": "{{documents[0].ContentHash}}", "summary": "第一章建立失踪压力。", "turning_points": ["发现线索"] },
                { "chapter_number": 2, "content_hash": "{{documents[1].ContentHash}}", "summary": "第二章扩大怀疑。", "turning_points": ["证词冲突"] },
                { "chapter_number": 3, "content_hash": "{{documents[2].ContentHash}}", "summary": "第三章给出反转。", "turning_points": ["旧案重现"] }
              ]
            }
            """,
            documents);

        Assert.Equal([1, 2, 3], summaries.Select(summary => summary.ChapterNumber).ToArray());

        var stale = Assert.Throws<NarrativePatternValidationException>(() =>
            NarrativePatternPipeline.ParseChapterSummaries(
                $$"""
                {
                  "schema_version": "narrative-pattern-v1",
                  "summaries": [
                    { "chapter_number": 1, "content_hash": "sha256:stale", "summary": "旧摘要。", "turning_points": ["旧线索"] },
                    { "chapter_number": 2, "content_hash": "{{documents[1].ContentHash}}", "summary": "第二章。", "turning_points": ["线索"] },
                    { "chapter_number": 3, "content_hash": "{{documents[2].ContentHash}}", "summary": "第三章。", "turning_points": ["线索"] }
                  ]
                }
                """,
                documents));
        Assert.Equal("pattern.stale_summary", stale.Code);
    }

    [Fact]
    public void ParsePhasesRequiresNonEmptyOrderedCoverage()
    {
        var summaries = Summaries(5);
        var phases = NarrativePatternPipeline.ParsePhases(
            """
            {
              "schema_version": "narrative-pattern-v1",
              "phases": [
                { "start_chapter": 1, "end_chapter": 3, "phase_name": "承压", "narrative_function": "建立谜题", "guidance": "先压低信息密度。" },
                { "start_chapter": 4, "end_chapter": 5, "phase_name": "反转", "narrative_function": "重组线索", "guidance": "让证据改变读者判断。" }
              ]
            }
            """,
            summaries);

        Assert.Equal(2, phases.Count);
        Assert.Equal("反转", phases[1].PhaseName);

        var empty = Assert.Throws<NarrativePatternValidationException>(() =>
            NarrativePatternPipeline.ParsePhases(
                """{"schema_version":"narrative-pattern-v1","phases":[]}""",
                summaries));
        Assert.Equal("pattern.empty_phase_output", empty.Code);
    }

    [Fact]
    public void CreateTokenBatchesHonorsContextBudgetAndPreservesOrder()
    {
        var batches = NarrativePatternPipeline.CreateTokenBatches(
            [100, 200, 400, 300, 100],
            tokenSelector: value => value,
            contextWindowTokens: 1_000,
            reservedOutputTokens: 250);

        Assert.Equal(2, batches.Count);
        Assert.Equal([100, 200, 400], batches[0].Items);
        Assert.Equal(700, batches[0].EstimatedTokens);
        Assert.Equal([300, 100], batches[1].Items);
        Assert.Equal(400, batches[1].EstimatedTokens);
    }

    [Fact]
    public void EvaluateCompressionProgressDetectsTargetStallAndMaxRounds()
    {
        Assert.Equal(
            "target_reached",
            NarrativePatternPipeline.EvaluateCompressionProgress(1, previousPhaseCount: 8, currentPhaseCount: 3, targetPhaseCount: 3).Reason);

        var stalled = NarrativePatternPipeline.EvaluateCompressionProgress(2, 8, 8, 3);
        Assert.True(stalled.Stop);
        Assert.True(stalled.Stalled);
        Assert.Equal("compression_stalled", stalled.Reason);

        var maxRounds = NarrativePatternPipeline.EvaluateCompressionProgress(4, 8, 5, 3);
        Assert.True(maxRounds.Stop);
        Assert.True(maxRounds.Stalled);
        Assert.Equal("max_rounds_reached", maxRounds.Reason);
    }

    private static IReadOnlyList<ChapterPayload> Chapters(int count)
    {
        return Enumerable.Range(1, count)
            .Select(number => new ChapterPayload(
                Id: 100 + number,
                NovelId: 42,
                ChapterNumber: number,
                Title: $"第{number}章",
                Summary: "",
                WordCount: 500,
                CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
                FilePath: $"chapters/{number:000}.md"))
            .ToArray();
    }

    private static IReadOnlyList<NarrativePatternChapterDocument> Documents(int count)
    {
        var chapters = Chapters(count);
        var selection = NarrativePatternPipeline.ResolveChapterSelection(chapters, []);
        var content = selection.Chapters.ToDictionary(
            chapter => chapter.FilePath,
            chapter => LongText($"第{chapter.ChapterNumber}章"));
        return NarrativePatternPipeline.BuildChapterDocuments(selection, content);
    }

    private static IReadOnlyList<NarrativePatternChapterSummary> Summaries(int count)
    {
        return Documents(count)
            .Select(document => new NarrativePatternChapterSummary(
                document.ChapterId,
                document.ChapterNumber,
                document.ContentHash,
                $"第{document.ChapterNumber}章摘要",
                ["转折点"]))
            .ToArray();
    }

    private static string LongText(string seed = "雨夜")
    {
        return string.Join(
            "\n",
            Enumerable.Range(1, 12).Select(index =>
                $"{seed}段落{index}。雨声把街口压低，人物在证词和沉默之间移动，线索不断改变读者对失踪案的判断。"));
    }
}
