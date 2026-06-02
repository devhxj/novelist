import { Loader2, CheckCircle2, XCircle, Eye, Plus, Pencil, Brain, FileText, Wrench } from 'lucide-react'
import { memo } from 'react'
import './ToolCallCard.css'

interface Props {
  toolName: string
  displayText: string
  status: 'executing' | 'completed' | 'failed'
  activityKind?: string
  error?: string
  compact?: boolean
}

function activityIcon(kind?: string) {
  switch (kind) {
    case 'view': case 'browse': return Eye
    case 'create': return Plus
    case 'write': case 'edit': return Pencil
    case 'memory': return Brain
    case 'review': return CheckCircle2
    case 'plan': return FileText
    default: return Wrench
  }
}

function activityBadge(kind?: string): string {
  switch (kind) {
    case 'view': case 'browse': return '查看中'
    case 'create': return '创建中'
    case 'write': return '写作中'
    case 'edit': return '编辑中'
    case 'memory': return '检索中'
    case 'review': return '审阅中'
    case 'plan': return '规划中'
    default: return '处理中'
  }
}

export default memo(function ToolCallCard({ displayText, status, activityKind, error, compact }: Props) {
  const Icon = activityIcon(activityKind)
  const isExecuting = status === 'executing'
  const isCompleted = status === 'completed'
  const isFailed = status === 'failed'

  return (
    <div className={`tool-card ${isExecuting ? 'executing' : isCompleted ? 'completed' : 'failed'} ${compact ? 'compact' : ''}`}>
      <div className={`tool-row ${compact ? 'compact' : ''}`}>
        <span className="tool-icon">
          {isExecuting ? (
            <Loader2 className="animate-spin" size={compact ? 12 : 14} />
          ) : isFailed ? (
            <XCircle size={compact ? 12 : 14} />
          ) : (
            <Icon size={compact ? 12 : 14} />
          )}
        </span>

        <span className="tool-label">{displayText}</span>

        <span className={`tool-badge ${isCompleted ? 'tool-badge-done' : isFailed ? 'tool-badge-failed' : ''}`}>
          {isExecuting ? activityBadge(activityKind) : isCompleted ? '完成' : '失败'}
        </span>
      </div>

      {isFailed && error && (
        <div className="tool-error">{error.slice(0, 120)}</div>
      )}
    </div>
  )
})
