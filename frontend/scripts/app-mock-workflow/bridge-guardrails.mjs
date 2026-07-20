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
    'SearchStyleSamples',
    'GetStyleSample',
    'CreateStyleSample',
    'UpdateStyleSample',
    'DeleteStyleSample',
    'ExtractStyleSkillFromSamples',
    'CancelStyleSkillExtraction',
    'BuildReferenceStyleProfile',
    'StartNarrativePatternExtraction',
    'CancelNarrativePatternExtraction',
    'GetNarrativePatternTrace',
    'GetGitCommits',
    'GetGitCommitFiles',
    'GetGitFileDiff',
    'SaveContent',
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
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetContent']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected writing bridge method ${method} to be called.`)
  }

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
    'ListReferenceMaterials',
    'RegisterReferenceMaterializationSource',
    'DeleteReferenceAnchor',
    'GenerateReferenceMaterializationBlueprintPreview',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected reference workspace bridge method ${method} to be called.`)
  }

  const previewRequest = calls.filter((call) => call.method === 'GenerateReferenceMaterializationBlueprintPreview').at(-1)?.args?.[0]
  assert.deepEqual(previewRequest?.anchor_ids, [101], 'blueprint preview must use the selected materialized reference source only')
  assert.equal(previewRequest?.novel_id, 42, 'blueprint preview must preserve the active novel')
  assert(!methods.includes('SaveContent'), 'blueprint preview must not write chapter content')
  assert(!methods.includes('runtime.shell.openExternal'), 'reference workspace must not open external URLs')
}

export async function verifyCorpusLibraryBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
    'GetReferenceAnchors',
    'GetReferenceMaterialDetail',
    'GetReferenceMaterialTagReviewQueue',
    'GetReferenceSourceSegmentDetail',
    'GetReferenceSourceProcessingDetail',
    'RebuildReferenceAnchor',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected corpus library bridge method ${method} to be called.`)
  }
  assertReferenceAnchorResultsArePathFree(calls)

  const forbiddenMethods = [
    'SaveContent',
    'StartReferenceOrchestrationRun',
    'GenerateReferenceChapterBlueprint',
    'ReviewReferenceChapterBlueprint',
    'ApproveReferenceChapterBlueprint',
    'BindReferenceBlueprintMaterials',
    'GetReferenceChapterBlueprint',
    'GetReferenceChapterBlueprints',
    'GetReferenceOrchestrationRuns',
    'GetReferenceOrchestrationRunEvents',
    'AdaptReferenceMaterial',
    'GenerateReferenceAnchoredDraft',
    'GetReferenceDraftCandidates',
    'AuditReferenceAnchoredDraft',
    'GetReferenceAnchoredDraftAudits',
  ]
  const unexpected = methods.filter((method) => forbiddenMethods.includes(method))
  assert.deepEqual(unexpected, [], `corpus library workflow must not trigger chapter-writing bridge calls: ${unexpected.join(', ')}`)
  assert(!methods.includes('runtime.shell.openExternal'), 'corpus library workflow must not open external URLs')
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
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
    'GetContent',
    'GetReferenceWritingSession',
    'GenerateReferenceBlueprints',
    'SelectReferenceBlueprint',
    'GenerateReferenceDraftCandidates',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), 'Expected chapter reference bridge method ' + method + ' to be called.')
  }

  for (const retiredMethod of [
    'SearchReferenceMaterials',
    'GetReferenceMaterialDetail',
    'GetReferenceCorpusBlueprintSession',
    'AdvanceReferenceCorpusBlueprintSession',
    'GenerateReferenceCorpusBlueprintCandidates',
    'GenerateReferenceCorpusInsertionDraft',
    'GenerateReferenceCorpusInsertionDraftCandidates',
    'RecordReferenceCorpusInsertionAudit',
    'GetReferenceOrchestrationRuns',
    'StartReferenceOrchestrationRun',
    'ResumeReferenceOrchestrationRun',
    'CancelReferenceOrchestrationRun',
    'GetReferenceDraftCandidates',
    'GetReferenceAnchoredDraftAudits',
  ]) {
    assert(!methods.includes(retiredMethod), 'chapter writing must not call retired bridge method ' + retiredMethod)
  }

  const sessionReads = calls.filter((call) => call.method === 'GetReferenceWritingSession')
  assert(sessionReads.length >= 2, 'chapter writing must read the persisted session on open and reopen')
  for (const read of sessionReads) {
    assert.equal(read.args?.[0]?.novel_id, 42, 'writing session read must bind the active novel')
    assert.equal(read.args?.[0]?.chapter_number, 1, 'writing session read must bind the active chapter')
    assert.equal(read.args?.[0]?.session_id, 'chapter:42:1', 'writing session read must use the stable chapter session id')
  }

  const generation = calls.find((call) => call.method === 'GenerateReferenceBlueprints')
  assert(generation?.result?.blueprints?.length === 3, 'chapter writing must receive three material blueprints')
  assert.equal(generation.args?.[0]?.novel_id, 42, 'blueprint generation must bind the active novel')
  assert.equal(generation.args?.[0]?.chapter_number, 1, 'blueprint generation must bind the active chapter')
  assert.equal(generation.args?.[0]?.session_id, 'chapter:42:1', 'blueprint generation must use the stable session')
  assert.equal(generation.args?.[0]?.requested_count, 3, 'blueprint generation must request three candidates')
  const generationJson = JSON.stringify(generation.result)
  assert(generationJson.includes('"material_id"'), 'writing blueprints must reference material identities')
  assert(generationJson.includes('"generation_id"'), 'writing blueprints must lock material generations')
  assert(!generationJson.includes('"node_id"'), 'writing blueprints must not reference retired node identities')

  const selection = calls.find((call) => call.method === 'SelectReferenceBlueprint')
  assert(selection?.args?.[0]?.blueprint_id, 'chapter writing must persist an explicit blueprint selection')
  assert.equal(selection.args?.[0]?.session_id, 'chapter:42:1', 'blueprint selection must target the stable session')
  assert.equal(selection.result?.selected_blueprint_id, selection.args?.[0]?.blueprint_id, 'backend selection must be returned')

  const draft = calls.find((call) => call.method === 'GenerateReferenceDraftCandidates')
  assert(draft?.result?.candidates?.length === 2, 'chapter writing must return every distinct draft candidate')
  assert.equal(draft.args?.[0]?.session_id, 'chapter:42:1', 'draft generation must target the stable session')
  assert.equal(draft.args?.[0]?.blueprint_id, selection.args?.[0]?.blueprint_id, 'draft generation must lock the selected blueprint')
  assert.equal(
    draft.args?.[0]?.current_draft_text,
    '林岚在雨夜旧宅门前停住。\n\n她看见桌上的水痕。',
    'draft generation must send the complete editor draft',
  )
  assert.equal(typeof draft.args?.[0]?.insertion_offset, 'number', 'draft generation must include the insertion offset')
  assert.deepEqual(draft.args?.[0]?.slot_values, {}, 'default writing must not synthesize slot values')
  assert.equal(draft.args?.[0]?.requested_count, 3, 'draft generation must request three candidates')

  for (const candidate of draft.result.candidates) {
    assert(candidate.text.includes('\n\n'), 'draft candidates must preserve paragraph boundaries')
    assert(candidate.audit?.passed === true, 'mock draft candidates must pass the server audit')
    for (const source of candidate.sources ?? []) {
      assert(source.material_id, 'draft provenance must include material_id')
      assert(source.generation_id, 'draft provenance must include generation_id')
      assert(!Object.hasOwn(source, 'node_id'), 'draft provenance must not include node_id')
    }
  }

  assert(!methods.includes('SaveContent'), 'chapter reference flow must update only the editor buffer')
}

