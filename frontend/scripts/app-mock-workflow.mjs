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

    logStep('checking bootstrap states')
    await verifyBootstrapStates(browser, url, consoleErrors, pageErrors)

    logStep('loading workspace')
    const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true })
    await page.goto(url, { waitUntil: 'domcontentloaded' })
    await expectVisible(page.getByText('全局回归小说'), 'workspace title')
    await expectVisible(page.getByText('AI 对话'), 'chat panel')
    await page.screenshot({ path: path.join(outputDir, 'app-01-shell.png'), fullPage: true })

    logStep('checking shell navigation')
    await verifyShellNavigation(page)

    logStep('checking chapter/editor path')
    await verifyChapterWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-02-editor.png'), fullPage: true })

    logStep('checking explicit editor save path')
    await verifyEditorSaveWorkflow(browser, url, consoleErrors, pageErrors)

    logStep('checking novel and chapter workflow')
    await verifyNovelChapterWorkflow(browser, url, consoleErrors, pageErrors)

    logStep('checking import export and file-picker paths')
    await verifyImportExportFilePickerWorkflow(browser, url, consoleErrors, pageErrors)

    logStep('checking search path')
    await verifySearchWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-03-search.png'), fullPage: true })

    logStep('checking chat path')
    await verifyChatWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-04-chat.png'), fullPage: true })

    logStep('checking settings path')
    await verifySettingsWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-05-settings.png'), fullPage: true })

    logStep('checking settings persistence path')
    await verifySettingsPersistenceWorkflow(browser, url, consoleErrors, pageErrors)

    logStep('checking metadata panels')
    await verifyMetadataPanels(page)
    await page.screenshot({ path: path.join(outputDir, 'app-06-metadata.png'), fullPage: true })

    logStep('checking reference entry point')
    await verifyReferenceSmoke(page)
    await page.screenshot({ path: path.join(outputDir, 'app-07-reference.png'), fullPage: true })

    logStep('checking bridge guardrails')
    await verifyBridgeCalls(page)
    await page.close()

    assert.deepEqual(pageErrors, [], `Unexpected page errors:\n${pageErrors.join('\n')}`)
    assert.deepEqual(consoleErrors, [], `Unexpected console errors:\n${consoleErrors.join('\n')}`)
    console.log(`App-wide mock workflow passed. Screenshots: ${path.relative(repoRoot, outputDir)}`)
  } finally {
    await browser?.close()
    stopProcess(server)
  }
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

  const bridgeUnavailablePage = await newAppPage(browser, consoleErrors, pageErrors)
  await bridgeUnavailablePage.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(bridgeUnavailablePage.getByRole('heading', { name: '无法连接桌面桥接' }), 'bridge unavailable heading')
  await expectVisible(bridgeUnavailablePage.getByText('请确认正在通过 Novelist 桌面应用打开此界面'), 'bridge unavailable guidance')
  await bridgeUnavailablePage.screenshot({ path: path.join(outputDir, 'app-00-bootstrap.png'), fullPage: true })
  await bridgeUnavailablePage.close()
}

async function newAppPage(browser, consoleErrors, pageErrors, bridgeOptions) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } })
  page.setDefaultTimeout(12_000)
  page.on('console', (message) => {
    if (message.type() === 'error') {
      const text = message.text()
      if (!isIgnorableDevServerConsoleError(text)) {
        consoleErrors.push(text)
      }
    }
  })
  page.on('pageerror', (error) => pageErrors.push(error.message))
  if (bridgeOptions) {
    await page.addInitScript(installConfigurableAppMockBridge, bridgeOptions)
  }
  return page
}

function isIgnorableDevServerConsoleError(text) {
  return /^WebSocket connection to 'ws:\/\/127\.0\.0\.1:\d+\/\?token=[^']+' failed: Error in connection establishment: net::ERR_NO_BUFFER_SPACE$/.test(text)
}

