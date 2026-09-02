import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ChevronLeft, ChevronRight, Gauge, Hammer, LibraryBig, PackageOpen, RefreshCcw } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { describeBridgeError } from '@/lib/novelist/bridgeErrors'
import type { reference, storage } from '@/lib/novelist/types'
import { OBSERVATION_FAMILIES, SPECIMEN_FAMILIES } from '@/lib/novelist/corpusFamilies'
import ReferenceCorpusWorkspace from './ReferenceCorpusWorkspace'

type Props = {
  novelId: number
  refreshKey: number
  anchors: reference.Anchor[]
  selectedAnchorIds: number[]
  onMaterializationChange: () => void
}

type CorpusTab = 'overview' | 'make' | 'browse' | 'pack'
type BrowseKind = 'observations' | 'specimens'

const BROWSE_PAGE_SIZE = 10

function isUsableAnchor(anchor: reference.Anchor): boolean {
  return anchor.status === 'ready' || anchor.status === 'completed'
}

export default function CorpusAreaView({ novelId, refreshKey, anchors, selectedAnchorIds, onMaterializationChange }: Props) {
  const [tab, setTab] = useState<CorpusTab>('make')

  const tabs: { id: CorpusTab; label: string; icon: typeof Gauge }[] = [
    { id: 'overview', label: '总览', icon: Gauge },
    { id: 'make', label: '制作', icon: Hammer },
    { id: 'browse', label: '浏览', icon: LibraryBig },
    { id: 'pack', label: '语料包', icon: PackageOpen },
  ]

  return (
    <section className="flex min-w-0 flex-1 flex-col overflow-hidden" data-testid="corpus-area">
      <div className="flex shrink-0 items-center gap-1 border-b bg-sidebar px-3 py-2" role="tablist" aria-label="语料视图" data-testid="corpus-area-tabs">
        {tabs.map((entry) => {
          const isActive = entry.id === tab
          return (
            <button
              key={entry.id}
              type="button"
              role="tab"
              aria-selected={isActive}
              onClick={() => setTab(entry.id)}
              className={`inline-flex h-8 items-center gap-1.5 rounded-md px-3 text-xs font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
                ${isActive ? 'bg-muted text-foreground' : 'text-muted-foreground hover:bg-muted/60 hover:text-foreground'}`}
            >
              <entry.icon className="h-3.5 w-3.5" aria-hidden="true" />
              {entry.label}
            </button>
          )
        })}
      </div>

      {tab === 'overview' && <CorpusOverview novelId={novelId} anchors={anchors} refreshKey={refreshKey} onOpenBrowse={() => setTab('browse')} />}
      {tab === 'make' && (
        <ReferenceCorpusWorkspace
          novelId={novelId}
          refreshKey={refreshKey}
          anchors={anchors}
          selectedAnchorIds={selectedAnchorIds}
          onMaterializationChange={onMaterializationChange}
        />
      )}
      {tab === 'browse' && <CorpusBrowse novelId={novelId} anchors={anchors} refreshKey={refreshKey} />}
      {tab === 'pack' && <CorpusPack novelId={novelId} anchors={anchors} />}
    </section>
  )
}

