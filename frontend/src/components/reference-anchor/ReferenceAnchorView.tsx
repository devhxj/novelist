import { useCallback, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import {
  BookMarked,
  Check,
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
import { useApp } from '@/hooks/useApp'
import type { reference } from '@/lib/novelist/types'
import { BlueprintDetail } from './BlueprintDetail'
import { OrchestrationPanel } from './OrchestrationPanel'
import {
  EMPTY_REVISION_FORM,
  addListChange,
  addSlotPlanChange,
  addStringChange,
  formFromBlueprint,
  lines,
} from './blueprintRevision'
import type { BlueprintRevisionForm, BlueprintRevisionStringKey } from './blueprintRevision'
import { inputClass, statusTone } from './referenceAnchorStyles'

interface Props {
  novelId: number
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

type MaterialPreviewState = {
  items: reference.Material[]
  page: number
  size: number
  total: number
  totalPages: number
}

type MaterialLibraryState = MaterialPreviewState

type MaterialTagForm = {
  functionTag: string
  emotionTag: string
  sceneTag: string
  povTag: string
  techniqueTag: string
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

function tagFormFromMaterial(material: reference.Material): MaterialTagForm {
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

function materialScoreComponents(material: reference.Material): Array<[string, number]> {
  return Object.entries(material.score_components ?? {})
    .filter(([, value]) => Number.isFinite(value) && value > 0)
    .sort(([, left], [, right]) => right - left)
}

function materialBestScore(material: reference.Material): number {
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
    anchor.title,
    anchor.author,
    anchor.source_path,
    anchor.source_kind,
    anchor.license_status,
    anchor.visibility,
    anchor.source_trust,
    anchor.owner_scope,
    ...anchor.user_tags,
  ].some(value => normalized(value).includes(needle))
}

function materialMatchesQuery(material: reference.Material, query: string): boolean {
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
    material.text,
  ].some(value => normalized(value).includes(needle))
}

export default function ReferenceAnchorView({ novelId }: Props) {
  const app = useApp()

  const [anchors, setAnchors] = useState<reference.Anchor[]>([])
  const [selectedAnchorIds, setSelectedAnchorIds] = useState<number[]>([])
  const [materials, setMaterials] = useState<reference.Material[]>([])
  const [blueprints, setBlueprints] = useState<reference.ChapterBlueprintSummary[]>([])
  const [activeBlueprint, setActiveBlueprint] = useState<reference.ChapterBlueprint | null>(null)
  const [orchestrationRuns, setOrchestrationRuns] = useState<reference.OrchestrationRun[]>([])
  const [activeOrchestrationRun, setActiveOrchestrationRun] = useState<reference.OrchestrationRun | null>(null)
  const [orchestrationEvents, setOrchestrationEvents] = useState<reference.OrchestrationRunEvent[]>([])
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
  const [selectedLibraryMaterialIds, setSelectedLibraryMaterialIds] = useState<string[]>([])
  const [bulkLibraryMaterialTagForm, setBulkLibraryMaterialTagForm] = useState<MaterialTagForm>(EMPTY_MATERIAL_TAG_FORM)
  const [blueprintForm, setBlueprintForm] = useState<BlueprintForm>(EMPTY_BLUEPRINT_FORM)
  const [revisionForm, setRevisionForm] = useState<BlueprintRevisionForm>(EMPTY_REVISION_FORM)
  const [materialFilters, setMaterialFilters] = useState<MaterialSearchFilters>(EMPTY_MATERIAL_FILTERS)
  const [materialQuery, setMaterialQuery] = useState('')
  const [orchestrationUseSelectedAnchors, setOrchestrationUseSelectedAnchors] = useState(false)
  const [advancedMode, setAdvancedMode] = useState(false)
  const [anchorScopeFilter, setAnchorScopeFilter] = useState<AnchorScopeFilter>('all')
  const [anchorQuery, setAnchorQuery] = useState('')
  const [anchorLicenseFilter, setAnchorLicenseFilter] = useState('all')
  const [anchorVisibilityFilter, setAnchorVisibilityFilter] = useState('all')
  const [anchorSourceTrustFilter, setAnchorSourceTrustFilter] = useState('all')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

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
  const selectedVisibleLibraryMaterialCount = useMemo(
    () => visibleMaterialLibraryIds.filter(id => selectedLibraryMaterialSet.has(id)).length,
    [visibleMaterialLibraryIds, selectedLibraryMaterialSet],
  )
  const hasBulkLibraryMaterialTagOverride = useMemo(
    () => Object.values(bulkLibraryMaterialTagForm).some(value => value.trim().length > 0),
    [bulkLibraryMaterialTagForm],
  )
  const hasMaterialLibraryPageQuery = materialLibraryPageQuery.trim().length > 0

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

  useEffect(() => {
    let cancelled = false
    void (async () => {
      setLoading(true)
      try {
        await Promise.all([loadAnchors(), loadBlueprints(), loadOrchestrationRuns()])
      } catch (err) {
        if (!cancelled) setError(err instanceof Error ? err.message : '加载失败')
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [loadAnchors, loadBlueprints, loadOrchestrationRuns])

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
        if (!cancelled) setError(err instanceof Error ? err.message : '加载编排事件失败')
      }
    })()
    return () => { cancelled = true }
  }, [app, novelId, activeOrchestrationRun])

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
      visibility: anchorForm.visibility,
      source_trust: anchorForm.sourceTrust,
      user_tags: lines(anchorForm.userTags),
    }), '参考锚点已创建')
    if (created) {
      setAnchorForm(EMPTY_ANCHOR_FORM)
      await loadAnchors()
    }
  }

  async function pickReferenceSourceFile() {
    const pickedPath = await run(() => app.PickReferenceSourceFile())
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
    await run(() => app.RebuildReferenceAnchor(novelId, anchorId), '锚点已重建')
    await loadAnchors()
  }

  async function promoteAnchorToWorkspaceCorpus(anchor: reference.Anchor) {
    const promoted = await run(() => app.PromoteReferenceAnchorToWorkspaceCorpus({
      novel_id: novelId,
      anchor_id: anchor.anchor_id,
    }), '已提升为工作区语料')
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
      setError(err instanceof Error ? err.message : '操作失败')
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
      setError(err instanceof Error ? err.message : '操作失败')
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
      setError(err instanceof Error ? err.message : '操作失败')
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
    }), '参考元数据已更新')
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
    }))
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

  function beginEditMaterialTags(material: reference.Material) {
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

  async function saveMaterialTags(material: reference.Material) {
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
    }), '材料标签已校正')
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
    }), `已批量校正 ${selectedMaterialIds.length} 条材料标签`)

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
    }))
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
    }
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
    }), `材料库已批量校正 ${selectedLibraryMaterialIds.length} 条材料标签`)

    if (updated) {
      const updatedById = new Map(updated.map(material => [material.material_id, material]))
      setMaterialLibrary(current => ({
        ...current,
        items: current.items.map(item => updatedById.get(item.material_id) ?? item),
      }))
      setSelectedLibraryMaterialIds([])
      setBulkLibraryMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)
    }
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

  async function startOrchestration() {
    const chapterNumber = Number.parseInt(blueprintForm.chapterNumber, 10)
    if (!Number.isFinite(chapterNumber) || chapterNumber < 1) {
      setError('请输入有效章节号')
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
        license_statuses: ['user_provided', 'unknown'],
        include_anchor_ids: orchestrationUseSelectedAnchors ? selectedAnchorIds : [],
        exclude_anchor_ids: [],
      },
      source_confirmed: false,
    }), '编排已启动，等待确认来源与事实边界')
    if (started) {
      setActiveOrchestrationRun(started)
      await loadOrchestrationRuns()
    }
  }

  async function selectOrchestrationRun(runId: string) {
    const selected = await run(() => app.GetReferenceOrchestrationRun(novelId, runId))
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
    }), '编排已继续')
    if (resumed) {
      setActiveOrchestrationRun(resumed)
      await syncBlueprintFromRun(resumed)
      await loadOrchestrationRuns()
      try {
        const events = await app.GetReferenceOrchestrationRunEvents(novelId, runId)
        setOrchestrationEvents(events ?? [])
      } catch (err) {
        setError(err instanceof Error ? err.message : '加载编排事件失败')
      }
    }
  }

  async function cancelOrchestration(runId: string) {
    const cancelled = await run(() => app.CancelReferenceOrchestrationRun({
      novel_id: novelId,
      run_id: runId,
      reason: 'cancelled from reference orchestration panel',
    }), '编排已取消')
    if (cancelled) {
      setActiveOrchestrationRun(cancelled)
      await loadOrchestrationRuns()
      try {
        const events = await app.GetReferenceOrchestrationRunEvents(novelId, runId)
        setOrchestrationEvents(events ?? [])
      } catch (err) {
        setError(err instanceof Error ? err.message : '加载编排事件失败')
      }
    }
  }

  async function selectBlueprint(blueprintId: number) {
    const blueprint = await run(() => app.GetReferenceChapterBlueprint(novelId, blueprintId))
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
    }), '蓝图评审已完成')
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
    }), '蓝图已批准')
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
    }), '材料已绑定到蓝图')
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
    const result = await run(() => app.GenerateReferenceAnchoredDraft({
      novel_id: novelId,
      blueprint_id: activeBlueprint.blueprint_id,
      beat_ids: [],
    }), '候选段落已生成')
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
    }), '蓝图已修订，需要重新评审和批准')
    if (revised) {
      setActiveBlueprint(revised)
      setRevisionForm(formFromBlueprint(revised))
      setBinding(null)
      setDraft(null)
      await loadBlueprints()
    }
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
            <button onClick={() => { void loadAnchors(); void loadBlueprints(); void loadOrchestrationRuns() }} className="inline-flex items-center gap-1 px-2.5 py-1 rounded text-xs text-muted-foreground hover:text-foreground hover:bg-secondary transition-colors">
              <RefreshCcw className="h-3 w-3" />刷新
            </button>
          </div>
        </div>

        {error && <div className="rounded-md border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">{error}</div>}
        {message && <div className="rounded-md border border-emerald-500/30 bg-emerald-500/10 px-3 py-2 text-xs text-emerald-700 dark:text-emerald-300">{message}</div>}

        <div className="grid grid-cols-1 xl:grid-cols-[360px_minmax(0,1fr)] gap-4">
          <section className="space-y-4" aria-labelledby="corpus-library-heading">
            <div className="space-y-1">
              <div className="flex items-center gap-2">
                <BookMarked className="h-3.5 w-3.5 text-muted-foreground" />
                <h3 id="corpus-library-heading" className="text-xs font-semibold text-foreground">语料库管理</h3>
              </div>
              <p className="text-xs leading-relaxed text-muted-foreground">
                管理可检索来源、库条目元数据、可见性和材料预览；写作时默认由右侧流程按故事上下文自动检索。
              </p>
            </div>

            <div className="rounded-lg border border-border bg-card p-4">
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
                    <div key={anchor.anchor_id} className="rounded-md border border-border bg-background px-3 py-2">
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
                              title="重建"
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
                                      <div key={material.material_id} className="rounded border border-border bg-card px-2.5 py-2">
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
                                              onClick={() => beginEditMaterialTags(material)}
                                              disabled={loading}
                                              className="rounded px-1.5 py-1 text-[11px] leading-none text-muted-foreground hover:bg-secondary hover:text-foreground disabled:opacity-50"
                                              aria-label={`校正 ${material.material_id} 的材料标签`}
                                            >
                                              校正
                                            </button>
                                          </span>
                                        </div>
                                        <p className="mt-1 line-clamp-3 text-xs leading-relaxed text-foreground">{material.text}</p>
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

            <div className="rounded-lg border border-border bg-card p-4">
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

              {materialLibrary.items.length > 0 ? (
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
                          <input value={bulkLibraryMaterialTagForm.functionTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, functionTag: event.target.value }))} className={inputClass} aria-label="材料库批量功能标签" />
                        </Field>
                        <Field label="材料库批量情绪">
                          <input value={bulkLibraryMaterialTagForm.emotionTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, emotionTag: event.target.value }))} className={inputClass} aria-label="材料库批量情绪标签" />
                        </Field>
                        <Field label="材料库批量场景">
                          <input value={bulkLibraryMaterialTagForm.sceneTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, sceneTag: event.target.value }))} className={inputClass} aria-label="材料库批量场景标签" />
                        </Field>
                        <Field label="材料库批量 POV">
                          <input value={bulkLibraryMaterialTagForm.povTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, povTag: event.target.value }))} className={inputClass} aria-label="材料库批量 POV 标签" />
                        </Field>
                        <Field label="材料库批量技法">
                          <input value={bulkLibraryMaterialTagForm.techniqueTag} onChange={event => setBulkLibraryMaterialTagForm(form => ({ ...form, techniqueTag: event.target.value }))} className={inputClass} aria-label="材料库批量技法标签" />
                        </Field>
                      </div>
                      <div className="flex flex-wrap items-center gap-1.5">
                        <button
                          type="button"
                          onClick={() => {
                            void saveBulkLibraryMaterialTags()
                          }}
                          disabled={loading || selectedLibraryMaterialIds.length === 0 || !hasBulkLibraryMaterialTagOverride}
                          className="inline-flex items-center gap-1.5 rounded bg-primary px-2.5 py-1.5 text-xs font-medium text-primary-foreground hover:opacity-90 disabled:opacity-50"
                        >
                          <Check className="h-3.5 w-3.5" />批量校正材料库
                        </button>
                        <button
                          type="button"
                          onClick={() => setBulkLibraryMaterialTagForm(EMPTY_MATERIAL_TAG_FORM)}
                          disabled={loading || !hasBulkLibraryMaterialTagOverride}
                          className="inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50"
                        >
                          <X className="h-3.5 w-3.5" />清空批量标签
                        </button>
                      </div>
                    </div>
                  )}
                  {visibleMaterialLibraryItems.length > 0 && (
                    <div className="space-y-2" aria-label="材料库结果">
                      {visibleMaterialLibraryItems.map(material => (
                        <div key={material.material_id} className="rounded border border-border bg-background px-2.5 py-2">
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
                            {material.user_verified && <span className="text-[11px] text-emerald-600 dark:text-emerald-400">已校正</span>}
                          </div>
                          <p className="mt-1 line-clamp-3 text-xs leading-relaxed text-foreground">{material.text}</p>
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
          </section>

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
              runs={orchestrationRuns}
              activeRun={activeOrchestrationRun}
              events={orchestrationEvents}
              loading={loading}
              onChapterNumberChange={value => setBlueprintForm(form => ({ ...form, chapterNumber: value }))}
              onChapterGoalChange={value => setBlueprintForm(form => ({ ...form, chapterGoal: value }))}
              onKnownFactsChange={value => setBlueprintForm(form => ({ ...form, knownFacts: value }))}
              onForbiddenFactsChange={value => setBlueprintForm(form => ({ ...form, forbiddenFacts: value }))}
              onUseSelectedAnchorsChange={setOrchestrationUseSelectedAnchors}
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
                  <div className="rounded-md border border-border bg-background p-3">
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
                          <div key={material.material_id} className="rounded-md border border-border bg-card p-3">
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
                            <p className="mt-1 line-clamp-3 text-xs leading-relaxed text-foreground">{material.text}</p>
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
                    <div className="rounded-md border border-border bg-background p-4">
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
        </div>
      </div>
    </main>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="block">
      <span className="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
      {children}
    </label>
  )
}
