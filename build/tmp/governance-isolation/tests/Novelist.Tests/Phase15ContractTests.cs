using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.Tests;

public sealed class Phase15ContractTests
{
    [Fact]
    public void AppConfigPayloadCarriesUpdateCheckConfigurationWithStableSnakeCaseJsonNames()
    {
        var payload = new AppConfigPayload(
            Initialized: true,
            DataDir: @"D:\novelist\data",
            UpdateCheck: new UpdateCheckConfigurationPayload(
                EndpointUrl: "https://updates.example.test/novelist/releases.json",
                DefaultEnabled: true,
                TimeoutMs: 2500),
            ImportRecovery: new NovelImportReconciliationResultPayload(
                ReconciledRuns:
                [
                    new NovelImportRunPayload(
                        TaskId: "import-recovered-1",
                        State: NovelImportRunStates.CleanupCompleted,
                        Stage: "cleanup_completed",
                        SourceDisplayName: "sample.txt",
                        SourcePathHash: "sha256:path",
                        ParserType: NovelImportKinds.Txt,
                        CreatedNovelId: 42,
                        CreatedFileRoots: ["novels/42"],
                        SkippedChapters: [],
                        Diagnostics: [],
                        Warnings: [],
                        Error: null,
                        StartedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
                        UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:01Z"),
                        CompletedAt: DateTimeOffset.Parse("2026-07-07T00:00:01Z"))
                ],
                BlockedRuns: [],
                Diagnostics: [],
                ReconciledAt: DateTimeOffset.Parse("2026-07-07T00:00:02Z")));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;
        Assert.True(root.GetProperty("initialized").GetBoolean());
        Assert.Equal(@"D:\novelist\data", root.GetProperty("data_dir").GetString());

        var updateCheck = root.GetProperty("update_check");
        Assert.Equal("https://updates.example.test/novelist/releases.json", updateCheck.GetProperty("endpoint_url").GetString());
        Assert.True(updateCheck.GetProperty("default_enabled").GetBoolean());
        Assert.Equal(2500, updateCheck.GetProperty("timeout_ms").GetInt32());
        Assert.Equal("import-recovered-1", root.GetProperty("import_recovery").GetProperty("reconciled_runs")[0].GetProperty("task_id").GetString());
        Assert.False(root.TryGetProperty("updateCheck", out _));
        Assert.False(root.TryGetProperty("importRecovery", out _));
        Assert.False(updateCheck.TryGetProperty("EndpointUrl", out _));
    }

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
            CurrentChapterIndex: 1,
            CurrentChapterTitle: "Rain",
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
        Assert.Equal(1, progressJson.RootElement.GetProperty("current_chapter_index").GetInt32());
        Assert.Equal("Rain", progressJson.RootElement.GetProperty("current_chapter_title").GetString());
        Assert.False(progressJson.RootElement.TryGetProperty("currentChapterTitle", out _));

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
            StatsSchemaVersion: "style_sample_stats_v2",
            Stats: new StyleSampleStatsPayload(
                SchemaVersion: "style_sample_stats_v2",
                CharacterCount: 120,
                WordCount: 80,
                SentenceCount: 6,
                SentenceLengthDistribution: [18, 20, 22, 19, 21, 20],
                AverageSentenceChars: 20,
                SentenceLengthStdDev: 1.291,
                PunctuationPer100Chars: 12.5,
                QuoteDensity: 3.3,
                ParagraphCount: 3,
                AverageParagraphChars: 40,
                DialogueRatio: 0.1,
                InteriorityRatio: 0.35,
                SensoryRatio: 0.5),
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
        var extractionRun = new StyleSkillExtractionRunPayload(
            TaskId: "style-task-1",
            Status: "completed",
            Stage: "skill_preview",
            ProgressCompleted: 1,
            ProgressTotal: 1,
            SampleIds: [11],
            SkillName: "雨夜克制",
            SkillPreview: "---\nname: 雨夜克制\n---",
            SkillFilePath: "skills/雨夜克制.md",
            Diagnostics: [],
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:01Z"),
            CompletedAt: DateTimeOffset.Parse("2026-07-07T00:00:01Z"));
        var pattern = new StartNarrativePatternExtractionPayload(
            TaskId: "pattern-task-1",
            NovelId: 42,
            ChapterRanges: [new ChapterRangePayload(1, 3)],
            ProviderName: "fake",
            ModelId: "fake-model",
            ReasoningEffort: "low",
            SkillName: "悬疑推进",
            SelectedChapterIds: [101, 102, 103]);
        var patternRun = new NarrativePatternRunPayload(
            TaskId: "pattern-task-1",
            NovelId: 42,
            Status: "running",
            Stage: "phase_compression",
            ProgressCompleted: 2,
            ProgressTotal: 5,
            ChapterRanges: [new ChapterRangePayload(1, 3)],
            SelectedChapterIds: [101, 102, 103],
            SkillName: "悬疑推进",
            SkillPreview: "",
            Diagnostics: [],
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:01Z"),
            CompletedAt: null);
        var patternProgress = new NarrativePatternProgressPayload(
            TaskId: "pattern-task-1",
            Status: "running",
            Stage: "phase_compression",
            ProgressCompleted: 2,
            ProgressTotal: 5,
            Message: "Compressing narrative phases",
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:01Z"),
            LlmStatus: "calling",
            Round: 2,
            BatchIndex: 1,
            BatchTotal: 3,
            TokenEstimate: 4096,
            BoundaryCount: 2,
            SummaryCount: 3,
            PhaseCount: 2);
        var commit = new GitCommitSummaryPayload(
            CommitId: "abcdef",
            ShortCommitId: "abcdef",
            AuthorName: "Author",
            AuthorEmail: "author@example.com",
            Message: "commit",
            CommittedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            ChangedFileCount: 2,
            Insertions: 10,
            Deletions: 3);
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
        Assert.Equal("style_sample_stats_v2", sampleJson.RootElement.GetProperty("stats_schema_version").GetString());
        var statsJson = sampleJson.RootElement.GetProperty("stats");
        Assert.Equal("style_sample_stats_v2", statsJson.GetProperty("schema_version").GetString());
        Assert.Equal(80, statsJson.GetProperty("word_count").GetInt32());
        Assert.Equal(18, statsJson.GetProperty("sentence_length_distribution")[0].GetInt32());
        Assert.Equal(1.291, statsJson.GetProperty("sentence_length_std_dev").GetDouble());
        Assert.Equal(3.3, statsJson.GetProperty("quote_density").GetDouble());
        Assert.Equal(3, statsJson.GetProperty("paragraph_count").GetInt32());
        Assert.Equal("source_hash", sampleJson.RootElement.GetProperty("source_metadata").EnumerateObject().Last().Name);

