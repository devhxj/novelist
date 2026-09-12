import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertTriangle,
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
import SettingsDialog from '@/components/settings/SettingsDialog'
import { BridgeError } from '@/lib/novelist/bridge'
import { bridgeErrorGuide, describeBridgeError } from '@/lib/novelist/bridgeErrors'
import { FEATURE_VALUE_LABELS, RUN_STATUS_LABELS, taxonomyLabel } from '@/lib/novelist/corpusTaxonomy'
import { describeAnchorStatus } from '@/lib/novelist/referenceAnchorStates'
import ReferenceErrorStrip from './ReferenceErrorStrip'
import type { reference } from '@/lib/novelist/types'

type Props = {
  novelId: number
  refreshKey: number
  anchors: reference.Anchor[]
  selectedAnchorIds: number[]
  /** 当前要制作的参考书；为 null 或已取消勾选时回落到第一本选中的书。 */
  activeAnchorId: number | null
  onActiveAnchorChange: (anchorId: number) => void
  onMaterializationChange: () => void
}

type Action = 'analyze' | 'manual-preview' | 'confirm' | 'enqueue' | 'retry' | 'review' | null

const numberFormatter = new Intl.NumberFormat('zh-CN')

const PROGRESS_PAGE_SIZE = 30
const CANDIDATE_PAGE_SIZE = 12

// 心跳阈值：只看总耗时无法分辨"慢"与"卡住"，因此单独记录"最近一次产出变化的时刻"。
// 超过 SLOW 视为"慢但在跑"，超过 STUCK 视为"疑似卡住"并给出可操作建议。
const SLOW_AFTER_MS = 90_000
const STUCK_AFTER_MS = 600_000

// 切分分析结果按锚点缓存于组件外：LLM 分析期间切换页面回来不丢结果。
const analyzedProfiles = new Map<number, reference.ChapterSplitProfile>()

function formatCount(value: number): string {
  return numberFormatter.format(Number.isFinite(value) ? Math.max(0, value) : 0)
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
    building_candidates: '抽取候选',
    llm_qualifying: '大模型准入',
    embedding: '生成向量',
    indexing: '建立索引',
    completed: '完成',
    failed: '失败',
    cancelled: '已取消',
  }
  return labels[stage] ?? stage.replaceAll('_', ' ')
}

// 阶段回答"做到哪一步"，状态回答"是否完成/卡住"——两者分开，作者才不用读原始枚举。
function chapterStateLabel(status: string): string {
  if (status === 'completed') return '已完成'
  if (status === 'failed') return '失败'
  if (status === 'cancelled') return '已取消'
  if (status === 'pending') return '等待中'
  return '进行中'
}

function chapterStateTone(status: string): string {
  if (status === 'completed') return 'text-emerald-700 dark:text-emerald-300'
  if (status === 'failed') return 'text-destructive'
  if (status === 'cancelled' || status === 'pending') return 'text-muted-foreground'
  return 'text-sky-700 dark:text-sky-300'
}

/** 大模型准入按轮次分批抽完整章的六类素材：把"第 N/M 趟"翻译成作者可读的轮次。 */
function extractionRoundText(item: reference.MaterializationChapterProgress): string {
  if (!item.extraction_round_count || item.current_stage !== 'llm_qualifying') return ''
  const round = Math.max(1, Math.min(item.extraction_round_index ?? 1, item.extraction_round_count))
  return `第 ${round}/${item.extraction_round_count} 轮抽取`
}

/**
 * 章节失败原因：后端落库的是错误码 + 原始异常消息（常是英文 SDK 文案），
 * 这里用错误码映射成作者可读的一句话；未命中映射才回退原文。
 */
function chapterFailureText(item: reference.MaterializationChapterProgress): string {
  if (!item.last_error_code) return item.last_error_message ?? ''
  return bridgeErrorGuide[item.last_error_code]?.message ?? item.last_error_message ?? ''
}

function candidateTags(tags: reference.MaterializationMaterialTags): string[] {
  return [
    ...tags.narrative_functions,
    ...tags.emotion_mechanics,
    ...tags.techniques,
    ...tags.scene_beat_roles,
  ].filter(Boolean).slice(0, 5)
}

function parseTimestampMs(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string') {
    const parsed = Date.parse(value)
    return Number.isNaN(parsed) ? null : parsed
  }
  return null
}

/** 分钟 → 「N 分钟」/「N 小时 M 分钟」。 */
function formatMinutes(minutes: number): string {
  if (minutes < 60) return `${minutes} 分钟`
  return `${Math.floor(minutes / 60)} 小时 ${minutes % 60} 分钟`
}

/** 毫秒 → 人话时长，用于"停滞多久"与"还要多久"。 */
function formatElapsedMs(ms: number): string {
  const minutes = Math.floor(ms / 60_000)
  if (minutes < 1) return '不到 1 分钟'
  return formatMinutes(minutes)
}

