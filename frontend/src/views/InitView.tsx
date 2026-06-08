import { useState, useEffect } from 'react'
import { useApp } from '@/hooks/useApp'
import { Button } from '@/components/ui/button'

interface Props {
  onInitialized: () => void
}

export default function InitView({ onInitialized }: Props) {
  const app = useApp()
  const [dataDir, setDataDir] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    app.GetPlatform().then((info) => {
      if (info.defaultPath) setDataDir(info.defaultPath as string)
    })
  }, [])

  async function handleInit() {
    setLoading(true)
    setError('')
    try {
      await app.Initialize(dataDir)
      onInitialized()
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className="flex items-center justify-center min-h-screen">
      <div className="w-full max-w-md mx-auto p-8">
        <div className="text-center mb-8">
          <h1 className="text-2xl font-semibold tracking-tight mb-2">
            欢迎使用 Goink
          </h1>
        </div>

        <div className="space-y-4">
          <div className="text-xs text-muted-foreground text-center bg-muted/30 rounded-md py-2 px-3 font-mono">
            {dataDir || '加载中...'}
          </div>

          {error && (
            <p className="text-sm text-destructive">{error}</p>
          )}

          <Button
            className="w-full"
            onClick={handleInit}
            disabled={loading || !dataDir}
          >
            {loading ? '正在初始化...' : '开始使用'}
          </Button>
        </div>
      </div>
    </div>
  )
}
