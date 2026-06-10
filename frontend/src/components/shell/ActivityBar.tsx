import type { LucideIcon } from 'lucide-react'
import { Library, List, Users, MapPin, GitBranch, History } from 'lucide-react'

interface Activity {
  id: string
  icon: LucideIcon
  label: string
  disabled?: boolean
}

const activities: Activity[] = [
  { id: 'novels', icon: Library, label: '书架' },
  { id: 'chapters', icon: List, label: '章节' },
  { id: 'characters', icon: Users, label: '角色' },
  { id: 'locations', icon: MapPin, label: '地点' },
  { id: 'storyarcs', icon: GitBranch, label: '弧线' },
  { id: 'timeline', icon: History, label: '时间线' },
]

interface Props {
  activeId: string
  onSelect: (id: string) => void
}

export default function ActivityBar({ activeId, onSelect }: Props) {
  return (
    <nav className="w-12 flex flex-col items-center py-3 gap-1.5 border-r bg-sidebar">
      {activities.map((a, i) => {
        const isActive = a.id === activeId
        return (
          <div key={a.id}>
            {i === 1 && <div className="w-6 h-px bg-border my-1 mx-auto" />}
            <button
              disabled={a.disabled}
              onClick={() => onSelect(a.id)}
              title={`${a.label}${a.disabled ? '（即将推出）' : ''}`}
              className={`relative w-10 h-10 flex items-center justify-center rounded-lg transition-all duration-200
                focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring
                ${a.disabled
                  ? 'text-muted-foreground/40 cursor-not-allowed'
                  : isActive
                    ? 'text-foreground bg-muted'
                    : 'text-muted-foreground hover:text-foreground hover:bg-muted/60'
                }`}
            >
              {isActive && !a.disabled && (
                <span className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 bg-primary rounded-r-full" />
              )}
              <a.icon className="w-5 h-5" />
            </button>
          </div>
        )
      })}
    </nav>
  )
}
