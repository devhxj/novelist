import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { FileSearch, Layers3, Loader2, RefreshCcw, Search } from 'lucide-react'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { useApp } from '@/hooks/useApp'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics, reference } from '@/lib/novelist/types'
import { inputClass, statusTone } from './referenceAnchorStyles'

type Props = {
  novelId: number
  anchors: reference.Anchor[]
}

type AnalysisErrorState = {
  title: string
  message: string
  diagnostic: diagnostics.CopyableDiagnostic | null
}

type ObservationFilters = {
  featureFamily: string
  featureKey: string
  nodeType: string
  reviewState: string
  validityState: string
  minConfidence: string
}

type SpecimenFilters = {
  techniqueFamily: string
  reviewState: string
  validityState: string
  minConfidence: string
}

type AnalysisPageState<T> = {
  items: T[]
  total: number
  page: number
  size: number
  nextCursor: string | null
  hasMore: boolean
}

const OBSERVATION_PAGE_SIZE = 8
const SPECIMEN_PAGE_SIZE = 5

const EMPTY_OBSERVATION_FILTERS: ObservationFilters = {
  featureFamily: '',
  featureKey: '',
  nodeType: '',
  reviewState: '',
  validityState: 'active',
  minConfidence: '',
}

const EMPTY_SPECIMEN_FILTERS: SpecimenFilters = {
  techniqueFamily: '',
  reviewState: '',
  validityState: 'active',
  minConfidence: '',
}

const EMPTY_OBSERVATION_PAGE: AnalysisPageState<reference.CorpusFeatureObservation> = {
  items: [],
  total: 0,
  page: 1,
  size: OBSERVATION_PAGE_SIZE,
  nextCursor: null,
  hasMore: false,
}

const EMPTY_SPECIMEN_PAGE: AnalysisPageState<reference.CorpusTechniqueSpecimen> = {
  items: [],
  total: 0,
  page: 1,
  size: SPECIMEN_PAGE_SIZE,
  nextCursor: null,
  hasMore: false,
}

const REVIEW_STATE_OPTIONS = [
  { value: '', label: '全部' },
  { value: 'unverified', label: '未复核' },
  { value: 'low_confidence', label: '低置信' },
  { value: 'confirmed', label: '已确认' },
  { value: 'rejected', label: '已拒绝' },
  { value: 'conflicted', label: '有冲突' },
] as const

const VALIDITY_STATE_OPTIONS = [
  { value: 'active', label: '有效' },
  { value: '', label: '全部' },
  { value: 'stale', label: '已过期' },
  { value: 'invalid', label: '无效' },
] as const

