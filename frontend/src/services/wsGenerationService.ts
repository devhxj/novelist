import { useAuthStore } from '@/stores/authStore'
import type { SessionLevel } from './sessionService'

export type GenerationType = 'chapter' | 'dialogue' | 'description' | 'outline' | 'summary' | 'character_profile' | 'chat'
export type GenerationStyle = 'narrative' | 'descriptive' | 'dialogue' | 'poetic' | 'dramatic' | 'natural' | 'vivid'
export type LLMModel = 'deepseek-chat' | 'deepseek-reasoner'

export interface CreateSessionMessage {
  type: 'create_session'
  scope: Scope
  model?: LLMModel
  edit_mode?: 'agent'
}

export interface LoadSessionMessage {
  type: 'load_session'
  session_id: string
}

export interface ChatMessage {
  type: 'chat'
  message: string
  tools_enabled?: boolean
}

export interface GenerateMessage {
  type: 'generate'
  generation_type: GenerationType
  params: Record<string, unknown>
  use_langgraph?: boolean
}

export interface CancelMessage {
  type: 'cancel'
  task_id: string
}

export interface Scope {
  type: string
  chapter_start?: number
  chapter_end?: number
}

export interface SessionCreatedMessage {
  type: 'session_created'
  session_id: string
  scope: Scope
  display_name: string
  title?: string
  subtitle?: string
  model?: string
  edit_mode?: string
  current_chapter_id?: number
  timestamp: string
}

export interface SessionLoadedMessage {
  type: 'session_loaded'
  session_id: string
  scope: Scope
  display_name: string
  title?: string
  subtitle?: string
  message_count: number
  recent_messages?: Array<{
    role: string
    content: string
    message_id?: string
    created_at?: string
  }>
}

export interface ChatStartedMessage {
  type: 'chat_started'
  task_id: string
  session_id?: string
  timestamp?: string
}

export interface ChatChunkMessage {
  type: 'chat_chunk'
  message_id: string
  chunk: string
  accumulated_length: number
}

export interface ChatCompletedMessage {
  type: 'chat_completed'
  task_id?: string
  session_id: string
  message_count?: number
  timestamp?: string
}

export interface ChatFailedMessage {
  type: 'chat_failed'
  message_id: string
  error: string
}

export interface GenerationStartedMessage {
  type: 'generation_started'
  task_id: string
  generation_type: GenerationType
  novel_id: number
}

export interface GenerationProgressMessage {
  type: 'generation_progress'
  task_id: string
  step: string
  progress: number
  message: string
}

export interface ContentChunkMessage {
  type: 'content_chunk'
  task_id: string
  chunk: string
  accumulated_length: number
}

export interface ReviewResultMessage {
  type: 'review_result'
  task_id: string
  approved: boolean
  score: number
  issues: string[]
}

export interface ConsistencyCheckMessage {
  type: 'consistency_check'
  task_id: string
  passed: boolean
  issues: string[]
}

export interface GenerationCompletedMessage {
  type: 'generation_completed'
  task_id: string
  content: string
  word_count: number
  chapter_id?: number
}

export interface GenerationFailedMessage {
  type: 'generation_failed'
  task_id: string
  error: string
}

export interface GenerationRejectedMessage {
  type: 'generation_rejected'
  reason: string
  current_tasks: number
  max_tasks: number
}

export interface ToolCallMessage {
  type: 'tool_call'
  task_id: string
  tool_name: string
  status: 'executing' | 'completed' | 'failed' | 'rejected'
  tool_id?: string
  error?: string
  timestamp: string
}

export interface TaskCancelledMessage {
  type: 'task_cancelled'
  task_id: string
  timestamp: string
}

export interface EditStartedMessage {
  type: 'edit_started'
  task_id: string
  tool_name: string
  chapter_id?: number
  edit_session_id?: string
  working_content?: string
  original_content?: string
  change_count?: number
  timestamp: string
}

export interface EditPreviewMessage {
  type: 'edit_preview'
  task_id: string
  tool_name: string
  chapter_id?: number
  edit_session_id?: string
  working_content?: string
  timestamp: string
}

export interface ErrorMessage {
  type: 'error'
  error: string
  timestamp: string
}

export type WSMessage =
  | SessionCreatedMessage
  | SessionLoadedMessage
  | ChatStartedMessage
  | ChatChunkMessage
  | ChatCompletedMessage
  | ChatFailedMessage
  | GenerationStartedMessage
  | GenerationProgressMessage
  | ContentChunkMessage
  | ReviewResultMessage
  | ConsistencyCheckMessage
  | GenerationCompletedMessage
  | GenerationFailedMessage
  | GenerationRejectedMessage
  | ToolCallMessage
  | TaskCancelledMessage
  | EditStartedMessage
  | EditPreviewMessage
  | ErrorMessage

