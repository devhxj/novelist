import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import {
  AlertTriangle,
  BarChart3,
  CheckSquare,
  ChevronLeft,
  ChevronRight,
  Edit3,
  Loader2,
  Plus,
  Search,
  Square,
  Tags,
  Trash2,
} from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { styleSample } from '@/lib/novelist/types'
import StyleExtractionPanel from './StyleExtractionPanel'

interface Props {
  novelId: number
}

type SampleFormState = {
  sampleId: number | null
  isGlobal: boolean
  name: string
  content: string
  tags: string
}

const PAGE_SIZE = 2

const EMPTY_FORM: SampleFormState = {
  sampleId: null,
  isGlobal: false,
  name: '',
  content: '',
  tags: '',
}

export default function StyleSampleLibraryView({ novelId }: Props) {
  const app = useApp()
  const [samples, setSamples] = useState<styleSample.StyleSample[]>([])
  const [detail, setDetail] = useState<styleSample.StyleSampleDetail | null>(null)
  const [selectedIds, setSelectedIds] = useState<number[]>([])
  const [query, setQuery] = useState('')
  const [tagFilter, setTagFilter] = useState('')
  const [includeGlobal, setIncludeGlobal] = useState(true)
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const [totalPages, setTotalPages] = useState(1)
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [form, setForm] = useState<SampleFormState | null>(null)

  const selectedSet = useMemo(() => new Set(selectedIds), [selectedIds])
  const parsedTagFilter = useMemo(() => parseTags(tagFilter), [tagFilter])
  const currentPage = Math.min(page, Math.max(totalPages, 1))

  const loadSamples = useCallback(async (targetPage = page) => {
    setLoading(true)
    try {
      const result = await app.SearchStyleSamples({
        novel_id: novelId,
        include_global: includeGlobal,
        query,
        tags: parsedTagFilter,
        page: targetPage,
        size: PAGE_SIZE,
      })
      setSamples(result.items ?? [])
      setTotal(result.total ?? 0)
      setTotalPages(Math.max(1, result.total_pages || 1))
      setPage(result.page || targetPage)
      setError(null)
    } catch (err) {
      setError(errorText(err, '加载风格素材失败'))
    } finally {
      setLoading(false)
    }
  }, [app, includeGlobal, novelId, page, parsedTagFilter, query])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      if (!cancelled) setLoading(true)
      try {
        const result = await app.SearchStyleSamples({
          novel_id: novelId,
          include_global: includeGlobal,
          query,
          tags: parsedTagFilter,
          page,
          size: PAGE_SIZE,
        })
        if (!cancelled) {
          setSamples(result.items ?? [])
          setTotal(result.total ?? 0)
          setTotalPages(Math.max(1, result.total_pages || 1))
          setPage(result.page || page)
          setError(null)
        }
      } catch (err) {
        if (!cancelled) setError(errorText(err, '加载风格素材失败'))
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [app, includeGlobal, novelId, page, parsedTagFilter, query])

  async function loadDetail(sampleId: number) {
    try {
      const result = await app.GetStyleSample({ sample_id: sampleId })
      setDetail(result)
      setError(null)
    } catch (err) {
      setError(errorText(err, '加载样本详情失败'))
    }
  }

  function beginCreate() {
    setForm(EMPTY_FORM)
    setDetail(null)
    setError(null)
  }

  async function beginEdit(sample: styleSample.StyleSample) {
    try {
      const result = await app.GetStyleSample({ sample_id: sample.sample_id })
      if (!result) {
        setError('样本不存在或已被删除')
        return
      }

      setForm({
        sampleId: result.sample_id,
        isGlobal: result.is_global,
        name: result.name,
        content: result.content,
        tags: result.tags.join('; '),
      })
      setDetail(result)
      setError(null)
    } catch (err) {
      setError(errorText(err, '加载编辑内容失败'))
    }
  }

  async function saveSample(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!form) return
    const name = form.name.trim()
    const content = form.content.trim()
    if (!name) {
      setError('请输入样本名称')
      return
    }
    if (!content) {
      setError('请输入样本内容')
      return
    }

    setSaving(true)
    try {
      const input = {
        novel_id: form.isGlobal ? null : novelId,
        is_global: form.isGlobal,
        name,
        content,
        tags: parseTags(form.tags),
        source_metadata: {
          source_type: 'manual',
          source_id: form.sampleId ? `style-sample-${form.sampleId}` : 'style-sample-ui',
          source_hash: `ui-${stableHash(content)}`,
        },
      }

      const saved = form.sampleId
        ? await app.UpdateStyleSample({ sample_id: form.sampleId, ...input })
        : await app.CreateStyleSample(input)
      setForm(null)
      await loadSamples(1)
      await loadDetail(saved.sample_id)
      setSelectedIds(ids => ids.includes(saved.sample_id) ? ids : [saved.sample_id, ...ids])
      setError(null)
    } catch (err) {
      setError(errorText(err, '保存风格样本失败'))
    } finally {
      setSaving(false)
    }
  }

  async function deleteSample(sample: styleSample.StyleSample) {
    try {
      await app.DeleteStyleSample({ sample_id: sample.sample_id })
      setSelectedIds(ids => ids.filter(id => id !== sample.sample_id))
      if (detail?.sample_id === sample.sample_id) setDetail(null)
      await loadSamples(page)
      setError(null)
    } catch (err) {
      setError(errorText(err, '删除风格样本失败'))
    }
  }

  function toggleSelected(sampleId: number, selected: boolean) {
    setSelectedIds(ids => selected
      ? ids.includes(sampleId) ? ids : [...ids, sampleId]
      : ids.filter(id => id !== sampleId))
  }

  function clearFilters() {
    setQuery('')
    setTagFilter('')
    setIncludeGlobal(true)
    setPage(1)
  }

  return (
    <main className="flex-1 min-w-0 overflow-y-auto overscroll-contain bg-background">
      <div className="mx-auto flex w-full max-w-7xl flex-col gap-4 px-5 py-5 xl:px-6">
        <header className="flex flex-col gap-3 border-b border-border pb-4 lg:flex-row lg:items-end lg:justify-between">
          <div className="min-w-0">
            <div className="flex items-center gap-2">
              <Tags className="h-5 w-5 text-primary" />
              <h1 className="text-xl font-semibold text-foreground">风格素材</h1>
            </div>
            <p className="mt-1 max-w-3xl text-sm leading-relaxed text-muted-foreground">
              保存可复用的文风样本，按全局或当前作品范围筛选，并查看确定性的句长、标点和段落统计。
            </p>
          </div>
          <button
            type="button"
            onClick={beginCreate}
            className="inline-flex h-9 items-center justify-center gap-2 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-opacity hover:opacity-90 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <Plus className="h-4 w-4" />
            新建样本
          </button>
        </header>

        {error && (
          <section
            role="alert"
            className="flex items-start gap-2 rounded-md border border-danger-border bg-danger-bg px-3 py-2 text-sm text-foreground"
          >
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <span className="min-w-0 break-words">{error}</span>
          </section>
        )}

        <section className="grid grid-cols-1 gap-4 2xl:grid-cols-[minmax(0,1fr)_420px]">
          <div className="min-w-0 space-y-4">
            <div className="rounded-lg border border-border bg-card p-4">
              <div className="grid grid-cols-1 gap-3 xl:grid-cols-[minmax(0,1fr)_220px_auto] xl:items-end">
                <label className="block">
                  <span className="mb-1 block text-xs font-medium text-muted-foreground">搜索</span>
                  <div className="relative">
                    <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                    <input
                      value={query}
                      onChange={event => { setQuery(event.target.value); setPage(1) }}
                      placeholder="搜索样本..."
                      className="h-9 w-full rounded-md border border-input bg-background pl-9 pr-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20"
                    />
                  </div>
                </label>
                <label className="block">
                  <span className="mb-1 block text-xs font-medium text-muted-foreground">标签</span>
                  <input
                    value={tagFilter}
                    onChange={event => { setTagFilter(event.target.value); setPage(1) }}
                    placeholder="标签过滤..."
                    className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20"
                  />
                </label>
                <div className="flex flex-wrap items-center gap-2">
                  <button
                    type="button"
                    onClick={() => { setIncludeGlobal(true); setPage(1) }}
                    aria-pressed={includeGlobal}
                    className={scopeButtonClass(includeGlobal)}
                  >
                    包含全局
                  </button>
                  <button
                    type="button"
                    onClick={() => { setIncludeGlobal(false); setPage(1) }}
                    aria-pressed={!includeGlobal}
                    className={scopeButtonClass(!includeGlobal)}
                  >
                    仅当前作品
                  </button>
                  <button
                    type="button"
                    onClick={clearFilters}
                    className="h-9 rounded-md border border-border bg-background px-3 text-xs font-medium text-foreground transition-colors hover:bg-muted"
                  >
                    清除筛选
                  </button>
                </div>
              </div>

              <div className="mt-3 flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
                <span>共 {total} 个样本 · 已选 {selectedIds.length} 个样本</span>
                <span>第 {currentPage} / {totalPages} 页</span>
              </div>
            </div>

            {loading ? (
              <div className="grid grid-cols-1 gap-3 xl:grid-cols-2" aria-busy="true" aria-label="正在加载风格素材">
                {[0, 1].map(index => (
                  <div key={index} className="h-44 animate-pulse rounded-lg border border-border bg-card" />
                ))}
              </div>
            ) : samples.length === 0 ? (
              <div className="rounded-lg border border-dashed border-border bg-card/60 px-5 py-10 text-center">
                <BarChart3 className="mx-auto h-8 w-8 text-muted-foreground" />
                <h2 className="mt-3 text-sm font-medium text-foreground">没有匹配的风格样本</h2>
                <p className="mt-1 text-sm text-muted-foreground">调整搜索条件，或新建一个样本。</p>
              </div>
            ) : (
              <div className="grid grid-cols-1 gap-3 xl:grid-cols-2" aria-label="风格样本列表">
                {samples.map(sample => (
                  <StyleSampleCard
                    key={sample.sample_id}
                    sample={sample}
                    selected={selectedSet.has(sample.sample_id)}
                    onSelectedChange={checked => toggleSelected(sample.sample_id, checked)}
                    onView={() => { void loadDetail(sample.sample_id) }}
                    onEdit={() => { void beginEdit(sample) }}
                    onDelete={() => { void deleteSample(sample) }}
                  />
                ))}
              </div>
            )}

            <div className="flex items-center justify-between gap-3">
              <button
                type="button"
                onClick={() => setPage(value => Math.max(1, value - 1))}
                disabled={loading || page <= 1}
                className="inline-flex h-9 items-center gap-1.5 rounded-md border border-border bg-background px-3 text-sm text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
              >
                <ChevronLeft className="h-4 w-4" />
                上一页
              </button>
              <span className="text-xs text-muted-foreground">第 {currentPage} / {totalPages} 页</span>
              <button
                type="button"
                onClick={() => setPage(value => Math.min(totalPages, value + 1))}
                disabled={loading || page >= totalPages}
                className="inline-flex h-9 items-center gap-1.5 rounded-md border border-border bg-background px-3 text-sm text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50"
              >
                下一页
                <ChevronRight className="h-4 w-4" />
              </button>
            </div>
          </div>

          <aside className="min-w-0 space-y-4">
            <StyleExtractionPanel novelId={novelId} selectedIds={selectedIds} />
            {form ? (
              <StyleSampleForm
                form={form}
                saving={saving}
                onChange={setForm}
                onCancel={() => setForm(null)}
                onSubmit={saveSample}
              />
            ) : (
              <StyleSampleDetailPanel detail={detail} selectedCount={selectedIds.length} />
            )}
          </aside>
        </section>
      </div>
    </main>
  )
}

