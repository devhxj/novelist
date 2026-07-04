import type { reference } from '@/lib/novelist/types'

export type BlueprintRevisionForm = {
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

export type BlueprintRevisionStringKey = {
  [Key in keyof BlueprintRevisionForm]: BlueprintRevisionForm[Key] extends string ? Key : never
}[keyof BlueprintRevisionForm]

export const EMPTY_REVISION_FORM: BlueprintRevisionForm = {
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

export function lines(value: string): string[] {
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

export function addStringChange(
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

export function addListChange(
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

export function addSlotPlanChange(
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

export function formFromBlueprint(blueprint: reference.ChapterBlueprint | null): BlueprintRevisionForm {
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
