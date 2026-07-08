import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { AlertTriangle, BookOpenCheck, CheckCircle2, Loader2, Sparkles, XCircle } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { llm, styleSample } from '@/lib/novelist/types'

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
  const [profileTitle, setProfileTitle] = useState('样本风格画像')
  const [profileDescription, setProfileDescription] = useState('')
  const [profileBuilding, setProfileBuilding] = useState(false)
  const [phase, setPhase] = useState<Phase>('idle')
  const [run, setRun] = useState<styleSample.StyleSkillExtractionRun | null>(null)
  const [activeTaskId, setActiveTaskId] = useState<string | null>(null)
  const [error, setError] = useState('')
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
  const canBuildProfile = selectedIds.length > 0 && profileTitle.trim().length > 0 && !profileBuilding && phase !== 'extracting'

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
        if (!cancelled) setError(errorText(err, '加载模型列表失败'))
      }
    })()
    return () => { cancelled = true }
  }, [app])

  const startExtraction = useCallback(async () => {
    if (!canStart) {
      setError(selectedIds.length === 0 ? '请先选择至少一个风格样本' : '请补全模型和技能名称')
      return
    }

    const [providerName, modelId] = selectedKey.split('/')
    if (!providerName || !modelId) {
      setError('模型配置不可用')
      return
    }

    const taskId = `style-skill-${Date.now()}-${Math.random().toString(16).slice(2)}`
    setActiveTaskId(taskId)
    setRun(null)
    setError('')
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
      setError(diagnosticsText(result.diagnostics, '风格技能抽取失败'))
    } catch (err) {
      if (cancelledTasks.current.has(taskId)) {
        setPhase('idle')
        setNotice('抽取已取消')
        return
      }
      setPhase('idle')
      setError(errorText(err, '风格技能抽取失败'))
    } finally {
      setActiveTaskId(current => current === taskId ? null : current)
    }
  }, [app, canStart, novelId, reasoningEffort, selectedIds, selectedKey, skillName])

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
      setError(errorText(err, '取消抽取失败'))
      return
    }
    setNotice('抽取已取消')
    setPhase('idle')
  }, [activeTaskId, app])

  const saveSkill = useCallback(async () => {
    if (!run || run.status !== 'completed') return
    setError('')
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
      setError(errorText(err, '保存技能失败'))
    }
  }, [app, novelId, run])

  const buildStyleProfile = useCallback(async () => {
    if (!canBuildProfile) {
      setError(selectedIds.length === 0 ? '请先选择至少一个风格样本' : '请填写画像标题')
      return
    }

    const buildId = `style-sample-profile-${Date.now()}-${Math.random().toString(16).slice(2)}`
    setProfileBuilding(true)
    setError('')
    setNotice('')
    try {
      await app.BuildReferenceStyleProfile({
        build_id: buildId,
        novel_id: novelId,
        title: profileTitle.trim(),
        description: profileDescription.trim(),
        anchor_ids: [],
        allowed_license_statuses: [],
        allowed_source_trust_levels: [],
        style_sample_ids: selectedIds,
      })
      setNotice('风格画像已构建')
    } catch (err) {
      setError(errorText(err, '构建风格画像失败'))
    } finally {
      setProfileBuilding(false)
    }
  }, [app, canBuildProfile, novelId, profileDescription, profileTitle, selectedIds])

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
        <div role="alert" className="mt-3 flex items-start gap-2 rounded-md border border-danger-border bg-danger-bg px-3 py-2 text-sm text-foreground">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
          <span className="min-w-0 break-words">{error}</span>
        </div>
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

      <div className="mt-4 border-t border-border pt-4">
        <div className="flex items-center gap-2">
          <BookOpenCheck className="h-4 w-4 text-primary" />
          <h3 className="text-sm font-semibold text-foreground">构建风格画像</h3>
        </div>
        <div className="mt-3 grid grid-cols-1 gap-3">
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-muted-foreground">画像标题</span>
            <input
              value={profileTitle}
              onChange={event => setProfileTitle(event.target.value)}
              disabled={profileBuilding}
              className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
            />
          </label>
          <label className="block">
            <span className="mb-1 block text-xs font-medium text-muted-foreground">画像说明</span>
            <textarea
              value={profileDescription}
              onChange={event => setProfileDescription(event.target.value)}
              disabled={profileBuilding}
              className="min-h-16 w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm leading-relaxed text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
            />
          </label>
        </div>
        <div className="mt-3 flex flex-wrap items-center justify-between gap-2">
          <span className="text-xs text-muted-foreground">已选 {selectedIds.length} 个样本</span>
          <button
            type="button"
            onClick={() => { void buildStyleProfile() }}
            disabled={!canBuildProfile}
            className="inline-flex h-9 items-center gap-2 rounded-md bg-secondary px-3 text-sm font-medium text-foreground transition-colors hover:bg-secondary/80 disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {profileBuilding ? <Loader2 className="h-4 w-4 animate-spin" /> : <BookOpenCheck className="h-4 w-4" />}
            构建画像
          </button>
        </div>
      </div>
    </section>
  )
}

function diagnosticsText(diagnostics: styleSample.StyleSkillExtractionRun['diagnostics'], fallback: string): string {
  const first = diagnostics?.[0]
  if (!first) return fallback
  return first.detail ? `${first.message} ${first.detail}` : first.message
}

function errorText(error: unknown, fallback: string): string {
  if (error instanceof Error) return error.message
  if (typeof error === 'string') return error
  return fallback
}
