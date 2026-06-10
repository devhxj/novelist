import { useState, useEffect, useCallback, useMemo } from 'react'
import { Search } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { character } from '@/hooks/useApp'

interface Props {
  novelId: number
}

export default function CharacterList({ novelId }: Props) {
  const app = useApp()

  const [characters, setCharacters] = useState<character.Character[]>([])
  const [search, setSearch] = useState('')

  const load = useCallback(async () => {
    if (!novelId) { setCharacters([]); return }
    const list = await app.GetCharacters(novelId)
    setCharacters(list ?? [])
  }, [novelId, app])

  useEffect(() => { load() }, [load])

  const filtered = useMemo(() => {
    if (!search.trim()) return characters
    const q = search.toLowerCase()
    return characters.filter(c => c.name.toLowerCase().includes(q))
  }, [characters, search])

  return (
    <>
      <div className="flex items-center justify-between px-3 py-2.5 border-b">
        <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
          角色 ({characters.length})
        </span>
      </div>

      <div className="px-2 py-1.5 border-b">
        <div className="relative">
          <Search className="absolute left-2 top-1/2 -translate-y-1/2 w-3 h-3 text-muted-foreground" />
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="搜索角色..."
            className="w-full h-7 rounded-md border bg-background pl-7 pr-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </div>
      </div>

      <div className="flex-1 overflow-y-auto">
        {filtered.length === 0 ? (
          <div className="flex items-center justify-center h-full">
            <p className="text-xs text-muted-foreground">
              {search ? '无匹配角色' : '暂无角色'}
            </p>
          </div>
        ) : (
          filtered.map(c => (
            <div
              key={c.id}
              className="w-full flex items-center gap-2.5 px-3 py-1.5 text-left hover:bg-muted/50 transition-colors"
            >
              <span className="w-5 h-5 rounded-full bg-primary/10 text-primary text-[10px] font-medium flex items-center justify-center shrink-0">
                {c.name.charAt(0)}
              </span>
              <span className="flex-1 text-sm truncate">{c.name}</span>
            </div>
          ))
        )}
      </div>
    </>
  )
}
