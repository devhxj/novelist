import { useCallback, useEffect, useRef, useState } from 'react'
import type { novelImport } from '@/lib/novelist/types'
import { EventsOn } from '@/lib/novelist/events'
import { buildStartNovelImportInput } from '@/lib/novelist/importBoundary'

export const NOVEL_IMPORT_PROGRESS_EVENT = 'novel_import:progress'

export type NovelImportUiStatus =
  | 'idle'
  | 'selecting'
  | 'running'
  | 'cancelling'
  | 'completed'
  | 'warning'
  | 'error'
  | 'cancelled'

export interface NovelImportUiState {
  status: NovelImportUiStatus
  taskId: string | null
  input: novelImport.StartNovelImportInput | null
  progress: novelImport.ImportProgress | null
  run: novelImport.ImportRun | null
  errorMessage: string
}

interface UseNovelImportOptions {
  onStartNovelImport: (input: novelImport.StartNovelImportInput) => Promise<novelImport.ImportRun>
  onCancelNovelImport: (input: novelImport.CancelNovelImportInput) => Promise<novelImport.ImportRun>
  onFinished?: (run: novelImport.ImportRun) => Promise<void> | void
}

const IDLE_STATE: NovelImportUiState = {
  status: 'idle',
  taskId: null,
  input: null,
  progress: null,
  run: null,
  errorMessage: '',
}

export function useNovelImport({
  onStartNovelImport,
  onCancelNovelImport,
  onFinished,
}: UseNovelImportOptions) {
  const [state, setState] = useState<NovelImportUiState>(IDLE_STATE)
  const activeTaskIdRef = useRef<string | null>(null)
  const finishedRef = useRef(onFinished)

  useEffect(() => {
    finishedRef.current = onFinished
  }, [onFinished])

  useEffect(() => {
    return EventsOn<novelImport.ImportProgress>(NOVEL_IMPORT_PROGRESS_EVENT, progress => {
      const activeTaskId = activeTaskIdRef.current
      if (!activeTaskId || progress.task_id !== activeTaskId) return

      setState(current => {
        if (current.taskId !== progress.task_id) return current
        const nextProgress = progress.current_chapter_title || !current.progress
          ? progress
          : {
            ...progress,
            current_chapter_index: current.progress.current_chapter_index,
            current_chapter_title: current.progress.current_chapter_title,
          }
        return {
          ...current,
          progress: nextProgress,
          status: statusFromProgress(progress, current.status),
        }
      })
    })
  }, [])

  const beginSelecting = useCallback(() => {
    setState({
      ...IDLE_STATE,
      status: 'selecting',
      progress: {
        task_id: 'selecting',
        state: 'selecting',
        stage: 'selecting',
        progress_completed: 0,
        progress_total: 1,
        message: '请选择 EPUB、TXT 或 Markdown 文件',
        updated_at: new Date().toISOString(),
      },
    })
  }, [])

  const markSelectionCancelled = useCallback(() => {
    activeTaskIdRef.current = null
    setState({
      ...IDLE_STATE,
      status: 'cancelled',
      progress: {
        task_id: 'selection-cancelled',
        state: 'cancelled',
        stage: 'selecting',
        progress_completed: 1,
        progress_total: 1,
        message: '已取消选择',
        updated_at: new Date().toISOString(),
      },
    })
  }, [])

  const startFromPath = useCallback(async (sourcePath: string) => {
    let input: novelImport.StartNovelImportInput
    try {
      input = buildStartNovelImportInput(sourcePath)
    } catch (error) {
      activeTaskIdRef.current = null
      setState({
        ...IDLE_STATE,
        status: 'error',
        errorMessage: errorMessage(error, '无法开始导入'),
      })
      return null
    }

    activeTaskIdRef.current = input.task_id
    setState({
      status: 'running',
      taskId: input.task_id,
      input,
      run: null,
      errorMessage: '',
      progress: {
        task_id: input.task_id,
        state: 'selecting',
        stage: 'selecting',
        progress_completed: 0,
        progress_total: 1,
        message: '已选择文件，正在创建导入任务',
        updated_at: new Date().toISOString(),
      },
    })

    try {
      const run = await onStartNovelImport(input)
      if (activeTaskIdRef.current !== input.task_id) return run

      activeTaskIdRef.current = null
      setState(current => ({
        ...current,
        run,
        progress: current.progress ?? progressFromRun(run),
        status: statusFromRun(run),
        errorMessage: run.error?.message ?? '',
      }))
      await finishedRef.current?.(run)
      return run
    } catch (error) {
      if (activeTaskIdRef.current !== input.task_id) return null

      activeTaskIdRef.current = null
      setState(current => ({
        ...current,
        status: 'error',
        errorMessage: errorMessage(error, '导入失败，请重试'),
      }))
      return null
    }
  }, [onStartNovelImport])

  const cancel = useCallback(async () => {
    const taskId = activeTaskIdRef.current
    if (!taskId) return null

    setState(current => ({
      ...current,
      status: 'cancelling',
      progress: current.progress
        ? { ...current.progress, message: '正在取消并清理导入' }
        : current.progress,
    }))

    try {
      const run = await onCancelNovelImport({ task_id: taskId, reason: 'User cancelled novel import from the import dialog.' })
      if (activeTaskIdRef.current !== taskId) return run

      activeTaskIdRef.current = null
      setState(current => ({
        ...current,
        run,
        progress: current.progress ?? progressFromRun(run),
        status: statusFromRun(run),
        errorMessage: run.error?.message ?? '',
      }))
      await finishedRef.current?.(run)
      return run
    } catch (error) {
      if (activeTaskIdRef.current !== taskId) return null

      setState(current => ({
        ...current,
        status: 'running',
        errorMessage: errorMessage(error, '取消导入失败'),
      }))
      return null
    }
  }, [onCancelNovelImport])

  const close = useCallback(() => {
    setState(current => canCloseImportState(current) ? IDLE_STATE : current)
  }, [])

  return {
    state,
    beginSelecting,
    markSelectionCancelled,
    startFromPath,
    cancel,
    close,
  }
}

