import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ChangeEvent, Dispatch, SetStateAction } from 'react'
import {
  BookMarked,
  CheckCircle2,
  FileSearch,
  Link2,
  Loader2,
  Plus,
  RefreshCcw,
  Search,
  ShieldCheck,
  Trash2,
  Wand2,
} from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reference } from '@/lib/novelist/types'

interface Props {
  novelId: number
}

type AnchorForm = {
  title: string
  author: string
  sourcePath: string
  sourceKind: string
  licenseStatus: string
}

type BlueprintForm = {
  chapterNumber: string
  title: string
  chapterGoal: string
  knownFacts: string
  forbiddenFacts: string
}

type BlueprintRevisionForm = {
  knownFacts: string
  forbiddenFacts: string
  narrativeFunction: string
  logicPremise: string
  conflictPressure: string
  causalityIn: string
  causalityOut: string
  transitionIn: string
  transitionOut: string
  povCharacter: string
  narrativeDistance: string
  viewpointAllowedKnowledge: string
  viewpointForbiddenKnowledge: string
  characterStatesBefore: string
  characterStatesAfter: string
  characterGoals: string
  characterMisbeliefs: string
  relationshipPressure: string
  emotionTrigger: string
  emotionBefore: string
  emotionAfter: string
  suppressedReaction: string
  externalEvidence: string
  narrationStrategy: string
  rhythmStrategy: string
  paragraphIntention: string
  executionMode: string
  antiScreenplayDuty: string
  sensoryAnchorTarget: string
  subtextPlan: string
  sourceBackedDetailTarget: string
  candidateRejectionRule: string
  sceneFacts: string
  beatForbiddenFacts: string
  requiredMaterialTypes: string
  maxRewriteLevel: string
  slotPlan: reference.SlotValue[]
  lockedPhrasePolicy: string
  noReuseReason: string
  proseDuties: string
  referenceQuery: string
  referenceMaterialTypes: string
  referenceEmotionTags: string
  referenceFunctionTags: string
  referencePovTags: string
  referenceTechniqueTags: string
  referenceMaxResults: string
}

type BlueprintRevisionStringKey = {
  [Key in keyof BlueprintRevisionForm]: BlueprintRevisionForm[Key] extends string ? Key : never
}[keyof BlueprintRevisionForm]

type FindingSection = {
  label: string
  items: string[]
}

const EMPTY_ANCHOR_FORM: AnchorForm = {
  title: '',
  author: '',
  sourcePath: '',
  sourceKind: 'markdown',
  licenseStatus: 'user_provided',
}

const EMPTY_BLUEPRINT_FORM: BlueprintForm = {
  chapterNumber: '1',
  title: '',
  chapterGoal: '',
  knownFacts: '',
  forbiddenFacts: '',
}

const EMPTY_REVISION_FORM: BlueprintRevisionForm = {
  knownFacts: '',
  forbiddenFacts: '',
  narrativeFunction: '',
  logicPremise: '',
  conflictPressure: '',
  causalityIn: '',
  causalityOut: '',
  transitionIn: '',
  transitionOut: '',
  povCharacter: '',
  narrativeDistance: '',
  viewpointAllowedKnowledge: '',
  viewpointForbiddenKnowledge: '',
  characterStatesBefore: '',
  characterStatesAfter: '',
  characterGoals: '',
  characterMisbeliefs: '',
  relationshipPressure: '',
  emotionTrigger: '',
  emotionBefore: '',
  emotionAfter: '',
  suppressedReaction: '',
  externalEvidence: '',
  narrationStrategy: '',
  rhythmStrategy: '',
  paragraphIntention: '',
  executionMode: '',
  antiScreenplayDuty: '',
  sensoryAnchorTarget: '',
  subtextPlan: '',
  sourceBackedDetailTarget: '',
  candidateRejectionRule: '',
  sceneFacts: '',
  beatForbiddenFacts: '',
  requiredMaterialTypes: '',
  maxRewriteLevel: '',
  slotPlan: [],
  lockedPhrasePolicy: '',
  noReuseReason: '',
  proseDuties: '',
  referenceQuery: '',
  referenceMaterialTypes: '',
  referenceEmotionTags: '',
  referenceFunctionTags: '',
  referencePovTags: '',
  referenceTechniqueTags: '',
  referenceMaxResults: '',
}

const inputClass = 'w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring'
const actionButtonClass = 'inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50'

function lines(value: string): string[] {
  return value
    .split(/\r?\n|;|；/)
    .map(item => item.trim())
    .filter(Boolean)
}

function multiline(values: string[] | undefined): string {
  return (values ?? []).join('\n')
}

function sameList(left: string[], right: string[]): boolean {
  return left.length === right.length && left.every((item, index) => item === right[index])
}

function normalizeSlotPlan(slotPlan: reference.SlotValue[] | undefined): reference.SlotValue[] {
  return (slotPlan ?? [])
    .map(slot => ({
      slot_name: slot.slot_name.trim(),
      value: slot.value.trim(),
    }))
    .filter(slot => slot.slot_name.length > 0 || slot.value.length > 0)
}

function sameSlotPlan(left: reference.SlotValue[], right: reference.SlotValue[]): boolean {
  return left.length === right.length &&
    left.every((slot, index) => slot.slot_name === right[index].slot_name && slot.value === right[index].value)
}

function addStringChange(
  changes: reference.BlueprintRevisionChange[],
  fieldPath: string,
  nextValue: string,
  currentValue: string,
) {
  const trimmed = nextValue.trim()
  if (trimmed !== currentValue) {
    changes.push({ field_path: fieldPath, new_value: trimmed })
  }
}

function addListChange(
  changes: reference.BlueprintRevisionChange[],
  fieldPath: string,
  nextValue: string,
  currentValue: string[],
) {
  const nextList = lines(nextValue)
  if (!sameList(nextList, currentValue)) {
    changes.push({ field_path: fieldPath, new_value: JSON.stringify(nextList) })
  }
}

function addSlotPlanChange(
  changes: reference.BlueprintRevisionChange[],
  fieldPath: string,
  nextValue: reference.SlotValue[],
  currentValue: reference.SlotValue[],
) {
  const nextSlotPlan = normalizeSlotPlan(nextValue)
  const currentSlotPlan = normalizeSlotPlan(currentValue)
  if (!sameSlotPlan(nextSlotPlan, currentSlotPlan)) {
    changes.push({ field_path: fieldPath, new_value: JSON.stringify(nextSlotPlan) })
  }
}

