import { useState, useCallback, useRef, useEffect } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent } from 'react'
import { MessageSquare, Loader2, History, Plus, PenLine, ChevronDown, Link2, Link2Off, Lock, CheckCircle2 } from 'lucide-react'
import { EventsOn } from '@/lib/novelist/events'
import { pushToast } from '@/lib/toast'
import { useApp } from '@/hooks/useApp'
import type { llm, app, reference } from '@/hooks/useApp'
import type { AgentEvent, Turn } from './types'
import { AgentEventType, emptySegment, rebuildTurns } from './types'
import ChatInput from './ChatInput'
import CorpusUsageCard, { type CorpusUsageMaterial } from './CorpusUsageCard'
import ChoiceBlock from './ChoiceBlock'
import { parseChoices } from './choices'
import ChatControls from './ChatControls'
import MessageBubble from './MessageBubble'
import ThinkingBlock from './ThinkingBlock'
import ToolCallCard from './ToolCallCard'
import WebSearchCard from './WebSearchCard'
import WebFetchCard from './WebFetchCard'
import SubagentCard from './SubagentCard'
import CompressionBlock from './CompressionBlock'
import type { UsageInfo } from './ContextRing'
import SettingsDialog from '@/components/settings/SettingsDialog'
import RecentSessions from './RecentSessions'
import SessionHistory from './SessionHistory'
import { LAYOUT_LIMITS, clampPanelWidth } from '@/lib/layout'

interface Props {
  width: number
  onWidthChange: (width: number) => void
  onWidthCommit: (width: number) => void
  novelId: number
  chapterNumber?: number | null
  referenceRefreshKey?: number
  onOpenPlans?: () => void
  onApprove: (toolId: string, feedback: string) => Promise<void>
  onReject: (toolId: string, feedback: string) => Promise<void>
  onApprovalFileEdit?: (payload: {
    path: string; title: string; diff: string; original: string; modified: string
    changeType: string; reason: string; toolId: string
  }) => void
}

const DIRECT_WRITE_MESSAGE = '直接开写：请立即按当前细纲开始写本章正文，不再继续访谈。'

// 章号绑定模式：auto 跟随编辑器当前章节 tab；pinned 锁定指定章号；off 显式不绑定。
type ChapterBinding = { mode: 'auto' } | { mode: 'pinned'; chapter: number } | { mode: 'off' }

const EVENT_REORDER_TIMEOUT = 120

// bridge 错误统一提取可读信息：BridgeError.message 已含服务端文案，避免 "Error: " 前缀。
function describeError(err: unknown): string {
  if (err instanceof Error && err.message) return err.message
  return String(err)
}

// 静默失败检查点（F11）：这些后台加载/取消失败过去只进 console，
// 界面呈现为"空"，作者会误以为没有数据。现在同时走统一通知通道。
function notifyChatFailure(message: string, err: unknown) {
  pushToast({ kind: 'error', message, description: describeError(err) })
}

interface EventQueue {
  nextSeq: number
  pending: Map<number, AgentEvent>
  flushTimer: ReturnType<typeof setTimeout> | null
}

// 每一轮对话各自持有退订句柄：单槽 ref 会让"先结束的那一轮"拆掉仍在进行的另一轮，
// 使后者的流式输出无处落地。backendTurnId 用于在收尾时只 flush 本轮的事件队列。
interface TurnSubscription {
  started: (() => void) | null
  agent: (() => void) | null
  backendTurnId: number | null
}

interface ChatStartedEvent {
  session_id?: string
  turn_id: number
}

