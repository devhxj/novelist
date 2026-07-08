import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import fs from 'node:fs/promises'
import net from 'node:net'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { chromium } from 'playwright'
import { verifyErrorFeedbackWorkflow } from './app-mock-workflow/error-feedback.mjs'
import { installConfigurableAppMockBridge, settingsFixture } from './app-mock-workflow/mock-bridge.mjs'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const frontendRoot = path.resolve(__dirname, '..')
const repoRoot = path.resolve(frontendRoot, '..')
const phaseOutputRoot = path.join(repoRoot, 'output', 'playwright', 'phase13')
const runConfig = parseRunConfig(process.argv.slice(2))
const outputDir = path.join(phaseOutputRoot, artifactRunName(runConfig))
const diagnostics = {
  consoleErrors: [],
  consoleWarnings: [],
  pageErrors: [],
  failedRequests: [],
}
const openPages = new Set()
let pageSequence = 0

async function main() {
  await fs.mkdir(outputDir, { recursive: true })
  await fs.mkdir(path.join(outputDir, 'bridge-calls'), { recursive: true })
  await fs.mkdir(path.join(outputDir, 'traces'), { recursive: true })

  const port = await getFreePort()
  const server = startServer(port, runConfig.target)
  const url = `http://127.0.0.1:${port}/`
  let browser

  try {
    logStep(`waiting for ${runConfig.target} server`)
    await waitForServer(url, server)
    browser = await launchBrowser()

    if (runConfig.suite === 'smoke') {
      await runSmokeSuite(browser, url)
    } else if (runConfig.suite === 'stress') {
      await runStressSuite(browser, url)
    } else if (runConfig.suite === 'usability') {
      await runUsabilitySuite(browser, url)
    } else {
      await runFullSuite(browser, url)
    }

    await writeRunDiagnostics()
    assert.deepEqual(diagnostics.pageErrors, [], `Unexpected page errors:\n${diagnostics.pageErrors.join('\n')}`)
    assert.deepEqual(diagnostics.consoleErrors, [], `Unexpected console errors:\n${diagnostics.consoleErrors.join('\n')}`)
    assert.deepEqual(diagnostics.failedRequests, [], `Unexpected failed requests:\n${diagnostics.failedRequests.join('\n')}`)
    console.log(`App ${runConfig.suite} mock workflow passed. Artifacts: ${path.relative(repoRoot, outputDir)}`)
  } catch (error) {
    await closeOpenPages()
    await writeRunDiagnostics().catch((diagnosticError) => {
      console.error('Failed to write diagnostics after app mock failure:', diagnosticError)
    })
    throw error
  } finally {
    await browser?.close()
    stopProcess(server)
  }
}

async function runSmokeSuite(browser, url) {
  const { consoleErrors, pageErrors } = diagnostics

  logStep('checking bootstrap states')
  await verifyBootstrapStates(browser, url, consoleErrors, pageErrors)

  logStep('checking fixture fault modes')
  await verifyFixtureFaultModes(browser, url, consoleErrors, pageErrors)

  logStep('loading workspace')
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true }, undefined, 'smoke-shell')
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'workspace title')
  await expectVisible(page.getByText('AI 对话'), 'chat panel')
  await page.screenshot({ path: path.join(outputDir, 'app-smoke-01-shell.png'), fullPage: true })

  logStep('checking shell navigation')
  await verifyShellNavigation(page)

  logStep('checking chapter/editor path')
  await verifyChapterWorkflow(page)
  await page.screenshot({ path: path.join(outputDir, 'app-smoke-02-editor.png'), fullPage: true })

  logStep('checking smoke guardrails')
  await verifySmokeBridgeCalls(page)
  await page.close()
}

function fullSuiteBridgeOptions() {
  if (runConfig.grep === '@update') {
    return {
      initialized: true,
      settings: {
        ...settingsFixture(42),
        update_check_enabled: true,
        update_check_endpoint_url: 'https://updates.example.test/latest',
      },
      faults: {
        'runtime.shell.openExternal': {
          mode: 'error',
          code: 'UPDATE_RELEASE_OPEN_FAILED',
          message: '打开发布页失败：Bearer open-error-token-abcdefghijklmnopqrstuvwxyz',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
      },
    }
  }

  if (runConfig.grep === '@error') {
    return {
      initialized: true,
      confirmResult: true,
      faults: errorFeedbackFaults(),
    }
  }

  return { initialized: true }
}

function errorFeedbackWorkflowContext(page, browser, url, consoleErrors, pageErrors) {
  return {
    page,
    browser,
    url,
    consoleErrors,
    pageErrors,
    outputDir,
    newAppPage,
    installClipboardSpy,
    sensitiveDiagnosticDetails,
    clickActivity,
    clickCardAction,
    waitForBridgeCall,
    waitForBridgeCallArg,
    waitForBridgeCallCountAfter,
    bridgeCallCount,
    errorAlert,
    expectVisible,
    assertNoSensitiveDiagnosticsVisible,
    assertCopyableDiagnostic,
    ensureChapterBlockExpanded,
    chapterButton,
    dispatchNovelImportDrop,
    replaceEditorText,
    shortcutKey,
  }
}

function errorFeedbackFaults() {
  const details = sensitiveDiagnosticDetails()
  return {
    DeleteCharacter: {
      mode: 'storage',
      code: 'CHARACTER_DELETE_FAILED',
      message: '角色删除失败：Bearer live-error-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: true,
    },
    DeleteLocation: {
      mode: 'storage',
      code: 'LOCATION_DELETE_FAILED',
      message: '地点删除失败：sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890',
      details,
      retryable: true,
    },
    DeleteSkill: {
      mode: 'storage',
      code: 'SKILL_DELETE_FAILED',
      message: '技能删除失败：sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890',
      details,
      retryable: true,
    },
    CreateNovel: {
      mode: 'storage',
      code: 'NOVEL_CREATE_FAILED',
      message: '创建作品失败：Bearer novel-create-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: true,
    },
    UpdateNovel: {
      mode: 'storage',
      code: 'NOVEL_UPDATE_FAILED',
      message: '更新作品失败：Bearer novel-update-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: true,
    },
    DeleteNovel: {
      mode: 'storage',
      code: 'NOVEL_DELETE_FAILED',
      message: '删除作品失败：Bearer novel-delete-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: true,
    },
    CreateStyleSample: {
      mode: 'storage',
      code: 'STYLE_SAMPLE_CREATE_FAILED',
      message: '保存风格样本失败：Bearer style-sample-create-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: true,
    },
    UpdateStyleSample: {
      mode: 'storage',
      code: 'STYLE_SAMPLE_UPDATE_FAILED',
      message: '保存风格样本失败：Bearer style-sample-update-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: true,
    },
    DeleteStyleSample: {
      mode: 'storage',
      code: 'STYLE_SAMPLE_DELETE_FAILED',
      message: '删除风格样本失败：Bearer style-sample-delete-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: true,
    },
    UpdateChapterTitle: {
      mode: 'validation',
      code: 'CHAPTER_RENAME_FAILED',
      message: '章节重命名失败：Bearer rename-error-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: false,
      once: false,
    },
    StartNovelImport: {
      mode: 'storage',
      code: 'IMPORT_PARSE_FAILED',
      message: '导入失败：Bearer import-error-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: false,
      once: false,
    },
    StartNarrativePatternExtraction: {
      mode: 'error',
      code: 'PATTERN_EXTRACTION_FAILED',
      message: '叙事模式抽取失败：sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890',
      details,
      retryable: true,
      once: false,
    },
    ExtractStyleSkillFromSamples: {
      mode: 'error',
      code: 'STYLE_SKILL_EXTRACTION_FAILED',
      message: '风格技能抽取失败：Bearer style-error-token-abcdefghijklmnopqrstuvwxyz',
      details,
      retryable: true,
      once: false,
    },
  }
}

function sensitiveDiagnosticDetails() {
  return {
    api_key: 'sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890',
    authorization: 'Bearer detail-error-token-abcdefghijklmnopqrstuvwxyz',
    source_text: '敏感源文本'.repeat(300),
    nested: {
      password: 'open-sesame-secret',
      token: 'detail-token-abcdefghijklmnopqrstuvwxyz',
    },
  }
}

async function installClipboardSpy(page) {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', {
      configurable: true,
      value: {
        async writeText(text) {
          window.__appMockClipboardText = String(text)
        },
      },
    })
  })
}

async function runFullSuite(browser, url) {
  const { consoleErrors, pageErrors } = diagnostics
  const shouldRun = makeTagFilter(runConfig.grep)

  if (shouldRun('@startup')) {
    logStep('checking bootstrap states')
    await verifyBootstrapStates(browser, url, consoleErrors, pageErrors)
  }

  if (shouldRun('@diagnostics')) {
    logStep('checking fixture fault modes')
    await verifyFixtureFaultModes(browser, url, consoleErrors, pageErrors)
  }

  logStep('loading workspace')
  const page = await newAppPage(browser, consoleErrors, pageErrors, fullSuiteBridgeOptions(), undefined, 'full-shell')
  if (runConfig.grep === '@error' || runConfig.grep === '@update') {
    await installClipboardSpy(page)
  }
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'workspace title')
  await expectVisible(page.getByText('AI 对话'), 'chat panel')
  await page.screenshot({ path: path.join(outputDir, 'app-01-shell.png'), fullPage: true })

  if (shouldRun('@surface')) {
    logStep('checking shell navigation')
    await verifyShellNavigation(page)
  }

  if (shouldRun('@surface') || shouldRun('@writing')) {
    logStep('checking chapter/editor path')
    await verifyChapterWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-02-editor.png'), fullPage: true })
  }

  if (shouldRun('@writing')) {
    logStep('checking explicit editor save path')
    await verifyEditorSaveWorkflow(browser, url, consoleErrors, pageErrors)
  }

  if (shouldRun('@surface')) {
    logStep('checking novel and chapter workflow')
    await verifyNovelChapterWorkflow(browser, url, consoleErrors, pageErrors)
  }

  if (shouldRun('@surface')) {
    logStep('checking import export and file-picker paths')
    await verifyImportExportFilePickerWorkflow(browser, url, consoleErrors, pageErrors)
  }

  if (shouldRun('@surface')) {
    logStep('checking search path')
    await verifySearchWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-03-search.png'), fullPage: true })
  }

  if (shouldRun('@surface')) {
    logStep('checking chat path')
    await verifyChatWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-04-chat.png'), fullPage: true })
  }

  if (shouldRun('@surface')) {
    logStep('checking settings path')
    await verifySettingsWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-05-settings.png'), fullPage: true })
  }

  if (shouldRun('@surface')) {
    logStep('checking settings persistence path')
    await verifySettingsPersistenceWorkflow(browser, url, consoleErrors, pageErrors)
  }

  if (shouldRun('@surface')) {
    logStep('checking settings failure path')
    await verifySettingsFailureWorkflow(browser, url, consoleErrors, pageErrors)
  }

  if (shouldRun('@surface')) {
    logStep('checking metadata panels')
    await verifyMetadataPanels(page)
    await page.screenshot({ path: path.join(outputDir, 'app-06-metadata.png'), fullPage: true })
  }

  if (shouldRun('@surface')) {
    logStep('checking metadata empty and action paths')
    await verifyMetadataActionWorkflow(browser, url, consoleErrors, pageErrors)
  }

  if (shouldRun('@surface') || shouldRun('@reference-anchor')) {
    logStep('checking reference entry point')
    await verifyReferenceSmoke(page)
    await page.screenshot({ path: path.join(outputDir, 'app-07-reference.png'), fullPage: true })
  }

  if (runConfig.grep === '@reference-anchor') {
    logStep('checking reference error feedback')
    await verifyReferenceErrorFeedbackWorkflow(browser, url, consoleErrors, pageErrors)
  }

  if (shouldRun('@surface')) {
    logStep('checking style sample library')
    await verifyStyleSampleWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-08-style-samples.png'), fullPage: true })
  }

  if (shouldRun('@surface') || shouldRun('@pattern')) {
    logStep('checking multi-range chapter selector')
    await verifyChapterRangeSelectorWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-09-chapter-ranges.png'), fullPage: true })
  }

  if (shouldRun('@surface') || shouldRun('@git')) {
    logStep('checking Git history workflow')
    await verifyGitHistoryWorkflow(page, browser, url, consoleErrors, pageErrors)
    await page.screenshot({ path: path.join(outputDir, 'app-10-git-history.png'), fullPage: true })
  }

  if (runConfig.grep === '@update') {
    logStep('checking update workflow')
    await verifyUpdateWorkflow(page, browser, url, consoleErrors, pageErrors)
    await page.screenshot({ path: path.join(outputDir, 'app-11-update.png'), fullPage: true })
  }

  if (runConfig.grep === '@time') {
    logStep('checking relative-time refresh workflow')
    await verifyRelativeTimeRefreshWorkflow(browser, url, consoleErrors, pageErrors)
  }

  if (runConfig.grep === '@layout') {
    logStep('checking layout persistence workflow')
    await verifyLayoutPersistenceWorkflow(page, browser, url, consoleErrors, pageErrors)
  }

  if (runConfig.grep === '@error') {
    logStep('checking unified error feedback workflow')
    await verifyErrorFeedbackWorkflow(errorFeedbackWorkflowContext(page, browser, url, consoleErrors, pageErrors))
    await page.screenshot({ path: path.join(outputDir, 'app-12-error-feedback.png'), fullPage: true })
  }

  if (shouldRun('@surface')) {
    logStep('checking compact viewport layout')
    await verifyCompactViewportSmoke(browser, url, consoleErrors, pageErrors)
  }

  logStep('checking bridge guardrails')
  if (runConfig.grep === '@startup') {
    await verifyStartupBridgeCalls(page)
  } else if (runConfig.grep === '@diagnostics') {
    await verifyDiagnosticsBridgeCalls(page)
  } else if (runConfig.grep === '@writing') {
    await verifyWritingBridgeCalls(page)
  } else if (runConfig.grep === '@reference-anchor') {
    await verifyReferenceBridgeCalls(page)
  } else if (runConfig.grep === '@pattern') {
    await verifyPatternBridgeCalls(page)
  } else if (runConfig.grep === '@git') {
    await verifyGitBridgeCalls(page)
  } else if (runConfig.grep === '@update') {
    await verifyUpdateBridgeCalls(page)
  } else if (runConfig.grep === '@time') {
    await verifyRelativeTimeBridgeCalls(page)
  } else if (runConfig.grep === '@layout') {
    await verifyLayoutBridgeCalls(page)
  } else if (runConfig.grep === '@error') {
    await verifyErrorBridgeCalls(page)
  } else {
    await verifyBridgeCalls(page)
  }
  await page.close()
}

async function runStressSuite(browser, url) {
  const { consoleErrors, pageErrors } = diagnostics
  const startedAt = Date.now()
  const chapterCount = runConfig.stressChapterCount
  const stressChapterNumber = chapterCount
  const stressTitle = `长篇压力章 ${String(stressChapterNumber).padStart(3, '0')}`
  const stressPath = `chapters/${stressChapterNumber}.md`
  const largeText = makeLargeChineseFixture(runConfig.stressSizeBytes)
  const chapters = makeStressChapters(chapterCount, stressTitle)
  const referenceStress = makeStressReferenceFixture(largeText)
  const page = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    {
      initialized: true,
      settings: settingsFixture(42),
      chaptersByNovelId: { 42: chapters },
      contentByPath: {
        'novelist.md': '## 压力测试故事状态\n用于验证大体量中文正文不会造成白屏。',
        [stressPath]: largeText,
      },
      writingStats: {
        total_words: largeText.length,
        total_days_active: 1,
        current_streak: 1,
        longest_streak: 1,
        total_novels: 1,
        total_chapters: chapterCount,
      },
      referenceStress,
      referenceAnchors: [referenceStress.anchor],
      referenceBuildStatuses: {
        [String(referenceStress.anchor.anchor_id)]: referenceStress.buildStatus,
      },
    },
    { width: 1440, height: 1100 },
    'stress-large-novel',
  )

  logStep(`loading ${Math.round(Buffer.byteLength(largeText, 'utf8') / 1024 / 1024)}MB stress fixture`)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'stress workspace title')
  await clickActivity(page, '章节')
  await expectVisible(page.getByText(`章节 (${chapterCount})`), 'stress chapter count')

  const stressBlockStart = Math.max(1, chapterCount - 99)
  await page.getByRole('button', { name: new RegExp(`第 ${stressBlockStart} - ${chapterCount} 章`) }).click()
  await chapterButton(page, stressTitle).click()
  await expectVisible(page.getByText(`第${stressChapterNumber}章 ${stressTitle}`).first(), 'stress chapter tab')
  await expectVisible(page.locator('.monaco-editor').first(), 'stress monaco editor')
  await waitForBridgeCallArg(page, 'GetContent', 1, stressPath)
  await page.waitForFunction(
    (minLength) => typeof window.__novelistEditor?.getValue === 'function' && window.__novelistEditor.getValue().length >= minLength,
    largeText.length,
    { timeout: 60_000 },
  )
  await page.screenshot({ path: path.join(outputDir, 'app-stress-10mb-editor.png'), fullPage: true })

  logStep('checking 10MB reference material browsing and binding')
  const referenceMetrics = await verifyStressReferenceMaterialPath(page, referenceStress)
  await page.screenshot({ path: path.join(outputDir, 'app-stress-10mb-reference.png'), fullPage: true })

  const callCount = await page.evaluate(() => window.__appMockState.calls.length)
  await fs.writeFile(
    path.join(outputDir, 'stress-metrics.json'),
    `${JSON.stringify({
      targetBytes: runConfig.stressSizeBytes,
      actualBytes: Buffer.byteLength(largeText, 'utf8'),
      characterCount: largeText.length,
      chapterCount,
      selectedPath: stressPath,
      referenceSourcePath: referenceStress.anchor.source_path,
      referenceSourceBytes: referenceStress.sourceBytes,
      referenceSourceCharacters: referenceStress.sourceCharacters,
      referenceSourceSegmentCount: referenceStress.buildStatus.source_segment_count,
      referenceMaterialCount: referenceStress.materialTotal,
      referenceMaterialPages: Math.ceil(referenceStress.materialTotal / 10),
      ...referenceMetrics,
      bridgeCallCount: callCount,
      elapsedMs: Date.now() - startedAt,
    }, null, 2)}\n`,
    'utf8',
  )

  await verifyStressGuardrails(page)
  await page.close()
}