export async function verifyPatternBridgeCalls(page) {
  const calls = await page.evaluate(() => window.__appMockState.calls)
  const methods = calls.map((call) => call.method)
  const requiredMethods = [
    'IsInitialized',
    'GetSettings',
    'GetNovels',
    'GetChapters',
    'GetModels',
    'StartNarrativePatternExtraction',
    'GetNarrativePatternTrace',
    'CancelNarrativePatternExtraction',
    'SaveContent',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected pattern bridge method ${method} to be called.`)
  }

  const chapterSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('chapters/'))
  assert.deepEqual(chapterSaves, [], 'pattern workflow must not save chapter content')

  const skillSaves = calls.filter((call) =>
    call.method === 'SaveContent' &&
    String(call.args?.[0]?.path ?? '').startsWith('skills/'))
  assert(skillSaves.length >= 1, 'pattern workflow must save generated skills through the skill catalog path')
  assert(!methods.includes('runtime.shell.openExternal'), 'pattern workflow must not open external URLs')
  assert(!methods.includes('ApproveReferenceChapterBlueprint'), 'pattern workflow must not approve reference blueprints')
  assert(!methods.includes('BindReferenceBlueprintMaterials'), 'pattern workflow must not bind reference materials')
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
    'StartNarrativePatternExtraction',
    'SearchStyleSamples',
    'GetStyleSample',
    'CreateStyleSample',
    'UpdateStyleSample',
    'DeleteStyleSample',
    'ExtractStyleSkillFromSamples',
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
    'SearchStyleSamples',
    'GetStyleSample',
    'CreateStyleSample',
    'UpdateStyleSample',
    'DeleteStyleSample',
    'ExtractStyleSkillFromSamples',
    'CancelStyleSkillExtraction',
    'BuildReferenceStyleProfile',
    'SaveContent',
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
  assert(methods.includes('GenerateReferenceChapterBlueprint'), 'stress workflow must generate a reference blueprint')
  assert(methods.includes('BindReferenceBlueprintMaterials'), 'stress workflow must bind generated materials into the blueprint')
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

  const blueprintCall = calls.find((call) => call.method === 'GenerateReferenceChapterBlueprint')
  assert(blueprintCall, 'stress workflow must generate a blueprint')
  assert.deepEqual(blueprintCall.args[0].anchor_ids, [], 'stress blueprint generation must work without manual per-novel corpus binding')

  const bindCall = calls.find((call) => call.method === 'BindReferenceBlueprintMaterials')
  assert(bindCall, 'stress workflow must bind blueprint materials')
  assert(bindCall.result?.links?.some((link) => String(link.material_id).startsWith('stress-mat-')), 'stress binding must use generated stress materials')
  assertBridgeCallOrder(calls, 'ReviewReferenceChapterBlueprint', 'ApproveReferenceChapterBlueprint')
  assertBridgeCallOrder(calls, 'ApproveReferenceChapterBlueprint', 'BindReferenceBlueprintMaterials')
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
