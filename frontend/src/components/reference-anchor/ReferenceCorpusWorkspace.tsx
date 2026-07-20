import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  BookOpenCheck,
  CheckCircle2,
  CircleAlert,
  ClipboardCheck,
  FileStack,
  Loader2,
  Play,
  RefreshCcw,
  ScanSearch,
  Sparkles,
  Workflow,
  XCircle,
} from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { BridgeError } from '@/lib/novelist/bridge'
import type { reference } from '@/lib/novelist/types'

type Props = {
  novelId: number
  refreshKey: number
  anchors: reference.Anchor[]
  selectedAnchorIds: number[]
  onMaterializationChange: () => void
}

type Action = 'analyze' | 'manual-preview' | 'confirm' | 'enqueue' | 'retry' | null

const numberFormatter = new Intl.NumberFormat('zh-CN')

function formatCount(value: number): string {
  return numberFormatter.format(Math.max(0, value))
}

function runTone(status: string): string {
  if (status === 'completed') return 'text-emerald-700 dark:text-emerald-300'
  if (status === 'failed' || status === 'cancelled') return 'text-destructive'
  if (status === 'running' || status === 'queued') return 'text-sky-700 dark:text-sky-300'
  return 'text-muted-foreground'
}

function profileStateLabel(profile: reference.ChapterSplitProfile): string {
  if (profile.status === 'confirmed') return '已冻结'
  if (profile.status === 'stale') return '来源已变化'
  return '待确认'
}

function stageLabel(stage: string): string {
  const labels: Record<string, string> = {
    pending: '等待处理',
    extracting: '提取材料',
    embedding: '生成向量',
    indexing: '建立索引',
    completed: '完成',
    failed: '失败',
    cancelled: '已取消',
  }
  return labels[stage] ?? stage.replaceAll('_', ' ')
}

function chapterSplitErrorMessage(error: unknown): string {
  if (error instanceof BridgeError && error.code === 'materialization_chapter_split_output_invalid') {
    return '自动分析返回的章节证据无法对应原文标题。请重新分析，或输入章节分隔模板后预览。'
  }
  return '自动章节分析失败。请检查当前大模型配置和来源文件后重试。'
}

