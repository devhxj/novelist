// 语料浏览维度枚举（单一来源）。
// 须与后端 ReferenceCorpusFeatureFamilies / 技法标本 family 词表保持一致；
// scene/trope 为场景级 family（方案 §4），分析产出随真实语料积累。
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
  'scene',
  'trope',
] as const

export const SPECIMEN_FAMILIES = [
  'emotion',
  'rhetoric',
  'rhythm',
  'action',
  'structure',
] as const