export function canCloseImportState(state: NovelImportUiState): boolean {
  return state.status === 'completed' ||
    state.status === 'warning' ||
    state.status === 'error' ||
    state.status === 'cancelled'
}

function statusFromProgress(
  progress: novelImport.ImportProgress,
  currentStatus: NovelImportUiStatus,
): NovelImportUiStatus {
  if (currentStatus === 'cancelling') return 'cancelling'
  return statusFromState(progress.state)
}

function statusFromRun(run: novelImport.ImportRun): NovelImportUiStatus {
  if (run.error?.code === 'import.cancelled') return 'cancelled'
  return statusFromState(run.state)
}

function statusFromState(state: string): NovelImportUiStatus {
  switch (state) {
    case 'completed':
      return 'completed'
    case 'completed_with_warning':
      return 'warning'
    case 'cancelled':
      return 'cancelled'
    case 'failed':
    case 'cleanup_completed':
    case 'cleanup_blocked':
      return 'error'
    default:
      return 'running'
  }
}

function progressFromRun(run: novelImport.ImportRun): novelImport.ImportProgress {
  return {
    task_id: run.task_id,
    state: run.state,
    stage: run.stage,
    progress_completed: isTerminalRun(run.state) ? 1 : 0,
    progress_total: 1,
    message: run.error?.message ?? run.warnings[0]?.message ?? '导入状态已更新',
    created_novel_id: run.created_novel_id,
    updated_at: run.updated_at,
  }
}

function isTerminalRun(state: string): boolean {
  return state === 'completed' ||
    state === 'completed_with_warning' ||
    state === 'failed' ||
    state === 'cancelled' ||
    state === 'cleanup_completed' ||
    state === 'cleanup_blocked'
}

function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error) return error.message
  if (typeof error === 'string') return error
  return fallback
}
