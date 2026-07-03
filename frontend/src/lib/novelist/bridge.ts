export const DEFAULT_BRIDGE_TIMEOUT_MS = 30_000
export const DEFAULT_MAX_MESSAGE_BYTES = 1024 * 1024

export type BridgeMessageKind = 'request' | 'response' | 'event' | 'cancel'

export interface BridgeRequest {
  kind: 'request'
  id: string
  method: string
  payload: unknown
  deadline_ms?: number
}

export interface BridgeCancel {
  kind: 'cancel'
  id: string
}

export interface BridgeErrorPayload {
  code: string
  message: string
  details?: unknown
  retryable?: boolean
}

export interface BridgeSuccessResponse<TResult = unknown> {
  kind: 'response'
  id: string
  ok: true
  result: TResult
}

export interface BridgeFailureResponse {
  kind: 'response'
  id: string | null
  ok: false
  error: BridgeErrorPayload
}

export type BridgeResponse<TResult = unknown> =
  | BridgeSuccessResponse<TResult>
  | BridgeFailureResponse

export interface BridgeEvent<TPayload = unknown> {
  kind: 'event'
  name: string
  payload: TPayload
}

export interface BridgeInvokeOptions {
  timeoutMs?: number | null
  signal?: AbortSignal
}

export interface BridgeInvocation<TResult = unknown> {
  id: string
  promise: Promise<TResult>
  cancel: (reason?: string) => boolean
}

export type BridgeEventHandler<TPayload = unknown> = (payload: TPayload) => void

export interface BridgeTransport {
  send: (message: string) => void
  subscribe: (handler: (message: string) => void) => () => void
}

export interface BridgeClientOptions {
  defaultTimeoutMs?: number | null
  maxMessageBytes?: number
}

export interface NovelistGlobal {
  invoke: <TResult = unknown>(
    method: string,
    payload?: unknown,
    options?: BridgeInvokeOptions,
  ) => Promise<TResult>
  invokeCancellable: <TResult = unknown>(
    method: string,
    payload?: unknown,
    options?: BridgeInvokeOptions,
  ) => BridgeInvocation<TResult>
  cancel: (id: string, reason?: string) => boolean
  events: {
    on: <TPayload = unknown>(
      eventName: string,
      callback: BridgeEventHandler<TPayload>,
    ) => () => void
  }
  window?: {
    minimize: () => Promise<void>
    toggleMaximize: () => Promise<void>
    isMaximized: () => Promise<boolean>
  }
  app?: {
    quit: () => Promise<void>
  }
  shell?: {
    openExternal: (url: string) => Promise<void>
  }
}

declare global {
  interface External {
    sendMessage?: (message: string) => void
    receiveMessage?: (callback: (message: string) => void) => void
  }

  interface Window {
    novelist?: NovelistGlobal
  }
}

interface PendingRequest {
  method: string
  resolve: (value: unknown) => void
  reject: (reason: unknown) => void
  timeoutId?: ReturnType<typeof setTimeout>
  cleanupAbort?: () => void
}

interface ErrorWithCodeOptions {
  code: string
  details?: unknown
  retryable?: boolean
  requestId?: string | null
  method?: string
  cause?: unknown
}

export class BridgeError extends Error {
  readonly code: string
  readonly details?: unknown
  readonly retryable: boolean
  readonly requestId?: string | null
  readonly method?: string

  constructor(message: string, options: ErrorWithCodeOptions) {
    super(message, { cause: options.cause })
    this.name = 'BridgeError'
    this.code = options.code
    this.details = options.details
    this.retryable = options.retryable ?? false
    this.requestId = options.requestId
    this.method = options.method
  }
}

export class BridgeInvocationError extends BridgeError {
  constructor(message: string, options: ErrorWithCodeOptions) {
    super(message, options)
    this.name = 'BridgeInvocationError'
  }
}

export class BridgeTimeoutError extends BridgeError {
  constructor(requestId: string, method: string, timeoutMs: number) {
    super(`Bridge request timed out after ${timeoutMs} ms.`, {
      code: 'REQUEST_TIMEOUT',
      requestId,
      method,
      retryable: true,
    })
    this.name = 'BridgeTimeoutError'
  }
}

export class BridgeAbortError extends BridgeError {
  constructor(requestId: string, method: string, reason?: string) {
    super(reason ?? 'Bridge request was cancelled.', {
      code: 'CANCELLED',
      requestId,
      method,
      retryable: true,
    })
    this.name = 'BridgeAbortError'
  }
}