export default function ReferenceCorpusWorkspace({
  novelId,
  refreshKey,
  anchors,
  selectedAnchorIds,
  onMaterializationChange,
}: Props) {
  const app = useApp()
  const [profile, setProfile] = useState<reference.ChapterSplitProfile | null>(null)
  const [run, setRun] = useState<reference.MaterializationStatus | null>(null)
  const [progress, setProgress] = useState<reference.MaterializationChapterProgress[]>([])
  const [materials, setMaterials] = useState<reference.ReferenceMaterialListItem[]>([])
  const [materialPage, setMaterialPage] = useState(1)
  const [materialTotal, setMaterialTotal] = useState(0)
  const [materialTotalPages, setMaterialTotalPages] = useState(0)
  const [batchSize, setBatchSize] = useState<5 | 10>(5)
  const [manualTemplate, setManualTemplate] = useState('')
  const [action, setAction] = useState<Action>(null)
  const [error, setError] = useState<string | null>(null)
  const requestIdRef = useRef(0)
  const notifiedCompletedRunRef = useRef<string | null>(null)

  const selectedAnchor = useMemo(() => {
    const selected = new Set(selectedAnchorIds)
    return anchors.find((anchor) => selected.has(anchor.anchor_id)) ?? null
  }, [anchors, selectedAnchorIds])

  const loadRun = useCallback(async (anchor: reference.Anchor | null) => {
    const requestId = requestIdRef.current + 1
    requestIdRef.current = requestId
    if (!anchor || !novelId) {
      setRun(null)
      setProgress([])
      setMaterials([])
      setMaterialTotal(0)
      setMaterialTotalPages(0)
      return
    }

    try {
      const status = await app.GetReferenceMaterializationStatus({
        novel_id: novelId,
        anchor_id: anchor.anchor_id,
      })
      if (requestId !== requestIdRef.current) return
      setRun(status)
    } catch {
      if (requestId === requestIdRef.current) {
        setError('无法读取该来源的材料化状态。请刷新后重试。')
      }
    }
  }, [app, novelId])

  const loadRunDetail = useCallback(async (status: reference.MaterializationStatus | null, page: number) => {
    if (!status || !novelId) {
      setProgress([])
      setMaterials([])
      setMaterialTotal(0)
      setMaterialTotalPages(0)
      return
    }

    try {
      const [nextProgress, nextMaterials] = await Promise.all([
        app.ListReferenceMaterializationChapterProgress({
          novel_id: novelId,
          anchor_id: status.anchor_id,
          run_id: status.run_id,
          page: 1,
          size: 30,
        }),
        status.status === 'completed'
          ? app.ListReferenceMaterials({
              novel_id: novelId,
              anchor_id: status.anchor_id,
              page,
              size: 10,
            })
          : Promise.resolve(null),
      ])
      setProgress(nextProgress.items ?? [])
      setMaterials(nextMaterials?.items ?? [])
      setMaterialTotal(nextMaterials?.total ?? 0)
      setMaterialTotalPages(nextMaterials?.total_pages ?? 0)
    } catch {
      setError('材料化进度或当前材料加载失败。请刷新后重试。')
    }
  }, [app, novelId])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setError(null)
      setProfile(null)
      setManualTemplate('')
      setMaterialPage(1)
      void loadRun(selectedAnchor)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [loadRun, refreshKey, selectedAnchor])

  useEffect(() => {
    const timer = window.setTimeout(() => { void loadRunDetail(run, materialPage) }, 0)
    return () => window.clearTimeout(timer)
  }, [loadRunDetail, materialPage, run])

  useEffect(() => {
    if (!run || run.status !== 'queued' && run.status !== 'running') return
    const timer = window.setInterval(() => {
      void loadRun(selectedAnchor)
    }, 3_000)
    return () => window.clearInterval(timer)
  }, [loadRun, run, selectedAnchor])

  useEffect(() => {
    if (run?.status !== 'completed' || notifiedCompletedRunRef.current === run.run_id) return
    notifiedCompletedRunRef.current = run.run_id
    onMaterializationChange()
  }, [onMaterializationChange, run])

  const analyze = async () => {
    if (!selectedAnchor) return
    setAction('analyze')
    setError(null)
    try {
      const nextProfile = await app.AnalyzeReferenceChapterSplit({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
      })
      setProfile(nextProfile)
    } catch (error) {
      setError(chapterSplitErrorMessage(error))
    } finally {
      setAction(null)
    }
  }

  const previewManual = async () => {
    if (!selectedAnchor || !manualTemplate.trim()) return
    setAction('manual-preview')
    setError(null)
    try {
      const nextProfile = await app.PreviewReferenceChapterSplit({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
        delimiter_template: manualTemplate.trim(),
      })
      setProfile(nextProfile)
    } catch {
      setError('章节分隔模板无法应用到整本来源。请调整模板后重试。')
    } finally {
      setAction(null)
    }
  }

  const confirmProfile = async () => {
    if (!selectedAnchor || !profile) return
    setAction('confirm')
    setError(null)
    try {
      const confirmed = await app.ConfirmReferenceChapterSplit({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
        split_profile_id: profile.split_profile_id,
      })
      setProfile(confirmed)
    } catch {
      setError('章节边界确认失败。来源可能已变化，请重新分析。')
    } finally {
      setAction(null)
    }
  }

  const enqueue = async () => {
    if (!selectedAnchor || !profile || profile.status !== 'confirmed') return
    setAction('enqueue')
    setError(null)
    try {
      const status = await app.EnqueueReferenceMaterialization({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
        split_profile_id: profile.split_profile_id,
        chapter_batch_size: batchSize,
      })
      setRun(status)
    } catch {
      setError('材料化未能启动。大模型、向量模型和索引均必须可用；修复后请显式重试。')
    } finally {
      setAction(null)
    }
  }

  const retry = async () => {
    if (!run || !selectedAnchor) return
    setAction('retry')
    setError(null)
    try {
      const status = await app.RetryReferenceMaterialization({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
        run_id: run.run_id,
      })
      setRun(status)
    } catch {
      setError('材料化重试未能启动。请先修复显示的模型或索引问题。')
    } finally {
      setAction(null)
    }
  }

  const activeProfile = profile ?? (run
    ? {
        split_profile_id: run.split_profile_id,
        status: 'confirmed' as const,
        chapter_count: run.total_chapters,
        delimiter_template: '',
        split_mode: 'auto' as const,
        pattern_kind: '',
        source_hash: '',
        anchor_id: run.anchor_id,
        sample_char_count: 0,
        boundaries: [],
      }
    : null)

  if (!selectedAnchor) {
    return (
      <main data-testid="reference-corpus-workspace" className="reference-materialization-surface min-w-0 flex-1 overflow-y-auto bg-background">
        <div className="mx-auto flex min-h-full max-w-5xl flex-col items-center justify-center px-6 text-center">
          <FileStack className="h-8 w-8 text-muted-foreground/55" aria-hidden="true" />
          <h1 className="mt-3 text-base font-semibold text-foreground">选择一个参考来源</h1>
          <p className="mt-1 max-w-md text-xs leading-5 text-muted-foreground">从左侧选中已导入书籍后，在这里确认章节边界并启动材料化。</p>
        </div>
      </main>
    )
  }

  const isBusy = action !== null
  const canStart = activeProfile?.status === 'confirmed' && !run

  return (
    <main data-testid="reference-corpus-workspace" className="reference-materialization-surface min-w-0 flex-1 overflow-y-auto bg-background" aria-busy={isBusy}>
      <div className="mx-auto flex min-h-full w-full max-w-6xl flex-col px-4 py-5 sm:px-6 lg:px-8">
        <header className="flex flex-wrap items-start justify-between gap-3 border-b border-border pb-4">
          <div className="min-w-0">
            <div className="flex items-center gap-2 text-muted-foreground">
              <Workflow className="h-4 w-4" aria-hidden="true" />
              <span className="text-xs font-medium">素材库 / 材料化</span>
            </div>
            <h1 className="mt-1 truncate text-base font-semibold text-foreground">{selectedAnchor.title}</h1>
            <p className="mt-1 text-xs text-muted-foreground">先冻结章节边界，再逐章提取并向量化可用材料。</p>
          </div>
          <button
            type="button"
            onClick={() => { setError(null); void loadRun(selectedAnchor) }}
            disabled={isBusy}
            className="inline-flex h-8 w-8 items-center justify-center rounded-md border border-border text-muted-foreground hover:bg-secondary hover:text-foreground disabled:cursor-not-allowed disabled:opacity-50"
            aria-label="刷新材料化状态"
            title="刷新材料化状态"
          >
            <RefreshCcw className="h-3.5 w-3.5" aria-hidden="true" />
          </button>
        </header>

        {error && (
          <div className="mt-4 flex items-start gap-2 border border-destructive/30 bg-destructive/5 px-3 py-2.5 text-xs text-destructive" role="alert">
            <CircleAlert className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
            <span className="min-w-0 break-words">{error}</span>
          </div>
        )}

        <section className="border-b border-border py-4" aria-labelledby="split-heading">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="flex min-w-0 items-start gap-2">
              <ScanSearch className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
              <div>
                <h2 id="split-heading" className="text-sm font-semibold text-foreground">1. 章节切分</h2>
                <p className="mt-1 text-xs leading-5 text-muted-foreground">
                  {activeProfile
                    ? `${profileStateLabel(activeProfile)} · ${formatCount(activeProfile.chapter_count)} 个章节${activeProfile.sample_char_count ? ` · 已分析前 ${formatCount(activeProfile.sample_char_count)} 字符` : ''}`
                    : '自动分析只会发送前 50,000 个归一化字符；也可直接提供分隔模板。'}
                </p>
              </div>
            </div>
            {!activeProfile && (
              <button
                type="button"
                onClick={() => { void analyze() }}
                disabled={isBusy}
                className="inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {action === 'analyze' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <Sparkles className="h-3.5 w-3.5" aria-hidden="true" />}
                自动分析前 50K
              </button>
            )}
          </div>

          {!activeProfile && (
            <div className="mt-3 flex flex-col gap-2 sm:flex-row">
              <label className="min-w-0 flex-1">
                <span className="sr-only">章节分隔模板</span>
                <input
                  value={manualTemplate}
                  onChange={(event) => setManualTemplate(event.target.value)}
                  className="h-9 w-full rounded-md border border-border bg-background px-2.5 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                  placeholder="手动模板，例如：第{number}章 {title}"
                  aria-label="章节分隔模板"
                />
              </label>
              <button
                type="button"
                onClick={() => { void previewManual() }}
                disabled={isBusy || !manualTemplate.trim()}
                className="inline-flex h-9 items-center justify-center gap-1.5 rounded-md border border-border px-3 text-xs font-medium text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50"
              >
                {action === 'manual-preview' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <BookOpenCheck className="h-3.5 w-3.5" aria-hidden="true" />}
                预览模板
              </button>
            </div>
          )}

          {activeProfile && (
            <div className="mt-3 border border-border bg-muted/20">
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-3 py-2">
                <div className="min-w-0">
                  <p className="truncate text-xs font-medium text-foreground">{activeProfile.delimiter_template || '已冻结章节配置'}</p>
                  <p className="mt-0.5 text-[11px] text-muted-foreground">{activeProfile.split_mode === 'auto' ? '模型识别' : '手动模板'}{activeProfile.confidence != null ? ` · 置信度 ${Math.round(activeProfile.confidence * 100)}%` : ''}</p>
                </div>
                {profile?.status !== 'confirmed' && !run && (
                  <button
                    type="button"
                    onClick={() => { void confirmProfile() }}
                    disabled={isBusy}
                    className="inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {action === 'confirm' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />}
                    确认章节边界
                  </button>
                )}
              </div>
              {profile && profile.boundaries.length > 0 && (
                <ol className="divide-y divide-border" aria-label="章节切分预览">
                  {profile.boundaries.slice(0, 6).map((boundary) => (
                    <li key={boundary.chapter_index} className="flex items-center gap-3 px-3 py-2 text-xs">
                      <span className="w-7 text-right text-muted-foreground">{boundary.chapter_index}</span>
                      <span className="min-w-0 flex-1 truncate text-foreground">{boundary.title}</span>
                      <span className="shrink-0 text-[11px] text-muted-foreground">{formatCount(boundary.content_end - boundary.content_start)} 字符</span>
                    </li>
                  ))}
                </ol>
              )}
            </div>
          )}
        </section>

        <section className="border-b border-border py-4" aria-labelledby="run-heading">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="flex items-start gap-2">
              <ClipboardCheck className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" aria-hidden="true" />
              <div>
                <h2 id="run-heading" className="text-sm font-semibold text-foreground">2. 材料化进度</h2>
                <p className="mt-1 text-xs leading-5 text-muted-foreground">
                  {run
                    ? `状态：${run.status.replaceAll('_', ' ')} · ${formatCount(run.processed_chapters)} / ${formatCount(run.total_chapters)} 章节`
                    : '确认章节边界后，按固定批次依次处理；批内可以并行，批次之间保持顺序。'}
                </p>
              </div>
            </div>
            {canStart && (
              <button
                type="button"
                onClick={() => { void enqueue() }}
                disabled={isBusy}
                className="inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {action === 'enqueue' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <Play className="h-3.5 w-3.5" aria-hidden="true" />}
                启动材料化
              </button>
            )}
            {run?.status === 'failed' && (
              <button type="button" onClick={() => { void retry() }} disabled={isBusy} className="inline-flex h-8 items-center gap-1.5 rounded-md border border-destructive/35 px-3 text-xs font-medium text-destructive hover:bg-destructive/5 disabled:cursor-not-allowed disabled:opacity-50">
                {action === 'retry' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <RefreshCcw className="h-3.5 w-3.5" aria-hidden="true" />}
                修复后重试
              </button>
            )}
          </div>

          {!run && activeProfile?.status === 'confirmed' && (
            <div className="mt-3 flex flex-wrap items-center gap-2" role="group" aria-label="章节批次大小">
              <span className="text-xs text-muted-foreground">每批并行章节</span>
              {([5, 10] as const).map((size) => (
                <button key={size} type="button" onClick={() => setBatchSize(size)} disabled={isBusy} aria-pressed={batchSize === size} className={`h-8 min-w-10 rounded-md border px-2.5 text-xs font-medium transition-colors ${batchSize === size ? 'border-primary bg-primary text-primary-foreground' : 'border-border text-foreground hover:bg-secondary'}`}>
                  {size}
                </button>
              ))}
              <span className="text-[11px] text-muted-foreground">默认 5；选择 10 会以 10 章为一个并行批次。</span>
            </div>
          )}

          {run && (
            <>
              <dl className="mt-3 grid grid-cols-2 divide-x divide-y divide-border border border-border sm:grid-cols-4" aria-label="材料化漏斗">
                {[
                  ['材料', run.accepted_count],
                  ['向量', run.vector_count],
                  ['已处理章节', run.processed_chapters],
                  ['总章节', run.total_chapters],
                ].map(([label, value]) => (
                  <div key={String(label)} className="px-3 py-2.5">
                    <dt className="text-[11px] text-muted-foreground">{label}</dt>
                    <dd className="mt-1 text-sm font-semibold text-foreground">{formatCount(Number(value))}</dd>
                  </div>
                ))}
              </dl>
              <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-muted-foreground">
                <span className={runTone(run.status)}>运行 {run.status}</span>
                <span>批次 {formatCount(run.completed_chapter_batches)} / {formatCount(run.total_chapter_batches)} · 每批 {run.chapter_batch_size} 章</span>
                <span>{run.vector_index_healthy ? '向量索引完整' : '向量索引未就绪'}</span>
                <span>LLM {run.llm.provider}/{run.llm.model_id}</span>
                <span>向量 {run.embedding.provider}/{run.embedding.model_id}</span>
              </div>
              {run.last_error_message && (
                <p className="mt-3 flex items-start gap-1.5 border-l-2 border-destructive px-2 text-xs leading-5 text-destructive"><XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />{run.last_error_code ?? 'materialization_failed'}：{run.last_error_message}</p>
              )}
            </>
          )}
        </section>

        {run && (
          <section className="border-b border-border py-4" aria-labelledby="chapters-heading">
            <div className="flex items-center gap-2">
              <Workflow className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
              <h2 id="chapters-heading" className="text-sm font-semibold text-foreground">章节进度</h2>
            </div>
            {progress.length === 0 ? (
              <p className="mt-3 text-xs text-muted-foreground">尚未取得章节进度。</p>
            ) : (
              <ol className="mt-3 divide-y divide-border border-y border-border" aria-label="材料化章节进度">
                {progress.map((item) => (
                  <li key={item.chapter_index} className="grid grid-cols-[2.5rem_minmax(0,1fr)_auto] items-center gap-3 px-2 py-2 text-xs sm:px-3">
                    <span className="text-muted-foreground">{item.chapter_index}</span>
                    <span className="min-w-0">
                      <span className="block truncate text-foreground">{stageLabel(item.current_stage)}</span>
                      <span className="mt-0.5 block text-[11px] text-muted-foreground">材料 {formatCount(item.accepted_count)} · 向量 {formatCount(item.vector_count)}</span>
                    </span>
                    <span className={runTone(item.status)}>{item.status}</span>
                  </li>
                ))}
              </ol>
            )}
          </section>
        )}

        {run?.status === 'completed' && (
          <section className="py-4" aria-labelledby="materials-heading">
            <div className="flex items-center gap-2">
              <ClipboardCheck className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
              <div>
                <h2 id="materials-heading" className="text-sm font-semibold text-foreground">当前材料</h2>
                <p className="mt-1 text-xs text-muted-foreground">共 {formatCount(materialTotal)} 条，按章节原文顺序浏览。</p>
              </div>
            </div>
            <ol className="mt-3 divide-y divide-border border-y border-border" aria-label="当前材料">
              {materials.map((material) => (
                <li key={material.material_id} className="px-3 py-3">
                  <p className="text-[11px] text-muted-foreground">第 {material.chapter_index} 章 · {material.material_type.replaceAll('_', ' ')}</p>
                  <p className="mt-2 whitespace-pre-wrap break-words text-xs leading-5 text-foreground">{material.text}</p>
                  {material.description && <p className="mt-2 text-[11px] leading-4 text-muted-foreground">{material.description}</p>}
                  {material.tags.length > 0 && (
                    <div className="mt-2 flex flex-wrap gap-1">
                      {material.tags.slice(0, 5).map((tag) => <span key={tag} className="border border-border bg-muted/35 px-1.5 py-0.5 text-[11px] text-muted-foreground">{tag.replaceAll('_', ' ')}</span>)}
                    </div>
                  )}
                </li>
              ))}
            </ol>
            {materialTotalPages > 1 && (
              <nav className="mt-3 flex items-center justify-end gap-2" aria-label="材料分页">
                <button type="button" onClick={() => setMaterialPage((page) => Math.max(1, page - 1))} disabled={materialPage <= 1 || isBusy} className="h-8 rounded-md border border-border px-2.5 text-xs text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50">上一页</button>
                <span className="text-[11px] text-muted-foreground">{materialPage} / {materialTotalPages}</span>
                <button type="button" onClick={() => setMaterialPage((page) => Math.min(materialTotalPages, page + 1))} disabled={materialPage >= materialTotalPages || isBusy} className="h-8 rounded-md border border-border px-2.5 text-xs text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50">下一页</button>
              </nav>
            )}
          </section>
        )}
      </div>
    </main>
  )
}
