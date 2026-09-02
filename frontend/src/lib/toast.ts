// 统一通知通道（F9）：后台完成/失败/提示的单一出口。
// 模块级小 store + useSyncExternalStore 订阅，避免为几条 toast 引入状态库。
// 展示端是 ToastHost（aria-live 容器）；任何组件都可直接 pushToast。

export type ToastKind = 'info' | 'success' | 'error'

export interface ToastAction {
  label: string
  run: () => void
}

export interface ToastItem {
  id: number
  kind: ToastKind
  message: string
  description?: string
  action?: ToastAction
}

export interface ToastInput {
  kind?: ToastKind
  message: string
  description?: string
  action?: ToastAction
}

type Listener = (toasts: ToastItem[]) => void

const AUTO_DISMISS_MS: Record<ToastKind, number> = { info: 5000, success: 6000, error: 10000 }
const MAX_VISIBLE = 4

let toasts: ToastItem[] = []
let nextId = 1
const listeners = new Set<Listener>()

function emit() {
  for (const listener of listeners) listener(toasts)
}

function scheduleAutoDismiss(id: number, kind: ToastKind) {
  const delay = AUTO_DISMISS_MS[kind]
  window.setTimeout(() => dismissToast(id), delay)
}

export function pushToast(input: ToastInput): number {
  const id = nextId++
  const kind = input.kind ?? 'info'
  const item: ToastItem = { id, kind, message: input.message, description: input.description, action: input.action }
  toasts = [...toasts, item].slice(-MAX_VISIBLE)
  emit()
  scheduleAutoDismiss(id, kind)
  return id
}

export function dismissToast(id: number) {
  if (!toasts.some((item) => item.id === id)) return
  toasts = toasts.filter((item) => item.id !== id)
  emit()
}

export function getToastSnapshot(): ToastItem[] {
  return toasts
}

export function subscribeToasts(listener: Listener): () => void {
  listeners.add(listener)
  listener(toasts)
  return () => {
    listeners.delete(listener)
  }
}