async function verifyShellNavigation(page) {
  await clickActivity(page, '书架')
  await expectVisible(page.getByRole('button', { name: '新建作品' }).last(), 'bookshelf create action')
  await expectVisible(page.getByText('全局回归小说').first(), 'bookshelf novel')

  await clickActivity(page, '章节')
  await expectVisible(page.getByText('章节 (2)'), 'chapter count')
  await expectVisible(page.getByRole('button', { name: /故事状态/ }), 'goink entry')
  await ensureChapterBlockExpanded(page)
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'editor tab from shell navigation')
  await expectVisible(page.locator('.monaco-editor').first(), 'editor surface from shell navigation')
  await expectVisible(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'chat panel from shell navigation')

  await clickActivity(page, '搜索')
  await expectVisible(page.getByPlaceholder('搜索人物、地点、时间线、正文...'), 'search sidebar from shell navigation')
  await expectVisible(page.getByText('输入关键词搜索'), 'search prompt from shell navigation')

  await clickActivity(page, '参考锚定')
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'reference panel from shell navigation')

  await clickActivity(page, '角色')
  await expectVisible(page.getByRole('heading', { name: /角色/ }), 'characters panel from shell navigation')

  await clickActivity(page, '地点')
  await expectVisible(page.getByRole('heading', { name: /地点/ }), 'locations panel from shell navigation')

  await clickActivity(page, '弧线')
  await expectVisible(page.getByRole('heading', { name: /弧线节点/ }), 'story arcs panel from shell navigation')

  await clickActivity(page, '时间线')
  await expectVisible(page.getByRole('heading', { name: /章节计划/ }), 'timeline panel from shell navigation')

  await clickActivity(page, '偏好')
  await expectVisible(page.getByRole('heading', { name: /创作偏好/ }), 'preferences panel from shell navigation')

  await clickActivity(page, '读者视角')
  await expectVisible(page.getByRole('heading', { name: /读者视角/ }), 'reader panel from shell navigation')

  await clickActivity(page, '技能')
  await expectVisible(page.getByText('技能 (2)'), 'skills panel from shell navigation')

  await page.locator('header').getByRole('button', { name: '个人中心' }).click()
  await expectVisible(page.getByText('Mock User'), 'profile panel from shell navigation')
  await expectVisible(page.getByText('累计字数'), 'profile stats from shell navigation')

  await clickActivity(page, '章节')
  await expectVisible(page.getByPlaceholder('输入消息，按 / 调用技能...'), 'chat panel restored after profile navigation')

  await page.locator('header').getByRole('button', { name: '帮助' }).click()
  await expectVisible(page.getByText('欢迎使用 Novelist'), 'help dialog')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()

  await page.locator('header').getByRole('button', { name: '设置' }).click()
  await expectVisible(page.getByText('基础设置'), 'settings affordance from shell navigation')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
}

async function clickActivity(page, label) {
  await page.locator('nav').first().getByRole('button', { name: label }).click()
}

async function verifyChapterWorkflow(page) {
  await page.getByTitle('章节').click()
  await ensureChapterBlockExpanded(page)

  await expectVisible(chapterButton(page, '雨夜线索'), 'first chapter in side panel')
  await chapterButton(page, '雨夜线索').click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'chapter tab title')
  await waitForBridgeCallArg(page, 'GetContent', 1, 'chapters/1.md')

  await page.getByRole('button', { name: /故事状态/ }).click()
  await expectVisible(page.getByText('故事状态').first(), 'story state tab')
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

  const savedText = '林岚在雨夜旧宅门前停住。\n\n她把显式保存片段留在正文里。'
  await replaceEditorText(page, savedText)
  await expectVisible(page.getByText('未保存'), 'dirty status after edit')
  await page.keyboard.press(shortcutKey('S'))
  await waitForSaveContent(page, 'chapters/1.md', '显式保存片段')
  await expectVisible(page.getByText('已保存'), 'saved status after explicit save')
  await assertStoredContent(page, 'chapters/1.md', savedText)
  const saveCountAfterSuccess = await bridgeCallCount(page, 'SaveContent')
  assert(saveCountAfterSuccess >= 1, 'Expected edited chapter to be saved at least once.')

  await page.getByTitle('搜索').click()
  await page.getByTitle('章节').click()
  await ensureChapterBlockExpanded(page)
  await assertBridgeCallCount(page, 'SaveContent', saveCountAfterSuccess)

  await chapterButton(page, '旧城门').click()
  await expectVisible(page.getByText('第2章 旧城门').first(), 'second chapter tab')
  await page.evaluate(() => { window.__appMockState.failNextSaveContent = true })
  await replaceEditorText(page, '旧城门下，保存失败片段仍留在编辑器。')
  await expectVisible(page.getByText('未保存'), 'dirty status after failed edit')
  await page.keyboard.press(shortcutKey('S'))
  await expectVisible(page.getByText('保存失败：模拟保存失败，请重试'), 'save failure alert')
  await expectVisible(page.getByText('未保存'), 'dirty status retained after failed save')
  const saveCountAfterFailure = await bridgeCallCount(page, 'SaveContent')
  assert(saveCountAfterFailure > saveCountAfterSuccess, 'Expected failed explicit save to call SaveContent.')

  await page.getByTitle('搜索').click()
  await delay(700)
  await assertBridgeCallCount(page, 'SaveContent', saveCountAfterFailure)
  await page.close()
}

