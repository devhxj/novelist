import type { Character } from './character'
import type { Chapter } from './chapter'

export type NovelStatus = 'draft' | 'writing' | 'completed' | 'published'

export interface Novel {
  id: number
  title: string
  genre: string
  description: string
  author_id: number
  status: NovelStatus
  chapter_count: number
  word_count: number
  created_at: string
  updated_at: string
}

export interface NovelDetail extends Novel {
  character_count: number
  characters?: Character[]
  chapters?: Chapter[]
}

export interface NovelCreate {
  title: string
  genre: string
  description: string
}

export interface NovelUpdate {
  title?: string
  genre?: string
  description?: string
  status?: NovelStatus
}

export interface NovelListParams {
  page?: number
  page_size?: number
  status?: NovelStatus
  genre?: string
  search?: string
}
