import Editor, { type OnMount, DiffEditor } from '@monaco-editor/react'
import { FileText, Loader2, Check, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import TabBar from './TabBar'
import type { EditorTab } from '@/hooks/useEditorTabs'

interface Props {
  tabs: EditorTab[]
  activeTab: EditorTab | null
  activeTabId: string | null
  isLoadingContent: boolean
  onSelectTab: (id: string) => void
  onCloseTab: (id: string) => void
  onEditorChange: (tabId: string, value: string | undefined) => void
  onEditorMount: OnMount
  onSetViewMode: (tabId: string, mode: 'content' | 'outline') => void
  onApprove: (toolId: string) => void
  onReject: (toolId: string) => void
  hasNovels: boolean
  noChapters: boolean
  onGoToNovels: () => void
}

export default function EditorArea({
  tabs, activeTab, activeTabId, isLoadingContent,
  onSelectTab, onCloseTab, onEditorChange, onEditorMount,
  onSetViewMode, onApprove, onReject,
  hasNovels, noChapters, onGoToNovels,
}: Props) {
  const tabBtnClass = (active: boolean) =>
    `px-3 py-1 text-xs rounded transition-colors ${
      active ? 'bg-muted text-foreground font-medium' : 'text-muted-foreground hover:text-foreground'
    }`

  // Empty state
  if (!activeTab) {
    return (
      <main className="flex-1 bg-background flex flex-col min-w-0 border-r">
        <TabBar
          tabs={tabs}
          activeTabId={activeTabId}
          onSelect={onSelectTab}
          onClose={onCloseTab}
        />
        <div className="flex-1 flex items-center justify-center">
          {!hasNovels ? (
            <div className="text-center">
              <FileText className="w-16 h-16 text-muted-foreground/15 mx-auto mb-4" />
              <h2 className="text-base font-medium text-foreground mb-1">开始你的第一部作品</h2>
              <p className="text-sm text-muted-foreground mb-4">点击左侧书架图标创建小说</p>
              <Button size="sm" onClick={onGoToNovels}>前往书架</Button>
            </div>
          ) : noChapters ? (
            <div className="text-center">
              <FileText className="w-12 h-12 text-muted-foreground/20 mx-auto mb-3" />
              <p className="text-sm text-muted-foreground">创建章节开始写作</p>
            </div>
          ) : (
            <div className="text-center">
              <FileText className="w-12 h-12 text-muted-foreground/20 mx-auto mb-3" />
              <p className="text-sm text-muted-foreground">选择或创建章节开始写作</p>
            </div>
          )}
        </div>
      </main>
    )
  }

  // Diff tab
  if (activeTab.type === 'diff') {
    return (
      <main className="flex-1 bg-background flex flex-col min-w-0 border-r">
        <TabBar
          tabs={tabs}
          activeTabId={activeTabId}
          onSelect={onSelectTab}
          onClose={onCloseTab}
        />
        <div className="flex items-center justify-between px-4 py-2 border-b shrink-0">
          <span className="text-sm font-medium truncate">{activeTab.title}</span>
          <div className="flex items-center gap-2 shrink-0">
            <button
              onClick={() => onApprove(activeTab.toolId!)}
              className="flex items-center gap-1 px-3 py-1 text-xs rounded bg-emerald-500 text-white hover:bg-emerald-600 transition-colors cursor-pointer"
            >
              <Check className="w-3.5 h-3.5" />
              批准
            </button>
            <button
              onClick={() => onReject(activeTab.toolId!)}
              className="flex items-center gap-1 px-3 py-1 text-xs rounded bg-red-500 text-white hover:bg-red-600 transition-colors cursor-pointer"
            >
              <X className="w-3.5 h-3.5" />
              拒绝
            </button>
          </div>
        </div>
        <div className="flex-1">
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
        </div>
      </main>
    )
  }

  // Edit tab
  const viewMode = activeTab.viewMode || 'content'
  return (
    <main className="flex-1 bg-background flex flex-col min-w-0 border-r">
      <TabBar
        tabs={tabs}
        activeTabId={activeTabId}
        onSelect={onSelectTab}
        onClose={onCloseTab}
      />
      <div className="flex items-center justify-between px-4 py-2 border-b shrink-0">
        <span className="text-sm font-medium truncate">{activeTab.title}</span>
        <div className="flex items-center gap-0.5 shrink-0">
          <button onClick={() => onSetViewMode(activeTab.id, 'content')} className={tabBtnClass(viewMode === 'content')}>
            正文
          </button>
          <button onClick={() => onSetViewMode(activeTab.id, 'outline')} className={tabBtnClass(viewMode === 'outline')}>
            大纲
          </button>
        </div>
      </div>

      <div className="flex-1">
        {isLoadingContent ? (
          <div className="flex items-center justify-center h-full">
            <Loader2 className="w-5 h-5 animate-spin text-muted-foreground" />
          </div>
        ) : viewMode === 'content' ? (
          <Editor
            height="100%"
            language="markdown"
            theme="light"
            value={activeTab.content ?? ''}
            onChange={v => onEditorChange(activeTab.id, v)}
            onMount={onEditorMount}
            options={{
              minimap: { enabled: false },
              lineNumbers: 'on',
              scrollBeyondLastLine: false,
              fontSize: 17,
              lineHeight: 30,
              fontFamily: "'Noto Serif SC', 'Source Han Serif SC', serif",
              wordWrap: 'on',
              automaticLayout: true,
              unicodeHighlight: { nonBasicASCII: false, ambiguousCharacters: false, invisibleCharacters: false },
            }}
          />
        ) : (
          <div className="flex items-center justify-center h-full">
            <div className="text-center">
              <FileText className="w-8 h-8 text-muted-foreground/30 mx-auto mb-2" />
              <p className="text-xs text-muted-foreground">大纲功能即将推出</p>
            </div>
          </div>
        )}
      </div>
    </main>
  )
}
