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
  const requiredMethods = ['IsInitialized', 'GetSettings', 'GetNovels', 'GetChapters', 'GetReferenceAnchors', 'SearchReferenceMaterials', 'GetReferenceMaterialDetail']

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected reference bridge method ${method} to be called.`)
  }

  assert(!methods.includes('SaveContent'), 'reference entry workflow must not save chapter content implicitly')
  assert(!methods.includes('runtime.shell.openExternal'), 'reference entry workflow must not open external URLs')
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
    'ListReferenceCorpusFeatureObservations',
    'ListReferenceCorpusTechniqueSpecimens',
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
    'SearchReferenceMaterials',
    'GetReferenceMaterialDetail',
    'GetReferenceOrchestrationRuns',
    'StartReferenceOrchestrationRun',
    'ResumeReferenceOrchestrationRun',
    'GenerateReferenceCorpusBlueprintCandidates',
    'GenerateReferenceCorpusInsertionDraftCandidates',
 'RecordReferenceCorpusInsertionAudit',
    'GetReferenceDraftCandidates',
    'GetReferenceAnchoredDraftAudits',
    'CancelReferenceOrchestrationRun',
  ]

  for (const method of requiredMethods) {
    assert(methods.includes(method), `Expected chapter reference bridge method ${method} to be called.`)
  }

  const searchCall = calls.find((call) => call.method === 'SearchReferenceMaterials')
  assert(searchCall, 'chapter reference drawer must search reference materials')
  assert.deepEqual(searchCall.args?.[0]?.anchor_ids, [], 'chapter reference drawer must search without manual anchor binding')
  const startCall = calls.find((call) => call.method === 'StartReferenceOrchestrationRun')
  assert(startCall, 'chapter reference drawer must start orchestration from the chapter surface')
  assert.equal(startCall.args?.[0]?.chapter_number, 1, 'chapter reference drawer must derive the active chapter number')
  assert.equal(startCall.args?.[0]?.anchor_ids, null, 'chapter reference orchestration must not require manual anchor selection')
  assert.equal(startCall.args?.[0]?.corpus_search_policy?.mode, 'story_context', 'chapter reference orchestration must use story-context corpus search')
  assert.deepEqual(startCall.args?.[0]?.corpus_search_policy?.include_anchor_ids, [], 'chapter reference orchestration must search accessible corpus by default')
  assert.equal(startCall.args?.[0]?.source_confirmed, false, 'chapter reference orchestration must preserve the source confirmation stop')

  const resumeCall = calls.find((call) => call.method === 'ResumeReferenceOrchestrationRun')
  assert(resumeCall, 'chapter reference drawer must resume the current orchestration decision in place')
  assert.equal(resumeCall.args?.[0]?.decision_type, 'confirm_source_and_facts', 'chapter reference resume must use stable backend decision type')

  const blueprintCandidateCalls = calls.filter((call) => call.method === 'GenerateReferenceCorpusBlueprintCandidates')
  assert(blueprintCandidateCalls.length >= 3, 'chapter reference drawer must generate corpus blueprint candidates, support feedback regeneration, and support draft-diagnosis regeneration')
  const firstBlueprintCandidateCall = blueprintCandidateCalls[0]
  const secondBlueprintCandidateCall = blueprintCandidateCalls[1]
  for (const [index, call] of blueprintCandidateCalls.entries()) {
    const payload = call.args?.[0]
    const libraryIds = payload?.scope?.library_ids
    assert(Array.isArray(libraryIds), `chapter reference blueprint candidate call ${index + 1} must send scope.library_ids as an array`)
    assert.deepEqual(
      libraryIds,
      [],
      `chapter reference blueprint candidate call ${index + 1} must let backend resolve default session libraries; got ${JSON.stringify(libraryIds)}`)
    assert.equal(
      payload?.scope?.session_id,
      'project:42:default',
      `chapter reference blueprint candidate call ${index + 1} must send the current chapter default corpus session; got ${JSON.stringify(payload?.scope?.session_id)}`)
  }
  assert(firstBlueprintCandidateCall.result?.candidates?.length >= 2, 'chapter reference blueprint candidate first round must return at least two candidates')
  assert(secondBlueprintCandidateCall.args?.[0]?.feedback, 'chapter reference blueprint candidate second round must send feedback')
  assert(
    (secondBlueprintCandidateCall.args?.[0]?.feedback?.problem_tags ?? []).includes('source_repetition'),
    'chapter reference blueprint candidate second round must send source_repetition feedback when the user asks for a different source mix',
  )
  assert.equal(secondBlueprintCandidateCall.result?.feedback_applied, true, 'chapter reference blueprint candidate second round must report feedback_applied')
  assert.match(
    String(secondBlueprintCandidateCall.result?.feedback_summary ?? ''),
    /rejected_blueprints:1/,
    'chapter reference blueprint candidate second round must report feedback_summary',
  )
  assert.match(
    String(secondBlueprintCandidateCall.result?.feedback_summary ?? ''),
    /fallback:feedback_filters_no_matches,fallback_to_base_filters/,
    'chapter reference blueprint candidate second round must report fallback diagnostics when feedback constraints are relaxed',
  )
  const firstRegeneratedCandidate = secondBlueprintCandidateCall.result?.candidates?.[0]
  assert(
    (firstRegeneratedCandidate?.feedback_reason ?? '').includes('fallback:feedback_filters_no_matches,fallback_to_base_filters'),
    'chapter reference blueprint candidate feedback_reason must include fallback diagnostics',
  )
  assert(
    (firstRegeneratedCandidate?.gap_reasons ?? []).includes('feedback_filters_no_matches') &&
      (firstRegeneratedCandidate?.gap_reasons ?? []).includes('fallback_to_base_filters'),
    'chapter reference blueprint candidate gap_reasons must expose fallback diagnostic codes',
  )
  assert(
    (firstRegeneratedCandidate?.gap_positions ?? []).some((position) =>
      (position?.gap_reasons ?? []).includes('missing_rhythm_evidence') &&
      (position?.missing_dimensions ?? []).includes('rhythm')),
    'chapter reference blueprint candidate gap_positions must expose beat-level missing dimension diagnostics',
  )
  const firstRegeneratedSources = secondBlueprintCandidateCall.result?.candidates?.[0]?.source_distribution ?? []
  assert(
    new Set(firstRegeneratedSources.map((source) => source.library_id)).size >= 2 ||
      new Set(firstRegeneratedSources.map((source) => source.anchor_id)).size >= 2,
    'chapter reference blueprint candidate source_repetition feedback must prioritize a cross-library or cross-anchor first regenerated candidate',
  )
  const firstStrategies = firstBlueprintCandidateCall.result?.candidates?.map((candidate) => candidate.blueprint?.strategy) ?? []
  const secondStrategies = secondBlueprintCandidateCall.result?.candidates?.map((candidate) => candidate.blueprint?.strategy) ?? []
  const firstSources = firstBlueprintCandidateCall.result?.candidates?.map((candidate) => candidate.source_distribution) ?? []
  const secondSources = secondBlueprintCandidateCall.result?.candidates?.map((candidate) => candidate.source_distribution) ?? []
  assert(
    JSON.stringify(firstStrategies) !== JSON.stringify(secondStrategies) ||
      JSON.stringify(firstSources) !== JSON.stringify(secondSources),
    'chapter reference blueprint candidate feedback regeneration must visibly change strategy or source distribution',
  )
  const draftNextActionBlueprintCall = blueprintCandidateCalls.find((call) =>
    (call.args?.[0]?.feedback?.problem_tags ?? []).includes('transition_replacement_required'))
  assert(draftNextActionBlueprintCall, 'chapter reference draft next_action must trigger blueprint regeneration with transition_replacement_required feedback')
  assert(
    (draftNextActionBlueprintCall.args?.[0]?.feedback?.problem_tags ?? []).includes('transition_replacement_outside_selected_blueprint'),
    'chapter reference draft next_action feedback must include the concrete transition replacement failure reason',
  )
  assert(
    (draftNextActionBlueprintCall.args?.[0]?.feedback?.rejected_node_ids ?? []).length >= 1,
    'chapter reference draft next_action feedback must carry rejected_node_ids for backend reranking',
  )
  assert.equal(draftNextActionBlueprintCall.result?.feedback_applied, true, 'chapter reference draft next_action blueprint regeneration must report feedback_applied')

  const insertionDraftCall = calls.find((call) => call.method === 'GenerateReferenceCorpusInsertionDraftCandidates')
  assert(insertionDraftCall, 'chapter reference drawer must generate corpus insertion draft candidates')
  const insertionDraftPayload = insertionDraftCall.args?.[0]
  const insertionSelectedBlueprint = insertionDraftPayload?.selected_blueprint
  assert(insertionSelectedBlueprint && typeof insertionSelectedBlueprint === 'object', 'chapter reference insertion draft must send selected_blueprint')
  assert(insertionSelectedBlueprint.blueprint_id, 'chapter reference insertion draft selected_blueprint must include blueprint_id')
  const selectedSecondRoundBlueprint = (secondBlueprintCandidateCall.result?.candidates ?? [])
    .map((candidate) => candidate.blueprint)
    .find((blueprint) => blueprint?.blueprint_id === insertionSelectedBlueprint.blueprint_id)
  assert(
    selectedSecondRoundBlueprint,
    `chapter reference insertion draft selected_blueprint must come from second-round blueprint candidates; got ${JSON.stringify(insertionSelectedBlueprint.blueprint_id)}`,
  )
  assert.deepEqual(
    insertionSelectedBlueprint,
    selectedSecondRoundBlueprint,
    'chapter reference insertion draft selected_blueprint must match the selected second-round blueprint candidate',
  )
  const insertionDraftLibraryIds = insertionDraftPayload?.scope?.library_ids
  assert(Array.isArray(insertionDraftLibraryIds), 'chapter reference insertion draft must send scope.library_ids as an array')
  assert.deepEqual(
    insertionDraftLibraryIds,
    [],
    `chapter reference insertion draft must let backend resolve default session libraries; got ${JSON.stringify(insertionDraftLibraryIds)}`)
  assert.equal(
    insertionDraftPayload?.scope?.session_id,
    'project:42:default',
    `chapter reference insertion draft must send the current chapter default corpus session; got ${JSON.stringify(insertionDraftPayload?.scope?.session_id)}`)
  assert.equal(
    typeof insertionDraftPayload?.chapter_context?.current_draft_text,
    'string',
    `chapter reference insertion draft chapter_context.current_draft_text must be a string; got ${typeof insertionDraftPayload?.chapter_context?.current_draft_text}`)
  assert.equal(
    typeof insertionDraftPayload?.chapter_context?.insertion_offset,
    'number',
    `chapter reference insertion draft chapter_context.insertion_offset must be a number; got ${typeof insertionDraftPayload?.chapter_context?.insertion_offset}`)
  assert.equal(insertionDraftPayload?.requested_count, 3, 'chapter reference insertion draft must request multiple candidates')
  assert(insertionDraftCall.result?.candidates?.length >= 2, 'chapter reference insertion draft result must include multiple candidates')
  assert.deepEqual(
    insertionDraftCall.result?.selected_blueprint,
    selectedSecondRoundBlueprint,
    'chapter reference insertion draft result selected_blueprint must match the selected second-round blueprint candidate',
  )
  let blockedAuditCandidateCount = 0
  let blockedTransitionCandidateCount = 0
  let readyCandidateCount = 0
  let readyTransitionCandidateCount = 0
  for (const candidate of insertionDraftCall.result?.candidates ?? []) {
    assert(candidate.candidate_id, 'chapter reference insertion draft candidate must include candidate_id')
    assert(candidate.draft?.assembled_text, 'chapter reference insertion draft candidate must include assembled_text')
    assert(candidate.draft?.gate?.passed === true, 'chapter reference insertion draft candidate gate should pass in the mock workflow')
    assert(Array.isArray(candidate.draft?.transitions), 'chapter reference insertion draft candidate must include transitions')
    assert(Array.isArray(candidate.draft?.audit?.transitions), 'chapter reference insertion draft audit must include transitions')
    const transitionAuditById = new Map((candidate.draft?.audit?.transitions ?? []).map((transition) => [transition.transition_id, transition]))
    const hasTransitionAuditBlock = (candidate.draft?.audit?.transitions ?? []).some((transition) => transition.passed === false)
    if (candidate.draft?.ready_for_insertion === true) {
      readyCandidateCount++
      assert(candidate.draft?.audit?.passed === true, 'ready chapter reference insertion draft candidate must pass draft audit')
      assert.deepEqual(candidate.draft?.audit?.errors ?? [], [], 'ready chapter reference insertion draft audit must not report errors')
      if ((candidate.draft?.transitions ?? []).some((transition) => String(transition.text ?? '').length > 0)) {
        readyTransitionCandidateCount++
      }
    } else {
      blockedAuditCandidateCount++
      assert(candidate.draft?.audit?.passed === false, 'blocked chapter reference insertion draft candidate must fail draft audit')
      assert((candidate.draft?.audit?.errors ?? []).length > 0, 'blocked chapter reference insertion draft audit must report errors')
      if (hasTransitionAuditBlock) {
        blockedTransitionCandidateCount++
      }
      assert.equal(
        candidate.draft?.chapter_text_after_insertion,
        insertionDraftPayload?.chapter_context?.current_draft_text,
        'blocked chapter reference insertion draft must preserve the current editor text',
      )
    }
    assertNoForbiddenProperties(candidate, ['source_text', 'raw_text', 'embedding'], 'chapter reference insertion draft candidate')
    const pieces = candidate.draft?.pieces ?? []
    assert(pieces.length > 0, 'chapter reference insertion draft candidate must include source-backed pieces')
    const auditPiecesByPieceId = new Map((candidate.draft?.audit?.pieces ?? []).map((piece) => [piece.piece_id, piece]))
    for (const piece of pieces) {
      assert(Array.isArray(piece.preserved_spans), 'chapter reference insertion draft piece must include preserved_spans')
      assert(piece.preserved_spans.length > 0, 'chapter reference insertion draft piece must include at least one preserved span')
      assert(Array.isArray(piece.locked_spans), 'chapter reference insertion draft piece must include locked_spans')
      const auditPiece = auditPiecesByPieceId.get(piece.piece_id)
      assert(auditPiece, `chapter reference insertion draft audit must include piece ${piece.piece_id}`)
      assert.equal(auditPiece.node_id, piece.node_id, 'chapter reference insertion draft audit piece must target the same node as the draft piece')
      if (candidate.draft?.ready_for_insertion === true) {
        assert.equal(auditPiece.passed, true, 'ready chapter reference insertion draft audit piece must pass')
        assert.equal(auditPiece.mismatched_span_count, 0, 'ready chapter reference insertion draft audit piece must not report preserved span mismatch')
        assert.deepEqual(auditPiece.violations, [], 'ready chapter reference insertion draft audit piece must not report violations')
      } else if (hasTransitionAuditBlock) {
        assert.equal(auditPiece.passed, true, 'transition-blocked chapter reference insertion draft should keep source piece audit passed')
        assert.equal(auditPiece.mismatched_span_count, 0, 'transition-blocked chapter reference insertion draft source pieces must not report preserved span mismatch')
      } else {
        assert.equal(auditPiece.passed, false, 'blocked chapter reference insertion draft audit piece must fail')
        assert(auditPiece.mismatched_span_count > 0, 'blocked chapter reference insertion draft audit piece must report a preserved span mismatch')
        assert(
          (auditPiece.violations ?? []).some((violation) => violation.code === 'preserved_text_hash_mismatch'),
          'blocked chapter reference insertion draft audit piece must report preserved_text_hash_mismatch',
        )
      }
      for (const span of piece.preserved_spans) {
        assert(span.span_id, 'chapter reference insertion draft preserved span must include span_id')
        assert(typeof span.source_text_hash === 'string' && span.source_text_hash.length > 0, 'chapter reference insertion draft preserved span must include source_text_hash')
        assert(typeof span.output_text_hash === 'string' && span.output_text_hash.length > 0, 'chapter reference insertion draft preserved span must include output_text_hash')
        if (candidate.draft?.ready_for_insertion === true) {
          assert(span.matches === true, 'ready chapter reference insertion draft preserved span must match')
        }
      }
    }
    for (const transition of candidate.draft?.transitions ?? []) {
      assert(transition.transition_id, 'chapter reference insertion draft transition must include transition_id')
      assert(transition.gap_id, 'chapter reference insertion draft transition must include gap_id')
      assert(transition.after_piece_id, 'chapter reference insertion draft transition must include after_piece_id')
      assert(transition.before_piece_id, 'chapter reference insertion draft transition must include before_piece_id')
      assert(transition.decision, 'chapter reference insertion draft transition must include decision')
      assert(transition.strategy, 'chapter reference insertion draft transition must include strategy')
      assert.equal(typeof transition.text, 'string', 'chapter reference insertion draft transition text must be a string')
      assert.equal(typeof transition.text_hash, 'string', 'chapter reference insertion draft transition text_hash must be a string')
      assert.equal(typeof transition.output_start, 'number', 'chapter reference insertion draft transition must include output_start')
      assert.equal(typeof transition.output_end, 'number', 'chapter reference insertion draft transition must include output_end')
      assert.equal(typeof transition.approved, 'boolean', 'chapter reference insertion draft transition must include approved')
      assert.equal(typeof transition.reason, 'string', 'chapter reference insertion draft transition must include reason')
      if (transition.decision === 'replace_piece') {
        assert(transition.replacement_piece_id, 'chapter reference replace_piece transition must include replacement_piece_id')
        assert(transition.replacement_node_id, 'chapter reference replace_piece transition must include replacement_node_id')
        const nextAction = candidate.next_action
        assert(nextAction, 'chapter reference replace_piece blocked draft must include next_action')
        assert.equal(nextAction.action, 'regenerate_blueprint', 'chapter reference replace_piece next_action must return to blueprint regeneration')
        assert.equal(
          nextAction.reason_code,
          'transition_replacement_outside_selected_blueprint',
          'chapter reference replace_piece next_action must expose the concrete reason code',
        )
        assert.equal(
          nextAction.rejected_piece_id,
          transition.replacement_piece_id,
          'chapter reference replace_piece next_action rejected_piece_id must match transition replacement_piece_id',
        )
        assert.equal(
          nextAction.replacement_node_id,
          transition.replacement_node_id,
          'chapter reference replace_piece next_action replacement_node_id must match transition replacement_node_id',
        )
        assert(
          (nextAction.feedback?.rejected_node_ids ?? []).includes(nextAction.rejected_node_id),
          'chapter reference replace_piece next_action feedback must carry the rejected source node',
        )
        assert(
          (nextAction.feedback?.problem_tags ?? []).includes('transition_replacement_required') &&
            (nextAction.feedback?.problem_tags ?? []).includes('transition_replacement_outside_selected_blueprint'),
          'chapter reference replace_piece next_action feedback must carry transition replacement problem tags',
        )
      }
      assert.equal(
        candidate.draft.assembled_text.slice(transition.output_start, transition.output_end),
        transition.text,
        'chapter reference insertion draft transition output range must match transition text',
      )
      const auditTransition = transitionAuditById.get(transition.transition_id)
      assert(auditTransition, `chapter reference insertion draft audit must include transition ${transition.transition_id}`)
      if (candidate.draft?.ready_for_insertion === true) {
        assert.equal(transition.approved, true, 'ready chapter reference insertion draft transition must be approved')
        assert.equal(auditTransition.passed, true, 'ready chapter reference insertion draft audit transition must pass')
        assert.deepEqual(auditTransition.violations ?? [], [], 'ready chapter reference insertion draft audit transition must not report violations')
      } else if (auditTransition.passed === false) {
        assert(
          (auditTransition.violations ?? []).some((violation) =>
            violation.transition_id === transition.transition_id &&
            [
              'transition_not_approved',
              'transition_text_unsafe',
              'transition_text_hash_mismatch',
              'transition_piece_replacement_required',
            ].includes(violation.code)),
          'blocked chapter reference insertion draft transition audit must report a transition-scoped violation',
        )
      }
    }
  }
  assert(blockedAuditCandidateCount >= 1, 'chapter reference insertion draft mock must include an audit-blocked candidate')
  assert(blockedTransitionCandidateCount >= 1, 'chapter reference insertion draft mock must include a transition-audit-blocked candidate')
  assert(readyCandidateCount >= 1, 'chapter reference insertion draft mock must include a ready candidate')
  assert(readyTransitionCandidateCount >= 1, 'chapter reference insertion draft mock must include a ready candidate with an audited transition')
  const slotVariantDrafts = await page.evaluate(async () => window.novelist.invoke(
    'GenerateReferenceCorpusInsertionDraftCandidates',
    {
      args: [{
        natural_language_goal: '写门口对峙，秦砚压住怒意，没有立刻开口。',
        chapter_context: {
          novel_id: 42,
          chapter_number: 1,
          current_draft_text: '秦砚停在黑塔门前。',
          insertion_offset: 8,
          previous_chapter_summary: '黑塔门前，秦砚需要压住情绪。',
          character_snapshots: [],
        },
        scope: {
          library_ids: [],
          reuse_policies: ['verbatim_ok', 'adapted_only'],
          include_anchor_ids: [],
          exclude_anchor_ids: [],
          session_id: 'project:42:default',
        },
        slot_values: {},
        selected_blueprint: {
          blueprint_id: 'mock-slot-variant-blueprint',
          query_context_hash: 'mock-slot-variant-query',
          strategy: 'selected_slot_only_fixture',
          beats: [{
            beat_id: 'mock-slot-variant-beat',
            beat_index: 0,
            role_in_beat: 'source_sentence',
            narrative_function: 'raise_pressure',
            node_ids: ['mock-node-rain-001'],
          }],
        },
        requested_count: 2,
        slot_value_variants: [{
          variant_id: 'strict-current-scene',
          label: '黑塔队长铜令',
          slot_values: {
            'character:她': '秦砚',
            'place:旧市集门口': '黑塔门前',
            'honorific:师兄': '队长',
            'plot_object:钥匙': '铜令',
          },
        }, {
          variant_id: 'alternate-current-scene',
          label: '废站组长门卡',
          slot_values: {
            'character:她': '秦砚',
            'place:旧市集门口': '废站门前',
            'honorific:师兄': '组长',
            'plot_object:钥匙': '门卡',
          },
        }],
      }],
    }))
  assert.equal(slotVariantDrafts?.candidates?.length, 2, 'slot_value_variants mock must return requested slot-only draft candidates')
  assert.deepEqual(
    slotVariantDrafts.candidates.map((candidate) => candidate.strategy),
    ['slot_variant_1', 'slot_variant_2'],
    'slot_value_variants mock must expose slot_variant strategies',
  )
  assert(
    slotVariantDrafts.candidates.every((candidate) =>
      candidate.draft?.ready_for_insertion === true &&
      candidate.draft?.gate?.passed === true &&
      candidate.draft?.audit?.passed === true),
    'slot_value_variants mock candidates must pass gate and audit',
  )
  assert.deepEqual(
    slotVariantDrafts.candidates.map((candidate) => candidate.draft?.pieces?.[0]?.node_id),
    ['mock-node-rain-001', 'mock-node-rain-001'],
    'slot_value_variants mock candidates must keep the same source node',
  )
  assert(slotVariantDrafts.candidates[0].draft?.assembled_text.includes('黑塔门前'), 'slot_value_variants first mock draft must apply place slot')
  assert(slotVariantDrafts.candidates[1].draft?.assembled_text.includes('废站门前'), 'slot_value_variants second mock draft must apply alternate place slot')
  assert(
    slotVariantDrafts.candidates.every((candidate) =>
      candidate.draft?.assembled_text.includes('《旧市集门口师兄钥匙案》')),
    'slot_value_variants mock must preserve protected title text',
  )

  const saveContentCalls = calls.filter((call) => call.method === 'SaveContent')
  assert.deepEqual(saveContentCalls, [], 'chapter reference drawer must not save chapter content directly')

  const forbiddenMethods = [
    'CreateReferenceAnchor',
    'CreateReferenceAnchors',
    'CreateReferenceAnchorsWithResult',
    'UpdateReferenceAnchor',
    'UpdateReferenceAnchorMetadata',
    'DeleteReferenceAnchor',
    'DeleteReferenceAnchors',
    'ArchiveReferenceAnchor',
    'PromoteReferenceAnchorToWorkspaceCorpus',
    'PromoteReferenceAnchorsToWorkspaceCorpus',
    'RebuildReferenceAnchor',
    'CorrectReferenceMaterialTags',
    'BulkCorrectReferenceMaterialTags',
    'UpdateReferenceMaterialTags',
    'UpdateReferenceMaterialsTags',
    'DeleteReferenceMaterials',
    'RestoreReferenceMaterials',
    'AdaptReferenceMaterial',
    'GenerateReferenceChapterBlueprint',
    'ReviewReferenceChapterBlueprint',
    'ApproveReferenceChapterBlueprint',
    'BindReferenceBlueprintMaterials',
    'GenerateReferenceAnchoredDraft',
    'AuditReferenceAnchoredDraft',
  ]
  const unexpected = methods.filter((method) => forbiddenMethods.includes(method))
  assert.deepEqual(unexpected, [], `chapter reference drawer must not mutate materials or save content: ${unexpected.join(', ')}`)
  assert(!methods.includes('runtime.shell.openExternal'), 'chapter reference drawer workflow must not open external URLs')
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
