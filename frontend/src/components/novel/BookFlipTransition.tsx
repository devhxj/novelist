import { useEffect } from 'react'
import BookCover from './BookCover'

interface Props {
  onComplete: () => void
}

export default function BookFlipTransition({ onComplete }: Props) {
  useEffect(() => {
    const timer = setTimeout(onComplete, 750)
    return () => clearTimeout(timer)
  }, [onComplete])

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-background">
      {/* 背景光晕 — 翻页时背后透出的柔和光 */}
      <div className="absolute inset-0 bg-gradient-to-r from-primary/[0.03] via-transparent to-transparent" />

      <div
        className="w-56 rounded-lg shadow-2xl"
        style={{
          aspectRatio: '3/4',
          transformOrigin: 'left center',
          animation: 'bookOpen 0.7s cubic-bezier(0.25, 0.46, 0.45, 0.94) forwards',
        }}
      >
        <BookCover />
      </div>

      <style>{`
        @keyframes bookOpen {
          0% {
            transform: perspective(1200px) rotateY(0deg);
            opacity: 1;
          }
          30% {
            opacity: 1;
          }
          100% {
            transform: perspective(1200px) rotateY(-150deg);
            opacity: 0;
          }
        }
      `}</style>
    </div>
  )
}
