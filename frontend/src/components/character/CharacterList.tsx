import { useState, useEffect, useCallback, useMemo } from 'react'
import { Search, Trash2 } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { character } from '@/hooks/useApp'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics } from '@/lib/novelist/types'

interface Props {
  novelId: number
}

type VisibleError = {
  message: string
  diagnostic?: diagnostics.CopyableDiagnostic | null
}

export default function CharacterList({ novelId }: Props) {
  const app = useApp()

  const [characters, setCharacters] = useState<character.Character[]>([])
  const [search, setSearch] = useState('')
  const [error, setError] = useState<VisibleError | null>(null)

  const load = useCallback(async () => {
    if (!novelId) { setCharacters([]); return }
    try {
      const list = await app.GetCharacters(novelId)
      setCharacters(list ?? [])
      setError(null)
    } catch (err) {
      setError(buildVisibleError(err, '加载角色失败', '加载角色', 'GetCharacters', { novel_id: novelId }))
    }
  }, [novelId, app])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      if (!novelId) {
        if (!cancelled) setCharacters([])
        return
      }
      try {
        const list = await app.GetCharacters(novelId)
        if (!cancelled) {
          setCharacters(list ?? [])
          setError(null)
        }
      } catch (err) {
        if (!cancelled) setError(buildVisibleError(err, '加载角色失败', '加载角色', 'GetCharacters', { novel_id: novelId }))
      }
    })()
    return () => { cancelled = true }
  }, [app, novelId])

  const filtered = useMemo(() => {
    if (!search.trim()) return characters
    const q = search.toLowerCase()
    return characters.filter(c => c.name.toLowerCase().includes(q))
  }, [characters, search])

  async function handleDelete(charId: number) {
    if (!confirm('确定要删除该角色吗？关联的关系记录也会被删除。')) return
    try {
      await app.DeleteCharacter(novelId, charId)
      await load()
    } catch (err) {
      setError(buildVisibleError(err, '删除角色失败', '删除角色', 'DeleteCharacter', { novel_id: novelId, character_id: charId }))
    }
  }

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
            placeholder="搜索角色..." aria-label="搜索角色"
            className="w-full h-7 rounded-md border bg-background pl-7 pr-2 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </div>
      </div>

      {error && (
        <div className="border-b px-2 py-2">
          <ErrorCallout
            compact
            message={error.message}
            diagnostic={error.diagnostic}
            onRetry={() => { void load() }}
            onClose={() => setError(null)}
          />
        </div>
      )}

      <div className="flex-1 overflow-y-auto overscroll-contain">
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
              className="w-full flex items-center gap-2.5 px-3 py-1.5 text-left hover:bg-muted/50 transition-colors group"
            >
              <span className="w-5 h-5 rounded-full bg-tag-blue text-tag-blue-foreground text-[10px] font-medium flex items-center justify-center shrink-0">
                {c.name.charAt(0)}
              </span>
              <span className="flex-1 text-sm truncate">{c.name}</span>
              <button
                onClick={(e) => { e.stopPropagation(); handleDelete(c.id) }}
                className="shrink-0 p-0.5 rounded text-muted-foreground hover:text-destructive opacity-0 group-hover:opacity-100 transition-opacity"
                title="删除"
              >
                <Trash2 className="h-3 w-3" />
              </button>
            </div>
          ))
        )}
      </div>
    </>
  )
}

function buildVisibleError(
  error: unknown,
  fallbackMessage: string,
  operation: string,
  bridgeMethod: string,
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
