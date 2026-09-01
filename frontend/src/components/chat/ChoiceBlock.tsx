import { ListChecks } from 'lucide-react'

export default function ChoiceBlock({ options, onPick, disabled }: {
  options: string[]
  onPick: (option: string) => void
  disabled?: boolean
}) {
  if (options.length === 0) return null

  return (
    <div className="mt-2 rounded-md border border-border bg-muted/30 px-2.5 py-2" data-testid="choice-block">
      <div className="flex items-center gap-1.5 text-xs font-medium text-foreground">
        <ListChecks className="h-3.5 w-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        选择一个方向
      </div>
      <div className="mt-1.5 flex flex-col gap-1.5">
        {options.map((option) => (
          <button
            key={option}
            type="button"
            disabled={disabled}
            onClick={() => { onPick(option) }}
            className="w-full rounded-md border border-border bg-background px-2.5 py-1.5 text-left text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
          >
            {option}
          </button>
        ))}
      </div>
    </div>
  )
}
