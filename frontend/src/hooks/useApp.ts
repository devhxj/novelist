import {
  ApproveTool,
  Chat,
  CreateChapter,
  CreateNovel,
  GetContent,
  SaveContent,
  SetActiveNovel,
  SetApprovalMode,
  GetAppConfig,
  GetChapters,
  GetNovels,
  GetPlatform,
  GetSettings,
  Initialize,
  IsInitialized,
  SaveSettings,
  GetLLMConfig,
  GetModels,
  GetSessions,
  GetSessionMessages,
  SaveLLMConfig,
} from '@/lib/wailsjs/go/app/App'
import type { app, novel, chapter, config, llm, session } from '@/lib/wailsjs/go/models'

export function useApp() {
  return {
    Chat,
    CreateChapter,
    CreateNovel,
    GetContent,
    SaveContent,
    GetAppConfig,
    GetChapters,
    GetNovels,
    GetPlatform,
    GetSettings,
    Initialize,
    IsInitialized,
    SaveSettings,
    SetActiveNovel,
    GetModels,
    GetSessions,
    GetSessionMessages,
    ApproveTool,
    SetApprovalMode,
    GetLLMConfig,
    SaveLLMConfig,
  }
}

export type { app, novel, chapter, config, llm, session }