function StyleSampleCard({
  sample,
  selected,
  onSelectedChange,
  onView,
  onEdit,
  onDelete,
}: {
  sample: styleSample.StyleSample
  selected: boolean
  onSelectedChange: (selected: boolean) => void
  onView: () => void
  onEdit: () => void
  onDelete: () => void
}) {
  return (
    <article className="rounded-lg border border-border bg-card p-4 transition-shadow hover:shadow-sm">
      <div className="flex items-start justify-between gap-3">
        <label className="flex min-w-0 flex-1 items-start gap-2">
          <input
            type="checkbox"
            checked={selected}
            onChange={event => onSelectedChange(event.target.checked)}
            aria-label={`选择样本 ${sample.name}`}
            className="mt-1 h-4 w-4 shrink-0"
          />
          <span className="min-w-0">
            <span className="block truncate text-sm font-semibold text-foreground">{sample.name}</span>
            <span className="mt-1 inline-flex items-center rounded bg-secondary px-2 py-0.5 text-[11px] text-muted-foreground">
              {sample.is_global ? '全局' : '当前作品'}
            </span>
          </span>
        </label>
        <div className="flex shrink-0 items-center gap-1">
          <button
            type="button"
            onClick={onView}
            aria-label={`查看样本 ${sample.name}`}
            className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <BarChart3 className="h-4 w-4" />
          </button>
          <button
            type="button"
            onClick={onEdit}
            aria-label={`编辑 ${sample.name}`}
            className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <Edit3 className="h-4 w-4" />
          </button>
          <button
            type="button"
            onClick={onDelete}
            aria-label={`删除 ${sample.name}`}
            className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-danger-bg hover:text-destructive focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <Trash2 className="h-4 w-4" />
          </button>
        </div>
      </div>

      <p className="mt-3 line-clamp-3 min-h-12 text-sm leading-relaxed text-foreground">{sample.preview}</p>

      <div className="mt-3 flex flex-wrap gap-1.5">
        {sample.tags.slice(0, 5).map(tag => (
          <span key={tag} className="rounded bg-muted px-2 py-0.5 text-[11px] text-muted-foreground">{tag}</span>
        ))}
      </div>

      <div className="mt-3 grid grid-cols-2 gap-2 text-[11px] text-muted-foreground sm:grid-cols-4">
        <Metric label="词数" value={`${sample.stats.word_count}`} />
        <Metric label="均句长" value={formatNumber(sample.stats.average_sentence_chars)} />
        <Metric label="标点密度" value={`${formatNumber(sample.stats.punctuation_per_100_chars)}%`} />
        <Metric label="段落" value={`${sample.stats.paragraph_count}`} />
      </div>
      <div className="mt-2 flex items-center justify-between gap-2 text-[11px] text-muted-foreground">
        <span>句长分布 {sample.stats.sentence_length_distribution.slice(0, 4).join(' / ') || '无'}</span>
        <span>{formatDate(sample.updated_at)}</span>
      </div>
    </article>
  )
}

