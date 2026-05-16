import { message } from 'antd'
import { useAuthStore } from '@/stores/authStore'

export type EditMode = 'agent'
export type ReasoningEffort = 'high' | 'max'

export interface ChatMsg {
  type: 'chat'
  session_id?: string
  message: string
  tools_enabled?: boolean
  model?: string
  reasoning_effort?: string | null
}

export interface CancelMsg {
  type: 'cancel'
  task_id: string
}

export interface OutlineApprovalMsg {
  approved: boolean
  feedback?: string
}

export type ClientMsg =
  | ChatMsg
  | CancelMsg
  | OutlineApprovalMsg

export interface SessionCreatedMsg {
  type: 'session_created'
  session_id: string
  model: string
  reasoning_effort?: ReasoningEffort
  title?: string
}

export interface TitleUpdatedMsg {
  type: 'title_updated'
  session_id: string
  title: string
  auto_generated: boolean
  timestamp?: string
}

export interface ContentChunkMsg {
  type: 'content_chunk'
  chunk: string
  task_id?: string
  message_id?: string
  parent_task_id?: string
  source?: string
}

export interface ThinkingChunkMsg {
  type: 'thinking_chunk'
  chunk: string
  task_id?: string
  parent_task_id?: string
  source?: string
  agent_type?: string
}

export interface ThinkingDoneMsg {
  type: 'thinking_done'
  task_id?: string
  parent_task_id?: string
  source?: string
  timestamp?: string
}

export interface ToolCallMsg {
  type: 'tool_call'
  task_id?: string
  parent_task_id?: string
  source?: string
  tool_name: string
  tool_id?: string
  status: 'executing' | 'completed' | 'failed' | 'rejected'
  phase?: 'selected' | 'executing' | 'completed' | 'failed'
  display_text?: string
  activity_kind?: 'general' | 'browse' | 'view' | 'edit' | 'write' | 'create' | 'memory' | 'review' | 'plan'
  chapter_id?: number
  chapter_number?: number
  chapter_title?: string
  metadata?: Record<string, unknown>
  result_summary?: {
    success?: boolean
    error?: string | null
    metadata?: Record<string, unknown>
    data_keys?: string[]
  }
  error?: string
  timestamp?: string
}

export interface ChatStartedMsg {
  type: 'chat_started'
  task_id?: string
  session_id?: string
  timestamp?: string
}

export interface ChatCompletedMsg {
  type: 'chat_completed'
  session_id: string
  task_id?: string
  timestamp?: string
}

export interface ChatFailedMsg {
  type: 'chat_failed'
  task_id?: string
  error: string
  timestamp?: string
}

export interface ErrorMsg {
  type: 'error'
  error: string
  edit_session_id?: string
  chapter_id?: number
  latest_pending_edit_session_id?: string | null
  timestamp?: string
}

export interface TaskCancelledMsg {
  type: 'task_cancelled'
  task_id: string
}

export interface DiffData {
  change_type: string
  hunks: DiffHunk[]
  old_content: string
  new_content: string
  summary: { additions: number; deletions: number; hunks: number }
}

export interface DiffHunk {
  old_start: number
  old_lines: number
  new_start: number
  new_lines: number
  changes: DiffChange[]
}

export interface DiffChange {
  type: 'delete' | 'insert' | 'context'
  content: string
  line_number: number
}

export interface EditPreviewMsg {
  type: 'edit_preview'
  task_id: string
  tool_name: string
  chapter_id: number
  edit_session_id: string
  working_content: string
  change_count: number
  diff: {
    change_type: string
    hunks: Array<{
      old_start: number
      old_lines: number
      new_start: number
      new_lines: number
      changes: Array<{
        type: string
        content: string
        line_number: number
      }>
    }>
    old_content: string
    new_content: string
    summary: {
      additions: number
      deletions: number
      hunks: number
    }
  }
}

export interface EditStreamMsg {
  type: 'edit_stream'
  task_id: string
  tool_name: string
  chapter_id?: number
  edit_session_id?: string
  working_content: string
  timestamp?: string
}

export interface EditPendingMsg {
  type: 'edit_pending'
  task_id: string
  edit_session_id: string
  latest_pending_edit_session_id?: string | null
  chapter_id?: number
  change_count: number
  timestamp?: string
}

export interface OutlineGeneratedMsg {
  type: 'outline_generated'
  novel_id: number
  chapter_numbers: number[]
  content: string
  outlines: Array<Record<string, unknown>>
}

