using System.Collections.Concurrent;
using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Core.Bridge;

namespace Novelist.Infrastructure.App;

public sealed class FileSystemNovelImportService : INovelImportRunService
{
    private const int MaxDiagnosticDetailLength = 4_000;
    private const int MaxProgressChapterTitleLength = 300;
    private const int DefaultProgressTotal = 7;
    private const string ProgressEventName = "novel_import:progress";

    private readonly INovelImportRunService _runs;
    private readonly INovelService _novels;
    private readonly IChapterContentService _chapters;
    private readonly IVersionControlService _versionControl;
    private readonly IBridgeEventSink _events;
    private readonly ImportRagRefreshRecorder? _ragRefreshRecorder;
    private readonly ConcurrentDictionary<string, ActiveImportRun> _activeRuns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public FileSystemNovelImportService(
        AppInitializationOptions? options = null,
        INovelImportRunService? runService = null,
        INovelService? novelService = null,
        IChapterContentService? chapterContentService = null,
        IVersionControlService? versionControl = null,
        IWritingDeltaRecorder? writingDeltaRecorder = null,
        IRagIndexRefreshNotifier? ragRefreshNotifier = null,
        IBridgeEventSink? eventSink = null)
    {
        var resolvedOptions = options ?? new AppInitializationOptions();
        _runs = runService ?? new FileSystemNovelImportRunService(resolvedOptions);
        _versionControl = versionControl ?? new GitVersionControlService(resolvedOptions);
        _events = eventSink ?? new NullBridgeEventSink();
        _novels = novelService ?? new FileSystemNovelService(
            resolvedOptions,
            new FileSystemAppSettingsService(resolvedOptions),
            _versionControl);
        _ragRefreshRecorder = ragRefreshNotifier is null ? null : new ImportRagRefreshRecorder(ragRefreshNotifier);
        _chapters = chapterContentService ?? new FileSystemChapterContentService(
            resolvedOptions,
            _novels,
            writingDeltaRecorder,
            ragRefreshNotifier: null,
            new DeferredImportVersionControlService());
    }

    public async ValueTask<NovelImportRunPayload> StartRunAsync(
        StartNovelImportPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            _ = await _runs.StartRunAsync(input, cancellationToken);
            await EmitProgressAsync(
                input.TaskId,
                NovelImportRunStates.Created,
                NovelImportRunStates.Created,
                0,
                DefaultProgressTotal,
                ProgressMessage(NovelImportRunStates.Created, NovelImportRunStates.Created),
                null,
                null,
                null);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var activeRun = new ActiveImportRun(operationCancellation);
            if (!_activeRuns.TryAdd(input.TaskId, activeRun))
            {
                throw new InvalidOperationException($"Novel import run is already active: {input.TaskId}.");
            }

            NovelImportRunPayload? finalRun = null;
            Exception? finalException = null;
            async ValueTask<NovelImportRunPayload> CompleteAsync(ValueTask<NovelImportRunPayload> pending)
            {
                finalRun = await pending;
                return finalRun;
            }

            var operationToken = activeRun.Token;
            try
            {
                long? createdNovelId = null;
                var createdFileRoots = Array.Empty<string>();

                try
                {
                    var progressTotal = DefaultProgressTotal;
                    await UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.Parsing,
                        "parse_source",
                        createdNovelId,
                        createdFileRoots,
                        null,
                        null,
                        null,
                        null,
                        operationToken,
                        progressCompleted: 1,
                        progressTotal: progressTotal);

                    var parsed = await ParseAsync(input, operationToken);
                    progressTotal = Math.Max(DefaultProgressTotal, parsed.Chapters.Count + 6);
                    var diagnostics = parsed.Diagnostics;
                    var skipped = parsed.SkippedChapters;
                    var title = ResolveNovelTitle(input, parsed);
                    var description = $"Imported from {input.SourceDisplayName}. Source path is intentionally not persisted.";

                    await UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.CreatingNovel,
                        "create_novel",
                        createdNovelId,
                        createdFileRoots,
                        skipped,
                        diagnostics,
                        null,
                        null,
                        operationToken,
                        progressCompleted: 2,
                        progressTotal: progressTotal);

                    var novel = await _novels.CreateNovelAsync(
                        new CreateNovelPayload(title, description, string.Empty),
                        operationToken);
                    createdNovelId = novel.Id;
                    createdFileRoots = [$"novels/{novel.Id}"];

                    await UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.WritingFiles,
                        "write_chapters",
                        createdNovelId,
                        createdFileRoots,
                        skipped,
                        diagnostics,
                        null,
                        null,
                        operationToken,
                        progressCompleted: 2,
                        progressTotal: progressTotal);

