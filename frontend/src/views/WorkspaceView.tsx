import { useState, useEffect, useCallback, useRef } from 'react'
import { flushSync } from 'react-dom'
import { useApp } from '@/hooks/useApp'
import type { novel, chapter, search } from '@/hooks/useApp'
import type { novelImport } from '@/lib/novelist/types'
import { buildStartNovelImportInput } from '@/lib/novelist/importBoundary'
import ActivityBar from '@/components/shell/ActivityBar'
import StatusBar from '@/components/shell/StatusBar'
import SidePanel from '@/components/sidebar/SidePanel'
import ContentPanel, { type ContentPanelHandle } from '@/components/content/ContentPanel'
import CharacterListView from '@/components/character/CharacterListView'
import LocationListView from '@/components/location/LocationListView'
import ArcListView from '@/components/storyarc/ArcListView'
import TimelineView from '@/components/timeline/TimelineView'
import ReaderView from '@/components/reader/ReaderView'
import PreferenceView from '@/components/preference/PreferenceView'
import ReferenceAnchorView from '@/components/reference-anchor/ReferenceAnchorView'
import BookshelfView from '@/components/novel/BookshelfView'
import NovelEditDialog from '@/components/novel/NovelEditDialog'
import NovelDeleteDialog from '@/components/novel/NovelDeleteDialog'
import ExportDialog from '@/components/export/ExportDialog'
import ChatPanel from '@/components/chat/ChatPanel'
import GitHubLink from '@/components/shell/GitHubLink'
import SettingsDialog from '@/components/settings/SettingsDialog'
import HelpDialog from '@/components/help/HelpDialog'
import ProfileView from '@/components/profile/ProfileView'
import { Settings, User, HelpCircle, Moon, Sun } from 'lucide-react'
import { WindowMinimise, WindowToggleMaximise, WindowIsMaximised, Quit } from '@/lib/novelist/runtime'
import Logo from '@/components/Logo'
import { useTheme, type Theme } from '@/hooks/useTheme'

const THEME_ICON: Record<Theme, React.ReactNode> = { light: <Moon className="w-5 h-5" />, dark: <Sun className="w-5 h-5" /> }
const THEME_LABEL: Record<Theme, string> = { light: '深色模式', dark: '浅色模式' }

interface Props {
  initialNovelId: number
  initialShowHelp?: boolean
}

