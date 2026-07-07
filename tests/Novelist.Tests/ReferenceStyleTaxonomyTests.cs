using System.Text.RegularExpressions;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed partial class ReferenceStyleTaxonomyTests
{
    [Fact]
    public void TaxonomyV1CoversCoreAndWebFictionStyleDimensions()
    {
        var required = new[]
        {
            "narration_distance",
            "pov_control",
            "rhythm",
            "sentence_shape",
            "paragraph_cadence",
            "dialogue_mechanics",
            "subtext",
            "externalized_emotion",
            "sensory_image",
            "metaphor_system",
            "image_system",
            "tension_pressure",
            "hook_pattern",
            "payoff_pattern",
            "transition_pattern",
            "exposition_handling",
            "action_clarity",
            "anti_screenplay_prose",
            "chapter_hook",
            "escalation_beat",
            "payoff_beat",
            "compression_expansion",
            "pleasure_point_delivery",
            "cliffhanger_type",
            "information_withholding",
            "reader_promise_tracking"
        };

        Assert.Equal(ReferenceStyleTaxonomyVersions.V1, ReferenceStyleTaxonomy.Version);
        Assert.Empty(required.Except(ReferenceStyleTaxonomy.FeatureKeys, StringComparer.Ordinal));
        Assert.Equal(
            ReferenceStyleTaxonomy.FeatureKeys.Count,
            ReferenceStyleTaxonomy.FeatureKeys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TaxonomyDefinitionsAreStableSnakeCaseAndBeatCompatible()
    {
        Assert.All(ReferenceStyleTaxonomy.Features, feature =>
        {
            Assert.Matches(SnakeCasePattern(), feature.FeatureKey);
            Assert.False(string.IsNullOrWhiteSpace(feature.Category));
            Assert.False(string.IsNullOrWhiteSpace(feature.Description));
            Assert.NotEmpty(feature.Labels);
            Assert.NotEmpty(feature.CompatibleBeatDuties);
            Assert.Equal(feature.Labels.Count, feature.Labels.Distinct(StringComparer.Ordinal).Count());
            Assert.All(feature.Labels, label => Assert.Matches(SnakeCasePattern(), label));
            Assert.All(feature.CompatibleBeatDuties, duty => Assert.Matches(SnakeCasePattern(), duty));
        });

        Assert.Contains("dialogue", ReferenceStyleTaxonomy.GetFeature("dialogue_mechanics").CompatibleBeatDuties);
        Assert.Contains("subtext", ReferenceStyleTaxonomy.GetFeature("subtext").CompatibleBeatDuties);
        Assert.Contains("external_evidence", ReferenceStyleTaxonomy.GetFeature("externalized_emotion").CompatibleBeatDuties);
        Assert.Contains("sensory_anchor", ReferenceStyleTaxonomy.GetFeature("sensory_image").CompatibleBeatDuties);
        Assert.Contains("hook", ReferenceStyleTaxonomy.GetFeature("chapter_hook").CompatibleBeatDuties);
        Assert.Contains("transition", ReferenceStyleTaxonomy.GetFeature("transition_pattern").CompatibleBeatDuties);
        Assert.Contains("anti_screenplay", ReferenceStyleTaxonomy.GetFeature("anti_screenplay_prose").CompatibleBeatDuties);
    }

    [Fact]
    public void ValidatorAcceptsEveryTaxonomyLabelWhenItIsGrounded()
    {
        foreach (var feature in ReferenceStyleTaxonomy.Features)
        {
            foreach (var label in feature.Labels)
            {
                var json = $$"""
                {
                  "schema_version": "reference-style-llm-analysis-v1",
                  "labels": [
                    {
                      "feature_key": "{{feature.FeatureKey}}",
                      "label": "{{label}}",
                      "confidence": 0.73,
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
                """;

                var result = ReferenceStyleLlmAnalysisValidator.Validate(77, json, [Window()]);

                Assert.Equal(ReferenceStyleLlmAnalysisValidationStatuses.Passed, result.Status);
                var evidence = Assert.Single(result.EvidenceSpans);
                Assert.Equal(feature.FeatureKey, evidence.FeatureKey);
                Assert.Equal(label, evidence.Label);
                Assert.Equal(ReferenceStyleAnalyzerSources.LlmAssisted, evidence.AnalyzerSource);
            }
        }
    }

    [Fact]
    public void ValidatorRejectsUnknownLabelsForSupportedFeatureKeys()
    {
        var result = ReferenceStyleLlmAnalysisValidator.Validate(
            77,
            """
            {
              "schema_version": "reference-style-llm-analysis-v1",
              "labels": [
                {
                  "feature_key": "hook_pattern",
                  "label": "invented_label",
                  "confidence": 0.73,
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

        Assert.Equal(ReferenceStyleLlmAnalysisValidationStatuses.Rejected, result.Status);
        Assert.Empty(result.EvidenceSpans);
        Assert.Contains(result.RejectedLabels, item => item.FeatureKey == "hook_pattern" && item.Label == "invented_label");
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

    [GeneratedRegex("^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SnakeCasePattern();
}
