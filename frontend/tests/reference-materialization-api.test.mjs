import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const apiPath = fileURLToPath(new URL('../src/lib/novelist/api.ts', import.meta.url))

test('materialization source registration and model-backed operations do not inherit the 30 second bridge timeout', async () => {
  const source = await readFile(apiPath, 'utf8')

  assert.match(source, /RegisterReferenceMaterializationSource:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('RegisterReferenceMaterializationSource', args, \{ timeoutMs: null \}\)/)
  assert.match(source, /AnalyzeReferenceChapterSplit:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('AnalyzeReferenceChapterSplit', args, \{ timeoutMs: null \}\)/)
  assert.match(source, /GenerateReferenceMaterializationBlueprintPreview:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('GenerateReferenceMaterializationBlueprintPreview', args, \{ timeoutMs: null \}\)/)
})
