import { bridge, type BridgeInvokeOptions } from './bridge'
import type {
  app,
  chapter,
  character,
  config,
  llm,
  location,
  novel,
  reader,
  reference,
  search,
  session,
  skill,
  storage,
  storyarc,
  timeline,
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
  onnx_runtime_path: string
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
  AuditReferenceAnchoredDraft: AppMethod<[reference.AuditAnchoredDraftInput], reference.AnchoredDraftAudit>
  AuditReferenceReuse: AppMethod<[reference.AuditReuseInput], reference.ReuseAudit>
  BindReferenceBlueprintMaterials: AppMethod<[reference.BindBlueprintMaterialsInput], reference.BlueprintMaterialBindingResult>
  CancelChat: AppMethod<[string], void>
  CancelReferenceOrchestrationRun: AppMethod<[reference.CancelOrchestrationRunInput], reference.OrchestrationRun>
  Chat: AppMethod<[app.ChatInput], app.ChatResult>
  CompressContext: AppMethod<[app.CompressInput], app.CompressResult>
  CreateArcNode: AppMethod<[number, app.CreateArcNodeInput], storyarc.ArcNode>
  CreateChapter: AppMethod<[app.CreateChapterInput], chapter.Chapter>
  CreateCharacter: AppMethod<[number, app.CreateCharacterInput], character.Character>
  CreateLocation: AppMethod<[number, app.CreateLocationInput], location.Location>
  CreateNovel: AppMethod<[app.CreateNovelInput], novel.Novel>
  CreatePreference: AppMethod<[number, app.CreatePreferenceInput], novel.PreferenceItem>
  CreateReaderPerspective: AppMethod<[number, app.CreateReaderPerspectiveInput], reader.ReaderPerspective>
  CreateReferenceAnchor: AppMethod<[reference.CreateAnchorInput], reference.Anchor>
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
  DeleteSkill: AppMethod<[app.DeleteSkillInput], void>
  DeleteStoryArc: AppMethod<[number, number], void>
  DeleteTimelineEntry: AppMethod<[number, number], void>
  DiscoverModels: AppMethod<[string, string], llm.ModelInfo[]>
  ExportNovel: AppMethod<[number, string], void>
  ExtractStyle: AppMethod<[app.ExtractStyleInput], app.ExtractStyleResult>
  GenerateReferenceAnchoredDraft: AppMethod<[reference.GenerateAnchoredDraftInput], reference.AnchoredDraft>
  GenerateReferenceChapterBlueprint: AppMethod<[reference.GenerateChapterBlueprintInput], reference.ChapterBlueprint>
  GetAppConfig: AppMethod<[], Record<string, unknown>>
  GetArcNodes: AppMethod<[number, number, number], storyarc.ArcNode[]>
  GetChapterPlans: AppMethod<[number], timeline.ChapterPlan[]>
  GetChapters: AppMethod<[number], chapter.Chapter[]>
  GetCharacterRelations: AppMethod<[number], character.CharacterRelation[]>
  GetCharacters: AppMethod<[number], character.Character[]>
  GetContent: AppMethod<[number, string], string>
  GetCover: AppMethod<[number], novel.NovelCover | null>
  GetEmbeddingConfig: AppMethod<[], EmbeddingConfigView>
  GetLLMConfig: AppMethod<[], llm.LLMConfigView>
  GetLocationRelations: AppMethod<[number], location.LocationRelation[]>
  GetLocations: AppMethod<[number], location.Location[]>
  GetMaxChapterNumber: AppMethod<[number], number>
  GetModels: AppMethod<[], llm.AvailableModel[]>
  GetNovels: AppMethod<[], novel.Novel[]>
  GetPlatform: AppMethod<[], Record<string, unknown>>
  GetPreferences: AppMethod<[number], app.PreferenceResult>
  GetReaderPerspectives: AppMethod<[number], reader.ReaderPerspective[]>
  GetReferenceAnchorBuildStatus: AppMethod<[number, number], reference.BuildStatus | null>
  GetReferenceAnchors: AppMethod<[number], reference.Anchor[]>
  GetReferenceChapterBlueprint: AppMethod<[number, number], reference.ChapterBlueprint | null>
  GetReferenceChapterBlueprints: AppMethod<[number, number | null], reference.ChapterBlueprintSummary[]>
  GetReferenceOrchestrationRun: AppMethod<[number, string], reference.OrchestrationRun | null>
  GetReferenceOrchestrationRunEvents: AppMethod<[number, string], reference.OrchestrationRunEvent[]>
  GetReferenceOrchestrationRuns: AppMethod<[number, number | null], reference.OrchestrationRun[]>
  GetReferenceUserFeedback: AppMethod<[reference.GetUserFeedbackInput], reference.UserFeedback[]>
  GetSession: AppMethod<[string], app.SessionDetail>
  GetSessionMessages: AppMethod<[string], session.Message[]>
  GetSessions: AppMethod<[app.GetSessionsInput], storage.PageResult_novel_app_SessionMeta_>
  GetSettings: AppMethod<[], config.AppSettings>
  GetSqliteVecStatus: AppMethod<[], SqliteVecStatusView>
  GetStoryArcs: AppMethod<[number], storyarc.StoryArc[]>
  GetTimelineEntries: AppMethod<[number, number, number], timeline.TimelineEntry[]>
  GetWritingActivity: AppMethod<[number], writing.DailyActivity[]>
  GetWritingStats: AppMethod<[], writing.WritingStats>
  Initialize: AppMethod<[string], void>
  IsInitialized: AppMethod<[], boolean>
  ListSkills: AppMethod<[app.ListSkillsInput], skill.SkillMeta[]>
  ListSlashCommands: AppMethod<[app.ListSlashCommandsInput], app.SlashCommand[]>
  PickReferenceSourceFile: AppMethod<[], string | null>
  RebuildReferenceAnchor: AppMethod<[number, number], reference.BuildStatus>
  RebuildNovelIndex: AppMethod<[number], void>
  RecordReferenceUserFeedback: AppMethod<[reference.RecordUserFeedbackInput], reference.UserFeedback>
  ResumeReferenceOrchestrationRun: AppMethod<[reference.ResumeOrchestrationRunInput], reference.OrchestrationRun>
  ReviseReferenceChapterBlueprint: AppMethod<[reference.ReviseChapterBlueprintInput], reference.ChapterBlueprint>
  ReviewReferenceChapterBlueprint: AppMethod<[reference.ReviewChapterBlueprintInput], reference.ChapterBlueprintReview>
  SaveAvatar: AppMethod<[number[]], void>
  SaveContent: AppMethod<[app.SaveContentInput], void>
  SaveCover: AppMethod<[number, number[]], void>
  SaveEmbeddingConfig: AppMethod<[EmbeddingConfigView], void>
  SaveLLMConfig: AppMethod<[llm.LLMConfigView], void>
  SaveSettings: AppMethod<[app.SaveSettingsInput], void>
  SaveUserName: AppMethod<[string], void>
  SearchAll: AppMethod<[number, string], search.Result[]>
  SearchReferenceMaterials: AppMethod<[reference.SearchMaterialsInput], storage.PageResult_reference_Material_>
  SearchStoryMemory: AppMethod<[SearchStoryMemoryInput], SearchStoryMemoryResult>
  SetActiveNovel: AppMethod<[app.SetActiveNovelInput], void>
  SetApprovalMode: AppMethod<[string], void>
  SetChatPanelWidth: AppMethod<[number], void>
  SetLastSession: AppMethod<[string], void>
  SetReasoningEffort: AppMethod<[string], void>
  SetSelectedModel: AppMethod<[string, string], void>
  StartReferenceOrchestrationRun: AppMethod<[reference.StartOrchestrationRunInput], reference.OrchestrationRun>
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
  UpdateReferenceMaterialTags: AppMethod<[reference.UpdateMaterialTagsInput], reference.Material>
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
  AuditReferenceAnchoredDraft: appMethod<NovelistAppApi['AuditReferenceAnchoredDraft']>('AuditReferenceAnchoredDraft'),
  AuditReferenceReuse: appMethod<NovelistAppApi['AuditReferenceReuse']>('AuditReferenceReuse'),
  BindReferenceBlueprintMaterials: appMethod<NovelistAppApi['BindReferenceBlueprintMaterials']>('BindReferenceBlueprintMaterials'),
  CancelChat: appMethod<NovelistAppApi['CancelChat']>('CancelChat'),
  CancelReferenceOrchestrationRun: appMethod<NovelistAppApi['CancelReferenceOrchestrationRun']>('CancelReferenceOrchestrationRun'),
  Chat: ((...args) => invokeAppArgs('Chat', args, { timeoutMs: null })) as NovelistAppApi['Chat'],
  CompressContext: appMethod<NovelistAppApi['CompressContext']>('CompressContext'),
  CreateArcNode: appMethod<NovelistAppApi['CreateArcNode']>('CreateArcNode'),
  CreateChapter: appMethod<NovelistAppApi['CreateChapter']>('CreateChapter'),
  CreateCharacter: appMethod<NovelistAppApi['CreateCharacter']>('CreateCharacter'),
  CreateLocation: appMethod<NovelistAppApi['CreateLocation']>('CreateLocation'),
  CreateNovel: appMethod<NovelistAppApi['CreateNovel']>('CreateNovel'),
  CreatePreference: appMethod<NovelistAppApi['CreatePreference']>('CreatePreference'),
  CreateReaderPerspective: appMethod<NovelistAppApi['CreateReaderPerspective']>('CreateReaderPerspective'),
  CreateReferenceAnchor: appMethod<NovelistAppApi['CreateReferenceAnchor']>('CreateReferenceAnchor'),
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
  DeleteSkill: appMethod<NovelistAppApi['DeleteSkill']>('DeleteSkill'),
  DeleteStoryArc: appMethod<NovelistAppApi['DeleteStoryArc']>('DeleteStoryArc'),
  DeleteTimelineEntry: appMethod<NovelistAppApi['DeleteTimelineEntry']>('DeleteTimelineEntry'),
  DiscoverModels: appMethod<NovelistAppApi['DiscoverModels']>('DiscoverModels'),
  ExportNovel: appMethod<NovelistAppApi['ExportNovel']>('ExportNovel'),
  ExtractStyle: appMethod<NovelistAppApi['ExtractStyle']>('ExtractStyle'),
  GenerateReferenceAnchoredDraft: appMethod<NovelistAppApi['GenerateReferenceAnchoredDraft']>('GenerateReferenceAnchoredDraft'),
  GenerateReferenceChapterBlueprint: appMethod<NovelistAppApi['GenerateReferenceChapterBlueprint']>('GenerateReferenceChapterBlueprint'),
  GetAppConfig: appMethod<NovelistAppApi['GetAppConfig']>('GetAppConfig'),
  GetArcNodes: appMethod<NovelistAppApi['GetArcNodes']>('GetArcNodes'),
  GetChapterPlans: appMethod<NovelistAppApi['GetChapterPlans']>('GetChapterPlans'),
  GetChapters: appMethod<NovelistAppApi['GetChapters']>('GetChapters'),
  GetCharacterRelations: appMethod<NovelistAppApi['GetCharacterRelations']>('GetCharacterRelations'),
  GetCharacters: appMethod<NovelistAppApi['GetCharacters']>('GetCharacters'),
  GetContent: appMethod<NovelistAppApi['GetContent']>('GetContent'),
  GetCover: appMethod<NovelistAppApi['GetCover']>('GetCover'),
  GetEmbeddingConfig: appMethod<NovelistAppApi['GetEmbeddingConfig']>('GetEmbeddingConfig'),
  GetLLMConfig: appMethod<NovelistAppApi['GetLLMConfig']>('GetLLMConfig'),
  GetLocationRelations: appMethod<NovelistAppApi['GetLocationRelations']>('GetLocationRelations'),
  GetLocations: appMethod<NovelistAppApi['GetLocations']>('GetLocations'),
  GetMaxChapterNumber: appMethod<NovelistAppApi['GetMaxChapterNumber']>('GetMaxChapterNumber'),
  GetModels: appMethod<NovelistAppApi['GetModels']>('GetModels'),
  GetNovels: appMethod<NovelistAppApi['GetNovels']>('GetNovels'),
  GetPlatform: appMethod<NovelistAppApi['GetPlatform']>('GetPlatform'),
  GetPreferences: appMethod<NovelistAppApi['GetPreferences']>('GetPreferences'),
  GetReaderPerspectives: appMethod<NovelistAppApi['GetReaderPerspectives']>('GetReaderPerspectives'),
  GetReferenceAnchorBuildStatus: appMethod<NovelistAppApi['GetReferenceAnchorBuildStatus']>('GetReferenceAnchorBuildStatus'),
  GetReferenceAnchors: appMethod<NovelistAppApi['GetReferenceAnchors']>('GetReferenceAnchors'),
  GetReferenceChapterBlueprint: appMethod<NovelistAppApi['GetReferenceChapterBlueprint']>('GetReferenceChapterBlueprint'),
  GetReferenceChapterBlueprints: appMethod<NovelistAppApi['GetReferenceChapterBlueprints']>('GetReferenceChapterBlueprints'),
  GetReferenceOrchestrationRun: appMethod<NovelistAppApi['GetReferenceOrchestrationRun']>('GetReferenceOrchestrationRun'),
  GetReferenceOrchestrationRunEvents: appMethod<NovelistAppApi['GetReferenceOrchestrationRunEvents']>('GetReferenceOrchestrationRunEvents'),
  GetReferenceOrchestrationRuns: appMethod<NovelistAppApi['GetReferenceOrchestrationRuns']>('GetReferenceOrchestrationRuns'),
  GetReferenceUserFeedback: appMethod<NovelistAppApi['GetReferenceUserFeedback']>('GetReferenceUserFeedback'),
  GetSession: appMethod<NovelistAppApi['GetSession']>('GetSession'),
  GetSessionMessages: appMethod<NovelistAppApi['GetSessionMessages']>('GetSessionMessages'),
  GetSessions: appMethod<NovelistAppApi['GetSessions']>('GetSessions'),
  GetSettings: appMethod<NovelistAppApi['GetSettings']>('GetSettings'),
  GetSqliteVecStatus: appMethod<NovelistAppApi['GetSqliteVecStatus']>('GetSqliteVecStatus'),
  GetStoryArcs: appMethod<NovelistAppApi['GetStoryArcs']>('GetStoryArcs'),
  GetTimelineEntries: appMethod<NovelistAppApi['GetTimelineEntries']>('GetTimelineEntries'),
  GetWritingActivity: appMethod<NovelistAppApi['GetWritingActivity']>('GetWritingActivity'),
  GetWritingStats: appMethod<NovelistAppApi['GetWritingStats']>('GetWritingStats'),
  Initialize: appMethod<NovelistAppApi['Initialize']>('Initialize'),
  IsInitialized: appMethod<NovelistAppApi['IsInitialized']>('IsInitialized'),
  ListSkills: appMethod<NovelistAppApi['ListSkills']>('ListSkills'),
  ListSlashCommands: appMethod<NovelistAppApi['ListSlashCommands']>('ListSlashCommands'),
  PickReferenceSourceFile: appMethod<NovelistAppApi['PickReferenceSourceFile']>('PickReferenceSourceFile'),
  RebuildReferenceAnchor: appMethod<NovelistAppApi['RebuildReferenceAnchor']>('RebuildReferenceAnchor'),
  RebuildNovelIndex: appMethod<NovelistAppApi['RebuildNovelIndex']>('RebuildNovelIndex'),
  RecordReferenceUserFeedback: appMethod<NovelistAppApi['RecordReferenceUserFeedback']>('RecordReferenceUserFeedback'),
  ResumeReferenceOrchestrationRun: appMethod<NovelistAppApi['ResumeReferenceOrchestrationRun']>('ResumeReferenceOrchestrationRun'),
  ReviseReferenceChapterBlueprint: appMethod<NovelistAppApi['ReviseReferenceChapterBlueprint']>('ReviseReferenceChapterBlueprint'),
  ReviewReferenceChapterBlueprint: appMethod<NovelistAppApi['ReviewReferenceChapterBlueprint']>('ReviewReferenceChapterBlueprint'),
  SaveAvatar: appMethod<NovelistAppApi['SaveAvatar']>('SaveAvatar'),
  SaveContent: appMethod<NovelistAppApi['SaveContent']>('SaveContent'),
  SaveCover: appMethod<NovelistAppApi['SaveCover']>('SaveCover'),
  SaveEmbeddingConfig: appMethod<NovelistAppApi['SaveEmbeddingConfig']>('SaveEmbeddingConfig'),
  SaveLLMConfig: appMethod<NovelistAppApi['SaveLLMConfig']>('SaveLLMConfig'),
  SaveSettings: appMethod<NovelistAppApi['SaveSettings']>('SaveSettings'),
  SaveUserName: appMethod<NovelistAppApi['SaveUserName']>('SaveUserName'),
  SearchAll: appMethod<NovelistAppApi['SearchAll']>('SearchAll'),
  SearchReferenceMaterials: appMethod<NovelistAppApi['SearchReferenceMaterials']>('SearchReferenceMaterials'),
  SearchStoryMemory: appMethod<NovelistAppApi['SearchStoryMemory']>('SearchStoryMemory'),
  SetActiveNovel: appMethod<NovelistAppApi['SetActiveNovel']>('SetActiveNovel'),
  SetApprovalMode: appMethod<NovelistAppApi['SetApprovalMode']>('SetApprovalMode'),
  SetChatPanelWidth: appMethod<NovelistAppApi['SetChatPanelWidth']>('SetChatPanelWidth'),
  SetLastSession: appMethod<NovelistAppApi['SetLastSession']>('SetLastSession'),
  SetReasoningEffort: appMethod<NovelistAppApi['SetReasoningEffort']>('SetReasoningEffort'),
  SetSelectedModel: appMethod<NovelistAppApi['SetSelectedModel']>('SetSelectedModel'),
  StartReferenceOrchestrationRun: appMethod<NovelistAppApi['StartReferenceOrchestrationRun']>('StartReferenceOrchestrationRun'),
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
  UpdateReferenceMaterialTags: appMethod<NovelistAppApi['UpdateReferenceMaterialTags']>('UpdateReferenceMaterialTags'),
  UpdateStoryArc: appMethod<NovelistAppApi['UpdateStoryArc']>('UpdateStoryArc'),
  UpdateTimelineEntry: appMethod<NovelistAppApi['UpdateTimelineEntry']>('UpdateTimelineEntry'),
}

function appMethod<TMethod extends BridgeBackedMethod>(method: string): TMethod {
  return createAppMethod(method) as TMethod
}
