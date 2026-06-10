import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Graph } from '@antv/g6'
import { ArrowLeft, ArrowRight, GitBranch, LocateFixed, RefreshCw, X } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { storyarc } from '@/hooks/useApp'

interface Props { novelId: number }

const PALETTE = [
  { fill: '#dbeafe', stroke: '#3b82f6', text: '#1d4ed8', edge: '#60a5fa' },
  { fill: '#dcfce7', stroke: '#22c55e', text: '#166534', edge: '#4ade80' },
  { fill: '#fef3c7', stroke: '#f59e0b', text: '#92400e', edge: '#fbbf24' },
  { fill: '#f3e8ff', stroke: '#a855f7', text: '#6b21a8', edge: '#c084fc' },
  { fill: '#ffe4e6', stroke: '#f43f5e', text: '#9f1239', edge: '#fb7185' },
  { fill: '#ccfbf1', stroke: '#14b8a6', text: '#115e59', edge: '#2dd4bf' },
  { fill: '#ffedd5', stroke: '#f97316', text: '#9a3412', edge: '#fb923c' },
]

const CH_W = 90
const LANE_H = 80
const LEFT_MARGIN = 120
const NODE_W = 88
const NODE_H = 30
const WINDOW = 30

function nid(id: number) { return `an-${id}` }
function aid(id: number) { return `arc-${id}` }
function eid(a: number, b: number) { return `e-${a}-${b}` }

function centerOnChapter(graph: Graph, containerW: number, containerH: number, ch: number, wf: number, laneCount: number) {
  const cx = LEFT_MARGIN + (ch - wf + 0.5) * CH_W
  const cy = (laneCount * LANE_H) / 2 + 30
  // translateTo moves canvas origin to (tx, ty) in viewport coordinates.
  // To center canvas point (cx, cy): tx = vw/2 - cx, ty = vh/2 - cy
  graph.translateTo([containerW / 2 - cx, containerH / 2 - cy], false)
}

