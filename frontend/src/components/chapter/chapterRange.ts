import type { chapter, pattern } from '@/lib/novelist/types'

export type ChapterSelectionMode = 'all' | 'custom'

export interface ChapterSelectionState {
  mode: ChapterSelectionMode
  selectedChapterNumbers: Set<number>
}

export interface ChapterRangeDraft {
  start_chapter: number
  end_chapter: number
}

type ChapterLike = Pick<chapter.Chapter, 'id' | 'chapter_number' | 'title'>

export function chapterSelectionToRanges(
  selection: ChapterSelectionState,
  chapters: readonly ChapterLike[],
): pattern.ChapterRange[] {
  const ordered = orderedChapterNumbers(chapters)
  if (ordered.length === 0) return []
  if (selection.mode === 'all') {
    return [{ start_chapter: ordered[0], end_chapter: ordered[ordered.length - 1] }]
  }

  const available = new Set(ordered)
  const selected = [...selection.selectedChapterNumbers]
    .filter((chapterNumber) => available.has(chapterNumber))
    .sort((left, right) => left - right)
  return numbersToRanges(selected)
}

export function normalizeChapterRanges(
  ranges: readonly ChapterRangeDraft[],
  chapters: readonly ChapterLike[],
): pattern.ChapterRange[] {
  const ordered = orderedChapterNumbers(chapters)
  if (ordered.length === 0) {
    if (ranges.length > 0) throw new RangeError('Chapter range is out of bounds because no chapters are available.')
    return []
  }

  const minChapter = ordered[0]
  const maxChapter = ordered[ordered.length - 1]
  const available = new Set(ordered)
  const normalized = ranges.map((range) => {
    const start = normalizeChapterNumber(range.start_chapter, 'start')
    const end = normalizeChapterNumber(range.end_chapter, 'end')
    if (start > end) {
      throw new RangeError('Chapter range start cannot be greater than end.')
    }
    if (start < minChapter || end > maxChapter || !available.has(start) || !available.has(end)) {
      throw new RangeError('Chapter range is out of bounds for available chapters.')
    }

    return { start_chapter: start, end_chapter: end }
  })

  normalized.sort((left, right) =>
    left.start_chapter - right.start_chapter ||
    left.end_chapter - right.end_chapter)

  const merged: pattern.ChapterRange[] = []
  for (const range of normalized) {
    const previous = merged.at(-1)
    if (previous && range.start_chapter <= previous.end_chapter + 1) {
      previous.end_chapter = Math.max(previous.end_chapter, range.end_chapter)
    } else {
      merged.push({ ...range })
    }
  }

  return merged
}

export function chapterRangesToIds(
  ranges: readonly ChapterRangeDraft[],
  chapters: readonly ChapterLike[],
): number[] {
  const normalized = normalizeChapterRanges(ranges, chapters)
  const byNumber = new Map(chapters.map((item) => [item.chapter_number, item.id]))
  const ids: number[] = []
  for (const range of normalized) {
    for (let chapterNumber = range.start_chapter; chapterNumber <= range.end_chapter; chapterNumber += 1) {
      const id = byNumber.get(chapterNumber)
      if (id != null) ids.push(id)
    }
  }

  return ids
}

export function invertChapterSelection(
  selectedChapterNumbers: ReadonlySet<number>,
  chapters: readonly ChapterLike[],
): Set<number> {
  return new Set(orderedChapterNumbers(chapters).filter((chapterNumber) => !selectedChapterNumbers.has(chapterNumber)))
}

export function rangeDraftToSelection(
  ranges: readonly ChapterRangeDraft[],
  chapters: readonly ChapterLike[],
): Set<number> {
  const selected = new Set<number>()
  for (const range of normalizeChapterRanges(ranges, chapters)) {
    for (let chapterNumber = range.start_chapter; chapterNumber <= range.end_chapter; chapterNumber += 1) {
      selected.add(chapterNumber)
    }
  }

  return selected
}

export function selectionSummary(
  selection: ChapterSelectionState,
  chapters: readonly ChapterLike[],
  maxSegments = 5,
): string {
  const total = chapters.length
  if (total === 0) return '暂无章节'
  if (selection.mode === 'all') return `全部 ${total} 章`

  const ranges = chapterSelectionToRanges(selection, chapters)
  const selectedCount = ranges.reduce((sum, range) => sum + range.end_chapter - range.start_chapter + 1, 0)
  if (selectedCount === 0) return `未选择章节 / 共 ${total} 章`

  const shown = ranges
    .slice(0, maxSegments)
    .map((range) => range.start_chapter === range.end_chapter
      ? `${range.start_chapter}`
      : `${range.start_chapter}-${range.end_chapter}`)
  const hiddenCount = ranges.length - shown.length
  const suffix = hiddenCount > 0 ? ` 等 ${ranges.length} 段` : ' 章'
  return `已选 ${selectedCount} / ${total} 章：第 ${shown.join('、')}${suffix}`
}

export function filterChaptersByQuery<T extends ChapterLike>(
  chapters: readonly T[],
  query: string,
): T[] {
  const normalized = query.trim().toLowerCase()
  if (!normalized) return [...chapters]
  return chapters.filter((item) =>
    String(item.chapter_number).includes(normalized) ||
    item.title.toLowerCase().includes(normalized))
}

export function orderedChapterNumbers(chapters: readonly ChapterLike[]): number[] {
  return [...new Set(chapters
    .map((item) => item.chapter_number)
    .filter((chapterNumber) => Number.isInteger(chapterNumber) && chapterNumber > 0))]
    .sort((left, right) => left - right)
}

function numbersToRanges(chapterNumbers: readonly number[]): pattern.ChapterRange[] {
  const ranges: pattern.ChapterRange[] = []
  for (const chapterNumber of chapterNumbers) {
    const previous = ranges.at(-1)
    if (previous && chapterNumber === previous.end_chapter + 1) {
      previous.end_chapter = chapterNumber
    } else {
      ranges.push({ start_chapter: chapterNumber, end_chapter: chapterNumber })
    }
  }

  return ranges
}

function normalizeChapterNumber(value: number, label: string): number {
  if (!Number.isInteger(value) || value <= 0) {
    throw new RangeError(`Chapter range ${label} is out of bounds; expected a positive integer.`)
  }

  return value
}
