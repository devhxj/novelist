using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceStyleProfileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "novelist-style-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BuildStyleProfileCreatesDeterministicBaselineWithoutCopyingSourceText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("风格画像测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "style-source.md",
            """
            # 第一章 雨夜

            雨声压低了整条街的呼吸。林岚在门口停了很久，指节慢慢发紧。

            她说：“你终于来了。”

            后来灯光暗下去，他没有回答，只把钥匙放回桌面。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "雨夜参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                Visibility: ReferenceCorpusVisibilities.Workspace,
                SourceTrust: ReferenceSourceTrustLevels.UserVerified),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);

        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "雨夜克制风格",
                "deterministic baseline",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.True(profile.ProfileId > 0);
        Assert.Equal(ReferenceStyleProfileStatuses.Active, profile.Status);
        Assert.Equal("reference-style-deterministic-v1", profile.AnalyzerVersion);
        Assert.Equal(ReferenceStyleFeatureSchemaVersions.V1, profile.FeatureSchemaVersion);
        Assert.Equal(ReferenceStyleAnalyzerSources.DeterministicBaseline, profile.AnalyzerSource);
        Assert.Equal([anchor.AnchorId], profile.SourceAnchorIds);
        Assert.Equal([anchor.SourceFileHash], profile.SourceHashes);
        Assert.InRange(profile.AggregateConfidence, 0.5, 1.0);
        Assert.NotEmpty(profile.EvidenceSpans);
        Assert.Contains(profile.Features.NumericFeatures, feature => feature.FeatureKey == "average_sentence_chars" && feature.Value > 0);
        Assert.Contains(profile.Features.NumericFeatures, feature => feature.FeatureKey == "dialogue_ratio" && feature.Value > 0);
        Assert.Contains(profile.Features.DistributionFeatures, feature => feature.FeatureKey == "sentence_length_distribution");
        Assert.Contains(profile.Features.CategoricalFeatures, feature => feature.FeatureKey == "dominant_technique");
        Assert.All(profile.EvidenceSpans, evidence =>
        {
            Assert.Equal(profile.ProfileId, evidence.ProfileId);
            Assert.Equal(anchor.AnchorId, evidence.AnchorId);
            Assert.False(string.IsNullOrWhiteSpace(evidence.SourceSegmentId));
            Assert.False(string.IsNullOrWhiteSpace(evidence.TextHash));
            Assert.True(evidence.EndOffset >= evidence.StartOffset);
        });

        var reloaded = await styleService.GetStyleProfileAsync(novel.Id, profile.ProfileId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal(profile.ProfileId, reloaded.ProfileId);
        Assert.Equal(profile.EvidenceSpans.Count, reloaded.EvidenceSpans.Count);

        var persisted = await ReadPersistedStyleProfileAsync(options, profile.ProfileId);
        Assert.DoesNotContain("雨声压低", persisted.FeatureVectorJson, StringComparison.Ordinal);
        Assert.DoesNotContain("你终于来了", persisted.FeatureVectorJson, StringComparison.Ordinal);
        Assert.DoesNotContain("text", persisted.EvidenceColumns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildStyleProfileFromStyleSamplesMapsStatsAndPersistsSampleSourceWithoutText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("样本画像测试", "", ""), CancellationToken.None);
        var samples = new FileSystemStyleSampleService(options, novels);
        const string sampleSentinel = "雨声压低了整条街的呼吸";
        var sample = await samples.CreateSampleAsync(
            new CreateStyleSamplePayload(
                novel.Id,
                IsGlobal: false,
                Name: "雨夜样本",
                Content:
                $"""
                {sampleSentinel}。林岚在门口停了很久，指节慢慢发紧。

                她说：“你终于来了。”

                后来灯光暗下去，他没有回答，只把钥匙放回桌面。
                """,
                Tags: ["rain", "restraint"],
                SourceMetadata: new StyleSampleSourceMetadataPayload("manual", "chapter-1", "sample-source-hash")),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels, llmAnalyzer: null, styleSamples: samples);

        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "样本风格画像",
                "sample backed profile",
                AnchorIds: [],
                AllowedLicenseStatuses: [],
                AllowedSourceTrustLevels: [],
                StyleSampleIds: [sample.SampleId]),
            CancellationToken.None);

        Assert.True(profile.ProfileId > 0);
        Assert.Equal(ReferenceStyleProfileStatuses.Active, profile.Status);
        Assert.Empty(profile.SourceAnchorIds);
        Assert.Equal([sample.SampleId], profile.SourceStyleSampleIds);
        Assert.NotEmpty(profile.SourceHashes);
        Assert.NotEmpty(profile.EvidenceSpans);
        Assert.All(profile.EvidenceSpans, evidence =>
        {
            Assert.Equal(0, evidence.AnchorId);
            Assert.Equal(ReferenceStyleProfileSourceTypes.StyleSample, evidence.SourceType);
            Assert.Equal(sample.SampleId, evidence.StyleSampleId);
            Assert.StartsWith($"style-sample:{sample.SampleId}:", evidence.SourceSegmentId, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(evidence.TextHash));
            Assert.True(evidence.EndOffset > evidence.StartOffset);
        });

        Assert.Equal(sample.Stats.AverageSentenceChars, ReadNumericFeature(profile.Features, "average_sentence_chars"), precision: 4);
        Assert.Equal(sample.Stats.DialogueRatio, ReadNumericFeature(profile.Features, "dialogue_ratio"), precision: 4);
        Assert.Equal(sample.Stats.InteriorityRatio, ReadNumericFeature(profile.Features, "interiority_ratio"), precision: 4);
        Assert.Equal(sample.Stats.SensoryRatio, ReadNumericFeature(profile.Features, "sensory_ratio"), precision: 4);

        var reloaded = await styleService.GetStyleProfileAsync(novel.Id, profile.ProfileId, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.Equal([sample.SampleId], reloaded.SourceStyleSampleIds);
        Assert.Equal(profile.EvidenceSpans.Count, reloaded.EvidenceSpans.Count);

        var sampleSources = await ReadSampleProfileSourcesAsync(options, profile.ProfileId);
        var source = Assert.Single(sampleSources);
        Assert.Equal(sample.SampleId, source.SampleId);
        Assert.Equal(novel.Id, source.NovelId);
        Assert.False(source.IsGlobal);
        Assert.Equal(sample.Stats.SchemaVersion, source.StatsSchemaVersion);
        Assert.Equal(sample.SourceMetadata!.SourceHash, source.SourceHash);
        Assert.True(source.MaterialCount > 0);
        Assert.True(source.SegmentCount > 0);

        var persisted = await ReadPersistedStyleProfileAsync(options, profile.ProfileId);
        Assert.DoesNotContain(sampleSentinel, persisted.FeatureVectorJson, StringComparison.Ordinal);
        var sampleEvidenceColumns = await ReadTableColumnsAsync(options, "reference_style_profile_sample_evidence");
        Assert.DoesNotContain("text", sampleEvidenceColumns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", sampleEvidenceColumns, StringComparer.OrdinalIgnoreCase);

        var status = await styleService.GetStyleProfileBuildStatusAsync(
            new GetReferenceStyleProfileBuildStatusPayload(novel.Id, "style-build-sample"),
            CancellationToken.None);
        Assert.Null(status);
    }

    [Fact]
    public async Task BuildStyleProfileFromStyleSamplesRespectsGlobalAndNovelScope()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("样本作用域测试", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("其他样本作用域测试", "", ""), CancellationToken.None);
        var samples = new FileSystemStyleSampleService(options, novels);
        var localSample = await samples.CreateSampleAsync(
            new CreateStyleSamplePayload(
                novel.Id,
                IsGlobal: false,
                Name: "当前小说样本",
                Content: "她说：“门口别停。”雨声压低了整条街的呼吸。",
                Tags: [],
                SourceMetadata: null),
            CancellationToken.None);
        var globalSample = await samples.CreateSampleAsync(
            new CreateStyleSamplePayload(
                NovelId: null,
                IsGlobal: true,
                Name: "全局样本",
                Content: "灯光落在窗边，他忽然明白自己不能回头。",
                Tags: [],
                SourceMetadata: null),
            CancellationToken.None);
        var otherSample = await samples.CreateSampleAsync(
            new CreateStyleSamplePayload(
                otherNovel.Id,
                IsGlobal: false,
                Name: "其他小说样本",
                Content: "其他小说里的门外脚步声越来越近。",
                Tags: [],
                SourceMetadata: null),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels, llmAnalyzer: null, styleSamples: samples);

        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "可访问样本画像",
                "",
                AnchorIds: [],
                AllowedLicenseStatuses: [],
                AllowedSourceTrustLevels: [],
                StyleSampleIds: [localSample.SampleId, globalSample.SampleId]),
            CancellationToken.None);

        Assert.Equal([localSample.SampleId, globalSample.SampleId], profile.SourceStyleSampleIds);
        await Assert.ThrowsAsync<ArgumentException>(async () => await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "越权样本画像",
                "",
                AnchorIds: [],
                AllowedLicenseStatuses: [],
                AllowedSourceTrustLevels: [],
                StyleSampleIds: [otherSample.SampleId]),
            CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task TenMbSourceBuildsDeterministicStyleProfileWithoutPersistingSourceText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("大语料画像测试", "", ""), CancellationToken.None);
        var source = BuildLargeChineseSource(minUtf8Bytes: 10_000_000);
        const string sourceSentinel = "雨声压低了整条街的呼吸";
        const string sourceTailSentinel = "答案就在灯光背后";
        Assert.InRange(Encoding.UTF8.GetByteCount(source), 10_000_000, 19_500_000);
        var sourcePath = CreateSourceFile("large-style-profile.md", source);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(
                novel.Id,
                "10MB 风格画像参考",
                null,
                sourcePath,
                "markdown",
                "user_provided",
                Visibility: ReferenceCorpusVisibilities.Workspace,
                SourceTrust: ReferenceSourceTrustLevels.UserVerified),
            CancellationToken.None);

        var status = await anchorService.GetBuildStatusAsync(novel.Id, anchor.AnchorId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceAnchorBuildStates.Ready, status.Status);
        Assert.True(status.SourceSegmentCount > 500);
        Assert.True(status.MaterialCount > 500);

        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "10MB 确定性画像",
                "large source deterministic boundary",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.True(profile.ProfileId > 0);
        Assert.Equal(ReferenceStyleProfileStatuses.Active, profile.Status);
        Assert.Equal(ReferenceStyleAnalyzerVersions.DeterministicV1, profile.AnalyzerVersion);
        Assert.Equal(ReferenceStyleAnalyzerSources.DeterministicBaseline, profile.AnalyzerSource);
        Assert.Equal([anchor.AnchorId], profile.SourceAnchorIds);
        Assert.Equal([anchor.SourceFileHash], profile.SourceHashes);
        Assert.InRange(profile.AggregateConfidence, 0.8, 1.0);
        Assert.NotEmpty(profile.EvidenceSpans);
        Assert.InRange(profile.EvidenceSpans.Count, 1, 64);
        Assert.All(profile.EvidenceSpans, evidence =>
        {
            Assert.Equal(profile.ProfileId, evidence.ProfileId);
            Assert.Equal(anchor.AnchorId, evidence.AnchorId);
            Assert.False(string.IsNullOrWhiteSpace(evidence.SourceSegmentId));
            Assert.False(string.IsNullOrWhiteSpace(evidence.TextHash));
            Assert.True(evidence.EndOffset > evidence.StartOffset);
        });

        Assert.True(ReadNumericFeature(profile.Features, "material_count") > 500);
        Assert.True(ReadNumericFeature(profile.Features, "sentence_count") > 0);
        Assert.True(ReadNumericFeature(profile.Features, "paragraph_count") > 0);
        Assert.True(ReadNumericFeature(profile.Features, "sensory_ratio") > 0);
        Assert.True(ReadNumericFeature(profile.Features, "hook_marker_ratio") > 0);

        var profileSource = Assert.Single(await ReadProfileSourcesAsync(options, profile.ProfileId));
        Assert.Equal(anchor.AnchorId, profileSource.AnchorId);
        Assert.True(profileSource.MaterialCount > 500);
        Assert.True(profileSource.SegmentCount > 500);

        var persisted = await ReadPersistedStyleProfileAsync(options, profile.ProfileId);
        Assert.DoesNotContain(sourceSentinel, persisted.FeatureVectorJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceTailSentinel, persisted.FeatureVectorJson, StringComparison.Ordinal);
        Assert.DoesNotContain("text", persisted.EvidenceColumns, StringComparer.OrdinalIgnoreCase);

        var analysisRun = Assert.Single(await ReadAnalysisRunsAsync(options, profile.ProfileId));
        Assert.Equal(ReferenceStyleAnalyzerSources.DeterministicBaseline, analysisRun.AnalyzerSource);
        Assert.Equal("completed", analysisRun.Status);
        Assert.DoesNotContain(sourceSentinel, analysisRun.DiagnosticsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceTailSentinel, analysisRun.DiagnosticsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveAndRestoreStyleProfileHidesDefaultLibraryRowsWithoutDeletingEvidence()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像归档测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "archive-style-source.md",
            """
            # 第一章

            她说：“门口别停。”

            雨声压低了整条街的呼吸。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "归档参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "可归档画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        var archived = await styleService.ArchiveStyleProfileAsync(
            new ArchiveReferenceStyleProfilePayload(novel.Id, profile.ProfileId),
            CancellationToken.None);

        Assert.Equal(ReferenceStyleProfileStatuses.Archived, archived.Status);
        Assert.NotNull(archived.ArchivedAt);
        Assert.NotEmpty(archived.EvidenceSpans);
        Assert.Equal(profile.EvidenceSpans.Count, archived.EvidenceSpans.Count);

        var defaultList = await styleService.GetStyleProfilesAsync(
            new GetReferenceStyleProfilesPayload(novel.Id),
            CancellationToken.None);
        Assert.DoesNotContain(defaultList, item => item.ProfileId == profile.ProfileId);

        var archivedList = await styleService.GetStyleProfilesAsync(
            new GetReferenceStyleProfilesPayload(novel.Id, IncludeArchived: true),
            CancellationToken.None);
        var archivedSummary = Assert.Single(archivedList, item => item.ProfileId == profile.ProfileId);
        Assert.Equal(ReferenceStyleProfileStatuses.Archived, archivedSummary.Status);
        Assert.NotNull(archivedSummary.ArchivedAt);

        var detailWhileArchived = await styleService.GetStyleProfileAsync(novel.Id, profile.ProfileId, CancellationToken.None);
        Assert.NotNull(detailWhileArchived);
        Assert.Equal(ReferenceStyleProfileStatuses.Archived, detailWhileArchived.Status);
        Assert.Equal(profile.EvidenceSpans.Count, detailWhileArchived.EvidenceSpans.Count);

        var restored = await styleService.RestoreStyleProfileAsync(
            new RestoreReferenceStyleProfilePayload(novel.Id, profile.ProfileId),
            CancellationToken.None);

        Assert.Equal(ReferenceStyleProfileStatuses.Active, restored.Status);
        Assert.Null(restored.ArchivedAt);
        Assert.NotEmpty(restored.EvidenceSpans);
        var restoredDefaultList = await styleService.GetStyleProfilesAsync(
            new GetReferenceStyleProfilesPayload(novel.Id),
            CancellationToken.None);
        Assert.Contains(restoredDefaultList, item => item.ProfileId == profile.ProfileId);
    }

    [Fact]
    public async Task CompareStyleProfilesReturnsFeatureDeltasAndRejectsCrossNovelProfiles()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像比较测试", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("其他画像比较测试", "", ""), CancellationToken.None);
        var dialogueSourcePath = CreateSourceFile(
            "compare-dialogue.md",
            """
            # 第一章

            她说：“别回头。”

            他问：“为什么？”
            """);
        var quietSourcePath = CreateSourceFile(
            "compare-quiet.md",
            """
            # 第一章

            雨声压低了屋檐，灯影贴在墙上。

            他在门口停了很久，指节慢慢发紧。
            """);
        var otherSourcePath = CreateSourceFile(
            "compare-other.md",
            """
            # 第一章

            她说：“门外还有人。”
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var dialogueAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "对话比较参考", null, dialogueSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var quietAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "克制比较参考", null, quietSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var otherAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(otherNovel.Id, "跨小说比较参考", null, otherSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        var dialogueProfile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "对话画像",
                "",
                [dialogueAnchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var quietProfile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "克制画像",
                "",
                [quietAnchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var otherProfile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                otherNovel.Id,
                "跨小说画像",
                "",
                [otherAnchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        var comparison = await styleService.CompareStyleProfilesAsync(
            new CompareReferenceStyleProfilesPayload(novel.Id, dialogueProfile.ProfileId, quietProfile.ProfileId),
            CancellationToken.None);

        Assert.Equal(novel.Id, comparison.NovelId);
        Assert.Equal(dialogueProfile.ProfileId, comparison.LeftProfile.ProfileId);
        Assert.Equal(quietProfile.ProfileId, comparison.RightProfile.ProfileId);
        var dialogueRatio = Assert.Single(
            comparison.NumericDifferences,
            item => item.FeatureKey == "dialogue_ratio");
        Assert.NotNull(dialogueRatio.LeftValue);
        Assert.NotNull(dialogueRatio.RightValue);
        Assert.NotNull(dialogueRatio.AbsoluteDelta);
        Assert.True(dialogueRatio.AbsoluteDelta > 0);
        Assert.Equal(
            Math.Abs(dialogueRatio.LeftValue.Value - dialogueRatio.RightValue.Value),
            dialogueRatio.AbsoluteDelta.Value,
            precision: 8);
        Assert.Contains(comparison.DistributionDifferences, item => item.FeatureKey == "sentence_length_distribution");
        Assert.Contains(comparison.CategoricalDifferences, item => item.FeatureKey == "dominant_technique");

        await Assert.ThrowsAsync<ArgumentException>(async () => await styleService.CompareStyleProfilesAsync(
            new CompareReferenceStyleProfilesPayload(novel.Id, dialogueProfile.ProfileId, otherProfile.ProfileId),
            CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(async () => await styleService.ArchiveStyleProfileAsync(
            new ArchiveReferenceStyleProfilePayload(otherNovel.Id, dialogueProfile.ProfileId),
            CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(async () => await styleService.RestoreStyleProfileAsync(
            new RestoreReferenceStyleProfilePayload(otherNovel.Id, dialogueProfile.ProfileId),
            CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task BuildStyleProfileMigratesPrePhase14DatabaseWithoutChangingReferenceIds()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像迁移测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "migration-source.md",
            """
            # 第一章

            第一句压住节奏。

            第二句转入沉默。第三句留下钩子？
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "迁移参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var segmentsBefore = await ReadSegmentSignaturesAsync(options, anchor.AnchorId);
        var materialsBefore = await ReadMaterialSignaturesAsync(options, anchor.AnchorId);

        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "迁移画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.Equal(segmentsBefore, await ReadSegmentSignaturesAsync(options, anchor.AnchorId));
        Assert.Equal(materialsBefore, await ReadMaterialSignaturesAsync(options, anchor.AnchorId));
        Assert.True(await TableExistsAsync(options, "reference_style_profiles"));
        Assert.True(await TableExistsAsync(options, "reference_style_profile_evidence"));
        Assert.True(await TableExistsAsync(options, "reference_material_style_tags"));
    }

    [Fact]
    public async Task RebuildingStyleProfileFromSameSourceProducesStableFeatureValues()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像复现测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "repro-source.md",
            """
            # 第一章

            她说：“先别开门。”

            他心里明白，雨声已经压住了脚步。门外忽然安静下来？
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "复现参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);

        var first = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "复现画像 A",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var second = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "复现画像 B",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.Equal(first.SourceHashes, second.SourceHashes);
        Assert.Equal(first.AnalyzerVersion, second.AnalyzerVersion);
        Assert.Equal(FeatureSignature(first.Features), FeatureSignature(second.Features));
        Assert.NotEqual(first.ProfileId, second.ProfileId);
        Assert.NotEqual(
            first.EvidenceSpans.Select(evidence => evidence.EvidenceId).Order(StringComparer.Ordinal),
            second.EvidenceSpans.Select(evidence => evidence.EvidenceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ArchivingOrHardDeletingMaterialDoesNotSilentlyOrphanStyleEvidence()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像溯源测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "provenance-source.md",
            """
            # 第一章

            她说：“先别开门。”

            他停了一下，指尖贴着冰冷的门把。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "溯源参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "溯源画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var evidenceMaterialId = profile.EvidenceSpans
            .Select(evidence => evidence.MaterialId)
            .FirstOrDefault(materialId => !string.IsNullOrWhiteSpace(materialId));
        Assert.False(string.IsNullOrWhiteSpace(evidenceMaterialId));

        await anchorService.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, [evidenceMaterialId!]),
            CancellationToken.None);

        Assert.NotNull(await ReadMaterialArchivedAtAsync(options, evidenceMaterialId!));
        var afterArchive = await styleService.GetStyleProfileAsync(novel.Id, profile.ProfileId, CancellationToken.None);
        Assert.NotNull(afterArchive);
        Assert.Contains(afterArchive.EvidenceSpans, evidence => evidence.MaterialId == evidenceMaterialId);

        await Assert.ThrowsAsync<SqliteException>(async () => await HardDeleteMaterialAsync(options, evidenceMaterialId!));
        var afterHardDeleteAttempt = await styleService.GetStyleProfileAsync(novel.Id, profile.ProfileId, CancellationToken.None);
        Assert.NotNull(afterHardDeleteAttempt);
        Assert.Contains(afterHardDeleteAttempt.EvidenceSpans, evidence => evidence.MaterialId == evidenceMaterialId);
    }

    [Fact]
    public async Task SearchMaterialsUsesStyleProfileEvidenceWithoutCrossNovelBypass()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("风格检索测试", "", ""), CancellationToken.None);
        var otherNovel = await novels.CreateNovelAsync(new CreateNovelPayload("其他风格画像", "", ""), CancellationToken.None);
        var dialogueSourcePath = CreateSourceFile(
            "style-dialogue.md",
            """
            # 第一章

            她说：“门口别停。”雨声压住门口。
            """);
        var neutralSourcePath = CreateSourceFile(
            "style-neutral.md",
            """
            # 第一章

            他在门口停住。雨声落在门口。
            """);
        var otherSourcePath = CreateSourceFile(
            "style-other.md",
            """
            # 第一章

            她说：“门口还有人。”后来灯光暗下去。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var dialogueAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "对话风格", null, dialogueSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var neutralAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "叙述风格", null, neutralSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var otherAnchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(otherNovel.Id, "其他小说风格", null, otherSourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);
        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "对话证据画像",
                "",
                [dialogueAnchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);
        var otherProfile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                otherNovel.Id,
                "跨小说画像",
                "",
                [otherAnchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        var styled = await anchorService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [dialogueAnchor.AnchorId, neutralAnchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10,
                StyleProfileIds: [profile.ProfileId],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong),
            CancellationToken.None);

        Assert.True(styled.Total >= 2);
        var first = styled.Items[0];
        Assert.Equal(dialogueAnchor.AnchorId, first.AnchorId);
        Assert.NotNull(first.ScoreComponents);
        Assert.True(first.ScoreComponents["style_fit"] > 0);
        Assert.True(first.ScoreComponents["source_risk_penalty"] < 0);
        Assert.DoesNotContain(styled.Items.Where(item => item.AnchorId == neutralAnchor.AnchorId), item =>
            item.ScoreComponents?.ContainsKey("style_fit") == true);

        await Assert.ThrowsAsync<ArgumentException>(async () => await anchorService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [dialogueAnchor.AnchorId, neutralAnchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10,
                StyleProfileIds: [otherProfile.ProfileId],
                StyleDimensions: ["dialogue_ratio"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong),
            CancellationToken.None));
    }

    [Fact]
    public async Task BuildStyleProfileUsesInjectedLlmAnalyzerForGroundedAdvancedLabels()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("LLM 画像测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "style-llm-source.md",
            """
            # 第一章

            她说：“门口别停。”雨声压住门口。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "LLM 风格参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var analyzer = new RecordingReferenceStyleLlmAnalyzer(request =>
        {
            var window = request.Windows.First(window => window.MaterialId is not null);
            return $$"""
            {
              "schema_version": "{{ReferenceStyleLlmAnalysisSchemaVersions.V1}}",
              "labels": [
                {
                  "feature_key": "hook_pattern",
                  "label": "question_tail",
                  "confidence": 0.99,
                  "evidence": [
                    {
                      "source_segment_id": "{{window.SourceSegmentId}}",
                      "material_id": "{{window.MaterialId}}",
                      "start_offset": {{window.StartOffset}},
                      "end_offset": {{Math.Min(window.EndOffset, window.StartOffset + 8)}}
                    }
                  ]
                }
              ]
            }
            """;
        });
        var styleService = new SqliteReferenceStyleProfileService(options, novels, analyzer);

        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "LLM 增强画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.NotNull(analyzer.LastRequest);
        Assert.Equal(profile.ProfileId, analyzer.LastRequest.ProfileId);
        Assert.Equal(ReferenceStyleLlmAnalysisSchemaVersions.V1, analyzer.LastRequest.SchemaVersion);
        Assert.NotEmpty(analyzer.LastRequest.Windows);
        Assert.All(analyzer.LastRequest.Windows, window =>
        {
            Assert.True(window.AnchorId > 0);
            Assert.False(string.IsNullOrWhiteSpace(window.SourceSegmentId));
            Assert.True(window.EndOffset > window.StartOffset);
            Assert.False(string.IsNullOrWhiteSpace(window.TextHash));
            Assert.False(string.IsNullOrWhiteSpace(window.Text));
        });
        Assert.Equal(ReferenceStyleAnalyzerSources.LlmAssisted, profile.AnalyzerSource);
        Assert.Equal(ReferenceStyleAnalyzerVersions.LlmAssistedV1, profile.AnalyzerVersion);
        var llmEvidence = Assert.Single(profile.EvidenceSpans, evidence => evidence.FeatureKey == "hook_pattern");
        Assert.Equal(ReferenceStyleAnalyzerSources.LlmAssisted, llmEvidence.AnalyzerSource);
        Assert.Equal(0.95, llmEvidence.Confidence);
        Assert.Contains(profile.Features.CategoricalFeatures, feature =>
            feature.FeatureKey == "hook_pattern" &&
            feature.Label == "question_tail" &&
            feature.EvidenceIds.Contains(llmEvidence.EvidenceId, StringComparer.Ordinal));

        var styled = await anchorService.SearchMaterialsAsync(
            new SearchReferenceMaterialsPayload(
                novel.Id,
                [anchor.AnchorId],
                "门口",
                [ReferenceMaterialTypes.Sentence],
                [],
                [],
                [],
                [],
                Page: 1,
                Size: 10,
                StyleProfileIds: [profile.ProfileId],
                StyleDimensions: ["hook_pattern"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Moderate),
            CancellationToken.None);
        Assert.Contains(styled.Items, item =>
            item.ScoreComponents?.TryGetValue("style_fit", out var styleFit) == true &&
            styleFit > 0);

        var runs = await ReadAnalysisRunsAsync(options, profile.ProfileId);
        Assert.Contains(runs, run => run.AnalyzerSource == ReferenceStyleAnalyzerSources.DeterministicBaseline && run.Status == "completed");
        Assert.Contains(runs, run => run.AnalyzerSource == ReferenceStyleAnalyzerSources.LlmAssisted && run.Status == ReferenceStyleLlmAnalysisValidationStatuses.Passed);
    }

    [Fact]
    public async Task BuildStyleProfileFallsBackWhenInjectedLlmAnalyzerReturnsInvalidJson()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("LLM fallback 测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "style-llm-invalid-source.md",
            """
            # 第一章

            她说：“先别开门。”雨声已经压住了脚步。
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "LLM fallback 参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var analyzer = new RecordingReferenceStyleLlmAnalyzer(_ => "{not json");
        var styleService = new SqliteReferenceStyleProfileService(options, novels, analyzer);

        var profile = await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "LLM fallback 画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified]),
            CancellationToken.None);

        Assert.NotNull(analyzer.LastRequest);
        Assert.Equal(ReferenceStyleAnalyzerSources.DeterministicBaseline, profile.AnalyzerSource);
        Assert.Equal(ReferenceStyleAnalyzerVersions.DeterministicV1, profile.AnalyzerVersion);
        Assert.DoesNotContain(profile.EvidenceSpans, evidence => evidence.AnalyzerSource == ReferenceStyleAnalyzerSources.LlmAssisted);
        Assert.DoesNotContain(profile.Features.CategoricalFeatures, feature => feature.FeatureKey == "hook_pattern");

        var runs = await ReadAnalysisRunsAsync(options, profile.ProfileId);
        Assert.Contains(runs, run => run.AnalyzerSource == ReferenceStyleAnalyzerSources.DeterministicBaseline && run.Status == "completed");
        var llmRun = Assert.Single(runs, run => run.AnalyzerSource == ReferenceStyleAnalyzerSources.LlmAssisted);
        Assert.Equal(ReferenceStyleLlmAnalysisValidationStatuses.InvalidJson, llmRun.Status);
        Assert.Contains("valid JSON", llmRun.DiagnosticsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedStyleProfileBuildPersistsRecoverableBuildStatusWithoutSourceText()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像失败恢复测试", "", ""), CancellationToken.None);
        const string sourceSentinel = "雨声压低了整条街的呼吸";
        var sourcePath = CreateSourceFile(
            "style-failure-source.md",
            $$"""
            # 第一章

            {{sourceSentinel}}。她说：“先别开门。”
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "失败恢复参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var materialIds = await ReadMaterialIdsAsync(options, anchor.AnchorId);
        Assert.NotEmpty(materialIds);
        await anchorService.DeleteMaterialsAsync(
            new DeleteReferenceMaterialsPayload(novel.Id, materialIds),
            CancellationToken.None);
        var styleService = new SqliteReferenceStyleProfileService(options, novels);

        await Assert.ThrowsAsync<ArgumentException>(async () => await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "失败画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified],
                BuildId: "style-build-failed"),
            CancellationToken.None).AsTask());

        var status = await styleService.GetStyleProfileBuildStatusAsync(
            new GetReferenceStyleProfileBuildStatusPayload(novel.Id, "style-build-failed"),
            CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("style-build-failed", status.BuildId);
        Assert.Equal(ReferenceStyleProfileBuildStatuses.Failed, status.Status);
        Assert.Equal(ReferenceStyleProfileBuildStages.Failed, status.Stage);
        Assert.Null(status.ProfileId);
        Assert.Equal([anchor.AnchorId], status.AnchorIds);
        Assert.Equal([anchor.SourceFileHash], status.SourceHashes);
        Assert.Equal("ArgumentException", status.ErrorCode);
        Assert.DoesNotContain(sourceSentinel, status.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(sourcePath, status.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(status.Diagnostics, item => item.Contains(sourceSentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(status.Diagnostics, item => item.Contains(sourcePath, StringComparison.Ordinal));
        var buildColumns = await ReadTableColumnsAsync(options, "reference_style_profile_builds");
        Assert.DoesNotContain(buildColumns, column => column.Contains("text", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(buildColumns, column => column.Contains("path", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(buildColumns, column => column.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(buildColumns, column => column.Contains("content", StringComparison.OrdinalIgnoreCase));

        var reloadedStatus = await new SqliteReferenceStyleProfileService(options, novels).GetStyleProfileBuildStatusAsync(
            new GetReferenceStyleProfileBuildStatusPayload(novel.Id, "style-build-failed"),
            CancellationToken.None);
        Assert.NotNull(reloadedStatus);
        Assert.Equal(ReferenceStyleProfileBuildStatuses.Failed, reloadedStatus.Status);

        var profilesAfterFailure = await styleService.GetStyleProfilesAsync(
            new GetReferenceStyleProfilesPayload(novel.Id, IncludeArchived: true),
            CancellationToken.None);
        Assert.DoesNotContain(profilesAfterFailure, profile => profile.Title == "失败画像");

        await anchorService.RestoreMaterialsAsync(
            new RestoreReferenceMaterialsPayload(novel.Id, materialIds),
            CancellationToken.None);
        var recovered = await new SqliteReferenceStyleProfileService(options, novels).BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "恢复后画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified],
                BuildId: "style-build-recovered"),
            CancellationToken.None);
        Assert.True(recovered.ProfileId > 0);
        var recoveredStatus = await styleService.GetStyleProfileBuildStatusAsync(
            new GetReferenceStyleProfileBuildStatusPayload(novel.Id, "style-build-recovered"),
            CancellationToken.None);
        Assert.NotNull(recoveredStatus);
        Assert.Equal(ReferenceStyleProfileBuildStatuses.Completed, recoveredStatus.Status);
        Assert.Equal(recovered.ProfileId, recoveredStatus.ProfileId);
    }

    [Fact]
    public async Task CancelledStyleProfileBuildPersistsCancelledStatusWithoutActiveProfile()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("画像取消测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "style-cancel-source.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。她说：“先别开门。”
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "取消参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var analyzer = new CancellingReferenceStyleLlmAnalyzer();
        var styleService = new SqliteReferenceStyleProfileService(options, novels, analyzer);

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "取消画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified],
                BuildId: "style-build-cancelled"),
            CancellationToken.None).AsTask());

        var status = await styleService.GetStyleProfileBuildStatusAsync(
            new GetReferenceStyleProfileBuildStatusPayload(novel.Id, "style-build-cancelled"),
            CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(ReferenceStyleProfileBuildStatuses.Cancelled, status.Status);
        Assert.Equal(ReferenceStyleProfileBuildStages.Cancelled, status.Stage);
        Assert.Null(status.ProfileId);
        Assert.NotNull(status.CancelledAt);

        var profiles = await styleService.GetStyleProfilesAsync(
            new GetReferenceStyleProfilesPayload(novel.Id, IncludeArchived: true),
            CancellationToken.None);
        Assert.DoesNotContain(profiles, profile => profile.Title == "取消画像");
    }

    [Fact]
    public async Task CancelStyleProfileBuildSignalsActiveBuildAndPersistsCancelledStatus()
    {
        var options = CreateOptions();
        await InitializeAsync(options);
        var novels = new FileSystemNovelService(options, new FileSystemAppSettingsService(options));
        var novel = await novels.CreateNovelAsync(new CreateNovelPayload("主动取消画像测试", "", ""), CancellationToken.None);
        var sourcePath = CreateSourceFile(
            "style-explicit-cancel-source.md",
            """
            # 第一章

            雨声压低了整条街的呼吸。她说：“先别开门。”
            """);
        var anchorService = new SqliteReferenceAnchorService(options, novels);
        var anchor = await anchorService.CreateAnchorAsync(
            new CreateReferenceAnchorPayload(novel.Id, "主动取消参考", null, sourcePath, "markdown", "user_provided"),
            CancellationToken.None);
        var analyzer = new BlockingReferenceStyleLlmAnalyzer();
        var styleService = new SqliteReferenceStyleProfileService(options, novels, analyzer);
        var buildTask = styleService.BuildStyleProfileAsync(
            new BuildReferenceStyleProfilePayload(
                novel.Id,
                "主动取消画像",
                "",
                [anchor.AnchorId],
                ["user_provided"],
                [ReferenceSourceTrustLevels.UserVerified],
                BuildId: "style-build-explicit-cancel"),
            CancellationToken.None).AsTask();

        await analyzer.WaitUntilStartedAsync(TimeSpan.FromSeconds(10));
        var cancelStatus = await styleService.CancelStyleProfileBuildAsync(
            new CancelReferenceStyleProfileBuildPayload(novel.Id, "style-build-explicit-cancel"),
            CancellationToken.None);
        Assert.Equal(ReferenceStyleProfileBuildStatuses.Cancelled, cancelStatus.Status);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await buildTask);
        var persisted = await styleService.GetStyleProfileBuildStatusAsync(
            new GetReferenceStyleProfileBuildStatusPayload(novel.Id, "style-build-explicit-cancel"),
            CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(ReferenceStyleProfileBuildStatuses.Cancelled, persisted.Status);
        Assert.NotNull(persisted.CancelledAt);
        Assert.Null(persisted.ProfileId);

        var profiles = await styleService.GetStyleProfilesAsync(
            new GetReferenceStyleProfilesPayload(novel.Id, IncludeArchived: true),
            CancellationToken.None);
        Assert.DoesNotContain(profiles, profile => profile.Title == "主动取消画像");
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

    private static string BuildLargeChineseSource(int minUtf8Bytes)
    {
        var paragraph = string.Concat(
            Enumerable.Repeat("雨声压低了整条街的呼吸，门外忽然安静下来，他终于明白答案就在灯光背后", 650)) + "？";
        var builder = new StringBuilder("# 第一章 大雨\n\n");
        while (Encoding.UTF8.GetByteCount(builder.ToString()) < minUtf8Bytes)
        {
            builder.Append(paragraph);
            builder.Append("\n\n");
        }

        return builder.ToString();
    }

    private static double ReadNumericFeature(
        ReferenceStyleFeatureVectorPayload features,
        string featureKey)
    {
        return Assert.Single(features.NumericFeatures, feature => feature.FeatureKey == featureKey).Value;
    }

    private static async ValueTask<PersistedStyleProfile> ReadPersistedStyleProfileAsync(
        AppInitializationOptions options,
        long profileId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT feature_vector_json
            FROM reference_style_profiles
            WHERE profile_id = $profile_id;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var featureVectorJson = Assert.IsType<string>(await command.ExecuteScalarAsync());

        await using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(reference_style_profile_evidence);";
        var columns = new List<string>();
        await using var reader = await info.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return new PersistedStyleProfile(featureVectorJson, columns);
    }

    private static async ValueTask<IReadOnlyList<PersistedProfileSource>> ReadProfileSourcesAsync(
        AppInitializationOptions options,
        long profileId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT anchor_id, material_count, segment_count
            FROM reference_style_profile_sources
            WHERE profile_id = $profile_id
            ORDER BY anchor_id ASC;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var rows = new List<PersistedProfileSource>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new PersistedProfileSource(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<PersistedSampleProfileSource>> ReadSampleProfileSourcesAsync(
        AppInitializationOptions options,
        long profileId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sample_id, novel_id, is_global, source_hash, stats_schema_version, material_count, segment_count
            FROM reference_style_profile_sample_sources
            WHERE profile_id = $profile_id
            ORDER BY sample_id ASC;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var rows = new List<PersistedSampleProfileSource>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new PersistedSampleProfileSource(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetInt32(2) == 1,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6)));
        }

        return rows;
    }

    private static IReadOnlyList<string> FeatureSignature(ReferenceStyleFeatureVectorPayload features)
    {
        var numeric = features.NumericFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => $"n:{feature.FeatureKey}:{feature.Value}:{feature.Unit}:{feature.Confidence}");
        var distributions = features.DistributionFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => "d:" + feature.FeatureKey + ":" + string.Join(
                ";",
                feature.Buckets.Select(bucket => $"{bucket.Label}:{bucket.Min}:{bucket.Max}:{bucket.Weight}")));
        var categories = features.CategoricalFeatures
            .OrderBy(feature => feature.FeatureKey, StringComparer.Ordinal)
            .Select(feature => $"c:{feature.FeatureKey}:{feature.Label}:{feature.Weight}:{feature.Confidence}");
        return numeric.Concat(distributions).Concat(categories).ToArray();
    }

    private static async ValueTask<IReadOnlyList<string>> ReadSegmentSignaturesAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id, segment_type, segment_index, text_hash
            FROM reference_source_segments
            WHERE anchor_id = $anchor_id
            ORDER BY segment_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<string>> ReadMaterialSignaturesAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id, source_segment_id, material_type, source_hash, extractor_version
            FROM reference_materials
            WHERE anchor_id = $anchor_id
            ORDER BY material_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<string>> ReadMaterialIdsAsync(
        AppInitializationOptions options,
        long anchorId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT material_id
            FROM reference_materials
            WHERE anchor_id = $anchor_id
            ORDER BY material_id ASC;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<string>> ReadTableColumnsAsync(
        AppInitializationOptions options,
        string tableName)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + tableName + ");";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async ValueTask<bool> TableExistsAsync(AppInitializationOptions options, string tableName)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $table_name;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async ValueTask<string?> ReadMaterialArchivedAtAsync(
        AppInitializationOptions options,
        string materialId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT archived_at
            FROM reference_materials
            WHERE material_id = $material_id;
            """;
        command.Parameters.AddWithValue("$material_id", materialId);
        var archivedAt = await command.ExecuteScalarAsync();
        return archivedAt is null || archivedAt == DBNull.Value ? null : Assert.IsType<string>(archivedAt);
    }

    private static async ValueTask HardDeleteMaterialAsync(AppInitializationOptions options, string materialId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reference_materials WHERE material_id = $material_id;";
        command.Parameters.AddWithValue("$material_id", materialId);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<IReadOnlyList<PersistedAnalysisRun>> ReadAnalysisRunsAsync(
        AppInitializationOptions options,
        long profileId)
    {
        await using var connection = await OpenReferenceConnectionAsync(options);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT analyzer_source, status, diagnostics_json
            FROM reference_style_analysis_runs
            WHERE profile_id = $profile_id
            ORDER BY created_at ASC, run_id ASC;
            """;
        command.Parameters.AddWithValue("$profile_id", profileId);
        var rows = new List<PersistedAnalysisRun>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new PersistedAnalysisRun(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return rows;
    }

    private static async ValueTask<SqliteConnection> OpenReferenceConnectionAsync(AppInitializationOptions options)
    {
        var databasePath = Path.Combine(options.DefaultDataDirectory, "reference-anchor", "index.sqlite");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private sealed record PersistedStyleProfile(
        string FeatureVectorJson,
        IReadOnlyList<string> EvidenceColumns);

    private sealed record PersistedProfileSource(
        long AnchorId,
        int MaterialCount,
        int SegmentCount);

    private sealed record PersistedSampleProfileSource(
        long SampleId,
        long? NovelId,
        bool IsGlobal,
        string SourceHash,
        string StatsSchemaVersion,
        int MaterialCount,
        int SegmentCount);

    private sealed record PersistedAnalysisRun(
        string AnalyzerSource,
        string Status,
        string DiagnosticsJson);

    private sealed class RecordingReferenceStyleLlmAnalyzer : IReferenceStyleLlmAnalyzer
    {
        private readonly Func<ReferenceStyleLlmAnalysisRequestPayload, string?> _handler;

        public RecordingReferenceStyleLlmAnalyzer(Func<ReferenceStyleLlmAnalysisRequestPayload, string?> handler)
        {
            _handler = handler;
        }

        public ReferenceStyleLlmAnalysisRequestPayload? LastRequest { get; private set; }

        public ValueTask<string?> AnalyzeAsync(
            ReferenceStyleLlmAnalysisRequestPayload request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return ValueTask.FromResult(_handler(request));
        }
    }

    private sealed class CancellingReferenceStyleLlmAnalyzer : IReferenceStyleLlmAnalyzer
    {
        public ValueTask<string?> AnalyzeAsync(
            ReferenceStyleLlmAnalysisRequestPayload request,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException("cancelled by test");
        }
    }

    private sealed class BlockingReferenceStyleLlmAnalyzer : IReferenceStyleLlmAnalyzer
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<string?> AnalyzeAsync(
            ReferenceStyleLlmAnalysisRequestPayload request,
            CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public async ValueTask WaitUntilStartedAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(_started.Task, Task.Delay(timeout));
            if (!ReferenceEquals(completed, _started.Task))
            {
                throw new TimeoutException("Style analyzer did not start.");
            }
        }
    }
}
