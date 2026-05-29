import defaultCover from '@/assets/covers/default-cover.jpg'

export default function BookCover() {
  return (
    <div className="w-full aspect-[3/4] rounded-md overflow-hidden shadow-sm select-none relative">
      <img src={defaultCover} alt="" className="w-full h-full object-cover block scale-100" />
    </div>
  )
}
