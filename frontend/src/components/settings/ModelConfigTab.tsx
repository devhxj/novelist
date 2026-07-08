import { useState, useEffect, useCallback, useRef } from 'react'
import { Loader2 } from 'lucide-react'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { useApp } from '@/hooks/useApp'
import type { llm } from '@/hooks/useApp'
import type { EmbeddingConfigView, SqliteVecStatusView } from '@/lib/novelist/api'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics } from '@/lib/novelist/types'
import BuiltinProviderPane from './BuiltinProviderPane'
import CustomProviderPane from './CustomProviderPane'
import EmbeddingConfigPane from './EmbeddingConfigPane'

type SubNav = 'builtin' | 'custom' | 'embedding'

interface Props {
  onSaved?: () => void
}

type DiagnosticFeedback = {
  title: string
  message: string
  diagnostic: diagnostics.CopyableDiagnostic | null
}

type SaveFeedback =
  | { kind: 'success' | 'validation' | 'progress'; message: string }
  | ({ kind: 'error' } & DiagnosticFeedback)

type TestResult = {
  ok: boolean
  msg?: string
  configSnapshot: string
  diagnostic?: diagnostics.CopyableDiagnostic | null
}

type EmbeddingTestResult = {
  ok: boolean
  msg?: string
  diagnostic?: diagnostics.CopyableDiagnostic | null
}

const emptyEmbeddingConfig = (): EmbeddingConfigView => ({
  provider_type: '',
  provider_key: '',
  endpoint_url: '',
  api_key: '',
  model_id: '',
  dimensions: null,
  user: '',
  onnx_model_path: '',
  onnx_vocab_path: '',
  max_sequence_length: null,
  normalize_embeddings: true,
})

const firstModelId = (provider: llm.ProviderView): string => {
  const models = provider.builtin_models?.length ? provider.builtin_models : provider.custom_models
  return models?.[0]?.id ?? ''
}

const buildTestSnapshot = (provider: llm.ProviderView): string => JSON.stringify({
  api_key: provider.api_key,
  base_url: (provider.base_url || provider.chat_url || '').trim(),
  endpoint_type: provider.endpoint_type || 'chat',
  model_id: firstModelId(provider),
})

