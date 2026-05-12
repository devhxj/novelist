import { useState, useEffect, useRef, useCallback, useMemo } from 'react'
import { flushSync } from 'react-dom'
import { useParams, useNavigate } from 'react-router-dom'
import Editor, { type OnMount } from '@monaco-editor/react'
import { Select, Segmented, Tooltip, message, Modal, Input, Collapse } from 'antd'
import {
  ArrowLeftOutlined,
  CheckOutlined,
  CloseOutlined,
  PlusOutlined,
  FileTextOutlined,
  MessageOutlined,
  LoadingOutlined,
  CheckCircleOutlined,
  CloseCircleOutlined,
  LeftOutlined,
  RightOutlined,
  BgColorsOutlined,
  EditOutlined,
  SaveOutlined,
  ReadOutlined,
} from '@ant-design/icons'
import { wsEditorService } from '@/services/wsEditorService'
import { editorApi } from '@/services/editorService'
import { chapterApi } from '@/services/chapterService'
import { sessionApi } from '@/services/sessionService'
import type {
  ServerMsg, DiffData,
  SessionCreatedMsg, SessionListMsg, ContentChunkMsg, ThinkingChunkMsg,
  ThinkingDoneMsg, ToolCallMsg,
  EditStartedMsg, EditAppliedMsg, EditAcceptedMsg, EditRejectedMsg,
  ErrorMsg,
  SessionLoadedMsg,
  EditPreviewMsg,
  EditPendingMsg,
  ReasoningEffort,
  OutlineGeneratedMsg,
} from '@/services/wsEditorService'
import { Markdown } from '@/components/Markdown'
import styles from './EditorPage.module.css'

interface ChapterInfo {
  id: number
  chapter_number: number
  title: string
  status: string
  word_count: number
}

interface SessionInfo {
  session_id: string
  display_name: string
  message_count: number
  updated_at: string
}

interface ToolCallInfo {
  tool_name: string
  status: 'executing' | 'completed' | 'failed' | 'rejected'
  tool_id?: string
  task_id?: string
  phase?: 'selected' | 'executing' | 'completed' | 'failed'
  display_text?: string
  activity_kind?: 'general' | 'browse' | 'view' | 'edit' | 'write' | 'create' | 'memory' | 'review' | 'plan'
  chapter_id?: number
  chapter_number?: number
  chapter_title?: string
  arguments?: Record<string, unknown>
  result_summary?: {
    success?: boolean
    error?: string | null
    metadata?: Record<string, unknown>
    data_keys?: string[]
  }
  error?: string
  timestamp?: string
}

interface TurnSegmentText {
  id: string
  type: 'text'
  content: string
  thinkingContent: string
  thinkingDone: boolean
  isStreaming: boolean
}

interface TurnSegmentTool {
  id: string
  type: 'tool'
  call: ToolCallInfo
}

interface TurnSegmentOutline {
  id: string
  type: 'outline'
  content: string
  outlines: Array<Record<string, unknown>>
  approvalState: 'pending' | 'approved' | 'rejected'
  feedbackDraft?: string
}

interface TurnSegmentSubagent {
  id: string
  type: 'subagent'
  agentType: 'memory' | 'review'
  taskId: string
  segments: TurnSegment[]
  status: 'streaming' | 'done' | 'failed'
  finalText: string
}

type TurnSegment = TurnSegmentText | TurnSegmentTool | TurnSegmentOutline | TurnSegmentSubagent

interface ConversationTurn {
  id: string
  userMessage?: string
  segments: TurnSegment[]
  status: 'streaming' | 'done' | 'failed'
}

const DEFAULT_MODEL_OPTIONS = [
  { value: 'deepseek-v4-flash', label: 'DeepSeek V4 Flash' },
  { value: 'deepseek-v4-pro', label: 'DeepSeek V4 Pro' },
  { value: 'glm-4.7-flash', label: 'GLM-4.7 Flash' },
]

const REASONING_OPTIONS: Array<{ value: ReasoningEffort; label: string }> = [
  { value: 'high', label: '高' },
  { value: 'max', label: '最大' },
]


function getActivityVisual(kind?: string): { icon: React.ReactNode; label: string; badge: string } {
  switch (kind) {
    case 'view': return { icon: <FileTextOutlined />, label: '查看', badge: '查看中' }
    case 'browse': return { icon: <FileTextOutlined />, label: '浏览', badge: '浏览中' }
    case 'create': return { icon: <PlusOutlined />, label: '创建', badge: '创建中' }
    case 'write': return { icon: <EditOutlined />, label: '写作', badge: '写作中' }
    case 'edit': return { icon: <EditOutlined />, label: '编辑', badge: '编辑中' }
    case 'memory': return { icon: <MessageOutlined />, label: '记忆', badge: '记忆中' }
    case 'review': return { icon: <CheckOutlined />, label: '审阅', badge: '审阅中' }
    case 'plan': return { icon: <FileTextOutlined />, label: '规划', badge: '规划中' }
    default: return { icon: <LoadingOutlined />, label: '处理', badge: '处理中' }
  }
}

function sanitizeToolError(err?: string): string {
  if (!err) return ''
  const maxLen = 120
  let s = err.length > maxLen ? err.slice(0, maxLen) + '...' : err
  s = s.replace(/<[^>]*>/g, '')
  s = s.replace(/\s+/g, ' ').trim()
  return s
}

interface InlineDiffPreviewProps {
  diff: DiffData
}

function InlineDiffPreview({ diff }: InlineDiffPreviewProps) {
  const { additions = 0, deletions = 0 } = diff.summary || {}
  const hunks = diff.hunks || []
  return (
    <div className={styles.diffContainer}>
      <div className={styles.diffSummaryBar}>
        <span className={styles.diffAdditions}>+{additions}</span>
        <span className={styles.diffDeletions}>-{deletions}</span>
      </div>
      {hunks.length === 0 && (
        <div style={{ padding: 16, color: '#999', fontSize: 13 }}>没有可预览的差异。</div>
      )}
      {hunks.map((hunk: any, i: number) => (
        <div key={i} className={styles.diffHunk}>
          {hunk.changes?.map((change: any, ci: number) => (
            <div
              key={ci}
              className={`${styles.diffLine} ${change.type === 'add' ? styles.diffLineAdd : change.type === 'remove' ? styles.diffLineRemove : ''}`}
            >
              <span className={styles.diffLineIndicator}>
                {change.type === 'add' ? '+' : change.type === 'remove' ? '-' : ' '}
              </span>
              <span className={styles.diffLineNumber}>{change.lineNumber ?? ''}</span>
              <span className={styles.diffLineText}>{change.content}</span>
            </div>
          ))}
        </div>
      ))}
    </div>
  )
}

