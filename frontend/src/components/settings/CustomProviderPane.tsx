import { useState } from 'react'
import { Plus, X } from 'lucide-react'
import type { llm } from '@/hooks/useApp'

interface Props {
  providers: llm.ProviderView[]
  onAdd: (provider: llm.ProviderView) => void
  onUpdate: (key: string, patch: Partial<llm.ProviderView>) => void
  onRemove: (key: string) => void
  onAddCustomModel: (providerKey: string, model: llm.ModelInfo) => void
  onRemoveCustomModel: (providerKey: string, modelId: string) => void
}

export default function CustomProviderPane({ providers, onAdd, onUpdate, onRemove, onAddCustomModel, onRemoveCustomModel }: Props) {
  const [selectedKey, setSelectedKey] = useState(providers[0]?.key || '')
  const [showNewForm, setShowNewForm] = useState(false)
  const [newName, setNewName] = useState('')
  const [newChatURL, setNewChatURL] = useState('')
  const [newApiKey, setNewApiKey] = useState('')
  const [showAddModel, setShowAddModel] = useState(false)
  const [newModelId, setNewModelId] = useState('')
  const [newModelName, setNewModelName] = useState('')

  const provider = providers.find(p => p.key === selectedKey)

  const handleAdd = () => {
    if (!newName.trim() || !newChatURL.trim()) return
    onAdd({
      key: newName.trim().toLowerCase().replace(/\s+/g, '-'),
      name: newName.trim(),
      chat_url: newChatURL.trim(),
      api_key: newApiKey,
      source: 'custom',
      builtin_models: [],
      custom_models: [],
    } as unknown as llm.ProviderView)
    setNewName('')
    setNewChatURL('')
    setNewApiKey('')
    setShowNewForm(false)
    // 选中新加的 provider
    setSelectedKey(newName.trim().toLowerCase().replace(/\s+/g, '-'))
  }

  const handleAddModel = () => {
    if (!selectedKey || !newModelId.trim() || !newModelName.trim()) return
    onAddCustomModel(selectedKey, {
      id: newModelId.trim(),
      name: newModelName.trim(),
      context_window: 0,
      max_output_tokens: 0,
      reasoning_levels: [],
      supports_vision: false,
    } as unknown as llm.ModelInfo)
    setNewModelId('')
    setNewModelName('')
    setShowAddModel(false)
  }

  // 无自定义服务商且未展开新建表单
  if (providers.length === 0 && !showNewForm) {
    return (
      <div className="flex flex-col items-center gap-3 py-8">
        <p className="text-sm text-muted-foreground">暂无自定义服务商</p>
        <button
          onClick={() => setShowNewForm(true)}
          className="flex items-center gap-1 text-xs text-primary hover:underline"
        >
          <Plus className="w-3 h-3" /> 添加自定义服务商
        </button>
      </div>
    )
  }

  return (
    <div className="flex flex-col gap-4">
      {/* 服务商选择 + 添加 */}
      <div className="flex items-center gap-3">
        <label className="text-xs text-muted-foreground w-14 shrink-0">服务商</label>
        <select
          value={selectedKey}
          onChange={e => setSelectedKey(e.target.value)}
          className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        >
          {providers.map(p => (
            <option key={p.key} value={p.key}>{p.name}</option>
          ))}
        </select>
        <button
          onClick={() => setShowNewForm(!showNewForm)}
          className="text-xs text-primary flex items-center gap-0.5 hover:underline shrink-0"
        >
          <Plus className="w-3 h-3" /> 添加
        </button>
      </div>

      {/* 新建表单 */}
      {showNewForm && (
        <div className="border rounded-md p-3 space-y-3">
          <div className="text-xs font-medium">新建自定义服务商</div>

          <div className="flex items-center gap-3">
            <label className="text-xs text-muted-foreground w-16 shrink-0">名称</label>
            <input
              value={newName}
              onChange={e => setNewName(e.target.value)}
              placeholder="服务商名称"
              className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
            />
          </div>

          <div className="flex items-center gap-3">
            <label className="text-xs text-muted-foreground w-16 shrink-0">Chat URL</label>
            <input
              value={newChatURL}
              onChange={e => setNewChatURL(e.target.value)}
              placeholder="https://api.example.com/v1/chat/completions"
              className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
            />
          </div>

          <div className="flex items-center gap-3">
            <label className="text-xs text-muted-foreground w-16 shrink-0">API Key</label>
            <input
              type="password"
              value={newApiKey}
              onChange={e => setNewApiKey(e.target.value)}
              placeholder="输入 API Key"
              className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
            />
          </div>

          <div className="flex items-center gap-2 pt-1">
            <div className="flex-1" />
            <button onClick={() => setShowNewForm(false)} className="h-8 px-3 rounded-md border text-xs text-muted-foreground">
              取消
            </button>
            <button onClick={handleAdd} className="h-8 px-3 rounded-md bg-primary text-primary-foreground text-xs">
              添加
            </button>
          </div>
        </div>
      )}

      {/* 选中已有服务商时的编辑区 */}
      {provider && !showNewForm && (
        <>
          <div className="flex items-center gap-3">
            <label className="text-xs text-muted-foreground w-16 shrink-0">名称</label>
            <input
              value={provider.name}
              disabled
              className="flex-1 h-8 rounded-md border bg-muted/50 px-2.5 text-sm text-muted-foreground"
            />
          </div>

          <div className="flex items-center gap-3">
            <label className="text-xs text-muted-foreground w-16 shrink-0">Chat URL</label>
            <input
              value={provider.chat_url}
              onChange={e => onUpdate(selectedKey, { chat_url: e.target.value })}
              className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
            />
          </div>

          <div className="flex items-center gap-3">
            <label className="text-xs text-muted-foreground w-16 shrink-0">API Key</label>
            <input
              type="password"
              value={provider.api_key}
              onChange={e => onUpdate(selectedKey, { api_key: e.target.value })}
              placeholder="输入 API Key"
              className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
            />
          </div>

          <div className="flex items-center gap-3">
            <label className="text-xs text-muted-foreground w-16 shrink-0">创意度</label>
            <input
              type="range"
              min="0"
              max="2"
              step="0.1"
              value={provider.temperature}
              onChange={e => onUpdate(selectedKey, { temperature: parseFloat(e.target.value) })}
              className="flex-1 h-8"
            />
            <span className="text-xs text-muted-foreground w-8 text-right">{provider.temperature.toFixed(1)}</span>
          </div>

          {/* 模型列表 */}
          <div>
            <div className="flex items-center justify-between mb-2">
              <span className="text-xs text-muted-foreground">模型列表</span>
              <button
                onClick={() => setShowAddModel(!showAddModel)}
                className="text-xs text-primary flex items-center gap-0.5 hover:underline"
              >
                <Plus className="w-3 h-3" /> 添加
              </button>
            </div>

            {provider.custom_models && provider.custom_models.length > 0 && (
              <div className="rounded-md border divide-y mb-2">
                {provider.custom_models.map(m => (
                  <div key={m.id} className="flex items-center justify-between px-3 py-2">
                    <span className="text-sm">{m.name || m.id}</span>
                    <button
                      onClick={() => onRemoveCustomModel(selectedKey, m.id)}
                      className="text-muted-foreground hover:text-red-500 transition-colors"
                    >
                      <X className="w-3.5 h-3.5" />
                    </button>
                  </div>
                ))}
              </div>
            )}

            {showAddModel && (
              <div className="flex items-center gap-2">
                <input
                  value={newModelId}
                  onChange={e => setNewModelId(e.target.value)}
                  placeholder="模型 ID"
                  className="flex-1 h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
                />
                <input
                  value={newModelName}
                  onChange={e => setNewModelName(e.target.value)}
                  placeholder="名称"
                  className="flex-1 h-8 rounded-md border bg-background px-2.5 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
                />
                <button onClick={handleAddModel} className="h-8 px-3 rounded-md bg-primary text-primary-foreground text-xs shrink-0">
                  确认
                </button>
              </div>
            )}
          </div>

          {/* 删除 */}
          <div className="flex pt-1">
            <button
              onClick={() => {
                const name = provider?.name || selectedKey
                if (!window.confirm(`确定删除服务商 "${name}"？此操作无法撤销。`)) return
                onRemove(selectedKey)
                setSelectedKey(providers.filter(p => p.key !== selectedKey)[0]?.key || '')
              }}
              className="h-8 px-3 rounded-md border border-red-200 text-red-500 text-xs hover:bg-red-50 transition-colors"
            >
              删除服务商
            </button>
          </div>
        </>
      )}
    </div>
  )
}
