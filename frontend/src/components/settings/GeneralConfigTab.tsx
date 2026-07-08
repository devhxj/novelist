import { useState, useEffect } from 'react'
import { BellRing, Folder, GitCommitHorizontal, RefreshCw } from 'lucide-react'
import { useApp, type novel } from '@/hooks/useApp'
import type { update } from '@/lib/novelist/types'
import UpdateDialog from '@/components/update/UpdateDialog'

export default function GeneralConfigTab() {
  const app = useApp()
  const [dataDir, setDataDir] = useState('')
  const [novels, setNovels] = useState<novel.Novel[]>([])
  const [selectedID, setSelectedID] = useState<number>(0)
  const [rebuilding, setRebuilding] = useState(false)
  const [gitAuthorName, setGitAuthorName] = useState('')
  const [gitAuthorEmail, setGitAuthorEmail] = useState('')
  const [gitAuthorSaving, setGitAuthorSaving] = useState(false)
  const [gitAuthorMessage, setGitAuthorMessage] = useState('')
  const [updateEnabled, setUpdateEnabled] = useState(false)
  const [updateEndpoint, setUpdateEndpoint] = useState('')
  const [updateDismissedVersion, setUpdateDismissedVersion] = useState('')
  const [updateSaving, setUpdateSaving] = useState(false)
  const [updateChecking, setUpdateChecking] = useState(false)
  const [updateMessage, setUpdateMessage] = useState('')
  const [updateResult, setUpdateResult] = useState<update.UpdateCheckResult | null>(null)
  const [showUpdateDialog, setShowUpdateDialog] = useState(false)

  useEffect(() => {
    app.GetAppConfig().then(cfg => {
      setDataDir(cfg?.data_dir || '')
      if (cfg?.update_check?.endpoint_url) {
        setUpdateEndpoint(prev => prev || cfg.update_check.endpoint_url)
      }
    }).catch(() => {})
    app.GetNovels().then(list => {
      setNovels(list || [])
    }).catch(() => {})
    app.GetSettings().then(s => {
      if (s?.last_novel_id) setSelectedID(s.last_novel_id)
    }).catch(() => {})
    app.GetGitAuthorSettings().then(settings => {
      setGitAuthorName(settings?.name || '')
      setGitAuthorEmail(settings?.email || '')
    }).catch(() => {
      setGitAuthorMessage('Git 作者设置加载失败')
    })
    app.GetUpdateCheckSettings().then(settings => {
      setUpdateEnabled(settings?.enabled === true)
      setUpdateEndpoint(settings?.endpoint_url || '')
      setUpdateDismissedVersion(settings?.dismissed_version || '')
    }).catch(() => {
      setUpdateMessage('更新检查设置加载失败')
    })
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

  async function handleSaveGitAuthor() {
    const name = gitAuthorName.trim()
    const email = gitAuthorEmail.trim()

    if ((name && !email) || (!name && email)) {
      setGitAuthorMessage('Git 作者名称和邮箱必须同时填写')
      return
    }

    if (email && !isValidGitEmail(email)) {
      setGitAuthorMessage('请输入有效的 Git 作者邮箱')
      return
    }

    setGitAuthorSaving(true)
    setGitAuthorMessage('')
    try {
      const saved = await app.SaveGitAuthorSettings({ name, email })
      setGitAuthorName(saved.name)
      setGitAuthorEmail(saved.email)
      setGitAuthorMessage(saved.name ? 'Git 作者设置已保存' : 'Git 作者设置已清空，将使用默认身份')
      window.setTimeout(() => setGitAuthorMessage(''), 2400)
    } catch (err) {
      setGitAuthorMessage(errorText(err, 'Git 作者设置保存失败'))
    } finally {
      setGitAuthorSaving(false)
    }
  }

  async function handleSaveUpdateSettings(nextDismissedVersion = updateDismissedVersion) {
    const endpoint = updateEndpoint.trim()
    if (updateEnabled && !endpoint) {
      setUpdateMessage('启用更新检查时必须填写 HTTPS endpoint')
      return
    }

    if (endpoint && !isHttpsUrl(endpoint)) {
      setUpdateMessage('更新检查 endpoint 必须是 HTTPS 地址')
      return
    }

    setUpdateSaving(true)
    setUpdateMessage('')
    try {
      const saved = await app.SaveUpdateCheckSettings({
        enabled: updateEnabled,
        endpoint_url: endpoint,
        dismissed_version: nextDismissedVersion.trim(),
      })
      setUpdateEnabled(saved.enabled)
      setUpdateEndpoint(saved.endpoint_url)
      setUpdateDismissedVersion(saved.dismissed_version)
      setUpdateMessage(saved.enabled ? '更新检查设置已保存' : '更新检查已关闭')
      window.setTimeout(() => setUpdateMessage(''), 2400)
    } catch (err) {
      setUpdateMessage(errorText(err, '更新检查设置保存失败'))
    } finally {
      setUpdateSaving(false)
    }
  }

  async function handleManualUpdateCheck() {
    const endpoint = updateEndpoint.trim()
    if (!endpoint) {
      setUpdateMessage('请先填写更新检查 endpoint')
      return
    }

    if (!isHttpsUrl(endpoint)) {
      setUpdateMessage('更新检查 endpoint 必须是 HTTPS 地址')
      return
    }

    setUpdateChecking(true)
    setUpdateMessage('')
    try {
      await app.SaveUpdateCheckSettings({
        enabled: updateEnabled,
        endpoint_url: endpoint,
        dismissed_version: updateDismissedVersion,
      })
      const result = await app.CheckForUpdates({
        task_id: `update-manual-${Date.now().toString(36)}`,
        manual: true,
      })
      setUpdateResult(result)
      if (result.status === 'update_available') {
        setShowUpdateDialog(true)
        setUpdateMessage(`发现新版本 ${result.latest_version || ''}`.trim())
      } else if (result.status === 'no_update') {
        setUpdateMessage('当前已是最新版本')
      } else if (result.status === 'failed') {
        setUpdateMessage(result.error_message || '更新检查失败')
      } else {
        setUpdateMessage('更新检查已完成')
      }
    } catch (err) {
      setUpdateMessage(errorText(err, '更新检查失败'))
    } finally {
      setUpdateChecking(false)
    }
  }

  async function handleDismissUpdateVersion(version: string) {
    const endpoint = updateEndpoint.trim()
    const saved = await app.SaveUpdateCheckSettings({
      enabled: updateEnabled,
      endpoint_url: endpoint,
      dismissed_version: version,
    })
    setUpdateDismissedVersion(saved.dismissed_version)
    setUpdateMessage(`已忽略版本 ${version}`)
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

      <div className="mt-6 space-y-3">
        <label className="text-xs font-medium text-muted-foreground flex items-center gap-1.5">
          <GitCommitHorizontal className="w-3.5 h-3.5" />
          Git 提交作者
        </label>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <div className="space-y-1.5">
            <label htmlFor="git-author-name" className="text-[11px] text-muted-foreground">作者名称</label>
            <input
              id="git-author-name"
              value={gitAuthorName}
              onChange={e => setGitAuthorName(e.target.value)}
              maxLength={512}
              className="h-8 w-full rounded-md border bg-background px-2 text-xs focus:outline-none focus:ring-2 focus:ring-ring"
              placeholder="Novelist"
            />
          </div>
          <div className="space-y-1.5">
            <label htmlFor="git-author-email" className="text-[11px] text-muted-foreground">作者邮箱</label>
            <input
              id="git-author-email"
              value={gitAuthorEmail}
              onChange={e => setGitAuthorEmail(e.target.value)}
              maxLength={320}
              className="h-8 w-full rounded-md border bg-background px-2 text-xs focus:outline-none focus:ring-2 focus:ring-ring"
              placeholder="novelist@local"
            />
          </div>
        </div>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-[11px] text-muted-foreground">留空时使用安全默认身份；保存后会在下一次仓库初始化或提交前写入 repo-local Git config。</p>
          <button
            type="button"
            onClick={handleSaveGitAuthor}
            disabled={gitAuthorSaving}
            className="inline-flex h-8 items-center gap-1.5 rounded-md border px-3 text-xs transition-colors hover:bg-muted disabled:opacity-50"
          >
            {gitAuthorSaving ? '保存中...' : '保存 Git 作者'}
          </button>
        </div>
        {gitAuthorMessage && (
          <p className={`text-xs ${gitAuthorMessage.includes('失败') || gitAuthorMessage.includes('必须') || gitAuthorMessage.includes('有效') ? 'text-red-500' : 'text-emerald-600'}`}>
            {gitAuthorMessage}
          </p>
        )}
      </div>

      <div className="mt-6 space-y-2">
        <label className="text-xs font-medium text-muted-foreground flex items-center gap-1.5">
          <BellRing className="w-3.5 h-3.5" />
          更新检查
        </label>
        <div className="space-y-2 rounded-md border border-border/70 p-3">
          <label className="flex items-center gap-2 text-xs text-foreground">
            <input
              type="checkbox"
              checked={updateEnabled}
              onChange={e => setUpdateEnabled(e.target.checked)}
              className="h-4 w-4"
            />
            启用启动后自动检查
          </label>
          <div className="space-y-1.5">
            <label htmlFor="update-check-endpoint" className="text-[11px] text-muted-foreground">Release endpoint</label>
            <input
              id="update-check-endpoint"
              value={updateEndpoint}
              onChange={e => setUpdateEndpoint(e.target.value)}
              className="h-8 w-full rounded-md border bg-background px-2 text-xs font-mono focus:outline-none focus:ring-2 focus:ring-ring"
              placeholder="https://example.test/novelist/releases/latest"
            />
          </div>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="text-[11px] text-muted-foreground">
              {updateDismissedVersion ? `已忽略版本：${updateDismissedVersion}` : '自动检查不会阻塞启动；打开发布页需要手动点击。'}
            </p>
            <div className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => void handleSaveUpdateSettings()}
                disabled={updateSaving}
                className="inline-flex h-8 items-center gap-1.5 rounded-md border px-3 text-xs transition-colors hover:bg-muted disabled:opacity-50"
              >
                {updateSaving ? '保存中...' : '保存更新设置'}
              </button>
              <button
                type="button"
                onClick={() => void handleManualUpdateCheck()}
                disabled={updateChecking}
                className="inline-flex h-8 items-center gap-1.5 rounded-md border px-3 text-xs transition-colors hover:bg-muted disabled:opacity-50"
              >
                <RefreshCw className={`w-3.5 h-3.5 ${updateChecking ? 'animate-spin' : ''}`} />
                {updateChecking ? '检查中...' : '立即检查'}
              </button>
            </div>
          </div>
          {updateMessage && (
            <p className={`text-xs ${updateMessage.includes('失败') || updateMessage.includes('必须') || updateMessage.includes('HTTPS') ? 'text-red-500' : 'text-emerald-600'}`}>
              {updateMessage}
            </p>
          )}
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
      <UpdateDialog
        open={showUpdateDialog}
        result={updateResult}
        onClose={() => setShowUpdateDialog(false)}
        onDismissVersion={handleDismissUpdateVersion}
      />
    </div>
  )
}

function isValidGitEmail(email: string) {
  return email.length > 2 &&
    email.length <= 320 &&
    !/\s/.test(email) &&
    email.indexOf('@') > 0 &&
    email.lastIndexOf('@') === email.indexOf('@') &&
    email.indexOf('@') < email.length - 1
}

function errorText(error: unknown, fallback: string) {
  if (error instanceof Error) return error.message
  if (typeof error === 'string') return error
  return fallback
}

function isHttpsUrl(value: string) {
  try {
    return new URL(value).protocol === 'https:'
  } catch {
    return false
  }
}
