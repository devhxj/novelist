import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-slash-'))
const outputFile = path.join(tempDir, 'slashCommands.mjs')

try {
  await build({
    entryPoints: ['src/lib/slashCommands.ts'],
    outfile: outputFile,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'es2023',
    logLevel: 'silent',
  })

  const { rankSlashCommands, scoreSlashCommand } = await import(pathToFileURL(outputFile))

  // 故意把最长的名字放在最前面，这样 'cha' 与 'char' 会产生真实的重排
  const commands = [
    { name: 'chapter', description: '章节操作' },
    { name: 'character', description: '角色卡管理' },
    { name: 'char', description: '快速插入角色名' },
    { name: 'outline', description: '生成大纲' },
    { name: 'continue', description: '续写正文' },
    { name: 'polish', description: '润色选中段落' },
  ]

  // 分档：完全相等 < 前缀 < 子串 < 顺序模糊 < 仅描述命中 < 不匹配
  assert.equal(scoreSlashCommand({ name: 'char', description: '角色名' }, 'char'), 0)
  assert.equal(scoreSlashCommand({ name: 'character', description: '角色卡' }, 'char'), 1)
  assert.equal(scoreSlashCommand({ name: 'my-char-tool', description: '无关' }, 'char'), 2)
  assert.equal(scoreSlashCommand({ name: 'chapter', description: '无关' }, 'char'), 3)
  assert.equal(scoreSlashCommand({ name: 'zzz', description: 'char 描述命中' }, 'char'), 4)

  assert.deepEqual(
    rankSlashCommands(commands, 'char').map(c => c.name),
    ['char', 'character', 'chapter'],
    'exact match outranks prefix, which outranks fuzzy; non-matches drop out',
  )

  // 描述命中也要能被找到
  assert.deepEqual(rankSlashCommands(commands, '润色').map(c => c.name), ['polish'])

  assert.deepEqual(
    rankSlashCommands(commands, 'zzzz'),
    [],
    'a query matching nothing yields an empty list, not the full list',
  )

  // 同分保持输入顺序，列表不会在继续输入时无理由跳动
  assert.deepEqual(
    rankSlashCommands(
      [
        { name: 'alpha-one', description: 'x' },
        { name: 'alpha-two', description: 'x' },
      ],
      'alpha',
    ).map(c => c.name),
    ['alpha-one', 'alpha-two'],
  )

  // 核心不变量：键盘高亮项与回车插入项必须是同一个命令。
  // ChatInput 只在 rankSlashCommands 的结果上做下标定位，
  // 渲染与导航共用同一份数组，所以重排后两者仍然一致。
  const simulateInput = (items, filterText, keyboardOffset) => {
    const visible = rankSlashCommands(items, filterText)
    const index = Math.min(keyboardOffset, Math.max(visible.length - 1, 0))
    return { highlighted: visible[index], inserted: visible[index] ?? visible[0], visible }
  }

  for (const filterText of ['c', 'ch', 'cha', 'char', 'chapter', 'o', 'p', '润']) {
    for (const offset of [0, 1, 2, 9]) {
      const { highlighted, inserted, visible } = simulateInput(commands, filterText, offset)
      if (visible.length === 0) {
        assert.equal(highlighted, undefined, 'empty result has nothing to highlight')
        continue
      }
      assert.equal(
        highlighted,
        inserted,
        `highlighted and inserted command must match for "${filterText}" at offset ${offset}`,
      )
    }
  }

  // 重排场景：'cha' 时三条都是前缀命中、保持输入顺序；补成 'char' 后
  // 完全相等的 char 反超到首位。高亮下标没变，指向的命令变了，但两者仍一致。
  const beforeReorder = rankSlashCommands(commands, 'cha')
  const afterReorder = rankSlashCommands(commands, 'char')
  assert.deepEqual(beforeReorder.map(c => c.name), ['chapter', 'character', 'char'])
  assert.deepEqual(afterReorder.map(c => c.name), ['char', 'character', 'chapter'])
  assert.notEqual(
    beforeReorder[0],
    afterReorder[0],
    'the list genuinely reorders, so the invariant above covers the reorder case',
  )
} finally {
  await rm(tempDir, { recursive: true, force: true })
}
