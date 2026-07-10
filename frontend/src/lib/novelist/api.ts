import { bridge, type BridgeInvokeOptions } from './bridge'
import type {
  app,
  chapter,
  character,
  config,
  git,
  llm,
  layout,
  location,
  novel,
  novelImport,
  pattern,
  reader,
  reference,
  search,
  session,
  skill,
  storage,
  storyarc,
  styleSample,
  timeline,
  update,
  writing,
} from './types'

export type AppMethodArgs = readonly unknown[]
type BridgeBackedMethod = (...args: never[]) => Promise<unknown>
type AppMethod<TArgs extends AppMethodArgs, TResult> = (...args: TArgs) => Promise<TResult>

export interface EmbeddingConfigView {
  provider_key: string
  endpoint_url: string
  api_key: string
  model_id: string
  dimensions: number | null
  user: string
  provider_type: string
  onnx_model_path: string
  onnx_vocab_path: string
  max_sequence_length: number | null
  normalize_embeddings: boolean
}

export interface SqliteVecStatusView {
  available: boolean
  status: string
  runtime_identifier: string
  file_name: string
  error: string
}

export interface SearchStoryMemoryInput {
  novel_id: number
  query: string
  top_k: number
  min_relevance: number
  chapter_numbers: number[]
  chunk_types: string[]
}

export interface StoryMemoryHit {
  chunk_id: string
  chapter_number: number
  chapter_title: string
  chunk_type: string
  relevance: number
  content: string
}

export interface SearchStoryMemoryResult {
  query: string
  total: number
  message: string
  max_relevance: string
  content: string
  results: StoryMemoryHit[]
}

