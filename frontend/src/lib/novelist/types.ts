/* eslint-disable @typescript-eslint/no-namespace -- Keep the former generated model namespace shape while owning the types locally. */

type Timestamp = unknown

export namespace app {
  export interface ChatInput {
    session_id: string
    novel_id: number
    message: string
    provider_name: string
    model_id: string
    reasoning_effort: string
  }

  export interface ChatResult {
    session_id: string
    turn_id: number
    final_text: string
  }

  export interface CompressInput {
    session_id: string
    provider_name: string
    model_id: string
  }

  export interface CompressResult {
    turn_id: number
  }

  export interface CreateArcNodeInput {
    story_arc_id: number
    title: string
    description?: string
    target_chapter: number
  }

  export interface CreateChapterInput {
    novel_id: number
    title: string
  }

  export interface CreateCharacterInput {
    name: string
    description?: string
    personality?: string
    abilities?: string
  }

  export interface CreateLocationInput {
    name: string
    location_type?: string
    description?: string
    detail_json?: string
    parent_location_id?: number
    tags?: string
  }

  export interface CreateNovelInput {
    title: string
    description?: string
    genre?: string
  }

  export interface CreatePreferenceInput {
    is_global: boolean
    category: string
    content: string
  }

  export interface CreateReaderPerspectiveInput {
    type: string
    content: string
    planted_chapter: number
    related_truth?: string
    revealed_chapter?: number
  }

  export interface CreateStoryArcInput {
    name: string
    arc_type: string
    description?: string
    importance?: number
  }

  export interface CreateTimelineEntryInput {
    category: string
    title: string
    content?: string
    detail_json?: string
    target_chapter: number
    importance?: number
    source_chapter_id?: number
    source?: string
  }

  export interface DeleteSkillInput {
    novel_id: number
    name: string
    source: string
  }

  export interface ExtractStyleInput {
    novel_id: number
    sample: string
    provider_name: string
    model_id: string
    reasoning_effort: string
  }

  export interface ExtractStyleResult {
    name: string
    description: string
    raw_content: string
    file_path: string
  }

  export interface GetSessionsInput {
    novel_id: number
    page: number
    size: number
    search: string
  }

  export interface ListSkillsInput {
    novel_id: number
  }

  export interface ListSlashCommandsInput {
    novel_id: number
  }

  export interface PreferenceResult {
    global: novel.PreferenceItem[]
    novel: novel.PreferenceItem[]
  }

  export interface SaveContentInput {
    novel_id: number
    path: string
    content: string
  }

  export interface SaveSettingsInput {
    [key: string]: unknown
  }

  export interface SessionDetail {
    session_id: string
    novel_id: number
    title: string
    model: string
    reasoning_effort: string
    active_version: number
    last_turn_id: number
    usage?: number[]
    created_at: string
    updated_at: string
  }

  export interface SessionMeta {
    session_id: string
    title: string
    updated_at: string
  }

  export interface SetActiveNovelInput {
    novel_id: number
  }

  export interface SlashCommand {
    name: string
    description: string
    type: string
  }

  export interface TestConnectionInput {
    provider_name: string
    base_url: string
    endpoint_type: string
    chat_url: string
    api_key: string
    model_id: string
  }

  export interface UpdateArcNodeInput {
    title?: string
    description?: string
    target_chapter?: number
    actual_chapter?: number
    status?: string
  }

  export interface UpdateChapterPlanInput {
    scope?: string
    content?: string
  }

  export interface UpdateCharacterInput {
    name?: string
    description?: string
    personality?: string
    abilities?: string
  }

  export interface UpdateLocationInput {
    name?: string
    location_type?: string
    description?: string
    detail_json?: string
    parent_location_id?: number
    tags?: string
    clear_parent?: boolean
  }

  export interface UpdateNovelInput {
    title?: string
    description?: string
    genre?: string
  }

  export interface UpdatePreferenceInput {
    category?: string
    content?: string
    is_global?: boolean
  }

  export interface UpdateReaderPerspectiveInput {
    type?: string
    content?: string
    planted_chapter?: number
    related_truth?: string
    revealed_chapter?: number
  }

