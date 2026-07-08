import { useCallback, useEffect, useState } from 'react'
import { Pencil, Plus, Settings, Trash2, X } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { novel } from '@/hooks/useApp'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics } from '@/lib/novelist/types'

interface Props { novelId: number; focusId?: number }

type EditMode =
  | { type: 'create'; isGlobal: boolean }
  | { type: 'edit'; item: novel.PreferenceItem }
  | null

type EditForm = {
  category: string
  content: string
}

const EMPTY_FORM: EditForm = { category: '', content: '' }

type VisibleError = {
  message: string
  diagnostic?: diagnostics.CopyableDiagnostic | null
}

export default function PreferenceView({ novelId }: Props) {
  const app = useApp()

  const [global, setGlobal] = useState<novel.PreferenceItem[]>([])
  const [novelPrefs, setNovelPrefs] = useState<novel.PreferenceItem[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<VisibleError | null>(null)
  const [editMode, setEditMode] = useState<EditMode>(null)
  const [form, setForm] = useState<EditForm>(EMPTY_FORM)
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    if (!novelId) { setGlobal([]); setNovelPrefs([]); return }
    setLoading(true)
    setError(null)
    try {
      const result = await app.GetPreferences(novelId)
      setGlobal(result.global ?? [])
      setNovelPrefs(result.novel ?? [])
    } catch (err) {
      setError(buildVisibleError(err, '加载偏好失败', '加载偏好', 'GetPreferences', { novel_id: novelId }))
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      if (!novelId) {
        if (!cancelled) {
          setGlobal([])
          setNovelPrefs([])
        }
        return
      }
      if (!cancelled) {
        setLoading(true)
        setError(null)
      }
      try {
        const result = await app.GetPreferences(novelId)
        if (!cancelled) {
          setGlobal(result.global ?? [])
          setNovelPrefs(result.novel ?? [])
        }
      } catch (err) {
        if (!cancelled) setError(buildVisibleError(err, '加载偏好失败', '加载偏好', 'GetPreferences', { novel_id: novelId }))
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [app, novelId])

  // ── CRUD handlers ────────────────────────────────────

  function openCreate(isGlobal: boolean) {
    setError(null)
    setForm(EMPTY_FORM)
    setEditMode({ type: 'create', isGlobal })
  }

  function openEdit(item: novel.PreferenceItem) {
    setError(null)
    setForm({ category: item.category, content: item.content })
    setEditMode({ type: 'edit', item })
  }

  function closeForm() {
    setEditMode(null)
    setForm(EMPTY_FORM)
  }

  async function handleSave() {
    if (!editMode) return
    if (!form.content.trim()) { setError({ message: '请输入偏好内容' }); return }

    setSaving(true)
    try {
      if (editMode.type === 'create') {
        await app.CreatePreference(novelId, {
          is_global: editMode.isGlobal,
          category: form.category || '未分类',
          content: form.content,
        })
      } else {
        await app.UpdatePreference(editMode.item.id, {
          category: form.category,
          content: form.content,
        })
      }
      setEditMode(null)
      setForm(EMPTY_FORM)
      await load()
    } catch (err) {
      const isCreate = editMode.type === 'create'
      setError(buildVisibleError(
        err,
        isCreate ? '创建偏好失败' : '更新偏好失败',
        isCreate ? '创建偏好' : '更新偏好',
        isCreate ? 'CreatePreference' : 'UpdatePreference',
        {
          novel_id: novelId,
          preference_id: editMode.type === 'edit' ? editMode.item.id : null,
          is_global: editMode.type === 'create' ? editMode.isGlobal : editMode.item.is_global,
          category: form.category || '未分类',
          source_text: form.content,
        },
      ))
    } finally {
      setSaving(false)
    }
  }

  async function handleDelete(id: number) {
    if (!confirm('确定要删除这条偏好吗？此操作不可撤销。')) return
    setSaving(true)
    try {
      await app.DeletePreference(id)
      await load()
    } catch (err) {
      setError(buildVisibleError(err, '删除偏好失败', '删除偏好', 'DeletePreference', {
        novel_id: novelId,
        preference_id: id,
      }))
    } finally {
      setSaving(false)
    }
  }

  // ── Render ───────────────────────────────────────────

  function renderSection(title: string, items: novel.PreferenceItem[], isGlobal: boolean) {
    const isCreating = editMode?.type === 'create' && editMode.isGlobal === isGlobal

    return (
      <section>
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">{title}</h3>
          {!isCreating && (
            <button
              onClick={() => openCreate(isGlobal)}
              className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-muted-foreground transition-colors"
            >
              <Plus className="h-3 w-3" /> 添加
            </button>
          )}
        </div>

        {items.length === 0 && !isCreating ? (
          <p className="text-xs text-muted-foreground py-4">
            {isGlobal ? '暂无全局偏好' : '暂无本书偏好'}
          </p>
        ) : (
          <div className="space-y-2">
            {items.map(item => {
              const isEditing = editMode?.type === 'edit' && editMode.item.id === item.id

              return isEditing ? (
                <div key={item.id} className="rounded-lg border border-border bg-card p-4">
                  <div className="flex items-center justify-between mb-3">
                    <span className="text-xs font-semibold text-foreground">编辑偏好</span>
                    <button onClick={closeForm} className="p-0.5 rounded text-muted-foreground hover:text-foreground">
                      <X className="h-3.5 w-3.5" />
                    </button>
                  </div>
                  {renderFormFields()}
                  <div className="flex items-center gap-2 justify-end mt-3">
                    <button
                      onClick={() => handleDelete(item.id)}
                      className="px-3 py-1 rounded text-xs text-destructive hover:bg-destructive/10 transition-colors"
                      disabled={saving}
                    >
                      <Trash2 className="h-3 w-3 inline mr-1" />删除
                    </button>
                    <button onClick={closeForm} className="px-3 py-1 rounded text-xs text-muted-foreground hover:text-foreground transition-colors">取消</button>
                    <button
                      onClick={handleSave}
                      disabled={saving || !form.content.trim()}
                      className="px-3 py-1 rounded text-xs font-medium bg-primary text-primary-foreground hover:opacity-90 transition-opacity disabled:opacity-50"
                    >
                      {saving ? '保存中...' : '保存'}
                    </button>
                  </div>
                </div>
              ) : (
                <div
                  key={item.id}
                  className="rounded-lg border border-border bg-card hover:border-border hover:shadow-sm transition-shadow group"
                >
                  <div className="flex items-start gap-3 px-4 py-3">
                    <span className="shrink-0 rounded px-1.5 py-0.5 text-[10px] font-medium bg-secondary text-muted-foreground">
                      {item.category || '未分类'}
                    </span>
                    <p className="flex-1 text-sm text-foreground leading-relaxed whitespace-pre-wrap">{item.content}</p>
                    <div className="shrink-0 flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                      <button
                        onClick={() => openEdit(item)}
                        className="p-1 rounded text-muted-foreground hover:text-foreground hover:bg-secondary transition-colors"
                        title="编辑"
                      >
                        <Pencil className="h-3.5 w-3.5" />
                      </button>
                      <button
                        onClick={() => handleDelete(item.id)}
                        className="p-1 rounded text-muted-foreground hover:text-destructive hover:bg-destructive/10 transition-colors"
                        title="删除"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  </div>
                </div>
              )
            })}

            {isCreating && (
              <div className="rounded-lg border border-dashed border-border bg-card/60 p-4">
                <div className="flex items-center justify-between mb-3">
                  <span className="text-xs font-semibold text-foreground">新建偏好</span>
                  <button onClick={closeForm} className="p-0.5 rounded text-muted-foreground hover:text-foreground">
                    <X className="h-3.5 w-3.5" />
                  </button>
                </div>
                {renderFormFields()}
                <div className="flex items-center gap-2 justify-end mt-3">
                  <button onClick={closeForm} className="px-3 py-1 rounded text-xs text-muted-foreground hover:text-foreground transition-colors">取消</button>
                  <button
                    onClick={handleSave}
                    disabled={saving || !form.content.trim()}
                    className="px-3 py-1 rounded text-xs font-medium bg-primary text-primary-foreground hover:opacity-90 transition-opacity disabled:opacity-50"
                  >
                    {saving ? '创建中...' : '创建'}
                  </button>
                </div>
              </div>
            )}
          </div>
        )}
      </section>
    )
  }

  function renderFormFields() {
    return (
      <div className="space-y-3">
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">分类</label>
          <input
            value={form.category}
            onChange={e => setForm(f => ({ ...f, category: e.target.value }))}
            placeholder="风格、对话、世界观..."
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          />
        </div>
        <div>
          <label className="text-xs font-medium text-muted-foreground mb-1 block">内容</label>
          <textarea
            value={form.content}
            onChange={e => setForm(f => ({ ...f, content: e.target.value }))}
            placeholder="偏好内容"
            rows={3}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring resize-y"
          />
        </div>
      </div>
    )
  }

  return (
    <main className="flex-1 min-w-0 overflow-y-auto overscroll-contain bg-background">
      {loading ? (
        <div className="flex h-full items-center justify-center text-sm text-muted-foreground">加载中...</div>
      ) : (
        <div className="max-w-3xl mx-auto px-5 py-6 space-y-8">
          {error && (
            <ErrorCallout
              message={error.message}
              diagnostic={error.diagnostic}
              onRetry={() => { void load() }}
              retrying={loading}
              onClose={() => setError(null)}
            />
          )}

          <div className="flex items-center gap-2">
            <Settings className="h-4 w-4 text-muted-foreground" />
            <h2 className="text-sm font-semibold text-foreground">
              创作偏好
              <span className="ml-2 text-xs font-normal text-muted-foreground">{global.length + novelPrefs.length} 条</span>
            </h2>
          </div>

          {renderSection('全局偏好 · 所有小说生效', global, true)}

          <div className="border-t border-border" />

          {renderSection('本书偏好 · 仅当前小说生效', novelPrefs, false)}
        </div>
      )}
    </main>
  )
}

function buildVisibleError(
  error: unknown,
  fallbackMessage: string,
  operation: string,
  bridgeMethod: string,
  detail: unknown,
): VisibleError {
  return {
    message: diagnosticMessage(error, fallbackMessage),
    diagnostic: buildCopyableDiagnostic({
      error,
      fallbackMessage,
      operation,
      bridgeMethod,
      detail,
    }),
  }
}