export default function EditorPage() {
  const { novelId, sessionId: urlSessionId } = useParams<{ novelId: string; sessionId?: string }>()
  const navigate = useNavigate()

  const [darkMode, setDarkMode] = useState(() => {
    try { return localStorage.getItem('editor_dark_mode') === 'true' } catch { return false }
  })
  const [connected, setConnected] = useState(false)
  const [chapters, setChapters] = useState<ChapterInfo[]>([])
  const [selectedChapterId, setSelectedChapterId] = useState<number | null>(null)
  const [chapterWordCount, setChapterWordCount] = useState(0)
  const [originalContent, setOriginalContent] = useState('')
  const [workingContent, setWorkingContent] = useState('')
  const editorRef = useRef<any>(null)
  const [editSessionId, setEditSessionId] = useState<string | null>(null)
  const [latestPendingEditSessionId, setLatestPendingEditSessionId] = useState<string | null>(null)
  const [selectedModel, setSelectedModel] = useState('deepseek-v4-flash')
  const [modelOptions, setModelOptions] = useState(DEFAULT_MODEL_OPTIONS)
  const [reasoningEffort, setReasoningEffort] = useState<ReasoningEffort>('high')
  const [changeCount, setChangeCount] = useState(0)
  const [hasActiveEdit, setHasActiveEdit] = useState(false)
  const [showDiff, setShowDiff] = useState(false)
  const [diffData, setDiffData] = useState<DiffData | null>(null)
  const [isSaving, setIsSaving] = useState(false)
  const [isApplyingEdit, setIsApplyingEdit] = useState(false)
  const [editNotice, setEditNotice] = useState<string>('')
  const [editorViewMode, setEditorViewMode] = useState<'content' | 'outline'>('content')
  const [outlineContent, setOutlineContent] = useState<string | null>(null)

  const [leftTab, setLeftTab] = useState<'chapters' | 'sessions'>('chapters')
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false)
  const [sessions, setSessions] = useState<SessionInfo[]>([])
  const [currentSessionId, setCurrentSessionId] = useState<string | null>(null)

  const [turns, setTurns] = useState<ConversationTurn[]>([])
  const [inputValue, setInputValue] = useState('')
  const [isStreaming, setIsStreaming] = useState(false)
  const [collapsedSubagents, setCollapsedSubagents] = useState<Set<string>>(new Set())
  const currentTurnIdRef = useRef<string | null>(null)
  const chatContainerRef = useRef<HTMLDivElement>(null)

  const [createChapterModal, setCreateChapterModal] = useState(false)
  const [newChapterTitle, setNewChapterTitle] = useState('')
  const [newChapterNumber, setNewChapterNumber] = useState<number | null>(null)
  const [creatingChapter, setCreatingChapter] = useState(false)
  const pendingMessageRef = useRef<string | null>(null)
  const pendingInterruptMessageRef = useRef<string | null>(null)
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const turnIdCounter = useRef(0)
  const subTaskMeta = useRef<Map<string, { turnId: string; agentType: string }>>(new Map())
  const segIdCounter = useRef(0)
  const isUserScrolledUp = useRef(false)
  const isProgrammaticScroll = useRef(false)
  const originalContentRef = useRef(originalContent)
  const dispatchMessageRef = useRef<((msg: string) => void) | null>(null)
  const updateTurn = useCallback((turnId: string, updater: (turn: ConversationTurn) => ConversationTurn) => {
    setTurns(prev => prev.map(t => t.id === turnId ? updater(t) : t))
  }, [])

  const nextTurnId = useCallback(() => `turn_${++turnIdCounter.current}`, [])
  const nextSegId = useCallback(() => `seg_${++segIdCounter.current}`, [])

  const applyToSubagent = useCallback((
    turnId: string,
    taskId: string,
    agentType: string,
    updater: (seg: TurnSegmentSubagent) => TurnSegmentSubagent,
  ) => {
    updateTurn(turnId, turn => {
      const idx = turn.segments.findIndex(s => s.type === 'subagent' && s.taskId === taskId)
      if (idx >= 0) {
        const segs = [...turn.segments]
        segs[idx] = updater(segs[idx] as TurnSegmentSubagent)
        return { ...turn, segments: segs }
      }
      const newSeg: TurnSegmentSubagent = {
        id: nextSegId(),
        type: 'subagent',
        agentType: agentType as 'memory' | 'review',
        taskId,
        segments: [],
        status: 'streaming',
        finalText: '',
      }
      return { ...turn, segments: [...turn.segments, newSeg] }
    })
  }, [updateTurn, nextSegId])

  const computedStats = useMemo(() => {
    if (!workingContent) return null
    let chineseChars = 0
    let englishWords = 0
    let spaces = 0
    let punctuation = 0
    const cjkRe = /[一-鿿㐀-䶿　-〿＀-￯]/
    const punctRe = /[，。！？；：""''【】（）、—…《》,.!?;:'"()\-\[\]{}<>\/\\@#$%^&*+=~`]/
    const engWordRe = /[a-zA-Z]+(?:'[a-zA-Z]+)?/g
    for (const ch of workingContent) {
      if (cjkRe.test(ch)) {
        if (punctRe.test(ch)) punctuation++
        else chineseChars++
      } else if (ch === ' ' || ch === '\t' || ch === '\n' || ch === '\r') {
        spaces++
      } else if (punctRe.test(ch)) {
        punctuation++
      }
    }
    const m = workingContent.match(engWordRe)
    englishWords = m ? m.length : 0
    return { chinese_chars: chineseChars, english_words: englishWords, spaces, punctuation, total_count: chineseChars + englishWords }
  }, [workingContent])

  const displayStats = computedStats

  const currentTurnId = currentTurnIdRef.current

  useEffect(() => {
    if (!novelId) return
    const nid = parseInt(novelId)

    wsEditorService.connect(nid).then(() => {
      setConnected(true)
      wsEditorService.listSessions()
    }).catch(() => {
      setConnected(false)
    })

    chapterApi.getChapters(nid, { page_size: 100 }).then(res => {
      if (res.success) {
        setChapters(res.data.items || [])
      }
    }).catch(() => {})

    sessionApi.getModels().then(res => {
      if (res.success && res.data?.models?.length) {
        setModelOptions(res.data.models.map((m: any) => ({ value: m.id, label: m.name })))
      }
    }).catch(() => {})

    return () => {
      wsEditorService.disconnect()
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
    }
  }, [novelId])

  useEffect(() => {
    if (!connected || !urlSessionId) return
    if (urlSessionId === currentSessionId) return
    setTurns([])
    currentTurnIdRef.current = null
    wsEditorService.loadSession(urlSessionId)
  }, [connected, urlSessionId])

  useEffect(() => {
    const el = chatContainerRef.current
    if (!el) return
    const handleScroll = () => {
      if (isProgrammaticScroll.current) return
      isUserScrolledUp.current = el.scrollTop + el.clientHeight < el.scrollHeight - 5
    }
    el.addEventListener('scroll', handleScroll, { passive: true })
    return () => el.removeEventListener('scroll', handleScroll)
  }, [])

  useEffect(() => {
    const hasPending = turns.some(t =>
      t.segments.some(s => s.type === 'outline' && s.approvalState === 'pending')
    )
    if (hasPending) return
    if (isUserScrolledUp.current) return
    const el = chatContainerRef.current
    if (!el) return
    isProgrammaticScroll.current = true
    requestAnimationFrame(() => {
      if (el) el.scrollTop = el.scrollHeight
      requestAnimationFrame(() => {
        isProgrammaticScroll.current = false
      })
    })
  }, [turns])

  useEffect(() => { originalContentRef.current = originalContent }, [originalContent])

  useEffect(() => {
    const hasPending = turns.some(t =>
      t.segments.some(s => s.type === 'outline' && s.approvalState === 'pending')
    )
    if (!hasPending) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault() }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [turns])

  // 子 Agent 完成后延迟 1s 自动折叠
  useEffect(() => {
    const timers: ReturnType<typeof setTimeout>[] = []
    for (const turn of turns) {
      for (const seg of turn.segments) {
        if (seg.type === 'subagent' && seg.status === 'done' && !collapsedSubagents.has(seg.taskId)) {
          timers.push(setTimeout(() => {
            setCollapsedSubagents(prev => new Set([...prev, seg.taskId]))
          }, 1000))
        }
      }
    }
    return () => timers.forEach(clearTimeout)
  }, [turns])

  const refreshChapterList = useCallback(async () => {
    if (!novelId) return
    try {
      const res = await chapterApi.getChapters(parseInt(novelId), { page_size: 100 })
      if (res.success) {
        setChapters(res.data.items || [])
      }
    } catch {
      // noop
    }
  }, [novelId])

  const refreshSelectedChapter = useCallback(async (chapterId: number) => {
    try {
      const res = await editorApi.getChapterForEditor(chapterId)
      if (!res.success) return

      const data = res.data
      setChapterWordCount(data.word_count)
      setOriginalContent(data.content)
      setWorkingContent(data.working_content || data.content)
      setEditSessionId(data.edit_session_id)
      setLatestPendingEditSessionId(data.latest_pending_edit_session_id)
      setHasActiveEdit(data.has_active_edit)
      setChangeCount(data.change_count)
      if (!data.has_active_edit) {
        setShowDiff(false)
        setDiffData(null)
      }
    } catch {
      // noop
    }
  }, [])

  const handleMsg = useCallback((msg: ServerMsg) => {
    console.log('[EditorPage] handleMsg:', msg.type)
    switch (msg.type) {
      case 'session_created': {
        const m = msg as SessionCreatedMsg
        setCurrentSessionId(m.session_id)
        if (m.model) setSelectedModel(m.model)
        if (m.reasoning_effort) setReasoningEffort(m.reasoning_effort)
        navigate(`/novels/${novelId}/editor/${m.session_id}`, { replace: true })
        wsEditorService.listSessions()
        if (pendingMessageRef.current) {
          wsEditorService.chat(m.session_id, pendingMessageRef.current, true)
          pendingMessageRef.current = null
        }
        break
      }
      case 'sessions_list': {
        const m = msg as SessionListMsg
        setSessions(m.sessions)
        break
      }
      case 'session_loaded': {
        const m = msg as SessionLoadedMsg
        console.log('[EditorPage] session_loaded:', m.session_id, 'messages:', m.recent_messages?.length)
        setCurrentSessionId(m.session_id)
        currentTurnIdRef.current = null

        if (m.recent_messages && m.recent_messages.length > 0) {
          const historyTurns: ConversationTurn[] = []
          let currentTaskId: string | null = null
          // 历史重建时的子 Agent segment 索引
          const subagentCache = new Map<string, TurnSegmentSubagent>()

          m.recent_messages.forEach((msg, _i) => {
            const msgTaskId = msg.message_id || `hist_${_i}`

            if (msg.role === 'user') {
              const turnId = `hist_turn_${historyTurns.length}`
              historyTurns.push({
                id: turnId,
                userMessage: msg.content || '',
                segments: [],
                status: 'done',
              })
              currentTaskId = turnId
            } else if (msg.role === 'assistant') {
              if (!currentTaskId) {
                const turnId = `hist_turn_${historyTurns.length}`
                historyTurns.push({
                  id: turnId,
                  segments: [],
                  status: 'done',
                })
                currentTaskId = turnId
              }

              const turn = historyTurns.find(t => t.id === currentTaskId)
              if (!turn) return

              // 子 Agent 消息 — 路由到嵌套 subagent segment
              if (msg.metadata?.source === 'subagent') {
                const subTaskId = msg.metadata.parent_task_id || 'unknown'
                let subSeg = subagentCache.get(subTaskId)
                if (!subSeg) {
                  subSeg = {
                    id: nextSegId(),
                    type: 'subagent',
                    agentType: (msg.metadata?.agent_type || 'memory') as 'memory' | 'review',
                    taskId: subTaskId,
                    segments: [],
                    status: 'done',
                    finalText: '',
                  }
                  turn.segments.push(subSeg)
                  subagentCache.set(subTaskId, subSeg)
                }

                if (msg.content) {
                  subSeg.finalText = (subSeg.finalText ? subSeg.finalText + '\n' : '') + msg.content
                  const lastSeg = subSeg.segments[subSeg.segments.length - 1]
                  if (lastSeg && lastSeg.type === 'text') {
                    lastSeg.content = (lastSeg.content ? lastSeg.content + '\n' : '') + msg.content
                  } else {
                    subSeg.segments.push({
                      id: nextSegId(),
                      type: 'text',
                      content: msg.content,
                      thinkingContent: '',
                      thinkingDone: true,
                      isStreaming: false,
                    })
                  }
                }

                if (msg.metadata?.tool_calls) {
                  const toolCalls = Array.isArray(msg.metadata.tool_calls)
                    ? msg.metadata.tool_calls
                    : JSON.parse(msg.metadata.tool_calls || '[]')
                  toolCalls.forEach((tc: any) => {
                    subSeg.segments.push({
                      id: nextSegId(),
                      type: 'tool',
                      call: {
                        tool_name: tc.function?.name || tc.name || 'unknown',
                        display_text: tc.display_text || '处理中...',
                        activity_kind: tc.activity_kind,
                        status: 'completed',
                        task_id: msgTaskId,
                      },
                    })
                  })
                }
                return
              }

              // 主 Agent 消息 — 原有逻辑
              // 恢复历史时保持和流式阶段一致的视觉顺序：
              // 先展示 thinking / content，再展示本轮 assistant 触发的工具或 subagent 卡片。
              if (msg.content || msg.metadata?.thinking_content) {
                turn.segments.push({
                  id: nextSegId(),
                  type: 'text',
                  content: msg.content || '',
                  thinkingContent: msg.metadata?.thinking_content || '',
                  thinkingDone: true,
                  isStreaming: false,
                })
              }

              if (msg.metadata?.tool_calls) {
                const toolCalls = Array.isArray(msg.metadata.tool_calls)
                  ? msg.metadata.tool_calls
                  : JSON.parse(msg.metadata.tool_calls || '[]')
                toolCalls.forEach((tc: any) => {
                  turn.segments.push({
                    id: nextSegId(),
                    type: 'tool',
                    call: {
                      tool_name: tc.function?.name || tc.name || 'unknown',
                      display_text: tc.display_text || '处理中...',
                      activity_kind: tc.activity_kind,
                      status: 'completed',
                      task_id: msgTaskId,
                    },
                  })
                })
              }
            }
          })

          setTurns(historyTurns)
        } else {
          setTurns([])
        }
        break
      }
      case 'chat_started': {
        const m = msg as { type: 'chat_started'; task_id?: string }
        setIsStreaming(true)
        isUserScrolledUp.current = false
        const turnId = m.task_id || nextTurnId()
        currentTurnIdRef.current = turnId
        setTurns(prev => {
          const closed = prev.map(t =>
            t.status === 'streaming'
              ? { ...t, status: 'done' as const, segments: t.segments.map(s => s.type === 'text' ? { ...s, isStreaming: false } : s) }
              : t
          )
          return [...closed, { id: turnId, segments: [], status: 'streaming' as const }]
        })
        break
      }
      case 'thinking_chunk': {
        const m = msg as ThinkingChunkMsg
        if (!m.chunk?.trim()) break
        if (m.parent_task_id && m.task_id) {
          const agentType = (subTaskMeta.current.get(m.parent_task_id)?.agentType || (m as any).agent_type || 'memory')
          applyToSubagent(m.parent_task_id, m.task_id, agentType, seg => {
            const lastSeg = seg.segments[seg.segments.length - 1]
            if (lastSeg && lastSeg.type === 'text' && lastSeg.isStreaming) {
              const subSegs = [...seg.segments]
              subSegs[subSegs.length - 1] = { ...lastSeg, thinkingContent: lastSeg.thinkingContent + m.chunk }
              return { ...seg, segments: subSegs }
            }
            return {
              ...seg,
              segments: [...seg.segments, {
                id: nextSegId(), type: 'text', content: '',
                thinkingContent: m.chunk, thinkingDone: false, isStreaming: true,
              }],
            }
          })
          break
        }
        const turnId = currentTurnIdRef.current
        if (!turnId) break
        updateTurn(turnId, turn => {
          const segs = [...turn.segments]
          const lastSeg = segs[segs.length - 1]
          if (lastSeg && lastSeg.type === 'text' && lastSeg.isStreaming) {
            segs[segs.length - 1] = {
              ...lastSeg,
              thinkingContent: lastSeg.thinkingContent + m.chunk,
            }
          } else {
            segs.push({
              id: nextSegId(),
              type: 'text',
              content: '',
              thinkingContent: m.chunk,
              thinkingDone: false,
              isStreaming: true,
            })
          }
          return { ...turn, segments: segs }
        })
        break
      }
      case 'thinking_done': {
        const m = msg as ThinkingDoneMsg
        if (m.parent_task_id && m.task_id) {
          const agentType = (subTaskMeta.current.get(m.parent_task_id)?.agentType || (m as any).agent_type || 'memory')
          applyToSubagent(m.parent_task_id, m.task_id, agentType, seg => ({
            ...seg,
            segments: seg.segments.map(s =>
              s.type === 'text' && s.thinkingContent && !s.thinkingDone
                ? { ...s, thinkingDone: true, isStreaming: false }
                : s
            ),
          }))
          break
        }
        const turnId = currentTurnIdRef.current
        if (!turnId) break
        updateTurn(turnId, turn => ({
          ...turn,
          segments: turn.segments.map(seg =>
            seg.type === 'text' && seg.thinkingContent && !seg.thinkingDone
              ? { ...seg, thinkingDone: true, isStreaming: false }
              : seg
          ),
        }))
        break
      }
      case 'content_chunk': {
        const m = msg as ContentChunkMsg
        if (m.parent_task_id && m.task_id) {
          const agentType = (subTaskMeta.current.get(m.parent_task_id)?.agentType || (m as any).agent_type || 'memory')
          applyToSubagent(m.parent_task_id, m.task_id, agentType, seg => {
            const lastSeg = seg.segments[seg.segments.length - 1]
            if (lastSeg && lastSeg.type === 'text' && lastSeg.isStreaming) {
              const subSegs = [...seg.segments]
              subSegs[subSegs.length - 1] = { ...lastSeg, content: lastSeg.content + m.chunk }
              return { ...seg, segments: subSegs }
            }
            return {
              ...seg,
              segments: [...seg.segments, {
                id: nextSegId(), type: 'text', content: m.chunk,
                thinkingContent: '', thinkingDone: false, isStreaming: true,
              }],
            }
          })
          break
        }
        const turnId = currentTurnIdRef.current
        if (!turnId) break
        updateTurn(turnId, turn => {
          const segs = [...turn.segments]
          const lastSeg = segs[segs.length - 1]
          if (lastSeg && lastSeg.type === 'text' && lastSeg.isStreaming) {
            segs[segs.length - 1] = {
              ...lastSeg,
              content: lastSeg.content + m.chunk,
            }
          } else {
            segs.push({
              id: nextSegId(),
              type: 'text',
              content: m.chunk,
              thinkingContent: '',
              thinkingDone: false,
              isStreaming: true,
            })
          }
          return { ...turn, segments: segs }
        })
        break
      }
      case 'chat_completed': {
        setIsStreaming(false)
        const turnId = currentTurnIdRef.current
        if (turnId) {
          updateTurn(turnId, turn => ({
            ...turn,
            status: 'done',
            segments: turn.segments.map(seg =>
              seg.type === 'text' ? { ...seg, isStreaming: false } : seg
            ),
          }))
        }
        void refreshChapterList()
        if (selectedChapterId) {
          void refreshSelectedChapter(selectedChapterId)
        }
        if (pendingInterruptMessageRef.current) {
          const nextMessage = pendingInterruptMessageRef.current
          pendingInterruptMessageRef.current = null
          dispatchMessageRef.current?.(nextMessage)
        }
        break
      }
      case 'chat_failed': {
        const m = msg as { type: 'chat_failed'; task_id?: string; error: string }
        setIsStreaming(false)
        const turnId = currentTurnIdRef.current
        if (turnId) {
          updateTurn(turnId, turn => ({
            ...turn,
            status: 'failed',
            segments: turn.segments.map(seg =>
              seg.type === 'text' ? { ...seg, isStreaming: false } : seg
            ),
          }))
        }
        message.error(m.error || '服务异常，请稍后重试。')
        break
      }
      case 'tool_call': {
        const m = msg as ToolCallMsg

        // 子 Agent 内部的工具调用 — 路由到嵌套 segment
        if (m.parent_task_id && m.task_id) {
          const agentType = (subTaskMeta.current.get(m.parent_task_id)?.agentType || (m as any).agent_type || 'memory')
          const subToolCall: ToolCallInfo = {
            tool_name: m.tool_name,
            task_id: m.task_id,
            status: m.status,
            tool_id: m.tool_id,
            phase: m.phase,
            display_text: m.display_text || '处理中...',
            activity_kind: m.activity_kind,
            chapter_id: m.chapter_id,
            chapter_number: m.chapter_number,
            chapter_title: m.chapter_title,
            arguments: m.arguments,
            result_summary: m.result_summary,
            error: m.error,
            timestamp: m.timestamp,
          }
          const subUpdater = (seg: TurnSegmentSubagent): TurnSegmentSubagent => {
            const subSegs = [...seg.segments]
            const idx = subSegs.findIndex(s =>
              s.type === 'tool' && (
                (m.tool_id && s.call.tool_id === m.tool_id) ||
                (!m.tool_id && s.call.task_id === m.task_id && s.call.tool_name === m.tool_name && s.call.status === 'executing')
              )
            )
            if (idx >= 0) {
              subSegs[idx] = { ...subSegs[idx] as TurnSegmentTool, call: { ...(subSegs[idx] as TurnSegmentTool).call, ...subToolCall } }
            } else {
              subSegs.push({ id: nextSegId(), type: 'tool', call: subToolCall })
            }
            return { ...seg, segments: subSegs }
          }
          if (m.status === 'executing') {
            flushSync(() => { applyToSubagent(m.parent_task_id!, m.task_id!, agentType, subUpdater) })
          } else {
            applyToSubagent(m.parent_task_id, m.task_id, agentType, subUpdater)
          }
          break
        }

        const turnId = currentTurnIdRef.current
        if (turnId) {
          const toolUpdater = (turn: ConversationTurn): ConversationTurn => {
            const segs = [...turn.segments]
            const idx = segs.findIndex(seg =>
              seg.type === 'tool' && (
                (m.tool_id && seg.call.tool_id === m.tool_id) ||
                (!m.tool_id && seg.call.task_id === m.task_id && seg.call.tool_name === m.tool_name && seg.call.status === 'executing')
              )
            )
            const toolCall: ToolCallInfo = {
              tool_name: m.tool_name,
              task_id: m.task_id,
              status: m.status,
              tool_id: m.tool_id,
              phase: m.phase,
              display_text: m.display_text || '处理中...',
              activity_kind: m.activity_kind,
              chapter_id: m.chapter_id,
              chapter_number: m.chapter_number,
              chapter_title: m.chapter_title,
              arguments: m.arguments,
              result_summary: m.result_summary,
              error: m.error,
              timestamp: m.timestamp,
            }
            if (idx >= 0) {
              const existing = (segs[idx] as TurnSegmentTool).call
              segs[idx] = {
                ...segs[idx] as TurnSegmentTool,
                call: { ...existing, ...toolCall },
              }
            } else {
              segs.push({ id: nextSegId(), type: 'tool', call: toolCall })
            }
            return { ...turn, segments: segs }
          }
          if (m.status === 'executing') {
            flushSync(() => { updateTurn(turnId, toolUpdater) })
          } else {
            updateTurn(turnId, toolUpdater)
          }
        }

        // 记录 run_subagent 的 agent_type，供子 Agent 事件路由使用
        if (m.tool_name === 'run_subagent' && m.status === 'executing' && m.task_id) {
          const agentType = String(m.arguments?.agent_type || 'memory')
          subTaskMeta.current.set(m.task_id, { turnId: m.task_id, agentType })
        }
        // run_subagent 完成时，将嵌套 subagent 段标记为 done
        if (m.tool_name === 'run_subagent' && m.status === 'completed' && m.task_id) {
          updateTurn(m.task_id, turn => ({
            ...turn,
            segments: turn.segments.map(s =>
              s.type === 'subagent' ? { ...s, status: 'done' as const } : s
            ),
          }))
        }

        if (m.status === 'completed' && (m.tool_name === 'create_new_chapter' || m.tool_name === 'edit_chapter' || m.tool_name === 'get_chapter_list')) {
          void refreshChapterList()
        }
        if (m.status === 'completed' && m.chapter_id && (m.activity_kind === 'edit' || m.activity_kind === 'write')) {
          void revealChapterFromTool(m.chapter_id)
        } else if (m.status === 'completed' && m.chapter_number && (m.activity_kind === 'edit' || m.activity_kind === 'write')) {
          void revealChapterByNumber(m.chapter_number)
        }
        if (m.tool_name === 'edit_chapter' && (m.status === 'failed' || m.status === 'rejected')) {
          setEditNotice('这次编辑没有完成，正文保持原样。')
          void refreshChapterList()
        }
        if (m.tool_name === 'edit_chapter' && (m.status === 'failed' || m.status === 'rejected')) {
          setEditNotice('这次 AI 修改没有完成，正文没有被自动改动。')
          if (selectedChapterId) {
            void refreshSelectedChapter(selectedChapterId)
          }
        }
        break
      }

      case 'outline_generated': {
        const om = msg as OutlineGeneratedMsg
        if (currentTurnIdRef.current) {
          updateTurn(currentTurnIdRef.current, turn => ({
            ...turn,
            segments: [...turn.segments, {
              id: nextSegId(),
              type: 'outline' as const,
              content: om.content,
              outlines: om.outlines,
              approvalState: 'pending' as const,
            }],
          }))
        }
        break
      }
      case 'edit_stream': {
        const m = msg as { type: 'edit_stream'; chapter_id?: number; edit_session_id?: string; working_content: string }
        if (m.chapter_id && selectedChapterId !== m.chapter_id) {
          void revealChapterFromTool(m.chapter_id)
        }
        if (m.edit_session_id) {
          setEditSessionId(prev => prev || m.edit_session_id || null)
          setLatestPendingEditSessionId(prev => prev || m.edit_session_id || null)
        }
        setHasActiveEdit(true)
        setShowDiff(false)
        setWorkingContent(m.working_content)
        break
      }
      case 'edit_started': {
        const m = msg as EditStartedMsg
        if (m.chapter_id && selectedChapterId !== m.chapter_id) {
          void revealChapterFromTool(m.chapter_id)
        }
        setEditSessionId(m.edit_session_id)
        setLatestPendingEditSessionId(m.latest_pending_edit_session_id || m.edit_session_id)
        setOriginalContent(m.original_content)
        setWorkingContent(m.working_content)
        setChangeCount(m.change_count)
        setHasActiveEdit(true)
        setShowDiff(false)
        break
      }
      case 'edit_preview': {
        const m = msg as EditPreviewMsg
        if (m.chapter_id && selectedChapterId !== m.chapter_id) {
          void revealChapterFromTool(m.chapter_id)
        }
        setOriginalContent(m.diff?.old_content || originalContentRef.current)
        setWorkingContent(m.working_content)
        setChangeCount(m.change_count)
        if (m.diff) {
          setDiffData(m.diff as unknown as DiffData)
        }
        break
      }
      case 'edit_pending': {
        const m = msg as EditPendingMsg
        if (m.chapter_id && selectedChapterId !== m.chapter_id) {
          void revealChapterFromTool(m.chapter_id)
        }
        setEditSessionId(m.edit_session_id)
        setLatestPendingEditSessionId(m.latest_pending_edit_session_id || m.edit_session_id)
        setChangeCount(m.change_count)
        if (m.chapter_id && selectedChapterId === m.chapter_id) {
          setHasActiveEdit(true)
        }
        setEditNotice('当前章节存在待确认修改，稍后回来也可以再确认。')
        break
      }
      case 'edit_applied': {
        const m = msg as EditAppliedMsg
        if (m.chapter_id && selectedChapterId !== m.chapter_id) {
          void revealChapterFromTool(m.chapter_id)
        }
        setEditSessionId(m.edit_session_id)
        setLatestPendingEditSessionId(m.latest_pending_edit_session_id || m.edit_session_id)
        setWorkingContent(m.working_content)
        setChangeCount(m.change_count)
        setDiffData(m.diff as DiffData)
        setHasActiveEdit(true)
        setShowDiff(true)
        setEditNotice('副本已更新，可以直接确认或拒绝。')
        break
      }
      case 'edit_accepted': {
        const m = msg as EditAcceptedMsg
        message.success(m.message)
        setIsApplyingEdit(false)
        setHasActiveEdit(false)
        setEditSessionId(null)
        setLatestPendingEditSessionId(m.latest_pending_edit_session_id || null)
        setShowDiff(false)
        setDiffData(null)
        setChangeCount(0)
        setChapterWordCount(m.word_count)
        setEditNotice(m.already_processed ? '这份修改之前已经接受过了。' : '修改已合并到正文。')
        if (selectedChapterId) {
          setChapters(prev => prev.map(c => c.id === selectedChapterId ? { ...c, word_count: m.word_count } : c))
          void refreshSelectedChapter(selectedChapterId)
        }
        break
      }
      case 'edit_rejected': {
        const m = msg as EditRejectedMsg
        message.info(m.message)
        setIsApplyingEdit(false)
        setHasActiveEdit(false)
        setEditSessionId(null)
        setLatestPendingEditSessionId(m.latest_pending_edit_session_id || null)
        setShowDiff(false)
        setDiffData(null)
        setChangeCount(0)
        setEditNotice(m.already_processed ? '这份修改之前已经拒绝过了。' : '副本修改已撤销，正文保持原样。')
        if (selectedChapterId) {
          void refreshSelectedChapter(selectedChapterId)
        }
        break
      }
      case 'error': {
        const m = msg as ErrorMsg
        setIsApplyingEdit(false)
        if (typeof m.latest_pending_edit_session_id !== 'undefined') {
          setLatestPendingEditSessionId(m.latest_pending_edit_session_id)
        }
        if (m.edit_session_id) {
          setEditSessionId(m.edit_session_id)
        }
        message.error(m.error)
        setIsStreaming(false)
        if (pendingInterruptMessageRef.current) {
          const nextMessage = pendingInterruptMessageRef.current
          pendingInterruptMessageRef.current = null
          dispatchMessageRef.current?.(nextMessage)
        }
        break
      }
      case 'task_cancelled': {
        setIsStreaming(false)
        setIsApplyingEdit(false)
        const turnId = currentTurnIdRef.current
        if (turnId) {
          updateTurn(turnId, turn => ({
            ...turn,
            status: turn.status === 'streaming' ? 'done' : turn.status,
            segments: turn.segments.map(seg =>
              seg.type === 'text' ? { ...seg, isStreaming: false } : seg
            ),
          }))
        }
        if (pendingInterruptMessageRef.current) {
          const nextMessage = pendingInterruptMessageRef.current
          pendingInterruptMessageRef.current = null
          dispatchMessageRef.current?.(nextMessage)
        }
        break
      }
    }
  }, [nextSegId, nextTurnId, refreshChapterList, refreshSelectedChapter, selectedChapterId, updateTurn])

  useEffect(() => {
    const unsub = wsEditorService.onMessage(handleMsg)
    return unsub
  }, [handleMsg])

  const handleEditorMount: OnMount = (editor) => {
    editorRef.current = editor
  }

  const selectChapter = async (chapterId: number) => {
    setSelectedChapterId(chapterId)
    setShowDiff(false)
    setDiffData(null)
    setEditNotice('')
    setEditorViewMode('content')
    setOutlineContent(null)

    try {
      const res = await editorApi.getChapterForEditor(chapterId)
      if (res.success) {
        const data = res.data
        setChapterWordCount(data.word_count)
        if (data.has_active_edit && data.working_content) {
          setOriginalContent(data.content)
          setWorkingContent(data.working_content)
          setEditSessionId(data.edit_session_id)
          setLatestPendingEditSessionId(data.latest_pending_edit_session_id)
          setHasActiveEdit(true)
          setChangeCount(data.change_count)
          setEditNotice('这章有待确认副本，你可以现在确认，也可以晚点回来处理。')
        } else {
          setOriginalContent(data.content)
          setWorkingContent(data.content)
          setEditSessionId(null)
          setLatestPendingEditSessionId(data.latest_pending_edit_session_id)
          setHasActiveEdit(false)
          setChangeCount(0)
        }
      }
    } catch {
      const res = await chapterApi.getChapter(chapterId)
      if (res.success) {
        setOriginalContent(res.data.content || '')
        setWorkingContent(res.data.content || '')
        setChapterWordCount(res.data.word_count || 0)
        setEditSessionId(null)
        setLatestPendingEditSessionId(null)
        setHasActiveEdit(false)
      }
    }

    chapterApi.getChapter(chapterId).then(res => {
      if (res.success) {
        setOutlineContent(res.data.outline_text || null)
      }
    }).catch(() => {})
  }

  async function revealChapterFromTool(chapterId?: number | null) {
    if (!chapterId) return
    await refreshChapterList()
    if (selectedChapterId !== chapterId) {
      await selectChapter(chapterId)
      return
    }
    await refreshSelectedChapter(chapterId)
  }

  async function revealChapterByNumber(chapterNumber?: number | null) {
    if (!chapterNumber || !novelId) return
    const res = await chapterApi.getChapters(parseInt(novelId), { page_size: 100 })
    if (!res.success) return
    const items = res.data.items || []
    setChapters(items)
    const matched = items.find(ch => ch.chapter_number === chapterNumber)
    if (matched) {
      await selectChapter(matched.id)
    }
  }

  function dispatchMessage(msg: string) {
    isUserScrolledUp.current = false
    const turnId = nextTurnId()
    currentTurnIdRef.current = turnId
    setTurns(prev => [...prev, {
      id: turnId,
      userMessage: msg,
      segments: [],
      status: 'streaming',
    }])

    if (!currentSessionId) {
      pendingMessageRef.current = msg
      const sent = wsEditorService.createSession(selectedModel, undefined, reasoningEffort)
      if (!sent) {
        message.error('WebSocket 未连接')
        setTurns(prev => prev.filter(t => t.id !== turnId))
        pendingMessageRef.current = null
      }
      return
    }

    const sent = wsEditorService.chat(currentSessionId, msg, true)
    if (!sent) {
      message.error('WebSocket 未连接')
      setTurns(prev => prev.filter(t => t.id !== turnId))
    }
  }
  dispatchMessageRef.current = dispatchMessage

  const handleEditorChange = (value: string | undefined) => {
    if (!value) return
    setWorkingContent(value)

    if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
    saveTimerRef.current = setTimeout(() => {
      debouncedSave(value)
    }, 2000)
  }

  const debouncedSave = async (content: string) => {
    if (!selectedChapterId) return
    setIsSaving(true)
    try {
      const res = await chapterApi.updateChapter(selectedChapterId, { content })
      if (res.success) {
        setOriginalContent(content)
        setChapterWordCount(res.data.word_count || 0)
        setChapters(prev => prev.map(c =>
          c.id === selectedChapterId ? { ...c, word_count: res.data.word_count || 0 } : c
        ))
      }
    } catch {
      message.error('保存失败')
    } finally {
      setIsSaving(false)
    }
  }

  const acceptEdit = () => {
    if (editSessionId || latestPendingEditSessionId || selectedChapterId) {
      setIsApplyingEdit(true)
      wsEditorService.acceptEdit(latestPendingEditSessionId || editSessionId, selectedChapterId)
    }
  }

  const rejectEdit = () => {
    if (editSessionId || latestPendingEditSessionId || selectedChapterId) {
      wsEditorService.rejectEdit(latestPendingEditSessionId || editSessionId, selectedChapterId)
    }
  }

  const handleApproveOutline = (turnId: string, segId: string) => {
    wsEditorService.sendOutlineApproval(true)
    updateTurn(turnId, turn => ({
      ...turn,
      segments: turn.segments.map(seg =>
        seg.type === 'outline' && seg.id === segId
          ? { ...seg, approvalState: 'approved' as const }
          : seg
      ),
    }))
  }

  const handleRejectOutline = (turnId: string, segId: string, feedback: string) => {
    if (!feedback.trim()) return
    wsEditorService.sendOutlineApproval(false, feedback.trim())
    updateTurn(turnId, turn => ({
      ...turn,
      segments: turn.segments.map(seg =>
        seg.type === 'outline' && seg.id === segId
          ? { ...seg, approvalState: 'rejected' as const }
          : seg
      ),
    }))
  }

  const handleStop = () => {
    if (currentTurnId) {
      wsEditorService.cancelTask(currentTurnId)
    }
  }

  const sendMessage = () => {
    if (!inputValue.trim()) return

    const msg = inputValue.trim()
    setInputValue('')

    if (isStreaming) {
      pendingInterruptMessageRef.current = msg
      handleStop()
      return
    }

    dispatchMessage(msg)
  }

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      sendMessage()
    }
  }

  const openCreateChapter = async () => {
    if (!novelId) return
    const nid = parseInt(novelId)
    try {
      const res = await chapterApi.getNextChapterNumber(nid)
      if (res.success) {
        setNewChapterNumber(res.data.next_chapter_number)
        setNewChapterTitle(`第${res.data.next_chapter_number}章`)
      }
    } catch {
      const maxNum = chapters.reduce((max, ch) => Math.max(max, ch.chapter_number), 0)
      const nextNum = maxNum + 1
      setNewChapterNumber(nextNum)
      setNewChapterTitle(`第${nextNum}章`)
    }
    setCreateChapterModal(true)
  }

  const handleCreateChapter = async () => {
    if (!novelId || !newChapterNumber) return
    setCreatingChapter(true)
    try {
      const res = await chapterApi.createChapter(parseInt(novelId), {
        chapter_number: newChapterNumber,
        title: newChapterTitle || `第${newChapterNumber}章`,
      })
      if (res.success) {
        message.success('章节创建成功')
        setCreateChapterModal(false)
        void refreshChapterList()
        selectChapter(res.data.id)
      }
    } catch {
      message.error('创建失败')
    } finally {
      setCreatingChapter(false)
    }
  }

  const selectedChapter = chapters.find(c => c.id === selectedChapterId)
  const activeChapterMeta = selectedChapter
    ? { chapterNumber: selectedChapter.chapter_number, title: selectedChapter.title }
    : null
  const theme = darkMode ? 'vs-dark' : 'light'
  const activePendingSessionId = latestPendingEditSessionId || editSessionId
  const hasPendingChanges = changeCount > 0 || workingContent !== originalContent
  const hasPendingReview = Boolean(activePendingSessionId || hasActiveEdit) && hasPendingChanges

  const hasAnyTurnContent = turns.length > 0 || isStreaming

  return (
    <div className={`${styles.editorPage} ${darkMode ? styles.editorPageDark : ''}`}>
      <div className={styles.editorToolbar}>
        <Tooltip title="返回小说详情">
          <button className={styles.toolbarBtn} onClick={() => navigate(`/novels/${novelId}`)}>
            <ArrowLeftOutlined />
          </button>
        </Tooltip>
        <div className={styles.toolbarDivider} />
        <span className={styles.toolbarTitle}>AI 创作工作台</span>
        <div className={styles.toolbarRight}>
          <Tooltip title={darkMode ? '切换亮色主题' : '切换暗色主题'}>
            <button className={styles.toolbarBtn} onClick={() => setDarkMode(!darkMode)}>
              <BgColorsOutlined />
            </button>
          </Tooltip>
          <span className={`${styles.wsStatus} ${connected ? styles.wsConnected : styles.wsDisconnected}`} />
          <span className={styles.wsLabel}>
            {connected ? '已连接' : '未连接'}
          </span>
        </div>
      </div>

      <div className={styles.editorBody}>
        <div className={styles.sidebarWrapper}>
          <div className={`${styles.leftSidebar} ${sidebarCollapsed ? styles.leftSidebarCollapsed : ''}`}>
            <div className={styles.sidebarTabs}>
              <button
                className={`${styles.sidebarTab} ${leftTab === 'chapters' ? styles.sidebarTabActive : ''}`}
                onClick={() => setLeftTab('chapters')}
              >
                <FileTextOutlined style={{ marginRight: 4 }} /> 章节
              </button>
              <button
                className={`${styles.sidebarTab} ${leftTab === 'sessions' ? styles.sidebarTabActive : ''}`}
                onClick={() => setLeftTab('sessions')}
              >
                <MessageOutlined style={{ marginRight: 4 }} /> 对话
              </button>
            </div>

            <div className={styles.sidebarContent}>
              {leftTab === 'chapters' ? (
                <>
                  <button className={styles.newSessionBtn} onClick={openCreateChapter}>
                    <PlusOutlined /> 新建章节
                  </button>
                  {chapters.map(ch => (
                    <div
                      key={ch.id}
                      className={`${styles.chapterItem} ${selectedChapterId === ch.id ? styles.chapterItemActive : ''}`}
                      onClick={() => selectChapter(ch.id)}
                    >
                      <span className={styles.chapterNumber}>第{ch.chapter_number}章</span>
                      <span className={styles.chapterTitle}>{ch.title}</span>
                      <span className={`${styles.chapterStatus} ${ch.status === 'completed' ? styles.chapterStatusCompleted : ''}`}>
                        {ch.word_count > 0 ? `${ch.word_count}字` : ch.status}
                      </span>
                    </div>
                  ))}
                  {chapters.length === 0 && (
                    <div style={{ padding: 16, textAlign: 'center', color: '#bfbfbf', fontSize: 12 }}>
                      暂无章节
                    </div>
                  )}
                </>
              ) : (
                <>
                  {sessions.map(s => (
                    <div
                      key={s.session_id}
                      className={`${styles.sessionItem} ${currentSessionId === s.session_id ? styles.sessionItemActive : ''}`}
                      onClick={() => {
                        navigate(`/novels/${novelId}/editor/${s.session_id}`)
                        setCurrentSessionId(s.session_id)
                        wsEditorService.loadSession(s.session_id)
                        setTurns([])
                        currentTurnIdRef.current = null
                      }}
                    >
                      <span>{s.display_name}</span>
                      <span className={styles.sessionScope}>
                        {s.message_count}条
                      </span>
                    </div>
                  ))}
                  {sessions.length === 0 && (
                    <div style={{ padding: 16, textAlign: 'center', color: '#bfbfbf', fontSize: 12 }}>
                      暂无对话
                    </div>
                  )}
                </>
              )}
            </div>
            <button
              className={`${styles.collapseBtn} ${sidebarCollapsed ? styles.collapseBtnCollapsed : ''}`}
              onClick={() => setSidebarCollapsed(!sidebarCollapsed)}
            >
              {sidebarCollapsed ? <RightOutlined /> : <LeftOutlined />}
            </button>
          </div>
        </div>

        <div className={styles.centerEditor}>
          {activeChapterMeta ? (
            <>
              <div className={styles.editorHeader}>
                <div className={styles.editorMeta}>
                  <span className={styles.editorFileName}>
                    {`第${activeChapterMeta.chapterNumber}章 ${activeChapterMeta.title}`}
                  </span>
                  <div className={styles.editorFacts}>
                    <Tooltip title={displayStats ? `中文字数: ${displayStats.chinese_chars} | 英文词数: ${displayStats.english_words} | 空格: ${displayStats.spaces} | 标点: ${displayStats.punctuation}` : ''}>
                      <span className={styles.editorWordCount}>
                        {`${chapterWordCount.toLocaleString()} 字`}
                      </span>
                    </Tooltip>
                    <span className={`${styles.chapterPill} ${hasPendingReview ? styles.chapterPillPending : ''}`}>
                      {hasActiveEdit ? 'AI 正在写作' : hasPendingReview ? '有待确认修改' : '正文已同步'}
                    </span>
                    <span className={styles.chapterPill}>
                      协作创作
                    </span>
                  </div>
                </div>
                <Segmented
                  size="small"
                  value={editorViewMode}
                  onChange={v => setEditorViewMode(v as 'content' | 'outline')}
                  options={[
                    { value: 'content', label: '正文' },
                    { value: 'outline', label: '大纲' },
                  ]}
                  className={styles.editorViewSegmented}
                />
                {isSaving && (
                  <span style={{ fontSize: 11, color: '#faad14' }}>
                    <SaveOutlined style={{ marginRight: 2 }} /> 保存中...
                  </span>
                )}
                {selectedChapter && hasPendingReview && (
                  <div className={styles.diffActions}>
                    <span className={styles.diffSummary}>
                      {changeCount} 处改动
                      {diffData && diffData.summary && (
                        <>
                          {' '}
                          <span className={styles.diffAdditions}>+{diffData.summary.additions}</span>
                          {' / '}
                          <span className={styles.diffDeletions}>-{diffData.summary.deletions}</span>
                        </>
                      )}
                    </span>
                    <button
                      className={styles.toolbarBtn}
                      onClick={() => setShowDiff(!showDiff)}
                      style={{ fontSize: 11 }}
                    >
                      {showDiff ? <EditOutlined /> : 'Diff'}
                    </button>
                    <button className={styles.btnAccept} onClick={acceptEdit} disabled={isApplyingEdit}>
                      {isApplyingEdit ? <LoadingOutlined spin /> : <CheckOutlined />} 接受
                    </button>
                    <button className={styles.btnReject} onClick={rejectEdit} disabled={isApplyingEdit}>
                      <CloseOutlined /> 拒绝
                    </button>
                  </div>
                )}
              </div>
              {(editNotice || hasPendingReview) && (
                <div className={styles.editNoticeBar}>
                  <div>
                    <div className={styles.editNoticeTitle}>副本编辑机制</div>
                    <div className={styles.editNoticeText}>
                      {editNotice || 'AI 的修改先进入副本，确认后再写回正文。'}
                    </div>
                  </div>
                  {activePendingSessionId && (
                    <span className={styles.editSessionBadge}>
                      {activePendingSessionId}
                    </span>
                  )}
                </div>
              )}
              <div className={styles.editorContainer}>
                {editorViewMode === 'outline' ? (
                  outlineContent ? (
                    <div className={styles.outlinePreview}>
                      <Markdown>{outlineContent}</Markdown>
                    </div>
                  ) : (
                    <div className={styles.emptyChat}>
                      <FileTextOutlined className={styles.emptyChatIcon} />
                      <div>该章节暂无大纲</div>
                      <div style={{ fontSize: 12, color: '#999' }}>大纲会在章节创作工作流中自动生成</div>
                    </div>
                  )
                ) : selectedChapter && showDiff && hasPendingReview ? (
                  diffData ? (
                    <InlineDiffPreview diff={diffData} />
                  ) : (
                    <Editor
                      height="100%"
                      language="markdown"
                      theme={theme}
                      value={workingContent}
                      options={{
                        readOnly: true,
                        minimap: { enabled: false },
                        lineNumbers: 'on',
                        scrollBeyondLastLine: false,
                        fontSize: 17,
                        lineHeight: 30,
                        fontFamily: "'LXGW WenKai Screen', 'Noto Serif SC', 'Source Han Serif SC', serif",
                        wordWrap: 'on',
                        automaticLayout: true,
                      }}
                    />
                  )
                ) : (
                  <Editor
                    key="content-editor"
                    height="100%"
                    language="markdown"
                    theme={theme}
                    value={workingContent}
                    onChange={handleEditorChange}
                    onMount={handleEditorMount}
                    options={{
                      readOnly: hasActiveEdit,
                      minimap: { enabled: false },
                      lineNumbers: 'on',
                      scrollBeyondLastLine: false,
                      fontSize: 17,
                      lineHeight: 30,
                      fontFamily: "'LXGW WenKai Screen', 'Noto Serif SC', 'Source Han Serif SC', serif",
                      wordWrap: 'on',
                      automaticLayout: true,
                    }}
                  />
                )}
              </div>
            </>
          ) : (
            <div className={styles.emptyEditor}>
              <FileTextOutlined style={{ fontSize: 48, opacity: 0.2 }} />
              <div>从左侧选择一个章节开始编辑</div>
            </div>
          )}
        </div>

        <div className={styles.rightChat}>
          <div className={styles.chatMessages} ref={chatContainerRef}>
            {!hasAnyTurnContent && (
              <div className={styles.emptyChat}>
                <MessageOutlined className={styles.emptyChatIcon} />
                <div>输入消息开始对话</div>
                <div style={{ fontSize: 12 }}>AI 可以帮你修改章节内容</div>
              </div>
            )}
            {turns.map((turn) => {
              const isCurrentlyStreaming = turn.status === 'streaming'

              return (
                <div key={turn.id} className={styles.chatTurn}>
                  {turn.userMessage && (
                    <div className={styles.chatMsgUser}>
                      <Markdown>{turn.userMessage}</Markdown>
                    </div>
                  )}

                  {turn.segments.map((seg) => {
                    if (seg.type === 'text') {
                      // 紧跟在 run_subagent 后的纯 thinking（无正文）由子 Agent 卡片渲染，此处跳过
                      const prevIdx = turn.segments.indexOf(seg) - 1
                      if (!seg.content && prevIdx >= 0) {
                        const prev = turn.segments[prevIdx]
                        if (prev.type === 'tool' && (prev as TurnSegmentTool).call.tool_name === 'run_subagent') return null
                      }
                      if (!seg.content && !seg.thinkingContent) return null
                      return (
                        <div key={seg.id} className={styles.chatTurnAssistant}>
                          {seg.thinkingContent && (
                            <details className={`${styles.thinkingBlock} ${!seg.thinkingDone && seg.isStreaming ? styles.thinkingThinking : ''}`} open={seg.isStreaming ? true : !seg.thinkingDone}>
                              <summary className={styles.thinkingSummary}>
                                <ReadOutlined /> 思考过程
                                {seg.thinkingDone || seg.content || !seg.isStreaming
                                  ? <span className={styles.thinkingToggle}>展开</span>
                                  : <span className={`${styles.thinkingToggle} ${styles.thinkingActive}`}>思考中…</span>
                                }
                              </summary>
                              <div className={styles.thinkingContent}>
                                <pre>{seg.thinkingContent}</pre>
                              </div>
                            </details>
                          )}
                          {seg.content && (
                            <div className={styles.assistantTextBlock}>
                              <Markdown>{seg.content}</Markdown>
                            </div>
                          )}
                        </div>
                      )
                    }

                    if (seg.type === 'outline') {
                      return (
                        <div key={seg.id} className={styles.outlineInline}>
                          <div className={styles.outlineBody}>
                            <Markdown>{seg.content}</Markdown>
                          </div>
                          {seg.approvalState === 'pending' ? (
                            <div className={styles.outlineActions}>
                              <textarea
                                className={styles.outlineFeedback}
                                value={seg.feedbackDraft || ''}
                                onChange={e => {
                                  const draft = e.target.value
                                  updateTurn(turn.id, t => ({
                                    ...t,
                                    segments: t.segments.map(s =>
                                      s.type === 'outline' && s.id === seg.id
                                        ? { ...s, feedbackDraft: draft }
                                        : s
                                    ),
                                  }))
                                }}
                                placeholder="修改意见（拒绝时必填）…"
                                rows={2}
                              />
                              <div className={styles.outlineButtons}>
                                <button
                                  className={styles.outlineRejectBtn}
                                  onClick={() => handleRejectOutline(turn.id, seg.id, seg.feedbackDraft || '')}
                                  disabled={!(seg.feedbackDraft || '').trim()}
                                >
                                  拒绝
                                </button>
                                <button
                                  className={styles.outlineApproveBtn}
                                  onClick={() => handleApproveOutline(turn.id, seg.id)}
                                >
                                  批准
                                </button>
                              </div>
                            </div>
                          ) : (
                            <div className={styles.outlineStatusBadge}>
                              {seg.approvalState === 'approved' ? '✅ 已批准' : '❌ 已拒绝'}
                            </div>
                          )}
                        </div>
                      )
                    }

                    if (seg.type === 'subagent') {
                      // 子 Agent 卡片已在 run_subagent 工具段位置渲染，此处跳过
                      return null
                    }

                    const renderSubagentCard = (subSeg: TurnSegmentSubagent) => {
                      const subIsReview = subSeg.agentType === 'review'
                      const subIsStreaming = subSeg.status === 'streaming'
                      const subIsFinished = subSeg.status === 'done'
                      const subAccentCls = subIsReview ? styles.subagentReview : styles.subagentMemory
                      const subAgentLabel = subIsReview ? '审核编辑' : '记忆分析师'
                      const subAgentEmoji = subIsReview ? '🔍' : '📝'
                      const subCollapsed = collapsedSubagents.has(subSeg.taskId)

                      const subActiveKey = subIsStreaming ? [subSeg.taskId]
                        : subIsFinished && subCollapsed ? []
                        : subIsFinished && !subCollapsed ? [subSeg.taskId]
                        : subSeg.status === 'failed' ? [subSeg.taskId]
                        : [subSeg.taskId]

                      const subHeader = (
                        <div className={`${styles.subagentPanelHeader} ${subAccentCls}`}>
                          <span className={styles.subagentPanelIcon}>{subAgentEmoji}</span>
                          <span className={styles.subagentPanelLabel}>{subAgentLabel}</span>
                          {subIsStreaming && (
                            <span className={styles.subagentPanelBadge}><LoadingOutlined spin /> 执行中</span>
                          )}
                          {subIsFinished && (
                            <span className={styles.subagentPanelBadgeDone}><CheckCircleOutlined /> 完成</span>
                          )}
                          {subSeg.status === 'failed' && (
                            <span className={styles.subagentPanelBadgeFailed}><CloseCircleOutlined /> 失败</span>
                          )}
                        </div>
                      )

                      return (
                        <div key={subSeg.id} className={`${styles.subagentCard} ${subAccentCls} ${subIsStreaming ? styles.subagentStreaming : ''}`}>
                          <Collapse
                            activeKey={subActiveKey}
                            onChange={keys => {
                              if (!subIsStreaming) {
                                setCollapsedSubagents(prev => {
                                  const next = new Set(prev)
                                  if (keys.includes(subSeg.taskId)) next.delete(subSeg.taskId)
                                  else next.add(subSeg.taskId)
                                  return next
                                })
                              }
                            }}
                            items={[{
                              key: subSeg.taskId,
                              label: subHeader,
                              children: subSeg.segments.length > 0 ? (
                                <div className={styles.subagentBody}>
                                  {subSeg.segments.map(subSubSeg => {
                                    if (subSubSeg.type === 'text') {
                                      return (
                                        <div key={subSubSeg.id} className={styles.subagentText}>
                                          {subSubSeg.thinkingContent && (
                                            <details className={`${styles.thinkingBlock} ${styles.subagentThinking}`} open={!subSubSeg.thinkingDone}>
                                              <summary className={styles.thinkingSummary}>
                                                <ReadOutlined /> 思考过程
                                              </summary>
                                              <div className={styles.thinkingContent}>
                                                <pre>{subSubSeg.thinkingContent}</pre>
                                              </div>
                                            </details>
                                          )}
                                          {subSubSeg.content && (
                                            <div className={styles.subagentContent}>
                                              <Markdown>{subSubSeg.content}</Markdown>
                                            </div>
                                          )}
                                        </div>
                                      )
                                    }
                                    if (subSubSeg.type === 'tool') {
                                      const subTc = subSubSeg.call
                                      const subVisual = getActivityVisual(subTc.activity_kind)
                                      return (
                                        <div
                                          key={subSubSeg.id}
                                          className={`${styles.toolInlineCompact} ${styles.subagentTool} ${subTc.status === 'executing' ? styles.toolInlineActive : ''}`}
                                        >
                                          <span className={styles.toolCompactIcon}>{subVisual.icon}</span>
                                          <span className={styles.toolCompactName}>
                                            {subTc.display_text || '处理中...'}
                                          </span>
                                          <span className={`${styles.toolCompactStatus} ${subTc.status === 'executing' ? styles.toolCompactRunning : styles.toolCompactDone}`}>
                                            {subTc.status === 'executing' && <LoadingOutlined spin />}
                                            {subTc.status === 'completed' && <CheckCircleOutlined />}
                                            {subTc.status === 'executing' ? subVisual.badge : '完成'}
                                          </span>
                                        </div>
                                      )
                                    }
                                    return null
                                  })}
                                </div>
                              ) : (
                                <div className={styles.subagentEmpty}>暂无内容</div>
                              ),
                            }]}
                            bordered={false}
                            expandIcon={() => null}
                            className={styles.subagentCollapse}
                          />
                        </div>
                      )
                    }

                    const tc = seg.call
                    // run_subagent 位置渲染子 Agent 卡片（同 turn 内匹配）
                    if (tc.tool_name === 'run_subagent') {
                      const subSeg = turn.segments.find(s => s.type === 'subagent') as TurnSegmentSubagent | undefined
                      if (!subSeg) return null
                      return renderSubagentCard(subSeg)
                    }
                    const visual = getActivityVisual(tc.activity_kind)
                    return (
                      <div
                        key={seg.id}
                        className={`${styles.toolInlineCompact} ${tc.status === 'executing' ? styles.toolInlineActive : ''}`}
                      >
                        <span className={styles.toolCompactIcon}>{visual.icon}</span>
                        <span className={styles.toolCompactName}>
                          {tc.display_text || '处理中...'}
                        </span>
                        <span className={`${styles.toolCompactStatus} ${tc.status === 'executing' ? styles.toolCompactRunning : tc.status === 'completed' ? styles.toolCompactDone : styles.toolCompactFailed}`}>
                          {tc.status === 'executing' && <LoadingOutlined spin />}
                          {tc.status === 'completed' && <CheckCircleOutlined />}
                          {(tc.status === 'failed' || tc.status === 'rejected') && <CloseCircleOutlined />}
                          {tc.status === 'executing'
                            ? visual.badge
                            : tc.status === 'completed'
                              ? '完成'
                              : '失败'}
                        </span>
                        {tc.error && <span className={styles.toolCompactError}>{sanitizeToolError(tc.error)}</span>}
                      </div>
                    )
                  })}

                  {isCurrentlyStreaming && turn.segments.length === 0 && (
                    <div className={styles.assistantThinking}>
                      <LoadingOutlined spin /> 思考中…
                    </div>
                  )}
                </div>
              )
            })}
          </div>

          <div className={styles.chatInput}>
            <div className={styles.inputRow}>
              <textarea
                className={styles.chatTextArea}
                value={inputValue}
                onChange={e => setInputValue(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder={isStreaming ? "继续输入并发送，会自动打断上一轮并接着聊" : "描述你要写什么、改什么，或者直接和 AI 讨论剧情与设定"}
                rows={2}
                onInput={e => {
                  const target = e.target as HTMLTextAreaElement
                  target.style.height = 'auto'
                  target.style.height = Math.min(target.scrollHeight, 180) + 'px'
                }}
              />
              <button
                className={`${styles.sendBtn} ${isStreaming ? styles.stopBtn : ''}`}
                onClick={inputValue.trim() ? sendMessage : isStreaming ? handleStop : sendMessage}
                disabled={!isStreaming && !inputValue.trim()}
              >
                {isStreaming && !inputValue.trim() ? (
                  <span style={{ fontSize: 12 }}>■</span>
                ) : (
                  <ArrowLeftOutlined style={{ transform: 'rotate(-45deg)' }} />
                )}
              </button>
            </div>
            <div className={styles.chatControls}>
              <Select
                size="small"
                value={selectedModel}
                onChange={setSelectedModel}
                className={styles.modelSelect}
                popupMatchSelectWidth={false}
                options={modelOptions}
                variant="borderless"
              />
              {(selectedModel.startsWith('deepseek-v4')) && (
                <Select
                  size="small"
                  value={reasoningEffort}
                  onChange={setReasoningEffort}
                  className={styles.reasoningSelect}
                  popupMatchSelectWidth={false}
                  options={REASONING_OPTIONS}
                  variant="borderless"
                />
              )}
            </div>
          </div>
        </div>
      </div>

      <Modal
        title="新建章节"
        open={createChapterModal}
        onOk={handleCreateChapter}
        onCancel={() => setCreateChapterModal(false)}
        confirmLoading={creatingChapter}
        okText="创建"
        cancelText="取消"
      >
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div>
            <div style={{ marginBottom: 4, fontSize: 13, color: '#666' }}>章节号</div>
            <Input
              type="number"
              value={newChapterNumber ?? undefined}
              onChange={e => setNewChapterNumber(parseInt(e.target.value) || null)}
              placeholder="章节号"
              style={{ width: '100%' }}
            />
          </div>
          <div>
            <div style={{ marginBottom: 4, fontSize: 13, color: '#666' }}>章节标题</div>
            <Input
              value={newChapterTitle}
              onChange={e => setNewChapterTitle(e.target.value)}
              placeholder="章节标题"
              style={{ width: '100%' }}
            />
          </div>
        </div>
      </Modal>
    </div>
  )
}
