import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ChevronLeft, ChevronRight, Gauge, Hammer, LibraryBig, PackageOpen, RefreshCcw } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { describeBridgeError } from '@/lib/novelist/bridgeErrors'
import { describeAnchorStatus } from '@/lib/novelist/referenceAnchorStates'
import type { reference, storage } from '@/lib/novelist/types'
import {
  COVERAGE_FACET_LABELS,
  FEATURE_VALUE_LABELS,
  MATERIAL_TYPE_LABELS,
  taxonomyLabel,
} from '@/lib/novelist/corpusTaxonomy'
import ReferenceCorpusWorkspace from './ReferenceCorpusWorkspace'

type Props = {
  novelId: number
  refreshKey: number
  anchors: reference.Anchor[]
  selectedAnchorIds: number[]
  /** 「制作」当前在做的参考书；null 时回落到第一本选中的书。 */
  activeAnchorId: number | null
  onActiveAnchorChange: (anchorId: number) => void
  onMaterializationChange: () => void
}

type CorpusTab = 'overview' | 'make' | 'browse' | 'pack'

// 覆盖度地图下钻：点某个维度取值 → 打开素材列表并带上该筛选。
// nonce 让"再点同一个取值"也能重新定位（只有对象身份变化才会重跑下钻 effect）。
type MaterialDrilldown = { facetKey: string; value: string; nonce: number }

// 覆盖度 facet 维度与素材检索入参一一对应，下钻不需要中间层。
type MaterialFacetKey = 'material_type' | 'function_tag' | 'emotion_tag' | 'scene_tag' | 'pov_tag' | 'technique_tag'

const MATERIAL_FACET_KEYS: readonly string[] = [
  'material_type',
  'function_tag',
  'emotion_tag',
  'scene_tag',
  'pov_tag',
  'technique_tag',
]

function isMaterialFacetKey(key: string): key is MaterialFacetKey {
  return MATERIAL_FACET_KEYS.includes(key)
}

// 覆盖度是全书口径，下钻必须能跨书检索，否则"地图说 1 条、列表 0 条"。
const ALL_ANCHORS = 0

const BROWSE_PAGE_SIZE = 10

function isUsableAnchor(anchor: reference.Anchor): boolean {
  return describeAnchorStatus(anchor.status).usable
}

/** facet 取值的中文标签：素材类型走自己的词表，其余是特征/标签词表。 */
function facetValueLabel(facetKey: string, value: string): string {
  return facetKey === 'material_type'
    ? taxonomyLabel(MATERIAL_TYPE_LABELS, value)
    : taxonomyLabel(FEATURE_VALUE_LABELS, value)
}

export default function CorpusAreaView({ novelId, refreshKey, anchors, selectedAnchorIds, activeAnchorId, onActiveAnchorChange, onMaterializationChange }: Props) {
  const [tab, setTab] = useState<CorpusTab>('make')
  const [drilldown, setDrilldown] = useState<MaterialDrilldown | null>(null)

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

      {tab === 'overview' && (
        <CorpusOverview
          novelId={novelId}
          anchors={anchors}
          refreshKey={refreshKey}
          onOpenMaterials={(facetKey, value) => {
            setDrilldown({ facetKey, value, nonce: Date.now() })
            setTab('browse')
          }}
        />
      )}
      {tab === 'make' && (
        <ReferenceCorpusWorkspace
          novelId={novelId}
          refreshKey={refreshKey}
          anchors={anchors}
          selectedAnchorIds={selectedAnchorIds}
          activeAnchorId={activeAnchorId}
          onActiveAnchorChange={onActiveAnchorChange}
          onMaterializationChange={onMaterializationChange}
        />
      )}
      {tab === 'browse' && (
        <CorpusBrowse
          novelId={novelId}
          anchors={anchors}
          refreshKey={refreshKey}
          drilldown={drilldown}
          onClearDrilldown={() => setDrilldown(null)}
        />
      )}
      {tab === 'pack' && <CorpusPack novelId={novelId} anchors={anchors} />}
    </section>
  )
}

