import { useState, useEffect, useCallback, useMemo } from 'react'
import { Search, Plus, Sparkle, Pencil, Trash2 } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { skill } from '@/hooks/useApp'
import ExtractStyleDialog from './ExtractStyleDialog'

interface Props {
  novelId: number
  activeSkillName: string | null
  onSelectSkill: (path: string, title: string, readOnly: boolean) => void
  onEditSkill: (path: string, title: string, readOnly: boolean) => void
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

export default function SkillList({ novelId, activeSkillName, onSelectSkill, onEditSkill, onNewSkill }: Props) {
  const app = useApp()
  const [skills, setSkills] = useState<skill.SkillMeta[]>([])
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(false)
  const [creating, setCreating] = useState(false)
  const [newName, setNewName] = useState('')
  const [dialogOpen, setDialogOpen] = useState(false)

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

  const handleDelete = async (s: skill.SkillMeta) => {
    if (!confirm(`确定删除技能「${s.name}」？此操作不可撤销。`)) return
    try {
      await app.DeleteSkill({ novel_id: novelId, name: s.name, source: s.source })
      await load()
    } catch (err) {
      console.error('Failed to delete skill:', err)
    }
  }

  return (
    <>
      <div className="flex items-center justify-between px-3 py-2.5 border-b gap-1">
        <span className="text-xs font-medium text-muted-foreground uppercase tracking-wider">
          技能 ({skills.length})
        </span>
        <div className="flex items-center gap-0.5">
          <button
            onClick={() => setDialogOpen(true)}
            className="p-0.5 rounded hover:bg-muted/60 text-action-extract hover:text-action-extract/80 transition-colors"
            title="提取写作风格"
          >
            <Sparkle className="w-3.5 h-3.5" />
          </button>
          <button
            onClick={() => setCreating(true)}
            className="p-0.5 rounded hover:bg-muted/60 text-muted-foreground hover:text-foreground transition-colors"
            title="新建技能"
          >
            <Plus className="w-3.5 h-3.5" />
          </button>
        </div>
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
            className="px-2 py-0.5 text-xs text-action-save hover:text-action-save/80 disabled:opacity-50"
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
              <SkillGroup
                title="当前小说"
                skills={novelSkills}
                activeSkillName={activeSkillName}
                onSelect={onSelectSkill}
                onEdit={onEditSkill}
                onDelete={handleDelete}
              />
            )}
            {userSkills.length > 0 && (
              <SkillGroup
                title="用户级"
                skills={userSkills}
                activeSkillName={activeSkillName}
                onSelect={onSelectSkill}
                onEdit={onEditSkill}
                onDelete={handleDelete}
              />
            )}
            {builtinSkills.length > 0 && (
              <SkillGroup
                title="内置"
                skills={builtinSkills}
                activeSkillName={activeSkillName}
                onSelect={onSelectSkill}
                onEdit={onEditSkill}
                onDelete={handleDelete}
              />
            )}
          </>
        )}
      </div>
      <ExtractStyleDialog
        open={dialogOpen}
        novelId={novelId}
        onClose={() => setDialogOpen(false)}
        onSaved={load}
      />
    </>
  )
}

function SkillGroup({ title, skills, activeSkillName, onSelect, onEdit, onDelete }: {
  title: string
  skills: skill.SkillMeta[]
  activeSkillName: string | null
  onSelect: (path: string, title: string, readOnly: boolean) => void
  onEdit: (path: string, title: string, readOnly: boolean) => void
  onDelete: (s: skill.SkillMeta) => void
}) {
  const isBuiltin = skills[0]?.source === 'builtin'
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
          <div key={`${s.source}:${s.name}`} className="group relative">
            <button
              onClick={() => onSelect(path, display, readOnly)}
              className={`w-full flex flex-col px-3 py-1.5 text-left hover:bg-muted/50 transition-colors ${
                active ? 'bg-muted' : ''
              }`}
            >
              {active && (
                <span className="absolute left-0 top-1/2 -translate-y-1/2 w-0.5 h-5 bg-primary rounded-r-full" />
              )}
              <span className="text-sm truncate">{s.name}</span>
              {s.description && (
                <span className="text-[11px] text-muted-foreground truncate">{s.description}</span>
              )}
            </button>
            {!isBuiltin && (
              <div className="absolute right-2 top-1/2 -translate-y-1/2 flex items-center gap-0.5 opacity-0 group-hover:opacity-100 transition-opacity">
                <button
                  onClick={e => {
                    e.stopPropagation()
                    onEdit(path, display, readOnly)
                  }}
                  className="p-0.5 rounded hover:bg-muted/60 text-muted-foreground hover:text-foreground transition-colors"
                  title="编辑技能"
                >
                  <Pencil className="w-3 h-3" />
                </button>
                <button
                  onClick={e => {
                    e.stopPropagation()
                    onDelete(s)
                  }}
                  className="p-0.5 rounded hover:bg-muted/60 text-muted-foreground hover:text-destructive transition-colors"
                  title="删除技能"
                >
                  <Trash2 className="w-3 h-3" />
                </button>
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}
