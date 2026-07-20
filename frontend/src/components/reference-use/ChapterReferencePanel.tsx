import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Check, CornerDownLeft, Loader2, Sparkles, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { useApp } from '@/hooks/useApp'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics, reference } from '@/lib/novelist/types'
import { chapterNumFromPath, isChapterPath } from '@/components/content/types'

type ActiveChapterContext = {
  path: string
  title: string
  viewMode: string
}

type EditorSnapshot = {
  currentDraftText: string
  insertionOffset: number
}

type PendingAction = 'restore' | 'blueprints' | 'select' | 'drafts' | null

type ReferenceErrorState = {
  title: string
  message: string
  diagnostic: diagnostics.CopyableDiagnostic
}

interface Props {
  novelId: number
  activeChapter: ActiveChapterContext | null
  getEditorSnapshot: () => EditorSnapshot | null
  onApplyChapterText: (nextContent: string) => boolean
  onClose: () => void
}

const STRATEGY_LABELS: Record<string, string> = {
  progressive: '递进推进',
  contrast: '对照推进',
  focused: '集中推进',
}

function strategyLabel(value: string): string {
  return STRATEGY_LABELS[value] ?? '写作方案'
}

function toErrorState(
  caught: unknown,
  title: string,
  operation: string,
  bridgeMethod: string,
  detail: Record<string, unknown>,
): ReferenceErrorState {
  return {
    title,
    message: diagnosticMessage(caught, title),
    diagnostic: buildCopyableDiagnostic({
      error: caught,
      fallbackMessage: title,
      operation,
      bridgeMethod,
      detail,
    }),
  }
}

