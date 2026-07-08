import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  AlertTriangle,
  Boxes,
  CheckCircle2,
  Clipboard,
  FileText,
  GitBranch,
  Layers3,
  Loader2,
  Lock,
  Save,
  Sparkles,
  Unlock,
  XCircle,
} from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { chapter, llm, pattern } from '@/lib/novelist/types'
import { usePatternProgress } from '@/hooks/usePatternProgress'
import MultiRangeChapterSelector from '@/components/chapter/MultiRangeChapterSelector'
import { chapterRangesToIds } from '@/components/chapter/chapterRange'

interface Props {
  novelId: number
}

type SaveState = 'idle' | 'saving' | 'saved'
type CopyState = 'idle' | 'copied' | 'failed'

export default function NarrativePatternView({ novelId }: Props) {
  const app = useApp()
  const [chapters, setChapters] = useState<chapter.Chapter[]>([])
  const [ranges, setRanges] = useState<pattern.ChapterRange[]>([])
  const [models, setModels] = useState<llm.AvailableModel[]>([])
  const [selectedKey, setSelectedKey] = useState('')
  const [reasoningEffort, setReasoningEffort] = useState('')
  const [skillName, setSkillName] = useState('叙事模式技能')
  const [loading, setLoading] = useState(false)
  const [locked, setLocked] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [saveState, setSaveState] = useState<SaveState>('idle')
  const [savedSkillPath, setSavedSkillPath] = useState('')
  const [copyState, setCopyState] = useState<CopyState>('idle')

  const patternFlow = usePatternProgress({
    onStart: app.StartNarrativePatternExtraction,
    onCancel: app.CancelNarrativePatternExtraction,
    onGetTrace: app.GetNarrativePatternTrace,
  })
  const { state: patternState } = patternFlow

  const selectedModel = useMemo(
    () => models.find(model => model.Key === selectedKey),
    [models, selectedKey],
  )
  const reasoningOptions = selectedModel?.ReasoningLevels ?? []
  const running = patternState.status === 'running' || patternState.status === 'cancelling'
  const selectionIds = useMemo(() => {
    try {
      return chapterRangesToIds(ranges, chapters)
    } catch {
      return []
    }
  }, [chapters, ranges])
  const latestProgress = patternState.progress
  const completedRun = patternState.run?.status === 'completed' ? patternState.run : null
  const canStart = !running &&
    chapters.length > 0 &&
    ranges.length > 0 &&
    selectionIds.length > 0 &&
    selectedKey.length > 0 &&
    skillName.trim().length > 0
  const canSave = completedRun?.skill_preview && saveState !== 'saving' && saveState !== 'saved'

  const loadChapters = useCallback(async () => {
    if (!novelId) {
      setChapters([])
      setRanges([])
      return
    }

    setLoading(true)
    setError('')
    try {
      const list = await app.GetChapters(novelId)
      const next = [...(list ?? [])].sort((left, right) => left.chapter_number - right.chapter_number)
      setChapters(next)
      setRanges(next.length > 0
        ? [{ start_chapter: next[0].chapter_number, end_chapter: next[next.length - 1].chapter_number }]
        : [])
    } catch (err) {
      setError(errorText(err, '加载章节失败'))
    } finally {
      setLoading(false)
    }
  }, [app, novelId])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      await loadChapters()
      if (cancelled) return
    })()
    return () => { cancelled = true }
  }, [loadChapters])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const [modelList, settings] = await Promise.all([
          app.GetModels(),
          app.GetSettings(),
        ])
        if (cancelled) return
        const nextModels = modelList ?? []
        setModels(nextModels)
        if (nextModels.length === 0) return

        let key = settings?.selected_model_key || ''
        if (!nextModels.find(model => model.Key === key)) key = nextModels[0].Key
        setSelectedKey(key)
        const model = nextModels.find(item => item.Key === key) ?? nextModels[0]
        setReasoningEffort(settings?.reasoning_effort || model.ReasoningLevels?.[0] || '')
      } catch (err) {
        if (!cancelled) setError(errorText(err, '加载模型列表失败'))
      }
    })()
    return () => { cancelled = true }
  }, [app])

  const startExtraction = useCallback(async () => {
    if (!canStart) {
      setError(selectionIds.length === 0 ? '请先选择至少一个章节范围。' : '请补全模型和技能名称。')
      return
    }

    const [providerName, modelId] = selectedKey.split('/')
    if (!providerName || !modelId) {
      setError('模型配置不可用。')
      return
    }

    const taskId = `narrative-pattern-${Date.now()}-${Math.random().toString(16).slice(2)}`
    setError('')
    setNotice('')
    setSaveState('idle')
    setSavedSkillPath('')
    setCopyState('idle')

    const result = await patternFlow.start({
      task_id: taskId,
      novel_id: novelId,
      chapter_ranges: ranges,
      selected_chapter_ids: null,
      provider_name: providerName,
      model_id: modelId,
      reasoning_effort: reasoningEffort,
      skill_name: skillName.trim(),
    })

    if (!result) return
    if (result.status === 'completed') {
      setNotice('叙事模式技能预览已生成。')
      return
    }
    if (result.status === 'cancelled') {
      setNotice('叙事模式抽取已取消。')
      return
    }
    if (result.status === 'failed') {
      setError(diagnosticsText(result.diagnostics, '叙事模式抽取失败。'))
    }
  }, [canStart, novelId, patternFlow, ranges, reasoningEffort, selectedKey, selectionIds, skillName])

  const cancelExtraction = useCallback(async () => {
    setError('')
    const result = await patternFlow.cancel()
    if (!result) return
    if (result.status === 'cancelled') {
      setNotice('叙事模式抽取已取消。')
    } else if (result.status === 'failed') {
      setError(diagnosticsText(result.diagnostics, '取消后状态读取失败。'))
    }
  }, [patternFlow])

  const saveSkill = useCallback(async () => {
    if (!completedRun) return
    setError('')
    setNotice('')
    setSaveState('saving')
    try {
      const path = skillPathFromRun(completedRun)
      await app.SaveContent({
        novel_id: novelId,
        path,
        content: completedRun.skill_preview,
      })
      setSavedSkillPath(path)
      setSaveState('saved')
      setNotice('技能已保存。')
    } catch (err) {
      setSaveState('idle')
      setError(errorText(err, '保存技能失败。'))
    }
  }, [app, completedRun, novelId])

  const copyDiagnostics = useCallback(async () => {
    const payload = {
      run: patternState.run,
      trace: patternState.trace,
      timeline: patternState.timeline,
      ui_error: error || patternState.errorMessage,
    }
    try {
      await copyTextToClipboard(JSON.stringify(payload, null, 2))
      setCopyState('copied')
      window.setTimeout(() => setCopyState('idle'), 1800)
    } catch {
      setCopyState('failed')
      window.setTimeout(() => setCopyState('idle'), 1800)
    }
  }, [error, patternState.errorMessage, patternState.run, patternState.timeline, patternState.trace])

  const visibleError = error || patternState.errorMessage
  const copyLabel = copyState === 'copied' ? '已复制' : copyState === 'failed' ? '复制失败' : '复制诊断'
  const selectedSummary = ranges.length === 0
    ? '未选择章节'
    : `chapter_ranges=${ranges.map(range => `${range.start_chapter}-${range.end_chapter}`).join(',')}`

  return (
    <main className="flex min-w-0 flex-1 flex-col bg-background">
      <header className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-5 py-4">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <Boxes className="h-4 w-4 text-primary" />
            <h1 className="text-base font-semibold text-foreground">叙事模式</h1>
          </div>
          <p className="mt-1 text-xs text-muted-foreground">{selectedSummary}</p>
        </div>
        <button
          type="button"
          onClick={() => setLocked(value => !value)}
          disabled={running}
          className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background px-2.5 text-xs text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          aria-pressed={locked}
        >
          {locked ? <Lock className="h-3.5 w-3.5" /> : <Unlock className="h-3.5 w-3.5" />}
          {locked ? '已锁定' : '锁定选择'}
        </button>
      </header>

      {(visibleError || notice) && (
        <div
          role={visibleError ? 'alert' : 'status'}
          className={`border-b px-5 py-2 text-sm ${visibleError ? 'border-danger-border bg-danger-bg' : 'border-success-border bg-success-bg'}`}
        >
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="flex min-w-0 items-start gap-2 text-foreground">
              {visibleError ? (
                <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
              ) : (
                <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-success" />
              )}
              <span className="min-w-0 break-words">{visibleError || notice}</span>
            </div>
            {visibleError && (
              <button
                type="button"
                onClick={() => { void copyDiagnostics() }}
                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background px-2.5 text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              >
                <Clipboard className="h-3.5 w-3.5" />
                {copyLabel}
              </button>
            )}
          </div>
        </div>
      )}

      <div className="grid min-h-0 flex-1 grid-cols-1 gap-4 p-4 xl:grid-cols-[minmax(420px,1.08fr)_minmax(360px,0.92fr)]">
        <section className="flex h-full min-h-[520px] flex-col overflow-hidden xl:min-h-0">
          {loading ? (
            <div className="flex h-full min-h-64 items-center justify-center border border-border bg-card text-sm text-muted-foreground">
              加载中...
            </div>
          ) : (
            <div className="min-h-0 flex-1">
              <MultiRangeChapterSelector
                chapters={chapters}
                value={ranges}
                onChange={(next) => {
                  setRanges(next)
                  setSaveState('idle')
                  setSavedSkillPath('')
                }}
                disabled={locked || running}
                compact
              />
            </div>
          )}
        </section>

        <aside className="flex min-h-0 flex-col gap-4 overflow-auto">
          <section className="border border-border bg-card p-4">
            <div className="flex items-center gap-2">
              <Sparkles className="h-4 w-4 text-primary" />
              <h2 className="text-sm font-semibold text-foreground">抽取设置</h2>
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
                  disabled={running}
                  className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
                >
                  {models.length === 0 ? (
                    <option value="">无可用模型</option>
                  ) : models.map(model => (
                    <option key={model.Key} value={model.Key}>{model.ModelName}</option>
                  ))}
                </select>
              </label>

              <div className="grid grid-cols-1 gap-3 sm:grid-cols-[minmax(0,1fr)_130px]">
                <label className="block">
                  <span className="mb-1 block text-xs font-medium text-muted-foreground">技能名称</span>
                  <input
                    value={skillName}
                    onChange={event => setSkillName(event.target.value)}
                    disabled={running}
                    className="h-9 w-full rounded-md border border-input bg-background px-3 text-sm text-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/20 disabled:opacity-60"
                  />
                </label>
                <label className="block">
                  <span className="mb-1 block text-xs font-medium text-muted-foreground">推理强度</span>
                  <select
                    value={reasoningEffort}
                    onChange={event => setReasoningEffort(event.target.value)}
                    disabled={running}
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
              <span className="text-xs text-muted-foreground">
                已选 {selectionIds.length} / {chapters.length} 章
              </span>
              <div className="flex items-center gap-2">
                {running && (
                  <button
                    type="button"
                    onClick={() => { void cancelExtraction() }}
                    disabled={patternState.status === 'cancelling'}
                    className="inline-flex h-9 items-center gap-2 rounded-md border border-border bg-background px-3 text-sm text-foreground transition-colors hover:bg-muted disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
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
                  {running ? <Loader2 className="h-4 w-4 animate-spin" /> : <Sparkles className="h-4 w-4" />}
                  开始抽取
                </button>
              </div>
            </div>
          </section>

          <ProgressPanel progress={latestProgress} timeline={patternState.timeline} />
          <TracePanel trace={patternState.trace} progress={latestProgress} onCopy={() => { void copyDiagnostics() }} copyLabel={copyLabel} />
          <SkillPreviewPanel
            run={completedRun}
            saveState={saveState}
            savedSkillPath={savedSkillPath}
            onSave={() => { void saveSkill() }}
            canSave={Boolean(canSave)}
          />
        </aside>
      </div>
    </main>
  )
}

function ProgressPanel({
  progress,
  timeline,
}: {
  progress: pattern.NarrativePatternProgress | null
  timeline: pattern.NarrativePatternProgress[]
}) {
  const percent = progressPercent(progress)
  const counters = [
    ['边界提示', progress?.boundary_count],
    ['章节摘要', progress?.summary_count],
    ['阶段压缩', progress?.phase_count],
  ] as const

  return (
    <section className="border border-border bg-card p-4">
      <div className="flex items-center gap-2">
        <GitBranch className="h-4 w-4 text-primary" />
        <h2 className="text-sm font-semibold text-foreground">进度时间线</h2>
      </div>

      <div className="mt-3">
        <div className="flex items-center justify-between gap-3 text-xs text-muted-foreground">
          <span>{progress ? stageLabel(progress.stage) : '未开始'}</span>
          <span>{progress ? `${progress.progress_completed}/${progress.progress_total}` : '0/0'}</span>
        </div>
        <div
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={percent}
          className="mt-2 h-2 overflow-hidden rounded-full bg-muted"
        >
          <div className="h-full rounded-full bg-primary transition-all" style={{ width: `${percent}%` }} />
        </div>
        {progress?.message && (
          <p className="mt-2 text-xs text-muted-foreground">{progress.message}</p>
        )}
      </div>

      <div className="mt-3 grid grid-cols-3 gap-2">
        {counters.map(([label, value]) => (
          <div key={label} className="rounded-md border border-border bg-background px-2.5 py-2">
            <div className="text-[11px] text-muted-foreground">{label}</div>
            <div className="mt-1 text-sm font-semibold tabular-nums text-foreground">{value ?? '-'}</div>
          </div>
        ))}
      </div>

      {(progress?.round || progress?.batch_index || progress?.token_estimate || progress?.llm_status) && (
        <div className="mt-3 flex flex-wrap gap-2 text-xs text-muted-foreground">
          {progress.llm_status && <span className="rounded border border-border bg-background px-2 py-1">LLM {progress.llm_status}</span>}
          {progress.round != null && <span className="rounded border border-border bg-background px-2 py-1">轮次 {progress.round}</span>}
          {progress.batch_index != null && progress.batch_total != null && (
            <span className="rounded border border-border bg-background px-2 py-1">批次 {progress.batch_index}/{progress.batch_total}</span>
          )}
          {progress.token_estimate != null && (
            <span className="rounded border border-border bg-background px-2 py-1">tokens {progress.token_estimate}</span>
          )}
        </div>
      )}

      <ol className="mt-3 max-h-48 overflow-auto border-t border-border pt-2">
        {timeline.length === 0 ? (
          <li className="py-4 text-center text-sm text-muted-foreground">暂无进度事件</li>
        ) : timeline.map((item, index) => (
          <li key={`${item.task_id}-${item.stage}-${item.updated_at}-${index}`} className="grid grid-cols-[auto_minmax(0,1fr)] gap-2 py-2">
            <span className="mt-1 h-2 w-2 rounded-full bg-primary" />
            <div className="min-w-0">
              <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
                <span className="text-xs font-medium text-foreground">{stageLabel(item.stage)}</span>
                <span className="text-[11px] text-muted-foreground">{item.status}</span>
              </div>
              <p className="mt-0.5 break-words text-xs text-muted-foreground">{item.message}</p>
            </div>
          </li>
        ))}
      </ol>
    </section>
  )
}

function TracePanel({
  trace,
  progress,
  onCopy,
  copyLabel,
}: {
  trace: pattern.NarrativePatternTrace | null
  progress: pattern.NarrativePatternProgress | null
  onCopy: () => void
  copyLabel: string
}) {
  const entries = trace?.entries ?? []
  return (
    <section className="border border-border bg-card p-4">
      <div className="flex items-center justify-between gap-3">
        <div className="flex items-center gap-2">
          <Layers3 className="h-4 w-4 text-primary" />
          <h2 className="text-sm font-semibold text-foreground">结构检查</h2>
        </div>
        <button
          type="button"
          onClick={onCopy}
          className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border bg-background px-2.5 text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <Clipboard className="h-3.5 w-3.5" />
          {copyLabel}
        </button>
      </div>

      <div className="mt-3 grid grid-cols-1 gap-2">
        <InspectableMetric
          title="边界提示"
          value={progress?.boundary_count}
          detail={entries.filter(entry => entry.stage === 'boundary_detection')}
        />
        <InspectableMetric
          title="章节摘要"
          value={progress?.summary_count}
          detail={entries.filter(entry => entry.stage === 'chapter_summary')}
        />
        <InspectableMetric
          title="阶段压缩"
          value={progress?.phase_count}
          detail={entries.filter(entry => entry.stage === 'phase_compression')}
        />
      </div>

      <details className="mt-3 rounded-md border border-border bg-background">
        <summary className="cursor-pointer px-3 py-2 text-sm font-medium text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
          Trace entries ({entries.length})
        </summary>
        <div className="max-h-56 overflow-auto border-t border-border">
          {entries.length === 0 ? (
            <div className="px-3 py-4 text-sm text-muted-foreground">暂无 trace</div>
          ) : entries.map(entry => (
            <div key={entry.trace_id} className="border-b border-border px-3 py-2 last:border-b-0">
              <div className="text-xs font-medium text-foreground">{stageLabel(entry.stage)}</div>
              <div className="mt-1 grid gap-1 text-[11px] text-muted-foreground">
                <span className="break-all">input_hash={entry.input_hash}</span>
                <span className="break-all">output_hash={entry.output_hash}</span>
              </div>
              {entry.diagnostics.length > 0 && (
                <div className="mt-2 space-y-1">
                  {entry.diagnostics.map(diagnostic => (
                    <div key={`${entry.trace_id}-${diagnostic.code}`} className="rounded border border-danger-border bg-danger-bg px-2 py-1 text-xs text-foreground">
                      {diagnostic.message}
                    </div>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      </details>
    </section>
  )
}

function InspectableMetric({
  title,
  value,
  detail,
}: {
  title: string
  value?: number | null
  detail: pattern.NarrativePatternTraceEntry[]
}) {
  return (
    <details className="rounded-md border border-border bg-background">
      <summary className="cursor-pointer px-3 py-2 text-sm text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
        <span className="font-medium">{title}</span>
        <span className="ml-2 text-xs tabular-nums text-muted-foreground">{value ?? 0}</span>
      </summary>
      <div className="border-t border-border px-3 py-2 text-xs text-muted-foreground">
        {detail.length === 0 ? (
          <span>暂无记录</span>
        ) : (
          <ul className="space-y-1">
            {detail.map(entry => (
              <li key={entry.trace_id} className="break-all">
                {entry.trace_id}: output_hash={entry.output_hash}
              </li>
            ))}
          </ul>
        )}
      </div>
    </details>
  )
}

function SkillPreviewPanel({
  run,
  saveState,
  savedSkillPath,
  onSave,
  canSave,
}: {
  run: pattern.NarrativePatternRun | null
  saveState: SaveState
  savedSkillPath: string
  onSave: () => void
  canSave: boolean
}) {
  return (
    <section className="border border-border bg-card p-4">
      <div className="flex items-center justify-between gap-3">
        <div className="flex min-w-0 items-center gap-2">
          <FileText className="h-4 w-4 shrink-0 text-primary" />
          <h2 className="truncate text-sm font-semibold text-foreground">技能预览</h2>
        </div>
        <button
          type="button"
          onClick={onSave}
          disabled={!canSave}
          className="inline-flex h-8 shrink-0 items-center gap-1.5 rounded-md bg-action-save px-2.5 text-xs font-medium text-action-save-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {saveState === 'saving' ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Save className="h-3.5 w-3.5" />}
          保存技能
        </button>
      </div>
      {savedSkillPath && (
        <p className="mt-2 text-xs text-muted-foreground">{savedSkillPath}</p>
      )}
      {run ? (
        <pre className="mt-3 max-h-80 overflow-auto whitespace-pre-wrap rounded-md border border-border bg-background px-3 py-2 text-xs leading-relaxed text-foreground">
          {run.skill_preview}
        </pre>
      ) : (
        <div className="mt-3 flex min-h-32 items-center justify-center rounded-md border border-border bg-background px-3 py-6 text-sm text-muted-foreground">
          暂无预览
        </div>
      )}
    </section>
  )
}

function stageLabel(stage: string): string {
  switch (stage) {
    case 'queued':
      return '排队'
    case 'load_chapters':
      return '加载章节'
    case 'boundary_detection':
      return '边界识别'
    case 'chapter_summary':
      return '章节摘要'
    case 'phase_compression':
      return '阶段压缩'
    case 'skill_generation':
      return '技能生成'
    case 'completed':
      return '完成'
    case 'failed':
      return '失败'
    case 'cancelled':
      return '已取消'
    default:
      return stage || '未知阶段'
  }
}

function progressPercent(progress: pattern.NarrativePatternProgress | null): number {
  if (!progress || progress.progress_total <= 0) return 0
  return Math.max(0, Math.min(100, Math.round((progress.progress_completed / progress.progress_total) * 100)))
}

function diagnosticsText(diagnostics: pattern.NarrativePatternRun['diagnostics'], fallback: string): string {
  const first = diagnostics?.[0]
  if (!first) return fallback
  return first.detail ? `${first.message} ${first.detail}` : first.message
}

function skillPathFromRun(run: pattern.NarrativePatternRun): string {
  const frontmatterName = parseSkillName(run.skill_preview)
  const name = normalizeSkillFileName(frontmatterName || run.skill_name)
  return `skills/${name}.md`
}

function parseSkillName(markdown: string): string {
  const normalized = markdown.replace(/\r\n/g, '\n')
  if (!normalized.startsWith('---\n')) return ''
  const end = normalized.indexOf('\n---', 4)
  if (end < 0) return ''
  const frontmatter = normalized.slice(4, end)
  for (const line of frontmatter.split('\n')) {
    const separator = line.indexOf(':')
    if (separator <= 0) continue
    if (line.slice(0, separator).trim() !== 'name') continue
    return line.slice(separator + 1).trim().replace(/^['"]|['"]$/g, '')
  }
  return ''
}

function normalizeSkillFileName(name: string): string {
  const normalized = name.trim()
  if (normalized.length === 0) throw new Error('技能名称为空，无法保存。')
  if ([...normalized].some(ch => ch.charCodeAt(0) < 32 || '/\\:*?"<>|'.includes(ch))) {
    throw new Error('技能名称包含不支持的路径字符。')
  }
  return normalized
}

function errorText(error: unknown, fallback: string): string {
  if (error instanceof Error) return error.message
  if (typeof error === 'string') return error
  return fallback
}

async function copyTextToClipboard(text: string) {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text)
      return
    } catch {
      // Fall through for desktop/webview clipboard permission quirks.
    }
  }

  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.setAttribute('readonly', 'true')
  textarea.style.position = 'fixed'
  textarea.style.left = '-9999px'
  document.body.appendChild(textarea)
  textarea.select()
  try {
    document.execCommand('copy')
  } finally {
    textarea.remove()
  }
}
