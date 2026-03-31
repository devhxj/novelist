import { useState, useEffect, useRef, useCallback } from 'react'
import { Card, Input, Button, Select, message, Spin, Typography, Space, Tag, Progress, Empty, List, Popconfirm, Tooltip, Modal, Form, Divider } from 'antd'
import { SendOutlined, PlusOutlined, DeleteOutlined, ClearOutlined, CompressOutlined, EditOutlined, FormOutlined } from '@ant-design/icons'
import { useParams } from 'react-router-dom'
import { wsGenerationService } from '@/services/wsGenerationService'
import { sessionApi } from '@/services/sessionService'
import { generationApi } from '@/services/generationService'
import { chapterApi } from '@/services/chapterService'
import type { WSMessage, LLMModel } from '@/services/wsGenerationService'
import type { Session, SessionMessage, SessionLevel, NovelContext, ChapterContext, UpdateNovelContextRequest, UpdateChapterContextRequest } from '@/services/sessionService'
import type { ModelOption } from '@/services/generationService'
import { getErrorMessage } from '@/types/error'

const { Option } = Select
const { Text } = Typography
const { TextArea } = Input

interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  timestamp: Date
}

interface CreateSessionModalProps {
  visible: boolean
  novelId?: number
  chapters: { id: number; chapter_number: number; title: string }[]
  onCancel: () => void
  onCreate: (level: SessionLevel, chapterNumber?: number, model?: LLMModel) => void
}

function CreateSessionModal({ visible, novelId, chapters, onCancel, onCreate }: CreateSessionModalProps) {
  const [form] = Form.useForm()
  const [level, setLevel] = useState<SessionLevel>('novel')

  useEffect(() => {
    form.setFieldsValue({ level: 'novel', model: 'deepseek-chat' })
    setLevel('novel')
  }, [visible, form])

  const handleLevelChange = (value: SessionLevel) => {
    setLevel(value)
    if (value !== 'chapter') {
      form.setFieldsValue({ chapter_number: undefined })
    }
  }

  return (
    <Modal
      title="创建新会话"
      open={visible}
      onCancel={onCancel}
      onOk={() => form.submit()}
      okText="创建"
      cancelText="取消"
    >
      <Form
        form={form}
        layout="vertical"
        onFinish={(values) => {
          onCreate(values.level, values.chapter_number, values.model)
          onCancel()
        }}
        initialValues={{ level: 'novel', model: 'deepseek-chat' }}
      >
        <Form.Item name="level" label="会话层级" rules={[{ required: true }]}>
          <Select onChange={handleLevelChange}>
            <Option value="novel">小说级 - 全局讨论、大纲生成</Option>
            {novelId && <Option value="chapter">章节级 - 章节生成、修改</Option>}
            <Option value="free">自由对话 - 通用问答</Option>
          </Select>
        </Form.Item>

        {level === 'chapter' && (
          <Form.Item name="chapter_number" label="选择章节" rules={[{ required: true }]}>
            <Select placeholder="选择章节">
              {chapters.map(ch => (
                <Option key={ch.id} value={ch.chapter_number}>
                  第{ch.chapter_number}章 {ch.title}
                </Option>
              ))}
            </Select>
          </Form.Item>
        )}

        <Form.Item name="model" label="模型">
          <Select>
            <Option value="deepseek-chat">DeepSeek Chat - 通用对话</Option>
            <Option value="deepseek-reasoner">DeepSeek Reasoner - 推理增强</Option>
          </Select>
        </Form.Item>
      </Form>
    </Modal>
  )
}

interface ContextEditorModalProps {
  visible: boolean
  session: Session | null
  onCancel: () => void
  onSave: (novelContext?: NovelContext, chapterContext?: ChapterContext) => void
}

