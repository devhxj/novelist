# Reference Anchor Tasks: Phase 14

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

## Phase 14: Advanced Style Anchoring and High-Fidelity Imitation

**Status:** Open as of 2026-07-07.

**Description:** Phase 14 upgrades the reference-anchor layer from rule-based material extraction into an advanced style anchoring system. The target is a robust, auditable imitation engine that understands source material at multiple scales, extracts high-quality style features, retrieves materials by story purpose and style fit, generates beat-scoped candidates under explicit style contracts, and audits outputs for provenance, factual safety, POV safety, style quality, and unsafe source-text proximity.

This phase exists because the current material bank is useful but not deep enough. Phase 13 proved that deterministic sentence/paragraph materials can be generated, searched, paged, bound, and stress-tested. Phase 14 must add richer literary understanding: scene function, rhythm, paragraph cadence, dialogue mechanics, image systems, tension turns, setup/payoff, hook patterns, narrative distance, web-fiction beat mechanics, and source-supported style evidence.

Phase 14 must not become a loose "write like this author" prompt. It must remain an anchored workflow with user-provided or authorized sources, source hashes, evidence spans, reviewable style profiles, explicit imitation intensity, rewrite limits, and audit gates before any candidate can be inserted into chapter text.

## Current Gap

The current extractor is deterministic and robust, but limited:

- it mainly creates sentence and paragraph/passage materials;
- tags are rule-based first-pass labels such as dialogue, interiority, emotion evidence, environment, transition, action, POV, and sensory detail;
- search can use narrative duty, emotion transition, prose duty, lexical score, optional embeddings, and feedback;
- it does not yet produce scene/beat-level materials, style signatures, rhythm profiles, image motifs, pacing patterns, suspense hooks, web-fiction payoff mechanics, or evidence-backed style explanations;
- it does not yet score whether a generated candidate truly matches a target style while staying far enough from source phrasing.

## Non-Negotiable Scope

- Build multi-scale source understanding: chapter, scene, beat, paragraph, sentence, dialogue exchange, action-afterbeat pair, sensory image, hook, payoff, and transition materials.
- Introduce style profiles as first-class, versioned, auditable records. A style profile stores feature distributions and cited evidence spans, not only free-form analysis.
- Add optional LLM-assisted style/material analysis behind explicit configuration. Model output is untrusted JSON and must pass deterministic validation, schema checks, source-span grounding, confidence thresholds, and fallback behavior.
- Preserve deterministic-only operation for basic import/search. Advanced style extraction may be unavailable without a configured model, but it must not break existing Phase 0-13 workflows.
- Keep imitation beat-scoped. A candidate is generated from an approved blueprint beat, selected materials, and a style contract, not from a raw "imitate this book" prompt.
- Add style audit before insertion: source leakage, n-gram overlap, phrase reuse, rewrite level, factual drift, POV drift, style-feature distance, generic AI prose, and excessive closeness to one source span.
- Keep final insertion manual. No style workflow may call `SaveContent` or mutate chapter prose without explicit user edit/save action.

## Architecture Decisions

- **Style features are structured data.** Store rhythm, pacing, imagery, narration distance, dialogue mechanics, sentence/paragraph statistics, hook/payoff patterns, and emotional presentation as typed fields with evidence spans.
- **Style profile != source text.** Profiles may summarize distributions and examples but must keep provenance links instead of copying full source text into prompts, skills, preferences, or chapter files.
- **LLM analysis is optional and audited.** Deterministic extraction provides baseline materials; LLM-assisted analysis enriches them only when configured, with source-span validation and confidence labels.
- **Imitation intensity is explicit.** Users choose loose, moderate, strong, or diagnostic-only style anchoring. Strong anchoring increases audit strictness and source-leak checks.
- **Similarity safety is a feature, not a blocker added later.** Phase 14 must ship with overlap and proximity tests before shipping higher-fidelity imitation.

## Task 1: Style Feature Contract and Storage Model

**Status:** Complete for the first Phase 14 storage boundary.

**Description:** Define contracts and SQLite storage for style profiles, style evidence spans, style feature vectors, style analysis runs, and per-material advanced tags.

**Acceptance criteria:**

