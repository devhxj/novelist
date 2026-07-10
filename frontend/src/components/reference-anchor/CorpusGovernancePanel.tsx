import { useCallback, useEffect, useMemo, useState } from 'react'
import { CheckCircle2, Database, RefreshCcw, ShieldCheck, SlidersHorizontal } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reference } from '@/lib/novelist/types'

interface Props { novelId: number }

const labels: Record<string, string> = {
 global: '全局库', project: '项目库', private: '私有库', authorized: '已授权',
 user_owned: '用户自有', unknown: '授权待确认', verbatim_ok: '允许原句复用',
 adapted_only: '仅允许适配改写', reference_only: '仅供参考', forbidden: '禁止使用',
 active: '有效', stale: '待重建', superseded: '已被新分析替代', low_confidence: '置信度偏低',
 cross_run_conflict: '不同分析轮次结果冲突', observation: '分析结果', syntax: '句法',
 rhythm: '节奏', sensory: '感官', emotion: '情绪', rhetoric: '修辞',
 narrative_function: '叙事功能', perspective: '视角', character_dynamics: '人物关系',
 action_chain: '动作链', commercial_mechanics: '商业机制',
}

const labelOf = (value: string) => labels[value] ?? value

export function CorpusGovernancePanel({ novelId }: Props) {
 const app = useApp()
 const sessionId = `project:${novelId}:default`
 const [expert, setExpert] = useState(false)
 const [governance, setGovernance] = useState<reference.CorpusGovernance | null>(null)
 const [aggregates, setAggregates] = useState<reference.CorpusAggregate[]>([])
 const [reviewPage, setReviewPage] = useState<reference.CorpusReviewQueuePage | null>(null)
 const [reviewCursor, setReviewCursor] = useState<string | null>(null)
 const [reviewHistory, setReviewHistory] = useState<Array<string | null>>([])
 const [selected, setSelected] = useState<string[]>([])
 const [busy, setBusy] = useState(false)
 const [message, setMessage] = useState('')
 const [error, setError] = useState('')

 const load = useCallback(async () => {
 setBusy(true)
 setError('')
 try {
 const [nextGovernance, nextAggregates, nextReviews] = await Promise.all([
 app.GetReferenceCorpusGovernance({ session_id: sessionId }),
 app.ListReferenceCorpusAggregates({ aggregate_type: null }),
 app.ListReferenceCorpusReviewQueue({
 page_request: { cursor: reviewCursor, page_size: 20, sort_by: 'created_at', sort_dir: 'asc' },
 }),
 ])
 setGovernance(nextGovernance)
 setAggregates(nextAggregates)
 setReviewPage(nextReviews)
 } catch (caught) {
 setError(caught instanceof Error ? caught.message : '语料治理数据加载失败')
 } finally {
 setBusy(false)
 }
 }, [app, reviewCursor, sessionId])

 useEffect(() => {
 queueMicrotask(() => { void load() })
 }, [load])

 const enabledLibraries = useMemo(
 () => governance?.libraries.filter(library => library.bound_to_session) ?? [],
 [governance],
 )

 const bindLibrary = async (library: reference.CorpusGovernanceLibrary) => {
 setGovernance(await app.SetReferenceCorpusSessionLibraryBinding({
 session_id: sessionId, library_id: library.library_id, enabled: !library.bound_to_session,
 }))
 }
 const toggleMember = async (library: reference.CorpusGovernanceLibrary, member: reference.CorpusGovernanceMember) => {
 setGovernance(await app.UpdateReferenceCorpusLibraryMember({
 library_id: library.library_id, anchor_id: member.anchor_id, enabled: !member.enabled,
 source_quality: member.source_quality ?? 'standard',
 disabled_reason: member.enabled ? '用户在治理面板禁用' : null,
 }))
 }
 const authorize = async (member: reference.CorpusGovernanceMember) => {
 setGovernance(await app.UpdateReferenceCorpusLicense({
 anchor_id: member.anchor_id, license_state: 'authorized', authorization_evidence: '用户在治理面板确认',
 reuse_policy: 'adapted_only', max_verbatim_ratio: 0.35, cleared_for_insertion: true,
 }))
 }
 const rebuild = async () => {
 const result = await app.RebuildReferenceCorpusDedupGroups({ library_id: null })
 setMessage(`已扫描 ${result.members_scanned} 个来源，形成 ${result.groups_assigned} 个去重组。`)
 await load()
 }
 const buildAggregates = async () => {
 setAggregates(await app.BuildReferenceCorpusAggregates({
 library_ids: enabledLibraries.map(library => library.library_id), run_id: null,
 }))
 }
 const refreshReviews = async () => {
 await app.RefreshReferenceCorpusReviewQueue({ confidence_threshold: 0.7 })
 setReviewHistory([])
 setReviewCursor(null)
 if (reviewCursor === null) await load()
 }
 const review = async (state: 'confirmed' | 'rejected') => {
 await app.ReviewReferenceCorpusItems({ queue_ids: selected, review_state: state })
 setSelected([])
 await load()
 }
 const previousReviewPage = () => {
 const previous = reviewHistory.at(-1) ?? null
 setReviewHistory(history => history.slice(0, -1))
 setReviewCursor(previous)
 }
 const nextReviewPage = () => {
 if (!reviewPage?.next_cursor) return
 setReviewHistory(history => [...history, reviewCursor])
 setReviewCursor(reviewPage.next_cursor)
 }

 return <div className="space-y-4" data-testid="corpus-governance-panel">
 {error && <div className="rounded-lg border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">{error}</div>}
 <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-border bg-card p-4">
 <div><h3 className="text-sm font-semibold">语料治理与复核</h3><p className="mt-1 text-xs text-muted-foreground">管理生效语料库、授权、聚合知识与人工复核。</p></div>
 <div className="flex gap-2"><button className="rounded-md border px-3 py-1.5 text-xs" onClick={() => setExpert(value => !value)}><SlidersHorizontal className="mr-1 inline h-3.5 w-3.5" />{expert ? '自动模式' : '专家模式'}</button><button className="rounded-md border px-3 py-1.5 text-xs" onClick={() => void load()} disabled={busy}><RefreshCcw className="mr-1 inline h-3.5 w-3.5" />刷新</button></div>
 </div>
 <div className="grid gap-3 md:grid-cols-4"><Metric icon={<Database />} label="生效语料库" value={enabledLibraries.length} /><Metric icon={<ShieldCheck />} label="可插入来源" value={enabledLibraries.flatMap(library => library.members).filter(member => member.cleared_for_insertion).length} /><Metric icon={<CheckCircle2 />} label="待复核" value={reviewPage?.total ?? 0} /><Metric icon={<Database />} label="聚合知识" value={aggregates.length} /></div>
 <section className="rounded-lg border border-border bg-card p-4"><div className="mb-3 flex items-center justify-between"><h4 className="text-xs font-semibold">会话生效语料库</h4><button className="text-xs text-primary" onClick={() => void rebuild()}>重建去重组</button></div>
 <div className="space-y-2">{governance?.libraries.map(library => <div key={library.library_id} className="rounded-md border p-3"><label className="flex items-center gap-2 text-xs font-medium"><input type="checkbox" checked={library.bound_to_session} onChange={() => void bindLibrary(library)} />{library.name}<span className="text-muted-foreground">{labelOf(library.scope)} · {library.members.length} 来源</span></label>{expert && <div className="mt-3 space-y-2">{library.members.map(member => <div key={member.anchor_id} className="flex flex-wrap items-center justify-between gap-2 rounded bg-muted/40 px-3 py-2 text-xs"><div><div className="font-medium">{member.title}</div><div className="text-muted-foreground">{labelOf(member.license_state)} / {labelOf(member.reuse_policy)} / {member.dedup_group_id ?? '未归入去重组'}</div></div><div className="flex gap-2"><button className="rounded border px-2 py-1" onClick={() => void authorize(member)}>确认授权</button><button className="rounded border px-2 py-1" onClick={() => void toggleMember(library, member)}>{member.enabled ? '禁用' : '启用'}</button></div></div>)}</div>}</div>)}</div>{message && <p className="mt-2 text-xs text-emerald-600">{message}</p>}</section>
 <section className="rounded-lg border border-border bg-card p-4"><div className="mb-3 flex items-center justify-between"><h4 className="text-xs font-semibold">聚合知识</h4><button className="text-xs text-primary disabled:opacity-50" disabled={enabledLibraries.length === 0} onClick={() => void buildAggregates()}>按生效库重建</button></div><div className="grid gap-2 md:grid-cols-2">{aggregates.map(item => <div key={item.aggregate_id} className="rounded-md border p-3"><div className="flex justify-between text-xs font-medium"><span>{item.name}</span><span>{labelOf(item.validity_state)}</span></div><p className="mt-1 text-xs text-muted-foreground">{item.summary}</p>{expert && <p className="mt-2 text-[11px] text-muted-foreground">{item.library_ids.length} 库 · {item.anchor_ids.length} 来源 · {item.sample_count} 证据</p>}</div>)}</div></section>
 {expert && <section className="rounded-lg border border-border bg-card p-4"><div className="mb-3 flex flex-wrap items-center justify-between gap-2"><h4 className="text-xs font-semibold">人工复核队列</h4><div className="flex gap-2"><button className="rounded border px-2 py-1 text-xs" onClick={() => void refreshReviews()}>刷新队列</button><button className="rounded border px-2 py-1 text-xs" disabled={selected.length === 0} onClick={() => void review('confirmed')}>批量确认</button><button className="rounded border px-2 py-1 text-xs" disabled={selected.length === 0} onClick={() => void review('rejected')}>批量拒绝</button></div></div><div className="space-y-2">{reviewPage?.items.map(item => <label key={item.queue_id} className="flex items-start gap-2 rounded-md border p-3 text-xs"><input type="checkbox" checked={selected.includes(item.queue_id)} onChange={() => setSelected(current => current.includes(item.queue_id) ? current.filter(id => id !== item.queue_id) : [...current, item.queue_id])} /><span><span className="font-medium">{labelOf(item.feature_family ?? item.item_type)} · {labelOf(item.reason)}</span><span className="block text-muted-foreground">节点 {item.node_id} · 置信度 {item.confidence.toFixed(2)}</span></span></label>)}</div><div className="mt-3 flex items-center justify-between text-xs"><button className="rounded border px-2 py-1 disabled:opacity-40" disabled={reviewHistory.length === 0} onClick={previousReviewPage}>上一页</button><span>第 {reviewPage?.page ?? 1} / {reviewPage?.total_pages ?? 1} 页 · 共 {reviewPage?.total ?? 0} 项</span><button className="rounded border px-2 py-1 disabled:opacity-40" disabled={!reviewPage?.has_more || !reviewPage.next_cursor} onClick={nextReviewPage}>下一页</button></div></section>}
 </div>
}

function Metric({ icon, label, value }: { icon: React.ReactNode; label: string; value: number }) {
 return <div className="rounded-lg border border-border bg-card p-3"><div className="flex items-center gap-2 text-muted-foreground [&_svg]:h-4 [&_svg]:w-4">{icon}<span className="text-xs">{label}</span></div><div className="mt-2 text-xl font-semibold">{value}</div></div>
}
