import { useState, useEffect, useCallback, useMemo } from 'react'
import { Search, Plus } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { skill } from '@/hooks/useApp'

interface Props {
  novelId: number
  activeSkillName: string | null
  onSelectSkill: (path: string, title: string, readOnly: boolean) => void
  onNewSkill: (name: string) => void
}

function skillPath(name: string, source: string): string {
  switch (source) {
    case 'novel': return `skills/${name}.md`
    case 'user': return `~/.goink/skills/${name}.md`
    case 'builtin': return `/builtin/skills/${name}.md`
    default: return `skills/${name}.md`
  }
}

export default function SkillList({ novelId, activeSkillName, onSelectSkill, onNewSkill }: Props) {
  const app = useApp()
  const [skills, setSkills] = useState<skill.SkillMeta[]>([])
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(false)
  const [creating, setCreating] = useState(false)
  const [newName, setNewName] = useState('')

  const load = useCallback(async () => {
    if (!novelId) { setSkills([]); return }
    setLoading(true)
    try {
      const list = await app.ListSkills({ novel_id: novelId })
      setSkills(list ?? [])
    } catch (err) {
      console.error('Failed to load skills:', err)
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => { load() }, [load])

  const filtered = useMemo(() => {
    if (!search.trim()) return skills
    const q = search.toLowerCase()
    return skills.filter(s => s.name.toLowerCase().includes(q) || s.description.toLowerCase().includes(q))
  }, [skills, search])

  const novelSkills = filtered.filter(s => s.source === 'novel')
  const userSkills = filtered.filter(s => s.source === 'user')
  const builtinSkills = filtered.filter(s => s.source === 'builtin')

  return (
    <>
      <div className="flex items-center justify-between px-3 py-2.5 border-b gap-1">
        <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
          技能 ({skills.length})
        </span>
        <button
          onClick={() => setCreating(true)}
          className="p-0.5 rounded hover:bg-muted/60 text-muted-foreground hover:text-foreground transition-colors"
          title="新建技能"
        >
          <Plus className="w-3.5 h-3.5" />
        </button>
      </div>
      {creating && (
        <div className="px-2 py-1.5 border-b flex gap-1">
          <input
            type="text"
            value={newName}
            onChange={e => setNewName(e.target.value)}
            onKeyDown={e => {
              if (e.key === 'Enter' && newName.trim()) {
                onNewSkill(newName.trim())
                setCreating(false)
                setNewName('')
              }
              if (e.key === 'Escape') {
                setCreating(false)
                setNewName('')
              }
            }}
            onBlur={() => {
              if (!newName.trim()) {
                setCreating(false)
              }
            }}
            placeholder="skill 名称..."
            autoFocus
            className="flex-1 px-2 py-0.5 text-xs bg-background border rounded outline-none focus:ring-1 focus:ring-ring"
          />
          <button
            onClick={() => {
              if (newName.trim()) {
                onNewSkill(newName.trim())
                setCreating(false)
                setNewName('')
              }
            }}
            disabled={!newName.trim()}
            className="px-2 py-0.5 text-xs text-emerald-600 hover:text-emerald-700 disabled:opacity-50"
          >
            确定
          </button>
        </div>
      )}
      <div className="px-2 py-1.5">
        <div className="relative">
          <Search className="absolute left-2 top-1/2 -translate-y-1/2 w-3 h-3 text-muted-foreground" />
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="搜索..."
            className="w-full pl-7 pr-2 py-1 text-xs bg-muted/40 rounded border-0 outline-none focus:ring-1 focus:ring-ring"
          />
        </div>
      </div>
      <div className="flex-1 overflow-y-auto overscroll-contain">
        {loading ? (
          <div className="flex items-center justify-center py-8 text-xs text-muted-foreground">加载中...</div>
        ) : skills.length === 0 ? (
          <div className="flex items-center justify-center py-8 text-xs text-muted-foreground">暂无技能</div>
        ) : (
          <>
            {novelSkills.length > 0 && (
              <SkillGroup title="当前小说" skills={novelSkills} activeSkillName={activeSkillName} onSelect={onSelectSkill} />
            )}
            {userSkills.length > 0 && (
              <SkillGroup title="用户级" skills={userSkills} activeSkillName={activeSkillName} onSelect={onSelectSkill} />
            )}
            {builtinSkills.length > 0 && (
              <SkillGroup title="内置" skills={builtinSkills} activeSkillName={activeSkillName} onSelect={onSelectSkill} />
            )}
          </>
        )}
      </div>
    </>
  )
}

function SkillGroup({ title, skills, activeSkillName, onSelect }: {
  title: string
  skills: skill.SkillMeta[]
  activeSkillName: string | null
  onSelect: (path: string, title: string, readOnly: boolean) => void
}) {
  return (
    <div>
      <div className="px-3 py-1.5">
        <span className="text-[10px] font-semibold text-muted-foreground/60 uppercase tracking-wider">{title}</span>
      </div>
      {skills.map(s => {
        const path = skillPath(s.name, s.source)
        const display = `技能: ${s.name}`
        const readOnly = s.source === 'builtin'
        const active = activeSkillName === display
        return (
          <button
            key={`${s.source}:${s.name}`}
            onClick={() => onSelect(path, display, readOnly)}
            className={`w-full flex flex-col px-3 py-1.5 text-left hover:bg-muted/50 transition-colors relative
              ${active ? 'bg-muted' : ''}`}
          >
            {active && (
              <span className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 bg-primary rounded-r-full" />
            )}
            <span className="text-sm truncate">{s.name}</span>
            {s.description && (
              <span className="text-[11px] text-muted-foreground truncate">{s.description}</span>
            )}
          </button>
        )
      })}
    </div>
  )
}