- [x] Contracts cover style profile id, source anchor ids, source hashes, analyzer version, feature schema version, evidence spans, confidence, and license/source-trust policy.
- [x] SQLite tables store style profiles and evidence without duplicating large source text outside source segment/material tables.
- [x] Migrations preserve existing reference-anchor databases and do not rebuild source/material ids.

**Verification:**

- [x] Contract JSON tests for stable snake_case payloads.
- [x] SQLite migration tests from pre-Phase 14 databases.
- [x] Integration test proves deleting/archiving source materials does not orphan profile provenance silently.

**Implementation notes:**

- `ReferenceStylePayloads.cs` defines profile, feature-vector, evidence-span, and build/list/detail payloads.
- `SqliteReferenceStyleProfileService` creates `reference_style_profiles`, `reference_style_profile_sources`, `reference_style_profile_evidence`, `reference_style_analysis_runs`, and `reference_material_style_tags` in the existing `reference-anchor/index.sqlite` database.
- Evidence spans store provenance ids, offsets, text hashes, confidence, and analyzer source, not source text. Soft material archive keeps evidence readable; hard material deletion is blocked by foreign keys.

**Dependencies:** None.

**Estimated scope:** M.

## Task 2: Multi-Scale Source Segmentation

**Description:** Extend segmentation beyond sentence/paragraph into scene, beat, dialogue exchange, action-afterbeat, image motif, hook, payoff, and transition candidates.

**Acceptance criteria:**

- [x] Existing chapter/paragraph/sentence segment ids remain stable.
- [x] New multi-scale segment ids are deterministic and include parent links to source segments.
- [x] Chinese web-fiction fixtures produce scene/beat/dialogue/hook/payoff materials with stable counts and source hashes.
- [x] 10MB source import stays paged and does not block existing material browsing.

**Verification:**

- [x] `ReferenceAnchorAdvancedSegmentationTests`
- [x] 10MB integration test with segment/material count thresholds.
- [x] Rebuild test proves stable ids when source text is unchanged.

**Dependencies:** Task 1.

**Estimated scope:** M.

## Task 3: Deterministic Style Baseline Extractor

**Status:** Partially complete. The deterministic baseline builder exists, is covered by pure feature tests, persists profile features, and search can consume baseline profile evidence for style-fit ranking; style audit consumers remain open.

**Description:** Add deterministic style features that do not require a model: sentence length distribution, paragraph length distribution, punctuation rhythm, dialogue ratio, narration/dialogue alternation, sensory-channel counts, interiority ratio, action-afterbeat ratio, transition frequency, and hook/cliffhanger markers.

**Acceptance criteria:**

- [x] Style profiles can be built without LLM configuration.
- [x] Feature values are reproducible for the same source hash and analyzer version.
- [x] Search and style audit can read these baseline features. Search-side baseline scoring is implemented, and anchored draft audit now reads persisted profile feature vectors for supported deterministic numeric dimensions.

**Verification:**

- [x] Unit tests for rhythm, punctuation, dialogue ratio, paragraph cadence, and hook marker extraction.
- [x] Integration test builds a deterministic style profile from a Chinese fixture.
- [x] Integration test proves material search can read baseline profile evidence without cross-novel profile bypass.
- [x] Integration test proves draft audit can read persisted profile feature vectors for deterministic style-distance checks.

**Dependencies:** Tasks 1-2.

**Estimated scope:** M.

## Task 4: LLM-Assisted Style Analyst Interface

**Status:** Complete for the first LLM-assisted analyzer boundary. A core `IReferenceStyleLlmAnalyzer` boundary and bounded-window request payload now exist, and `SqliteReferenceStyleProfileService` can use an analyzer provider after the deterministic baseline is built. `ReferenceStyleChatCompletionLlmAnalyzer` is wired in desktop composition through the user's selected model; if no model is selected it returns no output and the profile build remains deterministic without recording an LLM run. Returned model JSON is treated as untrusted: `ReferenceStyleLlmAnalysisValidator` accepts only versioned JSON, requires each label to cite source segment ids and offsets inside supplied bounded windows, rejects unsupported or ungrounded labels, downgrades overconfident labels, and returns diagnostics. Accepted LLM evidence is persisted as `llm_assisted` evidence/material tags and merged into categorical profile features; invalid, rejected, or provider-failed output records an LLM analysis run and leaves the deterministic profile intact.

