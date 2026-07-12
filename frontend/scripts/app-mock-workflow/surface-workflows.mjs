import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import { setTimeout as delay } from 'node:timers/promises'
import { pathToFileURL } from 'node:url'
import { newAppPage, outputDir } from './app-harness.mjs'
import { realisticWritingText } from './fixtures.mjs'
import {
  assertActiveNovelId,
  assertActiveTabTitle,
  assertBridgeCallCount,
  assertChapterTitle,
  assertCreatedReferenceAnchor,
  assertEditorContains,
  assertEditorNotContains,
  assertExportedNovels,
  assertLastBinaryCall,
  assertLastBridgeCallInput,
  assertNoBridgeCallArgValue,
  assertNovelDeleted,
  assertOnlyTemporaryFixturePaths,
  assertSavedAvatar,
  assertSavedCover,
  assertSearchResultContainsRestrictedSourcePath,
  assertSelectedChapterPath,
  assertStoredContent,
  bridgeCallCount,
  dispatchNovelImportDrop,
  expectHidden,
  expectInputValue,
  expectSelectedValue,
  expectVisible,
  insertEditorText,
  replaceEditorText,
  shortcutKey,
  waitForBridgeCall,
  waitForBridgeCallArg,
  waitForBridgeCallCountAfter,
  waitForSaveContent,
  waitForSaveContentAfter,
} from './page-helpers.mjs'
import {
  assertActiveActivity,
  assertHeaderButtonActive,
  assertNoActiveActivity,
  chapterButton,
  clickActivity,
  ensureChapterBlockExpanded,
  ensureChapterBlockForTitleExpanded,
  novelCard,
  tabLabel,
} from './navigation-helpers.mjs'

