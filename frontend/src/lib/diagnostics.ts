import type { diagnostics } from './novelist/types'

const REDACTION = '[REDACTED]'
const PATH_REDACTION = '[REDACTED_PATH]'
const SOURCE_REDACTION = '[REDACTED_SOURCE_TEXT]'
const MAX_MESSAGE_CHARS = 500
const MAX_STRING_CHARS = 800
const MAX_DETAIL_CHARS = 4000
const MAX_ARRAY_ITEMS = 20
const MAX_OBJECT_DEPTH = 5

const SECRET_KEY_PATTERN = /^(api[_-]?key|authorization|bearer|access[_-]?token|refresh[_-]?token|token|secret|password|credential)$/i
const SOURCE_KEY_PATTERN = /^(source[_-]?text|source[_-]?content|raw[_-]?source|raw[_-]?content|original[_-]?content|modified[_-]?content|candidate[_-]?text|candidate[_-]?content|chapter[_-]?text|chapter[_-]?content|full[_-]?text|full[_-]?content|prompt|content)$/i
const API_KEY_PATTERN = /\b(sk-proj-[A-Za-z0-9_-]{12,}|sk-[A-Za-z0-9_-]{12,}|AIza[0-9A-Za-z_-]{12,})\b/g
const BEARER_PATTERN = /\bBearer\s+[A-Za-z0-9._~+/=-]{8,}\b/gi
const SENSITIVE_ASSIGNMENT_PATTERN = /\b(source[_-]?text|source[_-]?content|raw[_-]?source|raw[_-]?content|original[_-]?content|modified[_-]?content|candidate[_-]?text|candidate[_-]?content|chapter[_-]?text|chapter[_-]?content|full[_-]?text|full[_-]?content|prompt|content)\b\s*[:=]\s*[^\r\n;]+/gi
const FILE_URI_PATTERN = /\bfile:\/\/[^\s;'"]+/gi
const UNC_PATH_PATTERN = /\\\\[^\\/:*?"<>|\r\n;]+\\[^\\/:*?"<>|\r\n;]+(?:\\[^\\/:*?"<>|\r\n;]+)*/g
const WINDOWS_PATH_PATTERN = /[A-Za-z]:[\\/](?:[^\\/:*?"<>|\r\n]+[\\/])*[^\\/:*?"<>|\r\n]*/g
const UNIX_PATH_PATTERN = /(?<![\w])\/(?:Users|home|var|tmp|private|Volumes|mnt|opt|etc|root)\/[^\s:'"]+/g

interface DiagnosticSource {
  error?: unknown
  fallbackMessage: string
  operation: string
  taskId?: string | null
  runId?: string | null
  bridgeMethod?: string | null
  timestamp?: string | number | Date
  detail?: unknown
}

export function diagnosticMessage(error: unknown, fallback: string): string {
  return truncateDiagnosticText(redactDiagnosticText(errorMessage(error, fallback)), MAX_MESSAGE_CHARS)
}

export function buildCopyableDiagnostic(input: DiagnosticSource): diagnostics.CopyableDiagnostic {
  const errorRecord = isRecord(input.error) ? input.error : null
  const code = stringField(errorRecord, 'code') || 'UI_ERROR'
  const bridgeMethod = input.bridgeMethod || stringField(errorRecord, 'method') || null
  const bridgeDetails = errorRecord && 'details' in errorRecord ? errorRecord.details : undefined
  const detail = combineDiagnosticDetail(input.detail, bridgeDetails, input.error)

  return {
    code,
    message: diagnosticMessage(input.error, input.fallbackMessage),
    detail: serializeDiagnosticDetail(detail),
    operation: input.operation,
    task_id: input.taskId ?? null,
    run_id: input.runId ?? null,
    bridge_method: bridgeMethod,
    timestamp: normalizeTimestamp(input.timestamp),
  }
}

function combineDiagnosticDetail(context: unknown, bridgeDetails: unknown, error: unknown): unknown {
  if (context !== undefined && bridgeDetails !== undefined) {
    return {
      context,
      bridge_details: bridgeDetails,
    }
  }
  if (context !== undefined) return context
  if (bridgeDetails !== undefined) return bridgeDetails
  return error
}

export function serializeDiagnosticDetail(value: unknown): string {
  const safe = sanitizeDiagnosticValue(value, 0, '')
  const serialized = typeof safe === 'string' ? safe : JSON.stringify(safe, null, 2)
  return truncateDiagnosticText(serialized, MAX_DETAIL_CHARS)
}

export function redactDiagnosticText(text: string): string {
  return text
    .replace(API_KEY_PATTERN, REDACTION)
    .replace(BEARER_PATTERN, 'Bearer ' + REDACTION)
    .replace(SENSITIVE_ASSIGNMENT_PATTERN, (match) => {
      const key = match.match(/^[^:=\s]+/)?.[0] ?? 'text'
      return `${key}=${SOURCE_REDACTION}`
    })
    .replace(FILE_URI_PATTERN, PATH_REDACTION)
    .replace(UNC_PATH_PATTERN, PATH_REDACTION)
    .replace(WINDOWS_PATH_PATTERN, PATH_REDACTION)
    .replace(UNIX_PATH_PATTERN, PATH_REDACTION)
}

export function formatDiagnosticNumber(value: unknown, locale = currentLocale()): string {
  const numeric = typeof value === 'number' && Number.isFinite(value) ? value : 0
  return new Intl.NumberFormat(locale).format(numeric)
}

function sanitizeDiagnosticValue(value: unknown, depth: number, key: string): unknown {
  if (SECRET_KEY_PATTERN.test(key)) return REDACTION
  if (SOURCE_KEY_PATTERN.test(key)) return redactSourceText(value)
  if (depth > MAX_OBJECT_DEPTH) return '[MAX_DEPTH]'

  if (typeof value === 'string') {
    return truncateDiagnosticText(redactDiagnosticText(value), MAX_STRING_CHARS)
  }
  if (typeof value === 'number') {
    return Number.isFinite(value) ? value : 0
  }
  if (typeof value === 'boolean' || value == null) {
    return value
  }
  if (value instanceof Error) {
    return {
      name: value.name,
      message: truncateDiagnosticText(redactDiagnosticText(value.message), MAX_STRING_CHARS),
    }
  }
  if (value instanceof Date) {
    return value.toISOString()
  }
  if (Array.isArray(value)) {
    const items = value
      .slice(0, MAX_ARRAY_ITEMS)
      .map((item) => sanitizeDiagnosticValue(item, depth + 1, key))
    if (value.length > MAX_ARRAY_ITEMS) {
      items.push(`[${value.length - MAX_ARRAY_ITEMS} more items]`)
    }
    return items
  }
  if (isRecord(value)) {
    const result: Record<string, unknown> = {}
    for (const [childKey, childValue] of Object.entries(value)) {
      result[childKey] = sanitizeDiagnosticValue(childValue, depth + 1, childKey)
    }
    return result
  }
  return truncateDiagnosticText(redactDiagnosticText(String(value)), MAX_STRING_CHARS)
}

function redactSourceText(value: unknown): string {
  const text = typeof value === 'string' ? value : JSON.stringify(value)
  const length = text?.length ?? 0
  return `${SOURCE_REDACTION}${length > 0 ? ` length=${formatDiagnosticNumber(length, 'en-US')}` : ''}`
}

function truncateDiagnosticText(text: string, maxChars: number): string {
  if (text.length <= maxChars) return text
  return `${text.slice(0, maxChars)}... [truncated ${formatDiagnosticNumber(text.length - maxChars, 'en-US')} chars]`
}

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message.trim()) return error.message
  if (typeof error === 'string' && error.trim()) return error
  if (isRecord(error)) {
    const message = stringField(error, 'message')
    if (message) return message
  }
  return fallback
}

function normalizeTimestamp(timestamp: string | number | Date | undefined): string {
  if (timestamp instanceof Date) return timestamp.toISOString()
  if (typeof timestamp === 'number' || typeof timestamp === 'string') {
    const date = new Date(timestamp)
    if (Number.isFinite(date.getTime())) return date.toISOString()
  }
  return new Date().toISOString()
}

function stringField(record: Record<string, unknown> | null, key: string): string {
  const value = record?.[key]
  return typeof value === 'string' && value.trim() ? value.trim() : ''
}

function currentLocale(): string {
  if (typeof navigator !== 'undefined' && navigator.language) {
    return navigator.language
  }
  return 'zh-CN'
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
