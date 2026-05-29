import { ChevronRight, FileText, Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import BookCover from '@/components/novel/BookCover'
import type { novel, chapter } from '@/hooks/useApp'

interface Props {
  activePanel: string
  novels: novel.Novel[]
  activeNovelId: number
  onSelectNovel: (n: novel.Novel) => void
  chapters: chapter.Chapter[]
  chapterBlocks: { key: number; start: number; end: number; chs: chapter.Chapter[] }[]
  expandedBlocks: Set<number>
  selectedChapterId: number | null
  onSelectChapter: (ch: chapter.Chapter) => void
  onToggleBlock: (key: number) => void
  showCreate: boolean
  setShowCreate: (v: boolean) => void
  title: string
  setTitle: (v: string) => void
  description: string
  setDescription: (v: string) => void
  onCreateNovel: () => void
  showCreateChapter: boolean
  setShowCreateChapter: (v: boolean) => void
  chapterTitle: string
  setChapterTitle: (v: string) => void
  onCreateChapter: () => void
}

export default function SidePanel({
  activePanel,
  novels, activeNovelId, onSelectNovel,
  chapters, chapterBlocks, expandedBlocks, selectedChapterId,
  onSelectChapter, onToggleBlock,
  showCreate, setShowCreate, title, setTitle, description, setDescription,
  onCreateNovel,
  showCreateChapter, setShowCreateChapter, chapterTitle, setChapterTitle,
  onCreateChapter,
}: Props) {
  return (
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
                <Button size="sm" onClick={onCreateNovel}>创建</Button>
                <Button size="sm" variant="ghost" onClick={() => { setShowCreate(false); setTitle(''); setDescription('') }}>取消</Button>
              </div>
            </div>
          )}

          <div className="flex-1 overflow-y-auto">
            {novels.map(n => (
              <button
                key={n.id}
                onClick={() => onSelectNovel(n)}
                className={`w-full flex items-center gap-3 px-3 py-2.5 text-left hover:bg-muted/50 transition-colors relative
                  ${n.id === activeNovelId ? 'bg-primary/10 text-foreground' : ''}`}
              >
                {n.id === activeNovelId && (
                  <span className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 bg-primary rounded-r-full" />
                )}
                <div className="w-8 shrink-0 rounded-sm overflow-hidden">
                  <BookCover />
                </div>
                <span className={`flex-1 text-sm truncate ${n.id === activeNovelId ? 'font-medium' : ''}`}>{n.title}</span>
              </button>
            ))}
          </div>
        </>
      ) : activePanel === 'chapters' ? (
        <>
          <div className="flex items-center justify-between px-3 py-2.5 border-b">
            <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
              章节 ({chapters.length})
            </span>
            <button
              onClick={() => setShowCreateChapter(true)}
              className="w-6 h-6 flex items-center justify-center rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
            >
              <Plus className="w-4 h-4" />
            </button>
          </div>

          {showCreateChapter && (
            <div className="p-3 border-b space-y-2">
              <input
                type="text" value={chapterTitle} autoFocus
                onChange={e => setChapterTitle(e.target.value)}
                onKeyDown={e => e.key === 'Enter' && onCreateChapter()}
                placeholder="章节标题"
                className="w-full h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
              <div className="flex gap-2">
                <Button size="sm" onClick={onCreateChapter}>添加</Button>
                <Button size="sm" variant="ghost" onClick={() => { setShowCreateChapter(false); setChapterTitle('') }}>取消</Button>
              </div>
            </div>
          )}

          <div className="flex-1 overflow-y-auto">
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
                      onClick={() => onToggleBlock(block.key)}
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
                          <button
                            key={ch.id}
                            onClick={() => onSelectChapter(ch)}
                            className={`w-full flex items-center gap-2.5 pl-7 pr-3 py-1.5 text-left hover:bg-muted/50 transition-colors relative
                              ${ch.id === selectedChapterId ? 'bg-primary/10 font-medium' : ''}`}
                          >
                            {ch.id === selectedChapterId && (
                              <span className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 bg-primary rounded-r-full" />
                            )}
                            <span className="text-xs text-muted-foreground w-8 shrink-0 tabular-nums">
                              第{ch.chapter_number}章
                            </span>
                            <span className="flex-1 text-sm truncate">{ch.title}</span>
                            {ch.word_count > 0 && (
                              <span className="text-[10px] text-muted-foreground/60 shrink-0">
                                {ch.word_count}字
                              </span>
                            )}
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                )
              })
            )}
          </div>
        </>
      ) : (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">即将推出</p>
        </div>
      )}
    </aside>
  )
}
