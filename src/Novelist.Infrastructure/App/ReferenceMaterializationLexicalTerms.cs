namespace Novelist.Infrastructure.App;

internal static class ReferenceMaterializationLexicalTerms
{
    public static IReadOnlyList<string> Extract(string value, int maximumCount)
    {
        if (string.IsNullOrWhiteSpace(value) || maximumCount <= 0)
        {
            return [];
        }

        var terms = new HashSet<string>(StringComparer.Ordinal);
        var span = value.AsSpan();
        for (var index = 0; index < span.Length && terms.Count < maximumCount;)
        {
            if (IsCjk(span[index]))
            {
                var start = index;
                while (index < span.Length && IsCjk(span[index]))
                {
                    index++;
                }

                var run = span[start..index];
                if (run.Length >= 2)
                {
                    for (var offset = 0; offset < run.Length - 1 && terms.Count < maximumCount; offset++)
                    {
                        terms.Add(run.Slice(offset, 2).ToString());
                    }
                }
                else
                {
                    terms.Add(run.ToString());
                }

                continue;
            }

            if (char.IsLetterOrDigit(span[index]) || span[index] == '_')
            {
                var start = index;
                while (index < span.Length && (char.IsLetterOrDigit(span[index]) || span[index] == '_') && !IsCjk(span[index]))
                {
                    index++;
                }

                terms.Add(span[start..index].ToString().ToLowerInvariant());
                continue;
            }

            index++;
        }

        return terms.ToArray();
    }

    public static string BuildIndexText(string value) => string.Join(' ', Extract(value, 1_024));

    private static bool IsCjk(char value) => value is >= '\u3400' and <= '\u9fff' || value is >= '\uf900' and <= '\ufaff';
}
