import { isContentPath, outlinePath } from './types'
import type { EditorTab, EditorTabConflict, FileChangeTarget } from './types'

export type FileChangeDecision =
  | { kind: 'ignore' }
  | { kind: 'refresh'; target: FileChangeTarget }
  | { kind: 'conflict'; target: FileChangeTarget }

// 决策只依赖这几个字段，测试里可以直接喂裸对象。
export type FileChangeTabState = Pick<EditorTab, 'type' | 'path' | 'isDirty'>

// chapters/007.md 的大纲伴生文件是 outlines/007.md；novelist.md 没有大纲。
export function derivedOutlinePath(path: string): string | null {
  if (!isContentPath(path) || path === 'novelist.md') return null
  const num = parseInt(path.replace(/.*\//, '').replace('.md', ''), 10)
  if (!Number.isFinite(num) || num <= 0) return null
  return outlinePath(num)
}

export function resolveFileChange(
  tab: FileChangeTabState,
  eventPath: string | undefined,
): FileChangeDecision {
  if (tab.type !== 'file' || !eventPath) return { kind: 'ignore' }

  if (tab.path === eventPath) {
    // 未保存的正文一旦被外部写入覆盖，作者刚敲的字就没了，且脏标记被清掉后
    // 连"还没存盘"这个线索都不剩。有脏改动时只挂起对方版本，去留交给作者。
    return tab.isDirty
      ? { kind: 'conflict', target: 'content' }
      : { kind: 'refresh', target: 'content' }
  }

  // 大纲只有只读视图，不存在未保存改动，直接刷新不会丢东西。
  const outline = derivedOutlinePath(tab.path)
  if (outline && outline === eventPath) {
    return { kind: 'refresh', target: 'outlineContent' }
  }

  return { kind: 'ignore' }
}

export function fileChangePatch(
  decision: FileChangeDecision,
  incoming: string,
  eventPath: string,
): Partial<EditorTab> {
  if (decision.kind === 'conflict') {
    const conflict: EditorTabConflict = { target: decision.target, path: eventPath, incoming }
    return { conflict }
  }
  if (decision.kind === 'refresh') {
    const patch: Partial<EditorTab> = { [decision.target]: incoming }
    // 已经采用磁盘版本，先前挂起的冲突随之失效。
    patch.conflict = undefined
    if (decision.target === 'content') patch.isDirty = false
    return patch
  }
  return {}
}

// 作者选「用 AI 版本」：接受挂起的外部内容，正文回到与磁盘一致的干净状态。
export function acceptIncomingPatch(conflict: EditorTabConflict): Partial<EditorTab> {
  const patch: Partial<EditorTab> = { [conflict.target]: conflict.incoming, conflict: undefined }
  if (conflict.target === 'content') patch.isDirty = false
  return patch
}

export function conflictDiffToolId(path: string): string {
  return `file-change-conflict:${path}`
}
