import { useState, useEffect, useCallback, useRef } from 'react'
import { useApp } from '@/hooks/useApp'
import type { novel, chapter } from '@/hooks/useApp'
import ActivityBar from '@/components/shell/ActivityBar'
import StatusBar from '@/components/shell/StatusBar'
import SidePanel from '@/components/sidebar/SidePanel'
import ContentPanel, { type ContentPanelHandle } from '@/components/content/ContentPanel'
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
  const loadedRef = useRef(false)

  // ── 窗口状态 ────────────────────────────────────────────

  useEffect(() => {
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

  const activeNovel = novels.find(n => n.id === activeNovelId)

  // ── 窗口按钮样式 ────────────────────────────────────────

  const winBtn = 'flex items-center justify-center w-6 h-full cursor-pointer text-muted-foreground hover:text-foreground hover:bg-muted/50 transition-colors'

  return (
    <div className="h-screen flex flex-col">
      <header
        className="h-11 flex items-center border-b bg-sidebar shrink-0"
        data-wails-drag
      >
        <span className="text-sm font-medium pl-3 flex-1">
          {activeNovel?.title ?? 'Goink'}
        </span>
        <div className="flex items-center gap-1 pr-1" style={{ '--wails-draggable': 'no-drag' } as React.CSSProperties}>
          <GitHubLink />
          <button
            onClick={() => setShowSettings(true)}
            className="text-muted-foreground hover:text-foreground transition-colors cursor-pointer w-8 h-8 flex items-center justify-center"
            title="设置"
          >
            <Settings className="w-5 h-5" />
          </button>
          <div className="w-px h-4 bg-border/30 mx-0.5" />
          <button onClick={WindowMinimise} className={winBtn} title="最小化">
            <svg width="10" height="10" viewBox="0 0 10 10"><path d="M1.5 5h7" stroke="currentColor" strokeWidth="1" strokeLinecap="round"/></svg>
          </button>
          <button
            onClick={() => { WindowToggleMaximise(); setIsMaximised(!isMaximised) }}
            className={winBtn}
            title={isMaximised ? '还原' : '最大化'}
          >
            {isMaximised ? (
              <svg width="10" height="10" viewBox="0 0 10 10">
                <rect x="3" y="1.5" width="6" height="6" rx="1" fill="none" stroke="currentColor" strokeWidth=".85" />
                <rect x="1" y="2.5" width="6" height="6" rx="1" fill="var(--color-sidebar)" stroke="currentColor" strokeWidth=".85" />
              </svg>
            ) : (
              <svg width="10" height="10" viewBox="0 0 10 10"><rect x="1.5" y="1.5" width="7" height="7" stroke="currentColor" strokeWidth=".9" rx=".5" fill="none" /></svg>
            )}
          </button>
          <button
            onClick={Quit}
            className={`${winBtn} hover:!bg-red-500 hover:!text-white`}
            title="关闭"
          >
            <svg width="10" height="10" viewBox="0 0 10 10"><path d="M2 2l6 6M8 2l-6 6" stroke="currentColor" strokeWidth=".9" strokeLinecap="round"/></svg>
          </button>
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

        <ContentPanel ref={contentRef} novelId={activeNovelId} onContentChange={setActiveContent} />

        <ChatPanel novelId={activeNovelId} onApprove={handleApprove} onReject={handleReject} />
      </div>

      <StatusBar content={activeContent} />

      <SettingsDialog
        open={showSettings}
        onClose={() => setShowSettings(false)}
        initialTab="general"
      />
    </div>
  )
}
