import { useCallback, useEffect, useMemo, useState } from 'react'
import { ChevronLeft, ChevronRight, Gauge, Hammer, LibraryBig, PackageOpen, RefreshCcw } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reference, storage } from '@/lib/novelist/types'
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
      {tab === 'pack' && <CorpusPack anchors={anchors} />}
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

  const load = useCallback(async () => {
    if (!novelId) return
    setLoading(true)
    setError(null)
    try {
      const [observationTotals, specimenTotals, coverageResult] = await Promise.all([
        Promise.all(usableAnchors.map(async (anchor) => {
          const page = await app.ListReferenceCorpusFeatureObservations({
            novel_id: novelId,
            anchor_id: anchor.anchor_id,
            page_request: { page_size: 1, sort_by: 'feature_family', sort_dir: 'asc' },
          })
          return page.total ?? 0
        })),
        Promise.all(usableAnchors.map(async (anchor) => {
          const page = await app.ListReferenceCorpusTechniqueSpecimens({
            novel_id: novelId,
            anchor_id: anchor.anchor_id,
            page_request: { page_size: 1, sort_by: 'confidence', sort_dir: 'desc' },
          })
          return page.total ?? 0
        })),
        app.GetReferenceMaterialCoverage({ novel_id: novelId, archive_filter: 'active' }),
      ])
      setStats({
        observations: observationTotals.reduce((sum, value) => sum + value, 0),
        specimens: specimenTotals.reduce((sum, value) => sum + value, 0),
      })
      setCoverage(coverageResult)
    } catch {
      setError('语料总览加载失败。请刷新后重试。')
    } finally {
      setLoading(false)
    }
  }, [app, novelId, usableAnchors])

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
            <div className="mt-1 text-lg font-semibold tabular-nums text-foreground">{card.value}</div>
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

  const load = useCallback(async (cursor: string | null) => {
    if (!novelId || anchorId == null) return
    setLoading(true)
    setError(null)
    try {
      if (kind === 'observations') {
        const page: storage.PageRequest = {
          cursor,
          page_size: BROWSE_PAGE_SIZE,
          sort_by: 'feature_family',
          sort_dir: 'asc',
          filters: family ? { feature_family: family } : null,
        }
        setObservations(await app.ListReferenceCorpusFeatureObservations({ novel_id: novelId, anchor_id: anchorId, page_request: page }))
      } else {
        const page: storage.PageRequest = {
          cursor,
          page_size: BROWSE_PAGE_SIZE,
          sort_by: 'confidence',
          sort_dir: 'desc',
          filters: family ? { technique_family: family } : null,
        }
        setSpecimens(await app.ListReferenceCorpusTechniqueSpecimens({ novel_id: novelId, anchor_id: anchorId, page_request: page }))
      }
    } catch {
      setError('语料浏览加载失败。请刷新后重试。')
    } finally {
      setLoading(false)
    }
  }, [app, novelId, anchorId, kind, family])

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
          维度
          <select
            value={family}
            onChange={(event) => { setFamily(event.target.value) }}
            className="h-8 rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="筛选维度"
          >
            <option value="">全部</option>
            {(kind === 'observations'
              ? ['emotion', 'sensory', 'rhythm', 'syntax', 'action', 'interaction', 'pov', 'rhetoric', 'hook', 'narrative']
              : ['emotion', 'rhetoric', 'rhythm', 'action', 'structure']
            ).map((value) => (
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

function CorpusPack({ anchors }: { anchors: reference.Anchor[] }) {
  const usableAnchors = useMemo(() => anchors.filter(isUsableAnchor), [anchors])

  return (
    <div className="min-h-0 flex-1 overflow-y-auto p-4" data-testid="corpus-pack">
      <h2 className="text-sm font-semibold text-foreground">语料包</h2>
      <p className="mt-1 text-xs text-muted-foreground">
        将语料资产（原文引用 + 维度观察 + 证据 + 复核状态）导出为 JSONL，或在另一台设备导入。当前共 {usableAnchors.length} 本可携带的参考书。
      </p>
      <div className="mt-3 rounded-md border border-dashed border-border bg-muted/20 px-3 py-4 text-xs text-muted-foreground">
        导出 / 导入通道建设中：语料包格式与 SafePath 校验落地后，此处提供「导出语料包」与「导入语料包」入口。
      </div>
    </div>
  )
}
