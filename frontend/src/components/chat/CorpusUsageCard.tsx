import { useState } from 'react'
import { BookMarked, ThumbsDown, ThumbsUp } from 'lucide-react'
import { useApp } from '@/hooks/useApp'

export interface CorpusUsageMaterial {
  material_id: string
  anchor_id: number
  anchor_title: string
  text_preview: string
  tags: string[]
}

// 语料使用反馈：作者对单条注入语料表态，写入用户反馈库驱动积累质量信号（accepted/rejected）。
export default function CorpusUsageCard({ materials, novelId }: { materials: CorpusUsageMaterial[]; novelId: number }) {
  const app = useApp()
  const [voted, setVoted] = useState<Record<string, 'accepted' | 'rejected'>>({})
  if (materials.length === 0) return null

  const recordFeedback = async (material: CorpusUsageMaterial, decision: 'accepted' | 'rejected') => {
    if (voted[material.material_id]) return
    setVoted((current) => ({ ...current, [material.material_id]: decision }))
    try {
      await app.RecordReferenceUserFeedback({
        novel_id: novelId,
        target_type: 'material',
        target_id: material.material_id,
        decision,
        material_id: material.material_id,
        candidate_id: '',
        blueprint_id: 0,
        beat_id: '',
        feedback_tags: ['corpus_injection'],
        note: '',
        edited_text: '',
        origin: 'chat_usage_card',
      })
    } catch {
      // 反馈失败静默回退按钮状态，不打断写作流。
      setVoted((current) => {
        const next = { ...current }
        delete next[material.material_id]
        return next
      })
    }
  }

  return (
    <div className="rounded-md border border-border bg-muted/30 px-2.5 py-2" data-testid="corpus-usage-card">
      <div className="flex items-center gap-1.5 text-xs font-medium text-foreground">
        <BookMarked className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        本章语料注入 {materials.length} 条
      </div>
      <ul className="mt-1.5 space-y-1.5">
        {materials.map((material) => (
          <li key={material.material_id} className="rounded border border-border bg-background px-2 py-1.5">
            <div className="flex items-center justify-between gap-2">
              <div className="min-w-0 text-[11px] font-medium text-foreground">{`《${material.anchor_title}》`}</div>
              <div className="flex shrink-0 items-center gap-1" role="group" aria-label={`对《${material.anchor_title}》的语料表态`}>
                <button
                  type="button"
                  onClick={() => { void recordFeedback(material, 'accepted') }}
                  aria-pressed={voted[material.material_id] === 'accepted'}
                  title="这条语料帮到了我"
                  className={`inline-flex h-5 w-5 items-center justify-center rounded transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${voted[material.material_id] === 'accepted' ? 'bg-emerald-600 text-white' : 'text-muted-foreground hover:bg-muted hover:text-foreground'}`}
                  data-testid={`feedback-up-${material.material_id}`}
                >
                  <ThumbsUp className="h-3 w-3" aria-hidden="true" />
                </button>
                <button
                  type="button"
                  onClick={() => { void recordFeedback(material, 'rejected') }}
                  aria-pressed={voted[material.material_id] === 'rejected'}
                  title="这条语料没有帮助"
                  className={`inline-flex h-5 w-5 items-center justify-center rounded transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${voted[material.material_id] === 'rejected' ? 'bg-destructive text-white' : 'text-muted-foreground hover:bg-muted hover:text-foreground'}`}
                  data-testid={`feedback-down-${material.material_id}`}
                >
                  <ThumbsDown className="h-3 w-3" aria-hidden="true" />
                </button>
              </div>
            </div>
            {material.tags.length > 0 && (
              <div className="mt-0.5 truncate text-[10px] text-muted-foreground">{material.tags.join(' · ')}</div>
            )}
            <p className="mt-0.5 line-clamp-2 text-[11px] leading-relaxed text-muted-foreground">{material.text_preview}</p>
          </li>
        ))}
      </ul>
    </div>
  )
}
