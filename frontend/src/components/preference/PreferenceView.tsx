import { useCallback, useEffect, useState } from 'react'
import { Plus, Settings, Trash2 } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { novel } from '@/hooks/useApp'

interface Props { novelId: number }

interface EditState {
  id: number
  category: string
  content: string
}

export default function PreferenceView({ novelId }: Props) {
  const app = useApp()

  const [global, setGlobal] = useState<novel.PreferenceItem[]>([])
  const [novelPrefs, setNovelPrefs] = useState<novel.PreferenceItem[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<EditState | null>(null)
  const [adding, setAdding] = useState<'global' | 'novel' | null>(null)
  const [newCategory, setNewCategory] = useState('')
  const [newContent, setNewContent] = useState('')

  const load = useCallback(async () => {
    if (!novelId) { setGlobal([]); setNovelPrefs([]); return }
    setLoading(true)
    setError(null)
    try {
      const result = await app.GetPreferences(novelId)
      setGlobal(result.global ?? [])
      setNovelPrefs(result.novel ?? [])
    } catch (err) {
      setError(err instanceof Error ? err.message : '加载失败')
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => { load() }, [load])

  async function handleSave(edit: EditState) {
    await app.UpdatePreference(edit.id, { category: edit.category, content: edit.content })
    setEditing(null)
    load()
  }

  async function handleDelete(id: number) {
    await app.DeletePreference(id)
    load()
  }

  async function handleAdd(isGlobal: boolean) {
    if (!newContent.trim()) return
    await app.CreatePreference(novelId, { is_global: isGlobal, category: newCategory || '未分类', content: newContent })
    setAdding(null)
    setNewCategory('')
    setNewContent('')
    load()
  }

  function startEdit(item: novel.PreferenceItem) {
    setEditing({ id: item.id, category: item.category, content: item.content })
  }

  return (
    <main className="flex-1 min-w-0 overflow-y-auto overscroll-contain bg-background">
      {loading ? (
        <div className="flex h-full items-center justify-center text-sm text-muted-foreground">加载中...</div>
      ) : error ? (
        <div className="flex h-full items-center justify-center text-sm text-rose-500">{error}</div>
      ) : (
        <div className="max-w-3xl mx-auto px-5 py-6 space-y-8">
          {/* Header */}
          <div className="flex items-center gap-2">
            <Settings className="h-4 w-4 text-muted-foreground" />
            <h2 className="text-sm font-semibold text-foreground">
              创作偏好
              <span className="ml-2 text-xs font-normal text-muted-foreground">{global.length + novelPrefs.length} 条</span>
            </h2>
          </div>

          {/* 全局偏好 */}
          <section>
            <div className="flex items-center justify-between mb-3">
              <h3 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">全局偏好 · 所有小说生效</h3>
              <button
                onClick={() => setAdding('global')}
                className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-muted-foreground transition-colors"
              >
                <Plus className="h-3 w-3" /> 添加
              </button>
            </div>
            {global.length === 0 ? (
              <p className="text-xs text-muted-foreground py-4">暂无全局偏好</p>
            ) : (
              <div className="space-y-2">
                {global.map(item => (
                  <PreferenceCard
                    key={item.id}
                    item={item}
                    editing={editing}
                    onEdit={startEdit}
                    onSave={handleSave}
                    onCancel={() => setEditing(null)}
                    onDelete={handleDelete}
                    setEditingCategory={(v) => setEditing(prev => prev ? { ...prev, category: v } : null)}
                    setEditingContent={(v) => setEditing(prev => prev ? { ...prev, content: v } : null)}
                  />
                ))}
              </div>
            )}
            {adding === 'global' && (
              <AddForm
                category={newCategory}
                content={newContent}
                onCategoryChange={setNewCategory}
                onContentChange={setNewContent}
                onSave={() => handleAdd(true)}
                onCancel={() => { setAdding(null); setNewCategory(''); setNewContent('') }}
              />
            )}
          </section>

          {/* 分隔 */}
          <div className="border-t border-border" />

          {/* 本书偏好 */}
          <section>
            <div className="flex items-center justify-between mb-3">
              <h3 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">本书偏好 · 仅当前小说生效</h3>
              <button
                onClick={() => setAdding('novel')}
                className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-muted-foreground transition-colors"
              >
                <Plus className="h-3 w-3" /> 添加
              </button>
            </div>
            {novelPrefs.length === 0 ? (
              <p className="text-xs text-muted-foreground py-4">暂无本书偏好</p>
            ) : (
              <div className="space-y-2">
                {novelPrefs.map(item => (
                  <PreferenceCard
                    key={item.id}
                    item={item}
                    editing={editing}
                    onEdit={startEdit}
                    onSave={handleSave}
                    onCancel={() => setEditing(null)}
                    onDelete={handleDelete}
                    setEditingCategory={(v) => setEditing(prev => prev ? { ...prev, category: v } : null)}
                    setEditingContent={(v) => setEditing(prev => prev ? { ...prev, content: v } : null)}
                  />
                ))}
              </div>
            )}
            {adding === 'novel' && (
              <AddForm
                category={newCategory}
                content={newContent}
                onCategoryChange={setNewCategory}
                onContentChange={setNewContent}
                onSave={() => handleAdd(false)}
                onCancel={() => { setAdding(null); setNewCategory(''); setNewContent('') }}
              />
            )}
          </section>
        </div>
      )}
    </main>
  )
}

// ── 子组件 ──

function PreferenceCard({
  item, editing, onEdit, onSave, onCancel, onDelete,
  setEditingCategory, setEditingContent,
}: {
  item: novel.PreferenceItem
  editing: EditState | null
  onEdit: (item: novel.PreferenceItem) => void
  onSave: (edit: EditState) => void
  onCancel: () => void
  onDelete: (id: number) => void
  setEditingCategory: (v: string) => void
  setEditingContent: (v: string) => void
}) {
  const isEditing = editing?.id === item.id

  return (
    <div className={`rounded-lg border bg-card transition-shadow ${isEditing ? 'border-border shadow-sm' : 'border-border'}`}>
      {isEditing ? (
        <div className="px-4 py-3 space-y-2">
          <input
            value={editing!.category}
            onChange={e => setEditingCategory(e.target.value)}
            placeholder="分类"
            className="w-full text-xs border border-border rounded px-2 py-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
          <textarea
            value={editing!.content}
            onChange={e => setEditingContent(e.target.value)}
            placeholder="偏好内容"
            rows={3}
            className="w-full text-xs border border-border rounded px-2 py-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-none"
          />
          <div className="flex items-center gap-2">
            <button
              onClick={() => onSave(editing!)}
              className="px-3 py-1 rounded text-xs font-medium bg-foreground text-background hover:bg-foreground/80 transition-colors"
            >
              保存
            </button>
            <button
              onClick={onCancel}
              className="px-3 py-1 rounded text-xs text-muted-foreground hover:text-foreground transition-colors"
            >
              取消
            </button>
          </div>
        </div>
      ) : (
        <div className="flex items-start gap-3 px-4 py-3 group">
          <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium bg-secondary text-muted-foreground">
            {item.category || '未分类'}
          </span>
          <p className="flex-1 text-sm text-foreground leading-relaxed whitespace-pre-wrap">{item.content}</p>
          <div className="shrink-0 flex items-center gap-2 opacity-0 group-hover:opacity-100 transition-opacity">
            <button
              onClick={() => onEdit(item)}
              className="text-xs text-muted-foreground hover:text-foreground transition-colors"
            >
              编辑
            </button>
            <button
              onClick={() => onDelete(item.id)}
              className="text-muted-foreground hover:text-destructive transition-colors"
            >
              <Trash2 className="h-3.5 w-3.5" />
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

function AddForm({
  category, content, onCategoryChange, onContentChange, onSave, onCancel,
}: {
  category: string
  content: string
  onCategoryChange: (v: string) => void
  onContentChange: (v: string) => void
  onSave: () => void
  onCancel: () => void
}) {
  return (
    <div className="mt-2 rounded-lg border border-dashed border-border bg-card/60 px-4 py-3 space-y-2">
      <input
        value={category}
        onChange={e => onCategoryChange(e.target.value)}
        placeholder="分类（如：风格、对话、世界观）"
        className="w-full text-xs border border-border rounded px-2 py-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      />
      <textarea
        value={content}
        onChange={e => onContentChange(e.target.value)}
        placeholder="偏好内容"
        rows={3}
        className="w-full text-xs border border-border rounded px-2 py-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-none"
      />
      <div className="flex items-center gap-2">
        <button
          onClick={onSave}
          disabled={!content.trim()}
          className="px-3 py-1 rounded text-xs font-medium bg-foreground text-background hover:bg-foreground/80 disabled:opacity-30 transition-colors"
        >
          添加
        </button>
        <button
          onClick={onCancel}
          className="px-3 py-1 rounded text-xs text-muted-foreground hover:text-foreground transition-colors"
        >
          取消
        </button>
      </div>
    </div>
  )
}