/** nowMs 由调用方传入：渲染期间不得读 Date.now()。 */
function runDurationText(run: reference.MaterializationStatus, nowMs: number): string {
  const startedMs = parseTimestampMs(run.started_at)
  if (startedMs === null) return ''
  const endedMs = run.completed_at ? parseTimestampMs(run.completed_at) ?? nowMs : nowMs
  if (endedMs <= startedMs) return ''
  const minutes = Math.floor((endedMs - startedMs) / 60_000)
  if (minutes < 1) return '不足 1 分钟'
  return formatMinutes(minutes)
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
  activeAnchorId,
  onActiveAnchorChange,
  onMaterializationChange,
}: Props) {
  const app = useApp()
  const [profile, setProfile] = useState<reference.ChapterSplitProfile | null>(null)
  const [run, setRun] = useState<reference.MaterializationStatus | null>(null)
  const [progress, setProgress] = useState<reference.MaterializationChapterProgress[]>([])
  const [candidates, setCandidates] = useState<reference.MaterializationCandidate[]>([])
  const [manualTemplate, setManualTemplate] = useState('')
  const [action, setAction] = useState<Action>(null)
  const [error, setError] = useState<{ message: string; detail: string | null } | null>(null)
  const showError = useCallback((err: unknown, fallback: string) => {
    const diagnostic = describeBridgeError(err, fallback)
    setError({ message: diagnostic.message, detail: diagnostic.detail })
  }, [])
  const [errorRetry, setErrorRetry] = useState<(() => void) | null>(null)
  const [sampleLimit, setSampleLimit] = useState<number | null>(null)
  const [statusTick, setStatusTick] = useState(0)
  const [candidateTotal, setCandidateTotal] = useState(0)
  const [candidatePage, setCandidatePage] = useState(1)
  const [loadingMore, setLoadingMore] = useState(false)
  const [progressPage, setProgressPage] = useState(1)
  const [progressTotal, setProgressTotal] = useState(0)
  const [loadingMoreProgress, setLoadingMoreProgress] = useState(false)
  const [failedProgressOnly, setFailedProgressOnly] = useState(false)
  const [candidateRefreshTick, setCandidateRefreshTick] = useState(0)
  const [showModelSettings, setShowModelSettings] = useState(false)
  const requestIdRef = useRef(0)
  const notifiedCompletedRunRef = useRef<string | null>(null)
  const loadedRunIdRef = useRef<string | null>(null)

  const usableAnchors = useMemo(
    () => anchors.filter((anchor) => describeAnchorStatus(anchor.status).usable),
    [anchors],
  )

  // 一本书一条流水线，所以这里只认一本：优先取"当前制作的书"，
  // 它被取消勾选后回落到第一本选中的书，避免制作页停在一本已经没被选中的书上。
  const selectedAnchor = useMemo(() => {
    const selected = new Set(selectedAnchorIds)
    const active = activeAnchorId != null && selected.has(activeAnchorId)
      ? anchors.find((anchor) => anchor.anchor_id === activeAnchorId) ?? null
      : null
    return active ?? anchors.find((anchor) => selected.has(anchor.anchor_id)) ?? null
  }, [anchors, selectedAnchorIds, activeAnchorId])

  // 消解"黑盒"：把"正在处理哪一章、走到哪一步"提炼成一句人话，
  // 作者不必逐行读取进度表、也不依赖原始英文枚举才能判断系统是否在推进。
  const activeChapter = useMemo(() => {
    if (!run || (run.status !== 'running' && run.status !== 'queued')) return null
    const inFlight = progress.find((item) => chapterStateLabel(item.status) === '进行中')
    const chapterIndex = inFlight?.chapter_index ?? run.current_batch_start_chapter ?? null
    if (chapterIndex == null) return null
    return { chapterIndex, item: inFlight ?? null }
  }, [run, progress])

  const isActiveRun = !!run && (run.status === 'queued' || run.status === 'running')

  // 时间基准放在 state 里由定时器推进：渲染期间读 Date.now() 是副作用，
  // 会让同一份数据在两次渲染间漂移（React 纯净渲染规则）。
  const [nowMs, setNowMs] = useState(() => Date.now())
  useEffect(() => {
    if (!isActiveRun) return
    const timer = window.setInterval(() => { setNowMs(Date.now()) }, 3_000)
    return () => window.clearInterval(timer)
  }, [isActiveRun])

  // 心跳：记录"最近一次观察到产出变化"的时刻。活跃 run 每 3s 轮询一次，
  // 因此这里的时间差可以实时反映"数字还动不动"，作者据此判断是慢还是卡住。
  // 心跳只随 run 的写入一起更新（事件/异步回调里改状态），不在 effect 中同步 setState。
  const heartbeatRef = useRef<{ signature: string; at: number } | null>(null)
  const [heartbeatAt, setHeartbeatAt] = useState<number | null>(null)
  const applyRun = useCallback((status: reference.MaterializationStatus | null) => {
    setRun(status)
    if (!status || (status.status !== 'queued' && status.status !== 'running')) {
      heartbeatRef.current = null
      setHeartbeatAt(null)
      return
    }
    const signature = [
      status.processed_chapters,
      status.model_call_count,
      status.candidate_count,
      status.accepted_count,
      status.vector_count,
    ].join('|')
    if (heartbeatRef.current?.signature !== signature) {
      heartbeatRef.current = { signature, at: Date.now() }
      setHeartbeatAt(heartbeatRef.current.at)
    }
  }, [])

  const stalledMs = heartbeatAt === null ? 0 : Math.max(0, nowMs - heartbeatAt)
  const progressPercent = run && run.total_chapters > 0
    ? Math.min(100, Math.round((Math.min(run.processed_chapters, run.total_chapters) / run.total_chapters) * 100))
    : 0

  // 剩余时间按"已处理章节的平均耗时"线性外推：粗糙但足以回答"还要等多久"。
  // 首章未完成前没有基准，不做估算，避免给出无依据的数字。
  const etaText = (() => {
    if (!run || !isActiveRun) return ''
    const remaining = run.total_chapters - run.processed_chapters
    if (remaining <= 0 || run.processed_chapters <= 0) return ''
    const startedMs = parseTimestampMs(run.started_at)
    if (startedMs === null || nowMs <= startedMs) return ''
    return `预计还需约 ${formatElapsedMs(((nowMs - startedMs) / run.processed_chapters) * remaining)}`
  })()

  const stallLevel: 'fresh' | 'slow' | 'stuck' =
    stalledMs >= STUCK_AFTER_MS ? 'stuck' : stalledMs >= SLOW_AFTER_MS ? 'slow' : 'fresh'
  const heartbeatText = (() => {
    if (!isActiveRun) return ''
    const chapterClause = activeChapter ? `第 ${formatCount(activeChapter.chapterIndex)} 章` : ''
    const roundText = activeChapter?.item ? extractionRoundText(activeChapter.item) : ''
    const stageClause = activeChapter?.item
      ? `${stageLabel(activeChapter.item.current_stage)}${roundText ? `（${roundText}）` : ''}`
      : ''
    if (stallLevel === 'stuck') {
      return `已 ${formatElapsedMs(stalledMs)}没有新产出，${chapterClause || '当前章节'}仍未完成 —— 可能已卡住，可点「刷新材料化状态」确认；若持续无进展请检查模型服务与配置。`
    }
    // 慢/卡住时不再外推剩余时间——此时"按已处理速度估算"已经不成立，给出反而误导。
    if (stallLevel === 'slow') {
      return `已 ${formatElapsedMs(stalledMs)}没有新产出${chapterClause ? `，${chapterClause}仍在${stageClause || '处理中'}` : ''}，比预期慢，暂不估算剩余时间`
    }
    return `正在${chapterClause ? `处理${chapterClause}` : '按章推进'}${stageClause ? ` · ${stageClause}` : ''}${etaText ? ` · ${etaText}` : ''}`
  })()

  // 失败说明与"从哪里继续"：后端恢复点永远是当前批次（逐章模式下即失败的那一章），
  // 恢复后按顺序继续后续章节，已完成的不重跑。前端把这件事讲清楚，作者不必猜。
  const failureGuide = run?.last_error_code ? bridgeErrorGuide[run.last_error_code] : undefined
  const failedProgressItem = progress.find((item) => item.status === 'failed') ?? null
  const resumeFromChapter = run?.current_batch_start_chapter ?? failedProgressItem?.chapter_index ?? null
  const resumeScopeText = run && resumeFromChapter && run.total_chapters > resumeFromChapter - 1
    ? `只重跑未完成的章节（第 ${resumeFromChapter}–${run.total_chapters} 章），已完成的 ${run.processed_chapters} 章不会重复处理。`
    : '只重跑未完成的章节，已完成的章节不会重复处理。'

  const loadRun = useCallback(async (anchor: reference.Anchor | null) => {
    const requestId = requestIdRef.current + 1
    requestIdRef.current = requestId
    if (!anchor || !novelId) {
      applyRun(null)
      setProgress([])
      setCandidates([])
      return
    }

    try {
      const status = await app.GetReferenceMaterializationStatus({
        novel_id: novelId,
        anchor_id: anchor.anchor_id,
      })
      if (requestId !== requestIdRef.current) return
      applyRun(status)
    } catch (err) {
      if (requestId === requestIdRef.current) {
          showError(err, '无法读取该来源的材料化状态。')
        setErrorRetry(() => () => { setStatusTick(t => t + 1) })
      }
    }
  }, [showError, app, novelId, applyRun])

  const loadRunDetail = useCallback(async (status: reference.MaterializationStatus | null, options: { resetPages: boolean } = { resetPages: true }) => {
    if (!status || !novelId) {
      loadedRunIdRef.current = null
      setProgressPage(1)
      setProgress([])
      setProgressTotal(0)
      setCandidatePage(1)
      setCandidates([])
      setCandidateTotal(0)
      return
    }

    // 锚点或 run 切换：所有分页状态归位，候选/进度都从第一页重来。
    const pagesToLoad = options.resetPages ? 1 : Math.max(1, progressPage)
    if (options.resetPages) {
      loadedRunIdRef.current = status.run_id
      setProgressPage(1)
      setProgress([])
      setProgressTotal(0)
      setCandidatePage(1)
      setCandidates([])
      setCandidateTotal(0)
    }

    try {
      // 非重置刷新（轮询）按已加载页数整段重拉：运行中的 run 进度会增长，
      // 但不能因此把作者已经翻到的章节进度截回第一页。
      const progressRequests = Array.from({ length: pagesToLoad }, (_, index) => (
        app.ListReferenceMaterializationChapterProgress({
          novel_id: novelId,
          anchor_id: status.anchor_id,
          run_id: status.run_id,
          page: index + 1,
          size: PROGRESS_PAGE_SIZE,
        })
      ))
      // run 切换后的首次加载同时取候选第一页；轮询刷新不动候选列表（O12）。
      const candidateRequest = options.resetPages
        ? app.ListReferenceMaterializationCandidates({
            novel_id: novelId,
            anchor_id: status.anchor_id,
            run_id: status.run_id,
            decision: 'review_required',
            page: 1,
            size: CANDIDATE_PAGE_SIZE,
          })
        : null
      const [progressResults, candidateResult] = await Promise.all([
        Promise.all(progressRequests),
        candidateRequest,
      ])
      const mergedProgress: reference.MaterializationChapterProgress[] = []
      const seenChapters = new Set<number>()
      for (const result of progressResults) {
        for (const item of result.items ?? []) {
          if (seenChapters.has(item.chapter_index)) continue
          seenChapters.add(item.chapter_index)
          mergedProgress.push(item)
        }
      }
      setProgress(mergedProgress)
      setProgressTotal(progressResults.at(-1)?.total ?? mergedProgress.length)
      if (candidateResult) {
        setCandidates(candidateResult.items ?? [])
        setCandidateTotal(candidateResult.total)
      }
    } catch (err) {
      showError(err, '材料化进度加载失败。')
      setErrorRetry(() => () => { setStatusTick(t => t + 1) })
    }
  }, [showError, app, novelId, progressPage])

  // 复核动作会改变候选队列，但不换 run：只需按已加载页数重拉候选，保留作者所在位置。
  const refreshCandidatesForCurrentPages = useCallback(async (status: reference.MaterializationStatus, pages: number) => {
    try {
      const requests = Array.from({ length: pages }, (_, index) => (
        app.ListReferenceMaterializationCandidates({
          novel_id: novelId,
          anchor_id: status.anchor_id,
          run_id: status.run_id,
          decision: 'review_required',
          page: index + 1,
          size: CANDIDATE_PAGE_SIZE,
        })
      ))
      const results = await Promise.all(requests)
      const merged: reference.MaterializationCandidate[] = []
      const seen = new Set<string>()
      for (const result of results) {
        for (const item of result.items ?? []) {
          if (seen.has(item.candidate_id)) continue
          seen.add(item.candidate_id)
          merged.push(item)
        }
      }
      setCandidates(merged)
      setCandidateTotal(results.at(-1)?.total ?? merged.length)
    } catch (err) {
      showError(err, '候选复核列表加载失败。')
      setErrorRetry(() => () => { setCandidateRefreshTick(t => t + 1) })
    }
  }, [showError, app, novelId])

  // 候选队列分页：固定页大小续拉下一页，追加到现有列表。
  const loadMoreCandidates = useCallback(async () => {
    if (!run || !novelId || loadingMore) return
    setLoadingMore(true)
    try {
      const next = await app.ListReferenceMaterializationCandidates({
        novel_id: novelId,
        anchor_id: run.anchor_id,
        run_id: run.run_id,
        decision: 'review_required',
        page: candidatePage + 1,
        size: CANDIDATE_PAGE_SIZE,
      })
      setCandidates((current) => {
        const seen = new Set(current.map((item) => item.candidate_id))
        return [...current, ...next.items.filter((item) => !seen.has(item.candidate_id))]
      })
      setCandidateTotal(next.total)
      setCandidatePage((current) => current + 1)
    } catch (err) {
      showError(err, '候选复核列表加载失败。')
      setErrorRetry(() => () => { void loadMoreCandidates() })
    } finally {
      setLoadingMore(false)
    }
  }, [showError, app, novelId, run, loadingMore, candidatePage])

  // 章节进度分页（O13）：同样追加式续拉，配合"仅看失败"筛选定位卡住的章节。
  const loadMoreProgress = useCallback(async () => {
    if (!run || !novelId || loadingMoreProgress) return
    setLoadingMoreProgress(true)
    try {
      const next = await app.ListReferenceMaterializationChapterProgress({
        novel_id: novelId,
        anchor_id: run.anchor_id,
        run_id: run.run_id,
        page: progressPage + 1,
        size: PROGRESS_PAGE_SIZE,
      })
      setProgress((current) => {
        const seen = new Set(current.map((item) => item.chapter_index))
        return [...current, ...next.items.filter((item) => !seen.has(item.chapter_index))]
      })
      setProgressTotal(next.total)
      setProgressPage((current) => current + 1)
    } catch (err) {
      showError(err, '章节进度加载失败。')
      setErrorRetry(() => () => { void loadMoreProgress() })
    } finally {
      setLoadingMoreProgress(false)
    }
  }, [showError, app, novelId, run, loadingMoreProgress, progressPage])

  useEffect(() => {
    const timer = window.setTimeout(() => {
      setError(null)
      setProfile(selectedAnchor ? analyzedProfiles.get(selectedAnchor.anchor_id) ?? null : null)
      setManualTemplate('')
      setFailedProgressOnly(false)
      void loadRun(selectedAnchor)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [loadRun, refreshKey, selectedAnchor, statusTick])

  // run 明细加载（O12）：
  // - 锚点或 run 切换（run_id 变化）→ 全部分页归位，候选与进度从第一页重来；
  // - 轮询刷新（3s 定时，同一 run_id）→ 只重拉章节进度，候选列表与作者所在页不受打扰。
  useEffect(() => {
    const timer = window.setTimeout(() => {
      if (!run || !novelId) {
        void loadRunDetail(null)
        return
      }
      const isNewRun = loadedRunIdRef.current !== run.run_id
      void loadRunDetail(run, { resetPages: isNewRun })
    }, 0)
    return () => window.clearTimeout(timer)
  }, [loadRunDetail, run, novelId])

  // 复核动作（candidateRefreshTick）后的候选刷新：按已加载页数整段重拉，位置不动。
  useEffect(() => {
    if (!candidateRefreshTick || !run || !novelId) return
    const timer = window.setTimeout(() => {
      void refreshCandidatesForCurrentPages(run, candidatePage)
    }, 0)
    return () => window.clearTimeout(timer)
  }, [candidateRefreshTick, run, novelId, candidatePage, refreshCandidatesForCurrentPages])

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
      analyzedProfiles.set(selectedAnchor.anchor_id, nextProfile)
      setProfile(nextProfile)
    } catch (error) {
      setError({ message: chapterSplitErrorMessage(error), detail: error instanceof BridgeError ? error.message : null })
      setErrorRetry(() => () => { void analyze() })
    } finally {
      setAction(null)
    }
  }

  // 重新分析：把缓存里留着的上次结果先清掉再跑一遍。
  // 成功后新 profile 以"待确认"落位，旧确认配置不受影响，作者确认后才会换用。
  const reanalyze = async () => {
    if (!selectedAnchor) return
    analyzedProfiles.delete(selectedAnchor.anchor_id)
    setProfile(null)
    await analyze()
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
      analyzedProfiles.set(selectedAnchor.anchor_id, nextProfile)
      setProfile(nextProfile)
    } catch (err) {
      showError(err, '章节分隔模板无法应用到整本来源。')
      setErrorRetry(() => () => { void previewManual() })
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
      // 缓存同步到已确认的切分：否则材料化完成触发 refreshKey 重挂时，
      // 会从缓存里读回"待确认"的旧分析结果，把重新材料化的入口整个锁死。
      analyzedProfiles.set(selectedAnchor.anchor_id, confirmed)
      setProfile(confirmed)
    } catch (err) {
      showError(err, '章节边界确认失败。来源可能已变化。')
      setErrorRetry(() => () => { void confirmProfile() })
    } finally {
      setAction(null)
    }
  }

  const enqueue = async () => {
    if (!selectedAnchor || !activeProfile || activeProfile.status !== 'confirmed') return
    setAction('enqueue')
    setError(null)
    try {
      const status = await app.EnqueueReferenceMaterialization({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
        split_profile_id: activeProfile.split_profile_id,
      })
      applyRun(status)
    } catch (err) {
      showError(err, '材料化未能启动。大模型、向量模型和索引均必须可用。')
      setErrorRetry(() => () => { void enqueue() })
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
      applyRun(status)
    } catch (err) {
      // 模型或资格模式在 run 启动后发生过变化时，旧 run 无法续跑：
      // 转为用同一份已冻结章节切分新建 run，而不是把死路留给作者。
      if (err instanceof BridgeError && err.code === 'materialization_retry_requires_new_run') {
        try {
          const status = await app.EnqueueReferenceMaterialization({
            novel_id: novelId,
            anchor_id: selectedAnchor.anchor_id,
            split_profile_id: run.split_profile_id,
          })
          applyRun(status)
        } catch (enqueueErr) {
          showError(enqueueErr, '旧 run 已无法续跑，新建材料化 run 也未能启动。请检查模型与索引后重试。')
          setErrorRetry(() => () => { void enqueue() })
        }
      } else {
        showError(err, '材料化重试未能启动。请先修复模型或索引问题。')
        setErrorRetry(() => () => { void retry() })
      }
    } finally {
      setAction(null)
    }
  }

  const reviewCandidate = async (candidate: reference.MaterializationCandidate, nextAction: 'confirm' | 'reject') => {
    if (!run || !selectedAnchor) return
    setAction('review')
    setError(null)
    try {
      const result = await app.ReviewReferenceMaterializationCandidate({
        novel_id: novelId,
        anchor_id: selectedAnchor.anchor_id,
        run_id: run.run_id,
        candidate_id: candidate.candidate_id,
        action: nextAction,
        expected_version: candidate.row_version,
      })
      applyRun(result.status)
      // 复核改变了候选队列：触发按当前页数重拉，不重置作者所在页。
      setCandidateRefreshTick((t) => t + 1)
    } catch (err) {
      showError(err, '候选复核未保存。列表已变更时请刷新后再次提交。')
      setErrorRetry(() => () => { void reviewCandidate(candidate, nextAction) })
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
      <main data-testid="reference-corpus-workspace" className="min-w-0 flex-1 overflow-y-auto bg-background">
        <div className="mx-auto flex min-h-full max-w-5xl flex-col items-center justify-center px-6 text-center">
          <FileStack className="h-8 w-8 text-muted-foreground/55" aria-hidden="true" />
          <h1 className="mt-3 text-base font-semibold text-foreground">选择一个参考来源</h1>
          <p className="mt-1 max-w-md text-xs leading-5 text-muted-foreground">从左侧选中已导入书籍后，在这里确认章节边界并启动材料化。</p>
        </div>
      </main>
    )
  }

  const isBusy = action !== null
  // 只有进行中的 run（排队/运行）挡住入口；完成、失败、取消都允许再次启动。
  const hasActiveRun = run?.status === 'queued' || run?.status === 'running'
  const canStart = activeProfile?.status === 'confirmed' && !hasActiveRun

  return (
    <main data-testid="reference-corpus-workspace" className="min-w-0 flex-1 overflow-y-auto bg-background" aria-busy={isBusy}>
      <div className="mx-auto flex min-h-full w-full max-w-6xl flex-col px-4 py-5 sm:px-6 lg:px-8">
        <header className="flex flex-wrap items-start justify-between gap-3 border-b border-border pb-4">
          <div className="min-w-0">
            <div className="flex items-center gap-2 text-muted-foreground">
              <Workflow className="h-4 w-4" aria-hidden="true" />
              <span className="text-xs font-medium">素材库 / 材料化</span>
            </div>
            <h1 className="mt-1 truncate text-base font-semibold text-foreground">{selectedAnchor.title}</h1>
            <p className="mt-1 text-xs text-muted-foreground">先冻结章节边界，再以大模型准入和向量索引生成可用材料。</p>
          </div>
          <div className="flex shrink-0 items-center gap-2">
            {/* 制作页自己也能换书：否则多选时只能靠左侧列表猜"现在做的是哪一本"。 */}
            {usableAnchors.length > 1 && (
              <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
                参考书
                <select
                  value={selectedAnchor.anchor_id}
                  onChange={(event) => { onActiveAnchorChange(Number(event.target.value)) }}
                  disabled={isBusy}
                  data-testid="reference-corpus-anchor-select"
                  className="h-8 max-w-[13rem] rounded-md border border-border bg-background px-2 text-xs text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  aria-label="选择要制作的参考书"
                  title="切换要制作材料的参考书"
                >
                  {usableAnchors.map((anchor) => (
                    <option key={anchor.anchor_id} value={anchor.anchor_id}>{anchor.title}</option>
                  ))}
                </select>
              </label>
            )}
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
          </div>
        </header>

        {error && (
          <div className="mt-4 flex flex-col gap-1.5">
            <ReferenceErrorStrip
              message={error.message}
              detail={error.detail}
              onRetry={errorRetry ? () => { setError(null); setErrorRetry(null); errorRetry() } : undefined}
              onClose={() => { setError(null); setErrorRetry(null) }}
            />
            <div>
              <button
                type="button"
                onClick={() => { setShowModelSettings(true) }}
                className="rounded border border-destructive/40 px-2 py-0.5 text-[11px] font-medium text-destructive hover:bg-destructive/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                data-testid="open-model-settings"
              >
                去配置模型
              </button>
            </div>
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
            {activeProfile && (
              <button
                type="button"
                onClick={() => { void reanalyze() }}
                disabled={isBusy}
                data-testid="reanalyze-split-button"
                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-border px-3 text-xs font-medium text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50"
              >
                {action === 'analyze' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <Sparkles className="h-3.5 w-3.5" aria-hidden="true" />}
                重新分析
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
                  <p className="truncate text-xs font-medium text-foreground">
                    {activeProfile.delimiter_template
                      ? <span>分隔模板：<code className="rounded bg-muted px-1 py-0.5 font-mono">{activeProfile.delimiter_template}</code><span className="ml-1 font-normal text-muted-foreground">（{'{number}'} 表示章号，{'{title}'} 表示标题）</span></span>
                      : '已冻结章节配置'}
                  </p>
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
                  {run ? (
                    <>
                      状态：<span className={runTone(run.status)}>{taxonomyLabel(RUN_STATUS_LABELS, run.status)}</span>
                      {` · ${formatCount(run.processed_chapters)} / ${formatCount(run.total_chapters)} 章节`}
                    </>
                  ) : (
                    '确认章节边界后逐章处理：一次一章、依次推进；失败只影响所在章节，修复后可续跑。'
                  )}
                </p>
              </div>
            </div>
            {canStart && !run && (
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
            {canStart && run && (
              <button
                type="button"
                onClick={() => { void enqueue() }}
                disabled={isBusy}
                data-testid="rematerialize-button"
                className="inline-flex h-8 items-center gap-1.5 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {action === 'enqueue' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <Play className="h-3.5 w-3.5" aria-hidden="true" />}
                重新材料化
              </button>
            )}
            {run?.status === 'failed' && (
              <button
                type="button"
                onClick={() => { void retry() }}
                disabled={isBusy}
                data-testid="resume-materialization-button"
                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-destructive/35 px-3 text-xs font-medium text-destructive hover:bg-destructive/5 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {action === 'retry' ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : <RefreshCcw className="h-3.5 w-3.5" aria-hidden="true" />}
                {resumeFromChapter ? `从第 ${formatCount(resumeFromChapter)} 章继续` : '继续未完成的章节'}
              </button>
            )}
          </div>

          {run && (
            <>
              <p className="mt-3 text-[11px] leading-5 text-muted-foreground" data-testid="materialization-flow-hint">
                材料化按四步推进、数字逐级收敛：从正文抽取候选素材 → 大模型判定哪些可直接采用 → 需要判定的交给你复核 → 通过后生成向量入库，写作时才能被检索引用。
              </p>
              <dl className="mt-2 grid grid-cols-2 divide-x divide-y divide-border border border-border sm:grid-cols-4" aria-label="材料化漏斗">
                {([
                  ['抽取候选', run.candidate_count, '大模型从正文找到的素材线索'],
                  ['通过准入', run.accepted_count, 'AI 判定可直接采用的'],
                  ['待你复核', run.review_count, '需要你确认才能入库的'],
                  ['已入库', run.vector_count, '已生成向量、可被检索引用'],
                ] as const).map(([label, value, hint]) => (
                  <div key={label} className="px-3 py-2.5">
                    <dt className="text-[11px] font-medium text-foreground">{label}</dt>
                    <dd className="mt-1 text-sm font-semibold tabular-nums text-foreground">
                      {formatCount(value)}
                      <span className="mt-0.5 block text-[10px] font-normal leading-4 text-muted-foreground">{hint}</span>
                    </dd>
                  </div>
                ))}
              </dl>
              <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-muted-foreground">
                <span>{isActiveRun ? '已运行' : '耗时'} {runDurationText(run, nowMs) || '不足 1 分钟'}</span>
                <span>模型调用 {formatCount(run.model_call_count)} 次</span>
                <span>{run.vector_index_healthy ? '向量索引完整，材料可在写作时被检索引用' : '向量索引未就绪，材料化完成后会自动建立'}</span>
              </div>
              <details className="mt-2 text-[11px] text-muted-foreground">
                <summary className="w-fit cursor-pointer select-none rounded px-1 py-0.5 hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">模型与索引详情</summary>
                <div className="mt-1 flex flex-wrap gap-x-4 gap-y-1 pl-3">
                  <span>准入模型：{run.llm.provider}/{run.llm.model_id}</span>
                  <span>向量模型：{run.embedding.provider}/{run.embedding.model_id}</span>
                </div>
              </details>
              {run.last_error_message && (
                <div className="mt-3 rounded-md border border-destructive/30 bg-destructive/5 px-3 py-2.5" data-testid="materialization-failure" role="alert">
                  <p className="flex items-start gap-1.5 text-xs font-medium leading-5 text-destructive">
                    <XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                    <span>{failureGuide?.message ?? '材料化在完成前中断了。'}</span>
                  </p>
                  {failureGuide && <p className="mt-1 pl-5 text-[11px] leading-4 text-destructive/90">下一步：{failureGuide.action}。</p>}
                  {run.status === 'failed' && (
                    <p className="mt-1 pl-5 text-[11px] leading-4 text-muted-foreground" data-testid="materialization-resume-scope">
                      点「{resumeFromChapter ? `从第 ${formatCount(resumeFromChapter)} 章继续` : '继续未完成的章节'}」{resumeScopeText}
                    </p>
                  )}
                  <details className="mt-1.5 pl-5 text-[11px] text-destructive/80">
                    <summary className="w-fit cursor-pointer select-none">技术详情（错误码与原始消息）</summary>
                    <pre className="mt-1 whitespace-pre-wrap break-words font-mono">{run.last_error_code ?? 'materialization_failed'}：{run.last_error_message}</pre>
                  </details>
                </div>
              )}
            </>
          )}
        </section>

        {run && (
          <section className="border-b border-border py-4" aria-labelledby="chapters-heading">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <div className="flex items-center gap-2">
                <Workflow className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
                <h2 id="chapters-heading" className="text-sm font-semibold text-foreground">章节进度</h2>
              </div>
              <button
                type="button"
                onClick={() => { setFailedProgressOnly((only) => !only) }}
                aria-pressed={failedProgressOnly}
                data-testid="progress-failed-filter"
                className="inline-flex h-7 items-center gap-1 rounded-md border border-border px-2.5 text-[11px] font-medium text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50"
                disabled={progress.length === 0}
              >
                仅看失败章节
              </button>
            </div>
            <div className="mt-3 rounded-md border border-border bg-muted/20 px-3 py-2.5" data-testid="chapter-progress-overview">
              <div className="flex items-center justify-between gap-3 text-[11px]">
                <span className="text-muted-foreground">
                  已完成 {formatCount(Math.min(run.processed_chapters, run.total_chapters))} / {formatCount(run.total_chapters)} 章
                </span>
                <span className="tabular-nums text-foreground">{progressPercent}%</span>
              </div>
              <div
                className="mt-1.5 h-1.5 w-full overflow-hidden rounded-full bg-muted"
                role="progressbar"
                aria-label="章节材料化整体进度"
                aria-valuemin={0}
                aria-valuemax={run.total_chapters}
                aria-valuenow={Math.min(run.processed_chapters, run.total_chapters)}
                aria-valuetext={`已完成 ${run.processed_chapters} / ${run.total_chapters} 章`}
              >
                <div
                  className={`h-full rounded-full transition-[width] duration-500 ${stallLevel === 'stuck' ? 'bg-destructive' : stallLevel === 'slow' ? 'bg-amber-500' : 'bg-primary'}`}
                  style={{ width: `${progressPercent}%` }}
                />
              </div>
              {isActiveRun && (
                <p
                  className={`mt-1.5 flex items-start gap-1.5 text-[11px] leading-4 ${stallLevel === 'stuck' ? 'text-destructive' : stallLevel === 'slow' ? 'text-amber-700 dark:text-amber-300' : 'text-sky-700 dark:text-sky-300'}`}
                  data-testid="materialization-active-chapter"
                  role="status"
                >
                  {stallLevel === 'fresh'
                    ? <Loader2 className="mt-px h-3 w-3 shrink-0 animate-spin" aria-hidden="true" />
                    : stallLevel === 'slow'
                      ? <AlertTriangle className="mt-px h-3 w-3 shrink-0" aria-hidden="true" />
                      : <CircleAlert className="mt-px h-3 w-3 shrink-0" aria-hidden="true" />}
                  <span className="min-w-0 break-words">{heartbeatText}</span>
                </p>
              )}
            </div>
            {progress.length === 0 ? (
              <p className="mt-3 text-xs text-muted-foreground">尚未取得章节进度。</p>
            ) : (
              <>
                {(() => {
                  const visibleProgress = failedProgressOnly
                    ? progress.filter((item) => item.status === 'failed')
                    : progress
                  if (failedProgressOnly && visibleProgress.length === 0) {
                    return <p className="mt-3 text-xs text-muted-foreground" data-testid="no-failed-progress">已加载的章节里没有失败项。</p>
                  }
                  return (
                    <ol className="mt-3 divide-y divide-border border-y border-border" aria-label="材料化章节进度">
                      {visibleProgress.map((item) => (
                        <li key={item.chapter_index} className="grid grid-cols-[2.5rem_minmax(0,1fr)_auto] items-center gap-3 px-2 py-2 text-xs sm:px-3">
                          <span className="text-muted-foreground" title={`第 ${item.chapter_index} 章`}>{item.chapter_index}</span>
                          <span className="min-w-0">
                            <span className="block truncate text-foreground">
                              {stageLabel(item.current_stage)}
                              {extractionRoundText(item) && <span className="ml-1 font-normal text-muted-foreground">（{extractionRoundText(item)}）</span>}
                            </span>
                            <span className="mt-0.5 block text-[11px] text-muted-foreground">
                              候选 {formatCount(item.candidate_count)} · 准入 {formatCount(item.accepted_count)} · 待复核 {formatCount(item.review_count)} · 入库 {formatCount(item.vector_count)}
                            </span>
                            {item.last_error_code && (
                              <span className="mt-0.5 block text-[11px] text-destructive" title={item.last_error_message ?? undefined}>{chapterFailureText(item)}</span>
                            )}
                          </span>
                          <span className="flex shrink-0 flex-col items-end gap-1">
                            <span className={`rounded-full border border-border px-1.5 py-0.5 text-[11px] ${chapterStateTone(item.status)}`}>{chapterStateLabel(item.status)}</span>
                            {run.status === 'failed' && item.status === 'failed' && item.chapter_index === resumeFromChapter && (
                              <button
                                type="button"
                                onClick={() => { void retry() }}
                                disabled={isBusy}
                                data-testid={`resume-from-chapter-${item.chapter_index}`}
                                className="inline-flex items-center gap-1 rounded border border-border px-1.5 py-0.5 text-[11px] font-medium text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50"
                              >
                                {action === 'retry' ? <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" /> : <RefreshCcw className="h-3 w-3" aria-hidden="true" />}
                                从这里继续
                              </button>
                            )}
                          </span>
                        </li>
                      ))}
                    </ol>
                  )
                })()}
                {!failedProgressOnly && progress.length < progressTotal && (
                  <button
                    type="button"
                    onClick={() => { void loadMoreProgress() }}
                    disabled={loadingMoreProgress}
                    className="mt-2 inline-flex h-8 items-center rounded-md border border-border px-3 text-xs font-medium text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50"
                    data-testid="load-more-progress"
                  >
                    {loadingMoreProgress ? '加载中…' : `加载更多章节（还有 ${formatCount(progressTotal - progress.length)} 章）`}
                  </button>
                )}
                {failedProgressOnly && (
                  <p className="mt-2 text-[11px] text-muted-foreground">
                    筛选范围：已加载的前 {formatCount(progress.length)} 章；全部 {formatCount(progressTotal)} 章里还有未加载的部分时，先取消筛选点「加载更多章节」。
                  </p>
                )}
              </>
            )}
          </section>
        )}

        {run && candidates.length > 0 && (
          <section className="py-4" aria-labelledby="review-heading">
            <div className="flex items-center gap-2">
              <ClipboardCheck className="h-4 w-4 text-muted-foreground" aria-hidden="true" />
              <div>
                <h2 id="review-heading" className="text-sm font-semibold text-foreground">候选复核</h2>
                <p className="mt-1 text-xs text-muted-foreground">确认或拒绝后，候选会重新经过大模型准入与向量处理。</p>
              </div>
            </div>
            <p className="mt-1 text-[11px] text-muted-foreground" data-testid="candidate-total">
              待复核共 {formatCount(candidateTotal)} 条{candidates.length < candidateTotal ? `，当前显示前 ${formatCount(candidates.length)} 条` : ''}
            </p>
            <div className="mt-2 flex items-center gap-1.5" role="group" aria-label="复核抽样">
              <span className="text-[11px] text-muted-foreground">抽样复核：</span>
              {([null, 5, 10] as const).map((limit) => (
                <button
                  key={String(limit)}
                  type="button"
                  onClick={() => { setSampleLimit(limit) }}
                  aria-pressed={sampleLimit === limit}
                  className={`h-6 min-w-9 rounded border px-1.5 text-[11px] transition-colors ${sampleLimit === limit ? 'border-primary bg-primary/10 text-primary' : 'border-border text-muted-foreground hover:text-foreground'}`}
                >
                  {limit === null ? '全部' : `前 ${limit} 条`}
                </button>
              ))}
            </div>
            <ol className="mt-3 space-y-2" aria-label="待复核候选">
              {(sampleLimit === null ? candidates : candidates.slice(0, sampleLimit)).map((candidate) => (
                <ReviewCandidateCard
                  key={candidate.candidate_id}
                  candidate={candidate}
                  busy={isBusy}
                  onReview={reviewCandidate}
                />
              ))}
            </ol>
            {candidates.length < candidateTotal && (
              <button
                type="button"
                onClick={() => { void loadMoreCandidates() }}
                disabled={loadingMore || isBusy}
                className="mt-2 inline-flex h-8 items-center rounded-md border border-border px-3 text-xs font-medium text-foreground hover:bg-secondary disabled:cursor-not-allowed disabled:opacity-50"
                data-testid="load-more-candidates"
              >
                {loadingMore ? '加载中…' : `加载更多（还有 ${formatCount(candidateTotal - candidates.length)} 条）`}
              </button>
            )}
          </section>
        )}
      </div>

      <SettingsDialog
        open={showModelSettings}
        onClose={() => setShowModelSettings(false)}
        initialTab="model"
      />
    </main>
  )
}

function ReviewCandidateCard({ candidate, busy, onReview }: {
  candidate: reference.MaterializationCandidate
  busy: boolean
  onReview: (candidate: reference.MaterializationCandidate, nextAction: 'confirm' | 'reject') => Promise<void>
}) {
  const [showEvidence, setShowEvidence] = useState(false)

  return (
    <li className="border border-border px-3 py-3">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <p className="text-xs leading-5 text-foreground">{candidate.text_preview}</p>
          <div className="mt-2 flex flex-wrap gap-1">
            {candidateTags(candidate.tags).map((tag) => <span key={tag} className="border border-border bg-muted/35 px-1.5 py-0.5 text-[11px] text-muted-foreground">{taxonomyLabel(FEATURE_VALUE_LABELS, tag) !== tag ? taxonomyLabel(FEATURE_VALUE_LABELS, tag) : tag.replaceAll('_', ' ')}</span>)}
          </div>
          <p className="mt-2 text-[11px] text-muted-foreground">第 {candidate.chapter_index} 章 · {candidate.candidate_type === 'observation' ? '观察' : candidate.candidate_type === 'specimen' ? '标本' : candidate.candidate_type.replaceAll('_', ' ')} · {candidate.reason_codes.map((code) => taxonomyLabel(FEATURE_VALUE_LABELS, code) !== code ? taxonomyLabel(FEATURE_VALUE_LABELS, code) : code.replaceAll('_', ' ')).join('；') || '需要人工判断'}</p>
          {candidate.source_spans.length > 0 && (
            <>
              <button
                type="button"
                onClick={() => { setShowEvidence((open) => !open) }}
                aria-expanded={showEvidence}
                className="mt-1.5 rounded px-1 py-0.5 text-[11px] text-primary underline-offset-2 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                data-testid={`evidence-toggle-${candidate.candidate_id}`}
              >
                {showEvidence ? '收起证据定位' : `查看证据定位（${candidate.source_spans.length} 处）`}
              </button>
              {showEvidence && (
                <ul className="mt-1 space-y-0.5 rounded border border-border bg-muted/20 px-2 py-1.5 text-[11px] text-muted-foreground" data-testid={`evidence-list-${candidate.candidate_id}`}>
                  {candidate.source_spans.map((span) => (
                    <li key={`${span.node_id}:${span.start}`} className="break-all">
                      节点 {span.node_id} · 偏移 {span.start}–{span.end}
                    </li>
                  ))}
                </ul>
              )}
            </>
          )}
        </div>
        <div className="flex shrink-0 gap-1">
          <button type="button" onClick={() => { void onReview(candidate, 'confirm') }} disabled={busy} className="inline-flex h-7 items-center gap-1 rounded-md bg-primary px-2 text-[11px] font-medium text-primary-foreground hover:bg-primary/90 disabled:opacity-50"><CheckCircle2 className="h-3 w-3" aria-hidden="true" />确认</button>
          <button type="button" onClick={() => { void onReview(candidate, 'reject') }} disabled={busy} className="inline-flex h-7 items-center gap-1 rounded-md border border-border px-2 text-[11px] text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"><XCircle className="h-3 w-3" aria-hidden="true" />拒绝</button>
        </div>
      </div>
    </li>
  )
}
