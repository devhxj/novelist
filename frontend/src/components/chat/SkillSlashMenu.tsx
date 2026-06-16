import { useEffect, useRef, useMemo } from 'react'
import { createPortal } from 'react-dom'
import type { skill } from '@/hooks/useApp'

interface Props {
  skills: skill.SkillMeta[]
  filterText: string
  selectedIndex: number
  position: { top: number; left: number; width: number }
  onSelect: (skill: skill.SkillMeta) => void
  onHover: (index: number) => void
}

const MENU_MAX_HEIGHT = 260
const GAP = 8

export default function SkillSlashMenu({
  skills, filterText, selectedIndex, position, onSelect, onHover,
}: Props) {
  const scrollRef = useRef<HTMLDivElement>(null)

  const q = filterText.toLowerCase()
  const filtered = skills.filter(s =>
    s.name.toLowerCase().includes(q) || s.description.toLowerCase().includes(q)
  )

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
        无匹配技能
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
        {filtered.map((s, i) => (
          <button
            key={`${s.source}:${s.name}`}
            onMouseDown={e => {
              e.preventDefault()
              onSelect(s)
            }}
            onMouseEnter={() => onHover(i)}
            className={`w-full text-left px-3 py-2 transition-colors ${
              i === selectedIndex ? 'bg-accent' : 'hover:bg-muted/60'
            }`}
          >
            <div className="flex items-center gap-2 min-w-0">
              <span className="text-sm font-medium text-foreground truncate">{s.name}</span>
              <span className="text-[10px] text-muted-foreground/60 shrink-0">{s.category}</span>
              <span className="text-[10px] text-muted-foreground/40 shrink-0 ml-auto">{s.source}</span>
            </div>
            {s.description && (
              <div className="text-xs text-muted-foreground line-clamp-2 mt-0.5">{s.description}</div>
            )}
          </button>
        ))}
      </div>
    </div>,
    document.body,
  )
}