  export interface UpdateStoryArcInput {
    name?: string
    description?: string
    arc_type?: string
    importance?: number
    status?: string
    reactivate_at?: string
  }

  export interface UpdateTimelineEntryInput {
    title?: string
    content?: string
    detail_json?: string
    target_chapter?: number
    importance?: number
    status?: string
    resolved_chapter_id?: number
  }
}

export namespace chapter {
  export interface Chapter {
    id: number
    novel_id: number
    chapter_number: number
    title: string
    summary: string
    word_count: number
    created_at: Timestamp
    updated_at: Timestamp
    file_path: string
  }
}

export namespace character {
  export interface Character {
    id: number
    novel_id: number
    name: string
    description: string
    personality: string
    abilities: string
    created_at: Timestamp
    updated_at: Timestamp
  }

  export interface CharacterRelation {
    id: number
    novel_id: number
    source_character_id: number
    target_character_id: number
    relation_describe: string
    description: string
    chapter_id: number
    is_current: boolean
    created_at: Timestamp
  }
}

export namespace config {
  export interface UpdateCheckConfiguration {
    endpoint_url: string
    default_enabled: boolean
    timeout_ms: number
  }

  export interface AppConfig {
    initialized: boolean
    data_dir?: string | null
    update_check: UpdateCheckConfiguration
    import_recovery?: novelImport.ImportReconciliationResult | null
  }

  export interface AppSettings {
    ID: number
    last_novel_id: number
    selected_model_key: string
    reasoning_effort: string
    approval_mode: string
    chat_panel_width: number
    last_session_id: string
    user_name: string
    git_author_name: string
    git_author_email: string
    update_check_enabled: boolean
    update_check_endpoint_url: string
    update_check_dismissed_version: string
    update_check_last_checked_at?: Timestamp | null
    sidebar_width: number
    metadata_panel_width: number
    window_x?: number | null
    window_y?: number | null
    window_width: number
    window_height: number
    window_maximized: boolean
  }
}

export namespace llm {
  export interface AvailableModel {
    Key: string
    ProviderName: string
    ModelName: string
    ContextWindow: number
    MaxOutputTokens: number
    SupportsThinking: boolean
    ReasoningLevels: string[]
    SupportsVision: boolean
  }

  export interface ModelInfo {
    id: string
    name: string
    context_window: number
    max_output_tokens: number
    supports_thinking: boolean
    reasoning_levels?: string[]
    supports_vision: boolean
  }

  export interface ProviderView {
    key: string
    name: string
    base_url: string
    endpoint_type: string
    chat_url: string
    api_key: string
    platform_url: string
    help_text: string
    temperature: number
    source: string
    builtin_models: ModelInfo[]
    custom_models: ModelInfo[]
  }

  export interface LLMConfigView {
    providers: ProviderView[]
  }
}

export namespace location {
  export interface Location {
    id: number
    novel_id: number
    name: string
    location_type: string
    description: string
    detail_json: string
    parent_location_id?: number
    tags: string
    created_at: Timestamp
    updated_at: Timestamp
  }

  export interface LocationRelation {
    id: number
    novel_id: number
    location_a_id: number
    location_b_id: number
    relation_type: string
    description: string
    created_at: Timestamp
    updated_at: Timestamp
  }
}

export namespace novel {
  export interface Novel {
    id: number
    title: string
    genre: string
    description: string
    created_at: Timestamp
    updated_at: Timestamp
  }

  export interface NovelCover {
    novel_id: number
    content_type: string
    data_base64: string
    length: number
    last_modified: Timestamp
  }

  export interface PreferenceItem {
    id: number
    novel_id: number
    is_global: boolean
    category: string
    content: string
    created_at: Timestamp
  }
}

export namespace diagnostics {
  export interface CopyableDiagnostic {
    code: string
    message: string
    detail: string
    operation: string
    task_id?: string | null
    run_id?: string | null
    bridge_method?: string | null
    timestamp: Timestamp
  }
}

export namespace novelImport {
  export interface StartNovelImportInput {
    task_id: string
    source_path: string
    source_display_name: string
    import_kind: 'epub' | 'txt' | 'markdown'
    requested_title?: string | null
    commit_message?: string | null
  }

