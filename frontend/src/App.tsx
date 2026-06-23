import { useState, useEffect } from 'react'
import { useApp } from '@/hooks/useApp'
import InitView from '@/views/InitView'
import WorkspaceView from '@/views/WorkspaceView'

type View = 'loading' | 'init' | 'workspace'

export default function App() {
  const [view, setView] = useState<View>('loading')
  const [initialNovelId, setInitialNovelId] = useState(0)
  const [fromInit, setFromInit] = useState(false)
  const app = useApp()

  useEffect(() => {
    app.IsInitialized().then(async (ok) => {
      if (ok) {
        const settings = await app.GetSettings()
        setInitialNovelId(settings?.last_novel_id ?? 0)
        setView('workspace')
      } else {
        setView('init')
      }
    }).catch((err) => {
      console.error('App initialization failed', err)
      setView('init')
    })
  }, [])

  if (view === 'loading') {
    return (
      <div className="flex items-center justify-center min-h-screen bg-background">
        <p className="text-muted-foreground">加载中...</p>
      </div>
    )
  }

  return (
    <div className="min-h-screen bg-background text-foreground">
      {view === 'init' && (
        <InitView onInitialized={async () => {
          const settings = await app.GetSettings()
          setInitialNovelId(settings?.last_novel_id ?? 0)
          setFromInit(true)
          setView('workspace')
        }} />
      )}
      {view === 'workspace' && (
        <WorkspaceView initialNovelId={initialNovelId} initialShowHelp={fromInit} />
      )}
    </div>
  )
}
