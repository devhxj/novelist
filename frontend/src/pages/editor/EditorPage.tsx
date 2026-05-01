import { useState, useEffect, useRef, useCallback, useMemo } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import Editor, { type OnMount } from '@monaco-editor/react'
import { Select, Tooltip, message, Modal, Input } from 'antd'
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
import { generationApi } from '@/services/generationService'
import {
  getToolDisplayName as mapToolName,
} from '@/utils/toolDisplayMap'
import type {
  ServerMsg, DiffData, EditMode,
  SessionCreatedMsg, SessionListMsg, ContentChunkMsg, ThinkingChunkMsg,
  ToolCallMsg,
  EditStartedMsg, EditAppliedMsg, EditAcceptedMsg, EditRejectedMsg,
  ErrorMsg,
  SessionLoadedMsg,
  EditPreviewMsg,
  EditPendingMsg,
  ChapterStreamMsg,
  ReasoningEffort,
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
  type: 'text'
  content: string
  thinkingContent: string
  thinkingDone: boolean
  isStreaming: boolean
}

interface TurnSegmentTool {
  type: 'tool'
  call: ToolCallInfo
}

type TurnSegment = TurnSegmentText | TurnSegmentTool

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

const EDIT_MODE_OPTIONS: Array<{ value: EditMode; label: string; desc: string }> = [
  { value: 'agent', label: '智能助手', desc: '可读可写，帮助创作修改' },
  { value: 'review', label: '审阅模式', desc: '只读审查，发现问题提供建议' },
  { value: 'plan', label: '规划模式', desc: '只读规划，制定大纲和计划' },
]