export function CorpusAnalysisLibraryTab({ novelId, anchors }: Props) {
  const app = useApp()
  const requestRef = useRef({ observations: 0, specimens: 0 })
  const readyAnchors = useMemo(() => {
    const available = anchors.filter(anchor => !isFailedAnchorStatus(anchor.status))
    return available.length > 0 ? available : anchors
  }, [anchors])
  const [selectedAnchorId, setSelectedAnchorId] = useState<number | null>(null)
  const [nodeId, setNodeId] = useState('')
  const [observationFilters, setObservationFilters] = useState<ObservationFilters>(EMPTY_OBSERVATION_FILTERS)
  const [specimenFilters, setSpecimenFilters] = useState<SpecimenFilters>(EMPTY_SPECIMEN_FILTERS)
  const [observations, setObservations] = useState<AnalysisPageState<reference.CorpusFeatureObservation>>(EMPTY_OBSERVATION_PAGE)
  const [specimens, setSpecimens] = useState<AnalysisPageState<reference.CorpusTechniqueSpecimen>>(EMPTY_SPECIMEN_PAGE)
  const [loading, setLoading] = useState({ observations: false, specimens: false })
  const [error, setError] = useState<AnalysisErrorState | null>(null)

  const selectedAnchor = useMemo(() => {
    return readyAnchors.find(anchor => anchor.anchor_id === selectedAnchorId) ?? readyAnchors[0] ?? null
  }, [readyAnchors, selectedAnchorId])

  const loadObservations = useCallback(async (options?: { append?: boolean; cursor?: string | null }) => {
    if (!selectedAnchor) return
    const append = options?.append ?? false
    const cursor = options?.cursor ?? null
    const requestId = requestRef.current.observations + 1
    requestRef.current = { ...requestRef.current, observations: requestId }
    setLoading(current => ({ ...current, observations: true }))
    setError(null)

    try {
      const page = await app.ListReferenceCorpusFeatureObservations({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
        node_id: nodeId.trim() || null,
        page_request: {
          cursor,
          page_size: OBSERVATION_PAGE_SIZE,
          sort_by: 'feature_family',
          sort_dir: 'asc',
          filters: observationFilterPayload(observationFilters),
        },
      })
      if (requestRef.current.observations !== requestId) return
      const items = page.items ?? []
      setObservations(current => ({
        items: append ? [...current.items, ...items] : items,
        total: page.total_estimate ?? page.total ?? items.length,
        page: page.page,
        size: page.size,
        nextCursor: page.next_cursor ?? null,
        hasMore: page.has_more ?? false,
      }))
    } catch (caught) {
      if (requestRef.current.observations !== requestId) return
      setObservations(EMPTY_OBSERVATION_PAGE)
      setError({
        title: '观察结果加载失败',
        message: diagnosticMessage(caught, '观察结果加载失败'),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage: '观察结果加载失败',
          operation: '加载素材库观察结果',
          bridgeMethod: 'ListReferenceCorpusFeatureObservations',
          detail: {
            novel_id: novelId,
            anchor_id: selectedAnchor.anchor_id,
            node_id: nodeId.trim() || null,
          },
        }),
      })
    } finally {
      if (requestRef.current.observations === requestId) {
        setLoading(current => ({ ...current, observations: false }))
      }
    }
  }, [app, nodeId, novelId, observationFilters, selectedAnchor])

  const loadSpecimens = useCallback(async (options?: { append?: boolean; cursor?: string | null }) => {
    if (!selectedAnchor) return
    const append = options?.append ?? false
    const cursor = options?.cursor ?? null
    const requestId = requestRef.current.specimens + 1
    requestRef.current = { ...requestRef.current, specimens: requestId }
    setLoading(current => ({ ...current, specimens: true }))
    setError(null)

    try {
      const page = await app.ListReferenceCorpusTechniqueSpecimens({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
        source_node_id: nodeId.trim() || null,
        page_request: {
          cursor,
          page_size: SPECIMEN_PAGE_SIZE,
          sort_by: 'confidence',
          sort_dir: 'desc',
          filters: specimenFilterPayload(specimenFilters),
        },
      })
      if (requestRef.current.specimens !== requestId) return
      const items = page.items ?? []
      setSpecimens(current => ({
        items: append ? [...current.items, ...items] : items,
        total: page.total_estimate ?? page.total ?? items.length,
        page: page.page,
        size: page.size,
        nextCursor: page.next_cursor ?? null,
        hasMore: page.has_more ?? false,
      }))
    } catch (caught) {
      if (requestRef.current.specimens !== requestId) return
      setSpecimens(EMPTY_SPECIMEN_PAGE)
      setError({
        title: '技法标本加载失败',
        message: diagnosticMessage(caught, '技法标本加载失败'),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage: '技法标本加载失败',
          operation: '加载素材库技法标本',
          bridgeMethod: 'ListReferenceCorpusTechniqueSpecimens',
          detail: {
            novel_id: novelId,
            anchor_id: selectedAnchor.anchor_id,
            source_node_id: nodeId.trim() || null,
          },
        }),
      })
    } finally {
      if (requestRef.current.specimens === requestId) {
        setLoading(current => ({ ...current, specimens: false }))
      }
    }
  }, [app, nodeId, novelId, selectedAnchor, specimenFilters])

  const reloadAll = useCallback(() => {
    void loadObservations()
    void loadSpecimens()
  }, [loadObservations, loadSpecimens])

  useEffect(() => {
    if (!selectedAnchor) return
    const timer = window.setTimeout(() => {
      reloadAll()
    }, 200)
    return () => window.clearTimeout(timer)
  }, [reloadAll, selectedAnchor])

  if (anchors.length === 0) {
    return (
      <section data-testid="reference-corpus-analysis-library-tab" className="rounded-lg border border-border bg-card p-4">
        <EmptyState title="暂无语料来源" detail="先在素材来源中导入参考文本。" />
      </section>
    )
  }

  return (
    <section data-testid="reference-corpus-analysis-library-tab" className="space-y-4 rounded-lg border border-border bg-card p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 space-y-1">
          <div className="flex items-center gap-2">
            <FileSearch className="h-3.5 w-3.5 text-muted-foreground" />
            <h3 className="text-xs font-semibold text-foreground">分析结果</h3>
          </div>
          {selectedAnchor && (
            <p className="truncate text-[11px] text-muted-foreground">
              {anchorScopeLabel(selectedAnchor.owner_scope)} · {selectedAnchor.source_file_hash || `anchor:${selectedAnchor.anchor_id}`}
            </p>
          )}
        </div>
        <button
          type="button"
          onClick={reloadAll}
          disabled={!selectedAnchor || loading.observations || loading.specimens}
          className="inline-flex items-center gap-1.5 rounded bg-secondary px-3 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
        >
          {loading.observations || loading.specimens ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCcw className="h-3.5 w-3.5" />}
          刷新
        </button>
      </div>

      <div className="grid grid-cols-1 gap-3 lg:grid-cols-[minmax(220px,0.8fr)_minmax(0,1.2fr)_minmax(0,1.2fr)]">
        <aside className="space-y-3 rounded-md border border-border bg-background p-3">
          <Field label="语料来源">
            <select
              value={selectedAnchor?.anchor_id ?? ''}
              onChange={event => setSelectedAnchorId(Number(event.target.value))}
              className={inputClass}
              aria-label="分析结果语料来源"
            >
              {readyAnchors.map(anchor => (
                <option key={anchor.anchor_id} value={anchor.anchor_id}>
                  {anchor.title} · {anchorScopeLabel(anchor.owner_scope)} · {anchor.status}
                </option>
              ))}
            </select>
          </Field>
          {selectedAnchor && (
            <div className="space-y-1 rounded border border-border bg-card px-2 py-2 text-[11px]">
              <div className="flex items-center justify-between gap-2">
                <span className="font-medium text-foreground">{selectedAnchor.title}</span>
                <span className={statusTone(selectedAnchor.status)}>{selectedAnchor.status}</span>
              </div>
              <p className="break-all text-muted-foreground">#{selectedAnchor.anchor_id} · {selectedAnchor.author || '未知作者'}</p>
              <p className="break-all text-muted-foreground">{selectedAnchor.user_tags.join(' / ') || '未标记'}</p>
            </div>
          )}
          <Field label="节点 ID">
            <input
              value={nodeId}
              onChange={event => setNodeId(event.target.value)}
              className={inputClass}
              placeholder="留空查看整本处理结果"
              aria-label="分析结果节点 ID"
            />
          </Field>
          <div className="grid grid-cols-2 gap-2">
            <Field label="有效性">
              <select
                value={observationFilters.validityState}
                onChange={event => {
                  const nextValue = event.target.value
                  setObservationFilters(current => ({ ...current, validityState: nextValue }))
                  setSpecimenFilters(current => ({ ...current, validityState: nextValue }))
                }}
                className={inputClass}
                aria-label="分析结果有效性"
              >
                {VALIDITY_STATE_OPTIONS.map(option => (
                  <option key={option.value || 'all'} value={option.value}>{option.label}</option>
                ))}
              </select>
            </Field>
            <Field label="审核">
              <select
                value={observationFilters.reviewState}
                onChange={event => {
                  const nextValue = event.target.value
                  setObservationFilters(current => ({ ...current, reviewState: nextValue }))
                  setSpecimenFilters(current => ({ ...current, reviewState: nextValue }))
                }}
                className={inputClass}
                aria-label="分析结果审核状态"
              >
                {REVIEW_STATE_OPTIONS.map(option => (
                  <option key={option.value || 'all'} value={option.value}>{option.label}</option>
                ))}
              </select>
            </Field>
          </div>
          <Field label="最低置信">
            <input
              value={observationFilters.minConfidence}
              onChange={event => {
                const nextValue = event.target.value
                setObservationFilters(current => ({ ...current, minConfidence: nextValue }))
                setSpecimenFilters(current => ({ ...current, minConfidence: nextValue }))
              }}
              type="number"
              min="0"
              max="1"
              step="0.05"
              className={inputClass}
              placeholder="0.80"
              aria-label="分析结果最低置信"
            />
          </Field>
          <button
            type="button"
            onClick={() => {
              setNodeId('')
              setObservationFilters(EMPTY_OBSERVATION_FILTERS)
              setSpecimenFilters(EMPTY_SPECIMEN_FILTERS)
            }}
            className="inline-flex w-full items-center justify-center gap-1.5 rounded border border-border px-3 py-1.5 text-xs text-muted-foreground hover:bg-secondary hover:text-foreground"
          >
            清空筛选
          </button>
        </aside>

        <AnalysisPanel
          title="观察维度"
          count={observations.total}
          loading={loading.observations}
          onRefresh={() => {
            void loadObservations()
          }}
          onLoadMore={() => {
            void loadObservations({ append: true, cursor: observations.nextCursor })
          }}
          hasMore={observations.hasMore}
          filterControls={(
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
              <Field label="family">
                <input
                  value={observationFilters.featureFamily}
                  onChange={event => setObservationFilters(current => ({ ...current, featureFamily: event.target.value }))}
                  className={inputClass}
                  placeholder="emotion"
                  aria-label="观察 family"
                />
              </Field>
              <Field label="key">
                <input
                  value={observationFilters.featureKey}
                  onChange={event => setObservationFilters(current => ({ ...current, featureKey: event.target.value }))}
                  className={inputClass}
                  placeholder="emotion_state"
                  aria-label="观察 key"
                />
              </Field>
              <Field label="node">
                <select
                  value={observationFilters.nodeType}
                  onChange={event => setObservationFilters(current => ({ ...current, nodeType: event.target.value }))}
                  className={inputClass}
                  aria-label="观察节点类型"
                >
                  <option value="">全部</option>
                  <option value="sentence">sentence</option>
                  <option value="passage">passage</option>
                  <option value="chapter">chapter</option>
                </select>
              </Field>
            </div>
          )}
        >
          {observations.items.length > 0 ? (
            <div className="divide-y divide-border rounded-md border border-border">
              {observations.items.map(observation => (
                <ObservationCard key={observation.observation_id} observation={observation} />
              ))}
            </div>
          ) : (
            <EmptyState title="暂无观察结果" detail="当前筛选没有匹配的 observation。" />
          )}
        </AnalysisPanel>

        <AnalysisPanel
          title="技法标本"
          count={specimens.total}
          loading={loading.specimens}
          onRefresh={() => {
            void loadSpecimens()
          }}
          onLoadMore={() => {
            void loadSpecimens({ append: true, cursor: specimens.nextCursor })
          }}
          hasMore={specimens.hasMore}
          filterControls={(
            <Field label="technique_family">
              <input
                value={specimenFilters.techniqueFamily}
                onChange={event => setSpecimenFilters(current => ({ ...current, techniqueFamily: event.target.value }))}
                className={inputClass}
                placeholder="action_as_emotion"
                aria-label="技法 family"
              />
            </Field>
          )}
        >
          {specimens.items.length > 0 ? (
            <div className="space-y-2">
              {specimens.items.map(specimen => (
                <SpecimenCard key={specimen.specimen_id} specimen={specimen} />
              ))}
            </div>
          ) : (
            <EmptyState title="暂无技法标本" detail="当前筛选没有匹配的 specimen。" />
          )}
        </AnalysisPanel>
      </div>

      {error && (
        <ErrorCallout
          compact
          title={error.title}
          message={error.message}
          diagnostic={error.diagnostic}
          className="rounded-md"
          onRetry={reloadAll}
          retryLabel="重试加载"
          onClose={() => setError(null)}
        />
      )}
    </section>
  )
}

