import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import fs from 'node:fs/promises'
import net from 'node:net'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { chromium } from 'playwright'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const frontendRoot = path.resolve(__dirname, '..')
const repoRoot = path.resolve(frontendRoot, '..')
const outputDir = path.join(repoRoot, 'output', 'playwright')

async function main() {
  await fs.mkdir(outputDir, { recursive: true })

  const port = await getFreePort()
  const server = startVite(port)
  const url = `http://127.0.0.1:${port}/`
  const consoleErrors = []
  const pageErrors = []
  let browser

  try {
    logStep('waiting for Vite')
    await waitForServer(url, server)
    browser = await launchBrowser()
    const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } })
    page.setDefaultTimeout(12_000)

    page.on('console', (message) => {
      if (message.type() === 'error') {
        consoleErrors.push(message.text())
      }
    })
    page.on('pageerror', (error) => pageErrors.push(error.message))

    logStep('loading workspace')
    await page.addInitScript(installAppMockBridge)
    await page.goto(url, { waitUntil: 'domcontentloaded' })
    await expectVisible(page.getByText('全局回归小说'), 'workspace title')
    await expectVisible(page.getByText('AI 对话'), 'chat panel')
    await page.screenshot({ path: path.join(outputDir, 'app-01-shell.png'), fullPage: true })

    logStep('checking shell navigation')
    await verifyShellNavigation(page)

    logStep('checking chapter/editor path')
    await verifyChapterWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-02-editor.png'), fullPage: true })

    logStep('checking search path')
    await verifySearchWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-03-search.png'), fullPage: true })

    logStep('checking chat path')
    await verifyChatWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-04-chat.png'), fullPage: true })

    logStep('checking settings path')
    await verifySettingsWorkflow(page)
    await page.screenshot({ path: path.join(outputDir, 'app-05-settings.png'), fullPage: true })

    logStep('checking metadata panels')
    await verifyMetadataPanels(page)
    await page.screenshot({ path: path.join(outputDir, 'app-06-metadata.png'), fullPage: true })

    logStep('checking reference entry point')
    await verifyReferenceSmoke(page)
    await page.screenshot({ path: path.join(outputDir, 'app-07-reference.png'), fullPage: true })

    logStep('checking bridge guardrails')
    await verifyBridgeCalls(page)

    assert.deepEqual(pageErrors, [], `Unexpected page errors:\n${pageErrors.join('\n')}`)
    assert.deepEqual(consoleErrors, [], `Unexpected console errors:\n${consoleErrors.join('\n')}`)
    console.log(`App-wide mock workflow passed. Screenshots: ${path.relative(repoRoot, outputDir)}`)
  } finally {
    await browser?.close()
    stopProcess(server)
  }
}

async function verifyShellNavigation(page) {
  await page.getByTitle('书架').click()
  await expectVisible(page.getByText('共'), 'bookshelf toolbar')
  await expectVisible(page.getByText('全局回归小说').first(), 'bookshelf novel')

  await page.getByTitle('章节').click()
  await expectVisible(page.getByText('章节 (2)'), 'chapter count')
  await expectVisible(page.getByRole('button', { name: /故事状态/ }), 'goink entry')

  await page.locator('header').getByTitle('帮助').click()
  await expectVisible(page.getByText('欢迎使用 Novelist'), 'help dialog')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
}

async function verifyChapterWorkflow(page) {
  await page.getByTitle('章节').click()
  const chapterBlock = page.getByRole('button', { name: /第 1 - 2 章/ })
  if (await chapterBlock.isVisible()) {
    await chapterBlock.click()
  }

  await expectVisible(page.getByRole('button', { name: /雨夜线索/ }), 'first chapter in side panel')
  await page.getByRole('button', { name: /雨夜线索/ }).click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'chapter tab title')
  await waitForBridgeCallArg(page, 'GetContent', 1, 'chapters/1.md')

  await page.getByRole('button', { name: /故事状态/ }).click()
  await expectVisible(page.getByText('故事状态').first(), 'story state tab')
}

async function verifySearchWorkflow(page) {
  await page.getByTitle('搜索').click()
  const searchInput = page.getByPlaceholder('搜索人物、地点、时间线、正文...')
  await searchInput.fill('雨夜')
  await expectVisible(page.getByText('正文匹配 (1)'), 'content search group')
  await expectVisible(page.getByText('人物 (1)'), 'character search group')
  await expectVisible(page.getByText('林岚在').first(), 'content result preview')

  await page.locator('aside').getByRole('button', { name: /雨夜线索/ }).click()
  await expectVisible(page.getByText('第1章 雨夜线索').first(), 'search opened chapter')
}

