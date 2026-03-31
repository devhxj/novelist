import { useAuthStore } from '@/stores/authStore'

export type ScopeType = 'novel' | 'chapters' | 'chapter'

export interface Scope {
  type: ScopeType
  chapter_start?: number
  chapter_end?: number
}

export interface StartEditMsg {
  type: 'start_edit'
  chapter_id: number
}

export interface ApplyEditMsg {
  type: 'apply_edit'
  edit_session_id: string
  change_type: 'full_replace' | 'partial_edit' | 'insert' | 'delete'
  new_content: string
  start_line?: number
  end_line?: number
  reason?: string
}

export interface AcceptEditMsg {
  type: 'accept_edit'
  edit_session_id: string
}

export interface RejectEditMsg {
  type: 'reject_edit'
  edit_session_id: string
}

export interface CreateSessionMsg {
  type: 'create_session'
  scope: Scope
  model?: string
}

export interface LoadSessionMsg {
  type: 'load_session'
  session_id: string
}

export interface ListSessionsMsg {
  type: 'list_sessions'
  scope_type?: ScopeType
}

export interface ChatMsg {
  type: 'chat'
  session_id: string
  message: string
  tools_enabled?: boolean
}

export interface ReadChapterMsg {
  type: 'read_chapter'
  chapter_id: number
}

export interface CancelMsg {
  type: 'cancel'
  task_id: string
}

export type ClientMsg =
  | StartEditMsg
  | ApplyEditMsg
  | AcceptEditMsg
  | RejectEditMsg
  | CreateSessionMsg
  | LoadSessionMsg
  | ListSessionsMsg
  | ChatMsg
  | ReadChapterMsg
  | CancelMsg

export interface EditStartedMsg {
  type: 'edit_started'
  edit_session_id: string
  chapter_id: number
  original_content: string
  working_content: string
  change_count: number
}

export interface EditAppliedMsg {
  type: 'edit_applied'
  edit_session_id: string
  change_count: number
  working_content: string
  diff: DiffData
}

export interface EditAcceptedMsg {
  type: 'edit_accepted'
  edit_session_id: string
  chapter_id: number
  change_count: number
  word_count: number
  message: string
}

export interface EditRejectedMsg {
  type: 'edit_rejected'
  edit_session_id: string
  chapter_id: number
  message: string
  original_content: string
}

export interface SessionCreatedMsg {
  type: 'session_created'
  session_id: string
  scope: Scope
  display_name: string
}

export interface SessionLoadedMsg {
  type: 'session_loaded'
  session_id: string
  scope: Scope
  display_name: string
  message_count: number
  recent_messages: Array<{ role: string; content: string; message_id?: string; created_at?: string }>
}

export interface SessionListMsg {
  type: 'sessions_list'
  sessions: Array<{
    session_id: string
    scope: Scope
    display_name: string
    title?: string
    message_count: number
    updated_at: string
  }>
}

export interface ContentChunkMsg {
  type: 'content_chunk'
  chunk: string
  task_id?: string
  message_id?: string
}

export interface ToolCallMsg {
  type: 'tool_call'
  tool_name: string
  status: 'executing' | 'completed' | 'failed'
  result?: unknown
}

export interface ChatStartedMsg {
  type: 'chat_started'
  message_id: string
}

export interface ChatCompletedMsg {
  type: 'chat_completed'
  session_id: string
  message_id?: string
  content?: string
}

export interface ChapterContentMsg {
  type: 'chapter_content'
  chapter_id: number
  content: string
  word_count: number
}

export interface ErrorMsg {
  type: 'error'
  error: string
}

export interface SessionEndedMsg {
  type: 'session_ended'
  session_id: string
  message: string
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

export type ServerMsg =
  | EditStartedMsg
  | EditAppliedMsg
  | EditAcceptedMsg
  | EditRejectedMsg
  | SessionCreatedMsg
  | SessionLoadedMsg
  | SessionListMsg
  | ContentChunkMsg
  | ToolCallMsg
  | ChatStartedMsg
  | ChatCompletedMsg
  | ChapterContentMsg
  | ErrorMsg
  | SessionEndedMsg

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

  startEdit(_chapterId: number): boolean {
    return this.send({ type: 'start_edit', chapter_id: _chapterId })
  }

  applyEdit(editSessionId: string, changeType: 'full_replace' | 'partial_edit' | 'insert' | 'delete', newContent: string, startLine?: number, endLine?: number, reason?: string): boolean {
    return this.send({
      type: 'apply_edit',
      edit_session_id: editSessionId,
      change_type: changeType,
      new_content: newContent,
      start_line: startLine,
      end_line: endLine,
      reason,
    })
  }

  acceptEdit(editSessionId: string): boolean {
    return this.send({ type: 'accept_edit', edit_session_id: editSessionId })
  }

  rejectEdit(editSessionId: string): boolean {
    return this.send({ type: 'reject_edit', edit_session_id: editSessionId })
  }

  createSession(scope: Scope, model?: string): boolean {
    return this.send({ type: 'create_session', scope, model })
  }

  loadSession(sessionId: string): boolean {
    return this.send({ type: 'load_session', session_id: sessionId })
  }

  listSessions(scopeType?: ScopeType): boolean {
    return this.send({ type: 'list_sessions', scope_type: scopeType })
  }

  chat(sessionId: string, message: string, toolsEnabled = true): boolean {
    return this.send({ type: 'chat', session_id: sessionId, message, tools_enabled: toolsEnabled })
  }

  readChapter(chapterId: number): boolean {
    return this.send({ type: 'read_chapter', chapter_id: chapterId })
  }

  cancelTask(taskId: string): boolean {
    return this.send({ type: 'cancel', task_id: taskId })
  }

  isConnected(): boolean {
    return this.ws !== null && this.ws.readyState === WebSocket.OPEN
  }
}

export const wsEditorService = new WsEditorService()
