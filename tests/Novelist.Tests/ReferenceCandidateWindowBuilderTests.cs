using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceCandidateWindowBuilderTests
{
    [Fact]
    public void BuildCreatesDeterministicNonOverlappingWindowsWithoutCrossingConfirmedChapterBoundaries()
    {
        var builder = new ReferenceCandidateWindowBuilder();
        var chapterOne = new ReferenceCandidateChapterInput(
            AnchorId: 99,
            ChapterIndex: 1,
            ContentStart: 0,
            ContentEnd: 100,
            Nodes:
            [
                new ReferenceCandidateSourceNode("p-1", "paragraph", 0, 18, "“别开门。”她把钥匙攥进掌心。", "p-1-hash"),
                new ReferenceCandidateSourceNode("s-1", "sentence", 0, 6, "“别开门。”", "s-1-hash"),
                new ReferenceCandidateSourceNode("s-2", "sentence", 6, 18, "”她把钥匙攥进掌心。", "s-2-hash"),
                new ReferenceCandidateSourceNode("p-2", "paragraph", 20, 46, "门外响起第三次敲门，她仍没有回答。", "p-2-hash"),
                new ReferenceCandidateSourceNode("s-3", "sentence", 20, 46, "门外响起第三次敲门，她仍没有回答。", "s-3-hash"),
                new ReferenceCandidateSourceNode("p-foreign", "paragraph", 100, 120, "第二章内容不属于当前章节。", "foreign-hash")
            ]);

        var first = builder.Build(chapterOne);
        var second = builder.Build(chapterOne);

        Assert.NotEmpty(first);
        Assert.Equal(first.Select(candidate => candidate.CandidateKey), second.Select(candidate => candidate.CandidateKey));
        Assert.Contains(first, candidate => candidate.CandidateType == ReferenceMaterializationCandidateTypes.DialogueExchange);
        Assert.Contains(first, candidate => candidate.CandidateType == ReferenceMaterializationCandidateTypes.Hook);
        Assert.DoesNotContain(first, candidate =>
            candidate.SourceNodes.Any(node => node.NodeId == "p-1") &&
            candidate.CandidateType == ReferenceMaterializationCandidateTypes.Emotion);
        Assert.Equal(
            first.Select(candidate => string.Join("|", candidate.SourceNodes.Select(node => node.NodeId))).Distinct(StringComparer.Ordinal).Count(),
            first.Count);
        Assert.All(first, candidate =>
        {
            Assert.All(candidate.SourceNodes, node =>
            {
                Assert.InRange(node.StartOffset, chapterOne.ContentStart, chapterOne.ContentEnd - 1);
                Assert.InRange(node.EndOffset, chapterOne.ContentStart + 1, chapterOne.ContentEnd);
                Assert.NotEqual("p-foreign", node.NodeId);
            });
        });
    }

    [Fact]
    public void BuildDoesNotPromoteVeryShortAcknowledgementsAsStandaloneQualifiedSentences()
    {
        var builder = new ReferenceCandidateWindowBuilder();
        var chapter = new ReferenceCandidateChapterInput(
            AnchorId: 99,
            ChapterIndex: 1,
            ContentStart: 0,
            ContentEnd: 80,
            Nodes:
            [
                new ReferenceCandidateSourceNode("p-1", "paragraph", 0, 34, "“好。”她把门闩扣上，雨声压住了走廊。", "p-1-hash"),
                new ReferenceCandidateSourceNode("s-short", "sentence", 0, 4, "“好。”", "short-hash"),
                new ReferenceCandidateSourceNode("s-long", "sentence", 4, 34, "”她把门闩扣上，雨声压住了走廊。", "long-hash")
            ]);

        var candidates = builder.Build(chapter);

        Assert.DoesNotContain(candidates, candidate =>
            candidate.CandidateType == ReferenceMaterializationCandidateTypes.QualifiedSentence &&
            Assert.Single(candidate.SourceNodes).NodeId == "s-short");
        Assert.Contains(candidates, candidate =>
            candidate.CandidateType == ReferenceMaterializationCandidateTypes.DialogueExchange &&
            candidate.SourceNodes.Any(node => node.NodeId == "p-1"));
    }

    [Fact]
    public void BuildMergesContextDependentAcknowledgementsAndTransitionsWithTheirNeighbors()
    {
        var builder = new ReferenceCandidateWindowBuilder();
        var chapter = new ReferenceCandidateChapterInput(
            AnchorId: 99,
            ChapterIndex: 1,
            ContentStart: 0,
            ContentEnd: 80,
            Nodes:
            [
                new ReferenceCandidateSourceNode("p-dialogue", "paragraph", 0, 8, "“你早就知道？”", "p-dialogue-hash"),
                new ReferenceCandidateSourceNode("p-ack", "paragraph", 8, 10, "嗯。", "p-ack-hash"),
                new ReferenceCandidateSourceNode("p-evidence", "paragraph", 10, 24, "他把遗嘱推到她面前。", "p-evidence-hash"),
                new ReferenceCandidateSourceNode("p-transition", "paragraph", 26, 34, "三天后，雨停了。", "p-transition-hash"),
                new ReferenceCandidateSourceNode("p-aftermath", "paragraph", 34, 56, "她在门缝里看见父亲留下的钥匙。", "p-aftermath-hash")
            ]);

        var candidates = builder.Build(chapter);

        Assert.Collection(
            candidates,
            first =>
            {
                Assert.Equal(ReferenceMaterializationCandidateTypes.DialogueExchange, first.CandidateType);
                Assert.Equal(["p-dialogue", "p-ack", "p-evidence"], first.SourceNodes.Select(node => node.NodeId).ToArray());
            },
            second =>
            {
                Assert.Equal(ReferenceMaterializationCandidateTypes.Transition, second.CandidateType);
                Assert.Equal(["p-transition", "p-aftermath"], second.SourceNodes.Select(node => node.NodeId).ToArray());
            });
    }

    [Fact]
    public void BuildMergesAFragmentaryActionWithItsTriggerAndReaction()
    {
        var builder = new ReferenceCandidateWindowBuilder();
        var chapter = new ReferenceCandidateChapterInput(
            AnchorId: 99,
            ChapterIndex: 1,
            ContentStart: 0,
            ContentEnd: 48,
            Nodes:
            [
                new ReferenceCandidateSourceNode("p-trigger", "paragraph", 0, 9, "她等着他开口。", "p-trigger-hash"),
                new ReferenceCandidateSourceNode("p-action", "paragraph", 9, 13, "他点头。", "p-action-hash"),
                new ReferenceCandidateSourceNode("p-reaction", "paragraph", 13, 28, "她攥紧门闩，没有松手。", "p-reaction-hash")
            ]);

        var candidate = Assert.Single(builder.Build(chapter));

        Assert.Equal(ReferenceMaterializationCandidateTypes.ActionReaction, candidate.CandidateType);
        Assert.Equal(["p-trigger", "p-action", "p-reaction"], candidate.SourceNodes.Select(node => node.NodeId).ToArray());
    }

    [Fact]
    public void BuildMergesAnOrdinaryShortParagraphWithItsNarrativeContext()
    {
        var builder = new ReferenceCandidateWindowBuilder();
        var chapter = new ReferenceCandidateChapterInput(
            AnchorId: 99,
            ChapterIndex: 1,
            ContentStart: 0,
            ContentEnd: 64,
            Nodes:
            [
                new ReferenceCandidateSourceNode("p-trigger", "paragraph", 0, 18, "钥匙在锁孔里停了一下。", "p-trigger-hash"),
                new ReferenceCandidateSourceNode("p-event", "paragraph", 18, 23, "门开了。", "p-event-hash"),
                new ReferenceCandidateSourceNode("p-reaction", "paragraph", 23, 42, "她没有回头，只把灯按灭。", "p-reaction-hash")
            ]);

        var candidate = Assert.Single(builder.Build(chapter));

        Assert.Equal(["p-trigger", "p-event", "p-reaction"], candidate.SourceNodes.Select(node => node.NodeId).ToArray());
        Assert.NotEqual(ReferenceMaterializationCandidateDecisions.Rejected, candidate.InitialDecision);
    }

    [Fact]
    public void BuildSplitsAnOverlongParagraphIntoContiguousSentenceWindows()
    {
        var sentenceTexts = Enumerable.Range(1, 4)
            .Select(index => $"第{index}段" + new string('叙', 440) + "。")
            .ToArray();
        var offset = 0;
        var sentences = sentenceTexts
            .Select((text, index) =>
            {
                var node = new ReferenceCandidateSourceNode(
                    $"s-{index + 1}",
                    "sentence",
                    offset,
                    offset + text.Length,
                    text,
                    $"s-{index + 1}-hash");
                offset += text.Length;
                return node;
            })
            .ToArray();
        var paragraph = new ReferenceCandidateSourceNode(
            "p-long",
            "paragraph",
            0,
            offset,
            string.Concat(sentenceTexts),
            "p-long-hash");
        var builder = new ReferenceCandidateWindowBuilder();
        var chapter = new ReferenceCandidateChapterInput(
            AnchorId: 99,
            ChapterIndex: 1,
            ContentStart: 0,
            ContentEnd: offset,
            Nodes: [paragraph, .. sentences]);

        var candidates = builder.Build(chapter);

        Assert.Equal(2, candidates.Count);
        Assert.Collection(
            candidates,
            first => Assert.Equal(["s-1", "s-2"], first.SourceNodes.Select(node => node.NodeId).ToArray()),
            second => Assert.Equal(["s-3", "s-4"], second.SourceNodes.Select(node => node.NodeId).ToArray()));
        Assert.All(candidates, candidate =>
            Assert.True(string.Join("\n", candidate.SourceNodes.Select(node => node.Text)).Length <= 1_200));
    }

    [Fact]
    public void BuildRecordsAnIsolatedAcknowledgementAsADeterministicRejection()
    {
        var builder = new ReferenceCandidateWindowBuilder();
        var chapter = new ReferenceCandidateChapterInput(
            AnchorId: 99,
            ChapterIndex: 1,
            ContentStart: 0,
            ContentEnd: 8,
            Nodes:
            [new ReferenceCandidateSourceNode("p-ack", "paragraph", 0, 2, "嗯。", "p-ack-hash")]);

        var candidate = Assert.Single(builder.Build(chapter));

        Assert.Equal(ReferenceMaterializationCandidateDecisions.Rejected, candidate.InitialDecision);
        Assert.Equal("deterministic_triage", candidate.DecisionOrigin);
        Assert.Equal(["fragment"], candidate.ReasonCodes);
    }

    [Fact]
    public void BuildRetainsAStandaloneHighValueShortSentence()
    {
        var builder = new ReferenceCandidateWindowBuilder();
        var chapter = new ReferenceCandidateChapterInput(
            AnchorId: 99,
            ChapterIndex: 1,
            ContentStart: 0,
            ContentEnd: 10,
            Nodes:
            [
                new ReferenceCandidateSourceNode("p-warning", "paragraph", 0, 3, "别开。", "p-warning-hash"),
                new ReferenceCandidateSourceNode("s-warning", "sentence", 0, 3, "别开。", "s-warning-hash")
            ]);

        var candidate = Assert.Single(builder.Build(chapter));

        Assert.Equal(ReferenceMaterializationCandidateTypes.QualifiedSentence, candidate.CandidateType);
        Assert.Equal("p-warning", Assert.Single(candidate.SourceNodes).NodeId);
    }
}