  export interface CancelNovelImportInput {
    task_id: string
    reason: string
  }

  export interface GetNovelImportRunInput {
    task_id: string
  }

  export interface ImportProgress {
    task_id: string
    state: string
    stage: string
    progress_completed: number
    progress_total: number
    message: string
    created_novel_id?: number | null
    current_chapter_index?: number | null
    current_chapter_title?: string | null
    updated_at: Timestamp
  }

  export interface ImportRun {
    task_id: string
    state: string
    stage: string
    source_display_name: string
    source_path_hash: string
    parser_type: string
    created_novel_id?: number | null
    created_file_roots: string[]
    skipped_chapters: SkippedChapter[]
    diagnostics: ImportDiagnostic[]
    warnings: ImportWarning[]
    error?: diagnostics.CopyableDiagnostic | null
    started_at: Timestamp
    updated_at: Timestamp
    completed_at?: Timestamp | null
  }

  export interface SkippedChapter {
    index: number
    title: string
    reason: string
  }

  export interface ImportDiagnostic {
    code: string
    message: string
    detail: string
    severity: string
  }

  export interface ImportWarning {
    code: string
    message: string
    detail: string
  }

  export interface ImportRecoveryStatus {
    pending_runs: ImportRun[]
    blocked_runs: ImportRun[]
    checked_at: Timestamp
  }

  export interface ImportReconciliationResult {
    reconciled_runs: ImportRun[]
    blocked_runs: ImportRun[]
    diagnostics: ImportDiagnostic[]
    reconciled_at: Timestamp
  }
}

export namespace styleSample {
  export interface StyleSampleSourceMetadata {
    source_type: string
    source_id: string
    source_hash: string
  }

  export interface StyleSampleStats {
    schema_version: string
    character_count: number
    word_count: number
    sentence_count: number
    sentence_length_distribution: number[]
    average_sentence_chars: number
    sentence_length_std_dev: number
    punctuation_per_100_chars: number
    quote_density: number
    paragraph_count: number
    average_paragraph_chars: number
    dialogue_ratio: number
    interiority_ratio: number
    sensory_ratio: number
  }

  export interface CreateStyleSampleInput {
    novel_id?: number | null
    is_global: boolean
    name: string
    content: string
    tags: string[]
    source_metadata?: StyleSampleSourceMetadata | null
  }

  export interface UpdateStyleSampleInput extends CreateStyleSampleInput {
    sample_id: number
  }

  export interface DeleteStyleSampleInput {
    sample_id: number
  }

  export interface GetStyleSampleInput {
    sample_id: number
  }

  export interface SearchStyleSamplesInput {
    novel_id?: number | null
    include_global: boolean
    query: string
    tags: string[]
    page: number
    size: number
  }

  export interface StyleSample {
    sample_id: number
    novel_id?: number | null
    is_global: boolean
    name: string
    preview: string
    tags: string[]
    stats_schema_version: string
    stats: StyleSampleStats
    source_metadata?: StyleSampleSourceMetadata | null
    created_at: Timestamp
    updated_at: Timestamp
  }

  export interface StyleSampleDetail extends StyleSample {
    content: string
  }

  export interface StartStyleSkillExtractionInput {
    task_id: string
    novel_id?: number | null
    sample_ids: number[]
    provider_name: string
    model_id: string
    reasoning_effort: string
    skill_name: string
  }

  export interface CancelStyleSkillExtractionInput {
    task_id: string
    reason: string
  }

  export interface StyleSkillExtractionRun {
    task_id: string
    status: string
    stage: string
    progress_completed: number
    progress_total: number
    sample_ids: number[]
    skill_name: string
    skill_preview: string
    skill_file_path: string
    diagnostics: diagnostics.CopyableDiagnostic[]
    created_at: Timestamp
    updated_at: Timestamp
    completed_at?: Timestamp | null
  }
}

export namespace pattern {
  export interface ChapterRange {
    start_chapter: number
    end_chapter: number
  }

