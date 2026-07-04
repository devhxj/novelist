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
