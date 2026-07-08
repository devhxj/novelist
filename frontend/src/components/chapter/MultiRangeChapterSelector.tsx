import { useMemo, useState } from 'react'
import { CheckSquare, Eraser, ListChecks, RotateCcw, Search } from 'lucide-react'
import type { chapter, pattern } from '@/lib/novelist/types'
import {
  chapterSelectionToRanges,
  filterChaptersByQuery,
  invertChapterSelection,
  rangeDraftToSelection,
  selectionSummary,
  type ChapterSelectionState,
} from './chapterRange'

interface Props {
  chapters: chapter.Chapter[]
  value: pattern.ChapterRange[]
  onChange: (ranges: pattern.ChapterRange[]) => void
  disabled?: boolean
  compact?: boolean
}

export default function MultiRangeChapterSelector({
  chapters,
  value,
  onChange,
  disabled = false,
  compact = false,
}: Props) {
  const orderedChapters = useMemo(
    () => [...chapters].sort((left, right) => left.chapter_number - right.chapter_number),
    [chapters],
  )
  const valueSelected = useMemo(() => {
    try {
      return rangeDraftToSelection(value, orderedChapters)
    } catch {
      return new Set<number>()
    }
  }, [orderedChapters, value])
  const allSelected = orderedChapters.length > 0 && valueSelected.size === orderedChapters.length
  const selection = useMemo<ChapterSelectionState>(() => ({
    mode: allSelected ? 'all' : 'custom',
    selectedChapterNumbers: valueSelected,
  }), [allSelected, valueSelected])

  const [query, setQuery] = useState('')
  const [rangeStart, setRangeStart] = useState('')
  const [rangeEnd, setRangeEnd] = useState('')
  const [error, setError] = useState('')

  const filteredChapters = useMemo(
    () => filterChaptersByQuery(orderedChapters, query),
    [orderedChapters, query],
  )
  const summary = selectionSummary(selection, orderedChapters)

  function commitSelection(nextSelection: ChapterSelectionState) {
    setError('')
    onChange(chapterSelectionToRanges(nextSelection, orderedChapters))
  }

  function selectAll() {
    commitSelection({ mode: 'all', selectedChapterNumbers: new Set() })
  }

  function clearSelection() {
    commitSelection({ mode: 'custom', selectedChapterNumbers: new Set() })
  }

  function invertSelection() {
    commitSelection({
      mode: 'custom',
      selectedChapterNumbers: invertChapterSelection(valueSelected, orderedChapters),
    })
  }

  function toggleChapter(chapterNumber: number) {
    const next = new Set(valueSelected)
    if (next.has(chapterNumber)) next.delete(chapterNumber)
    else next.add(chapterNumber)
    commitSelection({ mode: 'custom', selectedChapterNumbers: next })
  }

  function addRange() {
    const start = Number(rangeStart)
    const end = Number(rangeEnd || rangeStart)
    if (!Number.isInteger(start) || !Number.isInteger(end)) {
      setError('请输入有效章节号')
      return
    }

    try {
      const next = rangeDraftToSelection([
        ...chapterSelectionToRanges({ mode: 'custom', selectedChapterNumbers: valueSelected }, orderedChapters),
        { start_chapter: start, end_chapter: end },
      ], orderedChapters)
      commitSelection({ mode: 'custom', selectedChapterNumbers: next })
      setRangeStart('')
      setRangeEnd('')
    } catch (err) {
      setError(err instanceof Error ? err.message : '章节范围无效')
    }
  }

  return (
    <section
      className="flex h-full min-h-0 flex-col border border-border bg-card"
      aria-labelledby="chapter-range-selector-title"
    >
      <div className="border-b border-border px-4 py-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="min-w-0">
            <h2 id="chapter-range-selector-title" className="text-sm font-semibold text-foreground">
              章节范围
            </h2>
            <p className="mt-1 text-xs text-muted-foreground" aria-live="polite">{summary}</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={selectAll}
              disabled={disabled || orderedChapters.length === 0}
              className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background px-2.5 text-xs text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <CheckSquare className="h-3.5 w-3.5" />
              全部
            </button>
            <button
              type="button"
              onClick={invertSelection}
              disabled={disabled || orderedChapters.length === 0}
              className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background px-2.5 text-xs text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <RotateCcw className="h-3.5 w-3.5" />
              反选
            </button>
            <button
              type="button"
              onClick={clearSelection}
              disabled={disabled}
              className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background px-2.5 text-xs text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <Eraser className="h-3.5 w-3.5" />
              清空
            </button>
          </div>
        </div>
      </div>

      <div className="grid gap-3 border-b border-border px-4 py-3 lg:grid-cols-[minmax(0,1fr)_auto]">
        <label className="relative block">
          <span className="sr-only">搜索章节</span>
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <input
            value={query}
            onChange={event => setQuery(event.target.value)}
            disabled={disabled}
            placeholder="搜索章节号或标题"
            className="h-9 w-full rounded-md border border-input bg-background pl-8 pr-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
          />
        </label>

        <div className="flex flex-wrap items-end gap-2">
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-muted-foreground">起始</span>
            <input
              value={rangeStart}
              onChange={event => setRangeStart(event.target.value)}
              disabled={disabled}
              inputMode="numeric"
              className="h-9 w-20 rounded-md border border-input bg-background px-2 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-muted-foreground">结束</span>
            <input
              value={rangeEnd}
              onChange={event => setRangeEnd(event.target.value)}
              disabled={disabled}
              inputMode="numeric"
              className="h-9 w-20 rounded-md border border-input bg-background px-2 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
            />
          </label>
          <button
            type="button"
            onClick={addRange}
            disabled={disabled || rangeStart.trim().length === 0}
            className="inline-flex h-9 items-center gap-1.5 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <ListChecks className="h-4 w-4" />
            添加范围
          </button>
        </div>
      </div>

      {error && (
        <div role="alert" className="border-b border-danger-border bg-danger-bg px-4 py-2 text-xs text-foreground">
          {error}
        </div>
      )}

      <div className={`grid min-h-0 flex-1 overflow-auto ${compact ? 'grid-cols-1' : 'grid-cols-1 sm:grid-cols-2 xl:grid-cols-3'}`}>
        {filteredChapters.length === 0 ? (
          <div className="col-span-full flex min-h-32 items-center justify-center px-4 py-8 text-sm text-muted-foreground">
            {orderedChapters.length === 0 ? '暂无章节' : '没有匹配的章节'}
          </div>
        ) : filteredChapters.map(item => {
          const checked = valueSelected.has(item.chapter_number)
          return (
            <label
              key={item.id}
              className="flex min-h-12 cursor-pointer items-center gap-3 border-b border-r border-border/60 px-3 py-2 text-sm transition-colors hover:bg-muted/40 has-[:disabled]:cursor-not-allowed has-[:disabled]:opacity-60"
            >
              <input
                type="checkbox"
                checked={checked}
                disabled={disabled}
                onChange={() => toggleChapter(item.chapter_number)}
                aria-label={`选择章节 ${item.chapter_number} ${item.title}`}
                className="h-4 w-4 rounded border-input text-primary focus:ring-ring"
              />
              <span className="w-12 shrink-0 text-xs tabular-nums text-muted-foreground">第{item.chapter_number}章</span>
              <span className="min-w-0 flex-1 truncate text-foreground">{item.title}</span>
              {item.word_count > 0 && (
                <span className="shrink-0 text-xs tabular-nums text-muted-foreground">{item.word_count}字</span>
              )}
            </label>
          )
        })}
      </div>
    </section>
  )
}