function StyleSampleForm({
  form,
  saving,
  onChange,
  onCancel,
  onSubmit,
}: {
  form: SampleFormState
  saving: boolean
  onChange: (form: SampleFormState) => void
  onCancel: () => void
  onSubmit: (event: FormEvent<HTMLFormElement>) => void
}) {
  return (
    <form onSubmit={onSubmit} className="rounded-lg border border-border bg-card p-4">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h2 className="text-sm font-semibold text-foreground">{form.sampleId ? '编辑样本' : '新建样本'}</h2>
          <p className="mt-1 text-xs text-muted-foreground">标签可用分号、逗号或换行分隔。</p>
        </div>
        <label className="flex shrink-0 items-center gap-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={form.isGlobal}
            onChange={event => onChange({ ...form, isGlobal: event.target.checked })}
          />
          全局样本
        </label>
      </div>

      <div className="mt-4 space-y-3">
        <label className="block">
          <span className="mb-1 block text-xs font-medium text-muted-foreground">样本名称</span>
          <input
            value={form.name}
            onChange={event => onChange({ ...form, name: event.target.value })}
            className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20"
          />
        </label>
        <label className="block">
          <span className="mb-1 block text-xs font-medium text-muted-foreground">样本内容</span>
          <textarea
            value={form.content}
            onChange={event => onChange({ ...form, content: event.target.value })}
            className="min-h-36 w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm leading-relaxed text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20"
          />
        </label>
        <label className="block">
          <span className="mb-1 block text-xs font-medium text-muted-foreground">标签</span>
          <textarea
            value={form.tags}
            onChange={event => onChange({ ...form, tags: event.target.value })}
            className="min-h-20 w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm leading-relaxed text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20"
          />
        </label>
      </div>

      <div className="mt-4 flex items-center justify-end gap-2">
        <button
          type="button"
          onClick={onCancel}
          className="h-9 rounded-md border border-border bg-background px-3 text-sm text-foreground transition-colors hover:bg-muted"
        >
          取消
        </button>
        <button
          type="submit"
          disabled={saving}
          className="inline-flex h-9 items-center gap-2 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60"
        >
          {saving && <Loader2 className="h-4 w-4 animate-spin" />}
          保存样本
        </button>
      </div>
    </form>
  )
}

