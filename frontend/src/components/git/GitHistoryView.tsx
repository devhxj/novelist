import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Binary,
  ChevronDown,
  ChevronRight,
  Copy,
  FileCode2,
  FileMinus2,
  FilePlus2,
  FileText,
  GitCommitHorizontal,
  GitPullRequestArrow,
  Loader2,
  RefreshCw,
} from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { useRelativeTimeTicker } from '@/hooks/useRelativeTimeTicker'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { copyTextToClipboard } from '@/lib/clipboard'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import { formatAbsoluteDateTime, formatInteger, formatRelativeTime } from '@/lib/time'
import type { diagnostics, git } from '@/lib/novelist/types'

interface Props {
  novelId: number
}

type CommitFilesState = {
  loading: boolean
  error: string
  errorDiagnostic: diagnostics.CopyableDiagnostic | null
  files: git.GitCommitFile[]
}

const PAGE_SIZE = 3
const LOCALE = 'zh-CN'

export default function GitHistoryView({ novelId }: Props) {
  const app = useApp()
  const [commits, setCommits] = useState<git.GitCommitSummary[]>([])
  const [page, setPage] = useState(1)
  const [totalPages, setTotalPages] = useState(0)
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState('')
  const [errorDiagnostic, setErrorDiagnostic] = useState<diagnostics.CopyableDiagnostic | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const [filesByCommit, setFilesByCommit] = useState<Record<string, CommitFilesState>>({})
  const [selectedCommitId, setSelectedCommitId] = useState('')
  const [selectedFile, setSelectedFile] = useState<git.GitCommitFile | null>(null)
  const [diff, setDiff] = useState<git.GitFileDiff | null>(null)
  const [diffLoading, setDiffLoading] = useState(false)
  const [diffError, setDiffError] = useState('')
  const [diffErrorDiagnostic, setDiffErrorDiagnostic] = useState<diagnostics.CopyableDiagnostic | null>(null)
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const sentinelRef = useRef<HTMLDivElement | null>(null)
  const loadingRef = useRef(false)

  const hasMore = totalPages === 0 ? false : page < totalPages
  const nowMs = useRelativeTimeTicker(commits.map(commit => commit.committed_at), commits.length > 0)
  const selectedCommit = useMemo(
    () => commits.find(commit => commit.commit_id === selectedCommitId) ?? null,
    [commits, selectedCommitId],
  )

  const loadInitialCommits = useCallback(async () => {
    if (!novelId || loadingRef.current) return

    loadingRef.current = true
    setLoading(true)
    setError('')
    setErrorDiagnostic(null)

    try {
      const result = await app.GetGitCommits({
        novel_id: novelId,
        page: 1,
        size: PAGE_SIZE,
        cursor_commit_id: null,
      })
      const nextItems = result.items ?? []
      setCommits(nextItems)
      setPage(result.page || 1)
      setTotal(result.total ?? 0)
      setTotalPages(result.total_pages ?? 0)
      setExpanded(new Set())
      setFilesByCommit({})
      setSelectedCommitId('')
      setSelectedFile(null)
      setDiff(null)
      setDiffError('')
      setDiffErrorDiagnostic(null)
      setError('')
      setErrorDiagnostic(null)
    } catch (err) {
      setError(errorText(err, '加载 Git 历史失败'))
      setErrorDiagnostic(gitDiagnostic(err, '加载 Git 历史失败', 'GetGitCommits', {
        novel_id: novelId,
        page: 1,
        size: PAGE_SIZE,
      }))
      setCommits([])
      setTotal(0)
      setTotalPages(0)
    } finally {
      loadingRef.current = false
      setLoading(false)
    }
  }, [app, novelId])

  const loadMoreCommits = useCallback(async () => {
    if (!novelId || loadingRef.current || !hasMore) return

    const cursor = commits.at(-1)?.commit_id ?? null
    if (!cursor) return

    loadingRef.current = true
    setLoadingMore(true)

    try {
      const result = await app.GetGitCommits({
        novel_id: novelId,
        page: page + 1,
        size: PAGE_SIZE,
        cursor_commit_id: cursor,
      })
      const nextItems = result.items ?? []
      setCommits(current => mergeCommits(current, nextItems))
      setPage(result.page || page + 1)
      setTotal(result.total ?? total)
      setTotalPages(result.total_pages ?? totalPages)
      setError('')
      setErrorDiagnostic(null)
    } catch (err) {
      setError(errorText(err, '加载更早提交失败'))
      setErrorDiagnostic(gitDiagnostic(err, '加载更早提交失败', 'GetGitCommits', {
        novel_id: novelId,
        page: page + 1,
        size: PAGE_SIZE,
        cursor_commit_id: cursor,
      }))
    } finally {
      loadingRef.current = false
      setLoadingMore(false)
    }
  }, [app, commits, hasMore, novelId, page, total, totalPages])

  useEffect(() => {
    void loadInitialCommits()
  }, [loadInitialCommits])

  useEffect(() => {
    const node = sentinelRef.current
    if (!node || !hasMore) return

    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) {
        void loadMoreCommits()
      }
    }, { rootMargin: '160px' })
    observer.observe(node)
    return () => observer.disconnect()
  }, [hasMore, loadMoreCommits])

  const toggleCommit = useCallback(async (commit: git.GitCommitSummary) => {
    const willExpand = !expanded.has(commit.commit_id)
    setExpanded(current => {
      const next = new Set(current)
      if (willExpand) next.add(commit.commit_id)
      else next.delete(commit.commit_id)
      return next
    })

    if (!willExpand || filesByCommit[commit.commit_id]?.files.length || filesByCommit[commit.commit_id]?.loading) {
      return
    }

    setFilesByCommit(current => ({
      ...current,
      [commit.commit_id]: { loading: true, error: '', errorDiagnostic: null, files: [] },
    }))
    try {
      const files = await app.GetGitCommitFiles({
        novel_id: novelId,
        commit_id: commit.commit_id,
      })
      setFilesByCommit(current => ({
        ...current,
        [commit.commit_id]: { loading: false, error: '', errorDiagnostic: null, files: files ?? [] },
      }))
    } catch (err) {
      setFilesByCommit(current => ({
        ...current,
        [commit.commit_id]: {
          loading: false,
          error: errorText(err, '加载提交文件失败'),
          errorDiagnostic: gitDiagnostic(err, '加载提交文件失败', 'GetGitCommitFiles', {
            novel_id: novelId,
            commit_id: commit.commit_id,
          }),
          files: [],
        },
      }))
    }
  }, [app, expanded, filesByCommit, novelId])

  const retryCommitFiles = useCallback(async (commit: git.GitCommitSummary) => {
    setFilesByCommit(current => ({
      ...current,
      [commit.commit_id]: { loading: true, error: '', errorDiagnostic: null, files: current[commit.commit_id]?.files ?? [] },
    }))
    try {
      const files = await app.GetGitCommitFiles({
        novel_id: novelId,
        commit_id: commit.commit_id,
      })
      setFilesByCommit(current => ({
        ...current,
        [commit.commit_id]: { loading: false, error: '', errorDiagnostic: null, files: files ?? [] },
      }))
    } catch (err) {
      setFilesByCommit(current => ({
        ...current,
        [commit.commit_id]: {
          loading: false,
          error: errorText(err, '加载提交文件失败'),
          errorDiagnostic: gitDiagnostic(err, '加载提交文件失败', 'GetGitCommitFiles', {
            novel_id: novelId,
            commit_id: commit.commit_id,
          }),
          files: [],
        },
      }))
    }
  }, [app, novelId])

  const selectFile = useCallback(async (commit: git.GitCommitSummary, file: git.GitCommitFile) => {
    setSelectedCommitId(commit.commit_id)
    setSelectedFile(file)
    setDiff(null)
    setDiffError('')
    setDiffErrorDiagnostic(null)
    setDiffLoading(true)
    setCopyState('idle')

    try {
      const result = await app.GetGitFileDiff({
        novel_id: novelId,
        commit_id: commit.commit_id,
        path: file.path,
      })
      setDiff(result)
      setDiffError('')
      setDiffErrorDiagnostic(null)
    } catch (err) {
      setDiffError(errorText(err, '加载文件 diff 失败'))
      setDiffErrorDiagnostic(gitDiagnostic(err, '加载文件 diff 失败', 'GetGitFileDiff', {
        novel_id: novelId,
        commit_id: commit.commit_id,
        path: file.path,
      }))
    } finally {
      setDiffLoading(false)
    }
  }, [app, novelId])

  const copyDiffDiagnostics = useCallback(async () => {
    const payload = {
      selected_commit_id: selectedCommitId,
      selected_file: selectedFile,
      diff,
      error: diffError || error,
    }
    const diagnostic = buildCopyableDiagnostic({
      error: diffError || error || 'Git diff 诊断',
      fallbackMessage: 'Git diff 诊断',
      operation: 'GitHistoryDiff',
      bridgeMethod: 'GetGitFileDiff',
      detail: payload,
    })
    try {
      await copyTextToClipboard(JSON.stringify(diagnostic, null, 2))
      setCopyState('copied')
      window.setTimeout(() => setCopyState('idle'), 1800)
    } catch {
      setCopyState('failed')
      window.setTimeout(() => setCopyState('idle'), 1800)
    }
  }, [diff, diffError, error, selectedCommitId, selectedFile])

  return (
    <main className="flex min-w-0 flex-1 flex-col bg-background">
      <header className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-5 py-4">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <GitCommitHorizontal className="h-4 w-4 text-primary" />
            <h1 className="text-base font-semibold text-foreground">Git 历史</h1>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">
            {loading ? '加载中' : `${formatInteger(total, { locale: LOCALE })} 个提交`}
          </p>
        </div>
        <button
          type="button"
          onClick={() => { void loadInitialCommits() }}
          disabled={loading || loadingMore}
          className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background px-2.5 text-xs text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
          刷新
        </button>
      </header>

      {error && (
        <ErrorCallout
          title="Git 历史加载失败"
          message={error}
          diagnostic={errorDiagnostic}
          onRetry={() => { void loadInitialCommits() }}
          retrying={loading}
          className="border-x-0 border-t-0 px-4 py-3"
        />
      )}

      <div className="grid min-h-0 flex-1 grid-cols-1 xl:grid-cols-[minmax(360px,0.78fr)_minmax(0,1.22fr)]">
        <section className="min-h-0 border-b border-border xl:border-b-0 xl:border-r">
          {loading ? (
            <CommitSkeleton />
          ) : commits.length === 0 && !error ? (
            <EmptyHistory />
          ) : (
            <div className="h-full min-h-0 overflow-auto px-3 py-3" aria-label="Git 提交列表">
              <ol className="space-y-2">
                {commits.map(commit => (
                  <CommitItem
                    key={commit.commit_id}
                    commit={commit}
                    expanded={expanded.has(commit.commit_id)}
                    filesState={filesByCommit[commit.commit_id]}
                    selectedCommitId={selectedCommitId}
                    selectedPath={selectedFile?.path ?? ''}
                    nowMs={nowMs}
                    onToggle={() => { void toggleCommit(commit) }}
                    onRetryFiles={() => { void retryCommitFiles(commit) }}
                    onSelectFile={(file) => { void selectFile(commit, file) }}
                  />
                ))}
              </ol>
              <div ref={sentinelRef} className="h-10" aria-hidden="true" />
              <div className="flex items-center justify-center py-2 text-xs text-muted-foreground">
                {loadingMore ? (
                  <span className="inline-flex items-center gap-2">
                    <Loader2 className="h-3.5 w-3.5 animate-spin" />
                    加载更早提交
                  </span>
                ) : hasMore ? (
                  <button
                    type="button"
                    onClick={() => { void loadMoreCommits() }}
                    className="h-8 rounded-md border border-border bg-background px-3 text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  >
                    加载更早提交
                  </button>
                ) : commits.length > 0 ? (
                  <span>已到最早提交</span>
                ) : null}
              </div>
            </div>
          )}
        </section>

        <DiffPanel
          commit={selectedCommit}
          file={selectedFile}
          diff={diff}
          loading={diffLoading}
          error={diffError}
          errorDiagnostic={diffErrorDiagnostic}
          copyState={copyState}
          onRetry={() => {
            if (selectedCommit && selectedFile) {
              void selectFile(selectedCommit, selectedFile)
            }
          }}
          onCopy={() => { void copyDiffDiagnostics() }}
        />
      </div>
    </main>
  )
}

