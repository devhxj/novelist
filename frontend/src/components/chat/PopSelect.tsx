import { useState, useRef, useEffect } from 'react'
import { ChevronUp } from 'lucide-react'

interface Option {
  value: string
  label: string
}

interface Props {
  value: string
  options: Option[]
  onChange: (value: string) => void
  className?: string
  minWidth?: string
}

export default function PopSelect({ value, options, onChange, className = '', minWidth = '130px' }: Props) {
  const [open, setOpen] = useState(false)
  const containerRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const handleClick = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', handleClick)
    return () => document.removeEventListener('mousedown', handleClick)
  }, [open])

  const selected = options.find(o => o.value === value)

  return (
    <div ref={containerRef} className={`relative ${className}`}>
      <button
        onClick={() => setOpen(!open)}
        style={{ minWidth }}
        className="h-[30px] rounded-lg border bg-background px-2.5 text-xs text-muted-foreground flex items-center justify-between gap-1 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
      >
        <span className="truncate">{selected?.label || '无可用模型'}</span>
        <ChevronUp className={`w-3 h-3 shrink-0 transition-transform ${open ? '' : 'rotate-180'}`} />
      </button>

      {open && (
        <div className="absolute bottom-full left-0 mb-1 w-full max-h-[200px] overflow-y-auto rounded-lg border bg-background shadow-lg z-50">
          {options.map(opt => (
            <button
              key={opt.value}
              onClick={() => {
                onChange(opt.value)
                setOpen(false)
              }}
              className={`w-full text-left px-2.5 py-1.5 text-xs hover:bg-muted transition-colors ${
                opt.value === value ? 'bg-primary/10 text-primary font-medium' : 'text-muted-foreground'
              }`}
            >
              {opt.label}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
