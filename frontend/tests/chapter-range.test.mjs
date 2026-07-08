import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-chapter-range-'))
const outputFile = path.join(tempDir, 'chapterRange.mjs')

try {
  await build({
    entryPoints: ['src/components/chapter/chapterRange.ts'],
    outfile: outputFile,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'es2023',
    logLevel: 'silent',
  })

  const {
    chapterRangesToIds,
    chapterSelectionToRanges,
    invertChapterSelection,
    normalizeChapterRanges,
    selectionSummary,
  } = await import(pathToFileURL(outputFile))

  const chapters = [1, 2, 3, 4, 5, 6].map((chapterNumber) => ({
    id: chapterNumber * 10,
    novel_id: 42,
    chapter_number: chapterNumber,
    title: `第${chapterNumber}章`,
    summary: '',
    word_count: 1000,
    created_at: '2026-07-05T12:00:00.000Z',
    updated_at: '2026-07-05T12:00:00.000Z',
    file_path: `chapters/${chapterNumber}.md`,
  }))

  assert.deepEqual(
    normalizeChapterRanges([
      { start_chapter: 5, end_chapter: 6 },
      { start_chapter: 2, end_chapter: 4 },
      { start_chapter: 4, end_chapter: 5 },
      { start_chapter: 1, end_chapter: 1 },
    ], chapters),
    [{ start_chapter: 1, end_chapter: 6 }],
    'overlapping and adjacent ranges normalize into deterministic merged order',
  )

  assert.throws(
    () => normalizeChapterRanges([{ start_chapter: 0, end_chapter: 2 }], chapters),
    /out of bounds/i,
    'range starts before available chapters are rejected',
  )

  assert.throws(
    () => normalizeChapterRanges([{ start_chapter: 4, end_chapter: 3 }], chapters),
    /greater than end/i,
    'backward ranges are rejected',
  )

  assert.deepEqual(
    chapterSelectionToRanges(
      { mode: 'custom', selectedChapterNumbers: new Set([1, 2, 4, 6]) },
      chapters,
    ),
    [
      { start_chapter: 1, end_chapter: 2 },
      { start_chapter: 4, end_chapter: 4 },
      { start_chapter: 6, end_chapter: 6 },
    ],
    'explicit chapter ids compact into stable backend ranges',
  )

  assert.deepEqual(
    chapterSelectionToRanges({ mode: 'all', selectedChapterNumbers: new Set() }, chapters),
    [{ start_chapter: 1, end_chapter: 6 }],
    'all-chapter mode produces one full backend range',
  )

  assert.deepEqual(
    chapterRangesToIds([{ start_chapter: 2, end_chapter: 3 }, { start_chapter: 5, end_chapter: 5 }], chapters),
    [20, 30, 50],
    'range payload can be expanded to explicit chapter ids',
  )

  assert.deepEqual(
    [...invertChapterSelection(new Set([1, 6]), chapters)],
    [2, 3, 4, 5],
    'invert preserves available chapter ordering',
  )

  assert.equal(
    selectionSummary({ mode: 'custom', selectedChapterNumbers: new Set([1, 2, 4, 6]) }, chapters),
    '已选 4 / 6 章：第 1-2、4、6 章',
    'summary stays compact for disjoint selections',
  )

  assert.equal(
    selectionSummary({ mode: 'all', selectedChapterNumbers: new Set() }, chapters),
    '全部 6 章',
    'summary handles all-chapter mode',
  )
} finally {
  await rm(tempDir, { recursive: true, force: true })
}
