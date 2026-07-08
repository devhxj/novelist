import { useCallback, useEffect, useMemo, useState } from 'react'
import { GitBranch, Pencil, Plus, Trash2, X } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { useTheme } from '@/hooks/useTheme'
import type { storyarc } from '@/hooks/useApp'
import StoryArcGraph from '@/components/storyarc/StoryArcGraph'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics } from '@/lib/novelist/types'

interface Props { novelId: number; focusArcId?: number }

type ViewTab = 'list' | 'swimlane'

const PALETTE_LIGHT = [
  { fill: '#dbeafe', stroke: '#3b82f6', text: '#1d4ed8' },
  { fill: '#dcfce7', stroke: '#22c55e', text: '#166534' },
  { fill: '#fef3c7', stroke: '#f59e0b', text: '#92400e' },
  { fill: '#f3e8ff', stroke: '#a855f7', text: '#6b21a8' },
  { fill: '#ffe4e6', stroke: '#f43f5e', text: '#9f1239' },
  { fill: '#ccfbf1', stroke: '#14b8a6', text: '#115e59' },
  { fill: '#ffedd5', stroke: '#f97316', text: '#9a3412' },
]

const PALETTE_DARK = [
  { fill: 'oklch(0.58 0.15 255 / 0.15)', stroke: 'oklch(0.72 0.15 255)', text: 'oklch(0.78 0.1 255)' },
  { fill: 'oklch(0.58 0.16 145 / 0.15)', stroke: 'oklch(0.72 0.15 145)', text: 'oklch(0.78 0.1 145)' },
  { fill: 'oklch(0.62 0.18 80 / 0.15)', stroke: 'oklch(0.78 0.16 80)', text: 'oklch(0.82 0.1 80)' },
  { fill: 'oklch(0.55 0.18 280 / 0.15)', stroke: 'oklch(0.72 0.15 280)', text: 'oklch(0.78 0.1 280)' },
  { fill: 'oklch(0.5 0.18 15 / 0.15)', stroke: 'oklch(0.7 0.15 15)', text: 'oklch(0.76 0.1 15)' },
  { fill: 'oklch(0.58 0.16 175 / 0.15)', stroke: 'oklch(0.72 0.15 175)', text: 'oklch(0.78 0.1 175)' },
  { fill: 'oklch(0.62 0.18 45 / 0.15)', stroke: 'oklch(0.78 0.16 45)', text: 'oklch(0.82 0.1 45)' },
]

type Filter = 'all' | 'pending' | 'completed' | 'abandoned'
const WINDOW = 20

const FILTERS: { key: Filter; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'pending', label: '进行中' },
  { key: 'completed', label: '已完成' },
  { key: 'abandoned', label: '已废弃' },
]

const ARC_TYPES = [
  { value: 'main', label: '主线' },
  { value: 'sub', label: '支线' },
  { value: 'character', label: '角色线' },
  { value: 'background', label: '背景线' },
]

const ARC_STATUSES = [
  { value: 'active', label: '活跃' },
  { value: 'paused', label: '暂停' },
  { value: 'completed', label: '已完成' },
  { value: 'abandoned', label: '已废弃' },
]

const NODE_STATUSES = [
  { value: 'pending', label: '进行中' },
  { value: 'completed', label: '已完成' },
  { value: 'abandoned', label: '已废弃' },
]

const IMPORTANCES = [1, 2, 3, 4, 5]
function stars(v: number) { return '★'.repeat(Math.max(0, Math.min(5, v))) }

type EditMode =
  | { type: 'create_arc' }
  | { type: 'edit_arc'; arc: storyarc.StoryArc }
  | { type: 'create_node' }
  | { type: 'edit_node'; node: storyarc.ArcNode }
  | null

type ArcForm = { name: string; arc_type: string; description?: string; importance?: number; status?: string; reactivate_at?: string }
type NodeForm = { story_arc_id: number; title: string; description?: string; target_chapter: number; actual_chapter?: number; status?: string }

const EMPTY_ARC: ArcForm = { name: '', arc_type: 'main' }
const EMPTY_NODE: NodeForm = { story_arc_id: 0, title: '', target_chapter: 1 }

type VisibleError = {
  message: string
  diagnostic?: diagnostics.CopyableDiagnostic | null
}

