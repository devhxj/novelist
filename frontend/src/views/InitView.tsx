import { useState, useEffect } from 'react'
import { useApp } from '@/hooks/useApp'
import { Button } from '@/components/ui/button'
import logo from '@/assets/logo.svg'

interface Props {
  onInitialized: () => void
}

export default function InitView({ onInitialized }: Props) {
  const app = useApp()
  const [dataDir, setDataDir] = useState('')
  const [error, setError] = useState('')
  const [initializing, setInitializing] = useState(false)

  useEffect(() => {
    app.GetPlatform().then((info) => {
      if (info.defaultPath) setDataDir(info.defaultPath as string)
    })
  }, [])

  async function handleInit() {
    setError('')
    setInitializing(true)
    try {
      await app.Initialize(dataDir)
      onInitialized()
    } catch (e) {
      setError(String(e))
      setInitializing(false)
    }
  }

  return (
    <div className="flex items-center justify-center min-h-screen">
      <div className="w-full max-w-lg mx-auto px-8 py-12 text-center">
        <img src={logo} alt="Goink" className="h-16 w-16 mx-auto mb-8" />

        <h1 className="text-3xl font-semibold tracking-tight mb-3">
          欢迎使用 Goink
        </h1>

        <p className="text-base text-muted-foreground mb-10">
          你的 AI 创作伙伴
        </p>

        <div className="bg-muted/40 rounded-lg px-5 py-4 mb-3 text-left">
          <p className="text-xs text-muted-foreground mb-1">创作数据将存储在此目录</p>
          <p className="text-sm font-mono break-all">{dataDir || '加载中...'}</p>
        </div>

        <p className="text-xs text-muted-foreground mb-10">
          所有小说、角色、设置等数据可整体备份或迁移
        </p>

        {error && (
          <p className="text-sm text-destructive mb-6">{error}</p>
        )}

        <Button
          size="lg"
          className="w-full"
          onClick={handleInit}
          disabled={!dataDir || initializing}
        >
          {initializing ? '正在初始化...' : '开始使用'}
        </Button>
      </div>
    </div>
  )
}
