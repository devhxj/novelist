import { memo } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import 'highlight.js/styles/github.css'

interface MarkdownProps {
  children: string
  className?: string
}

export const Markdown = memo(({ children, className }: MarkdownProps) => {
  return (
    <div className={className}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          code({ node, inline, className, children, ...props }: any) {
            return !inline ? (
              <pre style={{ margin: '8px 0', overflow: 'auto' }}>
                <code className={className} {...props}>
                  {children}
                </code>
              </pre>
            ) : (
              <code className={className} {...props}>
                {children}
              </code>
            )
          },
          p({ node, ...props }: any) {
            return <p style={{ margin: '4px 0' }} {...props} />
          },
          ul({ node, ...props }: any) {
            return <ul style={{ margin: '4px 0', paddingLeft: '20px' }} {...props} />
          },
          ol({ node, ...props }: any) {
            return <ol style={{ margin: '4px 0', paddingLeft: '20px' }} {...props} />
          },
          blockquote({ node, ...props }: any) {
            return (
              <blockquote
                style={{
                  margin: '8px 0',
                  padding: '4px 12px',
                  borderLeft: '3px solid #1677ff',
                  background: '#f0f0f0',
                  borderRadius: '0 4px 4px 0',
                }}
                {...props}
              />
            )
          },
        }}
      >
        {children}
      </ReactMarkdown>
    </div>
  )
})

Markdown.displayName = 'Markdown'
