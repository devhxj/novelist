using System.Globalization;
using System.Reflection;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class SkillDocuments
{
    private const int MaxSkillNameLength = 128;

    public static IReadOnlyList<ParsedSkillDocument> LoadBuiltin()
    {
        var assembly = typeof(SkillDocuments).Assembly;
        var prefix = $"{assembly.GetName().Name}.BuiltinSkills.";

        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) &&
                name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Builtin skill resource '{name}' was not found.");
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd(), "builtin");
            })
            .ToArray();
    }

    public static IReadOnlyList<ParsedSkillDocument> ScanDirectory(string directory, string defaultAuthor)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<ParsedSkillDocument>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var raw = File.ReadAllText(path);
            if (TryParse(raw, defaultAuthor, out var skill))
            {
                result.Add(skill);
            }
        }

        return result;
    }

    public static ParsedSkillDocument Parse(string rawContent, string defaultAuthor)
    {
        if (!TryParse(rawContent, defaultAuthor, out var skill))
        {
            throw new ArgumentException("Skill document must contain valid frontmatter with a non-empty name.");
        }

        return skill;
    }

    public static bool TryParse(string rawContent, string defaultAuthor, out ParsedSkillDocument skill)
    {
        skill = default!;
        rawContent ??= string.Empty;
        var normalized = rawContent.Trim();
        var frontmatter = string.Empty;
        var body = normalized;

        if (normalized.StartsWith("---", StringComparison.Ordinal))
        {
            var withoutStart = normalized[3..].TrimStart('\r', '\n');
            var endIndex = withoutStart.IndexOf("\n---", StringComparison.Ordinal);
            if (endIndex < 0)
            {
                return false;
            }

            frontmatter = withoutStart[..endIndex];
            body = withoutStart[(endIndex + "\n---".Length)..].Trim();
        }

        var values = ParseFrontmatter(frontmatter);
        if (!values.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalizedName = NormalizeSkillName(name);
        var author = values.GetValueOrDefault("author");
        var versionText = values.GetValueOrDefault("version");
        var version = int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion)
            ? parsedVersion
            : 0;

        skill = new ParsedSkillDocument(
            normalizedName,
            values.GetValueOrDefault("description") ?? string.Empty,
            values.GetValueOrDefault("category") ?? string.Empty,
            NormalizeMode(values.GetValueOrDefault("mode")),
            string.IsNullOrWhiteSpace(author) ? defaultAuthor : author.Trim(),
            Math.Max(0, version),
            body,
            rawContent);
        return true;
    }

    public static string NormalizeSkillName(string name)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Skill name is required.", nameof(name));
        }

        if (normalized.Length > MaxSkillNameLength)
        {
            throw new ArgumentOutOfRangeException(nameof(name), normalized.Length, $"Skill name must be at most {MaxSkillNameLength} characters.");
        }

        if (normalized.Any(ch => char.IsControl(ch) || ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            throw new ArgumentException("Skill name contains unsupported path characters.", nameof(name));
        }

        return normalized;
    }

    private static Dictionary<string, string> ParseFrontmatter(string frontmatter)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in frontmatter.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            values[key] = Unquote(value);
        }

        return values;
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

    private static string NormalizeMode(string? mode)
    {
        return mode switch
        {
            "manual" => "manual",
            "always" => "always",
            "auto" or "on_demand" or null or "" => "auto",
            _ => "auto"
        };
    }
}

internal sealed record ParsedSkillDocument(
    string Name,
    string Description,
    string Category,
    string Mode,
    string Author,
    int Version,
    string Content,
    string RawContent)
{
    public SkillMetaPayload ToMeta(string source)
    {
        return new SkillMetaPayload(Name, Description, Category, Mode, Author, Version, source);
    }
}
