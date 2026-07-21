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
  AnalyzeReferenceChapterSplit: AppMethod<[reference.AnalyzeChapterSplitInput], reference.ChapterSplitProfile>
  CancelNovelImport: AppMethod<[novelImport.CancelNovelImportInput], novelImport.ImportRun>
  CancelChat: AppMethod<[string], void>
  CancelNarrativePatternExtraction: AppMethod<[pattern.CancelNarrativePatternExtractionInput], pattern.NarrativePatternRun>
  CancelStyleSkillExtraction: AppMethod<[styleSample.CancelStyleSkillExtractionInput], styleSample.StyleSkillExtractionRun>
  Chat: AppMethod<[app.ChatInput], app.ChatResult>
  CheckForUpdates: AppMethod<[update.CheckForUpdatesInput], update.UpdateCheckResult>
  ConfirmReferenceChapterSplit: AppMethod<[reference.ConfirmChapterSplitInput], reference.ChapterSplitProfile>
  CompressContext: AppMethod<[app.CompressInput], app.CompressResult>
  CreateArcNode: AppMethod<[number, app.CreateArcNodeInput], storyarc.ArcNode>
  CreateChapter: AppMethod<[app.CreateChapterInput], chapter.Chapter>
  CreateCharacter: AppMethod<[number, app.CreateCharacterInput], character.Character>
  CreateLocation: AppMethod<[number, app.CreateLocationInput], location.Location>
  CreateNovel: AppMethod<[app.CreateNovelInput], novel.Novel>
  CreatePreference: AppMethod<[number, app.CreatePreferenceInput], novel.PreferenceItem>
  CreateReaderPerspective: AppMethod<[number, app.CreateReaderPerspectiveInput], reader.ReaderPerspective>
  RegisterReferenceMaterializationSource: AppMethod<[reference.CreateAnchorInput], reference.Anchor>
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
  DeleteSkill: AppMethod<[app.DeleteSkillInput], void>
  DeleteStyleSample: AppMethod<[styleSample.DeleteStyleSampleInput], void>
  DeleteStoryArc: AppMethod<[number, number], void>
  DeleteTimelineEntry: AppMethod<[number, number], void>
  DiscoverModels: AppMethod<[string, string], llm.ModelInfo[]>
  ExportNovel: AppMethod<[number, string], void>
  ExtractStyleSkillFromSamples: AppMethod<[styleSample.StartStyleSkillExtractionInput], styleSample.StyleSkillExtractionRun>
  ExtractStyle: AppMethod<[app.ExtractStyleInput], app.ExtractStyleResult>
  GenerateReferenceBlueprints: AppMethod<[reference.GenerateWritingBlueprintsInput], reference.WritingSession>
  GenerateReferenceDraftCandidates: AppMethod<[reference.GenerateWritingDraftCandidatesInput], reference.WritingDraftCandidates>
  GetReferenceWritingSession: AppMethod<[reference.GetWritingSessionInput], reference.WritingSession | null>
 EnqueueReferenceMaterialization: AppMethod<[reference.EnqueueMaterializationInput], reference.MaterializationStatus>
 RetryReferenceMaterialization: AppMethod<[reference.RetryMaterializationInput], reference.MaterializationStatus>
 ListReferenceMaterializationChapterProgress: AppMethod<[reference.ListMaterializationChapterProgressInput], storage.PageResult_reference_MaterializationChapterProgress_>
 ListReferenceMaterials: AppMethod<[reference.ListReferenceMaterialsInput], storage.PageResult_reference_ReferenceMaterialListItem_>
 GenerateReferenceMaterializationBlueprintPreview: AppMethod<[reference.GenerateMaterializationBlueprintPreviewInput], reference.MaterializationBlueprintPreview>
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
  GetReferenceAnchors: AppMethod<[number], reference.Anchor[]>
  GetReferenceMaterializationStatus: AppMethod<[reference.GetMaterializationStatusInput], reference.MaterializationStatus | null>
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
  PreviewReferenceChapterSplit: AppMethod<[reference.PreviewChapterSplitInput], reference.ChapterSplitProfile>
  RebuildNovelIndex: AppMethod<[number], void>
  ReconcileNovelImportRuns: AppMethod<[], novelImport.ImportReconciliationResult>
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
  SearchReferenceMaterials: AppMethod<[reference.SearchReferenceMaterialsInput], reference.ReferenceMaterialSearchHit[]>
  SelectReferenceBlueprint: AppMethod<[reference.SelectWritingBlueprintInput], reference.WritingSession>
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
  AnalyzeReferenceChapterSplit: ((...args) => invokeAppArgs('AnalyzeReferenceChapterSplit', args, { timeoutMs: null })) as NovelistAppApi['AnalyzeReferenceChapterSplit'],
  CancelNovelImport: appMethod<NovelistAppApi['CancelNovelImport']>('CancelNovelImport'),
  CancelChat: appMethod<NovelistAppApi['CancelChat']>('CancelChat'),
  CancelNarrativePatternExtraction: appMethod<NovelistAppApi['CancelNarrativePatternExtraction']>('CancelNarrativePatternExtraction'),
  CancelStyleSkillExtraction: appMethod<NovelistAppApi['CancelStyleSkillExtraction']>('CancelStyleSkillExtraction'),
  Chat: ((...args) => invokeAppArgs('Chat', args, { timeoutMs: null })) as NovelistAppApi['Chat'],
  CheckForUpdates: appMethod<NovelistAppApi['CheckForUpdates']>('CheckForUpdates'),
  ConfirmReferenceChapterSplit: appMethod<NovelistAppApi['ConfirmReferenceChapterSplit']>('ConfirmReferenceChapterSplit'),
  CompressContext: appMethod<NovelistAppApi['CompressContext']>('CompressContext'),
  CreateArcNode: appMethod<NovelistAppApi['CreateArcNode']>('CreateArcNode'),
  CreateChapter: appMethod<NovelistAppApi['CreateChapter']>('CreateChapter'),
  CreateCharacter: appMethod<NovelistAppApi['CreateCharacter']>('CreateCharacter'),
  CreateLocation: appMethod<NovelistAppApi['CreateLocation']>('CreateLocation'),
  CreateNovel: appMethod<NovelistAppApi['CreateNovel']>('CreateNovel'),
  CreatePreference: appMethod<NovelistAppApi['CreatePreference']>('CreatePreference'),
  CreateReaderPerspective: appMethod<NovelistAppApi['CreateReaderPerspective']>('CreateReaderPerspective'),
  RegisterReferenceMaterializationSource: ((...args) => invokeAppArgs('RegisterReferenceMaterializationSource', args, { timeoutMs: null })) as NovelistAppApi['RegisterReferenceMaterializationSource'],
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
  DeleteReferenceAnchor: ((...args) => invokeAppArgs('DeleteReferenceAnchor', args, { timeoutMs: null })) as NovelistAppApi['DeleteReferenceAnchor'],
  DeleteReferenceAnchors: appMethod<NovelistAppApi['DeleteReferenceAnchors']>('DeleteReferenceAnchors'),
  DeleteSkill: appMethod<NovelistAppApi['DeleteSkill']>('DeleteSkill'),
  DeleteStyleSample: appMethod<NovelistAppApi['DeleteStyleSample']>('DeleteStyleSample'),
  DeleteStoryArc: appMethod<NovelistAppApi['DeleteStoryArc']>('DeleteStoryArc'),
  DeleteTimelineEntry: appMethod<NovelistAppApi['DeleteTimelineEntry']>('DeleteTimelineEntry'),
  DiscoverModels: appMethod<NovelistAppApi['DiscoverModels']>('DiscoverModels'),
  ExportNovel: appMethod<NovelistAppApi['ExportNovel']>('ExportNovel'),
  ExtractStyleSkillFromSamples: ((...args) => invokeAppArgs('ExtractStyleSkillFromSamples', args, { timeoutMs: null })) as NovelistAppApi['ExtractStyleSkillFromSamples'],
  ExtractStyle: appMethod<NovelistAppApi['ExtractStyle']>('ExtractStyle'),
  GenerateReferenceBlueprints: ((...args) => invokeAppArgs('GenerateReferenceBlueprints', args, { timeoutMs: null })) as NovelistAppApi['GenerateReferenceBlueprints'],
  GenerateReferenceDraftCandidates: ((...args) => invokeAppArgs('GenerateReferenceDraftCandidates', args, { timeoutMs: null })) as NovelistAppApi['GenerateReferenceDraftCandidates'],
  GetReferenceWritingSession: appMethod<NovelistAppApi['GetReferenceWritingSession']>('GetReferenceWritingSession'),
 EnqueueReferenceMaterialization: appMethod<NovelistAppApi['EnqueueReferenceMaterialization']>('EnqueueReferenceMaterialization'),
 RetryReferenceMaterialization: appMethod<NovelistAppApi['RetryReferenceMaterialization']>('RetryReferenceMaterialization'),
 ListReferenceMaterializationChapterProgress: appMethod<NovelistAppApi['ListReferenceMaterializationChapterProgress']>('ListReferenceMaterializationChapterProgress'),
 ListReferenceMaterials: appMethod<NovelistAppApi['ListReferenceMaterials']>('ListReferenceMaterials'),
  GenerateReferenceMaterializationBlueprintPreview: ((...args) => invokeAppArgs('GenerateReferenceMaterializationBlueprintPreview', args, { timeoutMs: null })) as NovelistAppApi['GenerateReferenceMaterializationBlueprintPreview'],
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
  GetReferenceAnchors: appMethod<NovelistAppApi['GetReferenceAnchors']>('GetReferenceAnchors'),
  GetReferenceMaterializationStatus: appMethod<NovelistAppApi['GetReferenceMaterializationStatus']>('GetReferenceMaterializationStatus'),
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
  PreviewReferenceChapterSplit: appMethod<NovelistAppApi['PreviewReferenceChapterSplit']>('PreviewReferenceChapterSplit'),
  RebuildNovelIndex: appMethod<NovelistAppApi['RebuildNovelIndex']>('RebuildNovelIndex'),
  ReconcileNovelImportRuns: appMethod<NovelistAppApi['ReconcileNovelImportRuns']>('ReconcileNovelImportRuns'),
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
  SearchReferenceMaterials: appMethod<NovelistAppApi['SearchReferenceMaterials']>('SearchReferenceMaterials'),
  SelectReferenceBlueprint: appMethod<NovelistAppApi['SelectReferenceBlueprint']>('SelectReferenceBlueprint'),
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
  UpdateStoryArc: appMethod<NovelistAppApi['UpdateStoryArc']>('UpdateStoryArc'),
  UpdateTimelineEntry: appMethod<NovelistAppApi['UpdateTimelineEntry']>('UpdateTimelineEntry'),
}

function appMethod<TMethod extends BridgeBackedMethod>(method: string): TMethod {
  return createAppMethod(method) as TMethod
}
