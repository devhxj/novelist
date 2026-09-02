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

test('every materialization source registration entry opts out of the timeout the same way', async () => {
  const source = await readFile(apiPath, 'utf8')

  // 两个注册入口都会跑完整的抓取/切分/分析流程，用时远超 30 秒。
  // 只给其中一个开豁免，另一个就会在长素材上被桥接层判超时。
  const registrationMethods = [
    'RegisterReferenceMaterializationSource',
    'RegisterReferenceMaterializationSourceFromContent',
  ]

  const timeoutOptions = registrationMethods.map(name => {
    const entry = new RegExp(`${name}:\\s*\\(\\(\\.\\.\\.args\\)\\s*=>\\s*invokeAppArgs\\('${name}', args, (\\{[^}]*\\})\\)`)
    const match = source.match(entry)
    assert.ok(match, `${name} must be declared with invokeAppArgs and an explicit timeout option`)
    return match[1].replace(/\s+/g, ' ').trim()
  })

  assert.equal(
    timeoutOptions[0],
    timeoutOptions[1],
    'both registration entries must share one timeout policy',
  )
  assert.equal(timeoutOptions[0], '{ timeoutMs: null }')
})
