import { useCallback, useEffect, useMemo, useState } from "react"
import { Ban, ChevronLeft, ChevronRight, CirclePause, CirclePlay, RefreshCcw, TimerReset } from "lucide-react"
import { useApp } from "@/hooks/useApp"
import type { reference } from "@/lib/novelist/types"

interface Props { novelId: number }

const statusLabels: Record<string, string> = {
 queued: "排队中", running: "运行中", pause_requested: "等待暂停", paused: "已暂停",
 cancel_requested: "等待取消", retry_wait: "等待重试", budget_exhausted: "预算耗尽",
 completed: "已完成", failed: "失败", cancelled: "已取消",
}

const actionLabels: Record<string, string> = {
 pause: "暂停", resume: "继续", cancel: "取消", reprioritize: "提高优先级",
}

function ratio(value: number, total: number) {
 return total > 0 ? Math.min(100, Math.round(value * 100 / total)) : 0
}

function retryText(value: unknown) {
 if (typeof value !== "string" || !value) return null
 const milliseconds = new Date(value).getTime() - Date.now()
 if (milliseconds <= 0) return "即将重试"
 const seconds = Math.ceil(milliseconds / 1000)
 return seconds < 60 ? `${seconds} 秒后重试` : `${Math.ceil(seconds / 60)} 分钟后重试`
}

function timestampMilliseconds(value: unknown) {
 if (typeof value === "string" || typeof value === "number" || value instanceof Date) {
 return new Date(value).getTime()
 }
 return Number.NaN
}

function elapsedText(job: reference.CorpusAnalysisJob) {
 const startedAt = timestampMilliseconds(job.started_at ?? job.queued_at)
 const endedAt = timestampMilliseconds(job.completed_at ?? job.updated_at)
 if (!Number.isFinite(startedAt) || !Number.isFinite(endedAt)) return null
 const seconds = Math.max(0, Math.floor((endedAt - startedAt) / 1000))
 const duration = seconds < 60
 ? `${seconds} 秒`
 : `${Math.floor(seconds / 60)} 分钟${seconds % 60 ? ` ${seconds % 60} 秒` : ""}`
 return ["completed", "cancelled", "failed"].includes(job.status) ? `本次用时 ${duration}` : `已运行 ${duration}`
}

function nextStep(job: reference.CorpusAnalysisJob): { message: string; action?: string } {
 const can = new Set(job.allowed_actions ?? [])
 const resume = can.has("resume") ? "resume" : undefined
 const pause = can.has("pause") ? "pause" : undefined
 switch (job.status) {
 case "queued": return { message: "任务已加入队列。可以离开此页面，返回后会显示最新进度。" }
 case "running": return { message: "任务正在后台运行。可以离开此页面，返回后会继续显示进度。", action: pause }
 case "pause_requested": return { message: "正在等待当前工作项结束后暂停。" }
 case "paused": return { message: "任务已暂停，继续后会从当前进度接着处理。", action: resume }
 case "retry_wait": return { message: retryText(job.next_attempt_at) ?? "系统会按退避计划自动重试。" }
 case "budget_exhausted": return { message: "预算已耗尽。补充可用预算后，可以从当前进度继续。", action: resume }
 case "failed": return { message: "任务未完成。继续会从最后保存的进度恢复。", action: resume }
 case "cancel_requested": return { message: "正在停止。当前工作项结束后不会再领取新任务。" }
 case "completed": return { message: "分析已完成，可前往“分析结果”查看产物。" }
 case "cancelled": return { message: "任务已取消；需要时可重新启动分析。" }
 default: return { message: "状态已更新，返回这里可继续查看可用操作。" }
 }
}

