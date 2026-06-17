import type { novel, chapter } from '@/hooks/useApp'
import NovelList from './NovelList'
import ChapterList from './ChapterList'
import CharacterList from '@/components/character/CharacterList'
import LocationList from '@/components/location/LocationList'
import SkillList from '@/components/skill/SkillList'
import SearchPanel from '@/components/search/SearchPanel'
import type { SearchResult } from '@/components/search/SearchPanel'

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
  activeSkillName: string | null
  onSelectSkill: (path: string, title: string, readOnly: boolean) => void
  onNewSkill: (name: string) => void
  onSearchNavigateEntity: (panelId: string, entityId: number) => void
  onSearchNavigateChapter: (filePath: string, title: string, chapterNum: number) => void
  searchQuery: string
  searchResults: SearchResult[]
  onSearchChange: (query: string, results: SearchResult[]) => void
}

export default function SidePanel({
  activePanel,
  novels, novelId, onSelectNovel,
  onSelectChapter, onSelectGoink, target,
  showCreate, setShowCreate, title, setTitle, description, setDescription,
  onCreateNovel,
  activeSkillName, onSelectSkill, onNewSkill,
  onSearchNavigateEntity, onSearchNavigateChapter,
  searchQuery, searchResults, onSearchChange,
}: Props) {
  return (
    <aside className="w-56 border-r bg-sidebar flex flex-col shrink-0">
      {activePanel === 'search' ? (
        <SearchPanel
          novelId={novelId}
          query={searchQuery}
          results={searchResults}
          onResultsChange={onSearchChange}
          onNavigateEntity={onSearchNavigateEntity}
          onNavigateChapter={onSearchNavigateChapter}
        />
      ) : activePanel === 'skills' ? (
        <SkillList
          novelId={novelId}
          activeSkillName={activeSkillName}
          onSelectSkill={onSelectSkill}
          onNewSkill={onNewSkill}
        />
      ) : activePanel === 'novels' ? (
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
      ) : activePanel === 'reader' ? (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">读者视角</p>
        </div>
      ) : activePanel === 'preferences' ? (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">创作偏好</p>
        </div>
      ) : (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">即将推出</p>
        </div>
      )}
    </aside>
  )
}
