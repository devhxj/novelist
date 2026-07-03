import { Loader2, CheckCircle2, XCircle, Eye, Plus, Pencil, Brain, FileText, Wrench, Check, AlertTriangle, Trash2 } from 'lucide-react'
import { memo, useState } from 'react'
import './ToolCallCard.css'

interface Props {
  toolName: string
  displayText: string
  status: 'executing' | 'awaiting_approval' | 'completed' | 'failed'
  activityKind?: string
  error?: string
  compact?: boolean
  // approval
  approvalType?: string
  approvalPayload?: Record<string, unknown>
  onApprove?: (feedback: string) => void
  onReject?: (feedback: string) => void
}

function ActivityIcon({ kind, size }: { kind?: string; size: number }) {
  switch (kind) {
    case 'view': case 'browse': return <Eye size={size} />
    case 'create': return <Plus size={size} />
    case 'write': case 'edit': return <Pencil size={size} />
    case 'memory': return <Brain size={size} />
    case 'review': return <CheckCircle2 size={size} />
    case 'delete': return <Trash2 size={size} />
    case 'plan': return <FileText size={size} />
    default: return <Wrench size={size} />
  }
}

function activityBadge(kind?: string): string {
  switch (kind) {
    case 'view': case 'browse': return '查看中'
    case 'create': return '创建中'
    case 'write': return '写作中'
    case 'edit': return '编辑中'
    case 'delete': return '删除中'
    case 'memory': return '检索中'
    case 'review': return '审阅中'
    case 'plan': return '规划中'
    default: return '处理中'
  }
}

const typeLabels: Record<string, string> = {
  character: '角色', character_relation: '角色关系',
  location: '地点', location_relation: '地点关系',
  timeline_entry: '时间线条目', story_arc: '故事弧',
  arc_node: '弧节点', reader_perspective_entry: '读者视角条目',
  preference: '偏好项',
}

function ApprovalBody({ type, payload }: { type?: string; payload?: Record<string, unknown> }) {
  if (type === 'delete' && payload?.deleted) {
    const d = payload.deleted as Record<string, unknown>
    const label = typeLabels[String(d.type)] ?? String(d.type ?? '记录')
    const nameOrTitle = (d.name ?? d.title) as string | undefined
    const title = nameOrTitle ?? `#${d.id}`

    if (d.type === 'character_relation') {
      return <span>确认删除 角色关系「{String(d.source)}」→「{String(d.target)}」（{String(d.relation)}）？</span>
    }
    if (d.type === 'location_relation') {
      return <span>确认删除 地点关系「{String(d.location_a)}」↔「{String(d.location_b)}」（{String(d.relation)}）？</span>
    }
    if (d.type === 'arc_node') {
      return <span>确认删除 弧节点「{title}」（{String(d.story_arc)}）？</span>
    }
    if (d.type === 'reader_perspective_entry') {
      return <span>确认删除 读者视角条目 #{String(d.id)}（{String(d.entry_type)}，第{String(d.planted_chapter)}章）？</span>
    }
    if (d.type === 'preference') {
      return <span>确认删除 偏好项 [{String(d.category)}]（#{String(d.id)}）？</span>
    }
    if (d.type === 'timeline_entry') {
      return <span>确认删除 时间线条目「{title}」？</span>
    }
    return <span>确认删除 {label}「{title}」？</span>
  }

  if (type === 'file_edit' && payload) {
    const changeTypeMap: Record<string, string> = {
      full_replace: '全文替换',
      search_replace: '查找替换',
      line_range_replace: '行范围替换',
    }
    const rawType = (payload.change_type as string) || ''
    const changeType = changeTypeMap[rawType] || rawType || '修改'
    const reason = (payload.reason as string) || ''
    return (
      <div>
        <div className="approval-summary">{changeType}</div>
        {reason && <div className="approval-reason">{reason}</div>}
      </div>
    )
  }

  return <span>等待审批...</span>
}

export default memo(function ToolCallCard({ displayText, status, activityKind, error, compact, approvalType, approvalPayload, onApprove, onReject }: Props) {
  const [feedback, setFeedback] = useState('')

  // 审批中状态
  if (status === 'awaiting_approval' && onApprove && onReject) {
    const handleInput = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
      setFeedback(e.target.value)
    }

    return (
      <div className={`tool-card awaiting-approval ${compact ? 'compact' : ''}`}>
        <div className="tool-row">
          <span className="tool-icon"><AlertTriangle size={compact ? 12 : 14} /></span>
          <span className="tool-label">{displayText}</span>
          <span className="tool-badge tool-badge-approval">
            <Loader2 size={10} className="animate-spin" /> 等待审批
          </span>
        </div>
        <div className="approval-body">
          <ApprovalBody type={approvalType} payload={approvalPayload} />
          <textarea
            value={feedback}
            onChange={handleInput}
            placeholder="反馈（可选）..."
            rows={1}
            className="approval-feedback"
          />
          <div className="approval-actions">
            <button
              onClick={() => { onReject(feedback); setFeedback('') }}
              className="approval-reject-btn cursor-pointer select-none"
            >
              <XCircle size={13} /> 拒绝
            </button>
            <button
              onClick={() => { onApprove(feedback); setFeedback('') }}
              className="approval-accept-btn cursor-pointer select-none"
            >
              <Check size={13} /> 批准
            </button>
          </div>
        </div>
      </div>
    )
  }

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
            <ActivityIcon kind={activityKind} size={compact ? 12 : 14} />
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
