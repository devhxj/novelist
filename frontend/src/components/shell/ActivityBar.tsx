import type { LucideIcon } from 'lucide-react'
import { BookMarked, GitCommitHorizontal, Library, List, Search, SlidersHorizontal, Users, MapPin, GitBranch, History, Eye, Wrench } from 'lucide-react'

interface Activity {
  id: string
  icon: LucideIcon
  label: string
  disabled?: boolean
}

// 三主区：书籍 / 语料。设置统一走顶栏齿轮（A6：消除双入口），不占活动栏面板位。
const primaryActivities: Activity[] = [
  { id: 'novels', icon: Library, label: '书架' },
  { id: 'reference', icon: BookMarked, label: '素材库' },
]

// 本书工具：只要打开了一本书就稳定可见（跨书架/语料区导航不隐藏，避免导航断链），数据范围限定当前作品。
const bookToolActivities: Activity[] = [
  { id: 'chapters', icon: List, label: '章节' },
  { id: 'search', icon: Search, label: '搜索' },
  { id: 'skills', icon: Wrench, label: '技能' },
  { id: 'characters', icon: Users, label: '角色' },
  { id: 'locations', icon: MapPin, label: '地点' },
  { id: 'storyarcs', icon: GitBranch, label: '弧线' },
  { id: 'timeline', icon: History, label: '时间线' },
  { id: 'reader', icon: Eye, label: '读者视角' },
  { id: 'preferences', icon: SlidersHorizontal, label: '偏好' },
  { id: 'git-history', icon: GitCommitHorizontal, label: 'Git 历史' },
]

interface Props {
  activeId: string
  bookToolsVisible: boolean
  onSelect: (id: string) => void
}

function ActivityButton({ activity, isActive, onSelect }: { activity: Activity; isActive: boolean; onSelect: (id: string) => void }) {
  return (
    <button
      disabled={activity.disabled}
      onClick={() => onSelect(activity.id)}
      title={`${activity.label}${activity.disabled ? '（即将推出）' : ''}`}
      className={`relative w-10 h-10 flex items-center justify-center rounded-lg transition-all duration-200
        focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
        ${activity.disabled
          ? 'text-muted-foreground/40 cursor-not-allowed'
          : isActive
            ? 'text-primary bg-primary/15 font-medium'
            : 'text-muted-foreground hover:text-foreground hover:bg-muted/60'
        }`}
    >
      {/* 视觉项：选中态增强——指示条加粗 + 图标着主色，不再只是细线 */}
      {isActive && !activity.disabled && (
        <span className="absolute left-0 top-1/2 -translate-y-1/2 w-1 h-6 bg-primary rounded-r-full" />
      )}
      <activity.icon className="w-5 h-5" />
    </button>
  )
}

export default function ActivityBar({ activeId, bookToolsVisible, onSelect }: Props) {
  return (
    <nav className="w-12 flex flex-col items-center py-3 gap-1.5 border-r bg-sidebar select-none cursor-default">
      {primaryActivities.map((activity) => (
        <ActivityButton key={activity.id} activity={activity} isActive={activity.id === activeId} onSelect={onSelect} />
      ))}
      {bookToolsVisible && (
        <>
          <div className="w-6 h-px bg-border my-1 mx-auto" aria-hidden="true" />
          {bookToolActivities.map((activity) => (
            <ActivityButton key={activity.id} activity={activity} isActive={activity.id === activeId} onSelect={onSelect} />
          ))}
        </>
      )}
    </nav>
  )
}
