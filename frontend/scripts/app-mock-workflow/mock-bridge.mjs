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
  const defaultReferenceAnchors = [{
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
    user_tags: ['雨夜'],
    source_file_hash: 'hash-anchor-app-001',
    build_version: 'mock-reference-v2',
    status: 'ready',
    created_at: now,
    updated_at: now,
  }]
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
    materializationProfiles: {},
    materializationRuns: [],
    referenceWritingSessions: options.referenceWritingSessions ?? {},
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
      case 'PickReferenceSourceFile': return options.pickedReferenceSourceFile ?? null
      case 'RegisterReferenceMaterializationSource': return registerReferenceMaterializationSource(args[0])
      case 'DeleteReferenceAnchor':
        deleteReferenceAnchor(args[0], args[1])
        return null
      case 'DeleteReferenceAnchors':
        deleteReferenceAnchors(args[0])
        return null
      case 'UpdateReferenceAnchorMetadata': return updateReferenceAnchorMetadata(args[0])
      case 'AnalyzeReferenceChapterSplit': return analyzeReferenceChapterSplit(args[0])
      case 'PreviewReferenceChapterSplit': return previewReferenceChapterSplit(args[0])
      case 'ConfirmReferenceChapterSplit': return confirmReferenceChapterSplit(args[0])
      case 'EnqueueReferenceMaterialization': return enqueueReferenceMaterialization(args[0])
      case 'RunReferenceMaterializationChapter': return runReferenceMaterializationChapter(args[0])
      case 'GetReferenceMaterializationStatus': return getReferenceMaterializationStatus(args[0])
      case 'ListReferenceMaterializationChapterProgress': return listReferenceMaterializationChapterProgress(args[0])
      case 'ListReferenceMaterializationChapterMaterials': return listReferenceMaterializationChapterMaterials(args[0])
      case 'ListReferenceMaterials': return listReferenceMaterials(args[0])
      case 'GenerateReferenceMaterializationBlueprintPreview': return generateReferenceMaterializationBlueprintPreview(args[0])
      case 'GenerateReferenceBlueprints': return generateReferenceBlueprints(args[0])
      case 'GetReferenceWritingSession': return getReferenceWritingSession(args[0])
      case 'SelectReferenceBlueprint': return selectReferenceBlueprint(args[0])
      case 'GenerateReferenceDraftCandidates': return generateReferenceDraftCandidates(args[0])
      case 'SearchReferenceMaterials': return searchReferenceMaterials(args[0])
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

  function referenceAnchors() {
 return [...state.referenceAnchors, ...state.createdReferenceAnchors]
 .filter((anchor) => anchor.owner_scope !== 'workspace_corpus' || anchor.visibility === 'workspace')
 .map(sanitizeReferenceAnchor)
 }

 function sanitizeReferenceAnchor(anchor) {
 return { ...anchor, source_path: '' }
 }

 function registerReferenceMaterializationSource(input = {}) {
 const sourcePath = String(input.source_path ?? '').trim()
 if (!sourcePath) throw new Error('A reference source path is required.')

 const duplicate = [...state.referenceAnchors, ...state.createdReferenceAnchors]
 .find((anchor) => anchor.source_path === sourcePath && anchor.novel_id === Number(input.novel_id ?? state.activeNovelId))
 if (duplicate) return sanitizeReferenceAnchor(duplicate)

 const isWorkspace = input.visibility === 'workspace'
 const anchor = {
 anchor_id: 200 + state.createdReferenceAnchors.length,
 novel_id: Number(input.novel_id ?? state.activeNovelId),
 title: String(input.title ?? '').trim(),
 author: String(input.author ?? ''),
 source_path: sourcePath,
 source_kind: String(input.source_kind ?? 'markdown'),
 license_status: String(input.license_status ?? 'user_provided'),
 visibility: isWorkspace ? 'workspace' : 'private',
 source_trust: String(input.source_trust ?? 'user_verified'),
 owner_scope: isWorkspace ? 'workspace_corpus' : 'novel',
 owner_novel_id: isWorkspace ? null : Number(input.novel_id ?? state.activeNovelId),
 user_tags: Array.isArray(input.user_tags) ? input.user_tags : [],
 source_file_hash: `hash-created-${state.createdReferenceAnchors.length}`,
 build_version: 'mock-reference-v2',
 status: 'pending_split',
 created_at: now,
 updated_at: now,
 }
 state.createdReferenceAnchors.push(anchor)
 return sanitizeReferenceAnchor(anchor)
 }

 function deleteReferenceAnchor(novelId, anchorId) {
 const id = Number(anchorId)
 if (!Number.isInteger(id) || id <= 0) throw new Error('Reference source id must be a positive integer.')
 const archiveWorkspaceAnchor = (anchor) => anchor.anchor_id === id && anchor.owner_scope === 'workspace_corpus'
 ? { ...anchor, visibility: 'restricted', updated_at: now }
 : anchor
 const isWorkspaceAnchor = [...state.referenceAnchors, ...state.createdReferenceAnchors]
 .some((anchor) => anchor.anchor_id === id && anchor.owner_scope === 'workspace_corpus')
 if (isWorkspaceAnchor) {
 state.referenceAnchors = state.referenceAnchors.map(archiveWorkspaceAnchor)
 state.createdReferenceAnchors = state.createdReferenceAnchors.map(archiveWorkspaceAnchor)
 return
 }
 state.referenceAnchors = state.referenceAnchors.filter((anchor) => anchor.anchor_id !== id)
 state.createdReferenceAnchors = state.createdReferenceAnchors.filter((anchor) => anchor.anchor_id !== id)
 state.materializationRuns = state.materializationRuns.filter((run) => run.anchor_id !== id)
 }

 function deleteReferenceAnchors(input = {}) {
 for (const anchorId of input.anchor_ids ?? []) deleteReferenceAnchor(input.novel_id, anchorId)
 }

 function updateReferenceAnchorMetadata(input = {}) {
 const anchorId = Number(input.anchor_id)
 let updated = null
 const update = (anchor) => {
 if (anchor.anchor_id !== anchorId) return anchor
 updated = {
 ...anchor,
 title: String(input.title ?? '').trim(),
 author: String(input.author ?? ''),
 license_status: String(input.license_status ?? anchor.license_status),
 visibility: String(input.visibility ?? anchor.visibility),
 source_trust: String(input.source_trust ?? anchor.source_trust),
 user_tags: Array.isArray(input.user_tags) ? input.user_tags : anchor.user_tags,
 updated_at: now,
 }
 return updated
 }
 state.referenceAnchors = state.referenceAnchors.map(update)
 state.createdReferenceAnchors = state.createdReferenceAnchors.map(update)
 if (!updated) throw new Error('Reference source was not found.')
 return sanitizeReferenceAnchor(updated)
 }

 function createChapterSplitProfile(input = {}, mode = 'auto') {
 const anchorId = Number(input.anchor_id ?? 0)
 const profile = {
 split_profile_id: `mock-split-${anchorId}-${mode}`,
 anchor_id: anchorId,
 source_hash: `mock-source-${anchorId}`,
 split_mode: mode,
 pattern_kind: 'chapter_template',
 delimiter_template: mode === 'manual' ? String(input.delimiter_template ?? '') : '第{number}章 {title}',
 sample_char_count: mode === 'auto' ? 50_000 : 0,
 status: 'validated',
 chapter_count: 3,
 boundaries: [
 { chapter_index: 1, title: '雨夜来信', heading_start: 0, content_start: 8, content_end: 960, text_hash: `mock-${anchorId}-1` },
 { chapter_index: 2, title: '钟楼回声', heading_start: 961, content_start: 970, content_end: 1930, text_hash: `mock-${anchorId}-2` },
 { chapter_index: 3, title: '未读的名字', heading_start: 1931, content_start: 1940, content_end: 2880, text_hash: `mock-${anchorId}-3` },
 ],
 model_provider: mode === 'auto' ? 'deepseek' : null,
 model_id: mode === 'auto' ? 'deepseek-v4-pro' : null,
 confidence: mode === 'auto' ? 0.96 : null,
 }
 state.materializationProfiles[String(anchorId)] = profile
 return profile
 }

 function analyzeReferenceChapterSplit(input) {
 return createChapterSplitProfile(input, 'auto')
 }

 function previewReferenceChapterSplit(input) {
 if (!String(input.delimiter_template ?? '').trim()) throw new Error('A chapter delimiter template is required.')
 return createChapterSplitProfile(input, 'manual')
 }

 function confirmReferenceChapterSplit(input) {
 const profile = state.materializationProfiles[String(input.anchor_id ?? '')]
 if (!profile || profile.split_profile_id !== input.split_profile_id) throw new Error('Chapter split profile was not found.')
 profile.status = 'confirmed'
 return { ...profile }
 }

 function materializationStatus(anchorId, profile) {
 const materialCount = profile.chapter_count * 2
 return {
 run_id: `mock-materialization-${anchorId}`,
 anchor_id: anchorId,
 split_profile_id: profile.split_profile_id,
 generation_id: `mock-generation-${anchorId}`,
 status: 'completed',
 total_chapters: profile.chapter_count,
 processed_chapters: profile.chapter_count,
 current_chapter_index: null,
 material_count: materialCount,
 vector_count: materialCount,
 model_call_count: profile.chapter_count,
 llm: { provider: 'deepseek', model_id: 'deepseek-v4-pro', dimensions: null },
 embedding: { provider: 'onnx', model_id: 'bge-m3', dimensions: 1024 },
 last_error_code: null,
 last_error_message: null,
 started_at: now,
 completed_at: now,
 vector_index_healthy: true,
 }
 }

 function enqueueReferenceMaterialization(input) {
 const anchorId = Number(input.anchor_id ?? 0)
 const profile = state.materializationProfiles[String(anchorId)]
 if (!profile || profile.status !== 'confirmed' || profile.split_profile_id !== input.split_profile_id) {
 throw new Error('A confirmed chapter split profile is required.')
 }
 const requestedRunId = String(input.run_id ?? '')
 if (requestedRunId) {
 const existing = state.materializationRuns.find((item) => item.run_id === requestedRunId && item.anchor_id === anchorId)
 if (!existing) throw new Error('Materialization run was not found.')
 Object.assign(existing, materializationStatus(anchorId, profile), {
 run_id: existing.run_id,
 generation_id: existing.generation_id,
 })
 return { ...existing }
 }
 const run = materializationStatus(anchorId, profile)
 state.materializationRuns = state.materializationRuns.filter((item) => item.anchor_id !== anchorId)
 state.materializationRuns.push(run)
 return { ...run }
 }

 function runReferenceMaterializationChapter(input) {
 const anchorId = Number(input.anchor_id ?? 0)
 const runId = String(input.run_id ?? '')
 const chapterIndex = Number(input.chapter_index ?? 0)
 const run = state.materializationRuns.find((item) => item.run_id === runId && item.anchor_id === anchorId)
 if (!run || chapterIndex <= 0 || chapterIndex > run.total_chapters) {
 throw new Error('Materialization chapter was not found.')
 }
 const profile = state.materializationProfiles[String(anchorId)]
 Object.assign(run, materializationStatus(anchorId, profile), {
 run_id: run.run_id,
 generation_id: run.generation_id,
 })
 return { ...run }
 }

 function getReferenceMaterializationStatus(input = {}) {
 const anchorId = Number(input.anchor_id ?? 0)
 const runId = String(input.run_id ?? '')
 const runs = state.materializationRuns.filter((run) => run.anchor_id === anchorId)
 const run = runId ? runs.find((candidate) => candidate.run_id === runId) : runs.at(-1)
 return run ? { ...run } : null
 }

 function listReferenceMaterializationChapterProgress(input = {}) {
 const run = getReferenceMaterializationStatus(input)
 const page = Math.max(1, Number(input.page ?? 1))
 const size = Math.max(1, Number(input.size ?? 20))
 if (!run) return pagedResult([], page, size, 0)
 const items = Array.from({ length: run.total_chapters }, (_, index) => {
 const chapterIndex = index + 1
 const failed = run.status === 'failed' && run.current_chapter_index === chapterIndex
 const pending = run.status !== 'completed' && chapterIndex > (run.current_chapter_index ?? run.total_chapters)
 const status = failed ? 'failed' : pending ? 'pending' : 'completed'
 const completed = status === 'completed'
 return {
 chapter_index: chapterIndex,
 status,
 material_count: completed ? 2 : 0,
 vector_count: completed ? 2 : 0,
 model_call_count: status === 'pending' ? 0 : 1,
 started_at: status === 'pending' ? null : now,
 completed_at: completed ? now : null,
 last_error_code: failed ? run.last_error_code : null,
 last_error_message: failed ? run.last_error_message : null,
 }
 })
 return pagedResult(items.slice((page - 1) * size, page * size), page, size, items.length)
 }

 function materializationRunMaterials(run) {
 const snippets = [
  ['动作', '她把杯底半圈水痕压进记忆里。\n\n她没有回答，目光越过他落在雨幕里。', '用克制反应承接跨段对话并保留线索压力。', ['信息揭示', '压力积累']],
  ['对话', '“你认得留下它的人。”\n\n对面的人停了两息，答非所问地提起旧门的锁。', '让对话中的回避成为下一步推断的依据。', ['悬念', '人物塑造']],
  ]
     return Array.from({ length: run.total_chapters }, (_, chapterOffset) => snippets.map(([sourceKind, text, reuseHint, narrativeFunctions], ordinal) => ({
 material_id: `mock-active-material-${run.anchor_id}-${chapterOffset + 1}-${ordinal + 1}`,
 generation_id: run.generation_id,
 anchor_id: run.anchor_id,
 chapter_index: chapterOffset + 1,
 ordinal,
  metadata: {
  source_span: { start_line: ordinal + 1, end_line: ordinal + 3 },
  source_kind: sourceKind,
  entities: ordinal === 0 ? [{ name: '林岚', kind: '人物' }] : [{ name: '旧门', kind: '物件' }],
  setting: { location: '雨夜的旧宅门前', time: '深夜', environment: '雨声压住窗沿' },
  perspective: ordinal === 0 ? { mode: '限知', focus_entity: '林岚' } : { mode: '客观', focus_entity: null },
  event: ordinal === 0 ? '她注意到杯底水痕，却没有回应追问。' : '对话者回避问题，提起旧门的锁。',
  facts: ordinal === 0 ? [{ subject: '林岚', content: '她注意到杯底水痕。' }] : [{ subject: '对话者', content: '他回避了身份问题。' }],
  causality: ordinal === 0 ? { cause: '杯底水痕触发警觉', consequence: '林岚保持沉默' } : { cause: '身份问题被提出', consequence: '对话者转而谈及旧门锁' },
  state_changes: ordinal === 0 ? [{ subject: '林岚', before: '平静观察', after: '保持戒备' }] : [{ subject: '双方关系', before: '试探', after: '猜疑加深' }],
  character_dynamics: ordinal === 0 ? '林岚以沉默保持戒备。' : '双方试探加深，信任尚未建立。',
  conflict: ordinal === 0 ? { pressure: '线索暴露与回避反应形成压力。', cost: '林岚不能贸然追问。' } : { pressure: '关键身份被回避。', cost: '真相仍受阻。' },
  information: ordinal === 0 ? { role: '揭示', content: '水痕提示有人刚刚来过。' } : { role: '隐藏', content: '对话者知晓留下物件的人。' },
  emotion: ordinal === 0 ? { tone: '克制', subtext: '她已察觉异常但不愿暴露。' } : { tone: '神秘', subtext: '回避本身暴露了秘密。' },
  narrative_functions: narrativeFunctions,
  foreshadowing: ordinal === 0 ? [{ phase: '埋设', target: '水痕来源将被追查' }] : [{ phase: '强化', target: '旧门锁关联旧案' }],
  motifs: ordinal === 0 ? ['雨幕', '水痕'] : ['旧门', '锁'],
  expression_techniques: ordinal === 0 ? ['动作替代解释', '环境烘托'] : ['对白留白', '信息延迟'],
  reuse_hint: reuseHint,
  },
       text: chapterOffset === 0 ? text : `${text}\n\n第${chapterOffset + 1}章的线索仍未揭示。`,
 text_hash: `mock-text-hash-${run.anchor_id}-${chapterOffset + 1}-${ordinal + 1}`,
 }))).flat()
 }

 function activeMaterials(anchorId) {
 const run = getReferenceMaterializationStatus({ anchor_id: anchorId })
 if (run?.status !== 'completed' || !run.vector_index_healthy) return []
 return materializationRunMaterials(run)
 }

 function listReferenceMaterializationChapterMaterials(input = {}) {
 const run = getReferenceMaterializationStatus(input)
 const chapterIndex = Number(input.chapter_index ?? 0)
 const page = Math.max(1, Number(input.page ?? 1))
 const size = Math.max(1, Number(input.size ?? 20))
 if (!run || !Number.isInteger(chapterIndex) || chapterIndex <= 0 || chapterIndex > run.total_chapters) {
 throw new Error('Materialization chapter was not found.')
 }
 const materials = materializationRunMaterials(run)
 .filter((material) => material.chapter_index === chapterIndex)
 return pagedResult(materials.slice((page - 1) * size, page * size), page, size, materials.length)
 }

 function listReferenceMaterials(input = {}) {
 const page = Math.max(1, Number(input.page ?? 1))
 const size = Math.max(1, Number(input.size ?? 20))
 const materials = activeMaterials(Number(input.anchor_id ?? 0))
 return pagedResult(materials.slice((page - 1) * size, page * size), page, size, materials.length)
 }

 function searchReferenceMaterials(input = {}) {
 const requestedIds = Array.isArray(input.anchor_ids) ? input.anchor_ids.map(Number).filter(Number.isInteger) : []
 const anchorIds = requestedIds.length > 0
 ? requestedIds
 : state.materializationRuns.map((run) => run.anchor_id)
 const query = String(input.query ?? '').trim().toLowerCase()
  const matches = anchorIds.flatMap(activeMaterials).filter((material) => !query || [material.text, material.metadata.source_kind, material.metadata.reuse_hint, material.metadata.event, material.metadata.character_dynamics, material.metadata.conflict?.pressure, material.metadata.conflict?.cost, material.metadata.information?.role, material.metadata.information?.content, material.metadata.emotion?.tone, material.metadata.emotion?.subtext, ...material.metadata.narrative_functions, ...material.metadata.motifs, ...material.metadata.expression_techniques, ...material.metadata.facts.map((fact) => fact.content), ...material.metadata.entities.map((entity) => entity.name)]
 .join(' ').toLowerCase().includes(query))
 const maxResults = Math.max(1, Number(input.max_results ?? 12))
 return matches.slice(0, maxResults).map((material, index) => ({ ...material, vector_distance: 0.08 + index / 100 }))
 }

 function generateReferenceMaterializationBlueprintPreview(input = {}) {
 const anchorIds = Array.isArray(input.anchor_ids) ? input.anchor_ids.map(Number).filter(Number.isInteger) : []
 if (anchorIds.length === 0) throw new Error('Active materials are required.')
 const sources = anchorIds.map((anchorId) => {
 const run = getReferenceMaterializationStatus({ anchor_id: anchorId })
 const materials = activeMaterials(anchorId)
 if (!run || materials.length === 0) throw new Error('Active vector index is required.')
 return { anchor_id: anchorId, generation_id: run.generation_id, material_count: materials.length }
 })
 const materials = anchorIds.flatMap(activeMaterials)
 const count = Math.max(1, Math.min(3, Number(input.requested_count ?? 1)))
 const materialLink = (material, explanation) => ({ ...material, vector_distance: 0.08, fit_explanation: explanation })
 return {
 goal: String(input.goal ?? ''),
 sources,
 candidates: Array.from({ length: count }, (_, index) => ({
 blueprint_id: `mock-materialization-blueprint-${index + 1}`,
 strategy: index === 0 ? '先确认水痕线索，再延迟揭示人物的真实动机。' : '将线索压力前置，用反应差异制造下一章冲突。',
 beats: [
  { beat_id: `mock-beat-${index}-1`, beat_index: 1, intent: '让主角发现线索与旧案的关联。', narrative_function: '信息揭示', materials: [materialLink(materials[0], '用克制反应保留判断空间。')] },
  { beat_id: `mock-beat-${index}-2`, beat_index: 2, intent: '在结尾抛出新的不确定性。', narrative_function: '钩子', materials: [materialLink(materials[1], '以延迟动作建立未解压力。')] },
 ],
 })),
 }
 }
 function referenceWritingSessionKey(input = {}) {
    return [
      Number(input.novel_id ?? 0),
      Number(input.chapter_number ?? 0),
      String(input.session_id ?? ''),
    ].join(':')
  }

  function generateReferenceBlueprints(input = {}) {
    const goal = String(input.goal ?? '').trim()
    if (!goal) throw new Error('Reference writing goal is required.')

    const blueprints = ['progressive', 'contrast', 'focused'].map((strategy, blueprintIndex) => ({
      blueprint_id: `mock-writing-blueprint-${blueprintIndex + 1}`,
      strategy,
      beats: [0, 1].map((beatIndex) => ({
        beat_id: `mock-writing-beat-${blueprintIndex + 1}-${beatIndex + 1}`,
        beat_index: beatIndex,
        intent: beatIndex === 0 ? '用追问压缩回避空间。' : '让回答暴露熟人线索并保留身份悬念。',
        narrative_function: beatIndex === 0 ? 'raise_pressure' : 'withhold_answer',
        materials: [0, 1].map((materialIndex) => ({
          material_id: `mock-material-${blueprintIndex + 1}-${beatIndex + 1}-${materialIndex + 1}`,
          generation_id: `mock-generation-${blueprintIndex + 1}`,
        })),
      })),
    }))
    const session = {
      session_id: String(input.session_id ?? ''),
      novel_id: Number(input.novel_id ?? 0),
      chapter_number: Number(input.chapter_number ?? 0),
      goal,
      blueprints,
      selected_blueprint_id: '',
      updated_at: now,
    }
    state.referenceWritingSessions[referenceWritingSessionKey(input)] = session
    return cloneJson(session)
  }

  function getReferenceWritingSession(input = {}) {
    const session = state.referenceWritingSessions[referenceWritingSessionKey(input)]
    return session ? cloneJson(session) : null
  }

  function selectReferenceBlueprint(input = {}) {
    const key = referenceWritingSessionKey(input)
    const session = state.referenceWritingSessions[key]
    if (!session) throw new Error('Reference writing session does not exist.')
    const blueprintId = String(input.blueprint_id ?? '')
    if (!session.blueprints.some((blueprint) => blueprint.blueprint_id === blueprintId)) {
      throw new Error('Selected reference blueprint does not belong to this session.')
    }

    const selected = {
      ...session,
      selected_blueprint_id: blueprintId,
      updated_at: now,
    }
    state.referenceWritingSessions[key] = selected
    return cloneJson(selected)
  }

  function generateReferenceDraftCandidates(input = {}) {
    const session = state.referenceWritingSessions[referenceWritingSessionKey(input)]
    const blueprintId = String(input.blueprint_id ?? '')
    if (!session || session.selected_blueprint_id !== blueprintId) {
      throw new Error('Select this reference blueprint before generating draft candidates.')
    }

    const blueprint = session.blueprints.find((candidate) => candidate.blueprint_id === blueprintId)
    if (!blueprint) throw new Error('The selected reference blueprint no longer belongs to this session.')
    const texts = [
      '雨声在窗沿上连成一线，林岚没有催促，只把杯底的水痕推到灯下。\n\n“你认得留下它的人。”\n\n对面的人停了两息，答非所问地提起旧门的锁。那点迟疑已经足够。\n\n【候选一完整正文末尾】',
      '林岚把话停在最窄的地方，让屋里的沉默自己往下压。\n\n“杯底朝向门口。只有熟人才会坐那个位置，对吗？”\n\n对方看向门外，却没有否认。她于是收起追问，把尚未露面的名字留在雨声里。\n\n【候选二完整正文末尾】',
    ]
    const currentDraft = String(input.current_draft_text ?? '')
    const insertionOffset = Math.max(0, Math.min(currentDraft.length, Number(input.insertion_offset ?? 0)))
    const sources = blueprint.beats.map((beat, beatIndex) => ({
      beat_id: beat.beat_id,
      material_id: beat.materials[0].material_id,
      generation_id: beat.materials[0].generation_id,
      anchor_id: 101 + beatIndex,
      chapter_index: 1 + beatIndex,
      text_hash: `mock-writing-hash-${beatIndex + 1}`,
      license_state: 'authorized',
      reuse_policy: 'verbatim_ok',
    }))
    return {
      session_id: session.session_id,
      blueprint_id: blueprintId,
      candidates: texts.map((text, index) => ({
        candidate_id: `mock-writing-draft-${index + 1}`,
        blueprint_id: blueprintId,
        text,
        chapter_text_after_insertion: currentDraft.slice(0, insertionOffset) + text + currentDraft.slice(insertionOffset),
        sources,
        audit: { passed: true, errors: [] },
      })),
    }
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
