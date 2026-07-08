import assert from 'node:assert/strict'
import fs from 'node:fs/promises'
import path from 'node:path'
import { assertGitHistoryReadOnlyCalls } from './bridge-guardrails.mjs'
import { makeLargeChineseFixture } from './fixtures.mjs'
import { newAppPage, outputDir, runConfig } from './app-harness.mjs'
import { clickActivity, escapeRegExp, novelCard } from './navigation-helpers.mjs'
import {
  assertBridgeCallCount,
  assertLastBridgeCallInput,
  assertNoBridgeCallArgValue,
  bridgeCallCount,
  dispatchNovelImportDrop,
  expectVisible,
  waitForBridgeCallCountAfter,
} from './page-helpers.mjs'

const IMPORT_DISPLAY_NAME = 'phase15-large-import.md'
const IMPORT_TITLE = 'phase15-large-import'
const DIFF_PATH = 'chapters/phase15-large-diff.md'
const DIFF_MARKER = 'phase15-large-diff-line'
const TARGET_DIFF_COMMIT_INDEX = 4

const COMMIT_IDS = [
  'ffffffffffffffffffffffffffffffffffffffff',
  'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
  'dddddddddddddddddddddddddddddddddddddddd',
  'cccccccccccccccccccccccccccccccccccccccc',
  'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
  'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  '9999999999999999999999999999999999999999',
  '8888888888888888888888888888888888888888',
]

const COMMIT_MESSAGES = [
  'phase15 checkpoint 08 polish newest',
  'phase15 checkpoint 07 revise timeline',
  'phase15 checkpoint 06 prepare cursor',
  'phase15 checkpoint 05 expand pressure set',
  'phase15 large truncated diff',
  'phase15 checkpoint 03 split outline',
  'phase15 checkpoint 02 seed import',
  'phase15 checkpoint 01 initial baseline',
]

export async function verifyPhase15StressWorkflow(browser, url, consoleErrors, pageErrors) {
  const startedAt = Date.now()
  const fixtureDir = path.join(outputDir, 'fixtures', 'phase15-stress')
  await fs.mkdir(fixtureDir, { recursive: true })

  const importFixture = await writeLargeImportFixture(fixtureDir)
  const gitFixture = makeLargeGitFixture()
  const page = await newAppPage(
    browser,
    consoleErrors,
    pageErrors,
    {
      initialized: true,
      gitCommits: gitFixture.commits,
      gitCommitFilesByCommitId: gitFixture.filesByCommitId,
      gitDiffsByCommitAndPath: gitFixture.diffsByCommitAndPath,
    },
    { width: 1360, height: 900 },
    'phase15-stress',
  )

  await page.goto(url, { waitUntil: 'domcontentloaded' })
  await verifyLargeImport(page, importFixture.path)
  await verifyLargeGitHistory(page, gitFixture)

  const totalBridgeCallCount = await page.evaluate(() => window.__appMockState.calls.length)
  await fs.writeFile(
    path.join(outputDir, 'phase15-stress-metrics.json'),
    `${JSON.stringify({
      importFixturePath: importFixture.path,
      importDisplayName: IMPORT_DISPLAY_NAME,
      importKind: 'markdown',
      importTargetBytes: runConfig.stressSizeBytes,
      importActualBytes: importFixture.actualBytes,
      gitCommitCount: gitFixture.commits.length,
      diffPath: DIFF_PATH,
      diffMarker: DIFF_MARKER,
      bridgeCallCount: totalBridgeCallCount,
      elapsedMs: Date.now() - startedAt,
    }, null, 2)}\n`,
    'utf8',
  )

  await assertBridgeCallCount(page, 'SaveContent', 0)
  await assertGitHistoryReadOnlyCalls(page)
  await page.close()
}

async function writeLargeImportFixture(fixtureDir) {
  const content = makeLargeChineseFixture(runConfig.stressSizeBytes)
  const fixturePath = path.join(fixtureDir, IMPORT_DISPLAY_NAME)
  await fs.writeFile(fixturePath, content, 'utf8')
  const stat = await fs.stat(fixturePath)
  assert(path.isAbsolute(fixturePath), 'Phase 15 import fixture path must be absolute.')
  assert(
    stat.size >= runConfig.stressSizeBytes,
    `Expected import fixture to be at least ${runConfig.stressSizeBytes} bytes, got ${stat.size}.`,
  )
  return { path: fixturePath, actualBytes: stat.size }
}