**Description:** Add an optional model-assisted analyzer that enriches materials with advanced style labels and evidence-backed explanations.

**Acceptance criteria:**

- [x] Analyzer accepts bounded source spans or sampled segment windows, never arbitrary file paths. The core analyzer request carries only `windows` with source segment/material provenance and bounded text excerpts.
- [x] Model output must be valid JSON matching a versioned schema. The first validator slice requires `schema_version = reference-style-llm-analysis-v1` and strict root/label/evidence shapes.
- [x] Every advanced label must cite source segment ids and character offsets.
- [x] Invalid, unsupported, ungrounded, or overconfident labels are rejected or downgraded.
- [x] No model configuration means the system falls back to deterministic baseline features. The service constructor defaults to no provider, and invalid injected-provider output records diagnostics while keeping the deterministic profile metadata/features/evidence.

**Verification:**

- [x] Unit tests for schema validation, source-span grounding, confidence downgrade, and invalid JSON fallback.
- [x] Integration test using an injected fake analyzer provider.
- [x] Integration tests for selected-model chat-completion provider prompting and no-selected-model skip behavior.
- [x] No-live-network test for default desktop/test composition. Photino composition smoke coverage builds a style profile through the desktop bridge and verifies only the deterministic analysis run is persisted when no provider is configured.

**Dependencies:** Tasks 1-3.

**Estimated scope:** M.

## Task 5: Advanced Style Taxonomy

**Status:** Complete for the first versioned taxonomy contract. `ReferenceStyleTaxonomy` now defines `reference-style-taxonomy-v1` with stable feature keys, allowed labels, categories, and compatible beat-duty hints. The LLM validator rejects unsupported labels as well as unsupported feature keys, and the chat-completion analyzer includes allowed labels in its bounded prompt. The taxonomy is documented in `docs/reference-anchor-implementation/style-taxonomy-v1.md`.

**Description:** Define and implement a high-fidelity fiction style taxonomy suitable for long-form Chinese novels and web fiction.

**Acceptance criteria:**

- [x] Taxonomy covers narration distance, POV control, rhythm, sentence shape, paragraph cadence, dialogue mechanics, subtext, externalized emotion, sensory image, metaphor/image system, tension pressure, hook, payoff, transition, exposition handling, action clarity, and anti-screenplay prose.
- [x] Taxonomy includes web-fiction mechanics: chapter hook, escalation beat, payoff beat, compression/expansion,爽点 delivery, cliffhanger type, information withholding, and reader-promise tracking.
- [x] Labels include confidence, evidence spans, and analyzer source. Accepted labels become `ReferenceStyleEvidenceSpanPayload` records with confidence, analyzer source, source ids, offsets, and hashes.
- [x] Taxonomy is documented and versioned.

**Verification:**

- [x] Golden fixture tests for every taxonomy label through grounded validator acceptance.
- [x] Regression tests for label compatibility with blueprint beat duties.

**Dependencies:** Tasks 3-4.

**Estimated scope:** M.

## Task 6: Style Profile Builder and Library UI

**Status:** Complete for the first product profile-library boundary. The backend bridge/service boundary supports profile build, list, detail, archive, restore, and deterministic profile comparison. The frontend `风格画像库` surface lets users build profiles from selected corpus anchors, inspect feature/evidence metadata, compare two profiles, and archive/restore profiles without exposing source text or mutating chapter prose. Archived profiles are hidden from default profile lists and remain visible when `include_archived` is requested. Profile comparison returns structured numeric, distribution, and categorical feature deltas without source text. Broader style-assisted default workflow polish remains tracked under Tasks 12 and 14.

**Description:** Add a product surface for building, inspecting, comparing, archiving, and reusing style profiles.

**Acceptance criteria:**

- [x] Users can build a style profile from selected workspace corpus sources.
- [x] Profile detail view shows feature distributions, evidence spans, confidence, analyzer version, and license/source-trust status.
- [x] Users can compare two profiles and see where rhythm, dialogue, narration distance, and hook mechanics differ.
- [x] Archived profiles disappear from default selection but remain available for audit history.

**Verification:**

- [x] Playwright coverage for profile build, inspect, compare, archive, and restore through `npm --prefix frontend run test:reference-style`.
- [x] Bridge tests for profile CRUD and profile-source visibility filters.

**Implementation notes:**