export default function ModelConfigTab({ onSaved }: Props) {
  const app = useApp()
  const [providers, setProviders] = useState<llm.ProviderView[]>([])
  const [embeddingConfig, setEmbeddingConfig] = useState<EmbeddingConfigView>(emptyEmbeddingConfig())
  const [sqliteVecStatus, setSqliteVecStatus] = useState<SqliteVecStatusView | null>(null)
  const [subNav, setSubNav] = useState<SubNav>('builtin')
  const [isLoading, setIsLoading] = useState(true)
  const [isSaving, setIsSaving] = useState(false)
  const [loadError, setLoadError] = useState<DiagnosticFeedback | null>(null)
  const [saveFeedback, setSaveFeedback] = useState<SaveFeedback | null>(null)

  // 测试状态：{ providerKey: { ok, msg, apiKey } }
  const [testResults, setTestResults] = useState<Record<string, TestResult | undefined>>({})
  const [testing, setTesting] = useState<Record<string, boolean>>({})
  const [embeddingTestResult, setEmbeddingTestResult] = useState<EmbeddingTestResult | undefined>()
  const [embeddingTesting, setEmbeddingTesting] = useState(false)
  // 保存过后的配置哈希，用于判断 key 是否被修改
  const savedKeysRef = useRef<Record<string, string>>({})

  useEffect(() => {
    let cancelled = false
    Promise.allSettled([
      app.GetLLMConfig(),
      app.GetEmbeddingConfig(),
      app.GetSqliteVecStatus(),
    ]).then((results) => {
      if (cancelled) return
      const [configResult, embeddingResult, sqliteVecResult] = results
      const failed: Array<{ method: string; reason: unknown }> = []

      if (configResult.status === 'fulfilled') {
        const config = configResult.value
        setProviders(config.providers)
        const keys: Record<string, string> = {}
        for (const p of config.providers) {
          if (p.api_key) keys[p.key] = p.api_key
        }
        savedKeysRef.current = keys
      } else {
        failed.push({ method: 'GetLLMConfig', reason: configResult.reason })
      }

      if (embeddingResult.status === 'fulfilled') {
        setEmbeddingConfig(embeddingResult.value ?? emptyEmbeddingConfig())
      } else {
        failed.push({ method: 'GetEmbeddingConfig', reason: embeddingResult.reason })
      }

      if (sqliteVecResult.status === 'fulfilled') {
        setSqliteVecStatus(sqliteVecResult.value ?? null)
      } else {
        failed.push({ method: 'GetSqliteVecStatus', reason: sqliteVecResult.reason })
      }

      if (failed.length > 0) {
        const firstFailure = failed[0]
        setLoadError(diagnosticFeedback({
          error: firstFailure.reason,
          fallbackMessage: '模型配置加载失败',
          operation: 'LoadModelSettings',
          bridgeMethod: firstFailure.method,
          detail: {
            failed_methods: failed.map(item => item.method),
            failure_count: failed.length,
          },
        }))
      } else {
        setLoadError(null)
      }
    }).finally(() => {
      if (!cancelled) setIsLoading(false)
    })
    return () => { cancelled = true }
  }, [app])

  const builtinProviders = providers.filter(p => p.source === 'builtin')
  const customProviders = providers.filter(p => p.source === 'custom')

  const handleUpdateProvider = useCallback((key: string, patch: Partial<llm.ProviderView>) => {
    setProviders(prev => prev.map(p => p.key === key ? { ...p, ...patch } as unknown as llm.ProviderView : p))
    // 会影响连通性请求的字段变了，就清除旧测试结果。
    if ('api_key' in patch || 'base_url' in patch || 'chat_url' in patch || 'endpoint_type' in patch) {
      setTestResults(prev => {
        const next = { ...prev }
        delete next[key]
        return next
      })
    }
  }, [])

  const handleUpdateEmbeddingConfig = useCallback((patch: Partial<EmbeddingConfigView>) => {
    setEmbeddingConfig(prev => ({ ...prev, ...patch }))
    setEmbeddingTestResult(undefined)
  }, [])

  const handleAddCustomProvider = useCallback((provider: llm.ProviderView) => {
    setProviders(prev => [...prev, provider])
  }, [])

  const handleRemoveCustomProvider = useCallback((key: string) => {
    setProviders(prev => prev.filter(p => p.key !== key))
  }, [])

  const handleAddCustomModel = useCallback((providerKey: string, model: llm.ModelInfo) => {
    setProviders(prev => prev.map(p => {
      if (p.key !== providerKey) return p
      const models = [...(p.custom_models || []), model]
      return { ...p, custom_models: models } as unknown as llm.ProviderView
    }))
    setTestResults(prev => {
      const next = { ...prev }
      delete next[providerKey]
      return next
    })
  }, [])

  const handleRemoveCustomModel = useCallback((providerKey: string, modelId: string) => {
    setProviders(prev => prev.map(p => {
      if (p.key !== providerKey) return p
      const models = (p.custom_models || []).filter(m => m.id !== modelId)
      return { ...p, custom_models: models } as unknown as llm.ProviderView
    }))
    setTestResults(prev => {
      const next = { ...prev }
      delete next[providerKey]
      return next
    })
  }, [])

  // 测试连通性，返回错误消息或 null
  const handleTest = useCallback(async (providerKey: string): Promise<string | null> => {
    const provider = providers.find(p => p.key === providerKey)
    if (!provider || !provider.api_key) {
      const msg = '未配置 API Key'
      setTestResults(prev => ({ ...prev, [providerKey]: { ok: false, msg, configSnapshot: '' } }))
      return msg
    }

    const baseURL = (provider.base_url || provider.chat_url || '').trim()
    if (!baseURL) {
      const msg = '请先配置 Base URL'
      setTestResults(prev => ({ ...prev, [providerKey]: { ok: false, msg, configSnapshot: '' } }))
      return msg
    }

    const modelId = firstModelId(provider)
    if (!modelId) {
      const msg = '请先添加至少一个模型'
      setTestResults(prev => ({ ...prev, [providerKey]: { ok: false, msg, configSnapshot: '' } }))
      return msg
    }

    const configSnapshot = buildTestSnapshot(provider)
    setTesting(prev => ({ ...prev, [providerKey]: true }))
    try {
      await app.TestConnection({
        provider_name: providerKey,
        base_url: baseURL,
        endpoint_type: provider.endpoint_type || 'chat',
        chat_url: '',
        api_key: provider.api_key,
        model_id: modelId,
      })
      setTestResults(prev => ({ ...prev, [providerKey]: { ok: true, configSnapshot } }))
      return null
    } catch (err: unknown) {
      const msg = diagnosticMessage(err, '模型连通性测试失败').replace(/^app: test connection: /, '')
      const diagnostic = buildCopyableDiagnostic({
        error: err,
        fallbackMessage: '模型连通性测试失败',
        operation: 'TestConnection',
        bridgeMethod: 'TestConnection',
        detail: providerDiagnosticDetail(provider, modelId),
      })
      setTestResults(prev => ({ ...prev, [providerKey]: { ok: false, msg, configSnapshot, diagnostic } }))
      return msg
    } finally {
      setTesting(prev => ({ ...prev, [providerKey]: false }))
    }
  }, [providers, app])

  const handleTestEmbedding = useCallback(async () => {
    setEmbeddingTesting(true)
    setEmbeddingTestResult(undefined)
    try {
      await app.TestEmbeddingConnection(embeddingConfig)
      setEmbeddingTestResult({ ok: true })
    } catch (err) {
      setEmbeddingTestResult({
        ok: false,
        msg: diagnosticMessage(err, 'Embedding 连通性测试失败'),
        diagnostic: buildCopyableDiagnostic({
          error: err,
          fallbackMessage: 'Embedding 连通性测试失败',
          operation: 'TestEmbeddingConnection',
          bridgeMethod: 'TestEmbeddingConnection',
          detail: embeddingDiagnosticDetail(embeddingConfig),
        }),
      })
    } finally {
      setEmbeddingTesting(false)
    }
  }, [app, embeddingConfig])

  const setTemporarySaveFeedback = useCallback((feedback: SaveFeedback, durationMs = 2000) => {
    setSaveFeedback(feedback)
    window.setTimeout(() => {
      setSaveFeedback(current => current?.message === feedback.message ? null : current)
    }, durationMs)
  }, [])

  const handleSaveEmbedding = useCallback(async () => {
    setIsSaving(true)
    setSaveFeedback(null)
    try {
      await app.SaveEmbeddingConfig(embeddingConfig)
      const [saved, sqliteVec] = await Promise.all([
        app.GetEmbeddingConfig(),
        app.GetSqliteVecStatus(),
      ])
      setEmbeddingConfig(saved ?? emptyEmbeddingConfig())
      setSqliteVecStatus(sqliteVec ?? null)
      setTemporarySaveFeedback({ kind: 'success', message: '配置已保存' })
      onSaved?.()
    } catch (err) {
      setSaveFeedback({
        kind: 'error',
        ...diagnosticFeedback({
          error: err,
          fallbackMessage: 'Embedding 配置保存失败',
          operation: 'SaveEmbeddingConfig',
          bridgeMethod: 'SaveEmbeddingConfig',
          detail: embeddingDiagnosticDetail(embeddingConfig),
        }),
      })
    } finally {
      setIsSaving(false)
    }
  }, [app, embeddingConfig, onSaved, setTemporarySaveFeedback])

  const handleSave = useCallback(async () => {
    if (subNav === 'embedding') {
      await handleSaveEmbedding()
      return
    }

    // 收集有 key 的 provider
    const withKey = providers.filter(p => p.api_key)
    if (withKey.length === 0) {
      setTemporarySaveFeedback({ kind: 'validation', message: '请先配置至少一个服务商的 API Key' }, 3000)
      return
    }

    // 找出需要测试的：从未测试过，或 key 跟上次测试/保存时不一致
    const needTest = withKey.filter(p => {
      const tr = testResults[p.key]
      if (!tr || !tr.ok) return true // 从未测试或上次失败
      if (tr.configSnapshot !== buildTestSnapshot(p)) return true // 请求配置变了
      return false
    })

    if (needTest.length > 0) {
      setSaveFeedback({ kind: 'progress', message: '正在测试连通性...' })
      for (const p of needTest) {
        const err = await handleTest(p.key)
        if (err) {
          setTemporarySaveFeedback({ kind: 'validation', message: `${p.name} 连通性测试失败: ${err}` }, 5000)
          return
        }
      }
    }

    setIsSaving(true)
    setSaveFeedback(null)
    try {
      await app.SaveLLMConfig({ providers } as unknown as llm.LLMConfigView)
      const keys: Record<string, string> = {}
      for (const p of providers) {
        if (p.api_key) keys[p.key] = p.api_key
      }
      savedKeysRef.current = keys
      setTemporarySaveFeedback({ kind: 'success', message: '配置已保存' })
      onSaved?.()
    } catch (err) {
      setSaveFeedback({
        kind: 'error',
        ...diagnosticFeedback({
          error: err,
          fallbackMessage: '模型配置保存失败',
          operation: 'SaveLLMConfig',
          bridgeMethod: 'SaveLLMConfig',
          detail: {
            provider_count: providers.length,
            configured_provider_keys: providers.filter(provider => provider.api_key).map(provider => provider.key),
            custom_provider_count: providers.filter(provider => provider.source === 'custom').length,
          },
        }),
      })
    } finally {
      setIsSaving(false)
    }
  }, [providers, app, testResults, handleTest, subNav, handleSaveEmbedding, onSaved, setTemporarySaveFeedback])

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-full">
        <Loader2 className="w-5 h-5 animate-spin text-muted-foreground" />
      </div>
    )
  }

  return (
    <div className="flex flex-col h-full">
      {/* 子导航 */}
      <div className="flex gap-6 px-1 mb-4">
        <button
          onClick={() => setSubNav('builtin')}
          className={`text-sm pb-1 transition-colors ${
            subNav === 'builtin'
              ? 'text-foreground border-b-2 border-primary font-medium'
              : 'text-muted-foreground hover:text-foreground'
          }`}
        >
          内置服务商
        </button>
        <button
          onClick={() => setSubNav('custom')}
          className={`text-sm pb-1 transition-colors ${
            subNav === 'custom'
              ? 'text-foreground border-b-2 border-primary font-medium'
              : 'text-muted-foreground hover:text-foreground'
          }`}
        >
          自定义服务商
        </button>
        <button
          onClick={() => setSubNav('embedding')}
          className={`text-sm pb-1 transition-colors ${
            subNav === 'embedding'
              ? 'text-foreground border-b-2 border-primary font-medium'
              : 'text-muted-foreground hover:text-foreground'
          }`}
        >
          Embeddings
        </button>
      </div>

      {/* 内容区 */}
      <div className="flex-1 overflow-y-auto">
        {loadError && (
          <ErrorCallout
            compact
            title={loadError.title}
            message={loadError.message}
            diagnostic={loadError.diagnostic}
            onClose={() => setLoadError(null)}
            className="mb-3 rounded-md"
          />
        )}
        {subNav === 'builtin' ? (
          <BuiltinProviderPane
            providers={builtinProviders}
            onUpdate={handleUpdateProvider}
            onAddCustomModel={handleAddCustomModel}
            onRemoveCustomModel={handleRemoveCustomModel}
            onTest={handleTest}
            testResults={testResults}
            testing={testing}
          />
        ) : subNav === 'custom' ? (
          <CustomProviderPane
            providers={customProviders}
            onAdd={handleAddCustomProvider}
            onUpdate={handleUpdateProvider}
            onRemove={handleRemoveCustomProvider}
            onAddCustomModel={handleAddCustomModel}
            onRemoveCustomModel={handleRemoveCustomModel}
            onTest={handleTest}
            testResults={testResults}
            testing={testing}
          />
        ) : (
          <EmbeddingConfigPane
            config={embeddingConfig}
            sqliteVecStatus={sqliteVecStatus}
            onUpdate={handleUpdateEmbeddingConfig}
            onTest={handleTestEmbedding}
            testing={embeddingTesting}
            testResult={embeddingTestResult}
          />
        )}
      </div>

      {/* 底部保存栏 */}
      <div className="space-y-2 pt-4 border-t mt-4">
        {saveFeedback?.kind === 'error' && (
          <ErrorCallout
            compact
            title={saveFeedback.title}
            message={saveFeedback.message}
            diagnostic={saveFeedback.diagnostic}
            onClose={() => setSaveFeedback(null)}
            className="rounded-md"
          />
        )}
        <div className="flex items-center justify-end gap-3">
          {saveFeedback && saveFeedback.kind !== 'error' && (
            <span
              role={saveFeedback.kind === 'success' ? 'status' : undefined}
              className={`text-xs ${saveFeedback.kind === 'success' ? 'text-emerald-600' : 'text-red-500'}`}
            >
              {saveFeedback.message}
            </span>
          )}
          <button
            onClick={handleSave}
            disabled={isSaving}
            className="h-8 px-4 rounded-md bg-primary text-primary-foreground text-sm disabled:opacity-50"
          >
            {isSaving ? '保存中...' : '保存配置'}
          </button>
        </div>
      </div>
    </div>
  )

}

