import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import fs from 'node:fs/promises'
import net from 'node:net'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const frontendRoot = path.resolve(__dirname, '..')
const repoRoot = path.resolve(frontendRoot, '..')
const outputDir = path.join(repoRoot, 'output', 'playwright')

const now = '2026-07-05T12:00:00.000Z'

async function main() {
  await fs.mkdir(outputDir, { recursive: true })

  const port = await getFreePort()
  const server = startVite(port)
  const url = `http://127.0.0.1:${port}/`
  const consoleErrors = []
  const pageErrors = []
  let browser

  try {
    logStep('waiting for Vite')
    await waitForServer(url, server)
    browser = await launchBrowser()
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } })
    page.setDefaultTimeout(10_000)

    page.on('console', (message) => {
      if (message.type() === 'error') {
        consoleErrors.push(message.text())
      }
    })
    page.on('pageerror', (error) => pageErrors.push(error.message))

    logStep('loading app')
    await page.addInitScript(installReferenceAnchorMockBridge)
    await page.goto(url, { waitUntil: 'domcontentloaded' })

    logStep('opening reference panel')
    await page.getByTitle('参考锚定').waitFor({ state: 'visible' })
    await page.getByTitle('参考锚定').click()
    await expectVisible(page.getByRole('heading', { name: '参考锚定' }), 'reference panel heading')
    await expectVisible(page.getByRole('heading', { name: '语料库管理' }), 'corpus library management heading')
    await expectVisible(page.getByRole('heading', { name: '导入语料来源' }), 'corpus source import heading')
    await expectVisible(page.getByRole('heading', { name: '库条目' }), 'corpus library entries heading')
    await expectVisible(page.getByRole('heading', { name: '参考写作检索' }), 'reference drafting retrieval heading')
    await page.screenshot({ path: path.join(outputDir, 'reference-anchor-01-initial.png'), fullPage: true })

    logStep('checking advanced-mode boundary')
    await expectHidden(page.getByText('材料搜索', { exact: true }), 'material search hidden by default')
    await expectHidden(page.getByRole('button', { name: /生成蓝图/ }), 'manual blueprint generation hidden by default')
    await expectHidden(page.getByText('当前节拍字段', { exact: true }), 'manual blueprint detail hidden by default')
    await openAdvancedMode(page)

    logStep('create/rebuild/search')
    await createRebuildAndSearchReferenceMaterial(page)
    logStep('manual blueprint workflow')
    await generateReviseApproveBindAndDraft(page)
    await page.screenshot({ path: path.join(outputDir, 'reference-anchor-02-draft-audit.png'), fullPage: true })

    logStep('default orchestration workflow')
    await runDefaultOrchestrationToFinalInsertionStop(page)
    await page.screenshot({ path: path.join(outputDir, 'reference-anchor-03-final-insertion-stop.png'), fullPage: true })

    logStep('stale blueprint workflow')
    await verifyStaleBlueprintIsReadOnly(page)
    await page.screenshot({ path: path.join(outputDir, 'reference-anchor-04-stale-blueprint.png'), fullPage: true })

    logStep('checking bridge calls')
    await verifyBridgeCalls(page)

    assert.deepEqual(pageErrors, [], `Unexpected page errors:\n${pageErrors.join('\n')}`)
    assert.deepEqual(consoleErrors, [], `Unexpected console errors:\n${consoleErrors.join('\n')}`)
    console.log(`Reference-anchor mock workflow passed. Screenshots: ${path.relative(repoRoot, outputDir)}`)
  } finally {
    await browser?.close()
    stopProcess(server)
  }
}

