using System.Text;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceRewriteLevelClassifier
{
    public static string Classify(
        string sourceText,
        string candidateText,
        IReadOnlyList<ReferenceSlotValuePayload>? changedSlots = null)
    {
        sourceText ??= string.Empty;
        candidateText ??= string.Empty;

        if (string.Equals(sourceText, candidateText, StringComparison.Ordinal))
        {
            return ReferenceRewriteLevels.L0;
        }

        if (IsDeclaredSlotOnlyReplacement(sourceText, candidateText, changedSlots))
        {
            return ReferenceRewriteLevels.L1;
        }

        if (NormalizeForSimilarity(sourceText) == NormalizeForSimilarity(candidateText))
        {
            return ReferenceRewriteLevels.L2;
        }

        var similarity = CharacterJaccard(sourceText, candidateText);
        if (similarity >= 0.88)
        {
            return ReferenceRewriteLevels.L2;
        }

        return similarity >= 0.35 ? ReferenceRewriteLevels.L3 : ReferenceRewriteLevels.L4;
    }

    private static bool IsDeclaredSlotOnlyReplacement(
        string sourceText,
        string candidateText,
        IReadOnlyList<ReferenceSlotValuePayload>? changedSlots)
    {
        if (changedSlots is null || changedSlots.Count == 0)
        {
            return false;
        }

        var expected = sourceText;
        foreach (var slot in changedSlots)
        {
            var slotName = (slot.SlotName ?? string.Empty).Trim();
            if (slotName.Length == 0)
            {
                continue;
            }

            var value = slot.Value ?? string.Empty;
            expected = expected.Replace("{{" + slotName + "}}", value, StringComparison.Ordinal);
            expected = expected.Replace("{" + slotName + "}", value, StringComparison.Ordinal);
        }

        return string.Equals(expected, candidateText, StringComparison.Ordinal);
    }

    private static string NormalizeForSimilarity(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static double CharacterJaccard(string left, string right)
    {
        var leftSet = NormalizeForSimilarity(left).ToCharArray().ToHashSet();
        var rightSet = NormalizeForSimilarity(right).ToCharArray().ToHashSet();
        if (leftSet.Count == 0 && rightSet.Count == 0)
        {
            return 1;
        }

        var intersection = leftSet.Intersect(rightSet).Count();
        var union = leftSet.Union(rightSet).Count();
        return union == 0 ? 0 : intersection / (double)union;
    }
}
