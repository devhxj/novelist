import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, readFile, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-bridge-errors-'))
const outputFile = path.join(tempDir, 'bridgeErrors.mjs')

try {
  // 用 stdin 入口把 bridgeErrors 与 BridgeError 类从同一次打包里导出，
  // 保证测试里 new 出来的实例能通过 instanceof 检查。
  // esbuild 的 stdin 无法解析 file:// URL，这里给绝对盘符路径（正斜杠）。
  const abs = (p) => path.resolve(p).replaceAll('\\', '/')
  await build({
    stdin: {
      contents: [
        `export { bridgeErrorGuide, describeBridgeError } from ${JSON.stringify(abs('src/lib/novelist/bridgeErrors.ts'))};`,
        `export { BridgeError } from ${JSON.stringify(abs('src/lib/novelist/bridge.ts'))};`,
      ].join('\n'),
      resolveDir: process.cwd(),
      loader: 'js',
    },
    outfile: outputFile,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'es2023',
    logLevel: 'silent',
  })

  const { bridgeErrorGuide, describeBridgeError, BridgeError } = await import(pathToFileURL(outputFile))

  // 从后端契约源码提取 ReferenceMaterializationErrorCodes 的全部取值，
  // 保证前端映射对后端错误码全集覆盖（U11 验收口径）。
  const contractsSource = await readFile(
    path.join('..', 'src', 'Novelist.Contracts', 'App', 'ReferenceMaterializationPayloads.cs'),
    'utf8',
  )
  const errorCodesClass = contractsSource.match(
    /public static class ReferenceMaterializationErrorCodes\s*\{([\s\S]*?)\n\}/,
  )
  assert(errorCodesClass, 'ReferenceMaterializationErrorCodes class must exist in contracts')
  const backendCodes = [...errorCodesClass[1].matchAll(/public const string \w+ = "([^"]+)"/g)].map((m) => m[1])
  assert(backendCodes.length >= 19, `expected the full error-code list, found ${backendCodes.length}`)

  const missing = backendCodes.filter((code) => !bridgeErrorGuide[code])
  assert.deepEqual(missing, [], 'every backend materialization error code must have a guide entry')

  for (const [code, guide] of Object.entries(bridgeErrorGuide)) {
    assert(guide.message.length > 0 && guide.action.length > 0, `${code} must carry both message and action`)
  }

  // 命中映射：友好消息 + 行动建议，后端原始消息降级为折叠诊断。
  const known = describeBridgeError(
    new BridgeError('LLM provider returned 502.', { code: 'materialization_llm_request_failed' }),
    '兜底文案',
  )
  assert.equal(known.code, 'materialization_llm_request_failed')
  assert.match(known.message, /大模型请求失败/)
  assert.match(known.message, /稍后重试/)
  assert.equal(known.detail, 'LLM provider returned 502.', 'backend message must survive as folded diagnostic')

  // 未命中映射：保持透传优先的老行为，detail 为 null。
  const unknown = describeBridgeError(
    new BridgeError('原始后端消息', { code: 'materialization_some_future_code' }),
    '兜底文案',
  )
  assert.equal(unknown.message, '原始后端消息')
  assert.equal(unknown.detail, null)

  const plain = describeBridgeError(new Error('普通错误'), '兜底文案')
  assert.equal(plain.message, '普通错误')
  assert.equal(plain.code, null)
  assert.equal(plain.detail, null)

  console.log('bridge-errors contract tests passed')
} finally {
  await rm(tempDir, { recursive: true, force: true })
}