function CorpusOverview({ novelId, anchors, refreshKey, onOpenMaterials }: {
  novelId: number
  anchors: reference.Anchor[]
  refreshKey: number
  onOpenMaterials: (facetKey: string, value: string) => void
}) {
  const app = useApp()
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
      const coverageResult = await app.GetReferenceMaterialCoverage({ novel_id: novelId, archive_filter: 'active' })
      setCoverage(coverageResult)
    } catch (error) {
      // E4：透出后端诊断而不是固定文案，让失败可排查。
      setError(describeBridgeError(error, '语料总览加载失败。请刷新后重试。').message)
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

  // 数字全部来自材料化的产出（当前语料实现：整章抽取 + 多维度标签），不再掺入其它管线的计数。
  const materialTypeCount = coverage?.facets.find((facet) => facet.key === 'material_type')?.distinct_value_count ?? 0
  const tagFacetValueCount = (coverage?.facets ?? [])
    .filter((facet) => facet.key !== 'material_type')
    .reduce((total, facet) => total + facet.distinct_value_count, 0)
  const cards = [
    { label: '参考书', value: usableAnchors.length, source: '导入的语料来源' },
    { label: '语料条目', value: coverage?.material_count ?? 0, source: '整章抽取的素材' },
    { label: '素材类型', value: materialTypeCount, source: '句子 / 段落 / 场景' },
    { label: '标签取值', value: tagFacetValueCount, source: '五个标签维度上的取值数' },
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
              {loading && coverage === null ? '—' : card.value}
            </div>
            <div className="mt-0.5 text-[10px] text-muted-foreground" data-testid={`corpus-card-source-${card.label}`}>{card.source}</div>
          </div>
        ))}
      </div>

      <h3 className="mt-5 text-xs font-semibold text-foreground">素材覆盖度地图</h3>
      {coverage && coverage.facets.length > 0 ? (
        <>
          {/* 维度归属要说清：这几个维度是「素材」的标签（材料化产物），不是观察/标本的维度。
              数字还要能追到条目，所以每个取值都是下钻入口。 */}
          <p className="mt-1 text-[11px] leading-5 text-muted-foreground">
            维度是「素材」自己的标签（来自整章抽取）；点任意取值可下钻到对应素材。
          </p>
          <div className="mt-2 space-y-2" data-testid="corpus-coverage-map">
            {coverage.facets.map((facet) => (
              <div key={facet.key} className="rounded-md border border-border bg-background px-3 py-2">
                <div className="text-xs font-medium text-foreground">{taxonomyLabel(COVERAGE_FACET_LABELS, facet.key)} · {facet.distinct_value_count} 类</div>
                <div className="mt-1.5 flex flex-wrap gap-1.5">
                  {facet.values.slice(0, 12).map((value) => (
                    <button
                      key={value.value}
                      type="button"
                      onClick={() => onOpenMaterials(facet.key, value.value)}
                      data-testid={`coverage-drilldown-${facet.key}-${value.value}`}
                      title={`查看「${facetValueLabel(facet.key, value.value)}」的素材`}
                      className="inline-flex items-center gap-1 rounded-full border border-border bg-muted/40 px-2 py-0.5 text-[11px] text-foreground transition-colors hover:border-primary/50 hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    >
                      {facetValueLabel(facet.key, value.value)}
                      <span className="tabular-nums text-muted-foreground">{value.material_count}</span>
                      <ChevronRight className="h-3 w-3 text-muted-foreground" aria-hidden="true" />
                    </button>
                  ))}
                </div>
              </div>
            ))}
          </div>
        </>
      ) : (
        <p className="mt-2 text-xs text-muted-foreground">暂无语料覆盖数据。先在「制作」完成一本书的材料化。</p>
      )}

      <button
        type="button"
        onClick={() => onOpenMaterials('all', '')}
        data-testid="corpus-open-materials"
        className="mt-5 inline-flex h-8 items-center rounded-md border border-border bg-background px-3 text-xs font-medium text-foreground hover:bg-muted"
      >
        浏览全部素材
      </button>
    </div>
  )
}

