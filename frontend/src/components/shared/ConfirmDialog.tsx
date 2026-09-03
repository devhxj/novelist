import { useState } from 'react'
import { useDialogA11y } from '@/hooks/useDialogA11y'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics } from '@/lib/novelist/types'

interface Props {
  open: boolean
  title: string
  /** 描述要删除的对象与后果（正文/大纲将一并移除、章号不复用等）。 */
  description: string
  confirmLabel?: string
  onClose: () => void
  onConfirm: () => Promise<void> | void
}

// A5：应用内删除确认对话框——替代原生 window.confirm，与主题一致。
// 确认动作失败时在对话框内给出可复制诊断，不打断流程。
export default function ConfirmDialog({ open, title, description, confirmLabel = '确认删除', onClose, onConfirm }: Props) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [errorDiagnostic, setErrorDiagnostic] = useState<diagnostics.CopyableDiagnostic | null>(null)
  const dialogProps = useDialogA11y(open, onClose, title)

  if (!open) return null

  async function handleConfirm() {
    if (busy) return
    setBusy(true)
    setError('')
    setErrorDiagnostic(null)
    try {
      await onConfirm()
      onClose()
    } catch (e: unknown) {
      setError(diagnosticMessage(e, '操作失败，请重试'))
      setErrorDiagnostic(buildCopyableDiagnostic({ error: e, fallbackMessage: title, operation: title, bridgeMethod: null }))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} />
      <div
        {...dialogProps}
        className="relative w-[420px] max-w-[92vw] rounded-xl border bg-background p-5 shadow-2xl"
        data-testid="confirm-dialog"
      >
        <h2 className="text-sm font-semibold text-foreground">{title}</h2>
        <p className="mt-2 whitespace-pre-wrap text-xs leading-5 text-muted-foreground">{description}</p>
        {error && (
          <div className="mt-3">
            <ErrorCallout compact message={error} diagnostic={errorDiagnostic} onClose={() => { setError(''); setErrorDiagnostic(null) }} />
          </div>
        )}
        <div className="mt-4 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            disabled={busy}
            className="h-8 rounded-md border border-border px-3 text-xs text-muted-foreground hover:bg-secondary disabled:opacity-50"
          >
            取消
          </button>
          <button
            type="button"
            onClick={() => { void handleConfirm() }}
            disabled={busy}
            data-testid="confirm-dialog-delete"
            className="h-8 rounded-md bg-destructive px-3 text-xs font-medium text-destructive-foreground hover:bg-destructive/90 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {busy ? '处理中…' : confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