- `frontend/src/components/reference-anchor/StyleProfileLibraryPanel.tsx` is mounted under `语料库管理` and uses only the owned Photino bridge adapter methods for style profile operations.
- The browser workflow extends `frontend/scripts/reference-anchor-mock-workflow.mjs` with style-profile mock bridge handlers and assertions for build payload policy defaults, detail inspection, comparison ids, archive/restore payloads, and no `SaveContent` calls.
- The detail panel displays provenance ids, offsets, text hashes, analyzer source, confidence, and feature summaries, but not source text.

**Dependencies:** Tasks 1-5.

**Estimated scope:** M.

## Task 7: Style-Aware Material Retrieval

**Description:** Upgrade retrieval so material selection considers story context, required beat duties, and target style profile fit.

**Acceptance criteria:**

- [x] Search accepts optional style profile ids, style dimensions, and imitation intensity.
- [x] Ranking exposes score components for lexical, embedding, tag, narrative duty, prose duty, style fit, confidence, feedback, and source-risk penalty.
- [x] Retrieval can choose scene/beat/dialogue/hook materials, not only sentence/passages.
- [x] Low style-fit matches are surfaced as retrieval gaps instead of silently free-drafting. The first backend slice marks selected low style-fit links as `low_confidence` weak matches, which fails draft audit until the retrieval gap is resolved.

**Verification:**

- [x] Integration tests for style-fit ranking and weak-match fallback. Style-fit ranking and low style-fit weak-match audit risk are covered.
- [x] Agent-tool tests prove style filters cannot bypass profile/corpus visibility boundaries. MAF executor integration now verifies style-aware material search still fails cross-novel style profiles and cannot read explicitly requested private workspace corpus anchors.

**Dependencies:** Tasks 2-6.

**Estimated scope:** M.

## Task 8: Style Contract for Blueprint Beats

**Status:** Complete for the first editable style-contract boundary. Backend contracts, SQLite storage, analysis-contract hashing, whole-contract revision, material-binding consumption, deterministic review diagnostics for invalid style contracts, and compact orchestration approval summaries exist. The frontend blueprint detail panel now exposes field-level style-contract controls for profile ids, style duties, imitation intensity, minimum fit, allowed closeness, required evidence, and forbidden style risks; saving those fields emits the existing `beat:<id>:style_contract` revision payload so approval hashes and material links are invalidated through the same backend path.

**Description:** Extend blueprint/material binding so each beat can carry a style contract: target style profile, style duties, imitation intensity, allowed closeness, required evidence types, and forbidden style risks.

**Acceptance criteria:**

- [x] Blueprint beat contracts can express target style profile ids, style dimensions/duties, imitation intensity, minimum style fit, allowed closeness, required evidence types, and forbidden style risks. Full rhythm/POV/dialogue/sensory/hook/payoff taxonomy mapping remains open.
- [x] Approval hash includes style contract fields.
- [x] Editing style contract invalidates approval/material links.
- [x] Style contracts can be summarized compactly for user approval. Orchestration blueprint approval summaries now include compact beat-level style contract profile ids, intensity, minimum fit, closeness, dimensions, evidence, and risk terms in `material_use_plan`.

**Verification:**

- [x] Contract/hash invalidation tests for JSON payload and analysis-contract hash.
- [x] Blueprint review tests for missing or contradictory style duties. The deterministic reviewer now fails missing style profile ids, empty style duties, strong imitation with too-low `min_style_fit`, and required style evidence labels/material granularities that are incompatible with the beat material search.
- [x] Integration test proves blueprint approval summary includes compact style contract terms.
- [x] Playwright approval-summary coverage through `npm --prefix frontend run test:reference-style`.

**Implementation notes:**

- `BlueprintDetail` keeps the user-facing editing surface field-level, while `ReferenceAnchorView.saveBlueprintEdits` serializes a normalized `style_contract` object into the existing whole-contract revision field. Empty style fields clear an existing contract with an empty revision value.
- The mock browser workflow edits a beat style contract, asserts the exact `ReviseReferenceChapterBlueprint` payload, verifies the saved beat summary, and verifies the orchestration approval summary includes compact style-contract terms.

**Dependencies:** Tasks 5-7.

**Estimated scope:** M.

## Task 9: Style-Guided Candidate Generation

