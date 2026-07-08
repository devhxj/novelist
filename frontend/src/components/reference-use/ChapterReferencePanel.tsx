import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { AlertTriangle, Copy, CornerDownLeft, FileSearch, ListEnd, Loader2, Replace, Search, Wand2, X } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { copyTextToClipboard } from '@/lib/clipboard'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics, reference } from '@/lib/novelist/types'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { chapterNumFromPath, isChapterPath } from '@/components/content/types'

const FINAL_INSERTION_DECISION = 'approve_final_insertion'
type CandidateInsertMode = 'cursor' | 'append' | 'replace'

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
  onInsertCandidate: (text: string, mode: CandidateInsertMode) => boolean
  onClose: () => void
}

function materialTags(material: reference.MaterialSummary): string {
  return [
    material.material_type,
    material.function_tag || 'untagged',
    material.emotion_tag || 'neutral',
    material.pov_tag || 'unknown',
  ].join(' · ')
}

function scoreComponentEntries(scoreComponents?: Record<string, number> | null): Array<[string, number]> {
  return Object.entries(scoreComponents ?? {})
    .filter((entry): entry is [string, number] => typeof entry[1] === 'number' && Number.isFinite(entry[1]))
    .sort((left, right) => right[1] - left[1])
}

function formatConfidence(value: number): string {
  return Number.isFinite(value) ? value.toFixed(2) : '0.00'
}

type BoundedPreview = {
  text: string
  truncated: boolean
}

