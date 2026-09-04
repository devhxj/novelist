import { useMemo, useRef, useState } from 'react'

interface Props {
  data: Record<string, number> // "YYYY-MM-DD" -> 字数
  months?: number
}

const LEVELS = [
  { max: 0,    cls: 'bg-[#ebedf0] dark:bg-[#2d333b]' },
  { max: 100,  cls: 'bg-[#9be9a8] dark:bg-[#0e4429]' },
  { max: 500,  cls: 'bg-[#40c463] dark:bg-[#006d32]' },
  { max: 2000, cls: 'bg-[#30a14e] dark:bg-[#26a641]' },
  { max: Infinity, cls: 'bg-[#216e39] dark:bg-[#39d353]' },
]

function levelClass(words: number): string {
  for (const l of LEVELS) {
    if (words <= l.max) return l.cls
  }
  return LEVELS[0].cls
}

function formatDate(dateStr: string): string {
  const d = new Date(dateStr + 'T00:00:00')
  return `${d.getFullYear()}年${d.getMonth() + 1}月${d.getDate()}日`
}

function cellLabel(date: string, words: number): string {
  return `${words > 0 ? `${words.toLocaleString()} 字` : '无写作'} · ${formatDate(date)}`
}

export default function ContributionGrid({ data, months = 12 }: Props) {
  const [tooltip, setTooltip] = useState<{ date: string; words: number; x: number; y: number } | null>(null)
  const gridRef = useRef<HTMLDivElement>(null)

  const weeks = useMemo(() => {
    const end = new Date()
    end.setHours(0, 0, 0, 0)
    const start = new Date(end)
    start.setMonth(end.getMonth() - months)
    // 对齐到周日
    start.setDate(start.getDate() - start.getDay())

    const result: { date: string; words: number }[][] = []
    const cur = new Date(start)
    while (cur <= end) {
      const week: { date: string; words: number }[] = []
      for (let i = 0; i < 7; i++) {
        const ds = cur.toISOString().slice(0, 10)
        week.push({ date: ds, words: data[ds] ?? 0 })
        cur.setDate(cur.getDate() + 1)
      }
      result.push(week)
    }
    return result
  }, [data, months])

  const monthLabels = useMemo(() => {
    const labels: { label: string; span: number }[] = []
    weeks.forEach((week, i) => {
      const midDay = week[3]?.date // 用周三判断月份
      if (!midDay) return
      const month = midDay.slice(0, 7)
      const last = labels[labels.length - 1]
      if (!last || last.label !== month) {
        if (last) last.span = i - labels.slice(0, -1).reduce((s, l) => s + l.span, 0)
        labels.push({ label: month, span: 0 })
      }
    })
    if (labels.length > 0) {
      labels[labels.length - 1].span = weeks.length - labels.slice(0, -1).reduce((s, l) => s + l.span, 0)
    }
    // 转中文月份
    return labels.map(l => {
      const parts = l.label.split('-')
      return { label: `${parseInt(parts[1], 10)}月`, span: l.span }
    })
  }, [weeks])

  const flatDays = useMemo(() => weeks.flat(), [weeks])

  const showTooltip = (e: React.MouseEvent, date: string, words: number) => {
    const rect = (e.target as HTMLElement).getBoundingClientRect()
    setTooltip({ date, words, x: rect.left + rect.width / 2, y: rect.top - 32 })
  }

  const showTooltipAt = (element: Element, date: string, words: number) => {
    const rect = element.getBoundingClientRect()
    setTooltip({ date, words, x: rect.left + rect.width / 2, y: rect.top - 32 })
  }

  // E5/C1-C2 漏网补齐：贡献格子此前只有鼠标悬停，键盘用户完全不可达。
  // 采用 roving tabindex——整个日历是一个 Tab 停靠点，方向键在格子间移动，
  // 避免把 364 个格子全部塞进 Tab 序列。
  const focusCell = (index: number) => {
    const next = Math.max(0, Math.min(flatDays.length - 1, index))
    const cell = gridRef.current?.querySelector<HTMLElement>(`[data-idx="${next}"]`)
    if (!cell) return
    cell.focus()
    const day = flatDays[next]
    showTooltipAt(cell, day.date, day.words)
  }

  const handleGridKeyDown = (e: React.KeyboardEvent) => {
    const focused = gridRef.current?.querySelector<HTMLElement>('[tabindex="0"]')
    const current = focused ? Number(focused.dataset.idx ?? 0) : 0
    const columns = 7
    switch (e.key) {
      case 'ArrowRight': e.preventDefault(); focusCell(current + 1); break
      case 'ArrowLeft': e.preventDefault(); focusCell(current - 1); break
      case 'ArrowDown': e.preventDefault(); focusCell(current + columns); break
      case 'ArrowUp': e.preventDefault(); focusCell(current - columns); break
      case 'Home': e.preventDefault(); focusCell(0); break
      case 'End': e.preventDefault(); focusCell(flatDays.length - 1); break
      case 'Escape': setTooltip(null); break
    }
  }

  return (
    <div className="relative select-none">
      {/* 月份标签 */}
      <div className="flex text-[10px] text-muted-foreground mb-1" style={{ paddingLeft: 28 }}>
        {monthLabels.map((m, i) => (
          <span key={i} className="text-left" style={{ width: m.span * 16 }}>
            {m.label}
          </span>
        ))}
      </div>

      <div className="flex gap-[3px]">
        {/* 星期标签 */}
        <div className="flex flex-col gap-[3px] text-[10px] text-muted-foreground pr-2" style={{ width: 22 }}>
          <span className="h-[13px] leading-[13px]" />
          <span className="h-[13px] leading-[13px]">一</span>
          <span className="h-[13px] leading-[13px]" />
          <span className="h-[13px] leading-[13px]">三</span>
          <span className="h-[13px] leading-[13px]" />
          <span className="h-[13px] leading-[13px]">五</span>
          <span className="h-[13px] leading-[13px]" />
        </div>

        {/* 格子矩阵：roving tabindex，一个 Tab 停靠点 + 方向键导航 */}
        <div
          ref={gridRef}
          className="flex gap-[3px] focus-visible:outline-none"
          role="group"
          aria-label="写作日历，使用方向键浏览每日字数"
          tabIndex={0}
          onKeyDown={handleGridKeyDown}
          onFocus={(e) => {
            if (e.target === e.currentTarget) focusCell(0)
          }}
          onBlur={() => setTooltip(null)}
        >
          {weeks.map((week, wi) => (
            <div key={wi} className="flex flex-col gap-[3px]">
              {week.map((day, di) => {
                const flatIndex = wi * 7 + di
                return (
                  <button
                    key={di}
                    type="button"
                    data-idx={flatIndex}
                    tabIndex={flatIndex === 0 ? 0 : -1}
                    aria-label={cellLabel(day.date, day.words)}
                    className={`w-[13px] h-[13px] rounded-[2px] ${levelClass(day.words)} cursor-pointer select-none focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-ring`}
                    onMouseEnter={(e) => showTooltip(e, day.date, day.words)}
                    onMouseLeave={() => setTooltip(null)}
                    onFocus={(e) => showTooltipAt(e.currentTarget, day.date, day.words)}
                    onBlur={() => setTooltip(null)}
                  />
                )
              })}
            </div>
          ))}
        </div>
      </div>

      {/* 图例 */}
      <div className="flex items-center gap-1 mt-2 justify-end text-[10px] text-muted-foreground">
        <span>少</span>
        {LEVELS.map((l, i) => (
          <div key={i} className={`w-[10px] h-[10px] rounded-[2px] ${l.cls}`} />
        ))}
        <span className="ml-1">多</span>
      </div>

      {/* Tooltip */}
      {tooltip && (
        <div
          className="fixed z-50 px-2 py-1 rounded text-xs bg-foreground text-background whitespace-nowrap pointer-events-none"
          style={{ left: tooltip.x, top: tooltip.y, transform: 'translateX(-50%)' }}
        >
          {tooltip.words > 0 ? `${tooltip.words.toLocaleString()} 字` : '无写作'} · {formatDate(tooltip.date)}
        </div>
      )}
    </div>
  )
}
