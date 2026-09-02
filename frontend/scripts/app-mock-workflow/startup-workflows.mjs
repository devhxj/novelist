import assert from 'node:assert/strict'
import path from 'node:path'
import { newAppPage, outputDir } from './app-harness.mjs'
import { mockImportRecoveryResult } from './fixtures.mjs'
import { settingsFixture } from './mock-bridge.mjs'
import { expectVisible, waitForBridgeCall } from './page-helpers.mjs'

export async function verifyBootstrapStates(browser, url, consoleErrors, pageErrors) {
  const initPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: false,
    platformDefaultPath: 'D:\\NovelistBootstrap',
    afterInitializeNovels: [],
    afterInitializeSettings: settingsFixture(0),
  })
  await initPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(initPage.getByText('欢迎使用 Novelist'), 'initialization screen')
  await expectVisible(initPage.getByText('D:\\NovelistBootstrap'), 'default data directory')
  await initPage.getByRole('button', { name: '开始使用' }).click()
  await expectVisible(initPage.getByText('还没有作品，创建第一部吧'), 'empty bookshelf after initialization')
  await waitForBridgeCall(initPage, 'Initialize')
  await initPage.close()

  const emptyPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    novels: [],
    settings: settingsFixture(0),
  })
  await emptyPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(emptyPage.getByText('还没有作品，创建第一部吧'), 'empty workspace bookshelf')
  await expectVisible(emptyPage.getByText('选择作品开始对话'), 'chat empty novel state')
  await emptyPage.close()

  const startupRecoveryPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    importRecovery: mockImportRecoveryResult(),
  }, undefined, 'bootstrap-import-recovery')
  await startupRecoveryPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(startupRecoveryPage.getByRole('heading', { name: '导入恢复已处理' }), 'startup import recovery heading')
  await expectVisible(startupRecoveryPage.getByText('已清理 1 个未完成导入'), 'startup import recovery cleaned count')
  await expectVisible(startupRecoveryPage.getByText('1 个导入需要手动处理'), 'startup import recovery blocked count')
  await expectVisible(startupRecoveryPage.getByText('startup-blocked-import'), 'startup import recovery blocked task id')
  await startupRecoveryPage.getByRole('button', { name: '复制诊断' }).click()
  await expectVisible(startupRecoveryPage.getByRole('button', { name: '已复制' }), 'startup import recovery copied state')
  await startupRecoveryPage.screenshot({ path: path.join(outputDir, 'app-00-import-recovery.png'), fullPage: true })
  await startupRecoveryPage.close()

  const startupErrorPage = await newAppPage(browser, consoleErrors, pageErrors, {
    failIsInitialized: true,
  })
  await startupErrorPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(startupErrorPage.getByRole('heading', { name: '启动检查失败' }), 'startup failure heading')
  await expectVisible(startupErrorPage.getByText('初始化状态读取失败'), 'startup failure detail')
  await startupErrorPage.getByRole('button', { name: '重试' }).click()
  await expectVisible(startupErrorPage.getByRole('heading', { name: '启动检查失败' }), 'startup retry failure')
  await waitForBridgeCall(startupErrorPage, 'IsInitialized')
  await startupErrorPage.close()

  const corruptRecoveryPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      IsInitialized: [{ mode: 'malformed-response' }, { mode: 'malformed-response' }],
    },
  }, undefined, 'bootstrap-corrupt-recovery')
  await corruptRecoveryPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(corruptRecoveryPage.getByRole('heading', { name: '启动检查失败' }), 'corrupt startup failure heading')
  await expectVisible(corruptRecoveryPage.getByText(/Bridge response is missing an ok flag/), 'corrupt startup failure detail')
  await corruptRecoveryPage.screenshot({ path: path.join(outputDir, 'app-00-corrupt-startup.png'), fullPage: true })
  await corruptRecoveryPage.evaluate(() => window.__appMockState.clearFaultQueue?.('IsInitialized'))
  await corruptRecoveryPage.getByRole('button', { name: '重试' }).click()
  await expectVisible(corruptRecoveryPage.getByText('全局回归小说'), 'workspace after corrupt startup retry')
  await expectVisible(corruptRecoveryPage.getByText('AI 对话'), 'chat panel after corrupt startup retry')
  const corruptCalls = await corruptRecoveryPage.evaluate(() =>
    window.__appMockState.calls.filter((call) => call.method === 'IsInitialized').length)
  assert(corruptCalls >= 2, `Expected corrupt startup retry to call IsInitialized at least twice, got ${corruptCalls}.`)
  await corruptRecoveryPage.close()

  const bridgeUnavailablePage = await newAppPage(browser, consoleErrors, pageErrors)
  await bridgeUnavailablePage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(bridgeUnavailablePage.getByRole('heading', { name: '无法连接桌面桥接' }), 'bridge unavailable heading')
  await expectVisible(bridgeUnavailablePage.getByText('请确认正在通过 Novelist 桌面应用打开此界面'), 'bridge unavailable guidance')
  await bridgeUnavailablePage.screenshot({ path: path.join(outputDir, 'app-00-bootstrap.png'), fullPage: true })
  await bridgeUnavailablePage.close()

  await verifyPlatformProbeRecovery(browser, url, consoleErrors, pageErrors)
  await verifyLastSessionRestore(browser, url, consoleErrors, pageErrors)
}

