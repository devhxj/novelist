import assert from 'node:assert/strict'

export async function verifyBridgeCalls(page) {
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
    'GetWritingActivity',
    'GetWritingStats',
    'ListSkills',
    'GetReferenceAnchors',
    // 风格素材 / 叙事模式面板已在轻量化聚焦 Phase 1 断入口退役，对应 bridge 调用不再属于全量冒烟必达集合。
    'GetGitCommits',
    'GetGitCommitFiles',
    'GetGitFileDiff',
    // SaveContent（skills/ 路径）此前由叙事模式抽取工作流产生，面板退役后默认路径无触发源，不再列为必达。
    'CancelChat',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected bridge method ${method} to be called.`)
  }

  const chapterSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('chapters/'))
  assert.deepEqual(chapterSaves, [], 'app-wide smoke must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'app-wide smoke must not open external URLs')
  assert(!methods.includes('PickReferenceSourceFile'), 'app-wide smoke must not open arbitrary file pickers')

  const saveCandidates = calls.filter((call) =>
    (call.method.startsWith('Save') || call.method.startsWith('Update') || call.method.startsWith('Delete')) &&
    !isAllowedSurfaceMutation(call))
  assert.deepEqual(
    saveCandidates.map((call) => `${call.method}:${JSON.stringify(call.args)}`),
    [],
    `Unexpected mutating bridge calls:\n${saveCandidates.map((call) => call.method).join('\n')}`)
  await assertGitHistoryReadOnlyCalls(page)
}

function isAllowedSurfaceMutation(call) {
  if (call.method === 'UpdateStyleSample' || call.method === 'DeleteStyleSample') {
    return true
  }

  // 章节计划是用户级表面数据（时间线视图 / 语料覆盖度信号都会写），允许显式工作流变更。
  if (call.method === 'UpdateChapterPlan') {
    return true
  }

  if (call.method === 'SaveContent') {
    const path = String(call.args?.[0]?.path ?? '')
    return path.startsWith('skills/') || path.startsWith('~/.novelist/skills/')
  }

  return false
}

export async function verifyStartupBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)

  assert(methods.includes('IsInitialized'), 'startup workflow must check initialization state')
  assert(methods.includes('GetAppConfig'), 'startup workflow must load startup recovery status')
  assert(methods.includes('GetSettings'), 'startup workflow must load settings after successful initialization')
  assert(!methods.includes('SaveContent'), 'startup workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'startup workflow must not open external URLs')
}

export async function verifyDiagnosticsBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)

  assert(methods.includes('IsInitialized'), 'diagnostics workflow must load the app before probing fixtures')
  assert(!methods.includes('SaveContent'), 'diagnostics workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'diagnostics workflow must not open external URLs')
}

export async function verifyWritingBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetContent', 'GetChapterCorpusCoverage']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected writing bridge method ${method} to be called.`)
  }

  const chapterChat = calls.find((call) => call.method === 'Chat' && call.args?.[0]?.chapter_number)
  assert(chapterChat, 'writing workflow must send chapter-scoped chat for corpus injection')
  assert(!methods.includes('SaveContent'), 'writing workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'writing workflow must not open external URLs')
}

export async function verifyReferenceBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetReferenceAnchors']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected reference bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'reference entry workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'reference entry workflow must not open external URLs')
}

export async function verifyReferenceWorkspaceBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetReferenceAnchors',
    'GetReferenceMaterializationStatus',
    'AnalyzeReferenceChapterSplit',
    'ConfirmReferenceChapterSplit',
    'EnqueueReferenceMaterialization',
    'ListReferenceMaterializationChapterProgress',
    'ListReferenceMaterializationCandidates',
    'RegisterReferenceMaterializationSource',
    'DeleteReferenceAnchor',
    'ListReferenceCorpusFeatureObservations',
    'ListReferenceCorpusTechniqueSpecimens',
    'GetReferenceMaterialCoverage',
    'GetReferenceCorpusAssetTotals',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected reference workspace bridge method ${method} to be called.`)
  }

  assert(!methods.includes('GenerateReferenceMaterializationBlueprintPreview'), 'blueprint preview is retired with the assembly line')
  assert(!methods.includes('SaveContent'), 'reference workspace must not write chapter content')
  assert(!methods.includes('runtime.shell.openExternal'), 'reference workspace must not open external URLs')
}


function assertReferenceAnchorResultsArePathFree(calls) {
  const anchorResults = calls
    .filter((call) => call.method === 'GetReferenceAnchors')
    .flatMap((call) => Array.isArray(call.result) ? call.result : [])
  const createResultAnchors = calls
    .filter((call) => call.method === 'CreateReferenceAnchorsWithResult')
    .flatMap((call) => Array.isArray(call.result?.succeeded) ? call.result.succeeded : [])
  const createResultFailures = calls
    .filter((call) => call.method === 'CreateReferenceAnchorsWithResult')
    .flatMap((call) => Array.isArray(call.result?.failed) ? call.result.failed : [])

  assert(anchorResults.length + createResultAnchors.length > 0, 'reference anchor calls must return at least one anchor fixture')
  for (const anchor of [...anchorResults, ...createResultAnchors]) {
    assert.equal(anchor.source_path ?? '', '', 'reference anchor bridge results must not expose local source_path values')
    assert(!JSON.stringify(anchor).includes('D:\\books'), 'reference anchor bridge results must not include local filesystem paths')
  }
  for (const failure of createResultFailures) {
    assert(!('source_path' in failure), 'reference anchor partial failure results must not expose source_path')
    assert(!JSON.stringify(failure).includes('D:\\books'), 'reference anchor partial failure results must not include local filesystem paths')
  }
}

export async function verifyChapterReferenceBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetContent']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected chapter reference bridge method ${method} to be called.`)
  }

  // 章节参考抽屉已随拼装线退役：编辑器表面不得再触发任何拼装线 bridge 方法。
  const retiredMethods = [
    'SearchReferenceMaterials',
    'GetReferenceOrchestrationRuns',
    'StartReferenceOrchestrationRun',
    'ResumeReferenceOrchestrationRun',
    'CancelReferenceOrchestrationRun',
    'GetReferenceCorpusBlueprintSession',
    'AdvanceReferenceCorpusBlueprintSession',
    'GenerateReferenceCorpusBlueprintCandidates',
    'GenerateReferenceCorpusInsertionDraftCandidates',
    'RecordReferenceCorpusInsertionAudit',
    'GetReferenceDraftCandidates',
    'GetReferenceAnchoredDraftAudits',
  ]
  const unexpected = methods.filter((method) => retiredMethods.includes(method))
  assert.deepEqual(unexpected, [], `chapter editor must not invoke retired assembly bridge methods, got ${unexpected.join(', ')}`)
}

export async function verifyRelativeTimeBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetSessions']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected relative-time workflow bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'relative-time workflow must not save chapter content')
  assert(!methods.includes('runtime.shell.openExternal'), 'relative-time workflow must not open external URLs')
  assert(!methods.includes('PickNovelImportFile'), 'relative-time workflow must not open file pickers')
}

export async function verifyLayoutBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetLayoutSettings',
    'SaveLayoutSettings',
    'GetWindowSettings',
    'SaveWindowSettings',
    'runtime.window.toggleMaximize',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected layout workflow bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SetChatPanelWidth'), 'layout workflow must use SaveLayoutSettings instead of the retired chat-width setter')
  assert(!methods.includes('SaveContent'), 'layout workflow must not save chapter content')
  assert(!methods.includes('runtime.shell.openExternal'), 'layout workflow must not open external URLs')
  assert(!methods.includes('PickNovelImportFile'), 'layout workflow must not open file pickers')
}

export async function verifyErrorBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
    'CreateNovel',
    'UpdateNovel',
    'DeleteNovel',
    'GetCharacters',
    'DeleteCharacter',
    'GetLocations',
    'DeleteLocation',
    'ListSkills',
    'DeleteSkill',
    'UpdateChapterTitle',
    'StartNovelImport',
    'GetModels',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected error workflow bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'error workflow must not save chapter content')
  assert(!methods.includes('runtime.shell.openExternal'), 'error workflow must not open external URLs')
  assert(!methods.includes('PickNovelImportFile'), 'error workflow must not open file pickers')
}

export async function verifyGitBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetGitCommits', 'GetGitCommitFiles', 'GetGitFileDiff']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected Git history bridge method ${method} to be called.`)
  }

  const pagedCall = calls.find((call) =>
    call.method === 'GetGitCommits' &&
    call.args?.[0]?.cursor_commit_id === 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
  assert(pagedCall, 'Git history bridge calls must include cursor-based paging')
  await assertGitHistoryReadOnlyCalls(page)
}