**Status:** Complete for the first auditable style-guided candidate boundary. Draft generation now accepts optional style intensity matrices, passes a beat-scoped style context into material adaptation, produces multiple candidates for loose/moderate/strong settings when requested, and records each candidate's attempted style obligations without copying source text. Candidate records persist `style_attempts` with profile ids, dimensions, requested intensity, minimum fit, closeness policy, evidence/risk terms, selected-material fit score, low-confidence retrieval status, and attempt status. The workflow still returns candidates only and does not write chapter content.

**Description:** Generate beat-scoped candidates that use selected materials and style contracts while preserving facts, POV, and rewrite budget.

**Acceptance criteria:**

- [x] Candidate generation receives one approved beat contract plus selected material/style evidence links. The internal material adaptation payload now carries a structured `style_context` derived from the approved beat style contract and selected material score components; agent-facing tools cannot forge this context.
- [x] Candidate output records which style features it attempted to satisfy. `ReferenceDraftParagraphCandidatePayload.style_attempts` stores structured style obligations and selected-material style-fit metadata, not source text or prompt text.
- [x] Multiple candidates can be generated with different style-intensity settings. `GenerateReferenceAnchoredDraftPayload` supports optional `style_intensities` and `candidates_per_beat`, and the frontend style-contract path requests loose/moderate/strong candidates.
- [x] Generated candidates never write directly to chapter content. Generation still returns `ReferenceAnchoredDraftPayload` candidates only; existing no-`SaveContent` browser and integration checks remain in place.

**Verification:**

- [x] Integration tests for loose/moderate/strong style candidate generation with fake provider. `GenerateDraftRecordsStyleAttemptsForLooseModerateAndStrongCandidates` verifies the style context reaches the fake reference adapter, three candidates are returned, and persisted candidates can be audited.
- [x] Guardrail tests for no `SaveContent`. Existing candidate-only integration coverage and the reference-style browser workflow continue to assert the reference-anchor workflow does not call `SaveContent`.

**Implementation notes:**

- `ReferenceDraftStyleAttemptPayload` is intentionally structured and source-text-free. It records ids, feature names, requested intensity, fit thresholds, retrieval status, and risk/evidence labels only.
- `reference_draft_paragraph_candidates.style_attempts_json` is additive and defaults to `[]` for existing databases.
- `BlueprintDetail` displays compact style-attempt chips on generated candidates; the browser workflow asserts loose/moderate/strong attempts and exact bridge payloads.

**Dependencies:** Tasks 1-8.

**Estimated scope:** M.

## Task 10: Style and Source-Leak Audit

**Status:** Partially complete. Reuse audit and anchored draft audit now have deterministic source-leak foundations for non-L0/L1 candidates using n-gram overlap, candidate source coverage, and source-span concentration. Anchored draft audit applies stricter source-leak thresholds for beats whose style contract uses strong imitation, enforces selected-link `min_style_fit`, and compares candidates against persisted deterministic profile numeric features for supported style dimensions. Full style-quality taxonomy, UI surfacing, persisted readable reports, and the full style-intensity matrix remain open.

**Description:** Add a dedicated audit layer for high-fidelity imitation risk and quality.

**Acceptance criteria:**

- [ ] Audit computes exact phrase overlap, n-gram overlap, source-span concentration, candidate/source similarity, style-feature distance, factual drift, POV drift, and generic AI prose signals. Reuse audit currently covers n-gram overlap, source-span concentration, source coverage, rewrite level, simple fact drift, and generic AI prose phrase risk; anchored draft audit now reuses deterministic source-leak checks against selected material text and computes deterministic style-feature distance for supported baseline numeric dimensions (`dialogue_ratio`, `interiority_ratio`, `sensory_ratio`, `action_afterbeat_ratio`, `transition_ratio`, `hook_marker_ratio`, `punctuation_per_100_chars`, and `average_sentence_chars`).
- [x] Strong imitation mode has stricter source-leak thresholds for anchored draft audit when a beat style contract uses `imitation_intensity = "strong"`.
- [x] Audit fails candidates that are too close to a source span or too far from required style features. Reuse audit and anchored draft audit now fail non-L0/L1 near-copy candidates that exceed deterministic source-leak thresholds, and anchored draft audit fails candidates whose selected material falls below `min_style_fit` or whose observable style features are too far from persisted deterministic profile targets for supported dimensions.
- [ ] Audit report is readable in UI and persisted with candidate ids.

