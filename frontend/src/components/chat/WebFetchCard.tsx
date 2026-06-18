import { memo, useState } from 'react'
import { Globe, ExternalLink, ChevronDown, ChevronRight } from 'lucide-react'
import Markdown from '@/components/Markdown'
import './WebFetchCard.css'

interface Props {
  result: Record<string, unknown>
  displayText: string
}

function openExternal(url: string) {
  if (window.confirm(`是否在浏览器中打开\n${url}`)) {
    window.open(url, '_blank', 'noopener,noreferrer')
  }
}

export default memo(function WebFetchCard({ result, displayText }: Props) {
  const [contentOpen, setContentOpen] = useState(false)

  const url = (result.url as string) || ''
  const title = (result.title as string) || ''
  const text = (result.text as string) || ''
  const wordCount = text.replace(/\s/g, '').length

  return (
    <div className="fetch-card completed">
      <div className="fetch-card-row">
        <span className="fetch-card-icon"><Globe size={14} /></span>
        <span className="fetch-card-label">{displayText}</span>
        <span className="fetch-card-badge fetch-card-badge-done">完成</span>
      </div>

      <div className="fetch-card-meta">
        <div className="fetch-card-title-line">
          <span className="fetch-card-title">{title || url}</span>
          <button
            className="fetch-card-ext-btn"
            onClick={() => openExternal(url)}
            title={url}
          >
            <ExternalLink size={12} />
          </button>
        </div>
        {url && (
          <span className="fetch-card-url">{url}</span>
        )}
      </div>

      {text && (
        <div className="fetch-card-content">
          <button
            className="fetch-card-content-toggle"
            onClick={() => setContentOpen(!contentOpen)}
          >
            {contentOpen ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
            页面内容 ({wordCount.toLocaleString()} 字)
          </button>
          {contentOpen && (
            <div className="fetch-card-content-body">
              <Markdown content={text} />
            </div>
          )}
        </div>
      )}
    </div>
  )
})
