import { useState, useEffect } from 'react'
import { Folder } from 'lucide-react'
import { useApp } from '@/hooks/useApp'

export default function GeneralConfigTab() {
  const app = useApp()
  const [dataDir, setDataDir] = useState('')

  useEffect(() => {
    app.GetAppConfig().then(cfg => {
      const dir = (cfg?.data_dir as string) || ''
      setDataDir(dir)
    }).catch(() => {})
  }, [app])

  return (
    <div className="flex-1 flex flex-col">
      <h3 className="text-sm font-medium mb-5">基础配置</h3>

      <div className="space-y-2">
        <label className="text-xs font-medium text-muted-foreground flex items-center gap-1.5">
          <Folder className="w-3.5 h-3.5" />
          数据目录
        </label>
        <div className="flex items-center gap-2">
          <input
            value={dataDir}
            readOnly
            className="flex-1 h-8 rounded-md border bg-muted/50 px-3 text-xs font-mono focus:outline-none cursor-default"
          />
        </div>
      </div>
    </div>
  )
}
