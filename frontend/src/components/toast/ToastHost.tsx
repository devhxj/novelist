import { useSyncExternalStore } from 'react'
import { CheckCircle2, Info, X, XCircle } from 'lucide-react'
import { dismissToast, getToastSnapshot, subscribeToasts, type ToastItem, type ToastKind } from '@/lib/toast'

const KIND_ICON: Record<ToastKind, typeof Info> = {
  info: Info,
  success: CheckCircle2,
  error: XCircle,
}

const KIND_STYLE: Record<ToastKind, string> = {
  info: 'border-border text-foreground',
  success: 'border-emerald-500/40 text-foreground',
  error: 'border-destructive/45 text-foreground',
}

function ToastCard({ toast }: { toast: ToastItem }) {
  const Icon = KIND_ICON[toast.kind]
  return (
    <div
      role={toast.kind === 'error' ? 'alert' : undefined}
      data-testid={`toast-${toast.kind}`}
      className={`flex w-80 items-start gap-2 rounded-lg border bg-card px-3 py-2.5 shadow-lg ${KIND_STYLE[toast.kind]}`}
    >
      <Icon className={`mt-0.5 h-4 w-4 shrink-0 ${toast.kind === 'error' ? 'text-destructive' : toast.kind === 'success' ? 'text-emerald-600' : 'text-muted-foreground'}`} aria-hidden="true" />
      <div className="min-w-0 flex-1">
        <p className="text-xs font-medium leading-5 break-words">{toast.message}</p>
        {toast.description && (
          <p className="mt-0.5 text-[11px] leading-4 text-muted-foreground break-words">{toast.description}</p>
        )}
        {toast.action && (
          <button
            type="button"
            onClick={() => {
              toast.action?.run()
              dismissToast(toast.id)
            }}
            className="mt-1 rounded text-[11px] font-medium text-primary underline-offset-2 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {toast.action.label}
          </button>
        )}
      </div>
      <button
        type="button"
        onClick={() => dismissToast(toast.id)}
        aria-label="关闭通知"
        className="shrink-0 rounded p-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
      >
        <X className="h-3.5 w-3.5" aria-hidden="true" />
      </button>
    </div>
  )
}

// 全局通知出口（F9）：整块容器是 polite live region，
// 错误条目再单独带 role="alert"，读屏器与普通作者都不会错过后台结果。
export default function ToastHost() {
  const toasts = useSyncExternalStore(subscribeToasts, getToastSnapshot)
  if (toasts.length === 0) return null
  return (
    <div
      role="status"
      aria-live="polite"
      aria-label="应用通知"
      data-testid="toast-host"
      className="fixed bottom-14 right-4 z-[70] flex flex-col gap-2"
    >
      {toasts.map((toast) => <ToastCard key={toast.id} toast={toast} />)}
    </div>
  )
}
