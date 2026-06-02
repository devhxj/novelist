import { useState, useCallback, useRef, useEffect } from 'react'
import { MessageSquare, Loader2, History, Plus } from 'lucide-react'
import { EventsOn, EventsOff } from '@/lib/wailsjs/runtime/runtime'
import { useApp } from '@/hooks/useApp'
import type { llm, app } from '@/hooks/useApp'
import type { AgentEvent, Turn } from './types'
import { AgentEventType, emptySegment, rebuildTurns } from './types'
import ChatInput from './ChatInput'
import ChatControls from './ChatControls'
import MessageBubble from './MessageBubble'
import ThinkingBlock from './ThinkingBlock'
import ToolCallCard from './ToolCallCard'
import SubagentCard from './SubagentCard'
import type { UsageInfo } from './ContextRing'
import SettingsDialog from '@/components/settings/SettingsDialog'
import RecentSessions from './RecentSessions'
import SessionHistory from './SessionHistory'

interface Props {
  novelId: number
  onApprove: (toolId: string, feedback: string) => Promise<void>
  onReject: (toolId: string, feedback: string) => Promise<void>
}

const MIN_WIDTH = 280
const MAX_WIDTH = 600
const DEFAULT_WIDTH = 360
const EVENT_REORDER_TIMEOUT = 120

interface EventQueue {
  nextSeq: number
  pending: Map<number, AgentEvent>
  flushTimer: ReturnType<typeof setTimeout> | null
}

interface ChatStartedEvent {
  session_id?: string
  turn_id: number
}

