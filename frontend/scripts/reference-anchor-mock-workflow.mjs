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
const outputDir = path.join(repoRoot, 'output', 'playwright', 'phase13', 'reference-anchor')

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
    await expectVisible(page.getByRole('heading', { name: '风格画像库' }), 'style profile library heading')
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

    logStep('failure and recovery workflow')
    await verifyReviewAndAuditFailureRecovery(page)
    await page.screenshot({ path: path.join(outputDir, 'reference-anchor-04-failure-recovery.png'), fullPage: true })

    logStep('stale blueprint workflow')
    await verifyStaleBlueprintIsReadOnly(page)
    await page.screenshot({ path: path.join(outputDir, 'reference-anchor-05-stale-blueprint.png'), fullPage: true })

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
  const importPanel = page.getByTestId('reference-import-panel')

  await importPanel.getByPlaceholder('参考书名').fill('雨夜动作参考')
  await importPanel.getByPlaceholder('可选').fill('Mock Author')
  await importPanel.getByLabel('本地路径').fill('D:\\books\\rain-reference.md')
  await importPanel.getByLabel('来源可信度').selectOption('imported')
  await importPanel.getByLabel('用户标签').fill('雨夜;动作克制')
  await importPanel.getByRole('button', { name: /^创建$/ }).click()
  await expectVisible(page.getByText('参考锚点已创建'), 'anchor created message')
  await expectVisible(page.getByText('雨夜动作参考'), 'created anchor title')
  await expectVisible(page.getByText('private · imported · novel'), 'created private anchor metadata')

  await page.getByRole('button', { name: /提升 雨夜动作参考 为工作区语料/ }).click()
  await expectVisible(page.getByText('已提升为工作区语料'), 'anchor promoted message')
  await expectVisible(page.getByText('workspace · imported · workspace_corpus'), 'created anchor corpus metadata')
  const createdAnchorRow = page.getByTestId('reference-anchor-row').filter({ hasText: '雨夜动作参考' }).first()
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
  const firstMaterial = materialPreview.getByTestId('reference-anchor-material-card').filter({ hasText: 'mat-001' }).first()
  await expectVisible(firstMaterial.getByText('mat-001'), 'anchor material preview id')
  await expectVisible(firstMaterial.getByText('把杯子推远，杯底在木桌上留下半圈水痕。'), 'anchor material preview text')
  await expectVisible(firstMaterial.getByText('seg-001'), 'anchor material preview source segment provenance')
  await expectVisible(firstMaterial.getByText('hash-material-001'), 'anchor material preview source hash provenance')
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

  await page.getByRole('button', { name: '选择当前材料' }).click()
  await expectVisible(page.getByText('已选 5 条材料'), 'selected current material page count')
  await page.getByLabel('批量材料功能标签').fill('spatial_pressure')
  await page.getByLabel('批量材料情绪标签').fill('coiled_alarm')
  await page.getByLabel('批量材料场景标签').fill('threshold_room')
  await page.getByLabel('批量材料 POV 标签').fill('shared_close')
  await page.getByLabel('批量材料技法标签').fill('object_afterbeat')
  await page.getByRole('button', { name: /^批量保存标签$/ }).click()
  await expectVisible(page.getByText('已批量校正 5 条材料标签'), 'bulk material tag update message')
  await expectVisible(page.getByText('已选 0 条材料'), 'bulk material selection cleared')
  await expectVisible(firstMaterial.getByText('spatial_pressure'), 'bulk corrected material function tag')
  await expectVisible(firstMaterial.getByText('shared_close'), 'bulk corrected material pov tag')

  await page.getByLabel('材料筛选').fill('杯子')
  await expectVisible(firstMaterial.getByText('mat-001'), 'filtered anchor material preview id')
  await expectHidden(materialPreview.getByText('mat-002'), 'filtered anchor material hides non-matching row')
  await page.getByLabel('材料筛选').fill('不存在材料')
  await expectVisible(page.getByText('没有匹配材料'), 'anchor material filtered empty state')
  await page.getByLabel('材料筛选').fill('')
  await page.getByLabel('材料排序').selectOption('score_desc')
  await expectVisible(materialPreview.getByTestId('reference-anchor-material-card').first().getByText('mat-004'), 'anchor material score sort first row')
  await page.getByLabel('材料排序').selectOption('material_id_asc')
  await expectVisible(materialPreview.getByTestId('reference-anchor-material-card').first().getByText('mat-001'), 'anchor material id sort first row')

  await page.getByRole('button', { name: /下一页材料/ }).click()
  await expectVisible(page.getByText('mat-006'), 'anchor material preview second page id')
  await expectVisible(page.getByText('雨水从伞沿断续落下，像有人在门外迟疑。'), 'anchor material preview second page text')

  const libraryMaterialPanel = page.getByTestId('reference-material-library')
  await expectVisible(libraryMaterialPanel.getByRole('heading', { name: '材料库' }), 'corpus material library heading')
  await libraryMaterialPanel.getByLabel('材料库搜索').fill('把杯子推远')
  await libraryMaterialPanel.getByLabel('材料库文体职责').fill('source_backed_detail')
  await libraryMaterialPanel.getByRole('button', { name: /^检索材料库$/ }).click()
  await expectVisible(libraryMaterialPanel.getByText('第 1 / 2 页 · 11 条材料'), 'corpus material library result count')
  await expectVisible(libraryMaterialPanel.getByText('mat-001'), 'corpus material library id')
  await expectVisible(libraryMaterialPanel.getByText('把杯子推远，杯底在木桌上留下半圈水痕。'), 'corpus material library text')
  const libraryFirstMaterial = libraryMaterialPanel.getByTestId('reference-material-library-card').filter({ hasText: 'mat-001' }).first()
  await expectVisible(libraryFirstMaterial.getByText('seg-001'), 'corpus material library source segment provenance')
  await expectVisible(libraryFirstMaterial.getByText('hash-material-001'), 'corpus material library source hash provenance')
  await expectVisible(libraryFirstMaterial.getByText('prose_duty 0.75'), 'corpus material library prose-duty score')
  await libraryMaterialPanel.getByLabel('材料库页内筛选').fill('杯子')
  await expectVisible(libraryMaterialPanel.getByText('mat-001'), 'filtered corpus material library id')
  await expectHidden(libraryMaterialPanel.getByText('mat-002'), 'filtered corpus material library hides non-matching row')
  await libraryMaterialPanel.getByLabel('材料库页内筛选').fill('不存在材料')
  await expectVisible(libraryMaterialPanel.getByText('没有匹配材料'), 'corpus material library filtered empty state')
  await libraryMaterialPanel.getByLabel('材料库页内筛选').fill('')
  await libraryMaterialPanel.getByLabel('材料库排序').selectOption('score_desc')
  await expectVisible(libraryMaterialPanel.locator('[aria-label="材料库结果"] > .rounded').first().getByText('mat-004'), 'corpus material library score sort first row')
  await libraryMaterialPanel.getByLabel('材料库排序').selectOption('material_id_asc')
  await expectVisible(libraryMaterialPanel.locator('[aria-label="材料库结果"] > .rounded').first().getByText('mat-001'), 'corpus material library id sort first row')
  await libraryMaterialPanel.getByLabel('材料库页内筛选').fill('杯子')
  await libraryMaterialPanel.getByRole('button', { name: /^选择当前材料$/ }).click()
  await expectVisible(libraryMaterialPanel.getByText('已选 1 条材料'), 'corpus material library selected filtered count')
  await libraryMaterialPanel.getByRole('button', { name: /^下一页$/ }).click()
  await expectVisible(libraryMaterialPanel.getByText('第 2 / 2 页 · 11 条材料'), 'corpus material library second page summary')
  await expectVisible(libraryMaterialPanel.getByText('已选 1 条材料 · 当前结果 0 条'), 'corpus material library cross-page selection count')
  await libraryMaterialPanel.getByLabel('材料库页内筛选').fill('')
  await expectVisible(libraryMaterialPanel.getByText('mat-006'), 'corpus material library second page material')
  await libraryMaterialPanel.getByRole('button', { name: /^选择当前材料$/ }).click()
  await expectVisible(libraryMaterialPanel.getByText('已选 2 条材料'), 'corpus material library selected cross-page count')
  await libraryMaterialPanel.getByLabel('材料库批量功能标签').fill('library_object_signal')
  await libraryMaterialPanel.getByLabel('材料库批量 POV 标签').fill('library_close')
  await libraryMaterialPanel.getByRole('button', { name: /^批量校正材料库$/ }).click()
  await expectVisible(page.getByText('材料库已批量校正 2 条材料标签'), 'corpus material library bulk update message')
  await expectVisible(libraryMaterialPanel.getByText('library_object_signal'), 'corpus material library corrected function tag')
  await expectVisible(libraryMaterialPanel.getByText('library_close'), 'corpus material library corrected pov tag')

  await page.locator('button[title="重建"]').first().click()
  await expectVisible(page.getByText('锚点已重建'), 'anchor rebuilt message')

  const materialPanel = page.getByTestId('reference-manual-material-search')
  await materialPanel.getByPlaceholder('叙事功能、情绪或具体句子').fill('把杯子推远')
  await materialPanel.getByLabel('文体职责').fill('source_backed_detail')
  await materialPanel.getByRole('button', { name: /搜索/ }).click()
  await expectVisible(materialPanel.getByText('把杯子推远，杯底在木桌上留下半圈水痕。'), 'material search hit')
  const manualFirstMaterial = materialPanel.getByTestId('reference-manual-material-card').filter({ hasText: '把杯子推远，杯底在木桌上留下半圈水痕。' }).first()
  await expectVisible(manualFirstMaterial.getByText('lexical 0.92'), 'material score component')
  await expectVisible(manualFirstMaterial.getByText('prose_duty 0.75'), 'material prose-duty score component')
  await libraryMaterialPanel.getByRole('button', { name: /^选择当前材料$/ }).click()
  await expectVisible(libraryMaterialPanel.getByText('已选 1 条材料'), 'corpus material library archive second-page selection')
  await libraryMaterialPanel.getByRole('button', { name: /^上一页$/ }).click()
  await libraryMaterialPanel.getByLabel('材料库页内筛选').fill('杯子')
  await libraryMaterialPanel.getByRole('button', { name: /^选择当前材料$/ }).click()
  await expectVisible(libraryMaterialPanel.getByText('已选 2 条材料'), 'corpus material library archive cross-page selection')
  await libraryMaterialPanel.getByRole('button', { name: /^归档所选材料$/ }).click()
  await expectVisible(page.getByText('材料库已归档 2 条材料'), 'corpus material library archive message')
  await expectHidden(libraryMaterialPanel.getByText('mat-001'), 'archived corpus material hidden from current library page')
  await libraryMaterialPanel.getByLabel('材料状态').selectOption('archived')
  await libraryMaterialPanel.getByRole('button', { name: /^检索材料库$/ }).click()
  await expectVisible(libraryMaterialPanel.getByText('第 1 / 1 页 · 2 条材料'), 'archived corpus material library result count')
  await expectVisible(libraryMaterialPanel.getByText('mat-001'), 'archived corpus material visible in archived filter')
  const archivedFirstMaterial = libraryMaterialPanel.getByTestId('reference-material-library-card').filter({ hasText: 'mat-001' }).first()
  await archivedFirstMaterial.getByRole('checkbox').check()
  await expectVisible(libraryMaterialPanel.getByText('已选 1 条材料'), 'archived corpus material selected for restore')
  await assertDisabled(libraryMaterialPanel.getByRole('button', { name: /^批量校正材料库$/ }), 'archived material bulk correction disabled')
  await libraryMaterialPanel.getByRole('button', { name: /^恢复所选材料$/ }).click()
  await expectVisible(page.getByText('材料库已恢复 1 条材料'), 'corpus material library restore message')
  await expectHidden(libraryMaterialPanel.getByText('mat-001'), 'restored corpus material hidden from archived page')
  await libraryMaterialPanel.getByLabel('材料状态').selectOption('active')

  await importPanel.getByPlaceholder('参考书名').fill('批量动作参考')
  await importPanel.getByPlaceholder('可选').fill('Batch Curator')
  await importPanel.getByLabel('本地路径').fill('D:\\books\\batch-reference.md')
  await importPanel.getByLabel('来源可信度').selectOption('imported')
  await importPanel.getByLabel('用户标签').fill('批量;候选')
  await importPanel.getByRole('button', { name: /^创建$/ }).click()
  await expectVisible(page.getByText('参考锚点已创建'), 'batch anchor created message')
  await expectVisible(page.getByText('批量动作参考'), 'batch anchor title')

  const batchAnchorRow = page.getByTestId('reference-anchor-row').filter({ hasText: '批量动作参考' }).first()
  await batchAnchorRow.getByRole('checkbox').check()
  await expectVisible(page.getByText('已选 1 项'), 'single selected corpus row count')
  await page.getByRole('button', { name: '批量提升选中项' }).click()
  await expectVisible(page.getByText('已批量提升 1 个参考锚点为工作区语料'), 'bulk promote selected corpus rows message')
  await expectVisible(batchAnchorRow.getByText('workspace · imported · workspace_corpus'), 'bulk promoted row metadata')
  assert.equal(await batchAnchorRow.getByRole('checkbox').isChecked(), false, 'bulk promote clears processed selection')

  await buildInspectArchiveRestoreAndCompareStyleProfiles(page)

  await page.getByRole('button', { name: '选择当前筛选' }).click()
  await expectVisible(page.getByText('已选 2 项'), 'selected all visible corpus rows count')
  await page.getByRole('button', { name: '批量归档选中工作区' }).click()
  await expectVisible(page.getByText('已批量归档 2 个工作区语料'), 'bulk archive selected workspace corpus rows message')
  await expectVisible(page.getByText('暂无参考锚点'), 'bulk archived workspace corpus hidden from library list')

  await importPanel.getByPlaceholder('参考书名').fill('批量导入语料')
  await importPanel.getByPlaceholder('可选').fill('Bulk Author')
  await importPanel.getByLabel('可见性').selectOption('workspace')
  await importPanel.getByLabel('来源可信度').selectOption('imported')
  await importPanel.getByLabel('用户标签').fill('批量导入;共享')
  await importPanel.getByLabel('批量路径').fill('D:\\books\\bulk-one.md\nD:\\books\\bulk-two.txt')
  await importPanel.getByRole('button', { name: /^批量导入$/ }).click()
  await expectVisible(page.getByText('已批量导入 2 个语料来源'), 'bulk source import message')
  await expectVisible(page.getByText('批量导入语料 1'), 'first bulk imported anchor title')
  await expectVisible(page.getByText('批量导入语料 2'), 'second bulk imported anchor title')
  const firstBulkImportRow = page.getByTestId('reference-anchor-row').filter({ hasText: '批量导入语料 1' }).first()
  await expectVisible(firstBulkImportRow.getByText('workspace · imported · workspace_corpus'), 'bulk imported workspace corpus metadata')

  await importPanel.getByPlaceholder('参考书名').fill('库包默认标题')
  await importPanel.getByPlaceholder('可选').fill('Pack Default Author')
  await importPanel.getByLabel('可见性').selectOption('workspace')
  await importPanel.getByLabel('来源可信度').selectOption('imported')
  await importPanel.getByLabel('用户标签').fill('库包;默认')
  await importPanel.getByLabel('库包清单').fill(JSON.stringify({
    sources: [
      {
        source_path: 'D:\\books\\pack-one.md',
        title: '库包显式标题',
        license_status: 'licensed',
        source_trust: 'user_verified',
        user_tags: ['库包', '显式'],
      },
      {
        path: 'D:\\books\\pack-two.txt',
        author: 'Pack Writer',
      },
    ],
  }))
  await importPanel.getByRole('button', { name: /^导入库包$/ }).click()
  await expectVisible(page.getByText('已导入库包 2 个语料来源'), 'library pack import message')
  await expectVisible(page.getByText('库包显式标题'), 'library pack explicit title')
  await expectVisible(page.getByText('库包默认标题 2'), 'library pack derived title')
}

