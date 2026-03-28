import apiClient from './apiClient'
import type {
  GenerationRequest,
  GenerationResponse,
  ConsistencyCheckRequest,
  ConsistencyCheckResponse,
  MemorySearchRequest,
  MemorySearchResponse,
} from '@/types/ai'
import type { ApiResponse } from '@/types/api'

export const generationApi = {
  generateChapter: async (novelId: number, chapterId: number, data: GenerationRequest): Promise<ApiResponse<GenerationResponse>> => {
    return apiClient.post(`/generation/novels/${novelId}/chapters/${chapterId}`, data)
  },

  regenerateChapter: async (novelId: number, chapterId: number, data: GenerationRequest): Promise<ApiResponse<GenerationResponse>> => {
    return apiClient.post(`/generation/novels/${novelId}/chapters/${chapterId}/regenerate`, data)
  },

  getTasks: async (novelId: number): Promise<ApiResponse<any[]>> => {
    return apiClient.get(`/generation/novels/${novelId}/tasks`)
  },

  getTaskStatus: async (taskId: string): Promise<ApiResponse<any>> => {
    return apiClient.get(`/generation/tasks/${taskId}`)
  },
}

export const consistencyApi = {
  checkConsistency: async (novelId: number, data: ConsistencyCheckRequest): Promise<ApiResponse<ConsistencyCheckResponse>> => {
    return apiClient.post(`/novels/${novelId}/consistency-check`, data)
  },
}

export const memoryApi = {
  searchMemory: async (novelId: number, data: MemorySearchRequest): Promise<ApiResponse<MemorySearchResponse>> => {
    return apiClient.post(`/novels/${novelId}/memory/search`, data)
  },
}
