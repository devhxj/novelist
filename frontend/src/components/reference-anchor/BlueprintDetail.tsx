import type { ChangeEvent, Dispatch, ReactNode, SetStateAction } from 'react'
import {
  CheckCircle2,
  FileSearch,
  Link2,
  Plus,
  ShieldCheck,
  Trash2,
  Wand2,
} from 'lucide-react'
import type { reference } from '@/lib/novelist/types'
import type { BlueprintRevisionForm } from './blueprintRevision'
import { actionButtonClass, inputClass, statusTone } from './referenceAnchorStyles'

type FindingSection = {
  label: string
  items: string[]
}

type BlueprintDetailProps = {
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

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

function RevisionSection({ title, children }: { title: string; children: ReactNode }) {
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

function ReviewDefects({ defects }: { defects: reference.ChapterBlueprintReviewDefect[] }) {
  if (defects.length === 0) {
    return null
  }

  return (
    <div className="mt-2 grid grid-cols-1 gap-2">
      {defects.map((defect, index) => (
        <div key={`${defect.field_path}:${defect.reason}:${index}`} className="rounded border border-border bg-card px-3 py-2">
          <div className="flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
            <span className={statusTone(defect.severity)}>{defect.severity}</span>
            <span>{defect.category}</span>
            {defect.beat_id && <span>beat {defect.beat_id}</span>}
            {defect.field_path && <span className="break-all">{defect.field_path}</span>}
          </div>
          <p className="mt-1 text-xs leading-relaxed text-foreground">{defect.reason}</p>
          <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">{defect.required_fix}</p>
        </div>
      ))}
    </div>
  )
}

export function BlueprintDetail({
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
}: BlueprintDetailProps) {
  if (!blueprint) {
    return (
      <div data-testid="reference-blueprint-detail" className="rounded-lg border border-dashed border-border bg-card/60 p-6">
        <div className="flex h-full min-h-[260px] flex-col items-center justify-center text-center">
          <FileSearch className="h-8 w-8 text-muted-foreground" />
          <p className="mt-3 text-sm font-medium text-foreground">尚未选择蓝图</p>
          <p className="mt-1 max-w-sm text-xs leading-relaxed text-muted-foreground">生成或选择章节蓝图后，在这里评审逻辑、情绪、叙述、角色、引用和执行轨道。</p>
        </div>
      </div>
    )
  }

  const review = blueprint.latest_review
  const isStale = blueprint.status === 'stale'
  const canApprove = review?.status === 'passed' && blueprint.status !== 'approved' && blueprint.status !== 'material_bound' && !isStale
  const requiresReview = blueprint.status === 'draft' || blueprint.status === 'review_failed'
  const reviewSections = review ? reviewFindings(review) : []
  const reviewDefects = review?.defects?.filter(defect => defect.reason || defect.required_fix) ?? []
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
    <div data-testid="reference-blueprint-detail" className="min-w-0 rounded-lg border border-border bg-card p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <h3 className="truncate text-sm font-semibold text-foreground">第{blueprint.chapter_number}章 · {blueprint.title}</h3>
          <p className={`mt-1 text-xs ${statusTone(blueprint.status)}`}>{blueprint.status}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          <button onClick={onReview} disabled={loading || isStale} className={actionButtonClass}><ShieldCheck className="h-3.5 w-3.5" />评审</button>
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

      {isStale && (
        <div className="mt-4 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs leading-relaxed text-destructive">
          章节规划已变化。此蓝图保留为只读对比，不能继续评审、批准、修订、绑定材料或生成候选；请生成新的章节蓝图。
        </div>
      )}

      <div className="mt-4 rounded-md border border-border bg-background p-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h4 className="text-xs font-semibold text-foreground">
            当前节拍字段{editableBeat ? <span className="ml-1 text-muted-foreground">#{editableBeat.beat_index}</span> : null}
          </h4>
          <button onClick={onSaveEdits} disabled={loading || isStale} className={actionButtonClass}>
            <CheckCircle2 className="h-3.5 w-3.5" />保存修订
          </button>
        </div>
        <fieldset disabled={isStale} className="mt-3 space-y-5">
          <legend className="sr-only">蓝图修订字段</legend>
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
        </fieldset>
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
          {reviewDefects.length > 0
            ? <ReviewDefects defects={reviewDefects} />
            : <FindingSections sections={reviewSections} emptyText="当前评审没有返回结构化缺陷。" />}
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
                {link.fit_explanation && (
                  <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">{link.fit_explanation}</p>
                )}
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