async function runUsabilitySuite(browser, url) {
  const { consoleErrors, pageErrors } = diagnostics
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true }, undefined, 'usability-baseline')
  const observations = []

  logStep('checking usability baseline surfaces')
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'usability workspace title')
  await page.screenshot({ path: path.join(outputDir, 'usability-01-startup.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Startup',
    severity: 'low',
    issueType: 'baseline',
    summary: 'Workspace title, active side panel, and chat panel are visible after the mocked bridge initializes.',
    screenshot: 'usability-01-startup.png',
    reproduction: ['Run `npm --prefix frontend run test:app:usability`.', 'Load the initialized mocked workspace.'],
    expected: 'A returning writer lands in the active workspace with no setup friction.',
    actual: 'The workspace title, chapter side panel, editor, and chat panel are visible.',
    impact: 'Low risk; this is a baseline observation for startup confidence.',
    proposedFix: 'No immediate fix. Keep this screenshot as the startup readability baseline.',
    scores: { discoverability: 5, clickCost: 5, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 3, informationDensity: 4, visualReadability: 4 },
  }))

  await clickActivity(page, '书架')
  await expectVisible(novelCard(page, '全局回归小说'), 'usability bookshelf card')
  await page.screenshot({ path: path.join(outputDir, 'usability-02-bookshelf.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Bookshelf / Novel Management',
    severity: 'low',
    issueType: 'baseline',
    summary: 'The bookshelf exposes the active novel card with direct management affordances.',
    screenshot: 'usability-02-bookshelf.png',
    reproduction: ['Open the initialized workspace.', 'Click the Bookshelf activity.'],
    expected: 'A writer can find the current project and management controls without leaving the workspace.',
    actual: 'The novel card is visible and remains reachable through one activity-bar click.',
    impact: 'Low risk; detailed create, edit, cover, and delete behavior is covered by the full surface suite.',
    proposedFix: 'No immediate fix. Recheck hover-only controls during later accessibility review.',
    scores: { discoverability: 4, clickCost: 5, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 3, informationDensity: 4, visualReadability: 4 },
  }))

  await clickActivity(page, '章节')
  await expectVisible(page.getByText('章节 (6)'), 'usability chapter panel')
  await expectVisible(page.getByRole('button', { name: /故事状态/ }), 'usability story state control')
  await page.getByRole('button', { name: /故事状态/ }).click()
  await waitForBridgeCallArg(page, 'GetContent', 1, 'novelist.md')
  await page.screenshot({ path: path.join(outputDir, 'usability-03-chapters-editor.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Chapters / Editor',
    severity: 'low',
    issueType: 'baseline',
    summary: 'Chapter access is one activity-bar click away; grouped chapters remain visible for the small fixture.',
    screenshot: 'usability-03-chapters-editor.png',
    reproduction: ['Open the initialized workspace.', 'Click the Chapters activity.', 'Open the story-state control.'],
    expected: 'Chapter navigation and story-state access should stay close to the writing surface.',
    actual: 'The chapter list, story-state control, editor, and chat panel remain visible after the task.',
    impact: 'Low risk; this records the normal writing navigation path.',
    proposedFix: 'No immediate fix. Recheck after large-project virtualization work.',
    scores: { discoverability: 5, clickCost: 4, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 3, informationDensity: 4, visualReadability: 4 },
  }))

  await expectVisible(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'usability chat input')
  await page.screenshot({ path: path.join(outputDir, 'usability-04-chat.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Chat / Agent',
    severity: 'low',
    issueType: 'baseline',
    summary: 'The chat input remains visible beside the writing surface without requiring live model access.',
    screenshot: 'usability-04-chat.png',
    reproduction: ['Open the initialized workspace.', 'Click the Chapters activity.', 'Inspect the right-side chat panel.'],
    expected: 'Agent assistance should be immediately reachable while editing or reviewing story state.',
    actual: 'The chat input and panel remain visible next to the editor in the default desktop layout.',
    impact: 'Low risk; streaming, tool cards, cancellation, and retry behavior are covered by the full surface suite.',
    proposedFix: 'No immediate fix. Continue using the full chat workflow for correctness regressions.',
    scores: { discoverability: 5, clickCost: 5, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 4, informationDensity: 4, visualReadability: 4 },
  }))

  await clickActivity(page, '搜索')
  await expectVisible(page.getByPlaceholder('搜索人物、地点、时间线、正文...'), 'usability search input')
  await page.screenshot({ path: path.join(outputDir, 'usability-05-search.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Search',
    severity: 'low',
    issueType: 'baseline',
    summary: 'Search presents a direct input and explicit empty prompt before a query.',
    screenshot: 'usability-05-search.png',
    reproduction: ['Open the initialized workspace.', 'Click the Search activity.'],
    expected: 'Search should make the query affordance obvious before the user remembers exact syntax.',
    actual: 'The search input is visible immediately with a broad placeholder covering people, places, timeline, and prose.',
    impact: 'Low risk; this records the global retrieval entry point.',
    proposedFix: 'No immediate fix. Keep semantic result grouping covered by the full surface suite.',
    scores: { discoverability: 5, clickCost: 5, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 4, informationDensity: 4, visualReadability: 4 },
  }))

  await clickActivity(page, '参考锚定')
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'usability reference heading')
  await page.screenshot({ path: path.join(outputDir, 'usability-06-reference.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Reference Anchor / Corpus',
    severity: 'medium',
    issueType: 'ergonomic-friction',
    summary: 'The reference workflow is reachable, but automatic source-to-material assistance still needs full task scoring.',
    screenshot: 'usability-06-reference.png',
    reproduction: ['Open the initialized workspace.', 'Click the Reference Anchor activity.', 'Inspect the corpus import and orchestration controls.'],
    expected: 'A writer should be able to provide source text and see clear automatic segmentation, material extraction, binding, and audit progress.',
    actual: 'The surface is reachable and exposes corpus/source controls, but Phase 13 still has open work to prove the flow feels automatic rather than manual data plumbing.',
    impact: 'Medium user impact for long-form writers; unclear automation would make reference anchoring feel like setup work instead of writing assistance.',
    proposedFix: 'Complete the reference/corpus workflow tests for source confirmation, automatic segmentation, material extraction, blueprint binding, audit stops, and progress feedback.',
    scores: { discoverability: 4, clickCost: 3, feedbackClarity: 3, errorRecovery: 3, keyboardErgonomics: 2, informationDensity: 3, visualReadability: 4 },
  }))

  await clickActivity(page, '角色')
  await expectVisible(page.getByRole('heading', { name: /角色/ }), 'usability metadata heading')
  await page.screenshot({ path: path.join(outputDir, 'usability-07-metadata.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Metadata Panels',
    severity: 'low',
    issueType: 'baseline',
    summary: 'Metadata panels are reachable from the activity bar and show fixture data without leaving the workspace.',
    screenshot: 'usability-07-metadata.png',
    reproduction: ['Open the initialized workspace.', 'Click the Characters activity.'],
    expected: 'Story metadata should be visible as a workspace surface, not hidden behind modals.',
    actual: 'The character panel renders fixture data and keeps the side activity structure intact.',
    impact: 'Low risk; create, edit, delete, empty, validation, and bridge-failure recovery are covered by the full surface suite.',
    proposedFix: 'No immediate fix. Continue checking dense metadata layouts on compact viewports.',
    scores: { discoverability: 5, clickCost: 5, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 3, informationDensity: 4, visualReadability: 4 },
  }))

  await clickActivity(page, '章节')
  await page.getByRole('button', { name: '导出作品' }).click()
  await expectVisible(page.getByRole('heading', { name: '导出作品' }), 'usability export dialog')
  await page.screenshot({ path: path.join(outputDir, 'usability-08-import-export.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Import / Export / File Picker',
    severity: 'low',
    issueType: 'baseline',
    summary: 'Export is available from the writing surface, while destructive file access remains behind explicit controls.',
    screenshot: 'usability-08-import-export.png',
    reproduction: ['Open the initialized workspace.', 'Click the Chapters activity.', 'Open the Export Novel dialog.'],
    expected: 'Export and file-picker affordances should be explicit and recoverable before bridge calls run.',
    actual: 'The export dialog opens with format choices and cancel/close affordances before any export call is made.',
    impact: 'Low risk; temporary fixture paths and file-picker bridge guardrails are covered by the full surface suite.',
    proposedFix: 'No immediate fix. Keep temporary-path guardrails in the import/export workflow tests.',
    scores: { discoverability: 4, clickCost: 4, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 3, informationDensity: 4, visualReadability: 4 },
  }))
  await page.locator('.fixed').getByRole('button', { name: '取消' }).click()

  await page.locator('header').getByRole('button', { name: '设置' }).click()
  await expectVisible(page.getByText('基础设置'), 'usability settings dialog')
  await page.screenshot({ path: path.join(outputDir, 'usability-09-settings.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Settings',
    severity: 'low',
    issueType: 'baseline',
    summary: 'Settings are reachable from the header and show provider configuration without live-key access.',
    screenshot: 'usability-09-settings.png',
    reproduction: ['Open the initialized workspace.', 'Click the Settings button in the header.'],
    expected: 'Configuration should be reachable without exposing live credentials during QA.',
    actual: 'The settings dialog opens on deterministic mocked configuration with no live-key requirement.',
    impact: 'Low risk; detailed settings persistence and failure behavior are covered in the full surface suite.',
    proposedFix: 'No immediate fix. Keep the mock-key/no-network guardrails in the Settings workflow tests.',
    scores: { discoverability: 4, clickCost: 5, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 3, informationDensity: 4, visualReadability: 4 },
  }))
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()

  const compactPage = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    { initialized: true },
    { width: 900, height: 720 },
    'usability-compact',
  )
  await compactPage.goto(url, { waitUntil: 'domcontentloaded' })
  await clickActivity(compactPage, '章节')
  await expectVisible(compactPage.getByPlaceholder('输入消息，按 / 调用技能...'), 'usability compact chat input')
  await expectVisible(compactPage.getByText('章节 (6)'), 'usability compact chapter side panel')
  await compactPage.screenshot({ path: path.join(outputDir, 'usability-10-compact.png'), fullPage: true })
  observations.push(usabilityObservation({
    surface: 'Compact Desktop Layout',
    severity: 'low',
    issueType: 'baseline',
    summary: 'The compact viewport keeps chapter navigation, editor, and chat reachable without white-screen failure.',
    screenshot: 'usability-10-compact.png',
    reproduction: ['Run the usability suite compact viewport page at 900x720.', 'Open the Chapters activity.'],
    expected: 'A smaller desktop window should preserve navigation and writing controls without incoherent overlap.',
    actual: 'The compact page keeps the chapter panel and chat input visible.',
    impact: 'Low risk; compact transition assertions are also covered by the full surface suite.',
    proposedFix: 'No immediate fix. Recheck after reference/corpus controls are expanded.',
    scores: { discoverability: 4, clickCost: 4, feedbackClarity: 4, errorRecovery: 4, keyboardErgonomics: 3, informationDensity: 3, visualReadability: 4 },
  }))
  await assertBridgeCallCount(compactPage, 'SaveContent', 0)
  await compactPage.close()

  await writeUsabilityReport(observations)
  await verifySmokeBridgeCalls(page)
  await page.close()
}

async function verifyBootstrapStates(browser, url, consoleErrors, pageErrors) {
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
}

async function verifyFixtureFaultModes(browser, url, consoleErrors, pageErrors) {
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

async function newAppPage(
  browser,
  consoleErrors,
  pageErrors,
  bridgeOptions,
  viewport = { width: 1440, height: 1100 },
  pageLabel = 'page',
) {
  const context = await browser.newContext({ viewport })
  await context.tracing.start({ screenshots: true, snapshots: true, sources: true })

  const page = await context.newPage()
  const artifactLabel = `${String(++pageSequence).padStart(2, '0')}-${sanitizeArtifactName(pageLabel)}`
  openPages.add(page)
  page.setDefaultTimeout(runConfig.suite === 'stress' ? 60_000 : 12_000)
  page.on('console', (message) => {
    if (message.type() === 'error') {
      const text = message.text()
      if (!isIgnorableDevServerConsoleError(text)) {
        consoleErrors.push(text)
      }
    } else if (message.type() === 'warning') {
      diagnostics.consoleWarnings.push(message.text())
    }
  })
  page.on('pageerror', (error) => pageErrors.push(error.message))
  page.on('requestfailed', (request) => {
    if (!isIgnorableRequestFailure(request)) {
      diagnostics.failedRequests.push(`${request.method()} ${request.url()} ${request.failure()?.errorText ?? 'request failed'}`)
    }
  })
  if (bridgeOptions) {
    await page.addInitScript(installConfigurableAppMockBridge, bridgeOptions)
  }

  const originalClose = page.close.bind(page)
  page.close = async (options) => {
    if (page.isClosed()) {
      openPages.delete(page)
      return
    }
    try {
      await writePageDiagnostics(page, artifactLabel)
      await context.tracing.stop({ path: path.join(outputDir, 'traces', `${artifactLabel}.zip`) })
      await originalClose(options)
      await context.close()
    } finally {
      openPages.delete(page)
    }
  }

  return page
}

function isIgnorableDevServerConsoleError(text) {
  return /^WebSocket connection to 'ws:\/\/127\.0\.0\.1:\d+\/\?token=[^']+' failed: Error in connection establishment: net::ERR_NO_BUFFER_SPACE$/.test(text)
}

function isIgnorableRequestFailure(request) {
  const url = request.url()
  return url.startsWith('ws://127.0.0.1:') || url.includes('/@vite/client')
}

async function writePageDiagnostics(page, artifactLabel) {
  const bridgeStates = await page.evaluate(() => {
    const states = []
    if (window.__appMockState?.calls) {
      states.push({
        name: 'app',
        calls: window.__appMockState.calls,
        appliedFaults: window.__appMockState.appliedFaults ?? [],
      })
    }
    return states
  }).catch(() => [])

  for (const state of bridgeStates) {
    await fs.writeFile(
      path.join(outputDir, 'bridge-calls', `${artifactLabel}-${state.name}.json`),
      `${JSON.stringify({ calls: state.calls, appliedFaults: state.appliedFaults }, null, 2)}\n`,
      'utf8',
    )
  }
}

function activityButton(page, label) {
  return page.locator('nav').first().getByRole('button', { name: label })
}

function novelCard(page, title) {
  return page.getByRole('article', { name: `作品卡片 ${title}`, exact: true })
}

function tabLabel(page, title) {
  return page.locator('main').locator('> div').first().getByText(title, { exact: true })
}

async function getActivityStates(page) {
  return await page.locator('nav').first().getByRole('button').evaluateAll((buttons) =>
    buttons.map((button) => {
      const title = button.getAttribute('title') ?? button.getAttribute('aria-label') ?? ''
      const label = title.replace(/（即将推出）$/, '')
      return {
        label,
        isActiveBackground: button.classList.contains('bg-muted'),
        hasActiveIndicator: Array.from(button.querySelectorAll('span')).some((span) =>
          span.classList.contains('bg-primary')),
      }
    }),
  )
}

async function assertActiveActivity(page, label) {
  await page.waitForFunction(
    (expectedLabel) => {
      const states = Array.from(document.querySelectorAll('nav:first-of-type button')).map((button) => {
        const title = button.getAttribute('title') ?? button.getAttribute('aria-label') ?? ''
        return {
          label: title.replace(/（即将推出）$/, ''),
          active: button.classList.contains('bg-muted') ||
            Array.from(button.querySelectorAll('span')).some((span) => span.classList.contains('bg-primary')),
        }
      })
      const active = states.filter((state) => state.active)
      return active.length === 1 && active[0].label === expectedLabel
    },
    label,
    { timeout: 12_000 },
  ).catch((error) => {
    throw new Error(`Expected active activity: ${label}`, { cause: error })
  })

  const states = await getActivityStates(page)
  const activeStates = states.filter((state) => state.isActiveBackground || state.hasActiveIndicator)
  assert.equal(activeStates.length, 1, `Expected exactly one active activity, got ${activeStates.map((state) => state.label).join(', ') || 'none'}.`)
  assert.equal(activeStates[0].label, label, `Expected active activity ${label}, got ${activeStates[0].label}.`)
  assert.equal(activeStates[0].isActiveBackground, true, `Expected ${label} to have active background.`)
  assert.equal(activeStates[0].hasActiveIndicator, true, `Expected ${label} to have active indicator.`)
}

async function assertNoActiveActivity(page, description) {
  const activeStates = (await getActivityStates(page))
    .filter((state) => state.isActiveBackground || state.hasActiveIndicator)
  assert.deepEqual(activeStates.map((state) => state.label), [], `Expected no active activity for ${description}.`)
}

async function assertHeaderButtonActive(page, label) {
  const isActive = await page.locator('header').getByRole('button', { name: label }).evaluate((button) =>
    button.classList.contains('text-foreground'))
  assert.equal(isActive, true, `Expected header button ${label} to be active.`)
}

async function verifyShellNavigation(page) {
  await clickActivity(page, '书架')
  await expectVisible(page.getByRole('button', { name: '新建作品' }).last(), 'bookshelf create action')
  await expectVisible(page.getByText('全局回归小说').first(), 'bookshelf novel')

  await clickActivity(page, '章节')
  await expectVisible(page.getByText('章节 (6)'), 'chapter count')
  await expectVisible(page.getByRole('button', { name: /故事状态/ }), 'novelist entry')
  await ensureChapterBlockExpanded(page)
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'editor tab from shell navigation')
  await expectVisible(page.locator('.monaco-editor').first(), 'editor surface from shell navigation')
  await expectVisible(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'chat panel from shell navigation')
  await assertSelectedChapterPath(page, 'chapters/1.md')
  await assertActiveTabTitle(page, '第1章 雨夜线索')

  await clickActivity(page, '搜索')
  await expectVisible(page.getByPlaceholder('搜索人物、地点、时间线、正文...'), 'search sidebar from shell navigation')
  await expectVisible(page.getByText('输入关键词搜索'), 'search prompt from shell navigation')

  await clickActivity(page, '参考锚定')
  await expectVisible(page.locator('aside').getByText('即将推出'), 'reference sidebar placeholder from shell navigation')
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'reference panel from shell navigation')

  await clickActivity(page, '风格素材')
  await expectVisible(page.getByRole('heading', { name: /风格素材/ }), 'style sample panel from shell navigation')
  await expectVisible(page.getByText('全局雨夜节奏').first(), 'style sample fixture from shell navigation')

  await clickActivity(page, 'Git 历史')
  await expectVisible(page.getByRole('heading', { name: 'Git 历史' }), 'Git history panel from shell navigation')
  await expectVisible(page.getByText('rename rain clue chapter').first(), 'Git history fixture from shell navigation')

  await clickActivity(page, '角色')
  await expectVisible(page.locator('aside').getByText(/角色 \(\d+\)/), 'characters sidebar from shell navigation')
  await expectVisible(page.locator('aside').getByPlaceholder('搜索角色...'), 'characters sidebar search from shell navigation')
  await expectVisible(page.getByRole('heading', { name: /角色/ }), 'characters panel from shell navigation')

  await clickActivity(page, '地点')
  await expectVisible(page.locator('aside').getByText(/地点 \(\d+\)/), 'locations sidebar from shell navigation')
  await expectVisible(page.getByRole('heading', { name: /地点/ }), 'locations panel from shell navigation')

  await clickActivity(page, '弧线')
  await expectVisible(page.locator('aside').getByText(/叙事弧线 \(\d+\)/), 'story arcs sidebar from shell navigation')
  await expectVisible(page.locator('aside').getByPlaceholder('搜索弧线...'), 'story arcs sidebar search from shell navigation')
  await expectVisible(page.getByRole('heading', { name: /弧线节点/ }), 'story arcs panel from shell navigation')

  await clickActivity(page, '时间线')
  await expectVisible(page.locator('aside').getByText(/伏笔\/指令 \(\d+\)/), 'timeline sidebar from shell navigation')
  await expectVisible(page.locator('aside').getByPlaceholder('搜索时间线...'), 'timeline sidebar search from shell navigation')
  await expectVisible(page.getByRole('heading', { name: /章节计划/ }), 'timeline panel from shell navigation')

  await clickActivity(page, '偏好')
  await expectVisible(page.locator('aside').getByText(/创作偏好 \(\d+\)/), 'preferences sidebar from shell navigation')
  await expectVisible(page.locator('aside').getByPlaceholder('搜索偏好...'), 'preferences sidebar search from shell navigation')
  await expectVisible(page.getByRole('heading', { name: /创作偏好/ }), 'preferences panel from shell navigation')

  await clickActivity(page, '读者视角')
  await expectVisible(page.locator('aside').getByText(/读者视角 \(\d+\)/), 'reader sidebar from shell navigation')
  await expectVisible(page.locator('aside').getByPlaceholder('搜索条目...'), 'reader sidebar search from shell navigation')
  await expectVisible(page.getByRole('heading', { name: /读者视角/ }), 'reader panel from shell navigation')

  await clickActivity(page, '技能')
  await expectVisible(page.getByText('技能 (2)'), 'skills panel from shell navigation')
  await expectVisible(page.locator('aside').getByRole('button', { name: '新建技能' }), 'skills sidebar create action from shell navigation')
  await expectVisible(page.locator('aside').getByPlaceholder('搜索...'), 'skills sidebar search from shell navigation')

  await page.locator('header').getByRole('button', { name: '个人中心' }).click()
  await assertNoActiveActivity(page, 'profile panel')
  await assertHeaderButtonActive(page, '个人中心')
  await expectHidden(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'chat panel hidden during profile navigation')
  await expectVisible(page.locator('aside').getByText('即将推出'), 'profile sidebar placeholder from shell navigation')
  await expectVisible(page.getByText('Mock User'), 'profile panel from shell navigation')
  await expectVisible(page.getByText('累计字数'), 'profile stats from shell navigation')

  await clickActivity(page, '章节')
  await expectVisible(page.getByText('章节 (6)'), 'chapter sidebar restored after profile navigation')
  await ensureChapterBlockExpanded(page)
  await assertSelectedChapterPath(page, 'chapters/1.md')
  await assertActiveTabTitle(page, '第1章 雨夜线索')
  await expectVisible(page.locator('.monaco-editor').first(), 'editor restored after repeated shell navigation')
  await expectVisible(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'chat panel restored after profile navigation')

  await page.locator('header').getByRole('button', { name: '帮助' }).click()
  await expectVisible(page.getByText('欢迎使用 Novelist'), 'help dialog')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
  await assertActiveActivity(page, '章节')

  await page.locator('header').getByRole('button', { name: '设置' }).click()
  await expectVisible(page.getByText('基础设置'), 'settings affordance from shell navigation')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
  await assertActiveActivity(page, '章节')
  await assertBridgeCallCount(page, 'SaveContent', 0)
}

async function clickActivity(page, label) {
  await activityButton(page, label).click()
  await assertActiveActivity(page, label)
}

async function verifyChapterWorkflow(page) {
  await page.getByTitle('章节').click()
  await ensureChapterBlockExpanded(page)

  await expectVisible(chapterButton(page, '雨夜线索'), 'first chapter in side panel')
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'chapter tab title')
  await assertSelectedChapterPath(page, 'chapters/1.md')
  await assertActiveTabTitle(page, '第1章 雨夜线索')
  await waitForBridgeCallArg(page, 'GetContent', 1, 'chapters/1.md')

  await chapterButton(page, '旧城门').click()
  await expectVisible(page.getByText('第2章 旧城门').first(), 'second chapter tab title')
  await assertSelectedChapterPath(page, 'chapters/2.md')
  await assertActiveTabTitle(page, '第2章 旧城门')
  await waitForBridgeCallArg(page, 'GetContent', 1, 'chapters/2.md')

  await page.getByText('第1章 雨夜线索').first().click()
  await assertActiveTabTitle(page, '第1章 雨夜线索')
  await assertSelectedChapterPath(page, 'chapters/1.md')

  await page.getByRole('button', { name: '关闭标签 第2章 旧城门' }).click({ force: true })
  await expectHidden(tabLabel(page, '第2章 旧城门'), 'closed second chapter tab')
  await expectVisible(tabLabel(page, '第1章 雨夜线索'), 'first chapter tab remains after closing second')
  await assertActiveTabTitle(page, '第1章 雨夜线索')

  await page.getByRole('button', { name: /故事状态/ }).click()
  await expectVisible(page.getByText('故事状态').first(), 'story state tab')
  await assertActiveTabTitle(page, '故事状态')
  await page.getByRole('button', { name: '关闭标签 故事状态' }).click({ force: true })
  await expectHidden(tabLabel(page, '故事状态'), 'story state tab closed')
  await expectVisible(tabLabel(page, '第1章 雨夜线索'), 'chapter tab restored after closing story state')
  await assertActiveTabTitle(page, '第1章 雨夜线索')
}

async function verifyEditorSaveWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    allowSaveContent: true,
  })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'editor save workspace')
  await assertBridgeCallCount(page, 'SaveContent', 0)

  await page.getByTitle('章节').click()
  await ensureChapterBlockExpanded(page)
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'editable chapter tab')
  await expectVisible(page.locator('.monaco-editor').first(), 'monaco editor render')
  await expectVisible(page.getByText('已保存'), 'initial saved status')

  const savedText = realisticWritingText()
  await replaceEditorText(page, savedText)
  await expectVisible(page.getByText('未保存'), 'dirty status after edit')
  await page.keyboard.press(shortcutKey('S'))
  await waitForSaveContent(page, 'chapters/1.md', '## 雨夜复盘')
  await expectVisible(page.getByText('已保存'), 'saved status after explicit save')
  await assertStoredContent(page, 'chapters/1.md', savedText)
  const saveCountAfterSuccess = await bridgeCallCount(page, 'SaveContent')
  assert(saveCountAfterSuccess >= 1, 'Expected edited chapter to be saved at least once.')

  const undoRedoAppend = '\n\n补记：这行文字专门用于验证 Monaco 撤销与重做。'
  await insertEditorText(page, undoRedoAppend)
  await expectVisible(page.getByText('未保存'), 'dirty status after undo-redo insert')
  await assertEditorContains(page, '验证 Monaco 撤销与重做')
  await page.keyboard.press(shortcutKey('Z'))
  await assertEditorNotContains(page, '验证 Monaco 撤销与重做')
  await page.keyboard.press(shortcutKey('Y'))
  await assertEditorContains(page, '验证 Monaco 撤销与重做')
  await page.keyboard.press(shortcutKey('Z'))
  await assertEditorNotContains(page, '验证 Monaco 撤销与重做')
  await page.keyboard.press(shortcutKey('S'))
  await waitForSaveContentAfter(page, 'chapters/1.md', '## 雨夜复盘', saveCountAfterSuccess)
  await expectVisible(page.getByText('已保存'), 'saved status after undo-redo restore')
  await assertStoredContent(page, 'chapters/1.md', savedText)
  const saveCountAfterUndoRedo = await bridgeCallCount(page, 'SaveContent')
  assert(saveCountAfterUndoRedo > saveCountAfterSuccess, 'Expected restored chapter to be saved after undo-redo.')
  await delay(700)
  const saveCountAfterUndoRedoSettled = await bridgeCallCount(page, 'SaveContent')
  await assertStoredContent(page, 'chapters/1.md', savedText)

  await page.getByTitle('搜索').click()
  await page.getByTitle('章节').click()
  await ensureChapterBlockExpanded(page)
  await assertSelectedChapterPath(page, 'chapters/1.md')
  await assertActiveTabTitle(page, '第1章 雨夜线索')
  await assertEditorContains(page, '## 雨夜复盘')
  await assertEditorNotContains(page, '验证 Monaco 撤销与重做')
  await assertBridgeCallCount(page, 'SaveContent', saveCountAfterUndoRedoSettled)

  await chapterButton(page, '旧城门').click()
  await expectVisible(page.getByText('第2章 旧城门').first(), 'second chapter tab')
  await page.evaluate(() => { window.__appMockState.failNextSaveContent = true })
  const retryText = '旧城门下，保存失败片段仍留在编辑器。\n\n她第二次按下保存，确认失败可以恢复。'
  await replaceEditorText(page, retryText)
  await expectVisible(page.getByText('未保存'), 'dirty status after failed edit')
  await page.keyboard.press(shortcutKey('S'))
  await expectVisible(page.getByText('保存失败：模拟保存失败，请重试'), 'save failure alert')
  await expectVisible(page.getByText('未保存'), 'dirty status retained after failed save')
  const saveCountAfterFailure = await bridgeCallCount(page, 'SaveContent')
  assert(saveCountAfterFailure > saveCountAfterUndoRedoSettled, 'Expected failed explicit save to call SaveContent.')
  await assertStoredContent(page, 'chapters/2.md', '旧城门下，暗号被雨水冲淡。')

  await page.getByRole('button', { name: '重试保存' }).click()
  await waitForSaveContentAfter(page, 'chapters/2.md', '第二次按下保存', saveCountAfterFailure)
  await assertStoredContent(page, 'chapters/2.md', retryText)
  await expectVisible(page.getByText('已保存'), 'saved status after retry save')
  const saveCountAfterRetry = await bridgeCallCount(page, 'SaveContent')
  assert(saveCountAfterRetry > saveCountAfterFailure, 'Expected retry explicit save to call SaveContent.')

  await page.getByText('第1章 雨夜线索').first().click()
  await assertActiveTabTitle(page, '第1章 雨夜线索')
  await assertEditorContains(page, '## 雨夜复盘')
  await page.getByText('第2章 旧城门').first().click()
  await assertActiveTabTitle(page, '第2章 旧城门')
  await assertEditorContains(page, '第二次按下保存')

  await page.getByTitle('搜索').click()
  await delay(700)
  await page.getByTitle('章节').click()
  await ensureChapterBlockExpanded(page)
  await assertBridgeCallCount(page, 'SaveContent', saveCountAfterRetry)
  await assertActiveTabTitle(page, '第2章 旧城门')
  await assertSelectedChapterPath(page, 'chapters/2.md')
  await assertEditorContains(page, '第二次按下保存')
  await page.close()
}

