import { CheckCircle2, CircleStop, FileClock, History, Loader2, Play, RefreshCcw, ShieldAlert } from 'lucide-react'
import type { ReactNode } from 'react'
import type { reference } from '@/lib/novelist/types'
import { actionButtonClass, inputClass, statusTone } from './referenceAnchorStyles'

type OrchestrationPanelProps = {
  chapterNumber: string
  chapterGoal: string
  knownFacts: string
  forbiddenFacts: string
  useSelectedAnchors: boolean
  selectedAnchorCount: number
  runs: reference.OrchestrationRun[]
  activeRun: reference.OrchestrationRun | null
  events: reference.OrchestrationRunEvent[]
  loading: boolean
  onChapterNumberChange: (value: string) => void
  onChapterGoalChange: (value: string) => void
  onKnownFactsChange: (value: string) => void
  onForbiddenFactsChange: (value: string) => void
  onUseSelectedAnchorsChange: (value: boolean) => void
  onStart: () => void
  onSelectRun: (runId: string) => void
  onRefresh: () => void
  onResume: (decisionType: string, payload: string) => void
  onCancel: (runId: string) => void
}

function decisionLabel(decisionType: string): string {
  switch (decisionType) {
    case 'confirm_source_and_facts': return '确认来源与事实边界'
    case 'apply_blueprint_revision': return '应用蓝图修订'
    case 'approve_blueprint': return '批准蓝图'
    case 'resolve_high_risk_stop': return '确认高风险停止'
    case 'approve_final_insertion': return '候选已审阅'
    default: return decisionType || '待确认'
  }
}

function stageLabel(stage: string): string {
  switch (stage) {
    case 'source_confirmation': return '来源确认'
    case 'blueprint_generation': return '蓝图生成'
    case 'blueprint_review': return '蓝图评审'
    case 'blueprint_approval': return '蓝图批准'
    case 'material_binding': return '材料绑定'
    case 'draft_generation': return '候选生成'
    case 'draft_audit': return '草稿审计'
    case 'final_insertion': return '最终插入前'
    default: return stage || '未知阶段'
  }
}