export class BridgeTransportError extends BridgeError {
  constructor(message: string, cause?: unknown) {
    super(message, {
      code: 'BRIDGE_UNAVAILABLE',
      retryable: true,
      cause,
    })
    this.name = 'BridgeTransportError'
  }
}

export class BridgeMessageTooLargeError extends BridgeError {
  constructor(sizeBytes: number, maxBytes: number) {
    super(`Bridge message is ${sizeBytes} bytes; limit is ${maxBytes} bytes.`, {
      code: 'MESSAGE_TOO_LARGE',
      details: { sizeBytes, maxBytes },
      retryable: false,
    })
    this.name = 'BridgeMessageTooLargeError'
  }
}

export class BridgeClient {
  private readonly pending = new Map<string, PendingRequest>()
  private readonly eventHandlers = new Map<string, Set<BridgeEventHandler<unknown>>>()
  private readonly defaultTimeoutMs: number | null
  private readonly maxMessageBytes: number
  private unsubscribeTransport?: () => void
  private requestSequence = 0

  constructor(
    private readonly transport: BridgeTransport = createPhotinoTransport(),
    options: BridgeClientOptions = {},
  ) {
    this.defaultTimeoutMs = normalizeTimeout(options.defaultTimeoutMs, DEFAULT_BRIDGE_TIMEOUT_MS)
    this.maxMessageBytes = normalizeMaxMessageBytes(options.maxMessageBytes)
  }

  invoke<TResult = unknown>(
    method: string,
    payload: unknown = {},
    options: BridgeInvokeOptions = {},
  ): Promise<TResult> {
    return this.invokeCancellable<TResult>(method, payload, options).promise
  }

  invokeCancellable<TResult = unknown>(
    method: string,
    payload: unknown = {},
    options: BridgeInvokeOptions = {},
  ): BridgeInvocation<TResult> {
    assertNonEmptyString(method, 'method')

    const id = this.createRequestId()
    const timeoutMs = normalizeTimeout(options.timeoutMs, this.defaultTimeoutMs)
    let resolvePromise: (value: TResult) => void
    let rejectPromise: (reason: unknown) => void

    const promise = new Promise<TResult>((resolve, reject) => {
      resolvePromise = resolve
      rejectPromise = reject
    })

    if (options.signal?.aborted) {
      rejectPromise!(new BridgeAbortError(id, method, getAbortReason(options.signal)))
      return {
        id,
        promise,
        cancel: () => false,
      }
    }

    const pending: PendingRequest = {
      method,
      resolve: (value) => resolvePromise!(value as TResult),
      reject: (reason) => rejectPromise!(reason),
    }

    if (timeoutMs !== null) {
      pending.timeoutId = setTimeout(() => {
        this.cancelPending(id, new BridgeTimeoutError(id, method, timeoutMs))
      }, timeoutMs)
    }

    if (options.signal) {
      const abortHandler = () => {
        this.cancelPending(id, new BridgeAbortError(id, method, getAbortReason(options.signal)))
      }
      options.signal.addEventListener('abort', abortHandler, { once: true })
      pending.cleanupAbort = () => options.signal?.removeEventListener('abort', abortHandler)
    }

    this.pending.set(id, pending)

    try {
      this.ensureSubscribed()
      this.sendEnvelope({
        kind: 'request',
        id,
        method,
        payload,
        deadline_ms: timeoutMs ?? undefined,
      })
    } catch (error) {
      this.rejectPending(
        id,
        error instanceof BridgeError
          ? error
          : new BridgeTransportError('Unable to send bridge request.', error),
      )
    }

    return {
      id,
      promise,
      cancel: (reason) => this.cancelPending(id, new BridgeAbortError(id, method, reason)),
    }
  }

  cancel(id: string, reason?: string): boolean {
    assertNonEmptyString(id, 'id')
    const pending = this.pending.get(id)
    return this.cancelPending(id, new BridgeAbortError(id, pending?.method ?? 'unknown', reason))
  }

  on<TPayload = unknown>(
    eventName: string,
    handler: BridgeEventHandler<TPayload>,
  ): () => void {
    assertNonEmptyString(eventName, 'eventName')
    this.ensureSubscribed()

    const handlers = this.eventHandlers.get(eventName) ?? new Set<BridgeEventHandler<unknown>>()
    const unknownHandler = handler as BridgeEventHandler<unknown>
    handlers.add(unknownHandler)
    this.eventHandlers.set(eventName, handlers)

    return () => {
      handlers.delete(unknownHandler)
      if (handlers.size === 0) {
        this.eventHandlers.delete(eventName)
      }
    }
  }

