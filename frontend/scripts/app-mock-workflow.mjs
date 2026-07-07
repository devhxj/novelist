import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import fs from 'node:fs/promises'
import net from 'node:net'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { chromium } from 'playwright'

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
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true }, undefined, 'full-shell')
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
  await expectVisible(page.getByText('章节 (2)'), 'usability chapter panel')
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
  await expectVisible(compactPage.getByText('章节 (2)'), 'usability compact chapter side panel')
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
  await expectVisible(page.getByText('章节 (2)'), 'chapter count')
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
  await expectVisible(page.getByText('章节 (2)'), 'chapter sidebar restored after profile navigation')
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
  await expectVisible(page.getByText('章节 (2)'), 'original novel chapter count restored')
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
  await expectVisible(page.getByText('章节 (2)'), 'updated novel chapter count')
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
  await expectVisible(page.getByText('章节 (3)'), 'chapter count after create')
  await ensureChapterBlockExpanded(page)
  await expectVisible(chapterButton(page, '新章验收'), 'created chapter visible')

  await page.getByRole('button', { name: '编辑章节 新章验收' }).click({ force: true })
  await page.locator('aside input[value="新章验收"]').fill('新章验收-改名')
  await page.keyboard.press('Enter')
  await waitForBridgeCall(page, 'UpdateChapterTitle')
  await expectVisible(chapterButton(page, '新章验收-改名'), 'renamed chapter visible')

  await chapterButton(page, '新章验收-改名').click()
  await expectVisible(page.getByText('第3章 新章验收-改名').first(), 'renamed chapter tab')
  await waitForBridgeCallArg(page, 'GetContent', 1, 'chapters/3.md')
  await assertSelectedChapterPath(page, 'chapters/3.md')
  await assertChapterTitle(page, 42, 3, '新章验收-改名')

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
  await fs.writeFile(
    pickedReferenceSourceFile,
    '# Phase 13 import/export fixture\n\n雨夜参考源只用于文件选择 mock，不读取真实用户项目。\n',
    'utf8',
  )
  await fs.writeFile(pickedNovelImportFile, '第一章\n通过文件选择导入。', 'utf8')
  await fs.writeFile(droppedNovelImportFile, '# 第一章\n\n通过拖放导入。', 'utf8')
  await fs.writeFile(droppedNovelImportUriFile, '# 第一章\n\n通过 file URI 拖放导入。', 'utf8')

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
  const getContentBeforeImport = await bridgeCallCount(page, 'GetContent')
  await page.getByRole('button', { name: '导入小说' }).click()
  await waitForBridgeCallCountAfter(page, 'PickNovelImportFile', pickImportBefore)
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', startImportBefore)
  await expectVisible(page.getByText('已导入：picker-import'), 'file-picker novel import success')
  await expectVisible(novelCard(page, 'picker-import'), 'file-picker imported novel card')
  await assertLastBridgeCallInput(page, 'StartNovelImport', {
    source_path: pickedNovelImportFile,
    source_display_name: 'picker-import.txt',
    import_kind: 'txt',
  })
  assert.equal(
    await bridgeCallCount(page, 'GetContent'),
    getContentBeforeImport,
    'Novel import picker must not route source paths through generic content reads.',
  )

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

  const getContentBeforeUriDrop = await bridgeCallCount(page, 'GetContent')
  await dispatchNovelImportDrop(page, {
    kind: 'fileUriText',
    uri: pathToFileURL(droppedNovelImportUriFile).href,
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', dropImportBefore)
  await expectVisible(page.getByText('已导入：drop-import-uri'), 'file URI novel import drop success')
  await expectVisible(novelCard(page, 'drop-import-uri'), 'file URI imported novel card')
  await assertLastBridgeCallInput(page, 'StartNovelImport', {
    source_path: droppedNovelImportUriFile,
    source_display_name: 'drop-import-uri.markdown',
    import_kind: 'markdown',
  })
  assert.equal(
    await bridgeCallCount(page, 'GetContent'),
    getContentBeforeUriDrop,
    'Dropped file URI novel import paths must not route through generic content reads.',
  )

  const getContentBeforeDrop = await bridgeCallCount(page, 'GetContent')
  const fileDropImportBefore = await bridgeCallCount(page, 'StartNovelImport')
  await importDropzone.hover()
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'drop-import.md', path: droppedNovelImportFile, type: 'text/markdown' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', fileDropImportBefore)
  await expectVisible(page.getByText('已导入：drop-import'), 'drag-drop novel import success')
  await expectVisible(novelCard(page, 'drop-import'), 'drag-drop imported novel card')
  await assertLastBridgeCallInput(page, 'StartNovelImport', {
    source_path: droppedNovelImportFile,
    source_display_name: 'drop-import.md',
    import_kind: 'markdown',
  })
  assert.equal(
    await bridgeCallCount(page, 'GetContent'),
    getContentBeforeDrop,
    'Dropped novel import paths must not route through generic content reads.',
  )

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

  const chapterBlock = page.getByRole('button', { name: /第 1 - 2 章/ })
  if (await chapterBlock.isVisible()) {
    await chapterBlock.click()
  }
  await expectVisible(firstChapter, 'expanded first chapter')
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

  await page.getByRole('button', { name: /模型配置/ }).click()
  const dialog = settingsDialog(page)
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
  const dialog = settingsDialog(page)
  await expectVisible(dialog.getByText('基础设置'), 'settings persistence dialog')
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
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    faults: {
      TestConnection: { mode: 'validation', message: '模拟模型连通失败' },
      SaveEmbeddingConfig: { mode: 'storage', message: '模拟 Embedding 保存失败' },
    },
  }, undefined, 'settings-failures')
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'settings failure workspace')

  await page.locator('header').getByTitle('设置').click()
  const dialog = settingsDialog(page)
  await expectVisible(dialog.getByText('基础设置'), 'settings failure dialog')
  await dialog.getByRole('button', { name: /模型配置/ }).click()

  await dialog.getByPlaceholder('输入 API Key').first().fill('mock-settings-key')
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await waitForBridgeCall(page, 'TestConnection')
  await expectVisible(dialog.getByText(/Mock Provider 连通性测试失败:.*模拟模型连通失败/), 'LLM connection failure message')
  await assertBridgeCallCount(page, 'SaveLLMConfig', 0)

  await dialog.getByRole('button', { name: 'Embeddings' }).click()
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await waitForBridgeCall(page, 'SaveEmbeddingConfig')
  await expectVisible(dialog.getByText(/保存失败:/), 'embedding save failure message')
  await expectVisible(dialog.getByText(/模拟 Embedding 保存失败/), 'embedding save failure detail')
  await assertNoSavedEmbeddingConfig(page)

  await assertBridgeCallCount(page, 'DiscoverModels', 0)
  await assertBridgeCallCount(page, 'PickReferenceSourceFile', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertSettingsCallsUseMockCredentials(page)
  await page.close()
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

  await clickActivity(page, '章节')
  await expectVisible(page.getByText('章节 (2)'), 'compact chapter sidebar restored')
  await ensureChapterBlockExpanded(page)
  await assertSelectedChapterPath(page, 'chapters/1.md')
  await assertActiveTabTitle(page, '第1章 雨夜线索')
  await expectVisible(page.locator('.monaco-editor').first(), 'compact editor restored after activity transitions')
  await expectVisible(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'compact chat restored after activity transitions')

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await page.screenshot({ path: path.join(outputDir, 'app-08-compact.png'), fullPage: true })
  await page.close()
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

async function verifyReferenceSmoke(page) {
  await page.getByTitle('参考锚定').click()
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'reference heading')
  await expectVisible(page.getByText('全局雨夜参考').first(), 'reference anchor fixture')
  await expectVisible(page.getByText('默认编排').first(), 'orchestration panel')
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
    'CancelChat',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'app-wide smoke must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'app-wide smoke must not open external URLs')
  assert(!methods.includes('PickReferenceSourceFile'), 'app-wide smoke must not open arbitrary file pickers')

  const saveCandidates = methods.filter(method => method.startsWith('Save') || method.startsWith('Update') || method.startsWith('Delete'))
  assert.deepEqual(saveCandidates, [], `Unexpected mutating bridge calls:\n${saveCandidates.join('\n')}`)
}