function AnalysisPanel({
  title,
  count,
  loading,
  hasMore,
  filterControls,
  children,
  onRefresh,
  onLoadMore,
}: {
  title: string
  count: number
  loading: boolean
  hasMore: boolean
  filterControls: ReactNode
  children: ReactNode
  onRefresh: () => void
  onLoadMore: () => void
}) {
  return (
    <section className="min-w-0 space-y-3 rounded-md border border-border bg-background p-3">
      <div className="flex items-center justify-between gap-2">
        <div className="min-w-0">
          <h4 className="truncate text-xs font-semibold text-foreground">{title}</h4>
          <p className="text-[11px] text-muted-foreground">{count} 条</p>
        </div>
        <button
          type="button"
          onClick={onRefresh}
          disabled={loading}
          className="inline-flex shrink-0 items-center gap-1 rounded border border-border px-2 py-1 text-[11px] text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
        >
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
          筛选
        </button>
      </div>
      {filterControls}
      {loading ? (
        <div className="rounded-md border border-border bg-card px-3 py-4 text-center text-[11px] text-muted-foreground">
          正在加载...
        </div>
      ) : children}
      {hasMore && (
        <button
          type="button"
          onClick={onLoadMore}
          disabled={loading}
          className="inline-flex w-full items-center justify-center gap-1.5 rounded border border-border px-3 py-1.5 text-xs text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
        >
          加载更多
        </button>
      )}
    </section>
  )
}