async function verifyLargeImport(page, importPath) {
  await expectVisible(page.getByText('全局回归小说'), 'Phase 15 stress workspace title')
  await clickActivity(page, '书架')

  const startImportBefore = await bridgeCallCount(page, 'StartNovelImport')
  await dispatchNovelImportDrop(page, {
    kind: 'files',
    files: [{ name: IMPORT_DISPLAY_NAME, path: importPath, type: 'text/markdown' }],
  })
  await waitForBridgeCallCountAfter(page, 'StartNovelImport', startImportBefore)

  const importDialog = page.getByRole('dialog', { name: '小说导入完成' })
  await expectVisible(importDialog, 'Phase 15 large import completion dialog')
  await expectVisible(importDialog.getByText('100%'), 'Phase 15 large import completion percent')
  await expectVisible(importDialog.getByText(`已导入：${IMPORT_TITLE}`), 'Phase 15 large import success message')
  await assertLastBridgeCallInput(page, 'StartNovelImport', {
    source_path: importPath,
    source_display_name: IMPORT_DISPLAY_NAME,
    import_kind: 'markdown',
  })
  await assertNoBridgeCallArgValue(
    page,
    'GetContent',
    importPath,
    'Phase 15 import source path must not route through generic content reads.',
  )
  await assertBridgeCallCount(page, 'SaveContent', 0)

  await importDialog.getByRole('button', { name: '完成' }).click()
  await expectVisible(page.getByText('导入开篇').first(), 'Phase 15 imported opening chapter')
  await clickActivity(page, '书架')
  await expectVisible(novelCard(page, IMPORT_TITLE), 'Phase 15 imported novel card')
  await assertNoBridgeCallArgValue(
    page,
    'GetContent',
    importPath,
    'Phase 15 import source path must stay out of GetContent after import completion.',
  )
}

async function verifyLargeGitHistory(page, gitFixture) {
  await clickActivity(page, 'Git 历史')
  await expectVisible(page.getByRole('heading', { name: 'Git 历史' }), 'Phase 15 Git history heading')
  await expectVisible(page.getByText(`${gitFixture.commits.length} 个提交`), 'Phase 15 Git history total count')
  await expectVisible(page.getByText(gitFixture.commits[0].message).first(), 'Phase 15 newest Git commit')
  await expectVisible(page.getByText(gitFixture.commits[2].message).first(), 'Phase 15 first Git page boundary commit')

  const targetCommit = await ensureCommitVisibleByPaging(page, gitFixture.targetCommit.message)
  await ensureCommitVisibleByPaging(page, gitFixture.commits.at(-1).message)
  await expectVisible(page.getByText('已到最早提交'), 'Phase 15 Git history end marker')
  await assertGitCursorPaging(page, gitFixture)

  await targetCommit.click()
  await expectVisible(page.getByText(DIFF_PATH).first(), 'Phase 15 large diff changed file entry')
  await page.getByRole('button', { name: new RegExp(escapeRegExp(DIFF_PATH)) }).click()
  await expectVisible(page.getByRole('heading', { name: DIFF_PATH }), 'Phase 15 large diff heading')
  await expectVisible(page.getByText('内容已截断'), 'Phase 15 truncated diff badge')
  await expectVisible(page.getByText(DIFF_MARKER).first(), 'Phase 15 large diff marker')
}

async function ensureCommitVisibleByPaging(page, commitMessage) {
  const target = page.getByRole('button', { name: new RegExp(escapeRegExp(commitMessage)) })
  const commitList = page.locator('[aria-label="Git 提交列表"]')

  for (let attempt = 0; attempt < 6; attempt += 1) {
    if (await target.isVisible().catch(() => false)) {
      return target
    }

    const loadOlder = page.getByRole('button', { name: '加载更早提交' })
    if (await loadOlder.isVisible().catch(() => false)) {
      const callsBefore = await bridgeCallCount(page, 'GetGitCommits')
      await loadOlder.click()
      await waitForBridgeCallCountAfter(page, 'GetGitCommits', callsBefore).catch(() => {})
    } else if (await commitList.isVisible().catch(() => false)) {
      await commitList.evaluate((node) => {
        node.scrollTop = node.scrollHeight
      })
    }

    await page.waitForTimeout(250)
  }

  await expectVisible(target, `Phase 15 Git commit ${commitMessage}`)
  return target
}

