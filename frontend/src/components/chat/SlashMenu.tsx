import { useEffect, useRef, useMemo } from 'react'
import { createPortal } from 'react-dom'
import { Zap, Play } from 'lucide-react'
import type { app } from '@/hooks/useApp'

interface Props {
  slashItems: app.SlashCommand[]
  filterText: string
  selectedIndex: number
  position: { top: number; left: number; width: number }
  onSelect: (cmd: app.SlashCommand) => void
  onHover: (index: number) => void
}

const MENU_MAX_HEIGHT = 260
const GAP = 8

export default function SlashMenu({
  slashItems, filterText, selectedIndex, position, onSelect, onHover,
}: Props) {
  const scrollRef = useRef<HTMLDivElement>(null)

  const q = filterText.toLowerCase()

  // charMatch 检查 q 的所有字符是否按顺序出现在 s 中（模糊匹配）
  const charMatch = (s: string): boolean => {
    let qi = 0
    for (let i = 0; i < s.length && qi < q.length; i++) {
      if (s[i] === q[qi]) qi++
    }
    return qi === q.length
  }

  // score 计算匹配得分，越低越好
  const score = (c: app.SlashCommand): number => {
    const name = c.name.toLowerCase()
    if (name === q) return 0
    if (name.startsWith(q)) return 1
    if (name.includes(q)) return 2
    if (charMatch(name)) return 3
    if (c.description.toLowerCase().includes(q)) return 4
    return 5
  }

  const filtered = slashItems
    .filter(c => score(c) < 5)
    .sort((a, b) => score(a) - score(b))

  useEffect(() => {
    const el = scrollRef.current
    if (!el) return
    const item = el.children[selectedIndex] as HTMLElement | undefined
    if (item) {
      item.scrollIntoView({ block: 'nearest' })
    }
  }, [selectedIndex])

  const style = useMemo(() => {
    const spaceAbove = position.top - GAP
    const maxH = Math.min(MENU_MAX_HEIGHT, spaceAbove)
    const menuWidth = position.width
    let left = position.left
    if (left + menuWidth > window.innerWidth - GAP) {
      left = Math.max(GAP, window.innerWidth - menuWidth - GAP)
    }
    return {
      left,
      width: menuWidth,
      maxHeight: maxH,
      bottom: window.innerHeight - position.top + GAP,
    }
  }, [position])

  if (filtered.length === 0) {
    return createPortal(
      <div
        className="fixed z-[9999] rounded-lg border bg-background shadow-lg px-3 py-2 text-xs text-muted-foreground"
        style={{ bottom: style.bottom, left: style.left, minWidth: style.width }}
      >
        无匹配命令
      </div>,
      document.body,
    )
  }

  return createPortal(
    <div
      className="fixed z-[9999] rounded-lg border bg-background shadow-lg overflow-hidden flex flex-col"
      style={{ bottom: style.bottom, left: style.left, width: style.width, maxHeight: style.maxHeight }}
    >
      <div ref={scrollRef} className="overflow-y-auto" style={{ maxHeight: style.maxHeight }}>
        {filtered.map((c, i) => {
          const isCommand = c.type === 'command'
          return (
          <button
            key={`${c.type}:${c.name}`}
            onMouseDown={e => {
              e.preventDefault()
              onSelect(c)
            }}
            onMouseEnter={() => onHover(i)}
            className={`w-full text-left px-3 py-2 transition-colors ${
              i === selectedIndex
                ? (isCommand ? 'bg-blue-50 dark:bg-blue-950/30' : 'bg-accent')
                : (isCommand ? 'hover:bg-blue-50/60 dark:hover:bg-blue-950/20' : 'hover:bg-muted/60')
            }`}
          >
            <div className="flex items-center gap-2 min-w-0">
              {isCommand
                ? <Play className="w-3.5 h-3.5 text-blue-500 shrink-0" />
                : <Zap className="w-3.5 h-3.5 text-amber-500 shrink-0" />
              }
              <span className="text-sm font-medium text-foreground truncate">{c.name}</span>
              <span className={`text-[10px] shrink-0 ml-auto ${
                isCommand ? 'text-blue-500' : 'text-muted-foreground/40'
              }`}>
                {isCommand ? '指令' : '技能'}
              </span>
            </div>
            {c.description && (
              <div className="text-xs text-muted-foreground line-clamp-2 mt-0.5">{c.description}</div>
            )}
          </button>
        )})}
      </div>
    </div>,
    document.body,
  )
}
