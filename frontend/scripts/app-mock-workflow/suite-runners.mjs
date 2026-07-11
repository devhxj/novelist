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
  verifyReferenceWorkspaceBridgeCalls,
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
  assertEditorNotContains,
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
  verifyReferenceWorkspaceWorkflow,
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

const FULL_MATERIAL_LEAK_SENTINEL = '__FULL_MATERIAL_SHOULD_NOT_RENDER__'
const MOCK_CORPUS_INSERTION_TEXT = '林岚把杯底半圈水痕压进记忆里，没有急着回头。'
const MOCK_CORPUS_TRANSITION_TEXT = '门外的雨声把沉默往前推了一寸。'
const MOCK_REFERENCE_CANDIDATE_TEXT = '林岚没有立刻抬头。杯底那半圈水痕贴着木纹，像刚被雨夜重新描过一遍；她只把指尖收紧，确认门外的人还不知道这条线索。'
const CORPUS_LIBRARY_FORBIDDEN_CHAPTER_METHODS = [
  'GenerateReferenceChapterBlueprint',
  'ReviewReferenceChapterBlueprint',
  'ApproveReferenceChapterBlueprint',
  'GetReferenceChapterBlueprint',
  'GetReferenceChapterBlueprints',
  'StartReferenceOrchestrationRun',
  'GetReferenceOrchestrationRuns',
  'GetReferenceOrchestrationRunEvents',
  'AdaptReferenceMaterial',
  'BindReferenceBlueprintMaterials',
  'GenerateReferenceAnchoredDraft',
  'GetReferenceDraftCandidates',
  'AuditReferenceAnchoredDraft',
  'GetReferenceAnchoredDraftAudits',
  'SaveContent',
]

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

async function waitForLatestBridgeCallWithResult(page, method, previousCount = -1) {
  await page.waitForFunction(
    ({ method, previousCount }) => {
      const calls = window.__appMockState.calls.filter((call) => call.method === method)
      return calls.length > previousCount && Object.hasOwn(calls.at(-1) ?? {}, 'result')
    },
    { method, previousCount },
    { timeout: 12_000 },
  )

  return page.evaluate((method) => {
    const calls = window.__appMockState.calls.filter((call) =>
      call.method === method && Object.hasOwn(call, 'result'))
    return calls.at(-1) ?? null
  }, method)
}

async function waitForLatestBridgeResult(page, method, previousCount = -1) {
  const call = await waitForLatestBridgeCallWithResult(page, method, previousCount)
  return call?.result ?? null
}

async function assertCorpusLibraryLegacyWritingEntrypointsHidden(page, context) {
  await expectHidden(page.getByRole('heading', { name: '参考写作检索' }), `${context} legacy reference-writing retrieval heading`)
  await expectHidden(page.getByTestId('reference-orchestration-panel'), `${context} legacy orchestration panel`)
  await expectHidden(page.getByRole('button', { name: '启动候选编排' }), `${context} legacy orchestration start button`)
  await expectHidden(page.getByRole('heading', { name: '默认编排' }), `${context} legacy orchestration heading`)
  await expectHidden(page.getByTestId('reference-manual-material-search'), `${context} legacy manual material search`)
  await expectHidden(page.getByTestId('reference-blueprint-panel'), `${context} legacy chapter blueprint panel`)
  await expectHidden(page.getByRole('heading', { name: '章节蓝图' }), `${context} legacy chapter blueprint heading`)
  await expectHidden(page.getByRole('button', { name: /生成蓝图/ }), `${context} legacy generate blueprint button`)
  await expectHidden(page.getByTestId('reference-blueprint-detail'), `${context} legacy blueprint detail`)
  await expectHidden(page.getByRole('button', { name: /^绑定$/ }), `${context} legacy bind materials button`)
  await expectHidden(page.getByRole('button', { name: /^候选$/ }), `${context} legacy generate anchored draft button`)
  await expectHidden(page.getByRole('button', { name: /生成锚定草稿|生成候选/ }), `${context} legacy anchored draft generation button`)
}

async function assertCorpusLibraryNoChapterWritingBridgeCalls(page, context) {
  const counts = await page.evaluate((methods) => {
    return Object.fromEntries(methods.map((method) => [
      method,
      window.__appMockState.calls.filter((call) => call.method === method).length,
    ]))
  }, CORPUS_LIBRARY_FORBIDDEN_CHAPTER_METHODS)

  for (const method of CORPUS_LIBRARY_FORBIDDEN_CHAPTER_METHODS) {
    assert.equal(counts[method], 0, `${context} must not call ${method}`)
  }
}

async function verifyCorpusLibraryAnalysisResultsTab(page, corpusTabs) {
  await corpusTabs.getByRole('tab', { name: '分析结果' }).click()
  const analysisTab = page.getByTestId('reference-corpus-analysis-library-tab')
  await expectVisible(analysisTab, 'corpus library analysis results tab')
  assert.equal(await corpusTabs.getByRole('tab', { name: '分析结果' }).getAttribute('aria-selected'), 'true', 'corpus library analysis results tab should be selected')

  const observationCard = analysisTab
    .locator('[data-testid="reference-corpus-feature-observation-card"], [data-testid="reference-corpus-analysis-observation-card"], [data-testid*="observation"][data-testid*="card"], article')
    .filter({ hasText: /emotion_state|restrained|动作压住外显情绪/ })
    .first()
await expectVisible(observationCard, 'corpus library analysis observation card')
 const nodeWindowCalls = await bridgeCallCount(page, 'GetReferenceCorpusNodeWindow')
 await observationCard.getByRole('button', { name: '定位原文' }).click()
 await waitForBridgeCallCountAfter(page, 'GetReferenceCorpusNodeWindow', nodeWindowCalls)
const locatedEvidence = page.getByTestId('located-corpus-evidence')
await expectVisible(locatedEvidence, 'located corpus evidence')
 await page.waitForFunction(() => !document.querySelector('[data-testid=located-corpus-evidence]')?.textContent?.includes('正在定位原文节点'), null, { timeout: 12_000 })
 const evidenceHighlight = locatedEvidence.locator('[data-corpus-evidence-selection]')
 if (await evidenceHighlight.count() === 0) throw new Error(`Located corpus evidence did not render a highlight: ${await locatedEvidence.innerText()}`)
 await expectVisible(evidenceHighlight, 'located corpus evidence highlight')

 await corpusTabs.getByRole('tab', { name: '分析结果' }).click()

  const specimenCard = analysisTab
    .locator('[data-testid="reference-corpus-technique-specimen-card"], [data-testid="reference-corpus-analysis-specimen-card"], [data-testid*="specimen"][data-testid*="card"], article')
    .filter({ hasText: /action_as_emotion|细节动作承载压抑情绪|迁移时可以保留技法/ })
    .first()
  await expectVisible(specimenCard, 'corpus library analysis specimen card')

  await assertCorpusLibraryLegacyWritingEntrypointsHidden(page, 'corpus library analysis results tab')
await assertCorpusLibraryNoChapterWritingBridgeCalls(page, 'corpus library analysis results tab')
}

async function verifyCorpusAnalysisJobsWorkflow(page, corpusTabs) {
 const statuses = [
 ['queued', '排队中'], ['running', '运行中'], ['pause_requested', '等待暂停'], ['paused', '已暂停'],
 ['cancel_requested', '等待取消'], ['retry_wait', '等待重试'], ['budget_exhausted', '预算耗尽'],
 ['completed', '已完成'], ['failed', '失败'], ['cancelled', '已取消'],
 ]
 await page.evaluate((statuses) => {
 const base = window.__appMockState.referenceCorpusAnalysisJobs[0]
 const allowedActions = {
 running: ['pause', 'cancel', 'reprioritize'],
 paused: ['resume', 'cancel'],
 failed: ['resume'],
 budget_exhausted: ['resume'],
 }
 window.__appMockState.referenceCorpusAnalysisJobs = statuses.map(([status], index) => ({
 ...base,
 job_id: `mock-corpus-job-${index}`,
 status,
 version: 1,
 allowed_actions: allowedActions[status] ?? [],
 safe_diagnostics: ['lease_renewal_pending', 'worker_attempt_trace_internal'],
 }))
 }, statuses)
 await corpusTabs.getByRole('tab', { name: '后台任务' }).click()
const panel = page.getByTestId('corpus-analysis-jobs-panel')
await expectVisible(panel, 'corpus analysis jobs panel')
 await page.waitForFunction(
 () => document.querySelector('[data-testid="corpus-analysis-jobs-panel"]')?.textContent?.includes('排队中'),
 null,
 { timeout: 12_000 },
 )
 const panelText = await panel.innerText()
 for (const [, label] of statuses) {
 if (!panelText.includes(label)) throw new Error(`Analysis job status ${label} missing from panel: ${panelText}`)
 }

 const runningJob = panel.getByTestId('corpus-analysis-job-mock-corpus-job-1')
 const pausedJob = panel.getByTestId('corpus-analysis-job-mock-corpus-job-3')
 const retryWaitJob = panel.getByTestId('corpus-analysis-job-mock-corpus-job-5')
 const budgetExhaustedJob = panel.getByTestId('corpus-analysis-job-mock-corpus-job-6')
 const failedJob = panel.getByTestId('corpus-analysis-job-mock-corpus-job-8')
 await expectVisible(runningJob.getByTestId('corpus-analysis-job-next-step'), 'running job leave-and-return guidance')
 await expectVisible(pausedJob.getByTestId('corpus-analysis-job-next-step'), 'paused job recovery guidance')
 await expectVisible(retryWaitJob.getByTestId('corpus-analysis-job-next-step'), 'retry-wait job recovery guidance')
 await expectVisible(budgetExhaustedJob.getByTestId('corpus-analysis-job-next-step'), 'budget-exhausted job recovery guidance')
 await expectVisible(failedJob.getByTestId('corpus-analysis-job-next-step'), 'failed job recovery guidance')
 await expectVisible(pausedJob.getByRole('button', { name: '继续' }), 'paused job primary resume action')
 await expectVisible(budgetExhaustedJob.getByRole('button', { name: '继续' }), 'budget-exhausted job primary resume action')
 await expectVisible(failedJob.getByRole('button', { name: '继续' }), 'failed job primary resume action')
 await expectHidden(panel.getByText('lease_renewal_pending').first(), 'collapsed background job diagnostics by default')

 await page.evaluate(() => { window.__appMockState.referenceCorpusAnalysisJobs[1].version = 2 })
 const listCalls = await bridgeCallCount(page, 'ListReferenceCorpusAnalysisJobs')
 await panel.getByRole('button', { name: '暂停' }).click()
 await waitForBridgeCallCountAfter(page, 'ListReferenceCorpusAnalysisJobs', listCalls)
 await expectVisible(runningJob.getByText('运行中', { exact: true }), 'analysis job refreshed after CAS conflict')

 await panel.getByRole('button', { name: '暂停' }).click()
 await expectVisible(runningJob.getByText('已暂停', { exact: true }), 'analysis job paused after refreshed CAS version')
}

async function verifyCorpusAnalysisStartAndReturnWorkflow(page, corpusTabs) {
  const importPanel = page.getByTestId('reference-import-panel')
  const title = '后台分析入口回归'
  const sourcePath = 'D:\\books\\analysis-start.md'
  const createCountBefore = await bridgeCallCount(page, 'CreateReferenceAnchor')

  await importPanel.getByPlaceholder('参考书名').fill(title)
  await importPanel.getByLabel('本地路径').fill(sourcePath)
  await importPanel.getByRole('button', { name: /^创建$/ }).click()

  const createCall = await waitForLatestBridgeCallWithResult(page, 'CreateReferenceAnchor', createCountBefore)
  const anchorId = createCall.result?.anchor_id
  assert(anchorId, 'analysis start workflow must create a source anchor first')

  const sourceRow = page
    .getByTestId('reference-anchor-row')
    .filter({ hasText: title })
    .first()
  await expectVisible(sourceRow, 'newly imported source row')
  await page.screenshot({ path: path.join(outputDir, 'app-phase16-corpus-analysis-start.png'), fullPage: true })

  const enqueueCountBefore = await bridgeCallCount(page, 'EnqueueReferenceCorpusAnalysisJob')
  await sourceRow.getByRole('button', { name: `开始分析 ${title}` }).click()
  const enqueueCall = await waitForLatestBridgeCallWithResult(page, 'EnqueueReferenceCorpusAnalysisJob', enqueueCountBefore)
  const enqueueInput = enqueueCall.args?.[0] ?? {}
  assert.equal(enqueueInput.novel_id, 42, 'analysis start must preserve the current novel')
  assert.equal(enqueueInput.anchor_id, anchorId, 'analysis start must target the imported source')
  assert.equal(enqueueInput.job_kind, 'feature_analysis', 'analysis start must enqueue the feature-analysis stage')
  assert.equal(enqueueInput.scope, 'sentence', 'analysis start must use the standard sentence scope')
  assert.equal(enqueueInput.priority_class, 'normal', 'analysis start must use the normal background priority')
  assert.match(String(enqueueInput.run_id ?? ''), /^analysis:feature:/, 'analysis start must create a persistent run id')

  const jobsTab = corpusTabs.getByRole('tab', { name: '后台任务' })
  assert.equal(await jobsTab.getAttribute('aria-selected'), 'true', 'analysis start should take the user to the background task')
  const jobsPanel = page.getByTestId('corpus-analysis-jobs-panel')
  const jobId = enqueueCall.result?.job_id
  assert(jobId, 'analysis start must return a persistent job id')
  const createdJob = jobsPanel.getByTestId(`corpus-analysis-job-${jobId}`)
  await expectVisible(createdJob, 'newly started background analysis job')
  await expectVisible(createdJob.getByText('排队中', { exact: true }), 'newly started analysis job status')
  await expectVisible(createdJob.getByTestId('corpus-analysis-job-next-step'), 'analysis job leave-and-return guidance')
  await page.screenshot({ path: path.join(outputDir, 'app-phase16-corpus-analysis-job.png'), fullPage: true })

  await corpusTabs.getByRole('tab', { name: '分析结果' }).click()
  await corpusTabs.getByRole('tab', { name: '后台任务' }).click()
  const restoredJob = page.getByTestId(`corpus-analysis-job-${jobId}`)
  await expectVisible(restoredJob, 'analysis job restored after leaving the task view')
  await expectVisible(restoredJob.getByText('排队中', { exact: true }), 'restored analysis job status')

  await corpusTabs.getByRole('tab', { name: '分析结果' }).click()
}

