import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { pathToFileURL } from 'node:url'
import { mkdtemp, rm } from 'node:fs/promises'
import path from 'node:path'
import os from 'node:os'

const tempDir = await mkdtemp(path.join(os.tmpdir(), 'novelist-embedding-models-'))
const outputFile = path.join(tempDir, 'embedding-models.mjs')

try {
  await build({
    entryPoints: ['src/lib/novelist/embeddingModels.ts'],
    outfile: outputFile,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'es2023',
    logLevel: 'silent',
  })

  const { resolveFixedEmbeddingDimensions, normalizeEmbeddingDimensionsForModel } = await import(
    pathToFileURL(outputFile).href
  )

  await (async () => {
    // 固定维度模型：按模型 ID 最后一段匹配（大小写不敏感），返回固定维度。
    assert.equal(resolveFixedEmbeddingDimensions('BAAI/bge-m3'), 1024)
    assert.equal(resolveFixedEmbeddingDimensions('baai/bge-m3'), 1024)
    assert.equal(resolveFixedEmbeddingDimensions('bge-m3'), 1024)
    assert.equal(resolveFixedEmbeddingDimensions('  BAAI/bge-m3  '), 1024)

    // 非固定维度模型与空值返回 null。
    assert.equal(resolveFixedEmbeddingDimensions('text-embedding-3-small'), null)
    assert.equal(resolveFixedEmbeddingDimensions('BAAI/bge-large-zh-v1.5'), null)
    assert.equal(resolveFixedEmbeddingDimensions(''), null)

    // 固定维度模型的 dimensions 归一为 null（请求不携带 dimensions 参数），其余保持原值。
    assert.equal(normalizeEmbeddingDimensionsForModel('BAAI/bge-m3', 1024), null)
    assert.equal(normalizeEmbeddingDimensionsForModel('BAAI/bge-m3', null), null)
    assert.equal(normalizeEmbeddingDimensionsForModel('text-embedding-3-small', 1024), 1024)
    assert.equal(normalizeEmbeddingDimensionsForModel('text-embedding-3-small', null), null)
  })()
} finally {
  await rm(tempDir, { recursive: true, force: true })
}