                    var importedChapterPaths = new List<string>(parsed.Chapters.Count);
                    for (var index = 0; index < parsed.Chapters.Count; index++)
                    {
                        var parsedChapter = parsed.Chapters[index];
                        operationToken.ThrowIfCancellationRequested();
                        var chapter = await _chapters.CreateChapterAsync(
                            new CreateChapterPayload(novel.Id, parsedChapter.Title),
                            operationToken);
                        await _chapters.SaveContentAsync(
                            new SaveContentPayload(novel.Id, chapter.FilePath, parsedChapter.Content),
                            operationToken);
                        importedChapterPaths.Add(chapter.FilePath);
                        await EmitProgressAsync(
                            input.TaskId,
                            NovelImportRunStates.WritingFiles,
                            "write_chapter",
                            3 + index,
                            progressTotal,
                            $"正在写入章节 {index + 1}/{parsed.Chapters.Count}",
                            createdNovelId,
                            index + 1,
                            parsedChapter.Title);
                    }

                    await UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.SavingMetadata,
                        "saving_metadata",
                        createdNovelId,
                        createdFileRoots,
                        skipped,
                        diagnostics,
                        null,
                        null,
                        operationToken,
                        progressCompleted: parsed.Chapters.Count + 3,
                        progressTotal: progressTotal);

                    var warnings = new List<NovelImportWarningPayload>();
                    operationToken.ThrowIfCancellationRequested();
                    if (_ragRefreshRecorder is not null)
                    {
                        foreach (var chapterPath in importedChapterPaths)
                        {
                            await _ragRefreshRecorder.MarkNovelIndexStaleAsync(
                                novel.Id,
                                $"Chapter content changed: {chapterPath}",
                                CancellationToken.None);
                        }

                        if (_ragRefreshRecorder.Failures.Count > 0)
                        {
                            warnings.AddRange(_ragRefreshRecorder.Failures.Select(failure => new NovelImportWarningPayload(
                                "index.refresh_failed",
                                "导入已完成，但搜索索引刷新标记失败。",
                                Truncate(failure))));
                        }
                    }

                    await UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.Indexing,
                        "indexing",
                        createdNovelId,
                        createdFileRoots,
                        skipped,
                        diagnostics,
                        warnings.Count == 0 ? null : warnings,
                        null,
                        operationToken,
                        progressCompleted: parsed.Chapters.Count + 4,
                        progressTotal: progressTotal);

                    await UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.GitCommit,
                        "git_commit",
                        createdNovelId,
                        createdFileRoots,
                        skipped,
                        diagnostics,
                        warnings.Count == 0 ? null : warnings,
                        null,
                        operationToken,
                        progressCompleted: parsed.Chapters.Count + 5,
                        progressTotal: progressTotal);

                    try
                    {
                        await _versionControl.CommitIfChangedAsync(
                            novel.Id,
                            ResolveCommitMessage(input, title),
                            operationToken);
                    }
                    catch (VersionControlException ex)
                    {
                        warnings.Add(new NovelImportWarningPayload(
                            "git.commit_failed",
                            "导入已完成，但 Git 提交失败。",
                            Truncate(ex.Message)));
                    }

                    var finalState = warnings.Count == 0
                        ? NovelImportRunStates.Completed
                        : NovelImportRunStates.CompletedWithWarning;
                    return await CompleteAsync(UpdateAsync(
                        input.TaskId,
                        finalState,
                        "done",
                        createdNovelId,
                        createdFileRoots,
                        skipped,
                        diagnostics,
                        warnings.Count == 0 ? null : warnings,
                        null,
                        operationToken,
                        progressCompleted: progressTotal,
                        progressTotal: progressTotal));
                }
                catch (OperationCanceledException ex) when (createdNovelId is not null)
                {
                    return await CompleteAsync(CleanupCreatedNovelAsync(
                        input.TaskId,
                        createdNovelId.Value,
                        createdFileRoots,
                        Diagnostic(
                            "import.cancelled",
                            "导入已取消。",
                            activeRun.CancellationReason ?? ex.Message,
                            input.TaskId),
                        CancellationToken.None));
                }
                catch (OperationCanceledException ex)
                {
                    return await CompleteAsync(UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.Cancelled,
                        "cancelled",
                        null,
                        null,
                        null,
                        null,
                        null,
                        Diagnostic(
                            "import.cancelled",
                            "导入已取消。",
                            activeRun.CancellationReason ?? ex.Message,
                            input.TaskId),
                        CancellationToken.None));
                }
                catch (NovelImportTextParseException ex)
                {
                    return await CompleteAsync(UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.Failed,
                        "parse_failed",
                        null,
                        null,
                        ex.SkippedChapters,
                        ex.Diagnostics,
                        null,
                        Diagnostic(ex.Code, ex.Message, JoinDiagnostics(ex.Diagnostics), input.TaskId),
                        CancellationToken.None));
                }
                catch (NovelImportEpubParseException ex)
                {
                    return await CompleteAsync(UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.Failed,
                        "parse_failed",
                        null,
                        null,
                        ex.SkippedChapters,
                        ex.Diagnostics,
                        null,
                        Diagnostic(ex.Code, ex.Message, JoinDiagnostics(ex.Diagnostics), input.TaskId),
                        CancellationToken.None));
                }
                catch (Exception ex) when (createdNovelId is not null)
                {
                    return await CompleteAsync(CleanupCreatedNovelAsync(
                        input.TaskId,
                        createdNovelId.Value,
                        createdFileRoots,
                        Diagnostic("import.write_failed", "导入写入失败，已尝试清理。", ex.Message, input.TaskId),
                        CancellationToken.None));
                }
                catch (Exception ex)
                {
                    return await CompleteAsync(UpdateAsync(
                        input.TaskId,
                        NovelImportRunStates.Failed,
                        "failed",
                        null,
                        null,
                        null,
                        null,
                        null,
                        Diagnostic("import.failed", "导入失败。", ex.Message, input.TaskId),
                        CancellationToken.None));
                }
            }
            catch (Exception ex)
            {
                finalException = ex;
                throw;
            }
            finally
            {
                _activeRuns.TryRemove(input.TaskId, out _);
                if (finalRun is not null)
                {
                    activeRun.Completion.TrySetResult(finalRun);
                }
                else if (finalException is not null)
                {
                    activeRun.Completion.TrySetException(finalException);
                }
                else
                {
                    activeRun.Completion.TrySetCanceled(CancellationToken.None);
                }
            }
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
        if (!string.IsNullOrWhiteSpace(input.TaskId) &&
            _activeRuns.TryGetValue(input.TaskId, out var activeRun))
        {
            activeRun.Cancel(input.Reason);
            return await activeRun.Completion.Task.WaitAsync(cancellationToken);
        }

        return await _runs.CancelRunAsync(input, cancellationToken);
    }

    public ValueTask<NovelImportRunPayload?> GetRunAsync(
        GetNovelImportRunPayload input,
        CancellationToken cancellationToken)
    {
        return _runs.GetRunAsync(input, cancellationToken);
    }

    public ValueTask<NovelImportRecoveryStatusPayload> GetRecoveryStatusAsync(CancellationToken cancellationToken)
    {
        return _runs.GetRecoveryStatusAsync(cancellationToken);
    }

    public ValueTask<NovelImportRunPayload> UpdateRunAsync(
        NovelImportRunUpdate update,
        CancellationToken cancellationToken)
    {
        return _runs.UpdateRunAsync(update, cancellationToken);
    }

    private async ValueTask<ParsedImportDocument> ParseAsync(
        StartNovelImportPayload input,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(input.SourcePath, cancellationToken);
        return input.ImportKind switch
        {
            NovelImportKinds.Txt or NovelImportKinds.Markdown => FromText(
                NovelImportTextParser.Parse(bytes, input.SourceDisplayName, input.ImportKind)),
            NovelImportKinds.Epub => FromEpub(NovelImportEpubParser.Parse(bytes, input.SourceDisplayName)),
            _ => throw new ArgumentException($"Unsupported import kind '{input.ImportKind}'.", nameof(input))
        };
    }

    private async ValueTask<NovelImportRunPayload> CleanupCreatedNovelAsync(
        string taskId,
        long novelId,
        IReadOnlyList<string> createdFileRoots,
        CopyableDiagnosticPayload error,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(
            taskId,
            NovelImportRunStates.CleanupPending,
            "cleanup_created_files",
            novelId,
            createdFileRoots,
            null,
            null,
            null,
            error,
            CancellationToken.None);

        try
        {
            await _novels.DeleteNovelAsync(novelId, cancellationToken);
            return await UpdateAsync(
                taskId,
                NovelImportRunStates.CleanupCompleted,
                "cleanup_completed",
                novelId,
                createdFileRoots,
                null,
                null,
                null,
                null,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            return await UpdateAsync(
                taskId,
                NovelImportRunStates.CleanupBlocked,
                "cleanup_blocked",
                novelId,
                createdFileRoots,
                null,
                null,
                null,
                Diagnostic("import.cleanup_blocked", "导入失败后的清理被阻止。", ex.Message, taskId),
                CancellationToken.None);
        }
    }

    private async ValueTask<NovelImportRunPayload> UpdateAsync(
        string taskId,
        string state,
        string stage,
        long? createdNovelId,
        IReadOnlyList<string>? createdFileRoots,
        IReadOnlyList<NovelImportSkippedChapterPayload>? skipped,
        IReadOnlyList<NovelImportDiagnosticPayload>? diagnostics,
        IReadOnlyList<NovelImportWarningPayload>? warnings,
        CopyableDiagnosticPayload? error,
        CancellationToken cancellationToken,
        int? progressCompleted = null,
        int? progressTotal = null,
        string? progressMessage = null)
    {
        var run = await _runs.UpdateRunAsync(
            new NovelImportRunUpdate(
                taskId,
                state,
                stage,
                createdNovelId,
                createdFileRoots,
                skipped,
                diagnostics,
                warnings,
                error),
            cancellationToken);
        var completed = progressCompleted ?? ProgressCompletedForState(run.State);
        var total = Math.Max(progressTotal ?? DefaultProgressTotal, 1);
        await EmitProgressAsync(
            run.TaskId,
            run.State,
            run.Stage,
            Math.Clamp(completed, 0, total),
            total,
            progressMessage ?? ProgressMessage(run.State, run.Stage),
            run.CreatedNovelId,
            null,
            null);
        return run;
    }

    private async ValueTask EmitProgressAsync(
        string taskId,
        string state,
        string stage,
        int progressCompleted,
        int progressTotal,
        string message,
        long? createdNovelId,
        int? currentChapterIndex,
        string? currentChapterTitle)
    {
        var total = Math.Max(progressTotal, 1);
        var payload = new NovelImportProgressPayload(
            TaskId: taskId,
            State: state,
            Stage: stage,
            ProgressCompleted: Math.Clamp(progressCompleted, 0, total),
            ProgressTotal: total,
            Message: Truncate(message, 500),
            CreatedNovelId: createdNovelId,
            CurrentChapterIndex: currentChapterIndex,
            CurrentChapterTitle: currentChapterTitle is null
                ? null
                : Truncate(currentChapterTitle, MaxProgressChapterTitleLength),
            UpdatedAt: DateTimeOffset.UtcNow);

        try
        {
            await _events.EmitAsync(ProgressEventName, payload, CancellationToken.None);
        }
        catch
        {
            // Progress events are observational; a UI delivery failure must not roll
            // back or corrupt an import that already updated durable state.
        }
    }

    private static int ProgressCompletedForState(string state)
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
            NovelImportRunStates.Completed or
                NovelImportRunStates.CompletedWithWarning or
                NovelImportRunStates.CleanupCompleted or
                NovelImportRunStates.CleanupBlocked or
                NovelImportRunStates.Failed or
                NovelImportRunStates.Cancelled => DefaultProgressTotal,
            NovelImportRunStates.CleanupPending => 1,
            _ => 0
        };
    }

    private static string ProgressMessage(string state, string stage)
    {
        return stage switch
        {
            "parse_source" => "正在解析源文件",
            "parse_failed" => "源文件解析失败",
            "create_novel" => "正在创建作品",
            "write_chapters" => "正在写入章节",
            "saving_metadata" => "正在保存元数据",
            "indexing" => "正在刷新搜索索引",
            "git_commit" => "正在创建 Git 导入提交",
            "cleanup_created_files" => "正在清理未完成导入",
            "cleanup_completed" => "失败导入已清理",
            "cleanup_blocked" => "失败导入需要手动清理",
            "cancelled" => "导入已取消",
            "failed" => "导入失败",
            "done" when state == NovelImportRunStates.CompletedWithWarning => "导入完成，但有警告",
            "done" => "导入完成",
            _ => state switch
            {
                NovelImportRunStates.Created => "导入任务已创建",
                NovelImportRunStates.CompletedWithWarning => "导入完成，但有警告",
                NovelImportRunStates.Completed => "导入完成",
                NovelImportRunStates.Failed => "导入失败",
                NovelImportRunStates.Cancelled => "导入已取消",
                _ => stage
            }
        };
    }

    private static ParsedImportDocument FromText(NovelImportTextParseResult result)
    {
        return new ParsedImportDocument(
            Title: null,
            Chapters: result.Chapters,
            SkippedChapters: result.SkippedChapters,
            Diagnostics: result.Diagnostics);
    }

    private static ParsedImportDocument FromEpub(NovelImportEpubParseResult result)
    {
        return new ParsedImportDocument(
            result.Title,
            result.Chapters,
            result.SkippedChapters,
            result.Diagnostics);
    }

    private static string ResolveNovelTitle(StartNovelImportPayload input, ParsedImportDocument parsed)
    {
        if (!string.IsNullOrWhiteSpace(input.RequestedTitle))
        {
            return input.RequestedTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(parsed.Title))
        {
            return parsed.Title.Trim();
        }

        var fileName = Path.GetFileNameWithoutExtension(input.SourceDisplayName);
        return string.IsNullOrWhiteSpace(fileName) ? "Imported Novel" : fileName.Trim();
    }

    private static string ResolveCommitMessage(StartNovelImportPayload input, string title)
    {
        return string.IsNullOrWhiteSpace(input.CommitMessage)
            ? $"import novel: {title}"
            : input.CommitMessage.Trim();
    }

    private static CopyableDiagnosticPayload Diagnostic(
        string code,
        string message,
        string detail,
        string taskId)
    {
        return new CopyableDiagnosticPayload(
            code,
            message,
            Truncate(detail),
            "StartNovelImport",
            taskId,
            null,
            "StartNovelImport",
            DateTimeOffset.UtcNow);
    }

    private static string JoinDiagnostics(IReadOnlyList<NovelImportDiagnosticPayload> diagnostics)
    {
        if (diagnostics.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Detail}"));
    }

    private static string Truncate(string value)
    {
        return Truncate(value, MaxDiagnosticDetailLength);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private sealed record ParsedImportDocument(
        string? Title,
        IReadOnlyList<NovelImportParsedChapter> Chapters,
        IReadOnlyList<NovelImportSkippedChapterPayload> SkippedChapters,
        IReadOnlyList<NovelImportDiagnosticPayload> Diagnostics);

    private sealed class ActiveImportRun
    {
        private readonly CancellationTokenSource _cancellation;

        public ActiveImportRun(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public TaskCompletionSource<NovelImportRunPayload> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token => _cancellation.Token;

        public string? CancellationReason { get; private set; }

        public void Cancel(string? reason)
        {
            CancellationReason = string.IsNullOrWhiteSpace(reason)
                ? "Cancellation requested."
                : reason.Trim();
            _cancellation.Cancel();
        }
    }

    private sealed class DeferredImportVersionControlService : IVersionControlService
    {
        public ValueTask EnsureRepositoryAsync(long novelId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<VersionControlCommitResult> CommitIfChangedAsync(
            long novelId,
            string message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new VersionControlCommitResult(false, string.Empty));
        }

        public ValueTask<IReadOnlyList<VersionControlCommitInfo>> GetLogAsync(
            long novelId,
            string? relativePath,
            int count,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<VersionControlCommitInfo>>([]);
        }

        public ValueTask<PageResultPayload<GitCommitSummaryPayload>> GetCommitSummariesAsync(
            GetGitCommitsPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = input.Page <= 0 ? 1 : input.Page;
            var size = input.Size <= 0 ? 20 : input.Size;
            return ValueTask.FromResult(new PageResultPayload<GitCommitSummaryPayload>([], 0, page, size, 0));
        }

        public ValueTask<IReadOnlyList<GitCommitFilePayload>> GetCommitFilesAsync(
            GetGitCommitFilesPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<GitCommitFilePayload>>([]);
        }

        public ValueTask<GitFileDiffPayload> GetFileDiffAsync(
            GetGitFileDiffPayload input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new VersionControlException("Deferred import version control does not expose Git diffs.");
        }
    }

    private sealed class ImportRagRefreshRecorder : IRagIndexRefreshNotifier
    {
        private readonly IRagIndexRefreshNotifier _target;

        public ImportRagRefreshRecorder(IRagIndexRefreshNotifier target)
        {
            _target = target;
        }

        public List<string> Failures { get; } = [];

        public async ValueTask MarkNovelIndexStaleAsync(
            long novelId,
            string reason,
            CancellationToken cancellationToken)
        {
            try
            {
                await _target.MarkNovelIndexStaleAsync(novelId, reason, cancellationToken);
            }
            catch (Exception ex)
            {
                Failures.Add(ex.Message);
            }
        }
    }
}
