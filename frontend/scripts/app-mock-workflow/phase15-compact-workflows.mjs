import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import { newAppPage, outputDir } from './app-harness.mjs'
import { assertGitHistoryReadOnlyCalls } from './bridge-guardrails.mjs'
import {
  assertCopyableDiagnostic,
  assertNoSensitiveDiagnosticsVisible,
  errorAlert,
  installClipboardSpy,
  sensitiveDiagnosticDetails,
} from './diagnostic-helpers.mjs'
import { clickActivity, novelCard } from './navigation-helpers.mjs'
import {
  assertLastBridgeCallInput,
  assertNoBridgeCallArgValue,
  bridgeCallCount,
  dispatchNovelImportDrop,
  expectVisible,
  settingsDialog,
  waitForBridgeCallCountAfter,
} from './page-helpers.mjs'

const compactViewport = { width: 900, height: 720 }

export async function verifyPhase15CompactMatrixWorkflow(browser, url, consoleErrors, pageErrors) {
  const fixtureDir = path.join(outputDir, 'fixtures', 'phase15-compact')
  await fs.mkdir(fixtureDir, { recursive: true })
  const importFixture = path.join(fixtureDir, 'phase15-compact-import.md')
  await fs.writeFile(importFixture, '# 第一章\n\nPhase 15 compact viewport import fixture.\n', 'utf8')

  const page = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    { initialized: true },
    compactViewport,
    'phase15-compact-matrix',
  )
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await expectVisible(page.getByText('全局回归小说'), 'Phase 15 compact workspace')

  await verifyCompactImport(page, importFixture)
  await selectDefaultNovel(page)
  await verifyCompactStyleLibrary(page)
  await verifyCompactPatternProgress(page)
  await verifyCompactGitHistory(page)
  await verifyCompactSettings(page)
  await assertNoImplicitChapterSavesOrExternalOpens(page, 'Phase 15 compact matrix workflow')
  await page.screenshot({ path: path.join(outputDir, 'phase15-compact-matrix.png'), fullPage: true })
  await page.close()

  await verifyCompactErrorCallout(browser, url, consoleErrors, pageErrors)
}

async function verifyCompactImport(page, importFixture) {
  await clickActivity(page, '书架')
  await expectVisible(page.getByRole('button', { name: '导入小说' }), 'compact bookshelf import action')
  await expectVisible(page.getByTestId('novel-import-dropzone'), 'compact import dropzone')

  const startImportBefore = await bridgeCallCount(page, 'StartNovelImport')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: 'phase15-compact-import.md', path: importFixture, type: 'text/markdown' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', startImportBefore)
  await expectVisible(page.getByRole('dialog', { name: '小说导入完成' }), 'compact import completion dialog')
  await expectVisible(page.getByText('100%'), 'compact import completion percent')
  await expectVisible(page.getByText('已导入：phase15-compact-import'), 'compact import completion message')
  await page.getByRole('button', { name: '完成' }).click()

  await clickActivity(page, '书架')
  await expectVisible(novelCard(page, 'phase15-compact-import'), 'compact imported novel card')
  await assertLastBridgeCallInput(page, 'StartNovelImport', {
    source_path: importFixture,
    source_display_name: 'phase15-compact-import.md',
    import_kind: 'markdown',
  })
  await assertNoBridgeCallArgValue(
    page,
    'GetContent',
    importFixture,
    'Compact novel import source path must not route through generic content reads.',
  )
}

async function selectDefaultNovel(page) {
  await clickActivity(page, '书架')
  await page.locator('aside').getByRole('button', { name: '全局回归小说', exact: true }).click()
  await page.waitForFunction(() => window.__appMockState.activeNovelId === 42, null, { timeout: 12_000 })
  const activeNovelId = await page.evaluate(() => window.__appMockState.activeNovelId)
  assert.equal(activeNovelId, 42, 'Phase 15 compact matrix must return to the six-chapter fixture novel.')
}