async function assertGitCursorPaging(page, gitFixture) {
  const calls = await page.evaluate(() =>
    window.__appMockState.calls
      .filter((call) => call.method === 'GetGitCommits')
      .map((call) => ({
        input: call.args?.[0] ?? null,
        total: call.result?.total ?? null,
      })))

  assert(calls.length >= 3, `Phase 15 Git history must request at least three pages, got ${calls.length}.`)
  assert(calls.some((call) => call.total === gitFixture.commits.length), 'Phase 15 Git history must report the full commit total.')
  assert(
    calls.every((call) => call.input?.size === 3),
    'Phase 15 Git history must request GitHistoryView PAGE_SIZE=3.',
  )
  assert(
    calls.some((call) => call.input?.cursor_commit_id === gitFixture.commits[2].commit_id),
    'Phase 15 Git history must use the first page tail commit as a cursor.',
  )
  assert(
    calls.some((call) => call.input?.cursor_commit_id === gitFixture.commits[5].commit_id),
    'Phase 15 Git history must use the second page tail commit as a cursor.',
  )
}

function makeLargeGitFixture() {
  const commits = COMMIT_IDS.map((commitId, index) => {
    const isTarget = index === TARGET_DIFF_COMMIT_INDEX
    return {
      commit_id: commitId,
      short_commit_id: commitId.slice(0, 7),
      author_name: 'Phase 15 Bot',
      author_email: 'phase15@example.test',
      message: COMMIT_MESSAGES[index],
      committed_at: `2026-07-05T12:${String(28 - index * 2).padStart(2, '0')}:00.000Z`,
      changed_file_count: isTarget ? 2 : 1,
      insertions: isTarget ? 1800 : 5 + index,
      deletions: isTarget ? 1200 : index,
    }
  })

  const filesByCommitId = {}
  const diffsByCommitAndPath = {}
  for (const [index, commit] of commits.entries()) {
    const isTarget = index === TARGET_DIFF_COMMIT_INDEX
    const filePath = isTarget ? DIFF_PATH : `notes/phase15-${String(8 - index).padStart(2, '0')}.md`
    filesByCommitId[commit.commit_id] = [
      {
        path: filePath,
        old_path: null,
        change_type: 'modified',
        additions: isTarget ? 1800 : 5 + index,
        deletions: isTarget ? 1200 : index,
        binary: false,
      },
    ]
    diffsByCommitAndPath[`${commit.commit_id}:${filePath}`] = isTarget
      ? makeLargeTruncatedDiff(commit.commit_id)
      : makeSmallDiff(commit.commit_id, filePath, commit.message)
  }

  const targetCommit = commits[TARGET_DIFF_COMMIT_INDEX]
  return { commits, filesByCommitId, diffsByCommitAndPath, targetCommit }
}

function makeLargeTruncatedDiff(commitId) {
  const originalContent = [
    '# Phase 15 large diff before',
    '旧稿只保留首尾片段，完整大 diff 不进入 mock payload。',
    'phase15-large-diff-before-line-0001',
  ].join('\n')
  const modifiedContent = [
    '# Phase 15 large diff after',
    `${DIFF_MARKER}-0001: 截断 diff 中保留的可见断言片段。`,
    `${DIFF_MARKER}-0002: 大体量正文只在文件导入 fixture 中生成。`,
  ].join('\n')

  return {
    commit_id: commitId,
    path: DIFF_PATH,
    old_path: null,
    change_type: 'modified',
    diff_text: [
      `diff --git a/${DIFF_PATH} b/${DIFF_PATH}`,
      '@@ -4800,6 +4800,8 @@',
      '-旧稿尾部片段',
      `+${DIFF_MARKER}-0001 truncated payload slice`,
      `+${DIFF_MARKER}-0002 marker remains visible`,
    ].join('\n'),
    truncated: true,
    binary: false,
    original_content: originalContent,
    modified_content: modifiedContent,
  }
}

function makeSmallDiff(commitId, filePath, message) {
  return {
    commit_id: commitId,
    path: filePath,
    old_path: null,
    change_type: 'modified',
    diff_text: [
      `diff --git a/${filePath} b/${filePath}`,
      '@@',
      `-${message} old`,
      `+${message} new`,
    ].join('\n'),
    truncated: false,
    binary: false,
    original_content: `${message} old`,
    modified_content: `${message} new`,
  }
}
