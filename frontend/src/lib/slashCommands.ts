// 斜杠命令的匹配与排序。
//
// 打分排序必须与渲染共用同一份结果，否则键盘高亮项与回车插入项会指向不同命令，
// 因此这里只暴露纯函数，由输入框计算一次后同时用于导航和渲染。

export interface RankableSlashCommand {
  name: string
  description: string
}

/** 未命中任何规则的得分，达到该分值即被过滤掉。 */
const NO_MATCH = 5

/** q 的所有字符是否按顺序出现在 s 中（模糊匹配，允许中间跳字符）。 */
function charMatch(s: string, q: string): boolean {
  let qi = 0
  for (let i = 0; i < s.length && qi < q.length; i++) {
    if (s[i] === q[qi]) qi++
  }
  return qi === q.length
}

/** 匹配得分，越低越靠前；返回 NO_MATCH 表示不匹配。 */
export function scoreSlashCommand(command: RankableSlashCommand, filterText: string): number {
  const q = filterText.toLowerCase()
  const name = command.name.toLowerCase()
  if (name === q) return 0
  if (name.startsWith(q)) return 1
  if (name.includes(q)) return 2
  if (charMatch(name, q)) return 3
  if (command.description.toLowerCase().includes(q)) return 4
  return NO_MATCH
}

/**
 * 过滤并按得分升序排序。同分保持输入顺序（sort 稳定），
 * 使列表在用户继续输入时不会无理由跳动。
 */
export function rankSlashCommands<T extends RankableSlashCommand>(
  items: readonly T[],
  filterText: string,
): T[] {
  return items
    .map(command => ({ command, score: scoreSlashCommand(command, filterText) }))
    .filter(entry => entry.score < NO_MATCH)
    .sort((a, b) => a.score - b.score)
    .map(entry => entry.command)
}
