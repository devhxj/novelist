import { useState, useCallback, useEffect, useRef } from 'react'
import type { EditorTab } from '@/components/content/types'

let idSeq = 0
function nextId(prefix: string) { return `${prefix}_${++idSeq}` }

type TabMeta = Pick<EditorTab, 'path' | 'title' | 'type' | 'viewMode' | 'readOnly'>

function tabMetasForStorage(tabs: EditorTab[], activeTabId: string | null): TabMeta[] {
  const metas = tabs.map(t => ({ path: t.path, title: t.title, type: t.type, viewMode: t.viewMode, readOnly: t.readOnly }))
  if (!activeTabId) return metas

  const activeIndex = tabs.findIndex(t => t.id === activeTabId)
  if (activeIndex < 0) return metas

  return [
    metas[activeIndex],
    ...metas.filter((_, index) => index !== activeIndex),
  ]
}

// E2：localStorage 里的历史形状可能与当前 TabMeta 不兼容（字段更名/类型变化）。
// 逐条校验必需字段，丢弃不合法项而不是让坏结构进入渲染导致白屏。
function isRestorableTabMeta(value: unknown): value is TabMeta {
  if (value == null || typeof value !== 'object') return false
  const candidate = value as Record<string, unknown>
  if (candidate.type !== 'file' && candidate.type !== 'diff') return false
  if (typeof candidate.path !== 'string' || candidate.path === '') return false
  if (typeof candidate.title !== 'string' || candidate.title === '') return false
  const viewMode = candidate.viewMode
  if (viewMode !== undefined &&
    viewMode !== 'content' && viewMode !== 'outline' && viewMode !== 'preview' && viewMode !== 'edit') {
    return false
  }
  if (candidate.readOnly !== undefined && typeof candidate.readOnly !== 'boolean') return false
  return true
}

function sanitizeRestoredMetas(saved: unknown): TabMeta[] {
  if (!Array.isArray(saved)) return []
  const valid = saved.filter(isRestorableTabMeta)
  if (valid.length === saved.length) return valid
  console.warn(
    `[useEditorTabs] 已忽略 ${saved.length - valid.length} 个形状不兼容的恢复标签（历史版本遗留数据）。`,
  )
  return valid
}

export function useEditorTabs(novelId: number) {
  const [tabs, setTabs] = useState<EditorTab[]>([])
  const [activeTabId, setActiveTabId] = useState<string | null>(null)
  const prevNovelIdRef = useRef(novelId)
  const allMetasRef = useRef<Record<string, TabMeta[]>>({})
  const initRef = useRef(false)
  const skipInitialEmptyPersistRef = useRef(false)
  const activeTabIdRef = useRef(activeTabId)
  useEffect(() => { activeTabIdRef.current = activeTabId }, [activeTabId])

  // 启动加载：从 localStorage 恢复
  useEffect(() => {
    if (initRef.current) return
    try {
      const raw = localStorage.getItem('novelist_tabs_all')
      if (raw) allMetasRef.current = JSON.parse(raw)
    } catch {
      allMetasRef.current = {}
    }
    const key = String(novelId)
    const saved = sanitizeRestoredMetas(allMetasRef.current[key])
    if (saved.length) {
      // 丢弃后回写，避免坏数据每次启动都重新触发告警。
      allMetasRef.current[key] = saved
      skipInitialEmptyPersistRef.current = true
      const restored: EditorTab[] = saved.map(t => ({
        ...t,
        id: nextId(t.type === 'diff' ? 'diff' : 'file'),
      }))
      setTabs(restored)
      setActiveTabId(restored[0].id)
    }
    initRef.current = true
  }, [novelId])

  // beforeunload 时保存到 localStorage
  useEffect(() => {
    function save() {
      try {
        localStorage.setItem('novelist_tabs_all', JSON.stringify(allMetasRef.current))
      } catch {
        return
      }
    }
    window.addEventListener('beforeunload', save)
    return () => window.removeEventListener('beforeunload', save)
  }, [])

  // novelId 变化：切换标签集
  useEffect(() => {
    if (!initRef.current) return
    const oldKey = String(prevNovelIdRef.current)
    const newKey = String(novelId)
    if (oldKey === newKey) return

    prevNovelIdRef.current = novelId
    const saved = sanitizeRestoredMetas(allMetasRef.current[newKey])
    if (saved.length) {
      allMetasRef.current[newKey] = saved
      const restored: EditorTab[] = saved.map(t => ({
        ...t,
        id: nextId(t.type === 'diff' ? 'diff' : 'file'),
      }))
      setTabs(restored)
      setActiveTabId(restored[0].id)
    } else {
      setTabs([])
      setActiveTabId(null)
    }
  }, [novelId])

  // tabs 变化时立即持久化，避免 ContentPanel 在面板切换中卸载后丢失会话标签。
  useEffect(() => {
    if (!initRef.current) return
    if (skipInitialEmptyPersistRef.current && tabs.length === 0) {
      skipInitialEmptyPersistRef.current = false
      return
    }
    skipInitialEmptyPersistRef.current = false

    const key = String(prevNovelIdRef.current)
    const metas = tabMetasForStorage(tabs, activeTabId)
    if (metas.length > 0) {
      allMetasRef.current[key] = metas
    } else {
      delete allMetasRef.current[key]
    }
    try {
      localStorage.setItem('novelist_tabs_all', JSON.stringify(allMetasRef.current))
    } catch {
      return
    }
  }, [tabs, activeTabId])

  const activeTab = tabs.find(t => t.id === activeTabId) ?? null

  const openTab = useCallback((tab: Omit<EditorTab, 'id'> & { id?: string }) => {
    const id = tab.id ?? nextId(tab.type)
    setTabs(prev => {
      const existing = prev.find(t => t.path === tab.path && t.type === tab.type)
      if (existing) { setActiveTabId(existing.id); return prev }
      return [...prev, { ...tab, id }]
    })
    setActiveTabId(id)
  }, [])

  const closeAllTabs = useCallback(() => {
    setTabs([])
    setActiveTabId(null)
  }, [])

  const closeTab = useCallback((id: string) => {
    setTabs(prev => {
      if (prev.length <= 1) {
        setActiveTabId(null)
        return []
      }
      const idx = prev.findIndex(t => t.id === id)
      const next = prev.filter(t => t.id !== id)
      if (activeTabIdRef.current === id) {
        const newIdx = Math.min(idx, next.length - 1)
        setActiveTabId(next[newIdx].id)
      }
      return next
    })
  }, [])

  const updateTab = useCallback((id: string, patch: Partial<EditorTab>) => {
    setTabs(prev => prev.map(t => t.id === id ? { ...t, ...patch } : t))
  }, [])

  const openDiffTab = useCallback((data: {
    path: string; title: string; diff: string; original: string; modified: string
    changeType: string; reason: string; toolId: string
  }) => {
    const id = nextId('diff')
    setTabs(prev => [...prev, { id, type: 'diff', ...data }])
    setActiveTabId(id)
    return id
  }, [])

  return {
    tabs, activeTab, activeTabId,
    openTab, closeTab, closeAllTabs, setActiveTabId,
    updateTab, openDiffTab,
    initRef,
  }
}