async function verifyNovelChapterWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, { initialized: true })
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'novel workflow workspace')

  await clickActivity(page, '书架')
  await page.getByRole('button', { name: '新建作品' }).last().click()
  await page.getByPlaceholder('输入书名').fill('回归新书')
  await page.getByPlaceholder('如：玄幻、科幻、都市...').fill('科幻')
  await page.getByPlaceholder('简单介绍一下这部作品（可选）').fill('覆盖小说创建与选择流程')
  await page.locator('.fixed').getByRole('button', { name: '保存' }).click()
  await waitForBridgeCall(page, 'CreateNovel')
  await expectVisible(page.getByText('回归新书').first(), 'created novel visible')
  await expectVisible(page.getByText('章节 (0)'), 'created novel empty chapter count')
  await expectVisible(page.getByText('暂无章节'), 'created novel empty chapter state')
  await assertActiveNovelId(page, 43)

  await clickActivity(page, '书架')
  await page.locator('aside').getByRole('button', { name: /全局回归小说/ }).click()
  await waitForBridgeCallArg(page, 'SetActiveNovel', 0, { novel_id: 42 })
  await expectVisible(page.getByText('章节 (2)'), 'original novel chapter count restored')
  await assertActiveNovelId(page, 42)

  await clickActivity(page, '书架')
  await page.getByRole('button', { name: '编辑作品 全局回归小说' }).click({ force: true })
  await page.getByPlaceholder('输入书名').fill('全局回归小说-修订')
  await page.getByPlaceholder('如：玄幻、科幻、都市...').fill('悬疑')
  await page.getByPlaceholder('简单介绍一下这部作品（可选）').fill('已通过回归流程编辑作品')
  await page.locator('.fixed').getByRole('button', { name: '保存' }).click()
  await waitForBridgeCall(page, 'UpdateNovel')
  await expectVisible(page.getByText('全局回归小说-修订').first(), 'updated novel visible')

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

  await assertBridgeCallCount(page, 'DeleteNovel', 0)
  await assertBridgeCallCount(page, 'SaveCover', 0)
  await assertBridgeCallCount(page, 'ExportNovel', 0)
  await page.close()
}

