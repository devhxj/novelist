import { useState, useEffect, useRef, useCallback } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import Editor, { DiffEditor, type OnMount } from '@monaco-editor/react'
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
import type {
  ServerMsg, Scope, ScopeType, DiffData, EditMode,
  SessionCreatedMsg, SessionListMsg, ContentChunkMsg,
  ToolCallMsg,
  EditStartedMsg, EditAppliedMsg, EditAcceptedMsg, EditRejectedMsg,
  ErrorMsg,
  SessionLoadedMsg,
  EditPreviewMsg,
  EditPendingMsg,
  ChapterStreamMsg,
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
}

interface ToolCallInfo {
  tool_name: string
  status: 'executing' | 'completed' | 'failed' | 'rejected'
  tool_id?: string
  task_id?: string
  display_text?: string
  activity_kind?: 'general' | 'browse' | 'view' | 'edit' | 'write' | 'create' | 'memory' | 'review' | 'plan'
  chapter_id?: number
  chapter_number?: number
  chapter_title?: string
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
  { value: 'deepseek-chat', label: 'DeepSeek Chat' },
  { value: 'deepseek-reasoner', label: 'DeepSeek Reasoner' },
  { value: 'glm-4.7-flash', label: 'GLM-4.7 Flash' },
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

  const mapping: Record<string, string> = {
    get_chapter_list: '查看章节目录',
    read_chapter_for_edit: chapterLabel ? `查看 ${chapterLabel}` : '查看章节内容',
    read_chapter: chapterLabel ? `查看 ${chapterLabel}` : '查看章节内容',
    start_edit_session: chapterLabel ? `准备修改 ${chapterLabel}` : '准备修改章节',
    get_edit_status: chapterLabel ? `查看 ${chapterLabel} 的修改进度` : '查看修改进度',
    edit_chapter_content: chapterLabel ? `正在修改 ${chapterLabel}` : '正在修改章节',
    create_new_chapter: chapterLabel ? `创建 ${chapterLabel}` : '创建新章节',
    generate_chapter_draft: chapterLabel ? `正在撰写 ${chapterLabel}` : '正在撰写章节',
    get_creative_profile: '查看长期创作规则',
    update_creative_profile: '更新长期创作规则',
    search_memory: '查找前文设定和记忆',
    get_recent_context: '回看最近创作上下文',
    run_agent_task: '调用协作助手',
  }

