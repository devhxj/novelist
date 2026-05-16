import apiClient from './apiClient'
import type { ApiResponse, PaginatedResponse } from '@/types/api'
import type { UsageData } from './wsEditorService'

export type LLMModel = 'deepseek-v4-flash' | 'deepseek-v4-pro' | 'glm-4.7-flash'

export type SessionLevel = 'novel' | 'chapter' | 'free'

export interface SessionMessage {
  id: string
  role: 'system' | 'user' | 'assistant' | 'tool'
  content: string
  created_at: string
}

export interface SessionStats extends Partial<UsageData> {
  token_count: number
  context_window: number
  usage_ratio: number
  should_compress: boolean
}

export interface Session {
  id: string
  session_id: string
  level: SessionLevel
  title?: string
  novel_id?: number
  chapter_number?: number
  chapter_number_end?: number
  model: LLMModel
  stats: SessionStats
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

export interface SessionStatsResponse extends SessionStats {}

export interface ModelOption {
  id: string
  name: string
  provider: string
  supports_thinking: boolean
}

export const sessionApi = {
  getModels: async (): Promise<ApiResponse<{ models: ModelOption[] }>> => {
    return apiClient.get('/models')
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
    size?: number
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

  getStats: async (sessionId: string): Promise<ApiResponse<SessionStatsResponse>> => {
    return apiClient.get(`/sessions/${sessionId}/usage`)
  },

  updateTitle: async (sessionId: string, data: UpdateTitleRequest): Promise<ApiResponse<Session>> => {
    return apiClient.put(`/sessions/${sessionId}/title`, data)
  },

  autoGenerateTitle: async (sessionId: string): Promise<ApiResponse<AutoGenerateTitleResponse>> => {
    return apiClient.post(`/sessions/${sessionId}/title/auto-generate`)
  },
}
