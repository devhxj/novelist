import assert from 'node:assert/strict'
import path from 'node:path'
import { newAppPage, outputDir } from './app-harness.mjs'
import {
  assertCopyableDiagnostic,
  assertNoSensitiveDiagnosticsVisible,
  errorAlert,
  installClipboardSpy,
  sensitiveDiagnosticDetails,
} from './diagnostic-helpers.mjs'
import { settingsFixture } from './mock-bridge.mjs'
import {
  assertBridgeCallCount,
  assertButtonDisabled,
  assertLastBridgeCallInput,
  assertNoSavedEmbeddingConfig,
  assertSavedEmbeddingConfig,
  assertSavedLLMConfig,
  assertSettingsCallsUseMockCredentials,
  bridgeCallCount,
  expectHidden,
  expectInputValue,
  expectVisible,
  settingsDialog,
  waitForBridgeCall,
  waitForBridgeCallCountAfter,
} from './page-helpers.mjs'

export async function verifySettingsWorkflow(page) {
  await page.locator('header').getByTitle('设置').click()
  await expectVisible(page.getByText('设置').first(), 'settings dialog')
  await expectVisible(page.getByText('基础设置'), 'general tab')
  await expectVisible(page.locator('input[value="D:\\\\NovelistData"]'), 'data directory')
  const dialog = settingsDialog(page)
  await expectVisible(dialog.getByText('Git 提交作者'), 'git author settings section')
  await expectVisible(dialog.getByLabel('作者名称'), 'git author name input')
  await expectVisible(dialog.getByLabel('作者邮箱'), 'git author email input')
  await expectVisible(dialog.getByRole('button', { name: '保存 Git 作者' }), 'git author save button')

  await page.getByRole('button', { name: /模型配置/ }).click()
  await expectVisible(page.getByText('内置服务商'), 'builtin model config')
  await expectVisible(dialog.getByText('Mock Provider'), 'builtin provider name')
  await expectVisible(dialog.getByText('Mock GPT'), 'builtin model name')
  await expectVisible(dialog.getByRole('button', { name: 'Chat' }), 'safe default chat endpoint')
  await assertButtonDisabled(dialog.getByRole('button', { name: '测试' }).first(), 'LLM test before API key')
  await expectVisible(page.getByText('Embeddings'), 'embedding settings tab')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
}

