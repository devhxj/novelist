import apiClient from './apiClient'
import type { Chapter, ChapterDetail, ChapterCreate, ChapterUpdate, ChapterListParams } from '@/types/chapter'
import type { ApiResponse, PaginatedResponse } from '@/types/api'

export const chapterApi = {
  getChapters: async (novelId: number, params: ChapterListParams): Promise<ApiResponse<PaginatedResponse<Chapter>>> => {
    return apiClient.get(`/chapters/novel/${novelId}`, { params })
  },

  getNextChapterNumber: async (novelId: number): Promise<ApiResponse<{ next_chapter_number: number; message: string }>> => {
    return apiClient.get(`/chapters/novel/${novelId}/next-number`)
  },

  getChapter: async (chapterId: number): Promise<ApiResponse<ChapterDetail>> => {
    return apiClient.get(`/chapters/${chapterId}`)
  },

  createChapter: async (novelId: number, data: ChapterCreate): Promise<ApiResponse<Chapter>> => {
    return apiClient.post(`/chapters`, { ...data, novel_id: novelId })
  },

  updateChapter: async (chapterId: number, data: ChapterUpdate): Promise<ApiResponse<Chapter>> => {
    return apiClient.put(`/chapters/${chapterId}`, data)
  },

  deleteChapter: async (chapterId: number): Promise<ApiResponse<void>> => {
    return apiClient.delete(`/chapters/${chapterId}`)
  },
}