async function verifyNovelChapterWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'novel workflow workspace')

  await clickActivity(page, '书架')
  const bookshelfSearch = page.getByPlaceholder('搜索作品、分类或简介...')
  await expectVisible(bookshelfSearch, 'bookshelf search input')
  await expectVisible(novelCard(page, '全局回归小说'), 'original novel card')

  await page.getByRole('button', { name: '新建作品' }).last().click()
  await page.getByPlaceholder('输入书名').fill('全局回归小说 副本')
  await page.getByPlaceholder('如：玄幻、科幻、都市...').fill('科幻')
  await page.getByPlaceholder('简单介绍一下这部作品（可选）').fill('覆盖小说创建、重名和选择流程')
  await page.locator('.fixed').getByRole('button', { name: '保存' }).click()
  await waitForBridgeCall(page, 'CreateNovel')
  await expectVisible(page.getByText('全局回归小说 副本').first(), 'duplicate-like novel visible')
  await expectVisible(page.getByText('章节 (0)'), 'created novel empty chapter count')
  await expectVisible(page.getByText('暂无章节'), 'created novel empty chapter state')
  await assertActiveNovelId(page, 43)

  await clickActivity(page, '书架')
  await bookshelfSearch.fill('副本')
  await expectVisible(novelCard(page, '全局回归小说 副本'), 'filtered duplicate-like novel card')
  await expectHidden(novelCard(page, '全局回归小说'), 'filtered out original novel card')
  await bookshelfSearch.fill('没有这部作品')
  await expectVisible(page.getByText('没有匹配的作品'), 'bookshelf empty search state')
  await bookshelfSearch.fill('')

  await page.locator('aside').getByRole('button', { name: '全局回归小说', exact: true }).click()
  await waitForBridgeCallArg(page, 'SetActiveNovel', 0, { novel_id: 42 })
  await expectVisible(page.getByText('章节 (6)'), 'original novel chapter count restored')
  await assertActiveNovelId(page, 42)

  await clickActivity(page, '书架')
  await page.getByRole('button', { name: '编辑作品 全局回归小说', exact: true }).click({ force: true })
  await page.getByPlaceholder('输入书名').fill('全局回归小说-修订')
  await page.getByPlaceholder('如：玄幻、科幻、都市...').fill('悬疑')
  await page.getByPlaceholder('简单介绍一下这部作品（可选）').fill('已通过回归流程编辑作品')
  await page.locator('.fixed').getByRole('button', { name: '保存' }).click()
  await waitForBridgeCall(page, 'UpdateNovel')
  await expectVisible(page.getByText('全局回归小说-修订').first(), 'updated novel visible')
  await assertActiveNovelId(page, 42)

  await bookshelfSearch.fill('修订')
  await expectVisible(novelCard(page, '全局回归小说-修订'), 'filtered renamed novel card')
  await expectHidden(novelCard(page, '全局回归小说 副本'), 'filtered out duplicate-like novel card')
  await bookshelfSearch.fill('')

  await page.getByRole('button', { name: '更换封面 全局回归小说-修订' }).click({ force: true })
  const coverInput = page.locator('input[type="file"][accept="image/*"]').first()
  await coverInput.setInputFiles({
    name: 'novel-workflow-cover.png',
    mimeType: 'image/png',
    buffer: Buffer.from([137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4]),
  })
  await waitForBridgeCall(page, 'SaveCover')
  await assertLastBinaryCall(page, 'SaveCover', 12)

  await page.getByRole('button', { name: '删除作品 全局回归小说 副本' }).click({ force: true })
  await expectVisible(page.getByRole('heading', { name: '删除作品' }), 'delete duplicate-like novel dialog')
  await assertBridgeCallCount(page, 'DeleteNovel', 0)
  await page.locator('.fixed').getByRole('button', { name: '取消' }).click()
  await expectHidden(page.getByRole('heading', { name: '删除作品' }), 'delete duplicate-like novel dialog cancelled')
  await expectVisible(novelCard(page, '全局回归小说 副本'), 'duplicate-like novel retained after delete cancel')

  await page.getByRole('button', { name: '删除作品 全局回归小说 副本' }).click({ force: true })
  await page.getByPlaceholder('输入书名确认').fill('全局回归小说 副本')
  await page.locator('.fixed').getByRole('button', { name: '确认删除' }).click()
  await waitForBridgeCall(page, 'DeleteNovel')
  await expectHidden(novelCard(page, '全局回归小说 副本'), 'duplicate-like novel removed after delete')
  await assertNovelDeleted(page, 43)
  await assertActiveNovelId(page, 42)

  await clickActivity(page, '章节')
  await expectVisible(page.getByText('章节 (6)'), 'updated novel chapter count')
  await ensureChapterBlockExpanded(page)
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'first chapter tab before second tab')
  await chapterButton(page, '旧城门').click()
  await expectVisible(page.getByText('第2章 旧城门').first(), 'second chapter tab')
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'first chapter tab preserved')
  await page.getByText('第1章 雨夜线索').first().click()
  await assertActiveTabTitle(page, '第1章 雨夜线索')
  await assertSelectedChapterPath(page, 'chapters/1.md')

  await page.getByRole('button', { name: '新建章节' }).click()
  await page.getByPlaceholder('章节标题').fill('新章验收')
  await page.getByRole('button', { name: '添加' }).click()
  await waitForBridgeCall(page, 'CreateChapter')
  await expectVisible(page.getByText('章节 (7)'), 'chapter count after create')
  await ensureChapterBlockForTitleExpanded(page, '新章验收')
  await expectVisible(chapterButton(page, '新章验收'), 'created chapter visible')

  await page.getByRole('button', { name: '编辑章节 新章验收' }).click({ force: true })
  await page.locator('aside input').first().fill('新章验收-改名')
  await page.keyboard.press('Enter')
  await waitForBridgeCall(page, 'UpdateChapterTitle')
  await expectVisible(chapterButton(page, '新章验收-改名'), 'renamed chapter visible')

  await chapterButton(page, '新章验收-改名').click()
  await expectVisible(page.getByText('第7章 新章验收-改名').first(), 'renamed chapter tab')
  await waitForBridgeCallArg(page, 'GetContent', 1, 'chapters/7.md')
  await assertSelectedChapterPath(page, 'chapters/7.md')
  await assertChapterTitle(page, 42, 7, '新章验收-改名')

  await assertBridgeCallCount(page, 'DeleteNovel', 1)
  await assertBridgeCallCount(page, 'SaveCover', 1)
  await assertBridgeCallCount(page, 'ExportNovel', 0)
  await page.close()
}

