import type { session } from '@/hooks/useApp'

// 与 Go 端 internal/agent/events.go 的 AgentEventType 枚举一一对应
export enum AgentEventType {
  Thinking = 0,
  ThinkingDone = 1,
  Content = 2,
  ToolCall = 3,
  Usage = 4,
  Error = 5,
}

// AgentEvent 与 Go 端 AgentEvent 的 JSON 序列化一一对应
export interface AgentEvent {
  turn_id: number
  seq?: number
  type: AgentEventType
  data?: string
  tool_name?: string
  tool_id?: string
  phase?: string         // "selected" | "executing" | "completed" | "failed" | "loop_detected"
  tool_args?: Record<string, unknown>
  success?: boolean
  error?: string
  display_text?: string
  activity_kind?: string
  metadata?: Record<string, unknown>
  usage?: Record<string, unknown>
  timestamp: string
}

// TurnSegment 是 turn 内的一个片段：文本块或工具调用
export interface TurnSegment {
  id: string
  type: 'text' | 'tool'
  content: string
  thinkingContent: string
  thinkingDone: boolean
  isStreaming: boolean
  toolName: string
  toolId: string
  toolStatus: 'executing' | 'completed' | 'failed'
  displayText: string
  error: string
}

export function emptySegment(id: string): TurnSegment {
  return {
    id,
    type: 'text',
    content: '',
    thinkingContent: '',
    thinkingDone: false,
    isStreaming: false,
    toolName: '',
    toolId: '',
    toolStatus: 'executing',
    displayText: '',
    error: '',
  }
}

// Turn 是一次对话轮次：用户消息 + AI 回复的 segments
export interface Turn {
  id: string
  turnId: number
  userMessage: string
  segments: TurnSegment[]
  status: 'streaming' | 'done' | 'failed'
  errorMessage?: string
}


export function rebuildTurns(messages: session.Message[]): Turn[] {
  const turns: Turn[] = []
  let current: Turn | null = null
  let segCounter = 0

  for (const msg of messages) {
    if (msg.role === 'user') {
      current = {
        id: `hist_${msg.turn_id}`,
        turnId: msg.turn_id,
        userMessage: msg.content,
        segments: [],
        status: 'done',
      }
      turns.push(current)
    } else if (msg.role === 'assistant' && msg.agent_type === 'main' && current) {
      const thinkingContent = msg.thinking_content || ''
      current.segments.push({
        id: `seg_${msg.turn_id}_${segCounter++}`,
        type: 'text',
        content: msg.content,
        thinkingContent,
        thinkingDone: true,
        isStreaming: false,
        toolName: '',
        toolId: '',
        toolStatus: 'executing' as const,
        displayText: '',
        error: '',
      })
    }
  }

  return turns
}