function CorpusOverview({ novelId, anchors, refreshKey, onOpenBrowse }: {
  novelId: number
  anchors: reference.Anchor[]
  refreshKey: number
  onOpenBrowse: () => void
}) {
  const app = useApp()
  const [stats, setStats] = useState<{ observations: number; specimens: number } | null>(null)
  const [coverage, setCoverage] = useState<reference.MaterialCoverage | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const usableAnchors = useMemo(() => anchors.filter(isUsableAnchor), [anchors])

  // 全书聚合端点一次取观察/标本总数，消除逐锚点 N+1。
  const load = useCallback(async () => {
    if (!novelId) return
    setLoading(true)
    setError(null)
    try {
      const [totals, coverageResult] = await Promise.all([
        app.GetReferenceCorpusAssetTotals({ novel_id: novelId }),
        app.GetReferenceMaterialCoverage({ novel_id: novelId, archive_filter: 'active' }),
      ])
      setStats({ observations: totals.observation_total, specimens: totals.specimen_total })
      setCoverage(coverageResult)
    } catch {
      setError('语料总览加载失败。请刷新后重试。')
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      void load()
    }, 0)
    return () => window.clearTimeout(timer)
  }, [load, refreshKey])

  const cards = [
    { label: '参考书', value: usableAnchors.length },
    { label: '特征观察', value: stats?.observations ?? 0 },
    { label: '技法标本', value: stats?.specimens ?? 0 },
    { label: '语料条目', value: coverage?.material_count ?? 0 },
  ]

  return (
    <div className="min-h-0 flex-1 overflow-y-auto p-4" data-testid="corpus-overview">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-sm font-semibold text-foreground">语料资产总览</h2>
        <button
          type="button"
          onClick={() => { void load() }}
          disabled={loading}
          className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-border bg-background text-muted-foreground hover:bg-muted hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
          aria-label="刷新语料总览"
          title="刷新语料总览"
        >
          <RefreshCcw className="h-3.5 w-3.5" aria-hidden="true" />
        </button>
      </div>

      {error && (
        <div className="mt-3 flex items-start gap-2 border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs text-destructive" role="alert">
          <span className="min-w-0 break-words">{error}</span>
        </div>
      )}

      <div className="mt-3 grid grid-cols-2 gap-2 lg:grid-cols-4">
        {cards.map((card) => (
          <div key={card.label} className="rounded-md border border-border bg-background px-3 py-3">
            <div className="text-xs text-muted-foreground">{card.label}</div>
            <div className="mt-1 text-lg font-semibold tabular-nums text-foreground">
              {loading && stats === null ? '—' : card.value}
            </div>
          </div>
        ))}
      </div>

      <h3 className="mt-5 text-xs font-semibold text-foreground">覆盖度地图</h3>
      {coverage && coverage.facets.length > 0 ? (
        <div className="mt-2 space-y-2" data-testid="corpus-coverage-map">
          {coverage.facets.map((facet) => (
            <div key={facet.key} className="rounded-md border border-border bg-background px-3 py-2">
              <div className="text-xs font-medium text-foreground">{facet.key} · {facet.distinct_value_count} 类</div>
              <div className="mt-1.5 flex flex-wrap gap-1.5">
                {facet.values.slice(0, 12).map((value) => (
                  <span key={value.value} className="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2 py-0.5 text-[11px] text-foreground">
                    {value.value}
                    <span className="tabular-nums text-muted-foreground">{value.material_count}</span>
                  </span>
                ))}
              </div>
            </div>
          ))}
        </div>
      ) : (
        <p className="mt-2 text-xs text-muted-foreground">暂无语料覆盖数据。先在「制作」完成一本书的材料化。</p>
      )}

      <button
        type="button"
        onClick={onOpenBrowse}
        className="mt-5 inline-flex h-8 items-center rounded-md border border-border bg-background px-3 text-xs font-medium text-foreground hover:bg-muted"
      >
        去浏览观察与标本
      </button>
    </div>
  )
}