async function createRebuildAndSearchReferenceMaterial(page) {
  await page.getByPlaceholder('参考书名').fill('雨夜动作参考')
  await page.getByPlaceholder('可选').first().fill('Mock Author')
  await page.getByLabel('本地路径').fill('D:\\books\\rain-reference.md')
  await page.getByLabel('来源可信度').selectOption('imported')
  await page.getByLabel('用户标签').fill('雨夜;动作克制')
  await page.getByRole('button', { name: /^创建$/ }).click()
  await expectVisible(page.getByText('参考锚点已创建'), 'anchor created message')
  await expectVisible(page.getByText('雨夜动作参考'), 'created anchor title')
  await expectVisible(page.getByText('private · imported · novel'), 'created private anchor metadata')

  await page.getByRole('button', { name: /提升 雨夜动作参考 为工作区语料/ }).click()
  await expectVisible(page.getByText('已提升为工作区语料'), 'anchor promoted message')
  await expectVisible(page.getByText('workspace · imported · workspace_corpus'), 'created anchor corpus metadata')
  const createdAnchorRow = page.locator('.rounded-md').filter({ hasText: '雨夜动作参考' }).first()
  assert.equal(await createdAnchorRow.getByRole('checkbox').isChecked(), false, 'promote action must not toggle anchor selection')
  await expectVisible(page.getByText('雨夜', { exact: true }), 'created anchor first user tag')
  await expectVisible(page.getByText('动作克制', { exact: true }), 'created anchor second user tag')

  await page.getByRole('button', { name: '工作区 1' }).click()
  await expectVisible(page.getByText('workspace · imported · workspace_corpus'), 'workspace corpus filter row')
  await page.getByRole('button', { name: '本小说 0' }).click()
  await expectVisible(page.getByText('暂无参考锚点'), 'empty novel-owned anchor filter')
  await page.getByRole('button', { name: '全部 1' }).click()

  await page.getByRole('button', { name: /编辑 雨夜动作参考 元数据/ }).click()
  await page.getByLabel('编辑锚点标题').fill('雨夜动作语料库')
  await page.getByLabel('编辑锚点作者').fill('Metadata Curator')
  await page.getByLabel('编辑锚点授权').selectOption('licensed')
  await page.getByLabel('编辑锚点可信度').selectOption('user_verified')
  await page.getByLabel('编辑锚点用户标签').fill('雨夜;动作克制;精选')
  await page.getByRole('button', { name: /^保存$/ }).click()
  await expectVisible(page.getByText('参考元数据已更新'), 'anchor metadata updated message')
  await expectVisible(page.getByText('雨夜动作语料库'), 'updated anchor title')
  await expectVisible(page.getByText('workspace · user_verified · workspace_corpus'), 'updated anchor metadata')
  await expectVisible(page.getByText('精选', { exact: true }), 'updated anchor user tag')

  await page.getByLabel('锚点搜索').fill('Mock Author')
  await expectVisible(page.getByText('没有匹配的参考锚点'), 'anchor list query excludes old author')
  await page.getByLabel('锚点搜索').fill('Metadata Curator')
  await expectVisible(page.getByText('雨夜动作语料库'), 'anchor list query matches updated author')
  await page.getByLabel('锚点搜索').fill('不存在的语料')
  await expectVisible(page.getByText('没有匹配的参考锚点'), 'anchor list query empty state')
  await page.getByLabel('锚点搜索').fill('精选')
  await expectVisible(page.getByText('雨夜动作语料库'), 'anchor list query matches updated user tag')
  await page.getByLabel('锚点可信度筛选').selectOption('unverified')
  await expectVisible(page.getByText('没有匹配的参考锚点'), 'anchor list source trust filter empty state')
  await page.getByLabel('锚点可信度筛选').selectOption('user_verified')
  await expectVisible(page.getByText('雨夜动作语料库'), 'anchor list source trust filter match')
  await page.getByLabel('锚点授权筛选').selectOption('unknown')
  await expectVisible(page.getByText('没有匹配的参考锚点'), 'anchor list license filter empty state')
  await page.getByLabel('锚点授权筛选').selectOption('licensed')
  await expectVisible(page.getByText('雨夜动作语料库'), 'anchor list license filter match')
  await page.getByRole('button', { name: '清除筛选' }).click()

  await page.getByRole('button', { name: /浏览 雨夜动作语料库 的材料/ }).click()
  const materialPreview = page.locator('[aria-label="雨夜动作语料库 材料预览"]')
  const firstMaterial = materialPreview.locator('.rounded').filter({ hasText: 'mat-001' }).first()
  await expectVisible(firstMaterial.getByText('mat-001'), 'anchor material preview id')
  await expectVisible(firstMaterial.getByText('把杯子推远，杯底在木桌上留下半圈水痕。'), 'anchor material preview text')
  await expectVisible(firstMaterial.getByText('lexical 0.92'), 'anchor material preview score component')
  await firstMaterial.getByRole('button', { name: /校正 mat-001 的材料标签/ }).click()
  await firstMaterial.getByLabel('材料功能标签').fill('object_subtext')
  await firstMaterial.getByLabel('材料情绪标签').fill('contained_tension')
  await firstMaterial.getByLabel('材料场景标签').fill('rain_threshold')
  await firstMaterial.getByLabel('材料 POV 标签').fill('limited_close')
  await firstMaterial.getByLabel('材料技法标签').fill('delayed_reaction')
  await firstMaterial.getByRole('button', { name: /^保存标签$/ }).click()
  await expectVisible(page.getByText('材料标签已校正'), 'material tag update message')
  await expectVisible(firstMaterial.getByText('object_subtext'), 'corrected material function tag')
  await expectVisible(firstMaterial.getByText('limited_close'), 'corrected material pov tag')
  await expectVisible(page.getByText('第 1 / 2 页 · 共 6 条'), 'anchor material preview pagination summary')
  await page.getByRole('button', { name: /下一页材料/ }).click()
  await expectVisible(page.getByText('mat-006'), 'anchor material preview second page id')
  await expectVisible(page.getByText('雨水从伞沿断续落下，像有人在门外迟疑。'), 'anchor material preview second page text')

  await page.locator('button[title="重建"]').first().click()
  await expectVisible(page.getByText('锚点已重建'), 'anchor rebuilt message')

  const materialPanel = page.locator('.rounded-lg').filter({ hasText: '材料搜索' }).first()
  await materialPanel.getByPlaceholder('叙事功能、情绪或具体句子').fill('把杯子推远')
  await materialPanel.getByLabel('文体职责').fill('source_backed_detail')
  await materialPanel.getByRole('button', { name: /搜索/ }).click()
  await expectVisible(materialPanel.getByText('把杯子推远，杯底在木桌上留下半圈水痕。'), 'material search hit')
  await expectVisible(materialPanel.getByText('lexical 0.92'), 'material score component')
  await expectVisible(materialPanel.getByText('prose_duty 0.75'), 'material prose-duty score component')
}

async function generateReviseApproveBindAndDraft(page) {
  await page.getByLabel('章节号').first().fill('3')
  await page.getByLabel('章节目标').first().fill('让主角在雨夜压住发现线索的反应')
  await page.getByLabel('已知事实').first().fill('主角只看见桌面\n雨声很大')
  await page.getByLabel('禁止事实').first().fill('门外身份')

  await page.getByRole('button', { name: /生成蓝图/ }).click()
  await expectVisible(page.getByText('章节蓝图已生成'), 'blueprint generated message')
  await expectVisible(page.getByRole('heading', { name: '第3章 · 雨夜线索' }), 'active blueprint title')

  const detail = blueprintDetail(page)
  await detail.locator('label').filter({ hasText: '段落意图' }).locator('textarea').fill('用手部动作和雨声停顿表现压住反应。')
  await detail.getByRole('button', { name: /保存修订/ }).click()
  await expectVisible(page.getByText('蓝图已修订，需要重新评审和批准'), 'blueprint revised message')

  await detail.getByRole('button', { name: /^评审$/ }).click()
  await expectVisible(page.getByText('蓝图评审已完成'), 'blueprint reviewed message')
  await expectVisible(page.getByText('passed · 0.96'), 'passed blueprint review score')

  await detail.getByRole('button', { name: /^批准$/ }).click()
  await expectVisible(page.getByText('蓝图已批准'), 'blueprint approved message')

  await detail.getByRole('button', { name: /^绑定$/ }).click()
  await expectVisible(page.getByText('材料已绑定到蓝图'), 'material binding message')
  await expectVisible(detail.getByText('mat-001'), 'bound material id')
  await expectVisible(detail.getByText('feedback_boost 0.18'), 'binding score component')

  await detail.getByRole('button', { name: /^候选$/ }).click()
  await expectVisible(page.getByText('候选段落已生成'), 'draft generated message')
  await expectVisible(page.getByText('审计 passed · L1'), 'draft audit status')
  await expectVisible(page.getByText('雨声压过门缝里的动静'), 'draft candidate text')
}

