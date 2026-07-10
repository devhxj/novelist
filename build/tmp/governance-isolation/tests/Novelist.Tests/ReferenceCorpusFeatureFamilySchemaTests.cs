using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceCorpusFeatureFamilySchemaTests
{
    [Fact]
    public void RegistryLoadsTenLockedFeatureFamilies()
    {
        var schemas = ReferenceCorpusFeatureFamilySchemaRegistry.All;

        Assert.Equal(10, schemas.Count);
        foreach (var family in ReferenceCorpusFeatureFamilies.All)
        {
            var schema = schemas[family];
            Assert.Equal(ReferenceCorpusFeatureFamilySchemaVersions.V1, schema.SchemaVersion);
            Assert.Equal(family, schema.Family);
            Assert.NotEmpty(schema.RequiredObservationFields);
            Assert.NotEmpty(schema.ObservationFields);
            Assert.Contains("feature_key", schema.RequiredObservationFields);
            Assert.Contains("confidence", schema.RequiredObservationFields);
            Assert.Contains("evidence_start", schema.RequiredObservationFields);
            Assert.Contains("evidence_end", schema.RequiredObservationFields);
            Assert.Contains("explanation", schema.RequiredObservationFields);
        }

        Assert.All(ReferenceCorpusFeatureFamilies.SentenceFamilies, family =>
            Assert.Equal("sentence", schemas[family].NodeType));
        Assert.All(ReferenceCorpusFeatureFamilies.PassageFamilies, family =>
            Assert.Equal("passage", schemas[family].NodeType));
    }

    [Fact]
    public void ValidateAcceptsSentenceSensoryArrayOutputAndBuildsObservationCandidates()
    {
        var result = ReferenceCorpusFeatureFamilyOutputValidator.Validate(
            """
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "sensory",
              "node_type": "sentence",
              "observations": [
                {
                  "feature_key": "sensory_detail",
                  "sense": "auditory",
                  "intensity": 7.5,
                  "narrative_function": "raise_tension",
                  "confidence": 0.84,
                  "evidence_start": 0,
                  "evidence_end": 8,
                  "explanation": "雨声作为近景听觉压力，压住人物尚未开口的动作。"
                },
                {
                  "feature_key": "sensory_detail",
                  "sense": "tactile",
                  "intensity": 5,
                  "narrative_function": "reveal_state",
                  "confidence": 0.72,
                  "evidence_start": 9,
                  "evidence_end": 16,
                  "explanation": "掌心动作承担压抑状态，不直接写心理词。"
                }
              ]
            }
            """,
            ReferenceCorpusFeatureFamilies.Sensory,
            "sentence",
            sourceTextLength: 24);

        Assert.Equal(ReferenceCorpusFeatureFamilyValidationStatuses.Passed, result.Status);
        Assert.Empty(result.RejectedObservations);
        var first = Assert.Single(result.AcceptedObservations);
        Assert.Equal("sensory", first.FeatureFamily);
        Assert.Equal("senses", first.FeatureKey);
        Assert.Equal("array", first.ValueKind);
        Assert.Equal("auditory,tactile", first.ValueText);
        Assert.Equal(7.5, first.Intensity);
        Assert.Equal(7.5, first.ValueNum);
        Assert.Equal(0.72, first.Confidence);
        Assert.Equal(0, first.EvidenceStart);
        Assert.Equal(16, first.EvidenceEnd);
        Assert.Contains("\"sense\":\"auditory\"", first.ValueJson);
        Assert.Contains("\"sense\":\"tactile\"", first.ValueJson);
        Assert.Contains("\"narrative_function\":\"raise_tension\"", first.ValueJson);
    }

    [Fact]
    public void ValidateAcceptsPassageCommercialOutput()
    {
        var result = ReferenceCorpusFeatureFamilyOutputValidator.Validate(
            """
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "commercial",
              "node_type": "passage",
              "observations": [
                {
                  "feature_key": "mechanics",
                  "hook_type": "question",
                  "payoff_density": "low",
                  "expectation_management": "delay",
                  "confidence": 0.8,
                  "evidence_start": 0,
                  "evidence_end": 20,
                  "explanation": "段落只给门外压力，不揭示来人真实目的，形成延迟回答。"
                }
              ]
            }
            """,
            ReferenceCorpusFeatureFamilies.Commercial,
            "passage",
            sourceTextLength: 48);

        Assert.Equal(ReferenceCorpusFeatureFamilyValidationStatuses.Passed, result.Status);
        var observation = Assert.Single(result.AcceptedObservations);
        Assert.Equal("commercial", observation.FeatureFamily);
        Assert.Equal("mechanics", observation.FeatureKey);
        Assert.Equal("question", observation.ValueText);
        Assert.Equal("enum", observation.ValueKind);
        Assert.Contains("\"expectation_management\":\"delay\"", observation.ValueJson);
    }

    [Fact]
    public void ValidateAcceptsEmptyObservationsWithoutForcingHallucinatedLabels()
    {
        var result = ReferenceCorpusFeatureFamilyOutputValidator.Validate(
            """
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "rhetoric",
              "node_type": "sentence",
              "observations": []
            }
            """,
            ReferenceCorpusFeatureFamilies.Rhetoric,
            "sentence",
            sourceTextLength: 24);

        Assert.Equal(ReferenceCorpusFeatureFamilyValidationStatuses.Passed, result.Status);
        Assert.Empty(result.AcceptedObservations);
        Assert.Empty(result.RejectedObservations);
        Assert.Contains(result.Diagnostics, item => item.Contains("no grounded observations", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateMapsRhythmNumericValuesByFeatureKey()
    {
        var result = ReferenceCorpusFeatureFamilyOutputValidator.Validate(
            """
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "rhythm",
              "node_type": "sentence",
              "observations": [
                {
                  "feature_key": "pause_density",
                  "label": "staccato",
                  "char_count": 18,
                  "cadence": "staccato",
                  "pause_density": 0.42,
                  "confidence": 0.81,
                  "evidence_start": 0,
                  "evidence_end": 12,
                  "explanation": "短停顿密集，形成顿挫。"
                }
              ]
            }
            """,
            ReferenceCorpusFeatureFamilies.Rhythm,
            "sentence",
            sourceTextLength: 24);

        Assert.Equal(ReferenceCorpusFeatureFamilyValidationStatuses.Passed, result.Status);
        var observation = Assert.Single(result.AcceptedObservations);
        Assert.Equal("pause_density", observation.FeatureKey);
        Assert.Equal("number", observation.ValueKind);
        Assert.Equal(0.42, observation.ValueNum);
        Assert.Equal("staccato", observation.ValueText);
        Assert.Contains("\"char_count\":18", observation.ValueJson);
    }

    [Fact]
    public void ValidateRejectsUnsupportedEnumsUnexpectedPropertiesAndUngroundedEvidence()
    {
        var result = ReferenceCorpusFeatureFamilyOutputValidator.Validate(
            """
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "emotion",
              "node_type": "sentence",
              "observations": [
                {
                  "feature_key": "emotion_state",
                  "surface": "world-weary brilliance",
                  "subtext": "restrained",
                  "direction": "stable",
                  "mode": "suppressed",
                  "intensity": 4,
                  "confidence": 0.7,
                  "evidence_start": 0,
                  "evidence_end": 6,
                  "explanation": "unsupported enum should be rejected"
                },
                {
                  "feature_key": "emotion_state",
                  "surface": "calm",
                  "subtext": "restrained",
                  "direction": "stable",
                  "mode": "suppressed",
                  "intensity": 4,
                  "confidence": 0.7,
                  "evidence_start": 0,
                  "evidence_end": 99,
                  "freeform_reasoning": "LLM tried to add an uncontracted field",
                  "explanation": "unexpected property should be rejected"
                }
              ]
            }
            """,
            ReferenceCorpusFeatureFamilies.Emotion,
            "sentence",
            sourceTextLength: 12);

        Assert.Equal(ReferenceCorpusFeatureFamilyValidationStatuses.Rejected, result.Status);
        Assert.Empty(result.AcceptedObservations);
        Assert.Equal(2, result.RejectedObservations.Count);
        Assert.Contains(result.RejectedObservations, item => item.Reason.Contains("unsupported enum", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.RejectedObservations, item => item.Reason.Contains("unsupported property", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.RejectedObservations, item => item.Reason.Contains("world-weary brilliance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateRejectsEvidenceOffsetsOutsideCurrentSourceNode()
    {
        var result = ReferenceCorpusFeatureFamilyOutputValidator.Validate(
            """
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "emotion",
              "node_type": "sentence",
              "observations": [
                {
                  "feature_key": "emotion_state",
                  "surface": "calm",
                  "subtext": "restrained",
                  "direction": "stable",
                  "mode": "suppressed",
                  "intensity": 4,
                  "confidence": 0.7,
                  "evidence_start": 0,
                  "evidence_end": 99,
                  "explanation": "offset points outside the current node text."
                }
              ]
            }
            """,
            ReferenceCorpusFeatureFamilies.Emotion,
            "sentence",
            sourceTextLength: 12);

        Assert.Equal(ReferenceCorpusFeatureFamilyValidationStatuses.Rejected, result.Status);
        var rejected = Assert.Single(result.RejectedObservations);
        Assert.Contains("Evidence offsets must be inside the source node", rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRejectsRootSchemaMismatchesBeforeAcceptingObservations()
    {
        var result = ReferenceCorpusFeatureFamilyOutputValidator.Validate(
            """
            {
              "schema_version": "reference-corpus-feature-family-v1",
              "family": "syntax",
              "node_type": "passage",
              "observations": []
            }
            """,
            ReferenceCorpusFeatureFamilies.Syntax,
            "sentence",
            sourceTextLength: 12);

        Assert.Equal(ReferenceCorpusFeatureFamilyValidationStatuses.InvalidSchema, result.Status);
        Assert.Contains(result.Diagnostics, item => item.Contains("node_type", StringComparison.OrdinalIgnoreCase));
    }
}