function CorpusBrowse({ novelId, anchors, refreshKey, drilldown, onClearDrilldown }: {
  novelId: number
  anchors: reference.Anchor[]
  refreshKey: number
  drilldown: MaterialDrilldown | null
  onClearDrilldown: () => void
}) {
  const app = useApp()
  const usableAnchors = useMemo(() => anchors.filter(isUsableAnchor), [anchors])
  const [anchorId, setAnchorId] = useState<number | null>(null)
  const [keyword, setKeyword] = useState('')
  const [materials, setMaterials] = useState<storage.PageResult_reference_MaterialSummary_ | null>(null)
  const [materialFilter, setMaterialFilter] = useState<{ facetKey: string; value: string } | null>(null)
  const [materialPage, setMaterialPage] = useState(1)
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

  // 下钻落地的唯一入口：地图点取值（或"浏览全部素材"）后，切到素材页并带上该筛选。
  useEffect(() => {
    if (!drilldown) return
    const timer = window.setTimeout(() => {
      // 地图统计的是全书，所以下钻默认看全部参考书；未知维度只跳到素材页、不加筛选。
      setMaterialFilter(
        drilldown.facetKey === 'all' || !isMaterialFacetKey(drilldown.facetKey)
          ? null
          : { facetKey: drilldown.facetKey, value: drilldown.value },
      )
      setAnchorId(ALL_ANCHORS)
      setMaterialPage(1)
      setExpandedId(null)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [drilldown])

  // 换参考书/改关键字/改筛选都回到第 1 页，避免停在越界页码上看到空列表。
  useEffect(() => {
    const timer = window.setTimeout(() => setMaterialPage(1), 0)
    return () => window.clearTimeout(timer)
  }, [anchorId, keyword, materialFilter])

  const result = materials
  const currentPage = materialPage
  // 空列表要分清"筛选太窄"和"本来就没有"：混成一句会把上游缺失误报成筛选问题。
  const hasBrowseFilter = materialFilter != null || keyword.trim().length > 0
  const requestSeqRef = useRef(0)

  // seq guard：快速切换参考书/筛选/翻页时丢弃过期响应，避免旧数据覆盖新结果。
  const load = useCallback(async () => {
    if (!novelId || anchorId == null) return
    const requestId = ++requestSeqRef.current
    setLoading(true)
    setError(null)
    try {
      // 下钻筛选落到检索契约的对应字段：维度与取值一一对应，不做二次解释。
      const onFacet = (facetKey: MaterialFacetKey) =>
        materialFilter?.facetKey === facetKey ? [materialFilter.value] : []
      const page = await app.SearchReferenceMaterials({
        novel_id: novelId,
        // 全部参考书（0）时不带锚点过滤，与覆盖度地图的全书口径一致。
        anchor_ids: anchorId > ALL_ANCHORS ? [anchorId] : [],
        query: keyword.trim(),
        material_types: onFacet('material_type'),
        function_tags: onFacet('function_tag'),
        emotion_tags: onFacet('emotion_tag'),
        scene_tags: onFacet('scene_tag'),
        pov_tags: onFacet('pov_tag'),
        technique_tags: onFacet('technique_tag'),
        page: materialPage,
        size: BROWSE_PAGE_SIZE,
        archive_filter: 'active',
        ready_only: true,
      })
      if (requestSeqRef.current === requestId) setMaterials(page)
    } catch (error) {
      // E4：透出后端诊断（如词表漂移的 invalid_filter_value）而不是笼统的"加载失败"。
      if (requestSeqRef.current === requestId) setError(describeBridgeError(error, '语料浏览加载失败。请刷新后重试。').message)
    } finally {
      if (requestSeqRef.current === requestId) setLoading(false)
    }
  }, [app, novelId, anchorId, keyword, materialFilter, materialPage])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setExpandedId(null)
      void load()
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
            <option value={ALL_ANCHORS}>全部参考书</option>
            {usableAnchors.map((anchor) => (
              <option key={anchor.anchor_id} value={anchor.anchor_id}>{anchor.title}</option>
            ))}
          </select>
        </label>
        <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
          关键字
          <input
            value={keyword}
            onChange={(event) => { setKeyword(event.target.value) }}
            placeholder="搜索素材内容或标签…"
            title="对素材正文与标签做包含匹配"
            className="h-8 w-36 rounded-md border border-border bg-background px-2 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            aria-label="按关键字筛选素材"
          />
        </label>
      </div>

      {/* 素材是唯一的浏览对象：它来自材料化（整章抽取 + 多维度标签）。 */}
      <p className="mt-2 text-[11px] leading-5 text-muted-foreground" data-testid="corpus-browse-provenance">
        素材来自材料化：按章抽取、并在语言/叙事/情绪/场景/视角/技法等维度上打过标签的语料条目。
      </p>

      {materialFilter && (
        <div className="mt-2 flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground" data-testid="material-drilldown-filter">
          <span>
            来自覆盖度地图：{taxonomyLabel(COVERAGE_FACET_LABELS, materialFilter.facetKey)} · {facetValueLabel(materialFilter.facetKey, materialFilter.value)}
          </span>
          <button
            type="button"
            onClick={() => { onClearDrilldown(); setMaterialFilter(null) }}
            data-testid="material-drilldown-clear"
            className="rounded border border-border px-1.5 py-0.5 text-[11px] text-foreground hover:bg-muted"
          >
            清除筛选
          </button>
        </div>
      )}

      {error && (
        <div className="mt-3 flex items-start gap-2 border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs text-destructive" role="alert">
          <span className="min-w-0 break-words">{error}</span>
        </div>
      )}

      <div className="mt-3 min-h-0 flex-1 space-y-1.5 overflow-y-auto pr-1">
        {result == null && loading && <p className="text-xs text-muted-foreground">加载中…</p>}
        {result != null && result.items.length === 0 && (
          <p className="text-xs text-muted-foreground" data-testid="corpus-browse-empty">
            {hasBrowseFilter ? '当前筛选没有匹配的素材，可清除筛选后重试。' : '这本参考书还没有素材。先在「制作」完成材料化。'}
          </p>
        )}
        {materials?.items.map((item) => (
          <MaterialCard
            key={item.material_id}
            novelId={novelId}
            material={item}
            isExpanded={expandedId === item.material_id}
            onToggle={() => { setExpandedId(expandedId === item.material_id ? null : item.material_id) }}
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
              onClick={() => { setMaterialPage(Math.max(1, materialPage - 1)) }}
              className="inline-flex h-7 items-center gap-1 rounded-md border border-border px-2 hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
            >
              <ChevronLeft className="h-3 w-3" aria-hidden="true" />
              上一页
            </button>
            <button
              type="button"
              disabled={!result.has_more || loading}
              onClick={() => { setMaterialPage(materialPage + 1) }}
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

function MaterialCard({ novelId, material, isExpanded, onToggle }: {
  novelId: number
  material: reference.MaterialSummary
  isExpanded: boolean
  onToggle: () => void
}) {
  const app = useApp()
  const [detail, setDetail] = useState<reference.MaterialDetail | null>(null)
  const [detailError, setDetailError] = useState(false)

  // 展开时才取明细：列表只展示有界预览，完整素材正文不进入可见 DOM。
  useEffect(() => {
    if (!isExpanded || detail || detailError) return
    let cancelled = false
    void (async () => {
      try {
        const result = await app.GetReferenceMaterialDetail({ novel_id: novelId, material_id: material.material_id })
        if (!cancelled) setDetail(result ?? null)
      } catch {
        if (!cancelled) setDetailError(true)
      }
    })()
    return () => { cancelled = true }
  }, [app, novelId, isExpanded, detail, detailError, material.material_id])

  const tags: [string, string][] = [
    ['叙事功能', material.function_tag],
    ['情绪', material.emotion_tag],
    ['场景', material.scene_tag],
    ['视角', material.pov_tag],
    ['技法', material.technique_tag],
  ].filter((entry): entry is [string, string] => Boolean(entry[1]))

  return (
    <div className="rounded-md border border-border bg-background" data-testid="material-card">
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={isExpanded}
        className="flex w-full items-start justify-between gap-2 px-3 py-2 text-left hover:bg-muted/40"
      >
        <span className="min-w-0 flex-1">
          <span className="block truncate text-xs font-medium text-foreground">{material.text_preview}</span>
          <span className="mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[11px] text-muted-foreground">
            <span className="rounded border border-border px-1">{taxonomyLabel(MATERIAL_TYPE_LABELS, material.material_type)}</span>
            {tags.map(([label, value]) => (
              <span key={label}>{label} {taxonomyLabel(FEATURE_VALUE_LABELS, value)}</span>
            ))}
          </span>
        </span>
        <span className="shrink-0 rounded-full border border-border px-1.5 py-0.5 text-[11px] text-muted-foreground">
          {material.user_verified ? '已复核' : '未复核'}
        </span>
      </button>
      {isExpanded && (
        <dl className="space-y-1.5 border-t border-border px-3 py-2.5 text-[11px] text-muted-foreground">
          <div>
            <dt className="font-medium text-foreground">素材正文（有界预览）</dt>
            <dd className="mt-0.5 whitespace-pre-wrap break-words">
              {material.text_preview}{material.text_truncated ? '…' : ''}
            </dd>
          </div>
          <div className="flex flex-wrap gap-3">
            <span>功能置信 {(material.function_confidence * 100).toFixed(0)}%</span>
            <span>情绪置信 {(material.emotion_confidence * 100).toFixed(0)}%</span>
            <span>视角置信 {(material.pov_confidence * 100).toFixed(0)}%</span>
          </div>
          {detail ? (
            <>
              <div>
                <dt className="font-medium text-foreground">来源</dt>
                <dd className="mt-0.5">
                  {detail.source.title}
                  {detail.segments[0] ? ` · 第 ${detail.segments[0].chapter_index} 章 ${detail.segments[0].chapter_title}` : ''}
                </dd>
              </div>
              <div>
                <dt className="font-medium text-foreground" title="材料化抽取的素材按章挂靠，来源片段即素材所属的章节">来源片段（有界预览）</dt>
                <dd className="mt-0.5 whitespace-pre-wrap break-words rounded border border-border bg-muted/20 px-2 py-1.5">
                  {detail.segments[0]?.text_preview || '（无片段预览）'}
                </dd>
              </div>
            </>
          ) : (
            <div>{detailError ? '来源信息不可用。' : '正在加载来源…'}</div>
          )}
          <div className="break-all">素材 ID：{material.material_id}</div>
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
