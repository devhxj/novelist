import fs from 'node:fs/promises'
import assert from 'node:assert/strict'
import path from 'node:path'
import {
  verifyChapterReferenceBridgeCalls,
  verifyBridgeCalls,
  verifyCorpusLibraryBridgeCalls,
  verifyDiagnosticsBridgeCalls,
  verifyErrorBridgeCalls,
  verifyGitBridgeCalls,
  verifyLayoutBridgeCalls,
  verifyPhase15SurfaceBridgeCalls,
  verifyPatternBridgeCalls,
  verifyReferenceBridgeCalls,
  verifyRelativeTimeBridgeCalls,
  verifySmokeBridgeCalls,
  verifyStartupBridgeCalls,
  verifyStressGuardrails,
  verifyUpdateBridgeCalls,
  verifyWritingBridgeCalls,
} from './bridge-guardrails.mjs'
import { verifyErrorFeedbackWorkflow } from './error-feedback.mjs'
import {
  makeLargeChineseFixture,
  makeStressChapters,
  makeStressReferenceFixture,
} from './fixtures.mjs'
import {
  diagnostics,
  logStep,
  newAppPage,
  outputDir,
  phaseOutputRoot,
  repoRoot,
  runConfig,
} from './app-harness.mjs'
import {
  assertCopyableDiagnostic,
  assertNoSensitiveDiagnosticsVisible,
  errorAlert,
  installClipboardSpy,
  sensitiveDiagnosticDetails,
} from './diagnostic-helpers.mjs'
import { verifyGitHistoryWorkflow, verifyRelativeTimeRefreshWorkflow } from './git-workflows.mjs'
import { verifyLayoutPersistenceWorkflow } from './layout-workflows.mjs'
import { verifyMetadataActionWorkflow, verifyMetadataPanels } from './metadata-workflows.mjs'
import { settingsFixture } from './mock-bridge.mjs'
import { verifyPhase15CompactMatrixWorkflow } from './phase15-compact-workflows.mjs'
import { verifyPhase15StressWorkflow } from './phase15-stress-workflows.mjs'
import { verifyReferenceErrorFeedbackWorkflow } from './reference-error-feedback.mjs'
import {
  assertBridgeCallCount,
  assertEditorContains,
  bridgeCallCount,
  clickCardAction,
  dispatchNovelImportDrop,
  expectHidden,
  expectVisible,
  replaceEditorText,
  shortcutKey,
  waitForBridgeCall,
  waitForBridgeCallArg,
  waitForBridgeCallCountAfter,
} from './page-helpers.mjs'
import { chapterButton, clickActivity, ensureChapterBlockExpanded, novelCard } from './navigation-helpers.mjs'
import { makeTagFilter } from './runtime.mjs'
import {
  verifyChapterRangeSelectorWorkflow,
  verifyStyleSampleWorkflow,
} from './style-pattern-workflows.mjs'
import { verifyStressReferenceMaterialPath } from './stress-workflows.mjs'
import {
  verifyBootstrapStates,
  verifyFixtureFaultModes,
} from './startup-workflows.mjs'
import {
  verifyChapterWorkflow,
  verifyChatWorkflow,
  verifyCompactViewportSmoke,
  verifyEditorSaveWorkflow,
  verifyImportExportFilePickerWorkflow,
  verifyNovelChapterWorkflow,
  verifyReferenceSmoke,
  verifySearchWorkflow,
  verifyShellNavigation,
} from './surface-workflows.mjs'
import {
  verifySettingsFailureWorkflow,
  verifySettingsPersistenceWorkflow,
  verifySettingsWorkflow,
  verifyUpdateWorkflow,
} from './settings-workflows.mjs'
import { usabilityObservation, writeUsabilityReport } from './usability-report.mjs'

