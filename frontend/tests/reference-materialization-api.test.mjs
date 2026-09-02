import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const apiPath = fileURLToPath(new URL('../src/lib/novelist/api.ts', import.meta.url))

test('materialization source registration and model-backed operations do not inherit the 30 second bridge timeout', async () => {
  const source = await readFile(apiPath, 'utf8')

  assert.match(source, /RegisterReferenceMaterializationSource:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('RegisterReferenceMaterializationSource', args, \{ timeoutMs: null \}\)/)
  assert.match(source, /AnalyzeReferenceChapterSplit:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('AnalyzeReferenceChapterSplit', args, \{ timeoutMs: null \}\)/)
  assert.match(source, /ExportReferenceCorpusPackage:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('ExportReferenceCorpusPackage', args, \{ timeoutMs: null \}\)/)
  assert.match(source, /ImportReferenceCorpusPackage:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('ImportReferenceCorpusPackage', args, \{ timeoutMs: null \}\)/)
  // 蓝图预览随装配线退役，bridge-guardrails.mjs 已断言桥接方法不得复活，这里守住前端适配层。
  assert.doesNotMatch(source, /GenerateReferenceMaterializationBlueprintPreview/)
})
