import { CircleAlert } from 'lucide-react'

interface Props {
  message: string
  /** 命中错误码映射时的后端原始消息（N5）：折叠展示，供排查与报障。 */
  detail?: string | null
  onRetry?: () => void
  onClose?: () => void
  className?: string
}

// 参考书面板的统一错误条（N5）：作者先看到"发生了什么"，命中错误码映射时
// 后端原始消息以折叠诊断呈现——映射文案偏泛时，具体证据不再丢失。
export default function ReferenceErrorStrip({ message, detail, onRetry, onClose, className }: Props) {
  return (
    <div
      className={`flex flex-col gap-1.5 rounded-md border border-destructive/30 bg-destructive/5 px-2.5 py-2 text-xs text-destructive ${className ?? ''}`}
      role="alert"
    >
      <div className="flex items-start gap-2">
        <CircleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
        <span className="min-w-0 flex-1 break-words">{message}</span>
        {onRetry && (
          <button
            type="button"
            onClick={onRetry}
            className="shrink-0 self-center rounded border border-destructive/40 px-2 py-0.5 text-[11px] font-medium hover:bg-destructive/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            重试
          </button>
        )}
        {onClose && (
          <button
            type="button"
            onClick={onClose}
            aria-label="关闭错误提示"
            className="shrink-0 self-center rounded px-1.5 py-0.5 text-[11px] hover:bg-destructive/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            关闭
          </button>
        )}
      </div>
      {detail && (
        <details className="text-[11px] text-destructive/80">
          <summary className="cursor-pointer select-none">诊断详情</summary>
          <pre className="mt-1 whitespace-pre-wrap break-words font-mono">{detail}</pre>
        </details>
      )}
    </div>
  )
}
