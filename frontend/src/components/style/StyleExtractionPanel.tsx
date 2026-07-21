import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { CheckCircle2, Loader2, Sparkles, XCircle } from 'lucide-react'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { useApp } from '@/hooks/useApp'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics, llm, styleSample } from '@/lib/novelist/types'

interface Props {
  novelId: number
  selectedIds: number[]
}

type Phase = 'idle' | 'extracting' | 'preview' | 'saved'

export default function StyleExtractionPanel({ novelId, selectedIds }: Props) {
  const app = useApp()
  const [models, setModels] = useState<llm.AvailableModel[]>([])
  const [selectedKey, setSelectedKey] = useState('')
  const [reasoningEffort, setReasoningEffort] = useState('')
  const [skillName, setSkillName] = useState('样本文风技能')
  const [phase, setPhase] = useState<Phase>('idle')
  const [run, setRun] = useState<styleSample.StyleSkillExtractionRun | null>(null)
  const [activeTaskId, setActiveTaskId] = useState<string | null>(null)
  const [errorTitle, setErrorTitle] = useState('风格技能抽取失败')
  const [error, setError] = useState('')
  const [errorDiagnostic, setErrorDiagnostic] = useState<diagnostics.CopyableDiagnostic | null>(null)
  const [notice, setNotice] = useState('')
  const cancelledTasks = useRef(new Set<string>())

  const selectedModel = useMemo(
    () => models.find(model => model.Key === selectedKey),
    [models, selectedKey],
  )
  const reasoningOptions = useMemo(
    () => selectedModel?.ReasoningLevels ?? [],
    [selectedModel],
  )
  const canStart = selectedIds.length > 0 && selectedKey.length > 0 && skillName.trim().length > 0 && phase !== 'extracting'
  const canSave = run?.status === 'completed' && run.skill_preview.length > 0 && phase !== 'saved'

  const setLocalError = useCallback((message: string) => {
    setErrorTitle('操作失败')
    setError(diagnosticMessage(message, '操作失败'))
    setErrorDiagnostic(null)
  }, [])

  const setErrorState = useCallback((
    errorValue: unknown,
    fallbackMessage: string,
    bridgeMethod: string,
    detail: Record<string, unknown>,
    taskId?: string | null,
  ) => {
    setErrorTitle(fallbackMessage)
    setError(diagnosticMessage(errorValue, fallbackMessage))
    setErrorDiagnostic(buildCopyableDiagnostic({
      error: errorValue,
      fallbackMessage,
      operation: bridgeMethod,
      taskId,
      bridgeMethod,
      detail,
    }))
  }, [])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const [modelList, settings] = await Promise.all([
          app.GetModels(),
          app.GetSettings(),
        ])
        if (cancelled) return
        setModels(modelList ?? [])
        if ((modelList ?? []).length === 0) return
        let key = settings?.selected_model_key || ''
        if (!modelList.find(model => model.Key === key)) {
          key = modelList[0].Key
        }
        setSelectedKey(key)
        const model = modelList.find(item => item.Key === key) ?? modelList[0]
        setReasoningEffort(model.ReasoningLevels?.[0] ?? '')
      } catch (err) {
        if (!cancelled) {
          setErrorState(
            err,
            '加载模型列表失败',
            'LoadStyleExtractionModels',
            { phase: 'load_models' },
          )
        }
      }
    })()
    return () => { cancelled = true }
  }, [app, setErrorState])

  const startExtraction = useCallback(async () => {
    if (!canStart) {
      setLocalError(selectedIds.length === 0 ? '请先选择至少一个风格样本' : '请补全模型和技能名称')
      return
    }

    const [providerName, modelId] = selectedKey.split('/')
    if (!providerName || !modelId) {
      setLocalError('模型配置不可用')
      return
    }

    const taskId = `style-skill-${Date.now()}-${Math.random().toString(16).slice(2)}`
    setActiveTaskId(taskId)
    setRun(null)
    setErrorTitle('风格技能抽取失败')
    setError('')
    setErrorDiagnostic(null)
    setNotice('')
    setPhase('extracting')

    try {
      const result = await app.ExtractStyleSkillFromSamples({
        task_id: taskId,
        novel_id: novelId,
        sample_ids: selectedIds,
        provider_name: providerName,
        model_id: modelId,
        reasoning_effort: reasoningEffort,
        skill_name: skillName.trim(),
      })

      setRun(result)
      if (result.status === 'completed') {
        setPhase('preview')
        setNotice('')
        return
      }

      if (result.status === 'cancelled' || cancelledTasks.current.has(taskId)) {
        setPhase('idle')
        setNotice('抽取已取消')
        return
      }

      setPhase('idle')
      setErrorTitle('风格技能抽取失败')
      setError(diagnosticsText(result.diagnostics, '风格技能抽取失败'))
      setErrorDiagnostic(result.diagnostics?.[0] ?? null)
    } catch (err) {
      if (cancelledTasks.current.has(taskId)) {
        setPhase('idle')
        setNotice('抽取已取消')
        return
      }
      setPhase('idle')
      setErrorState(
        err,
        '风格技能抽取失败',
        'ExtractStyleSkillFromSamples',
        styleExtractionDetail({
          taskId,
          novelId,
          selectedIds,
          providerName,
          modelId,
          reasoningEffort,
          skillName: skillName.trim(),
        }),
        taskId,
      )
    } finally {
      setActiveTaskId(current => current === taskId ? null : current)
    }
  }, [app, canStart, novelId, reasoningEffort, selectedIds, selectedKey, setErrorState, setLocalError, skillName])

  const cancelExtraction = useCallback(async () => {
    if (!activeTaskId) return
    cancelledTasks.current.add(activeTaskId)
    try {
      const result = await app.CancelStyleSkillExtraction({
        task_id: activeTaskId,
        reason: '用户取消',
      })
      setRun(result)
    } catch (err) {
      setErrorState(
        err,
        '取消抽取失败',
        'CancelStyleSkillExtraction',
        { phase: 'cancel_style_extraction' },
        activeTaskId,
      )
      return
    }
    setError('')
    setErrorDiagnostic(null)
    setNotice('抽取已取消')
    setPhase('idle')
  }, [activeTaskId, app, setErrorState])

  const saveSkill = useCallback(async () => {
    if (!run || run.status !== 'completed') return
    setErrorTitle('保存技能失败')
    setError('')
    setErrorDiagnostic(null)
    setNotice('')
    try {
      await app.SaveContent({
        novel_id: novelId,
        path: run.skill_file_path,
        content: run.skill_preview,
      })
      setPhase('saved')
      setNotice('技能已保存')
    } catch (err) {
      setErrorState(
        err,
        '保存技能失败',
        'SaveContent',
        {
          phase: 'save_style_skill',
          novel_id: novelId,
          path: run.skill_file_path,
          task_id: run.task_id,
        },
        run.task_id,
      )
    }
  }, [app, novelId, run, setErrorState])

  return (
    <section className="rounded-lg border border-border bg-card p-4">
      <div className="flex items-center gap-2">
        <Sparkles className="h-4 w-4 text-primary" />
        <h2 className="text-sm font-semibold text-foreground">风格技能抽取</h2>
      </div>

      <div className="mt-3 grid grid-cols-1 gap-3">
        <label className="block">
          <span className="mb-1 block text-xs font-medium text-muted-foreground">分析模型</span>
          <select
            value={selectedKey}
            onChange={event => {
              const key = event.target.value
              setSelectedKey(key)
              const model = models.find(item => item.Key === key)
              setReasoningEffort(model?.ReasoningLevels?.[0] ?? '')
            }}
            disabled={phase === 'extracting'}
            className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
          >
            {models.length === 0 ? (
              <option value="">无可用模型</option>
            ) : models.map(model => (
              <option key={model.Key} value={model.Key}>{model.ModelName}</option>
            ))}
          </select>
        </label>

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-[minmax(0,1fr)_120px]">
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-muted-foreground">技能名称</span>
            <input
              value={skillName}
              onChange={event => setSkillName(event.target.value)}
              disabled={phase === 'extracting'}
              className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-muted-foreground">推理强度</span>
            <select
              value={reasoningEffort}
              onChange={event => setReasoningEffort(event.target.value)}
              disabled={phase === 'extracting'}
              className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
            >
              {reasoningOptions.length === 0 ? (
                <option value="">默认</option>
              ) : reasoningOptions.map(level => (
                <option key={level} value={level}>{level}</option>
              ))}
            </select>
          </label>
        </div>
      </div>

      <div className="mt-3 flex flex-wrap items-center justify-between gap-2">
        <span className="text-xs text-muted-foreground">已选 {selectedIds.length} 个样本</span>
        <div className="flex items-center gap-2">
          {phase === 'extracting' && (
            <button
              type="button"
              onClick={cancelExtraction}
              className="inline-flex h-9 items-center gap-2 rounded-md border border-border bg-background px-3 text-sm text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              <XCircle className="h-4 w-4" />
              取消抽取
            </button>
          )}
          <button
            type="button"
            onClick={() => { void startExtraction() }}
            disabled={!canStart}
            className="inline-flex h-9 items-center gap-2 rounded-md bg-primary px-3 text-sm font-medium text-primary-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {phase === 'extracting' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Sparkles className="h-4 w-4" />}
            开始抽取
          </button>
        </div>
      </div>

      {notice && (
        <div className="mt-3 flex items-start gap-2 rounded-md border border-success-border bg-success-bg px-3 py-2 text-sm text-foreground">
          <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-success" />
          <span>{notice}</span>
        </div>
      )}

      {error && (
        <ErrorCallout
          title={errorTitle}
          message={error}
          diagnostic={errorDiagnostic}
          className="mt-3 rounded-md"
        />
      )}

      {run?.status === 'completed' && (
        <div className="mt-4 border-t border-border pt-3">
          <div className="flex items-center justify-between gap-3">
            <div className="min-w-0">
              <h3 className="text-sm font-semibold text-foreground">技能预览</h3>
              <p className="mt-1 truncate text-xs text-muted-foreground">{run.skill_file_path}</p>
            </div>
            <button
              type="button"
              onClick={() => { void saveSkill() }}
              disabled={!canSave}
              className="inline-flex h-9 shrink-0 items-center gap-2 rounded-md bg-action-save px-3 text-sm font-medium text-action-save-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            >
              保存技能
            </button>
          </div>
          <pre className="mt-3 max-h-64 overflow-auto whitespace-pre-wrap rounded-md border border-border bg-background px-3 py-2 text-xs leading-relaxed text-foreground">
            {run.skill_preview}
          </pre>
        </div>
      )}

    </section>
  )
}

function diagnosticsText(diagnostics: styleSample.StyleSkillExtractionRun['diagnostics'], fallback: string): string {
  const first = diagnostics?.[0]
  if (!first) return fallback
  return diagnosticMessage(first.detail ? `${first.message} ${first.detail}` : first.message, fallback)
}

function styleExtractionDetail({
  taskId,
  novelId,
  selectedIds,
  providerName,
  modelId,
  reasoningEffort,
  skillName,
}: {
  taskId: string
  novelId: number
  selectedIds: number[]
  providerName: string
  modelId: string
  reasoningEffort: string
  skillName: string
}): Record<string, unknown> {
  return {
    phase: 'extract_style_skill',
    task_id: taskId,
    novel_id: novelId,
    sample_ids: selectedIds,
    provider_name: providerName,
    model_id: modelId,
    reasoning_effort: reasoningEffort,
    skill_name: skillName,
  }
}