function formFromBlueprint(blueprint: reference.ChapterBlueprint | null): BlueprintRevisionForm {
  if (!blueprint) return EMPTY_REVISION_FORM
  const beat = blueprint.beats[0]
  return {
    knownFacts: multiline(blueprint.known_facts),
    forbiddenFacts: multiline(blueprint.forbidden_facts),
    narrativeFunction: beat?.narrative_function ?? '',
    logicPremise: beat?.logic_premise ?? '',
    conflictPressure: beat?.conflict_pressure ?? '',
    causalityIn: beat?.causality_in ?? '',
    causalityOut: beat?.causality_out ?? '',
    transitionIn: beat?.transition_in ?? '',
    transitionOut: beat?.transition_out ?? '',
    povCharacter: beat?.pov_character ?? '',
    narrativeDistance: beat?.narrative_distance ?? '',
    viewpointAllowedKnowledge: multiline(beat?.viewpoint_allowed_knowledge),
    viewpointForbiddenKnowledge: multiline(beat?.viewpoint_forbidden_knowledge),
    characterStatesBefore: multiline(beat?.character_states_before),
    characterStatesAfter: multiline(beat?.character_states_after),
    characterGoals: multiline(beat?.character_goals),
    characterMisbeliefs: multiline(beat?.character_misbeliefs),
    relationshipPressure: multiline(beat?.relationship_pressure),
    emotionTrigger: beat?.emotion_trigger ?? '',
    emotionBefore: beat?.emotion_before ?? '',
    emotionAfter: beat?.emotion_after ?? '',
    suppressedReaction: beat?.suppressed_reaction ?? '',
    externalEvidence: beat?.external_evidence ?? '',
    narrationStrategy: beat?.narration_strategy ?? '',
    rhythmStrategy: beat?.rhythm_strategy ?? '',
    paragraphIntention: beat?.paragraph_intention ?? '',
    executionMode: beat?.execution_mode ?? '',
    antiScreenplayDuty: beat?.anti_screenplay_duty ?? '',
    sensoryAnchorTarget: beat?.sensory_anchor_target ?? '',
    subtextPlan: beat?.subtext_plan ?? '',
    sourceBackedDetailTarget: beat?.source_backed_detail_target ?? '',
    candidateRejectionRule: beat?.candidate_rejection_rule ?? '',
    sceneFacts: multiline(beat?.scene_facts),
    beatForbiddenFacts: multiline(beat?.forbidden_facts),
    requiredMaterialTypes: multiline(beat?.required_material_types),
    maxRewriteLevel: beat?.max_rewrite_level ?? '',
    slotPlan: normalizeSlotPlan(beat?.slot_plan),
    lockedPhrasePolicy: beat?.locked_phrase_policy ?? '',
    noReuseReason: beat?.no_reuse_reason ?? '',
    proseDuties: multiline(beat?.prose_duties),
    referenceQuery: beat?.reference_query.query ?? '',
    referenceMaterialTypes: multiline(beat?.reference_query.material_types),
    referenceEmotionTags: multiline(beat?.reference_query.emotion_tags),
    referenceFunctionTags: multiline(beat?.reference_query.function_tags),
    referencePovTags: multiline(beat?.reference_query.pov_tags),
    referenceTechniqueTags: multiline(beat?.reference_query.technique_tags),
    referenceMaxResults: beat ? String(beat.reference_query.max_results) : '',
  }
}

function statusTone(status: string): string {
  if (status === 'approved' || status === 'material_bound' || status === 'passed') return 'text-emerald-600 dark:text-emerald-400'
  if (status === 'failed' || status === 'review_failed' || status === 'stale') return 'text-destructive'
  return 'text-muted-foreground'
}

function findingSections(sections: FindingSection[]): FindingSection[] {
  return sections
    .map(section => ({ ...section, items: section.items.filter(Boolean) }))
    .filter(section => section.items.length > 0)
}

function reviewFindings(review: reference.ChapterBlueprintReview): FindingSection[] {
  return findingSections([
    { label: '逻辑', items: review.logic_errors },
    { label: '因果', items: review.causality_errors },
    { label: '情绪', items: review.emotion_errors },
    { label: '叙述', items: review.narration_errors },
    { label: '执行', items: review.execution_errors },
    { label: '角色状态', items: review.character_state_errors },
    { label: 'POV', items: review.pov_errors },
    { label: '连续性', items: review.continuity_errors },
    { label: '转场', items: review.transition_errors },
    { label: '禁止事实', items: review.forbidden_fact_errors },
    { label: '引用绑定', items: review.reference_binding_errors },
    { label: '材料匹配', items: review.material_fit_errors },
    { label: '剧本化风险', items: review.screenplay_drift_risks },
    { label: '小说化叙述', items: review.novelistic_narration_errors },
    { label: 'AI 文风风险', items: review.ai_prose_risks },
    { label: '必须修复', items: review.required_fixes },
  ])
}

function auditFindings(audit: reference.AnchoredDraftAudit): FindingSection[] {
  return findingSections([
    { label: '来源溯源', items: audit.provenance_errors },
    { label: '蓝图约束', items: audit.blueprint_errors },
    { label: '未支持事实', items: audit.unsupported_fact_errors },
    { label: 'POV', items: audit.pov_errors },
    { label: 'AI 文风风险', items: audit.ai_prose_risks },
    { label: '必须修复', items: audit.required_fixes },
  ])
}

function scoreComponents(link: reference.BlueprintMaterialLink): Array<[string, number]> {
  return Object.entries(link.score_components ?? {})
    .filter(([, value]) => Number.isFinite(value) && value > 0)
    .sort(([, left], [, right]) => right - left)
}

