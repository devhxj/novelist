import { useState, useEffect } from 'react'
import type { novel } from '@/hooks/useApp'

const GENRE_PRESETS = ['玄幻', '科幻', '都市', '历史', '悬疑', '武侠', '言情', '其他']

function errorMessage(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback
}

interface Props {
  open: boolean
  novel?: novel.Novel | null  // 传了=编辑，不传=创建
  onClose: () => void
  onSave: (input: { title: string; description: string; genre: string }) => Promise<void>
}

export default function NovelEditDialog({ open, novel, onClose, onSave }: Props) {
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [genre, setGenre] = useState('')
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    const timer = window.setTimeout(() => {
      setTitle(novel?.title ?? '')
      setDescription(novel?.description ?? '')
      setGenre(novel?.genre ?? '')
      setSaving(false)
      setError('')
    }, 0)
    return () => window.clearTimeout(timer)
  }, [open, novel])

  if (!open) return null

  const isEdit = !!novel
  const canSave = isEdit ? true : title.trim().length > 0

  async function handleSave() {
    if (!canSave || saving) return
    setSaving(true)
    setError('')
    try {
      await onSave({ title: title.trim(), description: description.trim(), genre: genre.trim() })
    } catch (e: unknown) {
      setError(errorMessage(e, '保存失败，请重试'))
    } finally {
      setSaving(false)
    }
  }

  function handleKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      handleSave()
    }
    if (e.key === 'Escape') onClose()
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="absolute inset-0 bg-black/40" onClick={onClose} />
      <div className="relative bg-background rounded-xl shadow-2xl border w-[420px] max-w-[90vw] p-6" onKeyDown={handleKeyDown}>
        <button
          onClick={onClose}
          className="absolute top-3 right-3 w-7 h-7 flex items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-muted transition-colors"
        >
          ✕
        </button>

        <h2 className="text-base font-semibold mb-5">{isEdit ? '编辑作品' : '新建作品'}</h2>

        {error && (
          <p className="text-sm text-red-600 bg-danger-bg border border-danger-border rounded-md px-3 py-2 mb-4">{error}</p>
        )}

        <div className="space-y-4">
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1.5">书名 {!isEdit && <span className="text-red-500">*</span>}</label>
            <input
              type="text" value={title} autoFocus
              onChange={e => setTitle(e.target.value)}
              placeholder="输入书名"
              className="w-full h-9 rounded-md border bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1.5">分类</label>
            <input
              type="text" value={genre}
              onChange={e => setGenre(e.target.value)}
              placeholder="如：玄幻、科幻、都市..."
              list="genre-suggestions"
              className="w-full h-9 rounded-md border bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
            <datalist id="genre-suggestions">
              {GENRE_PRESETS.map(g => <option key={g} value={g} />)}
            </datalist>
          </div>

          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1.5">简介</label>
            <textarea
              value={description}
              onChange={e => setDescription(e.target.value)}
              placeholder="简单介绍一下这部作品（可选）"
              rows={3}
              className="w-full rounded-md border bg-background px-3 py-2 text-sm resize-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          </div>
        </div>

        <div className="flex justify-end gap-2 mt-6">
          <button
            onClick={onClose}
            className="h-9 px-4 rounded-md text-sm border hover:bg-muted transition-colors"
          >
            取消
          </button>
          <button
            onClick={handleSave}
            disabled={!canSave || saving}
            className="h-9 px-4 rounded-md text-sm bg-primary text-primary-foreground hover:opacity-90 transition-opacity disabled:opacity-50"
          >
            {saving ? '保存中...' : '保存'}
          </button>
        </div>
      </div>
    </div>
  )
}