        using var extractionJson = JsonDocument.Parse(JsonSerializer.Serialize(extraction, BridgeJson.SerializerOptions));
        Assert.Equal("style-task-1", extractionJson.RootElement.GetProperty("task_id").GetString());
        Assert.Equal(11, extractionJson.RootElement.GetProperty("sample_ids")[0].GetInt64());
        using var extractionRunJson = JsonDocument.Parse(JsonSerializer.Serialize(extractionRun, BridgeJson.SerializerOptions));
        Assert.Equal("skills/雨夜克制.md", extractionRunJson.RootElement.GetProperty("skill_file_path").GetString());
        Assert.False(extractionRunJson.RootElement.TryGetProperty("skillFilePath", out _));

        using var patternJson = JsonDocument.Parse(JsonSerializer.Serialize(pattern, BridgeJson.SerializerOptions));
        Assert.Equal("pattern-task-1", patternJson.RootElement.GetProperty("task_id").GetString());
        Assert.Equal(1, patternJson.RootElement.GetProperty("chapter_ranges")[0].GetProperty("start_chapter").GetInt32());
        Assert.Equal(101, patternJson.RootElement.GetProperty("selected_chapter_ids")[0].GetInt64());
        using var patternRunJson = JsonDocument.Parse(JsonSerializer.Serialize(patternRun, BridgeJson.SerializerOptions));
        Assert.Equal(103, patternRunJson.RootElement.GetProperty("selected_chapter_ids")[2].GetInt64());
        using var patternProgressJson = JsonDocument.Parse(JsonSerializer.Serialize(patternProgress, BridgeJson.SerializerOptions));
        Assert.Equal("calling", patternProgressJson.RootElement.GetProperty("llm_status").GetString());
        Assert.Equal(2, patternProgressJson.RootElement.GetProperty("round").GetInt32());
        Assert.Equal(1, patternProgressJson.RootElement.GetProperty("batch_index").GetInt32());
        Assert.Equal(4096, patternProgressJson.RootElement.GetProperty("token_estimate").GetInt32());
        Assert.False(patternProgressJson.RootElement.TryGetProperty("llmStatus", out _));

        using var commitJson = JsonDocument.Parse(JsonSerializer.Serialize(commit, BridgeJson.SerializerOptions));
        Assert.Equal(2, commitJson.RootElement.GetProperty("changed_file_count").GetInt32());
        Assert.Equal(10, commitJson.RootElement.GetProperty("insertions").GetInt32());
        Assert.Equal(3, commitJson.RootElement.GetProperty("deletions").GetInt32());
        Assert.False(commitJson.RootElement.TryGetProperty("changedFileCount", out _));

        using var updateJson = JsonDocument.Parse(JsonSerializer.Serialize(update, BridgeJson.SerializerOptions));
        Assert.Equal("update-task-1", updateJson.RootElement.GetProperty("task_id").GetString());
        Assert.Equal("https://example.com/releases/1.1.0", updateJson.RootElement.GetProperty("release_url").GetString());

