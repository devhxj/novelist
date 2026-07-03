import { useState } from 'react'
import { BookOpen, FileText, AlignLeft } from 'lucide-react'

interface Props {
  open: boolean
  novelTitle: string
  onClose: () => void
  onExport: (format: 'epub' | 'markdown' | 'txt') => Promise<void>
}

const FORMATS = [
  {
    id: 'epub' as const,
    label: 'EPUB',
    desc: '电子书格式，支持章节导航和封面，可用各类阅读器打开',
    icon: BookOpen,
  },
  {
    id: 'markdown' as const,
    label: 'Markdown',
    desc: '合并所有章节到一个 Markdown 文件，含目录和元信息',
    icon: FileText,
  },
  {
    id: 'txt' as const,
    label: 'TXT',
    desc: '纯文本格式，原样输出正文内容，通用性最强',
    icon: AlignLeft,
  },
] as const

function errorMessage(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback
}

export default function ExportDialog({ open, novelTitle, onClose, onExport }: Props) {
  const [format, setFormat] = useState<'epub' | 'markdown' | 'txt'>('epub')
  const [exporting, setExporting] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState(false)

  if (!open) return null

  async function handleExport() {
    if (exporting) return
    setExporting(true)
    setError('')
    setSuccess(false)
    try {
      await onExport(format)
      setSuccess(true)
    } catch (e: unknown) {
      setError(errorMessage(e, '导出失败，请重试'))
    } finally {
      setExporting(false)
    }
  }

  function handleKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'Escape') onClose()
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} />
      <div
        className="relative bg-background rounded-xl shadow-2xl border w-[420px] max-w-[90vw] p-6"
        onKeyDown={handleKeyDown}
      >
        <button
          onClick={onClose}
          className="absolute top-3 right-3 w-7 h-7 flex items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
        >
          ✕
        </button>

        <h2 className="text-base font-semibold mb-1">导出作品</h2>
        <p className="text-sm text-muted-foreground mb-5">{novelTitle}</p>

        {error && (
          <p className="text-sm text-red-600 bg-danger-bg border border-danger-border rounded-md px-3 py-2 mb-4">{error}</p>
        )}

        {success && (
          <p className="text-sm text-emerald-600 bg-emerald-50 border border-emerald-200 rounded-md px-3 py-2 mb-4">
            ✓ 导出成功
          </p>
        )}

        <div className="space-y-2">
          {FORMATS.map(f => (
            <button
              key={f.id}
              onClick={() => setFormat(f.id)}
              className={`w-full flex items-start gap-3 p-3 rounded-lg border text-left transition-colors
                ${format === f.id
                  ? 'ring-2 ring-primary border-primary/30 bg-primary/5'
                  : 'border-border hover:bg-muted/50'}`}
            >
              <f.icon className={`w-5 h-5 mt-0.5 shrink-0 ${format === f.id ? 'text-primary' : 'text-muted-foreground'}`} />
              <div>
                <span className={`text-sm font-medium ${format === f.id ? 'text-primary' : 'text-foreground'}`}>
                  {f.label}
                </span>
                <p className="text-xs text-muted-foreground mt-0.5">{f.desc}</p>
              </div>
            </button>
          ))}
        </div>

        <div className="flex justify-end gap-2 mt-6">
          {success ? (
            <button
              onClick={onClose}
              className="h-9 px-4 rounded-md text-sm bg-primary text-primary-foreground hover:opacity-90 transition-opacity"
            >
              完成
            </button>
          ) : (
            <>
              <button
                onClick={onClose}
                className="h-9 px-4 rounded-md text-sm border hover:bg-muted transition-colors"
              >
                取消
              </button>
              <button
                onClick={handleExport}
                disabled={exporting}
                className="h-9 px-4 rounded-md text-sm bg-primary text-primary-foreground hover:opacity-90 transition-opacity disabled:opacity-50"
              >
                {exporting ? '导出中...' : '导出'}
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