async function buildInspectArchiveRestoreAndCompareStyleProfiles(page) {
  const stylePanel = page.getByTestId('reference-style-profile-library')
  await expectVisible(stylePanel.getByRole('heading', { name: '风格画像库' }), 'style profile library heading')

  const firstAnchorRow = page.getByTestId('reference-anchor-row').filter({ hasText: '雨夜动作语料库' }).first()
  const secondAnchorRow = page.getByTestId('reference-anchor-row').filter({ hasText: '批量动作参考' }).first()

  await firstAnchorRow.locator('input[type="checkbox"]').first().check()
  await expectVisible(stylePanel.getByText(/已选 1 个来源：雨夜动作语料库/), 'style profile selected first source')
  await stylePanel.getByLabel('风格画像标题').fill('雨夜克制画像')
  await stylePanel.getByLabel('风格画像说明').fill('近距离 POV、克制动作和雨声压力')
  await stylePanel.getByRole('button', { name: /^构建风格画像$/ }).click()
  await expectVisible(stylePanel.getByText('风格画像已构建'), 'style profile built message')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-row').filter({ hasText: '雨夜克制画像' }), 'first style profile row')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-detail').getByText(/dialogue_ratio/).first(), 'first style profile numeric feature')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-detail').getByText(/证据 1/).first(), 'first style profile evidence count')

  await firstAnchorRow.locator('input[type="checkbox"]').first().uncheck()
  await secondAnchorRow.locator('input[type="checkbox"]').first().check()
  await expectVisible(stylePanel.getByText(/已选 1 个来源：批量动作参考/), 'style profile selected second source')
  await stylePanel.getByLabel('风格画像标题').fill('批量动作画像')
  await stylePanel.getByLabel('风格画像说明').fill('动作后拍和中等句长')
  await stylePanel.getByRole('button', { name: /^构建风格画像$/ }).click()
  await expectVisible(stylePanel.getByText('风格画像已构建'), 'second style profile built message')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-row').filter({ hasText: '批量动作画像' }), 'second style profile row')

  await stylePanel.getByRole('button', { name: /查看风格画像 雨夜克制画像/ }).click()
  await expectVisible(stylePanel.getByTestId('reference-style-profile-detail').getByRole('heading', { name: '雨夜克制画像' }), 'style profile detail title')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-detail').getByText(/来源 101/), 'style profile detail provenance')

  await stylePanel.getByLabel('左侧风格画像').selectOption('301')
  await stylePanel.getByLabel('右侧风格画像').selectOption('302')
  await stylePanel.getByRole('button', { name: /^比较$/ }).click()
  await expectVisible(stylePanel.getByText('风格画像比较已生成'), 'style profile comparison message')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-comparison').getByText(/dialogue_ratio/), 'style profile comparison numeric delta')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-comparison').getByText(/dominant_technique/).first(), 'style profile comparison categorical delta')

  await stylePanel.getByRole('button', { name: /归档风格画像 雨夜克制画像/ }).click()
  await expectVisible(stylePanel.getByText('风格画像已归档'), 'style profile archived message')
  await expectHidden(stylePanel.getByTestId('reference-style-profile-row').filter({ hasText: '雨夜克制画像' }), 'archived style profile hidden from default list')
  await stylePanel.getByText('显示归档画像').click()
  await expectVisible(stylePanel.getByTestId('reference-style-profile-row').filter({ hasText: '雨夜克制画像' }), 'archived style profile visible when included')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-row').filter({ hasText: '雨夜克制画像' }).getByText('archived'), 'archived style profile status')
  await stylePanel.getByRole('button', { name: /恢复风格画像 雨夜克制画像/ }).click()
  await expectVisible(stylePanel.getByText('风格画像已恢复'), 'style profile restored message')
  await expectVisible(stylePanel.getByTestId('reference-style-profile-row').filter({ hasText: '雨夜克制画像' }).getByText('active'), 'restored style profile status')

  await secondAnchorRow.locator('input[type="checkbox"]').first().uncheck()
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
  await detail.getByLabel('段落意图').fill('用手部动作和雨声停顿表现压住反应。')
  await detail.getByLabel('风格画像 ID').fill('profile-301')
  await detail.getByLabel('风格职责').fill('dialogue_ratio\nsensory_ratio')
  await detail.getByLabel('模仿强度').selectOption('strong')
  await detail.getByLabel('最低风格匹配').fill('0.8')
  await detail.getByLabel('允许接近度').fill('moderate')
  await detail.getByLabel('必需风格证据').fill('dialogue_exchange')
  await detail.getByLabel('禁止风格风险').fill('source_leak\nstyle_distance')
  await detail.getByRole('button', { name: /保存修订/ }).click()
  await expectVisible(page.getByText('风格画像 ID 必须是正整数：profile-301'), 'invalid style profile id validation')

  await detail.getByLabel('风格画像 ID').fill('301')
  await detail.getByRole('button', { name: /保存修订/ }).click()
  await expectVisible(page.getByText('蓝图已修订，需要重新评审和批准'), 'blueprint revised message')
  await expectVisible(detail.getByText('profiles=301'), 'style contract summary profile id')
  await expectVisible(detail.getByText('intensity=strong'), 'style contract summary intensity')
  await expectVisible(detail.getByText('dims=dialogue_ratio,sensory_ratio'), 'style contract summary dimensions')

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
  await expectVisible(detail.getByText(/Draft audit passed for 3 candidate\(s\) at rewrite level L1/), 'readable draft audit summary')
  await expectVisible(detail.getByText('雨声压过门缝里的动静').first(), 'draft candidate text')
  await expectVisible(detail.getByText(/风格尝试 intensity=loose status=attempted profiles=301/), 'loose style attempt summary')
  await expectVisible(detail.getByText(/风格尝试 intensity=moderate status=attempted profiles=301/), 'moderate style attempt summary')
  await expectVisible(detail.getByText(/风格尝试 intensity=strong status=attempted profiles=301/), 'strong style attempt summary')
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
  await expectVisible(panel.getByText('授权 user_provided'), 'license status policy')
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
  await expectVisible(panel.getByText(/style contracts: beat 1 profiles=301 intensity=strong min_fit=0.8/), 'approval summary style contract plan value')
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

