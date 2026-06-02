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
  sub_task_id?: string
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

// TurnSegment 是 turn 内的一个片段：文本块、工具调用或子 Agent
export interface TurnSegment {
  id: string
  type: 'text' | 'tool' | 'subagent'
  content: string
  thinkingContent: string
  thinkingDone: boolean
  isStreaming: boolean
  // tool
  toolName: string
  toolId: string
  toolStatus: 'executing' | 'completed' | 'failed'
  displayText: string
  activityKind: string
  error: string
  // subagent
  status?: 'streaming' | 'done' | 'failed'
  agentType?: 'memory' | 'review'
  taskId?: string
  segments?: TurnSegment[]
  finalText?: string
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
    activityKind: '',
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
  const subagentCache = new Map<string, TurnSegment>()

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
    } else if (msg.role === 'assistant') {
      // 子 Agent 消息：agent_type !== 'main' 且有 sub_task_id
      if (msg.agent_type !== 'main' && msg.sub_task_id && current) {
        const subTaskId = msg.sub_task_id
        const cached = subagentCache.get(subTaskId)
        const subSeg: TurnSegment = cached ?? (() => {
          const seg: TurnSegment = {
            ...emptySegment(`seg_${segCounter++}`),
            type: 'subagent',
            status: 'done',
            agentType: (msg.agent_type as 'memory' | 'review') || 'memory',
            taskId: subTaskId,
            segments: [],
            finalText: '',
          }
          // 不直接 push——等遇到 run_subagent tool_display 时插到正确位置
          subagentCache.set(subTaskId, seg)
          return seg
        })()

        // 追加子 agent 的文本内容
        if ((msg.content || msg.thinking_content) && subSeg.segments) {
          subSeg.segments.push({
            ...emptySegment(`seg_${segCounter++}`),
            type: 'text',
            content: msg.content || '',
            thinkingContent: msg.thinking_content || '',
            thinkingDone: true,
            isStreaming: false,
          })
          if (msg.content) {
            subSeg.finalText = (subSeg.finalText || '') ? (subSeg.finalText + '\n' + msg.content) : msg.content
          }
        }

        // 子 agent 的工具调用
        const toolDisplays = parseToolDisplays(msg.extra_metadata)
        if (toolDisplays.length > 0 && subSeg.segments) {
          for (const td of toolDisplays) {
            const phase = td.phase as 'completed' | 'failed' | 'executing' | undefined
            subSeg.segments.push({
              ...emptySegment(`seg_${segCounter++}`),
              type: 'tool',
              toolName: td.tool_name,
              toolId: td.tool_id,
              toolStatus: phase && (phase === 'executing' || phase === 'completed' || phase === 'failed') ? phase : 'completed',
              displayText: td.display_text,
              activityKind: td.activity_kind,
              error: '',
            })
          }
        }
        continue
      }

      // 主 Agent 消息
      if (!current) continue

      const thinkingContent = msg.thinking_content || ''

      // 文本内容
      if (msg.content || thinkingContent) {
        current.segments.push({
          ...emptySegment(`seg_${segCounter++}`),
          type: 'text',
          content: msg.content || '',
          thinkingContent,
          thinkingDone: true,
          isStreaming: false,
        })
      }

      // 工具展示信息
      const toolDisplays = parseToolDisplays(msg.extra_metadata)
      for (const td of toolDisplays) {
        // run_subagent：在此位置插入子 Agent 卡片
        if (td.tool_name === 'run_subagent') {
          const cached = subagentCache.get(td.tool_id)
          if (cached) current.segments.push(cached)
          continue
        }
        current.segments.push({
          ...emptySegment(`seg_${segCounter++}`),
          type: 'tool',
          toolName: td.tool_name,
          toolId: td.tool_id,
          toolStatus: (td.phase === 'completed' || td.phase === 'failed' || td.phase === 'executing') ? td.phase : 'completed',
          displayText: td.display_text || td.tool_name,
          activityKind: td.activity_kind || '',
          error: '',
        })
      }
    }
  }

  return turns
}

interface ToolDisplay {
  tool_id: string
  tool_name: string
  display_text: string
  activity_kind: string
  phase: string
}

function parseToolDisplays(extraMetadata?: string): ToolDisplay[] {
  if (!extraMetadata) return []
  try {
    const meta = JSON.parse(extraMetadata)
    if (meta.tool_displays && Array.isArray(meta.tool_displays)) {
      return meta.tool_displays as ToolDisplay[]
    }
    return []
  } catch {
    return []
  }
}
