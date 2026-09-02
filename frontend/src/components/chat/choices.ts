export interface ParsedChoices {
  body: string
  options: string[]
}

// 已闭合的 ```choices 块：捕获块内 JSON。
const CLOSED_CHOICES_BLOCK = /```choices[ \t]*\r?\n([\s\S]*?)\n?```/g
const CHOICES_MARKER = '```choices'

const MAX_OPTIONS = 6

// 流式输出中尚未闭合的尾部块：最后一个 choices 块之后没有闭合围栏时，整块隐藏，闭合后再解析渲染。
function openChoicesTailIndex(content: string): number {
  const start = content.lastIndexOf(CHOICES_MARKER)
  if (start < 0) return -1
  const firstBreak = content.indexOf('\n', start)
  if (firstBreak < 0) return start
  return content.indexOf('```', firstBreak + 1) < 0 ? start : -1
}

// 解析助手回复中的 ```choices 选项块：
// - 有效块的 options 去重后渲染为可点击按钮，并从正文中移除；
// - JSON 解析失败的块原样保留（AI 输出格式错误不应无声吞掉内容）；
// - 流式中未闭合的尾部块整块隐藏，避免闪现原始 JSON。
export function parseChoices(content: string): ParsedChoices {
  const tailIndex = openChoicesTailIndex(content)
  const closed = tailIndex < 0 ? content : content.slice(0, tailIndex)

  const options: string[] = []
  const seen = new Set<string>()
  const body = closed.replace(CLOSED_CHOICES_BLOCK, (block, inner: string) => {
    try {
      const parsed = JSON.parse(inner) as { options?: unknown }
      if (!Array.isArray(parsed.options)) {
        return block
      }
      for (const option of parsed.options) {
        if (typeof option !== 'string') continue
        const trimmed = option.trim()
        if (trimmed.length === 0 || seen.has(trimmed)) continue
        if (options.length >= MAX_OPTIONS) break
        seen.add(trimmed)
        options.push(trimmed)
      }
      return ''
    } catch {
      return block
    }
  })

  return { body: body.trim(), options }
}
