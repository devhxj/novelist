export type FileChangeTarget = 'content' | 'outlineContent'

// 外部改动撞上未保存正文时挂起的对方版本，等作者在冲突条上三选一。
export type EditorTabConflict = {
  target: FileChangeTarget
  path: string
  incoming: string
}

export type EditorTab = {
  id: string
  type: 'file' | 'diff'
  path: string
  title: string
  // file tab
  content?: string
  outlineContent?: string
  isDirty?: boolean
  // U1：最近一次已知磁盘正文的基线令牌（加载/保存/外部刷新时更新）。
  // 保存时随 SaveContent 上送做比较-交换；挂起冲突期间清空，让“保留我的”强制落盘。
  savedHash?: string
  viewMode?: 'content' | 'outline' | 'preview' | 'edit'
  readOnly?: boolean
  conflict?: EditorTabConflict
  // diff tab
  diff?: string
  original?: string
  modified?: string
  changeType?: string
  reason?: string
  toolId?: string
}

// 文件名格式 chapters/001.md，outlines/001.md 同理
export function outlinePath(num: number): string {
  return `outlines/${String(num).padStart(3, '0')}.md`
}

export function isContentPath(p: string): boolean {
  return isChapterPath(p) || p === 'novelist.md'
}

function isChapterPath(p: string): boolean {
  return /^chapters\/\d+\.md$/.test(p)
}

export function isOutlinePath(p: string): boolean {
  return /^outlines\/\d+\.md$/.test(p)
}

export function isSkillPath(p: string): boolean {
  return p.startsWith('skills/') || p.startsWith('~/.novelist/skills/') || p.startsWith('/builtin/skills/')
}

export function skillNameFromPath(p: string): string {
  return p.replace(/.*\//, '').replace('.md', '')
}

// splitFrontmatter splits YAML frontmatter from markdown content.
export function splitFrontmatter(content: string): { meta: Record<string, string>; body: string } {
  if (!content.startsWith('---')) {
    return { meta: {}, body: content }
  }
  const end = content.indexOf('\n---', 3)
  if (end === -1) {
    return { meta: {}, body: content }
  }
  const fm = content.substring(3, end).trim()
  const body = content.substring(end + 4).trim()
  const meta: Record<string, string> = {}
  for (const line of fm.split('\n')) {
    const i = line.indexOf(':')
    if (i > 0) {
      meta[line.substring(0, i).trim()] = line.substring(i + 1).trim()
    }
  }
  return { meta, body }
}