export default function WorkspaceView({ initialNovelId, initialShowHelp }: Props) {
  const app = useApp()
  const contentRef = useRef<ContentPanelHandle>(null)

  const [novels, setNovels] = useState<novel.Novel[]>([])
  const [activeNovelId, setActiveNovelId] = useState(initialNovelId)
  const [activePanel, setActivePanel] = useState(initialNovelId ? 'chapters' : 'novels')
  const [sidebarPanel, setSidebarPanel] = useState<string | null>(null)
  const [searchQuery, setSearchQuery] = useState('')
  const [searchResults, setSearchResults] = useState<search.Result[]>([])
  const [characterFocusId, setCharacterFocusId] = useState<number>(0)
  const [locationFocusId, setLocationFocusId] = useState<number>(0)
  const [timelineFocusId, setTimelineFocusId] = useState<number>(0)
  const [arcFocusId, setArcFocusId] = useState<number>(0)
  const [readerFocusId, setReaderFocusId] = useState<number>(0)
  const [preferenceFocusId, setPreferenceFocusId] = useState<number>(0)
  const [showCreate, setShowCreate] = useState(false)
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [showSettings, setShowSettings] = useState(false)
  const [showHelp, setShowHelp] = useState(false)
  const [tabTarget, setTabTarget] = useState<{ path: string; title: string } | null>(null)
  const [activeContent, setActiveContent] = useState('')
  const [isDirty, setIsDirty] = useState(false)
  const [activeSkillName, setActiveSkillName] = useState<string | null>(null)
  const [isMaximised, setIsMaximised] = useState(false)
  const [platformOS, setPlatformOS] = useState('')
  const loadedRef = useRef(false)
  const { theme, toggle: toggleTheme } = useTheme()

  // ── 书籍管理弹窗 ──────────────────────────────────────
  const [editingNovel, setEditingNovel] = useState<novel.Novel | null>(null)
  const [deletingNovel, setDeletingNovel] = useState<novel.Novel | null>(null)
  const [showCreateDialog, setShowCreateDialog] = useState(false)
  const [exportNovelId, setExportNovelId] = useState<number | null>(null)

  // ── 窗口状态 ────────────────────────────────────────────

  useEffect(() => {
    app.GetPlatform().then((info) => {
      if (info.os) setPlatformOS(info.os as string)
    })
    WindowIsMaximised().then(setIsMaximised)
  }, [app])

  // ── 首次进入自动弹帮助 ──────────────────────────────────

  useEffect(() => {
    if (!initialShowHelp) return
    const timer = window.setTimeout(() => setShowHelp(true), 0)
    return () => window.clearTimeout(timer)
  }, [initialShowHelp])

  // ── 作品列表 ────────────────────────────────────────────

  const loadNovels = useCallback(async () => {
    const list = await app.GetNovels()
    setNovels(list ?? [])
    loadedRef.current = true
  }, [app])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      const list = await app.GetNovels()
      if (!cancelled) {
        setNovels(list ?? [])
        loadedRef.current = true
      }
    })()
    return () => { cancelled = true }
  }, [app])

  // ── SidePanel → ContentPanel 桥接 ─────────────────────────

  function handleSelectChapter(ch: chapter.Chapter) {
    setTabTarget({ path: ch.file_path, title: `第${ch.chapter_number}章 ${ch.title}` })
    contentRef.current?.openFile(ch.file_path, `第${ch.chapter_number}章 ${ch.title}`)
  }

  function handleSelectNovelist() {
    setTabTarget({ path: 'novelist.md', title: '故事状态' })
    contentRef.current?.openFile('novelist.md', '故事状态')
  }

  // ── Approval ────────────────────────────────────────────

  async function handleApprove(toolId: string, feedback: string) {
    await app.ApproveTool(toolId, true, feedback)
    await contentRef.current?.handleDiffApprove(toolId)
  }

  async function handleReject(toolId: string, feedback: string) {
    await app.ApproveTool(toolId, false, feedback)
    contentRef.current?.handleDiffReject(toolId)
  }

  function handleApprovalFileEdit(data: {
    path: string; title: string; diff: string; original: string; modified: string
    changeType: string; reason: string; toolId: string
  }) {
    contentRef.current?.openDiffTab(data)
  }

  // ── 自动选择小说 ────────────────────────────────────────

  useEffect(() => {
    if (!loadedRef.current) return
    const exists = novels.find(n => n.id === activeNovelId)
    const timer = window.setTimeout(() => {
      if (!exists && novels.length > 0) {
        const first = novels[0]
        setActiveNovelId(first.id)
        setActivePanel('chapters')
        void app.SetActiveNovel({ novel_id: first.id })
      } else if (novels.length === 0) {
        setActivePanel('novels')
      }
    }, 0)
    return () => window.clearTimeout(timer)
  }, [app, novels, activeNovelId])

  function handleActivitySelect(id: string) {
    if (id === 'search') {
      setSidebarPanel('search')
    } else {
      setSidebarPanel(null)
      setActivePanel(id)
      contentRef.current?.clearHighlight()
    }
  }

  function handleSearchNavigateEntity(panelId: string, entityId: number) {
    setCharacterFocusId(0)
    setLocationFocusId(0)
    setTimelineFocusId(0)
    setArcFocusId(0)
    setReaderFocusId(0)
    setPreferenceFocusId(0)
    switch (panelId) {
      case 'characters': setCharacterFocusId(entityId); break
      case 'locations': setLocationFocusId(entityId); break
      case 'timeline': setTimelineFocusId(entityId); break
      case 'storyarcs': setArcFocusId(entityId); break
      case 'reader': setReaderFocusId(entityId); break
      case 'preferences': setPreferenceFocusId(entityId); break
    }
    setActivePanel(panelId)
  }

  function handleSearchNavigateChapter(filePath: string, title: string, _chapterNum: number, matchPos: number, matchLen: number) {
    flushSync(() => setActivePanel('chapters'))
    if (matchPos >= 0 && matchLen > 0) {
      contentRef.current?.openFileWithHighlight(filePath, title, matchPos, matchLen)
    } else {
      contentRef.current?.openFile(filePath, title)
    }
  }

  async function handleSelectNovel(n: novel.Novel) {
    setActiveNovelId(n.id)
    setActivePanel('chapters')
    contentRef.current?.closeAllTabs()
    setTabTarget(null)
    setActiveContent('')
    await app.SetActiveNovel({ novel_id: n.id })
  }

  async function handleCreateNovel() {
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

  async function handleCreateNovelFromDialog(input: { title: string; description: string; genre: string }) {
    const n = await app.CreateNovel({ title: input.title, description: input.description, genre: input.genre })
    if (n) {
      setShowCreateDialog(false)
      await loadNovels()
      setActiveNovelId(n.id)
      setActivePanel('chapters')
      await app.SetActiveNovel({ novel_id: n.id })
    }
  }

  async function handleUpdateNovel(input: { title: string; description: string; genre: string }) {
    if (!editingNovel) return
    await app.UpdateNovel(editingNovel.id, input)
    setEditingNovel(null)
    await loadNovels()
  }

  async function handleDeleteNovel() {
    if (!deletingNovel) return
    await app.DeleteNovel(deletingNovel.id)
    setDeletingNovel(null)
    await loadNovels()
  }

  async function handleExportNovel(format: 'epub' | 'markdown' | 'txt') {
    if (exportNovelId == null) return
    await app.ExportNovel(exportNovelId, format)
  }

  async function handleSaveCover(novelID: number, file: File) {
    const buf = await file.arrayBuffer()
    await app.SaveCover(novelID, Array.from(new Uint8Array(buf)))
  }

  async function handlePickNovelImportFile() {
    return await app.PickNovelImportFile()
  }

  async function handleStartNovelImport(sourcePath: string): Promise<novelImport.ImportRun> {
    const run = await app.StartNovelImport(buildStartNovelImportInput(sourcePath))
    await loadNovels()
    if (run.created_novel_id) {
      setActiveNovelId(run.created_novel_id)
      await app.SetActiveNovel({ novel_id: run.created_novel_id })
    }
    return run
  }

  const activeNovel = novels.find(n => n.id === activeNovelId)

  // ── 窗口按钮样式 ────────────────────────────────────────

  const winBtn = 'w-12 h-full flex items-center justify-center cursor-pointer text-foreground/80 hover:text-foreground hover:bg-black/25 hover:shadow-md transition-all'
  const closeBtn = 'w-12 h-full flex items-center justify-center cursor-pointer text-foreground/80 hover:text-destructive-foreground hover:bg-destructive transition-colors'

  return (
    <div className="h-screen flex flex-col overflow-hidden">
      <header
        className="h-11 flex items-center border-b bg-sidebar shrink-0 select-none cursor-default"
        onDoubleClick={() => { WindowToggleMaximise(); setIsMaximised(!isMaximised) }}
      >
        <Logo className="h-7 w-7 ml-3" />
        <span className="text-sm font-medium pl-2 flex-1">
          {activeNovel?.title ?? 'Novelist'}
        </span>
        <div className="flex items-center h-full">
          <GitHubLink />
          <button
            onClick={() => setActivePanel('profile')}
            className={`text-muted-foreground hover:text-foreground transition-colors cursor-pointer w-8 h-8 flex items-center justify-center ml-2 ${activePanel === 'profile' ? 'text-foreground' : ''}`}
            title="个人中心"
          >
            <User className="w-5 h-5" />
          </button>
          <button
            onClick={() => setShowHelp(true)}
            className="text-muted-foreground hover:text-foreground transition-colors cursor-pointer w-8 h-8 flex items-center justify-center"
            title="帮助"
          >
            <HelpCircle className="w-5 h-5" />
          </button>
          <button
            onClick={toggleTheme}
            className="text-muted-foreground hover:text-foreground transition-colors cursor-pointer w-8 h-8 flex items-center justify-center"
            title={THEME_LABEL[theme]}
          >
            {THEME_ICON[theme]}
          </button>
          <button
            onClick={() => setShowSettings(true)}
            className="text-muted-foreground hover:text-foreground transition-colors cursor-pointer w-8 h-8 flex items-center justify-center mr-1"
            title="设置"
          >
            <Settings className="w-5 h-5" />
          </button>
          {platformOS !== 'darwin' && (
            <>
              <button onClick={WindowMinimise} className={winBtn} title="最小化">
                <svg width="12" height="12" viewBox="0 0 12 12"><path d="M2.5 6h7" stroke="currentColor" strokeWidth="1.1" strokeLinecap="round"/></svg>
              </button>
              <button
                onClick={() => { WindowToggleMaximise(); setIsMaximised(!isMaximised) }}
                className={winBtn}
                title={isMaximised ? '还原' : '最大化'}
              >
                {isMaximised ? (
                  <svg width="12" height="12" viewBox="0 0 12 12">
                    <rect x="4" y="1.5" width="6.5" height="6.5" rx="1" fill="none" stroke="currentColor" strokeWidth=".9" />
                    <rect x="1.5" y="2.5" width="6.5" height="6.5" rx="1" fill="var(--color-sidebar)" stroke="currentColor" strokeWidth=".9" />
                  </svg>
                ) : (
                  <svg width="12" height="12" viewBox="0 0 12 12"><rect x="1.5" y="1.5" width="9" height="9" stroke="currentColor" strokeWidth=".9" rx=".5" fill="none" /></svg>
                )}
              </button>
              <button onClick={Quit} className={closeBtn} title="关闭">
                <svg width="12" height="12" viewBox="0 0 12 12"><path d="M2.5 2.5l7 7M9.5 2.5l-7 7" stroke="currentColor" strokeWidth="1" strokeLinecap="round"/></svg>
              </button>
            </>
          )}
        </div>
      </header>

      <div className="flex-1 flex min-h-0 overflow-hidden">
        <ActivityBar activeId={sidebarPanel ?? activePanel} onSelect={handleActivitySelect} />

        <SidePanel
          activePanel={sidebarPanel ?? activePanel}
          novels={novels}
          novelId={activeNovelId}
          onSelectNovel={handleSelectNovel}
          onSelectChapter={handleSelectChapter}
          onSelectNovelist={handleSelectNovelist}
          onExportNovel={(id) => setExportNovelId(id)}
          target={tabTarget}
          showCreate={showCreate}
          setShowCreate={setShowCreate}
          title={title}
          setTitle={setTitle}
          description={description}
          setDescription={setDescription}
          onCreateNovel={handleCreateNovel}
          activeSkillName={activeSkillName}
          onSelectSkill={(path, title, readOnly) => {
            setActiveSkillName(title)
            contentRef.current?.openFile(path, title, readOnly)
          }}
          onEditSkill={(path, title, readOnly) => {
            setActiveSkillName(title)
            contentRef.current?.openFile(path, title, readOnly, 'edit')
          }}
          onNewSkill={(name) => {
            setActiveSkillName(`技能: ${name}`)
            contentRef.current?.openFile(`skills/${name}.md`, `技能: ${name}`, false, 'edit')
          }}
          onSearchNavigateEntity={handleSearchNavigateEntity}
          onSearchNavigateChapter={handleSearchNavigateChapter}
          searchQuery={searchQuery}
          searchResults={searchResults}
          onSearchChange={(q, r) => { setSearchQuery(q); setSearchResults(r) }}
        />

        {activePanel === 'novels' ? (
          <BookshelfView
            novels={novels}
            activeNovelId={activeNovelId}
            onSelectNovel={handleSelectNovel}
            onEditNovel={setEditingNovel}
            onDeleteNovel={setDeletingNovel}
            onCreateNovel={() => setShowCreateDialog(true)}
            onSaveCover={handleSaveCover}
            onExportNovel={(n) => setExportNovelId(n.id)}
            onPickNovelImportFile={handlePickNovelImportFile}
            onStartNovelImport={handleStartNovelImport}
          />
        ) : activePanel !== 'characters' && activePanel !== 'locations' && activePanel !== 'storyarcs' && activePanel !== 'timeline' && activePanel !== 'reader' && activePanel !== 'preferences' && activePanel !== 'reference' && activePanel !== 'profile' && (
          <ContentPanel
            ref={contentRef}
            novelId={activeNovelId}
            onContentChange={setActiveContent}
            onDirtyChange={setIsDirty}
            onActiveFileChange={setTabTarget}
          />
        )}

        {activePanel === 'characters' ? (
          <CharacterListView novelId={activeNovelId} focusId={characterFocusId} />
        ) : activePanel === 'locations' ? (
          <LocationListView novelId={activeNovelId} focusId={locationFocusId} />
        ) : activePanel === 'storyarcs' ? (
          <ArcListView novelId={activeNovelId} focusArcId={arcFocusId} />
        ) : activePanel === 'timeline' ? (
          <TimelineView novelId={activeNovelId} focusEntryId={timelineFocusId} />
        ) : activePanel === 'reader' ? (
          <ReaderView novelId={activeNovelId} focusId={readerFocusId} />
        ) : activePanel === 'preferences' ? (
          <PreferenceView novelId={activeNovelId} focusId={preferenceFocusId} />
        ) : activePanel === 'reference' ? (
          <ReferenceAnchorView novelId={activeNovelId} />
        ) : activePanel === 'profile' ? (
          <ProfileView />
        ) : null}

        {activePanel !== 'profile' && (
          <ChatPanel novelId={activeNovelId} onApprove={handleApprove} onReject={handleReject} onApprovalFileEdit={handleApprovalFileEdit} />
        )}
      </div>

      <StatusBar content={activeContent} isDirty={isDirty} />

      <SettingsDialog
        open={showSettings}
        onClose={() => setShowSettings(false)}
        initialTab="general"
      />

      <HelpDialog
        open={showHelp}
        onClose={() => setShowHelp(false)}
      />

      <NovelEditDialog
        open={showCreateDialog}
        onClose={() => setShowCreateDialog(false)}
        onSave={handleCreateNovelFromDialog}
      />
      <NovelEditDialog
        open={!!editingNovel}
        novel={editingNovel}
        onClose={() => setEditingNovel(null)}
        onSave={handleUpdateNovel}
      />
      <NovelDeleteDialog
        open={!!deletingNovel}
        novelTitle={deletingNovel?.title ?? ''}
        onClose={() => setDeletingNovel(null)}
        onConfirm={handleDeleteNovel}
      />

      <ExportDialog
        open={exportNovelId !== null}
        novelTitle={novels.find(n => n.id === exportNovelId)?.title ?? ''}
        onClose={() => setExportNovelId(null)}
        onExport={handleExportNovel}
      />
    </div>
  )
}
