import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { Archive, BarChart3, BookOpenCheck, Eye, GitCompare, Loader2, RefreshCcw, RotateCcw, Wand2 } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { reference } from '@/lib/novelist/types'
import { lines } from './blueprintRevision'
import { actionButtonClass, inputClass, statusTone } from './referenceAnchorStyles'

type StyleProfileLibraryPanelProps = {
  novelId: number
  anchors: reference.Anchor[]
  selectedAnchorIds: number[]
}

type StyleProfileForm = {
  title: string
  description: string
  allowedLicenseStatuses: string
  allowedSourceTrustLevels: string
}

const EMPTY_STYLE_PROFILE_FORM: StyleProfileForm = {
  title: '',
  description: '',
  allowedLicenseStatuses: 'user_provided\nlicensed\npublic_domain',
  allowedSourceTrustLevels: 'user_verified\nimported',
}

function formatTime(value: unknown): string {
  if (typeof value !== 'string') return ''
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

function percent(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '无'
  return `${Math.round(value * 100)}%`
}

function formatNumber(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '无'
  return Math.abs(value) >= 1 ? value.toFixed(2) : value.toFixed(3)
}

function profileLabel(profile: reference.StyleProfileSummary): string {
  return `${profile.title || '未命名画像'} #${profile.profile_id}`
}

function selectedSourceAnchors(anchors: reference.Anchor[], selectedAnchorIds: number[]): reference.Anchor[] {
  const selected = new Set(selectedAnchorIds)
  return anchors.filter(anchor => selected.has(anchor.anchor_id))
}

function profileMatches(profile: reference.StyleProfileSummary, query: string): boolean {
  const needle = query.trim().toLowerCase()
  if (!needle) return true
  return [
    profile.title,
    profile.description,
    profile.status,
    profile.analyzer_source,
    profile.analyzer_version,
    ...(profile.source_anchor_ids ?? []).map(String),
    ...(profile.source_hashes ?? []),
  ].some(value => String(value ?? '').toLowerCase().includes(needle))
}

function featureEvidenceCount(feature: { evidence_ids: string[] }): number {
  return feature.evidence_ids?.length ?? 0
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}

export function StyleProfileLibraryPanel({
  novelId,
  anchors,
  selectedAnchorIds,
}: StyleProfileLibraryPanelProps) {
  const app = useApp()
  const [profiles, setProfiles] = useState<reference.StyleProfileSummary[]>([])
  const [activeProfile, setActiveProfile] = useState<reference.StyleProfile | null>(null)
  const [comparison, setComparison] = useState<reference.StyleProfileComparison | null>(null)
  const [form, setForm] = useState<StyleProfileForm>(EMPTY_STYLE_PROFILE_FORM)
  const [includeArchived, setIncludeArchived] = useState(false)
  const [query, setQuery] = useState('')
  const [leftCompareId, setLeftCompareId] = useState('')
  const [rightCompareId, setRightCompareId] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const selectedAnchors = useMemo(
    () => selectedSourceAnchors(anchors, selectedAnchorIds),
    [anchors, selectedAnchorIds],
  )
  const selectedAnchorIdList = useMemo(
    () => selectedAnchors.map(anchor => anchor.anchor_id),
    [selectedAnchors],
  )
  const filteredProfiles = useMemo(
    () => profiles.filter(profile => profileMatches(profile, query)),
    [profiles, query],
  )
  const activeProfiles = useMemo(
    () => profiles.filter(profile => profile.status !== 'archived'),
    [profiles],
  )
  const comparableProfiles = includeArchived ? profiles : activeProfiles
  const canBuildProfile = selectedAnchorIdList.length > 0 && form.title.trim().length > 0
  const canCompare = Number.parseInt(leftCompareId, 10) > 0 &&
    Number.parseInt(rightCompareId, 10) > 0 &&
    leftCompareId !== rightCompareId

  const fetchProfiles = useCallback(async () => {
    return await app.GetReferenceStyleProfiles({
      novel_id: novelId,
      include_archived: includeArchived,
    }) ?? []
  }, [app, novelId, includeArchived])

  function applyProfiles(nextProfiles: reference.StyleProfileSummary[]) {
    setProfiles(nextProfiles)
    setActiveProfile(current => {
      if (!current) return null
      return nextProfiles.some(profile => profile.profile_id === current.profile_id) ? current : null
    })
    setLeftCompareId(current => nextProfiles.some(profile => String(profile.profile_id) === current) ? current : '')
    setRightCompareId(current => nextProfiles.some(profile => String(profile.profile_id) === current) ? current : '')
  }

  const loadProfiles = useCallback(async () => {
    if (!novelId) {
      setProfiles([])
      setActiveProfile(null)
      setComparison(null)
      return
    }

    setLoading(true)
    setError(null)
    try {
      applyProfiles(await fetchProfiles())
    } catch (err) {
      setError(err instanceof Error ? err.message : '加载风格画像失败')
    } finally {
      setLoading(false)
    }
  }, [fetchProfiles, novelId])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      if (!novelId) {
        setProfiles([])
        setActiveProfile(null)
        setComparison(null)
        return
      }

      setLoading(true)
      setError(null)
      try {
        const nextProfiles = await fetchProfiles()
        if (!cancelled) applyProfiles(nextProfiles)
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : '加载风格画像失败')
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [fetchProfiles, novelId])

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

  async function buildProfile() {
    if (!canBuildProfile) {
      setError('请选择至少一个语料来源并填写画像标题')
      return
    }

    const built = await run(() => app.BuildReferenceStyleProfile({
      novel_id: novelId,
      title: form.title.trim(),
      description: form.description.trim(),
      anchor_ids: selectedAnchorIdList,
      allowed_license_statuses: lines(form.allowedLicenseStatuses),
      allowed_source_trust_levels: lines(form.allowedSourceTrustLevels),
    }), '风格画像已构建')
    if (built) {
      setForm(EMPTY_STYLE_PROFILE_FORM)
      setActiveProfile(built)
      setComparison(null)
      setLeftCompareId(String(built.profile_id))
      await loadProfiles()
    }
  }

  async function inspectProfile(profileId: number) {
    const detail = await run(() => app.GetReferenceStyleProfile(novelId, profileId))
    if (detail) {
      setActiveProfile(detail)
    }
  }

  async function archiveProfile(profileId: number) {
    const archived = await run(() => app.ArchiveReferenceStyleProfile({
      novel_id: novelId,
      profile_id: profileId,
    }), '风格画像已归档')
    if (archived) {
      setActiveProfile(archived)
      setComparison(null)
      await loadProfiles()
    }
  }

  async function restoreProfile(profileId: number) {
    const restored = await run(() => app.RestoreReferenceStyleProfile({
      novel_id: novelId,
      profile_id: profileId,
    }), '风格画像已恢复')
    if (restored) {
      setActiveProfile(restored)
      await loadProfiles()
    }
  }

  async function compareProfiles() {
    if (!canCompare) {
      setError('请选择两个不同的风格画像')
      return
    }

    const result = await run(() => app.CompareReferenceStyleProfiles({
      novel_id: novelId,
      left_profile_id: Number.parseInt(leftCompareId, 10),
      right_profile_id: Number.parseInt(rightCompareId, 10),
    }), '风格画像比较已生成')
    if (result) {
      setComparison(result)
    }
  }

  return (
    <div data-testid="reference-style-profile-library" className="rounded-lg border border-border bg-card p-4">
      <div className="mb-3 flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <BookOpenCheck className="h-3.5 w-3.5 text-muted-foreground" />
            <h3 className="text-xs font-semibold text-foreground">风格画像库</h3>
          </div>
          <p className="mt-1 text-xs leading-relaxed text-muted-foreground">
            从已选语料构建可审计风格画像，查看特征和证据，再按需要归档、恢复或比较差异。
          </p>
        </div>
        <button type="button" onClick={loadProfiles} disabled={loading} className={actionButtonClass}>
          {loading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCcw className="h-3.5 w-3.5" />}
          刷新画像
        </button>
      </div>

      {error && <div role="alert" className="mb-3 rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">{error}</div>}
      {message && <div role="status" className="mb-3 rounded-md border border-emerald-500/30 bg-emerald-500/10 px-3 py-2 text-xs text-emerald-700 dark:text-emerald-300">{message}</div>}

      <div className="space-y-3">
        <div className="rounded-md border border-border bg-background p-3">
          <div className="mb-2 flex items-center gap-2">
            <Wand2 className="h-3.5 w-3.5 text-muted-foreground" />
            <h4 className="text-xs font-semibold text-foreground">构建画像</h4>
          </div>
          <div className="mb-2 rounded border border-border bg-card px-2.5 py-2 text-[11px] leading-relaxed text-muted-foreground">
            已选 {selectedAnchors.length} 个来源
            {selectedAnchors.length > 0
              ? `：${selectedAnchors.map(anchor => anchor.title).join('、')}`
              : '。先在库条目中选择用于建模的来源。'}
          </div>
          <div className="grid grid-cols-1 gap-2">
            <Field label="画像标题">
              <input
                value={form.title}
                onChange={event => setForm(current => ({ ...current, title: event.target.value }))}
                className={inputClass}
                placeholder="雨夜克制风格"
                aria-label="风格画像标题"
              />
            </Field>
            <Field label="画像说明">
              <textarea
                value={form.description}
                onChange={event => setForm(current => ({ ...current, description: event.target.value }))}
                className={`${inputClass} min-h-14 resize-y`}
                placeholder="用于雨夜、克制动作和近距离 POV 的风格锚定"
                aria-label="风格画像说明"
              />
            </Field>
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
              <Field label="允许授权">
                <textarea
                  value={form.allowedLicenseStatuses}
                  onChange={event => setForm(current => ({ ...current, allowedLicenseStatuses: event.target.value }))}
                  className={`${inputClass} min-h-16 resize-y font-mono text-[11px]`}
                  aria-label="风格画像允许授权"
                />
              </Field>
              <Field label="允许可信度">
                <textarea
                  value={form.allowedSourceTrustLevels}
                  onChange={event => setForm(current => ({ ...current, allowedSourceTrustLevels: event.target.value }))}
                  className={`${inputClass} min-h-16 resize-y font-mono text-[11px]`}
                  aria-label="风格画像允许可信度"
                />
              </Field>
            </div>
            <button
              type="button"
              onClick={() => {
                void buildProfile()
              }}
              disabled={loading || !canBuildProfile}
              className="inline-flex items-center justify-center gap-1.5 rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50"
            >
              <Wand2 className="h-3.5 w-3.5" />
              构建风格画像
            </button>
          </div>
        </div>

        <div className="space-y-2">
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-[minmax(0,1fr)_auto]">
            <Field label="画像筛选">
              <input
                value={query}
                onChange={event => setQuery(event.target.value)}
                className={inputClass}
                placeholder="标题、状态、分析器或来源哈希"
                aria-label="风格画像筛选"
              />
            </Field>
            <label className="flex items-end gap-2 pb-1.5 text-xs text-muted-foreground">
              <input
                type="checkbox"
                checked={includeArchived}
                onChange={event => setIncludeArchived(event.target.checked)}
                className="shrink-0"
              />
              显示归档画像
            </label>
          </div>
          {filteredProfiles.length === 0 ? (
            <p className="rounded-md border border-dashed border-border bg-background px-3 py-3 text-xs text-muted-foreground">
              {query.trim() ? '没有匹配的风格画像' : '暂无风格画像'}
            </p>
          ) : (
            <div className="space-y-2">
              {filteredProfiles.map(profile => (
                <div key={profile.profile_id} data-testid="reference-style-profile-row" className="rounded-md border border-border bg-background px-3 py-2">
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <div className="min-w-0">
                      <p className="truncate text-xs font-semibold text-foreground">{profile.title || '未命名画像'}</p>
                      <p className="mt-0.5 text-[11px] leading-relaxed text-muted-foreground">
                        #{profile.profile_id} · {profile.analyzer_source || 'unknown'} · {profile.source_anchor_ids?.length ?? 0} 来源 · 置信 {percent(profile.aggregate_confidence)}
                      </p>
                      {profile.description && <p className="mt-1 line-clamp-2 text-xs leading-relaxed text-muted-foreground">{profile.description}</p>}
                    </div>
                    <span className={`text-[11px] ${statusTone(profile.status)}`}>{profile.status || 'unknown'}</span>
                  </div>
                  <div className="mt-2 flex flex-wrap items-center gap-1.5">
                    <button
                      type="button"
                      onClick={() => {
                        void inspectProfile(profile.profile_id)
                      }}
                      disabled={loading}
                      className={actionButtonClass}
                      aria-label={`查看风格画像 ${profile.title || profile.profile_id}`}
                    >
                      <Eye className="h-3.5 w-3.5" />
                      查看
                    </button>
                    {profile.status === 'archived' ? (
                      <button
                        type="button"
                        onClick={() => {
                          void restoreProfile(profile.profile_id)
                        }}
                        disabled={loading}
                        className={actionButtonClass}
                        aria-label={`恢复风格画像 ${profile.title || profile.profile_id}`}
                      >
                        <RotateCcw className="h-3.5 w-3.5" />
                        恢复
                      </button>
                    ) : (
                      <button
                        type="button"
                        onClick={() => {
                          void archiveProfile(profile.profile_id)
                        }}
                        disabled={loading}
                        className={actionButtonClass}
                        aria-label={`归档风格画像 ${profile.title || profile.profile_id}`}
                      >
                        <Archive className="h-3.5 w-3.5" />
                        归档
                      </button>
                    )}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="rounded-md border border-border bg-background p-3">
          <div className="mb-2 flex items-center gap-2">
            <GitCompare className="h-3.5 w-3.5 text-muted-foreground" />
            <h4 className="text-xs font-semibold text-foreground">画像比较</h4>
          </div>
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto]">
            <Field label="左侧画像">
              <select value={leftCompareId} onChange={event => setLeftCompareId(event.target.value)} className={inputClass} aria-label="左侧风格画像">
                <option value="">选择画像</option>
                {comparableProfiles.map(profile => (
                  <option key={profile.profile_id} value={profile.profile_id}>{profileLabel(profile)}</option>
                ))}
              </select>
            </Field>
            <Field label="右侧画像">
              <select value={rightCompareId} onChange={event => setRightCompareId(event.target.value)} className={inputClass} aria-label="右侧风格画像">
                <option value="">选择画像</option>
                {comparableProfiles.map(profile => (
                  <option key={profile.profile_id} value={profile.profile_id}>{profileLabel(profile)}</option>
                ))}
              </select>
            </Field>
            <button
              type="button"
              onClick={() => {
                void compareProfiles()
              }}
              disabled={loading || !canCompare}
              className="inline-flex items-center justify-center gap-1.5 self-end rounded bg-secondary px-3 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
            >
              <GitCompare className="h-3.5 w-3.5" />
              比较
            </button>
          </div>
          {comparison && (
            <div data-testid="reference-style-profile-comparison" className="mt-3 space-y-3">
              <p className="text-[11px] leading-relaxed text-muted-foreground">
                {comparison.left_profile.title || '左侧画像'} vs {comparison.right_profile.title || '右侧画像'} · {formatTime(comparison.compared_at)}
              </p>
              <DifferenceList
                title="数值差异"
                icon={<BarChart3 className="h-3.5 w-3.5" />}
                items={comparison.numeric_differences.slice(0, 8).map(item => `${item.feature_key}: ${formatNumber(item.left_value)} → ${formatNumber(item.right_value)}，差值 ${formatNumber(item.absolute_delta)}`)}
              />
              <DifferenceList
                title="分布差异"
                icon={<BarChart3 className="h-3.5 w-3.5" />}
                items={comparison.distribution_differences.slice(0, 4).map(item => `${item.feature_key}: ${item.buckets.slice(0, 3).map(bucket => `${bucket.label} ${formatNumber(bucket.left_weight)}→${formatNumber(bucket.right_weight)}`).join('；')}`)}
              />
              <DifferenceList
                title="类别差异"
                icon={<BarChart3 className="h-3.5 w-3.5" />}
                items={comparison.categorical_differences.slice(0, 8).map(item => `${item.feature_key}/${item.label}: ${formatNumber(item.left_weight)} → ${formatNumber(item.right_weight)}`)}
              />
            </div>
          )}
        </div>

        {activeProfile && (
          <div data-testid="reference-style-profile-detail" className="rounded-md border border-border bg-background p-3">
            <div className="mb-2 flex flex-wrap items-start justify-between gap-2">
              <div className="min-w-0">
                <h4 className="truncate text-xs font-semibold text-foreground">{activeProfile.title || '未命名画像'}</h4>
                <p className="mt-0.5 text-[11px] text-muted-foreground">
                  {activeProfile.analyzer_version} · {activeProfile.feature_schema_version} · 更新 {formatTime(activeProfile.updated_at)}
                </p>
              </div>
              <span className={`text-[11px] ${statusTone(activeProfile.status)}`}>{activeProfile.status}</span>
            </div>
            <div className="grid grid-cols-2 gap-2 text-[11px] text-muted-foreground">
              <div className="rounded border border-border bg-card px-2 py-1.5">来源 {activeProfile.source_anchor_ids?.join(', ') || '无'}</div>
              <div className="rounded border border-border bg-card px-2 py-1.5">证据 {activeProfile.evidence_spans?.length ?? 0}</div>
              <div className="rounded border border-border bg-card px-2 py-1.5">授权 {activeProfile.allowed_license_statuses?.join(', ') || '无'}</div>
              <div className="rounded border border-border bg-card px-2 py-1.5">可信度 {activeProfile.allowed_source_trust_levels?.join(', ') || '无'}</div>
            </div>
            <FeatureList title="数值特征" items={activeProfile.features.numeric_features.slice(0, 8).map(feature => `${feature.feature_key} ${formatNumber(feature.value)} ${feature.unit} · 证据 ${featureEvidenceCount(feature)}`)} />
            <FeatureList title="分布特征" items={activeProfile.features.distribution_features.slice(0, 4).map(feature => `${feature.feature_key}: ${feature.buckets.map(bucket => `${bucket.label} ${percent(bucket.weight)}`).join('；')}`)} />
            <FeatureList title="类别特征" items={activeProfile.features.categorical_features.slice(0, 10).map(feature => `${feature.feature_key}/${feature.label} · 权重 ${formatNumber(feature.weight)} · 证据 ${featureEvidenceCount(feature)}`)} />
            <div className="mt-3">
              <h5 className="text-[11px] font-semibold text-foreground">证据跨度</h5>
              {(activeProfile.evidence_spans?.length ?? 0) === 0 ? (
                <p className="mt-1 text-[11px] text-muted-foreground">暂无证据。</p>
              ) : (
                <div className="mt-2 max-h-44 space-y-1 overflow-y-auto pr-1">
                  {activeProfile.evidence_spans.slice(0, 12).map(evidence => (
                    <div key={evidence.evidence_id} className="rounded border border-border bg-card px-2 py-1.5 text-[11px] leading-relaxed text-muted-foreground">
                      {evidence.feature_key}/{evidence.label} · anchor {evidence.anchor_id} · {evidence.source_segment_id} · {evidence.start_offset}-{evidence.end_offset} · {evidence.analyzer_source}
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

function FeatureList({ title, items }: { title: string; items: string[] }) {
  return (
    <div className="mt-3">
      <h5 className="text-[11px] font-semibold text-foreground">{title}</h5>
      {items.length === 0 ? (
        <p className="mt-1 text-[11px] text-muted-foreground">暂无数据。</p>
      ) : (
        <div className="mt-2 space-y-1">
          {items.map(item => (
            <div key={item} className="rounded border border-border bg-card px-2 py-1.5 text-[11px] leading-relaxed text-muted-foreground">{item}</div>
          ))}
        </div>
      )}
    </div>
  )
}

function DifferenceList({ title, icon, items }: { title: string; icon: ReactNode; items: string[] }) {
  return (
    <div>
      <div className="mb-1 flex items-center gap-1.5 text-[11px] font-semibold text-foreground">
        {icon}
        {title}
      </div>
      {items.length === 0 ? (
        <p className="text-[11px] text-muted-foreground">暂无差异。</p>
      ) : (
        <div className="space-y-1">
          {items.map(item => (
            <div key={item} className="rounded border border-border bg-card px-2 py-1.5 text-[11px] leading-relaxed text-muted-foreground">{item}</div>
          ))}
        </div>
      )}
    </div>
  )
}
