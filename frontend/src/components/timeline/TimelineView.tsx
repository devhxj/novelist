import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertTriangle, BookOpen, Flag, Lightbulb, Target } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { timeline } from '@/hooks/useApp'

interface Props { novelId: number }

type Tab = 'next' | 'near' | 'far'
type Filter = 'all' | 'pending' | 'resolved' | 'abandoned'

const ENTRY_WINDOW = 20

const FILTERS: { key: Filter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'pending', label: '进行中' },
  { key: 'resolved', label: '已回收' },
  { key: 'abandoned', label: '已废弃' },
]

function safeJson<T>(json: string, fallback: T): T {
  try { return JSON.parse(json) }
  catch { return fallback }
}

function importStars(v: number) {
  return '★'.repeat(Math.max(0, Math.min(5, v)))
}

export default function TimelineView({ novelId }: Props) {
  const app = useApp()

  const [plans, setPlans] = useState<timeline.ChapterPlan[]>([])
  const [entries, setEntries] = useState<timeline.TimelineEntry[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [planTab, setPlanTab] = useState<Tab>('next')
  const [filter, setFilter] = useState<Filter>('all')
  const [expandedId, setExpandedId] = useState<number | null>(null)
  const [windowCenter, setWindowCenter] = useState(0)

  const load = useCallback(async () => {
    if (!novelId) { setPlans([]); setEntries([]); return }
    setLoading(true)
    setError(null)
    try {
      const [planList, entryList, maxCh] = await Promise.all([
        app.GetChapterPlans(novelId),
        app.GetTimelineEntries(novelId, 0, 0),
        app.GetMaxChapterNumber(novelId),
      ])
      setPlans(planList ?? [])
      setEntries(entryList ?? [])

      setWindowCenter(Math.max(1, maxCh))
    } catch (err) {
      setError(err instanceof Error ? err.message : '加载失败')
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => { load() }, [load])

  const windowFrom = Math.max(1, windowCenter - ENTRY_WINDOW)
  const windowTo = windowCenter + ENTRY_WINDOW

  const planMap = useMemo(() => {
    const map: Record<string, string> = { next: '', near: '', far: '' }
    for (const p of plans) {
      if (p.content) map[p.scope] = p.content
    }
    return map
  }, [plans])

  const planLabels: Record<Tab, string> = {
    next: '下一章',
    near: '近期',
    far: '远期',
  }

  const filteredEntries = useMemo(() => {
    if (filter === 'all') return entries
    return entries.filter(e => e.status === filter)
  }, [entries, filter])

  const grouped = useMemo(() => {
    const map = new Map<number, timeline.TimelineEntry[]>()
    for (const e of filteredEntries) {
      const ch = e.target_chapter
      if (!map.has(ch)) map.set(ch, [])
      map.get(ch)!.push(e)
    }
    return [...map.entries()].sort(([a], [b]) => a - b)
  }, [filteredEntries])

  const visibleChapters = grouped.filter(([ch]) => ch >= windowFrom && ch <= windowTo)
  const beforeChapters = grouped.filter(([ch]) => ch < windowFrom)
  const afterChapters = grouped.filter(([ch]) => ch > windowTo)

  const beforeCount = beforeChapters.reduce((s, [, items]) => s + items.length, 0)
  const afterCount = afterChapters.reduce((s, [, items]) => s + items.length, 0)

  const minChapter = grouped.length > 0 ? grouped[0][0] : 0
  const maxChapter = grouped.length > 0 ? grouped[grouped.length - 1][0] : 0

  function shiftWindow(delta: number) {
    setWindowCenter(prev => Math.max(ENTRY_WINDOW + 1, Math.min(maxChapter - ENTRY_WINDOW, prev + delta)))
  }

  const statusStyle = (status: string) => {
    switch (status) {
      case 'pending':
        return { bg: 'bg-blue-50', text: 'text-blue-600', label: '进行中' }
      case 'resolved':
        return { bg: 'bg-green-50', text: 'text-green-600', label: '已回收' }
      case 'abandoned':
        return { bg: 'bg-slate-100', text: 'text-slate-400', label: '已废弃' }
      default:
        return { bg: 'bg-slate-50', text: 'text-slate-500', label: status }
    }
  }

  const catStyle = (category: string) => {
    switch (category) {
      case 'foreshadowing':
        return { icon: Target, color: 'text-amber-500', bg: 'bg-amber-50', label: '伏笔' }
      case 'user_directive':
        return { icon: Lightbulb, color: 'text-violet-500', bg: 'bg-violet-50', label: '用户指令' }
      default:
        return { icon: Flag, color: 'text-slate-500', bg: 'bg-slate-50', label: category }
    }
  }

  return (
    <main className="relative flex-1 min-w-0 overflow-y-auto bg-[#fafbfc]">
      {loading ? (
        <div className="flex h-full items-center justify-center text-sm text-slate-500">加载中...</div>
      ) : error ? (
        <div className="flex h-full items-center justify-center text-sm text-rose-500">{error}</div>
      ) : (
        <div className="max-w-3xl mx-auto px-5 py-6 space-y-6">
          {/* Chapter Plans */}
          <section>
            <div className="flex items-center gap-2 mb-3">
              <BookOpen className="h-4 w-4 text-emerald-500" />
              <h2 className="text-sm font-semibold text-slate-800">章节计划</h2>
            </div>
            <div className="flex gap-1 mb-3">
              {(['next', 'near', 'far'] as Tab[]).map(tab => (
                <button
                  key={tab}
                  onClick={() => setPlanTab(tab)}
                  className={`
                    px-3 py-1.5 rounded text-xs font-medium transition-colors
                    ${planTab === tab
                      ? 'bg-white border border-slate-200 text-slate-800 shadow-sm'
                      : 'text-slate-500 hover:text-slate-700 hover:bg-white/60'
                    }
                  `}
                >
                  {planLabels[tab]}
                </button>
              ))}
            </div>
            <div className="rounded-lg border border-slate-200 bg-white p-4 min-h-[80px]">
              {planMap[planTab] ? (
                <p className="text-sm text-slate-600 leading-relaxed whitespace-pre-wrap">{planMap[planTab]}</p>
              ) : (
                <p className="text-sm text-slate-400">暂无{planLabels[planTab]}计划</p>
              )}
            </div>
          </section>

          {/* Timeline Entries */}
          <section>
            <div className="flex items-center justify-between mb-3">
              <div className="flex items-center gap-2">
                <AlertTriangle className="h-4 w-4 text-amber-500" />
                <h2 className="text-sm font-semibold text-slate-800">
                  伏笔与指令
                  <span className="ml-2 text-xs font-normal text-slate-400">{entries.length} 条</span>
                </h2>
              </div>
              <div className="flex items-center gap-2">
                <span className="text-[11px] text-slate-400">
                  第 {windowFrom}-{windowTo} 章 · 共 {minChapter}-{maxChapter} 章
                </span>
                <button
                  onClick={load}
                  className="text-xs text-slate-400 hover:text-slate-600 transition-colors"
                >
                  刷新
                </button>
              </div>
            </div>

            {/* Filter tabs */}
            <div className="flex gap-1 mb-4">
              {FILTERS.map(f => (
                <button
                  key={f.key}
                  onClick={() => setFilter(f.key)}
                  className={`
                    px-3 py-1 rounded text-xs transition-colors
                    ${filter === f.key
                      ? 'bg-white border border-slate-200 text-slate-700 shadow-sm'
                      : 'text-slate-500 hover:text-slate-700'
                    }
                  `}
                >
                  {f.label}
                  {f.key !== 'all' && (
                    <span className="ml-1 text-slate-400">
                      ({entries.filter(e => e.status === f.key).length})
                    </span>
                  )}
                </button>
              ))}
            </div>

            {grouped.length === 0 ? (
              <div className="text-center py-12">
                <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-slate-100 text-slate-400">
                  <Target className="h-5 w-5" />
                </div>
                <p className="mt-2 text-sm text-slate-500">
                  {filter === 'all' ? '暂无伏笔或用户指令' : '没有匹配的条目'}
                </p>
              </div>
            ) : (
              <div className="space-y-6">
                {/* Collapsed before */}
                {beforeCount > 0 && (
                  <button
                    onClick={() => shiftWindow(-ENTRY_WINDOW)}
                    className="w-full rounded-lg border border-dashed border-slate-200 bg-white/60 px-4 py-2.5 text-xs text-slate-500 hover:bg-white hover:border-slate-300 hover:text-slate-700 transition-colors"
                  >
                    ← 第 {beforeChapters[0]?.[0]}-{beforeChapters[beforeChapters.length - 1]?.[0]} 章 · {beforeCount} 条
                  </button>
                )}

                {/* Visible entries */}
                {visibleChapters.map(([ch, items]) => (
                  <div key={ch}>
                    <div className="flex items-center gap-1.5 mb-2">
                      <span className="text-xs font-medium text-slate-400">第 {ch} 章</span>
                      <span className="text-[10px] text-slate-300">{items.length} 条</span>
                    </div>
                    <div className="space-y-2">
                      {items.map(entry => {
                        const s = statusStyle(entry.status)
                        const c = catStyle(entry.category)
                        const CatIcon = c.icon
                        const isExpanded = expandedId === entry.id
                        const detail = safeJson<Record<string, any>>(entry.detail_json, {})
                        const detailKeys = Object.keys(detail).filter(k => k.trim())
                        const hasContent = entry.content || detailKeys.length > 0

                        return (
                          <div
                            key={entry.id}
                            onClick={() => setExpandedId(isExpanded ? null : entry.id)}
                            className={`
                              rounded-lg border bg-white transition-shadow cursor-pointer
                              ${isExpanded ? 'border-slate-300 shadow-sm' : 'border-slate-100 hover:border-slate-200 hover:shadow-sm'}
                            `}
                          >
                            <div className="flex items-center gap-3 px-4 py-3">
                              <span className={`shrink-0 flex h-7 w-7 items-center justify-center rounded ${c.bg}`}>
                                <CatIcon className={`h-3.5 w-3.5 ${c.color}`} />
                              </span>
                              <div className="flex-1 min-w-0">
                                <div className="flex items-center gap-2">
                                  <span className="text-sm font-medium text-slate-800 truncate">{entry.title}</span>
                                  <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium ${s.bg} ${s.text}`}>
                                    {s.label}
                                  </span>
                                </div>
                                <div className="flex items-center gap-2 mt-0.5 text-[11px] text-slate-400">
                                  <span className="text-amber-400 text-[10px]">{importStars(entry.importance)}</span>
                                  <span>目标第 {entry.target_chapter} 章</span>
                                  {entry.source_chapter_id > 0 && (
                                    <span>· 埋于第 {entry.source_chapter_id} 章</span>
                                  )}
                                  {entry.resolved_chapter_id > 0 && (
                                    <span className="text-green-500">· 回收于第 {entry.resolved_chapter_id} 章</span>
                                  )}
                                  <span className="text-slate-300">· {entry.source === 'ai' ? 'AI' : '用户'}</span>
                                </div>
                              </div>
                              <span className={`text-[10px] transition-transform ${isExpanded ? 'rotate-180' : ''}`}>▼</span>
                            </div>

                            {isExpanded && hasContent && (
                              <div className="border-t border-slate-100 px-4 py-3 space-y-3">
                                {entry.content && (
                                  <div>
                                    <p className="text-xs text-slate-400 mb-1">详情</p>
                                    <p className="text-xs text-slate-600 leading-relaxed whitespace-pre-wrap">{entry.content}</p>
                                  </div>
                                )}
                                {detailKeys.length > 0 && (
                                  <div className="space-y-1">
                                    {detailKeys.map(k => {
                                      const v = detail[k]
                                      const display = typeof v === 'string' ? v : Array.isArray(v) ? v.join('、') : String(v)
                                      return (
                                        <div key={k} className="flex gap-2 text-xs">
                                          <span className="text-slate-400 shrink-0">{k}</span>
                                          <span className="text-slate-600">{display}</span>
                                        </div>
                                      )
                                    })}
                                  </div>
                                )}
                              </div>
                            )}
                            {isExpanded && !hasContent && (
                              <div className="border-t border-slate-100 px-4 py-3">
                                <p className="text-xs text-slate-400">暂无详细内容</p>
                              </div>
                            )}
                          </div>
                        )
                      })}
                    </div>
                  </div>
                ))}

                {/* Collapsed after */}
                {afterCount > 0 && (
                  <button
                    onClick={() => shiftWindow(ENTRY_WINDOW)}
                    className="w-full rounded-lg border border-dashed border-slate-200 bg-white/60 px-4 py-2.5 text-xs text-slate-500 hover:bg-white hover:border-slate-300 hover:text-slate-700 transition-colors"
                  >
                    → 第 {afterChapters[0]?.[0]}-{afterChapters[afterChapters.length - 1]?.[0]} 章 · {afterCount} 条
                  </button>
                )}
              </div>
            )}
          </section>
        </div>
      )}
    </main>
  )
}
