import apiClient from './apiClient'
import type { Novel, NovelDetail, NovelCreate, NovelUpdate, NovelListParams } from '@/types/novel'
import type { ApiResponse, PaginatedResponse } from '@/types/api'

export const novelApi = {
  getNovels: async (params: NovelListParams): Promise<ApiResponse<PaginatedResponse<Novel>>> => {
    return apiClient.get('/novels', { params })
  },

  getNovel: async (novelId: number): Promise<ApiResponse<NovelDetail>> => {
    return apiClient.get(`/novels/${novelId}`)
  },

  createNovel: async (data: NovelCreate): Promise<ApiResponse<Novel>> => {
    return apiClient.post('/novels', data)
  },

  updateNovel: async (novelId: number, data: NovelUpdate): Promise<ApiResponse<Novel>> => {
    return apiClient.put(`/novels/${novelId}`, data)
  },

  deleteNovel: async (novelId: number): Promise<ApiResponse<void>> => {
    return apiClient.delete(`/novels/${novelId}`)
  },
}