async function verifyStartupBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)

  assert(methods.includes('IsInitialized'), 'startup workflow must check initialization state')
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
  const expectedTitle = expectedPath.endsWith('3.md')
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
  if (config.grep && !['@startup', '@diagnostics', '@surface', '@writing', '@reference-anchor'].includes(config.grep)) {
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

function settingsFixture(lastNovelId) {
  return {
    ID: 1,
    last_novel_id: lastNovelId,
    selected_model_key: 'mock/gpt',
    reasoning_effort: 'high',
    approval_mode: 'manual',
    chat_panel_width: 360,
    last_session_id: '',
    user_name: 'Mock User',
    git_author_name: '',
    git_author_email: '',
    update_check_enabled: false,
    update_check_endpoint_url: '',
    update_check_dismissed_version: '',
    update_check_last_checked_at: null,
    sidebar_width: 280,
    metadata_panel_width: 320,
    window_x: null,
    window_y: null,
    window_width: 1280,
    window_height: 840,
    window_maximized: false,
  }
}

function installConfigurableAppMockBridge(options = {}) {
  const now = '2026-07-05T12:00:00.000Z'
  const receivers = new Set()
  const defaultSettings = {
    ID: 1,
    last_novel_id: 42,
    selected_model_key: 'mock/gpt',
    reasoning_effort: 'high',
    approval_mode: 'manual',
    chat_panel_width: 360,
    last_session_id: '',
    user_name: 'Mock User',
    git_author_name: '',
    git_author_email: '',
    update_check_enabled: false,
    update_check_endpoint_url: '',
    update_check_dismissed_version: '',
    update_check_last_checked_at: null,
    sidebar_width: 280,
    metadata_panel_width: 320,
    window_x: null,
    window_y: null,
    window_width: 1280,
    window_height: 840,
    window_maximized: false,
  }
  const defaultNovel = {
    id: 42,
    title: '全局回归小说',
    genre: '悬疑',
    description: 'App-wide Playwright fixture',
    created_at: now,
    updated_at: now,
  }
  const defaultChapters = [
    {
      id: 1,
      novel_id: 42,
      chapter_number: 1,
      title: '雨夜线索',
      summary: '林岚在雨夜发现桌面痕迹。',
      word_count: 1200,
      file_path: 'chapters/1.md',
      created_at: now,
      updated_at: now,
    },
    {
      id: 2,
      novel_id: 42,
      chapter_number: 2,
      title: '旧城门',
      summary: '暗号被雨水冲淡。',
      word_count: 980,
      file_path: 'chapters/2.md',
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultCharacters = [
    {
      id: 1,
      novel_id: 42,
      name: '林岚',
      description: '旧城门案件的调查者。',
      personality: '谨慎、克制',
      abilities: JSON.stringify(['观察', '推理']),
      created_at: now,
      updated_at: now,
    },
    {
      id: 2,
      novel_id: 42,
      name: '周砚',
      description: '掌握旧城暗号的线人。',
      personality: '沉默',
      abilities: JSON.stringify(['记忆']),
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultLocations = [
    {
      id: 1,
      novel_id: 42,
      name: '旧城门',
      location_type: '城市',
      description: '雨夜里暗号被冲淡的城门。',
      detail_json: '{}',
      parent_location_id: 0,
      tags: JSON.stringify(['雨夜', '线索']),
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultStoryArcs = [
    {
      id: 1,
      novel_id: 42,
      name: '雨夜调查线',
      description: '围绕桌面水痕推进。',
      arc_type: 'main',
      importance: 5,
      status: 'active',
      reactivate_at: '',
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultArcNodes = [
    {
      id: 1,
      novel_id: 42,
      story_arc_id: 1,
      title: '桌面水痕触发调查',
      description: '林岚发现水痕但没有立刻揭示判断。',
      target_chapter: 1,
      actual_chapter: 0,
      status: 'pending',
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultChapterPlans = [
    { novel_id: 42, scope: 'next', content: '下一章继续旧城门调查。' },
    { novel_id: 42, scope: 'near', content: '近期回收桌面水痕。' },
    { novel_id: 42, scope: 'far', content: '远期揭示暗号来源。' },
  ]
  const defaultTimelineEntries = [
    {
      id: 1,
      novel_id: 42,
      category: 'foreshadowing',
      status: 'pending',
      title: '桌面水痕',
      content: '杯底留下半圈水痕，提示有人刚离开。',
      detail_json: '{}',
      target_chapter: 1,
      importance: 4,
      source_chapter_id: 1,
      source: 'user',
      resolved_chapter_id: 0,
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultReaderPerspectives = [
    {
      id: 1,
      novel_id: 42,
      type: 'known',
      content: '读者知道林岚正在调查旧城门。',
      related_truth: '旧城门暗号和桌面水痕相关。',
      planted_chapter: 1,
      revealed_chapter: 0,
      created_at: now,
    },
  ]
  const defaultPreferences = {
    global: [
      {
        id: 1,
        novel_id: 0,
        is_global: true,
        category: '叙事',
        content: '保持受限视角，不提前暴露门外身份。',
        created_at: now,
      },
    ],
    novel: [
      {
        id: 2,
        novel_id: 42,
        is_global: false,
        category: '节奏',
        content: '雨夜场景多用动作间隔承压。',
        created_at: now,
      },
    ],
  }
  const defaultWritingActivity = [
    { date: '2026-07-01', words: 800 },
    { date: '2026-07-02', words: 1200 },
  ]
  const defaultWritingStats = {
    total_words: 2180,
    total_days_active: 2,
    current_streak: 2,
    longest_streak: 2,
    total_novels: 1,
    total_chapters: 2,
  }
  const defaultSkills = [
    {
      name: '节奏控制',
      description: '控制动作、停顿和信息释放。',
      category: '写作技法',
      mode: 'manual',
      author: 'mock',
      version: 1,
      source: 'novel',
    },
    {
      name: '对话潜台词',
      description: '用潜台词替代解释。',
      category: '写作技法',
      mode: 'manual',
      author: 'mock',
      version: 1,
      source: 'builtin',
    },
  ]
  const defaultContentByPath = {
    'novelist.md': '## 当前状态\n林岚正在调查旧城门。',
    'chapters/1.md': '林岚在雨夜旧宅门前停住。\n\n她看见桌上的水痕。',
    'chapters/2.md': '旧城门下，暗号被雨水冲淡。',
    'skills/rhythm.md': '---\nname: 节奏控制\n---\n保持停顿和动作之间的张力。',
    'skills/节奏控制.md': '---\nname: 节奏控制\n---\n保持停顿和动作之间的张力。',
    '/builtin/skills/dialogue.md': '---\nname: 对话潜台词\n---\n用话外之意推动场景。',
  }
  const defaultReferenceAnchors = [
    {
      anchor_id: 101,
      novel_id: 42,
      title: '全局雨夜参考',
      author: 'Mock Author',
      source_path: 'D:\\books\\rain-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'user_verified',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['雨夜', '全局语料'],
      source_file_hash: 'hash-anchor-app-001',
      build_version: 'mock-reference-v1',
      status: 'ready',
      created_at: now,
      updated_at: now,
    },
  ]
  const state = {
    calls: [],
    emittedEvents: [],
    appliedFaults: [],
    activeNovelId: options.settings?.last_novel_id ?? defaultSettings.last_novel_id,
    nextNovelId: 43,
    nextChapterId: 3,
    nextCharacterId: 3,
    nextLocationId: 2,
    nextStoryArcId: 2,
    nextArcNodeId: 2,
    nextTimelineEntryId: 2,
    nextReaderPerspectiveId: 2,
    nextPreferenceId: 3,
    nextSessionId: 1,
    nextTurnId: 101,
    searchFailureRecovered: false,
    chatFailureRecovered: false,
    failNextSaveContent: false,
    savedLLMConfig: null,
    savedEmbeddingConfig: null,
    exportedNovels: [],
    savedCovers: [],
    savedAvatars: [],
    novelImportRuns: [],
    createdReferenceAnchors: [],
    referenceAnchors: options.referenceAnchors ?? defaultReferenceAnchors,
    referenceBuildStatuses: options.referenceBuildStatuses ?? {},
    referenceBlueprints: {},
    nextReferenceBlueprintId: 701,
    contentByPath: options.contentByPath ?? defaultContentByPath,
    initialized: options.initialized ?? true,
    novels: options.novels ?? [defaultNovel],
    chaptersByNovelId: options.chaptersByNovelId ?? { 42: defaultChapters },
    settings: options.settings ?? defaultSettings,
    characters: options.characters ?? defaultCharacters,
    locations: options.locations ?? defaultLocations,
    storyArcs: options.storyArcs ?? defaultStoryArcs,
    arcNodes: options.arcNodes ?? defaultArcNodes,
    chapterPlans: options.chapterPlans ?? defaultChapterPlans,
    timelineEntries: options.timelineEntries ?? defaultTimelineEntries,
    readerPerspectives: options.readerPerspectives ?? defaultReaderPerspectives,
    preferences: options.preferences ?? defaultPreferences,
    writingActivity: options.writingActivity ?? defaultWritingActivity,
    writingStats: options.writingStats ?? defaultWritingStats,
    skills: options.skills ?? defaultSkills,
  }
  const faultQueues = normalizeFaultQueues(options.faults ?? {})
  Object.defineProperty(state, 'clearFaultQueue', {
    configurable: true,
    enumerable: false,
    value(method) {
      if (method) {
        faultQueues[method] = []
      }
    },
  })

  window.localStorage.removeItem('novelist_tabs_all')
  window.localStorage.setItem('theme', 'light')
  window.confirm = () => Boolean(options.confirmResult)

  Object.defineProperty(window, '__appMockState', {
    configurable: true,
    value: state,
  })

  Object.defineProperty(window, 'external', {
    configurable: true,
    value: {
      sendMessage(message) {
        const envelope = JSON.parse(String(message))
        if (envelope.kind === 'request') {
          window.setTimeout(() => {
            void handleRequest(envelope)
          }, 0)
        }
      },
      receiveMessage(callback) {
        receivers.add(callback)
      },
    },
  })

  async function handleRequest(envelope) {
    try {
      const args = Array.isArray(envelope.payload?.args) ? envelope.payload.args : []
      state.calls.push({ method: envelope.method, args })
      const fault = nextFault(envelope.method)

      if (fault?.delayMs) {
        await wait(fault.delayMs)
      }

      if (fault?.mode === 'timeout') {
        return
      }

      if (fault?.mode === 'malformed-response') {
        respond({ kind: 'response', id: envelope.id, result: fault.result ?? null })
        return
      }

      if (fault?.mode === 'validation' || fault?.mode === 'storage' || fault?.mode === 'error') {
        respond({
          kind: 'response',
          id: envelope.id,
          ok: false,
          error: faultErrorPayload(fault),
        })
        return
      }

      if (envelope.method === 'SaveContent' && !options.allowSaveContent) {
        throw new Error('SaveContent is forbidden in the app-wide smoke unless the test explicitly edits content.')
      }

      const result = fault?.hasResult ? fault.result : await route(envelope.method, args)
      state.calls[state.calls.length - 1].result = result
      respond({ kind: 'response', id: envelope.id, ok: true, result })
    } catch (error) {
      respond({
        kind: 'response',
        id: envelope.id,
        ok: false,
        error: {
          code: 'MOCK_BRIDGE_ERROR',
          message: error instanceof Error ? error.message : String(error),
          retryable: false,
        },
      })
    }
  }

  function respond(payload) {
    const message = JSON.stringify(payload)
    for (const receiver of receivers) {
      receiver(message)
    }
  }

  function emit(name, payload) {
    state.emittedEvents.push({ name, payload })
    respond({ kind: 'event', name, payload })
  }

  function normalizeFaultQueues(faults) {
    const queues = {}
    for (const [method, fault] of Object.entries(faults)) {
      queues[method] = Array.isArray(fault) ? [...fault] : [fault]
    }
    return queues
  }

  function nextFault(method) {
    const queue = faultQueues[method]
    if (!queue || queue.length === 0) return null

    const fault = normalizeFault(queue[0])
    if (fault.once !== false) {
      queue.shift()
    }

    state.appliedFaults.push({
      method,
      mode: fault.mode,
      delayMs: fault.delayMs,
      code: fault.code,
      message: fault.message,
    })
    return fault
  }

  function normalizeFault(fault) {
    if (!fault || typeof fault !== 'object') {
      return { mode: 'error', message: 'Mock fixture fault' }
    }

    return {
      mode: String(fault.mode ?? 'success'),
      delayMs: Math.max(0, Number(fault.delayMs ?? 0)),
      code: typeof fault.code === 'string' ? fault.code : '',
      message: typeof fault.message === 'string' ? fault.message : '',
      retryable: fault.retryable === true,
      details: fault.details,
      result: fault.result,
      hasResult: Object.hasOwn(fault, 'result'),
      once: fault.once,
    }
  }

  function faultErrorPayload(fault) {
    if (fault.mode === 'validation') {
      return {
        code: fault.code || 'VALIDATION_ERROR',
        message: fault.message || 'Mock validation error',
        details: fault.details,
        retryable: false,
      }
    }

    if (fault.mode === 'storage') {
      return {
        code: fault.code || 'STORAGE_ERROR',
        message: fault.message || 'Mock storage error',
        details: fault.details,
        retryable: fault.retryable,
      }
    }

    return {
      code: fault.code || 'MOCK_BRIDGE_ERROR',
      message: fault.message || 'Mock bridge error',
      details: fault.details,
      retryable: fault.retryable,
    }
  }

  async function route(method, args) {
    switch (method) {
      case 'IsInitialized':
        if (options.failIsInitialized) throw new Error('初始化状态读取失败')
        return state.initialized
      case 'Initialize':
        state.initialized = true
        state.novels = options.afterInitializeNovels ?? state.novels
        state.settings = options.afterInitializeSettings ?? state.settings
        state.activeNovelId = state.settings.last_novel_id
        return null
      case 'GetSettings': return state.settings
      case 'GetGitAuthorSettings': return {
        name: state.settings.git_author_name ?? '',
        email: state.settings.git_author_email ?? '',
        scope: 'app',
      }
      case 'SaveGitAuthorSettings':
        state.settings.git_author_name = String(args[0]?.name ?? '')
        state.settings.git_author_email = String(args[0]?.email ?? '')
        return {
          name: state.settings.git_author_name,
          email: state.settings.git_author_email,
          scope: 'app',
        }
      case 'GetUpdateCheckSettings': return {
        enabled: state.settings.update_check_enabled === true,
        endpoint_url: state.settings.update_check_endpoint_url ?? '',
        dismissed_version: state.settings.update_check_dismissed_version ?? '',
        last_checked_at: state.settings.update_check_last_checked_at ?? null,
      }
      case 'SaveUpdateCheckSettings':
        state.settings.update_check_enabled = args[0]?.enabled === true
        state.settings.update_check_endpoint_url = String(args[0]?.endpoint_url ?? '')
        state.settings.update_check_dismissed_version = String(args[0]?.dismissed_version ?? '')
        return {
          enabled: state.settings.update_check_enabled,
          endpoint_url: state.settings.update_check_endpoint_url,
          dismissed_version: state.settings.update_check_dismissed_version,
          last_checked_at: state.settings.update_check_last_checked_at ?? null,
        }
      case 'GetLayoutSettings': return {
        sidebar_width: state.settings.sidebar_width ?? 280,
        chat_panel_width: state.settings.chat_panel_width ?? 360,
        metadata_panel_width: state.settings.metadata_panel_width ?? 320,
      }
      case 'SaveLayoutSettings':
        state.settings.sidebar_width = Number(args[0]?.sidebar_width ?? state.settings.sidebar_width ?? 280)
        state.settings.chat_panel_width = Number(args[0]?.chat_panel_width ?? state.settings.chat_panel_width ?? 360)
        state.settings.metadata_panel_width = Number(args[0]?.metadata_panel_width ?? state.settings.metadata_panel_width ?? 320)
        return {
          sidebar_width: state.settings.sidebar_width,
          chat_panel_width: state.settings.chat_panel_width,
          metadata_panel_width: state.settings.metadata_panel_width,
        }
      case 'GetWindowSettings': return {
        x: state.settings.window_x ?? null,
        y: state.settings.window_y ?? null,
        width: state.settings.window_width ?? 1280,
        height: state.settings.window_height ?? 840,
        maximized: state.settings.window_maximized === true,
      }
      case 'SaveWindowSettings':
        state.settings.window_x = args[0]?.x ?? null
        state.settings.window_y = args[0]?.y ?? null
        state.settings.window_width = Number(args[0]?.width ?? state.settings.window_width ?? 1280)
        state.settings.window_height = Number(args[0]?.height ?? state.settings.window_height ?? 840)
        state.settings.window_maximized = args[0]?.maximized === true
        return {
          x: state.settings.window_x,
          y: state.settings.window_y,
          width: state.settings.window_width,
          height: state.settings.window_height,
          maximized: state.settings.window_maximized,
        }
      case 'GetPlatform': return { os: 'win32', defaultPath: options.platformDefaultPath ?? 'D:\\NovelistData' }
      case 'runtime.window.isMaximized': return false
      case 'runtime.window.minimize':
      case 'runtime.window.toggleMaximize':
      case 'runtime.app.quit':
      case 'CancelChat':
      case 'ApproveTool':
      case 'RebuildNovelIndex':
      case 'TestConnection':
      case 'TestEmbeddingConnection':
        return null
      case 'SetLastSession':
        state.settings.last_session_id = String(args[0] ?? '')
        return null
      case 'SetSelectedModel':
        state.settings.selected_model_key = String(args[0] ?? '')
        state.settings.reasoning_effort = String(args[1] ?? '')
        return null
      case 'SetReasoningEffort':
        state.settings.reasoning_effort = String(args[0] ?? '')
        return null
      case 'SetApprovalMode':
        state.settings.approval_mode = String(args[0] ?? '')
        return null
      case 'SetChatPanelWidth':
        state.settings.chat_panel_width = Number(args[0] ?? state.settings.chat_panel_width ?? 360)
        return null
      case 'SaveLLMConfig':
        state.savedLLMConfig = args[0]
        return null
      case 'SaveEmbeddingConfig':
        state.savedEmbeddingConfig = args[0]
        return null
      case 'GetAppConfig': return {
        initialized: state.initialized,
        data_dir: options.platformDefaultPath ?? 'D:\\NovelistData',
        update_check: {
          endpoint_url: '',
          default_enabled: false,
          timeout_ms: 5000,
        },
      }
      case 'SetActiveNovel':
        state.activeNovelId = args[0]?.novel_id ?? state.activeNovelId
        state.settings.last_novel_id = state.activeNovelId
        return null
      case 'GetNovels': return state.novels
      case 'CreateNovel': return createNovel(args[0])
      case 'UpdateNovel': return updateNovel(args[0], args[1])
      case 'DeleteNovel':
        deleteNovel(args[0])
        return null
      case 'GetCover': return null
      case 'SaveCover':
        state.savedCovers.push({ novel_id: args[0], byte_count: Array.isArray(args[1]) ? args[1].length : 0 })
        return null
      case 'SaveAvatar':
        state.savedAvatars.push({ byte_count: Array.isArray(args[0]) ? args[0].length : 0 })
        return null
      case 'ExportNovel':
        state.exportedNovels.push({ novel_id: args[0], format: args[1] })
        return null
      case 'PickNovelImportFile': return options.pickedNovelImportFile ?? null
      case 'StartNovelImport': return startNovelImport(args[0])
      case 'GetNovelImportRun': return state.novelImportRuns.find((run) => run.task_id === args[0]?.task_id) ?? null
      case 'GetNovelImportRecoveryStatus': return {
        pending_runs: state.novelImportRuns.filter((run) => !['completed', 'completed_with_warning', 'failed', 'cancelled'].includes(run.state)),
        blocked_runs: state.novelImportRuns.filter((run) => run.state === 'cleanup_blocked'),
        checked_at: now,
      }
      case 'GetChapters': return chapters(args[0])
      case 'CreateChapter': return createChapter(args[0])
      case 'UpdateChapterTitle':
        updateChapterTitle(args[0], args[1], args[2])
        return null
      case 'GetContent': return content(args[1])
      case 'SaveContent': return saveContent(args[0])
      case 'GetModels': return [availableModel()]
      case 'GetSessions': return pageResult([])
      case 'GetSession': return sessionDetail(args[0])
      case 'GetSessionMessages': return []
      case 'ListSlashCommands': return [{ name: 'review', description: '审稿当前章节', type: 'manual' }]
      case 'Chat': return chat(args[0])
      case 'CompressContext': return { turn_id: state.nextTurnId++ }
      case 'SearchAll': return searchAll(args[1])
      case 'GetCharacters': return characters(args[0])
      case 'CreateCharacter': return createCharacter(args[0], args[1])
      case 'UpdateCharacter':
        updateCharacter(args[0], args[1], args[2])
        return null
      case 'DeleteCharacter':
        deleteCharacter(args[0], args[1])
        return null
      case 'GetCharacterRelations': return []
      case 'GetLocations': return locations(args[0])
      case 'CreateLocation': return createLocation(args[0], args[1])
      case 'UpdateLocation':
        updateLocation(args[0], args[1], args[2])
        return null
      case 'DeleteLocation':
        deleteLocation(args[0], args[1])
        return null
      case 'GetLocationRelations': return []
      case 'GetStoryArcs': return storyArcs(args[0])
      case 'CreateStoryArc': return createStoryArc(args[0], args[1])
      case 'UpdateStoryArc':
        updateStoryArc(args[0], args[1], args[2])
        return null
      case 'DeleteStoryArc':
        deleteStoryArc(args[0], args[1])
        return null
      case 'GetArcNodes': return arcNodes(args[0])
      case 'CreateArcNode': return createArcNode(args[0], args[1])
      case 'UpdateArcNode':
        updateArcNode(args[0], args[1], args[2])
        return null
      case 'DeleteArcNode':
        deleteArcNode(args[0], args[1])
        return null
      case 'GetMaxChapterNumber': return 2
      case 'GetChapterPlans': return chapterPlans(args[0])
      case 'UpdateChapterPlan':
        updateChapterPlan(args[0], args[1])
        return null
      case 'GetTimelineEntries': return timelineEntries(args[0])
      case 'CreateTimelineEntry': return createTimelineEntry(args[0], args[1])
      case 'UpdateTimelineEntry':
        updateTimelineEntry(args[0], args[1], args[2])
        return null
      case 'DeleteTimelineEntry':
        deleteTimelineEntry(args[0], args[1])
        return null
      case 'GetReaderPerspectives': return readerPerspectives(args[0])
      case 'CreateReaderPerspective': return createReaderPerspective(args[0], args[1])
      case 'UpdateReaderPerspective':
        updateReaderPerspective(args[1], args[0], args[2])
        return null
      case 'DeleteReaderPerspective':
        deleteReaderPerspective(args[1], args[0])
        return null
      case 'GetPreferences': return preferences(args[0])
      case 'CreatePreference': return createPreference(args[0], args[1])
      case 'UpdatePreference': return updatePreference(args[0], args[1])
      case 'DeletePreference':
        deletePreference(args[0])
        return null
      case 'GetWritingActivity': return writingActivity()
      case 'GetWritingStats': return writingStats()
      case 'ListSkills': return skills()
      case 'DeleteSkill':
        deleteSkill(args[0])
        return null
      case 'ExtractStyle': return extractStyle(args[0])
      case 'SaveUserName':
        state.settings.user_name = String(args[0] ?? '')
        return null
      case 'GetLLMConfig': return llmConfig()
      case 'GetEmbeddingConfig': return embeddingConfig()
      case 'GetSqliteVecStatus': return sqliteVecStatus()
      case 'GetReferenceAnchors': return referenceAnchors()
      case 'GetReferenceAnchorBuildStatus': return referenceBuildStatus(args[1])
      case 'PickReferenceSourceFile': return options.pickedReferenceSourceFile ?? null
      case 'CreateReferenceAnchor': return createReferenceAnchor(args[0])
      case 'RebuildReferenceAnchor': return rebuildReferenceAnchor(args[1])
      case 'SearchReferenceMaterials': return searchReferenceMaterials(args[0])
      case 'GetReferenceChapterBlueprints': return Object.values(state.referenceBlueprints).map(toReferenceBlueprintSummary)
      case 'GetReferenceChapterBlueprint': return state.referenceBlueprints[String(args[1])] ?? null
      case 'GenerateReferenceChapterBlueprint': return generateReferenceBlueprint(args[0])
      case 'ReviewReferenceChapterBlueprint': return reviewReferenceBlueprint(args[0])
      case 'ApproveReferenceChapterBlueprint': return approveReferenceBlueprint(args[0])
      case 'BindReferenceBlueprintMaterials': return bindReferenceBlueprintMaterials(args[0])
      case 'GetReferenceOrchestrationRuns': return []
      case 'GetReferenceOrchestrationRunEvents': return []
      default:
        return defaultValueFor(method)
    }
  }

  function createNovel(input) {
    const novel = {
      id: state.nextNovelId++,
      title: String(input?.title ?? ''),
      genre: String(input?.genre ?? ''),
      description: String(input?.description ?? ''),
      created_at: now,
      updated_at: now,
    }
    state.novels = [...state.novels, novel]
    state.chaptersByNovelId[novel.id] = []
    return novel
  }

  function updateNovel(novelId, input) {
    const existing = state.novels.find((novel) => novel.id === novelId)
    if (!existing) throw new Error(`Novel ${novelId} not found.`)
    const updated = {
      ...existing,
      title: String(input?.title ?? existing.title),
      genre: String(input?.genre ?? existing.genre ?? ''),
      description: String(input?.description ?? existing.description ?? ''),
      updated_at: now,
    }
    state.novels = state.novels.map((novel) => novel.id === novelId ? updated : novel)
    return updated
  }

  function deleteNovel(novelId) {
    state.novels = state.novels.filter((novel) => novel.id !== novelId)
    delete state.chaptersByNovelId[String(novelId)]
    if (state.activeNovelId === novelId) {
      state.activeNovelId = state.novels[0]?.id ?? 0
      state.settings.last_novel_id = state.activeNovelId
    }
  }

  function startNovelImport(input) {
    const sourcePath = String(input?.source_path ?? '')
    const sourceDisplayName = String(input?.source_display_name ?? fileNameFromPath(sourcePath) ?? '导入小说.txt')
    const importKind = String(input?.import_kind ?? importKindFromFileName(sourceDisplayName) ?? 'txt')
    const title = sourceDisplayName
      .replace(/\.(epub|txt|md|markdown)$/i, '')
      .trim() || '导入小说'
    const novel = {
      id: state.nextNovelId++,
      title,
      genre: importKind === 'epub' ? 'EPUB 导入' : '文本导入',
      description: '由小说导入流程创建',
      created_at: now,
      updated_at: now,
    }
    state.novels = [...state.novels, novel]
    state.chaptersByNovelId[novel.id] = []

    const run = {
      task_id: String(input?.task_id ?? `import-${state.novelImportRuns.length + 1}`),
      state: 'completed',
      stage: 'completed',
      source_display_name: sourceDisplayName,
      source_path_hash: `sha256:mock-import-${state.novelImportRuns.length + 1}`,
      parser_type: importKind,
      created_novel_id: novel.id,
      created_file_roots: [`novels/${novel.id}`],
      skipped_chapters: [],
      diagnostics: [],
      warnings: [],
      error: null,
      started_at: now,
      updated_at: now,
      completed_at: now,
    }
    state.novelImportRuns = [...state.novelImportRuns, run]
    return run
  }

  function fileNameFromPath(value) {
    return String(value)
      .split(/[\\/]/)
      .filter(Boolean)
      .at(-1)
  }

  function importKindFromFileName(value) {
    const lower = String(value).toLowerCase()
    if (lower.endsWith('.epub')) return 'epub'
    if (lower.endsWith('.txt')) return 'txt'
    if (lower.endsWith('.md') || lower.endsWith('.markdown')) return 'markdown'
    return ''
  }

  function chapters(novelId = state.activeNovelId) {
    return [...(state.chaptersByNovelId[String(novelId)] ?? [])]
  }

  function createChapter(input) {
    const novelId = input?.novel_id ?? state.activeNovelId
    const list = state.chaptersByNovelId[String(novelId)] ?? []
    const chapterNumber = list.reduce((max, chapter) => Math.max(max, chapter.chapter_number), 0) + 1
    const chapter = {
      id: state.nextChapterId++,
      novel_id: novelId,
      chapter_number: chapterNumber,
      title: String(input?.title ?? ''),
      summary: '',
      word_count: 0,
      file_path: `chapters/${chapterNumber}.md`,
      created_at: now,
      updated_at: now,
    }
    state.chaptersByNovelId[String(novelId)] = [...list, chapter]
    state.contentByPath[chapter.file_path] = ''
    return chapter
  }

  function updateChapterTitle(novelId, chapterNumber, title) {
    const key = String(novelId)
    const list = state.chaptersByNovelId[key] ?? []
    state.chaptersByNovelId[key] = list.map((chapter) =>
      chapter.chapter_number === chapterNumber
        ? { ...chapter, title: String(title ?? chapter.title), updated_at: now }
        : chapter,
    )
  }

  function content(filePath) {
    return state.contentByPath[filePath] ?? ''
  }

  function saveContent(input) {
    if (!options.allowSaveContent) {
      throw new Error('SaveContent is forbidden in the app-wide smoke unless the test explicitly edits content.')
    }
    if (state.failNextSaveContent) {
      state.failNextSaveContent = false
      throw new Error('模拟保存失败，请重试')
    }
    if (!input?.path) {
      throw new Error('SaveContent requires a path.')
    }
    state.contentByPath[input.path] = String(input.content ?? '')
    return null
  }

  function availableModel() {
    return {
      Key: 'mock/gpt',
      ProviderName: 'mock',
      ModelName: 'Mock GPT',
      ContextWindow: 128000,
      MaxOutputTokens: 4096,
      SupportsThinking: true,
      ReasoningLevels: ['high'],
      SupportsVision: false,
    }
  }

  function sessionDetail(sessionId) {
    return {
      session_id: sessionId,
      novel_id: 42,
      title: 'Mock session',
      model: 'mock/gpt',
      reasoning_effort: 'high',
      active_version: 1,
      last_turn_id: 0,
      created_at: now,
      updated_at: now,
    }
  }

  async function chat(input) {
    const sessionId = input?.session_id || `session-app-${state.nextSessionId++}`
    const turnId = state.nextTurnId++
    const message = String(input?.message ?? '')
    emit('chat:started', { turn_id: turnId })

    if (message.includes('停止生成')) {
      await wait(600)
      return {
        session_id: sessionId,
        turn_id: turnId,
        final_text: '',
      }
    }

    if (message.includes('触发失败态') && !state.chatFailureRecovered) {
      state.chatFailureRecovered = true
      await wait(50)
      emit(`agent:${turnId}`, agentEvent(turnId, 1, {
        type: 5,
        error: '模拟模型失败，请重试',
      }))
      await wait(50)
      return {
        session_id: sessionId,
        turn_id: turnId,
        final_text: '',
      }
    }

    if (message.includes('触发失败态')) {
      await wait(80)
      const retryText = '重试后恢复：模型返回稳定结果，未修改章节正文。'
      emit(`agent:${turnId}`, agentEvent(turnId, 1, {
        type: 2,
        data: retryText,
      }))
      await wait(40)
      return {
        session_id: sessionId,
        turn_id: turnId,
        final_text: retryText,
      }
    }

    if (message.includes('长文本 Markdown')) {
      const chunks = longMarkdownChatChunks()
      emit(`agent:${turnId}`, agentEvent(turnId, 1, {
        type: 0,
        data: '先检查章节约束、工具结果和是否需要写入正文。',
      }))
      await wait(80)
      emit(`agent:${turnId}`, agentEvent(turnId, 2, {
        type: 1,
      }))
      emit(`agent:${turnId}`, agentEvent(turnId, 3, {
        type: 3,
        tool_name: 'inspect_story_constraints',
        tool_id: 'tool-story-constraints-001',
        phase: 'completed',
        display_text: '检查章节约束',
        activity_kind: 'review',
        metadata: { chapter_path: 'chapters/1.md', can_mutate: false },
      }))
      for (let index = 0; index < chunks.length; index += 1) {
        emit(`agent:${turnId}`, agentEvent(turnId, index + 4, {
          type: 2,
          data: chunks[index],
        }))
        await wait(index === 0 ? 1800 : 120)
      }
      const finalText = chunks.join('')
      emit(`agent:${turnId}`, agentEvent(turnId, chunks.length + 4, {
        type: 4,
        usage: {
          prompt_tokens: 420,
          completion_tokens: 980,
          total_tokens: 1400,
          prompt_cache_hit_tokens: 320,
          prompt_cache_miss_tokens: 100,
          cache_hit_ratio: 76.2,
          context_window: 128000,
          usage_ratio: 1.1,
          detail: {
            system: 160,
            user: 260,
            assistant: 980,
            tool: 0,
          },
        },
      }))
      return {
        session_id: sessionId,
        turn_id: turnId,
        final_text: finalText,
      }
    }

    await wait(100)
    emit(`agent:${turnId}`, agentEvent(turnId, 1, {
      type: 3,
      tool_name: 'get_chapter_list',
      tool_id: 'tool-chapters-001',
      phase: 'executing',
      display_text: '读取章节列表',
      activity_kind: 'view',
    }))
    emit(`agent:${turnId}`, agentEvent(turnId, 2, {
      type: 3,
      tool_name: 'get_chapter_list',
      tool_id: 'tool-chapters-001',
      phase: 'completed',
      display_text: '读取章节列表',
      activity_kind: 'view',
      metadata: { chapters: 2 },
    }))
    emit(`agent:${turnId}`, agentEvent(turnId, 3, {
      type: 3,
      tool_name: 'web_search',
      tool_id: 'tool-web-001',
      phase: 'completed',
      display_text: '检索雨夜线索资料',
      activity_kind: 'browse',
      metadata: {
        queries: ['雨夜线索'],
        summary: '检索结果只用于对照氛围，不写入章节。',
        sources: [{ title: 'Mock source', url: 'https://example.com/mock-source' }],
      },
    }))
    emit(`agent:${turnId}`, agentEvent(turnId, 4, {
      type: 2,
      data: '已读取《雨夜线索》的章节列表，建议先保留受限视角。',
    }))
    emit(`agent:${turnId}`, agentEvent(turnId, 5, {
      type: 4,
      usage: {
        prompt_tokens: 96,
        completion_tokens: 32,
        total_tokens: 128,
        prompt_cache_hit_tokens: 86,
        prompt_cache_miss_tokens: 10,
        cache_hit_ratio: 89.6,
        context_window: 128000,
        usage_ratio: 0.1,
        detail: {
          system: 40,
          user: 20,
          assistant: 32,
          tool: 36,
        },
      },
    }))

    await wait(50)
    return {
      session_id: sessionId,
      turn_id: turnId,
      final_text: '已读取《雨夜线索》的章节列表，建议先保留受限视角。',
    }
  }

  function longMarkdownChatChunks() {
    const body = Array.from({ length: 12 }, (_, index) =>
      `第${toChineseOrdinal(index + 1)}段：雨声压住脚步声，回复仍保持可读宽度。`).join('\n\n')
    return [
      '### 约束检查\n\n',
      '- 不要直接写入章节正文。\n- 保留受限视角，不提前揭示门外身份。\n\n',
      '```yaml\nscene_guard: no_implicit_chapter_mutation\napproval_required: true\n```\n\n',
      `${body}\n\n最终建议：先读后改，不越过审批。`,
    ]
  }

  function toChineseOrdinal(value) {
    const values = ['一', '二', '三', '四', '五', '六', '七', '八', '九', '十', '十一', '十二']
    return values[value - 1] ?? String(value)
  }

  function agentEvent(turnId, seq, patch) {
    return {
      turn_id: turnId,
      seq,
      timestamp: now,
      ...patch,
    }
  }

  function searchAll(query) {
    if (!query?.trim()) return []
    if (query.includes('没有结果')) return []
    if (query.includes('搜索失败')) {
      if (!state.searchFailureRecovered) {
        state.searchFailureRecovered = true
        throw new Error('Mock search failure')
      }
      if (query.includes('恢复')) return searchResults()
      return []
    }
    return searchResults()
  }

  function searchResults() {
    return [
      {
        type: 'content',
        id: 1,
        title: '雨夜线索',
        subtitle: '第1章',
        chapter_num: 1,
        file_path: 'chapters/1.md',
        match_prefix: '林岚在',
        match_hit: '雨夜',
        match_suffix: '旧宅门前停住。',
        match_position: 3,
        match_len: 2,
        relevance: 1,
        panel_id: '',
      },
      {
        type: 'character',
        id: 1,
        title: '林岚',
        subtitle: '主角',
        chapter_num: 0,
        file_path: '',
        match_prefix: '旧城门调查者',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.8,
        panel_id: 'characters',
      },
      {
        type: 'location',
        id: 1,
        title: '旧城门',
        subtitle: '城市',
        chapter_num: 0,
        file_path: '',
        match_prefix: '雨夜里暗号被冲淡的城门。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.76,
        panel_id: 'locations',
      },
      {
        type: 'timeline',
        id: 1,
        title: '桌面水痕',
        subtitle: '伏笔',
        chapter_num: 1,
        file_path: '',
        match_prefix: '杯底留下半圈水痕，提示有人刚离开。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.74,
        panel_id: 'timeline',
      },
      {
        type: 'storyarc',
        id: 1,
        title: '雨夜调查线',
        subtitle: 'main',
        chapter_num: 0,
        file_path: '',
        match_prefix: '围绕桌面水痕推进。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.7,
        panel_id: 'storyarcs',
      },
      {
        type: 'preference',
        id: 2,
        title: '雨夜场景规则',
        subtitle: '节奏',
        chapter_num: 0,
        file_path: '',
        match_prefix: '雨夜场景多用动作间隔承压。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.72,
        panel_id: 'preferences',
      },
      {
        type: 'story_memory',
        id: 4,
        title: '故事记忆：旧城门约束',
        subtitle: '第1章',
        chapter_num: 1,
        file_path: 'chapters/1.md',
        match_prefix: '故事记忆只返回章节语义摘要，不暴露受限来源路径。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.88,
        panel_id: 'chapters',
        source_path: 'D:\\restricted\\reference-source.md',
      },
      {
        type: 'rag',
        id: 3,
        title: '雨夜语义片段',
        subtitle: '第1章',
        chapter_num: 1,
        file_path: 'chapters/1.md',
        match_prefix: '语义结果只指向章节内容，不暴露参考源路径。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.86,
        panel_id: '',
      },
    ]
  }

  function characters(novelId = state.activeNovelId) {
    return state.characters.filter((item) => item.novel_id === novelId)
  }

  function createCharacter(novelId, input) {
    const character = {
      id: state.nextCharacterId++,
      novel_id: novelId,
      name: String(input?.name ?? ''),
      description: String(input?.description ?? ''),
      personality: String(input?.personality ?? ''),
      abilities: String(input?.abilities ?? '[]'),
      created_at: now,
      updated_at: now,
    }
    state.characters = [...state.characters, character]
    return character
  }

  function updateCharacter(novelId, characterId, input) {
    state.characters = state.characters.map((item) =>
      item.novel_id === novelId && item.id === characterId
        ? {
          ...item,
          name: String(input?.name ?? item.name),
          description: String(input?.description ?? item.description),
          personality: String(input?.personality ?? item.personality),
          abilities: String(input?.abilities ?? item.abilities),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteCharacter(novelId, characterId) {
    state.characters = state.characters.filter((item) => item.novel_id !== novelId || item.id !== characterId)
  }

  function locations(novelId = state.activeNovelId) {
    return state.locations.filter((item) => item.novel_id === novelId)
  }

  function createLocation(novelId, input) {
    const location = {
      id: state.nextLocationId++,
      novel_id: novelId,
      name: String(input?.name ?? ''),
      location_type: String(input?.location_type ?? ''),
      description: String(input?.description ?? ''),
      detail_json: String(input?.detail_json ?? '{}'),
      parent_location_id: Number(input?.parent_location_id ?? 0),
      tags: String(input?.tags ?? '[]'),
      created_at: now,
      updated_at: now,
    }
    state.locations = [...state.locations, location]
    return location
  }

  function updateLocation(novelId, locationId, input) {
    state.locations = state.locations.map((item) =>
      item.novel_id === novelId && item.id === locationId
        ? {
          ...item,
          name: String(input?.name ?? item.name),
          location_type: String(input?.location_type ?? item.location_type),
          description: String(input?.description ?? item.description),
          detail_json: String(input?.detail_json ?? item.detail_json),
          parent_location_id: input?.clear_parent ? 0 : Number(input?.parent_location_id ?? item.parent_location_id ?? 0),
          tags: String(input?.tags ?? item.tags),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteLocation(novelId, locationId) {
    state.locations = state.locations.filter((item) => item.novel_id !== novelId || item.id !== locationId)
    state.locations = state.locations.map((item) =>
      item.parent_location_id === locationId ? { ...item, parent_location_id: 0, updated_at: now } : item,
    )
  }

  function storyArcs(novelId = state.activeNovelId) {
    return state.storyArcs.filter((item) => item.novel_id === novelId)
  }

  function createStoryArc(novelId, input) {
    const arc = {
      id: state.nextStoryArcId++,
      novel_id: novelId,
      name: String(input?.name ?? ''),
      description: String(input?.description ?? ''),
      arc_type: String(input?.arc_type ?? 'main'),
      importance: Number(input?.importance ?? 3),
      status: String(input?.status ?? 'active'),
      reactivate_at: String(input?.reactivate_at ?? ''),
      created_at: now,
      updated_at: now,
    }
    state.storyArcs = [...state.storyArcs, arc]
    return arc
  }

  function updateStoryArc(novelId, arcId, input) {
    state.storyArcs = state.storyArcs.map((item) =>
      item.novel_id === novelId && item.id === arcId
        ? {
          ...item,
          name: String(input?.name ?? item.name),
          description: String(input?.description ?? item.description),
          arc_type: String(input?.arc_type ?? item.arc_type),
          importance: Number(input?.importance ?? item.importance),
          status: String(input?.status ?? item.status),
          reactivate_at: String(input?.reactivate_at ?? item.reactivate_at),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteStoryArc(novelId, arcId) {
    state.storyArcs = state.storyArcs.filter((item) => item.novel_id !== novelId || item.id !== arcId)
    state.arcNodes = state.arcNodes.filter((item) => item.novel_id !== novelId || item.story_arc_id !== arcId)
  }

  function arcNodes(novelId = state.activeNovelId) {
    return state.arcNodes.filter((item) => item.novel_id === novelId)
  }

  function createArcNode(novelId, input) {
    const node = {
      id: state.nextArcNodeId++,
      novel_id: novelId,
      story_arc_id: Number(input?.story_arc_id ?? 0),
      title: String(input?.title ?? ''),
      description: String(input?.description ?? ''),
      target_chapter: Number(input?.target_chapter ?? 1),
      actual_chapter: Number(input?.actual_chapter ?? 0),
      status: String(input?.status ?? 'pending'),
      created_at: now,
      updated_at: now,
    }
    state.arcNodes = [...state.arcNodes, node]
    return node
  }

  function updateArcNode(novelId, nodeId, input) {
    state.arcNodes = state.arcNodes.map((item) =>
      item.novel_id === novelId && item.id === nodeId
        ? {
          ...item,
          story_arc_id: Number(input?.story_arc_id ?? item.story_arc_id),
          title: String(input?.title ?? item.title),
          description: String(input?.description ?? item.description),
          target_chapter: Number(input?.target_chapter ?? item.target_chapter),
          actual_chapter: Number(input?.actual_chapter ?? item.actual_chapter),
          status: String(input?.status ?? item.status),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteArcNode(novelId, nodeId) {
    state.arcNodes = state.arcNodes.filter((item) => item.novel_id !== novelId || item.id !== nodeId)
  }

  function chapterPlans(novelId = state.activeNovelId) {
    return state.chapterPlans.filter((item) => item.novel_id === novelId)
  }

  function updateChapterPlan(novelId, input) {
    const scope = String(input?.scope ?? '')
    const content = String(input?.content ?? '')
    const exists = state.chapterPlans.some((item) => item.novel_id === novelId && item.scope === scope)
    state.chapterPlans = exists
      ? state.chapterPlans.map((item) => item.novel_id === novelId && item.scope === scope ? { ...item, content } : item)
      : [...state.chapterPlans, { novel_id: novelId, scope, content }]
  }

  function timelineEntries(novelId = state.activeNovelId) {
    return state.timelineEntries.filter((item) => item.novel_id === novelId)
  }

  function createTimelineEntry(novelId, input) {
    const entry = {
      id: state.nextTimelineEntryId++,
      novel_id: novelId,
      category: String(input?.category ?? 'foreshadowing'),
      status: String(input?.status ?? 'pending'),
      title: String(input?.title ?? ''),
      content: String(input?.content ?? ''),
      detail_json: String(input?.detail_json ?? ''),
      target_chapter: Number(input?.target_chapter ?? 1),
      importance: Number(input?.importance ?? 3),
      source_chapter_id: Number(input?.source_chapter_id ?? 0),
      source: String(input?.source ?? 'user'),
      resolved_chapter_id: Number(input?.resolved_chapter_id ?? 0),
      created_at: now,
      updated_at: now,
    }
    state.timelineEntries = [...state.timelineEntries, entry]
    return entry
  }

  function updateTimelineEntry(novelId, entryId, input) {
    state.timelineEntries = state.timelineEntries.map((item) =>
      item.novel_id === novelId && item.id === entryId
        ? {
          ...item,
          title: String(input?.title ?? item.title),
          content: String(input?.content ?? item.content),
          detail_json: String(input?.detail_json ?? item.detail_json),
          target_chapter: Number(input?.target_chapter ?? item.target_chapter),
          importance: Number(input?.importance ?? item.importance),
          status: String(input?.status ?? item.status),
          resolved_chapter_id: Number(input?.resolved_chapter_id ?? item.resolved_chapter_id),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteTimelineEntry(novelId, entryId) {
    state.timelineEntries = state.timelineEntries.filter((item) => item.novel_id !== novelId || item.id !== entryId)
  }

  function readerPerspectives(novelId = state.activeNovelId) {
    return state.readerPerspectives.filter((item) => item.novel_id === novelId)
  }

  function createReaderPerspective(novelId, input) {
    const entry = {
      id: state.nextReaderPerspectiveId++,
      novel_id: novelId,
      type: String(input?.type ?? 'known'),
      content: String(input?.content ?? ''),
      related_truth: String(input?.related_truth ?? ''),
      planted_chapter: Number(input?.planted_chapter ?? 1),
      revealed_chapter: Number(input?.revealed_chapter ?? 0),
      created_at: now,
    }
    state.readerPerspectives = [...state.readerPerspectives, entry]
    return entry
  }

  function updateReaderPerspective(novelId, entryId, input) {
    state.readerPerspectives = state.readerPerspectives.map((item) =>
      item.novel_id === novelId && item.id === entryId
        ? {
          ...item,
          type: String(input?.type ?? item.type),
          content: String(input?.content ?? item.content),
          related_truth: String(input?.related_truth ?? item.related_truth),
          planted_chapter: Number(input?.planted_chapter ?? item.planted_chapter),
          revealed_chapter: Number(input?.revealed_chapter ?? item.revealed_chapter),
        }
        : item,
    )
  }

  function deleteReaderPerspective(novelId, entryId) {
    state.readerPerspectives = state.readerPerspectives.filter((item) => item.novel_id !== novelId || item.id !== entryId)
  }

  function preferences(novelId = state.activeNovelId) {
    return {
      global: state.preferences.global.filter((item) => item.is_global),
      novel: state.preferences.novel.filter((item) => item.novel_id === novelId),
    }
  }

  function createPreference(novelId, input) {
    const item = {
      id: state.nextPreferenceId++,
      novel_id: input?.is_global ? 0 : novelId,
      is_global: Boolean(input?.is_global),
      category: String(input?.category ?? '未分类'),
      content: String(input?.content ?? ''),
      created_at: now,
    }
    if (item.is_global) state.preferences.global = [...state.preferences.global, item]
    else state.preferences.novel = [...state.preferences.novel, item]
    return item
  }

  function updatePreference(preferenceId, input) {
    const update = (item) => item.id === preferenceId
      ? {
        ...item,
        category: String(input?.category ?? item.category),
        content: String(input?.content ?? item.content),
        is_global: input?.is_global ?? item.is_global,
      }
      : item
    state.preferences.global = state.preferences.global.map(update)
    state.preferences.novel = state.preferences.novel.map(update)
    return [...state.preferences.global, ...state.preferences.novel].find((item) => item.id === preferenceId) ?? null
  }

  function deletePreference(preferenceId) {
    state.preferences.global = state.preferences.global.filter((item) => item.id !== preferenceId)
    state.preferences.novel = state.preferences.novel.filter((item) => item.id !== preferenceId)
  }

  function writingActivity() {
    return state.writingActivity
  }

  function writingStats() {
    return state.writingStats
  }

  function skills() {
    return state.skills
  }

  function deleteSkill(input) {
    state.skills = state.skills.filter((item) => item.source !== input?.source || item.name !== input?.name)
  }

  function extractStyle() {
    return {
      name: '雨夜留白',
      description: '以短句和停顿保留悬念。',
      raw_content: '---\nname: 雨夜留白\ndescription: 以短句和停顿保留悬念。\ncategory: 写作技法\n---\n用动作间隔保留未说出口的信息。',
      file_path: 'skills/雨夜留白.md',
    }
  }

  function llmConfig() {
    return {
      providers: [
        {
          key: 'mock',
          name: 'Mock Provider',
          base_url: 'https://api.example.com/v1',
          endpoint_type: 'chat',
          chat_url: '',
          api_key: '',
          platform_url: '',
          help_text: '',
          temperature: 0.7,
          source: 'builtin',
          builtin_models: [
            {
              id: 'gpt',
              name: 'Mock GPT',
              context_window: 128000,
              max_output_tokens: 4096,
              supports_thinking: true,
              reasoning_levels: ['high'],
              supports_vision: false,
            },
          ],
          custom_models: [],
        },
      ],
    }
  }

  function embeddingConfig() {
    return {
      provider_key: 'onnx',
      endpoint_url: '',
      api_key: '',
      model_id: 'bge-small-zh-v1.5',
      dimensions: 512,
      user: '',
      provider_type: 'onnx',
      onnx_model_path: '',
      onnx_vocab_path: '',
      max_sequence_length: 512,
      normalize_embeddings: true,
    }
  }

  function sqliteVecStatus() {
    return {
      available: true,
      status: 'ready',
      runtime_identifier: 'mock-runtime',
      file_name: 'sqlite_vec_mock.dll',
      error: '',
    }
  }

  function referenceAnchors() {
    return [
      ...state.referenceAnchors,
      ...state.createdReferenceAnchors,
    ]
  }

  function createReferenceAnchor(input) {
    const anchor = {
      anchor_id: 200 + state.createdReferenceAnchors.length,
      novel_id: input?.novel_id ?? state.activeNovelId,
      title: String(input?.title ?? ''),
      author: String(input?.author ?? ''),
      source_path: String(input?.source_path ?? ''),
      source_kind: String(input?.source_kind ?? ''),
      license_status: String(input?.license_status ?? ''),
      visibility: String(input?.visibility ?? 'private'),
      source_trust: String(input?.source_trust ?? 'imported'),
      owner_scope: input?.visibility === 'workspace' ? 'workspace_corpus' : 'novel',
      owner_novel_id: input?.visibility === 'workspace' ? null : input?.novel_id ?? state.activeNovelId,
      user_tags: Array.isArray(input?.user_tags) ? input.user_tags : [],
      source_file_hash: `hash-created-${state.createdReferenceAnchors.length}`,
      build_version: 'mock-reference-v1',
      status: 'ready',
      created_at: now,
      updated_at: now,
    }
    state.createdReferenceAnchors.push(anchor)
    return anchor
  }

  function referenceBuildStatus(anchorId) {
    const key = String(anchorId)
    if (state.referenceBuildStatuses[key]) {
      return state.referenceBuildStatuses[key]
    }

    return {
      novel_id: 42,
      anchor_id: anchorId,
      status: 'ready',
      stage: 'completed',
      source_segment_count: 3,
      material_count: 6,
      slot_count: 2,
      vector_count: 0,
      last_error: '',
      updated_at: now,
    }
  }

  function rebuildReferenceAnchor(anchorId) {
    const status = referenceBuildStatus(anchorId)
    state.referenceBuildStatuses[String(anchorId)] = status
    state.referenceAnchors = state.referenceAnchors.map(anchor =>
      anchor.anchor_id === anchorId ? { ...anchor, status: 'ready', updated_at: now } : anchor)
    state.createdReferenceAnchors = state.createdReferenceAnchors.map(anchor =>
      anchor.anchor_id === anchorId ? { ...anchor, status: 'ready', updated_at: now } : anchor)
    return status
  }

  function searchReferenceMaterials(input = {}) {
    if (options.referenceStress) {
      return searchStressReferenceMaterials(input)
    }

    return pageResult([])
  }

  function searchStressReferenceMaterials(input = {}) {
    const page = Math.max(1, Number(input.page ?? 1))
    const size = Math.max(1, Number(input.size ?? 10))
    const total = options.referenceStress.materialTotal
    const anchorIds = Array.isArray(input.anchor_ids) ? input.anchor_ids : []
    const anchorScopedPreview = anchorIds.length === 1 && size === 5
    const startIndex = (page - 1) * size + 1
    const endIndex = Math.min(total, startIndex + size - 1)
    const items = []

    if (startIndex <= total) {
      for (let index = startIndex; index <= endIndex; index += 1) {
        items.push(stressReferenceMaterial(index))
      }
    }

    return pagedResult(items, page, size, anchorScopedPreview ? total : total)
  }

  function stressReferenceMaterial(index) {
    const padded = String(index).padStart(4, '0')
    const anchorId = options.referenceStress.anchor.anchor_id
    return {
      material_id: `stress-mat-${padded}`,
      anchor_id: anchorId,
      source_segment_id: `stress-seg-${padded}`,
      material_type: index % 5 === 0 ? 'passage' : 'sentence',
      function_tag: index % 3 === 0 ? 'environment' : 'emotion_evidence',
      emotion_tag: 'restrained',
      scene_tag: 'rain_threshold',
      pov_tag: 'close',
      technique_tag: index % 2 === 0 ? 'delayed_reaction' : 'subtext',
      function_confidence: 0.94,
      emotion_confidence: 0.91,
      pov_confidence: 0.9,
      text: `10MB 水痕参考材料 ${padded}：雨声压着旧城门，林岚只记录杯底半圈水痕、灯影和门缝停顿，不提前确认门外身份。`,
      source_hash: `hash-stress-material-${padded}`,
      extractor_version: 'mock-stress-extractor-v1',
      user_verified: index % 7 === 0,
      created_at: now,
      score_components: {
        lexical: index === 1 ? 0.97 : 0.84,
        function: index === 1 ? 0.92 : 0.81,
        prose_duty: index === 1 ? 0.9 : 0.78,
        feedback_boost: 0.1,
      },
    }
  }

  function generateReferenceBlueprint(input = {}) {
    const blueprint = makeReferenceBlueprint(state.nextReferenceBlueprintId++, {
      chapter_number: input.chapter_number,
      title: input.title || '10MB 材料绑定验收',
      known_facts: input.known_facts ?? [],
      forbidden_facts: input.forbidden_facts ?? [],
      primary_anchor_id: input.anchor_ids?.[0] ?? options.referenceStress?.anchor.anchor_id ?? 0,
      status: 'draft',
      latest_review: null,
    })
    state.referenceBlueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint
  }

  function reviewReferenceBlueprint(input = {}) {
    const blueprint = cloneReferenceBlueprint(input.blueprint_id)
    blueprint.status = 'reviewed'
    blueprint.latest_review = makeReferenceReview(blueprint.blueprint_id, `review-${blueprint.blueprint_id}`)
    blueprint.updated_at = now
    state.referenceBlueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint.latest_review
  }

  function approveReferenceBlueprint(input = {}) {
    const blueprint = cloneReferenceBlueprint(input.blueprint_id)
    blueprint.status = 'approved'
    blueprint.updated_at = now
    state.referenceBlueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint
  }

  function bindReferenceBlueprintMaterials(input = {}) {
    const blueprint = cloneReferenceBlueprint(input.blueprint_id)
    blueprint.status = 'material_bound'
    blueprint.updated_at = now
    state.referenceBlueprints[String(blueprint.blueprint_id)] = blueprint
    return {
      blueprint_id: blueprint.blueprint_id,
      links: [{
        link_id: `stress-link-${blueprint.blueprint_id}-001`,
        blueprint_id: blueprint.blueprint_id,
        beat_id: blueprint.beats[0].beat_id,
        material_id: 'stress-mat-0001',
        intended_use: 'source-backed detail from the 10MB segmented reference source',
        max_rewrite_level: 'L1',
        selected: true,
        score: 0.96,
        score_components: {
          lexical: 0.97,
          function: 0.92,
          prose_duty: 0.9,
        },
        fit_explanation: 'Uses generated material with stable source segment and hash provenance.',
        created_at: now,
      }],
    }
  }

  function makeReferenceBlueprint(blueprintId, overrides = {}) {
    const beat = makeReferenceBeat()
    return {
      blueprint_id: blueprintId,
      novel_id: 42,
      chapter_number: overrides.chapter_number ?? 1,
      title: overrides.title ?? '10MB 材料绑定验收',
      status: overrides.status ?? 'draft',
      source_plan_scope: 'chapter',
      source_plan_hash: `stress-plan-${blueprintId}`,
      context_hash: `stress-context-${blueprintId}`,
      analysis_contract_hash: `stress-contract-${blueprintId}`,
      blueprint_version: 1,
      build_version: 'mock-stress-blueprint-v1',
      parent_blueprint_id: 0,
      primary_anchor_id: overrides.primary_anchor_id ?? 0,
      chapter_function: '验证大体量参考源可以进入参考写作链路。',
      logic_analysis: referenceTrack('logic', ['从10MB参考源检索材料', '保持事实边界']),
      emotion_analysis: referenceTrack('emotion', ['警觉', '克制']),
      narration_analysis: referenceTrack('narration', ['close POV', '来源可审计细节']),
      character_analysis: referenceTrack('character', ['林岚只记录可见证据']),
      reference_analysis: referenceTrack('reference', ['绑定自动分段材料']),
      transition_plan: referenceTrack('transition', ['从材料搜索转入蓝图节拍']),
      execution_contract: {
        track: 'execution',
        summary: '使用自动分段材料的水痕细节完成候选节拍。',
        paragraph_intentions: [beat.paragraph_intention],
        execution_modes: [beat.execution_mode],
        anti_screenplay_duties: [beat.anti_screenplay_duty],
        source_backed_detail_targets: [beat.source_backed_detail_target],
        candidate_rejection_rules: [beat.candidate_rejection_rule],
      },
      previous_state: '大体量参考源已导入。',
      final_state: '蓝图可绑定来源材料。',
      final_hook: '继续候选段落前仍停在审批边界。',
      global_pov: '林岚',
      global_narrative_distance: 'close',
      known_facts: overrides.known_facts ?? ['只使用10MB参考源可审计材料'],
      forbidden_facts: overrides.forbidden_facts ?? ['未经来源支持的门外身份'],
      risk_flags: [],
      beats: [beat],
      latest_review: overrides.latest_review ?? null,
      created_at: now,
      updated_at: now,
    }
  }

  function makeReferenceBeat() {
    return {
      beat_id: 'stress-beat-001',
      beat_index: 1,
      scene_index: 1,
      beat_type: 'scene',
      narrative_function: '用大体量参考源中的水痕细节承载压力。',
      logic_premise: '材料来自自动分段的10MB参考源。',
      conflict_pressure: '不能越过来源材料推断门外身份。',
      causality_in: '检索到水痕材料。',
      causality_out: '蓝图保留受限视角。',
      transition_in: '材料浏览完成。',
      transition_out: '绑定到节拍。',
      pov_character: '林岚',
      narrative_distance: 'close',
      viewpoint_allowed_knowledge: ['水痕', '灯影', '门缝停顿'],
      viewpoint_forbidden_knowledge: ['门外身份'],
      character_states_before: ['警觉'],
      character_states_after: ['克制'],
      character_goals: ['确认可见证据'],
      character_misbeliefs: ['门外动静可能只是雨声'],
      relationship_pressure: ['不能暴露判断'],
      emotion_trigger: '杯底水痕',
      emotion_before: '警觉',
      emotion_after: '克制',
      suppressed_reaction: '没有立刻抬头',
      external_evidence: '杯底半圈水痕',
      narration_strategy: 'close POV, sensory evidence only.',
      rhythm_strategy: '先停顿，再动作。',
      paragraph_intention: '用10MB参考源材料支撑受限视角细节。',
      execution_mode: 'delayed_reaction',
      anti_screenplay_duty: '避免纯动作走位。',
      sensory_anchor_target: '雨声',
      subtext_plan: '水痕暗示刚有人离开。',
      source_backed_detail_target: '杯底半圈水痕',
      candidate_rejection_rule: '拒绝无来源身份揭示。',
      scene_facts: ['桌上有杯子', '雨声很大'],
      forbidden_facts: ['门外身份'],
      reference_query: {
        query: '10MB 水痕 受限视角',
        material_types: ['sentence', 'passage'],
        emotion_tags: ['restrained'],
        function_tags: ['emotion_evidence'],
        pov_tags: ['close'],
        technique_tags: ['subtext'],
        max_results: 3,
      },
      required_material_types: ['sentence'],
      max_rewrite_level: 'L1',
      slot_plan: [{ slot_name: 'object', value: '杯底水痕' }],
      locked_phrase_policy: '不锁定原句。',
      no_reuse_reason: '',
      prose_duties: ['source_backed_detail', 'subtext'],
      risk_flags: [],
    }
  }

  function referenceTrack(name, points) {
    return {
      track: name,
      summary: `${name} stress summary`,
      points,
    }
  }

  function makeReferenceReview(blueprintId, reviewId) {
    return {
      review_id: reviewId,
      blueprint_id: blueprintId,
      context_hash: `stress-context-${blueprintId}`,
      source_plan_hash: `stress-plan-${blueprintId}`,
      analysis_contract_hash: `stress-contract-${blueprintId}`,
      review_version: 1,
      status: 'passed',
      score: 0.95,
      logic_errors: [],
      causality_errors: [],
      emotion_errors: [],
      narration_errors: [],
      execution_errors: [],
      character_state_errors: [],
      pov_errors: [],
      continuity_errors: [],
      transition_errors: [],
      forbidden_fact_errors: [],
      reference_binding_errors: [],
      material_fit_errors: [],
      screenplay_drift_risks: [],
      ai_prose_risks: [],
      novelistic_narration_errors: [],
      required_fixes: [],
      defects: [],
      reviewed_at: now,
    }
  }

  function cloneReferenceBlueprint(blueprintId) {
    const blueprint = state.referenceBlueprints[String(blueprintId)]
    if (!blueprint) throw new Error(`Unknown reference blueprint ${blueprintId}`)
    return JSON.parse(JSON.stringify(blueprint))
  }

  function toReferenceBlueprintSummary(blueprint) {
    return {
      blueprint_id: blueprint.blueprint_id,
      novel_id: blueprint.novel_id,
      chapter_number: blueprint.chapter_number,
      title: blueprint.title,
      status: blueprint.status,
      source_plan_hash: blueprint.source_plan_hash,
      updated_at: blueprint.updated_at,
    }
  }

  function pageResult(items) {
    return {
      items,
      total: items.length,
      page: 1,
      size: Math.max(items.length, 1),
      total_pages: 1,
    }
  }

  function pagedResult(items, page, size, total) {
    return {
      items,
      total,
      page,
      size,
      total_pages: Math.max(1, Math.ceil(total / size)),
    }
  }

  function wait(ms) {
    return new Promise((resolve) => window.setTimeout(resolve, ms))
  }

  function defaultValueFor(method) {
    if (method.startsWith('Get')) return null
    if (method.startsWith('List')) return []
    return null
  }
}

main().catch((error) => {
  console.error(error)
  process.exitCode = 1
})