function StyleSampleDetailPanel({ detail, selectedCount }: { detail: styleSample.StyleSampleDetail | null; selectedCount: number }) {
  if (!detail) {
    return (
      <section className="rounded-lg border border-border bg-card p-4">
        <div className="flex items-center gap-2">
          {selectedCount > 0 ? <CheckSquare className="h-4 w-4 text-primary" /> : <Square className="h-4 w-4 text-muted-foreground" />}
          <h2 className="text-sm font-semibold text-foreground">样本详情</h2>
        </div>
        <p className="mt-3 text-sm leading-relaxed text-muted-foreground">
          选择或查看一个样本后，这里会显示全文和完整统计。当前已选 {selectedCount} 个样本。
        </p>
      </section>
    )
  }

  return (
    <section className="rounded-lg border border-border bg-card p-4">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <h2 className="truncate text-sm font-semibold text-foreground">{detail.name}</h2>
          <p className="mt-1 text-xs text-muted-foreground">{detail.is_global ? '全局样本' : '当前作品样本'} · {detail.tags.join(' / ') || '无标签'}</p>
        </div>
      </div>
      <div className="mt-3 max-h-48 overflow-y-auto rounded-md border border-border bg-background px-3 py-2 text-sm leading-relaxed text-foreground">
        {detail.content}
      </div>

      <h3 className="mt-4 text-sm font-semibold text-foreground">完整统计</h3>
      <div className="mt-3 grid grid-cols-2 gap-2 text-xs">
        <Metric label="字符" value={`${detail.stats.character_count}`} />
        <Metric label="词数" value={`${detail.stats.word_count}`} />
        <Metric label="句子" value={`${detail.stats.sentence_count}`} />
        <Metric label="段落" value={`${detail.stats.paragraph_count}`} />
        <Metric label="均句长" value={formatNumber(detail.stats.average_sentence_chars)} />
        <Metric label="句长标准差" value={formatNumber(detail.stats.sentence_length_std_dev)} />
        <Metric label="标点密度" value={`${formatNumber(detail.stats.punctuation_per_100_chars)}%`} />
        <Metric label="引号密度" value={`${formatNumber(detail.stats.quote_density)}%`} />
        <Metric label="段落均长" value={formatNumber(detail.stats.average_paragraph_chars)} />
        <Metric label="对白占比" value={formatRatio(detail.stats.dialogue_ratio)} />
      </div>
      <div className="mt-3 rounded-md border border-border bg-background px-3 py-2">
        <div className="text-xs font-medium text-muted-foreground">句长分布</div>
        <div className="mt-2 flex flex-wrap gap-1">
          {detail.stats.sentence_length_distribution.length > 0 ? detail.stats.sentence_length_distribution.map((length, index) => (
            <span key={`${index}-${length}`} className="rounded bg-muted px-2 py-0.5 text-[11px] text-muted-foreground">
              {index + 1}: {length}
            </span>
          )) : <span className="text-xs text-muted-foreground">无句子</span>}
        </div>
      </div>
    </section>
  )
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-md border border-border bg-background px-2.5 py-2">
      <div className="text-[11px] text-muted-foreground">{label}</div>
      <div className="mt-0.5 truncate text-sm font-medium text-foreground">{value}</div>
    </div>
  )
}

