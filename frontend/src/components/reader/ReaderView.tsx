import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertTriangle, BookOpen, Clock, Plus, Pencil, Trash2, X, Eye } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reader } from '@/hooks/useApp'

interface Props { novelId: number; focusId?: number }

type TypeFilter = 'all' | 'known' | 'suspense' | 'misconception'
type StatusFilter = 'all' | 'unrevealed' | 'revealed'

const WINDOW = 20

const TYPE_FILTERS: { key: TypeFilter; label: string; icon: typeof BookOpen; color: string }[] = [
  { key: 'all', label: '全部', icon: BookOpen, color: 'text-muted-foreground' },
  { key: 'known', label: '已知', icon: BookOpen, color: 'text-tag-green-foreground' },
  { key: 'suspense', label: '悬念', icon: Clock, color: 'text-tag-amber-foreground' },
  { key: 'misconception', label: '误解', icon: AlertTriangle, color: 'text-tag-rose-foreground' },
]

const STATUS_FILTERS: { key: StatusFilter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'unrevealed', label: '未回收' },
  { key: 'revealed', label: '已回收' },
]

const TYPES = [
  { value: 'known', label: '已知' },
  { value: 'suspense', label: '悬念' },
  { value: 'misconception', label: '误解' },
]

type EditMode = { type: 'create' } | { type: 'edit'; item: reader.ReaderPerspective } | null

type EditForm = {
  type: string
  content: string
  related_truth: string
  planted_chapter: number
  revealed_chapter: number
}

const EMPTY_FORM: EditForm = {
  type: 'known',
  content: '',
  related_truth: '',
  planted_chapter: 1,
  revealed_chapter: 0,
}

function typeMeta(type: string) {
  switch (type) {
    case 'known':
      return { icon: BookOpen, color: 'text-tag-green-foreground', bg: 'bg-tag-green', label: '已知' }
    case 'suspense':
      return { icon: Clock, color: 'text-tag-amber-foreground', bg: 'bg-tag-amber', label: '悬念' }
    case 'misconception':
      return { icon: AlertTriangle, color: 'text-tag-rose-foreground', bg: 'bg-tag-rose', label: '误解' }
    default:
      return { icon: BookOpen, color: 'text-muted-foreground', bg: 'bg-muted', label: type }
  }
}