function getToolDisplayName(toolName: string, chapterNumber?: number, chapterTitle?: string): string {
  if ((toolName === 'read_chapter' || toolName === 'get_chapter_content') && chapterNumber != null) {
    return `查看第${chapterNumber}章${chapterTitle ? ` ${chapterTitle}` : ''}`
  }
  if (toolName === 'edit_chapter' && chapterNumber != null) {
    return `编辑第${chapterNumber}章${chapterTitle ? ` ${chapterTitle}` : ''}`
  }
  if (toolName === 'create_new_chapter' && chapterNumber != null) {
    return `创建第${chapterNumber}章${chapterTitle ? ` ${chapterTitle}` : ''}`
  }
  return mapToolName(toolName)
}

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
  const [selectedEditMode, setSelectedEditMode] = useState<EditMode>('agent')
  const [reasoningEffort, setReasoningEffort] = useState<ReasoningEffort>('high')
  const [changeCount, setChangeCount] = useState(0)
  const [hasActiveEdit, setHasActiveEdit] = useState(false)
  const [showDiff, setShowDiff] = useState(false)
  const [diffData, setDiffData] = useState<DiffData | null>(null)
  const [isSaving, setIsSaving] = useState(false)
  const [isApplyingEdit, setIsApplyingEdit] = useState(false)
  const [editNotice, setEditNotice] = useState<string>('')

  const [leftTab, setLeftTab] = useState<'chapters' | 'sessions'>('chapters')
  const [sidebarCollapsed, setSidebarCollapsed] = useState(false)
  const [sessions, setSessions] = useState<SessionInfo[]>([])
  const [currentSessionId, setCurrentSessionId] = useState<string | null>(null)

  const [turns, setTurns] = useState<ConversationTurn[]>([])
  const [inputValue, setInputValue] = useState('')
  const [isStreaming, setIsStreaming] = useState(false)
  const currentTurnIdRef = useRef<string | null>(null)
  const chatContainerRef = useRef<HTMLDivElement>(null)

  const [createChapterModal, setCreateChapterModal] = useState(false)
  const [newChapterTitle, setNewChapterTitle] = useState('')
  const [newChapterNumber, setNewChapterNumber] = useState<number | null>(null)
  const [creatingChapter, setCreatingChapter] = useState(false)
  const pendingMessageRef = useRef<string | null>(null)
  const pendingInterruptMessageRef = useRef<string | null>(null)
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const [streamingChapterMeta, setStreamingChapterMeta] = useState<{ id: number; chapterNumber: number; title: string } | null>(null)

  const updateTurn = useCallback((turnId: string, updater: (turn: ConversationTurn) => ConversationTurn) => {
    setTurns(prev => prev.map(t => t.id === turnId ? updater(t) : t))
  }, [])

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

    generationApi.getModels().then(res => {
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
    if (chatContainerRef.current) {
      chatContainerRef.current.scrollTop = chatContainerRef.current.scrollHeight
    }
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
        if (m.edit_mode) setSelectedEditMode(m.edit_mode)
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

              if (msg.metadata?.tool_calls) {
                const toolCalls = Array.isArray(msg.metadata.tool_calls)
                  ? msg.metadata.tool_calls
                  : JSON.parse(msg.metadata.tool_calls || '[]')
                toolCalls.forEach((tc: any) => {
                  turn.segments.push({
                    type: 'tool',
                    call: {
                      tool_name: tc.function?.name || tc.name || 'unknown',
                      display_text: getToolDisplayName(tc.function?.name || tc.name || 'unknown'),
                      status: 'completed',
                      task_id: msgTaskId,
                    },
                  })
                })
              }

              if (msg.content || msg.metadata?.thinking_content) {
                turn.segments.push({
                  type: 'text',
                  content: msg.content || '',
                  thinkingContent: msg.metadata?.thinking_content || '',
                  thinkingDone: true,
                  isStreaming: false,
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
        const turnId = m.task_id || `task_${Date.now()}`
        currentTurnIdRef.current = turnId
        setTurns(prev => [...prev, {
          id: turnId,
          segments: [],
          status: 'streaming',
        }])
        break
      }
      case 'thinking_chunk': {
        const m = msg as ThinkingChunkMsg
        if (!m.chunk?.trim()) break
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
        const turnId = currentTurnIdRef.current
        if (!turnId) break
        updateTurn(turnId, turn => ({
          ...turn,
          segments: turn.segments.map(seg =>
            seg.type === 'text' && seg.thinkingContent && !seg.thinkingDone
              ? { ...seg, thinkingDone: true }
              : seg
          ),
        }))
        break
      }
      case 'content_chunk': {
        const m = msg as ContentChunkMsg
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
        if (streamingChapterMeta?.id) {
          void refreshSelectedChapter(streamingChapterMeta.id)
          setStreamingChapterMeta(null)
        }
        if (pendingInterruptMessageRef.current) {
          const nextMessage = pendingInterruptMessageRef.current
          pendingInterruptMessageRef.current = null
          dispatchMessage(nextMessage)
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
        const turnId = currentTurnIdRef.current
        if (turnId) {
          updateTurn(turnId, turn => {
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
              display_text: m.display_text || getToolDisplayName(m.tool_name, m.chapter_number, m.chapter_title),
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
                type: 'tool',
                call: { ...existing, ...toolCall },
              }
            } else {
              segs.push({ type: 'tool', call: toolCall })
            }
            return { ...turn, segments: segs }
          })
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
          setStreamingChapterMeta(null)
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
      case 'chapter_stream': {
        const m = msg as ChapterStreamMsg
        if (selectedChapterId !== m.chapter_id) {
          setSelectedChapterId(m.chapter_id)
          void refreshChapterList()
        }
        setStreamingChapterMeta({
          id: m.chapter_id,
          chapterNumber: m.chapter_number,
          title: m.chapter_title || `第${m.chapter_number}章`,
        })
        setWorkingContent(m.content)
        setChapterWordCount(m.word_count)
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
        setOriginalContent(m.diff?.old_content || originalContent)
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
        setStreamingChapterMeta(null)
        if (pendingInterruptMessageRef.current) {
          const nextMessage = pendingInterruptMessageRef.current
          pendingInterruptMessageRef.current = null
          dispatchMessage(nextMessage)
        }
        break
      }
      case 'task_cancelled': {
        setIsStreaming(false)
        setIsApplyingEdit(false)
        setStreamingChapterMeta(null)
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
          dispatchMessage(nextMessage)
        }
        break
      }
    }
  }, [dispatchMessage, originalContent, refreshChapterList, refreshSelectedChapter, selectedChapterId, streamingChapterMeta, updateTurn])

  useEffect(() => {
    const unsub = wsEditorService.onMessage(handleMsg)
    return unsub
  }, [handleMsg])

  const handleEditorMount: OnMount = (editor) => {
    editorRef.current = editor
  }

  const selectChapter = async (chapterId: number) => {
    setSelectedChapterId(chapterId)
    setStreamingChapterMeta(null)
    setShowDiff(false)
    setDiffData(null)
    setEditNotice('')

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
    const turnId = `turn_${Date.now()}`
    currentTurnIdRef.current = turnId
    setTurns(prev => [...prev, {
      id: turnId,
      userMessage: msg,
      segments: [],
      status: 'streaming',
    }])

    if (!currentSessionId) {
      pendingMessageRef.current = msg
      const sent = wsEditorService.createSession(selectedModel, selectedEditMode, reasoningEffort)
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
    : streamingChapterMeta
      ? { chapterNumber: streamingChapterMeta.chapterNumber, title: streamingChapterMeta.title }
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
                      {streamingChapterMeta ? 'AI 正在写作' : hasPendingReview ? '有待确认修改' : '正文已同步'}
                    </span>
                    <span className={styles.chapterPill}>
                      {selectedEditMode === 'agent' ? '协作创作' : selectedEditMode === 'review' ? '审阅模式' : '规划模式'}
                    </span>
                  </div>
                </div>
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
                {selectedChapter && showDiff && hasPendingReview ? (
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
                    height="100%"
                    language="markdown"
                    theme={theme}
                    value={workingContent}
                    onChange={handleEditorChange}
                    onMount={handleEditorMount}
                    options={{
                      readOnly: Boolean(streamingChapterMeta),
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

                  {turn.segments.map((seg, idx) => {
                    if (seg.type === 'text') {
                      if (!seg.content && !seg.thinkingContent) return null
                      return (
                        <div key={`text_${idx}`} className={styles.chatTurnAssistant}>
                          {seg.thinkingContent && (
                            <details className={`${styles.thinkingBlock} ${!seg.thinkingDone && seg.isStreaming ? styles.thinkingThinking : ''}`} open={seg.isStreaming ? true : !seg.thinkingDone}>
                              <summary className={styles.thinkingSummary}>
                                <ReadOutlined /> 思考过程
                                {seg.thinkingDone || (!seg.isStreaming && seg.content)
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

                    const tc = seg.call
                    const visual = getActivityVisual(tc.activity_kind)
                    return (
                      <div
                        key={`tool_${tc.tool_id || `${tc.task_id}_${idx}`}`}
                        className={`${styles.toolInlineCompact} ${tc.status === 'executing' ? styles.toolInlineActive : ''}`}
                      >
                        <span className={styles.toolCompactIcon}>{visual.icon}</span>
                        <span className={styles.toolCompactName}>
                          {tc.display_text || getToolDisplayName(tc.tool_name, tc.chapter_number, tc.chapter_title)}
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
              <div className={styles.controlChip}>
                <span className={styles.controlLabel}>模型</span>
                <Select
                  size="small"
                  value={selectedModel}
                  onChange={setSelectedModel}
                  className={styles.chatControlSelect}
                  popupMatchSelectWidth={false}
                  options={modelOptions}
                />
              </div>
              {(selectedModel.startsWith('deepseek-v4')) && (
                <div className={styles.controlChip}>
                  <span className={styles.controlLabel}>推理强度</span>
                  <Select
                    size="small"
                    value={reasoningEffort}
                    onChange={setReasoningEffort}
                    className={styles.chatControlCompact}
                    popupMatchSelectWidth={false}
                    options={REASONING_OPTIONS}
                  />
                </div>
              )}
              <div className={styles.controlChip}>
                <span className={styles.controlLabel}>模式</span>
                <Select
                  size="small"
                  value={selectedEditMode}
                  onChange={setSelectedEditMode}
                  className={styles.chatControlSelect}
                  popupMatchSelectWidth={false}
                  options={EDIT_MODE_OPTIONS.map(o => ({ value: o.value, label: o.label }))}
                />
              </div>
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
