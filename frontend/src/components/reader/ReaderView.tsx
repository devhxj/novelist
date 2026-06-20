import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertTriangle, BookOpen, Clock } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reader } from '@/hooks/useApp'

interface Props { novelId: number }

type TypeFilter = 'all' | 'known' | 'suspense' | 'misconception'
type StatusFilter = 'all' | 'unrevealed' | 'revealed'

const WINDOW = 20

const TYPE_FILTERS: { key: TypeFilter; label: string; icon: typeof BookOpen; color: string }[] = [
  { key: 'all', label: '全部', icon: BookOpen, color: 'text-slate-500' },
  { key: 'known', label: '已知', icon: BookOpen, color: 'text-emerald-500' },
  { key: 'suspense', label: '悬念', icon: Clock, color: 'text-amber-500' },
  { key: 'misconception', label: '误解', icon: AlertTriangle, color: 'text-rose-500' },
]

const STATUS_FILTERS: { key: StatusFilter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'unrevealed', label: '未回收' },
  { key: 'revealed', label: '已回收' },
]

function typeMeta(type: string) {
  switch (type) {
    case 'known':
      return { icon: BookOpen, color: 'text-emerald-500', bg: 'bg-emerald-50', label: '已知' }
    case 'suspense':
      return { icon: Clock, color: 'text-amber-500', bg: 'bg-amber-50', label: '悬念' }
    case 'misconception':
      return { icon: AlertTriangle, color: 'text-rose-500', bg: 'bg-rose-50', label: '误解' }
    default:
      return { icon: BookOpen, color: 'text-slate-500', bg: 'bg-slate-50', label: type }
  }
}

