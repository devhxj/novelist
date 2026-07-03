import { useState, useEffect } from 'react'
import { splitFrontmatter } from '@/components/content/types'

const MODE_OPTIONS = [
  { value: 'auto', label: '智能 — AI 可自主调用，用户也可 / 触发' },
  { value: 'manual', label: '指令 — 仅用户 / 手动触发，不出现在目录中' },
  { value: 'always', label: '常驻 — 会话开头自动注入，始终生效' },
]

const KNOWN_FIELDS = ['name', 'description', 'category', 'mode', 'author', 'version']

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error) return error.message
  if (typeof error === 'string') return error
  return fallback
}

interface Props {
  content: string
  readOnly?: boolean
  onSave: (newContent: string) => Promise<void>
  onCancel: () => void
}

export default function SkillEditForm({ content, readOnly, onSave, onCancel }: Props) {
  const { meta, body } = splitFrontmatter(content)

  const [name, setName] = useState(meta.name || '')
  const [description, setDescription] = useState(meta.description || '')
  const [category, setCategory] = useState(meta.category || '')
  const [mode, setMode] = useState(meta.mode || 'auto')
  const [author, setAuthor] = useState(meta.author || '')
  const [version, setVersion] = useState(meta.version || '1')
  const [bodyText, setBodyText] = useState(body || '')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [extraFields, setExtraFields] = useState<[string, string][]>([])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      const { meta: m, body: b } = splitFrontmatter(content)
      setName(m.name || '')
      setDescription(m.description || '')
      setCategory(m.category || '')
      setMode(m.mode || 'auto')
      setAuthor(m.author || '')
      setVersion(m.version || '1')
      setBodyText(b || '')
      setError('')
      const extras: [string, string][] = []
      for (const [k, v] of Object.entries(m)) {
        if (!KNOWN_FIELDS.includes(k)) {
          extras.push([k, v])
        }
      }
      setExtraFields(extras)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [content])

  if (readOnly) {
    return (
      <div className="flex items-center justify-center h-full">
        <p className="text-sm text-muted-foreground">内置技能不可编辑</p>
      </div>
    )
  }

  const handleSave = async () => {
    if (saving) return
    if (!name.trim()) { setError('名称不能为空'); return }
    if (!description.trim()) { setError('简介不能为空'); return }
    setSaving(true)
    setError('')
    try {
      const lines = [
        '---',
        `name: ${name.trim()}`,
        `description: ${description.trim()}`,
        `category: ${category.trim() || '未分类'}`,
        `mode: ${mode}`,
      ]
      if (author.trim()) {
        lines.push(`author: ${author.trim()}`)
      }
      lines.push(`version: ${parseInt(version) || 1}`)
      for (const [k, v] of extraFields) {
        lines.push(`${k}: ${v}`)
      }
      lines.push('---', '', bodyText.trim())
      await onSave(lines.join('\n'))
    } catch (e: unknown) {
      setError(errorMessage(e, '保存失败，请重试'))
    } finally {
      setSaving(false)
    }
  }

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape') onCancel()
  }

  return (
    <div className="overflow-y-auto h-full" onKeyDown={handleKeyDown}>
      <div className="max-w-2xl mx-auto px-6 py-6 space-y-4">
        {error && (
          <div className="sticky top-0 z-10 px-3 py-2.5 text-sm font-medium text-destructive-foreground bg-destructive border border-destructive rounded-md shadow-sm">
            {error}
          </div>
        )}

        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1.5">名称 *</label>
          <input
            type="text" value={name}
            onChange={e => setName(e.target.value)}
            placeholder="技能名称，如 scene-beats"
            className="w-full h-9 rounded-md border bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            autoFocus
          />
        </div>

        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1.5">简介 *</label>
          <textarea
            value={description}
            onChange={e => setDescription(e.target.value)}
            placeholder="简要描述此技能的功能和触发时机"
            rows={3}
            className="w-full rounded-md border bg-background px-3 py-2 text-sm resize-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </div>

        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1.5">分类</label>
          <input
            type="text" value={category}
            onChange={e => setCategory(e.target.value)}
            placeholder="如：结构、风格、系统"
            className="w-full h-9 rounded-md border bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </div>

        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1.5">模式</label>
          <select
            value={mode}
            onChange={e => setMode(e.target.value)}
            className="w-full h-9 rounded-md border bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {MODE_OPTIONS.map(o => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </select>
        </div>

        <div className="flex gap-4">
          <div className="flex-1">
            <label className="block text-xs font-medium text-muted-foreground mb-1.5">作者</label>
            <input
              type="text" value={author}
              onChange={e => setAuthor(e.target.value)}
              placeholder="技能创建者"
              className="w-full h-9 rounded-md border bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </div>
          <div className="w-24">
            <label className="block text-xs font-medium text-muted-foreground mb-1.5">版本</label>
            <input
              type="number" value={version}
              onChange={e => setVersion(e.target.value)}
              className="w-full h-9 rounded-md border bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </div>
        </div>

        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1.5">内容</label>
          <textarea
            value={bodyText}
            onChange={e => setBodyText(e.target.value)}
            placeholder="技能正文内容（markdown）"
            rows={16}
            className="w-full rounded-md border bg-background px-3 py-2 text-sm resize-y focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <button
            onClick={onCancel}
            className="h-9 px-4 rounded-md text-sm border hover:bg-muted transition-colors"
          >
            取消
          </button>
          <button
            onClick={handleSave}
            disabled={saving}
            className="h-9 px-4 rounded-md text-sm bg-primary text-primary-foreground hover:opacity-90 transition-opacity disabled:opacity-50"
          >
            {saving ? '保存中...' : '保存'}
          </button>
        </div>
      </div>
    </div>
  )
}
