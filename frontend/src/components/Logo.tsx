import { cn } from '@/lib/utils'

interface Props {
  className?: string
}

export default function Logo({ className }: Props) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 1000 1000"
      className={cn('shrink-0', className)}
      fill="none"
      role="img"
      aria-label="Novelist"
    >
      <rect width="1000" height="1000" rx="210" className="fill-foreground" />
      <path d="M192 764c88-54 193-61 306-19V313c-109-61-217-63-306-6v457Z" className="fill-background" opacity="0.96" />
      <path d="M808 764c-88-54-193-61-306-19V313c109-61 217-63 306-6v457Z" className="fill-background" opacity="0.86" />
      <path d="M500 313v432" className="stroke-foreground" strokeWidth="18" strokeLinecap="round" opacity="0.22" />
      <path d="M252 372c63-27 127-27 193 0M252 452c63-27 127-27 193 0M252 532c63-27 127-27 193 0" className="stroke-foreground" strokeWidth="22" strokeLinecap="round" opacity="0.2" />
      <path d="M555 372c63-27 127-27 193 0M555 452c63-27 127-27 193 0M555 532c63-27 127-27 193 0" className="stroke-foreground" strokeWidth="22" strokeLinecap="round" opacity="0.18" />
      <path d="M500 206v598" className="stroke-cyan-400" strokeWidth="42" strokeLinecap="round" />
      <circle cx="500" cy="280" r="58" className="fill-foreground stroke-cyan-300" strokeWidth="34" />
      <path d="M403 620c30 76 89 120 97 120s67-44 97-120" className="stroke-amber-300" strokeWidth="44" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M378 620h244" className="stroke-amber-300" strokeWidth="44" strokeLinecap="round" />
      <path d="M500 395 608 612 500 746 392 612 500 395Z" className="fill-foreground stroke-background" strokeWidth="32" strokeLinejoin="round" />
      <path d="M500 474v164" className="stroke-cyan-300" strokeWidth="30" strokeLinecap="round" />
      <circle cx="500" cy="638" r="22" className="fill-amber-300" />
    </svg>
  )
}