function ObservationCard({ observation }: { observation: reference.CorpusFeatureObservation }) {
  return (
    <article data-testid="reference-corpus-observation-card" className="space-y-2 px-2.5 py-2 text-xs">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="min-w-0 break-all font-medium text-foreground">
          {observation.feature_family}.{observation.feature_key}
        </span>
        <span className="shrink-0 text-[11px] text-muted-foreground">{formatConfidence(observation.confidence)}</span>
      </div>
      <div className="flex flex-wrap gap-1">
        <Chip>{observation.node_type}</Chip>
        <Chip>{formatReviewState(observation.review_state)}</Chip>
        <Chip>{formatValidityState(observation.validity_state)}</Chip>
        <Chip>{observation.value_kind}</Chip>
      </div>
      {observation.value_preview && (
        <p className="break-words leading-relaxed text-foreground">{boundedText(observation.value_preview, 180)}</p>
      )}
      {(observation.evidence_preview || observation.explanation) && (
        <p className="break-words rounded bg-secondary/60 px-2 py-1.5 leading-relaxed text-muted-foreground">
          {[boundedText(observation.evidence_preview ?? '', 120), boundedText(observation.explanation ?? '', 160)].filter(Boolean).join(' · ')}
        </p>
      )}
      <p className="break-all text-[11px] text-muted-foreground">
        {observation.node_id} · {observation.text_hash} · {observation.run_id}
      </p>
    </article>
  )
}

