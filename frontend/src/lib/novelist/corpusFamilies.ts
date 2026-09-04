// 语料浏览维度枚举（单一来源）。
// OBSERVATION_FAMILIES 与后端 ReferenceCorpusFeatureFamilies.All 逐项一致：
// 句子级 syntax/rhythm/sensory/emotion/rhetoric，段落级 narrative/pov/action/character/commercial，
// 场景级 scene/trope。词表漂移由 BridgeFrontendContractTests 守卫（E1）。
export const OBSERVATION_FAMILIES = [
  'syntax',
  'rhythm',
  'sensory',
  'emotion',
  'rhetoric',
  'narrative',
  'pov',
  'action',
  'character',
  'commercial',
  'scene',
  'trope',
] as const

// 技法标本的 technique_family 由分析模型自由命名（后端不设白名单），
// 这里只是筛选下拉的常用预设，不是封闭词表。
export const SPECIMEN_FAMILIES = [
  'emotion',
  'rhetoric',
  'rhythm',
  'action',
  'narrative',
] as const
