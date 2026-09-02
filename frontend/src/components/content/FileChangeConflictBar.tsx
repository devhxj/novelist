import { AlertTriangle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import type { EditorTabConflict } from './types'

interface Props {
  conflict: EditorTabConflict
  onKeepMine: () => void
  onTakeIncoming: () => void
  onViewDiff: () => void
}

export default function FileChangeConflictBar({ conflict, onKeepMine, onTakeIncoming, onViewDiff }: Props) {
  const scope = conflict.target === 'content' ? '正文' : '大纲'

  return (
    <section
      role="alert"
      aria-label="外部改动冲突"
      className="file-change-conflict-bar shrink-0 px-4 py-2 text-xs"
    >
      <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-2">
        <div className="flex min-w-0 flex-1 items-start gap-2">
          <AlertTriangle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-600 dark:text-amber-400" />
          <div className="min-w-0">
            <div className="font-medium">{scope}被外部改动</div>
            <div className="mt-0.5 break-words text-muted-foreground">
              你还有未保存的修改，已暂存对方版本，未覆盖你的内容。
            </div>
          </div>
        </div>
        <div className="flex shrink-0 flex-wrap items-center gap-2">
          <Button type="button" variant="outline" size="xs" onClick={onKeepMine}>
            保留我的
          </Button>
          <Button type="button" variant="outline" size="xs" onClick={onTakeIncoming}>
            用 AI 版本
          </Button>
          <Button type="button" variant="ghost" size="xs" onClick={onViewDiff}>
            查看差异
          </Button>
        </div>
      </div>
    </section>
  )
}
