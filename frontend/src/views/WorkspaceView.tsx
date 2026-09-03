import { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import { flushSync } from 'react-dom'
import { useApp } from '@/hooks/useApp'
import type { novel, chapter, search } from '@/hooks/useApp'
import type { novelImport, reference, update } from '@/lib/novelist/types'
import { useNovelImport } from '@/hooks/useNovelImport'
import { pushToast } from '@/lib/toast'
import ActivityBar from '@/components/shell/ActivityBar'
import StatusBar from '@/components/shell/StatusBar'
import SidePanel from '@/components/sidebar/SidePanel'
import SearchPanel from '@/components/search/SearchPanel'
import ContentPanel, { type ContentPanelHandle } from '@/components/content/ContentPanel'
import CharacterListView from '@/components/character/CharacterListView'
import LocationListView from '@/components/location/LocationListView'
import ArcListView from '@/components/storyarc/ArcListView'
import TimelineView from '@/components/timeline/TimelineView'
import ReaderView from '@/components/reader/ReaderView'
import PreferenceView from '@/components/preference/PreferenceView'
import CorpusAreaView from '@/components/reference-anchor/CorpusAreaView'
import GitHistoryView from '@/components/git/GitHistoryView'
import BookshelfView from '@/components/novel/BookshelfView'
import NovelImportDialog from '@/components/novel/NovelImportDialog'
import NovelEditDialog from '@/components/novel/NovelEditDialog'
import NovelDeleteDialog from '@/components/novel/NovelDeleteDialog'
import ExportDialog from '@/components/export/ExportDialog'
import ChatPanel from '@/components/chat/ChatPanel'
import GitHubLink from '@/components/shell/GitHubLink'
import SettingsDialog from '@/components/settings/SettingsDialog'
import HelpDialog from '@/components/help/HelpDialog'
import UpdateDialog from '@/components/update/UpdateDialog'
import ProfileView from '@/components/profile/ProfileView'
import { Settings, User, HelpCircle, Moon, Sun, AlertTriangle, CheckCircle2, Clipboard, X } from 'lucide-react'
import { WindowMinimise, Quit } from '@/lib/novelist/runtime'
import Logo from '@/components/Logo'
import { useTheme, type Theme } from '@/hooks/useTheme'
import { useMaterializationWatcher } from '@/hooks/useMaterializationWatcher'
import { useLayoutState } from '@/hooks/useLayoutState'
import { useWindowState } from '@/hooks/useWindowState'
import { copyTextToClipboard } from '@/lib/clipboard'

const THEME_ICON: Record<Theme, React.ReactNode> = { light: <Moon className="w-5 h-5" />, dark: <Sun className="w-5 h-5" /> }
const THEME_LABEL: Record<Theme, string> = { light: '深色模式', dark: '浅色模式' }

interface Props {
  initialNovelId: number
  initialShowHelp?: boolean
  startupRecovery?: novelImport.ImportReconciliationResult | null
}

export default function WorkspaceView({ initialNovelId, initialShowHelp, startupRecovery }: Props) {
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
  const [platformOS, setPlatformOS] = useState('')
  const [updateResult, setUpdateResult] = useState<update.UpdateCheckResult | null>(null)
  const [showUpdateDialog, setShowUpdateDialog] = useState(false)
  const [referenceAnchors, setReferenceAnchors] = useState<reference.Anchor[]>([])
  const [selectedReferenceAnchorIds, setSelectedReferenceAnchorIds] = useState<number[]>([])
  const [referenceRefreshKey, setReferenceRefreshKey] = useState(0)
  const loadedRef = useRef(false)
  const autoUpdateCheckedRef = useRef(false)
  const { theme, toggle: toggleTheme } = useTheme()
  const { layout, setSidebarWidth, setChatPanelWidth, commitLayout } = useLayoutState()
  const { isMaximised, toggleMaximise } = useWindowState()
  const novelImportController = useNovelImport({
    onStartNovelImport: handleStartNovelImport,
    onCancelNovelImport: handleCancelNovelImport,
    onFinished: handleNovelImportFinished,
  })

  // ── 书籍管理弹窗 ──────────────────────────────────────
  const [editingNovel, setEditingNovel] = useState<novel.Novel | null>(null)
  const [deletingNovel, setDeletingNovel] = useState<novel.Novel | null>(null)
  const [showCreateDialog, setShowCreateDialog] = useState(false)
  const [exportNovelId, setExportNovelId] = useState<number | null>(null)

  // ── 窗口状态 ────────────────────────────────────────────

  useEffect(() => {
    // 只用于状态栏的系统标识，探测失败不值得打扰作者，静默保留默认文案即可。
    app.GetPlatform().then((info) => {
      if (info.os) setPlatformOS(info.os as string)
    }).catch(() => {})
  }, [app])

  useEffect(() => {
    let cancelled = false

    void (async () => {
      try {
        const settings = await app.GetUpdateCheckSettings()
        if (cancelled || autoUpdateCheckedRef.current) return
        autoUpdateCheckedRef.current = true
        if (cancelled || !settings.enabled || !settings.endpoint_url) return
        const result = await app.CheckForUpdates({
          task_id: `update-auto-${Date.now().toString(36)}`,
          manual: false,
        })
        if (cancelled) return
        if (result.status === 'update_available') {
          setUpdateResult(result)
          setShowUpdateDialog(true)
        }
      } catch {
        // Automatic update checks must never interrupt writing startup.
      }
    })()

    return () => { cancelled = true }
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

  // O15：章节删除后关闭编辑器里的正文/大纲 tab，残留 tab 不再制造"章节还在"的错觉。
  function handleChapterDeleted(ch: chapter.Chapter) {
    contentRef.current?.closeTabsByPaths([ch.file_path, `outlines/${String(ch.chapter_number).padStart(3, '0')}.md`])
    if (tabTarget?.path === ch.file_path) {
      setTabTarget(null)
    }
  }

  // ── 全局快捷键 ──────────────────────────────────────────

  // N3：Ctrl+S / Ctrl+Shift+V 原来挂在 ContentPanel 上，面板一卸载快捷键就静默失效。
  // 监听器提升到工作区层；内容面板不在时优雅空操作。
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 's') {
        e.preventDefault()
        contentRef.current?.saveActiveTab()
        return
      }
      if ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'v') {
        e.preventDefault()
        contentRef.current?.toggleActivePreview()
      }
    }
    document.addEventListener('keydown', handler)
    return () => document.removeEventListener('keydown', handler)
  }, [])

  // ── Approval ────────────────────────────────────────────

  // 提交与收尾分成两段：提交失败要原样抛回审批卡片（卡片保留反馈文本，给出重试 / 结束本轮）；
  // 提交成功后的 diff 标签页收尾即使出错也不能再抛，否则卡片会把已提交的审批当成"未提交"，
  // 作者点重试就会重复提交同一次审批。
  async function submitApproval(toolId: string, approved: boolean, feedback: string) {
    await app.ApproveTool(toolId, approved, feedback)
    try {
      if (approved) await contentRef.current?.handleDiffApprove(toolId)
      else contentRef.current?.handleDiffReject(toolId)
    } catch {
      // 收尾只影响 diff 标签页的开合，审批本身已生效，标签页可由作者手动关闭。
    }
  }

  async function handleApprove(toolId: string, feedback: string) {
    await submitApproval(toolId, true, feedback)
  }

  async function handleReject(toolId: string, feedback: string) {
    await submitApproval(toolId, false, feedback)
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
        setReferenceAnchors([])
        setSelectedReferenceAnchorIds([])
        setActivePanel('chapters')
        void app.SetActiveNovel({ novel_id: first.id })
      } else if (novels.length === 0) {
        setActivePanel('novels')
      }
    }, 0)
    return () => window.clearTimeout(timer)
  }, [app, novels, activeNovelId])

  function handleActivitySelect(id: string) {
    if (id === 'settings') {
      setShowSettings(true)
      return
    }
    if (id === 'search') {
      // A4：搜索结果占主区——左侧窄栏挤不下结果列表，且旧视图留在主区会误导。
      setSidebarPanel('search')
      setActivePanel('search')
    } else {
      setSidebarPanel(null)
      setActivePanel(id)
      contentRef.current?.clearHighlight()
    }
  }

  function handleSearchNavigateEntity(panelId: string, entityId: number) {
    // A4：离开搜索视图——侧栏跟随目标面板，紧凑搜索框不再悬空（否则它的提示指向已不存在的结果区）。
    setSidebarPanel(null)
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
    setSidebarPanel(null)
    flushSync(() => setActivePanel('chapters'))
    if (matchPos >= 0 && matchLen > 0) {
      contentRef.current?.openFileWithHighlight(filePath, title, matchPos, matchLen)
    } else {
      contentRef.current?.openFile(filePath, title)
    }
  }

  async function handleSelectNovel(n: novel.Novel) {
    setActiveNovelId(n.id)
    setReferenceAnchors([])
    setSelectedReferenceAnchorIds([])
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
      setReferenceAnchors([])
      setSelectedReferenceAnchorIds([])
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
      setReferenceAnchors([])
      setSelectedReferenceAnchorIds([])
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

  async function handleStartNovelImport(input: novelImport.StartNovelImportInput): Promise<novelImport.ImportRun> {
    return await app.StartNovelImport(input)
  }

  async function handleCancelNovelImport(input: novelImport.CancelNovelImportInput): Promise<novelImport.ImportRun> {
    return await app.CancelNovelImport(input)
  }

  async function handleNovelImportFinished(run: novelImport.ImportRun) {
    await loadNovels()
    if (!isSuccessfulNovelImportRun(run) || !run.created_novel_id) {
      return
    }

    const importedNovelId = run.created_novel_id
    setActiveNovelId(importedNovelId)
    setReferenceAnchors([])
    setSelectedReferenceAnchorIds([])
    setActivePanel('chapters')
    setTabTarget(null)
    setActiveContent('')
    await app.SetActiveNovel({ novel_id: importedNovelId })

    const chapters = await app.GetChapters(importedNovelId)
    const firstChapter = chapters[0]
    if (firstChapter) {
      const target = {
        path: firstChapter.file_path,
        title: `第${firstChapter.chapter_number}章 ${firstChapter.title}`,
      }
      setTabTarget(target)
      window.setTimeout(() => {
        contentRef.current?.openFile(target.path, target.title)
      }, 0)
    }
  }

  const activeNovel = novels.find(n => n.id === activeNovelId)

  // 材料化在语料区之外完成时，同样推进 refreshKey：覆盖横幅与语料区数据自动失效重取。
  const handleMaterializationCompleted = useCallback(() => {
    setReferenceRefreshKey((current) => current + 1)
  }, [])

  // 材料化全局监视：素材库页之外也能收到 run 完成/失败通知（状态栏展示）。
  const isCorpusViewActive = activePanel === 'reference'
  const { notifications: materializationNotices, dismiss: dismissMaterializationNotice, activeCount: materializationActiveCount } =
    useMaterializationWatcher(activeNovelId, referenceAnchors, !isCorpusViewActive, handleMaterializationCompleted)

  // 同一批完成通知同步进统一通知通道（F9）：toast 带"打开素材库"动作，
  // 作者在任意面板都能感知后台结果并一步跳回制作页。
  const seenNoticeRunIdsRef = useRef<Set<string>>(new Set())
  useEffect(() => {
    for (const notice of materializationNotices) {
      if (seenNoticeRunIdsRef.current.has(notice.run_id)) continue
      seenNoticeRunIdsRef.current.add(notice.run_id)
      if (notice.status === 'failed') {
        pushToast({
          kind: 'error',
          message: `《${notice.anchor_title}》材料化失败`,
          description: notice.error_message ?? undefined,
        })
      } else {
        pushToast({
          kind: 'success',
          message: `《${notice.anchor_title}》材料化完成`,
          action: { label: '打开素材库', run: () => { setActivePanel('reference'); setSidebarPanel(null) } },
        })
      }
    }
  }, [materializationNotices])

  const activeChapterNumber = useMemo(() => {
    const match = tabTarget?.path.match(/^chapters\/(\d+)\.md$/)
    return match ? Number(match[1]) : null
  }, [tabTarget?.path])

  const handleReferenceAnchorsChange = useCallback((anchors: reference.Anchor[]) => {
    const validIds = new Set(anchors.map((anchor) => anchor.anchor_id))
    setReferenceAnchors(anchors)
    setSelectedReferenceAnchorIds((current) => current.filter((id) => validIds.has(id)))
  }, [])

  const handleReferenceMutation = useCallback(() => {
    setReferenceRefreshKey((current) => current + 1)
  }, [])

  // ── 窗口按钮样式 ────────────────────────────────────────

  const winBtn = 'w-12 h-full flex items-center justify-center cursor-pointer text-foreground/80 hover:text-foreground hover:bg-black/25 hover:shadow-md transition-all'
  const closeBtn = 'w-12 h-full flex items-center justify-center cursor-pointer text-foreground/80 hover:text-destructive-foreground hover:bg-destructive transition-colors'

  return (
    <div className="h-screen flex flex-col overflow-hidden">
      <header
        className="app-window-drag h-11 flex items-center border-b bg-sidebar shrink-0 select-none cursor-default"
        onDoubleClick={() => { void toggleMaximise() }}
      >
        <Logo className="h-7 w-7 ml-3" />
        <span className="text-sm font-medium pl-2 flex-1">
          {activeNovel?.title ?? 'Novelist'}
        </span>
        <div className="app-window-no-drag flex items-center h-full">
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
                onClick={() => { void toggleMaximise() }}
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

      <StartupImportRecoveryBanner recovery={startupRecovery} />

      <div className="flex-1 flex min-h-0 overflow-hidden">
        <ActivityBar
          activeId={sidebarPanel ?? activePanel}
          bookToolsVisible={!!activeNovel}
          onSelect={handleActivitySelect}
        />

        {(sidebarPanel ?? activePanel) !== 'git-history' && (
          <SidePanel
            width={layout.sidebar_width}
            onWidthChange={setSidebarWidth}
            onWidthCommit={(width) => { void commitLayout({ sidebar_width: width }) }}
            activePanel={sidebarPanel ?? activePanel}
            novels={novels}
            novelId={activeNovelId}
            onSelectNovel={handleSelectNovel}
            onSelectChapter={handleSelectChapter}
            onSelectNovelist={handleSelectNovelist}
            onExportNovel={(id) => setExportNovelId(id)}
            onChapterDeleted={handleChapterDeleted}
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
            selectedReferenceAnchorIds={selectedReferenceAnchorIds}
            referenceRefreshKey={referenceRefreshKey}
            onReferenceSelectionChange={setSelectedReferenceAnchorIds}
            onReferenceAnchorsChange={handleReferenceAnchorsChange}
            onReferenceMutation={handleReferenceMutation}
          />
        )}

        {activePanel === 'search' ? (
          <div className="flex min-w-0 flex-1 flex-col overflow-hidden bg-background" data-testid="search-main-view">
            <div className="border-b px-4 py-2.5">
              <h2 className="text-sm font-semibold text-foreground">全局搜索</h2>
              <p className="mt-0.5 text-[11px] text-muted-foreground">搜索人物、地点、时间线与正文；点击结果跳转到对应位置。</p>
            </div>
            <div className="min-h-0 flex-1 overflow-y-auto">
              <SearchPanel
                novelId={activeNovelId}
                query={searchQuery}
                results={searchResults}
                onResultsChange={(q, r) => { setSearchQuery(q); setSearchResults(r) }}
                onNavigateEntity={handleSearchNavigateEntity}
                onNavigateChapter={handleSearchNavigateChapter}
              />
            </div>
          </div>
        ) : activePanel === 'novels' ? (
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
            novelImportState={novelImportController.state}
            onBeginNovelImportSelection={novelImportController.beginSelecting}
            onCancelNovelImportSelection={novelImportController.markSelectionCancelled}
            onStartNovelImportFromPath={novelImportController.startFromPath}
          />
        ) : activePanel !== 'characters' && activePanel !== 'locations' && activePanel !== 'storyarcs' && activePanel !== 'timeline' && activePanel !== 'reader' && activePanel !== 'preferences' && activePanel !== 'reference' && activePanel !== 'git-history' && activePanel !== 'profile' && activePanel !== 'search' && (
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
          <TimelineView novelId={activeNovelId} focusEntryId={timelineFocusId} onOpenInEditor={(path, title, readOnly) => contentRef.current?.openFile(path, title, readOnly)} />
        ) : activePanel === 'reader' ? (
          <ReaderView novelId={activeNovelId} focusId={readerFocusId} />
        ) : activePanel === 'preferences' ? (
          <PreferenceView novelId={activeNovelId} focusId={preferenceFocusId} />
        ) : activePanel === 'reference' ? (
          <CorpusAreaView
            key={activeNovelId}
            novelId={activeNovelId}
            refreshKey={referenceRefreshKey}
            anchors={referenceAnchors}
            selectedAnchorIds={selectedReferenceAnchorIds}
            onMaterializationChange={handleReferenceMutation}
          />
        ) : activePanel === 'git-history' ? (
          <GitHistoryView novelId={activeNovelId} />
        ) : activePanel === 'profile' ? (
          <ProfileView />
        ) : null}

        {/* 个人中心会整体占住内容区，但聊天不能跟着卸载：
            ChatInput 的草稿是非受控状态，卸载即丢。挂载保留、仅用 CSS 隐藏（O14）。 */}
        <div
          className={activePanel === 'profile' ? 'hidden' : 'contents'}
          aria-hidden={activePanel === 'profile'}
        >
          <ChatPanel
            width={layout.chat_panel_width}
            onWidthChange={setChatPanelWidth}
            onWidthCommit={(width) => { void commitLayout({ chat_panel_width: width }) }}
            novelId={activeNovelId}
            chapterNumber={activeChapterNumber}
            referenceRefreshKey={referenceRefreshKey}
            onOpenPlans={() => { setActivePanel('timeline'); setSidebarPanel(null) }}
            onApprove={handleApprove}
            onReject={handleReject}
            onApprovalFileEdit={handleApprovalFileEdit}
          />
        </div>
      </div>

      <StatusBar
        content={activeContent}
        isDirty={isDirty}
        materializationActiveCount={materializationActiveCount}
        materializationNotifications={materializationNotices}
        onDismissMaterializationNotification={dismissMaterializationNotice}
        onOpenReference={() => { setActivePanel('reference'); setSidebarPanel(null) }}
      />

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
        novelId={deletingNovel?.id ?? null}
        novelTitle={deletingNovel?.title ?? ''}
        onClose={() => setDeletingNovel(null)}
        onConfirm={handleDeleteNovel}
      />

      <ExportDialog
        open={exportNovelId !== null}
        novelId={exportNovelId}
        novelTitle={novels.find(n => n.id === exportNovelId)?.title ?? ''}
        onClose={() => setExportNovelId(null)}
        onExport={handleExportNovel}
      />

      <NovelImportDialog
        state={novelImportController.state}
        onCancel={() => { void novelImportController.cancel() }}
        onClose={novelImportController.close}
      />

      <UpdateDialog
        open={showUpdateDialog}
        result={updateResult}
        onClose={() => setShowUpdateDialog(false)}
        onDismissVersion={async (version) => {
          const settings = await app.GetUpdateCheckSettings()
          await app.SaveUpdateCheckSettings({
            enabled: settings.enabled,
            endpoint_url: settings.endpoint_url,
            dismissed_version: version,
          })
        }}
      />
    </div>
  )
}

