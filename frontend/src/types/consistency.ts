export type ForeshadowingType = 'plot' | 'character' | 'item' | 'mystery' | 'other'
export type ForeshadowingStatus = 'unresolved' | 'resolved' | 'abandoned'

export interface Foreshadowing {
  id: number
  novel_id: number
  created_chapter_id: number | null
  resolved_chapter_id: number | null
  title: string
  description: string | null
  foreshadowing_type: ForeshadowingType
  status: ForeshadowingStatus
  importance: number
  resolution_notes: string | null
  metadata: Record<string, any> | null
  created_at: string
  resolved_at: string | null
  updated_at: string | null
}

export interface ForeshadowingCreate {
  title: string
  description?: string
  created_chapter_id?: number
  foreshadowing_type?: ForeshadowingType
  importance?: number
  metadata?: Record<string, any>
}

export interface ForeshadowingUpdate {
  title?: string
  description?: string
  foreshadowing_type?: ForeshadowingType
  importance?: number
  metadata?: Record<string, any>
}

export interface ForeshadowingResolve {
  resolved_chapter_id: number
  resolution_notes?: string
}

export interface ConsistencyIssue {
  issue_type: 'character' | 'plot' | 'timeline' | 'foreshadowing'
  severity: 'error' | 'warning' | 'info'
  chapter_id?: number
  chapter_number?: number
  description: string
  details?: Record<string, any>
  suggestion?: string
}

export interface ConsistencyCheckRequest {
  chapter_ids?: number[]
  check_types?: ('character' | 'plot' | 'timeline' | 'foreshadowing')[]
}

export interface ConsistencyCheckResponse {
  check_id: string
  novel_id: number
  status: string
  issues: ConsistencyIssue[]
  summary: {
    total_issues: number
    by_severity: {
      error: number
      warning: number
      info: number
    }
    by_type: {
      character: number
      plot: number
      timeline: number
      foreshadowing: number
    }
  }
  check_time: number
}

export interface ForeshadowingStatistics {
  total: number
  unresolved: number
  resolved: number
  abandoned: number
  high_importance_unresolved: number
  resolution_rate: number
}
