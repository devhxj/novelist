import { appApi, type NovelistAppApi } from '@/lib/novelist/api'
import type {
  app,
  chapter,
  character,
  config,
  llm,
  location,
  novel,
  reader,
  search,
  session,
  skill,
  storyarc,
  timeline,
} from '@/lib/novelist/types'

export function useApp(): NovelistAppApi {
  return appApi
}

export type {
  app,
  novel,
  chapter,
  config,
  llm,
  session,
  character,
  location,
  storyarc,
  timeline,
  reader,
  skill,
  search,
}
