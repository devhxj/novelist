import { useState, useEffect, useCallback, useMemo } from 'react'
import { ChevronRight, FileText, Pencil, Plus, Download, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useApp } from '@/hooks/useApp'
import type { chapter } from '@/hooks/useApp'
import { EventsOn } from '@/lib/novelist/events'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import { pushToast } from '@/lib/toast'
import { outlinePath } from '@/components/content/types'
import type { diagnostics } from '@/lib/novelist/types'

interface Props {
  novelId: number
  target: { path: string; title: string } | null
  onSelectChapter: (ch: chapter.Chapter) => void
  onSelectNovelist: () => void
  onExportNovel: () => void
  /** O15：章节删除成功后回调（携带被删章节），用于关闭编辑器里的对应 tab。 */
  onChapterDeleted?: (ch: chapter.Chapter) => void
}

const BLOCK_SIZE = 100

interface FileChangedEvent {
  novel_id?: number
  path?: string
}

type VisibleError = {
  message: string
  diagnostic?: diagnostics.CopyableDiagnostic | null
}

export default function ChapterList({ novelId, target, onSelectChapter, onSelectNovelist, onExportNovel, onChapterDeleted }: Props) {
  const app = useApp()

  const [chapters, setChapters] = useState<chapter.Chapter[]>([])
  const [chapterTitle, setChapterTitle] = useState('')
  const [showCreateChapter, setShowCreateChapter] = useState(false)
  const [expandedBlocks, setExpandedBlocks] = useState<Set<number>>(new Set())
  const [editingId, setEditingId] = useState<number | null>(null)
  const [editTitle, setEditTitle] = useState('')
  const [error, setError] = useState<VisibleError | null>(null)

  const loadChapters = useCallback(async () => {
    if (!novelId) { setChapters([]); return }
    try {
      const list = await app.GetChapters(novelId)
      setChapters(list ?? [])
      setError(null)
    } catch (err) {
      setError(buildVisibleError(err, '加载章节失败', '加载章节', 'GetChapters', { novel_id: novelId }))
    }
  }, [novelId, app])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      if (!novelId) {
        if (!cancelled) setChapters([])
        return
      }
      try {
        const list = await app.GetChapters(novelId)
        if (!cancelled) {
          setChapters(list ?? [])
          setError(null)
        }
      } catch (err) {
        if (!cancelled) setError(buildVisibleError(err, '加载章节失败', '加载章节', 'GetChapters', { novel_id: novelId }))
      }
    })()
    return () => { cancelled = true }
  }, [app, novelId])

  // file:changed 时刷新章节列表（字数统计、新章等）
  useEffect(() => {
    const unsub = EventsOn('file:changed', (data: FileChangedEvent) => {
      if (data.novel_id !== novelId) return
      if (data.path && (data.path.startsWith('chapters/') || data.path.startsWith('outlines/') || data.path === 'novelist.md')) {
        loadChapters()
      }
    })
    return () => unsub()
  }, [novelId, loadChapters])

  // ── 章节分块 ────────────────────────────────────────────

  const chapterBlocks = useMemo(() => {
    const sorted = [...chapters].sort((a, b) => b.chapter_number - a.chapter_number)
    const blocks: { key: number; start: number; end: number; chs: chapter.Chapter[] }[] = []
    for (let i = 0; i < sorted.length; i += BLOCK_SIZE) {
      const slice = sorted.slice(i, Math.min(i + BLOCK_SIZE, sorted.length))
      slice.sort((a, b) => a.chapter_number - b.chapter_number)
      blocks.push({
        key: i / BLOCK_SIZE,
        start: slice[0].chapter_number,
        end: slice[slice.length - 1].chapter_number,
        chs: slice,
      })
    }
    return blocks
  }, [chapters])

  function toggleBlock(key: number) {
    setExpandedBlocks(prev => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  async function handleCreateChapter() {
    if (!chapterTitle.trim()) return
    try {
      await app.CreateChapter({ novel_id: novelId, title: chapterTitle.trim() })
      setChapterTitle('')
      setShowCreateChapter(false)
      await loadChapters()
    } catch (err) {
      setError(buildVisibleError(err, '创建章节失败', '创建章节', 'CreateChapter', { novel_id: novelId, title: chapterTitle.trim() }))
    }
  }

  async function handleDeleteChapter(ch: chapter.Chapter) {
    // O7：软删除——章号不复用；U14：撤销恢复正文与大纲（以新章号），
    // 因此删除前先把内容读进内存，删除是本地操作，读取不会引入等待。
    if (!confirm(`确定要删除第${ch.chapter_number}章「${ch.title}」吗？\n\n章号不会复用。删除后可在通知里选择「撤销」，正文与大纲将以新章号恢复。`)) return
    try {
      const [content, outline] = await Promise.all([
        app.GetContent(novelId, ch.file_path).catch(() => ''),
        app.GetContent(novelId, outlinePath(ch.chapter_number)).catch(() => ''),
      ])
      await app.DeleteChapter({ novel_id: novelId, chapter_id: ch.id })
      // O15：编辑器里开着的正文/大纲 tab 随删除关闭，残留 tab 的保存会被后端守卫拒绝。
      onChapterDeleted?.(ch)
      await loadChapters()
      pushToast({
        kind: 'info',
        message: `已删除第${ch.chapter_number}章「${ch.title}」`,
        description: '撤销会把正文与大纲恢复为一个新章节（章号不复用）。',
        action: {
          label: '撤销',
          run: () => {
            void (async () => {
              try {
                const created = await app.CreateChapter({ novel_id: novelId, title: ch.title })
                if (content) {
                  await app.SaveContent({ novel_id: novelId, path: created.file_path, content })
                }
                if (outline) {
                  await app.SaveContent({ novel_id: novelId, path: outlinePath(created.chapter_number), content: outline })
                }
                await loadChapters()
                pushToast({
                  kind: 'success',
                  message: `已恢复「${ch.title}」为第${created.chapter_number}章，正文与大纲已还原。`,
                })
              } catch (err) {
                setError(buildVisibleError(err, '撤销删除失败', '撤销删除', 'CreateChapter', { novel_id: novelId }))
              }
            })()
          },
        },
      })
    } catch (err) {
      setError(buildVisibleError(err, '删除章节失败', '删除章节', 'DeleteChapter', {
        novel_id: novelId,
        chapter_id: ch.id,
      }))
    }
  }

  function startEdit(ch: chapter.Chapter) {
    setEditingId(ch.id)
    setEditTitle(ch.title)
  }

  async function commitEdit() {
    if (editingId == null) return
    const ch = chapters.find(c => c.id === editingId)
    if (!ch) return
    const newTitle = editTitle.trim()
    try {
      if (newTitle && newTitle !== ch.title) {
        await app.UpdateChapterTitle(novelId, ch.chapter_number, newTitle)
        await loadChapters()
      }
      setEditingId(null)
    } catch (err) {
      setError(buildVisibleError(err, '重命名章节失败', '重命名章节', 'UpdateChapterTitle', {
        novel_id: novelId,
        chapter_number: ch.chapter_number,
        title: newTitle,
      }))
    }
  }

  function cancelEdit() {
    setEditingId(null)
  }

  return (
    <>
      <div className="flex items-center justify-between px-3 py-2.5 border-b">
        <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
          章节 ({chapters.length})
        </span>
        <div className="flex items-center gap-0.5">
          <button
            onClick={onExportNovel}
            aria-label="导出作品"
            className="w-6 h-6 flex items-center justify-center rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
            title="导出"
          >
            <Download className="w-3.5 h-3.5" />
          </button>
          <button
            onClick={() => setShowCreateChapter(true)}
            aria-label="新建章节"
            className="w-6 h-6 flex items-center justify-center rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
          >
            <Plus className="w-4 h-4" />
          </button>
        </div>
      </div>

      {showCreateChapter && (
        <div className="p-3 border-b space-y-2">
          <input
            type="text" value={chapterTitle} autoFocus
            onChange={e => setChapterTitle(e.target.value)}
            onKeyDown={e => e.key === 'Enter' && handleCreateChapter()}
            placeholder="章节标题"
            className="w-full h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
          <div className="flex gap-2">
            <Button size="sm" onClick={handleCreateChapter}>添加</Button>
            <Button size="sm" variant="ghost" onClick={() => { setShowCreateChapter(false); setChapterTitle('') }}>取消</Button>
          </div>
        </div>
      )}

      {error && (
        <div className="border-b p-2">
          <ErrorCallout
            compact
            message={error.message}
            diagnostic={error.diagnostic}
            onRetry={() => { void loadChapters() }}
            onClose={() => setError(null)}
          />
        </div>
      )}

      <button
        onClick={onSelectNovelist}
        className={`w-full flex items-center gap-2.5 px-3 py-1.5 text-left hover:bg-muted/50 transition-colors relative border-b border-border/50
          ${target?.path === 'novelist.md' ? 'bg-primary/10 font-medium' : ''}`}
      >
        {target?.path === 'novelist.md' && (
          <span className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 bg-primary rounded-r-full" />
        )}
        <FileText className="w-3.5 h-3.5 text-muted-foreground shrink-0" />
        <span className="flex-1 text-sm truncate">故事状态</span>
      </button>

      <div className="flex-1 overflow-y-auto overscroll-contain">
        {chapters.length === 0 ? (
          <div className="flex items-center justify-center h-full">
            <div className="text-center">
              <FileText className="w-8 h-8 text-muted-foreground/30 mx-auto mb-2" />
              <p className="text-xs text-muted-foreground">暂无章节</p>
              <p className="text-xs text-muted-foreground/60 mt-0.5">点击 + 创建第一章</p>
            </div>
          </div>
        ) : (
          chapterBlocks.map(block => {
            const isExpanded = expandedBlocks.has(block.key)
            const range = block.start === block.end
              ? `第 ${block.start} 章`
              : `第 ${block.start} - ${block.end} 章`
            return (
              <div key={block.key}>
                <button
                  onClick={() => toggleBlock(block.key)}
                  className="w-full flex items-center gap-1.5 px-3 py-1.5 text-left hover:bg-muted/30 transition-colors border-b border-border/50"
                >
                  <ChevronRight
                    className={`w-3.5 h-3.5 text-muted-foreground shrink-0 transition-transform duration-200 ${isExpanded ? 'rotate-90' : ''}`}
                  />
                  <span className="text-xs text-muted-foreground">{range}</span>
                  <span className="text-[10px] text-muted-foreground/50 ml-auto">{block.chs.length} 章</span>
                </button>
                {isExpanded && (
                  <div>
                    {block.chs.map(ch => (
                      <div
                        key={ch.id}
                        className="group flex items-center w-full relative"
                      >
                        <button
                          onClick={() => onSelectChapter(ch)}
                          className={`flex items-center gap-2.5 pl-7 pr-2 py-1.5 text-left hover:bg-muted/50 transition-colors flex-1 min-w-0
                            ${target?.path === ch.file_path ? 'bg-primary/10 font-medium' : ''}`}
                        >
                          {target?.path === ch.file_path && (
                            <span className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 bg-primary rounded-r-full" />
                          )}
                          <span className="text-xs text-muted-foreground w-8 shrink-0 tabular-nums">
                            第{ch.chapter_number}章
                          </span>
                          {editingId === ch.id ? (
                            <input
                              value={editTitle}
                              onChange={e => setEditTitle(e.target.value)}
                              onKeyDown={e => {
                                if (e.key === 'Enter') commitEdit()
                                if (e.key === 'Escape') cancelEdit()
                              }}
                              onBlur={commitEdit}
                              autoFocus
                              onClick={e => e.stopPropagation()}
                              className="flex-1 h-6 rounded border bg-background px-1.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                            />
                          ) : (
                            <span className="flex-1 text-sm truncate">{ch.title}</span>
                          )}
                          {ch.word_count > 0 && editingId !== ch.id && (
                            <span className="text-[10px] text-muted-foreground/60 shrink-0">
                              {ch.word_count}字
                            </span>
                          )}
                        </button>
                          {editingId !== ch.id && (
                            <>
                              <button
                                onClick={e => { e.stopPropagation(); startEdit(ch) }}
                                aria-label={`编辑章节 ${ch.title}`}
                                className="shrink-0 w-6 h-6 flex items-center justify-center rounded opacity-0 group-hover:opacity-100 hover:bg-muted text-muted-foreground hover:text-foreground transition-all"
                              >
                                <Pencil className="w-3 h-3" />
                              </button>
                              <button
                                onClick={e => { e.stopPropagation(); void handleDeleteChapter(ch) }}
                                aria-label={`删除章节 ${ch.title}`}
                                data-testid={`delete-chapter-${ch.id}`}
                                className="shrink-0 w-6 h-6 flex items-center justify-center rounded opacity-0 group-hover:opacity-100 hover:bg-destructive/10 text-muted-foreground hover:text-destructive transition-all mr-1"
                              >
                                <Trash2 className="w-3 h-3" />
                              </button>
                            </>
                          )}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )
          })
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
