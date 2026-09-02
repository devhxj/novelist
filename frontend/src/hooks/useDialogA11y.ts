import { useCallback, useEffect, useRef } from 'react'

// 统一对话框可达性（N2）：role/aria-modal、document 级 Escape、焦点圈定与初始焦点。
// 之前 Escape 监听常挂在不可聚焦的 div 上，键盘作者根本按不出关闭效果。
// 用法：const dialogProps = useDialogA11y(open, onClose, '设置')，
// 然后把 {...dialogProps} 摊到面板根节点上。
export function useDialogA11y(open: boolean, onClose: () => void, label: string) {
  const panelNodeRef = useRef<HTMLDivElement | null>(null)

  const setPanelNode = useCallback((node: HTMLDivElement | null) => {
    panelNodeRef.current = node
  }, [])

  useEffect(() => {
    if (!open) return
    const panel = panelNodeRef.current
    panel?.focus()

    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.preventDefault()
        e.stopPropagation()
        onClose()
        return
      }
      if (e.key === 'Tab' && panel) {
        const focusables = Array.from(
          panel.querySelectorAll<HTMLElement>('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'),
        ).filter((el) => !el.hasAttribute('disabled') && el.getClientRects().length > 0)
        if (focusables.length === 0) return
        const first = focusables[0]
        const last = focusables[focusables.length - 1]
        const active = document.activeElement
        if (!panel.contains(active)) {
          e.preventDefault()
          first.focus()
        } else if (e.shiftKey && active === first) {
          e.preventDefault()
          last.focus()
        } else if (!e.shiftKey && active === last) {
          e.preventDefault()
          first.focus()
        }
      }
    }
    document.addEventListener('keydown', handler, true)
    return () => document.removeEventListener('keydown', handler, true)
  }, [open, onClose])

  return {
    ref: setPanelNode,
    role: 'dialog' as const,
    'aria-modal': true,
    'aria-label': label,
    tabIndex: -1,
    outline: 'none',
  }
}