export default function ArcListView({ novelId, focusArcId }: Props) {
  const app = useApp()
  const { theme } = useTheme()
  const PALETTE = { light: PALETTE_LIGHT, dark: PALETTE_DARK }[theme]

  const [arcs, setArcs] = useState<storyarc.StoryArc[]>([])
  const [allNodes, setAllNodes] = useState<storyarc.ArcNode[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<VisibleError | null>(null)
  const [expandedId, setExpandedId] = useState<number | null>(null)
  const [windowCenter, setWindowCenter] = useState(0)
  const [filter, setFilter] = useState<Filter>('all')
  const [hiddenArcIds, setHiddenArcIds] = useState<Set<number>>(new Set())
  const [viewTab, setViewTab] = useState<ViewTab>('list')
  const [editMode, setEditMode] = useState<EditMode>(null)
  const [arcForm, setArcForm] = useState<ArcForm>(EMPTY_ARC)
  const [nodeForm, setNodeForm] = useState<NodeForm>(EMPTY_NODE)
  const [saving, setSaving] = useState(false)

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
      setError(buildVisibleError(err, '加载弧线节点失败', '加载弧线节点', null, { novel_id: novelId }))
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      if (!novelId) {
        if (!cancelled) {
          setArcs([])
          setAllNodes([])
        }
        return
      }
      if (!cancelled) {
        setLoading(true)
        setError(null)
      }
      try {
        const [arcList, nodeList, maxCh] = await Promise.all([
          app.GetStoryArcs(novelId),
          app.GetArcNodes(novelId, 0, 0),
          app.GetMaxChapterNumber(novelId),
        ])
        if (!cancelled) {
          setArcs(arcList ?? [])
          setAllNodes(nodeList ?? [])
          setWindowCenter(Math.max(1, maxCh))
        }
      } catch (err) {
        if (!cancelled) setError(buildVisibleError(err, '加载弧线节点失败', '加载弧线节点', null, { novel_id: novelId }))
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [app, novelId])

  useEffect(() => {
    if (focusArcId && focusArcId > 0 && allNodes.length > 0) {
      const arcNodes = allNodes.filter(n => n.story_arc_id === focusArcId)
      if (arcNodes.length > 0) {
        const firstNode = arcNodes[0]
        const targetChapter = firstNode.target_chapter || firstNode.actual_chapter || 1
        const targetId = firstNode.id
        const timer = window.setTimeout(() => {
          setWindowCenter(targetChapter)
          setExpandedId(targetId)
        }, 0)
        return () => window.clearTimeout(timer)
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
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  function showAllArcs() { setHiddenArcIds(new Set()) }

  // ── Arc CRUD ─────────────────────────────────────────

  function openCreateArc() {
    setError(null)
    setArcForm(EMPTY_ARC)
    setEditMode({ type: 'create_arc' })
  }

  function openEditArc(arc: storyarc.StoryArc) {
    setError(null)
    setArcForm({
      name: arc.name,
      arc_type: arc.arc_type,
      description: arc.description || '',
      importance: arc.importance,
    })
    setEditMode({ type: 'edit_arc', arc })
  }

  async function handleCreateArc() {
    if (!arcForm.name.trim()) { setError({ message: '请输入弧线名称' }); return }
    if (!arcForm.arc_type) { setError({ message: '请选择弧线类型' }); return }
    setSaving(true)
    try {
      await app.CreateStoryArc(novelId, arcForm)
      setEditMode(null)
      await load()
    } catch (err) {
      setError(buildVisibleError(err, '创建弧线失败', '创建弧线', 'CreateStoryArc', {
        novel_id: novelId,
        name: arcForm.name,
        arc_type: arcForm.arc_type,
        importance: arcForm.importance,
        source_text: arcForm.description || '',
      }))
    } finally {
      setSaving(false)
    }
  }

  async function handleUpdateArc() {
    if (!editMode || editMode.type !== 'edit_arc') return
    setSaving(true)
    try {
      await app.UpdateStoryArc(novelId, editMode.arc.id, arcForm)
      setEditMode(null)
      await load()
    } catch (err) {
      setError(buildVisibleError(err, '更新弧线失败', '更新弧线', 'UpdateStoryArc', {
        novel_id: novelId,
        story_arc_id: editMode.arc.id,
        name: arcForm.name,
        arc_type: arcForm.arc_type,
        status: arcForm.status ?? editMode.arc.status,
        importance: arcForm.importance,
        source_text: arcForm.description || '',
      }))
    } finally {
      setSaving(false)
    }
  }

  async function handleDeleteArc(arcId: number) {
    if (!confirm('确定要删除这条弧线吗？关联的所有节点也会被删除。此操作不可撤销。')) return
    setSaving(true)
    try {
      await app.DeleteStoryArc(novelId, arcId)
      setExpandedId(null)
      await load()
    } catch (err) {
      setError(buildVisibleError(err, '删除弧线失败', '删除弧线', 'DeleteStoryArc', {
        novel_id: novelId,
        story_arc_id: arcId,
      }))
    } finally {
      setSaving(false)
    }
  }

  // ── Node CRUD ────────────────────────────────────────

  function openCreateNode(arcId?: number) {
    setError(null)
    setNodeForm({ ...EMPTY_NODE, story_arc_id: arcId ?? arcs[0]?.id ?? 0, target_chapter: Math.max(1, windowCenter) })
    setEditMode({ type: 'create_node' })
  }

  function openEditNode(node: storyarc.ArcNode) {
    setError(null)
    setNodeForm({
      story_arc_id: node.story_arc_id,
      title: node.title,
      description: node.description || '',
      target_chapter: node.target_chapter,
    })
    setEditMode({ type: 'edit_node', node })
  }

  async function handleCreateNode() {
    if (!nodeForm.title.trim()) { setError({ message: '请输入节点标题' }); return }
    if (!nodeForm.story_arc_id) { setError({ message: '请选择所属弧线' }); return }
    if (!nodeForm.target_chapter) { setError({ message: '请输入目标章节' }); return }
    setSaving(true)
    try {
      const created = await app.CreateArcNode(novelId, nodeForm)
      setEditMode(null)
      await load()
      setExpandedId(created.id)
    } catch (err) {
      setError(buildVisibleError(err, '创建节点失败', '创建弧线节点', 'CreateArcNode', {
        novel_id: novelId,
        story_arc_id: nodeForm.story_arc_id,
        title: nodeForm.title,
        target_chapter: nodeForm.target_chapter,
        source_text: nodeForm.description || '',
      }))
    } finally {
      setSaving(false)
    }
  }

  async function handleUpdateNode() {
    if (!editMode || editMode.type !== 'edit_node') return
    if (!nodeForm.title.trim()) { setError({ message: '请输入节点标题' }); return }
    const nodeId = editMode.node.id
    setSaving(true)
    try {
      await app.UpdateArcNode(novelId, nodeId, nodeForm)
      setEditMode(null)
      await load()
      setExpandedId(nodeId)
    } catch (err) {
      setError(buildVisibleError(err, '更新节点失败', '更新弧线节点', 'UpdateArcNode', {
        novel_id: novelId,
        arc_node_id: nodeId,
        story_arc_id: nodeForm.story_arc_id,
        title: nodeForm.title,
        target_chapter: nodeForm.target_chapter,
        status: nodeForm.status ?? editMode.node.status,
        source_text: nodeForm.description || '',
      }))
    } finally {
      setSaving(false)
    }
  }

  async function handleDeleteNode(nodeId: number) {
    if (!confirm('确定要删除这个节点吗？')) return
    setSaving(true)
    try {
      await app.DeleteArcNode(novelId, nodeId)
      setExpandedId(null)
      await load()
    } catch (err) {
      setError(buildVisibleError(err, '删除节点失败', '删除弧线节点', 'DeleteArcNode', {
        novel_id: novelId,
        arc_node_id: nodeId,
      }))
    } finally {
      setSaving(false)
    }
  }

  async function handleQuickNodeStatus(node: storyarc.ArcNode, newStatus: string) {
    setSaving(true)
    try {
      await app.UpdateArcNode(novelId, node.id, { status: newStatus })
      await load()
    } catch (err) {
      setError(buildVisibleError(err, '更新节点状态失败', '更新弧线节点状态', 'UpdateArcNode', {
        novel_id: novelId,
        arc_node_id: node.id,
        story_arc_id: node.story_arc_id,
        title: node.title,
        previous_status: node.status,
        next_status: newStatus,
        source_text: node.description || '',
      }))
    } finally {
      setSaving(false)
    }
  }

  const nodeStatusStyle = (status: string) => {
    switch (status) {
      case 'completed': return { bg: 'bg-tag-green', text: 'text-tag-green-foreground', label: '已完成' }
      case 'abandoned': return { bg: 'bg-secondary', text: 'text-muted-foreground', label: '已废弃' }
      default: return { bg: 'bg-tag-blue', text: 'text-tag-blue-foreground', label: '进行中' }
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

  function renderArcForm(isCreate: boolean) {
    return (
      <div className="space-y-3">
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">名称</label>
          <input
            type="text"
            value={arcForm.name}
            onChange={e => setArcForm(f => ({ ...f, name: e.target.value }))}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            placeholder="弧线名称"
          />
        </div>
        <div className="flex gap-3">
          <div className="flex-1">
            <label className="text-xs font-medium text-muted-foreground mb-1 block">类型</label>
            <select
              value={arcForm.arc_type}
              onChange={e => setArcForm(f => ({ ...f, arc_type: e.target.value }))}
              className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {ARC_TYPES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
            </select>
          </div>
          <div>
            <label className="text-xs font-medium text-muted-foreground mb-1 block">重要度</label>
            <select
              value={arcForm.importance}
              onChange={e => setArcForm(f => ({ ...f, importance: parseInt(e.target.value) }))}
              className="rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {IMPORTANCES.map(i => <option key={i} value={i}>{stars(i)}</option>)}
            </select>
          </div>
        </div>
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">描述</label>
          <textarea
            value={arcForm.description}
            onChange={e => setArcForm(f => ({ ...f, description: e.target.value }))}
            rows={2}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y"
            placeholder="弧线整体描述"
          />
        </div>
        {!isCreate && editMode?.type === 'edit_arc' && (
          <div>
            <label className="text-xs font-medium text-muted-foreground mb-1 block">状态</label>
            <select
              value={arcForm.status ?? editMode.arc.status}
              onChange={e => setArcForm(f => ({ ...f, status: e.target.value }))}
              className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {ARC_STATUSES.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
            </select>
          </div>
        )}
      </div>
    )
  }

  function renderNodeForm() {
    return (
      <div className="space-y-3">
        {editMode?.type === 'create_node' && (
          <div>
            <label className="text-xs font-medium text-muted-foreground mb-1 block">所属弧线</label>
            <select
              value={nodeForm.story_arc_id}
              onChange={e => setNodeForm(f => ({ ...f, story_arc_id: parseInt(e.target.value) }))}
              className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              {arcs.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
            </select>
          </div>
        )}
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">标题</label>
          <input
            type="text"
            value={nodeForm.title}
            onChange={e => setNodeForm(f => ({ ...f, title: e.target.value }))}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            placeholder="节点标题"
          />
        </div>
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">描述</label>
          <textarea
            value={nodeForm.description}
            onChange={e => setNodeForm(f => ({ ...f, description: e.target.value }))}
            rows={2}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y"
            placeholder="节点详情"
          />
        </div>
        <div className="flex gap-3">
          <div className="flex-1">
            <label className="text-xs font-medium text-muted-foreground mb-1 block">目标章节</label>
            <input
              type="number"
              value={nodeForm.target_chapter}
              onChange={e => setNodeForm(f => ({ ...f, target_chapter: parseInt(e.target.value) || 1 }))}
              min={1}
              className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </div>
          {editMode?.type === 'edit_node' && (
            <div>
              <label className="text-xs font-medium text-muted-foreground mb-1 block">状态</label>
              <select
                value={nodeForm.status ?? editMode.node.status}
                onChange={e => setNodeForm(f => ({ ...f, status: e.target.value }))}
                className="rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                {NODE_STATUSES.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}
              </select>
            </div>
          )}
        </div>
      </div>
    )
  }

  function renderFormButtons(onSubmit: () => Promise<void>, onDelete?: () => void) {
    return (
      <div className="flex items-center gap-2 justify-end mt-3">
        {onDelete && (
          <button onClick={onDelete} disabled={saving} className="px-3 py-1 rounded text-xs text-destructive hover:bg-destructive/10 transition-colors">
            <Trash2 className="h-3 w-3 inline mr-1" />删除
          </button>
        )}
        <button onClick={() => setEditMode(null)} className="px-3 py-1 rounded text-xs text-muted-foreground hover:text-foreground transition-colors">取消</button>
        <button onClick={onSubmit} disabled={saving} className="px-3 py-1 rounded bg-primary text-primary-foreground text-xs font-medium hover:opacity-90 transition-opacity disabled:opacity-50">
          {saving ? '保存中...' : '保存'}
        </button>
      </div>
    )
  }

  return (
    <main className="flex-1 min-w-0 flex flex-col overflow-hidden bg-background">
      {/* Tab bar */}
      <div className="flex items-center gap-1 px-5 pt-4 pb-2 shrink-0">
        <button
          onClick={() => setViewTab('list')}
          className={`px-3 py-1.5 rounded text-xs font-medium transition-colors ${
            viewTab === 'list'
              ? 'bg-card border border-border text-foreground shadow-sm'
              : 'text-muted-foreground hover:text-foreground hover:bg-card/60'
          }`}
        >
          列表
        </button>
        <button
          onClick={() => setViewTab('swimlane')}
          className={`px-3 py-1.5 rounded text-xs font-medium transition-colors ${
            viewTab === 'swimlane'
              ? 'bg-card border border-border text-foreground shadow-sm'
              : 'text-muted-foreground hover:text-foreground hover:bg-card/60'
          }`}
        >
          泳道图
        </button>
      </div>

      {viewTab === 'swimlane' ? (
        <StoryArcGraph novelId={novelId} />
      ) : loading ? (
        <div className="flex h-full items-center justify-center text-sm text-muted-foreground">加载中...</div>
      ) : (
        <div className="flex-1 overflow-y-auto overscroll-contain">
        <div className="max-w-3xl mx-auto px-5 py-6 space-y-6">
          {error && (
            <ErrorCallout
              message={error.message}
              diagnostic={error.diagnostic}
              onRetry={() => { void load() }}
              retrying={loading}
              onClose={() => setError(null)}
            />
          )}

          {/* Header */}
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-2">
              <GitBranch className="h-4 w-4 text-tag-purple-foreground" />
              <h2 className="text-sm font-semibold text-foreground">
                弧线节点
                <span className="ml-2 text-xs font-normal text-muted-foreground">{filteredNodes.length} 个</span>
              </h2>
            </div>
            <div className="flex items-center gap-2">
              <span className="text-[11px] text-muted-foreground">
                第 {windowFrom}-{windowTo} 章 · 共 {minChapter}-{maxChapter} 章
              </span>
              <button onClick={load} className="text-xs text-muted-foreground hover:text-muted-foreground transition-colors">刷新</button>
            </div>
          </div>

          {/* Arc filter chips */}
          <div className="flex flex-wrap gap-1.5">
            <button onClick={showAllArcs} className={`px-3 py-1 rounded text-xs font-medium transition-colors ${
              hiddenArcIds.size === 0
                ? 'bg-card border border-border text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground hover:bg-card/60'
            }`}>
              全部
            </button>
            {arcs.map((arc, i) => {
              const c = PALETTE[i % PALETTE.length]
              const hidden = hiddenArcIds.has(arc.id)
              return (
                <button
                  key={arc.id}
                  onClick={() => toggleArc(arc.id)}
                  className={`group px-3 py-1 rounded text-xs font-medium transition-colors border relative ${
                    hidden
                      ? 'text-muted-foreground border-transparent hover:text-muted-foreground hover:bg-card/60'
                      : 'border-border shadow-sm text-foreground'
                  }`}
                  style={hidden ? {} : { backgroundColor: c.fill, borderColor: c.stroke, color: c.text }}
                >
                  {arc.name}{arcStatusTag(arc.status)}
                  {/* Hover actions */}
                  <span className="ml-1 opacity-0 group-hover:opacity-100 inline-flex items-center gap-1 transition-opacity" style={{ color: hidden ? undefined : c.text }}>
                    <span
                      onClick={(e) => { e.stopPropagation(); openEditArc(arc) }}
                      className="p-0.5 rounded hover:opacity-70"
                      title="编辑"
                    >
                      <Pencil className="h-3.5 w-3.5" />
                    </span>
                    <span
                      onClick={(e) => { e.stopPropagation(); handleDeleteArc(arc.id) }}
                      className="p-0.5 rounded hover:opacity-70"
                      title="删除"
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </span>
                  </span>
                </button>
              )
            })}
            <button
              onClick={openCreateArc}
              className="px-3 py-1 rounded text-xs font-medium text-muted-foreground hover:text-foreground hover:bg-card/60 transition-colors border border-dashed border-border"
            >
              <Plus className="h-3 w-3 inline mr-1" />新弧线
            </button>
          </div>

          {/* Arc form */}
          {editMode?.type === 'create_arc' && (
            <div className="rounded-lg border border-border bg-card p-4">
              <div className="flex items-center justify-between mb-3">
                <span className="text-xs font-semibold text-foreground">新建弧线</span>
                <button onClick={() => setEditMode(null)} className="p-0.5 rounded text-muted-foreground hover:text-foreground"><X className="h-3.5 w-3.5" /></button>
              </div>
              {renderArcForm(true)}
              {renderFormButtons(handleCreateArc)}
            </div>
          )}
          {editMode?.type === 'edit_arc' && (
            <div className="rounded-lg border border-border bg-card p-4">
              <div className="flex items-center justify-between mb-3">
                <span className="text-xs font-semibold text-foreground">编辑弧线：{editMode.arc.name}</span>
                <button onClick={() => setEditMode(null)} className="p-0.5 rounded text-muted-foreground hover:text-foreground"><X className="h-3.5 w-3.5" /></button>
              </div>
              {renderArcForm(false)}
              {renderFormButtons(handleUpdateArc, () => handleDeleteArc(editMode.arc.id))}
            </div>
          )}

          {/* Quick actions bar */}
          <div className="flex items-center justify-between">
            <div className="flex gap-1">
              {FILTERS.map(f => (
                <button key={f.key} onClick={() => setFilter(f.key)} className={`px-3 py-1 rounded text-xs transition-colors ${
                  filter === f.key ? 'bg-card border border-border text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
                }`}>
                  {f.label}
                  {f.key !== 'all' && (
                    <span className="ml-1 text-muted-foreground">({allNodes.filter(n => activeArcIds.has(n.story_arc_id) && n.status === f.key).length})</span>
                  )}
                </button>
              ))}
            </div>
            {arcs.length > 0 && (
              <button onClick={() => openCreateNode()} className="inline-flex items-center gap-1 px-2.5 py-1 rounded text-xs font-medium bg-primary text-primary-foreground hover:opacity-90 transition-opacity">
                <Plus className="h-3 w-3" />新建节点
              </button>
            )}
          </div>

          {/* Node form */}
          {editMode?.type === 'create_node' && (
            <div className="rounded-lg border border-border bg-card p-4">
              <div className="flex items-center justify-between mb-3">
                <span className="text-xs font-semibold text-foreground">新建节点</span>
                <button onClick={() => setEditMode(null)} className="p-0.5 rounded text-muted-foreground hover:text-foreground"><X className="h-3.5 w-3.5" /></button>
              </div>
              {renderNodeForm()}
              {renderFormButtons(handleCreateNode)}
            </div>
          )}

          {/* Node list */}
          {grouped.length === 0 ? (
            <div className="text-center py-12">
              <div className="mx-auto flex h-12 w-12 items-center justify-center rounded-full bg-tag-purple text-tag-purple-foreground">
                <GitBranch className="h-5 w-5" />
              </div>
              <p className="mt-2 text-sm text-muted-foreground">{arcs.length === 0 ? '暂无叙事弧线' : '没有匹配的节点'}</p>
            </div>
          ) : (
            <div className="space-y-6">
              {beforeCount > 0 && (
                <button onClick={() => shiftWindow(-WINDOW)} className="w-full rounded-lg border border-dashed border-border bg-card/60 px-4 py-2.5 text-xs text-muted-foreground hover:bg-card hover:border-border hover:text-foreground transition-colors">
                  ← 第 {beforeChapters[0]?.[0]}-{beforeChapters[beforeChapters.length - 1]?.[0]} 章 · {beforeCount} 个节点
                </button>
              )}

              {visibleChapters.map(([ch, items]) => (
                <div key={ch}>
                  <div className="flex items-center gap-1.5 mb-2">
                    <span className="text-xs font-medium text-muted-foreground">第 {ch} 章</span>
                    <span className="text-[10px] text-muted-foreground">{items.length} 个节点</span>
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
                      const isEditing = editMode?.type === 'edit_node' && editMode.node.id === node.id

                      if (isEditing) {
                        return (
                          <div key={node.id} className="rounded-lg border border-border bg-card p-4">
                            <div className="flex items-center justify-between mb-3">
                              <span className="text-xs font-semibold text-foreground">编辑：{node.title}</span>
                              <button onClick={() => setEditMode(null)} className="p-0.5 rounded text-muted-foreground hover:text-foreground"><X className="h-3.5 w-3.5" /></button>
                            </div>
                            {renderNodeForm()}
                            {renderFormButtons(handleUpdateNode, () => handleDeleteNode(node.id))}
                          </div>
                        )
                      }

                      return (
                        <div
                          key={node.id}
                          className={`rounded-lg border bg-card transition-shadow group ${isExpanded ? 'border-border shadow-sm' : 'border-border hover:border-border hover:shadow-sm'}`}
                        >
                          <div className="flex items-center gap-3 px-4 py-3">
                            <span className="shrink-0 h-3 w-3 rounded-full" style={{ backgroundColor: c.stroke }} />
                            <div
                              className="flex-1 min-w-0 cursor-pointer"
                              onClick={() => setExpandedId(isExpanded ? null : node.id)}
                            >
                              <div className="flex items-center gap-2">
                                <span className="text-sm font-medium text-foreground truncate">{node.title}</span>
                                <span className={`shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium ${s.bg} ${s.text}`}>{s.label}</span>
                                {arc && (
                                  <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium" style={{ backgroundColor: c.fill, color: c.text }}>{arc.name}</span>
                                )}
                              </div>
                              <div className="flex items-center gap-2 mt-0.5 text-[11px] text-muted-foreground">
                                <span>目标第 {node.target_chapter} 章</span>
                                {node.actual_chapter > 0 && <span className="text-tag-green-foreground">· 实际第 {node.actual_chapter} 章</span>}
                                {arc && <span className="text-muted-foreground">· {arc.arc_type}</span>}
                              </div>
                            </div>
                            {/* Hover actions */}
                            <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                              {node.status === 'pending' && (
                                <button onClick={() => handleQuickNodeStatus(node, 'completed')} className="p-1 rounded text-muted-foreground hover:text-tag-green-foreground hover:bg-tag-green/20 transition-colors" title="标记完成">
                                  <span className="text-[10px]">✓</span>
                                </button>
                              )}
                              <button onClick={() => openEditNode(node)} className="p-1 rounded text-muted-foreground hover:text-foreground hover:bg-secondary transition-colors" title="编辑">
                                <Pencil className="h-3.5 w-3.5" />
                              </button>
                              <button onClick={() => handleDeleteNode(node.id)} className="p-1 rounded text-muted-foreground hover:text-destructive hover:bg-destructive/10 transition-colors" title="删除">
                                <Trash2 className="h-3.5 w-3.5" />
                              </button>
                            </div>
                            <span className={`text-[10px] transition-transform cursor-pointer ${isExpanded ? 'rotate-180' : ''}`} onClick={() => setExpandedId(isExpanded ? null : node.id)}>▼</span>
                          </div>

                          {isExpanded && hasContent && (
                            <div className="border-t border-border px-4 py-3">
                              <p className="text-xs text-muted-foreground leading-relaxed whitespace-pre-wrap">{desc}</p>
                            </div>
                          )}
                          {isExpanded && !hasContent && (
                            <div className="border-t border-border px-4 py-3">
                              <p className="text-xs text-muted-foreground">暂无详细描述</p>
                            </div>
                          )}
                        </div>
                      )
                    })}
                  </div>
                </div>
              ))}

              {afterCount > 0 && (
                <button onClick={() => shiftWindow(WINDOW)} className="w-full rounded-lg border border-dashed border-border bg-card/60 px-4 py-2.5 text-xs text-muted-foreground hover:bg-card hover:border-border hover:text-foreground transition-colors">
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

function buildVisibleError(
  error: unknown,
  fallbackMessage: string,
  operation: string,
  bridgeMethod: string | null,
  detail: unknown,
): VisibleError {
  return {
    message: diagnosticMessage(error, fallbackMessage),
    diagnostic: buildCopyableDiagnostic({
      error,
      fallbackMessage,
      operation,
      bridgeMethod,
      detail,
    }),
  }
}