function CommitItem({
  commit,
  expanded,
  filesState,
  selectedCommitId,
  selectedPath,
  nowMs,
  onToggle,
  onRetryFiles,
  onSelectFile,
}: {
  commit: git.GitCommitSummary
  expanded: boolean
  filesState?: CommitFilesState
  selectedCommitId: string
  selectedPath: string
  nowMs: number
  onToggle: () => void
  onRetryFiles: () => void
  onSelectFile: (file: git.GitCommitFile) => void
}) {
  const isSelectedCommit = selectedCommitId === commit.commit_id
  const committedAtTitle = formatAbsoluteDateTime(commit.committed_at, { locale: LOCALE })
  return (
    <li className={`rounded-md border bg-card transition-colors ${isSelectedCommit ? 'border-primary' : 'border-border'}`}>
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={expanded}
        className="flex w-full min-w-0 items-start gap-2 px-3 py-3 text-left transition-colors hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <span className="mt-0.5 shrink-0 text-muted-foreground">
          {expanded ? <ChevronDown className="h-4 w-4" /> : <ChevronRight className="h-4 w-4" />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-sm font-semibold text-foreground">{commit.message}</span>
          <span className="mt-1 flex flex-wrap gap-x-2 gap-y-1 text-[11px] text-muted-foreground">
            <span className="font-mono">{commit.short_commit_id}</span>
            <span>{commit.author_name}</span>
            <span title={committedAtTitle}>
              {formatRelativeTime(commit.committed_at, { now: nowMs, locale: LOCALE })}
            </span>
          </span>
          <span className="mt-2 flex flex-wrap gap-1.5 text-[11px]">
            <span className="rounded bg-muted px-2 py-0.5 text-muted-foreground">{formatInteger(commit.changed_file_count, { locale: LOCALE })} 文件</span>
            <span className="rounded bg-tag-green px-2 py-0.5 text-tag-green-foreground">+{formatInteger(commit.insertions, { locale: LOCALE })}</span>
            <span className="rounded bg-tag-rose px-2 py-0.5 text-tag-rose-foreground">-{formatInteger(commit.deletions, { locale: LOCALE })}</span>
          </span>
        </span>
      </button>

      {expanded && (
        <div className="border-t border-border px-2 py-2">
          {filesState?.loading ? (
            <div className="flex items-center gap-2 px-2 py-3 text-xs text-muted-foreground">
              <Loader2 className="h-3.5 w-3.5 animate-spin" />
              加载变更文件
            </div>
          ) : filesState?.error ? (
            <ErrorCallout
              title="加载提交文件失败"
              message={filesState.error}
              diagnostic={filesState.errorDiagnostic}
              onRetry={onRetryFiles}
              compact
              className="rounded-md"
            />
          ) : filesState?.files.length ? (
            <ul className="space-y-1">
              {filesState.files.map(file => (
                <li key={`${commit.commit_id}:${file.old_path ?? ''}:${file.path}`}>
                  <FileButton
                    file={file}
                    selected={isSelectedCommit && selectedPath === file.path}
                    onClick={() => onSelectFile(file)}
                  />
                </li>
              ))}
            </ul>
          ) : (
            <div className="px-2 py-3 text-xs text-muted-foreground">没有文件变更</div>
          )}
        </div>
      )}
    </li>
  )
}

function FileButton({
  file,
  selected,
  onClick,
}: {
  file: git.GitCommitFile
  selected: boolean
  onClick: () => void
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`flex w-full min-w-0 items-start gap-2 rounded-md px-2 py-2 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
        selected ? 'bg-primary text-primary-foreground' : 'hover:bg-muted'
      }`}
    >
      <FileChangeIcon binary={file.binary} changeType={file.change_type} />
      <span className="min-w-0 flex-1">
        <span className="block truncate text-xs font-medium">{file.path}</span>
        {file.old_path && (
          <span className="mt-0.5 block truncate text-[11px] opacity-80">{file.old_path} {'->'} {file.path}</span>
        )}
        <span className="mt-1 flex flex-wrap gap-1 text-[11px] opacity-90">
          <ChangeBadge changeType={file.change_type} selected={selected} />
          {file.binary ? <span>二进制</span> : <span>+{formatInteger(file.additions, { locale: LOCALE })} -{formatInteger(file.deletions, { locale: LOCALE })}</span>}
        </span>
      </span>
    </button>
  )
}