function CorpusBrowse({ novelId, anchors, refreshKey }: {
  novelId: number
  anchors: reference.Anchor[]
  refreshKey: number
}) {
  const app = useApp()
  const usableAnchors = useMemo(() => anchors.filter(isUsableAnchor), [anchors])
  const [anchorId, setAnchorId] = useState<number | null>(null)
  const [kind, setKind] = useState<BrowseKind>('observations')
  const [family, setFamily] = useState('')
  const [keyword, setKeyword] = useState('')
  const [reviewFilter, setReviewFilter] = useState('')
  const [observations, setObservations] = useState<storage.PageResult_reference_CorpusFeatureObservation_ | null>(null)
  const [specimens, setSpecimens] = useState<storage.PageResult_reference_CorpusTechniqueSpecimen_ | null>(null)
  const [cursorStack, setCursorStack] = useState<string[]>([])
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    if (anchorId != null || usableAnchors.length === 0) return
    const timer = window.setTimeout(() => {
      setAnchorId(usableAnchors[0].anchor_id)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [anchorId, usableAnchors])

  const result = kind === 'observations' ? observations : specimens
  const currentPage = cursorStack.length + 1
  const requestSeqRef = useRef(0)

  // seq guard：快速切换参考书/维度/翻页时丢弃过期响应，避免旧数据覆盖新结果。
  const load = useCallback(async (cursor: string | null) => {
    if (!novelId || anchorId == null) return
    const requestId = ++requestSeqRef.current
    setLoading(true)
    setError(null)
    try {
      if (kind === 'observations') {
        const filters: Record<string, string> = {}
        if (family) filters.feature_family = family
        if (keyword.trim()) filters.feature_key = keyword.trim()
        if (reviewFilter) filters.review_state = reviewFilter
        const page: storage.PageRequest = {
          cursor,
          page_size: BROWSE_PAGE_SIZE,
          sort_by: 'feature_family',
          sort_dir: 'asc',
          filters: Object.keys(filters).length > 0 ? filters : null,
        }
        const result = await app.ListReferenceCorpusFeatureObservations({ novel_id: novelId, anchor_id: anchorId, page_request: page })
        if (requestSeqRef.current === requestId) setObservations(result)
      } else {
        const filters: Record<string, string> = {}
        if (family) filters.technique_family = family
        if (reviewFilter) filters.review_state = reviewFilter
        const page: storage.PageRequest = {
          cursor,
          page_size: BROWSE_PAGE_SIZE,
          sort_by: 'confidence',
          sort_dir: 'desc',
          filters: Object.keys(filters).length > 0 ? filters : null,
        }
        const result = await app.ListReferenceCorpusTechniqueSpecimens({ novel_id: novelId, anchor_id: anchorId, page_request: page })
        if (requestSeqRef.current === requestId) setSpecimens(result)
      }
    } catch {
      if (requestSeqRef.current === requestId) setError('语料浏览加载失败。请刷新后重试。')
    } finally {
      if (requestSeqRef.current === requestId) setLoading(false)
    }
  }, [app, novelId, anchorId, kind, family, keyword, reviewFilter])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setCursorStack([])
      setExpandedId(null)
      void load(null)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [load, refreshKey])

  if (usableAnchors.length === 0) {
    return (
      <div className="flex min-h-0 flex-1 items-center justify-center p-6" data-testid="corpus-browse">
        <p className="text-xs text-muted-foreground">暂无可浏览的语料书。先在「制作」导入并完成材料化。</p>
      </div>
    )
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden p-4" data-testid="corpus-browse">
      <div className="flex flex-wrap items-center gap-2">
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          参考书
          <select
            value={anchorId ?? ''}
            onChange={(event) => { setAnchorId(Number(event.target.value)) }}
            className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="选择参考书"
          >
            {usableAnchors.map((anchor) => (
              <option key={anchor.anchor_id} value={anchor.anchor_id}>{anchor.title}</option>
            ))}
          </select>
        </label>
        <div className="flex h-8 items-center rounded-md border border-border bg-background p-0.5" role="tablist" aria-label="浏览内容">
          {([['observations', '特征观察'], ['specimens', '技法标本']] as const).map(([value, label]) => (
            <button
              key={value}
              type="button"
              role="tab"
              aria-selected={kind === value}
              onClick={() => { setKind(value); setFamily('') }}
              className={`h-7 rounded px-2.5 text-xs font-medium transition-colors ${kind === value ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground'}`}
            >
              {label}
            </button>
          ))}
        </div>
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          关键字
          <input
            value={keyword}
            onChange={(event) => { setKeyword(event.target.value) }}
            placeholder={kind === 'observations' ? 'feature_key' : '—'}
            disabled={kind !== 'observations'}
            className="h-8 w-28 rounded-md border border-border bg-background px-2 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-50"
            aria-label="按关键字筛选"
          />
        </label>
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          复核
          <select
            value={reviewFilter}
            onChange={(event) => { setReviewFilter(event.target.value) }}
            className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="按复核状态筛选"
          >
            <option value="">全部</option>
            <option value="unverified">未复核</option>
            <option value="low_confidence">低置信</option>
            <option value="confirmed">已确认</option>
            <option value="rejected">已拒绝</option>
          </select>
        </label>
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          维度
          <select
            value={family}
            onChange={(event) => { setFamily(event.target.value) }}
            className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="筛选维度"
          >
            <option value="">全部</option>
            {(kind === 'observations' ? OBSERVATION_FAMILIES : SPECIMEN_FAMILIES).map((value) => (
              <option key={value} value={value}>{value}</option>
            ))}
          </select>
        </label>
      </div>

      {error && (
        <div className="mt-3 flex items-start gap-2 border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs text-destructive" role="alert">
          <span className="min-w-0 break-words">{error}</span>
        </div>
      )}

      <div className="mt-3 min-h-0 flex-1 space-y-1.5 overflow-y-auto pr-1">
        {result == null && loading && <p className="text-xs text-muted-foreground">加载中…</p>}
        {result != null && result.items.length === 0 && <p className="text-xs text-muted-foreground">当前筛选没有匹配的{kind === 'observations' ? '观察' : '标本'}。</p>}
        {kind === 'observations' && observations?.items.map((item) => (
          <ObservationCard
            key={item.observation_id}
            observation={item}
            isExpanded={expandedId === item.observation_id}
            onToggle={() => { setExpandedId(expandedId === item.observation_id ? null : item.observation_id) }}
          />
        ))}
        {kind === 'specimens' && specimens?.items.map((item) => (
          <SpecimenCard
            key={item.specimen_id}
            specimen={item}
            isExpanded={expandedId === item.specimen_id}
            onToggle={() => { setExpandedId(expandedId === item.specimen_id ? null : item.specimen_id) }}
          />
        ))}
      </div>

      {result != null && (
        <div className="mt-2 flex shrink-0 items-center justify-between gap-2 border-t border-border pt-2 text-[11px] text-muted-foreground">
          <span>
            第 {currentPage} 页{result.total_pages > 0 ? ` / ${result.total_pages}` : ''} · 共 {result.total} 条
          </span>
          <span className="flex gap-1.5">
            <button
              type="button"
              disabled={currentPage <= 1 || loading}
              onClick={() => {
                const previousCursors = cursorStack.slice(0, -1)
                setCursorStack(previousCursors)
                void load(previousCursors.at(-1) ?? null)
              }}
              className="inline-flex h-7 items-center gap-1 rounded-md border border-border px-2 hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
            >
              <ChevronLeft className="h-3 w-3" aria-hidden="true" />
              上一页
            </button>
            <button
              type="button"
              disabled={!result.has_more || loading}
              onClick={() => {
                const nextCursor = result.next_cursor
                if (!nextCursor) return
                setCursorStack([...cursorStack, nextCursor])
                void load(nextCursor)
              }}
              className="inline-flex h-7 items-center gap-1 rounded-md border border-border px-2 hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
            >
              下一页
              <ChevronRight className="h-3 w-3" aria-hidden="true" />
            </button>
          </span>
        </div>
      )}
    </div>
  )
}

