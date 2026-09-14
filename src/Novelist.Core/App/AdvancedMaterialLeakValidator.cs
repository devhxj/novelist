namespace Novelist.Core.App;

public sealed record AdvancedMaterialLeakViolation(string Field, string Run);

// 防直搬断言：高级素材的文本字段不得与来源原文共享超过阈值的连续片段。
// evidence 字段本身是"引用"，不在此校验范围内（它就该带原文）。
public static class AdvancedMaterialLeakValidator
{
    public const int MaxVerbatimRunLength = 8;

    public static IReadOnlyList<AdvancedMaterialLeakViolation> Validate(
        IReadOnlyDictionary<string, string?> fields,
        IReadOnlyList<string> sourceTexts,
        int maxRun = MaxVerbatimRunLength)
    {
        var violations = new List<AdvancedMaterialLeakViolation>();
        if (maxRun <= 0 || sourceTexts.Count == 0)
        {
            return violations;
        }

        var normalizedSources = sourceTexts
            .Select(Normalize)
            .Where(text => text.Length >= maxRun)
            .ToArray();
        if (normalizedSources.Length == 0)
        {
            return violations;
        }

        foreach (var (field, rawValue) in fields)
        {
            var value = Normalize(rawValue);
            if (value.Length < maxRun)
            {
                continue;
            }

            foreach (var source in normalizedSources)
            {
                var run = FindSharedRun(value, source, maxRun);
                if (run is not null)
                {
                    violations.Add(new AdvancedMaterialLeakViolation(field, run));
                    break;
                }
            }
        }

        return violations;
    }

    private static string? FindSharedRun(string candidate, string source, int minRun)
    {
        for (var index = 0; index + minRun <= source.Length; index++)
        {
            var window = source.Substring(index, minRun);
            if (candidate.Contains(window, StringComparison.Ordinal))
            {
                return window;
            }
        }

        return null;
    }

    private static string Normalize(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : new string(value.Where(character => !char.IsWhiteSpace(character)).ToArray());
}
