using Novelist.Contracts.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterialSlotDetectorTests
{
    [Fact]
    public void DetectsSingleAndDoubleBraceSlotsWithOffsets()
    {
        var material = Material("他握住{{object}}，把{place}的门关上。");

        var slots = ReferenceMaterialSlotDetector.Detect(material);

        Assert.Collection(
            slots,
            slot =>
            {
                Assert.Equal("material-1:slot:object:1", slot.SlotId);
                Assert.Equal("object", slot.SlotName);
                Assert.Equal("{{object}}", slot.Placeholder);
                Assert.Equal(3, slot.StartOffset);
                Assert.Equal(13, slot.EndOffset);
            },
            slot =>
            {
                Assert.Equal("material-1:slot:place:1", slot.SlotId);
                Assert.Equal("place", slot.SlotName);
                Assert.Equal("{place}", slot.Placeholder);
                Assert.Equal(15, slot.StartOffset);
                Assert.Equal(22, slot.EndOffset);
            });
    }

    [Fact]
    public void DetectsRepeatedSlotsWithStableOrdinals()
    {
        var material = Material("他把{object}递过去，又收回{{object}}。");

        var slots = ReferenceMaterialSlotDetector.Detect(material);

        Assert.Equal(
            ["material-1:slot:object:1", "material-1:slot:object:2"],
            slots.Select(slot => slot.SlotId));
    }

    [Fact]
    public void IgnoresMalformedSlotPlaceholders()
    {
        var material = Material("他握住{1bad}、{bad-name}和{{}}，只替换{valid_name}。");

        var slot = Assert.Single(ReferenceMaterialSlotDetector.Detect(material));

        Assert.Equal("valid_name", slot.SlotName);
        Assert.Equal("{valid_name}", slot.Placeholder);
    }

    private static ReferenceMaterialPayload Material(string text)
    {
        return new ReferenceMaterialPayload(
            "material-1",
            AnchorId: 1,
            SourceSegmentId: "segment-1",
            MaterialType: ReferenceMaterialTypes.Sentence,
            FunctionTag: "narration",
            EmotionTag: "neutral",
            SceneTag: "scene",
            PovTag: "unknown",
            TechniqueTag: "plain",
            FunctionConfidence: 0.5,
            EmotionConfidence: 0.5,
            PovConfidence: 0.5,
            text,
            SourceHash: "hash",
            ExtractorVersion: "test",
            UserVerified: false,
            DateTimeOffset.UnixEpoch);
    }
}
