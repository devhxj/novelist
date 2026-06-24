import { useState } from 'react'
import { X } from 'lucide-react'
import type { llm } from '@/hooks/useApp'

interface Props {
  model: llm.ModelInfo
  onChange: (patch: Partial<llm.ModelInfo>) => void
  onSave: () => void
  onCancel: () => void
  title?: string
}

export default function ModelEditForm({ model, onChange, onSave, onCancel, title }: Props) {
  const [error, setError] = useState('')

  const handleSave = () => {
    if (!model.id.trim()) { setError('模型 ID 不能为空'); return }
    if (!model.name.trim()) { setError('名称不能为空'); return }
    if (!model.context_window || model.context_window <= 0) { setError('上下文窗口必须大于 0'); return }
    if (!model.max_output_tokens || model.max_output_tokens <= 0) { setError('最大输出必须大于 0'); return }
    setError('')
    onSave()
  }

  const handleChange = (patch: Partial<llm.ModelInfo>) => {
    setError('')
    onChange(patch)
  }

  return (
    <div className="border rounded-md p-3 space-y-2">
      {title && (
        <div className="flex items-center justify-between">
          <span className="text-xs font-medium">{title}</span>
          <button onClick={onCancel} className="text-muted-foreground hover:text-red-500">
            <X className="w-3.5 h-3.5" />
          </button>
        </div>
      )}
      <div>
        <label className="text-xs text-muted-foreground mb-0.5 block">模型 ID</label>
        <input value={model.id} onChange={e => handleChange({ id: e.target.value })}
          className="w-full h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50" />
      </div>
      <div>
        <label className="text-xs text-muted-foreground mb-0.5 block">名称</label>
        <input value={model.name} onChange={e => handleChange({ name: e.target.value })}
          className="w-full h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50" />
      </div>
      <div className="grid grid-cols-2 gap-2">
        <div>
          <label className="text-xs text-muted-foreground mb-0.5 block">上下文窗口 (tokens)</label>
          <input type="number" value={model.context_window || ''} onChange={e => handleChange({ context_window: parseInt(e.target.value, 10) || 0 })}
            className="w-full h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50" />
        </div>
        <div>
          <label className="text-xs text-muted-foreground mb-0.5 block">最大输出 (tokens)</label>
          <input type="number" value={model.max_output_tokens || ''} onChange={e => handleChange({ max_output_tokens: parseInt(e.target.value, 10) || 0 })}
            className="w-full h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50" />
        </div>
      </div>
      <div className="text-xs text-muted-foreground">以上为默认值，请以服务商文档为准</div>
      <div className="flex items-center gap-3">
        <label className="flex items-center gap-1 text-xs">
          <input type="checkbox" checked={model.supports_thinking} onChange={e => handleChange({ supports_thinking: e.target.checked })}
            className="rounded" />
          支持深度思考
        </label>
        <label className="flex items-center gap-1 text-xs">
          <input type="checkbox" checked={model.supports_vision} onChange={e => handleChange({ supports_vision: e.target.checked })}
            className="rounded" />
          视觉
        </label>
      </div>
      {error && <div className="text-xs text-red-500">{error}</div>}
      <div className="flex justify-end gap-2 pt-1">
        <button onClick={onCancel} className="h-8 px-3 rounded-md border text-xs text-muted-foreground">取消</button>
        <button onClick={handleSave} className="h-8 px-3 rounded-md bg-primary text-primary-foreground text-xs">确认</button>
      </div>
    </div>
  )
}