function ObservationCard({ observation, isExpanded, onToggle }: {
  observation: reference.CorpusFeatureObservation
  isExpanded: boolean
  onToggle: () => void
}) {
  const app = useApp()
  const [context, setContext] = useState<string | null>(null)
  const [contextError, setContextError] = useState(false)

  useEffect(() => {
    if (!isExpanded || context || contextError) return
    let cancelled = false
    void (async () => {
      try {
        const window = await app.GetReferenceCorpusNodeWindow({
          anchor_id: observation.anchor_id,
          node_id: observation.node_id,
          max_nodes: 3,
        })
        if (cancelled) return
        const text = (window?.chapter_nodes ?? [])
          .map((item) => item.text)
          .filter(Boolean)
          .join('\n……\n')
        setContext(text || null)
      } catch {
        if (!cancelled) setContextError(true)
      }
    })()
    return () => { cancelled = true }
  }, [app, isExpanded, context, contextError, observation.anchor_id, observation.node_id])

  return (
    <div className="rounded-md border border-border bg-background">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={isExpanded}
        className="flex w-full items-start justify-between gap-2 px-3 py-2 text-left hover:bg-muted/40"
      >
        <span className="min-w-0 flex-1">
          <span className="block truncate text-xs font-medium text-foreground">
            {observation.feature_family} · {observation.feature_key} → {observation.value_preview ?? ''}
          </span>
          <span className="mt-0.5 block truncate text-[11px] text-muted-foreground">{observation.observation_id}</span>
        </span>
        <span className="shrink-0 rounded-full border border-border px-1.5 py-0.5 text-[11px] tabular-nums text-muted-foreground">
          {(observation.confidence * 100).toFixed(0)}%
        </span>
      </button>
      {isExpanded && (
        <dl className="space-y-1.5 border-t border-border px-3 py-2.5 text-[11px] text-muted-foreground">
          <div>
            <dt className="font-medium text-foreground">证据</dt>
            <dd className="mt-0.5 whitespace-pre-wrap break-words">{observation.evidence_preview || '（无证据预览）'}</dd>
            {isExpanded && (
              <dd className="mt-1 whitespace-pre-wrap break-words rounded border border-border bg-muted/20 px-2 py-1.5" data-testid="evidence-context">
                {context ?? (contextError ? '（证据上下文不可用）' : '正在加载证据原文…')}
              </dd>
            )}
          </div>
          <div>
            <dt className="font-medium text-foreground">说明</dt>
            <dd className="mt-0.5 whitespace-pre-wrap break-words">{observation.explanation || '（无说明）'}</dd>
          </div>
          <div className="flex gap-3">
            <span>复核状态：{observation.review_state}</span>
            <span>节点：{observation.node_id}</span>
          </div>
        </dl>
      )}
    </div>
  )
}

