import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-diagnostics-'))
const outputFile = path.join(tempDir, 'diagnostics.mjs')

try {
  await build({
    entryPoints: ['src/lib/diagnostics.ts'],
    outfile: outputFile,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'es2023',
    logLevel: 'silent',
  })

  const {
    buildCopyableDiagnostic,
    diagnosticMessage,
    formatDiagnosticNumber,
    redactDiagnosticText,
    serializeDiagnosticDetail,
  } = await import(pathToFileURL(outputFile))

  const unsafe = {
    api_key: 'sk-proj-abcdefghijklmnopqrstuvwxyz1234567890',
    authorization: 'Bearer live-secret-token-1234567890',
    source_text: '第一章'.repeat(400),
    candidate_text: '候选正文不应进入诊断',
    prompt: '请续写这一段正文',
    nested: {
      password: 'correct-horse-battery-staple',
      message: 'failed at D:\\Users\\writer\\secret.txt; candidate_text=候选正文不应进入诊断; prompt: 请续写这一段正文',
      chapter_text: '章节正文不应进入诊断',
    },
  }
  const serialized = serializeDiagnosticDetail(unsafe)

  assert(!serialized.includes('sk-proj-abcdefghijklmnopqrstuvwxyz'), 'API keys must be redacted')
  assert(!serialized.includes('live-secret-token'), 'bearer tokens must be redacted')
  assert(!serialized.includes('correct-horse'), 'secret-like fields must be redacted')
  assert(!serialized.includes('D:\\Users\\writer\\secret.txt'), 'local file paths must be redacted')
  assert(!serialized.includes('候选正文不应进入诊断'), 'candidate text must be redacted')
  assert(!serialized.includes('请续写这一段正文'), 'prompt text must be redacted')
  assert(!serialized.includes('章节正文不应进入诊断'), 'chapter text must be redacted')
  assert(!serialized.includes('第一章'.repeat(100)), 'long raw source content must be truncated')
  assert(serialized.includes('[REDACTED]'), 'redaction marker should be visible in diagnostics')
  assert(serialized.includes('[REDACTED_PATH]'), 'path redaction marker should be visible in diagnostics')

  assert.equal(
    redactDiagnosticText('Authorization: Bearer abcdefghijklmnopqrstuvwxyz'),
    'Authorization: Bearer [REDACTED]',
    'bearer tokens redact in plain strings',
  )

  assert.equal(
    redactDiagnosticText('failed at file:///Users/writer/secret.md and /home/writer/private.md'),
    'failed at [REDACTED_PATH] and [REDACTED_PATH]',
    'file URIs and Unix paths redact in plain strings',
  )

  assert.equal(
    redactDiagnosticText('candidate_text=候选正文不应进入诊断; full_content: 全文不应进入诊断'),
    'candidate_text=[REDACTED_SOURCE_TEXT]; full_content=[REDACTED_SOURCE_TEXT]',
    'sensitive text assignments redact in plain strings',
  )

  assert.equal(
    diagnosticMessage(new Error('删除失败：sk-abcdefghijklmnopqrstuvwxyz'), 'fallback'),
    '删除失败：[REDACTED]',
    'visible diagnostic messages are redacted before display',
  )

  const diagnostic = buildCopyableDiagnostic({
    error: Object.assign(new Error('Bridge failed'), {
      code: 'VALIDATION_ERROR',
      details: unsafe,
      method: 'DeleteCharacter',
    }),
    fallbackMessage: '删除失败',
    operation: 'Delete character',
    taskId: 'task-1',
    runId: 'run-1',
    timestamp: '2026-07-08T00:00:00.000Z',
  })

  assert.equal(diagnostic.code, 'VALIDATION_ERROR')
  assert.equal(diagnostic.message, 'Bridge failed')
  assert.equal(diagnostic.operation, 'Delete character')
  assert.equal(diagnostic.task_id, 'task-1')
  assert.equal(diagnostic.run_id, 'run-1')
  assert.equal(diagnostic.bridge_method, 'DeleteCharacter')
  assert.equal(diagnostic.timestamp, '2026-07-08T00:00:00.000Z')
  assert(!diagnostic.detail.includes('sk-proj-abcdefghijklmnopqrstuvwxyz'), 'structured diagnostic detail must be redacted')
  assert(diagnostic.detail.includes('[REDACTED_SOURCE_TEXT]'), 'bridge details should be preserved and source text redacted')

  const diagnosticWithContext = buildCopyableDiagnostic({
    error: Object.assign(new Error('Bridge failed'), {
      code: 'VALIDATION_ERROR',
      details: unsafe,
      method: 'DeleteCharacter',
    }),
    fallbackMessage: '删除失败',
    operation: 'Delete character',
    detail: { novel_id: 42, character_id: 1 },
  })
  assert(diagnosticWithContext.detail.includes('"context"'), 'caller context should be retained')
  assert(diagnosticWithContext.detail.includes('"bridge_details"'), 'bridge details should be retained beside caller context')
  assert(!diagnosticWithContext.detail.includes('correct-horse'), 'merged diagnostic detail must still be redacted')

  assert.equal(
    formatDiagnosticNumber(1234567, 'en-US'),
    '1,234,567',
    'English diagnostic numbers use Intl formatting',
  )
  assert.equal(
    formatDiagnosticNumber(Number.NaN, 'en-US'),
    '0',
    'malformed diagnostic numbers degrade to a formatted fallback',
  )
} finally {
  await rm(tempDir, { recursive: true, force: true })
}