  export interface StartNarrativePatternExtractionInput {
    task_id: string
    novel_id: number
    chapter_ranges: ChapterRange[]
    provider_name: string
    model_id: string
    reasoning_effort: string
    skill_name: string
    selected_chapter_ids?: number[] | null
  }

  export interface CancelNarrativePatternExtractionInput {
    task_id: string
    reason: string
  }

  export interface GetNarrativePatternRunInput {
    task_id: string
  }

  export interface NarrativePatternRun {
    task_id: string
    novel_id: number
    status: string
    stage: string
    progress_completed: number
    progress_total: number
    chapter_ranges: ChapterRange[]
    selected_chapter_ids: number[]
    skill_name: string
    skill_preview: string
    diagnostics: diagnostics.CopyableDiagnostic[]
    created_at: Timestamp
    updated_at: Timestamp
    completed_at?: Timestamp | null
  }

  export interface NarrativePatternProgress {
    task_id: string
    status: string
    stage: string
    progress_completed: number
    progress_total: number
    message: string
    updated_at: Timestamp
    llm_status: string
    round?: number | null
    batch_index?: number | null
    batch_total?: number | null
    token_estimate?: number | null
    boundary_count?: number | null
    summary_count?: number | null
    phase_count?: number | null
  }

  export interface NarrativePatternTrace {
    task_id: string
    entries: NarrativePatternTraceEntry[]
  }

  export interface NarrativePatternTraceEntry {
    trace_id: string
    stage: string
    input_hash: string
    output_hash: string
    diagnostics: diagnostics.CopyableDiagnostic[]
    created_at: Timestamp
  }
}

export namespace git {
  export interface GetGitCommitsInput {
    novel_id: number
    page: number
    size: number
    cursor_commit_id?: string | null
  }

  export interface GetGitCommitFilesInput {
    novel_id: number
    commit_id: string
  }

  export interface GetGitFileDiffInput extends GetGitCommitFilesInput {
    path: string
  }

  export interface GitCommitSummary {
    commit_id: string
    short_commit_id: string
    author_name: string
    author_email: string
    message: string
    committed_at: Timestamp
    changed_file_count: number
    insertions: number
    deletions: number
  }

  export interface GitCommitFile {
    path: string
    old_path?: string | null
    change_type: string
    additions: number
    deletions: number
    binary: boolean
  }

  export interface GitFileDiff {
    commit_id: string
    path: string
    old_path?: string | null
    change_type: string
    diff_text: string
    truncated: boolean
    binary: boolean
    original_content?: string | null
    modified_content?: string | null
  }

  export interface GitAuthorSettings {
    name: string
    email: string
    scope: string
  }

  export interface SaveGitAuthorSettingsInput {
    name: string
    email: string
  }
}

export namespace update {
  export interface CheckForUpdatesInput {
    task_id: string
    manual: boolean
  }

  export interface UpdateCheckResult {
    task_id: string
    status: string
    current_version: string
    latest_version?: string | null
    release_url?: string | null
    checked_at: Timestamp
    error_code?: string | null
    error_message?: string | null
    release_name?: string | null
    release_notes?: string | null
    download_url?: string | null
    dismissed: boolean
  }

  export interface UpdateCheckSettings {
    enabled: boolean
    endpoint_url: string
    dismissed_version: string
    last_checked_at?: Timestamp | null
  }

  export interface SaveUpdateCheckSettingsInput {
    enabled: boolean
    endpoint_url: string
    dismissed_version: string
  }
}

export namespace layout {
  export interface LayoutSettings {
    sidebar_width: number
    chat_panel_width: number
    metadata_panel_width: number
  }

  export type SaveLayoutSettingsInput = LayoutSettings

  export interface WindowSettings {
    x?: number | null
    y?: number | null
    width: number
    height: number
    maximized: boolean
  }

  export type SaveWindowSettingsInput = WindowSettings
}

export namespace reader {
  export interface ReaderPerspective {
    id: number
    novel_id: number
    type: string
    content: string
    related_truth: string
    planted_chapter: number
    revealed_chapter: number
    created_at: Timestamp
  }
}