function boundedPreview(value: string, limit = 160): BoundedPreview {
  const normalized = value.trim().replace(/\s+/g, ' ')
  if (normalized.length <= limit) {
    return { text: normalized, truncated: false }
  }

  return {
    text: `${normalized.slice(0, limit).trimEnd()}...`,
    truncated: true,
  }
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

function candidatePreviewKey(run: reference.OrchestrationRun): string {
  return `${run.run_id}:${run.blueprint_id}:${run.candidate_ids.join('|')}`
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

export default function ChapterReferencePanel({ novelId, activeChapter, onInsertCandidate, onClose }: Props) {
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
  const [materials, setMaterials] = useState<reference.MaterialSummary[]>([])
  const [materialDetailId, setMaterialDetailId] = useState<string | null>(null)
  const [materialDetail, setMaterialDetail] = useState<reference.MaterialDetail | null>(null)
  const [materialDetailLoading, setMaterialDetailLoading] = useState(false)
  const [materialDetailError, setMaterialDetailError] = useState<ReferenceErrorState | null>(null)
  const materialDetailRequestRef = useRef(0)
  const [resultPath, setResultPath] = useState('')
  const [loading, setLoading] = useState(false)
  const [hasSearched, setHasSearched] = useState(false)
  const [error, setError] = useState<ReferenceErrorState | null>(null)
  const [run, setRun] = useState<reference.OrchestrationRun | null>(null)
  const [runLoading, setRunLoading] = useState(false)
  const [runError, setRunError] = useState<ReferenceErrorState | null>(null)
  const [runErrorAction, setRunErrorAction] = useState<'load' | 'start' | 'resume' | 'cancel' | null>(null)
  const [runActionLoading, setRunActionLoading] = useState<'resume' | 'cancel' | null>(null)
  const [candidateContextKey, setCandidateContextKey] = useState('')
  const [draftCandidates, setDraftCandidates] = useState<reference.DraftParagraphCandidate[]>([])
  const [draftAudits, setDraftAudits] = useState<reference.AnchoredDraftAudit[]>([])
  const [candidateLoading, setCandidateLoading] = useState(false)
  const [candidateError, setCandidateError] = useState<ReferenceErrorState | null>(null)
  const [candidateActionMessage, setCandidateActionMessage] = useState('')

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

  const openMaterialDetail = useCallback(async (materialId: string) => {
    const requestId = materialDetailRequestRef.current + 1
    materialDetailRequestRef.current = requestId
    setMaterialDetailId(materialId)
    setMaterialDetail(null)
    setMaterialDetailError(null)
    setMaterialDetailLoading(true)
    try {
      const detail = await app.GetReferenceMaterialDetail({
        novel_id: novelId,
        material_id: materialId,
      })
      if (materialDetailRequestRef.current !== requestId) return
      setMaterialDetail(detail ?? null)
      if (!detail) {
        const fallbackMessage = '材料明细不可用'
        setMaterialDetailError({
          title: fallbackMessage,
          message: '材料不存在、已归档，或当前作品无权访问。',
          diagnostic: buildCopyableDiagnostic({
            fallbackMessage,
            operation: '章节参考材料明细',
            bridgeMethod: 'GetReferenceMaterialDetail',
            detail: {
              novel_id: novelId,
              material_id: materialId,
              chapter_number: chapterNumber,
              chapter_path: activePath,
            },
          }),
        })
      }
    } catch (caught) {
      if (materialDetailRequestRef.current !== requestId) return
      const fallbackMessage = '材料明细加载失败'
      setMaterialDetailError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '章节参考材料明细',
          bridgeMethod: 'GetReferenceMaterialDetail',
          detail: {
            novel_id: novelId,
            material_id: materialId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
    } finally {
      if (materialDetailRequestRef.current === requestId) {
        setMaterialDetailLoading(false)
      }
    }
  }, [activePath, app, chapterNumber, novelId])

  const closeMaterialDetail = useCallback(() => {
    materialDetailRequestRef.current += 1
    setMaterialDetailId(null)
    setMaterialDetail(null)
    setMaterialDetailError(null)
    setMaterialDetailLoading(false)
  }, [])

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
  const finalCandidateKey = visibleRun && cannotResumeFinalInsertion && visibleRun.blueprint_id > 0 && visibleRun.candidate_ids.length > 0
    ? candidatePreviewKey(visibleRun)
    : ''
  const visibleDraftCandidates = finalCandidateKey && candidateContextKey === finalCandidateKey ? draftCandidates : []
  const visibleDraftAudits = finalCandidateKey && candidateContextKey === finalCandidateKey ? draftAudits : []

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

  const loadDraftCandidates = useCallback(async (targetRun: reference.OrchestrationRun, contextKey: string) => {
    setCandidateLoading(true)
    setCandidateError(null)
    setCandidateActionMessage('')
    try {
      const [candidates, audits] = await Promise.all([
        app.GetReferenceDraftCandidates({
          novel_id: novelId,
          blueprint_id: targetRun.blueprint_id,
          candidate_ids: targetRun.candidate_ids,
        }),
        app.GetReferenceAnchoredDraftAudits({
          novel_id: novelId,
          blueprint_id: targetRun.blueprint_id,
          candidate_ids: targetRun.candidate_ids,
          limit: Math.max(1, targetRun.candidate_ids.length),
        }),
      ])
      setCandidateContextKey(contextKey)
      setDraftCandidates(candidates ?? [])
      setDraftAudits(audits ?? [])
    } catch (caught) {
      const fallbackMessage = '候选预览加载失败'
      setCandidateContextKey(contextKey)
      setDraftCandidates([])
      setDraftAudits([])
      setCandidateError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '加载参考候选预览',
          bridgeMethod: 'GetReferenceDraftCandidates',
          detail: {
            novel_id: novelId,
            blueprint_id: targetRun.blueprint_id,
            candidate_ids: targetRun.candidate_ids,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
    } finally {
      setCandidateLoading(false)
    }
  }, [activePath, app, chapterNumber, novelId])

  useEffect(() => {
    if (!visibleRun || !finalCandidateKey) {
      return
    }

    if (candidateContextKey === finalCandidateKey) {
      return
    }

    const timer = window.setTimeout(() => {
      void loadDraftCandidates(visibleRun, finalCandidateKey)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [candidateContextKey, finalCandidateKey, loadDraftCandidates, visibleRun])

  const copyCandidate = useCallback(async (candidate: reference.DraftParagraphCandidate) => {
    try {
      await copyTextToClipboard(candidate.text)
      setCandidateActionMessage(`已复制 ${candidate.candidate_id}`)
    } catch (caught) {
      const fallbackMessage = '候选复制失败'
      setCandidateError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '复制参考候选',
          bridgeMethod: 'clipboard.writeText',
          detail: {
            candidate_id: candidate.candidate_id,
            blueprint_id: candidate.blueprint_id,
          },
        }),
      })
    }
  }, [])

  const insertCandidate = useCallback((candidate: reference.DraftParagraphCandidate, mode: CandidateInsertMode) => {
    const inserted = onInsertCandidate(candidate.text, mode)
    setCandidateActionMessage(inserted
      ? `已更新编辑器缓冲区：${candidate.candidate_id}`
      : '无法插入候选，请切回正文编辑器并确认光标或选区。')
  }, [onInsertCandidate])

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
              {visibleMaterials.map(material => {
                const preview = boundedPreview(material.text_preview)
                const isTruncated = material.text_truncated || preview.truncated

                return (
                  <article key={material.material_id} data-testid="chapter-reference-material-card" className="rounded border border-border bg-background px-2.5 py-2">
                    <div className="flex items-center justify-between gap-2">
                      <span className="min-w-0 truncate text-[11px] text-muted-foreground">
                        {material.material_id} · {materialTags(material)}
                      </span>
                      <span className="flex shrink-0 items-center gap-1">
                        {material.user_verified && <span className="text-[11px] text-emerald-600 dark:text-emerald-400">已校正</span>}
                        <button
                          type="button"
                          onClick={() => {
                            void openMaterialDetail(material.material_id)
                          }}
                          disabled={materialDetailLoading && materialDetailId === material.material_id}
                          className="inline-flex items-center gap-1 rounded px-1.5 py-1 text-[11px] leading-none text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
                          aria-label={`查看 ${material.material_id} 的材料明细`}
                        >
                          <FileSearch className="h-3.5 w-3.5" />
                          明细
                        </button>
                      </span>
                    </div>
                    <p className="mt-1 text-xs leading-relaxed text-foreground">{preview.text}</p>
                    {isTruncated && (
                      <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">
                        预览已截断，不显示全文
                      </p>
                    )}
                    <p className="mt-1 break-all text-[11px] leading-relaxed text-muted-foreground">
                      来源 {material.source_segment_id} · {material.source_hash}
                    </p>
                    <p className="mt-2 rounded bg-secondary/60 px-2 py-1 text-[11px] leading-relaxed text-muted-foreground">
                      候选由“启动参考流程”自动完成蓝图、绑定和审计后生成；推荐卡不直接改写或插入正文。
                    </p>
                  </article>
                )
              })}
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
          <h3 className="text-xs font-semibold text-foreground">严格流程</h3>
          <div className="rounded border border-border bg-background px-3 py-2 text-xs leading-relaxed text-muted-foreground">
            候选生成只通过下方参考流程推进：来源和事实边界确认、自动蓝图、材料绑定、审计通过后才进入最终插入决策。本面板不会从推荐素材直接生成可插入候选，也不会自动保存正文。
          </div>
          {runError && (
            <ErrorCallout
              compact
              title={runError.title}
              message={runError.message}
              diagnostic={runError.diagnostic}
              className="rounded-md"
              onRetry={retryRunError}
              retryLabel={runErrorAction === 'load' ? '重试加载流程记录' : runErrorAction === 'resume' ? '重试继续流程' : runErrorAction === 'cancel' ? '重试取消流程' : '重试启动流程'}
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
                  最终插入需要进入独立候选审查或正文编辑流程显式执行；参考流程不会自动保存正文。
                </div>
              )}
              {cannotResumeFinalInsertion && (
                <CandidatePreviewList
                  candidates={visibleDraftCandidates}
                  audits={visibleDraftAudits}
                  loading={candidateLoading}
                  error={candidateError}
                  actionMessage={candidateActionMessage}
                  insertionDisabled={insertionDisabled}
                  onRetry={() => {
                    if (visibleRun && finalCandidateKey) {
                      void loadDraftCandidates(visibleRun, finalCandidateKey)
                    }
                  }}
                  onCopy={candidate => {
                    void copyCandidate(candidate)
                  }}
                  onInsert={insertCandidate}
                  onDismissError={() => setCandidateError(null)}
                />
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
              大纲视图下仅可查看和推进参考流程；正文写入需要切回正文编辑器并经过独立候选审查。
            </div>
          )}
        </section>
      </div>
      {materialDetailId && (
        <ChapterMaterialDetailDrawer
          materialId={materialDetailId}
          detail={materialDetail}
          loading={materialDetailLoading}
          error={materialDetailError}
          onClose={closeMaterialDetail}
          onRetry={() => {
            void openMaterialDetail(materialDetailId)
          }}
        />
      )}
    </aside>
  )
}

function CandidatePreviewList({
  candidates,
  audits,
  loading,
  error,
  actionMessage,
  insertionDisabled,
  onRetry,
  onCopy,
  onInsert,
  onDismissError,
}: {
  candidates: reference.DraftParagraphCandidate[]
  audits: reference.AnchoredDraftAudit[]
  loading: boolean
  error: ReferenceErrorState | null
  actionMessage: string
  insertionDisabled: boolean
  onRetry: () => void
  onCopy: (candidate: reference.DraftParagraphCandidate) => void
  onInsert: (candidate: reference.DraftParagraphCandidate, mode: CandidateInsertMode) => void
  onDismissError: () => void
}) {
  return (
    <section data-testid="chapter-reference-candidate-preview" className="space-y-2 rounded border border-border bg-card px-2 py-2">
      <div className="flex items-center justify-between gap-2">
        <h4 className="text-xs font-semibold text-foreground">候选预览</h4>
        {loading && (
          <span className="inline-flex items-center gap-1 text-[11px] text-muted-foreground">
            <Loader2 className="h-3 w-3 animate-spin" />
            加载中
          </span>
        )}
      </div>

      {error && (
        <ErrorCallout
          compact
          title={error.title}
          message={error.message}
          diagnostic={error.diagnostic}
          className="rounded-md"
          onRetry={onRetry}
          retryLabel="重试候选预览"
          onClose={onDismissError}
        />
      )}

      {actionMessage && (
        <p className="rounded bg-secondary/70 px-2 py-1 text-[11px] text-muted-foreground">{actionMessage}</p>
      )}

      {!loading && !error && candidates.length === 0 && (
        <p className="rounded border border-dashed border-border bg-background px-2 py-2 text-xs text-muted-foreground">
          暂无可查看候选。请重试加载流程记录；需要新候选时，请回到参考流程重新生成。
        </p>
      )}

      {candidates.map(candidate => {
        const audit = auditForCandidate(candidate, audits)
        const canInsert = candidateCanInsert(candidate, audit)
        const findings = candidateFindings(candidate, audit)
        const actionsDisabled = insertionDisabled || !canInsert

        return (
          <article key={candidate.candidate_id} className="space-y-2 rounded border border-border bg-background px-2.5 py-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <span className="min-w-0 break-all text-[11px] text-muted-foreground">
                {candidate.candidate_id} · {candidate.beat_id} · {candidate.material_id}
              </span>
              <span className={`rounded px-1.5 py-0.5 text-[11px] ${canInsert ? 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-300' : 'bg-amber-500/10 text-amber-800 dark:text-amber-200'}`}>
                {audit?.status ?? candidate.audit_status}
              </span>
            </div>

            <p className="whitespace-pre-wrap rounded border border-border bg-card px-2 py-2 text-xs leading-relaxed text-foreground">
              {candidate.text}
            </p>

            <div className="space-y-1 text-[11px] leading-relaxed text-muted-foreground">
              <p>改写级别：{candidate.rewrite_level}</p>
              {audit?.readable_report?.summary && <p>审计：{audit.readable_report.summary}</p>}
              {candidate.non_slot_edits.length > 0 && <p>处理：{candidate.non_slot_edits.join('；')}</p>}
              {findings.length > 0 && (
                <div className="rounded border border-amber-500/30 bg-amber-500/10 px-2 py-1 text-amber-800 dark:text-amber-200">
                  {findings.map((finding, index) => (
                    <p key={`${finding.category}-${index}`}>
                      {finding.severity} · {finding.message}；{finding.required_action}
                    </p>
                  ))}
                </div>
              )}
            </div>

            <div className="grid grid-cols-2 gap-1.5">
              <button
                type="button"
                onClick={() => onCopy(candidate)}
                className="inline-flex items-center justify-center gap-1 rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:bg-secondary hover:text-foreground"
              >
                <Copy className="h-3.5 w-3.5" />
                复制候选
              </button>
              <button
                type="button"
                onClick={() => onInsert(candidate, 'cursor')}
                disabled={actionsDisabled}
                className="inline-flex items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:opacity-50"
              >
                <CornerDownLeft className="h-3.5 w-3.5" />
                插入到光标
              </button>
              <button
                type="button"
                onClick={() => onInsert(candidate, 'append')}
                disabled={actionsDisabled}
                className="inline-flex items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:opacity-50"
              >
                <ListEnd className="h-3.5 w-3.5" />
                追加到末尾
              </button>
              <button
                type="button"
                onClick={() => onInsert(candidate, 'replace')}
                disabled={actionsDisabled}
                className="inline-flex items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:opacity-50"
              >
                <Replace className="h-3.5 w-3.5" />
                替换选区
              </button>
            </div>

            {!canInsert && (
              <p className="text-[11px] text-muted-foreground">
                审计未通过或审计记录不可用时不能插入。
              </p>
            )}
          </article>
        )
      })}
    </section>
  )
}

function auditForCandidate(
  candidate: reference.DraftParagraphCandidate,
  audits: reference.AnchoredDraftAudit[],
): reference.AnchoredDraftAudit | null {
  return audits.find(audit => (audit.candidate_ids ?? []).includes(candidate.candidate_id)) ?? null
}

function candidateCanInsert(
  candidate: reference.DraftParagraphCandidate,
  audit: reference.AnchoredDraftAudit | null,
): boolean {
  return candidate.audit_status === 'passed' && audit?.status === 'passed'
}

function candidateFindings(
  candidate: reference.DraftParagraphCandidate,
  audit: reference.AnchoredDraftAudit | null,
): reference.DraftAuditReadableFinding[] {
  return audit?.readable_report?.findings
    ?.filter(finding => finding.candidate_ids.includes(candidate.candidate_id)) ?? []
}

function ChapterMaterialDetailDrawer({
  materialId,
  detail,
  loading,
  error,
  onClose,
  onRetry,
}: {
  materialId: string
  detail: reference.MaterialDetail | null
  loading: boolean
  error: ReferenceErrorState | null
  onClose: () => void
  onRetry: () => void
}) {
  const material = detail?.material
  const source = detail?.source
  const scores = scoreComponentEntries(material?.score_components)

  return (
    <aside
      role="dialog"
      aria-modal="false"
      aria-label="章节推荐材料明细"
      data-testid="chapter-reference-material-detail-drawer"
      className="fixed inset-y-0 right-0 z-50 flex w-[420px] max-w-[calc(100vw-3rem)] flex-col border-l border-border bg-card shadow-xl"
    >
      <div className="flex items-start justify-between gap-3 border-b border-border px-4 py-3">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-foreground">材料明细</h3>
          <p className="mt-0.5 truncate text-[11px] text-muted-foreground">{materialId}</p>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="rounded p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
          aria-label="关闭章节推荐材料明细"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-3">
        {loading && (
          <div className="flex items-center gap-2 rounded-md border border-border bg-background px-3 py-2 text-xs text-muted-foreground">
            <Loader2 className="h-3.5 w-3.5 animate-spin" />
            正在加载材料明细...
          </div>
        )}

        {error && (
          <ErrorCallout
            compact
            title={error.title}
            message={error.message}
            diagnostic={error.diagnostic}
            className="rounded-md"
            onRetry={onRetry}
            retryLabel="重试加载材料明细"
            onClose={onClose}
          />
        )}

        {material && source && (
          <>
            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">材料</h4>
              <div className="space-y-1 text-xs text-muted-foreground">
                <DetailKeyValue label="类型" value={`${material.material_type} · ${material.function_tag || 'untagged'} · ${material.emotion_tag || 'neutral'}`} />
                <DetailKeyValue label="POV/技法" value={`${material.pov_tag || 'unknown'} · ${material.technique_tag || 'none'}`} />
                <DetailKeyValue label="置信度" value={`功能 ${formatConfidence(material.function_confidence)} · 情绪 ${formatConfidence(material.emotion_confidence)} · POV ${formatConfidence(material.pov_confidence)}`} />
                <DetailKeyValue label="来源段落" value={material.source_segment_id} />
                <DetailKeyValue label="来源哈希" value={material.source_hash} />
                <DetailKeyValue label="校正状态" value={material.user_verified ? '已人工校正' : '未人工校正'} />
                <DetailKeyValue
                  label="评分明细"
                  value={scores.length > 0 ? scores.map(([name, value]) => `${name} ${value.toFixed(2)}`).join(' · ') : '暂无评分明细'}
                />
              </div>
              <p className="rounded-md border border-border bg-background px-3 py-2 text-xs leading-relaxed text-foreground">
                {material.text_preview || '无预览'}
              </p>
              <PreviewBoundary truncated={material.text_truncated} />
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">来源</h4>
              <div className="space-y-1 text-xs text-muted-foreground">
                <DetailKeyValue label="标题" value={source.title} />
                <DetailKeyValue label="作者" value={source.author || '未填写'} />
                <DetailKeyValue label="归属" value={source.owner_scope === 'workspace_corpus' ? '工作区语料' : `小说 ${source.owner_novel_id ?? source.novel_id}`} />
                <DetailKeyValue label="可见性" value={`${source.visibility} · ${source.source_trust}`} />
                <DetailKeyValue label="状态" value={`${source.status} · ${source.build_version}`} />
              </div>
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">来源片段</h4>
              {detail.segments.length === 0 ? (
                <p className="text-xs text-muted-foreground">暂无来源片段</p>
              ) : (
                <div className="space-y-2">
                  {detail.segments.map(segment => (
                    <div key={segment.segment_id} className="rounded-md border border-border bg-background px-3 py-2">
                      <p className="text-[11px] text-muted-foreground">
                        {segment.segment_id} · 第 {segment.chapter_index} 章 · {segment.chapter_title || '未命名章节'}
                      </p>
                      <p className="mt-1 text-xs leading-relaxed text-foreground">{segment.text_preview || '无预览'}</p>
                      <PreviewBoundary truncated={segment.text_truncated} compact />
                    </div>
                  ))}
                </div>
              )}
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">处理记录</h4>
              {detail.processing_notes.length === 0 ? (
                <p className="text-xs text-muted-foreground">暂无处理记录</p>
              ) : (
                <div className="space-y-2">
                  {detail.processing_notes.map((note, index) => (
                    <div key={`${note.stage}-${index}`} className="rounded-md border border-border bg-background px-3 py-2">
                      <p className="text-[11px] text-muted-foreground">{note.stage} · {note.status}</p>
                      <p className="mt-1 text-xs leading-relaxed text-foreground">{note.message || '无诊断信息'}</p>
                      <p className="mt-1 text-[11px] text-muted-foreground">
                        segments={note.source_segment_count} · materials={note.material_count} · slots={note.slot_count} · vectors={note.vector_count}
                      </p>
                    </div>
                  ))}
                </div>
              )}
            </section>
          </>
        )}
      </div>
    </aside>
  )
}

function PreviewBoundary({ truncated, compact = false }: { truncated: boolean; compact?: boolean }) {
  return (
    <p className={`${compact ? 'mt-1' : ''} text-[11px] text-muted-foreground`}>
      {truncated ? '预览已截断，不显示全文' : '完整预览'}
    </p>
  )
}

function DetailKeyValue({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid grid-cols-[72px_minmax(0,1fr)] gap-2">
      <span className="text-muted-foreground">{label}</span>
      <span className="min-w-0 break-all text-foreground">{value}</span>
    </div>
  )
}
