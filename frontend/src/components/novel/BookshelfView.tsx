import { useMemo, useState, useRef } from 'react'
import { Plus, Pencil, Trash2, BookOpen, Camera, Download, Search } from 'lucide-react'
import BookCover from '@/components/sidebar/BookCover'
import type { novel } from '@/hooks/useApp'

interface Props {
  novels: novel.Novel[]
  activeNovelId: number
  onSelectNovel: (n: novel.Novel) => void
  onEditNovel: (n: novel.Novel) => void
  onDeleteNovel: (n: novel.Novel) => void
  onCreateNovel: () => void
  onSaveCover: (novelID: number, file: File) => Promise<void>
  onExportNovel: (n: novel.Novel) => void
}

export default function BookshelfView({
  novels, activeNovelId,
  onSelectNovel, onEditNovel, onDeleteNovel, onCreateNovel,
  onSaveCover, onExportNovel,
}: Props) {
  const [coverKeys, setCoverKeys] = useState<Record<number, number>>({})
  const [searchQuery, setSearchQuery] = useState('')
  const fileInputRef = useRef<HTMLInputElement>(null)
  const uploadingRef = useRef<number | null>(null)
  const filteredNovels = useMemo(() => {
    const query = searchQuery.trim().toLowerCase()
    if (!query) return novels

    return novels.filter(novel =>
      novel.title.toLowerCase().includes(query) ||
      (novel.genre ?? '').toLowerCase().includes(query) ||
      (novel.description ?? '').toLowerCase().includes(query),
    )
  }, [novels, searchQuery])

  function handleCoverClick(novelID: number, e: React.MouseEvent) {
    e.stopPropagation()
    uploadingRef.current = novelID
    fileInputRef.current?.click()
  }

  async function handleFileChange(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (!file || uploadingRef.current == null) return
    const novelID = uploadingRef.current
    uploadingRef.current = null
    // 清空 input 以便重复选同一文件
    e.target.value = ''
    await onSaveCover(novelID, file)
    setCoverKeys(prev => ({ ...prev, [novelID]: (prev[novelID] ?? 0) + 1 }))
  }

  return (
    <div className="flex-1 flex flex-col min-h-0 bg-background">
      {/* 隐藏文件选择器 */}
      <input
        ref={fileInputRef}
        type="file" accept="image/*"
        className="hidden"
        onChange={handleFileChange}
      />

      {/* 顶部工具栏 */}
      <div className="flex items-center justify-between gap-3 px-6 py-4 border-b shrink-0">
        <div className="flex min-w-0 flex-1 items-center gap-3">
          <span className="shrink-0 text-sm text-muted-foreground">
            共 <b className="text-foreground">{novels.length}</b> 部作品
          </span>
          {novels.length > 0 && (
            <div className="relative w-full max-w-xs">
              <Search className="absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
              <input
                type="search"
                value={searchQuery}
                onChange={event => setSearchQuery(event.target.value)}
                placeholder="搜索作品、分类或简介..."
                className="h-8 w-full rounded-md border bg-background pl-8 pr-2 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
            </div>
          )}
        </div>
        <button
          onClick={onCreateNovel}
          className="inline-flex h-8 shrink-0 items-center gap-1.5 rounded-md bg-primary px-3 text-sm text-primary-foreground transition-opacity hover:opacity-90"
        >
          <Plus className="w-4 h-4" />
          新建作品
        </button>
      </div>

      {/* 空状态 */}
      {novels.length === 0 ? (
        <div className="flex-1 flex flex-col items-center justify-center text-muted-foreground gap-3">
          <BookOpen className="w-12 h-12 opacity-30" />
          <p className="text-sm">还没有作品，创建第一部吧</p>
        </div>
      ) : filteredNovels.length === 0 ? (
        <div className="flex-1 flex flex-col items-center justify-center text-muted-foreground gap-3">
          <BookOpen className="w-12 h-12 opacity-30" />
          <p className="text-sm">没有匹配的作品</p>
        </div>
      ) : (
        /* 书架网格 */
        <div className="flex-1 overflow-y-auto overscroll-contain p-6">
          <div className="grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-5">
            {filteredNovels.map(n => (
              <div
                key={n.id}
                role="article"
                aria-label={`作品卡片 ${n.title}`}
                className={`group relative flex flex-col rounded-lg border bg-card hover:shadow-md transition-shadow cursor-pointer select-none
                  ${n.id === activeNovelId ? 'ring-2 ring-primary' : ''}`}
              >
                {/* 点击卡片主体切换书 */}
                <div
                  className="flex flex-col flex-1 p-3"
                  onClick={() => onSelectNovel(n)}
                >
                  <div className="w-full aspect-[3/4] mb-3 rounded-sm overflow-hidden relative">
                    <BookCover novelId={n.id} refreshKey={coverKeys[n.id]} />
                    {/* 悬浮封面上传按钮 */}
                    <button
                      onClick={(e) => handleCoverClick(n.id, e)}
                      aria-label={`更换封面 ${n.title}`}
                      className="absolute inset-0 flex items-center justify-center bg-black/40 opacity-0 group-hover:opacity-100 transition-opacity"
                      title="更换封面"
                    >
                      <Camera className="w-5 h-5 text-white" />
                    </button>
                  </div>
                  <h3 className="text-sm font-medium truncate mb-1">{n.title}</h3>
                  {n.genre ? (
                    <span className="inline-block self-start text-[11px] px-1.5 py-0.5 rounded bg-primary/10 text-primary mb-1.5">
                      {n.genre}
                    </span>
                  ) : (
                    <span className="inline-block self-start text-[11px] px-1.5 py-0.5 rounded bg-muted text-muted-foreground mb-1.5">
                      未分类
                    </span>
                  )}
                  {n.description && (
                    <p className="text-xs text-muted-foreground line-clamp-2 leading-relaxed">
                      {n.description}
                    </p>
                  )}
                </div>

                {/* 悬浮操作按钮 */}
                <div className="absolute top-2 right-2 flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                  <button
                    onClick={(e) => { e.stopPropagation(); onExportNovel(n) }}
                    aria-label={`导出作品 ${n.title}`}
                    className="w-7 h-7 flex items-center justify-center rounded-md bg-background/90 border shadow-sm hover:bg-muted transition-colors"
                    title="导出"
                  >
                    <Download className="w-3.5 h-3.5 text-muted-foreground" />
                  </button>
                  <button
                    onClick={(e) => { e.stopPropagation(); onEditNovel(n) }}
                    aria-label={`编辑作品 ${n.title}`}
                    className="w-7 h-7 flex items-center justify-center rounded-md bg-background/90 border shadow-sm hover:bg-muted transition-colors"
                    title="编辑"
                  >
                    <Pencil className="w-3.5 h-3.5 text-muted-foreground" />
                  </button>
                  <button
                    onClick={(e) => { e.stopPropagation(); onDeleteNovel(n) }}
                    aria-label={`删除作品 ${n.title}`}
                    className="w-7 h-7 flex items-center justify-center rounded-md bg-background/90 border shadow-sm hover:bg-danger-bg hover:border-danger-border transition-colors"
                    title="删除"
                  >
                    <Trash2 className="w-3.5 h-3.5 text-muted-foreground hover:text-destructive" />
                  </button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
