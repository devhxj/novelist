import { create } from 'zustand'
import type { Novel, NovelDetail } from '@/types/novel'

interface NovelState {
  novels: Novel[]
  currentNovel: NovelDetail | null
  loading: boolean
  setNovels: (novels: Novel[]) => void
  setCurrentNovel: (novel: NovelDetail | null) => void
  setLoading: (loading: boolean) => void
  addNovel: (novel: Novel) => void
  updateNovel: (novelId: number, updates: Partial<Novel>) => void
  removeNovel: (novelId: number) => void
}

export const useNovelStore = create<NovelState>((set) => ({
  novels: [],
  currentNovel: null,
  loading: false,
  setNovels: (novels) => set({ novels }),
  setCurrentNovel: (novel) => set({ currentNovel: novel }),
  setLoading: (loading) => set({ loading }),
  addNovel: (novel) => set((state) => ({ novels: [...state.novels, novel] })),
  updateNovel: (novelId, updates) =>
    set((state) => ({
      novels: state.novels.map((n) => (n.id === novelId ? { ...n, ...updates } : n)),
    })),
  removeNovel: (novelId) =>
    set((state) => ({
      novels: state.novels.filter((n) => n.id !== novelId),
    })),
}))
