import { Loader2, CheckCircle2, XCircle, Eye, Plus, Pencil, Brain, FileText, Wrench, Check, AlertTriangle, Trash2, RotateCcw, Ban } from 'lucide-react'
import { memo, useEffect, useState } from 'react'
import { diagnosticMessage } from '@/lib/diagnostics'
import './ToolCallCard.css'

// 审批提交走本地桥，超过这个时长基本就是卡住了：给作者一条"结束本轮"的出路，别让卡片干等。
const APPROVAL_SLOW_MS = 6000
// 残余 4（前端可做部分）：后端不做审批自动击杀（误杀合法长审批的风险大于收益），
// 但等待远超正常范围时把提示升级为明确的行动建议。
const APPROVAL_STUCK_MS = 60_000

interface Props {
  displayText: string
  status: 'executing' | 'awaiting_approval' | 'completed' | 'failed'
  activityKind?: string
  error?: string
  compact?: boolean
  // approval
  approvalType?: string
  approvalPayload?: Record<string, unknown>
  onApprove?: (feedback: string) => void | Promise<void>
  onReject?: (feedback: string) => void | Promise<void>
  onEndTurn?: () => void
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
    // I7：未知类型不再透出英文枚举原文，统一兜底"记录"。
    const label = typeLabels[String(d.type)] ?? '记录'
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
    // I7：未知 change_type 兜底中文，不透出内部枚举原文。
    const changeType = changeTypeMap[rawType] || '修改'
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

export default memo(function ToolCallCard({ displayText, status, activityKind, error, compact, approvalType, approvalPayload, onApprove, onReject, onEndTurn }: Props) {
  const [feedback, setFeedback] = useState('')
  const [submitting, setSubmitting] = useState<'approve' | 'reject' | null>(null)
  const [slow, setSlow] = useState(false)
  const [stuck, setStuck] = useState(false)
  const [submitError, setSubmitError] = useState<{ decision: 'approve' | 'reject'; message: string } | null>(null)

  // O17：慢路径计时覆盖整个"等待审批"时长（含提交成功后后端已死、
  // 后续工具事件永远不来的场景），不只盯提交瞬间——否则作者会被钉死在
  // "等待审批"常态里，唯一出路是故意制造一次提交失败。
  // 残余 4：60 秒无进展升级为"后端可能已失效"的行动建议（后端不做自动击杀，
  // 避免误杀合法长审批；离开等待态时两条计时一并复位）。
  // 计时状态以"等待期"为单位复位：status/submitting 组成的键变化即新等待期，
  // 渲染期同步重置（React 官方推荐的 adjust-state-on-prop-change 模式，effect 只挂定时器）。
  const timerKey = `${status}:${submitting ?? ''}`
  const [prevTimerKey, setPrevTimerKey] = useState(timerKey)
  if (prevTimerKey !== timerKey) {
    setPrevTimerKey(timerKey)
    setSlow(false)
    setStuck(false)
  }

  useEffect(() => {
    if (status !== 'awaiting_approval') return
    const slowTimer = window.setTimeout(() => setSlow(true), APPROVAL_SLOW_MS)
    const stuckTimer = window.setTimeout(() => setStuck(true), APPROVAL_STUCK_MS)
    return () => {
      window.clearTimeout(slowTimer)
      window.clearTimeout(stuckTimer)
    }
    // timerKey 已编码 status 与 submitting；status 仅作守卫读取。
  }, [status, timerKey])

  // 审批中状态
  if (status === 'awaiting_approval' && onApprove && onReject) {
    const handleInput = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
      setFeedback(e.target.value)
    }

    const submit = async (decision: 'approve' | 'reject') => {
      setSubmitting(decision)
      setSubmitError(null)
      try {
        await (decision === 'approve' ? onApprove(feedback) : onReject(feedback))
        // 只有提交成功才清空反馈：失败时作者刚敲的那段话必须留在框里。
        setFeedback('')
      } catch (e) {
        setSubmitError({
          decision,
          message: diagnosticMessage(e, decision === 'approve' ? '批准提交失败' : '拒绝提交失败'),
        })
      } finally {
        setSubmitting(null)
      }
    }

    const busy = submitting !== null

    return (
      <div className={`tool-card awaiting-approval ${compact ? 'compact' : ''}`}>
        <div className="tool-row">
          <span className="tool-icon"><AlertTriangle size={compact ? 12 : 14} /></span>
          <span className="tool-label">{displayText}</span>
          <span className="tool-badge tool-badge-approval">
            <Loader2 size={10} className="animate-spin" /> {busy ? '提交中' : '等待审批'}
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
            disabled={busy}
          />
          {submitError ? (
            <div className="approval-error" role="alert">
              {submitError.message}
            </div>
          ) : stuck ? (
            <div className="approval-hint" role="alert">
              审批等待已超过 60 秒仍无进展，后端本轮可能已失效，建议结束本轮后重试。
            </div>
          ) : slow ? (
            <div className="approval-hint" role="status">
              审批等待已超过 6 秒没有进展，可以继续等，也可以结束本轮。
            </div>
          ) : null}
          <div className="approval-actions">
            {(submitError || slow || stuck) && onEndTurn && (
              <button
                onClick={onEndTurn}
                className="approval-end-btn cursor-pointer select-none"
              >
                <Ban size={13} /> 结束本轮
              </button>
            )}
            {submitError ? (
              <button
                onClick={() => { void submit(submitError.decision) }}
                disabled={busy}
                className="approval-retry-btn cursor-pointer select-none disabled:opacity-60"
              >
                <RotateCcw size={13} /> 重试{submitError.decision === 'approve' ? '批准' : '拒绝'}
              </button>
            ) : (
              <>
                <button
                  onClick={() => { void submit('reject') }}
                  disabled={busy}
                  className="approval-reject-btn cursor-pointer select-none disabled:opacity-60"
                >
                  <XCircle size={13} /> 拒绝
                </button>
                <button
                  onClick={() => { void submit('approve') }}
                  disabled={busy}
                  className="approval-accept-btn cursor-pointer select-none disabled:opacity-60"
                >
                  <Check size={13} /> 批准
                </button>
              </>
            )}
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
