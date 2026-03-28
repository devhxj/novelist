import apiClient from './apiClient'
import type { Character, CharacterDetail, CharacterCreate, CharacterUpdate, CharacterListParams } from '@/types/character'
import type { ApiResponse, PaginatedResponse } from '@/types/api'

export const characterApi = {
  getCharacters: async (novelId: number, params: CharacterListParams): Promise<ApiResponse<PaginatedResponse<Character>>> => {
    return apiClient.get(`/novels/${novelId}/characters`, { params })
  },

  getCharacter: async (characterId: number): Promise<ApiResponse<CharacterDetail>> => {
    return apiClient.get(`/characters/${characterId}`)
  },

  createCharacter: async (novelId: number, data: CharacterCreate): Promise<ApiResponse<Character>> => {
    return apiClient.post(`/novels/${novelId}/characters`, data)
  },

  updateCharacter: async (characterId: number, data: CharacterUpdate): Promise<ApiResponse<Character>> => {
    return apiClient.put(`/characters/${characterId}`, data)
  },

  deleteCharacter: async (characterId: number): Promise<ApiResponse<void>> => {
    return apiClient.delete(`/characters/${characterId}`)
  },
}
