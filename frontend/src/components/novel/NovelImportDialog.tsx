import type { ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Loader2, Upload, X, XCircle } from 'lucide-react'
import type { novelImport } from '@/lib/novelist/types'
import type { NovelImportUiState } from '@/hooks/useNovelImport'
import { canCloseImportState } from '@/hooks/useNovelImport'

interface Props {
  state: NovelImportUiState
  onCancel: () => void
  onClose: () => void
}

export default function NovelImportDialog({ state, onCancel, onClose }: Props) {
  if (state.status === 'idle') return null

  const progress = state.progress
  const run = state.run
  const percent = progressPercent(progress)
  const closeable = canCloseImportState(state)
  const inProgress = state.status === 'running' || state.status === 'selecting' || state.status === 'cancelling'
  const cancelling = state.status === 'cancelling'
  const tone = dialogTone(state)
  const title = dialogTitle(state)
  const sourceName = run?.source_display_name ?? state.input?.source_display_name ?? '待选择文件'
  const skippedChapters = run?.skipped_chapters ?? []
  const warnings = run?.warnings ?? []
  const diagnostics = run?.diagnostics ?? []
  const errorMessage = state.errorMessage || run?.error?.message || ''

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-background/70 px-4 py-6 backdrop-blur-sm">
      <section
        role="dialog"
        aria-modal="true"
        aria-labelledby="novel-import-dialog-title"
        className="flex max-h-[min(720px,92vh)] w-full max-w-xl flex-col rounded-lg border border-border bg-card shadow-xl"
      >
        <header className="flex items-start justify-between gap-3 border-b px-5 py-4">
          <div className="flex min-w-0 gap-3">
            <div className={`mt-0.5 shrink-0 ${tone.iconClass}`}>
              {tone.icon}
            </div>
            <div className="min-w-0">
              <h2 id="novel-import-dialog-title" className="text-base font-semibold text-foreground">
                {title}
              </h2>
              <p className="mt-1 break-words text-xs text-muted-foreground">{sourceName}</p>
            </div>
          </div>
          {closeable && (
            <button
              type="button"
              onClick={onClose}
              aria-label="关闭导入结果"
              className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <X className="h-4 w-4" />
            </button>
          )}
        </header>

        <div className="min-h-0 overflow-y-auto px-5 py-4">
          <div className="space-y-4">
            <div>
              <div className="mb-2 flex items-center justify-between gap-3 text-xs">
                <span className="font-medium text-foreground">{stageText(progress, run)}</span>
                <span className="tabular-nums text-muted-foreground">
                  {percent}%
                  {progress && (
                    <span className="ml-2">
                      {progress.progress_completed}/{progress.progress_total}
                    </span>
                  )}
                </span>
              </div>
              <div
                className="h-2 overflow-hidden rounded-full bg-muted"
                role="progressbar"
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={percent}
                aria-label="导入进度"
              >
                <div className={`h-full rounded-full transition-all ${tone.progressClass}`} style={{ width: `${percent}%` }} />
              </div>
            </div>

            {progress?.message && (
              <p className="rounded-md border border-border bg-muted/40 px-3 py-2 text-sm text-foreground">
                {progress.message}
              </p>
            )}

            {progress?.current_chapter_title && (
              <div className="rounded-md border border-border px-3 py-2 text-sm">
                <div className="text-xs font-medium text-muted-foreground">当前章节</div>
                <div className="mt-1 break-words text-foreground">
                  {progress.current_chapter_index ? `第 ${progress.current_chapter_index} 个：` : null}
                  {progress.current_chapter_title}
                </div>
              </div>
            )}

            {run?.created_novel_id && (state.status === 'completed' || state.status === 'warning') && (
              <div className="rounded-md border border-primary/25 bg-primary/8 px-3 py-2 text-sm text-foreground">
                <div className="font-medium">已导入：{titleFromDisplayName(sourceName)}</div>
                <div className="mt-1 text-xs text-muted-foreground">作品 ID：{run.created_novel_id}</div>
              </div>
            )}

            {skippedChapters.length > 0 && (
              <section className="rounded-md border border-border px-3 py-2">
                <h3 className="text-sm font-medium text-foreground">跳过 {skippedChapters.length} 章</h3>
                <div className="mt-2 max-h-28 space-y-1 overflow-y-auto text-xs text-muted-foreground">
                  {skippedChapters.slice(0, 20).map(chapter => (
                    <div key={`${chapter.index}-${chapter.reason}`} className="break-words">
                      #{chapter.index} {chapter.title || '未命名章节'} · {chapter.reason}
                    </div>
                  ))}
                  {skippedChapters.length > 20 && (
                    <div>还有 {skippedChapters.length - 20} 条未显示</div>
                  )}
                </div>
              </section>
            )}

            {warnings.length > 0 && (
              <section className="rounded-md border border-tag-amber bg-tag-amber px-3 py-2">
                <h3 className="text-sm font-medium text-foreground">导入警告</h3>
                <div className="mt-2 space-y-1 text-xs text-muted-foreground">
                  {warnings.map(warning => (
                    <div key={`${warning.code}-${warning.message}`} className="break-words">
                      {warning.message}
                      {warning.detail ? <span>：{warning.detail}</span> : null}
                    </div>
                  ))}
                </div>
              </section>
            )}

            {(errorMessage || diagnostics.length > 0) && (
              <section className="rounded-md border border-danger-border bg-danger-bg px-3 py-2">
                <h3 className="text-sm font-medium text-destructive">导入未完成</h3>
                {errorMessage && <p className="mt-1 break-words text-sm text-foreground">{errorMessage}</p>}
                {diagnostics.length > 0 && (
                  <div className="mt-2 max-h-28 space-y-1 overflow-y-auto text-xs text-muted-foreground">
                    {diagnostics.slice(0, 12).map(diagnostic => (
                      <div key={`${diagnostic.code}-${diagnostic.message}`} className="break-words">
                        {diagnostic.code} · {diagnostic.message}
                      </div>
                    ))}
                  </div>
                )}
              </section>
            )}
          </div>
        </div>

        <footer className="flex items-center justify-between gap-3 border-t px-5 py-3">
          <div className="min-w-0 text-xs text-muted-foreground">
            {state.taskId && <span className="break-all">任务：{state.taskId}</span>}
          </div>
          <div className="flex shrink-0 items-center gap-2">
            {inProgress && state.taskId && (
              <button
                type="button"
                onClick={onCancel}
                disabled={cancelling}
                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background px-3 text-sm text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-60"
              >
                {cancelling ? <Loader2 className="h-4 w-4 animate-spin" /> : <XCircle className="h-4 w-4" />}
                {cancelling ? '正在取消' : '取消导入'}
              </button>
            )}
            {closeable && (
              <button
                type="button"
                onClick={onClose}
                className="inline-flex h-8 items-center rounded-md bg-primary px-3 text-sm text-primary-foreground transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                完成
              </button>
            )}
          </div>
        </footer>
      </section>
    </div>
  )
}

