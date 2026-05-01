import apiClient from './apiClient'
import type { ApiResponse } from '@/types/api'
import type {
  MCPToolCategory,
  MCPToolInfo,
  MCPToolResult,
  NovelSummary,
  ChapterListResult,
  ChapterContentResult,
  NovelProgressResult,
  CharacterListResult,
  CharacterDetailResult,
  MemorySearchResult,
  CharacterMemoryResult,
  RecentContextResult,
  ConsistencyCheckResult,
  ForeshadowingStatusResult,
} from '@/types/mcp'

export const mcpApi = {
  listTools: async (category?: MCPToolCategory): Promise<ApiResponse<{ tools: MCPToolInfo[]; total: number }>> => {
    return apiClient.get('/mcp/tools', { params: { category } })
  },

  listCategories: async (): Promise<ApiResponse<Record<string, MCPToolInfo[]>>> => {
    return apiClient.get('/mcp/tools/categories')
  },

  getToolInfo: async (toolName: string): Promise<ApiResponse<MCPToolInfo>> => {
    return apiClient.get(`/mcp/tools/${toolName}`)
  },

  executeTool: async (toolName: string, params: Record<string, any>): Promise<ApiResponse<MCPToolResult>> => {
    return apiClient.post(`/mcp/tools/${toolName}/execute`, params)
  },

  getNovelSummary: async (novelId: number): Promise<ApiResponse<NovelSummary>> => {
    return apiClient.post(`/mcp/novels/${novelId}/summary`)
  },

  getChapterList: async (
    novelId: number,
    params?: { status?: string; page?: number; page_size?: number }
  ): Promise<ApiResponse<ChapterListResult>> => {
    return apiClient.post(`/mcp/novels/${novelId}/chapters/list`, null, { params })
  },

  getChapterContent: async (
    chapterId: number,
    includeSummary?: boolean
  ): Promise<ApiResponse<ChapterContentResult>> => {
    return apiClient.post(`/mcp/chapters/${chapterId}/content`, null, {
      params: { include_summary: includeSummary ?? true },
    })
  },

  getNovelProgress: async (novelId: number): Promise<ApiResponse<NovelProgressResult>> => {
    return apiClient.post(`/mcp/novels/${novelId}/progress`)
  },

  getCharacterList: async (novelId: number, search?: string): Promise<ApiResponse<CharacterListResult>> => {
    return apiClient.post(`/mcp/novels/${novelId}/characters/list`, null, {
      params: { search },
    })
  },

  getCharacterDetail: async (characterId: number): Promise<ApiResponse<CharacterDetailResult>> => {
    return apiClient.post(`/mcp/characters/${characterId}/detail`)
  },

  searchPlotMemory: async (
    novelId: number,
    query: string,
    topK?: number,
    chapterIds?: number[]
  ): Promise<ApiResponse<MemorySearchResult>> => {
    return apiClient.post(`/mcp/novels/${novelId}/memory/search`, null, {
      params: {
        query,
        top_k: topK ?? 10,
        chapter_ids: chapterIds?.join(','),
      },
    })
  },

  getCharacterMemory: async (
    characterId: number,
  ): Promise<ApiResponse<CharacterMemoryResult>> => {
    return apiClient.post(`/mcp/characters/${characterId}/memory`)
  },

  getRecentContext: async (
    chapterId: number,
    windowSize?: number,
    contextSize?: number
  ): Promise<ApiResponse<RecentContextResult>> => {
    return apiClient.post(`/mcp/chapters/${chapterId}/context`, null, {
      params: {
        window_size: windowSize ?? 3,
        context_size: contextSize ?? 3000,
      },
    })
  },

  checkCharacterConsistency: async (
    novelId: number,
    chapterIds?: number[],
    characterId?: number
  ): Promise<ApiResponse<ConsistencyCheckResult>> => {
    return apiClient.post(`/mcp/novels/${novelId}/consistency/character`, null, {
      params: {
        chapter_ids: chapterIds?.join(','),
        character_id: characterId,
      },
    })
  },

  checkPlotConsistency: async (
    novelId: number,
    chapterIds?: number[]
  ): Promise<ApiResponse<ConsistencyCheckResult>> => {
    return apiClient.post(`/mcp/novels/${novelId}/consistency/plot`, null, {
      params: { chapter_ids: chapterIds?.join(',') },
    })
  },

  runFullConsistencyCheck: async (
    novelId: number,
    chapterIds?: number[],
    checkTypes?: string[]
  ): Promise<ApiResponse<ConsistencyCheckResult>> => {
    return apiClient.post(`/mcp/novels/${novelId}/consistency/full`, null, {
      params: {
        chapter_ids: chapterIds?.join(','),
        check_types: checkTypes?.join(','),
      },
    })
  },

  listUnresolvedPlots: async (
    novelId: number,
    minImportance?: number,
  ): Promise<ApiResponse<{ items: any[]; total: number }>> => {
    return apiClient.get(`/timeline/novels/${novelId}`, {
      params: {
        category: 'foreshadowing',
        status: 'pending',
        page_size: minImportance ? undefined : 100,
      },
    })
  },

  getForeshadowingStatus: async (novelId: number): Promise<ApiResponse<ForeshadowingStatusResult>> => {
    return apiClient.get(`/timeline/novels/${novelId}/stats`)
  },
}
