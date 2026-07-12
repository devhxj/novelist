using System.Security.Cryptography;
using System.Text;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public sealed class ReferenceCandidateWindowBuilder
{
    public const string Version = "candidate-window-v2";

    private static readonly string[] DialogueMarkers = ["“", "”", "「", "」", "『", "』", "\""];
    private static readonly string[] EmotionMarkers = ["沉默", "没有回答", "攥", "发紧", "发凉", "发涩", "发颤", "避开", "咽下", "欲言又止"];
    private static readonly string[] HookMarkers = ["忽然", "突然", "敲门", "第三次", "威胁", "？", "?"];
    private static readonly string[] PayoffMarkers = ["终于", "真相", "答案", "原来", "揭开", "兑现"];
    private static readonly string[] TransitionMarkers = ["然后", "后来", "与此同时", "片刻", "很快", "直到", "次日", "天后", "日后", "月后", "年后"];
    private static readonly string[] ActionMarkers = ["点头", "抬头", "转身", "看了", "笑了", "坐下", "起身", "把", "推", "拉", "走", "停", "转", "拿", "放", "扣", "握"];
    private static readonly string[] HighValueShortMarkers = ["别开", "不能", "真相", "原来", "死了", "活着", "回来", "钥匙"];
    private const int GenericActionMaximumCharacters = 8;

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
        var paragraphs = boundedNodes
            .Where(node => string.Equals(node.NodeType, "paragraph", StringComparison.Ordinal))
            .ToArray();
        if (paragraphs.Length > 0)
        {
            var mergedRanges = BuildContextDependentRanges(paragraphs);
            var coveredParagraphs = new bool[paragraphs.Length];
            foreach (var range in mergedRanges)
            {
                for (var index = range.Start; index <= range.End; index++)
                {
                    coveredParagraphs[index] = true;
                }

                var nodes = paragraphs[range.Start..(range.End + 1)];
                AddCandidate(
                    candidates,
                    input,
                    range.RuleRejectionCode is null ? SelectCandidateType(nodes) : ReferenceMaterializationCandidateTypes.QualifiedSentence,
                    nodes,
                    range.RuleRejectionCode is null ? ReferenceMaterializationCandidateDecisions.Pending : ReferenceMaterializationCandidateDecisions.Rejected,
                    range.RuleRejectionCode is null ? "candidate_window_builder" : "deterministic_triage",
                    range.RuleRejectionCode is null ? [] : [range.RuleRejectionCode]);
            }

            for (var index = 0; index < paragraphs.Length; index++)
            {
                if (!coveredParagraphs[index])
                {
                    AddCandidate(candidates, input, SelectCandidateType([paragraphs[index]]), [paragraphs[index]]);
                }
            }
        }
        else
        {
            foreach (var sentence in boundedNodes.Where(node => string.Equals(node.NodeType, "sentence", StringComparison.Ordinal)))
            {
                if (IsStandaloneValuable(sentence.Text) ||
                    (NormalizedCharacterCount(sentence.Text) >= 8 && !IsAcknowledgementOnly(sentence.Text)))
                {
                    AddCandidate(candidates, input, ReferenceMaterializationCandidateTypes.QualifiedSentence, [sentence]);
                }
            }
        }

        return candidates
            .OrderBy(candidate => candidate.SourceNodes[0].StartOffset)
            .ThenBy(candidate => candidate.SourceNodes[0].EndOffset)
            .ThenBy(candidate => candidate.CandidateType, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<ParagraphRange> BuildContextDependentRanges(
        IReadOnlyList<ReferenceCandidateSourceNode> paragraphs)
    {
        var ranges = new List<ParagraphRange>();
        for (var index = 0; index < paragraphs.Count; index++)
        {
            var kind = GetContextDependencyKind(paragraphs[index].Text);
            if (kind == ContextDependencyKind.None)
            {
                continue;
            }

            var range = kind == ContextDependencyKind.Transition
                ? new ParagraphRange(index, Math.Min(index + 1, paragraphs.Count - 1))
                : new ParagraphRange(Math.Max(index - 1, 0), Math.Min(index + 1, paragraphs.Count - 1));
            if (ranges.Count > 0 && range.Start <= ranges[^1].End)
            {
                ranges[^1] = new ParagraphRange(ranges[^1].Start, Math.Max(ranges[^1].End, range.End));
            }
            else
            {
                ranges.Add(range with
                {
                    RuleRejectionCode = range.Start == range.End ? RuleRejectionCode(kind) : null
                });
            }
        }

        return ranges;
    }

    private static string SelectCandidateType(IReadOnlyList<ReferenceCandidateSourceNode> nodes)
    {
        var text = string.Join("\n", nodes.Select(node => node.Text));
        if (ContainsAny(text, DialogueMarkers))
        {
            return ReferenceMaterializationCandidateTypes.DialogueExchange;
        }

        if (ContainsAny(text, ActionMarkers) && ContainsAny(text, EmotionMarkers))
        {
            return ReferenceMaterializationCandidateTypes.ActionReaction;
        }

        if (ContainsAny(text, HookMarkers))
        {
            return ReferenceMaterializationCandidateTypes.Hook;
        }

        if (ContainsAny(text, PayoffMarkers))
        {
            return ReferenceMaterializationCandidateTypes.Payoff;
        }

        if (ContainsAny(text, EmotionMarkers))
        {
            return ReferenceMaterializationCandidateTypes.Emotion;
        }

        if (ContainsAny(text, TransitionMarkers))
        {
            return ReferenceMaterializationCandidateTypes.Transition;
        }

        if (nodes.Count == 1 && IsStandaloneValuable(text))
        {
            return ReferenceMaterializationCandidateTypes.QualifiedSentence;
        }

        return ReferenceMaterializationCandidateTypes.Passage;
    }

    private static void AddCandidate(
        List<ReferenceMaterialCandidateWindow> candidates,
        ReferenceCandidateChapterInput chapter,
        string candidateType,
        IReadOnlyList<ReferenceCandidateSourceNode> nodes,
        string initialDecision = ReferenceMaterializationCandidateDecisions.Pending,
        string decisionOrigin = "candidate_window_builder",
        IReadOnlyList<string>? reasonCodes = null)
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
            nodes,
            initialDecision,
            decisionOrigin,
            reasonCodes ?? []));
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

    private static ContextDependencyKind GetContextDependencyKind(string value)
    {
        if (IsAcknowledgementOnly(value))
        {
            return ContextDependencyKind.Acknowledgement;
        }

        if (IsTransitionOnly(value))
        {
            return ContextDependencyKind.Transition;
        }

        return IsGenericActionOnly(value)
            ? ContextDependencyKind.GenericAction
            : ContextDependencyKind.None;
    }

    private static bool IsTransitionOnly(string value) =>
        NormalizedCharacterCount(value) <= 24 && ContainsAny(value, TransitionMarkers);

    private static bool IsGenericActionOnly(string value) =>
        NormalizedCharacterCount(value) <= GenericActionMaximumCharacters &&
        ContainsAny(value, ActionMarkers) &&
        !IsStandaloneValuable(value);

    private static bool IsStandaloneValuable(string value) =>
        ContainsAny(value, HighValueShortMarkers) ||
        (NormalizedCharacterCount(value) >= 8 &&
         (ContainsAny(value, HookMarkers) || ContainsAny(value, PayoffMarkers) || ContainsAny(value, EmotionMarkers)));

    private static string RuleRejectionCode(ContextDependencyKind kind) => kind switch
    {
        ContextDependencyKind.Acknowledgement => "fragment",
        ContextDependencyKind.GenericAction => "generic_action",
        ContextDependencyKind.Transition => "noise",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static int NormalizedCharacterCount(string value) =>
        value.Count(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character));

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private enum ContextDependencyKind
    {
        None,
        Acknowledgement,
        GenericAction,
        Transition
    }

    private readonly record struct ParagraphRange(int Start, int End, string? RuleRejectionCode = null);
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
    IReadOnlyList<ReferenceCandidateSourceNode> SourceNodes,
    string InitialDecision,
    string DecisionOrigin,
    IReadOnlyList<string> ReasonCodes);