export async function runSmokeSuite(browser, url) {
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
  if (runConfig.grep === '@chapter-reference') {
    return { initialized: true, allowSaveContent: true }
  }

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
    assertBridgeCallCount,
    errorAlert,
    expectHidden,
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

async function verifyCorpusLibraryWorkflow(page) {
  await clickActivity(page, '素材库')
  await expectVisible(page.getByRole('heading', { name: /素材库|语料库管理/ }).first(), 'corpus library heading')
  await expectVisible(page.getByLabel('材料库结果').or(page.getByLabel('材料库搜索')).first(), 'corpus library material area')

  await page.getByRole('button', { name: '检索材料库' }).click()
  await expectVisible(page.getByTestId('reference-material-library-card').first(), 'corpus library material card')
  await page.getByRole('button', { name: /查看 .* 的材料明细/ }).first().click()
  const drawer = page.getByTestId('reference-material-detail-drawer')
  await expectVisible(drawer, 'corpus material detail drawer')
  await expectVisible(drawer.getByText('来源片段'), 'corpus material detail segments')
  await expectVisible(drawer.getByText('处理记录'), 'corpus material detail processing notes')
  await expectVisible(drawer.getByText('工作区语料'), 'corpus material detail owner scope')
  await expectVisible(drawer.getByText('活跃'), 'corpus material detail archive state')
  await expectVisible(drawer.getByText(/lexical 0\.92/), 'corpus material detail score components')
  await expectVisible(drawer.getByText(/segments=3 · materials=2 · slots=2 · vectors=0/), 'corpus material detail structured counts')
  await expectVisible(drawer.getByText(/affected: 101 · mock-mat-rain-001 · mock-seg-rain-001 · object/), 'corpus material detail affected ids')
  await waitForBridgeCall(page, 'GetReferenceMaterialDetail')
  const drawerText = await drawer.innerText()
  assert(!drawerText.includes('D:\\books'), 'material detail drawer must not render local source paths')

  const legacyRetrievalVisible = await page.getByText('参考写作检索', { exact: true }).isVisible().catch(() => false)
  assert.equal(legacyRetrievalVisible, false, 'corpus library activity must not render the retired reference-writing retrieval section')
}

async function verifyChapterReferenceWorkflow(page) {
  await clickActivity(page, '章节')
  await ensureChapterBlockExpanded(page)
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.locator('.monaco-editor').first(), 'chapter editor')
  await waitForBridgeCallArg(page, 'GetContent', 1, 'chapters/1.md')

  await page.getByRole('button', { name: /参考素材/ }).click()
  const drawer = page.getByTestId('chapter-reference-panel')
  await expectVisible(drawer, 'chapter reference drawer')
  await expectVisible(drawer.getByText('推荐素材'), 'chapter reference recommendations heading')
  await expectVisible(drawer.getByTestId('chapter-reference-material-card').first(), 'chapter reference recommendation card')
  await waitForBridgeCall(page, 'SearchReferenceMaterials')
  await waitForBridgeCall(page, 'GetReferenceOrchestrationRuns')

  await drawer.getByRole('button', { name: '启动参考流程' }).click()
  await waitForBridgeCall(page, 'StartReferenceOrchestrationRun')
  await expectVisible(drawer.getByTestId('chapter-reference-orchestration-run'), 'chapter reference orchestration status')
  await expectVisible(drawer.getByText('请确认本章来源和事实边界后继续。'), 'chapter reference orchestration decision')

  await drawer.getByRole('button', { name: '确认并继续' }).click()
  await waitForBridgeCall(page, 'ResumeReferenceOrchestrationRun')
  await expectVisible(drawer.getByText('来源和事实边界已确认，请审批自动蓝图。'), 'chapter reference resumed decision')

  const saveCountBeforeCandidate = await bridgeCallCount(page, 'SaveContent')
  await drawer.getByRole('button', { name: '生成候选' }).first().click()
  await waitForBridgeCall(page, 'AdaptReferenceMaterial')
  const candidatePreview = drawer.getByTestId('chapter-reference-candidate-preview')
  await expectVisible(candidatePreview, 'chapter reference candidate preview')
  await expectVisible(candidatePreview.getByText('林岚把雨声和杯底半圈水痕'), 'chapter reference candidate text')
  await candidatePreview.getByRole('button', { name: '复制' }).click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('杯底半圈水痕'), null, { timeout: 12_000 })
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeCandidate, 'copying a candidate must not save chapter content')

  await candidatePreview.getByRole('button', { name: '追加末尾' }).click()
  await assertEditorContains(page, '林岚把雨声和杯底半圈水痕重新放回眼前')
  await waitForBridgeCallCountAfter(page, 'SaveContent', saveCountBeforeCandidate)

  const adaptCountBeforeFailed = await bridgeCallCount(page, 'AdaptReferenceMaterial')
  const saveCountBeforeFailed = await bridgeCallCount(page, 'SaveContent')
  await drawer.getByLabel('已知事实').fill('mock_failed_audit')
  await drawer.getByRole('button', { name: '生成候选' }).first().click()
  await waitForBridgeCallCountAfter(page, 'AdaptReferenceMaterial', adaptCountBeforeFailed)
  await expectVisible(candidatePreview.getByText('L2 · failed'), 'failed audit candidate status')
  await expectVisible(drawer.getByTestId('chapter-reference-candidate-blocked'), 'failed audit insertion block')
  assert.equal(await candidatePreview.getByRole('button', { name: '插入光标' }).isDisabled(), true, 'failed audit candidate must not allow cursor insertion')
  assert.equal(await candidatePreview.getByRole('button', { name: '追加末尾' }).isDisabled(), true, 'failed audit candidate must not allow append insertion')
  assert.equal(await candidatePreview.getByRole('button', { name: '替换选区' }).isDisabled(), true, 'failed audit candidate must not allow selection replacement')
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeFailed, 'failed audit candidate must not save chapter content')

  await drawer.getByRole('button', { name: '取消流程' }).click()
  await waitForBridgeCall(page, 'CancelReferenceOrchestrationRun')
  await expectVisible(drawer.getByText('cancelled · blueprint_approval'), 'chapter reference cancelled run status')

  const firstSearchInput = await page.evaluate(() => {
    const call = window.__appMockState.calls.find((item) => item.method === 'SearchReferenceMaterials')
    return call?.args?.[0] ?? null
  })
  assert(firstSearchInput, 'chapter reference drawer must call SearchReferenceMaterials')
  assert.deepEqual(firstSearchInput.anchor_ids, [], 'chapter reference drawer must search all accessible corpus materials by default')

  const startInput = await page.evaluate(() => {
    const call = window.__appMockState.calls.find((item) => item.method === 'StartReferenceOrchestrationRun')
    return call?.args?.[0] ?? null
  })
  assert(startInput, 'chapter reference drawer must call StartReferenceOrchestrationRun')
  assert.equal(startInput.chapter_number, 1, 'chapter reference drawer must derive chapter number from active tab')
  assert.equal(startInput.anchor_ids, null, 'chapter reference drawer must not require selected anchors')

  const resumeInput = await page.evaluate(() => {
    const call = window.__appMockState.calls.find((item) => item.method === 'ResumeReferenceOrchestrationRun')
    return call?.args?.[0] ?? null
  })
  assert(resumeInput, 'chapter reference drawer must support in-place orchestration resume')
  assert.equal(resumeInput.decision_type, 'confirm_source_and_facts', 'chapter reference resume must use the backend decision type')

  const adaptInput = await page.evaluate(() => {
    const call = window.__appMockState.calls.find((item) => item.method === 'AdaptReferenceMaterial')
    return call?.args?.[0] ?? null
  })
  assert(adaptInput, 'chapter reference drawer must generate a candidate from an existing material')
  assert.equal(adaptInput.max_rewrite_level, 'L2', 'chapter reference candidate generation must use an explicit rewrite budget')

  await page.getByRole('button', { name: '大纲' }).click()
  await expectHidden(drawer, 'chapter reference drawer after switching to outline view')

  await page.getByRole('button', { name: /故事状态/ }).click()
  await waitForBridgeCallArg(page, 'GetContent', 1, 'novelist.md')
  await expectHidden(drawer, 'chapter reference drawer after switching to non-chapter file')
}