export default function ChatPanel({
  width,
  onWidthChange,
  onWidthCommit,
  novelId,
  chapterNumber,
  referenceRefreshKey = 0,
  onOpenPlans,
  onApprove,
  onReject,
  onApprovalFileEdit,
}: Props) {
  const app = useApp()
  const [isDragging, setIsDragging] = useState(false)
  const startXRef = useRef(0)
  const startWidthRef = useRef(width)
  const latestWidthRef = useRef(width)
  const [turns, setTurns] = useState<Turn[]>([])
  const [sessionId, setSessionId] = useState('')
  const [isLoading, setIsLoading] = useState(false)
  const [models, setModels] = useState<llm.AvailableModel[]>([])
  const [selectedKey, setSelectedKey] = useState('')
  const [reasoningEffort, setReasoningEffort] = useState('')
  const [approvalMode, setApprovalMode] = useState<'manual' | 'auto'>('manual')
  const [lastUsage, setLastUsage] = useState<UsageInfo | null>(null)
  const [isCompressing, setIsCompressing] = useState(false)
  const compressingRef = useRef(false)
  const activeCountRef = useRef(0)
  const [showSettings, setShowSettings] = useState(false)
  const [activeSessionId, setActiveSessionId] = useState<string | null | undefined>(undefined)
  const [sessions, setSessions] = useState<app.SessionMeta[]>([])
  const [sessionsTotal, setSessionsTotal] = useState(0)
  const [showHistoryPanel, setShowHistoryPanel] = useState(false)
  const [isLoadingHistory, setIsLoadingHistory] = useState(false)
  const [initLoadError, setInitLoadError] = useState(false)
  const [initLoadRetry, setInitLoadRetry] = useState(0)
  const [historyLoadError, setHistoryLoadError] = useState(false)
  const [historyLoadRetry, setHistoryLoadRetry] = useState(0)
  const [slashCommands, setSlashCommands] = useState<app.SlashCommand[]>([])
  const [chapterCoverage, setChapterCoverage] = useState<reference.ChapterCorpusCoverage | null>(null)
  const [coverageState, setCoverageState] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle')
  const [chapterBinding, setChapterBinding] = useState<ChapterBinding>({ mode: 'auto' })
  const [bindingMenuOpen, setBindingMenuOpen] = useState(false)
  const [pinnedDraft, setPinnedDraft] = useState('')
  const [advancingChapter, setAdvancingChapter] = useState(false)
  const coverageRequestSeqRef = useRef(0)
  // 生效章号：锁定优先，其次跟随编辑器 tab；off 显式为空。
  const effectiveChapterNumber = chapterBinding.mode === 'pinned'
    ? chapterBinding.chapter
    : chapterBinding.mode === 'off'
      ? null
      : (chapterNumber ?? null)
  const effectiveChapterRef = useRef<number | null>(effectiveChapterNumber)
  effectiveChapterRef.current = effectiveChapterNumber
  const messagesEndRef = useRef<HTMLDivElement>(null)
  const scrollContainerRef = useRef<HTMLDivElement>(null)
  const isNearBottomRef = useRef(true)
  const counterRef = useRef(0)
  const turnSubsRef = useRef<Map<string, TurnSubscription>>(new Map())
  const eventQueuesRef = useRef<Map<number, EventQueue>>(new Map())
  // 停止按钮可能在 chat:started 之前按下，此时会话 id 还是空串，
  // 直接发取消会被后端的空 id 分支丢弃，只能先记意图、等 id 到手再补发。
  const pendingCancelRef = useRef(false)
  // O18：当前流式轮次的会话 id（chat:started 时记录，轮次收尾时清除）。
  const activeTurnSessionIdRef = useRef<string | null>(null)
  // O21：当前 turns 已经属于哪个"本端流式过"的会话。真实后端的 chat:started 总是携带
  // session_id，它会置 activeSessionId 并触发"加载历史消息" effect——若不区分，
  // 首轮流式输出会在中途被 rebuildTurns 整体冲掉（第三轮 mock 规避记录的真实缺陷）。
  const liveTurnsSessionIdRef = useRef<string | null>(null)
  // R5：离开"仍在流式的会话"时留存 turns 快照——切回时恢复快照而非重放历史，
  // 否则在途回复会在作者眼前消失（服务端落库不受影响，纯展示层丢失）。
  const liveTurnsSnapshotsRef = useRef<Map<string, Turn[]>>(new Map())
  const turnsRef = useRef<Turn[]>([])
  // R5：历史加载的响应竞态守卫——快速连续切换会话时，慢的旧响应不得覆盖新会话。
  const historySeqRef = useRef(0)
  const onApprovalFileEditRef = useRef(onApprovalFileEdit)
  useEffect(() => { onApprovalFileEditRef.current = onApprovalFileEdit }, [onApprovalFileEdit])
  // 待恢复的上次会话 ID：设置加载完成后由恢复 effect 消费（F12）。
  const [pendingLastSessionId, setPendingLastSessionId] = useState('')

  useEffect(() => {
    latestWidthRef.current = width
  }, [width])

  useEffect(() => { turnsRef.current = turns }, [turns])

  // 章节语料覆盖度：打开章节后按细纲 beat 聚合检索命中，写作前给出“语料不足”信号。
  // seq guard 防止章节快速切换时旧请求覆盖新结果；失败显式呈现，不再静默吞掉。
  const loadCoverage = useCallback((options?: { refresh?: boolean }) => {
    const activeChapter = effectiveChapterRef.current
    if (!novelId || !activeChapter) {
      coverageRequestSeqRef.current++
      setChapterCoverage(null)
      setCoverageState('idle')
      return
    }
    const requestId = ++coverageRequestSeqRef.current
    setCoverageState('loading')
    app.GetChapterCorpusCoverage({ novel_id: novelId, chapter_number: activeChapter, refresh: options?.refresh ?? false })
      .then((coverage) => {
        if (coverageRequestSeqRef.current !== requestId) return
        setChapterCoverage(coverage)
        setCoverageState('ready')
      })
      .catch(() => {
        if (coverageRequestSeqRef.current !== requestId) return
        setChapterCoverage(null)
        setCoverageState('error')
      })
  }, [app, novelId])

  useEffect(() => {
    const timer = window.setTimeout(() => { loadCoverage() }, 0)
    return () => { window.clearTimeout(timer) }
  }, [loadCoverage, effectiveChapterNumber])

  useEffect(() => {
    if (!isLoading) return
    const refresh = () => { loadCoverage() }
    return refresh
  }, [isLoading, loadCoverage])

  // 语料区发生材料化/复核变更时（referenceRefreshKey 递增），强制刷新覆盖度信号。
  const coverageRefreshKeyRef = useRef(referenceRefreshKey)
  useEffect(() => {
    if (coverageRefreshKeyRef.current === referenceRefreshKey) return
    coverageRefreshKeyRef.current = referenceRefreshKey
    loadCoverage({ refresh: true })
  }, [referenceRefreshKey, loadCoverage])

  // 本章完成：轮转三层计划（细纲并入部纲、部纲并入大纲），随后刷新覆盖度信号。
  const handleFinishChapter = useCallback(async () => {
    if (!novelId || advancingChapter) return
    setAdvancingChapter(true)
    try {
      await app.AdvanceChapterPlan({ novel_id: novelId })
      loadCoverage({ refresh: true })
    } catch {
      // 轮转失败静默恢复按钮；作者可重试或到时间线面板手动调整。
    } finally {
      setAdvancingChapter(false)
    }
  }, [app, novelId, advancingChapter, loadCoverage])

  // 加载模型列表并恢复持久化设置
  useEffect(() => {
    setInitLoadError(false)
    Promise.all([
      app.GetModels(),
      app.GetSettings(),
    ]).then(([modelList, settings]) => {
      if (modelList && modelList.length > 0) {
        setModels(modelList)

        // 恢复模型选择（验证 key 仍存在）
        let key = settings?.selected_model_key || ''
        let model = modelList.find(m => m.Key === key)
        if (!model) {
          model = modelList[0]
          key = model.Key
        }
        setSelectedKey(key)

        // 恢复推理程度（验证级别仍合法）
        let effort = settings?.reasoning_effort || ''
        if (!effort || !model.ReasoningLevels?.includes(effort)) {
          effort = model.ReasoningLevels?.[0] || ''
        }
        setReasoningEffort(effort)
      }

      // 恢复审批模式
      const mode = settings?.approval_mode
      if (mode === 'manual' || mode === 'auto') {
        setApprovalMode(mode)
      }

      // 上次会话 ID 改用状态保存（F12）：它由设置请求异步送达，
      // 若像以前一样塞进 ref，会话列表 effect 可能早已带着空值跑完，恢复就永远不触发。
      setPendingLastSessionId(settings?.last_session_id || '')
    }).catch((err) => {
      console.error('Load models/settings failed', err)
      setInitLoadError(true)
    })
  }, [app, initLoadRetry])

  // 加载会话列表
  useEffect(() => {
    if (!novelId) return
    setActiveSessionId(undefined)
    setTurns([])
    setSessionId('')
    liveTurnsSessionIdRef.current = null
    liveTurnsSnapshotsRef.current.clear()
    app.GetSessions({ novel_id: novelId, page: 1, size: 5, search: '' }).then(r => {
      if (r) {
        setSessions(r.items)
        setSessionsTotal(r.total)
      }
    }).catch((err) => {
      console.error('Load sessions failed', err)
      notifyChatFailure('会话列表加载失败', err)
    })
  }, [app, novelId])

  // 恢复上次活跃会话：设置与 novelId 都就位后才执行，恢复一次即消费掉。
  useEffect(() => {
    const sid = pendingLastSessionId
    if (!sid || !novelId) return
    setPendingLastSessionId('')
    app.GetSession(sid).then(detail => {
      if (detail && detail.novel_id === novelId) {
        setActiveSessionId(sid)
      }
    }).catch(() => {
      app.SetLastSession('').catch(err => console.warn('SetLastSession clear failed', err))
    })
  }, [app, novelId, pendingLastSessionId])

  // 加载历史消息
  useEffect(() => {
    if (!activeSessionId || !novelId) return
    const seq = ++historySeqRef.current
    // O21/R5：会话仍在流式时 turns 是权威状态——恢复离开时的快照而不是重放历史，
    // 重放会把在途气泡冲掉；快照恢复后，后续流式事件按 turn id 继续嫁接。
    if (liveTurnsSessionIdRef.current === activeSessionId) {
      setSessionId(activeSessionId)
      const snapshot = liveTurnsSnapshotsRef.current.get(activeSessionId)
      if (snapshot) {
        setTurns(snapshot)
        liveTurnsSnapshotsRef.current.delete(activeSessionId)
      }
      return
    }
    setSessionId(activeSessionId)
    setHistoryLoadError(false)
    setIsLoadingHistory(true)
    app.GetSessionMessages(activeSessionId).then(msgs => {
      if (seq !== historySeqRef.current) return
      if (msgs) {
        setTurns(rebuildTurns(msgs))
      }
    }).catch((err) => {
      if (seq !== historySeqRef.current) return
      console.error('Load messages failed', err)
      setHistoryLoadError(true)
    }).finally(() => {
      if (seq === historySeqRef.current) setIsLoadingHistory(false)
    })
  }, [app, activeSessionId, novelId, historyLoadRetry])

  const handleMouseDown = useCallback((e: ReactMouseEvent) => {
    e.preventDefault()
    setIsDragging(true)
    startXRef.current = e.clientX
    startWidthRef.current = width
  }, [width])

  useEffect(() => {
    if (!isDragging) return
    const previousUserSelect = document.body.style.userSelect
    document.body.style.userSelect = 'none'
    const handleMouseMove = (e: MouseEvent) => {
      const delta = startXRef.current - e.clientX
      const nextWidth = clampPanelWidth(
        startWidthRef.current + delta,
        LAYOUT_LIMITS.chat.min,
        LAYOUT_LIMITS.chat.max,
        LAYOUT_LIMITS.chat.fallback,
      )
      latestWidthRef.current = nextWidth
      onWidthChange(nextWidth)
    }
    const handleMouseUp = () => {
      setIsDragging(false)
      onWidthCommit(latestWidthRef.current)
    }
    document.addEventListener('mousemove', handleMouseMove)
    document.addEventListener('mouseup', handleMouseUp)
    return () => {
      document.body.style.userSelect = previousUserSelect
      document.removeEventListener('mousemove', handleMouseMove)
      document.removeEventListener('mouseup', handleMouseUp)
    }
  }, [isDragging, onWidthChange, onWidthCommit])

  const handleResizeKeyDown = useCallback((e: ReactKeyboardEvent) => {
    const step = e.shiftKey ? 40 : 16
    let nextWidth: number
    if (e.key === 'ArrowLeft') {
      nextWidth = width + step
    } else if (e.key === 'ArrowRight') {
      nextWidth = width - step
    } else if (e.key === 'Home') {
      nextWidth = LAYOUT_LIMITS.chat.min
    } else if (e.key === 'End') {
      nextWidth = LAYOUT_LIMITS.chat.max
    } else {
      return
    }
    e.preventDefault()
    const clamped = clampPanelWidth(
      nextWidth,
      LAYOUT_LIMITS.chat.min,
      LAYOUT_LIMITS.chat.max,
      LAYOUT_LIMITS.chat.fallback,
    )
    onWidthChange(clamped)
    onWidthCommit(clamped)
  }, [onWidthChange, onWidthCommit, width])

  // 清理事件监听器
  useEffect(() => {
    const eventQueues = eventQueuesRef.current
    const turnSubs = turnSubsRef.current
    return () => {
      turnSubs.forEach(sub => {
        sub.started?.()
        sub.agent?.()
      })
      turnSubs.clear()
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
    // R5：离开仍在流式的会话时留存快照，等效关系保留到本轮收尾——
    // 切回时恢复快照，而不是重放历史把在途回复冲掉。
    const liveSessionId = liveTurnsSessionIdRef.current
    if (liveSessionId && liveSessionId !== sid) {
      liveTurnsSnapshotsRef.current.set(liveSessionId, turnsRef.current)
    }
    setActiveSessionId(sid)
    app.SetLastSession(sid).catch(err => console.warn('SetLastSession failed', err))
    app.GetSession(sid).then(detail => {
      if (detail?.usage) {
        setLastUsage(detail.usage as unknown as UsageInfo)
      } else {
        setLastUsage(null)
      }
    }).catch(() => setLastUsage(null))
  }, [app])

  const handleNewChat = useCallback(() => {
    const liveSessionId = liveTurnsSessionIdRef.current
    if (liveSessionId) {
      liveTurnsSnapshotsRef.current.set(liveSessionId, turnsRef.current)
      liveTurnsSessionIdRef.current = null
    }
    setActiveSessionId(null)
    setTurns([])
    setSessionId('')
    setLastUsage(null)
    app.GetSessions({ novel_id: novelId, page: 1, size: 5, search: '' }).then(r => {
      if (r) { setSessions(r.items); setSessionsTotal(r.total) }
    }).catch((err) => {
      console.error('Refresh sessions failed', err)
      notifyChatFailure('会话列表刷新失败', err)
    })
  }, [novelId, app])

  const handleOpenHistory = useCallback(() => {
    setShowHistoryPanel(true)
  }, [])

  const handleCloseHistory = useCallback(() => {
    setShowHistoryPanel(false)
  }, [])

  const loadSlash = useCallback(async () => {
    if (!novelId) { setSlashCommands([]); return }
    try {
      const list = await app.ListSlashCommands({ novel_id: novelId })
      setSlashCommands(list ?? [])
    } catch (err) {
      console.error('Load slash commands failed', err)
      notifyChatFailure('技能列表加载失败', err)
    }
  }, [app, novelId])

  useEffect(() => { loadSlash() }, [loadSlash])

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
      case AgentEventType.Compression: {
        const phase = (event.compression_phase || 'started') as 'compressing' | 'done'
        if (event.sub_task_id) {
          setTurns(prev => prev.map(turn => {
            if (turn.turnId !== turnId) return turn
            const subIdx = turn.segments.findIndex(s =>
              s.type === 'subagent' && s.taskId === event.sub_task_id
            )
            if (subIdx < 0) {
              turn.segments.push({
                ...emptySegment(`subagent_${event.sub_task_id}`),
                type: 'subagent',
                status: 'streaming',
                agentType: 'review' as const,
                taskId: event.sub_task_id,
                segments: [{
                  ...emptySegment(`comp_${++counterRef.current}`),
                  type: 'compression',
                  compressionPhase: phase,
                }],
              })
              return turn
            }
            const subSeg = { ...turn.segments[subIdx] }
            if (!subSeg.segments) subSeg.segments = []
            const subSegs = [...subSeg.segments]
            const compIdx = subSegs.findIndex(s => s.type === 'compression')
            if (compIdx >= 0) {
              subSegs[compIdx] = { ...subSegs[compIdx], compressionPhase: phase }
            } else {
              subSegs.push({
                ...emptySegment(`comp_${++counterRef.current}`),
                type: 'compression',
                compressionPhase: phase,
              })
            }
            subSeg.segments = subSegs
            const newSegs = [...turn.segments]
            newSegs[subIdx] = subSeg
            return { ...turn, segments: newSegs }
          }))
          return
        }
        setTurns(prev => prev.map(turn => {
          if (turn.turnId !== turnId) return turn
          const compIdx = turn.segments.findIndex(s => s.type === 'compression')
          if (compIdx >= 0) {
            const segs = [...turn.segments]
            segs[compIdx] = { ...segs[compIdx], compressionPhase: phase }
            return { ...turn, segments: segs }
          }
          return {
            ...turn,
            segments: [...turn.segments, {
              ...emptySegment(`comp_${++counterRef.current}`),
              type: 'compression' as const,
              compressionPhase: phase,
            }],
          }
        }))
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
          const toolStatus =
            event.phase === 'awaiting_approval' ? 'awaiting_approval' as const
            : event.phase === 'completed' ? 'completed' as const
            : event.phase === 'failed' ? 'failed' as const
            : 'executing' as const

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
                agentType,
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

          const approvalType = toolStatus === 'awaiting_approval'
            ? (event.metadata?.approval_type as string | undefined)
            : undefined
          const approvalPayload = toolStatus === 'awaiting_approval'
            ? (event.metadata?.payload as Record<string, unknown> | undefined)
            : undefined

          if (idx >= 0) {
            segments[idx] = {
              ...segments[idx],
              toolName: event.tool_name || segments[idx].toolName,
              toolId: event.tool_id || segments[idx].toolId,
              toolStatus,
              displayText: event.display_text || segments[idx].displayText,
              activityKind: event.activity_kind || segments[idx].activityKind || '',
              error: event.error || '',
              approvalType: approvalType ?? segments[idx].approvalType,
              approvalPayload: approvalPayload ?? segments[idx].approvalPayload,
              result: toolStatus === 'completed' ? (event.metadata || segments[idx].result) : segments[idx].result,
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
              approvalType,
              approvalPayload,
              result: toolStatus === 'completed' ? event.metadata : undefined,
            })
          }

          // 文件编辑审批 → 通知 ContentPanel 打开 diff 标签页
          if (toolStatus === 'awaiting_approval' && approvalType === 'file_edit' && approvalPayload) {
            const p = approvalPayload
            const path = (p.path as string) || ''
            let title = `diff: ${path}`
            if (path.startsWith('chapters/')) {
              const num = path.replace('chapters/', '').replace('.md', '')
              title = `diff: 第${parseInt(num)}章`
            } else if (path === 'novelist.md') {
              title = 'diff: 故事状态'
            } else if (path.startsWith('outlines/')) {
              const num = path.replace('outlines/', '').replace('.md', '')
              title = `diff: 第${parseInt(num)}章大纲`
            }
            onApprovalFileEditRef.current?.({
              path,
              title,
              diff: '',
              original: (p.original as string) || '',
              modified: (p.modified as string) || '',
              changeType: (p.change_type as string) || '',
              reason: (p.reason as string) || '',
              toolId: (event.tool_id as string) || '',
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

  const refreshModels = useCallback(() => {
    app.GetModels().then(list => {
      if (list && list.length > 0) setModels(list)
    }).catch(err => {
      notifyChatFailure('刷新模型列表失败', err)
    })
  }, [app])

  const handleSelectModel = useCallback((key: string) => {
    const previous = selectedKey
    setSelectedKey(key)
    const m = models.find(x => x.Key === key)
    let effort = ''
    if (m?.ReasoningLevels?.length) {
      effort = m.ReasoningLevels[0]
      setReasoningEffort(effort)
    }
    app.SetSelectedModel(key, effort).catch(err => {
      // 选中态必须与后端实际使用的模型一致，失败即回滚（U3 同类）。
      setSelectedKey(previous)
      notifyChatFailure('切换模型失败，仍使用原模型', err)
    })
  }, [models, app, selectedKey])

  const handleSelectEffort = useCallback((effort: string) => {
    const previous = reasoningEffort
    setReasoningEffort(effort)
    app.SetReasoningEffort(effort).catch(err => {
      setReasoningEffort(previous)
      notifyChatFailure('切换推理力度失败', err)
    })
  }, [app, reasoningEffort])

  const handleToggleApproval = useCallback(() => {
    const previous = approvalMode
    const next = previous === 'manual' ? 'auto' : 'manual'
    setApprovalMode(next)
    app.SetApprovalMode(next).catch(err => {
      // U3：审批模式是安全设置——后端没切换成功就必须回滚显示，
      // 否则作者以为收紧了权限而后端仍在旧模式执行工具。
      setApprovalMode(previous)
      notifyChatFailure(`切换审批模式失败，仍保持${previous === 'manual' ? '手动' : '自动'}审批`, err)
    })
  }, [approvalMode, app])

  const handleCompress = useCallback(async () => {
    if (!sessionId || !selectedKey || compressingRef.current) return
    const [providerName, modelID] = selectedKey.split('/')
    if (!providerName || !modelID) return

    compressingRef.current = true
    setIsCompressing(true)
    // 创建压缩中 turn（用于动画展示）
    const compTurnId = `comp_${++counterRef.current}`
    const compressingTurn: Turn = {
      id: compTurnId,
      turnId: 0,
      userMessage: '',
      segments: [{
        ...emptySegment(compTurnId),
        type: 'compression' as const,
        compressionPhase: 'compressing' as const,
      }],
      status: 'done' as const,
      compressionOnly: true,
    }
    setTurns(prev => [...prev, compressingTurn])

    try {
      const result = await app.CompressContext({
        session_id: sessionId,
        provider_name: providerName,
        model_id: modelID,
      })
      // 更新：回填真实 turnId + 完成状态
      setTurns(prev => prev.map(t => {
        if (t.id === compTurnId) {
          return {
            ...t,
            turnId: result.turn_id,
            segments: t.segments.map(s => s.type === 'compression' ? { ...s, compressionPhase: 'done' as const } : s),
          }
        }
        return t
      }))
    } catch {
      // 压缩失败，移除 compressing turn
      setTurns(prev => prev.filter(t => t.id !== compTurnId))
    } finally {
      setIsCompressing(false)
      compressingRef.current = false
    }
  }, [sessionId, selectedKey, app])

  const handleSend = useCallback(async (content: string) => {
    if (!selectedKey) return
    const [p, m] = selectedKey.split('/')
    // 上一轮遗留的"待取消"意图不能顺延到新一轮
    pendingCancelRef.current = false
    activeCountRef.current++
    if (activeCountRef.current > 1) {
      const previousSessionId = activeTurnSessionIdRef.current ?? sessionId
      if (previousSessionId) {
        app.CancelChat(previousSessionId).catch(err => {
          console.error('Cancel previous chat failed', err)
          notifyChatFailure('停止上一轮的请求未送达', err)
        })
      }
    }
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
    const subs: TurnSubscription = { started: null, agent: null, backendTurnId: null }
    turnSubsRef.current.set(turnId, subs)
    const startedCleanup = EventsOn('chat:started', (data: ChatStartedEvent) => {
      // chat:started 是全局事件，收到的第一条即属于本轮；立即退订，
      // 避免这个监听器在后续轮次里再次触发、把别人的 turn_id 记到自己名下。
      subs.started?.()
      subs.started = null

      if (data.session_id) {
        setSessionId(data.session_id)
        setActiveSessionId(data.session_id)
        // O18：记录"正在流式输出的这一轮"的会话 id——作者中途切换历史会话后，
        // sessionId state 已不是本轮的会话，停止必须以这个引用为准。
        activeTurnSessionIdRef.current = data.session_id
        // O21：标记 turns 属于本场流式，历史加载 effect 不得在中途重放。
        liveTurnsSessionIdRef.current = data.session_id
        app.SetLastSession(data.session_id).catch(err => console.warn('SetLastSession failed', err))
        // 作者在会话 id 到手之前就按了停止：此刻补发，否则取消请求会被后端空 id 分支丢弃
        if (pendingCancelRef.current) {
          pendingCancelRef.current = false
          app.CancelChat(data.session_id).catch(err => {
            notifyChatFailure('停止会话的请求未送达', err)
          })
        }
      }

      // 更新 turn 的 turnId 为后端分配的真实值
      setTurns(prev => prev.map(t =>
        t.id === turnId ? { ...t, turnId: data.turn_id } : t
      ))

      subs.backendTurnId = data.turn_id
      subs.agent?.()
      subs.agent = EventsOn(`agent:${data.turn_id}`, handleAgentEvent(data.turn_id))
    })
    subs.started = startedCleanup

    try {
      await app.Chat({
        session_id: sessionId,
        novel_id: novelId,
        message: content,
        provider_name: p,
        model_id: m,
        reasoning_effort: reasoningEffort,
        chapter_number: effectiveChapterRef.current,
      })
      // 刷新会话列表
      app.GetSessions({ novel_id: novelId, page: 1, size: 5, search: '' }).then(r => {
        if (r) { setSessions(r.items); setSessionsTotal(r.total) }
      }).catch((err) => {
        console.error('Post-send refresh sessions failed', err)
        notifyChatFailure('会话列表刷新失败', err)
      })
    } catch (err) {
      setTurns(prev => prev.map(t => {
        if (t.id !== turnId) return t
        if (t.status === 'stopped') return t
        return { ...t, status: 'interrupted' as const, errorMessage: describeError(err) }
      }))
    } finally {
      // 只收尾本轮的队列。此前这里遍历并清空了整张表，
      // 于是先结束的一轮会把仍在进行的另一轮的待排序事件一起丢掉。
      const backendTurnId = subs.backendTurnId
      if (backendTurnId !== null) {
        const queue = eventQueuesRef.current.get(backendTurnId)
        if (queue?.flushTimer) {
          clearTimeout(queue.flushTimer)
          queue.flushTimer = null
        }
        flushEventQueue(backendTurnId, true)
        eventQueuesRef.current.delete(backendTurnId)
      }
      setTurns(prev => prev.map(t =>
        t.id === turnId && t.status === 'streaming'
          ? { ...t, status: 'done' as const, segments: t.segments.map(seg =>
              seg.type === 'text' ? { ...seg, isStreaming: false } : seg
            )}
          : t
      ))
      activeCountRef.current--
      if (activeCountRef.current === 0) {
        setIsLoading(false)
        activeTurnSessionIdRef.current = null
        // 本轮流式结束：快照作废（服务端已有完整记录），之后切回应走正常历史重放。
        const finishedLiveSessionId = liveTurnsSessionIdRef.current
        liveTurnsSessionIdRef.current = null
        if (finishedLiveSessionId) {
          liveTurnsSnapshotsRef.current.delete(finishedLiveSessionId)
        }
      }
      turnSubsRef.current.delete(turnId)
      subs.started?.()
      subs.started = null
      subs.agent?.()
      subs.agent = null
    }
  }, [sessionId, novelId, selectedKey, reasoningEffort, app, handleAgentEvent, flushEventQueue, activeSessionId])

  const handleStop = useCallback(() => {
    setTurns(prev => prev.map(t =>
      t.status === 'streaming'
        ? { ...t, status: 'stopped' as const }
        : t
    ))
    // O18：取消目标是"正在流式输出的那一轮"的会话，而不是 sessionId state——
    // 作者可能在中途把左侧会话切到了别的历史会话。
    const targetSessionId = activeTurnSessionIdRef.current ?? sessionId
    if (targetSessionId) {
      pendingCancelRef.current = false
      app.CancelChat(targetSessionId).catch(err => {
        console.error('Cancel chat failed', err)
        notifyChatFailure('停止会话的请求未送达', err)
      })
      return
    }
    // 会话尚未建立，取消意图先挂起，由 chat:started 拿到 session id 后补发
    pendingCancelRef.current = true
  }, [app, sessionId])

  // 审批提交失败后的出路：工具片段只会因后端后续 ToolCall 事件改变状态，
  // 而提交失败时那个事件永远不会到，卡片会一直钉在"等待审批"。
  // 这里把它标成失败并停掉本轮，作者才能继续往下写。
  const handleAbandonApproval = useCallback((toolId: string) => {
    setTurns(prev => prev.map(turn => ({
      ...turn,
      segments: turn.segments.map(seg =>
        seg.type === 'tool' && seg.toolId === toolId && seg.toolStatus === 'awaiting_approval'
          ? { ...seg, toolStatus: 'failed' as const, error: seg.error || '已结束本轮，审批未提交' }
          : seg
      ),
    })))
    handleStop()
  }, [handleStop])

  const hasNovel = novelId > 0
  const hasTurns = turns.length > 0
  const hasActiveSession = activeSessionId !== undefined && activeSessionId !== null
  const showRecent = !hasActiveSession && !hasTurns && !isLoading


  const inputPlaceholder = !hasNovel
    ? '请先选择作品'
    : !selectedKey
      ? '请先配置模型'
      : '输入消息，按 / 调用技能...'

  return (
    <aside className="shrink-0 flex flex-col bg-sidebar border-l relative overflow-hidden" style={{ width }}>
      <div
        role="separator"
        aria-label="调整对话面板宽度"
        aria-orientation="vertical"
        aria-valuemin={LAYOUT_LIMITS.chat.min}
        aria-valuemax={LAYOUT_LIMITS.chat.max}
        aria-valuenow={Math.round(width)}
        tabIndex={0}
        className="absolute left-0 top-0 bottom-0 w-1 cursor-col-resize hover:bg-primary/30 focus-visible:bg-primary/30 focus-visible:outline-none transition-colors z-10 select-none"
        style={{ marginLeft: -2 }}
        onMouseDown={handleMouseDown}
        onKeyDown={handleResizeKeyDown}
      />

      <div className="px-4 py-2.5 border-b shrink-0 flex items-center justify-between select-none">
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

      {initLoadError && (
        <div className="px-4 py-2 bg-danger-bg border-b border-danger-border text-xs text-red-600 flex items-center justify-between shrink-0">
          <span>加载设置失败，模型列表和偏好可能不准确</span>
          <button
            onClick={() => setInitLoadRetry(n => n + 1)}
            className="underline hover:text-destructive cursor-pointer"
          >
            重试
          </button>
        </div>
      )}

      <div className="absolute left-0 right-0 top-[41px] bottom-0 pointer-events-none z-30">
        <SessionHistory
          open={showHistoryPanel}
          novelId={novelId}
          onClose={handleCloseHistory}
          onSelectSession={handleSelectSession}
        />
      </div>

      <div ref={scrollContainerRef} onScroll={handleMessagesScroll} className="flex-1 overflow-y-auto overscroll-contain px-3 py-3 relative">
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
            {historyLoadError ? (
              <div className="flex items-center justify-center h-full">
                <div className="text-center">
                  <p className="text-sm text-red-500 mb-2">加载消息失败</p>
                  <button
                    onClick={() => setHistoryLoadRetry(n => n + 1)}
                    className="text-xs text-primary underline cursor-pointer"
                  >
                    重试
                  </button>
                </div>
              </div>
            ) : !hasTurns && !isLoading ? (
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

                        if (seg.toolName === 'web_search' && seg.toolStatus === 'completed' && seg.result) {
                          return <WebSearchCard key={seg.id} result={seg.result} />
                        }
                        if (seg.toolName === 'web_fetch' && seg.toolStatus === 'completed' && seg.result) {
                          return <WebFetchCard key={seg.id} result={seg.result} displayText={seg.displayText} />
                        }
                        if (seg.toolName === 'corpus_injection' && seg.toolStatus === 'completed' && seg.result?.automatic) {
                          const materials = (seg.result.materials as CorpusUsageMaterial[] | undefined) ?? []
                          return <CorpusUsageCard key={seg.id} materials={materials} novelId={novelId} />
                        }

                        return (
                          <ToolCallCard
                            key={seg.id}
                                                    displayText={seg.displayText}
                            status={seg.toolStatus}
                            activityKind={seg.activityKind}
                            error={seg.error}
                            approvalType={seg.approvalType}
                            approvalPayload={seg.approvalPayload}
                            onApprove={
                              seg.toolStatus === 'awaiting_approval'
                                ? (feedback: string) => onApprove(seg.toolId, feedback)
                                : undefined
                            }
                            onReject={
                              seg.toolStatus === 'awaiting_approval'
                                ? (feedback: string) => onReject(seg.toolId, feedback)
                                : undefined
                            }
                            onEndTurn={
                              seg.toolStatus === 'awaiting_approval'
                                ? () => handleAbandonApproval(seg.toolId)
                                : undefined
                            }
                          />
                        )
                      }

                      if (seg.type === 'compression') {
                        return (
                          <CompressionBlock
                            key={seg.id}
                            phase={seg.compressionPhase || 'compressing'}
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
                          {seg.content && (() => {
                            const { body, options } = parseChoices(seg.content)
                            return (
                              <>
                                <MessageBubble role="assistant" content={body} />
                                {options.length > 0 && !isLoading && (
                                  <ChoiceBlock options={options} onPick={(option) => { void handleSend(option) }} />
                                )}
                              </>
                            )
                          })()}
                        </div>
                      )
                    })}

                    {turn.status === 'failed' && turn.errorMessage && (
                      <div className="flex justify-start">
                        <div className="bg-danger-bg border border-danger-border rounded-lg px-3 py-2 text-xs text-red-600 max-w-[80%] flex flex-col gap-2">
                          <span>{turn.errorMessage}</span>
                          {turn.userMessage && (
                            <button
                              type="button"
                              disabled={isLoading}
                              onClick={() => handleSend(turn.userMessage)}
                              className="self-start rounded-md border border-danger-border bg-background/70 px-2 py-1 text-xs text-red-600 hover:bg-background disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer"
                            >
                              重试
                            </button>
                          )}
                        </div>
                      </div>
                    )}
                    {turn.status === 'interrupted' && (
                      <div className="flex justify-center">
                        <div className="bg-danger-bg border border-danger-border rounded-lg px-3 py-2 text-xs text-red-500 max-w-[80%] flex flex-col gap-2">
                          <span>{turn.errorMessage || '对话被中断'}</span>
                          {turn.userMessage && (
                            <button
                              type="button"
                              disabled={isLoading}
                              onClick={() => handleSend(turn.userMessage)}
                              className="self-start rounded-md border border-danger-border bg-background/70 px-2 py-1 text-xs text-red-600 hover:bg-background disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer"
                            >
                              重试
                            </button>
                          )}
                        </div>
                      </div>
                    )}
                    {turn.status === 'stopped' && (
                      <div className="flex justify-center">
                        <div className="bg-muted/50 border rounded-lg px-3 py-2 text-xs text-muted-foreground max-w-[80%]">
                          对话已停止
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

      {/* 章节语境行：当前为第几章写作 + 访谈逃生门（直接开写）。 */}
      <div className="relative mx-4 mb-1.5 flex items-center gap-1.5" data-testid="chapter-context-row">
        <button
          type="button"
          onClick={() => {
            setBindingMenuOpen((open) => !open)
            setPinnedDraft(chapterBinding.mode === 'pinned' ? String(chapterBinding.chapter) : '')
          }}
          aria-haspopup="menu"
          aria-expanded={bindingMenuOpen}
          className={`inline-flex h-6 items-center gap-1 rounded-md border px-2 text-[11px] transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
            effectiveChapterNumber
              ? chapterBinding.mode === 'pinned'
                ? 'border-tag-amber text-tag-amber-foreground bg-tag-amber/30'
                : 'border-primary/30 text-primary bg-primary/5'
              : 'border-border text-muted-foreground bg-muted/30'
          }`}
          data-testid="chapter-context-badge"
        >
          {effectiveChapterNumber
            ? chapterBinding.mode === 'pinned'
              ? (<><Lock className="h-3 w-3" aria-hidden="true" />第 {effectiveChapterNumber} 章 · 已锁定（细纲取「下一章」槽）</>)
              : (<><Link2 className="h-3 w-3" aria-hidden="true" />第 {effectiveChapterNumber} 章 · 语料注入开启</>)
            : (<><Link2Off className="h-3 w-3" aria-hidden="true" />未绑定章节 · 注入关闭</>)}
          <ChevronDown className="h-3 w-3 opacity-60" aria-hidden="true" />
        </button>
        {hasNovel && (
          <button
            type="button"
            onClick={() => { void handleSend(DIRECT_WRITE_MESSAGE) }}
            disabled={!selectedKey || isLoading}
            title="跳过剩余访谈，让 AI 立即按当前细纲开始写正文"
            className="inline-flex h-6 items-center gap-1 rounded-md border border-border bg-background px-2 text-[11px] text-muted-foreground transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
            data-testid="direct-write-button"
          >
            <PenLine className="h-3 w-3" aria-hidden="true" />
            直接开写
          </button>
        )}
        {hasNovel && (
          <button
            type="button"
            onClick={() => { void handleFinishChapter() }}
            disabled={isLoading || advancingChapter}
            title="本章已完成：把细纲并入部纲、部纲并入大纲，腾出下一章细纲"
            className="inline-flex h-6 items-center gap-1 rounded-md border border-border bg-background px-2 text-[11px] text-muted-foreground transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
            data-testid="finish-chapter-button"
          >
            <CheckCircle2 className="h-3 w-3" aria-hidden="true" />
            {advancingChapter ? '推进中…' : '完成本章'}
          </button>
        )}

        {bindingMenuOpen && (
          <div
            role="menu"
            aria-label="章节绑定设置"
            className="absolute bottom-full left-0 z-40 mb-1 w-56 rounded-md border border-border bg-background p-2 shadow-lg"
          >
            <p className="px-1 pb-1.5 text-[11px] text-muted-foreground">本章语料注入绑定的章节</p>
            <div className="flex items-center gap-1.5">
              <input
                value={pinnedDraft}
                onChange={(event) => { setPinnedDraft(event.target.value.replace(/[^0-9]/g, '').slice(0, 6)) }}
                placeholder="章号，如 3"
                inputMode="numeric"
                aria-label="锁定章号"
                className="h-7 min-w-0 flex-1 rounded border border-border bg-background px-2 text-[11px] text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
              <button
                type="button"
                disabled={!pinnedDraft || Number(pinnedDraft) <= 0}
                onClick={() => {
                  setChapterBinding({ mode: 'pinned', chapter: Number(pinnedDraft) })
                  setBindingMenuOpen(false)
                }}
                className="h-7 rounded bg-primary px-2 text-[11px] font-medium text-primary-foreground disabled:opacity-50"
              >
                锁定
              </button>
            </div>
            <div className="mt-1.5 flex gap-1.5">
              <button
                type="button"
                onClick={() => { setChapterBinding({ mode: 'auto' }); setBindingMenuOpen(false) }}
                className={`h-7 flex-1 rounded border px-2 text-[11px] ${chapterBinding.mode === 'auto' ? 'border-primary text-primary' : 'border-border text-muted-foreground hover:text-foreground'}`}
              >
                跟随编辑器
              </button>
              <button
                type="button"
                onClick={() => { setChapterBinding({ mode: 'off' }); setBindingMenuOpen(false) }}
                className={`h-7 flex-1 rounded border px-2 text-[11px] ${chapterBinding.mode === 'off' ? 'border-primary text-primary' : 'border-border text-muted-foreground hover:text-foreground'}`}
              >
                不绑定
              </button>
            </div>
            <p className="mt-1.5 px-1 text-[10px] leading-relaxed text-muted-foreground">
              「跟随编辑器」按当前打开的章节 tab 自动绑定；锁定后切走 tab 仍保持注入。
              语料注入与覆盖度始终依据时间线中的「细纲」（下一章计划槽）。
            </p>
          </div>
        )}
      </div>

      {effectiveChapterNumber && coverageState === 'ready' && chapterCoverage && chapterCoverage.total_count === 0 && (
        <div
          className="mx-4 mb-1.5 rounded-md border border-border bg-muted/30 px-2.5 py-2 text-[11px] text-muted-foreground"
          data-testid="chapter-coverage-banner"
        >
          <div className="flex items-center justify-between gap-2">
            <span className="font-medium">本章还没有细纲</span>
            {onOpenPlans && (
              <button
                type="button"
                onClick={onOpenPlans}
                className="shrink-0 rounded px-1.5 py-0.5 underline underline-offset-2 hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                去创建
              </button>
            )}
          </div>
          <p className="mt-1 leading-relaxed">创建细纲后，AI 将按细纲检索参考语料并显示覆盖度信号。</p>
        </div>
      )}

      {effectiveChapterNumber && coverageState === 'error' && (
        <div
          className="mx-4 mb-1.5 rounded-md border border-destructive/30 bg-destructive/5 px-2.5 py-2 text-[11px] text-destructive"
          data-testid="chapter-coverage-banner"
          role="alert"
        >
          <div className="flex items-center justify-between gap-2">
            <span className="font-medium">覆盖度计算失败，语料信号暂不可用</span>
            <button
              type="button"
              onClick={() => { loadCoverage({ refresh: true }) }}
              className="shrink-0 rounded px-1.5 py-0.5 underline underline-offset-2 hover:opacity-80 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              重试
            </button>
          </div>
        </div>
      )}

      {effectiveChapterNumber && coverageState === 'loading' && !chapterCoverage && (
        <div
          className="mx-4 mb-1.5 rounded-md border border-border bg-muted/30 px-2.5 py-2 text-[11px] text-muted-foreground"
          data-testid="chapter-coverage-banner"
        >
          正在计算本章语料覆盖度…
        </div>
      )}

      {effectiveChapterNumber && chapterCoverage && chapterCoverage.total_count > 0 && (
        <div
          className={`mx-4 mb-1.5 rounded-md border px-2.5 py-2 text-[11px] ${chapterCoverage.sufficient ? 'border-border bg-muted/30 text-muted-foreground' : 'border-border bg-tag-amber text-tag-amber-foreground'}`}
          data-testid="chapter-coverage-banner"
        >
          <div className="flex items-center justify-between gap-2">
            <span className="font-medium">
              {chapterCoverage.sufficient ? '语料覆盖' : '语料不足'}
              ：{Math.round(chapterCoverage.coverage_ratio * 100)}%（{chapterCoverage.covered_count}/{chapterCoverage.total_count} beat）
            </span>
            <button
              type="button"
              disabled={coverageState === 'loading'}
              onClick={() => { loadCoverage({ refresh: true }) }}
              className="shrink-0 rounded px-1.5 py-0.5 underline-offset-2 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
            >
              刷新
            </button>
          </div>
          {chapterCoverage.truncated && (
            <p className="mt-1 leading-relaxed opacity-80">细纲超过 40 个 beat，仅统计前 40 个。</p>
          )}
          {!chapterCoverage.sufficient && (
            <p className="mt-1 leading-relaxed">
              可直写（AI 会诚实标注语料不足），或先到「语料」区导入同类参考书补足 beat 对应素材。
              {chapterCoverage.source_books && chapterCoverage.source_books.length > 0 && (
                <span className="opacity-80">
                  {' '}现有语料来源：{chapterCoverage.source_books.map((book) => `《${book}》`).join('')}。
                </span>
              )}
            </p>
          )}
          {chapterCoverage.beats.filter((beat) => !beat.covered).length > 0 && (
            <ul className="mt-1 space-y-0.5">
              {chapterCoverage.beats.filter((beat) => !beat.covered).slice(0, 3).map((beat) => (
                <li key={beat.beat} className="truncate">· {beat.beat}</li>
              ))}
            </ul>
          )}
        </div>
      )}

      <ChatInput
        disabled={!hasNovel || !selectedKey}
        isLoading={isLoading}
        placeholder={inputPlaceholder}
        slashItems={slashCommands}
        onSend={handleSend}
        onListSlash={loadSlash}
        onStop={handleStop}
      />

      <div className="border-t mx-4" />

      <ChatControls
        models={models}
        selectedKey={selectedKey}
        onSelectModel={handleSelectModel}
        onRefreshModels={refreshModels}
        reasoningEffort={reasoningEffort}
        onSelectEffort={handleSelectEffort}
        approvalMode={approvalMode}
        onToggleApproval={handleToggleApproval}
        onConfigModel={handleConfigModel}
        usage={lastUsage}
        onCompress={handleCompress}
        isTurnRunning={isLoading}
        isCompressing={isCompressing}
      />

      {isDragging && (
        <div className="fixed inset-0 z-50 cursor-col-resize select-none" />
      )}

      <SettingsDialog
        open={showSettings}
        onClose={() => setShowSettings(false)}
        onSaved={() => {
          app.GetModels().then(list => {
            if (list && list.length > 0) {
              setModels(list)
              if (!list.find(m => m.Key === selectedKey)) {
                setSelectedKey(list[0].Key)
                if (list[0].ReasoningLevels?.length) {
                  setReasoningEffort(list[0].ReasoningLevels[0])
                }
              }
            }
          }).catch(err => {
            notifyChatFailure('保存后刷新模型列表失败', err)
          })
        }}
        initialTab="model"
      />
    </aside>
  )
}
