import { useEffect, useRef, useState, useCallback } from 'react'
import { MessageSquare, Loader2, History } from 'lucide-react'
import type { app } from '@/hooks/useApp'
import { useApp } from '@/hooks/useApp'

interface Props {
  open: boolean
  novelId: number
  onClose: () => void
  onSelectSession: (sessionId: string) => void
}

function timeAgo(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime()
  const min = Math.floor(diff / 60000)
  if (min < 1) return '刚刚'
  if (min < 60) return `${min} 分钟前`
  const hour = Math.floor(min / 60)
  if (hour < 24) return `${hour} 小时前`
  const day = Math.floor(hour / 24)
  if (day < 30) return `${day} 天前`
  return `${Math.floor(day / 30)} 个月前`
}

export default function SessionHistory({ open, novelId, onClose, onSelectSession }: Props) {
  const app = useApp()
  const [mounted, setMounted] = useState(false)
  const [visible, setVisible] = useState(false)
  const [sessions, setSessions] = useState<app.SessionMeta[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [isLoading, setIsLoading] = useState(false)
  const [hasMore, setHasMore] = useState(true)
  const [search, setSearch] = useState('')
  const listRef = useRef<HTMLDivElement>(null)
  const loadingRef = useRef(false)
  const searchRef = useRef('')

  const loadPage = useCallback(async (p: number) => {
    if (loadingRef.current) return
    loadingRef.current = true
    setIsLoading(true)
    try {
      const result = await app.GetSessions({ novel_id: novelId, page: p, size: 20, search: searchRef.current })
      if (result?.items) {
        setSessions(prev => p === 1 ? result.items : [...prev, ...result.items])
        setTotal(result.total)
        setHasMore(result.page < result.total_pages)
      }
    } catch {
      // Session history is non-critical; keep the panel usable on transient failures.
    } finally {
      setIsLoading(false)
      loadingRef.current = false
    }
  }, [app, novelId])

  useEffect(() => {
    let frame = 0
    let secondFrame = 0
    let timer: ReturnType<typeof setTimeout> | null = null
    if (open) {
      frame = requestAnimationFrame(() => {
        setMounted(true)
        secondFrame = requestAnimationFrame(() => setVisible(true))
      })
    } else {
      frame = requestAnimationFrame(() => setVisible(false))
      timer = setTimeout(() => setMounted(false), 200)
    }
    return () => {
      if (frame) cancelAnimationFrame(frame)
      if (secondFrame) cancelAnimationFrame(secondFrame)
      if (timer) clearTimeout(timer)
    }
  }, [open])

  // 搜索防抖 300ms
  useEffect(() => {
    const timer = setTimeout(() => {
      if (searchRef.current !== search) {
        searchRef.current = search
        setSessions([])
        setPage(1)
        setHasMore(true)
        loadPage(1)
      }
    }, 300)
    return () => clearTimeout(timer)
  }, [loadPage, search])

  useEffect(() => {
    if (!open) return
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      if (cancelled) return
      setSearch('')
      searchRef.current = ''
      setSessions([])
      setPage(1)
      setHasMore(true)
      await loadPage(1)
    })()
    return () => { cancelled = true }
  }, [loadPage, open, novelId])

  const handleScroll = useCallback(() => {
    if (!listRef.current || !hasMore || isLoading) return
    const { scrollTop, scrollHeight, clientHeight } = listRef.current
    if (scrollHeight - scrollTop - clientHeight < 80) {
      const next = page + 1
      setPage(next)
      loadPage(next)
    }
  }, [hasMore, isLoading, loadPage, page])

  if (!mounted) return null

  return (
    <div className="absolute inset-0 pointer-events-none">
      <div className="absolute inset-0 z-30 pointer-events-auto" onClick={onClose} />
      <div className={`absolute right-3 left-3 z-40 flex flex-col bg-card border rounded-xl shadow-lg pointer-events-auto transition-all duration-200 ease-out overflow-hidden ${visible ? 'opacity-100 translate-y-0' : 'opacity-0 -translate-y-2'}`}
        style={{ height: '40%', top: '4px' }}>
      <div className="flex items-center justify-between px-4 py-2 border-b shrink-0">
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2">
            <History className="w-4 h-4 text-muted-foreground" />
            <span className="text-xs font-medium">历史会话</span>
          </div>
          {total > 0 && (
            <span className="text-[10px] text-muted-foreground">共 {total} 个</span>
          )}
        </div>
      </div>

      <div className="px-4 py-2 shrink-0">
        <input
          value={search}
          onChange={e => setSearch(e.target.value)}
          placeholder="搜索会话..."
          className="w-full h-7 rounded-md border bg-muted/30 px-2.5 text-xs"
        />
      </div>

      {/* 会话列表 */}
      <div
        ref={listRef}
        onScroll={handleScroll}
        className="flex-1 overflow-y-auto overscroll-contain px-3 pb-2"
      >
        {sessions.length === 0 && isLoading ? (
          <div className="flex items-center justify-center h-full">
            <Loader2 className="w-4 h-4 animate-spin text-muted-foreground" />
          </div>
        ) : sessions.length === 0 && search.trim() ? (
          <div className="flex items-center justify-center h-full">
            <span className="text-xs text-muted-foreground">无匹配会话</span>
          </div>
        ) : (
          <div className="space-y-0.5">
            {sessions.map(s => (
              <button
                key={s.session_id}
                onClick={() => { onSelectSession(s.session_id); onClose() }}
                className="w-full flex items-center gap-2.5 px-2.5 py-2.5 rounded-lg text-left hover:bg-muted/50 transition-colors cursor-pointer select-none"
              >
                <MessageSquare className="w-4 h-4 shrink-0 text-muted-foreground" />
                <div className="min-w-0 flex-1">
                  <div className="text-xs truncate">{s.title || '新对话'}</div>
                  <div className="text-[10px] text-muted-foreground mt-0.5">{timeAgo(s.updated_at)}</div>
                </div>
              </button>
            ))}
            {isLoading && (
              <div className="flex justify-center py-3">
                <Loader2 className="w-4 h-4 animate-spin text-muted-foreground" />
              </div>
            )}
            {!hasMore && sessions.length > 0 && (
              <div className="text-center text-[10px] text-muted-foreground py-2">已显示全部会话</div>
            )}
          </div>
        )}
      </div>
    </div>
    </div>
  )
}