function FileChangeIcon({ binary, changeType }: { binary: boolean; changeType: string }) {
  const className = 'mt-0.5 h-4 w-4 shrink-0'

  if (binary) return <Binary className={className} />

  switch (changeType) {
    case 'added':
      return <FilePlus2 className={className} />
    case 'deleted':
      return <FileMinus2 className={className} />
    case 'renamed':
      return <GitPullRequestArrow className={className} />
    default:
      return <FileText className={className} />
  }
}

function DiffPanel({
  commit,
  file,
  diff,
  loading,
  error,
  errorDiagnostic,
  copyState,
  onRetry,
  onCopy,
}: {
  commit: git.GitCommitSummary | null
  file: git.GitCommitFile | null
  diff: git.GitFileDiff | null
  loading: boolean
  error: string
  errorDiagnostic: diagnostics.CopyableDiagnostic | null
  copyState: 'idle' | 'copied' | 'failed'
  onRetry: () => void
  onCopy: () => void
}) {
  const copyLabel = copyState === 'copied' ? '已复制' : copyState === 'failed' ? '复制失败' : '复制诊断'
  return (
    <section className="flex min-h-0 min-w-0 flex-col">
      <header className="flex min-h-14 items-center justify-between gap-3 border-b border-border px-4 py-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <FileCode2 className="h-4 w-4 text-primary" />
            <h2 className="truncate text-sm font-semibold text-foreground">{file?.path ?? '文件差异'}</h2>
          </div>
          <p className="mt-1 truncate text-xs text-muted-foreground">
            {commit ? `${commit.short_commit_id} · ${commit.message}` : '未选择文件'}
          </p>
        </div>
        {(diff || error) && (
          <button
            type="button"
            onClick={onCopy}
            className="inline-flex h-8 shrink-0 items-center gap-1.5 rounded-md border border-border bg-background px-2.5 text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <Copy className="h-3.5 w-3.5" />
            {copyLabel}
          </button>
        )}
      </header>

      {loading ? (
        <div className="flex flex-1 items-center justify-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="h-4 w-4 animate-spin" />
          加载 diff
        </div>
      ) : error ? (
        <div className="m-4">
          <ErrorCallout
            title="加载文件 diff 失败"
            message={error}
            diagnostic={errorDiagnostic}
            onRetry={onRetry}
            retrying={loading}
            className="rounded-md"
          />
        </div>
      ) : diff ? (
        <div className="min-h-0 flex-1 overflow-auto p-4">
          <div className="mb-3 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            <ChangeBadge changeType={diff.change_type} />
            {diff.old_path && <span className="rounded border border-border bg-background px-2 py-1">{diff.old_path} {'->'} {diff.path}</span>}
            {diff.binary && <span className="rounded border border-border bg-background px-2 py-1">二进制</span>}
            {diff.truncated && <span className="rounded border border-danger-border bg-danger-bg px-2 py-1 text-foreground">内容已截断</span>}
          </div>
          {diff.binary ? (
            <div className="rounded-md border border-border bg-card px-4 py-8 text-center text-sm text-muted-foreground">
              二进制文件不展示文本 diff
            </div>
          ) : (
            <div className="grid grid-cols-1 gap-3 2xl:grid-cols-2">
              <ContentBlock title="原始内容" value={diff.original_content} emptyText="无原始内容" />
              <ContentBlock title="修改后内容" value={diff.modified_content} emptyText="无修改后内容" />
              <div className="2xl:col-span-2">
                <ContentBlock title="变更补丁" value={diff.diff_text} emptyText="无补丁内容" />
              </div>
            </div>
          )}
        </div>
      ) : (
        <div className="flex flex-1 items-center justify-center px-6 text-center text-sm text-muted-foreground">
          选择提交中的文件查看只读 diff
        </div>
      )}
    </section>
  )
}

