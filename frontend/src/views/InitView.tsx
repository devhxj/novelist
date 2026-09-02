import { useState, useEffect, useCallback } from 'react'
import { useApp } from '@/hooks/useApp'
import { useTheme, type Theme } from '@/hooks/useTheme'
import { Button } from '@/components/ui/button'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics } from '@/lib/novelist/types'
import { Sun, Moon } from 'lucide-react'
import Logo from '@/components/Logo'

const THEME_OPTIONS: { key: Theme; icon: React.ReactNode; label: string }[] = [
  { key: 'light', icon: <Sun className="w-5 h-5" />, label: '浅色模式' },
  { key: 'dark', icon: <Moon className="w-5 h-5" />, label: '深色模式' },
]

// 探测失败时拿不到平台信息，只能靠 UA 给个手填示例，避免让作者猜路径写法。
const DATA_DIR_PLACEHOLDER = /windows/i.test(typeof navigator === 'undefined' ? '' : navigator.userAgent)
  ? 'D:\\Novelist'
  : '/Users/你的用户名/Novelist'

function ThemePreview({ theme }: { theme: Theme }) {
  const isLight = theme === 'light'
  const mockupBg = isLight ? '#f5efd7' : '#1b1f2b'
  const sidebarBg = isLight ? '#e8dfbf' : '#111827'
  const line1 = isLight ? '#d4c69a' : '#374151'
  const line2 = isLight ? '#b9ab79' : '#4b5563'
  const accent = isLight ? '#5f7138' : '#a78bfa'

  return (
    <div className="rounded-lg p-2 mb-3 border border-border" style={{ backgroundColor: mockupBg }}>
      <div className="flex gap-2">
        <div className="w-6 h-14 rounded-sm flex flex-col gap-1 p-1" style={{ backgroundColor: sidebarBg }}>
          <div className="w-4 h-1 rounded-sm" style={{ backgroundColor: accent }} />
          <div className="w-3 h-1 rounded-sm mt-0.5" style={{ backgroundColor: line1 }} />
          <div className="w-3 h-1 rounded-sm" style={{ backgroundColor: line1 }} />
        </div>
        <div className="flex-1 flex flex-col gap-1.5 pt-1">
          <div className="h-1.5 w-3/4 rounded-sm" style={{ backgroundColor: line1 }} />
          <div className="h-1.5 w-1/2 rounded-sm" style={{ backgroundColor: line2 }} />
          <div className="h-1.5 w-2/3 rounded-sm" style={{ backgroundColor: line2 }} />
          <div className="flex gap-1 mt-1">
            <div className="h-2 w-11 rounded-sm" style={{ backgroundColor: accent, opacity: 0.85 }} />
            <div className="h-2 w-7 rounded-sm border" style={{ borderColor: line1, backgroundColor: 'transparent' }} />
          </div>
        </div>
      </div>
    </div>
  )
}

interface Props {
  onInitialized: () => void
}