function diagnosticFeedback({
  error,
  fallbackMessage,
  operation,
  bridgeMethod,
  detail,
}: {
  error: unknown
  fallbackMessage: string
  operation: string
  bridgeMethod: string | null
  detail: Record<string, unknown>
}): DiagnosticFeedback {
  return {
    title: fallbackMessage,
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

function providerDiagnosticDetail(provider: llm.ProviderView, modelId: string) {
  return {
    provider_key: provider.key,
    provider_name: provider.name,
    source: provider.source,
    base_url: (provider.base_url || provider.chat_url || '').trim(),
    endpoint_type: provider.endpoint_type || 'chat',
    model_id: modelId,
    has_api_key: Boolean(provider.api_key),
    custom_model_count: provider.custom_models?.length ?? 0,
  }
}

function embeddingDiagnosticDetail(config: EmbeddingConfigView) {
  return {
    provider_type: config.provider_type || 'api',
    provider_key: config.provider_key,
    endpoint_url: config.endpoint_url,
    model_id: config.model_id,
    dimensions: config.dimensions,
    user_present: Boolean(config.user),
    has_api_key: Boolean(config.api_key),
    onnx_model_path_present: Boolean(config.onnx_model_path),
    onnx_vocab_path_present: Boolean(config.onnx_vocab_path),
    max_sequence_length: config.max_sequence_length,
    normalize_embeddings: config.normalize_embeddings,
  }
}
