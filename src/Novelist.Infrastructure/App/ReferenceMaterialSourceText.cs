using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceMaterialSourceText
{
    public static bool TryResolve(
        string chapterText,
        ReferenceMaterialSourceSpan? span,
        out string text)
    {
        text = string.Empty;
        if (string.IsNullOrEmpty(chapterText) ||
            span is null ||
            span.StartLine <= 0 ||
            span.EndLine < span.StartLine)
        {
            return false;
        }

        var lines = ReadLines(chapterText);
        if (span.EndLine > lines.Count)
        {
            return false;
        }

        var first = lines[span.StartLine - 1];
        var last = lines[span.EndLine - 1];
        var start = first.Start;
        var end = last.End;
        while (start < first.End && char.IsWhiteSpace(chapterText[start]))
        {
            start++;
        }

        while (end > last.Start && char.IsWhiteSpace(chapterText[end - 1]))
        {
            end--;
        }

        if (start == first.End || end == last.Start || start >= end)
        {
            return false;
        }

        text = chapterText[start..end];
        return true;
    }

    private static IReadOnlyList<Line> ReadLines(string value)
    {
        var lines = new List<Line>();
        var start = 0;
        while (true)
        {
            var newline = value.IndexOf('\n', start);
            var end = newline < 0 ? value.Length : newline;
            lines.Add(new Line(start, end));
            if (newline < 0)
            {
                return lines;
            }

            start = newline + 1;
        }
    }

    private sealed record Line(int Start, int End);
}
