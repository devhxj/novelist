import { useState, useEffect } from 'react'
import { Folder, RefreshCw } from 'lucide-react'
import { useApp, type novel } from '@/hooks/useApp'

export default function GeneralConfigTab() {
  const app = useApp()
  const [dataDir, setDataDir] = useState('')
  const [novels, setNovels] = useState<novel.Novel[]>([])
  const [selectedID, setSelectedID] = useState<number>(0)
  const [rebuilding, setRebuilding] = useState(false)

  useEffect(() => {
    app.GetAppConfig().then(cfg => {
      setDataDir((cfg?.data_dir as string) || '')
    }).catch(() => {})
    app.GetNovels().then(list => {
      setNovels(list || [])
    }).catch(() => {})
    app.GetSettings().then(s => {
      if (s?.last_novel_id) setSelectedID(s.last_novel_id)
    }).catch(() => {})
  }, [app])

  async function handleRebuild() {
    if (!selectedID) return
    setRebuilding(true)
    try {
      await app.RebuildNovelIndex(selectedID)
    } catch (err) {
      console.error('Rebuild failed:', err)
    } finally {
      setRebuilding(false)
    }
  }

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

      <div className="mt-6 space-y-2">
        <label className="text-xs font-medium text-muted-foreground">维护</label>
        <p className="text-[11px] text-muted-foreground">搜索异常时，可重建指定小说的向量索引。</p>
        <div className="flex items-center gap-2">
          <select
            value={selectedID}
            onChange={e => setSelectedID(Number(e.target.value))}
            className="h-8 rounded-md border bg-background px-2 text-xs focus:outline-none"
          >
            {novels.map(n => (
              <option key={n.id} value={n.id}>{n.title}</option>
            ))}
          </select>
          <button
            onClick={handleRebuild}
            disabled={rebuilding || !selectedID}
            className="inline-flex items-center gap-1.5 h-8 px-3 rounded-md text-xs border hover:bg-muted transition-colors disabled:opacity-50"
          >
            <RefreshCw className={`w-3.5 h-3.5 ${rebuilding ? 'animate-spin' : ''}`} />
            {rebuilding ? '重建中...' : '重建向量索引'}
          </button>
        </div>
      </div>
    </div>
  )
}