export interface NovelistAppApi {
  ApproveTool: AppMethod<[string, boolean, string], void>
  AdaptReferenceMaterial: AppMethod<[reference.AdaptMaterialInput], reference.AdaptMaterialResult>
  ApproveReferenceChapterBlueprint: AppMethod<[reference.ApproveChapterBlueprintInput], reference.ChapterBlueprint>
  ArchiveReferenceStyleProfile: AppMethod<[reference.ArchiveStyleProfileInput], reference.StyleProfile>
  AuditReferenceAnchoredDraft: AppMethod<[reference.AuditAnchoredDraftInput], reference.AnchoredDraftAudit>
  AuditReferenceReuse: AppMethod<[reference.AuditReuseInput], reference.ReuseAudit>
  BackfillReferenceCorpusTechniqueVectorIndex: AppMethod<[reference.BackfillCorpusTechniqueVectorIndexInput], reference.CorpusTechniqueVectorIndexBackfill>
  BindReferenceBlueprintMaterials: AppMethod<[reference.BindBlueprintMaterialsInput], reference.BlueprintMaterialBindingResult>
  BuildReferenceStyleProfile: AppMethod<[reference.BuildStyleProfileInput], reference.StyleProfile>
  CancelNovelImport: AppMethod<[novelImport.CancelNovelImportInput], novelImport.ImportRun>
  CancelChat: AppMethod<[string], void>
  CancelNarrativePatternExtraction: AppMethod<[pattern.CancelNarrativePatternExtractionInput], pattern.NarrativePatternRun>
 CancelReferenceOrchestrationRun: AppMethod<[reference.CancelOrchestrationRunInput], reference.OrchestrationRun>
 CancelReferenceCorpusAnalysisJob: AppMethod<[{ job_id: string; expected_version: number }], reference.CorpusAnalysisJob>
  CancelReferenceStyleProfileBuild: AppMethod<[reference.CancelStyleProfileBuildInput], reference.StyleProfileBuildStatus>
  CancelStyleSkillExtraction: AppMethod<[styleSample.CancelStyleSkillExtractionInput], styleSample.StyleSkillExtractionRun>
  Chat: AppMethod<[app.ChatInput], app.ChatResult>
  CheckForUpdates: AppMethod<[update.CheckForUpdatesInput], update.UpdateCheckResult>
  CompareReferenceStyleProfiles: AppMethod<[reference.CompareStyleProfilesInput], reference.StyleProfileComparison>
  CompressContext: AppMethod<[app.CompressInput], app.CompressResult>
  CreateArcNode: AppMethod<[number, app.CreateArcNodeInput], storyarc.ArcNode>
  CreateChapter: AppMethod<[app.CreateChapterInput], chapter.Chapter>
  CreateCharacter: AppMethod<[number, app.CreateCharacterInput], character.Character>
  CreateLocation: AppMethod<[number, app.CreateLocationInput], location.Location>
  CreateNovel: AppMethod<[app.CreateNovelInput], novel.Novel>
  CreatePreference: AppMethod<[number, app.CreatePreferenceInput], novel.PreferenceItem>
  CreateReaderPerspective: AppMethod<[number, app.CreateReaderPerspectiveInput], reader.ReaderPerspective>
  CreateReferenceAnchor: AppMethod<[reference.CreateAnchorInput], reference.Anchor>
  CreateReferenceAnchors: AppMethod<[reference.CreateAnchorsInput], reference.Anchor[]>
  CreateReferenceAnchorsWithResult: AppMethod<[reference.CreateAnchorsInput], reference.CreateAnchorsResult>
  CreateStyleSample: AppMethod<[styleSample.CreateStyleSampleInput], styleSample.StyleSample>
  CreateStoryArc: AppMethod<[number, app.CreateStoryArcInput], storyarc.StoryArc>
  CreateTimelineEntry: AppMethod<[number, app.CreateTimelineEntryInput], timeline.TimelineEntry>
  DeleteArcNode: AppMethod<[number, number], void>
  DeleteCharacter: AppMethod<[number, number], void>
  DeleteCover: AppMethod<[number], void>
  DeleteLocation: AppMethod<[number, number], void>
  DeleteNovel: AppMethod<[number], void>
  DeletePreference: AppMethod<[number], void>
  DeleteReaderPerspective: AppMethod<[number, number], void>
  DeleteReferenceAnchor: AppMethod<[number, number], void>
  DeleteReferenceAnchors: AppMethod<[reference.DeleteAnchorsInput], void>
  DeleteReferenceMaterials: AppMethod<[reference.DeleteMaterialsInput], void>
  DeleteSkill: AppMethod<[app.DeleteSkillInput], void>
  DeleteStyleSample: AppMethod<[styleSample.DeleteStyleSampleInput], void>
  DeleteStoryArc: AppMethod<[number, number], void>
  DeleteTimelineEntry: AppMethod<[number, number], void>
  DiscoverModels: AppMethod<[string, string], llm.ModelInfo[]>
  ExportNovel: AppMethod<[number, string], void>
  ExtractStyleSkillFromSamples: AppMethod<[styleSample.StartStyleSkillExtractionInput], styleSample.StyleSkillExtractionRun>
  ExtractStyle: AppMethod<[app.ExtractStyleInput], app.ExtractStyleResult>
  GenerateReferenceAnchoredDraft: AppMethod<[reference.GenerateAnchoredDraftInput], reference.AnchoredDraft>
  GenerateReferenceChapterBlueprint: AppMethod<[reference.GenerateChapterBlueprintInput], reference.ChapterBlueprint>
  GenerateReferenceCorpusBlueprintCandidates: AppMethod<[reference.GenerateCorpusBlueprintCandidatesInput], reference.CorpusBlueprintCandidates>
  GenerateReferenceCorpusInsertionDraft: AppMethod<[reference.GenerateCorpusInsertionDraftInput], reference.CorpusInsertionDraft>
 GenerateReferenceCorpusInsertionDraftCandidates: AppMethod<[reference.GenerateCorpusInsertionDraftCandidatesInput], reference.CorpusInsertionDraftCandidates>
GetReferenceCorpusGovernance: AppMethod<[{ session_id?: string | null }], reference.CorpusGovernance>
 GetReferenceCorpusNodeWindow: AppMethod<[{ anchor_id: number; node_id: string; previous_chapter_count?: number; next_chapter_count?: number; include_scene_siblings?: boolean; max_nodes?: number }], reference.CorpusNodeWindow | null>
 SetReferenceCorpusSessionLibraryBinding: AppMethod<[{ session_id: string; library_id: string; enabled: boolean }], reference.CorpusGovernance>
 UpdateReferenceCorpusLibraryMember: AppMethod<[{ library_id: string; anchor_id: number; enabled: boolean; source_quality?: string | null; disabled_reason?: string | null }], reference.CorpusGovernance>
 UpdateReferenceCorpusLicense: AppMethod<[{ anchor_id: number; license_state: string; authorization_evidence?: string | null; reuse_policy: string; max_verbatim_ratio?: number | null; cleared_for_insertion: boolean }], reference.CorpusGovernance>
 RebuildReferenceCorpusDedupGroups: AppMethod<[{ library_id?: string | null }], { members_scanned: number; groups_assigned: number }>
 RecordReferenceCorpusInsertionAudit: AppMethod<[{ audit_id: string; session_id: string; novel_id: number; chapter_number: number; candidate_id: string; draft: reference.CorpusInsertionDraft }], boolean>
 BuildReferenceCorpusAggregates: AppMethod<[{ library_ids: string[]; run_id?: string | null }], reference.CorpusAggregate[]>
 ListReferenceCorpusAggregates: AppMethod<[{ aggregate_type?: string | null }], reference.CorpusAggregate[]>
 RefreshReferenceCorpusReviewQueue: AppMethod<[{ confidence_threshold: number }], number>
 ListReferenceCorpusReviewQueue: AppMethod<[{ page_request: { cursor?: string | null; page_size: number; sort_by: string; sort_dir: string } }], reference.CorpusReviewQueuePage>
 ReviewReferenceCorpusItems: AppMethod<[{ queue_ids: string[]; review_state: string }], number>
 ReconcileReferenceCorpusRun: AppMethod<[{ anchor_id: number; new_run_id: string }], { superseded_observations: number; superseded_specimens: number; conflicts_queued: number; aggregates_marked_stale: number }>
  StartReferenceCorpusFeatureAnalysis: AppMethod<[reference.StartCorpusFeatureAnalysisInput], reference.CorpusFeatureAnalysisRun>
 GetReferenceCorpusFeatureAnalysisRun: AppMethod<[reference.GetCorpusFeatureAnalysisRunInput], reference.CorpusFeatureAnalysisRun | null>
 EnqueueReferenceCorpusAnalysisJob: AppMethod<[reference.EnqueueCorpusAnalysisJobInput], reference.CorpusAnalysisJob>
 GetReferenceCorpusAnalysisJob: AppMethod<[{ job_id: string }], reference.CorpusAnalysisJob | null>
 ListReferenceCorpusAnalysisJobs: AppMethod<[{ page_request: storage.PageRequest }], reference.CorpusAnalysisJobPage>
 PauseReferenceCorpusAnalysisJob: AppMethod<[{ job_id: string; expected_version: number }], reference.CorpusAnalysisJob>
 ResumeReferenceCorpusAnalysisJob: AppMethod<[{ job_id: string; expected_version: number; new_token_budget?: number | null }], reference.CorpusAnalysisJob>
 ReprioritizeReferenceCorpusAnalysisJob: AppMethod<[{ job_id: string; expected_version: number; priority_class: string; priority_value: number }], reference.CorpusAnalysisJob>
  StartReferenceCorpusTechniqueSpecimenAnalysis: AppMethod<[reference.StartCorpusTechniqueSpecimenAnalysisInput], reference.CorpusTechniqueSpecimenAnalysisRun>
  GetReferenceCorpusTechniqueSpecimenAnalysisRun: AppMethod<[reference.GetCorpusTechniqueSpecimenAnalysisRunInput], reference.CorpusTechniqueSpecimenAnalysisRun | null>
  ListReferenceCorpusFeatureObservations: AppMethod<[reference.ListCorpusFeatureObservationsInput], storage.PageResult_reference_CorpusFeatureObservation_>
  ListReferenceCorpusTechniqueSpecimens: AppMethod<[reference.ListCorpusTechniqueSpecimensInput], storage.PageResult_reference_CorpusTechniqueSpecimen_>
  GetAppConfig: AppMethod<[], config.AppConfig>
  GetArcNodes: AppMethod<[number, number, number], storyarc.ArcNode[]>
  GetChapterPlans: AppMethod<[number], timeline.ChapterPlan[]>
  GetChapters: AppMethod<[number], chapter.Chapter[]>
  GetCharacterRelations: AppMethod<[number], character.CharacterRelation[]>
  GetCharacters: AppMethod<[number], character.Character[]>
  GetContent: AppMethod<[number, string], string>
  GetCover: AppMethod<[number], novel.NovelCover | null>
  GetEmbeddingConfig: AppMethod<[], EmbeddingConfigView>
  GetGitAuthorSettings: AppMethod<[], git.GitAuthorSettings>
  GetGitCommitFiles: AppMethod<[git.GetGitCommitFilesInput], git.GitCommitFile[]>
  GetGitCommits: AppMethod<[git.GetGitCommitsInput], storage.PageResult_git_GitCommitSummary_>
  GetGitFileDiff: AppMethod<[git.GetGitFileDiffInput], git.GitFileDiff>
  GetLLMConfig: AppMethod<[], llm.LLMConfigView>
  GetLayoutSettings: AppMethod<[], layout.LayoutSettings>
  GetLocationRelations: AppMethod<[number], location.LocationRelation[]>
  GetLocations: AppMethod<[number], location.Location[]>
  GetMaxChapterNumber: AppMethod<[number], number>
  GetModels: AppMethod<[], llm.AvailableModel[]>
  GetNarrativePatternRun: AppMethod<[pattern.GetNarrativePatternRunInput], pattern.NarrativePatternRun | null>
  GetNarrativePatternTrace: AppMethod<[pattern.GetNarrativePatternRunInput], pattern.NarrativePatternTrace | null>
  GetNovelImportRecoveryStatus: AppMethod<[], novelImport.ImportRecoveryStatus>
  GetNovelImportRun: AppMethod<[novelImport.GetNovelImportRunInput], novelImport.ImportRun | null>
  GetNovels: AppMethod<[], novel.Novel[]>
  GetPlatform: AppMethod<[], Record<string, unknown>>
  GetPreferences: AppMethod<[number], app.PreferenceResult>
  GetReaderPerspectives: AppMethod<[number], reader.ReaderPerspective[]>
  GetReferenceAnchorBuildStatus: AppMethod<[number, number], reference.BuildStatus | null>
  GetReferenceAnchors: AppMethod<[number], reference.Anchor[]>
  GetReferenceAnchoredDraftAudits: AppMethod<[reference.GetAnchoredDraftAuditsInput], reference.AnchoredDraftAudit[]>
  GetReferenceChapterBlueprint: AppMethod<[number, number], reference.ChapterBlueprint | null>
  GetReferenceChapterBlueprints: AppMethod<[number, number | null], reference.ChapterBlueprintSummary[]>
  GetReferenceDraftCandidates: AppMethod<[reference.GetDraftCandidatesInput], reference.DraftParagraphCandidate[]>
  GetReferenceMaterialDetail: AppMethod<[reference.GetMaterialDetailInput], reference.MaterialDetail | null>
  GetReferenceMaterialTagReviewQueue: AppMethod<[reference.GetMaterialTagReviewQueueInput], storage.PageResult_reference_MaterialTagReviewItem_>
  GetReferenceSourceSegmentDetail: AppMethod<[reference.GetSourceSegmentDetailInput], reference.SourceSegmentDetail | null>
  GetReferenceOrchestrationRun: AppMethod<[number, string], reference.OrchestrationRun | null>
  GetReferenceOrchestrationRunEvents: AppMethod<[number, string], reference.OrchestrationRunEvent[]>
  GetReferenceOrchestrationRuns: AppMethod<[number, number | null], reference.OrchestrationRun[]>
  GetReferenceSourceProcessingDetail: AppMethod<[reference.GetSourceProcessingDetailInput], reference.SourceProcessingDetail | null>
  GetReferenceStyleAuditFindings: AppMethod<[reference.GetStyleAuditFindingsInput], reference.StyleAuditFinding[]>
  GetReferenceStyleProfileBuildStatus: AppMethod<[reference.GetStyleProfileBuildStatusInput], reference.StyleProfileBuildStatus | null>
  GetReferenceStyleProfile: AppMethod<[number, number], reference.StyleProfile | null>
  GetReferenceStyleProfiles: AppMethod<[reference.GetStyleProfilesInput], reference.StyleProfileSummary[]>
  GetReferenceUserFeedback: AppMethod<[reference.GetUserFeedbackInput], reference.UserFeedback[]>
  GetSession: AppMethod<[string], app.SessionDetail>
  GetSessionMessages: AppMethod<[string], session.Message[]>
  GetSessions: AppMethod<[app.GetSessionsInput], storage.PageResult_novel_app_SessionMeta_>
  GetSettings: AppMethod<[], config.AppSettings>
  GetSqliteVecStatus: AppMethod<[], SqliteVecStatusView>
  GetStyleSample: AppMethod<[styleSample.GetStyleSampleInput], styleSample.StyleSampleDetail | null>
  GetStyleSkillExtractionRun: AppMethod<[novelImport.GetNovelImportRunInput], styleSample.StyleSkillExtractionRun | null>
  GetStoryArcs: AppMethod<[number], storyarc.StoryArc[]>
  GetTimelineEntries: AppMethod<[number, number, number], timeline.TimelineEntry[]>
  GetUpdateCheckSettings: AppMethod<[], update.UpdateCheckSettings>
  GetWindowSettings: AppMethod<[], layout.WindowSettings>
  GetWritingActivity: AppMethod<[number], writing.DailyActivity[]>
  GetWritingStats: AppMethod<[], writing.WritingStats>
  Initialize: AppMethod<[string], void>
  IsInitialized: AppMethod<[], boolean>
  ListSkills: AppMethod<[app.ListSkillsInput], skill.SkillMeta[]>
  ListSlashCommands: AppMethod<[app.ListSlashCommandsInput], app.SlashCommand[]>
  PickNovelImportFile: AppMethod<[], string | null>
  PickReferenceSourceFile: AppMethod<[], string | null>
  PromoteReferenceAnchorsToWorkspaceCorpus: AppMethod<[reference.PromoteAnchorsToWorkspaceCorpusInput], reference.Anchor[]>
  PromoteReferenceAnchorToWorkspaceCorpus: AppMethod<[reference.PromoteAnchorToWorkspaceCorpusInput], reference.Anchor>
  RebuildReferenceAnchor: AppMethod<[number, number], reference.BuildStatus>
  RebuildNovelIndex: AppMethod<[number], void>
  ReconcileNovelImportRuns: AppMethod<[], novelImport.ImportReconciliationResult>
  RecordReferenceUserFeedback: AppMethod<[reference.RecordUserFeedbackInput], reference.UserFeedback>
  ResumeReferenceOrchestrationRun: AppMethod<[reference.ResumeOrchestrationRunInput], reference.OrchestrationRun>
  RestoreReferenceMaterials: AppMethod<[reference.RestoreMaterialsInput], void>
  RestoreReferenceStyleProfile: AppMethod<[reference.RestoreStyleProfileInput], reference.StyleProfile>
  ReviseReferenceChapterBlueprint: AppMethod<[reference.ReviseChapterBlueprintInput], reference.ChapterBlueprint>
  ReviewReferenceChapterBlueprint: AppMethod<[reference.ReviewChapterBlueprintInput], reference.ChapterBlueprintReview>
  SaveAvatar: AppMethod<[number[]], void>
  SaveContent: AppMethod<[app.SaveContentInput], void>
  SaveCover: AppMethod<[number, number[]], void>
  SaveEmbeddingConfig: AppMethod<[EmbeddingConfigView], void>
  SaveGitAuthorSettings: AppMethod<[git.SaveGitAuthorSettingsInput], git.GitAuthorSettings>
  SaveLayoutSettings: AppMethod<[layout.SaveLayoutSettingsInput], layout.LayoutSettings>
  SaveLLMConfig: AppMethod<[llm.LLMConfigView], void>
  SaveSettings: AppMethod<[app.SaveSettingsInput], void>
  SaveUpdateCheckSettings: AppMethod<[update.SaveUpdateCheckSettingsInput], update.UpdateCheckSettings>
  SaveUserName: AppMethod<[string], void>
  SaveWindowSettings: AppMethod<[layout.SaveWindowSettingsInput], layout.WindowSettings>
  SearchAll: AppMethod<[number, string], search.Result[]>
  SearchReferenceCorpusCandidates: AppMethod<[reference.SearchCorpusCandidatesInput], storage.PageResult_reference_CorpusCandidate_>
  SearchReferenceMaterials: AppMethod<[reference.SearchMaterialsInput], storage.PageResult_reference_MaterialSummary_>
  SearchStyleSamples: AppMethod<[styleSample.SearchStyleSamplesInput], storage.PageResult_styleSample_StyleSample_>
  SearchStoryMemory: AppMethod<[SearchStoryMemoryInput], SearchStoryMemoryResult>
  SetActiveNovel: AppMethod<[app.SetActiveNovelInput], void>
  SetApprovalMode: AppMethod<[string], void>
  SetChatPanelWidth: AppMethod<[number], void>
  SetLastSession: AppMethod<[string], void>
  SetReasoningEffort: AppMethod<[string], void>
  SetSelectedModel: AppMethod<[string, string], void>
  StartNarrativePatternExtraction: AppMethod<[pattern.StartNarrativePatternExtractionInput], pattern.NarrativePatternRun>
  StartNovelImport: AppMethod<[novelImport.StartNovelImportInput], novelImport.ImportRun>
  StartReferenceOrchestrationRun: AppMethod<[reference.StartOrchestrationRunInput], reference.OrchestrationRun>
  UpdateStyleSample: AppMethod<[styleSample.UpdateStyleSampleInput], styleSample.StyleSample>
  TestEmbeddingConnection: AppMethod<[EmbeddingConfigView], void>
  TestConnection: AppMethod<[app.TestConnectionInput], void>
  UpdateArcNode: AppMethod<[number, number, app.UpdateArcNodeInput], void>
  UpdateChapterPlan: AppMethod<[number, app.UpdateChapterPlanInput], void>
  UpdateChapterTitle: AppMethod<[number, number, string], void>
  UpdateCharacter: AppMethod<[number, number, app.UpdateCharacterInput], void>
  UpdateDataDir: AppMethod<[string], void>
  UpdateLocation: AppMethod<[number, number, app.UpdateLocationInput], void>
  UpdateNovel: AppMethod<[number, app.UpdateNovelInput], novel.Novel>
  UpdatePreference: AppMethod<[number, app.UpdatePreferenceInput], novel.PreferenceItem>
  UpdateReaderPerspective: AppMethod<[number, number, app.UpdateReaderPerspectiveInput], void>
  UpdateReferenceAnchorMetadata: AppMethod<[reference.UpdateAnchorMetadataInput], reference.Anchor>
  UpdateReferenceMaterialTags: AppMethod<[reference.UpdateMaterialTagsInput], reference.MaterialSummary>
  UpdateReferenceMaterialsTags: AppMethod<[reference.UpdateMaterialsTagsInput], reference.MaterialSummary[]>
  UpdateStoryArc: AppMethod<[number, number, app.UpdateStoryArcInput], void>
  UpdateTimelineEntry: AppMethod<[number, number, app.UpdateTimelineEntryInput], void>
}

