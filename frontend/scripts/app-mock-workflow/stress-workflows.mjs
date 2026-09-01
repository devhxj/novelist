import assert from 'node:assert/strict'
import { clickActivity, escapeRegExp } from './navigation-helpers.mjs'
import { expectVisible } from './page-helpers.mjs'

export async function verifyStressReferenceMaterialPath(page, referenceStress) {
  await clickActivity(page, '素材库')
  const referenceBooks = page.getByTestId('reference-book-sidebar')
  const corpusWorkspace = page.getByTestId('reference-corpus-workspace')
  await expectVisible(referenceBooks.getByRole('heading', { name: '参考书籍' }), 'stress corpus books heading')
  await expectVisible(referenceBooks.getByText(referenceStress.anchor.title), 'stress reference anchor title')

  // 当前语料工作台没有重建按钮：重建与构建状态走统一 bridge 直接调用，保持大体量来源的压测覆盖。
  await page.evaluate(async ({ novelId, anchorId }) => {
    await window.novelist.invoke('RebuildReferenceAnchor', { args: [novelId, anchorId] })
  }, { novelId: referenceStress.anchor.novel_id, anchorId: referenceStress.anchor.anchor_id })
  const rebuildStatus = await page.evaluate(async ({ novelId, anchorId }) =>
    await window.novelist.invoke('GetReferenceAnchorBuildStatus', { args: [novelId, anchorId] }),
  { novelId: referenceStress.anchor.novel_id, anchorId: referenceStress.anchor.anchor_id })
  assert.equal(rebuildStatus.source_segment_count, referenceStress.buildStatus.source_segment_count, 'stress build status must preserve source segment count')
  assert.equal(rebuildStatus.material_count, referenceStress.buildStatus.material_count, 'stress build status must preserve material count')

  await referenceBooks.getByRole('button', { name: `选择《${referenceStress.anchor.title}》` }).click()
  await expectVisible(corpusWorkspace.getByRole('heading', { name: referenceStress.anchor.title }), 'stress selected materialization source')

  // 检索压测：大体量语料的分页检索必须在时限内返回且分页稳定。
  const searchStartedAt = Date.now()
  const librarySearch = await page.evaluate(async () =>
    await window.novelist.invoke('SearchReferenceMaterials', { args: [{ novel_id: 42, anchor_ids: [], query: '水痕', page: 1, size: 10 }] }))
  assert.equal(librarySearch.total, referenceStress.materialTotal, 'stress library search must not require manually selected anchors')
  const searchPage1 = await page.evaluate(async (anchorId) =>
    await window.novelist.invoke('SearchReferenceMaterials', { args: [{ novel_id: 42, anchor_ids: [anchorId], query: '水痕', page: 1, size: 10 }] }),
  referenceStress.anchor.anchor_id)
  const materialSearchElapsedMs = Date.now() - searchStartedAt
  assert(materialSearchElapsedMs < 10_000, `stress material search took ${materialSearchElapsedMs}ms`)
  assert.equal(searchPage1.total, referenceStress.materialTotal, 'stress material search must report the full material total')
  assert.equal(searchPage1.items[0].material_id, 'stress-mat-0001', 'stress material search first page id')
  const searchPage2 = await page.evaluate(async (anchorId) =>
    await window.novelist.invoke('SearchReferenceMaterials', { args: [{ novel_id: 42, anchor_ids: [anchorId], query: '水痕', page: 2, size: 10 }] }),
  referenceStress.anchor.anchor_id)
  assert.equal(searchPage2.items[0].material_id, 'stress-mat-0011', 'stress material search next page id')

  return {
    materialSearchElapsedMs,
  }
}