async function verifyImportExportFilePickerWorkflow(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(browser, consoleErrors, pageErrors, {
    initialized: true,
    pickedReferenceSourceFile: 'D:\\NovelistTestFixtures\\reference-source.md',
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

  await clickActivity(page, '书架')
  await page.getByRole('button', { name: '导出作品 全局回归小说' }).click({ force: true })
  await expectVisible(page.getByRole('heading', { name: '导出作品' }), 'bookshelf export dialog')
  await page.getByRole('button', { name: /TXT/ }).click()
  await page.locator('.fixed').getByRole('button', { name: '导出' }).click()
  await expectVisible(page.getByText('✓ 导出成功'), 'bookshelf export success')
  await waitForBridgeCallArg(page, 'ExportNovel', 1, 'txt')
  await page.locator('.fixed').getByRole('button', { name: '完成' }).click()

  await page.getByRole('button', { name: '更换封面 全局回归小说' }).click({ force: true })
  const coverInput = page.locator('input[type="file"][accept="image/*"]').first()
  await coverInput.setInputFiles({
    name: 'cover.png',
    mimeType: 'image/png',
    buffer: Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
  })
  await waitForBridgeCall(page, 'SaveCover')
  await assertLastBinaryCall(page, 'SaveCover', 8)

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

  await clickActivity(page, '参考锚定')
  await page.getByRole('button', { name: '选择参考源文件' }).click()
  await waitForBridgeCall(page, 'PickReferenceSourceFile')
  await expectVisible(page.locator('input[value="D:\\\\NovelistTestFixtures\\\\reference-source.md"]'), 'picked reference source path')
  await expectSelectedValue(page.locator('select').first(), 'markdown')
  await page.getByPlaceholder('参考书名').fill('文件选择参考')
  await page.getByRole('button', { name: /^创建$/ }).click()
  await waitForBridgeCall(page, 'CreateReferenceAnchor')
  await expectVisible(page.getByText('参考锚点已创建'), 'reference anchor created from picked file')
  await assertCreatedReferenceAnchor(page, {
    title: '文件选择参考',
    sourcePath: 'D:\\NovelistTestFixtures\\reference-source.md',
    sourceKind: 'markdown',
  })

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
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

  await expectVisible(page.getByText('输入关键词搜索'), 'search prompt')

  await searchInput.fill('没有结果')
  await expectVisible(page.getByText('无搜索结果'), 'empty search state')

  await searchInput.fill('搜索失败')
  await expectVisible(page.getByText('搜索失败，请稍后重试'), 'search failure state')
  await page.getByRole('button', { name: '重试' }).click()
  await expectVisible(page.getByText('无搜索结果'), 'search retry recovery')

  await searchInput.fill('雨夜')
  await expectVisible(page.getByText('正文匹配 (1)'), 'content search group')
  await expectVisible(page.getByText('人物 (1)'), 'character search group')
  await expectVisible(page.getByText('语义匹配 (1)'), 'semantic search group')
  await expectVisible(page.getByText('林岚在').first(), 'content result preview')
  await expectHidden(page.getByText('D:\\books\\rain-reference.md'), 'reference source path in global search')

  await page.locator('aside').getByRole('button', { name: /^雨夜线索/ }).click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'search opened chapter')
}

async function verifyChatWorkflow(page) {
  const input = page.getByPlaceholder('输入消息，按 / 调用技能...')
  await input.fill('检查雨夜线索这一章的约束')
  await input.press('Enter')

  await expectVisible(page.getByText('检查雨夜线索这一章的约束'), 'user chat message')
  await expectVisible(page.getByText('读取章节列表').first(), 'tool card')
  await expectVisible(page.getByText('搜索完成').first(), 'web search card')
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
}

async function verifySettingsWorkflow(page) {
  await page.locator('header').getByTitle('设置').click()
  await expectVisible(page.getByText('设置').first(), 'settings dialog')
  await expectVisible(page.getByText('基础设置'), 'general tab')
  await expectVisible(page.locator('input[value="D:\\\\NovelistData"]'), 'data directory')

  await page.getByRole('button', { name: /模型配置/ }).click()
  await expectVisible(page.getByText('内置服务商'), 'builtin model config')
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
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await expectVisible(dialog.getByText('配置已保存'), 'model settings saved')
  await waitForBridgeCall(page, 'TestConnection')
  await waitForBridgeCall(page, 'SaveLLMConfig')
  await assertSavedProviderKey(page, 'mock')

  await dialog.getByRole('button', { name: 'Embeddings' }).click()
  await expectVisible(dialog.getByText('sqlite-vec 已就绪'), 'sqlite vec ready')
  await expectVisible(dialog.getByText('bge-small-zh-v1.5'), 'builtin onnx embedding model')
  await dialog.getByRole('button', { name: '测试' }).click()
  await expectVisible(dialog.getByText('✓ 连通成功'), 'embedding test success')
  await dialog.getByRole('button', { name: '保存配置' }).click()
  await expectVisible(dialog.getByText('配置已保存'), 'embedding settings saved')
  await waitForBridgeCall(page, 'TestEmbeddingConnection')
  await waitForBridgeCall(page, 'SaveEmbeddingConfig')
  await assertSavedEmbeddingProvider(page, 'onnx')

  await assertBridgeCallCount(page, 'DiscoverModels', 0)
  await assertBridgeCallCount(page, 'PickReferenceSourceFile', 0)
  await assertBridgeCallCount(page, 'runtime.shell.openExternal', 0)
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

async function replaceEditorText(page, content) {
  const editor = page.locator('.monaco-editor').first()
  await expectVisible(editor, 'content editor')
  await page.waitForFunction(() => typeof window.__novelistEditor?.setValue === 'function', null, { timeout: 12_000 })
  await page.evaluate((content) => window.__novelistEditor.setValue(content), content)
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

async function assertSavedProviderKey(page, expectedKey) {
  const actual = await page.evaluate(() => window.__appMockState.savedLLMConfig?.providers?.[0]?.key ?? '')
  assert.equal(actual, expectedKey)
}

async function assertSavedEmbeddingProvider(page, expectedProvider) {
  const actual = await page.evaluate(() => window.__appMockState.savedEmbeddingConfig?.provider_key ?? '')
  assert.equal(actual, expectedProvider)
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

async function assertActiveNovelId(page, expectedNovelId) {
  const actual = await page.evaluate(() => window.__appMockState.activeNovelId)
  assert.equal(actual, expectedNovelId)
}

async function assertSelectedChapterPath(page, expectedPath) {
  const activeClasses = await page.locator('aside').getByRole('button', { name: /第\d+章/ }).evaluateAll((buttons) =>
    buttons
      .map((button) => ({ text: button.textContent ?? '', className: button.getAttribute('class') ?? '' }))
      .filter((button) => button.className.includes('bg-primary/10')),
  )
  assert(activeClasses.some((button) => button.text.includes(expectedPath.endsWith('3.md') ? '新章验收-改名' : expectedPath.endsWith('2.md') ? '旧城门' : '雨夜线索')), `Expected selected chapter for ${expectedPath}.`)
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

async function expectSelectedValue(locator, expectedValue) {
  const actual = await locator.inputValue()
  assert.equal(actual, expectedValue)
}

async function assertCreatedReferenceAnchor(page, expected) {
  const actual = await page.evaluate(() => window.__appMockState.createdReferenceAnchors.at(-1))
  assert.equal(actual?.title, expected.title)
  assert.equal(actual?.source_path, expected.sourcePath)
  assert.equal(actual?.source_kind, expected.sourceKind)
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
  console.log(`[app mock] ${message}`)
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
  const state = {
    calls: [],
    activeNovelId: options.settings?.last_novel_id ?? defaultSettings.last_novel_id,
    nextNovelId: 43,
    nextChapterId: 3,
    nextSessionId: 1,
    nextTurnId: 101,
    searchFailureRecovered: false,
    failNextSaveContent: false,
    savedLLMConfig: null,
    savedEmbeddingConfig: null,
    exportedNovels: [],
    savedCovers: [],
    savedAvatars: [],
    createdReferenceAnchors: [],
    contentByPath: {
      'goink.md': '## 当前状态\n林岚正在调查旧城门。',
      'chapters/1.md': '林岚在雨夜旧宅门前停住。\n\n她看见桌上的水痕。',
      'chapters/2.md': '旧城门下，暗号被雨水冲淡。',
      'skills/rhythm.md': '---\nname: 节奏控制\n---\n保持停顿和动作之间的张力。',
      '/builtin/skills/dialogue.md': '---\nname: 对话潜台词\n---\n用话外之意推动场景。',
    },
    initialized: options.initialized ?? true,
    novels: options.novels ?? [defaultNovel],
    chaptersByNovelId: options.chaptersByNovelId ?? { 42: defaultChapters },
    settings: options.settings ?? defaultSettings,
  }

  window.localStorage.removeItem('goink_tabs_all')
  window.localStorage.setItem('theme', 'light')
  window.confirm = () => false

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

      if (envelope.method === 'SaveContent' && !options.allowSaveContent) {
        throw new Error('SaveContent is forbidden in the app-wide smoke unless the test explicitly edits content.')
      }

      const result = await route(envelope.method, args)
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
    respond({ kind: 'event', name, payload })
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
      case 'GetPlatform': return { os: 'win32', defaultPath: options.platformDefaultPath ?? 'D:\\NovelistData' }
      case 'runtime.window.isMaximized': return false
      case 'runtime.window.minimize':
      case 'runtime.window.toggleMaximize':
      case 'runtime.app.quit':
      case 'SetLastSession':
      case 'SetSelectedModel':
      case 'SetReasoningEffort':
      case 'SetApprovalMode':
      case 'SetChatPanelWidth':
      case 'CancelChat':
      case 'ApproveTool':
      case 'RebuildNovelIndex':
      case 'TestConnection':
      case 'TestEmbeddingConnection':
        return null
      case 'SaveLLMConfig':
        state.savedLLMConfig = args[0]
        return null
      case 'SaveEmbeddingConfig':
        state.savedEmbeddingConfig = args[0]
        return null
      case 'GetAppConfig': return { data_dir: options.platformDefaultPath ?? 'D:\\NovelistData' }
      case 'SetActiveNovel':
        state.activeNovelId = args[0]?.novel_id ?? state.activeNovelId
        state.settings.last_novel_id = state.activeNovelId
        return null
      case 'GetNovels': return state.novels
      case 'CreateNovel': return createNovel(args[0])
      case 'UpdateNovel': return updateNovel(args[0], args[1])
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
      case 'GetCharacters': return characters()
      case 'GetCharacterRelations': return []
      case 'GetLocations': return locations()
      case 'GetLocationRelations': return []
      case 'GetStoryArcs': return storyArcs()
      case 'GetArcNodes': return arcNodes()
      case 'GetMaxChapterNumber': return 2
      case 'GetChapterPlans': return chapterPlans()
      case 'GetTimelineEntries': return timelineEntries()
      case 'GetReaderPerspectives': return readerPerspectives()
      case 'GetPreferences': return preferences()
      case 'GetWritingActivity': return writingActivity()
      case 'GetWritingStats': return writingStats()
      case 'ListSkills': return skills()
      case 'GetLLMConfig': return llmConfig()
      case 'GetEmbeddingConfig': return embeddingConfig()
      case 'GetSqliteVecStatus': return sqliteVecStatus()
      case 'GetReferenceAnchors': return referenceAnchors()
      case 'PickReferenceSourceFile': return options.pickedReferenceSourceFile ?? null
      case 'CreateReferenceAnchor': return createReferenceAnchor(args[0])
      case 'GetReferenceChapterBlueprints': return []
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

    if (message.includes('触发失败态')) {
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
      return []
    }
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

  function characters() {
    return [
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
  }

  function locations() {
    return [
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
  }

  function storyArcs() {
    return [
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
  }

  function arcNodes() {
    return [
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
  }

  function chapterPlans() {
    return [
      { novel_id: 42, scope: 'next', content: '下一章继续旧城门调查。' },
      { novel_id: 42, scope: 'near', content: '近期回收桌面水痕。' },
      { novel_id: 42, scope: 'far', content: '远期揭示暗号来源。' },
    ]
  }

  function timelineEntries() {
    return [
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
  }

  function readerPerspectives() {
    return [
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
  }

  function preferences() {
    return {
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
  }

  function writingActivity() {
    return [
      { date: '2026-07-01', words: 800 },
      { date: '2026-07-02', words: 1200 },
    ]
  }

  function writingStats() {
    return {
      total_words: 2180,
      total_days_active: 2,
      current_streak: 2,
      longest_streak: 2,
      total_novels: 1,
      total_chapters: 2,
    }
  }

  function skills() {
    return [
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
      onnx_runtime_path: '',
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
      {
        anchor_id: 101,
        novel_id: 42,
        title: '全局雨夜参考',
        author: 'Mock Author',
        source_path: 'D:\\books\\rain-reference.md',
        source_kind: 'markdown',
        license_status: 'user_provided',
        source_file_hash: 'hash-anchor-app-001',
        build_version: 'mock-reference-v1',
        status: 'ready',
        created_at: now,
        updated_at: now,
      },
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
      source_file_hash: `hash-created-${state.createdReferenceAnchors.length}`,
      build_version: 'mock-reference-v1',
      status: 'ready',
      created_at: now,
      updated_at: now,
    }
    state.createdReferenceAnchors.push(anchor)
    return anchor
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
