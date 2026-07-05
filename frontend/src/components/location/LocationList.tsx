import { useState, useEffect, useCallback, useMemo } from 'react'
import { ChevronRight, MapPin, Trash2 } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { location } from '@/hooks/useApp'

interface TreeNode {
  location: location.Location
  children: TreeNode[]
}

interface Props {
  novelId: number
}

function buildTree(locations: location.Location[]): TreeNode[] {
  const map = new Map<number, TreeNode>()
  const roots: TreeNode[] = []

  for (const loc of locations) {
    map.set(loc.id, { location: loc, children: [] })
  }
  for (const loc of locations) {
    const node = map.get(loc.id)!
    if (loc.parent_location_id && map.has(loc.parent_location_id)) {
      map.get(loc.parent_location_id)!.children.push(node)
    } else {
      roots.push(node)
    }
  }

  return roots
}

function rootIds(locations: location.Location[]): Set<number> {
  return new Set(locations.filter(l => !l.parent_location_id).map(l => l.id))
}

export default function LocationList({ novelId }: Props) {
  const app = useApp()

  const [locations, setLocations] = useState<location.Location[]>([])
  const [expandedIds, setExpandedIds] = useState<Set<number>>(new Set())

  const load = useCallback(async () => {
    if (!novelId) {
      setLocations([])
      setExpandedIds(new Set())
      return
    }
    const list = await app.GetLocations(novelId)
    const nextLocations = list ?? []
    setLocations(nextLocations)
    setExpandedIds(rootIds(nextLocations))
  }, [novelId, app])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      if (!novelId) {
        if (!cancelled) {
          setLocations([])
          setExpandedIds(new Set())
        }
        return
      }
      const list = await app.GetLocations(novelId)
      if (!cancelled) {
        const nextLocations = list ?? []
        setLocations(nextLocations)
        setExpandedIds(rootIds(nextLocations))
      }
    })()
    return () => { cancelled = true }
  }, [app, novelId])

  async function handleDelete(locId: number) {
    if (!confirm('确定要删除该地点吗？子地点将变为根节点，关联的空间关系也会被删除。')) return
    try {
      await app.DeleteLocation(novelId, locId)
      await load()
    } catch { /* 静默失败 */ }
  }

  const tree = useMemo(() => buildTree(locations), [locations])

  function toggle(id: number) {
    setExpandedIds(prev => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  function renderNode(node: TreeNode, depth: number) {
    const { location: loc } = node
    const isExpanded = expandedIds.has(loc.id)
    const hasChildren = node.children.length > 0
    return (
      <div key={loc.id} className="group">
        <div
          className="w-full flex items-center hover:bg-muted/50 transition-colors"
        >
          <button
            type="button"
            onClick={() => { if (hasChildren) toggle(loc.id) }}
            className={`flex flex-1 min-w-0 items-center gap-1.5 py-1.5 pr-1 text-left ${hasChildren ? '' : 'cursor-default'}`}
            style={{ paddingLeft: `${12 + depth * 16}px` }}
            aria-label={hasChildren ? `${isExpanded ? '折叠' : '展开'} ${loc.name}` : loc.name}
          >
            {hasChildren ? (
              <ChevronRight
                className={`w-3.5 h-3.5 text-muted-foreground shrink-0 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`}
              />
            ) : (
              <span className="w-3.5 shrink-0" />
            )}
            <MapPin className="w-3.5 h-3.5 text-muted-foreground shrink-0" />
            <span className="flex-1 text-sm truncate">{loc.name}</span>
            {loc.location_type && (
              <span className="text-[10px] text-muted-foreground/60 shrink-0">{loc.location_type}</span>
            )}
          </button>
          <button
            onClick={(e) => { e.stopPropagation(); handleDelete(loc.id) }}
            className="mr-3 shrink-0 p-0.5 rounded text-muted-foreground hover:text-destructive opacity-0 group-hover:opacity-100 transition-opacity"
            title="删除"
          >
            <Trash2 className="h-3 w-3" />
          </button>
        </div>
        {isExpanded && hasChildren && (
          <div>
            {node.children.map(child => renderNode(child, depth + 1))}
          </div>
        )}
      </div>
    )
  }

  return (
    <>
      <div className="flex items-center justify-between px-3 py-2.5 border-b">
        <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
          地点 ({locations.length})
        </span>
      </div>

      <div className="flex-1 overflow-y-auto overscroll-contain">
        {tree.length === 0 ? (
          <div className="flex items-center justify-center h-full">
            <p className="text-xs text-muted-foreground">暂无地点</p>
          </div>
        ) : (
          tree.map(node => renderNode(node, 0))
        )}
      </div>
    </>
  )
}
