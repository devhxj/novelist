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
  export interface AppSettings {
    ID: number
    last_novel_id: number
    selected_model_key: string
    reasoning_effort: string
    approval_mode: string
    chat_panel_width: number
    last_session_id: string
    user_name: string
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
  }

  export interface CreateAnchorInput {
    novel_id: number
    title: string
    author?: string
    source_path: string
    source_kind: string
    license_status: string
  }

  export interface BuildStatus {
    novel_id: number
    anchor_id: number
    status: string
    stage: string
    source_segment_count: number
    material_count: number
    slot_count: number
    vector_count: number
    last_error: string
    updated_at: Timestamp
  }

  export interface Material {
    material_id: string
    anchor_id: number
    source_segment_id: string
    material_type: string
    function_tag: string
    emotion_tag: string
    scene_tag: string
    pov_tag: string
    technique_tag: string
    function_confidence: number
    emotion_confidence: number
    pov_confidence: number
    text: string
    source_hash: string
    extractor_version: string
    user_verified: boolean
    created_at: Timestamp
  }

  export interface MaterialQuery {
    query: string
    material_types: string[]
    emotion_tags: string[]
    function_tags: string[]
    pov_tags: string[]
    technique_tags: string[]
    max_results: number
  }

  export interface SearchMaterialsInput {
    novel_id: number
    anchor_ids: number[]
    query: string
    material_types: string[]
    emotion_tags: string[]
    function_tags: string[]
    pov_tags: string[]
    technique_tags: string[]
    page: number
    size: number
  }

  export interface SlotValue {
    slot_name: string
    value: string
  }

  export interface AdaptMaterialInput {
    novel_id: number
    material_id: string
    slot_values: SlotValue[]
    max_rewrite_level: string
    scene_facts: string[]
  }

  export interface AuditReuseInput {
    novel_id: number
    material_id: string
    candidate_text: string
    max_rewrite_level: string
    scene_facts: string[]
  }

  export interface ReuseAudit {
    audit_id: string
    status: string
    rewrite_level: string
    provenance_errors: string[]
    unsupported_fact_errors: string[]
    ai_prose_risks: string[]
    non_slot_edits: string[]
    required_fixes: string[]
    audited_at: Timestamp
  }

  export interface AdaptMaterialResult {
    candidate_id: string
    material_id: string
    rewrite_level: string
    text: string
    changed_slots: SlotValue[]
    non_slot_edits: string[]
    audit: ReuseAudit
  }

  export interface RecordUserFeedbackInput {
    novel_id: number
    target_type: string
    target_id: string
    decision: string
    material_id: string
    candidate_id: string
    blueprint_id: number
    beat_id: string
    feedback_tags: string[]
    note: string
    edited_text: string
    origin: string
  }

  export interface GetUserFeedbackInput {
    novel_id: number
    target_type: string
    target_id: string
    limit: number
  }

  export interface UserFeedback {
    feedback_id: string
    novel_id: number
    target_type: string
    target_id: string
    decision: string
    material_id: string
    candidate_id: string
    blueprint_id: number
    beat_id: string
    feedback_tags: string[]
    note: string
    edited_text_hash: string
    origin: string
    created_at: Timestamp
  }

  export interface GenerateChapterBlueprintInput {
    novel_id: number
    chapter_number: number
    title?: string
    chapter_goal?: string
    anchor_ids: number[]
    known_facts: string[]
    forbidden_facts: string[]
  }

  export interface ChapterBlueprintSummary {
    blueprint_id: number
    novel_id: number
    chapter_number: number
    title: string
    status: string
    source_plan_hash: string
    updated_at: Timestamp
  }

  export interface ChapterBlueprint {
    blueprint_id: number
    novel_id: number
    chapter_number: number
    title: string
    status: string
    source_plan_scope: string
    source_plan_hash: string
    context_hash: string
    analysis_contract_hash: string
    blueprint_version: number
    parent_blueprint_id: number
    primary_anchor_id: number
    chapter_function: string
    logic_analysis: ChapterBlueprintAnalysisTrack
    emotion_analysis: ChapterBlueprintAnalysisTrack
    narration_analysis: ChapterBlueprintAnalysisTrack
    character_analysis: ChapterBlueprintAnalysisTrack
    reference_analysis: ChapterBlueprintAnalysisTrack
    transition_plan: ChapterBlueprintAnalysisTrack
    execution_contract: ChapterBlueprintExecutionTrack
    previous_state: string
    final_state: string
    final_hook: string
    global_pov: string
    global_narrative_distance: string
    known_facts: string[]
    forbidden_facts: string[]
    risk_flags: string[]
    beats: ChapterBlueprintBeat[]
    latest_review?: ChapterBlueprintReview | null
    created_at: Timestamp
    updated_at: Timestamp
  }

  export interface ChapterBlueprintAnalysisTrack {
    track: string
    summary: string
    points: string[]
  }

  export interface ChapterBlueprintExecutionTrack {
    track: string
    summary: string
    paragraph_intentions: string[]
    execution_modes: string[]
    anti_screenplay_duties: string[]
    source_backed_detail_targets: string[]
    candidate_rejection_rules: string[]
  }

  export interface ChapterBlueprintBeat {
    beat_id: string
    beat_index: number
    scene_index: number
    beat_type: string
    narrative_function: string
    logic_premise: string
    conflict_pressure: string
    causality_in: string
    causality_out: string
    transition_in: string
    transition_out: string
    pov_character: string
    narrative_distance: string
    viewpoint_allowed_knowledge: string[]
    viewpoint_forbidden_knowledge: string[]
    character_states_before: string[]
    character_states_after: string[]
    character_goals: string[]
    character_misbeliefs: string[]
    relationship_pressure: string[]
    emotion_trigger: string
    emotion_before: string
    emotion_after: string
    suppressed_reaction: string
    external_evidence: string
    narration_strategy: string
    rhythm_strategy: string
    paragraph_intention: string
    execution_mode: string
    anti_screenplay_duty: string
    sensory_anchor_target: string
    subtext_plan: string
    source_backed_detail_target: string
    candidate_rejection_rule: string
    scene_facts: string[]
    forbidden_facts: string[]
    reference_query: MaterialQuery
    required_material_types: string[]
    max_rewrite_level: string
    slot_plan: SlotValue[]
    locked_phrase_policy: string
    no_reuse_reason: string
    prose_duties: string[]
    risk_flags: string[]
  }

  export interface ReviewChapterBlueprintInput {
    novel_id: number
    blueprint_id: number
  }

  export interface BlueprintRevisionChange {
    field_path: string
    new_value: string
  }

  export interface ReviseChapterBlueprintInput {
    novel_id: number
    blueprint_id: number
    changes: BlueprintRevisionChange[]
    origin: string
    revision_reason: string
  }

  export interface ChapterBlueprintReview {
    review_id: string
    blueprint_id: number
    context_hash: string
    source_plan_hash: string
    analysis_contract_hash: string
    status: string
    score: number
    logic_errors: string[]
    causality_errors: string[]
    emotion_errors: string[]
    narration_errors: string[]
    execution_errors: string[]
    character_state_errors: string[]
    pov_errors: string[]
    continuity_errors: string[]
    transition_errors: string[]
    forbidden_fact_errors: string[]
    reference_binding_errors: string[]
    material_fit_errors: string[]
    screenplay_drift_risks: string[]
    ai_prose_risks: string[]
    novelistic_narration_errors: string[]
    required_fixes: string[]
    reviewed_at: Timestamp
  }

  export interface ApproveChapterBlueprintInput {
    novel_id: number
    blueprint_id: number
    review_id: string
  }

  export interface BindBlueprintMaterialsInput {
    novel_id: number
    blueprint_id: number
    max_results_per_beat: number
  }

  export interface BlueprintMaterialLink {
    link_id: string
    blueprint_id: number
    beat_id: string
    material_id: string
    intended_use: string
    max_rewrite_level: string
    selected: boolean
    score: number
    score_components: Record<string, number>
    created_at: Timestamp
  }

  export interface BlueprintMaterialBindingResult {
    blueprint_id: number
    links: BlueprintMaterialLink[]
  }

  export interface GenerateAnchoredDraftInput {
    novel_id: number
    blueprint_id: number
    beat_ids: string[]
  }

  export interface DraftParagraphCandidate {
    candidate_id: string
    blueprint_id: number
    beat_id: string
    material_id: string
    rewrite_level: string
    text: string
    changed_slots: SlotValue[]
    non_slot_edits: string[]
    audit_status: string
    created_at: Timestamp
  }

  export interface AnchoredDraft {
    blueprint_id: number
    candidates: DraftParagraphCandidate[]
    audit?: AnchoredDraftAudit | null
  }

  export interface AuditAnchoredDraftInput {
    novel_id: number
    blueprint_id: number
    candidate_ids: string[]
  }

  export interface AnchoredDraftAudit {
    audit_id: string
    blueprint_id: number
    status: string
    rewrite_level: string
    provenance_errors: string[]
    blueprint_errors: string[]
    unsupported_fact_errors: string[]
    pov_errors: string[]
    ai_prose_risks: string[]
    required_fixes: string[]
    audited_at: Timestamp
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
  export interface PageResult_novel_app_SessionMeta_ {
    items: app.SessionMeta[]
    total: number
    page: number
    size: number
    total_pages: number
  }

  export interface PageResult_reference_Material_ {
    items: reference.Material[]
    total: number
    page: number
    size: number
    total_pages: number
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
