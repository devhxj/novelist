import { useEffect, useState } from 'react'
import type { MouseEvent as ReactMouseEvent } from 'react'
import { X } from 'lucide-react'
import { useApp } from '@/hooks/useApp'
import { pushToast } from '@/lib/toast'
import { describeBridgeError } from '@/lib/novelist/bridgeErrors'
import defaultCover from '@/assets/covers/default-cover.jpg'

interface Props {
  novelId?: number
  refreshKey?: number
}

export default function BookCover({ novelId, refreshKey }: Props) {
  const app = useApp()
  const coverKey = novelId ? `${novelId}:${refreshKey ?? 0}` : ''
  const [cover, setCover] = useState<{ key: string, src: string | null }>({ key: '', src: null })
  const [removing, setRemoving] = useState(false)
  const src = cover.key === coverKey && cover.src ? cover.src : defaultCover
  const hasCover = cover.key === coverKey && cover.src !== null

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

  // F13：封面可移除（DeleteCover 接线）——换封面有了退出路径，不再是"只能一直换"。
  async function handleRemoveCover(e: ReactMouseEvent) {
    e.stopPropagation()
    if (!novelId || removing) return
    setRemoving(true)
    try {
      await app.DeleteCover(novelId)
      setCover({ key: coverKey, src: null })
    } catch (err) {
      pushToast({ kind: 'error', message: describeBridgeError(err, '移除封面失败，请稍后重试。').message })
    } finally {
      setRemoving(false)
    }
  }

  return (
    <div className="w-full aspect-[3/4] rounded-md overflow-hidden shadow-sm select-none relative bg-muted">
      <img
        key={refreshKey}
        src={src}
        alt=""
        onError={() => setCover({ key: coverKey, src: null })}
        className="w-full h-full object-cover block"
      />
      {hasCover && (
        <button
          type="button"
          onClick={handleRemoveCover}
          aria-label="移除封面"
          title="移除封面"
          data-testid={`remove-cover-${novelId}`}
          disabled={removing}
          className="absolute right-1.5 top-1.5 z-10 flex h-6 w-6 items-center justify-center rounded-md bg-background/85 border shadow-sm text-muted-foreground hover:text-destructive transition-colors disabled:opacity-50"
        >
          <X className="h-3 w-3" aria-hidden="true" />
        </button>
      )}
    </div>
  )
}