async function verifyChatWorkflow(page) {
  const input = page.getByPlaceholder('输入消息，按 / 调用技能...')
  await input.fill('检查雨夜线索这一章的约束')
  await input.press('Enter')

  await expectVisible(page.getByText('检查雨夜线索这一章的约束'), 'user chat message')
  await expectVisible(page.getByText('读取章节列表').first(), 'tool card')
  await expectVisible(page.getByText('搜索完成').first(), 'web search card')
  await expectVisible(page.getByText('建议先保留受限视角').first(), 'assistant message')
}

async function verifySettingsWorkflow(page) {
  await page.locator('header').getByTitle('设置').click()
  await expectVisible(page.getByText('设置').first(), 'settings dialog')
  await expectVisible(page.getByText('基础设置'), 'general tab')
  await expectVisible(page.locator('input[value="D:\\\\NovelistData"]'), 'data directory')

  await page.getByRole('button', { name: /模型配置/ }).click()
  await expectVisible(page.getByText('内置服务商'), 'builtin model config')
  await expectVisible(page.getByText('Embeddings'), 'embedding settings tab')
  await page.locator('.fixed').getByRole('button', { name: '✕' }).click()
}

async function verifyMetadataPanels(page) {
  await page.getByTitle('角色').click()
  await expectVisible(page.getByRole('heading', { name: /角色/ }), 'characters heading')
  await expectVisible(page.getByText('林岚').first(), 'character fixture')

  await page.getByTitle('地点').click()
  await expectVisible(page.getByRole('heading', { name: /地点/ }), 'locations heading')
  await expectVisible(page.getByText('旧城门').first(), 'location fixture')

  await page.getByTitle('弧线').click()
  await expectVisible(page.getByRole('heading', { name: /弧线节点/ }), 'story arc heading')
  await expectVisible(page.getByText('雨夜调查线').first(), 'story arc fixture')

  await page.getByTitle('时间线').click()
  await expectVisible(page.getByRole('heading', { name: /章节计划/ }), 'timeline heading')
  await expectVisible(page.getByText('桌面水痕').first(), 'timeline fixture')

  await page.getByTitle('读者视角').click()
  await expectVisible(page.getByRole('heading', { name: /读者视角/ }), 'reader heading')
  await expectVisible(page.getByText(/读者知道林岚正在调查/).first(), 'reader fixture')

  await page.getByTitle('偏好').click()
  await expectVisible(page.getByRole('heading', { name: /创作偏好/ }), 'preference heading')
  await expectVisible(page.getByText(/保持受限视角/).first(), 'preference fixture')

  await page.getByTitle('技能').click()
  await expectVisible(page.getByText('技能 (2)'), 'skills side panel')
  await expectVisible(page.getByText('节奏控制').first(), 'skill fixture')
}

async function verifyReferenceSmoke(page) {
  await page.getByTitle('参考锚定').click()
  await expectVisible(page.getByRole('heading', { name: /参考锚定/ }), 'reference heading')
  await expectVisible(page.getByText('全局雨夜参考').first(), 'reference anchor fixture')
  await expectVisible(page.getByText('默认编排').first(), 'orchestration panel')
}

async function verifyBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
    'GetContent',
    'SearchAll',
    'Chat',
    'GetModels',
    'GetSessions',
    'ListSlashCommands',
    'GetLLMConfig',
    'GetEmbeddingConfig',
    'GetSqliteVecStatus',
    'GetCharacters',
    'GetLocations',
    'GetStoryArcs',
    'GetTimelineEntries',
    'GetReaderPerspectives',
    'GetPreferences',
    'ListSkills',
    'GetReferenceAnchors',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'app-wide smoke must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'app-wide smoke must not open external URLs')
  assert(!methods.includes('PickReferenceSourceFile'), 'app-wide smoke must not open arbitrary file pickers')

  const saveCandidates = methods.filter(method => method.startsWith('Save') || method.startsWith('Update') || method.startsWith('Delete'))
  assert.deepEqual(saveCandidates, [], `Unexpected mutating bridge calls:\n${saveCandidates.join('\n')}`)
}

async function expectVisible(locator, description) {
  await locator.waitFor({ state: 'visible', timeout: 12_000 }).catch((error) => {
    throw new Error(`Expected visible: ${description}`, { cause: error })
  })
}