async function verifyTechniqueSpecimenBudgetResumeWorkflow(page) {
 const exhausted = await page.evaluate(async () => window.novelist.invoke(
 'StartReferenceCorpusTechniqueSpecimenAnalysis',
 {
 args: [{
 novel_id: 42,
 anchor_id: 101,
 source_node_type: 'passage',
 min_observation_confidence: 0.7,
 run_id: 'technique-budget-resume-mock',
 token_budget: 24,
 resume: false,
 }],
 },
 { timeoutMs: null },
 ))

 assert.equal(exhausted.status, 'budget_exhausted')
 assert.equal(exhausted.token_budget, 24)
 assert.equal(exhausted.tokens_spent, 24)
 assert.equal(exhausted.resume_cursor, 'passage:1')
 assert.equal(exhausted.processed_nodes, 1)
 assert.equal(exhausted.completed_at, null)

 const resumed = await page.evaluate(async () => window.novelist.invoke(
 'StartReferenceCorpusTechniqueSpecimenAnalysis',
 {
 args: [{
 novel_id: 42,
 anchor_id: 101,
 source_node_type: 'passage',
 min_observation_confidence: 0.7,
 run_id: 'technique-budget-resume-mock',
 token_budget: 48,
 resume: true,
 }],
 },
 { timeoutMs: null },
 ))

 assert.equal(resumed.status, 'completed')
 assert.equal(resumed.token_budget, 48)
 assert.equal(resumed.tokens_spent, 48)
 assert.equal(resumed.resume_cursor, 'passage:2')
 assert.equal(resumed.processed_nodes, 2)
 assert.equal(resumed.specimen_count, 2)
 assert(resumed.completed_at, 'resumed technique specimen run must have a completion time')
}

