interface Props {
  disabled: boolean
  onSend: (message: string) => void
}

export default function ChatInput({ disabled, onSend }: Props) {
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      const input = e.currentTarget as HTMLInputElement
      const value = input.value.trim()
      if (value) {
        onSend(value)
        input.value = ''
      }
    }
  }

  return (
    <div className="p-3 border-t">
      <div className="flex items-center gap-2">
        <input
          type="text"
          placeholder={disabled ? '请先选择作品' : '输入消息...'}
          disabled={disabled}
          onKeyDown={handleKeyDown}
          className="flex-1 h-8 rounded-md border bg-background px-3 text-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:text-muted-foreground/50"
        />
        <button
          disabled={disabled}
          className="w-8 h-8 flex items-center justify-center rounded-md bg-primary text-primary-foreground text-sm disabled:bg-muted disabled:text-muted-foreground/50"
        >
          →
        </button>
      </div>
    </div>
  )
}
