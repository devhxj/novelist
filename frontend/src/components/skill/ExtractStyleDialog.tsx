import { useState, useEffect, useCallback } from 'react'
import { Sparkle, Loader2 } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import type { llm } from '@/hooks/useApp'
import PopSelect from '@/components/chat/PopSelect'
import Markdown from '@/components/Markdown'
import { splitFrontmatter } from '@/components/content/types'

interface Props {
  open: boolean
  novelId: number
  onClose: () => void
  onSaved: () => void
}

type Phase = 'input' | 'extracting' | 'preview' | 'saving'

function errorMessage(error: unknown, fallback: string): string {
  return error instanceof Error ? error.message : fallback
}

export default function ExtractStyleDialog({ open, novelId, onClose, onSaved }: Props) {
  const app = useApp()

  const [models, setModels] = useState<llm.AvailableModel[]>([])
  const [selectedKey, setSelectedKey] = useState('')
  const [sample, setSample] = useState('')
  const [phase, setPhase] = useState<Phase>('input')
  const [result, setResult] = useState<{ name: string; filePath: string; rawContent: string } | null>(null)
  const [error, setError] = useState('')

  // 加载模型列表并同步当前聊天模型
  useEffect(() => {
    if (!open) return
    let cancelled = false
    void (async () => {
      await Promise.resolve()
      if (cancelled) return
      setSample('')
      setPhase('input')
      setResult(null)
      setError('')

      try {
        const [modelList, settings] = await Promise.all([
          app.GetModels(),
          app.GetSettings(),
        ])
        if (!cancelled && modelList && modelList.length > 0) {
          setModels(modelList)
          let key = settings?.selected_model_key || ''
          if (!modelList.find(m => m.Key === key)) {
            key = modelList[0].Key
          }
          setSelectedKey(key)
        }
      } catch (err) {
        console.error('Load models for extract style failed', err)
      }
    })()
    return () => { cancelled = true }
  }, [app, open])

  const selected = models.find(m => m.Key === selectedKey)

  const handleExtract = useCallback(async () => {
    if (!sample.trim() || !selectedKey) return
    const [providerName, modelID] = selectedKey.split('/')
    if (!providerName || !modelID) return

    setPhase('extracting')
    setError('')

    const reasoningEffort = selected?.ReasoningLevels?.[0] || ''

    try {
      const res = await app.ExtractStyle({
        novel_id: novelId,
        sample: sample.trim(),
        provider_name: providerName,
        model_id: modelID,
        reasoning_effort: reasoningEffort,
      })
      setResult({
        name: res.name,
        filePath: res.file_path,
        rawContent: res.raw_content,
      })
      setPhase('preview')
    } catch (e: unknown) {
      setError(errorMessage(e, '提取失败，请重试'))
      setPhase('input')
    }
  }, [sample, selectedKey, selected, novelId, app])

  const handleSave = useCallback(async () => {
    if (!result) return
    setPhase('saving')
    setError('')
    try {
      await app.SaveContent({
        novel_id: novelId,
        path: result.filePath,
        content: result.rawContent,
      })
      onSaved()
      onClose()
    } catch (e: unknown) {
      setError(errorMessage(e, '保存失败，请重试'))
      setPhase('preview')
    }
  }, [result, novelId, app, onSaved, onClose])

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Escape' && phase !== 'extracting' && phase !== 'saving') {
      onClose()
    }
  }

  if (!open) return null

  const modelOptions = models.map(m => ({ value: m.Key, label: m.ModelName }))
  const canExtract = sample.trim().length > 0 && selectedKey && phase !== 'extracting'

  const { meta, body } = result ? splitFrontmatter(result.rawContent) : { meta: {}, body: '' }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" onKeyDown={handleKeyDown}>
      <div className="absolute inset-0 bg-black/40" onClick={phase === 'extracting' || phase === 'saving' ? undefined : onClose} />
      <div className="relative bg-background rounded-xl shadow-2xl border w-[860px] max-w-[95vw] h-[88vh] max-h-[96vh] flex flex-col">
        {/* 标题栏 */}
        <div className="flex items-center justify-between px-5 py-3.5 border-b shrink-0">
          <div className="flex items-center gap-2">
            <Sparkle className="w-4 h-4 text-action-extract" />
            <h2 className="text-sm font-semibold">提取写作风格</h2>
          </div>
          <button
            onClick={onClose}
            disabled={phase === 'extracting' || phase === 'saving'}
            className="w-7 h-7 flex items-center justify-center rounded-md text-muted-foreground hover:text-foreground hover:bg-muted transition-colors disabled:opacity-50"
          >
            ✕
          </button>
        </div>

        {/* 错误提示 */}
        {error && (
          <div className="mx-5 mt-3 px-3 py-2 text-xs text-red-600 bg-danger-bg border border-danger-border rounded-md shrink-0">
            {error}
          </div>
        )}

        {/* 模型选择 + 输入区（shrink-0，不裁剪 PopSelect dropdown） */}
        {(phase === 'input' || phase === 'extracting') && (
          <div className="flex-1 min-h-0 px-5 pt-4 flex flex-col gap-3">
            <div className="flex items-center gap-2 shrink-0">
              <span className="text-xs text-muted-foreground">分析模型</span>
              <PopSelect
                value={selectedKey}
                options={modelOptions}
                onChange={setSelectedKey}
                minWidth="160px"
              />
            </div>
            <textarea
              value={sample}
              onChange={e => setSample(e.target.value)}
              placeholder="粘贴要模仿的文字样本..."
              disabled={phase === 'extracting'}
              className="w-full flex-1 min-h-[200px] rounded-lg border bg-background px-3.5 py-3 text-sm resize-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60"
            />
            <div className="flex justify-end pb-4 shrink-0">
              <button
                onClick={handleExtract}
                disabled={!canExtract}
                className="inline-flex items-center gap-1.5 h-9 px-5 rounded-lg text-sm font-medium bg-action-extract text-action-extract-foreground hover:bg-action-extract/80 transition-colors disabled:opacity-40 shadow-sm"
              >
                {phase === 'extracting' ? (
                  <>
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                    分析中...
                  </>
                ) : (
                  <>
                    <Sparkle className="w-3.5 h-3.5" />
                    开始分析
                  </>
                )}
              </button>
            </div>
          </div>
        )}

        {/* 预览区（可滚动） */}
        {(phase === 'preview' || phase === 'saving') && (
          <div className="flex-1 overflow-y-auto px-5 py-4 space-y-3">
            {Object.keys(meta).length > 0 && (
              <table className="border bg-muted/20 w-full text-sm rounded-lg overflow-hidden">
                <tbody>
                  {meta.name && (
                    <tr className="border-b">
                      <td className="px-4 py-2 text-muted-foreground whitespace-nowrap w-16">名称</td>
                      <td className="px-4 py-2 text-foreground font-semibold">{meta.name}</td>
                    </tr>
                  )}
                  {meta.description && (
                    <tr className="border-b">
                      <td className="px-4 py-2 text-muted-foreground whitespace-nowrap w-16">简介</td>
                      <td className="px-4 py-2 text-foreground">{meta.description}</td>
                    </tr>
                  )}
                  {meta.category && (
                    <tr>
                      <td className="px-4 py-2 text-muted-foreground whitespace-nowrap w-16">分类</td>
                      <td className="px-4 py-2 text-foreground">{meta.category}</td>
                    </tr>
                  )}
                </tbody>
              </table>
            )}
            <div className="rounded-lg border bg-muted/10 p-4">
              <Markdown content={body} />
            </div>
          </div>
        )}

        {/* 底部按钮栏（仅预览态） */}
        {(phase === 'preview' || phase === 'saving') && (
          <div className="flex justify-end gap-2 px-5 py-3.5 border-t shrink-0">
            <button
              onClick={onClose}
              disabled={phase === 'saving'}
              className="h-9 px-4 rounded-md text-sm border hover:bg-muted transition-colors disabled:opacity-50"
            >
              取消
            </button>
            <button
              onClick={handleSave}
              disabled={phase === 'saving'}
              className="inline-flex items-center gap-1.5 h-9 px-5 rounded-md text-sm font-medium bg-action-save text-action-save-foreground hover:bg-action-save/80 transition-colors disabled:opacity-50"
            >
              {phase === 'saving' ? (
                <>
                  <Loader2 className="w-3.5 h-3.5 animate-spin" />
                  保存中...
                </>
              ) : (
                '保存技能'
              )}
            </button>
          </div>
        )}
      </div>
    </div>
  )
}
