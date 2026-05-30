import { ArrowUp } from 'lucide-react'

interface Props {
  disabled: boolean
  placeholder: string
  onSend: (message: string) => void
}

export default function ChatInput({ disabled, placeholder, onSend }: Props) {
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      const textarea = e.currentTarget as HTMLTextAreaElement
      const value = textarea.value.trim()
      if (value && !disabled) {
        onSend(value)
        textarea.value = ''
        textarea.style.height = 'auto'
      }
    }
  }

  const handleInput = (e: React.FormEvent<HTMLTextAreaElement>) => {
    const target = e.currentTarget
    target.style.height = 'auto'
    target.style.height = Math.min(target.scrollHeight, 180) + 'px'
  }

  const handleClick = () => {
    const container = document.getElementById('chat-input-textarea') as HTMLTextAreaElement | null
    if (!container) return
    const value = container.value.trim()
    if (value && !disabled) {
      onSend(value)
      container.value = ''
      container.style.height = 'auto'
    }
  }

  return (
    <div className="px-4 pt-2 shrink-0">
      <div className="flex items-end gap-2 bg-muted/30 rounded-2xl border px-2 py-2">
        <textarea
          id="chat-input-textarea"
          placeholder={placeholder}
          disabled={disabled}
          rows={1}
          onKeyDown={handleKeyDown}
          onInput={handleInput}
          className="flex-1 bg-transparent resize-none text-sm leading-relaxed text-foreground outline-none placeholder:text-muted-foreground/50 disabled:text-muted-foreground/40 py-2 px-2 min-h-[28px] max-h-[180px]"
        />
        <button
          disabled={disabled}
          onClick={handleClick}
          className="w-[52px] h-[36px] min-w-[52px] flex items-center justify-center rounded-xl bg-gradient-to-br from-amber-400 to-amber-600 text-white shadow-md shadow-amber-500/20 transition-all hover:-translate-y-px hover:shadow-lg hover:shadow-amber-500/30 disabled:bg-muted disabled:text-muted-foreground/40 disabled:shadow-none disabled:hover:translate-y-0 shrink-0"
        >
          <ArrowUp className="w-5 h-5" />
        </button>
      </div>
    </div>
  )
}
