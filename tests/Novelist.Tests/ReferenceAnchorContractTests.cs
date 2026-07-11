using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.Bridge;

namespace Novelist.Tests;

public sealed class ReferenceAnchorContractTests
{
    [Fact]
    public void ReferenceAnchorPayloadsUseStableSnakeCaseJsonNames()
    {
        var input = new CreateReferenceAnchorPayload(
            NovelId: 42,
            Title: "Anchor Book",
            Author: "Reference Author",
            SourcePath: @"D:\books\anchor.md",
            SourceKind: "markdown",
            LicenseStatus: "user_provided");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal("Anchor Book", root.GetProperty("title").GetString());
        Assert.Equal("Reference Author", root.GetProperty("author").GetString());
        Assert.Equal(@"D:\books\anchor.md", root.GetProperty("source_path").GetString());
        Assert.Equal("markdown", root.GetProperty("source_kind").GetString());
        Assert.Equal("user_provided", root.GetProperty("license_status").GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));

        var anchor = new ReferenceAnchorPayload(
            AnchorId: 7,
            NovelId: 0,
            Title: "Shared Anchor",
            Author: "",
            SourcePath: @"D:\books\shared.md",
            SourceKind: "markdown",
            LicenseStatus: "licensed",
            SourceFileHash: "hash",
            BuildVersion: "reference-anchor-v1",
            Status: ReferenceAnchorBuildStates.Ready,
            CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            Visibility: ReferenceCorpusVisibilities.Workspace,
            SourceTrust: ReferenceSourceTrustLevels.UserVerified,
            UserTags: ["rain", "threshold"]);

        using var anchorJson = JsonDocument.Parse(JsonSerializer.Serialize(anchor, BridgeJson.SerializerOptions));
        var anchorRoot = anchorJson.RootElement;
        Assert.Equal("workspace", anchorRoot.GetProperty("visibility").GetString());
        Assert.Equal("user_verified", anchorRoot.GetProperty("source_trust").GetString());
        Assert.Equal("rain", anchorRoot.GetProperty("user_tags")[0].GetString());
        Assert.Equal("workspace_corpus", anchorRoot.GetProperty("owner_scope").GetString());
        Assert.False(anchorRoot.TryGetProperty("owner_novel_id", out _));
        Assert.False(anchorRoot.TryGetProperty("SourceTrust", out _));

        var novelAnchor = anchor with
        {
            NovelId = 42,
            OwnerScope = ReferenceAnchorOwnerScopes.Novel,
            OwnerNovelId = 42
        };
        using var novelAnchorJson = JsonDocument.Parse(JsonSerializer.Serialize(novelAnchor, BridgeJson.SerializerOptions));
        var novelAnchorRoot = novelAnchorJson.RootElement;
        Assert.Equal(42, novelAnchorRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal("novel", novelAnchorRoot.GetProperty("owner_scope").GetString());
        Assert.Equal(42, novelAnchorRoot.GetProperty("owner_novel_id").GetInt64());
    }

    [Fact]
    public void PromoteReferenceAnchorToWorkspaceCorpusPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new PromoteReferenceAnchorToWorkspaceCorpusPayload(
            NovelId: 42,
            AnchorId: 7,
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["migrated", "shared"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, root.GetProperty("anchor_id").GetInt64());
        Assert.Equal("imported", root.GetProperty("source_trust").GetString());
        Assert.Equal("migrated", root.GetProperty("user_tags")[0].GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("AnchorId", out _));
    }

    [Fact]
    public void CreateReferenceAnchorsPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new CreateReferenceAnchorsPayload(
            [
                new CreateReferenceAnchorPayload(
                    NovelId: 42,
                    Title: "Bulk Anchor",
                    Author: "Reference Author",
                    SourcePath: @"D:\books\bulk.md",
                    SourceKind: "markdown",
                    LicenseStatus: "user_provided",
                    Visibility: ReferenceCorpusVisibilities.Workspace,
                    SourceTrust: ReferenceSourceTrustLevels.Imported,
                    UserTags: ["library", "bulk"])
            ]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;
        var anchor = root.GetProperty("anchors")[0];

        Assert.Equal(42, anchor.GetProperty("novel_id").GetInt64());
        Assert.Equal("Bulk Anchor", anchor.GetProperty("title").GetString());
        Assert.Equal(@"D:\books\bulk.md", anchor.GetProperty("source_path").GetString());
        Assert.Equal("workspace", anchor.GetProperty("visibility").GetString());
        Assert.Equal("imported", anchor.GetProperty("source_trust").GetString());
        Assert.Equal("library", anchor.GetProperty("user_tags")[0].GetString());
        Assert.False(root.TryGetProperty("Anchors", out _));
        Assert.False(anchor.TryGetProperty("NovelId", out _));
    }