function dialogTitle(state: NovelImportUiState): string {
  switch (state.status) {
    case 'selecting':
      return '选择导入文件'
    case 'completed':
      return '小说导入完成'
    case 'warning':
      return '小说导入完成，有警告'
    case 'error':
      return '小说导入失败'
    case 'cancelled':
      return '小说导入已取消'
    case 'cancelling':
      return '正在取消导入'
    default:
      return '正在导入小说'
  }
}

function dialogTone(state: NovelImportUiState): {
  icon: ReactNode
  iconClass: string
  progressClass: string
} {
  switch (state.status) {
    case 'completed':
      return {
        icon: <CheckCircle2 className="h-5 w-5" />,
        iconClass: 'text-primary',
        progressClass: 'bg-primary',
      }
    case 'warning':
      return {
        icon: <AlertTriangle className="h-5 w-5" />,
        iconClass: 'text-tag-amber-foreground',
        progressClass: 'bg-tag-amber-foreground',
      }
    case 'error':
      return {
        icon: <AlertTriangle className="h-5 w-5" />,
        iconClass: 'text-destructive',
        progressClass: 'bg-destructive',
      }
    case 'cancelled':
      return {
        icon: <XCircle className="h-5 w-5" />,
        iconClass: 'text-muted-foreground',
        progressClass: 'bg-muted-foreground',
      }
    default:
      return {
        icon: state.status === 'selecting' ? <Upload className="h-5 w-5" /> : <Loader2 className="h-5 w-5 animate-spin" />,
        iconClass: 'text-primary',
        progressClass: 'bg-primary',
      }
  }
}

function progressPercent(progress: novelImport.ImportProgress | null): number {
  if (!progress || progress.progress_total <= 0) return 0
  return Math.max(0, Math.min(100, Math.round((progress.progress_completed / progress.progress_total) * 100)))
}

function stageText(progress: novelImport.ImportProgress | null, run: novelImport.ImportRun | null): string {
  const stage = progress?.stage ?? run?.stage ?? ''
  switch (stage) {
    case 'selecting':
      return '选择文件'
    case 'created':
      return '创建任务'
    case 'parse_source':
      return '解析源文件'
    case 'parse_failed':
      return '解析失败'
    case 'create_novel':
      return '创建作品'
    case 'write_chapters':
    case 'write_chapter':
      return '写入章节'
    case 'saving_metadata':
      return '保存元数据'
    case 'indexing':
      return '刷新索引'
    case 'git_commit':
      return 'Git 提交'
    case 'done':
      return '完成'
    case 'cleanup_created_files':
      return '清理未完成导入'
    case 'cleanup_completed':
      return '清理完成'
    case 'cleanup_blocked':
      return '清理受阻'
    case 'cancelled':
      return '已取消'
    default:
      return stage || '准备导入'
  }
}

function titleFromDisplayName(displayName: string): string {
  return displayName.replace(/\.(epub|txt|md|markdown)$/i, '').trim() || displayName
}