function formatTime(value: unknown): string {
  if (typeof value !== 'string') return ''
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

function payloadForDecision(run: reference.OrchestrationRun, decisionType: string): string {
  if (decisionType === 'apply_blueprint_revision' && run.current_decision?.proposed_blueprint_revision) {
    return JSON.stringify(run.current_decision.proposed_blueprint_revision)
  }

  if (decisionType === 'approve_blueprint') {
    return run.review_id
  }

  if (decisionType === 'approve_final_insertion') {
    return run.candidate_ids.join('\n')
  }

  if (decisionType === 'resolve_high_risk_stop') {
    return 'acknowledged'
  }

  return 'confirmed'
}

function summaryItems(summary: reference.OrchestrationApprovalSummary): Array<[string, string | string[]]> {
  const textOrFallback = (value: string, fallback: string) => value.trim() || fallback
  const listOrFallback = (value: string[], fallback: string) => value.length > 0 ? value : [fallback]
  return [
    ['章节功能', textOrFallback(summary.chapter_function, '未提供')],
    ['POV', textOrFallback(summary.pov, '未选择')],
    ['情绪轨迹', textOrFallback(summary.emotional_trajectory, '未提供')],
    ['材料计划', textOrFallback(summary.material_use_plan, '未提供')],
    ['改写预算', textOrFallback(summary.rewrite_budget, '未提供')],
    ['事实边界', listOrFallback(summary.fact_boundary_changes, '无事实边界变更')],
    ['高风险', listOrFallback(summary.high_risk_findings, '无高风险')],
  ]
}

function canResumeDecision(run: reference.OrchestrationRun, decisionType: string): boolean {
  if (decisionType === 'approve_final_insertion') return false
  if (decisionType === 'apply_blueprint_revision') {
    return (run.current_decision?.proposed_blueprint_revision?.changes.length ?? 0) > 0
  }
  return true
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

export function OrchestrationPanel({
  chapterNumber,
  chapterGoal,
  knownFacts,
  forbiddenFacts,
  useSelectedAnchors,
  selectedAnchorCount,
  runs,
  activeRun,
  events,
  loading,
  onChapterNumberChange,
  onChapterGoalChange,
  onKnownFactsChange,
  onForbiddenFactsChange,
  onUseSelectedAnchorsChange,
  onStart,
  onSelectRun,
  onRefresh,
  onResume,
  onCancel,
}: OrchestrationPanelProps) {
  const decision = activeRun?.current_decision ?? null
  const decisionType = decision?.decision_type ?? ''
  const canResume = Boolean(activeRun && decision && canResumeDecision(activeRun, decisionType))
  const resumePayload = activeRun && decision ? payloadForDecision(activeRun, decisionType) : ''

  return (
    <div className="rounded-lg border border-border bg-card p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <Play className="h-3.5 w-3.5 text-muted-foreground" />
            <h3 className="text-xs font-semibold text-foreground">默认编排</h3>
          </div>
          <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
            先确认来源和事实边界，再自动生成、评审、绑定材料并审计候选；最终正文仍需作者单独处理。
          </p>
          <div className="mt-2 grid grid-cols-1 gap-2 text-[11px] text-muted-foreground md:grid-cols-2">
            <div className="rounded-md border border-border bg-background px-2.5 py-2">
              <span className="font-medium text-foreground">AI 自动阶段</span>
              <span className="mt-1 block leading-relaxed">生成蓝图、确定性评审、绑定材料、生成候选、草稿审计</span>
            </div>
            <div className="rounded-md border border-border bg-background px-2.5 py-2">
              <span className="font-medium text-foreground">作者决策</span>
              <span className="mt-1 block leading-relaxed">来源/事实边界、蓝图批准或修订、高风险停止、最终正文插入</span>
            </div>
          </div>
        </div>
        <button type="button" onClick={onRefresh} disabled={loading} className={actionButtonClass}>
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCcw className="h-3.5 w-3.5" />}
          刷新
        </button>
      </div>

      <div className="mt-4 grid grid-cols-1 gap-3 lg:grid-cols-[88px_minmax(0,1fr)_minmax(0,1fr)]">
        <Field label="章节号">
          <input value={chapterNumber} onChange={event => onChapterNumberChange(event.target.value)} className={inputClass} inputMode="numeric" />
        </Field>
        <Field label="章节目标">
          <input value={chapterGoal} onChange={event => onChapterGoalChange(event.target.value)} className={inputClass} placeholder="本章要完成的逻辑、情绪或钩子" />
        </Field>
        <div className="flex items-end">
          <button type="button" onClick={onStart} disabled={loading} className="inline-flex w-full items-center justify-center gap-1.5 rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50">
            <Play className="h-3.5 w-3.5" />
            启动候选编排
          </button>
        </div>
        <Field label="已知事实">
          <textarea value={knownFacts} onChange={event => onKnownFactsChange(event.target.value)} className={`${inputClass} min-h-16 resize-y`} placeholder="一行一个" />
        </Field>
        <Field label="禁止事实">
          <textarea value={forbiddenFacts} onChange={event => onForbiddenFactsChange(event.target.value)} className={`${inputClass} min-h-16 resize-y`} placeholder="一行一个" />
        </Field>
        <label className="flex items-center gap-2 self-start rounded-md border border-border bg-background px-3 py-2 text-xs text-muted-foreground">
          <input
            type="checkbox"
            checked={useSelectedAnchors}
            onChange={event => onUseSelectedAnchorsChange(event.target.checked)}
            className="shrink-0"
          />
          <span>限制到已选锚点（{selectedAnchorCount}）</span>
        </label>
      </div>

      <div className="mt-4 grid grid-cols-1 gap-4 xl:grid-cols-[280px_minmax(0,1fr)]">
        <div className="space-y-2">
          <div className="flex items-center gap-2">
            <FileClock className="h-3.5 w-3.5 text-muted-foreground" />
            <h4 className="text-xs font-semibold text-foreground">运行记录</h4>
          </div>
          {runs.length === 0 ? (
            <p className="rounded-md border border-dashed border-border bg-background px-3 py-3 text-xs text-muted-foreground">暂无编排运行。</p>
          ) : (
            <div className="space-y-2">
              {runs.slice(0, 6).map(run => (
                <button
                  type="button"
                  key={run.run_id}
                  onClick={() => onSelectRun(run.run_id)}
                  className={`w-full rounded-md border px-3 py-2 text-left transition-colors ${activeRun?.run_id === run.run_id ? 'border-primary bg-secondary' : 'border-border bg-background hover:bg-secondary/60'}`}
                >
                  <span className="flex items-center justify-between gap-2">
                    <span className="min-w-0 truncate text-xs font-medium text-foreground">第{run.chapter_number}章 · {stageLabel(run.stage)}</span>
                    <span className={`shrink-0 text-[11px] ${statusTone(run.status)}`}>{run.status}</span>
                  </span>
                  <span className="mt-1 block truncate text-[11px] text-muted-foreground">{run.last_stop_reason || run.run_id}</span>
                </button>
              ))}
            </div>
          )}
        </div>

        <div className="min-w-0 rounded-md border border-border bg-background p-3">
          {!activeRun ? (
            <div className="flex min-h-[160px] flex-col items-center justify-center text-center">
              <History className="h-7 w-7 text-muted-foreground" />
              <p className="mt-2 text-xs font-medium text-foreground">选择或启动一个编排运行</p>
              <p className="mt-1 max-w-sm text-xs leading-relaxed text-muted-foreground">运行详情会显示当前停止原因、审批摘要和本地事件历史。</p>
            </div>
          ) : (
            <div className="space-y-3">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <p className="truncate text-xs font-semibold text-foreground">第{activeRun.chapter_number}章 · {stageLabel(activeRun.stage)}</p>
                  <p className={`mt-1 text-[11px] ${statusTone(activeRun.status)}`}>{activeRun.status} · {activeRun.last_stop_reason || 'running'}</p>
                </div>
                <button type="button" onClick={() => onCancel(activeRun.run_id)} disabled={loading || activeRun.status === 'cancelled' || activeRun.status === 'completed'} className={actionButtonClass}>
                  <CircleStop className="h-3.5 w-3.5" />
                  取消
                </button>
              </div>

              {decision ? (
                <div className="rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-2">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="flex min-w-0 items-center gap-2">
                      <ShieldAlert className="h-3.5 w-3.5 text-amber-700 dark:text-amber-300" />
                      <p className="truncate text-xs font-semibold text-foreground">{decisionLabel(decision.decision_type)}</p>
                    </div>
                    {decisionType === 'approve_final_insertion' ? (
                      <span className="rounded bg-background px-2 py-1 text-[11px] text-muted-foreground">候选已就绪</span>
                    ) : (
                      <button type="button" onClick={() => onResume(decisionType, resumePayload)} disabled={loading || !canResume} className="inline-flex items-center gap-1.5 rounded bg-primary px-2.5 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50">
                        <CheckCircle2 className="h-3.5 w-3.5" />
                        确认
                      </button>
                    )}
                  </div>
                  <p className="mt-2 text-xs leading-relaxed text-muted-foreground">{decision.summary}</p>
                  <div className="mt-2 flex flex-wrap gap-1">
                    {decision.required_actions.map(action => (
                      <span key={action} className="rounded bg-background px-1.5 py-0.5 text-[11px] text-muted-foreground">{action}</span>
                    ))}
                  </div>
                </div>
              ) : (
                <p className="rounded-md border border-border bg-card px-3 py-2 text-xs text-muted-foreground">当前运行没有待确认决策。</p>
              )}

              {decision && (
                <div className="rounded-md border border-border bg-card px-3 py-2">
                  <h4 className="text-xs font-semibold text-foreground">审批摘要</h4>
                  <dl className="mt-2 grid grid-cols-1 gap-2 lg:grid-cols-2">
                    {summaryItems(decision.approval_summary).map(([label, value]) => (
                      <div key={label}>
                        <dt className="text-[11px] font-medium text-muted-foreground">{label}</dt>
                        <dd className="mt-0.5 text-xs leading-relaxed text-foreground">
                          {Array.isArray(value) ? value.join('；') : value}
                        </dd>
                      </div>
                    ))}
                  </dl>
                </div>
              )}

              {activeRun.candidate_ids.length > 0 && (
                <div className="rounded-md border border-border bg-card px-3 py-2">
                  <p className="text-xs font-semibold text-foreground">候选</p>
                  <div className="mt-2 flex flex-wrap gap-1">
                    {activeRun.candidate_ids.map(id => (
                      <span key={id} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">{id}</span>
                    ))}
                  </div>
                </div>
              )}

              <div className="rounded-md border border-border bg-card px-3 py-2">
                <h4 className="text-xs font-semibold text-foreground">事件历史</h4>
                {events.length === 0 ? (
                  <p className="mt-2 text-xs text-muted-foreground">暂无事件。</p>
                ) : (
                  <div className="mt-2 space-y-2">
                    {events.slice(-8).map(event => (
                      <div key={event.event_id} className="border-l border-border pl-3">
                        <div className="flex flex-wrap items-center gap-2">
                          <span className="text-xs font-medium text-foreground">{event.event_type}</span>
                          <span className="text-[11px] text-muted-foreground">{stageLabel(event.stage)}</span>
                          {event.decision_type && <span className="text-[11px] text-muted-foreground">{decisionLabel(event.decision_type)}</span>}
                          <span className="text-[11px] text-muted-foreground">{formatTime(event.created_at)}</span>
                        </div>
                        <p className="mt-0.5 line-clamp-2 text-[11px] leading-relaxed text-muted-foreground">{event.summary}</p>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
