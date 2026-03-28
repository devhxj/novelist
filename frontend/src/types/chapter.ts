import type { PlotEvent } from './plotEvent'

export type ChapterStatus = 'draft' | 'completed'

export interface Chapter {
  id: number
  novel_id: number
  chapter_number: number
  title: string
  word_count: number
  status: ChapterStatus
  summary: string
  created_at: string
  updated_at: string
}

export interface ChapterDetail extends Chapter {
  content: string
  novel?: {
    id: number
    title: string
  }
  plot_events?: PlotEvent[]
}

export interface ChapterCreate {
  chapter_number: number
  title: string
  content?: string
  summary?: string
}

export interface ChapterUpdate {
  title?: string
  content?: string
  summary?: string
  status?: ChapterStatus
}

export interface ChapterListParams {
  page?: number
  page_size?: number
  status?: ChapterStatus
  order?: 'asc' | 'desc'
}
