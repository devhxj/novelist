import { useCallback, useEffect, useState } from 'react'
import { useApp } from '@/hooks/useApp'
import ContributionGrid from './ContributionGrid'
import { PenLine, CalendarDays, Flame, User } from 'lucide-react'

interface WritingStats {
  total_words: number
  total_days_active: number
  current_streak: number
  longest_streak: number
  total_novels: number
  total_chapters: number
}

export default function ProfileView() {
  const app = useApp()
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [activity, setActivity] = useState<Record<string, number>>({})
  const [stats, setStats] = useState<WritingStats | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [act, st] = await Promise.all([
        app.GetWritingActivity(12),
        app.GetWritingStats(),
      ])
      const dict: Record<string, number> = {}
      if (act) {
        for (const d of act as Array<{ date: string; words: number }>) {
          dict[d.date] = d.words
        }
      }
      setActivity(dict)
      setStats(st as WritingStats)
    } catch (err) {
      setError(err instanceof Error ? err.message : '加载失败')
    } finally {
      setLoading(false)
    }
  }, [app])

  useEffect(() => { load() }, [load])

  if (loading) {
    return (
      <main className="flex-1 min-w-0 overflow-y-auto bg-[#fafbfc] dark:bg-background">
        <div className="flex h-full items-center justify-center text-sm text-slate-500">加载中...</div>
      </main>
    )
  }

  if (error) {
    return (
      <main className="flex-1 min-w-0 overflow-y-auto bg-[#fafbfc] dark:bg-background">
        <div className="flex h-full items-center justify-center text-sm text-rose-500">{error}</div>
      </main>
    )
  }

  return (
    <main className="flex-1 min-w-0 overflow-y-auto bg-[#fafbfc] dark:bg-background">
      <div className="max-w-4xl mx-auto px-6 py-8 space-y-8">
        {/* 头像 + 问候 */}
        <div className="flex items-center gap-4">
          <div className="w-14 h-14 rounded-full bg-slate-200 dark:bg-slate-700 flex items-center justify-center flex-shrink-0">
            <User className="w-7 h-7 text-slate-400 dark:text-slate-500" />
          </div>
          <div>
            <h1 className="text-lg font-semibold text-slate-800 dark:text-foreground">
              我的书房
            </h1>
            <p className="text-xs text-slate-500 mt-0.5">
              过去一年 · {Object.keys(activity).length} 天有写作记录
            </p>
          </div>
        </div>

        {/* 统计卡片 */}
        <div className="grid grid-cols-2 gap-3">
          <StatCard
            icon={PenLine}
            label="累计字数"
            value={(stats?.total_words ?? 0).toLocaleString()}
          />
          <StatCard
            icon={CalendarDays}
            label="写作天数"
            value={`${stats?.total_days_active ?? 0}`}
          />
          <StatCard
            icon={Flame}
            label="连续写作"
            value={`${stats?.current_streak ?? 0} 天`}
          />
          <StatCard
            icon={Flame}
            label="最长连续"
            value={`${stats?.longest_streak ?? 0} 天`}
          />
        </div>

        {/* 作品/章节概览 */}
        <div className="flex gap-6 text-xs text-slate-500">
          <span>作品 <b className="text-slate-700 dark:text-foreground">{stats?.total_novels ?? 0}</b> 部</span>
          <span>章节 <b className="text-slate-700 dark:text-foreground">{stats?.total_chapters ?? 0}</b> 章</span>
        </div>

        {/* 绿格子 */}
        <section>
          <h2 className="text-sm font-medium text-slate-700 dark:text-foreground mb-4">
            {new Date().getFullYear()} 年写作日历
          </h2>
          <div className="overflow-x-auto">
            <ContributionGrid data={activity} />
          </div>
        </section>

        {Object.keys(activity).length === 0 && (
          <div className="text-center py-12">
            <PenLine className="w-10 h-10 mx-auto text-slate-300 dark:text-slate-600 mb-3" />
            <p className="text-sm text-slate-500">
              还没有写作记录。开始写吧，每天的字数都会被记录下来。
            </p>
          </div>
        )}
      </div>
    </main>
  )
}

function StatCard({ icon: Icon, label, value }: { icon: any; label: string; value: string }) {
  return (
    <div className="rounded-lg border bg-white dark:bg-card px-4 py-3 space-y-1">
      <div className="flex items-center gap-1.5 text-slate-400">
        <Icon className="w-3.5 h-3.5" />
        <span className="text-[11px]">{label}</span>
      </div>
      <p className="text-lg font-semibold text-slate-800 dark:text-foreground">{value}</p>
    </div>
  )
}
