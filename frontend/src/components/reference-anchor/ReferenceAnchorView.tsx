import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import {
  BookMarked,
  FileSearch,
  FolderOpen,
  Loader2,
  Plus,
  RefreshCcw,
  Search,
  Wand2,
} from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reference } from '@/lib/novelist/types'
import { BlueprintDetail } from './BlueprintDetail'
import {
  EMPTY_REVISION_FORM,
  addListChange,
  addSlotPlanChange,
  addStringChange,
  formFromBlueprint,
  lines,
} from './blueprintRevision'
import type { BlueprintRevisionForm, BlueprintRevisionStringKey } from './blueprintRevision'
import { inputClass, statusTone } from './referenceAnchorStyles'

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

function sourceKindFromPath(path: string, fallback: string): string {
  const lowerPath = path.toLowerCase()
  if (lowerPath.endsWith('.txt')) return 'text'
  if (lowerPath.endsWith('.md') || lowerPath.endsWith('.markdown')) return 'markdown'
  return fallback
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

  async function pickReferenceSourceFile() {
    const pickedPath = await run(() => app.PickReferenceSourceFile())
    if (!pickedPath?.trim()) {
      return
    }

    setAnchorForm(form => ({
      ...form,
      sourcePath: pickedPath,
      sourceKind: sourceKindFromPath(pickedPath, form.sourceKind),
    }))
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
                <div>
                  <span className="mb-1 block text-xs font-medium text-muted-foreground">本地路径</span>
                  <div className="flex items-center gap-2">
                    <input value={anchorForm.sourcePath} onChange={event => setAnchorForm(form => ({ ...form, sourcePath: event.target.value }))} className={`${inputClass} min-w-0 flex-1`} placeholder="D:\\books\\reference.md" aria-label="本地路径" />
                    <button
                      type="button"
                      onClick={pickReferenceSourceFile}
                      disabled={loading}
                      className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-md border border-border bg-background text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
                      title="选择文件"
                      aria-label="选择参考源文件"
                    >
                      <FolderOpen className="h-3.5 w-3.5" />
                    </button>
                  </div>
                </div>
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

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}
