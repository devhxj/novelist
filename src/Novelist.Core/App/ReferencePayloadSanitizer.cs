using System.Text.RegularExpressions;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class ReferencePayloadSanitizer
{
    private const int MetadataMaxChars = 256;

    private static readonly Regex SensitiveFieldAssignmentPattern = new(
        @"(?<![\w-])[""']?(source_path|source_text|candidate_text|prompt)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n;,}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SecretAssignmentPattern = new(
        @"(?<![\w-])[""']?(api[_-]?key|token|secret|authorization|password|credential)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s;,}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BearerTokenPattern = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]{12,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-(?:proj-)?[A-Za-z0-9_-]{16,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WindowsPathPattern = new(
        @"\b[A-Z]:[\\/][^\s;,""'<>|]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UncPathPattern = new(
        @"\\\\[^\\/\s;,""'<>|]+[\\/][^\s;,""'<>|]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FileUriPattern = new(
        @"\bfile://[^\s;""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UnixPathPattern = new(
        @"(?<!\w)/(?:Users|home|private|mnt|Volumes|var/folders|tmp)/[^\s;""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FullTextSentinelPattern = new(
        @"__[A-Z0-9_]*(?:FULL|SOURCE|MATERIAL|CHAPTER|CANDIDATE|PROMPT)[A-Z0-9_]*__",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string RedactAndBoundText(string? value, int maxChars)
    {
        var redacted = RedactSensitiveText(value);
        return redacted.Length <= maxChars
            ? redacted
            : redacted[..maxChars].TrimEnd() + "...";
    }

    public static ReferenceAnchorPayload SanitizeAnchor(ReferenceAnchorPayload anchor) =>
        anchor with
        {
            Title = RedactSensitiveText(anchor.Title),
            Author = RedactSensitiveText(anchor.Author),
            SourcePath = string.Empty,
            SourceKind = RedactSensitiveText(anchor.SourceKind),
            LicenseStatus = RedactSensitiveText(anchor.LicenseStatus),
            SourceFileHash = RedactSensitiveText(anchor.SourceFileHash),
            BuildVersion = RedactSensitiveText(anchor.BuildVersion),
            Status = RedactSensitiveText(anchor.Status),
            Visibility = RedactSensitiveText(anchor.Visibility),
            SourceTrust = RedactSensitiveText(anchor.SourceTrust),
            UserTags = anchor.UserTags
                .Select(tag => RedactAndBoundText(tag, MetadataMaxChars))
                .ToArray(),
            OwnerScope = RedactSensitiveText(anchor.OwnerScope)
        };

    private static string RedactSensitiveText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = SensitiveFieldAssignmentPattern.Replace(value, "[redacted_field]");
        redacted = SecretAssignmentPattern.Replace(redacted, "[redacted_secret]");
        redacted = BearerTokenPattern.Replace(redacted, "Bearer [redacted_secret]");
        redacted = ApiKeyPattern.Replace(redacted, "[redacted_secret]");
        redacted = WindowsPathPattern.Replace(redacted, "[redacted_path]");
        redacted = UncPathPattern.Replace(redacted, "[redacted_path]");
        redacted = FileUriPattern.Replace(redacted, "[redacted_path]");
        redacted = UnixPathPattern.Replace(redacted, "[redacted_path]");
        return FullTextSentinelPattern.Replace(redacted, "[redacted_text]");
    }
}
