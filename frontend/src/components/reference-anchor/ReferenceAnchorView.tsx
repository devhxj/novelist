import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import {
  Archive,
  BookMarked,
  Check,
  Clipboard,
  Edit3,
  FileSearch,
  FolderOpen,
  Loader2,
  Plus,
  RefreshCcw,
  Search,
  Share2,
  SlidersHorizontal,
  Tags,
  Trash2,
  Wand2,
  X,
} from 'lucide-react'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { useApp } from '@/hooks/useApp'
import { copyTextToClipboard } from '@/lib/clipboard'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics, reference } from '@/lib/novelist/types'
import { BlueprintDetail } from './BlueprintDetail'
import { CorpusAnalysisLibraryTab } from './CorpusAnalysisLibraryTab'
import { CorpusAnalysisJobsPanel } from './CorpusAnalysisJobsPanel'
import { CorpusGovernancePanel } from './CorpusGovernancePanel'
import { OrchestrationPanel } from './OrchestrationPanel'
import { StyleProfileLibraryPanel } from './StyleProfileLibraryPanel'
import {
  EMPTY_REVISION_FORM,
  addListChange,
  addSlotPlanChange,
  addStringChange,
  addStyleContractChange,
  formFromBlueprint,
  lines,
  styleContractFromForm,
} from './blueprintRevision'
import type { BlueprintRevisionForm, BlueprintRevisionStringKey } from './blueprintRevision'
import { inputClass, statusTone } from './referenceAnchorStyles'

interface Props {
  novelId: number
  refreshKey?: number
}

type AnchorForm = {
  title: string
  author: string
  licenseStatus: string
  visibility: string
  sourceTrust: string
  userTags: string
}

type CreateAnchorForm = AnchorForm & {
  sourcePath: string
  bulkSourcePaths: string
  libraryPackManifest: string
  sourceKind: string
}

type BlueprintForm = {
  chapterNumber: string
  title: string
  chapterGoal: string
  knownFacts: string
  forbiddenFacts: string
}

type MaterialSearchFilters = {
  materialTypes: string
  emotionTags: string
  functionTags: string
  povTags: string
  techniqueTags: string
  narrativeDuties: string
  emotionTransitions: string
  proseDuties: string
}

type AnchorScopeFilter = 'all' | 'novel' | 'workspace_corpus'
type MaterialPreviewSort = 'default' | 'score_desc' | 'material_id_asc'
type MaterialArchiveFilter = 'active' | 'archived'
type CorpusLibraryTab = 'materials' | 'analysis_results' | 'analysis_jobs' | 'governance' | 'sources' | 'tag_review' | 'style_profiles' | 'processing_records' | 'advanced'

type LocatedCorpusEvidence = {
 anchorId: number
 nodeId: string
 nodeType: string
 chapterIndex?: number | null
 startOffset: number
 endOffset: number
 text: string
}

type MaterialPreviewState = {
  items: reference.MaterialSummary[]
  page: number
  size: number
  total: number
  totalPages: number
}

type MaterialLibraryState = MaterialPreviewState

type MaterialTagReviewQueueState = {
  items: reference.MaterialTagReviewItem[]
  page: number
  size: number
  total: number
  totalPages: number
}

type MaterialTagForm = {
  functionTag: string
  emotionTag: string
  sceneTag: string
  povTag: string
  techniqueTag: string
}

type ReferenceErrorState =
  | string
  | {
    title: string
    message: string
    diagnostic: diagnostics.CopyableDiagnostic | null
  }

type ReferenceRunOptions = {
  fallbackMessage?: string
  operation?: string
  bridgeMethod?: string | null
  detail?: Record<string, unknown>
}

const EMPTY_MATERIAL_TAG_FORM: MaterialTagForm = {
  functionTag: '',
  emotionTag: '',
  sceneTag: '',
  povTag: '',
  techniqueTag: '',
}

const EMPTY_ANCHOR_FORM: CreateAnchorForm = {
  title: '',
  author: '',
  sourcePath: '',
  bulkSourcePaths: '',
  libraryPackManifest: '',
  sourceKind: 'markdown',
  licenseStatus: 'user_provided',
  visibility: 'private',
  sourceTrust: 'user_verified',
  userTags: '',
}

const EMPTY_BLUEPRINT_FORM: BlueprintForm = {
  chapterNumber: '1',
  title: '',
  chapterGoal: '',
  knownFacts: '',
  forbiddenFacts: '',
}

const EMPTY_MATERIAL_FILTERS: MaterialSearchFilters = {
  materialTypes: '',
  emotionTags: '',
  functionTags: '',
  povTags: '',
  techniqueTags: '',
  narrativeDuties: '',
  emotionTransitions: '',
  proseDuties: '',
}

const EMPTY_MATERIAL_PREVIEW: MaterialPreviewState = {
  items: [],
  page: 1,
  size: 5,
  total: 0,
  totalPages: 1,
}

const EMPTY_MATERIAL_LIBRARY: MaterialLibraryState = {
  items: [],
  page: 1,
  size: 10,
  total: 0,
  totalPages: 1,
}

const EMPTY_MATERIAL_TAG_REVIEW_QUEUE: MaterialTagReviewQueueState = {
  items: [],
  page: 1,
  size: 10,
  total: 0,
  totalPages: 1,
}

const DEFAULT_ORCHESTRATION_STYLE_DIMENSIONS = 'dialogue_ratio\nsensory_ratio'
const DEFAULT_ORCHESTRATION_STYLE_EVIDENCE = 'dialogue_exchange'
const DEFAULT_ORCHESTRATION_STYLE_RISKS = 'source_leak\nstyle_distance'
const ENABLE_REFERENCE_ACTIVITY_CHAPTER_DEBUG =
  import.meta.env.DEV && import.meta.env.VITE_REFERENCE_ACTIVITY_CHAPTER_DEBUG === 'true'
const CORPUS_LIBRARY_TABS: Array<{ id: CorpusLibraryTab; label: string }> = [
  { id: 'materials', label: '处理后语料' },
{ id: 'analysis_results', label: '分析结果' },
 { id: 'analysis_jobs', label: '后台任务' },
 { id: 'governance', label: '治理与复核' },
  { id: 'sources', label: '素材来源' },
  { id: 'tag_review', label: '标签校正' },
  { id: 'style_profiles', label: '风格画像' },
  { id: 'processing_records', label: '处理记录' },
  { id: 'advanced', label: '高级' },
]
const ORCHESTRATION_STYLE_MIN_FIT: Record<reference.StyleImitationIntensity, string> = {
  diagnostic_only: '0',
  loose: '0.35',
  moderate: '0.65',
  strong: '0.8',
}

function tagFormFromMaterial(material: reference.MaterialSummary): MaterialTagForm {
  return {
    functionTag: material.function_tag,
    emotionTag: material.emotion_tag,
    sceneTag: material.scene_tag,
    povTag: material.pov_tag,
    techniqueTag: material.technique_tag,
  }
}

function sourceKindFromPath(path: string, fallback: string): string {
  const lowerPath = path.toLowerCase()
  if (lowerPath.endsWith('.txt')) return 'text'
  if (lowerPath.endsWith('.md') || lowerPath.endsWith('.markdown')) return 'markdown'
  return fallback
}

function sourceTitleFromPath(sourcePath: string, index: number): string {
  const normalizedPath = sourcePath.trim().replace(/[\\/]+$/, '')
  const fileName = normalizedPath.split(/[\\/]/).pop()?.trim() || `参考 ${index + 1}`
  const title = fileName.replace(/\.[^.]+$/, '').trim()
  return title || `参考 ${index + 1}`
}

function bulkAnchorTitle(formTitle: string, sourcePath: string, index: number, count: number): string {
  const title = formTitle.trim()
  if (!title) return sourceTitleFromPath(sourcePath, index)
  return count === 1 ? title : `${title} ${index + 1}`
}

function formatCreateAnchorFailure(failure: reference.CreateAnchorFailure): string {
  const title = failure.title.trim() || `第 ${failure.index + 1} 项`
  const sourceKind = failure.source_kind.trim() || 'unknown'
  const sourceIdentity = failure.source_identity.trim() || 'source:unknown'
  const diagnostic = failure.diagnostic.trim() || '导入失败'
  return `第 ${failure.index + 1} 项「${title}」 · ${sourceKind} · ${sourceIdentity} · ${diagnostic}`
}

function isFailedAnchorStatus(status: string): boolean {
  return status.startsWith('failed_') || status === 'cancelled'
}

