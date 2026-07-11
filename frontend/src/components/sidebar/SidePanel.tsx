import { useCallback, useEffect, useRef, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, MouseEvent as ReactMouseEvent } from 'react'
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
import ReferenceBookSidebar from '@/components/reference-anchor/ReferenceBookSidebar'
import type { SearchResult } from '@/components/search/SearchPanel'
import type { reference } from '@/lib/novelist/types'
import { LAYOUT_LIMITS, clampPanelWidth } from '@/lib/layout'

interface Props {
  width: number
  onWidthChange: (width: number) => void
  onWidthCommit: (width: number) => void
  activePanel: string
  novels: novel.Novel[]
  novelId: number
  onSelectNovel: (n: novel.Novel) => void
  onSelectChapter: (ch: chapter.Chapter) => void
  onSelectNovelist: () => void
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
  selectedReferenceAnchorIds: number[]
  referenceRefreshKey: number
  onReferenceSelectionChange: (anchorIds: number[]) => void
  onReferenceAnchorsChange: (anchors: reference.Anchor[]) => void
  onReferenceMutation: () => void
}

export default function SidePanel({
  width,
  onWidthChange,
  onWidthCommit,
  activePanel,
  novels, novelId, onSelectNovel,
  onSelectChapter, onSelectNovelist, onExportNovel, target,
  showCreate, setShowCreate, title, setTitle, description, setDescription,
  onCreateNovel,
  activeSkillName, onSelectSkill, onEditSkill, onNewSkill,
  onSearchNavigateEntity, onSearchNavigateChapter,
  searchQuery, searchResults, onSearchChange,
  selectedReferenceAnchorIds, referenceRefreshKey,
  onReferenceSelectionChange, onReferenceAnchorsChange, onReferenceMutation,
}: Props) {
  const [isDragging, setIsDragging] = useState(false)
  const startXRef = useRef(0)
  const startWidthRef = useRef(width)
  const latestWidthRef = useRef(width)

  useEffect(() => {
    latestWidthRef.current = width
  }, [width])

  const handleMouseDown = useCallback((e: ReactMouseEvent) => {
    e.preventDefault()
    setIsDragging(true)
    startXRef.current = e.clientX
    startWidthRef.current = width
    latestWidthRef.current = width
  }, [width])

  useEffect(() => {
    if (!isDragging) return
    const previousUserSelect = document.body.style.userSelect
    document.body.style.userSelect = 'none'
    const handleMouseMove = (e: MouseEvent) => {
      const delta = e.clientX - startXRef.current
      const nextWidth = clampPanelWidth(
        startWidthRef.current + delta,
        LAYOUT_LIMITS.sidebar.min,
        LAYOUT_LIMITS.sidebar.max,
        LAYOUT_LIMITS.sidebar.fallback,
      )
      latestWidthRef.current = nextWidth
      onWidthChange(nextWidth)
    }
    const handleMouseUp = () => {
      setIsDragging(false)
      onWidthCommit(latestWidthRef.current)
    }
    document.addEventListener('mousemove', handleMouseMove)
    document.addEventListener('mouseup', handleMouseUp)
    return () => {
      document.body.style.userSelect = previousUserSelect
      document.removeEventListener('mousemove', handleMouseMove)
      document.removeEventListener('mouseup', handleMouseUp)
    }
  }, [isDragging, onWidthChange, onWidthCommit])

  const handleResizeKeyDown = useCallback((e: ReactKeyboardEvent) => {
    const step = e.shiftKey ? 40 : 16
    let nextWidth: number
    if (e.key === 'ArrowLeft') {
      nextWidth = width - step
    } else if (e.key === 'ArrowRight') {
      nextWidth = width + step
    } else if (e.key === 'Home') {
      nextWidth = LAYOUT_LIMITS.sidebar.min
    } else if (e.key === 'End') {
      nextWidth = LAYOUT_LIMITS.sidebar.max
    } else {
      return
    }
    e.preventDefault()
    const clamped = clampPanelWidth(
      nextWidth,
      LAYOUT_LIMITS.sidebar.min,
      LAYOUT_LIMITS.sidebar.max,
      LAYOUT_LIMITS.sidebar.fallback,
    )
    onWidthChange(clamped)
    onWidthCommit(clamped)
  }, [onWidthChange, onWidthCommit, width])

  return (
    <aside
      className="relative border-r bg-sidebar flex flex-col shrink-0 select-none cursor-default overflow-hidden"
      style={{ width }}
    >
      <div
        role="separator"
        aria-label="调整侧边栏宽度"
        aria-orientation="vertical"
        aria-valuemin={LAYOUT_LIMITS.sidebar.min}
        aria-valuemax={LAYOUT_LIMITS.sidebar.max}
        aria-valuenow={Math.round(width)}
        tabIndex={0}
        className="absolute right-0 top-0 bottom-0 z-20 w-1 cursor-col-resize bg-transparent transition-colors hover:bg-primary/30 focus-visible:bg-primary/30 focus-visible:outline-none"
        style={{ marginRight: -2 }}
        onMouseDown={handleMouseDown}
        onKeyDown={handleResizeKeyDown}
      />

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
          onSelectNovelist={onSelectNovelist}
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
      ) : activePanel === 'reference' ? (
        <ReferenceBookSidebar
          novelId={novelId}
          selectedAnchorIds={selectedReferenceAnchorIds}
          refreshKey={referenceRefreshKey}
          onSelectionChange={onReferenceSelectionChange}
          onAnchorsChange={onReferenceAnchorsChange}
          onReferenceMutation={onReferenceMutation}
        />
      ) : (
        <div className="flex-1 flex items-center justify-center">
          <p className="text-xs text-muted-foreground">即将推出</p>
        </div>
      )}

      {isDragging && (
        <div className="fixed inset-0 z-50 cursor-col-resize select-none" />
      )}
    </aside>
  )
}
