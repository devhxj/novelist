import { useState, useEffect, useCallback, useRef, useMemo } from 'react'
import { useApp } from '@/hooks/useApp'
import type { novel, chapter } from '@/hooks/useApp'
import ActivityBar from '@/components/shell/ActivityBar'
import StatusBar from '@/components/shell/StatusBar'
import SidePanel from '@/components/workspace/SidePanel'
import EditorArea from '@/components/workspace/EditorArea'
import ChatPanel from '@/components/workspace/ChatPanel'
import GitHubLink from '@/components/shell/GitHubLink'
import type { OnMount } from '@monaco-editor/react'

interface Props {
  initialNovelId: number
}

export default function WorkspaceView({ initialNovelId }: Props) {
  const app = useApp()
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
  const [selectedChapterId, setSelectedChapterId] = useState<number | null>(null)
  const [editorContent, setEditorContent] = useState('')
  const [editorViewMode, setEditorViewMode] = useState<'content' | 'outline'>('content')
  const [isLoadingContent, setIsLoadingContent] = useState(false)
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const editorRef = useRef<Parameters<OnMount>[0] | null>(null)
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
    setSelectedChapterId(null)
    setEditorContent('')
  }, [activeNovelId])

  async function handleCreateChapter() {
    if (!chapterTitle.trim()) return
    await app.CreateChapter({ novel_id: activeNovelId, title: chapterTitle.trim() })
    setChapterTitle('')
    setShowCreateChapter(false)
    loadChapters()
  }

  async function handleSelectChapter(ch: chapter.Chapter) {
    setSelectedChapterId(ch.id)
    setEditorViewMode('content')
    setIsLoadingContent(true)
    const content = await app.GetChapterContent(activeNovelId, ch.chapter_number)
    setEditorContent(content)
    setIsLoadingContent(false)
  }

  function handleEditorChange(value: string | undefined) {
    const content = value ?? ''
    setEditorContent(content)
    if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
    saveTimerRef.current = setTimeout(() => {
      const chapter = chapters.find(c => c.id === selectedChapterId)
      if (!chapter || !activeNovelId) return
      app.SaveChapterContent({
        novel_id: activeNovelId,
        chapter_number: chapter.chapter_number,
        content,
      })
    }, 500)
  }

  const handleEditorMount: OnMount = (editor) => {
    editorRef.current = editor
  }

  // 自动选择活跃小说
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
  const selectedChapter = chapters.find(c => c.id === selectedChapterId)

  return (
    <div className="h-screen flex flex-col">
      <header className="h-11 flex items-center justify-between px-4 border-b bg-muted/10 shrink-0">
        <span className="text-sm font-medium">
          {activeNovel?.title ?? 'Goink'}
        </span>
        <GitHubLink />
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
          selectedChapterId={selectedChapterId}
          onSelectChapter={handleSelectChapter}
          onToggleBlock={toggleBlock}
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
          selectedChapter={selectedChapter}
          editorContent={editorContent}
          editorViewMode={editorViewMode}
          setEditorViewMode={setEditorViewMode}
          isLoadingContent={isLoadingContent}
          onEditorChange={handleEditorChange}
          onEditorMount={handleEditorMount}
          hasNovels={novels.length > 0}
          noChapters={chapters.length === 0}
          onGoToNovels={() => setActivePanel('novels')}
        />

        <ChatPanel />
      </div>

      <StatusBar />
    </div>
  )
}
