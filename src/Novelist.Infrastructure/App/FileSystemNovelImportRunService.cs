using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemNovelImportRunService : INovelImportRunService
{
    private const int MaxTaskIdLength = 160;
    private const int MaxSourcePathLength = 4_096;
    private const int MaxSourceDisplayNameLength = 255;
    private const long MaxTextOrMarkdownImportBytes = 50L * 1024 * 1024;
    private const long MaxCompressedEpubImportBytes = 100L * 1024 * 1024;
    private const int MaxRequestedTitleLength = 200;
    private const int MaxCommitMessageLength = 500;
    private const int MaxStageLength = 128;
    private const int MaxCreatedFileRootCount = 512;
    private const int MaxCreatedFileRootLength = 512;
    private const int MaxSkippedChapterCount = 20_000;
    private const int MaxSkippedChapterTitleLength = 300;
    private const int MaxCodeLength = 128;
    private const int MaxMessageLength = 500;
    private const int MaxDetailLength = 4_000;
    private const int MaxSeverityLength = 32;
    private const int MaxDiagnostics = 256;
    private const int MaxWarnings = 256;

    private const string CleanupNotStarted = "not_started";
    private const string CleanupPending = "pending";
    private const string CleanupCompleted = "completed";
    private const string CleanupBlocked = "blocked";
    private const string WarningNone = "none";
    private const string WarningPresent = "present";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppInitializationOptions _options;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemNovelImportRunService(AppInitializationOptions? options = null)
    {
        _options = options ?? new AppInitializationOptions();
    }

    public async ValueTask<NovelImportRunPayload> StartRunAsync(
        StartNovelImportPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var sourceDisplayName = NormalizeSourceDisplayName(input.SourceDisplayName);
        var parserType = NormalizeImportKind(input.ImportKind);
        var sourcePath = ValidateSourceFilePath(input.SourcePath, parserType);
        var sourcePathHash = HashSourcePath(sourcePath);
        var requestedTitle = NormalizeOptionalText(input.RequestedTitle, nameof(input.RequestedTitle), MaxRequestedTitleLength, allowLineBreaks: false);
        var commitMessage = NormalizeOptionalText(input.CommitMessage, nameof(input.CommitMessage), MaxCommitMessageLength, allowLineBreaks: false);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            if (store.Runs.Any(run => string.Equals(run.TaskId, taskId, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Novel import run '{taskId}' already exists.", nameof(input.TaskId));
            }

            var now = DateTimeOffset.UtcNow;
            var run = new NovelImportRunStoreItem
            {
                TaskId = taskId,
                State = NovelImportRunStates.Created,
                Stage = NovelImportRunStates.Created,
                SourceDisplayName = sourceDisplayName,
                SourcePathHash = sourcePathHash,
                ParserType = parserType,
                RequestedTitle = requestedTitle,
                CommitMessage = commitMessage,
                CreatedNovelId = null,
                CreatedFileRoots = [],
                SkippedChapters = [],
                Diagnostics = [],
                Warnings = [],
                Error = null,
                CleanupState = CleanupNotStarted,
                WarningState = WarningNone,
                StartedAt = now,
                UpdatedAt = now,
                CompletedAt = null
            };

            store.Runs.Add(run);
            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NovelImportRunPayload> CancelRunAsync(
        CancelNovelImportPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var reason = NormalizeRequiredText(input.Reason, nameof(input.Reason), MaxDetailLength, allowLineBreaks: true);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            if (run.State == NovelImportRunStates.Cancelled)
            {
                return ToPayload(run);
            }

            EnsureTransitionAllowed(run.State, NovelImportRunStates.Cancelled);
            var now = DateTimeOffset.UtcNow;
            run.State = NovelImportRunStates.Cancelled;
            run.Stage = NovelImportRunStates.Cancelled;
            run.Error = new CopyableDiagnosticPayload(
                Code: "import.cancelled",
                Message: "小说导入已取消。",
                Detail: reason,
                Operation: "CancelNovelImport",
                TaskId: taskId,
                RunId: null,
                BridgeMethod: "CancelNovelImport",
                Timestamp: now);
            run.UpdatedAt = now;
            run.CompletedAt ??= now;
            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NovelImportRunPayload?> GetRunAsync(
        GetNovelImportRunPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var taskId = NormalizeRequiredText(input.TaskId, nameof(input.TaskId), MaxTaskIdLength, allowLineBreaks: false);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = store.Runs.FirstOrDefault(item => string.Equals(item.TaskId, taskId, StringComparison.Ordinal));
            return run is null ? null : ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NovelImportRecoveryStatusPayload> GetRecoveryStatusAsync(
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var pending = store.Runs
                .Where(run => IsRecoveryPending(run.State))
                .OrderBy(run => run.StartedAt)
                .Select(ToPayload)
                .ToArray();
            var blocked = store.Runs
                .Where(run => run.State == NovelImportRunStates.CleanupBlocked)
                .OrderBy(run => run.StartedAt)
                .Select(ToPayload)
                .ToArray();
            return new NovelImportRecoveryStatusPayload(pending, blocked, DateTimeOffset.UtcNow);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<NovelImportRunPayload> UpdateRunAsync(
        NovelImportRunUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var taskId = NormalizeRequiredText(update.TaskId, nameof(update.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        var nextState = NormalizeRunState(update.State);
        var stage = NormalizeRequiredText(update.Stage, nameof(update.Stage), MaxStageLength, allowLineBreaks: false);
        var createdFileRoots = update.CreatedFileRoots is null ? null : NormalizeCreatedFileRoots(update.CreatedFileRoots);
        var skippedChapters = update.SkippedChapters is null ? null : NormalizeSkippedChapters(update.SkippedChapters);
        var diagnostics = update.Diagnostics is null ? null : NormalizeDiagnostics(update.Diagnostics);
        var warnings = update.Warnings is null ? null : NormalizeWarnings(update.Warnings);
        var error = update.Error is null ? null : NormalizeCopyableDiagnostic(update.Error, taskId);

        if (update.CreatedNovelId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(update.CreatedNovelId), update.CreatedNovelId, "Created novel id must be positive.");
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var store = await LoadOrCreateAsync(cancellationToken);
            var run = FindRun(store, taskId);
            EnsureTransitionAllowed(run.State, nextState);
            if (RequiresError(nextState) && error is null && run.Error is null)
            {
                throw new ArgumentException($"Novel import state '{nextState}' requires an error diagnostic.", nameof(update.Error));
            }

            if (nextState == NovelImportRunStates.CompletedWithWarning &&
                (warnings is null ? run.Warnings.Count == 0 : warnings.Count == 0))
            {
                throw new ArgumentException("completed_with_warning requires at least one warning.", nameof(update.Warnings));
            }

            var now = DateTimeOffset.UtcNow;
            run.State = nextState;
            run.Stage = stage;
            if (update.CreatedNovelId is not null)
            {
                run.CreatedNovelId = update.CreatedNovelId;
            }

            if (createdFileRoots is not null)
            {
                run.CreatedFileRoots = createdFileRoots.ToList();
            }

            if (skippedChapters is not null)
            {
                run.SkippedChapters = skippedChapters.ToList();
            }

            if (diagnostics is not null)
            {
                run.Diagnostics = diagnostics.ToList();
            }

            if (warnings is not null)
            {
                run.Warnings = warnings.ToList();
                run.WarningState = run.Warnings.Count == 0 ? WarningNone : WarningPresent;
            }

            if (error is not null)
            {
                run.Error = error;
            }

            run.CleanupState = CleanupStateFor(nextState, run.CleanupState);
            run.UpdatedAt = now;
            if (IsTerminalState(nextState))
            {
                run.CompletedAt ??= now;
            }

            await SaveAsync(store, cancellationToken);
            return ToPayload(run);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<NovelImportRunStoreDocument> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var path = await StorePathAsync(cancellationToken);
        if (!File.Exists(path))
        {
            var empty = new NovelImportRunStoreDocument();
            await SaveAsync(empty, cancellationToken);
            return empty;
        }

        await using var stream = File.OpenRead(path);
        var store = await JsonSerializer.DeserializeAsync<NovelImportRunStoreDocument>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Novel import run store is empty or malformed.");

        ValidateStore(store);
        return store;
    }

    private async ValueTask SaveAsync(NovelImportRunStoreDocument store, CancellationToken cancellationToken)
    {
        ValidateStore(store);
        var path = await StorePathAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async ValueTask<string> StorePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "novel_imports",
            "runs.json");
    }

    private static NovelImportRunStoreItem FindRun(NovelImportRunStoreDocument store, string taskId)
    {
        return store.Runs.FirstOrDefault(run => string.Equals(run.TaskId, taskId, StringComparison.Ordinal))
            ?? throw new ArgumentException($"Novel import run '{taskId}' does not exist.", nameof(taskId));
    }

    private static string HashSourcePath(string? sourcePath)
    {
        var normalized = NormalizeRequiredText(sourcePath, nameof(sourcePath), MaxSourcePathLength, allowLineBreaks: false);
        try
        {
            if (!Path.IsPathFullyQualified(normalized))
            {
                throw new ArgumentException("Source path must be absolute.", nameof(sourcePath));
            }

            var fullPath = Path.GetFullPath(normalized);
            var hashInput = OperatingSystem.IsWindows()
                ? fullPath.ToUpperInvariant()
                : fullPath;
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
            return $"sha256:{Convert.ToHexString(bytes).ToLowerInvariant()}";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Source path must be a valid absolute local path.", nameof(sourcePath), ex);
        }
    }

    private static string ValidateSourceFilePath(string? sourcePath, string parserType)
    {
        var normalized = NormalizeRequiredText(sourcePath, nameof(sourcePath), MaxSourcePathLength, allowLineBreaks: false);
        if (LooksLikeAbsoluteUri(normalized))
        {
            throw new ArgumentException("Source path must be a local filesystem path, not a URI.", nameof(sourcePath));
        }

        if (ContainsTraversalSegment(normalized))
        {
            throw new ArgumentException("Source path must not contain traversal segments.", nameof(sourcePath));
        }

        if (LooksLikeUnsupportedDevicePath(normalized))
        {
            throw new ArgumentException("Source path must not use an unsupported device path.", nameof(sourcePath));
        }

        string fullPath;
        try
        {
            if (!Path.IsPathFullyQualified(normalized))
            {
                throw new ArgumentException("Source path must be absolute.", nameof(sourcePath));
            }

            fullPath = Path.GetFullPath(normalized);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Source path must be a valid absolute local path.", nameof(sourcePath), ex);
        }

        if (LooksLikeUnsupportedDevicePath(fullPath))
        {
            throw new ArgumentException("Source path must not use an unsupported device path.", nameof(sourcePath));
        }

        if (Directory.Exists(fullPath))
        {
            throw new ArgumentException("Source path must point to a file, not a directory.", nameof(sourcePath));
        }

        var expectedKind = ImportKindForExtension(Path.GetExtension(fullPath));
        if (expectedKind is null)
        {
            throw new ArgumentException("Source path extension is not supported for novel import.", nameof(sourcePath));
        }

        if (!string.Equals(expectedKind, parserType, StringComparison.Ordinal))
        {
            throw new ArgumentException("Source path extension does not match the requested import kind.", nameof(sourcePath));
        }

        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
        {
            throw new ArgumentException("Source path must point to an existing file.", nameof(sourcePath));
        }

        long sourceBytes;
        try
        {
            sourceBytes = fileInfo.Length;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            throw new ArgumentException("Source path must point to a readable file.", nameof(sourcePath), ex);
        }

        var limitBytes = MaxSourceBytesForKind(parserType);
        if (sourceBytes > limitBytes)
        {
            throw new ArgumentException(
                $"Source file is too large for '{parserType}' import. Limit: {limitBytes} bytes.",
                nameof(sourcePath));
        }

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            _ = stream.Length;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            throw new ArgumentException("Source path must point to a readable file.", nameof(sourcePath), ex);
        }

        return fullPath;
    }

    private static bool LooksLikeAbsoluteUri(string value)
    {
        return value.Contains("://", StringComparison.Ordinal) ||
            value.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsTraversalSegment(string value)
    {
        return value
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    private static bool LooksLikeUnsupportedDevicePath(string value)
    {
        var windowsStyle = value.Replace('/', '\\');
        if (windowsStyle.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            windowsStyle.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return true;
        }

        var unixStyle = value.Replace('\\', '/');
        return unixStyle.Equals("/dev", StringComparison.Ordinal) ||
            unixStyle.StartsWith("/dev/", StringComparison.Ordinal);
    }

    private static string? ImportKindForExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".epub" => NovelImportKinds.Epub,
            ".txt" => NovelImportKinds.Txt,
            ".md" => NovelImportKinds.Markdown,
            ".markdown" => NovelImportKinds.Markdown,
            _ => null
        };
    }

    private static long MaxSourceBytesForKind(string importKind)
    {
        return importKind switch
        {
            NovelImportKinds.Epub => MaxCompressedEpubImportBytes,
            NovelImportKinds.Txt or NovelImportKinds.Markdown => MaxTextOrMarkdownImportBytes,
            _ => throw new ArgumentException($"Unsupported novel import kind '{importKind}'.", nameof(importKind))
        };
    }

    private static string NormalizeSourceDisplayName(string? value)
    {
        var displayName = NormalizeRequiredText(value, nameof(value), MaxSourceDisplayNameLength, allowLineBreaks: false);
        if (displayName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            displayName.Contains('/', StringComparison.Ordinal) ||
            displayName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Source display name must be a file name, not a path.", nameof(value));
        }

        return displayName;
    }

    private static string NormalizeImportKind(string? value)
    {
        var kind = NormalizeRequiredText(value, nameof(value), 32, allowLineBreaks: false).ToLowerInvariant();
        return kind switch
        {
            NovelImportKinds.Epub => NovelImportKinds.Epub,
            NovelImportKinds.Txt => NovelImportKinds.Txt,
            NovelImportKinds.Markdown => NovelImportKinds.Markdown,
            _ => throw new ArgumentException($"Unsupported novel import kind '{kind}'.", nameof(value))
        };
    }

    private static string NormalizeRunState(string? value)
    {
        var state = NormalizeRequiredText(value, nameof(value), 64, allowLineBreaks: false);
        if (!IsSupportedState(state))
        {
            throw new ArgumentException($"Unsupported novel import run state '{state}'.", nameof(value));
        }

        return state;
    }

    private static IReadOnlyList<string> NormalizeCreatedFileRoots(IReadOnlyList<string> roots)
    {
        if (roots.Count > MaxCreatedFileRootCount)
        {
            throw new ArgumentOutOfRangeException(nameof(roots), roots.Count, $"At most {MaxCreatedFileRootCount} file roots are allowed.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var root in roots)
        {
            var rawValue = NormalizeRequiredText(root, nameof(roots), MaxCreatedFileRootLength, allowLineBreaks: false);
            var value = rawValue.Replace('\\', '/').Trim('/');
            if (Path.IsPathRooted(rawValue) ||
                LooksLikeWindowsRootedPath(rawValue) ||
                string.IsNullOrWhiteSpace(value) ||
                value.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            {
                throw new ArgumentException("Created file roots must be safe relative paths.", nameof(roots));
            }

            if (seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private static bool LooksLikeWindowsRootedPath(string value)
    {
        return value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';
    }

    private static IReadOnlyList<NovelImportSkippedChapterPayload> NormalizeSkippedChapters(
        IReadOnlyList<NovelImportSkippedChapterPayload> chapters)
    {
        if (chapters.Count > MaxSkippedChapterCount)
        {
            throw new ArgumentOutOfRangeException(nameof(chapters), chapters.Count, $"At most {MaxSkippedChapterCount} skipped chapters are allowed.");
        }

        return chapters.Select(chapter =>
        {
            if (chapter.Index <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chapter.Index), chapter.Index, "Skipped chapter index must be positive.");
            }

            return new NovelImportSkippedChapterPayload(
                Index: chapter.Index,
                Title: NormalizeOptionalText(chapter.Title, nameof(chapter.Title), MaxSkippedChapterTitleLength, allowLineBreaks: false),
                Reason: NormalizeRequiredText(chapter.Reason, nameof(chapter.Reason), MaxCodeLength, allowLineBreaks: false));
        }).ToArray();
    }

    private static IReadOnlyList<NovelImportDiagnosticPayload> NormalizeDiagnostics(
        IReadOnlyList<NovelImportDiagnosticPayload> diagnostics)
    {
        if (diagnostics.Count > MaxDiagnostics)
        {
            throw new ArgumentOutOfRangeException(nameof(diagnostics), diagnostics.Count, $"At most {MaxDiagnostics} diagnostics are allowed.");
        }

        return diagnostics.Select(diagnostic => new NovelImportDiagnosticPayload(
            Code: NormalizeRequiredText(diagnostic.Code, nameof(diagnostic.Code), MaxCodeLength, allowLineBreaks: false),
            Message: NormalizeRequiredText(diagnostic.Message, nameof(diagnostic.Message), MaxMessageLength, allowLineBreaks: false),
            Detail: NormalizeOptionalText(diagnostic.Detail, nameof(diagnostic.Detail), MaxDetailLength, allowLineBreaks: true),
            Severity: NormalizeRequiredText(diagnostic.Severity, nameof(diagnostic.Severity), MaxSeverityLength, allowLineBreaks: false))).ToArray();
    }

    private static IReadOnlyList<NovelImportWarningPayload> NormalizeWarnings(
        IReadOnlyList<NovelImportWarningPayload> warnings)
    {
        if (warnings.Count > MaxWarnings)
        {
            throw new ArgumentOutOfRangeException(nameof(warnings), warnings.Count, $"At most {MaxWarnings} warnings are allowed.");
        }

        return warnings.Select(warning => new NovelImportWarningPayload(
            Code: NormalizeRequiredText(warning.Code, nameof(warning.Code), MaxCodeLength, allowLineBreaks: false),
            Message: NormalizeRequiredText(warning.Message, nameof(warning.Message), MaxMessageLength, allowLineBreaks: false),
            Detail: NormalizeOptionalText(warning.Detail, nameof(warning.Detail), MaxDetailLength, allowLineBreaks: true))).ToArray();
    }

    private static CopyableDiagnosticPayload NormalizeCopyableDiagnostic(CopyableDiagnosticPayload diagnostic, string taskId)
    {
        var diagnosticTaskId = diagnostic.TaskId is null
            ? null
            : NormalizeRequiredText(diagnostic.TaskId, nameof(diagnostic.TaskId), MaxTaskIdLength, allowLineBreaks: false);
        if (diagnosticTaskId is not null && !string.Equals(diagnosticTaskId, taskId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Diagnostic task id must match the import run.", nameof(diagnostic.TaskId));
        }

        return new CopyableDiagnosticPayload(
            Code: NormalizeRequiredText(diagnostic.Code, nameof(diagnostic.Code), MaxCodeLength, allowLineBreaks: false),
            Message: NormalizeRequiredText(diagnostic.Message, nameof(diagnostic.Message), MaxMessageLength, allowLineBreaks: false),
            Detail: NormalizeOptionalText(diagnostic.Detail, nameof(diagnostic.Detail), MaxDetailLength, allowLineBreaks: true),
            Operation: NormalizeRequiredText(diagnostic.Operation, nameof(diagnostic.Operation), MaxMessageLength, allowLineBreaks: false),
            TaskId: diagnosticTaskId ?? taskId,
            RunId: diagnostic.RunId is null
                ? null
                : NormalizeRequiredText(diagnostic.RunId, nameof(diagnostic.RunId), MaxTaskIdLength, allowLineBreaks: false),
            BridgeMethod: diagnostic.BridgeMethod is null
                ? null
                : NormalizeRequiredText(diagnostic.BridgeMethod, nameof(diagnostic.BridgeMethod), MaxMessageLength, allowLineBreaks: false),
            Timestamp: diagnostic.Timestamp);
    }

    private static string CleanupStateFor(string state, string current)
    {
        return state switch
        {
            NovelImportRunStates.CleanupPending => CleanupPending,
            NovelImportRunStates.CleanupCompleted => CleanupCompleted,
            NovelImportRunStates.CleanupBlocked => CleanupBlocked,
            _ => current
        };
    }

    private static void EnsureTransitionAllowed(string currentState, string nextState)
    {
        if (!IsSupportedState(nextState))
        {
            throw new ArgumentException($"Unsupported novel import run state '{nextState}'.", nameof(nextState));
        }

        if (IsTerminalState(currentState))
        {
            throw new InvalidOperationException($"Novel import run is already terminal: {currentState}.");
        }

        if (StateRank(nextState) < StateRank(currentState))
        {
            throw new InvalidOperationException($"Novel import run state cannot move from '{currentState}' back to '{nextState}'.");
        }
    }

    private static bool IsRecoveryPending(string state)
    {
        return !IsTerminalState(state);
    }

    private static bool RequiresError(string state)
    {
        return state is NovelImportRunStates.CleanupBlocked
            or NovelImportRunStates.Failed
            or NovelImportRunStates.Cancelled;
    }

    private static bool IsTerminalState(string state)
    {
        return state is NovelImportRunStates.Completed
            or NovelImportRunStates.CompletedWithWarning
            or NovelImportRunStates.CleanupCompleted
            or NovelImportRunStates.CleanupBlocked
            or NovelImportRunStates.Failed
            or NovelImportRunStates.Cancelled;
    }

    private static bool IsSupportedState(string state)
    {
        return StateRank(state) >= 0;
    }

    private static int StateRank(string state)
    {
        return state switch
        {
            NovelImportRunStates.Created => 0,
            NovelImportRunStates.Parsing => 1,
            NovelImportRunStates.CreatingNovel => 2,
            NovelImportRunStates.WritingFiles => 3,
            NovelImportRunStates.SavingMetadata => 4,
            NovelImportRunStates.Indexing => 5,
            NovelImportRunStates.GitCommit => 6,
            NovelImportRunStates.Completed => 7,
            NovelImportRunStates.CompletedWithWarning => 8,
            NovelImportRunStates.CleanupPending => 9,
            NovelImportRunStates.CleanupCompleted => 10,
            NovelImportRunStates.CleanupBlocked => 11,
            NovelImportRunStates.Failed => 12,
            NovelImportRunStates.Cancelled => 13,
            _ => -1
        };
    }

    private static string NormalizeRequiredText(string? value, string name, int maxLength, bool allowLineBreaks)
    {
        var normalized = NormalizeOptionalText(value, name, maxLength, allowLineBreaks);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Value must be a non-empty string.", name);
        }

        return normalized;
    }

    private static string NormalizeOptionalText(string? value, string name, int maxLength, bool allowLineBreaks)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, normalized.Length, $"Value must be at most {maxLength} characters.");
        }

        if (normalized.Any(ch => char.IsControl(ch) && (!allowLineBreaks || ch is not ('\r' or '\n' or '\t'))))
        {
            throw new ArgumentException("Value must not contain unsupported control characters.", name);
        }

        return normalized;
    }

    private static void ValidateStore(NovelImportRunStoreDocument store)
    {
        if (store.Version != 1)
        {
            throw new InvalidOperationException($"Unsupported novel import run store version '{store.Version}'.");
        }

        if (store.Runs is null || store.Runs.Any(run =>
            run is null ||
            string.IsNullOrWhiteSpace(run.TaskId) ||
            !IsSupportedState(run.State) ||
            string.IsNullOrWhiteSpace(run.Stage) ||
            string.IsNullOrWhiteSpace(run.SourceDisplayName) ||
            string.IsNullOrWhiteSpace(run.SourcePathHash) ||
            !run.SourcePathHash.StartsWith("sha256:", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(run.ParserType) ||
            run.CreatedNovelId is <= 0 ||
            run.CreatedFileRoots is null ||
            run.SkippedChapters is null ||
            run.Diagnostics is null ||
            run.Warnings is null ||
            string.IsNullOrWhiteSpace(run.CleanupState) ||
            string.IsNullOrWhiteSpace(run.WarningState) ||
            !IsSupportedCleanupState(run.CleanupState) ||
            !IsSupportedWarningState(run.WarningState) ||
            (run.WarningState == WarningNone && run.Warnings.Count > 0) ||
            (run.WarningState == WarningPresent && run.Warnings.Count == 0) ||
            (run.State == NovelImportRunStates.CleanupPending && run.CleanupState != CleanupPending) ||
            (run.State == NovelImportRunStates.CleanupCompleted && run.CleanupState != CleanupCompleted) ||
            (run.State == NovelImportRunStates.CleanupBlocked && run.CleanupState != CleanupBlocked) ||
            (RequiresError(run.State) && run.Error is null) ||
            (run.State == NovelImportRunStates.CompletedWithWarning && run.Warnings.Count == 0) ||
            run.UpdatedAt < run.StartedAt ||
            IsTerminalState(run.State) && run.CompletedAt is null ||
            !IsTerminalState(run.State) && run.CompletedAt is not null))
        {
            throw new InvalidOperationException("Novel import run store contains invalid run state.");
        }

        if (store.Runs.Select(run => run.TaskId).Distinct(StringComparer.Ordinal).Count() != store.Runs.Count)
        {
            throw new InvalidOperationException("Novel import run store contains duplicate task ids.");
        }

        foreach (var run in store.Runs)
        {
            _ = NormalizeImportKind(run.ParserType);
            _ = NormalizeCreatedFileRoots(run.CreatedFileRoots);
            _ = NormalizeSkippedChapters(run.SkippedChapters);
            _ = NormalizeDiagnostics(run.Diagnostics);
            _ = NormalizeWarnings(run.Warnings);
            if (run.Error is not null)
            {
                _ = NormalizeCopyableDiagnostic(run.Error, run.TaskId);
            }
        }
    }

    private static bool IsSupportedCleanupState(string state)
    {
        return state is CleanupNotStarted or CleanupPending or CleanupCompleted or CleanupBlocked;
    }

    private static bool IsSupportedWarningState(string state)
    {
        return state is WarningNone or WarningPresent;
    }

    private static NovelImportRunPayload ToPayload(NovelImportRunStoreItem run)
    {
        return new NovelImportRunPayload(
            TaskId: run.TaskId,
            State: run.State,
            Stage: run.Stage,
            SourceDisplayName: run.SourceDisplayName,
            SourcePathHash: run.SourcePathHash,
            ParserType: run.ParserType,
            CreatedNovelId: run.CreatedNovelId,
            CreatedFileRoots: run.CreatedFileRoots.ToArray(),
            SkippedChapters: run.SkippedChapters.ToArray(),
            Diagnostics: run.Diagnostics.ToArray(),
            Warnings: run.Warnings.ToArray(),
            Error: run.Error,
            StartedAt: run.StartedAt,
            UpdatedAt: run.UpdatedAt,
            CompletedAt: run.CompletedAt);
    }

    private sealed class NovelImportRunStoreDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("runs")]
        public List<NovelImportRunStoreItem> Runs { get; set; } = [];
    }

    private sealed class NovelImportRunStoreItem
    {
        [JsonPropertyName("task_id")]
        public string TaskId { get; set; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; set; } = NovelImportRunStates.Created;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = NovelImportRunStates.Created;

        [JsonPropertyName("source_display_name")]
        public string SourceDisplayName { get; set; } = string.Empty;

        [JsonPropertyName("source_path_hash")]
        public string SourcePathHash { get; set; } = string.Empty;

        [JsonPropertyName("parser_type")]
        public string ParserType { get; set; } = string.Empty;

        [JsonPropertyName("requested_title")]
        public string RequestedTitle { get; set; } = string.Empty;

        [JsonPropertyName("commit_message")]
        public string CommitMessage { get; set; } = string.Empty;

        [JsonPropertyName("created_novel_id")]
        public long? CreatedNovelId { get; set; }

        [JsonPropertyName("created_file_roots")]
        public List<string> CreatedFileRoots { get; set; } = [];

        [JsonPropertyName("skipped_chapters")]
        public List<NovelImportSkippedChapterPayload> SkippedChapters { get; set; } = [];

        [JsonPropertyName("diagnostics")]
        public List<NovelImportDiagnosticPayload> Diagnostics { get; set; } = [];

        [JsonPropertyName("warnings")]
        public List<NovelImportWarningPayload> Warnings { get; set; } = [];

        [JsonPropertyName("error")]
        public CopyableDiagnosticPayload? Error { get; set; }

        [JsonPropertyName("cleanup_state")]
        public string CleanupState { get; set; } = CleanupNotStarted;

        [JsonPropertyName("warning_state")]
        public string WarningState { get; set; } = WarningNone;

        [JsonPropertyName("started_at")]
        public DateTimeOffset StartedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("completed_at")]
        public DateTimeOffset? CompletedAt { get; set; }
    }
}
