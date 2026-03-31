import { useAuthStore } from '@/stores/authStore'
import type { SessionLevel } from './sessionService'

export type GenerationType = 'chapter' | 'dialogue' | 'description' | 'outline' | 'summary' | 'character_profile' | 'chat'
export type GenerationStyle = 'narrative' | 'descriptive' | 'dialogue' | 'poetic' | 'dramatic' | 'natural' | 'vivid'
export type LLMModel = 'deepseek-chat' | 'deepseek-reasoner'

export interface CreateSessionMessage {
  type: 'create_session'
  level: SessionLevel
  novel_id?: number
  chapter_number?: number
  model?: LLMModel
}

export interface LoadSessionMessage {
  type: 'load_session'
  session_id: string
}

export interface ChatMessage {
  type: 'chat'
  message: string
  model?: LLMModel
  temperature?: number
}

export interface StartGenerationMessage {
  type: 'start_generation'
  generation_type: GenerationType
  params: Record<string, unknown>
  use_langgraph?: boolean
}

export interface CancelGenerationMessage {
  type: 'cancel_generation'
  task_id: string
}

export interface SessionCreatedMessage {
  type: 'session_created'
  session_id: string
  level: SessionLevel
  display_name: string
  novel_id?: number
  chapter_number?: number
  context_usage: number
}

export interface SessionLoadedMessage {
  type: 'session_loaded'
  session_id: string
  level: SessionLevel
  display_name: string
  message_count: number
  context_usage: number
}

export interface ChatStartedMessage {
  type: 'chat_started'
  message_id: string
}

export interface ChatChunkMessage {
  type: 'chat_chunk'
  message_id: string
  chunk: string
  accumulated_length: number
}

export interface ChatCompletedMessage {
  type: 'chat_completed'
  message_id: string
  content: string
  word_count: number
  context_usage: number
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
      let wsUrl = `${protocol}//${host}/ws/generation?token=${token}`
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

  createSession(level: SessionLevel, novelId?: number, chapterNumber?: number, model?: LLMModel) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected')
    }

    const message: CreateSessionMessage = {
      type: 'create_session',
      level,
      novel_id: novelId,
      chapter_number: chapterNumber,
      model,
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

  chat(message: string, model?: LLMModel, temperature?: number) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected')
    }

    const chatMsg: ChatMessage = {
      type: 'chat',
      message,
      model,
      temperature,
    }

    this.ws.send(JSON.stringify(chatMsg))
  }

  startGeneration(generationType: GenerationType, params: Record<string, unknown>, useLanggraph = false) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error('WebSocket not connected')
    }

    const message: StartGenerationMessage = {
      type: 'start_generation',
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

    const message: CancelGenerationMessage = {
      type: 'cancel_generation',
      task_id: taskId,
    }

    this.ws.send(JSON.stringify(message))
  }

  isConnected(): boolean {
    return this.ws !== null && this.ws.readyState === WebSocket.OPEN
  }
}

export const wsGenerationService = new WebSocketGenerationService()
