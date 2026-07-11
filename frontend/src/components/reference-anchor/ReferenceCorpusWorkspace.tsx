import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Archive,
  ChevronLeft,
  ChevronRight,
  FileText,
  Loader2,
  RefreshCcw,
  Search,
  SlidersHorizontal,
} from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reference } from '@/lib/novelist/types'
import MaterialCoveragePanel from './MaterialCoveragePanel'

type Props = {
  novelId: number
  refreshKey: number
}

type ArchiveFilter = 'active' | 'archived'
type FacetKey = 'material_type' | 'function_tag' | 'emotion_tag' | 'scene_tag' | 'pov_tag' | 'technique_tag'
type FacetSelections = Partial<Record<FacetKey, string[]>>
type AdvancedFilters = {
  narrativeDuties: string
  emotionTransitions: string
  proseDuties: string
}

type MaterialResults = {
  items: reference.MaterialSummary[]
  page: number
  total: number
  totalPages: number
}

const EMPTY_ADVANCED_FILTERS: AdvancedFilters = {
  narrativeDuties: '',
  emotionTransitions: '',
  proseDuties: '',
}

const EMPTY_RESULTS: MaterialResults = {
  items: [],
  page: 1,
  total: 0,
  totalPages: 1,
}

const numberFormatter = new Intl.NumberFormat('zh-CN')

function lines(value: string): string[] {
  return value
    .split(/[\n；;，,]/)
    .map((item) => item.trim())
    .filter(Boolean)
}

function formatCount(value: number): string {
  return numberFormatter.format(Math.max(0, value))
}

function tagLabel(value: string): string {
  return value.replaceAll('_', ' ')
}

function confidenceLabel(value: number): string {
  return Number.isFinite(value) ? value.toFixed(2) : '0.00'
}

function countFacetSelections(selections: FacetSelections): number {
  return Object.values(selections).reduce((total, values) => total + (values?.length ?? 0), 0)
}

function hasAdvancedFilterValues(filters: AdvancedFilters): boolean {
  return lines(filters.narrativeDuties).length > 0 ||
    lines(filters.emotionTransitions).length > 0 ||
    lines(filters.proseDuties).length > 0
}