  dispose(reason = 'Bridge client disposed.'): void {
    this.unsubscribeTransport?.()
    this.unsubscribeTransport = undefined

    for (const [id, pending] of this.pending) {
      this.pending.delete(id)
      this.cleanupPending(pending)
      pending.reject(new BridgeAbortError(id, pending.method, reason))
    }

    this.eventHandlers.clear()
  }

  private createRequestId(): string {
    this.requestSequence += 1

    if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
      return `req_${crypto.randomUUID().replaceAll('-', '')}`
    }

    return `req_${Date.now().toString(36)}_${this.requestSequence.toString(36)}`
  }

  private ensureSubscribed(): void {
    if (this.unsubscribeTransport) {
      return
    }

    this.unsubscribeTransport = this.transport.subscribe((message) => this.handleTransportMessage(message))
  }

  private sendEnvelope(envelope: BridgeRequest | BridgeCancel): void {
    const message = JSON.stringify(envelope)
    const sizeBytes = getUtf8ByteLength(message)

    if (sizeBytes > this.maxMessageBytes) {
      throw new BridgeMessageTooLargeError(sizeBytes, this.maxMessageBytes)
    }

    this.transport.send(message)
  }

  private handleTransportMessage(message: string): void {
    let parsed: unknown

    try {
      parsed = JSON.parse(message)
    } catch (error) {
      this.warnMalformedMessage('Received non-JSON bridge message.', error)
      return
    }

    if (!isRecord(parsed)) {
      this.warnMalformedMessage('Received bridge message that is not an object.')
      return
    }

    switch (parsed.kind) {
      case 'response':
        this.handleResponse(parsed)
        return
      case 'event':
        this.handleEvent(parsed)
        return
      default:
        this.warnMalformedMessage('Received bridge message with unsupported kind.', parsed.kind)
    }
  }

  private handleResponse(message: Record<string, unknown>): void {
    const id = typeof message.id === 'string' ? message.id : null

    if (!id) {
      this.warnMalformedMessage('Received bridge response without a request id.')
      return
    }

    const pending = this.pending.get(id)
    if (!pending) {
      return
    }

    if (message.ok === true) {
      this.resolvePending(id, message.result)
      return
    }

    if (message.ok === false) {
      const bridgeError = parseBridgeErrorPayload(message.error)
      this.rejectPending(
        id,
        new BridgeInvocationError(bridgeError.message, {
          code: bridgeError.code,
          details: bridgeError.details,
          retryable: bridgeError.retryable,
          requestId: id,
          method: pending.method,
        }),
      )
      return
    }

    this.rejectPending(
      id,
      new BridgeInvocationError('Bridge response is missing an ok flag.', {
        code: 'INVALID_BRIDGE_RESPONSE',
        requestId: id,
        method: pending.method,
      }),
    )
  }

  private handleEvent(message: Record<string, unknown>): void {
    if (typeof message.name !== 'string' || message.name.trim() === '') {
      this.warnMalformedMessage('Received bridge event without a name.')
      return
    }

    const handlers = this.eventHandlers.get(message.name)
    if (!handlers || handlers.size === 0) {
      return
    }

    for (const handler of [...handlers]) {
      try {
        handler(message.payload)
      } catch (error) {
        this.warnMalformedMessage('Bridge event handler failed.', error)
      }
    }
  }

  private resolvePending(id: string, value: unknown): void {
    const pending = this.pending.get(id)
    if (!pending) {
      return
    }

    this.pending.delete(id)
    this.cleanupPending(pending)
    pending.resolve(value)
  }

  private rejectPending(id: string, reason: unknown): void {
    const pending = this.pending.get(id)
    if (!pending) {
      return
    }

    this.pending.delete(id)
    this.cleanupPending(pending)
    pending.reject(reason)
  }

  private cancelPending(id: string, reason: BridgeAbortError | BridgeTimeoutError): boolean {
    const pending = this.pending.get(id)
    if (!pending) {
      return false
    }

    this.pending.delete(id)
    this.cleanupPending(pending)

    try {
      this.sendEnvelope({ kind: 'cancel', id })
    } catch (error) {
      pending.reject(
        new BridgeTransportError('Bridge request was cancelled, but the cancel message could not be sent.', error),
      )
      return true
    }

    pending.reject(reason)
    return true
  }

  private cleanupPending(pending: PendingRequest): void {
    if (pending.timeoutId) {
      clearTimeout(pending.timeoutId)
    }

    pending.cleanupAbort?.()
  }

  private warnMalformedMessage(message: string, detail?: unknown): void {
    if (typeof console !== 'undefined') {
      console.warn(`[novelist bridge] ${message}`, detail)
    }
  }
}