async function verifyReviewAndAuditFailureRecovery(page) {
  const blueprintPanel = page.getByTestId('reference-blueprint-panel')
  await blueprintPanel.getByLabel('章节号').fill('6')
  await blueprintPanel.getByLabel('标题').fill('缺陷蓝图')
  await blueprintPanel.getByLabel('章节目标').fill('先暴露检索缺口，再用作者修订恢复。')
  await blueprintPanel.getByLabel('已知事实').fill('主角只看见桌面\n雨声很大')
  await blueprintPanel.getByLabel('禁止事实').fill('门外身份')
  await blueprintPanel.getByRole('button', { name: /生成蓝图/ }).click()
  await expectVisible(page.getByText('章节蓝图已生成'), 'failure-recovery blueprint generated')

  const detail = blueprintDetail(page)
  await detail.getByRole('button', { name: /^评审$/ }).click()
  await expectVisible(page.getByText('蓝图评审已完成'), 'failed review completed')
  await expectVisible(detail.getByText('failed · 0.42'), 'failed review score')
  await expectVisible(detail.getByText('检索缺口和弱匹配会让候选缺少可审计来源。'), 'retrieval gap and weak match review defect')
  await expectVisible(detail.getByText('补充可访问材料或放宽授权策略。'), 'review defect required fix')
  await assertDisabled(detail.getByRole('button', { name: /^批准$/ }), 'failed review approve button')

  await detail.getByLabel('段落意图').fill('修复检索缺口：限制为 user_provided 材料并明确弱匹配回退。')
  await detail.getByRole('button', { name: /保存修订/ }).click()
  await expectVisible(page.getByText('蓝图已修订，需要重新评审和批准'), 'review failure revision saved')
  await detail.getByRole('button', { name: /^评审$/ }).click()
  await expectVisible(detail.getByText('passed · 0.96'), 'review recovered score')
  await detail.getByRole('button', { name: /^批准$/ }).click()
  await expectVisible(page.getByText('蓝图已批准'), 'review recovered approval')
  await detail.getByRole('button', { name: /^绑定$/ }).click()
  await expectVisible(page.getByText('材料已绑定到蓝图'), 'review recovered binding')

  await detail.getByRole('button', { name: /^候选$/ }).click()
  await expectVisible(page.getByText('候选段落已生成'), 'failed audit draft generated')
  await expectVisible(detail.getByText('审计 failed · L3'), 'failed draft audit status')
  await expectVisible(detail.getByText(/Draft audit failed for 1 candidate\(s\) at rewrite level L3/), 'failed readable audit summary')
  await expectVisible(detail.getByText('候选引用了未绑定的门外身份。').first(), 'failed audit provenance issue')
  await expectVisible(detail.getByText('回到蓝图修订，移除未支持事实并降低改写级别。').first(), 'failed audit required fix')

  await detail.getByLabel('段落意图').fill('审计修复：只保留桌面和雨声信息，不揭示门外身份。')
  await detail.getByRole('button', { name: /保存修订/ }).click()
  await expectVisible(page.getByText('蓝图已修订，需要重新评审和批准'), 'audit failure revision saved')
  await detail.getByRole('button', { name: /^评审$/ }).click()
  await expectVisible(detail.getByText('passed · 0.96'), 'audit recovery review passed')
  await detail.getByRole('button', { name: /^批准$/ }).click()
  await detail.getByRole('button', { name: /^绑定$/ }).click()
  await detail.getByRole('button', { name: /^候选$/ }).click()
  await expectVisible(detail.getByText('审计 passed · L1'), 'audit recovered status')
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
    'CreateReferenceAnchors',
    'PromoteReferenceAnchorToWorkspaceCorpus',
    'PromoteReferenceAnchorsToWorkspaceCorpus',
    'UpdateReferenceAnchorMetadata',
    'UpdateReferenceMaterialTags',
    'UpdateReferenceMaterialsTags',
    'DeleteReferenceAnchors',
    'DeleteReferenceMaterials',
    'RestoreReferenceMaterials',
    'RebuildReferenceAnchor',
    'SearchReferenceMaterials',
    'BuildReferenceStyleProfile',
    'GetReferenceStyleProfiles',
    'GetReferenceStyleProfile',
    'ArchiveReferenceStyleProfile',
    'RestoreReferenceStyleProfile',
    'CompareReferenceStyleProfiles',
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
  assert(!methods.includes('runtime.shell.openExternal'), 'reference-anchor workflow must not open external URLs')
  assertBridgeCallOrder(calls, 'ReviewReferenceChapterBlueprint', 'ApproveReferenceChapterBlueprint')
  assertBridgeCallOrder(calls, 'ApproveReferenceChapterBlueprint', 'BindReferenceBlueprintMaterials')
  assertBridgeCallOrder(calls, 'BindReferenceBlueprintMaterials', 'GenerateReferenceAnchoredDraft')

  const resumeDecisions = calls
    .filter((call) => call.method === 'ResumeReferenceOrchestrationRun')
    .map((call) => call.args[0]?.decision_type)
  assert.deepEqual(
    resumeDecisions,
    ['confirm_source_and_facts', 'approve_blueprint'],
    'default orchestration must stop before final insertion and only resume explicit author gates',
  )
  assert(!resumeDecisions.includes('approve_final_insertion'), 'final insertion must not be auto-approved through orchestration')

  const createCall = calls.find((call) => call.method === 'CreateReferenceAnchor')
  assert(createCall, 'missing CreateReferenceAnchor call')
  assert.equal(createCall.args[0].visibility, 'private', 'anchor create payload must start as per-novel private visibility')
  assert.equal(createCall.args[0].source_trust, 'imported', 'anchor create payload must include source trust')
  assert.deepEqual(createCall.args[0].user_tags, ['雨夜', '动作克制'], 'anchor create payload must include user tags')

  const createAnchorsCall = calls.find((call) => call.method === 'CreateReferenceAnchors')
  assert(createAnchorsCall, 'missing CreateReferenceAnchors call')
  assert.equal(createAnchorsCall.args[0].anchors.length, 2, 'bulk source import payload must include both source paths')
  assert.deepEqual(
    createAnchorsCall.args[0].anchors.map((anchor) => anchor.title),
    ['批量导入语料 1', '批量导入语料 2'],
    'bulk source import should derive ordered titles from the shared title',
  )
  assert.deepEqual(
    createAnchorsCall.args[0].anchors.map((anchor) => anchor.source_path),
    ['D:\\books\\bulk-one.md', 'D:\\books\\bulk-two.txt'],
    'bulk source import payload must preserve source paths',
  )
  assert.deepEqual(
    createAnchorsCall.args[0].anchors.map((anchor) => anchor.source_kind),
    ['markdown', 'text'],
    'bulk source import payload should infer source kind per path',
  )
  assert(createAnchorsCall.args[0].anchors.every((anchor) => anchor.visibility === 'workspace'), 'bulk source import should preserve selected workspace visibility')
  assert(createAnchorsCall.args[0].anchors.every((anchor) => anchor.source_trust === 'imported'), 'bulk source import should preserve selected source trust')
  assert.deepEqual(createAnchorsCall.args[0].anchors[0].user_tags, ['批量导入', '共享'], 'bulk source import payload must include shared user tags')

  const libraryPackCall = calls.find((call) =>
    call.method === 'CreateReferenceAnchors' &&
    call.args[0]?.anchors?.some((anchor) => anchor.source_path === 'D:\\books\\pack-one.md'))
  assert(libraryPackCall, 'missing library pack CreateReferenceAnchors call')
  assert.equal(libraryPackCall.args[0].anchors.length, 2, 'library pack import payload must include manifest sources')
  assert.equal(libraryPackCall.args[0].anchors[0].title, '库包显式标题', 'library pack should keep explicit title')
  assert.equal(libraryPackCall.args[0].anchors[0].license_status, 'licensed', 'library pack should keep per-source license status')
  assert.equal(libraryPackCall.args[0].anchors[0].source_trust, 'user_verified', 'library pack should keep per-source source trust')
  assert.deepEqual(libraryPackCall.args[0].anchors[0].user_tags, ['库包', '显式'], 'library pack should keep per-source user tags')
  assert.equal(libraryPackCall.args[0].anchors[1].title, '库包默认标题 2', 'library pack should derive missing title from shared title')
  assert.equal(libraryPackCall.args[0].anchors[1].author, 'Pack Writer', 'library pack should keep per-source author')
  assert.equal(libraryPackCall.args[0].anchors[1].source_kind, 'text', 'library pack should infer text source kind from txt path')
  assert.equal(libraryPackCall.args[0].anchors[1].visibility, 'workspace', 'library pack should inherit selected workspace visibility')
  assert.equal(libraryPackCall.args[0].anchors[1].source_trust, 'imported', 'library pack should inherit selected source trust')
  assert.deepEqual(libraryPackCall.args[0].anchors[1].user_tags, ['库包', '默认'], 'library pack should inherit shared user tags')

  const promoteCalls = calls.filter((call) => call.method === 'PromoteReferenceAnchorToWorkspaceCorpus')
  assert.equal(promoteCalls.length, 1, 'single promote should call PromoteReferenceAnchorToWorkspaceCorpus once')
  assert.deepEqual(promoteCalls.map((call) => call.args[0].anchor_id), [101], 'single promote call must include the first anchor id')
  assert(promoteCalls.every((call) => call.args[0].novel_id === 42), 'anchor promote payloads must include novel id')
  const bulkPromoteCall = calls.find((call) => call.method === 'PromoteReferenceAnchorsToWorkspaceCorpus')
  assert(bulkPromoteCall, 'missing PromoteReferenceAnchorsToWorkspaceCorpus call')
  assert.equal(bulkPromoteCall.args[0].novel_id, 42, 'bulk promote payload must include novel id')
  assert.deepEqual(bulkPromoteCall.args[0].anchor_ids, [102], 'bulk promote payload must include selected anchor ids')

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

  const bulkDeleteCall = calls.find((call) => call.method === 'DeleteReferenceAnchors')
  assert(bulkDeleteCall, 'missing DeleteReferenceAnchors call')
  assert.equal(bulkDeleteCall.args[0].novel_id, 42, 'bulk archive payload must include novel id')
  assert.deepEqual(bulkDeleteCall.args[0].anchor_ids, [101, 102], 'bulk archive payload must include selected workspace anchor ids')

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

  const bulkMaterialTagCall = calls.find((call) => call.method === 'UpdateReferenceMaterialsTags')
  assert(bulkMaterialTagCall, 'missing UpdateReferenceMaterialsTags call')
  assert.equal(bulkMaterialTagCall.args[0].novel_id, 42, 'bulk material tag update payload must include novel id')
  assert.deepEqual(bulkMaterialTagCall.args[0].material_ids, ['mat-001', 'mat-002', 'mat-003', 'mat-004', 'mat-005'], 'bulk material tag update payload must include selected current-page material ids')
  assert.equal(bulkMaterialTagCall.args[0].function_tag, 'spatial_pressure', 'bulk material tag update payload must include function tag')
  assert.equal(bulkMaterialTagCall.args[0].emotion_tag, 'coiled_alarm', 'bulk material tag update payload must include emotion tag')
  assert.equal(bulkMaterialTagCall.args[0].scene_tag, 'threshold_room', 'bulk material tag update payload must include scene tag')
  assert.equal(bulkMaterialTagCall.args[0].pov_tag, 'shared_close', 'bulk material tag update payload must include pov tag')
  assert.equal(bulkMaterialTagCall.args[0].technique_tag, 'object_afterbeat', 'bulk material tag update payload must include technique tag')
  assert.equal(bulkMaterialTagCall.args[0].origin, 'ui', 'bulk material tag update payload must mark UI origin')
  assert.equal(bulkMaterialTagCall.args[0].note, 'corpus material browser bulk correction', 'bulk material tag update payload must include correction note')

  const libraryMaterialSearchCall = calls.find((call) =>
    call.method === 'SearchReferenceMaterials' &&
    Array.isArray(call.args[0]?.anchor_ids) &&
    call.args[0].anchor_ids.length === 0 &&
    call.args[0].query === '把杯子推远' &&
    Array.isArray(call.args[0]?.prose_duties) &&
    call.args[0].prose_duties.includes('source_backed_detail'))
  assert(libraryMaterialSearchCall, 'missing corpus material library SearchReferenceMaterials call')
  assert.equal(libraryMaterialSearchCall.args[0].novel_id, 42, 'corpus material library search payload must include novel id')
  assert.deepEqual(libraryMaterialSearchCall.args[0].anchor_ids, [], 'corpus material library search must not require selected anchor ids')

  const libraryBulkMaterialTagCall = calls.find((call) =>
    call.method === 'UpdateReferenceMaterialsTags' &&
    call.args[0]?.note === 'corpus material library bulk correction')
  assert(libraryBulkMaterialTagCall, 'missing corpus material library UpdateReferenceMaterialsTags call')
  assert.equal(libraryBulkMaterialTagCall.args[0].novel_id, 42, 'corpus material library bulk correction payload must include novel id')
  assert.deepEqual(libraryBulkMaterialTagCall.args[0].material_ids, ['mat-001', 'mat-006'], 'corpus material library bulk correction payload must include selected cross-page material ids')
  assert.equal(libraryBulkMaterialTagCall.args[0].function_tag, 'library_object_signal', 'corpus material library bulk correction payload must include function tag')
  assert.equal(libraryBulkMaterialTagCall.args[0].pov_tag, 'library_close', 'corpus material library bulk correction payload must include pov tag')
  assert.equal(libraryBulkMaterialTagCall.args[0].origin, 'ui', 'corpus material library bulk correction payload must mark UI origin')

  const libraryDeleteMaterialsCall = calls.find((call) => call.method === 'DeleteReferenceMaterials')
  assert(libraryDeleteMaterialsCall, 'missing corpus material library DeleteReferenceMaterials call')
  assert.equal(libraryDeleteMaterialsCall.args[0].novel_id, 42, 'corpus material library archive payload must include novel id')
  assert.deepEqual(libraryDeleteMaterialsCall.args[0].material_ids, ['mat-006', 'mat-001'], 'corpus material library archive payload must include selected cross-page material ids')

  const libraryRestoreMaterialsCall = calls.find((call) => call.method === 'RestoreReferenceMaterials')
  assert(libraryRestoreMaterialsCall, 'missing corpus material library RestoreReferenceMaterials call')
  assert.equal(libraryRestoreMaterialsCall.args[0].novel_id, 42, 'corpus material library restore payload must include novel id')
  assert.deepEqual(libraryRestoreMaterialsCall.args[0].material_ids, ['mat-001'], 'corpus material library restore payload must include selected archived material ids')

  const styleBuildCalls = calls.filter((call) => call.method === 'BuildReferenceStyleProfile')
  assert.equal(styleBuildCalls.length, 2, 'style profile workflow must build two profiles for comparison')
  assert.equal(styleBuildCalls[0].args[0].novel_id, 42, 'style profile build payload must include novel id')
  assert.equal(styleBuildCalls[0].args[0].title, '雨夜克制画像', 'first style profile build payload must include title')
  assert.deepEqual(styleBuildCalls[0].args[0].anchor_ids, [101], 'first style profile build payload must use selected first anchor')
  assert.deepEqual(styleBuildCalls[0].args[0].allowed_license_statuses, ['user_provided', 'licensed', 'public_domain'], 'style profile build payload must include license policy')
  assert.deepEqual(styleBuildCalls[0].args[0].allowed_source_trust_levels, ['user_verified', 'imported'], 'style profile build payload must include source-trust policy')
  assert.equal(styleBuildCalls[1].args[0].title, '批量动作画像', 'second style profile build payload must include title')
  assert.deepEqual(styleBuildCalls[1].args[0].anchor_ids, [102], 'second style profile build payload must use selected second anchor')

  const styleDetailCall = calls.find((call) => call.method === 'GetReferenceStyleProfile' && call.args[1] === 301)
  assert(styleDetailCall, 'missing GetReferenceStyleProfile call')
  assert.equal(styleDetailCall.args[0], 42, 'style profile detail call must include novel id')

  const styleCompareCall = calls.find((call) => call.method === 'CompareReferenceStyleProfiles')
  assert(styleCompareCall, 'missing CompareReferenceStyleProfiles call')
  assert.deepEqual(styleCompareCall.args[0], {
    novel_id: 42,
    left_profile_id: 301,
    right_profile_id: 302,
  }, 'style profile comparison payload must include two same-novel profile ids')

  const styleArchiveCall = calls.find((call) => call.method === 'ArchiveReferenceStyleProfile')
  assert(styleArchiveCall, 'missing ArchiveReferenceStyleProfile call')
  assert.deepEqual(styleArchiveCall.args[0], { novel_id: 42, profile_id: 301 }, 'style profile archive payload must include novel and profile id')

  const styleRestoreCall = calls.find((call) => call.method === 'RestoreReferenceStyleProfile')
  assert(styleRestoreCall, 'missing RestoreReferenceStyleProfile call')
  assert.deepEqual(styleRestoreCall.args[0], { novel_id: 42, profile_id: 301 }, 'style profile restore payload must include novel and profile id')

  const styleContractRevisionCall = calls.find((call) =>
    call.method === 'ReviseReferenceChapterBlueprint' &&
    call.args[0]?.changes?.some((change) => change.field_path === 'beat:beat-001:style_contract'))
  assert(styleContractRevisionCall, 'missing style contract blueprint revision call')
  const styleContractChange = styleContractRevisionCall.args[0].changes.find((change) => change.field_path === 'beat:beat-001:style_contract')
  const revisedStyleContract = JSON.parse(styleContractChange.new_value)
  assert.deepEqual(revisedStyleContract.style_profile_ids, [301], 'style contract revision must include selected style profile ids')
  assert.deepEqual(revisedStyleContract.style_dimensions, ['dialogue_ratio', 'sensory_ratio'], 'style contract revision must include style dimensions')
  assert.equal(revisedStyleContract.imitation_intensity, 'strong', 'style contract revision must include imitation intensity')
  assert.equal(revisedStyleContract.min_style_fit, 0.8, 'style contract revision must include minimum style fit')
  assert.equal(revisedStyleContract.allowed_closeness, 'moderate', 'style contract revision must include allowed closeness')
  assert.deepEqual(revisedStyleContract.required_evidence_types, ['dialogue_exchange'], 'style contract revision must include evidence requirements')
  assert.deepEqual(revisedStyleContract.forbidden_style_risks, ['source_leak', 'style_distance'], 'style contract revision must include forbidden style risks')

  const styleDraftCall = calls.find((call) =>
    call.method === 'GenerateReferenceAnchoredDraft' &&
    call.args[0]?.blueprint_id === styleContractRevisionCall.args[0].blueprint_id)
  assert(styleDraftCall, 'missing style-guided draft generation call')
  assert.deepEqual(
    styleDraftCall.args[0].style_intensities,
    ['loose', 'moderate', 'strong'],
    'style-guided draft generation must request loose/moderate/strong candidates')
  assert.equal(styleDraftCall.args[0].candidates_per_beat, 3, 'style-guided draft generation must request three candidates per beat')

  const startCall = calls.find((call) => call.method === 'StartReferenceOrchestrationRun')
  assert(startCall, 'missing StartReferenceOrchestrationRun call')
  assert.equal(startCall.args[0].anchor_ids, null, 'default orchestration must not require selected anchor ids')
  assert.deepEqual(startCall.args[0].corpus_search_policy.include_anchor_ids, [], 'default orchestration must not pin include anchors')
  assert.deepEqual(startCall.args[0].corpus_search_policy.exclude_anchor_ids, [], 'default orchestration must not pin exclude anchors')
  assert.equal(startCall.args[0].corpus_search_policy.mode, 'story_context', 'default orchestration must use story-context corpus search')
  assert.deepEqual(startCall.args[0].corpus_search_policy.license_statuses, ['user_provided'], 'default orchestration must use safe license policy')

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

function assertBridgeCallOrder(calls, beforeMethod, afterMethod) {
  const beforeIndex = calls.findIndex((call) => call.method === beforeMethod)
  const afterIndex = calls.findIndex((call) => call.method === afterMethod)
  assert(beforeIndex >= 0, `Missing bridge call ${beforeMethod}`)
  assert(afterIndex >= 0, `Missing bridge call ${afterMethod}`)
  assert(beforeIndex < afterIndex, `${beforeMethod} must happen before ${afterMethod}`)
}

function blueprintDetail(page) {
  return page.getByTestId('reference-blueprint-detail')
}

function orchestrationPanel(page) {
  return page.getByTestId('reference-orchestration-panel')
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
    nextStyleProfileId: 301,
    anchors: [],
    styleProfiles: [],
    blueprints: {},
    runs: [],
    events: {},
    archivedMaterialIds: new Set(),
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
      case 'CreateReferenceAnchors': return createReferenceAnchors(args[0])
      case 'PromoteReferenceAnchorToWorkspaceCorpus': return promoteReferenceAnchor(args[0])
      case 'PromoteReferenceAnchorsToWorkspaceCorpus': return promoteReferenceAnchors(args[0])
      case 'UpdateReferenceAnchorMetadata': return updateReferenceAnchorMetadata(args[0])
      case 'DeleteReferenceAnchor': return deleteReferenceAnchor(args[0], args[1])
      case 'DeleteReferenceAnchors': return deleteReferenceAnchors(args[0])
      case 'DeleteReferenceMaterials': return deleteReferenceMaterials(args[0])
      case 'RestoreReferenceMaterials': return restoreReferenceMaterials(args[0])
      case 'UpdateReferenceMaterialTags': return updateReferenceMaterialTags(args[0])
      case 'UpdateReferenceMaterialsTags': return updateReferenceMaterialsTags(args[0])
      case 'RebuildReferenceAnchor': return rebuildReferenceAnchor(args[1])
      case 'SearchReferenceMaterials': return searchReferenceMaterials(args[0])
      case 'BuildReferenceStyleProfile': return buildReferenceStyleProfile(args[0])
      case 'GetReferenceStyleProfiles': return getReferenceStyleProfiles(args[0])
      case 'GetReferenceStyleProfile': return getReferenceStyleProfile(args[0], args[1])
      case 'ArchiveReferenceStyleProfile': return archiveReferenceStyleProfile(args[0])
      case 'RestoreReferenceStyleProfile': return restoreReferenceStyleProfile(args[0])
      case 'CompareReferenceStyleProfiles': return compareReferenceStyleProfiles(args[0])
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
    state.anchors = [...state.anchors, anchor]
    return anchor
  }

  function createReferenceAnchors(input) {
    return input.anchors.map((anchor) => createReferenceAnchor(anchor))
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

  function promoteReferenceAnchors(input) {
    return input.anchor_ids.map((anchorId) => promoteReferenceAnchor({
      novel_id: input.novel_id,
      anchor_id: anchorId,
      source_trust: input.source_trust,
      user_tags: input.user_tags,
    }))
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

  function deleteReferenceAnchor(_novelId, anchorId) {
    state.anchors = state.anchors.filter((item) => item.anchor_id !== anchorId)
    return null
  }

  function deleteReferenceAnchors(input) {
    for (const anchorId of input.anchor_ids) {
      deleteReferenceAnchor(input.novel_id, anchorId)
    }
    return null
  }

  function deleteReferenceMaterials(input) {
    for (const materialId of input.material_ids) {
      state.archivedMaterialIds.add(materialId)
    }
    return null
  }

  function restoreReferenceMaterials(input) {
    for (const materialId of input.material_ids) {
      state.archivedMaterialIds.delete(materialId)
    }
    return null
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
      const isMaterialLibrarySearch = Array.isArray(input.anchor_ids) && input.anchor_ids.length === 0
      if (isMaterialLibrarySearch) {
        const page = input.page ?? 1
        const size = input.size ?? 10
        const archivedOnly = input.archive_filter === 'archived'
        const itemIndexes = archivedOnly ? [1, 6] : page === 2 ? [6] : [1, 2, 3, 4]
        const items = itemIndexes
          .map(material)
          .filter((item) => archivedOnly ? state.archivedMaterialIds.has(item.material_id) : !state.archivedMaterialIds.has(item.material_id))
        return pagedResult(items, page, size, archivedOnly ? state.archivedMaterialIds.size : Math.max(0, 11 - state.archivedMaterialIds.size))
      }

      return pagedResult([material(1)], input.page ?? 1, input.size ?? 10, 1)
    }

    const page = input.page ?? 1
    const size = input.size ?? 5
    if (page === 2) {
      return pagedResult([material(6)], page, size, 6)
    }

    return pagedResult([1, 2, 3, 4, 5].map(material), page, size, 6)
  }

  function buildReferenceStyleProfile(input) {
    const profileId = state.nextStyleProfileId++
    const sourceAnchors = input.anchor_ids.map((anchorId) => {
      const anchor = state.anchors.find((item) => item.anchor_id === anchorId)
      if (!anchor) throw new Error(`Unknown reference anchor ${anchorId}`)
      return anchor
    })
    const profile = makeStyleProfile(profileId, {
      novel_id: input.novel_id,
      title: input.title,
      description: input.description ?? '',
      source_anchor_ids: input.anchor_ids,
      source_hashes: sourceAnchors.map((anchor) => anchor.source_file_hash),
      allowed_license_statuses: input.allowed_license_statuses ?? ['user_provided'],
      allowed_source_trust_levels: input.allowed_source_trust_levels ?? ['user_verified'],
      average_sentence_chars: profileId % 2 === 0 ? 24.5 : 16.25,
      dialogue_ratio: profileId % 2 === 0 ? 0.18 : 0.42,
      dominant_technique: profileId % 2 === 0 ? 'sensory_detail' : 'dialogue_exchange',
    })
    state.styleProfiles = [profile, ...state.styleProfiles]
    return profile
  }

  function getReferenceStyleProfiles(input) {
    return state.styleProfiles
      .filter((profile) => input?.include_archived || profile.archived_at === null)
      .map(toStyleProfileSummary)
  }

  function getReferenceStyleProfile(novelId, profileId) {
    return state.styleProfiles.find((profile) => profile.novel_id === novelId && profile.profile_id === profileId) ?? null
  }

  function archiveReferenceStyleProfile(input) {
    const profile = getMutableStyleProfile(input.novel_id, input.profile_id)
    profile.status = 'archived'
    profile.archived_at = now
    profile.updated_at = now
    return profile
  }

  function restoreReferenceStyleProfile(input) {
    const profile = getMutableStyleProfile(input.novel_id, input.profile_id)
    profile.status = 'active'
    profile.archived_at = null
    profile.updated_at = now
    return profile
  }

  function compareReferenceStyleProfiles(input) {
    const left = getMutableStyleProfile(input.novel_id, input.left_profile_id)
    const right = getMutableStyleProfile(input.novel_id, input.right_profile_id)
    const leftNumeric = numericByKey(left)
    const rightNumeric = numericByKey(right)
    return {
      novel_id: input.novel_id,
      left_profile: toStyleProfileSummary(left),
      right_profile: toStyleProfileSummary(right),
      numeric_differences: ['average_sentence_chars', 'dialogue_ratio'].map((featureKey) => {
        const leftFeature = leftNumeric.get(featureKey)
        const rightFeature = rightNumeric.get(featureKey)
        return {
          feature_key: featureKey,
          unit: leftFeature?.unit ?? rightFeature?.unit ?? '',
          left_value: leftFeature?.value ?? null,
          right_value: rightFeature?.value ?? null,
          absolute_delta: Math.abs((leftFeature?.value ?? 0) - (rightFeature?.value ?? 0)),
          relative_delta: leftFeature?.value ? Math.abs((rightFeature?.value ?? 0) - leftFeature.value) / Math.abs(leftFeature.value) : null,
          left_confidence: leftFeature?.confidence ?? null,
          right_confidence: rightFeature?.confidence ?? null,
        }
      }),
      distribution_differences: [
        {
          feature_key: 'sentence_length_distribution',
          unit: 'chars',
          buckets: [
            { label: 'short', left_min: 0, left_max: 20, left_weight: 0.55, right_min: 0, right_max: 20, right_weight: 0.34, absolute_delta: 0.21 },
            { label: 'medium', left_min: 21, left_max: 60, left_weight: 0.45, right_min: 21, right_max: 60, right_weight: 0.66, absolute_delta: 0.21 },
          ],
          left_confidence: 0.84,
          right_confidence: 0.82,
        },
      ],
      categorical_differences: [
        {
          feature_key: 'dominant_technique',
          label: left.features.categorical_features[0]?.label ?? 'unknown',
          left_weight: 0.72,
          right_weight: null,
          absolute_delta: 0.72,
          left_confidence: 0.86,
          right_confidence: null,
        },
        {
          feature_key: 'dominant_technique',
          label: right.features.categorical_features[0]?.label ?? 'unknown',
          left_weight: null,
          right_weight: 0.68,
          absolute_delta: 0.68,
          left_confidence: null,
          right_confidence: 0.84,
        },
      ],
      compared_at: now,
    }
  }

  function getMutableStyleProfile(novelId, profileId) {
    const profile = state.styleProfiles.find((item) => item.novel_id === novelId && item.profile_id === profileId)
    if (!profile) throw new Error(`Unknown style profile ${profileId}`)
    return profile
  }

  function numericByKey(profile) {
    return new Map(profile.features.numeric_features.map((feature) => [feature.feature_key, feature]))
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

  function updateReferenceMaterialsTags(input) {
    return input.material_ids.map((materialId) => ({
      ...materialById(materialId),
      function_tag: input.function_tag ?? 'emotion_evidence',
      emotion_tag: input.emotion_tag ?? 'restrained',
      scene_tag: input.scene_tag ?? 'rain_room',
      pov_tag: input.pov_tag ?? 'close',
      technique_tag: input.technique_tag ?? 'subtext',
      user_verified: true,
    }))
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
        lexical: index === 4 ? 0.98 : 0.92,
        function: index === 4 ? 0.88 : 0.83,
        prose_duty: index === 4 ? 0.8 : 0.75,
        feedback_boost: 0.18,
      },
    }
  }

  function materialText(index) {
    if (index === 1) return '把杯子推远，杯底在木桌上留下半圈水痕。'
    if (index === 6) return '雨水从伞沿断续落下，像有人在门外迟疑。'
    return `第 ${index} 条克制动作材料。`
  }

  function makeStyleProfile(profileId, overrides = {}) {
    const evidenceId = `style-evidence-${profileId}`
    const dialogueRatio = overrides.dialogue_ratio ?? 0.35
    const averageSentenceChars = overrides.average_sentence_chars ?? 18.5
    const technique = overrides.dominant_technique ?? 'dialogue_exchange'
    return {
      profile_id: profileId,
      novel_id: overrides.novel_id ?? 42,
      title: overrides.title ?? `风格画像 ${profileId}`,
      description: overrides.description ?? '',
      status: 'active',
      analyzer_version: 'reference-style-deterministic-v1',
      feature_schema_version: 'style-profile-v1',
      analyzer_source: 'deterministic_baseline',
      source_anchor_ids: overrides.source_anchor_ids ?? [101],
      source_hashes: overrides.source_hashes ?? ['hash-anchor-001'],
      allowed_license_statuses: overrides.allowed_license_statuses ?? ['user_provided', 'licensed', 'public_domain'],
      allowed_source_trust_levels: overrides.allowed_source_trust_levels ?? ['user_verified', 'imported'],
      aggregate_confidence: 0.87,
      features: {
        numeric_features: [
          {
            feature_key: 'dialogue_ratio',
            value: dialogueRatio,
            unit: 'ratio',
            confidence: 0.86,
            evidence_ids: [evidenceId],
          },
          {
            feature_key: 'average_sentence_chars',
            value: averageSentenceChars,
            unit: 'chars',
            confidence: 0.84,
            evidence_ids: [evidenceId],
          },
        ],
        distribution_features: [
          {
            feature_key: 'sentence_length_distribution',
            unit: 'chars',
            buckets: [
              { label: 'short', min: 0, max: 20, weight: profileId % 2 === 0 ? 0.34 : 0.55 },
              { label: 'medium', min: 21, max: 60, weight: profileId % 2 === 0 ? 0.66 : 0.45 },
            ],
            confidence: 0.83,
            evidence_ids: [evidenceId],
          },
        ],
        categorical_features: [
          {
            feature_key: 'dominant_technique',
            label: technique,
            weight: 0.7,
            confidence: 0.85,
            evidence_ids: [evidenceId],
          },
        ],
      },
      evidence_spans: [
        {
          evidence_id: evidenceId,
          profile_id: profileId,
          anchor_id: overrides.source_anchor_ids?.[0] ?? 101,
          source_segment_id: 'seg-001',
          material_id: 'mat-001',
          feature_key: 'dialogue_ratio',
          label: 'dialogue_exchange',
          start_offset: 0,
          end_offset: 12,
          text_hash: 'hash-material-001',
          confidence: 0.86,
          analyzer_source: 'deterministic_baseline',
        },
      ],
      created_at: now,
      updated_at: now,
      archived_at: null,
    }
  }

  function toStyleProfileSummary(profile) {
    return {
      profile_id: profile.profile_id,
      novel_id: profile.novel_id,
      title: profile.title,
      description: profile.description,
      status: profile.status,
      analyzer_version: profile.analyzer_version,
      feature_schema_version: profile.feature_schema_version,
      analyzer_source: profile.analyzer_source,
      source_anchor_ids: profile.source_anchor_ids,
      source_hashes: profile.source_hashes,
      aggregate_confidence: profile.aggregate_confidence,
      created_at: profile.created_at,
      updated_at: profile.updated_at,
      archived_at: profile.archived_at,
    }
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
      if (change.field_path.endsWith('style_contract')) {
        blueprint.beats[0].style_contract = change.new_value.trim() ? JSON.parse(change.new_value) : null
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
    const hasRecoveredReview = blueprint.beats.some((beat) => beat.paragraph_intention.includes('修复检索缺口') || beat.paragraph_intention.includes('审计修复'))
    const shouldFailReview = blueprint.title.includes('缺陷') && !hasRecoveredReview
    blueprint.status = shouldFailReview ? 'review_failed' : 'reviewed'
    blueprint.latest_review = shouldFailReview
      ? makeReview(blueprint.blueprint_id, `review-${blueprint.blueprint_id}`, {
          status: 'failed',
          score: 0.42,
          reference_binding_errors: ['检索缺口：unknown/restricted 授权过滤后没有合格材料。'],
          material_fit_errors: ['弱匹配：最高候选材料分低于阈值。'],
          required_fixes: ['补充可访问材料或放宽授权策略。'],
          defects: [{
            field_path: 'beat:beat-001:reference_query',
            category: 'material_binding',
            severity: 'high',
            reason: '检索缺口和弱匹配会让候选缺少可审计来源。',
            required_fix: '补充可访问材料或放宽授权策略。',
            beat_id: 'beat-001',
          }],
        })
      : makeReview(blueprint.blueprint_id, `review-${blueprint.blueprint_id}`)
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
    const recoveredAudit = blueprint.beats.some((beat) => beat.paragraph_intention.includes('审计修复'))
    const shouldFailAudit = blueprint.title.includes('缺陷') && !recoveredAudit
    const auditStatus = shouldFailAudit ? 'failed' : 'passed'
    const rewriteLevel = shouldFailAudit ? 'L3' : 'L1'
    const styleContract = blueprint.beats[0].style_contract
    const styleIntensities = (input.style_intensities?.length ? input.style_intensities : [styleContract?.imitation_intensity ?? 'moderate'])
      .slice(0, input.candidates_per_beat || 1)
    const candidates = styleIntensities.map((intensity, index) => ({
      candidate_id: index === 0 ? 'candidate-001' : `candidate-00${index + 1}`,
      blueprint_id: input.blueprint_id,
      beat_id: blueprint.beats[0].beat_id,
      material_id: 'mat-001',
      rewrite_level: rewriteLevel,
      text: shouldFailAudit
        ? '雨声压过门缝里的动静，她突然确认了门外身份，把杯子推远。'
        : '雨声压过门缝里的动静，她把杯子推远，指尖停在那半圈水痕旁，没有立刻抬头。',
      changed_slots: [{ slot_name: 'object', value: '杯子' }],
      non_slot_edits: ['调整为当前 POV 的感官证据。'],
      audit_status: auditStatus,
      created_at: now,
      style_attempts: styleContract
        ? [
            {
              style_profile_ids: styleContract.style_profile_ids,
              style_dimensions: styleContract.style_dimensions,
              imitation_intensity: intensity,
              min_style_fit: styleContract.min_style_fit,
              allowed_closeness: styleContract.allowed_closeness,
              required_evidence_types: styleContract.required_evidence_types,
              forbidden_style_risks: styleContract.forbidden_style_risks,
              selected_material_style_fit: 1.25,
              selected_material_low_confidence: false,
              status: 'attempted',
            },
          ]
        : [],
    }))
    const candidateIds = candidates.map((candidate) => candidate.candidate_id)
    const readableFindings = shouldFailAudit
      ? [
          {
            category: 'provenance',
            severity: 'error',
            candidate_ids: ['candidate-001'],
            message: '候选引用了未绑定的门外身份。',
            required_action: '重新绑定来源材料，或回到蓝图移除未支持事实。',
          },
          {
            category: 'required_fix',
            severity: 'action',
            candidate_ids: ['candidate-001'],
            message: '回到蓝图修订，移除未支持事实并降低改写级别。',
            required_action: '回到蓝图修订，移除未支持事实并降低改写级别。',
          },
        ]
      : []
    return {
      blueprint_id: input.blueprint_id,
      candidates,
      audit: {
        audit_id: 'audit-001',
        blueprint_id: input.blueprint_id,
        status: auditStatus,
        rewrite_level: rewriteLevel,
        provenance_errors: shouldFailAudit ? ['候选引用了未绑定的门外身份。'] : [],
        blueprint_errors: shouldFailAudit ? ['候选越过禁止事实边界。'] : [],
        unsupported_fact_errors: shouldFailAudit ? ['门外身份没有来源材料支持。'] : [],
        pov_errors: shouldFailAudit ? ['POV 获知了当前节拍不可知信息。'] : [],
        ai_prose_risks: shouldFailAudit ? ['突然确认了门外身份过于解释化。'] : [],
        required_fixes: shouldFailAudit ? ['回到蓝图修订，移除未支持事实并降低改写级别。'] : [],
        audited_at: now,
        candidate_ids: candidateIds,
        readable_report: {
          summary: shouldFailAudit
            ? `Draft audit failed for ${candidateIds.length} candidate(s) at rewrite level ${rewriteLevel} with ${readableFindings.length} finding(s).`
            : `Draft audit passed for ${candidateIds.length} candidate(s) at rewrite level ${rewriteLevel}.`,
          candidate_ids: candidateIds,
          findings: readableFindings,
        },
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
        material_use_plan: '使用工作区参考材料中的克制动作证据。 style contracts: beat 1 profiles=301 intensity=strong min_fit=0.8 closeness=moderate dims=dialogue_ratio,sensory_ratio evidence=dialogue_exchange risks=source_leak,style_distance',
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

  function makeReview(blueprintId, reviewId, overrides = {}) {
    return {
      review_id: reviewId,
      blueprint_id: blueprintId,
      context_hash: `context-${blueprintId}`,
      source_plan_hash: `plan-${blueprintId}`,
      analysis_contract_hash: `contract-${blueprintId}`,
      review_version: 1,
      status: overrides.status ?? 'passed',
      score: overrides.score ?? 0.96,
      logic_errors: overrides.logic_errors ?? [],
      causality_errors: overrides.causality_errors ?? [],
      emotion_errors: overrides.emotion_errors ?? [],
      narration_errors: overrides.narration_errors ?? [],
      execution_errors: overrides.execution_errors ?? [],
      character_state_errors: overrides.character_state_errors ?? [],
      pov_errors: overrides.pov_errors ?? [],
      continuity_errors: overrides.continuity_errors ?? [],
      transition_errors: overrides.transition_errors ?? [],
      forbidden_fact_errors: overrides.forbidden_fact_errors ?? [],
      reference_binding_errors: overrides.reference_binding_errors ?? [],
      material_fit_errors: overrides.material_fit_errors ?? [],
      screenplay_drift_risks: overrides.screenplay_drift_risks ?? [],
      ai_prose_risks: overrides.ai_prose_risks ?? [],
      novelistic_narration_errors: overrides.novelistic_narration_errors ?? [],
      required_fixes: overrides.required_fixes ?? [],
      defects: overrides.defects ?? [],
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
