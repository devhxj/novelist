// ContextRing — SVG 圆环显示 token 用量，照搬 Python ContextRing.tsx
import { useState, useRef, useCallback } from 'react'

export interface UsageInfo {
  prompt_tokens: number
  completion_tokens: number
  total_tokens: number
  prompt_cache_hit_tokens: number
  prompt_cache_miss_tokens: number
  cache_hit_ratio: number
  context_window: number
  usage_ratio: number
  detail: {
    system: number
    user: number
    assistant: number
    tool: number
  }
}

function ringColor(ratio: number): string {
  if (ratio >= 90) return '#e74c3c'
  if (ratio >= 80) return '#f39c12'
  return '#52c41a'
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return (n / 1_000_000).toFixed(1) + 'M'
  if (n >= 1_000) return (n / 1_000).toFixed(0) + 'K'
  return String(n)
}

const DETAIL_LABELS: Record<string, string> = {
  system: '系统上下文',
  user: '用户输入',
  assistant: 'AI 输出',
  tool: '工具结果',
}

interface Props {
  usage: UsageInfo | null
  onCompress?: () => void
  isTurnRunning?: boolean
  isCompressing?: boolean
}

export default function ContextRing({ usage, onCompress, isTurnRunning, isCompressing }: Props) {
  const [showPopover, setShowPopover] = useState(false)
  const hideTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const handleEnter = useCallback(() => {
    if (hideTimerRef.current) {
      clearTimeout(hideTimerRef.current)
      hideTimerRef.current = null
    }
    setShowPopover(true)
  }, [])

  const handleLeave = useCallback(() => {
    hideTimerRef.current = setTimeout(() => setShowPopover(false), 150)
  }, [])

  const hasUsage = usage && usage.context_window && usage.total_tokens
  const ratio = hasUsage ? Math.min(usage.usage_ratio, 100) : 0
  const r = 18
  const circumference = 2 * Math.PI * r
  const offset = circumference - (ratio / 100) * circumference
  const color = hasUsage ? ringColor(ratio) : 'var(--muted-foreground)'

  return (
    <span
      className="relative inline-flex items-center justify-center cursor-pointer shrink-0 select-none"
      onMouseEnter={handleEnter}
      onMouseLeave={handleLeave}
    >
      <svg width={44} height={44} viewBox="0 0 44 44">
        <circle cx={22} cy={22} r={r} fill="none" stroke="rgb(0 0 0 / 0.12)" strokeWidth={3} />
        <circle
          cx={22} cy={22} r={r} fill="none"
          stroke={color}
          strokeWidth={3}
          strokeLinecap="round"
          strokeDasharray={circumference}
          strokeDashoffset={offset}
          transform="rotate(-90 22 22)"
          style={{ transition: 'stroke-dashoffset 0.4s ease, stroke 0.4s ease' }}
        />
      </svg>
      <span className="absolute text-[11px] font-semibold tabular-nums pointer-events-none" style={{ color }}>
        {ratio.toFixed(0)}%
      </span>

      {showPopover && (
        <div className="absolute bottom-full right-0 mb-2 z-50 flex flex-col gap-2.5 bg-background text-foreground rounded-xl p-3 min-w-[240px] shadow-lg border">
          <div className="flex gap-4 text-[13px] font-semibold">
            <span>上下文占用: {ratio.toFixed(1)}%</span>
            {hasUsage && usage.cache_hit_ratio > 0 && (
              <span>缓存命中率: {usage.cache_hit_ratio.toFixed(1)}%</span>
            )}
          </div>
          <div className="h-1.5 rounded-sm bg-muted overflow-hidden">
            <div
              className="h-full rounded-sm transition-all duration-400"
              style={{ width: `${ratio}%`, backgroundColor: color }}
            />
          </div>
          <div className="text-xs text-muted-foreground">
            已用: {hasUsage ? formatTokens(usage.total_tokens) : '0'}
            {hasUsage && <>{' · '}总大小: {formatTokens(usage.context_window)}</>}
          </div>
          {hasUsage && usage.detail && (
            <div className="flex flex-col gap-1.5 border-t pt-2">
              {Object.entries(DETAIL_LABELS).map(([key, label]) => {
                const count = usage.detail[key as keyof UsageInfo['detail']] || 0
                return (
                  <div key={key} className="flex justify-between items-center text-xs">
                    <span className="text-muted-foreground">{label}</span>
                    <span className="tabular-nums">
                      {formatTokens(count)}
                      <span className="text-muted-foreground/60">
                        {' '}{(usage.context_window > 0 ? (count / usage.context_window * 100).toFixed(1) : '0.0')}%
                      </span>
                    </span>
                  </div>
                )
              })}
            </div>
          )}
          {onCompress && (
            <button
              className="w-full mt-1 py-1.5 rounded-lg text-xs font-medium border transition-colors
                disabled:opacity-40 disabled:cursor-not-allowed
                hover:bg-tag-amber hover:border-amber-300 hover:text-amber-700
                dark:hover:bg-amber-950 dark:hover:border-amber-700 dark:hover:text-amber-300"
              disabled={isTurnRunning || isCompressing}
              onClick={(e) => { e.stopPropagation(); onCompress() }}
            >
              {isCompressing ? '压缩中...' : '压缩上下文'}
            </button>
          )}
        </div>
      )}
    </span>
  )
}