**Verification:**

- [x] Unit tests for overlap/similarity thresholds.
- [ ] Integration tests for L2/L3/L4 rewrite-risk escalation. L2 anchored-draft near-copy, strong-style source-leak tightening, and L3/L4 high-rewrite stops are covered; the full style-intensity matrix remains open.
- [x] Fixtures proving near-copy candidates fail even when rewrite level is otherwise allowed.
- [x] Fixtures proving near-copy candidates fail even when style fit is high for strong style contracts.
- [x] Fixtures proving low selected-link style fit and persisted-profile style-feature distance fail anchored draft audit.

**Dependencies:** Task 9.

**Estimated scope:** M.

## Task 11: Human Evaluation and Golden Style Fixtures

**Description:** Build a repeatable evaluation suite for style understanding and imitation quality.

**Acceptance criteria:**

- [ ] Golden fixtures cover dialogue-heavy, introspective, sensory, action, suspense, emotional restraint, high-tempo web-fiction, and slow-burn literary prose.
- [ ] Each fixture has expected advanced labels and acceptable feature ranges.
- [ ] Candidate evaluation uses a rubric for style fit, readability, originality distance, fact safety, POV safety, and usefulness to author.
- [ ] Evaluation reports are generated under `output/reference-style-eval/`.

**Verification:**

- [ ] `dotnet test ... --filter ReferenceStyle`
- [ ] `npm --prefix frontend run test:reference-style` or equivalent browser workflow after UI exists.

**Dependencies:** Tasks 3-10.

**Estimated scope:** M.

## Task 12: Product Workflow and UX

**Description:** Make advanced style anchoring usable without turning it into manual data plumbing.

**Acceptance criteria:**

- [ ] Default workflow suggests style profiles from available corpus by story need.
- [ ] Users can inspect why a profile/material was selected.
- [ ] Users can adjust imitation intensity and risk tolerance.
- [ ] Retrieval gaps and source-leak failures provide concrete next actions.
- [ ] Advanced controls stay available but the default path remains low-intervention.

**Verification:**

- [ ] Playwright coverage for default style-assisted run, profile suggestion, evidence inspection, intensity adjustment, and audit failure recovery.
- [ ] Usability report update focused on style anchoring friction.

**Dependencies:** Tasks 6-11.

**Estimated scope:** M.

## Task 13: Agent Tool Boundaries

**Status:** Partially complete. Agent-side `search_reference_materials` can now pass style profile ids, style dimensions, and imitation intensity into the existing read/search material boundary while keeping `novel_id` runtime-injected and source/license/visibility filtering in the service. Agent-side `get_reference_style_profiles` and `get_reference_style_profile` now provide read-only profile inspection with injected novel context and no source text. MAF integration coverage proves style filters do not bypass cross-novel profile or private workspace-corpus boundaries. Style audit inspection tools and the remaining allowed/forbidden style-tool matrix remain open.

**Description:** Expose style search, profile inspection, and style-audit inspection to agents without allowing agent-side import, approval, or insertion.

**Acceptance criteria:**

- [x] Agent tools can search style profiles/materials with injected novel context. The material-search side is implemented for style filters, and profile inspection is available through read-only list/detail tools.
- [ ] Agent tools cannot import sources, read arbitrary files, approve style contracts, approve blueprint revisions, or insert prose.
- [ ] Tool descriptions make source/license/style-risk boundaries explicit.

**Verification:**

- [x] MAF registry tests for style-aware reference material search filters, injected novel context, and absent style import/approval/insertion tools.
- [x] MAF registry tests for dedicated style profile inspection tools.
- [ ] Bridge/agent authority tests proving no bypass of user approvals. Material/profile visibility authority is covered at MAF executor integration level; style-audit inspection and the remaining approval matrix remain open.

**Dependencies:** Tasks 6-12.

**Estimated scope:** S.

## Task 14: Performance, Robustness, and Phase 14 Playwright Gate

**Description:** Add stress and regression coverage for large style corpora and high-fidelity style workflows.

**Acceptance criteria:**

- [ ] 10MB source builds baseline and advanced style profiles without white screen or unbounded memory growth.
- [ ] Profile build supports progress, cancellation, failure recovery, and resumable inspection.
- [ ] Material/profile search remains paged and responsive.
- [ ] The full style workflow is covered by Playwright with screenshots, bridge-call logs, console diagnostics, and traces.

