using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.Tests;

public sealed class Phase15ContractTests
{
    [Fact]
    public void NovelImportPayloadsUseStableSnakeCaseJsonNamesWithoutSourceText()
    {
        var request = new StartNovelImportPayload(
            TaskId: "import-task-1",
            SourcePath: @"D:\books\sample.txt",
            SourceDisplayName: "sample.txt",
            ImportKind: NovelImportKinds.Txt,
            RequestedTitle: "Imported Novel",
            CommitMessage: "import novel");
        var progress = new NovelImportProgressPayload(
            TaskId: "import-task-1",
            State: NovelImportRunStates.WritingFiles,
            Stage: "writing_files",
            ProgressCompleted: 2,
            ProgressTotal: 5,
            Message: "Writing chapters",
            CreatedNovelId: 42,
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"));
        var run = new NovelImportRunPayload(
            TaskId: "import-task-1",
            State: NovelImportRunStates.CompletedWithWarning,
            Stage: "git_commit",
            SourceDisplayName: "sample.txt",
            SourcePathHash: "sha256:path",
            ParserType: NovelImportKinds.Txt,
            CreatedNovelId: 42,
            CreatedFileRoots: ["novels/42"],
            SkippedChapters:
            [
                new NovelImportSkippedChapterPayload(
                    Index: 3,
                    Title: "Empty chapter",
                    Reason: "empty_content")
            ],
            Diagnostics:
            [
                new NovelImportDiagnosticPayload(
                    Code: "decoder.gb18030",
                    Message: "Decoded with GB18030 fallback.",
                    Detail: "confidence=high",
                    Severity: "info")
            ],
            Warnings:
            [
                new NovelImportWarningPayload(
                    Code: "git.commit_failed",
                    Message: "Import completed but Git commit failed.",
                    Detail: "Repository remains usable.")
            ],
            Error: null,
            StartedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:05Z"),
            CompletedAt: DateTimeOffset.Parse("2026-07-07T00:00:05Z"));

        using var requestJson = JsonDocument.Parse(JsonSerializer.Serialize(request, BridgeJson.SerializerOptions));
        var requestRoot = requestJson.RootElement;
        Assert.Equal("import-task-1", requestRoot.GetProperty("task_id").GetString());
        Assert.Equal(@"D:\books\sample.txt", requestRoot.GetProperty("source_path").GetString());
        Assert.Equal("txt", requestRoot.GetProperty("import_kind").GetString());
        Assert.False(requestRoot.TryGetProperty("TaskId", out _));

        using var progressJson = JsonDocument.Parse(JsonSerializer.Serialize(progress, BridgeJson.SerializerOptions));
        Assert.Equal("writing_files", progressJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(2, progressJson.RootElement.GetProperty("progress_completed").GetInt32());

        using var runJson = JsonDocument.Parse(JsonSerializer.Serialize(run, BridgeJson.SerializerOptions));
        var runRoot = runJson.RootElement;
        Assert.Equal("completed_with_warning", runRoot.GetProperty("state").GetString());
        Assert.Equal("sha256:path", runRoot.GetProperty("source_path_hash").GetString());
        Assert.Equal("empty_content", runRoot.GetProperty("skipped_chapters")[0].GetProperty("reason").GetString());
        Assert.False(runRoot.TryGetProperty("source_text", out _));
        Assert.False(runRoot.TryGetProperty("candidate_text", out _));
        Assert.False(runRoot.TryGetProperty("prompt", out _));
        Assert.False(runRoot.TryGetProperty("content", out _));
    }

    [Fact]
    public void Phase15StylePatternGitUpdateAndErrorPayloadsUseStableSnakeCaseJsonNames()
    {
        var sample = new StyleSamplePayload(
            SampleId: 11,
            NovelId: 42,
            IsGlobal: false,
            Name: "雨夜样本",
            Preview: "雨声压低...",
            Tags: ["rain", "restraint"],
            StatsSchemaVersion: "style-sample-stats-v1",
            Stats: new StyleSampleStatsPayload(
                CharacterCount: 120,
                SentenceCount: 6,
                AverageSentenceChars: 20,
                DialogueRatio: 0.1,
                InteriorityRatio: 0.35,
                SensoryRatio: 0.5,
                PunctuationPer100Chars: 12.5),
            SourceMetadata: new StyleSampleSourceMetadataPayload(
                SourceType: "manual",
                SourceId: "chapter-1",
                SourceHash: "hash"),
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"));
        var extraction = new StartStyleSkillExtractionPayload(
            TaskId: "style-task-1",
            NovelId: 42,
            SampleIds: [11],
            ProviderName: "fake",
            ModelId: "fake-model",
            ReasoningEffort: "low",
            SkillName: "雨夜克制");
        var pattern = new StartNarrativePatternExtractionPayload(
            TaskId: "pattern-task-1",
            NovelId: 42,
            ChapterRanges: [new ChapterRangePayload(1, 3)],
            ProviderName: "fake",
            ModelId: "fake-model",
            ReasoningEffort: "low",
            SkillName: "悬疑推进");
        var commit = new GitCommitSummaryPayload(
            CommitId: "abcdef",
            ShortCommitId: "abcdef",
            AuthorName: "Author",
            AuthorEmail: "author@example.com",
            Message: "commit",
            CommittedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            ChangedFileCount: 2);
        var update = new UpdateCheckResultPayload(
            TaskId: "update-task-1",
            Status: "update_available",
            CurrentVersion: "1.0.0",
            LatestVersion: "1.1.0",
            ReleaseUrl: "https://example.com/releases/1.1.0",
            CheckedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            ErrorCode: null,
            ErrorMessage: null);
        var diagnostic = new CopyableDiagnosticPayload(
            Code: "import.decode_failed",
            Message: "Decode failed.",
            Detail: "redacted",
            Operation: "StartNovelImport",
            TaskId: "import-task-1",
            RunId: null,
            BridgeMethod: "StartNovelImport",
            Timestamp: DateTimeOffset.Parse("2026-07-07T00:00:00Z"));

        using var sampleJson = JsonDocument.Parse(JsonSerializer.Serialize(sample, BridgeJson.SerializerOptions));
        Assert.Equal("style-sample-stats-v1", sampleJson.RootElement.GetProperty("stats_schema_version").GetString());
        Assert.Equal("source_hash", sampleJson.RootElement.GetProperty("source_metadata").EnumerateObject().Last().Name);

        using var extractionJson = JsonDocument.Parse(JsonSerializer.Serialize(extraction, BridgeJson.SerializerOptions));
        Assert.Equal("style-task-1", extractionJson.RootElement.GetProperty("task_id").GetString());
        Assert.Equal(11, extractionJson.RootElement.GetProperty("sample_ids")[0].GetInt64());

        using var patternJson = JsonDocument.Parse(JsonSerializer.Serialize(pattern, BridgeJson.SerializerOptions));
        Assert.Equal("pattern-task-1", patternJson.RootElement.GetProperty("task_id").GetString());
        Assert.Equal(1, patternJson.RootElement.GetProperty("chapter_ranges")[0].GetProperty("start_chapter").GetInt32());

        using var commitJson = JsonDocument.Parse(JsonSerializer.Serialize(commit, BridgeJson.SerializerOptions));
        Assert.Equal("changed_file_count", commitJson.RootElement.EnumerateObject().Last().Name);

        using var updateJson = JsonDocument.Parse(JsonSerializer.Serialize(update, BridgeJson.SerializerOptions));
        Assert.Equal("update-task-1", updateJson.RootElement.GetProperty("task_id").GetString());
        Assert.Equal("https://example.com/releases/1.1.0", updateJson.RootElement.GetProperty("release_url").GetString());

        using var diagnosticJson = JsonDocument.Parse(JsonSerializer.Serialize(diagnostic, BridgeJson.SerializerOptions));
        Assert.Equal("StartNovelImport", diagnosticJson.RootElement.GetProperty("bridge_method").GetString());
        Assert.False(diagnosticJson.RootElement.TryGetProperty("api_key", out _));
        Assert.False(diagnosticJson.RootElement.TryGetProperty("source_text", out _));
    }

    [Fact]
    public void CompatibilityRegistryIncludesPhase15MethodNames()
    {
        string[] expected =
        [
            "PickNovelImportFile",
            "StartNovelImport",
            "CancelNovelImport",
            "GetNovelImportRun",
            "GetNovelImportRecoveryStatus",
            "ReconcileNovelImportRuns",
            "CreateStyleSample",
            "UpdateStyleSample",
            "DeleteStyleSample",
            "GetStyleSample",
            "SearchStyleSamples",
            "ExtractStyleSkillFromSamples",
            "CancelStyleSkillExtraction",
            "GetStyleSkillExtractionRun",
            "StartNarrativePatternExtraction",
            "CancelNarrativePatternExtraction",
            "GetNarrativePatternRun",
            "GetNarrativePatternTrace",
            "GetGitCommits",
            "GetGitCommitFiles",
            "GetGitFileDiff",
            "GetGitAuthorSettings",
            "SaveGitAuthorSettings",
            "CheckForUpdates",
            "GetUpdateCheckSettings",
            "SaveUpdateCheckSettings",
            "GetLayoutSettings",
            "SaveLayoutSettings",
            "GetWindowSettings",
            "SaveWindowSettings"
        ];

        foreach (var method in expected)
        {
            Assert.Contains(method, BridgeCompatibilityAppMethods.MethodNames);
        }
    }
}