async function waitForBridgeCallArg(page, method, argIndex, expectedValue) {
  await page.waitForFunction(
    ({ method, argIndex, expectedValue }) => {
      return window.__appMockState.calls.some((call) =>
        call.method === method && call.args[argIndex] === expectedValue)
    },
    { method, argIndex, expectedValue },
    { timeout: 12_000 },
  )
}

function startVite(port) {
  const viteBin = path.join(frontendRoot, 'node_modules', 'vite', 'bin', 'vite.js')

  return spawn(process.execPath, [viteBin, '--host', '127.0.0.1', '--port', String(port)], {
    cwd: frontendRoot,
    env: {
      ...process.env,
      BROWSER: 'none',
      NODE_ENV: 'development',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  })
}

async function launchBrowser() {
  try {
    return await chromium.launch({ headless: true })
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)
    if (process.platform === 'win32' && message.includes("Executable doesn't exist")) {
      logStep('Playwright Chromium is not installed; falling back to Microsoft Edge')
      return chromium.launch({ channel: 'msedge', headless: true })
    }

    throw new Error(
      `${message}\nRun "npx playwright install chromium" from frontend/ if this machine has no browser fallback.`,
      { cause: error },
    )
  }
}

async function waitForServer(url, child) {
  const logs = []
  child.stdout.on('data', (chunk) => logs.push(String(chunk)))
  child.stderr.on('data', (chunk) => logs.push(String(chunk)))

  const startedAt = Date.now()
  while (Date.now() - startedAt < 30_000) {
    if (child.exitCode !== null) {
      throw new Error(`Vite exited before becoming ready:\n${logs.join('')}`)
    }

    try {
      const response = await fetch(url)
      if (response.ok) return
    } catch {
      // keep polling
    }
    await delay(200)
  }

  throw new Error(`Timed out waiting for Vite:\n${logs.join('')}`)
}

function stopProcess(child) {
  if (child.exitCode === null && child.signalCode === null) {
    child.kill()
  }
}

async function getFreePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer()
    server.unref()
    server.on('error', reject)
    server.listen(0, '127.0.0.1', () => {
      const address = server.address()
      server.close(() => {
        if (typeof address === 'object' && address?.port) {
          resolve(address.port)
        } else {
          reject(new Error('Unable to reserve a local port.'))
        }
      })
    })
  })
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

function logStep(message) {
  console.log(`[app mock] ${message}`)
}