async function verifyImportExportFilePickerWorkflow(browser, url, consoleErrors, pageErrors) {
  const fixtureDir = path.join(outputDir, 'fixtures', 'import-export')
  await fs.mkdir(fixtureDir, { recursive: true })
  const pickedReferenceSourceFile = path.join(fixtureDir, 'reference-source.md')
  const pickedNovelImportFile = path.join(fixtureDir, 'picker-import.txt')
  const droppedNovelImportFile = path.join(fixtureDir, 'drop-import.md')
  const droppedNovelImportUriFile = path.join(fixtureDir, 'drop-import-uri.markdown')
  const cancelNovelImportFile = path.join(fixtureDir, 'cancel-import.txt')
  const parserFailureImportFile = path.join(fixtureDir, 'parser-failure.txt')
  const writeFailureImportFile = path.join(fixtureDir, 'write-failure.md')
  const gitWarningImportFile = path.join(fixtureDir, 'git-warning.txt')
  const skippedEpubImportFile = path.join(fixtureDir, 'skipped-chapters.epub')
  await fs.writeFile(
    pickedReferenceSourceFile,
    '# Phase 13 import/export fixture\n\n雨夜参考源只用于文件选择 mock，不读取真实用户项目。\n',
    'utf8',
  )
  await fs.writeFile(pickedNovelImportFile, '第一章\n通过文件选择导入。', 'utf8')
  await fs.writeFile(droppedNovelImportFile, '# 第一章\n\n通过拖放导入。', 'utf8')
  await fs.writeFile(droppedNovelImportUriFile, '# 第一章\n\n通过 file URI 拖放导入。', 'utf8')
  await fs.writeFile(cancelNovelImportFile, '第一章\n取消导入。', 'utf8')
  await fs.writeFile(parserFailureImportFile, '这份 fixture 由 mock 模拟解析失败。', 'utf8')
  await fs.writeFile(writeFailureImportFile, '# 第一章\n\n这份 fixture 由 mock 模拟写入失败并清理。', 'utf8')
  await fs.writeFile(gitWarningImportFile, '第一章\n这份 fixture 由 mock 模拟 Git 警告。', 'utf8')
  await fs.writeFile(skippedEpubImportFile, 'mock epub bytes', 'utf8')

  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    pickedReferenceSourceFile,
    pickedNovelImportFile,
  })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'file-picker workflow workspace')

  await clickActivity(page, '章节')
  await page.getByRole('button', { name: '导出作品' }).click()
  await expectVisible(page.getByRole('heading', { name: '导出作品' }), 'chapter export dialog')
  await page.getByRole('button', { name: /Markdown/ }).click()
  await page.locator('.fixed').getByRole('button', { name: '导出' }).click()
  await expectVisible(page.getByText('✓ 导出成功'), 'chapter export success')
  await waitForBridgeCallArg(page, 'ExportNovel', 1, 'markdown')
  await page.locator('.fixed').getByRole('button', { name: '完成' }).click()
  await assertExportedNovels(page, [{ novel_id: 42, format: 'markdown' }])

  await clickActivity(page, '书架')
  await page.getByRole('button', { name: '导出作品 全局回归小说' }).click({ force: true })
  await expectVisible(page.getByRole('heading', { name: '导出作品' }), 'bookshelf export dialog')
  await page.getByRole('button', { name: /TXT/ }).click()
  await page.locator('.fixed').getByRole('button', { name: '导出' }).click()
  await expectVisible(page.getByText('✓ 导出成功'), 'bookshelf export success')
  await waitForBridgeCallArg(page, 'ExportNovel', 1, 'txt')
  await page.locator('.fixed').getByRole('button', { name: '完成' }).click()
  await assertExportedNovels(page, [
    { novel_id: 42, format: 'markdown' },
    { novel_id: 42, format: 'txt' },
  ])

  const pickImportBefore = await bridgeCallCount(page, 'PickNovelImportFile')
  const startImportBefore = await bridgeCallCount(page, 'StartNovelImport')
  await page.getByRole('button', { name: '导入小说' }).click()
  await waitForBridgeCallCountAfter(page, 'PickNovelImportFile', pickImportBefore)
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', startImportBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入完成' }), 'file-picker novel import dialog')
  await expectVisible(page.getByText('100%'), 'file-picker novel import percent')
  await expectVisible(page.getByText('当前章节'), 'file-picker novel import current chapter label')
  await expectVisible(page.getByText('导入开篇').first(), 'file-picker novel import current chapter')
  await expectVisible(page.getByText('已导入：picker-import'), 'file-picker novel import success')
  await expectHidden(page.getByText('旧导入不应显示'), 'stale novel import progress ignored')
  await page.getByRole('button', { name: '完成' }).click()
  await expectVisible(page.getByText('导入开篇').first(), 'first imported chapter opens after file-picker import')
  await clickActivity(page, '书架')
  await expectVisible(novelCard(page, 'picker-import'), 'file-picker imported novel card')
  await assertLastBridgeCallInput(page, 'StartNovelImport', {
    source_path: pickedNovelImportFile,
    source_display_name: 'picker-import.txt',
    import_kind: 'txt',
  })
  await assertNoBridgeCallArgValue(page, 'GetContent', pickedNovelImportFile, 'Novel import picker must not route source paths through generic content reads.')

  const importDropzone = page.getByTestId('novel-import-dropzone')
  const dropImportBefore = await bridgeCallCount(page, 'StartNovelImport')
  await dispatchNovelImportDrop(page, { kind: 'empty' })
  await expectVisible(page.getByText('没有可导入的文件'), 'empty novel import drop rejection')
  await dispatchNovelImportDrop(page, { kind: 'url', url: 'https://example.test/book.txt' })
  await expectVisible(page.getByText('不能拖入 URL'), 'URL novel import drop rejection')
  await dispatchNovelImportDrop(page, { kind: 'directory' })
  await expectVisible(page.getByText('不能拖入文件夹'), 'folder novel import drop rejection')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [
      { name: 'unsupported.pdf', path: path.join(fixtureDir, 'unsupported.pdf'), type: 'application/pdf' },
      { name: 'unsupported.docx', path: path.join(fixtureDir, 'unsupported.docx'), type: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' },
    ],
  })
  await expectVisible(page.getByText('仅支持 EPUB、TXT 或 Markdown 文件'), 'unsupported novel import drop rejection')
  assert.equal(
    await bridgeCallCount(page, 'StartNovelImport'),
    dropImportBefore,
    'Rejected novel import drops must not call StartNovelImport.',
  )

  await dispatchNovelImportDrop(page, {
    kind: 'fileUriText',
    uri: pathToFileURL(droppedNovelImportUriFile).href,
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', dropImportBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入完成' }), 'file URI novel import dialog')
  await expectVisible(page.getByText('已导入：drop-import-uri'), 'file URI novel import drop success')
  await page.getByRole('button', { name: '完成' }).click()
  await clickActivity(page, '书架')
  await expectVisible(novelCard(page, 'drop-import-uri'), 'file URI imported novel card')
  await assertLastBridgeCallInput(page, 'StartNovelImport', {
    source_path: droppedNovelImportUriFile,
    source_display_name: 'drop-import-uri.markdown',
    import_kind: 'markdown',
  })
  await assertNoBridgeCallArgValue(page, 'GetContent', droppedNovelImportUriFile, 'Dropped file URI novel import paths must not route through generic content reads.')

  const fileDropImportBefore = await bridgeCallCount(page, 'StartNovelImport')
  await importDropzone.hover()
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'drop-import.md', path: droppedNovelImportFile, type: 'text/markdown' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', fileDropImportBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入完成' }), 'drag-drop novel import dialog')
  await expectVisible(page.getByText('已导入：drop-import'), 'drag-drop novel import success')
  await page.getByRole('button', { name: '完成' }).click()
  await clickActivity(page, '书架')
  await expectVisible(novelCard(page, 'drop-import'), 'drag-drop imported novel card')
  await assertLastBridgeCallInput(page, 'StartNovelImport', {
    source_path: droppedNovelImportFile,
    source_display_name: 'drop-import.md',
    import_kind: 'markdown',
  })
  await assertNoBridgeCallArgValue(page, 'GetContent', droppedNovelImportFile, 'Dropped novel import paths must not route through generic content reads.')

  const cancelImportBefore = await bridgeCallCount(page, 'StartNovelImport')
  const cancelCallBefore = await bridgeCallCount(page, 'CancelNovelImport')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'cancel-import.txt', path: cancelNovelImportFile, type: 'text/plain' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', cancelImportBefore)
  await expectVisible(page.getByRole('dialog', { name: '正在导入小说' }), 'cancel import in-progress dialog')
  await page.getByRole('button', { name: '取消导入' }).click()
  await waitForBridgeCallCountAfter(page, 'CancelNovelImport', cancelCallBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入已取消' }), 'user cancel import terminal dialog')
  await expectVisible(page.getByText('导入已取消').nth(1), 'user cancel import message')
  await page.getByRole('button', { name: '完成' }).click()
  await clickActivity(page, '书架')
  await expectHidden(novelCard(page, 'cancel-import'), 'cancelled import must not leave a novel card')
  await assertNoBridgeCallArgValue(page, 'GetContent', cancelNovelImportFile, 'Cancelled novel import paths must not route through generic content reads.')

  const parserFailureBefore = await bridgeCallCount(page, 'StartNovelImport')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'parser-failure.txt', path: parserFailureImportFile, type: 'text/plain' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', parserFailureBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入失败' }), 'parser failure import dialog')
  await expectVisible(page.getByText('解析失败', { exact: true }), 'parser failure stage')
  await expectVisible(page.getByText('源文件解析失败').first(), 'parser failure message')
  await page.getByRole('button', { name: '完成' }).click()
  await clickActivity(page, '书架')
  await expectHidden(novelCard(page, 'parser-failure'), 'parser failure must not leave a novel card')
  await assertNoBridgeCallArgValue(page, 'GetContent', parserFailureImportFile, 'Parser failure import paths must not route through generic content reads.')

  const writeFailureBefore = await bridgeCallCount(page, 'StartNovelImport')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'write-failure.md', path: writeFailureImportFile, type: 'text/markdown' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', writeFailureBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入失败' }), 'write failure cleanup dialog')
  await expectVisible(page.getByText('清理完成', { exact: true }), 'write failure cleanup stage')
  await expectVisible(page.getByText('导入写入失败，已清理未完成数据。').first(), 'write failure cleanup message')
  await page.getByRole('button', { name: '完成' }).click()
  await clickActivity(page, '书架')
  await expectHidden(novelCard(page, 'write-failure'), 'write failure cleanup must not leave a novel card')
  await assertNoBridgeCallArgValue(page, 'GetContent', writeFailureImportFile, 'Write failure import paths must not route through generic content reads.')

  const gitWarningBefore = await bridgeCallCount(page, 'StartNovelImport')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'git-warning.txt', path: gitWarningImportFile, type: 'text/plain' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', gitWarningBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入完成，有警告' }), 'git warning import dialog')
  await expectVisible(page.getByText('导入警告'), 'git warning section')
  await expectVisible(page.getByText('导入已完成，但 Git 提交失败。'), 'git warning message')
  await page.getByRole('button', { name: '完成' }).click()
  await expectVisible(page.getByText('导入开篇').first(), 'git warning import opens first chapter')
  await clickActivity(page, '书架')
  await expectVisible(novelCard(page, 'git-warning'), 'git warning import keeps imported novel card')
  await assertNoBridgeCallArgValue(page, 'GetContent', gitWarningImportFile, 'Git warning import paths must not route through generic content reads.')

  const skippedEpubBefore = await bridgeCallCount(page, 'StartNovelImport')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'skipped-chapters.epub', path: skippedEpubImportFile, type: 'application/epub+zip' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', skippedEpubBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入完成' }), 'skipped EPUB import dialog')
  await expectVisible(page.getByText('跳过 2 章'), 'skipped EPUB chapter count')
  await expectVisible(page.getByText(/#2 空白章节 · empty_content/), 'skipped EPUB empty chapter detail')
  await expectVisible(page.getByText(/#3 缺失章节 · missing_spine_item/), 'skipped EPUB missing chapter detail')
  await page.getByRole('button', { name: '完成' }).click()
  await clickActivity(page, '书架')
  await expectVisible(novelCard(page, 'skipped-chapters'), 'skipped EPUB import keeps imported novel card')
  await assertNoBridgeCallArgValue(page, 'GetContent', skippedEpubImportFile, 'Skipped EPUB import paths must not route through generic content reads.')

  await page.getByRole('button', { name: '更换封面 全局回归小说' }).click({ force: true })
  const coverInput = page.locator('input[type="file"][accept="image/*"]').first()
  await coverInput.setInputFiles({
    name: 'cover.png',
    mimeType: 'image/png',
    buffer: Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
  })
  await waitForBridgeCall(page, 'SaveCover')
  await assertLastBinaryCall(page, 'SaveCover', 8)
  await assertSavedCover(page, { novel_id: 42, byte_count: 8 })

  await page.locator('header').getByRole('button', { name: '个人中心' }).click()
  await expectVisible(page.getByText('Mock User'), 'profile before avatar upload')
  const avatarInput = page.locator('input[type="file"][accept="image/*"]').first()
  await avatarInput.setInputFiles({
    name: 'avatar.jpg',
    mimeType: 'image/jpeg',
    buffer: Buffer.from([255, 216, 255, 224]),
  })
  await waitForBridgeCall(page, 'SaveAvatar')
  await assertLastBinaryCall(page, 'SaveAvatar', 4)
  await assertSavedAvatar(page, { byte_count: 4 })

  await clickActivity(page, '参考锚定')
  await page.getByRole('button', { name: '选择参考源文件' }).click()
  await waitForBridgeCall(page, 'PickReferenceSourceFile')
  await expectInputValue(page.getByLabel('本地路径'), pickedReferenceSourceFile)
  await expectSelectedValue(page.locator('select').first(), 'markdown')
  await page.getByPlaceholder('参考书名').fill('文件选择参考')
  await page.getByRole('button', { name: /^创建$/ }).click()
  await waitForBridgeCall(page, 'CreateReferenceAnchor')
  await expectVisible(page.getByText('参考锚点已创建'), 'reference anchor created from picked file')
  await assertCreatedReferenceAnchor(page, {
    title: '文件选择参考',
    sourcePath: pickedReferenceSourceFile,
    sourceKind: 'markdown',
  })

  await assertBridgeCallCount(page, 'PickReferenceSourceFile', 1)
  await assertBridgeCallCount(page, 'PickNovelImportFile', 1)
  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await assertOnlyTemporaryFixturePaths(page, fixtureDir)
  await page.close()
}

async function ensureChapterBlockExpanded(page) {
  const firstChapter = chapterButton(page, '雨夜线索')
  if (await firstChapter.isVisible()) return

  const chapterBlock = page.getByRole('button', { name: /第 1 - \d+ 章/ })
  if (await chapterBlock.isVisible()) {
    await chapterBlock.click()
  }
  await expectVisible(firstChapter, 'expanded first chapter')
}

async function ensureChapterBlockForTitleExpanded(page, title) {
  const target = chapterButton(page, title)
  if (await target.isVisible()) return

  const blocks = page.locator('aside').getByRole('button', { name: /第 \d+( - \d+)? 章/ })
  const count = await blocks.count()
  for (let index = 0; index < count; index += 1) {
    await blocks.nth(index).click()
    if (await target.isVisible()) return
  }

  await expectVisible(target, `expanded chapter ${title}`)
}

function chapterButton(page, title) {
  return page.locator('aside').getByRole('button', { name: new RegExp(`第\\d+章\\s+${escapeRegExp(title)}`) })
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

async function verifySearchWorkflow(page) {
  await page.getByTitle('搜索').click()
  const searchInput = page.getByPlaceholder('搜索人物、地点、时间线、正文...')
  const searchPanel = page.locator('aside').first()

  await expectVisible(page.getByText('输入关键词搜索'), 'search prompt')

  await searchInput.fill('没有结果')
  await expectVisible(page.getByText('无搜索结果'), 'empty search state')

  await searchInput.fill('搜索失败后恢复')
  await expectVisible(page.getByText('搜索失败，请稍后重试'), 'search failure state')
  await searchPanel.getByRole('button', { name: '重试' }).click()
  await expectVisible(page.getByText('正文匹配 (1)'), 'search retry recovery')

  await searchInput.fill('雨夜')
  await expectVisible(page.getByText('正文匹配 (1)'), 'content search group')
  await expectVisible(page.getByText('人物 (1)'), 'character search group')
  await expectVisible(page.getByText('地点 (1)'), 'location search group')
  await expectVisible(page.getByText('时间线 (1)'), 'timeline search group')
  await expectVisible(page.getByText('故事弧 (1)'), 'story arc search group')
  await expectVisible(page.getByText('偏好 (1)'), 'preference search group')
  await expectVisible(page.getByText('故事记忆 (1)'), 'story memory search group')
  await expectVisible(page.getByText('语义匹配 (1)'), 'semantic search group')
  await expectVisible(page.getByText('林岚在').first(), 'content result preview')
  await expectVisible(page.getByText('旧城门调查者'), 'character result preview')
  await expectVisible(page.getByText('雨夜里暗号被冲淡'), 'location result preview')
  await expectVisible(page.getByText('杯底留下半圈水痕'), 'timeline result preview')
  await expectVisible(page.getByText('雨夜场景多用动作间隔承压'), 'preference result preview')
  await expectVisible(page.getByText('故事记忆只返回章节语义摘要'), 'story memory preview')
  await expectVisible(page.getByText('86%'), 'rag relevance score')
  await assertSearchResultContainsRestrictedSourcePath(page)
  await expectHidden(page.getByText('D:\\books\\rain-reference.md'), 'reference source path in global search')
  await expectHidden(page.getByText('D:\\restricted\\reference-source.md'), 'restricted reference source path in global search')

  await searchPanel.getByRole('button', { name: /^旧城门/ }).click()
  await expectVisible(page.getByRole('heading', { name: /地点/ }), 'location search navigation')
  await expectVisible(page.getByRole('main').getByText('雨夜里暗号被冲淡的城门'), 'location search navigation target')

  await page.getByTitle('搜索').click()
  await searchPanel.getByRole('button', { name: /^雨夜场景规则/ }).click()
  await expectVisible(page.getByRole('heading', { name: /创作偏好/ }), 'preference search navigation')
  await expectVisible(page.getByRole('main').getByText('雨夜场景多用动作间隔承压。'), 'preference search navigation target')

  await page.getByTitle('搜索').click()
  const getContentBeforeStoryMemory = await bridgeCallCount(page, 'GetContent')
  await searchPanel.getByRole('button', { name: /^故事记忆：旧城门约束/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetContent', getContentBeforeStoryMemory)
  await expectVisible(page.locator('.monaco-editor').first(), 'story memory opened editor')
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'story memory opened chapter')

  await page.getByTitle('搜索').click()
  await searchPanel.getByRole('button', { name: /^雨夜线索/ }).click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'search opened chapter')
  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
}

async function verifyChatWorkflow(page) {
  const chapterContentBefore = await page.evaluate(() => window.__appMockState.contentByPath['chapters/1.md'])
  const saveContentBefore = await bridgeCallCount(page, 'SaveContent')
  const input = page.getByPlaceholder('输入消息，按 / 调用技能...')
  await input.fill('检查雨夜线索这一章的约束')
  await input.press('Enter')

  await expectVisible(page.getByText('检查雨夜线索这一章的约束'), 'user chat message')
  await expectVisible(page.getByText('读取章节列表').first(), 'tool card')
  await expectVisible(page.getByText('搜索完成').first(), 'web search card')
  await expectVisible(page.getByText('Mock source'), 'web search source title')
  await expectVisible(page.getByText('https://example.com/mock-source'), 'web search source URL')
  await page.getByRole('button', { name: '搜索结果总结' }).click()
  await expectVisible(page.getByText('检索结果只用于对照氛围，不写入章节。'), 'web search summary')
  await expectVisible(page.getByText('建议先保留受限视角').first(), 'assistant message')

  await input.fill('停止生成这个回复')
  await input.press('Enter')
  await expectVisible(page.getByText('停止生成这个回复'), 'cancellable chat prompt')
  await page.getByRole('button', { name: '停止生成' }).click()
  await expectVisible(page.getByText('对话已停止'), 'chat stopped state')
  await waitForBridgeCall(page, 'CancelChat')
  await page.getByRole('button', { name: '发送消息' }).waitFor({ state: 'visible', timeout: 12_000 })

  await input.fill('触发失败态')
  await input.press('Enter')
  await expectVisible(page.getByText('触发失败态'), 'failing chat prompt')
  await expectVisible(page.getByText('模拟模型失败，请重试'), 'chat failure state')
  await page.locator('aside').getByRole('button', { name: '重试' }).click()
  await expectVisible(page.getByText('重试后恢复：模型返回稳定结果，未修改章节正文。'), 'chat retry recovery')
  await page.getByRole('button', { name: '发送消息' }).waitFor({ state: 'visible', timeout: 12_000 })

  await input.fill('生成长文本 Markdown 报告')
  await input.press('Enter')
  await expectVisible(page.getByText('生成长文本 Markdown 报告'), 'long markdown prompt')
  await expectVisible(page.getByRole('button', { name: '停止生成' }), 'streaming control during long chat')
  await expectVisible(page.getByRole('heading', { name: '约束检查' }), 'streamed markdown heading')
  await expectVisible(page.getByRole('button', { name: '停止生成' }), 'streaming control after partial markdown render')
  await expectVisible(page.getByText('不要直接写入章节正文'), 'markdown bullet content')
  await expectVisible(page.getByText('scene_guard:'), 'markdown code block')
  await expectVisible(page.getByRole('button', { name: '复制代码' }).first(), 'markdown code copy affordance')
  await expectVisible(page.getByText('第十二段：雨声压住脚步声，回复仍保持可读宽度。'), 'long generated text tail')
  await expectVisible(page.getByText('最终建议：先读后改，不越过审批。'), 'long markdown final marker')
  const longMarkdownContentEvents = await page.evaluate(() =>
    window.__appMockState.emittedEvents.filter((event) =>
      event.name.startsWith('agent:') &&
      event.payload?.type === 2 &&
      (String(event.payload?.data ?? '').includes('约束检查') ||
        String(event.payload?.data ?? '').includes('最终建议'))).length)
  assert(longMarkdownContentEvents >= 2, `Expected streamed markdown content events, got ${longMarkdownContentEvents}.`)

  await assertBridgeCallCount(page, 'SaveContent', saveContentBefore)
  const chapterContentAfter = await page.evaluate(() => window.__appMockState.contentByPath['chapters/1.md'])
  assert.equal(chapterContentAfter, chapterContentBefore, 'Chat workflow must not mutate chapter content.')
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
}

async function verifySettingsWorkflow(page) {
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

async function verifySettingsPersistenceWorkflow(browser, url, consoleErrors, pageErrors) {
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
  await expectVisible(dialog.getByText('bge-small-zh-v1.5'), 'builtin onnx embedding model')
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
  await dialog.getByText('高级路径').click()
  await dialog.locator('#embedding-onnx-model').fill('D:\\mock\\models\\bge-small.onnx')
  await dialog.locator('#embedding-onnx-vocab').fill('D:\\mock\\models\\vocab.txt')
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await waitForBridgeCallCountAfter(page, 'SaveEmbeddingConfig', onnxSaveCount)
  await expectVisible(dialog.getByText('配置已保存'), 'onnx embedding paths saved')
  await assertSavedEmbeddingConfig(page, {
    provider_type: 'onnx',
    provider_key: 'onnx',
    onnx_model_path: 'D:\\mock\\models\\bge-small.onnx',
    onnx_vocab_path: 'D:\\mock\\models\\vocab.txt',
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

async function verifySettingsFailureWorkflow(browser, url, consoleErrors, pageErrors) {
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

async function verifyUpdateWorkflow(page, browser, url, consoleErrors, pageErrors) {
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

async function verifyMetadataPanels(page) {
  await page.getByTitle('角色').click()
  await expectVisible(page.getByRole('heading', { name: /角色/ }), 'characters heading')
  await expectVisible(page.getByText('林岚').first(), 'character fixture')

  await page.getByTitle('地点').click()
  await expectVisible(page.getByRole('heading', { name: /地点/ }), 'locations heading')
  await expectVisible(page.getByText('旧城门').first(), 'location fixture')

  await page.getByTitle('弧线').click()
  await expectVisible(page.getByRole('heading', { name: /弧线节点/ }), 'story arc heading')
  await expectVisible(page.getByText('雨夜调查线').first(), 'story arc fixture')

  await page.getByTitle('时间线').click()
  await expectVisible(page.getByRole('heading', { name: /章节计划/ }), 'timeline heading')
  await expectVisible(page.getByText('桌面水痕').first(), 'timeline fixture')

  await page.getByTitle('读者视角').click()
  await expectVisible(page.getByRole('heading', { name: /读者视角/ }), 'reader heading')
  await expectVisible(page.getByText(/读者知道林岚正在调查/).first(), 'reader fixture')

  await page.getByTitle('偏好').click()
  await expectVisible(page.getByRole('heading', { name: /创作偏好/ }), 'preference heading')
  await expectVisible(page.getByText(/保持受限视角/).first(), 'preference fixture')

  await page.getByTitle('技能').click()
  await expectVisible(page.getByText('技能 (2)'), 'skills side panel')
  await expectVisible(page.getByText('节奏控制').first(), 'skill fixture')
}

async function verifyCompactViewportSmoke(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true }, { width: 900, height: 720 })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'compact workspace title')

  await clickActivity(page, '章节')
  await ensureChapterBlockExpanded(page)
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.locator('.monaco-editor').first(), 'compact editor surface')
  await expectVisible(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'compact chat input')
  await assertSelectedChapterPath(page, 'chapters/1.md')
  await assertActiveTabTitle(page, '第1章 雨夜线索')

  await clickActivity(page, '角色')
  await expectVisible(page.locator('aside').getByText(/角色 \(\d+\)/), 'compact character sidebar')
  await expectVisible(page.getByRole('heading', { name: /角色/ }), 'compact character surface')

  await clickActivity(page, '参考锚定')
  await expectVisible(page.locator('aside').getByText('即将推出'), 'compact reference sidebar placeholder')
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'compact reference surface')

  await clickActivity(page, '风格素材')
  await expectVisible(page.getByRole('heading', { name: /风格素材/ }), 'compact style sample surface')
  await expectVisible(page.getByText('全局雨夜节奏').first(), 'compact style sample card')

  await clickActivity(page, 'Git 历史')
  await expectVisible(page.getByRole('heading', { name: 'Git 历史' }), 'compact Git history surface')
  await expectVisible(page.getByText('rename rain clue chapter').first(), 'compact Git history fixture')

  await clickActivity(page, '章节')
  await expectVisible(page.getByText('章节 (6)'), 'compact chapter sidebar restored')
  await ensureChapterBlockExpanded(page)
  await assertSelectedChapterPath(page, 'chapters/1.md')
  await assertActiveTabTitle(page, '第1章 雨夜线索')
  await expectVisible(page.locator('.monaco-editor').first(), 'compact editor restored after activity transitions')
  await expectVisible(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'compact chat restored after activity transitions')

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await page.screenshot({ path: path.join(outputDir, 'app-09-compact.png'), fullPage: true })
  await page.close()
}

async function verifyLayoutPersistenceWorkflow(page, browser, url, consoleErrors, pageErrors) {
  await expectVisible(page.getByText('全局回归小说'), 'layout workspace title')
  await expectVisible(page.getByRole('separator', { name: '调整侧边栏宽度' }), 'sidebar resize separator')
  await expectVisible(page.getByRole('separator', { name: '调整对话面板宽度' }), 'chat resize separator')

  await assertPanelWidth(page, '调整侧边栏宽度', 280, 2, 'initial sidebar width')
  await assertPanelWidth(page, '调整对话面板宽度', 360, 2, 'initial chat width')

  await dragSeparator(page, '调整侧边栏宽度', 80)
  await waitForLayoutSave(page, { sidebar_width: 360, chat_panel_width: 360 })
  await assertPanelWidth(page, '调整侧边栏宽度', 360, 2, 'dragged sidebar width')

  await dragSeparator(page, '调整对话面板宽度', -80)
  await waitForLayoutSave(page, { sidebar_width: 360, chat_panel_width: 440 })
  await assertPanelWidth(page, '调整对话面板宽度', 440, 2, 'dragged chat width')
  await assertWorkspacePanelsDoNotOverlap(page, 'desktop layout after drag')

  await page.getByTitle('最大化').click()
  await page.waitForFunction(
    () => window.__appMockState.calls.some((call) =>
      call.method === 'SaveWindowSettings' &&
      call.args[0]?.maximized === true &&
      call.args[0]?.width >= 800 &&
      call.args[0]?.height >= 600),
    null,
    { timeout: 12_000 },
  )
  await expectVisible(page.getByTitle('还原'), 'maximized title button')

  await page.reload({ waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'layout workspace after reload')
  await assertPanelWidth(page, '调整侧边栏宽度', 360, 2, 'persisted sidebar width after reload')
  await assertPanelWidth(page, '调整对话面板宽度', 440, 2, 'persisted chat width after reload')

  const saveLayoutCallsBeforeCompact = await bridgeCallCount(page, 'SaveLayoutSettings')
  await page.setViewportSize({ width: 900, height: 720 })
  await page.waitForFunction(() => {
    const widthFor = (label) => {
      const separator = document.querySelector(`[role="separator"][aria-label="${label}"]`)
      const panel = separator?.closest('aside')
      return panel ? Math.round(panel.getBoundingClientRect().width) : 0
    }
    const widths = {
      sidebar: widthFor('调整侧边栏宽度'),
      chat: widthFor('调整对话面板宽度'),
    }
    return widths.sidebar + widths.chat <= 593 &&
      widths.sidebar >= 220 &&
      widths.chat >= 280
  }, null, { timeout: 12_000 })
  const compactWidths = await layoutPanelWidths(page)
  assert(compactWidths.sidebar + compactWidths.chat <= 593, `compact layout should preserve content budget, got ${JSON.stringify(compactWidths)}`)
  await assertWorkspacePanelsDoNotOverlap(page, 'compact layout after viewport shrink')
  assert.equal(
    await bridgeCallCount(page, 'SaveLayoutSettings'),
    saveLayoutCallsBeforeCompact,
    'automatic compact clamping must not overwrite the persisted user layout',
  )

  const corruptPage = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    settings: {
      ...settingsFixture(42),
      sidebar_width: 'not-a-width',
      chat_panel_width: 'not-a-width',
      metadata_panel_width: 'bad-metadata-width',
      window_x: 9999999,
      window_y: -9999999,
      window_width: 100,
      window_height: 200,
      window_maximized: true,
    },
  }, { width: 1280, height: 820 }, 'layout-corrupt-settings')
  await corruptPage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(corruptPage.getByText('全局回归小说'), 'workspace after corrupt layout settings')
  await assertPanelWidth(corruptPage, '调整侧边栏宽度', 280, 2, 'corrupt sidebar width fallback')
  await assertPanelWidth(corruptPage, '调整对话面板宽度', 360, 2, 'corrupt chat width fallback')
  await assertWorkspacePanelsDoNotOverlap(corruptPage, 'layout after corrupt settings fallback')
  await assertBridgeCallCount(corruptPage, 'SaveContent', 0)
  await corruptPage.close()

  await page.getByRole('separator', { name: '调整侧边栏宽度' }).press('ArrowLeft')
  await waitForBridgeCall(page, 'SaveLayoutSettings')
  await page.getByTitle('还原').click()
  await page.waitForFunction(
    () => window.__appMockState.calls.some((call) =>
      call.method === 'SaveWindowSettings' &&
      call.args[0]?.maximized === false),
    null,
    { timeout: 12_000 },
  )

  await assertBridgeCallCount(page, 'SetChatPanelWidth', 0)
  await assertBridgeCallCount(page, 'SaveContent', 0)
}

async function dragSeparator(page, label, deltaX) {
  const handle = page.getByRole('separator', { name: label })
  const box = await handle.boundingBox()
  assert(box, `Expected separator ${label} to have a bounding box.`)
  const startX = label.includes('侧边栏')
    ? box.x + Math.min(1, box.width / 2)
    : box.x + Math.max(1, box.width - 1)
  const startY = box.y + box.height / 2
  await page.mouse.move(startX, startY)
  await page.mouse.down()
  await page.waitForTimeout(50)
  await page.mouse.move(startX + deltaX, startY, { steps: 8 })
  await page.mouse.up()
}

async function waitForLayoutSave(page, expected) {
  await page.waitForFunction(
    (expected) => window.__appMockState.calls.some((call) => {
      if (call.method !== 'SaveLayoutSettings') return false
      return Object.entries(expected).every(([key, value]) => call.args[0]?.[key] === value)
    }),
    expected,
    { timeout: 12_000 },
  )
}

async function assertPanelWidth(page, label, expected, tolerance, description) {
  const actual = await panelWidthBySeparator(page, label)
  assert(
    Math.abs(actual - expected) <= tolerance,
    `Expected ${description} to be ${expected}px +/- ${tolerance}px, got ${actual}px.`,
  )
}

async function panelWidthBySeparator(page, label) {
  return await page.getByRole('separator', { name: label }).evaluate((separator) => {
    const panel = separator.closest('aside')
    return panel ? Math.round(panel.getBoundingClientRect().width) : 0
  })
}

async function layoutPanelWidths(page) {
  return await page.evaluate(() => {
    const widthFor = (label) => {
      const separator = document.querySelector(`[role="separator"][aria-label="${label}"]`)
      const panel = separator?.closest('aside')
      return panel ? Math.round(panel.getBoundingClientRect().width) : 0
    }
    return {
      sidebar: widthFor('调整侧边栏宽度'),
      chat: widthFor('调整对话面板宽度'),
    }
  })
}

async function assertWorkspacePanelsDoNotOverlap(page, description) {
  const result = await page.evaluate(() => {
    const sideHandle = document.querySelector('[role="separator"][aria-label="调整侧边栏宽度"]')
    const chatHandle = document.querySelector('[role="separator"][aria-label="调整对话面板宽度"]')
    const sidebar = sideHandle?.closest('aside')?.getBoundingClientRect()
    const chat = chatHandle?.closest('aside')?.getBoundingClientRect()
    if (!sidebar || !chat) return { ok: false, reason: 'missing panel box' }
    if (sidebar.right > chat.left) {
      return { ok: false, reason: `panels overlap: sidebar right ${sidebar.right}, chat left ${chat.left}` }
    }
    const chatButtons = Array.from(chatHandle.closest('aside')?.querySelectorAll('button') ?? [])
      .filter((button) => (button.textContent ?? '').includes('历史') || (button.textContent ?? '').includes('新对话'))
    const clippedButton = chatButtons.find((button) => {
      const box = button.getBoundingClientRect()
      return box.left < chat.left || box.right > chat.right
    })
    if (clippedButton) return { ok: false, reason: `chat action clipped: ${clippedButton.textContent}` }
    return { ok: true, reason: '' }
  })
  assert.equal(result.ok, true, `${description} should not overlap or clip panel controls: ${result.reason}`)
}

async function verifyMetadataActionWorkflow(browser, url, consoleErrors, pageErrors) {
  await verifyMetadataEmptyStates(browser, url, consoleErrors, pageErrors)
  await verifyMetadataValidationWorkflow(browser, url, consoleErrors, pageErrors)
  await verifyMetadataBridgeFailureRecoveryWorkflow(browser, url, consoleErrors, pageErrors)

  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    confirmResult: true,
  })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'metadata action workspace')

  await verifyCharacterActions(page)
  await verifyLocationActions(page)
  await verifyStoryArcActions(page)
  await verifyTimelineActions(page)
  await verifyReaderActions(page)
  await verifyPreferenceActions(page)
  await verifyProfileActions(page)
  await verifySkillActions(page)

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}

async function verifyMetadataEmptyStates(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    characters: [],
    locations: [],
    storyArcs: [],
    arcNodes: [],
    chapterPlans: [],
    timelineEntries: [],
    readerPerspectives: [],
    preferences: { global: [], novel: [] },
    writingActivity: [],
    writingStats: {
      total_words: 0,
      total_days_active: 0,
      current_streak: 0,
      longest_streak: 0,
      total_novels: 1,
      total_chapters: 2,
    },
    skills: [],
  })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'metadata empty workspace')

  await clickActivity(page, '角色')
  await expectVisible(page.locator('main').getByText('暂无角色'), 'empty characters state')
  await clickActivity(page, '地点')
  await expectVisible(page.locator('main').getByText('暂无地点'), 'empty locations state')
  await clickActivity(page, '弧线')
  await expectVisible(page.locator('main').getByText('暂无叙事弧线'), 'empty story arc state')
  await clickActivity(page, '时间线')
  await expectVisible(page.locator('main').getByText('暂无伏笔或用户指令'), 'empty timeline state')
  await clickActivity(page, '读者视角')
  await expectVisible(page.locator('main').getByText('暂无读者认知数据'), 'empty reader state')
  await clickActivity(page, '偏好')
  await expectVisible(page.locator('main').getByText('暂无全局偏好'), 'empty global preference state')
  await expectVisible(page.locator('main').getByText('暂无本书偏好'), 'empty novel preference state')
  await clickActivity(page, '技能')
  await expectVisible(page.locator('aside').getByText('暂无技能'), 'empty skill state')
  await page.locator('header').getByRole('button', { name: '个人中心' }).click()
  await expectVisible(page.getByText('还没有写作记录。开始写吧，每天的字数都会被记录下来。'), 'empty profile writing state')
  await page.close()
}