// F12：设置里带 last_session_id 时，重新打开工作区必须恢复上次会话。
// 旧实现把该 ID 塞进 ref，会话列表 effect 可能带着空值先跑完，恢复被静默跳过。
async function verifyLastSessionRestore(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    settings: { ...settingsFixture(42), last_session_id: 'session-last-42' },
  }, undefined, 'last-session-restore')
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'workspace title before session restore')

  // 恢复生效的可观察结果：先按上次会话 ID 查详情，再加载该会话的历史消息。
  await waitForBridgeCall(page, 'GetSessionMessages')
  const restored = await page.evaluate(() => {
    const calls = window.__appMockState.calls
    const getSession = calls.filter((call) => call.method === 'GetSession').at(-1) ?? null
    const messages = calls.filter((call) => call.method === 'GetSessionMessages').at(-1) ?? null
    return { getSessionId: getSession?.args?.[0] ?? null, messagesSessionId: messages?.args?.[0] ?? null }
  })
  assert.equal(restored.getSessionId, 'session-last-42', 'the last session id from settings must be restored')
  assert.equal(restored.messagesSessionId, 'session-last-42', 'the restored session must load its history')
  await page.close()
}

// 默认目录探测失败过去会把首屏钉死在"加载中..."，按钮永久禁用。
// 这里断言错误文案、重试按钮与手填目录兜底三条出路都在。
async function verifyPlatformProbeRecovery(browser, url, consoleErrors, pageErrors) {
  const retryPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: false,
    platformDefaultPath: 'D:\\NovelistRecovered',
    afterInitializeNovels: [],
    afterInitializeSettings: settingsFixture(0),
    faults: {
      GetPlatform: [{ mode: 'storage', message: '平台信息读取失败' }],
    },
  }, undefined, 'init-platform-probe-failure')
  await retryPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(retryPage.getByText('欢迎使用 Novelist'), 'initialization screen with failed platform probe')
  await expectVisible(retryPage.getByText('无法自动确定数据目录', { exact: true }), 'platform probe failure title')
  await expectVisible(retryPage.getByText('平台信息读取失败'), 'platform probe failure detail')

  const manualInput = retryPage.getByLabel('手动填写创作数据目录')
  await expectVisible(manualInput, 'manual data directory input')
  const retryButton = retryPage.getByRole('button', { name: '重新检测' })
  await expectVisible(retryButton, 'platform probe retry button')
  assert.equal(await retryButton.isEnabled(), true, 'the platform probe retry button must stay clickable')
  await retryPage.screenshot({ path: path.join(outputDir, 'app-00-init-platform-failure.png'), fullPage: true })

  await retryButton.click()
  await expectVisible(retryPage.getByText('D:\\NovelistRecovered'), 'default data directory after platform probe retry')
  await retryPage.getByRole('button', { name: '开始使用' }).click()
  await expectVisible(retryPage.getByText('还没有作品，创建第一部吧'), 'bookshelf after recovering from platform probe failure')
  await retryPage.close()

  const manualPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: false,
    afterInitializeNovels: [],
    afterInitializeSettings: settingsFixture(0),
    faults: {
      GetPlatform: [{ mode: 'storage', message: '平台信息读取失败', once: false }],
    },
  }, undefined, 'init-platform-manual-fallback')
  await manualPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(manualPage.getByText('无法自动确定数据目录', { exact: true }), 'persistent platform probe failure title')

  const startButton = manualPage.getByRole('button', { name: '开始使用' })
  assert.equal(await startButton.isDisabled(), true, 'the start button must stay disabled while no directory is known')
  await manualPage.getByLabel('手动填写创作数据目录').fill('D:\\NovelistManual')
  assert.equal(await startButton.isEnabled(), true, 'a manually typed directory must unblock the start button')
  await startButton.click()
  await expectVisible(manualPage.getByText('还没有作品，创建第一部吧'), 'bookshelf after manual data directory fallback')

  const initializeCall = await manualPage.evaluate(() =>
    window.__appMockState.calls.find((call) => call.method === 'Initialize') ?? null)
  assert(initializeCall, 'the manual fallback must reach Initialize')
  assert.equal(initializeCall.args[0], 'D:\\NovelistManual', 'Initialize must receive the manually typed directory')
  await manualPage.close()
}

