import { useState } from 'react'
import { X, CheckCircle2, Loader2 } from 'lucide-react'
import type { llm } from '@/hooks/useApp'
import TemperatureInfo from './TemperatureInfo'
import ModelDiscoveryPanel from './ModelDiscoveryPanel'

interface Props {
  providers: llm.ProviderView[]
  onUpdate: (key: string, patch: Partial<llm.ProviderView>) => void
  onAddCustomModel: (providerKey: string, model: llm.ModelInfo) => void
  onRemoveCustomModel: (providerKey: string, modelId: string) => void
  onTest: (providerKey: string) => Promise<string | null>
  testResults: Record<string, { ok: boolean; msg?: string } | undefined>
  testing: Record<string, boolean>
}

export default function BuiltinProviderPane({ providers, onUpdate, onAddCustomModel, onRemoveCustomModel, onTest, testResults, testing }: Props) {
  const [selectedKey, setSelectedKey] = useState(providers[0]?.key || '')

  const provider = providers.find(p => p.key === selectedKey)
  if (!provider) {
    return <div className="text-sm text-muted-foreground p-4">暂无内置服务商</div>
  }

  const hasKey = !!provider.api_key
  const isTesting = testing[selectedKey]
  const testResult = testResults[selectedKey]

  const allExistingIds = new Set([
    ...(provider?.builtin_models || []).map(m => m.id),
    ...(provider?.custom_models || []).map(m => m.id),
  ])

  return (
    <div className="flex flex-col gap-4">
      {/* 服务商选择 + 状态 */}
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
        <span className={`flex items-center gap-1 text-xs shrink-0 ${hasKey ? 'text-emerald-600' : 'text-muted-foreground'}`}>
          {hasKey ? <><CheckCircle2 className="w-3.5 h-3.5" /> 已配置</> : '未配置'}
        </span>
      </div>

      {/* API Key + 测试 */}
      <div className="flex items-center gap-2">
        <label className="text-xs text-muted-foreground w-14 shrink-0">API Key</label>
        <input
          type="password"
          value={provider.api_key}
          onChange={e => onUpdate(selectedKey, { api_key: e.target.value })}
          placeholder="输入 API Key"
          className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
        <button
          onClick={() => onTest(selectedKey)}
          disabled={!provider.api_key || isTesting}
          className="h-8 px-2.5 rounded-md border text-xs shrink-0 hover:bg-muted/50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {isTesting ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : '测试'}
        </button>
      </div>

      {/* Chat URL */}
      <div className="flex items-center gap-3">
        <label className="text-xs text-muted-foreground w-14 shrink-0">Chat URL</label>
        <input
          value={provider.chat_url}
          onChange={e => onUpdate(selectedKey, { chat_url: e.target.value })}
          className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
      </div>

      {/* 测试结果 */}
      {testResult && (
        <div className={`text-xs pl-[4.5rem] ${testResult.ok ? 'text-emerald-600' : 'text-red-500'}`}>
          {testResult.ok ? '✓ 连通成功' : `✗ ${testResult.msg || '连接失败'}`}
        </div>
      )}

      {/* Temperature */}
      <div className="flex items-center gap-3">
        <label className="text-xs text-muted-foreground w-14 shrink-0 flex items-center gap-1">创意度<TemperatureInfo /></label>
        <input
          type="range"
          min="0"
          max="2"
          step="0.1"
          value={provider.temperature}
          onChange={e => onUpdate(selectedKey, { temperature: parseFloat(e.target.value) })}
          className="flex-1 h-8"
        />
        <span className="text-xs text-muted-foreground w-8 text-right">{(provider.temperature ?? 0.7).toFixed(1)}</span>
      </div>

      {/* 内置模型 */}
      {provider.builtin_models && provider.builtin_models.length > 0 && (
        <div>
          <div className="text-xs text-muted-foreground mb-2">内置模型</div>
          <div className="rounded-md border divide-y">
            {provider.builtin_models.map(m => (
              <div key={m.id} className="flex items-center justify-between px-3 py-2 bg-muted/30">
                <span className="text-sm">{m.name}</span>
                <span className="text-xs text-muted-foreground">
                  {m.context_window >= 1_000_000 ? (m.context_window / 1_000_000).toFixed(0) + 'M' : (m.context_window / 1_000).toFixed(0) + 'K'} 上下文
                  {m.max_output_tokens > 0 && <> · {(m.max_output_tokens / 1_000).toFixed(0)}K 输出</>}
                </span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* 自定义模型 */}
      {provider.custom_models && provider.custom_models.length > 0 && (
        <div>
          <span className="text-xs text-muted-foreground mb-2 block">自定义模型</span>
          <div className="rounded-md border divide-y">
            {provider.custom_models.map(m => (
              <div key={m.id} className="flex items-center justify-between px-3 py-2">
                <div>
                  <span className="text-sm">{m.name || m.id}</span>
                  {(m.context_window > 0 || m.max_output_tokens > 0) && (
                    <span className="text-xs text-muted-foreground ml-2">
                      {m.context_window > 0 && (m.context_window >= 1_000_000 ? (m.context_window / 1_000_000).toFixed(0) + 'M' : (m.context_window / 1_000).toFixed(0) + 'K')}
                      {m.max_output_tokens > 0 && <> · {(m.max_output_tokens / 1_000).toFixed(0)}K 输出</>}
                      {m.supports_thinking ? <> · 思考</> : null}
                      {m.reasoning_levels?.length ? <> · 等级: {m.reasoning_levels.join(',')}</> : null}
                      {m.supports_vision ? <> · 视觉</> : null}
                    </span>
                  )}
                </div>
                <button
                  onClick={() => onRemoveCustomModel(selectedKey, m.id)}
                  className="text-muted-foreground hover:text-red-500 transition-colors"
                >
                  <X className="w-3.5 h-3.5" />
                </button>
              </div>
            ))}
          </div>
        </div>
      )}

      <ModelDiscoveryPanel
        key={selectedKey}
        chatUrl={provider.chat_url}
        apiKey={provider.api_key}
        existingIds={allExistingIds}
        onAddModel={m => onAddCustomModel(selectedKey, m)}
      />
    </div>
  )
}
