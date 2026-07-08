import { Suspense, lazy, useState, useEffect, useCallback } from 'react'
import { useApp } from '@/hooks/useApp'
import InitView from '@/views/InitView'
import { Button } from '@/components/ui/button'
import { AlertTriangle } from 'lucide-react'
import type { novelImport } from '@/lib/novelist/types'

type View = 'loading' | 'init' | 'workspace' | 'error'

interface StartupError {
  title: string
  message: string
  detail?: string
}

const WorkspaceView = lazy(() => import('@/views/WorkspaceView'))

export default function App() {
  const [view, setView] = useState<View>('loading')
  const [initialNovelId, setInitialNovelId] = useState(0)
  const [fromInit, setFromInit] = useState(false)
  const [startupError, setStartupError] = useState<StartupError | null>(null)
  const [startupRecovery, setStartupRecovery] = useState<novelImport.ImportReconciliationResult | null>(null)
  const app = useApp()

  const checkStartup = useCallback(async () => {
    setStartupError(null)
    setView('loading')
    try {
      const ok = await app.IsInitialized()
      if (ok) {
        const config = await app.GetAppConfig()
        const settings = await app.GetSettings()
        setStartupRecovery(config?.import_recovery ?? null)
        setInitialNovelId(settings?.last_novel_id ?? 0)
        setView('workspace')
      } else {
        setStartupRecovery(null)
        setView('init')
      }
    } catch (error) {
      setStartupError(describeStartupError(error))
      setView('error')
    }
  }, [app])

  useEffect(() => {
    let cancelled = false

    app.IsInitialized().then(async (ok) => {
      if (cancelled) return
      if (ok) {
        const config = await app.GetAppConfig()
        if (cancelled) return
        const settings = await app.GetSettings()
        if (cancelled) return
        setStartupRecovery(config?.import_recovery ?? null)
        setInitialNovelId(settings?.last_novel_id ?? 0)
        setView('workspace')
      } else {
        setStartupRecovery(null)
        setView('init')
      }
    }).catch((error) => {
      if (cancelled) return
      setStartupError(describeStartupError(error))
      setView('error')
    })

    return () => { cancelled = true }
  }, [app])

  if (view === 'loading') {
    return (
      <div className="flex items-center justify-center min-h-screen bg-background">
        <p className="text-muted-foreground">加载中...</p>
      </div>
    )
  }

  if (view === 'error') {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background px-6">
        <div className="w-full max-w-md text-center">
          <div className="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-danger-bg text-destructive">
            <AlertTriangle className="h-6 w-6" />
          </div>
          <h1 className="mb-2 text-xl font-semibold tracking-tight">
            {startupError?.title ?? '启动检查失败'}
          </h1>
          <p className="text-sm leading-6 text-muted-foreground">
            {startupError?.message ?? 'Novelist 无法读取当前初始化状态。请重试。'}
          </p>
          {startupError?.detail && (
            <p className="mt-3 break-words rounded-md bg-muted px-3 py-2 text-left text-xs text-muted-foreground">
              {startupError.detail}
            </p>
          )}
          <Button type="button" className="mt-6 w-full" onClick={() => void checkStartup()}>
            重试
          </Button>
        </div>
      </div>
    )
  }

  return (
    <div className="min-h-screen bg-background text-foreground">
      {view === 'init' && (
        <InitView onInitialized={async () => {
          const config = await app.GetAppConfig()
          const settings = await app.GetSettings()
          setStartupRecovery(config?.import_recovery ?? null)
          setInitialNovelId(settings?.last_novel_id ?? 0)
          setFromInit(true)
          setView('workspace')
        }} />
      )}
      {view === 'workspace' && (
        <Suspense fallback={<LoadingScreen />}>
          <WorkspaceView
            initialNovelId={initialNovelId}
            initialShowHelp={fromInit}
            startupRecovery={startupRecovery}
          />
        </Suspense>
      )}
    </div>
  )
}

function LoadingScreen() {
  return (
    <div className="flex min-h-screen items-center justify-center bg-background">
      <p className="text-muted-foreground">加载中...</p>
    </div>
  )
}

function describeStartupError(error: unknown): StartupError {
  const code = typeof error === 'object' && error !== null && 'code' in error
    ? String((error as { code?: unknown }).code ?? '')
    : ''
  const message = error instanceof Error ? error.message : String(error)

  if (code === 'BRIDGE_UNAVAILABLE') {
    return {
      title: '无法连接桌面桥接',
      message: '请确认正在通过 Novelist 桌面应用打开此界面，然后重试。',
      detail: message,
    }
  }

  return {
    title: '启动检查失败',
    message: 'Novelist 无法读取当前初始化状态。请重试，若仍失败请检查本地数据目录或桌面桥接服务。',
    detail: message,
  }
}
