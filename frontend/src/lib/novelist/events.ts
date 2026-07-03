import { bridge, type BridgeEventHandler } from './bridge'

export type EventCallback<TPayload = unknown> = BridgeEventHandler<TPayload>

export function on<TPayload = unknown>(
  eventName: string,
  callback: EventCallback<TPayload>,
): () => void {
  return bridge.on(eventName, callback)
}

export function EventsOn<TPayload = unknown>(
  eventName: string,
  callback: EventCallback<TPayload>,
): () => void {
  return on(eventName, callback)
}
