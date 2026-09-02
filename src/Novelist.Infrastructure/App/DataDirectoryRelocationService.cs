using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novelist.Infrastructure.App;

public interface IDataDirectoryRelocationService
{
    ValueTask<DataDirectoryRelocationResult> RelocateAsync(
        string sourceDataDirectory,
        string targetDataDirectory,
        CancellationToken cancellationToken);
}

public sealed record DataDirectoryRelocationResult(
    int CopiedFiles,
    int SkippedFiles,
    int WarningCount,
    string ManifestPath);

/// <summary>
/// 数据目录 copy-first 搬迁（U13）：先把源目录完整复制到目标并写清单，
/// 复制完成后调用方才允许重指 config。源目录在任何路径下都不被修改；
/// 复制失败时目标留下 failed 清单，调用方保持原指针即可回到原状态。
/// </summary>
public sealed class DataDirectoryRelocationService : IDataDirectoryRelocationService
{
    public const string ManifestFileName = "relocation_manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async ValueTask<DataDirectoryRelocationResult> RelocateAsync(
        string sourceDataDirectory,
        string targetDataDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceDataDirectory))
        {
            throw new ArgumentException("Source data directory is required.", nameof(sourceDataDirectory));
        }

        if (string.IsNullOrWhiteSpace(targetDataDirectory))
        {
            throw new ArgumentException("Target data directory is required.", nameof(targetDataDirectory));
        }

        var source = Path.GetFullPath(sourceDataDirectory);
        var target = Path.GetFullPath(targetDataDirectory);
        EnsureDistinctLocations(source, target);

        if (!Directory.Exists(source))
        {
            throw new InvalidOperationException($"Current data directory does not exist: {source}");
        }

        Directory.CreateDirectory(target);
        var manifestPath = Path.Combine(target, ManifestFileName);
        var result = new CopyResult();
        var manifest = new RelocationManifest
        {
            StartedAt = DateTimeOffset.UtcNow,
            Status = "running",
            Source = source,
            Target = target
        };

        await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        try
        {
            await CopyDirectoryRecursiveAsync(source, target, result, cancellationToken);
            manifest.CompletedAt = DateTimeOffset.UtcNow;
            manifest.Status = result.Warnings.Count == 0 ? "completed" : "completed_with_warnings";
            manifest.CopiedFiles = result.Copied;
            manifest.SkippedFiles = result.Skipped;
            manifest.Warnings = result.Warnings.Take(20).ToList();
            await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        }
        catch (Exception ex)
        {
            manifest.CompletedAt = DateTimeOffset.UtcNow;
            manifest.Status = "failed";
            manifest.CopiedFiles = result.Copied;
            manifest.SkippedFiles = result.Skipped;
            manifest.Warnings = result.Warnings.Take(20).ToList();
            manifest.Error = ex.Message;
            await WriteManifestAsync(manifestPath, manifest, CancellationToken.None);
            throw;
        }

        return new DataDirectoryRelocationResult(
            result.Copied,
            result.Skipped,
            result.Warnings.Count,
            manifestPath);
    }

    private static void EnsureDistinctLocations(string source, string target)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedSource = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedSource, normalizedTarget, comparison))
        {
            throw new InvalidOperationException("The new data directory must differ from the current data directory.");
        }

        if (IsChildPath(normalizedTarget, normalizedSource, comparison))
        {
            throw new InvalidOperationException("The new data directory must not be inside the current data directory.");
        }

        if (IsChildPath(normalizedSource, normalizedTarget, comparison))
        {
            throw new InvalidOperationException("The new data directory must not contain the current data directory.");
        }
    }

    private static bool IsChildPath(string child, string parent, StringComparison comparison)
    {
        return child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    private static async ValueTask CopyDirectoryRecursiveAsync(
        string sourceDirectory,
        string targetDirectory,
        CopyResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsReparsePoint(sourceDirectory))
        {
            result.Skipped++;
            result.Warnings.Add($"Skipped reparse-point directory: {sourceDirectory}");
            return;
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(entry))
            {
                result.Skipped++;
                result.Warnings.Add($"Skipped reparse-point entry: {entry}");
                continue;
            }

            var target = Path.Combine(targetDirectory, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                await CopyDirectoryRecursiveAsync(entry, target, result, cancellationToken);
                continue;
            }

            if (!File.Exists(entry))
            {
                result.Skipped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target))
            {
                File.Copy(entry, target, overwrite: false);
                result.Copied++;
                continue;
            }

            if (await FilesEqualAsync(entry, target, cancellationToken))
            {
                result.Skipped++;
                continue;
            }

            result.Skipped++;
            result.Warnings.Add($"Skipped conflicting existing file: {target}");
        }
    }

    private static async ValueTask<bool> FilesEqualAsync(string left, string right, CancellationToken cancellationToken)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        await using var leftStream = File.OpenRead(left);
        await using var rightStream = File.OpenRead(right);
        var leftHash = await System.Security.Cryptography.SHA256.HashDataAsync(leftStream, cancellationToken);
        var rightHash = await System.Security.Cryptography.SHA256.HashDataAsync(rightStream, cancellationToken);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static async ValueTask WriteManifestAsync(
        string path,
        RelocationManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private sealed class CopyResult
    {
        public int Copied { get; set; }

        public int Skipped { get; set; }

        public List<string> Warnings { get; } = [];
    }

    private sealed class RelocationManifest
    {
        [JsonPropertyName("started_at")]
        public DateTimeOffset StartedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public DateTimeOffset? CompletedAt { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "running";

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("copied_files")]
        public int CopiedFiles { get; set; }

        [JsonPropertyName("skipped_files")]
        public int SkippedFiles { get; set; }

        [JsonPropertyName("warnings")]
        public List<string> Warnings { get; set; } = [];

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}
