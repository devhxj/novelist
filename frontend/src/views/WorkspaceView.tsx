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
  const loadedRef = useRef(false)

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

  return (
    <div className="h-screen flex flex-col">
      <header className="h-11 flex items-center justify-between pl-4 pr-2 border-b bg-muted/10 shrink-0">
        <span className="text-sm font-medium">
          {activeNovel?.title ?? 'Goink'}
        </span>
        <div className="flex items-center gap-3">
          <GitHubLink />
          <button
            onClick={() => setShowSettings(true)}
            className="text-muted-foreground hover:text-foreground transition-colors cursor-pointer"
            title="设置"
          >
            <Settings className="w-5 h-5" />
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