async function verifyMetadataValidationWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'metadata validation workspace')

  await clickActivity(page, '角色')
  await page.getByRole('button', { name: '新建角色' }).click()
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await expectVisible(page.locator('main').getByText('请输入角色名称'), 'character validation message')
  await assertBridgeCallCount(page, 'CreateCharacter', 0)

  await clickActivity(page, '地点')
  await page.getByRole('button', { name: '新建地点' }).click()
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await expectVisible(page.locator('main').getByText('请输入地点名称'), 'location validation message')
  await assertBridgeCallCount(page, 'CreateLocation', 0)

  await clickActivity(page, '弧线')
  await page.getByRole('button', { name: '新弧线' }).click()
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await expectVisible(page.locator('main').getByText('请输入弧线名称'), 'story arc validation message')
  await assertBridgeCallCount(page, 'CreateStoryArc', 0)

  await clickActivity(page, '时间线')
  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await assertButtonDisabled(page.locator('main').getByRole('button', { name: '创建' }).last(), 'timeline create before title')
  await assertBridgeCallCount(page, 'CreateTimelineEntry', 0)

  await clickActivity(page, '读者视角')
  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await assertButtonDisabled(page.locator('main').getByRole('button', { name: '创建' }).last(), 'reader create before content')
  await assertBridgeCallCount(page, 'CreateReaderPerspective', 0)

  await clickActivity(page, '偏好')
  await page.locator('section').filter({ hasText: '全局偏好' }).getByRole('button', { name: '添加' }).click()
  await assertButtonDisabled(page.locator('main').getByRole('button', { name: '创建' }).last(), 'preference create before content')
  await assertBridgeCallCount(page, 'CreatePreference', 0)

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}

async function verifyMetadataBridgeFailureRecoveryWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      CreateCharacter: { mode: 'storage', message: '模拟角色保存失败' },
    },
  })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'metadata bridge failure workspace')

  await clickActivity(page, '角色')
  await page.getByRole('button', { name: '新建角色' }).click()
  await page.getByPlaceholder('角色名称').fill('故障恢复角色')
  await page.getByPlaceholder('角色外貌、背景等自然语言描述').fill('第一次保存会失败，切换回来后重试成功。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateCharacter')
  await expectVisible(page.locator('main').getByText('模拟角色保存失败'), 'character create failure message')

  const failedCreateCount = await bridgeCallCount(page, 'CreateCharacter')
  await clickActivity(page, '地点')
  await expectVisible(page.getByRole('heading', { name: /地点/ }), 'metadata recovery navigation target')
  await clickActivity(page, '角色')
  await expectVisible(page.getByText('林岚').first(), 'characters recovered after failed create')
  await page.getByRole('button', { name: '新建角色' }).click()
  await page.getByPlaceholder('角色名称').fill('故障恢复角色')
  await page.getByPlaceholder('角色外貌、背景等自然语言描述').fill('第二次保存成功，证明桥失败后可恢复。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCallCountAfter(page, 'CreateCharacter', failedCreateCount)
  await expectVisible(page.getByText('故障恢复角色').first(), 'character recovered after failed create retry')

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}

async function verifyCharacterActions(page) {
  await clickActivity(page, '角色')
  await page.getByRole('button', { name: '新建角色' }).click()
  await page.getByPlaceholder('角色名称').fill('沈望')
  await page.getByPlaceholder('角色外貌、背景等自然语言描述').fill('在雨夜负责确认门外脚印。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateCharacter')
  await expectVisible(page.getByText('沈望').first(), 'created character')

  await clickCardAction(page, '沈望', '编辑')
  await page.getByPlaceholder('角色名称').fill('沈望-修订')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'UpdateCharacter')
  await expectVisible(page.getByText('沈望-修订').first(), 'updated character')

  await clickCardAction(page, '沈望-修订', '删除')
  await waitForBridgeCall(page, 'DeleteCharacter')
  await expectHidden(page.locator('main').getByText('沈望-修订'), 'deleted character')
}

async function verifyLocationActions(page) {
  await clickActivity(page, '地点')
  await page.getByRole('button', { name: '新建地点' }).click()
  await page.getByPlaceholder('地点名称').fill('旧钟楼')
  await page.getByPlaceholder('如：森林、城市、洞穴').fill('建筑')
  await page.getByPlaceholder('环境氛围、特色等自然语言描述').fill('能俯瞰旧城门雨线的钟楼。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateLocation')
  await expectVisible(page.getByText('旧钟楼').first(), 'created location')

  await clickCardAction(page, '旧钟楼', '编辑')
  await page.getByPlaceholder('地点名称').fill('旧钟楼-修订')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'UpdateLocation')
  await expectVisible(page.getByText('旧钟楼-修订').first(), 'updated location')

  await clickCardAction(page, '旧钟楼-修订', '删除')
  await waitForBridgeCall(page, 'DeleteLocation')
  await expectHidden(page.locator('main').getByText('旧钟楼-修订'), 'deleted location')
}

async function verifyStoryArcActions(page) {
  await clickActivity(page, '弧线')
  await page.getByRole('button', { name: '新弧线' }).click()
  await page.getByPlaceholder('弧线名称').fill('真相回收线')
  await page.getByPlaceholder('弧线整体描述').fill('覆盖弧线创建流程。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateStoryArc')
  await expectVisible(page.getByText('真相回收线').first(), 'created story arc')

  await page.getByRole('button', { name: '新建节点' }).click()
  await page.getByPlaceholder('节点标题').fill('门外脚印')
  await page.getByPlaceholder('节点详情').fill('脚印把旧钟楼和旧城门连起来。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'CreateArcNode')
  await expectVisible(page.getByText('门外脚印').first(), 'created arc node')

  await clickCardAction(page, '门外脚印', '标记完成')
  await waitForBridgeCall(page, 'UpdateArcNode')
  await expectVisible(page.getByText('已完成').first(), 'arc node quick status')

  await clickCardAction(page, '门外脚印', '删除')
  await waitForBridgeCall(page, 'DeleteArcNode')
  await expectHidden(page.locator('main').getByText('门外脚印'), 'deleted arc node')
}

async function verifyTimelineActions(page) {
  await clickActivity(page, '时间线')
  await page.locator('section').filter({ hasText: '章节计划' }).getByTitle('编辑').click({ force: true })
  await page.getByPlaceholder('下一章计划内容...').fill('下一章让钟楼线索与旧城门交叉。')
  await page.locator('section').filter({ hasText: '章节计划' }).getByRole('button', { name: '保存' }).click()
  await waitForBridgeCall(page, 'UpdateChapterPlan')
  await expectVisible(page.getByText('下一章让钟楼线索与旧城门交叉。'), 'updated chapter plan')

  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await page.getByPlaceholder('简短标题').fill('钥匙回收')
  await page.getByPlaceholder('详细描述').fill('钥匙在第二章前被重新提及。')
  await page.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCall(page, 'CreateTimelineEntry')
  await expectVisible(page.getByText('钥匙回收').first(), 'created timeline entry')

  await clickCardAction(page, '钥匙回收', '标记已回收')
  await waitForBridgeCall(page, 'UpdateTimelineEntry')
  await expectVisible(page.getByText('已回收').first(), 'timeline quick status')
}

async function verifyReaderActions(page) {
  await clickActivity(page, '读者视角')
  await page.locator('main').getByRole('button', { name: '新建' }).click()
  await page.getByPlaceholder('读者知道/想知道/误以为的事情').fill('读者误以为钟楼里的人已经离开。')
  await page.getByPlaceholder('真实情况是什么').fill('钟楼里的人仍在观察旧城门。')
  await page.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCall(page, 'CreateReaderPerspective')
  await expectVisible(page.getByText(/读者误以为钟楼里的人/).first(), 'created reader entry')

  await expectVisible(page.getByText('作者视角真相'), 'reader inspect detail')
  await clickCardAction(page, '读者误以为钟楼里的人', '标记已回收')
  await waitForBridgeCall(page, 'UpdateReaderPerspective')
  await expectVisible(page.getByText(/第1章回收/).first(), 'reader quick reveal')

  await clickCardAction(page, '读者误以为钟楼里的人', '删除')
  await waitForBridgeCall(page, 'DeleteReaderPerspective')
  await expectHidden(page.locator('main').getByText(/读者误以为钟楼里的人/), 'deleted reader entry')
}

async function verifyPreferenceActions(page) {
  await clickActivity(page, '偏好')
  await page.locator('section').filter({ hasText: '全局偏好' }).getByRole('button', { name: '添加' }).click()
  await page.getByPlaceholder('风格、对话、世界观...').fill('对白')
  await page.getByPlaceholder('偏好内容').fill('对话保留半句停顿。')
  await page.locator('main').getByRole('button', { name: '创建' }).last().click()
  await waitForBridgeCall(page, 'CreatePreference')
  await expectVisible(page.getByText('对话保留半句停顿。'), 'created preference')

  await clickCardAction(page, '对话保留半句停顿。', '编辑')
  await page.getByPlaceholder('偏好内容').fill('对话保留半句停顿，避免提前解释。')
  await page.locator('main').getByRole('button', { name: '保存' }).last().click()
  await waitForBridgeCall(page, 'UpdatePreference')
  await expectVisible(page.getByText('对话保留半句停顿，避免提前解释。'), 'updated preference')

  await clickCardAction(page, '对话保留半句停顿，避免提前解释。', '删除')
  await waitForBridgeCall(page, 'DeletePreference')
  await expectHidden(page.locator('main').getByText('对话保留半句停顿，避免提前解释。'), 'deleted preference')
}

async function verifyProfileActions(page) {
  await page.locator('header').getByRole('button', { name: '个人中心' }).click()
  await expectVisible(page.getByText('累计字数'), 'profile stats before edit')
  await page.getByText('Mock User').click()
  await page.locator('main').getByRole('textbox').fill('Metadata Tester')
  await page.keyboard.press('Enter')
  await waitForBridgeCall(page, 'SaveUserName')
  await expectVisible(page.getByText('Metadata Tester'), 'updated profile name')
}

async function verifySkillActions(page) {
  await clickActivity(page, '技能')
  await page.getByPlaceholder('搜索...').fill('节奏')
  await expectVisible(page.locator('aside').getByText('节奏控制'), 'filtered skill')
  await page.locator('aside').getByRole('button', { name: /节奏控制/ }).click()
  await waitForBridgeCallArg(page, 'GetContent', 1, 'skills/节奏控制.md')
  await expectVisible(page.getByText('保持停顿和动作之间的张力。'), 'inspected skill content')

  await clickCardAction(page.locator('aside'), '节奏控制', '删除技能')
  await waitForBridgeCall(page, 'DeleteSkill')
  await expectVisible(page.getByText('技能 (1)'), 'skill count after delete')
  await expectHidden(page.locator('aside').getByText('节奏控制'), 'deleted skill hidden in side panel')
}

function errorAlert(page, text) {
  return page.getByRole('alert').filter({ hasText: text }).first()
}

async function assertCopyableDiagnostic(page, alert, expectedBridgeMethod) {
  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await alert.getByRole('button', { name: '复制错误诊断' }).click()
  await page.waitForFunction(() => typeof window.__appMockClipboardText === 'string' && window.__appMockClipboardText.length > 0)
  const copied = await page.evaluate(() => window.__appMockClipboardText)
  const diagnostic = JSON.parse(copied)

  assert.equal(diagnostic.bridge_method, expectedBridgeMethod)
  assert.equal(typeof diagnostic.timestamp, 'string')
  assert(copied.includes('[REDACTED]'), 'copied diagnostics should include redaction markers')
  assert(copied.includes('[REDACTED_SOURCE_TEXT]'), 'copied diagnostics should redact source text')
  assertNoSensitiveDiagnosticText(copied, `copied ${expectedBridgeMethod} diagnostics`)
}

async function assertNoSensitiveDiagnosticsVisible(page) {
  const bodyText = await page.locator('body').textContent()
  assertNoSensitiveDiagnosticText(bodyText ?? '', 'visible error feedback')
}

function assertNoSensitiveDiagnosticText(text, label) {
  const forbidden = [
    'sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890',
    'live-error-token-abcdefghijklmnopqrstuvwxyz',
    'open-error-token-abcdefghijklmnopqrstuvwxyz',
    'update-check-token-abcdefghijklmnopqrstuvwxyz',
    'update-settings-token-abcdefghijklmnopqrstuvwxyz',
    'novel-create-token-abcdefghijklmnopqrstuvwxyz',
    'novel-update-token-abcdefghijklmnopqrstuvwxyz',
    'novel-delete-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-search-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-detail-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-create-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-update-token-abcdefghijklmnopqrstuvwxyz',
    'style-sample-delete-token-abcdefghijklmnopqrstuvwxyz',
    'export-error-token-abcdefghijklmnopqrstuvwxyz',
    'content-save-token-abcdefghijklmnopqrstuvwxyz',
    'skill-edit-save-token-abcdefghijklmnopqrstuvwxyz',
    'legacy-style-extract-token-abcdefghijklmnopqrstuvwxyz',
    'legacy-style-save-token-abcdefghijklmnopqrstuvwxyz',
    'reader-create-token-abcdefghijklmnopqrstuvwxyz',
    'reader-quick-reveal-token-abcdefghijklmnopqrstuvwxyz',
    'reader-update-token-abcdefghijklmnopqrstuvwxyz',
    'reader-delete-token-abcdefghijklmnopqrstuvwxyz',
    'preference-create-token-abcdefghijklmnopqrstuvwxyz',
    'preference-update-token-abcdefghijklmnopqrstuvwxyz',
    'preference-delete-token-abcdefghijklmnopqrstuvwxyz',
    'story-arc-create-token-abcdefghijklmnopqrstuvwxyz',
    'story-arc-update-token-abcdefghijklmnopqrstuvwxyz',
    'story-arc-delete-token-abcdefghijklmnopqrstuvwxyz',
    'arc-node-create-token-abcdefghijklmnopqrstuvwxyz',
    'arc-node-quick-token-abcdefghijklmnopqrstuvwxyz',
    'arc-node-update-token-abcdefghijklmnopqrstuvwxyz',
    'arc-node-delete-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-plan-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-create-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-quick-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-update-token-abcdefghijklmnopqrstuvwxyz',
    'timeline-delete-token-abcdefghijklmnopqrstuvwxyz',
    'model-test-token-abcdefghijklmnopqrstuvwxyz',
    'model-save-token-abcdefghijklmnopqrstuvwxyz',
    'model-discovery-token-abcdefghijklmnopqrstuvwxyz',
    'embedding-test-token-abcdefghijklmnopqrstuvwxyz',
    'embedding-save-token-abcdefghijklmnopqrstuvwxyz',
    'git-author-save-token-abcdefghijklmnopqrstuvwxyz',
    'rename-error-token-abcdefghijklmnopqrstuvwxyz',
    'import-error-token-abcdefghijklmnopqrstuvwxyz',
    'style-error-token-abcdefghijklmnopqrstuvwxyz',
    'detail-error-token-abcdefghijklmnopqrstuvwxyz',
    'open-sesame-secret',
    '敏感源文本敏感源文本敏感源文本',
  ]
  for (const value of forbidden) {
    assert(!text.includes(value), `${label} leaked sensitive diagnostic text: ${value}`)
  }
}

async function verifyReferenceSmoke(page) {
  await page.getByTitle('参考锚定').click()
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'reference heading')
  await expectVisible(page.getByText('全局雨夜参考').first(), 'reference anchor fixture')
  await expectVisible(page.getByText('默认编排').first(), 'orchestration panel')
}

async function verifyReferenceErrorFeedbackWorkflow(browser, url, consoleErrors, pageErrors) {
  const details = sensitiveDiagnosticDetails()
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      CreateReferenceAnchor: {
        mode: 'storage',
        code: 'REFERENCE_ANCHOR_CREATE_FAILED',
        message: '参考锚点创建失败：Bearer reference-create-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      RebuildReferenceAnchor: {
        mode: 'storage',
        code: 'REFERENCE_ANCHOR_REBUILD_FAILED',
        message: '锚点重建失败：Bearer reference-rebuild-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      SearchReferenceMaterials: {
        mode: 'storage',
        code: 'REFERENCE_MATERIAL_SEARCH_FAILED',
        message: '参考材料搜索失败：Bearer reference-search-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      GenerateReferenceChapterBlueprint: {
        mode: 'storage',
        code: 'REFERENCE_BLUEPRINT_GENERATE_FAILED',
        message: '章节蓝图生成失败：Bearer reference-blueprint-generate-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      ReviewReferenceChapterBlueprint: {
        mode: 'storage',
        code: 'REFERENCE_BLUEPRINT_REVIEW_FAILED',
        message: '蓝图评审失败：Bearer reference-blueprint-review-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      ApproveReferenceChapterBlueprint: {
        mode: 'storage',
        code: 'REFERENCE_BLUEPRINT_APPROVE_FAILED',
        message: '蓝图批准失败：Bearer reference-blueprint-approve-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
      BindReferenceBlueprintMaterials: {
        mode: 'storage',
        code: 'REFERENCE_BLUEPRINT_BIND_FAILED',
        message: '蓝图材料绑定失败：Bearer reference-blueprint-bind-token-abcdefghijklmnopqrstuvwxyz',
        details,
        retryable: true,
      },
    },
  }, undefined, 'reference-error-feedback')
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'reference error workspace')
  await page.getByTitle('参考锚定').click()
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'reference error heading')

  await page.getByPlaceholder('参考书名').fill('错误反馈参考')
  await page.getByLabel('本地路径').fill('D:\\books\\reference-error.md')
  const createBefore = await bridgeCallCount(page, 'CreateReferenceAnchor')
  await page.getByTestId('reference-import-panel').getByRole('button', { name: '创建' }).click()
  await waitForBridgeCallCountAfter(page, 'CreateReferenceAnchor', createBefore)
  const createAlert = errorAlert(page, '参考锚点创建失败')
  await expectVisible(createAlert, 'reference create failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, createAlert, 'CreateReferenceAnchor')

  const rebuildBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  await page.getByTitle('重建').first().click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', rebuildBefore)
  const rebuildAlert = errorAlert(page, '锚点重建失败')
  await expectVisible(rebuildAlert, 'reference rebuild failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, rebuildAlert, 'RebuildReferenceAnchor')

  const searchBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  await page.getByRole('button', { name: /浏览 全局雨夜参考 的材料/ }).click()
  await waitForBridgeCallCountAfter(page, 'SearchReferenceMaterials', searchBefore)
  const searchAlert = errorAlert(page, '参考材料搜索失败')
  await expectVisible(searchAlert, 'reference material search failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, searchAlert, 'SearchReferenceMaterials')

  await page.getByRole('button', { name: /打开高级模式/ }).click()
  const blueprintPanel = page.getByTestId('reference-blueprint-panel')
  await blueprintPanel.getByLabel('章节号').fill('1')
  await blueprintPanel.getByLabel('标题').fill('错误反馈蓝图')
  const generateBefore = await bridgeCallCount(page, 'GenerateReferenceChapterBlueprint')
  await blueprintPanel.getByRole('button', { name: /生成蓝图/ }).click()
  await waitForBridgeCallCountAfter(page, 'GenerateReferenceChapterBlueprint', generateBefore)
  const generateAlert = errorAlert(page, '章节蓝图生成失败')
  await expectVisible(generateAlert, 'reference blueprint generate failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, generateAlert, 'GenerateReferenceChapterBlueprint')

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('GenerateReferenceChapterBlueprint') })
  const generateSuccessBefore = await bridgeCallCount(page, 'GenerateReferenceChapterBlueprint')
  await blueprintPanel.getByRole('button', { name: /生成蓝图/ }).click()
  await waitForBridgeCallCountAfter(page, 'GenerateReferenceChapterBlueprint', generateSuccessBefore)
  await expectVisible(page.getByText('章节蓝图已生成'), 'reference blueprint generated after clearing fault')

  const detail = page.getByTestId('reference-blueprint-detail')
  const reviewBefore = await bridgeCallCount(page, 'ReviewReferenceChapterBlueprint')
  await detail.getByRole('button', { name: /评审/ }).click()
  await waitForBridgeCallCountAfter(page, 'ReviewReferenceChapterBlueprint', reviewBefore)
  const reviewAlert = errorAlert(page, '蓝图评审失败')
  await expectVisible(reviewAlert, 'reference blueprint review failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, reviewAlert, 'ReviewReferenceChapterBlueprint')

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('ReviewReferenceChapterBlueprint') })
  const reviewSuccessBefore = await bridgeCallCount(page, 'ReviewReferenceChapterBlueprint')
  await detail.getByRole('button', { name: /评审/ }).click()
  await waitForBridgeCallCountAfter(page, 'ReviewReferenceChapterBlueprint', reviewSuccessBefore)
  await expectVisible(page.getByText('蓝图评审已完成'), 'reference blueprint reviewed after clearing fault')

  const approveBefore = await bridgeCallCount(page, 'ApproveReferenceChapterBlueprint')
  await detail.getByRole('button', { name: /批准/ }).click()
  await waitForBridgeCallCountAfter(page, 'ApproveReferenceChapterBlueprint', approveBefore)
  const approveAlert = errorAlert(page, '蓝图批准失败')
  await expectVisible(approveAlert, 'reference blueprint approve failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, approveAlert, 'ApproveReferenceChapterBlueprint')

  await page.evaluate(() => { window.__appMockState.clearFaultQueue('ApproveReferenceChapterBlueprint') })
  const approveSuccessBefore = await bridgeCallCount(page, 'ApproveReferenceChapterBlueprint')
  await detail.getByRole('button', { name: /批准/ }).click()
  await waitForBridgeCallCountAfter(page, 'ApproveReferenceChapterBlueprint', approveSuccessBefore)
  await expectVisible(page.getByText('蓝图已批准'), 'reference blueprint approved after clearing fault')

  const bindBefore = await bridgeCallCount(page, 'BindReferenceBlueprintMaterials')
  await detail.getByRole('button', { name: /绑定/ }).click()
  await waitForBridgeCallCountAfter(page, 'BindReferenceBlueprintMaterials', bindBefore)
  const bindAlert = errorAlert(page, '蓝图材料绑定失败')
  await expectVisible(bindAlert, 'reference blueprint bind failure callout')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, bindAlert, 'BindReferenceBlueprintMaterials')

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await page.close()
}

