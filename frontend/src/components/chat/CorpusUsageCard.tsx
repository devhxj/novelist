import { BookMarked } from 'lucide-react'

export interface CorpusUsageMaterial {
  material_id: string
  anchor_id: number
  anchor_title: string
  text_preview: string
  tags: string[]
}

export default function CorpusUsageCard({ materials }: { materials: CorpusUsageMaterial[] }) {
  if (materials.length === 0) return null

  return (
    <div className="rounded-md border border-border bg-muted/30 px-2.5 py-2" data-testid="corpus-usage-card">
      <div className="flex items-center gap-1.5 text-xs font-medium text-foreground">
        <BookMarked className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        本章语料注入 {materials.length} 条
      </div>
      <ul className="mt-1.5 space-y-1.5">
        {materials.map((material) => (
          <li key={material.material_id} className="rounded border border-border bg-background px-2 py-1.5">
            <div className="text-[11px] font-medium text-foreground">{`《${material.anchor_title}》`}</div>
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
