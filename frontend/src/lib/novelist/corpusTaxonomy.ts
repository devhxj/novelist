// 语料 taxonomy 中文映射（B1/B2，2026-09-03 评审）。
// 数据契约保持枚举值不变：仅展示层走映射，未命中键回退原文。
// 词表来源：ReferenceMaterializationChatCompletionQualifier 的 Allowed* 词表
// 与材料覆盖度 facet 维度（SqliteReferenceAnchorService.MaterialCoverageFacetColumns），
// 两端词表扩展时须同步本文件。

export const MATERIAL_TYPE_LABELS: Record<string, string> = {
  sentence: '句子',
  passage: '段落',
  scene: '场景',
}

export const RUN_STATUS_LABELS: Record<string, string> = {
  queued: '排队中',
  running: '进行中',
  completed: '已完成',
  failed: '失败',
  cancelled: '已取消',
}

export const REVIEW_STATE_LABELS: Record<string, string> = {
  unverified: '未复核',
  low_confidence: '低置信',
  confirmed: '已确认',
  rejected: '已驳回',
}

// 覆盖度地图的维度键（MaterialCoverageFacetColumns）。
export const COVERAGE_FACET_LABELS: Record<string, string> = {
  material_type: '素材类型',
  function_tag: '叙事功能',
  emotion_tag: '情绪机制',
  scene_tag: '场景节拍',
  pov_tag: '视角',
  technique_tag: '技法',
}

export const FAMILY_LABELS: Record<string, string> = {
  // 句子级
  syntax: '句法',
  rhythm: '节奏',
  sensory: '感官',
  emotion: '情绪',
  rhetoric: '修辞',
  // 段落级
  narrative: '叙事',
  pov: '视角',
  action: '动作',
  character: '人物',
  commercial: '商业性',
  // 场景级
  scene: '场景',
  trope: '桥段',
}

export const FEATURE_VALUE_LABELS: Record<string, string> = {
  // observation feature_key（ReferenceCorpusFeatureSchemas 各 family 的 feature_key 枚举，E1 补齐）
  action_chain: '动作链',
  dynamics: '关系动态',
  mechanics: '钩子机制',
  emotion_state: '情绪状态',
  narrative_function: '叙事功能',
  perspective: '视角',
  rhetorical_device: '修辞装置',
  length_band: '句长档位',
  cadence: '节奏型',
  pause_density: '停顿密度',
  ending_weight: '句尾权重',
  scene_structure: '场景结构',
  sentence_pattern: '句式',
  structure_complexity: '结构复杂度',
  pos_profile: '词性画像',
  trope_pattern: '桥段模式',
  // narrative functions（叙事功能）
  characterization: '人物塑造',
  conflict: '冲突',
  hook: '钩子',
  payoff: '兑现',
  pacing: '节奏',
  relationship_pressure: '关系张力',
  reveal: '揭示',
  setup: '铺垫',
  transition: '过渡',
  turn: '转折',
  worldbuilding: '世界观',
  // emotion mechanics（情绪机制）
  anger: '愤怒',
  anticipation: '期待',
  desire: '渴望',
  escalation: '升级',
  fear: '恐惧',
  grief: '悲恸',
  relief: '释然',
  reversal: '反转',
  release: '宣泄',
  shame: '羞耻',
  suppression: '压抑',
  tension: '张力',
  // pov（视角）
  first_person: '第一人称',
  close_third: '紧贴第三人称',
  limited_third: '限知第三人称',
  omniscient: '全知',
  second_person: '第二人称',
  mixed: '混合',
  // techniques（技法）
  callback: '呼应',
  contrast: '对比',
  delayed_reaction: '延迟反应',
  dialogue_turn: '对话转折',
  foreshadowing: '伏笔',
  free_indirect_discourse: '自由间接引语',
  rhythm_shift: '节奏切换',
  sensory_detail: '感官细节',
  subtext: '潜台词',
  withholding: '留白',
  // scene beat roles（场景节拍）
  aftermath_beat: '余波节拍',
  escalation_beat: '升级节拍',
  hook_beat: '钩子节拍',
  opening_pressure_beat: '开场压力节拍',
  payoff_beat: '兑现节拍',
  transition_beat: '过渡节拍',
  turn_beat: '转折节拍',
  // character relations（人物关系）
  alliance: '同盟',
  antagonism: '敌对',
  authority: '权威',
  dependency: '依赖',
  distance: '疏离',
  intimacy: '亲密',
  mentorship: '师承',
  mistrust: '猜疑',
  obligation: '义务',
  rivalry: '竞争',
  // causal information roles（因果信息角色）
  cause: '起因',
  concealment: '隐瞒',
  consequence: '后果',
  constraint: '制约',
  decision: '抉择',
  evidence: '证据',
  trigger: '触发',
}

/** 取映射标签；未命中回退原始键（契约键不丢失）。 */
export function taxonomyLabel(map: Record<string, string>, key: string | null | undefined): string {
  if (!key) return ''
  if (!(key in map)) {
    // E1：未知键即词表漂移的信号。开发构建下告警让漂移可见；
    // 运行时仍回退原始键——桥段等开放词表会随积累生长，不能当错误渲染。
    if (import.meta.env.DEV) {
      console.warn(`[taxonomyLabel] 词表未覆盖键 "${key}"——若属封闭词表，请同步 corpusTaxonomy/corpusFamilies 与后端真值。`)
    }
    return key
  }
  return map[key]
}
