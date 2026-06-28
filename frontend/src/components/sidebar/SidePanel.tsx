import type { novel, chapter } from '@/hooks/useApp'
import NovelList from './NovelList'
import ChapterList from './ChapterList'
import CharacterList from '@/components/character/CharacterList'
import LocationList from '@/components/location/LocationList'
import SkillList from '@/components/skill/SkillList'
import SearchPanel from '@/components/search/SearchPanel'
import TimelineList from '@/components/timeline/TimelineList'
import ArcList from '@/components/storyarc/ArcList'
import ReaderList from '@/components/reader/ReaderList'
import PreferenceList from '@/components/preference/PreferenceList'
import type { SearchResult } from '@/components/search/SearchPanel'

interface Props {
  activePanel: string
  novels: novel.Novel[]
  novelId: number
  onSelectNovel: (n: novel.Novel) => void
  onSelectChapter: (ch: chapter.Chapter) => void
  onSelectGoink: () => void
  onExportNovel: (novelId: number) => void
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
  onEditSkill: (path: string, title: string, readOnly: boolean) => void
  onNewSkill: (name: string) => void
  onSearchNavigateEntity: (panelId: string, entityId: number) => void
  onSearchNavigateChapter: (filePath: string, title: string, chapterNum: number, matchPos: number, matchLen: number) => void
  searchQuery: string
  searchResults: SearchResult[]
  onSearchChange: (query: string, results: SearchResult[]) => void
}

export default function SidePanel({
  activePanel,
  novels, novelId, onSelectNovel,
  onSelectChapter, onSelectGoink, onExportNovel, target,
  showCreate, setShowCreate, title, setTitle, description, setDescription,
  onCreateNovel,
  activeSkillName, onSelectSkill, onEditSkill, onNewSkill,
  onSearchNavigateEntity, onSearchNavigateChapter,
  searchQuery, searchResults, onSearchChange,
}: Props) {
  return (
    <aside className="w-56 border-r bg-sidebar flex flex-col shrink-0 select-none cursor-default">
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
          onEditSkill={onEditSkill}
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
          onExportNovel={() => onExportNovel(novelId)}
        />
      ) : activePanel === 'characters' ? (
        <CharacterList novelId={novelId} />
      ) : activePanel === 'locations' ? (
        <LocationList novelId={novelId} />
      ) : activePanel === 'storyarcs' ? (
        <ArcList novelId={novelId} />
      ) : activePanel === 'timeline' ? (
        <TimelineList novelId={novelId} />
      ) : activePanel === 'reader' ? (
        <ReaderList novelId={novelId} />
      ) : activePanel === 'preferences' ? (
        <PreferenceList novelId={novelId} />
      ) : (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">即将推出</p>
        </div>
      )}
    </aside>
  )
}
