import { useEffect, useState } from 'react'
import { useApp } from '@/hooks/useApp'
import defaultCover from '@/assets/covers/default-cover.jpg'

interface Props {
  novelId?: number
  refreshKey?: number
}

export default function BookCover({ novelId, refreshKey }: Props) {
  const app = useApp()
  const coverKey = novelId ? `${novelId}:${refreshKey ?? 0}` : ''
  const [cover, setCover] = useState<{ key: string, src: string | null }>({ key: '', src: null })
  const src = cover.key === coverKey && cover.src ? cover.src : defaultCover

  useEffect(() => {
    let cancelled = false

    if (!novelId) {
      return () => {
        cancelled = true
      }
    }

    app.GetCover(novelId)
      .then((cover) => {
        if (cancelled) {
          return
        }

        setCover({
          key: coverKey,
          src: cover ? `data:${cover.content_type};base64,${cover.data_base64}` : null,
        })
      })
      .catch(() => {
        if (!cancelled) {
          setCover({ key: coverKey, src: null })
        }
      })

    return () => {
      cancelled = true
    }
  }, [app, coverKey, novelId])

  return (
    <div className="w-full aspect-[3/4] rounded-md overflow-hidden shadow-sm select-none relative bg-muted">
      <img
        key={refreshKey}
        src={src}
        alt=""
        onError={() => setCover({ key: coverKey, src: null })}
        className="w-full h-full object-cover block"
      />
    </div>
  )
}
