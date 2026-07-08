import { useCallback, useEffect, useMemo, useState } from 'react'
import { AlertTriangle, Check, Clipboard, CornerDownLeft, Loader2, Plus, Replace, Search, Wand2, X } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import { copyTextToClipboard } from '@/lib/clipboard'
import type { diagnostics, reference } from '@/lib/novelist/types'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { chapterNumFromPath, isChapterPath } from '@/components/content/types'

const FINAL_INSERTION_DECISION = 'approve_final_insertion'
type CandidateUseMode = 'insert' | 'append' | 'replace'

type ActiveChapterContext = {
  path: string
  title: string
  viewMode: string
}

type ReferenceErrorState = {
  title: string
  message: string
  diagnostic: diagnostics.CopyableDiagnostic
}

interface Props {
  novelId: number
  activeChapter: ActiveChapterContext | null
  onApplyCandidate: (text: string, mode: CandidateUseMode) => { ok: boolean; message: string }
  onClose: () => void
}

function materialTags(material: reference.Material): string {
  return [
    material.material_type,
    material.function_tag || 'untagged',
    material.emotion_tag || 'neutral',
    material.pov_tag || 'unknown',
  ].join(' · ')
}

function boundedPreview(value: string, limit = 160): string {
  const normalized = value.trim().replace(/\s+/g, ' ')
  if (normalized.length <= limit) return normalized
  return `${normalized.slice(0, limit).trimEnd()}...`
}

function inputLines(value: string): string[] {
  return value
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(Boolean)
}

function isTerminalRun(run: reference.OrchestrationRun | null): boolean {
  return run?.status === 'completed' || run?.status === 'cancelled'
}

function decisionPayloadFor(run: reference.OrchestrationRun, decision: reference.OrchestrationRequiredDecision): string {
  if (decision.decision_type === 'confirm_source_and_facts') {
    return 'confirmed'
  }

  if (decision.decision_type === 'approve_blueprint') {
    return run.review_id || 'approved'
  }

  if (decision.decision_type === 'apply_blueprint_revision' && decision.proposed_blueprint_revision) {
    return JSON.stringify(decision.proposed_blueprint_revision)
  }

  if (decision.decision_type === 'resolve_high_risk_stop') {
    return 'resolved'
  }

  return 'confirmed'
}

