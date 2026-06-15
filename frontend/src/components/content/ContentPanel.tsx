import { useState, useEffect, useCallback, useRef, forwardRef, useImperativeHandle } from 'react'
import { type OnMount, DiffEditor } from '@monaco-editor/react'
import { FileText, Loader2 } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { useEditorTabs } from '@/hooks/useEditorTabs'
import { EventsOn } from '@/lib/wailsjs/runtime/runtime'
import TabBar from './TabBar'
import ContentEditor from './ContentEditor'
import OutlineViewer from './OutlineViewer'
import SkillPreview from './SkillPreview'
import Markdown from '@/components/Markdown'
import { outlinePath, isContentPath, isOutlinePath, isSkillPath, skillNameFromPath } from './types'
import type { EditorTab } from './types'

export interface ContentPanelHandle {
  openFile: (path: string, title: string, readOnly?: boolean) => void
  closeAllTabs: () => void
  openDiffTab: (data: {
    path: string; title: string; diff: string; original: string; modified: string
    changeType: string; reason: string; toolId: string
  }) => void
  handleDiffApprove: (toolId: string) => Promise<void>
  handleDiffReject: (toolId: string) => void
}

interface Props {
  novelId: number
  onContentChange?: (content: string) => void
}

const ContentPanel = forwardRef<ContentPanelHandle, Props>(function ContentPanel(
  { novelId, onContentChange }, ref
) {
  const app = useApp()
  const {
    tabs, activeTab, activeTabId,
    openTab, closeTab, closeAllTabs, setActiveTabId,
    updateTab, openDiffTab,
  } = useEditorTabs()

  const [isLoading, setIsLoading] = useState(false)
  const saveTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const editorRef = useRef<Parameters<OnMount>[0] | null>(null)
  const savingRef = useRef<{ id: string; path: string; content: string } | null>(null)
  const novelIdRef = useRef(novelId)
  const tabsRef = useRef(tabs)

  useEffect(() => { novelIdRef.current = novelId }, [novelId])
  useEffect(() => { tabsRef.current = tabs }, [tabs])

  useEffect(() => {
    return () => { if (saveTimerRef.current) clearTimeout(saveTimerRef.current) }
  }, [])

  useEffect(() => {
    if (activeTab?.type === 'file') {
      onContentChange?.(activeTab.content ?? '')
    }
  }, [activeTab, onContentChange])

  // ── 切换 viewMode：按需加载大纲内容 ──────────────────────

  const handleSetViewMode = useCallback((tabId: string, mode: 'content' | 'outline') => {
    const tab = tabs.find(t => t.id === tabId)
    if (!tab) return

    updateTab(tabId, { viewMode: mode })

    // 切换到大纲时，如果未加载（或上次加载时文件不存在）则重新加载
    if (mode === 'outline' && tab.type === 'file' && !tab.outlineContent) {
      const derivedOutline = isContentPath(tab.path) && tab.path !== 'goink.md'
        ? outlinePath(parseInt(tab.path.replace(/.*\//, '').replace('.md', '')))
        : null
      if (derivedOutline) {
        app.GetContent(novelId, derivedOutline).then(oc => {
          updateTab(tabId, { outlineContent: oc || '' })
        }).catch(() => {
          updateTab(tabId, { outlineContent: '' })
        })
      }
    }
  }, [novelId, tabs, app, updateTab])

  // ── 保存逻辑 ────────────────────────────────────────────

  const doSave = useCallback((tabId: string, path: string, content: string) => {
    if (!novelIdRef.current) return
    app.SaveContent({ novel_id: novelIdRef.current, path, content })
    updateTab(tabId, { isDirty: false })
  }, [app, updateTab])

  const handleEditorChange = useCallback((tabId: string, value: string | undefined) => {
    const content = value ?? ''
    updateTab(tabId, { content, isDirty: true })
    onContentChange?.(content)

    if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
    const tab = tabs.find(t => t.id === tabId)
    if (!tab) return
    savingRef.current = { id: tabId, path: tab.path, content }
    saveTimerRef.current = setTimeout(() => {
      if (!savingRef.current) return
      const s = savingRef.current
      doSave(s.id, s.path, s.content)
    }, 500)
  }, [tabs, updateTab, doSave, onContentChange])

  const handleEditorMount: OnMount = useCallback((editor) => {
    editorRef.current = editor
    editor.onDidBlurEditorText(() => {
      if (saveTimerRef.current) clearTimeout(saveTimerRef.current)
      const s = savingRef.current
      if (!s) return
      doSave(s.id, s.path, s.content)
    })
  }, [doSave])

  // ── file:changed 事件监听 ─────────────────────────────────
  // 用 ref 读取最新 tabs，避免因 tabs 变化频繁重建订阅丢失事件

  useEffect(() => {
    const unsub = EventsOn('file:changed', async (data: any) => {
      if (data.novel_id !== novelIdRef.current) return

      for (const tab of tabsRef.current) {
        if (tab.type !== 'file') continue

        let needRefresh = false
        let refreshKey: 'content' | 'outlineContent' = 'content'

        if (tab.path === data.path) {
          needRefresh = true
          refreshKey = 'content'
        } else {
          const derivedOutline = isContentPath(tab.path) && tab.path !== 'goink.md'
            ? outlinePath(parseInt(tab.path.replace(/.*\//, '').replace('.md', '')))
            : null
          if (derivedOutline && derivedOutline === data.path) {
            needRefresh = true
            refreshKey = 'outlineContent'
          }
        }

        if (needRefresh) {
          try {
            const fresh = await app.GetContent(data.novel_id, data.path)
            const patch: Partial<EditorTab> = { [refreshKey]: fresh }
            if (refreshKey === 'content') patch.isDirty = false
            updateTab(tab.id, patch)
          } catch { /* 文件可能被删 */ }
        }
      }
    })
    return () => unsub()
  }, [app, updateTab])

  // ── 打开/激活文件 tab ──────────────────────────────────

  function titleFromPath(p: string): string {
    if (p.startsWith('chapters/')) {
      const num = parseInt(p.replace('chapters/', '').replace('.md', ''))
      return `第${num}章`
    }
    if (p === 'goink.md') return '故事状态'
    if (isSkillPath(p)) return `技能: ${skillNameFromPath(p)}`
    return p
  }

  const doOpenFile = useCallback((path: string, title?: string, readOnly?: boolean) => {
    const display = title || titleFromPath(path)
    const existing = tabs.find(t => t.path === path && t.type === 'file')
    if (existing) {
      setActiveTabId(existing.id)
      onContentChange?.(existing.content ?? '')
      return
    }

    const skReadOnly = readOnly ?? path.startsWith('builtin/skills/')
    const initialMode = skReadOnly ? 'preview' : (isSkillPath(path) ? 'content' : 'content') as 'content' | 'outline' | 'preview'

    setIsLoading(true)
    app.GetContent(novelId, path).then(content => {
      const c = content ?? ''
      openTab({ type: 'file', path, title: display, content: c, isDirty: false, viewMode: initialMode, readOnly: skReadOnly })
      onContentChange?.(c)
    }).catch(() => {
      openTab({ type: 'file', path, title: display, content: '', isDirty: false, viewMode: initialMode, readOnly: skReadOnly })
      onContentChange?.('')
    }).finally(() => setIsLoading(false))
  }, [novelId, tabs, app, openTab, setActiveTabId, onContentChange])

  function filePathFromDiff(diffPath: string): { filePath: string; viewMode: 'content' | 'outline' } {
    if (isOutlinePath(diffPath)) {
      return { filePath: diffPath.replace('outlines/', 'chapters/'), viewMode: 'outline' }
    }
    return { filePath: diffPath, viewMode: 'content' }
  }

  // ── 审批操作（由 WorkspaceView 通过 ref 调用）───────────

  const handleDiffApprove = useCallback(async (toolId: string) => {
    const dt = tabs.find(t => t.type === 'diff' && t.toolId === toolId)
    if (!dt) return

    const { filePath, viewMode } = filePathFromDiff(dt.path)
    const ft = tabs.find(t => t.type === 'file' && t.path === filePath)

    if (ft) {
      try {
        const fresh = await app.GetContent(novelId, dt.path)
        const patch: Partial<EditorTab> = { viewMode }
        if (viewMode === 'outline') {
          patch.outlineContent = fresh
        } else {
          patch.content = fresh
          patch.isDirty = false
        }
        updateTab(ft.id, patch)
      } catch { }
    }

    closeTab(dt.id)
    doOpenFile(filePath)
  }, [novelId, tabs, app, updateTab, closeTab, doOpenFile])

  const handleDiffReject = useCallback((toolId: string) => {
    const dt = tabs.find(t => t.type === 'diff' && t.toolId === toolId)
    if (!dt) return

    const { filePath } = filePathFromDiff(dt.path)
    closeTab(dt.id)
    doOpenFile(filePath)
  }, [tabs, closeTab, doOpenFile])

  // ── 暴露给父组件的方法 ──────────────────────────────────

  useImperativeHandle(ref, () => ({
    openFile: doOpenFile,
    closeAllTabs,
    openDiffTab,
    handleDiffApprove,
    handleDiffReject,
  }), [doOpenFile, closeAllTabs, openDiffTab, handleDiffApprove, handleDiffReject])


  // ── 渲染 ────────────────────────────────────────────────

  const tabBtnClass = (active: boolean) =>
    `px-3 py-1 text-xs rounded transition-colors cursor-pointer ${
      active ? 'bg-muted text-foreground font-medium' : 'text-muted-foreground hover:text-foreground'
    }`

  // 空状态
  if (!activeTab) {
    return (
      <main className="flex-1 bg-background flex flex-col min-w-0 min-h-0 border-r overflow-hidden">
        <TabBar tabs={tabs} activeTabId={activeTabId} onSelect={setActiveTabId} onClose={closeTab} />
        <div className="flex-1 flex items-center justify-center">
          {tabs.length === 0 ? (
            <div className="text-center">
              <FileText className="w-12 h-12 text-muted-foreground/20 mx-auto mb-3" />
              <p className="text-sm text-muted-foreground">选择或创建章节开始写作</p>
            </div>
          ) : (
            <div className="text-center">
              <FileText className="w-12 h-12 text-muted-foreground/20 mx-auto mb-3" />
              <p className="text-sm text-muted-foreground">选择标签页</p>
            </div>
          )}
        </div>
      </main>
    )
  }

  // Diff tab
  if (activeTab.type === 'diff') {
    const isOutline = activeTab.path?.startsWith('outlines/')

    return (
      <main className="flex-1 bg-background flex flex-col min-w-0 min-h-0 border-r overflow-hidden">
        <TabBar tabs={tabs} activeTabId={activeTabId} onSelect={setActiveTabId} onClose={closeTab} />
        <div className="flex items-center px-4 py-2 border-b shrink-0">
          <span className="text-sm font-medium truncate">{activeTab.title}</span>
        </div>
        <div className="flex-1 overflow-auto">
          {isOutline ? (
            <div className="p-6">
              <Markdown content={activeTab.modified ?? ''} />
            </div>
          ) : (
            <DiffEditor
              height="100%"
              language="markdown"
              theme="light"
              original={activeTab.original}
              modified={activeTab.modified}
              onMount={editor => {
                editor.getOriginalEditor().updateOptions({ wordWrap: 'on' })
                const changes = editor.getLineChanges()
                if (changes?.length) {
                  editor.revealLine(changes[0].modifiedStartLineNumber)
                }
              }}
              options={{
                minimap: { enabled: false },
                scrollBeyondLastLine: false,
                fontSize: 15,
                lineHeight: 26,
                fontFamily: "'Noto Serif SC', 'Source Han Serif SC', serif",
                wordWrap: 'on',
                automaticLayout: true,
                readOnly: true,
                renderSideBySide: true,
              }}
            />
          )}
        </div>
      </main>
    )
  }

  // File tab
  const viewMode = activeTab.viewMode || 'content'
  return (
    <main className="flex-1 bg-background flex flex-col min-w-0 min-h-0 border-r overflow-hidden">
      <TabBar tabs={tabs} activeTabId={activeTabId} onSelect={setActiveTabId} onClose={closeTab} />
      <div className="flex items-center justify-between px-4 py-2 border-b shrink-0">
        <span className="text-sm font-medium truncate">{activeTab.title}</span>
        <div className="flex items-center gap-0.5 shrink-0">
          {isSkillPath(activeTab.path) ? (
            <button
              onClick={() => {
                const newMode = activeTab.viewMode === 'preview' ? 'content' : 'preview'
                updateTab(activeTab.id, { viewMode: newMode })
              }}
              className={tabBtnClass(viewMode === 'preview')}
            >
              预览
            </button>
          ) : (
            <>
              <button onClick={() => handleSetViewMode(activeTab.id, 'content')} className={tabBtnClass(viewMode === 'content')}>
                正文
              </button>
              <button onClick={() => handleSetViewMode(activeTab.id, 'outline')} className={tabBtnClass(viewMode === 'outline')}>
                大纲
              </button>
            </>
          )}
        </div>
      </div>

      <div className="flex-1 min-h-0">
        {isLoading ? (
          <div className="flex items-center justify-center h-full">
            <Loader2 className="w-5 h-5 animate-spin text-muted-foreground" />
          </div>
        ) : viewMode === 'preview' ? (
          <SkillPreview content={activeTab.content ?? ''} />
        ) : viewMode === 'content' ? (
          activeTab.readOnly ? (
            <SkillPreview content={activeTab.content ?? ''} />
          ) : (
            <ContentEditor
              value={activeTab.content ?? ''}
              onChange={v => handleEditorChange(activeTab.id, v)}
              onMount={handleEditorMount}
            />
          )
        ) : (
          <OutlineViewer content={activeTab.outlineContent ?? ''} />
        )}
      </div>
    </main>
  )
})

export default ContentPanel
