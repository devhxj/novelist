import type { llm } from '@/hooks/useApp'
import PopSelect from './PopSelect'
import ContextRing from './ContextRing'
import type { UsageInfo } from './ContextRing'

interface Props {
  models: llm.AvailableModel[]
  selectedKey: string
  onSelectModel: (key: string) => void
  reasoningEffort: string
  onSelectEffort: (effort: string) => void
  approvalMode: 'manual' | 'auto'
  onToggleApproval: () => void
  usage: UsageInfo | null
}

export default function ChatControls({
  models,
  selectedKey,
  onSelectModel,
  reasoningEffort,
  onSelectEffort,
  approvalMode,
  onToggleApproval,
  usage,
}: Props) {
  const selected = models.find(m => m.Key === selectedKey)
  const supportsReasoning = selected?.ReasoningLevels && selected.ReasoningLevels.length > 0

  const modelOptions = models.map(m => ({ value: m.Key, label: m.ModelName }))
  const reasoningOptions = supportsReasoning
    ? selected.ReasoningLevels.map(level => ({
        value: level,
        label: level === 'high' ? '高推理' : '最大推理',
      }))
    : []

  return (
    <div className="flex items-center gap-1.5 px-4 py-2 text-xs shrink-0">
      <PopSelect
        value={selectedKey}
        options={modelOptions}
        onChange={onSelectModel}
      />

      {supportsReasoning && (
        <PopSelect
          value={reasoningEffort}
          options={reasoningOptions}
          onChange={onSelectEffort}
          minWidth="80px"
        />
      )}

      <button
        onClick={onToggleApproval}
        className={`h-[30px] rounded-lg border px-2.5 text-xs transition-colors shrink-0 ${
          approvalMode === 'auto'
            ? 'bg-primary/10 text-primary border-primary/30'
            : 'bg-background text-muted-foreground'
        }`}
      >
        {approvalMode === 'auto' ? '自动' : '手动'}
      </button>

      <div className="flex-1" />

      <ContextRing usage={usage} />
    </div>
  )
}