async function verifyCompactStyleLibrary(page) {
  await clickActivity(page, '风格素材')
  await expectVisible(page.getByRole('heading', { name: /风格素材/ }), 'compact style library heading')
  await expectVisible(page.getByText('全局雨夜节奏').first(), 'compact style library card')
  await page.screenshot({ path: path.join(outputDir, 'phase15-compact-style-library.png'), fullPage: true })
}

async function verifyCompactPatternProgress(page) {
  await clickActivity(page, '叙事模式')
  await expectVisible(page.getByRole('heading', { name: '叙事模式' }), 'compact narrative pattern heading')
  await expectVisible(page.getByRole('heading', { name: '进度时间线' }), 'compact narrative pattern progress panel')

  await page.evaluate(() => { window.__appMockState.nextNarrativePatternDelayMs = 900 })
  await page.getByLabel('技能名称').fill('紧凑视口叙事技能')
  await page.getByRole('button', { name: '开始抽取' }).click()
  await expectVisible(page.getByRole('progressbar'), 'compact narrative pattern progressbar')
  await expectVisible(
    page.getByText(/正在加载并校验章节。|正在识别叙事边界。|章节摘要已完成。|正在压缩叙事阶段/).first(),
    'compact narrative pattern progress message',
  )
  await expectVisible(page.getByRole('heading', { name: '技能预览' }), 'compact narrative pattern preview after progress')
}

async function verifyCompactGitHistory(page) {
  await clickActivity(page, 'Git 历史')
  await expectVisible(page.getByRole('heading', { name: 'Git 历史' }), 'compact Git history heading')
  await expectVisible(page.getByText('rename rain clue chapter').first(), 'compact Git history commit')
  await page.getByRole('button', { name: /rename rain clue chapter/ }).click()
  await expectVisible(page.getByText('chapters/renamed-rain.md').first(), 'compact Git history changed file list')
  await assertGitHistoryReadOnlyCalls(page)
}

async function verifyCompactSettings(page) {
  await page.locator('header').getByTitle('设置').click()
  const dialog = settingsDialog(page)
  await expectVisible(dialog.getByText('基础设置'), 'compact settings general tab')
  await expectVisible(dialog.getByText('Git 提交作者'), 'compact settings Git author section')
  await expectVisible(dialog.getByLabel('作者名称'), 'compact settings Git author name input')
  await expectVisible(dialog.getByLabel('作者邮箱'), 'compact settings Git author email input')
  await page.screenshot({ path: path.join(outputDir, 'phase15-compact-settings.png'), fullPage: true })
  await dialog.getByRole('button', { name: '✕' }).click()
}

async function verifyCompactErrorCallout(browser, url, consoleErrors, pageErrors) {
  const page = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    {
      initialized: true,
      faults: {
        GetGitCommits: {
          mode: 'error',
          code: 'VERSION_CONTROL_ERROR',
          message: 'Compact Git history failed',
          details: sensitiveDiagnosticDetails(),
          retryable: true,
        },
      },
    },
    compactViewport,
    'phase15-compact-error',
  )
  await installClipboardSpy(page)
  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await clickActivity(page, 'Git 历史')
  const gitAlert = errorAlert(page, 'Compact Git history failed')
  await expectVisible(gitAlert, 'compact Git history error alert')
  await assertNoSensitiveDiagnosticsVisible(page)
  await assertCopyableDiagnostic(page, gitAlert, 'GetGitCommits')
  await assertNoImplicitChapterSavesOrExternalOpens(page, 'Phase 15 compact error workflow')
  await page.screenshot({ path: path.join(outputDir, 'phase15-compact-error-callout.png'), fullPage: true })
  await page.close()
}

async function assertNoImplicitChapterSavesOrExternalOpens(page, description) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const chapterSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('chapters/'))
  assert.deepEqual(chapterSaves, [], `${description} must not save chapter content implicitly.`)
  assert.equal(
    calls.some((call) => call.method === 'runtime.shell.openExternal'),
    false,
    `${description} must not open external URLs.`,
  )
}
