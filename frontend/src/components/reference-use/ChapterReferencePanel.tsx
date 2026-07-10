import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { AlertTriangle, Check, Copy, CornerDownLeft, FileSearch, GitCompareArrows, ListEnd, Loader2, Lock, RefreshCw, Replace, Search, Wand2, X } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { copyTextToClipboard } from '@/lib/clipboard'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics, reference } from '@/lib/novelist/types'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { chapterNumFromPath, isChapterPath } from '@/components/content/types'

const FINAL_INSERTION_DECISION = 'approve_final_insertion'
type CandidateInsertMode = 'cursor' | 'append' | 'replace'

const CORPUS_BLUEPRINT_CHECKLIST_DIMENSIONS = [
  'emotion_arc',
  'rhythm',
  'technique_diversity',
  'scene_template',
  'source_distribution',
] as const

function corpusBlueprintChecklist(
  decision: 'accepted' | 'revise',
  problemTags: string[] = [],
  notes: string | null = null,
): reference.CorpusBlueprintChecklistItem[] {
  return CORPUS_BLUEPRINT_CHECKLIST_DIMENSIONS.map(dimension => ({
    dimension,
    decision: dimension === 'source_distribution' ? decision : 'accepted',
    problem_tags: dimension === 'source_distribution' ? problemTags : [],
    notes: dimension === 'source_distribution' ? notes : null,
  }))
}

type ActiveChapterContext = {
  path: string
  title: string
  viewMode: string
}

type EditorSnapshot = {
  currentDraftText: string
  insertionOffset: number
}

type ReferenceErrorState = {
  title: string
  message: string
  diagnostic: diagnostics.CopyableDiagnostic
}

type CorpusWritingRequestContext = {
  naturalGoal: string
  chapterContext: reference.CurrentChapterContext
  scope: reference.CorpusScope
}

type CorpusBlueprintRetryAction = {
  input: reference.AdvanceCorpusBlueprintSessionInput
  feedbackLabel?: string
}

interface Props {
  novelId: number
  activeChapter: ActiveChapterContext | null
  onInsertCandidate: (text: string, mode: CandidateInsertMode) => boolean
  getEditorSnapshot: () => EditorSnapshot | null
  onApplyChapterText: (nextContent: string) => boolean
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

const CORPUS_BLUEPRINT_DIAGNOSTIC_LABELS: Record<string, string> = {
  initial_candidate: '初始候选',
  feedback_filters_no_matches: '严格反馈约束没有命中可用语料',
  fallback_to_base_filters: '已退回目标基础检索以保持蓝图循环',
  fallback_base_no_candidates: '退回基础检索后仍无候选',
  avoid_sources_no_alternatives: '避开来源后没有可替代候选',
  fallback_ignored_avoid_sources: '已临时放宽避开来源约束',
  rejected_nodes_exhausted_candidates: '拒绝节点耗尽候选',
  insufficient_beats: '可用节拍不足',
  single_library_source: '当前方案仍集中在单一语料库',
  single_anchor_source: '当前方案仍集中在同一参考来源',
  missing_emotion_evidence: '缺少情绪证据',
  missing_rhythm_evidence: '缺少节奏证据',
  missing_narrative_evidence: '缺少叙事功能证据',
  missing_technique_coverage: '缺少技法标本覆盖',
}

function formatCorpusBlueprintDiagnosticReason(value: string): string {
  const trimmed = value.trim()
  if (!trimmed) return ''
  if (trimmed.startsWith('fallback:')) {
    const reasons = trimmed
      .slice('fallback:'.length)
      .split(',')
      .map(reason => reason.trim())
      .filter(Boolean)
    const readable = reasons
      .map(reason => CORPUS_BLUEPRINT_DIAGNOSTIC_LABELS[reason] ?? reason)
      .join('；')
    return readable ? `回退：${readable}（${trimmed}）` : trimmed
  }

  const label = CORPUS_BLUEPRINT_DIAGNOSTIC_LABELS[trimmed]
  return label ? `${label}（${trimmed}）` : trimmed
}

function formatCorpusBlueprintFeedbackReason(value: string): string {
  return value
    .split(';')
    .map(formatCorpusBlueprintDiagnosticReason)
    .filter(Boolean)
    .join('；')
}

const CORPUS_BLUEPRINT_DIMENSION_LABELS: Record<string, string> = {
  emotion: '情绪',
  rhythm: '节奏',
  narrative: '叙事',
  technique: '技法',
}

function formatCorpusBlueprintDimension(value: string): string {
  const trimmed = value.trim()
  return CORPUS_BLUEPRINT_DIMENSION_LABELS[trimmed] ?? trimmed
}

const CORPUS_DRAFT_STRATEGY_LABELS: Record<string, string> = {
  source_variant_1: '来源变体 1',
  source_variant_2: '来源变体 2',
  source_variant_3: '来源变体 3',
  transition_repair: '转场重组',
  selected_blueprint_empty: '蓝图无来源',
}

function formatCorpusDraftStrategy(value: string): string {
  const trimmed = value.trim()
  if (!trimmed) return '来源变体'
  return CORPUS_DRAFT_STRATEGY_LABELS[trimmed] ?? trimmed
}

const CORPUS_BLUEPRINT_STRATEGY_LABELS: Record<string, string> = {
  emotion_priority_m4: '突出人物情绪变化',
  rhythm_priority_m4: '调整叙事节奏与停顿',
  technique_diversity_m4: '换用不同的叙事技法',
  scene_template_m4: '按场景推进线索',
  source_repetition_diversity_m1: '换用不同来源，保持线索推进',
}

function formatCorpusBlueprintStrategy(value: string): string {
  const trimmed = value.trim()
  if (!trimmed) return '按当前章节目标推进'
  if (CORPUS_BLUEPRINT_STRATEGY_LABELS[trimmed]) {
    return CORPUS_BLUEPRINT_STRATEGY_LABELS[trimmed]
  }

  return /^[a-z0-9_]+$/i.test(trimmed)
    ? '按当前章节目标推进'
    : trimmed
}

function corpusBlueprintBeatSummary(blueprint: reference.CorpusInsertionBlueprint): string {
  const summary = blueprint.beats
    .slice(0, 3)
    .map((beat, index) => beat.narrative_function || beat.role_in_beat || `推进 ${index + 1}`)
    .filter(Boolean)
    .join(' → ')

  return summary || '按当前章节线索推进'
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

function inferPrimaryCharacterSnapshot(
  goal: string,
  draftText: string,
  forbiddenFacts: string[],
): reference.CharacterStateSnapshot[] {
  const source = `${goal}\n${draftText}`
  const actionPrefix = '在|正|刚|又|已|把|将|没有|没|不|只|先|重新|压|停|看|说|问|走|坐|站|抬|低|握|捏|笑|开口|回头|伸手|按|扣|盯|望|转身|沉默'
  const match = source.match(new RegExp(`(?:^|[\\n。！？；，])([\\u4e00-\\u9fff]{2,3})(?=(?:${actionPrefix}))`))
  const character = match?.[1]?.trim()
  if (!character) return []

  return [{
    character,
    state: 'current_chapter_focus',
    allowed_knowledge: [],
    forbidden_knowledge: forbiddenFacts,
  }]
}

export default function ChapterReferencePanel({
  novelId,
  activeChapter,
  onInsertCandidate,
  getEditorSnapshot,
  onApplyChapterText,
  onClose,
}: Props) {
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
  const [corpusBlueprintPath, setCorpusBlueprintPath] = useState('')
  const [corpusBlueprintSession, setCorpusBlueprintSession] = useState<reference.CorpusBlueprintSession | null>(null)
  const [corpusBlueprintSessionLoading, setCorpusBlueprintSessionLoading] = useState(false)
  const [corpusBlueprintCandidates, setCorpusBlueprintCandidates] = useState<reference.CorpusBlueprintCandidates | null>(null)
  const [corpusBlueprintLoading, setCorpusBlueprintLoading] = useState(false)
  const [corpusBlueprintError, setCorpusBlueprintError] = useState<ReferenceErrorState | null>(null)
  const [corpusBlueprintActionMessage, setCorpusBlueprintActionMessage] = useState('')
  const [selectedCorpusBlueprintId, setSelectedCorpusBlueprintId] = useState('')
  const [corpusDraftPath, setCorpusDraftPath] = useState('')
  const [corpusDraftCandidates, setCorpusDraftCandidates] = useState<reference.CorpusInsertionDraftCandidates | null>(null)
  const [selectedCorpusDraftCandidateId, setSelectedCorpusDraftCandidateId] = useState('')
  const [corpusDraftLoading, setCorpusDraftLoading] = useState(false)
  const [corpusDraftError, setCorpusDraftError] = useState<ReferenceErrorState | null>(null)
const [corpusDraftActionMessage, setCorpusDraftActionMessage] = useState('')
 const [expertSlotName, setExpertSlotName] = useState('character')
 const [expertSlotVariantsText, setExpertSlotVariantsText] = useState('林岚\n顾沉')
 const [expertTransitionStrategies, setExpertTransitionStrategies] = useState<string[]>(['default', 'direct_join'])
 const [lockedCorpusDraftCandidateId, setLockedCorpusDraftCandidateId] = useState('')
  const [advancedOpen, setAdvancedOpen] = useState(false)
 const [writingMode, setWritingMode] = useState<'auto' | 'expert'>('auto')
  const [governance, setGovernance] = useState<reference.CorpusGovernance | null>(null)
 const [hydratedRecoveryKey, setHydratedRecoveryKey] = useState('')
 const corpusBlueprintRequestSequenceRef = useRef(0)
 const [corpusBlueprintRetry, setCorpusBlueprintRetry] = useState<CorpusBlueprintRetryAction | null>(null)
 const goalInputRef = useRef<HTMLTextAreaElement | null>(null)

  const recoveryKey = `novelist:corpus-writing:${novelId}:${chapterNumber}:${activePath}`
  const corpusLibrarySessionId = `project:${novelId}:default`
  const corpusBlueprintSessionId = `chapter:${novelId}:${chapterNumber}`

  const loadCorpusBlueprintSession = useCallback(async () => {
    if (!hasValidChapter || !activePath) {
      setCorpusBlueprintSession(null)
      setCorpusBlueprintCandidates(null)
      setSelectedCorpusBlueprintId('')
      return
    }

    setCorpusBlueprintSessionLoading(true)
    setCorpusBlueprintPath(activePath)
    try {
      const session = await app.GetReferenceCorpusBlueprintSession({
        novel_id: novelId,
        chapter_number: chapterNumber,
        session_id: corpusBlueprintSessionId,
      })
      setCorpusBlueprintSession(session)
      setCorpusBlueprintCandidates(session?.candidates ?? null)
      setSelectedCorpusBlueprintId(session?.accepted_blueprint_id || session?.selected_blueprint_id || '')
      if (session?.natural_language_goal?.trim()) {
        setGoal(session.natural_language_goal)
      }
      setCorpusBlueprintError(null)
      setCorpusBlueprintActionMessage(session
        ? session.status === 'accepted'
          ? '已从服务端恢复已确认蓝图，可继续生成正文候选。'
          : `已从服务端恢复第 ${session.iteration} 轮蓝图。`
        : '')
    } catch (caught) {
      const fallbackMessage = '蓝图会话恢复失败'
      setCorpusBlueprintSession(null)
      setCorpusBlueprintCandidates(null)
      setSelectedCorpusBlueprintId('')
      setCorpusBlueprintError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '恢复语料蓝图会话',
          bridgeMethod: 'GetReferenceCorpusBlueprintSession',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
            session_id: corpusBlueprintSessionId,
          },
        }),
      })
    } finally {
      setCorpusBlueprintSessionLoading(false)
    }
  }, [activePath, app, chapterNumber, corpusBlueprintSessionId, hasValidChapter, novelId])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      void loadCorpusBlueprintSession()
    }, 0)
    return () => window.clearTimeout(timer)
  }, [loadCorpusBlueprintSession])

  useEffect(() => {
    const frame = window.requestAnimationFrame(() => goalInputRef.current?.focus())
    return () => window.cancelAnimationFrame(frame)
  }, [])

