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

export const aiApi = {
  generateChapter: async (novelId: number, chapterId: number, data: GenerationRequest): Promise<ApiResponse<GenerationResponse>> => {
    return apiClient.post(`/novels/${novelId}/chapters/${chapterId}/generate`, data)
  },

  checkConsistency: async (novelId: number, data: ConsistencyCheckRequest): Promise<ApiResponse<ConsistencyCheckResponse>> => {
    return apiClient.post(`/novels/${novelId}/consistency-check`, data)
  },

  searchMemory: async (novelId: number, data: MemorySearchRequest): Promise<ApiResponse<MemorySearchResponse>> => {
    return apiClient.post(`/novels/${novelId}/memory/search`, data)
  },
}