function ContextEditorModal({ visible, session, onCancel, onSave }: ContextEditorModalProps) {
  const [form] = Form.useForm()

  useEffect(() => {
    if (session && visible) {
      if (session.level === 'novel' && session.novel_context) {
        form.setFieldsValue(session.novel_context)
      } else if (session.level === 'chapter' && session.chapter_context) {
        form.setFieldsValue({
          ...session.chapter_context,
          key_events: session.chapter_context.key_events?.join('\n'),
          focus_characters: session.chapter_context.focus_characters?.join('\n'),
        })
      }
    }
  }, [session, visible, form])

  const handleSave = () => {
    form.submit()
  }

  const onFinish = (values: Record<string, unknown>) => {
    if (session?.level === 'novel') {
      onSave(values as NovelContext, undefined)
    } else if (session?.level === 'chapter') {
      const chapterContext: ChapterContext = {
        chapter_number: values.chapter_number as number,
        chapter_title: values.chapter_title as string,
        previous_summary: values.previous_summary as string,
        current_outline: values.current_outline as string,
        key_events: (values.key_events as string)?.split('\n').filter(e => e.trim()),
        focus_characters: (values.focus_characters as string)?.split('\n').filter(c => c.trim()),
      }
      onSave(undefined, chapterContext)
    }
    onCancel()
  }

  if (!session) return null

  return (
    <Modal
      title={`编辑${session.level === 'novel' ? '小说' : '章节'}上下文`}
      open={visible}
      onCancel={onCancel}
      onOk={handleSave}
      okText="保存"
      cancelText="取消"
      width={600}
    >
      <Form form={form} layout="vertical" onFinish={onFinish}>
        {session.level === 'novel' && (
          <>
            <Form.Item name="title" label="小说标题">
              <Input placeholder="小说标题" />
            </Form.Item>
            <Form.Item name="description" label="简介">
              <TextArea rows={2} placeholder="小说简介" />
            </Form.Item>
            <Form.Item name="genre" label="类型">
              <Input placeholder="如：玄幻、都市、科幻" />
            </Form.Item>
            <Form.Item name="outline" label="故事大纲">
              <TextArea rows={4} placeholder="故事大纲" />
            </Form.Item>
            <Form.Item name="world_setting" label="世界观设定">
              <TextArea rows={3} placeholder="世界观设定" />
            </Form.Item>
            <Form.Item name="characters_summary" label="角色摘要">
              <TextArea rows={2} placeholder="主要角色信息" />
            </Form.Item>
            <Form.Item name="main_plot" label="主线情节">
              <TextArea rows={3} placeholder="主线情节" />
            </Form.Item>
          </>
        )}
        {session.level === 'chapter' && (
          <>
            <Form.Item name="chapter_number" label="章节编号">
              <Input type="number" disabled />
            </Form.Item>
            <Form.Item name="chapter_title" label="章节标题">
              <Input placeholder="章节标题" />
            </Form.Item>
            <Form.Item name="previous_summary" label="前文摘要">
              <TextArea rows={3} placeholder="前文摘要" />
            </Form.Item>
            <Form.Item name="current_outline" label="本章大纲">
              <TextArea rows={4} placeholder="1. 开场&#10;2. 发展&#10;3. 高潮&#10;4. 结尾" />
            </Form.Item>
            <Form.Item name="key_events" label="关键事件" extra="每行一个事件">
              <TextArea rows={3} placeholder="主角遭遇强敌&#10;展示新能力" />
            </Form.Item>
            <Form.Item name="focus_characters" label="重点角色" extra="每行一个角色名">
              <TextArea rows={2} placeholder="张三&#10;李四" />
            </Form.Item>
          </>
        )}
      </Form>
    </Modal>
  )
}