export default function ReferenceAnchorView({ novelId }: Props) {
  const app = useApp()

  const [anchors, setAnchors] = useState<reference.Anchor[]>([])
  const [selectedAnchorIds, setSelectedAnchorIds] = useState<number[]>([])
  const [materials, setMaterials] = useState<reference.Material[]>([])
  const [blueprints, setBlueprints] = useState<reference.ChapterBlueprintSummary[]>([])
  const [activeBlueprint, setActiveBlueprint] = useState<reference.ChapterBlueprint | null>(null)
  const [binding, setBinding] = useState<reference.BlueprintMaterialBindingResult | null>(null)
  const [draft, setDraft] = useState<reference.AnchoredDraft | null>(null)
  const [anchorForm, setAnchorForm] = useState<AnchorForm>(EMPTY_ANCHOR_FORM)
  const [blueprintForm, setBlueprintForm] = useState<BlueprintForm>(EMPTY_BLUEPRINT_FORM)
  const [revisionForm, setRevisionForm] = useState<BlueprintRevisionForm>(EMPTY_REVISION_FORM)
  const [materialQuery, setMaterialQuery] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const selectedAnchorSet = useMemo(() => new Set(selectedAnchorIds), [selectedAnchorIds])

  const loadAnchors = useCallback(async () => {
    if (!novelId) {
      setAnchors([])
      return
    }

    setError(null)
    const list = await app.GetReferenceAnchors(novelId)
    setAnchors(list ?? [])
    setSelectedAnchorIds(current => {
      const valid = new Set((list ?? []).map(anchor => anchor.anchor_id))
      const next = current.filter(id => valid.has(id))
      return next.length > 0 ? next : (list?.[0] ? [list[0].anchor_id] : [])
    })
  }, [app, novelId])

  const loadBlueprints = useCallback(async () => {
    if (!novelId) {
      setBlueprints([])
      return
    }

    const list = await app.GetReferenceChapterBlueprints(novelId, null)
    setBlueprints(list ?? [])
  }, [app, novelId])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      setLoading(true)
      try {
        await Promise.all([loadAnchors(), loadBlueprints()])
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : '加载失败')
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [loadAnchors, loadBlueprints])

  async function run<T>(task: () => Promise<T>, success?: string): Promise<T | null> {
    setLoading(true)
    setError(null)
    setMessage(null)
    try {
      const result = await task()
      if (success) setMessage(success)
      return result
    } catch (err) {
      setError(err instanceof Error ? err.message : '操作失败')
      return null
    } finally {
      setLoading(false)
    }
  }

  async function createAnchor() {
    if (!anchorForm.title.trim() || !anchorForm.sourcePath.trim()) {
      setError('请输入参考书标题和本地文件路径')
      return
    }

    const created = await run(() => app.CreateReferenceAnchor({
      novel_id: novelId,
      title: anchorForm.title.trim(),
      author: anchorForm.author.trim() || undefined,
      source_path: anchorForm.sourcePath.trim(),
      source_kind: anchorForm.sourceKind,
      license_status: anchorForm.licenseStatus,
    }), '参考锚点已创建')
    if (created) {
      setAnchorForm(EMPTY_ANCHOR_FORM)
      await loadAnchors()
    }
  }

  async function rebuildAnchor(anchorId: number) {
    await run(() => app.RebuildReferenceAnchor(novelId, anchorId), '锚点已重建')
    await loadAnchors()
  }

  async function searchMaterials() {
    const result = await run(() => app.SearchReferenceMaterials({
      novel_id: novelId,
      anchor_ids: selectedAnchorIds,
      query: materialQuery.trim(),
      material_types: [],
      emotion_tags: [],
      function_tags: [],
      pov_tags: [],
      technique_tags: [],
      page: 1,
      size: 10,
    }))
    if (result) setMaterials(result.items ?? [])
  }

  async function generateBlueprint() {
    const chapterNumber = Number.parseInt(blueprintForm.chapterNumber, 10)
    if (!Number.isFinite(chapterNumber) || chapterNumber < 1) {
      setError('请输入有效章节号')
      return
    }

    const blueprint = await run(() => app.GenerateReferenceChapterBlueprint({
      novel_id: novelId,
      chapter_number: chapterNumber,
      title: blueprintForm.title.trim() || undefined,
      chapter_goal: blueprintForm.chapterGoal.trim() || undefined,
      anchor_ids: selectedAnchorIds,
      known_facts: lines(blueprintForm.knownFacts),
      forbidden_facts: lines(blueprintForm.forbiddenFacts),
    }), '章节蓝图已生成')
    if (blueprint) {
      setActiveBlueprint(blueprint)
      setRevisionForm(formFromBlueprint(blueprint))
      setBinding(null)
      setDraft(null)
      await loadBlueprints()
    }
  }

  async function selectBlueprint(blueprintId: number) {
    const blueprint = await run(() => app.GetReferenceChapterBlueprint(novelId, blueprintId))
    if (blueprint) {
      setActiveBlueprint(blueprint)
      setRevisionForm(formFromBlueprint(blueprint))
      setBinding(null)
      setDraft(null)
    }
  }

  async function reviewBlueprint() {
    if (!activeBlueprint) return
    const review = await run(() => app.ReviewReferenceChapterBlueprint({
      novel_id: novelId,
      blueprint_id: activeBlueprint.blueprint_id,
    }), '蓝图评审已完成')
    if (review) {
      const refreshed = await app.GetReferenceChapterBlueprint(novelId, activeBlueprint.blueprint_id)
      setActiveBlueprint(refreshed)
      setRevisionForm(formFromBlueprint(refreshed))
      await loadBlueprints()
    }
  }

  async function approveBlueprint() {
    if (!activeBlueprint?.latest_review) return
    const approved = await run(() => app.ApproveReferenceChapterBlueprint({
      novel_id: novelId,
      blueprint_id: activeBlueprint.blueprint_id,
      review_id: activeBlueprint.latest_review!.review_id,
    }), '蓝图已批准')
    if (approved) {
      setActiveBlueprint(approved)
      setRevisionForm(formFromBlueprint(approved))
      await loadBlueprints()
    }
  }

  async function bindMaterials() {
    if (!activeBlueprint) return
    const result = await run(() => app.BindReferenceBlueprintMaterials({
      novel_id: novelId,
      blueprint_id: activeBlueprint.blueprint_id,
      max_results_per_beat: 3,
    }), '材料已绑定到蓝图')
    if (result) {
      setBinding(result)
      const refreshed = await app.GetReferenceChapterBlueprint(novelId, activeBlueprint.blueprint_id)
      setActiveBlueprint(refreshed)
      setRevisionForm(formFromBlueprint(refreshed))
      await loadBlueprints()
    }
  }

  async function generateDraft() {
    if (!activeBlueprint) return
    const result = await run(() => app.GenerateReferenceAnchoredDraft({
      novel_id: novelId,
      blueprint_id: activeBlueprint.blueprint_id,
      beat_ids: [],
    }), '候选段落已生成')
    if (result) setDraft(result)
  }

  async function saveBlueprintEdits() {
    if (!activeBlueprint) return
    const beat = activeBlueprint.beats[0]
    if (!beat) {
      setError('当前蓝图没有可编辑节拍')
      return
    }

    const changes: reference.BlueprintRevisionChange[] = []
    const prefix = `beat:${beat.beat_id}:`

    addListChange(changes, 'known_facts', revisionForm.knownFacts, activeBlueprint.known_facts)
    addListChange(changes, 'forbidden_facts', revisionForm.forbiddenFacts, activeBlueprint.forbidden_facts)

    const beatStringFields: Array<[BlueprintRevisionStringKey, string, string]> = [
      ['narrativeFunction', 'narrative_function', beat.narrative_function],
      ['logicPremise', 'logic_premise', beat.logic_premise],
      ['conflictPressure', 'conflict_pressure', beat.conflict_pressure],
      ['causalityIn', 'causality_in', beat.causality_in],
      ['causalityOut', 'causality_out', beat.causality_out],
      ['transitionIn', 'transition_in', beat.transition_in],
      ['transitionOut', 'transition_out', beat.transition_out],
      ['povCharacter', 'pov_character', beat.pov_character],
      ['narrativeDistance', 'narrative_distance', beat.narrative_distance],
      ['emotionTrigger', 'emotion_trigger', beat.emotion_trigger],
      ['emotionBefore', 'emotion_before', beat.emotion_before],
      ['emotionAfter', 'emotion_after', beat.emotion_after],
      ['suppressedReaction', 'suppressed_reaction', beat.suppressed_reaction],
      ['externalEvidence', 'external_evidence', beat.external_evidence],
      ['narrationStrategy', 'narration_strategy', beat.narration_strategy],
      ['rhythmStrategy', 'rhythm_strategy', beat.rhythm_strategy],
      ['paragraphIntention', 'paragraph_intention', beat.paragraph_intention],
      ['executionMode', 'execution_mode', beat.execution_mode],
      ['antiScreenplayDuty', 'anti_screenplay_duty', beat.anti_screenplay_duty],
      ['sensoryAnchorTarget', 'sensory_anchor_target', beat.sensory_anchor_target],
      ['subtextPlan', 'subtext_plan', beat.subtext_plan],
      ['sourceBackedDetailTarget', 'source_backed_detail_target', beat.source_backed_detail_target],
      ['candidateRejectionRule', 'candidate_rejection_rule', beat.candidate_rejection_rule],
      ['maxRewriteLevel', 'max_rewrite_level', beat.max_rewrite_level],
      ['lockedPhrasePolicy', 'locked_phrase_policy', beat.locked_phrase_policy],
      ['noReuseReason', 'no_reuse_reason', beat.no_reuse_reason],
      ['referenceQuery', 'reference_query.query', beat.reference_query.query],
    ]

    const beatListFields: Array<[BlueprintRevisionStringKey, string, string[]]> = [
      ['viewpointAllowedKnowledge', 'viewpoint_allowed_knowledge', beat.viewpoint_allowed_knowledge],
      ['viewpointForbiddenKnowledge', 'viewpoint_forbidden_knowledge', beat.viewpoint_forbidden_knowledge],
      ['characterStatesBefore', 'character_states_before', beat.character_states_before],
      ['characterStatesAfter', 'character_states_after', beat.character_states_after],
      ['characterGoals', 'character_goals', beat.character_goals],
      ['characterMisbeliefs', 'character_misbeliefs', beat.character_misbeliefs],
      ['relationshipPressure', 'relationship_pressure', beat.relationship_pressure],
      ['sceneFacts', 'scene_facts', beat.scene_facts],
      ['beatForbiddenFacts', 'forbidden_facts', beat.forbidden_facts],
      ['requiredMaterialTypes', 'required_material_types', beat.required_material_types],
      ['proseDuties', 'prose_duties', beat.prose_duties],
      ['referenceMaterialTypes', 'reference_query.material_types', beat.reference_query.material_types],
      ['referenceEmotionTags', 'reference_query.emotion_tags', beat.reference_query.emotion_tags],
      ['referenceFunctionTags', 'reference_query.function_tags', beat.reference_query.function_tags],
      ['referencePovTags', 'reference_query.pov_tags', beat.reference_query.pov_tags],
      ['referenceTechniqueTags', 'reference_query.technique_tags', beat.reference_query.technique_tags],
    ]

    for (const [formKey, fieldName, currentValue] of beatStringFields) {
      addStringChange(changes, `${prefix}${fieldName}`, revisionForm[formKey], currentValue)
    }
    for (const [formKey, fieldName, currentValue] of beatListFields) {
      addListChange(changes, `${prefix}${fieldName}`, revisionForm[formKey], currentValue)
    }

    const nextMaxResults = revisionForm.referenceMaxResults.trim()
    if (nextMaxResults !== String(beat.reference_query.max_results)) {
      const parsed = Number.parseInt(nextMaxResults, 10)
      if (!Number.isFinite(parsed) || parsed < 1 || parsed > 50) {
        setError('引用最大结果数必须是 1 到 50 的整数')
        return
      }
      if (String(parsed) !== String(beat.reference_query.max_results)) {
        changes.push({ field_path: `${prefix}reference_query.max_results`, new_value: String(parsed) })
      }
    }
    addSlotPlanChange(changes, `${prefix}slot_plan`, revisionForm.slotPlan, beat.slot_plan)

    if (changes.length === 0) {
      setMessage('没有需要保存的蓝图修改')
      return
    }

    const revised = await run(() => app.ReviseReferenceChapterBlueprint({
      novel_id: novelId,
      blueprint_id: activeBlueprint.blueprint_id,
      changes,
      origin: 'ui',
      revision_reason: 'field-level blueprint edit',
    }), '蓝图已修订，需要重新评审和批准')
    if (revised) {
      setActiveBlueprint(revised)
      setRevisionForm(formFromBlueprint(revised))
      setBinding(null)
      setDraft(null)
      await loadBlueprints()
    }
  }

  return (
    <main className="flex-1 min-w-0 overflow-y-auto overscroll-contain bg-background">
      <div className="mx-auto max-w-6xl px-5 py-6 space-y-5">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2">
            <BookMarked className="h-4 w-4 text-muted-foreground" />
            <h2 className="text-sm font-semibold text-foreground">
              参考锚定
              <span className="ml-2 text-xs font-normal text-muted-foreground">{anchors.length} 个锚点</span>
            </h2>
          </div>
          <div className="flex items-center gap-2">
            {loading && <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />}
            <button onClick={() => { void loadAnchors(); void loadBlueprints() }} className="inline-flex items-center gap-1 px-2.5 py-1 rounded text-xs text-muted-foreground hover:text-foreground hover:bg-secondary transition-colors">
              <RefreshCcw className="h-3 w-3" />刷新
            </button>
          </div>
        </div>

        {error && <div className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">{error}</div>}
        {message && <div className="rounded-md border border-emerald-500/30 bg-emerald-500/10 px-3 py-2 text-xs text-emerald-700 dark:text-emerald-300">{message}</div>}

        <div className="grid grid-cols-1 xl:grid-cols-[360px_minmax(0,1fr)] gap-4">
          <section className="space-y-4">
            <div className="rounded-lg border border-border bg-card p-4">
              <div className="flex items-center gap-2 mb-3">
                <Plus className="h-3.5 w-3.5 text-muted-foreground" />
                <h3 className="text-xs font-semibold text-foreground">创建参考锚点</h3>
              </div>
              <div className="space-y-3">
                <Field label="标题">
                  <input value={anchorForm.title} onChange={event => setAnchorForm(form => ({ ...form, title: event.target.value }))} className={inputClass} placeholder="参考书名" />
                </Field>
                <Field label="作者">
                  <input value={anchorForm.author} onChange={event => setAnchorForm(form => ({ ...form, author: event.target.value }))} className={inputClass} placeholder="可选" />
                </Field>
                <Field label="本地路径">
                  <input value={anchorForm.sourcePath} onChange={event => setAnchorForm(form => ({ ...form, sourcePath: event.target.value }))} className={inputClass} placeholder="D:\\books\\reference.md" />
                </Field>
                <div className="grid grid-cols-2 gap-2">
                  <Field label="格式">
                    <select value={anchorForm.sourceKind} onChange={event => setAnchorForm(form => ({ ...form, sourceKind: event.target.value }))} className={inputClass}>
                      <option value="markdown">markdown</option>
                      <option value="text">text</option>
                    </select>
                  </Field>
                  <Field label="授权">
                    <select value={anchorForm.licenseStatus} onChange={event => setAnchorForm(form => ({ ...form, licenseStatus: event.target.value }))} className={inputClass}>
                      <option value="user_provided">user_provided</option>
                      <option value="unknown">unknown</option>
                    </select>
                  </Field>
                </div>
                <button onClick={createAnchor} disabled={loading} className="inline-flex w-full items-center justify-center gap-1.5 rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50">
                  <Plus className="h-3.5 w-3.5" />创建
                </button>
              </div>
            </div>

            <div className="rounded-lg border border-border bg-card p-4">
              <div className="flex items-center gap-2 mb-3">
                <BookMarked className="h-3.5 w-3.5 text-muted-foreground" />
                <h3 className="text-xs font-semibold text-foreground">锚点</h3>
              </div>
              {anchors.length === 0 ? (
                <p className="text-xs text-muted-foreground">暂无参考锚点</p>
              ) : (
                <div className="space-y-2">
                  {anchors.map(anchor => (
                    <div key={anchor.anchor_id} className="rounded-md border border-border bg-background px-3 py-2">
                      <label className="flex items-start gap-2">
                        <input
                          type="checkbox"
                          checked={selectedAnchorSet.has(anchor.anchor_id)}
                          onChange={event => {
                            setSelectedAnchorIds(ids => event.target.checked
                              ? [...ids, anchor.anchor_id]
                              : ids.filter(id => id !== anchor.anchor_id))
                          }}
                          className="mt-0.5"
                        />
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-xs font-medium text-foreground">{anchor.title}</span>
                          <span className={`block text-[11px] ${statusTone(anchor.status)}`}>{anchor.status}</span>
                        </span>
                        <button onClick={() => rebuildAnchor(anchor.anchor_id)} className="rounded p-1 text-muted-foreground hover:text-foreground hover:bg-secondary" title="重建">
                          <RefreshCcw className="h-3.5 w-3.5" />
                        </button>
                      </label>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </section>

          <section className="min-w-0 space-y-4">
            <div className="rounded-lg border border-border bg-card p-4">
              <div className="flex flex-wrap items-end gap-3">
                <div className="min-w-[220px] flex-1">
                  <Field label="材料搜索">
                    <input value={materialQuery} onChange={event => setMaterialQuery(event.target.value)} className={inputClass} placeholder="叙事功能、情绪或具体句子" />
                  </Field>
                </div>
                <button onClick={searchMaterials} disabled={loading} className="inline-flex items-center gap-1.5 rounded bg-secondary px-3 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50">
                  <Search className="h-3.5 w-3.5" />搜索
                </button>
              </div>
              {materials.length > 0 && (
                <div className="mt-3 grid grid-cols-1 lg:grid-cols-2 gap-2">
                  {materials.map(material => (
                    <div key={material.material_id} className="rounded-md border border-border bg-background p-3">
                      <div className="flex items-center justify-between gap-2">
                        <span className="text-[11px] text-muted-foreground">{material.material_type} · {material.function_tag || 'untagged'}</span>
                        <span className="text-[11px] text-muted-foreground">{material.pov_tag || 'pov?'}</span>
                      </div>
                      <p className="mt-1 line-clamp-3 text-xs leading-relaxed text-foreground">{material.text}</p>
                    </div>
                  ))}
                </div>
              )}
            </div>

            <div className="grid grid-cols-1 2xl:grid-cols-[360px_minmax(0,1fr)] gap-4">
              <div className="rounded-lg border border-border bg-card p-4">
                <div className="flex items-center gap-2 mb-3">
                  <FileSearch className="h-3.5 w-3.5 text-muted-foreground" />
                  <h3 className="text-xs font-semibold text-foreground">章节蓝图</h3>
                </div>
                <div className="space-y-3">
                  <div className="grid grid-cols-[88px_minmax(0,1fr)] gap-2">
                    <Field label="章节号">
                      <input value={blueprintForm.chapterNumber} onChange={event => setBlueprintForm(form => ({ ...form, chapterNumber: event.target.value }))} className={inputClass} inputMode="numeric" />
                    </Field>
                    <Field label="标题">
                      <input value={blueprintForm.title} onChange={event => setBlueprintForm(form => ({ ...form, title: event.target.value }))} className={inputClass} placeholder="可选" />
                    </Field>
                  </div>
                  <Field label="章节目标">
                    <textarea value={blueprintForm.chapterGoal} onChange={event => setBlueprintForm(form => ({ ...form, chapterGoal: event.target.value }))} className={`${inputClass} min-h-16 resize-y`} placeholder="本章要完成的逻辑、情绪或钩子" />
                  </Field>
                  <Field label="已知事实">
                    <textarea value={blueprintForm.knownFacts} onChange={event => setBlueprintForm(form => ({ ...form, knownFacts: event.target.value }))} className={`${inputClass} min-h-14 resize-y`} placeholder="一行一个" />
                  </Field>
                  <Field label="禁止事实">
                    <textarea value={blueprintForm.forbiddenFacts} onChange={event => setBlueprintForm(form => ({ ...form, forbiddenFacts: event.target.value }))} className={`${inputClass} min-h-14 resize-y`} placeholder="一行一个" />
                  </Field>
                  <button onClick={generateBlueprint} disabled={loading} className="inline-flex w-full items-center justify-center gap-1.5 rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50">
                    <Wand2 className="h-3.5 w-3.5" />生成蓝图
                  </button>
                </div>

                {blueprints.length > 0 && (
                  <div className="mt-4 border-t border-border pt-3 space-y-2">
                    {blueprints.slice(0, 8).map(blueprint => (
                      <button
                        key={blueprint.blueprint_id}
                        onClick={() => selectBlueprint(blueprint.blueprint_id)}
                        className={`w-full rounded-md border px-3 py-2 text-left transition-colors ${activeBlueprint?.blueprint_id === blueprint.blueprint_id ? 'border-primary bg-secondary' : 'border-border bg-background hover:bg-secondary/60'}`}
                      >
                        <span className="block truncate text-xs font-medium text-foreground">第{blueprint.chapter_number}章 · {blueprint.title}</span>
                        <span className={`block text-[11px] ${statusTone(blueprint.status)}`}>{blueprint.status}</span>
                      </button>
                    ))}
                  </div>
                )}
              </div>

              <BlueprintDetail
                blueprint={activeBlueprint}
                binding={binding}
                draft={draft}
                loading={loading}
                onReview={reviewBlueprint}
                onApprove={approveBlueprint}
                onBind={bindMaterials}
                onGenerateDraft={generateDraft}
                revisionForm={revisionForm}
                onRevisionFormChange={setRevisionForm}
                onSaveEdits={saveBlueprintEdits}
              />
            </div>
          </section>
        </div>
      </div>
    </main>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

function RevisionSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="space-y-2">
      <h5 className="text-[11px] font-semibold text-muted-foreground">{title}</h5>
      <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
        {children}
      </div>
    </div>
  )
}

function FindingSections({ sections, emptyText }: { sections: FindingSection[]; emptyText: string }) {
  if (sections.length === 0) {
    return <p className="mt-2 text-xs text-muted-foreground">{emptyText}</p>
  }

  return (
    <div className="mt-2 grid grid-cols-1 gap-2 lg:grid-cols-2">
      {sections.map(section => (
        <div key={section.label} className="rounded border border-border bg-card px-3 py-2">
          <p className="text-[11px] font-medium text-foreground">{section.label}</p>
          <ul className="mt-1 list-disc space-y-1 pl-4 text-xs leading-relaxed text-muted-foreground">
            {section.items.map((item, index) => <li key={index}>{item}</li>)}
          </ul>
        </div>
      ))}
    </div>
  )
}

function BlueprintDetail({
  blueprint,
  binding,
  draft,
  loading,
  onReview,
  onApprove,
  onBind,
  onGenerateDraft,
  revisionForm,
  onRevisionFormChange,
  onSaveEdits,
}: {
  blueprint: reference.ChapterBlueprint | null
  binding: reference.BlueprintMaterialBindingResult | null
  draft: reference.AnchoredDraft | null
  loading: boolean
  onReview: () => void
  onApprove: () => void
  onBind: () => void
  onGenerateDraft: () => void
  revisionForm: BlueprintRevisionForm
  onRevisionFormChange: Dispatch<SetStateAction<BlueprintRevisionForm>>
  onSaveEdits: () => void
}) {
  if (!blueprint) {
    return (
      <div className="rounded-lg border border-dashed border-border bg-card/60 p-6">
        <div className="flex h-full min-h-[260px] flex-col items-center justify-center text-center">
          <FileSearch className="h-8 w-8 text-muted-foreground" />
          <p className="mt-3 text-sm font-medium text-foreground">尚未选择蓝图</p>
          <p className="mt-1 max-w-sm text-xs leading-relaxed text-muted-foreground">生成或选择章节蓝图后，在这里评审逻辑、情绪、叙述、角色、引用和执行轨道。</p>
        </div>
      </div>
    )
  }

  const review = blueprint.latest_review
  const canApprove = review?.status === 'passed' && blueprint.status !== 'approved' && blueprint.status !== 'material_bound'
  const requiresReview = blueprint.status === 'draft' || blueprint.status === 'review_failed' || blueprint.status === 'stale'
  const reviewSections = review ? reviewFindings(review) : []
  const auditSections = draft?.audit ? auditFindings(draft.audit) : []
  const editableBeat = blueprint.beats[0]
  const updateRevisionField = (key: keyof BlueprintRevisionForm) =>
    (event: ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
      onRevisionFormChange(form => ({ ...form, [key]: event.target.value }))
    }
  const updateSlotPlanField = (index: number, key: keyof reference.SlotValue) =>
    (event: ChangeEvent<HTMLInputElement>) => {
      onRevisionFormChange(form => ({
        ...form,
        slotPlan: form.slotPlan.map((slot, slotIndex) =>
          slotIndex === index ? { ...slot, [key]: event.target.value } : slot),
      }))
    }
  const addSlotPlanRow = () => {
    onRevisionFormChange(form => ({
      ...form,
      slotPlan: [...form.slotPlan, { slot_name: '', value: '' }],
    }))
  }
  const removeSlotPlanRow = (index: number) => {
    onRevisionFormChange(form => ({
      ...form,
      slotPlan: form.slotPlan.filter((_, slotIndex) => slotIndex !== index),
    }))
  }

  return (
    <div className="min-w-0 rounded-lg border border-border bg-card p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate text-sm font-semibold text-foreground">第{blueprint.chapter_number}章 · {blueprint.title}</h3>
          <p className={`mt-1 text-xs ${statusTone(blueprint.status)}`}>{blueprint.status}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button onClick={onReview} disabled={loading} className={actionButtonClass}><ShieldCheck className="h-3.5 w-3.5" />评审</button>
          <button onClick={onApprove} disabled={loading || !canApprove} className={actionButtonClass}><CheckCircle2 className="h-3.5 w-3.5" />批准</button>
          <button onClick={onBind} disabled={loading || (blueprint.status !== 'approved' && blueprint.status !== 'material_bound')} className={actionButtonClass}><Link2 className="h-3.5 w-3.5" />绑定</button>
          <button onClick={onGenerateDraft} disabled={loading || blueprint.status !== 'material_bound'} className={actionButtonClass}><Wand2 className="h-3.5 w-3.5" />候选</button>
        </div>
      </div>

      {requiresReview && (
        <div className="mt-4 rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs leading-relaxed text-amber-700 dark:text-amber-300">
          当前蓝图需要重新评审和批准；材料绑定与候选生成会保持禁用，直到通过评审并批准。
        </div>
      )}

      <div className="mt-4 rounded-md border border-border bg-background p-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h4 className="text-xs font-semibold text-foreground">
            当前节拍字段{editableBeat ? <span className="ml-1 text-muted-foreground">#{editableBeat.beat_index}</span> : null}
          </h4>
          <button onClick={onSaveEdits} disabled={loading} className={actionButtonClass}>
            <CheckCircle2 className="h-3.5 w-3.5" />保存修订
          </button>
        </div>
        <div className="mt-3 space-y-5">
          <RevisionSection title="节拍逻辑与转场">
            <Field label="叙事功能">
              <textarea value={revisionForm.narrativeFunction} onChange={updateRevisionField('narrativeFunction')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="逻辑前提">
              <textarea value={revisionForm.logicPremise} onChange={updateRevisionField('logicPremise')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="冲突压力">
              <textarea value={revisionForm.conflictPressure} onChange={updateRevisionField('conflictPressure')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="因果进入">
              <textarea value={revisionForm.causalityIn} onChange={updateRevisionField('causalityIn')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="因果输出">
              <textarea value={revisionForm.causalityOut} onChange={updateRevisionField('causalityOut')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="转场进入">
              <textarea value={revisionForm.transitionIn} onChange={updateRevisionField('transitionIn')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="转场输出">
              <textarea value={revisionForm.transitionOut} onChange={updateRevisionField('transitionOut')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
          </RevisionSection>

          <RevisionSection title="事实与 POV">
            <Field label="已知事实">
              <textarea value={revisionForm.knownFacts} onChange={updateRevisionField('knownFacts')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
            <Field label="全局禁止事实">
              <textarea value={revisionForm.forbiddenFacts} onChange={updateRevisionField('forbiddenFacts')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
            <Field label="场景事实">
              <textarea value={revisionForm.sceneFacts} onChange={updateRevisionField('sceneFacts')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
            <Field label="节拍禁止事实">
              <textarea value={revisionForm.beatForbiddenFacts} onChange={updateRevisionField('beatForbiddenFacts')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
            <Field label="POV 角色">
              <input value={revisionForm.povCharacter} onChange={updateRevisionField('povCharacter')} className={inputClass} />
            </Field>
            <Field label="叙述距离">
              <input value={revisionForm.narrativeDistance} onChange={updateRevisionField('narrativeDistance')} className={inputClass} />
            </Field>
            <Field label="POV 可知边界">
              <textarea value={revisionForm.viewpointAllowedKnowledge} onChange={updateRevisionField('viewpointAllowedKnowledge')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
            <Field label="POV 禁知边界">
              <textarea value={revisionForm.viewpointForbiddenKnowledge} onChange={updateRevisionField('viewpointForbiddenKnowledge')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
          </RevisionSection>

          <RevisionSection title="角色与情绪">
            <Field label="角色前状态">
              <textarea value={revisionForm.characterStatesBefore} onChange={updateRevisionField('characterStatesBefore')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
            <Field label="角色后状态">
              <textarea value={revisionForm.characterStatesAfter} onChange={updateRevisionField('characterStatesAfter')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
            <Field label="角色目标">
              <textarea value={revisionForm.characterGoals} onChange={updateRevisionField('characterGoals')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="角色误信">
              <textarea value={revisionForm.characterMisbeliefs} onChange={updateRevisionField('characterMisbeliefs')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="关系压力">
              <textarea value={revisionForm.relationshipPressure} onChange={updateRevisionField('relationshipPressure')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="情绪触发">
              <input value={revisionForm.emotionTrigger} onChange={updateRevisionField('emotionTrigger')} className={inputClass} />
            </Field>
            <Field label="情绪前">
              <input value={revisionForm.emotionBefore} onChange={updateRevisionField('emotionBefore')} className={inputClass} />
            </Field>
            <Field label="情绪后">
              <input value={revisionForm.emotionAfter} onChange={updateRevisionField('emotionAfter')} className={inputClass} />
            </Field>
            <Field label="压抑反应">
              <input value={revisionForm.suppressedReaction} onChange={updateRevisionField('suppressedReaction')} className={inputClass} />
            </Field>
            <Field label="外部证据">
              <input value={revisionForm.externalEvidence} onChange={updateRevisionField('externalEvidence')} className={inputClass} />
            </Field>
          </RevisionSection>

          <RevisionSection title="叙述与执行">
            <Field label="叙述策略">
              <textarea value={revisionForm.narrationStrategy} onChange={updateRevisionField('narrationStrategy')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="节奏策略">
              <textarea value={revisionForm.rhythmStrategy} onChange={updateRevisionField('rhythmStrategy')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="段落意图">
              <textarea value={revisionForm.paragraphIntention} onChange={updateRevisionField('paragraphIntention')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
            <Field label="执行模式">
              <input value={revisionForm.executionMode} onChange={updateRevisionField('executionMode')} className={inputClass} />
            </Field>
            <Field label="反剧本职责">
              <textarea value={revisionForm.antiScreenplayDuty} onChange={updateRevisionField('antiScreenplayDuty')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="感官锚点">
              <input value={revisionForm.sensoryAnchorTarget} onChange={updateRevisionField('sensoryAnchorTarget')} className={inputClass} />
            </Field>
            <Field label="潜台词计划">
              <textarea value={revisionForm.subtextPlan} onChange={updateRevisionField('subtextPlan')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="细节目标">
              <textarea value={revisionForm.sourceBackedDetailTarget} onChange={updateRevisionField('sourceBackedDetailTarget')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="候选拒绝规则">
              <textarea value={revisionForm.candidateRejectionRule} onChange={updateRevisionField('candidateRejectionRule')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="正文职责">
              <textarea value={revisionForm.proseDuties} onChange={updateRevisionField('proseDuties')} className={`${inputClass} min-h-20 resize-y`} />
            </Field>
          </RevisionSection>

          <RevisionSection title="引用与复用策略">
            <Field label="引用查询">
              <input value={revisionForm.referenceQuery} onChange={updateRevisionField('referenceQuery')} className={inputClass} />
            </Field>
            <Field label="引用最大结果数">
              <input type="number" min={1} max={50} value={revisionForm.referenceMaxResults} onChange={updateRevisionField('referenceMaxResults')} className={inputClass} />
            </Field>
            <Field label="引用材料类型">
              <textarea value={revisionForm.referenceMaterialTypes} onChange={updateRevisionField('referenceMaterialTypes')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="必需材料类型">
              <textarea value={revisionForm.requiredMaterialTypes} onChange={updateRevisionField('requiredMaterialTypes')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="引用情绪标签">
              <textarea value={revisionForm.referenceEmotionTags} onChange={updateRevisionField('referenceEmotionTags')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="引用功能标签">
              <textarea value={revisionForm.referenceFunctionTags} onChange={updateRevisionField('referenceFunctionTags')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="引用 POV 标签">
              <textarea value={revisionForm.referencePovTags} onChange={updateRevisionField('referencePovTags')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="引用技法标签">
              <textarea value={revisionForm.referenceTechniqueTags} onChange={updateRevisionField('referenceTechniqueTags')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
            <Field label="最大改写级别">
              <input value={revisionForm.maxRewriteLevel} onChange={updateRevisionField('maxRewriteLevel')} className={inputClass} />
            </Field>
            <div className="lg:col-span-2">
              <Field label="槽位计划">
                <div className="space-y-2">
                  {revisionForm.slotPlan.map((slot, index) => (
                    <div key={index} className="grid grid-cols-[minmax(0,1fr)_minmax(0,1.4fr)_auto] gap-2">
                      <input
                        aria-label="槽位名"
                        value={slot.slot_name}
                        onChange={updateSlotPlanField(index, 'slot_name')}
                        className={inputClass}
                      />
                      <input
                        aria-label="槽位值"
                        value={slot.value}
                        onChange={updateSlotPlanField(index, 'value')}
                        className={inputClass}
                      />
                      <button
                        type="button"
                        aria-label="移除槽位"
                        onClick={() => removeSlotPlanRow(index)}
                        className="inline-flex h-8 w-8 items-center justify-center rounded border border-border text-muted-foreground hover:bg-secondary hover:text-foreground"
                      >
                        <Trash2 className="h-3.5 w-3.5" />
                      </button>
                    </div>
                  ))}
                  <button type="button" onClick={addSlotPlanRow} className={actionButtonClass}>
                    <Plus className="h-3.5 w-3.5" />新增槽位
                  </button>
                </div>
              </Field>
            </div>
            <Field label="锁定短语策略">
              <input value={revisionForm.lockedPhrasePolicy} onChange={updateRevisionField('lockedPhrasePolicy')} className={inputClass} />
            </Field>
            <Field label="不复用理由">
              <textarea value={revisionForm.noReuseReason} onChange={updateRevisionField('noReuseReason')} className={`${inputClass} min-h-16 resize-y`} />
            </Field>
          </RevisionSection>
        </div>
      </div>

      <div className="mt-4 grid grid-cols-1 lg:grid-cols-2 gap-3">
        <Track title="逻辑" track={blueprint.logic_analysis} />
        <Track title="情绪" track={blueprint.emotion_analysis} />
        <Track title="叙述" track={blueprint.narration_analysis} />
        <Track title="角色" track={blueprint.character_analysis} />
        <Track title="引用" track={blueprint.reference_analysis} />
        <Track title="执行" track={blueprint.execution_contract} />
      </div>

      <div className="mt-4 rounded-md border border-border bg-background p-3">
        <h4 className="text-xs font-semibold text-foreground">节拍</h4>
        <div className="mt-2 space-y-2">
          {blueprint.beats.map(beat => (
            <div key={beat.beat_id} className="rounded border border-border bg-card px-3 py-2">
              <div className="flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
                <span>#{beat.beat_index}</span>
                <span>{beat.beat_type}</span>
                <span>POV {beat.pov_character || blueprint.global_pov}</span>
                <span>{beat.execution_mode}</span>
              </div>
              <p className="mt-1 text-xs text-foreground">{beat.narrative_function}</p>
              <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">{beat.paragraph_intention}</p>
            </div>
          ))}
        </div>
      </div>

      {review && (
        <div className="mt-4 rounded-md border border-border bg-background p-3">
          <h4 className="text-xs font-semibold text-foreground">评审结果</h4>
          <p className={`mt-1 text-xs ${statusTone(review.status)}`}>{review.status} · {review.score.toFixed(2)}</p>
          <FindingSections sections={reviewSections} emptyText="当前评审没有返回结构化缺陷。" />
        </div>
      )}

      {binding && (
        <div className="mt-4 rounded-md border border-border bg-background p-3">
          <h4 className="text-xs font-semibold text-foreground">材料绑定</h4>
          <div className="mt-2 grid grid-cols-1 lg:grid-cols-2 gap-2">
            {binding.links.map(link => (
              <div key={link.link_id} className="rounded border border-border bg-card px-3 py-2 text-xs">
                <div className="flex items-center justify-between gap-2">
                  <span className="truncate text-foreground">{link.material_id}</span>
                  <span className="text-muted-foreground">{link.score.toFixed(2)}</span>
                </div>
                <p className="mt-1 text-[11px] text-muted-foreground">{link.intended_use} · {link.max_rewrite_level}</p>
                {scoreComponents(link).length > 0 && (
                  <div className="mt-2 flex flex-wrap gap-1">
                    {scoreComponents(link).map(([name, value]) => (
                      <span key={name} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                        {name} {value.toFixed(2)}
                      </span>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {draft && (
        <div className="mt-4 rounded-md border border-border bg-background p-3">
          <h4 className="text-xs font-semibold text-foreground">候选段落</h4>
          {draft.audit && <p className={`mt-1 text-xs ${statusTone(draft.audit.status)}`}>审计 {draft.audit.status} · {draft.audit.rewrite_level}</p>}
          {draft.audit && <FindingSections sections={auditSections} emptyText="当前草稿审计没有返回结构化问题。" />}
          <div className="mt-2 space-y-2">
            {draft.candidates.map(candidate => (
              <div key={candidate.candidate_id} className="rounded border border-border bg-card px-3 py-2">
                <div className="flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
                  <span>节拍 {candidate.beat_id}</span>
                  <span>材料 {candidate.material_id}</span>
                  <span>{candidate.rewrite_level}</span>
                  <span>{candidate.audit_status}</span>
                </div>
                {candidate.changed_slots.length > 0 && (
                  <div className="mt-2 flex flex-wrap gap-1">
                    {candidate.changed_slots.map(slot => (
                      <span key={`${slot.slot_name}:${slot.value}`} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                        {slot.slot_name} -&gt; {slot.value}
                      </span>
                    ))}
                  </div>
                )}
                {candidate.non_slot_edits.length > 0 && (
                  <div className="mt-2">
                    <p className="text-[11px] font-medium text-foreground">非槽位改动</p>
                    <ul className="mt-1 list-disc space-y-1 pl-4 text-[11px] leading-relaxed text-muted-foreground">
                      {candidate.non_slot_edits.map((edit, index) => <li key={index}>{edit}</li>)}
                    </ul>
                  </div>
                )}
                <p className="mt-1 whitespace-pre-wrap text-xs leading-relaxed text-foreground">{candidate.text}</p>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

function Track({ title, track }: { title: string; track: reference.ChapterBlueprintAnalysisTrack | reference.ChapterBlueprintExecutionTrack }) {
  const points = 'points' in track ? track.points : [
    ...track.paragraph_intentions,
    ...track.execution_modes,
    ...track.anti_screenplay_duties,
    ...track.source_backed_detail_targets,
  ]

  return (
    <div className="rounded-md border border-border bg-background p-3">
      <h4 className="text-xs font-semibold text-foreground">{title}</h4>
      <p className="mt-1 line-clamp-2 text-xs leading-relaxed text-muted-foreground">{track.summary}</p>
      {points.length > 0 && (
        <div className="mt-2 flex flex-wrap gap-1">
          {points.slice(0, 5).map((point, index) => (
            <span key={index} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">{point}</span>
          ))}
        </div>
      )}
    </div>
  )
}