export default function ChatPanel({ novelId, onApprove, onReject }: Props) {
  const app = useApp()
  const [width, setWidth] = useState(DEFAULT_WIDTH)
  const [isDragging, setIsDragging] = useState(false)
  const startXRef = useRef(0)
  const startWidthRef = useRef(DEFAULT_WIDTH)
  const [turns, setTurns] = useState<Turn[]>([])
  const [sessionId, setSessionId] = useState('')
  const [isLoading, setIsLoading] = useState(false)
  const [models, setModels] = useState<llm.AvailableModel[]>([])
  const [selectedKey, setSelectedKey] = useState('')
  const [reasoningEffort, setReasoningEffort] = useState('')
  const [approvalMode, setApprovalMode] = useState<'manual' | 'auto'>('manual')
  const [lastUsage, setLastUsage] = useState<UsageInfo | null>(null)
  const [showSettings, setShowSettings] = useState(false)
  const [activeSessionId, setActiveSessionId] = useState<string | null | undefined>(undefined)
  const [sessions, setSessions] = useState<app.SessionMeta[]>([])
  const [sessionsTotal, setSessionsTotal] = useState(0)
  const [showHistoryPanel, setShowHistoryPanel] = useState(false)
  const [activeApproval, setActiveApproval] = useState<{ toolId: string; path: string; changeType: string } | null>(null)

  // 监听审批请求，更新工具卡片状态和输入区审批栏
  useEffect(() => {
    EventsOn('approval:requested', (data: any) => {
      const toolId = data?.tool_id ?? ''
      setActiveApproval({
        toolId,
        path: data?.payload?.path ?? '',
        changeType: data?.payload?.change_type ?? '',
      })
    })
    return () => { EventsOff('approval:requested') }
  }, [])
  const [isLoadingHistory, setIsLoadingHistory] = useState(false)
  const messagesEndRef = useRef<HTMLDivElement>(null)
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)
  const counterRef = useRef(0)
  const startedUnsubRef = useRef<(() => void) | null>(null)
  const agentUnsubRef = useRef<(() => void) | null>(null)
  const eventQueuesRef = useRef<Map<number, EventQueue>>(new Map())

  // 加载模型列表
  useEffect(() => {
    app.GetModels().then(list => {
      if (list && list.length > 0) {
        setModels(list)
        const first = list[0]
        setSelectedKey(first.Key)
        if (first.ReasoningLevels?.length) {
          setReasoningEffort(first.ReasoningLevels[0])
        }
      }
    }).catch(() => {})
  }, [])

  // 加载会话列表
  useEffect(() => {
    if (!novelId) return
    setActiveSessionId(undefined)
    setTurns([])
    setSessionId('')
    app.GetSessions(novelId, 1, 5).then(r => {
      if (r) {
        setSessions(r.items)
        setSessionsTotal(r.total)
      }
    }).catch(() => {})
  }, [novelId])

  // 加载历史消息
  useEffect(() => {
    if (!activeSessionId || !novelId) return
    setSessionId(activeSessionId)
    setIsLoadingHistory(true)
    app.GetSessionMessages(activeSessionId).then(msgs => {
      if (msgs) {
        setTurns(rebuildTurns(msgs))
      }
    }).catch(() => {}).finally(() => setIsLoadingHistory(false))
  }, [activeSessionId, novelId])

  const handleMouseDown = useCallback((e: React.MouseEvent) => {
    e.preventDefault()
    setIsDragging(true)
    startXRef.current = e.clientX
    startWidthRef.current = width
  }, [width])

  useEffect(() => {
    if (!isDragging) return
    const handleMouseMove = (e: MouseEvent) => {
      const delta = e.clientX - startXRef.current
      const newWidth = Math.min(MAX_WIDTH, Math.max(MIN_WIDTH, startWidthRef.current - delta))
      setWidth(newWidth)
    }
    const handleMouseUp = () => setIsDragging(false)
    document.addEventListener('mousemove', handleMouseMove)
    document.addEventListener('mouseup', handleMouseUp)
    return () => {
      document.removeEventListener('mousemove', handleMouseMove)
      document.removeEventListener('mouseup', handleMouseUp)
    }
  }, [isDragging])

  // 清理事件监听器
  useEffect(() => {
    const eventQueues = eventQueuesRef.current
    return () => {
      startedUnsubRef.current?.()
      agentUnsubRef.current?.()
      eventQueues.forEach(queue => {
        if (queue.flushTimer) clearTimeout(queue.flushTimer)
      })
      eventQueues.clear()
    }
  }, [])

  // 流式输出时自动滚到底部，但仅在用户未主动上滚时
  useEffect(() => {
    if (isNearBottomRef.current) {
      messagesEndRef.current?.scrollIntoView({ behavior: 'instant' })
    }
  }, [turns])

  const handleMessagesScroll = useCallback(() => {
    const el = scrollContainerRef.current
    if (!el) return
    isNearBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60
  }, [])

  const handleSelectSession = useCallback((sid: string) => {
    setActiveSessionId(sid)
  }, [])

  const handleNewChat = useCallback(() => {
    setActiveSessionId(null)
    setTurns([])
    setSessionId('')
    app.GetSessions(novelId, 1, 5).then(r => {
      if (r) { setSessions(r.items); setSessionsTotal(r.total) }
    }).catch(() => {})
  }, [novelId, app])

  const handleOpenHistory = useCallback(() => {
    setShowHistoryPanel(true)
  }, [])

  const handleCloseHistory = useCallback(() => {
    setShowHistoryPanel(false)
  }, [])

  const applyAgentEvent = useCallback((turnId: number, event: AgentEvent) => {
    switch (event.type) {
      case AgentEventType.Usage: {
        if (event.usage) {
          setLastUsage(event.usage as unknown as UsageInfo)
        }
        return
      }
      case AgentEventType.Error: {
        setTurns(prev => prev.map(turn =>
          turn.turnId === turnId
            ? { ...turn, status: 'failed' as const, errorMessage: event.error || '对话出错，请重试' }
            : turn
        ))
        return
      }
    }

    setTurns(prev => prev.map(turn => {
      if (turn.turnId !== turnId) return turn

      // 子 Agent 事件：按 sub_task_id 路由到对应 SubagentSegment
      if (event.sub_task_id) {
        let subIdx = turn.segments.findIndex(s =>
          s.type === 'subagent' && s.taskId === event.sub_task_id
        )
        if (subIdx < 0) {
          // run_subagent 的 ToolCall 事件还没 apply，子 Agent 事件先到了——就地创建
          turn.segments.push({
            ...emptySegment(`subagent_${event.sub_task_id}`),
            type: 'subagent' as const,
            status: 'streaming' as const,
            agentType: 'memory' as const,
            taskId: event.sub_task_id,
            segments: [],
            finalText: '',
            toolStatus: 'executing' as const,
          })
          subIdx = turn.segments.length - 1
        }
        const subSeg = { ...turn.segments[subIdx] }
        if (!subSeg.segments) subSeg.segments = []
        const subSegs = [...subSeg.segments]
        const subSegId = `subseg_${++counterRef.current}`

        switch (event.type) {
          case AgentEventType.Thinking: {
            const chunk = event.data || ''
            const last = subSegs[subSegs.length - 1]
            if (last && last.type === 'text' && last.isStreaming) {
              subSegs[subSegs.length - 1] = { ...last, thinkingContent: last.thinkingContent + chunk }
            } else {
              subSegs.push({ ...emptySegment(subSegId), thinkingContent: chunk, thinkingDone: false, isStreaming: true })
            }
            break
          }
          case AgentEventType.ThinkingDone: {
            for (let i = 0; i < subSegs.length; i++) {
              if (subSegs[i].type === 'text' && !subSegs[i].thinkingDone) {
                subSegs[i] = { ...subSegs[i], thinkingDone: true, isStreaming: false }
              }
            }
            break
          }
          case AgentEventType.Content: {
            const chunk = event.data || ''
            const last = subSegs[subSegs.length - 1]
            if (last && last.type === 'text' && last.isStreaming) {
              subSegs[subSegs.length - 1] = { ...last, content: last.content + chunk, thinkingDone: true }
            } else {
              subSegs.push({ ...emptySegment(subSegId), content: chunk, thinkingDone: true, isStreaming: true })
            }
            break
          }
          case AgentEventType.ToolCall: {
            const subToolStatus = event.phase === 'completed' ? 'completed' as const
              : event.phase === 'failed' ? 'failed' as const
              : 'executing' as const
            const stIdx = subSegs.findIndex(s =>
              s.type === 'tool' && s.toolId === event.tool_id
            )
            if (stIdx >= 0) {
              subSegs[stIdx] = {
                ...subSegs[stIdx],
                toolStatus: subToolStatus,
                displayText: event.display_text || subSegs[stIdx].displayText,
                activityKind: event.activity_kind || '',
                error: event.error || '',
              }
            } else {
              subSegs.push({
                ...emptySegment(subSegId),
                type: 'tool',
                toolName: event.tool_name || '',
                toolId: event.tool_id || '',
                toolStatus: subToolStatus,
                displayText: event.display_text || event.tool_name || '',
                activityKind: event.activity_kind || '',
                error: event.error || '',
              })
            }
            break
          }
          default:
            break
        }

        subSeg.segments = subSegs
        const newSegs = [...turn.segments]
        newSegs[subIdx] = subSeg
        return { ...turn, segments: newSegs }
      }

      const segments = [...turn.segments]
      const segId = `seg_${++counterRef.current}`

      switch (event.type) {
        case AgentEventType.Thinking: {
          const chunk = event.data || ''
          const lastSeg = segments[segments.length - 1]
          if (lastSeg && lastSeg.type === 'text' && lastSeg.isStreaming) {
            segments[segments.length - 1] = {
              ...lastSeg,
              thinkingContent: lastSeg.thinkingContent + chunk,
            }
          } else {
            segments.push({
              ...emptySegment(segId),
              thinkingContent: chunk,
              thinkingDone: false,
              isStreaming: true,
            })
          }
          return { ...turn, segments }
        }

        case AgentEventType.ThinkingDone: {
          return {
            ...turn,
            segments: segments.map(seg =>
              seg.type === 'text' && !seg.thinkingDone
                ? { ...seg, thinkingDone: true, isStreaming: false }
                : seg
            ),
          }
        }

        case AgentEventType.Content: {
          const chunk = event.data || ''
          const lastSeg = segments[segments.length - 1]
          if (lastSeg && lastSeg.type === 'text' && lastSeg.isStreaming) {
            segments[segments.length - 1] = {
              ...lastSeg,
              content: lastSeg.content + chunk,
              thinkingDone: true,
            }
          } else {
            segments.push({
              ...emptySegment(segId),
              content: chunk,
              thinkingDone: true,
              isStreaming: true,
            })
          }
          return { ...turn, segments }
        }

        case AgentEventType.ToolCall: {
          const isSubagent = event.tool_name === 'run_subagent'
          const toolStatus = event.phase === 'completed' ? 'completed' as const
            : event.phase === 'failed' ? 'failed' as const
            : 'executing' as const

          // 工具结束即清除审批等待标记
          if (toolStatus !== 'executing' && event.tool_id) {
            setActiveApproval(prev => prev?.toolId === event.tool_id ? null : prev)
          }

          // run_subagent：维护对应的 subagent segment
          if (isSubagent) {
            const agentType = (event.metadata?.agent_type as 'memory' | 'review') || 'memory'
            const toolId = event.tool_id || ''
            const subIdx = segments.findIndex(seg =>
              seg.type === 'subagent' && seg.taskId === toolId
            )
            if (subIdx >= 0) {
              segments[subIdx] = {
                ...segments[subIdx],
                status: toolStatus === 'executing' ? 'streaming' : toolStatus === 'failed' ? 'failed' : 'done',
                toolStatus,
              }
            } else {
              segments.push({
                ...emptySegment(`subagent_${toolId || segId}`),
                type: 'subagent',
                status: 'streaming',
                agentType,
                taskId: toolId,
                segments: [],
                finalText: '',
                toolStatus: 'executing',
              })
            }
            // 移除同 toolId 的 tool segment（可能由空 toolName 的早期事件误创建）
            const cleanSegs = toolId
              ? segments.filter(seg => !(seg.type === 'tool' && seg.toolId === toolId))
              : segments
            return { ...turn, segments: cleanSegs }
          }

          const idx = segments.findIndex(seg =>
            seg.type === 'tool' && event.tool_id && seg.toolId === event.tool_id
          )

          if (idx >= 0) {
            segments[idx] = {
              ...segments[idx],
              toolStatus,
              displayText: event.display_text || segments[idx].displayText,
              activityKind: event.activity_kind || segments[idx].activityKind || '',
              error: event.error || '',
            }
          } else {
            segments.push({
              ...emptySegment(segId),
              type: 'tool',
              toolName: event.tool_name || '',
              toolId: event.tool_id || '',
              toolStatus,
              displayText: event.display_text || event.tool_name || '',
              activityKind: event.activity_kind || '',
              error: event.error || '',
            })
          }
          return { ...turn, segments }
        }

        default:
          return turn
      }
    }))
  }, [])

  const flushEventQueue = useCallback((turnId: number, force = false) => {
    const queue = eventQueuesRef.current.get(turnId)
    if (!queue) return

    let event = queue.pending.get(queue.nextSeq)
    while (event) {
      queue.pending.delete(queue.nextSeq)
      queue.nextSeq += 1
      applyAgentEvent(turnId, event)
      event = queue.pending.get(queue.nextSeq)
    }

    if (force && queue.pending.size > 0) {
      const orderedEvents = [...queue.pending.entries()].sort(([a], [b]) => a - b)
      queue.pending.clear()

      for (const [seq, queuedEvent] of orderedEvents) {
        if (seq >= queue.nextSeq) {
          queue.nextSeq = seq + 1
          applyAgentEvent(turnId, queuedEvent)
        }
      }
    }

    if (queue.pending.size === 0 && queue.flushTimer) {
      clearTimeout(queue.flushTimer)
      queue.flushTimer = null
    }
  }, [applyAgentEvent])

  const handleAgentEvent = useCallback((turnId: number) => (event: AgentEvent) => {
    if (!event.seq) {
      applyAgentEvent(turnId, event)
      return
    }

    let queue = eventQueuesRef.current.get(turnId)
    if (!queue) {
      queue = {
        nextSeq: 1,
        pending: new Map<number, AgentEvent>(),
        flushTimer: null,
      }
      eventQueuesRef.current.set(turnId, queue)
    }

    if (event.seq < queue.nextSeq) return

    queue.pending.set(event.seq, event)
    flushEventQueue(turnId)

    if (queue.pending.size > 0 && !queue.flushTimer) {
      queue.flushTimer = setTimeout(() => {
        queue.flushTimer = null
        flushEventQueue(turnId, true)
      }, EVENT_REORDER_TIMEOUT)
    }
  }, [applyAgentEvent, flushEventQueue])

  const handleConfigModel = useCallback(() => setShowSettings(true), [])

  const handleSelectModel = useCallback((key: string) => {
    setSelectedKey(key)
    const m = models.find(x => x.Key === key)
    if (m?.ReasoningLevels?.length) {
      setReasoningEffort(m.ReasoningLevels[0])
    }
  }, [models])

  const handleSelectEffort = useCallback((effort: string) => {
    setReasoningEffort(effort)
  }, [])

  const handleToggleApproval = useCallback(() => {
    const next = approvalMode === 'manual' ? 'auto' : 'manual'
    setApprovalMode(next)
    app.SetApprovalMode(next)
  }, [approvalMode, app])

  const handleSend = useCallback(async (content: string) => {
    if (!selectedKey) return
    const [p, m] = selectedKey.split('/')
    setIsLoading(true)

    const turnId = `turn_${++counterRef.current}`
    const newTurn: Turn = {
      id: turnId,
      turnId: 0,
      userMessage: content,
      segments: [],
      status: 'streaming',
    }

    // 如果是新对话，清除历史标记
    if (activeSessionId === null || activeSessionId === undefined) {
      setActiveSessionId(null)
    }

    setTurns(prev => [...prev, newTurn])

    // 监听 chat:started，拿到 turnId 后订阅 agent 事件流
    startedUnsubRef.current?.()
    const startedCleanup = EventsOn('chat:started', (data: ChatStartedEvent) => {
      if (data.session_id) {
        setSessionId(data.session_id)
        setActiveSessionId(data.session_id)
      }

      // 更新 turn 的 turnId 为后端分配的真实值
      setTurns(prev => prev.map(t =>
        t.id === turnId ? { ...t, turnId: data.turn_id } : t
      ))

      agentUnsubRef.current?.()
      const agentCleanup = EventsOn(`agent:${data.turn_id}`, handleAgentEvent(data.turn_id))
      agentUnsubRef.current = agentCleanup
    })
    startedUnsubRef.current = startedCleanup

    try {
      await app.Chat({
        session_id: sessionId,
        novel_id: novelId,
        message: content,
        provider_name: p,
        model_id: m,
        reasoning_effort: reasoningEffort,
      })
      // 刷新会话列表
      app.GetSessions(novelId, 1, 5).then(r => {
        if (r) { setSessions(r.items); setSessionsTotal(r.total) }
      }).catch(() => {})
    } catch (err) {
      setTurns(prev => prev.map(t =>
        t.id === turnId ? { ...t, status: 'failed' as const, errorMessage: String(err) } : t
      ))
    } finally {
      eventQueuesRef.current.forEach((queue, queuedTurnId) => {
        if (queue.flushTimer) clearTimeout(queue.flushTimer)
        const orderedEvents = [...queue.pending.entries()].sort(([a], [b]) => a - b)
        queue.pending.clear()
        for (const [seq, queuedEvent] of orderedEvents) {
          if (seq >= queue.nextSeq) {
            queue.nextSeq = seq + 1
            applyAgentEvent(queuedTurnId, queuedEvent)
          }
        }
      })
      eventQueuesRef.current.clear()
      setTurns(prev => prev.map(t =>
        t.id === turnId && t.status === 'streaming'
          ? { ...t, status: 'done' as const, segments: t.segments.map(seg =>
              seg.type === 'text' ? { ...seg, isStreaming: false } : seg
            )}
          : t
      ))
      setIsLoading(false)
      startedUnsubRef.current?.()
      startedUnsubRef.current = null
      agentUnsubRef.current?.()
      agentUnsubRef.current = null
    }
  }, [sessionId, novelId, selectedKey, reasoningEffort, app, handleAgentEvent, applyAgentEvent, activeSessionId])

  const hasNovel = novelId > 0
  const hasTurns = turns.length > 0
  const hasActiveSession = activeSessionId !== undefined && activeSessionId !== null
  const showRecent = !hasActiveSession && !hasTurns && !isLoading


  const handleApprovalApprove = useCallback(async (feedback: string) => {
    if (!activeApproval) return
    const toolId = activeApproval.toolId
    setActiveApproval(null)
    await onApprove(toolId, feedback)
  }, [activeApproval, onApprove])

  const handleApprovalReject = useCallback(async (feedback: string) => {
    if (!activeApproval) return
    const toolId = activeApproval.toolId
    setActiveApproval(null)
    await onReject(toolId, feedback)
  }, [activeApproval, onReject])

  const inputPlaceholder = !hasNovel
    ? '请先选择作品'
    : !selectedKey
      ? '请先配置模型'
      : '输入消息...'

  return (
    <aside className="shrink-0 flex flex-col bg-background border-l relative" style={{ width }}>
      <div
        className="absolute left-0 top-0 bottom-0 w-1 cursor-col-resize hover:bg-primary/30 transition-colors z-10"
        style={{ marginLeft: -2 }}
        onMouseDown={handleMouseDown}
      />

      <div className="px-4 py-2.5 border-b shrink-0 flex items-center justify-between">
        <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">AI 对话</span>
        <div className="flex items-center gap-2">
          <button
            onClick={handleOpenHistory}
            className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground transition-colors cursor-pointer"
          >
            <History className="w-3.5 h-3.5" /> 历史
          </button>
          <button
            onClick={handleNewChat}
            className="flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground transition-colors cursor-pointer"
          >
            <Plus className="w-3.5 h-3.5" /> 新对话
          </button>
        </div>
      </div>

      <div className="absolute left-0 right-0 top-[41px] bottom-0 pointer-events-none z-30">
        <SessionHistory
          open={showHistoryPanel}
          novelId={novelId}
          onClose={handleCloseHistory}
          onSelectSession={handleSelectSession}
        />
      </div>

      <div ref={scrollContainerRef} onScroll={handleMessagesScroll} className="flex-1 overflow-y-auto px-3 py-3 relative">
        {!hasNovel ? (
          <div className="flex items-center justify-center h-full">
            <div className="text-center">
              <MessageSquare className="w-10 h-10 text-muted-foreground/20 mx-auto mb-3" />
              <p className="text-sm text-muted-foreground">选择作品开始对话</p>
            </div>
          </div>
        ) : showRecent ? (
          <RecentSessions
            sessions={sessions}
            total={sessionsTotal}
            onSelectSession={handleSelectSession}
            onViewAll={handleOpenHistory}
          />
        ) : isLoadingHistory ? (
          <div className="flex items-center justify-center h-full">
            <Loader2 className="w-5 h-5 animate-spin text-muted-foreground" />
          </div>
        ) : (
          <>
            {/* 消息列表 */}
            {!hasTurns && !isLoading ? (
              <div className="flex items-center justify-center h-full">
                <div className="text-center">
                  <MessageSquare className="w-10 h-10 text-muted-foreground/20 mx-auto mb-3" />
                  <p className="text-sm text-muted-foreground">输入消息开始对话</p>
                </div>
              </div>
            ) : (
              <div className="space-y-4">
                {turns.map(turn => (
                  <div key={turn.id} className="space-y-2">
                    {turn.userMessage && (
                      <MessageBubble role="user" content={turn.userMessage} />
                    )}

                    {turn.segments.map(seg => {
                      if (seg.type === 'subagent' && seg.agentType) {
                        return (
                          <SubagentCard
                            key={seg.id}
                            agentType={seg.agentType}
                            segments={seg.segments || []}
                            status={seg.status || 'done'}
                          />
                        )
                      }

                      if (seg.type === 'tool') {
                        // run_subagent 已由 subagent 段渲染，跳过纯工具卡
                        if (seg.toolName === 'run_subagent') return null

                        return (
                          <ToolCallCard
                            key={seg.id}
                            toolName={seg.toolName}
                            displayText={seg.displayText}
                            status={seg.toolStatus}
                            activityKind={seg.activityKind}
                            error={seg.error}
                          />
                        )
                      }

                      return (
                        <div key={seg.id}>
                          {seg.thinkingContent && (
                            <div className="max-w-[85%]">
                              <ThinkingBlock
                                content={seg.thinkingContent}
                                isStreaming={!seg.thinkingDone && seg.isStreaming}
                              />
                            </div>
                          )}
                          {seg.content && (
                            <MessageBubble role="assistant" content={seg.content} />
                          )}
                        </div>
                      )
                    })}

                    {turn.status === 'failed' && turn.errorMessage && (
                      <div className="flex justify-start">
                        <div className="bg-red-50 border border-red-200 rounded-lg px-3 py-2 text-xs text-red-600 max-w-[80%]">
                          {turn.errorMessage}
                        </div>
                      </div>
                    )}
                    {turn.status === 'streaming' && turn.segments.length === 0 && (
                      <div className="flex justify-start">
                        <div className="bg-muted rounded-lg rounded-bl-sm px-3 py-2">
                          <Loader2 className="w-4 h-4 animate-spin text-muted-foreground" />
                        </div>
                      </div>
                    )}
                  </div>
                ))}
              </div>
            )}
          </>
        )}

        <div ref={messagesEndRef} />
      </div>

      <ChatInput
        disabled={!hasNovel || !selectedKey}
        isLoading={isLoading}
        placeholder={inputPlaceholder}
        onSend={handleSend}
        onStop={() => app.CancelChat(sessionId)}
        approval={activeApproval}
        onApprove={handleApprovalApprove}
        onReject={handleApprovalReject}
      />

      <div className="border-t mx-4" />

      <ChatControls
        models={models}
        selectedKey={selectedKey}
        onSelectModel={handleSelectModel}
        reasoningEffort={reasoningEffort}
        onSelectEffort={handleSelectEffort}
        approvalMode={approvalMode}
        onToggleApproval={handleToggleApproval}
        onConfigModel={handleConfigModel}
        usage={lastUsage}
      />

      {isDragging && (
        <div className="fixed inset-0 z-50 cursor-col-resize select-none" />
      )}

      <SettingsDialog
        open={showSettings}
        onClose={() => setShowSettings(false)}
        initialTab="model"
      />
    </aside>
  )
}