export namespace reference {
  export interface Anchor {
    anchor_id: number
    novel_id: number
    title: string
    author: string
    source_path: string
    source_kind: string
    license_status: string
    source_file_hash: string
    build_version: string
    status: string
    created_at: Timestamp
    updated_at: Timestamp
    visibility: string
    source_trust: string
    user_tags: string[]
    owner_scope: string
    owner_novel_id?: number | null
  }

  export interface CreateAnchorInput {
    novel_id: number
    title: string
    author?: string
    source_path: string
    source_kind: string
    license_status: string
    visibility?: string | null
    source_trust?: string | null
    user_tags?: string[] | null
  }

  export interface DeleteAnchorsInput {
    novel_id: number
    anchor_ids: number[]
  }

  export interface UpdateAnchorMetadataInput {
    novel_id: number
    anchor_id: number
    title: string
    author?: string | null
    license_status: string
    visibility: string
    source_trust: string
    user_tags: string[]
  }

  export interface AnalyzeChapterSplitInput {
    novel_id: number
    anchor_id: number
  }

  export interface PreviewChapterSplitInput {
    novel_id: number
    anchor_id: number
    delimiter_template: string
  }

  export interface ConfirmChapterSplitInput {
    novel_id: number
    anchor_id: number
    split_profile_id: string
  }

  export interface ChapterSplitBoundary {
    chapter_index: number
    title: string
    heading_start: number
    content_start: number
    content_end: number
    text_hash: string
  }

  export interface ChapterSplitProfile {
    split_profile_id: string
    anchor_id: number
    source_hash: string
    split_mode: 'auto' | 'manual'
    pattern_kind: string
    delimiter_template: string
    sample_char_count: number
    status: 'draft' | 'validated' | 'confirmed' | 'stale'
    chapter_count: number
    boundaries: ChapterSplitBoundary[]
    model_provider?: string | null
    model_id?: string | null
    confidence?: number | null
  }

  export interface EnqueueMaterializationInput {
    novel_id: number
    anchor_id: number
    split_profile_id: string
    run_id?: string | null
  }

  export interface RunMaterializationChapterInput {
    novel_id: number
    anchor_id: number
    run_id: string
    chapter_index: number
  }

  export interface GetMaterializationStatusInput {
    novel_id: number
    anchor_id: number
    run_id?: string | null
  }

  export interface ListMaterializationChapterProgressInput extends GetMaterializationStatusInput {
    page: number
    size: number
  }

  export interface ListMaterializationChapterMaterialsInput extends GetMaterializationStatusInput {
    run_id: string
    chapter_index: number
    page: number
    size: number
  }

  export interface ListReferenceMaterialsInput {
    novel_id: number
    anchor_id: number
    page?: number
    size?: number
  }

  export interface MaterializationModelIdentity {
    provider: string
    model_id: string
    dimensions?: number | null
  }

  export interface MaterializationChapterProgress {
    chapter_index: number
    status: string
    material_count: number
    vector_count: number
    model_call_count: number
    started_at?: Timestamp | null
    completed_at?: Timestamp | null
    last_error_code?: string | null
    last_error_message?: string | null
  }

  export interface MaterializationStatus {
    run_id: string
    anchor_id: number
    split_profile_id: string
    generation_id: string
    status: string
    total_chapters: number
    processed_chapters: number
    current_chapter_index?: number | null
    material_count: number
    vector_count: number
    model_call_count: number
    llm: MaterializationModelIdentity
    embedding: MaterializationModelIdentity
    last_error_code?: string | null
    last_error_message?: string | null
    started_at: Timestamp
    completed_at?: Timestamp | null
    vector_index_healthy: boolean
  }

  export interface ReferenceMaterialListItem {
    material_id: string
    generation_id: string
    anchor_id: number
    chapter_index: number
    ordinal: number
    text: string
    metadata: ReferenceMaterialMetadata
    text_hash: string
  }

  export interface ReferenceMaterialEntity {
    name: string
    kind: string
  }

  export interface ReferenceMaterialSetting {
    location?: string | null
    time?: string | null
    environment?: string | null
  }

  export interface ReferenceMaterialSourceSpan {
    start_line: number
    end_line: number
  }

