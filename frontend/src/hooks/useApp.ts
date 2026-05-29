import {
  Chat,
  CreateNovel,
  GetAppConfig,
  GetChapters,
  GetNovels,
  GetPlatform,
  GetSettings,
  Initialize,
  IsInitialized,
  SaveSettings,
} from '@/lib/wailsjs/go/app/App'
import type { app, novel, chapter, config } from '@/lib/wailsjs/go/models'

export function useApp() {
  return {
    Chat,
    CreateNovel,
    GetAppConfig,
    GetChapters,
    GetNovels,
    GetPlatform,
    GetSettings,
    Initialize,
    IsInitialized,
    SaveSettings,
  }
}

export type { app, novel, chapter, config }
