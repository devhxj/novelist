import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { useApp } from '@/hooks/useApp'
import type { novel, chapter } from '@/hooks/useApp'
import { useEditorTabs } from '@/hooks/useEditorTabs'
import ActivityBar from '@/components/shell/ActivityBar'
import StatusBar from '@/components/shell/StatusBar'
import SidePanel from '@/components/novel/SidePanel'
import EditorArea from '@/components/editor/EditorArea'
import ChatPanel from '@/components/chat/ChatPanel'
import GitHubLink from '@/components/shell/GitHubLink'
import SettingsDialog from '@/components/settings/SettingsDialog'
import { Settings } from 'lucide-react'
import type { OnMount } from '@monaco-editor/react'
import { EventsOn, EventsOff } from '@/lib/wailsjs/runtime/runtime'

interface Props {
  initialNovelId: number
}

export default function WorkspaceView({ initialNovelId }: Props) {
  const app = useApp()
  const {
    tabs, activeTab, activeTabId,
    openTab, closeTab, setActiveTabId,
    updateTab, openDiffTab,
  } = useEditorTabs()

  const [novels, setNovels] = useState<novel.Novel[]>([])
  const [activeNovelId, setActiveNovelId] = useState(initialNovelId)
  const [activePanel, setActivePanel] = useState(initialNovelId ? 'chapters' : 'novels')
  const [showCreate, setShowCreate] = useState(false)
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [chapters, setChapters] = useState<chapter.Chapter[]>([])
  const [chapterTitle, setChapterTitle] = useState('')
  const [showCreateChapter, setShowCreateChapter] = useState(false)
  const [expandedBlocks, setExpandedBlocks] = useState<Set<number>>(new Set())
  const [isLoadingContent, setIsLoadingContent] = useState(false)
  const [showSettings, setShowSettings] = useState(false)
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const editorRef = useRef<Parameters<OnMount>[0] | null>(null)
  const savingTabRef = useRef<{ id: string; path: string; content: string } | null>(null)
  const novelIdRef = useRef(activeNovelId)
  const loadedRef = useRef(false)
  const BLOCK_SIZE = 100

  const chapterBlocks = useMemo(() => {
    const sorted = [...chapters].sort((a, b) => b.chapter_number - a.chapter_number)
    const blocks: { key: number; start: number; end: number; chs: chapter.Chapter[] }[] = []
    for (let i = 0; i < sorted.length; i += BLOCK_SIZE) {
      const slice = sorted.slice(i, Math.min(i + BLOCK_SIZE, sorted.length))
      slice.sort((a, b) => a.chapter_number - b.chapter_number)
      blocks.push({
        key: i / BLOCK_SIZE,
        start: slice[0].chapter_number,
        end: slice[slice.length - 1].chapter_number,
        chs: slice,
      })
    }
    return blocks
  }, [chapters])

  function toggleBlock(key: number) {
    setExpandedBlocks(prev => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  // ── Data loading ──────────────────────────────────────────

  const loadNovels = useCallback(async () => {
    const list = await app.GetNovels()
    setNovels(list ?? [])
    loadedRef.current = true
  }, [])

  useEffect(() => { loadNovels() }, [loadNovels])

  const loadChapters = useCallback(async () => {
    if (!activeNovelId) { setChapters([]); return }
    const list = await app.GetChapters(activeNovelId)
    setChapters(list ?? [])
  }, [activeNovelId])

  useEffect(() => { loadChapters() }, [loadChapters])

  useEffect(() => {
    return () => { if (saveTimerRef.current) clearTimeout(saveTimerRef.current) }
  }, [])

  useEffect(() => {
    novelIdRef.current = activeNovelId
  }, [activeNovelId])

  // 监听 AI edit 工具写入文件后的刷新通知
  useEffect(() => {
    const unsub = EventsOn('file:changed', async (data: any) => {
      if (data.novel_id !== activeNovelId) return
      // 刷新侧边栏章节列表
      if (data.path && (data.path.startsWith('chapters/') || data.path.startsWith('outlines/') || data.path === 'goink.md')) {
        loadChapters()
      }
      // 刷新已打开的编辑器 tab
      const activeId = tabs.find(t => t.id === activeTabId)
      if (activeId && activeTab && activeTab.path === data.path) {
        try {
          const content = await app.GetContent(data.novel_id, data.path)
          updateTab(activeTab.id, { content, isDirty: false })
        } catch { /* 文件可能被删 */ }
      }
    })
    return () => unsub()
  }, [activeNovelId, activeTabId, activeTab, tabs, loadChapters])

  // ── Tab management ────────────────────────────────────────

  async function handleSelectChapter(ch: chapter.Chapter) {
    const path = ch.file_path
    const title = `第${ch.chapter_number}章 ${ch.title}`
    const existing = tabs.find(t => t.path === path && t.type === 'edit')
    if (existing) { setActiveTabId(existing.id); return }

    setIsLoadingContent(true)
    try {
      const content = await app.GetContent(activeNovelId, path)
      openTab({ type: 'edit', path, title, content, isDirty: false, viewMode: 'content' })
    } catch {
      openTab({ type: 'edit', path, title, content: '', isDirty: false, viewMode: 'content' })
    } finally {
      setIsLoadingContent(false)
    }
  }

  function handleSelectGoink() {
    const path = 'goink.md'
    const title = '故事状态'
    const existing = tabs.find(t => t.path === path && t.type === 'edit')
    if (existing) { setActiveTabId(existing.id); return }

    setIsLoadingContent(true)
    app.GetContent(activeNovelId, path).then(content => {
      openTab({ type: 'edit', path, title, content, isDirty: false, viewMode: 'content' })
    }).catch(() => {
      openTab({ type: 'edit', path, title, content: '', isDirty: false, viewMode: 'content' })
    }).finally(() => setIsLoadingContent(false))
  }

  function handleEditorChange(tabId: string, value: string | undefined) {
    const content = value ?? ''
    updateTab(tabId, { content, isDirty: true })

    if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
    const tab = tabs.find(t => t.id === tabId)
    if (!tab) return
    savingTabRef.current = { id: tabId, path: tab.path, content }
    saveTimerRef.current = setTimeout(() => {
      if (!activeNovelId || !savingTabRef.current) return
      const s = savingTabRef.current
      app.SaveContent({ novel_id: activeNovelId, path: s.path, content: s.content })
      updateTab(s.id, { isDirty: false })
    }, 500)
  }

  const handleEditorMount: OnMount = (editor) => {
    editorRef.current = editor
    editor.onDidBlurEditorText(() => {
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
      const s = savingTabRef.current
      const nid = novelIdRef.current
      if (!nid || !s) return
      app.SaveContent({ novel_id: nid, path: s.path, content: s.content })
      updateTab(s.id, { isDirty: false })
    })
  }

  function handleSetViewMode(tabId: string, mode: 'content' | 'outline') {
    updateTab(tabId, { viewMode: mode })
  }

  // ── Approval event listener ───────────────────────────────

  useEffect(() => {
    EventsOn('approval:requested', (data: any) => {
      const p = data?.payload ?? {}
      let title = `diff: ${p.path || ''}`
      if (p.path?.startsWith('chapters/')) {
        const num = p.path.replace('chapters/', '').replace('.md', '')
        title = `diff: 第${parseInt(num)}章`
      } else if (p.path === 'goink.md') {
        title = 'diff: 故事状态'
      }
      openDiffTab({
        path: p.path ?? '',
        title,
        diff: p.diff ?? '',
        original: p.original ?? '',
        modified: p.modified ?? '',
        changeType: p.change_type ?? '',
        reason: p.reason ?? '',
        toolId: data?.tool_id ?? '',
      })
    })
    return () => { EventsOff('approval:requested') }
  }, [openDiffTab])

  async function handleApprove(toolId: string, feedback: string) {
    const diffTab = tabs.find(t => t.type === 'diff' && t.toolId === toolId)
    await app.ApproveTool(toolId, true, feedback)
    // 刷新同路径的编辑 tab 内容
    if (diffTab) {
      const editTab = tabs.find(t => t.type === 'edit' && t.path === diffTab.path)
      if (editTab) {
        try {
          const content = await app.GetContent(activeNovelId, diffTab.path)
          updateTab(editTab.id, { content, isDirty: false })
        } catch { /* tab may have been closed */ }
      }
      closeTab(diffTab.id)
    }
  }

  async function handleReject(toolId: string, feedback: string) {
    const diffTab = tabs.find(t => t.type === 'diff' && t.toolId === toolId)
    await app.ApproveTool(toolId, false, feedback)
    if (diffTab) closeTab(diffTab.id)
  }

  // ── Chapter operations ────────────────────────────────────

  async function handleCreateChapter() {
    if (!chapterTitle.trim()) return
    await app.CreateChapter({ novel_id: activeNovelId, title: chapterTitle.trim() })
    setChapterTitle('')
    setShowCreateChapter(false)
    loadChapters()
  }

  // ── Auto-select active novel ──────────────────────────────

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
          activeNovelId={activeNovelId}
          onSelectNovel={handleSelectNovel}
          chapters={chapters}
          chapterBlocks={chapterBlocks}
          expandedBlocks={expandedBlocks}
          target={activeTab?.type === 'edit' ? { path: activeTab.path, title: activeTab.title } : null}
          onSelectChapter={handleSelectChapter}
          onToggleBlock={toggleBlock}
          onSelectGoink={handleSelectGoink}
          showCreate={showCreate}
          setShowCreate={setShowCreate}
          title={title}
          setTitle={setTitle}
          description={description}
          setDescription={setDescription}
          onCreateNovel={handleCreateNovel}
          showCreateChapter={showCreateChapter}
          setShowCreateChapter={setShowCreateChapter}
          chapterTitle={chapterTitle}
          setChapterTitle={setChapterTitle}
          onCreateChapter={handleCreateChapter}
        />

        <EditorArea
          tabs={tabs}
          activeTab={activeTab}
          activeTabId={activeTabId}
          isLoadingContent={isLoadingContent}
          onSelectTab={setActiveTabId}
          onCloseTab={closeTab}
          onEditorChange={handleEditorChange}
          onEditorMount={handleEditorMount}
          onSetViewMode={handleSetViewMode}
          hasNovels={novels.length > 0}
          noChapters={chapters.length === 0}
          onGoToNovels={() => setActivePanel('novels')}
        />

        <ChatPanel novelId={activeNovelId} onApprove={handleApprove} onReject={handleReject} />
      </div>

      <StatusBar />

      <SettingsDialog
        open={showSettings}
        onClose={() => setShowSettings(false)}
        initialTab="general"
      />
    </div>
  )
}