export default function ChapterReferencePanel({
  novelId,
  activeChapter,
  getEditorSnapshot,
  onApplyChapterText,
  onClose,
}: Props) {
  const app = useApp()
  const activePath = activeChapter?.path ?? ''
  const chapterNumber = activePath && isChapterPath(activePath)
    ? chapterNumFromPath(activePath)
    : 0
  const hasValidChapter = Number.isFinite(chapterNumber) && chapterNumber > 0
  const sessionId = `chapter:${novelId}:${chapterNumber}`
  const contextKey = `${novelId}:${chapterNumber}:${activePath}`
  const contextRef = useRef(contextKey)
  const goalInputRef = useRef<HTMLTextAreaElement | null>(null)
  const [goal, setGoal] = useState('')
  const [session, setSession] = useState<reference.WritingSession | null>(null)
  const [drafts, setDrafts] = useState<reference.WritingDraftCandidates | null>(null)
  const [selectedDraftId, setSelectedDraftId] = useState('')
  const [draftSourceText, setDraftSourceText] = useState<string | null>(null)
  const [pending, setPending] = useState<PendingAction>(null)
  const [error, setError] = useState<ReferenceErrorState | null>(null)
  const [statusMessage, setStatusMessage] = useState('')

  const selectedBlueprint = useMemo(
    () => session?.blueprints.find(blueprint => blueprint.blueprint_id === session.selected_blueprint_id) ?? null,
    [session],
  )
  const selectedDraft = useMemo(
    () => drafts?.candidates.find(candidate => candidate.candidate_id === selectedDraftId) ?? null,
    [drafts, selectedDraftId],
  )
  const busy = pending !== null
  const contextSummary = hasValidChapter
    ? `第 ${chapterNumber} 章 · ${activeChapter?.title || activePath}`
    : activeChapter?.title || '未打开章节'

  useEffect(() => {
    contextRef.current = contextKey
    let disposed = false
    queueMicrotask(() => {
      if (disposed || contextRef.current !== contextKey) return
      setGoal('')
      setSession(null)
      setDrafts(null)
      setSelectedDraftId('')
      setDraftSourceText(null)
      setError(null)
      setStatusMessage('')

      if (!hasValidChapter) {
        setPending(null)
        return
      }

      setPending('restore')
      void app.GetReferenceWritingSession({
        novel_id: novelId,
        chapter_number: chapterNumber,
        session_id: sessionId,
      }).then(restored => {
        if (disposed || contextRef.current !== contextKey) return
        setSession(restored)
        setGoal(restored?.goal ?? '')
        setStatusMessage(restored ? '已恢复本章写作蓝图。' : '')
      }).catch(caught => {
        if (disposed || contextRef.current !== contextKey) return
        setError(toErrorState(
          caught,
          '写作会话恢复失败',
          '恢复章节参考写作会话',
          'GetReferenceWritingSession',
          { novel_id: novelId, chapter_number: chapterNumber, session_id: sessionId },
        ))
      }).finally(() => {
        if (!disposed && contextRef.current === contextKey) {
          setPending(null)
          window.requestAnimationFrame(() => goalInputRef.current?.focus())
        }
      })
    })

    return () => {
      disposed = true
    }
  }, [app, chapterNumber, contextKey, hasValidChapter, novelId, sessionId])

  useEffect(() => {
    const frame = window.requestAnimationFrame(() => goalInputRef.current?.focus())
    return () => window.cancelAnimationFrame(frame)
  }, [contextKey])

  const generateBlueprints = useCallback(async () => {
    const normalizedGoal = goal.trim()
    if (!hasValidChapter || !normalizedGoal || busy) return

    const requestContext = contextKey
    setPending('blueprints')
    setSession(null)
    setDrafts(null)
    setSelectedDraftId('')
    setDraftSourceText(null)
    setError(null)
    setStatusMessage('')
    try {
      const result = await app.GenerateReferenceBlueprints({
        novel_id: novelId,
        chapter_number: chapterNumber,
        session_id: sessionId,
        goal: normalizedGoal,
        requested_count: 3,
      })
      if (contextRef.current !== requestContext) return
      if (result.blueprints.length === 0) {
        throw new Error('服务端未返回写作蓝图。')
      }
      setSession(result)
      setGoal(result.goal)
      setStatusMessage(`已生成 ${result.blueprints.length} 份写作蓝图。`)
    } catch (caught) {
      if (contextRef.current !== requestContext) return
      setError(toErrorState(
        caught,
        '蓝图生成失败',
        '生成章节参考写作蓝图',
        'GenerateReferenceBlueprints',
        { novel_id: novelId, chapter_number: chapterNumber, session_id: sessionId },
      ))
    } finally {
      if (contextRef.current === requestContext) setPending(null)
    }
  }, [app, busy, chapterNumber, contextKey, goal, hasValidChapter, novelId, sessionId])

  const selectBlueprint = useCallback(async (blueprintId: string) => {
    if (!hasValidChapter || busy || !session) return

    const requestContext = contextKey
    setPending('select')
    setDrafts(null)
    setSelectedDraftId('')
    setDraftSourceText(null)
    setError(null)
    setStatusMessage('')
    try {
      const result = await app.SelectReferenceBlueprint({
        novel_id: novelId,
        chapter_number: chapterNumber,
        session_id: sessionId,
        blueprint_id: blueprintId,
      })
      if (contextRef.current !== requestContext) return
      setSession(result)
      setStatusMessage('已选择写作蓝图。')
    } catch (caught) {
      if (contextRef.current !== requestContext) return
      setSession(null)
      setError(toErrorState(
        caught,
        '蓝图选择失败',
        '选择章节参考写作蓝图',
        'SelectReferenceBlueprint',
        { novel_id: novelId, chapter_number: chapterNumber, session_id: sessionId, blueprint_id: blueprintId },
      ))
    } finally {
      if (contextRef.current === requestContext) setPending(null)
    }
  }, [app, busy, chapterNumber, contextKey, hasValidChapter, novelId, session, sessionId])

  const generateDrafts = useCallback(async () => {
    if (!hasValidChapter || busy || !selectedBlueprint) return
    const snapshot = getEditorSnapshot()
    if (!snapshot) {
      setError(toErrorState(
        new Error('当前章节编辑器不可用。'),
        '正文生成失败',
        '读取章节编辑器正文',
        'GenerateReferenceDraftCandidates',
        { novel_id: novelId, chapter_number: chapterNumber },
      ))
      return
    }

    const requestContext = contextKey
    setPending('drafts')
    setDrafts(null)
    setSelectedDraftId('')
    setDraftSourceText(null)
    setError(null)
    setStatusMessage('')
    try {
      const result = await app.GenerateReferenceDraftCandidates({
        novel_id: novelId,
        chapter_number: chapterNumber,
        session_id: sessionId,
        blueprint_id: selectedBlueprint.blueprint_id,
        current_draft_text: snapshot.currentDraftText,
        insertion_offset: snapshot.insertionOffset,
        slot_values: {},
        requested_count: 3,
      })
      if (contextRef.current !== requestContext) return
      if (result.candidates.length === 0) {
        throw new Error('服务端未返回正文候选。')
      }
      setDrafts(result)
      setDraftSourceText(snapshot.currentDraftText)
      setStatusMessage(`已生成 ${result.candidates.length} 份正文候选。`)
    } catch (caught) {
      if (contextRef.current !== requestContext) return
      setError(toErrorState(
        caught,
        '正文生成失败',
        '生成章节参考正文候选',
        'GenerateReferenceDraftCandidates',
        {
          novel_id: novelId,
          chapter_number: chapterNumber,
          session_id: sessionId,
          blueprint_id: selectedBlueprint.blueprint_id,
          current_draft_length: snapshot.currentDraftText.length,
        },
      ))
    } finally {
      if (contextRef.current === requestContext) setPending(null)
    }
  }, [app, busy, chapterNumber, contextKey, getEditorSnapshot, hasValidChapter, novelId, selectedBlueprint, sessionId])

  const applyDraft = useCallback(() => {
    if (!selectedDraft || !selectedDraft.audit.passed) return
    const snapshot = getEditorSnapshot()
    if (!snapshot || snapshot.currentDraftText !== draftSourceText) {
      setDrafts(null)
      setSelectedDraftId('')
      setDraftSourceText(null)
      setStatusMessage('')
      setError(toErrorState(
        new Error('章节正文已变化，请重新生成正文候选。'),
        '正文插入失败',
        '校验章节编辑器正文',
        'EditorBuffer',
        { novel_id: novelId, chapter_number: chapterNumber },
      ))
      return
    }

    if (!onApplyChapterText(selectedDraft.chapter_text_after_insertion)) {
      setError(toErrorState(
        new Error('当前章节编辑器拒绝了正文写入。'),
        '正文插入失败',
        '写入章节编辑器',
        'EditorBuffer',
        { novel_id: novelId, chapter_number: chapterNumber, candidate_id: selectedDraft.candidate_id },
      ))
      return
    }

    setDrafts(null)
    setSelectedDraftId('')
    setDraftSourceText(null)
    setError(null)
    setStatusMessage('正文已插入编辑器。')
  }, [chapterNumber, draftSourceText, getEditorSnapshot, novelId, onApplyChapterText, selectedDraft])

  return (
    <aside
      data-testid="chapter-reference-panel"
      aria-label="章节参考素材"
      aria-busy={busy}
      className="flex h-full w-[440px] max-w-[45vw] shrink-0 flex-col border-l bg-card max-[1100px]:fixed max-[1100px]:inset-x-0 max-[1100px]:bottom-6 max-[1100px]:top-11 max-[1100px]:z-40 max-[1100px]:h-auto max-[1100px]:w-auto max-[1100px]:max-w-none max-[1100px]:border-l-0 max-[1100px]:shadow-lg"
    >
      <header className="flex items-start justify-between gap-3 border-b px-4 py-3">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-foreground">参考写作</h2>
          <p className="mt-0.5 break-words text-xs text-muted-foreground">{contextSummary}</p>
        </div>
        <Button type="button" variant="ghost" size="icon-sm" onClick={onClose} aria-label="关闭参考素材面板">
          <X />
        </Button>
      </header>

      <div className="min-h-0 flex-1 overflow-y-auto px-4 py-4">
        {!hasValidChapter && (
          <ErrorCallout
            compact
            title="无法开始参考写作"
            message="当前文件不是可识别的章节正文。"
          />
        )}

        {error && (
          <ErrorCallout
            compact
            title={error.title}
            message={error.message}
            diagnostic={error.diagnostic}
            onClose={() => setError(null)}
            className="mb-4"
          />
        )}

        {statusMessage && (
          <p role="status" aria-live="polite" className="mb-4 border-l-2 border-primary px-2 text-xs text-muted-foreground">
            {statusMessage}
          </p>
        )}

        <section className="border-b pb-4">
          <div className="mb-2 flex items-center justify-between gap-3">
            <label htmlFor="chapter-reference-goal" className="text-xs font-semibold text-foreground">章节目标</label>
            {pending === 'restore' && <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" aria-label="正在恢复写作会话" />}
          </div>
          <textarea
            ref={goalInputRef}
            id="chapter-reference-goal"
            aria-label="章节目标"
            value={goal}
            maxLength={2_000}
            disabled={!hasValidChapter || busy}
            onChange={event => {
              const nextGoal = event.target.value
              setGoal(nextGoal)
              if (session && nextGoal.trim() !== session.goal) {
                setSession(null)
                setDrafts(null)
                setSelectedDraftId('')
                setDraftSourceText(null)
                setStatusMessage('')
              }
            }}
            className="min-h-24 w-full resize-y rounded-md border border-input bg-background px-3 py-2 text-sm leading-relaxed text-foreground outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60"
            placeholder="写下本章要推进的冲突、信息或人物变化"
          />
          <Button
            type="button"
            size="sm"
            data-testid="chapter-reference-generate-blueprints"
            onClick={() => { void generateBlueprints() }}
            disabled={!hasValidChapter || busy || !goal.trim()}
            className="mt-3 w-full"
          >
            {pending === 'blueprints' ? <Loader2 className="animate-spin" /> : <Sparkles />}
            {session ? '重新生成蓝图' : '生成蓝图'}
          </Button>
        </section>

        {session && (
          <BlueprintList
            blueprints={session.blueprints}
            selectedBlueprintId={session.selected_blueprint_id}
            disabled={busy}
            onSelect={blueprintId => { void selectBlueprint(blueprintId) }}
          />
        )}

        {selectedBlueprint && (
          <section className="border-b py-4">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              data-testid="chapter-reference-generate-drafts"
              onClick={() => { void generateDrafts() }}
              disabled={busy}
              className="w-full"
            >
              {pending === 'drafts' ? <Loader2 className="animate-spin" /> : <CornerDownLeft />}
              生成正文
            </Button>
          </section>
        )}

        {drafts && (
          <DraftList
            drafts={drafts.candidates}
            selectedDraftId={selectedDraftId}
            disabled={busy}
            onSelect={setSelectedDraftId}
          />
        )}

        {selectedDraft && (
          <div className="border-t py-3">
            <Button
              type="button"
              size="sm"
              onClick={applyDraft}
              disabled={!selectedDraft.audit.passed}
              className="w-full"
            >
              <CornerDownLeft />
              插入正文
            </Button>
          </div>
        )}
      </div>
    </aside>
  )
}

function BlueprintList({
  blueprints,
  selectedBlueprintId,
  disabled,
  onSelect,
}: {
  blueprints: reference.WritingBlueprint[]
  selectedBlueprintId: string
  disabled: boolean
  onSelect: (blueprintId: string) => void
}) {
  return (
    <section data-testid="chapter-reference-blueprints" className="border-b py-4">
      <h3 className="mb-3 text-xs font-semibold text-foreground">写作蓝图</h3>
      <div role="radiogroup" aria-label="写作蓝图" className="space-y-2">
        {blueprints.map((blueprint, blueprintIndex) => {
          const selected = blueprint.blueprint_id === selectedBlueprintId
          return (
            <article
              key={blueprint.blueprint_id}
              data-testid="chapter-reference-blueprint-card"
              className={`rounded-md border px-3 py-3 ${selected ? 'border-primary bg-primary/5' : 'border-border bg-background'}`}
            >
              <div className="flex items-center justify-between gap-3">
                <h4 className="text-xs font-semibold text-foreground">方案 {blueprintIndex + 1}</h4>
                <span className="text-[11px] text-muted-foreground">{strategyLabel(blueprint.strategy)}</span>
              </div>
              <ol className="mt-2 space-y-2">
                {blueprint.beats.map((beat, beatIndex) => (
                  <li key={beat.beat_id} className="flex gap-2 text-xs leading-relaxed text-foreground">
                    <span className="shrink-0 text-muted-foreground">{beatIndex + 1}.</span>
                    <span>{beat.intent}</span>
                  </li>
                ))}
              </ol>
              {selected ? (
                <p data-testid="chapter-reference-blueprint-selected" className="mt-3 flex items-center gap-1.5 text-xs font-medium text-primary">
                  <Check className="h-3.5 w-3.5" />
                  已选择
                </p>
              ) : (
                <Button
                  type="button"
                  variant="outline"
                  size="xs"
                  disabled={disabled}
                  onClick={() => onSelect(blueprint.blueprint_id)}
                  className="mt-3"
                >
                  选择蓝图
                </Button>
              )}
            </article>
          )
        })}
      </div>
    </section>
  )
}

function DraftList({
  drafts,
  selectedDraftId,
  disabled,
  onSelect,
}: {
  drafts: reference.WritingDraftCandidate[]
  selectedDraftId: string
  disabled: boolean
  onSelect: (candidateId: string) => void
}) {
  return (
    <section data-testid="chapter-reference-drafts" className="py-4">
      <h3 className="mb-3 text-xs font-semibold text-foreground">正文候选</h3>
      <div role="radiogroup" aria-label="正文候选" className="space-y-3">
        {drafts.map((draft, draftIndex) => {
          const selected = draft.candidate_id === selectedDraftId
          return (
            <article
              key={draft.candidate_id}
              data-testid="chapter-reference-draft-card"
              className={`rounded-md border px-3 py-3 ${selected ? 'border-primary bg-primary/5' : 'border-border bg-background'}`}
            >
              <div className="flex items-center justify-between gap-3">
                <h4 className="text-xs font-semibold text-foreground">正文 {draftIndex + 1}</h4>
                <span className="text-[11px] text-muted-foreground">{draft.sources.length} 个来源</span>
              </div>
              <p className="mt-3 whitespace-pre-wrap break-words text-sm leading-6 text-foreground">{draft.text}</p>
              {!draft.audit.passed && (
                <div role="alert" className="mt-3 text-xs text-destructive">
                  {draft.audit.errors.join('；') || '正文审计未通过。'}
                </div>
              )}
              {selected ? (
                <p className="mt-3 flex items-center gap-1.5 text-xs font-medium text-primary">
                  <Check className="h-3.5 w-3.5" />
                  已选择
                </p>
              ) : (
                <Button
                  type="button"
                  variant="outline"
                  size="xs"
                  disabled={disabled || !draft.audit.passed}
                  onClick={() => onSelect(draft.candidate_id)}
                  className="mt-3"
                >
                  选择正文
                </Button>
              )}
            </article>
          )
        })}
      </div>
    </section>
  )
}
