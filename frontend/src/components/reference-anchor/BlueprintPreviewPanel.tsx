import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent } from 'react'
import { BookMarked, CircleAlert, Layers3, Loader2, Sparkles, Wand2 } from 'lucide-react'
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
  refreshKey: number
}

function sourceLabel(anchorId: number, anchorsById: Map<number, reference.Anchor>): string {
  return anchorsById.get(anchorId)?.title ?? `来源 #${anchorId}`
}

export default function BlueprintPreviewPanel({
  width,
  onWidthChange,
  onWidthCommit,
  novelId,
  anchors,
  selectedAnchorIds,
  refreshKey,
}: Props) {
  const app = useApp()
  const [isDragging, setIsDragging] = useState(false)
  const [goal, setGoal] = useState('')
  const [requestedCount, setRequestedCount] = useState<1 | 2 | 3>(2)
  const [isCheckingSources, setIsCheckingSources] = useState(false)
  const [isGenerating, setIsGenerating] = useState(false)
  const [readyAnchorIds, setReadyAnchorIds] = useState<number[]>([])
  const [result, setResult] = useState<reference.MaterializationBlueprintPreview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const startXRef = useRef(0)
  const startWidthRef = useRef(width)
  const latestWidthRef = useRef(width)

  const selectedAnchors = useMemo(() => {
    const selected = new Set(selectedAnchorIds)
    return anchors.filter((anchor) => selected.has(anchor.anchor_id))
  }, [anchors, selectedAnchorIds])
  const selectedAnchorKey = selectedAnchors.map((anchor) => anchor.anchor_id).sort((left, right) => left - right).join(',')
  const anchorsById = useMemo(() => new Map(anchors.map((anchor) => [anchor.anchor_id, anchor])), [anchors])
  const readyAnchors = useMemo(() => {
    const ready = new Set(readyAnchorIds)
    return selectedAnchors.filter((anchor) => ready.has(anchor.anchor_id))
  }, [readyAnchorIds, selectedAnchors])

  useEffect(() => {
    latestWidthRef.current = width
  }, [width])

  useEffect(() => {
    let cancelled = false
    const startTimer = window.setTimeout(() => {
      setResult(null)
      setError(null)
      if (!novelId || selectedAnchors.length === 0) {
        setReadyAnchorIds([])
        setIsCheckingSources(false)
        return
      }

      const checkSources = () => {
        void Promise.all(selectedAnchors.map(async (anchor) => {
          const materials = await app.ListActiveReferenceMaterializationMaterials({
            novel_id: novelId,
            anchor_id: anchor.anchor_id,
            page: 1,
            size: 1,
          })
          return (materials.items?.length ?? 0) > 0 ? anchor.anchor_id : null
        }))
          .then((ids) => {
            if (!cancelled) setReadyAnchorIds(ids.filter((id): id is number => id !== null))
          })
          .catch(() => {
            if (!cancelled) {
              setReadyAnchorIds([])
              setError('无法确认来源的 active 材料状态。请在中部检查材料化是否完成。')
            }
          })
          .finally(() => {
            if (!cancelled) setIsCheckingSources(false)
          })
      }

      setIsCheckingSources(true)
      checkSources()
    }, 0)
    return () => {
      cancelled = true
      window.clearTimeout(startTimer)
    }
  }, [app, novelId, refreshKey, selectedAnchorKey, selectedAnchors])

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
    if (event.key === 'ArrowLeft') nextWidth = width + step
    else if (event.key === 'ArrowRight') nextWidth = width - step
    else if (event.key === 'Home') nextWidth = LAYOUT_LIMITS.chat.min
    else if (event.key === 'End') nextWidth = LAYOUT_LIMITS.chat.max
    else return
    event.preventDefault()
    const clamped = clampPanelWidth(nextWidth, LAYOUT_LIMITS.chat.min, LAYOUT_LIMITS.chat.max, LAYOUT_LIMITS.chat.fallback)
    onWidthChange(clamped)
    onWidthCommit(clamped)
  }, [onWidthChange, onWidthCommit, width])

  const generatePreview = async () => {
    if (!novelId || !goal.trim() || readyAnchors.length === 0) return
    setIsGenerating(true)
    setError(null)
    setResult(null)
    try {
      const preview = await app.GenerateReferenceMaterializationBlueprintPreview({
        novel_id: novelId,
        anchor_ids: readyAnchors.map((anchor) => anchor.anchor_id),
        goal: goal.trim(),
        requested_count: requestedCount,
      })
      setResult(preview)
    } catch {
      setError('蓝图预演未完成。请确认所选来源的材料化和向量索引均已完成后重试。')
    } finally {
      setIsGenerating(false)
    }
  }

  const canGenerate = Boolean(novelId && goal.trim() && readyAnchors.length > 0 && !isCheckingSources && !isGenerating)

  return (
    <aside data-testid="blueprint-preview-panel" className="reference-materialization-surface relative flex shrink-0 flex-col overflow-hidden border-l bg-sidebar" style={{ width }} aria-busy={isCheckingSources || isGenerating}>
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
        <p className="mt-1 text-xs text-muted-foreground">只使用已完成大模型准入和向量索引的当前材料。</p>
      </header>

      <div className="min-h-0 flex-1 overflow-y-auto">
        <section className="space-y-3 border-b px-4 py-4" aria-label="蓝图预演参数">
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-foreground">预演目标</span>
            <textarea
              value={goal}
              onChange={(event) => setGoal(event.target.value)}
              className="min-h-24 w-full resize-y rounded-md border border-border bg-background px-2.5 py-2 text-xs leading-5 text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              placeholder="例如：让主角确认线索，并在结尾留下新的冲突。"
              aria-label="预演目标"
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-foreground">方案数</span>
            <select value={requestedCount} onChange={(event) => setRequestedCount(Number(event.target.value) as 1 | 2 | 3)} className="h-8 w-full rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" aria-label="预演方案数">
              <option value="1">1 份</option>
              <option value="2">2 份</option>
              <option value="3">3 份</option>
            </select>
          </label>

          <div className="border border-border bg-muted/25 px-2.5 py-2">
            <div className="flex items-start gap-2">
              <BookMarked className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
              <div className="min-w-0 flex-1">
                <p className="text-xs font-medium text-foreground">已选 {selectedAnchors.length} 本来源 · 可预演 {readyAnchors.length} 本</p>
                {selectedAnchors.length > 0 && <p className="mt-1 break-words text-[11px] leading-4 text-muted-foreground">{selectedAnchors.map((anchor) => anchor.title).join('、')}</p>}
              </div>
            </div>
          </div>

          <button type="button" onClick={() => { void generatePreview() }} disabled={!canGenerate} className="inline-flex h-9 w-full items-center justify-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50">
            {isGenerating ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <Wand2 className="h-3.5 w-3.5" aria-hidden="true" />}
            生成预演
          </button>
        </section>

        {!isCheckingSources && selectedAnchors.length === 0 && !error && (
          <EmptyState icon={<BookMarked className="h-7 w-7 text-muted-foreground/45" aria-hidden="true" />} title="先选择参考来源" description="从左侧选择一本已导入书籍，在中部完成材料化后即可预演。" />
        )}
        {!isCheckingSources && selectedAnchors.length > 0 && readyAnchors.length === 0 && !error && (
          <EmptyState icon={<Layers3 className="h-7 w-7 text-muted-foreground/45" aria-hidden="true" />} title="等待可用材料" description="当前来源尚未完成大模型准入和向量索引；请在中部完成材料化。" />
        )}
        {isCheckingSources && (
          <div className="space-y-2 px-4 py-4" aria-label="正在检查来源材料状态">
            {[0, 1].map((index) => <div key={index} className="h-14 animate-pulse bg-muted" />)}
          </div>
        )}
        {error && <div className="mx-4 mt-4 flex items-start gap-2 border border-destructive/30 bg-destructive/5 px-2.5 py-2 text-xs text-destructive" role="alert"><CircleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" /><span className="min-w-0 break-words">{error}</span></div>}
        {isGenerating && <div className="space-y-2 px-4 py-4" aria-label="正在生成蓝图预演">{[0, 1, 2].map((index) => <div key={index} className="h-20 animate-pulse bg-muted" />)}</div>}
        {!isGenerating && result && result.candidates.length === 0 && !error && <EmptyState icon={<CircleAlert className="h-6 w-6 text-muted-foreground/55" aria-hidden="true" />} title="没有生成可用方案" description="补充预演目标或更换材料化完成的来源后再试。" />}
        {!isGenerating && result && result.candidates.length > 0 && (
          <section className="space-y-3 px-4 py-4" aria-label="蓝图预演结果">
            <div className="flex items-center justify-between gap-3"><h3 className="text-xs font-semibold text-foreground">候选方案</h3><span className="text-[11px] text-muted-foreground">仅供预演</span></div>
            <ol className="space-y-3">
              {result.candidates.map((candidate, index) => (
                <li key={candidate.blueprint_id} data-testid="blueprint-preview-candidate" className="border border-border bg-background px-3 py-3">
                  <p className="text-xs font-medium text-foreground">方案 {index + 1} · {candidate.strategy}</p>
                  <ol className="mt-3 space-y-2 border-l border-border pl-3">
                    {candidate.beats.map((beat) => (
                      <li key={beat.beat_id} className="text-xs leading-5">
                        <p className="text-foreground">{beat.beat_index}. {beat.intent}</p>
                        <p className="text-[11px] text-muted-foreground">{beat.narrative_function}</p>
                        {beat.materials.slice(0, 2).map((material) => <p key={material.material_id} className="mt-1 text-[11px] leading-4 text-muted-foreground">{sourceLabel(material.anchor_id, anchorsById)}：{material.fit_explanation}</p>)}
                      </li>
                    ))}
                  </ol>
                </li>
              ))}
            </ol>
          </section>
        )}
      </div>
      {isDragging && <div className="fixed inset-0 z-50 cursor-col-resize select-none" />}
    </aside>
  )
}

function EmptyState({ icon, title, description }: { icon: React.ReactNode; title: string; description: string }) {
  return <div className="flex min-h-40 flex-col items-center justify-center px-5 text-center" role="status">{icon}<p className="mt-2 text-xs font-medium text-foreground">{title}</p><p className="mt-1 text-xs leading-5 text-muted-foreground">{description}</p></div>
}
