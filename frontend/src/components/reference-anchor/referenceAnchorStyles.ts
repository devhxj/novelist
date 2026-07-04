export const inputClass = 'w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-xs text-foreground placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring'
export const actionButtonClass = 'inline-flex items-center gap-1.5 rounded bg-secondary px-2.5 py-1.5 text-xs font-medium text-foreground hover:bg-secondary/80 disabled:opacity-50'

export function statusTone(status: string): string {
  if (status === 'approved' || status === 'material_bound' || status === 'passed') return 'text-emerald-600 dark:text-emerald-400'
  if (status === 'failed' || status === 'review_failed' || status === 'stale') return 'text-destructive'
  return 'text-muted-foreground'
}
