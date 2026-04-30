import { useState, useEffect, useRef, useCallback } from 'react'
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
  SettingOutlined,
  LeftOutlined,
  RightOutlined,
  BgColorsOutlined,
  EditOutlined,
  SaveOutlined,
  EyeOutlined,
  FileAddOutlined,
  BookOutlined,
  HighlightOutlined,
  BranchesOutlined,
  ReadOutlined,
} from '@ant-design/icons'
import { wsEditorService } from '@/services/wsEditorService'
import { editorApi } from '@/services/editorService'
import { chapterApi } from '@/services/chapterService'
import {
  getToolDisplayName as mapToolName,
  getToolUserAction,
} from '@/utils/toolDisplayMap'
import type {
  ServerMsg, Scope, ScopeType, DiffData, EditMode,
  SessionCreatedMsg, SessionListMsg, ContentChunkMsg, ThinkingChunkMsg, ThinkingDoneMsg,
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
  scope: Scope
  display_name: string
  message_count: number
  updated_at: string
}

interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  isStreaming?: boolean
  taskId?: string
  timestamp?: number
  kind?: 'message' | 'tool'
  thinkingContent?: string
  thinkingDone?: boolean
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
  order: number
}

const SCOPE_OPTIONS: Array<{ value: ScopeType; label: string }> = [
  { value: 'novel', label: '整本小说' },
  { value: 'chapter', label: '单章' },
  { value: 'chapters', label: '章节范围' },
]

const MODEL_OPTIONS = [
  { value: 'deepseek-v4-flash', label: 'DeepSeek V4 Flash' },
  { value: 'deepseek-v4-pro', label: 'DeepSeek V4 Pro' },
  { value: 'deepseek-chat', label: 'DeepSeek Chat (兼容)' },
  { value: 'deepseek-reasoner', label: 'DeepSeek Reasoner (兼容)' },
  { value: 'qwen-max', label: 'Qwen Max (阿里)' },
  { value: 'qwen-plus', label: 'Qwen Plus (阿里)' },
  { value: 'qwq', label: 'QwQ 推理 (阿里)' },
  { value: 'glm-4.7-flash', label: 'GLM-4.7 Flash' },
]

const REASONING_OPTIONS: Array<{ value: ReasoningEffort; label: string }> = [
  { value: 'high', label: '高' },
  { value: 'max', label: '最大' },
]

const EDIT_MODE_OPTIONS: Array<{ value: EditMode; label: string; desc: string }> = [
  { value: 'agent', label: '智能助手', desc: '可读可写，帮助创作修改' },
  { value: 'review', label: '审阅模式', desc: '只读，提供审阅意见' },
  { value: 'plan', label: '规划模式', desc: '只读，创建写作大纲' },
]

function getScopeLabel(scope: Scope): string {
  if (scope.type === 'novel') return '整本小说'
  if (scope.type === 'chapter') return `第${scope.chapter_start}章`
  return `第${scope.chapter_start}-${scope.chapter_end}章`
}

function getToolDisplayName(toolName: string, chapterNumber?: number, chapterTitle?: string): string {
  const chapterLabel = chapterNumber
    ? `第${chapterNumber}章${chapterTitle ? ` ${chapterTitle}` : ''}`
    : chapterTitle || ''

  const chapterAwareMap: Record<string, (label: string) => string> = {
    read_chapter_for_edit: (l) => l ? `查看 ${l}` : '读取待编辑原文',
    read_chapter: (l) => l ? `查看 ${l}` : '读取章节正文',
    get_chapter_content: (l) => l ? `查看 ${l}` : '读取章节正文',
    start_edit_session: (l) => l ? `准备修改 ${l}` : '开始安全编辑',
    edit_chapter_content: (l) => l ? `正在修改 ${l}` : '编辑章节内容',
    create_new_chapter: (l) => l ? `创建 ${l}` : '创建新章节',
    generate_chapter_draft: (l) => l ? `正在撰写 ${l}` : 'AI生成新章节',
    apply_edit: (l) => l ? `正在修改 ${l}` : '应用修改内容',
    get_edit_status: (l) => l ? `查看 ${l} 的修改进度` : '查看编辑状态',
  }

  if (chapterAwareMap[toolName]) {
    return chapterAwareMap[toolName](chapterLabel)
  }

  const name = mapToolName(toolName)
  if (name !== toolName) return name

  return getToolUserAction(toolName).replace(/正在|正在/g, '').replace(/[…。]/g, '')
}

