import { useState, useEffect, useCallback, useRef } from 'react'
import { useApp } from '@/hooks/useApp'
import type { novel, chapter } from '@/hooks/useApp'
import ActivityBar from '@/components/shell/ActivityBar'
import StatusBar from '@/components/shell/StatusBar'
import SidePanel from '@/components/sidebar/SidePanel'
import ContentPanel, { type ContentPanelHandle } from '@/components/content/ContentPanel'
import CharacterGraph from '@/components/character/CharacterGraph'
import LocationGraph from '@/components/location/LocationGraph'
import ArcListView from '@/components/storyarc/ArcListView'
import TimelineView from '@/components/timeline/TimelineView'
import ReaderView from '@/components/reader/ReaderView'
import PreferenceView from '@/components/preference/PreferenceView'
import BookshelfView from '@/components/novel/BookshelfView'
import NovelEditDialog from '@/components/novel/NovelEditDialog'
import NovelDeleteDialog from '@/components/novel/NovelDeleteDialog'
import ChatPanel from '@/components/chat/ChatPanel'
import GitHubLink from '@/components/shell/GitHubLink'
import SettingsDialog from '@/components/settings/SettingsDialog'
import { Settings } from 'lucide-react'
import { WindowMinimise, WindowToggleMaximise, WindowIsMaximised, Quit } from '@/lib/wailsjs/runtime/runtime'

interface Props {
  initialNovelId: number
}

export default function WorkspaceView({ initialNovelId }: Props) {
  const app = useApp()
  const contentRef = useRef<ContentPanelHandle>(null)

  const [novels, setNovels] = useState<novel.Novel[]>([])
  const [activeNovelId, setActiveNovelId] = useState(initialNovelId)
  const [activePanel, setActivePanel] = useState(initialNovelId ? 'chapters' : 'novels')
  const [showCreate, setShowCreate] = useState(false)
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [showSettings, setShowSettings] = useState(false)
  const [tabTarget, setTabTarget] = useState<{ path: string; title: string } | null>(null)
  const [activeContent, setActiveContent] = useState('')
  const [isMaximised, setIsMaximised] = useState(false)
  const [platformOS, setPlatformOS] = useState('')
  const loadedRef = useRef(false)

  // ── 书籍管理弹窗 ──────────────────────────────────────
  const [editingNovel, setEditingNovel] = useState<novel.Novel | null>(null)
  const [deletingNovel, setDeletingNovel] = useState<novel.Novel | null>(null)
  const [showCreateDialog, setShowCreateDialog] = useState(false)

  // ── 窗口状态 ────────────────────────────────────────────

  useEffect(() => {
    app.GetPlatform().then((info) => {
      if (info.os) setPlatformOS(info.os as string)
    })
    WindowIsMaximised().then(setIsMaximised)
  }, [])

  // ── 作品列表 ────────────────────────────────────────────

  const loadNovels = useCallback(async () => {
    const list = await app.GetNovels()
    setNovels(list ?? [])
    loadedRef.current = true
  }, [])

  useEffect(() => { loadNovels() }, [loadNovels])

  // ── SidePanel → ContentPanel 桥接 ─────────────────────────

  function handleSelectChapter(ch: chapter.Chapter) {
    setTabTarget({ path: ch.file_path, title: `第${ch.chapter_number}章 ${ch.title}` })
    contentRef.current?.openFile(ch.file_path, `第${ch.chapter_number}章 ${ch.title}`)
  }

  function handleSelectGoink() {
    setTabTarget({ path: 'goink.md', title: '故事状态' })
    contentRef.current?.openFile('goink.md', '故事状态')
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

  // ── 自动选择小说 ────────────────────────────────────────

  useEffect(() => {
    if (!loadedRef.current) return
    const exists = novels.find(n => n.id === activeNovelId)
    if (!exists && novels.length > 0) {
      const first = novels[0]
      setActiveNovelId(first.id)
      setActivePanel('chapters')
      app.SetActiveNovel({ novel_id: first.id })
    } else if (novels.length === 0) {
      setActivePanel('novels')
    }
  }, [novels, activeNovelId])

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

  const activeNovel = novels.find(n => n.id === activeNovelId)

  // ── 窗口按钮样式 ────────────────────────────────────────

  const winBtn = 'w-12 h-full flex items-center justify-center cursor-pointer text-foreground/80 hover:text-foreground hover:bg-black/25 hover:shadow-md transition-all'
  const closeBtn = 'w-12 h-full flex items-center justify-center cursor-pointer text-foreground/80 hover:text-white hover:bg-red-500 transition-colors'

  return (
    <div className="h-screen flex flex-col">
      <header
        className="h-11 flex items-center border-b bg-sidebar shrink-0"
        data-wails-drag
      >
        <span className="text-sm font-medium pl-3 flex-1">
          {activeNovel?.title ?? 'Goink'}
        </span>
        <div className="flex items-center h-full" style={{ '--wails-draggable': 'no-drag' } as React.CSSProperties}>
          <GitHubLink />
          <button
            onClick={() => setShowSettings(true)}
            className="text-muted-foreground hover:text-foreground transition-colors cursor-pointer w-8 h-8 flex items-center justify-center ml-2 mr-1"
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

      <div className="flex-1 flex min-h-0">
        <ActivityBar activeId={activePanel} onSelect={setActivePanel} />

        <SidePanel
          activePanel={activePanel}
          novels={novels}
          novelId={activeNovelId}
          onSelectNovel={handleSelectNovel}
          onSelectChapter={handleSelectChapter}
          onSelectGoink={handleSelectGoink}
          target={tabTarget}
          showCreate={showCreate}
          setShowCreate={setShowCreate}
          title={title}
          setTitle={setTitle}
          description={description}
          setDescription={setDescription}
          onCreateNovel={handleCreateNovel}
        />

        {activePanel === 'novels' ? (
          <BookshelfView
            novels={novels}
            activeNovelId={activeNovelId}
            onSelectNovel={handleSelectNovel}
            onEditNovel={setEditingNovel}
            onDeleteNovel={setDeletingNovel}
            onCreateNovel={() => setShowCreateDialog(true)}
          />
        ) : activePanel !== 'characters' && activePanel !== 'locations' && activePanel !== 'storyarcs' && activePanel !== 'timeline' && activePanel !== 'reader' && activePanel !== 'preferences' && (
          <ContentPanel ref={contentRef} novelId={activeNovelId} onContentChange={setActiveContent} />
        )}

        {activePanel === 'characters' ? (
          <CharacterGraph novelId={activeNovelId} />
        ) : activePanel === 'locations' ? (
          <LocationGraph novelId={activeNovelId} />
        ) : activePanel === 'storyarcs' ? (
          <ArcListView novelId={activeNovelId} />
        ) : activePanel === 'timeline' ? (
          <TimelineView novelId={activeNovelId} />
        ) : activePanel === 'reader' ? (
          <ReaderView novelId={activeNovelId} />
        ) : activePanel === 'preferences' ? (
          <PreferenceView novelId={activeNovelId} />
        ) : null}

        <ChatPanel novelId={activeNovelId} onApprove={handleApprove} onReject={handleReject} />
      </div>

      <StatusBar content={activeContent} />

      <SettingsDialog
        open={showSettings}
        onClose={() => setShowSettings(false)}
        initialTab="general"
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
    </div>
  )
}