function SpecimenCard({ specimen, isExpanded, onToggle }: {
  specimen: reference.CorpusTechniqueSpecimen
  isExpanded: boolean
  onToggle: () => void
}) {
  return (
    <div className="rounded-md border border-border bg-background">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={isExpanded}
        className="flex w-full items-start justify-between gap-2 px-3 py-2 text-left hover:bg-muted/40"
      >
        <span className="min-w-0 flex-1">
          <span className="block truncate text-xs font-medium text-foreground">
            {specimen.technique_family} · {specimen.technique_abstract}
          </span>
          <span className="mt-0.5 block truncate text-[11px] text-muted-foreground">{specimen.specimen_id}</span>
        </span>
        <span className="shrink-0 rounded-full border border-border px-1.5 py-0.5 text-[11px] tabular-nums text-muted-foreground">
          {(specimen.confidence * 100).toFixed(0)}%
        </span>
      </button>
      {isExpanded && (
        <dl className="space-y-1.5 border-t border-border px-3 py-2.5 text-[11px] text-muted-foreground">
          <div>
            <dt className="font-medium text-foreground">证据</dt>
            <dd className="mt-0.5 whitespace-pre-wrap break-words">
              {specimen.evidence.map((entry) => entry.evidence_preview).filter(Boolean).join('\n') || '（无证据预览）'}
            </dd>
          </div>
          <div>
            <dt className="font-medium text-foreground">触发语境</dt>
            <dd className="mt-0.5 whitespace-pre-wrap break-words">{specimen.trigger_context || '（无说明）'}</dd>
          </div>
          <div className="flex gap-3">
            <span>复核状态：{specimen.review_state}</span>
            <span>节点：{specimen.source_node_id}</span>
          </div>
        </dl>
      )}
    </div>
  )
}