function sanitizeToolError(rawError: string | undefined): string | undefined {
  if (!rawError) return undefined
  const cleaned = rawError
    .replace(/^工具\s+\w+\s+失败[：:]\s*/, '')
    .replace(/\s*请修正参数后重试。?\s*$/g, '')
    .replace(/[a-zA-Z_]*Error[(:][^)]*[)]?\s*:?\s*/gi, '')
    .replace(/name\s+['"][\w']+['"]\s+is\s+not\s+defined/gi, '内部配置错误')
    .replace(/源角色或目标角色不存在/gi, '角色不存在')
    .replace(/操作人物关系失败/gi, '')
    .replace(/更新时间线条目失败/gi, '')
    .trim()
  return cleaned || undefined
}

function getActivityVisual(activityKind?: ToolCallInfo['activity_kind']) {
  switch (activityKind) {
    case 'view':
    case 'browse':
      return { icon: <EyeOutlined />, badge: '查看中' }
    case 'create':
      return { icon: <FileAddOutlined />, badge: '创建中' }
    case 'write':
      return { icon: <EditOutlined />, badge: '写作中' }
    case 'edit':
      return { icon: <HighlightOutlined />, badge: '修改中' }
    case 'memory':
      return { icon: <BookOutlined />, badge: '回看中' }
    case 'review':
      return { icon: <ReadOutlined />, badge: '检查中' }
    case 'plan':
      return { icon: <BranchesOutlined />, badge: '规划中' }
    default:
      return { icon: <SettingOutlined />, badge: '处理中' }
  }
}

function InlineDiffPreview({ diff }: { diff: DiffData }) {
  return (
    <div className={styles.inlineDiffPane}>
      {diff.hunks.map((hunk, hunkIndex) => (
        <div key={`hunk_${hunkIndex}`} className={styles.inlineDiffHunk}>
          <div className={styles.inlineDiffHeader}>
            变更块 {hunkIndex + 1} · 原第 {hunk.old_start} 行 / 新第 {hunk.new_start} 行
          </div>
          {hunk.changes.map((change, changeIndex) => (
            <div
              key={`change_${hunkIndex}_${changeIndex}`}
              className={
                change.type === 'insert'
                  ? styles.inlineDiffInsert
                  : change.type === 'delete'
                    ? styles.inlineDiffDelete
                    : styles.inlineDiffContext
              }
            >
              <span className={styles.inlineDiffMarker}>
                {change.type === 'insert' ? '+' : change.type === 'delete' ? '-' : ' '}
              </span>
              <span className={styles.inlineDiffLineNo}>{change.line_number}</span>
              <pre className={styles.inlineDiffContent}>{change.content || ' '}</pre>
            </div>
          ))}
        </div>
      ))}
    </div>
  )
}

export default function EditorPage() {
  const { novelId } = useParams<{ novelId: string }>()
  const navigate = useNavigate()
  const editorRef = useRef<Parameters<OnMount>[0] | null>(null)

  const [connected, setConnected] = useState(false)
  const [darkMode, setDarkMode] = useState(false)
  const [selectedModel, setSelectedModel] = useState('deepseek-v4-flash')
  const [reasoningEffort, setReasoningEffort] = useState<ReasoningEffort>('max')
  const [selectedEditMode, setSelectedEditMode] = useState<EditMode>('agent')

  const [chapters, setChapters] = useState<ChapterInfo[]>([])
  const [selectedChapterId, setSelectedChapterId] = useState<number | null>(null)

  const [originalContent, setOriginalContent] = useState('')
  const [workingContent, setWorkingContent] = useState('')
  const [chapterWordCount, setChapterWordCount] = useState(0)
  const [editSessionId, setEditSessionId] = useState<string | null>(null)
  const [latestPendingEditSessionId, setLatestPendingEditSessionId] = useState<string | null>(null)
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
  const [currentScope, setCurrentScope] = useState<Scope>({ type: 'novel' })
  const [scopeChapterStart, setScopeChapterStart] = useState<number | undefined>()
  const [scopeChapterEnd, setScopeChapterEnd] = useState<number | undefined>()

  const [chatMessages, setChatMessages] = useState<ChatMessage[]>([])
  const [toolCalls, setToolCalls] = useState<ToolCallInfo[]>([])
  const [inputValue, setInputValue] = useState('')
  const [isStreaming, setIsStreaming] = useState(false)
  const [currentTaskId, setCurrentTaskId] = useState<string | null>(null)
  const chatContainerRef = useRef<HTMLDivElement>(null)

  const [createChapterModal, setCreateChapterModal] = useState(false)
  const [newChapterTitle, setNewChapterTitle] = useState('')
  const [newChapterNumber, setNewChapterNumber] = useState<number | null>(null)
  const [creatingChapter, setCreatingChapter] = useState(false)
  const pendingMessageRef = useRef<string | null>(null)
  const pendingInterruptMessageRef = useRef<string | null>(null)
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const timelineOrderRef = useRef(0)
  const shouldBreakAssistantSegmentRef = useRef(false)
  const [streamingChapterMeta, setStreamingChapterMeta] = useState<{ id: number; chapterNumber: number; title: string } | null>(null)

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

    return () => {
      wsEditorService.disconnect()
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
    }
  }, [novelId])

  useEffect(() => {
    if (chatContainerRef.current) {
      chatContainerRef.current.scrollTop = chatContainerRef.current.scrollHeight
    }
  }, [chatMessages, toolCalls])

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
        setCurrentScope(m.scope)
        if (m.edit_mode) setSelectedEditMode(m.edit_mode)
        if (m.model) setSelectedModel(m.model)
        if (m.reasoning_effort) setReasoningEffort(m.reasoning_effort)
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
        setCurrentScope(m.scope)
        if (m.recent_messages && m.recent_messages.length > 0) {
          const history: ChatMessage[] = []
          const historyToolCalls: ToolCallInfo[] = []
          
          m.recent_messages.forEach((msg, i) => {
            const msgId = msg.message_id || `hist_${i}`
            
            if (msg.role === 'assistant' && msg.metadata?.tool_calls) {
              const toolCalls = Array.isArray(msg.metadata.tool_calls) 
                ? msg.metadata.tool_calls 
                : JSON.parse(msg.metadata.tool_calls || '[]')
              
              toolCalls.forEach((tc: any) => {
                historyToolCalls.push({
                  tool_name: tc.function?.name || tc.name || 'unknown',
                  display_text: getToolDisplayName(tc.function?.name || tc.name || 'unknown'),
                  status: 'completed',
                  task_id: msgId,
                  order: timelineOrderRef.current++,
                })
              })
            }
            
            if (msg.content || msg.metadata?.thinking_content) {
              history.push({
                id: msgId,
                role: msg.role as 'user' | 'assistant',
                content: msg.content || '',
                timestamp: timelineOrderRef.current++,
                thinkingContent: msg.metadata?.thinking_content || undefined,
              })
            }
          })
          
          setChatMessages(history)
          setToolCalls(historyToolCalls)
        } else {
          setChatMessages([])
          setToolCalls([])
        }
        break
      }
      case 'chat_started': {
        const m = msg as { type: 'chat_started'; task_id?: string }
        setIsStreaming(true)
        if (m.task_id) setCurrentTaskId(m.task_id)
        shouldBreakAssistantSegmentRef.current = false
        const newMsg = {
          id: m.task_id || `stream_${Date.now()}`,
          role: 'assistant' as const,
          content: '',
          isStreaming: true,
          taskId: m.task_id,
          timestamp: timelineOrderRef.current++,
        }
        setChatMessages(prev => [...prev, newMsg])
        break
      }
      case 'thinking_chunk': {
        const m = msg as ThinkingChunkMsg
        if (!m.chunk?.trim()) break
        setChatMessages(prev => {
          const taskId = m.task_id || currentTaskId || undefined
          const existingIndex = prev.findIndex(item => item.taskId === taskId && item.role === 'assistant' && item.isStreaming)
          if (existingIndex >= 0) {
            return prev.map((item, index) => index === existingIndex
              ? { ...item, thinkingContent: (item.thinkingContent || '') + m.chunk }
              : item)
          }
          return prev
        })
        break
      }
      case 'thinking_done': {
        void (msg as ThinkingDoneMsg)
        setChatMessages(prev => prev.map(item => {
          if (item.isStreaming && item.thinkingContent) {
            return { ...item, thinkingDone: true }
          }
          return item
        }))
        break
      }
      case 'content_chunk': {
        const m = msg as ContentChunkMsg
        setChatMessages(prev => {
          const taskId = m.task_id || currentTaskId || undefined
          const existingIndex = shouldBreakAssistantSegmentRef.current
            ? -1
            : prev.findIndex(item => item.taskId === taskId && item.role === 'assistant' && item.isStreaming)
          if (existingIndex >= 0) {
            return prev.map((item, index) => index === existingIndex
              ? { ...item, content: item.content + m.chunk, isStreaming: true }
              : item)
          }
          shouldBreakAssistantSegmentRef.current = false
          return [
            ...prev,
            {
              id: `${taskId || 'unknown'}_round_${Date.now()}`,
              role: 'assistant' as const,
              content: m.chunk,
              isStreaming: true,
              taskId,
              timestamp: timelineOrderRef.current++,
            },
          ]
        })
        break
      }
      case 'chat_completed': {
        setIsStreaming(false)
        setCurrentTaskId(null)
        shouldBreakAssistantSegmentRef.current = false
        setChatMessages(prev => {
          return prev.map(item => item.taskId === msg.task_id
            ? { ...item, isStreaming: false }
            : item)
        })
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
      case 'tool_call': {
        const m = msg as ToolCallMsg
        setToolCalls(prev => {
          const idx = prev.findIndex(t => (
            (m.tool_id && t.tool_id === m.tool_id) ||
            (!m.tool_id && t.task_id === m.task_id && t.tool_name === m.tool_name && t.status === 'executing')
          ))
        if (idx >= 0) {
          return prev.map((t, i) => i === idx ? {
              ...t,
              status: m.status,
              task_id: m.task_id || t.task_id,
              phase: m.phase || t.phase,
              display_text: m.display_text || t.display_text,
              activity_kind: m.activity_kind || t.activity_kind,
              chapter_id: m.chapter_id ?? t.chapter_id,
              chapter_number: m.chapter_number ?? t.chapter_number,
              chapter_title: m.chapter_title ?? t.chapter_title,
              arguments: m.arguments || t.arguments,
              result_summary: m.result_summary || t.result_summary,
              error: m.error,
              tool_id: m.tool_id || t.tool_id,
              timestamp: m.timestamp || t.timestamp,
            } : t)
          }
          const newToolCall: ToolCallInfo = { 
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
            order: timelineOrderRef.current++,
          }
          return [...prev, newToolCall].slice(-20)
        })
        setChatMessages(prev => prev.map(item =>
          item.taskId === m.task_id && item.isStreaming
            ? { ...item, isStreaming: false }
            : item
        ))
        if (m.status === 'completed' && (m.tool_name === 'create_new_chapter' || m.tool_name === 'generate_chapter_draft' || m.tool_name === 'get_chapter_list')) {
          void refreshChapterList()
        }
        if (m.status === 'completed' && m.chapter_id && (m.activity_kind === 'edit' || m.activity_kind === 'write')) {
          void revealChapterFromTool(m.chapter_id)
        } else if (m.status === 'completed' && m.chapter_number && (m.activity_kind === 'edit' || m.activity_kind === 'write')) {
          void revealChapterByNumber(m.chapter_number)
        }
        if (m.tool_name === 'generate_chapter_draft' && (m.status === 'failed' || m.status === 'rejected')) {
          setStreamingChapterMeta(null)
          setEditNotice('这次章节写作没有完成，正文保持原样。')
          void refreshChapterList()
        }
        if (m.status === 'executing' || m.status === 'completed' || m.status === 'failed' || m.status === 'rejected') {
          shouldBreakAssistantSegmentRef.current = true
        }
        if ((m.tool_name === 'apply_edit' || m.tool_name === 'start_edit_session') && (m.status === 'failed' || m.status === 'rejected')) {
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
          setShowDiff(false)
          setDiffData(null)
          setHasActiveEdit(false)
          setEditSessionId(null)
          setLatestPendingEditSessionId(null)
          setChangeCount(0)
          void refreshChapterList()
        }
        setStreamingChapterMeta({
          id: m.chapter_id,
          chapterNumber: m.chapter_number,
          title: m.chapter_title || `第${m.chapter_number}章`,
        })
        setOriginalContent('')
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
        setCurrentTaskId(null)
        setStreamingChapterMeta(null)
        shouldBreakAssistantSegmentRef.current = false
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
        setCurrentTaskId(null)
        setStreamingChapterMeta(null)
        shouldBreakAssistantSegmentRef.current = false
        setChatMessages(prev => prev.map(item => item.taskId === currentTaskId ? { ...item, isStreaming: false } : item))
        if (pendingInterruptMessageRef.current) {
          const nextMessage = pendingInterruptMessageRef.current
          pendingInterruptMessageRef.current = null
          dispatchMessage(nextMessage)
        }
        break
      }
    }
  }, [currentTaskId, dispatchMessage, originalContent, refreshChapterList, refreshSelectedChapter, selectedChapterId, streamingChapterMeta])

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
    const userMsg: ChatMessage = {
      id: `user_${Date.now()}`,
      role: 'user',
      content: msg,
      timestamp: timelineOrderRef.current++,
    }
    setChatMessages(prev => [...prev, userMsg])

    if (!currentSessionId) {
      const scope: Scope = { ...currentScope }
      if (scope.type === 'chapter' && scopeChapterStart) scope.chapter_start = scopeChapterStart
      else if (scope.type === 'chapters') {
        if (scopeChapterStart) scope.chapter_start = scopeChapterStart
        if (scopeChapterEnd) scope.chapter_end = scopeChapterEnd
      }
      pendingMessageRef.current = msg
      const sent = wsEditorService.createSession(scope, selectedModel, selectedEditMode, reasoningEffort)
      if (!sent) {
        message.error('WebSocket 未连接')
        setChatMessages(prev => prev.filter(m => m.id !== userMsg.id))
        pendingMessageRef.current = null
      }
      return
    }

    const sent = wsEditorService.chat(currentSessionId, msg, true)
    if (!sent) {
      message.error('WebSocket 未连接')
      setChatMessages(prev => prev.filter(m => m.id !== userMsg.id))
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

  const handleScopeTypeChange = (scopeType: ScopeType) => {
    const newScope: Scope = { type: scopeType }
    if (scopeType === 'chapter') {
      if (scopeChapterStart) newScope.chapter_start = scopeChapterStart
      else if (selectedChapterId) {
        const ch = chapters.find(c => c.id === selectedChapterId)
        if (ch) newScope.chapter_start = ch.chapter_number
      }
    } else if (scopeType === 'chapters') {
      if (scopeChapterStart) newScope.chapter_start = scopeChapterStart
      if (scopeChapterEnd) newScope.chapter_end = scopeChapterEnd
    }
    setCurrentScope(newScope)
  }

  const handleStop = () => {
    if (currentTaskId) {
      wsEditorService.cancelTask(currentTaskId)
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
  interface ConversationTurnItem {
    type: 'message' | 'tool'
    item: ChatMessage | ToolCallInfo
  }

  interface ConversationTurn {
    id: string
    role: 'user' | 'assistant'
    userMessage?: ChatMessage
    items: ConversationTurnItem[]
    order: number
  }

function buildConversationTurns(
  msgs: ChatMessage[],
  calls: ToolCallInfo[]
): ConversationTurn[] {
  const sorted = [
    ...msgs.map(m => ({ type: 'message' as const, order: m.timestamp || 0, item: m })),
    ...calls.map(t => ({ type: 'tool' as const, order: t.order, item: t })),
  ].sort((a, b) => a.order - b.order)

  const turns: ConversationTurn[] = []
  let currentTurn: ConversationTurn | null = null

  for (const entry of sorted) {
    if (entry.type === 'message') {
      const msg = entry.item as ChatMessage
      if (msg.role === 'user') {
        if (currentTurn) turns.push(currentTurn)
        currentTurn = {
          id: msg.id,
          role: 'user',
          userMessage: msg,
          items: [],
          order: entry.order,
        }
      } else {
        if (currentTurn && currentTurn.role === 'user') {
          turns.push(currentTurn)
          currentTurn = null
        }
        if (!currentTurn) {
          currentTurn = { id: msg.id, role: 'assistant', items: [], order: entry.order }
        }
        currentTurn.items.push({ type: 'message', item: msg })
      }
    } else {
      const tc = entry.item as ToolCallInfo
      if (currentTurn && currentTurn.role === 'user') {
        turns.push(currentTurn)
        currentTurn = null
      }
      if (!currentTurn) {
        currentTurn = { id: tc.tool_id || `tc_${tc.order}`, role: 'assistant', items: [], order: entry.order }
      }
      currentTurn.items.push({ type: 'tool', item: tc })
    }
  }
  if (currentTurn) turns.push(currentTurn)
  return turns
}

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
                        setCurrentSessionId(s.session_id)
                        wsEditorService.loadSession(s.session_id)
                        setChatMessages([])
                        setToolCalls([])
                      }}
                    >
                      <span>{s.display_name}</span>
                      <span className={styles.sessionScope}>
                        {getScopeLabel(s.scope)} · {s.message_count}条
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
                    <span className={styles.editorWordCount}>
                      {`${chapterWordCount.toLocaleString()} 字`}
                    </span>
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
            {chatMessages.length === 0 && !isStreaming && (
              <div className={styles.emptyChat}>
                <MessageOutlined className={styles.emptyChatIcon} />
                <div>输入消息开始对话</div>
                <div style={{ fontSize: 12 }}>AI 可以帮你修改章节内容</div>
              </div>
            )}
            {buildConversationTurns(chatMessages, toolCalls).map((turn) => {
              if (turn.role === 'user') {
                return (
                  <div key={turn.id} className={styles.chatTurn}>
                    <div className={styles.chatMsgUser}>
                      <Markdown>{turn.userMessage?.content || ''}</Markdown>
                    </div>
                  </div>
                )
              }

              const hasAnyContent = turn.items.length > 0
              if (!hasAnyContent) return null

              const isCurrentlyStreaming = turn.items.some(
                it => it.type === 'message' && (it.item as ChatMessage).isStreaming
              )

              return (
                <div key={turn.id} className={`${styles.chatTurn} ${styles.chatTurnAssistant}`}>
                  {turn.items.map((it, idx) => {
                    if (it.type === 'message') {
                      const msg = it.item as ChatMessage
                      if (!msg.content && !msg.thinkingContent) return null
                      return (
                        <div key={`msg_${msg.id || idx}`}>
                          {msg.thinkingContent && (
                            <details className={`${styles.thinkingBlock} ${!msg.thinkingDone && msg.isStreaming ? styles.thinkingThinking : ''}`} open={msg.isStreaming ? true : !msg.thinkingDone}>
                              <summary className={styles.thinkingSummary}>
                                <ReadOutlined /> 思考过程
                                {msg.thinkingDone || (!msg.isStreaming && msg.content)
                                  ? <span className={styles.thinkingToggle}>展开</span>
                                  : <span className={`${styles.thinkingToggle} ${styles.thinkingActive}`}>思考中…</span>
                                }
                              </summary>
                              <div className={styles.thinkingContent}>
                                <pre>{msg.thinkingContent}</pre>
                              </div>
                            </details>
                          )}
                          {msg.content && (
                            <div className={styles.assistantTextBlock}>
                              <Markdown>{msg.content}</Markdown>
                            </div>
                          )}
                        </div>
                      )
                    }

                    const tc = it.item as ToolCallInfo
                    const visual = getActivityVisual(tc.activity_kind)
                    return (
                      <div
                        key={`tool_${tc.tool_id || `${tc.task_id}_${tc.order}`}`}
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
                  {isCurrentlyStreaming && !turn.items.some(it => it.type === 'message' && (it.item as ChatMessage).content) && (
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
                  options={MODEL_OPTIONS}
                />
              </div>
              {(selectedModel.startsWith('deepseek-v4') || selectedModel === 'deepseek-reasoner') && (
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
                <span className={styles.controlLabel}>作用域</span>
                <Select
                  size="small"
                  value={currentScope.type}
                  onChange={handleScopeTypeChange}
                  className={styles.chatControlSelect}
                  popupMatchSelectWidth={false}
                  options={SCOPE_OPTIONS.map(o => ({ value: o.value, label: o.label }))}
                />
                {currentScope.type === 'chapter' && (
                  <Select
                    size="small"
                    value={scopeChapterStart}
                    onChange={v => {
                      setScopeChapterStart(v)
                      setCurrentScope({ type: 'chapter', chapter_start: v })
                    }}
                    className={styles.chatControlCompact}
                    placeholder="章节"
                    popupMatchSelectWidth={false}
                    options={chapters.map(ch => ({
                      value: ch.chapter_number,
                      label: `第${ch.chapter_number}章`,
                    }))}
                  />
                )}
                {currentScope.type === 'chapters' && (
                  <>
                    <Select
                      size="small"
                      value={scopeChapterStart}
                      onChange={v => setScopeChapterStart(v)}
                      className={styles.chatControlCompact}
                      placeholder="起始"
                      options={chapters.map(ch => ({
                        value: ch.chapter_number,
                        label: `第${ch.chapter_number}章`,
                      }))}
                    />
                    <Select
                      size="small"
                      value={scopeChapterEnd}
                      onChange={v => setScopeChapterEnd(v)}
                      className={styles.chatControlCompact}
                      placeholder="结束"
                      options={chapters.map(ch => ({
                        value: ch.chapter_number,
                        label: `第${ch.chapter_number}章`,
                      }))}
                    />
                  </>
                )}
              </div>
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
