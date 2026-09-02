import { useMemo, useState, useRef } from 'react'
import { BookMarked, CircleCheck, CircleX } from 'lucide-react'
import type { MaterializationCompletion } from '@/hooks/useMaterializationWatcher'

interface Props {
  content: string
  isDirty?: boolean
  materializationActiveCount?: number
  materializationNotifications?: MaterializationCompletion[]
  onDismissMaterializationNotification?: (runId: string) => void
  onOpenReference?: () => void
}

interface DetailedStats {
  wordCount: number
  lineCount: number
  chineseChars: number
  englishWords: number
  charCountSpace: number
  charCountNoSpace: number
  paragraphCount: number
}

function computeStats(text: string): DetailedStats {
  if (!text) {
    return { wordCount: 0, lineCount: 0, chineseChars: 0, englishWords: 0, charCountSpace: 0, charCountNoSpace: 0, paragraphCount: 0 }
  }

  let chineseChars = 0
  let spaces = 0
  let paragraphCount = 0
  let inPara = false

  for (const ch of text) {
    const cp = ch.codePointAt(0)!
    if ((cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x20000 && cp <= 0x2A6DF) || (cp >= 0xF900 && cp <= 0xFAFF)) {
      chineseChars++
    } else if (ch === ' ' || ch === '\t' || ch === '\n' || ch === '\r') {
      spaces++
    }

    if (ch === '\n') {
      if (inPara) { paragraphCount++; inPara = false }
    } else if (ch !== ' ' && ch !== '\t' && ch !== '\r') {
      inPara = true
    }
  }
  if (inPara) paragraphCount++

  const englishWords = (text.match(/[a-zA-Z]+(?:'[a-zA-Z]+)?/g) || []).length
  const lineCount = text ? text.split('\n').length : 0

  return {
    wordCount: chineseChars + englishWords,
    lineCount,
    chineseChars,
    englishWords,
    charCountSpace: [...text].length,
    charCountNoSpace: [...text].length - spaces,
    paragraphCount,
  }
}

export default function StatusBar({
  content,
  isDirty,
  materializationActiveCount = 0,
  materializationNotifications = [],
  onDismissMaterializationNotification,
  onOpenReference,
}: Props) {
  const stats = useMemo(() => computeStats(content), [content])
  const [showDetail, setShowDetail] = useState(false)
  const hoverTimer = useRef<ReturnType<typeof setTimeout>>(0)

  function handleMouseEnter() {
    hoverTimer.current = setTimeout(() => setShowDetail(true), 150)
  }

  function handleMouseLeave() {
    if (hoverTimer.current) clearTimeout(hoverTimer.current)
    setShowDetail(false)
  }

  return (
    <div className="relative h-7 flex items-center justify-between px-4 border-t bg-sidebar text-xs text-muted-foreground select-none">
      <div className="flex items-center gap-4">
        <span
          className="cursor-default"
          onMouseEnter={handleMouseEnter}
          onMouseLeave={handleMouseLeave}
        >
          字数 {stats.wordCount}
        </span>
        <span>行数 {stats.lineCount}</span>
        {materializationActiveCount > 0 && (
          <button
            type="button"
            onClick={onOpenReference}
            className="flex items-center gap-1 text-sky-700 dark:text-sky-300 hover:text-foreground transition-colors"
            title="参考书材料化进行中，点击打开语料区"
            data-testid="materialization-active-indicator"
          >
            <BookMarked className="h-3 w-3 animate-pulse" aria-hidden="true" />
            材料化中 ×{materializationActiveCount}
          </button>
        )}
      </div>

      {showDetail && (
        <div
          className="absolute bottom-0 left-0 mb-7 ml-4 bg-popover border rounded-lg shadow-lg p-4 text-sm space-y-1.5 z-50 min-w-[220px]"
          onMouseEnter={() => { if (hoverTimer.current) clearTimeout(hoverTimer.current); setShowDetail(true) }}
          onMouseLeave={handleMouseLeave}
        >
          <div className="font-medium text-foreground mb-2">字数统计</div>
          <div className="flex justify-between gap-8">
            <span>字数</span>
            <span className="tabular-nums">{stats.wordCount}</span>
          </div>
          <div className="flex justify-between gap-8">
            <span className="pl-3">中文字符</span>
            <span className="tabular-nums">{stats.chineseChars}</span>
          </div>
          <div className="flex justify-between gap-8">
            <span className="pl-3">英文单词</span>
            <span className="tabular-nums">{stats.englishWords}</span>
          </div>
          <div className="border-t my-1.5" />
          <div className="flex justify-between gap-8">
            <span>字符数（不计空格）</span>
            <span className="tabular-nums">{stats.charCountNoSpace}</span>
          </div>
          <div className="flex justify-between gap-8">
            <span>字符数（计空格）</span>
            <span className="tabular-nums">{stats.charCountSpace}</span>
          </div>
          <div className="border-t my-1.5" />
          <div className="flex justify-between gap-8">
            <span>行数</span>
            <span className="tabular-nums">{stats.lineCount}</span>
          </div>
          <div className="flex justify-between gap-8">
            <span>段落数</span>
            <span className="tabular-nums">{stats.paragraphCount}</span>
          </div>
        </div>
      )}

      <span className="flex items-center gap-1">
        <span className={`w-1.5 h-1.5 rounded-full ${isDirty ? 'bg-amber-500' : 'bg-emerald-500'}`} />
        {isDirty ? '未保存' : '已保存'}
      </span>

      {materializationNotifications.length > 0 && (
        <div className="absolute bottom-8 right-4 z-50 flex w-72 flex-col gap-2" role="status" aria-live="polite">
          {materializationNotifications.map((notice) => (
            <div
              key={notice.run_id}
              className="rounded-md border border-border bg-popover px-3 py-2 shadow-lg text-xs"
              data-testid={`materialization-notice-${notice.status}`}
            >
              <div className="flex items-center gap-1.5 font-medium text-foreground">
                {notice.status === 'completed'
                  ? <CircleCheck className="h-3.5 w-3.5 text-emerald-600" aria-hidden="true" />
                  : <CircleX className="h-3.5 w-3.5 text-destructive" aria-hidden="true" />}
                {notice.status === 'completed' ? '材料化完成' : notice.status === 'failed' ? '材料化失败' : '材料化已取消'}
              </div>
              <p className="mt-0.5 text-muted-foreground">《{notice.anchor_title}》</p>
              {notice.error_message && (
                <p className="mt-0.5 line-clamp-2 break-words text-destructive">{notice.error_message}</p>
              )}
              <div className="mt-1.5 flex gap-2">
                <button
                  type="button"
                  onClick={() => { onDismissMaterializationNotification?.(notice.run_id); onOpenReference?.() }}
                  className="rounded border border-border px-1.5 py-0.5 text-[11px] text-foreground hover:bg-muted"
                >
                  查看
                </button>
                <button
                  type="button"
                  onClick={() => onDismissMaterializationNotification?.(notice.run_id)}
                  className="rounded px-1.5 py-0.5 text-[11px] text-muted-foreground hover:text-foreground"
                >
                  关闭
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