function ContentBlock({ title, value, emptyText }: { title: string; value?: string | null; emptyText: string }) {
  return (
    <section className="min-w-0 rounded-md border border-border bg-card">
      <div className="border-b border-border px-3 py-2 text-xs font-medium text-foreground">{title}</div>
      {value ? (
        <pre className="max-h-[36rem] overflow-auto whitespace-pre-wrap break-words px-3 py-3 font-mono text-xs leading-relaxed text-foreground">
          {value}
        </pre>
      ) : (
        <div className="px-3 py-8 text-center text-sm text-muted-foreground">{emptyText}</div>
      )}
    </section>
  )
}

function EmptyHistory() {
  return (
    <div className="flex h-full min-h-64 items-center justify-center px-6 text-center">
      <div>
        <GitCommitHorizontal className="mx-auto h-8 w-8 text-muted-foreground" />
        <h2 className="mt-3 text-sm font-medium text-foreground">暂无 Git 提交</h2>
        <p className="mt-1 text-sm text-muted-foreground">仓库还没有可展示的历史记录。</p>
      </div>
    </div>
  )
}

function CommitSkeleton() {
  return (
    <div className="space-y-2 px-3 py-3" aria-busy="true" aria-label="正在加载 Git 历史">
      {[0, 1, 2].map(index => (
        <div key={index} className="h-28 animate-pulse rounded-md border border-border bg-card" />
      ))}
    </div>
  )
}

