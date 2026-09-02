// 语料浏览维度枚举（单一来源）。
// 须与后端 ReferenceCorpusFeatureFamilies / 技法标本 family 词表保持一致；
// 轻量化聚焦方案第四节新增 scene/trope 场景级 family 时，在此同步扩展。
export const OBSERVATION_FAMILIES = [
  'emotion',
  'sensory',
  'rhythm',
  'syntax',
  'action',
  'interaction',
  'pov',
  'rhetoric',
  'hook',
  'narrative',
] as const

export const SPECIMEN_FAMILIES = [
  'emotion',
  'rhetoric',
  'rhythm',
  'action',
  'structure',
] as const
