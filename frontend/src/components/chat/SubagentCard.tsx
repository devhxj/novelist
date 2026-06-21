import { useState, useEffect, useRef, memo } from 'react'
import { Loader2, CheckCircle2, XCircle, ChevronDown } from 'lucide-react'
import type { TurnSegment } from './types'
import ThinkingBlock from './ThinkingBlock'
import MessageBubble from './MessageBubble'
import ToolCallCard from './ToolCallCard'
import CompressionBlock from './CompressionBlock'
import './SubagentCard.css'

interface Props {
  agentType: 'memory' | 'review'
  segments: TurnSegment[]
  status: 'streaming' | 'done' | 'failed'
}

const agentMeta: Record<string, { label: string; emoji: string }> = {
  memory: { label: '记忆分析师', emoji: '📝' },
  review: { label: '审核编辑', emoji: '🔍' },
}

export default memo(function SubagentCard({ agentType, segments, status }: Props) {
  const [collapsed, setCollapsed] = useState(status !== 'streaming')
  const autoExpanded = useRef(false)
  const meta = agentMeta[agentType]
  const isStreaming = status === 'streaming'
  const isDone = status === 'done'
  const isFailed = status === 'failed'

  const accentCls = agentType === 'review' ? 'subagent-review' : 'subagent-memory'

  const prevStatusRef = useRef(status)

  useEffect(() => {
    const prev = prevStatusRef.current
    prevStatusRef.current = status

    if (isStreaming && !autoExpanded.current) {
      setCollapsed(false)
      autoExpanded.current = true
    }
    if (prev !== 'streaming') {
      autoExpanded.current = false
    }
    if (prev === 'streaming' && isDone) {
      const t = setTimeout(() => setCollapsed(true), 1000)
      return () => clearTimeout(t)
    }
  }, [status, isStreaming, isDone])

  return (
    <div className="flex justify-start">
    <div className={`subagent-card max-w-[85%] ${accentCls} ${isStreaming ? 'subagent-streaming' : ''}`}>
      <button
        onClick={() => setCollapsed(!collapsed)}
        className="subagent-header"
      >
        <ChevronDown className={`shrink-0 transition-transform duration-200 text-muted-foreground/50 ${collapsed ? '' : 'rotate-180'}`} size={12} />
        <span className="subagent-icon">{meta.emoji}</span>
        <span className="subagent-label">{meta.label}</span>

        <span className="flex-1" />

        {isStreaming && (
          <span className="subagent-badge subagent-badge-running">
            <Loader2 size={10} className="animate-spin" /> 执行中
          </span>
        )}
        {isDone && (
          <span className="subagent-badge subagent-badge-done">
            <CheckCircle2 size={10} /> 完成
          </span>
        )}
        {isFailed && (
          <span className="subagent-badge subagent-badge-failed">
            <XCircle size={10} /> 失败
          </span>
        )}
      </button>

      <div
        className={`grid transition-all duration-300 ease-out ${
          collapsed ? 'grid-rows-[0fr] opacity-0' : 'grid-rows-[1fr] opacity-100'
        }`}
      >
        <div className="overflow-hidden border-t border-border/30">
          <div className="px-3 pb-3 space-y-2 pt-2">
            {segments.length === 0 && isStreaming && (
              <div className="flex items-center gap-2 text-xs text-muted-foreground py-2">
                <Loader2 size={12} className="animate-spin" /> 正在分析…
              </div>
            )}
            {segments.length === 0 && !isStreaming && (
              <div className="text-xs text-muted-foreground py-2">暂无内容</div>
            )}

            {segments.map(seg => {
              if (seg.type === 'compression') {
                return <CompressionBlock key={seg.id} phase={seg.compressionPhase || 'done'} />
              }
              if (seg.type === 'text') {
                return (
                  <div key={seg.id} className="space-y-1">
                    {seg.thinkingContent && (
                      <ThinkingBlock content={seg.thinkingContent} isStreaming={isStreaming && !seg.thinkingDone} />
                    )}
                    {seg.content && (
                      <div className="text-xs">
                        <MessageBubble role="assistant" content={seg.content} />
                      </div>
                    )}
                  </div>
                )
              }
              if (seg.type === 'tool') {
                return (
                  <ToolCallCard
                    key={seg.id}
                    toolName={seg.toolName}
                    displayText={seg.displayText}
                    status={seg.toolStatus}
                    activityKind={seg.activityKind}
                    error={seg.error}
                    compact
                  />
                )
              }
              return null
            })}
          </div>
        </div>
      </div>
    </div>
    </div>
  )
})
