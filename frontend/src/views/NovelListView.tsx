import { useState, useEffect, useCallback } from 'react'
import { useApp } from '@/hooks/useApp'
import type { novel } from '@/hooks/useApp'
import NovelCard from '@/components/novel/NovelCard'
import { Button } from '@/components/ui/button'
import { Plus, Settings, BookOpen } from 'lucide-react'

interface Props {
  onNovelClick: (novel: novel.Novel) => void
}

export default function NovelListView({ onNovelClick }: Props) {
  const app = useApp()
  const [novels, setNovels] = useState<novel.Novel[]>([])
  const [showCreate, setShowCreate] = useState(false)
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')

  const refresh = useCallback(async () => {
    const list = await app.GetNovels()
    setNovels(list ?? [])
  }, [])

  useEffect(() => {
    refresh()
  }, [refresh])

  async function handleCreate() {
    if (!title.trim()) return
    await app.CreateNovel({ title: title.trim(), description: description.trim() })
    setTitle('')
    setDescription('')
    setShowCreate(false)
    refresh()
  }

  return (
    <div className="min-h-screen flex flex-col">
      {/* 顶部栏 */}
      <header className="flex items-center justify-between px-10 pt-8 pb-6">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Goink</h1>
          <p className="text-sm text-muted-foreground mt-1">AI 辅助小说创作</p>
        </div>
        <button className="w-9 h-9 flex items-center justify-center rounded-lg text-muted-foreground hover:text-foreground hover:bg-muted transition-colors">
          <Settings className="w-5 h-5" />
        </button>
      </header>

      {/* 内容区 */}
      <main className="flex-1 flex items-center justify-center px-10 pb-12">
        <div className="w-full max-w-5xl">
          {novels.length === 0 && !showCreate ? (
            /* 空状态 */
            <div className="flex flex-col items-center gap-5 py-20">
              <div className="w-20 h-20 rounded-2xl bg-muted/50 flex items-center justify-center">
                <BookOpen className="w-10 h-10 text-muted-foreground/30" />
              </div>
              <div className="text-center">
                <h2 className="text-lg font-medium">开始你的第一部作品</h2>
                <p className="text-sm text-muted-foreground mt-1.5 max-w-xs leading-relaxed">
                  AI 辅助写作，从大纲到完稿，让创作更高效
                </p>
              </div>
              <Button size="lg" onClick={() => setShowCreate(true)} className="mt-2">
                <Plus className="w-4 h-4 mr-1.5" />
                创建小说
              </Button>
            </div>
          ) : (
            <>
              {/* 作品计数 */}
              <div className="flex items-center justify-between mb-6">
                <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
                  作品 ({novels.length})
                </span>
              </div>

              <div className="grid grid-cols-3 sm:grid-cols-4 md:grid-cols-5 xl:grid-cols-6 gap-6">
                {novels.map((n) => (
                  <NovelCard
                    key={n.id}
                    novel={n}
                    onClick={() => onNovelClick(n)}
                  />
                ))}

                {/* 创建卡片 */}
                {!showCreate ? (
                  <button
                    onClick={() => setShowCreate(true)}
                    className="group text-left w-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-xl transition-transform duration-300 ease-out hover:scale-[1.03]"
                  >
                    <div className="w-full aspect-[3/4] rounded-lg border-2 border-dashed border-border flex items-center justify-center transition-colors duration-300 group-hover:border-primary/40 group-hover:bg-primary/[0.02]">
                      <Plus className="w-8 h-8 text-muted-foreground/50 group-hover:text-primary transition-colors duration-300" />
                    </div>
                    <p className="mt-2.5 text-sm text-muted-foreground">新建</p>
                  </button>
                ) : (
                  /* 创建表单 */
                  <div className="w-full">
                    <div className="w-full aspect-[3/4] rounded-lg border-2 border-primary bg-primary/[0.04] flex flex-col items-center justify-center p-4 gap-2.5">
                      <input
                        type="text"
                        value={title}
                        onChange={(e) => setTitle(e.target.value)}
                        placeholder="书名"
                        autoFocus
                        className="w-full h-9 rounded-lg border border-input bg-background px-3 text-sm text-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring placeholder:text-muted-foreground/50"
                      />
                      <input
                        type="text"
                        value={description}
                        onChange={(e) => setDescription(e.target.value)}
                        placeholder="简介（可选）"
                        className="w-full h-9 rounded-lg border border-input bg-background px-3 text-xs text-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring placeholder:text-muted-foreground/50"
                      />
                      <div className="flex gap-2 mt-1">
                        <Button size="sm" onClick={handleCreate}>
                          创建
                        </Button>
                        <Button
                          size="sm"
                          variant="ghost"
                          onClick={() => {
                            setShowCreate(false)
                            setTitle('')
                            setDescription('')
                          }}
                        >
                          取消
                        </Button>
                      </div>
                    </div>
                  </div>
                )}
              </div>
            </>
          )}
        </div>
      </main>
    </div>
  )
}
