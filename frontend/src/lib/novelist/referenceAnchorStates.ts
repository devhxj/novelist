// 参考书导入状态 → 作者可读标签与可用性（U12）。
// 状态清单以 src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs 的
// ReferenceAnchorBuildStates 为准，契约由 tests/reference-anchor-states.test.mjs 守住：
// 后端新增状态时这里必须同步补齐，不许落回兜底。

export type ReferenceAnchorTone = 'done' | 'failed' | 'working' | 'pending'

export interface ReferenceAnchorState {
  label: string
  tone: ReferenceAnchorTone
  /** 语料动线上是否可以直接选用（进入切分/材料化/检索）。 */
  usable: boolean
}

export const referenceAnchorStates: Record<string, ReferenceAnchorState> = {
  created: { label: '待处理', tone: 'pending', usable: false },
  importing: { label: '导入中', tone: 'working', usable: false },
  source_imported: { label: '来源已导入', tone: 'working', usable: false },
  segmenting: { label: '章节切分中', tone: 'working', usable: false },
  segments_built: { label: '章节切分完成', tone: 'working', usable: false },
  extracting_materials: { label: '语料抽取中', tone: 'working', usable: false },
  materials_extracted: { label: '语料抽取完成', tone: 'working', usable: false },
  detecting_slots: { label: '插槽识别中', tone: 'working', usable: false },
  slots_detected: { label: '插槽识别完成', tone: 'working', usable: false },
  embedding: { label: '生成向量中', tone: 'working', usable: false },
  ready: { label: '已导入', tone: 'done', usable: true },
  failed_import: { label: '导入失败', tone: 'failed', usable: false },
  failed_segmenting: { label: '章节切分失败', tone: 'failed', usable: false },
  failed_extraction: { label: '语料抽取失败', tone: 'failed', usable: false },
  failed_slotting: { label: '插槽识别失败', tone: 'failed', usable: false },
  failed_embedding: { label: '向量化失败', tone: 'failed', usable: false },
  cancelled: { label: '已取消', tone: 'failed', usable: false },
  stale: { label: '来源已变化', tone: 'pending', usable: false },
}

export function describeAnchorStatus(status: string | undefined | null): ReferenceAnchorState {
  const known = status ? referenceAnchorStates[status] : undefined
  if (known) return known
  // 未知状态（后端新增而前端滞后）：不能误标成可用，兜底为待处理。
  return { label: '待处理', tone: 'pending', usable: false }
}
