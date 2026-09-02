import { useCallback, useEffect, useRef, useState } from 'react'
import { useApp } from '@/hooks/useApp'
import type { reference } from '@/lib/novelist/types'

export interface MaterializationCompletion {
  anchor_id: number
  anchor_title: string
  run_id: string
  status: 'completed' | 'failed' | 'cancelled'
  error_message: string | null
}

const POLL_INTERVAL_MS = 30_000
const ACTIVE_STATUSES = new Set(['queued', 'running'])

function isActive(status: string | undefined): boolean {
  return status !== undefined && ACTIVE_STATUSES.has(status)
}

// 材料化全局监视器：离开「素材库」页后仍周期性轮询活跃 run，
// 检测到 终态 转变时给出一次性的完成/失败通知（含失败原因），供状态栏展示。
export function useMaterializationWatcher(
  novelId: number,
  anchors: reference.Anchor[],
  enabled: boolean,
  onCompleted?: () => void,
): { notifications: MaterializationCompletion[]; dismiss: (runId: string) => void; activeCount: number } {
  const app = useApp()
  const [notifications, setNotifications] = useState<MaterializationCompletion[]>([])
  const [activeCount, setActiveCount] = useState(0)
  const prevStatusesRef = useRef<Map<number, { status: string; run_id: string }>>(new Map())
  const titlesRef = useRef<Map<number, string>>(new Map())

  useEffect(() => {
    titlesRef.current = new Map(anchors.map((anchor) => [anchor.anchor_id, anchor.title]))
  }, [anchors])

  const poll = useCallback(async () => {
    if (!novelId || anchors.length === 0) {
      setActiveCount(0)
      return
    }
    const anchorIds = anchors.map((anchor) => anchor.anchor_id)
    const statuses = await Promise.all(anchorIds.map(async (anchorId) => {
      try {
        return { anchorId, status: await app.GetReferenceMaterializationStatus({ novel_id: novelId, anchor_id: anchorId }) }
      } catch {
        return { anchorId, status: null }
      }
    }))
    let active = 0
    const arrivals: MaterializationCompletion[] = []
    for (const { anchorId, status } of statuses) {
      const runId = status?.run_id
      const runStatus = status?.status
      if (isActive(runStatus)) {
        active++
        prevStatusesRef.current.set(anchorId, { status: runStatus!, run_id: runId! })
        continue
      }
      const previous = prevStatusesRef.current.get(anchorId)
      if (previous && status && (runStatus === 'completed' || runStatus === 'failed' || runStatus === 'cancelled')) {
        arrivals.push({
          anchor_id: anchorId,
          anchor_title: titlesRef.current.get(anchorId) ?? '参考书',
          run_id: runId!,
          status: runStatus,
          error_message: status.last_error_message ?? null,
        })
      }
      if (status) {
        prevStatusesRef.current.set(anchorId, { status: runStatus!, run_id: runId! })
      } else {
        prevStatusesRef.current.delete(anchorId)
      }
    }
    setActiveCount(active)
    if (arrivals.length > 0) {
      setNotifications((current) => [...arrivals, ...current.filter(item => !arrivals.some(a => a.run_id === item.run_id))].slice(0, 3))
      // 材料化产物（观察/标本/覆盖面）变化：通知调用方刷新依赖语料数据的视图（覆盖度、总览等）。
      onCompleted?.()
    }
  }, [app, novelId, anchors, onCompleted])

  useEffect(() => {
    if (!enabled || !novelId || anchors.length === 0) {
      return
    }
    const timer = window.setInterval(() => { void poll() }, POLL_INTERVAL_MS)
    const initial = window.setTimeout(() => { void poll() }, 0)
    return () => {
      window.clearInterval(timer)
      window.clearTimeout(initial)
    }
  }, [enabled, novelId, anchors, poll])

  const dismiss = useCallback((runId: string) => {
    setNotifications((current) => current.filter(item => item.run_id !== runId))
  }, [])

  return { notifications, dismiss, activeCount }
}
