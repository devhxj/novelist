import assert from 'node:assert/strict'
import { clickActivity } from './navigation-helpers.mjs'
import {
  assertDisabled,
  expectHidden,
  expectVisible,
  waitForSaveContent,
} from './page-helpers.mjs'

export async function verifyStyleSampleWorkflow(page) {
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

export async function verifyChapterRangeSelectorWorkflow(page) {
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