export interface SessionUsageDetail {
  system: number
  user: number
  assistant: number
  tool: number
}

export interface UsageData {
  prompt_tokens: number
  completion_tokens: number
  total_tokens: number
  context_window: number
  usage_ratio: number
  detail?: SessionUsageDetail
}

export interface UsageMsg extends UsageData {
  type: 'usage'
  task_id?: string
  parent_task_id?: string
  timestamp?: string
}

export type ServerMsg =
  | SessionCreatedMsg
  | TitleUpdatedMsg
  | ContentChunkMsg
  | ThinkingChunkMsg
  | ThinkingDoneMsg
  | ToolCallMsg
  | ChatStartedMsg
  | ChatCompletedMsg
  | ChatFailedMsg
  | ErrorMsg
  | TaskCancelledMsg
  | EditPreviewMsg
  | EditPendingMsg
  | EditStreamMsg
  | OutlineGeneratedMsg
  | UsageMsg

export type MsgHandler = (msg: ServerMsg) => void

export class WsEditorService {
  private ws: WebSocket | null = null
  private handlers: Set<MsgHandler> = new Set()
  private isConnecting = false
  private shouldReconnect = true
  private reconnectAttempts = 0
  private maxReconnect = 5

  connect(novelId: number): Promise<void> {
    this.shouldReconnect = true
    return new Promise((resolve, reject) => {
      const token = useAuthStore.getState().accessToken
      if (!token) { reject(new Error('No token')); return }
      if (this.isConnecting) { resolve(); return }
      this.isConnecting = true

      const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:'
      const host = window.location.host
      const url = `${protocol}//${host}/ws/chat?token=${token}&novel_id=${novelId}`

      this.ws = new WebSocket(url)

      this.ws.onopen = () => {
        console.log('[EditorWS] connected')
        this.reconnectAttempts = 0
        this.isConnecting = false
        resolve()
      }

      this.ws.onmessage = (e) => {
        try {
          const msg: ServerMsg = JSON.parse(e.data)
          this.handlers.forEach(h => h(msg))
        } catch (err) {
          console.error('[EditorWS] parse error:', err)
        }
      }

      this.ws.onerror = () => { this.isConnecting = false }

      this.ws.onclose = (e) => {
        this.isConnecting = false
        if (e.code === 4001) {
          this.shouldReconnect = false
          message.error('登录已过期，请重新登录')
          setTimeout(() => {
            useAuthStore.getState().logout()
            window.location.href = '/login'
          }, 1500)
          return
        }
        if (this.shouldReconnect && e.code !== 1000 && this.reconnectAttempts < this.maxReconnect) {
          this.reconnectAttempts++
          const delay = Math.min(1000 * this.reconnectAttempts, 10000)
          setTimeout(() => this.connect(novelId).catch(() => {}), delay)
        }
      }
    })
  }

  disconnect() {
    this.shouldReconnect = false
    this.reconnectAttempts = this.maxReconnect
    if (this.ws && (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CONNECTING)) {
      this.ws.close(1000, 'User disconnected')
    }
    this.ws = null
    this.handlers.clear()
    this.isConnecting = false
  }

  onMessage(handler: MsgHandler): () => void {
    this.handlers.add(handler)
    return () => { this.handlers.delete(handler) }
  }

  send(msg: ClientMsg): boolean {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      console.warn('[EditorWS] not connected, cannot send. readyState:', this.ws?.readyState)
      return false
    }
    this.ws.send(JSON.stringify(msg))
    return true
  }

  chat(sessionId: string | null, message: string, options?: { toolsEnabled?: boolean; model?: string; reasoningEffort?: string | null }): boolean {
    const msg: ChatMsg = { type: 'chat', message, tools_enabled: options?.toolsEnabled ?? true }
    if (sessionId) msg.session_id = sessionId
    if (options?.model) msg.model = options.model
    if (options?.reasoningEffort) msg.reasoning_effort = options.reasoningEffort
    return this.send(msg)
  }

  cancelTask(taskId: string): boolean {
    return this.send({ type: 'cancel', task_id: taskId })
  }

  sendOutlineApproval(approved: boolean, feedback?: string): boolean {
    return this.send({ type: 'outline_approval', approved, feedback })
  }

  isConnected(): boolean {
    return this.ws !== null && this.ws.readyState === WebSocket.OPEN
  }
}

export const wsEditorService = new WsEditorService()
