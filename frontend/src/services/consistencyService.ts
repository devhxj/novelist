import apiClient from './apiClient'
import type {
  Foreshadowing,
  ForeshadowingCreate,
  ForeshadowingUpdate,
  ForeshadowingResolve,
  ConsistencyCheckRequest,
  ConsistencyCheckResponse,
  ForeshadowingStatistics,
} from '@/types/consistency'
import type { ApiResponse, PaginatedResponse } from '@/types/api'

export const consistencyApi = {
  checkConsistency: async (novelId: number, data: ConsistencyCheckRequest): Promise<ApiResponse<ConsistencyCheckResponse>> => {
    return apiClient.post(`/consistency/novels/${novelId}/check`, data)
  },

  getForeshadowings: async (
    novelId: number,
    params?: {
      status?: string
      foreshadowing_type?: string
      page?: number
      page_size?: number
    }
  ): Promise<ApiResponse<PaginatedResponse<Foreshadowing>>> => {
    return apiClient.get(`/consistency/novels/${novelId}/foreshadowings`, { params })
  },

  createForeshadowing: async (novelId: number, data: ForeshadowingCreate): Promise<ApiResponse<Foreshadowing>> => {
    return apiClient.post(`/consistency/novels/${novelId}/foreshadowings`, data)
  },

  getForeshadowing: async (novelId: number, foreshadowingId: number): Promise<ApiResponse<Foreshadowing>> => {
    return apiClient.get(`/consistency/novels/${novelId}/foreshadowings/${foreshadowingId}`)
  },

  updateForeshadowing: async (novelId: number, foreshadowingId: number, data: ForeshadowingUpdate): Promise<ApiResponse<Foreshadowing>> => {
    return apiClient.put(`/consistency/novels/${novelId}/foreshadowings/${foreshadowingId}`, data)
  },

  resolveForeshadowing: async (novelId: number, foreshadowingId: number, data: ForeshadowingResolve): Promise<ApiResponse<Foreshadowing>> => {
    return apiClient.post(`/consistency/novels/${novelId}/foreshadowings/${foreshadowingId}/resolve`, data)
  },

  abandonForeshadowing: async (novelId: number, foreshadowingId: number, reason?: string): Promise<ApiResponse<Foreshadowing>> => {
    return apiClient.post(`/consistency/novels/${novelId}/foreshadowings/${foreshadowingId}/abandon`, null, {
      params: { reason },
    })
  },

  getUnresolvedForeshadowings: async (novelId: number): Promise<ApiResponse<{ items: Foreshadowing[]; total: number }>> => {
    return apiClient.get(`/consistency/novels/${novelId}/foreshadowings/unresolved`)
  },

  getForeshadowingStatistics: async (novelId: number): Promise<ApiResponse<ForeshadowingStatistics>> => {
    return apiClient.get(`/consistency/novels/${novelId}/foreshadowings/statistics`)
  },
}
