import apiClient from './apiClient'
import type { LoginRequest, RegisterRequest, AuthResponse, RefreshTokenResponse, User } from '@/types/auth'
import type { ApiResponse } from '@/types/api'

export const authApi = {
  login: async (data: LoginRequest): Promise<ApiResponse<AuthResponse>> => {
    return apiClient.post('/auth/login', data)
  },

  register: async (data: RegisterRequest): Promise<ApiResponse<User>> => {
    return apiClient.post('/auth/register', data)
  },

  refreshToken: async (): Promise<ApiResponse<RefreshTokenResponse>> => {
    const refreshToken = localStorage.getItem('refresh_token')
    return apiClient.post('/auth/refresh', null, {
      headers: {
        Authorization: `Bearer ${refreshToken}`,
      },
    })
  },

  getCurrentUser: async (): Promise<ApiResponse<User>> => {
    return apiClient.get('/auth/me')
  },
}
