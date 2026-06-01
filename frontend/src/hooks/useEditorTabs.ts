import { useState, useCallback } from 'react'

export type EditorTab = {
  id: string
  type: 'edit' | 'diff'
  path: string
  title: string
  // edit tab
  content?: string
  isDirty?: boolean
  viewMode?: 'content' | 'outline'
  // diff tab
  diff?: string
  original?: string
  modified?: string
  changeType?: string
  reason?: string
  toolId?: string
}

let idSeq = 0
function nextId(prefix: string) { return `${prefix}_${++idSeq}` }

export function useEditorTabs() {
  const [tabs, setTabs] = useState<EditorTab[]>([])
  const [activeTabId, setActiveTabId] = useState<string | null>(null)

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

  const closeTab = useCallback((id: string) => {
    setTabs(prev => {
      const idx = prev.findIndex(t => t.id === id)
      const next = prev.filter(t => t.id !== id)
      if (activeTabId === id && next.length > 0) {
        const newIdx = Math.min(idx, next.length - 1)
        setActiveTabId(next[newIdx].id)
      } else if (next.length === 0) {
        setActiveTabId(null)
      }
      return next
    })
  }, [activeTabId])

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

  const findDiffByToolId = useCallback((toolId: string) => {
    return tabs.find(t => t.type === 'diff' && t.toolId === toolId) ?? null
  }, [tabs])

  return {
    tabs, activeTab, activeTabId,
    openTab, closeTab, setActiveTabId,
    updateTab, openDiffTab, findDiffByToolId,
  }
}