function ChatPage() {
  const { novelId } = useParams<{ novelId: string }>()
  const messagesEndRef = useRef<HTMLDivElement>(null)

  const [models, setModels] = useState<ModelOption[]>([])
  const [chapters, setChapters] = useState<{ id: number; chapter_number: number; title: string }[]>([])
  const [isConnected, setIsConnected] = useState(false)
  const [currentSession, setCurrentSession] = useState<Session | null>(null)
  const [sessions, setSessions] = useState<Session[]>([])
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [inputValue, setInputValue] = useState('')
  const [selectedModel, setSelectedModel] = useState<LLMModel>('deepseek-chat')
  const [temperature, setTemperature] = useState(0.7)
  const [isStreaming, setIsStreaming] = useState(false)
  const [streamingContent, setStreamingContent] = useState('')
  const [createModalVisible, setCreateModalVisible] = useState(false)
  const [contextModalVisible, setContextModalVisible] = useState(false)
  const [editingTitleSessionId, setEditingTitleSessionId] = useState<string | null>(null)
  const [editingTitleValue, setEditingTitleValue] = useState('')

  useEffect(() => {
    loadModels()
    loadSessions()
    loadChapters()
    connectWebSocket()
    return () => {
      wsGenerationService.disconnect()
    }
  }, [novelId])

  useEffect(() => {
    scrollToBottom()
  }, [messages, streamingContent])

  const scrollToBottom = () => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }

  const loadModels = async () => {
    try {
      const response = await generationApi.getModels()
      if (response.success) {
        setModels(response.data.models)
      }
    } catch (error) {
      console.error('Failed to load models:', error)
    }
  }

  const loadChapters = async () => {
    if (!novelId) return
    try {
      const response = await chapterApi.getChapters(parseInt(novelId), {})
      if (response.success) {
        setChapters(response.data.items || [])
      }
    } catch (error) {
      console.error('Failed to load chapters:', error)
    }
  }

  const loadSessions = async () => {
    try {
      const params: { novel_id?: number; page_size: number } = { page_size: 20 }
      if (novelId) {
        params.novel_id = parseInt(novelId)
      }
      const response = await sessionApi.list(params)
      if (response.success) {
        setSessions(response.data.items || [])
      }
    } catch (error) {
      console.error('Failed to load sessions:', error)
    }
  }

  const connectWebSocket = async () => {
    try {
      await wsGenerationService.connect(novelId ? parseInt(novelId) : undefined)
      setIsConnected(true)
      wsGenerationService.onMessage(handleWSMessage)
    } catch (error) {
      console.error('WebSocket connection failed:', error)
      // 不显示 warning，让重连机制处理
    }
  }

  const handleWSMessage = useCallback((msg: WSMessage) => {
    switch (msg.type) {
      case 'session_created':
        setCurrentSession({
          id: msg.session_id,
          session_id: msg.session_id,
          level: msg.level,
          display_name: msg.display_name,
          novel_id: msg.novel_id,
          chapter_number: msg.chapter_number,
          model: 'deepseek-chat',
          stats: { message_count: 0, token_count: 0, context_window: 131072, usage_ratio: msg.context_usage, should_compress: false },
          created_at: new Date().toISOString(),
          updated_at: new Date().toISOString(),
          expires_at: new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(),
        })
        message.success(`会话创建成功: ${msg.display_name}`)
        loadSessions()
        break

      case 'session_loaded':
        message.success(`会话加载成功，共${msg.message_count}条消息`)
        break

      case 'chat_started':
        setIsStreaming(true)
        setStreamingContent('')
        break

      case 'chat_chunk':
        setStreamingContent(prev => prev + msg.chunk)
        break

      case 'chat_completed':
        setIsStreaming(false)
        setStreamingContent('')
        setMessages(prev => {
          const newMessages = [
            ...prev,
            {
              id: msg.message_id,
              role: 'assistant' as const,
              content: msg.content,
              timestamp: new Date(),
            },
          ]
          if (prev.length === 0 && currentSession && !currentSession.title) {
            sessionApi.autoGenerateTitle(currentSession.id)
              .then(titleResponse => {
                if (titleResponse.success) {
                  setCurrentSession(s => s ? { ...s, title: titleResponse.data.title } : null)
                  setSessions(sessions => sessions.map(s => 
                    s.id === currentSession.id ? { ...s, title: titleResponse.data.title } : s
                  ))
                }
              })
              .catch(() => console.error('Failed to auto-generate title'))
          }
          return newMessages
        })
        break

      case 'chat_failed':
        setIsStreaming(false)
        setStreamingContent('')
        message.error(`对话失败: ${msg.error}`)
        break

      case 'generation_rejected':
        message.warning(`任务被拒绝: ${msg.reason}`)
        break
    }
  }, [currentSession])

  const createSession = async (level: SessionLevel, chapterNumber?: number, model?: LLMModel) => {
    try {
      if (isConnected) {
        wsGenerationService.createSession(level, novelId ? parseInt(novelId) : undefined, chapterNumber, model)
      } else {
        const response = await sessionApi.create({
          level,
          novel_id: novelId ? parseInt(novelId) : undefined,
          chapter_number: chapterNumber,
          model,
        })
        if (response.success) {
          setCurrentSession(response.data)
          setMessages([])
          message.success('会话创建成功')
          loadSessions()
        }
      }
    } catch (error) {
      message.error(getErrorMessage(error))
    }
  }

  const loadSession = async (sessionId: string) => {
    try {
      if (isConnected) {
        wsGenerationService.loadSession(sessionId)
      }
      const [sessionRes, messagesRes] = await Promise.all([
        sessionApi.get(sessionId),
        sessionApi.getMessages(sessionId, { page_size: 100 }),
      ])
      if (sessionRes.success) {
        setCurrentSession(sessionRes.data)
      }
      if (messagesRes.success) {
        const chatMessages: ChatMessage[] = (messagesRes.data.items || [])
          .filter((m: SessionMessage) => m.role === 'user' || m.role === 'assistant')
          .map((m: SessionMessage) => ({
            id: m.id,
            role: m.role as 'user' | 'assistant',
            content: m.content,
            timestamp: new Date(m.created_at),
          }))
        setMessages(chatMessages)
      }
    } catch (error) {
      message.error(getErrorMessage(error))
    }
  }

  const deleteSession = async (sessionId: string) => {
    try {
      const response = await sessionApi.delete(sessionId)
      if (response.success) {
        message.success('会话已删除')
        if (currentSession?.id === sessionId) {
          setCurrentSession(null)
          setMessages([])
        }
        loadSessions()
      }
    } catch (error) {
      message.error(getErrorMessage(error))
    }
  }

  const clearSession = async () => {
    if (!currentSession) return
    try {
      const response = await sessionApi.clear(currentSession.id)
      if (response.success) {
        message.success('会话已清空')
        setMessages([])
      }
    } catch (error) {
      message.error(getErrorMessage(error))
    }
  }

  const compressSession = async () => {
    if (!currentSession) return
    try {
      const response = await sessionApi.compress(currentSession.id)
      if (response.success) {
        message.success(`压缩完成，移除了${response.data.messages_removed}条消息`)
        loadSession(currentSession.id)
      }
    } catch (error) {
      message.error(getErrorMessage(error))
    }
  }

  const saveContext = async (novelContext?: NovelContext, chapterContext?: ChapterContext) => {
    if (!currentSession) return
    try {
      if (novelContext) {
        await sessionApi.updateNovelContext(currentSession.id, novelContext as UpdateNovelContextRequest)
        message.success('小说上下文已更新')
      }
      if (chapterContext) {
        await sessionApi.updateChapterContext(currentSession.id, chapterContext as UpdateChapterContextRequest)
        message.success('章节上下文已更新')
      }
      loadSession(currentSession.id)
    } catch (error) {
      message.error(getErrorMessage(error))
    }
  }

  const getSessionDisplayName = (session: Session): string => {
    if (session.title) return session.title
    return session.display_name
  }

  const startEditTitle = (session: Session, e: React.MouseEvent) => {
    e.stopPropagation()
    setEditingTitleSessionId(session.id)
    setEditingTitleValue(session.title || session.display_name)
  }

  const cancelEditTitle = () => {
    setEditingTitleSessionId(null)
    setEditingTitleValue('')
  }

  const saveTitle = async (sessionId: string) => {
    if (!editingTitleValue.trim()) {
      message.warning('标题不能为空')
      return
    }
    try {
      const response = await sessionApi.updateTitle(sessionId, { title: editingTitleValue.trim() })
      if (response.success) {
        message.success('标题已更新')
        setSessions(prev => prev.map(s => 
          s.id === sessionId ? { ...s, title: editingTitleValue.trim() } : s
        ))
        if (currentSession?.id === sessionId) {
          setCurrentSession(prev => prev ? { ...prev, title: editingTitleValue.trim() } : null)
        }
        setEditingTitleSessionId(null)
        setEditingTitleValue('')
      }
    } catch (error) {
      message.error(getErrorMessage(error))
    }
  }

  const handleTitleKeyPress = (e: React.KeyboardEvent, sessionId: string) => {
    if (e.key === 'Enter') {
      saveTitle(sessionId)
    } else if (e.key === 'Escape') {
      cancelEditTitle()
    }
  }

  const sendMessage = async () => {
    if (!inputValue.trim()) return
    if (!currentSession && !isConnected) {
      message.warning('请先创建会话')
      return
    }

    const userMessage: ChatMessage = {
      id: `temp_${Date.now()}`,
      role: 'user',
      content: inputValue.trim(),
      timestamp: new Date(),
    }
    setMessages(prev => [...prev, userMessage])
    setInputValue('')

    const shouldAutoGenerateTitle = currentSession && !currentSession.title && messages.length === 0

    try {
      if (isConnected) {
        wsGenerationService.chat(inputValue.trim(), selectedModel, temperature)
      } else if (currentSession) {
        const response = await sessionApi.chat(currentSession.id, {
          message: inputValue.trim(),
          model: selectedModel,
          temperature,
        })
        if (response.success) {
          setMessages(prev => [
            ...prev,
            {
              id: response.data.message_id,
              role: 'assistant',
              content: response.data.content,
              timestamp: new Date(),
            },
          ])
          if (shouldAutoGenerateTitle) {
            try {
              const titleResponse = await sessionApi.autoGenerateTitle(currentSession.id)
              if (titleResponse.success) {
                setCurrentSession(prev => prev ? { ...prev, title: titleResponse.data.title } : null)
                setSessions(prev => prev.map(s => 
                  s.id === currentSession.id ? { ...s, title: titleResponse.data.title } : s
                ))
              }
            } catch {
              console.error('Failed to auto-generate title')
            }
          }
        }
      }
    } catch (error) {
      message.error(getErrorMessage(error))
      setMessages(prev => prev.filter(m => m.id !== userMessage.id))
    }
  }

  const handleKeyPress = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      sendMessage()
    }
  }

  const getLevelTag = (level: SessionLevel) => {
    const config: Record<SessionLevel, { color: string; text: string }> = {
      novel: { color: 'blue', text: '小说' },
      chapter: { color: 'green', text: '章节' },
      free: { color: 'default', text: '自由' },
    }
    return <Tag color={config[level].color}>{config[level].text}</Tag>
  }

  const getUsageColor = (ratio: number) => {
    if (ratio >= 80) return '#ff4d4f'
    if (ratio >= 60) return '#faad14'
    return '#52c41a'
  }

  const groupedSessions = sessions.reduce((acc, session) => {
    const key = session.level
    if (!acc[key]) acc[key] = []
    acc[key].push(session)
    return acc
  }, {} as Record<SessionLevel, Session[]>)

  return (
    <div style={{ height: 'calc(100vh - 120px)', display: 'flex', gap: 16 }}>
      <Card
        title="会话列表"
        extra={
          <Button
            type="primary"
            icon={<PlusOutlined />}
            onClick={() => setCreateModalVisible(true)}
            size="small"
          >
            新建
          </Button>
        }
        style={{ width: 300, display: 'flex', flexDirection: 'column' }}
        styles={{ body: { flex: 1, overflow: 'auto', padding: 8 } }}
      >
        {Object.entries(groupedSessions).map(([level, levelSessions]) => (
          <div key={level} style={{ marginBottom: 16 }}>
            <Divider style={{ margin: '8px 0' }}>{getLevelTag(level as SessionLevel)}</Divider>
            <List
              dataSource={levelSessions}
              renderItem={(session) => (
                <List.Item
                  style={{
                    padding: '8px 12px',
                    cursor: 'pointer',
                    backgroundColor: currentSession?.id === session.id ? '#e6f7ff' : 'transparent',
                    borderRadius: 4,
                  }}
                  onClick={() => loadSession(session.id)}
                >
                  <div style={{ flex: 1, minWidth: 0 }}>
                    {editingTitleSessionId === session.id ? (
                      <Input
                        size="small"
                        value={editingTitleValue}
                        onChange={(e) => setEditingTitleValue(e.target.value)}
                        onKeyPress={(e) => handleTitleKeyPress(e, session.id)}
                        onBlur={() => saveTitle(session.id)}
                        autoFocus
                        onClick={(e) => e.stopPropagation()}
                        style={{ marginBottom: 4 }}
                      />
                    ) : (
                      <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                        <Text ellipsis style={{ flex: 1 }}>
                          {getSessionDisplayName(session)}
                        </Text>
                        <Tooltip title="编辑标题">
                          <Button
                            type="text"
                            size="small"
                            icon={<FormOutlined />}
                            onClick={(e) => startEditTitle(session, e)}
                            style={{ padding: '0 4px' }}
                          />
                        </Tooltip>
                      </div>
                    )}
                    <Space size="small">
                      <Text type="secondary" style={{ fontSize: 12 }}>
                        {session.stats.message_count}条
                      </Text>
                      <Progress
                        percent={session.stats.usage_ratio}
                        size="small"
                        style={{ width: 60 }}
                        strokeColor={getUsageColor(session.stats.usage_ratio)}
                        showInfo={false}
                      />
                    </Space>
                  </div>
                  <Popconfirm
                    title="确定删除此会话？"
                    onConfirm={(e) => {
                      e?.stopPropagation()
                      deleteSession(session.id)
                    }}
                    onCancel={(e) => e?.stopPropagation()}
                  >
                    <Button
                      type="text"
                      danger
                      size="small"
                      icon={<DeleteOutlined />}
                      onClick={(e) => e.stopPropagation()}
                    />
                  </Popconfirm>
                </List.Item>
              )}
            />
          </div>
        ))}
        {sessions.length === 0 && (
          <Empty description="暂无会话" image={Empty.PRESENTED_IMAGE_SIMPLE} />
        )}
      </Card>

      <Card
        style={{ flex: 1, display: 'flex', flexDirection: 'column' }}
        styles={{ body: { flex: 1, display: 'flex', flexDirection: 'column', padding: 0 } }}
        title={
          currentSession ? (
            <Space>
              {getLevelTag(currentSession.level)}
              <Text>{getSessionDisplayName(currentSession)}</Text>
              <Tooltip title={`Token使用率: ${currentSession.stats.usage_ratio.toFixed(1)}%`}>
                <Progress
                  percent={currentSession.stats.usage_ratio}
                  size="small"
                  style={{ width: 100 }}
                  strokeColor={getUsageColor(currentSession.stats.usage_ratio)}
                  showInfo={false}
                />
              </Tooltip>
              {currentSession.stats.should_compress && (
                <Tag color="warning">建议压缩</Tag>
              )}
            </Space>
          ) : (
            'AI创作助手'
          )
        }
        extra={
          currentSession && (
            <Space>
              {currentSession.level !== 'free' && (
                <Tooltip title="编辑上下文">
                  <Button icon={<EditOutlined />} onClick={() => setContextModalVisible(true)} size="small" />
                </Tooltip>
              )}
              <Tooltip title="清空消息">
                <Button icon={<ClearOutlined />} onClick={clearSession} size="small" />
              </Tooltip>
              <Tooltip title="压缩上下文">
                <Button icon={<CompressOutlined />} onClick={compressSession} size="small" />
              </Tooltip>
            </Space>
          )
        }
      >
        {!currentSession ? (
          <div style={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <Empty
              description="选择或创建一个会话开始对话"
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            >
              <Button type="primary" onClick={() => setCreateModalVisible(true)}>
                创建新会话
              </Button>
            </Empty>
          </div>
        ) : (
          <>
            <div style={{ flex: 1, overflow: 'auto', padding: 16 }}>
              {messages.map((msg) => (
                <div
                  key={msg.id}
                  style={{
                    marginBottom: 16,
                    display: 'flex',
                    justifyContent: msg.role === 'user' ? 'flex-end' : 'flex-start',
                  }}
                >
                  <Card
                    size="small"
                    style={{
                      maxWidth: '80%',
                      backgroundColor: msg.role === 'user' ? '#e6f7ff' : '#f5f5f5',
                    }}
                  >
                    <Text style={{ whiteSpace: 'pre-wrap' }}>{msg.content}</Text>
                  </Card>
                </div>
              ))}
              {isStreaming && streamingContent && (
                <div style={{ marginBottom: 16, display: 'flex', justifyContent: 'flex-start' }}>
                  <Card size="small" style={{ maxWidth: '80%', backgroundColor: '#f5f5f5' }}>
                    <Text style={{ whiteSpace: 'pre-wrap' }}>{streamingContent}</Text>
                  </Card>
                </div>
              )}
              {isStreaming && !streamingContent && (
                <div style={{ marginBottom: 16, display: 'flex', justifyContent: 'flex-start' }}>
                  <Spin size="small" />
                </div>
              )}
              <div ref={messagesEndRef} />
            </div>

            <div style={{ padding: 16, borderTop: '1px solid #f0f0f0' }}>
              <Space style={{ marginBottom: 8 }}>
                <Select
                  value={selectedModel}
                  onChange={setSelectedModel}
                  style={{ width: 180 }}
                  size="small"
                >
                  {models.map((m) => (
                    <Option key={m.value} value={m.value}>
                      {m.label}
                    </Option>
                  ))}
                </Select>
                <Text type="secondary">Temperature:</Text>
                <Input
                  type="number"
                  value={temperature}
                  onChange={(e) => setTemperature(parseFloat(e.target.value) || 0.7)}
                  min={0}
                  max={2}
                  step={0.1}
                  style={{ width: 80 }}
                  size="small"
                />
              </Space>
              <div style={{ display: 'flex', gap: 8 }}>
                <TextArea
                  value={inputValue}
                  onChange={(e) => setInputValue(e.target.value)}
                  onKeyPress={handleKeyPress}
                  placeholder="输入消息... (Enter发送, Shift+Enter换行)"
                  autoSize={{ minRows: 1, maxRows: 4 }}
                  style={{ flex: 1 }}
                  disabled={isStreaming}
                />
                <Button
                  type="primary"
                  icon={<SendOutlined />}
                  onClick={sendMessage}
                  loading={isStreaming}
                  disabled={!inputValue.trim() || isStreaming}
                >
                  发送
                </Button>
              </div>
            </div>
          </>
        )}
      </Card>

      <CreateSessionModal
        visible={createModalVisible}
        novelId={novelId ? parseInt(novelId) : undefined}
        chapters={chapters}
        onCancel={() => setCreateModalVisible(false)}
        onCreate={createSession}
      />

      <ContextEditorModal
        visible={contextModalVisible}
        session={currentSession}
        onCancel={() => setContextModalVisible(false)}
        onSave={saveContext}
      />
    </div>
  )
}

export default ChatPage