**Verification:**

- [ ] `npm --prefix frontend run test:reference-style`
- [ ] `npm --prefix frontend run test:reference-style:stress`
- [ ] Existing Phase 13 matrix remains green.
- [ ] Full .NET tests pass for style-related services and contracts.

**Dependencies:** Tasks 1-13.

**Estimated scope:** M.

## Checkpoints

### Checkpoint A: Contracts and Storage

- [ ] Tasks 1-3 complete.
- [ ] Existing Phase 0-13 tests still pass.
- [ ] Deterministic style profile can be built without model configuration.

### Checkpoint B: Advanced Understanding

- [ ] Tasks 4-7 complete.
- [ ] LLM-assisted labels are grounded, validated, and optional.
- [ ] Retrieval uses style fit without weakening license, fact, or POV boundaries.

### Checkpoint C: Imitation and Audit

- [ ] Tasks 8-11 complete.
- [ ] Candidate generation is style-guided and beat-scoped.
- [ ] Style/source-leak audit can fail unsafe candidates.

### Checkpoint D: Product Gate

- [ ] Tasks 12-14 complete.
- [ ] Playwright style workflows and stress tests pass.
- [ ] Usability report shows no high-severity style workflow issues.

## Definition of Done

Phase 14 is complete only when all of the following are true:

- [ ] Advanced style profiles are stored, searchable, inspectable, and auditable.
- [ ] Multi-scale materials include scene/beat/dialogue/hook/payoff/style-evidence records, not only sentence and paragraph materials.
- [ ] Optional LLM-assisted analysis is schema-validated, source-grounded, and safe to disable.
- [ ] Style-aware retrieval works with current story context and does not require manual per-novel binding.
- [ ] Beat-level style contracts are reviewed, approved, hash-guarded, and invalidated by edits.
- [ ] Candidate generation respects style contracts while preserving fact, POV, provenance, and rewrite limits.
- [ ] Style/source-leak audit prevents near-copy outputs and records readable findings.
- [ ] UI supports profile build/inspect/compare/select, intensity controls, evidence inspection, and failure recovery.
- [ ] Agent tools remain read/search/inspect oriented and cannot import, approve, or insert.
- [ ] Full regression matrix passes:

```text
npm --prefix frontend run build
npm --prefix frontend run lint
npm --prefix frontend run test:reference-anchor
npm --prefix frontend run test:app
npm --prefix frontend run test:app:full
npm --prefix frontend run test:app:stress
npm --prefix frontend run test:app:usability
npm --prefix frontend run test:reference-style
npm --prefix frontend run test:reference-style:stress
dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --no-restore -v minimal -p:UseSharedCompilation=false
dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --no-restore -v minimal -p:UseSharedCompilation=false
```

## Risks and Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Model-generated style labels are wrong | Bad retrieval and poor imitation | Require source-span grounding, confidence thresholds, golden fixtures, and deterministic fallback |
| Candidate copies source too closely | Legal/product-quality risk | Add n-gram/source-span concentration audit before insertion |
| Style system becomes too manual | Low usability | Default profile suggestion and compact approval summary; advanced controls remain optional |
| Large corpora become slow | Poor author experience | Background jobs, progress, pagination, cancellation, and stress tests |
| Strong style imitation weakens facts/POV | Story inconsistency | Keep blueprint, fact, POV, and draft audit gates mandatory |

## Files Likely Touched

- `src/Novelist.Contracts/App/*Style*Payloads.cs`
- `src/Novelist.Core/App/*Style*Service.cs`
- `src/Novelist.Core/Bridge/*Style*BridgeHandlers.cs`
- `src/Novelist.Infrastructure/App/*Style*Service.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs`
- `src/Novelist.Agent/NovelistMafReferenceTools.cs`
- `frontend/src/lib/novelist/*`
- `frontend/src/components/reference/**/*`
- `frontend/src/views/**/*`
- `frontend/scripts/*style*.mjs`
- `tests/Novelist.Tests/**/*Style*Tests.cs`
- `tests/Novelist.IntegrationTests/**/*Style*Tests.cs`
- `docs/reference-anchor-implementation/*.md`