async function verifyStyleSampleWorkflow(page) {
  await clickActivity(page, '风格素材')
  await expectVisible(page.getByRole('heading', { name: /风格素材/ }), 'style sample heading')
  await expectVisible(page.getByText('全局雨夜节奏').first(), 'global style sample card')
  await expectVisible(page.getByText('词数').first(), 'style sample word count label')
  await expectVisible(page.getByText('26').first(), 'style sample word count value')
  await expectVisible(page.getByText('句长分布').first(), 'style sample distribution label')

  await page.getByRole('checkbox', { name: '选择样本 全局雨夜节奏' }).check()
  await expectVisible(page.getByText('已选 1 个样本').first(), 'style sample selected state')

  await page.getByRole('button', { name: '仅当前作品' }).click()
  await expectHidden(page.getByText('全局雨夜节奏').first(), 'global style sample hidden by novel-only filter')
  await expectVisible(page.getByText('近身内心动作').first(), 'local style sample remains visible')

  await page.getByRole('button', { name: '包含全局' }).click()
  await page.getByPlaceholder('搜索样本...').fill('对白')
  await expectVisible(page.getByText('全局雨夜节奏').first(), 'style sample search result')
  await expectHidden(page.getByText('近身内心动作').first(), 'style sample query hides nonmatching local sample')

  await page.getByPlaceholder('标签过滤...').fill('克制')
  await expectVisible(page.getByText('全局雨夜节奏').first(), 'style sample tag filter result')

  await page.getByRole('button', { name: '清除筛选' }).click()
  await expectVisible(page.getByText('第 1 / 2 页').first(), 'style sample first page status')
  await page.getByRole('button', { name: '下一页' }).click()
  await expectVisible(page.getByText('段落留白记录').first(), 'style sample second page item')
  await expectVisible(page.getByText('第 2 / 2 页').first(), 'style sample second page status')
  await page.getByRole('button', { name: '上一页' }).click()

  await page.getByRole('button', { name: '新建样本' }).click()
  await page.locator('form').getByLabel('样本名称').fill('新建雨声样本')
  await page.locator('form').getByLabel('样本内容').fill('雨落在窗上。她没有解释。')
  await page.locator('form').getByLabel('标签').fill('雨夜;新建')
  await page.getByRole('button', { name: '保存样本' }).click()
  await expectVisible(page.getByText('新建雨声样本').first(), 'created style sample card')

  await page.getByRole('button', { name: '编辑 新建雨声样本' }).click()
  await page.locator('form').getByLabel('样本名称').fill('新建雨声样本修订')
  await page.getByRole('button', { name: '保存样本' }).click()
  await expectVisible(page.getByText('新建雨声样本修订').first(), 'updated style sample card')

  await page.evaluate(() => { window.__appMockState.failNextStyleSampleDelete = true })
  await page.getByRole('button', { name: '删除 新建雨声样本修订' }).click()
  await expectVisible(page.getByText('模拟样本删除失败'), 'style sample delete failure message')
  await expectVisible(page.getByText('新建雨声样本修订').first(), 'style sample remains after delete failure')

  await page.getByRole('button', { name: '删除 新建雨声样本修订' }).click()
  await expectHidden(page.getByText('新建雨声样本修订').first(), 'style sample removed after confirmed delete')

  await page.getByRole('button', { name: '查看样本 全局雨夜节奏' }).click()
  await expectVisible(page.getByText('完整统计'), 'style sample detail stats section')
  await expectVisible(page.getByText('引号密度'), 'style sample quote density stat')
  await expectVisible(page.getByText('段落均长'), 'style sample paragraph stat')

  await expectVisible(page.getByRole('heading', { name: '风格技能抽取' }), 'style extraction panel')
  await page.getByLabel('技能名称').fill('全局雨夜技能')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByRole('heading', { name: '技能预览' }), 'style skill preview')
  await expectVisible(page.getByText('source_sample_ids: 1'), 'style skill source sample ids')
  await expectVisible(page.getByText('短句推进，动作留白。'), 'style skill generated guidance')
  await page.getByRole('button', { name: '保存技能' }).click()
  await waitForSaveContent(page, 'skills/全局雨夜技能.md', 'source_sample_ids: 1')
  await expectVisible(page.getByText('技能已保存').first(), 'style skill saved state')

  await page.getByLabel('画像标题').fill('样本风格画像')
  await page.getByRole('button', { name: '构建画像' }).click()
  await expectVisible(page.getByText('风格画像已构建').first(), 'style sample profile built state')

  const profileBuildCall = await page.evaluate(() =>
    window.__appMockState.calls.find((call) => call.method === 'BuildReferenceStyleProfile'))
  assert(profileBuildCall, 'style sample workflow must build a reference style profile from selected samples')
  assert.deepEqual(profileBuildCall.args?.[0]?.style_sample_ids, [1], 'style sample profile build must pass selected style_sample_ids')
  assert.deepEqual(profileBuildCall.args?.[0]?.anchor_ids, [], 'style sample profile build must not fabricate reference anchors')
  const sampleProfile = await page.evaluate(() =>
    window.__appMockState.referenceStyleProfiles?.find((profile) =>
      Array.isArray(profile.source_style_sample_ids) &&
      profile.source_style_sample_ids.includes(1)))
  assert(sampleProfile, 'style sample workflow must persist a mock reference style profile payload')
  assert.deepEqual(sampleProfile.source_anchor_ids, [], 'sample-backed profile payload must not contain fabricated anchors')
  assert.deepEqual(sampleProfile.source_style_sample_ids, [1], 'sample-backed profile payload must preserve source style sample ids')
  assert(sampleProfile.evidence_spans?.length > 0, 'sample-backed profile payload must include source evidence')
  assert(
    sampleProfile.evidence_spans.every((evidence) =>
      evidence.source_type === 'style_sample' &&
      evidence.style_sample_id === 1 &&
      !Object.prototype.hasOwnProperty.call(evidence, 'text') &&
      !Object.prototype.hasOwnProperty.call(evidence, 'content')),
    'sample-backed profile evidence must use sample source metadata without copied text')

  await page.evaluate(() => { window.__appMockState.nextStyleSkillExtractionDelayMs = 900 })
  await page.getByLabel('技能名称').fill('取消风格技能')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByRole('button', { name: '取消抽取' }), 'style extraction cancel button')
  await page.getByRole('button', { name: '取消抽取' }).click()
  await expectVisible(page.getByText('抽取已取消').first(), 'style extraction cancelled state')

  await page.evaluate(() => { window.__appMockState.nextStyleSkillExtractionMode = 'invalid_frontmatter' })
  await page.getByLabel('技能名称').fill('坏格式风格')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByText('模型返回的技能 Markdown 未通过校验').first(), 'style extraction validation failure')

  await page.getByLabel('技能名称').fill('保存失败风格')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByRole('heading', { name: '技能预览' }), 'style skill preview before save failure')
  await page.evaluate(() => { window.__appMockState.failNextSaveContent = true })
  await page.getByRole('button', { name: '保存技能' }).click()
  await expectVisible(page.getByText('模拟保存失败，请重试').first(), 'style skill save failure message')

  const chapterSaves = await page.evaluate(() =>
    window.__appMockState.calls
      .filter((call) => call.method === 'SaveContent')
      .map((call) => String(call.args?.[0]?.path ?? ''))
      .filter((path) => path.startsWith('chapters/')))
  assert.deepEqual(chapterSaves, [], 'style sample workflow must not mutate chapter content')

  const bypassMethods = await page.evaluate(() =>
    window.__appMockState.calls
      .map((call) => call.method)
      .filter((method) => method === 'ApproveReferenceChapterBlueprint' || method === 'BindReferenceBlueprintMaterials'))
  assert.deepEqual(bypassMethods, [], 'style sample workflow must not approve or bind reference blueprints')
}

async function verifyChapterRangeSelectorWorkflow(page) {
  await clickActivity(page, '叙事模式')
  await expectVisible(page.getByRole('heading', { name: '叙事模式' }), 'narrative pattern heading')
  await expectVisible(page.getByRole('heading', { name: '章节范围' }), 'chapter range selector heading')
  await expectVisible(page.getByText('全部 6 章'), 'initial all chapter summary')
  await expectVisible(page.getByText('chapter_ranges=1-6'), 'initial backend range payload')

  await page.getByRole('button', { name: '清空' }).click()
  await expectVisible(page.getByText('未选择章节 / 共 6 章'), 'cleared chapter range summary')
  await expectVisible(page.getByText('未选择章节').first(), 'cleared backend range status')

  await page.getByLabel('起始').fill('2')
  await page.getByLabel('结束').fill('4')
  await page.getByRole('button', { name: '添加范围' }).click()
  await expectVisible(page.getByText('已选 3 / 6 章：第 2-4 章'), 'single chapter range summary')
  await expectVisible(page.getByText('chapter_ranges=2-4'), 'single backend range payload')

  await page.getByLabel('起始').fill('4')
  await page.getByLabel('结束').fill('6')
  await page.getByRole('button', { name: '添加范围' }).click()
  await expectVisible(page.getByText('已选 5 / 6 章：第 2-6 章'), 'merged overlapping chapter range summary')
  await expectVisible(page.getByText('chapter_ranges=2-6'), 'merged backend range payload')

  await page.getByLabel('搜索章节').fill('钟楼')
  const selector = page.locator('main').getByRole('region', { name: '章节范围' })
  await expectVisible(selector.getByText('钟楼回声').first(), 'chapter range search result')
  await expectHidden(selector.getByText('雨夜线索').first(), 'chapter range search hides unmatched chapter')
  await page.getByLabel('搜索章节').fill('')

  await page.getByLabel('选择章节 3 钟楼回声').uncheck()
  await expectVisible(page.getByText('已选 4 / 6 章：第 2、4-6 章'), 'individual chapter toggle splits range')
  await expectVisible(page.getByText('chapter_ranges=2-2,4-6'), 'split backend range payload')

  await page.getByRole('button', { name: '反选' }).click()
  await expectVisible(page.getByText('已选 2 / 6 章：第 1、3 章'), 'inverted range summary')
  await expectVisible(page.getByText('chapter_ranges=1-1,3-3'), 'inverted backend range payload')

  await page.getByRole('button', { name: '全部' }).click()
  await expectVisible(page.getByText('全部 6 章'), 'all-chapter range summary after button')
  await expectVisible(page.getByText('chapter_ranges=1-6'), 'all backend range payload')

  await page.getByRole('button', { name: '锁定选择' }).click()
  await expectVisible(page.getByRole('button', { name: '已锁定' }), 'locked selector state')
  await assertDisabled(page.getByRole('button', { name: '清空' }), 'clear button disabled while selector locked')
  await assertDisabled(page.getByLabel('搜索章节'), 'search disabled while selector locked')
  await assertDisabled(page.getByLabel('选择章节 1 雨夜线索'), 'chapter checkbox disabled while selector locked')

  const patternCalls = await page.evaluate(() =>
    window.__appMockState.calls
      .map((call) => call.method)
      .filter((method) => method === 'StartNarrativePatternExtraction'))
  assert.deepEqual(patternCalls, [], 'chapter selector task must not start narrative extraction')

  await page.getByRole('button', { name: '已锁定' }).click()
  await page.getByLabel('技能名称').fill('雨夜结构技能')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByText('正在识别叙事边界。'), 'narrative pattern boundary progress')
  await expectVisible(page.getByText('章节摘要已完成。'), 'narrative pattern summary progress')
  await expectVisible(page.getByText('正在压缩叙事阶段：轮次 1，批次 1/1。'), 'narrative pattern phase progress')
  await expectVisible(page.getByRole('heading', { name: '技能预览' }), 'narrative pattern skill preview panel')
  await expectVisible(page.getByText('generated_by: narrative_pattern_extraction'), 'narrative pattern skill provenance')
  await expectVisible(page.getByText('## 边界提示'), 'narrative pattern boundary inspectable preview')
  await expectVisible(page.getByText('## 章节摘要'), 'narrative pattern summary inspectable preview')
  await expectVisible(page.getByText('## 阶段压缩'), 'narrative pattern phase inspectable preview')
  await expectVisible(page.getByText('Trace entries (5)'), 'narrative pattern trace entries')

  await page.getByRole('button', { name: '保存技能' }).click()
  await waitForSaveContent(page, 'skills/雨夜结构技能.md', 'generated_by: narrative_pattern_extraction')
  await expectVisible(page.getByText('技能已保存。').first(), 'narrative pattern saved state')

  const successfulStart = await page.evaluate(() =>
    window.__appMockState.calls
      .filter((call) => call.method === 'StartNarrativePatternExtraction')
      .at(-1))
  assert(successfulStart, 'narrative pattern workflow must call StartNarrativePatternExtraction')
  assert.deepEqual(successfulStart.args?.[0]?.chapter_ranges, [{ start_chapter: 1, end_chapter: 6 }], 'narrative pattern start must pass normalized chapter_ranges')
  assert.equal(successfulStart.args?.[0]?.selected_chapter_ids, null, 'narrative pattern start should let backend derive ids from chapter_ranges for large selections')
  assert.equal(successfulStart.args?.[0]?.provider_name, 'mock', 'narrative pattern start must pass provider')
  assert.equal(successfulStart.args?.[0]?.model_id, 'gpt', 'narrative pattern start must pass model id')

  const progressStages = await page.evaluate(() =>
    window.__appMockState.emittedEvents
      .filter((event) => event.name === 'narrative_pattern_extraction:progress')
      .map((event) => event.payload.stage))
  assert.deepEqual(
    progressStages.slice(0, 6),
    ['load_chapters', 'boundary_detection', 'chapter_summary', 'chapter_summary', 'phase_compression', 'skill_generation'],
    'narrative pattern progress events must keep pipeline ordering')

  await page.getByRole('button', { name: '清空' }).click()
  await page.getByLabel('选择章节 1 雨夜线索').check()
  await page.getByLabel('技能名称').fill('章节不足技能')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByText('可用章节不足，无法抽取叙事模式。').first(), 'narrative pattern insufficient chapters error')
  await page.getByRole('button', { name: /复制诊断|已复制|复制失败/ }).first().click()

  await page.getByRole('button', { name: '全部' }).click()
  await page.evaluate(() => { window.__appMockState.nextNarrativePatternMode = 'invalid_model' })
  await page.getByLabel('技能名称').fill('坏输出技能')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByText('模型返回的边界 JSON 无法解析。').first(), 'narrative pattern invalid model output error')

  await page.evaluate(() => { window.__appMockState.nextNarrativePatternDelayMs = 900 })
  await page.getByLabel('技能名称').fill('取消叙事技能')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByRole('button', { name: '取消抽取' }), 'narrative pattern cancel button')
  await page.getByRole('button', { name: '取消抽取' }).click()
  await expectVisible(page.getByText('叙事模式抽取已取消。').first(), 'narrative pattern cancelled state')

  const chapterSaves = await page.evaluate(() =>
    window.__appMockState.calls
      .filter((call) => call.method === 'SaveContent')
      .map((call) => String(call.args?.[0]?.path ?? ''))
      .filter((path) => path.startsWith('chapters/')))
  assert.deepEqual(chapterSaves, [], 'narrative pattern workflow must not mutate chapter content')
}

async function verifyGitHistoryWorkflow(page, browser, url, consoleErrors, pageErrors) {
  await clickActivity(page, 'Git 历史')
  await expectVisible(page.getByRole('heading', { name: 'Git 历史' }), 'Git history heading')
  await expectVisible(page.getByText('4 个提交'), 'Git history total count')
  await expectVisible(page.getByText('rename rain clue chapter').first(), 'Git history first commit')
  await expectVisible(page.getByText('delete obsolete note').first(), 'Git history second commit')
  await expectVisible(page.getByText('add outline seed').first(), 'Git history third commit')

  await page.getByRole('button', { name: /rename rain clue chapter/ }).click()
  await expectVisible(page.getByText('chapters/renamed-rain.md').first(), 'renamed file entry')
  await expectVisible(page.getByText('chapters/rain.md -> chapters/renamed-rain.md').first(), 'renamed file old path marker')
  await expectVisible(page.getByText('covers/rain.bin').first(), 'binary file entry')

  await page.getByRole('button', { name: /chapters\/renamed-rain\.md/ }).click()
  await expectVisible(page.getByRole('heading', { name: 'chapters/renamed-rain.md' }), 'renamed diff heading')
  await expectVisible(page.getByText('重命名').first(), 'renamed diff badge')
  await expectVisible(page.getByText('chapters/rain.md -> chapters/renamed-rain.md').first(), 'renamed diff path')
  await expectVisible(page.getByText('old rain clue').first(), 'renamed original content')
  await expectVisible(page.getByText('new rain clue').first(), 'renamed modified content')

  await page.getByRole('button', { name: /notes\/rhythm\.md/ }).click()
  await expectVisible(page.getByRole('heading', { name: 'notes/rhythm.md' }), 'modified diff heading')
  await expectVisible(page.getByText('内容已截断'), 'truncated diff state')

  await page.getByRole('button', { name: /covers\/rain\.bin/ }).click()
  await expectVisible(page.getByRole('heading', { name: 'covers/rain.bin' }), 'binary diff heading')
  await expectVisible(page.getByText('二进制文件不展示文本 diff'), 'binary diff state')

  await page.getByRole('button', { name: /delete obsolete note/ }).click()
  await expectVisible(page.getByText('notes/deleted.md').first(), 'deleted file entry')
  await page.getByRole('button', { name: /notes\/deleted\.md/ }).click()
  await expectVisible(page.getByRole('heading', { name: 'notes/deleted.md' }), 'deleted diff heading')
  await expectVisible(page.getByText('旧笔记将被删除。').first(), 'deleted original content')
  await expectVisible(page.getByText('无修改后内容'), 'deleted modified content empty state')

  await page.getByRole('button', { name: /add outline seed/ }).click()
  await expectVisible(page.getByText('chapters/new-outline.md').first(), 'added file entry')
  await page.getByRole('button', { name: /chapters\/new-outline\.md/ }).click()
  await expectVisible(page.getByRole('heading', { name: 'chapters/new-outline.md' }), 'added diff heading')
  await expectVisible(page.getByText('新增').first(), 'added diff badge')
  await expectVisible(page.getByText('无原始内容'), 'added original content empty state')
  await expectVisible(page.getByText('新的章节纲要。').first(), 'added modified content')

  const olderCommit = page.getByText('initial import').first()
  if (!(await olderCommit.isVisible().catch(() => false))) {
    const loadOlder = page.getByRole('button', { name: '加载更早提交' })
    if (await loadOlder.isVisible().catch(() => false)) {
      await loadOlder.click()
    }
  }
  await expectVisible(olderCommit, 'older Git commit after cursor paging')
  await expectVisible(page.getByText('已到最早提交'), 'Git history end marker')

  const gitCalls = await page.evaluate(() =>
    window.__appMockState.calls
      .filter((call) => call.method === 'GetGitCommits')
      .map((call) => call.args?.[0] ?? null))
  assert(gitCalls.length >= 2, 'Git history workflow must request at least two commit pages')
  assert(
    gitCalls.some((input) => input?.cursor_commit_id === 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'),
    'Git history paging must send the previous page cursor_commit_id')

  await page.getByRole('button', { name: /复制诊断|已复制|复制失败/ }).click()

  await verifyGitHistoryEmptyRepo(browser, url, consoleErrors, pageErrors)
  await verifyGitHistoryFailureRecovery(browser, url, consoleErrors, pageErrors)
  await verifyGitHistoryCompactViewport(browser, url, consoleErrors, pageErrors)
}

async function verifyGitHistoryEmptyRepo(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    {
      initialized: true,
      gitCommits: [],
      gitCommitFilesByCommitId: {},
      gitDiffsByCommitAndPath: {},
    },
    { width: 1100, height: 780 },
    'git-empty-repo',
  )
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await clickActivity(page, 'Git 历史')
  await expectVisible(page.getByText('0 个提交'), 'empty Git history count')
  await expectVisible(page.getByText('暂无 Git 提交'), 'empty Git history state')
  await assertGitHistoryReadOnlyCalls(page)
  await page.close()
}

async function verifyGitHistoryFailureRecovery(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    {
      initialized: true,
      faults: {
        GetGitCommits: {
          mode: 'error',
          code: 'VERSION_CONTROL_ERROR',
          message: 'Git executable not found',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
      },
    },
    { width: 1100, height: 780 },
    'git-failure-retry',
  )
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await clickActivity(page, 'Git 历史')
  await expectVisible(page.getByText('Git executable not found'), 'Git history failure message')
  const gitAlert = errorAlert(page, 'Git executable not found')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, gitAlert, 'GetGitCommits')
  await page.getByRole('button', { name: '重试' }).first().click()
  await expectVisible(page.getByText('rename rain clue chapter').first(), 'Git history retry recovery')
  await assertGitHistoryReadOnlyCalls(page)
  await page.close()
}

async function verifyGitHistoryCompactViewport(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    { initialized: true },
    { width: 900, height: 720 },
    'git-compact',
  )
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await clickActivity(page, 'Git 历史')
  await expectVisible(page.getByRole('heading', { name: 'Git 历史' }), 'compact Git history heading')
  await expectVisible(page.getByText('rename rain clue chapter').first(), 'compact Git history first commit')
  await page.getByRole('button', { name: /rename rain clue chapter/ }).click()
  await expectVisible(page.getByText('chapters/renamed-rain.md').first(), 'compact Git changed file list')
  await assertGitHistoryReadOnlyCalls(page)
  await page.close()
}

async function verifyRelativeTimeRefreshWorkflow(browser, url, consoleErrors, pageErrors) {
  const sessionId = 'relative-session-1'
  const commitId = '1111111111111111111111111111111111111111'
  const page = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    {
      initialized: true,
      sessions: [
        {
          session_id: sessionId,
          novel_id: 42,
          title: '短时刷新会话',
          updated_at: '2026-07-05T12:09:00.000Z',
        },
      ],
      gitCommits: [
        {
          commit_id: commitId,
          short_commit_id: '1111111',
          author_name: 'Mock Author',
          author_email: 'mock@example.com',
          message: 'relative time commit',
          committed_at: '2026-07-05T12:09:00.000Z',
          changed_file_count: 1,
          insertions: 1,
          deletions: 0,
        },
      ],
      gitCommitFilesByCommitId: {
        [commitId]: [
          {
            path: 'chapters/time.md',
            old_path: null,
            change_type: 'modified',
            additions: 1,
            deletions: 0,
            binary: false,
          },
        ],
      },
      gitDiffsByCommitAndPath: {
        [`${commitId}:chapters/time.md`]: {
          path: 'chapters/time.md',
          old_path: null,
          change_type: 'modified',
          binary: false,
          truncated: false,
          original_content: '旧时间标签',
          modified_content: '新时间标签',
          diff_text: '-旧时间标签\n+新时间标签',
        },
      },
    },
    { width: 1280, height: 900 },
    'relative-time',
  )
  await page.clock.install({ time: new Date('2026-07-05T12:10:00.000Z') })
  await page.goto(url, { waitUntil: 'domcontentloaded' })

  const recentSession = page.getByRole('button', { name: /短时刷新会话/ }).first()
  await expectVisible(recentSession, 'recent session fixture')
  await expectVisible(recentSession.getByText('1分钟前'), 'recent session initial relative time')

  await page.locator('aside').getByRole('button', { name: /历史/ }).click()
  await expectVisible(page.getByText('历史会话'), 'session history panel')
  await expectVisible(page.getByText('短时刷新会话').last(), 'session history fixture')
  await expectVisible(page.getByText('1分钟前').last(), 'session history initial relative time')

  await clickActivity(page, 'Git 历史')
  const gitCommit = page.getByRole('button', { name: /relative time commit/ })
  await expectVisible(gitCommit, 'relative-time Git commit')
  await expectVisible(gitCommit.getByText('1分钟前'), 'Git initial relative time')

  await page.clock.fastForward(125_000)

  await expectVisible(recentSession.getByText('3分钟前'), 'recent session refreshed relative time')
  await expectVisible(gitCommit.getByText('3分钟前'), 'Git refreshed relative time')

  await assertGitHistoryReadOnlyCalls(page)
  await page.close()
}