export function createPhotinoTransport(): BridgeTransport {
  const subscribers = new Set<(message: string) => void>()
  let receiveRegistered = false

  return {
    send(message) {
      getPhotinoExternal().sendMessage(message)
    },
    subscribe(handler) {
      const external = getPhotinoExternal()
      subscribers.add(handler)

      if (!receiveRegistered) {
        try {
          external.receiveMessage((message) => {
            for (const subscriber of [...subscribers]) {
              subscriber(String(message))
            }
          })
          receiveRegistered = true
        } catch (error) {
          subscribers.delete(handler)
          throw error
        }
      }

      return () => {
        subscribers.delete(handler)
      }
    },
  }
}

export const bridge = new BridgeClient()

export function installNovelistBridgeGlobal(client: BridgeClient = bridge): NovelistGlobal {
  const globalApi: NovelistGlobal = {
    invoke: (method, payload = {}, options = {}) => client.invoke(method, payload, options),
    invokeCancellable: (method, payload = {}, options = {}) =>
      client.invokeCancellable(method, payload, options),
    cancel: (id, reason) => client.cancel(id, reason),
    events: {
      on: (eventName, callback) => client.on(eventName, callback),
    },
  }

  if (typeof window !== 'undefined') {
    window.novelist = {
      ...window.novelist,
      ...globalApi,
      events: {
        ...window.novelist?.events,
        ...globalApi.events,
      },
    }
  }

  return globalApi
}

export const novelist = installNovelistBridgeGlobal()

function getPhotinoExternal(): Required<Pick<External, 'sendMessage' | 'receiveMessage'>> {
  if (typeof window === 'undefined') {
    throw new BridgeTransportError('Photino bridge is unavailable outside a browser window.')
  }

  if (
    typeof window.external?.sendMessage !== 'function' ||
    typeof window.external.receiveMessage !== 'function'
  ) {
    throw new BridgeTransportError('Photino bridge is unavailable on window.external.')
  }

  return {
    sendMessage: window.external.sendMessage.bind(window.external),
    receiveMessage: window.external.receiveMessage.bind(window.external),
  }
}

function parseBridgeErrorPayload(value: unknown): BridgeErrorPayload {
  if (!isRecord(value)) {
    return {
      code: 'INVALID_BRIDGE_RESPONSE',
      message: 'Bridge error response is missing an error object.',
      retryable: false,
    }
  }

  return {
    code: typeof value.code === 'string' && value.code.trim() !== ''
      ? value.code
      : 'INVALID_BRIDGE_RESPONSE',
    message: typeof value.message === 'string' && value.message.trim() !== ''
      ? value.message
      : 'Bridge error response is missing an error message.',
    details: value.details,
    retryable: value.retryable === true,
  }
}

function normalizeTimeout(value: number | null | undefined, fallback: number | null): number | null {
  if (value === null) {
    return null
  }

  if (value === undefined) {
    return fallback
  }

  if (!Number.isFinite(value) || value <= 0) {
    throw new RangeError('Bridge timeout must be a positive finite number or null.')
  }

  return Math.trunc(value)
}

function normalizeMaxMessageBytes(value: number | undefined): number {
  if (value === undefined) {
    return DEFAULT_MAX_MESSAGE_BYTES
  }

  if (!Number.isFinite(value) || value <= 0) {
    throw new RangeError('Bridge max message bytes must be a positive finite number.')
  }

  return Math.trunc(value)
}

function assertNonEmptyString(value: string, label: string): void {
  if (typeof value !== 'string' || value.trim() === '') {
    throw new TypeError(`Bridge ${label} must be a non-empty string.`)
  }
}

function getUtf8ByteLength(value: string): number {
  if (typeof TextEncoder !== 'undefined') {
    return new TextEncoder().encode(value).byteLength
  }

  return value.length
}

function getAbortReason(signal: AbortSignal | undefined): string | undefined {
  const reason = signal?.reason
  return typeof reason === 'string' && reason.trim() !== '' ? reason : undefined
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