export function invokeApp<TResult = unknown>(
  method: string,
  payload: unknown = {},
  options: BridgeInvokeOptions = {},
): Promise<TResult> {
  return bridge.invoke<TResult>(method, payload, options)
}

export function invokeAppArgs<TResult = unknown>(
  method: string,
  args: AppMethodArgs = [],
  options: BridgeInvokeOptions = {},
): Promise<TResult> {
  return invokeApp<TResult>(method, toArgsPayload(args), options)
}

export function createAppMethod<TResult = unknown, TArgs extends AppMethodArgs = AppMethodArgs>(
  method: string,
): (...args: TArgs) => Promise<TResult> {
  return (...args) => invokeAppArgs<TResult>(method, args)
}

export function toArgsPayload(args: AppMethodArgs): unknown {
  return args.length === 0 ? {} : { args: [...args] }
}

export const appApi: NovelistAppApi = {
  ApproveTool: appMethod<NovelistAppApi['ApproveTool']>('ApproveTool'),
  AdaptReferenceMaterial: appMethod<NovelistAppApi['AdaptReferenceMaterial']>('AdaptReferenceMaterial'),
  ApproveReferenceChapterBlueprint: appMethod<NovelistAppApi['ApproveReferenceChapterBlueprint']>('ApproveReferenceChapterBlueprint'),
  ArchiveReferenceStyleProfile: appMethod<NovelistAppApi['ArchiveReferenceStyleProfile']>('ArchiveReferenceStyleProfile'),
  AuditReferenceAnchoredDraft: appMethod<NovelistAppApi['AuditReferenceAnchoredDraft']>('AuditReferenceAnchoredDraft'),
  AuditReferenceReuse: appMethod<NovelistAppApi['AuditReferenceReuse']>('AuditReferenceReuse'),
  BackfillReferenceCorpusTechniqueVectorIndex: ((...args) => invokeAppArgs('BackfillReferenceCorpusTechniqueVectorIndex', args, { timeoutMs: null })) as NovelistAppApi['BackfillReferenceCorpusTechniqueVectorIndex'],
  BindReferenceBlueprintMaterials: appMethod<NovelistAppApi['BindReferenceBlueprintMaterials']>('BindReferenceBlueprintMaterials'),
  BuildReferenceStyleProfile: ((...args) => invokeAppArgs('BuildReferenceStyleProfile', args, { timeoutMs: null })) as NovelistAppApi['BuildReferenceStyleProfile'],
  CancelNovelImport: appMethod<NovelistAppApi['CancelNovelImport']>('CancelNovelImport'),
  CancelChat: appMethod<NovelistAppApi['CancelChat']>('CancelChat'),
  CancelNarrativePatternExtraction: appMethod<NovelistAppApi['CancelNarrativePatternExtraction']>('CancelNarrativePatternExtraction'),
CancelReferenceOrchestrationRun: appMethod<NovelistAppApi['CancelReferenceOrchestrationRun']>('CancelReferenceOrchestrationRun'),
 CancelReferenceCorpusAnalysisJob: appMethod<NovelistAppApi['CancelReferenceCorpusAnalysisJob']>('CancelReferenceCorpusAnalysisJob'),
  CancelReferenceStyleProfileBuild: appMethod<NovelistAppApi['CancelReferenceStyleProfileBuild']>('CancelReferenceStyleProfileBuild'),
  CancelStyleSkillExtraction: appMethod<NovelistAppApi['CancelStyleSkillExtraction']>('CancelStyleSkillExtraction'),
  Chat: ((...args) => invokeAppArgs('Chat', args, { timeoutMs: null })) as NovelistAppApi['Chat'],
  CheckForUpdates: appMethod<NovelistAppApi['CheckForUpdates']>('CheckForUpdates'),
  CompareReferenceStyleProfiles: appMethod<NovelistAppApi['CompareReferenceStyleProfiles']>('CompareReferenceStyleProfiles'),
  CompressContext: appMethod<NovelistAppApi['CompressContext']>('CompressContext'),
  CreateArcNode: appMethod<NovelistAppApi['CreateArcNode']>('CreateArcNode'),
  CreateChapter: appMethod<NovelistAppApi['CreateChapter']>('CreateChapter'),
  CreateCharacter: appMethod<NovelistAppApi['CreateCharacter']>('CreateCharacter'),
  CreateLocation: appMethod<NovelistAppApi['CreateLocation']>('CreateLocation'),
  CreateNovel: appMethod<NovelistAppApi['CreateNovel']>('CreateNovel'),
  CreatePreference: appMethod<NovelistAppApi['CreatePreference']>('CreatePreference'),
  CreateReaderPerspective: appMethod<NovelistAppApi['CreateReaderPerspective']>('CreateReaderPerspective'),
  CreateReferenceAnchor: appMethod<NovelistAppApi['CreateReferenceAnchor']>('CreateReferenceAnchor'),
  CreateReferenceAnchors: appMethod<NovelistAppApi['CreateReferenceAnchors']>('CreateReferenceAnchors'),
  CreateReferenceAnchorsWithResult: appMethod<NovelistAppApi['CreateReferenceAnchorsWithResult']>('CreateReferenceAnchorsWithResult'),
  CreateStyleSample: appMethod<NovelistAppApi['CreateStyleSample']>('CreateStyleSample'),
  CreateStoryArc: appMethod<NovelistAppApi['CreateStoryArc']>('CreateStoryArc'),
  CreateTimelineEntry: appMethod<NovelistAppApi['CreateTimelineEntry']>('CreateTimelineEntry'),
  DeleteArcNode: appMethod<NovelistAppApi['DeleteArcNode']>('DeleteArcNode'),
  DeleteCharacter: appMethod<NovelistAppApi['DeleteCharacter']>('DeleteCharacter'),
  DeleteCover: appMethod<NovelistAppApi['DeleteCover']>('DeleteCover'),
  DeleteLocation: appMethod<NovelistAppApi['DeleteLocation']>('DeleteLocation'),
  DeleteNovel: appMethod<NovelistAppApi['DeleteNovel']>('DeleteNovel'),
  DeletePreference: appMethod<NovelistAppApi['DeletePreference']>('DeletePreference'),
  DeleteReaderPerspective: appMethod<NovelistAppApi['DeleteReaderPerspective']>('DeleteReaderPerspective'),
  DeleteReferenceAnchor: appMethod<NovelistAppApi['DeleteReferenceAnchor']>('DeleteReferenceAnchor'),
  DeleteReferenceAnchors: appMethod<NovelistAppApi['DeleteReferenceAnchors']>('DeleteReferenceAnchors'),
  DeleteReferenceMaterials: appMethod<NovelistAppApi['DeleteReferenceMaterials']>('DeleteReferenceMaterials'),
  DeleteSkill: appMethod<NovelistAppApi['DeleteSkill']>('DeleteSkill'),
  DeleteStyleSample: appMethod<NovelistAppApi['DeleteStyleSample']>('DeleteStyleSample'),
  DeleteStoryArc: appMethod<NovelistAppApi['DeleteStoryArc']>('DeleteStoryArc'),
  DeleteTimelineEntry: appMethod<NovelistAppApi['DeleteTimelineEntry']>('DeleteTimelineEntry'),
  DiscoverModels: appMethod<NovelistAppApi['DiscoverModels']>('DiscoverModels'),
  ExportNovel: appMethod<NovelistAppApi['ExportNovel']>('ExportNovel'),
  ExtractStyleSkillFromSamples: ((...args) => invokeAppArgs('ExtractStyleSkillFromSamples', args, { timeoutMs: null })) as NovelistAppApi['ExtractStyleSkillFromSamples'],
  ExtractStyle: appMethod<NovelistAppApi['ExtractStyle']>('ExtractStyle'),
  GenerateReferenceAnchoredDraft: appMethod<NovelistAppApi['GenerateReferenceAnchoredDraft']>('GenerateReferenceAnchoredDraft'),
  GenerateReferenceChapterBlueprint: appMethod<NovelistAppApi['GenerateReferenceChapterBlueprint']>('GenerateReferenceChapterBlueprint'),
  GenerateReferenceCorpusBlueprintCandidates: appMethod<NovelistAppApi['GenerateReferenceCorpusBlueprintCandidates']>('GenerateReferenceCorpusBlueprintCandidates'),
  GenerateReferenceCorpusInsertionDraft: appMethod<NovelistAppApi['GenerateReferenceCorpusInsertionDraft']>('GenerateReferenceCorpusInsertionDraft'),
GenerateReferenceCorpusInsertionDraftCandidates: appMethod<NovelistAppApi['GenerateReferenceCorpusInsertionDraftCandidates']>('GenerateReferenceCorpusInsertionDraftCandidates'),
GetReferenceCorpusGovernance: appMethod<NovelistAppApi['GetReferenceCorpusGovernance']>('GetReferenceCorpusGovernance'),
 GetReferenceCorpusNodeWindow: appMethod<NovelistAppApi['GetReferenceCorpusNodeWindow']>('GetReferenceCorpusNodeWindow'),
 SetReferenceCorpusSessionLibraryBinding: appMethod<NovelistAppApi['SetReferenceCorpusSessionLibraryBinding']>('SetReferenceCorpusSessionLibraryBinding'),
 UpdateReferenceCorpusLibraryMember: appMethod<NovelistAppApi['UpdateReferenceCorpusLibraryMember']>('UpdateReferenceCorpusLibraryMember'),
 UpdateReferenceCorpusLicense: appMethod<NovelistAppApi['UpdateReferenceCorpusLicense']>('UpdateReferenceCorpusLicense'),
RebuildReferenceCorpusDedupGroups: appMethod<NovelistAppApi['RebuildReferenceCorpusDedupGroups']>('RebuildReferenceCorpusDedupGroups'),
 RecordReferenceCorpusInsertionAudit: appMethod<NovelistAppApi['RecordReferenceCorpusInsertionAudit']>('RecordReferenceCorpusInsertionAudit'),
 BuildReferenceCorpusAggregates: appMethod<NovelistAppApi['BuildReferenceCorpusAggregates']>('BuildReferenceCorpusAggregates'),
 ListReferenceCorpusAggregates: appMethod<NovelistAppApi['ListReferenceCorpusAggregates']>('ListReferenceCorpusAggregates'),
 RefreshReferenceCorpusReviewQueue: appMethod<NovelistAppApi['RefreshReferenceCorpusReviewQueue']>('RefreshReferenceCorpusReviewQueue'),
 ListReferenceCorpusReviewQueue: appMethod<NovelistAppApi['ListReferenceCorpusReviewQueue']>('ListReferenceCorpusReviewQueue'),
 ReviewReferenceCorpusItems: appMethod<NovelistAppApi['ReviewReferenceCorpusItems']>('ReviewReferenceCorpusItems'),
 ReconcileReferenceCorpusRun: appMethod<NovelistAppApi['ReconcileReferenceCorpusRun']>('ReconcileReferenceCorpusRun'),
  StartReferenceCorpusFeatureAnalysis: ((...args) => invokeAppArgs('StartReferenceCorpusFeatureAnalysis', args, { timeoutMs: null })) as NovelistAppApi['StartReferenceCorpusFeatureAnalysis'],
GetReferenceCorpusFeatureAnalysisRun: appMethod<NovelistAppApi['GetReferenceCorpusFeatureAnalysisRun']>('GetReferenceCorpusFeatureAnalysisRun'),
 EnqueueReferenceCorpusAnalysisJob: appMethod<NovelistAppApi['EnqueueReferenceCorpusAnalysisJob']>('EnqueueReferenceCorpusAnalysisJob'),
 GetReferenceCorpusAnalysisJob: appMethod<NovelistAppApi['GetReferenceCorpusAnalysisJob']>('GetReferenceCorpusAnalysisJob'),
 ListReferenceCorpusAnalysisJobs: appMethod<NovelistAppApi['ListReferenceCorpusAnalysisJobs']>('ListReferenceCorpusAnalysisJobs'),
 PauseReferenceCorpusAnalysisJob: appMethod<NovelistAppApi['PauseReferenceCorpusAnalysisJob']>('PauseReferenceCorpusAnalysisJob'),
 ResumeReferenceCorpusAnalysisJob: appMethod<NovelistAppApi['ResumeReferenceCorpusAnalysisJob']>('ResumeReferenceCorpusAnalysisJob'),
 ReprioritizeReferenceCorpusAnalysisJob: appMethod<NovelistAppApi['ReprioritizeReferenceCorpusAnalysisJob']>('ReprioritizeReferenceCorpusAnalysisJob'),
  StartReferenceCorpusTechniqueSpecimenAnalysis: ((...args) => invokeAppArgs('StartReferenceCorpusTechniqueSpecimenAnalysis', args, { timeoutMs: null })) as NovelistAppApi['StartReferenceCorpusTechniqueSpecimenAnalysis'],
  GetReferenceCorpusTechniqueSpecimenAnalysisRun: appMethod<NovelistAppApi['GetReferenceCorpusTechniqueSpecimenAnalysisRun']>('GetReferenceCorpusTechniqueSpecimenAnalysisRun'),
  ListReferenceCorpusFeatureObservations: appMethod<NovelistAppApi['ListReferenceCorpusFeatureObservations']>('ListReferenceCorpusFeatureObservations'),
  ListReferenceCorpusTechniqueSpecimens: appMethod<NovelistAppApi['ListReferenceCorpusTechniqueSpecimens']>('ListReferenceCorpusTechniqueSpecimens'),
  GetAppConfig: appMethod<NovelistAppApi['GetAppConfig']>('GetAppConfig'),
  GetArcNodes: appMethod<NovelistAppApi['GetArcNodes']>('GetArcNodes'),
  GetChapterPlans: appMethod<NovelistAppApi['GetChapterPlans']>('GetChapterPlans'),
  GetChapters: appMethod<NovelistAppApi['GetChapters']>('GetChapters'),
  GetCharacterRelations: appMethod<NovelistAppApi['GetCharacterRelations']>('GetCharacterRelations'),
  GetCharacters: appMethod<NovelistAppApi['GetCharacters']>('GetCharacters'),
  GetContent: appMethod<NovelistAppApi['GetContent']>('GetContent'),
  GetCover: appMethod<NovelistAppApi['GetCover']>('GetCover'),
  GetEmbeddingConfig: appMethod<NovelistAppApi['GetEmbeddingConfig']>('GetEmbeddingConfig'),
  GetGitAuthorSettings: appMethod<NovelistAppApi['GetGitAuthorSettings']>('GetGitAuthorSettings'),
  GetGitCommitFiles: appMethod<NovelistAppApi['GetGitCommitFiles']>('GetGitCommitFiles'),
  GetGitCommits: appMethod<NovelistAppApi['GetGitCommits']>('GetGitCommits'),
  GetGitFileDiff: appMethod<NovelistAppApi['GetGitFileDiff']>('GetGitFileDiff'),
  GetLLMConfig: appMethod<NovelistAppApi['GetLLMConfig']>('GetLLMConfig'),
  GetLayoutSettings: appMethod<NovelistAppApi['GetLayoutSettings']>('GetLayoutSettings'),
  GetLocationRelations: appMethod<NovelistAppApi['GetLocationRelations']>('GetLocationRelations'),
  GetLocations: appMethod<NovelistAppApi['GetLocations']>('GetLocations'),
  GetMaxChapterNumber: appMethod<NovelistAppApi['GetMaxChapterNumber']>('GetMaxChapterNumber'),
  GetModels: appMethod<NovelistAppApi['GetModels']>('GetModels'),
  GetNarrativePatternRun: appMethod<NovelistAppApi['GetNarrativePatternRun']>('GetNarrativePatternRun'),
  GetNarrativePatternTrace: appMethod<NovelistAppApi['GetNarrativePatternTrace']>('GetNarrativePatternTrace'),
  GetNovelImportRecoveryStatus: appMethod<NovelistAppApi['GetNovelImportRecoveryStatus']>('GetNovelImportRecoveryStatus'),
  GetNovelImportRun: appMethod<NovelistAppApi['GetNovelImportRun']>('GetNovelImportRun'),
  GetNovels: appMethod<NovelistAppApi['GetNovels']>('GetNovels'),
  GetPlatform: appMethod<NovelistAppApi['GetPlatform']>('GetPlatform'),
  GetPreferences: appMethod<NovelistAppApi['GetPreferences']>('GetPreferences'),
  GetReaderPerspectives: appMethod<NovelistAppApi['GetReaderPerspectives']>('GetReaderPerspectives'),
  GetReferenceAnchorBuildStatus: appMethod<NovelistAppApi['GetReferenceAnchorBuildStatus']>('GetReferenceAnchorBuildStatus'),
  GetReferenceAnchors: appMethod<NovelistAppApi['GetReferenceAnchors']>('GetReferenceAnchors'),
  GetReferenceAnchoredDraftAudits: appMethod<NovelistAppApi['GetReferenceAnchoredDraftAudits']>('GetReferenceAnchoredDraftAudits'),
  GetReferenceChapterBlueprint: appMethod<NovelistAppApi['GetReferenceChapterBlueprint']>('GetReferenceChapterBlueprint'),
  GetReferenceChapterBlueprints: appMethod<NovelistAppApi['GetReferenceChapterBlueprints']>('GetReferenceChapterBlueprints'),
  GetReferenceDraftCandidates: appMethod<NovelistAppApi['GetReferenceDraftCandidates']>('GetReferenceDraftCandidates'),
  GetReferenceMaterialDetail: appMethod<NovelistAppApi['GetReferenceMaterialDetail']>('GetReferenceMaterialDetail'),
  GetReferenceMaterialTagReviewQueue: appMethod<NovelistAppApi['GetReferenceMaterialTagReviewQueue']>('GetReferenceMaterialTagReviewQueue'),
  GetReferenceSourceSegmentDetail: appMethod<NovelistAppApi['GetReferenceSourceSegmentDetail']>('GetReferenceSourceSegmentDetail'),
  GetReferenceOrchestrationRun: appMethod<NovelistAppApi['GetReferenceOrchestrationRun']>('GetReferenceOrchestrationRun'),
  GetReferenceOrchestrationRunEvents: appMethod<NovelistAppApi['GetReferenceOrchestrationRunEvents']>('GetReferenceOrchestrationRunEvents'),
  GetReferenceOrchestrationRuns: appMethod<NovelistAppApi['GetReferenceOrchestrationRuns']>('GetReferenceOrchestrationRuns'),
  GetReferenceSourceProcessingDetail: appMethod<NovelistAppApi['GetReferenceSourceProcessingDetail']>('GetReferenceSourceProcessingDetail'),
  GetReferenceStyleAuditFindings: appMethod<NovelistAppApi['GetReferenceStyleAuditFindings']>('GetReferenceStyleAuditFindings'),
  GetReferenceStyleProfileBuildStatus: appMethod<NovelistAppApi['GetReferenceStyleProfileBuildStatus']>('GetReferenceStyleProfileBuildStatus'),
  GetReferenceStyleProfile: appMethod<NovelistAppApi['GetReferenceStyleProfile']>('GetReferenceStyleProfile'),
  GetReferenceStyleProfiles: appMethod<NovelistAppApi['GetReferenceStyleProfiles']>('GetReferenceStyleProfiles'),
  GetReferenceUserFeedback: appMethod<NovelistAppApi['GetReferenceUserFeedback']>('GetReferenceUserFeedback'),
  GetSession: appMethod<NovelistAppApi['GetSession']>('GetSession'),
  GetSessionMessages: appMethod<NovelistAppApi['GetSessionMessages']>('GetSessionMessages'),
  GetSessions: appMethod<NovelistAppApi['GetSessions']>('GetSessions'),
  GetSettings: appMethod<NovelistAppApi['GetSettings']>('GetSettings'),
  GetSqliteVecStatus: appMethod<NovelistAppApi['GetSqliteVecStatus']>('GetSqliteVecStatus'),
  GetStyleSample: appMethod<NovelistAppApi['GetStyleSample']>('GetStyleSample'),
  GetStyleSkillExtractionRun: appMethod<NovelistAppApi['GetStyleSkillExtractionRun']>('GetStyleSkillExtractionRun'),
  GetStoryArcs: appMethod<NovelistAppApi['GetStoryArcs']>('GetStoryArcs'),
  GetTimelineEntries: appMethod<NovelistAppApi['GetTimelineEntries']>('GetTimelineEntries'),
  GetUpdateCheckSettings: appMethod<NovelistAppApi['GetUpdateCheckSettings']>('GetUpdateCheckSettings'),
  GetWindowSettings: appMethod<NovelistAppApi['GetWindowSettings']>('GetWindowSettings'),
  GetWritingActivity: appMethod<NovelistAppApi['GetWritingActivity']>('GetWritingActivity'),
  GetWritingStats: appMethod<NovelistAppApi['GetWritingStats']>('GetWritingStats'),
  Initialize: appMethod<NovelistAppApi['Initialize']>('Initialize'),
  IsInitialized: appMethod<NovelistAppApi['IsInitialized']>('IsInitialized'),
  ListSkills: appMethod<NovelistAppApi['ListSkills']>('ListSkills'),
  ListSlashCommands: appMethod<NovelistAppApi['ListSlashCommands']>('ListSlashCommands'),
  PickNovelImportFile: appMethod<NovelistAppApi['PickNovelImportFile']>('PickNovelImportFile'),
  PickReferenceSourceFile: appMethod<NovelistAppApi['PickReferenceSourceFile']>('PickReferenceSourceFile'),
  PromoteReferenceAnchorsToWorkspaceCorpus: appMethod<NovelistAppApi['PromoteReferenceAnchorsToWorkspaceCorpus']>('PromoteReferenceAnchorsToWorkspaceCorpus'),
  PromoteReferenceAnchorToWorkspaceCorpus: appMethod<NovelistAppApi['PromoteReferenceAnchorToWorkspaceCorpus']>('PromoteReferenceAnchorToWorkspaceCorpus'),
  RebuildReferenceAnchor: appMethod<NovelistAppApi['RebuildReferenceAnchor']>('RebuildReferenceAnchor'),
  RebuildNovelIndex: appMethod<NovelistAppApi['RebuildNovelIndex']>('RebuildNovelIndex'),
  ReconcileNovelImportRuns: appMethod<NovelistAppApi['ReconcileNovelImportRuns']>('ReconcileNovelImportRuns'),
  RecordReferenceUserFeedback: appMethod<NovelistAppApi['RecordReferenceUserFeedback']>('RecordReferenceUserFeedback'),
  ResumeReferenceOrchestrationRun: appMethod<NovelistAppApi['ResumeReferenceOrchestrationRun']>('ResumeReferenceOrchestrationRun'),
  RestoreReferenceMaterials: appMethod<NovelistAppApi['RestoreReferenceMaterials']>('RestoreReferenceMaterials'),
  RestoreReferenceStyleProfile: appMethod<NovelistAppApi['RestoreReferenceStyleProfile']>('RestoreReferenceStyleProfile'),
  ReviseReferenceChapterBlueprint: appMethod<NovelistAppApi['ReviseReferenceChapterBlueprint']>('ReviseReferenceChapterBlueprint'),
  ReviewReferenceChapterBlueprint: appMethod<NovelistAppApi['ReviewReferenceChapterBlueprint']>('ReviewReferenceChapterBlueprint'),
  SaveAvatar: appMethod<NovelistAppApi['SaveAvatar']>('SaveAvatar'),
  SaveContent: appMethod<NovelistAppApi['SaveContent']>('SaveContent'),
  SaveCover: appMethod<NovelistAppApi['SaveCover']>('SaveCover'),
  SaveEmbeddingConfig: appMethod<NovelistAppApi['SaveEmbeddingConfig']>('SaveEmbeddingConfig'),
  SaveGitAuthorSettings: appMethod<NovelistAppApi['SaveGitAuthorSettings']>('SaveGitAuthorSettings'),
  SaveLayoutSettings: appMethod<NovelistAppApi['SaveLayoutSettings']>('SaveLayoutSettings'),
  SaveLLMConfig: appMethod<NovelistAppApi['SaveLLMConfig']>('SaveLLMConfig'),
  SaveSettings: appMethod<NovelistAppApi['SaveSettings']>('SaveSettings'),
  SaveUpdateCheckSettings: appMethod<NovelistAppApi['SaveUpdateCheckSettings']>('SaveUpdateCheckSettings'),
  SaveUserName: appMethod<NovelistAppApi['SaveUserName']>('SaveUserName'),
  SaveWindowSettings: appMethod<NovelistAppApi['SaveWindowSettings']>('SaveWindowSettings'),
  SearchAll: appMethod<NovelistAppApi['SearchAll']>('SearchAll'),
  SearchReferenceCorpusCandidates: appMethod<NovelistAppApi['SearchReferenceCorpusCandidates']>('SearchReferenceCorpusCandidates'),
  SearchReferenceMaterials: appMethod<NovelistAppApi['SearchReferenceMaterials']>('SearchReferenceMaterials'),
  SearchStyleSamples: appMethod<NovelistAppApi['SearchStyleSamples']>('SearchStyleSamples'),
  SearchStoryMemory: appMethod<NovelistAppApi['SearchStoryMemory']>('SearchStoryMemory'),
  SetActiveNovel: appMethod<NovelistAppApi['SetActiveNovel']>('SetActiveNovel'),
  SetApprovalMode: appMethod<NovelistAppApi['SetApprovalMode']>('SetApprovalMode'),
  SetChatPanelWidth: appMethod<NovelistAppApi['SetChatPanelWidth']>('SetChatPanelWidth'),
  SetLastSession: appMethod<NovelistAppApi['SetLastSession']>('SetLastSession'),
  SetReasoningEffort: appMethod<NovelistAppApi['SetReasoningEffort']>('SetReasoningEffort'),
  SetSelectedModel: appMethod<NovelistAppApi['SetSelectedModel']>('SetSelectedModel'),
  StartNarrativePatternExtraction: ((...args) => invokeAppArgs('StartNarrativePatternExtraction', args, { timeoutMs: null })) as NovelistAppApi['StartNarrativePatternExtraction'],
  StartNovelImport: ((...args) => invokeAppArgs('StartNovelImport', args, { timeoutMs: null })) as NovelistAppApi['StartNovelImport'],
  StartReferenceOrchestrationRun: appMethod<NovelistAppApi['StartReferenceOrchestrationRun']>('StartReferenceOrchestrationRun'),
  UpdateStyleSample: appMethod<NovelistAppApi['UpdateStyleSample']>('UpdateStyleSample'),
  TestEmbeddingConnection: appMethod<NovelistAppApi['TestEmbeddingConnection']>('TestEmbeddingConnection'),
  TestConnection: appMethod<NovelistAppApi['TestConnection']>('TestConnection'),
  UpdateArcNode: appMethod<NovelistAppApi['UpdateArcNode']>('UpdateArcNode'),
  UpdateChapterPlan: appMethod<NovelistAppApi['UpdateChapterPlan']>('UpdateChapterPlan'),
  UpdateChapterTitle: appMethod<NovelistAppApi['UpdateChapterTitle']>('UpdateChapterTitle'),
  UpdateCharacter: appMethod<NovelistAppApi['UpdateCharacter']>('UpdateCharacter'),
  UpdateDataDir: appMethod<NovelistAppApi['UpdateDataDir']>('UpdateDataDir'),
  UpdateLocation: appMethod<NovelistAppApi['UpdateLocation']>('UpdateLocation'),
  UpdateNovel: appMethod<NovelistAppApi['UpdateNovel']>('UpdateNovel'),
  UpdatePreference: appMethod<NovelistAppApi['UpdatePreference']>('UpdatePreference'),
  UpdateReaderPerspective: appMethod<NovelistAppApi['UpdateReaderPerspective']>('UpdateReaderPerspective'),
  UpdateReferenceAnchorMetadata: appMethod<NovelistAppApi['UpdateReferenceAnchorMetadata']>('UpdateReferenceAnchorMetadata'),
  UpdateReferenceMaterialTags: appMethod<NovelistAppApi['UpdateReferenceMaterialTags']>('UpdateReferenceMaterialTags'),
  UpdateReferenceMaterialsTags: appMethod<NovelistAppApi['UpdateReferenceMaterialsTags']>('UpdateReferenceMaterialsTags'),
  UpdateStoryArc: appMethod<NovelistAppApi['UpdateStoryArc']>('UpdateStoryArc'),
  UpdateTimelineEntry: appMethod<NovelistAppApi['UpdateTimelineEntry']>('UpdateTimelineEntry'),
}

function appMethod<TMethod extends BridgeBackedMethod>(method: string): TMethod {
  return createAppMethod(method) as TMethod
}
