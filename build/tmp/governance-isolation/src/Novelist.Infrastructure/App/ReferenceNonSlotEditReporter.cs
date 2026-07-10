using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceNonSlotEditReporter
{
    private const int MaxDetailedTokens = 1200;
    private const int MaxReportedEdits = 20;

    public static IReadOnlyList<string> Report(
        string sourceText,
        string candidateText,
        IReadOnlyList<ReferenceSlotValuePayload>? changedSlots = null)
    {
        var expectedText = ApplyDeclaredSlots(sourceText ?? string.Empty, changedSlots);
        candidateText ??= string.Empty;
        if (string.Equals(expectedText, candidateText, StringComparison.Ordinal))
        {
            return [];
        }

        if (expectedText.Length > MaxDetailedTokens || candidateText.Length > MaxDetailedTokens)
        {
            return [BuildCommonSpanEdit(expectedText, candidateText)];
        }

        return BuildDetailedEdits(expectedText, candidateText);
    }

    private static string ApplyDeclaredSlots(
        string sourceText,
        IReadOnlyList<ReferenceSlotValuePayload>? changedSlots)
    {
        var expected = sourceText;
        if (changedSlots is null)
        {
            return expected;
        }

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

        return expected;
    }

    private static IReadOnlyList<string> BuildDetailedEdits(string expectedText, string candidateText)
    {
        var left = expectedText.ToCharArray();
        var right = candidateText.ToCharArray();
        var lcs = BuildLcsTable(left, right);
        var edits = new List<string>();
        var i = 0;
        var j = 0;

        while (i < left.Length || j < right.Length)
        {
            if (i < left.Length && j < right.Length && left[i] == right[j])
            {
                i++;
                j++;
                continue;
            }

            var startOffset = i;
            var removed = new List<char>();
            var inserted = new List<char>();
            while (i < left.Length || j < right.Length)
            {
                if (i < left.Length && j < right.Length && left[i] == right[j])
                {
                    break;
                }

                if (i < left.Length && (j >= right.Length || lcs[i + 1, j] >= lcs[i, j + 1]))
                {
                    removed.Add(left[i]);
                    i++;
                }
                else if (j < right.Length)
                {
                    inserted.Add(right[j]);
                    j++;
                }
            }

            edits.Add(DescribeEdit(startOffset, new string(removed.ToArray()), new string(inserted.ToArray())));
            if (edits.Count >= MaxReportedEdits)
            {
                edits.Add("Additional non-slot edits omitted after 20 reported edits.");
                break;
            }
        }

        return edits;
    }

    private static int[,] BuildLcsTable(char[] left, char[] right)
    {
        var table = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                table[i, j] = left[i] == right[j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        return table;
    }

    private static string BuildCommonSpanEdit(string expectedText, string candidateText)
    {
        var prefixLength = CommonPrefixLength(expectedText, candidateText);
        var suffixLength = CommonSuffixLength(expectedText, candidateText, prefixLength);
        var removed = expectedText.Substring(prefixLength, expectedText.Length - prefixLength - suffixLength);
        var inserted = candidateText.Substring(prefixLength, candidateText.Length - prefixLength - suffixLength);
        return DescribeEdit(prefixLength, removed, inserted);
    }

    private static int CommonPrefixLength(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < length && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private static int CommonSuffixLength(string left, string right, int prefixLength)
    {
        var leftIndex = left.Length - 1;
        var rightIndex = right.Length - 1;
        var suffixLength = 0;
        while (leftIndex >= prefixLength &&
            rightIndex >= prefixLength &&
            left[leftIndex] == right[rightIndex])
        {
            suffixLength++;
            leftIndex--;
            rightIndex--;
        }

        return suffixLength;
    }

    private static string DescribeEdit(int offset, string removed, string inserted)
    {
        if (removed.Length == 0)
        {
            return $"Inserted non-slot text '{Display(inserted)}' at offset {offset}.";
        }

        if (inserted.Length == 0)
        {
            return $"Removed non-slot text '{Display(removed)}' at offset {offset}.";
        }

        return $"Replaced non-slot text '{Display(removed)}' with '{Display(inserted)}' at offset {offset}.";
    }

    private static string Display(string value)
    {
        return value
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }
}