export default function ReferenceCorpusWorkspace({ novelId, refreshKey }: Props) {
  const app = useApp()
  const [archiveFilter, setArchiveFilter] = useState<ArchiveFilter>('active')
  const [query, setQuery] = useState('')
  const [facetSelections, setFacetSelections] = useState<FacetSelections>({})
  const [advancedFilters, setAdvancedFilters] = useState<AdvancedFilters>(EMPTY_ADVANCED_FILTERS)
  const [coverage, setCoverage] = useState<reference.MaterialCoverage | null>(null)
  const [coverageLoading, setCoverageLoading] = useState(false)
  const [coverageError, setCoverageError] = useState<string | null>(null)
  const [results, setResults] = useState<MaterialResults>(EMPTY_RESULTS)
  const [resultsLoading, setResultsLoading] = useState(false)
  const [resultsError, setResultsError] = useState<string | null>(null)
  const [hasSearched, setHasSearched] = useState(false)
  const coverageRequestRef = useRef(0)
  const searchRequestRef = useRef(0)
  const materialSearchActiveRef = useRef(false)
  const workspaceStateRef = useRef({
    archiveFilter,
    query,
    facetSelections,
    advancedFilters,
  })

  useEffect(() => {
    workspaceStateRef.current = {
      archiveFilter,
      query,
      facetSelections,
      advancedFilters,
    }
  }, [advancedFilters, archiveFilter, facetSelections, query])

  const loadCoverage = useCallback(async (nextArchiveFilter: ArchiveFilter) => {
    const requestId = coverageRequestRef.current + 1
    coverageRequestRef.current = requestId
    if (!novelId) {
      setCoverage(null)
      setCoverageError(null)
      setCoverageLoading(false)
      return
    }

    setCoverageLoading(true)
    setCoverageError(null)
    try {
      const nextCoverage = await app.GetReferenceMaterialCoverage({
        novel_id: novelId,
        archive_filter: nextArchiveFilter,
      })
      if (coverageRequestRef.current === requestId) setCoverage(nextCoverage)
    } catch {
      if (coverageRequestRef.current === requestId) {
        setCoverageError('语料覆盖加载失败。')
        setCoverage(null)
      }
    } finally {
      if (coverageRequestRef.current === requestId) setCoverageLoading(false)
    }
  }, [app, novelId])

  const loadMaterials = useCallback(async ({
    nextArchiveFilter,
    nextQuery,
    nextFacetSelections,
    nextAdvancedFilters,
    page,
  }: {
    nextArchiveFilter: ArchiveFilter
    nextQuery: string
    nextFacetSelections: FacetSelections
    nextAdvancedFilters: AdvancedFilters
    page: number
  }) => {
    const requestId = searchRequestRef.current + 1
    searchRequestRef.current = requestId
    if (!novelId) {
      setResults(EMPTY_RESULTS)
      setResultsError(null)
      setResultsLoading(false)
      return
    }

    setResultsLoading(true)
    setResultsError(null)
    try {
      const result = await app.SearchReferenceMaterials({
        novel_id: novelId,
        anchor_ids: [],
        query: nextQuery.trim(),
        material_types: nextFacetSelections.material_type ?? [],
        function_tags: nextFacetSelections.function_tag ?? [],
        emotion_tags: nextFacetSelections.emotion_tag ?? [],
        scene_tags: nextFacetSelections.scene_tag ?? [],
        pov_tags: nextFacetSelections.pov_tag ?? [],
        technique_tags: nextFacetSelections.technique_tag ?? [],
        narrative_duties: lines(nextAdvancedFilters.narrativeDuties),
        emotion_transitions: lines(nextAdvancedFilters.emotionTransitions),
        prose_duties: lines(nextAdvancedFilters.proseDuties),
        archive_filter: nextArchiveFilter,
        ready_only: true,
        page,
        size: 12,
      })
      if (searchRequestRef.current === requestId) {
        setResults({
          items: result.items ?? [],
          page: Math.max(1, result.page),
          total: Math.max(0, result.total),
          totalPages: Math.max(1, result.total_pages),
        })
      }
    } catch {
      if (searchRequestRef.current === requestId) {
        setResults(EMPTY_RESULTS)
        setResultsError('材料检索失败。')
      }
    } finally {
      if (searchRequestRef.current === requestId) setResultsLoading(false)
    }
  }, [app, novelId])

  const activateMaterialSearch = useCallback(() => {
    materialSearchActiveRef.current = true
    setHasSearched(true)
  }, [])

  const resetMaterialSearch = useCallback(() => {
    searchRequestRef.current += 1
    materialSearchActiveRef.current = false
    setHasSearched(false)
    setResults(EMPTY_RESULTS)
    setResultsLoading(false)
    setResultsError(null)
  }, [])

  useEffect(() => {
    const next = workspaceStateRef.current
    const timer = window.setTimeout(() => {
      resetMaterialSearch()
      void loadCoverage(next.archiveFilter)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [loadCoverage, novelId, refreshKey, resetMaterialSearch])

  const activeFilterCount = useMemo(
    () => countFacetSelections(facetSelections),
    [facetSelections],
  )
  const hasMaterialSearchCriteria = useMemo(
    () => Boolean(query.trim() || activeFilterCount > 0 || hasAdvancedFilterValues(advancedFilters)),
    [activeFilterCount, advancedFilters, query],
  )

  const submitSearch = () => {
    if (!hasMaterialSearchCriteria) return
    activateMaterialSearch()
    void loadMaterials({
      nextArchiveFilter: archiveFilter,
      nextQuery: query,
      nextFacetSelections: facetSelections,
      nextAdvancedFilters: advancedFilters,
      page: 1,
    })
  }

  const toggleFacetValue = (facet: FacetKey, value: string) => {
    const current = facetSelections[facet] ?? []
    const nextValues = current.includes(value)
      ? current.filter((item) => item !== value)
      : [...current, value]
    const nextFacetSelections = { ...facetSelections, [facet]: nextValues }
    setFacetSelections(nextFacetSelections)
    if (!query.trim() && !hasAdvancedFilterValues(advancedFilters) && countFacetSelections(nextFacetSelections) === 0) {
      resetMaterialSearch()
      return
    }

    activateMaterialSearch()
    void loadMaterials({
      nextArchiveFilter: archiveFilter,
      nextQuery: query,
      nextFacetSelections,
      nextAdvancedFilters: advancedFilters,
      page: 1,
    })
  }

  const clearFacetFilters = () => {
    setFacetSelections({})
    const hasTextFilters = Boolean(
      query.trim() ||
      hasAdvancedFilterValues(advancedFilters),
    )
    if (!hasTextFilters) {
      resetMaterialSearch()
      return
    }

    activateMaterialSearch()
    void loadMaterials({
      nextArchiveFilter: archiveFilter,
      nextQuery: query,
      nextFacetSelections: {},
      nextAdvancedFilters: advancedFilters,
      page: 1,
    })
  }

  const changeArchiveFilter = (nextArchiveFilter: ArchiveFilter) => {
    if (nextArchiveFilter === archiveFilter) return
    setArchiveFilter(nextArchiveFilter)
    void loadCoverage(nextArchiveFilter)
    if (materialSearchActiveRef.current) {
      void loadMaterials({
        nextArchiveFilter,
        nextQuery: query,
        nextFacetSelections: facetSelections,
        nextAdvancedFilters: advancedFilters,
        page: 1,
      })
    }
  }

  const changePage = (page: number) => {
    if (page < 1 || page > results.totalPages || page === results.page) return
    void loadMaterials({
      nextArchiveFilter: archiveFilter,
      nextQuery: query,
      nextFacetSelections: facetSelections,
      nextAdvancedFilters: advancedFilters,
      page,
    })
  }

  return (
    <main data-testid="reference-corpus-workspace" className="min-w-0 flex-1 overflow-y-auto bg-background" aria-busy={coverageLoading || (hasSearched && resultsLoading)}>
      <div className="mx-auto flex min-h-full w-full max-w-6xl flex-col px-4 py-5 sm:px-6 lg:px-8">
        <header className="flex flex-wrap items-start justify-between gap-3 border-b border-border pb-4">
          <div>
            <h1 className="text-base font-semibold text-foreground">参考语料工作台</h1>
          </div>
          <div className="inline-flex rounded-md border border-border bg-muted/35 p-0.5" aria-label="材料状态">
            <button
              type="button"
              onClick={() => changeArchiveFilter('active')}
              aria-pressed={archiveFilter === 'active'}
              className={`rounded px-2.5 py-1.5 text-xs font-medium transition-colors ${archiveFilter === 'active' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}`}
            >
              当前材料
            </button>
            <button
              type="button"
              onClick={() => changeArchiveFilter('archived')}
              aria-pressed={archiveFilter === 'archived'}
              className={`rounded px-2.5 py-1.5 text-xs font-medium transition-colors ${archiveFilter === 'archived' ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}`}
            >
              已归档
            </button>
          </div>
        </header>

        <div className="flex min-h-0 flex-1 flex-col gap-4 py-4">
          <MaterialCoveragePanel
            coverage={coverage}
            loading={coverageLoading}
            error={coverageError}
            selectedValues={facetSelections}
            onToggleValue={toggleFacetValue}
            onClearFilters={clearFacetFilters}
            onRefresh={() => { void loadCoverage(archiveFilter) }}
          />

          <section aria-labelledby="reference-material-results-heading" className="min-w-0">
            <div className="flex flex-wrap items-end justify-between gap-3">
              <div>
                <h2 id="reference-material-results-heading" className="text-sm font-semibold text-foreground">材料检索</h2>
                <p className="mt-1 text-xs text-muted-foreground">
                  {!hasSearched
                    ? '选择维度标签或输入关键词后查看材料'
                    : resultsLoading
                      ? '正在更新结果'
                      : `匹配 ${formatCount(results.total)} 条材料${activeFilterCount > 0 ? ` · 已选 ${activeFilterCount} 个标签` : ''}`}
                </p>
              </div>
              <button
                type="button"
                onClick={() => {
                  if (!materialSearchActiveRef.current) return
                  void loadMaterials({
                    nextArchiveFilter: archiveFilter,
                    nextQuery: query,
                    nextFacetSelections: facetSelections,
                    nextAdvancedFilters: advancedFilters,
                    page: results.page,
                  })
                }}
                disabled={!hasSearched || resultsLoading}
                className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-border text-muted-foreground hover:bg-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
                aria-label="刷新材料检索"
                title="刷新材料检索"
              >
                <RefreshCcw className={`h-3.5 w-3.5 ${resultsLoading ? 'animate-spin' : ''}`} aria-hidden="true" />
              </button>
            </div>

            <form
              className="mt-3 flex gap-2"
              onSubmit={(event) => {
                event.preventDefault()
                submitSearch()
              }}
            >
              <label className="relative min-w-0 flex-1">
                <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
                <span className="sr-only">搜索材料</span>
                <input
                  value={query}
                  onChange={(event) => setQuery(event.target.value)}
                  className="h-9 w-full rounded-md border border-border bg-background pl-8 pr-2 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  placeholder="搜索材料文本"
                  aria-label="搜索材料"
                />
              </label>
              <button
                type="submit"
                disabled={!hasMaterialSearchCriteria || resultsLoading}
                className="inline-flex h-9 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {resultsLoading ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <Search className="h-3.5 w-3.5" aria-hidden="true" />}
                检索
              </button>
            </form>

            <details className="mt-2 border border-border bg-muted/20">
              <summary className="flex cursor-pointer list-none items-center gap-1.5 px-3 py-2 text-xs font-medium text-foreground">
                <SlidersHorizontal className="h-3.5 w-3.5 text-muted-foreground" aria-hidden="true" />精细条件
              </summary>
              <div className="grid grid-cols-1 gap-2 border-t border-border p-3 sm:grid-cols-3">
                <label className="block">
                  <span className="mb-1 block text-[11px] font-medium text-muted-foreground">叙事职责</span>
                  <input value={advancedFilters.narrativeDuties} onChange={(event) => setAdvancedFilters((current) => ({ ...current, narrativeDuties: event.target.value }))} className="h-8 w-full rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" placeholder="subtext；causality" aria-label="叙事职责" />
                </label>
                <label className="block">
                  <span className="mb-1 block text-[11px] font-medium text-muted-foreground">情绪转场</span>
                  <input value={advancedFilters.emotionTransitions} onChange={(event) => setAdvancedFilters((current) => ({ ...current, emotionTransitions: event.target.value }))} className="h-8 w-full rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" placeholder="calm->threat" aria-label="情绪转场" />
                </label>
                <label className="block">
                  <span className="mb-1 block text-[11px] font-medium text-muted-foreground">文体职责</span>
                  <input value={advancedFilters.proseDuties} onChange={(event) => setAdvancedFilters((current) => ({ ...current, proseDuties: event.target.value }))} className="h-8 w-full rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring" placeholder="source_backed_detail" aria-label="文体职责" />
                </label>
              </div>
              <div className="flex justify-end border-t border-border px-3 py-2">
                <button type="button" onClick={submitSearch} disabled={resultsLoading} className="inline-flex h-8 items-center gap-1.5 rounded-md bg-secondary px-2.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50">
                  <SlidersHorizontal className="h-3.5 w-3.5" aria-hidden="true" />应用条件
                </button>
              </div>
            </details>

            {!hasSearched ? (
              <div className="mt-3 flex min-h-44 flex-col items-center justify-center border border-dashed border-border px-5 text-center" role="status">
                <Search className="h-6 w-6 text-muted-foreground/55" aria-hidden="true" />
                <p className="mt-2 text-xs font-medium text-foreground">先缩小材料范围</p>
                <p className="mt-1 text-xs leading-5 text-muted-foreground">从上方六个维度选择标签，或输入关键词开始检索。</p>
              </div>
            ) : resultsError ? (
              <div role="alert" className="mt-3 flex items-center justify-between gap-3 border border-destructive/30 bg-destructive/5 px-3 py-2 text-xs text-destructive">
                <span>{resultsError}</span>
                <button type="button" onClick={submitSearch} className="inline-flex h-7 w-7 items-center justify-center rounded-md hover:bg-destructive/10" aria-label="重试材料检索" title="重试材料检索">
                  <RefreshCcw className="h-3.5 w-3.5" aria-hidden="true" />
                </button>
              </div>
            ) : resultsLoading && results.items.length === 0 ? (
              <div className="mt-3 space-y-2" aria-label="正在加载材料">
                {[0, 1, 2, 3].map((index) => <div key={index} className="h-20 animate-pulse border border-border bg-muted/45" />)}
              </div>
            ) : results.items.length === 0 ? (
              <div className="mt-3 flex min-h-44 flex-col items-center justify-center border border-dashed border-border px-5 text-center" role="status">
                <Archive className="h-6 w-6 text-muted-foreground/55" aria-hidden="true" />
                <p className="mt-2 text-xs font-medium text-foreground">没有匹配材料</p>
                <p className="mt-1 text-xs leading-5 text-muted-foreground">调整维度、搜索词或材料状态后再试。</p>
              </div>
            ) : (
              <ol className="mt-3 divide-y divide-border border-y border-border" aria-label="材料检索结果">
                {results.items.map((material) => (
                  <li key={material.material_id} className="px-1 py-3 sm:px-2">
                    <div className="flex gap-2.5">
                      <FileText className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
                      <div className="min-w-0 flex-1">
                        <div className="flex flex-wrap items-center justify-between gap-2">
                          <p className="min-w-0 truncate text-xs font-medium text-foreground">{material.material_id}</p>
                          <span className="shrink-0 text-[11px] text-muted-foreground">{material.user_verified ? '已校正' : '待校正'}</span>
                        </div>
                        <div className="mt-1 flex flex-wrap gap-1" aria-label={`${material.material_id} 的维度标签`}>
                          {[material.material_type, material.function_tag, material.emotion_tag, material.scene_tag, material.pov_tag, material.technique_tag]
                            .filter(Boolean)
                            .map((tag, index) => <span key={`${index}:${tag}`} className="border border-border bg-muted/35 px-1.5 py-0.5 text-[11px] text-muted-foreground">{tagLabel(tag)}</span>)}
                        </div>
                        <p className="mt-2 text-xs leading-5 text-foreground">{material.text_preview || '无可显示预览'}</p>
                        <details className="mt-2 text-[11px] text-muted-foreground">
                          <summary className="cursor-pointer select-none hover:text-foreground">来源与置信度</summary>
                          <div className="mt-1 grid grid-cols-1 gap-1 sm:grid-cols-2">
                            <span className="break-all">来源片段 {material.source_segment_id}</span>
                            <span>功能 {confidenceLabel(material.function_confidence)} · 情绪 {confidenceLabel(material.emotion_confidence)} · POV {confidenceLabel(material.pov_confidence)}</span>
                          </div>
                        </details>
                      </div>
                    </div>
                  </li>
                ))}
              </ol>
            )}

            {results.totalPages > 1 && (
              <nav className="mt-3 flex items-center justify-between gap-3" aria-label="材料结果分页">
                <span className="text-[11px] text-muted-foreground">第 {results.page} / {results.totalPages} 页</span>
                <span className="flex items-center gap-1">
                  <button type="button" onClick={() => changePage(results.page - 1)} disabled={resultsLoading || results.page <= 1} className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-border text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50" aria-label="上一页材料" title="上一页材料">
                    <ChevronLeft className="h-4 w-4" aria-hidden="true" />
                  </button>
                  <button type="button" onClick={() => changePage(results.page + 1)} disabled={resultsLoading || results.page >= results.totalPages} className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-border text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50" aria-label="下一页材料" title="下一页材料">
                    <ChevronRight className="h-4 w-4" aria-hidden="true" />
                  </button>
                </span>
              </nav>
            )}
          </section>
        </div>
      </div>
    </main>
  )
}
