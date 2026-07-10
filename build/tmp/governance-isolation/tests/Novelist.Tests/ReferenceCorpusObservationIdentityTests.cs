using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceCorpusObservationIdentityTests
{
    [Fact]
    public void CreateBuildsStableObservationIdFromGenerationKey()
    {
        var first = ReferenceCorpusObservationIdentity.Create(
            runId: "run-1",
            nodeId: "node-1",
            featureFamily: "sensory",
            featureKey: "profile",
            evidenceStart: 0,
            evidenceEnd: 8);
        var second = ReferenceCorpusObservationIdentity.Create(
            runId: "run-1",
            nodeId: "node-1",
            featureFamily: "sensory",
            featureKey: "profile",
            evidenceStart: 0,
            evidenceEnd: 8);

        Assert.Equal(first, second);
        Assert.StartsWith("obs_", first.ObservationId, StringComparison.Ordinal);
        Assert.Equal(68, first.ObservationId.Length);
    }

    [Fact]
    public void CreateNormalizesNullEvidenceBoundsToDatabaseSentinel()
    {
        var identity = ReferenceCorpusObservationIdentity.Create(
            runId: "run-1",
            nodeId: "node-1",
            featureFamily: "rhythm",
            featureKey: "sentence_rhythm",
            evidenceStart: null,
            evidenceEnd: null);

        Assert.Equal(-1, identity.NormalizedEvidenceStart);
        Assert.Equal(-1, identity.NormalizedEvidenceEnd);
    }

    [Fact]
    public void CreateKeepsArrayFamiliesAsOneObservationIdentity()
    {
        var identity = ReferenceCorpusObservationIdentity.Create(
            runId: "run-1",
            nodeId: "sentence-9",
            featureFamily: "sensory",
            featureKey: "senses",
            evidenceStart: null,
            evidenceEnd: null);

        Assert.Equal("run-1", identity.RunId);
        Assert.Equal("sentence-9", identity.NodeId);
        Assert.Equal("sensory", identity.FeatureFamily);
        Assert.Equal("senses", identity.FeatureKey);
    }

    [Fact]
    public void CreateChangesObservationIdWhenGenerationKeyChanges()
    {
        var baseIdentity = ReferenceCorpusObservationIdentity.Create("run-1", "node-1", "sensory", "senses", 0, 5);
        var differentSpan = ReferenceCorpusObservationIdentity.Create("run-1", "node-1", "sensory", "senses", 1, 5);
        var differentRun = ReferenceCorpusObservationIdentity.Create("run-2", "node-1", "sensory", "senses", 0, 5);

        Assert.NotEqual(baseIdentity.ObservationId, differentSpan.ObservationId);
        Assert.NotEqual(baseIdentity.ObservationId, differentRun.ObservationId);
    }

    [Theory]
    [InlineData("", "node-1", "sensory", "senses")]
    [InlineData("run-1", "", "sensory", "senses")]
    [InlineData("run-1", "node-1", "", "senses")]
    [InlineData("run-1", "node-1", "sensory", "")]
    public void CreateRejectsBlankGenerationKeyParts(
        string runId,
        string nodeId,
        string featureFamily,
        string featureKey)
    {
        Assert.Throws<ArgumentException>(() =>
            ReferenceCorpusObservationIdentity.Create(
                runId,
                nodeId,
                featureFamily,
                featureKey,
                evidenceStart: null,
                evidenceEnd: null));
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    [InlineData(8, 4)]
    public void CreateRejectsInvalidEvidenceBounds(int? evidenceStart, int? evidenceEnd)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReferenceCorpusObservationIdentity.Create(
                runId: "run-1",
                nodeId: "node-1",
                featureFamily: "sensory",
                featureKey: "senses",
                evidenceStart,
                evidenceEnd));
    }
}
