import BookCover from './BookCover'
import type { novel } from '@/hooks/useApp'

interface Props {
  novel: novel.Novel
  onClick: () => void
}

export default function NovelCard({ novel, onClick }: Props) {
  return (
    <button
      onClick={onClick}
      className="group text-left w-full focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded-xl transition-transform duration-300 ease-out hover:scale-[1.03]"
    >
      <div className="rounded-lg overflow-hidden transition-shadow duration-300 group-hover:shadow-lg group-hover:shadow-foreground/[0.06]">
        <BookCover />
      </div>
      <p className="mt-3 text-sm font-medium truncate text-center group-hover:text-primary transition-colors">
        {novel.title}
      </p>
    </button>
  )
}
