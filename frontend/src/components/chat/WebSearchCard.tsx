import { memo, useState } from 'react'
import { Search, ExternalLink, ChevronDown, ChevronRight } from 'lucide-react'
import Markdown from '@/components/Markdown'
import { BrowserOpenURL } from '@/lib/novelist/runtime'
import './WebSearchCard.css'

interface SourceItem {
  title: string
  url: string
}

interface Props {
  result: Record<string, unknown>
}

function openExternal(url: string) {
  if (window.confirm(`是否在浏览器中打开\n${url}`)) {
    void BrowserOpenURL(url)
  }
}

export default memo(function WebSearchCard({ result }: Props) {
  const [summaryOpen, setSummaryOpen] = useState(false)

  const queries = (result.queries as string[]) || []
  const summary = (result.summary as string) || ''
  const sources = (result.sources as SourceItem[]) || []

  return (
    <div className="web-card completed">
      <div className="web-card-row">
        <span className="web-card-icon"><Search size={14} /></span>
        <span className="web-card-label">搜索完成</span>
        <span className="web-card-badge web-card-badge-done">完成</span>
      </div>

      {queries.length > 0 && (
        <div className="web-card-queries">
          <span className="web-card-queries-label">搜索词：</span>
          {queries.map((q, i) => (
            <span key={i} className="web-card-query-tag">{q}</span>
          ))}
        </div>
      )}

      {sources.length > 0 && (
        <div className="web-card-sources">
          {sources.map((s, i) => (
            <div
              key={i}
              className="web-card-source"
              onClick={() => openExternal(s.url)}
              title={s.url}
            >
              <span className="web-card-source-index">{i + 1}</span>
              <div className="web-card-source-body">
                <span className="web-card-source-title">{s.title || s.url}</span>
                <span className="web-card-source-url">{s.url}</span>
              </div>
              <ExternalLink size={12} className="web-card-source-ext" />
            </div>
          ))}
        </div>
      )}

      {summary && (
        <div className="web-card-summary">
          <button
            className="web-card-summary-toggle"
            onClick={() => setSummaryOpen(!summaryOpen)}
          >
            {summaryOpen ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
            搜索结果总结
          </button>
          {summaryOpen && (
            <div className="web-card-summary-body">
              <Markdown content={summary} />
            </div>
          )}
        </div>
      )}
    </div>
  )
})