async function verifyBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
    'GetContent',
    'SearchAll',
    'Chat',
    'GetModels',
    'GetSessions',
    'ListSlashCommands',
    'GetLLMConfig',
    'GetEmbeddingConfig',
    'GetSqliteVecStatus',
    'GetCharacters',
    'GetLocations',
    'GetStoryArcs',
    'GetTimelineEntries',
    'GetReaderPerspectives',
    'GetPreferences',
    'GetWritingActivity',
    'GetWritingStats',
    'ListSkills',
    'GetReferenceAnchors',
    'SearchStyleSamples',
    'GetStyleSample',
    'CreateStyleSample',
    'UpdateStyleSample',
    'DeleteStyleSample',
    'ExtractStyleSkillFromSamples',
    'CancelStyleSkillExtraction',
    'BuildReferenceStyleProfile',
    'StartNarrativePatternExtraction',
    'CancelNarrativePatternExtraction',
    'GetNarrativePatternTrace',
    'GetGitCommits',
    'GetGitCommitFiles',
    'GetGitFileDiff',
    'SaveContent',
    'CancelChat',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected bridge method ${method} to be called.`)
  }

  const chapterSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('chapters/'))
  assert.deepEqual(chapterSaves, [], 'app-wide smoke must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'app-wide smoke must not open external URLs')
  assert(!methods.includes('PickReferenceSourceFile'), 'app-wide smoke must not open arbitrary file pickers')

  const saveCandidates = calls.filter((call) =>
    (call.method.startsWith('Save') || call.method.startsWith('Update') || call.method.startsWith('Delete')) &&
    !isAllowedSurfaceMutation(call))
  assert.deepEqual(
    saveCandidates.map((call) => `${call.method}:${JSON.stringify(call.args)}`),
    [],
    `Unexpected mutating bridge calls:\n${saveCandidates.map((call) => call.method).join('\n')}`)
  await assertGitHistoryReadOnlyCalls(page)
}

function isAllowedSurfaceMutation(call) {
  if (call.method === 'UpdateStyleSample' || call.method === 'DeleteStyleSample') {
    return true
  }

  if (call.method === 'SaveContent') {
    const path = String(call.args?.[0]?.path ?? '')
    return path.startsWith('skills/') || path.startsWith('~/.novelist/skills/')
  }

  return false
}

async function verifyStartupBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)

  assert(methods.includes('IsInitialized'), 'startup workflow must check initialization state')
  assert(methods.includes('GetAppConfig'), 'startup workflow must load startup recovery status')
  assert(methods.includes('GetSettings'), 'startup workflow must load settings after successful initialization')
  assert(!methods.includes('SaveContent'), 'startup workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'startup workflow must not open external URLs')
}

async function verifyDiagnosticsBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)

  assert(methods.includes('IsInitialized'), 'diagnostics workflow must load the app before probing fixtures')
  assert(!methods.includes('SaveContent'), 'diagnostics workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'diagnostics workflow must not open external URLs')
}

async function verifyWritingBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetContent']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected writing bridge method ${method} to be called.`)
  }

  assert(!methods.includes('runtime.shell.openExternal'), 'writing workflow must not open external URLs')
}

async function verifyReferenceBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetReferenceAnchors']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected reference bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'reference entry workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'reference entry workflow must not open external URLs')
}

async function verifyPatternBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
    'GetModels',
    'StartNarrativePatternExtraction',
    'GetNarrativePatternTrace',
    'CancelNarrativePatternExtraction',
    'SaveContent',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected pattern bridge method ${method} to be called.`)
  }

  const chapterSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('chapters/'))
  assert.deepEqual(chapterSaves, [], 'pattern workflow must not save chapter content')

  const skillSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('skills/'))
  assert(skillSaves.length >= 1, 'pattern workflow must save generated skills through the skill catalog path')
  assert(!methods.includes('runtime.shell.openExternal'), 'pattern workflow must not open external URLs')
  assert(!methods.includes('ApproveReferenceChapterBlueprint'), 'pattern workflow must not approve reference blueprints')
  assert(!methods.includes('BindReferenceBlueprintMaterials'), 'pattern workflow must not bind reference materials')
}

async function verifyRelativeTimeBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetSessions']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected relative-time workflow bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'relative-time workflow must not save chapter content')
  assert(!methods.includes('runtime.shell.openExternal'), 'relative-time workflow must not open external URLs')
  assert(!methods.includes('PickNovelImportFile'), 'relative-time workflow must not open file pickers')
}

async function verifyLayoutBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetLayoutSettings',
    'SaveLayoutSettings',
    'GetWindowSettings',
    'SaveWindowSettings',
    'runtime.window.toggleMaximize',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected layout workflow bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SetChatPanelWidth'), 'layout workflow must use SaveLayoutSettings instead of the retired chat-width setter')
  assert(!methods.includes('SaveContent'), 'layout workflow must not save chapter content')
  assert(!methods.includes('runtime.shell.openExternal'), 'layout workflow must not open external URLs')
  assert(!methods.includes('PickNovelImportFile'), 'layout workflow must not open file pickers')
}

async function verifyErrorBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
    'CreateNovel',
    'UpdateNovel',
    'DeleteNovel',
    'GetCharacters',
    'DeleteCharacter',
    'GetLocations',
    'DeleteLocation',
    'ListSkills',
    'DeleteSkill',
    'UpdateChapterTitle',
    'StartNovelImport',
    'GetModels',
    'StartNarrativePatternExtraction',
    'SearchStyleSamples',
    'GetStyleSample',
    'CreateStyleSample',
    'UpdateStyleSample',
    'DeleteStyleSample',
    'ExtractStyleSkillFromSamples',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected error workflow bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'error workflow must not save chapter content')
  assert(!methods.includes('runtime.shell.openExternal'), 'error workflow must not open external URLs')
  assert(!methods.includes('PickNovelImportFile'), 'error workflow must not open file pickers')
}

async function verifyGitBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetGitCommits', 'GetGitCommitFiles', 'GetGitFileDiff']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected Git history bridge method ${method} to be called.`)
  }

  const pagedCall = calls.find((call) =>
    call.method === 'GetGitCommits' &&
    call.args?.[0]?.cursor_commit_id === 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
  assert(pagedCall, 'Git history bridge calls must include cursor-based paging')
  await assertGitHistoryReadOnlyCalls(page)
}

async function verifyUpdateBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetUpdateCheckSettings',
    'CheckForUpdates',
    'SaveUpdateCheckSettings',
    'runtime.shell.openExternal',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected update workflow bridge method ${method} to be called.`)
  }

  const opened = calls.filter((call) => call.method === 'runtime.shell.openExternal')
  assert.equal(opened.length, 1, 'update workflow must open exactly one external URL after explicit user action')
  assert.equal(opened[0].payload?.url, 'https://updates.example.test/releases/v2.0.0')
  assert(!methods.includes('SaveContent'), 'update workflow must not save chapter content')
  assert(!methods.includes('PickNovelImportFile'), 'update workflow must not open file pickers')
  assert(!methods.includes('GetGitCommits'), 'update workflow must not load Git history')
  assert(!methods.includes('GetGitCommitFiles'), 'update workflow must not load Git changed files')
  assert(!methods.includes('GetGitFileDiff'), 'update workflow must not load Git diffs')
}

async function assertGitHistoryReadOnlyCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const gitMethods = calls
    .map((call) => call.method)
    .filter((method) => /^Git|^GetGit|^SaveGit|^SetGit|^DeleteGit|^CreateGit|^UpdateGit|^RevertGit|^ResetGit|^CheckoutGit|^RestoreGit|^CommitGit/.test(method))
  const unexpected = gitMethods.filter((method) =>
    !['GetGitCommits', 'GetGitCommitFiles', 'GetGitFileDiff', 'GetGitAuthorSettings', 'SaveGitAuthorSettings'].includes(method))
  assert.deepEqual(unexpected, [], `Git history UI must call only read-only Git methods, got ${unexpected.join(', ')}`)

  const chapterSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('chapters/'))
  assert.deepEqual(chapterSaves, [], 'Git history workflow must not save chapter content')
}

async function verifySmokeBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetContent']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected smoke bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'smoke workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'smoke workflow must not open external URLs')
}

async function verifyStressGuardrails(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  assert(methods.includes('GetContent'), 'stress workflow must load the large chapter through the bridge')
  assert(methods.includes('GetReferenceAnchors'), 'stress workflow must load reference anchors')
  assert(methods.includes('RebuildReferenceAnchor'), 'stress workflow must exercise reference import/segmentation status')
  assert(methods.includes('SearchReferenceMaterials'), 'stress workflow must search generated reference materials')
  assert(methods.includes('GenerateReferenceChapterBlueprint'), 'stress workflow must generate a reference blueprint')
  assert(methods.includes('BindReferenceBlueprintMaterials'), 'stress workflow must bind generated materials into the blueprint')
  assert(!methods.includes('SaveContent'), 'stress workflow must not save large chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'stress workflow must not open external URLs')

  const rebuildCall = calls.find((call) => call.method === 'RebuildReferenceAnchor')
  assert(rebuildCall?.result?.source_segment_count > 0, 'stress rebuild must report source segments')
  assert(rebuildCall?.result?.material_count > 0, 'stress rebuild must report generated materials')

  const defaultLibrarySearch = calls.find((call) =>
    call.method === 'SearchReferenceMaterials' &&
    Array.isArray(call.args[0]?.anchor_ids) &&
    call.args[0].anchor_ids.length === 0 &&
    call.args[0].page === 1)
  assert(defaultLibrarySearch, 'stress material library search must not require manually selected anchors')
  assert(defaultLibrarySearch.result?.total >= 1_200, 'stress material library search must expose a large paged material set')

  const blueprintCall = calls.find((call) => call.method === 'GenerateReferenceChapterBlueprint')
  assert(blueprintCall, 'stress workflow must generate a blueprint')
  assert.deepEqual(blueprintCall.args[0].anchor_ids, [], 'stress blueprint generation must work without manual per-novel corpus binding')

  const bindCall = calls.find((call) => call.method === 'BindReferenceBlueprintMaterials')
  assert(bindCall, 'stress workflow must bind blueprint materials')
  assert(bindCall.result?.links?.some((link) => String(link.material_id).startsWith('stress-mat-')), 'stress binding must use generated stress materials')
  assertBridgeCallOrder(calls, 'ReviewReferenceChapterBlueprint', 'ApproveReferenceChapterBlueprint')
  assertBridgeCallOrder(calls, 'ApproveReferenceChapterBlueprint', 'BindReferenceBlueprintMaterials')
}

function assertBridgeCallOrder(calls, beforeMethod, afterMethod) {
  const beforeIndex = calls.findIndex((call) => call.method === beforeMethod)
  const afterIndex = calls.findIndex((call) => call.method === afterMethod)
  assert(beforeIndex >= 0, `Missing bridge call ${beforeMethod}`)
  assert(afterIndex >= 0, `Missing bridge call ${afterMethod}`)
  assert(beforeIndex < afterIndex, `${beforeMethod} must happen before ${afterMethod}`)
}

async function verifyStressReferenceMaterialPath(page, referenceStress) {
  await clickActivity(page, '参考锚定')
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'stress reference heading')
  await expectVisible(page.getByText(referenceStress.anchor.title), 'stress reference anchor title')
  await expectVisible(page.getByText(referenceStress.anchor.status), 'stress reference anchor ready state')

  await page.getByTitle('重建').first().click()
  await expectVisible(page.getByText('锚点已重建'), 'stress anchor rebuild message')

  const rebuildStatus = await page.evaluate(async ({ novelId, anchorId }) =>
    await window.novelist.invoke('GetReferenceAnchorBuildStatus', { args: [novelId, anchorId] }),
  { novelId: referenceStress.anchor.novel_id, anchorId: referenceStress.anchor.anchor_id })
  assert.equal(rebuildStatus.source_segment_count, referenceStress.buildStatus.source_segment_count, 'stress build status must preserve source segment count')
  assert.equal(rebuildStatus.material_count, referenceStress.buildStatus.material_count, 'stress build status must preserve material count')

  const anchorTitlePattern = escapeRegExp(referenceStress.anchor.title)
  await page.getByRole('button', { name: new RegExp(`浏览 ${anchorTitlePattern} 的材料`) }).click()
  await expectVisible(page.getByText(`第 1 / ${Math.ceil(referenceStress.materialTotal / 5)} 页 · 共 ${referenceStress.materialTotal} 条`), 'stress anchor material pagination summary')
  await expectVisible(page.getByText('stress-mat-0001'), 'stress anchor material first id')
  await expectVisible(page.getByText('stress-seg-0001'), 'stress anchor material provenance segment')
  await page.getByRole('button', { name: new RegExp(`浏览 ${anchorTitlePattern} 的下一页材料`) }).click()
  await expectVisible(page.getByText('stress-mat-0006'), 'stress anchor material next page id')

  const libraryPanel = page.getByTestId('reference-material-library')
  const searchStartedAt = Date.now()
  await libraryPanel.getByLabel('材料库搜索').fill('10MB 水痕')
  await libraryPanel.getByRole('button', { name: /^检索材料库$/ }).click()
  await expectVisible(libraryPanel.getByText(`第 1 / ${Math.ceil(referenceStress.materialTotal / 10)} 页 · ${referenceStress.materialTotal} 条材料`), 'stress material library pagination summary')
  const materialSearchElapsedMs = Date.now() - searchStartedAt
  assert(materialSearchElapsedMs < 10_000, `stress material library search took ${materialSearchElapsedMs}ms`)
  await expectVisible(libraryPanel.getByText('stress-mat-0001'), 'stress material library first id')
  await expectVisible(libraryPanel.getByText('stress-seg-0001'), 'stress material library provenance segment')
  await libraryPanel.getByRole('button', { name: /^下一页$/ }).click()
  await expectVisible(libraryPanel.getByText(`第 2 / ${Math.ceil(referenceStress.materialTotal / 10)} 页 · ${referenceStress.materialTotal} 条材料`), 'stress material library next page summary')
  await expectVisible(libraryPanel.getByText('stress-mat-0011'), 'stress material library next page id')

  await page.getByRole('button', { name: /打开高级模式/ }).click()
  await expectVisible(page.getByRole('button', { name: /生成蓝图/ }), 'stress manual blueprint generation visible')
  const blueprintPanel = page.getByTestId('reference-blueprint-panel')
  await blueprintPanel.getByLabel('章节号').fill(String(referenceStress.chapterNumber))
  await blueprintPanel.getByLabel('标题').fill('10MB 材料绑定验收')
  await blueprintPanel.getByLabel('章节目标').fill('验证大体量参考源自动分段后可检索、分页浏览并绑定蓝图。')
  await blueprintPanel.getByLabel('已知事实').fill('只使用10MB参考源可审计材料\n保持受限视角')
  await blueprintPanel.getByLabel('禁止事实').fill('未经来源支持的门外身份')
  await blueprintPanel.getByRole('button', { name: /生成蓝图/ }).click()
  await expectVisible(page.getByText('章节蓝图已生成'), 'stress blueprint generated message')

  const bindingStartedAt = Date.now()
  const detail = page.getByTestId('reference-blueprint-detail')
  await detail.getByRole('button', { name: /评审/ }).click()
  await expectVisible(page.getByText('蓝图评审已完成'), 'stress blueprint reviewed message')
  await detail.getByRole('button', { name: /批准/ }).click()
  await expectVisible(page.getByText('蓝图已批准'), 'stress blueprint approved message')
  await detail.getByRole('button', { name: /绑定/ }).click()
  await expectVisible(page.getByText('材料已绑定到蓝图'), 'stress materials bound message')
  const blueprintBindingElapsedMs = Date.now() - bindingStartedAt
  assert(blueprintBindingElapsedMs < 10_000, `stress blueprint binding took ${blueprintBindingElapsedMs}ms`)
  await expectVisible(detail.getByText('stress-mat-0001'), 'stress bound generated material id')

  return {
    materialSearchElapsedMs,
    blueprintBindingElapsedMs,
  }
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

function makeStressChapters(count, stressTitle) {
  return Array.from({ length: count }, (_, index) => {
    const chapterNumber = index + 1
    return {
      id: chapterNumber,
      novel_id: 42,
      chapter_number: chapterNumber,
      title: chapterNumber === count ? stressTitle : `压力章 ${String(chapterNumber).padStart(3, '0')}`,
      summary: chapterNumber === count ? '10MB 中文正文压力章节。' : '压力测试占位章节。',
      word_count: chapterNumber === count ? 3_500_000 : 1200,
      file_path: `chapters/${chapterNumber}.md`,
      created_at: '2026-07-05T12:00:00.000Z',
      updated_at: '2026-07-05T12:00:00.000Z',
    }
  })
}

function makeLargeChineseFixture(targetBytes) {
  const chunks = []
  let bytes = 0
  let paragraph = 1

  while (bytes < targetBytes) {
    const line = `第${paragraph}段，雨声沿着旧城门往下落，林岚把杯子推远，仍然只记录自己能看见的水痕、灯影和门缝里的停顿。她不替门外的人下结论，也不把未经确认的身份写进正文。\n`
    chunks.push(line)
    bytes += Buffer.byteLength(line, 'utf8')
    paragraph += 1
  }

  return chunks.join('')
}

function makeStressReferenceFixture(sourceText) {
  const sourceBytes = Buffer.byteLength(sourceText, 'utf8')
  const sourceSegmentCount = Math.max(1_200, Math.ceil(sourceBytes / 4096))
  const materialTotal = Math.max(1_200, sourceSegmentCount)
  const anchorId = 1001
  const chapterNumber = runConfig.stressChapterCount
  const sourcePath = 'D:\\books\\stress-reference-10mb.md'

  const anchor = {
    anchor_id: anchorId,
    novel_id: 42,
    title: '10MB 自动分段参考源',
    author: 'Phase 13 Stress Fixture',
    source_path: sourcePath,
    source_kind: 'markdown',
    license_status: 'user_provided',
    visibility: 'workspace',
    source_trust: 'user_verified',
    owner_scope: 'workspace_corpus',
    owner_novel_id: null,
    user_tags: ['10MB', '自动分段', '压力测试'],
    source_file_hash: `hash-stress-${sourceBytes}`,
    build_version: 'mock-reference-stress-v1',
    status: 'ready',
    created_at: '2026-07-05T12:00:00.000Z',
    updated_at: '2026-07-05T12:00:00.000Z',
  }

  return {
    anchor,
    buildStatus: {
      novel_id: 42,
      anchor_id: anchorId,
      status: 'ready',
      stage: 'completed',
      source_segment_count: sourceSegmentCount,
      material_count: materialTotal,
      slot_count: Math.ceil(materialTotal / 5),
      vector_count: 0,
      last_error: '',
      updated_at: '2026-07-05T12:00:00.000Z',
    },
    sourceBytes,
    sourceCharacters: sourceText.length,
    materialTotal,
    chapterNumber,
    sourcePath,
  }
}

function realisticWritingText() {
  const paragraphs = [
    '## 雨夜复盘',
    '林岚在雨夜旧宅门前停住。门缝里没有灯，檐下却挂着半截湿线，像有人刚把伞收起，又故意把水滴留在青砖上。',
    '她没有立刻推门，只把手套往上拉了拉，低声记下三件事：第一，杯沿朝外；第二，桌面水痕还没散；第三，门后的人知道她会来。',
    '> 这不是供词，只是一段给自己看的现场笔记。',
    '- 风从旧城门方向吹来，带着铁锈味。',
    '- 鞋印停在门槛前，没有跨进去。',
    '- “如果我说没看见，你会信吗？”周砚问。',
    '林岚没有回答。她把那句话在心里放慢了一遍，确认其中的停顿比内容更像线索。雨声压住远处车灯，整条巷子只剩纸页被潮气卷起的声音。',
    '她写下最后一行：不要替门外的人下结论；先让读者看见水、光、脚印和沉默。',
  ]

  return paragraphs.join('\n\n')
}

function usabilityObservation(input) {
  const scores = {
    discoverability: input.scores?.discoverability ?? 0,
    clickCost: input.scores?.clickCost ?? 0,
    feedbackClarity: input.scores?.feedbackClarity ?? 0,
    errorRecovery: input.scores?.errorRecovery ?? 0,
    keyboardErgonomics: input.scores?.keyboardErgonomics ?? 0,
    informationDensity: input.scores?.informationDensity ?? 0,
    visualReadability: input.scores?.visualReadability ?? 0,
  }
  return {
    surface: input.surface,
    severity: input.severity,
    issueType: input.issueType,
    summary: input.summary,
    screenshot: input.screenshot,
    reproduction: input.reproduction ?? [],
    expected: input.expected,
    actual: input.actual,
    impact: input.impact,
    proposedFix: input.proposedFix,
    scores,
  }
}

async function writeUsabilityReport(observations) {
  const scoreRows = observations
    .map((item) => [
      item.surface,
      item.scores.discoverability,
      item.scores.clickCost,
      item.scores.feedbackClarity,
      item.scores.errorRecovery,
      item.scores.keyboardErgonomics,
      item.scores.informationDensity,
      item.scores.visualReadability,
    ].map(markdownCell).join(' | '))
    .map((row) => `| ${row} |`)
    .join('\n')

  const issueRows = observations
    .map((item) => `| ${markdownCell(item.surface)} | ${markdownCell(item.severity)} | ${markdownCell(item.issueType)} | ${markdownCell(item.summary)} | ${markdownCell(artifactPath(item.screenshot))} |`)
    .join('\n')

  const details = observations
    .map((item) => `### ${item.surface}

- Severity: ${item.severity}
- Type: ${item.issueType}
- Screenshot: \`${artifactPath(item.screenshot)}\`
- Reproduction:
${numberedList(item.reproduction)}
- Expected behavior: ${item.expected}
- Actual behavior: ${item.actual}
- Likely user impact: ${item.impact}
- Proposed fix or tracking: ${item.proposedFix}`)
    .join('\n\n')

  const report = `# Phase 13 Usability Report

Generated by \`npm --prefix frontend run test:app:usability\`.

## Scope

This report is generated from deterministic mocked bridge fixtures. It records workflow scoring, severity, affected surface, screenshots, reproduction steps, expected behavior, actual behavior, likely user impact, and proposed fixes. Correctness bugs and ergonomic friction are separated by the Type field.

Artifacts: \`${path.relative(repoRoot, outputDir)}\`

Scoring uses 1-5 where 5 is best. Scores are heuristic product-QA ratings from the scripted browser walkthrough, not performance metrics.

## Workflow Scores

| Surface | Discoverability | Click Cost | Feedback Clarity | Error Recovery | Keyboard Ergonomics | Information Density | Visual Readability |
| --- | --- | --- | --- | --- | --- | --- | --- |
${scoreRows}

## Issues And Observations

| Surface | Severity | Type | Summary | Screenshot |
| --- | --- | --- | --- | --- |
${issueRows}

## Details

${details}

## Reference/Corpus Automation Verdict

The current usability pass confirms the reference surface is reachable, but it deliberately keeps a medium ergonomic-friction item open until Phase 13 proves the normal flow can turn supplied source text into segmented source records, extracted materials, blueprint bindings, and audit feedback without manual corpus plumbing.
`

  await fs.mkdir(phaseOutputRoot, { recursive: true })
  await fs.writeFile(path.join(phaseOutputRoot, 'usability-report.md'), report, 'utf8')
  await fs.writeFile(path.join(outputDir, 'usability-report.md'), report, 'utf8')
}

function artifactPath(fileName) {
  return path.join(path.relative(repoRoot, outputDir), fileName)
}

function markdownCell(value) {
  return String(value ?? '').replaceAll('|', '\\|').replace(/\r?\n/g, '<br>')
}

function numberedList(items) {
  if (!items.length) return '1. No reproduction steps recorded.'
  return items.map((item, index) => `${index + 1}. ${item}`).join('\n')
}