function installAppMockBridge() {
  const now = '2026-07-05T12:00:00.000Z'
  const receivers = new Set()
  const state = {
    calls: [],
    nextSessionId: 1,
    nextTurnId: 101,
  }

  window.localStorage.removeItem('goink_tabs_all')
  window.localStorage.setItem('theme', 'light')
  window.confirm = () => false

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
      state.calls.push({ method: envelope.method, args })

      if (envelope.method === 'SaveContent') {
        throw new Error('SaveContent is forbidden in the app-wide smoke unless the test explicitly edits content.')
      }

      const result = await route(envelope.method, args)
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
    respond({ kind: 'event', name, payload })
  }

  async function route(method, args) {
    switch (method) {
      case 'IsInitialized': return true
      case 'GetSettings': return settings()
      case 'GetPlatform': return { os: 'win32', defaultPath: 'D:\\NovelistData' }
      case 'runtime.window.isMaximized': return false
      case 'runtime.window.minimize':
      case 'runtime.window.toggleMaximize':
      case 'runtime.app.quit':
      case 'SetActiveNovel':
      case 'SetLastSession':
      case 'SetSelectedModel':
      case 'SetReasoningEffort':
      case 'SetApprovalMode':
      case 'SetChatPanelWidth':
      case 'CancelChat':
      case 'ApproveTool':
      case 'RebuildNovelIndex':
      case 'TestConnection':
      case 'TestEmbeddingConnection':
        return null
      case 'GetAppConfig': return { data_dir: 'D:\\NovelistData' }
      case 'GetNovels': return [novel()]
      case 'GetCover': return null
      case 'GetChapters': return chapters()
      case 'GetContent': return content(args[1])
      case 'GetModels': return [availableModel()]
      case 'GetSessions': return pageResult([])
      case 'GetSession': return sessionDetail(args[0])
      case 'GetSessionMessages': return []
      case 'ListSlashCommands': return [{ name: 'review', description: '审稿当前章节', type: 'manual' }]
      case 'Chat': return chat(args[0])
      case 'CompressContext': return { turn_id: state.nextTurnId++ }
      case 'SearchAll': return searchAll(args[1])
      case 'GetCharacters': return characters()
      case 'GetCharacterRelations': return []
      case 'GetLocations': return locations()
      case 'GetLocationRelations': return []
      case 'GetStoryArcs': return storyArcs()
      case 'GetArcNodes': return arcNodes()
      case 'GetMaxChapterNumber': return 2
      case 'GetChapterPlans': return chapterPlans()
      case 'GetTimelineEntries': return timelineEntries()
      case 'GetReaderPerspectives': return readerPerspectives()
      case 'GetPreferences': return preferences()
      case 'GetWritingActivity': return writingActivity()
      case 'GetWritingStats': return writingStats()
      case 'ListSkills': return skills()
      case 'GetLLMConfig': return llmConfig()
      case 'GetEmbeddingConfig': return embeddingConfig()
      case 'GetSqliteVecStatus': return sqliteVecStatus()
      case 'GetReferenceAnchors': return referenceAnchors()
      case 'GetReferenceChapterBlueprints': return []
      case 'GetReferenceOrchestrationRuns': return []
      case 'GetReferenceOrchestrationRunEvents': return []
      default:
        return defaultValueFor(method)
    }
  }

  function settings() {
    return {
      ID: 1,
      last_novel_id: 42,
      selected_model_key: 'mock/gpt',
      reasoning_effort: 'high',
      approval_mode: 'manual',
      chat_panel_width: 360,
      last_session_id: '',
      user_name: 'Mock User',
    }
  }

  function novel() {
    return {
      id: 42,
      title: '全局回归小说',
      genre: '悬疑',
      description: 'App-wide Playwright fixture',
      created_at: now,
      updated_at: now,
    }
  }

  function chapters() {
    return [
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
    ]
  }

  function content(filePath) {
    const map = {
      'goink.md': '## 当前状态\n林岚正在调查旧城门。',
      'chapters/1.md': '林岚在雨夜旧宅门前停住。\n\n她看见桌上的水痕。',
      'chapters/2.md': '旧城门下，暗号被雨水冲淡。',
      'skills/rhythm.md': '---\nname: 节奏控制\n---\n保持停顿和动作之间的张力。',
      '/builtin/skills/dialogue.md': '---\nname: 对话潜台词\n---\n用话外之意推动场景。',
    }
    return map[filePath] ?? ''
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
    emit('chat:started', { turn_id: turnId })

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
    ]
  }

  function characters() {
    return [
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
  }

  function locations() {
    return [
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
  }

  function storyArcs() {
    return [
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
  }

  function arcNodes() {
    return [
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
  }

  function chapterPlans() {
    return [
      { novel_id: 42, scope: 'next', content: '下一章继续旧城门调查。' },
      { novel_id: 42, scope: 'near', content: '近期回收桌面水痕。' },
      { novel_id: 42, scope: 'far', content: '远期揭示暗号来源。' },
    ]
  }

  function timelineEntries() {
    return [
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
  }

  function readerPerspectives() {
    return [
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
  }

  function preferences() {
    return {
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
  }

  function writingActivity() {
    return [
      { date: '2026-07-01', words: 800 },
      { date: '2026-07-02', words: 1200 },
    ]
  }

  function writingStats() {
    return {
      total_words: 2180,
      total_days_active: 2,
      current_streak: 2,
      longest_streak: 2,
      total_novels: 1,
      total_chapters: 2,
    }
  }

  function skills() {
    return [
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
      onnx_runtime_path: '',
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
    return [
      {
        anchor_id: 101,
        novel_id: 42,
        title: '全局雨夜参考',
        author: 'Mock Author',
        source_path: 'D:\\books\\rain-reference.md',
        source_kind: 'markdown',
        license_status: 'user_provided',
        source_file_hash: 'hash-anchor-app-001',
        build_version: 'mock-reference-v1',
        status: 'ready',
        created_at: now,
        updated_at: now,
      },
    ]
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

  function wait(ms) {
    return new Promise((resolve) => window.setTimeout(resolve, ms))
  }

  function defaultValueFor(method) {
    if (method.startsWith('Get')) return null
    if (method.startsWith('List')) return []
    return null
  }
}

main().catch((error) => {
  console.error(error)
  process.exitCode = 1
})
