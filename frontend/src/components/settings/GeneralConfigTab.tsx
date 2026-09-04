import { useState, useEffect } from 'react'
import { BellRing, Folder, GitCommitHorizontal, RefreshCw } from 'lucide-react'
import ErrorCallout from '@/components/shared/ErrorCallout'
import { useApp, type novel } from '@/hooks/useApp'
import { EventsOn } from '@/lib/novelist/events'
import { buildCopyableDiagnostic, diagnosticMessage } from '@/lib/diagnostics'
import type { diagnostics, update } from '@/lib/novelist/types'
import UpdateDialog from '@/components/update/UpdateDialog'

type UpdateFeedback =
  | { kind: 'success'; message: string }
  | {
    kind: 'error'
    title: string
    message: string
    diagnostic: diagnostics.CopyableDiagnostic | null
  }

type InlineSettingsFeedback =
  | { kind: 'success' | 'validation'; message: string }
  | {
    kind: 'error'
    title: string
    message: string
    diagnostic: diagnostics.CopyableDiagnostic | null
  }

export default function GeneralConfigTab({ onBusyChange }: { onBusyChange?: (busy: boolean) => void }) {
  const app = useApp()
  const [dataDir, setDataDir] = useState('')
  const [novels, setNovels] = useState<novel.Novel[]>([])
  const [selectedID, setSelectedID] = useState<number>(0)
  const [rebuilding, setRebuilding] = useState(false)
  const [gitAuthorName, setGitAuthorName] = useState('')
  const [gitAuthorEmail, setGitAuthorEmail] = useState('')
  const [gitAuthorSaving, setGitAuthorSaving] = useState(false)
  const [gitAuthorFeedback, setGitAuthorFeedback] = useState<InlineSettingsFeedback | null>(null)
  const [rebuildFeedback, setRebuildFeedback] = useState<InlineSettingsFeedback | null>(null)
  const [migratingInputVisible, setMigratingInputVisible] = useState(false)
  const [migrating, setMigrating] = useState(false)
  // 残余 2：复制进行中的进度（后端 datadir:migration:progress 事件驱动）。
  const [migrationProgress, setMigrationProgress] = useState<{ copied_files: number; total_files: number } | null>(null)
  const [newDataDir, setNewDataDir] = useState('')
  const [migrateFeedback, setMigrateFeedback] = useState<InlineSettingsFeedback | null>(null)
  const [updateEnabled, setUpdateEnabled] = useState(false)
  const [updateEndpoint, setUpdateEndpoint] = useState('')
  const [updateDismissedVersion, setUpdateDismissedVersion] = useState('')
  const [updateSaving, setUpdateSaving] = useState(false)
  const [updateChecking, setUpdateChecking] = useState(false)
  const [updateFeedback, setUpdateFeedback] = useState<UpdateFeedback | null>(null)
  const [updateResult, setUpdateResult] = useState<update.UpdateCheckResult | null>(null)
  const [showUpdateDialog, setShowUpdateDialog] = useState(false)

  const [loadFailed, setLoadFailed] = useState(false)

  useEffect(() => {
    // 初始加载失败不再静默：设置页显示空白会误导作者以为配置丢了。
    app.GetAppConfig().then(cfg => {
      setDataDir(cfg?.data_dir || '')
      if (cfg?.update_check?.endpoint_url) {
        setUpdateEndpoint(prev => prev || cfg.update_check.endpoint_url)
      }
    }).catch(() => setLoadFailed(true))
    app.GetNovels().then(list => {
      setNovels(list || [])
    }).catch(() => setLoadFailed(true))
    app.GetSettings().then(s => {
      if (s?.last_novel_id) setSelectedID(s.last_novel_id)
    }).catch(() => setLoadFailed(true))
    app.GetGitAuthorSettings().then(settings => {
      setGitAuthorName(settings?.name || '')
      setGitAuthorEmail(settings?.email || '')
    }).catch((err) => {
      setGitAuthorError(err, 'Git 作者设置加载失败', 'GetGitAuthorSettings', {
        phase: 'load_git_author_settings',
      })
    })
    app.GetUpdateCheckSettings().then(settings => {
      setUpdateEnabled(settings?.enabled === true)
      setUpdateEndpoint(settings?.endpoint_url || '')
      setUpdateDismissedVersion(settings?.dismissed_version || '')
    }).catch((err) => {
      setUpdateError(err, '更新检查设置加载失败', 'GetUpdateCheckSettings', 'GetUpdateCheckSettings', {
        phase: 'load_update_settings',
      })
    })
  }, [app])

  useEffect(() => {
    // 迁移进行中要向上报告 busy：SettingsDialog 据此阻止 Escape/遮罩/✕ 关闭（U13）。
    onBusyChange?.(migrating)
  }, [migrating, onBusyChange])

  // 常驻订阅：进度事件可能在 migrating 状态渲染完成前就已发出（invoke 立即开始），
  // 迁移期间才订阅会漏掉开头的事件；进度值只在迁移 UI 中展示。
  useEffect(() => {
    const unsub = EventsOn('datadir:migration:progress', (data: { copied_files?: number; total_files?: number }) => {
      setMigrationProgress({ copied_files: data?.copied_files ?? 0, total_files: data?.total_files ?? 0 })
    })
    return () => unsub()
  }, [])

  async function handleMigrateDataDir() {
    const target = newDataDir.trim()
    if (!target || target === dataDir) return
    // 最后确认：迁移会切换整个工作区数据源，作者必须明确知道即将发生什么（F10）。
    if (!window.confirm(`确定要把数据目录迁移到：\n${target}\n\n复制完成前不会改动原目录；迁移期间请勿关闭应用。`)) return
    setMigrating(true)
    setMigrationProgress(null)
    setMigrateFeedback(null)
    try {
      const result = await app.UpdateDataDir(target)
      const cfg = await app.GetAppConfig().catch(() => null)
      if (cfg?.data_dir) setDataDir(cfg.data_dir)
      setMigratingInputVisible(false)
      setNewDataDir('')
      // 如实呈现复制结果（U13）：copy-first 完成后才切换指针，原目录保持原样。
      const copied = result?.copied_files ?? 0
      const warnings = result?.warnings ?? 0
      const skipped = result?.skipped_files ?? 0
      setMigrateFeedback({
        kind: 'success',
        message: `数据目录迁移完成：已复制 ${copied} 个文件` +
          (warnings > 0
            ? `；${warnings} 个目标已有、内容不同的文件被跳过（未覆盖，详见迁移清单）。`
            : skipped > 0 ? `（${skipped} 个已存在的相同文件跳过）` : '') +
          `应用已切换到新目录。原目录未做任何改动，确认无误后可自行备份清理。` +
          (result?.manifest_path ? `迁移清单：${result.manifest_path}` : ''),
      })
      // 残余 1：迁移后仍打开的旧 UI（作品列表、会话）指向已切换的数据源——
      // 稍候片刻让作者看到结果，再整页刷新以新目录重新初始化。
      window.setTimeout(() => { window.location.reload() }, 1200)
    } catch (err) {
      console.error('Data dir migration failed:', err)
      setMigrateFeedback({
        kind: 'error',
        title: '数据目录迁移失败',
        message: diagnosticMessage(err, '迁移未能完成，原数据目录未受影响，可重试或更换目标路径。'),
        diagnostic: buildCopyableDiagnostic({
          error: err,
          fallbackMessage: '数据目录迁移失败',
          operation: 'GeneralConfigTab.UpdateDataDir',
          bridgeMethod: 'UpdateDataDir',
        }),
      })
    } finally {
      setMigrating(false)
      setMigrationProgress(null)
    }
  }

  async function handleRebuild() {
    if (!selectedID) return
    setRebuilding(true)
    setRebuildFeedback(null)
    try {
      await app.RebuildNovelIndex(selectedID)
      setRebuildFeedback({ kind: 'success', message: '向量索引重建完成' })
    } catch (err) {
      console.error('Rebuild failed:', err)
      // 重建失败必须有作者可见的反馈，不能只留在控制台（F11）。
      setRebuildFeedback({
        kind: 'error',
        title: '向量索引重建失败',
        message: diagnosticMessage(err, '重建未能完成，请稍后重试。'),
        diagnostic: buildCopyableDiagnostic({
          error: err,
          fallbackMessage: '向量索引重建失败',
          operation: 'GeneralConfigTab.RebuildNovelIndex',
          bridgeMethod: 'RebuildNovelIndex',
        }),
      })
    } finally {
      setRebuilding(false)
    }
  }

  async function handleSaveGitAuthor() {
    const name = gitAuthorName.trim()
    const email = gitAuthorEmail.trim()

    if ((name && !email) || (!name && email)) {
      setGitAuthorFeedback({ kind: 'validation', message: 'Git 作者名称和邮箱必须同时填写' })
      return
    }

    if (email && !isValidGitEmail(email)) {
      setGitAuthorFeedback({ kind: 'validation', message: '请输入有效的 Git 作者邮箱' })
      return
    }

    setGitAuthorSaving(true)
    setGitAuthorFeedback(null)
    try {
      const saved = await app.SaveGitAuthorSettings({ name, email })
      setGitAuthorName(saved.name)
      setGitAuthorEmail(saved.email)
      const message = saved.name ? 'Git 作者设置已保存' : 'Git 作者设置已清空，将使用默认身份'
      setGitAuthorFeedback({ kind: 'success', message })
      window.setTimeout(() => {
        setGitAuthorFeedback(current => current?.message === message ? null : current)
      }, 2400)
    } catch (err) {
      setGitAuthorError(err, 'Git 作者设置保存失败', 'SaveGitAuthorSettings', {
        phase: 'save_git_author_settings',
        name_present: name.length > 0,
        email_present: email.length > 0,
        email_domain: emailDomain(email),
      })
    } finally {
      setGitAuthorSaving(false)
    }
  }

  async function handleSaveUpdateSettings(nextDismissedVersion = updateDismissedVersion) {
    const endpoint = updateEndpoint.trim()
    if (updateEnabled && !endpoint) {
      setUpdateValidationError('启用更新检查时必须填写更新检查地址（HTTPS）', endpoint)
      return
    }

    if (endpoint && !isHttpsUrl(endpoint)) {
      setUpdateValidationError('更新检查地址必须是 HTTPS', endpoint)
      return
    }

    setUpdateSaving(true)
    clearUpdateFeedback()
    try {
      const saved = await app.SaveUpdateCheckSettings({
        enabled: updateEnabled,
        endpoint_url: endpoint,
        dismissed_version: nextDismissedVersion.trim(),
      })
      setUpdateEnabled(saved.enabled)
      setUpdateEndpoint(saved.endpoint_url)
      setUpdateDismissedVersion(saved.dismissed_version)
      setUpdateSuccess(saved.enabled ? '更新检查设置已保存' : '更新检查已关闭')
    } catch (err) {
      setUpdateError(err, '更新检查设置保存失败', 'SaveUpdateCheckSettings', 'SaveUpdateCheckSettings', {
        phase: 'save_update_settings',
        enabled: updateEnabled,
        dismissed_version_present: nextDismissedVersion.trim().length > 0,
        endpoint_host: endpointHost(endpoint),
      })
    } finally {
      setUpdateSaving(false)
    }
  }

  async function handleManualUpdateCheck() {
    const endpoint = updateEndpoint.trim()
    if (!endpoint) {
      setUpdateValidationError('请先填写更新检查地址', endpoint)
      return
    }

    if (!isHttpsUrl(endpoint)) {
      setUpdateValidationError('更新检查地址必须是 HTTPS', endpoint)
      return
    }

    setUpdateChecking(true)
    clearUpdateFeedback()
    try {
      await app.SaveUpdateCheckSettings({
        enabled: updateEnabled,
        endpoint_url: endpoint,
        dismissed_version: updateDismissedVersion,
      })
      const taskId = `update-manual-${Date.now().toString(36)}`
      const result = await app.CheckForUpdates({
        task_id: taskId,
        manual: true,
      })
      setUpdateResult(result)
      if (result.status === 'update_available') {
        setShowUpdateDialog(true)
        setUpdateSuccess(`发现新版本 ${result.latest_version || ''}`.trim())
      } else if (result.status === 'no_update') {
        setUpdateSuccess('当前已是最新版本')
      } else if (result.status === 'failed') {
        setUpdateError(result.error_message || '更新检查失败', '更新检查失败', 'CheckForUpdates', 'CheckForUpdates', {
          phase: 'manual_update_check',
          result,
          endpoint_host: endpointHost(endpoint),
        }, result.task_id || taskId)
      } else {
        setUpdateSuccess('更新检查已完成')
      }
    } catch (err) {
      setUpdateError(err, '更新检查失败', 'CheckForUpdates', 'CheckForUpdates', {
        phase: 'manual_update_check',
        enabled: updateEnabled,
        dismissed_version_present: updateDismissedVersion.trim().length > 0,
        endpoint_host: endpointHost(endpoint),
      })
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
    setUpdateSuccess(`已忽略版本 ${version}`)
  }

  function clearUpdateFeedback() {
    setUpdateFeedback(null)
  }

  function setUpdateSuccess(message: string) {
    setUpdateFeedback({ kind: 'success', message })
  }

  function setGitAuthorError(
    errorValue: unknown,
    fallbackMessage: string,
    bridgeMethod: string,
    detail: Record<string, unknown>,
  ) {
    setGitAuthorFeedback({
      kind: 'error',
      title: fallbackMessage,
      message: diagnosticMessage(errorValue, fallbackMessage),
      diagnostic: buildCopyableDiagnostic({
        error: errorValue,
        fallbackMessage,
        operation: bridgeMethod,
        bridgeMethod,
        detail,
      }),
    })
  }

  function setUpdateValidationError(message: string, endpoint: string) {
    setUpdateError(message, '更新检查设置无效', 'UpdateCheckSettingsValidation', null, {
      phase: 'validate_update_settings',
      enabled: updateEnabled,
      endpoint_present: endpoint.trim().length > 0,
      endpoint_protocol: endpointProtocol(endpoint),
    })
  }

  function setUpdateError(
    errorValue: unknown,
    fallbackMessage: string,
    operation: string,
    bridgeMethod: string | null,
    detail: Record<string, unknown>,
    taskId?: string | null,
  ) {
    setUpdateFeedback({
      kind: 'error',
      title: fallbackMessage,
      message: diagnosticMessage(errorValue, fallbackMessage),
      diagnostic: buildCopyableDiagnostic({
        error: errorValue,
        fallbackMessage,
        operation,
        bridgeMethod,
        taskId,
        detail,
      }),
    })
  }

  return (
    <div className="flex-1 flex flex-col">
      <h3 className="text-sm font-medium mb-5">基础配置</h3>

      {loadFailed && (
        <div className="mb-4 rounded-md border border-destructive/30 bg-destructive/5 px-3 py-2 text-xs text-destructive" role="alert">
          部分配置加载失败，下方显示的可能是默认值。请关闭后重新打开设置，或重启应用后重试。
        </div>
      )}

      <div className="space-y-2">
        <label className="text-xs font-medium text-muted-foreground flex items-center gap-1.5">
          <Folder className="w-3.5 h-3.5" />
          数据目录
        </label>
        <div className="flex items-center gap-2">
          <input
            value={dataDir}
            readOnly
            aria-label="当前数据目录"
            className="flex-1 h-8 rounded-md border bg-muted/50 px-3 text-xs font-mono focus:outline-none cursor-default"
          />
          <button
            type="button"
            onClick={() => { setNewDataDir(''); setMigratingInputVisible(true); setMigrateFeedback(null) }}
            className="h-8 shrink-0 rounded-md border border-border px-2.5 text-xs font-medium text-foreground hover:bg-secondary"
            data-testid="start-data-dir-migration"
          >
            更改…
          </button>
        </div>
        {migrateFeedback && (
          migrateFeedback.kind === 'success' ? (
            <p className="text-[11px] text-emerald-600" role="status">{migrateFeedback.message}</p>
          ) : (
            <ErrorCallout
              title={migrateFeedback.kind === 'error' ? migrateFeedback.title : undefined}
              message={migrateFeedback.message}
              diagnostic={migrateFeedback.kind === 'error' ? migrateFeedback.diagnostic : null}
              className="rounded-md"
              onClose={() => setMigrateFeedback(null)}
            />
          )
        )}
        {migratingInputVisible && (
          <div className="rounded-md border border-border bg-muted/20 p-3 space-y-2" data-testid="migrate-data-dir-form">
            <p className="text-[11px] leading-4 text-muted-foreground">
              迁移会把当前数据目录完整复制到新位置（copy-first：复制完成并校验前不动原目录）。
              大目录可能耗时较长，迁移期间请勿关闭应用。
              {migrationProgress && (
                <span className="ml-1 font-medium text-foreground" data-testid="migration-progress">
                  已复制 {migrationProgress.copied_files} / {migrationProgress.total_files} 个文件。
                </span>
              )}
            </p>
            <div className="flex items-center gap-2">
              <input
                value={newDataDir}
                onChange={event => setNewDataDir(event.target.value)}
                placeholder="输入新的数据目录绝对路径，例如 D:\\NovelistData"
                aria-label="新的数据目录"
                className="flex-1 h-8 rounded-md border bg-background px-2.5 text-xs font-mono focus:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              />
              <button
                type="button"
                onClick={() => { void handleMigrateDataDir() }}
                disabled={migrating || !newDataDir.trim() || newDataDir.trim() === dataDir}
                className="h-8 shrink-0 rounded-md bg-primary px-3 text-xs font-medium text-primary-foreground hover:bg-primary/90 disabled:cursor-not-allowed disabled:opacity-50"
              >
                {migrating ? '迁移中…' : '开始迁移'}
              </button>
              <button
                type="button"
                onClick={() => { setMigratingInputVisible(false); setNewDataDir('') }}
                disabled={migrating}
                className="h-8 shrink-0 rounded-md border border-border px-2.5 text-xs text-muted-foreground hover:bg-secondary disabled:opacity-50"
              >
                取消
              </button>
            </div>
          </div>
        )}
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
          <p className="text-[11px] text-muted-foreground">留空时使用安全默认身份；保存后会在下一次仓库初始化、小说导入提交或普通保存提交前写入 repo-local Git config。</p>
          <button
            type="button"
            onClick={handleSaveGitAuthor}
            disabled={gitAuthorSaving}
            className="inline-flex h-8 items-center gap-1.5 rounded-md border px-3 text-xs transition-colors hover:bg-muted disabled:opacity-50"
          >
            {gitAuthorSaving ? '保存中...' : '保存 Git 作者'}
          </button>
        </div>
        {gitAuthorFeedback && gitAuthorFeedback.kind !== 'error' && (
          <p className={`text-xs ${gitAuthorFeedback.kind === 'success' ? 'text-emerald-600' : 'text-red-500'}`}>
            {gitAuthorFeedback.message}
          </p>
        )}
        {gitAuthorFeedback?.kind === 'error' && (
          <ErrorCallout
            compact
            title={gitAuthorFeedback.title}
            message={gitAuthorFeedback.message}
            diagnostic={gitAuthorFeedback.diagnostic}
            className="rounded-md"
            onClose={() => setGitAuthorFeedback(null)}
          />
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
            <label htmlFor="update-check-endpoint" className="text-[11px] text-muted-foreground">更新检查地址（HTTPS）</label>
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
              {updateDismissedVersion ? `已忽略版本：${updateDismissedVersion}` : '自动检查默认关闭且不会阻塞启动；打开发布页需要手动点击。'}
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
          {updateFeedback?.kind === 'success' && (
            <p role="status" className="text-xs text-emerald-600">
              {updateFeedback.message}
            </p>
          )}
          {updateFeedback?.kind === 'error' && (
            <ErrorCallout
              compact
              title={updateFeedback.title}
              message={updateFeedback.message}
              diagnostic={updateFeedback.diagnostic}
              className="rounded-md"
              onClose={clearUpdateFeedback}
            />
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
        {rebuildFeedback && (
          rebuildFeedback.kind === 'success' ? (
            <p className="text-[11px] text-emerald-600" role="status">{rebuildFeedback.message}</p>
          ) : (
            <ErrorCallout
              title={rebuildFeedback.kind === 'error' ? rebuildFeedback.title : undefined}
              message={rebuildFeedback.message}
              diagnostic={rebuildFeedback.kind === 'error' ? rebuildFeedback.diagnostic : null}
              className="rounded-md"
              onClose={() => setRebuildFeedback(null)}
            />
          )
        )}
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

function isHttpsUrl(value: string) {
  try {
    return new URL(value).protocol === 'https:'
  } catch {
    return false
  }
}

function endpointHost(value: string) {
  try {
    return new URL(value).host
  } catch {
    return ''
  }
}

function endpointProtocol(value: string) {
  try {
    return new URL(value).protocol
  } catch {
    return ''
  }
}

function emailDomain(value: string) {
  const at = value.lastIndexOf('@')
  if (at <= 0 || at >= value.length - 1) return ''
  return value.slice(at + 1)
}