export default function ChapterReferencePanel({ novelId, activeChapter, onApplyCandidate, onClose }: Props) {
  const app = useApp()
  const activePath = activeChapter?.path ?? ''
  const activeTitle = activeChapter?.title ?? ''
  const activeViewMode = activeChapter?.viewMode ?? ''
  const chapterNumber = activePath && isChapterPath(activePath)
    ? chapterNumFromPath(activePath)
    : 0
  const hasValidChapter = Number.isFinite(chapterNumber) && chapterNumber > 0
  const [goal, setGoal] = useState('')
  const [knownFacts, setKnownFacts] = useState('')
  const [forbiddenFacts, setForbiddenFacts] = useState('')
  const [materials, setMaterials] = useState<reference.Material[]>([])
  const [resultPath, setResultPath] = useState('')
  const [loading, setLoading] = useState(false)
  const [hasSearched, setHasSearched] = useState(false)
  const [error, setError] = useState<ReferenceErrorState | null>(null)
  const [run, setRun] = useState<reference.OrchestrationRun | null>(null)
  const [runLoading, setRunLoading] = useState(false)
  const [runError, setRunError] = useState<ReferenceErrorState | null>(null)
  const [runErrorAction, setRunErrorAction] = useState<'load' | 'start' | 'resume' | 'cancel' | null>(null)
  const [runActionLoading, setRunActionLoading] = useState<'resume' | 'cancel' | null>(null)
  const [candidate, setCandidate] = useState<reference.AdaptMaterialResult | null>(null)
  const [candidateLoadingId, setCandidateLoadingId] = useState<string | null>(null)
  const [candidateError, setCandidateError] = useState<ReferenceErrorState | null>(null)
  const [candidateMessage, setCandidateMessage] = useState('')
  const [candidateCopyState, setCandidateCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')

  const contextSummary = useMemo(() => {
    if (!activeChapter) return '未打开章节'
    if (!hasValidChapter) return `${activeTitle} · 需要确认章节`
    return `第 ${chapterNumber} 章 · ${activeTitle}`
  }, [activeChapter, activeTitle, chapterNumber, hasValidChapter])

  const searchMaterials = useCallback(async (reason: 'auto' | 'manual' = 'manual', goalOverride = '') => {
    if (!activePath || !hasValidChapter) return
    setLoading(true)
    setError(null)
    try {
      const result = await app.SearchReferenceMaterials({
        novel_id: novelId,
        anchor_ids: [],
        query: [activeTitle, goalOverride].filter(Boolean).join(' ').trim(),
        material_types: [],
        emotion_tags: [],
        function_tags: [],
        pov_tags: [],
        technique_tags: [],
        page: 1,
        size: 5,
        narrative_duties: [],
        emotion_transitions: [],
        prose_duties: [],
      })
      setMaterials(result.items ?? [])
      setResultPath(activePath)
      setHasSearched(true)
    } catch (caught) {
      const fallbackMessage = reason === 'auto' ? '参考素材自动推荐失败' : '参考素材检索失败'
      setError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '章节参考素材检索',
          bridgeMethod: 'SearchReferenceMaterials',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
      setResultPath(activePath)
      setHasSearched(true)
    } finally {
      setLoading(false)
    }
  }, [activePath, activeTitle, app, chapterNumber, hasValidChapter, novelId])

  const loadLatestRun = useCallback(async () => {
    if (!activePath || !hasValidChapter) return
    try {
      const runs = await app.GetReferenceOrchestrationRuns(novelId, chapterNumber)
      setRun(runs[0] ?? null)
      setRunErrorAction(null)
    } catch (caught) {
      const fallbackMessage = '参考流程记录加载失败'
      setRunErrorAction('load')
      setRunError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '加载章节参考流程记录',
          bridgeMethod: 'GetReferenceOrchestrationRuns',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
    }
  }, [activePath, app, chapterNumber, hasValidChapter, novelId])

  useEffect(() => {
    if (!activePath || !hasValidChapter) return
    const timer = window.setTimeout(() => {
      void searchMaterials('auto')
    }, 0)
    return () => window.clearTimeout(timer)
  }, [activePath, hasValidChapter, searchMaterials])

  useEffect(() => {
    if (!activePath || !hasValidChapter) return
    const timer = window.setTimeout(() => {
      void loadLatestRun()
    }, 0)
    return () => window.clearTimeout(timer)
  }, [activePath, hasValidChapter, loadLatestRun])

  const startOrchestration = useCallback(async () => {
    if (!activePath || !hasValidChapter) return
    setRunLoading(true)
    setRunError(null)
    setRunErrorAction(null)
    try {
      const started = await app.StartReferenceOrchestrationRun({
        novel_id: novelId,
        chapter_number: chapterNumber,
        chapter_goal: goal.trim() || null,
        known_facts: inputLines(knownFacts),
        forbidden_facts: inputLines(forbiddenFacts),
        anchor_ids: null,
        corpus_search_policy: {
          mode: 'story_context',
          max_results_per_beat: 3,
          license_statuses: ['user_provided'],
          include_anchor_ids: [],
          exclude_anchor_ids: [],
        },
        source_confirmed: false,
        style_policy: null,
      })
      setRun(started)
    } catch (caught) {
      const fallbackMessage = '参考流程启动失败'
      setRunErrorAction('start')
      setRunError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '章节参考流程启动',
          bridgeMethod: 'StartReferenceOrchestrationRun',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
    } finally {
      setRunLoading(false)
    }
  }, [activePath, app, chapterNumber, forbiddenFacts, goal, hasValidChapter, knownFacts, novelId])

  const insertionDisabled = activeViewMode === 'outline'
  const isCurrentResult = resultPath === activePath
  const visibleError = isCurrentResult ? error : null
  const visibleMaterials = isCurrentResult ? materials : []
  const visibleHasSearched = isCurrentResult && hasSearched
  const visibleRun = run?.chapter_number === chapterNumber ? run : null
  const visibleDecision = visibleRun?.current_decision ?? null
  const cannotResumeFinalInsertion = visibleDecision?.decision_type === FINAL_INSERTION_DECISION
  const runActionBusy = runActionLoading !== null
  const canActOnRun = Boolean(visibleRun) && !isTerminalRun(visibleRun)
  const candidateAuditPassed = candidate?.audit.status === 'passed'
  const candidateInsertionDisabled = !candidateAuditPassed || insertionDisabled

  const resumeOrchestration = useCallback(async () => {
    if (!visibleRun || !visibleDecision || cannotResumeFinalInsertion) return
    setRunActionLoading('resume')
    setRunError(null)
    setRunErrorAction(null)
    try {
      const updated = await app.ResumeReferenceOrchestrationRun({
        novel_id: novelId,
        run_id: visibleRun.run_id,
        decision_type: visibleDecision.decision_type,
        decision_payload: decisionPayloadFor(visibleRun, visibleDecision),
      })
      setRun(updated)
    } catch (caught) {
      const fallbackMessage = '参考流程继续失败'
      setRunErrorAction('resume')
      setRunError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '继续章节参考流程',
          bridgeMethod: 'ResumeReferenceOrchestrationRun',
          detail: {
            novel_id: novelId,
            run_id: visibleRun.run_id,
            decision_type: visibleDecision.decision_type,
            chapter_number: chapterNumber,
          },
        }),
      })
    } finally {
      setRunActionLoading(null)
    }
  }, [app, cannotResumeFinalInsertion, chapterNumber, novelId, visibleDecision, visibleRun])

  const cancelOrchestration = useCallback(async () => {
    if (!visibleRun || isTerminalRun(visibleRun)) return
    setRunActionLoading('cancel')
    setRunError(null)
    setRunErrorAction(null)
    try {
      const cancelled = await app.CancelReferenceOrchestrationRun({
        novel_id: novelId,
        run_id: visibleRun.run_id,
        reason: 'user cancelled from chapter reference panel',
      })
      setRun(cancelled)
    } catch (caught) {
      const fallbackMessage = '参考流程取消失败'
      setRunErrorAction('cancel')
      setRunError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '取消章节参考流程',
          bridgeMethod: 'CancelReferenceOrchestrationRun',
          detail: {
            novel_id: novelId,
            run_id: visibleRun.run_id,
            chapter_number: chapterNumber,
          },
        }),
      })
    } finally {
      setRunActionLoading(null)
    }
  }, [app, chapterNumber, novelId, visibleRun])

  const retryRunError = useCallback(() => {
    if (runErrorAction === 'load') {
      void loadLatestRun()
      return
    }

    if (runErrorAction === 'resume') {
      void resumeOrchestration()
      return
    }

    if (runErrorAction === 'cancel') {
      void cancelOrchestration()
      return
    }

    void startOrchestration()
  }, [cancelOrchestration, loadLatestRun, resumeOrchestration, runErrorAction, startOrchestration])

  const generateCandidate = useCallback(async (material: reference.Material) => {
    setCandidateLoadingId(material.material_id)
    setCandidateError(null)
    setCandidateMessage('')
    try {
      const adapted = await app.AdaptReferenceMaterial({
        novel_id: novelId,
        material_id: material.material_id,
        slot_values: [],
        max_rewrite_level: 'L2',
        scene_facts: [goal.trim(), ...inputLines(knownFacts)].filter(Boolean),
      })
      setCandidate(adapted)
      setCandidateCopyState('idle')
    } catch (caught) {
      const fallbackMessage = '候选预览生成失败'
      setCandidateError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '生成章节参考候选预览',
          bridgeMethod: 'AdaptReferenceMaterial',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            material_id: material.material_id,
          },
        }),
      })
    } finally {
      setCandidateLoadingId(null)
    }
  }, [app, chapterNumber, goal, knownFacts, novelId])

  const copyCandidate = useCallback(async () => {
    if (!candidate) return
    try {
      await copyTextToClipboard(candidate.text)
      setCandidateCopyState('copied')
      setCandidateMessage('候选已复制。')
      window.setTimeout(() => setCandidateCopyState('idle'), 1500)
    } catch {
      setCandidateCopyState('failed')
      setCandidateMessage('复制失败，请重试。')
      window.setTimeout(() => setCandidateCopyState('idle'), 1500)
    }
  }, [candidate])

  const applyCandidate = useCallback((mode: CandidateUseMode) => {
    if (!candidate) return
    if (candidate.audit.status !== 'passed') {
      setCandidateMessage('审计未通过，不能插入正文。')
      return
    }

    if (insertionDisabled) {
      setCandidateMessage('大纲视图下禁止直接插入正文。')
      return
    }

    const result = onApplyCandidate(candidate.text, mode)
    setCandidateMessage(result.message)
  }, [candidate, insertionDisabled, onApplyCandidate])

  return (
    <aside
      data-testid="chapter-reference-panel"
      className="flex h-full w-[360px] max-w-[40vw] shrink-0 flex-col border-l bg-card"
      aria-label="章节参考素材"
    >
      <div className="flex items-start justify-between gap-3 border-b px-3 py-2">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-foreground">参考素材</h2>
          <p className="mt-0.5 truncate text-xs text-muted-foreground">{contextSummary}</p>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="rounded p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
          aria-label="关闭参考素材面板"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="min-h-0 flex-1 space-y-3 overflow-y-auto px-3 py-3">
        {!hasValidChapter && (
          <div className="rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs leading-relaxed text-amber-800 dark:text-amber-200">
            当前文件无法可靠推导章节号。请切换到 `chapters/001.md` 这类章节正文后再使用参考素材。
          </div>
        )}

        {visibleError && (
          <ErrorCallout
            compact
            title={visibleError.title}
            message={visibleError.message}
            diagnostic={visibleError.diagnostic}
            className="rounded-md"
            onRetry={() => {
              void searchMaterials('manual', goal)
            }}
            retryLabel="重试检索"
            onClose={() => setError(null)}
          />
        )}

        <section className="space-y-2">
          <h3 className="text-xs font-semibold text-foreground">章节上下文</h3>
          <label className="block text-xs text-muted-foreground">
            章节目标
            <textarea
              value={goal}
              onChange={event => setGoal(event.target.value)}
              className="mt-1 min-h-16 w-full resize-y rounded border border-input bg-background px-2 py-1.5 text-xs text-foreground outline-none focus:ring-2 focus:ring-ring"
              placeholder="可留空，系统会先按章节标题和可访问素材推荐"
            />
          </label>
          <label className="block text-xs text-muted-foreground">
            已知事实
            <textarea
              value={knownFacts}
              onChange={event => setKnownFacts(event.target.value)}
              className="mt-1 min-h-14 w-full resize-y rounded border border-input bg-background px-2 py-1.5 text-xs text-foreground outline-none focus:ring-2 focus:ring-ring"
              placeholder="每行一条，可留空"
            />
          </label>
          <label className="block text-xs text-muted-foreground">
            禁止事实
            <textarea
              value={forbiddenFacts}
              onChange={event => setForbiddenFacts(event.target.value)}
              className="mt-1 min-h-14 w-full resize-y rounded border border-input bg-background px-2 py-1.5 text-xs text-foreground outline-none focus:ring-2 focus:ring-ring"
              placeholder="每行一条，可留空"
            />
          </label>
        </section>

        <section className="space-y-2">
          <div className="flex items-center justify-between gap-2">
            <h3 className="text-xs font-semibold text-foreground">推荐素材</h3>
            <button
              type="button"
              onClick={() => {
                void searchMaterials('manual', goal)
              }}
              disabled={!hasValidChapter || loading}
              className="inline-flex items-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:opacity-50"
            >
              {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Search className="h-3.5 w-3.5" />}
              重新推荐
            </button>
          </div>

          {loading ? (
            <div className="rounded border border-border bg-background px-3 py-4 text-center text-xs text-muted-foreground">
              正在检索可访问素材...
            </div>
          ) : visibleMaterials.length > 0 ? (
            <div className="space-y-2" aria-label="章节推荐素材结果">
              {visibleMaterials.map(material => (
                <article key={material.material_id} data-testid="chapter-reference-material-card" className="rounded border border-border bg-background px-2.5 py-2">
                  <div className="flex items-center justify-between gap-2">
                    <span className="min-w-0 truncate text-[11px] text-muted-foreground">
                      {material.material_id} · {materialTags(material)}
                    </span>
                    {material.user_verified && <span className="shrink-0 text-[11px] text-emerald-600 dark:text-emerald-400">已校正</span>}
                  </div>
                  <p className="mt-1 text-xs leading-relaxed text-foreground">{boundedPreview(material.text)}</p>
                  <p className="mt-1 break-all text-[11px] leading-relaxed text-muted-foreground">
                    来源 {material.source_segment_id} · {material.source_hash}
                  </p>
                  <button
                    type="button"
                    onClick={() => {
                      void generateCandidate(material)
                    }}
                    disabled={candidateLoadingId !== null}
                    className="mt-2 inline-flex items-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:opacity-50"
                  >
                    {candidateLoadingId === material.material_id ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wand2 className="h-3.5 w-3.5" />}
                    生成候选
                  </button>
                </article>
              ))}
            </div>
          ) : visibleHasSearched ? (
            <div className="rounded border border-dashed border-border bg-background px-3 py-4 text-xs leading-relaxed text-muted-foreground">
              当前没有可用参考素材。请到素材库导入并处理语料，处理完成后回到本章重新推荐。
            </div>
          ) : (
            <div className="rounded border border-border bg-background px-3 py-4 text-xs text-muted-foreground">
              打开面板后会自动推荐可访问素材。
            </div>
          )}
        </section>

        <section className="space-y-2">
          <h3 className="text-xs font-semibold text-foreground">候选使用</h3>
          <div className="rounded border border-border bg-background px-3 py-2 text-xs leading-relaxed text-muted-foreground">
            候选生成和插入仍需通过审计流程。启动后会停在来源、事实边界、蓝图或审计决策处，最终插入不会自动写入正文。
          </div>
          {candidateError && (
            <ErrorCallout
              compact
              title={candidateError.title}
              message={candidateError.message}
              diagnostic={candidateError.diagnostic}
              className="rounded-md"
              onClose={() => setCandidateError(null)}
            />
          )}
          {candidate && (
            <div data-testid="chapter-reference-candidate-preview" className="space-y-2 rounded border border-border bg-background px-3 py-2 text-xs">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-medium text-foreground">候选 {candidate.candidate_id}</span>
                <span className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                  {candidate.rewrite_level} · {candidate.audit.status}
                </span>
              </div>
              <p className="max-h-32 overflow-y-auto whitespace-pre-wrap rounded border border-border bg-card px-2 py-1.5 leading-relaxed text-foreground">
                {candidate.text}
              </p>
              {(candidate.audit.required_fixes.length > 0 || candidate.audit.unsupported_fact_errors.length > 0 || candidate.audit.ai_prose_risks.length > 0) && (
                <div className="rounded border border-amber-500/30 bg-amber-500/10 px-2 py-1.5 leading-relaxed text-amber-800 dark:text-amber-200">
                  {[...candidate.audit.required_fixes, ...candidate.audit.unsupported_fact_errors, ...candidate.audit.ai_prose_risks].join('；')}
                </div>
              )}
              {!candidateAuditPassed && (
                <p data-testid="chapter-reference-candidate-blocked" className="text-[11px] text-amber-700 dark:text-amber-300">
                  审计未通过，插入正文前需要先处理修复项。
                </p>
              )}
              <div className="grid grid-cols-2 gap-1.5">
                <button
                  type="button"
                  onMouseDown={event => event.preventDefault()}
                  onClick={() => {
                    void copyCandidate()
                  }}
                  className="inline-flex items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80"
                >
                  {candidateCopyState === 'copied' ? <Check className="h-3.5 w-3.5" /> : <Clipboard className="h-3.5 w-3.5" />}
                  复制
                </button>
                <button
                  type="button"
                  onMouseDown={event => event.preventDefault()}
                  onClick={() => applyCandidate('insert')}
                  disabled={candidateInsertionDisabled}
                  className="inline-flex items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <CornerDownLeft className="h-3.5 w-3.5" />
                  插入光标
                </button>
                <button
                  type="button"
                  onMouseDown={event => event.preventDefault()}
                  onClick={() => applyCandidate('append')}
                  disabled={candidateInsertionDisabled}
                  className="inline-flex items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Plus className="h-3.5 w-3.5" />
                  追加末尾
                </button>
                <button
                  type="button"
                  onMouseDown={event => event.preventDefault()}
                  onClick={() => applyCandidate('replace')}
                  disabled={candidateInsertionDisabled}
                  className="inline-flex items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:cursor-not-allowed disabled:opacity-50"
                >
                  <Replace className="h-3.5 w-3.5" />
                  替换选区
                </button>
              </div>
              {candidateMessage && (
                <p data-testid="chapter-reference-candidate-message" className="text-[11px] text-muted-foreground">{candidateMessage}</p>
              )}
            </div>
          )}
          {runError && (
            <ErrorCallout
              compact
              title={runError.title}
              message={runError.message}
              diagnostic={runError.diagnostic}
              className="rounded-md"
              onRetry={retryRunError}
              retryLabel={runErrorAction === 'load' ? '重试加载' : runErrorAction === 'resume' ? '重试继续' : runErrorAction === 'cancel' ? '重试取消' : '重试启动'}
              onClose={() => setRunError(null)}
            />
          )}
          <button
            type="button"
            onClick={() => {
              void startOrchestration()
            }}
            disabled={!hasValidChapter || runLoading}
            className="inline-flex w-full items-center justify-center gap-1.5 rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50"
          >
            {runLoading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wand2 className="h-3.5 w-3.5" />}
            启动参考流程
          </button>
          {visibleRun && (
            <div data-testid="chapter-reference-orchestration-run" className="space-y-2 rounded border border-border bg-background px-3 py-2 text-xs">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <span className="font-medium text-foreground">流程 {visibleRun.run_id}</span>
                <span className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                  {visibleRun.status} · {visibleRun.stage}
                </span>
              </div>
              <p className="text-muted-foreground">
                第 {visibleRun.chapter_number} 章 · 候选 {visibleRun.candidate_ids.length} 个 · 蓝图 {visibleRun.blueprint_id || '待生成'}
              </p>
              {visibleDecision && (
                <div className="rounded border border-amber-500/30 bg-amber-500/10 px-2 py-1.5 text-amber-800 dark:text-amber-200">
                  <p className="font-medium">{visibleDecision.summary}</p>
                  {visibleDecision.required_actions.length > 0 && (
                    <p className="mt-1">{visibleDecision.required_actions.join('；')}</p>
                  )}
                  <div className="mt-2 space-y-1 text-[11px] leading-relaxed">
                    <p>章节功能：{visibleDecision.approval_summary.chapter_function || '待确认'}</p>
                    <p>视角：{visibleDecision.approval_summary.pov || '待确认'} · 情绪：{visibleDecision.approval_summary.emotional_trajectory || '待确认'}</p>
                    <p>素材计划：{visibleDecision.approval_summary.material_use_plan || '待确认'}</p>
                    <p>改写预算：{visibleDecision.approval_summary.rewrite_budget || '待确认'}</p>
                    {visibleDecision.approval_summary.fact_boundary_changes.length > 0 && (
                      <p>事实边界：{visibleDecision.approval_summary.fact_boundary_changes.join('；')}</p>
                    )}
                    {visibleDecision.approval_summary.high_risk_findings.length > 0 && (
                      <p>高风险：{visibleDecision.approval_summary.high_risk_findings.join('；')}</p>
                    )}
                  </div>
                </div>
              )}
              {cannotResumeFinalInsertion && (
                <div className="rounded border border-border bg-card px-2 py-1.5 text-muted-foreground">
                  最终插入必须由用户在候选预览中显式执行，参考流程不会自动保存正文。
                </div>
              )}
              {canActOnRun && (
                <div className="flex gap-2">
                  <button
                    type="button"
                    onClick={() => {
                      void resumeOrchestration()
                    }}
                    disabled={!visibleDecision || cannotResumeFinalInsertion || runActionBusy}
                    className="inline-flex flex-1 items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:opacity-50"
                  >
                    {runActionLoading === 'resume' && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                    确认并继续
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      void cancelOrchestration()
                    }}
                    disabled={runActionBusy}
                    className="inline-flex items-center justify-center gap-1 rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
                  >
                    {runActionLoading === 'cancel' && <Loader2 className="h-3.5 w-3.5 animate-spin" />}
                    取消流程
                  </button>
                </div>
              )}
            </div>
          )}
          {insertionDisabled && (
            <div className="flex gap-2 rounded border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs leading-relaxed text-amber-800 dark:text-amber-200">
              <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0" />
              大纲视图下禁止直接插入正文，请切回正文后再使用候选插入。
            </div>
          )}
        </section>
      </div>
    </aside>
  )
}
