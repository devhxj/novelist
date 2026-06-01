import { isValidElement, useCallback, useMemo, useState, type ReactNode } from 'react'
import ReactMarkdown from 'react-markdown'
import type { Components } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import remarkMath from 'remark-math'
import rehypeKatex from 'rehype-katex'
import rehypeRaw from 'rehype-raw'
import hljs from 'highlight.js/lib/common'
import 'katex/dist/katex.min.css'
import './Markdown.css'

interface MarkdownProps {
  content: string
  className?: string
}

interface CodeBlockProps {
  className?: string
  children?: ReactNode
}

interface CodeElementProps {
  className?: string
  children?: ReactNode
}

function getNodeText(children: ReactNode): string {
  if (children === null || children === undefined) {
    return ''
  }

  if (Array.isArray(children)) {
    return children.map(getNodeText).join('')
  }

  if (typeof children === 'string' || typeof children === 'number') {
    return String(children)
  }

  return ''
}

function CodeBlock({ className, children }: CodeBlockProps) {
  const [copied, setCopied] = useState(false)
  const lang = className?.replace(/^language-/, '') || ''
  const code = getNodeText(children).replace(/\n$/, '')
  const highlightedCode = useMemo(() => {
    if (!lang || !hljs.getLanguage(lang)) {
      return null
    }

    return hljs.highlight(code, { language: lang, ignoreIllegals: true }).value
  }, [code, lang])

  const handleCopy = useCallback(() => {
    navigator.clipboard.writeText(code).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }, [code])

  return (
    <div className="markdown-code-block group not-prose">
      <div className="markdown-code-toolbar">
        <span className="markdown-code-lang">{lang || 'text'}</span>
        <button
          type="button"
          onClick={handleCopy}
          className="markdown-code-copy"
          aria-label="复制代码"
        >
          {copied ? '已复制' : '复制'}
        </button>
      </div>
      <pre className="markdown-code-pre">
        {highlightedCode ? (
          <code
            className={className}
            dangerouslySetInnerHTML={{ __html: highlightedCode }}
          />
        ) : (
          <code className={className}>{code}</code>
        )}
      </pre>
    </div>
  )
}

function PreBlock({ children }: { children?: ReactNode }) {
  const codeElement = isValidElement<CodeElementProps>(children) && children.type === 'code'
    ? children
    : null

  if (!codeElement) {
    return <pre className="markdown-code-pre">{children}</pre>
  }

  return (
    <CodeBlock className={codeElement.props.className}>
      {codeElement.props.children}
    </CodeBlock>
  )
}

const components: Components = {
  a: ({ href, children }) => (
    <a href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  ),
  pre: ({ children }) => <PreBlock>{children}</PreBlock>,
  code: ({ className, children }) => (
    <code className={className}>{children}</code>
  ),
  img: ({ src, alt }) => (
    <img src={src} alt={alt || ''} loading="lazy" />
  ),
  input: ({ checked, type, ...props }) => (
    <input type={type} checked={checked} readOnly {...props} />
  ),
  details: ({ children, ...props }) => (
    <details {...props}>{children}</details>
  ),
  summary: ({ children, ...props }) => (
    <summary {...props}>{children}</summary>
  ),
}

export default function Markdown({ content, className }: MarkdownProps) {
  const normalizedContent = content.replace(/\r\n?/g, '\n').replace(/^\n+/, '')

  return (
    <div className={`prose prose-sm max-w-none dark:prose-invert ${className || ''}`}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath]}
        rehypePlugins={[rehypeKatex, rehypeRaw]}
        components={components}
      >
        {normalizedContent}
      </ReactMarkdown>
    </div>
  )
}