function ChangeBadge({ changeType, selected = false }: { changeType: string; selected?: boolean }) {
  const label = changeTypeLabel(changeType)
  return (
    <span className={selected ? 'rounded bg-background/20 px-1.5 py-0.5' : 'rounded border border-border bg-background px-2 py-1'}>
      {label}
    </span>
  )
}

function changeTypeLabel(changeType: string): string {
  switch (changeType) {
    case 'added':
      return '新增'
    case 'deleted':
      return '删除'
    case 'renamed':
      return '重命名'
    case 'modified':
      return '修改'
    default:
      return changeType || '变更'
  }
}

function mergeCommits(
  current: git.GitCommitSummary[],
  nextItems: git.GitCommitSummary[],
): git.GitCommitSummary[] {
  const seen = new Set(current.map(commit => commit.commit_id))
  const merged = [...current]
  for (const commit of nextItems) {
    if (!seen.has(commit.commit_id)) {
      seen.add(commit.commit_id)
      merged.push(commit)
    }
  }
  return merged
}

function errorText(error: unknown, fallback: string): string {
  return diagnosticMessage(error, fallback)
}

function gitDiagnostic(
  error: unknown,
  fallbackMessage: string,
  bridgeMethod: string,
  detail: Record<string, unknown>,
): diagnostics.CopyableDiagnostic {
  return buildCopyableDiagnostic({
    error,
    fallbackMessage,
    operation: bridgeMethod,
    bridgeMethod,
    detail,
  })
}
