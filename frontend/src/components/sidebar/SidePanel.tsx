import type { novel, chapter } from '@/hooks/useApp'
import NovelList from './NovelList'
import ChapterList from './ChapterList'
import CharacterList from '@/components/character/CharacterList'
import LocationList from '@/components/location/LocationList'

interface Props {
  activePanel: string
  novels: novel.Novel[]
  novelId: number
  onSelectNovel: (n: novel.Novel) => void
  onSelectChapter: (ch: chapter.Chapter) => void
  onSelectGoink: () => void
  target: { path: string; title: string } | null
  showCreate: boolean
  setShowCreate: (v: boolean) => void
  title: string
  setTitle: (v: string) => void
  description: string
  setDescription: (v: string) => void
  onCreateNovel: () => void
}

export default function SidePanel({
  activePanel,
  novels, novelId, onSelectNovel,
  onSelectChapter, onSelectGoink, target,
  showCreate, setShowCreate, title, setTitle, description, setDescription,
  onCreateNovel,
}: Props) {
  return (
    <aside className="w-56 border-r bg-sidebar flex flex-col shrink-0">
      {activePanel === 'novels' ? (
        <NovelList
          novels={novels}
          novelId={novelId}
          onSelectNovel={onSelectNovel}
          showCreate={showCreate}
          setShowCreate={setShowCreate}
          title={title}
          setTitle={setTitle}
          description={description}
          setDescription={setDescription}
          onCreateNovel={onCreateNovel}
        />
      ) : activePanel === 'chapters' ? (
        <ChapterList
          novelId={novelId}
          target={target}
          onSelectChapter={onSelectChapter}
          onSelectGoink={onSelectGoink}
        />
      ) : activePanel === 'characters' ? (
        <CharacterList novelId={novelId} />
      ) : activePanel === 'locations' ? (
        <LocationList novelId={novelId} />
      ) : activePanel === 'storyarcs' ? (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">故事弧线</p>
        </div>
      ) : activePanel === 'timeline' ? (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">时间线</p>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">即将推出</p>
        </div>
      )}
    </aside>
  )
}
