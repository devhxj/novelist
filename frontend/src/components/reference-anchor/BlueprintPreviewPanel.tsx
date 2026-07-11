import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent } from 'react'
import { BookMarked, Check, CircleAlert, Loader2, Sparkles, Wand2 } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { LAYOUT_LIMITS, clampPanelWidth } from '@/lib/layout'
import type { reference } from '@/lib/novelist/types'

type Props = {
  width: number
  onWidthChange: (width: number) => void
  onWidthCommit: (width: number) => void
  novelId: number
  anchors: reference.Anchor[]
  selectedAnchorIds: number[]
}

function isReadyAnchor(anchor: reference.Anchor): boolean {
  return anchor.status === 'ready' || anchor.status === 'completed'
}

function formatCoverage(score: number): string {
  if (!Number.isFinite(score)) return '无数据'
  return `${Math.round(Math.max(0, Math.min(1, score)) * 100)}%`
}

function sourceLabel(anchorId: number, anchorsById: Map<number, reference.Anchor>): string {
  return anchorsById.get(anchorId)?.title ?? `素材 #${anchorId}`
}

export default function BlueprintPreviewPanel({
  width,
  onWidthChange,
  onWidthCommit,
  novelId,
  anchors,
  selectedAnchorIds,
}: Props) {
  const app = useApp()
  const [isDragging, setIsDragging] = useState(false)
  const [goal, setGoal] = useState('')
  const [chapterNumber, setChapterNumber] = useState('1')
  const [contextSummary, setContextSummary] = useState('')
  const [requestedCount, setRequestedCount] = useState('3')
  const [isGenerating, setIsGenerating] = useState(false)
  const [result, setResult] = useState<reference.CorpusBlueprintCandidates | null>(null)
  const [resultScopeKey, setResultScopeKey] = useState('')
  const [selectedBlueprintId, setSelectedBlueprintId] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [errorScopeKey, setErrorScopeKey] = useState('')
  const startXRef = useRef(0)
  const startWidthRef = useRef(width)
  const latestWidthRef = useRef(width)

  useEffect(() => {
    latestWidthRef.current = width
  }, [width])

  const selectedAnchors = useMemo(() => {
    const selected = new Set(selectedAnchorIds)
    return anchors.filter((anchor) => selected.has(anchor.anchor_id) && isReadyAnchor(anchor))
  }, [anchors, selectedAnchorIds])
  const selectedAnchorKey = selectedAnchors.map((anchor) => anchor.anchor_id).sort((left, right) => left - right).join(',')
  const anchorsById = useMemo(() => new Map(anchors.map((anchor) => [anchor.anchor_id, anchor])), [anchors])

  const handleMouseDown = useCallback((event: ReactMouseEvent) => {
    event.preventDefault()
    setIsDragging(true)
    startXRef.current = event.clientX
    startWidthRef.current = width
    latestWidthRef.current = width
  }, [width])

  useEffect(() => {
    if (!isDragging) return
    const previousUserSelect = document.body.style.userSelect
    document.body.style.userSelect = 'none'
    const handleMouseMove = (event: MouseEvent) => {
      const nextWidth = clampPanelWidth(
        startWidthRef.current + startXRef.current - event.clientX,
        LAYOUT_LIMITS.chat.min,
        LAYOUT_LIMITS.chat.max,
        LAYOUT_LIMITS.chat.fallback,
      )
      latestWidthRef.current = nextWidth
      onWidthChange(nextWidth)
    }
    const handleMouseUp = () => {
      setIsDragging(false)
      onWidthCommit(latestWidthRef.current)
    }
    document.addEventListener('mousemove', handleMouseMove)
    document.addEventListener('mouseup', handleMouseUp)
    return () => {
      document.body.style.userSelect = previousUserSelect
      document.removeEventListener('mousemove', handleMouseMove)
      document.removeEventListener('mouseup', handleMouseUp)
    }
  }, [isDragging, onWidthChange, onWidthCommit])

  const handleResizeKeyDown = useCallback((event: ReactKeyboardEvent) => {
    const step = event.shiftKey ? 40 : 16
    let nextWidth: number
    if (event.key === 'ArrowLeft') {
      nextWidth = width + step
    } else if (event.key === 'ArrowRight') {
      nextWidth = width - step
    } else if (event.key === 'Home') {
      nextWidth = LAYOUT_LIMITS.chat.min
    } else if (event.key === 'End') {
      nextWidth = LAYOUT_LIMITS.chat.max
    } else {
      return
    }
    event.preventDefault()
    const clamped = clampPanelWidth(nextWidth, LAYOUT_LIMITS.chat.min, LAYOUT_LIMITS.chat.max, LAYOUT_LIMITS.chat.fallback)
    onWidthChange(clamped)
    onWidthCommit(clamped)
  }, [onWidthChange, onWidthCommit, width])

  const generatePreview = async () => {
    if (!novelId || !goal.trim() || selectedAnchors.length === 0) return

    const parsedChapterNumber = Math.max(1, Number.parseInt(chapterNumber, 10) || 1)
    setIsGenerating(true)
    setError(null)
    setErrorScopeKey(selectedAnchorKey)
    setResult(null)
    setResultScopeKey(selectedAnchorKey)
    setSelectedBlueprintId('')
    try {
      const candidates = await app.GenerateReferenceCorpusBlueprintCandidates({
        natural_language_goal: goal.trim(),
        chapter_context: {
          novel_id: novelId,
          chapter_number: parsedChapterNumber,
          current_draft_text: null,
          insertion_offset: 0,
          previous_chapter_summary: contextSummary.trim() || null,
          character_snapshots: [],
        },
        scope: {
          library_ids: [],
          reuse_policies: ['verbatim_ok', 'adapted_only'],
          include_anchor_ids: selectedAnchors.map((anchor) => anchor.anchor_id),
          exclude_anchor_ids: [],
          session_id: null,
        },
        requested_count: Math.max(2, Number.parseInt(requestedCount, 10) || 3),
      })
      setResult(candidates)
      setResultScopeKey(selectedAnchorKey)
      setSelectedBlueprintId(candidates.candidates[0]?.blueprint.blueprint_id ?? '')
    } catch {
      setError('蓝图预演没有完成，请调整目标或稍后重试。')
      setErrorScopeKey(selectedAnchorKey)
    } finally {
      setIsGenerating(false)
    }
  }

  const visibleResult = resultScopeKey === selectedAnchorKey ? result : null
  const visibleError = errorScopeKey === selectedAnchorKey ? error : null
  const candidates = visibleResult?.candidates ?? []
  const selectedCandidate = candidates.find((candidate) => candidate.blueprint.blueprint_id === selectedBlueprintId) ?? null
  const canGenerate = Boolean(novelId && goal.trim() && selectedAnchors.length > 0 && !isGenerating)

  return (
    <aside
      data-testid="blueprint-preview-panel"
      className="relative flex shrink-0 flex-col overflow-hidden border-l bg-sidebar"
      style={{ width }}
      aria-busy={isGenerating}
    >
      <div
        role="separator"
        aria-label="调整蓝图预演面板宽度"
        aria-orientation="vertical"
        aria-valuemin={LAYOUT_LIMITS.chat.min}
        aria-valuemax={LAYOUT_LIMITS.chat.max}
        aria-valuenow={Math.round(width)}
        tabIndex={0}
        className="absolute bottom-0 left-0 top-0 z-20 w-1 cursor-col-resize bg-transparent transition-colors hover:bg-primary/30 focus-visible:bg-primary/30 focus-visible:outline-none"
        style={{ marginLeft: -2 }}
        onMouseDown={handleMouseDown}
        onKeyDown={handleResizeKeyDown}
      />

      <header className="border-b px-4 py-3">
        <div className="flex items-center gap-2">
          <Sparkles className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
          <h2 className="text-sm font-semibold text-foreground">AI 蓝图预演</h2>
        </div>
        <p className="mt-1 text-xs text-muted-foreground">第 {Math.max(1, Number.parseInt(chapterNumber, 10) || 1)} 章</p>
      </header>

      <div className="min-h-0 flex-1 overflow-y-auto">
        <section className="space-y-3 border-b px-4 py-4" aria-label="蓝图预演参数">
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-foreground">预演目标</span>
            <textarea
              value={goal}
              onChange={(event) => setGoal(event.target.value)}
              className="min-h-20 w-full resize-y rounded-md border border-border bg-background px-2.5 py-2 text-xs leading-5 text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              placeholder="例如：让主角确认线索，并在结尾留下新的冲突。"
              aria-label="预演目标"
            />
          </label>

          <div className="grid grid-cols-[minmax(0,1fr)_6rem] gap-2">
            <label className="block">
              <span className="mb-1 block text-xs font-medium text-foreground">章节</span>
              <input
                value={chapterNumber}
                onChange={(event) => setChapterNumber(event.target.value)}
                min="1"
                inputMode="numeric"
                className="h-8 w-full rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                aria-label="章节号"
              />
            </label>
            <label className="block">
              <span className="mb-1 block text-xs font-medium text-foreground">方案数</span>
              <select
                value={requestedCount}
                onChange={(event) => setRequestedCount(event.target.value)}
                className="h-8 w-full rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                aria-label="预演方案数"
              >
                <option value="2">2 份</option>
                <option value="3">3 份</option>
                <option value="4">4 份</option>
              </select>
            </label>
          </div>

          <label className="block">
            <span className="mb-1 block text-xs font-medium text-foreground">上下文摘要</span>
            <textarea
              value={contextSummary}
              onChange={(event) => setContextSummary(event.target.value)}
              className="min-h-16 w-full resize-y rounded-md border border-border bg-background px-2.5 py-2 text-xs leading-5 text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              placeholder="可选：上一章已知事实、人物状态或限制。"
              aria-label="上下文摘要"
            />
          </label>

          <div className="flex items-start gap-2 rounded-md border border-border bg-muted/35 px-2.5 py-2">
            <BookMarked className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
            <div className="min-w-0 flex-1">
              <p className="text-xs font-medium text-foreground">已选 {selectedAnchors.length} 本参考书</p>
              {selectedAnchors.length > 0 && (
                <p className="mt-1 break-words text-[11px] leading-4 text-muted-foreground">
                  {selectedAnchors.map((anchor) => anchor.title).join('、')}
                </p>
              )}
            </div>
          </div>

          <button
            type="button"
            onClick={() => { void generatePreview() }}
            disabled={!canGenerate}
            className="inline-flex h-9 w-full items-center justify-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {isGenerating ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <Wand2 className="h-3.5 w-3.5" aria-hidden="true" />}
            生成预演
          </button>
        </section>

        {selectedAnchors.length === 0 && !isGenerating && !visibleResult && !visibleError && (
          <div className="flex min-h-40 flex-col items-center justify-center px-5 text-center" role="status">
            <BookMarked className="h-7 w-7 text-muted-foreground/45" aria-hidden="true" />
            <p className="mt-2 text-xs font-medium text-foreground">先选择参考书籍</p>
            <p className="mt-1 text-xs leading-5 text-muted-foreground">从左侧选择已处理完成的参考书后开始预演。</p>
          </div>
        )}

        {isGenerating && (
          <div className="space-y-2 px-4 py-4" aria-label="正在生成蓝图预演">
            {[0, 1, 2].map((index) => <div key={index} className="h-20 animate-pulse rounded-md bg-muted" />)}
          </div>
        )}

        {visibleError && (
          <div className="mx-4 mt-4 flex items-start gap-2 rounded-md border border-destructive/30 bg-destructive/5 px-2.5 py-2 text-xs text-destructive" role="alert">
            <CircleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
            <span className="min-w-0 break-words">{visibleError}</span>
          </div>
        )}

        {!isGenerating && visibleResult && candidates.length === 0 && !visibleError && (
          <div className="flex min-h-40 flex-col items-center justify-center px-5 text-center" role="status">
            <CircleAlert className="h-6 w-6 text-muted-foreground/55" aria-hidden="true" />
            <p className="mt-2 text-xs font-medium text-foreground">没有生成可用方案</p>
            <p className="mt-1 text-xs leading-5 text-muted-foreground">补充目标或更换参考书后再试。</p>
          </div>
        )}

        {!isGenerating && candidates.length > 0 && (
          <section className="space-y-3 px-4 py-4" aria-label="蓝图预演结果">
            <div className="flex items-center justify-between gap-3">
              <h3 className="text-xs font-semibold text-foreground">候选方案</h3>
              <span className="text-[11px] text-muted-foreground">未写入正文</span>
            </div>
            <div className="space-y-2">
              {candidates.map((candidate, index) => {
                const isSelected = candidate.blueprint.blueprint_id === selectedBlueprintId
                return (
                  <button
                    key={candidate.blueprint.blueprint_id}
                    type="button"
                    data-testid="blueprint-preview-candidate"
                    onClick={() => setSelectedBlueprintId(candidate.blueprint.blueprint_id)}
                    className={`w-full rounded-md border px-3 py-2.5 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
                      isSelected ? 'border-primary bg-primary/10' : 'border-border bg-background hover:bg-muted/65'
                    }`}
                    aria-pressed={isSelected}
                    aria-label={`选择方案 ${index + 1}`}
                  >
                    <span className="flex items-center justify-between gap-3">
                      <span className="text-xs font-medium text-foreground">方案 {index + 1}</span>
                      <span className="shrink-0 text-[11px] text-muted-foreground">覆盖 {formatCoverage(candidate.coverage_score)}</span>
                    </span>
                    <span className="mt-1.5 block break-words text-xs leading-5 text-muted-foreground">{candidate.blueprint.strategy}</span>
                    <span className="mt-2 flex flex-wrap gap-x-2 gap-y-1 text-[11px] text-muted-foreground">
                      {candidate.source_distribution.map((source) => (
                        <span key={`${source.library_id}:${source.anchor_id}`}>{sourceLabel(source.anchor_id, anchorsById)} {source.node_count} 段</span>
                      ))}
                    </span>
                  </button>
                )
              })}
            </div>

            {selectedCandidate && (
              <section data-testid="blueprint-preview-selected" className="border-t pt-3" aria-label="已选本轮方案">
                <div className="flex items-center gap-1.5">
                  <Check className="h-3.5 w-3.5 text-emerald-700 dark:text-emerald-400" aria-hidden="true" />
                  <h3 className="text-xs font-semibold text-foreground">已选本轮方案</h3>
                </div>
                <ol className="mt-2 space-y-1.5">
                  {selectedCandidate.blueprint.beats.map((beat, index) => (
                    <li key={beat.beat_id} className="grid grid-cols-[1.25rem_minmax(0,1fr)] gap-2 text-xs leading-5">
                      <span className="text-muted-foreground">{index + 1}</span>
                      <span className="min-w-0 break-words text-foreground">{beat.role_in_beat} · {beat.narrative_function}</span>
                    </li>
                  ))}
                </ol>
                {selectedCandidate.gap_reasons.length > 0 && (
                  <p className="mt-3 break-words text-[11px] leading-4 text-muted-foreground">待补强：{selectedCandidate.gap_reasons.join('；')}</p>
                )}
              </section>
            )}
          </section>
        )}
      </div>

      {isDragging && <div className="fixed inset-0 z-50 cursor-col-resize select-none" />}
    </aside>
  )
}
