using System.Globalization;

namespace Novelist.Core.App;

public sealed record StyleSkillDocument(
    string Name,
    string Description,
    string Category,
    string Mode,
    string Author,
    int Version,
    string Body)
{
    public static StyleSkillDocument ParseStrict(string raw)
    {
        var normalized = (raw ?? string.Empty).Trim().Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new StyleSkillValidationException("Skill markdown must start with YAML frontmatter.");
        }

        var endIndex = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (endIndex < 0)
        {
            throw new StyleSkillValidationException("Skill markdown frontmatter is not closed.");
        }

        var frontmatter = normalized[4..endIndex];
        var body = normalized[(endIndex + "\n---".Length)..].Trim();
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new StyleSkillValidationException("Skill markdown body is required.");
        }

        var values = ParseFrontmatter(frontmatter);
        var missing = RequiredFields
            .Where(field => !values.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new StyleSkillValidationException($"Missing required frontmatter fields: {string.Join(", ", missing)}.");
        }

        var name = NormalizeSkillName(Required(values, "name"));
        var description = NormalizeFrontmatterValue(Required(values, "description"), "description");
        var category = NormalizeFrontmatterValue(Required(values, "category"), "category");
        var mode = NormalizeFrontmatterValue(Required(values, "mode"), "mode");
        if (mode is not ("auto" or "manual" or "always"))
        {
            throw new StyleSkillValidationException("Frontmatter field 'mode' must be auto, manual, or always.");
        }

        var author = NormalizeFrontmatterValue(Required(values, "author"), "author");
        if (!int.TryParse(Required(values, "version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ||
            version <= 0)
        {
            throw new StyleSkillValidationException("Frontmatter field 'version' must be a positive integer.");
        }

        return new StyleSkillDocument(name, description, category, mode, author, version, body);
    }

    public static string NormalizeSkillName(string name)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Skill name is required.", nameof(name));
        }

        if (normalized.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(name), normalized.Length, "Skill name must be at most 128 characters.");
        }

        if (normalized.Any(ch => char.IsControl(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            throw new ArgumentException("Skill name contains unsupported path characters.", nameof(name));
        }

        return normalized;
    }

    private static readonly string[] RequiredFields =
    [
        "name",
        "description",
        "category",
        "mode",
        "author",
        "version"
    ];

    private static string Required(Dictionary<string, string> values, string key)
    {
        return values[key];
    }

    private static Dictionary<string, string> ParseFrontmatter(string frontmatter)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new StyleSkillValidationException($"Invalid frontmatter line: {line}");
            }

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            values[key] = value;
        }

        return values;
    }

    private static string NormalizeFrontmatterValue(string value, string name)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new StyleSkillValidationException($"Frontmatter field '{name}' is required.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new StyleSkillValidationException($"Frontmatter field '{name}' must be a single-line value.");
        }

        return normalized;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}

public sealed class StyleSkillValidationException : Exception
{
    public StyleSkillValidationException(string message)
        : base(message)
    {
    }
}
