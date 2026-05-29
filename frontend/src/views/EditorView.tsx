import { useState, useEffect, useCallback, useRef } from 'react'
import { useApp } from '@/hooks/useApp'
import type { novel } from '@/hooks/useApp'
import ActivityBar from '@/components/shell/ActivityBar'
import StatusBar from '@/components/shell/StatusBar'
import BookCover from '@/components/novel/BookCover'
import { FileText, Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'

interface Props {
  initialNovelId: number
}

export default function EditorView({ initialNovelId }: Props) {
  const app = useApp()
  const [novels, setNovels] = useState<novel.Novel[]>([])
  const [activeNovelId, setActiveNovelId] = useState(initialNovelId)
  const [activePanel, setActivePanel] = useState(initialNovelId ? 'chapters' : 'novels')
  const [showCreate, setShowCreate] = useState(false)
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const loadedRef = useRef(false)

  const loadNovels = useCallback(async () => {
    const list = await app.GetNovels()
    setNovels(list ?? [])
    loadedRef.current = true
  }, [])

  useEffect(() => { loadNovels() }, [loadNovels])

  // 自动选择活跃小说
  useEffect(() => {
    if (!loadedRef.current) return
    const exists = novels.find(n => n.id === activeNovelId)
    if (!exists && novels.length > 0) {
      const first = novels[0]
      setActiveNovelId(first.id)
      setActivePanel('chapters')
      app.SetActiveNovel({ novel_id: first.id })
    } else if (novels.length === 0) {
      setActivePanel('novels')
    }
  }, [novels, activeNovelId])

  async function handleSelectNovel(n: novel.Novel) {
    setActiveNovelId(n.id)
    setActivePanel('chapters')
    await app.SetActiveNovel({ novel_id: n.id })
  }

  async function handleCreate() {
    if (!title.trim()) return
    const n = await app.CreateNovel({ title: title.trim(), description: description.trim() })
    if (n) {
      setTitle('')
      setDescription('')
      setShowCreate(false)
      await loadNovels()
      setActiveNovelId(n.id)
      setActivePanel('chapters')
      await app.SetActiveNovel({ novel_id: n.id })
    }
  }

  const activeNovel = novels.find(n => n.id === activeNovelId)

  return (
    <div className="h-screen flex flex-col">
      {/* 工具栏 */}
      <header className="h-11 flex items-center px-4 border-b bg-muted/10 shrink-0">
        <span className="text-sm font-medium">
          {activeNovel?.title ?? 'Goink'}
        </span>
      </header>

      <div className="flex-1 flex min-h-0">
        <ActivityBar activeId={activePanel} onSelect={setActivePanel} />

        {/* 左面板 */}
        <aside className="w-56 border-r bg-background flex flex-col shrink-0">
          {activePanel === 'novels' ? (
            <>
              <div className="flex items-center justify-between px-3 py-2.5 border-b">
                <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
                  作品 ({novels.length})
                </span>
                <button
                  onClick={() => setShowCreate(true)}
                  className="w-6 h-6 flex items-center justify-center rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
                >
                  <Plus className="w-4 h-4" />
                </button>
              </div>

              {showCreate && (
                <div className="p-3 border-b space-y-2">
                  <input
                    type="text" value={title} autoFocus
                    onChange={e => setTitle(e.target.value)}
                    placeholder="书名"
                    className="w-full h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  />
                  <input
                    type="text" value={description}
                    onChange={e => setDescription(e.target.value)}
                    placeholder="简介（可选）"
                    className="w-full h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  />
                  <div className="flex gap-2">
                    <Button size="sm" onClick={handleCreate}>创建</Button>
                    <Button size="sm" variant="ghost" onClick={() => { setShowCreate(false); setTitle(''); setDescription('') }}>取消</Button>
                  </div>
                </div>
              )}

              <div className="flex-1 overflow-y-auto">
                {novels.map(n => {
                  return (
                    <button
                      key={n.id}
                      onClick={() => handleSelectNovel(n)}
                      className={`w-full flex items-center gap-3 px-3 py-2.5 text-left hover:bg-muted/50 transition-colors
                        ${n.id === activeNovelId ? 'bg-muted/30' : ''}`}
                    >
                      <div className="w-8 shrink-0 rounded-sm overflow-hidden">
                        <BookCover />
                      </div>
                      <span className="flex-1 text-sm truncate">{n.title}</span>
                      {n.id === activeNovelId && (
                        <span className="w-1.5 h-1.5 rounded-full bg-primary shrink-0" />
                      )}
                    </button>
                  )
                })}
              </div>
            </>
          ) : activePanel === 'chapters' ? (
            <>
              <div className="flex items-center justify-between px-3 py-2.5 border-b">
                <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
                  章节列表
                </span>
                <button className="w-6 h-6 flex items-center justify-center rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors">
                  <Plus className="w-4 h-4" />
                </button>
              </div>
              <div className="flex-1 flex items-center justify-center">
                <div className="text-center">
                  <FileText className="w-8 h-8 text-muted-foreground/30 mx-auto mb-2" />
                  <p className="text-xs text-muted-foreground">暂无章节</p>
                </div>
              </div>
            </>
          ) : (
            <div className="flex-1 flex items-center justify-center">
              <p className="text-xs text-muted-foreground">即将推出</p>
            </div>
          )}
        </aside>

        {/* 编辑区 */}
        <main className="flex-1 bg-background flex items-center justify-center border-r">
          {novels.length === 0 ? (
            <div className="text-center">
              <FileText className="w-16 h-16 text-muted-foreground/15 mx-auto mb-4" />
              <h2 className="text-base font-medium text-foreground mb-1">开始你的第一部作品</h2>
              <p className="text-sm text-muted-foreground mb-4">点击左侧书架图标创建小说</p>
              <Button size="sm" onClick={() => setActivePanel('novels')}>前往书架</Button>
            </div>
          ) : (
            <div className="text-center">
              <FileText className="w-12 h-12 text-muted-foreground/20 mx-auto mb-3" />
              <p className="text-sm text-muted-foreground">选择或创建章节开始写作</p>
            </div>
          )}
        </main>

        {/* 聊天区 */}
        <aside className="w-80 bg-muted/10 flex flex-col shrink-0">
          <div className="px-4 py-2.5 border-b">
            <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">AI 对话</span>
          </div>
          <div className="flex-1 flex items-center justify-center">
            <p className="text-xs text-muted-foreground">选择章节后开始对话</p>
          </div>
          <div className="p-3 border-t">
            <div className="flex items-center gap-2">
              <input type="text" placeholder="输入消息..." disabled
                className="flex-1 h-8 rounded-md border bg-background px-3 text-xs text-muted-foreground" />
              <button disabled className="w-8 h-8 flex items-center justify-center rounded-md bg-muted text-muted-foreground/50">→</button>
            </div>
          </div>
        </aside>
      </div>

      <StatusBar />
    </div>
  )
}
