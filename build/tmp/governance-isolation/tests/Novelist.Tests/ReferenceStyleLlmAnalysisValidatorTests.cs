using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceStyleLlmAnalysisValidatorTests
{
    [Fact]
    public void ValidateAcceptsGroundedLabelsFromBoundedWindows()
    {
        var result = ReferenceStyleLlmAnalysisValidator.Validate(
            profileId: 77,
            """
            {
              "schema_version": "reference-style-llm-analysis-v1",
              "labels": [
                {
                  "feature_key": "narration_distance",
                  "label": "close_limited",
                  "confidence": 0.82,
                  "evidence": [
                    {
                      "source_segment_id": "seg-1",
                      "material_id": "mat-1",
                      "start_offset": 4,
                      "end_offset": 12
                    }
                  ]
                }
              ]
            }
            """,
            [Window()]);

        Assert.Equal(ReferenceStyleLlmAnalysisValidationStatuses.Passed, result.Status);
        Assert.Empty(result.RejectedLabels);
        var evidence = Assert.Single(result.EvidenceSpans);
        Assert.Equal(77, evidence.ProfileId);
        Assert.Equal(12, evidence.AnchorId);
        Assert.Equal("seg-1", evidence.SourceSegmentId);
        Assert.Equal("mat-1", evidence.MaterialId);
        Assert.Equal("narration_distance", evidence.FeatureKey);
        Assert.Equal("close_limited", evidence.Label);
        Assert.Equal(4, evidence.StartOffset);
        Assert.Equal(12, evidence.EndOffset);
        Assert.Equal("hash-window", evidence.TextHash);
        Assert.Equal(0.82, evidence.Confidence);
        Assert.Equal(ReferenceStyleAnalyzerSources.LlmAssisted, evidence.AnalyzerSource);
    }

    [Fact]
    public void ValidateRejectsUngroundedOffsetsOutsideBoundedWindow()
    {
        var result = ReferenceStyleLlmAnalysisValidator.Validate(
            profileId: 77,
            """
            {
              "schema_version": "reference-style-llm-analysis-v1",
              "labels": [
                {
                  "feature_key": "dialogue_mechanics",
                  "label": "short_turns",
                  "confidence": 0.74,
                  "evidence": [
                    {
                      "source_segment_id": "seg-1",
                      "material_id": "mat-1",
                      "start_offset": 4,
                      "end_offset": 31
                    }
                  ]
                }
              ]
            }
            """,
            [Window()]);

        Assert.Equal(ReferenceStyleLlmAnalysisValidationStatuses.Rejected, result.Status);
        Assert.Empty(result.EvidenceSpans);
        Assert.Contains(result.Diagnostics, item => item.Contains("outside bounded source window", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RejectedLabels, item => item.FeatureKey == "dialogue_mechanics" && item.Reason.Contains("ungrounded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateRejectsInvalidJsonAndUnsupportedSchema()
    {
        var invalidJson = ReferenceStyleLlmAnalysisValidator.Validate(77, "{not json", [Window()]);

        Assert.Equal(ReferenceStyleLlmAnalysisValidationStatuses.InvalidJson, invalidJson.Status);
        Assert.Empty(invalidJson.EvidenceSpans);
        Assert.Contains(invalidJson.Diagnostics, item => item.Contains("valid JSON", StringComparison.OrdinalIgnoreCase));

        var unsupportedSchema = ReferenceStyleLlmAnalysisValidator.Validate(
            77,
            """{"schema_version":"old","labels":[]}""",
            [Window()]);

        Assert.Equal(ReferenceStyleLlmAnalysisValidationStatuses.InvalidSchema, unsupportedSchema.Status);
        Assert.Contains(unsupportedSchema.Diagnostics, item => item.Contains("schema_version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateRejectsUnsupportedFeatureKeysAndDowngradesOverconfidentLabels()
    {
        var result = ReferenceStyleLlmAnalysisValidator.Validate(
            profileId: 77,
            """
            {
              "schema_version": "reference-style-llm-analysis-v1",
              "labels": [
                {
                  "feature_key": "unsupported_magic",
                  "label": "too_vague",
                  "confidence": 0.70,
                  "evidence": [
                    {
                      "source_segment_id": "seg-1",
                      "material_id": "mat-1",
                      "start_offset": 4,
                      "end_offset": 12
                    }
                  ]
                },
                {
                  "feature_key": "hook_pattern",
                  "label": "question_tail",
                  "confidence": 0.999,
                  "evidence": [
                    {
                      "source_segment_id": "seg-1",
                      "material_id": "mat-1",
                      "start_offset": 4,
                      "end_offset": 12
                    }
                  ]
                }
              ]
            }
            """,
            [Window()]);

        Assert.Equal(ReferenceStyleLlmAnalysisValidationStatuses.Partial, result.Status);
        Assert.Contains(result.RejectedLabels, item => item.FeatureKey == "unsupported_magic");
        var accepted = Assert.Single(result.EvidenceSpans);
        Assert.Equal("hook_pattern", accepted.FeatureKey);
        Assert.Equal(0.95, accepted.Confidence);
        Assert.Contains(result.Diagnostics, item => item.Contains("downgraded", StringComparison.OrdinalIgnoreCase));
    }

    private static ReferenceStyleAnalysisWindowPayload Window()
    {
        return new ReferenceStyleAnalysisWindowPayload(
            WindowId: "win-1",
            AnchorId: 12,
            SourceSegmentId: "seg-1",
            MaterialId: "mat-1",
            StartOffset: 0,
            EndOffset: 24,
            TextHash: "hash-window",
            Text: "她说：门口别停。雨声压住门口。");
    }
}
