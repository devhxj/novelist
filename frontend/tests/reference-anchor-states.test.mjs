import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-anchor-states-'))
const outputFile = path.join(tempDir, 'referenceAnchorStates.mjs')

try {
  await build({
    entryPoints: ['src/lib/novelist/referenceAnchorStates.ts'],
    outfile: outputFile,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'es2023',
    logLevel: 'silent',
  })

  const { referenceAnchorStates, describeAnchorStatus } = await import(pathToFileURL(outputFile))

  // 从后端契约源码提取 ReferenceAnchorBuildStates 的全部取值（U12 验收口径）：
  // 前端状态映射必须覆盖真实枚举，不允许散落的手写状态判断漏掉新状态。
  const contractsSource = await readFile(
    path.join('..', 'src', 'Novelist.Contracts', 'App', 'ReferenceAnchorPayloads.cs'),
    'utf8',
  )
  const statesClass = contractsSource.match(
    /public static class ReferenceAnchorBuildStates\s*\{([\s\S]*?)\n\}/,
  )
  assert(statesClass, 'ReferenceAnchorBuildStates class must exist in contracts')
  const backendStatuses = [...statesClass[1].matchAll(/public const string \w+ = "([^"]+)"/g)].map((m) => m[1])
  assert(backendStatuses.length >= 18, `expected the full build-state list, found ${backendStatuses.length}`)

  const missing = backendStatuses.filter((status) => !referenceAnchorStates[status])
  assert.deepEqual(missing, [], 'every backend build state must have a frontend mapping')

  // 只有 ready 是可用态；失败/取消/过期一律不可用。
  const usable = backendStatuses.filter((status) => referenceAnchorStates[status].usable)
  assert.deepEqual(usable, ['ready'], 'only the ready state may enter the corpus workflow')

  // 失败族与过期态的语义检查：作者能看到具体卡在哪一步。
  for (const status of ['failed_import', 'failed_segmenting', 'failed_extraction', 'failed_slotting', 'failed_embedding', 'cancelled']) {
    assert.equal(referenceAnchorStates[status].tone, 'failed', `${status} must read as failed`)
  }
  assert.equal(referenceAnchorStates.stale.label, '来源已变化', 'stale must surface the source-changed meaning')
  for (const status of ['importing', 'segmenting', 'extracting_materials', 'detecting_slots', 'embedding']) {
    assert.equal(referenceAnchorStates[status].tone, 'working', `${status} must read as in-progress`)
  }

  // 未知状态兜底：不能误标成可用。
  const unknown = describeAnchorStatus('some_future_state')
  assert.equal(unknown.usable, false)
  assert.equal(describeAnchorStatus(undefined).usable, false)

  console.log('reference anchor states contract tests passed')
} finally {
  await rm(tempDir, { recursive: true, force: true })
}