async function verifyCorpusLibraryWorkflow(page) {
 await verifyTechniqueSpecimenBudgetResumeWorkflow(page)
 await clickActivity(page, '素材库')
  await expectVisible(page.getByRole('heading', { name: '语料库管理' }), 'corpus library heading')
  const corpusTabs = page.getByTestId('corpus-library-tabs')
  await expectVisible(corpusTabs, 'corpus library task tabs')
  for (const tabName of ['处理后语料', '分析结果', '素材来源', '标签校正', '风格画像', '处理记录', '高级']) {
    await expectVisible(corpusTabs.getByRole('tab', { name: tabName }), `corpus library ${tabName} tab`)
  }
  await expectVisible(page.getByTestId('reference-import-panel'), 'corpus library default source import panel')
  assert.equal(await corpusTabs.getByRole('tab', { name: '素材来源' }).getAttribute('aria-selected'), 'true', 'corpus library should default to source import tab')
  await assertCorpusLibraryLegacyWritingEntrypointsHidden(page, 'corpus library default source tab')
  await assertCorpusLibraryNoChapterWritingBridgeCalls(page, 'corpus library default source tab')
  await verifyCorpusLibraryPartialImportFailure(page)

  await verifyCorpusAnalysisStartAndReturnWorkflow(page, corpusTabs)

await verifyCorpusLibraryAnalysisResultsTab(page, corpusTabs)
 await verifyCorpusAnalysisJobsWorkflow(page, corpusTabs)

  await corpusTabs.getByRole('tab', { name: '高级' }).click()
  await expectVisible(page.getByTestId('reference-corpus-advanced-tab'), 'corpus library advanced tab')
  assert.equal(await corpusTabs.getByRole('tab', { name: '高级' }).getAttribute('aria-selected'), 'true', 'corpus library advanced tab should be selected')
  await assertCorpusLibraryLegacyWritingEntrypointsHidden(page, 'corpus library advanced tab')
  await assertCorpusLibraryNoChapterWritingBridgeCalls(page, 'corpus library advanced tab')

  const tagReviewQueueCountBeforeTab = await bridgeCallCount(page, 'GetReferenceMaterialTagReviewQueue')
  await corpusTabs.getByRole('tab', { name: '标签校正' }).click()
  await expectVisible(page.getByLabel('材料库结果').or(page.getByLabel('材料库搜索')).first(), 'corpus library material area')
  const initialTagReviewQueueResult = await waitForLatestBridgeResult(page, 'GetReferenceMaterialTagReviewQueue', tagReviewQueueCountBeforeTab)
  assert.equal(initialTagReviewQueueResult?.total, 1, 'corpus material tag review queue must be loaded from the server-owned queue')

  const corpusSearchCountBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  const tagReviewQueueCountBeforeSearch = await bridgeCallCount(page, 'GetReferenceMaterialTagReviewQueue')
  await page.getByRole('button', { name: '检索材料库' }).click()
  const corpusSearchResult = await waitForLatestBridgeResult(page, 'SearchReferenceMaterials', corpusSearchCountBefore)
  const corpusSearchJson = JSON.stringify(corpusSearchResult)
  assert(!corpusSearchJson.includes(FULL_MATERIAL_LEAK_SENTINEL), 'corpus library bridge search result must not expose full material text')
  assert(!corpusSearchJson.includes('"text"'), 'corpus library bridge search result must not include full text field')
  assert(corpusSearchJson.includes('text_preview'), 'corpus library bridge search result must include bounded text_preview')
  const tagReviewQueueResult = await waitForLatestBridgeResult(page, 'GetReferenceMaterialTagReviewQueue', tagReviewQueueCountBeforeSearch)
  const tagReviewQueueJson = JSON.stringify(tagReviewQueueResult)
  assert.equal(tagReviewQueueResult?.total, 1, 'corpus material tag review server queue must include one item before correction')
  assert.equal(tagReviewQueueResult?.items?.[0]?.material?.material_id, 'mock-mat-rain-002', 'corpus material tag review server queue must focus the queued material')
  assert(!tagReviewQueueJson.includes(FULL_MATERIAL_LEAK_SENTINEL), 'corpus material tag review queue result must not expose full material text')
  assert(!tagReviewQueueJson.includes('"text"'), 'corpus material tag review queue result must not include full text field')
  assert(tagReviewQueueJson.includes('text_preview'), 'corpus material tag review queue result must include bounded text_preview')
  for (const expectedIssue of ['unverified', 'low_confidence', 'unknown_tag']) {
    assert(
      tagReviewQueueResult.items[0].issues.some((issue) => issue.code === expectedIssue),
      `corpus material tag review server queue must include ${expectedIssue}`,
    )
  }
  const materialLibraryCard = page.getByTestId('reference-material-library-card').first()
  await expectVisible(materialLibraryCard, 'corpus library material card')
  const materialLibraryCardText = await materialLibraryCard.innerText()
  assert(!materialLibraryCardText.includes(FULL_MATERIAL_LEAK_SENTINEL), 'corpus library material card must render bounded preview only')
  assert(materialLibraryCardText.includes('预览已截断，不显示全文'), 'corpus library material card must mark bounded preview')
  const tagReviewQueue = page.getByTestId('reference-material-tag-review-queue')
  await expectVisible(tagReviewQueue, 'corpus material tag review queue')
  const tagReviewText = await tagReviewQueue.innerText()
  for (const expectedText of ['标签校正', '待校正 1 · 队列第 1 / 1 页', '服务端跨页计算', '未校正', '低置信', 'unknown 标签']) {
    assert(tagReviewText.includes(expectedText), `corpus material tag review queue must render ${expectedText}`)
  }
  const tagReviewItem = tagReviewQueue.getByTestId('reference-material-tag-review-item').first()
  await expectVisible(tagReviewItem, 'corpus material tag review queue item')
  const tagReviewItemText = await tagReviewItem.innerText()
  const tagReviewMaterialId = tagReviewItemText.match(/\bmock-mat-[\w-]+\b/)?.[0] ?? ''
  assert.equal(tagReviewMaterialId, 'mock-mat-rain-002', 'corpus material tag review queue must focus the unverified low-confidence material')
  await tagReviewQueue.getByRole('button', { name: '选择当前队列' }).click()
  await expectVisible(tagReviewQueue.getByText('已选 1 / 1'), 'corpus material tag review selected count')
  await expectVisible(page.getByText('已选 1 条材料'), 'corpus material library selected review material count')
  await page.getByLabel('材料库批量功能标签').fill('library_review_corrected')
  await page.getByLabel('材料库批量场景标签').fill('interior_reviewed')
  await page.getByLabel('材料库批量 POV 标签').fill('close_reviewed')
  const tagUpdateCountBefore = await bridgeCallCount(page, 'UpdateReferenceMaterialsTags')
  const tagReviewQueueCountBeforeUpdate = await bridgeCallCount(page, 'GetReferenceMaterialTagReviewQueue')
  await page.getByRole('button', { name: /^批量校正材料库$/ }).click()
  const tagUpdateCall = await waitForLatestBridgeCallWithResult(page, 'UpdateReferenceMaterialsTags', tagUpdateCountBefore)
  assert(tagUpdateCall, 'corpus material tag review must call UpdateReferenceMaterialsTags')
  const tagUpdateInput = tagUpdateCall.args?.[0] ?? {}
  assert(Array.isArray(tagUpdateInput.material_ids), 'corpus material tag review bulk payload must include material_ids')
  assert(tagUpdateInput.material_ids.includes(tagReviewMaterialId), 'corpus material tag review bulk payload must include queued material id')
  assert.equal(tagUpdateInput.function_tag, 'library_review_corrected', 'corpus material tag review bulk payload must include corrected function tag')
  assert.equal(tagUpdateInput.scene_tag, 'interior_reviewed', 'corpus material tag review bulk payload must include corrected scene tag')
  assert.equal(tagUpdateInput.pov_tag, 'close_reviewed', 'corpus material tag review bulk payload must include corrected POV tag')
  assert.equal(tagUpdateInput.origin, 'ui', 'corpus material tag review bulk payload must mark UI origin')
  assert.equal(tagUpdateInput.note, 'corpus material library bulk correction', 'corpus material tag review bulk payload must mark material library correction')
  const tagUpdateJson = JSON.stringify(tagUpdateCall.result)
  assert(!tagUpdateJson.includes(FULL_MATERIAL_LEAK_SENTINEL), 'corpus material tag review bridge update result must not expose full material text')
  assert(!tagUpdateJson.includes('"text"'), 'corpus material tag review bridge update result must not include full text field')
  assert(tagUpdateJson.includes('text_preview'), 'corpus material tag review bridge update result must include bounded text_preview')
  await expectVisible(page.getByText('材料库已批量校正 1 条材料标签'), 'corpus material tag review bulk update message')
  const tagReviewQueueAfterUpdate = await waitForLatestBridgeResult(page, 'GetReferenceMaterialTagReviewQueue', tagReviewQueueCountBeforeUpdate)
  assert.equal(tagReviewQueueAfterUpdate?.total, 0, 'corpus material tag review server queue must clear after correction')
  assert.equal(tagReviewQueueAfterUpdate?.items?.length, 0, 'corpus material tag review server queue must return no items after correction')
  await expectVisible(tagReviewQueue.getByText('待校正 0 · 队列第 1 / 1 页'), 'corpus material tag review queue clears after correction')
  await expectVisible(tagReviewQueue.getByText('服务端队列暂无未校正、低置信或 unknown 标签材料'), 'corpus material tag review queue empty state')
  await page.getByRole('button', { name: /查看 .* 的材料明细/ }).first().click()
  const drawer = page.getByTestId('reference-material-detail-drawer')
  await expectVisible(drawer, 'corpus material detail drawer')
  await expectVisible(drawer.getByText('来源片段'), 'corpus material detail segments')
  await expectVisible(drawer.getByText('处理记录'), 'corpus material detail processing notes')
  await expectVisible(drawer.getByText('工作区语料'), 'corpus material detail owner scope')
  await expectVisible(drawer.getByText('活跃'), 'corpus material detail archive state')
  await expectVisible(drawer.getByText(/lexical 0\.92/), 'corpus material detail score components')
  await expectVisible(drawer.getByText('预览已截断，不显示全文').first(), 'corpus material detail bounded preview marker')
  await expectVisible(drawer.getByText(/segments=3 · materials=2 · slots=2 · vectors=0/), 'corpus material detail structured counts')
  await expectVisible(drawer.getByText(/affected: 101 · mock-mat-rain-001 · mock-seg-rain-001 · object/), 'corpus material detail affected ids')
  await waitForBridgeCall(page, 'GetReferenceMaterialDetail')
  const drawerText = await drawer.innerText()
  assert(!drawerText.includes('D:\\books'), 'material detail drawer must not render local source paths')
  assert(!drawerText.includes(FULL_MATERIAL_LEAK_SENTINEL), 'material detail drawer must not render full material text')
  await drawer.getByRole('button', { name: '关闭材料明细' }).click()
  await expectHidden(drawer, 'corpus material detail drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  await expectVisible(page.getByTestId('reference-processing-records-tab'), 'corpus processing records tab')
  await page.getByTestId('reference-processing-record-detail-button').first().click()
  await waitForBridgeCall(page, 'GetReferenceSourceProcessingDetail')
  const processingDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(processingDrawer, 'corpus source processing drawer')
  const processingDrawerText = await processingDrawer.innerText()
  for (const expectedText of [
    '处理记录',
    '全局雨夜参考',
    '当前状态',
    'embedding · ready',
    'segments=3 · materials=2 · slots=1 · vectors=2',
    '处理尝试',
    '第 1 次 · embedding · ready',
    'attempt=anchor:101:attempt:1 · build=anchor:101:build:1',
    '历史事件',
    'event-1',
    'affected: 101 · mock-mat-rain-001 · mock-seg-rain-001 · object',
    '可重建语料',
    '当前无失败重试项',
    '重试加载详情只刷新本抽屉；重建语料会重新跑来源解析、切分、材料抽取和索引。',
    '重建语料',
  ]) {
    assert(processingDrawerText.includes(expectedText), `source processing drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text']) {
    assert(!processingDrawerText.includes(sensitiveText), `source processing drawer must not render ${sensitiveText}`)
  }
  await processingDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 全局雨夜参考'), null, { timeout: 12_000 })
  const copiedProcessingDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedProcessingDiagnostic.includes('current=embedding/ready'), 'source processing copied diagnostic must include current status')
  assert(copiedProcessingDiagnostic.includes('current_attempt=1 anchor:101:attempt:1 embedding/ready'), 'source processing copied diagnostic must include current attempt')
  assert(copiedProcessingDiagnostic.includes('affected=101 · mock-mat-rain-001 · mock-seg-rain-001 · object'), 'source processing copied diagnostic must include affected ids')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!copiedProcessingDiagnostic.includes(sensitiveText), `source processing copied diagnostic must not include ${sensitiveText}`)
  }
  const rebuildCountBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  const processingDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await processingDrawer.getByRole('button', { name: /重建语料 全局雨夜参考/ }).click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', rebuildCountBefore)
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', processingDetailCountBefore)

  await processingDrawer.getByRole('button', { name: /查看 mock-mat-rain-001 的材料明细/ }).click()
  await expectHidden(processingDrawer, 'source processing drawer after opening affected material detail')
  const affectedMaterialDrawer = page.getByTestId('reference-material-detail-drawer')
  await expectVisible(affectedMaterialDrawer, 'affected material detail drawer from source processing')
  await expectVisible(affectedMaterialDrawer.getByText('mock-mat-rain-001', { exact: true }), 'affected material detail id')
  await affectedMaterialDrawer.getByRole('button', { name: '关闭材料明细' }).click()
  await expectHidden(affectedMaterialDrawer, 'affected material detail drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const materialLocateProcessingCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByTestId('reference-processing-record-detail-button').first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', materialLocateProcessingCountBefore)
  const materialLocateDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(materialLocateDrawer, 'source processing drawer before affected material locate')
  const materialLocateSearchBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  await materialLocateDrawer.getByRole('button', { name: /在材料库筛选 mock-mat-rain-001/ }).click()
  await waitForBridgeCallCountAfter(page, 'SearchReferenceMaterials', materialLocateSearchBefore)
  await expectHidden(materialLocateDrawer, 'source processing drawer after affected material locate')
  assert.equal(await page.getByLabel('材料库搜索').inputValue(), 'mock-mat-rain-001', 'affected material locate must fill material library query')
  await expectVisible(page.getByTestId('reference-material-library-card').filter({ hasText: 'mock-mat-rain-001' }).first(), 'affected material located in material library')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const segmentDetailProcessingCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByTestId('reference-processing-record-detail-button').first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', segmentDetailProcessingCountBefore)
  const segmentDetailProcessingDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(segmentDetailProcessingDrawer, 'source processing drawer before affected segment detail')
  const segmentDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceSegmentDetail')
  await segmentDetailProcessingDrawer.getByRole('button', { name: /查看 mock-seg-rain-001 的来源片段明细/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceSegmentDetail', segmentDetailCountBefore)
  await expectHidden(segmentDetailProcessingDrawer, 'source processing drawer after opening affected segment detail')
  const sourceSegmentDrawer = page.getByTestId('reference-source-segment-detail-drawer')
  await expectVisible(sourceSegmentDrawer, 'affected source segment detail drawer')
  const sourceSegmentDrawerText = await sourceSegmentDrawer.innerText()
  assert(sourceSegmentDrawerText.includes('来源片段明细'), 'source segment detail drawer must render title')
  assert(sourceSegmentDrawerText.includes('mock-seg-rain-001'), 'source segment detail drawer must render segment id')
  assert(sourceSegmentDrawerText.includes('预览已截断，不显示全文'), 'source segment detail drawer must mark bounded preview')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!sourceSegmentDrawerText.includes(sensitiveText), `source segment detail drawer must not render ${sensitiveText}`)
  }
  await sourceSegmentDrawer.getByRole('button', { name: '关闭来源片段明细' }).click()
  await expectHidden(sourceSegmentDrawer, 'affected source segment detail drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const sourceLocateProcessingCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByTestId('reference-processing-record-detail-button').first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', sourceLocateProcessingCountBefore)
  const sourceLocateDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(sourceLocateDrawer, 'source processing drawer before affected source locate')
  await sourceLocateDrawer.getByRole('button', { name: /定位来源 101/ }).first().click()
  await expectHidden(sourceLocateDrawer, 'source processing drawer after affected source locate')
  assert.equal(await corpusTabs.getByRole('tab', { name: '素材来源' }).getAttribute('aria-selected'), 'true', 'affected source locate must switch to sources tab')
  assert.equal(await page.getByLabel('锚点搜索').inputValue(), '101', 'affected source locate must filter by source id')
  await expectVisible(page.getByTestId('reference-anchor-row').filter({ hasText: '全局雨夜参考' }).first(), 'affected source located in source list')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const restartRecoveryDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 重启恢复参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', restartRecoveryDetailCountBefore)
  const restartRecoveryDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(restartRecoveryDrawer, 'restart recovery source processing drawer')
  const restartRecoveryDrawerText = await restartRecoveryDrawer.innerText()
  for (const expectedText of [
    '重启恢复参考',
    'embedding · ready',
    'segments=2 · materials=1 · slots=1 · vectors=1',
    'recovered from interrupted embedding after app restart',
    '第 2 次 · embedding · ready',
    '恢复自 anchor:104:attempt:1 · anchor:104:build:1',
    '历史尝试：2 次',
    '第 1 次 · embedding · interrupted',
    'app_restart_during_embedding',
    'event-interrupted-embedding',
    'event-startup-recovered-embedding',
    'affected: 104 · mock-mat-restart-001 · mock-seg-restart-001 · object',
    '当前无失败重试项',
    '重建语料',
  ]) {
    assert(restartRecoveryDrawerText.includes(expectedText), `restart recovery source drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!restartRecoveryDrawerText.includes(sensitiveText), `restart recovery source drawer must not render ${sensitiveText}`)
  }

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await restartRecoveryDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 重启恢复参考'), null, { timeout: 12_000 })
  const copiedRestartRecoveryDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedRestartRecoveryDiagnostic.includes('current=embedding/ready'), 'restart recovery copied diagnostic must include ready current status')
  assert(copiedRestartRecoveryDiagnostic.includes('current_attempt=2 anchor:104:attempt:2 embedding/ready'), 'restart recovery copied diagnostic must include recovered attempt')
  assert(copiedRestartRecoveryDiagnostic.includes('recovered_from=anchor:104:attempt:1 build=anchor:104:build:1'), 'restart recovery copied diagnostic must include recovered-from ids')
  assert(copiedRestartRecoveryDiagnostic.includes('prior_attempt=1 anchor:104:attempt:1 embedding/interrupted'), 'restart recovery copied diagnostic must include prior interrupted attempt')
  assert(copiedRestartRecoveryDiagnostic.includes('blocked_reason=app_restart_during_embedding'), 'restart recovery copied diagnostic must include restart blocked reason')
  assert(copiedRestartRecoveryDiagnostic.includes('affected=104 · mock-mat-restart-001 · mock-seg-restart-001 · object'), 'restart recovery copied diagnostic must include affected ids')
  assert(copiedRestartRecoveryDiagnostic.includes('actions=rebuild:yes retry:no'), 'restart recovery copied diagnostic must include resolved retry availability')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!copiedRestartRecoveryDiagnostic.includes(sensitiveText), `restart recovery copied diagnostic must not include ${sensitiveText}`)
  }

  const restartRecoveryRebuildCountBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  const restartRecoveryRefreshBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await restartRecoveryDrawer.getByRole('button', { name: /重建语料 重启恢复参考/ }).click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', restartRecoveryRebuildCountBefore)
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', restartRecoveryRefreshBefore)
  await page.waitForFunction(() =>
    document.querySelector('[data-testid="reference-source-processing-drawer"]')?.innerText.includes('mock-mat-restart-001'),
  null, { timeout: 12_000 })
  const restartMaterialLocateSearchBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  await restartRecoveryDrawer.getByRole('button', { name: /在材料库筛选 mock-mat-restart-001/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'SearchReferenceMaterials', restartMaterialLocateSearchBefore)
  await expectHidden(restartRecoveryDrawer, 'restart recovery source drawer after affected material locate')
  assert.equal(await page.getByLabel('材料库搜索').inputValue(), 'mock-mat-restart-001', 'restart recovery affected material locate must fill material library query')
  assert.equal(
    await page.getByTestId('reference-material-library-card').filter({ hasText: 'mock-mat-restart-001' }).count(),
    1,
    'restart recovery rebuild must keep one searchable material row for the recovered material id',
  )

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const restartSegmentDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 重启恢复参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', restartSegmentDetailCountBefore)
  const restartSegmentDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(restartSegmentDrawer, 'restart recovery source drawer before source-segment detail')
  const restartSegmentBridgeCountBefore = await bridgeCallCount(page, 'GetReferenceSourceSegmentDetail')
  await restartSegmentDrawer.getByRole('button', { name: /查看 mock-seg-restart-001 的来源片段明细/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceSegmentDetail', restartSegmentBridgeCountBefore)
  await expectHidden(restartSegmentDrawer, 'restart recovery drawer after opening affected source segment')
  const restartSourceSegmentDrawer = page.getByTestId('reference-source-segment-detail-drawer')
  await expectVisible(restartSourceSegmentDrawer, 'restart recovery affected source segment detail drawer')
  const restartSourceSegmentText = await restartSourceSegmentDrawer.innerText()
  assert(restartSourceSegmentText.includes('重启恢复参考'), 'restart recovery source segment drawer must render source title')
  assert(restartSourceSegmentText.includes('mock-seg-restart-001'), 'restart recovery source segment drawer must render segment id')
  assert(restartSourceSegmentText.includes('预览已截断，不显示全文'), 'restart recovery source segment drawer must mark bounded preview')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!restartSourceSegmentText.includes(sensitiveText), `restart recovery source segment drawer must not render ${sensitiveText}`)
  }
  await restartSourceSegmentDrawer.getByRole('button', { name: '关闭来源片段明细' }).click()
  await expectHidden(restartSourceSegmentDrawer, 'restart recovery affected source segment drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const slotsStartupDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 槽位重启参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', slotsStartupDetailCountBefore)
  const slotsStartupDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(slotsStartupDrawer, 'slots-detected startup source processing drawer')
  const slotsStartupText = await slotsStartupDrawer.innerText()
  for (const expectedText of [
    '槽位重启参考',
    'embedding · ready',
    'segments=2 · materials=1 · slots=1 · vectors=1',
    'recovered from slots_detected startup recovery',
    '第 2 次 · embedding · ready',
    '恢复自 anchor:108:attempt:1 · anchor:108:build:1',
    '历史尝试：2 次',
    '第 1 次 · slots_detected · slots_detected',
    'app_restart_before_vector_indexing',
    'event-startup-slots-detected',
    'event-startup-slots-indexed',
    'affected: 108 · mock-mat-slots-startup-001 · mock-seg-slots-startup-001 · object',
    '当前无失败重试项',
    '重建语料',
  ]) {
    assert(slotsStartupText.includes(expectedText), `slots-detected startup drawer must render ${expectedText}`)
  }
  const slotsStartupSensitiveText = [
    'D:\\books',
    'source_path',
    'source_text',
    'prompt',
    'candidate_text',
    'chapter_text',
    'full_content',
    FULL_MATERIAL_LEAK_SENTINEL,
  ]
  for (const sensitiveText of slotsStartupSensitiveText) {
    assert(!slotsStartupText.includes(sensitiveText), `slots-detected startup drawer must not render ${sensitiveText}`)
  }

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await slotsStartupDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 槽位重启参考'), null, { timeout: 12_000 })
  const copiedSlotsStartupDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedSlotsStartupDiagnostic.includes('current=embedding/ready'), 'slots-detected startup copied diagnostic must include ready current status')
  assert(copiedSlotsStartupDiagnostic.includes('current_attempt=2 anchor:108:attempt:2 embedding/ready'), 'slots-detected startup copied diagnostic must include recovered attempt')
  assert(copiedSlotsStartupDiagnostic.includes('recovered_from=anchor:108:attempt:1 build=anchor:108:build:1'), 'slots-detected startup copied diagnostic must include recovered-from ids')
  assert(copiedSlotsStartupDiagnostic.includes('prior_attempt=1 anchor:108:attempt:1 slots_detected/slots_detected'), 'slots-detected startup copied diagnostic must include prior slots-detected attempt')
  assert(copiedSlotsStartupDiagnostic.includes('blocked_reason=app_restart_before_vector_indexing'), 'slots-detected startup copied diagnostic must include restart blocked reason')
  assert(copiedSlotsStartupDiagnostic.includes('affected=108 · mock-mat-slots-startup-001 · mock-seg-slots-startup-001 · object'), 'slots-detected startup copied diagnostic must include affected ids')
  assert(copiedSlotsStartupDiagnostic.includes('actions=rebuild:yes retry:no'), 'slots-detected startup copied diagnostic must include resolved retry availability')
  for (const sensitiveText of slotsStartupSensitiveText) {
    assert(!copiedSlotsStartupDiagnostic.includes(sensitiveText), `slots-detected startup copied diagnostic must not include ${sensitiveText}`)
  }

  const slotsStartupLocateSearchBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  await slotsStartupDrawer.getByRole('button', { name: /在材料库筛选 mock-mat-slots-startup-001/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'SearchReferenceMaterials', slotsStartupLocateSearchBefore)
  await expectHidden(slotsStartupDrawer, 'slots-detected startup drawer after affected material locate')
  assert.equal(await page.getByLabel('材料库搜索').inputValue(), 'mock-mat-slots-startup-001', 'slots-detected startup affected material locate must fill material library query')
  assert.equal(
    await page.getByTestId('reference-material-library-card').filter({ hasText: 'mock-mat-slots-startup-001' }).count(),
    1,
    'slots-detected startup recovery must keep one searchable material row for the indexed material id',
  )

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const missingSourceStartupDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 缺源重启参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', missingSourceStartupDetailCountBefore)
  const missingSourceStartupDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(missingSourceStartupDrawer, 'missing-source startup source processing drawer')
  const missingSourceStartupText = await missingSourceStartupDrawer.innerText()
  for (const expectedText of [
    '缺源重启参考',
    'failed_import · failed_import',
    'segments=2 · materials=1 · slots=1 · vectors=0',
    '启动恢复时来源不可读；保留旧材料，路径已脱敏。',
    '第 2 次 · failed_import · failed_import',
    'source_missing_after_app_restart_redacted',
    '历史尝试：2 次',
    '第 1 次 · materials_extracted · materials_extracted',
    'app_restart_before_searchable_activation',
    'event-startup-preembedding-interrupted',
    'event-startup-missing-source-failed-import',
    'affected: 107 · mock-mat-missing-startup-001 · mock-seg-missing-startup-001 · object',
    '失败状态可恢复',
    '重建语料',
  ]) {
    assert(missingSourceStartupText.includes(expectedText), `missing-source startup drawer must render ${expectedText}`)
  }
  const missingSourceStartupSensitiveText = [
    'D:\\books',
    'source_path',
    'source_text',
    'prompt',
    'candidate_text',
    'chapter_text',
    'full_content',
    FULL_MATERIAL_LEAK_SENTINEL,
  ]
  for (const sensitiveText of missingSourceStartupSensitiveText) {
    assert(!missingSourceStartupText.includes(sensitiveText), `missing-source startup drawer must not render ${sensitiveText}`)
  }

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await missingSourceStartupDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 缺源重启参考'), null, { timeout: 12_000 })
  const copiedMissingSourceStartupDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedMissingSourceStartupDiagnostic.includes('current=failed_import/failed_import'), 'missing-source startup copied diagnostic must include failed current status')
  assert(copiedMissingSourceStartupDiagnostic.includes('current_attempt=2 anchor:107:attempt:2 failed_import/failed_import'), 'missing-source startup copied diagnostic must include failed attempt')
  assert(copiedMissingSourceStartupDiagnostic.includes('prior_attempt=1 anchor:107:attempt:1 materials_extracted/materials_extracted'), 'missing-source startup copied diagnostic must include interrupted prior attempt')
  assert(copiedMissingSourceStartupDiagnostic.includes('blocked_reason=source_missing_after_app_restart_redacted'), 'missing-source startup copied diagnostic must include blocked reason')
  assert(copiedMissingSourceStartupDiagnostic.includes('affected=107 · mock-mat-missing-startup-001 · mock-seg-missing-startup-001 · object'), 'missing-source startup copied diagnostic must include retained material id')
  assert(copiedMissingSourceStartupDiagnostic.includes('actions=rebuild:yes retry:yes'), 'missing-source startup copied diagnostic must include retry/rebuild availability')
  for (const sensitiveText of missingSourceStartupSensitiveText) {
    assert(!copiedMissingSourceStartupDiagnostic.includes(sensitiveText), `missing-source startup copied diagnostic must not include ${sensitiveText}`)
  }

  const missingSourceSegmentDetailBefore = await bridgeCallCount(page, 'GetReferenceSourceSegmentDetail')
  await missingSourceStartupDrawer.getByRole('button', { name: /查看 mock-seg-missing-startup-001 的来源片段明细/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceSegmentDetail', missingSourceSegmentDetailBefore)
  await expectHidden(missingSourceStartupDrawer, 'missing-source startup drawer after opening affected source segment detail')
  const missingSourceSegmentDrawer = page.getByTestId('reference-source-segment-detail-drawer')
  await expectVisible(missingSourceSegmentDrawer, 'missing-source startup affected source segment detail drawer')
  const missingSourceSegmentText = await missingSourceSegmentDrawer.innerText()
  assert(missingSourceSegmentText.includes('缺源重启参考'), 'missing-source startup source segment drawer must render source title')
  assert(missingSourceSegmentText.includes('mock-seg-missing-startup-001'), 'missing-source startup source segment drawer must render segment id')
  assert(missingSourceSegmentText.includes('failed_import'), 'missing-source startup source segment drawer must render failed processing note')
  assert(missingSourceSegmentText.includes('启动恢复缺源；旧来源片段与材料输出已保留。'), 'missing-source startup source segment drawer must explain retained segment output')
  assert(missingSourceSegmentText.includes('预览已截断，不显示全文'), 'missing-source startup source segment drawer must mark bounded preview')
  assert(missingSourceSegmentText.includes('affected: 107 · mock-mat-missing-startup-001 · mock-seg-missing-startup-001 · object'), 'missing-source startup source segment drawer must render affected ids')
  for (const sensitiveText of missingSourceStartupSensitiveText) {
    assert(!missingSourceSegmentText.includes(sensitiveText), `missing-source startup source segment drawer must not render ${sensitiveText}`)
  }
  await missingSourceSegmentDrawer.getByRole('button', { name: '关闭来源片段明细' }).click()
  await expectHidden(missingSourceSegmentDrawer, 'missing-source startup affected source segment drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const missingSourceMaterialProcessingCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 缺源重启参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', missingSourceMaterialProcessingCountBefore)
  const missingSourceMaterialProcessingDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(missingSourceMaterialProcessingDrawer, 'missing-source startup drawer before retained material detail')
  const missingSourceMaterialDetailBefore = await bridgeCallCount(page, 'GetReferenceMaterialDetail')
  await missingSourceMaterialProcessingDrawer.getByRole('button', { name: /查看 mock-mat-missing-startup-001 的材料明细/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceMaterialDetail', missingSourceMaterialDetailBefore)
  await expectHidden(missingSourceMaterialProcessingDrawer, 'missing-source startup drawer after opening retained material detail')
  const missingSourceMaterialDrawer = page.getByTestId('reference-material-detail-drawer')
  await expectVisible(missingSourceMaterialDrawer, 'missing-source startup retained material detail drawer')
  const missingSourceMaterialText = await missingSourceMaterialDrawer.innerText()
  assert(missingSourceMaterialText.includes('mock-mat-missing-startup-001'), 'missing-source startup material detail must render material id')
  assert(missingSourceMaterialText.includes('缺源重启参考'), 'missing-source startup material detail must render source title')
  assert(missingSourceMaterialText.includes('failed_import'), 'missing-source startup material detail must render failed processing note')
  assert(missingSourceMaterialText.includes('预览已截断，不显示全文'), 'missing-source startup material detail must mark bounded preview')
  for (const sensitiveText of missingSourceStartupSensitiveText) {
    assert(!missingSourceMaterialText.includes(sensitiveText), `missing-source startup material detail must not render ${sensitiveText}`)
  }
  await missingSourceMaterialDrawer.getByRole('button', { name: '关闭材料明细' }).click()
  await expectHidden(missingSourceMaterialDrawer, 'missing-source startup retained material drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const missingSourceRecoveryDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 缺源重启参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', missingSourceRecoveryDetailCountBefore)
  const missingSourceRecoveryDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(missingSourceRecoveryDrawer, 'missing-source startup drawer before explicit rebuild')
  const missingSourceRebuildCountBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  const missingSourceRefreshBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await missingSourceRecoveryDrawer.getByRole('button', { name: /重建语料 缺源重启参考/ }).click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', missingSourceRebuildCountBefore)
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', missingSourceRefreshBefore)
  await page.waitForFunction(() =>
    document.querySelector('[data-testid="reference-source-processing-drawer"]')?.innerText.includes('恢复自 anchor:107:attempt:2 · anchor:107:build:2'),
  null, { timeout: 12_000 })
  const missingSourceRecoveryText = await missingSourceRecoveryDrawer.innerText()
  for (const expectedText of [
    '缺源重启参考',
    'embedding · ready',
    'segments=2 · materials=1 · slots=1 · vectors=1',
    'recovered from missing source startup failure',
    '第 3 次 · embedding · ready',
    '恢复自 anchor:107:attempt:2 · anchor:107:build:2',
    '历史尝试：3 次',
    '第 2 次 · failed_import · failed_import',
    'source_missing_after_app_restart_redacted',
    '第 1 次 · materials_extracted · materials_extracted',
    'event-recovered-missing-source-startup',
    'affected: 107 · mock-mat-missing-startup-001 · mock-seg-missing-startup-001 · object',
    '当前无失败重试项',
  ]) {
    assert(missingSourceRecoveryText.includes(expectedText), `recovered missing-source startup drawer must render ${expectedText}`)
  }
  for (const sensitiveText of missingSourceStartupSensitiveText) {
    assert(!missingSourceRecoveryText.includes(sensitiveText), `recovered missing-source startup drawer must not render ${sensitiveText}`)
  }

  const missingSourceLocateSearchBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  await missingSourceRecoveryDrawer.getByRole('button', { name: /在材料库筛选 mock-mat-missing-startup-001/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'SearchReferenceMaterials', missingSourceLocateSearchBefore)
  await expectHidden(missingSourceRecoveryDrawer, 'recovered missing-source startup drawer after affected material locate')
  assert.equal(await page.getByLabel('材料库搜索').inputValue(), 'mock-mat-missing-startup-001', 'recovered missing-source startup affected material locate must fill material library query')
  assert.equal(
    await page.getByTestId('reference-material-library-card').filter({ hasText: 'mock-mat-missing-startup-001' }).count(),
    1,
    'recovered missing-source startup rebuild must keep exactly one searchable material row for the retained material id',
  )

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const extractionFailureDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 抽取失败参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', extractionFailureDetailCountBefore)
  const extractionFailureDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(extractionFailureDrawer, 'failed extraction source processing drawer')
  const extractionFailureDrawerText = await extractionFailureDrawer.innerText()
  for (const expectedText of [
    '抽取失败参考',
    'extracting_materials · failed_extraction',
    'segments=2 · materials=0 · slots=0 · vectors=0',
    '第 1 次 · extracting_materials · failed_extraction',
    'attempt=anchor:105:attempt:1 · build=anchor:105:build:1',
    'extractor_output_empty_redacted',
    '材料抽取未产生可用输出；来源片段可检查，正文已脱敏。',
    'event-failed-extraction-current',
    'affected: 105 · mock-seg-extract-001',
    '失败状态可恢复',
    '重建语料',
  ]) {
    assert(extractionFailureDrawerText.includes(expectedText), `failed extraction drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!extractionFailureDrawerText.includes(sensitiveText), `failed extraction drawer must not render ${sensitiveText}`)
  }

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await extractionFailureDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 抽取失败参考'), null, { timeout: 12_000 })
  const copiedExtractionFailureDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedExtractionFailureDiagnostic.includes('current=extracting_materials/failed_extraction'), 'failed extraction copied diagnostic must include current status')
  assert(copiedExtractionFailureDiagnostic.includes('current_attempt=1 anchor:105:attempt:1 extracting_materials/failed_extraction'), 'failed extraction copied diagnostic must include failed attempt')
  assert(copiedExtractionFailureDiagnostic.includes('blocked_reason=extractor_output_empty_redacted'), 'failed extraction copied diagnostic must include blocked reason')
  assert(copiedExtractionFailureDiagnostic.includes('affected=105 · mock-seg-extract-001'), 'failed extraction copied diagnostic must include affected source segment')
  assert(copiedExtractionFailureDiagnostic.includes('actions=rebuild:yes retry:yes'), 'failed extraction copied diagnostic must include retry/rebuild availability')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!copiedExtractionFailureDiagnostic.includes(sensitiveText), `failed extraction copied diagnostic must not include ${sensitiveText}`)
  }

  const failedExtractionSegmentCountBefore = await bridgeCallCount(page, 'GetReferenceSourceSegmentDetail')
  await extractionFailureDrawer.getByRole('button', { name: /查看 mock-seg-extract-001 的来源片段明细/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceSegmentDetail', failedExtractionSegmentCountBefore)
  await expectHidden(extractionFailureDrawer, 'failed extraction drawer after opening source segment detail')
  const failedExtractionSegmentDrawer = page.getByTestId('reference-source-segment-detail-drawer')
  await expectVisible(failedExtractionSegmentDrawer, 'failed extraction source segment detail drawer')
  const failedExtractionSegmentText = await failedExtractionSegmentDrawer.innerText()
  assert(failedExtractionSegmentText.includes('抽取失败参考'), 'failed extraction source segment drawer must render source title')
  assert(failedExtractionSegmentText.includes('mock-seg-extract-001'), 'failed extraction source segment drawer must render segment id')
  assert(failedExtractionSegmentText.includes('failed_extraction'), 'failed extraction source segment drawer must render failed processing note')
  assert(failedExtractionSegmentText.includes('预览已截断，不显示全文'), 'failed extraction source segment drawer must mark bounded preview')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!failedExtractionSegmentText.includes(sensitiveText), `failed extraction source segment drawer must not render ${sensitiveText}`)
  }
  await failedExtractionSegmentDrawer.getByRole('button', { name: '关闭来源片段明细' }).click()
  await expectHidden(failedExtractionSegmentDrawer, 'failed extraction source segment drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const extractionRecoveryDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 抽取失败参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', extractionRecoveryDetailCountBefore)
  const extractionRecoveryDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(extractionRecoveryDrawer, 'failed extraction drawer before explicit rebuild')
  const extractionRebuildCountBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  const extractionRecoveryRefreshBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await extractionRecoveryDrawer.getByRole('button', { name: /重建语料 抽取失败参考/ }).click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', extractionRebuildCountBefore)
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', extractionRecoveryRefreshBefore)
  await page.waitForFunction(() =>
    document.querySelector('[data-testid="reference-source-processing-drawer"]')?.innerText.includes('恢复自 anchor:105:attempt:1 · anchor:105:build:1'),
  null, { timeout: 12_000 })
  const extractionRecoveryText = await extractionRecoveryDrawer.innerText()
  for (const expectedText of [
    '抽取失败参考',
    'embedding · ready',
    'segments=2 · materials=1 · slots=1 · vectors=1',
    'recovered from failed_extraction',
    '第 2 次 · embedding · ready',
    '恢复自 anchor:105:attempt:1 · anchor:105:build:1',
    '历史尝试：2 次',
    '第 1 次 · extracting_materials · failed_extraction',
    'event-recovered-extraction',
    'affected: 105 · mock-mat-extract-001 · mock-seg-extract-001 · object',
    '当前无失败重试项',
  ]) {
    assert(extractionRecoveryText.includes(expectedText), `recovered extraction drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!extractionRecoveryText.includes(sensitiveText), `recovered extraction drawer must not render ${sensitiveText}`)
  }

  const extractionMaterialLocateSearchBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  await extractionRecoveryDrawer.getByRole('button', { name: /在材料库筛选 mock-mat-extract-001/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'SearchReferenceMaterials', extractionMaterialLocateSearchBefore)
  await expectHidden(extractionRecoveryDrawer, 'recovered extraction drawer after affected material locate')
  assert.equal(await page.getByLabel('材料库搜索').inputValue(), 'mock-mat-extract-001', 'recovered extraction affected material locate must fill material library query')
  assert.equal(
    await page.getByTestId('reference-material-library-card').filter({ hasText: 'mock-mat-extract-001' }).count(),
    1,
    'recovered extraction rebuild must create exactly one searchable material row for the recovered material id',
  )

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const slottingFailureDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 槽位失败参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', slottingFailureDetailCountBefore)
  const slottingFailureDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(slottingFailureDrawer, 'failed slotting source processing drawer')
  const slottingFailureDrawerText = await slottingFailureDrawer.innerText()
  for (const expectedText of [
    '槽位失败参考',
    'detecting_slots · failed_slotting',
    'segments=2 · materials=1 · slots=0 · vectors=0',
    '第 1 次 · detecting_slots · failed_slotting',
    'slot_detection_failed_redacted',
    '槽位检测失败；已生成材料保留，可查看材料明细。',
    'event-failed-slotting-current',
    'affected: 106 · mock-mat-slot-001 · mock-seg-slot-001',
    '失败状态可恢复',
    '重建语料',
  ]) {
    assert(slottingFailureDrawerText.includes(expectedText), `failed slotting drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!slottingFailureDrawerText.includes(sensitiveText), `failed slotting drawer must not render ${sensitiveText}`)
  }

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await slottingFailureDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 槽位失败参考'), null, { timeout: 12_000 })
  const copiedSlottingFailureDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedSlottingFailureDiagnostic.includes('current=detecting_slots/failed_slotting'), 'failed slotting copied diagnostic must include current status')
  assert(copiedSlottingFailureDiagnostic.includes('current_attempt=1 anchor:106:attempt:1 detecting_slots/failed_slotting'), 'failed slotting copied diagnostic must include failed attempt')
  assert(copiedSlottingFailureDiagnostic.includes('blocked_reason=slot_detection_failed_redacted'), 'failed slotting copied diagnostic must include blocked reason')
  assert(copiedSlottingFailureDiagnostic.includes('affected=106 · mock-mat-slot-001 · mock-seg-slot-001'), 'failed slotting copied diagnostic must include retained material id')
  assert(copiedSlottingFailureDiagnostic.includes('actions=rebuild:yes retry:yes'), 'failed slotting copied diagnostic must include retry/rebuild availability')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!copiedSlottingFailureDiagnostic.includes(sensitiveText), `failed slotting copied diagnostic must not include ${sensitiveText}`)
  }

  const failedSlottingMaterialDetailBefore = await bridgeCallCount(page, 'GetReferenceMaterialDetail')
  await slottingFailureDrawer.getByRole('button', { name: /查看 mock-mat-slot-001 的材料明细/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceMaterialDetail', failedSlottingMaterialDetailBefore)
  await expectHidden(slottingFailureDrawer, 'failed slotting drawer after opening retained material detail')
  const failedSlottingMaterialDrawer = page.getByTestId('reference-material-detail-drawer')
  await expectVisible(failedSlottingMaterialDrawer, 'failed slotting retained material detail drawer')
  const failedSlottingMaterialText = await failedSlottingMaterialDrawer.innerText()
  assert(failedSlottingMaterialText.includes('mock-mat-slot-001'), 'failed slotting material detail must render material id')
  assert(failedSlottingMaterialText.includes('槽位失败参考'), 'failed slotting material detail must render source title')
  assert(failedSlottingMaterialText.includes('failed_slotting'), 'failed slotting material detail must render failed processing note')
  assert(failedSlottingMaterialText.includes('预览已截断，不显示全文'), 'failed slotting material detail must mark bounded preview')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!failedSlottingMaterialText.includes(sensitiveText), `failed slotting material detail must not render ${sensitiveText}`)
  }
  await failedSlottingMaterialDrawer.getByRole('button', { name: '关闭材料明细' }).click()
  await expectHidden(failedSlottingMaterialDrawer, 'failed slotting retained material drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const slottingRecoveryDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 槽位失败参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', slottingRecoveryDetailCountBefore)
  const slottingRecoveryDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(slottingRecoveryDrawer, 'failed slotting drawer before explicit rebuild')
  const slottingRebuildCountBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  const slottingRecoveryRefreshBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await slottingRecoveryDrawer.getByRole('button', { name: /重建语料 槽位失败参考/ }).click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', slottingRebuildCountBefore)
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', slottingRecoveryRefreshBefore)
  await page.waitForFunction(() =>
    document.querySelector('[data-testid="reference-source-processing-drawer"]')?.innerText.includes('恢复自 anchor:106:attempt:1 · anchor:106:build:1'),
  null, { timeout: 12_000 })
  const slottingRecoveryText = await slottingRecoveryDrawer.innerText()
  for (const expectedText of [
    '槽位失败参考',
    'embedding · ready',
    'segments=2 · materials=1 · slots=1 · vectors=1',
    'recovered from failed_slotting',
    '第 2 次 · embedding · ready',
    '恢复自 anchor:106:attempt:1 · anchor:106:build:1',
    '历史尝试：2 次',
    '第 1 次 · detecting_slots · failed_slotting',
    'event-recovered-slotting',
    'affected: 106 · mock-mat-slot-001 · mock-seg-slot-001 · object',
    '当前无失败重试项',
  ]) {
    assert(slottingRecoveryText.includes(expectedText), `recovered slotting drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!slottingRecoveryText.includes(sensitiveText), `recovered slotting drawer must not render ${sensitiveText}`)
  }

  const slottingMaterialLocateSearchBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  await slottingRecoveryDrawer.getByRole('button', { name: /在材料库筛选 mock-mat-slot-001/ }).first().click()
  await waitForBridgeCallCountAfter(page, 'SearchReferenceMaterials', slottingMaterialLocateSearchBefore)
  await expectHidden(slottingRecoveryDrawer, 'recovered slotting drawer after affected material locate')
  assert.equal(await page.getByLabel('材料库搜索').inputValue(), 'mock-mat-slot-001', 'recovered slotting affected material locate must fill material library query')
  assert.equal(
    await page.getByTestId('reference-material-library-card').filter({ hasText: 'mock-mat-slot-001' }).count(),
    1,
    'recovered slotting rebuild must keep exactly one searchable material row for the retained material id',
  )

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const failedProcessingDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 失败导入参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', failedProcessingDetailCountBefore)
  const failedProcessingDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(failedProcessingDrawer, 'failed source processing drawer')
  const failedProcessingDrawerText = await failedProcessingDrawer.innerText()
  for (const expectedText of [
    '失败导入参考',
    'failed_import · failed_import',
    'segments=0 · materials=0 · slots=0 · vectors=0',
    '第 1 次 · failed_import · failed_import',
    'attempt=anchor:102:attempt:1 · build=anchor:102:build:1',
    'source_unavailable_redacted',
    '失败状态可恢复',
    '无法读取来源；本地路径已脱敏。',
    'event-failed-import',
    'affected: 102',
    '重建语料',
  ]) {
    assert(failedProcessingDrawerText.includes(expectedText), `failed source processing drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!failedProcessingDrawerText.includes(sensitiveText), `failed source processing drawer must not render ${sensitiveText}`)
  }

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await failedProcessingDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 失败导入参考'), null, { timeout: 12_000 })
  const copiedFailedProcessingDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedFailedProcessingDiagnostic.includes('current=failed_import/failed_import'), 'failed source copied diagnostic must include failed current status')
  assert(copiedFailedProcessingDiagnostic.includes('current_attempt=1 anchor:102:attempt:1 failed_import/failed_import'), 'failed source copied diagnostic must include failed attempt')
  assert(copiedFailedProcessingDiagnostic.includes('blocked_reason=source_unavailable_redacted'), 'failed source copied diagnostic must include blocked reason')
  assert(copiedFailedProcessingDiagnostic.includes('actions=rebuild:yes retry:yes'), 'failed source copied diagnostic must include retry/rebuild availability')
  assert(copiedFailedProcessingDiagnostic.includes('affected=102'), 'failed source copied diagnostic must include affected source id')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!copiedFailedProcessingDiagnostic.includes(sensitiveText), `failed source copied diagnostic must not include ${sensitiveText}`)
  }

  const failedRebuildCountBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  const failedProcessingDetailRefreshBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await failedProcessingDrawer.getByRole('button', { name: /重建语料 失败导入参考/ }).click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', failedRebuildCountBefore)
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', failedProcessingDetailRefreshBefore)
  await page.waitForFunction(() =>
    document.querySelector('[data-testid="reference-source-processing-drawer"]')?.innerText.includes('恢复自 anchor:102:attempt:1 · anchor:102:build:1'),
  null, { timeout: 12_000 })
  const recoveredProcessingDrawerText = await failedProcessingDrawer.innerText()
  for (const expectedText of [
    '失败导入参考',
    'embedding · ready',
    'segments=3 · materials=2 · slots=1 · vectors=2',
    'recovered from failed_import',
    '第 2 次 · embedding · ready',
    '恢复自 anchor:102:attempt:1 · anchor:102:build:1',
    '历史尝试：2 次',
    '第 1 次 · failed_import · failed_import',
    '当前无失败重试项',
    'event-recovered-import',
    'affected: 102 · mock-mat-rain-001 · mock-seg-rain-001 · object',
  ]) {
    assert(recoveredProcessingDrawerText.includes(expectedText), `recovered source processing drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!recoveredProcessingDrawerText.includes(sensitiveText), `recovered source processing drawer must not render ${sensitiveText}`)
  }

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await failedProcessingDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 失败导入参考'), null, { timeout: 12_000 })
  const copiedRecoveredProcessingDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedRecoveredProcessingDiagnostic.includes('current=embedding/ready'), 'recovered source copied diagnostic must include ready current status')
  assert(copiedRecoveredProcessingDiagnostic.includes('current_attempt=2 anchor:102:attempt:2 embedding/ready'), 'recovered source copied diagnostic must include recovered attempt')
  assert(copiedRecoveredProcessingDiagnostic.includes('recovered_from=anchor:102:attempt:1 build=anchor:102:build:1'), 'recovered source copied diagnostic must include recovered-from ids')
  assert(copiedRecoveredProcessingDiagnostic.includes('prior_attempt=1 anchor:102:attempt:1 failed_import/failed_import'), 'recovered source copied diagnostic must include prior failed attempt')
  assert(copiedRecoveredProcessingDiagnostic.includes('actions=rebuild:yes retry:no'), 'recovered source copied diagnostic must include resolved retry availability')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!copiedRecoveredProcessingDiagnostic.includes(sensitiveText), `recovered source copied diagnostic must not include ${sensitiveText}`)
  }
  await failedProcessingDrawer.getByRole('button', { name: '关闭处理记录' }).click()
  await expectHidden(failedProcessingDrawer, 'failed source processing drawer after close')

  await corpusTabs.getByRole('tab', { name: '处理记录' }).click()
  const retryFailureDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await page.getByRole('button', { name: /查看 重试仍失败参考 的处理详情与失败记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', retryFailureDetailCountBefore)
  const retryFailureDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(retryFailureDrawer, 'retry-failure source processing drawer')
  const retryFailureInitialText = await retryFailureDrawer.innerText()
  for (const expectedText of [
    '重试仍失败参考',
    'failed_import · failed_import',
    '第 1 次 · failed_import · failed_import',
    'source_unavailable_redacted',
    '失败状态可恢复',
    '重建语料',
  ]) {
    assert(retryFailureInitialText.includes(expectedText), `retry-failure source drawer must render initial ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!retryFailureInitialText.includes(sensitiveText), `retry-failure initial drawer must not render ${sensitiveText}`)
  }

  const retryFailureRebuildCountBefore = await bridgeCallCount(page, 'RebuildReferenceAnchor')
  const retryFailureRefreshBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await retryFailureDrawer.getByRole('button', { name: /重建语料 重试仍失败参考/ }).click()
  await waitForBridgeCallCountAfter(page, 'RebuildReferenceAnchor', retryFailureRebuildCountBefore)
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', retryFailureRefreshBefore)
  await page.waitForFunction(() =>
    document.querySelector('[data-testid="reference-source-processing-drawer"]')?.innerText.includes('第 2 次 · failed_import · failed_import'),
  null, { timeout: 12_000 })
  const retryFailureAfterText = await retryFailureDrawer.innerText()
  for (const expectedText of [
    '重试仍失败参考',
    '重试仍失败；本地路径已脱敏。',
    '第 2 次 · failed_import · failed_import',
    'attempt=anchor:103:attempt:2 · build=anchor:103:build:2',
    'source_unavailable_after_retry_redacted',
    '历史尝试：2 次',
    '第 1 次 · failed_import · failed_import',
    'event-retry-failed-import',
    'affected: 103',
    '失败状态可恢复',
  ]) {
    assert(retryFailureAfterText.includes(expectedText), `retry-failure source drawer must render retry failure ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!retryFailureAfterText.includes(sensitiveText), `retry-failure drawer after retry must not render ${sensitiveText}`)
  }

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await retryFailureDrawer.getByTestId('reference-source-processing-copy-diagnostic').click()
  await page.waitForFunction(() => window.__appMockClipboardText?.includes('处理记录: 重试仍失败参考'), null, { timeout: 12_000 })
  const copiedRetryFailureDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(copiedRetryFailureDiagnostic.includes('current=failed_import/failed_import'), 'retry-failure copied diagnostic must include failed current status')
  assert(copiedRetryFailureDiagnostic.includes('current_attempt=2 anchor:103:attempt:2 failed_import/failed_import'), 'retry-failure copied diagnostic must include current retry attempt')
  assert(copiedRetryFailureDiagnostic.includes('prior_attempt=1 anchor:103:attempt:1 failed_import/failed_import'), 'retry-failure copied diagnostic must include prior failed attempt')
  assert(copiedRetryFailureDiagnostic.includes('blocked_reason=source_unavailable_after_retry_redacted'), 'retry-failure copied diagnostic must include updated blocked reason')
  assert(copiedRetryFailureDiagnostic.includes('actions=rebuild:yes retry:yes'), 'retry-failure copied diagnostic must keep retry availability')
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!copiedRetryFailureDiagnostic.includes(sensitiveText), `retry-failure copied diagnostic must not include ${sensitiveText}`)
  }
  await retryFailureDrawer.getByRole('button', { name: '关闭处理记录' }).click()
  await expectHidden(retryFailureDrawer, 'retry-failure source processing drawer after close')

  await assertCorpusLibraryLegacyWritingEntrypointsHidden(page, 'corpus library workflow completion')
  await assertCorpusLibraryNoChapterWritingBridgeCalls(page, 'corpus library workflow completion')
}

async function verifyCorpusLibraryPartialImportFailure(page) {
  const importPanel = page.getByTestId('reference-import-panel')
  const partialImportTitle = '部分失败导入'
  const partialImportPaths = 'D:\\books\\partial-success.md\nD:\\books\\mock-partial-fail.md'
  const createCountBefore = await bridgeCallCount(page, 'CreateReferenceAnchorsWithResult')

  await importPanel.getByPlaceholder('参考书名').fill(partialImportTitle)
  await importPanel.getByPlaceholder('可选').fill('Partial Import Author')
  await importPanel.getByLabel('可见性').selectOption('workspace')
  await importPanel.getByLabel('来源可信度').selectOption('imported')
  await importPanel.getByLabel('用户标签').fill('partial;failure')
  await importPanel.getByLabel('批量路径').fill(partialImportPaths)
  await importPanel.getByRole('button', { name: /^批量导入$/ }).click()

  const createCall = await waitForLatestBridgeCallWithResult(page, 'CreateReferenceAnchorsWithResult', createCountBefore)
  assert.equal(createCall.result?.total_count, 2, 'partial corpus import must report both attempted sources')
  assert.equal(createCall.result?.succeeded_count, 1, 'partial corpus import must keep the successful source')
  assert.equal(createCall.result?.failed_count, 1, 'partial corpus import must report one failed source')
  assert(!JSON.stringify(createCall.result?.failed ?? []).includes('D:\\books'), 'partial corpus import failure result must not expose local source paths')
  const firstPartialImportAnchorId = createCall.result?.succeeded?.[0]?.anchor_id
  assert(Number.isInteger(firstPartialImportAnchorId), 'partial corpus import must return a stable successful anchor id')

  await expectVisible(page.getByText('已批量导入 1/2 个语料来源'), 'partial corpus import success count message')
  await expectVisible(
    page.getByTestId('reference-anchor-row').filter({ hasText: '部分失败导入 1' }).first(),
    'partial corpus import successful source row',
  )
  const partialFailedSourceRow = page.getByTestId('reference-anchor-row').filter({ hasText: '部分失败导入 2' })
  await expectVisible(partialFailedSourceRow.first(), 'partial corpus import failed source row')
  const partialFailedSourceRowText = await partialFailedSourceRow.first().innerText()
  assert(partialFailedSourceRowText.includes('failed_import'), 'partial corpus import failed source row must persist failed_import status')
  assert(!partialFailedSourceRowText.includes('D:\\books'), 'partial corpus import failed source row must not expose local source paths')
  assert.equal(await importPanel.getByPlaceholder('参考书名').inputValue(), partialImportTitle, 'partial corpus import must keep title input')
  assert.equal(await importPanel.getByPlaceholder('可选').inputValue(), 'Partial Import Author', 'partial corpus import must keep author input')
  assert.equal(await importPanel.getByLabel('用户标签').inputValue(), 'partial;failure', 'partial corpus import must keep tag input')
  assert.equal(await importPanel.getByLabel('批量路径').inputValue(), partialImportPaths, 'partial corpus import must keep bulk path input')

  const partialAlert = errorAlert(page, '部分语料导入失败')
  await expectVisible(partialAlert, 'partial corpus import failure callout')
  await expectVisible(partialAlert.getByText('第 2 项「部分失败导入 2」'), 'partial corpus import failed item preview')
  await expectVisible(partialAlert.getByText('模拟语料解析失败；本地路径已隐藏。'), 'partial corpus import sanitized diagnostic')
  await expectVisible(partialAlert.getByRole('button', { name: '复制错误诊断' }), 'partial corpus import copy diagnostic button')
  const partialAlertText = await partialAlert.innerText()
  assert(!partialAlertText.includes('D:\\books'), 'partial corpus import visible diagnostics must not expose local source paths')

  await page.evaluate(() => { window.__appMockClipboardText = '' })
  await partialAlert.getByRole('button', { name: '复制错误诊断' }).click()
  await page.waitForFunction(() => typeof window.__appMockClipboardText === 'string' && window.__appMockClipboardText.length > 0)
  const copiedDiagnostic = await page.evaluate(() => window.__appMockClipboardText)
  assert(!copiedDiagnostic.includes('D:\\books'), 'partial corpus import copied diagnostics must not expose local source paths')
  const parsedDiagnostic = JSON.parse(copiedDiagnostic)
  assert.equal(parsedDiagnostic.bridge_method, 'CreateReferenceAnchorsWithResult', 'partial corpus import diagnostic must name the bridge method')
  const diagnosticDetail = JSON.parse(parsedDiagnostic.detail)
  assert.equal(diagnosticDetail.succeeded_count, 1, 'partial corpus import diagnostic must include succeeded count')
  assert.equal(diagnosticDetail.failed_count, 1, 'partial corpus import diagnostic must include failed count')

  const duplicateCreateCountBefore = await bridgeCallCount(page, 'CreateReferenceAnchorsWithResult')
  await importPanel.getByRole('button', { name: /^批量导入$/ }).click()
  const duplicateCreateCall = await waitForLatestBridgeCallWithResult(page, 'CreateReferenceAnchorsWithResult', duplicateCreateCountBefore)
  assert.equal(duplicateCreateCall.result?.total_count, 2, 'duplicate partial corpus import must report both attempted sources')
  assert.equal(duplicateCreateCall.result?.succeeded_count, 1, 'duplicate partial corpus import must report the reusable successful source')
  assert.equal(duplicateCreateCall.result?.failed_count, 1, 'duplicate partial corpus import must keep reporting the failed source')
  assert.equal(duplicateCreateCall.result?.succeeded?.[0]?.anchor_id, firstPartialImportAnchorId, 'duplicate corpus import must reuse the existing anchor id')
  assert(!JSON.stringify(duplicateCreateCall.result).includes('D:\\books'), 'duplicate corpus import result must not expose local source paths')
  assert.equal(
    await page.getByTestId('reference-anchor-row').filter({ hasText: '部分失败导入 1' }).count(),
    1,
    'duplicate corpus import must not render a second source row for the same stable source identity',
  )
  assert.equal(
    await page.getByTestId('reference-anchor-row').filter({ hasText: '部分失败导入 2' }).count(),
    1,
    'duplicate corpus import must not render a second failed source row for the same terminal failed identity',
  )

  const partialFailureDetailCountBefore = await bridgeCallCount(page, 'GetReferenceSourceProcessingDetail')
  await partialFailedSourceRow.first().getByRole('button', { name: /查看 部分失败导入 2 的处理记录/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceSourceProcessingDetail', partialFailureDetailCountBefore)
  const partialFailureProcessingDrawer = page.getByTestId('reference-source-processing-drawer')
  await expectVisible(partialFailureProcessingDrawer, 'partial failed source processing drawer')
  const partialFailureProcessingText = await partialFailureProcessingDrawer.innerText()
  for (const expectedText of [
    '部分失败导入 2',
    'failed_import · failed_import',
    'segments=0 · materials=0 · slots=0 · vectors=0',
    '失败状态可恢复',
    'source_unavailable_redacted',
  ]) {
    assert(partialFailureProcessingText.includes(expectedText), `partial failed source processing drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!partialFailureProcessingText.includes(sensitiveText), `partial failed source processing drawer must not render ${sensitiveText}`)
  }
  await partialFailureProcessingDrawer.getByRole('button', { name: '关闭处理记录' }).click()
  await expectHidden(partialFailureProcessingDrawer, 'partial failed source processing drawer after close')
}

async function verifyChapterReferenceWorkflow(page) {
  await clickActivity(page, '章节')
  await ensureChapterBlockExpanded(page)
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.locator('.monaco-editor').first(), 'chapter editor')
  await waitForBridgeCallArg(page, 'GetContent', 1, 'chapters/1.md')

  const chapterSearchCountBefore = await bridgeCallCount(page, 'SearchReferenceMaterials')
  const blueprintSessionLoadCountBefore = await bridgeCallCount(page, 'GetReferenceCorpusBlueprintSession')
  await page.getByRole('button', { name: /参考素材/ }).click()
  const drawer = page.getByTestId('chapter-reference-panel')
  await expectVisible(drawer, 'chapter reference drawer')
  await waitForBridgeCallCountAfter(page, 'GetReferenceCorpusBlueprintSession', blueprintSessionLoadCountBefore)
  await page.waitForFunction(
    () => document.activeElement?.getAttribute('placeholder') === '可留空，系统会先按章节标题和可访问素材推荐',
    null,
    { timeout: 12_000 },
  )
  await expectVisible(drawer.getByRole('heading', { name: '语料驱动草稿' }), 'corpus insertion draft heading')
  await expectHidden(drawer.getByRole('heading', { name: '推荐素材' }), 'advanced recommendation heading before expand')
  await expectHidden(drawer.getByRole('button', { name: '启动参考流程' }), 'advanced strict flow start before expand')
  assert(!((await drawer.innerText()).includes('剧本')), 'automatic chapter path must describe the intermediate artifact as a blueprint, not a script')
  assert.equal(await bridgeCallCount(page, 'SearchReferenceMaterials'), chapterSearchCountBefore, 'chapter reference default path must not run material recommendation before advanced expansion')

  const blueprintAdvanceCountBefore = await bridgeCallCount(page, 'AdvanceReferenceCorpusBlueprintSession')
  await drawer.getByTestId('chapter-corpus-blueprint-generate-button').click()
  const firstBlueprintSessionCall = await waitForLatestBridgeCallWithResult(page, 'AdvanceReferenceCorpusBlueprintSession', blueprintAdvanceCountBefore)
  assert.equal(firstBlueprintSessionCall.args?.[0]?.action, 'generate', 'chapter corpus blueprint primary action must create a persisted session')
  assert.equal(firstBlueprintSessionCall.args?.[0]?.generation_input?.chapter_context?.chapter_number, 1, 'chapter corpus blueprint session must bind the active chapter')
  const firstBlueprintSession = firstBlueprintSessionCall.result
  const firstBlueprintCandidates = firstBlueprintSession?.candidates
  assert(firstBlueprintCandidates?.candidates?.length >= 2, 'chapter corpus blueprint candidate mock must return at least two candidates')
  const blueprintCandidates = drawer.getByTestId('chapter-corpus-blueprint-candidates')
  await expectVisible(blueprintCandidates, 'chapter corpus blueprint candidates')
  await expectHidden(blueprintCandidates.getByTestId('chapter-corpus-blueprint-iteration'), 'automatic blueprint iteration internals')
  await expectHidden(blueprintCandidates.getByTestId('chapter-corpus-blueprint-difference-audit').first(), 'automatic blueprint difference audit internals')
  await expectVisible(blueprintCandidates.getByTestId('chapter-corpus-blueprint-emotion-arc').first(), 'chapter corpus blueprint emotion arc')
  const automaticBlueprintText = await blueprintCandidates.innerText()
  assert(!automaticBlueprintText.includes('mock-corpus-blueprint-001'), 'automatic blueprint path must not expose blueprint identifiers')
  assert(!automaticBlueprintText.includes('mock-node-'), 'automatic blueprint path must not expose node identifiers')
  await expectVisible(blueprintCandidates.locator('[data-testid="chapter-corpus-blueprint-candidate-select"]').first(), 'chapter corpus blueprint candidate select')
  const firstBlueprintSelectionCountBefore = await bridgeCallCount(page, 'AdvanceReferenceCorpusBlueprintSession')
  await blueprintCandidates.locator('[data-testid="chapter-corpus-blueprint-candidate-select"]').first().click()
  const firstBlueprintSelectionCall = await waitForLatestBridgeCallWithResult(page, 'AdvanceReferenceCorpusBlueprintSession', firstBlueprintSelectionCountBefore)
  assert.equal(firstBlueprintSelectionCall.args?.[0]?.action, 'select', 'choosing an automatic blueprint must persist selection server-side')
  const firstSelectedBlueprintId = firstBlueprintSelectionCall.args?.[0]?.selected_blueprint_id
  assert(firstSelectedBlueprintId, 'blueprint selection must include the chosen blueprint id')

  const restoredBlueprintSessionCountBefore = await bridgeCallCount(page, 'GetReferenceCorpusBlueprintSession')
  await drawer.getByRole('button', { name: '关闭参考素材面板' }).click()
  await expectHidden(drawer, 'chapter reference drawer after close before blueprint recovery')
  await page.waitForFunction(
    () => document.activeElement?.getAttribute('title') === '打开章节参考素材',
    null,
    { timeout: 12_000 },
  )
  await page.evaluate(() => window.sessionStorage.setItem(
    'novelist:corpus-writing:42:1:chapters/1.md',
    JSON.stringify({
      goal: '浏览器旧目标不应覆盖服务端会话',
      writingMode: 'auto',
      selectedDraftId: '',
    }),
  ))
  await page.getByRole('button', { name: /参考素材/ }).click()
  await expectVisible(drawer, 'chapter reference drawer after reopen for blueprint recovery')
  await waitForBridgeCallCountAfter(page, 'GetReferenceCorpusBlueprintSession', restoredBlueprintSessionCountBefore)
  await expectVisible(drawer.getByText('已从服务端恢复第 1 轮蓝图。'), 'persisted blueprint session recovery message')
  await expectVisible(drawer.getByTestId('chapter-corpus-blueprint-selected'), 'persisted selected blueprint after reopen')
  assert.equal(await drawer.getByTestId('chapter-corpus-blueprint-selected').innerText(), '已选此方案，可以继续生成正文候选。', 'chapter reference reopen must recover the selected blueprint without sessionStorage')
  assert.equal(
    await drawer.getByPlaceholder('可留空，系统会先按章节标题和可访问素材推荐').inputValue(),
    '第1章 雨夜线索',
    'server-restored blueprint goal must win over stale sessionStorage cache',
  )

  const secondBlueprintAdvanceCountBefore = await bridgeCallCount(page, 'AdvanceReferenceCorpusBlueprintSession')
  await drawer.getByTestId('chapter-corpus-blueprint-feedback-button').click()
  const secondBlueprintSessionCall = await waitForLatestBridgeCallWithResult(page, 'AdvanceReferenceCorpusBlueprintSession', secondBlueprintAdvanceCountBefore)
  const secondBlueprintSession = secondBlueprintSessionCall.result
  const secondBlueprintCandidates = secondBlueprintSession?.candidates
  assert.equal(secondBlueprintSessionCall.args?.[0]?.action, 'revise', 'choosing another automatic source mix must revise the persisted session')
  assert.equal(secondBlueprintSessionCall.args?.[0]?.selected_blueprint_id, firstSelectedBlueprintId, 'blueprint revision must target the selected blueprint')
  assert(
    (secondBlueprintSessionCall.args?.[0]?.checklist ?? []).some((item) =>
      item?.decision === 'revise' && (item?.problem_tags ?? []).includes('source_repetition')),
    'chapter corpus blueprint feedback button must send source_repetition through the revision checklist',
  )
  assert.equal(secondBlueprintCandidates?.feedback_applied, true, 'chapter corpus blueprint candidate mock must apply feedback on the second round')
  assert.match(
    String(secondBlueprintCandidates?.feedback_summary ?? ''),
    /rejected_blueprints:1/,
    'chapter corpus blueprint candidate mock must summarize applied feedback',
  )
  assert.match(
    String(secondBlueprintCandidates?.feedback_summary ?? ''),
    /fallback:feedback_filters_no_matches,fallback_to_base_filters/,
    'chapter corpus blueprint candidate mock must expose fallback diagnostics after feedback',
  )
  assert(
    (secondBlueprintCandidates?.candidates?.[0]?.gap_reasons ?? []).includes('feedback_filters_no_matches') &&
      (secondBlueprintCandidates?.candidates?.[0]?.gap_reasons ?? []).includes('fallback_to_base_filters'),
    'chapter corpus blueprint candidate mock must include fallback diagnostic gap reasons',
  )
  assert(
    (secondBlueprintCandidates?.candidates?.[0]?.gap_positions ?? []).some((position) =>
      (position?.gap_reasons ?? []).includes('missing_rhythm_evidence') &&
      (position?.missing_dimensions ?? []).includes('rhythm')),
    'chapter corpus blueprint candidate mock must include beat-level gap positions',
  )
  const firstRegeneratedSources = secondBlueprintCandidates?.candidates?.[0]?.source_distribution ?? []
  assert(
    new Set(firstRegeneratedSources.map((source) => source.library_id)).size >= 2 ||
      new Set(firstRegeneratedSources.map((source) => source.anchor_id)).size >= 2,
    'chapter corpus blueprint candidate mock must prioritize a cross-library or cross-anchor first candidate after source_repetition feedback',
  )
  assert(
    JSON.stringify(firstBlueprintCandidates.candidates.map((candidate) => candidate.blueprint.strategy)) !==
      JSON.stringify(secondBlueprintCandidates.candidates.map((candidate) => candidate.blueprint.strategy)) ||
      JSON.stringify(firstBlueprintCandidates.candidates.map((candidate) => candidate.source_distribution)) !==
        JSON.stringify(secondBlueprintCandidates.candidates.map((candidate) => candidate.source_distribution)),
    'chapter corpus blueprint candidate mock must visibly change strategy or source distribution after feedback',
  )
  await expectHidden(drawer.getByTestId('chapter-corpus-blueprint-feedback-summary'), 'automatic blueprint feedback internals')
  await expectHidden(drawer.getByTestId('chapter-corpus-blueprint-feedback-reason').first(), 'automatic blueprint feedback reason')
  await expectHidden(drawer.getByTestId('chapter-corpus-blueprint-gap-reasons').first(), 'automatic blueprint gap reasons')
  await expectHidden(drawer.getByTestId('chapter-corpus-blueprint-gap-positions').first(), 'automatic blueprint beat diagnostics')
  await expectVisible(blueprintCandidates.locator('[data-testid="chapter-corpus-blueprint-candidate-select"]').first(), 'chapter corpus regenerated blueprint candidate select')
  const secondBlueprintSelectionCountBefore = await bridgeCallCount(page, 'AdvanceReferenceCorpusBlueprintSession')
  await blueprintCandidates.locator('[data-testid="chapter-corpus-blueprint-candidate-select"]').first().click()
  const secondBlueprintSelectionCall = await waitForLatestBridgeCallWithResult(page, 'AdvanceReferenceCorpusBlueprintSession', secondBlueprintSelectionCountBefore)
  assert.equal(secondBlueprintSelectionCall.args?.[0]?.action, 'select', 'regenerated blueprint selection must persist server-side')

  const corpusDraftCountBefore = await bridgeCallCount(page, 'GenerateReferenceCorpusInsertionDraftCandidates')
  const saveCountBeforeCorpusDraft = await bridgeCallCount(page, 'SaveContent')
  await drawer.getByTestId('chapter-corpus-draft-generate-button').click()
  const corpusDraftCall = await waitForLatestBridgeCallWithResult(page, 'GenerateReferenceCorpusInsertionDraftCandidates', corpusDraftCountBefore)
  const corpusDraftInput = corpusDraftCall.args?.[0] ?? null
  assert(corpusDraftInput, 'chapter corpus insertion must call GenerateReferenceCorpusInsertionDraftCandidates with input')
  assert(corpusDraftInput.selected_blueprint?.blueprint_id, 'chapter corpus insertion must include selected_blueprint.blueprint_id')
  assert(
    secondBlueprintCandidates.candidates.some((candidate) =>
      candidate.blueprint?.blueprint_id === corpusDraftInput.selected_blueprint.blueprint_id),
    `chapter corpus insertion selected_blueprint must come from second-round candidates; got ${JSON.stringify(corpusDraftInput.selected_blueprint?.blueprint_id)}`,
  )
  assert.equal(corpusDraftInput.chapter_context?.chapter_number, 1, 'chapter corpus insertion must derive active chapter number')
  assert.equal(corpusDraftInput.chapter_context?.current_draft_text, '林岚在雨夜旧宅门前停住。\n\n她看见桌上的水痕。', 'chapter corpus insertion must send current editor draft text')
  assert.equal(typeof corpusDraftInput.chapter_context?.insertion_offset, 'number', 'chapter corpus insertion must send editor insertion offset')
  assert.deepEqual(corpusDraftInput.scope?.library_ids, [], 'chapter corpus insertion must leave default library resolution to the backend session scope')
  assert.equal(corpusDraftInput.scope?.session_id, 'project:42:default', 'chapter corpus insertion must send the current chapter default corpus session')
  assert.deepEqual(corpusDraftInput.scope?.reuse_policies, ['verbatim_ok', 'adapted_only'], 'chapter corpus insertion must use insertion-safe reuse policies')
  assert.equal(corpusDraftInput.requested_count, 3, 'chapter corpus insertion must request multiple draft candidates')
  assert(corpusDraftCall.result?.candidates?.length >= 2, 'chapter corpus insertion candidate mock must return at least two candidates')
  await expectVisible(drawer.getByTestId('chapter-corpus-draft-candidates'), 'chapter corpus insertion draft candidates')
  const corpusDraftCandidateCards = drawer.locator('[data-testid="chapter-corpus-draft-candidate-card"]')
  assert((await corpusDraftCandidateCards.count()) >= 2, 'chapter corpus insertion UI must render at least two draft candidate cards')
  await expectVisible(corpusDraftCandidateCards.getByText('转场重组').first(), 'chapter corpus transition repair draft candidate label')
  await expectVisible(drawer.getByTestId('chapter-corpus-draft-diff'), 'chapter corpus insertion diff preview')
  await expectVisible(drawer.getByTestId('chapter-corpus-draft-diff').getByText(MOCK_CORPUS_INSERTION_TEXT), 'chapter corpus insertion preview text')
  await expectVisible(drawer.getByTestId('chapter-corpus-diff-preserved').first(), 'chapter corpus insertion preserved text')
  await expectVisible(drawer.getByTestId('chapter-corpus-diff-slot-replacement'), 'chapter corpus insertion slot replacement highlight')
  await assertEditorNotContains(page, MOCK_CORPUS_INSERTION_TEXT)
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeCorpusDraft, 'generating corpus insertion draft must not save chapter content')
  const blockedCorpusDraftIndex = corpusDraftCall.result.candidates.findIndex((candidate) =>
    candidate.draft?.gate?.passed === true && candidate.draft?.audit?.passed === false)
  assert(blockedCorpusDraftIndex >= 0, 'chapter corpus insertion mock must include an audit-blocked draft candidate')
  assert.equal(corpusDraftCall.result.candidates[blockedCorpusDraftIndex].draft.ready_for_insertion, false, 'audit-blocked corpus draft must not be ready for insertion')
  await expectVisible(corpusDraftCandidateCards.nth(blockedCorpusDraftIndex).getByText('暂不能插入'), 'chapter corpus blocked draft card status')
  await expectVisible(drawer.getByText('暂不能插入').first(), 'chapter corpus blocked draft preview status')
  await expectHidden(drawer.getByText('preserved_text_hash_mismatch').first(), 'automatic corpus draft audit internals')
  const applyCorpusButton = drawer.getByRole('button', { name: '应用到编辑器' })
  assert.equal(await applyCorpusButton.isDisabled(), true, 'audit-blocked corpus draft apply button must be disabled')
  await assertEditorNotContains(page, MOCK_CORPUS_INSERTION_TEXT)

  const transitionBlockedCorpusDraftIndex = corpusDraftCall.result.candidates.findIndex((candidate) =>
    candidate.draft?.audit?.transitions?.some((transition) => transition.passed === false))
  assert(transitionBlockedCorpusDraftIndex >= 0, 'chapter corpus insertion mock must include a transition-audit-blocked draft candidate')
  await drawer.locator('[data-testid="chapter-corpus-draft-candidate-select"]').nth(transitionBlockedCorpusDraftIndex).click()
  await expectVisible(drawer.getByText('需要重组蓝图').first(), 'chapter corpus transition blocked draft status')
  await expectHidden(drawer.getByText('transition_piece_replacement_required').first(), 'automatic corpus transition audit internals')
  const transitionBlockedCorpusDraftCandidate = corpusDraftCall.result.candidates[transitionBlockedCorpusDraftIndex]
  const transitionNextAction = transitionBlockedCorpusDraftCandidate.next_action
  assert(transitionNextAction, 'chapter corpus transition blocked draft candidate must include next_action')
  assert.equal(transitionNextAction.action, 'regenerate_blueprint', 'chapter corpus transition blocked draft next_action must regenerate blueprint candidates')
  assert(
    (transitionNextAction.feedback?.problem_tags ?? []).includes('transition_replacement_required'),
    'chapter corpus transition blocked draft next_action feedback must carry transition_replacement_required',
  )
  const transitionNextActionButton = corpusDraftCandidateCards.nth(transitionBlockedCorpusDraftIndex).getByTestId('chapter-corpus-draft-next-action-button')
  await expectVisible(transitionNextActionButton, 'chapter corpus transition blocked draft next action button')
  assert.equal(await applyCorpusButton.isDisabled(), true, 'transition-audit-blocked corpus draft apply button must be disabled')
  await assertEditorNotContains(page, MOCK_CORPUS_TRANSITION_TEXT)

  const selectedCorpusDraftIndex = corpusDraftCall.result.candidates.findIndex((candidate) =>
    candidate.draft?.ready_for_insertion === true &&
    candidate.draft?.gate?.passed === true &&
    candidate.draft?.audit?.passed === true &&
    candidate.draft?.transitions?.some((transition) => transition.text === MOCK_CORPUS_TRANSITION_TEXT))
  assert(selectedCorpusDraftIndex >= 0, 'chapter corpus insertion mock must include a ready draft candidate')
  const selectedCorpusDraft = corpusDraftCall.result.candidates[selectedCorpusDraftIndex].draft
  await drawer.locator('[data-testid="chapter-corpus-draft-candidate-select"]').nth(selectedCorpusDraftIndex).click()
  await expectVisible(drawer.getByTestId('chapter-corpus-draft-diff').getByText(selectedCorpusDraft.pieces[0].output_text), 'chapter corpus selected insertion preview first source text')
  await expectVisible(drawer.getByTestId('chapter-corpus-draft-diff').getByText(selectedCorpusDraft.pieces[1].output_text), 'chapter corpus selected insertion preview second source text')
  await expectVisible(drawer.getByTestId('chapter-corpus-draft-transition').getByText(MOCK_CORPUS_TRANSITION_TEXT), 'chapter corpus selected transition preview text')
  assert.equal(await applyCorpusButton.isDisabled(), false, 'ready corpus draft apply button must be enabled')
  const insertionAuditCountBefore = await bridgeCallCount(page, 'RecordReferenceCorpusInsertionAudit')
 await applyCorpusButton.click()
 const insertionAuditCall = await waitForLatestBridgeCallWithResult(page, 'RecordReferenceCorpusInsertionAudit', insertionAuditCountBefore)
 assert.equal(insertionAuditCall.args?.[0]?.candidate_id, corpusDraftCall.result.candidates[selectedCorpusDraftIndex].candidate_id, 'corpus insertion audit must bind the selected candidate')
 assert.deepEqual(insertionAuditCall.args?.[0]?.draft, selectedCorpusDraft, 'corpus insertion audit must submit the complete draft for server-side recomputation')
   await page.waitForFunction(
    (expectedText) => window.__novelistEditor?.getValue?.() === expectedText,
    selectedCorpusDraft.chapter_text_after_insertion,
    { timeout: 12_000 },
  )
  await page.waitForTimeout(700)
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeCorpusDraft, 'applying corpus insertion draft must update editor buffer without direct SaveContent')
  await assertEditorContains(page, MOCK_CORPUS_TRANSITION_TEXT)
  await page.keyboard.press(shortcutKey('z'))
  await assertEditorNotContains(page, MOCK_CORPUS_INSERTION_TEXT)
  await assertEditorNotContains(page, MOCK_CORPUS_TRANSITION_TEXT)

  const draftNextActionBlueprintCountBefore = await bridgeCallCount(page, 'AdvanceReferenceCorpusBlueprintSession')
  await transitionNextActionButton.click()
  const draftNextActionBlueprintCall = await waitForLatestBridgeCallWithResult(page, 'AdvanceReferenceCorpusBlueprintSession', draftNextActionBlueprintCountBefore)
  assert.equal(draftNextActionBlueprintCall.args?.[0]?.action, 'revise', 'chapter corpus transition blocked next action must revise the persisted blueprint session')
  assert(
    (draftNextActionBlueprintCall.args?.[0]?.checklist ?? []).some((item) =>
      item?.decision === 'revise' && (item?.problem_tags ?? []).includes('transition_replacement_required')),
    'chapter corpus transition blocked draft next_action must pass its recovery reason through the revision checklist',
  )
  assert.equal(draftNextActionBlueprintCall.result?.candidates?.feedback_applied, true, 'chapter corpus transition blocked draft next_action must return feedback-applied blueprint candidates')
  await expectVisible(drawer.getByText(/已按正文候选诊断重组第 \d+ 轮蓝图。/).first(), 'chapter corpus draft next action blueprint regeneration message')

  const thirdBlueprintSelectionCountBefore = await bridgeCallCount(page, 'AdvanceReferenceCorpusBlueprintSession')
  await drawer.getByTestId('chapter-corpus-blueprint-candidate-select').first().click()
  const thirdBlueprintSelectionCall = await waitForLatestBridgeCallWithResult(page, 'AdvanceReferenceCorpusBlueprintSession', thirdBlueprintSelectionCountBefore)
  assert.equal(thirdBlueprintSelectionCall.args?.[0]?.action, 'select', 'blueprint selection after blocked recovery must persist server-side')
 await drawer.getByTestId('chapter-writing-mode').getByRole('button', { name: '专家' }).click()
 await expectVisible(blueprintCandidates.getByTestId('chapter-corpus-blueprint-iteration'), 'expert blueprint iteration state')
 await expectVisible(blueprintCandidates.getByTestId('chapter-corpus-blueprint-difference-audit').first(), 'expert blueprint difference audit')
 await expectVisible(drawer.getByTestId('chapter-corpus-blueprint-feedback-summary'), 'expert blueprint feedback summary')
 await expectVisible(drawer.getByTestId('chapter-corpus-blueprint-feedback-reason').first(), 'expert blueprint feedback fallback reason')
 await expectVisible(drawer.getByTestId('chapter-corpus-blueprint-gap-reasons').first(), 'expert blueprint fallback gap reasons')
 await expectVisible(drawer.getByTestId('chapter-corpus-blueprint-gap-positions').first(), 'expert blueprint beat-level gap positions')
 await expectVisible(
   drawer.getByText('已避开上一轮拒绝的蓝图、节点或来源。').first(),
   'expert blueprint readable feedback diagnostic',
 )
 await expectVisible(drawer.getByTestId('chapter-corpus-expert-controls'), 'chapter corpus expert slot and transition controls')
 await expectVisible(drawer.getByTestId('chapter-corpus-expert-slot-table'), 'chapter corpus expert slot table')
 await expectVisible(drawer.getByTestId('chapter-corpus-expert-transition-list'), 'chapter corpus expert selected transition list')
 const expertDraftCountBefore = await bridgeCallCount(page, 'GenerateReferenceCorpusInsertionDraftCandidates')
 await drawer.getByTestId('chapter-corpus-draft-generate-button').click()
 const expertDraftCall = await waitForLatestBridgeCallWithResult(page, 'GenerateReferenceCorpusInsertionDraftCandidates', expertDraftCountBefore)
 assert((expertDraftCall.args?.[0]?.slot_value_variants ?? []).length >= 2, 'expert corpus draft request must include slot value variants')
 assert.deepEqual(expertDraftCall.args?.[0]?.transition_strategy_variants, ['default', 'direct_join'], 'expert corpus draft request must include selected transition strategies')
await expectVisible(drawer.getByTestId('chapter-corpus-draft-comparison'), 'chapter corpus expert parallel draft comparison')
 await expectVisible(drawer.getByTestId('chapter-corpus-draft-transition-list').first(), 'chapter corpus per-draft transition list')
 await expectVisible(drawer.getByText(/候选集审计/).first(), 'chapter corpus candidate set audit summary')
 const expertApplyButton = drawer.getByRole('button', { name: '应用到编辑器' })
 assert.equal(await expertApplyButton.isDisabled(), true, 'expert corpus draft must require locking before apply')
 const expertReadyIndex = expertDraftCall.result.candidates.findIndex((candidate) => candidate.draft?.ready_for_insertion === true)
 assert(expertReadyIndex >= 0, 'expert corpus draft mock must include a ready candidate')
await drawer.getByTestId('chapter-corpus-draft-comparison').getByRole('button', { name: '锁定此稿' }).nth(expertReadyIndex).click()
 await expectVisible(drawer.getByTestId('chapter-corpus-draft-lock-confirmation'), 'chapter corpus locked draft confirmation')
 assert.equal(await expertApplyButton.isDisabled(), false, 'locked expert corpus draft must be eligible for confirmation')
 const expertAuditCountBefore = await bridgeCallCount(page, 'RecordReferenceCorpusInsertionAudit')
 await expertApplyButton.click()
 const expertAuditCall = await waitForLatestBridgeCallWithResult(page, 'RecordReferenceCorpusInsertionAudit', expertAuditCountBefore)
 assert.equal(expertAuditCall.args?.[0]?.candidate_id, expertDraftCall.result.candidates[expertReadyIndex].candidate_id, 'expert corpus insertion audit must bind locked candidate')
 await page.keyboard.press(shortcutKey('z'))

await drawer.getByText('高级参考流程').click()
  await expectVisible(drawer.getByRole('heading', { name: '推荐素材' }), 'chapter reference recommendations heading')
  const chapterMaterialCard = drawer.getByTestId('chapter-reference-material-card').first()
  await expectVisible(chapterMaterialCard, 'chapter reference recommendation card')
  const chapterSearchResult = await waitForLatestBridgeResult(page, 'SearchReferenceMaterials', chapterSearchCountBefore)
  const chapterSearchJson = JSON.stringify(chapterSearchResult)
  assert(!chapterSearchJson.includes(FULL_MATERIAL_LEAK_SENTINEL), 'chapter reference bridge search result must not expose full material text')
  assert(!chapterSearchJson.includes('"text"'), 'chapter reference bridge search result must not include full text field')
  assert(chapterSearchJson.includes('text_preview'), 'chapter reference bridge search result must include bounded text_preview')
  const chapterMaterialCardText = await chapterMaterialCard.innerText()
  assert(!chapterMaterialCardText.includes(FULL_MATERIAL_LEAK_SENTINEL), 'chapter reference material card must render bounded preview only')
  assert(chapterMaterialCardText.includes('预览已截断，不显示全文'), 'chapter reference material card must mark bounded preview')
  const chapterMaterialDetailCountBefore = await bridgeCallCount(page, 'GetReferenceMaterialDetail')
  await chapterMaterialCard.getByRole('button', { name: /查看 .* 的材料明细/ }).click()
  await waitForBridgeCallCountAfter(page, 'GetReferenceMaterialDetail', chapterMaterialDetailCountBefore)
  const chapterMaterialDetailDrawer = page.getByTestId('chapter-reference-material-detail-drawer')
  await expectVisible(chapterMaterialDetailDrawer, 'chapter reference material detail drawer')
  const chapterMaterialDetailText = await chapterMaterialDetailDrawer.innerText()
  for (const expectedText of ['材料明细', '来源片段', '处理记录', '工作区语料', '预览已截断，不显示全文']) {
    assert(chapterMaterialDetailText.includes(expectedText), `chapter material detail drawer must render ${expectedText}`)
  }
  for (const sensitiveText of ['D:\\books', 'source_text', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!chapterMaterialDetailText.includes(sensitiveText), `chapter material detail drawer must not render ${sensitiveText}`)
  }
  await chapterMaterialDetailDrawer.getByRole('button', { name: '关闭章节推荐材料明细' }).click()
  await expectHidden(chapterMaterialDetailDrawer, 'chapter reference material detail drawer after close')

  await waitForBridgeCall(page, 'GetReferenceOrchestrationRuns')

  await drawer.getByRole('button', { name: '启动参考流程' }).click()
  await waitForBridgeCall(page, 'StartReferenceOrchestrationRun')
  await expectVisible(drawer.getByTestId('chapter-reference-orchestration-run'), 'chapter reference orchestration status')
  await expectVisible(drawer.getByText('请确认本章来源和事实边界后继续。'), 'chapter reference orchestration decision')

  await drawer.getByRole('button', { name: '确认并继续' }).click()
  await waitForBridgeCall(page, 'ResumeReferenceOrchestrationRun')
  await expectVisible(drawer.getByText('来源和事实边界已确认，请审批自动蓝图。'), 'chapter reference resumed decision')

  const adaptCountBeforeStrictFlow = await bridgeCallCount(page, 'AdaptReferenceMaterial')
  const saveCountBeforeStrictFlow = await bridgeCallCount(page, 'SaveContent')
  await expectHidden(drawer.getByRole('button', { name: '生成候选' }), 'direct material candidate generation button')
  await expectHidden(drawer.getByTestId('chapter-reference-candidate-preview'), 'direct chapter candidate preview')
  await expectVisible(drawer.getByText('推荐卡不直接改写或插入正文'), 'strict chapter reference material card copy')
  await expectVisible(drawer.getByText(/本面板不会从推荐素材直接生成可插入候选/), 'strict chapter reference flow copy')
  assert.equal(await bridgeCallCount(page, 'AdaptReferenceMaterial'), adaptCountBeforeStrictFlow, 'chapter reference drawer must not call direct material adaptation')
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeStrictFlow, 'chapter reference drawer must not save chapter content')

  const finalInsertionResumeCountBefore = await bridgeCallCount(page, 'ResumeReferenceOrchestrationRun')
  const candidateReadCountBefore = await bridgeCallCount(page, 'GetReferenceDraftCandidates')
  const auditReadCountBefore = await bridgeCallCount(page, 'GetReferenceAnchoredDraftAudits')
  await drawer.getByRole('button', { name: '确认并继续' }).click()
  await waitForBridgeCallCountAfter(page, 'ResumeReferenceOrchestrationRun', finalInsertionResumeCountBefore)
  await expectVisible(drawer.getByText('候选已通过审计，请在正文中显式插入。'), 'chapter reference final insertion stop')
  await expectVisible(drawer.getByText(/候选 1 个/), 'chapter reference final insertion candidate count')
  await expectVisible(drawer.getByText(/最终插入需要进入独立候选审查/), 'chapter reference final insertion manual boundary copy')
  const candidateResult = await waitForLatestBridgeResult(page, 'GetReferenceDraftCandidates', candidateReadCountBefore)
  await waitForLatestBridgeResult(page, 'GetReferenceAnchoredDraftAudits', auditReadCountBefore)
  const candidateJson = JSON.stringify(candidateResult)
  assert(candidateJson.includes(MOCK_REFERENCE_CANDIDATE_TEXT), 'chapter reference candidate getter must return preview text for explicit editor insertion')
  for (const sensitiveText of ['D:\\books', 'source_text', 'source_path', 'prompt', 'candidate_text', FULL_MATERIAL_LEAK_SENTINEL]) {
    assert(!candidateJson.includes(sensitiveText), `chapter reference candidate getter must not expose ${sensitiveText}`)
  }
  const candidatePreview = drawer.getByTestId('chapter-reference-candidate-preview')
  await expectVisible(candidatePreview, 'chapter reference candidate preview')
  await expectVisible(candidatePreview.getByText(MOCK_REFERENCE_CANDIDATE_TEXT), 'chapter reference candidate text')
  await assertEditorNotContains(page, MOCK_REFERENCE_CANDIDATE_TEXT)
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeStrictFlow, 'chapter reference candidate preview must not save chapter content')
  await candidatePreview.getByRole('button', { name: '复制候选' }).click()
  await page.waitForFunction(
    (expectedText) => window.__appMockClipboardText === expectedText,
    MOCK_REFERENCE_CANDIDATE_TEXT,
    { timeout: 12_000 },
  )
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeStrictFlow, 'copying a candidate must not save chapter content')
  await candidatePreview.getByRole('button', { name: '插入到光标' }).click()
  await assertEditorContains(page, MOCK_REFERENCE_CANDIDATE_TEXT)
  await page.keyboard.press(shortcutKey('z'))
  await assertEditorNotContains(page, MOCK_REFERENCE_CANDIDATE_TEXT)
  await candidatePreview.getByRole('button', { name: '追加到末尾' }).click()
  await assertEditorContains(page, MOCK_REFERENCE_CANDIDATE_TEXT)
  await page.keyboard.press(shortcutKey('z'))
  await assertEditorNotContains(page, MOCK_REFERENCE_CANDIDATE_TEXT)
  await page.evaluate(() => window.__novelistEditor.selectAll())
  await candidatePreview.getByRole('button', { name: '替换选区' }).click()
  await page.waitForFunction(
    (expectedText) => window.__novelistEditor?.getValue?.() === expectedText,
    MOCK_REFERENCE_CANDIDATE_TEXT,
    { timeout: 12_000 },
  )
  await page.waitForTimeout(700)
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeStrictFlow, 'explicit candidate insertion must update editor buffer without direct SaveContent')
  assert.equal(await drawer.getByRole('button', { name: '确认并继续' }).isDisabled(), true, 'final insertion resume must be disabled in chapter reference drawer')
  assert.equal(await bridgeCallCount(page, 'SaveContent'), saveCountBeforeStrictFlow, 'chapter reference final insertion stop must not save chapter content')
  const resumeDecisionTypes = await page.evaluate(() =>
    window.__appMockState.calls
      .filter((item) => item.method === 'ResumeReferenceOrchestrationRun')
      .map((item) => item.args?.[0]?.decision_type))
  assert(resumeDecisionTypes.includes('approve_blueprint'), 'chapter reference must explicitly resume blueprint approval before final insertion stop')
  assert(!resumeDecisionTypes.includes('approve_final_insertion'), 'chapter reference drawer must not auto-resume final insertion')

  await drawer.getByRole('button', { name: '取消流程' }).click()
  await waitForBridgeCall(page, 'CancelReferenceOrchestrationRun')
  await expectVisible(drawer.getByText('cancelled · final_insertion'), 'chapter reference cancelled run status')

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

  assert.equal(await bridgeCallCount(page, 'AdaptReferenceMaterial'), 0, 'chapter reference drawer must not bypass orchestration with direct material adaptation')

  await page.getByRole('button', { name: '大纲' }).click()
  await expectHidden(drawer, 'chapter reference drawer after switching to outline view')

  await page.getByRole('button', { name: /故事状态/ }).click()
  await waitForBridgeCallArg(page, 'GetContent', 1, 'novelist.md')
  await expectHidden(drawer, 'chapter reference drawer after switching to non-chapter file')
}

async function assertInViewport(page, locator, description) {
  await expectVisible(locator, description)
  const box = await locator.boundingBox()
  const viewport = page.viewportSize()
  assert(box, `${description} must have a bounding box`)
  assert(viewport, `${description} must run with a fixed viewport`)
  assert(box.x >= -1 && box.y >= -1, `${description} must not start outside the viewport; got ${JSON.stringify(box)}`)
  assert(
    box.x + box.width <= viewport.width + 1 && box.y + box.height <= viewport.height + 1,
    `${description} must remain inside ${viewport.width}x${viewport.height}; got ${JSON.stringify(box)}`,
  )
}

async function verifyChapterReferenceViewportMatrix(browser, url, consoleErrors, pageErrors) {
  const viewports = [
    { width: 1280, height: 720, label: '1280x720' },
    { width: 1024, height: 576, label: '1280x720-125' },
    { width: 853, height: 480, label: '1280x720-150' },
    { width: 1440, height: 900, label: '1440x900' },
    { width: 1152, height: 720, label: '1440x900-125' },
    { width: 960, height: 600, label: '1440x900-150' },
  ]

  for (const viewport of viewports) {
    const matrixPage = await newAppPage(
      browser,
      consoleErrors,
      pageErrors,
      { initialized: true },
      { width: viewport.width, height: viewport.height },
      `chapter-reference-${viewport.label}`,
    )
    try {
      await matrixPage.goto(url, { waitUntil: 'domcontentloaded' })
      await clickActivity(matrixPage, '章节')
      await ensureChapterBlockExpanded(matrixPage)
      await chapterButton(matrixPage, '雨夜线索').click()
      await expectVisible(matrixPage.locator('.monaco-editor').first(), `${viewport.label} chapter editor`)
      await matrixPage.getByRole('button', { name: /参考素材/ }).click()

      const drawer = matrixPage.getByTestId('chapter-reference-panel')
      const goal = drawer.getByLabel('章节目标')
      const primaryAction = drawer.getByTestId('chapter-corpus-blueprint-generate-button')
      await assertInViewport(matrixPage, drawer, `${viewport.label} chapter reference drawer`)
      await assertInViewport(matrixPage, goal, `${viewport.label} chapter goal input`)
      await assertInViewport(matrixPage, primaryAction, `${viewport.label} chapter blueprint primary action`)
      const drawerBox = await drawer.boundingBox()
      const primaryActionBox = await primaryAction.boundingBox()
      assert(drawerBox && primaryActionBox, `${viewport.label} chapter reference drawer and primary action must be measurable`)
      assert(
        primaryActionBox.x >= drawerBox.x - 1 && primaryActionBox.x + primaryActionBox.width <= drawerBox.x + drawerBox.width + 1,
        `${viewport.label} chapter blueprint primary action must remain inside its panel; drawer=${JSON.stringify(drawerBox)}, action=${JSON.stringify(primaryActionBox)}`,
      )
      const primaryActionOverflow = await primaryAction.evaluate((element) => element.scrollWidth > element.clientWidth)
      assert.equal(primaryActionOverflow, false, `${viewport.label} chapter blueprint primary action text must not be clipped`)
      const primaryActionIsTopmost = await primaryAction.evaluate((element) => {
        const rect = element.getBoundingClientRect()
        const topmost = document.elementFromPoint(rect.left + rect.width / 2, rect.top + rect.height / 2)
        return topmost === element || element.contains(topmost)
      })
      assert.equal(primaryActionIsTopmost, true, `${viewport.label} chapter blueprint primary action must not be covered by another panel`)
      const horizontalOverflow = await drawer.evaluate((element) => element.scrollWidth > element.clientWidth)
      assert.equal(horizontalOverflow, false, `${viewport.label} chapter reference drawer must not overflow horizontally`)
      await matrixPage.screenshot({ path: path.join(outputDir, `app-phase16-chapter-reference-${viewport.label}.png`) })
    } finally {
      await matrixPage.close()
    }
  }
}

async function verifyChapterReferenceRetryWorkflow(browser, url, consoleErrors, pageErrors) {
  const retryPage = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    {
      initialized: true,
      allowSaveContent: true,
      faults: {
        AdvanceReferenceCorpusBlueprintSession: {
          mode: 'storage',
          code: 'BLUEPRINT_SESSION_WRITE_INTERRUPTED',
          message: '蓝图会话写入暂时不可用',
          retryable: true,
        },
      },
    },
    undefined,
    'chapter-reference-retry',
  )
  try {
    await retryPage.goto(url, { waitUntil: 'domcontentloaded' })
    await clickActivity(retryPage, '章节')
    await ensureChapterBlockExpanded(retryPage)
    await chapterButton(retryPage, '雨夜线索').click()
    await retryPage.getByRole('button', { name: /参考素材/ }).click()

    const drawer = retryPage.getByTestId('chapter-reference-panel')
    await expectVisible(drawer, 'chapter reference retry drawer')
    const advanceCountBefore = await bridgeCallCount(retryPage, 'AdvanceReferenceCorpusBlueprintSession')
    await drawer.getByTestId('chapter-corpus-blueprint-generate-button').click()
    await waitForBridgeCallCountAfter(retryPage, 'AdvanceReferenceCorpusBlueprintSession', advanceCountBefore)
    const failedAdvanceCall = await retryPage.evaluate((method) =>
      window.__appMockState.calls.filter((call) => call.method === method).at(-1) ?? null,
    'AdvanceReferenceCorpusBlueprintSession')
    assert(failedAdvanceCall && !Object.hasOwn(failedAdvanceCall, 'result'), 'faulted blueprint advance must be recorded without a success result')
    const retryAlert = drawer.getByRole('alert').filter({ hasText: '蓝图候选生成失败' })
    await expectVisible(retryAlert, 'chapter reference retry error state')
    const retryAdvanceCountBefore = await bridgeCallCount(retryPage, 'AdvanceReferenceCorpusBlueprintSession')
    await retryAlert.getByRole('button', { name: '重试当前操作' }).click()
    const retriedAdvanceCall = await waitForLatestBridgeCallWithResult(retryPage, 'AdvanceReferenceCorpusBlueprintSession', retryAdvanceCountBefore)
    assert.equal(retriedAdvanceCall.args?.[0]?.request_id, failedAdvanceCall.args?.[0]?.request_id, 'blueprint retry must reuse the original idempotency request id')
    await expectVisible(drawer.getByTestId('chapter-corpus-blueprint-candidates'), 'chapter reference retry candidates')
  } finally {
    await retryPage.close()
  }
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
  if (runConfig.grep === '@error' || runConfig.grep === '@update' || runConfig.grep === '@chapter-reference' || runConfig.grep === '@corpus-library') {
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
    logStep('checking Phase 16 chapter reference viewport matrix')
    await verifyChapterReferenceViewportMatrix(browser, url, consoleErrors, pageErrors)
    logStep('checking Phase 16 chapter reference retry recovery')
    await verifyChapterReferenceRetryWorkflow(browser, url, consoleErrors, pageErrors)
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

  if (runConfig.grep === '@reference-workspace') {
    logStep('checking reference books and blueprint preview workspace')
    await verifyReferenceWorkspaceWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-reference-workspace.png'), fullPage: true })
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
  } else if (runConfig.grep === '@reference-workspace') {
    await verifyReferenceWorkspaceBridgeCalls(page)
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

  await clickActivity(page, '素材库')
  await expectVisible(page.getByRole('heading', { name: '语料库管理' }), 'usability corpus library heading')
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
