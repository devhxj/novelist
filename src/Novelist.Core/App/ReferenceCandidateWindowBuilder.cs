using System.Security.Cryptography;
using System.Text;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public sealed class ReferenceCandidateWindowBuilder
{
    private static readonly string[] DialogueMarkers = ["“", "”", "「", "」", "『", "』", "\""];
    private static readonly string[] EmotionMarkers = ["沉默", "没有回答", "攥", "发紧", "发凉", "发涩", "发颤", "避开", "咽下", "欲言又止"];
    private static readonly string[] HookMarkers = ["忽然", "突然", "敲门", "第三次", "威胁", "？", "?"];
    private static readonly string[] PayoffMarkers = ["终于", "真相", "答案", "原来", "揭开", "兑现"];
    private static readonly string[] TransitionMarkers = ["然后", "后来", "与此同时", "片刻", "很快", "直到"];
    private static readonly string[] ActionMarkers = ["把", "推", "拉", "走", "停", "转", "拿", "放", "扣", "握"];

    public IReadOnlyList<ReferenceMaterialCandidateWindow> Build(ReferenceCandidateChapterInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInput(input);
        var boundedNodes = input.Nodes
            .Where(node => node.StartOffset >= input.ContentStart && node.EndOffset <= input.ContentEnd)
            .OrderBy(node => node.StartOffset)
            .ThenBy(node => node.EndOffset)
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .ToArray();
        var candidates = new List<ReferenceMaterialCandidateWindow>();

        foreach (var paragraph in boundedNodes.Where(node => string.Equals(node.NodeType, "paragraph", StringComparison.Ordinal)))
        {
            AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.Passage, [paragraph]);
            if (ContainsAny(paragraph.Text, DialogueMarkers))
            {
                AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.DialogueExchange, [paragraph]);
            }

            if (ContainsAny(paragraph.Text, EmotionMarkers))
            {
                AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.Emotion, [paragraph]);
            }

            if (ContainsAny(paragraph.Text, HookMarkers))
            {
                AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.Hook, [paragraph]);
            }

            if (ContainsAny(paragraph.Text, PayoffMarkers))
            {
                AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.Payoff, [paragraph]);
            }

            if (ContainsAny(paragraph.Text, TransitionMarkers))
            {
                AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.Transition, [paragraph]);
            }

            if (ContainsAny(paragraph.Text, ActionMarkers) && ContainsAny(paragraph.Text, EmotionMarkers))
            {
                AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.ActionReaction, [paragraph]);
            }
        }

        foreach (var sentence in boundedNodes.Where(node => string.Equals(node.NodeType, "sentence", StringComparison.Ordinal)))
        {
            if (NormalizedCharacterCount(sentence.Text) >= 8 && !IsAcknowledgementOnly(sentence.Text))
            {
                AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.QualifiedSentence, [sentence]);
            }
        }

        return candidates
            .OrderBy(candidate => candidate.SourceNodes[0].StartOffset)
            .ThenBy(candidate => candidate.SourceNodes[0].EndOffset)
            .ThenBy(candidate => candidate.CandidateType, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddCandidate(
        List<ReferenceMaterialCandidateWindow> candidates,
        ReferenceCandidateChapterInput chapter,
        string candidateType,
        IReadOnlyList<ReferenceCandidateSourceNode> nodes)
    {
        var candidateKey = BuildCandidateKey(chapter.AnchorId, chapter.ChapterIndex, candidateType, nodes);
        if (candidates.Any(candidate => string.Equals(candidate.CandidateKey, candidateKey, StringComparison.Ordinal)))
        {
            return;
        }

        candidates.Add(new ReferenceMaterialCandidateWindow(
            candidateKey,
            chapter.ChapterIndex,
            candidateType,
            HashText(string.Join("\n", nodes.Select(node => node.Text))),
            nodes));
    }

    private static string BuildCandidateKey(
        long anchorId,
        int chapterIndex,
        string candidateType,
        IReadOnlyList<ReferenceCandidateSourceNode> nodes)
    {
        var payload = new StringBuilder();
        payload.Append(anchorId).Append('|').Append(chapterIndex).Append('|').Append(candidateType);
        foreach (var node in nodes)
        {
            payload.Append('|').Append(node.NodeId)
                .Append(':').Append(node.StartOffset)
                .Append(':').Append(node.EndOffset)
                .Append(':').Append(node.TextHash);
        }

        return HashText(payload.ToString());
    }

    private static void ValidateInput(ReferenceCandidateChapterInput input)
    {
        if (input.AnchorId <= 0 || input.ChapterIndex <= 0 || input.ContentStart < 0 || input.ContentEnd <= input.ContentStart)
        {
            throw new ArgumentOutOfRangeException(nameof(input), "Candidate chapter bounds are invalid.");
        }

        foreach (var node in input.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId) ||
                string.IsNullOrWhiteSpace(node.NodeType) ||
                string.IsNullOrWhiteSpace(node.TextHash) ||
                node.StartOffset < 0 ||
                node.EndOffset <= node.StartOffset)
            {
                throw new ArgumentException("Candidate source node is invalid.", nameof(input));
            }
        }
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers)
    {
        return markers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsAcknowledgementOnly(string value)
    {
        var normalized = new string(value.Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character) && character is not '“' and not '”').ToArray());
        return normalized is "好" or "嗯" or "哦" or "是" or "行" or "知道";
    }

    private static int NormalizedCharacterCount(string value) =>
        value.Count(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character));

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record ReferenceCandidateChapterInput(
    long AnchorId,
    int ChapterIndex,
    int ContentStart,
    int ContentEnd,
    IReadOnlyList<ReferenceCandidateSourceNode> Nodes);

public sealed record ReferenceCandidateSourceNode(
    string NodeId,
    string NodeType,
    int StartOffset,
    int EndOffset,
    string Text,
    string TextHash);

public sealed record ReferenceMaterialCandidateWindow(
    string CandidateKey,
    int ChapterIndex,
    string CandidateType,
    string TextHash,
    IReadOnlyList<ReferenceCandidateSourceNode> SourceNodes);
