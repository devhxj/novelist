import assert from 'node:assert/strict'
import test from 'node:test'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-choices-'))
const outputFile = path.join(tempDir, 'choices.mjs')

try {
  await build({
    entryPoints: ['src/components/chat/choices.ts'],
    outfile: outputFile,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'es2023',
    logLevel: 'silent',
  })

  const { parseChoices } = await import(pathToFileURL(outputFile))

  await test('parses a valid choices block and strips it from the body', () => {
    const content = [
      '这处冲突怎么处理？',
      '',
      '```choices',
      '{"options": ["冷处理", "爆发"]}',
      '```',
    ].join('\n')
    const { body, options } = parseChoices(content)
    assert.equal(body, '这处冲突怎么处理？')
    assert.deepEqual(options, ['冷处理', '爆发'])
  })

  await test('keeps malformed choices blocks visible instead of dropping them', () => {
    const content = [
      '选一个：',
      '',
      '```choices',
      '{"options": ["冷处理"',
      '```',
    ].join('\n')
    const { body, options } = parseChoices(content)
    assert.deepEqual(options, [])
    assert.equal(body, ['选一个：', '', '```choices', '{"options": ["冷处理"', '```'].join('\n'))
  })

  await test('hides an unclosed trailing choices block during streaming', () => {
    const content = ['问题？', '', '```choices', '{"options": ["A"'].join('\n')
    const { body, options } = parseChoices(content)
    assert.equal(body, '问题？')
    assert.deepEqual(options, [])
  })

  await test('merges multiple blocks, deduplicates and caps options', () => {
    const content = [
      '```choices',
      '{"options": ["A", "B"]}',
      '```',
      '中间说明文字',
      '```choices',
      '{"options": ["B", "C", "D", "E", "F", "G", "H"]}',
      '```',
    ].join('\n')
    const { body, options } = parseChoices(content)
    assert.equal(body, '中间说明文字')
    assert.deepEqual(options, ['A', 'B', 'C', 'D', 'E', 'F'])
  })

  await test('returns content untouched when there is no choices block', () => {
    const { body, options } = parseChoices('普通回复，```ts\nconst a = 1\n```')
    assert.equal(options.length, 0)
    assert.equal(body, '普通回复，```ts\nconst a = 1\n```')
  })

  console.log('choices tests passed')
} finally {
  await rm(tempDir, { recursive: true, force: true })
}
