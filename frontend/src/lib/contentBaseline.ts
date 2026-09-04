// 章节保存的基线令牌（U1）。
// 与后端 src/Novelist.Core/App/ChapterContentBaselineHash.cs 逐字节一致：
// FNV-1a 32 位，按 UTF-16 码元（charCodeAt）迭代，输出 "fnv1a:{8位小写hex}:{长度}"。
// 两端必须同时改；Novelist.Tests/Bridge/BridgeFrontendContractTests 有守卫测试。

/** 基线令牌：内容保存时的"我读到的那份"指纹。 */
export function contentBaselineHash(content: string): string {
  let hash = 2166136261
  for (let index = 0; index < content.length; index += 1) {
    hash ^= content.charCodeAt(index)
    hash = Math.imul(hash, 16777619)
  }
  return `fnv1a:${(hash >>> 0).toString(16).padStart(8, '0')}:${content.length}`
}

/** 诊断摘要用的裸 FNV-1a 十六进制（保持既有 diagnostic 字段格式不变）。 */
export function fnv1a32Hex(content: string): string {
  let hash = 2166136261
  for (let index = 0; index < content.length; index += 1) {
    hash ^= content.charCodeAt(index)
    hash = Math.imul(hash, 16777619)
  }
  return (hash >>> 0).toString(16).padStart(8, '0')
}
