export type EditorTab = {
  id: string
  type: 'file' | 'diff'
  path: string
  title: string
  // file tab
  content?: string
  outlineContent?: string
  isDirty?: boolean
  viewMode?: 'content' | 'outline' | 'preview' | 'edit'
  readOnly?: boolean
  // diff tab
  diff?: string
  original?: string
  modified?: string
  changeType?: string
  reason?: string
  toolId?: string
}

// 文件名格式 chapters/001.md，outlines/001.md 同理
export function chapterPath(num: number): string {
  return `chapters/${String(num).padStart(3, '0')}.md`
}

export function outlinePath(num: number): string {
  return `outlines/${String(num).padStart(3, '0')}.md`
}

export function novelistPath(): string {
  return 'novelist.md'
}

export function isContentPath(p: string): boolean {
  return isChapterPath(p) || p === 'novelist.md'
}

export function isChapterPath(p: string): boolean {
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

export function chapterNumFromPath(p: string): number {
  const match = /^(?:chapters|outlines)\/(\d+)\.md$/.exec(p)
  if (!match) return 0
  return parseInt(match[1], 10) || 0
}
