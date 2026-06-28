import { useState, useEffect, useCallback, useMemo } from 'react'
import { Search, Settings } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { novel } from '@/hooks/useApp'

interface Props { novelId: number }

export default function SidebarPreferenceList({ novelId }: Props) {
  const app = useApp()

  const [items, setItems] = useState<novel.PreferenceItem[]>([])
  const [search, setSearch] = useState('')

  const load = useCallback(async () => {
    if (!novelId) { setItems([]); return }
    const result = await app.GetPreferences(novelId)
    setItems([...(result.global ?? []), ...(result.novel ?? [])])
  }, [novelId, app])

  useEffect(() => { load() }, [load])

  const filtered = useMemo(() => {
    if (!search.trim()) return items
    const q = search.toLowerCase()
    return items.filter(e => e.content.toLowerCase().includes(q) || e.category.toLowerCase().includes(q))
  }, [items, search])

  return (
    <>
      <div className="flex items-center justify-between px-3 py-2.5 border-b">
        <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
          创作偏好 ({items.length})
        </span>
      </div>
      <div className="px-2 py-1.5 border-b">
        <div className="relative">
          <Search className="absolute left-2 top-1/2 -translate-y-1/2 w-3 h-3 text-muted-foreground" />
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="搜索偏好..."
            className="w-full h-7 rounded-md border bg-background pl-7 pr-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </div>
      </div>
      <div className="flex-1 overflow-y-auto overscroll-contain">
        {filtered.length === 0 ? (
          <div className="flex items-center justify-center h-full">
            <p className="text-xs text-muted-foreground">{search ? '无匹配偏好' : '暂无偏好'}</p>
          </div>
        ) : (
          filtered.map(e => (
            <div key={e.id} className="w-full flex items-center gap-2 px-3 py-1.5 text-left hover:bg-muted/50 transition-colors">
              <span className="shrink-0 flex h-5 w-5 items-center justify-center rounded bg-secondary text-muted-foreground">
                <Settings className="h-3 w-3" />
              </span>
              <div className="flex-1 min-w-0">
                <span className="text-xs truncate block text-foreground">{e.content.length > 30 ? e.content.slice(0, 30) + '…' : e.content}</span>
                <span className="text-[10px] text-muted-foreground">{e.category || '未分类'}{e.is_global ? ' · 全局' : ' · 本书'}</span>
              </div>
            </div>
          ))
        )}
      </div>
    </>
  )
}