async function replaceEditorText(page, content) {
  const editor = page.locator('.monaco-editor').first()
  await expectVisible(editor, 'content editor')
  await page.waitForFunction(() => typeof window.__novelistEditor?.setValue === 'function', null, { timeout: 12_000 })
  await page.evaluate((content) => window.__novelistEditor.setValue(content), content)
}

async function insertEditorText(page, content) {
  const editor = page.locator('.monaco-editor').first()
  await expectVisible(editor, 'content editor')
  await page.waitForFunction(() => typeof window.__novelistEditor?.insertText === 'function', null, { timeout: 12_000 })
  await page.evaluate((content) => window.__novelistEditor.insertText(content), content)
}

async function assertEditorContains(page, expectedText) {
  await page.waitForFunction(
    (expectedText) => window.__novelistEditor?.getValue?.().includes(expectedText),
    expectedText,
    { timeout: 12_000 },
  ).catch((error) => {
    throw new Error(`Expected editor to contain: ${expectedText}`, { cause: error })
  })
}

async function assertEditorNotContains(page, unexpectedText) {
  await page.waitForFunction(
    (unexpectedText) => !window.__novelistEditor?.getValue?.().includes(unexpectedText),
    unexpectedText,
    { timeout: 12_000 },
  ).catch((error) => {
    throw new Error(`Expected editor not to contain: ${unexpectedText}`, { cause: error })
  })
}

function shortcutKey(key) {
  return `${process.platform === 'darwin' ? 'Meta' : 'Control'}+${key}`
}

async function waitForSaveContent(page, path, expectedText) {
  await page.waitForFunction(
    ({ path, expectedText }) => {
      return window.__appMockState.calls.some((call) =>
        call.method === 'SaveContent' &&
        call.args[0]?.path === path &&
        String(call.args[0]?.content ?? '').includes(expectedText))
    },
    { path, expectedText },
    { timeout: 12_000 },
  )
}

async function waitForSaveContentAfter(page, path, expectedText, previousCount) {
  await page.waitForFunction(
    ({ path, expectedText, previousCount }) => {
      const saveCalls = window.__appMockState.calls.filter((call) => call.method === 'SaveContent')
      return saveCalls.length > previousCount &&
        window.__appMockState.contentByPath[path] === saveCalls.at(-1)?.args[0]?.content &&
        String(window.__appMockState.contentByPath[path] ?? '').includes(expectedText)
    },
    { path, expectedText, previousCount },
    { timeout: 12_000 },
  )
}

async function assertStoredContent(page, path, expectedContent) {
  const actual = await page.evaluate((path) => window.__appMockState.contentByPath[path], path)
  assert.equal(actual, expectedContent)
}

async function assertBridgeCallCount(page, method, expectedCount) {
  const actual = await bridgeCallCount(page, method)
  assert.equal(actual, expectedCount, `Expected ${expectedCount} ${method} calls, got ${actual}.`)
}

async function assertNoBridgeCallArgValue(page, method, unexpectedValue, message) {
  const found = await page.evaluate(
    ({ method, unexpectedValue }) => window.__appMockState.calls.some((call) =>
      call.method === method &&
      (call.args ?? []).some((arg) => JSON.stringify(arg) === JSON.stringify(unexpectedValue))),
    { method, unexpectedValue },
  )
  assert.equal(found, false, message)
}

async function bridgeCallCount(page, method) {
  return await page.evaluate(
    (method) => window.__appMockState.calls.filter((call) => call.method === method).length,
    method,
  )
}

function settingsDialog(page) {
  return page.locator('.fixed').filter({ hasText: '设置' }).last()
}

async function assertSavedLLMConfig(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.savedLLMConfig?.providers?.[0] ?? null)
  assert(actual, 'Expected LLM config to be saved.')
  assert.equal(actual.key, expected.providerKey)
  assert.equal(actual.api_key, expected.apiKey)
  assert.equal(actual.endpoint_type, expected.endpointType)
}

async function assertSavedEmbeddingConfig(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.savedEmbeddingConfig ?? null)
  assert(actual, 'Expected embedding config to be saved.')
  for (const [key, expectedValue] of Object.entries(expected)) {
    assert.deepEqual(actual[key], expectedValue, `Expected saved embedding ${key} to equal ${JSON.stringify(expectedValue)}.`)
  }
}

async function assertNoSavedEmbeddingConfig(page) {
  const actual = await page.evaluate(() => window.__appMockState.savedEmbeddingConfig)
  assert.equal(actual, null, 'Expected embedding config not to be saved.')
}

async function assertLastBridgeCallInput(page, method, expected) {
  const actual = await page.evaluate((method) => {
    const call = window.__appMockState.calls.filter((item) => item.method === method).at(-1)
    return call?.args?.[0] ?? null
  }, method)
  assert(actual, `Expected ${method} to be called.`)
  for (const [key, expectedValue] of Object.entries(expected)) {
    assert.deepEqual(actual[key], expectedValue, `Expected ${method}.${key} to equal ${JSON.stringify(expectedValue)}.`)
  }
}

async function assertButtonDisabled(locator, description) {
  await locator.waitFor({ state: 'visible', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected visible button before disabled check: ${description}`, { cause: error })
  })
  const disabled = await locator.isDisabled()
  assert.equal(disabled, true, `Expected disabled button: ${description}.`)
}

async function assertSettingsCallsUseMockCredentials(page) {
  const leakedLiveCredentialOrEndpoint = await page.evaluate(() => {
    const liveCredentialPatterns = [
      /sk-[A-Za-z0-9_-]{12,}/,
      /sk-proj-[A-Za-z0-9_-]{12,}/,
      /AIza[0-9A-Za-z_-]{12,}/,
      /xox[baprs]-[0-9A-Za-z-]{12,}/,
      /api\.openai\.com/i,
      /api\.anthropic\.com/i,
      /generativelanguage\.googleapis\.com/i,
      /dashscope\.aliyuncs\.com/i,
      /api\.siliconflow\.cn/i,
    ]
    return window.__appMockState.calls.some((call) =>
      liveCredentialPatterns.some((pattern) => pattern.test(JSON.stringify(call.args ?? []))))
  })
  assert.equal(leakedLiveCredentialOrEndpoint, false, 'Settings workflow must use mock credentials and non-live endpoints only.')
}

async function assertSearchResultContainsRestrictedSourcePath(page) {
  const hasRestrictedPath = await page.evaluate(() =>
    window.__appMockState.calls
      .filter((call) => call.method === 'SearchAll')
      .some((call) => call.result?.some?.((item) => item.source_path === 'D:\\restricted\\reference-source.md')))
  assert.equal(hasRestrictedPath, true, 'Expected mocked search payload to include a restricted source path for leakage guardrail coverage.')
}

async function expectVisible(locator, description) {
  await locator.waitFor({ state: 'visible', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected visible: ${description}`, { cause: error })
  })
}

async function expectHidden(locator, description) {
  await locator.waitFor({ state: 'hidden', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected hidden: ${description}`, { cause: error })
  })
}

async function assertDisabled(locator, description) {
  await locator.waitFor({ state: 'attached', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected attached before disabled check: ${description}`, { cause: error })
  })
  const disabled = await locator.isDisabled()
  assert.equal(disabled, true, `Expected disabled: ${description}`)
}

async function waitForBridgeCallArg(page, method, argIndex, expectedValue) {
  await page.waitForFunction(
    ({ method, argIndex, expectedValue }) => {
      return window.__appMockState.calls.some((call) =>
        call.method === method && JSON.stringify(call.args[argIndex]) === JSON.stringify(expectedValue))
    },
    { method, argIndex, expectedValue },
    { timeout: 12_000 },
  )
}

async function waitForBridgeCall(page, method) {
  await page.waitForFunction(
    (method) => window.__appMockState.calls.some((call) => call.method === method),
    method,
    { timeout: 12_000 },
  )
}

async function waitForBridgeCallCountAfter(page, method, previousCount) {
  await page.waitForFunction(
    ({ method, previousCount }) =>
      window.__appMockState.calls.filter((call) => call.method === method).length > previousCount,
    { method, previousCount },
    { timeout: 12_000 },
  )
}

async function assertActiveNovelId(page, expectedNovelId) {
  const actual = await page.evaluate(() => window.__appMockState.activeNovelId)
  assert.equal(actual, expectedNovelId)
}

async function assertNovelDeleted(page, novelId) {
  const exists = await page.evaluate((novelId) =>
    window.__appMockState.novels.some((novel) => novel.id === novelId),
  novelId)
  assert.equal(exists, false, `Expected novel ${novelId} to be deleted.`)
}

async function assertSelectedChapterPath(page, expectedPath) {
  const expectedTitle = expectedPath.endsWith('7.md')
    ? '新章验收-改名'
    : expectedPath.endsWith('2.md')
      ? '旧城门'
      : '雨夜线索'

  await page.waitForFunction(
    ({ expectedTitle }) => {
      return Array.from(document.querySelectorAll('aside button')).some((button) =>
        button.classList.contains('bg-primary/10') &&
        (button.textContent ?? '').includes(expectedTitle))
    },
    { expectedTitle },
    { timeout: 12_000 },
  ).catch((error) => {
    throw new Error(`Expected selected chapter for ${expectedPath}.`, { cause: error })
  })

  const activeClasses = await page.locator('aside').getByRole('button', { name: /第\d+章/ }).evaluateAll((buttons) =>
    buttons
      .map((button) => ({ text: button.textContent ?? '', className: button.getAttribute('class') ?? '' }))
      .filter((button) => button.className.includes('bg-primary/10')),
  )
  assert(activeClasses.some((button) => button.text.includes(expectedTitle)), `Expected selected chapter for ${expectedPath}.`)
}

async function assertActiveTabTitle(page, expectedTitle) {
  const activeTabs = await page.locator('main').locator('div').evaluateAll((nodes) =>
    nodes
      .map((node) => ({ text: node.textContent ?? '', className: node.getAttribute('class') ?? '' }))
      .filter((node) => node.className.includes('border-t-blue-500')),
  )
  assert(activeTabs.some((tab) => tab.text.includes(expectedTitle)), `Expected active tab ${expectedTitle}.`)
}

async function assertChapterTitle(page, novelId, chapterNumber, expectedTitle) {
  const actual = await page.evaluate(({ novelId, chapterNumber }) => {
    return window.__appMockState.chaptersByNovelId[String(novelId)]
      ?.find((chapter) => chapter.chapter_number === chapterNumber)
      ?.title ?? ''
  }, { novelId, chapterNumber })
  assert.equal(actual, expectedTitle)
}

async function assertLastBinaryCall(page, method, expectedByteCount) {
  const actual = await page.evaluate((method) => {
    const call = window.__appMockState.calls.filter((item) => item.method === method).at(-1)
    const payload = call?.args.at(-1)
    return Array.isArray(payload) ? payload.length : 0
  }, method)
  assert.equal(actual, expectedByteCount, `Expected ${method} to receive ${expectedByteCount} bytes, got ${actual}.`)
}

async function assertExportedNovels(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.exportedNovels)
  assert.deepEqual(actual, expected)
}

async function assertSavedCover(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.savedCovers.at(-1))
  assert.deepEqual(actual, expected)
}

async function assertSavedAvatar(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.savedAvatars.at(-1))
  assert.deepEqual(actual, expected)
}

async function expectInputValue(locator, expectedValue) {
  const actual = await locator.inputValue()
  assert.equal(actual, expectedValue)
}

async function expectSelectedValue(locator, expectedValue) {
  const actual = await locator.inputValue()
  assert.equal(actual, expectedValue)
}

async function clickCardAction(root, cardText, actionTitle) {
  const card = root.locator('.group').filter({ hasText: cardText }).first()
  await expectVisible(card, `${cardText} card`)
  await card.getByTitle(actionTitle).click({ force: true })
}

async function assertCreatedReferenceAnchor(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.createdReferenceAnchors.at(-1))
  assert.equal(actual?.title, expected.title)
  assert.equal(actual?.source_path, expected.sourcePath)
  assert.equal(actual?.source_kind, expected.sourceKind)
}

async function assertOnlyTemporaryFixturePaths(page, allowedFixtureRoot) {
  const unexpectedAbsolutePaths = await page.evaluate((allowedFixtureRoot) => {
    const normalize = (value) => String(value).replaceAll('\\', '/')
    const allowedRoot = normalize(allowedFixtureRoot).replace(/\/+$/, '')
    const isAbsolutePath = (value) => /^[A-Za-z]:[\\/]/.test(value) || value.startsWith('/')
    const strings = []

    const collectStrings = (value) => {
      if (typeof value === 'string') {
        strings.push(value)
        return
      }
      if (Array.isArray(value)) {
        for (const item of value) collectStrings(item)
        return
      }
      if (value && typeof value === 'object') {
        for (const item of Object.values(value)) collectStrings(item)
      }
    }

    for (const call of window.__appMockState.calls) {
      collectStrings(call.args)
    }

    return strings.filter((value) => {
      if (!isAbsolutePath(value)) return false
      const normalized = normalize(value)
      return normalized !== allowedRoot && !normalized.startsWith(`${allowedRoot}/`)
    })
  }, allowedFixtureRoot)

  assert.deepEqual(
    unexpectedAbsolutePaths,
    [],
    `Expected absolute file path bridge arguments to stay under ${allowedFixtureRoot}.`,
  )
}

async function dispatchNovelImportDrop(page, payload) {
  await page.evaluate((payload) => {
    const target = document.querySelector('[data-testid="novel-import-dropzone"]')
    if (!target) throw new Error('Novel import dropzone was not found.')

    const event = new Event('drop', { bubbles: true, cancelable: true })
    if (payload.kind === 'files') {
      const dataTransfer = new DataTransfer()
      for (const dropped of payload.files ?? []) {
        const file = new File(['mock import fixture'], dropped.name, { type: dropped.type ?? '' })
        Object.defineProperty(file, 'path', {
          configurable: true,
          enumerable: false,
          value: dropped.path,
        })
        dataTransfer.items.add(file)
      }
      Object.defineProperty(event, 'dataTransfer', { value: dataTransfer })
    } else if (payload.kind === 'url') {
      const data = {
        'text/plain': payload.url,
        'text/uri-list': payload.url,
      }
      Object.defineProperty(event, 'dataTransfer', {
        value: {
          files: [],
          items: [],
          getData(type) {
            return data[type] ?? ''
          },
        },
      })
    } else if (payload.kind === 'fileUriText') {
      const data = {
        'text/plain': payload.uri,
        'text/uri-list': '',
      }
      Object.defineProperty(event, 'dataTransfer', {
        value: {
          files: [],
          items: [],
          getData(type) {
            return data[type] ?? ''
          },
        },
      })
    } else if (payload.kind === 'directory') {
      Object.defineProperty(event, 'dataTransfer', {
        value: {
          files: [],
          items: [
            {
              kind: 'file',
              webkitGetAsEntry() {
                return { isDirectory: true }
              },
            },
          ],
          getData() {
            return ''
          },
        },
      })
    } else {
      Object.defineProperty(event, 'dataTransfer', {
        value: {
          files: [],
          items: [],
          getData() {
            return ''
          },
        },
      })
    }

    target.dispatchEvent(event)
  }, payload)
}

function startServer(port, target) {
  if (target === 'dist') return startVitePreview(port)
  return startVite(port)
}

function startVite(port) {
  const viteBin = path.join(frontendRoot, 'node_modules', 'vite', 'bin', 'vite.js')

  return spawn(process.execPath, [viteBin, '--host', '127.0.0.1', '--port', String(port)], {
    cwd: frontendRoot,
    env: {
      ...process.env,
      BROWSER: 'none',
      NODE_ENV: 'development',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })
}

function startVitePreview(port) {
  const viteBin = path.join(frontendRoot, 'node_modules', 'vite', 'bin', 'vite.js')

  return spawn(process.execPath, [viteBin, 'preview', '--host', '127.0.0.1', '--port', String(port), '--strictPort'], {
    cwd: frontendRoot,
    env: {
      ...process.env,
      BROWSER: 'none',
      NODE_ENV: 'production',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })
}

async function launchBrowser() {
  try {
    return await chromium.launch({ headless: true })
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    if (process.platform === 'win32' && message.includes("Executable doesn't exist")) {
      logStep('Playwright Chromium is not installed; falling back to Microsoft Edge')
      return chromium.launch({ channel: 'msedge', headless: true })
    }

    throw new Error(
      `${message}\nRun "npx playwright install chromium" from frontend/ if this machine has no browser fallback.`,
      { cause: error },
    )
  }
}

async function waitForServer(url, child) {
  const logs = []
  child.stdout.on('data', (chunk) => logs.push(String(chunk)))
  child.stderr.on('data', (chunk) => logs.push(String(chunk)))

  const startedAt = Date.now()
  while (Date.now() - startedAt < 30_000) {
    if (child.exitCode !== null) {
      throw new Error(`Vite exited before becoming ready:\n${logs.join('')}`)
    }

    try {
      const response = await fetch(url)
      if (response.ok) return
    } catch {
      // keep polling
    }
    await delay(200)
  }

  throw new Error(`Timed out waiting for Vite:\n${logs.join('')}`)
}

function stopProcess(child) {
  if (child.exitCode === null && child.signalCode === null) {
    child.kill()
  }
}

async function getFreePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer()
    server.unref()
    server.on('error', reject)
    server.listen(0, '127.0.0.1', () => {
      const address = server.address()
      server.close(() => {
        if (typeof address === 'object' && address?.port) {
          resolve(address.port)
        } else {
          reject(new Error('Unable to reserve a local port.'))
        }
      })
    })
  })
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function logStep(message) {
  console.log(`[app mock:${runConfig.suite}:${runConfig.target}] ${message}`)
}

function parseRunConfig(args) {
  const config = {
    suite: 'full',
    target: 'vite',
    grep: '',
    stressSizeBytes: 10 * 1024 * 1024,
    stressChapterCount: 250,
  }

  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index]
    if (arg.startsWith('--suite=')) {
      config.suite = arg.slice('--suite='.length)
    } else if (arg.startsWith('--target=')) {
      config.target = arg.slice('--target='.length)
    } else if (arg.startsWith('--grep=')) {
      config.grep = arg.slice('--grep='.length)
    } else if (arg === '--grep') {
      config.grep = args[index + 1] ?? ''
      index += 1
    } else if (arg.startsWith('--stress-size-mb=')) {
      config.stressSizeBytes = Math.max(1, Number.parseInt(arg.slice('--stress-size-mb='.length), 10)) * 1024 * 1024
    } else if (arg.startsWith('--stress-chapters=')) {
      config.stressChapterCount = Math.max(1, Number.parseInt(arg.slice('--stress-chapters='.length), 10))
    }
  }

  if (!['smoke', 'full', 'stress', 'usability'].includes(config.suite)) {
    throw new Error(`Unsupported app mock suite: ${config.suite}`)
  }
  if (!['vite', 'dist'].includes(config.target)) {
    throw new Error(`Unsupported app mock target: ${config.target}`)
  }
  config.grep = normalizeGrepTag(config.grep)
  if (config.grep && !['@startup', '@diagnostics', '@surface', '@writing', '@reference-anchor', '@pattern', '@git', '@update', '@time', '@layout', '@error'].includes(config.grep)) {
    throw new Error(`Unsupported app mock grep: ${config.grep}`)
  }
  if (!Number.isFinite(config.stressSizeBytes) || config.stressSizeBytes <= 0) {
    throw new Error('Stress size must be a positive number of megabytes.')
  }
  if (!Number.isFinite(config.stressChapterCount) || config.stressChapterCount <= 0) {
    throw new Error('Stress chapter count must be a positive number.')
  }

  return config
}

function makeTagFilter(grep) {
  return (tag) => !grep || grep === tag
}

function normalizeGrepTag(value) {
  const tag = String(value ?? '').trim()
  if (!tag) return ''
  return tag.startsWith('@') ? tag : `@${tag}`
}

function artifactRunName(config) {
  const grepSuffix = config.grep ? `-${sanitizeArtifactName(config.grep.replace(/^@/, ''))}` : ''
  return `${config.suite}-${config.target}${grepSuffix}`
}

function mockImportRecoveryResult() {
  const now = '2026-07-05T12:00:00.000Z'
  return {
    reconciled_runs: [
      {
        task_id: 'startup-cleaned-import',
        state: 'cleanup_completed',
        stage: 'cleanup_completed',
        source_display_name: '半截导入.txt',
        source_path_hash: 'sha256:startup-cleaned',
        parser_type: 'txt',
        created_novel_id: 77,
        created_file_roots: ['novels/77'],
        skipped_chapters: [],
        diagnostics: [],
        warnings: [],
        error: {
          code: 'import.recovered_cleanup',
          message: '启动恢复已清理未完成的导入。',
          detail: 'Created rows and files were removed.',
          operation: 'ReconcileNovelImportRuns',
          task_id: 'startup-cleaned-import',
          run_id: null,
          bridge_method: 'ReconcileNovelImportRuns',
          timestamp: now,
        },
        started_at: now,
        updated_at: now,
        completed_at: now,
      },
    ],
    blocked_runs: [
      {
        task_id: 'startup-blocked-import',
        state: 'cleanup_blocked',
        stage: 'cleanup_blocked',
        source_display_name: '越界导入.txt',
        source_path_hash: 'sha256:startup-blocked',
        parser_type: 'txt',
        created_novel_id: 88,
        created_file_roots: ['novels/88'],
        skipped_chapters: [],
        diagnostics: [],
        warnings: [],
        error: {
          code: 'import.cleanup_blocked',
          message: '导入恢复清理被阻止。',
          detail: 'Created file root resolves outside the allowed cleanup boundary.',
          operation: 'ReconcileNovelImportRuns',
          task_id: 'startup-blocked-import',
          run_id: null,
          bridge_method: 'ReconcileNovelImportRuns',
          timestamp: now,
        },
        started_at: now,
        updated_at: now,
        completed_at: now,
      },
    ],
    diagnostics: [
      {
        code: 'import.cleanup_blocked',
        message: '导入恢复清理被阻止。',
        detail: 'startup-blocked-import requires manual review.',
        severity: 'warning',
      },
    ],
    reconciled_at: now,
  }
}

async function writeRunDiagnostics() {
  await fs.writeFile(
    path.join(outputDir, 'diagnostics.json'),
    `${JSON.stringify({
      suite: runConfig.suite,
      target: runConfig.target,
      grep: runConfig.grep,
      artifactDirectory: path.relative(repoRoot, outputDir),
      consoleErrors: diagnostics.consoleErrors,
      consoleWarnings: diagnostics.consoleWarnings,
      pageErrors: diagnostics.pageErrors,
      failedRequests: diagnostics.failedRequests,
    }, null, 2)}\n`,
    'utf8',
  )
}

async function closeOpenPages() {
  const pages = [...openPages]
  for (const page of pages) {
    await page.close().catch((error) => {
      diagnostics.pageErrors.push(`Failed to close diagnostic page: ${error instanceof Error ? error.message : String(error)}`)
    })
  }
}

function sanitizeArtifactName(value) {
  return String(value)
    .replace(/[^a-z0-9._-]+/gi, '-')
    .replace(/^-+|-+$/g, '')
    || 'page'
}

main().catch((error) => {
  console.error(error)
  process.exitCode = 1
})
