import { useCallback, useEffect, useMemo, useState } from 'react'
import { GitBranch } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { storyarc } from '@/hooks/useApp'
import StoryArcGraph from '@/components/storyarc/StoryArcGraph'

interface Props { novelId: number; focusArcId?: number }

type ViewTab = 'list' | 'swimlane'

const PALETTE = [
  { fill: '#dbeafe', stroke: '#3b82f6', text: '#1d4ed8' },
  { fill: '#dcfce7', stroke: '#22c55e', text: '#166534' },
  { fill: '#fef3c7', stroke: '#f59e0b', text: '#92400e' },
  { fill: '#f3e8ff', stroke: '#a855f7', text: '#6b21a8' },
  { fill: '#ffe4e6', stroke: '#f43f5e', text: '#9f1239' },
  { fill: '#ccfbf1', stroke: '#14b8a6', text: '#115e59' },
  { fill: '#ffedd5', stroke: '#f97316', text: '#9a3412' },
]

type Filter = 'all' | 'pending' | 'completed' | 'abandoned'
const WINDOW = 20

const FILTERS: { key: Filter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'pending', label: '进行中' },
  { key: 'completed', label: '已完成' },
  { key: 'abandoned', label: '已废弃' },
]

export default function ArcListView({ novelId, focusArcId }: Props) {
  const app = useApp()

  const [arcs, setArcs] = useState<storyarc.StoryArc[]>([])
  const [allNodes, setAllNodes] = useState<storyarc.ArcNode[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [expandedId, setExpandedId] = useState<number | null>(null)
  const [windowCenter, setWindowCenter] = useState(0)
  const [filter, setFilter] = useState<Filter>('all')
  const [hiddenArcIds, setHiddenArcIds] = useState<Set<number>>(new Set())
  const [viewTab, setViewTab] = useState<ViewTab>('list')

  const load = useCallback(async () => {
    if (!novelId) { setArcs([]); setAllNodes([]); return }
    setLoading(true)
    setError(null)
    try {
      const [arcList, nodeList, maxCh] = await Promise.all([
        app.GetStoryArcs(novelId),
        app.GetArcNodes(novelId, 0, 0),
        app.GetMaxChapterNumber(novelId),
      ])
      setArcs(arcList ?? [])
      setAllNodes(nodeList ?? [])
      setWindowCenter(Math.max(1, maxCh))
    } catch (err) {
      setError(err instanceof Error ? err.message : '加载失败')
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => { load() }, [load])

  useEffect(() => {
    if (focusArcId && focusArcId > 0 && allNodes.length > 0) {
      const arcNodes = allNodes.filter(n => n.story_arc_id === focusArcId)
      if (arcNodes.length > 0) {
        const firstNode = arcNodes[0]
        setWindowCenter(firstNode.target_chapter || firstNode.actual_chapter || 1)
        setExpandedId(firstNode.id)
      }
    }
  }, [focusArcId, allNodes])

  const windowFrom = Math.max(1, windowCenter - WINDOW)
  const windowTo = windowCenter + WINDOW

  const activeArcIds = useMemo(() => {
    if (hiddenArcIds.size === 0) return new Set(arcs.map(a => a.id))
    return new Set(arcs.map(a => a.id).filter(id => !hiddenArcIds.has(id)))
  }, [arcs, hiddenArcIds])

  const filteredNodes = useMemo(() => {
    let nodes = allNodes.filter(n => activeArcIds.has(n.story_arc_id))
    if (filter !== 'all') nodes = nodes.filter(n => n.status === filter)
    return nodes
  }, [allNodes, activeArcIds, filter])

  const grouped = useMemo(() => {
    const map = new Map<number, storyarc.ArcNode[]>()
    for (const n of filteredNodes) {
      const ch = n.target_chapter
      if (!map.has(ch)) map.set(ch, [])
      map.get(ch)!.push(n)
    }
    return [...map.entries()].sort(([a], [b]) => a - b)
  }, [filteredNodes])

  const visibleChapters = grouped.filter(([ch]) => ch >= windowFrom && ch <= windowTo)
  const beforeChapters = grouped.filter(([ch]) => ch < windowFrom)
  const afterChapters = grouped.filter(([ch]) => ch > windowTo)

  const beforeCount = beforeChapters.reduce((s, [, items]) => s + items.length, 0)
  const afterCount = afterChapters.reduce((s, [, items]) => s + items.length, 0)

  const minChapter = grouped.length > 0 ? grouped[0][0] : 0
  const maxChapter = grouped.length > 0 ? grouped[grouped.length - 1][0] : 0

  function shiftWindow(delta: number) {
    setWindowCenter(prev => Math.max(WINDOW + 1, Math.min(maxChapter - WINDOW, prev + delta)))
  }

  function toggleArc(id: number) {
    setHiddenArcIds(prev => {
      const next = new Set(prev)
      if (next.has(id)) { next.delete(id) }
      else { next.add(id) }
      return next
    })
  }

  function showAllArcs() {
    setHiddenArcIds(new Set())
  }

  const nodeStatusStyle = (status: string) => {
    switch (status) {
      case 'completed':
        return { bg: 'bg-green-50', text: 'text-green-600', label: '已完成' }
      case 'abandoned':
        return { bg: 'bg-slate-100', text: 'text-slate-400', label: '已废弃' }
      default:
        return { bg: 'bg-blue-50', text: 'text-blue-600', label: '进行中' }
    }
  }

  const arcStatusTag = (status: string) => {
    switch (status) {
      case 'paused': return ' ⏸'
      case 'completed': return ' ✓'
      case 'abandoned': return ' ✗'
      default: return ''
    }
  }

  return (
    <main className="flex-1 min-w-0 flex flex-col overflow-hidden bg-[#fafbfc]">
      {/* Tab bar */}
      <div className="flex items-center gap-1 px-5 pt-4 pb-2 shrink-0">
        <button
          onClick={() => setViewTab('list')}
          className={`px-3 py-1.5 rounded text-xs font-medium transition-colors ${
            viewTab === 'list'
              ? 'bg-white border border-slate-200 text-slate-800 shadow-sm'
              : 'text-slate-500 hover:text-slate-700 hover:bg-white/60'
          }`}
        >
          列表
        </button>
        <button
          onClick={() => setViewTab('swimlane')}
          className={`px-3 py-1.5 rounded text-xs font-medium transition-colors ${
            viewTab === 'swimlane'
              ? 'bg-white border border-slate-200 text-slate-800 shadow-sm'
              : 'text-slate-500 hover:text-slate-700 hover:bg-white/60'
          }`}
        >
          泳道图
        </button>
      </div>

      {viewTab === 'swimlane' ? (
        <StoryArcGraph novelId={novelId} />
      ) : loading ? (
        <div className="flex h-full items-center justify-center text-sm text-slate-500">加载中...</div>
      ) : error ? (
        <div className="flex h-full items-center justify-center text-sm text-rose-500">{error}</div>
      ) : (
        <div className="flex-1 overflow-y-auto">
        <div className="max-w-3xl mx-auto px-5 py-6 space-y-6">
          {/* Header */}
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <GitBranch className="h-4 w-4 text-purple-500" />
              <h2 className="text-sm font-semibold text-slate-800">
                弧线节点
                <span className="ml-2 text-xs font-normal text-slate-400">{filteredNodes.length} 个</span>
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

          {/* Arc filter chips */}
          {arcs.length > 0 && (
            <div className="flex flex-wrap gap-1.5">
              <button
                onClick={showAllArcs}
                className={`px-3 py-1 rounded text-xs font-medium transition-colors ${
                  hiddenArcIds.size === 0
                    ? 'bg-white border border-slate-200 text-slate-700 shadow-sm'
                    : 'text-slate-500 hover:text-slate-700 hover:bg-white/60'
                }`}
              >
                全部
              </button>
              {arcs.map((arc, i) => {
                const c = PALETTE[i % PALETTE.length]
                const hidden = hiddenArcIds.has(arc.id)
                return (
                  <button
                    key={arc.id}
                    onClick={() => toggleArc(arc.id)}
                    className={`px-3 py-1 rounded text-xs font-medium transition-colors border ${
                      hidden
                        ? 'text-slate-400 border-transparent hover:text-slate-500 hover:bg-white/60'
                        : 'border-slate-200 shadow-sm text-slate-700'
                    }`}
                    style={hidden ? {} : { backgroundColor: c.fill, borderColor: c.stroke, color: c.text }}
                  >
                    {arc.name}{arcStatusTag(arc.status)}
                  </button>
                )
              })}
            </div>
          )}

          {/* Status filter */}
          <div className="flex gap-1">
            {FILTERS.map(f => (
              <button
                key={f.key}
                onClick={() => setFilter(f.key)}
                className={`px-3 py-1 rounded text-xs transition-colors ${
                  filter === f.key
                    ? 'bg-white border border-slate-200 text-slate-700 shadow-sm'
                    : 'text-slate-500 hover:text-slate-700'
                }`}
              >
                {f.label}
                {f.key !== 'all' && (
                  <span className="ml-1 text-slate-400">
                    ({allNodes.filter(n => activeArcIds.has(n.story_arc_id) && n.status === f.key).length})
                  </span>
                )}
              </button>
            ))}
          </div>

          {/* Node list */}
          {grouped.length === 0 ? (
            <div className="text-center py-12">
              <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-purple-50 text-purple-400">
                <GitBranch className="h-5 w-5" />
              </div>
              <p className="mt-2 text-sm text-slate-500">
                {arcs.length === 0 ? '暂无叙事弧线' : '没有匹配的节点'}
              </p>
            </div>
          ) : (
            <div className="space-y-6">
              {beforeCount > 0 && (
                <button
                  onClick={() => shiftWindow(-WINDOW)}
                  className="w-full rounded-lg border border-dashed border-slate-200 bg-white/60 px-4 py-2.5 text-xs text-slate-500 hover:bg-white hover:border-slate-300 hover:text-slate-700 transition-colors"
                >
                  ← 第 {beforeChapters[0]?.[0]}-{beforeChapters[beforeChapters.length - 1]?.[0]} 章 · {beforeCount} 个节点
                </button>
              )}

              {visibleChapters.map(([ch, items]) => (
                <div key={ch}>
                  <div className="flex items-center gap-1.5 mb-2">
                    <span className="text-xs font-medium text-slate-400">第 {ch} 章</span>
                    <span className="text-[10px] text-slate-300">{items.length} 个节点</span>
                  </div>
                  <div className="space-y-2">
                    {items.map(node => {
                      const s = nodeStatusStyle(node.status)
                      const arcIdx = arcs.findIndex(a => a.id === node.story_arc_id)
                      const c = PALETTE[arcIdx >= 0 ? arcIdx % PALETTE.length : 0]
                      const arc = arcIdx >= 0 ? arcs[arcIdx] : null
                      const isExpanded = expandedId === node.id
                      const desc = node.description?.trim() || ''
                      const hasContent = desc.length > 0

                      return (
                        <div
                          key={node.id}
                          onClick={() => setExpandedId(isExpanded ? null : node.id)}
                          className={`rounded-lg border bg-white transition-shadow cursor-pointer ${
                            isExpanded ? 'border-slate-300 shadow-sm' : 'border-slate-100 hover:border-slate-200 hover:shadow-sm'
                          }`}
                        >
                          <div className="flex items-center gap-3 px-4 py-3">
                            {/* Arc color indicator */}
                            <span
                              className="shrink-0 h-3 w-3 rounded-full"
                              style={{ backgroundColor: c.stroke }}
                            />
                            <div className="flex-1 min-w-0">
                              <div className="flex items-center gap-2">
                                <span className="text-sm font-medium text-slate-800 truncate">{node.title}</span>
                                <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium ${s.bg} ${s.text}`}>
                                  {s.label}
                                </span>
                                {arc && (
                                  <span
                                    className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium"
                                    style={{ backgroundColor: c.fill, color: c.text }}
                                  >
                                    {arc.name}
                                  </span>
                                )}
                              </div>
                              <div className="flex items-center gap-2 mt-0.5 text-[11px] text-slate-400">
                                <span>目标第 {node.target_chapter} 章</span>
                                {node.actual_chapter > 0 && (
                                  <span className="text-green-500">· 实际第 {node.actual_chapter} 章</span>
                                )}
                                {arc && (
                                  <span className="text-slate-300">· {arc.arc_type}</span>
                                )}
                              </div>
                            </div>
                            <span className={`text-[10px] transition-transform ${isExpanded ? 'rotate-180' : ''}`}>▼</span>
                          </div>

                          {isExpanded && hasContent && (
                            <div className="border-t border-slate-100 px-4 py-3">
                              <p className="text-xs text-slate-500 leading-relaxed whitespace-pre-wrap">{desc}</p>
                            </div>
                          )}
                          {isExpanded && !hasContent && (
                            <div className="border-t border-slate-100 px-4 py-3">
                              <p className="text-xs text-slate-400">暂无详细描述</p>
                            </div>
                          )}
                        </div>
                      )
                    })}
                  </div>
                </div>
              ))}

              {afterCount > 0 && (
                <button
                  onClick={() => shiftWindow(WINDOW)}
                  className="w-full rounded-lg border border-dashed border-slate-200 bg-white/60 px-4 py-2.5 text-xs text-slate-500 hover:bg-white hover:border-slate-300 hover:text-slate-700 transition-colors"
                >
                  → 第 {afterChapters[0]?.[0]}-{afterChapters[afterChapters.length - 1]?.[0]} 章 · {afterCount} 个节点
                </button>
              )}
            </div>
          )}
        </div>
        </div>
      )}
    </main>
  )
}
