import { useState, useCallback, useRef, useEffect } from 'react'
import { ArrowUp, Square, Zap } from 'lucide-react'
import type { skill } from '@/hooks/useApp'
import SkillSlashMenu from './SkillSlashMenu'

interface Props {
  disabled: boolean
  isLoading: boolean
  placeholder: string
  skills: skill.SkillMeta[]
  onSend: (message: string) => void
  onStop: () => void
  onListSkills: () => void
}

export default function ChatInput({ disabled, isLoading, placeholder, skills, onSend, onStop, onListSkills }: Props) {
  const [hasContent, setHasContent] = useState(false)
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  // slash menu state
  const [slashOpen, setSlashOpen] = useState(false)
  const [slashFilter, setSlashFilter] = useState('')
  const [slashIndex, setSlashIndex] = useState(0)
  const [slashPos, setSlashPos] = useState({ top: 0, left: 0, width: 0 })
  const [activeSkill, setActiveSkill] = useState<skill.SkillMeta | null>(null)

  const q = slashFilter.toLowerCase()
  const filteredSkills = skills.filter(s =>
    s.name.toLowerCase().includes(q) || s.description.toLowerCase().includes(q)
  )

  const closeSlash = useCallback(() => {
    setSlashOpen(false)
    setSlashFilter('')
    setSlashIndex(0)
  }, [])

  const applySlashSelection = useCallback((sk: skill.SkillMeta) => {
    const textarea = textareaRef.current
    if (!textarea) return
    textarea.value = '/' + sk.name + ' '
    textarea.focus()
    textarea.setSelectionRange(textarea.value.length, textarea.value.length)
    textarea.style.height = 'auto'
    textarea.style.height = Math.min(textarea.scrollHeight, 180) + 'px'
    setHasContent(true)
    setActiveSkill(sk)
    closeSlash()
  }, [closeSlash])

  const updateSlashPos = useCallback(() => {
    const textarea = textareaRef.current
    if (!textarea) return
    const rect = textarea.getBoundingClientRect()
    setSlashPos({ top: rect.top, left: rect.left, width: rect.width })
  }, [])

  const checkSlash = useCallback((value: string) => {
    if (value.length > 0 && value[0] === '/') {
      const spaceIdx = value.indexOf(' ')
      if (spaceIdx > 1) {
        const name = value.slice(1, spaceIdx)
        setActiveSkill(skills.find(s => s.name === name) ?? null)
        closeSlash()
        return
      }
      setActiveSkill(null)
      updateSlashPos()
      setSlashFilter(value.slice(1))
      setSlashIndex(0)
      setSlashOpen(true)
      onListSkills()
    } else {
      setActiveSkill(null)
      closeSlash()
    }
  }, [closeSlash, updateSlashPos, onListSkills, skills])

  // close slash on resize/scroll
  useEffect(() => {
    if (!slashOpen) return
    const handler = () => updateSlashPos()
    window.addEventListener('resize', handler)
    window.addEventListener('scroll', handler, true)
    return () => {
      window.removeEventListener('resize', handler)
      window.removeEventListener('scroll', handler, true)
    }
  }, [slashOpen, updateSlashPos])

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (slashOpen && filteredSkills.length > 0) {
      if (e.key === 'ArrowDown') {
        e.preventDefault()
        setSlashIndex(i => (i + 1) % filteredSkills.length)
        return
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault()
        setSlashIndex(i => (i - 1 + filteredSkills.length) % filteredSkills.length)
        return
      }
      if (e.key === 'Enter' || e.key === 'Tab') {
        e.preventDefault()
        applySlashSelection(filteredSkills[slashIndex])
        return
      }
      if (e.key === 'Escape') {
        e.preventDefault()
        closeSlash()
        return
      }
    }

    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      const textarea = e.currentTarget as HTMLTextAreaElement
      const value = textarea.value.trim()
      if (value && !disabled) {
        onSend(value)
        textarea.value = ''
        textarea.style.height = 'auto'
        setHasContent(false)
        setActiveSkill(null)
        closeSlash()
      }
    }
  }, [slashOpen, filteredSkills, slashIndex, disabled, onSend, applySlashSelection, closeSlash])

  const handleInput = useCallback((e: React.FormEvent<HTMLTextAreaElement>) => {
    const target = e.currentTarget
    target.style.height = 'auto'
    target.style.height = Math.min(target.scrollHeight, 180) + 'px'
    setHasContent(target.value.trim().length > 0)
    checkSlash(target.value)
  }, [checkSlash])

  const handleSendClick = useCallback(() => {
    const textarea = textareaRef.current
    if (!textarea) return
    const value = textarea.value.trim()
    if (value && !disabled) {
      onSend(value)
      textarea.value = ''
      textarea.style.height = 'auto'
      setHasContent(false)
      setActiveSkill(null)
      closeSlash()
    }
  }, [disabled, onSend, closeSlash])

  const handleStopClick = useCallback(() => {
    onStop()
  }, [onStop])

  return (
    <div className="px-4 pt-2 shrink-0">
      {activeSkill && (
        <div className="flex items-center mb-1 px-1">
          <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-md bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-400 text-xs font-medium">
            <Zap className="w-3 h-3" />
            {activeSkill.name}
          </span>
        </div>
      )}
      <div className="flex items-end gap-2 bg-muted/30 rounded-2xl border px-2 py-2">
        <textarea
          ref={textareaRef}
          placeholder={placeholder}
          disabled={disabled}
          rows={1}
          onKeyDown={handleKeyDown}
          onInput={handleInput}
          className="flex-1 bg-transparent resize-none text-sm leading-relaxed text-foreground outline-none placeholder:text-muted-foreground/50 disabled:text-muted-foreground/40 py-2 px-2 min-h-[28px] max-h-[180px]"
        />
        {isLoading && !hasContent ? (
          <button
            onClick={handleStopClick}
            className="w-[52px] h-[36px] min-w-[52px] flex items-center justify-center rounded-xl bg-red-500 text-white shadow-md shadow-red-500/20 transition-all hover:bg-red-600 hover:shadow-lg hover:shadow-red-500/30 shrink-0"
          >
            <Square className="w-4 h-4" fill="currentColor" />
          </button>
        ) : (
          <button
            disabled={disabled || !hasContent}
            onClick={handleSendClick}
            className="w-[52px] h-[36px] min-w-[52px] flex items-center justify-center rounded-xl bg-gradient-to-br from-amber-400 to-amber-600 text-white shadow-md shadow-amber-500/20 transition-all hover:-translate-y-px hover:shadow-lg hover:shadow-amber-500/30 disabled:bg-muted disabled:text-muted-foreground/40 disabled:shadow-none disabled:hover:translate-y-0 shrink-0"
          >
            <ArrowUp className="w-5 h-5" />
          </button>
        )}
      </div>

      {slashOpen && filteredSkills.length > 0 && (
        <SkillSlashMenu
          skills={filteredSkills}
          filterText={slashFilter}
          selectedIndex={slashIndex}
          position={slashPos}
          onSelect={applySlashSelection}
          onHover={setSlashIndex} />
      )}
    </div>
  )
}
