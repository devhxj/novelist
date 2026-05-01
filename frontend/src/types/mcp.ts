export type MCPToolCategory = 'novel_management' | 'memory_retrieval' | 'consistency_check' | 'writing_assistant'

export interface MCPToolInfo {
  name: string
  description: string
  category: MCPToolCategory
  parameters: {
    name: string
    type: string
    required: boolean
    description: string
    default?: any
  }[]
  returns: string
}

export interface MCPToolResult<T = any> {
  success: boolean
  data: T
  error: string | null
  metadata: Record<string, any> | null
}

export interface MCPToolExecuteParams {
  [key: string]: any
}

export interface NovelSummary {
  id: number
  title: string
  genre: string
  status: string
  chapter_count: number
  word_count: number
  character_count: number
  description: string
}

export interface ChapterListResult {
  chapters: {
    id: number
    chapter_number: number
    title: string
    status: string
    word_count: number
    summary: string | null
  }[]
  total: number
  page: number
  page_size: number
}

export interface ChapterContentResult {
  id: number
  chapter_number: number
  title: string
  content: string
  word_count: number
  summary: string | null
  status: string
}

export interface NovelProgressResult {
  total_chapters: number
  completed_chapters: number
  total_words: number
  status: string
  completion_percentage: number
  last_updated: string
}

export interface CharacterListResult {
  characters: {
    id: number
    name: string
    role: string
    personality: Record<string, any>
  }[]
  total: number
}

export interface CharacterDetailResult {
  id: number
  name: string
  personality: Record<string, any>
  relationships: Record<string, any>
  abilities: string[]
  background: string
  appearances: {
    chapter_id: number
    chapter_number: number
    events: string[]
  }[]
}

export interface MemorySearchResult {
  results: {
    id: number
    type: string
    content: string
    chapter_id: number | null
    relevance_score: number
    metadata: Record<string, any>
  }[]
  total: number
  query: string
}

export interface CharacterMemoryResult {
  character_id: number
  character_name: string
  personality: Record<string, any>
  related_content: {
    content: string
    chapter_id: number
    relevance: number
  }[]
}

export interface RecentContextResult {
  chapter_id: number
  chapter_number: number
  previous_summary: string
  character_info: {
    id: number
    name: string
    role: string
  }[]
  plot_threads: string[]
  context_text: string
}

export interface ConsistencyCheckResult {
  check_type: string
  passed: boolean
  issues: {
    type: string
    severity: string
    chapter_id: number | null
    description: string
    suggestion: string
  }[]
  checked_chapters: number[]
}

export interface ForeshadowingStatusResult {
  total: number
  resolved: number
  pending: number
  abandoned: number
  by_importance: Record<string, number>
  oldest_pending_days: number | null
}