export default function ReaderView({ novelId, focusId }: Props) {
  const app = useApp()

  const [entries, setEntries] = useState<reader.ReaderPerspective[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [typeFilter, setTypeFilter] = useState<TypeFilter>('all')
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all')
  const [expandedId, setExpandedId] = useState<number | null>(null)
  const [windowCenter, setWindowCenter] = useState(0)
  const [editMode, setEditMode] = useState<EditMode>(null)
  const [form, setForm] = useState<EditForm>(EMPTY_FORM)
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    if (!novelId) { setEntries([]); return }
    setLoading(true)
    setError(null)
    try {
      const items = await app.GetReaderPerspectives(novelId)
      setEntries(items ?? [])
      if (items && items.length > 0) {
        const maxCh = Math.max(...items.map(e => e.planted_chapter))
        setWindowCenter(prev => prev || maxCh)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : '加载失败')
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => { load() }, [load])

  useEffect(() => {
    if (focusId && focusId > 0 && entries.length > 0) {
      const entry = entries.find(e => e.id === focusId)
      if (entry) setWindowCenter(entry.planted_chapter)
    }
  }, [focusId, entries])

  const filtered = useMemo(() => {
    let items = entries
    if (typeFilter !== 'all') items = items.filter(e => e.type === typeFilter)
    if (statusFilter === 'unrevealed') items = items.filter(e => e.revealed_chapter === 0)
    if (statusFilter === 'revealed') items = items.filter(e => e.revealed_chapter > 0)
    return items
  }, [entries, typeFilter, statusFilter])

  const groupedDesc = useMemo(() => {
    const map = new Map<number, reader.ReaderPerspective[]>()
    for (const e of filtered) {
      const ch = e.planted_chapter
      if (!map.has(ch)) map.set(ch, [])
      map.get(ch)!.push(e)
    }
    return [...map.entries()].sort(([a], [b]) => b - a)
  }, [filtered])

  const windowFrom = Math.max(1, windowCenter - WINDOW)
  const windowTo = windowCenter

  const visibleChapters = groupedDesc.filter(([ch]) => ch >= windowFrom && ch <= windowTo)
  const beforeChapters = groupedDesc.filter(([ch]) => ch < windowFrom)

  const beforeCount = beforeChapters.reduce((s, [, items]) => s + items.length, 0)
  const minChapter = groupedDesc.length > 0 ? groupedDesc[groupedDesc.length - 1][0] : 0
  const maxChapter = groupedDesc.length > 0 ? groupedDesc[0][0] : 0

  function shiftWindow(delta: number) {
    setWindowCenter(prev => Math.max(WINDOW, Math.min(maxChapter, prev + delta)))
  }

  // ── CRUD handlers ────────────────────────────────────

  function openCreate() {
    setError(null)
    setForm({ ...EMPTY_FORM, planted_chapter: Math.max(1, windowCenter) })
    setEditMode({ type: 'create' })
  }

  function openEdit(item: reader.ReaderPerspective) {
    setError(null)
    setForm({
      type: item.type,
      content: item.content,
      related_truth: item.related_truth || '',
      planted_chapter: item.planted_chapter,
      revealed_chapter: item.revealed_chapter,
    })
    setEditMode({ type: 'edit', item })
  }

  async function handleCreate() {
    if (!form.content.trim()) { setError('请输入内容'); return }
    if (!form.type) { setError('请选择类型'); return }
    setSaving(true)
    try {
      const created = await app.CreateReaderPerspective(novelId, {
        type: form.type,
        content: form.content,
        planted_chapter: form.planted_chapter,
        related_truth: form.related_truth,
        revealed_chapter: form.revealed_chapter,
      })
      setEditMode(null)
      setForm(EMPTY_FORM)
      await load()
      setExpandedId(created.id)
    } catch (err) {
      setError(err instanceof Error ? err.message : '创建失败')
    } finally {
      setSaving(false)
    }
  }

  async function handleUpdate() {
    if (!editMode || editMode.type !== 'edit') return
    if (!form.content.trim()) { setError('请输入内容'); return }
    const entryId = editMode.item.id
    setSaving(true)
    try {
      await app.UpdateReaderPerspective(entryId, novelId, {
        type: form.type,
        content: form.content,
        related_truth: form.related_truth,
        planted_chapter: form.planted_chapter,
        revealed_chapter: form.revealed_chapter,
      })
      setEditMode(null)
      setForm(EMPTY_FORM)
      await load()
      setExpandedId(entryId)
    } catch (err) {
      setError(err instanceof Error ? err.message : '更新失败')
    } finally {
      setSaving(false)
    }
  }

  async function handleDelete(id: number) {
    if (!confirm('确定要删除这条读者认知条目吗？此操作不可撤销。')) return
    setSaving(true)
    try {
      await app.DeleteReaderPerspective(id, novelId)
      if (expandedId === id) setExpandedId(null)
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : '删除失败')
    } finally {
      setSaving(false)
    }
  }

  async function handleQuickReveal(item: reader.ReaderPerspective) {
    setSaving(true)
    try {
      await app.UpdateReaderPerspective(item.id, novelId, {
        type: item.type,
        content: item.content,
        related_truth: item.related_truth || '',
        planted_chapter: item.planted_chapter,
        revealed_chapter: item.planted_chapter,
      })
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : '更新失败')
    } finally {
      setSaving(false)
    }
  }

  // ── Form fields ──────────────────────────────────────

  function renderFormFields() {
    return (
      <div className="space-y-3">
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">类型</label>
          <select
            value={form.type}
            onChange={e => setForm(f => ({ ...f, type: e.target.value }))}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {TYPES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
          </select>
        </div>
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">内容</label>
          <textarea
            value={form.content}
            onChange={e => setForm(f => ({ ...f, content: e.target.value }))}
            rows={3}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y"
            placeholder="读者知道/想知道/误以为的事情"
          />
        </div>
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">作者视角真相（可选）</label>
          <textarea
            value={form.related_truth}
            onChange={e => setForm(f => ({ ...f, related_truth: e.target.value }))}
            rows={2}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y"
            placeholder="真实情况是什么"
          />
        </div>
        <div className="flex gap-3">
          <div className="flex-1">
            <label className="text-xs font-medium text-muted-foreground mb-1 block">种下章节</label>
            <input
              type="number"
              value={form.planted_chapter}
              onChange={e => setForm(f => ({ ...f, planted_chapter: parseInt(e.target.value) || 1 }))}
              min={1}
              className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </div>
          <div className="flex-1">
            <label className="text-xs font-medium text-muted-foreground mb-1 block">回收章节（0=未回收）</label>
            <input
              type="number"
              value={form.revealed_chapter}
              onChange={e => setForm(f => ({ ...f, revealed_chapter: parseInt(e.target.value) || 0 }))}
              min={0}
              className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </div>
        </div>
      </div>
    )
  }

  return (
    <main className="flex-1 min-w-0 overflow-y-auto overscroll-contain bg-background">
      {loading ? (
        <div className="flex h-full items-center justify-center text-sm text-muted-foreground">加载中...</div>
      ) : error ? (
        <div className="flex h-full items-center justify-center text-sm text-destructive">{error}</div>
      ) : (
        <div className="max-w-3xl mx-auto px-5 py-6 space-y-5">
          {/* Header */}
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <Eye className="h-4 w-4 text-tag-blue-foreground" />
              <h2 className="text-sm font-semibold text-foreground">
                读者视角
                <span className="ml-2 text-xs font-normal text-muted-foreground">{filtered.length} 条</span>
              </h2>
            </div>
            <div className="flex items-center gap-2">
              <span className="text-[11px] text-muted-foreground">
                第 {windowFrom}-{windowTo} 章 · 共 {minChapter}-{maxChapter} 章
              </span>
              <button onClick={load} className="text-xs text-muted-foreground hover:text-muted-foreground transition-colors">
                刷新
              </button>
              <button
                onClick={openCreate}
                className="inline-flex items-center gap-1 px-2.5 py-1 rounded text-xs font-medium bg-primary text-primary-foreground hover:opacity-90 transition-opacity"
              >
                <Plus className="h-3 w-3" />
                新建
              </button>
            </div>
          </div>

          {/* Type filter */}
          <div className="flex gap-1">
            {TYPE_FILTERS.map(f => {
              const Icon = f.icon
              return (
                <button
                  key={f.key}
                  onClick={() => setTypeFilter(f.key)}
                  className={`inline-flex items-center gap-1 px-3 py-1 rounded text-xs transition-colors ${
                    typeFilter === f.key
                      ? 'bg-card border border-border text-foreground shadow-sm'
                      : 'text-muted-foreground hover:text-foreground'
                  }`}
                >
                  <Icon className={`h-3 w-3 ${typeFilter === f.key ? f.color : ''}`} />
                  {f.label}
                  {f.key !== 'all' && (
                    <span className="text-muted-foreground">({entries.filter(e => e.type === f.key).length})</span>
                  )}
                </button>
              )
            })}
          </div>

          {/* Status filter */}
          <div className="flex gap-1">
            {STATUS_FILTERS.map(f => (
              <button
                key={f.key}
                onClick={() => setStatusFilter(f.key)}
                className={`px-3 py-1 rounded text-xs transition-colors ${
                  statusFilter === f.key
                    ? 'bg-card border border-border text-foreground shadow-sm'
                    : 'text-muted-foreground hover:text-foreground'
                }`}
              >
                {f.label}
                {f.key === 'unrevealed' && (
                  <span className="ml-1 text-muted-foreground">({entries.filter(e => e.revealed_chapter === 0).length})</span>
                )}
                {f.key === 'revealed' && (
                  <span className="ml-1 text-muted-foreground">({entries.filter(e => e.revealed_chapter > 0).length})</span>
                )}
              </button>
            ))}
          </div>

          {/* Create form */}
          {editMode?.type === 'create' && (
            <div className="rounded-lg border border-border bg-card p-4">
              <div className="flex items-center justify-between mb-3">
                <span className="text-xs font-semibold text-foreground">新建读者认知条目</span>
                <button onClick={() => { setEditMode(null); setForm(EMPTY_FORM) }} className="p-0.5 rounded text-muted-foreground hover:text-foreground">
                  <X className="h-3.5 w-3.5" />
                </button>
              </div>
              {renderFormFields()}
              <div className="flex items-center gap-2 justify-end mt-3">
                <button onClick={() => { setEditMode(null); setForm(EMPTY_FORM) }} className="px-3 py-1 rounded text-xs text-muted-foreground hover:text-foreground transition-colors">取消</button>
                <button
                  onClick={handleCreate}
                  disabled={saving || !form.content.trim()}
                  className="px-3 py-1 rounded text-xs font-medium bg-primary text-primary-foreground hover:opacity-90 transition-opacity disabled:opacity-50"
                >
                  {saving ? '创建中...' : '创建'}
                </button>
              </div>
            </div>
          )}

          {/* Entries */}
          {groupedDesc.length === 0 ? (
            <div className="text-center py-12">
              <p className="text-sm text-muted-foreground">
                {entries.length === 0 ? '暂无读者认知数据' : '没有匹配的条目'}
              </p>
            </div>
          ) : (
            <div className="space-y-6">
              {beforeCount > 0 && (
                <button
                  onClick={() => shiftWindow(-WINDOW)}
                  className="w-full rounded-lg border border-dashed border-border bg-card/60 px-4 py-2.5 text-xs text-muted-foreground hover:bg-card hover:border-border hover:text-foreground transition-colors"
                >
                  ← 第 {beforeChapters[beforeChapters.length - 1]?.[0]}-{beforeChapters[0]?.[0]} 章 · {beforeCount} 条
                </button>
              )}

              {visibleChapters.map(([ch, items]) => (
                <div key={ch}>
                  <div className="flex items-center gap-1.5 mb-2">
                    <span className="text-xs font-medium text-muted-foreground">第 {ch} 章</span>
                    <span className="text-[10px] text-muted-foreground">{items.length} 条</span>
                  </div>
                  <div className="space-y-2">
                    {items.map(entry => {
                      const meta = typeMeta(entry.type)
                      const Icon = meta.icon
                      const isEditing = editMode?.type === 'edit' && editMode.item.id === entry.id
                      const isExpanded = expandedId === entry.id && !isEditing
                      const isRevealed = entry.revealed_chapter > 0

                      return isEditing ? (
                        <div key={entry.id} className="rounded-lg border border-border bg-card p-4">
                          <div className="flex items-center justify-between mb-3">
                            <span className="text-xs font-semibold text-foreground">编辑条目</span>
                            <button onClick={() => { setEditMode(null); setForm(EMPTY_FORM) }} className="p-0.5 rounded text-muted-foreground hover:text-foreground">
                              <X className="h-3.5 w-3.5" />
                            </button>
                          </div>
                          {renderFormFields()}
                          <div className="flex items-center gap-2 justify-end mt-3">
                            <button
                              onClick={() => handleDelete(entry.id)}
                              className="px-3 py-1 rounded text-xs text-destructive hover:bg-destructive/10 transition-colors"
                              disabled={saving}
                            >
                              <Trash2 className="h-3 w-3 inline mr-1" />删除
                            </button>
                            <button onClick={() => { setEditMode(null); setForm(EMPTY_FORM) }} className="px-3 py-1 rounded text-xs text-muted-foreground hover:text-foreground transition-colors">取消</button>
                            <button
                              onClick={handleUpdate}
                              disabled={saving || !form.content.trim()}
                              className="px-3 py-1 rounded text-xs font-medium bg-primary text-primary-foreground hover:opacity-90 transition-opacity disabled:opacity-50"
                            >
                              {saving ? '保存中...' : '保存'}
                            </button>
                          </div>
                        </div>
                      ) : (
                        <div
                          key={entry.id}
                          onClick={() => setExpandedId(isExpanded ? null : entry.id)}
                          className={`rounded-lg border bg-card transition-shadow cursor-pointer ${
                            isExpanded ? 'border-border shadow-sm' : 'border-border hover:border-border hover:shadow-sm'
                          } group`}
                        >
                          <div className="flex items-center gap-3 px-4 py-3">
                            <span className={`shrink-0 flex h-7 w-7 items-center justify-center rounded ${meta.bg}`}>
                              <Icon className={`h-3.5 w-3.5 ${meta.color}`} />
                            </span>
                            <div className="flex-1 min-w-0">
                              <div className="flex items-center gap-2">
                                <span className="text-sm font-medium text-foreground truncate">
                                  {entry.content.length > 40 ? entry.content.slice(0, 40) + '…' : entry.content}
                                </span>
                                <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium ${meta.bg} ${meta.color}`}>
                                  {meta.label}
                                </span>
                                {isRevealed ? (
                                  <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium bg-tag-green text-tag-green-foreground">
                                    第{entry.revealed_chapter}章回收
                                  </span>
                                ) : (
                                  <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium bg-tag-blue text-tag-blue-foreground">
                                    未回收
                                  </span>
                                )}
                              </div>
                              <div className="flex items-center gap-2 mt-0.5 text-[11px] text-muted-foreground">
                                <span>种于第 {entry.planted_chapter} 章</span>
                                {entry.related_truth && (
                                  <span className="text-muted-foreground">· 有真相</span>
                                )}
                              </div>
                            </div>
                            {/* Quick actions */}
                            <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity" onClick={e => e.stopPropagation()}>
                              {!isRevealed && (
                                <button
                                  onClick={() => handleQuickReveal(entry)}
                                  className="p-1 rounded text-muted-foreground hover:text-tag-green-foreground hover:bg-tag-green/20 transition-colors"
                                  title="标记已回收"
                                >
                                  <span className="text-[11px]">✓</span>
                                </button>
                              )}
                              <button
                                onClick={() => { setExpandedId(null); openEdit(entry) }}
                                className="p-1 rounded text-muted-foreground hover:text-foreground hover:bg-secondary transition-colors"
                                title="编辑"
                              >
                                <Pencil className="h-3.5 w-3.5" />
                              </button>
                              <button
                                onClick={() => handleDelete(entry.id)}
                                className="p-1 rounded text-muted-foreground hover:text-destructive hover:bg-destructive/10 transition-colors"
                                title="删除"
                              >
                                <Trash2 className="h-3.5 w-3.5" />
                              </button>
                            </div>
                            <span className={`text-[10px] transition-transform ${isExpanded ? 'rotate-180' : ''}`}>▼</span>
                          </div>

                          {isExpanded && (
                            <div className="border-t border-border px-4 py-3 space-y-3">
                              <div>
                                <p className="text-xs text-muted-foreground mb-1">内容</p>
                                <p className="text-xs text-muted-foreground leading-relaxed whitespace-pre-wrap">{entry.content}</p>
                              </div>
                              {entry.related_truth && (
                                <div>
                                  <p className="text-xs text-muted-foreground mb-1">作者视角真相</p>
                                  <p className="text-xs text-muted-foreground leading-relaxed whitespace-pre-wrap">{entry.related_truth}</p>
                                </div>
                              )}
                              {entry.revealed_chapter > 0 && (
                                <div>
                                  <p className="text-xs text-muted-foreground mb-1">回收章节</p>
                                  <p className="text-xs text-muted-foreground">第 {entry.revealed_chapter} 章</p>
                                </div>
                              )}
                            </div>
                          )}
                        </div>
                      )
                    })}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </main>
  )
}
