export interface GenerationRequest {
  prompt: string
  context?: {
    previous_chapters?: number[]
    characters?: number[]
    style?: string
  }
  options?: {
    temperature?: number
    max_tokens?: number
  }
}

export interface GenerationResponse {
  chapter_id: number
  content: string
  word_count: number
  generation_time: number
  model_used: string
}

export interface ConsistencyCheckRequest {
  chapter_ids: number[]
  check_types: ('character' | 'plot' | 'timeline')[]
}

export interface ConsistencyIssue {
  type: string
  severity: 'warning' | 'error'
  chapter_id: number
  description: string
  details: {
    character_id?: number
    issue: string
  }
  suggestion: string
}

export interface ConsistencyCheckResponse {
  check_id: string
  status: string
  issues: ConsistencyIssue[]
  check_time: number
}

