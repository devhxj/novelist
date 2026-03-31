import apiClient from './apiClient'
import type { ApiResponse } from '@/types/api'

export interface StartEditResponse {
  edit_session_id: string
  chapter_id: number
  original_content: string
  working_content: string
  change_count: number
  status: string
  message: string
}

export interface ApplyEditResponse {
  edit_session_id: string
  change_count: number
  working_content: string
  diff: {
    change_type: string
    hunks: Array<{
      old_start: number
      old_lines: number
      new_start: number
      new_lines: number
      changes: Array<{ type: string; content: string; line_number: number }>
    }>
    old_content: string
    new_content: string
    summary: { additions: number; deletions: number; hunks: number }
  }
  message: string
}

export interface AcceptEditResponse {
  edit_session_id: string
  chapter_id: number
  change_count: number
  word_count: number
  message: string
}

export interface RejectEditResponse {
  edit_session_id: string
  chapter_id: number
  message: string
}

export interface ChapterEditStatus {
  has_active_edit: boolean
  edit_session_id?: string
  status?: string
  change_count?: number
  working_content?: string
  original_content?: string
  diff?: Record<string, unknown>
  chapter_content?: string
  message?: string
}

export interface ChapterForEditor {
  chapter_id: number
  chapter_number: number
  title: string
  content: string
  word_count: number
  status: string
  has_active_edit: boolean
  edit_session_id: string | null
  working_content: string | null
  change_count: number
}

export const editorApi = {
  startEdit: (chapterId: number, wsSessionId: string): Promise<ApiResponse<StartEditResponse>> => {
    return apiClient.post('/editor/session/start', { chapter_id: chapterId, ws_session_id: wsSessionId })
  },

  applyEdit: (editSessionId: string, data: {
    change_type: string
    new_content: string
    start_line?: number
    end_line?: number
    reason?: string
    source?: string
  }): Promise<ApiResponse<ApplyEditResponse>> => {
    return apiClient.post(`/editor/session/${editSessionId}/apply`, data)
  },

  acceptEdit: (editSessionId: string): Promise<ApiResponse<AcceptEditResponse>> => {
    return apiClient.post(`/editor/session/${editSessionId}/accept`)
  },

  rejectEdit: (editSessionId: string): Promise<ApiResponse<RejectEditResponse>> => {
    return apiClient.post(`/editor/session/${editSessionId}/reject`)
  },

  getEditStatus: (editSessionId: string): Promise<ApiResponse<Record<string, unknown>>> => {
    return apiClient.get(`/editor/session/${editSessionId}`)
  },

  getChapterEditStatus: (chapterId: number): Promise<ApiResponse<ChapterEditStatus>> => {
    return apiClient.get(`/editor/chapter/${chapterId}/status`)
  },

  getChapterForEditor: (chapterId: number): Promise<ApiResponse<ChapterForEditor>> => {
    return apiClient.get(`/editor/chapter/${chapterId}`)
  },
}
