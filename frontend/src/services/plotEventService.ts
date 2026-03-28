import apiClient from './apiClient'
import type { PlotEvent, PlotEventDetail, PlotEventCreate, PlotEventUpdate, PlotEventListParams } from '@/types/plotEvent'
import type { ApiResponse, PaginatedResponse } from '@/types/api'

export const plotEventApi = {
  getPlotEvents: async (novelId: number, params: PlotEventListParams): Promise<ApiResponse<PaginatedResponse<PlotEvent>>> => {
    return apiClient.get(`/novels/${novelId}/plot-events`, { params })
  },

  getPlotEvent: async (eventId: number): Promise<ApiResponse<PlotEventDetail>> => {
    return apiClient.get(`/plot-events/${eventId}`)
  },

  createPlotEvent: async (novelId: number, data: PlotEventCreate): Promise<ApiResponse<PlotEvent>> => {
    return apiClient.post(`/novels/${novelId}/plot-events`, data)
  },

  updatePlotEvent: async (eventId: number, data: PlotEventUpdate): Promise<ApiResponse<PlotEvent>> => {
    return apiClient.put(`/plot-events/${eventId}`, data)
  },

  deletePlotEvent: async (eventId: number): Promise<ApiResponse<void>> => {
    return apiClient.delete(`/plot-events/${eventId}`)
  },
}