useEffect(() => {
 queueMicrotask(() => {
 if (!hasValidChapter) { setHydratedRecoveryKey(''); return }
 try {
const saved = window.sessionStorage.getItem(recoveryKey)
 if (saved) {
 const state = JSON.parse(saved) as { goal?: string; writingMode?: 'auto' | 'expert'; selectedDraftId?: string }
 setGoal(state.goal ?? '')
 setWritingMode(state.writingMode ?? 'auto')
 setSelectedCorpusDraftCandidateId(state.selectedDraftId ?? '')
 }
 } catch { window.sessionStorage.removeItem(recoveryKey) }
 finally { setHydratedRecoveryKey(recoveryKey) }
 })
}, [activePath, hasValidChapter, recoveryKey])

useEffect(() => {
 if (!hasValidChapter || hydratedRecoveryKey !== recoveryKey) return
window.sessionStorage.setItem(recoveryKey, JSON.stringify({ goal, writingMode, selectedDraftId: selectedCorpusDraftCandidateId }))
 }, [goal, hasValidChapter, hydratedRecoveryKey, recoveryKey, selectedCorpusDraftCandidateId, writingMode])

 useEffect(() => {
 if (writingMode !== 'expert' || !hasValidChapter) return
 void app.GetReferenceCorpusGovernance({ session_id: `project:${novelId}:default` }).then(setGovernance).catch(() => setGovernance(null))
 }, [app, hasValidChapter, novelId, writingMode])

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
    if (!activePath || !hasValidChapter || !advancedOpen) return
    const timer = window.setTimeout(() => {
      void searchMaterials('auto')
    }, 0)
    return () => window.clearTimeout(timer)
  }, [activePath, advancedOpen, hasValidChapter, searchMaterials])

  useEffect(() => {
    if (!activePath || !hasValidChapter || !advancedOpen) return
    const timer = window.setTimeout(() => {
      void loadLatestRun()
    }, 0)
    return () => window.clearTimeout(timer)
  }, [activePath, advancedOpen, hasValidChapter, loadLatestRun])

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
  const visibleCorpusBlueprintCandidates = corpusBlueprintPath === activePath ? corpusBlueprintCandidates : null
  const visibleCorpusBlueprintError = corpusBlueprintPath === activePath ? corpusBlueprintError : null
  const visibleCorpusBlueprintActionMessage = corpusBlueprintPath === activePath ? corpusBlueprintActionMessage : ''
  const selectedCorpusBlueprintCandidate = useMemo(() => {
    const candidates = visibleCorpusBlueprintCandidates?.candidates ?? []
    if (candidates.length === 0 || !selectedCorpusBlueprintId) return null
    return candidates.find(candidate => candidate.blueprint.blueprint_id === selectedCorpusBlueprintId) ?? null
  }, [selectedCorpusBlueprintId, visibleCorpusBlueprintCandidates])
  const visibleCorpusDraftCandidates = corpusDraftPath === activePath ? corpusDraftCandidates : null
  const selectedCorpusDraftCandidate = useMemo(() => {
    const candidates = visibleCorpusDraftCandidates?.candidates ?? []
    if (candidates.length === 0) return null
    return candidates.find(candidate => candidate.candidate_id === selectedCorpusDraftCandidateId) ?? candidates[0]
  }, [selectedCorpusDraftCandidateId, visibleCorpusDraftCandidates])
  const visibleCorpusDraft = selectedCorpusDraftCandidate?.draft ?? null
  const visibleCorpusDraftError = corpusDraftPath === activePath ? corpusDraftError : null
  const visibleCorpusDraftActionMessage = corpusDraftPath === activePath ? corpusDraftActionMessage : ''

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

  const buildCorpusWritingRequestContext = useCallback((): CorpusWritingRequestContext | null => {
    if (!activePath || !hasValidChapter) return null
    const snapshot = getEditorSnapshot()
    if (!snapshot) {
      return null
    }

    const known = inputLines(knownFacts)
    const forbidden = inputLines(forbiddenFacts)
    const naturalGoal = goal.trim() || activeTitle || `第 ${chapterNumber} 章`
    return {
      naturalGoal,
      chapterContext: {
        novel_id: novelId,
        chapter_number: chapterNumber,
        current_draft_text: snapshot.currentDraftText,
        insertion_offset: snapshot.insertionOffset,
        previous_chapter_summary: known.length > 0 ? known.join('；') : null,
        character_snapshots: inferPrimaryCharacterSnapshot(naturalGoal, snapshot.currentDraftText, forbidden),
      },
      scope: {
        library_ids: [],
        reuse_policies: ['verbatim_ok', 'adapted_only'],
        include_anchor_ids: [],
        exclude_anchor_ids: [],
        session_id: corpusLibrarySessionId,
      },
    }
  }, [activePath, activeTitle, chapterNumber, corpusLibrarySessionId, forbiddenFacts, getEditorSnapshot, goal, hasValidChapter, knownFacts, novelId])

  const nextCorpusBlueprintRequestId = useCallback((action: string) => {
    corpusBlueprintRequestSequenceRef.current += 1
    return `${corpusBlueprintSessionId}:${action}:${Date.now().toString(36)}:${corpusBlueprintRequestSequenceRef.current}`
  }, [corpusBlueprintSessionId])

  const applyCorpusBlueprintSession = useCallback((session: reference.CorpusBlueprintSession, message: string) => {
    setCorpusBlueprintRetry(null)
    setCorpusBlueprintSession(session)
    setCorpusBlueprintPath(activePath)
    setCorpusBlueprintCandidates(session.candidates)
    setSelectedCorpusBlueprintId(session.accepted_blueprint_id || session.selected_blueprint_id || '')
    if (session.natural_language_goal?.trim()) {
      setGoal(session.natural_language_goal)
    }
    setCorpusBlueprintError(null)
    setCorpusBlueprintActionMessage(message)
    setCorpusDraftPath(activePath)
    setCorpusDraftCandidates(null)
    setSelectedCorpusDraftCandidateId('')
    setLockedCorpusDraftCandidateId('')
    setCorpusDraftError(null)
    setCorpusDraftActionMessage('')
  }, [activePath])

  const runCorpusBlueprintAction = useCallback(async (retry: CorpusBlueprintRetryAction) => {
    const { input, feedbackLabel } = retry
    setCorpusBlueprintRetry(retry)
    setCorpusBlueprintLoading(true)
    setCorpusBlueprintError(null)
    try {
      const session = await app.AdvanceReferenceCorpusBlueprintSession(input)
      const message = input.action === 'select'
        ? '已选择蓝图，服务端会话已保存。'
        : input.action === 'revise'
          ? `已按${feedbackLabel ?? '反馈'}重组第 ${session.iteration} 轮蓝图。`
          : input.action === 'accept'
            ? '蓝图已确认。'
            : `已生成 ${session.candidates.candidates.length} 份蓝图候选。`
      applyCorpusBlueprintSession(session, message)
    } catch (caught) {
      const failure = input.action === 'select'
        ? { fallbackMessage: '蓝图选择保存失败', operation: '选择语料蓝图' }
        : input.action === 'revise'
          ? { fallbackMessage: '蓝图反馈重组失败', operation: '反馈重组语料蓝图候选' }
          : input.action === 'accept'
            ? { fallbackMessage: '蓝图确认失败', operation: '确认语料蓝图' }
            : { fallbackMessage: '蓝图候选生成失败', operation: '生成语料蓝图候选' }
      setCorpusBlueprintPath(activePath)
      setCorpusBlueprintError({
        title: failure.fallbackMessage,
        message: diagnosticMessage(caught, failure.fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage: failure.fallbackMessage,
          operation: failure.operation,
          bridgeMethod: 'AdvanceReferenceCorpusBlueprintSession',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
            session_id: input.session_id,
            request_id: input.request_id,
            action: input.action,
            ...(input.selected_blueprint_id ? { blueprint_id: input.selected_blueprint_id } : {}),
          },
        }),
      })
    } finally {
      setCorpusBlueprintLoading(false)
    }
  }, [activePath, app, applyCorpusBlueprintSession, chapterNumber, novelId])

  const retryCorpusBlueprintAction = useCallback(() => {
    if (corpusBlueprintRetry?.input.session_id === corpusBlueprintSessionId) {
      void runCorpusBlueprintAction(corpusBlueprintRetry)
      return
    }
    setCorpusBlueprintRetry(null)
    void loadCorpusBlueprintSession()
  }, [corpusBlueprintRetry, corpusBlueprintSessionId, loadCorpusBlueprintSession, runCorpusBlueprintAction])

  const generateCorpusBlueprintCandidates = useCallback(async (
    feedback: reference.CorpusBlueprintFeedback | null = null,
    feedbackLabel = '反馈',
  ) => {
    if (!activePath || !hasValidChapter || corpusBlueprintSessionLoading) return
    const context = buildCorpusWritingRequestContext()
    if (!context) {
      const fallbackMessage = '蓝图候选生成失败'
      setCorpusBlueprintPath(activePath)
      setCorpusBlueprintCandidates(null)
      setSelectedCorpusBlueprintId('')
      setCorpusBlueprintActionMessage('')
      setCorpusBlueprintError({
        title: fallbackMessage,
        message: '无法读取当前正文缓冲区。请切回章节正文编辑器后重试。',
        diagnostic: buildCopyableDiagnostic({
          fallbackMessage,
          operation: '生成语料蓝图候选',
          bridgeMethod: 'AdvanceReferenceCorpusBlueprintSession',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
            session_id: corpusBlueprintSessionId,
          },
        }),
      })
      return
    }

    const action = feedback ? 'revise' : 'generate'
    if (!feedback && corpusBlueprintSession) {
      setCorpusBlueprintPath(activePath)
      setCorpusBlueprintError({
        title: '蓝图会话已存在',
        message: corpusBlueprintSession.status === 'accepted'
          ? '当前章节的蓝图已确认。请继续生成正文候选，或切换章节开始新的写作任务。'
          : '当前章节已有可恢复蓝图。请先选择方案、反馈重组或继续生成正文候选。',
        diagnostic: buildCopyableDiagnostic({
          fallbackMessage: '蓝图会话已存在',
          operation: '生成语料蓝图候选',
          bridgeMethod: 'AdvanceReferenceCorpusBlueprintSession',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
            session_id: corpusBlueprintSessionId,
            status: corpusBlueprintSession.status,
          },
        }),
      })
      return
    }

    if (feedback && (!corpusBlueprintSession || corpusBlueprintSession.status === 'accepted' || !selectedCorpusBlueprintCandidate)) {
      setCorpusBlueprintPath(activePath)
      setCorpusBlueprintError({
        title: '蓝图反馈重组失败',
        message: '请先选择当前会话中的一份蓝图；已确认的蓝图不能再修改。',
        diagnostic: buildCopyableDiagnostic({
          fallbackMessage: '蓝图反馈重组失败',
          operation: '反馈重组语料蓝图候选',
          bridgeMethod: 'AdvanceReferenceCorpusBlueprintSession',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
            session_id: corpusBlueprintSessionId,
          },
        }),
      })
      return
    }

    const request: reference.AdvanceCorpusBlueprintSessionInput = {
      session_id: corpusBlueprintSessionId,
      request_id: nextCorpusBlueprintRequestId(action),
      action,
      generation_input: {
        natural_language_goal: context.naturalGoal,
        chapter_context: context.chapterContext,
        scope: context.scope,
        requested_count: 3,
      },
      selected_blueprint_id: feedback ? selectedCorpusBlueprintCandidate?.blueprint.blueprint_id ?? null : null,
      checklist: feedback
        ? corpusBlueprintChecklist('revise', feedback.problem_tags ?? [], feedback.notes || null)
        : null,
    }
    await runCorpusBlueprintAction({
      input: request,
      feedbackLabel: feedback ? feedbackLabel : undefined,
    })
  }, [activePath, buildCorpusWritingRequestContext, chapterNumber, corpusBlueprintSession, corpusBlueprintSessionId, corpusBlueprintSessionLoading, hasValidChapter, nextCorpusBlueprintRequestId, novelId, runCorpusBlueprintAction, selectedCorpusBlueprintCandidate])

  const selectCorpusBlueprintCandidate = useCallback(async (candidate: reference.CorpusBlueprintCandidate) => {
    if (!corpusBlueprintSession || corpusBlueprintSession.status === 'accepted' || corpusBlueprintSessionLoading) return
    const request: reference.AdvanceCorpusBlueprintSessionInput = {
      session_id: corpusBlueprintSessionId,
      request_id: nextCorpusBlueprintRequestId('select'),
      action: 'select',
      selected_blueprint_id: candidate.blueprint.blueprint_id,
    }
    await runCorpusBlueprintAction({ input: request })
  }, [corpusBlueprintSession, corpusBlueprintSessionId, corpusBlueprintSessionLoading, nextCorpusBlueprintRequestId, runCorpusBlueprintAction])

  const regenerateCorpusBlueprintCandidatesFromSelection = useCallback(async () => {
    if (!selectedCorpusBlueprintCandidate) {
      const fallbackMessage = '蓝图反馈重组失败'
      setCorpusBlueprintPath(activePath)
      setCorpusBlueprintError({
        title: fallbackMessage,
        message: '当前没有可反馈的蓝图候选。',
        diagnostic: buildCopyableDiagnostic({
          fallbackMessage,
          operation: '反馈重组语料蓝图候选',
          bridgeMethod: 'AdvanceReferenceCorpusBlueprintSession',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
      return
    }

    await generateCorpusBlueprintCandidates({
      rejected_blueprint_ids: [selectedCorpusBlueprintCandidate.blueprint.blueprint_id],
      rejected_node_ids: [],
      avoid_library_ids: [],
      avoid_anchor_ids: [],
      problem_tags: ['source_repetition'],
      notes: '上一轮蓝图不合适，请换一组来源和节奏。',
    })
  }, [activePath, chapterNumber, generateCorpusBlueprintCandidates, novelId, selectedCorpusBlueprintCandidate])

  const regenerateCorpusBlueprintCandidatesFromDraftAction = useCallback(async (candidate: reference.CorpusInsertionDraftCandidate) => {
    const nextAction = candidate.next_action
    if (!nextAction || nextAction.action !== 'regenerate_blueprint') {
      const fallbackMessage = '正文候选诊断重组失败'
      setCorpusBlueprintPath(activePath)
      setCorpusBlueprintError({
        title: fallbackMessage,
        message: '当前正文候选没有可执行的蓝图重组建议。',
        diagnostic: buildCopyableDiagnostic({
          fallbackMessage,
          operation: '按正文候选诊断重组语料蓝图候选',
          bridgeMethod: 'AdvanceReferenceCorpusBlueprintSession',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
            candidate_id: candidate.candidate_id,
            next_action: nextAction?.action ?? null,
          },
        }),
      })
      return
    }

    await generateCorpusBlueprintCandidates(nextAction.feedback, '正文候选诊断')
  }, [activePath, chapterNumber, generateCorpusBlueprintCandidates, novelId])

  const generateCorpusDraft = useCallback(async () => {
    if (!activePath || !hasValidChapter) return
    if (!selectedCorpusBlueprintCandidate) {
      const fallbackMessage = '语料草稿生成失败'
      setCorpusDraftPath(activePath)
      setCorpusDraftCandidates(null)
      setSelectedCorpusDraftCandidateId('')
      setCorpusDraftActionMessage('')
      setCorpusDraftError({
        title: fallbackMessage,
        message: '请先生成并选择一份蓝图候选。',
        diagnostic: buildCopyableDiagnostic({
          fallbackMessage,
          operation: '生成语料驱动插入草稿',
          bridgeMethod: 'GenerateReferenceCorpusInsertionDraftCandidates',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
      return
    }

    const context = buildCorpusWritingRequestContext()
    if (!context) {
      const fallbackMessage = '语料草稿生成失败'
      setCorpusDraftPath(activePath)
      setCorpusDraftCandidates(null)
      setSelectedCorpusDraftCandidateId('')
      setCorpusDraftActionMessage('')
      setCorpusDraftError({
        title: fallbackMessage,
        message: '无法读取当前正文缓冲区。请切回章节正文编辑器后重试。',
        diagnostic: buildCopyableDiagnostic({
          fallbackMessage,
          operation: '生成语料驱动插入草稿',
          bridgeMethod: 'GenerateReferenceCorpusInsertionDraftCandidates',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
      return
    }

    setCorpusDraftLoading(true)
    setCorpusDraftPath(activePath)
    setCorpusDraftCandidates(null)
    setSelectedCorpusDraftCandidateId('')
    setCorpusDraftError(null)
    setCorpusDraftActionMessage('')
    try {
const result = await app.GenerateReferenceCorpusInsertionDraftCandidates({
        natural_language_goal: context.naturalGoal,
        chapter_context: context.chapterContext,
        scope: context.scope,
 slot_values: {},
selected_blueprint: selectedCorpusBlueprintCandidate.blueprint,
 requested_count: writingMode === 'expert' ? 6 : 3,
 ...(writingMode === 'expert' ? {
 slot_value_variants: expertSlotVariantsText
 .split(/\r?\n|，|,/)
 .map(value => value.trim())
 .filter(Boolean)
 .slice(0, 4)
 .map((value, index) => ({
 variant_id: `expert-slot-${index + 1}`,
 label: `${expertSlotName || 'slot'}=${value}`,
 slot_values: { [expertSlotName.trim() || 'character']: value },
 })),
 transition_strategy_variants: expertTransitionStrategies,
 } : {}),
})
      setCorpusDraftCandidates(result)
      setSelectedCorpusDraftCandidateId(result.candidates[0]?.candidate_id ?? '')
      setCorpusDraftActionMessage(result.candidates.length > 0
        ? `已生成 ${result.candidates.length} 份正文候选。`
        : '未生成可用正文候选。')
    } catch (caught) {
      const fallbackMessage = '语料草稿生成失败'
      setCorpusDraftCandidates(null)
      setSelectedCorpusDraftCandidateId('')
      setCorpusDraftError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '生成语料驱动插入草稿',
          bridgeMethod: 'GenerateReferenceCorpusInsertionDraftCandidates',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
            session_id: `project:${novelId}:default`,
          },
        }),
      })
    } finally {
      setCorpusDraftLoading(false)
    }
 }, [activePath, app, buildCorpusWritingRequestContext, chapterNumber, expertSlotName, expertSlotVariantsText, expertTransitionStrategies, hasValidChapter, novelId, selectedCorpusBlueprintCandidate, writingMode])

  const applyCorpusDraft = useCallback(async () => {
 if (!visibleCorpusDraft || !selectedCorpusDraftCandidate) return
 if (!corpusDraftCanApply(visibleCorpusDraft)) {
setCorpusDraftActionMessage('草稿审计未通过，不能应用到编辑器。')
return
}
 if (writingMode === 'expert' && lockedCorpusDraftCandidateId !== selectedCorpusDraftCandidate.candidate_id) {
 setCorpusDraftActionMessage('专家模式需要先锁定当前正文候选，再确认写入。')
 return
 }

 setCorpusDraftActionMessage('正在执行服务端授权与相似度复核…')
 try {
 await app.RecordReferenceCorpusInsertionAudit({
 audit_id: `chapter-${novelId}-${chapterNumber}-${selectedCorpusDraftCandidate.candidate_id}`,
 session_id: `project:${novelId}:default`,
 novel_id: novelId,
 chapter_number: chapterNumber,
 candidate_id: selectedCorpusDraftCandidate.candidate_id,
 draft: visibleCorpusDraft,
 })
 const applied = onApplyChapterText(visibleCorpusDraft.chapter_text_after_insertion)
 setCorpusDraftActionMessage(applied ? '服务端审计通过，已应用到当前章节编辑器缓冲区。' : '审计已记录，但无法应用草稿；请切回正文编辑器后重试。')
 } catch (caught) {
 setCorpusDraftActionMessage(`服务端审计拒绝应用：${diagnosticMessage(caught, '授权或相似度复核失败')}`)
 }
 }, [app, chapterNumber, lockedCorpusDraftCandidateId, novelId, onApplyChapterText, selectedCorpusDraftCandidate, visibleCorpusDraft, writingMode])

 const copyCorpusDraft = useCallback(async () => {
    if (!visibleCorpusDraft) return
    try {
      await copyTextToClipboard(visibleCorpusDraft.assembled_text)
      setCorpusDraftActionMessage('已复制语料草稿片段。')
    } catch (caught) {
      const fallbackMessage = '语料草稿复制失败'
      setCorpusDraftError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '复制语料驱动插入草稿',
          bridgeMethod: 'clipboard.writeText',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: activePath,
          },
        }),
      })
    }
  }, [activePath, chapterNumber, novelId, visibleCorpusDraft])

const selectCorpusDraftCandidate = useCallback((candidate: reference.CorpusInsertionDraftCandidate) => {
setSelectedCorpusDraftCandidateId(candidate.candidate_id)
 setLockedCorpusDraftCandidateId('')
setCorpusDraftActionMessage(`已选择正文候选 ${candidate.candidate_id}`)
}, [])

  return (
    <aside
      data-testid="chapter-reference-panel"
      className={`flex h-full shrink-0 flex-col border-l bg-card max-[1100px]:fixed max-[1100px]:inset-x-0 max-[1100px]:top-11 max-[1100px]:bottom-6 max-[1100px]:z-40 max-[1100px]:h-auto max-[1100px]:w-auto max-[1100px]:max-w-none max-[1100px]:border-l-0 max-[1100px]:shadow-lg ${writingMode === 'expert' ? 'w-[760px] max-w-[70vw]' : 'w-[360px] max-w-[40vw]'}`}
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
 <div className="flex items-center justify-between gap-2">
 <span className="text-[11px] font-medium text-muted-foreground">写作模式</span>
 <div className="inline-flex rounded border border-border bg-background p-0.5" data-testid="chapter-writing-mode">
 {(['auto', 'expert'] as const).map(mode => (
 <button
 key={mode}
 type="button"
 onClick={() => setWritingMode(mode)}
 className={`rounded px-2 py-1 text-[11px] ${writingMode === mode ? 'bg-secondary font-medium text-foreground' : 'text-muted-foreground hover:text-foreground'}`}
 >
 {mode === 'auto' ? '自动' : '专家'}
 </button>
 ))}
 </div>
 </div>
        {writingMode === 'expert' && (
 <div data-testid="chapter-writing-expert-context" className="grid grid-cols-3 gap-2 rounded border bg-background p-2 text-[11px]">
 <div><p className="font-semibold">阶段进度</p><p>目标 {goal.trim() ? '✓' : '○'} · 蓝图 {selectedCorpusBlueprintCandidate ? '✓' : '○'} · 正文 {visibleCorpusDraft ? '✓' : '○'} · 审计 {visibleCorpusDraft?.ready_for_insertion ? '待确认' : '○'}</p></div>
 <div><p className="font-semibold">当前章节</p><p className="truncate">第 {chapterNumber || '—'} 章 · {activeTitle || activePath || '未选择'}</p></div>
 <div><p className="font-semibold">生效语料库</p><p>{governance?.libraries.filter(library => library.bound_to_session).map(library => library.name).join('、') || '未加载'}</p></div>
 </div>
 )}

 {!hasValidChapter && (
          <div className="rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs leading-relaxed text-amber-800 dark:text-amber-200">
            当前文件无法可靠推导章节号。请切换到 `chapters/001.md` 这类章节正文后再使用参考素材。
          </div>
        )}

        <section className="space-y-2">
          <h3 className="text-xs font-semibold text-foreground">章节目标</h3>
          <label className="block text-xs text-muted-foreground">
            <textarea
              ref={goalInputRef}
              value={goal}
              onChange={event => setGoal(event.target.value)}
              aria-label="章节目标"
              className="mt-1 min-h-16 w-full resize-y rounded border border-input bg-background px-2 py-1.5 text-xs text-foreground outline-none focus:ring-2 focus:ring-ring"
              placeholder="可留空，系统会先按章节标题和可访问素材推荐"
            />
          </label>
        </section>

        <section
          data-testid="chapter-corpus-insertion"
          className="space-y-2"
          aria-busy={corpusBlueprintSessionLoading || corpusBlueprintLoading || corpusDraftLoading}
        >
          <div className="space-y-2">
            <div className="flex items-center justify-between gap-2">
              <h3 className="text-xs font-semibold text-foreground">语料驱动草稿</h3>
              <button
                type="button"
                data-testid="chapter-corpus-blueprint-generate-button"
                onClick={() => {
                  void generateCorpusBlueprintCandidates()
                }}
                disabled={!hasValidChapter || corpusBlueprintSessionLoading || corpusBlueprintLoading || corpusDraftLoading || Boolean(corpusBlueprintSession)}
                className="inline-flex shrink-0 items-center gap-1 rounded bg-primary px-2 py-1 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50"
              >
                {corpusBlueprintSessionLoading || corpusBlueprintLoading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Wand2 className="h-3.5 w-3.5" />}
                {corpusBlueprintSessionLoading
                  ? '正在恢复会话'
                  : corpusBlueprintSession?.status === 'accepted'
                    ? '蓝图已确认'
                    : corpusBlueprintSession
                      ? selectedCorpusBlueprintCandidate ? '已保存蓝图' : '请选择蓝图'
                      : '生成蓝图候选'}
              </button>
            </div>

            <div className="rounded border border-border bg-background px-2.5 py-2 text-[11px] leading-relaxed text-muted-foreground">
              按当前章节会话跨已启用语料库检索，先生成多份蓝图；选中满意蓝图后再生成可插入草稿。
            </div>

            {corpusBlueprintSessionLoading && (
              <p
                data-testid="chapter-corpus-blueprint-session-loading"
                role="status"
                aria-live="polite"
                className="rounded bg-secondary/70 px-2 py-1 text-[11px] text-muted-foreground"
              >
                正在恢复本章上次未完成的写作会话…
              </p>
            )}
          </div>

          {visibleCorpusBlueprintError && (
            <ErrorCallout
              compact
              title={visibleCorpusBlueprintError.title}
              message={visibleCorpusBlueprintError.message}
              diagnostic={visibleCorpusBlueprintError.diagnostic}
              className="rounded-md"
              onRetry={() => {
                retryCorpusBlueprintAction()
              }}
              retryLabel="重试当前操作"
              retrying={corpusBlueprintLoading || corpusBlueprintSessionLoading}
              onClose={() => {
                setCorpusBlueprintRetry(null)
                setCorpusBlueprintError(null)
              }}
            />
          )}

          {visibleCorpusBlueprintActionMessage && (
            <p role="status" aria-live="polite" className="rounded bg-secondary/70 px-2 py-1 text-[11px] text-muted-foreground">{visibleCorpusBlueprintActionMessage}</p>
          )}

          {writingMode === 'expert' && visibleCorpusBlueprintCandidates?.feedback_applied && (
            <p
              data-testid="chapter-corpus-blueprint-feedback-summary"
              className="rounded border border-emerald-500/30 bg-emerald-500/10 px-2 py-1 text-[11px] leading-relaxed text-emerald-700 dark:text-emerald-300"
            >
              反馈已应用：{visibleCorpusBlueprintCandidates.feedback_summary || 'feedback_applied'}
            </p>
          )}

          {visibleCorpusBlueprintCandidates ? (
            <CorpusBlueprintCandidateList
              result={visibleCorpusBlueprintCandidates}
              selectedCandidate={selectedCorpusBlueprintCandidate}
              loading={corpusBlueprintSessionLoading || corpusBlueprintLoading}
              expert={writingMode === 'expert'}
              onSelect={selectCorpusBlueprintCandidate}
              onRegenerate={() => {
                void regenerateCorpusBlueprintCandidatesFromSelection()
              }}
            />
) : !corpusBlueprintLoading && !visibleCorpusBlueprintError ? (
<div className="rounded border border-dashed border-border bg-background px-3 py-3 text-xs leading-relaxed text-muted-foreground">
输入章节目标后生成多份参考写作蓝图。这里不会处理素材库，只消费当前章节会话可访问的公共语料库。
</div>
) : null}

 {writingMode === 'expert' && selectedCorpusBlueprintCandidate && (
 <CorpusExpertDraftControls
 slotName={expertSlotName}
 slotVariantsText={expertSlotVariantsText}
 transitionStrategies={expertTransitionStrategies}
 onSlotNameChange={setExpertSlotName}
 onSlotVariantsTextChange={setExpertSlotVariantsText}
 onTransitionStrategiesChange={setExpertTransitionStrategies}
 />
 )}

          <button
            type="button"
            data-testid="chapter-corpus-draft-generate-button"
            onClick={() => {
              void generateCorpusDraft()
            }}
            disabled={!hasValidChapter || corpusBlueprintSessionLoading || corpusDraftLoading || corpusBlueprintLoading || !selectedCorpusBlueprintCandidate}
            className="inline-flex w-full items-center justify-center gap-1.5 rounded bg-secondary px-3 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
          >
            {corpusDraftLoading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <CornerDownLeft className="h-3.5 w-3.5" />}
            按选中蓝图生成草稿
          </button>

          {visibleCorpusDraftError && (
            <ErrorCallout
              compact
              title={visibleCorpusDraftError.title}
              message={visibleCorpusDraftError.message}
              diagnostic={visibleCorpusDraftError.diagnostic}
              className="rounded-md"
              onRetry={() => {
                void generateCorpusDraft()
              }}
              retryLabel="重试生成草稿"
              onClose={() => setCorpusDraftError(null)}
            />
          )}

          {visibleCorpusDraftActionMessage && (
            <p className="rounded bg-secondary/70 px-2 py-1 text-[11px] text-muted-foreground">{visibleCorpusDraftActionMessage}</p>
          )}

          {visibleCorpusDraftCandidates ? (
            <div className="space-y-2">
<CorpusInsertionDraftCandidateList
result={visibleCorpusDraftCandidates}
selectedCandidate={selectedCorpusDraftCandidate}
expert={writingMode === 'expert'}
onSelect={selectCorpusDraftCandidate}
onNextAction={regenerateCorpusBlueprintCandidatesFromDraftAction}
/>
 {writingMode === 'expert' && (
 <CorpusDraftComparison
 result={visibleCorpusDraftCandidates}
 selectedCandidateId={selectedCorpusDraftCandidate?.candidate_id ?? ''}
 lockedCandidateId={lockedCorpusDraftCandidateId}
 onSelect={selectCorpusDraftCandidate}
 onLock={candidateId => {
 setSelectedCorpusDraftCandidateId(candidateId)
 setLockedCorpusDraftCandidateId(candidateId)
 setCorpusDraftActionMessage(`已锁定正文候选 ${candidateId}，可确认写入。`)
 }}
 />
 )}
{visibleCorpusDraft && (
                <>
                  <CorpusInsertionDraftPreview
                    draft={visibleCorpusDraft}
                    expert={writingMode === 'expert'}
 insertionDisabled={insertionDisabled || (writingMode === 'expert' && lockedCorpusDraftCandidateId !== selectedCorpusDraftCandidate?.candidate_id)}
                    onApply={() => { void applyCorpusDraft() }}
                    onCopy={() => {
                      void copyCorpusDraft()
                    }}
                  />
                  <CorpusAnalysisPanel
                    novelId={novelId}
                    draft={visibleCorpusDraft}
                    chapterNumber={chapterNumber}
                    chapterPath={activePath}
                  />
                </>
              )}
            </div>
          ) : !corpusDraftLoading && !visibleCorpusDraftError ? (
            <div className="rounded border border-dashed border-border bg-background px-3 py-3 text-xs leading-relaxed text-muted-foreground">
              输入章节目标后可生成多份可比较正文候选；默认按当前章节会话检索已启用语料库与工作区公用语料。
            </div>
          ) : null}
        </section>

        <details
          data-testid="chapter-reference-advanced"
          open={advancedOpen}
          onToggle={event => setAdvancedOpen(event.currentTarget.open)}
          className="rounded border border-border bg-background"
        >
          <summary className="cursor-pointer select-none px-3 py-2 text-xs font-semibold text-foreground">
            高级参考流程
          </summary>
          <div className="space-y-3 border-t border-border px-3 py-3">
            <section className="space-y-2">
              <h3 className="text-xs font-semibold text-foreground">事实边界</h3>
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
                  展开后会自动推荐可访问素材。
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
        </details>
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

type CorpusAnalysisTarget = {
  key: string
  label: string
  anchorId: number
  nodeId: string
  textHash: string
}

function CorpusAnalysisPanel({
  novelId,
  draft,
  chapterNumber,
  chapterPath,
}: {
  novelId: number
  draft: reference.CorpusInsertionDraft
  chapterNumber: number
  chapterPath: string
}) {
  const app = useApp()
  const requestRef = useRef(0)
  const targets = useMemo<CorpusAnalysisTarget[]>(() => {
    const seen = new Set<string>()
    return draft.pieces
      .map((piece, index) => ({
        key: `${piece.anchor_id}:${piece.node_id}`,
        label: `${index + 1}. ${piece.node_id}`,
        anchorId: piece.anchor_id,
        nodeId: piece.node_id,
        textHash: piece.text_hash,
      }))
      .filter(target => {
        if (!target.nodeId || seen.has(target.key)) return false
        seen.add(target.key)
        return true
      })
  }, [draft.pieces])
  const [selectedKey, setSelectedKey] = useState('')
  const selectedTarget = targets.find(target => target.key === selectedKey) ?? targets[0] ?? null
  const [observations, setObservations] = useState<reference.CorpusFeatureObservation[]>([])
  const [specimens, setSpecimens] = useState<reference.CorpusTechniqueSpecimen[]>([])
  const [totals, setTotals] = useState({ observations: 0, specimens: 0 })
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<ReferenceErrorState | null>(null)

  const loadAnalysis = useCallback(async (target: CorpusAnalysisTarget) => {
    const requestId = requestRef.current + 1
    requestRef.current = requestId
    setLoading(true)
    setError(null)
    try {
      const [observationPage, specimenPage] = await Promise.all([
        app.ListReferenceCorpusFeatureObservations({
          novel_id: novelId,
          anchor_id: target.anchorId,
          node_id: target.nodeId,
          page_request: {
            cursor: null,
            page_size: 8,
            sort_by: 'feature_family',
            sort_dir: 'asc',
            filters: { validity_state: 'active' },
          },
        }),
        app.ListReferenceCorpusTechniqueSpecimens({
          novel_id: novelId,
          anchor_id: target.anchorId,
          source_node_id: target.nodeId,
          page_request: {
            cursor: null,
            page_size: 5,
            sort_by: 'confidence',
            sort_dir: 'desc',
            filters: { validity_state: 'active' },
          },
        }),
      ])
      if (requestRef.current !== requestId) return
      setObservations(observationPage.items ?? [])
      setSpecimens(specimenPage.items ?? [])
      setTotals({
        observations: observationPage.total_estimate ?? observationPage.total ?? observationPage.items.length,
        specimens: specimenPage.total_estimate ?? specimenPage.total ?? specimenPage.items.length,
      })
    } catch (caught) {
      if (requestRef.current !== requestId) return
      const fallbackMessage = '节点分析加载失败'
      setObservations([])
      setSpecimens([])
      setTotals({ observations: 0, specimens: 0 })
      setError({
        title: fallbackMessage,
        message: diagnosticMessage(caught, fallbackMessage),
        diagnostic: buildCopyableDiagnostic({
          error: caught,
          fallbackMessage,
          operation: '加载当前章节语料节点分析',
          bridgeMethod: 'ListReferenceCorpusFeatureObservations/ListReferenceCorpusTechniqueSpecimens',
          detail: {
            novel_id: novelId,
            chapter_number: chapterNumber,
            chapter_path: chapterPath,
            anchor_id: target.anchorId,
            node_id: target.nodeId,
          },
        }),
      })
    } finally {
      if (requestRef.current === requestId) {
        setLoading(false)
      }
    }
  }, [app, chapterNumber, chapterPath, novelId])

  useEffect(() => {
    if (!selectedTarget) return
    const timer = window.setTimeout(() => {
      void loadAnalysis(selectedTarget)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [loadAnalysis, selectedTarget])

  if (targets.length === 0 || !selectedTarget) {
    return null
  }

  return (
    <section data-testid="chapter-corpus-analysis-panel" className="rounded border border-border bg-background text-xs">
<div className="flex items-center justify-between gap-2 border-b border-border px-2.5 py-2">
        <div className="min-w-0">
          <h3 className="truncate text-xs font-semibold text-foreground">节点分析 / 技法标本</h3>
          <p className="mt-0.5 truncate text-[11px] text-muted-foreground">
            {selectedTarget.nodeId} · {selectedTarget.textHash}
          </p>
        </div>
        <button
          type="button"
          onClick={() => {
            void loadAnalysis(selectedTarget)
          }}
          disabled={loading}
          className="inline-flex shrink-0 items-center gap-1 rounded border border-border px-2 py-1 text-[11px] text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
        >
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <FileSearch className="h-3.5 w-3.5" />}
          刷新
</button>
</div>

{targets.length > 1 && (
        <div className="flex gap-1 overflow-x-auto border-b border-border px-2.5 py-2">
          {targets.map(target => (
            <button
              key={target.key}
              type="button"
              onClick={() => setSelectedKey(target.key)}
              className={`max-w-[150px] shrink-0 truncate rounded px-2 py-1 text-[11px] ${target.key === selectedTarget.key ? 'bg-primary text-primary-foreground' : 'bg-secondary text-muted-foreground hover:text-foreground'}`}
              title={target.nodeId}
            >
              {target.label}
            </button>
          ))}
        </div>
      )}

      <div className="space-y-3 px-2.5 py-2">
        {error && (
          <ErrorCallout
            compact
            title={error.title}
            message={error.message}
            diagnostic={error.diagnostic}
            className="rounded-md"
            onRetry={() => {
              void loadAnalysis(selectedTarget)
            }}
            retryLabel="重试加载分析"
            onClose={() => setError(null)}
          />
        )}

        {loading && !error ? (
          <div className="rounded border border-border bg-card px-3 py-3 text-center text-[11px] text-muted-foreground">
            正在加载分析结果...
          </div>
        ) : null}

        {!loading && !error && (
          <>
            <div>
              <div className="flex items-center justify-between gap-2 text-[11px] text-muted-foreground">
                <span className="font-medium text-foreground">观察维度</span>
                <span>{totals.observations}</span>
              </div>
              {observations.length > 0 ? (
                <div className="mt-1 divide-y divide-border rounded border border-border">
                  {observations.map(observation => (
                    <div key={observation.observation_id} className="space-y-1 px-2 py-1.5">
                      <div className="flex items-center justify-between gap-2">
                        <span className="min-w-0 truncate font-medium text-foreground">
                          {observation.feature_family}.{observation.feature_key}
                        </span>
                        <span className="shrink-0 text-[11px] text-muted-foreground">{formatConfidence(observation.confidence)}</span>
                      </div>
                      {observation.value_preview && (
                        <p className="leading-relaxed text-foreground">{observation.value_preview}</p>
                      )}
                      {(observation.evidence_preview || observation.explanation) && (
                        <p className="leading-relaxed text-muted-foreground">
                          {[observation.evidence_preview, observation.explanation].filter(Boolean).join(' · ')}
                        </p>
                      )}
                    </div>
                  ))}
                </div>
              ) : (
                <p className="mt-1 rounded border border-dashed border-border px-2 py-2 text-[11px] leading-relaxed text-muted-foreground">
                  当前节点暂无观察结果。
                </p>
              )}
            </div>

            <div>
              <div className="flex items-center justify-between gap-2 text-[11px] text-muted-foreground">
                <span className="font-medium text-foreground">技法标本</span>
                <span>{totals.specimens}</span>
              </div>
              {specimens.length > 0 ? (
                <div className="mt-1 divide-y divide-border rounded border border-border">
                  {specimens.map(specimen => (
                    <div key={specimen.specimen_id} className="space-y-1.5 px-2 py-2">
                      <div className="flex items-center justify-between gap-2">
                        <span className="min-w-0 truncate font-medium text-foreground">{specimen.technique_family}</span>
                        <span className="shrink-0 text-[11px] text-muted-foreground">{formatConfidence(specimen.confidence)}</span>
                      </div>
                      <p className="leading-relaxed text-foreground">{specimen.technique_abstract}</p>
                      <p className="rounded bg-secondary/60 px-2 py-1 leading-relaxed text-muted-foreground">{specimen.transfer_template}</p>
                      {specimen.why_it_works.contributing_factors.slice(0, 2).map(factor => (
                        <div key={`${specimen.specimen_id}:${factor.factor}`} className="space-y-1">
                          <p className="leading-relaxed text-muted-foreground">{factor.factor} · {factor.explanation}</p>
                          {factor.evidence.length > 0 && (
                            <p className="break-words text-[11px] leading-relaxed text-muted-foreground">
                              {factor.evidence.map(item => `${item.feature_family}.${item.feature_key}`).join(' / ')}
                            </p>
                          )}
                        </div>
                      ))}
                      {!specimen.why_it_works.trace_complete && (
                        <p className="rounded border border-amber-500/30 bg-amber-500/10 px-2 py-1 text-[11px] text-amber-800 dark:text-amber-200">
                          证据链不完整
                        </p>
                      )}
                    </div>
                  ))}
                </div>
              ) : (
                <p className="mt-1 rounded border border-dashed border-border px-2 py-2 text-[11px] leading-relaxed text-muted-foreground">
                  当前节点暂无技法标本。
                </p>
              )}
            </div>
          </>
        )}
      </div>
    </section>
  )
}

function CorpusBlueprintCandidateList({
  result,
  selectedCandidate,
  loading,
  expert,
  onSelect,
  onRegenerate,
}: {
  result: reference.CorpusBlueprintCandidates
  selectedCandidate: reference.CorpusBlueprintCandidate | null
  loading: boolean
  expert: boolean
  onSelect: (candidate: reference.CorpusBlueprintCandidate) => void
  onRegenerate: () => void
}) {
  const selectedId = selectedCandidate?.blueprint.blueprint_id ?? ''
  const candidates = result.candidates ?? []

  return (
    <section
      data-testid="chapter-corpus-blueprint-candidates"
      className="rounded border border-border bg-background text-xs"
      aria-busy={loading}
    >
      <div className="flex items-center justify-between gap-2 border-b border-border px-2.5 py-2">
        <div className="min-w-0">
          <h4 className="truncate font-semibold text-foreground">蓝图候选</h4>
          <p className="mt-0.5 truncate text-[11px] text-muted-foreground">
            {expert
              ? `${result.query_context.scene_type || 'story_context'} · ${result.query_context.pacing_target || 'auto'} · ${candidates.length} 份`
              : `比较 ${candidates.length} 种推进方式，选择最适合本章的一种。`}
          </p>
        </div>
        <button
          type="button"
          data-testid="chapter-corpus-blueprint-feedback-button"
          onClick={onRegenerate}
          disabled={!selectedCandidate || loading}
          className="inline-flex shrink-0 items-center gap-1 rounded border border-border px-2 py-1 text-[11px] text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
        >
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
          {expert ? '反馈重组' : '换一组方案'}
        </button>
</div>

 {expert && result.iteration && (
 <div data-testid="chapter-corpus-blueprint-iteration" className="border-b border-border px-2.5 py-1.5 text-[11px] text-muted-foreground">
 第 {result.iteration.iteration} 轮 · {result.iteration.state} · 独立候选 {result.iteration.distinct_candidate_count}/{result.iteration.candidate_count}
 </div>
 )}

{candidates.length > 0 ? (
        <div className="divide-y divide-border">
          {candidates.map((candidate, index) => {
            const blueprint = candidate.blueprint
            const isSelected = blueprint.blueprint_id === selectedId
            const feedbackReason = candidate.feedback_reason
              ? formatCorpusBlueprintFeedbackReason(candidate.feedback_reason)
              : ''
            const gapReasons = candidate.gap_reasons
              .map(formatCorpusBlueprintDiagnosticReason)
              .filter(Boolean)
            const gapPositions = (candidate.gap_positions ?? [])
              .map(position => ({
                ...position,
                gapReasonLabels: (position.gap_reasons ?? [])
                  .map(formatCorpusBlueprintDiagnosticReason)
                  .filter(Boolean),
                missingLabels: (position.missing_dimensions ?? [])
                  .map(formatCorpusBlueprintDimension)
                  .filter(Boolean),
                coveredLabels: (position.covered_dimensions ?? [])
                  .map(formatCorpusBlueprintDimension)
                  .filter(Boolean),
              }))
              .filter(position => position.missingLabels.length > 0 || position.gapReasonLabels.length > 0)

            return (
              <article
                key={blueprint.blueprint_id}
                data-testid="chapter-corpus-blueprint-candidate-card"
                className={`space-y-2 px-2.5 py-2 ${isSelected ? 'bg-primary/5' : ''}`}
              >
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span className="font-medium text-foreground">方案 {index + 1}</span>
                      <span className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                        {formatCorpusBlueprintStrategy(blueprint.strategy)}
                      </span>
                      {expert && (
                        <span className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                          coverage {formatConfidence(candidate.coverage_score)}
                        </span>
                      )}
                    </div>
                    {expert ? (
                      <p className="mt-1 break-all text-[11px] leading-relaxed text-muted-foreground">
                        {blueprint.blueprint_id}
                      </p>
                    ) : (
                      <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">
                        推进：{corpusBlueprintBeatSummary(blueprint)}
                      </p>
                    )}
                  </div>
                  <button
                    type="button"
                    data-testid="chapter-corpus-blueprint-candidate-select"
                    onClick={() => onSelect(candidate)}
                    disabled={loading}
                    aria-pressed={isSelected}
                    className={`inline-flex shrink-0 items-center gap-1 rounded px-2 py-1 text-[11px] ${isSelected ? 'bg-primary text-primary-foreground' : 'bg-secondary text-muted-foreground hover:text-foreground'}`}
                  >
                    {isSelected ? <Check className="h-3.5 w-3.5" /> : null}
                    {isSelected ? '已选' : '选用此方案'}
                  </button>
                </div>

{expert && candidate.source_distribution.length > 0 && (
                  <div className="space-y-1">
                    <p className="text-[11px] font-medium text-foreground">来源分布</p>
                    <div className="flex flex-wrap gap-1">
                      {candidate.source_distribution.map((source, sourceIndex) => (
                        <span
                          key={`${blueprint.blueprint_id}:source:${source.library_id}:${source.anchor_id}:${sourceIndex}`}
                          className="max-w-full break-all rounded bg-secondary/70 px-1.5 py-0.5 text-[11px] text-muted-foreground"
                        >
                          {source.library_id} / anchor {source.anchor_id} / nodes {source.node_count}
                        </span>
                      ))}
                    </div>
                  </div>
)}

 {expert && candidate.difference_audit && (
 <div data-testid="chapter-corpus-blueprint-difference-audit" className={`rounded border px-2 py-1.5 text-[11px] ${candidate.difference_audit.passed ? 'border-emerald-500/30 bg-emerald-500/10 text-emerald-800 dark:text-emerald-200' : 'border-amber-500/30 bg-amber-500/10 text-amber-900 dark:text-amber-100'}`}>
 <div className="flex flex-wrap items-center justify-between gap-1">
 <span className="font-medium">{candidate.difference_audit.passed ? '差异审计通过' : '差异不足'}</span>
 <span>最近差异 {(candidate.difference_audit.closest_node_difference_ratio * 100).toFixed(0)}%</span>
 </div>
 <p className="mt-1 break-all opacity-80">node set {candidate.difference_audit.node_set_hash.slice(0, 12)} · 来源 {candidate.difference_audit.source_distribution_differs ? '不同' : '相同'} · 策略 {candidate.difference_audit.strategy_differs ? '不同' : '相同'}</p>
 {candidate.difference_audit.diagnostics.length > 0 && <p className="mt-1 break-words">{candidate.difference_audit.diagnostics.join('；')}</p>}
 </div>
 )}

<div className="space-y-1">
<p className="text-[11px] font-medium text-foreground">{expert ? '节拍' : '推进节奏'}</p>
 <div data-testid="chapter-corpus-blueprint-emotion-arc" className="flex min-h-8 items-end gap-1 rounded border border-border bg-card px-2 py-1">
 {blueprint.beats.map((beat, beatIndex) => {
 const height = corpusEmotionArcHeight(beat.narrative_function, beatIndex, blueprint.beats.length)
 return <span key={`${beat.beat_id}:arc`} title={`${beat.beat_index + 1}. ${beat.narrative_function}`} className="min-w-3 flex-1 bg-primary/60" style={{ height: `${height}%` }} />
 })}
 </div>
                  {expert && blueprint.beats.map(beat => (
                    <p key={beat.beat_id} className="break-all text-[11px] leading-relaxed text-muted-foreground">
                      {beat.beat_index + 1}. {beat.narrative_function || 'function'} · {beat.role_in_beat || 'role'} · {beat.node_ids.join(', ')}
                    </p>
                  ))}
                </div>

                {expert && (gapReasons.length > 0 || gapPositions.length > 0 || feedbackReason) && (
                  <div
                    data-testid="chapter-corpus-blueprint-diagnostics"
                    className="space-y-1 rounded border border-border bg-card px-2 py-1.5 text-[11px] leading-relaxed text-muted-foreground"
                  >
                    {feedbackReason && (
                      <p data-testid="chapter-corpus-blueprint-feedback-reason" className="break-words">
                        反馈：{feedbackReason}
                      </p>
                    )}
                    {gapReasons.length > 0 && (
                      <p data-testid="chapter-corpus-blueprint-gap-reasons" className="break-words">
                        缺口：{gapReasons.join('；')}
                      </p>
                    )}
                    {gapPositions.length > 0 && (
                      <div data-testid="chapter-corpus-blueprint-gap-positions" className="space-y-1">
                        {gapPositions.map(position => (
                          <p key={`${blueprint.blueprint_id}:gap:${position.beat_id}`} className="break-words">
                            第 {position.beat_index + 1} 拍缺 {position.missingLabels.join('、')}
                            {position.coveredLabels.length > 0 ? ` · 已有 ${position.coveredLabels.join('、')}` : ''}
                            {position.node_ids.length > 0 ? ` · ${position.node_ids.join(', ')}` : ''}
                          </p>
                        ))}
                      </div>
                    )}
                  </div>
                )}
              </article>
            )
          })}
        </div>
      ) : (
        <div className="px-2.5 py-3 text-xs leading-relaxed text-muted-foreground">
          当前没有可用蓝图候选。请调整章节目标或检查当前会话启用的语料库。
        </div>
      )}

      {selectedCandidate && (
        <div data-testid="chapter-corpus-blueprint-selected" className="border-t border-border px-2.5 py-2 text-[11px] leading-relaxed text-muted-foreground">
          {expert
            ? `已选：${selectedCandidate.blueprint.blueprint_id} · beats=${selectedCandidate.blueprint.beats.length}`
            : '已选此方案，可以继续生成正文候选。'}
        </div>
      )}
    </section>
  )
}

function CorpusInsertionDraftCandidateList({
  result,
  selectedCandidate,
  expert,
  onSelect,
  onNextAction,
}: {
  result: reference.CorpusInsertionDraftCandidates
  selectedCandidate: reference.CorpusInsertionDraftCandidate | null
  expert: boolean
  onSelect: (candidate: reference.CorpusInsertionDraftCandidate) => void
  onNextAction?: (candidate: reference.CorpusInsertionDraftCandidate) => void
}) {
  const selectedId = selectedCandidate?.candidate_id ?? ''
  const candidates = result.candidates ?? []

  return (
    <section data-testid="chapter-corpus-draft-candidates" className="rounded border border-border bg-background text-xs">
      <div className="border-b border-border px-2.5 py-2">
        <h4 className="truncate font-semibold text-foreground">正文候选</h4>
        <p className="mt-0.5 break-all text-[11px] leading-relaxed text-muted-foreground">
          {expert
            ? `selected ${result.selected_blueprint.blueprint_id} · ${candidates.length} 份`
            : `比较 ${candidates.length} 个可插入版本，选一个继续预览。`}
        </p>
      </div>

      {candidates.length > 0 ? (
        <div className="divide-y divide-border">
          {candidates.map((candidate, index) => {
            const isSelected = candidate.candidate_id === selectedId
            const draft = candidate.draft
            const canInsert = corpusDraftCanApply(draft)
            const nextAction = candidate.next_action?.action === 'regenerate_blueprint'
              ? candidate.next_action
              : null
            const statusLabel = corpusDraftStatusLabel(draft, expert)

            return (
              <article
                key={candidate.candidate_id}
                data-testid="chapter-corpus-draft-candidate-card"
                className={`space-y-2 px-2.5 py-2 ${isSelected ? 'bg-primary/5' : ''}`}
              >
                <div className="flex items-start justify-between gap-2">
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span className="font-medium text-foreground">正文 {index + 1}</span>
                      <span className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                        {formatCorpusDraftStrategy(candidate.strategy || draft.blueprint.strategy || 'source_variant')}
                      </span>
                      <span className={`rounded px-1.5 py-0.5 text-[11px] ${canInsert ? 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-300' : 'bg-amber-500/10 text-amber-800 dark:text-amber-200'}`}>
                        {statusLabel}
                      </span>
                    </div>
                    {expert && (
                      <p className="mt-1 break-all text-[11px] leading-relaxed text-muted-foreground">
                        {candidate.candidate_id}
                      </p>
                    )}
                  </div>
                  <button
                    type="button"
                    data-testid="chapter-corpus-draft-candidate-select"
                    onClick={() => onSelect(candidate)}
                    aria-pressed={isSelected}
                    className={`inline-flex shrink-0 items-center gap-1 rounded px-2 py-1 text-[11px] ${isSelected ? 'bg-primary text-primary-foreground' : 'bg-secondary text-muted-foreground hover:text-foreground'}`}
                  >
                    {isSelected ? <Check className="h-3.5 w-3.5" /> : null}
                    {isSelected ? '已选' : '预览此稿'}
                  </button>
                </div>

                <p className="line-clamp-2 text-[11px] leading-relaxed text-muted-foreground">
                  {candidate.explanation || '按选中蓝图复用语料节点生成。'}
                </p>
                <p className="line-clamp-2 text-[11px] leading-relaxed text-foreground">
                  {draft.assembled_text || '未生成正文'}
                </p>
                {nextAction && onNextAction && (
                  <div className="rounded border border-amber-500/30 bg-amber-500/10 px-2 py-1.5 text-[11px] leading-relaxed text-amber-900 dark:text-amber-100">
                    <p className="break-all">
                      {expert
                        ? nextAction.message || '当前正文候选需要回到蓝图重组。'
                        : '这份正文不能安全插入。请回到蓝图重组，或选择其他版本。'}
                    </p>
                    <div className="mt-1.5 flex flex-wrap items-center gap-1.5">
                      {expert && <span className="break-all text-muted-foreground">{nextAction.reason_code}</span>}
                      <button
                        type="button"
                        data-testid="chapter-corpus-draft-next-action-button"
                        onClick={() => onNextAction(candidate)}
                        className="inline-flex shrink-0 items-center gap-1 rounded bg-background px-2 py-1 text-[11px] font-medium text-foreground shadow-sm ring-1 ring-border hover:bg-secondary"
                      >
                        <RefreshCw className="h-3.5 w-3.5" />
                        回到蓝图重组
                      </button>
                    </div>
                  </div>
                )}
                {expert && (
                  <p className="text-[11px] text-muted-foreground">
                    beats={draft.blueprint.beats.length} · pieces={draft.pieces.length} · transitions={(draft.transitions ?? []).length}
                  </p>
                )}
              </article>
            )
          })}
        </div>
      ) : (
        <div className="px-2.5 py-3 text-xs leading-relaxed text-muted-foreground">
          当前没有可用正文候选。请调整章节目标或重新选择蓝图。
        </div>
      )}
    </section>
)
}

function CorpusExpertDraftControls({
 slotName,
 slotVariantsText,
 transitionStrategies,
 onSlotNameChange,
 onSlotVariantsTextChange,
 onTransitionStrategiesChange,
}: {
 slotName: string
 slotVariantsText: string
 transitionStrategies: string[]
 onSlotNameChange: (value: string) => void
 onSlotVariantsTextChange: (value: string) => void
 onTransitionStrategiesChange: (value: string[]) => void
}) {
 const slotValues = slotVariantsText
 .split(/\r?\n|，|,/)
 .map(value => value.trim())
 .filter(Boolean)
 .slice(0, 4)
const toggleStrategy = (strategy: string) => {
 onTransitionStrategiesChange(transitionStrategies.includes(strategy)
 ? transitionStrategies.filter(value => value !== strategy)
 : [...transitionStrategies, strategy])
 }

 return (
 <section data-testid="chapter-corpus-expert-controls" className="grid gap-2 border-y border-border bg-background px-2.5 py-2 text-[11px] sm:grid-cols-[minmax(0,1fr)_minmax(0,2fr)]">
 <label className="space-y-1 text-muted-foreground">
 <span className="font-medium text-foreground">槽位名</span>
 <input value={slotName} onChange={event => onSlotNameChange(event.target.value)} className="w-full rounded border border-input bg-card px-2 py-1 text-foreground outline-none focus:ring-2 focus:ring-ring" />
 </label>
 <label className="space-y-1 text-muted-foreground">
 <span className="font-medium text-foreground">槽位变体</span>
 <textarea value={slotVariantsText} onChange={event => onSlotVariantsTextChange(event.target.value)} className="min-h-14 w-full resize-y rounded border border-input bg-card px-2 py-1 text-foreground outline-none focus:ring-2 focus:ring-ring" />
</label>
 <div data-testid="chapter-corpus-expert-slot-table" className="sm:col-span-2 overflow-hidden rounded border border-border">
 <div className="grid grid-cols-[minmax(90px,0.7fr)_minmax(0,1.3fr)] bg-secondary/60 px-2 py-1 font-medium text-foreground">
 <span>槽位</span>
 <span>变体值</span>
 </div>
 {slotValues.length > 0 ? slotValues.map((value, index) => (
 <div key={`${value}-${index}`} className="grid grid-cols-[minmax(90px,0.7fr)_minmax(0,1.3fr)] border-t border-border px-2 py-1 text-muted-foreground">
 <span className="truncate">{slotName.trim() || 'character'}</span>
 <span className="break-words text-foreground">{value}</span>
 </div>
 )) : <p className="border-t border-border px-2 py-1 text-muted-foreground">暂无槽位变体</p>}
 </div>
<div className="space-y-1 sm:col-span-2">
 <span className="font-medium text-foreground">过渡策略</span>
 <div className="flex flex-wrap gap-1.5">
 {[['default', '语义过渡'], ['direct_join', '直接拼接']].map(([value, label]) => (
 <label key={value} className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-muted-foreground">
 <input type="checkbox" checked={transitionStrategies.includes(value)} onChange={() => toggleStrategy(value)} />
 {label}
 </label>
 ))}
</div>
 <div data-testid="chapter-corpus-expert-transition-list" className="flex flex-wrap items-center gap-1.5 text-muted-foreground">
 <span className="font-medium text-foreground">已选清单</span>
 {transitionStrategies.length > 0 ? transitionStrategies.map(strategy => (
 <span key={strategy} className="rounded bg-secondary px-1.5 py-0.5">{strategy === 'direct_join' ? '直接拼接' : '语义过渡'}</span>
 )) : <span>未选择</span>}
 </div>
</div>
 </section>
 )
}

function CorpusDraftComparison({
 result,
 selectedCandidateId,
 lockedCandidateId,
 onSelect,
 onLock,
}: {
 result: reference.CorpusInsertionDraftCandidates
 selectedCandidateId: string
 lockedCandidateId: string
 onSelect: (candidate: reference.CorpusInsertionDraftCandidate) => void
 onLock: (candidateId: string) => void
}) {
 const readyCandidates = result.candidates.filter(candidate => corpusDraftCanApply(candidate.draft))
 const visibleCandidates = (readyCandidates.length > 0 ? readyCandidates : result.candidates).slice(0, 4)
 const audit = result.candidate_set_audit

 return (
 <section data-testid="chapter-corpus-draft-comparison" className="space-y-2 rounded border border-border bg-background p-2">
 <div className="flex flex-wrap items-center justify-between gap-2 text-[11px]">
 <div className="flex items-center gap-1.5">
 <GitCompareArrows className="h-3.5 w-3.5 text-muted-foreground" />
 <span className="font-semibold text-foreground">并排差异</span>
 </div>
 {audit && <span className={audit.passed ? 'text-emerald-700 dark:text-emerald-300' : 'text-amber-800 dark:text-amber-200'}>候选集审计 {audit.passed ? '通过' : '阻断'} · 文本 {audit.distinct_text_count}</span>}
 </div>
 <div className="grid auto-cols-[minmax(220px,1fr)] grid-flow-col gap-2 overflow-x-auto pb-1">
 {visibleCandidates.map(candidate => {
 const selected = candidate.candidate_id === selectedCandidateId
 const locked = candidate.candidate_id === lockedCandidateId
const difference = audit?.differences.find(item => item.candidate_id === candidate.candidate_id)
 const transitionLabels = candidate.draft.transitions.map(transition =>
 `${transition.decision}/${transition.strategy}`)
 return (
 <article key={candidate.candidate_id} className={`min-w-0 space-y-2 rounded border p-2 ${selected ? 'border-primary bg-primary/5' : 'border-border bg-card'}`}>
 <button type="button" onClick={() => onSelect(candidate)} className="w-full text-left">
 <p className="truncate text-[11px] font-medium text-foreground">{formatCorpusDraftStrategy(candidate.strategy)}</p>
 <p className="mt-1 line-clamp-5 whitespace-pre-wrap text-xs leading-relaxed text-foreground">{candidate.draft.assembled_text}</p>
 </button>
 <div className="flex flex-wrap gap-1 text-[10px] text-muted-foreground">
 <span className="rounded bg-secondary px-1 py-0.5">槽位差 {difference?.slot_difference_count ?? candidate.draft.slot_replacements.length}</span>
 <span className="rounded bg-secondary px-1 py-0.5">过渡差 {difference?.transition_difference_count ?? candidate.draft.transitions.length}</span>
 <span className="rounded bg-secondary px-1 py-0.5">锁定段 {candidate.draft.pieces.reduce((sum, piece) => sum + piece.locked_spans.length, 0)}</span>
</div>
 <div data-testid="chapter-corpus-draft-transition-list" className="space-y-1 text-[10px] text-muted-foreground">
 <p className="font-medium text-foreground">过渡清单</p>
 {transitionLabels.length > 0
 ? transitionLabels.map((label, index) => <p key={`${label}-${index}`} className="truncate">{label}</p>)
 : <p>无相邻片段</p>}
 </div>
<button type="button" disabled={!corpusDraftCanApply(candidate.draft)} onClick={() => onLock(candidate.candidate_id)} className={`inline-flex w-full items-center justify-center gap-1 rounded px-2 py-1 text-[11px] ${locked ? 'bg-primary text-primary-foreground' : 'bg-secondary text-foreground hover:bg-secondary/80'} disabled:opacity-50`}>
 {locked ? <Check className="h-3.5 w-3.5" /> : <Lock className="h-3.5 w-3.5" />}
 {locked ? '已锁定' : '锁定此稿'}
</button>
 {locked && <p data-testid="chapter-corpus-draft-lock-confirmation" className="text-center text-[10px] font-medium text-emerald-700 dark:text-emerald-300">已锁定，待确认写入</p>}
</article>
 )
 })}
 </div>
 {audit && audit.errors.length > 0 && <p className="break-words text-[11px] text-amber-800 dark:text-amber-200">{audit.errors.join('；')}</p>}
 </section>
 )
}

function corpusEmotionArcHeight(narrativeFunction: string, index: number, count: number): number {
 const normalized = narrativeFunction.toLowerCase()
 if (normalized.includes('climax') || normalized.includes('reveal') || normalized.includes('pressure')) return 88
 if (normalized.includes('withhold') || normalized.includes('suspend')) return 72
 if (normalized.includes('release') || normalized.includes('resolve')) return 42
 return Math.round(35 + ((index + 1) / Math.max(1, count)) * 35)
}

function corpusDraftCanApply(draft: reference.CorpusInsertionDraft): boolean {
  return draft.ready_for_insertion &&
    draft.gate.passed &&
    draft.audit.passed &&
    corpusDraftTransitionIssueMessages(draft).length === 0
}

function corpusDraftStatusLabel(draft: reference.CorpusInsertionDraft, expert: boolean): string {
  if (corpusDraftCanApply(draft)) return '可插入'
  if (corpusDraftTransitionIssueMessages(draft).length > 0) {
    return expert ? '过渡审计阻断' : '需要重组蓝图'
  }
  if (!draft.audit.passed) {
    return expert ? '审计阻断' : '暂不能插入'
  }
  return expert ? draft.gate.status || '闸门阻断' : '暂不能插入'
}

function corpusDraftBlockedMessage(
  draft: reference.CorpusInsertionDraft,
  transitionIssues: string[],
): string {
  if (transitionIssues.length > 0) {
    return '这份正文的过渡不能安全拼接。请回到蓝图重组，或选择其他版本。'
  }
  if (!draft.audit.passed) {
    return '这份正文未通过来源文本核验，不能插入。请选择其他版本。'
  }
  return '这份正文未通过写入检查，不能插入。请选择其他版本。'
}

function corpusDraftTransitionIssueMessages(draft: reference.CorpusInsertionDraft): string[] {
  const transitions = draft.transitions ?? []
  if (transitions.length === 0) return []

  const auditByTransitionId = new Map((draft.audit.transitions ?? []).map(transition => [transition.transition_id, transition]))
  const messages: string[] = []
  for (const transition of transitions) {
    const audit = auditByTransitionId.get(transition.transition_id)
    if (!audit) {
      messages.push(`transition_audit_missing:${transition.transition_id}`)
    } else {
      if (!audit.passed) {
        messages.push(`transition_blocked:${transition.transition_id}`)
      }
      for (const violation of audit.violations ?? []) {
        messages.push(`${violation.code}:${violation.transition_id ?? transition.transition_id}`)
      }
    }

    if (!transition.approved) {
      messages.push(`transition_not_approved:${transition.transition_id}`)
    }

    if (transition.output_start < 0 ||
      transition.output_end < transition.output_start ||
      transition.output_end > draft.assembled_text.length ||
      draft.assembled_text.slice(transition.output_start, transition.output_end) !== transition.text) {
      messages.push(`transition_output_range_mismatch:${transition.transition_id}`)
    }
  }

  return Array.from(new Set(messages))
}

function corpusDraftTransitionAfterPiece(
  draft: reference.CorpusInsertionDraft,
  piece: reference.CorpusInsertionPiece,
  nextPiece: reference.CorpusInsertionPiece | undefined,
): reference.CorpusInsertionTransition | null {
  if (!nextPiece) return null
  return (draft.transitions ?? []).find(transition =>
    transition.after_piece_id === piece.piece_id &&
    transition.before_piece_id === nextPiece.piece_id) ?? null
}

function CorpusInsertionDraftPreview({
  draft,
  expert,
  insertionDisabled,
  onApply,
  onCopy,
}: {
  draft: reference.CorpusInsertionDraft
  expert: boolean
  insertionDisabled: boolean
  onApply: () => void
  onCopy: () => void
}) {
  const transitionIssueMessages = corpusDraftTransitionIssueMessages(draft)
  const draftCanApply = corpusDraftCanApply(draft)
  const canApply = draftCanApply && !insertionDisabled
  const gateLabel = draftCanApply ? '闸门通过' : corpusDraftStatusLabel(draft, expert)
  const blockingMessages = [
    ...draft.gate.errors,
    ...draft.audit.errors,
    ...draft.audit.pieces.flatMap(piece => piece.violations.map(violation => `${violation.code}:${violation.node_id}`)),
    ...transitionIssueMessages,
  ]

  return (
    <article data-testid="chapter-corpus-draft-preview" className="space-y-2 rounded border border-border bg-background px-2.5 py-2 text-xs">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <span className="font-medium text-foreground">{draft.blueprint.strategy || '自动蓝图'}</span>
        <span className={`rounded px-1.5 py-0.5 text-[11px] ${draft.ready_for_insertion ? 'bg-emerald-500/10 text-emerald-700 dark:text-emerald-300' : 'bg-amber-500/10 text-amber-800 dark:text-amber-200'}`}>
          {gateLabel}
        </span>
      </div>

      <div
        data-testid="chapter-corpus-draft-diff"
        className="space-y-1 rounded border border-border bg-card px-2 py-2 leading-relaxed text-foreground"
      >
        {draft.pieces.length > 0 ? (
          draft.pieces.map((piece, index) => {
            const transition = corpusDraftTransitionAfterPiece(draft, piece, draft.pieces[index + 1])
            return (
              <Fragment key={piece.piece_id}>
                <p className="whitespace-pre-wrap">
                  {renderCorpusPieceText(piece, expert)}
                </p>
                {transition && transition.text.trim().length > 0 && (
                  <p
                    data-testid="chapter-corpus-draft-transition"
                    className="whitespace-pre-wrap rounded border border-dashed border-border bg-secondary/40 px-2 py-1 text-[11px] text-muted-foreground"
                    title={expert ? `${transition.strategy}: ${transition.reason}` : undefined}
                  >
                    {renderCorpusTransitionText(transition, expert)}
                  </p>
                )}
              </Fragment>
            )
          })
        ) : (
          <p className="whitespace-pre-wrap">{draft.assembled_text || '未生成可插入文本'}</p>
        )}
      </div>

      <div className="grid grid-cols-2 gap-1.5">
        <button
          type="button"
          onClick={onCopy}
          disabled={!draft.assembled_text}
          className="inline-flex items-center justify-center gap-1 rounded border border-border px-2 py-1 text-xs text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
        >
          <Copy className="h-3.5 w-3.5" />
          复制片段
        </button>
        <button
          type="button"
          onClick={onApply}
          disabled={!canApply}
          className="inline-flex items-center justify-center gap-1 rounded bg-secondary px-2 py-1 text-xs text-foreground hover:bg-secondary/80 disabled:opacity-50"
        >
          <CornerDownLeft className="h-3.5 w-3.5" />
          应用到编辑器
        </button>
      </div>

      {!draftCanApply && (
        <div className="rounded border border-amber-500/30 bg-amber-500/10 px-2 py-1.5 text-[11px] leading-relaxed text-amber-800 dark:text-amber-200">
          {expert
            ? blockingMessages.length > 0
              ? Array.from(new Set(blockingMessages)).join('；')
              : '当前草稿未通过授权、相似度或保留文本校验，不能写入编辑器。'
            : corpusDraftBlockedMessage(draft, transitionIssueMessages)}
        </div>
      )}

      {expert && (
      <div className="space-y-1 text-[11px] leading-relaxed text-muted-foreground">
        <p>
          蓝图：{draft.blueprint.blueprint_id} · beats={draft.blueprint.beats.length} · pieces={draft.pieces.length} · locked={draft.pieces.reduce((total, piece) => total + piece.locked_spans.length, 0)} · transitions={(draft.transitions ?? []).length}
        </p>
        {draft.blueprint.beats.map(beat => (
          <p key={beat.beat_id} className="break-all">
            {beat.beat_index + 1}. {beat.narrative_function} · {beat.role_in_beat} · {beat.node_ids.join(', ')}
          </p>
        ))}
        {draft.slot_replacements.length > 0 && (
          <div className="rounded border border-border bg-card px-2 py-1.5">
            {draft.slot_replacements.map((replacement, index) => (
              <p key={`${replacement.slot_name}-${replacement.source_start}-${index}`} className="break-all">
                槽位 {replacement.slot_name}: {replacement.source_value}{' -> '}{replacement.replacement_value}
              </p>
            ))}
          </div>
        )}
        {draft.gate.pieces.length > 0 && (
          <div className="rounded border border-border bg-card px-2 py-1.5">
            {draft.gate.pieces.map(piece => (
              <p key={piece.piece_id} className="break-all">
                {piece.node_id} · 4gram {piece.four_gram_containment_ratio.toFixed(2)} · LCS {piece.longest_common_substring_ratio.toFixed(2)}
              </p>
            ))}
          </div>
        )}
        {draft.audit.pieces.length > 0 && (
          <div className="rounded border border-border bg-card px-2 py-1.5">
            {draft.audit.pieces.map(piece => (
              <p key={piece.piece_id} className="break-all">
                {piece.node_id} · preserved {piece.preserved_span_count} · mismatch {piece.mismatched_span_count}
              </p>
            ))}
          </div>
        )}
        {(draft.audit.transitions ?? []).length > 0 && (
          <div className="rounded border border-border bg-card px-2 py-1.5">
            {draft.audit.transitions.map(transition => (
              <p key={transition.transition_id} className="break-all">
                {transition.decision} · {transition.after_piece_id}{' -> '}{transition.before_piece_id} · {transition.passed ? 'passed' : 'blocked'}
              </p>
            ))}
          </div>
        )}
      </div>
      )}

      {insertionDisabled && (
        <p className="text-[11px] text-muted-foreground">
          大纲视图下不能应用到正文编辑器。
        </p>
      )}
    </article>
  )
}

function renderCorpusPieceText(piece: reference.CorpusInsertionPiece, expert: boolean): ReactNode {
  const text = piece.output_text
  const replacements = piece.slot_replacements
    .filter(replacement =>
      replacement.output_start >= 0 &&
      replacement.output_end > replacement.output_start &&
      replacement.output_end <= text.length)
  const lockedSpans = piece.locked_spans
    .filter(span =>
      span.output_start >= 0 &&
      span.output_end > span.output_start &&
      span.output_end <= text.length)
  const preservedSpans = piece.preserved_spans
    .filter(span =>
      span.output_start >= 0 &&
      span.output_end > span.output_start &&
      span.output_end <= text.length)

  if (replacements.length === 0 && lockedSpans.length === 0 && preservedSpans.length === 0) {
    return <span data-testid="chapter-corpus-diff-preserved">{text}</span>
  }

  const boundaries = new Set<number>([0, text.length])
  replacements.forEach(replacement => {
    boundaries.add(replacement.output_start)
    boundaries.add(replacement.output_end)
  })
  lockedSpans.forEach(span => {
    boundaries.add(span.output_start)
    boundaries.add(span.output_end)
  })
  preservedSpans.forEach(span => {
    boundaries.add(span.output_start)
    boundaries.add(span.output_end)
  })

  const parts: ReactNode[] = []
  const sortedBoundaries = Array.from(boundaries)
    .filter(value => value >= 0 && value <= text.length)
    .sort((left, right) => left - right)

  for (let index = 0; index + 1 < sortedBoundaries.length; index += 1) {
    const start = sortedBoundaries[index]
    const end = sortedBoundaries[index + 1]
    if (end <= start) {
      continue
    }

    const replacement = replacements.find(item => start >= item.output_start && end <= item.output_end)
    if (replacement) {
      parts.push(
        <mark
          key={`replacement-${start}-${index}`}
          data-testid="chapter-corpus-diff-slot-replacement"
          className="rounded bg-amber-200 px-0.5 text-amber-950 dark:bg-amber-400/30 dark:text-amber-100"
          title={`${replacement.source_value} -> ${replacement.replacement_value}`}
        >
          {text.slice(start, end)}
        </mark>,
      )
      continue
    }

    const locked = lockedSpans.find(span => start >= span.output_start && end <= span.output_end)
    if (locked) {
      parts.push(
        <mark
          key={`locked-${start}-${index}`}
          data-testid="chapter-corpus-diff-locked-span"
          className="rounded bg-sky-100 px-0.5 text-sky-950 ring-1 ring-sky-300 dark:bg-sky-400/20 dark:text-sky-100 dark:ring-sky-400/40"
          title={expert ? locked.reason : '已锁定保留片段'}
        >
          {text.slice(start, end)}
        </mark>,
      )
      continue
    }

    const preserved = preservedSpans.find(span => start >= span.output_start && end <= span.output_end)
    parts.push(
      <span
        key={`preserved-span-${start}-${index}`}
        data-testid={preserved?.matches === false ? 'chapter-corpus-diff-preserved-mismatch' : 'chapter-corpus-diff-preserved'}
        className={preserved?.matches === false ? 'rounded bg-destructive/10 px-0.5 text-destructive' : undefined}
      >
        {text.slice(start, end)}
      </span>,
    )
  }

  return parts
}

function renderCorpusTransitionText(transition: reference.CorpusInsertionTransition, expert: boolean): ReactNode {
  return (
    <span>
      {expert && (
        <span className="mr-1 rounded bg-secondary px-1 py-0.5 text-[10px] uppercase tracking-normal">
          {transition.strategy || transition.decision}
        </span>
      )}
      <span>{transition.text}</span>
    </span>
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
      className="fixed inset-y-0 right-0 z-50 flex w-[640px] max-w-[calc(100vw-2rem)] flex-col border-l border-border bg-card shadow-xl"
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
              <p className="whitespace-pre-wrap break-words rounded-md border border-border bg-background px-3 py-2 text-xs leading-relaxed text-foreground">
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
                      <p className="mt-1 whitespace-pre-wrap break-words text-xs leading-relaxed text-foreground">{segment.text_preview || '无预览'}</p>
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
                      <p className="mt-1 whitespace-pre-wrap break-words text-xs leading-relaxed text-foreground">{note.message || '无诊断信息'}</p>
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
