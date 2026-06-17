import { useMemo, useState } from 'react'

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

export default function ContributionGrid({ data, months = 12 }: Props) {
  const [tooltip, setTooltip] = useState<{ date: string; words: number; x: number; y: number } | null>(null)

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

  const showTooltip = (e: React.MouseEvent, date: string, words: number) => {
    const rect = (e.target as HTMLElement).getBoundingClientRect()
    setTooltip({ date, words, x: rect.left + rect.width / 2, y: rect.top - 32 })
  }

  return (
    <div className="relative select-none">
      {/* 月份标签 */}
      <div className="flex text-[10px] text-slate-400 mb-1" style={{ paddingLeft: 28 }}>
        {monthLabels.map((m, i) => (
          <span key={i} className="text-left" style={{ width: m.span * 16 }}>
            {m.label}
          </span>
        ))}
      </div>

      <div className="flex gap-[3px]">
        {/* 星期标签 */}
        <div className="flex flex-col gap-[3px] text-[10px] text-slate-400 pr-2" style={{ width: 22 }}>
          <span className="h-[13px] leading-[13px]" />
          <span className="h-[13px] leading-[13px]">一</span>
          <span className="h-[13px] leading-[13px]" />
          <span className="h-[13px] leading-[13px]">三</span>
          <span className="h-[13px] leading-[13px]" />
          <span className="h-[13px] leading-[13px]">五</span>
          <span className="h-[13px] leading-[13px]" />
        </div>

        {/* 格子矩阵 */}
        <div className="flex gap-[3px]">
          {weeks.map((week, wi) => (
            <div key={wi} className="flex flex-col gap-[3px]">
              {week.map((day, di) => (
                <div
                  key={di}
                  className={`w-[13px] h-[13px] rounded-[2px] ${levelClass(day.words)} cursor-pointer`}
                  onMouseEnter={(e) => showTooltip(e, day.date, day.words)}
                  onMouseLeave={() => setTooltip(null)}
                />
              ))}
            </div>
          ))}
        </div>
      </div>

      {/* 图例 */}
      <div className="flex items-center gap-1 mt-2 justify-end text-[10px] text-slate-400">
        <span>少</span>
        {LEVELS.map((l, i) => (
          <div key={i} className={`w-[10px] h-[10px] rounded-[2px] ${l.cls}`} />
        ))}
        <span className="ml-1">多</span>
      </div>

      {/* Tooltip */}
      {tooltip && (
        <div
          className="fixed z-50 px-2 py-1 rounded text-xs bg-slate-800 text-white whitespace-nowrap pointer-events-none -translate-x-1/2"
          style={{ left: tooltip.x, top: tooltip.y }}
        >
          {tooltip.words > 0 ? `${tooltip.words.toLocaleString()} 字` : '无写作'} · {formatDate(tooltip.date)}
        </div>
      )}
    </div>
  )
}
