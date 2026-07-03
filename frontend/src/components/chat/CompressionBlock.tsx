import { useState, useEffect } from 'react'
import './CompressionBlock.css'

interface Props {
  phase: 'compressing' | 'done'
}

export default function CompressionBlock({ phase }: Props) {
  const [seconds, setSeconds] = useState(0)

  useEffect(() => {
    if (phase !== 'compressing') return
    const resetTimer = window.setTimeout(() => setSeconds(0), 0)
    const timer = setInterval(() => setSeconds(s => s + 1), 1000)
    return () => {
      window.clearTimeout(resetTimer)
      clearInterval(timer)
    }
  }, [phase])

  const elapsed = seconds < 60
    ? `${seconds}s`
    : `${Math.floor(seconds / 60)}m${seconds % 60}s`

  if (phase === 'compressing') {
    return (
      <div className="compression-block">
        <div className="compression-compressing">
          <span className="compression-shimmer">正在压缩上下文（{elapsed}）</span>
        </div>
      </div>
    )
  }

  return (
    <div className="compression-block">
      <div className="compression-done">
        <span className="compression-line" />
        <span className="compression-label">已压缩上下文</span>
        <span className="compression-line" />
      </div>
    </div>
  )
}