  export interface ReferenceMaterialPerspective {
    mode: string
    focus_entity?: string | null
  }

  export interface ReferenceMaterialFact {
    content: string
    subject?: string | null
  }

  export interface ReferenceMaterialCausality {
    cause?: string | null
    consequence?: string | null
  }

  export interface ReferenceMaterialStateChange {
    subject: string
    before: string
    after: string
  }

  export interface ReferenceMaterialConflict {
    pressure?: string | null
    cost?: string | null
  }

  export interface ReferenceMaterialInformation {
    role?: string | null
    content?: string | null
  }

  export interface ReferenceMaterialEmotion {
    tone?: string | null
    subtext?: string | null
  }

  export interface ReferenceMaterialForeshadowing {
    phase: string
    target: string
  }

  export interface ReferenceMaterialMetadata {
    source_span: ReferenceMaterialSourceSpan
    source_kind: string
    entities: ReferenceMaterialEntity[]
    setting?: ReferenceMaterialSetting | null
    perspective?: ReferenceMaterialPerspective | null
    event?: string | null
    facts: ReferenceMaterialFact[]
    causality?: ReferenceMaterialCausality | null
    state_changes: ReferenceMaterialStateChange[]
    character_dynamics?: string | null
    conflict?: ReferenceMaterialConflict | null
    information?: ReferenceMaterialInformation | null
    emotion?: ReferenceMaterialEmotion | null
    narrative_functions: string[]
    foreshadowing: ReferenceMaterialForeshadowing[]
    motifs: string[]
    expression_techniques: string[]
    reuse_hint: string
  }

  export interface SearchReferenceMaterialsInput {
    query: string
    max_results?: number
    novel_id?: number | null
    session_id?: string | null
    library_ids?: string[] | null
    anchor_ids?: number[] | null
  }

  export interface ReferenceMaterialSearchHit extends ReferenceMaterialListItem {
    vector_distance: number
  }

  export interface GenerateMaterializationBlueprintPreviewInput {
    novel_id: number
    anchor_ids: number[]
    goal: string
    requested_count?: 1 | 2 | 3
  }

  export interface MaterializationBlueprintPreviewSource {
    anchor_id: number
    generation_id: string
    material_count: number
  }

  export interface MaterializationBlueprintPreviewMaterialLink {
    material_id: string
    anchor_id: number
    generation_id: string
    text: string
    metadata: ReferenceMaterialMetadata
    vector_distance: number
    fit_explanation: string
  }

  export interface MaterializationBlueprintPreviewBeat {
    beat_id: string
    beat_index: number
    intent: string
    narrative_function: string
    materials: MaterializationBlueprintPreviewMaterialLink[]
  }

  export interface MaterializationBlueprintPreviewCandidate {
    blueprint_id: string
    strategy: string
    beats: MaterializationBlueprintPreviewBeat[]
  }

  export interface MaterializationBlueprintPreview {
    goal: string
    sources: MaterializationBlueprintPreviewSource[]
    candidates: MaterializationBlueprintPreviewCandidate[]
  }

  export interface GenerateWritingBlueprintsInput {
    novel_id: number
    chapter_number: number
    session_id: string
    goal: string
    requested_count?: number
  }

  export interface GetWritingSessionInput {
    novel_id: number
    chapter_number: number
    session_id: string
  }

  export interface SelectWritingBlueprintInput extends GetWritingSessionInput {
    blueprint_id: string
  }

  export interface GenerateWritingDraftCandidatesInput extends SelectWritingBlueprintInput {
    current_draft_text: string
    insertion_offset: number
    slot_values: Record<string, string>
    requested_count?: number
  }

  export interface WritingMaterialIdentity {
    material_id: string
    generation_id: string
  }

  export interface WritingBlueprintBeat {
    beat_id: string
    beat_index: number
    intent: string
    narrative_function: string
    materials: WritingMaterialIdentity[]
  }

  export interface WritingBlueprint {
    blueprint_id: string
    strategy: string
    beats: WritingBlueprintBeat[]
  }

