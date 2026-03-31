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
} from '@ant-design/icons'
import { wsEditorService } from '@/services/wsEditorService'
import { editorApi } from '@/services/editorService'
import { chapterApi } from '@/services/chapterService'
import type {
  ServerMsg, Scope, ScopeType, DiffData,
  SessionCreatedMsg, SessionListMsg, ContentChunkMsg,
  ToolCallMsg,
  EditStartedMsg, EditAppliedMsg, EditAcceptedMsg, EditRejectedMsg,
  ErrorMsg,
  SessionLoadedMsg,
} from '@/services/wsEditorService'
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
}

interface ToolCallInfo {
  tool_name: string
  status: 'executing' | 'completed' | 'failed'
  result?: unknown
}

const SCOPE_OPTIONS: Array<{ value: ScopeType; label: string }> = [
  { value: 'novel', label: '整本小说' },
  { value: 'chapter', label: '单章' },
  { value: 'chapters', label: '章节范围' },
]

const MODEL_OPTIONS = [
  { value: 'deepseek-chat', label: 'DeepSeek Chat' },
  { value: 'deepseek-reasoner', label: 'DeepSeek Reasoner' },
]

const TOOL_GROUPS = [
  { key: 'editing', label: '编辑工具' },
  { key: 'memory', label: '记忆检索' },
  { key: 'consistency', label: '一致性检查' },
  { key: 'novel', label: '小说信息' },
]

function getScopeLabel(scope: Scope): string {
  if (scope.type === 'novel') return '整本小说'
  if (scope.type === 'chapter') return `第${scope.chapter_start}章`
  return `第${scope.chapter_start}-${scope.chapter_end}章`
}

