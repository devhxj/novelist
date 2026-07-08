using System.Globalization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal static class GitHistoryParser
{
    public static IReadOnlyList<GitCommitMetadata> ParseCommitMetadataLog(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var commits = new List<GitCommitMetadata>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\0', 6);
            if (parts.Length != 6 ||
                !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                continue;
            }

            commits.Add(new GitCommitMetadata(
                parts[0],
                parts[1],
                parts[2],
                parts[3],
                parts[5].Split('\n', 2)[0],
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
        }

        return commits;
    }

    public static IReadOnlyList<GitCommitFilePayload> ParseCommitFiles(string nameStatusZ, string numstatZ)
    {
        var nameStatus = ParseNameStatus(nameStatusZ);
        var stats = ParseNumstat(numstatZ)
            .GroupBy(item => item.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var files = new List<GitCommitFilePayload>();
        foreach (var entry in nameStatus)
        {
            stats.TryGetValue(entry.Path, out var stat);
            files.Add(new GitCommitFilePayload(
                entry.Path,
                entry.OldPath,
                entry.ChangeType,
                stat?.Additions ?? 0,
                stat?.Deletions ?? 0,
                stat?.Binary ?? false));
        }

        foreach (var stat in stats.Values.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            if (nameStatus.Any(item => string.Equals(item.Path, stat.Path, StringComparison.Ordinal)))
            {
                continue;
            }

            files.Add(new GitCommitFilePayload(
                stat.Path,
                stat.OldPath,
                "modified",
                stat.Additions,
                stat.Deletions,
                stat.Binary));
        }

        return files
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<VersionControlCommitInfo> ParseSimpleLog(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var commits = new List<VersionControlCommitInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\0', 3);
            if (parts.Length != 3 ||
                !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                continue;
            }

            commits.Add(new VersionControlCommitInfo(
                parts[0],
                parts[1].Split('\n', 2)[0],
                DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
        }

        return commits;
    }

    private static IReadOnlyList<NameStatusEntry> ParseNameStatus(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var tokens = SplitZ(output);
        var entries = new List<NameStatusEntry>();
        for (var index = 0; index < tokens.Count;)
        {
            var status = tokens[index++];
            if (string.IsNullOrWhiteSpace(status))
            {
                continue;
            }

            if (status.StartsWith('R') || status.StartsWith('C'))
            {
                if (index + 1 >= tokens.Count)
                {
                    break;
                }

                var oldPath = tokens[index++];
                var path = tokens[index++];
                if (IsSafeGitPath(path) && IsSafeGitPath(oldPath))
                {
                    entries.Add(new NameStatusEntry(path, oldPath, "renamed"));
                }

                continue;
            }

            if (index >= tokens.Count)
            {
                break;
            }

            var currentPath = tokens[index++];
            if (!IsSafeGitPath(currentPath))
            {
                continue;
            }

            entries.Add(new NameStatusEntry(currentPath, null, status[0] switch
            {
                'A' => "added",
                'D' => "deleted",
                'M' => "modified",
                'T' => "modified",
                _ => "modified"
            }));
        }

        return entries;
    }

    private static IReadOnlyList<NumstatEntry> ParseNumstat(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var tokens = SplitZ(output);
        var entries = new List<NumstatEntry>();
        for (var index = 0; index < tokens.Count;)
        {
            var header = tokens[index++];
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var parts = header.Split('\t', 3);
            if (parts.Length != 3)
            {
                continue;
            }

            var binary = parts[0] == "-" || parts[1] == "-";
            var additions = binary ? 0 : ParseNonNegativeInt(parts[0]);
            var deletions = binary ? 0 : ParseNonNegativeInt(parts[1]);
            string? oldPath = null;
            var path = parts[2];
            if (string.IsNullOrEmpty(path))
            {
                if (index + 1 >= tokens.Count)
                {
                    break;
                }

                oldPath = tokens[index++];
                path = tokens[index++];
            }

            if (IsSafeGitPath(path) && (oldPath is null || IsSafeGitPath(oldPath)))
            {
                entries.Add(new NumstatEntry(path, oldPath, additions, deletions, binary));
            }
        }

        return entries;
    }

    private static int ParseNonNegativeInt(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
    }

    private static IReadOnlyList<string> SplitZ(string output)
    {
        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.TrimEnd('\r', '\n'))
            .Where(item => item.Length > 0)
            .ToArray();
    }

    private static bool IsSafeGitPath(string path)
    {
        return path.Length is > 0 and <= 512 &&
            !path.Contains('\0') &&
            !path.StartsWith("/", StringComparison.Ordinal) &&
            !path.Contains(':', StringComparison.Ordinal) &&
            path.Split('/').All(segment => segment is not "" and not "." and not "..");
    }

    private sealed record NameStatusEntry(string Path, string? OldPath, string ChangeType);

    private sealed record NumstatEntry(string Path, string? OldPath, int Additions, int Deletions, bool Binary);
}

internal sealed record GitCommitMetadata(
    string CommitId,
    string ShortCommitId,
    string AuthorName,
    string AuthorEmail,
    string Message,
    DateTimeOffset CommittedAt);