  export interface WritingSession {
    session_id: string
    novel_id: number
    chapter_number: number
    goal: string
    blueprints: WritingBlueprint[]
    selected_blueprint_id: string
    updated_at: Timestamp
  }

  export interface WritingDraftSource {
    beat_id: string
    material_id: string
    generation_id: string
    anchor_id: number
    chapter_index: number
    text_hash: string
    license_state: string
    reuse_policy: string
  }

  export interface WritingDraftAudit {
    passed: boolean
    errors: string[]
  }

  export interface WritingDraftCandidate {
    candidate_id: string
    blueprint_id: string
    text: string
    chapter_text_after_insertion: string
    sources: WritingDraftSource[]
    audit: WritingDraftAudit
  }

  export interface WritingDraftCandidates {
    session_id: string
    blueprint_id: string
    candidates: WritingDraftCandidate[]
  }

}

export namespace search {
  export interface Result {
    type: string
    id: number
    title: string
    subtitle: string
    chapter_num: number
    file_path: string
    match_prefix: string
    match_hit: string
    match_suffix: string
    match_position: number
    match_len: number
    relevance: number
    panel_id: string
  }
}

export namespace session {
  export interface Message {
    id: number
    session_id: string
    turn_id: number
    role: string
    content: string
    thinking_content?: string
    token_count: number
    extra_metadata?: string
    version: number
    to_api: boolean
    to_frontend: boolean
    event_type?: string
    agent_type: string
    sub_task_id?: string
    created_at: Timestamp
  }
}

export namespace skill {
  export interface SkillMeta {
    name: string
    description: string
    category: string
    mode: string
    author: string
    version: number
    source: string
  }
}

export namespace storage {
  export interface PageRequest {
    cursor?: string | null
    page_size: number
    sort_by: string
    sort_dir: 'asc' | 'desc' | string
    filters?: Record<string, string> | null
  }

  export interface PageResult_reference_MaterializationChapterProgress_ {
    items: reference.MaterializationChapterProgress[]
    total: number
    page: number
    size: number
    total_pages: number
  }

  export interface PageResult_reference_ReferenceMaterialListItem_ {
    items: reference.ReferenceMaterialListItem[]
    total: number
    page: number
    size: number
    total_pages: number
  }

  export interface PageResult_git_GitCommitSummary_ {
    items: git.GitCommitSummary[]
    total: number
    page: number
    size: number
    total_pages: number
    next_cursor?: string | null
    has_more?: boolean
    total_estimate?: number | null
  }

  export interface PageResult_novel_app_SessionMeta_ {
    items: app.SessionMeta[]
    total: number
    page: number
    size: number
    total_pages: number
    next_cursor?: string | null
    has_more?: boolean
    total_estimate?: number | null
  }

  export interface PageResult_styleSample_StyleSample_ {
    items: styleSample.StyleSample[]
    total: number
    page: number
    size: number
    total_pages: number
    next_cursor?: string | null
    has_more?: boolean
    total_estimate?: number | null
  }
}

export namespace storyarc {
  export interface ArcNode {
    id: number
    novel_id: number
    story_arc_id: number
    title: string
    description: string
    target_chapter: number
    actual_chapter: number
    status: string
    created_at: Timestamp
    updated_at: Timestamp
  }

  export interface StoryArc {
    id: number
    novel_id: number
    name: string
    description: string
    arc_type: string
    importance: number
    status: string
    reactivate_at: string
    created_at: Timestamp
    updated_at: Timestamp
  }
}

export namespace timeline {
  export interface ChapterPlan {
    novel_id: number
    scope: string
    content: string
  }

  export interface TimelineEntry {
    id: number
    novel_id: number
    category: string
    status: string
    title: string
    content: string
    detail_json: string
    target_chapter: number
    importance: number
    source_chapter_id: number
    source: string
    resolved_chapter_id: number
    created_at: Timestamp
    updated_at: Timestamp
  }
}

export namespace writing {
  export interface DailyActivity {
    date: string
    words: number
  }

  export interface WritingStats {
    total_words: number
    total_days_active: number
    current_streak: number
    longest_streak: number
    total_novels: number
    total_chapters: number
  }
}