export default function ReaderView({ novelId }: Props) {
  const app = useApp()

  const [entries, setEntries] = useState<reader.ReaderPerspective[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [typeFilter, setTypeFilter] = useState<TypeFilter>('all')
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all')
  const [expandedId, setExpandedId] = useState<number | null>(null)
  const [windowCenter, setWindowCenter] = useState(0)

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

  return (
    <main className="flex-1 min-w-0 overflow-y-auto overscroll-contain bg-[#fafbfc]">
      {loading ? (
        <div className="flex h-full items-center justify-center text-sm text-slate-500">加载中...</div>
      ) : error ? (
        <div className="flex h-full items-center justify-center text-sm text-rose-500">{error}</div>
      ) : (
        <div className="max-w-3xl mx-auto px-5 py-6 space-y-5">
          {/* Header */}
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <h2 className="text-sm font-semibold text-slate-800">
                读者视角
                <span className="ml-2 text-xs font-normal text-slate-400">{filtered.length} 条</span>
              </h2>
            </div>
            <div className="flex items-center gap-2">
              <span className="text-[11px] text-slate-400">
                第 {windowFrom}-{windowTo} 章 · 共 {minChapter}-{maxChapter} 章
              </span>
              <button onClick={load} className="text-xs text-slate-400 hover:text-slate-600 transition-colors">
                刷新
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
                      ? 'bg-white border border-slate-200 text-slate-700 shadow-sm'
                      : 'text-slate-500 hover:text-slate-700'
                  }`}
                >
                  <Icon className={`h-3 w-3 ${typeFilter === f.key ? f.color : ''}`} />
                  {f.label}
                  {f.key !== 'all' && (
                    <span className="text-slate-400">({entries.filter(e => e.type === f.key).length})</span>
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
                    ? 'bg-white border border-slate-200 text-slate-700 shadow-sm'
                    : 'text-slate-500 hover:text-slate-700'
                }`}
              >
                {f.label}
                {f.key === 'unrevealed' && (
                  <span className="ml-1 text-slate-400">({entries.filter(e => e.revealed_chapter === 0).length})</span>
                )}
                {f.key === 'revealed' && (
                  <span className="ml-1 text-slate-400">({entries.filter(e => e.revealed_chapter > 0).length})</span>
                )}
              </button>
            ))}
          </div>

          {/* Entries */}
          {groupedDesc.length === 0 ? (
            <div className="text-center py-12">
              <p className="text-sm text-slate-500">
                {entries.length === 0 ? '暂无读者认知数据' : '没有匹配的条目'}
              </p>
            </div>
          ) : (
            <div className="space-y-6">
              {beforeCount > 0 && (
                <button
                  onClick={() => shiftWindow(-WINDOW)}
                  className="w-full rounded-lg border border-dashed border-slate-200 bg-white/60 px-4 py-2.5 text-xs text-slate-500 hover:bg-white hover:border-slate-300 hover:text-slate-700 transition-colors"
                >
                  ← 第 {beforeChapters[beforeChapters.length - 1]?.[0]}-{beforeChapters[0]?.[0]} 章 · {beforeCount} 条
                </button>
              )}

              {visibleChapters.map(([ch, items]) => (
                <div key={ch}>
                  <div className="flex items-center gap-1.5 mb-2">
                    <span className="text-xs font-medium text-slate-400">第 {ch} 章</span>
                    <span className="text-[10px] text-slate-300">{items.length} 条</span>
                  </div>
                  <div className="space-y-2">
                    {items.map(entry => {
                      const meta = typeMeta(entry.type)
                      const Icon = meta.icon
                      const isExpanded = expandedId === entry.id
                      const isRevealed = entry.revealed_chapter > 0

                      return (
                        <div
                          key={entry.id}
                          onClick={() => setExpandedId(isExpanded ? null : entry.id)}
                          className={`rounded-lg border bg-white transition-shadow cursor-pointer ${
                            isExpanded ? 'border-slate-300 shadow-sm' : 'border-slate-100 hover:border-slate-200 hover:shadow-sm'
                          }`}
                        >
                          <div className="flex items-center gap-3 px-4 py-3">
                            <span className={`shrink-0 flex h-7 w-7 items-center justify-center rounded ${meta.bg}`}>
                              <Icon className={`h-3.5 w-3.5 ${meta.color}`} />
                            </span>
                            <div className="flex-1 min-w-0">
                              <div className="flex items-center gap-2">
                                <span className="text-sm font-medium text-slate-800 truncate">
                                  {entry.content.length > 40 ? entry.content.slice(0, 40) + '…' : entry.content}
                                </span>
                                <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium ${meta.bg} ${meta.color}`}>
                                  {meta.label}
                                </span>
                                {isRevealed ? (
                                  <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium bg-green-50 text-green-600">
                                    第{entry.revealed_chapter}章回收
                                  </span>
                                ) : (
                                  <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium bg-blue-50 text-blue-600">
                                    未回收
                                  </span>
                                )}
                              </div>
                              <div className="flex items-center gap-2 mt-0.5 text-[11px] text-slate-400">
                                <span>种于第 {entry.planted_chapter} 章</span>
                                {entry.related_truth && (
                                  <span className="text-slate-300">· 有真相</span>
                                )}
                              </div>
                            </div>
                            <span className={`text-[10px] transition-transform ${isExpanded ? 'rotate-180' : ''}`}>▼</span>
                          </div>

                          {isExpanded && (
                            <div className="border-t border-slate-100 px-4 py-3 space-y-3">
                              <div>
                                <p className="text-xs text-slate-400 mb-1">内容</p>
                                <p className="text-xs text-slate-600 leading-relaxed whitespace-pre-wrap">{entry.content}</p>
                              </div>
                              {entry.related_truth && (
                                <div>
                                  <p className="text-xs text-slate-400 mb-1">作者视角真相</p>
                                  <p className="text-xs text-slate-600 leading-relaxed whitespace-pre-wrap">{entry.related_truth}</p>
                                </div>
                              )}
                              {entry.revealed_chapter > 0 && (
                                <div>
                                  <p className="text-xs text-slate-400 mb-1">回收章节</p>
                                  <p className="text-xs text-slate-600">第 {entry.revealed_chapter} 章</p>
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
