import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Archive,
  BookMarked,
  Check,
  CircleAlert,
  FileText,
  FolderOpen,
  Loader2,
  Plus,
  RefreshCcw,
  Search,
  Trash2,
  X,
} from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reference } from '@/lib/novelist/types'

type Props = {
  novelId: number
  selectedAnchorIds: number[]
  refreshKey: number
  onSelectionChange: (anchorIds: number[]) => void
  onAnchorsChange: (anchors: reference.Anchor[]) => void
  onReferenceMutation: () => void
}

type CreateForm = {
  title: string
  author: string
  sourcePath: string
}

const EMPTY_FORM: CreateForm = {
  title: '',
  author: '',
  sourcePath: '',
}

function sourceKindFromPath(path: string): string {
  const lowerPath = path.toLowerCase()
  return lowerPath.endsWith('.txt') ? 'text' : 'markdown'
}

function titleFromPath(path: string): string {
  const fileName = path.trim().replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? ''
  return fileName.replace(/\.[^.]+$/, '').trim()
}

function anchorState(anchor: reference.Anchor): { label: string; className: string; usable: boolean } {
  if (anchor.status === 'ready' || anchor.status === 'completed') {
    return { label: '已导入', className: 'text-emerald-700 dark:text-emerald-400', usable: true }
  }
  if (anchor.status.startsWith('failed_') || anchor.status === 'cancelled') {
    return { label: '处理失败', className: 'text-destructive', usable: false }
  }
  if (anchor.status === 'queued' || anchor.status === 'running' || anchor.status === 'processing') {
    return { label: '处理中', className: 'text-sky-700 dark:text-sky-300', usable: false }
  }
  return { label: '待处理', className: 'text-amber-700 dark:text-amber-300', usable: false }
}

function isWorkspaceCorpus(anchor: reference.Anchor): boolean {
  return anchor.owner_scope === 'workspace_corpus'
}

