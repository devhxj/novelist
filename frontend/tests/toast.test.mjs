import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

// toast.ts 用 window.setTimeout 调度自动消失；node 下先放替身并记录定时器，
// 测试就能手动触发"到时"而不真等 5–10 秒。
const scheduledTimers = []
globalThis.window = {
  setTimeout: (fn, delay) => {
    scheduledTimers.push({ fn, delay })
    return scheduledTimers.length
  },
}

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-toast-'))
const outputFile = path.join(tempDir, 'toast.mjs')

try {
  const abs = (p) => path.resolve(p).replaceAll('\\', '/')
  await build({
    stdin: {
      contents: `export * from ${JSON.stringify(abs('src/lib/toast.ts'))};`,
      resolveDir: process.cwd(),
      loader: 'js',
    },
    outfile: outputFile,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'es2023',
    logLevel: 'silent',
  })
  const toast = await import(pathToFileURL(outputFile).href)

  function clearAll() {
    for (const item of toast.getToastSnapshot()) toast.dismissToast(item.id)
    scheduledTimers.length = 0
  }

  {
    // U16：无动作条最多 4 条，挤出从最老的无动作条开始。
    clearAll()
    const ids = []
    for (let i = 1; i <= 6; i += 1) {
      ids.push(toast.pushToast({ kind: 'info', message: `plain-${i}` }))
    }
    const snapshot = toast.getToastSnapshot()
    assert.equal(snapshot.length, 4, 'plain toasts cap at MAX_VISIBLE')
    assert.deepEqual(snapshot.map((item) => item.message), ['plain-3', 'plain-4', 'plain-5', 'plain-6'], 'oldest plain toasts evicted first')
    assert.ok(ids.slice(0, 2).every((id) => !snapshot.some((item) => item.id === id)), 'evicted ids leave the snapshot')
  }

  {
    // U16/U14：带动作的 toast 不自动消失、不参与挤出。
    clearAll()
    const actionA = toast.pushToast({ kind: 'info', message: 'deleted chapter', action: { label: '撤销', run: () => {} } })
    const actionB = toast.pushToast({ kind: 'error', message: 'export failed', action: { label: '重试', run: () => {} } })
    assert.equal(scheduledTimers.length, 0, 'no auto-dismiss timer is scheduled for action toasts')
    for (let i = 1; i <= 6; i += 1) {
      toast.pushToast({ kind: 'info', message: `plain-${i}` })
    }
    const snapshot = toast.getToastSnapshot()
    assert.equal(snapshot.length, 6, '2 action toasts + 4 plain toasts coexist')
    assert.ok(snapshot.some((item) => item.id === actionA && item.action?.label === '撤销'), 'undo toast survives the burst')
    assert.ok(snapshot.some((item) => item.id === actionB), 'error action toast survives the burst')
    assert.equal(scheduledTimers.length, 6, 'one dismiss timer per plain toast push')
    assert.ok(scheduledTimers.every((timer) => timer.delay === 5000), 'plain info toasts schedule the 5s dismiss')
  }

  {
    // U14：动作条不接受自动消失——手动触发全部已调度定时器后它仍在。
    clearAll()
    const actionId = toast.pushToast({ kind: 'info', message: 'deleted chapter', action: { label: '撤销', run: () => {} } })
    toast.pushToast({ kind: 'info', message: 'plain note' })
    for (const timer of [...scheduledTimers]) timer.fn()
    const snapshot = toast.getToastSnapshot()
    assert.ok(snapshot.some((item) => item.id === actionId), 'action toast must not auto-dismiss')
    assert.ok(!snapshot.some((item) => item.message === 'plain note'), 'plain toast dismissed by its timer')
  }

  {
    // dismissToast 仍可移除动作条（动作执行/手动关闭的路径）。
    clearAll()
    const id = toast.pushToast({ kind: 'success', message: 'restored', action: { label: '知道了', run: () => {} } })
    toast.dismissToast(id)
    assert.equal(toast.getToastSnapshot().length, 0, 'dismissToast removes an action toast')
  }

  {
    // R9：动作条自身有界（多本书材料化完成会一次推 N 条）——超限从最老的丢起，
    // 否则旧卡片被顶出屏幕、动作按钮再也点不到。
    clearAll()
    for (let i = 1; i <= 7; i += 1) {
      toast.pushToast({ kind: 'success', message: `action-${i}`, action: { label: `动作${i}`, run: () => {} } })
    }
    const snapshot = toast.getToastSnapshot()
    assert.equal(snapshot.length, 4, 'action toasts cap at MAX_ACTION_VISIBLE')
    assert.deepEqual(snapshot.map((item) => item.message), ['action-4', 'action-5', 'action-6', 'action-7'], 'oldest action toasts dropped first')
  }

  console.log('toast store tests passed')
} finally {
  await rm(tempDir, { recursive: true, force: true })
}