function SpecimenCard({ specimen }: { specimen: reference.CorpusTechniqueSpecimen }) {
  return (
    <article data-testid="reference-corpus-specimen-card" className="space-y-2 rounded-md border border-border bg-card px-2.5 py-2 text-xs">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="min-w-0 break-all font-medium text-foreground">{specimen.technique_family}</span>
        <span className="shrink-0 text-[11px] text-muted-foreground">{formatConfidence(specimen.confidence)}</span>
      </div>
      <div className="flex flex-wrap gap-1">
        <Chip>{formatReviewState(specimen.review_state)}</Chip>
        <Chip>{formatValidityState(specimen.validity_state)}</Chip>
        <Chip>{specimen.why_it_works.trace_complete ? '证据完整' : '证据不足'}</Chip>
      </div>
      <p className="break-words leading-relaxed text-foreground">{boundedText(specimen.technique_abstract, 220)}</p>
      <p className="break-words rounded bg-secondary/60 px-2 py-1.5 leading-relaxed text-muted-foreground">
        {boundedText(specimen.transfer_template, 180)}
      </p>
      <KeyValue label="触发" value={specimen.trigger_context} />
      <KeyValue label="效果" value={specimen.effect_on_reader} />
      {specimen.why_it_works.contributing_factors.slice(0, 2).map(factor => (
        <div key={`${specimen.specimen_id}:${factor.factor}`} className="rounded border border-border bg-background px-2 py-1.5">
          <p className="font-medium text-foreground">{boundedText(factor.factor, 80)}</p>
          <p className="mt-1 break-words leading-relaxed text-muted-foreground">{boundedText(factor.explanation, 160)}</p>
          {factor.evidence.length > 0 && (
            <p className="mt-1 break-words text-[11px] text-muted-foreground">
              {factor.evidence.map(item => `${item.feature_family}.${item.feature_key}`).join(' / ')}
            </p>
          )}
        </div>
      ))}
      {specimen.evidence.length > 0 && (
        <div className="space-y-1">
          <p className="text-[11px] font-medium text-muted-foreground">evidence</p>
          {specimen.evidence.slice(0, 2).map(item => (
            <p key={`${specimen.specimen_id}:${item.observation_id}`} className="break-words rounded border border-border bg-background px-2 py-1 text-[11px] text-muted-foreground">
              {item.feature_family}.{item.feature_key} · {boundedText(item.evidence_preview ?? item.value_preview ?? '', 120)}
            </p>
          ))}
        </div>
      )}
      <p className="break-all text-[11px] text-muted-foreground">
        {specimen.source_node_id} · {specimen.analysis_run_id}
      </p>
    </article>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

function Chip({ children }: { children: ReactNode }) {
  return (
    <span className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
      {children}
    </span>
  )
}

function KeyValue({ label, value }: { label: string; value: string }) {
  if (!value) return null
  return (
    <p className="grid grid-cols-[40px_minmax(0,1fr)] gap-2 break-words text-[11px] leading-relaxed">
      <span className="text-muted-foreground">{label}</span>
      <span className="text-foreground">{boundedText(value, 160)}</span>
    </p>
  )
}

function EmptyState({ title, detail }: { title: string; detail: string }) {
  return (
    <div className="rounded-md border border-dashed border-border px-3 py-4 text-center text-xs">
      <Layers3 className="mx-auto h-4 w-4 text-muted-foreground" />
      <p className="mt-2 font-medium text-foreground">{title}</p>
      <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">{detail}</p>
    </div>
  )
}

function observationFilterPayload(filters: ObservationFilters): Record<string, string> {
  return compactFilters({
    feature_family: filters.featureFamily,
    feature_key: filters.featureKey,
    node_type: filters.nodeType,
    review_state: filters.reviewState,
    validity_state: filters.validityState,
    min_confidence: normalizedConfidence(filters.minConfidence),
  })
}

function specimenFilterPayload(filters: SpecimenFilters): Record<string, string> {
  return compactFilters({
    technique_family: filters.techniqueFamily,
    review_state: filters.reviewState,
    validity_state: filters.validityState,
    min_confidence: normalizedConfidence(filters.minConfidence),
  })
}

function compactFilters(values: Record<string, string | null>): Record<string, string> {
  return Object.fromEntries(
    Object.entries(values)
      .map(([key, value]) => [key, String(value ?? '').trim()] as const)
      .filter(([, value]) => value.length > 0),
  )
}

function normalizedConfidence(value: string): string | null {
  if (!value.trim()) return null
  const parsed = Number(value)
  if (!Number.isFinite(parsed)) return null
  return String(Math.min(1, Math.max(0, parsed)))
}

function isFailedAnchorStatus(status: string): boolean {
  return status.startsWith('failed_') || status === 'cancelled'
}

function anchorScopeLabel(scope: string): string {
  if (scope === 'workspace_corpus') return '工作区语料'
  if (scope === 'novel') return '当前小说'
  return scope || '未知范围'
}

function formatConfidence(value: number): string {
  return Number.isFinite(value) ? value.toFixed(2) : '0.00'
}

function formatReviewState(value: string): string {
  return REVIEW_STATE_OPTIONS.find(option => option.value === value)?.label ?? value
}

function formatValidityState(value: string): string {
  return VALIDITY_STATE_OPTIONS.find(option => option.value === value)?.label ?? value
}

function boundedText(value: string, limit: number): string {
  const normalized = value.trim().replace(/\s+/g, ' ')
  if (normalized.length <= limit) return normalized
  return `${normalized.slice(0, limit).trimEnd()}...`
}
