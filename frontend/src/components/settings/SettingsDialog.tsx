import { useState } from 'react'
import { Settings, Cpu } from 'lucide-react'
import ModelConfigTab from './ModelConfigTab'
import GeneralConfigTab from './GeneralConfigTab'
import { useDialogA11y } from '@/hooks/useDialogA11y'
import { pushToast } from '@/lib/toast'

type Tab = 'general' | 'model'

interface Props {
  open: boolean
  onClose: () => void
  onSaved?: () => void
  initialTab?: Tab
}

export default function SettingsDialog({ open, onClose, onSaved, initialTab = 'model' }: Props) {
  const [activeTab, setActiveTab] = useState<Tab>(initialTab)
  // 数据目录迁移等独占操作进行中时，对话框不可被关闭（U13）：
  // 半途关掉会把进行中的迁移留在后台、成功/失败反馈随组件卸载丢失。
  const [busy, setBusy] = useState(false)
  const guardedClose = () => {
    if (busy) {
      pushToast({ kind: 'info', message: '操作正在进行中，完成前无法关闭设置。' })
      return
    }
    onClose()
  }
  const dialogProps = useDialogA11y(open, guardedClose, '设置')

  if (!open) return null

  const tabs: { id: Tab; label: string; icon: React.ReactNode }[] = [
    { id: 'general', label: '基础设置', icon: <Settings className="w-4 h-4" /> },
    { id: 'model', label: '模型配置', icon: <Cpu className="w-4 h-4" /> },
  ]

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      {/* 遮罩 */}
      <div className="absolute inset-0 bg-black/40" onClick={guardedClose} />

      {/* 弹窗 */}
      <div
        {...dialogProps}
        className="relative flex h-[760px] max-h-[calc(100vh-32px)] w-[920px] max-w-[95vw] overflow-hidden rounded-xl border bg-background shadow-2xl"
      >
        {/* 左侧导航 */}
        <nav className="w-[160px] border-r py-4 px-2 flex flex-col gap-1 shrink-0">
          <div className="text-sm font-medium px-3 pb-3 text-foreground">设置</div>
          {tabs.map(tab => (
            <button
              key={tab.id}
              onClick={() => setActiveTab(tab.id)}
              // R1：独占操作（数据目录迁移）进行中锁定 tab 切换——否则 GeneralConfigTab 卸载会
              // 重置 busy（对话框在迁移进行中变得可关），进行中的调用失去归属与反馈。
              disabled={busy}
              className={`flex items-center gap-2 px-3 py-2 rounded-lg text-sm transition-colors w-full text-left disabled:cursor-not-allowed disabled:opacity-50 ${
                activeTab === tab.id
                  ? 'bg-primary/10 text-primary font-medium'
                  : 'text-muted-foreground hover:text-foreground hover:bg-muted/50'
              }`}
            >
              {tab.icon}
              {tab.label}
            </button>
          ))}
        </nav>

        {/* 右侧内容区 */}
        <div className="min-h-0 flex-1 overflow-y-auto p-5 pr-6 flex flex-col min-w-0">
          {/* 关闭按钮 */}
          <button
            onClick={guardedClose}
            aria-disabled={busy}
            className="absolute top-3 right-3 w-7 h-7 flex items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-muted transition-colors disabled:opacity-50"
          >
            ✕
          </button>

          {activeTab === 'model' ? (
            <ModelConfigTab onSaved={onSaved} />
          ) : (
            <GeneralConfigTab onBusyChange={setBusy} />
          )}
        </div>
      </div>
    </div>
  )
}
