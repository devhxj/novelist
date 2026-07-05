# Reference Anchor Verification and Guardrails

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

## Required Test Matrix

Run after backend phases:

```text
dotnet test tests/Novelist.Tests/Novelist.Tests.csproj
dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj
```

Run after frontend phases:

```text
cd frontend
npm run build
npm run lint
```

Run after Phase 10 frontend workflow hardening once the Playwright harness exists:

```text
cd frontend
npm run test:reference-anchor
```

Run after Phase 13 app-wide Playwright coverage exists:

```text
cd frontend
npm run test:app
```

Targeted new tests:

```text
tests/Novelist.Tests/ReferenceAnchorSegmentationTests.cs
tests/Novelist.Tests/ReferenceAnchorRewriteLevelTests.cs
tests/Novelist.Tests/ReferenceAnchorAuditTests.cs
tests/Novelist.Tests/ReferenceChapterBlueprintAnalysisContractTests.cs
tests/Novelist.Tests/ReferenceChapterBlueprintReviewTests.cs
tests/Novelist.Tests/ReferenceChapterBlueprintApprovalInvalidationTests.cs
tests/Novelist.Tests/ReferenceAnchoredDraftAuditTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchorServiceTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchorBridgeTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchoredDraftServiceTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchoredDraftBridgeTests.cs
```

## Critical Guardrails

- Do not put reference source text into `goink.md`, chapter files, skills, or preferences.
- Do not expose arbitrary file reads through agent tools.
- Do not let agent tools call `SaveContent` through this workflow.
- Do not generate chapter prose from a raw prompt or chapter plan when the reference-anchored workflow is selected.
- Do not generate draft candidates from unreviewed, failed, stale, or unapproved blueprints.
- Do not let `review_passed` alone unlock draft generation; explicit approval is required.
- Do not let draft generation use stale material links after blueprint edits.
- Do not let a blueprint hide final prose paragraphs in analysis fields to bypass candidate audit.
- Do not store blueprints as unstructured Markdown only; beat-level fields must remain machine-checkable.
- Do not allow a blueprint to pass with missing logic, emotion, narration, character, or reference-use tracks.
- Do not allow emotional state changes without trigger, suppressed/internal reaction, and external evidence.
- Do not allow scene transitions that only say location/time changed without causal, emotional, or informational pressure.
- Do not allow POV knowledge outside the current viewpoint boundary.
- Do not let a material match pass on semantic similarity alone; beat function, emotion, POV, prose duty, or explicit no-reuse policy must be considered.
- Do not let blueprint generation introduce facts outside the known fact set without marking them for review.
- Do not let blueprint approval survive a changed source chapter-plan hash.
- Do not let blueprint approval survive manual edits to analysis tracks or beats.
- Do not build source import into `ExtractStyle`.
- Do not store large source/material banks in JSON stores.
- Do not reuse `rag_chunks` or story-memory vector tables.
- Do not depend on embeddings being configured for basic source import/search.
- Do not add ASP.NET Core/Kestrel as the product API path for reference anchors; use the Photino bridge.
- Do not ship L2+ adaptation without rewrite-level tests.
- Do not ship blueprint-gated drafting without blueprint review tests and draft audit tests.
- Do not add frontend API methods without updating `BridgeCompatibilityAppMethods`.

## Phase 10 Design Decisions To Close

- Unknown-license source previews: resolved for the current implementation. Search/library preview payloads truncate exact text by default; complete source/material text remains in SQLite for provenance, adaptation, binding, and audit.
- Source segment text policy: keep full original line text, normalized text, or both.
- Optional model assistance: resolved for the current implementation. Extraction, material tagging, slot adaptation, rewrite-level classification, and reuse audit stay deterministic-only. Future LLM-assisted tagging/adaptation requires an explicit opt-in interface or feature flag and must feed deterministic review/binding/audit instead of bypassing those gates.
- Search scope: resolved for the current implementation. Keep reference-anchor results isolated in `SearchReferenceMaterials` and the dedicated reference panel; do not merge them into global `SearchAll` until a staged opt-in design defines result taxonomy, ranking, and preview policy.
- Revision lineage: keep in-place draft blueprint revision, or create `parent_blueprint_id` lineage rows for every material contract edit.
- Failed review assistance: keep revision fully manual/agent-driven, or add field-level fix suggestions that remain separate from approval.
- Transition/no-reuse policy: require every generated paragraph to trace to material, or allow approved no-reuse transition beats without direct links.
- Candidate assembly: keep beat-level candidates only, or assemble a full-chapter candidate after all beats pass audit.
- Stale blueprint UX: resolved for the current implementation. Preserve stale blueprints read-only for comparison, keep them visible, disable review/approval/revision/material binding/candidate generation, and show a regeneration prompt.
- Development workflow: resolved as explicit frontend build/Vite steps for faster backend-only loops.
- Phase 10 runtime verification: resolved as layered coverage. Playwright mock-bridge tests own the full frontend workflow and screenshot/DOM state matrix. .NET integration tests own bridge/service behavior and production composition. Real Photino verification is a minimal runtime smoke, not a manual full-workflow requirement.

## Phase 11 Automation Decisions To Close