async function runDefaultOrchestrationToFinalInsertionStop(page) {
  const panel = orchestrationPanel(page)

  await expectVisible(panel.getByText('AI 自动阶段'), 'orchestration automated stage copy')
  await expectVisible(panel.getByText(/生成蓝图.*绑定材料.*草稿审计/), 'orchestration automated stage details')
  await expectVisible(panel.getByText('作者决策'), 'orchestration author decision copy')
  await expectVisible(panel.getByText(/来源\/事实边界.*蓝图批准.*最终正文插入/), 'orchestration author decision details')
  await expectVisible(panel.getByText(/默认按故事上下文从可访问工作区语料检索材料/), 'default workspace corpus retrieval copy')

  await panel.getByRole('button', { name: /启动候选编排/ }).click()
  await expectVisible(page.getByText('编排已启动，等待确认来源与事实边界'), 'orchestration started message')
  await expectVisible(panel.getByText('确认来源与事实边界', { exact: true }), 'source confirmation decision')
  await expectVisible(panel.getByText('检索策略'), 'corpus search policy heading')
  await expectVisible(panel.getByText('story_context'), 'story context search policy')
  await expectVisible(panel.getByText('可访问工作区语料', { exact: true }), 'workspace corpus search scope')
  await expectVisible(panel.getByText('每节拍最多 3'), 'max results policy')
  await expectVisible(panel.getByText('授权 user_provided, unknown'), 'license status policy')
  await expectVisible(panel.getByText('未限制到已选锚点'), 'unrestricted anchor policy')
  await panel.getByRole('button', { name: /^确认$/ }).click()

  await expectVisible(page.getByText('编排已继续'), 'orchestration resumed message')
  await expectVisible(panel.getByText('批准蓝图', { exact: true }).first(), 'blueprint approval decision')
  await expectVisible(panel.getByText('章节功能'), 'approval summary chapter function')
  await expectVisible(panel.getByText('POV', { exact: true }), 'approval summary pov label')
  await expectVisible(panel.getByText('事实边界', { exact: true }), 'approval summary fact boundary label')
  await expectVisible(panel.getByText('情绪轨迹', { exact: true }), 'approval summary emotion label')
  await expectVisible(panel.getByText('材料计划', { exact: true }), 'approval summary material plan label')
  await expectVisible(panel.getByText('改写预算', { exact: true }), 'approval summary rewrite budget label')
  await expectVisible(panel.getByText('高风险', { exact: true }), 'approval summary high risk label')
  await expectVisible(panel.getByText('close / 主角'), 'approval summary pov value')
  await expectVisible(panel.getByText('不新增门外身份'), 'approval summary fact boundary value')
  await expectVisible(panel.getByText('警觉 -> 克制 -> 暂缓确认'), 'approval summary emotion value')
  await expectVisible(panel.getByText('使用工作区参考材料中的克制动作证据。'), 'approval summary material plan value')
  await expectVisible(panel.getByText('L1'), 'approval summary rewrite budget value')
  await expectVisible(panel.getByText('无高风险'), 'approval summary empty high risk value')
  await panel.getByRole('button', { name: /^确认$/ }).click()

  await expectVisible(panel.getByText('候选已就绪'), 'final insertion stop badge')
  await expectVisible(panel.getByText('候选与审计已就绪。最终正文仍需作者单独处理。'), 'manual insertion copy')
  await expectVisible(panel.getByText('candidate-run-001'), 'orchestration candidate id')
  await expectVisible(panel.getByText('waiting_for_decision · final_insertion_required'), 'final insertion run status')

  const finalConfirmCount = await panel.getByRole('button', { name: /^确认$/ }).count()
  assert.equal(finalConfirmCount, 0, 'final insertion decision must not expose a resume confirmation button')
}

async function verifyStaleBlueprintIsReadOnly(page) {
  await page.getByRole('button', { name: /第9章 · 过期蓝图/ }).click()
  await expectVisible(page.getByText(/章节规划已变化。此蓝图保留为只读对比/), 'stale blueprint warning')

  const detail = blueprintDetail(page)
  for (const buttonName of [/^评审$/, /^批准$/, /^绑定$/, /^候选$/, /保存修订/]) {
    await assertDisabled(detail.getByRole('button', { name: buttonName }), `stale button ${buttonName.toString()}`)
  }
  await assertHasDisabledAttribute(detail.locator('fieldset').first(), 'stale revision fieldset')
}