        using var diagnosticJson = JsonDocument.Parse(JsonSerializer.Serialize(diagnostic, BridgeJson.SerializerOptions));
        Assert.Equal("StartNovelImport", diagnosticJson.RootElement.GetProperty("bridge_method").GetString());
        Assert.False(diagnosticJson.RootElement.TryGetProperty("api_key", out _));
        Assert.False(diagnosticJson.RootElement.TryGetProperty("source_text", out _));
    }

    [Fact]
    public void SampleBackedReferenceStyleProfilePayloadsUseExplicitSourceMetadataWithoutText()
    {
        var build = new BuildReferenceStyleProfilePayload(
            NovelId: 42,
            Title: "样本画像",
            Description: "from curated samples",
            AnchorIds: [],
            AllowedLicenseStatuses: [],
            AllowedSourceTrustLevels: [],
            BuildId: "style-build-sample",
            StyleSampleIds: [11, 12]);
        var evidence = new ReferenceStyleEvidenceSpanPayload(
            EvidenceId: "style-sample-evidence-1",
            ProfileId: 99,
            AnchorId: 0,
            SourceSegmentId: "style-sample:11:sentence:1",
            MaterialId: "style-sample-material:11:sentence:1",
            FeatureKey: "dialogue_ratio",
            Label: "style_sample_stats",
            StartOffset: 0,
            EndOffset: 18,
            TextHash: "sha256:text",
            Confidence: 0.75,
            AnalyzerSource: ReferenceStyleAnalyzerSources.DeterministicBaseline,
            SourceType: ReferenceStyleProfileSourceTypes.StyleSample,
            StyleSampleId: 11);
        var profile = new ReferenceStyleProfilePayload(
            ProfileId: 99,
            NovelId: 42,
            Title: "样本画像",
            Description: "from curated samples",
            Status: ReferenceStyleProfileStatuses.Active,
            AnalyzerVersion: ReferenceStyleAnalyzerVersions.DeterministicV1,
            FeatureSchemaVersion: ReferenceStyleFeatureSchemaVersions.V1,
            AnalyzerSource: ReferenceStyleAnalyzerSources.DeterministicBaseline,
            SourceAnchorIds: [],
            SourceHashes: ["sha256:text"],
            AllowedLicenseStatuses: [],
            AllowedSourceTrustLevels: [],
            AggregateConfidence: 0.75,
            Features: new ReferenceStyleFeatureVectorPayload(
                NumericFeatures:
                [
                    new ReferenceStyleNumericFeaturePayload(
                        "dialogue_ratio",
                        0.35,
                        "ratio",
                        0.75,
                        ["style-sample-evidence-1"])
                ],
                DistributionFeatures: [],
                CategoricalFeatures: []),
            EvidenceSpans: [evidence],
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            ArchivedAt: null,
            SourceStyleSampleIds: [11, 12]);
        var status = new ReferenceStyleProfileBuildStatusPayload(
            BuildId: "style-build-sample",
            NovelId: 42,
            ProfileId: 99,
            Title: "样本画像",
            Status: ReferenceStyleProfileBuildStatuses.Completed,
            Stage: ReferenceStyleProfileBuildStages.Completed,
            ProgressCompleted: 7,
            ProgressTotal: 7,
            AnchorIds: [],
            SourceHashes: ["sha256:text"],
            Diagnostics: [],
            ErrorCode: null,
            ErrorMessage: null,
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:01Z"),
            CompletedAt: DateTimeOffset.Parse("2026-07-07T00:00:01Z"),
            CancelledAt: null,
            StyleSampleIds: [11, 12]);

        using var buildJson = JsonDocument.Parse(JsonSerializer.Serialize(build, BridgeJson.SerializerOptions));
        Assert.Equal(11, buildJson.RootElement.GetProperty("style_sample_ids")[0].GetInt64());
        Assert.False(buildJson.RootElement.TryGetProperty("sample_text", out _));
        Assert.False(buildJson.RootElement.TryGetProperty("content", out _));

        using var profileJson = JsonDocument.Parse(JsonSerializer.Serialize(profile, BridgeJson.SerializerOptions));
        var profileRoot = profileJson.RootElement;
        Assert.Equal(11, profileRoot.GetProperty("source_style_sample_ids")[0].GetInt64());
        var evidenceRoot = profileRoot.GetProperty("evidence_spans")[0];
        Assert.Equal("style_sample", evidenceRoot.GetProperty("source_type").GetString());
        Assert.Equal(11, evidenceRoot.GetProperty("style_sample_id").GetInt64());
        Assert.False(evidenceRoot.TryGetProperty("text", out _));
        Assert.False(evidenceRoot.TryGetProperty("sample_text", out _));
        Assert.False(evidenceRoot.TryGetProperty("content", out _));

        using var statusJson = JsonDocument.Parse(JsonSerializer.Serialize(status, BridgeJson.SerializerOptions));
        Assert.Equal(12, statusJson.RootElement.GetProperty("style_sample_ids")[1].GetInt64());
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
