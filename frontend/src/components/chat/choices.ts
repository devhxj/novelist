export interface ParsedChoices {
  body: string
  options: string[]
}

const CHOICES_BLOCK_PATTERN = /```choices\s*\n([\s\S]*?)\n?```/

// 解析助手回复中的 ```choices 选项块：块外文本正常渲染，块内 options 渲染为可点击按钮。
export function parseChoices(content: string): ParsedChoices {
  const match = content.match(CHOICES_BLOCK_PATTERN)
  if (!match) return { body: content, options: [] }

  let options: string[] = []
  try {
    const parsed = JSON.parse(match[1]) as { options?: unknown }
    if (Array.isArray(parsed.options)) {
      options = parsed.options
        .map((option) => (typeof option === 'string' ? option.trim() : ''))
        .filter((option) => option.length > 0)
        .slice(0, 6)
    }
  } catch {
    options = []
  }

  return { body: content.replace(match[0], '').trim(), options }
}