export async function verifyFixtureFaultModes(browser, url, consoleErrors, pageErrors) {
  const faultPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      FaultSlowProbe: { delayMs: 80 },
      FaultValidationProbe: { mode: 'validation', message: '模拟校验错误' },
      FaultStorageProbe: { mode: 'storage', message: '模拟存储错误' },
      FaultMalformedProbe: { mode: 'malformed-response' },
      FaultTimeoutProbe: { mode: 'timeout' },
      FaultResetProbe: { mode: 'validation', message: '一次性 fixture 错误' },
    },
  }, undefined, 'fixture-fault-modes')

  await faultPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(faultPage.getByText('全局回归小说'), 'fixture fault workspace')

  const success = await invokeProbe(faultPage, 'FaultSuccessProbe')
  assert.equal(success.ok, true, 'default fixture path should succeed')

  const slow = await invokeProbe(faultPage, 'FaultSlowProbe')
  assert.equal(slow.ok, true, 'slow fixture path should still succeed')
  assert(slow.elapsedMs >= 40, `slow fixture should delay the response, got ${slow.elapsedMs}ms`)

  const validation = await invokeProbe(faultPage, 'FaultValidationProbe')
  assert.equal(validation.ok, false, 'validation fixture should reject')
  assert.equal(validation.code, 'VALIDATION_ERROR')
  assert.match(validation.message, /模拟校验错误/)

  const storage = await invokeProbe(faultPage, 'FaultStorageProbe')
  assert.equal(storage.ok, false, 'storage fixture should reject')
  assert.equal(storage.code, 'STORAGE_ERROR')
  assert.match(storage.message, /模拟存储错误/)

  const malformed = await invokeProbe(faultPage, 'FaultMalformedProbe')
  assert.equal(malformed.ok, false, 'malformed fixture response should reject')
  assert.equal(malformed.code, 'INVALID_BRIDGE_RESPONSE')
  assert.match(malformed.message, /missing an ok flag/)

  const timeout = await invokeProbe(faultPage, 'FaultTimeoutProbe', 20)
  assert.equal(timeout.ok, false, 'timeout fixture should reject')
  assert.equal(timeout.code, 'REQUEST_TIMEOUT')
  assert.equal(timeout.retryable, true)

  const resetFailure = await invokeProbe(faultPage, 'FaultResetProbe')
  assert.equal(resetFailure.ok, false, 'reset probe should fail in the faulted page')
  await faultPage.close()

  const resetPage = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true }, undefined, 'fixture-reset')
  await resetPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(resetPage.getByText('全局回归小说'), 'fixture reset workspace')
  const resetSuccess = await invokeProbe(resetPage, 'FaultResetProbe')
  assert.equal(resetSuccess.ok, true, 'fixture state must reset for a new page')
  await resetPage.close()
}

async function invokeProbe(page, method, timeoutMs = 1_000) {
  return await page.evaluate(
    async ({ method, timeoutMs }) => {
      const startedAt = performance.now()
      try {
        const result = await window.novelist.invoke(method, {}, { timeoutMs })
        return {
          ok: true,
          result,
          elapsedMs: performance.now() - startedAt,
        }
      } catch (error) {
        return {
          ok: false,
          name: error instanceof Error ? error.name : '',
          message: error instanceof Error ? error.message : String(error),
          code: typeof error === 'object' && error !== null && 'code' in error ? error.code : '',
          retryable: typeof error === 'object' && error !== null && 'retryable' in error ? error.retryable : false,
          elapsedMs: performance.now() - startedAt,
        }
      }
    },
    { method, timeoutMs },
  )
}
