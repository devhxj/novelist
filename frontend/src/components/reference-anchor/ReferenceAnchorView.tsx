import { useCallback, useEffect, useMemo, useState } from 'react'
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

const inputClass = 'w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring'
const actionButtonClass = 'inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50'

function lines(value: string): string[] {
  return value
    .split(/\r?\n|;/)
    .map(item => item.trim())
    .filter(Boolean)
}

function statusTone(status: string): string {
  if (status === 'approved' || status === 'material_bound' || status === 'passed') return 'text-emerald-600 dark:text-emerald-400'
  if (status === 'failed' || status === 'review_failed' || status === 'stale') return 'text-destructive'
  return 'text-muted-foreground'
}

function joinErrors(items: string[]): string {
  return items.filter(Boolean).join('；')
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
      setBinding(null)
      setDraft(null)
      await loadBlueprints()
    }
  }

  async function selectBlueprint(blueprintId: number) {
    const blueprint = await run(() => app.GetReferenceChapterBlueprint(novelId, blueprintId))
    if (blueprint) {
      setActiveBlueprint(blueprint)
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

function BlueprintDetail({
  blueprint,
  binding,
  draft,
  loading,
  onReview,
  onApprove,
  onBind,
  onGenerateDraft,
}: {
  blueprint: reference.ChapterBlueprint | null
  binding: reference.BlueprintMaterialBindingResult | null
  draft: reference.AnchoredDraft | null
  loading: boolean
  onReview: () => void
  onApprove: () => void
  onBind: () => void
  onGenerateDraft: () => void
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
          <div className="mt-2 space-y-1 text-xs text-muted-foreground">
            {[joinErrors(review.logic_errors), joinErrors(review.emotion_errors), joinErrors(review.narration_errors), joinErrors(review.execution_errors), joinErrors(review.pov_errors), joinErrors(review.required_fixes)]
              .filter(Boolean)
              .map((item, index) => <p key={index}>{item}</p>)}
          </div>
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
              </div>
            ))}
          </div>
        </div>
      )}

      {draft && (
        <div className="mt-4 rounded-md border border-border bg-background p-3">
          <h4 className="text-xs font-semibold text-foreground">候选段落</h4>
          {draft.audit && <p className={`mt-1 text-xs ${statusTone(draft.audit.status)}`}>审计 {draft.audit.status} · {draft.audit.rewrite_level}</p>}
          <div className="mt-2 space-y-2">
            {draft.candidates.map(candidate => (
              <div key={candidate.candidate_id} className="rounded border border-border bg-card px-3 py-2">
                <div className="flex flex-wrap items-center gap-2 text-[11px] text-muted-foreground">
                  <span>{candidate.rewrite_level}</span>
                  <span>{candidate.audit_status}</span>
                  <span>{candidate.material_id}</span>
                </div>
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
