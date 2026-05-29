import { useState, useEffect, useCallback } from 'react'
import { useApp } from '@/hooks/useApp'
import type { novel } from '@/hooks/useApp'
import NovelCard from '@/components/novel/NovelCard'
import { Button } from '@/components/ui/button'
import { Plus } from 'lucide-react'

export default function NovelListView() {
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
      <header className="flex items-center justify-between px-8 py-4 border-b">
        <h1 className="text-lg font-semibold">Goink</h1>
        <button className="text-muted-foreground hover:text-foreground transition-colors text-sm">
          设置
        </button>
      </header>

      {/* 内容区 */}
      <main className="flex-1 flex items-center justify-center p-8">
        <div className="w-full max-w-4xl">
          {novels.length === 0 && !showCreate ? (
            <div className="text-center">
              <p className="text-muted-foreground mb-4">还没有小说，创建你的第一部作品</p>
              <Button onClick={() => setShowCreate(true)}>
                <Plus className="w-4 h-4 mr-1" />
                创建小说
              </Button>
            </div>
          ) : (
            <div className="grid grid-cols-3 sm:grid-cols-4 md:grid-cols-5 gap-6">
              {novels.map((n) => (
                <NovelCard
                  key={n.id}
                  novel={n}
                  onClick={() => {
                    // TODO: 翻书动画 → NovelHubView
                  }}
                />
              ))}
              {/* 创建按钮 */}
              {!showCreate ? (
                <button
                  onClick={() => setShowCreate(true)}
                  className="group text-left w-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-lg"
                >
                  <div className="w-full aspect-[3/4] rounded-md border-2 border-dashed border-border flex items-center justify-center group-hover:border-primary/50 transition-colors">
                    <Plus className="w-8 h-8 text-muted-foreground group-hover:text-primary transition-colors" />
                  </div>
                  <p className="mt-2 text-sm text-muted-foreground">新建</p>
                </button>
              ) : (
                /* 创建表单卡片 */
                <div className="w-full">
                  <div className="w-full aspect-[3/4] rounded-md border-2 border-primary flex flex-col items-center justify-center p-3 gap-2 bg-primary/5">
                    <input
                      type="text"
                      value={title}
                      onChange={(e) => setTitle(e.target.value)}
                      placeholder="书名"
                      autoFocus
                      className="w-full h-8 rounded border border-input bg-background px-2 text-sm text-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    />
                    <input
                      type="text"
                      value={description}
                      onChange={(e) => setDescription(e.target.value)}
                      placeholder="简介（可选）"
                      className="w-full h-8 rounded border border-input bg-background px-2 text-xs text-center focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                    />
                    <div className="flex gap-1 mt-1">
                      <Button size="sm" onClick={handleCreate}>
                        创建
                      </Button>
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => setShowCreate(false)}
                      >
                        取消
                      </Button>
                    </div>
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      </main>
    </div>
  )
}
