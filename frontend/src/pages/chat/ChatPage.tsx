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

type TimelineItemType = 'user' | 'assistant' | 'tool' | 'assistant_final'

interface ToolCallInfo {
  tool_name: string
  status: 'executing' | 'completed' | 'failed' | 'rejected'
  tool_id?: string
  error?: string
  timestamp: string
}

interface TimelineItem {
  id: string
  type: TimelineItemType
  content?: string
  tool_calls?: ToolCallInfo[]
  timestamp: Date
  parent_id?: string
  task_id?: string
}

function scopeToLevel(scope?: { type?: string }): SessionLevel {
  if (scope?.type === 'chapter') return 'chapter'
  if (scope?.type === 'novel') return 'novel'
  return 'free'
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
  const [timelineItems, setTimelineItems] = useState<TimelineItem[]>([])
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
  }, [messages, streamingContent, timelineItems])

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
          level: scopeToLevel(msg.scope),
          display_name: msg.display_name,
          title: msg.title,
          novel_id: novelId ? parseInt(novelId) : undefined,
          chapter_number: msg.scope?.chapter_start,
          model: (msg.model as LLMModel) || 'deepseek-chat',
          stats: { message_count: 0, token_count: 0, context_window: 131072, usage_ratio: 0, should_compress: false },
          created_at: new Date().toISOString(),
          updated_at: new Date().toISOString(),
          expires_at: new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(),
        })
        message.success(`会话创建成功: ${msg.title || msg.display_name}`)
        loadSessions()
        break

      case 'session_loaded':
        setCurrentSession(prev => prev ? {
          ...prev,
          id: msg.session_id,
          session_id: msg.session_id,
          level: scopeToLevel(msg.scope),
          display_name: msg.display_name,
          title: msg.title || prev.title,
          chapter_number: msg.scope?.chapter_start,
        } : prev)
        if (msg.recent_messages) {
          const history = msg.recent_messages
            .filter(item => item.role === 'user' || item.role === 'assistant')
            .map((item, index) => ({
              id: item.message_id || `ws_hist_${index}`,
              role: item.role as 'user' | 'assistant',
              content: item.content,
              timestamp: item.created_at ? new Date(item.created_at) : new Date(),
            }))
          setMessages(history)
          setTimelineItems(history.map(item => ({
            id: item.id,
            type: item.role === 'user' ? 'user' : 'assistant_final',
            content: item.content,
            timestamp: item.timestamp,
            task_id: item.id,
          })))
        }
        break

      case 'chat_started':
        setIsStreaming(true)
        setStreamingContent('')
        // 创建AI初始回复时间线项目
        const taskId = msg.task_id
        setTimelineItems(prev => {
          // 检查是否已经有AI回复项目（用于同一任务）
          const existingAiIndex = prev.findIndex(item => item.task_id === taskId && item.type === 'assistant');
          if (existingAiIndex >= 0) {
            return prev;
          }
          const newItem: TimelineItem = {
            id: `assistant_${taskId}`,
            type: 'assistant',
            content: '思考中...',
            task_id: taskId,
            timestamp: new Date()
          };
          return [...prev, newItem];
        });
        break

      case 'content_chunk':
        setTimelineItems(prev => {
          return prev.map(item => {
            if (item.task_id === msg.task_id && item.type === 'assistant') {
              return {
                ...item,
                content: (item.content || '') + msg.chunk
              };
            }
            return item;
          });
        });
        setStreamingContent(prev => prev + msg.chunk)
        break

      case 'chat_completed':
        setIsStreaming(false)
        setStreamingContent('')
        let completedContent = ''
        setTimelineItems(prev => {
          const updated = prev.map(item => {
            if (item.task_id === msg.task_id && item.type === 'assistant') {
              completedContent = item.content || ''
              return {
                ...item,
                type: 'assistant_final' as TimelineItemType
              };
            }
            return item;
          });
          return updated;
        });
        setMessages(prev => {
          if (!completedContent.trim()) return prev
          return [
            ...prev,
            {
              id: `assistant_${msg.task_id || Date.now()}`,
              role: 'assistant',
              content: completedContent,
              timestamp: new Date(),
            },
          ]
        })
        loadSessions()
        break

      case 'chat_failed':
        setIsStreaming(false)
        setStreamingContent('')
        message.error(`对话失败: ${msg.error}`)
        break

      case 'tool_call':
        // 处理工具调用消息
        console.log('收到工具调用消息:', msg);
        setTimelineItems(prev => {
          // 查找是否有对应的任务时间线项目
          const existingIndex = prev.findIndex(item => item.task_id === msg.task_id && item.type === 'tool');

          if (existingIndex >= 0) {
            // 更新现有的工具调用项目
            const updated = [...prev];
            const existingItem = updated[existingIndex];

            // 确保tool_calls数组存在
            if (!existingItem.tool_calls) {
              existingItem.tool_calls = [];
            }

            // 查找是否有相同的工具调用（通过tool_id或tool_name）
            let toolCallIndex = -1;
            if (msg.tool_id) {
              toolCallIndex = existingItem.tool_calls.findIndex(tc => tc.tool_id === msg.tool_id);
            }
            // 如果没找到，尝试通过工具名查找（针对没有tool_id的情况）
            if (toolCallIndex < 0 && msg.tool_name) {
              toolCallIndex = existingItem.tool_calls.findIndex(tc => tc.tool_name === msg.tool_name);
            }

            if (toolCallIndex >= 0) {
              // 更新现有的工具调用
              const existingToolCall = existingItem.tool_calls[toolCallIndex];
              existingItem.tool_calls[toolCallIndex] = {
                ...existingToolCall,
                status: msg.status,
                error: msg.error || existingToolCall.error,
                timestamp: msg.timestamp || existingToolCall.timestamp,
                tool_id: msg.tool_id || existingToolCall.tool_id,
                tool_name: msg.tool_name || existingToolCall.tool_name
              };
              console.log('更新现有工具调用:', existingItem.tool_calls[toolCallIndex]);
            } else {
              // 添加新的工具调用
              const newToolCall: ToolCallInfo = {
                tool_name: msg.tool_name,
                status: msg.status,
                tool_id: msg.tool_id,
                error: msg.error,
                timestamp: msg.timestamp
              };
              existingItem.tool_calls.push(newToolCall);
              console.log('添加新工具调用到现有项目:', newToolCall);
            }

            // 不更新时间戳，保持原始创建时间以便排序稳定
            // existingItem.timestamp = new Date(msg.timestamp);

            return updated;
          } else {
            // 创建新的工具调用项目
            const newItem: TimelineItem = {
              id: `tool_${msg.task_id}_${Date.now()}`,
              type: 'tool',
              task_id: msg.task_id,
              timestamp: new Date(msg.timestamp),
              tool_calls: [{
                tool_name: msg.tool_name,
                status: msg.status,
                tool_id: msg.tool_id,
                error: msg.error,
                timestamp: msg.timestamp
              }]
            };
            console.log('创建新的工具调用项目:', newItem);
            return [...prev, newItem];
          }
        });
        break

      case 'generation_started':
        message.info(`生成任务开始: ${msg.generation_type}`)
        // 创建生成任务时间线项目
        setTimelineItems(prev => {
          const newItem: TimelineItem = {
            id: `gen_${msg.task_id}`,
            type: 'assistant',
            content: `开始生成${msg.generation_type}...`,
            task_id: msg.task_id,
            timestamp: new Date()
          };
          return [...prev, newItem];
        });
        break

      case 'generation_progress':
        // 更新生成进度
        setTimelineItems(prev => prev.map(item => {
          if (item.task_id === msg.task_id && item.type === 'assistant') {
            return {
              ...item,
              content: `${msg.step}: ${msg.progress}%${msg.message ? ` - ${msg.message}` : ''}`
            };
          }
          return item;
        }));
        break

      case 'generation_completed':
        message.success(`生成完成: ${msg.word_count}字`)
        // 更新生成结果
        setTimelineItems(prev => prev.map(item => {
          if (item.task_id === msg.task_id && item.type === 'assistant') {
            return {
              ...item,
              type: 'assistant_final',
              content: msg.content
            };
          }
          return item;
        }));
        break

      case 'generation_failed':
        message.error(`生成失败: ${msg.error}`)
        // 更新失败状态
        setTimelineItems(prev => prev.map(item => {
          if (item.task_id === msg.task_id && item.type === 'assistant') {
            return {
              ...item,
              type: 'assistant_final',
              content: `生成失败: ${msg.error}`
            };
          }
          return item;
        }));
        break

      case 'generation_rejected':
        message.warning(`任务被拒绝: ${msg.reason}`)
        break

      case 'task_cancelled':
        message.info(`任务已取消: ${msg.task_id}`)
        // 只清理AI回复项目，保留工具调用记录
        setTimelineItems(prev => prev.filter(item => {
          if (item.task_id === msg.task_id) {
            // 保留工具调用记录，只移除AI回复
            return item.type === 'tool'
          }
          return true
        }))
        break

      case 'edit_started':
        message.success('AI 已创建待确认的编辑副本')
        break

      case 'edit_preview':
        message.info('已生成编辑预览，可前往编辑工作台查看并确认')
        break

      case 'error':
        message.error(`错误: ${msg.error}`)
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

        // 将消息转换为时间线项目
        const timelineItems: TimelineItem[] = []
        chatMessages.forEach((msg) => {
          if (msg.role === 'user') {
            timelineItems.push({
              id: msg.id,
              type: 'user',
              content: msg.content,
              timestamp: msg.timestamp,
              task_id: msg.id
            })
          } else if (msg.role === 'assistant') {
            timelineItems.push({
              id: msg.id,
              type: 'assistant_final',
              content: msg.content,
              timestamp: msg.timestamp,
              task_id: msg.id
            })
          }
        })
        setTimelineItems(timelineItems)
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
    if (!currentSession) {
      message.warning('请先创建或选择一个会话')
      return
    }

    const userMessage: ChatMessage = {
      id: `temp_${Date.now()}`,
      role: 'user',
      content: inputValue.trim(),
      timestamp: new Date(),
    }
    setMessages(prev => [...prev, userMessage])
    // 添加用户时间线项目
    const userTimelineItem: TimelineItem = {
      id: userMessage.id,
      type: 'user',
      content: inputValue.trim(),
      timestamp: new Date(),
      task_id: userMessage.id
    };
    setTimelineItems(prev => [...prev, userTimelineItem]);
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
    <div style={{
      height: 'calc(100vh - 120px)',
      display: 'flex',
      gap: 18,
      padding: '8px 4px',
      background: 'radial-gradient(circle at top left, rgba(255,229,180,0.22), transparent 28%), linear-gradient(180deg, #fbf8f2 0%, #f4efe7 100%)',
    }}>
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
        style={{
          width: 320,
          display: 'flex',
          flexDirection: 'column',
          borderRadius: 20,
          border: '1px solid rgba(120, 95, 70, 0.12)',
          boxShadow: '0 18px 40px rgba(102, 76, 45, 0.08)',
          background: 'rgba(255,255,255,0.72)',
          backdropFilter: 'blur(18px)',
        }}
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
        style={{
          flex: 1,
          display: 'flex',
          flexDirection: 'column',
          borderRadius: 24,
          border: '1px solid rgba(120, 95, 70, 0.12)',
          boxShadow: '0 20px 48px rgba(102, 76, 45, 0.1)',
          background: 'rgba(255,255,255,0.78)',
          backdropFilter: 'blur(20px)',
        }}
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
              description="创建一个创作会话，像和搭档讨论一样推进情节、风格和章节修改"
              image={Empty.PRESENTED_IMAGE_SIMPLE}
            >
              <Button type="primary" onClick={() => setCreateModalVisible(true)}>
                创建新会话
              </Button>
            </Empty>
          </div>
        ) : (
          <>
            <div style={{ flex: 1, overflow: 'auto', padding: 20 }}>
              {[...timelineItems].sort((a, b) => a.timestamp.getTime() - b.timestamp.getTime()).map((item) => {
                if (item.type === 'user') {
                  return (
                    <div
                      key={item.id}
                      style={{
                        marginBottom: 16,
                        display: 'flex',
                        justifyContent: 'flex-end',
                      }}
                    >
                      <Card
                        size="small"
                        style={{
                          maxWidth: '80%',
                          background: 'linear-gradient(135deg, #fff2cf 0%, #ffe3b3 100%)',
                          border: '1px solid rgba(204, 146, 58, 0.22)',
                          borderRadius: 18,
                          boxShadow: '0 12px 24px rgba(171, 123, 47, 0.08)',
                        }}
                      >
                        <Text style={{ whiteSpace: 'pre-wrap' }}>{item.content}</Text>
                        <div style={{ fontSize: '11px', color: '#8c8c8c', marginTop: 4, textAlign: 'right' }}>
                          {item.timestamp.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                        </div>
                      </Card>
                    </div>
                  )
                } else if (item.type === 'assistant' || item.type === 'assistant_final') {
                  return (
                    <div
                      key={item.id}
                      style={{
                        marginBottom: 16,
                        display: 'flex',
                        justifyContent: 'flex-start',
                      }}
                    >
                      <Card
                        size="small"
                        style={{
                          maxWidth: '80%',
                          background: 'linear-gradient(180deg, #ffffff 0%, #faf6ef 100%)',
                          border: '1px solid rgba(120, 95, 70, 0.12)',
                          borderRadius: 18,
                          boxShadow: '0 12px 24px rgba(120, 95, 70, 0.08)',
                        }}
                      >
                        <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8 }}>
                          <div style={{ color: '#9b5c18', fontWeight: 'bold' }}>AI</div>
                          <div style={{ flex: 1 }}>
                            <Text style={{ whiteSpace: 'pre-wrap' }}>{item.content}</Text>
                            {item.type === 'assistant' && !item.content && (
                              <div style={{ display: 'flex', alignItems: 'center', gap: 4, color: '#8c8c8c' }}>
                                <Spin size="small" />
                                <Text type="secondary" style={{ fontSize: '12px' }}>思考中...</Text>
                              </div>
                            )}
                          </div>
                        </div>
                        <div style={{ fontSize: '11px', color: '#8c8c8c', marginTop: 4, textAlign: 'right' }}>
                          {item.timestamp.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
                        </div>
                      </Card>
                    </div>
                  )
                } else if (item.type === 'tool') {
                  return (
                    <div
                      key={item.id}
                      style={{
                        marginBottom: 16,
                        display: 'flex',
                        justifyContent: 'center',
                      }}
                    >
                      <Card
                        size="small"
                        style={{
                          maxWidth: '90%',
                          background: 'linear-gradient(180deg, #fffaf0 0%, #fff3d8 100%)',
                          border: '1px solid rgba(222, 176, 87, 0.32)',
                          borderRadius: 18,
                        }}
                      >
                        <div style={{ marginBottom: 8 }}>
                            <Text strong style={{ color: '#b06a16' }}>
                              工具协作
                            </Text>
                        </div>
                        {item.tool_calls?.map((tool, index) => (
                          <div
                            key={tool.tool_id || index}
                            style={{
                              padding: '8px 12px',
                              backgroundColor: '#fff',
                              border: '1px solid #ffd591',
                              borderRadius: 4,
                              marginBottom: 8,
                            }}
                          >
                            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                              <div>
                                <Text strong>{tool.tool_name}</Text>
                                <Text type="secondary" style={{ marginLeft: 8, fontSize: '12px' }}>
                                  {tool.timestamp ? new Date(tool.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ''}
                                </Text>
                              </div>
                              <div>
                                {tool.status === 'executing' && (
                                  <Tag color="processing" icon={<Spin size="small" />}>执行中</Tag>
                                )}
                                {tool.status === 'completed' && (
                                  <Tag color="success">完成</Tag>
                                )}
                                {tool.status === 'failed' && (
                                  <Tag color="error">失败</Tag>
                                )}
                                {tool.status === 'rejected' && (
                                  <Tag color="warning">拒绝</Tag>
                                )}
                              </div>
                            </div>
                            {tool.error && (
                              <div style={{ marginTop: 4 }}>
                                <Text type="danger" style={{ fontSize: '12px' }}>错误: {tool.error}</Text>
                              </div>
                            )}
                          </div>
                        ))}
                      </Card>
                    </div>
                  )
                }
                return null
              })}
              <div ref={messagesEndRef} />
            </div>

            <div style={{ padding: 18, borderTop: '1px solid rgba(120, 95, 70, 0.1)', background: 'rgba(255, 250, 244, 0.7)' }}>
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
                  placeholder="直接描述你的创作意图、长期风格偏好，或者让 AI 帮你续写、审阅、修改"
                  autoSize={{ minRows: 1, maxRows: 4 }}
                  style={{ flex: 1, borderRadius: 14, background: 'rgba(255,255,255,0.85)' }}
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
