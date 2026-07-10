import { useCallback, useEffect, useMemo, useState } from "react"
import { Ban, ChevronLeft, ChevronRight, CirclePause, CirclePlay, RefreshCcw, TimerReset } from "lucide-react"
import { useApp } from "@/hooks/useApp"
import type { reference } from "@/lib/novelist/types"

interface Props { novelId: number }

const statusLabels: Record<string, string> = {
 queued: "排队中", running: "运行中", pause_requested: "等待暂停", paused: "已暂停",
 retry_wait: "等待重试", budget_exhausted: "预算耗尽", partial_completed: "部分完成",
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
 {error && <div className="mb-3 border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">{error}</div>}
 <div className="space-y-2">
 {jobs.map(job => {
 const retry = retryText(job.next_attempt_at)
 return <article key={job.job_id} className="border border-border bg-background p-3">
 <div className="flex flex-wrap items-start justify-between gap-2">
 <div className="min-w-0">
 <div className="flex flex-wrap items-center gap-2 text-xs font-medium">
 <span>{job.job_kind}</span><span className="text-muted-foreground">{job.scope}</span>
 <span className="border border-border px-1.5 py-0.5 text-[10px]">{statusLabels[job.status] ?? job.status}</span>
 </div>
 <div className="mt-1 text-[11px] text-muted-foreground">锚点 {job.anchor_id}{job.current_chapter != null ? ` · 第 ${job.current_chapter} 章` : ""} · 尝试 {job.attempt_count}/{job.max_attempts}</div>
 </div>
 <div className="flex items-center gap-1">
 {(job.allowed_actions ?? []).map(action => <button key={action} type="button" className="inline-flex h-7 items-center gap-1 border border-border px-2 text-[11px] disabled:opacity-50" disabled={busyJobId === job.job_id} onClick={() => void runAction(job, action)} title={actionLabels[action] ?? action}>
 {action === "pause" && <CirclePause className="h-3.5 w-3.5" />}
 {action === "resume" && <CirclePlay className="h-3.5 w-3.5" />}
 {action === "cancel" && <Ban className="h-3.5 w-3.5" />}
 {action === "reprioritize" && <TimerReset className="h-3.5 w-3.5" />}
 {actionLabels[action] ?? action}
 </button>)}
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
 </div>
 {(job.safe_diagnostics?.length ?? 0) > 0 && <div className="mt-2 border-l-2 border-amber-500/60 pl-2 text-[11px] text-muted-foreground">{job.safe_diagnostics?.join(" · ")}</div>}
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