function parseTags(value: string | string[]): string[] {
  const values = Array.isArray(value) ? value : [value]
  const seen = new Set<string>()
  const tags: string[] = []
  for (const item of values) {
    for (const part of item.split(/[;；,，\r\n]+/)) {
      const tag = part.trim()
      const key = tag.toLowerCase()
      if (tag && !seen.has(key)) {
        seen.add(key)
        tags.push(tag)
      }
    }
  }
  return tags
}

function errorText(error: unknown, fallback: string): string {
  if (error instanceof Error) return error.message
  if (typeof error === 'string') return error
  return fallback
}

function scopeButtonClass(active: boolean): string {
  return active
    ? 'h-9 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground transition-opacity hover:opacity-90'
    : 'h-9 rounded-md border border-border bg-background px-3 text-xs font-medium text-foreground transition-colors hover:bg-muted'
}

function formatDate(value: unknown): string {
  const raw = String(value ?? '')
  const date = new Date(raw)
  if (Number.isNaN(date.getTime())) return raw
  return new Intl.DateTimeFormat('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }).format(date)
}

function formatNumber(value: number): string {
  return new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 2 }).format(value)
}

function formatRatio(value: number): string {
  return `${formatNumber(value * 100)}%`
}

function stableHash(value: string): string {
  let hash = 0
  for (let index = 0; index < value.length; index += 1) {
    hash = ((hash << 5) - hash + value.charCodeAt(index)) | 0
  }
  return Math.abs(hash).toString(16)
}
