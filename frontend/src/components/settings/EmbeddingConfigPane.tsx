import { AlertTriangle, CheckCircle2, Loader2 } from 'lucide-react'
import type { EmbeddingConfigView, SqliteVecStatusView } from '@/lib/novelist/api'

interface Props {
  config: EmbeddingConfigView
  sqliteVecStatus: SqliteVecStatusView | null
  onUpdate: (patch: Partial<EmbeddingConfigView>) => void
  onTest: () => Promise<void>
  testing: boolean
  testResult?: { ok: boolean; msg?: string }
}

export default function EmbeddingConfigPane({
  config,
  sqliteVecStatus,
  onUpdate,
  onTest,
  testing,
  testResult,
}: Props) {
  const canTest = !!config.provider_key && !!config.endpoint_url && !!config.api_key && !!config.model_id
  const dimensions = config.dimensions ?? ''

  return (
    <div className="flex flex-col gap-4">
      <div className="rounded-md border px-3 py-2">
        {sqliteVecStatus?.available ? (
          <div className="flex items-center gap-2 text-xs text-emerald-600">
            <CheckCircle2 className="w-3.5 h-3.5 shrink-0" />
            <span>sqlite-vec 已就绪</span>
            <span className="text-muted-foreground">{sqliteVecStatus.runtime_identifier}</span>
            {sqliteVecStatus.file_name && <span className="text-muted-foreground">{sqliteVecStatus.file_name}</span>}
          </div>
        ) : (
          <div className="flex flex-col gap-1">
            <div className="flex items-center gap-2 text-xs text-amber-600">
              <AlertTriangle className="w-3.5 h-3.5 shrink-0" />
              <span>sqlite-vec 未就绪</span>
              {sqliteVecStatus?.runtime_identifier && (
                <span className="text-muted-foreground">{sqliteVecStatus.runtime_identifier}</span>
              )}
            </div>
            {sqliteVecStatus?.error && (
              <div className="text-xs text-muted-foreground pl-5">{sqliteVecStatus.error}</div>
            )}
          </div>
        )}
      </div>

      <div className="flex items-center gap-3">
        <label htmlFor="embedding-provider" className="text-xs text-muted-foreground w-24 shrink-0">服务商 Key</label>
        <input
          id="embedding-provider"
          value={config.provider_key}
          onChange={e => onUpdate({ provider_key: e.target.value })}
          placeholder="custom"
          className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
      </div>

      <div className="flex items-center gap-3">
        <label htmlFor="embedding-url" className="text-xs text-muted-foreground w-24 shrink-0">Endpoint URL</label>
        <input
          id="embedding-url"
          value={config.endpoint_url}
          onChange={e => onUpdate({ endpoint_url: e.target.value })}
          placeholder="https://api.example.com/v1/embeddings"
          className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
      </div>

      <div className="flex items-center gap-2">
        <label htmlFor="embedding-api-key" className="text-xs text-muted-foreground w-24 shrink-0">API Key</label>
        <input
          id="embedding-api-key"
          type="password"
          value={config.api_key}
          onChange={e => onUpdate({ api_key: e.target.value })}
          placeholder="输入 Embeddings API Key"
          className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
        <button
          onClick={onTest}
          disabled={!canTest || testing}
          className="h-8 px-2.5 rounded-md border text-xs shrink-0 hover:bg-muted/50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
        >
          {testing ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : '测试'}
        </button>
      </div>

      {testResult && (
        <div className={`text-xs pl-[7rem] ${testResult.ok ? 'text-emerald-600' : 'text-red-500'}`}>
          {testResult.ok ? '✓ 连通成功' : `✗ ${testResult.msg || '连接失败'}`}
        </div>
      )}

      <div className="flex items-center gap-3">
        <label htmlFor="embedding-model" className="text-xs text-muted-foreground w-24 shrink-0">模型 ID</label>
        <input
          id="embedding-model"
          value={config.model_id}
          onChange={e => onUpdate({ model_id: e.target.value })}
          placeholder="text-embedding-3-small"
          className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
      </div>

      <div className="flex items-center gap-3">
        <label htmlFor="embedding-dimensions" className="text-xs text-muted-foreground w-24 shrink-0">向量维度</label>
        <input
          id="embedding-dimensions"
          type="number"
          min="1"
          step="1"
          value={dimensions}
          onChange={e => {
            const value = e.target.value.trim()
            onUpdate({ dimensions: value ? Number(value) : null })
          }}
          placeholder="自动"
          className="w-36 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
        <span className="text-xs text-muted-foreground">留空使用服务商默认维度</span>
      </div>

      <div className="flex items-center gap-3">
        <label htmlFor="embedding-user" className="text-xs text-muted-foreground w-24 shrink-0">User</label>
        <input
          id="embedding-user"
          value={config.user}
          onChange={e => onUpdate({ user: e.target.value })}
          placeholder="可选"
          className="flex-1 h-8 rounded-md border bg-background px-2.5 text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
      </div>
    </div>
  )
}
