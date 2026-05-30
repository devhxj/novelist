import Editor, { type OnMount } from '@monaco-editor/react'
import { FileText, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'

type EditingTarget = { type: string; path: string; title: string } | null

interface Props {
  target: EditingTarget
  editorContent: string
  editorViewMode: 'content' | 'outline'
  setEditorViewMode: (v: 'content' | 'outline') => void
  isLoadingContent: boolean
  onEditorChange: (value: string | undefined) => void
  onEditorMount: OnMount
  hasNovels: boolean
  noChapters: boolean
  onGoToNovels: () => void
}

export default function EditorArea({
  target, editorContent, editorViewMode, setEditorViewMode,
  isLoadingContent, onEditorChange, onEditorMount,
  hasNovels, noChapters, onGoToNovels,
}: Props) {
  const tabClass = (active: boolean) =>
    `px-3 py-1 text-xs rounded transition-colors ${
      active
        ? 'bg-muted text-foreground font-medium'
        : 'text-muted-foreground hover:text-foreground'
    }`

  if (!target) {
    return (
      <main className="flex-1 bg-background flex items-center justify-center border-r">
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
      </main>
    )
  }

  return (
    <main className="flex-1 bg-background flex flex-col min-w-0 border-r">
      <div className="flex items-center justify-between px-4 py-2 border-b shrink-0">
        <span className="text-sm font-medium truncate">{target.title}</span>
        <div className="flex items-center gap-0.5 shrink-0">
          <button
            onClick={() => setEditorViewMode('content')}
            className={tabClass(editorViewMode === 'content')}
          >
            正文
          </button>
          <button
            onClick={() => setEditorViewMode('outline')}
            className={tabClass(editorViewMode === 'outline')}
          >
            大纲
          </button>
        </div>
      </div>

      <div className="flex-1">
        {isLoadingContent ? (
          <div className="flex items-center justify-center h-full">
            <Loader2 className="w-5 h-5 animate-spin text-muted-foreground" />
          </div>
        ) : editorViewMode === 'content' ? (
          <Editor
            height="100%"
            language="markdown"
            theme="light"
            value={editorContent}
            onChange={onEditorChange}
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
              unicodeHighlight: {
                nonBasicASCII: false,
                ambiguousCharacters: false,
                invisibleCharacters: false,
              },
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