function StartupImportRecoveryBanner({ recovery }: { recovery?: novelImport.ImportReconciliationResult | null }) {
  const [dismissed, setDismissed] = useState(false)
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')

  if (!recovery || dismissed) return null

  const cleaned = recovery.reconciled_runs.filter(run => run.state === 'cleanup_completed').length
  const warned = recovery.reconciled_runs.filter(run => run.state === 'completed_with_warning').length
  const blocked = recovery.blocked_runs.length
  const total = cleaned + warned + blocked
  if (total === 0) return null

  const hasBlocked = blocked > 0
  const copyLabel = copyState === 'copied' ? '已复制' : copyState === 'failed' ? '复制失败' : '复制诊断'
  const diagnosticText = JSON.stringify(recovery, null, 2)

  async function handleCopyDiagnostics() {
    try {
      await copyTextToClipboard(diagnosticText)
      setCopyState('copied')
      window.setTimeout(() => setCopyState('idle'), 1800)
    } catch {
      setCopyState('failed')
      window.setTimeout(() => setCopyState('idle'), 1800)
    }
  }

  return (
    <section
      role="status"
      aria-label="导入启动恢复状态"
      className={`shrink-0 border-b px-4 py-2.5 text-sm ${hasBlocked ? 'border-danger-border bg-danger-bg' : 'border-border bg-tag-amber'}`}
    >
      <div className="flex flex-col gap-2 lg:flex-row lg:items-start lg:justify-between">
        <div className="flex min-w-0 gap-2.5">
          <div className={`mt-0.5 shrink-0 ${hasBlocked ? 'text-destructive' : 'text-tag-amber-foreground'}`}>
            {hasBlocked ? <AlertTriangle className="h-4 w-4" /> : <CheckCircle2 className="h-4 w-4" />}
          </div>
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-foreground">导入恢复已处理</h2>
            <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 text-xs text-muted-foreground">
              {cleaned > 0 && <span>已清理 {cleaned} 个未完成导入</span>}
              {warned > 0 && <span>{warned} 个导入已保留并带有警告</span>}
              {blocked > 0 && <span>{blocked} 个导入需要手动处理</span>}
            </div>
            {hasBlocked && (
              <div className="mt-2 flex flex-col gap-1 text-xs text-foreground">
                {recovery.blocked_runs.slice(0, 3).map(run => (
                  <div key={run.task_id} className="break-words">
                    <span className="font-medium">{run.task_id}</span>
                    {run.error?.message ? <span className="text-muted-foreground"> · {run.error.message}</span> : null}
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-2 self-start">
          <button
            type="button"
            onClick={() => void handleCopyDiagnostics()}
            className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background/80 px-2.5 text-xs font-medium text-foreground transition-colors hover:bg-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <Clipboard className="h-3.5 w-3.5" />
            {copyLabel}
          </button>
          {!hasBlocked && (
            <button
              type="button"
              onClick={() => setDismissed(true)}
              className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-background/80 hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              aria-label="关闭导入恢复提示"
            >
              <X className="h-4 w-4" />
            </button>
          )}
        </div>
      </div>
    </section>
  )
}

function isSuccessfulNovelImportRun(run: novelImport.ImportRun): boolean {
  return run.state === 'completed' || run.state === 'completed_with_warning'
}
