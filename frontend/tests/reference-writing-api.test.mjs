import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

const apiPath = fileURLToPath(new URL('../src/lib/novelist/api.ts', import.meta.url))
const typesPath = fileURLToPath(new URL('../src/lib/novelist/types.ts', import.meta.url))

test('material-based chapter writing exposes one typed bridge path without short generation timeouts', async () => {
  const [apiSource, typesSource] = await Promise.all([
    readFile(apiPath, 'utf8'),
    readFile(typesPath, 'utf8'),
  ])

  for (const method of [
    'GenerateReferenceBlueprints',
    'GetReferenceWritingSession',
    'SelectReferenceBlueprint',
    'GenerateReferenceDraftCandidates',
  ]) {
    assert.match(apiSource, new RegExp(`\\b${method}: AppMethod<`))
  }

  assert.match(apiSource, /GenerateReferenceBlueprints:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('GenerateReferenceBlueprints', args, \{ timeoutMs: null \}\)/)
  assert.match(apiSource, /GenerateReferenceDraftCandidates:\s*\(\(\.\.\.args\)\s*=>\s*invokeAppArgs\('GenerateReferenceDraftCandidates', args, \{ timeoutMs: null \}\)/)

  const sourceContract = typesSource.match(/export interface WritingDraftSource \{([\s\S]*?)\n\s*\}/)
  assert(sourceContract, 'WritingDraftSource contract must exist')
  assert.match(sourceContract[1], /material_id:\s*string/)
  assert.match(sourceContract[1], /generation_id:\s*string/)
  assert.doesNotMatch(sourceContract[1], /node_id/)
})