- Default stop points: which risks always require human confirmation, and which low-risk stages can continue automatically.
- Approval granularity: whether approving a compact blueprint summary approves the entire current analysis contract or only selected beats.
- AI revision authority: whether AI may apply its own proposed revision in agent mode, or whether every proposed blueprint change requires user approval.
- Run persistence: whether orchestration runs live only in SQLite reference-anchor storage or also appear in chat/session history.
- Candidate insertion UX: whether final insertion is copy-only, diff-preview insertion, or a separate approved chapter-edit operation.
- Advanced mode boundary: which manual controls remain visible by default and which move behind an advanced/debug toggle.
- Failure recovery: whether users should resume from the failed stage, regenerate from scratch, or branch a new blueprint revision.

## Phase 12 Shared Corpus Decisions To Close

- Corpus scope: workspace-only for now, or also support user-level/importable library packs later.
- Source ownership migration: convert `reference_anchors.novel_id` to nullable/visibility metadata, or create new corpus tables and compatibility views.
- Material identity: keep current anchor-derived material ids, or introduce stable corpus material ids independent of importing novel.
- Search policy: define default license/status filters, include/exclude controls, and whether explicit source opt-out is needed per run.
- Retrieval gap policy: decide thresholds for automatic query expansion, weak-match warnings, approved no-reuse continuation, and mandatory user stop.
- Feedback scope: which decisions should improve global corpus ranking, and which should remain per-novel because they depend on plot facts.
- UI information architecture: separate corpus library management from reference-anchored drafting controls.
- Agent authority: whether AI may import new corpus sources, or only search/use already imported sources.

## Phase 13 App-Wide Playwright Decisions To Close

- Suite boundary: keep `test:reference-anchor` as a deep feature workflow and add `test:app` for broad product regression, or consolidate both under a shared Playwright runner with separate projects.
- Fixture strategy: use one shared mocked Novelist bridge fixture for shell/editor/search/chat/settings flows, or split fixtures by surface to keep failures easier to diagnose.
- Selector policy: rely on accessible roles/names by default and add `data-testid` only for controls whose visible labels are intentionally dynamic.
- Visual baseline scope: decide which desktop screenshots are stable enough for regression and which flows should remain DOM/assertion-only.
- Real Photino boundary: keep app-wide coverage on Vite plus mocked bridge, with real Photino limited to asset loading and representative production bridge routing.
- CI policy: decide whether app-wide Playwright runs on every PR or only on frontend-affecting changes until runtime is stable enough.

## Recommended Next Coding Sessions

Start with Phase 10. Do not restart from Phase 0 unless contracts have regressed.

Latest verified scope: `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge -v minimal` passed 29/29, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceAnchorContractTests -v minimal` passed 10/10, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter MafToolRegistryTests -v minimal` passed 15/15, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchorServiceTests -v minimal` passed 26/26, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchor -v minimal` passed 89/89, `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter ReferenceChapterBlueprintPayloadsUseStableSnakeCaseJsonNames -v minimal` passed 1/1, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter 'FullyQualifiedName~ReviseApprovedBlueprintInvalidatesApprovalAndMaterialLinks|FullyQualifiedName~BridgeReferenceAnchoredDraftHandlersGenerateReviewAndApproveBlueprint' -v minimal` passed 2/2, `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceDraftTools -v minimal` passed 2/2, and `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter Reference -v minimal` passed 124/124 after adding full reference bridge service-routing coverage, reference bridge app-not-initialized/invalid-path semantics coverage, MAF tool exposure/schema constraints, stable reference bridge invalid-payload coverage, structured blueprint bridge payload verification, approval/material-link invalidation coverage, complete Phase 1 create/import/rebuild/failure-status coverage, Phase 2 material provenance/tag/slot coverage, blueprint tag-filter/prose-duty binding coverage, extractor Chinese punctuation/dialogue/paragraph plus emotion/POV/function-tag coverage, Phase 3 lexical ranking/pagination coverage, Phase 3 search score-component coverage, and Phase 3 narrative-duty/emotion-transition search filtering coverage. `cd frontend && npm run build` and `cd frontend && npm run lint` passed after adding optional reference material search scoring and filter fields to the frontend types.

Recommended next session:

1. Add the Playwright mock-bridge reference-anchor workflow suite and record screenshots/DOM assertions for the complete Phase 10 frontend path.
2. Keep the real Photino check minimal: load the app, open the reference panel, invoke representative bridge calls, and confirm there is no automatic chapter insertion path.

Recommended following session:

1. Keep broadening deterministic Chinese narration, emotion, POV, and unsupported-fact fixtures; any optional model-assisted tagging or adaptation should be designed later as an explicit opt-in path that feeds the deterministic gates.
2. Design Phase 11 orchestration contracts before changing UI, so frontend, bridge, service, and agent tools share the same run-state model.
3. Design Phase 12 shared-corpus contracts before migrating storage, so global materials can be reused without weakening per-novel safety gates.
4. Add Phase 13 app-wide Playwright coverage after the core reference-anchor harness is stable, so shell/editor/search/chat/settings regressions are tested alongside reference-anchor smoke coverage.

Do not broaden frontend workflow beyond the review-first path until source corpus, material binding, blueprint review, and draft audit are reliable. The system's quality depends on immutable provenance, hard blueprint gates, and candidate audit before any manual insertion.

After Phase 3, implement Phase 4 before any prose generation work. The blueprint review gate is the control layer that makes reference reuse robust; drafting before that gate would recreate the original failure mode under a different name.