export default function ReferenceBookSidebar({
  novelId,
  selectedAnchorIds,
  refreshKey,
  onSelectionChange,
  onAnchorsChange,
  onReferenceMutation,
}: Props) {
  const app = useApp()
  const [anchors, setAnchors] = useState<reference.Anchor[]>([])
  const [query, setQuery] = useState('')
  const [isLoading, setIsLoading] = useState(false)
  const [isCreateOpen, setIsCreateOpen] = useState(false)
  const [form, setForm] = useState<CreateForm>(EMPTY_FORM)
  const [pendingDeleteId, setPendingDeleteId] = useState<number | null>(null)
  const [activeAction, setActiveAction] = useState<'pick' | 'create' | 'delete' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const selectedIdsRef = useRef(selectedAnchorIds)
  const loadSequenceRef = useRef(0)

  useEffect(() => {
    selectedIdsRef.current = selectedAnchorIds
  }, [selectedAnchorIds])

  const loadAnchors = useCallback(async () => {
    const loadSequence = loadSequenceRef.current + 1
    loadSequenceRef.current = loadSequence
    if (!novelId) {
      setIsLoading(false)
      setAnchors([])
      onAnchorsChange([])
      return
    }

    setIsLoading(true)
    setError(null)
    try {
      const nextAnchors = (await app.GetReferenceAnchors(novelId)) ?? []
      if (loadSequence !== loadSequenceRef.current) return
      const validIds = new Set(nextAnchors.map((anchor) => anchor.anchor_id))
      const nextSelectedIds = selectedIdsRef.current.filter((id) => validIds.has(id))
      setAnchors(nextAnchors)
      onAnchorsChange(nextAnchors)
      if (nextSelectedIds.length !== selectedIdsRef.current.length) {
        onSelectionChange(nextSelectedIds)
      }
    } catch {
      if (loadSequence !== loadSequenceRef.current) return
      setError('参考书籍加载失败，请重试。')
    } finally {
      if (loadSequence === loadSequenceRef.current) setIsLoading(false)
    }
  }, [app, novelId, onAnchorsChange, onSelectionChange])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      void loadAnchors()
    }, 0)
    return () => window.clearTimeout(timer)
  }, [loadAnchors, refreshKey])

  const visibleAnchors = useMemo(() => {
    const normalizedQuery = query.trim().toLowerCase()
    if (!normalizedQuery) return anchors
    return anchors.filter((anchor) => [anchor.title, anchor.author, ...anchor.user_tags]
      .join(' ')
      .toLowerCase()
      .includes(normalizedQuery))
  }, [anchors, query])

  const toggleAnchor = (anchor: reference.Anchor) => {
    const state = anchorState(anchor)
    if (!state.usable) return
    const selected = new Set(selectedIdsRef.current)
    if (selected.has(anchor.anchor_id)) {
      selected.delete(anchor.anchor_id)
    } else {
      selected.add(anchor.anchor_id)
    }
    const nextSelectedIds = [...selected]
    selectedIdsRef.current = nextSelectedIds
    onSelectionChange(nextSelectedIds)
  }

  const pickSourceFile = async () => {
    setActiveAction('pick')
    setError(null)
    try {
      const sourcePath = await app.PickReferenceSourceFile()
      if (!sourcePath?.trim()) return
      setForm((current) => ({
        ...current,
        sourcePath,
        title: current.title.trim() || titleFromPath(sourcePath),
      }))
    } catch {
      setError('无法打开文件选择器，请手动填写文件路径。')
    } finally {
      setActiveAction(null)
    }
  }

  const createReferenceBook = async () => {
    const title = form.title.trim()
    const sourcePath = form.sourcePath.trim()
    if (!title || !sourcePath) {
      setError('请填写书名并选择本地文件。')
      return
    }

    setActiveAction('create')
    setError(null)
    try {
      const created = await app.CreateReferenceAnchor({
        novel_id: novelId,
        title,
        author: form.author.trim() || undefined,
        source_path: sourcePath,
        source_kind: sourceKindFromPath(sourcePath),
        license_status: 'user_provided',
        visibility: 'private',
        source_trust: 'user_verified',
        user_tags: [],
      })
      if (anchorState(created).usable) {
        const nextSelectedIds = [...new Set([...selectedIdsRef.current, created.anchor_id])]
        selectedIdsRef.current = nextSelectedIds
        onSelectionChange(nextSelectedIds)
      }
      setForm(EMPTY_FORM)
      setIsCreateOpen(false)
      await loadAnchors()
      onReferenceMutation()
    } catch {
      setError('无法添加参考书籍，请检查文件路径后重试。')
    } finally {
      setActiveAction(null)
    }
  }

  const deleteReferenceBook = async (anchor: reference.Anchor) => {
    setActiveAction('delete')
    setError(null)
    try {
      await app.DeleteReferenceAnchor(novelId, anchor.anchor_id)
      const nextSelectedIds = selectedIdsRef.current.filter((id) => id !== anchor.anchor_id)
      selectedIdsRef.current = nextSelectedIds
      onSelectionChange(nextSelectedIds)
      setPendingDeleteId(null)
      await loadAnchors()
      onReferenceMutation()
    } catch {
      setError('无法删除参考书籍，请稍后重试。')
    } finally {
      setActiveAction(null)
    }
  }

  return (
    <section data-testid="reference-book-sidebar" className="reference-materialization-sidebar flex min-h-0 flex-1 flex-col bg-sidebar" aria-busy={isLoading}>
      <header className="flex items-center gap-2 border-b px-4 py-3">
        <BookMarked className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
        <div className="min-w-0 flex-1">
          <h2 className="text-sm font-semibold text-foreground">参考书籍</h2>
          <p className="mt-0.5 text-xs text-muted-foreground">已选 {selectedAnchorIds.length} 本</p>
        </div>
        <button
          type="button"
          onClick={() => { void loadAnchors() }}
          disabled={isLoading || activeAction !== null}
          className="inline-flex h-7 w-7 items-center justify-center rounded-md text-muted-foreground hover:bg-muted hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
          aria-label="刷新参考书籍"
          title="刷新参考书籍"
        >
          <RefreshCcw className={`h-3.5 w-3.5 ${isLoading ? 'animate-spin' : ''}`} aria-hidden="true" />
        </button>
        <button
          type="button"
          onClick={() => { setError(null); setIsCreateOpen((open) => !open) }}
          disabled={activeAction !== null}
          className="inline-flex h-7 w-7 items-center justify-center rounded-md bg-secondary text-foreground hover:bg-secondary/80 disabled:cursor-not-allowed disabled:opacity-50"
          aria-label="添加参考书籍"
          title="添加参考书籍"
        >
          {isCreateOpen ? <X className="h-3.5 w-3.5" aria-hidden="true" /> : <Plus className="h-3.5 w-3.5" aria-hidden="true" />}
        </button>
      </header>

      <div className="border-b px-3 py-2">
        <label className="relative block">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <span className="sr-only">筛选参考书籍</span>
          <input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            className="h-8 w-full rounded-md border border-border bg-background pl-8 pr-2 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            placeholder="筛选书名、作者或标签"
            aria-label="筛选参考书籍"
          />
        </label>
      </div>

      {isCreateOpen && (
        <div className="space-y-2 border-b bg-muted/30 px-3 py-3">
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-foreground">书名</span>
            <input
              value={form.title}
              onChange={(event) => setForm((current) => ({ ...current, title: event.target.value }))}
              className="h-8 w-full rounded-md border border-border bg-background px-2 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              placeholder="参考书名"
              aria-label="参考书标题"
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-foreground">作者</span>
            <input
              value={form.author}
              onChange={(event) => setForm((current) => ({ ...current, author: event.target.value }))}
              className="h-8 w-full rounded-md border border-border bg-background px-2 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              placeholder="可选"
              aria-label="参考书作者"
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-foreground">本地文件</span>
            <span className="flex gap-1.5">
              <input
                value={form.sourcePath}
                onChange={(event) => setForm((current) => ({ ...current, sourcePath: event.target.value }))}
                className="h-8 min-w-0 flex-1 rounded-md border border-border bg-background px-2 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                placeholder="D:\\books\\reference.md"
                aria-label="参考书文件路径"
              />
              <button
                type="button"
                onClick={() => { void pickSourceFile() }}
                disabled={activeAction !== null}
                className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-border bg-background text-muted-foreground hover:bg-muted hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
                aria-label="选择参考书文件"
                title="选择参考书文件"
              >
                {activeAction === 'pick' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <FolderOpen className="h-3.5 w-3.5" aria-hidden="true" />}
              </button>
            </span>
          </label>
          <button
            type="button"
            onClick={() => { void createReferenceBook() }}
            disabled={activeAction !== null}
            className="inline-flex h-8 w-full items-center justify-center gap-1.5 rounded-md bg-primary px-2 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {activeAction === 'create' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <Plus className="h-3.5 w-3.5" aria-hidden="true" />}
            添加参考书
          </button>
        </div>
      )}

      {error && (
        <div className="mx-3 mt-3 flex items-start gap-2 rounded-md border border-destructive/30 bg-destructive/5 px-2.5 py-2 text-xs text-destructive" role="alert">
          <CircleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
          <span className="min-w-0 break-words">{error}</span>
        </div>
      )}

      <div className="min-h-0 flex-1 overflow-y-auto px-2 py-2">
        {isLoading && anchors.length === 0 ? (
          <div className="space-y-2 px-1 py-2" aria-label="正在加载参考书籍">
            {[0, 1, 2].map((index) => <div key={index} className="h-14 animate-pulse rounded-md bg-muted" />)}
          </div>
        ) : visibleAnchors.length === 0 ? (
          <div className="flex min-h-44 flex-col items-center justify-center px-4 text-center">
            <BookMarked className="h-7 w-7 text-muted-foreground/45" aria-hidden="true" />
            <p className="mt-2 text-xs font-medium text-foreground">{anchors.length === 0 ? '还没有参考书籍' : '没有匹配的参考书籍'}</p>
            <p className="mt-1 text-xs leading-5 text-muted-foreground">{anchors.length === 0 ? '添加一本书后，即可用于蓝图预演。' : '调整筛选条件后再试。'}</p>
          </div>
        ) : (
          <ul className="space-y-1.5" aria-label="参考书籍列表">
            {visibleAnchors.map((anchor) => {
              const state = anchorState(anchor)
              const isSelected = selectedAnchorIds.includes(anchor.anchor_id)
              const deletePending = pendingDeleteId === anchor.anchor_id
              const workspaceCorpus = isWorkspaceCorpus(anchor)
              return (
                <li key={anchor.anchor_id} className="rounded-md border border-border bg-background">
                  <div className="relative">
                    <button
                      type="button"
                      onClick={() => toggleAnchor(anchor)}
                      disabled={!state.usable || activeAction !== null}
                      className={`flex w-full items-start gap-2 rounded-md px-2.5 py-2 pr-9 text-left transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-65 ${
                        isSelected ? 'bg-primary/10' : 'hover:bg-muted/70'
                      }`}
                      aria-pressed={isSelected}
                      aria-label={`${isSelected ? '取消选择' : '选择'}《${anchor.title}》`}
                      title={state.usable ? `${isSelected ? '取消选择' : '选择'}《${anchor.title}》` : `${anchor.title}尚未可用于预演`}
                    >
                      <span className={`mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center rounded border ${isSelected ? 'border-primary bg-primary text-primary-foreground' : 'border-muted-foreground/45 text-transparent'}`} aria-hidden="true">
                        <Check className="h-3 w-3" />
                      </span>
                      <FileText className="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-xs font-medium text-foreground">{anchor.title}</span>
                        <span className="mt-0.5 block truncate text-[11px] text-muted-foreground">{anchor.author.trim() || '未署名'}</span>
                        <span className="mt-1 flex flex-wrap items-center gap-1.5 text-[11px]">
                          <span className={state.className}>{state.label}</span>
                          <span className="rounded bg-muted px-1.5 py-0.5 leading-none text-muted-foreground">
                            {workspaceCorpus ? '工作区语料' : '本小说'}
                          </span>
                        </span>
                      </span>
                    </button>
                    <button
                      type="button"
                      onClick={() => { setError(null); setPendingDeleteId(anchor.anchor_id) }}
                      disabled={activeAction !== null}
                      className="absolute right-1.5 top-1.5 inline-flex h-6 w-6 items-center justify-center rounded-md text-muted-foreground hover:bg-destructive/10 hover:text-destructive disabled:cursor-not-allowed disabled:opacity-50"
                      aria-label={workspaceCorpus ? `归档《${anchor.title}》为受限语料` : `删除《${anchor.title}》`}
                      title={workspaceCorpus ? `归档《${anchor.title}》为受限语料` : `删除《${anchor.title}》`}
                    >
                      {workspaceCorpus
                        ? <Archive className="h-3.5 w-3.5" aria-hidden="true" />
                        : <Trash2 className="h-3.5 w-3.5" aria-hidden="true" />}
                    </button>
                  </div>
                  {deletePending && (
                    <div className="flex items-center justify-between gap-2 border-t border-border bg-muted/45 px-2.5 py-2">
                      <p className="text-xs text-foreground">
                        {workspaceCorpus ? '确认将工作区语料归档为受限？' : '确认删除这本参考书？'}
                      </p>
                      <div className="flex shrink-0 items-center gap-1">
                        <button
                          type="button"
                          onClick={() => setPendingDeleteId(null)}
                          disabled={activeAction !== null}
                          className="h-7 rounded-md px-2 text-xs text-muted-foreground hover:bg-background hover:text-foreground disabled:opacity-50"
                        >
                          取消
                        </button>
                        <button
                          type="button"
                          onClick={() => { void deleteReferenceBook(anchor) }}
                          disabled={activeAction !== null}
                          className="inline-flex h-7 items-center gap-1 rounded-md bg-destructive px-2 text-xs font-medium text-destructive-foreground hover:bg-destructive/90 disabled:cursor-not-allowed disabled:opacity-50"
                          aria-label={workspaceCorpus ? `确认归档《${anchor.title}》` : `确认删除《${anchor.title}》`}
                        >
                          {activeAction === 'delete' ? <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" /> : null}
                          {workspaceCorpus ? '归档' : '删除'}
                        </button>
                      </div>
                    </div>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </div>
    </section>
  )
}