  return mapping[toolName] || '处理创作任务'
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

export default function EditorPage() {
  const { novelId } = useParams<{ novelId: string }>()
  const navigate = useNavigate()
  const editorRef = useRef<Parameters<OnMount>[0] | null>(null)

  const [connected, setConnected] = useState(false)
  const [darkMode, setDarkMode] = useState(false)
  const [selectedModel, setSelectedModel] = useState('deepseek-chat')
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
            
            if (msg.content) {
              history.push({
                id: msgId,
                role: msg.role as 'user' | 'assistant',
                content: msg.content,
                timestamp: timelineOrderRef.current++,
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
              id: taskId || `stream_${Date.now()}`,
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
              display_text: m.display_text || t.display_text,
              activity_kind: m.activity_kind || t.activity_kind,
              chapter_id: m.chapter_id ?? t.chapter_id,
              chapter_number: m.chapter_number ?? t.chapter_number,
              chapter_title: m.chapter_title ?? t.chapter_title,
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
            display_text: m.display_text || getToolDisplayName(m.tool_name, m.chapter_number, m.chapter_title),
            activity_kind: m.activity_kind,
            chapter_id: m.chapter_id,
            chapter_number: m.chapter_number,
            chapter_title: m.chapter_title,
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
        if (m.tool_name === 'generate_chapter_draft' && m.status === 'executing') {
          setEditNotice('AI 正在撰写章节，正文会实时出现在中间编辑区。')
        }
        if (m.tool_name === 'generate_chapter_draft' && (m.status === 'failed' || m.status === 'rejected')) {
          setStreamingChapterMeta(null)
          setEditNotice('这次章节写作没有完成，已保留当前真实章节状态。')
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
        setEditNotice('AI 正在撰写正文，你可以实时看到内容生成。')
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
        setEditNotice('AI 正在修改副本内容，你可以实时看到改动。')
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
        setEditNotice('AI 已创建可确认的编辑副本，修改不会直接覆盖正文。')
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
        setEditNotice('修改已应用到副本，可以直接在差异视图里确认或拒绝。')
        break
      }
      case 'edit_accepted': {
        const m = msg as EditAcceptedMsg
        message.success(m.message)
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
      const sent = wsEditorService.createSession(scope, selectedModel, selectedEditMode)
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
  const timelineItems = [
    ...chatMessages.map(item => ({ type: 'message' as const, order: item.timestamp || 0, item })),
    ...toolCalls.map(item => ({ type: 'tool' as const, order: item.order, item })),
  ].sort((a, b) => a.order - b.order)

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
                    <button className={styles.btnAccept} onClick={acceptEdit}>
                      <CheckOutlined /> 接受
                    </button>
                    <button className={styles.btnReject} onClick={rejectEdit}>
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
                      {editNotice || 'AI 的修改会先进入副本，只有你确认后才会写回正文。'}
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
                  <DiffEditor
                    height="100%"
                    language="markdown"
                    theme={theme}
                    original={originalContent}
                    modified={workingContent}
                    options={{
                      readOnly: true,
                      minimap: { enabled: false },
                      lineNumbers: 'on',
                      scrollBeyondLastLine: false,
                      fontSize: 17,
                      lineHeight: 30,
                      fontFamily: "'LXGW WenKai Screen', 'Noto Serif SC', 'Source Han Serif SC', serif",
                      wordWrap: 'on',
                      renderSideBySide: true,
                      enableSplitViewResizing: true,
                    }}
                  />
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
            {timelineItems.map((entry) => {
              if (entry.type === 'message') {
                const msg = entry.item as ChatMessage
                return (
                  <div key={msg.id} className={styles.chatEntry}>
                    <div
                      className={`${styles.chatMsg} ${msg.role === 'user' ? styles.chatMsgUser : msg.isStreaming ? styles.chatMsgStreaming : styles.chatMsgAssistant}`}
                    >
                      <Markdown>{msg.content}</Markdown>
                    </div>
                  </div>
                )
              }

              const activity = entry.item as ToolCallInfo
              const activityKey = activity.tool_id || `${activity.task_id || 'no_task'}_${activity.tool_name}_${activity.order || activity.timestamp || Date.now()}`
              const visual = getActivityVisual(activity.activity_kind)
              return (
                <div key={activityKey} className={styles.toolInlineWrap}>
                  <div className={`${styles.activityCard} ${activity.status === 'executing' ? styles.activityCardActive : ''}`}>
                    <div className={styles.activityIconWrap}>
                      <span className={styles.activityIcon}>{visual.icon}</span>
                      {activity.status === 'executing' && <span className={styles.activityPulse} />}
                    </div>
                    <div className={styles.activityBody}>
                      <div className={styles.activityLine}>
                        <span className={styles.activityName}>
                          {activity.display_text || getToolDisplayName(activity.tool_name, activity.chapter_number, activity.chapter_title)}
                        </span>
                        <span className={`${styles.activityState} ${activity.status === 'executing' ? styles.activityStateRunning : activity.status === 'completed' ? styles.activityStateDone : styles.activityStateFailed}`}>
                          {activity.status === 'executing' && <LoadingOutlined spin />}
                          {activity.status === 'completed' && <CheckCircleOutlined />}
                          {(activity.status === 'failed' || activity.status === 'rejected') && <CloseCircleOutlined />}
                          {activity.status === 'executing' ? visual.badge : activity.status === 'completed' ? '已完成' : '未完成'}
                        </span>
                      </div>
                      {(activity.chapter_number || activity.chapter_title) && (
                        <div className={styles.activityMeta}>
                          {activity.chapter_number ? `第${activity.chapter_number}章` : ''}
                          {activity.chapter_title ? ` · ${activity.chapter_title}` : ''}
                        </div>
                      )}
                      {activity.error && <div className={styles.activityError}>{activity.error}</div>}
                    </div>
                  </div>
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