export async function verifyShellNavigation(page) {
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

  await clickActivity(page, '素材库')
  await expectVisible(page.getByRole('heading', { name: '选择一个参考来源' }), 'reference materialization workspace from shell navigation')
  await expectVisible(page.getByTestId('reference-book-sidebar'), 'reference source sidebar from shell navigation')

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

export async function verifyChapterWorkflow(page) {
  await clickActivity(page, '章节')
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

export async function verifyEditorSaveWorkflow(browser, url, consoleErrors, pageErrors) {
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

export async function verifyNovelChapterWorkflow(browser, url, consoleErrors, pageErrors) {
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

export async function verifyImportExportFilePickerWorkflow(browser, url, consoleErrors, pageErrors) {
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

  await clickActivity(page, '素材库')
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

export async function verifySearchWorkflow(page) {
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

export async function verifyChatWorkflow(page) {
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

export async function verifyReferenceSmoke(page) {
  await page.getByTitle('素材库').click()
  await expectVisible(page.getByRole('heading', { name: '选择一个参考来源' }), 'reference materialization workspace heading')
  await expectVisible(page.getByText('全局雨夜参考').first(), 'reference anchor fixture')
  await expectVisible(page.getByTestId('blueprint-preview-panel'), 'materialization blueprint preview')
}

export async function verifyReferenceWorkspaceWorkflow(page) {
  await clickActivity(page, '素材库')

  const referenceBooks = page.getByTestId('reference-book-sidebar')
  const blueprintPreview = page.getByTestId('blueprint-preview-panel')
  const corpusWorkspace = page.getByTestId('reference-corpus-workspace')
  await expectVisible(referenceBooks.getByRole('heading', { name: '参考书籍' }), 'reference books sidebar heading')
  await expectVisible(referenceBooks.getByText('全局雨夜参考'), 'reference books sidebar fixture')
  await expectVisible(blueprintPreview.getByRole('heading', { name: 'AI 蓝图预演' }), 'blueprint preview heading')
  await expectVisible(corpusWorkspace.getByRole('heading', { name: '选择一个参考来源' }), 'empty materialization workspace')
  await expectHidden(page.getByText('AI 对话', { exact: true }), 'generic chat is hidden for the corpus workspace')

  await referenceBooks.getByRole('button', { name: '选择《全局雨夜参考》' }).click()
  await expectVisible(corpusWorkspace.getByRole('heading', { name: '全局雨夜参考' }), 'selected materialization source')

  const analyzeCount = await bridgeCallCount(page, 'AnalyzeReferenceChapterSplit')
  await corpusWorkspace.getByRole('button', { name: '自动分析前 50K' }).click()
  await waitForBridgeCallCountAfter(page, 'AnalyzeReferenceChapterSplit', analyzeCount)
  await expectVisible(corpusWorkspace.getByRole('button', { name: '确认章节边界' }), 'chapter split confirmation')

  const confirmCount = await bridgeCallCount(page, 'ConfirmReferenceChapterSplit')
  await corpusWorkspace.getByRole('button', { name: '确认章节边界' }).click()
  await waitForBridgeCallCountAfter(page, 'ConfirmReferenceChapterSplit', confirmCount)
  await corpusWorkspace.getByRole('button', { name: '10' }).click()
  const enqueueCount = await bridgeCallCount(page, 'EnqueueReferenceMaterialization')
  await corpusWorkspace.getByRole('button', { name: '启动材料化' }).click()
  await waitForBridgeCallCountAfter(page, 'EnqueueReferenceMaterialization', enqueueCount)
  await expectVisible(corpusWorkspace.getByText('向量索引完整'), 'completed materialization index state')

  await blueprintPreview.getByLabel('预演目标').fill('让林岚确认门口线索，并在结尾留下新的悬念。')
  await expectVisible(blueprintPreview.getByText('可预演 1 本'), 'active material source count')
  const previewCallCount = await bridgeCallCount(page, 'GenerateReferenceMaterializationBlueprintPreview')
  await blueprintPreview.getByRole('button', { name: '生成预演' }).click()
  await waitForBridgeCallCountAfter(page, 'GenerateReferenceMaterializationBlueprintPreview', previewCallCount)
  await expectVisible(blueprintPreview.getByTestId('blueprint-preview-candidate').first(), 'generated blueprint candidate')
  await page.screenshot({ path: path.join(outputDir, 'materialized-reference-workspace.png'), fullPage: true })

  await referenceBooks.getByRole('button', { name: '归档《全局雨夜参考》为受限语料' }).click()
  await expectVisible(referenceBooks.getByText('确认将工作区语料归档为受限？'), 'workspace corpus archive confirmation')
  const archiveCallCount = await bridgeCallCount(page, 'DeleteReferenceAnchor')
  await referenceBooks.getByRole('button', { name: '确认归档《全局雨夜参考》' }).click()
  await waitForBridgeCallCountAfter(page, 'DeleteReferenceAnchor', archiveCallCount)
  await expectHidden(referenceBooks.getByText('全局雨夜参考'), 'archived workspace corpus')

  await referenceBooks.getByRole('button', { name: '添加参考书籍' }).click()
  await referenceBooks.getByLabel('参考书标题').fill('蓝图预演测试书')
  await referenceBooks.getByLabel('参考书文件路径').fill('D:\\books\\blueprint-preview.md')
  const createCallCount = await bridgeCallCount(page, 'CreateReferenceAnchor')
  await referenceBooks.getByRole('button', { name: /^添加参考书$/ }).click()
  await waitForBridgeCallCountAfter(page, 'CreateReferenceAnchor', createCallCount)
  await expectVisible(referenceBooks.getByText('蓝图预演测试书'), 'created reference book')

  await referenceBooks.getByRole('button', { name: '删除《蓝图预演测试书》' }).click()
  await expectVisible(referenceBooks.getByText('确认删除这本参考书？'), 'reference book deletion confirmation')
  const deleteCallCount = await bridgeCallCount(page, 'DeleteReferenceAnchor')
  await referenceBooks.getByRole('button', { name: '确认删除《蓝图预演测试书》' }).click()
  await waitForBridgeCallCountAfter(page, 'DeleteReferenceAnchor', deleteCallCount)
  await expectHidden(referenceBooks.getByText('蓝图预演测试书'), 'deleted reference book')
}

export async function verifyCompactViewportSmoke(browser, url, consoleErrors, pageErrors) {
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

  await clickActivity(page, '素材库')
  await expectVisible(page.getByRole('heading', { name: '选择一个参考来源' }), 'compact reference materialization workspace')
  await expectVisible(page.getByTestId('reference-book-sidebar'), 'compact reference source sidebar')

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
