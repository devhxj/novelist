import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'
import test from 'node:test'

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-conflict-'))
const outputFile = path.join(tempDir, 'fileChangeConflict.mjs')

await build({
  entryPoints: ['src/components/content/fileChangeConflict.ts'],
  outfile: outputFile,
  bundle: true,
  platform: 'node',
  format: 'esm',
  target: 'es2023',
  logLevel: 'silent',
})

const {
  resolveFileChange,
  fileChangePatch,
  acceptIncomingPatch,
  derivedOutlinePath,
  conflictDiffToolId,
} = await import(pathToFileURL(outputFile))

// 模拟 ContentPanel 里 file:changed 的实际处理：决策 → 取盘上内容 → 打补丁。
const applyFileChange = (tab, eventPath, incoming) => {
  const decision = resolveFileChange(tab, eventPath)
  if (decision.kind === 'ignore') return { decision, next: { ...tab } }
  return { decision, next: { ...tab, ...fileChangePatch(decision, incoming, eventPath) } }
}

test('a dirty content tab keeps its own text and dirty flag when the file changes underneath', () => {
  const tab = {
    id: 'file_1',
    type: 'file',
    path: 'chapters/007.md',
    title: '第7章',
    content: '我刚写下的这一段还没存盘',
    isDirty: true,
  }

  const { decision, next } = applyFileChange(tab, 'chapters/007.md', 'AI 重写后的整章内容')

  assert.equal(decision.kind, 'conflict')
  assert.equal(decision.target, 'content')
  // 这两条是 U7 的全部要点：内容不被覆盖，脏标记不被清掉。
  assert.equal(next.content, '我刚写下的这一段还没存盘')
  assert.equal(next.isDirty, true)
  assert.deepEqual(next.conflict, {
    target: 'content',
    path: 'chapters/007.md',
    incoming: 'AI 重写后的整章内容',
  })
})

test('the conflict patch never carries content or isDirty at all', () => {
  const patch = fileChangePatch({ kind: 'conflict', target: 'content' }, '外部内容', 'chapters/007.md')

  // 用 in 而不是 undefined 比较：写进 patch 的 undefined 也会覆盖掉 tab 上的原值。
  assert.equal('content' in patch, false)
  assert.equal('outlineContent' in patch, false)
  assert.equal('isDirty' in patch, false)
  assert.deepEqual(Object.keys(patch), ['conflict'])
})

test('a clean content tab still refreshes in place', () => {
  const tab = {
    id: 'file_2',
    type: 'file',
    path: 'chapters/007.md',
    title: '第7章',
    content: '旧内容',
    isDirty: false,
  }

  const { decision, next } = applyFileChange(tab, 'chapters/007.md', '新内容')

  assert.equal(decision.kind, 'refresh')
  assert.equal(next.content, '新内容')
  assert.equal(next.isDirty, false)
  assert.equal(next.conflict, undefined)
})

test('refreshing clears a previously pending conflict', () => {
  const tab = {
    id: 'file_3',
    type: 'file',
    path: 'chapters/007.md',
    title: '第7章',
    content: '已存盘的内容',
    isDirty: false,
    conflict: { target: 'content', path: 'chapters/007.md', incoming: '过期的外部版本' },
  }

  const { next } = applyFileChange(tab, 'chapters/007.md', '最新内容')

  assert.equal(next.conflict, undefined, 'a stale conflict bar must not survive an adopted refresh')
})

test('the outline companion refreshes even while the chapter body is dirty', () => {
  const tab = {
    id: 'file_4',
    type: 'file',
    path: 'chapters/007.md',
    title: '第7章',
    content: '未保存的正文',
    isDirty: true,
    outlineContent: '旧大纲',
  }

  // 大纲只有只读视图，不存在未保存改动，刷新不会丢东西。
  const { decision, next } = applyFileChange(tab, 'outlines/007.md', '新大纲')

  assert.equal(decision.kind, 'refresh')
  assert.equal(decision.target, 'outlineContent')
  assert.equal(next.outlineContent, '新大纲')
  assert.equal(next.content, '未保存的正文', 'the outline refresh must not touch the body')
  assert.equal(next.isDirty, true, 'the outline refresh must not clear the body dirty flag')
})

test('unrelated paths, diff tabs and empty events are ignored', () => {
  const fileTab = { id: 'file_5', type: 'file', path: 'chapters/007.md', title: '第7章', isDirty: true }

  assert.equal(resolveFileChange(fileTab, 'chapters/008.md').kind, 'ignore')
  assert.equal(resolveFileChange(fileTab, 'outlines/008.md').kind, 'ignore')
  assert.equal(resolveFileChange(fileTab, undefined).kind, 'ignore')
  assert.equal(resolveFileChange(fileTab, '').kind, 'ignore')
  assert.equal(
    resolveFileChange({ id: 'diff_1', type: 'diff', path: 'chapters/007.md', title: 'diff' }, 'chapters/007.md').kind,
    'ignore',
  )
  assert.deepEqual(fileChangePatch({ kind: 'ignore' }, '任意内容', 'chapters/007.md'), {})
})

test('novelist.md has no outline companion and conflicts on itself', () => {
  assert.equal(derivedOutlinePath('novelist.md'), null)
  assert.equal(derivedOutlinePath('chapters/007.md'), 'outlines/007.md')
  assert.equal(derivedOutlinePath('skills/polish.md'), null)

  const tab = { id: 'file_6', type: 'file', path: 'novelist.md', title: '故事状态', content: '我的版本', isDirty: true }
  assert.equal(resolveFileChange(tab, 'novelist.md').kind, 'conflict')
})

test('choosing the incoming version lands it and returns the tab to a clean state', () => {
  const conflict = { target: 'content', path: 'chapters/007.md', incoming: 'AI 版本' }
  const patch = acceptIncomingPatch(conflict)

  assert.equal(patch.content, 'AI 版本')
  assert.equal(patch.isDirty, false)
  assert.equal(patch.conflict, undefined)
  assert.equal('conflict' in patch, true, 'the conflict key must be present so the spread clears it')

  // 大纲侧不该顺手清正文的脏标记。
  const outlinePatch = acceptIncomingPatch({ target: 'outlineContent', path: 'outlines/007.md', incoming: '新大纲' })
  assert.equal(outlinePatch.outlineContent, '新大纲')
  assert.equal('isDirty' in outlinePatch, false)
})

test('the conflict diff tab id is derived from the path so repeat clicks reuse one tab', () => {
  assert.equal(conflictDiffToolId('chapters/007.md'), 'file-change-conflict:chapters/007.md')
  assert.notEqual(conflictDiffToolId('chapters/007.md'), conflictDiffToolId('chapters/008.md'))
})

test.after(async () => {
  await rm(tempDir, { recursive: true, force: true })
})
