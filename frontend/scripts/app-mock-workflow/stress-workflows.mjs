import assert from 'node:assert/strict'
import { clickActivity, escapeRegExp } from './navigation-helpers.mjs'
import { expectVisible } from './page-helpers.mjs'

export async function verifyStressReferenceMaterialPath(page, referenceStress) {
  await clickActivity(page, '素材库')
  await expectVisible(page.getByRole('heading', { name: '语料库管理' }), 'stress corpus library heading')
  await expectVisible(page.getByText(referenceStress.anchor.title), 'stress reference anchor title')
  await expectVisible(page.getByText(referenceStress.anchor.status), 'stress reference anchor ready state')

  await page.getByTitle('重建语料').first().click()
  await expectVisible(page.getByText('语料已重建'), 'stress anchor rebuild message')

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