export async function verifySettingsPersistenceWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'settings persistence workspace')

  await page.locator('header').getByTitle('设置').click()
  let dialog = settingsDialog(page)
  await expectVisible(dialog.getByText('基础设置'), 'settings persistence dialog')

  await expectVisible(dialog.getByText('Git 提交作者'), 'git author settings pane')
  const authorName = dialog.getByLabel('作者名称')
  const authorEmail = dialog.getByLabel('作者邮箱')
  await expectInputValue(authorName, '', 'default git author name')
  await expectInputValue(authorEmail, '', 'default git author email')

  const initialGitAuthorSaveCount = await bridgeCallCount(page, 'SaveGitAuthorSettings')
  await authorName.fill('Mock Git Author')
  await dialog.getByRole('button', { name: '保存 Git 作者' }).click()
  await expectVisible(dialog.getByText('Git 作者名称和邮箱必须同时填写'), 'git author paired validation')
  await assertBridgeCallCount(page, 'SaveGitAuthorSettings', initialGitAuthorSaveCount)

  await authorEmail.fill('not an email')
  await dialog.getByRole('button', { name: '保存 Git 作者' }).click()
  await expectVisible(dialog.getByText('请输入有效的 Git 作者邮箱'), 'git author email validation')
  await assertBridgeCallCount(page, 'SaveGitAuthorSettings', initialGitAuthorSaveCount)

  await authorEmail.fill('mock.git@example.com')
  await dialog.getByRole('button', { name: '保存 Git 作者' }).click()
  await waitForBridgeCallCountAfter(page, 'SaveGitAuthorSettings', initialGitAuthorSaveCount)
  await expectVisible(dialog.getByText('Git 作者设置已保存'), 'git author settings saved')
  await assertLastBridgeCallInput(page, 'SaveGitAuthorSettings', {
    name: 'Mock Git Author',
    email: 'mock.git@example.com',
  })

  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
  await page.reload({ waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'settings persistence workspace after reload')
  await page.locator('header').getByTitle('设置').click()
  dialog = settingsDialog(page)
  await expectVisible(dialog.getByText('基础设置'), 'settings dialog after reload')
  await expectInputValue(dialog.getByLabel('作者名称'), 'Mock Git Author', 'persisted git author name')
  await expectInputValue(dialog.getByLabel('作者邮箱'), 'mock.git@example.com', 'persisted git author email')

  const clearGitAuthorSaveCount = await bridgeCallCount(page, 'SaveGitAuthorSettings')
  await dialog.getByLabel('作者名称').fill('')
  await dialog.getByLabel('作者邮箱').fill('')
  await dialog.getByRole('button', { name: '保存 Git 作者' }).click()
  await waitForBridgeCallCountAfter(page, 'SaveGitAuthorSettings', clearGitAuthorSaveCount)
  await expectVisible(dialog.getByText('Git 作者设置已清空，将使用默认身份'), 'git author settings cleared')
  await assertLastBridgeCallInput(page, 'SaveGitAuthorSettings', {
    name: '',
    email: '',
  })

  await dialog.getByRole('button', { name: /模型配置/ }).click()
  await expectVisible(dialog.getByText('内置服务商'), 'model settings pane')

  await dialog.getByRole('button', { name: '保存配置' }).click()
  await expectVisible(dialog.getByText('请先配置至少一个服务商的 API Key'), 'missing credential validation')
  await assertBridgeCallCount(page, 'SaveLLMConfig', 0)
  await assertBridgeCallCount(page, 'TestConnection', 0)

  await dialog.getByPlaceholder('输入 API Key').first().fill('mock-settings-key')
  await dialog.getByRole('button', { name: 'Responses' }).click()
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await expectVisible(dialog.getByText('配置已保存'), 'model settings saved')
  await waitForBridgeCall(page, 'TestConnection')
  await waitForBridgeCall(page, 'SaveLLMConfig')
  await assertSavedLLMConfig(page, {
    providerKey: 'mock',
    apiKey: 'mock-settings-key',
    endpointType: 'responses',
  })
  await assertLastBridgeCallInput(page, 'TestConnection', {
    provider_name: 'mock',
    api_key: 'mock-settings-key',
    endpoint_type: 'responses',
    model_id: 'gpt',
  })

  await dialog.getByRole('button', { name: 'Embeddings' }).click()
  await expectVisible(dialog.getByText('sqlite-vec 已就绪'), 'sqlite vec ready')
  await expectVisible(dialog.getByText('BGE Small · CPU'), 'default onnx embedding profile')
  await expectVisible(dialog.getByText('512 维 · 512 tokens · INT8'), 'default onnx embedding profile summary')
  await dialog.getByRole('button', { name: '测试' }).click()
  await expectVisible(dialog.getByText('✓ 连通成功'), 'embedding test success')
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await expectVisible(dialog.getByText('配置已保存'), 'embedding settings saved')
  await waitForBridgeCall(page, 'TestEmbeddingConnection')
  await waitForBridgeCall(page, 'SaveEmbeddingConfig')
  await assertSavedEmbeddingConfig(page, {
    provider_type: 'onnx',
    provider_key: 'onnx',
    model_id: 'bge-small-zh-v1.5',
    dimensions: 512,
    max_sequence_length: 512,
    normalize_embeddings: true,
  })

  const onnxSaveCount = await bridgeCallCount(page, 'SaveEmbeddingConfig')
  const enhancedProfileButton = dialog.getByRole('button', { name: /提高/ })
  await enhancedProfileButton.click()
  assert.equal(await enhancedProfileButton.getAttribute('aria-pressed'), 'true')
  await expectVisible(dialog.getByText('Qwen3 0.6B · DirectML'), 'enhanced embedding DirectML provider')
  await expectVisible(dialog.getByText('1024 维 · 4096 tokens · FP16'), 'enhanced embedding profile')
  await page.waitForTimeout(200)
  await page.screenshot({ path: path.join(outputDir, 'app-05-embedding-directml.png'), fullPage: true })
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await waitForBridgeCallCountAfter(page, 'SaveEmbeddingConfig', onnxSaveCount)
  await expectVisible(dialog.getByText('配置已保存'), 'enhanced embedding profile saved')
  await assertSavedEmbeddingConfig(page, {
    provider_type: 'onnx',
    provider_key: 'onnx',
    model_id: 'Qwen/Qwen3-Embedding-0.6B',
    dimensions: 1024,
    max_sequence_length: 4096,
  })

  await dialog.getByRole('button', { name: 'API' }).click()
  await assertButtonDisabled(dialog.getByRole('button', { name: '测试' }), 'embedding API test before credentials')
  await dialog.locator('#embedding-provider').fill('mock-api-embedding')
  await dialog.locator('#embedding-url').fill('https://embeddings.invalid/v1')
  await dialog.locator('#embedding-api-key').fill('mock-embedding-key')
  await dialog.locator('#embedding-model').fill('mock-embedding-v2')
  await dialog.locator('#embedding-dimensions').fill('1536')
  await dialog.locator('#embedding-user').fill('phase13-settings')
  const embeddingTestCount = await bridgeCallCount(page, 'TestEmbeddingConnection')
  const embeddingSaveCount = await bridgeCallCount(page, 'SaveEmbeddingConfig')
  await dialog.getByRole('button', { name: '测试' }).click()
  await waitForBridgeCallCountAfter(page, 'TestEmbeddingConnection', embeddingTestCount)
  await expectVisible(dialog.getByText('✓ 连通成功'), 'embedding API test success')
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await waitForBridgeCallCountAfter(page, 'SaveEmbeddingConfig', embeddingSaveCount)
  await expectVisible(dialog.getByText('配置已保存'), 'embedding API settings saved')
  await assertLastBridgeCallInput(page, 'TestEmbeddingConnection', {
    provider_type: 'api',
    provider_key: 'mock-api-embedding',
    endpoint_url: 'https://embeddings.invalid/v1',
    api_key: 'mock-embedding-key',
    model_id: 'mock-embedding-v2',
    dimensions: 1536,
    user: 'phase13-settings',
  })
  await assertSavedEmbeddingConfig(page, {
    provider_type: 'api',
    provider_key: 'mock-api-embedding',
    endpoint_url: 'https://embeddings.invalid/v1',
    api_key: 'mock-embedding-key',
    model_id: 'mock-embedding-v2',
    dimensions: 1536,
    user: 'phase13-settings',
    normalize_embeddings: true,
  })

  await assertBridgeCallCount(page, 'DiscoverModels', 0)
  await assertBridgeCallCount(page, 'PickReferenceSourceFile', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertSettingsCallsUseMockCredentials(page)
  await page.close()
}

export async function verifySettingsFailureWorkflow(browser, url, consoleErrors, pageErrors) {
  const details = sensitiveDiagnosticDetails()
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      TestConnection: {
        mode: 'validation',
        message: '模拟模型连通失败：Bearer model-test-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      SaveLLMConfig: {
        mode: 'storage',
        message: '模拟模型配置保存失败：Bearer model-save-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      DiscoverModels: {
        mode: 'storage',
        message: '模拟模型发现失败：Bearer model-discovery-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      TestEmbeddingConnection: {
        mode: 'validation',
        message: '模拟 Embedding 连通失败：Bearer embedding-test-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      SaveEmbeddingConfig: {
        mode: 'storage',
        message: '模拟 Embedding 保存失败：Bearer embedding-save-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      SaveGitAuthorSettings: {
        mode: 'storage',
        message: '模拟 Git 作者保存失败：Bearer git-author-save-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
    },
  }, undefined, 'settings-failures')
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'settings failure workspace')

  await page.locator('header').getByTitle('设置').click()
  const dialog = settingsDialog(page)
  await expectVisible(dialog.getByText('基础设置'), 'settings failure dialog')
  await dialog.getByLabel('作者名称').fill('Failed Git Author')
  await dialog.getByLabel('作者邮箱').fill('failed.git@example.com')
  await dialog.getByRole('button', { name: '保存 Git 作者' }).click()
  await waitForBridgeCall(page, 'SaveGitAuthorSettings')
  const gitAuthorAlert = errorAlert(dialog, '模拟 Git 作者保存失败')
  await expectVisible(gitAuthorAlert, 'git author save failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, gitAuthorAlert, 'SaveGitAuthorSettings')

  await dialog.getByRole('button', { name: /模型配置/ }).click()

  await dialog.getByPlaceholder('输入 API Key').first().fill('mock-settings-key')
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await waitForBridgeCall(page, 'TestConnection')
  await expectVisible(dialog.getByText(/Mock Provider 连通性测试失败:.*模拟模型连通失败/), 'LLM connection failure message')
  const llmTestAlert = errorAlert(dialog, '模拟模型连通失败')
  await expectVisible(llmTestAlert, 'LLM connection failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, llmTestAlert, 'TestConnection')
  await assertBridgeCallCount(page, 'SaveLLMConfig', 0)

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('TestConnection') })
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await waitForBridgeCall(page, 'SaveLLMConfig')
  const llmSaveAlert = errorAlert(dialog, '模拟模型配置保存失败')
  await expectVisible(llmSaveAlert, 'LLM save failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, llmSaveAlert, 'SaveLLMConfig')

  const discoverCount = await bridgeCallCount(page, 'DiscoverModels')
  await dialog.getByRole('button', { name: '自动发现' }).click()
  await waitForBridgeCallCountAfter(page, 'DiscoverModels', discoverCount)
  const discoverAlert = errorAlert(dialog, '模拟模型发现失败')
  await expectVisible(discoverAlert, 'model discovery failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, discoverAlert, 'DiscoverModels')

  await dialog.getByRole('button', { name: 'Embeddings' }).click()
  const embeddingTestCount = await bridgeCallCount(page, 'TestEmbeddingConnection')
  await dialog.getByRole('button', { name: '测试' }).click()
  await waitForBridgeCallCountAfter(page, 'TestEmbeddingConnection', embeddingTestCount)
  const embeddingTestAlert = errorAlert(dialog, '模拟 Embedding 连通失败')
  await expectVisible(embeddingTestAlert, 'embedding test failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, embeddingTestAlert, 'TestEmbeddingConnection')

  await dialog.getByRole('button', { name: '保存配置' }).click()
  await waitForBridgeCall(page, 'SaveEmbeddingConfig')
  const embeddingSaveAlert = errorAlert(dialog, '模拟 Embedding 保存失败')
  await expectVisible(embeddingSaveAlert, 'embedding save failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, embeddingSaveAlert, 'SaveEmbeddingConfig')
  await assertNoSavedEmbeddingConfig(page)

  assert((await bridgeCallCount(page, 'DiscoverModels')) >= 1, 'settings failure workflow should cover model discovery failure')
  await assertBridgeCallCount(page, 'PickReferenceSourceFile', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertSettingsCallsUseMockCredentials(page)
  await page.close()
}

export async function verifyUpdateWorkflow(page, browser, url, consoleErrors, pageErrors) {
  await expectVisible(page.getByRole('heading', { name: '发现新版本 v2.0.0' }), 'automatic update dialog')
  await assertLastBridgeCallInput(page, 'CheckForUpdates', {
    manual: false,
  })
  const dismissCount = await bridgeCallCount(page, 'SaveUpdateCheckSettings')
  await page.getByRole('button', { name: '忽略此版本' }).click()
  await waitForBridgeCallCountAfter(page, 'SaveUpdateCheckSettings', dismissCount)
  await assertLastBridgeCallInput(page, 'SaveUpdateCheckSettings', {
    enabled: true,
    endpoint_url: 'https://updates.example.test/latest',
    dismissed_version: 'v2.0.0',
  })
  await expectHidden(page.getByRole('heading', { name: '发现新版本 v2.0.0' }), 'dismissed automatic update dialog')

  await page.locator('header').getByTitle('设置').click()
  const dialog = settingsDialog(page)
  await expectVisible(dialog.getByText('更新检查'), 'update settings section')
  await expectInputValue(dialog.locator('#update-check-endpoint'), 'https://updates.example.test/latest', 'persisted update endpoint')

  await dialog.locator('#update-check-endpoint').fill('file:///tmp/latest.json')
  await dialog.getByRole('button', { name: '立即检查' }).click()
  await expectVisible(dialog.getByText('更新检查 endpoint 必须是 HTTPS 地址'), 'update endpoint validation')

  await dialog.locator('#update-check-endpoint').fill('https://updates.example.test/latest')
  await page.evaluate(() => { window.__appMockState.nextUpdateCheckMode = 'no_update' })
  const manualNoUpdateCount = await bridgeCallCount(page, 'CheckForUpdates')
  await dialog.getByRole('button', { name: '立即检查' }).click()
  await waitForBridgeCallCountAfter(page, 'CheckForUpdates', manualNoUpdateCount)
  await expectVisible(dialog.getByText('当前已是最新版本'), 'manual no-update result')
  await assertLastBridgeCallInput(page, 'CheckForUpdates', {
    manual: true,
  })

  await page.evaluate(() => { window.__appMockState.nextUpdateCheckMode = 'failed' })
  const manualFailureCount = await bridgeCallCount(page, 'CheckForUpdates')
  await dialog.getByRole('button', { name: '立即检查' }).click()
  await waitForBridgeCallCountAfter(page, 'CheckForUpdates', manualFailureCount)
  const manualFailureAlert = errorAlert(dialog, '更新检查失败')
  await expectVisible(manualFailureAlert, 'manual update failure callout')
  await expectVisible(manualFailureAlert.getByText('模拟更新检查失败'), 'manual update failure result')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, manualFailureAlert, 'CheckForUpdates')

  await page.evaluate(() => { window.__appMockState.nextUpdateCheckMode = 'available' })
  const manualAvailableCount = await bridgeCallCount(page, 'CheckForUpdates')
  await dialog.getByRole('button', { name: '立即检查' }).click()
  await waitForBridgeCallCountAfter(page, 'CheckForUpdates', manualAvailableCount)
  await expectVisible(page.getByRole('heading', { name: '发现新版本 v2.0.0' }), 'manual update dialog')
  await expectVisible(page.getByText('安全更新'), 'release notes rendered as markdown text')
  const openCount = await bridgeCallCount(page, 'runtime.shell.openExternal')
  await page.getByRole('button', { name: '查看发布页' }).click()
  await waitForBridgeCallCountAfter(page, 'runtime.shell.openExternal', openCount)
  const openedRelease = await page.evaluate(() => {
    const calls = window.__appMockState.calls.filter((call) => call.method === 'runtime.shell.openExternal')
    return calls.at(-1)?.payload?.url ?? null
  })
  assert.equal(openedRelease, 'https://updates.example.test/releases/v2.0.0')
  const updateAlert = errorAlert(page, '更新操作失败')
  await expectVisible(updateAlert, 'update release open error callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, updateAlert, 'runtime.shell.openExternal')
  await page.getByRole('button', { name: '关闭更新提示' }).click()
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()

  const noUpdatePage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    settings: {
      ...settingsFixture(42),
      update_check_enabled: true,
      update_check_endpoint_url: 'https://updates.example.test/latest',
    },
    updateCheckMode: 'no_update',
  }, undefined, 'update-auto-no-update')
  await noUpdatePage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(noUpdatePage.getByText('全局回归小说'), 'no-update workspace')
  await expectHidden(noUpdatePage.getByText('发现新版本'), 'no automatic dialog when current version is latest')
  await assertBridgeCallCount(noUpdatePage, 'runtime.shell.openExternal', 0)
  await noUpdatePage.close()

  const settingsFailurePage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    settings: {
      ...settingsFixture(42),
      update_check_enabled: false,
      update_check_endpoint_url: '',
    },
    faults: {
      SaveUpdateCheckSettings: {
        mode: 'storage',
        code: 'UPDATE_SETTINGS_SAVE_FAILED',
        message: '更新设置保存失败：Bearer update-settings-token-abcdefghijklmnopqrstuvwxyz',
        details: sensitiveDiagnosticDetails(),
        retryable: true,
      },
    },
  }, undefined, 'update-settings-failure')
  await installClipboardSpy(settingsFailurePage)
  await settingsFailurePage.goto(url, { waitUntil: 'domcontentloaded' })
  await settingsFailurePage.locator('header').getByTitle('设置').click()
  const failureDialog = settingsDialog(settingsFailurePage)
  await failureDialog.locator('#update-check-endpoint').fill('https://updates.example.test/latest')
  await failureDialog.getByRole('button', { name: '保存更新设置' }).click()
  await waitForBridgeCall(settingsFailurePage, 'SaveUpdateCheckSettings')
  const saveFailureAlert = errorAlert(failureDialog, '更新检查设置保存失败')
  await expectVisible(saveFailureAlert, 'update settings save failure callout')
  await assertNoSensitiveDiagnosticsVisible(settingsFailurePage)
  await assertCopyableDiagnostic(settingsFailurePage, saveFailureAlert, 'SaveUpdateCheckSettings')
  await assertBridgeCallCount(settingsFailurePage, 'runtime.shell.openExternal', 0)
  await assertBridgeCallCount(settingsFailurePage, 'SaveContent', 0)
  await settingsFailurePage.close()
}
