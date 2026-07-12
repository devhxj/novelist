using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceCandidateWindowBuilderTests
{
    [Fact]
    public void BuildCreatesDeterministicQualifiedWindowsWithoutCrossingConfirmedChapterBoundaries()
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
        Assert.Contains(first, candidate => candidate.CandidateType == ReferenceMaterializationCandidateTypes.Passage);
        Assert.Contains(first, candidate => candidate.CandidateType == ReferenceMaterializationCandidateTypes.DialogueExchange);
        Assert.Contains(first, candidate => candidate.CandidateType == ReferenceMaterializationCandidateTypes.Emotion);
        Assert.Contains(first, candidate => candidate.CandidateType == ReferenceMaterializationCandidateTypes.Hook);
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
            candidate.CandidateType == ReferenceMaterializationCandidateTypes.QualifiedSentence &&
            Assert.Single(candidate.SourceNodes).NodeId == "s-long");
        Assert.Contains(candidates, candidate =>
            candidate.CandidateType == ReferenceMaterializationCandidateTypes.Passage &&
            candidate.SourceNodes.Any(node => node.NodeId == "p-1"));
    }
}
