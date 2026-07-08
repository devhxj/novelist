import { useState } from 'react'
import { BellRing, Download, ExternalLink, X } from 'lucide-react'
import Markdown from '@/components/Markdown'
import { runtime } from '@/lib/novelist/runtime'
import type { update } from '@/lib/novelist/types'

interface Props {
  result: update.UpdateCheckResult | null
  open: boolean
  onClose: () => void
  onDismissVersion?: (version: string) => Promise<void> | void
}

export default function UpdateDialog({ result, open, onClose, onDismissVersion }: Props) {
  const [actionError, setActionError] = useState('')
  const [dismissing, setDismissing] = useState(false)

  if (!open || !result) return null

  const latestVersion = result.latest_version || ''
  const releaseUrl = result.release_url || ''
  const downloadUrl = result.download_url || ''

  async function openUrl(url: string) {
    if (!url) return
    setActionError('')
    try {
      await runtime.shell.openExternal(url)
    } catch (error) {
      setActionError(error instanceof Error ? error.message : '打开链接失败')
    }
  }

  async function dismissVersion() {
    if (!latestVersion || !onDismissVersion) {
      onClose()
      return
    }

    setDismissing(true)
    setActionError('')
    try {
      await onDismissVersion(latestVersion)
      onClose()
    } catch (error) {
      setActionError(error instanceof Error ? error.message : '忽略版本失败')
    } finally {
      setDismissing(false)
    }
  }

  return (
    <div className="fixed inset-0 z-[60] flex items-center justify-center px-4">
      <div className="absolute inset-0 bg-black/45" onClick={onClose} />
      <section
        role="dialog"
        aria-modal="true"
        aria-labelledby="update-dialog-title"
        className="relative flex max-h-[86vh] w-full max-w-[680px] flex-col overflow-hidden rounded-lg border bg-background shadow-2xl"
      >
        <header className="flex shrink-0 items-start gap-3 border-b px-5 py-4">
          <div className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">
            <BellRing className="h-5 w-5" />
          </div>
          <div className="min-w-0 flex-1">
            <h2 id="update-dialog-title" className="text-base font-semibold text-foreground">
              发现新版本 {latestVersion}
            </h2>
            <p className="mt-1 text-xs text-muted-foreground">
              当前版本 {result.current_version}
              {result.release_name ? ` · ${result.release_name}` : ''}
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="flex h-8 w-8 shrink-0 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
            aria-label="关闭更新提示"
          >
            <X className="h-4 w-4" />
          </button>
        </header>

        <div className="min-h-0 flex-1 overflow-y-auto px-5 py-4">
          {result.release_notes ? (
            <Markdown content={result.release_notes} className="update-release-notes" />
          ) : (
            <p className="text-sm text-muted-foreground">此版本没有提供发布说明。</p>
          )}
          {actionError && (
            <p role="alert" className="mt-3 rounded-md border border-danger-border bg-danger-bg px-3 py-2 text-xs text-destructive">
              {actionError}
            </p>
          )}
        </div>

        <footer className="flex shrink-0 flex-wrap items-center justify-between gap-2 border-t px-5 py-3">
          <button
            type="button"
            onClick={() => void dismissVersion()}
            disabled={dismissing}
            className="h-8 rounded-md px-3 text-xs text-muted-foreground transition-colors hover:bg-muted hover:text-foreground disabled:opacity-50"
          >
            {dismissing ? '处理中...' : '忽略此版本'}
          </button>
          <div className="flex flex-wrap items-center justify-end gap-2">
            {downloadUrl && (
              <button
                type="button"
                onClick={() => void openUrl(downloadUrl)}
                className="inline-flex h-8 items-center gap-1.5 rounded-md border px-3 text-xs transition-colors hover:bg-muted"
              >
                <Download className="h-3.5 w-3.5" />
                下载
              </button>
            )}
            {releaseUrl && (
              <button
                type="button"
                onClick={() => void openUrl(releaseUrl)}
                className="inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground transition-opacity hover:opacity-90"
              >
                <ExternalLink className="h-3.5 w-3.5" />
                查看发布页
              </button>
            )}
          </div>
        </footer>
      </section>
    </div>
  )
}
