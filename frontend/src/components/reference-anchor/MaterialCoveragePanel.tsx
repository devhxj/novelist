import { BarChart3, RefreshCcw, X } from 'lucide-react'
import type { reference } from '@/lib/novelist/types'

type FacetKey = 'material_type' | 'function_tag' | 'emotion_tag' | 'scene_tag' | 'pov_tag' | 'technique_tag'

type Props = {
  coverage: reference.MaterialCoverage | null
  loading: boolean
  error: string | null
  selectedValues: Partial<Record<FacetKey, string[]>>
  onToggleValue: (facet: FacetKey, value: string) => void
  onClearFilters: () => void
  onRefresh: () => void
}

const FACETS: Array<{ key: FacetKey; label: string }> = [
  { key: 'material_type', label: '材料类型' },
  { key: 'function_tag', label: '叙事功能' },
  { key: 'emotion_tag', label: '情绪张力' },
  { key: 'scene_tag', label: '场景节点' },
  { key: 'pov_tag', label: '叙事视角' },
  { key: 'technique_tag', label: '表达技法' },
]

const numberFormatter = new Intl.NumberFormat('zh-CN')

function formatCount(value: number): string {
  return numberFormatter.format(Math.max(0, value))
}

function formatValue(value: string): string {
  return value.replaceAll('_', ' ')
}

export default function MaterialCoveragePanel({
  coverage,
  loading,
  error,
  selectedValues,
  onToggleValue,
  onClearFilters,
  onRefresh,
}: Props) {
  const activeFilterCount = Object.values(selectedValues).reduce((total, values) => total + (values?.length ?? 0), 0)
  const facetsByKey = new Map((coverage?.facets ?? []).map((facet) => [facet.key, facet]))

  return (
    <section data-testid="reference-material-coverage" className="border-y border-border py-3" aria-busy={loading}>
      <header className="flex flex-wrap items-start justify-between gap-2">
        <div className="flex min-w-0 items-start gap-2">
          <BarChart3 className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          <div>
            <h3 className="text-xs font-semibold text-foreground">语料覆盖</h3>
            <p className="mt-0.5 text-[11px] text-muted-foreground">
              {coverage ? `${formatCount(coverage.material_count)} 条材料 · ${formatCount(coverage.source_count)} 个来源` : '正在汇总当前可用语料'}
            </p>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-1">
          {activeFilterCount > 0 && (
            <button
              type="button"
              onClick={onClearFilters}
              className="inline-flex h-7 items-center gap-1 rounded-md px-1.5 text-[11px] text-muted-foreground hover:bg-secondary hover:text-foreground"
              aria-label="清除维度筛选"
              title="清除维度筛选"
            >
              <X className="h-3.5 w-3.5" aria-hidden="true" />{activeFilterCount}
            </button>
          )}
          <button
            type="button"
            onClick={onRefresh}
            disabled={loading}
            className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground hover:bg-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
            aria-label="刷新语料覆盖"
            title="刷新语料覆盖"
          >
            <RefreshCcw className={`h-3.5 w-3.5 ${loading ? 'animate-spin' : ''}`} aria-hidden="true" />
          </button>
        </div>
      </header>

      {error ? (
        <div role="alert" className="mt-3 flex items-center justify-between gap-2 border border-destructive/30 bg-destructive/5 px-3 py-2 text-xs text-destructive">
          <span>{error}</span>
          <button
            type="button"
            onClick={onRefresh}
            className="inline-flex h-7 w-7 shrink-0 items-center justify-center rounded-md hover:bg-destructive/10"
            aria-label="重试加载语料覆盖"
            title="重试加载语料覆盖"
          >
            <RefreshCcw className="h-3.5 w-3.5" aria-hidden="true" />
          </button>
        </div>
      ) : (
        <div className="mt-3 grid grid-cols-1 gap-2 sm:grid-cols-2 2xl:grid-cols-3">
          {FACETS.map((meta) => {
            const facet = facetsByKey.get(meta.key)
            const selected = new Set(selectedValues[meta.key] ?? [])
            return (
              <section key={meta.key} data-testid={`reference-material-facet-${meta.key}`} className="min-w-0 border border-border bg-background/40 p-2.5">
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <h4 className="text-xs font-medium text-foreground">{meta.label}</h4>
                  </div>
                  <span className="shrink-0 text-[11px] text-muted-foreground">{facet?.distinct_value_count ?? 0} 类</span>
                </div>
                <div className="mt-2 flex flex-wrap gap-1" aria-label={`${meta.label}标签`}>
                  {loading ? (
                    Array.from({ length: 4 }, (_, index) => <span key={index} className="h-6 w-16 animate-pulse bg-muted" />)
                  ) : facet && facet.values.length > 0 ? (
                    facet.values.map((item) => {
                      const isSelected = selected.has(item.value)
                      return (
                        <button
                          key={item.value}
                          type="button"
                          onClick={() => onToggleValue(meta.key, item.value)}
                          aria-pressed={isSelected}
                          title={`${meta.label}: ${item.value}，${formatCount(item.material_count)} 条材料`}
                          className={`inline-flex max-w-full items-center gap-1 border px-1.5 py-1 text-[11px] leading-none transition-colors ${isSelected
                            ? 'border-primary bg-primary/10 text-foreground'
                            : 'border-border bg-card text-muted-foreground hover:bg-secondary hover:text-foreground'}`}
                        >
                          <span className="max-w-32 truncate">{formatValue(item.value)}</span>
                          <span className="shrink-0 tabular-nums text-muted-foreground">{formatCount(item.material_count)}</span>
                        </button>
                      )
                    })
                  ) : (
                    <span className="text-[11px] text-muted-foreground">暂无标签</span>
                  )}
                </div>
              </section>
            )
          })}
        </div>
      )}
    </section>
  )
}
