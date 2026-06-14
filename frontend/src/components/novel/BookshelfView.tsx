import { Plus, Pencil, Trash2, BookOpen } from 'lucide-react'
import BookCover from '@/components/sidebar/BookCover'
import type { novel } from '@/hooks/useApp'

interface Props {
  novels: novel.Novel[]
  activeNovelId: number
  onSelectNovel: (n: novel.Novel) => void
  onEditNovel: (n: novel.Novel) => void
  onDeleteNovel: (n: novel.Novel) => void
  onCreateNovel: () => void
}

export default function BookshelfView({
  novels, activeNovelId,
  onSelectNovel, onEditNovel, onDeleteNovel, onCreateNovel,
}: Props) {
  return (
    <div className="flex-1 flex flex-col min-h-0 bg-background">
      {/* 顶部工具栏 */}
      <div className="flex items-center justify-between px-6 py-4 border-b shrink-0">
        <span className="text-sm text-muted-foreground">
          共 <b className="text-foreground">{novels.length}</b> 部作品
        </span>
        <button
          onClick={onCreateNovel}
          className="inline-flex items-center gap-1.5 h-8 px-3 rounded-md text-sm bg-primary text-primary-foreground hover:opacity-90 transition-opacity"
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
      ) : (
        /* 书架网格 */
        <div className="flex-1 overflow-y-auto p-6">
          <div className="grid grid-cols-[repeat(auto-fill,minmax(180px,1fr))] gap-5">
            {novels.map(n => (
              <div
                key={n.id}
                className={`group relative flex flex-col rounded-lg border bg-card hover:shadow-md transition-shadow cursor-pointer
                  ${n.id === activeNovelId ? 'ring-2 ring-primary' : ''}`}
              >
                {/* 点击卡片主体切换书 */}
                <div
                  className="flex flex-col flex-1 p-3"
                  onClick={() => onSelectNovel(n)}
                >
                  <div className="w-full aspect-[3/4] mb-3 rounded-sm overflow-hidden">
                    <BookCover />
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
                    onClick={(e) => { e.stopPropagation(); onEditNovel(n) }}
                    className="w-7 h-7 flex items-center justify-center rounded-md bg-background/90 border shadow-sm hover:bg-muted transition-colors"
                    title="编辑"
                  >
                    <Pencil className="w-3.5 h-3.5 text-muted-foreground" />
                  </button>
                  <button
                    onClick={(e) => { e.stopPropagation(); onDeleteNovel(n) }}
                    className="w-7 h-7 flex items-center justify-center rounded-md bg-background/90 border shadow-sm hover:bg-red-50 hover:border-red-200 transition-colors"
                    title="删除"
                  >
                    <Trash2 className="w-3.5 h-3.5 text-muted-foreground hover:text-red-600" />
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