async function verifyBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__referenceAnchorMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'CreateReferenceAnchor',
    'PromoteReferenceAnchorToWorkspaceCorpus',
    'UpdateReferenceAnchorMetadata',
    'UpdateReferenceMaterialTags',
    'RebuildReferenceAnchor',
    'SearchReferenceMaterials',
    'GenerateReferenceChapterBlueprint',
    'ReviseReferenceChapterBlueprint',
    'ReviewReferenceChapterBlueprint',
    'ApproveReferenceChapterBlueprint',
    'BindReferenceBlueprintMaterials',
    'GenerateReferenceAnchoredDraft',
    'StartReferenceOrchestrationRun',
    'ResumeReferenceOrchestrationRun',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'reference-anchor workflow must not call SaveContent')

  const createCall = calls.find((call) => call.method === 'CreateReferenceAnchor')
  assert(createCall, 'missing CreateReferenceAnchor call')
  assert.equal(createCall.args[0].visibility, 'private', 'anchor create payload must start as per-novel private visibility')
  assert.equal(createCall.args[0].source_trust, 'imported', 'anchor create payload must include source trust')
  assert.deepEqual(createCall.args[0].user_tags, ['雨夜', '动作克制'], 'anchor create payload must include user tags')

  const promoteCall = calls.find((call) => call.method === 'PromoteReferenceAnchorToWorkspaceCorpus')
  assert(promoteCall, 'missing PromoteReferenceAnchorToWorkspaceCorpus call')
  assert.equal(promoteCall.args[0].novel_id, 42, 'anchor promote payload must include novel id')
  assert.equal(promoteCall.args[0].anchor_id, 101, 'anchor promote payload must include anchor id')

  const metadataCall = calls.find((call) => call.method === 'UpdateReferenceAnchorMetadata')
  assert(metadataCall, 'missing UpdateReferenceAnchorMetadata call')
  assert.equal(metadataCall.args[0].novel_id, 42, 'anchor metadata update payload must include novel id')
  assert.equal(metadataCall.args[0].anchor_id, 101, 'anchor metadata update payload must include anchor id')
  assert.equal(metadataCall.args[0].title, '雨夜动作语料库', 'anchor metadata update payload must include title')
  assert.equal(metadataCall.args[0].author, 'Metadata Curator', 'anchor metadata update payload must include author')
  assert.equal(metadataCall.args[0].license_status, 'licensed', 'anchor metadata update payload must include license status')
  assert.equal(metadataCall.args[0].visibility, 'workspace', 'anchor metadata update payload must preserve workspace visibility')
  assert.equal(metadataCall.args[0].source_trust, 'user_verified', 'anchor metadata update payload must include source trust')
  assert.deepEqual(metadataCall.args[0].user_tags, ['雨夜', '动作克制', '精选'], 'anchor metadata update payload must include user tags')

  const materialTagCall = calls.find((call) => call.method === 'UpdateReferenceMaterialTags')
  assert(materialTagCall, 'missing UpdateReferenceMaterialTags call')
  assert.equal(materialTagCall.args[0].novel_id, 42, 'material tag update payload must include novel id')
  assert.equal(materialTagCall.args[0].material_id, 'mat-001', 'material tag update payload must include material id')
  assert.equal(materialTagCall.args[0].function_tag, 'object_subtext', 'material tag update payload must include function tag')
  assert.equal(materialTagCall.args[0].emotion_tag, 'contained_tension', 'material tag update payload must include emotion tag')
  assert.equal(materialTagCall.args[0].scene_tag, 'rain_threshold', 'material tag update payload must include scene tag')
  assert.equal(materialTagCall.args[0].pov_tag, 'limited_close', 'material tag update payload must include pov tag')
  assert.equal(materialTagCall.args[0].technique_tag, 'delayed_reaction', 'material tag update payload must include technique tag')
  assert.equal(materialTagCall.args[0].origin, 'ui', 'material tag update payload must mark UI origin')
  assert.equal(materialTagCall.args[0].note, 'corpus material browser correction', 'material tag update payload must include correction note')

  const startCall = calls.find((call) => call.method === 'StartReferenceOrchestrationRun')
  assert(startCall, 'missing StartReferenceOrchestrationRun call')
  assert.equal(startCall.args[0].anchor_ids, null, 'default orchestration must not require selected anchor ids')
  assert.deepEqual(startCall.args[0].corpus_search_policy.include_anchor_ids, [], 'default orchestration must not pin include anchors')
  assert.deepEqual(startCall.args[0].corpus_search_policy.exclude_anchor_ids, [], 'default orchestration must not pin exclude anchors')
  assert.equal(startCall.args[0].corpus_search_policy.mode, 'story_context', 'default orchestration must use story-context corpus search')

  const materialPreviewCall = calls.find((call) =>
    call.method === 'SearchReferenceMaterials' &&
    Array.isArray(call.args[0]?.anchor_ids) &&
    call.args[0].anchor_ids.length === 1 &&
    call.args[0].anchor_ids[0] === 101 &&
    call.args[0].page === 1 &&
    call.args[0].size === 5)
  assert(materialPreviewCall, 'missing anchor material preview search call')

  const materialPreviewNextPageCall = calls.find((call) =>
    call.method === 'SearchReferenceMaterials' &&
    Array.isArray(call.args[0]?.anchor_ids) &&
    call.args[0].anchor_ids.length === 1 &&
    call.args[0].anchor_ids[0] === 101 &&
    call.args[0].page === 2 &&
    call.args[0].size === 5)
  assert(materialPreviewNextPageCall, 'missing anchor material preview next-page search call')

  const searchCall = calls.find((call) =>
    call.method === 'SearchReferenceMaterials' &&
    Array.isArray(call.args[0]?.prose_duties) &&
    call.args[0].prose_duties.includes('source_backed_detail'))
  assert(searchCall, 'missing manual SearchReferenceMaterials call')
  assert.deepEqual(searchCall.args[0].prose_duties, ['source_backed_detail'], 'manual story-context material search must pass prose duties')
}

function blueprintDetail(page) {
  return page.getByTestId('reference-blueprint-detail')
}

function orchestrationPanel(page) {
  return page.locator('.rounded-lg').filter({ hasText: '默认编排' }).first()
}

async function expectVisible(locator, description) {
  await locator.waitFor({ state: 'visible', timeout: 10_000 }).catch((error) => {
    throw new Error(`Expected visible: ${description}`, { cause: error })
  })
}

async function expectHidden(locator, description) {
  await locator.waitFor({ state: 'hidden', timeout: 10_000 }).catch((error) => {
    throw new Error(`Expected hidden: ${description}`, { cause: error })
  })
}

async function openAdvancedMode(page) {
  const button = page.getByRole('button', { name: '打开高级模式' })
  await expectVisible(button, 'open advanced mode button')
  await button.click()
  await expectVisible(page.getByText('材料搜索', { exact: true }), 'material search visible in advanced mode')
  await expectVisible(page.getByRole('button', { name: /生成蓝图/ }), 'manual blueprint generation visible in advanced mode')
  await expectVisible(blueprintDetail(page), 'manual blueprint detail visible in advanced mode')
}