    [Fact]
    public void CreateReferenceAnchorsResultPayloadUsesStableSnakeCaseWithoutSourcePaths()
    {
        var result = new CreateReferenceAnchorsResultPayload(
            Succeeded:
            [
                new ReferenceAnchorPayload(
                    AnchorId: 7,
                    NovelId: 42,
                    Title: "Bulk Anchor",
                    Author: "Reference Author",
                    SourcePath: string.Empty,
                    SourceKind: "markdown",
                    LicenseStatus: "user_provided",
                    SourceFileHash: "source-hash",
                    BuildVersion: "reference-anchor-v1",
                    Status: ReferenceAnchorBuildStates.Ready,
                    CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                    UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                    Visibility: ReferenceCorpusVisibilities.Private,
                    SourceTrust: ReferenceSourceTrustLevels.Imported,
                    UserTags: ["bulk"])
            ],
            Failed:
            [
                new CreateReferenceAnchorFailurePayload(
                    Index: 1,
                    Title: "Missing Anchor",
                    SourceKind: "markdown",
                    SourceIdentity: "source:6d0c04cf",
                    Diagnostic: "Reference source file does not exist.",
                    RetryAvailable: true)
            ],
            TotalCount: 2,
            SucceededCount: 1,
            FailedCount: 1);

        var serialized = JsonSerializer.Serialize(result, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal(2, root.GetProperty("total_count").GetInt32());
        Assert.Equal(1, root.GetProperty("succeeded_count").GetInt32());
        Assert.Equal(1, root.GetProperty("failed_count").GetInt32());
        Assert.Equal(7, root.GetProperty("succeeded")[0].GetProperty("anchor_id").GetInt64());
        var failure = root.GetProperty("failed")[0];
        Assert.Equal(1, failure.GetProperty("index").GetInt32());
        Assert.Equal("Missing Anchor", failure.GetProperty("title").GetString());
        Assert.Equal("markdown", failure.GetProperty("source_kind").GetString());
        Assert.Equal("source:6d0c04cf", failure.GetProperty("source_identity").GetString());
        Assert.True(failure.GetProperty("retry_available").GetBoolean());
        Assert.False(root.TryGetProperty("Succeeded", out _));
        Assert.False(failure.TryGetProperty("source_path", out _));
        Assert.False(failure.TryGetProperty("SourcePath", out _));
        Assert.DoesNotContain(@"D:\books", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PromoteReferenceAnchorsToWorkspaceCorpusPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new PromoteReferenceAnchorsToWorkspaceCorpusPayload(
            NovelId: 42,
            AnchorIds: [7, 8],
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["migrated", "shared"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal([7, 8], root.GetProperty("anchor_ids").EnumerateArray().Select(item => item.GetInt64()).ToArray());
        Assert.Equal("imported", root.GetProperty("source_trust").GetString());
        Assert.Equal("migrated", root.GetProperty("user_tags")[0].GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("AnchorIds", out _));
    }

    [Fact]
    public void DeleteReferenceAnchorsPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new DeleteReferenceAnchorsPayload(
            NovelId: 42,
            AnchorIds: [7, 8]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal([7, 8], root.GetProperty("anchor_ids").EnumerateArray().Select(item => item.GetInt64()).ToArray());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("AnchorIds", out _));
    }

    [Fact]
    public void DeleteReferenceMaterialsPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new DeleteReferenceMaterialsPayload(
            NovelId: 42,
            MaterialIds: ["material-1", "material-2"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(["material-1", "material-2"], root.GetProperty("material_ids").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("MaterialIds", out _));
    }

    [Fact]
    public void RestoreReferenceMaterialsPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new RestoreReferenceMaterialsPayload(
            NovelId: 42,
            MaterialIds: ["material-1", "material-2"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(["material-1", "material-2"], root.GetProperty("material_ids").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("MaterialIds", out _));
    }

    [Fact]
    public void UpdateReferenceAnchorMetadataPayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new UpdateReferenceAnchorMetadataPayload(
            NovelId: 42,
            AnchorId: 7,
            Title: "Updated Anchor",
            Author: "Reference Author",
            LicenseStatus: "user_provided",
            Visibility: ReferenceCorpusVisibilities.Workspace,
            SourceTrust: ReferenceSourceTrustLevels.Imported,
            UserTags: ["curated", "rain"]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, root.GetProperty("anchor_id").GetInt64());
        Assert.Equal("Updated Anchor", root.GetProperty("title").GetString());
        Assert.Equal("Reference Author", root.GetProperty("author").GetString());
        Assert.Equal("user_provided", root.GetProperty("license_status").GetString());
        Assert.Equal("workspace", root.GetProperty("visibility").GetString());
        Assert.Equal("imported", root.GetProperty("source_trust").GetString());
        Assert.Equal("curated", root.GetProperty("user_tags")[0].GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("AnchorId", out _));
        Assert.False(root.TryGetProperty("SourceTrust", out _));
    }

    [Fact]
    public void ReferenceMaterialPayloadSearchScoresUseStableSnakeCaseJsonNames()
    {
        var material = new ReferenceMaterialPayload(
            MaterialId: "material-1",
            AnchorId: 7,
            SourceSegmentId: "segment-1",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "environment",
            EmotionTag: "reflective",
            SceneTag: "scene",
            PovTag: "close",
            TechniqueTag: "sensory_detail",
            FunctionConfidence: 0.8,
            EmotionConfidence: 0.7,
            PovConfidence: 0.6,
            Text: "雨声压低了门口。",
            SourceHash: "hash",
            ExtractorVersion: "deterministic-v1",
            UserVerified: false,
            CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            ScoreComponents: new Dictionary<string, double>
            {
                ["lexical"] = 12.0,
                ["material_type"] = 1.5,
                ["confidence"] = 1.0
            });

        using var scoredJson = JsonDocument.Parse(JsonSerializer.Serialize(material, BridgeJson.SerializerOptions));
        var root = scoredJson.RootElement;
        Assert.Equal(12.0, root.GetProperty("score_components").GetProperty("lexical").GetDouble());
        Assert.Equal(1.5, root.GetProperty("score_components").GetProperty("material_type").GetDouble());
        Assert.False(root.TryGetProperty("ScoreComponents", out _));

        using var unscoredJson = JsonDocument.Parse(JsonSerializer.Serialize(material with { ScoreComponents = null }, BridgeJson.SerializerOptions));
        Assert.False(unscoredJson.RootElement.TryGetProperty("score_components", out _));
    }

    [Fact]
    public void ReferenceMaterialDetailPayloadUsesBoundedSnakeCaseFieldsWithoutSensitiveText()
    {
        var input = new GetReferenceMaterialDetailPayload(
            NovelId: 42,
            MaterialId: "material-1");

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var inputRoot = inputJson.RootElement;
        Assert.Equal(42, inputRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal("material-1", inputRoot.GetProperty("material_id").GetString());
        Assert.False(inputRoot.TryGetProperty("NovelId", out _));

        var detail = new ReferenceMaterialDetailPayload(
            Material: new ReferenceMaterialSummaryPayload(
                MaterialId: "material-1",
                AnchorId: 7,
                SourceSegmentId: "segment-1",
                MaterialType: ReferenceMaterialTypes.Sentence,
                FunctionTag: "environment",
                EmotionTag: "reflective",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "sensory_detail",
                FunctionConfidence: 0.8,
                EmotionConfidence: 0.7,
                PovConfidence: 0.6,
                TextPreview: "雨声压低了门口。",
                TextTruncated: true,
                SourceHash: "material-hash",
                ExtractorVersion: "deterministic-v1",
                UserVerified: false,
                CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                ArchiveState: ReferenceMaterialArchiveFilters.Archived,
                ArchivedAt: DateTimeOffset.Parse("2026-07-04T01:00:00Z"),
                ScoreComponents: new Dictionary<string, double>
                {
                    ["lexical"] = 0.7,
                    ["function"] = 0.5
                }),
            Source: new ReferenceMaterialSourceSummaryPayload(
                AnchorId: 7,
                NovelId: 42,
                Title: "Shared Anchor",
                Author: "Reference Author",
                SourceKind: "markdown",
                LicenseStatus: "user_provided",
                SourceFileHash: "source-hash",
                BuildVersion: "reference-anchor-v1",
                Status: ReferenceAnchorBuildStates.Ready,
                Visibility: ReferenceCorpusVisibilities.Workspace,
                SourceTrust: ReferenceSourceTrustLevels.UserVerified,
                UserTags: ["rain"],
                OwnerScope: ReferenceAnchorOwnerScopes.WorkspaceCorpus,
                OwnerNovelId: null),
            Segments:
            [
                new ReferenceMaterialSegmentPreviewPayload(
                    SegmentId: "segment-1",
                    SegmentType: "paragraph",
                    ChapterIndex: 1,
                    ChapterTitle: "雨夜",
                    SegmentIndex: 2,
                    TextPreview: "片段预览",
                    TextTruncated: false,
                    TextHash: "segment-hash")
            ],
            Slots:
            [
                new ReferenceMaterialSlotPreviewPayload(
                    SlotName: "object",
                    Placeholder: "门",
                    StartOffset: 1,
                    EndOffset: 2)
            ],
            ProcessingNotes:
            [
                new ReferenceMaterialProcessingNotePayload(
                    Stage: "completed",
                    Status: ReferenceAnchorBuildStates.Ready,
                    Message: "segments=1; materials=1; slots=1; vectors=0",
                    UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                    SourceSegmentCount: 1,
                    MaterialCount: 1,
                    SlotCount: 1,
                    VectorCount: 0,
                    AffectedSourceId: "7",
                    AffectedMaterialId: "material-1",
                    AffectedSegmentId: "segment-1",
                    AffectedSlotId: "object")
            ]);

        var serialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal("material-1", root.GetProperty("material").GetProperty("material_id").GetString());
        Assert.Equal("雨声压低了门口。", root.GetProperty("material").GetProperty("text_preview").GetString());
        Assert.True(root.GetProperty("material").GetProperty("text_truncated").GetBoolean());
        Assert.Equal("archived", root.GetProperty("material").GetProperty("archive_state").GetString());
        Assert.Equal(0.7, root.GetProperty("material").GetProperty("score_components").GetProperty("lexical").GetDouble());
        Assert.Equal("Shared Anchor", root.GetProperty("source").GetProperty("title").GetString());
        Assert.Equal("workspace_corpus", root.GetProperty("source").GetProperty("owner_scope").GetString());
        Assert.False(root.GetProperty("source").TryGetProperty("owner_novel_id", out _));
        Assert.Equal("segment-1", root.GetProperty("segments")[0].GetProperty("segment_id").GetString());
        Assert.Equal("object", root.GetProperty("slots")[0].GetProperty("slot_name").GetString());
        Assert.Equal("completed", root.GetProperty("processing_notes")[0].GetProperty("stage").GetString());
        Assert.Equal(1, root.GetProperty("processing_notes")[0].GetProperty("source_segment_count").GetInt32());
        Assert.Equal(1, root.GetProperty("processing_notes")[0].GetProperty("material_count").GetInt32());
        Assert.Equal(1, root.GetProperty("processing_notes")[0].GetProperty("slot_count").GetInt32());
        Assert.Equal("7", root.GetProperty("processing_notes")[0].GetProperty("affected_source_id").GetString());
        Assert.Equal("material-1", root.GetProperty("processing_notes")[0].GetProperty("affected_material_id").GetString());
        Assert.Equal("segment-1", root.GetProperty("processing_notes")[0].GetProperty("affected_segment_id").GetString());
        Assert.False(root.GetProperty("material").TryGetProperty("text", out _));
        Assert.False(root.GetProperty("source").TryGetProperty("source_path", out _));
        Assert.DoesNotContain("source_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\books", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferenceSourceSegmentDetailPayloadUsesBoundedSnakeCaseFieldsWithoutSensitiveText()
    {
        var input = new GetReferenceSourceSegmentDetailPayload(
            NovelId: 42,
            AnchorId: 7,
            SegmentId: "segment-1");

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var inputRoot = inputJson.RootElement;
        Assert.Equal(42, inputRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, inputRoot.GetProperty("anchor_id").GetInt64());
        Assert.Equal("segment-1", inputRoot.GetProperty("segment_id").GetString());
        Assert.False(inputRoot.TryGetProperty("NovelId", out _));

        var detail = new ReferenceSourceSegmentDetailPayload(
            Source: new ReferenceMaterialSourceSummaryPayload(
                AnchorId: 7,
                NovelId: 42,
                Title: "Shared Anchor",
                Author: "Reference Author",
                SourceKind: "markdown",
                LicenseStatus: "user_provided",
                SourceFileHash: "source-hash",
                BuildVersion: "reference-anchor-v1",
                Status: ReferenceAnchorBuildStates.FailedExtraction,
                Visibility: ReferenceCorpusVisibilities.Private,
                SourceTrust: ReferenceSourceTrustLevels.UserVerified,
                UserTags: ["rain"],
                OwnerScope: ReferenceAnchorOwnerScopes.Novel,
                OwnerNovelId: 42),
            Segment: new ReferenceSourceSegmentPreviewPayload(
                AnchorId: 7,
                SegmentId: "segment-1",
                SegmentType: "paragraph",
                ChapterIndex: 1,
                ChapterTitle: "雨夜",
                SegmentIndex: 2,
                ParentSegmentId: "chapter-1",
                StartOffset: 12,
                EndOffset: 80,
                TextPreview: "片段预览",
                TextTruncated: true,
                TextHash: "segment-hash"),
            ProcessingNotes:
            [
                new ReferenceMaterialProcessingNotePayload(
                    Stage: "extracting_materials",
                    Status: ReferenceAnchorBuildStates.FailedExtraction,
                    Message: "extract failed",
                    UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                    SourceSegmentCount: 2,
                    MaterialCount: 0,
                    SlotCount: 0,
                    VectorCount: 0,
                    AffectedSourceId: "7",
                    AffectedSegmentId: "segment-1")
            ]);

        var serialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal("Shared Anchor", root.GetProperty("source").GetProperty("title").GetString());
        var segment = root.GetProperty("segment");
        Assert.Equal(7, segment.GetProperty("anchor_id").GetInt64());
        Assert.Equal("segment-1", segment.GetProperty("segment_id").GetString());
        Assert.Equal("paragraph", segment.GetProperty("segment_type").GetString());
        Assert.Equal("chapter-1", segment.GetProperty("parent_segment_id").GetString());
        Assert.Equal(12, segment.GetProperty("start_offset").GetInt32());
        Assert.Equal(80, segment.GetProperty("end_offset").GetInt32());
        Assert.Equal("片段预览", segment.GetProperty("text_preview").GetString());
        Assert.True(segment.GetProperty("text_truncated").GetBoolean());
        Assert.Equal("segment-hash", segment.GetProperty("text_hash").GetString());
        Assert.Equal("segment-1", root.GetProperty("processing_notes")[0].GetProperty("affected_segment_id").GetString());
        Assert.False(root.GetProperty("source").TryGetProperty("source_path", out _));
        Assert.False(segment.TryGetProperty("text", out _));
        Assert.False(segment.TryGetProperty("source_text", out _));
        Assert.False(segment.TryGetProperty("chapter_text", out _));
        Assert.DoesNotContain("candidate_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\books", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferenceSourceProcessingDetailPayloadUsesStableSnakeCaseWithoutSensitiveText()
    {
        var input = new GetReferenceSourceProcessingDetailPayload(
            NovelId: 42,
            AnchorId: 7);
        var detail = new ReferenceSourceProcessingDetailPayload(
            Source: new ReferenceMaterialSourceSummaryPayload(
                AnchorId: 7,
                NovelId: 0,
                Title: "Shared Anchor",
                Author: "Reference Author",
                SourceKind: "markdown",
                LicenseStatus: "user_provided",
                SourceFileHash: "source-hash",
                BuildVersion: "reference-anchor-v1",
                Status: ReferenceAnchorBuildStates.Ready,
                Visibility: ReferenceCorpusVisibilities.Workspace,
                SourceTrust: ReferenceSourceTrustLevels.UserVerified,
                UserTags: ["rain"],
                OwnerScope: ReferenceAnchorOwnerScopes.WorkspaceCorpus,
                OwnerNovelId: null),
            CurrentStatus: new ReferenceSourceProcessingStatusPayload(
                Stage: "embedding",
                Status: ReferenceAnchorBuildStates.Ready,
                Diagnostic: "segments=3; materials=2; slots=1; vectors=2",
                UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                SourceSegmentCount: 3,
                MaterialCount: 2,
                SlotCount: 1,
                VectorCount: 2),
            Events:
            [
                new ReferenceSourceProcessingEventPayload(
                    EventId: "event-1",
                    Stage: "embedding",
                    Status: ReferenceAnchorBuildStates.Ready,
                    Message: "segments=3; materials=2; slots=1; vectors=2",
                    CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                    SourceSegmentCount: 3,
                    MaterialCount: 2,
                    SlotCount: 1,
                    VectorCount: 2,
                    AffectedSourceId: "7",
                    AffectedMaterialId: "material-1",
                    AffectedSegmentId: "segment-1",
                    AffectedSlotId: "slot-1")
            ],
            RetryAvailable: false,
            RebuildAvailable: true,
            AttemptCount: 2,
            CurrentAttempt: new ReferenceSourceProcessingAttemptPayload(
                AttemptId: "attempt-2",
                AttemptNumber: 2,
                BuildId: "build-2",
                BuildVersion: "reference-anchor-v1",
                Stage: "embedding",
                Status: ReferenceAnchorBuildStates.Ready,
                StartedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:01:00Z"),
                CompletedAt: DateTimeOffset.Parse("2026-07-04T00:01:00Z"),
                EventCount: 1,
                SourceSegmentCount: 3,
                MaterialCount: 2,
                SlotCount: 1,
                VectorCount: 2,
                RecoveredFromAttemptId: "attempt-1",
                RecoveredFromBuildId: "build-1",
                BlockedReason: ""),
            PriorAttempts:
            [
                new ReferenceSourceProcessingAttemptPayload(
                    AttemptId: "attempt-1",
                    AttemptNumber: 1,
                    BuildId: "build-1",
                    BuildVersion: "reference-anchor-v1",
                    Stage: "embedding",
                    Status: ReferenceAnchorBuildStates.FailedEmbedding,
                    StartedAt: DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
                    UpdatedAt: DateTimeOffset.Parse("2026-07-03T00:01:00Z"),
                    CompletedAt: DateTimeOffset.Parse("2026-07-03T00:01:00Z"),
                    EventCount: 1,
                    SourceSegmentCount: 3,
                    MaterialCount: 2,
                    SlotCount: 1,
                    VectorCount: 0,
                    RecoveredFromAttemptId: "",
                    RecoveredFromBuildId: "",
                    BlockedReason: "sqlite-vec native extension is unavailable")
            ],
            RecoveredFromAttemptId: "attempt-1",
            RecoveredFromBuildId: "build-1",
            BlockedReason: "");

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        Assert.Equal(42, inputJson.RootElement.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, inputJson.RootElement.GetProperty("anchor_id").GetInt64());

        var serialized = JsonSerializer.Serialize(detail, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;
        Assert.Equal("Shared Anchor", root.GetProperty("source").GetProperty("title").GetString());
        Assert.Equal("workspace_corpus", root.GetProperty("source").GetProperty("owner_scope").GetString());
        Assert.Equal("embedding", root.GetProperty("current_status").GetProperty("stage").GetString());
        Assert.Equal(3, root.GetProperty("current_status").GetProperty("source_segment_count").GetInt32());
        Assert.Equal("event-1", root.GetProperty("events")[0].GetProperty("event_id").GetString());
        Assert.Equal("material-1", root.GetProperty("events")[0].GetProperty("affected_material_id").GetString());
        Assert.Equal(2, root.GetProperty("attempt_count").GetInt32());
        Assert.Equal("attempt-2", root.GetProperty("current_attempt").GetProperty("attempt_id").GetString());
        Assert.Equal(2, root.GetProperty("current_attempt").GetProperty("attempt_number").GetInt32());
        Assert.Equal("build-2", root.GetProperty("current_attempt").GetProperty("build_id").GetString());
        Assert.Equal("attempt-1", root.GetProperty("current_attempt").GetProperty("recovered_from_attempt_id").GetString());
        Assert.Equal("attempt-1", root.GetProperty("recovered_from_attempt_id").GetString());
        Assert.Equal("sqlite-vec native extension is unavailable", root.GetProperty("prior_attempts")[0].GetProperty("blocked_reason").GetString());
        Assert.True(root.GetProperty("rebuild_available").GetBoolean());
        Assert.False(root.GetProperty("retry_available").GetBoolean());
        Assert.False(root.TryGetProperty("source_path", out _));
        Assert.DoesNotContain("source_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"D:\books", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferenceStyleProfilePayloadsUseStableSnakeCaseJsonNamesWithoutSourceText()
    {
        var build = new BuildReferenceStyleProfilePayload(
            NovelId: 42,
            Title: "雨夜克制风格",
            Description: "从工作区语料构建的确定性基线风格画像",
            AnchorIds: [7, 8],
            AllowedLicenseStatuses: ["user_provided", "licensed"],
            AllowedSourceTrustLevels: [ReferenceSourceTrustLevels.UserVerified, ReferenceSourceTrustLevels.Imported]);

        using var buildJson = JsonDocument.Parse(JsonSerializer.Serialize(build, BridgeJson.SerializerOptions));
        var buildRoot = buildJson.RootElement;
        Assert.Equal(42, buildRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal("雨夜克制风格", buildRoot.GetProperty("title").GetString());
        Assert.Equal("从工作区语料构建的确定性基线风格画像", buildRoot.GetProperty("description").GetString());
        Assert.Equal([7, 8], buildRoot.GetProperty("anchor_ids").EnumerateArray().Select(item => item.GetInt64()).ToArray());
        Assert.Equal("user_provided", buildRoot.GetProperty("allowed_license_statuses")[0].GetString());
        Assert.Equal("user_verified", buildRoot.GetProperty("allowed_source_trust_levels")[0].GetString());
        Assert.False(buildRoot.TryGetProperty("NovelId", out _));
        Assert.False(buildRoot.TryGetProperty("AnchorIds", out _));

        var evidence = new ReferenceStyleEvidenceSpanPayload(
            EvidenceId: "style-evidence-1",
            ProfileId: 100,
            AnchorId: 7,
            SourceSegmentId: "segment-1",
            MaterialId: "material-1",
            FeatureKey: "dialogue_ratio",
            Label: "dialogue_exchange",
            StartOffset: 12,
            EndOffset: 28,
            TextHash: "segment-hash",
            Confidence: 0.8,
            AnalyzerSource: ReferenceStyleAnalyzerSources.DeterministicBaseline);

        var featureVector = new ReferenceStyleFeatureVectorPayload(
            NumericFeatures:
            [
                new ReferenceStyleNumericFeaturePayload(
                    FeatureKey: "average_sentence_chars",
                    Value: 18.5,
                    Unit: "chars",
                    Confidence: 0.75,
                    EvidenceIds: ["style-evidence-1"])
            ],
            DistributionFeatures:
            [
                new ReferenceStyleDistributionFeaturePayload(
                    FeatureKey: "sentence_length_distribution",
                    Unit: "chars",
                    Buckets:
                    [
                        new ReferenceStyleDistributionBucketPayload("short", 0, 20, 0.6),
                        new ReferenceStyleDistributionBucketPayload("medium", 21, 60, 0.4)
                    ],
                    Confidence: 0.75,
                    EvidenceIds: ["style-evidence-1"])
            ],
            CategoricalFeatures:
            [
                new ReferenceStyleCategoricalFeaturePayload(
                    FeatureKey: "dominant_technique",
                    Label: "dialogue_exchange",
                    Weight: 0.4,
                    Confidence: 0.7,
                    EvidenceIds: ["style-evidence-1"])
            ]);

        var profile = new ReferenceStyleProfilePayload(
            ProfileId: 100,
            NovelId: 42,
            Title: "雨夜克制风格",
            Description: "从工作区语料构建的确定性基线风格画像",
            Status: ReferenceStyleProfileStatuses.Active,
            AnalyzerVersion: "reference-style-deterministic-v1",
            FeatureSchemaVersion: ReferenceStyleFeatureSchemaVersions.V1,
            AnalyzerSource: ReferenceStyleAnalyzerSources.DeterministicBaseline,
            SourceAnchorIds: [7],
            SourceHashes: ["source-hash"],
            AllowedLicenseStatuses: ["user_provided"],
            AllowedSourceTrustLevels: [ReferenceSourceTrustLevels.UserVerified],
            AggregateConfidence: 0.72,
            Features: featureVector,
            EvidenceSpans: [evidence],
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            ArchivedAt: null);

        using var profileJson = JsonDocument.Parse(JsonSerializer.Serialize(profile, BridgeJson.SerializerOptions));
        var root = profileJson.RootElement;
        Assert.Equal(100, root.GetProperty("profile_id").GetInt64());
        Assert.Equal("active", root.GetProperty("status").GetString());
        Assert.Equal("reference-style-deterministic-v1", root.GetProperty("analyzer_version").GetString());
        Assert.Equal("style-profile-v1", root.GetProperty("feature_schema_version").GetString());
        Assert.Equal("deterministic_baseline", root.GetProperty("analyzer_source").GetString());
        Assert.Equal(7, root.GetProperty("source_anchor_ids")[0].GetInt64());
        Assert.Equal("source-hash", root.GetProperty("source_hashes")[0].GetString());
        Assert.Equal(0.72, root.GetProperty("aggregate_confidence").GetDouble());
        Assert.Equal("average_sentence_chars", root.GetProperty("features").GetProperty("numeric_features")[0].GetProperty("feature_key").GetString());
        Assert.Equal("style-evidence-1", root.GetProperty("evidence_spans")[0].GetProperty("evidence_id").GetString());
        Assert.False(root.GetProperty("evidence_spans")[0].TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("ProfileId", out _));
        Assert.False(root.TryGetProperty("SourceText", out _));
    }

    [Fact]
    public void ReferenceStyleProfileLibraryPayloadsUseStableSnakeCaseJsonNamesWithoutSourceText()
    {
        var archive = new ArchiveReferenceStyleProfilePayload(
            NovelId: 42,
            ProfileId: 100);
        var restore = new RestoreReferenceStyleProfilePayload(
            NovelId: 42,
            ProfileId: 100);
        var compareInput = new CompareReferenceStyleProfilesPayload(
            NovelId: 42,
            LeftProfileId: 100,
            RightProfileId: 101);

        using var archiveJson = JsonDocument.Parse(JsonSerializer.Serialize(archive, BridgeJson.SerializerOptions));
        Assert.Equal(42, archiveJson.RootElement.GetProperty("novel_id").GetInt64());
        Assert.Equal(100, archiveJson.RootElement.GetProperty("profile_id").GetInt64());
        Assert.False(archiveJson.RootElement.TryGetProperty("NovelId", out _));
        Assert.False(archiveJson.RootElement.TryGetProperty("ProfileId", out _));

        using var restoreJson = JsonDocument.Parse(JsonSerializer.Serialize(restore, BridgeJson.SerializerOptions));
        Assert.Equal(42, restoreJson.RootElement.GetProperty("novel_id").GetInt64());
        Assert.Equal(100, restoreJson.RootElement.GetProperty("profile_id").GetInt64());
        Assert.False(restoreJson.RootElement.TryGetProperty("NovelId", out _));

        using var compareInputJson = JsonDocument.Parse(JsonSerializer.Serialize(compareInput, BridgeJson.SerializerOptions));
        Assert.Equal(42, compareInputJson.RootElement.GetProperty("novel_id").GetInt64());
        Assert.Equal(100, compareInputJson.RootElement.GetProperty("left_profile_id").GetInt64());
        Assert.Equal(101, compareInputJson.RootElement.GetProperty("right_profile_id").GetInt64());
        Assert.False(compareInputJson.RootElement.TryGetProperty("LeftProfileId", out _));

        var leftSummary = new ReferenceStyleProfileSummaryPayload(
            ProfileId: 100,
            NovelId: 42,
            Title: "短句风格",
            Description: "",
            Status: ReferenceStyleProfileStatuses.Active,
            AnalyzerVersion: ReferenceStyleAnalyzerVersions.DeterministicV1,
            FeatureSchemaVersion: ReferenceStyleFeatureSchemaVersions.V1,
            AnalyzerSource: ReferenceStyleAnalyzerSources.DeterministicBaseline,
            SourceAnchorIds: [7],
            SourceHashes: ["left-source-hash"],
            AggregateConfidence: 0.8,
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            ArchivedAt: null);
        var rightSummary = leftSummary with
        {
            ProfileId = 101,
            Title = "对话风格",
            SourceAnchorIds = [8],
            SourceHashes = ["right-source-hash"]
        };
        var comparison = new ReferenceStyleProfileComparisonPayload(
            NovelId: 42,
            LeftProfile: leftSummary,
            RightProfile: rightSummary,
            NumericDifferences:
            [
                new ReferenceStyleNumericFeatureDifferencePayload(
                    FeatureKey: "dialogue_ratio",
                    Unit: "ratio",
                    LeftValue: 0.1,
                    RightValue: 0.4,
                    AbsoluteDelta: 0.3,
                    RelativeDelta: 3.0,
                    LeftConfidence: 0.8,
                    RightConfidence: 0.9)
            ],
            DistributionDifferences:
            [
                new ReferenceStyleDistributionFeatureDifferencePayload(
                    FeatureKey: "sentence_length_distribution",
                    Unit: "chars",
                    Buckets:
                    [
                        new ReferenceStyleDistributionBucketDifferencePayload(
                            Label: "short",
                            LeftMin: 0,
                            LeftMax: 20,
                            LeftWeight: 0.6,
                            RightMin: 0,
                            RightMax: 20,
                            RightWeight: 0.3,
                            AbsoluteDelta: 0.3)
                    ],
                    LeftConfidence: 0.8,
                    RightConfidence: 0.9)
            ],
            CategoricalDifferences:
            [
                new ReferenceStyleCategoricalFeatureDifferencePayload(
                    FeatureKey: "hook_pattern",
                    Label: "question_tail",
                    LeftWeight: 0.7,
                    RightWeight: null,
                    AbsoluteDelta: 0.7,
                    LeftConfidence: 0.8,
                    RightConfidence: null)
            ],
            ComparedAt: DateTimeOffset.Parse("2026-07-07T00:05:00Z"));

        var serialized = JsonSerializer.Serialize(comparison, BridgeJson.SerializerOptions);
        using var comparisonJson = JsonDocument.Parse(serialized);
        var root = comparisonJson.RootElement;
        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(100, root.GetProperty("left_profile").GetProperty("profile_id").GetInt64());
        Assert.Equal(101, root.GetProperty("right_profile").GetProperty("profile_id").GetInt64());
        Assert.Equal("dialogue_ratio", root.GetProperty("numeric_differences")[0].GetProperty("feature_key").GetString());
        Assert.Equal(0.3, root.GetProperty("numeric_differences")[0].GetProperty("absolute_delta").GetDouble());
        Assert.Equal("short", root.GetProperty("distribution_differences")[0].GetProperty("buckets")[0].GetProperty("label").GetString());
        Assert.Equal("question_tail", root.GetProperty("categorical_differences")[0].GetProperty("label").GetString());
        Assert.False(root.TryGetProperty("LeftProfile", out _));
        Assert.DoesNotContain("source_text", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidence_spans", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferenceStyleProfileBuildStatusPayloadsUseStableSnakeCaseJsonNamesWithoutTextFields()
    {
        var build = new BuildReferenceStyleProfilePayload(
            NovelId: 42,
            Title: "雨夜克制风格",
            Description: "bounded metadata only",
            AnchorIds: [7, 8],
            AllowedLicenseStatuses: ["user_provided"],
            AllowedSourceTrustLevels: [ReferenceSourceTrustLevels.UserVerified],
            BuildId: "style-build-test");
        var get = new GetReferenceStyleProfileBuildStatusPayload(
            NovelId: 42,
            BuildId: "style-build-test");
        var cancel = new CancelReferenceStyleProfileBuildPayload(
            NovelId: 42,
            BuildId: "style-build-test");
        var status = new ReferenceStyleProfileBuildStatusPayload(
            BuildId: "style-build-test",
            NovelId: 42,
            ProfileId: null,
            Title: "雨夜克制风格",
            Status: ReferenceStyleProfileBuildStatuses.Failed,
            Stage: ReferenceStyleProfileBuildStages.Failed,
            ProgressCompleted: 2,
            ProgressTotal: 6,
            AnchorIds: [7, 8],
            SourceHashes: ["source-hash"],
            Diagnostics: ["validation_error"],
            ErrorCode: "validation_error",
            ErrorMessage: "Style profile build failed before completion.",
            CreatedAt: DateTimeOffset.Parse("2026-07-07T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-07T00:01:00Z"),
            CompletedAt: null,
            CancelledAt: null);

        using var buildJson = JsonDocument.Parse(JsonSerializer.Serialize(build, BridgeJson.SerializerOptions));
        var buildRoot = buildJson.RootElement;
        Assert.Equal("style-build-test", buildRoot.GetProperty("build_id").GetString());
        Assert.False(buildRoot.TryGetProperty("BuildId", out _));

        using var getJson = JsonDocument.Parse(JsonSerializer.Serialize(get, BridgeJson.SerializerOptions));
        Assert.Equal(42, getJson.RootElement.GetProperty("novel_id").GetInt64());
        Assert.Equal("style-build-test", getJson.RootElement.GetProperty("build_id").GetString());

        using var cancelJson = JsonDocument.Parse(JsonSerializer.Serialize(cancel, BridgeJson.SerializerOptions));
        Assert.Equal(42, cancelJson.RootElement.GetProperty("novel_id").GetInt64());
        Assert.Equal("style-build-test", cancelJson.RootElement.GetProperty("build_id").GetString());

        var serializedStatus = JsonSerializer.Serialize(status, BridgeJson.SerializerOptions);
        using var statusJson = JsonDocument.Parse(serializedStatus);
        var root = statusJson.RootElement;
        Assert.Equal("style-build-test", root.GetProperty("build_id").GetString());
        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("failed", root.GetProperty("stage").GetString());
        Assert.Equal(2, root.GetProperty("progress_completed").GetInt32());
        Assert.Equal(6, root.GetProperty("progress_total").GetInt32());
        Assert.Equal(7, root.GetProperty("anchor_ids")[0].GetInt64());
        Assert.Equal("source-hash", root.GetProperty("source_hashes")[0].GetString());
        Assert.Equal("validation_error", root.GetProperty("diagnostics")[0].GetString());
        Assert.Equal("validation_error", root.GetProperty("error_code").GetString());
        Assert.Equal("Style profile build failed before completion.", root.GetProperty("error_message").GetString());
        Assert.False(root.TryGetProperty("profile_id", out _));
        Assert.False(root.TryGetProperty("source_text", out _));
        Assert.False(root.TryGetProperty("candidate_text", out _));
        Assert.False(root.TryGetProperty("prompt", out _));
        Assert.False(root.TryGetProperty("path", out _));
        Assert.False(root.TryGetProperty("content", out _));
        Assert.DoesNotContain("source_text", serializedStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidate_text", serializedStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serializedStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", serializedStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReferenceStyleLlmAnalysisPayloadsUseStableSnakeCaseJsonNamesWithoutPaths()
    {
        var window = new ReferenceStyleAnalysisWindowPayload(
            WindowId: "win-1",
            AnchorId: 7,
            SourceSegmentId: "seg-1",
            MaterialId: "mat-1",
            StartOffset: 10,
            EndOffset: 42,
            TextHash: "window-hash",
            Text: "bounded excerpt only");
        var request = new ReferenceStyleLlmAnalysisRequestPayload(
            ProfileId: 99,
            SchemaVersion: ReferenceStyleLlmAnalysisSchemaVersions.V1,
            RequestedFeatureKeys: ["hook_pattern"],
            Windows: [window]);

        var rejected = new ReferenceStyleLlmAnalysisRejectedLabelPayload(
            FeatureKey: "unsupported_magic",
            Label: "too_vague",
            Reason: "Unsupported style feature key.");

        var result = new ReferenceStyleLlmAnalysisValidationResultPayload(
            Status: ReferenceStyleLlmAnalysisValidationStatuses.Partial,
            EvidenceSpans:
            [
                new ReferenceStyleEvidenceSpanPayload(
                    EvidenceId: "llm-style-1",
                    ProfileId: 99,
                    AnchorId: 7,
                    SourceSegmentId: "seg-1",
                    MaterialId: "mat-1",
                    FeatureKey: "hook_pattern",
                    Label: "question_tail",
                    StartOffset: 10,
                    EndOffset: 20,
                    TextHash: "window-hash",
                    Confidence: 0.95,
                    AnalyzerSource: ReferenceStyleAnalyzerSources.LlmAssisted)
            ],
            RejectedLabels: [rejected],
            Diagnostics: ["confidence downgraded"]);

        using var requestJson = JsonDocument.Parse(JsonSerializer.Serialize(request, BridgeJson.SerializerOptions));
        var requestRoot = requestJson.RootElement;
        Assert.Equal(99, requestRoot.GetProperty("profile_id").GetInt64());
        Assert.Equal("reference-style-llm-analysis-v1", requestRoot.GetProperty("schema_version").GetString());
        Assert.Equal("hook_pattern", requestRoot.GetProperty("requested_feature_keys")[0].GetString());
        Assert.Equal("win-1", requestRoot.GetProperty("windows")[0].GetProperty("window_id").GetString());
        Assert.False(requestRoot.TryGetProperty("path", out _));
        Assert.False(requestRoot.TryGetProperty("source_path", out _));
        Assert.False(requestRoot.TryGetProperty("ProfileId", out _));

        using var windowJson = JsonDocument.Parse(JsonSerializer.Serialize(window, BridgeJson.SerializerOptions));
        var windowRoot = windowJson.RootElement;
        Assert.Equal("win-1", windowRoot.GetProperty("window_id").GetString());
        Assert.Equal(7, windowRoot.GetProperty("anchor_id").GetInt64());
        Assert.Equal("seg-1", windowRoot.GetProperty("source_segment_id").GetString());
        Assert.Equal("mat-1", windowRoot.GetProperty("material_id").GetString());
        Assert.Equal(10, windowRoot.GetProperty("start_offset").GetInt32());
        Assert.Equal("window-hash", windowRoot.GetProperty("text_hash").GetString());
        Assert.Equal("bounded excerpt only", windowRoot.GetProperty("text").GetString());
        Assert.False(windowRoot.TryGetProperty("path", out _));
        Assert.False(windowRoot.TryGetProperty("source_path", out _));
        Assert.False(windowRoot.TryGetProperty("WindowId", out _));

        using var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeJson.SerializerOptions));
        var resultRoot = resultJson.RootElement;
        Assert.Equal("partial", resultRoot.GetProperty("status").GetString());
        Assert.Equal("llm-style-1", resultRoot.GetProperty("evidence_spans")[0].GetProperty("evidence_id").GetString());
        Assert.Equal("llm_assisted", resultRoot.GetProperty("evidence_spans")[0].GetProperty("analyzer_source").GetString());
        Assert.Equal("unsupported_magic", resultRoot.GetProperty("rejected_labels")[0].GetProperty("feature_key").GetString());
        Assert.Equal("confidence downgraded", resultRoot.GetProperty("diagnostics")[0].GetString());
        Assert.False(resultRoot.GetProperty("evidence_spans")[0].TryGetProperty("text", out _));
        Assert.False(resultRoot.TryGetProperty("RejectedLabels", out _));
    }

    [Fact]
    public void SearchReferenceMaterialsPayloadUsesStableNarrativeFilterJsonNames()
    {
        var payload = new SearchReferenceMaterialsPayload(
            NovelId: 42,
            AnchorIds: [7],
            Query: "rain pressure",
            MaterialTypes: [ReferenceMaterialTypes.Sentence],
            EmotionTags: ["heightened"],
            FunctionTags: ["environment"],
            PovTags: ["unknown"],
            TechniqueTags: ["sensory_detail"],
            Page: 1,
            Size: 10,
            NarrativeDuties: ["external_evidence"],
            EmotionTransitions: ["neutral->heightened"],
            ProseDuties: ["source_backed_detail"],
            ArchiveFilter: ReferenceMaterialArchiveFilters.Archived,
            StyleProfileIds: [99],
            StyleDimensions: ["dialogue_ratio"],
            ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
            SceneTags: ["threshold"],
            ReadyOnly: true);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal("external_evidence", root.GetProperty("narrative_duties")[0].GetString());
        Assert.Equal("neutral->heightened", root.GetProperty("emotion_transitions")[0].GetString());
        Assert.Equal("source_backed_detail", root.GetProperty("prose_duties")[0].GetString());
        Assert.Equal("archived", root.GetProperty("archive_filter").GetString());
        Assert.Equal(99, root.GetProperty("style_profile_ids")[0].GetInt64());
        Assert.Equal("dialogue_ratio", root.GetProperty("style_dimensions")[0].GetString());
        Assert.Equal("strong", root.GetProperty("imitation_intensity").GetString());
        Assert.Equal("threshold", root.GetProperty("scene_tags")[0].GetString());
        Assert.True(root.GetProperty("ready_only").GetBoolean());
        Assert.False(root.TryGetProperty("NarrativeDuties", out _));
        Assert.False(root.TryGetProperty("EmotionTransitions", out _));
        Assert.False(root.TryGetProperty("ProseDuties", out _));
        Assert.False(root.TryGetProperty("ArchiveFilter", out _));
        Assert.False(root.TryGetProperty("StyleProfileIds", out _));
        Assert.False(root.TryGetProperty("StyleDimensions", out _));
        Assert.False(root.TryGetProperty("ImitationIntensity", out _));
        Assert.False(root.TryGetProperty("SceneTags", out _));
        Assert.False(root.TryGetProperty("ReadyOnly", out _));
    }

    [Fact]
    public void MaterialCoveragePayloadUsesBoundedFacetCountsWithoutSourceText()
    {
        var input = new GetReferenceMaterialCoveragePayload(
            NovelId: 42,
            ArchiveFilter: ReferenceMaterialArchiveFilters.Active);
        var result = new ReferenceMaterialCoveragePayload(
            MaterialCount: 128,
            SourceCount: 2,
            Facets:
            [
                new ReferenceMaterialFacetPayload(
                    Key: "technique_tag",
                    DistinctValueCount: 2,
                    Values:
                    [
                        new ReferenceMaterialFacetValuePayload("interiority", 96),
                        new ReferenceMaterialFacetValuePayload("afterbeat", 32)
                    ])
            ]);

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        using var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeJson.SerializerOptions));

        Assert.Equal(42, inputJson.RootElement.GetProperty("novel_id").GetInt64());
        Assert.Equal("active", inputJson.RootElement.GetProperty("archive_filter").GetString());
        Assert.Equal(128, resultJson.RootElement.GetProperty("material_count").GetInt64());
        Assert.Equal("technique_tag", resultJson.RootElement.GetProperty("facets")[0].GetProperty("key").GetString());
        Assert.Equal("interiority", resultJson.RootElement.GetProperty("facets")[0].GetProperty("values")[0].GetProperty("value").GetString());
        Assert.False(resultJson.RootElement.GetRawText().Contains("text", StringComparison.OrdinalIgnoreCase));
        Assert.False(resultJson.RootElement.GetRawText().Contains("source_path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MaterialTagReviewQueuePayloadUsesStableJsonNamesWithoutFullTextFields()
    {
        var input = new GetReferenceMaterialTagReviewQueuePayload(
            NovelId: 42,
            AnchorIds: [7, 8],
            Page: 2,
            Size: 25,
            ArchiveFilter: ReferenceMaterialArchiveFilters.Active);
        var item = new ReferenceMaterialTagReviewItemPayload(
            new ReferenceMaterialSummaryPayload(
                MaterialId: "mat-1",
                AnchorId: 7,
                SourceSegmentId: "seg-1",
                MaterialType: ReferenceMaterialTypes.Sentence,
                FunctionTag: "environment",
                EmotionTag: "unknown",
                SceneTag: "threshold",
                PovTag: "close",
                TechniqueTag: "afterbeat",
                FunctionConfidence: 0.7,
                EmotionConfidence: 0.8,
                PovConfidence: 0.6,
                TextPreview: "bounded preview",
                TextTruncated: true,
                SourceHash: "hash",
                ExtractorVersion: "extractor",
                UserVerified: false,
                CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z")),
            [
                new ReferenceMaterialTagReviewIssuePayload(
                    ReferenceMaterialTagReviewIssueCodes.LowConfidence,
                    "低置信 功能 0.70 / POV 0.60",
                    "warning")
            ]);

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var inputRoot = inputJson.RootElement;
        Assert.Equal(42, inputRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, inputRoot.GetProperty("anchor_ids")[0].GetInt64());
        Assert.Equal(2, inputRoot.GetProperty("page").GetInt32());
        Assert.Equal(25, inputRoot.GetProperty("size").GetInt32());
        Assert.Equal("active", inputRoot.GetProperty("archive_filter").GetString());
        Assert.False(inputRoot.TryGetProperty("NovelId", out _));
        Assert.False(inputRoot.TryGetProperty("AnchorIds", out _));

        using var itemJson = JsonDocument.Parse(JsonSerializer.Serialize(item, BridgeJson.SerializerOptions));
        var itemRoot = itemJson.RootElement;
        Assert.Equal("mat-1", itemRoot.GetProperty("material").GetProperty("material_id").GetString());
        Assert.Equal("bounded preview", itemRoot.GetProperty("material").GetProperty("text_preview").GetString());
        Assert.Equal(ReferenceMaterialTagReviewIssueCodes.LowConfidence, itemRoot.GetProperty("issues")[0].GetProperty("code").GetString());
        Assert.False(itemRoot.GetProperty("material").TryGetProperty("text", out _));
        Assert.False(itemRoot.TryGetProperty("source_text", out _));
        Assert.False(itemRoot.TryGetProperty("prompt", out _));
        Assert.False(itemRoot.TryGetProperty("candidate_text", out _));
    }

    [Fact]
    public void BindReferenceBlueprintMaterialsPayloadUsesStableSelectionJsonName()
    {
        var payload = new BindReferenceBlueprintMaterialsPayload(
            NovelId: 42,
            BlueprintId: 10,
            MaxResultsPerBeat: 3,
            SelectTopCandidate: true);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal(3, root.GetProperty("max_results_per_beat").GetInt32());
        Assert.True(root.GetProperty("select_top_candidate").GetBoolean());
        Assert.False(root.TryGetProperty("SelectTopCandidate", out _));

        var defaulted = JsonSerializer.Deserialize<BindReferenceBlueprintMaterialsPayload>(
            """{"novel_id":42,"blueprint_id":10,"max_results_per_beat":3}""",
            BridgeJson.SerializerOptions);
        Assert.NotNull(defaulted);
        Assert.False(defaulted.SelectTopCandidate);
    }

    [Fact]
    public void ReferenceBlueprintMaterialLinkPayloadUsesStableFitExplanationJsonName()
    {
        var link = new ReferenceBlueprintMaterialLinkPayload(
            LinkId: "link-1",
            BlueprintId: 10,
            BeatId: "beat-1",
            MaterialId: "material-1",
            IntendedUse: "show restrained pressure",
            MaxRewriteLevel: ReferenceRewriteLevels.L1,
            Selected: true,
            Score: 9.5,
            ScoreComponents: new Dictionary<string, double>
            {
                ["function"] = 3.0,
                ["emotion"] = 2.0
            },
            FitExplanation: "function and emotion match the beat reference query.",
            CreatedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(link, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal("function and emotion match the beat reference query.", root.GetProperty("fit_explanation").GetString());
        Assert.False(root.TryGetProperty("FitExplanation", out _));
    }

    [Fact]
    public void ReferenceChapterBlueprintPayloadsUseStableSnakeCaseJsonNames()
    {
        var beat = new ReferenceChapterBlueprintBeatPayload(
            BeatId: "beat-1",
            BeatIndex: 1,
            SceneIndex: 1,
            BeatType: ReferenceBlueprintBeatTypes.Interiority,
            NarrativeFunction: "reveal pressure through restrained interiority",
            LogicPremise: "the clue contradicts the previous chapter state",
            ConflictPressure: "the protagonist must decide whether to confront the witness",
            CausalityIn: "previous clue forces the protagonist to reconsider",
            CausalityOut: "the reconsideration motivates the next confrontation",
            TransitionIn: "camera stays with the protagonist after the clue is found",
            TransitionOut: "private unease pushes the next scene",
            PovCharacter: "protagonist",
            NarrativeDistance: "close",
            ViewpointAllowedKnowledge: ["known clue"],
            ViewpointForbiddenKnowledge: ["culprit identity"],
            CharacterStatesBefore: ["guarded"],
            CharacterStatesAfter: ["uneasy"],
            CharacterGoals: ["protect the clue"],
            CharacterMisbeliefs: ["the witness is still available"],
            RelationshipPressure: ["trust in the witness weakens"],
            EmotionTrigger: "a detail contradicts the protagonist's assumption",
            EmotionBefore: "controlled suspicion",
            EmotionAfter: "private unease",
            SuppressedReaction: "does not voice the fear",
            ExternalEvidence: "hand pauses before opening the door",
            NarrationStrategy: "brief close interiority followed by physical afterbeat",
            RhythmStrategy: "short sentence after a longer reflective sentence",
            ParagraphIntention: "linger on hesitation before the next action",
            ExecutionMode: "dwell",
            AntiScreenplayDuty: "carry the beat through interiority and physical afterbeat, not dialogue blocking",
            SensoryAnchorTarget: "the locked door under the protagonist's hand",
            SubtextPlan: "the pause implies fear without naming it directly",
            SourceBackedDetailTarget: "physical hesitation detail",
            CandidateRejectionRule: "reject dialogue-only or action-only prose",
            SceneFacts: ["door is locked"],
            ForbiddenFacts: ["culprit identity"],
            ReferenceQuery: new ReferenceMaterialQueryPayload(
                Query: "close interiority hesitation",
                MaterialTypes: [ReferenceMaterialTypes.Passage],
                EmotionTags: ["unease"],
                FunctionTags: ["interiority"],
                PovTags: ["close"],
                TechniqueTags: ["afterbeat"],
                MaxResults: 5),
            RequiredMaterialTypes: [ReferenceMaterialTypes.Passage],
            MaxRewriteLevel: ReferenceRewriteLevels.L2,
            SlotPlan: [new ReferenceSlotValuePayload("object", "door")],
            LockedPhrasePolicy: "preserve physical afterbeat cadence",
            NoReuseReason: "",
            ProseDuties: ["interiority", "physical_afterbeat"],
            RiskFlags: ["fake_emotion"],
            StyleContract: new ReferenceBlueprintStyleContractPayload(
                StyleProfileIds: [99],
                StyleDimensions: ["dialogue_ratio", "paragraph_cadence"],
                ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                MinStyleFit: 1.25,
                AllowedCloseness: "moderate",
                RequiredEvidenceTypes: ["dialogue_exchange"],
                ForbiddenStyleRisks: ["source_leak"]));

        var review = new ReferenceChapterBlueprintReviewPayload(
            ReviewId: "review-1",
            BlueprintId: 10,
            ContextHash: "context-hash",
            SourcePlanHash: "plan-hash",
            AnalysisContractHash: "analysis-hash",
            ReviewVersion: 1,
            Status: ReferenceBlueprintReviewStatuses.Failed,
            Score: 0.45,
            LogicErrors: ["missing payoff"],
            CausalityErrors: ["beat 2 lacks causality_in"],
            EmotionErrors: ["emotion shift lacks external evidence"],
            NarrationErrors: ["dialogue beat lacks anti-screenplay duty"],
            ExecutionErrors: ["paragraph intention missing"],
            CharacterStateErrors: ["role-state delta missing"],
            PovErrors: ["pov leak"],
            ContinuityErrors: ["state mismatch"],
            TransitionErrors: ["scene jump lacks reason"],
            ForbiddenFactErrors: ["forbidden fact appears"],
            ReferenceBindingErrors: ["reference query missing"],
            MaterialFitErrors: ["semantic match lacks function fit"],
            ScreenplayDriftRisks: ["action dialogue only"],
            AiProseRisks: ["generic emotion label"],
            NovelisticNarrationErrors: ["beat reads like blocking"],
            RequiredFixes: ["add external evidence"],
            Defects:
            [
                new ReferenceChapterBlueprintReviewDefectPayload(
                    Category: "emotion",
                    FieldPath: "beat:beat-1:external_evidence",
                    BeatId: "beat-1",
                    Severity: "error",
                    Reason: "emotion shift lacks external evidence",
                    RequiredFix: "Add concrete observable evidence for the emotion shift.")
            ],
            ReviewedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"));

        var payload = new ReferenceChapterBlueprintPayload(
            BlueprintId: 10,
            NovelId: 42,
            ChapterNumber: 7,
            Title: "Chapter 7 Blueprint",
            Status: ReferenceBlueprintStates.ReviewPassed,
            SourcePlanScope: "chapter",
            SourcePlanHash: "plan-hash",
            ContextHash: "context-hash",
            AnalysisContractHash: "analysis-hash",
            BlueprintVersion: 1,
            ParentBlueprintId: 0,
            PrimaryAnchorId: 3,
            ChapterFunction: "turn suspicion into commitment",
            LogicAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("logic", "cause to hook", ["premise", "turn"]),
            EmotionAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("emotion", "suspicion to unease", ["trigger", "evidence"]),
            NarrationAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("narration", "close controlled interiority", ["distance", "rhythm"]),
            CharacterAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("character", "guarded to committed", ["goal", "misbelief"]),
            ReferenceAnalysis: new ReferenceChapterBlueprintAnalysisTrackPayload("reference", "bind material by function, emotion, POV, and prose duty", ["query", "rewrite budget"]),
            TransitionPlan: new ReferenceChapterBlueprintAnalysisTrackPayload("transition", "pressure-driven scene movement", ["door to witness"]),
            ExecutionContract: new ReferenceChapterBlueprintExecutionTrackPayload(
                Track: "execution",
                Summary: "novelistic paragraph execution before prose drafting",
                ParagraphIntentions: ["linger on hesitation"],
                ExecutionModes: ["dwell"],
                AntiScreenplayDuties: ["interiority before action"],
                SourceBackedDetailTargets: ["physical hesitation detail"],
                CandidateRejectionRules: ["reject dialogue-only prose"]),
            PreviousState: "protagonist doubts the clue",
            FinalState: "protagonist decides to confront a witness",
            FinalHook: "the witness is missing",
            GlobalPov: "protagonist",
            GlobalNarrativeDistance: "close",
            KnownFacts: ["the clue exists"],
            ForbiddenFacts: ["culprit identity"],
            RiskFlags: ["screenplay_drift"],
            Beats: [beat],
            LatestReview: review,
            CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"))
        {
            BuildVersion = "reference-blueprint-v1"
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal(7, root.GetProperty("chapter_number").GetInt32());
        Assert.Equal("review_passed", root.GetProperty("status").GetString());
        Assert.Equal("plan-hash", root.GetProperty("source_plan_hash").GetString());
        Assert.Equal("analysis-hash", root.GetProperty("analysis_contract_hash").GetString());
        Assert.Equal(1, root.GetProperty("blueprint_version").GetInt32());
        Assert.Equal("reference-blueprint-v1", root.GetProperty("build_version").GetString());
        Assert.Equal("logic", root.GetProperty("logic_analysis").GetProperty("track").GetString());
        Assert.Equal("emotion", root.GetProperty("emotion_analysis").GetProperty("track").GetString());
        Assert.Equal("narration", root.GetProperty("narration_analysis").GetProperty("track").GetString());
        Assert.Equal("character", root.GetProperty("character_analysis").GetProperty("track").GetString());
        Assert.Equal("reference", root.GetProperty("reference_analysis").GetProperty("track").GetString());
        Assert.Equal("transition", root.GetProperty("transition_plan").GetProperty("track").GetString());
        Assert.Equal("execution", root.GetProperty("execution_contract").GetProperty("track").GetString());
        Assert.Equal("close", root.GetProperty("global_narrative_distance").GetString());
        Assert.Equal("beat-1", root.GetProperty("beats")[0].GetProperty("beat_id").GetString());
        Assert.Equal("the clue contradicts the previous chapter state", root.GetProperty("beats")[0].GetProperty("logic_premise").GetString());
        Assert.Equal("camera stays with the protagonist after the clue is found", root.GetProperty("beats")[0].GetProperty("transition_in").GetString());
        Assert.Equal("does not voice the fear", root.GetProperty("beats")[0].GetProperty("suppressed_reaction").GetString());
        Assert.Equal("linger on hesitation before the next action", root.GetProperty("beats")[0].GetProperty("paragraph_intention").GetString());
        Assert.Equal("dwell", root.GetProperty("beats")[0].GetProperty("execution_mode").GetString());
        Assert.Equal("preserve physical afterbeat cadence", root.GetProperty("beats")[0].GetProperty("locked_phrase_policy").GetString());
        Assert.Equal(99, root.GetProperty("beats")[0].GetProperty("style_contract").GetProperty("style_profile_ids")[0].GetInt64());
        Assert.Equal("strong", root.GetProperty("beats")[0].GetProperty("style_contract").GetProperty("imitation_intensity").GetString());
        Assert.Equal(1.25, root.GetProperty("beats")[0].GetProperty("style_contract").GetProperty("min_style_fit").GetDouble());
        Assert.Equal("close interiority hesitation", root.GetProperty("beats")[0].GetProperty("reference_query").GetProperty("query").GetString());
        Assert.Equal("missing payoff", root.GetProperty("latest_review").GetProperty("logic_errors")[0].GetString());
        Assert.Equal(1, root.GetProperty("latest_review").GetProperty("review_version").GetInt32());
        Assert.Equal("beat 2 lacks causality_in", root.GetProperty("latest_review").GetProperty("causality_errors")[0].GetString());
        Assert.Equal("emotion shift lacks external evidence", root.GetProperty("latest_review").GetProperty("emotion_errors")[0].GetString());
        Assert.Equal("dialogue beat lacks anti-screenplay duty", root.GetProperty("latest_review").GetProperty("narration_errors")[0].GetString());
        Assert.Equal("paragraph intention missing", root.GetProperty("latest_review").GetProperty("execution_errors")[0].GetString());
        Assert.Equal("role-state delta missing", root.GetProperty("latest_review").GetProperty("character_state_errors")[0].GetString());
        Assert.Equal("pov leak", root.GetProperty("latest_review").GetProperty("pov_errors")[0].GetString());
        Assert.Equal("state mismatch", root.GetProperty("latest_review").GetProperty("continuity_errors")[0].GetString());
        Assert.Equal("scene jump lacks reason", root.GetProperty("latest_review").GetProperty("transition_errors")[0].GetString());
        Assert.Equal("forbidden fact appears", root.GetProperty("latest_review").GetProperty("forbidden_fact_errors")[0].GetString());
        Assert.Equal("reference query missing", root.GetProperty("latest_review").GetProperty("reference_binding_errors")[0].GetString());
        Assert.Equal("semantic match lacks function fit", root.GetProperty("latest_review").GetProperty("material_fit_errors")[0].GetString());
        Assert.Equal("action dialogue only", root.GetProperty("latest_review").GetProperty("screenplay_drift_risks")[0].GetString());
        Assert.Equal("generic emotion label", root.GetProperty("latest_review").GetProperty("ai_prose_risks")[0].GetString());
        Assert.Equal("beat reads like blocking", root.GetProperty("latest_review").GetProperty("novelistic_narration_errors")[0].GetString());
        Assert.Equal("add external evidence", root.GetProperty("latest_review").GetProperty("required_fixes")[0].GetString());
        var defect = root.GetProperty("latest_review").GetProperty("defects")[0];
        Assert.Equal("emotion", defect.GetProperty("category").GetString());
        Assert.Equal("beat:beat-1:external_evidence", defect.GetProperty("field_path").GetString());
        Assert.Equal("beat-1", defect.GetProperty("beat_id").GetString());
        Assert.Equal("error", defect.GetProperty("severity").GetString());
        Assert.Equal("emotion shift lacks external evidence", defect.GetProperty("reason").GetString());
        Assert.Equal("Add concrete observable evidence for the emotion shift.", defect.GetProperty("required_fix").GetString());
        Assert.False(root.TryGetProperty("BlueprintId", out _));
    }

    [Fact]
    public void ReferenceBlueprintRevisionPayloadsUseStableSnakeCaseJsonNames()
    {
        var payload = new ReviseReferenceChapterBlueprintPayload(
            NovelId: 42,
            BlueprintId: 10,
            Changes: [new ReferenceBlueprintRevisionChangePayload("beat:beat-1:paragraph_intention", "linger on the threshold")],
            Origin: "user",
            RevisionReason: "tighten novelistic execution");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("beat:beat-1:paragraph_intention", root.GetProperty("changes")[0].GetProperty("field_path").GetString());
        Assert.Equal("linger on the threshold", root.GetProperty("changes")[0].GetProperty("new_value").GetString());
        Assert.Equal("user", root.GetProperty("origin").GetString());
        Assert.Equal("tighten novelistic execution", root.GetProperty("revision_reason").GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
    }

    [Fact]
    public void ApproveReferenceChapterBlueprintPayloadUsesStableSnakeCaseJsonNames()
    {
        var payload = new ApproveReferenceChapterBlueprintPayload(
            NovelId: 42,
            BlueprintId: 10,
            ReviewId: "review-1",
            ApproverOrigin: "user");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("review-1", root.GetProperty("review_id").GetString());
        Assert.Equal("user", root.GetProperty("approver_origin").GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));

        var legacyPayload = new ApproveReferenceChapterBlueprintPayload(42, 10, "review-1");
        Assert.Equal("user", legacyPayload.ApproverOrigin);
    }

    [Fact]
    public void ReferenceOrchestrationPayloadsUseStableSnakeCaseJsonNames()
    {
        var policy = new ReferenceCorpusSearchPolicyPayload(
            Mode: "story_context",
            MaxResultsPerBeat: 4,
            LicenseStatuses: ["user_provided"],
            IncludeAnchorIds: [7],
            ExcludeAnchorIds: [9]);
        var stylePolicy = new ReferenceOrchestrationStylePolicyPayload(
            StyleProfileIds: [301],
            StyleDimensions: ["dialogue_ratio", "sensory_ratio"],
            ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
            MinStyleFit: 0.8,
            AllowedCloseness: "moderate",
            RequiredEvidenceTypes: ["dialogue_exchange"],
            ForbiddenStyleRisks: ["source_leak", "style_distance"]);
        var input = new StartReferenceOrchestrationRunPayload(
            NovelId: 42,
            ChapterNumber: 7,
            ChapterGoal: "rain-night confrontation",
            KnownFacts: ["林岚在门口"],
            ForbiddenFacts: ["凶手身份"],
            AnchorIds: null,
            CorpusSearchPolicy: policy,
            SourceConfirmed: false,
            StylePolicy: stylePolicy);

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var inputRoot = inputJson.RootElement;

        Assert.Equal(42, inputRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal(7, inputRoot.GetProperty("chapter_number").GetInt32());
        Assert.Equal("rain-night confrontation", inputRoot.GetProperty("chapter_goal").GetString());
        Assert.Equal("林岚在门口", inputRoot.GetProperty("known_facts")[0].GetString());
        Assert.Equal("凶手身份", inputRoot.GetProperty("forbidden_facts")[0].GetString());
        Assert.Equal(JsonValueKind.Null, inputRoot.GetProperty("anchor_ids").ValueKind);
        Assert.False(inputRoot.GetProperty("source_confirmed").GetBoolean());
        var policyRoot = inputRoot.GetProperty("corpus_search_policy");
        Assert.Equal("story_context", policyRoot.GetProperty("mode").GetString());
        Assert.Equal(4, policyRoot.GetProperty("max_results_per_beat").GetInt32());
        Assert.Equal("user_provided", policyRoot.GetProperty("license_statuses")[0].GetString());
        Assert.Equal(7, policyRoot.GetProperty("include_anchor_ids")[0].GetInt64());
        Assert.Equal(9, policyRoot.GetProperty("exclude_anchor_ids")[0].GetInt64());
        var stylePolicyRoot = inputRoot.GetProperty("style_policy");
        Assert.Equal(301, stylePolicyRoot.GetProperty("style_profile_ids")[0].GetInt64());
        Assert.Equal("dialogue_ratio", stylePolicyRoot.GetProperty("style_dimensions")[0].GetString());
        Assert.Equal("strong", stylePolicyRoot.GetProperty("imitation_intensity").GetString());
        Assert.Equal(0.8, stylePolicyRoot.GetProperty("min_style_fit").GetDouble());
        Assert.Equal("moderate", stylePolicyRoot.GetProperty("allowed_closeness").GetString());
        Assert.Equal("dialogue_exchange", stylePolicyRoot.GetProperty("required_evidence_types")[0].GetString());
        Assert.Equal("source_leak", stylePolicyRoot.GetProperty("forbidden_style_risks")[0].GetString());
        Assert.False(inputRoot.TryGetProperty("NovelId", out _));
        Assert.False(stylePolicyRoot.TryGetProperty("source_text", out _));
        Assert.False(stylePolicyRoot.TryGetProperty("candidate_text", out _));
        Assert.False(stylePolicyRoot.TryGetProperty("prompt", out _));
        Assert.False(stylePolicyRoot.TryGetProperty("path", out _));

        var run = new ReferenceOrchestrationRunPayload(
            RunId: "run-1",
            NovelId: 42,
            ChapterNumber: 7,
            Status: ReferenceOrchestrationRunStatuses.WaitingForUser,
            Stage: ReferenceOrchestrationStages.SourceConfirmation,
            ChapterGoal: "rain-night confrontation",
            KnownFacts: ["林岚在门口"],
            ForbiddenFacts: ["凶手身份"],
            AnchorIds: [],
            CorpusSearchPolicy: policy,
            BlueprintId: 0,
            ReviewId: "",
            CandidateIds: [],
            CurrentDecision: new ReferenceOrchestrationRequiredDecisionPayload(
                DecisionType: ReferenceOrchestrationDecisionTypes.ConfirmSourceAndFacts,
                StopReason: ReferenceOrchestrationStopReasons.SourceConfirmationRequired,
                Summary: "Confirm sources and fact boundaries before automation.",
                RequiredActions: ["confirm_source", "confirm_license_status", "confirm_known_facts", "confirm_forbidden_facts"],
                ApprovalSummary: new ReferenceOrchestrationApprovalSummaryPayload(
                    ChapterFunction: "turn hesitation into action",
                    Pov: "林岚 close",
                    FactBoundaryChanges: [],
                    EmotionalTrajectory: "guarded -> resolved",
                    MaterialUsePlan: "bind by beat function",
                    RewriteBudget: "L2",
                    HighRiskFindings: []),
                ProposedBlueprintRevision: new ReferenceOrchestrationBlueprintRevisionProposalPayload(
                    BlueprintId: 501,
                    ReviewId: "review-1",
                    Origin: "orchestrator",
                    RevisionReason: "deterministic fix proposal",
                    Changes: [new ReferenceBlueprintRevisionChangePayload("final_hook", "approved hook")])),
            LastStopReason: ReferenceOrchestrationStopReasons.SourceConfirmationRequired,
            ErrorMessage: "",
            CreatedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
            UpdatedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
            StylePolicy: stylePolicy);

        using var runJson = JsonDocument.Parse(JsonSerializer.Serialize(run, BridgeJson.SerializerOptions));
        var runRoot = runJson.RootElement;

        Assert.Equal("run-1", runRoot.GetProperty("run_id").GetString());
        Assert.Equal("waiting_for_user", runRoot.GetProperty("status").GetString());
        Assert.Equal("source_confirmation", runRoot.GetProperty("stage").GetString());
        Assert.Equal(0, runRoot.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("", runRoot.GetProperty("review_id").GetString());
        Assert.Equal("source_confirmation_required", runRoot.GetProperty("last_stop_reason").GetString());
        var runStylePolicy = runRoot.GetProperty("style_policy");
        Assert.Equal(301, runStylePolicy.GetProperty("style_profile_ids")[0].GetInt64());
        Assert.Equal("strong", runStylePolicy.GetProperty("imitation_intensity").GetString());
        Assert.False(runStylePolicy.TryGetProperty("source_text", out _));
        Assert.False(runStylePolicy.TryGetProperty("candidate_text", out _));
        Assert.False(runStylePolicy.TryGetProperty("prompt", out _));
        Assert.False(runStylePolicy.TryGetProperty("path", out _));
        var decision = runRoot.GetProperty("current_decision");
        Assert.Equal("confirm_source_and_facts", decision.GetProperty("decision_type").GetString());
        Assert.Equal("confirm_source", decision.GetProperty("required_actions")[0].GetString());
        Assert.Equal("confirm_license_status", decision.GetProperty("required_actions")[1].GetString());
        Assert.Equal("turn hesitation into action", decision.GetProperty("approval_summary").GetProperty("chapter_function").GetString());
        var proposal = decision.GetProperty("proposed_blueprint_revision");
        Assert.Equal(501, proposal.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("review-1", proposal.GetProperty("review_id").GetString());
        Assert.Equal("final_hook", proposal.GetProperty("changes")[0].GetProperty("field_path").GetString());
        Assert.False(runRoot.TryGetProperty("RunId", out _));

        var runEvent = new ReferenceOrchestrationRunEventPayload(
            EventId: 12,
            RunId: "run-1",
            NovelId: 42,
            EventType: "decision_resumed",
            Stage: ReferenceOrchestrationStages.BlueprintApproval,
            Status: ReferenceOrchestrationRunStatuses.Running,
            StopReason: ReferenceOrchestrationStopReasons.BlueprintApprovalRequired,
            DecisionType: ReferenceOrchestrationDecisionTypes.ApproveBlueprint,
            Summary: "user approved blueprint review-1",
            CreatedAt: DateTimeOffset.Parse("2026-07-05T00:01:00Z"));

        using var eventJson = JsonDocument.Parse(JsonSerializer.Serialize(runEvent, BridgeJson.SerializerOptions));
        var eventRoot = eventJson.RootElement;
        Assert.Equal(12, eventRoot.GetProperty("event_id").GetInt64());
        Assert.Equal("run-1", eventRoot.GetProperty("run_id").GetString());
        Assert.Equal("decision_resumed", eventRoot.GetProperty("event_type").GetString());
        Assert.Equal("blueprint_approval", eventRoot.GetProperty("stage").GetString());
        Assert.Equal("blueprint_approval_required", eventRoot.GetProperty("stop_reason").GetString());
        Assert.Equal("approve_blueprint", eventRoot.GetProperty("decision_type").GetString());
        Assert.Equal("user approved blueprint review-1", eventRoot.GetProperty("summary").GetString());
        Assert.False(eventRoot.TryGetProperty("EventId", out _));
    }

    [Fact]
    public void ReferenceReuseAuditPayloadsExposeNonSlotEditsAsSnakeCase()
    {
        var payload = new ReferenceReuseAuditPayload(
            AuditId: "audit-1",
            Status: "passed",
            RewriteLevel: ReferenceRewriteLevels.L2,
            ProvenanceErrors: [],
            UnsupportedFactErrors: [],
            AiProseRisks: [],
            NonSlotEdits: ["Inserted non-slot text '却' at offset 1."],
            RequiredFixes: [],
            AuditedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal("audit-1", root.GetProperty("audit_id").GetString());
        Assert.Equal("L2", root.GetProperty("rewrite_level").GetString());
        Assert.Equal("Inserted non-slot text '却' at offset 1.", root.GetProperty("non_slot_edits")[0].GetString());
        Assert.False(root.TryGetProperty("NonSlotEdits", out _));
    }

    [Fact]
    public void ReferenceUserFeedbackPayloadsUseStableSnakeCaseJsonNames()
    {
        var input = new RecordReferenceUserFeedbackPayload(
            NovelId: 42,
            TargetType: ReferenceFeedbackTargetTypes.ReuseCandidate,
            TargetId: "candidate-1",
            Decision: ReferenceFeedbackDecisions.Edited,
            MaterialId: "material-1",
            CandidateId: "candidate-1",
            BlueprintId: 10,
            BeatId: "beat-1",
            FeedbackTags: ["too_ai_flavored", "introduced_fact"],
            Note: "kept only the pressure image",
            EditedText: "edited candidate text",
            Origin: "user");

        using var inputJson = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var inputRoot = inputJson.RootElement;

        Assert.Equal(42, inputRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal("reuse_candidate", inputRoot.GetProperty("target_type").GetString());
        Assert.Equal("candidate-1", inputRoot.GetProperty("target_id").GetString());
        Assert.Equal("edited", inputRoot.GetProperty("decision").GetString());
        Assert.Equal("material-1", inputRoot.GetProperty("material_id").GetString());
        Assert.Equal("candidate-1", inputRoot.GetProperty("candidate_id").GetString());
        Assert.Equal(10, inputRoot.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("beat-1", inputRoot.GetProperty("beat_id").GetString());
        Assert.Equal("too_ai_flavored", inputRoot.GetProperty("feedback_tags")[0].GetString());
        Assert.Equal("kept only the pressure image", inputRoot.GetProperty("note").GetString());
        Assert.Equal("edited candidate text", inputRoot.GetProperty("edited_text").GetString());
        Assert.Equal("user", inputRoot.GetProperty("origin").GetString());
        Assert.False(inputRoot.TryGetProperty("NovelId", out _));

        var result = new ReferenceUserFeedbackPayload(
            FeedbackId: "feedback-1",
            NovelId: 42,
            TargetType: ReferenceFeedbackTargetTypes.ReuseCandidate,
            TargetId: "candidate-1",
            Decision: ReferenceFeedbackDecisions.Edited,
            MaterialId: "material-1",
            CandidateId: "candidate-1",
            BlueprintId: 10,
            BeatId: "beat-1",
            FeedbackTags: ["too_ai_flavored"],
            Note: "kept only the pressure image",
            EditedTextHash: "edited-hash",
            Origin: "user",
            CreatedAt: DateTimeOffset.Parse("2026-07-04T00:00:00Z"));

        using var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeJson.SerializerOptions));
        var resultRoot = resultJson.RootElement;

        Assert.Equal("feedback-1", resultRoot.GetProperty("feedback_id").GetString());
        Assert.Equal("edited-hash", resultRoot.GetProperty("edited_text_hash").GetString());
        Assert.Equal("too_ai_flavored", resultRoot.GetProperty("feedback_tags")[0].GetString());
        Assert.False(resultRoot.TryGetProperty("EditedTextHash", out _));
    }

    [Fact]
    public void ReferenceMaterialTagUpdatePayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new UpdateReferenceMaterialTagsPayload(
            NovelId: 42,
            MaterialId: "material-1",
            FunctionTag: "interiority",
            EmotionTag: "unease",
            SceneTag: "threshold",
            PovTag: "close",
            TechniqueTag: "afterbeat",
            Origin: "user",
            Note: "manual correction after reviewing search results");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal("material-1", root.GetProperty("material_id").GetString());
        Assert.Equal("interiority", root.GetProperty("function_tag").GetString());
        Assert.Equal("unease", root.GetProperty("emotion_tag").GetString());
        Assert.Equal("threshold", root.GetProperty("scene_tag").GetString());
        Assert.Equal("close", root.GetProperty("pov_tag").GetString());
        Assert.Equal("afterbeat", root.GetProperty("technique_tag").GetString());
        Assert.Equal("user", root.GetProperty("origin").GetString());
        Assert.Equal("manual correction after reviewing search results", root.GetProperty("note").GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
    }

    [Fact]
    public void ReferenceMaterialsTagUpdatePayloadUsesStableSnakeCaseJsonNames()
    {
        var input = new UpdateReferenceMaterialsTagsPayload(
            NovelId: 42,
            MaterialIds: ["material-1", "material-2"],
            FunctionTag: "interiority",
            EmotionTag: "unease",
            SceneTag: "threshold",
            PovTag: "close",
            TechniqueTag: "afterbeat",
            Origin: "user",
            Note: "bulk correction after reviewing search results");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(input, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(["material-1", "material-2"], root.GetProperty("material_ids").EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray());
        Assert.Equal("interiority", root.GetProperty("function_tag").GetString());
        Assert.Equal("unease", root.GetProperty("emotion_tag").GetString());
        Assert.Equal("threshold", root.GetProperty("scene_tag").GetString());
        Assert.Equal("close", root.GetProperty("pov_tag").GetString());
        Assert.Equal("afterbeat", root.GetProperty("technique_tag").GetString());
        Assert.Equal("user", root.GetProperty("origin").GetString());
        Assert.Equal("bulk correction after reviewing search results", root.GetProperty("note").GetString());
        Assert.False(root.TryGetProperty("NovelId", out _));
        Assert.False(root.TryGetProperty("MaterialIds", out _));
    }

    [Fact]
    public void ReferenceConstantsDocumentInitialStateAndRewriteVocabulary()
    {
        Assert.Equal("L0", ReferenceRewriteLevels.L0);
        Assert.Equal("L4", ReferenceRewriteLevels.L4);
        Assert.Contains(ReferenceAnchorBuildStates.Ready, ReferenceAnchorBuildStates.All);
        Assert.Contains(ReferenceAnchorBuildStates.FailedEmbedding, ReferenceAnchorBuildStates.All);
        Assert.Contains(ReferenceBlueprintStates.Approved, ReferenceBlueprintStates.All);
        Assert.Contains(ReferenceBlueprintStates.Stale, ReferenceBlueprintStates.All);
        Assert.Contains(ReferenceBlueprintStates.Normalized, ReferenceBlueprintStates.All);
        Assert.Contains(ReferenceBlueprintStates.MaterialBound, ReferenceBlueprintStates.All);
        Assert.Contains(ReferenceBlueprintBeatTypes.Interiority, ReferenceBlueprintBeatTypes.All);
        Assert.Contains(ReferenceBlueprintReviewStatuses.Failed, ReferenceBlueprintReviewStatuses.All);
        Assert.Contains(ReferenceOrchestrationRunStatuses.WaitingForUser, ReferenceOrchestrationRunStatuses.All);
        Assert.Contains(ReferenceOrchestrationStages.SourceConfirmation, ReferenceOrchestrationStages.All);
        Assert.Contains(ReferenceOrchestrationDecisionTypes.ApproveBlueprint, ReferenceOrchestrationDecisionTypes.All);
        Assert.Contains(ReferenceOrchestrationDecisionTypes.ResolveHighRiskStop, ReferenceOrchestrationDecisionTypes.All);
        Assert.Contains(ReferenceOrchestrationStopReasons.HighRiskGateBlocked, ReferenceOrchestrationStopReasons.All);
        Assert.Contains(ReferenceOrchestrationStopReasons.FinalInsertionRequired, ReferenceOrchestrationStopReasons.All);
        Assert.Contains(ReferenceOrchestrationStopReasons.DraftAuditFailed, ReferenceOrchestrationStopReasons.All);
        Assert.Contains(ReferenceFeedbackDecisions.Accepted, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackDecisions.Rejected, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackDecisions.Edited, ReferenceFeedbackDecisions.All);
        Assert.Contains(ReferenceFeedbackTargetTypes.ReuseCandidate, ReferenceFeedbackTargetTypes.All);
        Assert.Contains(ReferenceStyleAttemptStatuses.Attempted, ReferenceStyleAttemptStatuses.All);
        Assert.Contains(ReferenceStyleAttemptStatuses.RetrievalGap, ReferenceStyleAttemptStatuses.All);
        Assert.Contains(ReferenceStyleProfileBuildStatuses.Running, ReferenceStyleProfileBuildStatuses.All);
        Assert.Contains(ReferenceStyleProfileBuildStatuses.Completed, ReferenceStyleProfileBuildStatuses.All);
        Assert.Contains(ReferenceStyleProfileBuildStatuses.Failed, ReferenceStyleProfileBuildStatuses.All);
        Assert.Contains(ReferenceStyleProfileBuildStatuses.Cancelled, ReferenceStyleProfileBuildStatuses.All);
    }

    [Fact]
    public void AnchoredDraftPayloadSerializesBeatCandidatesWithoutFullChapterAssembly()
    {
        var payload = new ReferenceAnchoredDraftPayload(
            BlueprintId: 10,
            Candidates:
            [
                new ReferenceDraftParagraphCandidatePayload(
                    CandidateId: "candidate-1",
                    BlueprintId: 10,
                    BeatId: "beat-1",
                    MaterialId: "material-1",
                    RewriteLevel: ReferenceRewriteLevels.L1,
                    Text: "候选段落",
                    ChangedSlots: [new ReferenceSlotValuePayload("object", "门")],
                    NonSlotEdits: [],
                    AuditStatus: "passed",
                    CreatedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
                    StyleAttempts:
                    [
                        new ReferenceDraftStyleAttemptPayload(
                            StyleProfileIds: [99],
                            StyleDimensions: ["dialogue_ratio", "sensory_ratio"],
                            ImitationIntensity: ReferenceStyleImitationIntensities.Strong,
                            MinStyleFit: 0.8,
                            AllowedCloseness: "moderate",
                            RequiredEvidenceTypes: ["dialogue_exchange"],
                            ForbiddenStyleRisks: ["source_leak"],
                            SelectedMaterialStyleFit: 1.25,
                            SelectedMaterialLowConfidence: false,
                            Status: ReferenceStyleAttemptStatuses.Attempted)
                    ])
            ],
            Audit: null);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(10, root.GetProperty("blueprint_id").GetInt64());
        var candidate = Assert.Single(root.GetProperty("candidates").EnumerateArray());
        Assert.Equal("beat-1", candidate.GetProperty("beat_id").GetString());
        Assert.Equal("候选段落", candidate.GetProperty("text").GetString());
        var styleAttempt = Assert.Single(candidate.GetProperty("style_attempts").EnumerateArray());
        Assert.Equal(99, styleAttempt.GetProperty("style_profile_ids")[0].GetInt64());
        Assert.Equal("dialogue_ratio", styleAttempt.GetProperty("style_dimensions")[0].GetString());
        Assert.Equal("strong", styleAttempt.GetProperty("imitation_intensity").GetString());
        Assert.Equal(0.8, styleAttempt.GetProperty("min_style_fit").GetDouble());
        Assert.Equal("moderate", styleAttempt.GetProperty("allowed_closeness").GetString());
        Assert.Equal("dialogue_exchange", styleAttempt.GetProperty("required_evidence_types")[0].GetString());
        Assert.Equal("source_leak", styleAttempt.GetProperty("forbidden_style_risks")[0].GetString());
        Assert.Equal(1.25, styleAttempt.GetProperty("selected_material_style_fit").GetDouble());
        Assert.False(styleAttempt.GetProperty("selected_material_low_confidence").GetBoolean());
        Assert.Equal("attempted", styleAttempt.GetProperty("status").GetString());
        Assert.False(root.TryGetProperty("chapter_text", out _));
        Assert.False(root.TryGetProperty("assembled_text", out _));
        Assert.False(root.TryGetProperty("full_chapter", out _));
        Assert.False(styleAttempt.TryGetProperty("source_text", out _));
        Assert.False(styleAttempt.TryGetProperty("text", out _));
        Assert.False(styleAttempt.TryGetProperty("prompt", out _));
    }

    [Fact]
    public void GenerateAnchoredDraftPayloadSupportsOptionalStyleIntensityMatrix()
    {
        var payload = new GenerateReferenceAnchoredDraftPayload(
            NovelId: 42,
            BlueprintId: 501,
            BeatIds: ["beat-1"],
            StyleIntensities:
            [
                ReferenceStyleImitationIntensities.Loose,
                ReferenceStyleImitationIntensities.Moderate,
                ReferenceStyleImitationIntensities.Strong
            ],
            CandidatesPerBeat: 3);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions));
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(501, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("beat-1", root.GetProperty("beat_ids")[0].GetString());
        Assert.Equal("loose", root.GetProperty("style_intensities")[0].GetString());
        Assert.Equal("moderate", root.GetProperty("style_intensities")[1].GetString());
        Assert.Equal("strong", root.GetProperty("style_intensities")[2].GetString());
        Assert.Equal(3, root.GetProperty("candidates_per_beat").GetInt32());
        Assert.False(root.TryGetProperty("content", out _));
        Assert.False(root.TryGetProperty("chapter_text", out _));
        Assert.False(root.TryGetProperty("SaveContent", out _));
    }

    [Fact]
    public void AnchoredDraftAuditPayloadSerializesReadableReportWithoutCandidateOrSourceText()
    {
        var payload = new ReferenceAnchoredDraftAuditPayload(
            AuditId: "draft-audit-1",
            BlueprintId: 10,
            Status: "failed",
            RewriteLevel: ReferenceRewriteLevels.L3,
            ProvenanceErrors: ["Candidate candidate-1 uses low-confidence weak match material provenance."],
            BlueprintErrors: [],
            UnsupportedFactErrors: [],
            PovErrors: [],
            AiProseRisks: [],
            RequiredFixes: ["Bind stronger reference material for candidate candidate-1."],
            AuditedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"),
            CandidateIds: ["candidate-1"],
            ReadableReport: new ReferenceDraftAuditReadableReportPayload(
                Summary: "Draft audit failed for 1 candidate.",
                CandidateIds: ["candidate-1"],
                Findings:
                [
                    new ReferenceDraftAuditReadableFindingPayload(
                        Category: "provenance",
                        Severity: "error",
                        CandidateIds: ["candidate-1"],
                        Message: "Candidate candidate-1 uses low-confidence weak match material provenance.",
                        RequiredAction: "Bind stronger reference material for candidate candidate-1.")
                ]));

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal("draft-audit-1", root.GetProperty("audit_id").GetString());
        Assert.Equal("candidate-1", root.GetProperty("candidate_ids")[0].GetString());
        var report = root.GetProperty("readable_report");
        Assert.Equal("Draft audit failed for 1 candidate.", report.GetProperty("summary").GetString());
        Assert.Equal("candidate-1", report.GetProperty("candidate_ids")[0].GetString());
        var finding = Assert.Single(report.GetProperty("findings").EnumerateArray());
        Assert.Equal("provenance", finding.GetProperty("category").GetString());
        Assert.Equal("error", finding.GetProperty("severity").GetString());
        Assert.Equal("candidate-1", finding.GetProperty("candidate_ids")[0].GetString());
        Assert.Equal("Bind stronger reference material for candidate candidate-1.", finding.GetProperty("required_action").GetString());
        Assert.False(finding.TryGetProperty("candidate_text", out _));
        Assert.False(finding.TryGetProperty("source_text", out _));
        Assert.False(finding.TryGetProperty("prompt", out _));
        Assert.DoesNotContain("候选段落", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("source text", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetAnchoredDraftAuditsPayloadUsesStableSnakeCaseWithoutTextFields()
    {
        var payload = new GetReferenceAnchoredDraftAuditsPayload(
            NovelId: 42,
            BlueprintId: 501,
            CandidateIds: ["candidate-1", "candidate-2"],
            Limit: 25);

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(501, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("candidate-1", root.GetProperty("candidate_ids")[0].GetString());
        Assert.Equal("candidate-2", root.GetProperty("candidate_ids")[1].GetString());
        Assert.Equal(25, root.GetProperty("limit").GetInt32());
        Assert.False(root.TryGetProperty("candidate_text", out _));
        Assert.False(root.TryGetProperty("source_text", out _));
        Assert.False(root.TryGetProperty("prompt", out _));
        Assert.False(root.TryGetProperty("path", out _));
    }

    [Fact]
    public void GetReferenceDraftCandidatesPayloadUsesStableSnakeCaseWithoutSourceOrSaveFields()
    {
        var payload = new GetReferenceDraftCandidatesPayload(
            NovelId: 42,
            BlueprintId: 501,
            CandidateIds: ["candidate-1", "candidate-2"]);

        var serialized = JsonSerializer.Serialize(payload, BridgeJson.SerializerOptions);
        using var json = JsonDocument.Parse(serialized);
        var root = json.RootElement;

        Assert.Equal(42, root.GetProperty("novel_id").GetInt64());
        Assert.Equal(501, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("candidate-1", root.GetProperty("candidate_ids")[0].GetString());
        Assert.Equal("candidate-2", root.GetProperty("candidate_ids")[1].GetString());
        Assert.False(root.TryGetProperty("text", out _));
        Assert.False(root.TryGetProperty("candidate_text", out _));
        Assert.False(root.TryGetProperty("source_text", out _));
        Assert.False(root.TryGetProperty("source_path", out _));
        Assert.False(root.TryGetProperty("prompt", out _));
        Assert.False(root.TryGetProperty("path", out _));
        Assert.False(root.TryGetProperty("SaveContent", out _));
    }

    [Fact]
    public void ReferenceStyleAuditFindingPayloadUsesStableSnakeCaseWithoutTextFields()
    {
        var input = new GetReferenceStyleAuditFindingsPayload(
            NovelId: 42,
            BlueprintId: 501,
            CandidateIds: ["candidate-1"],
            RiskTypes: ["source_leak", "style_distance"],
            Limit: 25);
        var finding = new ReferenceStyleAuditFindingPayload(
            AuditId: "draft-audit-1",
            BlueprintId: 501,
            Status: "failed",
            RewriteLevel: ReferenceRewriteLevels.L2,
            CandidateIds: ["candidate-1"],
            RiskType: "source_leak",
            Category: "required_fix",
            Severity: "action",
            Message: "Source-leak risk for candidate candidate-1: longest exact shared phrase length 20 exceeded threshold.",
            RequiredAction: "Resolve source-leak risk before insertion.",
            AuditedAt: DateTimeOffset.Parse("2026-07-05T00:00:00Z"));

        var inputJson = JsonSerializer.Serialize(input, BridgeJson.SerializerOptions);
        using var inputDocument = JsonDocument.Parse(inputJson);
        var inputRoot = inputDocument.RootElement;
        Assert.Equal(42, inputRoot.GetProperty("novel_id").GetInt64());
        Assert.Equal(501, inputRoot.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("candidate-1", inputRoot.GetProperty("candidate_ids")[0].GetString());
        Assert.Equal("source_leak", inputRoot.GetProperty("risk_types")[0].GetString());
        Assert.Equal("style_distance", inputRoot.GetProperty("risk_types")[1].GetString());
        Assert.Equal(25, inputRoot.GetProperty("limit").GetInt32());

        var findingJson = JsonSerializer.Serialize(finding, BridgeJson.SerializerOptions);
        using var findingDocument = JsonDocument.Parse(findingJson);
        var root = findingDocument.RootElement;
        Assert.Equal("draft-audit-1", root.GetProperty("audit_id").GetString());
        Assert.Equal(501, root.GetProperty("blueprint_id").GetInt64());
        Assert.Equal("source_leak", root.GetProperty("risk_type").GetString());
        Assert.Equal("candidate-1", root.GetProperty("candidate_ids")[0].GetString());
        Assert.Equal("Resolve source-leak risk before insertion.", root.GetProperty("required_action").GetString());
        Assert.False(root.TryGetProperty("candidate_text", out _));
        Assert.False(root.TryGetProperty("source_text", out _));
        Assert.False(root.TryGetProperty("prompt", out _));
        Assert.False(root.TryGetProperty("path", out _));
        Assert.False(root.TryGetProperty("content", out _));
    }

    [Fact]
    public void CompatibilityRegistryIncludesReferenceAnchorMethods()
    {
        string[] expected =
        [
            "CreateReferenceAnchor",
            "CreateReferenceAnchors",
            "CreateReferenceAnchorsWithResult",
            "GetReferenceAnchors",
            "DeleteReferenceAnchor",
            "DeleteReferenceAnchors",
            "DeleteReferenceMaterials",
            "RestoreReferenceMaterials",
            "PromoteReferenceAnchorsToWorkspaceCorpus",
            "PromoteReferenceAnchorToWorkspaceCorpus",
            "UpdateReferenceAnchorMetadata",
            "RebuildReferenceAnchor",
            "GetReferenceAnchorBuildStatus",
            "SearchReferenceMaterials",
            "GetReferenceMaterialDetail",
            "GetReferenceMaterialTagReviewQueue",
            "GetReferenceSourceSegmentDetail",
            "GetReferenceSourceProcessingDetail",
            "AdaptReferenceMaterial",
            "AuditReferenceReuse",
            "RecordReferenceUserFeedback",
            "GetReferenceUserFeedback",
            "UpdateReferenceMaterialTags",
            "UpdateReferenceMaterialsTags",
            "GenerateReferenceChapterBlueprint",
            "GetReferenceChapterBlueprints",
            "GetReferenceChapterBlueprint",
            "ReviewReferenceChapterBlueprint",
            "ReviseReferenceChapterBlueprint",
            "ApproveReferenceChapterBlueprint",
            "BindReferenceBlueprintMaterials",
            "GenerateReferenceAnchoredDraft",
            "GetReferenceDraftCandidates",
            "AuditReferenceAnchoredDraft",
            "GetReferenceAnchoredDraftAudits",
            "GetReferenceStyleAuditFindings",
            "ArchiveReferenceStyleProfile",
            "BuildReferenceStyleProfile",
            "CompareReferenceStyleProfiles",
            "GetReferenceStyleProfiles",
            "GetReferenceStyleProfile",
            "GetReferenceStyleProfileBuildStatus",
            "CancelReferenceStyleProfileBuild",
            "RestoreReferenceStyleProfile",
            "StartReferenceOrchestrationRun",
            "GetReferenceOrchestrationRuns",
            "GetReferenceOrchestrationRun",
            "ResumeReferenceOrchestrationRun",
            "CancelReferenceOrchestrationRun"
        ];

        foreach (var method in expected)
        {
            Assert.Contains(method, BridgeCompatibilityAppMethods.MethodNames);
        }
    }
}