export async function runFullSuite(browser, url) {
  const { consoleErrors, pageErrors } = diagnostics

  if (runConfig.grep === '@phase15-stress') {
    logStep('checking Phase 15 large import and Git fixtures')
    await verifyPhase15StressWorkflow(browser, url, consoleErrors, pageErrors)
    return
  }

  if (runConfig.grep === '@phase15-compact') {
    logStep('checking Phase 15 compact viewport matrix')
    await verifyPhase15CompactMatrixWorkflow(browser, url, consoleErrors, pageErrors)
    return
  }

  const shouldRun = makeTagFilter(runConfig.grep)
  const isPhase15Surface = runConfig.grep === '@phase15-surface'

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
  if (runConfig.grep === '@error' || runConfig.grep === '@update' || runConfig.grep === '@chapter-reference') {
    await installClipboardSpy(page)
  }
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'workspace title')
  await expectVisible(page.getByText('AI 对话'), 'chat panel')
  await page.screenshot({ path: path.join(outputDir, 'app-01-shell.png'), fullPage: true })

  if (runConfig.grep === '@corpus-library') {
    logStep('checking Phase 16 corpus library split')
    await verifyCorpusLibraryWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-phase16-corpus-library.png'), fullPage: true })
  }

  if (runConfig.grep === '@chapter-reference') {
    logStep('checking Phase 16 chapter reference drawer')
    await verifyChapterReferenceWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-phase16-chapter-reference.png'), fullPage: true })
  }

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

  if (shouldRun('@surface') || isPhase15Surface) {
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

  if (shouldRun('@surface') || isPhase15Surface) {
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
    await verifyReferenceErrorFeedbackWorkflow(errorFeedbackWorkflowContext(page, browser, url, consoleErrors, pageErrors))
  }

  if (shouldRun('@surface') || isPhase15Surface) {
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

  if (shouldRun('@surface') || isPhase15Surface) {
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
  } else if (runConfig.grep === '@corpus-library') {
    await verifyCorpusLibraryBridgeCalls(page)
  } else if (runConfig.grep === '@chapter-reference') {
    await verifyChapterReferenceBridgeCalls(page)
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
  } else if (runConfig.grep === '@phase15-surface') {
    await verifyPhase15SurfaceBridgeCalls(page)
  } else if (runConfig.grep === '@error') {
    await verifyErrorBridgeCalls(page)
  } else {
    await verifyBridgeCalls(page)
  }
  await page.close()
}

export async function runStressSuite(browser, url) {
  const { consoleErrors, pageErrors } = diagnostics
  const startedAt = Date.now()
  const chapterCount = runConfig.stressChapterCount
  const stressChapterNumber = chapterCount
  const stressTitle = `长篇压力章 ${String(stressChapterNumber).padStart(3, '0')}`
  const stressPath = `chapters/${stressChapterNumber}.md`
  const largeText = makeLargeChineseFixture(runConfig.stressSizeBytes)
  const chapters = makeStressChapters(chapterCount, stressTitle)
  const referenceStress = makeStressReferenceFixture(largeText, runConfig.stressChapterCount)
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

export async function runUsabilitySuite(browser, url) {
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

  await writeUsabilityReport(observations, { phaseOutputRoot, outputDir, repoRoot })
  await verifySmokeBridgeCalls(page)
  await page.close()
}
