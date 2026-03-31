import apiClient from './apiClient'
import type { ApiResponse, PaginatedResponse } from '@/types/api'
import type { LLMModel } from './wsGenerationService'

export type SessionLevel = 'novel' | 'chapter' | 'free'

export interface SessionMessage {
  id: string
  role: 'system' | 'user' | 'assistant' | 'tool'
  content: string
  created_at: string
  importance?: number
}

export interface SessionStats {
  message_count: number
  token_count: number
  context_window: number
  usage_ratio: number
  should_compress: boolean
}

export interface NovelContext {
  title?: string
  description?: string
  genre?: string
  outline?: string
  world_setting?: string
  characters_summary?: string
  main_plot?: string
}

export interface ChapterContext {
  chapter_number: number
  chapter_title?: string
  previous_summary?: string
  current_outline?: string
  key_events?: string[]
  focus_characters?: string[]
}

export interface Session {
  id: string
  session_id: string
  level: SessionLevel
  display_name: string
  title?: string
  novel_id?: number
  chapter_number?: number
  chapter_number_end?: number
  model: LLMModel
  stats: SessionStats
  novel_context?: NovelContext
  chapter_context?: ChapterContext
  created_at: string
  updated_at: string
  expires_at: string
}

export interface UpdateTitleRequest {
  title: string
}

export interface AutoGenerateTitleResponse {
  title: string
  auto_generated: boolean
  message: string
}

export interface CreateSessionRequest {
  novel_id?: number
  chapter_number?: number
  level: SessionLevel
  model?: LLMModel
}

export interface ChatRequest {
  message: string
  model?: LLMModel
  temperature?: number
}

export interface ChatResponse {
  message_id: string
  content: string
  word_count: number
  context_usage: number
}

export interface ClearResponse {
  cleared: boolean
  messages_removed: number
}

export interface UpdateNovelContextRequest {
  title?: string
  description?: string
  genre?: string
  outline?: string
  world_setting?: string
  characters_summary?: string
  main_plot?: string
}

export interface UpdateChapterContextRequest {
  chapter_number: number
  chapter_title?: string
  previous_summary?: string
  current_outline?: string
  key_events?: string[]
  focus_characters?: string[]
}

export interface SessionStatsResponse {
  message_count: number
  token_count: number
  context_window: number
  usage_ratio: number
  should_compress: boolean
}

export const sessionApi = {
  create: async (data: CreateSessionRequest): Promise<ApiResponse<Session>> => {
    return apiClient.post('/sessions/create', data)
  },

  get: async (sessionId: string): Promise<ApiResponse<Session>> => {
    return apiClient.get(`/sessions/${sessionId}`)
  },

  delete: async (sessionId: string): Promise<ApiResponse<void>> => {
    return apiClient.delete(`/sessions/${sessionId}`)
  },

  list: async (params?: {
    novel_id?: number
    level?: SessionLevel
    page?: number
    page_size?: number
  }): Promise<ApiResponse<PaginatedResponse<Session>>> => {
    return apiClient.get('/sessions/list', { params })
  },

  getMessages: async (sessionId: string, params?: {
    page?: number
    page_size?: number
  }): Promise<ApiResponse<PaginatedResponse<SessionMessage>>> => {
    return apiClient.get(`/sessions/${sessionId}/messages`, { params })
  },

  chat: async (sessionId: string, data: ChatRequest): Promise<ApiResponse<ChatResponse>> => {
    return apiClient.post(`/sessions/${sessionId}/chat`, data)
  },

  clear: async (sessionId: string): Promise<ApiResponse<ClearResponse>> => {
    return apiClient.post(`/sessions/${sessionId}/clear`)
  },

  compress: async (sessionId: string): Promise<ApiResponse<{ compressed: boolean; messages_removed: number }>> => {
    return apiClient.post(`/sessions/${sessionId}/compress`)
  },

  updateNovelContext: async (sessionId: string, data: UpdateNovelContextRequest): Promise<ApiResponse<void>> => {
    return apiClient.put(`/sessions/${sessionId}/context/novel`, data)
  },

  updateChapterContext: async (sessionId: string, data: UpdateChapterContextRequest): Promise<ApiResponse<void>> => {
    return apiClient.put(`/sessions/${sessionId}/context/chapter`, data)
  },

  getStats: async (sessionId: string): Promise<ApiResponse<SessionStatsResponse>> => {
    return apiClient.get(`/sessions/${sessionId}/stats`)
  },

  updateTitle: async (sessionId: string, data: UpdateTitleRequest): Promise<ApiResponse<Session>> => {
    return apiClient.put(`/sessions/${sessionId}/title`, data)
  },

  autoGenerateTitle: async (sessionId: string): Promise<ApiResponse<AutoGenerateTitleResponse>> => {
    return apiClient.post(`/sessions/${sessionId}/title/auto-generate`)
  },
}