function createCorpusAnalysisRunId(anchorId: number): string {
  const suffix = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`
  return `analysis:feature:${anchorId}:${suffix}`
}

function optionalManifestText(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value.trim() : undefined
}

function optionalManifestList(value: unknown): string[] | undefined {
  if (Array.isArray(value)) {
    const items = value
      .map(item => typeof item === 'string' ? item.trim() : '')
      .filter(Boolean)
    return items.length > 0 ? items : undefined
  }

  if (typeof value === 'string') {
    const items = lines(value)
    return items.length > 0 ? items : undefined
  }

  return undefined
}

function parseLibraryPackManifest(
  manifest: string,
  form: CreateAnchorForm,
  novelId: number,
): reference.CreateAnchorInput[] {
  let parsed: unknown
  try {
    parsed = JSON.parse(manifest)
  } catch {
    throw new Error('库包清单必须是 JSON')
  }

  const sources = Array.isArray(parsed)
    ? parsed
    : parsed && typeof parsed === 'object' && Array.isArray((parsed as { sources?: unknown }).sources)
      ? (parsed as { sources: unknown[] }).sources
      : null

  if (!sources || sources.length === 0) {
    throw new Error('库包清单至少需要 1 个 sources 条目')
  }

  if (sources.length > 50) {
    throw new Error('一次最多导入 50 个库包来源')
  }

  const fallbackTags = lines(form.userTags)
  return sources.map((item, index) => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) {
      throw new Error(`库包第 ${index + 1} 项必须是对象`)
    }

    const entry = item as Record<string, unknown>
    const sourcePath = optionalManifestText(entry.source_path) ??
      optionalManifestText(entry.sourcePath) ??
      optionalManifestText(entry.path)
    if (!sourcePath) {
      throw new Error(`库包第 ${index + 1} 项缺少 source_path`)
    }

    const title = optionalManifestText(entry.title) ?? bulkAnchorTitle(form.title, sourcePath, index, sources.length)
    const author = optionalManifestText(entry.author) ?? (form.author.trim() || undefined)
    const sourceKind = optionalManifestText(entry.source_kind) ??
      optionalManifestText(entry.sourceKind) ??
      sourceKindFromPath(sourcePath, form.sourceKind)
    const licenseStatus = optionalManifestText(entry.license_status) ??
      optionalManifestText(entry.licenseStatus) ??
      form.licenseStatus
    const visibility = optionalManifestText(entry.visibility) ?? form.visibility
    const sourceTrust = optionalManifestText(entry.source_trust) ??
      optionalManifestText(entry.sourceTrust) ??
      form.sourceTrust
    const userTags = optionalManifestList(entry.user_tags) ??
      optionalManifestList(entry.userTags) ??
      fallbackTags

    return {
      novel_id: novelId,
      title,
      author,
      source_path: sourcePath,
      source_kind: sourceKind,
      license_status: licenseStatus,
      visibility,
      source_trust: sourceTrust,
      user_tags: userTags,
    }
  })
}

function scoreComponentEntries(scoreComponents?: Record<string, number> | null): Array<[string, number]> {
  return Object.entries(scoreComponents ?? {})
    .filter(([, value]) => Number.isFinite(value) && value > 0)
    .sort(([, left], [, right]) => right - left)
}

function materialScoreComponents(material: reference.MaterialSummary): Array<[string, number]> {
  return scoreComponentEntries(material.score_components)
}

function materialBestScore(material: reference.MaterialSummary): number {
  return materialScoreComponents(material)[0]?.[1] ?? 0
}

function formFromAnchor(anchor: reference.Anchor): AnchorForm {
  return {
    title: anchor.title,
    author: anchor.author,
    licenseStatus: anchor.license_status,
    visibility: anchor.visibility,
    sourceTrust: anchor.source_trust,
    userTags: anchor.user_tags.join(';'),
  }
}

function normalized(value: string | null | undefined): string {
  return (value ?? '').trim().toLowerCase()
}

function anchorMatchesQuery(anchor: reference.Anchor, query: string): boolean {
  const needle = normalized(query)
  if (!needle) return true

  return [
    String(anchor.anchor_id),
    anchor.title,
    anchor.author,
    anchor.source_kind,
    anchor.license_status,
    anchor.visibility,
    anchor.source_trust,
    anchor.owner_scope,
    ...anchor.user_tags,
  ].some(value => normalized(value).includes(needle))
}

function materialMatchesQuery(material: reference.MaterialSummary, query: string): boolean {
  const needle = normalized(query)
  if (!needle) return true

  return [
    material.material_id,
    material.source_segment_id,
    material.material_type,
    material.function_tag,
    material.emotion_tag,
    material.scene_tag,
    material.pov_tag,
    material.technique_tag,
    material.text_preview,
  ].some(value => normalized(value).includes(needle))
}

const MATERIAL_LIST_PREVIEW_LIMIT = 160
function boundedMaterialPreview(text: string, limit = MATERIAL_LIST_PREVIEW_LIMIT): { text: string; truncated: boolean } {
  const normalizedText = text.trim().replace(/\s+/g, ' ')
  if (normalizedText.length <= limit) {
    return { text: normalizedText, truncated: false }
  }

  return {
    text: `${normalizedText.slice(0, limit).trimEnd()}...`,
    truncated: true,
  }
}

function MaterialListPreview({ text, truncated }: { text: string; truncated: boolean }) {
  const preview = boundedMaterialPreview(text)
  const isTruncated = truncated || preview.truncated

  return (
    <>
      <p className="mt-1 line-clamp-3 text-xs leading-relaxed text-foreground">{preview.text || '无预览'}</p>
      {isTruncated && (
        <p className="mt-1 text-[11px] leading-relaxed text-muted-foreground">预览已截断，不显示全文</p>
      )}
    </>
  )
}

export default function ReferenceAnchorView({ novelId, refreshKey = 0 }: Props) {
  const app = useApp()

  const [anchors, setAnchors] = useState<reference.Anchor[]>([])
  const [selectedAnchorIds, setSelectedAnchorIds] = useState<number[]>([])
  const [materials, setMaterials] = useState<reference.MaterialSummary[]>([])
  const [blueprints, setBlueprints] = useState<reference.ChapterBlueprintSummary[]>([])
  const [activeBlueprint, setActiveBlueprint] = useState<reference.ChapterBlueprint | null>(null)
  const [orchestrationRuns, setOrchestrationRuns] = useState<reference.OrchestrationRun[]>([])
  const [activeOrchestrationRun, setActiveOrchestrationRun] = useState<reference.OrchestrationRun | null>(null)
  const [orchestrationEvents, setOrchestrationEvents] = useState<reference.OrchestrationRunEvent[]>([])
  const [styleProfiles, setStyleProfiles] = useState<reference.StyleProfileSummary[]>([])
  const [binding, setBinding] = useState<reference.BlueprintMaterialBindingResult | null>(null)
  const [draft, setDraft] = useState<reference.AnchoredDraft | null>(null)
  const [anchorForm, setAnchorForm] = useState<CreateAnchorForm>(EMPTY_ANCHOR_FORM)
  const [editingAnchorId, setEditingAnchorId] = useState<number | null>(null)
  const [anchorEditForm, setAnchorEditForm] = useState<AnchorForm | null>(null)
  const [expandedAnchorMaterialId, setExpandedAnchorMaterialId] = useState<number | null>(null)
  const [anchorMaterialPreview, setAnchorMaterialPreview] = useState<MaterialPreviewState>(EMPTY_MATERIAL_PREVIEW)
  const [anchorMaterialQuery, setAnchorMaterialQuery] = useState('')
  const [anchorMaterialSort, setAnchorMaterialSort] = useState<MaterialPreviewSort>('default')
  const [editingMaterialId, setEditingMaterialId] = useState<string | null>(null)
  const [materialTagForm, setMaterialTagForm] = useState<MaterialTagForm | null>(null)
  const [selectedMaterialIds, setSelectedMaterialIds] = useState<string[]>([])
  const [bulkMaterialTagForm, setBulkMaterialTagForm] = useState<MaterialTagForm>(EMPTY_MATERIAL_TAG_FORM)
  const [materialLibraryQuery, setMaterialLibraryQuery] = useState('')
  const [materialLibraryFilters, setMaterialLibraryFilters] = useState<MaterialSearchFilters>(EMPTY_MATERIAL_FILTERS)
  const [materialLibrary, setMaterialLibrary] = useState<MaterialLibraryState>(EMPTY_MATERIAL_LIBRARY)
  const [materialLibraryPageQuery, setMaterialLibraryPageQuery] = useState('')
  const [materialLibrarySort, setMaterialLibrarySort] = useState<MaterialPreviewSort>('default')
  const [materialLibraryArchiveFilter, setMaterialLibraryArchiveFilter] = useState<MaterialArchiveFilter>('active')
  const [materialTagReviewQueue, setMaterialTagReviewQueue] = useState<MaterialTagReviewQueueState>(EMPTY_MATERIAL_TAG_REVIEW_QUEUE)
  const [selectedLibraryMaterialIds, setSelectedLibraryMaterialIds] = useState<string[]>([])
  const [bulkLibraryMaterialTagForm, setBulkLibraryMaterialTagForm] = useState<MaterialTagForm>(EMPTY_MATERIAL_TAG_FORM)
  const [materialDetailId, setMaterialDetailId] = useState<string | null>(null)
  const [materialDetail, setMaterialDetail] = useState<reference.MaterialDetail | null>(null)
  const [materialDetailLoading, setMaterialDetailLoading] = useState(false)
  const [materialDetailError, setMaterialDetailError] = useState<Exclude<ReferenceErrorState, string> | null>(null)
  const materialDetailRequestRef = useRef(0)
  const [sourceProcessingAnchorId, setSourceProcessingAnchorId] = useState<number | null>(null)
  const [sourceProcessingDetail, setSourceProcessingDetail] = useState<reference.SourceProcessingDetail | null>(null)
  const [sourceProcessingLoading, setSourceProcessingLoading] = useState(false)
  const [sourceProcessingError, setSourceProcessingError] = useState<Exclude<ReferenceErrorState, string> | null>(null)
  const [sourceSegmentDetailKey, setSourceSegmentDetailKey] = useState<{ anchorId: number; segmentId: string } | null>(null)
  const [sourceSegmentDetail, setSourceSegmentDetail] = useState<reference.SourceSegmentDetail | null>(null)
  const [sourceSegmentDetailLoading, setSourceSegmentDetailLoading] = useState(false)
  const [sourceSegmentDetailError, setSourceSegmentDetailError] = useState<Exclude<ReferenceErrorState, string> | null>(null)
  const sourceSegmentDetailRequestRef = useRef(0)
  const [blueprintForm, setBlueprintForm] = useState<BlueprintForm>(EMPTY_BLUEPRINT_FORM)
  const [revisionForm, setRevisionForm] = useState<BlueprintRevisionForm>(EMPTY_REVISION_FORM)
  const [materialFilters, setMaterialFilters] = useState<MaterialSearchFilters>(EMPTY_MATERIAL_FILTERS)
  const [materialQuery, setMaterialQuery] = useState('')
  const [orchestrationUseSelectedAnchors, setOrchestrationUseSelectedAnchors] = useState(false)
  const [orchestrationStyleProfileId, setOrchestrationStyleProfileId] = useState('')
  const [orchestrationStyleOptedOut, setOrchestrationStyleOptedOut] = useState(false)
  const [orchestrationStyleIntensity, setOrchestrationStyleIntensity] = useState<reference.StyleImitationIntensity>('strong')
  const [orchestrationStyleMinFit, setOrchestrationStyleMinFit] = useState(ORCHESTRATION_STYLE_MIN_FIT.strong)
  const [orchestrationStyleAllowedCloseness, setOrchestrationStyleAllowedCloseness] = useState('moderate')
  const [orchestrationStyleDimensions, setOrchestrationStyleDimensions] = useState(DEFAULT_ORCHESTRATION_STYLE_DIMENSIONS)
  const [orchestrationStyleRequiredEvidenceTypes, setOrchestrationStyleRequiredEvidenceTypes] = useState(DEFAULT_ORCHESTRATION_STYLE_EVIDENCE)
  const [orchestrationStyleForbiddenRisks, setOrchestrationStyleForbiddenRisks] = useState(DEFAULT_ORCHESTRATION_STYLE_RISKS)
  const [advancedMode, setAdvancedMode] = useState(false)
const [activeCorpusTab, setActiveCorpusTab] = useState<CorpusLibraryTab>('sources')
const [focusedEvidenceAnchorId, setFocusedEvidenceAnchorId] = useState<number | null>(null)
 const [locatedCorpusEvidence, setLocatedCorpusEvidence] = useState<LocatedCorpusEvidence | null>(null)
 const [locatedCorpusEvidenceLoading, setLocatedCorpusEvidenceLoading] = useState(false)
 const [locatedCorpusEvidenceError, setLocatedCorpusEvidenceError] = useState<string | null>(null)
  const [anchorScopeFilter, setAnchorScopeFilter] = useState<AnchorScopeFilter>('all')
  const [anchorQuery, setAnchorQuery] = useState('')
  const [anchorLicenseFilter, setAnchorLicenseFilter] = useState('all')
  const [anchorVisibilityFilter, setAnchorVisibilityFilter] = useState('all')
  const [anchorSourceTrustFilter, setAnchorSourceTrustFilter] = useState('all')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<ReferenceErrorState | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const selectAdjacentCorpusTab = useCallback((tabId: CorpusLibraryTab, offset: number) => {
    const currentIndex = CORPUS_LIBRARY_TABS.findIndex(tab => tab.id === tabId)
    if (currentIndex < 0) return
    const nextTab = CORPUS_LIBRARY_TABS[(currentIndex + offset + CORPUS_LIBRARY_TABS.length) % CORPUS_LIBRARY_TABS.length]
    setActiveCorpusTab(nextTab.id)
    window.requestAnimationFrame(() => {
      document.getElementById(`corpus-tab-${nextTab.id}`)?.focus()
    })
  }, [])

  const selectedAnchorSet = useMemo(() => new Set(selectedAnchorIds), [selectedAnchorIds])
  const anchorScopeCounts = useMemo(() => ({
    all: anchors.length,
    novel: anchors.filter(anchor => anchor.owner_scope === 'novel').length,
    workspace_corpus: anchors.filter(anchor => anchor.owner_scope === 'workspace_corpus').length,
  }), [anchors])
  const visibleAnchors = useMemo(() => {
    return anchors.filter(anchor => {
      if (anchorScopeFilter !== 'all' && anchor.owner_scope !== anchorScopeFilter) return false
      if (anchorLicenseFilter !== 'all' && anchor.license_status !== anchorLicenseFilter) return false
      if (anchorVisibilityFilter !== 'all' && anchor.visibility !== anchorVisibilityFilter) return false
      if (anchorSourceTrustFilter !== 'all' && anchor.source_trust !== anchorSourceTrustFilter) return false
      return anchorMatchesQuery(anchor, anchorQuery)
    })
  }, [anchors, anchorScopeFilter, anchorLicenseFilter, anchorVisibilityFilter, anchorSourceTrustFilter, anchorQuery])
  const hasAnchorListFilters = anchorQuery.trim().length > 0
    || anchorLicenseFilter !== 'all'
    || anchorVisibilityFilter !== 'all'
    || anchorSourceTrustFilter !== 'all'
  const canClearAnchorFilters = hasAnchorListFilters || anchorScopeFilter !== 'all'
  const selectedAnchors = useMemo(() => anchors.filter(anchor => selectedAnchorSet.has(anchor.anchor_id)), [anchors, selectedAnchorSet])
  const selectedVisibleAnchorIds = useMemo(() => visibleAnchors.map(anchor => anchor.anchor_id), [visibleAnchors])
  const selectedNovelAnchors = useMemo(() => selectedAnchors.filter(anchor => anchor.owner_scope === 'novel'), [selectedAnchors])
  const selectedWorkspaceAnchors = useMemo(() => selectedAnchors.filter(anchor => anchor.owner_scope === 'workspace_corpus'), [selectedAnchors])
  const activeStyleProfiles = useMemo(() => styleProfiles.filter(profile => profile.status === 'active'), [styleProfiles])
  const orchestrationEffectiveStyleProfileId = useMemo(() => {
    if (orchestrationStyleProfileId && activeStyleProfiles.some(profile => String(profile.profile_id) === orchestrationStyleProfileId)) {
      return orchestrationStyleProfileId
    }

    if (!orchestrationStyleOptedOut && activeStyleProfiles.length > 0) {
      return String(activeStyleProfiles[0].profile_id)
    }

    return ''
  }, [activeStyleProfiles, orchestrationStyleOptedOut, orchestrationStyleProfileId])
  const selectedMaterialSet = useMemo(() => new Set(selectedMaterialIds), [selectedMaterialIds])
  const visibleAnchorMaterialItems = useMemo(() => {
    const indexedItems = anchorMaterialPreview.items
      .map((material, index) => ({ material, index }))
      .filter(({ material }) => materialMatchesQuery(material, anchorMaterialQuery))

    if (anchorMaterialSort === 'score_desc') {
      indexedItems.sort((left, right) => {
        const scoreDelta = materialBestScore(right.material) - materialBestScore(left.material)
        if (scoreDelta !== 0) return scoreDelta
        return left.index - right.index
      })
    } else if (anchorMaterialSort === 'material_id_asc') {
      indexedItems.sort((left, right) => left.material.material_id.localeCompare(right.material.material_id))
    }

    return indexedItems.map(({ material }) => material)
  }, [anchorMaterialPreview.items, anchorMaterialQuery, anchorMaterialSort])
  const visibleAnchorMaterialIds = useMemo(
    () => visibleAnchorMaterialItems.map(material => material.material_id),
    [visibleAnchorMaterialItems],
  )
  const selectedVisibleMaterialCount = useMemo(
    () => visibleAnchorMaterialIds.filter(id => selectedMaterialSet.has(id)).length,
    [visibleAnchorMaterialIds, selectedMaterialSet],
  )
  const hasBulkMaterialTagOverride = useMemo(
    () => Object.values(bulkMaterialTagForm).some(value => value.trim().length > 0),
    [bulkMaterialTagForm],
  )
  const hasAnchorMaterialQuery = anchorMaterialQuery.trim().length > 0
  const selectedLibraryMaterialSet = useMemo(() => new Set(selectedLibraryMaterialIds), [selectedLibraryMaterialIds])
  const visibleMaterialLibraryItems = useMemo(() => {
    const indexedItems = materialLibrary.items
      .map((material, index) => ({ material, index }))
      .filter(({ material }) => materialMatchesQuery(material, materialLibraryPageQuery))

    if (materialLibrarySort === 'score_desc') {
      indexedItems.sort((left, right) => {
        const scoreDelta = materialBestScore(right.material) - materialBestScore(left.material)
        if (scoreDelta !== 0) return scoreDelta
        return left.index - right.index
      })
    } else if (materialLibrarySort === 'material_id_asc') {
      indexedItems.sort((left, right) => left.material.material_id.localeCompare(right.material.material_id))
    }

    return indexedItems.map(({ material }) => material)
  }, [materialLibrary.items, materialLibraryPageQuery, materialLibrarySort])
  const visibleMaterialLibraryIds = useMemo(
    () => visibleMaterialLibraryItems.map(material => material.material_id),
    [visibleMaterialLibraryItems],
  )
  const tagReviewQueueIds = useMemo(
    () => materialTagReviewQueue.items.map(item => item.material.material_id),
    [materialTagReviewQueue.items],
  )
  const selectedTagReviewQueueCount = useMemo(
    () => tagReviewQueueIds.filter(id => selectedLibraryMaterialSet.has(id)).length,
    [tagReviewQueueIds, selectedLibraryMaterialSet],
  )
  const selectedVisibleLibraryMaterialCount = useMemo(
    () => visibleMaterialLibraryIds.filter(id => selectedLibraryMaterialSet.has(id)).length,
    [visibleMaterialLibraryIds, selectedLibraryMaterialSet],
  )
  const hasBulkLibraryMaterialTagOverride = useMemo(
    () => Object.values(bulkLibraryMaterialTagForm).some(value => value.trim().length > 0),
    [bulkLibraryMaterialTagForm],
  )
  const hasMaterialLibraryPageQuery = materialLibraryPageQuery.trim().length > 0

  const referenceError = useCallback((
    errorValue: unknown,
    {
      fallbackMessage = '操作失败',
      operation = 'ReferenceAnchorOperation',
      bridgeMethod = null,
      detail = {},
    }: ReferenceRunOptions = {},
  ): Exclude<ReferenceErrorState, string> => {
    return {
      title: fallbackMessage,
      message: diagnosticMessage(errorValue, fallbackMessage),
      diagnostic: buildCopyableDiagnostic({
        error: errorValue,
        fallbackMessage,
        operation,
        bridgeMethod,
        detail: {
          novel_id: novelId,
          ...detail,
        },
      }),
    }
  }, [novelId])

  const loadMaterialTagReviewQueue = useCallback(async (page = 1): Promise<boolean> => {
    const size = EMPTY_MATERIAL_TAG_REVIEW_QUEUE.size
    if (!novelId || materialLibraryArchiveFilter === 'archived') {
      setMaterialTagReviewQueue({
        ...EMPTY_MATERIAL_TAG_REVIEW_QUEUE,
        page: Math.max(1, page),
        size,
      })
      return true
    }

    setLoading(true)
    setError(null)
    try {
      const result = await app.GetReferenceMaterialTagReviewQueue({
        novel_id: novelId,
        anchor_ids: [],
        page,
        size,
        archive_filter: materialLibraryArchiveFilter,
      })
      setMaterialTagReviewQueue({
        items: result.items ?? [],
        page: Math.max(1, result.page),
        size: result.size,
        total: Math.max(0, result.total),
        totalPages: Math.max(1, result.total_pages),
      })
      return true
    } catch (err) {
      setError(referenceError(err, {
        fallbackMessage: '标签校正队列加载失败',
        operation: 'GetReferenceMaterialTagReviewQueue',
        bridgeMethod: 'GetReferenceMaterialTagReviewQueue',
        detail: {
          page,
          size,
          archive_filter: materialLibraryArchiveFilter,
          scope: 'material_tag_review_queue',
        },
      }))
      return false
    } finally {
      setLoading(false)
    }
  }, [app, materialLibraryArchiveFilter, novelId, referenceError])

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
      return current.filter(id => valid.has(id))
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

  const loadOrchestrationRuns = useCallback(async () => {
    if (!novelId) {
      setOrchestrationRuns([])
      setActiveOrchestrationRun(null)
      return
    }

    const list = await app.GetReferenceOrchestrationRuns(novelId, null)
    const runs = list ?? []
    setOrchestrationRuns(runs)
    setActiveOrchestrationRun(current => {
      if (!current) return runs[0] ?? null
      return runs.find(item => item.run_id === current.run_id) ?? runs[0] ?? null
    })
  }, [app, novelId])

  const loadStyleProfiles = useCallback(async () => {
    if (!novelId) {
      setStyleProfiles([])
      return
    }

    const list = await app.GetReferenceStyleProfiles({
      novel_id: novelId,
      include_archived: false,
    })
    setStyleProfiles(list ?? [])
  }, [app, novelId])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      setLoading(true)
      try {
        await Promise.all([loadAnchors(), loadStyleProfiles()])
      } catch (err) {
        if (!cancelled) {
          setError(referenceError(err, {
            fallbackMessage: '参考锚定数据加载失败',
            operation: 'LoadReferenceAnchorSurface',
            bridgeMethod: null,
            detail: { phase: 'initial_load' },
          }))
        }
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [loadAnchors, loadStyleProfiles, referenceError, refreshKey])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      if (!novelId || !activeOrchestrationRun) {
        setOrchestrationEvents([])
        return
      }

      try {
        const list = await app.GetReferenceOrchestrationRunEvents(novelId, activeOrchestrationRun.run_id)
        if (!cancelled) setOrchestrationEvents(list ?? [])
      } catch (err) {
        if (!cancelled) {
          setError(referenceError(err, {
            fallbackMessage: '加载编排事件失败',
            operation: 'GetReferenceOrchestrationRunEvents',
            bridgeMethod: 'GetReferenceOrchestrationRunEvents',
            detail: { run_id: activeOrchestrationRun.run_id },
          }))
        }
      }
    })()
    return () => { cancelled = true }
  }, [app, novelId, activeOrchestrationRun, referenceError])

useEffect(() => {
if (activeCorpusTab !== 'tag_review') return
    const timeoutId = window.setTimeout(() => {
      void loadMaterialTagReviewQueue(1)
    }, 0)
    return () => window.clearTimeout(timeoutId)
}, [activeCorpusTab, loadMaterialTagReviewQueue])

 useEffect(() => {
 if (!locatedCorpusEvidence) return
 const evidence = document.querySelector<HTMLElement>('[data-corpus-evidence-selection]')
 if (!evidence) return
 evidence.scrollIntoView({ behavior: 'smooth', block: 'center' })
 const range = document.createRange()
 range.selectNodeContents(evidence)
 const selection = window.getSelection()
 selection?.removeAllRanges()
 selection?.addRange(range)
 }, [locatedCorpusEvidence])

 useEffect(() => {
 const locateEvidence = async (event: Event) => {
 const detail = (event as CustomEvent<{ anchorId?: number; nodeId?: string; evidenceStart?: number | null; evidenceEnd?: number | null }>).detail
 if (!detail?.anchorId || !detail.nodeId) return
const anchor = anchors.find(item => item.anchor_id === detail.anchorId)
if (!anchor) return
 setActiveCorpusTab('sources')
 setAnchorScopeFilter('all')
 setAnchorQuery('')
 setAnchorLicenseFilter('all')
setExpandedAnchorMaterialId(anchor.anchor_id)
setFocusedEvidenceAnchorId(anchor.anchor_id)
 setLocatedCorpusEvidence(null)
 setLocatedCorpusEvidenceError(null)
 setLocatedCorpusEvidenceLoading(true)
 try {
 const nodeWindow = await app.GetReferenceCorpusNodeWindow({
 anchor_id: anchor.anchor_id, node_id: detail.nodeId,
 previous_chapter_count: 0, next_chapter_count: 0, include_scene_siblings: false, max_nodes: 20,
 })
 const node = nodeWindow?.chapter_nodes.find(item => item.node_id === detail.nodeId)
 ?? nodeWindow?.scene_siblings.find(item => item.node_id === detail.nodeId)
 if (!node) throw new Error('未找到复核证据对应的原文节点')
 const rawStart = detail.evidenceStart ?? 0
 const rawEnd = detail.evidenceEnd ?? rawStart
 const relativeStart = Math.max(0, Math.min(node.text.length, rawStart))
 const boundedEnd = Math.max(relativeStart, Math.min(node.text.length, rawEnd))
 const relativeEnd = boundedEnd > relativeStart ? boundedEnd : node.text.length
 setLocatedCorpusEvidence({
 anchorId: anchor.anchor_id, nodeId: node.node_id, nodeType: node.node_type,
 chapterIndex: node.chapter_index, startOffset: relativeStart, endOffset: relativeEnd, text: node.text,
 })
 } catch (caught) {
 setLocatedCorpusEvidenceError(caught instanceof Error ? caught.message : '复核证据原文加载失败')
 } finally {
 setLocatedCorpusEvidenceLoading(false)
 }
window.setTimeout(() => setFocusedEvidenceAnchorId(current => current === anchor.anchor_id ? null : current), 2400)
}
 window.addEventListener('novelist:locate-corpus-evidence', locateEvidence)
 return () => window.removeEventListener('novelist:locate-corpus-evidence', locateEvidence)
 }, [app, anchors])

  async function run<T>(
    task: () => Promise<T>,
    success?: string,
    options?: ReferenceRunOptions,
  ): Promise<T | null> {
    setLoading(true)
    setError(null)
    setMessage(null)
    try {
      const result = await task()
      if (success) setMessage(success)
      return result
    } catch (err) {
      setError(referenceError(err, options))
      return null
    } finally {
      setLoading(false)
    }
  }

  function handleCreateAnchorsResult(
    result: reference.CreateAnchorsResult,
    successPrefix: string,
    failureTitle: string,
    options: ReferenceRunOptions,
  ): boolean {
    if (result.succeeded_count > 0) {
      setMessage(`${successPrefix} ${result.succeeded_count}/${result.total_count} 个语料来源`)
    }

    if (result.failed_count === 0) {
      setError(null)
      return true
    }

    const preview = result.failed.slice(0, 5).map(formatCreateAnchorFailure).join('；')
    const suffix = result.failed.length > 5 ? `；另有 ${result.failed.length - 5} 项失败，可复制诊断查看` : ''
    setError({
      title: result.succeeded_count > 0 ? '部分语料导入失败' : failureTitle,
      message: `${preview || '语料来源导入失败'}${suffix}`,
      diagnostic: buildCopyableDiagnostic({
        fallbackMessage: failureTitle,
        operation: options.operation ?? 'CreateReferenceAnchorsWithResult',
        bridgeMethod: options.bridgeMethod ?? 'CreateReferenceAnchorsWithResult',
        detail: {
          ...(options.detail ?? {}),
          total_count: result.total_count,
          succeeded_count: result.succeeded_count,
          failed_count: result.failed_count,
          failed: result.failed,
        },
      }),
    })
    return false
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
      visibility: anchorForm.visibility,
      source_trust: anchorForm.sourceTrust,
      user_tags: lines(anchorForm.userTags),
    }), '参考锚点已创建', {
      fallbackMessage: '参考锚点创建失败',
      operation: 'CreateReferenceAnchor',
      bridgeMethod: 'CreateReferenceAnchor',
      detail: {
        title: anchorForm.title.trim(),
        source_kind: anchorForm.sourceKind,
        visibility: anchorForm.visibility,
        source_trust: anchorForm.sourceTrust,
        source_path_present: anchorForm.sourcePath.trim().length > 0,
      },
    })
    if (created) {
      if (isFailedAnchorStatus(created.status)) {
        setMessage(null)
        setError({
          title: '语料导入失败',
          message: `「${created.title}」处理失败，已保留处理记录；请到“处理记录”查看失败详情，需要重新跑解析、切分、抽取和索引时再重建语料。`,
          diagnostic: buildCopyableDiagnostic({
            fallbackMessage: '语料导入失败',
            operation: 'CreateReferenceAnchor',
            bridgeMethod: 'CreateReferenceAnchor',
            detail: {
              anchor_id: created.anchor_id,
              title: created.title,
              status: created.status,
              source_kind: created.source_kind,
              source_file_hash: created.source_file_hash,
            },
          }),
        })
        await loadAnchors()
        return
      }

      setAnchorForm(EMPTY_ANCHOR_FORM)
      await loadAnchors()
    }
  }

  async function createAnchors() {
    const sourcePaths = lines(anchorForm.bulkSourcePaths)
    if (sourcePaths.length === 0) {
      setError('请输入批量导入路径')
      return
    }

    if (sourcePaths.length > 50) {
      setError('一次最多批量导入 50 个语料来源')
      return
    }

    const userTags = lines(anchorForm.userTags)
    const result = await run(() => app.CreateReferenceAnchorsWithResult({
      anchors: sourcePaths.map((sourcePath, index) => ({
        novel_id: novelId,
        title: bulkAnchorTitle(anchorForm.title, sourcePath, index, sourcePaths.length),
        author: anchorForm.author.trim() || undefined,
        source_path: sourcePath,
        source_kind: sourceKindFromPath(sourcePath, anchorForm.sourceKind),
        license_status: anchorForm.licenseStatus,
        visibility: anchorForm.visibility,
        source_trust: anchorForm.sourceTrust,
        user_tags: userTags,
      })),
    }), undefined, {
      fallbackMessage: '批量导入语料来源失败',
      operation: 'CreateReferenceAnchorsWithResult',
      bridgeMethod: 'CreateReferenceAnchorsWithResult',
      detail: {
        source_count: sourcePaths.length,
        source_kind: anchorForm.sourceKind,
        visibility: anchorForm.visibility,
        source_trust: anchorForm.sourceTrust,
      },
    })
    if (result) {
      const allSucceeded = handleCreateAnchorsResult(
        result,
        '已批量导入',
        '批量导入语料来源失败',
        {
          operation: 'CreateReferenceAnchorsWithResult',
          bridgeMethod: 'CreateReferenceAnchorsWithResult',
          detail: {
            source_count: sourcePaths.length,
            source_kind: anchorForm.sourceKind,
            visibility: anchorForm.visibility,
            source_trust: anchorForm.sourceTrust,
          },
        },
      )
      if (result.succeeded_count > 0 || result.failed_count > 0) {
        await loadAnchors()
      }
      if (!allSucceeded) {
        handleCreateAnchorsResult(
          result,
          '已批量导入',
          '批量导入语料来源失败',
          {
            operation: 'CreateReferenceAnchorsWithResult',
            bridgeMethod: 'CreateReferenceAnchorsWithResult',
            detail: {
              source_count: sourcePaths.length,
              source_kind: anchorForm.sourceKind,
              visibility: anchorForm.visibility,
              source_trust: anchorForm.sourceTrust,
            },
          },
        )
        return
      }
      setAnchorForm(EMPTY_ANCHOR_FORM)
    }
  }

  async function startCorpusAnalysis(anchor: reference.Anchor) {
    if (isFailedAnchorStatus(anchor.status)) {
      setError('该来源尚未准备完成，请先查看处理记录并重建来源。')
      return
    }

    const job = await run(() => app.EnqueueReferenceCorpusAnalysisJob({
      run_id: createCorpusAnalysisRunId(anchor.anchor_id),
      novel_id: novelId,
      anchor_id: anchor.anchor_id,
      job_kind: 'feature_analysis',
      scope: 'sentence',
      priority_class: 'normal',
      priority_value: 10,
      token_budget: null,
      max_attempts: 3,
      min_observation_confidence: 0.7,
    }), `已开始分析「${anchor.title}」，可以离开此页，稍后在“后台任务”查看进度。`, {
      fallbackMessage: '启动素材分析失败',
      operation: 'EnqueueReferenceCorpusAnalysisJob',
      bridgeMethod: 'EnqueueReferenceCorpusAnalysisJob',
      detail: {
        anchor_id: anchor.anchor_id,
        job_kind: 'feature_analysis',
        scope: 'sentence',
      },
    })

    if (!job) return

    setActiveCorpusTab('analysis_jobs')
    window.requestAnimationFrame(() => document.getElementById('corpus-tab-analysis_jobs')?.focus())
  }

  async function importLibraryPack() {
    if (!anchorForm.libraryPackManifest.trim()) {
      setError('请输入库包清单 JSON')
      return
    }

    let anchors: reference.CreateAnchorInput[]
    try {
      anchors = parseLibraryPackManifest(anchorForm.libraryPackManifest, anchorForm, novelId)
    } catch (err) {
      setError(err instanceof Error ? err.message : '库包清单无法解析')
      return
    }

    const result = await run(() => app.CreateReferenceAnchorsWithResult({ anchors }), undefined, {
      fallbackMessage: '库包导入失败',
      operation: 'CreateReferenceAnchorsWithResult',
      bridgeMethod: 'CreateReferenceAnchorsWithResult',
      detail: {
        source_count: anchors.length,
        manifest_present: anchorForm.libraryPackManifest.trim().length > 0,
      },
    })
    if (result) {
      const allSucceeded = handleCreateAnchorsResult(
        result,
        '已导入库包',
        '库包导入失败',
        {
          operation: 'CreateReferenceAnchorsWithResult',
          bridgeMethod: 'CreateReferenceAnchorsWithResult',
          detail: {
            source_count: anchors.length,
            manifest_present: anchorForm.libraryPackManifest.trim().length > 0,
          },
        },
      )
      if (result.succeeded_count > 0 || result.failed_count > 0) {
        await loadAnchors()
      }
      if (!allSucceeded) {
        handleCreateAnchorsResult(
          result,
          '已导入库包',
          '库包导入失败',
          {
            operation: 'CreateReferenceAnchorsWithResult',
            bridgeMethod: 'CreateReferenceAnchorsWithResult',
            detail: {
              source_count: anchors.length,
              manifest_present: anchorForm.libraryPackManifest.trim().length > 0,
            },
          },
        )
        return
      }
      setAnchorForm(EMPTY_ANCHOR_FORM)
    }
  }

  async function pickReferenceSourceFile() {
    const pickedPath = await run(() => app.PickReferenceSourceFile(), undefined, {
      fallbackMessage: '选择参考源文件失败',
      operation: 'PickReferenceSourceFile',
      bridgeMethod: 'PickReferenceSourceFile',
      detail: {
        phase: 'pick_reference_source_file',
      },
    })
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
    const rebuilt = await run(() => app.RebuildReferenceAnchor(novelId, anchorId), '语料已重建', {
      fallbackMessage: '语料重建失败',
      operation: 'RebuildReferenceAnchor',
      bridgeMethod: 'RebuildReferenceAnchor',
      detail: {
        anchor_id: anchorId,
      },
    })
    if (rebuilt) {
      await loadAnchors()
    }
  }

  async function promoteAnchorToWorkspaceCorpus(anchor: reference.Anchor) {
    const promoted = await run(() => app.PromoteReferenceAnchorToWorkspaceCorpus({
      novel_id: novelId,
      anchor_id: anchor.anchor_id,
    }), '已提升为工作区语料', {
      fallbackMessage: '提升为工作区语料失败',
      operation: 'PromoteReferenceAnchorToWorkspaceCorpus',
      bridgeMethod: 'PromoteReferenceAnchorToWorkspaceCorpus',
      detail: {
        anchor_id: anchor.anchor_id,
        title: anchor.title,
      },
    })
    if (promoted) {
      await loadAnchors()
    }
  }

  async function promoteSelectedAnchorsToWorkspaceCorpus() {
    if (selectedNovelAnchors.length === 0) return

    const anchorIds = selectedNovelAnchors.map(anchor => anchor.anchor_id)
    const anchorIdSet = new Set(anchorIds)
    setLoading(true)
    setError(null)
    setMessage(null)
    try {
      await app.PromoteReferenceAnchorsToWorkspaceCorpus({
        novel_id: novelId,
        anchor_ids: anchorIds,
      })
      setMessage(`已批量提升 ${anchorIds.length} 个参考锚点为工作区语料`)
    } catch (err) {
      setError(referenceError(err, {
        fallbackMessage: '批量提升为工作区语料失败',
        operation: 'PromoteReferenceAnchorsToWorkspaceCorpus',
        bridgeMethod: 'PromoteReferenceAnchorsToWorkspaceCorpus',
        detail: { anchor_count: anchorIds.length },
      }))
      setLoading(false)
      return
    } finally {
      setLoading(false)
    }

    setSelectedAnchorIds(ids => ids.filter(id => !anchorIdSet.has(id)))
    await loadAnchors()
  }

  async function deleteOrArchiveAnchor(anchor: reference.Anchor) {
    const isWorkspaceCorpus = anchor.owner_scope === 'workspace_corpus'
    setLoading(true)
    setError(null)
    setMessage(null)
    try {
      await app.DeleteReferenceAnchor(novelId, anchor.anchor_id)
      setMessage(isWorkspaceCorpus ? '工作区语料已归档为受限' : '参考锚点已删除')
    } catch (err) {
      setError(referenceError(err, {
        fallbackMessage: isWorkspaceCorpus ? '工作区语料归档失败' : '参考锚点删除失败',
        operation: 'DeleteReferenceAnchor',
        bridgeMethod: 'DeleteReferenceAnchor',
        detail: {
          anchor_id: anchor.anchor_id,
          owner_scope: anchor.owner_scope,
        },
      }))
      setLoading(false)
      return
    } finally {
      setLoading(false)
    }

    setSelectedAnchorIds(ids => ids.filter(id => id !== anchor.anchor_id))
    if (expandedAnchorMaterialId === anchor.anchor_id) {
      setExpandedAnchorMaterialId(null)
      setAnchorMaterialPreview(EMPTY_MATERIAL_PREVIEW)
      setAnchorMaterialQuery('')
      setAnchorMaterialSort('default')
      setEditingMaterialId(null)
      setMaterialTagForm(null)
      setSelectedMaterialIds([])
      setBulkMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
    }
    if (editingAnchorId === anchor.anchor_id) {
      cancelEditAnchor()
    }
    await loadAnchors()
  }

  async function archiveSelectedWorkspaceAnchors() {
    if (selectedWorkspaceAnchors.length === 0) return

    const anchorIds = selectedWorkspaceAnchors.map(anchor => anchor.anchor_id)
    const anchorIdSet = new Set(anchorIds)
    setLoading(true)
    setError(null)
    setMessage(null)
    try {
      await app.DeleteReferenceAnchors({
        novel_id: novelId,
        anchor_ids: anchorIds,
      })
      setMessage(`已批量归档 ${anchorIds.length} 个工作区语料`)
    } catch (err) {
      setError(referenceError(err, {
        fallbackMessage: '批量归档工作区语料失败',
        operation: 'DeleteReferenceAnchors',
        bridgeMethod: 'DeleteReferenceAnchors',
        detail: { anchor_count: anchorIds.length },
      }))
      setLoading(false)
      return
    } finally {
      setLoading(false)
    }

    setSelectedAnchorIds(ids => ids.filter(id => !anchorIdSet.has(id)))
    if (expandedAnchorMaterialId !== null && anchorIdSet.has(expandedAnchorMaterialId)) {
      setExpandedAnchorMaterialId(null)
      setAnchorMaterialPreview(EMPTY_MATERIAL_PREVIEW)
      setAnchorMaterialQuery('')
      setAnchorMaterialSort('default')
      setEditingMaterialId(null)
      setMaterialTagForm(null)
      setSelectedMaterialIds([])
      setBulkMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
    }
    if (editingAnchorId !== null && anchorIdSet.has(editingAnchorId)) {
      cancelEditAnchor()
    }
    await loadAnchors()
  }

  function beginEditAnchor(anchor: reference.Anchor) {
    setEditingAnchorId(anchor.anchor_id)
    setAnchorEditForm(formFromAnchor(anchor))
  }

  function cancelEditAnchor() {
    setEditingAnchorId(null)
    setAnchorEditForm(null)
  }

  async function saveAnchorMetadata(anchor: reference.Anchor) {
    if (!anchorEditForm) return
    if (!anchorEditForm.title.trim()) {
      setError('请输入参考书标题')
      return
    }

    const updated = await run(() => app.UpdateReferenceAnchorMetadata({
      novel_id: novelId,
      anchor_id: anchor.anchor_id,
      title: anchorEditForm.title.trim(),
      author: anchorEditForm.author.trim() || undefined,
      license_status: anchorEditForm.licenseStatus,
      visibility: anchorEditForm.visibility,
      source_trust: anchorEditForm.sourceTrust,
      user_tags: lines(anchorEditForm.userTags),
    }), '参考元数据已更新', {
      fallbackMessage: '参考元数据更新失败',
      operation: 'UpdateReferenceAnchorMetadata',
      bridgeMethod: 'UpdateReferenceAnchorMetadata',
      detail: {
        anchor_id: anchor.anchor_id,
        title: anchorEditForm.title.trim(),
        visibility: anchorEditForm.visibility,
        source_trust: anchorEditForm.sourceTrust,
      },
    })
    if (updated) {
      cancelEditAnchor()
      await loadAnchors()
    }
  }

  async function toggleAnchorMaterialPreview(anchor: reference.Anchor) {
    if (expandedAnchorMaterialId === anchor.anchor_id) {
      setExpandedAnchorMaterialId(null)
      setAnchorMaterialPreview(EMPTY_MATERIAL_PREVIEW)
      setAnchorMaterialQuery('')
      setAnchorMaterialSort('default')
      setEditingMaterialId(null)
      setMaterialTagForm(null)
      setSelectedMaterialIds([])
      setBulkMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
      return
    }

    setAnchorMaterialQuery('')
    setAnchorMaterialSort('default')
    await loadAnchorMaterialPreview(anchor, 1)
  }

  async function loadAnchorMaterialPreview(anchor: reference.Anchor, page: number) {
    const result = await run(() => app.SearchReferenceMaterials({
      novel_id: novelId,
      anchor_ids: [anchor.anchor_id],
      query: '',
      material_types: [],
      emotion_tags: [],
      function_tags: [],
      pov_tags: [],
      technique_tags: [],
      page,
      size: 5,
      narrative_duties: [],
      emotion_transitions: [],
      prose_duties: [],
    }), undefined, {
      fallbackMessage: '锚点材料加载失败',
      operation: 'SearchReferenceMaterials',
      bridgeMethod: 'SearchReferenceMaterials',
      detail: {
        anchor_id: anchor.anchor_id,
        page,
        size: 5,
        scope: 'anchor_preview',
      },
    })
    if (result) {
      setExpandedAnchorMaterialId(anchor.anchor_id)
      setEditingMaterialId(null)
      setMaterialTagForm(null)
      setSelectedMaterialIds([])
      setBulkMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
      setAnchorMaterialPreview({
        items: result.items ?? [],
        page: result.page,
        size: result.size,
        total: result.total,
        totalPages: result.total_pages,
      })
    }
  }

  function beginEditMaterialTags(material: reference.MaterialSummary) {
    setEditingMaterialId(material.material_id)
    setMaterialTagForm(tagFormFromMaterial(material))
  }

  function cancelEditMaterialTags() {
    setEditingMaterialId(null)
    setMaterialTagForm(null)
  }

  function toggleMaterialSelection(materialId: string, checked: boolean) {
    setSelectedMaterialIds(ids => {
      if (checked) {
        return ids.includes(materialId) ? ids : [...ids, materialId]
      }

      return ids.filter(id => id !== materialId)
    })
  }

  async function saveMaterialTags(material: reference.MaterialSummary) {
    if (!materialTagForm) return

    const updated = await run(() => app.UpdateReferenceMaterialTags({
      novel_id: novelId,
      material_id: material.material_id,
      function_tag: materialTagForm.functionTag.trim() || null,
      emotion_tag: materialTagForm.emotionTag.trim() || null,
      scene_tag: materialTagForm.sceneTag.trim() || null,
      pov_tag: materialTagForm.povTag.trim() || null,
      technique_tag: materialTagForm.techniqueTag.trim() || null,
      origin: 'ui',
      note: 'corpus material browser correction',
    }), '材料标签已校正', {
      fallbackMessage: '材料标签更新失败',
      operation: 'UpdateReferenceMaterialTags',
      bridgeMethod: 'UpdateReferenceMaterialTags',
      detail: {
        material_id: material.material_id,
        source_segment_id: material.source_segment_id,
        anchor_id: material.anchor_id,
      },
    })
    if (updated) {
      setAnchorMaterialPreview(current => ({
        ...current,
        items: current.items.map(item => item.material_id === updated.material_id ? updated : item),
      }))
      cancelEditMaterialTags()
    }
  }

  async function saveBulkMaterialTags() {
    if (selectedMaterialIds.length === 0 || !hasBulkMaterialTagOverride) return
    const updated = await run(() => app.UpdateReferenceMaterialsTags({
      novel_id: novelId,
      material_ids: selectedMaterialIds,
      function_tag: bulkMaterialTagForm.functionTag.trim() || null,
      emotion_tag: bulkMaterialTagForm.emotionTag.trim() || null,
      scene_tag: bulkMaterialTagForm.sceneTag.trim() || null,
      pov_tag: bulkMaterialTagForm.povTag.trim() || null,
      technique_tag: bulkMaterialTagForm.techniqueTag.trim() || null,
      origin: 'ui',
      note: 'corpus material browser bulk correction',
    }), `已批量校正 ${selectedMaterialIds.length} 条材料标签`, {
      fallbackMessage: '批量材料标签更新失败',
      operation: 'UpdateReferenceMaterialsTags',
      bridgeMethod: 'UpdateReferenceMaterialsTags',
      detail: {
        material_count: selectedMaterialIds.length,
        scope: 'anchor_preview',
      },
    })

    if (updated) {
      const updatedById = new Map(updated.map(material => [material.material_id, material]))
      setAnchorMaterialPreview(current => ({
        ...current,
        items: current.items.map(item => updatedById.get(item.material_id) ?? item),
      }))
      setSelectedMaterialIds([])
      setBulkMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
      cancelEditMaterialTags()
    }
  }

  function toggleLibraryMaterialSelection(materialId: string, checked: boolean) {
    setSelectedLibraryMaterialIds(ids => {
      if (checked) {
        return ids.includes(materialId) ? ids : [...ids, materialId]
      }

      return ids.filter(id => id !== materialId)
    })
  }

  function changeMaterialLibraryArchiveFilter(value: MaterialArchiveFilter) {
    setMaterialLibraryArchiveFilter(value)
    setMaterialLibrary(EMPTY_MATERIAL_LIBRARY)
    setMaterialTagReviewQueue(EMPTY_MATERIAL_TAG_REVIEW_QUEUE)
    setMaterialLibraryPageQuery('')
    setMaterialLibrarySort('default')
    setSelectedLibraryMaterialIds([])
    setBulkLibraryMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
  }

  async function searchMaterialLibrary(page = 1, resetPageView = false) {
    const result = await run(() => app.SearchReferenceMaterials({
      novel_id: novelId,
      anchor_ids: [],
      query: materialLibraryQuery.trim(),
      material_types: lines(materialLibraryFilters.materialTypes),
      emotion_tags: lines(materialLibraryFilters.emotionTags),
      function_tags: lines(materialLibraryFilters.functionTags),
      pov_tags: lines(materialLibraryFilters.povTags),
      technique_tags: lines(materialLibraryFilters.techniqueTags),
      page,
      size: 10,
      narrative_duties: lines(materialLibraryFilters.narrativeDuties),
      emotion_transitions: lines(materialLibraryFilters.emotionTransitions),
      prose_duties: lines(materialLibraryFilters.proseDuties),
      archive_filter: materialLibraryArchiveFilter,
    }), undefined, {
      fallbackMessage: '材料库搜索失败',
      operation: 'SearchReferenceMaterials',
      bridgeMethod: 'SearchReferenceMaterials',
      detail: {
        query: materialLibraryQuery.trim(),
        page,
        size: 10,
        archive_filter: materialLibraryArchiveFilter,
        scope: 'material_library',
      },
    })
    if (result) {
      setMaterialLibrary({
        items: result.items ?? [],
        page: result.page,
        size: result.size,
        total: result.total,
        totalPages: result.total_pages,
      })
      if (resetPageView) {
        setSelectedLibraryMaterialIds([])
        setBulkLibraryMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
        setMaterialLibraryPageQuery('')
        setMaterialLibrarySort('default')
      }
      await loadMaterialTagReviewQueue(1)
    }
  }

  async function openMaterialDetail(materialId: string) {
    const requestId = materialDetailRequestRef.current + 1
    materialDetailRequestRef.current = requestId
    setMaterialDetailId(materialId)
    setMaterialDetail(null)
    setMaterialDetailError(null)
    setMaterialDetailLoading(true)
    try {
      const detail = await app.GetReferenceMaterialDetail({
        novel_id: novelId,
        material_id: materialId,
      })
      if (materialDetailRequestRef.current !== requestId) return
      setMaterialDetail(detail ?? null)
      if (!detail) {
        setMaterialDetailError({
          title: '材料明细不可用',
          message: '材料不存在、已归档，或当前作品无权访问。',
          diagnostic: null,
        })
      }
    } catch (err) {
      if (materialDetailRequestRef.current !== requestId) return
      setMaterialDetailError(referenceError(err, {
        fallbackMessage: '材料明细加载失败',
        operation: 'GetReferenceMaterialDetail',
        bridgeMethod: 'GetReferenceMaterialDetail',
        detail: { material_id: materialId },
      }))
    } finally {
      if (materialDetailRequestRef.current === requestId) {
        setMaterialDetailLoading(false)
      }
    }
  }

  function closeMaterialDetail() {
    materialDetailRequestRef.current += 1
    setMaterialDetailId(null)
    setMaterialDetail(null)
    setMaterialDetailError(null)
    setMaterialDetailLoading(false)
  }

  async function openSourceProcessingDetail(anchorId: number) {
    setSourceProcessingAnchorId(anchorId)
    setSourceProcessingDetail(null)
    setSourceProcessingError(null)
    setSourceProcessingLoading(true)
    try {
      const detail = await app.GetReferenceSourceProcessingDetail({
        novel_id: novelId,
        anchor_id: anchorId,
      })
      setSourceProcessingDetail(detail ?? null)
      if (!detail) {
        setSourceProcessingError({
          title: '处理记录不可用',
          message: '来源不存在，或当前作品无权访问。',
          diagnostic: null,
        })
      }
    } catch (err) {
      setSourceProcessingError(referenceError(err, {
        fallbackMessage: '处理记录加载失败',
        operation: 'GetReferenceSourceProcessingDetail',
        bridgeMethod: 'GetReferenceSourceProcessingDetail',
        detail: { anchor_id: anchorId },
      }))
    } finally {
      setSourceProcessingLoading(false)
    }
  }

  function closeSourceProcessingDetail() {
    setSourceProcessingAnchorId(null)
    setSourceProcessingDetail(null)
    setSourceProcessingError(null)
    setSourceProcessingLoading(false)
  }

  async function openSourceSegmentDetail(anchorId: number, segmentId: string) {
    const requestId = sourceSegmentDetailRequestRef.current + 1
    sourceSegmentDetailRequestRef.current = requestId
    setSourceSegmentDetailKey({ anchorId, segmentId })
    setSourceSegmentDetail(null)
    setSourceSegmentDetailError(null)
    setSourceSegmentDetailLoading(true)
    try {
      const detail = await app.GetReferenceSourceSegmentDetail({
        novel_id: novelId,
        anchor_id: anchorId,
        segment_id: segmentId,
      })
      if (sourceSegmentDetailRequestRef.current !== requestId) return
      setSourceSegmentDetail(detail ?? null)
      if (!detail) {
        setSourceSegmentDetailError({
          title: '片段明细不可用',
          message: '片段不存在，或当前作品无权访问。',
          diagnostic: null,
        })
      }
    } catch (err) {
      if (sourceSegmentDetailRequestRef.current !== requestId) return
      setSourceSegmentDetailError(referenceError(err, {
        fallbackMessage: '片段明细加载失败',
        operation: 'GetReferenceSourceSegmentDetail',
        bridgeMethod: 'GetReferenceSourceSegmentDetail',
        detail: { anchor_id: anchorId, segment_id: segmentId },
      }))
    } finally {
      if (sourceSegmentDetailRequestRef.current === requestId) {
        setSourceSegmentDetailLoading(false)
      }
    }
  }

  function closeSourceSegmentDetail() {
    sourceSegmentDetailRequestRef.current += 1
    setSourceSegmentDetailKey(null)
    setSourceSegmentDetail(null)
    setSourceSegmentDetailError(null)
    setSourceSegmentDetailLoading(false)
  }

  function locateAffectedSource(sourceId: string) {
    setActiveCorpusTab('sources')
    setAnchorScopeFilter('all')
    setAnchorQuery(sourceId)
    closeSourceProcessingDetail()
  }

  async function locateAffectedMaterial(materialId: string) {
    setActiveCorpusTab('materials')
    setMaterialLibraryArchiveFilter('active')
    setMaterialLibraryQuery(materialId)
    setMaterialLibraryPageQuery('')
    setMaterialLibrarySort('default')
    setSelectedLibraryMaterialIds([])
    closeSourceProcessingDetail()

    const result = await run(() => app.SearchReferenceMaterials({
      novel_id: novelId,
      anchor_ids: [],
      query: materialId,
      material_types: [],
      emotion_tags: [],
      function_tags: [],
      pov_tags: [],
      technique_tags: [],
      page: 1,
      size: 10,
      narrative_duties: [],
      emotion_transitions: [],
      prose_duties: [],
      archive_filter: 'active',
    }), undefined, {
      fallbackMessage: '定位材料失败',
      operation: 'SearchReferenceMaterials',
      bridgeMethod: 'SearchReferenceMaterials',
      detail: {
        material_id: materialId,
        scope: 'source_processing_affected_material',
      },
    })
    if (result) {
      setMaterialLibrary({
        items: result.items ?? [],
        page: result.page,
        size: result.size,
        total: result.total,
        totalPages: result.total_pages,
      })
    }
  }

  function openAffectedMaterialDetail(materialId: string) {
    closeSourceProcessingDetail()
    void openMaterialDetail(materialId)
  }

  function openAffectedSourceSegmentDetail(anchorId: number, segmentId: string) {
    closeSourceProcessingDetail()
    void openSourceSegmentDetail(anchorId, segmentId)
  }

  async function saveBulkLibraryMaterialTags() {
    if (selectedLibraryMaterialIds.length === 0 || !hasBulkLibraryMaterialTagOverride) return
    const updated = await run(() => app.UpdateReferenceMaterialsTags({
      novel_id: novelId,
      material_ids: selectedLibraryMaterialIds,
      function_tag: bulkLibraryMaterialTagForm.functionTag.trim() || null,
      emotion_tag: bulkLibraryMaterialTagForm.emotionTag.trim() || null,
      scene_tag: bulkLibraryMaterialTagForm.sceneTag.trim() || null,
      pov_tag: bulkLibraryMaterialTagForm.povTag.trim() || null,
      technique_tag: bulkLibraryMaterialTagForm.techniqueTag.trim() || null,
      origin: 'ui',
      note: 'corpus material library bulk correction',
    }), `材料库已批量校正 ${selectedLibraryMaterialIds.length} 条材料标签`, {
      fallbackMessage: '材料库批量标签更新失败',
      operation: 'UpdateReferenceMaterialsTags',
      bridgeMethod: 'UpdateReferenceMaterialsTags',
      detail: {
        material_count: selectedLibraryMaterialIds.length,
        scope: 'material_library',
      },
    })

    if (updated) {
      const updatedById = new Map(updated.map(material => [material.material_id, material]))
      setMaterialLibrary(current => ({
        ...current,
        items: current.items.map(item => updatedById.get(item.material_id) ?? item),
      }))
      setSelectedLibraryMaterialIds([])
      setBulkLibraryMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
      await loadMaterialTagReviewQueue(1)
    }
  }

  async function archiveSelectedLibraryMaterials() {
    if (selectedLibraryMaterialIds.length === 0) return
    const archivedIds = [...selectedLibraryMaterialIds]
    setLoading(true)
    setError(null)
    setMessage(null)
    try {
      await app.DeleteReferenceMaterials({
        novel_id: novelId,
        material_ids: archivedIds,
      })
      setMessage(`材料库已归档 ${archivedIds.length} 条材料`)
    } catch (err) {
      setError(referenceError(err, {
        fallbackMessage: '材料库归档失败',
        operation: 'DeleteReferenceMaterials',
        bridgeMethod: 'DeleteReferenceMaterials',
        detail: { material_count: archivedIds.length },
      }))
      setLoading(false)
      return
    } finally {
      setLoading(false)
    }

    const archivedSet = new Set(archivedIds)
    setMaterialLibrary(current => ({
      ...current,
      items: current.items.filter(item => !archivedSet.has(item.material_id)),
      total: Math.max(0, current.total - archivedSet.size),
    }))
    setSelectedLibraryMaterialIds(ids => ids.filter(id => !archivedSet.has(id)))
    setBulkLibraryMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
    await loadMaterialTagReviewQueue(1)
  }

  async function restoreSelectedLibraryMaterials() {
    if (selectedLibraryMaterialIds.length === 0) return
    const restoredIds = [...selectedLibraryMaterialIds]
    setLoading(true)
    setError(null)
    setMessage(null)
    try {
      await app.RestoreReferenceMaterials({
        novel_id: novelId,
        material_ids: restoredIds,
      })
      setMessage(`材料库已恢复 ${restoredIds.length} 条材料`)
    } catch (err) {
      setError(referenceError(err, {
        fallbackMessage: '材料库恢复失败',
        operation: 'RestoreReferenceMaterials',
        bridgeMethod: 'RestoreReferenceMaterials',
        detail: { material_count: restoredIds.length },
      }))
      setLoading(false)
      return
    } finally {
      setLoading(false)
    }

    const restoredSet = new Set(restoredIds)
    setMaterialLibrary(current => ({
      ...current,
      items: current.items.filter(item => !restoredSet.has(item.material_id)),
      total: Math.max(0, current.total - restoredSet.size),
    }))
    setSelectedLibraryMaterialIds(ids => ids.filter(id => !restoredSet.has(id)))
    setBulkLibraryMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
    await loadMaterialTagReviewQueue(1)
  }

  async function searchMaterials() {
    const result = await run(() => app.SearchReferenceMaterials({
      novel_id: novelId,
      anchor_ids: selectedAnchorIds,
      query: materialQuery.trim(),
      material_types: lines(materialFilters.materialTypes),
      emotion_tags: lines(materialFilters.emotionTags),
      function_tags: lines(materialFilters.functionTags),
      pov_tags: lines(materialFilters.povTags),
      technique_tags: lines(materialFilters.techniqueTags),
      page: 1,
      size: 10,
      narrative_duties: lines(materialFilters.narrativeDuties),
      emotion_transitions: lines(materialFilters.emotionTransitions),
      prose_duties: lines(materialFilters.proseDuties),
    }), undefined, {
      fallbackMessage: '参考材料搜索失败',
      operation: 'SearchReferenceMaterials',
      bridgeMethod: 'SearchReferenceMaterials',
      detail: {
        anchor_count: selectedAnchorIds.length,
        query: materialQuery.trim(),
        scope: 'manual_material_search',
      },
    })
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
    }), '章节蓝图已生成', {
      fallbackMessage: '章节蓝图生成失败',
      operation: 'GenerateReferenceChapterBlueprint',
      bridgeMethod: 'GenerateReferenceChapterBlueprint',
      detail: {
        chapter_number: chapterNumber,
        title: blueprintForm.title.trim(),
        anchor_count: selectedAnchorIds.length,
        known_fact_count: lines(blueprintForm.knownFacts).length,
        forbidden_fact_count: lines(blueprintForm.forbiddenFacts).length,
      },
    })
    if (blueprint) {
      setActiveBlueprint(blueprint)
      setRevisionForm(formFromBlueprint(blueprint))
      setBinding(null)
      setDraft(null)
      await loadBlueprints()
    }
  }

  async function syncBlueprintFromRun(runPayload: reference.OrchestrationRun) {
    if (runPayload.blueprint_id <= 0) return
    const blueprint = await app.GetReferenceChapterBlueprint(novelId, runPayload.blueprint_id)
    if (blueprint) {
      setActiveBlueprint(blueprint)
      setRevisionForm(formFromBlueprint(blueprint))
      setBinding(null)
      setDraft(null)
      await loadBlueprints()
    }
  }

  function changeOrchestrationStyleProfile(profileId: string) {
    setOrchestrationStyleOptedOut(profileId === '')
    setOrchestrationStyleProfileId(profileId)
  }

  function changeOrchestrationStyleIntensity(intensity: reference.StyleImitationIntensity) {
    setOrchestrationStyleIntensity(intensity)
    setOrchestrationStyleMinFit(ORCHESTRATION_STYLE_MIN_FIT[intensity])
  }

  function buildOrchestrationStylePolicy(): reference.OrchestrationStylePolicy | null {
    const profileId = Number.parseInt(orchestrationEffectiveStyleProfileId, 10)
    if (!Number.isFinite(profileId) || profileId <= 0) {
      return null
    }

    const dimensions = lines(orchestrationStyleDimensions)
    const requiredEvidenceTypes = lines(orchestrationStyleRequiredEvidenceTypes)
    if (dimensions.length === 0 && requiredEvidenceTypes.length === 0) {
      throw new Error('启用风格策略时至少需要一个风格维度或证据类型')
    }

    const minStyleFit = Number.parseFloat(orchestrationStyleMinFit)
    if (!Number.isFinite(minStyleFit) || minStyleFit < 0) {
      throw new Error('最低拟合必须是非负数字')
    }

    return {
      style_profile_ids: [profileId],
      style_dimensions: dimensions,
      imitation_intensity: orchestrationStyleIntensity,
      min_style_fit: minStyleFit,
      allowed_closeness: orchestrationStyleAllowedCloseness.trim(),
      required_evidence_types: requiredEvidenceTypes,
      forbidden_style_risks: lines(orchestrationStyleForbiddenRisks),
    }
  }

  async function startOrchestration() {
    const chapterNumber = Number.parseInt(blueprintForm.chapterNumber, 10)
    if (!Number.isFinite(chapterNumber) || chapterNumber < 1) {
      setError('请输入有效章节号')
      return
    }

    let stylePolicy: reference.OrchestrationStylePolicy | null = null
    try {
      stylePolicy = buildOrchestrationStylePolicy()
    } catch (err) {
      setError(err instanceof Error ? err.message : '风格策略无效')
      return
    }

    const started = await run(() => app.StartReferenceOrchestrationRun({
      novel_id: novelId,
      chapter_number: chapterNumber,
      chapter_goal: blueprintForm.chapterGoal.trim() || undefined,
      known_facts: lines(blueprintForm.knownFacts),
      forbidden_facts: lines(blueprintForm.forbiddenFacts),
      anchor_ids: orchestrationUseSelectedAnchors ? selectedAnchorIds : null,
      corpus_search_policy: {
        mode: 'story_context',
        max_results_per_beat: 3,
        license_statuses: ['user_provided'],
        include_anchor_ids: orchestrationUseSelectedAnchors ? selectedAnchorIds : [],
        exclude_anchor_ids: [],
      },
      source_confirmed: false,
      style_policy: stylePolicy,
    }), '编排已启动，等待确认来源与事实边界', {
      fallbackMessage: '参考编排启动失败',
      operation: 'StartReferenceOrchestrationRun',
      bridgeMethod: 'StartReferenceOrchestrationRun',
      detail: {
        chapter_number: chapterNumber,
        use_selected_anchors: orchestrationUseSelectedAnchors,
        selected_anchor_count: selectedAnchorIds.length,
        style_profile_id: orchestrationEffectiveStyleProfileId,
      },
    })
    if (started) {
      setActiveOrchestrationRun(started)
      await loadOrchestrationRuns()
    }
  }

  async function selectOrchestrationRun(runId: string) {
    const selected = await run(() => app.GetReferenceOrchestrationRun(novelId, runId), undefined, {
      fallbackMessage: '加载参考编排失败',
      operation: 'GetReferenceOrchestrationRun',
      bridgeMethod: 'GetReferenceOrchestrationRun',
      detail: { run_id: runId },
    })
    if (selected) {
      setActiveOrchestrationRun(selected)
      await syncBlueprintFromRun(selected)
    }
  }

  async function resumeOrchestration(decisionType: string, decisionPayload: string) {
    if (!activeOrchestrationRun) return
    const runId = activeOrchestrationRun.run_id
    const resumed = await run(() => app.ResumeReferenceOrchestrationRun({
      novel_id: novelId,
      run_id: runId,
      decision_type: decisionType,
      decision_payload: decisionPayload,
    }), '编排已继续', {
      fallbackMessage: '参考编排继续失败',
      operation: 'ResumeReferenceOrchestrationRun',
      bridgeMethod: 'ResumeReferenceOrchestrationRun',
      detail: {
        run_id: runId,
        decision_type: decisionType,
        decision_payload_present: decisionPayload.trim().length > 0,
      },
    })
    if (resumed) {
      setActiveOrchestrationRun(resumed)
      await syncBlueprintFromRun(resumed)
      await loadOrchestrationRuns()
      try {
        const events = await app.GetReferenceOrchestrationRunEvents(novelId, runId)
        setOrchestrationEvents(events ?? [])
      } catch (err) {
        setError(referenceError(err, {
          fallbackMessage: '加载编排事件失败',
          operation: 'GetReferenceOrchestrationRunEvents',
          bridgeMethod: 'GetReferenceOrchestrationRunEvents',
          detail: { run_id: runId, phase: 'after_resume' },
        }))
      }
    }
  }

  async function cancelOrchestration(runId: string) {
    const cancelled = await run(() => app.CancelReferenceOrchestrationRun({
      novel_id: novelId,
      run_id: runId,
      reason: 'cancelled from reference orchestration panel',
    }), '编排已取消', {
      fallbackMessage: '取消参考编排失败',
      operation: 'CancelReferenceOrchestrationRun',
      bridgeMethod: 'CancelReferenceOrchestrationRun',
      detail: { run_id: runId },
    })
    if (cancelled) {
      setActiveOrchestrationRun(cancelled)
      await loadOrchestrationRuns()
      try {
        const events = await app.GetReferenceOrchestrationRunEvents(novelId, runId)
        setOrchestrationEvents(events ?? [])
      } catch (err) {
        setError(referenceError(err, {
          fallbackMessage: '加载编排事件失败',
          operation: 'GetReferenceOrchestrationRunEvents',
          bridgeMethod: 'GetReferenceOrchestrationRunEvents',
          detail: { run_id: runId, phase: 'after_cancel' },
        }))
      }
    }
  }

  async function selectBlueprint(blueprintId: number) {
    const blueprint = await run(() => app.GetReferenceChapterBlueprint(novelId, blueprintId), undefined, {
      fallbackMessage: '加载章节蓝图失败',
      operation: 'GetReferenceChapterBlueprint',
      bridgeMethod: 'GetReferenceChapterBlueprint',
      detail: { blueprint_id: blueprintId },
    })
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
    }), '蓝图评审已完成', {
      fallbackMessage: '蓝图评审失败',
      operation: 'ReviewReferenceChapterBlueprint',
      bridgeMethod: 'ReviewReferenceChapterBlueprint',
      detail: {
        blueprint_id: activeBlueprint.blueprint_id,
        chapter_number: activeBlueprint.chapter_number,
      },
    })
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
      approver_origin: 'user',
    }), '蓝图已批准', {
      fallbackMessage: '蓝图批准失败',
      operation: 'ApproveReferenceChapterBlueprint',
      bridgeMethod: 'ApproveReferenceChapterBlueprint',
      detail: {
        blueprint_id: activeBlueprint.blueprint_id,
        review_id: activeBlueprint.latest_review!.review_id,
      },
    })
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
      select_top_candidate: true,
    }), '材料已绑定到蓝图', {
      fallbackMessage: '蓝图材料绑定失败',
      operation: 'BindReferenceBlueprintMaterials',
      bridgeMethod: 'BindReferenceBlueprintMaterials',
      detail: {
        blueprint_id: activeBlueprint.blueprint_id,
        beat_count: activeBlueprint.beats.length,
        max_results_per_beat: 3,
      },
    })
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
    const hasStyleContract = activeBlueprint.beats.some(beat => beat.style_contract)
    const result = await run(() => app.GenerateReferenceAnchoredDraft({
      novel_id: novelId,
      blueprint_id: activeBlueprint.blueprint_id,
      beat_ids: [],
      ...(hasStyleContract
        ? {
            style_intensities: ['loose', 'moderate', 'strong'],
            candidates_per_beat: 3,
          } as const
        : {}),
    }), '候选段落已生成', {
      fallbackMessage: '参考候选段落生成失败',
      operation: 'GenerateReferenceAnchoredDraft',
      bridgeMethod: 'GenerateReferenceAnchoredDraft',
      detail: {
        blueprint_id: activeBlueprint.blueprint_id,
        has_style_contract: hasStyleContract,
      },
    })
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
    try {
      addStyleContractChange(
        changes,
        `${prefix}style_contract`,
        styleContractFromForm(revisionForm),
        beat.style_contract ?? null,
      )
    } catch (err) {
      setError(err instanceof Error ? err.message : '风格合约无效')
      return
    }

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
    }), '蓝图已修订，需要重新评审和批准', {
      fallbackMessage: '蓝图修订保存失败',
      operation: 'ReviseReferenceChapterBlueprint',
      bridgeMethod: 'ReviseReferenceChapterBlueprint',
      detail: {
        blueprint_id: activeBlueprint.blueprint_id,
        change_count: changes.length,
        beat_id: beat.beat_id,
      },
    })
    if (revised) {
      setActiveBlueprint(revised)
      setRevisionForm(formFromBlueprint(revised))
      setBinding(null)
      setDraft(null)
      await loadBlueprints()
    }
  }

  const showLegacyChapterReference = ENABLE_REFERENCE_ACTIVITY_CHAPTER_DEBUG

  return (
    <main className="flex-1 min-w-0 overflow-y-auto overscroll-contain bg-background">
      <div className="mx-auto max-w-6xl px-5 py-6 space-y-5">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex items-center gap-2">
            <BookMarked className="h-4 w-4 text-muted-foreground" />
            <h2 className="text-sm font-semibold text-foreground">
              素材库
              <span className="ml-2 text-xs font-normal text-muted-foreground">{anchors.length} 个锚点</span>
            </h2>
          </div>
          <div className="flex items-center gap-2">
            {loading && <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />}
            <button onClick={() => { void loadAnchors(); void loadStyleProfiles() }} className="inline-flex items-center gap-1 px-2.5 py-1 rounded text-xs text-muted-foreground hover:text-foreground hover:bg-secondary transition-colors">
              <RefreshCcw className="h-3 w-3" />刷新
            </button>
          </div>
        </div>

        {error && (
          typeof error === 'string' ? (
            <ErrorCallout
              compact
              title="操作失败"
              message={error}
              diagnostic={null}
              className="rounded-md"
              onClose={() => setError(null)}
            />
          ) : (
            <ErrorCallout
              compact
              title={error.title}
              message={error.message}
              diagnostic={error.diagnostic}
              className="rounded-md"
              onClose={() => setError(null)}
            />
          )
        )}
        {message && <div className="rounded-md border border-emerald-500/30 bg-emerald-500/10 px-3 py-2 text-xs text-emerald-700 dark:text-emerald-300">{message}</div>}

        <div data-testid="corpus-library-tabs" className="flex flex-wrap gap-1 rounded-lg border border-border bg-card p-1" role="tablist" aria-label="素材库任务">
          {CORPUS_LIBRARY_TABS.map(tab => (
            <button
              id={`corpus-tab-${tab.id}`}
              key={tab.id}
              type="button"
              role="tab"
              aria-selected={activeCorpusTab === tab.id}
              aria-controls={`corpus-panel-${tab.id}`}
              tabIndex={activeCorpusTab === tab.id ? 0 : -1}
              onClick={() => setActiveCorpusTab(tab.id)}
              onKeyDown={event => {
                if (event.key === 'ArrowRight' || event.key === 'ArrowDown') {
                  event.preventDefault()
                  selectAdjacentCorpusTab(tab.id, 1)
                  return
                }
                if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') {
                  event.preventDefault()
                  selectAdjacentCorpusTab(tab.id, -1)
                  return
                }
                if (event.key === 'Home') {
                  event.preventDefault()
                  setActiveCorpusTab(CORPUS_LIBRARY_TABS[0].id)
                  window.requestAnimationFrame(() => document.getElementById(`corpus-tab-${CORPUS_LIBRARY_TABS[0].id}`)?.focus())
                  return
                }
                if (event.key === 'End') {
                  event.preventDefault()
                  const lastTab = CORPUS_LIBRARY_TABS[CORPUS_LIBRARY_TABS.length - 1]
                  setActiveCorpusTab(lastTab.id)
                  window.requestAnimationFrame(() => document.getElementById(`corpus-tab-${lastTab.id}`)?.focus())
                }
              }}
              className={`rounded-md px-3 py-1.5 text-xs font-medium transition-colors ${activeCorpusTab === tab.id ? 'bg-secondary text-foreground' : 'text-muted-foreground hover:bg-secondary/60 hover:text-foreground'}`}
            >
              {tab.label}
            </button>
          ))}
        </div>

        <div className={showLegacyChapterReference ? 'grid grid-cols-1 xl:grid-cols-[360px_minmax(0,1fr)] gap-4' : 'space-y-4'}>
          <section className="space-y-4" aria-labelledby="corpus-library-heading">
            <div className="space-y-1">
              <div className="flex items-center gap-2">
                <BookMarked className="h-3.5 w-3.5 text-muted-foreground" />
                <h3 id="corpus-library-heading" className="text-xs font-semibold text-foreground">语料库管理</h3>
              </div>
              <p className="text-xs leading-relaxed text-muted-foreground">
                管理可检索来源、库条目元数据、可见性和材料预览；写作时会在章节正文的“参考素材”面板按上下文自动推荐。
              </p>
            </div>

            <div
              id={`corpus-panel-${activeCorpusTab}`}
              role="tabpanel"
              aria-labelledby={`corpus-tab-${activeCorpusTab}`}
              className="space-y-4"
            >
{activeCorpusTab === 'sources' && (
<>
 {(locatedCorpusEvidenceLoading || locatedCorpusEvidenceError || locatedCorpusEvidence) && (
 <section data-testid="located-corpus-evidence" className="rounded-lg border border-primary/40 bg-primary/5 p-4">
 <div className="flex flex-wrap items-center justify-between gap-2">
 <h3 className="text-xs font-semibold text-foreground">复核证据原文</h3>
 {locatedCorpusEvidence && <span className="text-[11px] text-muted-foreground">{locatedCorpusEvidence.nodeId} · {locatedCorpusEvidence.nodeType}{locatedCorpusEvidence.chapterIndex != null ? ` · 第 ${locatedCorpusEvidence.chapterIndex} 章` : ''}</span>}
 </div>
 {locatedCorpusEvidenceLoading && <p className="mt-2 text-xs text-muted-foreground">正在定位原文节点...</p>}
 {locatedCorpusEvidenceError && <p className="mt-2 text-xs text-destructive">{locatedCorpusEvidenceError}</p>}
 {locatedCorpusEvidence && <p className="mt-2 whitespace-pre-wrap text-sm leading-7 text-foreground">
 {locatedCorpusEvidence.text.slice(0, locatedCorpusEvidence.startOffset)}
 <mark data-corpus-evidence-selection className="bg-amber-300 px-0.5 text-foreground">{locatedCorpusEvidence.text.slice(locatedCorpusEvidence.startOffset, locatedCorpusEvidence.endOffset) || locatedCorpusEvidence.text}</mark>
 {locatedCorpusEvidence.text.slice(locatedCorpusEvidence.endOffset)}
 </p>}
 </section>
 )}
<div data-testid="reference-import-panel" className="rounded-lg border border-border bg-card p-4">
              <div className="flex items-center gap-2 mb-3">
                <Plus className="h-3.5 w-3.5 text-muted-foreground" />
                <h3 className="text-xs font-semibold text-foreground">导入语料来源</h3>
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
                <div className="grid grid-cols-2 gap-2">
                  <Field label="可见性">
                    <select value={anchorForm.visibility} onChange={event => setAnchorForm(form => ({ ...form, visibility: event.target.value }))} className={inputClass}>
                      <option value="private">private</option>
                      <option value="workspace">workspace</option>
                      <option value="restricted">restricted</option>
                    </select>
                  </Field>
                  <Field label="来源可信度">
                    <select value={anchorForm.sourceTrust} onChange={event => setAnchorForm(form => ({ ...form, sourceTrust: event.target.value }))} className={inputClass}>
                      <option value="user_verified">user_verified</option>
                      <option value="imported">imported</option>
                      <option value="unverified">unverified</option>
                    </select>
                  </Field>
                </div>
                <Field label="用户标签">
                  <input value={anchorForm.userTags} onChange={event => setAnchorForm(form => ({ ...form, userTags: event.target.value }))} className={inputClass} placeholder="分号分隔" />
                </Field>
                <button onClick={createAnchor} disabled={loading} className="inline-flex w-full items-center justify-center gap-1.5 rounded bg-primary px-3 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50">
                  <Plus className="h-3.5 w-3.5" />创建
                </button>
                <Field label="批量路径">
                  <textarea
                    value={anchorForm.bulkSourcePaths}
                    onChange={event => setAnchorForm(form => ({ ...form, bulkSourcePaths: event.target.value }))}
                    className={`${inputClass} min-h-20 resize-y`}
                    placeholder={'D:\\books\\reference-a.md\nD:\\books\\reference-b.txt'}
                    aria-label="批量路径"
                  />
                </Field>
                <button onClick={createAnchors} disabled={loading} className="inline-flex w-full items-center justify-center gap-1.5 rounded border border-border bg-background px-3 py-1.5 text-xs font-medium text-foreground hover:bg-secondary disabled:opacity-50">
                  <Plus className="h-3.5 w-3.5" />批量导入
                </button>
                <Field label="库包清单">
                  <textarea
                    value={anchorForm.libraryPackManifest}
                    onChange={event => setAnchorForm(form => ({ ...form, libraryPackManifest: event.target.value }))}
                    className={`${inputClass} min-h-24 resize-y font-mono text-[11px]`}
                    placeholder={'{"sources":[{"source_path":"D:\\\\books\\\\pack-a.md","title":"库包参考"}]}'}
                    aria-label="库包清单"
                  />
                </Field>
                <button onClick={importLibraryPack} disabled={loading} className="inline-flex w-full items-center justify-center gap-1.5 rounded border border-border bg-background px-3 py-1.5 text-xs font-medium text-foreground hover:bg-secondary disabled:opacity-50">
                  <BookMarked className="h-3.5 w-3.5" />导入库包
                </button>
              </div>
            </div>

            <div className="rounded-lg border border-border bg-card p-4">
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <BookMarked className="h-3.5 w-3.5 text-muted-foreground" />
                  <h3 className="text-xs font-semibold text-foreground">库条目</h3>
                </div>
                <div className="inline-flex rounded-md border border-border bg-background p-0.5" aria-label="锚点范围筛选">
                  {[
                    ['all', `全部 ${anchorScopeCounts.all}`],
                    ['novel', `本小说 ${anchorScopeCounts.novel}`],
                    ['workspace_corpus', `工作区 ${anchorScopeCounts.workspace_corpus}`],
                  ].map(([value, label]) => (
                    <button
                      key={value}
                      type="button"
                      onClick={() => setAnchorScopeFilter(value as AnchorScopeFilter)}
                      className={`rounded px-2 py-1 text-[11px] leading-none transition-colors ${anchorScopeFilter === value ? 'bg-secondary text-foreground' : 'text-muted-foreground hover:text-foreground'}`}
                    >
                      {label}
                    </button>
                  ))}
                </div>
              </div>
              <div className="mb-3 space-y-2">
                <Field label="锚点搜索">
                  <input
                    value={anchorQuery}
                    onChange={event => setAnchorQuery(event.target.value)}
                    className={inputClass}
                    placeholder="标题、作者、标签、路径或元数据"
                    aria-label="锚点搜索"
                  />
                </Field>
                <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
                  <Field label="授权">
                    <select
                      value={anchorLicenseFilter}
                      onChange={event => setAnchorLicenseFilter(event.target.value)}
                      className={inputClass}
                      aria-label="锚点授权筛选"
                    >
                      <option value="all">全部授权</option>
                      <option value="user_provided">user_provided</option>
                      <option value="licensed">licensed</option>
                      <option value="public_domain">public_domain</option>
                      <option value="unknown">unknown</option>
                    </select>
                  </Field>
                  <Field label="可见性">
                    <select
                      value={anchorVisibilityFilter}
                      onChange={event => setAnchorVisibilityFilter(event.target.value)}
                      className={inputClass}
                      aria-label="锚点可见性筛选"
                    >
                      <option value="all">全部可见性</option>
                      <option value="private">private</option>
                      <option value="workspace">workspace</option>
                      <option value="restricted">restricted</option>
                    </select>
                  </Field>
                  <Field label="可信度">
                    <select
                      value={anchorSourceTrustFilter}
                      onChange={event => setAnchorSourceTrustFilter(event.target.value)}
                      className={inputClass}
                      aria-label="锚点可信度筛选"
                    >
                      <option value="all">全部可信度</option>
                      <option value="user_verified">user_verified</option>
                      <option value="imported">imported</option>
                      <option value="unverified">unverified</option>
                    </select>
                  </Field>
                </div>
                {canClearAnchorFilters && (
                  <button
                    type="button"
                    onClick={() => {
                      setAnchorScopeFilter('all')
                      setAnchorQuery('')
                      setAnchorLicenseFilter('all')
                      setAnchorVisibilityFilter('all')
                      setAnchorSourceTrustFilter('all')
                    }}
                    className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80"
                  >
                    清除筛选
                  </button>
                )}
              </div>
              <div className="mb-3 flex flex-wrap items-center gap-2 rounded-md border border-border bg-background px-2.5 py-2">
                <span className="text-[11px] text-muted-foreground">
                  已选 {selectedAnchors.length} 项
                  {selectedAnchors.length > 0 && ` · 可提升 ${selectedNovelAnchors.length} · 可归档 ${selectedWorkspaceAnchors.length}`}
                </span>
                <div className="flex flex-wrap items-center gap-1">
                  <button
                    type="button"
                    onClick={() => setSelectedAnchorIds(selectedVisibleAnchorIds)}
                    disabled={loading || visibleAnchors.length === 0}
                    className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                  >
                    选择当前筛选
                  </button>
                  <button
                    type="button"
                    onClick={() => setSelectedAnchorIds([])}
                    disabled={loading || selectedAnchors.length === 0}
                    className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                  >
                    清除选择
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      void promoteSelectedAnchorsToWorkspaceCorpus()
                    }}
                    disabled={loading || selectedNovelAnchors.length === 0}
                    className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                  >
                    批量提升选中项
                  </button>
                  <button
                    type="button"
                    onClick={() => {
                      void archiveSelectedWorkspaceAnchors()
                    }}
                    disabled={loading || selectedWorkspaceAnchors.length === 0}
                    className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                  >
                    批量归档选中工作区
                  </button>
                </div>
              </div>
              {visibleAnchors.length === 0 ? (
                <p className="text-xs text-muted-foreground">{hasAnchorListFilters ? '没有匹配的参考锚点' : '暂无参考锚点'}</p>
              ) : (
                <div className="space-y-2">
                  {visibleAnchors.map(anchor => (
 <div key={anchor.anchor_id} data-anchor-id={anchor.anchor_id} data-testid="reference-anchor-row" className={`rounded-md border bg-background px-3 py-2 transition-colors ${focusedEvidenceAnchorId === anchor.anchor_id ? 'border-primary bg-primary/5' : 'border-border'}`}>
                      {editingAnchorId === anchor.anchor_id && anchorEditForm ? (
                        <div className="space-y-2">
                          <Field label="标题">
                            <input value={anchorEditForm.title} onChange={event => setAnchorEditForm(form => form ? ({ ...form, title: event.target.value }) : form)} className={inputClass} aria-label="编辑锚点标题" />
                          </Field>
                          <Field label="作者">
                            <input value={anchorEditForm.author} onChange={event => setAnchorEditForm(form => form ? ({ ...form, author: event.target.value }) : form)} className={inputClass} aria-label="编辑锚点作者" />
                          </Field>
                          <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
                            <Field label="授权">
                              <select value={anchorEditForm.licenseStatus} onChange={event => setAnchorEditForm(form => form ? ({ ...form, licenseStatus: event.target.value }) : form)} className={inputClass} aria-label="编辑锚点授权">
                                <option value="user_provided">user_provided</option>
                                <option value="licensed">licensed</option>
                                <option value="public_domain">public_domain</option>
                                <option value="unknown">unknown</option>
                              </select>
                            </Field>
                            <Field label="可见性">
                              <select value={anchorEditForm.visibility} onChange={event => setAnchorEditForm(form => form ? ({ ...form, visibility: event.target.value }) : form)} className={inputClass} aria-label="编辑锚点可见性">
                                <option value="private">private</option>
                                <option value="workspace">workspace</option>
                                <option value="restricted">restricted</option>
                              </select>
                            </Field>
                            <Field label="可信度">
                              <select value={anchorEditForm.sourceTrust} onChange={event => setAnchorEditForm(form => form ? ({ ...form, sourceTrust: event.target.value }) : form)} className={inputClass} aria-label="编辑锚点可信度">
                                <option value="user_verified">user_verified</option>
                                <option value="imported">imported</option>
                                <option value="unverified">unverified</option>
                              </select>
                            </Field>
                          </div>
                          <Field label="用户标签">
                            <input value={anchorEditForm.userTags} onChange={event => setAnchorEditForm(form => form ? ({ ...form, userTags: event.target.value }) : form)} className={inputClass} placeholder="分号分隔" aria-label="编辑锚点用户标签" />
                          </Field>
                          <div className="flex items-center gap-1">
                            <button
                              type="button"
                              onClick={() => {
                                void saveAnchorMetadata(anchor)
                              }}
                              disabled={loading}
                              className="inline-flex items-center gap-1.5 rounded bg-primary px-2.5 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50"
                            >
                              <Check className="h-3.5 w-3.5" />保存
                            </button>
                            <button
                              type="button"
                              onClick={cancelEditAnchor}
                              className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80"
                            >
                              <X className="h-3.5 w-3.5" />取消
                            </button>
                          </div>
                        </div>
                      ) : (
                        <div className="flex items-start gap-2">
                          <label className="flex min-w-0 flex-1 items-start gap-2">
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
                              <span className="block truncate text-[11px] text-muted-foreground">{anchor.visibility} · {anchor.source_trust} · {anchor.owner_scope}</span>
                              {anchor.user_tags.length > 0 && (
                                <span className="mt-1 flex flex-wrap gap-1">
                                  {anchor.user_tags.slice(0, 3).map(tag => (
                                    <span key={tag} className="rounded border border-border bg-secondary px-1.5 py-0.5 text-[10px] leading-none text-muted-foreground">{tag}</span>
                                  ))}
                                </span>
                              )}
                            </span>
                          </label>
                          <span className="flex shrink-0 items-center gap-1">
                            <button
                              type="button"
                              data-testid="reference-anchor-start-analysis-button"
                              onClick={() => {
                                void startCorpusAnalysis(anchor)
                              }}
                              disabled={loading || isFailedAnchorStatus(anchor.status)}
                              className="inline-flex items-center gap-1 rounded bg-primary px-2 py-1 text-[11px] leading-none text-primary-foreground hover:opacity-90 disabled:opacity-50"
                              title={isFailedAnchorStatus(anchor.status) ? '来源未准备完成，先查看处理记录' : '开始后台分析'}
                              aria-label={`开始分析 ${anchor.title}`}
                            >
                              <FileSearch className="h-3.5 w-3.5" />开始分析
                            </button>
                            <button
                              type="button"
                              onClick={() => {
                                void toggleAnchorMaterialPreview(anchor)
                              }}
                              disabled={loading}
                              className="rounded px-1.5 py-1 text-[11px] leading-none text-muted-foreground hover:text-foreground hover:bg-secondary disabled:opacity-50"
                              title="浏览材料"
                              aria-label={`浏览 ${anchor.title} 的材料`}
                            >
                              材料
                            </button>
                            <button
                              type="button"
                              data-testid="reference-source-processing-button"
                              onClick={() => {
                                void openSourceProcessingDetail(anchor.anchor_id)
                              }}
                              disabled={loading}
                              className="rounded px-1.5 py-1 text-[11px] leading-none text-muted-foreground hover:text-foreground hover:bg-secondary disabled:opacity-50"
                              title="处理记录"
                              aria-label={`查看 ${anchor.title} 的处理记录`}
                            >
                              记录
                            </button>
                            <button
                              type="button"
                              onClick={() => beginEditAnchor(anchor)}
                              disabled={loading}
                              className="rounded p-1 text-muted-foreground hover:text-foreground hover:bg-secondary disabled:opacity-50"
                              title="编辑元数据"
                              aria-label={`编辑 ${anchor.title} 元数据`}
                            >
                              <Edit3 className="h-3.5 w-3.5" />
                            </button>
                            {anchor.owner_scope === 'novel' && (
                              <button
                                type="button"
                                onClick={() => {
                                  void promoteAnchorToWorkspaceCorpus(anchor)
                                }}
                                disabled={loading}
                                className="rounded p-1 text-muted-foreground hover:text-foreground hover:bg-secondary disabled:opacity-50"
                                title="提升为工作区语料"
                                aria-label={`提升 ${anchor.title} 为工作区语料`}
                              >
                                <Share2 className="h-3.5 w-3.5" />
                              </button>
                            )}
                            <button
                              type="button"
                              onClick={() => {
                                void deleteOrArchiveAnchor(anchor)
                              }}
                              disabled={loading}
                              className="rounded p-1 text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
                              title={anchor.owner_scope === 'workspace_corpus' ? '归档为受限' : '删除'}
                              aria-label={anchor.owner_scope === 'workspace_corpus' ? `归档 ${anchor.title} 为受限语料` : `删除 ${anchor.title}`}
                            >
                              <Trash2 className="h-3.5 w-3.5" />
                            </button>
                            <button
                              type="button"
                              onClick={() => {
                                void rebuildAnchor(anchor.anchor_id)
                              }}
                              className="rounded p-1 text-muted-foreground hover:text-foreground hover:bg-secondary"
                              title="重建语料"
                              aria-label={`重建语料 ${anchor.title}，重新跑解析、切分、抽取和索引`}
                            >
                              <RefreshCcw className="h-3.5 w-3.5" />
                            </button>
                          </span>
                        </div>
                      )}
                      {expandedAnchorMaterialId === anchor.anchor_id && (
                        <div className="mt-3 border-t border-border pt-2">
                          <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
                            <span className="text-[11px] text-muted-foreground">
                              第 {anchorMaterialPreview.page} / {anchorMaterialPreview.totalPages} 页 · 共 {anchorMaterialPreview.total} 条
                            </span>
                            <span className="flex items-center gap-1">
                              <button
                                type="button"
                                onClick={() => {
                                  void loadAnchorMaterialPreview(anchor, Math.max(1, anchorMaterialPreview.page - 1))
                                }}
                                disabled={loading || anchorMaterialPreview.page <= 1}
                                className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                                aria-label={`浏览 ${anchor.title} 的上一页材料`}
                              >
                                上一页
                              </button>
                              <button
                                type="button"
                                onClick={() => {
                                  void loadAnchorMaterialPreview(anchor, Math.min(anchorMaterialPreview.totalPages, anchorMaterialPreview.page + 1))
                                }}
                                disabled={loading || anchorMaterialPreview.page >= anchorMaterialPreview.totalPages}
                                className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                                aria-label={`浏览 ${anchor.title} 的下一页材料`}
                              >
                                下一页
                              </button>
                            </span>
                          </div>
                          {anchorMaterialPreview.items.length === 0 ? (
                            <p className="text-[11px] text-muted-foreground">暂无可浏览材料</p>
                          ) : (
                            <>
                              <div className="mb-2 grid grid-cols-1 gap-2 sm:grid-cols-[minmax(0,1fr)_10rem]">
                                <Field label="材料筛选">
                                  <input
                                    value={anchorMaterialQuery}
                                    onChange={event => setAnchorMaterialQuery(event.target.value)}
                                    className={inputClass}
                                    placeholder="ID、文本或标签"
                                    aria-label="材料筛选"
                                  />
                                </Field>
                                <Field label="材料排序">
                                  <select
                                    value={anchorMaterialSort}
                                    onChange={event => setAnchorMaterialSort(event.target.value as MaterialPreviewSort)}
                                    className={inputClass}
                                    aria-label="材料排序"
                                  >
                                    <option value="default">默认顺序</option>
                                    <option value="score_desc">最高分优先</option>
                                    <option value="material_id_asc">材料 ID</option>
                                  </select>
                                </Field>
                              </div>
                              {visibleAnchorMaterialItems.length === 0 ? (
                                <p className="text-[11px] text-muted-foreground">{hasAnchorMaterialQuery ? '没有匹配材料' : '暂无可浏览材料'}</p>
                              ) : (
                                <>
                                  <div className="mb-2 space-y-2 rounded border border-border bg-background p-2">
                                    <div className="flex flex-wrap items-center justify-between gap-2">
                                      <span className="inline-flex items-center gap-1.5 text-[11px] font-medium text-foreground">
                                        <Tags className="h-3.5 w-3.5 text-muted-foreground" />
                                        已选 {selectedMaterialIds.length} 条材料
                                        {selectedVisibleMaterialCount !== selectedMaterialIds.length && selectedMaterialIds.length > 0
                                          ? ` · 当前页 ${selectedVisibleMaterialCount} 条`
                                          : ''}
                                      </span>
                                      <span className="flex flex-wrap items-center gap-1">
                                        <button
                                          type="button"
                                          onClick={() => setSelectedMaterialIds(visibleAnchorMaterialIds)}
                                          disabled={loading || visibleAnchorMaterialIds.length === 0}
                                          className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                                        >
                                          选择当前材料
                                        </button>
                                        <button
                                          type="button"
                                          onClick={() => setSelectedMaterialIds([])}
                                          disabled={loading || selectedMaterialIds.length === 0}
                                          className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                                        >
                                          清除材料选择
                                        </button>
                                      </span>
                                    </div>
                                    <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-5">
                                      <Field label="批量功能">
                                        <input value={bulkMaterialTagForm.functionTag} onChange={event => setBulkMaterialTagForm(form => ({ ...form, functionTag: event.target.value }))} className={inputClass} aria-label="批量材料功能标签" />
                                      </Field>
                                      <Field label="批量情绪">
                                        <input value={bulkMaterialTagForm.emotionTag} onChange={event => setBulkMaterialTagForm(form => ({ ...form, emotionTag: event.target.value }))} className={inputClass} aria-label="批量材料情绪标签" />
                                      </Field>
                                      <Field label="批量场景">
                                        <input value={bulkMaterialTagForm.sceneTag} onChange={event => setBulkMaterialTagForm(form => ({ ...form, sceneTag: event.target.value }))} className={inputClass} aria-label="批量材料场景标签" />
                                      </Field>
                                      <Field label="批量 POV">
                                        <input value={bulkMaterialTagForm.povTag} onChange={event => setBulkMaterialTagForm(form => ({ ...form, povTag: event.target.value }))} className={inputClass} aria-label="批量材料 POV 标签" />
                                      </Field>
                                      <Field label="批量技法">
                                        <input value={bulkMaterialTagForm.techniqueTag} onChange={event => setBulkMaterialTagForm(form => ({ ...form, techniqueTag: event.target.value }))} className={inputClass} aria-label="批量材料技法标签" />
                                      </Field>
                                    </div>
                                    <div className="flex flex-wrap items-center gap-1.5">
                                      <button
                                        type="button"
                                        onClick={() => {
                                          void saveBulkMaterialTags()
                                        }}
                                        disabled={loading || selectedMaterialIds.length === 0 || !hasBulkMaterialTagOverride}
                                        className="inline-flex items-center gap-1.5 rounded bg-primary px-2.5 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50"
                                      >
                                        <Check className="h-3.5 w-3.5" />批量保存标签
                                      </button>
                                      <button
                                        type="button"
                                        onClick={() => setBulkMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)}
                                        disabled={loading || !hasBulkMaterialTagOverride}
                                        className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
                                      >
                                        <X className="h-3.5 w-3.5" />清空批量标签
                                      </button>
                                      <span className="text-[11px] text-muted-foreground">仅覆盖已填写字段</span>
                                    </div>
                                  </div>
                                  <div className="space-y-2" aria-label={`${anchor.title} 材料预览`}>
                                    {visibleAnchorMaterialItems.map(material => (
                                      <div key={material.material_id} data-testid="reference-anchor-material-card" className="rounded border border-border bg-card px-2.5 py-2">
                                        <div className="flex flex-wrap items-center justify-between gap-2">
                                          <label className="flex min-w-0 flex-1 items-center gap-2">
                                            <input
                                              type="checkbox"
                                              checked={selectedMaterialSet.has(material.material_id)}
                                              onChange={event => toggleMaterialSelection(material.material_id, event.target.checked)}
                                              className="shrink-0"
                                              aria-label={`选择 ${material.material_id} 做批量标签校正`}
                                            />
                                            <span className="min-w-0 truncate text-[11px] text-muted-foreground">
                                              {material.material_id} · {material.material_type} · {material.function_tag || 'untagged'} · {material.pov_tag || 'unknown'}
                                            </span>
                                          </label>
                                          <span className="flex shrink-0 items-center gap-1">
                                            {material.user_verified && <span className="text-[11px] text-emerald-600 dark:text-emerald-400">已校正</span>}
                                            <button
                                              type="button"
                                              onClick={() => {
                                                void openMaterialDetail(material.material_id)
                                              }}
                                              disabled={materialDetailLoading && materialDetailId === material.material_id}
                                              className="rounded px-1.5 py-1 text-[11px] leading-none text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
                                              aria-label={`查看 ${material.material_id} 的材料明细`}
                                            >
                                              明细
                                            </button>
                                            <button
                                              type="button"
                                              onClick={() => beginEditMaterialTags(material)}
                                              disabled={loading}
                                              className="rounded px-1.5 py-1 text-[11px] leading-none text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
                                              aria-label={`校正 ${material.material_id} 的材料标签`}
                                            >
                                              校正
                                            </button>
                                          </span>
                                        </div>
                                        <MaterialListPreview text={material.text_preview} truncated={material.text_truncated} />
                                        <p className="mt-1 break-all text-[11px] leading-relaxed text-muted-foreground">
                                          来源 {material.source_segment_id} · {material.source_hash}
                                        </p>
                                        {editingMaterialId === material.material_id && materialTagForm && (
                                          <div className="mt-2 space-y-2 rounded border border-border bg-background p-2">
                                            <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                                              <Field label="功能">
                                                <input value={materialTagForm.functionTag} onChange={event => setMaterialTagForm(form => form ? ({ ...form, functionTag: event.target.value }) : form)} className={inputClass} aria-label="材料功能标签" />
                                              </Field>
                                              <Field label="情绪">
                                                <input value={materialTagForm.emotionTag} onChange={event => setMaterialTagForm(form => form ? ({ ...form, emotionTag: event.target.value }) : form)} className={inputClass} aria-label="材料情绪标签" />
                                              </Field>
                                              <Field label="场景">
                                                <input value={materialTagForm.sceneTag} onChange={event => setMaterialTagForm(form => form ? ({ ...form, sceneTag: event.target.value }) : form)} className={inputClass} aria-label="材料场景标签" />
                                              </Field>
                                              <Field label="POV">
                                                <input value={materialTagForm.povTag} onChange={event => setMaterialTagForm(form => form ? ({ ...form, povTag: event.target.value }) : form)} className={inputClass} aria-label="材料 POV 标签" />
                                              </Field>
                                              <Field label="技法">
                                                <input value={materialTagForm.techniqueTag} onChange={event => setMaterialTagForm(form => form ? ({ ...form, techniqueTag: event.target.value }) : form)} className={inputClass} aria-label="材料技法标签" />
                                              </Field>
                                            </div>
                                            <div className="flex items-center gap-1">
                                              <button
                                                type="button"
                                                onClick={() => {
                                                  void saveMaterialTags(material)
                                                }}
                                                disabled={loading}
                                                className="inline-flex items-center gap-1.5 rounded bg-primary px-2.5 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50"
                                              >
                                                <Check className="h-3.5 w-3.5" />保存标签
                                              </button>
                                              <button
                                                type="button"
                                                onClick={cancelEditMaterialTags}
                                                className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80"
                                              >
                                                <X className="h-3.5 w-3.5" />取消
                                              </button>
                                            </div>
                                          </div>
                                        )}
                                        {materialScoreComponents(material).length > 0 && (
                                          <div className="mt-2 flex flex-wrap gap-1">
                                            {materialScoreComponents(material).slice(0, 4).map(([name, value]) => (
                                              <span key={name} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                                                {name} {value.toFixed(2)}
                                              </span>
                                            ))}
                                          </div>
                                        )}
                                      </div>
                                    ))}
                                  </div>
                                </>
                              )}
                            </>
                          )}
                        </div>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </div>
            </>
            )}

{activeCorpusTab === 'analysis_results' && (
<CorpusAnalysisLibraryTab novelId={novelId} anchors={anchors} />
)}
 {activeCorpusTab === 'analysis_jobs' && <CorpusAnalysisJobsPanel novelId={novelId} />}

 {activeCorpusTab === 'governance' && (
 <CorpusGovernancePanel novelId={novelId} />
 )}

            {(activeCorpusTab === 'materials' || activeCorpusTab === 'tag_review') && (
            <>
            <div data-testid="reference-material-library" className="rounded-lg border border-border bg-card p-4">
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <Tags className="h-3.5 w-3.5 text-muted-foreground" />
                  <h3 className="text-xs font-semibold text-foreground">材料库</h3>
                </div>
                {materialLibrary.total > 0 && (
                  <span className="text-[11px] text-muted-foreground">
                    第 {materialLibrary.page} / {materialLibrary.totalPages} 页 · {materialLibrary.total} 条材料
                  </span>
                )}
              </div>
              <div className="space-y-2">
                <div className="flex flex-wrap items-end gap-2">
                  <div className="min-w-[180px] flex-1">
                    <Field label="材料库搜索">
                      <input
                        value={materialLibraryQuery}
                        onChange={event => setMaterialLibraryQuery(event.target.value)}
                        className={inputClass}
                        placeholder="ID、文本、标签或写作需求"
                        aria-label="材料库搜索"
                      />
                    </Field>
                  </div>
                  <div className="w-32">
                    <Field label="材料状态">
                      <select
                        value={materialLibraryArchiveFilter}
                        onChange={event => changeMaterialLibraryArchiveFilter(event.target.value as MaterialArchiveFilter)}
                        className={inputClass}
                        aria-label="材料状态"
                      >
                        <option value="active">当前材料</option>
                        <option value="archived">已归档</option>
                      </select>
                    </Field>
                  </div>
                  <button
                    type="button"
                    onClick={() => {
                      void searchMaterialLibrary(1, true)
                    }}
                    disabled={loading}
                    className="inline-flex items-center gap-1.5 rounded bg-secondary px-3 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
                  >
                    <Search className="h-3.5 w-3.5" />检索材料库
                  </button>
                </div>
                <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                  <Field label="材料库文体职责">
                    <input value={materialLibraryFilters.proseDuties} onChange={event => setMaterialLibraryFilters(filters => ({ ...filters, proseDuties: event.target.value }))} className={inputClass} placeholder="source_backed_detail；subtext" aria-label="材料库文体职责" />
                  </Field>
                  <Field label="材料库功能">
                    <input value={materialLibraryFilters.functionTags} onChange={event => setMaterialLibraryFilters(filters => ({ ...filters, functionTags: event.target.value }))} className={inputClass} placeholder="environment；emotion_evidence" aria-label="材料库功能标签" />
                  </Field>
                  <Field label="材料库情绪">
                    <input value={materialLibraryFilters.emotionTags} onChange={event => setMaterialLibraryFilters(filters => ({ ...filters, emotionTags: event.target.value }))} className={inputClass} placeholder="restrained" aria-label="材料库情绪标签" />
                  </Field>
                  <Field label="材料库 POV">
                    <input value={materialLibraryFilters.povTags} onChange={event => setMaterialLibraryFilters(filters => ({ ...filters, povTags: event.target.value }))} className={inputClass} placeholder="close" aria-label="材料库 POV 标签" />
                  </Field>
                </div>
              </div>

              {materialLibraryArchiveFilter === 'archived' && activeCorpusTab === 'tag_review' ? (
                <div data-testid="reference-material-tag-review-queue" className="mt-3 rounded border border-border bg-background p-2">
                  <p className="text-xs leading-relaxed text-muted-foreground">
                    已归档材料不进入标签校正队列；切回“当前材料”后可继续处理服务端待校正项。
                  </p>
                </div>
              ) : (activeCorpusTab === 'tag_review' || materialTagReviewQueue.total > 0 || materialTagReviewQueue.items.length > 0) && (
                <div data-testid="reference-material-tag-review-queue" className="mt-3 space-y-2 rounded border border-border bg-background p-2">
                  <div className="flex flex-wrap items-center justify-between gap-2">
                    <div className="flex min-w-0 items-center gap-2">
                      <Tags className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                      <h4 className="text-xs font-semibold text-foreground">标签校正</h4>
                      <span className="text-[11px] text-muted-foreground">
                        待校正 {materialTagReviewQueue.total} · 队列第 {materialTagReviewQueue.page} / {materialTagReviewQueue.totalPages} 页
                      </span>
                    </div>
                    <button
                      type="button"
                      onClick={() => {
                        void loadMaterialTagReviewQueue(materialTagReviewQueue.page)
                      }}
                      disabled={loading}
                      className="inline-flex items-center gap-1 rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                    >
                      <RefreshCcw className="h-3.5 w-3.5" />刷新队列
                    </button>
                  </div>
                  {materialTagReviewQueue.items.length === 0 ? (
                    <p className="text-xs leading-relaxed text-muted-foreground">
                      {loading ? '正在加载服务端标签校正队列...' : '服务端队列暂无未校正、低置信或 unknown 标签材料；材料仍可在材料库中检索并查看明细。'}
                    </p>
                  ) : (
                    <>
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <p className="text-xs leading-relaxed text-muted-foreground">
                          队列由服务端跨页计算；高置信材料留在材料库结果中，不阻塞章节推荐。
                        </p>
                        <div className="flex shrink-0 items-center gap-2">
                          <span className="text-[11px] text-muted-foreground">
                            已选 {selectedTagReviewQueueCount} / {materialTagReviewQueue.items.length}
                          </span>
                          <button
                            type="button"
                            onClick={() => setSelectedLibraryMaterialIds(ids => Array.from(new Set([...ids, ...tagReviewQueueIds])))}
                            disabled={loading || tagReviewQueueIds.length === 0}
                            className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                          >
                            选择当前队列
                          </button>
                        </div>
                      </div>
                      <div className="grid grid-cols-1 gap-2 lg:grid-cols-2">
                        {materialTagReviewQueue.items.map(({ material, issues }) => (
                          <div key={`review-${material.material_id}`} data-testid="reference-material-tag-review-item" className="rounded border border-border bg-card px-2.5 py-2">
                            <div className="flex flex-wrap items-center justify-between gap-2">
                              <span className="min-w-0 truncate text-[11px] font-medium text-foreground">
                                {material.material_id} · {material.function_tag || 'untagged'} · {material.pov_tag || 'unknown'}
                              </span>
                              <span className="flex shrink-0 items-center gap-1">
                                <button
                                  type="button"
                                  onClick={() => {
                                    void openMaterialDetail(material.material_id)
                                  }}
                                  disabled={materialDetailLoading && materialDetailId === material.material_id}
                                  className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                                >
                                  明细
                                </button>
                                <button
                                  type="button"
                                  onClick={() => setSelectedLibraryMaterialIds(ids => ids.includes(material.material_id) ? ids : [...ids, material.material_id])}
                                  disabled={loading || selectedLibraryMaterialSet.has(material.material_id)}
                                  className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                                >
                                  选择
                                </button>
                              </span>
                            </div>
                            <div className="mt-1 flex flex-wrap gap-1">
                              {issues.map(issue => (
                                <span key={`${issue.code}-${issue.label}`} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                                  {issue.label}
                                </span>
                              ))}
                            </div>
                            <p className="mt-1 text-[11px] text-muted-foreground">
                              置信度 功能 {formatConfidence(material.function_confidence)} · 情绪 {formatConfidence(material.emotion_confidence)} · POV {formatConfidence(material.pov_confidence)}
                            </p>
                            <MaterialListPreview text={material.text_preview} truncated={material.text_truncated} />
                          </div>
                        ))}
                      </div>
                      <div className="flex items-center justify-between gap-2">
                        <button
                          type="button"
                          onClick={() => {
                            void loadMaterialTagReviewQueue(Math.max(1, materialTagReviewQueue.page - 1))
                          }}
                          disabled={loading || materialTagReviewQueue.page <= 1}
                          className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                        >
                          上一页队列
                        </button>
                        <button
                          type="button"
                          onClick={() => {
                            void loadMaterialTagReviewQueue(Math.min(materialTagReviewQueue.totalPages, materialTagReviewQueue.page + 1))
                          }}
                          disabled={loading || materialTagReviewQueue.page >= materialTagReviewQueue.totalPages}
                          className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                        >
                          下一页队列
                        </button>
                      </div>
                    </>
                  )}
                </div>
              )}

              {(materialLibrary.items.length > 0 || selectedLibraryMaterialIds.length > 0) ? (
                <div className="mt-3 space-y-3">
                  <div className="grid grid-cols-1 gap-2 sm:grid-cols-[minmax(0,1fr)_10rem]">
                    <Field label="材料库页内筛选">
                      <input
                        value={materialLibraryPageQuery}
                        onChange={event => setMaterialLibraryPageQuery(event.target.value)}
                        className={inputClass}
                        placeholder="ID、文本或标签"
                        aria-label="材料库页内筛选"
                      />
                    </Field>
                    <Field label="材料库排序">
                      <select
                        value={materialLibrarySort}
                        onChange={event => setMaterialLibrarySort(event.target.value as MaterialPreviewSort)}
                        className={inputClass}
                        aria-label="材料库排序"
                      >
                        <option value="default">默认顺序</option>
                        <option value="score_desc">最高分优先</option>
                        <option value="material_id_asc">材料 ID</option>
                      </select>
                    </Field>
                  </div>
                  {visibleMaterialLibraryItems.length === 0 && (
                    <p className="text-[11px] text-muted-foreground">{hasMaterialLibraryPageQuery ? '没有匹配材料' : '暂无可浏览材料'}</p>
                  )}
                  {(visibleMaterialLibraryItems.length > 0 || selectedLibraryMaterialIds.length > 0) && (
                    <div className="space-y-2 rounded border border-border bg-background p-2">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <span className="text-[11px] font-medium text-foreground">
                          已选 {selectedLibraryMaterialIds.length} 条材料
                          {selectedVisibleLibraryMaterialCount !== selectedLibraryMaterialIds.length && selectedLibraryMaterialIds.length > 0
                            ? ` · 当前结果 ${selectedVisibleLibraryMaterialCount} 条`
                            : ''}
                        </span>
                        <span className="flex flex-wrap items-center gap-1">
                          <button
                            type="button"
                            onClick={() => setSelectedLibraryMaterialIds(ids => Array.from(new Set([...ids, ...visibleMaterialLibraryIds])))}
                            disabled={loading || visibleMaterialLibraryIds.length === 0}
                            className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                          >
                            选择当前材料
                          </button>
                          <button
                            type="button"
                            onClick={() => setSelectedLibraryMaterialIds([])}
                            disabled={loading || selectedLibraryMaterialIds.length === 0}
                            className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                          >
                            清除材料选择
                          </button>
                        </span>
                      </div>
                      <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                        <Field label="材料库批量功能">
                          <input value={bulkLibraryMaterialTagForm.functionTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, functionTag: event.target.value }))} className={inputClass} aria-label="材料库批量功能标签" disabled={materialLibraryArchiveFilter === 'archived'} />
                        </Field>
                        <Field label="材料库批量情绪">
                          <input value={bulkLibraryMaterialTagForm.emotionTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, emotionTag: event.target.value }))} className={inputClass} aria-label="材料库批量情绪标签" disabled={materialLibraryArchiveFilter === 'archived'} />
                        </Field>
                        <Field label="材料库批量场景">
                          <input value={bulkLibraryMaterialTagForm.sceneTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, sceneTag: event.target.value }))} className={inputClass} aria-label="材料库批量场景标签" disabled={materialLibraryArchiveFilter === 'archived'} />
                        </Field>
                        <Field label="材料库批量 POV">
                          <input value={bulkLibraryMaterialTagForm.povTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, povTag: event.target.value }))} className={inputClass} aria-label="材料库批量 POV 标签" disabled={materialLibraryArchiveFilter === 'archived'} />
                        </Field>
                        <Field label="材料库批量技法">
                          <input value={bulkLibraryMaterialTagForm.techniqueTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, techniqueTag: event.target.value }))} className={inputClass} aria-label="材料库批量技法标签" disabled={materialLibraryArchiveFilter === 'archived'} />
                        </Field>
                      </div>
                      <div className="flex flex-wrap items-center gap-1.5">
                        <button
                          type="button"
                          onClick={() => {
                            void saveBulkLibraryMaterialTags()
                          }}
                          disabled={loading || materialLibraryArchiveFilter === 'archived' || selectedLibraryMaterialIds.length === 0 || !hasBulkLibraryMaterialTagOverride}
                          className="inline-flex items-center gap-1.5 rounded bg-primary px-2.5 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50"
                        >
                          <Check className="h-3.5 w-3.5" />批量校正材料库
                        </button>
                        <button
                          type="button"
                          onClick={() => setBulkLibraryMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)}
                          disabled={loading || materialLibraryArchiveFilter === 'archived' || !hasBulkLibraryMaterialTagOverride}
                          className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
                        >
                          <X className="h-3.5 w-3.5" />清空批量标签
                        </button>
                        {materialLibraryArchiveFilter === 'archived' ? (
                          <button
                            type="button"
                            onClick={() => {
                              void restoreSelectedLibraryMaterials()
                            }}
                            disabled={loading || selectedLibraryMaterialIds.length === 0}
                            className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
                          >
                            <RefreshCcw className="h-3.5 w-3.5" />恢复所选材料
                          </button>
                        ) : (
                          <button
                            type="button"
                            onClick={() => {
                              void archiveSelectedLibraryMaterials()
                            }}
                            disabled={loading || selectedLibraryMaterialIds.length === 0}
                            className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
                          >
                            <Archive className="h-3.5 w-3.5" />归档所选材料
                          </button>
                        )}
                      </div>
                    </div>
                  )}
                  {visibleMaterialLibraryItems.length > 0 && (
                    <div className="space-y-2" aria-label="材料库结果">
                      {visibleMaterialLibraryItems.map(material => (
                        <div key={material.material_id} data-testid="reference-material-library-card" className="rounded border border-border bg-background px-2.5 py-2">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <label className="flex min-w-0 flex-1 items-center gap-2">
                              <input
                                type="checkbox"
                                checked={selectedLibraryMaterialSet.has(material.material_id)}
                                onChange={event => toggleLibraryMaterialSelection(material.material_id, event.target.checked)}
                                className="shrink-0"
                                aria-label={`选择材料库材料 ${material.material_id} 做批量标签校正`}
                              />
                              <span className="min-w-0 truncate text-[11px] text-muted-foreground">
                                {material.material_id} · {material.material_type} · {material.function_tag || 'untagged'} · {material.pov_tag || 'unknown'}
                              </span>
                            </label>
                            <span className="flex shrink-0 items-center gap-1">
                              {material.user_verified && <span className="text-[11px] text-emerald-600 dark:text-emerald-400">已校正</span>}
                              <button
                                type="button"
                                onClick={() => {
                                  void openMaterialDetail(material.material_id)
                                }}
                                disabled={materialDetailLoading && materialDetailId === material.material_id}
                                className="rounded px-1.5 py-1 text-[11px] leading-none text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
                                aria-label={`查看 ${material.material_id} 的材料明细`}
                              >
                                明细
                              </button>
                            </span>
                          </div>
                          <MaterialListPreview text={material.text_preview} truncated={material.text_truncated} />
                          <p className="mt-1 break-all text-[11px] leading-relaxed text-muted-foreground">
                            来源 {material.source_segment_id} · {material.source_hash}
                          </p>
                          {materialScoreComponents(material).length > 0 && (
                            <div className="mt-2 flex flex-wrap gap-1">
                              {materialScoreComponents(material).slice(0, 4).map(([name, value]) => (
                                <span key={name} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                                  {name} {value.toFixed(2)}
                                </span>
                              ))}
                            </div>
                          )}
                        </div>
                      ))}
                    </div>
                  )}
                  <div className="flex items-center justify-between gap-2">
                    <button
                      type="button"
                      onClick={() => {
                        void searchMaterialLibrary(Math.max(1, materialLibrary.page - 1))
                      }}
                      disabled={loading || materialLibrary.page <= 1}
                      className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                    >
                      上一页
                    </button>
                    <button
                      type="button"
                      onClick={() => {
                        void searchMaterialLibrary(Math.min(materialLibrary.totalPages, materialLibrary.page + 1))
                      }}
                      disabled={loading || materialLibrary.page >= materialLibrary.totalPages}
                      className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                    >
                      下一页
                    </button>
                  </div>
                </div>
              ) : (
                <p className="mt-3 text-[11px] text-muted-foreground">输入检索条件后查看可访问材料；默认不需要先选择库条目。</p>
              )}
            </div>
            </>
            )}

            {activeCorpusTab === 'style_profiles' && (
            <StyleProfileLibraryPanel
              novelId={novelId}
              anchors={anchors}
              selectedAnchorIds={selectedAnchorIds}
              onProfilesChanged={loadStyleProfiles}
            />
            )}

            {activeCorpusTab === 'processing_records' && (
            <div data-testid="reference-processing-records-tab" className="rounded-lg border border-border bg-card p-4">
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <FileSearch className="h-3.5 w-3.5 text-muted-foreground" />
                  <h3 className="text-xs font-semibold text-foreground">处理记录</h3>
                </div>
                <button
                  type="button"
                  onClick={() => { void loadAnchors() }}
                  disabled={loading}
                  className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
                >
                  <RefreshCcw className="h-3.5 w-3.5" />刷新状态
                </button>
              </div>
              {anchors.length === 0 ? (
                <p className="text-xs text-muted-foreground">暂无语料来源。先在“素材来源”导入文件后，可在这里查看解析、切分、抽取、索引和重建记录。</p>
              ) : (
                <div className="space-y-2" aria-label="来源处理记录">
                  {anchors.map(anchor => (
                    <div key={`processing-${anchor.anchor_id}`} className="rounded border border-border bg-background px-3 py-2">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <div className="min-w-0">
                          <p className="truncate text-xs font-medium text-foreground">{anchor.title}</p>
                          <p className="mt-0.5 text-[11px] text-muted-foreground">
                            {anchor.owner_scope} · {anchor.visibility} · {anchor.source_trust} · hash {anchor.source_file_hash || 'n/a'}
                          </p>
                        </div>
                        <div className="flex shrink-0 items-center gap-1">
                          <span className={`rounded bg-secondary px-2 py-1 text-[11px] ${statusTone(anchor.status)}`}>{anchor.status}</span>
                          <button
                            type="button"
                            data-testid="reference-processing-record-detail-button"
                            onClick={() => { void openSourceProcessingDetail(anchor.anchor_id) }}
                            disabled={loading}
                            className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                            title="查看处理详情"
                            aria-label={`查看 ${anchor.title} 的处理详情与失败记录`}
                          >
                            处理详情
                          </button>
                          <button
                            type="button"
                            onClick={() => { void rebuildAnchor(anchor.anchor_id) }}
                            disabled={loading}
                            className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80 disabled:opacity-50"
                            aria-label={`重建语料 ${anchor.title}，重新跑解析、切分、抽取和索引`}
                          >
                            重建语料
                          </button>
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
            )}

            {activeCorpusTab === 'advanced' && (
            <div data-testid="reference-corpus-advanced-tab" className="rounded-lg border border-border bg-card p-4">
              <div className="flex items-center gap-2">
                <SlidersHorizontal className="h-3.5 w-3.5 text-muted-foreground" />
                <h3 className="text-xs font-semibold text-foreground">高级</h3>
              </div>
              <p className="mt-2 text-xs leading-relaxed text-muted-foreground">
                素材库高级区只保留语料管理相关能力。章节蓝图、材料绑定、候选生成和插入审批已迁移到章节正文里的“参考素材”面板，避免语料处理流程误触发章节写作。
              </p>
            </div>
            )}
            </div>
          </section>

          {showLegacyChapterReference && (
          <section className="min-w-0 space-y-4" aria-labelledby="reference-retrieval-heading">
            <div className="space-y-1">
              <div className="flex items-center gap-2">
                <Search className="h-3.5 w-3.5 text-muted-foreground" />
                <h3 id="reference-retrieval-heading" className="text-xs font-semibold text-foreground">参考写作检索</h3>
              </div>
              <p className="text-xs leading-relaxed text-muted-foreground">
                默认编排会按章节目标、事实边界和检索策略选择可用材料；手工搜索与蓝图调试保留在高级模式。
              </p>
            </div>

            <OrchestrationPanel
              chapterNumber={blueprintForm.chapterNumber}
              chapterGoal={blueprintForm.chapterGoal}
              knownFacts={blueprintForm.knownFacts}
              forbiddenFacts={blueprintForm.forbiddenFacts}
              useSelectedAnchors={orchestrationUseSelectedAnchors}
              selectedAnchorCount={selectedAnchorIds.length}
              styleProfiles={activeStyleProfiles}
              styleProfileId={orchestrationEffectiveStyleProfileId}
              styleIntensity={orchestrationStyleIntensity}
              styleMinFit={orchestrationStyleMinFit}
              styleAllowedCloseness={orchestrationStyleAllowedCloseness}
              styleDimensions={orchestrationStyleDimensions}
              styleRequiredEvidenceTypes={orchestrationStyleRequiredEvidenceTypes}
              styleForbiddenRisks={orchestrationStyleForbiddenRisks}
              runs={orchestrationRuns}
              activeRun={activeOrchestrationRun}
              events={orchestrationEvents}
              loading={loading}
              onChapterNumberChange={value => setBlueprintForm(form => ({ ...form, chapterNumber: value }))}
              onChapterGoalChange={value => setBlueprintForm(form => ({ ...form, chapterGoal: value }))}
              onKnownFactsChange={value => setBlueprintForm(form => ({ ...form, knownFacts: value }))}
              onForbiddenFactsChange={value => setBlueprintForm(form => ({ ...form, forbiddenFacts: value }))}
              onUseSelectedAnchorsChange={setOrchestrationUseSelectedAnchors}
              onStyleProfileIdChange={changeOrchestrationStyleProfile}
              onStyleIntensityChange={changeOrchestrationStyleIntensity}
              onStyleMinFitChange={setOrchestrationStyleMinFit}
              onStyleAllowedClosenessChange={setOrchestrationStyleAllowedCloseness}
              onStyleDimensionsChange={setOrchestrationStyleDimensions}
              onStyleRequiredEvidenceTypesChange={setOrchestrationStyleRequiredEvidenceTypes}
              onStyleForbiddenRisksChange={setOrchestrationStyleForbiddenRisks}
              onRefreshStyleProfiles={loadStyleProfiles}
              onStart={startOrchestration}
              onSelectRun={selectOrchestrationRun}
              onRefresh={loadOrchestrationRuns}
              onResume={resumeOrchestration}
              onCancel={cancelOrchestration}
            />

            <div className="rounded-lg border border-border bg-card p-4">
              <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <SlidersHorizontal className="h-3.5 w-3.5 text-muted-foreground" />
                    <h3 className="text-xs font-semibold text-foreground">高级模式</h3>
                  </div>
                  <p className="mt-1 max-w-2xl text-xs leading-relaxed text-muted-foreground">
                    默认使用上方编排流程；打开后可手动搜索材料、生成/修订/评审/批准蓝图、绑定材料和生成候选。
                  </p>
                </div>
                <button
                  type="button"
                  onClick={() => setAdvancedMode(value => !value)}
                  aria-pressed={advancedMode}
                  className={`${advancedMode ? 'bg-primary text-primary-foreground hover:opacity-90' : 'bg-secondary text-foreground hover:bg-secondary/80'} inline-flex items-center gap-1.5 rounded px-3 py-1.5 text-xs font-medium disabled:opacity-50`}
                >
                  <SlidersHorizontal className="h-3.5 w-3.5" />
                  {advancedMode ? '关闭高级模式' : '打开高级模式'}
                </button>
              </div>

              {advancedMode && (
                <div className="mt-4 space-y-4">
                  <div data-testid="reference-manual-material-search" className="rounded-md border border-border bg-background p-3">
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
                    <div className="mt-3 grid grid-cols-1 gap-2 md:grid-cols-2 xl:grid-cols-4">
                      <Field label="叙事职责">
                        <input value={materialFilters.narrativeDuties} onChange={event => setMaterialFilters(filters => ({ ...filters, narrativeDuties: event.target.value }))} className={inputClass} placeholder="external_evidence；transition" />
                      </Field>
                      <Field label="情绪转变">
                        <input value={materialFilters.emotionTransitions} onChange={event => setMaterialFilters(filters => ({ ...filters, emotionTransitions: event.target.value }))} className={inputClass} placeholder="neutral->pressure" />
                      </Field>
                      <Field label="文体职责">
                        <input value={materialFilters.proseDuties} onChange={event => setMaterialFilters(filters => ({ ...filters, proseDuties: event.target.value }))} className={inputClass} placeholder="source_backed_detail；subtext" />
                      </Field>
                      <Field label="材料类型">
                        <input value={materialFilters.materialTypes} onChange={event => setMaterialFilters(filters => ({ ...filters, materialTypes: event.target.value }))} className={inputClass} placeholder="sentence；passage" />
                      </Field>
                      <Field label="功能标签">
                        <input value={materialFilters.functionTags} onChange={event => setMaterialFilters(filters => ({ ...filters, functionTags: event.target.value }))} className={inputClass} placeholder="emotion_evidence" />
                      </Field>
                      <Field label="情绪标签">
                        <input value={materialFilters.emotionTags} onChange={event => setMaterialFilters(filters => ({ ...filters, emotionTags: event.target.value }))} className={inputClass} placeholder="restrained" />
                      </Field>
                      <Field label="POV 标签">
                        <input value={materialFilters.povTags} onChange={event => setMaterialFilters(filters => ({ ...filters, povTags: event.target.value }))} className={inputClass} placeholder="limited；close" />
                      </Field>
                      <Field label="技法标签">
                        <input value={materialFilters.techniqueTags} onChange={event => setMaterialFilters(filters => ({ ...filters, techniqueTags: event.target.value }))} className={inputClass} placeholder="afterbeat" />
                      </Field>
                    </div>
                    {materials.length > 0 && (
                      <div className="mt-3 grid grid-cols-1 lg:grid-cols-2 gap-2">
                        {materials.map(material => (
                          <div key={material.material_id} data-testid="reference-manual-material-card" className="rounded-md border border-border bg-card p-3">
                            <div className="flex items-center justify-between gap-2">
                              <span className="min-w-0 truncate text-[11px] text-muted-foreground">
                                {material.material_type} · {material.function_tag || 'untagged'} · {material.emotion_tag || 'neutral'}
                              </span>
                              {material.user_verified && <span className="shrink-0 text-[11px] text-emerald-600 dark:text-emerald-400">已校正</span>}
                            </div>
                            <div className="mt-1 flex flex-wrap gap-1">
                              {[material.pov_tag, material.technique_tag, material.scene_tag].filter(Boolean).map(tag => (
                                <span key={tag} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">{tag}</span>
                              ))}
                            </div>
                            <MaterialListPreview text={material.text_preview} truncated={material.text_truncated} />
                            {materialScoreComponents(material).length > 0 && (
                              <div className="mt-2 flex flex-wrap gap-1">
                                {materialScoreComponents(material).map(([name, value]) => (
                                  <span key={name} className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                                    {name} {value.toFixed(2)}
                                  </span>
                                ))}
                              </div>
                            )}
                          </div>
                        ))}
                      </div>
                    )}
                  </div>

                  <div className="grid grid-cols-1 2xl:grid-cols-[360px_minmax(0,1fr)] gap-4">
                    <div data-testid="reference-blueprint-panel" className="rounded-md border border-border bg-background p-4">
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
                              className={`w-full rounded-md border px-3 py-2 text-left transition-colors ${activeBlueprint?.blueprint_id === blueprint.blueprint_id ? 'border-primary bg-secondary' : 'border-border bg-card hover:bg-secondary/60'}`}
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
                </div>
              )}
            </div>
          </section>
          )}
        </div>
        {materialDetailId && (
          <MaterialDetailDrawer
            materialId={materialDetailId}
            detail={materialDetail}
            loading={materialDetailLoading}
            error={materialDetailError}
            onClose={closeMaterialDetail}
            onRetry={() => {
              void openMaterialDetail(materialDetailId)
            }}
          />
        )}
        {sourceSegmentDetailKey && (
          <SourceSegmentDetailDrawer
            anchorId={sourceSegmentDetailKey.anchorId}
            segmentId={sourceSegmentDetailKey.segmentId}
            detail={sourceSegmentDetail}
            loading={sourceSegmentDetailLoading}
            error={sourceSegmentDetailError}
            onClose={closeSourceSegmentDetail}
            onRetry={() => {
              void openSourceSegmentDetail(sourceSegmentDetailKey.anchorId, sourceSegmentDetailKey.segmentId)
            }}
          />
        )}
        {sourceProcessingAnchorId !== null && (
          <SourceProcessingDrawer
            anchorId={sourceProcessingAnchorId}
            detail={sourceProcessingDetail}
            loading={sourceProcessingLoading}
            error={sourceProcessingError}
            onClose={closeSourceProcessingDetail}
            onLocateSource={locateAffectedSource}
            onLocateMaterial={(materialId) => {
              void locateAffectedMaterial(materialId)
            }}
            onOpenMaterialDetail={openAffectedMaterialDetail}
            onOpenSourceSegmentDetail={openAffectedSourceSegmentDetail}
            onRetry={() => {
              void openSourceProcessingDetail(sourceProcessingAnchorId)
            }}
            onRebuild={() => {
              void (async () => {
                await rebuildAnchor(sourceProcessingAnchorId)
                await openSourceProcessingDetail(sourceProcessingAnchorId)
              })()
            }}
          />
        )}
      </div>
    </main>
  )
}

function sourceProcessingDiagnosticText(detail: reference.SourceProcessingDetail): string {
  const lines = [
    `处理记录: ${detail.source.title}`,
    `source=${detail.source.anchor_id} owner=${detail.source.owner_scope} visibility=${detail.source.visibility} trust=${detail.source.source_trust}`,
  ]

  if (detail.current_status) {
    const status = detail.current_status
    lines.push(
      `current=${status.stage}/${status.status}`,
      `counts=segments:${status.source_segment_count} materials:${status.material_count} slots:${status.slot_count} vectors:${status.vector_count}`,
      `diagnostic=${status.diagnostic || 'n/a'}`,
    )
  }

  if (detail.current_attempt) {
    const attempt = detail.current_attempt
    lines.push(
      `current_attempt=${attempt.attempt_number} ${attempt.attempt_id} ${attempt.stage}/${attempt.status}`,
      `build=${attempt.build_id} version=${attempt.build_version}`,
      `attempt_counts=events:${attempt.event_count} segments:${attempt.source_segment_count} materials:${attempt.material_count} slots:${attempt.slot_count} vectors:${attempt.vector_count}`,
    )
    if (attempt.recovered_from_attempt_id || attempt.recovered_from_build_id) {
      lines.push(`recovered_from=${attempt.recovered_from_attempt_id || 'n/a'} build=${attempt.recovered_from_build_id || 'n/a'}`)
    }
    if (attempt.blocked_reason) {
      lines.push(`blocked_reason=${attempt.blocked_reason}`)
    }
  }

  for (const attempt of detail.prior_attempts ?? []) {
    lines.push(
      `prior_attempt=${attempt.attempt_number} ${attempt.attempt_id} ${attempt.stage}/${attempt.status}`,
      `build=${attempt.build_id} version=${attempt.build_version}`,
    )
    if (attempt.blocked_reason) {
      lines.push(`blocked_reason=${attempt.blocked_reason}`)
    }
  }

  for (const event of detail.events) {
    lines.push(
      `event=${event.event_id} ${event.stage}/${event.status}`,
      `counts=segments:${event.source_segment_count} materials:${event.material_count} slots:${event.slot_count} vectors:${event.vector_count}`,
      `message=${event.message || 'n/a'}`,
    )
    const affected = [event.affected_source_id, event.affected_material_id, event.affected_segment_id, event.affected_slot_id].filter(Boolean)
    if (affected.length > 0) {
      lines.push(`affected=${affected.join(' · ')}`)
    }
  }

  lines.push(`actions=rebuild:${detail.rebuild_available ? 'yes' : 'no'} retry:${detail.retry_available ? 'yes' : 'no'}`)
  return lines.join('\n')
}

function SourceProcessingDrawer({
  anchorId,
  detail,
  loading,
  error,
  onClose,
  onLocateSource,
  onLocateMaterial,
  onOpenMaterialDetail,
  onOpenSourceSegmentDetail,
  onRetry,
  onRebuild,
}: {
  anchorId: number
  detail: reference.SourceProcessingDetail | null
  loading: boolean
  error: Exclude<ReferenceErrorState, string> | null
  onClose: () => void
  onLocateSource: (sourceId: string) => void
  onLocateMaterial: (materialId: string) => void
  onOpenMaterialDetail: (materialId: string) => void
  onOpenSourceSegmentDetail: (anchorId: number, segmentId: string) => void
  onRetry: () => void
  onRebuild: () => void
}) {
  const source = detail?.source
  const status = detail?.current_status
  const currentAttempt = detail?.current_attempt ?? null
  const priorAttempts = detail?.prior_attempts ?? []
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'failed'>('idle')

  const copyDiagnostics = useCallback(async () => {
    if (!detail) return
    try {
      await copyTextToClipboard(sourceProcessingDiagnosticText(detail))
      setCopyState('copied')
      window.setTimeout(() => setCopyState('idle'), 1500)
    } catch {
      setCopyState('failed')
      window.setTimeout(() => setCopyState('idle'), 1500)
    }
  }, [detail])

  return (
    <aside
      role="dialog"
      aria-modal="false"
      aria-label="处理记录"
      data-testid="reference-source-processing-drawer"
      className="fixed inset-y-0 right-0 z-40 flex w-[640px] max-w-[calc(100vw-2rem)] flex-col border-l border-border bg-card shadow-xl"
    >
      <div className="flex items-start justify-between gap-3 border-b border-border px-4 py-3">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-foreground">处理记录</h3>
          <p className="mt-0.5 truncate text-[11px] text-muted-foreground">{source?.title ?? `anchor ${anchorId}`}</p>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="rounded p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
          aria-label="关闭处理记录"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-3">
        {loading && (
          <div className="flex items-center gap-2 rounded-md border border-border bg-background px-3 py-2 text-xs text-muted-foreground">
            <Loader2 className="h-3.5 w-3.5 animate-spin" />
            正在加载处理记录...
          </div>
        )}

        {error && (
          <ErrorCallout
            compact
            title={error.title}
            message={error.message}
            diagnostic={error.diagnostic}
            className="rounded-md"
            onRetry={onRetry}
            retryLabel="重试加载处理记录"
            onClose={onClose}
          />
        )}

        {detail && (
          <>
            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">来源</h4>
              <div className="space-y-1 text-xs text-muted-foreground">
                <DetailKeyValue label="归属" value={detail.source.owner_scope === 'workspace_corpus' ? '工作区语料' : `小说 ${detail.source.owner_novel_id ?? detail.source.novel_id}`} />
                <DetailKeyValue label="可见性" value={`${detail.source.visibility} · ${detail.source.source_trust}`} />
                <DetailKeyValue
                  label="可执行操作"
                  value={`${detail.rebuild_available ? '可重建语料' : '不可重建语料'} · ${detail.retry_available ? '失败状态可恢复' : '当前无失败重试项'}`}
                />
              </div>
              <p className="text-[11px] leading-relaxed text-muted-foreground">
                重试加载详情只刷新本抽屉；重建语料会重新跑来源解析、切分、材料抽取和索引。
              </p>
              <div className="flex flex-wrap gap-2">
                {detail.rebuild_available && (
                  <button
                    type="button"
                    onClick={onRebuild}
                    disabled={loading}
                    className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-[11px] font-medium text-foreground hover:bg-secondary disabled:opacity-50"
                    title="重建语料"
                    aria-label={`重建语料 ${detail.source.title}，重新跑解析、切分、抽取和索引`}
                  >
                    <RefreshCcw className="h-3.5 w-3.5" />
                    重建语料
                  </button>
                )}
                <button
                  type="button"
                  data-testid="reference-source-processing-copy-diagnostic"
                  onClick={() => {
                    void copyDiagnostics()
                  }}
                  className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 text-[11px] font-medium text-foreground hover:bg-secondary disabled:opacity-50"
                  aria-label={`复制 ${detail.source.title} 的已脱敏诊断`}
                >
                  {copyState === 'copied' ? <Check className="h-3.5 w-3.5" /> : <Clipboard className="h-3.5 w-3.5" />}
                  {copyState === 'copied' ? '已复制' : copyState === 'failed' ? '复制失败' : '复制诊断'}
                </button>
              </div>
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">当前状态</h4>
              {status ? (
                <div className="rounded-md border border-border bg-background px-3 py-2">
                  <div className="flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground">
                    <span>{status.stage} · {status.status}</span>
                    <span>{String(status.updated_at ?? '')}</span>
                  </div>
                  <p className="mt-1 text-xs leading-relaxed text-foreground">{status.diagnostic || '无诊断信息'}</p>
                  <p className="mt-1 text-[11px] text-muted-foreground">
                    segments={status.source_segment_count} · materials={status.material_count} · slots={status.slot_count} · vectors={status.vector_count}
                  </p>
                </div>
              ) : (
                <p className="text-xs text-muted-foreground">暂无当前状态</p>
              )}
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">处理尝试</h4>
              {currentAttempt ? (
                <div className="rounded-md border border-border bg-background px-3 py-2">
                  <div className="flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground">
                    <span>第 {currentAttempt.attempt_number} 次 · {currentAttempt.stage} · {currentAttempt.status}</span>
                    <span>{String(currentAttempt.updated_at ?? '')}</span>
                  </div>
                  <p className="mt-1 break-all text-[11px] text-muted-foreground">
                    attempt={currentAttempt.attempt_id} · build={currentAttempt.build_id} · version={currentAttempt.build_version}
                  </p>
                  <p className="mt-1 text-[11px] text-muted-foreground">
                    events={currentAttempt.event_count} · segments={currentAttempt.source_segment_count} · materials={currentAttempt.material_count} · slots={currentAttempt.slot_count} · vectors={currentAttempt.vector_count}
                  </p>
                  {(currentAttempt.recovered_from_attempt_id || currentAttempt.recovered_from_build_id) && (
                    <p className="mt-1 break-all text-[11px] text-muted-foreground">
                      恢复自 {currentAttempt.recovered_from_attempt_id || 'n/a'} · {currentAttempt.recovered_from_build_id || 'n/a'}
                    </p>
                  )}
                  {currentAttempt.blocked_reason && (
                    <p className="mt-1 text-xs leading-relaxed text-foreground">{currentAttempt.blocked_reason}</p>
                  )}
                </div>
              ) : (
                <p className="text-xs text-muted-foreground">暂无处理尝试摘要</p>
              )}

              {priorAttempts.length > 0 && (
                <div className="space-y-2">
                  <p className="text-[11px] text-muted-foreground">历史尝试：{detail.attempt_count ?? priorAttempts.length + 1} 次</p>
                  {priorAttempts.map(attempt => (
                    <div key={attempt.attempt_id} className="rounded-md border border-border bg-background px-3 py-2">
                      <div className="flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground">
                        <span>第 {attempt.attempt_number} 次 · {attempt.stage} · {attempt.status}</span>
                        <span>{String(attempt.updated_at ?? '')}</span>
                      </div>
                      <p className="mt-1 break-all text-[11px] text-muted-foreground">
                        attempt={attempt.attempt_id} · build={attempt.build_id}
                      </p>
                      {attempt.blocked_reason && (
                        <p className="mt-1 text-xs leading-relaxed text-foreground">{attempt.blocked_reason}</p>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">历史事件</h4>
              {detail.events.length === 0 ? (
                <p className="text-xs text-muted-foreground">暂无处理记录</p>
              ) : (
                <div className="space-y-2">
                  {detail.events.map(event => (
                    <div key={event.event_id} className="rounded-md border border-border bg-background px-3 py-2">
                      <div className="flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground">
                        <span>{event.event_id} · {event.stage} · {event.status}</span>
                        <span>{String(event.created_at ?? '')}</span>
                      </div>
                      <p className="mt-1 text-xs leading-relaxed text-foreground">{event.message || '无诊断信息'}</p>
                      <p className="mt-1 text-[11px] text-muted-foreground">
                        segments={event.source_segment_count} · materials={event.material_count} · slots={event.slot_count} · vectors={event.vector_count}
                      </p>
                      <AffectedProcessingIds
                        anchorId={anchorId}
                        event={event}
                        onLocateSource={onLocateSource}
                        onLocateMaterial={onLocateMaterial}
                        onOpenMaterialDetail={onOpenMaterialDetail}
                        onOpenSourceSegmentDetail={onOpenSourceSegmentDetail}
                      />
                    </div>
                  ))}
                </div>
              )}
            </section>
          </>
        )}
      </div>
    </aside>
  )
}

function AffectedProcessingIds({
  anchorId,
  event,
  onLocateSource,
  onLocateMaterial,
  onOpenMaterialDetail,
  onOpenSourceSegmentDetail,
}: {
  anchorId: number
  event: reference.SourceProcessingEvent
  onLocateSource: (sourceId: string) => void
  onLocateMaterial: (materialId: string) => void
  onOpenMaterialDetail: (materialId: string) => void
  onOpenSourceSegmentDetail: (anchorId: number, segmentId: string) => void
}) {
  const affectedIds = [event.affected_source_id, event.affected_material_id, event.affected_segment_id, event.affected_slot_id].filter(Boolean)
  if (affectedIds.length === 0) return null

  return (
    <div className="mt-1 space-y-1">
      <p className="break-all text-[11px] text-muted-foreground">
        affected: {affectedIds.join(' · ')}
      </p>
      <div className="flex flex-wrap gap-1">
        {event.affected_source_id && (
          <button
            type="button"
            onClick={() => onLocateSource(event.affected_source_id)}
            className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80"
            aria-label={`定位来源 ${event.affected_source_id}`}
          >
            定位来源
          </button>
        )}
        {event.affected_material_id && (
          <>
            <button
              type="button"
              onClick={() => onLocateMaterial(event.affected_material_id)}
              className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80"
              aria-label={`在材料库筛选 ${event.affected_material_id}`}
            >
              筛选材料库
            </button>
            <button
              type="button"
              onClick={() => onOpenMaterialDetail(event.affected_material_id)}
              className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80"
              aria-label={`查看 ${event.affected_material_id} 的材料明细`}
            >
              查看材料明细
            </button>
          </>
        )}
        {event.affected_segment_id && (
          <button
            type="button"
            onClick={() => onOpenSourceSegmentDetail(anchorId, event.affected_segment_id)}
            className="rounded bg-secondary px-2 py-1 text-[11px] leading-none text-foreground hover:bg-secondary/80"
            aria-label={`查看 ${event.affected_segment_id} 的来源片段明细`}
          >
            查看片段明细
          </button>
        )}
      </div>
    </div>
  )
}

function SourceSegmentDetailDrawer({
  anchorId,
  segmentId,
  detail,
  loading,
  error,
  onClose,
  onRetry,
}: {
  anchorId: number
  segmentId: string
  detail: reference.SourceSegmentDetail | null
  loading: boolean
  error: Exclude<ReferenceErrorState, string> | null
  onClose: () => void
  onRetry: () => void
}) {
  const source = detail?.source
  const segment = detail?.segment

  return (
    <aside
      role="dialog"
      aria-modal="false"
      aria-label="来源片段明细"
      data-testid="reference-source-segment-detail-drawer"
      className="fixed inset-y-0 right-0 z-40 flex w-[640px] max-w-[calc(100vw-2rem)] flex-col border-l border-border bg-card shadow-xl"
    >
      <div className="flex items-start justify-between gap-3 border-b border-border px-4 py-3">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-foreground">来源片段明细</h3>
          <p className="mt-0.5 truncate text-[11px] text-muted-foreground">{source?.title ?? `anchor ${anchorId}`}</p>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="rounded p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
          aria-label="关闭来源片段明细"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-3">
        {loading && (
          <div className="flex items-center gap-2 rounded-md border border-border bg-background px-3 py-2 text-xs text-muted-foreground">
            <Loader2 className="h-3.5 w-3.5 animate-spin" />
            正在加载片段明细...
          </div>
        )}

        {error && (
          <ErrorCallout
            compact
            title={error.title}
            message={error.message}
            diagnostic={error.diagnostic}
            className="rounded-md"
            onRetry={onRetry}
            retryLabel="重试加载片段明细"
            onClose={onClose}
          />
        )}

        {detail && segment && (
          <>
            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">来源</h4>
              <div className="space-y-1 text-xs text-muted-foreground">
                <DetailKeyValue label="归属" value={detail.source.owner_scope === 'workspace_corpus' ? '工作区语料' : `小说 ${detail.source.owner_novel_id ?? detail.source.novel_id}`} />
                <DetailKeyValue label="可见性" value={`${detail.source.visibility} · ${detail.source.source_trust}`} />
                <DetailKeyValue label="授权" value={detail.source.license_status} />
                <DetailKeyValue label="来源状态" value={detail.source.status} />
              </div>
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">片段</h4>
              <div className="rounded-md border border-border bg-background px-3 py-2">
                <div className="flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground">
                  <span className="min-w-0 truncate">{segment.segment_id} · {segment.segment_type}</span>
                  <span>第 {segment.chapter_index} 章 · #{segment.segment_index}</span>
                </div>
                {segment.chapter_title && (
                  <p className="mt-1 text-[11px] text-muted-foreground">{segment.chapter_title}</p>
                )}
                <p className="mt-2 whitespace-pre-wrap break-words text-xs leading-relaxed text-foreground">
                  {segment.text_preview || '无预览'}
                </p>
                <PreviewBoundary truncated={segment.text_truncated} compact />
                <div className="mt-2 grid grid-cols-2 gap-2 text-[11px] text-muted-foreground">
                  <span>offset {segment.start_offset}-{segment.end_offset}</span>
                  <span className="truncate">parent {segment.parent_segment_id || 'n/a'}</span>
                </div>
                <p className="mt-1 break-all text-[11px] text-muted-foreground">hash {segment.text_hash}</p>
              </div>
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">处理记录</h4>
              {detail.processing_notes.length === 0 ? (
                <p className="text-xs text-muted-foreground">暂无处理记录</p>
              ) : (
                <div className="space-y-2">
                  {detail.processing_notes.map((note, index) => (
                    <div key={`${note.stage}-${note.updated_at}-${index}`} className="rounded-md border border-border bg-background px-3 py-2">
                      <div className="flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground">
                        <span>{note.stage} · {note.status}</span>
                        <span>{String(note.updated_at ?? '')}</span>
                      </div>
                      <p className="mt-1 text-xs leading-relaxed text-foreground">{note.message}</p>
                      <p className="mt-1 text-[11px] text-muted-foreground">
                        segments={note.source_segment_count} · materials={note.material_count} · slots={note.slot_count} · vectors={note.vector_count}
                      </p>
                      {(note.affected_source_id || note.affected_material_id || note.affected_segment_id || note.affected_slot_id) && (
                        <p className="mt-1 break-all text-[11px] text-muted-foreground">
                          affected: {[note.affected_source_id, note.affected_material_id, note.affected_segment_id, note.affected_slot_id].filter(Boolean).join(' · ')}
                        </p>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </section>
          </>
        )}

        {!loading && !error && !detail && (
          <p className="text-xs text-muted-foreground">片段 {segmentId} 暂无可查看明细</p>
        )}
      </div>
    </aside>
  )
}

function MaterialDetailDrawer({
  materialId,
  detail,
  loading,
  error,
  onClose,
  onRetry,
}: {
  materialId: string
  detail: reference.MaterialDetail | null
  loading: boolean
  error: Exclude<ReferenceErrorState, string> | null
  onClose: () => void
  onRetry: () => void
}) {
  const material = detail?.material
  const source = detail?.source
  const materialScores = material ? scoreComponentEntries(material.score_components) : []

  return (
    <aside
      role="dialog"
      aria-modal="false"
      aria-label="材料明细"
      data-testid="reference-material-detail-drawer"
      className="fixed inset-y-0 right-0 z-40 flex w-[640px] max-w-[calc(100vw-2rem)] flex-col border-l border-border bg-card shadow-xl"
    >
      <div className="flex items-start justify-between gap-3 border-b border-border px-4 py-3">
        <div className="min-w-0">
          <h3 className="text-sm font-semibold text-foreground">材料明细</h3>
          <p className="mt-0.5 truncate text-[11px] text-muted-foreground">{materialId}</p>
        </div>
        <button
          type="button"
          onClick={onClose}
          className="rounded p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
          aria-label="关闭材料明细"
        >
          <X className="h-4 w-4" />
        </button>
      </div>

      <div className="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-3">
        {loading && (
          <div className="flex items-center gap-2 rounded-md border border-border bg-background px-3 py-2 text-xs text-muted-foreground">
            <Loader2 className="h-3.5 w-3.5 animate-spin" />
            正在加载材料明细...
          </div>
        )}

        {error && (
          <ErrorCallout
            compact
            title={error.title}
            message={error.message}
            diagnostic={error.diagnostic}
            className="rounded-md"
            onRetry={onRetry}
            retryLabel="重试加载材料明细"
            onClose={onClose}
          />
        )}

        {material && source && (
          <>
            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">材料</h4>
              <div className="space-y-1 text-xs text-muted-foreground">
                <DetailKeyValue label="类型" value={`${material.material_type} · ${material.function_tag || 'untagged'} · ${material.emotion_tag || 'neutral'}`} />
                <DetailKeyValue label="POV/技法" value={`${material.pov_tag || 'unknown'} · ${material.technique_tag || 'none'}`} />
                <DetailKeyValue label="置信度" value={`功能 ${formatConfidence(material.function_confidence)} · 情绪 ${formatConfidence(material.emotion_confidence)} · POV ${formatConfidence(material.pov_confidence)}`} />
                <DetailKeyValue label="来源段落" value={material.source_segment_id} />
                <DetailKeyValue label="来源哈希" value={material.source_hash} />
                <DetailKeyValue label="抽取器" value={material.extractor_version} />
                <DetailKeyValue label="校正状态" value={material.user_verified ? '已人工校正' : '未人工校正'} />
                <DetailKeyValue label="归档状态" value={material.archive_state === 'archived' ? `已归档${material.archived_at ? ` · ${String(material.archived_at)}` : ''}` : '活跃'} />
                <DetailKeyValue
                  label="评分明细"
                  value={materialScores.length > 0
                    ? materialScores.map(([name, value]) => `${name} ${value.toFixed(2)}`).join(' · ')
                    : '暂无评分明细'}
                />
              </div>
              <p className="whitespace-pre-wrap break-words rounded-md border border-border bg-background px-3 py-2 text-xs leading-relaxed text-foreground">
                {material.text_preview || '无预览'}
              </p>
              <PreviewBoundary truncated={material.text_truncated} />
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">来源</h4>
              <div className="space-y-1 text-xs text-muted-foreground">
                <DetailKeyValue label="标题" value={source.title} />
                <DetailKeyValue label="作者" value={source.author || '未填写'} />
                <DetailKeyValue label="格式/授权" value={`${source.source_kind} · ${source.license_status}`} />
                <DetailKeyValue label="归属" value={source.owner_scope === 'workspace_corpus' ? '工作区语料' : `小说 ${source.owner_novel_id ?? source.novel_id}`} />
                <DetailKeyValue label="可见性/可信度" value={`${source.visibility} · ${source.source_trust}`} />
                <DetailKeyValue label="状态/版本" value={`${source.status} · ${source.build_version}`} />
                <DetailKeyValue label="文件哈希" value={source.source_file_hash} />
                {source.user_tags.length > 0 && <DetailKeyValue label="标签" value={source.user_tags.join('；')} />}
              </div>
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">来源片段</h4>
              {detail.segments.length === 0 ? (
                <p className="text-xs text-muted-foreground">暂无片段预览</p>
              ) : (
                <div className="space-y-2">
                  {detail.segments.map(segment => (
                    <div key={segment.segment_id} className="rounded-md border border-border bg-background px-3 py-2">
                      <div className="flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground">
                        <span className="min-w-0 truncate">{segment.segment_id} · {segment.segment_type}</span>
                        <span>第 {segment.chapter_index} 章 · #{segment.segment_index}</span>
                      </div>
                      <p className="mt-1 whitespace-pre-wrap break-words text-xs leading-relaxed text-foreground">
                        {segment.text_preview || '无预览'}
                      </p>
                      <PreviewBoundary truncated={segment.text_truncated} compact />
                      <p className="mt-1 break-all text-[11px] text-muted-foreground">hash {segment.text_hash}</p>
                    </div>
                  ))}
                </div>
              )}
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">占位槽</h4>
              {detail.slots.length === 0 ? (
                <p className="text-xs text-muted-foreground">未检测到占位槽</p>
              ) : (
                <div className="space-y-1">
                  {detail.slots.map((slot, index) => (
                    <DetailKeyValue
                      key={`${slot.slot_name}-${slot.start_offset}-${index}`}
                      label={slot.slot_name}
                      value={`${slot.placeholder} · ${slot.start_offset}-${slot.end_offset}`}
                    />
                  ))}
                </div>
              )}
            </section>

            <section className="space-y-2">
              <h4 className="text-xs font-semibold text-foreground">处理记录</h4>
              {detail.processing_notes.length === 0 ? (
                <p className="text-xs text-muted-foreground">暂无处理记录</p>
              ) : (
                <div className="space-y-2">
                  {detail.processing_notes.map((note, index) => (
                    <div key={`${note.stage}-${note.updated_at}-${index}`} className="rounded-md border border-border bg-background px-3 py-2">
                      <div className="flex flex-wrap items-center justify-between gap-2 text-[11px] text-muted-foreground">
                        <span>{note.stage} · {note.status}</span>
                        <span>{String(note.updated_at ?? '')}</span>
                      </div>
                      <p className="mt-1 whitespace-pre-wrap break-words text-xs leading-relaxed text-foreground">{note.message}</p>
                      <p className="mt-1 text-[11px] text-muted-foreground">
                        segments={note.source_segment_count} · materials={note.material_count} · slots={note.slot_count} · vectors={note.vector_count}
                      </p>
                      {(note.affected_source_id || note.affected_material_id || note.affected_segment_id || note.affected_slot_id) && (
                        <p className="mt-1 break-all text-[11px] text-muted-foreground">
                          affected: {[note.affected_source_id, note.affected_material_id, note.affected_segment_id, note.affected_slot_id].filter(Boolean).join(' · ')}
                        </p>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </section>
          </>
        )}
      </div>
    </aside>
  )
}

function PreviewBoundary({ truncated, compact = false }: { truncated: boolean; compact?: boolean }) {
  return (
    <p className={`${compact ? 'mt-1' : ''} text-[11px] text-muted-foreground`}>
      {truncated ? '预览已截断，不显示全文' : '完整预览'}
    </p>
  )
}

function DetailKeyValue({ label, value }: { label: string; value: string }) {
  return (
    <div className="grid grid-cols-[72px_minmax(0,1fr)] gap-2">
      <span className="text-muted-foreground">{label}</span>
      <span className="min-w-0 break-all text-foreground">{value}</span>
    </div>
  )
}

function formatConfidence(value: number): string {
  return Number.isFinite(value) ? value.toFixed(2) : '0.00'
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}
