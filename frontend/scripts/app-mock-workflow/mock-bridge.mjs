import {
  createDefaultGitMockFixtures,
  createMockGitService,
  getGitCommitFiles,
  getGitCommits,
  getGitFileDiff,
} from './mock-git-service.mjs'

export function settingsFixture(lastNovelId) {
  return {
    ID: 1,
    last_novel_id: lastNovelId,
    selected_model_key: 'mock/gpt',
    reasoning_effort: 'high',
    approval_mode: 'manual',
    chat_panel_width: 360,
    last_session_id: '',
    user_name: 'Mock User',
    git_author_name: '',
    git_author_email: '',
    update_check_enabled: false,
    update_check_endpoint_url: '',
    update_check_dismissed_version: '',
    update_check_last_checked_at: null,
    sidebar_width: 280,
    metadata_panel_width: 320,
    window_x: null,
    window_y: null,
    window_width: 1280,
    window_height: 840,
    window_maximized: false,
  }
}

export function installConfigurableAppMockBridge(options = {}) {
  const now = '2026-07-05T12:00:00.000Z'
  const referenceCandidateText = '林岚没有立刻抬头。杯底那半圈水痕贴着木纹，像刚被雨夜重新描过一遍；她只把指尖收紧，确认门外的人还不知道这条线索。'
  const receivers = new Set()
  const defaultSettings = {
    ID: 1,
    last_novel_id: 42,
    selected_model_key: 'mock/gpt',
    reasoning_effort: 'high',
    approval_mode: 'manual',
    chat_panel_width: 360,
    last_session_id: '',
    user_name: 'Mock User',
    git_author_name: '',
    git_author_email: '',
    update_check_enabled: false,
    update_check_endpoint_url: '',
    update_check_dismissed_version: '',
    update_check_last_checked_at: null,
    sidebar_width: 280,
    metadata_panel_width: 320,
    window_x: null,
    window_y: null,
    window_width: 1280,
    window_height: 840,
    window_maximized: false,
  }
  const persistedSettings = readPersistedMockSettings()
  const defaultNovel = {
    id: 42,
    title: '全局回归小说',
    genre: '悬疑',
    description: 'App-wide Playwright fixture',
    created_at: now,
    updated_at: now,
  }
  const defaultChapters = [
    {
      id: 1,
      novel_id: 42,
      chapter_number: 1,
      title: '雨夜线索',
      summary: '林岚在雨夜发现桌面痕迹。',
      word_count: 1200,
      file_path: 'chapters/1.md',
      created_at: now,
      updated_at: now,
    },
    {
      id: 2,
      novel_id: 42,
      chapter_number: 2,
      title: '旧城门',
      summary: '暗号被雨水冲淡。',
      word_count: 980,
      file_path: 'chapters/2.md',
      created_at: now,
      updated_at: now,
    },
    {
      id: 3,
      novel_id: 42,
      chapter_number: 3,
      title: '钟楼回声',
      summary: '钟楼里的回声指向旧门后的脚印。',
      word_count: 1120,
      file_path: 'chapters/3.md',
      created_at: now,
      updated_at: now,
    },
    {
      id: 4,
      novel_id: 42,
      chapter_number: 4,
      title: '暗号复盘',
      summary: '林岚复盘暗号和水痕之间的关系。',
      word_count: 1050,
      file_path: 'chapters/4.md',
      created_at: now,
      updated_at: now,
    },
    {
      id: 5,
      novel_id: 42,
      chapter_number: 5,
      title: '雨线尽头',
      summary: '雨线尽头出现新的目击证词。',
      word_count: 990,
      file_path: 'chapters/5.md',
      created_at: now,
      updated_at: now,
    },
    {
      id: 6,
      novel_id: 42,
      chapter_number: 6,
      title: '门后停顿',
      summary: '门后的停顿让线索重新排序。',
      word_count: 1180,
      file_path: 'chapters/6.md',
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultCharacters = [
    {
      id: 1,
      novel_id: 42,
      name: '林岚',
      description: '旧城门案件的调查者。',
      personality: '谨慎、克制',
      abilities: JSON.stringify(['观察', '推理']),
      created_at: now,
      updated_at: now,
    },
    {
      id: 2,
      novel_id: 42,
      name: '周砚',
      description: '掌握旧城暗号的线人。',
      personality: '沉默',
      abilities: JSON.stringify(['记忆']),
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultLocations = [
    {
      id: 1,
      novel_id: 42,
      name: '旧城门',
      location_type: '城市',
      description: '雨夜里暗号被冲淡的城门。',
      detail_json: '{}',
      parent_location_id: 0,
      tags: JSON.stringify(['雨夜', '线索']),
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultStoryArcs = [
    {
      id: 1,
      novel_id: 42,
      name: '雨夜调查线',
      description: '围绕桌面水痕推进。',
      arc_type: 'main',
      importance: 5,
      status: 'active',
      reactivate_at: '',
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultArcNodes = [
    {
      id: 1,
      novel_id: 42,
      story_arc_id: 1,
      title: '桌面水痕触发调查',
      description: '林岚发现水痕但没有立刻揭示判断。',
      target_chapter: 1,
      actual_chapter: 0,
      status: 'pending',
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultChapterPlans = [
    { novel_id: 42, scope: 'next', content: '下一章继续旧城门调查。' },
    { novel_id: 42, scope: 'near', content: '近期回收桌面水痕。' },
    { novel_id: 42, scope: 'far', content: '远期揭示暗号来源。' },
  ]
  const defaultTimelineEntries = [
    {
      id: 1,
      novel_id: 42,
      category: 'foreshadowing',
      status: 'pending',
      title: '桌面水痕',
      content: '杯底留下半圈水痕，提示有人刚离开。',
      detail_json: '{}',
      target_chapter: 1,
      importance: 4,
      source_chapter_id: 1,
      source: 'user',
      resolved_chapter_id: 0,
      created_at: now,
      updated_at: now,
    },
  ]
  const defaultReaderPerspectives = [
    {
      id: 1,
      novel_id: 42,
      type: 'known',
      content: '读者知道林岚正在调查旧城门。',
      related_truth: '旧城门暗号和桌面水痕相关。',
      planted_chapter: 1,
      revealed_chapter: 0,
      created_at: now,
    },
  ]
  const defaultPreferences = {
    global: [
      {
        id: 1,
        novel_id: 0,
        is_global: true,
        category: '叙事',
        content: '保持受限视角，不提前暴露门外身份。',
        created_at: now,
      },
    ],
    novel: [
      {
        id: 2,
        novel_id: 42,
        is_global: false,
        category: '节奏',
        content: '雨夜场景多用动作间隔承压。',
        created_at: now,
      },
    ],
  }
  const defaultWritingActivity = [
    { date: '2026-07-01', words: 800 },
    { date: '2026-07-02', words: 1200 },
  ]
  const defaultWritingStats = {
    total_words: 2180,
    total_days_active: 2,
    current_streak: 2,
    longest_streak: 2,
    total_novels: 1,
    total_chapters: 2,
  }
  const defaultSkills = [
    {
      name: '节奏控制',
      description: '控制动作、停顿和信息释放。',
      category: '写作技法',
      mode: 'manual',
      author: 'mock',
      version: 1,
      source: 'novel',
    },
    {
      name: '对话潜台词',
      description: '用潜台词替代解释。',
      category: '写作技法',
      mode: 'manual',
      author: 'mock',
      version: 1,
      source: 'builtin',
    },
  ]
  const defaultStyleSamples = [
    {
      sample_id: 1,
      novel_id: null,
      is_global: true,
      name: '全局雨夜节奏',
      content: '“别回头。”雨声压着窗沿。她想了想，只把灯关掉。\n\n脚步声停在门外。',
      preview: '“别回头。”雨声压着窗沿。她想了想，只把灯关掉。 脚步声停在门外。',
      tags: ['雨夜', '克制', '对白'],
      stats_schema_version: 'style_sample_stats_v2',
      stats: styleSampleStats({
        characterCount: 46,
        wordCount: 26,
        sentenceCount: 4,
        sentenceLengths: [5, 9, 12, 8],
        averageSentenceChars: 11.5,
        sentenceLengthStdDev: 2.6926,
        punctuationPer100Chars: 17.3913,
        quoteDensity: 4.3478,
        paragraphCount: 2,
        averageParagraphChars: 23,
        dialogueRatio: 0.1739,
        interiorityRatio: 0.4565,
        sensoryRatio: 0.7826,
      }),
      source_metadata: { source_type: 'manual', source_id: 'global-rain', source_hash: 'hash-style-001' },
      created_at: '2026-07-05T11:58:00.000Z',
      updated_at: '2026-07-05T12:03:00.000Z',
    },
    {
      sample_id: 2,
      novel_id: 42,
      is_global: false,
      name: '近身内心动作',
      content: '他没有回头，只把手按在门把上。心里那点犹豫，像潮湿木头里没灭的火。',
      preview: '他没有回头，只把手按在门把上。心里那点犹豫，像潮湿木头里没灭的火。',
      tags: ['内心', '克制'],
      stats_schema_version: 'style_sample_stats_v2',
      stats: styleSampleStats({
        characterCount: 35,
        wordCount: 31,
        sentenceCount: 2,
        sentenceLengths: [15, 19],
        averageSentenceChars: 17.5,
        sentenceLengthStdDev: 2,
        punctuationPer100Chars: 8.5714,
        quoteDensity: 0,
        paragraphCount: 1,
        averageParagraphChars: 35,
        dialogueRatio: 0,
        interiorityRatio: 0.5429,
        sensoryRatio: 0.5429,
      }),
      source_metadata: { source_type: 'chapter_selection', source_id: '42:1', source_hash: 'hash-style-002' },
      created_at: '2026-07-05T11:57:00.000Z',
      updated_at: '2026-07-05T12:02:00.000Z',
    },
    {
      sample_id: 3,
      novel_id: 42,
      is_global: false,
      name: '段落留白记录',
      content: '桌上的水痕还在。\n\n林岚没有碰杯子。她只是把袖口往下拉。',
      preview: '桌上的水痕还在。 林岚没有碰杯子。她只是把袖口往下拉。',
      tags: ['留白', '动作'],
      stats_schema_version: 'style_sample_stats_v2',
      stats: styleSampleStats({
        characterCount: 29,
        wordCount: 24,
        sentenceCount: 3,
        sentenceLengths: [8, 8, 11],
        averageSentenceChars: 9.6667,
        sentenceLengthStdDev: 1.4142,
        punctuationPer100Chars: 10.3448,
        quoteDensity: 0,
        paragraphCount: 2,
        averageParagraphChars: 14.5,
        dialogueRatio: 0,
        interiorityRatio: 0,
        sensoryRatio: 0.2759,
      }),
      source_metadata: { source_type: 'manual', source_id: 'paragraph-gap', source_hash: 'hash-style-003' },
      created_at: '2026-07-05T11:56:00.000Z',
      updated_at: '2026-07-05T12:01:00.000Z',
    },
  ]
  const defaultContentByPath = {
    'novelist.md': '## 当前状态\n林岚正在调查旧城门。',
    'chapters/1.md': '林岚在雨夜旧宅门前停住。\n\n她看见桌上的水痕。',
    'chapters/2.md': '旧城门下，暗号被雨水冲淡。',
    'chapters/3.md': '钟楼里的回声很轻，脚印停在旧门背后。',
    'chapters/4.md': '林岚重新排列暗号、杯底水痕和钟楼时间。',
    'chapters/5.md': '雨线尽头的目击者只说自己看见了灯。',
    'chapters/6.md': '门后的停顿被记录下来，没有人提前下结论。',
    'skills/rhythm.md': '---\nname: 节奏控制\n---\n保持停顿和动作之间的张力。',
    'skills/节奏控制.md': '---\nname: 节奏控制\n---\n保持停顿和动作之间的张力。',
    '/builtin/skills/dialogue.md': '---\nname: 对话潜台词\n---\n用话外之意推动场景。',
  }
  const defaultReferenceAnchors = [
    {
      anchor_id: 101,
      novel_id: 42,
      title: '全局雨夜参考',
      author: 'Mock Author',
      source_path: 'D:\\books\\rain-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'user_verified',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['雨夜', '全局语料'],
      source_file_hash: 'hash-anchor-app-001',
      build_version: 'mock-reference-v1',
      status: 'ready',
      created_at: now,
      updated_at: now,
    },
    {
      anchor_id: 102,
      novel_id: 42,
      title: '失败导入参考',
      author: 'Mock Failed Author',
      source_path: 'D:\\books\\failed-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'imported',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['失败恢复', '处理明细'],
      source_file_hash: 'hash-anchor-failed-001',
      build_version: 'mock-reference-v1',
      status: 'failed_import',
      created_at: now,
      updated_at: now,
    },
    {
      anchor_id: 103,
      novel_id: 42,
      title: '重试仍失败参考',
      author: 'Mock Retry Failure Author',
      source_path: 'D:\\books\\retry-failure-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'imported',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['失败恢复', '重试失败'],
      source_file_hash: 'unavailable:hash-anchor-retry-failed-001',
      build_version: 'mock-reference-v1',
      status: 'failed_import',
      created_at: now,
      updated_at: now,
    },
    {
      anchor_id: 104,
      novel_id: 42,
      title: '重启恢复参考',
      author: 'Mock Restart Recovery Author',
      source_path: 'D:\\books\\restart-recovery-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'user_verified',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['重启恢复', '中断处理'],
      source_file_hash: 'hash-anchor-restart-recovered-001',
      build_version: 'mock-reference-v1',
      status: 'ready',
      created_at: now,
      updated_at: now,
    },
    {
      anchor_id: 105,
      novel_id: 42,
      title: '抽取失败参考',
      author: 'Mock Extraction Failure Author',
      source_path: 'D:\\books\\failed-extraction-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'user_verified',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['抽取失败', '来源片段'],
      source_file_hash: 'hash-anchor-extraction-failed-001',
      build_version: 'mock-reference-v1',
      status: 'failed_extraction',
      created_at: now,
      updated_at: now,
    },
    {
      anchor_id: 106,
      novel_id: 42,
      title: '槽位失败参考',
      author: 'Mock Slotting Failure Author',
      source_path: 'D:\\books\\failed-slotting-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'user_verified',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['槽位失败', '材料保留'],
      source_file_hash: 'hash-anchor-slotting-failed-001',
      build_version: 'mock-reference-v1',
      status: 'failed_slotting',
      created_at: now,
      updated_at: now,
    },
    {
      anchor_id: 107,
      novel_id: 42,
      title: '缺源重启参考',
      author: 'Mock Missing Source Startup Author',
      source_path: 'D:\\books\\missing-startup-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'user_verified',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['重启恢复', '缺源失败', '材料保留'],
      source_file_hash: 'hash-anchor-missing-startup-prior-001',
      build_version: 'mock-reference-v1',
      status: 'failed_import',
      created_at: now,
      updated_at: now,
    },
    {
      anchor_id: 108,
      novel_id: 42,
      title: '槽位重启参考',
      author: 'Mock Slots Startup Recovery Author',
      source_path: 'D:\\books\\slots-startup-reference.md',
      source_kind: 'markdown',
      license_status: 'user_provided',
      visibility: 'workspace',
      source_trust: 'user_verified',
      owner_scope: 'workspace_corpus',
      owner_novel_id: null,
      user_tags: ['重启恢复', '槽位恢复', '向量索引'],
      source_file_hash: 'hash-anchor-slots-startup-recovered-001',
      build_version: 'mock-reference-v1',
      status: 'ready',
      created_at: now,
      updated_at: now,
    },
  ]
  const longMaterialLeakSentinel = '__FULL_MATERIAL_SHOULD_NOT_RENDER__'
  const longRainMaterialText = [
    '雨夜线索从旧城门开始，雨声压着门槛，林岚只看见杯底半圈水痕，没有急着给出判断。',
    '她先把杯沿的缺口、窗台的潮气、墙根一串被踩乱的泥点按时间顺序记下，又故意把最像答案的那一项留到最后。',
    '这段素材用于验证列表和明细只展示有界预览，完整素材正文不应直接进入任何可见 DOM。',
    longMaterialLeakSentinel,
  ].join('')
  const defaultReferenceMaterials = [
    {
      material_id: 'mock-mat-rain-001',
      anchor_id: 101,
      source_segment_id: 'mock-seg-rain-001',
      material_type: 'sentence',
      function_tag: 'environment',
      emotion_tag: 'restrained',
      scene_tag: 'rain_threshold',
      pov_tag: 'close',
      technique_tag: 'delayed_reaction',
      function_confidence: 0.91,
      emotion_confidence: 0.88,
      pov_confidence: 0.9,
      text: longRainMaterialText,
      source_hash: 'hash-mock-material-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.92,
        function: 0.9,
        prose_duty: 0.86,
      },
    },
    {
      material_id: 'mock-mat-rain-002',
      anchor_id: 101,
      source_segment_id: 'mock-seg-rain-002',
      material_type: 'passage',
      function_tag: 'emotion_evidence',
      emotion_tag: 'suspense',
      scene_tag: 'unknown',
      pov_tag: 'close',
      technique_tag: 'subtext',
      function_confidence: 0.72,
      emotion_confidence: 0.84,
      pov_confidence: 0.62,
      text: '灯影在门缝里停了一瞬，她把袖口往下拉，只把这处停顿记进本子。',
      source_hash: 'hash-mock-material-002',
      extractor_version: 'mock-reference-v1',
      user_verified: false,
      created_at: now,
      score_components: {
        lexical: 0.84,
        function: 0.86,
        prose_duty: 0.82,
      },
    },
    {
      material_id: 'mock-mat-restart-001',
      anchor_id: 104,
      source_segment_id: 'mock-seg-restart-001',
      material_type: 'sentence',
      function_tag: 'continuity',
      emotion_tag: 'restrained',
      scene_tag: 'restart_recovery',
      pov_tag: 'close',
      technique_tag: 'delayed_reaction',
      function_confidence: 0.93,
      emotion_confidence: 0.89,
      pov_confidence: 0.91,
      text: '重启后恢复的语料只保留雨夜门槛和杯底水痕的可审计摘要，不回写任何完整来源正文。',
      source_hash: 'hash-mock-material-restart-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.9,
        function: 0.88,
        prose_duty: 0.84,
      },
    },
    {
      material_id: 'mock-mat-slot-001',
      anchor_id: 106,
      source_segment_id: 'mock-seg-slot-001',
      material_type: 'sentence',
      function_tag: 'clue',
      emotion_tag: 'restrained',
      scene_tag: 'slot_failure',
      pov_tag: 'close',
      technique_tag: 'subtext',
      function_confidence: 0.94,
      emotion_confidence: 0.89,
      pov_confidence: 0.9,
      text: '槽位检测失败前已经生成的材料仍可检查，只显示线索摘要和来源片段预览，不暴露完整来源正文。',
      source_hash: 'hash-mock-material-slot-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.89,
        function: 0.91,
        prose_duty: 0.83,
      },
    },
    {
      material_id: 'mock-mat-missing-startup-001',
      anchor_id: 107,
      source_segment_id: 'mock-seg-missing-startup-001',
      material_type: 'sentence',
      function_tag: 'continuity',
      emotion_tag: 'unease',
      scene_tag: 'missing_source_restart',
      pov_tag: 'close',
      technique_tag: 'afterbeat',
      function_confidence: 0.93,
      emotion_confidence: 0.88,
      pov_confidence: 0.91,
      text: '源文件丢失后的启动恢复保留旧材料摘要，可搜索、可检查，但诊断不暴露本地路径或完整来源正文。',
      source_hash: 'hash-mock-material-missing-startup-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.9,
        function: 0.89,
        prose_duty: 0.84,
      },
    },
    {
      material_id: 'mock-mat-slots-startup-001',
      anchor_id: 108,
      source_segment_id: 'mock-seg-slots-startup-001',
      material_type: 'sentence',
      function_tag: 'continuity',
      emotion_tag: 'restrained',
      scene_tag: 'slots_startup_recovery',
      pov_tag: 'close',
      technique_tag: 'slot_continuity',
      function_confidence: 0.94,
      emotion_confidence: 0.9,
      pov_confidence: 0.91,
      text: '槽位检测后重启恢复的材料摘要保留{{object}}槽位和向量索引状态，可定位但不暴露完整来源正文。',
      source_hash: 'hash-mock-material-slots-startup-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.91,
        function: 0.9,
        prose_duty: 0.85,
      },
    },
  ]
  const defaultGitFixtures = createDefaultGitMockFixtures()
  const state = {
    calls: [],
    emittedEvents: [],
    appliedFaults: [],
    activeNovelId: options.settings?.last_novel_id ?? defaultSettings.last_novel_id,
    nextNovelId: 43,
    nextChapterId: 7,
    nextCharacterId: 3,
    nextLocationId: 2,
    nextStoryArcId: 2,
    nextArcNodeId: 2,
    nextTimelineEntryId: 2,
    nextReaderPerspectiveId: 2,
    nextPreferenceId: 3,
    nextStyleSampleId: 4,
    nextStyleSkillExtractionDelayMs: 0,
    nextStyleSkillExtractionMode: 'success',
    nextNarrativePatternDelayMs: 0,
    nextNarrativePatternMode: 'success',
    nextUpdateCheckMode: options.updateCheckMode ?? 'available',
    nextSessionId: 1,
    nextTurnId: 101,
    searchFailureRecovered: false,
    chatFailureRecovered: false,
    failNextSaveContent: false,
    savedLLMConfig: null,
    savedEmbeddingConfig: null,
    exportedNovels: [],
    savedCovers: [],
    savedAvatars: [],
    failNextStyleSampleDelete: false,
    cancelledStyleSkillExtractionTaskIds: [],
    styleSkillExtractionRuns: [],
    cancelledNarrativePatternTaskIds: [],
    narrativePatternRuns: [],
    narrativePatternTraces: {},
    novelImportRuns: [],
    activeNovelImports: {},
    cancelledNovelImportTaskIds: [],
    createdReferenceAnchors: [],
    referenceAnchors: options.referenceAnchors ?? defaultReferenceAnchors,
    referenceMaterials: options.referenceMaterials ?? defaultReferenceMaterials,
    referenceBuildStatuses: options.referenceBuildStatuses ?? {},
    referenceStyleProfiles: options.referenceStyleProfiles ?? [],
    referenceStyleProfileBuildStatuses: options.referenceStyleProfileBuildStatuses ?? {},
    nextReferenceStyleProfileId: 301,
    referenceBlueprints: {},
    nextReferenceBlueprintId: 701,
    referenceCorpusFeatureAnalysisRuns: [],
    nextReferenceCorpusFeatureAnalysisRunId: 1,
    referenceCorpusTechniqueSpecimenAnalysisRuns: [],
    nextReferenceCorpusTechniqueSpecimenAnalysisRunId: 1,
    referenceOrchestrationRuns: [],
    nextReferenceOrchestrationRunId: 1,
    contentByPath: options.contentByPath ?? defaultContentByPath,
    initialized: options.initialized ?? true,
    novels: options.novels ?? [defaultNovel],
    chaptersByNovelId: options.chaptersByNovelId ?? { 42: defaultChapters },
    settings: options.settings ?? persistedSettings ?? defaultSettings,
    characters: options.characters ?? defaultCharacters,
    locations: options.locations ?? defaultLocations,
    storyArcs: options.storyArcs ?? defaultStoryArcs,
    arcNodes: options.arcNodes ?? defaultArcNodes,
    chapterPlans: options.chapterPlans ?? defaultChapterPlans,
    timelineEntries: options.timelineEntries ?? defaultTimelineEntries,
    readerPerspectives: options.readerPerspectives ?? defaultReaderPerspectives,
    preferences: options.preferences ?? defaultPreferences,
    styleSamples: options.styleSamples ?? defaultStyleSamples,
    sessions: options.sessions ?? [],
    gitCommits: options.gitCommits ?? defaultGitFixtures.commits,
    gitCommitFilesByCommitId: options.gitCommitFilesByCommitId ?? defaultGitFixtures.commitFilesByCommitId,
    gitDiffsByCommitAndPath: options.gitDiffsByCommitAndPath ?? defaultGitFixtures.diffsByCommitAndPath,
    writingActivity: options.writingActivity ?? defaultWritingActivity,
    writingStats: options.writingStats ?? defaultWritingStats,
    skills: options.skills ?? defaultSkills,
    importRecovery: options.importRecovery ?? null,
  }
  state.runtimeWindowMaximized = options.runtimeWindowMaximized ?? (state.settings.window_maximized === true)
  const faultQueues = normalizeFaultQueues(options.faults ?? {})
  Object.defineProperty(state, 'clearFaultQueue', {
    configurable: true,
    enumerable: false,
    value(method) {
      if (method) {
        faultQueues[method] = []
      }
    },
  })

  window.localStorage.removeItem('novelist_tabs_all')
  window.localStorage.setItem('theme', 'light')
  window.confirm = () => Boolean(options.confirmResult)

  Object.defineProperty(window, '__appMockState', {
    configurable: true,
    value: state,
  })

  Object.defineProperty(window, 'external', {
    configurable: true,
    value: {
      sendMessage(message) {
        const envelope = JSON.parse(String(message))
        if (envelope.kind === 'request') {
          window.setTimeout(() => {
            void handleRequest(envelope)
          }, 0)
        }
      },
      receiveMessage(callback) {
        receivers.add(callback)
      },
    },
  })

  async function handleRequest(envelope) {
    try {
      const args = Array.isArray(envelope.payload?.args) ? envelope.payload.args : []
      state.calls.push({ method: envelope.method, args, payload: envelope.payload })
      const fault = nextFault(envelope.method)

      if (fault?.delayMs) {
        await wait(fault.delayMs)
      }

      if (fault?.mode === 'timeout') {
        return
      }

      if (fault?.mode === 'malformed-response') {
        respond({ kind: 'response', id: envelope.id, result: fault.result ?? null })
        return
      }

      if (fault?.mode === 'validation' || fault?.mode === 'storage' || fault?.mode === 'error') {
        respond({
          kind: 'response',
          id: envelope.id,
          ok: false,
          error: faultErrorPayload(fault),
        })
        return
      }

      if (envelope.method === 'SaveContent' && !options.allowSaveContent && !isSkillSaveInput(args[0])) {
        throw new Error('SaveContent is forbidden in the app-wide smoke unless the test explicitly edits content.')
      }

      const result = fault?.hasResult ? fault.result : await route(envelope.method, args)
      state.calls[state.calls.length - 1].result = result
      respond({ kind: 'response', id: envelope.id, ok: true, result })
    } catch (error) {
      respond({
        kind: 'response',
        id: envelope.id,
        ok: false,
        error: {
          code: 'MOCK_BRIDGE_ERROR',
          message: error instanceof Error ? error.message : String(error),
          retryable: false,
        },
      })
    }
  }

  function respond(payload) {
    const message = JSON.stringify(payload)
    for (const receiver of receivers) {
      receiver(message)
    }
  }

  function emit(name, payload) {
    state.emittedEvents.push({ name, payload })
    respond({ kind: 'event', name, payload })
  }

  function normalizeFaultQueues(faults) {
    const queues = {}
    for (const [method, fault] of Object.entries(faults)) {
      queues[method] = Array.isArray(fault) ? [...fault] : [fault]
    }
    return queues
  }

  function nextFault(method) {
    const queue = faultQueues[method]
    if (!queue || queue.length === 0) return null

    const fault = normalizeFault(queue[0])
    if (fault.once !== false) {
      queue.shift()
    }

    state.appliedFaults.push({
      method,
      mode: fault.mode,
      delayMs: fault.delayMs,
      code: fault.code,
      message: fault.message,
    })
    return fault
  }

  function normalizeFault(fault) {
    if (!fault || typeof fault !== 'object') {
      return { mode: 'error', message: 'Mock fixture fault' }
    }

    return {
      mode: String(fault.mode ?? 'success'),
      delayMs: Math.max(0, Number(fault.delayMs ?? 0)),
      code: typeof fault.code === 'string' ? fault.code : '',
      message: typeof fault.message === 'string' ? fault.message : '',
      retryable: fault.retryable === true,
      details: fault.details,
      result: fault.result,
      hasResult: Object.hasOwn(fault, 'result'),
      once: fault.once,
    }
  }

  function faultErrorPayload(fault) {
    if (fault.mode === 'validation') {
      return {
        code: fault.code || 'VALIDATION_ERROR',
        message: fault.message || 'Mock validation error',
        details: fault.details,
        retryable: false,
      }
    }

    if (fault.mode === 'storage') {
      return {
        code: fault.code || 'STORAGE_ERROR',
        message: fault.message || 'Mock storage error',
        details: fault.details,
        retryable: fault.retryable,
      }
    }

    return {
      code: fault.code || 'MOCK_BRIDGE_ERROR',
      message: fault.message || 'Mock bridge error',
      details: fault.details,
      retryable: fault.retryable,
    }
  }

  async function route(method, args) {
    switch (method) {
      case 'IsInitialized':
        if (options.failIsInitialized) throw new Error('初始化状态读取失败')
        return state.initialized
      case 'Initialize':
        state.initialized = true
        state.novels = options.afterInitializeNovels ?? state.novels
        state.settings = options.afterInitializeSettings ?? state.settings
        state.activeNovelId = state.settings.last_novel_id
        return null
      case 'GetSettings': return state.settings
      case 'GetGitAuthorSettings': return {
        name: state.settings.git_author_name ?? '',
        email: state.settings.git_author_email ?? '',
        scope: 'app',
      }
      case 'SaveGitAuthorSettings': return saveGitAuthorSettings(args[0])
      case 'GetUpdateCheckSettings': return {
        enabled: state.settings.update_check_enabled === true,
        endpoint_url: state.settings.update_check_endpoint_url ?? '',
        dismissed_version: state.settings.update_check_dismissed_version ?? '',
        last_checked_at: state.settings.update_check_last_checked_at ?? null,
      }
      case 'SaveUpdateCheckSettings':
        state.settings.update_check_enabled = args[0]?.enabled === true
        state.settings.update_check_endpoint_url = String(args[0]?.endpoint_url ?? '')
        state.settings.update_check_dismissed_version = String(args[0]?.dismissed_version ?? '')
        persistMockSettings()
        return {
          enabled: state.settings.update_check_enabled,
          endpoint_url: state.settings.update_check_endpoint_url,
          dismissed_version: state.settings.update_check_dismissed_version,
          last_checked_at: state.settings.update_check_last_checked_at ?? null,
        }
      case 'CheckForUpdates': return checkForUpdates(args[0])
      case 'GetLayoutSettings': return {
        sidebar_width: state.settings.sidebar_width ?? 280,
        chat_panel_width: state.settings.chat_panel_width ?? 360,
        metadata_panel_width: state.settings.metadata_panel_width ?? 320,
      }
      case 'SaveLayoutSettings':
        state.settings.sidebar_width = Number(args[0]?.sidebar_width ?? state.settings.sidebar_width ?? 280)
        state.settings.chat_panel_width = Number(args[0]?.chat_panel_width ?? state.settings.chat_panel_width ?? 360)
        state.settings.metadata_panel_width = Number(args[0]?.metadata_panel_width ?? state.settings.metadata_panel_width ?? 320)
        persistMockSettings()
        return {
          sidebar_width: state.settings.sidebar_width,
          chat_panel_width: state.settings.chat_panel_width,
          metadata_panel_width: state.settings.metadata_panel_width,
        }
      case 'GetWindowSettings': return {
        x: state.settings.window_x ?? null,
        y: state.settings.window_y ?? null,
        width: state.settings.window_width ?? 1280,
        height: state.settings.window_height ?? 840,
        maximized: state.settings.window_maximized === true,
      }
      case 'SaveWindowSettings':
        state.settings.window_x = args[0]?.x ?? null
        state.settings.window_y = args[0]?.y ?? null
        state.settings.window_width = Number(args[0]?.width ?? state.settings.window_width ?? 1280)
        state.settings.window_height = Number(args[0]?.height ?? state.settings.window_height ?? 840)
        state.settings.window_maximized = args[0]?.maximized === true
        state.runtimeWindowMaximized = state.settings.window_maximized
        persistMockSettings()
        return {
          x: state.settings.window_x,
          y: state.settings.window_y,
          width: state.settings.window_width,
          height: state.settings.window_height,
          maximized: state.settings.window_maximized,
        }
      case 'GetPlatform': return { os: 'win32', defaultPath: options.platformDefaultPath ?? 'D:\\NovelistData' }
      case 'runtime.window.getBounds': return getRuntimeWindowBounds()
      case 'runtime.window.isMaximized': return state.runtimeWindowMaximized === true
      case 'runtime.window.minimize':
      case 'runtime.app.quit':
      case 'CancelChat':
      case 'ApproveTool':
      case 'RebuildNovelIndex':
      case 'TestConnection':
      case 'TestEmbeddingConnection':
        return null
      case 'runtime.window.toggleMaximize':
        state.runtimeWindowMaximized = !(state.runtimeWindowMaximized === true)
        state.settings.window_maximized = state.runtimeWindowMaximized
        return null
      case 'SetLastSession':
        state.settings.last_session_id = String(args[0] ?? '')
        return null
      case 'SetSelectedModel':
        state.settings.selected_model_key = String(args[0] ?? '')
        state.settings.reasoning_effort = String(args[1] ?? '')
        return null
      case 'SetReasoningEffort':
        state.settings.reasoning_effort = String(args[0] ?? '')
        return null
      case 'SetApprovalMode':
        state.settings.approval_mode = String(args[0] ?? '')
        return null
      case 'SetChatPanelWidth':
        state.settings.chat_panel_width = Number(args[0] ?? state.settings.chat_panel_width ?? 360)
        return null
      case 'SaveLLMConfig':
        state.savedLLMConfig = args[0]
        return null
      case 'SaveEmbeddingConfig':
        state.savedEmbeddingConfig = args[0]
        return null
      case 'GetAppConfig': return {
        initialized: state.initialized,
        data_dir: options.platformDefaultPath ?? 'D:\\NovelistData',
        update_check: {
          endpoint_url: state.settings.update_check_endpoint_url ?? '',
          default_enabled: state.settings.update_check_enabled === true,
          timeout_ms: 5000,
        },
        import_recovery: state.importRecovery,
      }
      case 'SetActiveNovel':
        state.activeNovelId = args[0]?.novel_id ?? state.activeNovelId
        state.settings.last_novel_id = state.activeNovelId
        return null
      case 'GetNovels': return state.novels
      case 'CreateNovel': return createNovel(args[0])
      case 'UpdateNovel': return updateNovel(args[0], args[1])
      case 'DeleteNovel':
        deleteNovel(args[0])
        return null
      case 'GetCover': return null
      case 'SaveCover':
        state.savedCovers.push({ novel_id: args[0], byte_count: Array.isArray(args[1]) ? args[1].length : 0 })
        return null
      case 'SaveAvatar':
        state.savedAvatars.push({ byte_count: Array.isArray(args[0]) ? args[0].length : 0 })
        return null
      case 'ExportNovel':
        state.exportedNovels.push({ novel_id: args[0], format: args[1] })
        return null
      case 'PickNovelImportFile': return options.pickedNovelImportFile ?? null
      case 'StartNovelImport': return startNovelImport(args[0])
      case 'CancelNovelImport': return cancelNovelImport(args[0])
      case 'GetNovelImportRun': return state.novelImportRuns.find((run) => run.task_id === args[0]?.task_id) ?? null
      case 'GetNovelImportRecoveryStatus': return {
        pending_runs: state.novelImportRuns.filter((run) => !['completed', 'completed_with_warning', 'failed', 'cancelled'].includes(run.state)),
        blocked_runs: state.novelImportRuns.filter((run) => run.state === 'cleanup_blocked'),
        checked_at: now,
      }
      case 'GetGitCommits': return getGitCommits(state, args[0])
      case 'GetGitCommitFiles': return getGitCommitFiles(state, args[0])
      case 'GetGitFileDiff': return getGitFileDiff(state, args[0])
      case 'GetChapters': return chapters(args[0])
      case 'CreateChapter': return createChapter(args[0])
      case 'UpdateChapterTitle':
        updateChapterTitle(args[0], args[1], args[2])
        return null
      case 'GetContent': return content(args[1])
      case 'SaveContent': return saveContent(args[0])
      case 'GetModels': return [availableModel()]
      case 'GetSessions': return getSessions(args[0])
      case 'GetSession': return sessionDetail(args[0])
      case 'GetSessionMessages': return []
      case 'ListSlashCommands': return [{ name: 'review', description: '审稿当前章节', type: 'manual' }]
      case 'Chat': return chat(args[0])
      case 'CompressContext': return { turn_id: state.nextTurnId++ }
      case 'SearchAll': return searchAll(args[1])
      case 'GetCharacters': return characters(args[0])
      case 'CreateCharacter': return createCharacter(args[0], args[1])
      case 'UpdateCharacter':
        updateCharacter(args[0], args[1], args[2])
        return null
      case 'DeleteCharacter':
        deleteCharacter(args[0], args[1])
        return null
      case 'GetCharacterRelations': return []
      case 'GetLocations': return locations(args[0])
      case 'CreateLocation': return createLocation(args[0], args[1])
      case 'UpdateLocation':
        updateLocation(args[0], args[1], args[2])
        return null
      case 'DeleteLocation':
        deleteLocation(args[0], args[1])
        return null
      case 'GetLocationRelations': return []
      case 'GetStoryArcs': return storyArcs(args[0])
      case 'CreateStoryArc': return createStoryArc(args[0], args[1])
      case 'UpdateStoryArc':
        updateStoryArc(args[0], args[1], args[2])
        return null
      case 'DeleteStoryArc':
        deleteStoryArc(args[0], args[1])
        return null
      case 'GetArcNodes': return arcNodes(args[0])
      case 'CreateArcNode': return createArcNode(args[0], args[1])
      case 'UpdateArcNode':
        updateArcNode(args[0], args[1], args[2])
        return null
      case 'DeleteArcNode':
        deleteArcNode(args[0], args[1])
        return null
      case 'GetMaxChapterNumber': return maxChapterNumber(args[0])
      case 'GetChapterPlans': return chapterPlans(args[0])
      case 'UpdateChapterPlan':
        updateChapterPlan(args[0], args[1])
        return null
      case 'GetTimelineEntries': return timelineEntries(args[0])
      case 'CreateTimelineEntry': return createTimelineEntry(args[0], args[1])
      case 'UpdateTimelineEntry':
        updateTimelineEntry(args[0], args[1], args[2])
        return null
      case 'DeleteTimelineEntry':
        deleteTimelineEntry(args[0], args[1])
        return null
      case 'GetReaderPerspectives': return readerPerspectives(args[0])
      case 'CreateReaderPerspective': return createReaderPerspective(args[0], args[1])
      case 'UpdateReaderPerspective':
        updateReaderPerspective(args[1], args[0], args[2])
        return null
      case 'DeleteReaderPerspective':
        deleteReaderPerspective(args[1], args[0])
        return null
      case 'GetPreferences': return preferences(args[0])
      case 'CreatePreference': return createPreference(args[0], args[1])
      case 'UpdatePreference': return updatePreference(args[0], args[1])
      case 'DeletePreference':
        deletePreference(args[0])
        return null
      case 'GetWritingActivity': return writingActivity()
      case 'GetWritingStats': return writingStats()
      case 'ListSkills': return skills()
      case 'DeleteSkill':
        deleteSkill(args[0])
        return null
      case 'ExtractStyle': return extractStyle(args[0])
      case 'ExtractStyleSkillFromSamples': return extractStyleSkillFromSamples(args[0])
      case 'CancelStyleSkillExtraction': return cancelStyleSkillExtraction(args[0])
      case 'GetStyleSkillExtractionRun': return state.styleSkillExtractionRuns.find((run) => run.task_id === args[0]?.task_id) ?? null
      case 'StartNarrativePatternExtraction': return startNarrativePatternExtraction(args[0])
      case 'CancelNarrativePatternExtraction': return cancelNarrativePatternExtraction(args[0])
      case 'GetNarrativePatternRun': return state.narrativePatternRuns.find((run) => run.task_id === args[0]?.task_id) ?? null
      case 'GetNarrativePatternTrace': return state.narrativePatternTraces[String(args[0]?.task_id ?? '')] ?? null
      case 'SearchStyleSamples': return searchStyleSamples(args[0])
      case 'GetStyleSample': return getStyleSample(args[0])
      case 'CreateStyleSample': return createStyleSample(args[0])
      case 'UpdateStyleSample': return updateStyleSample(args[0])
      case 'DeleteStyleSample':
        deleteStyleSample(args[0])
        return null
      case 'SaveUserName':
        state.settings.user_name = String(args[0] ?? '')
        return null
      case 'GetLLMConfig': return llmConfig()
      case 'GetEmbeddingConfig': return embeddingConfig()
      case 'GetSqliteVecStatus': return sqliteVecStatus()
      case 'GetReferenceAnchors': return referenceAnchors()
      case 'GetReferenceAnchorBuildStatus': return referenceBuildStatus(args[1])
      case 'PickReferenceSourceFile': return options.pickedReferenceSourceFile ?? null
      case 'CreateReferenceAnchor': return createReferenceAnchor(args[0])
      case 'CreateReferenceAnchors': return createReferenceAnchors(args[0])
      case 'CreateReferenceAnchorsWithResult': return createReferenceAnchorsWithResult(args[0])
      case 'RebuildReferenceAnchor': return rebuildReferenceAnchor(args[1])
      case 'SearchReferenceMaterials': return searchReferenceMaterials(args[0])
      case 'GetReferenceMaterialTagReviewQueue': return getReferenceMaterialTagReviewQueue(args[0])
      case 'GetReferenceMaterialDetail': return getReferenceMaterialDetail(args[0])
      case 'GetReferenceSourceSegmentDetail': return getReferenceSourceSegmentDetail(args[0])
      case 'GetReferenceSourceProcessingDetail': return getReferenceSourceProcessingDetail(args[0])
      case 'StartReferenceCorpusFeatureAnalysis': return startReferenceCorpusFeatureAnalysis(args[0])
      case 'GetReferenceCorpusFeatureAnalysisRun': return getReferenceCorpusFeatureAnalysisRun(args[0])
      case 'StartReferenceCorpusTechniqueSpecimenAnalysis': return startReferenceCorpusTechniqueSpecimenAnalysis(args[0])
      case 'GetReferenceCorpusTechniqueSpecimenAnalysisRun': return getReferenceCorpusTechniqueSpecimenAnalysisRun(args[0])
      case 'ListReferenceCorpusFeatureObservations': return listReferenceCorpusFeatureObservations(args[0])
      case 'ListReferenceCorpusTechniqueSpecimens': return listReferenceCorpusTechniqueSpecimens(args[0])
      case 'GenerateReferenceCorpusBlueprintCandidates': return generateReferenceCorpusBlueprintCandidates(args[0])
      case 'GenerateReferenceCorpusInsertionDraft': return generateReferenceCorpusInsertionDraft(args[0])
      case 'GenerateReferenceCorpusInsertionDraftCandidates': return generateReferenceCorpusInsertionDraftCandidates(args[0])
 case 'RecordReferenceCorpusInsertionAudit': return args[0]?.draft?.ready_for_insertion === true && args[0]?.draft?.gate?.passed === true && args[0]?.draft?.audit?.passed === true
      case 'UpdateReferenceMaterialTags': return updateReferenceMaterialTags(args[0])
      case 'UpdateReferenceMaterialsTags': return updateReferenceMaterialsTags(args[0])
      case 'AdaptReferenceMaterial': return adaptReferenceMaterial(args[0])
      case 'BuildReferenceStyleProfile': return buildReferenceStyleProfile(args[0])
      case 'GetReferenceStyleProfileBuildStatus': return referenceStyleProfileBuildStatus(args[0])
      case 'GetReferenceChapterBlueprints': return Object.values(state.referenceBlueprints).map(toReferenceBlueprintSummary)
      case 'GetReferenceChapterBlueprint': return state.referenceBlueprints[String(args[1])] ?? null
      case 'GenerateReferenceChapterBlueprint': return generateReferenceBlueprint(args[0])
      case 'ReviewReferenceChapterBlueprint': return reviewReferenceBlueprint(args[0])
      case 'ApproveReferenceChapterBlueprint': return approveReferenceBlueprint(args[0])
      case 'BindReferenceBlueprintMaterials': return bindReferenceBlueprintMaterials(args[0])
      case 'GetReferenceDraftCandidates': return getReferenceDraftCandidates(args[0])
      case 'GetReferenceAnchoredDraftAudits': return getReferenceAnchoredDraftAudits(args[0])
      case 'StartReferenceOrchestrationRun': return startReferenceOrchestrationRun(args[0])
      case 'GetReferenceOrchestrationRuns': return referenceOrchestrationRuns(args[0], args[1])
      case 'GetReferenceOrchestrationRun': return referenceOrchestrationRun(args[1])
      case 'GetReferenceOrchestrationRunEvents': return referenceOrchestrationRunEvents(args[1])
      case 'ResumeReferenceOrchestrationRun': return resumeReferenceOrchestrationRun(args[0])
      case 'CancelReferenceOrchestrationRun': return cancelReferenceOrchestrationRun(args[0])
      default:
        return defaultValueFor(method)
    }
  }

  function createNovel(input) {
    const novel = {
      id: state.nextNovelId++,
      title: String(input?.title ?? ''),
      genre: String(input?.genre ?? ''),
      description: String(input?.description ?? ''),
      created_at: now,
      updated_at: now,
    }
    state.novels = [...state.novels, novel]
    state.chaptersByNovelId[novel.id] = []
    return novel
  }

  function updateNovel(novelId, input) {
    const existing = state.novels.find((novel) => novel.id === novelId)
    if (!existing) throw new Error(`Novel ${novelId} not found.`)
    const updated = {
      ...existing,
      title: String(input?.title ?? existing.title),
      genre: String(input?.genre ?? existing.genre ?? ''),
      description: String(input?.description ?? existing.description ?? ''),
      updated_at: now,
    }
    state.novels = state.novels.map((novel) => novel.id === novelId ? updated : novel)
    return updated
  }

  function deleteNovel(novelId) {
    state.novels = state.novels.filter((novel) => novel.id !== novelId)
    delete state.chaptersByNovelId[String(novelId)]
    if (state.activeNovelId === novelId) {
      state.activeNovelId = state.novels[0]?.id ?? 0
      state.settings.last_novel_id = state.activeNovelId
    }
  }

  function saveGitAuthorSettings(input = {}) {
    const name = String(input?.name ?? '').trim()
    const email = String(input?.email ?? '').trim()

    if (name.length === 0 && email.length === 0) {
      state.settings.git_author_name = ''
      state.settings.git_author_email = ''
      persistMockSettings()
      return { name: '', email: '', scope: 'app' }
    }

    if (name.length === 0 || email.length === 0 || !isValidMockGitEmail(email)) {
      throw new Error('Git author name and a valid email must be provided together.')
    }

    state.settings.git_author_name = name
    state.settings.git_author_email = email
    persistMockSettings()
    return {
      name: state.settings.git_author_name,
      email: state.settings.git_author_email,
      scope: 'app',
    }
  }

  function checkForUpdates(input = {}) {
    const taskId = String(input?.task_id ?? `update-${Date.now()}`)
    const manual = input?.manual === true
    const mode = state.nextUpdateCheckMode || 'available'
    state.settings.update_check_last_checked_at = now
    state.nextUpdateCheckMode = options.updateCheckMode ?? 'available'

    if (mode === 'failed') {
      return {
        task_id: taskId,
        status: 'failed',
        current_version: '1.0.0',
        latest_version: null,
        release_url: null,
        checked_at: now,
        error_code: 'update.mock_failure',
        error_message: '模拟更新检查失败：Bearer update-check-token-abcdefghijklmnopqrstuvwxyz',
        release_name: null,
        release_notes: null,
        download_url: null,
        dismissed: false,
        diagnostic_details: mockSensitiveDiagnosticDetails(),
      }
    }

    if (mode === 'no_update') {
      return {
        task_id: taskId,
        status: 'no_update',
        current_version: '2.0.0',
        latest_version: 'v2.0.0',
        release_url: 'https://updates.example.test/releases/v2.0.0',
        checked_at: now,
        error_code: null,
        error_message: null,
        release_name: 'Novelist 2.0',
        release_notes: '## 安全更新\n\n- 当前已是最新版本。',
        download_url: 'https://updates.example.test/downloads/novelist-2.0.zip',
        dismissed: false,
      }
    }

    const dismissed = !manual && state.settings.update_check_dismissed_version === 'v2.0.0'
    return {
      task_id: taskId,
      status: dismissed ? 'dismissed' : 'update_available',
      current_version: '1.0.0',
      latest_version: 'v2.0.0',
      release_url: 'https://updates.example.test/releases/v2.0.0',
      checked_at: now,
      error_code: null,
      error_message: null,
      release_name: 'Novelist 2.0',
      release_notes: '## 安全更新\n\n- 改进导入恢复与错误提示。',
      download_url: 'https://updates.example.test/downloads/novelist-2.0.zip',
      dismissed,
    }
  }

  function persistMockSettings() {
    window.localStorage.setItem('novelist_app_mock_settings', JSON.stringify(state.settings))
  }

  function mockSensitiveDiagnosticDetails() {
    return {
      api_key: 'sk-proj-errorabcdefghijklmnopqrstuvwxyz1234567890',
      authorization: 'Bearer detail-error-token-abcdefghijklmnopqrstuvwxyz',
      source_text: '敏感源文本'.repeat(300),
      nested: {
        password: 'open-sesame-secret',
        token: 'detail-token-abcdefghijklmnopqrstuvwxyz',
      },
    }
  }

  function readPersistedMockSettings() {
    try {
      const raw = window.localStorage.getItem('novelist_app_mock_settings')
      if (!raw) return null
      const parsed = JSON.parse(raw)
      if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null
      return { ...defaultSettings, ...parsed }
    } catch {
      return null
    }
  }

  function isValidMockGitEmail(email) {
    return email.length > 2 &&
      email.length <= 320 &&
      !/\s/.test(email) &&
      email.indexOf('@') > 0 &&
      email.lastIndexOf('@') === email.indexOf('@') &&
      email.indexOf('@') < email.length - 1
  }

  function getRuntimeWindowBounds() {
    return {
      x: state.settings.window_x ?? null,
      y: state.settings.window_y ?? null,
      width: state.settings.window_width ?? 1280,
      height: state.settings.window_height ?? 840,
      maximized: state.settings.window_maximized === true,
    }
  }

  function getSessions(input = {}) {
    const page = Math.max(1, Number(input?.page ?? 1))
    const size = Math.max(1, Math.min(100, Number(input?.size ?? 20)))
    const search = String(input?.search ?? '').trim().toLowerCase()
    const sessions = state.sessions
      .filter((session) => !search || String(session.title ?? '').toLowerCase().includes(search))
      .map(cloneJson)
    const startIndex = (page - 1) * size
    return pagedResult(sessions.slice(startIndex, startIndex + size), page, size, sessions.length)
  }

  async function startNovelImport(input) {
    const sourcePath = String(input?.source_path ?? '')
    const sourceDisplayName = String(input?.source_display_name ?? fileNameFromPath(sourcePath) ?? '导入小说.txt')
    const importKind = String(input?.import_kind ?? importKindFromFileName(sourceDisplayName) ?? 'txt')
    const taskId = String(input?.task_id ?? `import-${state.novelImportRuns.length + 1}`)
    const scenario = novelImportScenario(sourceDisplayName)
    const progressTotal = 7
    const title = sourceDisplayName
      .replace(/\.(epub|txt|md|markdown)$/i, '')
      .trim() || '导入小说'

    emit('novel_import:progress', {
      task_id: 'stale-import-task',
      state: 'writing_files',
      stage: 'write_chapter',
      progress_completed: 3,
      progress_total: progressTotal,
      message: '旧导入不应显示',
      current_chapter_index: 99,
      current_chapter_title: '旧导入章节',
      created_novel_id: 999,
      updated_at: now,
    })

    emitNovelImportProgress(taskId, 'created', 'created', 0, progressTotal, '导入任务已创建', null)
    await wait(10)
    emitNovelImportProgress(taskId, 'parsing', 'parse_source', 1, progressTotal, '正在解析源文件', null)

    if (scenario === 'parser_failure') {
      await wait(10)
      const run = pushNovelImportRun(makeNovelImportRun({
        taskId,
        stateValue: 'failed',
        stage: 'parse_failed',
        sourceDisplayName,
        importKind,
        error: importDiagnostic('import.parse_failed', '源文件解析失败', 'mock parser rejected this source'),
        diagnostics: [{
          code: 'import.parse_failed',
          message: '源文件解析失败',
          detail: 'mock parser rejected this source',
          severity: 'error',
        }],
      }))
      emitNovelImportProgress(taskId, 'failed', 'parse_failed', progressTotal, progressTotal, '源文件解析失败', null)
      return run
    }

    await wait(10)
    const novel = createImportedNovel(title, importKind)
    state.activeNovelImports[taskId] = {
      sourceDisplayName,
      importKind,
      createdNovelId: novel.id,
    }
    emitNovelImportProgress(taskId, 'creating_novel', 'create_novel', 2, progressTotal, '正在创建作品', novel.id)
    await wait(10)
    const importedChapter = createImportedChapter(novel.id, sourceDisplayName, title)
    emitNovelImportProgress(taskId, 'writing_files', 'write_chapters', 2, progressTotal, '正在写入章节', novel.id)
    await wait(10)
    emitNovelImportProgress(
      taskId,
      'writing_files',
      'write_chapter',
      3,
      progressTotal,
      '正在写入章节 1/1',
      novel.id,
      1,
      importedChapter.title,
    )

    if (scenario === 'cancel') {
      const cancelled = await waitForNovelImportCancellation(taskId, 900)
      if (cancelled) {
        return finalizeCancelledNovelImport(taskId, sourceDisplayName, importKind, novel.id)
      }
    }

    if (scenario === 'write_failure') {
      await wait(10)
      emitNovelImportProgress(taskId, 'cleanup_pending', 'cleanup_created_files', 4, progressTotal, '正在清理未完成导入', novel.id)
      deleteNovel(novel.id)
      delete state.activeNovelImports[taskId]
      const run = pushNovelImportRun(makeNovelImportRun({
        taskId,
        stateValue: 'cleanup_completed',
        stage: 'cleanup_completed',
        sourceDisplayName,
        importKind,
        createdNovelId: novel.id,
        createdFileRoots: [`novels/${novel.id}`],
        error: importDiagnostic('import.write_failed', '导入写入失败，已清理未完成数据。', 'mock write failure after first chapter'),
        diagnostics: [{
          code: 'import.cleanup_completed',
          message: '失败导入已清理',
          detail: 'mock cleanup removed created novel and chapter files',
          severity: 'info',
        }],
      }))
      emitNovelImportProgress(taskId, 'cleanup_completed', 'cleanup_completed', progressTotal, progressTotal, '导入写入失败，已清理未完成数据。', novel.id)
      return run
    }

    await wait(10)
    emitNovelImportProgress(taskId, 'saving_metadata', 'saving_metadata', 4, progressTotal, '正在保存元数据', novel.id)
    await wait(10)
    emitNovelImportProgress(taskId, 'indexing', 'indexing', 5, progressTotal, '正在刷新搜索索引', novel.id)
    await wait(10)
    emitNovelImportProgress(taskId, 'git_commit', 'git_commit', 6, progressTotal, '正在创建 Git 导入提交', novel.id)
    await wait(10)

    const warnings = scenario === 'git_warning'
      ? [{
        code: 'git.commit_failed',
        message: '导入已完成，但 Git 提交失败。',
        detail: 'mock git commit failure; imported files remain in the workspace',
      }]
      : []
    const skippedChapters = scenario === 'skipped_epub'
      ? [
        { index: 2, title: '空白章节', reason: 'empty_content' },
        { index: 3, title: '缺失章节', reason: 'missing_spine_item' },
      ]
      : []
    const finalState = warnings.length > 0 ? 'completed_with_warning' : 'completed'
    const run = pushNovelImportRun(makeNovelImportRun({
      taskId,
      stateValue: finalState,
      stage: 'done',
      sourceDisplayName,
      importKind,
      createdNovelId: novel.id,
      createdFileRoots: [`novels/${novel.id}`],
      skippedChapters,
      warnings,
    }))
    delete state.activeNovelImports[taskId]
    emitNovelImportProgress(
      taskId,
      finalState,
      'done',
      progressTotal,
      progressTotal,
      finalState === 'completed_with_warning' ? '导入完成，但有警告' : '导入完成',
      novel.id,
    )
    return run
  }

  function cancelNovelImport(input) {
    const taskId = String(input?.task_id ?? '')
    if (!taskId) throw new Error('CancelNovelImport requires task_id.')
    if (!state.cancelledNovelImportTaskIds.includes(taskId)) {
      state.cancelledNovelImportTaskIds.push(taskId)
    }

    const active = state.activeNovelImports[taskId]
    if (active?.createdNovelId) {
      deleteNovel(active.createdNovelId)
    }

    const existing = state.novelImportRuns.find((run) => run.task_id === taskId)
    if (existing) return existing

    return finalizeCancelledNovelImport(
      taskId,
      active?.sourceDisplayName ?? 'cancelled-import.txt',
      active?.importKind ?? 'txt',
      active?.createdNovelId ?? null,
    )
  }

  function finalizeCancelledNovelImport(taskId, sourceDisplayName, importKind, createdNovelId) {
    if (createdNovelId) {
      deleteNovel(createdNovelId)
    }
    delete state.activeNovelImports[taskId]
    const run = pushNovelImportRun(makeNovelImportRun({
      taskId,
      stateValue: createdNovelId ? 'cleanup_completed' : 'cancelled',
      stage: createdNovelId ? 'cleanup_completed' : 'cancelled',
      sourceDisplayName,
      importKind,
      createdNovelId,
      createdFileRoots: createdNovelId ? [`novels/${createdNovelId}`] : [],
      error: importDiagnostic('import.cancelled', '导入已取消', 'user cancelled the mocked import'),
      diagnostics: createdNovelId ? [{
        code: 'import.cleanup_completed',
        message: '取消导入已清理',
        detail: 'mock cleanup removed created novel and chapter files',
        severity: 'info',
      }] : [],
    }))
    emitNovelImportProgress(
      taskId,
      run.state,
      run.stage,
      7,
      7,
      '导入已取消',
      createdNovelId,
    )
    return run
  }

  function novelImportScenario(sourceDisplayName) {
    const lower = String(sourceDisplayName).toLowerCase()
    if (lower.includes('cancel-import')) return 'cancel'
    if (lower.includes('parser-failure')) return 'parser_failure'
    if (lower.includes('write-failure')) return 'write_failure'
    if (lower.includes('git-warning')) return 'git_warning'
    if (lower.includes('skipped-chapters')) return 'skipped_epub'
    return 'success'
  }

  function createImportedNovel(title, importKind) {
    const novel = {
      id: state.nextNovelId++,
      title,
      genre: importKind === 'epub' ? 'EPUB 导入' : '文本导入',
      description: '由小说导入流程创建',
      created_at: now,
      updated_at: now,
    }
    state.novels = [...state.novels, novel]
    state.chaptersByNovelId[novel.id] = []
    return novel
  }

  function createImportedChapter(novelId, sourceDisplayName, title) {
    const importedChapter = {
      id: state.nextChapterId++,
      novel_id: novelId,
      chapter_number: 1,
      title: '导入开篇',
      summary: '',
      word_count: 12,
      file_path: `chapters/import-${novelId}-001.md`,
      created_at: now,
      updated_at: now,
    }
    state.chaptersByNovelId[novelId] = [importedChapter]
    state.contentByPath[importedChapter.file_path] = `# ${title}\n\n这是 ${sourceDisplayName} 的导入正文。`
    return importedChapter
  }

  function makeNovelImportRun({
    taskId,
    stateValue,
    stage,
    sourceDisplayName,
    importKind,
    createdNovelId = null,
    createdFileRoots = [],
    skippedChapters = [],
    diagnostics = [],
    warnings = [],
    error = null,
  }) {
    return {
      task_id: taskId,
      state: stateValue,
      stage,
      source_display_name: sourceDisplayName,
      source_path_hash: `sha256:mock-import-${state.novelImportRuns.length + 1}`,
      parser_type: importKind,
      created_novel_id: createdNovelId,
      created_file_roots: createdFileRoots,
      skipped_chapters: skippedChapters,
      diagnostics,
      warnings,
      error,
      started_at: now,
      updated_at: now,
      completed_at: now,
    }
  }

  function pushNovelImportRun(run) {
    const existingIndex = state.novelImportRuns.findIndex((item) => item.task_id === run.task_id)
    if (existingIndex >= 0) {
      state.novelImportRuns = state.novelImportRuns.map((item, index) => index === existingIndex ? run : item)
    } else {
      state.novelImportRuns = [...state.novelImportRuns, run]
    }
    return run
  }

  function importDiagnostic(code, message, detail) {
    return {
      code,
      message,
      detail,
      operation: 'StartNovelImport',
      task_id: null,
      run_id: null,
      bridge_method: 'StartNovelImport',
      timestamp: now,
    }
  }

  async function waitForNovelImportCancellation(taskId, timeoutMs) {
    const startedAt = Date.now()
    while (Date.now() - startedAt < timeoutMs) {
      if (state.cancelledNovelImportTaskIds.includes(taskId)) return true
      await wait(25)
    }
    return state.cancelledNovelImportTaskIds.includes(taskId)
  }

  function emitNovelImportProgress(
    taskId,
    stateValue,
    stage,
    completed,
    total,
    message,
    createdNovelId,
    currentChapterIndex = null,
    currentChapterTitle = null,
  ) {
    emit('novel_import:progress', {
      task_id: taskId,
      state: stateValue,
      stage,
      progress_completed: completed,
      progress_total: total,
      message,
      created_novel_id: createdNovelId,
      current_chapter_index: currentChapterIndex,
      current_chapter_title: currentChapterTitle,
      updated_at: now,
    })
  }

  function fileNameFromPath(value) {
    return String(value)
      .split(/[\\/]/)
      .filter(Boolean)
      .at(-1)
  }

  function importKindFromFileName(value) {
    const lower = String(value).toLowerCase()
    if (lower.endsWith('.epub')) return 'epub'
    if (lower.endsWith('.txt')) return 'txt'
    if (lower.endsWith('.md') || lower.endsWith('.markdown')) return 'markdown'
    return ''
  }

  function chapters(novelId = state.activeNovelId) {
    return [...(state.chaptersByNovelId[String(novelId)] ?? [])]
  }

  function maxChapterNumber(novelId = state.activeNovelId) {
    return chapters(novelId).reduce((max, chapter) => Math.max(max, Number(chapter.chapter_number) || 0), 0)
  }

  function createChapter(input) {
    const novelId = input?.novel_id ?? state.activeNovelId
    const list = state.chaptersByNovelId[String(novelId)] ?? []
    const chapterNumber = list.reduce((max, chapter) => Math.max(max, chapter.chapter_number), 0) + 1
    const chapter = {
      id: state.nextChapterId++,
      novel_id: novelId,
      chapter_number: chapterNumber,
      title: String(input?.title ?? ''),
      summary: '',
      word_count: 0,
      file_path: `chapters/${chapterNumber}.md`,
      created_at: now,
      updated_at: now,
    }
    state.chaptersByNovelId[String(novelId)] = [...list, chapter]
    state.contentByPath[chapter.file_path] = ''
    return chapter
  }

  function updateChapterTitle(novelId, chapterNumber, title) {
    const key = String(novelId)
    const list = state.chaptersByNovelId[key] ?? []
    state.chaptersByNovelId[key] = list.map((chapter) =>
      chapter.chapter_number === chapterNumber
        ? { ...chapter, title: String(title ?? chapter.title), updated_at: now }
        : chapter,
    )
  }

  function content(filePath) {
    return state.contentByPath[filePath] ?? ''
  }

  function saveContent(input) {
    if (!options.allowSaveContent && !isSkillSaveInput(input)) {
      throw new Error('SaveContent is forbidden in the app-wide smoke unless the test explicitly edits content.')
    }
    if (state.failNextSaveContent) {
      state.failNextSaveContent = false
      throw new Error('模拟保存失败，请重试')
    }
    if (!input?.path) {
      throw new Error('SaveContent requires a path.')
    }
    state.contentByPath[input.path] = String(input.content ?? '')
    return null
  }

  function isSkillSaveInput(input) {
    const path = String(input?.path ?? '')
    return path.startsWith('skills/') || path.startsWith('~/.novelist/skills/')
  }

  function availableModel() {
    return {
      Key: 'mock/gpt',
      ProviderName: 'mock',
      ModelName: 'Mock GPT',
      ContextWindow: 128000,
      MaxOutputTokens: 4096,
      SupportsThinking: true,
      ReasoningLevels: ['high'],
      SupportsVision: false,
    }
  }

  function sessionDetail(sessionId) {
    return {
      session_id: sessionId,
      novel_id: 42,
      title: 'Mock session',
      model: 'mock/gpt',
      reasoning_effort: 'high',
      active_version: 1,
      last_turn_id: 0,
      created_at: now,
      updated_at: now,
    }
  }

  async function chat(input) {
    const sessionId = input?.session_id || `session-app-${state.nextSessionId++}`
    const turnId = state.nextTurnId++
    const message = String(input?.message ?? '')
    emit('chat:started', { turn_id: turnId })

    if (message.includes('停止生成')) {
      await wait(600)
      return {
        session_id: sessionId,
        turn_id: turnId,
        final_text: '',
      }
    }

    if (message.includes('触发失败态') && !state.chatFailureRecovered) {
      state.chatFailureRecovered = true
      await wait(50)
      emit(`agent:${turnId}`, agentEvent(turnId, 1, {
        type: 5,
        error: '模拟模型失败，请重试',
      }))
      await wait(50)
      return {
        session_id: sessionId,
        turn_id: turnId,
        final_text: '',
      }
    }

    if (message.includes('触发失败态')) {
      await wait(80)
      const retryText = '重试后恢复：模型返回稳定结果，未修改章节正文。'
      emit(`agent:${turnId}`, agentEvent(turnId, 1, {
        type: 2,
        data: retryText,
      }))
      await wait(40)
      return {
        session_id: sessionId,
        turn_id: turnId,
        final_text: retryText,
      }
    }

    if (message.includes('长文本 Markdown')) {
      const chunks = longMarkdownChatChunks()
      emit(`agent:${turnId}`, agentEvent(turnId, 1, {
        type: 0,
        data: '先检查章节约束、工具结果和是否需要写入正文。',
      }))
      await wait(80)
      emit(`agent:${turnId}`, agentEvent(turnId, 2, {
        type: 1,
      }))
      emit(`agent:${turnId}`, agentEvent(turnId, 3, {
        type: 3,
        tool_name: 'inspect_story_constraints',
        tool_id: 'tool-story-constraints-001',
        phase: 'completed',
        display_text: '检查章节约束',
        activity_kind: 'review',
        metadata: { chapter_path: 'chapters/1.md', can_mutate: false },
      }))
      for (let index = 0; index < chunks.length; index += 1) {
        emit(`agent:${turnId}`, agentEvent(turnId, index + 4, {
          type: 2,
          data: chunks[index],
        }))
        await wait(index === 0 ? 1800 : 120)
      }
      const finalText = chunks.join('')
      emit(`agent:${turnId}`, agentEvent(turnId, chunks.length + 4, {
        type: 4,
        usage: {
          prompt_tokens: 420,
          completion_tokens: 980,
          total_tokens: 1400,
          prompt_cache_hit_tokens: 320,
          prompt_cache_miss_tokens: 100,
          cache_hit_ratio: 76.2,
          context_window: 128000,
          usage_ratio: 1.1,
          detail: {
            system: 160,
            user: 260,
            assistant: 980,
            tool: 0,
          },
        },
      }))
      return {
        session_id: sessionId,
        turn_id: turnId,
        final_text: finalText,
      }
    }

    await wait(100)
    emit(`agent:${turnId}`, agentEvent(turnId, 1, {
      type: 3,
      tool_name: 'get_chapter_list',
      tool_id: 'tool-chapters-001',
      phase: 'executing',
      display_text: '读取章节列表',
      activity_kind: 'view',
    }))
    emit(`agent:${turnId}`, agentEvent(turnId, 2, {
      type: 3,
      tool_name: 'get_chapter_list',
      tool_id: 'tool-chapters-001',
      phase: 'completed',
      display_text: '读取章节列表',
      activity_kind: 'view',
      metadata: { chapters: 2 },
    }))
    emit(`agent:${turnId}`, agentEvent(turnId, 3, {
      type: 3,
      tool_name: 'web_search',
      tool_id: 'tool-web-001',
      phase: 'completed',
      display_text: '检索雨夜线索资料',
      activity_kind: 'browse',
      metadata: {
        queries: ['雨夜线索'],
        summary: '检索结果只用于对照氛围，不写入章节。',
        sources: [{ title: 'Mock source', url: 'https://example.com/mock-source' }],
      },
    }))
    emit(`agent:${turnId}`, agentEvent(turnId, 4, {
      type: 2,
      data: '已读取《雨夜线索》的章节列表，建议先保留受限视角。',
    }))
    emit(`agent:${turnId}`, agentEvent(turnId, 5, {
      type: 4,
      usage: {
        prompt_tokens: 96,
        completion_tokens: 32,
        total_tokens: 128,
        prompt_cache_hit_tokens: 86,
        prompt_cache_miss_tokens: 10,
        cache_hit_ratio: 89.6,
        context_window: 128000,
        usage_ratio: 0.1,
        detail: {
          system: 40,
          user: 20,
          assistant: 32,
          tool: 36,
        },
      },
    }))

    await wait(50)
    return {
      session_id: sessionId,
      turn_id: turnId,
      final_text: '已读取《雨夜线索》的章节列表，建议先保留受限视角。',
    }
  }

  function longMarkdownChatChunks() {
    const body = Array.from({ length: 12 }, (_, index) =>
      `第${toChineseOrdinal(index + 1)}段：雨声压住脚步声，回复仍保持可读宽度。`).join('\n\n')
    return [
      '### 约束检查\n\n',
      '- 不要直接写入章节正文。\n- 保留受限视角，不提前揭示门外身份。\n\n',
      '```yaml\nscene_guard: no_implicit_chapter_mutation\napproval_required: true\n```\n\n',
      `${body}\n\n最终建议：先读后改，不越过审批。`,
    ]
  }

  function toChineseOrdinal(value) {
    const values = ['一', '二', '三', '四', '五', '六', '七', '八', '九', '十', '十一', '十二']
    return values[value - 1] ?? String(value)
  }

  function agentEvent(turnId, seq, patch) {
    return {
      turn_id: turnId,
      seq,
      timestamp: now,
      ...patch,
    }
  }

  function searchAll(query) {
    if (!query?.trim()) return []
    if (query.includes('没有结果')) return []
    if (query.includes('搜索失败')) {
      if (!state.searchFailureRecovered) {
        state.searchFailureRecovered = true
        throw new Error('Mock search failure')
      }
      if (query.includes('恢复')) return searchResults()
      return []
    }
    return searchResults()
  }

  function searchResults() {
    return [
      {
        type: 'content',
        id: 1,
        title: '雨夜线索',
        subtitle: '第1章',
        chapter_num: 1,
        file_path: 'chapters/1.md',
        match_prefix: '林岚在',
        match_hit: '雨夜',
        match_suffix: '旧宅门前停住。',
        match_position: 3,
        match_len: 2,
        relevance: 1,
        panel_id: '',
      },
      {
        type: 'character',
        id: 1,
        title: '林岚',
        subtitle: '主角',
        chapter_num: 0,
        file_path: '',
        match_prefix: '旧城门调查者',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.8,
        panel_id: 'characters',
      },
      {
        type: 'location',
        id: 1,
        title: '旧城门',
        subtitle: '城市',
        chapter_num: 0,
        file_path: '',
        match_prefix: '雨夜里暗号被冲淡的城门。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.76,
        panel_id: 'locations',
      },
      {
        type: 'timeline',
        id: 1,
        title: '桌面水痕',
        subtitle: '伏笔',
        chapter_num: 1,
        file_path: '',
        match_prefix: '杯底留下半圈水痕，提示有人刚离开。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.74,
        panel_id: 'timeline',
      },
      {
        type: 'storyarc',
        id: 1,
        title: '雨夜调查线',
        subtitle: 'main',
        chapter_num: 0,
        file_path: '',
        match_prefix: '围绕桌面水痕推进。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.7,
        panel_id: 'storyarcs',
      },
      {
        type: 'preference',
        id: 2,
        title: '雨夜场景规则',
        subtitle: '节奏',
        chapter_num: 0,
        file_path: '',
        match_prefix: '雨夜场景多用动作间隔承压。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.72,
        panel_id: 'preferences',
      },
      {
        type: 'story_memory',
        id: 4,
        title: '故事记忆：旧城门约束',
        subtitle: '第1章',
        chapter_num: 1,
        file_path: 'chapters/1.md',
        match_prefix: '故事记忆只返回章节语义摘要，不暴露受限来源路径。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.88,
        panel_id: 'chapters',
        source_path: 'D:\\restricted\\reference-source.md',
      },
      {
        type: 'rag',
        id: 3,
        title: '雨夜语义片段',
        subtitle: '第1章',
        chapter_num: 1,
        file_path: 'chapters/1.md',
        match_prefix: '语义结果只指向章节内容，不暴露参考源路径。',
        match_hit: '',
        match_suffix: '',
        match_position: 0,
        match_len: 0,
        relevance: 0.86,
        panel_id: '',
      },
    ]
  }

  function characters(novelId = state.activeNovelId) {
    return state.characters.filter((item) => item.novel_id === novelId)
  }

  function createCharacter(novelId, input) {
    const character = {
      id: state.nextCharacterId++,
      novel_id: novelId,
      name: String(input?.name ?? ''),
      description: String(input?.description ?? ''),
      personality: String(input?.personality ?? ''),
      abilities: String(input?.abilities ?? '[]'),
      created_at: now,
      updated_at: now,
    }
    state.characters = [...state.characters, character]
    return character
  }

  function updateCharacter(novelId, characterId, input) {
    state.characters = state.characters.map((item) =>
      item.novel_id === novelId && item.id === characterId
        ? {
          ...item,
          name: String(input?.name ?? item.name),
          description: String(input?.description ?? item.description),
          personality: String(input?.personality ?? item.personality),
          abilities: String(input?.abilities ?? item.abilities),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteCharacter(novelId, characterId) {
    state.characters = state.characters.filter((item) => item.novel_id !== novelId || item.id !== characterId)
  }

  function locations(novelId = state.activeNovelId) {
    return state.locations.filter((item) => item.novel_id === novelId)
  }

  function createLocation(novelId, input) {
    const location = {
      id: state.nextLocationId++,
      novel_id: novelId,
      name: String(input?.name ?? ''),
      location_type: String(input?.location_type ?? ''),
      description: String(input?.description ?? ''),
      detail_json: String(input?.detail_json ?? '{}'),
      parent_location_id: Number(input?.parent_location_id ?? 0),
      tags: String(input?.tags ?? '[]'),
      created_at: now,
      updated_at: now,
    }
    state.locations = [...state.locations, location]
    return location
  }

  function updateLocation(novelId, locationId, input) {
    state.locations = state.locations.map((item) =>
      item.novel_id === novelId && item.id === locationId
        ? {
          ...item,
          name: String(input?.name ?? item.name),
          location_type: String(input?.location_type ?? item.location_type),
          description: String(input?.description ?? item.description),
          detail_json: String(input?.detail_json ?? item.detail_json),
          parent_location_id: input?.clear_parent ? 0 : Number(input?.parent_location_id ?? item.parent_location_id ?? 0),
          tags: String(input?.tags ?? item.tags),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteLocation(novelId, locationId) {
    state.locations = state.locations.filter((item) => item.novel_id !== novelId || item.id !== locationId)
    state.locations = state.locations.map((item) =>
      item.parent_location_id === locationId ? { ...item, parent_location_id: 0, updated_at: now } : item,
    )
  }

  function storyArcs(novelId = state.activeNovelId) {
    return state.storyArcs.filter((item) => item.novel_id === novelId)
  }

  function createStoryArc(novelId, input) {
    const arc = {
      id: state.nextStoryArcId++,
      novel_id: novelId,
      name: String(input?.name ?? ''),
      description: String(input?.description ?? ''),
      arc_type: String(input?.arc_type ?? 'main'),
      importance: Number(input?.importance ?? 3),
      status: String(input?.status ?? 'active'),
      reactivate_at: String(input?.reactivate_at ?? ''),
      created_at: now,
      updated_at: now,
    }
    state.storyArcs = [...state.storyArcs, arc]
    return arc
  }

  function updateStoryArc(novelId, arcId, input) {
    state.storyArcs = state.storyArcs.map((item) =>
      item.novel_id === novelId && item.id === arcId
        ? {
          ...item,
          name: String(input?.name ?? item.name),
          description: String(input?.description ?? item.description),
          arc_type: String(input?.arc_type ?? item.arc_type),
          importance: Number(input?.importance ?? item.importance),
          status: String(input?.status ?? item.status),
          reactivate_at: String(input?.reactivate_at ?? item.reactivate_at),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteStoryArc(novelId, arcId) {
    state.storyArcs = state.storyArcs.filter((item) => item.novel_id !== novelId || item.id !== arcId)
    state.arcNodes = state.arcNodes.filter((item) => item.novel_id !== novelId || item.story_arc_id !== arcId)
  }

  function arcNodes(novelId = state.activeNovelId) {
    return state.arcNodes.filter((item) => item.novel_id === novelId)
  }

  function createArcNode(novelId, input) {
    const node = {
      id: state.nextArcNodeId++,
      novel_id: novelId,
      story_arc_id: Number(input?.story_arc_id ?? 0),
      title: String(input?.title ?? ''),
      description: String(input?.description ?? ''),
      target_chapter: Number(input?.target_chapter ?? 1),
      actual_chapter: Number(input?.actual_chapter ?? 0),
      status: String(input?.status ?? 'pending'),
      created_at: now,
      updated_at: now,
    }
    state.arcNodes = [...state.arcNodes, node]
    return node
  }

  function updateArcNode(novelId, nodeId, input) {
    state.arcNodes = state.arcNodes.map((item) =>
      item.novel_id === novelId && item.id === nodeId
        ? {
          ...item,
          story_arc_id: Number(input?.story_arc_id ?? item.story_arc_id),
          title: String(input?.title ?? item.title),
          description: String(input?.description ?? item.description),
          target_chapter: Number(input?.target_chapter ?? item.target_chapter),
          actual_chapter: Number(input?.actual_chapter ?? item.actual_chapter),
          status: String(input?.status ?? item.status),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteArcNode(novelId, nodeId) {
    state.arcNodes = state.arcNodes.filter((item) => item.novel_id !== novelId || item.id !== nodeId)
  }

  function chapterPlans(novelId = state.activeNovelId) {
    return state.chapterPlans.filter((item) => item.novel_id === novelId)
  }

  function updateChapterPlan(novelId, input) {
    const scope = String(input?.scope ?? '')
    const content = String(input?.content ?? '')
    const exists = state.chapterPlans.some((item) => item.novel_id === novelId && item.scope === scope)
    state.chapterPlans = exists
      ? state.chapterPlans.map((item) => item.novel_id === novelId && item.scope === scope ? { ...item, content } : item)
      : [...state.chapterPlans, { novel_id: novelId, scope, content }]
  }

  function timelineEntries(novelId = state.activeNovelId) {
    return state.timelineEntries.filter((item) => item.novel_id === novelId)
  }

  function createTimelineEntry(novelId, input) {
    const entry = {
      id: state.nextTimelineEntryId++,
      novel_id: novelId,
      category: String(input?.category ?? 'foreshadowing'),
      status: String(input?.status ?? 'pending'),
      title: String(input?.title ?? ''),
      content: String(input?.content ?? ''),
      detail_json: String(input?.detail_json ?? ''),
      target_chapter: Number(input?.target_chapter ?? 1),
      importance: Number(input?.importance ?? 3),
      source_chapter_id: Number(input?.source_chapter_id ?? 0),
      source: String(input?.source ?? 'user'),
      resolved_chapter_id: Number(input?.resolved_chapter_id ?? 0),
      created_at: now,
      updated_at: now,
    }
    state.timelineEntries = [...state.timelineEntries, entry]
    return entry
  }

  function updateTimelineEntry(novelId, entryId, input) {
    state.timelineEntries = state.timelineEntries.map((item) =>
      item.novel_id === novelId && item.id === entryId
        ? {
          ...item,
          title: String(input?.title ?? item.title),
          content: String(input?.content ?? item.content),
          detail_json: String(input?.detail_json ?? item.detail_json),
          target_chapter: Number(input?.target_chapter ?? item.target_chapter),
          importance: Number(input?.importance ?? item.importance),
          status: String(input?.status ?? item.status),
          resolved_chapter_id: Number(input?.resolved_chapter_id ?? item.resolved_chapter_id),
          updated_at: now,
        }
        : item,
    )
  }

  function deleteTimelineEntry(novelId, entryId) {
    state.timelineEntries = state.timelineEntries.filter((item) => item.novel_id !== novelId || item.id !== entryId)
  }

  function readerPerspectives(novelId = state.activeNovelId) {
    return state.readerPerspectives.filter((item) => item.novel_id === novelId)
  }

  function createReaderPerspective(novelId, input) {
    const entry = {
      id: state.nextReaderPerspectiveId++,
      novel_id: novelId,
      type: String(input?.type ?? 'known'),
      content: String(input?.content ?? ''),
      related_truth: String(input?.related_truth ?? ''),
      planted_chapter: Number(input?.planted_chapter ?? 1),
      revealed_chapter: Number(input?.revealed_chapter ?? 0),
      created_at: now,
    }
    state.readerPerspectives = [...state.readerPerspectives, entry]
    return entry
  }

  function updateReaderPerspective(novelId, entryId, input) {
    state.readerPerspectives = state.readerPerspectives.map((item) =>
      item.novel_id === novelId && item.id === entryId
        ? {
          ...item,
          type: String(input?.type ?? item.type),
          content: String(input?.content ?? item.content),
          related_truth: String(input?.related_truth ?? item.related_truth),
          planted_chapter: Number(input?.planted_chapter ?? item.planted_chapter),
          revealed_chapter: Number(input?.revealed_chapter ?? item.revealed_chapter),
        }
        : item,
    )
  }

  function deleteReaderPerspective(novelId, entryId) {
    state.readerPerspectives = state.readerPerspectives.filter((item) => item.novel_id !== novelId || item.id !== entryId)
  }

  function preferences(novelId = state.activeNovelId) {
    return {
      global: state.preferences.global.filter((item) => item.is_global),
      novel: state.preferences.novel.filter((item) => item.novel_id === novelId),
    }
  }

  function createPreference(novelId, input) {
    const item = {
      id: state.nextPreferenceId++,
      novel_id: input?.is_global ? 0 : novelId,
      is_global: Boolean(input?.is_global),
      category: String(input?.category ?? '未分类'),
      content: String(input?.content ?? ''),
      created_at: now,
    }
    if (item.is_global) state.preferences.global = [...state.preferences.global, item]
    else state.preferences.novel = [...state.preferences.novel, item]
    return item
  }

  function updatePreference(preferenceId, input) {
    const update = (item) => item.id === preferenceId
      ? {
        ...item,
        category: String(input?.category ?? item.category),
        content: String(input?.content ?? item.content),
        is_global: input?.is_global ?? item.is_global,
      }
      : item
    state.preferences.global = state.preferences.global.map(update)
    state.preferences.novel = state.preferences.novel.map(update)
    return [...state.preferences.global, ...state.preferences.novel].find((item) => item.id === preferenceId) ?? null
  }

  function deletePreference(preferenceId) {
    state.preferences.global = state.preferences.global.filter((item) => item.id !== preferenceId)
    state.preferences.novel = state.preferences.novel.filter((item) => item.id !== preferenceId)
  }

  function writingActivity() {
    return state.writingActivity
  }

  function writingStats() {
    return state.writingStats
  }

  function skills() {
    return state.skills
  }

  function deleteSkill(input) {
    state.skills = state.skills.filter((item) => item.source !== input?.source || item.name !== input?.name)
  }

  function extractStyle() {
    return {
      name: '雨夜留白',
      description: '以短句和停顿保留悬念。',
      raw_content: '---\nname: 雨夜留白\ndescription: 以短句和停顿保留悬念。\ncategory: 写作技法\n---\n用动作间隔保留未说出口的信息。',
      file_path: 'skills/雨夜留白.md',
    }
  }

  async function extractStyleSkillFromSamples(input = {}) {
    const taskId = String(input?.task_id ?? `style-skill-${state.styleSkillExtractionRuns.length + 1}`)
    const sampleIds = Array.isArray(input?.sample_ids) ? input.sample_ids.map(Number) : []
    const skillName = String(input?.skill_name ?? '').trim() || '未命名风格技能'
    const delayMs = Math.max(0, Number(state.nextStyleSkillExtractionDelayMs ?? 0))
    const mode = String(state.nextStyleSkillExtractionMode ?? 'success')
    state.nextStyleSkillExtractionDelayMs = 0
    state.nextStyleSkillExtractionMode = 'success'

    let run = styleSkillRun({
      taskId,
      status: 'running',
      stage: 'model_call',
      progressCompleted: 0,
      progressTotal: Math.max(sampleIds.length, 1),
      sampleIds,
      skillName,
      skillPreview: '',
      skillFilePath: '',
      diagnostics: [],
      completedAt: null,
    })
    upsertStyleSkillRun(run)
    emit('style_skill_extraction:progress', {
      task_id: run.task_id,
      status: run.status,
      stage: run.stage,
      progress_completed: run.progress_completed,
      progress_total: run.progress_total,
      message: '正在抽取风格技能。',
      updated_at: now,
    })

    if (delayMs > 0) {
      await wait(delayMs)
    }

    if (state.cancelledStyleSkillExtractionTaskIds.includes(taskId)) {
      run = styleSkillRun({
        taskId,
        status: 'cancelled',
        stage: 'cancelled',
        progressCompleted: 0,
        progressTotal: Math.max(sampleIds.length, 1),
        sampleIds,
        skillName,
        skillPreview: '',
        skillFilePath: '',
        diagnostics: [copyableDiagnostic('style_skill.cancelled', '抽取已取消', '用户取消', 'CancelStyleSkillExtraction', taskId)],
        completedAt: now,
      })
      upsertStyleSkillRun(run)
      emit('style_skill_extraction:progress', {
        task_id: run.task_id,
        status: run.status,
        stage: run.stage,
        progress_completed: run.progress_completed,
        progress_total: run.progress_total,
        message: '抽取已取消。',
        updated_at: now,
      })
      return run
    }

    if (mode === 'invalid_frontmatter') {
      run = styleSkillRun({
        taskId,
        status: 'failed',
        stage: 'skill_validation',
        progressCompleted: Math.max(sampleIds.length, 1),
        progressTotal: Math.max(sampleIds.length, 1),
        sampleIds,
        skillName,
        skillPreview: '',
        skillFilePath: '',
        diagnostics: [
          copyableDiagnostic(
            'style_skill.invalid_frontmatter',
            '模型返回的技能 Markdown 未通过校验。',
            'Missing required frontmatter fields: category, author, version.',
            'ExtractStyleSkillFromSamples',
            taskId),
        ],
        completedAt: now,
      })
      upsertStyleSkillRun(run)
      return run
    }

    const selected = sampleIds
      .map((sampleId) => state.styleSamples.find((sample) => sample.sample_id === sampleId))
      .filter(Boolean)
    const hashes = selected.map((sample) => sample.source_metadata?.source_hash || `style-sample:${sample.sample_id}`)
    const skillPreview = [
      '---',
      `name: ${skillName}`,
      'description: 从风格素材抽取的可复用文风技能。',
      'category: 风格仿写',
      'mode: auto',
      'author: ai',
      'version: 1',
      `source_sample_ids: ${sampleIds.join(',')}`,
      `source_sample_hashes: ${hashes.join(',')}`,
      'generated_by: style_sample_extraction',
      '---',
      '',
      `# ${skillName}`,
      '',
      '## 仿写要点',
      '- 短句推进，动作留白。',
      '- 让对白承担转折，不解释人物心情。',
    ].join('\n')
    run = styleSkillRun({
      taskId,
      status: 'completed',
      stage: 'skill_preview',
      progressCompleted: Math.max(sampleIds.length, 1),
      progressTotal: Math.max(sampleIds.length, 1),
      sampleIds,
      skillName,
      skillPreview,
      skillFilePath: `skills/${skillName}.md`,
      diagnostics: [copyableDiagnostic('style_skill.preview_ready', '风格技能预览已生成。', `skill_file_path=skills/${skillName}.md`, 'ExtractStyleSkillFromSamples', taskId)],
      completedAt: now,
    })
    upsertStyleSkillRun(run)
    emit('style_skill_extraction:progress', {
      task_id: run.task_id,
      status: run.status,
      stage: run.stage,
      progress_completed: run.progress_completed,
      progress_total: run.progress_total,
      message: '风格技能预览已生成。',
      updated_at: now,
    })
    return run
  }

  function cancelStyleSkillExtraction(input = {}) {
    const taskId = String(input?.task_id ?? '')
    if (!state.cancelledStyleSkillExtractionTaskIds.includes(taskId)) {
      state.cancelledStyleSkillExtractionTaskIds.push(taskId)
    }

    const existing = state.styleSkillExtractionRuns.find((run) => run.task_id === taskId)
    const run = styleSkillRun({
      taskId,
      status: 'cancelled',
      stage: 'cancelled',
      progressCompleted: existing?.progress_completed ?? 0,
      progressTotal: existing?.progress_total ?? 1,
      sampleIds: existing?.sample_ids ?? [],
      skillName: existing?.skill_name ?? '',
      skillPreview: '',
      skillFilePath: '',
      diagnostics: [copyableDiagnostic('style_skill.cancelled', '抽取已取消', String(input?.reason ?? ''), 'CancelStyleSkillExtraction', taskId)],
      completedAt: now,
    })
    upsertStyleSkillRun(run)
    return run
  }

  async function startNarrativePatternExtraction(input = {}) {
    const taskId = String(input?.task_id ?? `narrative-pattern-${state.narrativePatternRuns.length + 1}`)
    const chapterRanges = Array.isArray(input?.chapter_ranges) ? input.chapter_ranges.map(normalizeChapterRange) : []
    const selectedChapterIds = Array.isArray(input?.selected_chapter_ids)
      ? input.selected_chapter_ids.map(Number).filter(Number.isFinite)
      : chapterRangesToMockChapterIds(chapterRanges, Number(input?.novel_id ?? state.activeNovelId))
    const skillName = String(input?.skill_name ?? '').trim() || '叙事模式技能'
    const delayMs = Math.max(0, Number(state.nextNarrativePatternDelayMs ?? 0))
    const mode = String(state.nextNarrativePatternMode ?? 'success')
    state.nextNarrativePatternDelayMs = 0
    state.nextNarrativePatternMode = 'success'

    let run = narrativePatternRun({
      taskId,
      status: 'running',
      stage: 'load_chapters',
      progressCompleted: 0,
      progressTotal: 6,
      chapterRanges,
      selectedChapterIds,
      skillName,
      skillPreview: '',
      diagnostics: [],
      completedAt: null,
    })
    upsertNarrativePatternRun(run)
    state.narrativePatternTraces[taskId] = { task_id: taskId, entries: [] }
    emitNarrativePatternProgress(run, '正在加载并校验章节。', {
      llmStatus: 'idle',
    })

    if (delayMs > 0) {
      await wait(delayMs)
    }

    if (state.cancelledNarrativePatternTaskIds.includes(taskId)) {
      run = narrativePatternRun({
        taskId,
        status: 'cancelled',
        stage: 'cancelled',
        progressCompleted: run.progress_completed,
        progressTotal: run.progress_total,
        chapterRanges,
        selectedChapterIds,
        skillName,
        skillPreview: '',
        diagnostics: [copyableDiagnostic('pattern.cancelled', '叙事模式抽取已取消。', '用户取消', 'CancelNarrativePatternExtraction', taskId)],
        completedAt: now,
      })
      upsertNarrativePatternRun(run)
      emitNarrativePatternProgress(run, '叙事模式抽取已取消。', { llmStatus: 'cancelled' })
      return run
    }

    if (selectedChapterIds.length > 0 && selectedChapterIds.length < 3) {
      const diagnostic = copyableDiagnostic('pattern.insufficient_chapters', '可用章节不足，无法抽取叙事模式。', '至少需要 3 章且正文长度达到最低阈值。', 'StartNarrativePatternExtraction', taskId)
      run = narrativePatternRun({
        taskId,
        status: 'failed',
        stage: 'load_chapters',
        progressCompleted: 1,
        progressTotal: 6,
        chapterRanges,
        selectedChapterIds,
        skillName,
        skillPreview: '',
        diagnostics: [diagnostic],
        completedAt: now,
      })
      upsertNarrativePatternRun(run)
      appendNarrativePatternTrace(taskId, 'load_chapters', [diagnostic])
      emitNarrativePatternProgress(run, diagnostic.message, { llmStatus: 'failed' })
      return run
    }

    run = updateNarrativePatternRunProgress(run, 'boundary_detection', 1)
    appendNarrativePatternTrace(taskId, 'boundary_detection', [])
    emitNarrativePatternProgress(run, '正在识别叙事边界。', {
      llmStatus: 'calling',
      tokenEstimate: 1800,
      boundaryCount: 2,
    })

    if (mode === 'invalid_model') {
      const diagnostic = copyableDiagnostic('pattern.invalid_boundary_json', '模型返回的边界 JSON 无法解析。', 'Expected valid narrative boundary JSON.', 'StartNarrativePatternExtraction', taskId)
      run = narrativePatternRun({
        taskId,
        status: 'failed',
        stage: 'boundary_detection',
        progressCompleted: 1,
        progressTotal: 6,
        chapterRanges,
        selectedChapterIds,
        skillName,
        skillPreview: '',
        diagnostics: [diagnostic],
        completedAt: now,
      })
      upsertNarrativePatternRun(run)
      appendNarrativePatternTrace(taskId, 'boundary_detection', [diagnostic])
      emitNarrativePatternProgress(run, diagnostic.message, {
        llmStatus: 'failed',
        boundaryCount: 0,
      })
      return run
    }

    run = updateNarrativePatternRunProgress(run, 'chapter_summary', 2)
    appendNarrativePatternTrace(taskId, 'chapter_summary', [])
    emitNarrativePatternProgress(run, '正在提取章节摘要：批次 1/2。', {
      llmStatus: 'calling',
      batchIndex: 1,
      batchTotal: 2,
      tokenEstimate: 2200,
      boundaryCount: 2,
      summaryCount: Math.max(1, Math.floor(selectedChapterIds.length / 2)),
    })

    run = updateNarrativePatternRunProgress(run, 'chapter_summary', 3)
    appendNarrativePatternTrace(taskId, 'chapter_summary', [])
    emitNarrativePatternProgress(run, '章节摘要已完成。', {
      llmStatus: 'completed',
      batchIndex: 2,
      batchTotal: 2,
      tokenEstimate: 2400,
      boundaryCount: 2,
      summaryCount: Math.max(selectedChapterIds.length, 1),
    })

    run = updateNarrativePatternRunProgress(run, 'phase_compression', 4)
    appendNarrativePatternTrace(taskId, 'phase_compression', [])
    emitNarrativePatternProgress(run, '正在压缩叙事阶段：轮次 1，批次 1/1。', {
      llmStatus: 'calling',
      round: 1,
      batchIndex: 1,
      batchTotal: 1,
      tokenEstimate: 2600,
      boundaryCount: 2,
      summaryCount: Math.max(selectedChapterIds.length, 1),
      phaseCount: 2,
    })

    run = updateNarrativePatternRunProgress(run, 'skill_generation', 5)
    appendNarrativePatternTrace(taskId, 'skill_generation', [])
    emitNarrativePatternProgress(run, '正在生成叙事模式技能。', {
      llmStatus: 'calling',
      boundaryCount: 2,
      summaryCount: Math.max(selectedChapterIds.length, 1),
      phaseCount: 2,
    })

    const rangeText = chapterRanges.map((range) => `${range.start_chapter}-${range.end_chapter}`).join(',')
    const skillPreview = [
      '---',
      `name: ${skillName}`,
      'description: 从章节结构抽取的叙事模式技能。',
      'category: 叙事结构',
      'mode: auto',
      'author: ai',
      'version: 1',
      'generated_by: narrative_pattern_extraction',
      `source_chapter_ranges: ${rangeText}`,
      `source_chapter_ids: ${selectedChapterIds.join(',')}`,
      '---',
      '',
      `# ${skillName}`,
      '',
      '## 边界提示',
      '- 1-3：雨夜线索压低信息量。',
      '- 4-6：证词冲突推动反转。',
      '',
      '## 章节摘要',
      '- 第1章以桌面水痕触发调查。',
      '- 第3章用钟楼回声制造误导。',
      '',
      '## 阶段压缩',
      '- 雨夜压迫到证据反转：让证词冲突逐步重组线索。',
    ].join('\n')

    run = narrativePatternRun({
      taskId,
      status: 'completed',
      stage: 'completed',
      progressCompleted: 6,
      progressTotal: 6,
      chapterRanges,
      selectedChapterIds,
      skillName,
      skillPreview,
      diagnostics: [copyableDiagnostic('pattern.preview_ready', '叙事模式技能预览已生成。', `skill_name=${skillName}`, 'StartNarrativePatternExtraction', taskId)],
      completedAt: now,
    })
    upsertNarrativePatternRun(run)
    emitNarrativePatternProgress(run, '叙事模式技能预览已生成。', {
      llmStatus: 'completed',
      boundaryCount: 2,
      summaryCount: Math.max(selectedChapterIds.length, 1),
      phaseCount: 2,
    })
    return run
  }

  function cancelNarrativePatternExtraction(input = {}) {
    const taskId = String(input?.task_id ?? '')
    if (!state.cancelledNarrativePatternTaskIds.includes(taskId)) {
      state.cancelledNarrativePatternTaskIds.push(taskId)
    }

    const existing = state.narrativePatternRuns.find((run) => run.task_id === taskId)
    const run = narrativePatternRun({
      taskId,
      status: 'cancelled',
      stage: 'cancelled',
      progressCompleted: existing?.progress_completed ?? 0,
      progressTotal: existing?.progress_total ?? 6,
      chapterRanges: existing?.chapter_ranges ?? [],
      selectedChapterIds: existing?.selected_chapter_ids ?? [],
      skillName: existing?.skill_name ?? '',
      skillPreview: '',
      diagnostics: [copyableDiagnostic('pattern.cancelled', '叙事模式抽取已取消。', String(input?.reason ?? ''), 'CancelNarrativePatternExtraction', taskId)],
      completedAt: now,
    })
    upsertNarrativePatternRun(run)
    appendNarrativePatternTrace(taskId, 'cancelled', run.diagnostics)
    emitNarrativePatternProgress(run, '叙事模式抽取已取消。', { llmStatus: 'cancelled' })
    return run
  }

  function narrativePatternRun({
    taskId,
    status,
    stage,
    progressCompleted,
    progressTotal,
    chapterRanges,
    selectedChapterIds,
    skillName,
    skillPreview,
    diagnostics,
    completedAt,
  }) {
    return {
      task_id: taskId,
      novel_id: state.activeNovelId,
      status,
      stage,
      progress_completed: progressCompleted,
      progress_total: progressTotal,
      chapter_ranges: chapterRanges,
      selected_chapter_ids: selectedChapterIds,
      skill_name: skillName,
      skill_preview: skillPreview,
      diagnostics,
      created_at: now,
      updated_at: now,
      completed_at: completedAt,
    }
  }

  function updateNarrativePatternRunProgress(run, stage, progressCompleted) {
    const updated = { ...run, stage, progress_completed: progressCompleted, updated_at: now }
    upsertNarrativePatternRun(updated)
    return updated
  }

  function upsertNarrativePatternRun(run) {
    state.narrativePatternRuns = [
      run,
      ...state.narrativePatternRuns.filter((item) => item.task_id !== run.task_id),
    ]
  }

  function appendNarrativePatternTrace(taskId, stage, diagnostics) {
    const trace = state.narrativePatternTraces[taskId] ?? { task_id: taskId, entries: [] }
    const nextIndex = trace.entries.length + 1
    trace.entries = [
      ...trace.entries,
      {
        trace_id: `${taskId}-trace-${String(nextIndex).padStart(2, '0')}`,
        stage,
        input_hash: `sha256:mock-${stage}-input-${nextIndex}`,
        output_hash: `sha256:mock-${stage}-output-${nextIndex}`,
        diagnostics,
        created_at: now,
      },
    ]
    state.narrativePatternTraces[taskId] = trace
  }

  function emitNarrativePatternProgress(run, message, options = {}) {
    emit('narrative_pattern_extraction:progress', {
      task_id: run.task_id,
      status: run.status,
      stage: run.stage,
      progress_completed: run.progress_completed,
      progress_total: run.progress_total,
      message,
      updated_at: now,
      llm_status: options.llmStatus ?? '',
      round: options.round ?? null,
      batch_index: options.batchIndex ?? null,
      batch_total: options.batchTotal ?? null,
      token_estimate: options.tokenEstimate ?? null,
      boundary_count: options.boundaryCount ?? null,
      summary_count: options.summaryCount ?? null,
      phase_count: options.phaseCount ?? null,
    })
  }

  function normalizeChapterRange(range = {}) {
    return {
      start_chapter: Number(range.start_chapter ?? 0),
      end_chapter: Number(range.end_chapter ?? 0),
    }
  }

  function chapterRangesToMockChapterIds(ranges, novelId = state.activeNovelId) {
    const byNumber = new Map(chapters(novelId).map((chapter) => [chapter.chapter_number, chapter.id]))
    const ids = []
    for (const range of ranges) {
      for (let chapterNumber = range.start_chapter; chapterNumber <= range.end_chapter; chapterNumber += 1) {
        const id = byNumber.get(chapterNumber)
        if (id != null) ids.push(id)
      }
    }
    return ids
  }

  function searchStyleSamples(input = {}) {
    const novelId = input?.novel_id == null ? null : Number(input.novel_id)
    const includeGlobal = Boolean(input?.include_global)
    const query = normalizeSearchText(input?.query)
    const tags = normalizeStyleTags(input?.tags)
    const page = Math.max(1, Number(input?.page ?? 1))
    const size = Math.max(1, Math.min(100, Number(input?.size ?? 10)))
    const filtered = state.styleSamples
      .filter((sample) => matchesStyleScope(sample, novelId, includeGlobal))
      .filter((sample) => matchesStyleQuery(sample, query))
      .filter((sample) => matchesStyleTags(sample, tags))
      .sort((left, right) => {
        const timeDelta = Date.parse(right.updated_at) - Date.parse(left.updated_at)
        return timeDelta || right.sample_id - left.sample_id
      })
    const total = filtered.length
    const items = filtered
      .slice((page - 1) * size, page * size)
      .map(styleSampleSummary)
    return pagedResult(items, page, size, total)
  }

  function getStyleSample(input = {}) {
    const sampleId = Number(input?.sample_id ?? 0)
    return state.styleSamples.find((sample) => sample.sample_id === sampleId) ?? null
  }

  function createStyleSample(input = {}) {
    const sampleId = state.nextStyleSampleId++
    const timestamp = styleSampleTimestamp(sampleId)
    const sample = normalizeStyleSampleInput({
      ...input,
      sample_id: sampleId,
      created_at: timestamp,
      updated_at: timestamp,
    })
    state.styleSamples = [sample, ...state.styleSamples]
    return styleSampleSummary(sample)
  }

  function updateStyleSample(input = {}) {
    const sampleId = Number(input?.sample_id ?? 0)
    const current = state.styleSamples.find((sample) => sample.sample_id === sampleId)
    if (!current) throw new Error(`Unknown style sample ${sampleId}`)
    const updated = normalizeStyleSampleInput({
      ...input,
      sample_id: sampleId,
      created_at: current.created_at,
      updated_at: styleSampleTimestamp(sampleId + 10),
    })
    state.styleSamples = state.styleSamples.map((sample) => sample.sample_id === sampleId ? updated : sample)
    return styleSampleSummary(updated)
  }

  function deleteStyleSample(input = {}) {
    if (state.failNextStyleSampleDelete) {
      state.failNextStyleSampleDelete = false
      throw new Error('模拟样本删除失败')
    }

    const sampleId = Number(input?.sample_id ?? 0)
    state.styleSamples = state.styleSamples.filter((sample) => sample.sample_id !== sampleId)
  }

  function styleSkillRun({
    taskId,
    status,
    stage,
    progressCompleted,
    progressTotal,
    sampleIds,
    skillName,
    skillPreview,
    skillFilePath,
    diagnostics,
    completedAt,
  }) {
    return {
      task_id: taskId,
      status,
      stage,
      progress_completed: progressCompleted,
      progress_total: progressTotal,
      sample_ids: sampleIds,
      skill_name: skillName,
      skill_preview: skillPreview,
      skill_file_path: skillFilePath,
      diagnostics,
      created_at: now,
      updated_at: now,
      completed_at: completedAt,
    }
  }

  function upsertStyleSkillRun(run) {
    state.styleSkillExtractionRuns = [
      run,
      ...state.styleSkillExtractionRuns.filter((item) => item.task_id !== run.task_id),
    ]
  }

  function copyableDiagnostic(code, message, detail, operation, taskId) {
    return {
      code,
      message,
      detail,
      operation,
      task_id: taskId,
      run_id: null,
      bridge_method: operation,
      timestamp: now,
    }
  }

  function normalizeStyleSampleInput(input) {
    const isGlobal = Boolean(input?.is_global)
    const novelId = isGlobal ? null : Number(input?.novel_id ?? state.activeNovelId)
    const content = String(input?.content ?? '').trim()
    const name = String(input?.name ?? '').trim() || '未命名样本'
    const tags = normalizeStyleTags(input?.tags)
    return {
      sample_id: Number(input.sample_id),
      novel_id: novelId,
      is_global: isGlobal,
      name,
      content,
      preview: buildStylePreview(content),
      tags,
      stats_schema_version: 'style_sample_stats_v2',
      stats: deriveStyleStats(content),
      source_metadata: input?.source_metadata ?? null,
      created_at: input.created_at,
      updated_at: input.updated_at,
    }
  }

  function styleSampleSummary(sample) {
    const summary = { ...sample }
    delete summary.content
    return summary
  }

  function matchesStyleScope(sample, novelId, includeGlobal) {
    if (sample.is_global) return includeGlobal
    return novelId != null && sample.novel_id === novelId
  }

  function matchesStyleQuery(sample, query) {
    if (!query) return true
    return [
      sample.name,
      sample.content,
      sample.preview,
      ...sample.tags,
    ].some((value) => normalizeSearchText(value).includes(query))
  }

  function matchesStyleTags(sample, tags) {
    return tags.length === 0 ||
      tags.every((required) => sample.tags.some((tag) => normalizeSearchText(tag) === normalizeSearchText(required)))
  }

  function normalizeSearchText(value) {
    return String(value ?? '').trim().toLowerCase()
  }

  function normalizeStyleTags(value) {
    const raw = Array.isArray(value) ? value : [value]
    const tags = []
    const seen = new Set()
    for (const item of raw) {
      for (const part of String(item ?? '').split(/[;；,，\r\n]+/)) {
        const tag = part.trim()
        const key = tag.toLowerCase()
        if (tag && !seen.has(key)) {
          seen.add(key)
          tags.push(tag)
        }
      }
    }

    return tags
  }

  function buildStylePreview(content) {
    return content.replace(/\s+/g, ' ').trim().slice(0, 120)
  }

  function deriveStyleStats(content) {
    const compact = content.replace(/\s+/g, '')
    const sentenceLengths = content
      .split(/[。！？!?；;\n]+/)
      .map((part) => part.replace(/\s+/g, '').length)
      .filter(Boolean)
    const characterCount = compact.length
    const punctuationCount = Array.from(content).filter((ch) => /\p{P}/u.test(ch)).length
    const quoteCount = Array.from(content).filter((ch) => /[“”「」『』"']/u.test(ch)).length
    return styleSampleStats({
      characterCount,
      wordCount: Math.max(0, compact.length - punctuationCount),
      sentenceCount: sentenceLengths.length,
      sentenceLengths,
      averageSentenceChars: averageNumber(sentenceLengths),
      sentenceLengthStdDev: standardDeviation(sentenceLengths),
      punctuationPer100Chars: characterCount ? roundNumber((punctuationCount / characterCount) * 100) : 0,
      quoteDensity: characterCount ? roundNumber((quoteCount / characterCount) * 100) : 0,
      paragraphCount: content.split(/\n+/).filter((part) => part.trim()).length,
      averageParagraphChars: averageNumber(content.split(/\n+/).map((part) => part.replace(/\s+/g, '').length).filter(Boolean)),
      dialogueRatio: characterCount ? roundNumber((quoteCount / characterCount)) : 0,
      interiorityRatio: /想|心里|知道|觉得|犹豫/.test(content) ? 0.35 : 0,
      sensoryRatio: /雨|风|声|光|冷|潮|窗/.test(content) ? 0.45 : 0,
    })
  }

  function styleSampleStats(overrides = {}) {
    return {
      schema_version: 'style_sample_stats_v2',
      character_count: overrides.characterCount ?? 0,
      word_count: overrides.wordCount ?? 0,
      sentence_count: overrides.sentenceCount ?? 0,
      sentence_length_distribution: overrides.sentenceLengths ?? [],
      average_sentence_chars: overrides.averageSentenceChars ?? 0,
      sentence_length_std_dev: overrides.sentenceLengthStdDev ?? 0,
      punctuation_per_100_chars: overrides.punctuationPer100Chars ?? 0,
      quote_density: overrides.quoteDensity ?? 0,
      paragraph_count: overrides.paragraphCount ?? 0,
      average_paragraph_chars: overrides.averageParagraphChars ?? 0,
      dialogue_ratio: overrides.dialogueRatio ?? 0,
      interiority_ratio: overrides.interiorityRatio ?? 0,
      sensory_ratio: overrides.sensoryRatio ?? 0,
    }
  }

  function styleSampleTimestamp(seed) {
    return `2026-07-05T12:${String(Math.min(59, 10 + seed)).padStart(2, '0')}:00.000Z`
  }

  function averageNumber(values) {
    return values.length ? roundNumber(values.reduce((total, value) => total + value, 0) / values.length) : 0
  }

  function standardDeviation(values) {
    if (!values.length) return 0
    const average = values.reduce((total, value) => total + value, 0) / values.length
    return roundNumber(Math.sqrt(values.reduce((total, value) => total + ((value - average) ** 2), 0) / values.length))
  }

  function roundNumber(value) {
    return Math.round(value * 10000) / 10000
  }

  function llmConfig() {
    return {
      providers: [
        {
          key: 'mock',
          name: 'Mock Provider',
          base_url: 'https://api.example.com/v1',
          endpoint_type: 'chat',
          chat_url: '',
          api_key: '',
          platform_url: '',
          help_text: '',
          temperature: 0.7,
          source: 'builtin',
          builtin_models: [
            {
              id: 'gpt',
              name: 'Mock GPT',
              context_window: 128000,
              max_output_tokens: 4096,
              supports_thinking: true,
              reasoning_levels: ['high'],
              supports_vision: false,
            },
          ],
          custom_models: [],
        },
      ],
    }
  }

  function embeddingConfig() {
    return {
      provider_key: 'onnx',
      endpoint_url: '',
      api_key: '',
      model_id: 'bge-small-zh-v1.5',
      dimensions: 512,
      user: '',
      provider_type: 'onnx',
      onnx_model_path: '',
      onnx_vocab_path: '',
      max_sequence_length: 512,
      normalize_embeddings: true,
    }
  }

  function sqliteVecStatus() {
    return {
      available: true,
      status: 'ready',
      runtime_identifier: 'mock-runtime',
      file_name: 'sqlite_vec_mock.dll',
      error: '',
    }
  }

  function buildReferenceStyleProfile(input = {}) {
    const novelId = Number(input?.novel_id ?? state.activeNovelId)
    const anchorIds = normalizeNumericIds(input?.anchor_ids)
    const styleSampleIds = normalizeNumericIds(input?.style_sample_ids)
    if (anchorIds.length === 0 && styleSampleIds.length === 0) {
      throw new Error('BuildReferenceStyleProfile requires at least one anchor or style sample.')
    }

    const anchors = anchorIds.map((anchorId) => {
      const anchor = referenceAnchors().find((item) => item.anchor_id === anchorId)
      if (!anchor) throw new Error(`Reference anchor ${anchorId} is not available.`)
      return anchor
    })
    const samples = styleSampleIds.map((sampleId) => {
      const sample = state.styleSamples.find((item) => item.sample_id === sampleId)
      if (!sample) throw new Error(`Style sample ${sampleId} is not available.`)
      if (!sample.is_global && Number(sample.novel_id) !== novelId) {
        throw new Error(`Style sample ${sampleId} is not available for this novel.`)
      }
      return sample
    })

    const profileId = state.nextReferenceStyleProfileId++
    const sourceHashes = [
      ...anchors.map((anchor) => anchor.source_file_hash || `reference-anchor:${anchor.anchor_id}`),
      ...samples.map((sample) => sample.source_metadata?.source_hash || `style-sample:${sample.sample_id}`),
    ]
    const sampleEvidence = samples.map((sample, index) => {
      const contentLength = String(sample.content ?? '').length
      return {
        evidence_id: `mock-style-profile-${profileId}-sample-${sample.sample_id}-${index + 1}`,
        profile_id: profileId,
        anchor_id: 0,
        source_segment_id: `style-sample:${sample.sample_id}:stats`,
        material_id: `style-sample-material:${sample.sample_id}:stats`,
        feature_key: 'dialogue_ratio',
        label: 'style_sample_stats',
        start_offset: 0,
        end_offset: Math.max(1, Math.min(contentLength, 120)),
        text_hash: sample.source_metadata?.source_hash || `style-sample:${sample.sample_id}:content-hash`,
        confidence: 0.82,
        analyzer_source: 'deterministic_baseline',
        source_type: 'style_sample',
        style_sample_id: sample.sample_id,
      }
    })
    const anchorEvidence = anchors.map((anchor, index) => ({
      evidence_id: `mock-style-profile-${profileId}-anchor-${anchor.anchor_id}-${index + 1}`,
      profile_id: profileId,
      anchor_id: anchor.anchor_id,
      source_segment_id: `reference-anchor:${anchor.anchor_id}:summary`,
      material_id: `reference-material:${anchor.anchor_id}:summary`,
      feature_key: 'rhythm',
      label: 'reference_anchor_baseline',
      start_offset: 0,
      end_offset: 1,
      text_hash: anchor.source_file_hash || `reference-anchor:${anchor.anchor_id}:hash`,
      confidence: 0.78,
      analyzer_source: 'deterministic_baseline',
      source_type: 'reference_anchor',
      style_sample_id: null,
    }))
    const evidenceSpans = [...sampleEvidence, ...anchorEvidence]
    const profile = {
      profile_id: profileId,
      novel_id: novelId,
      title: String(input?.title ?? '').trim() || '样本风格画像',
      description: String(input?.description ?? ''),
      status: 'active',
      analyzer_version: 'reference-style-deterministic-v1',
      feature_schema_version: 'style-profile-v1',
      analyzer_source: 'deterministic_baseline',
      source_anchor_ids: anchorIds,
      source_hashes: sourceHashes,
      source_style_sample_ids: styleSampleIds,
      allowed_license_statuses: Array.isArray(input?.allowed_license_statuses) ? input.allowed_license_statuses : [],
      allowed_source_trust_levels: Array.isArray(input?.allowed_source_trust_levels) ? input.allowed_source_trust_levels : [],
      aggregate_confidence: samples.length > 0 ? 0.82 : 0.78,
      features: styleProfileFeatures(samples, evidenceSpans),
      evidence_spans: evidenceSpans,
      created_at: now,
      updated_at: now,
      archived_at: null,
    }

    state.referenceStyleProfiles = [
      profile,
      ...state.referenceStyleProfiles.filter((item) => item.profile_id !== profile.profile_id),
    ]
    const buildId = String(input?.build_id ?? `mock-style-build-${profileId}`)
    state.referenceStyleProfileBuildStatuses[buildId] = {
      build_id: buildId,
      novel_id: novelId,
      profile_id: profileId,
      title: profile.title,
      status: 'completed',
      stage: 'completed',
      progress_completed: 7,
      progress_total: 7,
      anchor_ids: anchorIds,
      source_hashes: sourceHashes,
      style_sample_ids: styleSampleIds,
      diagnostics: [],
      error_code: null,
      error_message: null,
      created_at: now,
      updated_at: now,
      completed_at: now,
      cancelled_at: null,
    }
    return profile
  }

  function referenceStyleProfileBuildStatus(input = {}) {
    return state.referenceStyleProfileBuildStatuses[String(input?.build_id ?? '')] ?? null
  }

  function styleProfileFeatures(samples, evidenceSpans) {
    const sampleEvidenceIds = evidenceSpans
      .filter((evidence) => evidence.source_type === 'style_sample')
      .map((evidence) => evidence.evidence_id)
    const evidenceIds = sampleEvidenceIds.length > 0
      ? sampleEvidenceIds
      : evidenceSpans.map((evidence) => evidence.evidence_id)
    const numericKeys = [
      ['average_sentence_chars', 'chars'],
      ['sentence_length_std_dev', 'chars'],
      ['punctuation_per_100_chars', 'per_100_chars'],
      ['quote_density', 'ratio'],
      ['average_paragraph_chars', 'chars'],
      ['dialogue_ratio', 'ratio'],
      ['interiority_ratio', 'ratio'],
      ['sensory_ratio', 'ratio'],
    ]
    return {
      numeric_features: numericKeys
        .map(([key, unit]) => ({
          feature_key: key,
          value: averageStyleSampleStat(samples, key),
          unit,
          confidence: samples.length > 0 ? 0.82 : 0.65,
          evidence_ids: evidenceIds,
        }))
        .filter((feature) => feature.value > 0 || samples.length > 0),
      distribution_features: [],
      categorical_features: samples.length > 0 ? [{
        feature_key: 'rhythm',
        label: averageStyleSampleStat(samples, 'average_sentence_chars') <= 16 ? 'short_direct' : 'balanced',
        weight: 0.72,
        confidence: 0.78,
        evidence_ids: evidenceIds,
      }] : [],
    }
  }

  function averageStyleSampleStat(samples, key) {
    if (samples.length === 0) return 0
    return roundNumber(samples.reduce((total, sample) => total + Number(sample.stats?.[key] ?? 0), 0) / samples.length)
  }

  function normalizeNumericIds(value) {
    if (!Array.isArray(value)) return []
    const seen = new Set()
    const ids = []
    for (const item of value) {
      const id = Number(item)
      if (Number.isInteger(id) && id > 0 && !seen.has(id)) {
        seen.add(id)
        ids.push(id)
      }
    }
    return ids
  }

  function referenceAnchors() {
    return [
      ...state.referenceAnchors,
      ...state.createdReferenceAnchors,
    ].map(sanitizeReferenceAnchor)
  }

  function sanitizeReferenceAnchor(anchor) {
    return {
      ...anchor,
      source_path: '',
    }
  }

  function createReferenceAnchor(input) {
    const duplicate = findExistingReferenceAnchorForInput(input)
    if (duplicate) return sanitizeReferenceAnchor(duplicate)

    const anchor = {
      anchor_id: 200 + state.createdReferenceAnchors.length,
      novel_id: input?.novel_id ?? state.activeNovelId,
      title: String(input?.title ?? ''),
      author: String(input?.author ?? ''),
      source_path: String(input?.source_path ?? ''),
      source_kind: String(input?.source_kind ?? ''),
      license_status: String(input?.license_status ?? ''),
      visibility: String(input?.visibility ?? 'private'),
      source_trust: String(input?.source_trust ?? 'imported'),
      owner_scope: input?.visibility === 'workspace' ? 'workspace_corpus' : 'novel',
      owner_novel_id: input?.visibility === 'workspace' ? null : input?.novel_id ?? state.activeNovelId,
      user_tags: Array.isArray(input?.user_tags) ? input.user_tags : [],
      source_file_hash: `hash-created-${state.createdReferenceAnchors.length}`,
      build_version: 'mock-reference-v1',
      status: 'ready',
      created_at: now,
      updated_at: now,
    }
    state.createdReferenceAnchors.push(anchor)
    return sanitizeReferenceAnchor(anchor)
  }

  function findExistingReferenceAnchorForInput(input) {
    const identity = referenceAnchorIdentityForInput(input)
    if (!identity.source_path) return null

    return [...state.referenceAnchors, ...state.createdReferenceAnchors]
      .find((anchor) => {
        const anchorIdentity = referenceAnchorIdentityForAnchor(anchor)
        return anchorIdentity.source_path === identity.source_path &&
          anchorIdentity.source_kind === identity.source_kind &&
          anchorIdentity.visibility === identity.visibility &&
          anchorIdentity.scope_key === identity.scope_key
      }) ?? null
  }

  function referenceAnchorIdentityForInput(input = {}) {
    const visibility = String(input?.visibility ?? 'private')
    const scopeKey = visibility === 'workspace'
      ? 'workspace:0'
      : `novel:${Number(input?.novel_id ?? state.activeNovelId)}`
    return {
      visibility,
      scope_key: scopeKey,
      source_kind: normalizeReferenceSourceIdentityPart(input?.source_kind),
      source_path: normalizeReferenceSourcePath(input?.source_path),
    }
  }

  function referenceAnchorIdentityForAnchor(anchor = {}) {
    const visibility = String(anchor?.visibility ?? 'private')
    const ownerScope = String(anchor?.owner_scope ?? '')
    const scopeKey = visibility === 'workspace' || ownerScope === 'workspace_corpus'
      ? 'workspace:0'
      : `novel:${Number(anchor?.novel_id ?? state.activeNovelId)}`
    return {
      visibility,
      scope_key: scopeKey,
      source_kind: normalizeReferenceSourceIdentityPart(anchor?.source_kind),
      source_path: normalizeReferenceSourcePath(anchor?.source_path),
    }
  }

  function normalizeReferenceSourceIdentityPart(value) {
    return String(value ?? '').trim().toLowerCase()
  }

  function normalizeReferenceSourcePath(value) {
    return normalizeReferenceSourceIdentityPart(value).replaceAll('/', '\\')
  }

  function createReferenceAnchors(input) {
    const anchors = Array.isArray(input?.anchors) ? input.anchors : []
    return anchors.map((anchor) => createReferenceAnchor(anchor))
  }

  function createReferenceAnchorsWithResult(input) {
    const anchors = Array.isArray(input?.anchors) ? input.anchors : []
    const succeeded = []
    const failed = []

    anchors.forEach((anchor, index) => {
      if (shouldMockReferenceAnchorPartialFailure(anchor)) {
        failed.push(mockReferenceAnchorFailure(anchor, index))
        return
      }

      succeeded.push(createReferenceAnchor(anchor))
    })

    return {
      succeeded,
      failed,
      total_count: anchors.length,
      succeeded_count: succeeded.length,
      failed_count: failed.length,
    }
  }

  function shouldMockReferenceAnchorPartialFailure(anchor) {
    const values = [
      anchor?.title,
      anchor?.source_path,
      anchor?.source_kind,
      ...(Array.isArray(anchor?.user_tags) ? anchor.user_tags : []),
    ]
    return values.some((value) => String(value ?? '').toLowerCase().includes('mock-partial-fail'))
  }

  function mockReferenceAnchorFailure(anchor, index) {
    const failedAnchor = ensureFailedReferenceAnchor(anchor)
    return {
      index,
      title: String(anchor?.title ?? ''),
      source_kind: String(anchor?.source_kind ?? ''),
      source_identity: `mock-source-${failedAnchor.anchor_id}`,
      diagnostic: '模拟语料解析失败；本地路径已隐藏。',
      retry_available: true,
    }
  }

  function ensureFailedReferenceAnchor(input) {
    const duplicate = findExistingReferenceAnchorForInput(input)
    if (duplicate) return duplicate

    const anchor = {
      anchor_id: 200 + state.createdReferenceAnchors.length,
      novel_id: input?.novel_id ?? state.activeNovelId,
      title: String(input?.title ?? ''),
      author: String(input?.author ?? ''),
      source_path: String(input?.source_path ?? ''),
      source_kind: String(input?.source_kind ?? ''),
      license_status: String(input?.license_status ?? ''),
      visibility: String(input?.visibility ?? 'private'),
      source_trust: String(input?.source_trust ?? 'imported'),
      owner_scope: input?.visibility === 'workspace' ? 'workspace_corpus' : 'novel',
      owner_novel_id: input?.visibility === 'workspace' ? null : input?.novel_id ?? state.activeNovelId,
      user_tags: Array.isArray(input?.user_tags) ? input.user_tags : [],
      source_file_hash: `unavailable:mock-created-${state.createdReferenceAnchors.length}`,
      build_version: 'mock-reference-v1',
      status: 'failed_import',
      created_at: now,
      updated_at: now,
    }
    state.createdReferenceAnchors.push(anchor)
    state.referenceBuildStatuses[String(anchor.anchor_id)] = {
      novel_id: 42,
      anchor_id: anchor.anchor_id,
      status: 'failed_import',
      stage: 'failed_import',
      source_segment_count: 0,
      material_count: 0,
      slot_count: 0,
      vector_count: 0,
      last_error: '模拟语料解析失败；本地路径已隐藏。',
      updated_at: now,
    }
    return anchor
  }

  function referenceBuildStatus(anchorId) {
    const key = String(anchorId)
    if (state.referenceBuildStatuses[key]) {
      return state.referenceBuildStatuses[key]
    }

    const anchor = [...state.referenceAnchors, ...state.createdReferenceAnchors]
      .find((item) => Number(item.anchor_id) === Number(anchorId))
    if (anchor?.status === 'failed_extraction') {
      return failedExtractionBuildStatus(anchorId)
    }
    if (anchor?.status === 'failed_slotting') {
      return failedSlottingBuildStatus(anchorId)
    }
    if (Number(anchorId) === 107 && anchor?.status === 'failed_import') {
      return missingSourceStartupFailedImportBuildStatus(anchorId)
    }
    if (Number(anchorId) === 108) {
      return recoveredSlotsDetectedStartupBuildStatus(anchorId)
    }

    return {
      novel_id: 42,
      anchor_id: anchorId,
      status: 'ready',
      stage: 'completed',
      source_segment_count: 3,
      material_count: 6,
      slot_count: 2,
      vector_count: 0,
      last_error: '',
      updated_at: now,
    }
  }

  function rebuildReferenceAnchor(anchorId) {
    const status = Number(anchorId) === 102
      ? recoveredFailedImportBuildStatus(anchorId)
      : Number(anchorId) === 103
        ? failedImportRetryFailureBuildStatus(anchorId)
        : Number(anchorId) === 104
          ? startupRecoveredBuildStatus(anchorId)
          : Number(anchorId) === 105
            ? recoveredFailedExtractionBuildStatus(anchorId)
            : Number(anchorId) === 106
              ? recoveredFailedSlottingBuildStatus(anchorId)
              : Number(anchorId) === 107
                ? recoveredMissingSourceStartupBuildStatus(anchorId)
                : Number(anchorId) === 108
                  ? recoveredSlotsDetectedStartupBuildStatus(anchorId)
      : referenceBuildStatus(anchorId)
    state.referenceBuildStatuses[String(anchorId)] = status
    if (Number(anchorId) === 105 && status.status === 'ready') {
      ensureRecoveredExtractionMaterial(anchorId)
    }
    if (Number(anchorId) === 106 && status.status === 'ready') {
      ensureFailedSlottingMaterial(anchorId)
    }
    if (Number(anchorId) === 107 && status.status === 'ready') {
      ensureMissingSourceStartupMaterial(anchorId)
    }
    if (Number(anchorId) === 108 && status.status === 'ready') {
      ensureSlotsDetectedStartupMaterial(anchorId)
    }
    const nextAnchorStatus = status.status
    state.referenceAnchors = state.referenceAnchors.map(anchor =>
      anchor.anchor_id === anchorId ? { ...anchor, status: nextAnchorStatus, updated_at: now } : anchor)
    state.createdReferenceAnchors = state.createdReferenceAnchors.map(anchor =>
      anchor.anchor_id === anchorId ? { ...anchor, status: nextAnchorStatus, updated_at: now } : anchor)
    return status
  }

  function failedImportRetryFailureBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: anchorId,
      status: 'failed_import',
      stage: 'failed_import',
      source_segment_count: 0,
      material_count: 0,
      slot_count: 0,
      vector_count: 0,
      last_error: '重试仍失败；本地路径已脱敏。',
      updated_at: now,
    }
  }

  function failedExtractionBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: Number(anchorId),
      status: 'failed_extraction',
      stage: 'extracting_materials',
      source_segment_count: 2,
      material_count: 0,
      slot_count: 0,
      vector_count: 0,
      last_error: '材料抽取未产生可用输出；来源片段可检查，正文已脱敏。',
      updated_at: now,
    }
  }

  function recoveredFailedExtractionBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: Number(anchorId),
      status: 'ready',
      stage: 'embedding',
      source_segment_count: 2,
      material_count: 1,
      slot_count: 1,
      vector_count: 1,
      last_error: '',
      updated_at: now,
    }
  }

  function failedSlottingBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: Number(anchorId),
      status: 'failed_slotting',
      stage: 'detecting_slots',
      source_segment_count: 2,
      material_count: 1,
      slot_count: 0,
      vector_count: 0,
      last_error: '槽位检测失败；已生成材料保留，可查看材料明细。',
      updated_at: now,
    }
  }

  function recoveredFailedSlottingBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: Number(anchorId),
      status: 'ready',
      stage: 'embedding',
      source_segment_count: 2,
      material_count: 1,
      slot_count: 1,
      vector_count: 1,
      last_error: '',
      updated_at: now,
    }
  }

  function missingSourceStartupFailedImportBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: Number(anchorId),
      status: 'failed_import',
      stage: 'failed_import',
      source_segment_count: 2,
      material_count: 1,
      slot_count: 1,
      vector_count: 0,
      last_error: '启动恢复时来源不可读；保留旧材料，路径已脱敏。',
      updated_at: now,
    }
  }

  function recoveredMissingSourceStartupBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: Number(anchorId),
      status: 'ready',
      stage: 'embedding',
      source_segment_count: 2,
      material_count: 1,
      slot_count: 1,
      vector_count: 1,
      last_error: '',
      updated_at: now,
    }
  }

  function recoveredSlotsDetectedStartupBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: Number(anchorId),
      status: 'ready',
      stage: 'embedding',
      source_segment_count: 2,
      material_count: 1,
      slot_count: 1,
      vector_count: 1,
      last_error: '',
      updated_at: now,
    }
  }

  function ensureRecoveredExtractionMaterial(anchorId) {
    if (state.referenceMaterials.some((material) => material.material_id === 'mock-mat-extract-001')) {
      return
    }

    state.referenceMaterials.push({
      material_id: 'mock-mat-extract-001',
      anchor_id: Number(anchorId),
      source_segment_id: 'mock-seg-extract-001',
      material_type: 'sentence',
      function_tag: 'clue',
      emotion_tag: 'restrained',
      scene_tag: 'extraction_recovery',
      pov_tag: 'close',
      technique_tag: 'delayed_reaction',
      function_confidence: 0.92,
      emotion_confidence: 0.87,
      pov_confidence: 0.9,
      text: '抽取恢复后的材料只保留线索和动作摘要，方便检查来源片段而不暴露完整来源正文。',
      source_hash: 'hash-mock-material-extract-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.88,
        function: 0.9,
        prose_duty: 0.82,
      },
    })
  }

  function ensureFailedSlottingMaterial(anchorId) {
    if (state.referenceMaterials.some((material) => material.material_id === 'mock-mat-slot-001')) {
      return
    }

    state.referenceMaterials.push({
      material_id: 'mock-mat-slot-001',
      anchor_id: Number(anchorId),
      source_segment_id: 'mock-seg-slot-001',
      material_type: 'sentence',
      function_tag: 'clue',
      emotion_tag: 'restrained',
      scene_tag: 'slot_failure',
      pov_tag: 'close',
      technique_tag: 'subtext',
      function_confidence: 0.94,
      emotion_confidence: 0.89,
      pov_confidence: 0.9,
      text: '槽位检测失败前已经生成的材料仍可检查，只显示线索摘要和来源片段预览，不暴露完整来源正文。',
      source_hash: 'hash-mock-material-slot-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.89,
        function: 0.91,
        prose_duty: 0.83,
      },
    })
  }

  function ensureMissingSourceStartupMaterial(anchorId) {
    if (state.referenceMaterials.some((material) => material.material_id === 'mock-mat-missing-startup-001')) {
      return
    }

    state.referenceMaterials.push({
      material_id: 'mock-mat-missing-startup-001',
      anchor_id: Number(anchorId),
      source_segment_id: 'mock-seg-missing-startup-001',
      material_type: 'sentence',
      function_tag: 'continuity',
      emotion_tag: 'unease',
      scene_tag: 'missing_source_restart',
      pov_tag: 'close',
      technique_tag: 'afterbeat',
      function_confidence: 0.93,
      emotion_confidence: 0.88,
      pov_confidence: 0.91,
      text: '源文件丢失后的启动恢复保留旧材料摘要，可搜索、可检查，但诊断不暴露本地路径或完整来源正文。',
      source_hash: 'hash-mock-material-missing-startup-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.9,
        function: 0.89,
        prose_duty: 0.84,
      },
    })
  }

  function ensureSlotsDetectedStartupMaterial(anchorId) {
    if (state.referenceMaterials.some((material) => material.material_id === 'mock-mat-slots-startup-001')) {
      return
    }

    state.referenceMaterials.push({
      material_id: 'mock-mat-slots-startup-001',
      anchor_id: Number(anchorId),
      source_segment_id: 'mock-seg-slots-startup-001',
      material_type: 'sentence',
      function_tag: 'continuity',
      emotion_tag: 'restrained',
      scene_tag: 'slots_startup_recovery',
      pov_tag: 'close',
      technique_tag: 'slot_continuity',
      function_confidence: 0.94,
      emotion_confidence: 0.9,
      pov_confidence: 0.91,
      text: '槽位检测后重启恢复的材料摘要保留{{object}}槽位和向量索引状态，可定位但不暴露完整来源正文。',
      source_hash: 'hash-mock-material-slots-startup-001',
      extractor_version: 'mock-reference-v1',
      user_verified: true,
      created_at: now,
      score_components: {
        lexical: 0.91,
        function: 0.9,
        prose_duty: 0.85,
      },
    })
  }

  function recoveredFailedImportBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: anchorId,
      status: 'ready',
      stage: 'embedding',
      source_segment_count: 3,
      material_count: 2,
      slot_count: 1,
      vector_count: 2,
      last_error: '',
      updated_at: now,
    }
  }

  function startupRecoveredBuildStatus(anchorId) {
    return {
      novel_id: 42,
      anchor_id: anchorId,
      status: 'ready',
      stage: 'embedding',
      source_segment_count: 2,
      material_count: 1,
      slot_count: 1,
      vector_count: 1,
      last_error: '',
      updated_at: now,
    }
  }

  function searchReferenceMaterials(input = {}) {
    if (options.referenceStress) {
      return searchStressReferenceMaterials(input)
    }

    const page = Math.max(1, Number(input.page ?? 1))
    const size = Math.max(1, Number(input.size ?? 10))
    const anchorIds = Array.isArray(input.anchor_ids)
      ? input.anchor_ids.map((value) => Number(value)).filter((value) => Number.isInteger(value) && value > 0)
      : []
    const queryTerms = String(input.query ?? '')
      .trim()
      .toLowerCase()
      .split(/\s+/)
      .filter((term) => term && !/^第\d+章$/.test(term))
    const source = Array.isArray(state.referenceMaterials) ? state.referenceMaterials : []
    const filtered = source.filter((material) => {
      if (anchorIds.length > 0 && !anchorIds.includes(Number(material.anchor_id))) return false
      if (queryTerms.length === 0) return true
      const searchable = [
        material.text,
        material.material_id,
        material.function_tag,
        material.emotion_tag,
        material.scene_tag,
        material.pov_tag,
        material.technique_tag,
      ].map((value) => String(value ?? '').toLowerCase()).join('\n')
      return queryTerms.some((term) => searchable.includes(term))
    })
    const startIndex = (page - 1) * size
    return pagedResult(
      filtered.slice(startIndex, startIndex + size).map(toReferenceMaterialSummary),
      page,
      size,
      filtered.length,
    )
  }

  function toReferenceMaterialSummary(material) {
    const preview = boundedPreview(material.text, 160)
    return {
      material_id: material.material_id,
      anchor_id: material.anchor_id,
      source_segment_id: material.source_segment_id,
      material_type: material.material_type,
      function_tag: material.function_tag,
      emotion_tag: material.emotion_tag,
      scene_tag: material.scene_tag,
      pov_tag: material.pov_tag,
      technique_tag: material.technique_tag,
      function_confidence: material.function_confidence,
      emotion_confidence: material.emotion_confidence,
      pov_confidence: material.pov_confidence,
      text_preview: preview.text,
      text_truncated: preview.truncated,
      source_hash: material.source_hash,
      extractor_version: material.extractor_version,
      user_verified: material.user_verified,
      created_at: material.created_at,
      archive_state: material.archived_at ? 'archived' : 'active',
      archived_at: material.archived_at ?? null,
      score_components: material.score_components ?? null,
    }
  }

  function getReferenceMaterialTagReviewQueue(input = {}) {
    if (options.referenceStress) {
      return getStressReferenceMaterialTagReviewQueue(input)
    }

    const page = Math.max(1, Number(input.page ?? 1))
    const size = Math.max(1, Number(input.size ?? 10))
    const anchorIds = Array.isArray(input.anchor_ids)
      ? input.anchor_ids.map((value) => Number(value)).filter((value) => Number.isInteger(value) && value > 0)
      : []
    const archiveFilter = String(input.archive_filter ?? 'active')
    const source = Array.isArray(state.referenceMaterials) ? state.referenceMaterials : []
    const queued = source
      .filter((material) => {
        if (anchorIds.length > 0 && !anchorIds.includes(Number(material.anchor_id))) return false
        if (archiveFilter === 'active' && material.archived_at) return false
        if (archiveFilter === 'archived' && !material.archived_at) return false
        return true
      })
      .map((material) => toReferenceMaterialTagReviewItem(material))
      .filter((item) => item.issues.length > 0)
    const startIndex = (page - 1) * size
    return pagedResult(queued.slice(startIndex, startIndex + size), page, size, queued.length)
  }

  function toReferenceMaterialTagReviewItem(material) {
    return {
      material: toReferenceMaterialSummary(material),
      issues: referenceMaterialTagReviewIssues(material),
    }
  }

  function referenceMaterialTagReviewIssues(material) {
    const issues = []
    if (material.user_verified !== true) {
      issues.push({ code: 'unverified', label: '未校正', severity: 'review' })
    }

    const lowConfidence = []
    addLowConfidenceIssueLabel(lowConfidence, '功能', material.function_confidence)
    addLowConfidenceIssueLabel(lowConfidence, '情绪', material.emotion_confidence)
    addLowConfidenceIssueLabel(lowConfidence, 'POV', material.pov_confidence)
    if (lowConfidence.length > 0) {
      issues.push({
        code: 'low_confidence',
        label: `低置信 ${lowConfidence.join(' / ')}`,
        severity: 'warning',
      })
    }

    const unknownTags = []
    addUnknownTagIssueLabel(unknownTags, '功能', material.function_tag)
    addUnknownTagIssueLabel(unknownTags, '情绪', material.emotion_tag)
    addUnknownTagIssueLabel(unknownTags, '场景', material.scene_tag)
    addUnknownTagIssueLabel(unknownTags, 'POV', material.pov_tag)
    addUnknownTagIssueLabel(unknownTags, '技法', material.technique_tag)
    if (unknownTags.length > 0) {
      issues.push({
        code: 'unknown_tag',
        label: `unknown 标签 ${unknownTags.join(' / ')}`,
        severity: 'review',
      })
    }

    return issues
  }

  function addLowConfidenceIssueLabel(labels, label, confidence) {
    const value = Number(confidence)
    if (Number.isFinite(value) && value < 0.75) {
      labels.push(`${label} ${value.toFixed(2)}`)
    }
  }

  function addUnknownTagIssueLabel(labels, label, tag) {
    if (isUnknownReferenceTag(tag)) {
      labels.push(label)
    }
  }

  function isUnknownReferenceTag(tag) {
    const value = String(tag ?? '').trim().toLowerCase()
    return value.length === 0 || ['unknown', 'untagged', 'none', 'null', 'undefined'].includes(value)
  }

  function updateReferenceMaterialTags(input = {}) {
    const materialId = String(input?.material_id ?? '')
    let updatedMaterial = null
    state.referenceMaterials = state.referenceMaterials.map((material) => {
      if (material.material_id !== materialId) return material
      updatedMaterial = {
        ...material,
        function_tag: input.function_tag ?? material.function_tag,
        emotion_tag: input.emotion_tag ?? material.emotion_tag,
        scene_tag: input.scene_tag ?? material.scene_tag,
        pov_tag: input.pov_tag ?? material.pov_tag,
        technique_tag: input.technique_tag ?? material.technique_tag,
        function_confidence: input.function_tag == null ? material.function_confidence : 1,
        emotion_confidence: input.emotion_tag == null ? material.emotion_confidence : 1,
        pov_confidence: input.pov_tag == null ? material.pov_confidence : 1,
        user_verified: true,
      }
      return updatedMaterial
    })

    return updatedMaterial ? toReferenceMaterialSummary(updatedMaterial) : null
  }

  function updateReferenceMaterialsTags(input = {}) {
    const materialIds = new Set(Array.isArray(input.material_ids) ? input.material_ids.map(String) : [])
    const updated = []
    state.referenceMaterials = state.referenceMaterials.map((material) => {
      if (!materialIds.has(material.material_id)) return material
      const updatedMaterial = {
        ...material,
        function_tag: input.function_tag ?? material.function_tag,
        emotion_tag: input.emotion_tag ?? material.emotion_tag,
        scene_tag: input.scene_tag ?? material.scene_tag,
        pov_tag: input.pov_tag ?? material.pov_tag,
        technique_tag: input.technique_tag ?? material.technique_tag,
        function_confidence: input.function_tag == null ? material.function_confidence : 1,
        emotion_confidence: input.emotion_tag == null ? material.emotion_confidence : 1,
        pov_confidence: input.pov_tag == null ? material.pov_confidence : 1,
        user_verified: true,
      }
      updated.push(toReferenceMaterialSummary(updatedMaterial))
      return updatedMaterial
    })

    return updated
  }

  function getReferenceMaterialDetail(input = {}) {
    const materialId = String(input?.material_id ?? '')
    const material = state.referenceMaterials.find((item) => item.material_id === materialId)
    if (!material) return null

    const anchor = referenceAnchors().find((item) => Number(item.anchor_id) === Number(material.anchor_id))
    if (!anchor) return null

    const preview = boundedPreview(material.text, 32)
    return {
      material: {
        material_id: material.material_id,
        anchor_id: material.anchor_id,
        source_segment_id: material.source_segment_id,
        material_type: material.material_type,
        function_tag: material.function_tag,
        emotion_tag: material.emotion_tag,
        scene_tag: material.scene_tag,
        pov_tag: material.pov_tag,
        technique_tag: material.technique_tag,
        function_confidence: material.function_confidence,
        emotion_confidence: material.emotion_confidence,
        pov_confidence: material.pov_confidence,
        text_preview: preview.text,
        text_truncated: preview.truncated,
        source_hash: material.source_hash,
        extractor_version: material.extractor_version,
        user_verified: material.user_verified,
        created_at: material.created_at,
        archive_state: material.archived_at ? 'archived' : 'active',
        archived_at: material.archived_at ?? null,
        score_components: material.score_components ?? null,
      },
      source: {
        anchor_id: anchor.anchor_id,
        novel_id: anchor.novel_id ?? 0,
        title: anchor.title,
        author: anchor.author,
        source_kind: anchor.source_kind,
        license_status: anchor.license_status,
        source_file_hash: anchor.source_file_hash,
        build_version: anchor.build_version,
        status: anchor.status,
        visibility: anchor.visibility,
        source_trust: anchor.source_trust,
        user_tags: anchor.user_tags,
        owner_scope: anchor.owner_scope ?? (Number(anchor.novel_id ?? 0) === 0 ? 'workspace_corpus' : 'novel'),
        owner_novel_id: anchor.owner_novel_id ?? (Number(anchor.novel_id ?? 0) === 0 ? null : Number(anchor.novel_id)),
      },
      segments: [{
        segment_id: material.source_segment_id,
        segment_type: material.material_type === 'passage' ? 'paragraph' : 'sentence',
        chapter_index: 1,
        chapter_title: '雨夜参考',
        segment_index: 1,
        text_preview: preview.text,
        text_truncated: preview.truncated,
        text_hash: `hash-segment-${material.material_id}`,
      }],
      slots: [{
        slot_name: 'object',
        placeholder: '杯底水痕',
        start_offset: 12,
        end_offset: 16,
      }],
      processing_notes: [{
        stage: Number(anchor.anchor_id) === 106 && anchor.status === 'failed_slotting'
          ? 'detecting_slots'
          : Number(anchor.anchor_id) === 107 && anchor.status === 'failed_import'
            ? 'failed_import'
            : 'completed',
        status: Number(anchor.anchor_id) === 106 && anchor.status === 'failed_slotting'
          ? 'failed_slotting'
          : Number(anchor.anchor_id) === 107 && anchor.status === 'failed_import'
            ? 'failed_import'
            : 'ready',
        message: Number(anchor.anchor_id) === 106 && anchor.status === 'failed_slotting'
          ? '槽位检测失败；材料输出已保留。'
          : Number(anchor.anchor_id) === 107 && anchor.status === 'failed_import'
            ? '启动恢复缺源；旧材料输出已保留。'
            : 'segments=3; materials=2; slots=2; vectors=0',
        updated_at: now,
        source_segment_count: Number(anchor.anchor_id) === 106 || Number(anchor.anchor_id) === 107 ? 2 : 3,
        material_count: Number(anchor.anchor_id) === 106 || Number(anchor.anchor_id) === 107 ? 1 : 2,
        slot_count: Number(anchor.anchor_id) === 106 && anchor.status === 'failed_slotting' ? 0 : Number(anchor.anchor_id) === 107 ? 1 : 2,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: material.material_id,
        affected_segment_id: material.source_segment_id,
        affected_slot_id: 'object',
      }],
    }
  }

  function getReferenceSourceSegmentDetail(input = {}) {
    const anchorId = Number(input?.anchor_id ?? 0)
    const segmentId = String(input?.segment_id ?? '')
    const anchor = referenceAnchors().find((item) => Number(item.anchor_id) === anchorId)
    if (!anchor || !segmentId) return null

    const material = state.referenceMaterials.find((item) =>
      Number(item.anchor_id) === anchorId && String(item.source_segment_id) === segmentId)
    const fallbackSourceText = segmentId === 'mock-seg-extract-001'
      ? '抽取失败前的来源片段保留了雨夜门槛、杯底水痕、迟疑动作和时间顺序，但这里只允许显示短预览，完整来源正文必须留在服务端。'
      : '雨声压低了整条街的呼吸。他在门口停了很久，手指贴着伞柄。'
    const sourceText = material?.text ?? fallbackSourceText
    const preview = boundedPreview(sourceText, 32)
    const isMissingSourceStartup = Number(anchor.anchor_id) === 107
    const isMissingSourceStartupFailed = isMissingSourceStartup && anchor.status === 'failed_import'
    return {
      source: {
        anchor_id: anchor.anchor_id,
        novel_id: anchor.novel_id ?? 0,
        title: anchor.title,
        author: anchor.author,
        source_kind: anchor.source_kind,
        license_status: anchor.license_status,
        source_file_hash: anchor.source_file_hash,
        build_version: anchor.build_version,
        status: anchor.status,
        visibility: anchor.visibility,
        source_trust: anchor.source_trust,
        user_tags: anchor.user_tags,
        owner_scope: anchor.owner_scope ?? (Number(anchor.novel_id ?? 0) === 0 ? 'workspace_corpus' : 'novel'),
        owner_novel_id: anchor.owner_novel_id ?? (Number(anchor.novel_id ?? 0) === 0 ? null : Number(anchor.novel_id)),
      },
      segment: {
        anchor_id: anchor.anchor_id,
        segment_id: segmentId,
        segment_type: material?.material_type === 'sentence' ? 'sentence' : 'paragraph',
        chapter_index: 1,
        chapter_title: '雨夜参考',
        segment_index: 1,
        parent_segment_id: 'mock-chapter-rain-001',
        start_offset: 0,
        end_offset: sourceText.length,
        text_preview: preview.text,
        text_truncated: preview.truncated,
        text_hash: `hash-segment-${segmentId}`,
      },
      processing_notes: [{
        stage: isMissingSourceStartupFailed
          ? 'failed_import'
          : isMissingSourceStartup ? 'embedding' : 'extracting_materials',
        status: isMissingSourceStartupFailed
          ? 'failed_import'
          : material ? 'ready' : 'failed_extraction',
        message: isMissingSourceStartupFailed
          ? '启动恢复缺源；旧来源片段与材料输出已保留。'
          : isMissingSourceStartup
            ? 'segments=2; materials=1; slots=1; vectors=1'
            : material ? 'segments=3; materials=2; slots=1; vectors=2' : 'extractor stopped before material rows were produced',
        updated_at: now,
        source_segment_count: isMissingSourceStartup ? 2 : 3,
        material_count: isMissingSourceStartup ? 1 : material ? 2 : 0,
        slot_count: isMissingSourceStartup ? 1 : material ? 1 : 0,
        vector_count: isMissingSourceStartupFailed ? 0 : isMissingSourceStartup ? 1 : material ? 2 : 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: material?.material_id ?? '',
        affected_segment_id: segmentId,
        affected_slot_id: material ? 'object' : '',
      }],
    }
  }

  function getReferenceSourceProcessingDetail(input = {}) {
    const anchorId = Number(input?.anchor_id ?? 0)
    const anchor = referenceAnchors().find((item) => Number(item.anchor_id) === anchorId)
    if (!anchor) return null

    if (anchorId === 103 && state.referenceBuildStatuses[String(anchorId)]) {
      return failedImportRetryFailureProcessingDetail(anchor)
    }

    if (anchorId === 102) {
      return anchor.status === 'ready'
        ? recoveredFailedImportProcessingDetail(anchor)
        : failedImportProcessingDetail(anchor)
    }

    if (anchorId === 104) {
      return startupRecoveredProcessingDetail(anchor)
    }

    if (anchorId === 105) {
      return anchor.status === 'ready'
        ? recoveredFailedExtractionProcessingDetail(anchor)
        : failedExtractionProcessingDetail(anchor)
    }

    if (anchorId === 106) {
      return anchor.status === 'ready'
        ? recoveredFailedSlottingProcessingDetail(anchor)
        : failedSlottingProcessingDetail(anchor)
    }

    if (anchorId === 107) {
      return anchor.status === 'ready'
        ? recoveredMissingSourceStartupProcessingDetail(anchor)
        : missingSourceStartupFailedImportProcessingDetail(anchor)
    }

    if (anchorId === 108) {
      return recoveredSlotsDetectedStartupProcessingDetail(anchor)
    }

    if (anchor.status === 'failed_import') {
      return failedImportProcessingDetail(anchor)
    }

    return {
      source: {
        anchor_id: anchor.anchor_id,
        novel_id: anchor.novel_id ?? 0,
        title: anchor.title,
        author: anchor.author,
        source_kind: anchor.source_kind,
        license_status: anchor.license_status,
        source_file_hash: anchor.source_file_hash,
        build_version: anchor.build_version,
        status: anchor.status,
        visibility: anchor.visibility,
        source_trust: anchor.source_trust,
        user_tags: anchor.user_tags,
        owner_scope: anchor.owner_scope ?? (Number(anchor.novel_id ?? 0) === 0 ? 'workspace_corpus' : 'novel'),
        owner_novel_id: anchor.owner_novel_id ?? (Number(anchor.novel_id ?? 0) === 0 ? null : Number(anchor.novel_id)),
      },
      current_status: {
        stage: 'embedding',
        status: 'ready',
        diagnostic: 'segments=3; materials=2; slots=1; vectors=2',
        updated_at: now,
        source_segment_count: 3,
        material_count: 2,
        slot_count: 1,
        vector_count: 2,
      },
      events: [{
        event_id: 'event-1',
        stage: 'embedding',
        status: 'ready',
        message: 'segments=3; materials=2; slots=1; vectors=2',
        created_at: now,
        source_segment_count: 3,
        material_count: 2,
        slot_count: 1,
        vector_count: 2,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-rain-001',
        affected_segment_id: 'mock-seg-rain-001',
        affected_slot_id: 'object',
      }, {
        event_id: 'event-failed-extraction',
        stage: 'extracting_materials',
        status: 'failed_extraction',
        message: 'extractor stopped before material rows were produced',
        created_at: now,
        source_segment_count: 3,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: '',
        affected_segment_id: 'mock-seg-rain-001',
        affected_slot_id: '',
      }],
      retry_available: false,
      rebuild_available: true,
      attempt_count: 1,
      current_attempt: {
        attempt_id: `anchor:${anchor.anchor_id}:attempt:1`,
        attempt_number: 1,
        build_id: `anchor:${anchor.anchor_id}:build:1`,
        build_version: anchor.build_version,
        stage: 'embedding',
        status: 'ready',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 3,
        material_count: 2,
        slot_count: 1,
        vector_count: 2,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: '',
      },
      prior_attempts: [],
      recovered_from_attempt_id: '',
      recovered_from_build_id: '',
      blocked_reason: '',
    }
  }

  function failedImportProcessingDetail(anchor) {
    const diagnostic = '无法读取来源；本地路径已脱敏。'
    const blockedReason = 'source_unavailable_redacted'

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'failed_import',
        status: 'failed_import',
        diagnostic,
        updated_at: now,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
      },
      events: [{
        event_id: 'event-failed-import',
        stage: 'failed_import',
        status: 'failed_import',
        message: diagnostic,
        created_at: now,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: '',
        affected_segment_id: '',
        affected_slot_id: '',
      }],
      retry_available: true,
      rebuild_available: true,
      attempt_count: 1,
      current_attempt: {
        attempt_id: `anchor:${anchor.anchor_id}:attempt:1`,
        attempt_number: 1,
        build_id: `anchor:${anchor.anchor_id}:build:1`,
        build_version: anchor.build_version,
        stage: 'failed_import',
        status: 'failed_import',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: blockedReason,
      },
      prior_attempts: [],
      recovered_from_attempt_id: '',
      recovered_from_build_id: '',
      blocked_reason: blockedReason,
    }
  }

  function failedImportRetryFailureProcessingDetail(anchor) {
    const failedAttemptId = `anchor:${anchor.anchor_id}:attempt:1`
    const failedBuildId = `anchor:${anchor.anchor_id}:build:1`
    const retryAttemptId = `anchor:${anchor.anchor_id}:attempt:2`
    const retryBuildId = `anchor:${anchor.anchor_id}:build:2`
    const diagnostic = '重试仍失败；本地路径已脱敏。'
    const blockedReason = 'source_unavailable_after_retry_redacted'

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'failed_import',
        status: 'failed_import',
        diagnostic,
        updated_at: now,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
      },
      events: [{
        event_id: 'event-failed-import',
        stage: 'failed_import',
        status: 'failed_import',
        message: '无法读取来源；本地路径已脱敏。',
        created_at: now,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: '',
        affected_segment_id: '',
        affected_slot_id: '',
      }, {
        event_id: 'event-retry-failed-import',
        stage: 'failed_import',
        status: 'failed_import',
        message: diagnostic,
        created_at: now,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: '',
        affected_segment_id: '',
        affected_slot_id: '',
      }],
      retry_available: true,
      rebuild_available: true,
      attempt_count: 2,
      current_attempt: {
        attempt_id: retryAttemptId,
        attempt_number: 2,
        build_id: retryBuildId,
        build_version: anchor.build_version,
        stage: 'failed_import',
        status: 'failed_import',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: blockedReason,
      },
      prior_attempts: [{
        attempt_id: failedAttemptId,
        attempt_number: 1,
        build_id: failedBuildId,
        build_version: anchor.build_version,
        stage: 'failed_import',
        status: 'failed_import',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'source_unavailable_redacted',
      }],
      recovered_from_attempt_id: '',
      recovered_from_build_id: '',
      blocked_reason: blockedReason,
    }
  }

  function recoveredFailedImportProcessingDetail(anchor) {
    const failedAttemptId = `anchor:${anchor.anchor_id}:attempt:1`
    const failedBuildId = `anchor:${anchor.anchor_id}:build:1`

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'embedding',
        status: 'ready',
        diagnostic: 'segments=3; materials=2; slots=1; vectors=2; recovered from failed_import',
        updated_at: now,
        source_segment_count: 3,
        material_count: 2,
        slot_count: 1,
        vector_count: 2,
      },
      events: [{
        event_id: 'event-failed-import',
        stage: 'failed_import',
        status: 'failed_import',
        message: '无法读取来源；本地路径已脱敏。',
        created_at: now,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: '',
        affected_segment_id: '',
        affected_slot_id: '',
      }, {
        event_id: 'event-recovered-import',
        stage: 'embedding',
        status: 'ready',
        message: 'segments=3; materials=2; slots=1; vectors=2',
        created_at: now,
        source_segment_count: 3,
        material_count: 2,
        slot_count: 1,
        vector_count: 2,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-rain-001',
        affected_segment_id: 'mock-seg-rain-001',
        affected_slot_id: 'object',
      }],
      retry_available: false,
      rebuild_available: true,
      attempt_count: 2,
      current_attempt: {
        attempt_id: `anchor:${anchor.anchor_id}:attempt:2`,
        attempt_number: 2,
        build_id: `anchor:${anchor.anchor_id}:build:2`,
        build_version: anchor.build_version,
        stage: 'embedding',
        status: 'ready',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 3,
        material_count: 2,
        slot_count: 1,
        vector_count: 2,
        recovered_from_attempt_id: failedAttemptId,
        recovered_from_build_id: failedBuildId,
        blocked_reason: '',
      },
      prior_attempts: [{
        attempt_id: failedAttemptId,
        attempt_number: 1,
        build_id: failedBuildId,
        build_version: anchor.build_version,
        stage: 'failed_import',
        status: 'failed_import',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 0,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'source_unavailable_redacted',
      }],
      recovered_from_attempt_id: failedAttemptId,
      recovered_from_build_id: failedBuildId,
      blocked_reason: '',
    }
  }

  function startupRecoveredProcessingDetail(anchor) {
    const interruptedAttemptId = `anchor:${anchor.anchor_id}:attempt:1`
    const interruptedBuildId = `anchor:${anchor.anchor_id}:build:1`
    const recoveredAttemptId = `anchor:${anchor.anchor_id}:attempt:2`
    const recoveredBuildId = `anchor:${anchor.anchor_id}:build:2`

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'embedding',
        status: 'ready',
        diagnostic: 'segments=2; materials=1; slots=1; vectors=1; recovered from interrupted embedding after app restart',
        updated_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
      },
      events: [{
        event_id: 'event-interrupted-embedding',
        stage: 'embedding',
        status: 'interrupted',
        message: 'app restart interrupted vector indexing; durable material output retained',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-restart-001',
        affected_segment_id: 'mock-seg-restart-001',
        affected_slot_id: 'object',
      }, {
        event_id: 'event-startup-recovered-embedding',
        stage: 'embedding',
        status: 'ready',
        message: 'startup recovery completed indexing without duplicate searchable materials',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-restart-001',
        affected_segment_id: 'mock-seg-restart-001',
        affected_slot_id: 'object',
      }],
      retry_available: false,
      rebuild_available: true,
      attempt_count: 2,
      current_attempt: {
        attempt_id: recoveredAttemptId,
        attempt_number: 2,
        build_id: recoveredBuildId,
        build_version: anchor.build_version,
        stage: 'embedding',
        status: 'ready',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        recovered_from_attempt_id: interruptedAttemptId,
        recovered_from_build_id: interruptedBuildId,
        blocked_reason: '',
      },
      prior_attempts: [{
        attempt_id: interruptedAttemptId,
        attempt_number: 1,
        build_id: interruptedBuildId,
        build_version: anchor.build_version,
        stage: 'embedding',
        status: 'interrupted',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'app_restart_during_embedding',
      }],
      recovered_from_attempt_id: interruptedAttemptId,
      recovered_from_build_id: interruptedBuildId,
      blocked_reason: '',
    }
  }

  function failedExtractionProcessingDetail(anchor) {
    const diagnostic = '材料抽取未产生可用输出；来源片段可检查，正文已脱敏。'
    const blockedReason = 'extractor_output_empty_redacted'

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'extracting_materials',
        status: 'failed_extraction',
        diagnostic,
        updated_at: now,
        source_segment_count: 2,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
      },
      events: [{
        event_id: 'event-failed-extraction-current',
        stage: 'extracting_materials',
        status: 'failed_extraction',
        message: diagnostic,
        created_at: now,
        source_segment_count: 2,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: '',
        affected_segment_id: 'mock-seg-extract-001',
        affected_slot_id: '',
      }],
      retry_available: true,
      rebuild_available: true,
      attempt_count: 1,
      current_attempt: {
        attempt_id: `anchor:${anchor.anchor_id}:attempt:1`,
        attempt_number: 1,
        build_id: `anchor:${anchor.anchor_id}:build:1`,
        build_version: anchor.build_version,
        stage: 'extracting_materials',
        status: 'failed_extraction',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: blockedReason,
      },
      prior_attempts: [],
      recovered_from_attempt_id: '',
      recovered_from_build_id: '',
      blocked_reason: blockedReason,
    }
  }

  function recoveredFailedExtractionProcessingDetail(anchor) {
    const failedAttemptId = `anchor:${anchor.anchor_id}:attempt:1`
    const failedBuildId = `anchor:${anchor.anchor_id}:build:1`

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'embedding',
        status: 'ready',
        diagnostic: 'segments=2; materials=1; slots=1; vectors=1; recovered from failed_extraction',
        updated_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
      },
      events: [{
        event_id: 'event-failed-extraction-current',
        stage: 'extracting_materials',
        status: 'failed_extraction',
        message: '材料抽取未产生可用输出；来源片段可检查，正文已脱敏。',
        created_at: now,
        source_segment_count: 2,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: '',
        affected_segment_id: 'mock-seg-extract-001',
        affected_slot_id: '',
      }, {
        event_id: 'event-recovered-extraction',
        stage: 'embedding',
        status: 'ready',
        message: 'segments=2; materials=1; slots=1; vectors=1',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-extract-001',
        affected_segment_id: 'mock-seg-extract-001',
        affected_slot_id: 'object',
      }],
      retry_available: false,
      rebuild_available: true,
      attempt_count: 2,
      current_attempt: {
        attempt_id: `anchor:${anchor.anchor_id}:attempt:2`,
        attempt_number: 2,
        build_id: `anchor:${anchor.anchor_id}:build:2`,
        build_version: anchor.build_version,
        stage: 'embedding',
        status: 'ready',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        recovered_from_attempt_id: failedAttemptId,
        recovered_from_build_id: failedBuildId,
        blocked_reason: '',
      },
      prior_attempts: [{
        attempt_id: failedAttemptId,
        attempt_number: 1,
        build_id: failedBuildId,
        build_version: anchor.build_version,
        stage: 'extracting_materials',
        status: 'failed_extraction',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 0,
        slot_count: 0,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'extractor_output_empty_redacted',
      }],
      recovered_from_attempt_id: failedAttemptId,
      recovered_from_build_id: failedBuildId,
      blocked_reason: '',
    }
  }

  function failedSlottingProcessingDetail(anchor) {
    const diagnostic = '槽位检测失败；已生成材料保留，可查看材料明细。'
    const blockedReason = 'slot_detection_failed_redacted'

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'detecting_slots',
        status: 'failed_slotting',
        diagnostic,
        updated_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 0,
        vector_count: 0,
      },
      events: [{
        event_id: 'event-failed-slotting-current',
        stage: 'detecting_slots',
        status: 'failed_slotting',
        message: diagnostic,
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-slot-001',
        affected_segment_id: 'mock-seg-slot-001',
        affected_slot_id: '',
      }],
      retry_available: true,
      rebuild_available: true,
      attempt_count: 1,
      current_attempt: {
        attempt_id: `anchor:${anchor.anchor_id}:attempt:1`,
        attempt_number: 1,
        build_id: `anchor:${anchor.anchor_id}:build:1`,
        build_version: anchor.build_version,
        stage: 'detecting_slots',
        status: 'failed_slotting',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 0,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: blockedReason,
      },
      prior_attempts: [],
      recovered_from_attempt_id: '',
      recovered_from_build_id: '',
      blocked_reason: blockedReason,
    }
  }

  function recoveredFailedSlottingProcessingDetail(anchor) {
    const failedAttemptId = `anchor:${anchor.anchor_id}:attempt:1`
    const failedBuildId = `anchor:${anchor.anchor_id}:build:1`

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'embedding',
        status: 'ready',
        diagnostic: 'segments=2; materials=1; slots=1; vectors=1; recovered from failed_slotting',
        updated_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
      },
      events: [{
        event_id: 'event-failed-slotting-current',
        stage: 'detecting_slots',
        status: 'failed_slotting',
        message: '槽位检测失败；已生成材料保留，可查看材料明细。',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 0,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-slot-001',
        affected_segment_id: 'mock-seg-slot-001',
        affected_slot_id: '',
      }, {
        event_id: 'event-recovered-slotting',
        stage: 'embedding',
        status: 'ready',
        message: 'segments=2; materials=1; slots=1; vectors=1',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-slot-001',
        affected_segment_id: 'mock-seg-slot-001',
        affected_slot_id: 'object',
      }],
      retry_available: false,
      rebuild_available: true,
      attempt_count: 2,
      current_attempt: {
        attempt_id: `anchor:${anchor.anchor_id}:attempt:2`,
        attempt_number: 2,
        build_id: `anchor:${anchor.anchor_id}:build:2`,
        build_version: anchor.build_version,
        stage: 'embedding',
        status: 'ready',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        recovered_from_attempt_id: failedAttemptId,
        recovered_from_build_id: failedBuildId,
        blocked_reason: '',
      },
      prior_attempts: [{
        attempt_id: failedAttemptId,
        attempt_number: 1,
        build_id: failedBuildId,
        build_version: anchor.build_version,
        stage: 'detecting_slots',
        status: 'failed_slotting',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 0,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'slot_detection_failed_redacted',
      }],
      recovered_from_attempt_id: failedAttemptId,
      recovered_from_build_id: failedBuildId,
      blocked_reason: '',
    }
  }

  function missingSourceStartupFailedImportProcessingDetail(anchor) {
    const interruptedAttemptId = `anchor:${anchor.anchor_id}:attempt:1`
    const interruptedBuildId = `anchor:${anchor.anchor_id}:build:1`
    const failedAttemptId = `anchor:${anchor.anchor_id}:attempt:2`
    const failedBuildId = `anchor:${anchor.anchor_id}:build:2`
    const diagnostic = '启动恢复时来源不可读；保留旧材料，路径已脱敏。'
    const blockedReason = 'source_missing_after_app_restart_redacted'

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'failed_import',
        status: 'failed_import',
        diagnostic,
        updated_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
      },
      events: [{
        event_id: 'event-startup-preembedding-interrupted',
        stage: 'materials_extracted',
        status: 'materials_extracted',
        message: 'app restart found durable material output before searchable activation',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-missing-startup-001',
        affected_segment_id: 'mock-seg-missing-startup-001',
        affected_slot_id: 'object',
      }, {
        event_id: 'event-startup-missing-source-failed-import',
        stage: 'failed_import',
        status: 'failed_import',
        message: diagnostic,
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-missing-startup-001',
        affected_segment_id: 'mock-seg-missing-startup-001',
        affected_slot_id: 'object',
      }],
      retry_available: true,
      rebuild_available: true,
      attempt_count: 2,
      current_attempt: {
        attempt_id: failedAttemptId,
        attempt_number: 2,
        build_id: failedBuildId,
        build_version: anchor.build_version,
        stage: 'failed_import',
        status: 'failed_import',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: blockedReason,
      },
      prior_attempts: [{
        attempt_id: interruptedAttemptId,
        attempt_number: 1,
        build_id: interruptedBuildId,
        build_version: anchor.build_version,
        stage: 'materials_extracted',
        status: 'materials_extracted',
        started_at: now,
        updated_at: now,
        completed_at: '',
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'app_restart_before_searchable_activation',
      }],
      recovered_from_attempt_id: '',
      recovered_from_build_id: '',
      blocked_reason: blockedReason,
    }
  }

  function recoveredMissingSourceStartupProcessingDetail(anchor) {
    const interruptedAttemptId = `anchor:${anchor.anchor_id}:attempt:1`
    const interruptedBuildId = `anchor:${anchor.anchor_id}:build:1`
    const failedAttemptId = `anchor:${anchor.anchor_id}:attempt:2`
    const failedBuildId = `anchor:${anchor.anchor_id}:build:2`
    const recoveredAttemptId = `anchor:${anchor.anchor_id}:attempt:3`
    const recoveredBuildId = `anchor:${anchor.anchor_id}:build:3`

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'embedding',
        status: 'ready',
        diagnostic: 'segments=2; materials=1; slots=1; vectors=1; recovered from missing source startup failure',
        updated_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
      },
      events: [{
        event_id: 'event-startup-preembedding-interrupted',
        stage: 'materials_extracted',
        status: 'materials_extracted',
        message: 'app restart found durable material output before searchable activation',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-missing-startup-001',
        affected_segment_id: 'mock-seg-missing-startup-001',
        affected_slot_id: 'object',
      }, {
        event_id: 'event-startup-missing-source-failed-import',
        stage: 'failed_import',
        status: 'failed_import',
        message: '启动恢复时来源不可读；保留旧材料，路径已脱敏。',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-missing-startup-001',
        affected_segment_id: 'mock-seg-missing-startup-001',
        affected_slot_id: 'object',
      }, {
        event_id: 'event-recovered-missing-source-startup',
        stage: 'embedding',
        status: 'ready',
        message: 'segments=2; materials=1; slots=1; vectors=1',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-missing-startup-001',
        affected_segment_id: 'mock-seg-missing-startup-001',
        affected_slot_id: 'object',
      }],
      retry_available: false,
      rebuild_available: true,
      attempt_count: 3,
      current_attempt: {
        attempt_id: recoveredAttemptId,
        attempt_number: 3,
        build_id: recoveredBuildId,
        build_version: anchor.build_version,
        stage: 'embedding',
        status: 'ready',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        recovered_from_attempt_id: failedAttemptId,
        recovered_from_build_id: failedBuildId,
        blocked_reason: '',
      },
      prior_attempts: [{
        attempt_id: failedAttemptId,
        attempt_number: 2,
        build_id: failedBuildId,
        build_version: anchor.build_version,
        stage: 'failed_import',
        status: 'failed_import',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'source_missing_after_app_restart_redacted',
      }, {
        attempt_id: interruptedAttemptId,
        attempt_number: 1,
        build_id: interruptedBuildId,
        build_version: anchor.build_version,
        stage: 'materials_extracted',
        status: 'materials_extracted',
        started_at: now,
        updated_at: now,
        completed_at: '',
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'app_restart_before_searchable_activation',
      }],
      recovered_from_attempt_id: failedAttemptId,
      recovered_from_build_id: failedBuildId,
      blocked_reason: '',
    }
  }

  function recoveredSlotsDetectedStartupProcessingDetail(anchor) {
    const interruptedAttemptId = `anchor:${anchor.anchor_id}:attempt:1`
    const interruptedBuildId = `anchor:${anchor.anchor_id}:build:1`
    const recoveredAttemptId = `anchor:${anchor.anchor_id}:attempt:2`
    const recoveredBuildId = `anchor:${anchor.anchor_id}:build:2`

    return {
      source: referenceProcessingSourceSummary(anchor),
      current_status: {
        stage: 'embedding',
        status: 'ready',
        diagnostic: 'segments=2; materials=1; slots=1; vectors=1; recovered from slots_detected startup recovery',
        updated_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
      },
      events: [{
        event_id: 'event-startup-slots-detected',
        stage: 'slots_detected',
        status: 'slots_detected',
        message: 'app restart found detected slots before vector indexing',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-slots-startup-001',
        affected_segment_id: 'mock-seg-slots-startup-001',
        affected_slot_id: 'object',
      }, {
        event_id: 'event-startup-slots-indexed',
        stage: 'embedding',
        status: 'ready',
        message: 'startup recovery built active vectors without duplicate searchable materials',
        created_at: now,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        affected_source_id: String(anchor.anchor_id),
        affected_material_id: 'mock-mat-slots-startup-001',
        affected_segment_id: 'mock-seg-slots-startup-001',
        affected_slot_id: 'object',
      }],
      retry_available: false,
      rebuild_available: true,
      attempt_count: 2,
      current_attempt: {
        attempt_id: recoveredAttemptId,
        attempt_number: 2,
        build_id: recoveredBuildId,
        build_version: anchor.build_version,
        stage: 'embedding',
        status: 'ready',
        started_at: now,
        updated_at: now,
        completed_at: now,
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 1,
        recovered_from_attempt_id: interruptedAttemptId,
        recovered_from_build_id: interruptedBuildId,
        blocked_reason: '',
      },
      prior_attempts: [{
        attempt_id: interruptedAttemptId,
        attempt_number: 1,
        build_id: interruptedBuildId,
        build_version: anchor.build_version,
        stage: 'slots_detected',
        status: 'slots_detected',
        started_at: now,
        updated_at: now,
        completed_at: '',
        event_count: 1,
        source_segment_count: 2,
        material_count: 1,
        slot_count: 1,
        vector_count: 0,
        recovered_from_attempt_id: '',
        recovered_from_build_id: '',
        blocked_reason: 'app_restart_before_vector_indexing',
      }],
      recovered_from_attempt_id: interruptedAttemptId,
      recovered_from_build_id: interruptedBuildId,
      blocked_reason: '',
    }
  }

  function referenceProcessingSourceSummary(anchor) {
    return {
      anchor_id: anchor.anchor_id,
      novel_id: anchor.novel_id ?? 0,
      title: anchor.title,
      author: anchor.author,
      source_kind: anchor.source_kind,
      license_status: anchor.license_status,
      source_file_hash: anchor.source_file_hash,
      build_version: anchor.build_version,
      status: anchor.status,
      visibility: anchor.visibility,
      source_trust: anchor.source_trust,
      user_tags: anchor.user_tags,
      owner_scope: anchor.owner_scope ?? (Number(anchor.novel_id ?? 0) === 0 ? 'workspace_corpus' : 'novel'),
      owner_novel_id: anchor.owner_novel_id ?? (Number(anchor.novel_id ?? 0) === 0 ? null : Number(anchor.novel_id)),
    }
  }

  function adaptReferenceMaterial(input = {}) {
    const materialId = String(input?.material_id ?? '')
    const material = state.referenceMaterials.find((item) => item.material_id === materialId)
    if (!material) throw new Error(`Unknown reference material ${materialId}`)
    const candidateText = `林岚把雨声和杯底半圈水痕重新放回眼前，只写下门缝里那一下停顿，没有替任何人提前下结论。`
    const facts = Array.isArray(input?.scene_facts) ? input.scene_facts.map((item) => String(item)) : []
    const shouldFailAudit = facts.some((item) => item.includes('mock_failed_audit'))
    return {
      candidate_id: `mock-adapt-${material.material_id}`,
      material_id: material.material_id,
      rewrite_level: String(input?.max_rewrite_level ?? 'L2'),
      text: candidateText,
      changed_slots: Array.isArray(input?.slot_values) ? input.slot_values : [],
      non_slot_edits: [],
      audit: {
        audit_id: `mock-audit-${material.material_id}`,
        status: shouldFailAudit ? 'failed' : 'passed',
        rewrite_level: String(input?.max_rewrite_level ?? 'L2'),
        provenance_errors: shouldFailAudit ? ['mock source-leak risk'] : [],
        unsupported_fact_errors: [],
        ai_prose_risks: [],
        non_slot_edits: [],
        required_fixes: shouldFailAudit ? ['mock_failed_audit requires revision before insertion'] : [],
        audited_at: now,
      },
    }
  }

  function boundedPreview(text, maxLength) {
    const normalized = String(text ?? '').trim().replace(/\s+/g, ' ')
    if (normalized.length <= maxLength) {
      return { text: normalized, truncated: false }
    }

    return { text: `${normalized.slice(0, maxLength).trimEnd()}...`, truncated: true }
  }

  function searchStressReferenceMaterials(input = {}) {
    const page = Math.max(1, Number(input.page ?? 1))
    const size = Math.max(1, Number(input.size ?? 10))
    const total = options.referenceStress.materialTotal
    const anchorIds = Array.isArray(input.anchor_ids) ? input.anchor_ids : []
    const anchorScopedPreview = anchorIds.length === 1 && size === 5
    const startIndex = (page - 1) * size + 1
    const endIndex = Math.min(total, startIndex + size - 1)
    const items = []

    if (startIndex <= total) {
      for (let index = startIndex; index <= endIndex; index += 1) {
        items.push(toReferenceMaterialSummary(stressReferenceMaterial(index)))
      }
    }

    return pagedResult(items, page, size, anchorScopedPreview ? total : total)
  }

  function getStressReferenceMaterialTagReviewQueue(input = {}) {
    const page = Math.max(1, Number(input.page ?? 1))
    const size = Math.max(1, Number(input.size ?? 10))
    const total = options.referenceStress.materialTotal
    const anchorId = options.referenceStress.anchor.anchor_id
    const anchorIds = Array.isArray(input.anchor_ids) ? input.anchor_ids.map(Number).filter(Number.isFinite) : []
    const archiveFilter = String(input.archive_filter ?? 'active')

    if ((anchorIds.length > 0 && !anchorIds.includes(Number(anchorId))) || archiveFilter === 'archived') {
      return pagedResult([], page, size, 0)
    }

    const queuedIndexes = []
    for (let index = 1; index <= total; index += 1) {
      if (referenceMaterialTagReviewIssues(stressReferenceMaterial(index)).length > 0) {
        queuedIndexes.push(index)
      }
    }

    const startIndex = (page - 1) * size
    const items = queuedIndexes
      .slice(startIndex, startIndex + size)
      .map((index) => toReferenceMaterialTagReviewItem(stressReferenceMaterial(index)))
    return pagedResult(items, page, size, queuedIndexes.length)
  }

  function stressReferenceMaterial(index) {
    const padded = String(index).padStart(4, '0')
    const anchorId = options.referenceStress.anchor.anchor_id
    return {
      material_id: `stress-mat-${padded}`,
      anchor_id: anchorId,
      source_segment_id: `stress-seg-${padded}`,
      material_type: index % 5 === 0 ? 'passage' : 'sentence',
      function_tag: index % 3 === 0 ? 'environment' : 'emotion_evidence',
      emotion_tag: 'restrained',
      scene_tag: 'rain_threshold',
      pov_tag: 'close',
      technique_tag: index % 2 === 0 ? 'delayed_reaction' : 'subtext',
      function_confidence: 0.94,
      emotion_confidence: 0.91,
      pov_confidence: 0.9,
      text: `10MB 水痕参考材料 ${padded}：雨声压着旧城门，林岚只记录杯底半圈水痕、灯影和门缝停顿，不提前确认门外身份。`,
      source_hash: `hash-stress-material-${padded}`,
      extractor_version: 'mock-stress-extractor-v1',
      user_verified: index % 7 === 0,
      created_at: now,
      score_components: {
        lexical: index === 1 ? 0.97 : 0.84,
        function: index === 1 ? 0.92 : 0.81,
        prose_duty: index === 1 ? 0.9 : 0.78,
        feedback_boost: 0.1,
      },
    }
  }

  function generateReferenceBlueprint(input = {}) {
    const blueprint = makeReferenceBlueprint(state.nextReferenceBlueprintId++, {
      chapter_number: input.chapter_number,
      title: input.title || '10MB 材料绑定验收',
      known_facts: input.known_facts ?? [],
      forbidden_facts: input.forbidden_facts ?? [],
      primary_anchor_id: input.anchor_ids?.[0] ?? options.referenceStress?.anchor.anchor_id ?? 0,
      status: 'draft',
      latest_review: null,
    })
    state.referenceBlueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint
  }

  function reviewReferenceBlueprint(input = {}) {
    const blueprint = cloneReferenceBlueprint(input.blueprint_id)
    blueprint.status = 'reviewed'
    blueprint.latest_review = makeReferenceReview(blueprint.blueprint_id, `review-${blueprint.blueprint_id}`)
    blueprint.updated_at = now
    state.referenceBlueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint.latest_review
  }

  function approveReferenceBlueprint(input = {}) {
    const blueprint = cloneReferenceBlueprint(input.blueprint_id)
    blueprint.status = 'approved'
    blueprint.updated_at = now
    state.referenceBlueprints[String(blueprint.blueprint_id)] = blueprint
    return blueprint
  }

  function bindReferenceBlueprintMaterials(input = {}) {
    const blueprint = cloneReferenceBlueprint(input.blueprint_id)
    blueprint.status = 'material_bound'
    blueprint.updated_at = now
    state.referenceBlueprints[String(blueprint.blueprint_id)] = blueprint
    return {
      blueprint_id: blueprint.blueprint_id,
      links: [{
        link_id: `stress-link-${blueprint.blueprint_id}-001`,
        blueprint_id: blueprint.blueprint_id,
        beat_id: blueprint.beats[0].beat_id,
        material_id: 'stress-mat-0001',
        intended_use: 'source-backed detail from the 10MB segmented reference source',
        max_rewrite_level: 'L1',
        selected: true,
        score: 0.96,
        score_components: {
          lexical: 0.97,
          function: 0.92,
          prose_duty: 0.9,
        },
        fit_explanation: 'Uses generated material with stable source segment and hash provenance.',
        created_at: now,
      }],
    }
  }

  function startReferenceOrchestrationRun(input = {}) {
    const runId = `mock-orch-${String(state.nextReferenceOrchestrationRunId++).padStart(3, '0')}`
    const chapterNumber = Number(input?.chapter_number ?? 1)
    const run = {
      run_id: runId,
      novel_id: Number(input?.novel_id ?? state.activeNovelId),
      chapter_number: chapterNumber,
      status: 'waiting_for_user',
      stage: 'source_confirmation',
      chapter_goal: String(input?.chapter_goal ?? ''),
      known_facts: Array.isArray(input?.known_facts) ? input.known_facts : [],
      forbidden_facts: Array.isArray(input?.forbidden_facts) ? input.forbidden_facts : [],
      anchor_ids: Array.isArray(input?.anchor_ids) ? input.anchor_ids : [],
      corpus_search_policy: input?.corpus_search_policy ?? {
        mode: 'story_context',
        max_results_per_beat: 3,
        license_statuses: ['user_provided'],
        include_anchor_ids: [],
        exclude_anchor_ids: [],
      },
      style_policy: input?.style_policy ?? null,
      blueprint_id: 0,
      review_id: '',
      candidate_ids: [],
      current_decision: {
        decision_type: 'confirm_source_and_facts',
        stop_reason: 'source_confirmation_required',
        summary: '请确认本章来源和事实边界后继续。',
        required_actions: ['检查推荐素材', '确认禁止事实没有被突破'],
        approval_summary: {
          chapter_function: '用共享语料支撑当前章节。',
          pov: 'close',
          fact_boundary_changes: [],
          emotional_trajectory: 'restrained -> pressure',
          material_use_plan: '按章节上下文检索共享素材，不要求手动选择 anchor。',
          rewrite_budget: 'L0-L2',
          high_risk_findings: [],
        },
        proposed_blueprint_revision: null,
      },
      last_stop_reason: 'source_confirmation_required',
      error_message: '',
      created_at: now,
      updated_at: now,
    }
    state.referenceOrchestrationRuns = [run, ...state.referenceOrchestrationRuns]
    return run
  }

  function resumeReferenceOrchestrationRun(input = {}) {
    const run = referenceOrchestrationRun(input?.run_id)
    if (!run) throw new Error(`Unknown reference orchestration run ${input?.run_id}`)
    const decisionType = String(input?.decision_type ?? '')
    if (run.current_decision?.decision_type !== decisionType) {
      throw new Error(`Decision type does not match pending decision ${run.current_decision?.decision_type ?? ''}`)
    }

    let updated
    if (decisionType === 'confirm_source_and_facts') {
      updated = {
        ...run,
        status: 'waiting_for_user',
        stage: 'blueprint_approval',
        blueprint_id: run.blueprint_id || 701,
        review_id: run.review_id || 'review-mock-001',
        current_decision: {
          decision_type: 'approve_blueprint',
          stop_reason: 'blueprint_approval_required',
          summary: '来源和事实边界已确认，请审批自动蓝图。',
          required_actions: ['检查章节功能', '确认事实边界'],
          approval_summary: {
            chapter_function: '用共享语料支撑雨夜线索。',
            pov: 'close',
            fact_boundary_changes: ['known: 杯底半圈水痕', 'forbidden: 门外身份'],
            emotional_trajectory: 'restrained -> pressure',
            material_use_plan: '继续使用自动推荐素材，不要求手动绑定 anchor。',
            rewrite_budget: 'L0-L2',
            high_risk_findings: [],
          },
          proposed_blueprint_revision: null,
        },
        last_stop_reason: 'blueprint_approval_required',
        updated_at: now,
      }
    } else if (decisionType === 'approve_blueprint') {
      updated = {
        ...run,
        status: 'waiting_for_user',
        stage: 'final_insertion',
        candidate_ids: ['mock-candidate-001'],
        current_decision: {
          decision_type: 'approve_final_insertion',
          stop_reason: 'final_insertion_required',
          summary: '候选已通过审计，请在正文中显式插入。',
          required_actions: ['预览候选', '选择插入方式'],
          approval_summary: {
            chapter_function: '保留受限视角并承接雨夜线索。',
            pov: 'close',
            fact_boundary_changes: [],
            emotional_trajectory: 'pressure -> restraint',
            material_use_plan: '候选已改写并通过素材来源审计。',
            rewrite_budget: 'L0-L2',
            high_risk_findings: [],
          },
          proposed_blueprint_revision: null,
        },
        last_stop_reason: 'final_insertion_required',
        updated_at: now,
      }
    } else {
      updated = {
        ...run,
        status: 'waiting_for_user',
        updated_at: now,
      }
    }

    state.referenceOrchestrationRuns = state.referenceOrchestrationRuns.map((item) =>
      item.run_id === run.run_id ? updated : item)
    return updated
  }

  function getReferenceDraftCandidates(input = {}) {
    const blueprintId = Number(input?.blueprint_id ?? 701)
    const candidateIds = Array.isArray(input?.candidate_ids) ? input.candidate_ids : []
    return candidateIds.map((candidateId, index) => ({
      candidate_id: String(candidateId),
      blueprint_id: blueprintId,
      beat_id: `beat-${index + 1}`,
      material_id: 'material-global-rain',
      rewrite_level: 'L2',
      text: referenceCandidateText,
      changed_slots: [
        { slot_name: 'sensory_anchor', value: '杯底半圈水痕' },
      ],
      non_slot_edits: ['压缩直述，保留近距离视角。'],
      audit_status: 'passed',
      created_at: now,
      style_attempts: [
        {
          style_profile_ids: [],
          style_dimensions: ['sensory_ratio', 'inner_monologue_ratio'],
          imitation_intensity: 'moderate',
          min_style_fit: 0.8,
          allowed_closeness: 'moderate',
          required_evidence_types: ['sensory_anchor'],
          forbidden_style_risks: ['source_leak'],
          selected_material_style_fit: 0.91,
          selected_material_low_confidence: false,
          status: 'attempted',
        },
      ],
    }))
  }

  function getReferenceAnchoredDraftAudits(input = {}) {
    const blueprintId = Number(input?.blueprint_id ?? 701)
    const candidateIds = Array.isArray(input?.candidate_ids) ? input.candidate_ids.map(String) : ['mock-candidate-001']
    return [{
      audit_id: 'draft-audit-mock-001',
      blueprint_id: blueprintId,
      status: 'passed',
      rewrite_level: 'L2',
      provenance_errors: [],
      blueprint_errors: [],
      unsupported_fact_errors: [],
      pov_errors: [],
      ai_prose_risks: [],
      required_fixes: [],
      audited_at: now,
      candidate_ids: candidateIds,
      readable_report: {
        summary: `Draft audit passed for ${candidateIds.length} candidate(s) at rewrite level L2.`,
        candidate_ids: candidateIds,
        findings: [],
      },
    }]
  }

  function startReferenceCorpusFeatureAnalysis(input = {}) {
    const scope = normalizeReferenceCorpusFeatureAnalysisScope(input?.scope)
    const novelId = normalizeReferenceCorpusFeatureAnalysisId(input?.novel_id ?? state.activeNovelId, 'novel_id', true)
    const anchorId = normalizeReferenceCorpusFeatureAnalysisId(input?.anchor_id ?? 101, 'anchor_id', false)
    const requestedRunId = normalizeReferenceCorpusFeatureAnalysisRunId(input?.run_id)
    const tokenBudget = normalizeReferenceCorpusFeatureAnalysisTokenBudget(input?.token_budget)

    if (input?.resume === true && !requestedRunId) {
      throw new Error('Resume requires run_id.')
    }

    if (requestedRunId) {
      const existing = getReferenceCorpusFeatureAnalysisRun({ novel_id: novelId, run_id: requestedRunId })
      if (existing && input?.resume === true) return existing
    }

    const runId = requestedRunId ??
      `corpus-feature:${anchorId}:${scope}:mock-${String(state.nextReferenceCorpusFeatureAnalysisRunId++).padStart(3, '0')}`
    const families = referenceCorpusFeatureAnalysisFamilies(scope)
    const budgetExhausted = tokenBudget === 0
    const processedWorkItems = budgetExhausted ? 0 : families.length
    const observationCount = budgetExhausted ? 0 : families.length
    const tokensSpent = budgetExhausted ? 0 : Math.min(tokenBudget ?? (families.length * 24), families.length * 24)
    const run = {
      run_id: runId,
      novel_id: novelId,
      anchor_id: anchorId,
      scope,
      families,
      status: budgetExhausted ? 'budget_exhausted' : 'completed',
      token_budget: tokenBudget,
      tokens_spent: tokensSpent,
      resume_cursor: budgetExhausted ? `${scope}:0` : `${scope}:${families[families.length - 1]}`,
      observation_count: observationCount,
      processed_work_items: processedWorkItems,
      analyzer_version: 'reference-corpus-feature-llm-v1',
      schema_version: 'reference-corpus-feature-family-v1',
      model_provider: 'mock',
      model_id: 'gpt',
      started_at: now,
      completed_at: budgetExhausted ? null : now,
      diagnostics: budgetExhausted
        ? ['mock feature analysis stopped at the token budget boundary']
        : ['mock feature analysis completed'],
    }

    state.referenceCorpusFeatureAnalysisRuns = [
      run,
      ...state.referenceCorpusFeatureAnalysisRuns.filter((item) => item.run_id !== run.run_id),
    ]
    return run
  }

  function getReferenceCorpusFeatureAnalysisRun(input = {}) {
    const runId = String(input?.run_id ?? '').trim()
    if (!runId) return null
    const novelId = normalizeReferenceCorpusFeatureAnalysisId(input?.novel_id ?? state.activeNovelId, 'novel_id', true)
    return state.referenceCorpusFeatureAnalysisRuns.find((run) =>
      run.run_id === runId && Number(run.novel_id) === novelId) ?? null
  }

 function startReferenceCorpusTechniqueSpecimenAnalysis(input = {}) {
 const sourceNodeType = normalizeReferenceCorpusTechniqueSpecimenSourceNodeType(input?.source_node_type)
 const novelId = normalizeReferenceCorpusFeatureAnalysisId(input?.novel_id ?? state.activeNovelId, 'novel_id', true)
 const anchorId = normalizeReferenceCorpusFeatureAnalysisId(input?.anchor_id ?? 101, 'anchor_id', false)
 const requestedRunId = normalizeReferenceCorpusFeatureAnalysisRunId(input?.run_id)
 const minObservationConfidence = normalizeReferenceCorpusTechniqueSpecimenConfidence(input?.min_observation_confidence)
 const tokenBudget = normalizeReferenceCorpusFeatureAnalysisTokenBudget(input?.token_budget)

 if (input?.resume === true && !requestedRunId) {
 throw new Error('Resume requires run_id.')
 }

 const existing = requestedRunId
 ? getReferenceCorpusTechniqueSpecimenAnalysisRun({ novel_id: novelId, run_id: requestedRunId })
 : null
 if (existing && input?.resume !== true) {
 throw new Error('Existing technique specimen run requires resume=true.')
 }
 if (input?.resume === true && !existing) {
 throw new Error('Technique specimen analysis run was not found.')
 }
 if (existing && existing.status !== 'budget_exhausted') {
 throw new Error('Only budget-exhausted technique specimen runs can be resumed.')
 }
 if (existing && (tokenBudget == null || tokenBudget <= existing.tokens_spent)) {
 throw new Error('Resume token_budget must be greater than tokens_spent.')
 }

 const runId = requestedRunId ??
 `corpus-technique:${anchorId}:${sourceNodeType}:mock-${String(state.nextReferenceCorpusTechniqueSpecimenAnalysisRunId++).padStart(3, '0')}`
 const totalNodes = sourceNodeType === 'passage' ? 2 : 1
 const tokensPerNode = 24
 const previousProcessedNodes = existing?.processed_nodes ?? 0
 const previousTokensSpent = existing?.tokens_spent ?? 0
 const effectiveBudget = tokenBudget ?? (totalNodes * tokensPerNode)
 const affordableNodeCount = Math.floor(Math.max(0, effectiveBudget - previousTokensSpent) / tokensPerNode)
 const processedNodes = Math.min(totalNodes, previousProcessedNodes + affordableNodeCount)
 const newlyProcessedNodes = processedNodes - previousProcessedNodes
 const tokensSpent = previousTokensSpent + (newlyProcessedNodes * tokensPerNode)
 const budgetExhausted = processedNodes < totalNodes
 const run = {
 run_id: runId,
 novel_id: novelId,
 anchor_id: anchorId,
 scope: 'technique_specimen',
 status: budgetExhausted ? 'budget_exhausted' : 'completed',
 token_budget: tokenBudget,
 tokens_spent: tokensSpent,
 resume_cursor: `${sourceNodeType}:${processedNodes}`,
 specimen_count: processedNodes,
 processed_nodes: processedNodes,
 analyzer_version: 'reference-corpus-technique-specimen-llm-v1',
 schema_version: 'reference-corpus-technique-specimen-v1',
 model_provider: 'mock',
 model_id: 'gpt',
 started_at: existing?.started_at ?? now,
 completed_at: budgetExhausted ? null : now,
 diagnostics: budgetExhausted
 ? [`mock technique specimen analysis stopped at ${processedNodes}/${totalNodes} nodes for min confidence ${minObservationConfidence}`]
 : [`mock technique specimen analysis completed at min confidence ${minObservationConfidence}`],
 }

    state.referenceCorpusTechniqueSpecimenAnalysisRuns = [
      run,
      ...state.referenceCorpusTechniqueSpecimenAnalysisRuns.filter((item) => item.run_id !== run.run_id),
    ]
    return run
  }

  function getReferenceCorpusTechniqueSpecimenAnalysisRun(input = {}) {
    const runId = String(input?.run_id ?? '').trim()
    if (!runId) return null
    const novelId = normalizeReferenceCorpusFeatureAnalysisId(input?.novel_id ?? state.activeNovelId, 'novel_id', true)
    return state.referenceCorpusTechniqueSpecimenAnalysisRuns.find((run) =>
      run.run_id === runId && Number(run.novel_id) === novelId) ?? null
  }

  function listReferenceCorpusFeatureObservations(input = {}) {
    const novelId = normalizeReferenceCorpusFeatureAnalysisId(input?.novel_id ?? state.activeNovelId, 'novel_id', true)
    const anchorId = normalizeReferenceCorpusFeatureAnalysisId(input?.anchor_id ?? 101, 'anchor_id', false)
    const nodeId = String(input?.node_id ?? 'mock-node-rain-001').trim()
    const page = normalizeReferenceCorpusAnalysisPage(input?.page_request, ['created_at', 'feature_family', 'confidence', 'observation_id'], 'feature_family')
    const filters = page.filters ?? {}
    const observations = mockReferenceCorpusFeatureObservations(novelId, anchorId, nodeId)
      .filter((item) => !filters.feature_family || item.feature_family === filters.feature_family)
      .filter((item) => !filters.feature_key || item.feature_key === filters.feature_key)
      .filter((item) => !filters.node_type || item.node_type === filters.node_type)
      .filter((item) => !filters.run_id || item.run_id === filters.run_id)
      .filter((item) => (filters.validity_state ?? 'active') === item.validity_state)
      .filter((item) => !filters.min_confidence || item.confidence >= Number(filters.min_confidence))
    return pageReferenceCorpusAnalysisItems(sortReferenceCorpusAnalysisItems(observations, page.sort_by, page.sort_dir), page)
  }

  function listReferenceCorpusTechniqueSpecimens(input = {}) {
    const novelId = normalizeReferenceCorpusFeatureAnalysisId(input?.novel_id ?? state.activeNovelId, 'novel_id', true)
    const anchorId = normalizeReferenceCorpusFeatureAnalysisId(input?.anchor_id ?? 101, 'anchor_id', false)
    const sourceNodeId = String(input?.source_node_id ?? 'mock-node-rain-001').trim()
    const page = normalizeReferenceCorpusAnalysisPage(input?.page_request, ['created_at', 'technique_family', 'confidence', 'specimen_id'], 'confidence')
    const filters = page.filters ?? {}
    const specimens = mockReferenceCorpusTechniqueSpecimens(novelId, anchorId, sourceNodeId)
      .filter((item) => !filters.technique_family || item.technique_family === filters.technique_family)
      .filter((item) => !filters.run_id || item.analysis_run_id === filters.run_id)
      .filter((item) => (filters.validity_state ?? 'active') === item.validity_state)
      .filter((item) => !filters.min_confidence || item.confidence >= Number(filters.min_confidence))
    return pageReferenceCorpusAnalysisItems(sortReferenceCorpusAnalysisItems(specimens, page.sort_by, page.sort_dir), page)
  }

  function mockReferenceCorpusFeatureObservations(novelId, anchorId, nodeId) {
    return [
      {
        observation_id: `mock-${anchorId}-${nodeId}-emotion-state`,
        node_id: nodeId,
        anchor_id: anchorId,
        node_type: 'sentence',
        text_hash: `hash-${nodeId}`,
        feature_family: 'emotion',
        feature_key: 'emotion_state',
        value_kind: 'enum',
        value_preview: 'restrained',
        value_text: 'restrained',
        value_num: 7,
        value_bool: null,
        intensity: 7,
        confidence: 0.88,
        evidence_start: 0,
        evidence_end: 14,
        evidence_preview: '把杯底半圈水痕压进记忆里',
        explanation: '动作压住外显情绪，让压力停在可见细节上。',
        review_state: 'unverified',
        validity_state: 'active',
        run_id: `mock-feature:${anchorId}:sentence`,
        created_at: now,
        novel_id: novelId,
      },
      {
        observation_id: `mock-${anchorId}-${nodeId}-rhythm-cadence`,
        node_id: nodeId,
        anchor_id: anchorId,
        node_type: 'sentence',
        text_hash: `hash-${nodeId}`,
        feature_family: 'rhythm',
        feature_key: 'cadence',
        value_kind: 'enum',
        value_preview: '短促后停顿',
        value_text: '短促后停顿',
        value_num: null,
        value_bool: null,
        intensity: 6,
        confidence: 0.82,
        evidence_start: 0,
        evidence_end: 18,
        evidence_preview: '没有急着回头',
        explanation: '句尾把动作停住，给读者留出反应时间。',
        review_state: 'unverified',
        validity_state: 'active',
        run_id: `mock-feature:${anchorId}:sentence`,
        created_at: now,
        novel_id: novelId,
      },
      {
        observation_id: `mock-${anchorId}-${nodeId}-rhetoric-ellipsis`,
        node_id: nodeId,
        anchor_id: anchorId,
        node_type: 'sentence',
        text_hash: `hash-${nodeId}`,
        feature_family: 'rhetoric',
        feature_key: 'devices',
        value_kind: 'array',
        value_preview: '动作替代心理陈述',
        value_text: '动作替代心理陈述',
        value_num: null,
        value_bool: null,
        intensity: null,
        confidence: 0.84,
        evidence_start: 0,
        evidence_end: 20,
        evidence_preview: '把杯底半圈水痕压进记忆里',
        explanation: '不直接写情绪词，用动作承载未说出口的反应。',
        review_state: 'unverified',
        validity_state: 'active',
        run_id: `mock-feature:${anchorId}:sentence`,
        created_at: now,
        novel_id: novelId,
      },
    ]
  }

  function mockReferenceCorpusTechniqueSpecimens(novelId, anchorId, sourceNodeId) {
    const evidence = mockReferenceCorpusFeatureObservations(novelId, anchorId, sourceNodeId).filter((item) =>
      item.feature_family === 'emotion' || item.feature_family === 'rhetoric')
    const evidencePayload = evidence.map((item) => ({
      observation_id: item.observation_id,
      node_id: item.node_id,
      node_type: item.node_type,
      text_hash: item.text_hash,
      feature_family: item.feature_family,
      feature_key: item.feature_key,
      confidence: item.confidence,
      evidence_start: item.evidence_start,
      evidence_end: item.evidence_end,
      evidence_preview: item.evidence_preview,
      value_preview: item.value_preview,
      explanation: item.explanation,
    }))
    return [{
      specimen_id: `mock-technique-${anchorId}-${sourceNodeId}`,
      source_node_id: sourceNodeId,
      source_anchor_id: anchorId,
      analysis_run_id: `mock-technique:${anchorId}:sentence`,
      technique_family: 'action_as_emotion',
      technique_abstract: '用细节动作承载压抑情绪，省略直接情绪陈述，以留白放大张力',
      trigger_context: '角色承压但不能把真实反应说出口',
      transfer_template: '[角色] [细节动作]，随后压住即时反应。',
      transfer_slots: [{
        slot_name: 'character',
        purpose: '当前承压角色',
        constraints: '必须处在强情绪但不可直说的场景',
      }],
      effect_on_reader: '读者从动作和停顿里补全情绪，紧张感更稳。',
      applicability_conditions: ['短句节点', '角色需要克制', '上下文已经有压力来源'],
      failure_modes: ['动作没有因果会变成装饰', '连续使用会显得程式化'],
      anti_patterns: ['直接解释“他很愤怒”', '动作与角色目标无关'],
      world_context_dependencies: [],
      why_it_works: {
        contributing_factors: [{
          factor: '外化动作提供可见证据',
          observation_ids: evidencePayload.map((item) => item.observation_id),
          explanation: '情绪和修辞 observation 同时指向动作承载情绪，迁移时可以保留技法而替换人物与动作。',
          evidence: evidencePayload,
        }],
        trace_complete: true,
      },
      confidence: 0.86,
      review_state: 'unverified',
      validity_state: 'active',
      mastery_notes: '适合短句或段尾，不适合需要密集信息交代的位置。',
      created_at: now,
      evidence: evidencePayload,
    }]
  }

  function normalizeReferenceCorpusAnalysisPage(request = {}, allowedSorts, defaultSort) {
    const pageSize = Number(request?.page_size ?? 20)
    if (!Number.isInteger(pageSize) || pageSize <= 0 || pageSize > 200) {
      throw new Error('page_size must be between 1 and 200.')
    }

    const sortBy = String(request?.sort_by ?? defaultSort).trim() || defaultSort
    if (!allowedSorts.includes(sortBy)) {
      throw new Error(`sort_by '${sortBy}' is not supported.`)
    }

    const sortDir = String(request?.sort_dir ?? 'desc').trim().toLowerCase()
    if (sortDir !== 'asc' && sortDir !== 'desc') {
      throw new Error("sort_dir must be 'asc' or 'desc'.")
    }

    const cursorText = String(request?.cursor ?? '').trim()
    const offset = cursorText === '' ? 0 : Number(cursorText)
    if (!Number.isInteger(offset) || offset < 0) {
      throw new Error('cursor is invalid.')
    }

    return {
      page_size: pageSize,
      sort_by: sortBy,
      sort_dir: sortDir,
      cursor: cursorText,
      offset,
      filters: request?.filters && typeof request.filters === 'object' ? request.filters : {},
    }
  }

  function sortReferenceCorpusAnalysisItems(items, sortBy, sortDir) {
    const direction = sortDir === 'asc' ? 1 : -1
    return [...items].sort((left, right) => {
      const primary = compareReferenceCorpusAnalysisValues(left[sortBy], right[sortBy])
      if (primary !== 0) return primary * direction
      return compareReferenceCorpusAnalysisValues(left.created_at, right.created_at) * direction ||
        compareReferenceCorpusAnalysisValues(left.observation_id ?? left.specimen_id, right.observation_id ?? right.specimen_id) * direction
    })
  }

  function compareReferenceCorpusAnalysisValues(left, right) {
    if (typeof left === 'number' && typeof right === 'number') return left - right
    return String(left ?? '').localeCompare(String(right ?? ''))
  }

  function pageReferenceCorpusAnalysisItems(items, page) {
    const visible = items.slice(page.offset, page.offset + page.page_size)
    const nextOffset = page.offset + visible.length
    const hasMore = nextOffset < items.length
    return {
      items: visible,
      total: items.length,
      page: Math.floor(page.offset / page.page_size) + 1,
      size: page.page_size,
      total_pages: items.length === 0 ? 0 : Math.ceil(items.length / page.page_size),
      next_cursor: hasMore ? String(nextOffset) : null,
      has_more: hasMore,
      total_estimate: items.length,
    }
  }

  function normalizeReferenceCorpusFeatureAnalysisScope(scope) {
    const normalized = String(scope ?? 'sentence').trim()
    if (normalized === 'sentence' || normalized === 'passage') return normalized
    throw new Error('Feature analysis scope must be sentence or passage.')
  }

  function normalizeReferenceCorpusTechniqueSpecimenSourceNodeType(sourceNodeType) {
    const normalized = String(sourceNodeType ?? 'sentence').trim()
    if (normalized === 'sentence' || normalized === 'passage') return normalized
    throw new Error('Technique specimen source_node_type must be sentence or passage.')
  }

  function referenceCorpusFeatureAnalysisFamilies(scope) {
    return scope === 'passage'
      ? ['narrative', 'pov', 'action', 'character', 'commercial']
      : ['syntax', 'rhythm', 'sensory', 'emotion', 'rhetoric']
  }

  function normalizeReferenceCorpusFeatureAnalysisId(value, name, allowZero) {
    const id = Number(value)
    if (!Number.isInteger(id) || id < 0 || (!allowZero && id === 0)) {
      throw new Error(`${name} must be ${allowZero ? 'non-negative' : 'positive'}.`)
    }

    return id
  }

  function normalizeReferenceCorpusFeatureAnalysisRunId(value) {
    if (value == null || String(value).trim() === '') return ''
    const runId = String(value).trim()
    if (runId.length > 128 || /[^A-Za-z0-9_:.-]/.test(runId)) {
      throw new Error('run_id contains unsupported characters.')
    }

    return runId
  }

  function normalizeReferenceCorpusFeatureAnalysisTokenBudget(value) {
    if (value == null) return null
    const tokenBudget = Number(value)
    if (!Number.isInteger(tokenBudget) || tokenBudget < 0) {
      throw new Error('token_budget must be a non-negative integer.')
    }

    return tokenBudget
  }

  function normalizeReferenceCorpusTechniqueSpecimenConfidence(value) {
    if (value == null) return 0.70
    const confidence = Number(value)
    if (!Number.isFinite(confidence) || confidence < 0 || confidence > 0.95) {
      throw new Error('min_observation_confidence must be between 0 and 0.95.')
    }

    return confidence
  }

  function generateReferenceCorpusBlueprintCandidates(input = {}) {
    const chapterContext = input?.chapter_context ?? {}
    const novelId = Number(chapterContext.novel_id ?? 42)
    const libraryIds = Array.isArray(input?.scope?.library_ids) ? input.scope.library_ids : []
    const defaultProjectLibraryId = `project:${novelId}:default`
    const effectiveLibraryIds = libraryIds.length > 0
      ? libraryIds
      : [defaultProjectLibraryId, 'global:workspace']
    const feedback = input?.feedback ?? null
    const feedbackApplied = hasCorpusBlueprintFeedback(feedback)
    const problemTags = Array.isArray(feedback?.problem_tags)
      ? feedback.problem_tags.map((tag) => String(tag))
      : []
    const sourceRepetitionFeedback = feedbackApplied && problemTags.includes('source_repetition')
    const fallbackReasonCodes = sourceRepetitionFeedback
      ? ['feedback_filters_no_matches', 'fallback_to_base_filters']
      : []
    const feedbackSummary = describeCorpusBlueprintFeedback(feedback, fallbackReasonCodes)
    const count = Math.max(2, Math.min(5, Number(input?.requested_count ?? 2) || 2))
    const primaryLibraryId = feedbackApplied && feedback?.avoid_library_ids?.includes(effectiveLibraryIds[0])
      ? effectiveLibraryIds[1] ?? 'global:workspace'
      : effectiveLibraryIds[0] ?? defaultProjectLibraryId
    const secondaryLibraryId = feedbackApplied
      ? 'project:42:contrast'
      : effectiveLibraryIds[1] ?? 'global:workspace'
    const primaryAnchorId = feedbackApplied && feedback?.avoid_anchor_ids?.includes(101) ? 104 : 101
    const secondaryAnchorId = feedbackApplied ? 108 : 104
    const candidateSeeds = [{
      id: feedbackApplied ? 'mock-corpus-blueprint-alt-001' : 'mock-corpus-blueprint-001',
      strategy: sourceRepetitionFeedback
        ? 'source_repetition_diversity_m1'
        : feedbackApplied
        ? '避开已拒绝水痕节点，改用钟楼回声材料制造压力递进'
        : '先用杯底水痕建立线索压力，再以受限视角延迟判断',
      nodeIds: sourceRepetitionFeedback
        ? ['mock-node-clock-001', 'mock-node-restart-001', 'mock-node-pause-002']
        : feedbackApplied ? ['mock-node-clock-001', 'mock-node-pause-002'] : ['mock-node-rain-001', 'mock-node-pause-001'],
      libraryId: primaryLibraryId,
      anchorId: primaryAnchorId,
      coverage: feedbackApplied ? 0.84 : 0.91,
      sourceDistribution: sourceRepetitionFeedback
        ? [{
          library_id: primaryLibraryId,
          anchor_id: primaryAnchorId,
          node_count: 1,
        }, {
          library_id: secondaryLibraryId,
          anchor_id: secondaryAnchorId,
          node_count: 2,
        }]
        : null,
      feedbackReason: sourceRepetitionFeedback
        ? feedbackSummary
        : feedbackApplied ? '已避开上一轮拒绝的蓝图、节点或来源。' : '',
      gapReasons: sourceRepetitionFeedback
        ? fallbackReasonCodes
        : feedbackApplied ? ['水痕节点被反馈排除，改用相邻压力材料补位。'] : [],
      gapPositions: sourceRepetitionFeedback
        ? [{
          beatIndex: 0,
          coveredDimensions: ['emotion'],
          missingDimensions: ['rhythm', 'narrative', 'technique'],
          gapReasons: ['missing_rhythm_evidence', 'missing_narrative_evidence', 'missing_technique_coverage'],
        }]
        : [],
    }, {
      id: feedbackApplied ? 'mock-corpus-blueprint-alt-002' : 'mock-corpus-blueprint-002',
      strategy: feedbackApplied
        ? '改走外部环境压迫路线，降低同一语料库节点占比'
        : '从门缝停顿切入，补充环境细节后回扣水痕线索',
      nodeIds: feedbackApplied ? ['mock-node-restart-001', 'mock-node-slot-001', 'mock-node-recovery-001'] : ['mock-node-door-001', 'mock-node-rain-002'],
      libraryId: secondaryLibraryId,
      anchorId: secondaryAnchorId,
      coverage: feedbackApplied ? 0.79 : 0.86,
      feedbackReason: feedbackApplied ? '根据 avoid/rejected 反馈切换来源分布。' : '',
      gapReasons: feedbackApplied ? ['可用来源变窄，覆盖率略低。'] : ['缺少更强的角色内心节点。'],
      gapPositions: feedbackApplied
        ? [{
          beatIndex: 1,
          coveredDimensions: ['rhythm'],
          missingDimensions: ['emotion', 'technique'],
          gapReasons: ['missing_emotion_evidence', 'missing_technique_coverage'],
        }]
        : [],
    }]
    const candidates = Array.from({ length: count }, (_, index) => {
      const seed = candidateSeeds[index] ?? {
        ...candidateSeeds[index % candidateSeeds.length],
        id: `mock-corpus-blueprint-extra-${index + 1}`,
        coverage: Math.max(0.6, candidateSeeds[index % candidateSeeds.length].coverage - (index * 0.03)),
      }
      const blueprint = makeCorpusInsertionBlueprint(seed.id, seed.strategy, seed.nodeIds)
      return {
        blueprint,
        source_distribution: seed.sourceDistribution ?? [{
          library_id: seed.libraryId,
          anchor_id: seed.anchorId,
          node_count: seed.nodeIds.length,
        }],
        coverage_score: seed.coverage,
        gap_reasons: seed.gapReasons,
        feedback_reason: seed.feedbackReason,
        gap_positions: makeCorpusBlueprintGapPositions(blueprint, seed.gapPositions ?? []),
      }
    })

    return {
      query_context: {
        scene_type: 'rain_threshold',
        emotion_target: 'restrained_pressure',
        pacing_target: feedbackApplied ? 'varied' : 'tight',
        narrative_position: 'chapter_insert',
        commercial_mechanic: 'clue_hook',
        character_states: ['current_chapter_focus'],
        required_narrative_functions: ['clue_pressure'],
        chapter_context: chapterContext,
        scope: input?.scope ?? {
          library_ids: libraryIds,
          reuse_policies: ['verbatim_ok', 'adapted_only'],
          include_anchor_ids: [],
          exclude_anchor_ids: [],
          session_id: `project:${novelId}:default`,
        },
      },
      candidates,
      feedback_applied: feedbackApplied,
      feedback_summary: feedbackSummary,
    }
  }

  function hasCorpusBlueprintFeedback(feedback) {
    if (!feedback || typeof feedback !== 'object') return false
    return [
      feedback.rejected_blueprint_ids,
      feedback.rejected_node_ids,
      feedback.avoid_library_ids,
      feedback.avoid_anchor_ids,
      feedback.problem_tags,
    ].some((value) => Array.isArray(value) && value.length > 0) ||
      String(feedback.notes ?? '').trim().length > 0
  }

  function describeCorpusBlueprintFeedback(feedback, fallbackReasonCodes = []) {
    if (!feedback || typeof feedback !== 'object') return 'none'
    const parts = []
    if (Array.isArray(feedback.rejected_blueprint_ids) && feedback.rejected_blueprint_ids.length > 0) {
      parts.push(`rejected_blueprints:${feedback.rejected_blueprint_ids.length}`)
    }
    if (Array.isArray(feedback.rejected_node_ids) && feedback.rejected_node_ids.length > 0) {
      parts.push(`rejected_nodes:${feedback.rejected_node_ids.length}`)
    }
    if (Array.isArray(feedback.avoid_library_ids) && feedback.avoid_library_ids.length > 0) {
      parts.push(`avoid_libraries:${feedback.avoid_library_ids.length}`)
    }
    if (Array.isArray(feedback.avoid_anchor_ids) && feedback.avoid_anchor_ids.length > 0) {
      parts.push(`avoid_anchors:${feedback.avoid_anchor_ids.length}`)
    }
    if (Array.isArray(feedback.problem_tags) && feedback.problem_tags.length > 0) {
      parts.push(`problems:${feedback.problem_tags.join(',')}`)
    }
    if (fallbackReasonCodes.length > 0) {
      parts.push(`fallback:${fallbackReasonCodes.join(',')}`)
    }
    return parts.length > 0 ? parts.join(';') : 'feedback_present'
  }

  function makeCorpusInsertionBlueprint(blueprintId, strategy, nodeIds) {
    return {
      blueprint_id: blueprintId,
      query_context_hash: `mock-query-context-hash-${blueprintId}`,
      strategy,
      beats: nodeIds.map((nodeId, index) => ({
        beat_id: `${blueprintId}-beat-${index + 1}`,
        beat_index: index,
        role_in_beat: index === 0 ? 'establish_clue_pressure' : 'deepen_restrained_reaction',
        narrative_function: index === 0 ? 'clue_pressure' : 'emotional_pressure',
        node_ids: [nodeId],
      })),
    }
  }

  function makeCorpusBlueprintGapPositions(blueprint, seeds) {
    return seeds
      .map((seed) => {
        const beatIndex = Number(seed.beatIndex ?? 0)
        const beat = blueprint.beats?.[beatIndex]
        if (!beat) return null
        return {
          beat_id: beat.beat_id,
          beat_index: beat.beat_index,
          role_in_beat: beat.role_in_beat,
          narrative_function: beat.narrative_function,
          node_ids: beat.node_ids,
          covered_dimensions: seed.coveredDimensions ?? [],
          missing_dimensions: seed.missingDimensions ?? [],
          gap_reasons: seed.gapReasons ?? [],
        }
      })
      .filter(Boolean)
  }

  function generateReferenceCorpusInsertionDraft(input = {}) {
    const chapterContext = input?.chapter_context ?? {}
    const currentDraft = String(chapterContext.current_draft_text ?? '')
    const requestedOffset = Number(chapterContext.insertion_offset ?? currentDraft.length)
    const insertionOffset = Number.isFinite(requestedOffset)
      ? Math.max(0, Math.min(currentDraft.length, requestedOffset))
      : currentDraft.length
    const prefix = currentDraft.length === 0
      ? ''
      : currentDraft.slice(0, insertionOffset).endsWith('\n') ? '\n' : '\n\n'
    const assembledText = '林岚把杯底半圈水痕压进记忆里，没有急着回头。'
    const chapterTextAfterInsertion = `${currentDraft.slice(0, insertionOffset)}${prefix}${assembledText}${currentDraft.slice(insertionOffset)}`
    const libraryIds = Array.isArray(input?.scope?.library_ids) ? input.scope.library_ids : []
    const novelId = Number(chapterContext.novel_id ?? 42)
    const defaultProjectLibraryId = `project:${novelId}:default`
    const effectiveLibraryIds = libraryIds.length > 0
      ? libraryIds
      : [defaultProjectLibraryId, 'global:workspace']
    const selectedBlueprint = input?.selected_blueprint && typeof input.selected_blueprint === 'object'
      ? input.selected_blueprint
      : null

    return {
      query_context: {
        scene_type: 'rain_threshold',
        emotion_target: 'restrained_pressure',
        pacing_target: 'tight',
        narrative_position: 'chapter_insert',
        commercial_mechanic: 'clue_hook',
        character_states: ['current_chapter_focus'],
        required_narrative_functions: ['clue_pressure'],
        chapter_context: chapterContext,
        scope: input?.scope ?? {
          library_ids: libraryIds,
          reuse_policies: ['verbatim_ok', 'adapted_only'],
          include_anchor_ids: [],
          exclude_anchor_ids: [],
          session_id: `project:${novelId}:default`,
        },
      },
      blueprint: selectedBlueprint ?? {
        blueprint_id: 'mock-corpus-blueprint-001',
        query_context_hash: 'mock-query-context-hash-001',
        strategy: '自动检索共享语料并迁移为当前章节插入片段',
        beats: [{
          beat_id: 'mock-corpus-beat-001',
          beat_index: 0,
          role_in_beat: 'insert_clue_pressure',
          narrative_function: 'clue_pressure',
          node_ids: ['mock-node-rain-001'],
        }],
      },
      pieces: [{
        piece_id: 'mock-corpus-piece-001',
        beat_id: 'mock-corpus-beat-001',
        candidate_id: 'mock-corpus-candidate-001',
        node_id: 'mock-node-rain-001',
        anchor_id: 101,
        library_id: effectiveLibraryIds[0] ?? 'global:workspace',
        text_hash: 'hash-mock-node-rain-001',
        reuse_policy: 'adapted_only',
        license_state: 'authorized',
        output_text: assembledText,
        preserved_text_hash: 'hash-preserved-mock-corpus-001',
        preserved_hash_matches: true,
        preserved_spans: [{
          span_id: 'mock-preserved-span-001',
          source_start: 1,
          source_end: 10,
          output_start: 2,
          output_end: 11,
          source_text_hash: 'hash-preserved-span-mock-corpus-001',
          output_text_hash: 'hash-preserved-span-mock-corpus-001',
          matches: true,
        }],
        locked_spans: [],
        slot_replacements: [{
          slot_name: 'character',
          source_value: '她',
          replacement_value: '林岚',
          source_start: 0,
          source_end: 1,
          output_start: 0,
          output_end: 2,
        }],
      }],
      slot_replacements: [{
        slot_name: 'character',
        source_value: '她',
        replacement_value: '林岚',
        source_start: 0,
        source_end: 1,
        output_start: 0,
        output_end: 2,
      }],
      transitions: [],
      assembled_text: assembledText,
      chapter_text_after_insertion: chapterTextAfterInsertion,
      ready_for_insertion: true,
      gate: {
        passed: true,
        status: 'passed',
        errors: [],
        pieces: [{
          piece_id: 'mock-corpus-piece-001',
          node_id: 'mock-node-rain-001',
          should_block: false,
          four_gram_containment_ratio: 0.08,
          longest_common_substring_ratio: 0.11,
          violations: [],
        }],
      },
      audit: {
        passed: true,
        status: 'passed',
        errors: [],
        pieces: [{
          piece_id: 'mock-corpus-piece-001',
          node_id: 'mock-node-rain-001',
          passed: true,
          preserved_span_count: 1,
          mismatched_span_count: 0,
          violations: [],
        }],
        transitions: [],
      },
    }
  }

  function generateReferenceCorpusInsertionDraftCandidates(input = {}) {
    const selectedBlueprint = input?.selected_blueprint && typeof input.selected_blueprint === 'object'
      ? input.selected_blueprint
      : makeCorpusInsertionBlueprint(
        'mock-corpus-blueprint-001',
        '自动检索共享语料并迁移为当前章节插入片段',
        ['mock-node-rain-001'])
    const requestedSlotVariants = Array.isArray(input?.slot_value_variants)
      ? input.slot_value_variants.filter((variant) => variant && typeof variant === 'object')
      : []
    if (requestedSlotVariants.length > 0) {
      return buildMockCorpusSlotValueDraftCandidates(input, selectedBlueprint, requestedSlotVariants)
    }

    const count = Math.max(2, Math.min(4, Number(input?.requested_count ?? 2) || 2))
    const firstBeat = selectedBlueprint.beats?.[0] ?? {
      beat_id: 'mock-corpus-beat-001',
      node_ids: ['mock-node-rain-001'],
    }
    const secondBeat = selectedBlueprint.beats?.[1] ?? firstBeat
    const variants = [{
      candidate_id: 'mock-corpus-draft-candidate-001',
      strategy: 'source_variant_1',
      explanation: '保留选中蓝图首选节点，但审计发现保留片段不一致，必须阻断。',
      text: '林岚把杯底半圈水痕压进记忆里，没有急着回头。',
      piece_id: 'mock-corpus-piece-001',
      beat_id: firstBeat.beat_id,
      node_id: firstBeat.node_ids?.[0] ?? 'mock-node-rain-001',
      auditBlocked: true,
    }, {
      candidate_id: 'mock-corpus-draft-candidate-002',
      strategy: 'transition_repair',
      explanation: '转场分析要求换用选中蓝图同一节拍内的备选语料，重组后重新通过审计。',
      text: '林岚把门外的雨声留在身后，指尖仍压着那枚钥匙。',
      transitionText: '门外的雨声把沉默往前推了一寸。',
      secondText: '她没有立刻开口，只让视线落回那道水痕。',
      piece_id: 'mock-corpus-piece-002',
      second_piece_id: 'mock-corpus-piece-002b',
      beat_id: secondBeat.beat_id,
      node_id: secondBeat.node_ids?.[0] ?? firstBeat.node_ids?.[1] ?? 'mock-node-door-001',
      second_node_id: firstBeat.node_ids?.[1] ?? 'mock-node-pause-001',
    }, {
      candidate_id: 'mock-corpus-draft-candidate-003',
      strategy: 'source_variant_3',
      explanation: '转场分析要求换源，但替代节点不在选中蓝图节拍内，必须回到蓝图重组。',
      text: '林岚没有回头，只把钥匙扣在掌心。',
      secondText: '雨声贴着门缝往里挤。',
      piece_id: 'mock-corpus-piece-003',
      second_piece_id: 'mock-corpus-piece-003b',
      beat_id: firstBeat.beat_id,
      node_id: firstBeat.node_ids?.[1] ?? firstBeat.node_ids?.[0] ?? 'mock-node-pause-001',
      second_node_id: secondBeat.node_ids?.[0] ?? 'mock-node-door-001',
      replacePieceBlocked: true,
      replacement_node_id: 'mock-node-outside-selected-blueprint',
    }]
    const drafts = variants.slice(0, count).map((variant) => {
      const draft = buildMockCorpusInsertionDraft(input, selectedBlueprint, variant)
      const nextAction = buildMockCorpusDraftCandidateNextAction(selectedBlueprint, variant)
      return {
        candidate_id: variant.candidate_id,
        strategy: variant.strategy,
        explanation: variant.explanation,
        draft,
        ...(nextAction ? { next_action: nextAction } : {}),
      }
    })

    return {
      query_context: drafts[0]?.draft?.query_context ?? generateReferenceCorpusInsertionDraft(input).query_context,
      selected_blueprint: selectedBlueprint,
      candidates: drafts,
    }
  }

  function buildMockCorpusSlotValueDraftCandidates(input, selectedBlueprint, requestedSlotVariants) {
    const count = Math.max(1, Math.min(
      requestedSlotVariants.length,
      Number(input?.requested_count ?? requestedSlotVariants.length) || requestedSlotVariants.length))
    const firstBeat = selectedBlueprint.beats?.[0] ?? {
      beat_id: 'mock-corpus-beat-001',
      node_ids: ['mock-node-rain-001'],
    }
    const nodeId = firstBeat.node_ids?.[0] ?? 'mock-node-rain-001'
    const drafts = requestedSlotVariants.slice(0, count).map((slotVariant, index) => {
      const suffix = index + 1
      const slotValues = {
        ...(input?.slot_values && typeof input.slot_values === 'object' && !Array.isArray(input.slot_values)
          ? input.slot_values
          : {}),
        ...(slotVariant.slot_values && typeof slotVariant.slot_values === 'object' && !Array.isArray(slotVariant.slot_values)
          ? slotVariant.slot_values
          : {}),
      }
      const transferred = buildMockCorpusTransferredSlotSentence(slotValues)
      const variant = {
        candidate_id: `mock-corpus-slot-draft-candidate-${suffix}`,
        strategy: `slot_variant_${suffix}`,
        explanation: `复用同一选中蓝图和同一语料节点，仅按槽位映射生成正文候选：${String(slotVariant.label ?? slotVariant.variant_id ?? suffix)}`,
        text: transferred.text,
        piece_id: `mock-corpus-slot-piece-${suffix}`,
        beat_id: firstBeat.beat_id,
        node_id: nodeId,
        slot_replacements: transferred.slotReplacements,
      }
      const draft = buildMockCorpusInsertionDraft(
        { ...input, slot_values: slotValues },
        selectedBlueprint,
        variant)
      return {
        candidate_id: variant.candidate_id,
        strategy: variant.strategy,
        explanation: variant.explanation,
        draft,
      }
    })

    return {
      query_context: drafts[0]?.draft?.query_context ?? generateReferenceCorpusInsertionDraft(input).query_context,
      selected_blueprint: selectedBlueprint,
      candidates: drafts,
    }
  }

  function buildMockCorpusTransferredSlotSentence(slotValues) {
    const sourceText = '她在旧市集门口没有立刻开口，只叫了一声师兄，把钥匙扣在掌心，《旧市集门口师兄钥匙案》没有改。'
    const character = readMockSlotValue(slotValues, ['character:她', '角色:她', '她', 'character'], '林岚')
    const place = readMockSlotValue(slotValues, ['place:旧市集门口', '地点:旧市集门口', '旧市集门口', 'place'], '雨廊门口')
    const honorific = readMockSlotValue(slotValues, ['honorific:师兄', '称谓:师兄', '师兄', 'honorific'], '师兄')
    const plotObject = readMockSlotValue(slotValues, ['plot_object:钥匙', '道具:钥匙', '钥匙', 'plot_object'], '钥匙')
    const outputText = `${character}在${place}没有立刻开口，只叫了一声${honorific}，把${plotObject}扣在掌心，《旧市集门口师兄钥匙案》没有改。`
    const slotReplacements = [
      makeMockSlotReplacement('character', '她', character, sourceText, outputText),
      makeMockSlotReplacement('place', '旧市集门口', place, sourceText, outputText),
      makeMockSlotReplacement('honorific', '师兄', honorific, sourceText, outputText),
      makeMockSlotReplacement('plot_object', '钥匙', plotObject, sourceText, outputText),
    ].filter(Boolean)
    return { text: outputText, slotReplacements }
  }

  function readMockSlotValue(slotValues, keys, fallback) {
    for (const key of keys) {
      const value = slotValues?.[key]
      if (typeof value === 'string' && value.trim().length > 0) {
        return value.trim()
      }
    }

    return fallback
  }

  function makeMockSlotReplacement(slotName, sourceValue, replacementValue, sourceText, outputText) {
    const sourceStart = sourceText.indexOf(sourceValue)
    const outputStart = outputText.indexOf(replacementValue)
    if (sourceStart < 0 || outputStart < 0) return null
    return {
      slot_name: slotName,
      source_value: sourceValue,
      replacement_value: replacementValue,
      source_start: sourceStart,
      source_end: sourceStart + sourceValue.length,
      output_start: outputStart,
      output_end: outputStart + replacementValue.length,
    }
  }

  function buildMockCorpusDraftCandidateNextAction(selectedBlueprint, variant) {
    if (variant.replacePieceBlocked !== true) return null

    const transitionId = `mock-transition-${variant.candidate_id}`
    const rejectedNodeId = String(variant.node_id ?? '')
    const replacementNodeId = String(variant.replacement_node_id ?? '')

    return {
      action: 'regenerate_blueprint',
      reason_code: 'transition_replacement_outside_selected_blueprint',
      message: '替代节点不在当前选中蓝图节拍内，请回到共享语料库重新组合蓝图。',
      transition_id: transitionId,
      rejected_piece_id: variant.piece_id,
      rejected_node_id: rejectedNodeId,
      replacement_node_id: replacementNodeId,
      feedback: {
        rejected_blueprint_ids: [selectedBlueprint.blueprint_id],
        rejected_node_ids: rejectedNodeId ? [rejectedNodeId] : [],
        avoid_library_ids: [],
        avoid_anchor_ids: [],
        problem_tags: [
          'transition_replacement_required',
          'transition_replacement_outside_selected_blueprint',
        ],
        notes: `正文候选 ${variant.candidate_id} 的转场要求替换为 ${replacementNodeId || 'unknown'}，但该节点不在选中蓝图节拍内。请重新检索可闭合的蓝图。`,
      },
    }
  }

  function buildMockCorpusInsertionDraft(input, selectedBlueprint, variant) {
    const base = generateReferenceCorpusInsertionDraft({
      ...input,
      selected_blueprint: selectedBlueprint,
    })
    const auditBlocked = variant.auditBlocked === true
    const replacementBlocked = variant.replacePieceBlocked === true
    const transitionBlocked = variant.transitionBlocked === true || replacementBlocked
    const hasTransitionText = typeof variant.transitionText === 'string' && variant.transitionText.length > 0
    const hasTransition = hasTransitionText || replacementBlocked
    const assembledText = hasTransitionText
      ? `${variant.text}\n${variant.transitionText}\n${variant.secondText}`
      : replacementBlocked
        ? `${variant.text}\n${variant.secondText}`
      : variant.text
    const chapterTextAfterInsertion = buildMockCorpusChapterTextAfterInsertion(
      input?.chapter_context ?? {},
      assembledText)
    const blockedViolation = {
      violation_id: `mock-draft-audit-violation-${variant.piece_id}`,
      code: 'preserved_text_hash_mismatch',
      severity: 'error',
      piece_id: variant.piece_id,
      node_id: variant.node_id,
      span_id: `mock-preserved-span-${variant.piece_id}`,
      message: '保留片段 hash 与输出不一致，不能插入。',
      transition_id: null,
    }
    const blockedAuditErrors = [`preserved_text_hash_mismatch:${variant.node_id}:${blockedViolation.span_id}`]
    const transitionId = `mock-transition-${variant.candidate_id}`
    const transitionText = replacementBlocked ? '' : variant.transitionText
    const transitionOutputStart = replacementBlocked ? variant.text.length : variant.text.length + 1
    const transition = hasTransition
      ? {
          transition_id: transitionId,
          gap_id: `mock-transition-gap-${variant.candidate_id}`,
          after_piece_id: variant.piece_id,
          before_piece_id: variant.second_piece_id,
          decision: replacementBlocked ? 'replace_piece' : 'insert_transition',
          strategy: replacementBlocked ? 'replace_piece' : 'bridge_sentence',
          text: transitionText,
          text_hash: `hash-${transitionId}`,
          output_start: transitionOutputStart,
          output_end: transitionOutputStart + transitionText.length,
          approved: !transitionBlocked,
          reason: replacementBlocked
            ? 'mock transition requested a source replacement outside selected blueprint'
            : transitionBlocked ? 'mock transition rejected by audit' : 'mock bridge transition between selected blueprint pieces',
          replacement_piece_id: replacementBlocked ? variant.piece_id : null,
          replacement_node_id: replacementBlocked ? variant.replacement_node_id : null,
        }
      : null
    const transitionViolation = transitionBlocked && transition
      ? {
          violation_id: `mock-draft-audit-violation-${transition.transition_id}`,
          code: replacementBlocked ? 'transition_piece_replacement_required' : 'transition_not_approved',
          severity: 'error',
          piece_id: variant.piece_id,
          node_id: variant.node_id,
          span_id: null,
          message: replacementBlocked
            ? '替代节点不在选中蓝图节拍内，需要回到蓝图重组。'
            : '过渡句未通过 transition resolver 审批，不能插入。',
          transition_id: transition.transition_id,
        }
      : null
    const transitionAuditErrors = transitionViolation
      ? [`${transitionViolation.code}:${variant.node_id}:${transitionId}`]
      : []
    const secondPiece = hasTransition
      ? {
          ...base.pieces[0],
          piece_id: variant.second_piece_id,
          beat_id: variant.beat_id,
          candidate_id: variant.candidate_id,
          node_id: variant.second_node_id,
          output_text: variant.secondText,
          preserved_hash_matches: true,
          preserved_spans: [{
            span_id: `mock-preserved-span-${variant.second_piece_id}`,
            source_start: 0,
            source_end: variant.secondText.length,
            output_start: 0,
            output_end: variant.secondText.length,
            source_text_hash: `hash-preserved-span-${variant.second_piece_id}`,
            output_text_hash: `hash-preserved-span-${variant.second_piece_id}`,
            matches: true,
          }],
          locked_spans: [],
          slot_replacements: [],
        }
      : null
    const firstAuditPiece = {
      ...base.audit.pieces[0],
      piece_id: variant.piece_id,
      node_id: variant.node_id,
      passed: !auditBlocked,
      mismatched_span_count: auditBlocked ? 1 : 0,
      violations: auditBlocked ? [blockedViolation] : [],
    }
    const secondAuditPiece = secondPiece
      ? {
          ...base.audit.pieces[0],
          piece_id: secondPiece.piece_id,
          node_id: secondPiece.node_id,
          passed: true,
          preserved_span_count: 1,
          mismatched_span_count: 0,
          violations: [],
        }
      : null

    return {
      ...base,
      blueprint: selectedBlueprint,
      pieces: [{
        ...base.pieces[0],
        piece_id: variant.piece_id,
        beat_id: variant.beat_id,
        candidate_id: variant.candidate_id,
        node_id: variant.node_id,
        output_text: variant.text,
        preserved_hash_matches: !auditBlocked,
        preserved_spans: [{
          ...base.pieces[0].preserved_spans[0],
          span_id: `mock-preserved-span-${variant.piece_id}`,
          matches: !auditBlocked,
          output_text_hash: auditBlocked ? `hash-mismatch-${variant.piece_id}` : base.pieces[0].preserved_spans[0].output_text_hash,
        }],
        locked_spans: [],
        slot_replacements: variant.slot_replacements ?? base.pieces[0].slot_replacements,
      }, ...(secondPiece ? [secondPiece] : [])],
      slot_replacements: variant.slot_replacements ?? base.slot_replacements,
      transitions: transition ? [transition] : [],
      assembled_text: assembledText,
      chapter_text_after_insertion: (auditBlocked || transitionBlocked)
        ? String(input?.chapter_context?.current_draft_text ?? '')
        : chapterTextAfterInsertion,
      ready_for_insertion: !auditBlocked && !transitionBlocked,
      gate: {
        ...base.gate,
        pieces: [{
          ...base.gate.pieces[0],
          piece_id: variant.piece_id,
          node_id: variant.node_id,
        }, ...(secondPiece ? [{
          ...base.gate.pieces[0],
          piece_id: secondPiece.piece_id,
          node_id: secondPiece.node_id,
        }] : [])],
      },
      audit: {
        ...base.audit,
        passed: !auditBlocked && !transitionBlocked,
        status: (auditBlocked || transitionBlocked) ? 'blocked' : 'passed',
        errors: [
          ...(auditBlocked ? blockedAuditErrors : []),
          ...transitionAuditErrors,
        ],
        pieces: [firstAuditPiece, ...(secondAuditPiece ? [secondAuditPiece] : [])],
        transitions: transition
          ? [{
              transition_id: transition.transition_id,
              gap_id: transition.gap_id,
              after_piece_id: transition.after_piece_id,
              before_piece_id: transition.before_piece_id,
              decision: transition.decision,
              passed: !transitionBlocked,
              violations: transitionViolation ? [transitionViolation] : [],
            }]
          : [],
      },
    }
  }

  function buildMockCorpusChapterTextAfterInsertion(chapterContext, assembledText) {
    const currentDraft = String(chapterContext.current_draft_text ?? '')
    const requestedOffset = Number(chapterContext.insertion_offset ?? currentDraft.length)
    const insertionOffset = Number.isFinite(requestedOffset)
      ? Math.max(0, Math.min(currentDraft.length, requestedOffset))
      : currentDraft.length
    const prefix = currentDraft.length === 0
      ? ''
      : currentDraft.slice(0, insertionOffset).endsWith('\n') ? '\n' : '\n\n'
    return `${currentDraft.slice(0, insertionOffset)}${prefix}${assembledText}${currentDraft.slice(insertionOffset)}`
  }

  function cancelReferenceOrchestrationRun(input = {}) {
    const run = referenceOrchestrationRun(input?.run_id)
    if (!run) throw new Error(`Unknown reference orchestration run ${input?.run_id}`)
    const updated = {
      ...run,
      status: 'cancelled',
      current_decision: null,
      last_stop_reason: 'cancelled',
      error_message: String(input?.reason ?? 'cancelled'),
      updated_at: now,
    }
    state.referenceOrchestrationRuns = state.referenceOrchestrationRuns.map((item) =>
      item.run_id === run.run_id ? updated : item)
    return updated
  }

  function referenceOrchestrationRuns(novelId, chapterNumber) {
    return state.referenceOrchestrationRuns.filter((run) => {
      if (Number(run.novel_id) !== Number(novelId ?? state.activeNovelId)) return false
      if (chapterNumber == null) return true
      return Number(run.chapter_number) === Number(chapterNumber)
    })
  }

  function referenceOrchestrationRun(runId) {
    return state.referenceOrchestrationRuns.find((run) => run.run_id === String(runId ?? '')) ?? null
  }

  function referenceOrchestrationRunEvents(runId) {
    const run = referenceOrchestrationRun(runId)
    if (!run) return []
    return [{
      event_id: 1,
      run_id: run.run_id,
      novel_id: run.novel_id,
      event_type: 'run_started',
      stage: run.stage,
      status: run.status,
      stop_reason: run.last_stop_reason,
      decision_type: run.current_decision?.decision_type ?? '',
      summary: run.current_decision?.summary ?? '参考流程已启动。',
      created_at: run.created_at,
    }]
  }

  function makeReferenceBlueprint(blueprintId, overrides = {}) {
    const beat = makeReferenceBeat()
    return {
      blueprint_id: blueprintId,
      novel_id: 42,
      chapter_number: overrides.chapter_number ?? 1,
      title: overrides.title ?? '10MB 材料绑定验收',
      status: overrides.status ?? 'draft',
      source_plan_scope: 'chapter',
      source_plan_hash: `stress-plan-${blueprintId}`,
      context_hash: `stress-context-${blueprintId}`,
      analysis_contract_hash: `stress-contract-${blueprintId}`,
      blueprint_version: 1,
      build_version: 'mock-stress-blueprint-v1',
      parent_blueprint_id: 0,
      primary_anchor_id: overrides.primary_anchor_id ?? 0,
      chapter_function: '验证大体量参考源可以进入参考写作链路。',
      logic_analysis: referenceTrack('logic', ['从10MB参考源检索材料', '保持事实边界']),
      emotion_analysis: referenceTrack('emotion', ['警觉', '克制']),
      narration_analysis: referenceTrack('narration', ['close POV', '来源可审计细节']),
      character_analysis: referenceTrack('character', ['林岚只记录可见证据']),
      reference_analysis: referenceTrack('reference', ['绑定自动分段材料']),
      transition_plan: referenceTrack('transition', ['从材料搜索转入蓝图节拍']),
      execution_contract: {
        track: 'execution',
        summary: '使用自动分段材料的水痕细节完成候选节拍。',
        paragraph_intentions: [beat.paragraph_intention],
        execution_modes: [beat.execution_mode],
        anti_screenplay_duties: [beat.anti_screenplay_duty],
        source_backed_detail_targets: [beat.source_backed_detail_target],
        candidate_rejection_rules: [beat.candidate_rejection_rule],
      },
      previous_state: '大体量参考源已导入。',
      final_state: '蓝图可绑定来源材料。',
      final_hook: '继续候选段落前仍停在审批边界。',
      global_pov: '林岚',
      global_narrative_distance: 'close',
      known_facts: overrides.known_facts ?? ['只使用10MB参考源可审计材料'],
      forbidden_facts: overrides.forbidden_facts ?? ['未经来源支持的门外身份'],
      risk_flags: [],
      beats: [beat],
      latest_review: overrides.latest_review ?? null,
      created_at: now,
      updated_at: now,
    }
  }

  function makeReferenceBeat() {
    return {
      beat_id: 'stress-beat-001',
      beat_index: 1,
      scene_index: 1,
      beat_type: 'scene',
      narrative_function: '用大体量参考源中的水痕细节承载压力。',
      logic_premise: '材料来自自动分段的10MB参考源。',
      conflict_pressure: '不能越过来源材料推断门外身份。',
      causality_in: '检索到水痕材料。',
      causality_out: '蓝图保留受限视角。',
      transition_in: '材料浏览完成。',
      transition_out: '绑定到节拍。',
      pov_character: '林岚',
      narrative_distance: 'close',
      viewpoint_allowed_knowledge: ['水痕', '灯影', '门缝停顿'],
      viewpoint_forbidden_knowledge: ['门外身份'],
      character_states_before: ['警觉'],
      character_states_after: ['克制'],
      character_goals: ['确认可见证据'],
      character_misbeliefs: ['门外动静可能只是雨声'],
      relationship_pressure: ['不能暴露判断'],
      emotion_trigger: '杯底水痕',
      emotion_before: '警觉',
      emotion_after: '克制',
      suppressed_reaction: '没有立刻抬头',
      external_evidence: '杯底半圈水痕',
      narration_strategy: 'close POV, sensory evidence only.',
      rhythm_strategy: '先停顿，再动作。',
      paragraph_intention: '用10MB参考源材料支撑受限视角细节。',
      execution_mode: 'delayed_reaction',
      anti_screenplay_duty: '避免纯动作走位。',
      sensory_anchor_target: '雨声',
      subtext_plan: '水痕暗示刚有人离开。',
      source_backed_detail_target: '杯底半圈水痕',
      candidate_rejection_rule: '拒绝无来源身份揭示。',
      scene_facts: ['桌上有杯子', '雨声很大'],
      forbidden_facts: ['门外身份'],
      reference_query: {
        query: '10MB 水痕 受限视角',
        material_types: ['sentence', 'passage'],
        emotion_tags: ['restrained'],
        function_tags: ['emotion_evidence'],
        pov_tags: ['close'],
        technique_tags: ['subtext'],
        max_results: 3,
      },
      required_material_types: ['sentence'],
      max_rewrite_level: 'L1',
      slot_plan: [{ slot_name: 'object', value: '杯底水痕' }],
      locked_phrase_policy: '不锁定原句。',
      no_reuse_reason: '',
      prose_duties: ['source_backed_detail', 'subtext'],
      risk_flags: [],
    }
  }

  function referenceTrack(name, points) {
    return {
      track: name,
      summary: `${name} stress summary`,
      points,
    }
  }

  function makeReferenceReview(blueprintId, reviewId) {
    return {
      review_id: reviewId,
      blueprint_id: blueprintId,
      context_hash: `stress-context-${blueprintId}`,
      source_plan_hash: `stress-plan-${blueprintId}`,
      analysis_contract_hash: `stress-contract-${blueprintId}`,
      review_version: 1,
      status: 'passed',
      score: 0.95,
      logic_errors: [],
      causality_errors: [],
      emotion_errors: [],
      narration_errors: [],
      execution_errors: [],
      character_state_errors: [],
      pov_errors: [],
      continuity_errors: [],
      transition_errors: [],
      forbidden_fact_errors: [],
      reference_binding_errors: [],
      material_fit_errors: [],
      screenplay_drift_risks: [],
      ai_prose_risks: [],
      novelistic_narration_errors: [],
      required_fixes: [],
      defects: [],
      reviewed_at: now,
    }
  }

  function cloneReferenceBlueprint(blueprintId) {
    const blueprint = state.referenceBlueprints[String(blueprintId)]
    if (!blueprint) throw new Error(`Unknown reference blueprint ${blueprintId}`)
    return JSON.parse(JSON.stringify(blueprint))
  }

  function cloneJson(value) {
    return JSON.parse(JSON.stringify(value))
  }

  function toReferenceBlueprintSummary(blueprint) {
    return {
      blueprint_id: blueprint.blueprint_id,
      novel_id: blueprint.novel_id,
      chapter_number: blueprint.chapter_number,
      title: blueprint.title,
      status: blueprint.status,
      source_plan_hash: blueprint.source_plan_hash,
      updated_at: blueprint.updated_at,
    }
  }

  function pageResult(items) {
    return {
      items,
      total: items.length,
      page: 1,
      size: Math.max(items.length, 1),
      total_pages: 1,
    }
  }

  function pagedResult(items, page, size, total) {
    return {
      items,
      total,
      page,
      size,
      total_pages: Math.max(1, Math.ceil(total / size)),
    }
  }

  function wait(ms) {
    return new Promise((resolve) => window.setTimeout(resolve, ms))
  }

  function defaultValueFor(method) {
    if (method.startsWith('Get')) return null
    if (method.startsWith('List')) return []
    return null
  }
}

const installConfigurableAppMockBridgeSource = installConfigurableAppMockBridge.toString()
const gitServiceBootstrapSource = `const {
    createDefaultGitMockFixtures,
    getGitCommitFiles,
    getGitCommits,
    getGitFileDiff,
  } = (${createMockGitService.toString()})()`

// Playwright addInitScript serializes this function into the browser context.
installConfigurableAppMockBridge.toString = () =>
  installConfigurableAppMockBridgeSource.replace(') {', `) {\n  ${gitServiceBootstrapSource}`)
