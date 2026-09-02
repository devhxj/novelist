import { BridgeError } from './bridge'

export interface BridgeErrorDiagnostic {
  message: string
  code: string | null
}

// 统一的 bridge 错误呈现：优先透出服务端错误码与消息，未知错误退回兜底文案。
// 与 TimelineView 的 buildVisibleError 同范式，供参考书/语料组件复用。
export function describeBridgeError(error: unknown, fallback: string): BridgeErrorDiagnostic {
  if (error instanceof BridgeError) {
    return { message: error.message || fallback, code: error.code }
  }
  if (error instanceof Error && error.message) {
    return { message: error.message, code: null }
  }
  return { message: fallback, code: null }
}