function CorpusPack({ novelId, anchors }: { novelId: number; anchors: reference.Anchor[] }) {
  const app = useApp()
  const usableAnchors = useMemo(() => anchors.filter(isUsableAnchor), [anchors])
  const [anchorId, setAnchorId] = useState<number | null>(null)
  const [busy, setBusy] = useState<'export' | 'import' | null>(null)
  const [message, setMessage] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null)

  useEffect(() => {
    if (anchorId != null || usableAnchors.length === 0) return
    const timer = window.setTimeout(() => {
      setAnchorId(usableAnchors[0]?.anchor_id ?? null)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [anchorId, usableAnchors])

  const exportPackage = async () => {
    if (anchorId == null) return
    setBusy('export')
    setMessage(null)
    try {
      const result = await app.ExportReferenceCorpusPackage({ novel_id: novelId, anchor_id: anchorId })
      setMessage({ tone: 'ok', text: `已导出 ${result.observation_count} 条观察、${result.specimen_count} 条标本 → ${result.file_path}` })
    } catch (err) {
      const diagnostic = describeBridgeError(err, '语料包导出失败。')
      setMessage({
        tone: diagnostic.code === 'materialization_cancelled' ? 'ok' : 'error',
        text: diagnostic.message,
      })
    } finally {
      setBusy(null)
    }
  }

  const importPackage = async () => {
    if (anchorId == null) return
    setBusy('import')
    setMessage(null)
    try {
      const result = await app.ImportReferenceCorpusPackage({ novel_id: novelId, anchor_id: anchorId })
      setMessage({ tone: 'ok', text: `导入完成：新增 ${result.imported_count} 条，跳过已存在 ${result.skipped_count} 条（观察 ${result.observation_count} / 标本 ${result.specimen_count}）。` })
    } catch (err) {
      const diagnostic = describeBridgeError(err, '语料包导入失败。')
      setMessage({
        tone: diagnostic.code === 'materialization_cancelled' ? 'ok' : 'error',
        text: diagnostic.message,
      })
    } finally {
      setBusy(null)
    }
  }

  return (
    <div className="min-h-0 flex-1 overflow-y-auto p-4" data-testid="corpus-pack">
      <h2 className="text-sm font-semibold text-foreground">语料包</h2>
      <p className="mt-1 text-xs text-muted-foreground">
        将参考书的语料资产（观察 + 标本 + 证据原文）导出为 JSONL 备份；导入按同书恢复语义合并，已存在的条目自动跳过。当前共 {usableAnchors.length} 本可携带的参考书。
      </p>
      {usableAnchors.length === 0 ? (
        <div className="mt-3 rounded-md border border-dashed border-border bg-muted/20 px-3 py-4 text-xs text-muted-foreground">
          暂无可导出的语料书。先在「制作」完成一本书的材料化。
        </div>
      ) : (
        <>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
              参考书
              <select
                value={anchorId ?? ''}
                onChange={(event) => { setAnchorId(Number(event.target.value)) }}
                className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                aria-label="选择要导出的参考书"
              >
                {usableAnchors.map((anchor) => (
                  <option key={anchor.anchor_id} value={anchor.anchor_id}>{anchor.title}</option>
                ))}
              </select>
            </label>
            <button
              type="button"
              onClick={() => { void exportPackage() }}
              disabled={busy !== null || anchorId == null}
              className="inline-flex h-8 items-center rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
              data-testid="corpus-pack-export"
            >
              {busy === 'export' ? '导出中…' : '导出语料包'}
            </button>
            <button
              type="button"
              onClick={() => { void importPackage() }}
              disabled={busy !== null || anchorId == null}
              className="inline-flex h-8 items-center rounded-md border border-border px-3 text-xs font-medium text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50"
              data-testid="corpus-pack-import"
            >
              {busy === 'import' ? '导入中…' : '导入语料包'}
            </button>
          </div>
          {message && (
            <div
              role={message.tone === 'error' ? 'alert' : 'status'}
              className={`mt-3 rounded-md border px-3 py-2 text-xs ${message.tone === 'error' ? 'border-destructive/30 bg-destructive/5 text-destructive' : 'border-border bg-muted/30 text-foreground'}`}
              data-testid="corpus-pack-message"
            >
              {message.text}
            </div>
          )}
          <p className="mt-3 text-[11px] leading-relaxed text-muted-foreground">
            提示：导入为同书备份恢复语义——观察/标本需要原文证据节点，跨设备迁移请在同一本书的文本树存在时执行。
          </p>
        </>
      )}
    </div>
  )
}
