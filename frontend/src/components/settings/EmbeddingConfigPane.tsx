import { AlertTriangle, CheckCircle2, Loader2 } from 'lucide-react'
import ErrorCallout from '@/components/shared/ErrorCallout'
import type { EmbeddingConfigView, SqliteVecStatusView } from '@/lib/novelist/api'
import type { diagnostics } from '@/lib/novelist/types'

const BUILTIN_ONNX_MODEL_ID = 'bge-small-zh-v1.5'
const BUILTIN_ONNX_DIMENSIONS = 512
const BUILTIN_ONNX_MAX_SEQUENCE_LENGTH = 512
const QWEN_ONNX_MODEL_ID = 'Qwen/Qwen3-Embedding-0.6B'
const QWEN_ONNX_DIMENSIONS = 1024
const QWEN_ONNX_MAX_SEQUENCE_LENGTH = 4096

interface Props {
  config: EmbeddingConfigView
  sqliteVecStatus: SqliteVecStatusView | null
  onUpdate: (patch: Partial<EmbeddingConfigView>) => void
  onTest: () => Promise<void>
  testing: boolean
  testResult?: { ok: boolean; msg?: string; diagnostic?: diagnostics.CopyableDiagnostic | null }
}

export default function EmbeddingConfigPane({
  config,
  sqliteVecStatus,
  onUpdate,
  onTest,
  testing,
  testResult,
}: Props) {
  const providerType = config.provider_type || 'api'
  const canTest = providerType === 'onnx'
    ? true
    : !!config.provider_key && !!config.endpoint_url && !!config.api_key && !!config.model_id
  const dimensions = config.dimensions ?? ''
  const isQwenProfile = config.model_id === QWEN_ONNX_MODEL_ID

  const selectOnnxProfile = (profile: 'default' | 'enhanced') => {
    const enhanced = profile === 'enhanced'
    onUpdate({
      provider_type: 'onnx',
      provider_key: 'onnx',
      endpoint_url: '',
      api_key: '',
      user: '',
      model_id: enhanced ? QWEN_ONNX_MODEL_ID : BUILTIN_ONNX_MODEL_ID,
      dimensions: enhanced ? QWEN_ONNX_DIMENSIONS : BUILTIN_ONNX_DIMENSIONS,
      onnx_model_path: '',
      onnx_vocab_path: '',
      max_sequence_length: enhanced ? QWEN_ONNX_MAX_SEQUENCE_LENGTH : BUILTIN_ONNX_MAX_SEQUENCE_LENGTH,
      normalize_embeddings: true,
    })
  }

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
        <label className="text-xs text-muted-foreground w-24 shrink-0">Provider</label>
        <div className="inline-flex h-8 rounded-md border overflow-hidden">
          {(['api', 'onnx'] as const).map(type => (
            <button
              key={type}
              onClick={() => onUpdate({
                provider_type: type,
                provider_key: type === 'onnx' ? 'onnx' : (config.provider_key || 'custom'),
                endpoint_url: type === 'onnx' ? '' : config.endpoint_url,
                api_key: type === 'onnx' ? '' : config.api_key,
                user: type === 'onnx' ? '' : config.user,
                model_id: type === 'onnx'
                  ? (config.model_id === QWEN_ONNX_MODEL_ID ? QWEN_ONNX_MODEL_ID : BUILTIN_ONNX_MODEL_ID)
                  : config.model_id,
                dimensions: type === 'onnx'
                  ? (config.model_id === QWEN_ONNX_MODEL_ID ? QWEN_ONNX_DIMENSIONS : BUILTIN_ONNX_DIMENSIONS)
                  : config.dimensions,
                max_sequence_length: type === 'onnx'
                  ? (config.model_id === QWEN_ONNX_MODEL_ID
                      ? QWEN_ONNX_MAX_SEQUENCE_LENGTH
                      : BUILTIN_ONNX_MAX_SEQUENCE_LENGTH)
                  : null,
                normalize_embeddings: true,
              })}
              className={`px-3 text-xs transition-colors ${
                providerType === type
                  ? 'bg-primary text-primary-foreground'
                  : 'bg-background hover:bg-muted/50'
              }`}
            >
              {type === 'api' ? 'API' : 'ONNX'}
            </button>
          ))}
        </div>
      </div>

      {providerType === 'api' && (
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
      )}

      {providerType === 'api' && (
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
      )}

      {providerType === 'api' && (
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
      )}

      {providerType === 'onnx' && (
        <div className="flex items-start gap-3">
          <label className="text-xs text-muted-foreground w-24 shrink-0 pt-2">模型档位</label>
          <div className="flex-1 min-w-0">
            <div className="inline-flex min-h-12 max-w-full rounded-md border overflow-hidden">
              <button
                type="button"
                aria-pressed={!isQwenProfile}
                onClick={() => selectOnnxProfile('default')}
                className={`min-w-36 px-3 py-1.5 text-left transition-colors ${
                  !isQwenProfile
                    ? 'bg-primary text-primary-foreground'
                    : 'bg-background hover:bg-muted/50'
                }`}
              >
                <span className="block text-xs font-medium">默认</span>
                <span className={`block text-[11px] ${!isQwenProfile ? 'opacity-80' : 'text-muted-foreground'}`}>
                  BGE Small · CPU
                </span>
              </button>
              <button
                type="button"
                aria-pressed={isQwenProfile}
                onClick={() => selectOnnxProfile('enhanced')}
                className={`min-w-40 border-l px-3 py-1.5 text-left transition-colors ${
                  isQwenProfile
                    ? 'bg-primary text-primary-foreground'
                    : 'bg-background hover:bg-muted/50'
                }`}
              >
                <span className="block text-xs font-medium">提高</span>
                <span className={`block text-[11px] ${isQwenProfile ? 'opacity-80' : 'text-muted-foreground'}`}>
                  Qwen3 0.6B · DirectML
                </span>
              </button>
            </div>
            <div className="mt-1.5 text-xs text-muted-foreground">
              {isQwenProfile
                ? `${QWEN_ONNX_DIMENSIONS} 维 · ${QWEN_ONNX_MAX_SEQUENCE_LENGTH} tokens · FP16`
                : `${BUILTIN_ONNX_DIMENSIONS} 维 · ${BUILTIN_ONNX_MAX_SEQUENCE_LENGTH} tokens · INT8`}
            </div>
          </div>
          <button
            onClick={onTest}
            disabled={!canTest || testing}
            className="h-8 px-2.5 rounded-md border text-xs shrink-0 hover:bg-muted/50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {testing ? <Loader2 className="w-3.5 h-3.5 animate-spin" /> : '测试'}
          </button>
        </div>
      )}

      {testResult?.ok && (
        <div className="text-xs pl-[7rem] text-emerald-600">
          ✓ 连通成功
        </div>
      )}
      {testResult && !testResult.ok && testResult.diagnostic && (
        <div className="pl-[7rem]">
          <ErrorCallout
            compact
            title="Embedding 连通性测试失败"
            message={testResult.msg || '连接失败'}
            diagnostic={testResult.diagnostic}
            className="rounded-md"
          />
        </div>
      )}
      {testResult && !testResult.ok && !testResult.diagnostic && (
        <div className="text-xs pl-[7rem] text-red-500">
          ✗ {testResult.msg || '连接失败'}
        </div>
      )}

      {providerType === 'api' && (
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
      )}

      {providerType === 'api' && (
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
      )}

      {providerType === 'api' && (
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
      )}
    </div>
  )
}