export default function InitView({ onInitialized }: Props) {
  const app = useApp()
  const { theme, setTheme } = useTheme()
  const [selectedTheme, setSelectedTheme] = useState<Theme>(theme)
  const [dataDir, setDataDir] = useState('')
  const [error, setError] = useState('')
  const [initializing, setInitializing] = useState(false)
  const [detecting, setDetecting] = useState(true)
  // 默认目录探测失败时，首屏原本会永远停在"加载中..."且按钮禁用，作者无路可走。
  const [detectError, setDetectError] = useState<{ message: string; diagnostic: diagnostics.CopyableDiagnostic } | null>(null)

  const detectDefaultDir = useCallback(async () => {
    setDetecting(true)
    setDetectError(null)
    try {
      const info = await app.GetPlatform()
      const defaultPath = typeof info?.defaultPath === 'string' ? info.defaultPath.trim() : ''
      if (!defaultPath) throw new Error('未返回可用的默认数据目录')
      setDataDir(defaultPath)
      setDetectError(null)
    } catch (e) {
      setDetectError({
        message: diagnosticMessage(e, '无法自动确定数据目录，请手动填写一个可写目录。'),
        diagnostic: buildCopyableDiagnostic({
          error: e,
          fallbackMessage: '无法自动确定数据目录',
          operation: 'InitView.GetPlatform',
          bridgeMethod: 'GetPlatform',
        }),
      })
    } finally {
      setDetecting(false)
    }
  }, [app])

  // 挂起探测放进定时器回调：探测入口会同步置状态，直接在 effect 体调用会触发级联渲染告警。
  useEffect(() => {
    const timer = window.setTimeout(() => { void detectDefaultDir() }, 0)
    return () => window.clearTimeout(timer)
  }, [detectDefaultDir])

  function handleThemeSelect(t: Theme) {
    setSelectedTheme(t)
    setTheme(t)
  }

  async function handleInit() {
    setError('')
    setInitializing(true)
    try {
      await app.Initialize(dataDir.trim())
      onInitialized()
    } catch (e) {
      setError(String(e))
      setInitializing(false)
    }
  }

  return (
    <div className="flex items-center justify-center min-h-screen">
      <div className="w-full max-w-lg mx-auto px-8 py-12 text-center">
        <Logo className="h-16 w-16 mx-auto mb-8" />

        <h1 className="text-3xl font-semibold tracking-tight mb-3">
          欢迎使用 Novelist
        </h1>

        <p className="text-base text-muted-foreground mb-8">
          你的 AI 创作伙伴
        </p>

        {/* 主题选择 */}
        <div className="mb-8">
          <p className="text-sm text-muted-foreground mb-3">选择界面主题</p>
          <div className="grid grid-cols-2 gap-3">
            {THEME_OPTIONS.map((opt) => {
              const selected = selectedTheme === opt.key
              return (
                <button
                  key={opt.key}
                  onClick={() => handleThemeSelect(opt.key)}
                  className={`
                    relative rounded-xl border-2 p-3 text-left transition-all cursor-pointer
                    ${selected
                      ? 'border-primary ring-2 ring-primary/20'
                      : 'border-border hover:border-muted-foreground/50 hover:-translate-y-0.5 hover:shadow-md'}
                  `}
                >
                  <ThemePreview theme={opt.key} />
                  <div className="flex items-center gap-2">
                    <span className={selected ? 'text-primary' : 'text-muted-foreground'}>
                      {opt.icon}
                    </span>
                    <span className="text-sm font-medium">{opt.label}</span>
                    {selected && (
                      <span className="ml-auto w-4 h-4 rounded-full bg-primary flex items-center justify-center">
                        <span className="w-1.5 h-1.5 rounded-full bg-background" />
                      </span>
                    )}
                  </div>
                </button>
              )
            })}
          </div>
        </div>

        {detectError ? (
          <div className="mb-3 text-left">
            <ErrorCallout
              compact
              title="无法自动确定数据目录"
              message={detectError.message}
              diagnostic={detectError.diagnostic}
              retryLabel="重新检测"
              retrying={detecting}
              onRetry={() => { void detectDefaultDir() }}
              className="rounded-lg"
            />
            <label htmlFor="init-data-dir" className="mt-3 block text-xs text-muted-foreground">
              手动填写创作数据目录
            </label>
            <input
              id="init-data-dir"
              type="text"
              value={dataDir}
              onChange={e => setDataDir(e.target.value)}
              placeholder={DATA_DIR_PLACEHOLDER}
              spellCheck={false}
              className="mt-1 w-full h-9 rounded-md border bg-background px-3 font-mono text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
            <p className="mt-1 text-xs text-muted-foreground">
              填一个你有写入权限的空目录，Novelist 会在其中创建数据文件。
            </p>
          </div>
        ) : (
          <div className="bg-muted/40 rounded-lg px-5 py-4 mb-3 text-left">
            <p className="text-xs text-muted-foreground mb-1">创作数据将存储在此目录</p>
            <p className="text-sm font-mono break-all">{dataDir || (detecting ? '检测中...' : '未检测到目录')}</p>
          </div>
        )}

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
          disabled={!dataDir.trim() || initializing}
        >
          {initializing ? '正在初始化...' : '开始使用'}
        </Button>
      </div>
    </div>
  )
}
