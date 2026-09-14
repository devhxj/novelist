import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Check, ChevronDown, ChevronRight, Sparkles, X } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { describeBridgeError } from '@/lib/novelist/bridgeErrors'
import { describeAnchorStatus } from '@/lib/novelist/referenceAnchorStates'
import type { reference, storage } from '@/lib/novelist/types'
import {
  ADVANCED_MATERIAL_FAMILY_LABELS,
  ADVANCED_MATERIAL_LAYER_LABELS,
  ADVANCED_MATERIAL_REVIEW_STATE_LABELS,
  taxonomyLabel,
} from '@/lib/novelist/corpusTaxonomy'

const PAGE_SIZE = 10
const LAYERS = ['observation', 'specimen', 'strategy'] as const

type Props = {
  novelId: number
  anchors: reference.Anchor[]
  refreshKey: number
}

// 高级写作素材（L2）：按书浏览事实/机理/策略，对照证据原文并复核。
// 未复核素材不进入写作注入（消费侧已在检索层强制），这里是它的复核面。
export default function AdvancedMaterialPanel({ novelId, anchors, refreshKey }: Props) {
  const app = useApp()
  const usableAnchors = useMemo(() => anchors.filter((anchor) => describeAnchorStatus(anchor.status).usable), [anchors])
  const [anchorId, setAnchorId] = useState<number | null>(null)
  const [family, setFamily] = useState('')
  const [layer, setLayer] = useState('')
  const [reviewState, setReviewState] = useState('')
  const [cursors, setCursors] = useState<string[]>([''])
  const [result, setResult] = useState<storage.PageResult_reference_AdvancedMaterialSummary_ | null>(null)
  const [counts, setCounts] = useState<Record<string, number> | null>(null)
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<reference.AdvancedMaterialDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const requestSeqRef = useRef(0)

  useEffect(() => {
    if (anchorId != null || usableAnchors.length === 0) return
    const timer = window.setTimeout(() => setAnchorId(usableAnchors[0].anchor_id), 0)
    return () => window.clearTimeout(timer)
  }, [anchorId, usableAnchors])

  useEffect(() => {
    const timer = window.setTimeout(() => setCursors(['']), 0)
    return () => window.clearTimeout(timer)
  }, [anchorId, family, layer, reviewState])

  const load = useCallback(async () => {
    if (!novelId || anchorId == null) return
    const requestId = ++requestSeqRef.current
    setLoading(true)
    setError(null)
    try {
      const pageResult = await app.ListReferenceAdvancedMaterials({
        anchor_id: anchorId,
        family: family || null,
        layer: layer || null,
        review_state: reviewState || null,
        include_superseded: false,
        page_request: { cursor: cursors[cursors.length - 1] || null, page_size: PAGE_SIZE, sort_by: 'created_at', sort_dir: 'desc' },
      })
      const layerCounts = await Promise.all(
        LAYERS.map(async (entry) => {
          const counted = await app.ListReferenceAdvancedMaterials({
            anchor_id: anchorId,
            family: family || null,
            layer: entry,
            review_state: reviewState || null,
            include_superseded: false,
            page_request: { page_size: 1, sort_by: 'created_at', sort_dir: 'desc' },
          })
          return [entry, counted.total] as const
        }),
      )
      if (requestSeqRef.current === requestId) {
        setResult(pageResult)
        setCounts(Object.fromEntries(layerCounts))
      }
    } catch (caught) {
      if (requestSeqRef.current === requestId) {
        setError(describeBridgeError(caught, '高级素材加载失败。请刷新后重试。').message)
      }
    } finally {
      if (requestSeqRef.current === requestId) setLoading(false)
    }
  }, [app, novelId, anchorId, family, layer, reviewState, cursors])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setExpandedId(null)
      setDetail(null)
      void load()
    }, 0)
    return () => window.clearTimeout(timer)
  }, [load, refreshKey])

  const toggleDetail = useCallback(async (materialId: string) => {
    if (expandedId === materialId) {
      setExpandedId(null)
      setDetail(null)
      return
    }
    if (anchorId == null) return
    setExpandedId(materialId)
    setDetail(null)
    try {
      const loaded = await app.GetReferenceAdvancedMaterialDetail({ anchor_id: anchorId, material_id: materialId })
      setDetail(loaded)
    } catch (caught) {
      setError(describeBridgeError(caught, '高级素材明细加载失败。').message)
    }
  }, [app, anchorId, expandedId])

  const review = useCallback(async (materialId: string, decision: 'confirm' | 'reject') => {
    if (anchorId == null) return
    try {
      const reviewed = await app.ReviewReferenceAdvancedMaterial({ anchor_id: anchorId, material_id: materialId, decision })
      setResult((current) => current == null
        ? current
        : {
            ...current,
            items: current.items.map((item) =>
              item.material_id === materialId ? { ...item, review_state: reviewed.review_state } : item),
          })
    } catch (caught) {
      setError(describeBridgeError(caught, '复核失败。').message)
    }
  }, [app, anchorId])

  if (usableAnchors.length === 0) {
    return (
      <div className="mt-4 rounded-md border border-border bg-background px-3 py-3" data-testid="advanced-material-panel">
        <p className="text-xs text-muted-foreground">暂无可复核的高级素材。先在「制作」完成一本参考书的分析。</p>
      </div>
    )
  }

  return (
    <div className="mt-5 rounded-md border border-border bg-background" data-testid="advanced-material-panel">
      <div className="flex items-center gap-1.5 border-b border-border px-3 py-2">
        <Sparkles className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        <span className="text-xs font-semibold text-foreground">高级写作素材</span>
        <span className="text-[10px] text-muted-foreground">事实 / 机理 / 策略 · 未复核不进入写作注入</span>
      </div>

      <div className="flex flex-wrap items-center gap-2 px-3 py-2">
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          参考书
          <select
            value={anchorId ?? ''}
            onChange={(event) => setAnchorId(Number(event.target.value))}
            className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="选择高级素材参考书"
          >
            {usableAnchors.map((anchor) => (
              <option key={anchor.anchor_id} value={anchor.anchor_id}>{anchor.title}</option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          类别
          <select
            value={family}
            onChange={(event) => setFamily(event.target.value)}
            className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="按类别筛选高级素材"
          >
            <option value="">全部</option>
            {Object.entries(ADVANCED_MATERIAL_FAMILY_LABELS).map(([key, label]) => (
              <option key={key} value={key}>{label}</option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          层级
          <select
            value={layer}
            onChange={(event) => setLayer(event.target.value)}
            className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="按层级筛选高级素材"
          >
            <option value="">全部</option>
            {LAYERS.map((entry) => (
              <option key={entry} value={entry}>{taxonomyLabel(ADVANCED_MATERIAL_LAYER_LABELS, entry)}</option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          复核
          <select
            value={reviewState}
            onChange={(event) => setReviewState(event.target.value)}
            className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="按复核状态筛选高级素材"
          >
            <option value="">全部</option>
            {Object.entries(ADVANCED_MATERIAL_REVIEW_STATE_LABELS).map(([key, label]) => (
              <option key={key} value={key}>{label}</option>
            ))}
          </select>
        </label>
      </div>

      {counts && (
        <div className="flex flex-wrap gap-2 px-3 pb-2 text-[11px] text-muted-foreground" data-testid="advanced-material-counts">
          {LAYERS.map((entry) => (
            <span key={entry} className="rounded-full border border-border bg-muted/40 px-2 py-0.5">
              {taxonomyLabel(ADVANCED_MATERIAL_LAYER_LABELS, entry)} <span className="tabular-nums text-foreground">{counts[entry] ?? 0}</span>
            </span>
          ))}
        </div>
      )}

      {error && (
        <div className="mx-3 mb-2 border border-destructive/30 bg-destructive/5 px-3 py-2 text-xs text-destructive" role="alert">
          <span className="min-w-0 break-words">{error}</span>
        </div>
      )}

      <div className="space-y-1.5 px-3 pb-3" data-testid="advanced-material-list">
        {result == null && loading && <p className="text-xs text-muted-foreground">加载中…</p>}
        {result != null && result.items.length === 0 && (
          <p className="text-xs text-muted-foreground" data-testid="advanced-material-empty">
            当前筛选没有高级素材。分析与复核在「制作」完成后再回到这里查看。
          </p>
        )}
        {result?.items.map((item) => {
          const isExpanded = expandedId === item.material_id
          return (
            <div key={item.material_id} className="rounded border border-border bg-background px-2.5 py-2" data-testid="advanced-material-item">
              <button
                type="button"
                onClick={() => { void toggleDetail(item.material_id) }}
                className="flex w-full items-center gap-1.5 text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                aria-expanded={isExpanded}
              >
                {isExpanded ? <ChevronDown className="h-3 w-3 shrink-0" aria-hidden="true" /> : <ChevronRight className="h-3 w-3 shrink-0" aria-hidden="true" />}
                <span className="shrink-0 text-[11px] font-medium text-foreground">
                  {taxonomyLabel(ADVANCED_MATERIAL_FAMILY_LABELS, item.family)} · {item.feature_key}
                </span>
                <span className="shrink-0 rounded border border-border px-1.5 text-[10px] text-muted-foreground">
                  {taxonomyLabel(ADVANCED_MATERIAL_LAYER_LABELS, item.layer)}
                </span>
                <span className="ml-auto shrink-0 text-[10px] text-muted-foreground">
                  {taxonomyLabel(ADVANCED_MATERIAL_REVIEW_STATE_LABELS, item.review_state)} · 置信 {item.confidence.toFixed(2)}
                </span>
              </button>
              {item.value_text && (
                <p className="mt-1 line-clamp-2 text-[11px] leading-relaxed text-muted-foreground">{item.value_text}</p>
              )}
              <div className="mt-1.5 flex items-center gap-1.5">
                <button
                  type="button"
                  onClick={() => { void review(item.material_id, 'confirm') }}
                  aria-pressed={item.review_state === 'confirmed'}
                  data-testid={`advanced-material-confirm-${item.material_id}`}
                  className={`inline-flex h-6 items-center gap-1 rounded-md border px-2 text-[11px] transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${item.review_state === 'confirmed' ? 'border-emerald-600 bg-emerald-600 text-white' : 'border-border text-muted-foreground hover:bg-muted hover:text-foreground'}`}
                >
                  <Check className="h-3 w-3" aria-hidden="true" />
                  确认
                </button>
                <button
                  type="button"
                  onClick={() => { void review(item.material_id, 'reject') }}
                  aria-pressed={item.review_state === 'rejected'}
                  data-testid={`advanced-material-reject-${item.material_id}`}
                  className={`inline-flex h-6 items-center gap-1 rounded-md border px-2 text-[11px] transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${item.review_state === 'rejected' ? 'border-destructive bg-destructive text-white' : 'border-border text-muted-foreground hover:bg-muted hover:text-foreground'}`}
                >
                  <X className="h-3 w-3" aria-hidden="true" />
                  驳回
                </button>
              </div>
              {isExpanded && (
                <div className="mt-2 space-y-2 border-t border-border pt-2" data-testid="advanced-material-detail">
                  {detail == null && <p className="text-[11px] text-muted-foreground">明细加载中…</p>}
                  {detail?.rationale_json && (
                    <div className="text-[11px] text-muted-foreground">
                      <span className="font-medium text-foreground">为什么这样写：</span>
                      <span className="break-words">{detail.rationale_json}</span>
                    </div>
                  )}
                  {detail?.boundary_json && (
                    <div className="text-[11px] text-muted-foreground">
                      <span className="font-medium text-foreground">失效边界：</span>
                      <span className="break-words">{detail.boundary_json}</span>
                    </div>
                  )}
                  {detail?.transfer_template && (
                    <div className="text-[11px] text-muted-foreground">
                      <span className="font-medium text-foreground">迁移骨架：</span>
                      <span className="break-words">{detail.transfer_template}</span>
                    </div>
                  )}
                  {detail && detail.evidence.length > 0 && (
                    <div className="space-y-1">
                      <div className="text-[11px] font-medium text-foreground">证据原文</div>
                      {detail.evidence.map((span, index) => (
                        <div key={`${span.node_id}-${index}`} className="rounded border border-border bg-muted/30 px-2 py-1 text-[11px] text-muted-foreground">
                          <span className="text-[10px]">{span.node_id} · {span.start_offset}-{span.end_offset}</span>
                          {span.text && <p className="mt-0.5 break-words text-foreground">{span.text}</p>}
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          )
        })}
      </div>

      {result != null && (
        <div className="flex items-center justify-between gap-2 border-t border-border px-3 py-2 text-[11px] text-muted-foreground">
          <span>第 {cursors.length} 页 · 共 {result.total} 条</span>
          <span className="flex gap-1.5">
            <button
              type="button"
              disabled={cursors.length <= 1 || loading}
              onClick={() => setCursors((current) => current.slice(0, -1))}
              className="inline-flex h-7 items-center rounded-md border border-border px-2 hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
            >
              上一页
            </button>
            <button
              type="button"
              disabled={!result.has_more || loading}
              onClick={() => setCursors((current) => [...current, result.next_cursor ?? ''])}
              className="inline-flex h-7 items-center rounded-md border border-border px-2 hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
            >
              下一页
            </button>
          </span>
        </div>
      )}
    </div>
  )
}
