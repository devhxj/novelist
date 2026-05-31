import Markdown from '@/components/Markdown'

interface Props {
  role: 'user' | 'assistant'
  content: string
}

export default function MessageBubble({ role, content }: Props) {
  const isUser = role === 'user'

  return (
    <div className={`flex ${isUser ? 'justify-end' : 'justify-start'}`}>
      <div
        className={`max-w-[85%] rounded-xl px-3.5 py-3 break-words ${
          isUser
            ? 'bg-primary text-primary-foreground rounded-br-sm'
            : 'bg-card border border-border/30 text-foreground rounded-bl-sm shadow-xs'
        }`}
      >
        <Markdown content={content} className={isUser ? 'markdown-user' : undefined} />
      </div>
    </div>
  )
}