async function assertDisabled(locator, description) {
  await locator.waitFor({ state: 'attached', timeout: 10_000 })
  assert.equal(await locator.isDisabled(), true, `${description} should be disabled`)
}

async function assertHasDisabledAttribute(locator, description) {
  await locator.waitFor({ state: 'attached', timeout: 10_000 })
  assert.equal(await locator.evaluate((element) => element.hasAttribute('disabled')), true, `${description} should have a disabled attribute`)
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
  console.log(`[reference-anchor mock] ${message}`)
}

function installReferenceAnchorMockBridge() {
  const now = '2026-07-05T12:00:00.000Z'
  const receivers = new Set()
  const state = {
    calls: [],
    nextAnchorId: 101,
    nextBlueprintId: 501,
    nextEventId: 1,
    anchors: [],
    blueprints: {},
    runs: [],
    events: {},
  }

  state.blueprints[902] = makeBlueprint(902, {
    chapter_number: 9,
    title: '过期蓝图',
    status: 'stale',
    latest_review: makeReview(902, 'review-stale-902'),
  })

  Object.defineProperty(window, '__referenceAnchorMockState', {
    configurable: true,
    value: state,
  })

  Object.defineProperty(window, 'external', {
    configurable: true,
    value: {
      sendMessage(message) {
        const envelope = JSON.parse(String(message))
        if (envelope.kind === 'request') {
          window.setTimeout(() => handleRequest(envelope), 0)
        }
      },
      receiveMessage(callback) {
        receivers.add(callback)
      },
    },
  })

  function handleRequest(envelope) {
    try {
      const args = Array.isArray(envelope.payload?.args) ? envelope.payload.args : []
      state.calls.push({ method: envelope.method, args })
      if (envelope.method === 'SaveContent') {
        throw new Error('SaveContent is forbidden in the reference-anchor mock workflow.')
      }
      const result = route(envelope.method, args)
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

  function route(method, args) {
    switch (method) {
      case 'IsInitialized': return true
      case 'GetSettings': return settings()
      case 'GetPlatform': return { os: 'win32' }
      case 'runtime.window.isMaximized': return false
      case 'runtime.window.minimize':
      case 'runtime.window.toggleMaximize':
      case 'runtime.app.quit':
      case 'SetActiveNovel':
      case 'SetLastSession':
      case 'SetSelectedModel':
      case 'SetReasoningEffort':
      case 'SetApprovalMode':
      case 'SetChatPanelWidth':
        return null
      case 'GetNovels': return [novel()]
      case 'GetCover': return null
      case 'GetChapters': return []
      case 'GetModels': return [model()]
      case 'GetSessions': return pageResult([])
      case 'ListSlashCommands': return []
      case 'GetReferenceAnchors': return state.anchors
      case 'CreateReferenceAnchor': return createReferenceAnchor(args[0])
      case 'PromoteReferenceAnchorToWorkspaceCorpus': return promoteReferenceAnchor(args[0])
      case 'UpdateReferenceAnchorMetadata': return updateReferenceAnchorMetadata(args[0])
      case 'UpdateReferenceMaterialTags': return updateReferenceMaterialTags(args[0])
      case 'RebuildReferenceAnchor': return rebuildReferenceAnchor(args[1])
      case 'SearchReferenceMaterials': return searchReferenceMaterials(args[0])
      case 'GetReferenceChapterBlueprints': return Object.values(state.blueprints).map(toBlueprintSummary)
      case 'GetReferenceChapterBlueprint': return state.blueprints[String(args[1])] ?? null
      case 'GenerateReferenceChapterBlueprint': return generateBlueprint(args[0])
      case 'ReviseReferenceChapterBlueprint': return reviseBlueprint(args[0])
      case 'ReviewReferenceChapterBlueprint': return reviewBlueprint(args[0])
      case 'ApproveReferenceChapterBlueprint': return approveBlueprint(args[0])
      case 'BindReferenceBlueprintMaterials': return bindMaterials(args[0])
      case 'GenerateReferenceAnchoredDraft': return generateDraft(args[0])
      case 'StartReferenceOrchestrationRun': return startRun(args[0])
      case 'GetReferenceOrchestrationRuns': return state.runs
      case 'GetReferenceOrchestrationRun': return state.runs.find((run) => run.run_id === args[1]) ?? null
      case 'GetReferenceOrchestrationRunEvents': return state.events[args[1]] ?? []
      case 'ResumeReferenceOrchestrationRun': return resumeRun(args[0])
      case 'CancelReferenceOrchestrationRun': return cancelRun(args[0])
      default:
        return defaultValueFor(method)
    }
  }

  function settings() {
    return {
      ID: 1,
      last_novel_id: 42,
      selected_model_key: 'mock/gpt',
      reasoning_effort: 'high',
      approval_mode: 'manual',
      chat_panel_width: 360,
      last_session_id: '',
      user_name: 'Mock User',
    }
  }

  function novel() {
    return {
      id: 42,
      title: '桥接验收小说',
      genre: '悬疑',
      description: 'Reference-anchor browser harness novel',
      created_at: now,
      updated_at: now,
    }
  }

  function model() {
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

  function createReferenceAnchor(input) {
    const anchor = {
      anchor_id: state.nextAnchorId++,
      novel_id: input.novel_id,
      title: input.title,
      author: input.author ?? '',
      source_path: input.source_path,
      source_kind: input.source_kind,
      license_status: input.license_status,
      source_file_hash: 'hash-anchor-001',
      build_version: 'mock-reference-anchor-v1',
      status: 'ready',
      created_at: now,
      updated_at: now,
      visibility: input.visibility ?? 'private',
      source_trust: input.source_trust ?? 'user_verified',
      user_tags: Array.isArray(input.user_tags) ? input.user_tags : [],
      owner_scope: input.visibility === 'workspace' ? 'workspace_corpus' : 'novel',
      owner_novel_id: input.visibility === 'workspace' ? null : input.novel_id,
    }
    state.anchors = [anchor]
    return anchor
  }

  function promoteReferenceAnchor(input) {
    const anchor = state.anchors.find((item) => item.anchor_id === input.anchor_id)
    if (!anchor) {
      throw new Error('Reference anchor does not exist for this novel.')
    }

    anchor.novel_id = 0
    anchor.visibility = 'workspace'
    anchor.source_trust = input.source_trust ?? anchor.source_trust
    anchor.user_tags = Array.isArray(input.user_tags) ? input.user_tags : anchor.user_tags
    anchor.owner_scope = 'workspace_corpus'
    anchor.owner_novel_id = null
    anchor.updated_at = now
    return anchor
  }

  function updateReferenceAnchorMetadata(input) {
    const anchor = state.anchors.find((item) => item.anchor_id === input.anchor_id)
    if (!anchor) {
      throw new Error('Reference anchor does not exist for this novel.')
    }

    anchor.title = input.title
    anchor.author = input.author ?? ''
    anchor.license_status = input.license_status
    anchor.visibility = input.visibility
    anchor.source_trust = input.source_trust
    anchor.user_tags = Array.isArray(input.user_tags) ? input.user_tags : []
    anchor.owner_scope = input.visibility === 'workspace' ? 'workspace_corpus' : 'novel'
    anchor.owner_novel_id = input.visibility === 'workspace' ? null : input.novel_id
    anchor.novel_id = input.visibility === 'workspace' ? 0 : input.novel_id
    anchor.updated_at = now
    return anchor
  }

  function rebuildReferenceAnchor(anchorId) {
    const anchor = state.anchors.find((item) => item.anchor_id === anchorId)
    if (anchor) {
      anchor.status = 'ready'
      anchor.updated_at = now
    }
    return {
      novel_id: 42,
      anchor_id: anchorId,
      status: 'ready',
      stage: 'completed',
      source_segment_count: 3,
      material_count: 1,
      slot_count: 2,
      vector_count: 0,
      last_error: '',
      updated_at: now,
    }
  }

  function searchReferenceMaterials(input) {
    const isAnchorScopedPreview = Array.isArray(input.anchor_ids) && input.anchor_ids.length === 1 && input.query === '' && input.size === 5
    if (!isAnchorScopedPreview) {
      return pagedResult([material(1)], input.page ?? 1, input.size ?? 10, 1)
    }

    const page = input.page ?? 1
    const size = input.size ?? 5
    if (page === 2) {
      return pagedResult([material(6)], page, size, 6)
    }

    return pagedResult([1, 2, 3, 4, 5].map(material), page, size, 6)
  }

  function updateReferenceMaterialTags(input) {
    return {
      ...materialById(input.material_id),
      function_tag: input.function_tag ?? 'emotion_evidence',
      emotion_tag: input.emotion_tag ?? 'restrained',
      scene_tag: input.scene_tag ?? 'rain_room',
      pov_tag: input.pov_tag ?? 'close',
      technique_tag: input.technique_tag ?? 'subtext',
      user_verified: true,
    }
  }

  function materialById(materialId) {
    const match = String(materialId).match(/(\d+)$/)
    return material(match ? Number.parseInt(match[1], 10) : 1)
  }

  function material(index = 1) {
    return {
      material_id: `mat-${String(index).padStart(3, '0')}`,
      anchor_id: state.anchors[0]?.anchor_id ?? 101,
      source_segment_id: `seg-${String(index).padStart(3, '0')}`,
      material_type: 'sentence',
      function_tag: 'emotion_evidence',
      emotion_tag: 'restrained',
      scene_tag: 'rain_room',
      pov_tag: 'close',
      technique_tag: 'subtext',
      function_confidence: 0.97,
      emotion_confidence: 0.93,
      pov_confidence: 0.91,
      text: materialText(index),
      source_hash: `hash-material-${String(index).padStart(3, '0')}`,
      extractor_version: 'mock-extractor-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.92,
        function: 0.83,
        prose_duty: 0.75,
        feedback_boost: 0.18,
      },
    }
  }

  function materialText(index) {
    if (index === 1) return '把杯子推远，杯底在木桌上留下半圈水痕。'
    if (index === 6) return '雨水从伞沿断续落下，像有人在门外迟疑。'
    return `第 ${index} 条克制动作材料。`
  }

  function generateBlueprint(input) {
    const blueprint = makeBlueprint(state.nextBlueprintId++, {
      chapter_number: input.chapter_number,
      title: input.title || '雨夜线索',
      status: 'draft',
      known_facts: input.known_facts ?? [],
      forbidden_facts: input.forbidden_facts ?? [],
      primary_anchor_id: state.anchors[0]?.anchor_id ?? 0,
      latest_review: null,
    })
    state.blueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint
  }

  function reviseBlueprint(input) {
    const blueprint = cloneBlueprint(input.blueprint_id)
    for (const change of input.changes ?? []) {
      if (change.field_path.endsWith('paragraph_intention')) {
        blueprint.beats[0].paragraph_intention = change.new_value
      }
      if (change.field_path === 'known_facts') {
        blueprint.known_facts = JSON.parse(change.new_value)
      }
      if (change.field_path === 'forbidden_facts') {
        blueprint.forbidden_facts = JSON.parse(change.new_value)
      }
    }
    blueprint.status = 'draft'
    blueprint.blueprint_version += 1
    blueprint.latest_review = null
    blueprint.updated_at = now
    state.blueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint
  }

  function reviewBlueprint(input) {
    const blueprint = cloneBlueprint(input.blueprint_id)
    blueprint.status = 'reviewed'
    blueprint.latest_review = makeReview(blueprint.blueprint_id, `review-${blueprint.blueprint_id}`)
    blueprint.updated_at = now
    state.blueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint.latest_review
  }

  function approveBlueprint(input) {
    const blueprint = cloneBlueprint(input.blueprint_id)
    blueprint.status = 'approved'
    blueprint.updated_at = now
    state.blueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint
  }

  function bindMaterials(input) {
    const blueprint = cloneBlueprint(input.blueprint_id)
    blueprint.status = 'material_bound'
    blueprint.updated_at = now
    state.blueprints[String(blueprint.blueprint_id)] = blueprint
    return {
      blueprint_id: blueprint.blueprint_id,
      links: [bindingLink(blueprint.blueprint_id, blueprint.beats[0].beat_id)],
    }
  }

  function generateDraft(input) {
    const blueprint = state.blueprints[String(input.blueprint_id)]
    return {
      blueprint_id: input.blueprint_id,
      candidates: [
        {
          candidate_id: 'candidate-001',
          blueprint_id: input.blueprint_id,
          beat_id: blueprint.beats[0].beat_id,
          material_id: 'mat-001',
          rewrite_level: 'L1',
          text: '雨声压过门缝里的动静，她把杯子推远，指尖停在那半圈水痕旁，没有立刻抬头。',
          changed_slots: [{ slot_name: 'object', value: '杯子' }],
          non_slot_edits: ['调整为当前 POV 的感官证据。'],
          audit_status: 'passed',
          created_at: now,
        },
      ],
      audit: {
        audit_id: 'audit-001',
        blueprint_id: input.blueprint_id,
        status: 'passed',
        rewrite_level: 'L1',
        provenance_errors: [],
        blueprint_errors: [],
        unsupported_fact_errors: [],
        pov_errors: [],
        ai_prose_risks: [],
        required_fixes: [],
        audited_at: now,
      },
    }
  }

  function startRun(input) {
    const run = {
      run_id: 'run-001',
      novel_id: input.novel_id,
      chapter_number: input.chapter_number,
      status: 'waiting_for_decision',
      stage: 'source_confirmation',
      chapter_goal: input.chapter_goal ?? '',
      known_facts: input.known_facts ?? [],
      forbidden_facts: input.forbidden_facts ?? [],
      anchor_ids: input.anchor_ids ?? [],
      corpus_search_policy: input.corpus_search_policy,
      blueprint_id: 0,
      review_id: '',
      candidate_ids: [],
      current_decision: decision('confirm_source_and_facts', '确认来源与事实边界后继续自动生成蓝图。', ['确认授权状态', '确认事实边界']),
      last_stop_reason: 'source_confirmation_required',
      error_message: '',
      created_at: now,
      updated_at: now,
    }
    state.runs = [run]
    state.events[run.run_id] = []
    addEvent(run, 'run_started', '编排启动，等待来源与事实边界确认。')
    addEvent(run, 'required_decision', '需要作者确认来源与事实边界。')
    return run
  }

  function resumeRun(input) {
    const run = state.runs.find((item) => item.run_id === input.run_id)
    if (!run) throw new Error(`Unknown run ${input.run_id}`)

    addEvent(run, 'user_resumed', `作者确认 ${input.decision_type}。`, input.decision_type)

    if (input.decision_type === 'confirm_source_and_facts') {
      const blueprint = makeBlueprint(701, {
        chapter_number: run.chapter_number,
        title: '默认编排蓝图',
        status: 'reviewed',
        known_facts: run.known_facts,
        forbidden_facts: run.forbidden_facts,
        primary_anchor_id: state.anchors[0]?.anchor_id ?? 0,
        latest_review: makeReview(701, 'review-run-701'),
      })
      state.blueprints[String(blueprint.blueprint_id)] = blueprint
      run.stage = 'blueprint_approval'
      run.blueprint_id = blueprint.blueprint_id
      run.review_id = blueprint.latest_review.review_id
      run.current_decision = decision('approve_blueprint', '蓝图评审通过，批准后自动绑定材料、生成候选并运行审计。', ['审批当前分析合同'])
      run.last_stop_reason = 'blueprint_approval_required'
      run.updated_at = now
      addEvent(run, 'gate_passed', '蓝图生成和确定性评审通过。')
      addEvent(run, 'required_decision', '等待作者批准蓝图。', 'approve_blueprint')
      return run
    }

    if (input.decision_type === 'approve_blueprint') {
      const blueprint = cloneBlueprint(run.blueprint_id)
      blueprint.status = 'material_bound'
      state.blueprints[String(blueprint.blueprint_id)] = blueprint
      run.stage = 'final_insertion'
      run.status = 'waiting_for_decision'
      run.candidate_ids = ['candidate-run-001']
      run.current_decision = decision('approve_final_insertion', '候选与审计已就绪。最终正文仍需作者单独处理。', ['复制或进入独立正文编辑流程'])
      run.last_stop_reason = 'final_insertion_required'
      run.updated_at = now
      addEvent(run, 'gate_passed', '材料绑定、候选生成和草稿审计通过。')
      addEvent(run, 'required_decision', 'final_insertion_required', 'approve_final_insertion')
      return run
    }

    run.status = 'failed'
    run.stage = 'failed'
    run.current_decision = null
    run.last_stop_reason = 'resolved_high_risk_stop'
    run.updated_at = now
    addEvent(run, 'failed', '高风险停止已确认，运行结束。', input.decision_type)
    return run
  }

  function cancelRun(input) {
    const run = state.runs.find((item) => item.run_id === input.run_id)
    if (!run) throw new Error(`Unknown run ${input.run_id}`)
    run.status = 'cancelled'
    run.current_decision = null
    run.last_stop_reason = input.reason
    run.updated_at = now
    addEvent(run, 'cancelled', input.reason)
    return run
  }

  function addEvent(run, eventType, summary, decisionType = '') {
    state.events[run.run_id].push({
      event_id: state.nextEventId++,
      run_id: run.run_id,
      novel_id: run.novel_id,
      event_type: eventType,
      stage: run.stage,
      status: run.status,
      stop_reason: run.last_stop_reason,
      decision_type: decisionType,
      summary,
      created_at: now,
    })
  }

  function decision(type, summary, requiredActions) {
    return {
      decision_type: type,
      stop_reason: type,
      summary,
      required_actions: requiredActions,
      approval_summary: {
        chapter_function: '用受限 POV 处理雨夜线索发现。',
        pov: 'close / 主角',
        fact_boundary_changes: ['不新增门外身份'],
        emotional_trajectory: '警觉 -> 克制 -> 暂缓确认',
        material_use_plan: '使用工作区参考材料中的克制动作证据。',
        rewrite_budget: 'L1',
        high_risk_findings: [],
      },
      proposed_blueprint_revision: null,
    }
  }

  function makeBlueprint(blueprintId, overrides = {}) {
    const beat = makeBeat()
    return {
      blueprint_id: blueprintId,
      novel_id: 42,
      chapter_number: overrides.chapter_number ?? 3,
      title: overrides.title ?? '雨夜线索',
      status: overrides.status ?? 'draft',
      source_plan_scope: 'chapter',
      source_plan_hash: `plan-${blueprintId}`,
      context_hash: `context-${blueprintId}`,
      analysis_contract_hash: `contract-${blueprintId}`,
      blueprint_version: 1,
      build_version: 'mock-blueprint-v1',
      parent_blueprint_id: 0,
      primary_anchor_id: overrides.primary_anchor_id ?? 0,
      chapter_function: '让主角发现线索但压住反应。',
      logic_analysis: track('logic', ['线索出现', '反应被压住']),
      emotion_analysis: track('emotion', ['警觉', '克制']),
      narration_analysis: track('narration', ['close POV', '感官证据']),
      character_analysis: track('character', ['主角谨慎']),
      reference_analysis: track('reference', ['使用克制动作材料']),
      transition_plan: track('transition', ['由外部雨声转入内心停顿']),
      execution_contract: {
        track: 'execution',
        summary: '以手部动作和声音停顿执行。',
        paragraph_intentions: [beat.paragraph_intention],
        execution_modes: [beat.execution_mode],
        anti_screenplay_duties: [beat.anti_screenplay_duty],
        source_backed_detail_targets: [beat.source_backed_detail_target],
        candidate_rejection_rules: [beat.candidate_rejection_rule],
      },
      previous_state: '主角进入房间。',
      final_state: '主角暂时压住线索判断。',
      final_hook: '门外身份仍不可揭示。',
      global_pov: '主角',
      global_narrative_distance: 'close',
      known_facts: overrides.known_facts ?? ['主角只看见桌面', '雨声很大'],
      forbidden_facts: overrides.forbidden_facts ?? ['门外身份'],
      risk_flags: [],
      beats: [beat],
      latest_review: overrides.latest_review ?? null,
      created_at: now,
      updated_at: now,
    }
  }

  function makeBeat() {
    return {
      beat_id: 'beat-001',
      beat_index: 1,
      scene_index: 1,
      beat_type: 'scene',
      narrative_function: '用外部动作承载压住反应。',
      logic_premise: '主角只拥有桌面和雨声信息。',
      conflict_pressure: '线索可能存在但不能确认门外身份。',
      causality_in: '发现桌面痕迹。',
      causality_out: '选择暂不抬头。',
      transition_in: '雨声压过门缝动静。',
      transition_out: '视角停留在手部动作。',
      pov_character: '主角',
      narrative_distance: 'close',
      viewpoint_allowed_knowledge: ['桌面痕迹', '雨声'],
      viewpoint_forbidden_knowledge: ['门外身份'],
      character_states_before: ['警觉'],
      character_states_after: ['克制'],
      character_goals: ['确认线索但不暴露反应'],
      character_misbeliefs: ['门外动静可能只是雨声'],
      relationship_pressure: ['不能让旁人看出判断'],
      emotion_trigger: '桌面水痕',
      emotion_before: '警觉',
      emotion_after: '克制',
      suppressed_reaction: '没有立刻抬头',
      external_evidence: '把杯子推远',
      narration_strategy: 'close POV, restrict knowledge to sensory evidence.',
      rhythm_strategy: '先停顿，再外部动作。',
      paragraph_intention: '用杯子动作表现压住反应。',
      execution_mode: 'delayed_reaction',
      anti_screenplay_duty: '补足内心停顿和感官压力。',
      sensory_anchor_target: '雨声',
      subtext_plan: '用杯底水痕暗示线索。',
      source_backed_detail_target: '杯底半圈水痕',
      candidate_rejection_rule: '拒绝直接揭示门外身份或纯动作走位。',
      scene_facts: ['桌上有杯子', '雨声很大'],
      forbidden_facts: ['门外身份'],
      reference_query: {
        query: '克制动作 杯子 推远 雨声',
        material_types: ['sentence'],
        emotion_tags: ['restrained'],
        function_tags: ['emotion_evidence'],
        pov_tags: ['close'],
        technique_tags: ['subtext'],
        max_results: 3,
      },
      required_material_types: ['sentence'],
      max_rewrite_level: 'L1',
      slot_plan: [{ slot_name: 'object', value: '杯子' }],
      locked_phrase_policy: '不锁定原句。',
      no_reuse_reason: '',
      prose_duties: ['delayed_reaction', 'subtext', 'source_backed_detail'],
      risk_flags: [],
    }
  }

  function track(name, points) {
    return {
      track: name,
      summary: `${name} summary`,
      points,
    }
  }

  function makeReview(blueprintId, reviewId) {
    return {
      review_id: reviewId,
      blueprint_id: blueprintId,
      context_hash: `context-${blueprintId}`,
      source_plan_hash: `plan-${blueprintId}`,
      analysis_contract_hash: `contract-${blueprintId}`,
      review_version: 1,
      status: 'passed',
      score: 0.96,
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

  function bindingLink(blueprintId, beatId) {
    return {
      link_id: 'link-001',
      blueprint_id: blueprintId,
      beat_id: beatId,
      material_id: 'mat-001',
      intended_use: 'external evidence and subtext',
      max_rewrite_level: 'L1',
      selected: true,
      score: 0.94,
      score_components: {
        lexical: 0.92,
        function: 0.84,
        feedback_boost: 0.18,
      },
      fit_explanation: 'Matches restrained emotion evidence, close POV, and source-backed detail.',
      created_at: now,
    }
  }

  function cloneBlueprint(blueprintId) {
    const blueprint = state.blueprints[String(blueprintId)]
    if (!blueprint) throw new Error(`Unknown blueprint ${blueprintId}`)
    return JSON.parse(JSON.stringify(blueprint))
  }

  function toBlueprintSummary(blueprint) {
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