function JobActionButton({
 job,
 action,
 busy,
 onAction,
 primary = false,
}: {
 job: reference.CorpusAnalysisJob
 action: string
 busy: boolean
 onAction: (job: reference.CorpusAnalysisJob, action: string) => void
 primary?: boolean
}) {
 return <button
 type="button"
 className={`inline-flex h-7 items-center gap-1 border px-2 text-[11px] disabled:opacity-50 ${primary ? "border-primary bg-primary text-primary-foreground hover:opacity-90" : "border-border bg-background hover:bg-secondary"}`}
 disabled={busy}
 onClick={() => void onAction(job, action)}
 title={actionLabels[action] ?? action}
 >
 {action === "pause" && <CirclePause className="h-3.5 w-3.5" />}
 {action === "resume" && <CirclePlay className="h-3.5 w-3.5" />}
 {action === "cancel" && <Ban className="h-3.5 w-3.5" />}
 {action === "reprioritize" && <TimerReset className="h-3.5 w-3.5" />}
 {actionLabels[action] ?? action}
 </button>
}

export function CorpusAnalysisJobsPanel({ novelId }: Props) {
 const app = useApp()
 const [page, setPage] = useState<reference.CorpusAnalysisJobPage | null>(null)
 const [cursor, setCursor] = useState<string | null>(null)
 const [history, setHistory] = useState<Array<string | null>>([])
 const [busyJobId, setBusyJobId] = useState<string | null>(null)
 const [error, setError] = useState("")

 const load = useCallback(async () => {
 try {
 setError("")
setPage(await app.ListReferenceCorpusAnalysisJobs({
 page_request: {
 cursor,
 page_size: 20,
 sort_by: "updated_at",
 sort_dir: "desc",
 filters: { novel_id: String(novelId) },
 },
}))
 } catch (caught) {
 setError(caught instanceof Error ? caught.message : "后台任务加载失败")
 }
 }, [app, cursor, novelId])

 useEffect(() => {
 queueMicrotask(() => { void load() })
 const timer = window.setInterval(() => { void load() }, 5000)
 return () => window.clearInterval(timer)
 }, [load])

 const jobs = useMemo(() => page?.items ?? [], [page])

 const runAction = async (job: reference.CorpusAnalysisJob, action: string) => {
 setBusyJobId(job.job_id)
 setError("")
 try {
 if (action === "pause") await app.PauseReferenceCorpusAnalysisJob({ job_id: job.job_id, expected_version: job.version })
 if (action === "resume") await app.ResumeReferenceCorpusAnalysisJob({ job_id: job.job_id, expected_version: job.version })
 if (action === "cancel") await app.CancelReferenceCorpusAnalysisJob({ job_id: job.job_id, expected_version: job.version })
 if (action === "reprioritize") await app.ReprioritizeReferenceCorpusAnalysisJob({
 job_id: job.job_id,
 expected_version: job.version,
 priority_class: "current_chapter",
 priority_value: Math.max(job.priority_value, 100),
 })
 } catch (caught) {
 setError(caught instanceof Error ? caught.message : "任务状态已变化，列表已刷新")
 } finally {
 setBusyJobId(null)
 await load()
 }
 }

 const previous = () => {
 setCursor(history.at(-1) ?? null)
 setHistory(current => current.slice(0, -1))
 }

 const next = () => {
 if (!page?.next_cursor) return
 setHistory(current => [...current, cursor])
 setCursor(page.next_cursor ?? null)
 }

 return <section className="border border-border bg-card p-4" data-testid="corpus-analysis-jobs-panel">
 <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
 <div>
 <h4 className="text-xs font-semibold">后台分析任务</h4>
 <p className="mt-1 text-[11px] text-muted-foreground">项目 {novelId} · 服务端许可操作</p>
 </div>
 <button type="button" className="inline-flex h-7 w-7 items-center justify-center rounded border" onClick={() => void load()} title="刷新任务" aria-label="刷新后台任务"><RefreshCcw className="h-3.5 w-3.5" /></button>
 </div>
 {error && <div role="alert" className="mb-3 flex flex-wrap items-center justify-between gap-2 border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"><span>{error}</span><button type="button" className="border border-destructive/30 bg-background px-2 py-1 text-[11px] text-foreground" onClick={() => void load()}>刷新任务</button></div>}
 <div className="space-y-2">
 {jobs.map(job => {
 const retry = retryText(job.next_attempt_at)
 const timing = elapsedText(job)
 const recovery = nextStep(job)
 const availableActions = job.allowed_actions ?? []
 const secondaryActions = availableActions.filter(action => action !== recovery.action)
 return <article key={job.job_id} className="border border-border bg-background p-3" data-testid={`corpus-analysis-job-${job.job_id}`}>
 <div className="flex flex-wrap items-start justify-between gap-2">
 <div className="min-w-0">
 <div className="flex flex-wrap items-center gap-2 text-xs font-medium">
 <span>{job.job_kind}</span><span className="text-muted-foreground">{job.scope}</span>
 <span className="border border-border px-1.5 py-0.5 text-[10px]">{statusLabels[job.status] ?? job.status}</span>
 </div>
 <div className="mt-1 text-[11px] text-muted-foreground">锚点 {job.anchor_id}{job.current_chapter != null ? ` · 第 ${job.current_chapter} 章` : ""} · 尝试 {job.attempt_count}/{job.max_attempts}</div>
 </div>
 <div className="flex items-center gap-1">
 {recovery.action && <JobActionButton job={job} action={recovery.action} busy={busyJobId === job.job_id} onAction={runAction} primary />}
 {secondaryActions.length > 0 && <details className="relative"><summary className="cursor-pointer border border-border bg-background px-2 py-1 text-[11px] text-muted-foreground hover:bg-secondary">其他操作</summary><div className="absolute right-0 z-10 mt-1 flex min-w-max flex-col gap-1 border border-border bg-card p-1 shadow-sm">{secondaryActions.map(action => <JobActionButton key={action} job={job} action={action} busy={busyJobId === job.job_id} onAction={runAction} />)}</div></details>}
 </div>
 </div>
 <div className="mt-3 grid gap-2 md:grid-cols-2">
 <Progress label={`节点 ${job.processed_nodes}/${job.total_nodes}`} value={ratio(job.processed_nodes, job.total_nodes)} />
 <Progress label={`工作项 ${job.processed_work_items}/${job.total_work_items}`} value={ratio(job.processed_work_items, job.total_work_items)} />
 </div>
 <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-muted-foreground">
 <span>Token {job.tokens_spent}{job.tokens_reserved > 0 ? ` + ${job.tokens_reserved} 预留` : ""}{job.token_budget != null ? ` / ${job.token_budget}` : ""}</span>
 <span>成功 {job.succeeded_work_items} · 跳过 {job.skipped_work_items} · 失败 {job.failed_work_items} · 重试 {job.retrying_work_items}</span>
 {retry && <span>{retry}</span>}
 {timing && <span>{timing}</span>}
 </div>
 <p data-testid="corpus-analysis-job-next-step" role="status" className="mt-2 border-l-2 border-primary/60 pl-2 text-[11px] leading-relaxed text-muted-foreground">下一步：{recovery.message}</p>
 {(job.safe_diagnostics?.length ?? 0) > 0 && <details data-testid="corpus-analysis-job-diagnostics" className="mt-2 text-[11px] text-muted-foreground"><summary className="cursor-pointer select-none">查看技术诊断</summary><div className="mt-1 border-l-2 border-amber-500/60 pl-2">{job.safe_diagnostics?.join(" · ")}</div></details>}
 </article>
 })}
 {jobs.length === 0 && <p className="py-4 text-center text-xs text-muted-foreground">当前页没有本项目后台任务</p>}
 </div>
 <div className="mt-3 flex items-center justify-between text-[11px] text-muted-foreground">
 <span>共 {page?.total ?? 0} 项</span>
 <div className="flex items-center gap-1">
 <button type="button" className="inline-flex h-7 w-7 items-center justify-center border disabled:opacity-40" disabled={history.length === 0} onClick={previous} aria-label="上一页任务"><ChevronLeft className="h-3.5 w-3.5" /></button>
 <button type="button" className="inline-flex h-7 w-7 items-center justify-center border disabled:opacity-40" disabled={!page?.has_more} onClick={next} aria-label="下一页任务"><ChevronRight className="h-3.5 w-3.5" /></button>
 </div>
 </div>
 </section>
}

function Progress({ label, value }: { label: string; value: number }) {
 return <div><div className="mb-1 flex justify-between text-[11px] text-muted-foreground"><span>{label}</span><span>{value}%</span></div><div className="h-1.5 overflow-hidden bg-muted"><div className="h-full bg-primary" style={{ width: `${value}%` }} /></div></div>
}