export default function StoryArcGraph({ novelId }: Props) {
  const app = useApp()
  const containerRef = useRef<HTMLDivElement>(null)
  const graphRef = useRef<Graph | null>(null)

  const [arcs, setArcs] = useState<storyarc.StoryArc[]>([])
  const [allNodes, setAllNodes] = useState<storyarc.ArcNode[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selectedNode, setSelectedNode] = useState<storyarc.ArcNode | null>(null)
  const [selectedArc, setSelectedArc] = useState<storyarc.StoryArc | null>(null)
  const [expanded, setExpanded] = useState(false)
  const [windowCenter, setWindowCenter] = useState(0)
  const [edgeCounts, setEdgeCounts] = useState({ left: 0, right: 0 })

  const windowFrom = useMemo(() => Math.max(1, windowCenter - WINDOW), [windowCenter])
  const windowTo = useMemo(() => windowCenter + WINDOW, [windowCenter])
  const windowFromRef = useRef(windowFrom)
  const windowToRef = useRef(windowTo)
  windowFromRef.current = windowFrom
  windowToRef.current = windowTo

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
      const nodes = nodeList ?? []
      setAllNodes(nodes)

      setWindowCenter(Math.max(1, maxCh))
    } catch (err) {
      setError(err instanceof Error ? err.message : '加载失败')
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => { load() }, [load])

  const nodesByArc = useMemo(() => {
    const map = new Map<number, storyarc.ArcNode[]>()
    for (const n of allNodes) {
      if (!map.has(n.story_arc_id)) map.set(n.story_arc_id, [])
      map.get(n.story_arc_id)!.push(n)
    }
    for (const [, ns] of map) {
      ns.sort((a, b) => a.target_chapter - b.target_chapter || a.id - b.id)
    }
    return map
  }, [allNodes])

  const graphData = useMemo(() => {
    if (arcs.length === 0) return { nodes: [], edges: [] }

    const visibleNodes = allNodes.filter(
      n => n.target_chapter >= windowFrom && n.target_chapter <= windowTo
    )

    const gNodes: any[] = []
    const gEdges: any[] = []

    // Arc lane labels
    arcs.forEach((arc, i) => {
      const color = PALETTE[i % PALETTE.length]
      const statusSuffix = (() => {
        switch (arc.status) {
          case 'paused': return ' ⏸'
          case 'completed': return ' ✓'
          case 'abandoned': return ' ✗'
          default: return ''
        }
      })()
      const dim = arc.status === 'abandoned'
      gNodes.push({
        id: aid(arc.id),
        type: 'rect',
        style: {
          size: [LEFT_MARGIN - 12, NODE_H],
          x: LEFT_MARGIN / 2,
          y: i * LANE_H + LANE_H / 2,
          fill: dim ? '#f1f5f9' : color.fill,
          stroke: dim ? '#cbd5e1' : color.stroke,
          radius: 8,
          lineWidth: 2,
          labelText: arc.name + statusSuffix,
          labelFontSize: 12,
          labelFontWeight: 600,
          labelFill: dim ? '#94a3b8' : color.text,
          labelPlacement: 'center' as const,
          cursor: 'pointer',
        },
        data: { arc },
      })
    })

    // Chapter ruler marks (every 5 chapters)
    for (let ch = Math.floor(windowFrom / 5) * 5; ch <= windowTo; ch += 5) {
      if (ch < 1) continue
      const x = LEFT_MARGIN + (ch - windowFrom) * CH_W
      gNodes.push({
        id: `ch-${ch}`,
        type: 'rect',
        style: {
          size: [34, 22],
          x,
          y: 10,
          fill: '#e2e8f0',
          stroke: '#cbd5e1',
          radius: 4,
          lineWidth: 1,
          labelText: String(ch),
          labelFontSize: 12,
          labelFontWeight: 600,
          labelFill: '#475569',
          labelPlacement: 'center' as const,
        },
        data: {},
      })
    }

    // Arc nodes
    for (const n of visibleNodes) {
      const arcIdx = arcs.findIndex(a => a.id === n.story_arc_id)
      if (arcIdx < 0) continue
      const color = PALETTE[arcIdx % PALETTE.length]
      const x = LEFT_MARGIN + (n.target_chapter - windowFrom + 0.5) * CH_W
      const y = arcIdx * LANE_H + LANE_H / 2

      let fill = color.fill
      let strokeColor = color.stroke
      let textColor = color.text
      let opacity = 1
      let dash: number[] | undefined

      if (n.status === 'pending') {
        fill = '#ffffff'
      } else if (n.status === 'abandoned') {
        fill = '#f8fafc'
        strokeColor = '#cbd5e1'
        textColor = '#94a3b8'
        opacity = 0.55
        dash = [3, 3]
      }

      const label = n.title.length > 5 ? n.title.slice(0, 5) + '…' : n.title

      gNodes.push({
        id: nid(n.id),
        type: 'rect',
        style: {
          size: [NODE_W, NODE_H],
          x,
          y,
          fill,
          stroke: strokeColor,
          radius: NODE_H / 2,
          lineWidth: 2,
          lineDash: dash,
          opacity,
          labelText: label,
          labelFontSize: 11,
          labelFontWeight: 500,
          labelFill: textColor,
          labelPlacement: 'center' as const,
          cursor: 'pointer',
        },
        data: { arcNode: n, arcIdx },
      })
    }

    // Edges
    for (const arc of arcs) {
      const ns = nodesByArc.get(arc.id)
      if (!ns || ns.length < 2) continue
      const color = PALETTE[arcs.findIndex(a => a.id === arc.id) % PALETTE.length]

      for (let i = 0; i < ns.length - 1; i++) {
        const src = ns[i]
        const tgt = ns[i + 1]
        const srcVis = src.target_chapter >= windowFrom && src.target_chapter <= windowTo
        const tgtVis = tgt.target_chapter >= windowFrom && tgt.target_chapter <= windowTo
        if (!srcVis || !tgtVis) continue

        let stroke = color.edge
        let lineDash: number[] | undefined
        let opacity = 1
        let arrow = false

        if (src.status === 'abandoned' || tgt.status === 'abandoned') {
          stroke = '#cbd5e1'
          lineDash = [4, 4]
          opacity = 0.4
        } else if (src.status === 'pending' && tgt.status === 'pending') {
          lineDash = [4, 3]
          opacity = 0.5
        } else if (src.status === 'completed' && tgt.status === 'pending') {
          arrow = true
        }

        gEdges.push({
          id: eid(src.id, tgt.id),
          source: nid(src.id),
          target: nid(tgt.id),
          type: 'line',
          style: {
            stroke,
            lineWidth: 2,
            lineDash,
            opacity,
            endArrow: arrow,
            endArrowSize: arrow ? 8 : 0,
          },
        })
      }
    }

    return { nodes: gNodes, edges: gEdges }
  }, [arcs, allNodes, windowFrom, windowTo, nodesByArc])

  // Create G6 graph once on mount
  useEffect(() => {
    const container = containerRef.current
    if (!container || loading || arcs.length === 0) return

    const graph = new Graph({
      container,
      data: graphData,
      background: '#fafbfc',
      animation: false,
      node: {
        type: 'rect',
        style: {
          radius: NODE_H / 2,
          lineWidth: 2,
          labelPlacement: 'center' as const,
          labelOffsetY: 0,
        },
      },
      edge: {
        type: 'line',
      },
      behaviors: [
        'drag-canvas',
        'zoom-canvas',
        'optimize-viewport-transform',
      ],
    })

    graphRef.current = graph
    graph.render().then(() => {
      centerOnChapter(graph, container.clientWidth, container.clientHeight, windowCenter, windowFrom, arcs.length)
    })

    graph.on('node:click', (event: any) => {
      const rawId = event.target?.id || ''
      const arcMatch = arcs.find(a => rawId === aid(a.id))
      if (arcMatch) {
        setSelectedArc(prev => prev?.id === arcMatch.id ? null : arcMatch)
        setSelectedNode(null)
        return
      }
      if (rawId.startsWith('ch-')) return
      const nodeMatch = allNodes.find(n => rawId.startsWith(nid(n.id)))
      if (nodeMatch) {
        setSelectedNode(prev => prev?.id === nodeMatch.id ? null : nodeMatch)
        setSelectedArc(null)
        setExpanded(false)
      }
    })

    // Auto-expand window on drag near edges
    graph.on('canvas:dragend', () => {
      const wf = windowFromRef.current
      const wt = windowToRef.current
      const tc = totalChaptersRef.current
      const vp = graph.getCanvasByViewport([container.clientWidth / 2, container.clientHeight / 2])
      if (!vp) return
      const ch = Math.round((vp[0] - LEFT_MARGIN) / CH_W) + wf
      const margin = Math.max(3, Math.floor(WINDOW / 6))
      if (ch < wf + margin) {
        setWindowCenter(prev => Math.max(WINDOW + 1, prev - Math.floor(WINDOW / 2)))
      } else if (ch > wt - margin) {
        setWindowCenter(prev => Math.min(tc - WINDOW, prev + Math.floor(WINDOW / 2)))
      }
    })

    // Update edge counts on zoom / after drag
    const updateEdgeCounts = () => {
      const zoom = graph.getZoom()
      const pos = graph.getPosition()
      const cw = container.clientWidth
      const wf = windowFromRef.current
      const left = (-pos[0]) / zoom
      const right = left + cw / zoom
      const visFrom = Math.floor((left - LEFT_MARGIN) / CH_W) + wf
      const visTo = Math.ceil((right - LEFT_MARGIN) / CH_W) + wf
      const nodes = allNodesRef.current
      setEdgeCounts({
        left: nodes.filter(n => n.target_chapter < visFrom).length,
        right: nodes.filter(n => n.target_chapter > visTo).length,
      })
    }
    graph.on('canvas:zoom', updateEdgeCounts)
    graph.on('canvas:dragend', updateEdgeCounts)
    setTimeout(updateEdgeCounts, 200)

    const ro = new ResizeObserver(() => graph.resize())
    ro.observe(container)

    return () => {
      ro.disconnect()
      graph.destroy()
      graphRef.current = null
    }
  }, [arcs.length, loading]) // only on mount / novel switch

  // Update graph data when window shifts
  useEffect(() => {
    const graph = graphRef.current
    if (!graph || arcs.length === 0) return
    graph.setData(graphData)
    graph.render().then(() => {
      const cw = containerRef.current?.clientWidth ?? 800
      const ch = containerRef.current?.clientHeight ?? 600
      centerOnChapter(graph, cw, ch, windowCenter, windowFrom, arcs.length)
    })
  }, [graphData])

  const totalChapters = allNodes.length > 0
    ? Math.max(...allNodes.map(n => n.target_chapter))
    : 1
  const totalChaptersRef = useRef(totalChapters)
  totalChaptersRef.current = totalChapters
  const allNodesRef = useRef(allNodes)
  allNodesRef.current = allNodes

  const canShiftLeft = windowCenter > WINDOW + 1
  const canShiftRight = windowTo < totalChapters

  function shift(delta: number) {
    setWindowCenter(prev => Math.max(WINDOW + 1, Math.min(totalChapters - WINDOW, prev + delta)))
  }

  return (
    <main className="relative flex-1 min-w-0 overflow-hidden bg-[#fafbfc]">
      {/* Toolbar */}
      <div className="absolute left-5 right-5 top-4 z-10 flex items-center justify-between">
        <div>
          <div className="flex items-center gap-2 text-sm font-semibold text-slate-800">
            <GitBranch className="h-4 w-4 text-purple-500" />
            故事弧线
          </div>
          <div className="mt-1 text-xs text-slate-500">
            {arcs.length} 条弧线 · {allNodes.length} 个节点 · 第 {windowFrom}-{windowTo} 章（共 {totalChapters} 章）
          </div>
        </div>
        <div className="flex items-center gap-1.5 rounded-md border border-slate-200/80 bg-white/82 p-1 shadow-sm backdrop-blur">
          <button
            type="button"
            onClick={() => shift(-20)}
            disabled={!canShiftLeft}
            className="flex h-7 w-7 items-center justify-center rounded text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800 disabled:opacity-30 disabled:cursor-default"
            title="前移 20 章"
          >
            <ArrowLeft className="h-3.5 w-3.5" />
          </button>
          <button
            type="button"
            onClick={() => shift(20)}
            disabled={!canShiftRight}
            className="flex h-7 w-7 items-center justify-center rounded text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800 disabled:opacity-30 disabled:cursor-default"
            title="后移 20 章"
          >
            <ArrowRight className="h-3.5 w-3.5" />
          </button>
          <div className="w-px h-4 bg-slate-200" />
          <button
            type="button"
            onClick={() => graphRef.current?.fitView({ when: 'always' }, { duration: 360, easing: 'ease-in-out' })}
            className="flex h-7 w-7 items-center justify-center rounded text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800"
            title="适配视图"
          >
            <LocateFixed className="h-3.5 w-3.5" />
          </button>
          <button
            type="button"
            onClick={load}
            className="flex h-7 w-7 items-center justify-center rounded text-slate-500 transition-colors hover:bg-slate-100 hover:text-slate-800"
            title="刷新"
          >
            <RefreshCw className="h-3.5 w-3.5" />
          </button>
        </div>
      </div>

      {/* Legend */}
      <div className="absolute right-5 bottom-5 z-10 rounded-md border border-slate-200 bg-white/90 px-3 py-2.5 text-xs text-slate-600 shadow-sm backdrop-blur space-y-2.5">
        <div>
          <span className="text-slate-400 text-[10px] uppercase tracking-wider">节点</span>
          <div className="flex items-center gap-3 mt-1.5">
            <span className="inline-flex items-center gap-1.5">
              <span className="inline-block h-3.5 w-8 rounded-full bg-blue-400 border border-blue-500" />
              已完成
            </span>
            <span className="inline-flex items-center gap-1.5">
              <span className="inline-block h-3.5 w-8 rounded-full border-2 border-blue-400 bg-white" />
              进行中
            </span>
            <span className="inline-flex items-center gap-1.5">
              <span className="inline-block h-3.5 w-8 rounded-full bg-slate-200 border border-dashed border-slate-300" />
              已废弃
            </span>
          </div>
        </div>
        <div>
          <span className="text-slate-400 text-[10px] uppercase tracking-wider">连线</span>
          <div className="flex items-center gap-3 mt-1.5">
            <span className="inline-flex items-center gap-1.5">
              <span className="inline-block h-0.5 w-7 bg-blue-400 rounded" />
              已发生
            </span>
            <span className="inline-flex items-center gap-1.5">
              <span className="inline-block h-0 w-7 border-t-2 border-dashed border-blue-300/60" />
              未发生
            </span>
            <span className="inline-flex items-center gap-1.5">
              <span className="inline-block h-0 w-7 border-t-2 border-dashed border-slate-300" />
              断裂
            </span>
          </div>
        </div>
      </div>

      {/* Detail panel: selected node */}
      {selectedNode && (() => {
        const desc = selectedNode.description?.trim() || ''
        const longDesc = desc.length > 100
        const ch = selectedNode.actual_chapter > 0
          ? `实际第${selectedNode.actual_chapter}章`
          : `目标第${selectedNode.target_chapter}章`
        const arc = arcs.find(a => a.id === selectedNode.story_arc_id)
        return (
          <div className="absolute left-5 bottom-5 z-10 w-64 rounded-lg border border-slate-200 bg-white/94 p-4 shadow-lg backdrop-blur text-sm">
            <div className="flex items-start justify-between gap-2 mb-2">
              <h3 className="font-semibold text-slate-800">{selectedNode.title}</h3>
              <button
                onClick={() => { setSelectedNode(null); setExpanded(false) }}
                className="shrink-0 rounded p-0.5 text-slate-400 hover:text-slate-600 hover:bg-slate-100 transition-colors"
              >
                <X className="h-3.5 w-3.5" />
              </button>
            </div>
            {arc && (
              <span className="inline-block rounded bg-purple-50 px-2 py-0.5 text-xs text-purple-600 mb-2">{arc.name}</span>
            )}
            <div className="text-xs text-slate-500 mb-2">
              <span className={selectedNode.status === 'completed' ? 'text-green-600' : selectedNode.status === 'abandoned' ? 'text-slate-400 line-through' : 'text-blue-600'}>
                {selectedNode.status === 'completed' ? '已完成' : selectedNode.status === 'abandoned' ? '已废弃' : '进行中'}
              </span>
              <span className="mx-1.5">·</span>
              <span>{ch}</span>
            </div>
            {desc && (
              <div>
                <p className="text-xs text-slate-500 leading-relaxed">
                  {longDesc && !expanded ? desc.slice(0, 100) + '…' : desc}
                </p>
                {longDesc && (
                  <button
                    onClick={() => setExpanded(!expanded)}
                    className="text-xs text-blue-500 hover:text-blue-600 mt-0.5"
                  >
                    {expanded ? '收起' : '展开'}
                  </button>
                )}
              </div>
            )}
            {!desc && <p className="text-xs text-slate-400">暂无详细描述</p>}
          </div>
        )
      })()}

      {/* Detail panel: selected arc */}
      {selectedArc && !selectedNode && (
        <div className="absolute left-5 bottom-5 z-10 w-64 rounded-lg border border-slate-200 bg-white/94 p-4 shadow-lg backdrop-blur text-sm">
          <div className="flex items-start justify-between gap-2 mb-2">
            <h3 className="font-semibold text-slate-800">{selectedArc.name}</h3>
            <button
              onClick={() => setSelectedArc(null)}
              className="shrink-0 rounded p-0.5 text-slate-400 hover:text-slate-600 hover:bg-slate-100 transition-colors"
            >
              <X className="h-3.5 w-3.5" />
            </button>
          </div>
          <div className="flex items-center gap-2 mb-2">
            <span className="inline-block rounded bg-slate-100 px-2 py-0.5 text-xs text-slate-500">{selectedArc.arc_type}</span>
            <span className={`
              inline-block rounded px-2 py-0.5 text-xs
              ${selectedArc.status === 'active' ? 'bg-green-50 text-green-600' : ''}
              ${selectedArc.status === 'paused' ? 'bg-amber-50 text-amber-600' : ''}
              ${selectedArc.status === 'completed' ? 'bg-blue-50 text-blue-600' : ''}
              ${selectedArc.status === 'abandoned' ? 'bg-slate-100 text-slate-400' : ''}
            `}>
              {selectedArc.status === 'active' ? '活跃' :
               selectedArc.status === 'paused' ? '暂停' :
               selectedArc.status === 'completed' ? '已完成' :
               selectedArc.status === 'abandoned' ? '已废弃' : selectedArc.status}
            </span>
            <span className="text-xs text-slate-400">{'★'.repeat(selectedArc.importance)}</span>
          </div>
          {selectedArc.description && (
            <p className="text-xs text-slate-500 leading-relaxed">{selectedArc.description}</p>
          )}
          {selectedArc.status === 'paused' && selectedArc.reactivate_at && (
            <div className="mt-2 pt-2 border-t border-slate-100">
              <p className="text-xs text-slate-400 mb-0.5">恢复条件</p>
              <p className="text-xs text-slate-500">{selectedArc.reactivate_at}</p>
            </div>
          )}
        </div>
      )}

      {/* Edge indicators */}
      {edgeCounts.left > 0 && (
        <button
          onClick={() => shift(-WINDOW)}
          className="absolute left-5 top-1/2 z-10 -translate-y-1/2 rounded-full border border-slate-200 bg-white/88 px-2 py-1.5 text-[10px] text-slate-500 shadow-sm backdrop-blur hover:bg-white hover:text-slate-700 transition-colors"
        >
          ← {edgeCounts.left} 个节点
        </button>
      )}
      {edgeCounts.right > 0 && (
        <button
          onClick={() => shift(WINDOW)}
          className="absolute right-5 top-1/2 z-10 -translate-y-1/2 rounded-full border border-slate-200 bg-white/88 px-2 py-1.5 text-[10px] text-slate-500 shadow-sm backdrop-blur hover:bg-white hover:text-slate-700 transition-colors"
        >
          {edgeCounts.right} 个节点 →
        </button>
      )}

      {/* G6 container */}
      {error ? (
        <div className="relative z-10 flex h-full items-center justify-center text-sm text-rose-500">{error}</div>
      ) : loading ? (
        <div className="relative z-10 flex h-full items-center justify-center text-sm text-slate-500">加载中...</div>
      ) : arcs.length === 0 ? (
        <div className="relative z-10 flex h-full items-center justify-center">
          <div className="text-center">
            <div className="mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-purple-50 text-purple-500 shadow-sm">
              <GitBranch className="h-6 w-6" />
            </div>
            <div className="mt-3 text-sm font-medium text-slate-700">暂无叙事弧线</div>
          </div>
        </div>
      ) : (
        <div ref={containerRef} className="relative z-[1] h-full w-full" />
      )}
    </main>
  )
}