export async function verifyUpdateBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetUpdateCheckSettings',
    'CheckForUpdates',
    'SaveUpdateCheckSettings',
    'runtime.shell.openExternal',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected update workflow bridge method ${method} to be called.`)
  }

  const opened = calls.filter((call) => call.method === 'runtime.shell.openExternal')
  assert.equal(opened.length, 1, 'update workflow must open exactly one external URL after explicit user action')
  assert.equal(opened[0].payload?.url, 'https://updates.example.test/releases/v2.0.0')
  assert(!methods.includes('SaveContent'), 'update workflow must not save chapter content')
  assert(!methods.includes('PickNovelImportFile'), 'update workflow must not open file pickers')
  assert(!methods.includes('GetGitCommits'), 'update workflow must not load Git history')
  assert(!methods.includes('GetGitCommitFiles'), 'update workflow must not load Git changed files')
  assert(!methods.includes('GetGitFileDiff'), 'update workflow must not load Git diffs')
}

export async function verifyPhase15SurfaceBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected Phase 15 surface bridge method ${method} to be called.`)
  }

  const chapterSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('chapters/'))
  assert.deepEqual(chapterSaves, [], 'Phase 15 surface workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'Phase 15 surface workflow must not open external URLs')
  await assertGitHistoryReadOnlyCalls(page)
}

export async function assertGitHistoryReadOnlyCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const gitMethods = calls
    .map((call) => call.method)
    .filter((method) => /^Git|^GetGit|^SaveGit|^SetGit|^DeleteGit|^CreateGit|^UpdateGit|^RevertGit|^ResetGit|^CheckoutGit|^RestoreGit|^CommitGit/.test(method))
  const unexpected = gitMethods.filter((method) =>
    !['GetGitCommits', 'GetGitCommitFiles', 'GetGitFileDiff', 'GetGitAuthorSettings', 'SaveGitAuthorSettings'].includes(method))
  assert.deepEqual(unexpected, [], `Git history UI must call only read-only Git methods, got ${unexpected.join(', ')}`)

  const chapterSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('chapters/'))
  assert.deepEqual(chapterSaves, [], 'Git history workflow must not save chapter content')
}

export async function verifySmokeBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetContent']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected smoke bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'smoke workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'smoke workflow must not open external URLs')
}

export async function verifyStressGuardrails(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  assert(methods.includes('GetContent'), 'stress workflow must load the large chapter through the bridge')
  assert(methods.includes('GetReferenceAnchors'), 'stress workflow must load reference anchors')
  assert(methods.includes('RebuildReferenceAnchor'), 'stress workflow must exercise reference import/segmentation status')
  assert(methods.includes('SearchReferenceMaterials'), 'stress workflow must search generated reference materials')
  assert(!methods.includes('GenerateReferenceChapterBlueprint'), 'blueprint generation is retired with the assembly line')
  assert(!methods.includes('BindReferenceBlueprintMaterials'), 'blueprint material binding is retired with the assembly line')
  assert(!methods.includes('SaveContent'), 'stress workflow must not save large chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'stress workflow must not open external URLs')

  const rebuildCall = calls.find((call) => call.method === 'RebuildReferenceAnchor')
  assert(rebuildCall?.result?.source_segment_count > 0, 'stress rebuild must report source segments')
  assert(rebuildCall?.result?.material_count > 0, 'stress rebuild must report generated materials')

  const defaultLibrarySearch = calls.find((call) =>
    call.method === 'SearchReferenceMaterials' &&
    Array.isArray(call.args[0]?.anchor_ids) &&
    call.args[0].anchor_ids.length === 0 &&
    call.args[0].page === 1)
  assert(defaultLibrarySearch, 'stress material library search must not require manually selected anchors')
  assert(defaultLibrarySearch.result?.total >= 1_200, 'stress material library search must expose a large paged material set')

  const scopedSearch = calls.find((call) =>
    call.method === 'SearchReferenceMaterials' &&
    Array.isArray(call.args[0]?.anchor_ids) &&
    call.args[0].anchor_ids.length === 1)
  assert(scopedSearch?.result?.items?.[0]?.material_id === 'stress-mat-0001', 'stress scoped material search must page generated stress materials')
}

function assertBridgeCallOrder(calls, beforeMethod, afterMethod) {
  const beforeIndex = calls.findIndex((call) => call.method === beforeMethod)
  const afterIndex = calls.findIndex((call) => call.method === afterMethod)
  assert(beforeIndex >= 0, `Missing bridge call ${beforeMethod}`)
  assert(afterIndex >= 0, `Missing bridge call ${afterMethod}`)
  assert(beforeIndex < afterIndex, `${beforeMethod} must happen before ${afterMethod}`)
}

function assertNoForbiddenProperties(value, forbiddenNames, path) {
  if (Array.isArray(value)) {
    value.forEach((item, index) => assertNoForbiddenProperties(item, forbiddenNames, `${path}[${index}]`))
    return
  }

  if (!value || typeof value !== 'object') {
    return
  }

  const forbidden = new Set(forbiddenNames.map((name) => name.toLowerCase()))
  for (const [key, child] of Object.entries(value)) {
    assert(!forbidden.has(key.toLowerCase()), `${path} must not expose ${key}`)
    assertNoForbiddenProperties(child, forbiddenNames, `${path}.${key}`)
  }
}