export default function EditorPage() {
  const { novelId } = useParams<{ novelId: string }>()
  const navigate = useNavigate()
  const editorRef = useRef<Parameters<OnMount>[0] | null>(null)

  const [connected, setConnected] = useState(false)
  const [darkMode, setDarkMode] = useState(false)
  const [selectedModel, setSelectedModel] = useState('deepseek-chat')
  const [enabledTools, setEnabledTools] = useState<Set<string>>(new Set(['editing', 'memory', 'consistency', 'novel']))

  const [chapters, setChapters] = useState<ChapterInfo[]>([])
  const [selectedChapterId, setSelectedChapterId] = useState<number | null>(null)

  const [originalContent, setOriginalContent] = useState('')
  const [workingContent, setWorkingContent] = useState('')
  const [chapterWordCount, setChapterWordCount] = useState(0)
  const [editSessionId, setEditSessionId] = useState<string | null>(null)
  const [changeCount, setChangeCount] = useState(0)
  const [hasActiveEdit, setHasActiveEdit] = useState(false)
  const [showDiff, setShowDiff] = useState(false)
  const [diffData, setDiffData] = useState<DiffData | null>(null)
  const [isSaving, setIsSaving] = useState(false)

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

  const [createChapterModal, setCreateChapterModal] = useState(false)
  const [newChapterTitle, setNewChapterTitle] = useState('')
  const [newChapterNumber, setNewChapterNumber] = useState<number | null>(null)
  const [creatingChapter, setCreatingChapter] = useState(false)
  const pendingMessageRef = useRef<string | null>(null)
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const messagesEndRef = useRef<HTMLDivElement>(null)

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
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [chatMessages, toolCalls])

  const handleMsg = useCallback((msg: ServerMsg) => {
    console.log('[EditorPage] handleMsg:', msg.type)
    switch (msg.type) {
      case 'session_created': {
        const m = msg as SessionCreatedMsg
        setCurrentSessionId(m.session_id)
        setCurrentScope(m.scope)
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
          const history: ChatMessage[] = m.recent_messages.map((msg, i) => ({
            id: msg.message_id || `hist_${i}`,
            role: msg.role as 'user' | 'assistant',
            content: msg.content,
          }))
          setChatMessages(history)
        } else {
          setChatMessages([])
        }
        break
      }
      case 'chat_started': {
        setIsStreaming(true)
        setChatMessages(prev => [...prev, {
          id: `stream_${Date.now()}`,
          role: 'assistant',
          content: '',
          isStreaming: true,
        }])
        break
      }
      case 'content_chunk': {
        const m = msg as ContentChunkMsg
        setChatMessages(prev => {
          const updated = [...prev]
          const last = updated[updated.length - 1]
          if (last && last.isStreaming) {
            updated[updated.length - 1] = { ...last, content: last.content + m.chunk }
          }
          return updated
        })
        break
      }
      case 'chat_completed': {
        setIsStreaming(false)
        setChatMessages(prev => {
          const updated = [...prev]
          const last = updated[updated.length - 1]
          if (last && last.isStreaming) {
            updated[updated.length - 1] = { ...last, isStreaming: false, id: `msg_${Date.now()}` }
          }
          return updated
        })
        break
      }
      case 'tool_call': {
        const m = msg as ToolCallMsg
        setToolCalls(prev => {
          const idx = prev.findIndex(t => t.tool_name === m.tool_name && t.status === 'executing')
          if (idx >= 0) {
            return prev.map((t, i) => i === idx ? { ...t, status: m.status, result: m.result } : t)
          }
          return [...prev, { tool_name: m.tool_name, status: m.status, result: m.result }]
        })
        break
      }
      case 'edit_started': {
        const m = msg as EditStartedMsg
        setEditSessionId(m.edit_session_id)
        setOriginalContent(m.original_content)
        setWorkingContent(m.working_content)
        setChangeCount(m.change_count)
        setHasActiveEdit(true)
        setShowDiff(false)
        break
      }
      case 'edit_applied': {
        const m = msg as EditAppliedMsg
        setWorkingContent(m.working_content)
        setChangeCount(m.change_count)
        setDiffData(m.diff as DiffData)
        setShowDiff(true)
        break
      }
      case 'edit_accepted': {
        const m = msg as EditAcceptedMsg
        message.success(m.message)
        setHasActiveEdit(false)
        setEditSessionId(null)
        setShowDiff(false)
        setDiffData(null)
        setChangeCount(0)
        setChapterWordCount(m.word_count)
        if (selectedChapterId) {
          const ch = chapters.find(c => c.id === selectedChapterId)
          if (ch) {
            setChapters(prev => prev.map(c => c.id === selectedChapterId ? { ...c, word_count: m.word_count } : c))
          }
        }
        break
      }
      case 'edit_rejected': {
        const m = msg as EditRejectedMsg
        message.info(m.message)
        setWorkingContent(m.original_content)
        setHasActiveEdit(false)
        setEditSessionId(null)
        setShowDiff(false)
        setDiffData(null)
        setChangeCount(0)
        break
      }
      case 'error': {
        const m = msg as ErrorMsg
        message.error(m.error)
        break
      }
    }
  }, [selectedChapterId, chapters])

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

    try {
      const res = await editorApi.getChapterForEditor(chapterId)
      if (res.success) {
        const data = res.data
        setChapterWordCount(data.word_count)
        if (data.has_active_edit && data.working_content) {
          setOriginalContent(data.content)
          setWorkingContent(data.working_content)
          setEditSessionId(data.edit_session_id)
          setHasActiveEdit(true)
          setChangeCount(data.change_count)
        } else {
          setOriginalContent(data.content)
          setWorkingContent(data.content)
          setEditSessionId(null)
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
        setHasActiveEdit(false)
      }
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
    if (editSessionId) {
      wsEditorService.acceptEdit(editSessionId)
    }
  }

  const rejectEdit = () => {
    if (editSessionId) {
      wsEditorService.rejectEdit(editSessionId)
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

  const sendMessage = () => {
    if (!inputValue.trim()) return
    if (isStreaming) return

    const msg = inputValue.trim()
    const userMsg: ChatMessage = {
      id: `user_${Date.now()}`,
      role: 'user',
      content: msg,
    }
    setChatMessages(prev => [...prev, userMsg])
    setInputValue('')

    if (!currentSessionId) {
      const scope: Scope = { ...currentScope }
      if (scope.type === 'chapter' && scopeChapterStart) scope.chapter_start = scopeChapterStart
      else if (scope.type === 'chapters') {
        if (scopeChapterStart) scope.chapter_start = scopeChapterStart
        if (scopeChapterEnd) scope.chapter_end = scopeChapterEnd
      }
      pendingMessageRef.current = msg
      const sent = wsEditorService.createSession(scope, selectedModel)
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
        chapterApi.getChapters(parseInt(novelId), { page_size: 100 }).then(r => {
          if (r.success) setChapters(r.data.items || [])
        }).catch(() => {})
        selectChapter(res.data.id)
      }
    } catch {
      message.error('创建失败')
    } finally {
      setCreatingChapter(false)
    }
  }

  const selectedChapter = chapters.find(c => c.id === selectedChapterId)
  const theme = darkMode ? 'vs-dark' : 'light'

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
                  <button className={styles.newSessionBtn} onClick={() => {
                    const scope: Scope = { ...currentScope }
                    if (scope.type === 'chapter' && scopeChapterStart) scope.chapter_start = scopeChapterStart
                    else if (scope.type === 'chapters') {
                      if (scopeChapterStart) scope.chapter_start = scopeChapterStart
                      if (scopeChapterEnd) scope.chapter_end = scopeChapterEnd
                    }
                    wsEditorService.createSession(scope, selectedModel)
                  }}>
                    <PlusOutlined /> 新建对话
                  </button>
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
          </div>
          <button
            className={`${styles.collapseBtn} ${sidebarCollapsed ? styles.collapseBtnCollapsed : ''}`}
            onClick={() => setSidebarCollapsed(!sidebarCollapsed)}
          >
            {sidebarCollapsed ? <RightOutlined /> : <LeftOutlined />}
          </button>
        </div>

        <div className={styles.centerEditor}>
          {selectedChapter ? (
            <>
              <div className={styles.editorHeader}>
                <span className={styles.editorFileName}>
                  第{selectedChapter.chapter_number}章 {selectedChapter.title}
                </span>
                <span className={styles.editorWordCount}>
                  {chapterWordCount.toLocaleString()} 字
                </span>
                {isSaving && (
                  <span style={{ fontSize: 11, color: '#faad14' }}>
                    <SaveOutlined style={{ marginRight: 2 }} /> 保存中...
                  </span>
                )}
                {hasActiveEdit && changeCount > 0 && (
                  <div className={styles.diffActions}>
                    <span className={styles.diffSummary}>
                      {changeCount} 处改动
                      {diffData && (
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
              <div className={styles.editorContainer}>
                {showDiff && hasActiveEdit ? (
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
                      fontSize: 14,
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
                      readOnly: false,
                      minimap: { enabled: false },
                      lineNumbers: 'on',
                      scrollBeyondLastLine: false,
                      fontSize: 14,
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
          <div className={styles.chatScope}>
            <div className={styles.scopeLabel}>对话作用域</div>
            <div className={styles.scopeRow}>
              <Select
                size="small"
                value={currentScope.type}
                onChange={handleScopeTypeChange}
                style={{ flex: 1 }}
                popupMatchSelectWidth={false}
                variant="borderless"
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
                  style={{ width: 120 }}
                  placeholder="选择章节"
                  variant="borderless"
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
                    style={{ width: 90 }}
                    placeholder="起始"
                    variant="borderless"
                    options={chapters.map(ch => ({
                      value: ch.chapter_number,
                      label: `第${ch.chapter_number}章`,
                    }))}
                  />
                  <span className={styles.scopeDash}>-</span>
                  <Select
                    size="small"
                    value={scopeChapterEnd}
                    onChange={v => setScopeChapterEnd(v)}
                    style={{ width: 90 }}
                    placeholder="结束"
                    variant="borderless"
                    options={chapters.map(ch => ({
                      value: ch.chapter_number,
                      label: `第${ch.chapter_number}章`,
                    }))}
                  />
                </>
              )}
            </div>
            <div className={styles.scopeRow} style={{ marginTop: 4 }}>
              <Select
                size="small"
                value={selectedModel}
                onChange={setSelectedModel}
                style={{ flex: 1 }}
                popupMatchSelectWidth={false}
                variant="borderless"
                options={MODEL_OPTIONS}
              />
            </div>
          </div>

          <div className={styles.chatMessages}>
            {chatMessages.length === 0 && !isStreaming && (
              <div className={styles.emptyChat}>
                <MessageOutlined className={styles.emptyChatIcon} />
                <div>输入消息开始对话</div>
                <div style={{ fontSize: 12 }}>AI 可以帮你修改章节内容</div>
              </div>
            )}
            {chatMessages.map(msg => (
              <div
                key={msg.id}
                className={`${styles.chatMsg} ${msg.role === 'user' ? styles.chatMsgUser : msg.isStreaming ? styles.chatMsgStreaming : styles.chatMsgAssistant}`}
              >
                {msg.content}
              </div>
            ))}
            {toolCalls.map((tc, i) => (
              <div key={`tool_${i}`} className={styles.toolCallCard}>
                <div className={styles.toolCallName}>
                  <SettingOutlined style={{ marginRight: 4 }} />
                  {tc.tool_name}
                </div>
                <div className={styles.toolCallStatus}>
                  {tc.status === 'executing' && (
                    <span className={styles.toolCallExecuting}>
                      <LoadingOutlined spin style={{ marginRight: 4 }} /> 执行中...
                    </span>
                  )}
                  {tc.status === 'completed' && (
                    <span className={styles.toolCallCompleted}>
                      <CheckCircleOutlined style={{ marginRight: 4 }} /> 已完成
                    </span>
                  )}
                  {tc.status === 'failed' && (
                    <span className={styles.toolCallFailed}>
                      <CloseCircleOutlined style={{ marginRight: 4 }} /> 失败
                    </span>
                  )}
                </div>
              </div>
            ))}
            <div ref={messagesEndRef} />
          </div>

          <div className={styles.chatInput}>
            <div className={styles.toolBar}>
              {TOOL_GROUPS.map(group => (
                <button
                  key={group.key}
                  className={`${styles.toolTag} ${enabledTools.has(group.key) ? styles.toolTagActive : ''}`}
                  onClick={() => {
                    setEnabledTools(prev => {
                      const next = new Set(prev)
                      if (next.has(group.key)) next.delete(group.key)
                      else next.add(group.key)
                      return next
                    })
                  }}
                >
                  {enabledTools.has(group.key) ? <CheckOutlined style={{ fontSize: 10, marginRight: 2 }} /> : null}
                  {group.label}
                </button>
              ))}
            </div>
            <div className={styles.inputRow}>
              <textarea
                className={styles.chatTextArea}
                value={inputValue}
                onChange={e => setInputValue(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="输入消息... (Enter 发送)"
                disabled={isStreaming}
                rows={1}
                onInput={e => {
                  const target = e.target as HTMLTextAreaElement
                  target.style.height = 'auto'
                  target.style.height = Math.min(target.scrollHeight, 120) + 'px'
                }}
              />
              <button
                className={styles.sendBtn}
                onClick={sendMessage}
                disabled={!inputValue.trim() || isStreaming}
              >
                <ArrowLeftOutlined style={{ transform: 'rotate(-45deg)' }} />
              </button>
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