export type WSMessageHandler = (message: WSMessage) => void

export interface ChapterGenerationParams {
  chapter_id?: number
  chapter_number: number
  target_length?: number
  model?: LLMModel
  style?: GenerationStyle
  user_prompt?: string
  chapter_outline?: string
  key_events?: string[]
  focus_characters?: string[]
}

export class WebSocketGenerationService {
  private ws: WebSocket | null = null
  private reconnectAttempts = 0
  private maxReconnectAttempts = 5
  private reconnectDelay = 1000
  private messageHandlers: Set<WSMessageHandler> = new Set()
  private novelId: number | null = null
  private isConnecting = false
  private shouldReconnect = true

  connect(novelId?: number): Promise<void> {
    this.novelId = novelId || null
    this.shouldReconnect = true
    return new Promise((resolve, reject) => {
      const token = useAuthStore.getState().accessToken
      if (!token) {
        reject(new Error('No authentication token'))
        return
      }

      if (this.isConnecting) {
        resolve()
        return
      }

      this.isConnecting = true

      const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
      const host = window.location.host
      let wsUrl = `${protocol}//${host}/ws/chat?token=${token}`
      if (novelId) {
        wsUrl += `&novel_id=${novelId}`
      }

      this.ws = new WebSocket(wsUrl)

      this.ws.onopen = () => {
        console.log('WebSocket connected')
        this.reconnectAttempts = 0
        this.isConnecting = false
        resolve()
      }

      this.ws.onmessage = (event) => {
        try {
          const message: WSMessage = JSON.parse(event.data)
          this.messageHandlers.forEach(handler => handler(message))
        } catch (error) {
          console.error('Failed to parse WebSocket message:', error)
        }
      }

      this.ws.onerror = (error) => {
        console.error('WebSocket error:', error)
        this.isConnecting = false
      }

      this.ws.onclose = (event) => {
        console.log('WebSocket closed:', event.code, event.reason)
        this.isConnecting = false
        if (this.shouldReconnect && event.code !== 1000 && this.reconnectAttempts < this.maxReconnectAttempts) {
          this.reconnectAttempts++
          const delay = Math.min(this.reconnectDelay * this.reconnectAttempts, 10000)
          console.log(`WebSocket reconnecting in ${delay}ms (attempt ${this.reconnectAttempts}/${this.maxReconnectAttempts})`)
          setTimeout(() => {
            this.connect(this.novelId || undefined).catch(() => {})
          }, delay)
        } else if (event.code === 1000) {
          console.log('WebSocket disconnected by user')
        } else if (this.reconnectAttempts >= this.maxReconnectAttempts) {
          console.error('WebSocket max reconnect attempts reached')
        }
      }
    })
  }

  disconnect() {
    this.shouldReconnect = false
    this.reconnectAttempts = this.maxReconnectAttempts
    if (this.ws) {
      if (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING) {
        this.ws.close(1000, 'User disconnected')
      }
      this.ws = null
    }
    this.messageHandlers.clear()
    this.novelId = null
    this.isConnecting = false
  }

  onMessage(handler: WSMessageHandler): () => void {
    this.messageHandlers.add(handler)
    return () => {
      this.messageHandlers.delete(handler)
    }
  }

  createSession(level: SessionLevel, _novelId?: number, chapterNumber?: number, model?: LLMModel) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected')
    }

    const message: CreateSessionMessage = {
      type: 'create_session',
      scope: {
        type: level === 'free' ? 'novel' : level,
        chapter_start: level === 'chapter' ? chapterNumber : undefined,
      },
      model,
      edit_mode: 'agent',
    }

    this.ws.send(JSON.stringify(message))
  }

  loadSession(sessionId: string) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected')
    }

    const message: LoadSessionMessage = {
      type: 'load_session',
      session_id: sessionId,
    }

    this.ws.send(JSON.stringify(message))
  }

  chat(message: string, _model?: LLMModel, _temperature?: number) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected')
    }

    const chatMsg: ChatMessage = {
      type: 'chat',
      message,
      tools_enabled: true,
    }

    this.ws.send(JSON.stringify(chatMsg))
  }

  startGeneration(generationType: GenerationType, params: Record<string, unknown>, useLanggraph = false) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected')
    }

    const message: GenerateMessage = {
      type: 'generate',
      generation_type: generationType,
      params,
      use_langgraph: useLanggraph,
    }

    this.ws.send(JSON.stringify(message))
  }

  cancelGeneration(taskId: string) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected')
    }

    const message: CancelMessage = {
      type: 'cancel',
      task_id: taskId,
    }

    this.ws.send(JSON.stringify(message))
  }

  isConnected(): boolean {
    return this.ws !== null && this.ws.readyState === WebSocket.OPEN
  }
}

export const wsGenerationService = new WebSocketGenerationService()
