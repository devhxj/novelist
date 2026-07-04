# Reference Anchor Layer Implementation Plan

## Status

Draft for implementation.

## Date

2026-07-04

## Scope

This document turns `docs/reference-anchor-layer-plan.md` into a codebase-grounded implementation plan for the current Novelist architecture.

The implementation must preserve the full design constraints from the design plan:

- immutable source corpus
- provenance on every material and candidate
- explicit L0-L4 rewrite levels
- bounded retrieval and pagination
- candidate audit before insertion
- no direct chapter mutation from reference-agent tools
- reviewed chapter narrative blueprint before prose generation
- explicit causality, emotion, POV, narration, role-state, scene-fact, and risk gates before drafting
- evaluation fixtures and regression tests before broad agent integration

This is not a simplification into plain RAG or a style prompt.

## Current Architecture Map

### Solution Layout

The current repository is a .NET 10 + Photino implementation:

```text
src/Novelist.Contracts/      JSON payload contracts shared by frontend bridge and services
src/Novelist.Core/           service interfaces, bridge dispatcher, bridge handler registration
src/Novelist.Infrastructure/ infrastructure implementations: file stores, SQLite, RAG, LLM, embeddings
src/Novelist.Agent/          Microsoft Agent Framework tool registry and chat tool executor
src/Novelist.App/            Photino desktop shell, local asset loading, manual service composition
frontend/src/                React/Vite UI with hand-owned bridge client
tests/Novelist.Tests/        unit and contract tests
tests/Novelist.IntegrationTests/ service, bridge, and host integration tests
```

Important dependency direction:

```text
Contracts <- Core <- Infrastructure
Contracts <- Core <- Agent
Contracts/Core/Infrastructure/Agent <- App
```

Reference-anchor code must follow this direction. Contracts cannot depend on Core, Infrastructure, or Agent.

### Runtime Composition

`src/Novelist.App/Desktop/PhotinoWindowFactory.cs` manually constructs services and registers bridge handlers. There is no central DI container for application services in desktop mode.

Current composition pattern:

```text
AppInitializationOptions
  -> FileSystemAppSettingsService
  -> FileSystemNovelService
  -> FileSystemChapterContentService
  -> FileSystemPreferenceService
  -> FileSystemWorldEntityService
  -> FileSystemPlanningService
  -> FileSystemEmbeddingSettingsService
  -> SqliteRagIndexService
  -> RagStoryMemorySearchService
  -> NovelistMafToolRegistry
  -> FileSystemChatSessionService
  -> BridgeDispatcher.Register...
```

Reference-anchor service must be instantiated in this factory and passed to both:

- `ReferenceAnchorBridgeHandlers`
- `NovelistMafToolRegistry`

The anchored drafting service must be instantiated after the reference-anchor service because it consumes:

- the immutable reference material bank;
- chapter content and planning services;
- world/timeline state used to prevent invented facts;
- the LLM configuration used to create structured blueprints and prose candidates.

It should be passed to:

- `ReferenceAnchoredDraftBridgeHandlers`
- `NovelistMafToolRegistry`

### Bridge Model

Frontend calls go through `frontend/src/lib/novelist/api.ts`, which wraps `BridgeDispatcher` requests as:

```json
{ "kind": "request", "id": "...", "method": "...", "payload": { "args": [] } }
```

Backend handlers are extension methods under `src/Novelist.Core/Bridge/`.

Existing tests enforce frontend/backend method parity:

- `tests/Novelist.Tests/Bridge/BridgeFrontendContractTests.cs`
- `tests/Novelist.Tests/Bridge/BridgeHandlerRegistrationTests.cs`

Adding methods requires updating all of:

- `BridgeCompatibilityAppMethods.MethodNames`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- bridge registration tests if expected counts or representative methods change

### Storage Model

The project uses two storage styles:

- JSON file stores for novels, chapters, world entities, planning, preferences, settings.
- SQLite + sqlite-vec for large semantic indexes in `SqliteRagIndexService`.

Reference-anchor storage is closer to RAG than to JSON stores because it must support:

- many source segments
- many derived materials
- vector search
- rebuild state
- provenance joins
- candidate/audit records

Therefore the implementation should use SQLite, not JSON.

### Existing RAG Reuse Points

Relevant files:

- `src/Novelist.Core/App/IRagIndexService.cs`
- `src/Novelist.Infrastructure/App/SqliteRagIndexService.cs`
- `src/Novelist.Infrastructure/App/SqliteVecProvisioning.cs`
- `src/Novelist.Infrastructure/App/RagStoryMemorySearchService.cs`
- `tests/Novelist.IntegrationTests/RagIndexServiceTests.cs`
- `tests/Novelist.IntegrationTests/StoryMemorySearchServiceTests.cs`

Reusable dependencies:

- `IEmbeddingConfigurationService`
- `IEmbeddingClient`
- `EmbeddingRequestOptions`
- `ISqliteVecTableProvisioner`
- `ISqliteVecQueryProvider`
- `SqliteVecProvisionRequest`
- `SqliteVecSearchRequest`

Do not reuse `RagChunkPayload` or `rag_chunks`; reference anchors need a separate schema and table namespace.

### Agent Tool Model

`NovelistMafToolRegistry` is partial:

- base registry in `NovelistMafToolRegistry.cs`
- structured writing tools in `NovelistMafStructuredTools.cs`
- web tools in `NovelistMafWebTools.cs`

The registry injects `NovelId` through `NovelistMafToolContext`. Tool schemas must not expose internal session fields unless intentionally part of the tool.

Reference tools should be added as a new partial file:

```text
src/Novelist.Agent/NovelistMafReferenceTools.cs
```

The agent tools should retrieve/adapt/audit materials and generate/review blueprint-gated draft candidates, but must not call `SaveContent`.

### Frontend Model

Workspace UI is organized through:

- `frontend/src/views/WorkspaceView.tsx`
- `frontend/src/components/shell/ActivityBar.tsx`
- `frontend/src/components/sidebar/SidePanel.tsx`
- feature folders under `frontend/src/components/`

Recommended frontend entry:

```text
frontend/src/components/reference-anchor/ReferenceAnchorView.tsx
frontend/src/components/reference-anchor/ReferenceAnchorList.tsx
frontend/src/components/reference-anchor/ReferenceMaterialSearch.tsx
frontend/src/components/reference-anchor/ReferenceCandidatePreview.tsx
```

Add a new activity id, for example `reference`, in `ActivityBar`. Render the main view from `WorkspaceView`. A small list/filter panel can be added to `SidePanel` later, but the first UI can be a full main panel.

## Implementation Decisions

### Storage Location

Use a dedicated database:

```text
{appData}/reference-anchor/index.sqlite
```

Rationale:

- keeps reference materials separate from story memory RAG
- allows different schema migrations and build state
- avoids mixing rowids with `rag_chunks`
- keeps sqlite-vec table names scoped to reference anchors

### Chapter Narrative Blueprint Layer

Add a mandatory chapter narrative blueprint layer between chapter planning and prose generation.

Do not name this artifact "script" in code or UI. A script-like artifact tends to bias generation toward action/dialogue beats and away from novelistic narration. The intended artifact is a structured prose-generation contract:

```text
chapter plan / timeline / world state / previous chapter state
  -> Chapter Narrative Blueprint
  -> Blueprint Review Gate
  -> Reference Material Binding
  -> Paragraph Candidate Generation
  -> Draft Audit
  -> user copy/manual insertion
```

The blueprint is not a detailed outline. It must contain all important features needed to write the chapter without relying on model intuition:

- chapter function in the whole story;
- causality chain from previous chapter state to final hook;
- per-character emotional trajectory;
- role-state changes and relationship pressure;
- POV character and narrative distance;
- scene facts that may be used;
- forbidden facts that must not be introduced;
- paragraph/beat-level narrative duty;
- reference material query plan for each beat;
- max allowed rewrite level for each material use;
- risk flags for AI-like prose, fake emotion, screenplay drift, and hard transitions.

Rationale:

- **Decomposition:** current LLMs perform poorly when asked to solve plot logic, emotional continuity, viewpoint control, style matching, and prose execution in one pass. A blueprint separates reasoning from prose.
- **Checkability:** causality, emotion triggers, POV knowledge, and scene facts can be reviewed before any prose is written.
- **Hallucination containment:** the blueprint establishes a bounded fact set and explicit forbidden facts before paragraph generation.
- **Emotion realism:** the blueprint forces every emotional change to have a trigger, internal state, external evidence, and narrative expression plan.
- **Novelistic narration:** every beat must declare whether it is action, reaction, interiority, environment, transition, information reveal, or hook, preventing the output from collapsing into dialogue/action-only screenplay form.
- **Reference anchoring:** retrieved sentences and passages are bound to a beat's narrative function, not pasted by superficial semantic similarity.

The first implementation may use an LLM to generate the blueprint, but the review gate must be a separate operation with deterministic checks. A failed review must block prose generation until the blueprint is revised and reviewed again.

### Service Boundary

Add the main interface in:

```text
src/Novelist.Core/App/IReferenceAnchorService.cs
```

The interface should expose stable operations, not implementation internals:

```csharp
public interface IReferenceAnchorService
{
    ValueTask<ReferenceAnchorPayload> CreateAnchorAsync(
        CreateReferenceAnchorPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
        long novelId,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchorBuildStatusPayload> RebuildAnchorAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchorBuildStatusPayload?> GetBuildStatusAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken);

    ValueTask<PageResultPayload<ReferenceMaterialPayload>> SearchMaterialsAsync(
        SearchReferenceMaterialsPayload input,
        CancellationToken cancellationToken);

    ValueTask<AdaptReferenceMaterialResultPayload> AdaptMaterialAsync(
        AdaptReferenceMaterialPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceReuseAuditPayload> AuditCandidateAsync(
        AuditReferenceReusePayload input,
        CancellationToken cancellationToken);

    ValueTask DeleteAnchorAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken);
}
```

If `PageResultPayload<T>` cannot be reused cleanly from existing session contracts, add a generic or reference-specific paged result in `ReferenceAnchorPayloads.cs`.

Add a second service for blueprint-gated drafting:

```text
src/Novelist.Core/App/IReferenceAnchoredDraftService.cs
```

This service consumes reference materials but does not own import/indexing:

```csharp
public interface IReferenceAnchoredDraftService
{
    ValueTask<ReferenceChapterBlueprintPayload> GenerateChapterBlueprintAsync(
        GenerateReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ReferenceChapterBlueprintSummaryPayload>> GetChapterBlueprintsAsync(
        long novelId,
        int? chapterNumber,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterBlueprintPayload?> GetChapterBlueprintAsync(
        long novelId,
        long blueprintId,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterBlueprintReviewPayload> ReviewChapterBlueprintAsync(
        ReviewReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceChapterBlueprintPayload> ApproveChapterBlueprintAsync(
        ApproveReferenceChapterBlueprintPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceBlueprintMaterialBindingResultPayload> BindBlueprintMaterialsAsync(
        BindReferenceBlueprintMaterialsPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchoredDraftPayload> GenerateDraftFromBlueprintAsync(
        GenerateReferenceAnchoredDraftPayload input,
        CancellationToken cancellationToken);

    ValueTask<ReferenceAnchoredDraftAuditPayload> AuditDraftAgainstBlueprintAsync(
        AuditReferenceAnchoredDraftPayload input,
        CancellationToken cancellationToken);
}
```

Important boundary rules:

- `GenerateDraftFromBlueprintAsync` must reject unreviewed or failed blueprints.
- The drafting service returns candidates only; it must not call `SaveContent`.
- Inputs should use `novel_id` and `chapter_number` as the stable public targeting model. `ChapterPayload.Id` exists, but chapter-number based workflows already exist in planning, RAG, and workspace utilities.
- Blueprint records may reference the current `ChapterPlanPayload.Scope` and a hash of `ChapterPlanPayload.Content`, but must remain valid even when a chapter file does not exist yet.

### Import Semantics

Phase 1 import should support only `.txt` and `.md`.

`CreateReferenceAnchorPayload` should accept a user-provided local source path:

```csharp
public sealed record CreateReferenceAnchorPayload(
    long NovelId,
    string Title,
    string? Author,
    string SourcePath,
    string SourceKind,
    string LicenseStatus);
```

Validation:

- `NovelId > 0`
- title non-empty, max 200 chars
- source path non-empty, max 1024 chars
- source file must exist
- source extension must be `.txt` or `.md`
- source file max size should be capped in phase 1, for example 20 MB
- license status must be an allowed enum string

The service should read the source once and persist immutable source segments. It should not let agent tools read arbitrary source paths later.

### Build Pipeline

Use a synchronous implementation first, but model the result as a build status. The UI can later poll the same status without changing contracts.

Pipeline stages:

```text
created
importing
source_imported
segmenting
segments_built
extracting_materials
materials_extracted
detecting_slots
slots_detected
embedding
ready
```

Failure states:

```text
failed_import
failed_segmenting
failed_extraction
failed_slotting
failed_embedding
cancelled
stale
```

Use stage-level transactions. Do not keep one transaction open for the whole book.

### Blueprint Generation Pipeline

Blueprint generation is separate from source import. It runs per target chapter:

```text
context_pack
  -> blueprint_generated
  -> deterministic_review
  -> material_binding
  -> approved
  -> draft_generation
  -> draft_audit
```

Blueprint states:

```text
draft
review_failed
review_passed
approved
stale
used_for_candidate
superseded
```

Context pack inputs:

- current novel id and chapter number;
- current chapter plan scope/content, if available;
- previous chapter summary/content excerpt, bounded by token budget;
- unresolved timeline entries and active story arcs for this chapter;
- relevant world entities and role states;
- user-supplied chapter goal, if provided;
- active reference anchor ids and material search filters.

The context pack must also include an explicit "known facts" and "forbidden facts" list. Blueprints must be reviewed against these lists before prose generation.

### Blueprint Payload Shape

The payload should be structured, not free-form prose:

```text
ReferenceChapterBlueprintPayload
- blueprint_id
- novel_id
- chapter_number
- title
- status
- source_plan_scope
- source_plan_hash
- context_hash
- primary_anchor_id
- chapter_function
- previous_state
- final_state
- final_hook
- global_pov
- global_narrative_distance
- known_facts
- forbidden_facts
- risk_flags
- beats
- latest_review
- created_at
- updated_at
```

Each beat must be structured:

```text
ReferenceChapterBlueprintBeatPayload
- beat_id
- beat_index
- scene_index
- beat_type
- narrative_function
- causality_in
- causality_out
- pov_character
- narrative_distance
- viewpoint_allowed_knowledge
- character_states_before
- character_states_after
- emotion_trigger
- emotion_before
- emotion_after
- external_evidence
- narration_strategy
- scene_facts
- forbidden_facts
- reference_query
- required_material_types
- max_rewrite_level
- prose_duties
- risk_flags
```

The `prose_duties` field is important. It prevents screenplay drift by forcing each beat to declare whether the final prose needs interiority, sensory detail, transition, reaction, subtext, environmental pressure, or information reveal.

### Blueprint Review Strategy

Blueprint review is a gate, not a comment generator. It must return pass/fail plus concrete defects:

```text
ReferenceChapterBlueprintReviewPayload
- review_id
- blueprint_id
- status
- score
- causality_errors
- emotion_errors
- pov_errors
- continuity_errors
- forbidden_fact_errors
- reference_binding_errors
- screenplay_drift_risks
- ai_prose_risks
- required_fixes
- reviewed_at
```

Initial deterministic checks:

- every beat except the first has `causality_in`;
- every beat except the last has `causality_out`;
- every emotional change has a trigger and external evidence;
- POV knowledge does not include facts outside the current viewpoint;
- forbidden facts do not appear in beat facts or final hook;
- every prose-generation beat has a reference query and max rewrite level;
- no beat is dialogue/action-only unless intentionally marked as a short exchange;
- final hook follows from earlier beat state instead of appearing as a new fact.

LLM review can be added as a second pass, but deterministic review must decide whether drafting is allowed.

### Vector Table Naming

Do not use `SqliteVecTableProvisioner.BuildVectorTableName(long novelId, int dimensions)` directly because it creates story-memory names like `vec_novel_1_1536`.

Add a reference-anchor specific helper, for example inside `SqliteReferenceAnchorService`:

```text
vec_reference_anchor_{anchorId}_{dimensions}
```

Validate the generated identifier with the same simple identifier rule used by `SqliteVecTableProvisioner.BuildCreateTableSql`.

### Material Extraction Strategy

Initial material extraction should be deterministic for the core corpus:

- chapter segments
- paragraph segments
- sentence segments
- simple passage windows

For sentence bank and passage bank, use rule-based first-pass tags:

- punctuation and dialogue quote detection
- paragraph length
- sentence position in paragraph/chapter
- contains dialogue marker
- contains action verbs or sensory nouns from a small local list
- connector patterns
- silence/hesitation/action-afterbeat patterns
- narrative-duty compatibility with blueprint beats
- emotion-trigger and external-evidence compatibility
- POV/narrative-distance compatibility

LLM-assisted tagging can be added behind a separate extractor interface, but the first storage and pipeline should not depend on LLM availability.

Recommended extractor interfaces in Infrastructure:

```csharp
internal interface IReferenceTextSegmenter { ... }
internal interface IReferenceMaterialExtractor { ... }
internal interface IReferenceSlotDetector { ... }
internal interface IReferenceCandidateAuditor { ... }
```

Keep these internal until the abstractions prove stable.

### Adaptation Strategy

For chapter drafting, adaptation should be performed against a reviewed blueprint beat, not against a raw user prompt. The beat supplies:

- narrative function;
- scene facts;
- allowed and forbidden knowledge;
- target emotion transition;
- required prose duty;
- reference material id and max rewrite level.

Implement L1 before L2.

L1:

- replacement only through declared slots
- no model call required
- changed slots recorded
- locked phrases must remain

L2:

- allow small connector and agreement edits
- every non-slot edit must be reported
- if non-slot edit count or similarity delta exceeds threshold, classify as L3 and fail unless explicitly allowed

L3/L4:

- L3 may return candidate with warning but should not pass unless requested
- L4 disabled

### Audit Strategy

Audit is not optional. It is a pure service operation and should run inside `AdaptMaterialAsync` before returning the candidate.

Initial deterministic checks:

- source/material/candidate provenance exists
- source hash still matches
- candidate links to an approved blueprint and beat when generated for chapter drafting
- blueprint review status is still valid for the current chapter-plan hash
- candidate facts are a subset of blueprint beat facts plus declared slot values
- candidate preserves the beat POV and narrative distance
- candidate satisfies the beat's prose duty rather than only restating plot action
- rewrite level within input max
- L1 changed only slots
- locked phrases preserved for L1/L2
- adapted candidate is non-empty and below max output length
- simple unsupported fact detection via new proper nouns/numbers/object-like tokens compared to slot values and scene facts
- high-risk AI phrase list

LLM-assisted audit can be a second pass later, but deterministic audit gates must exist first.

## Database Schema Plan

Create schema in `SqliteReferenceAnchorService.EnsureSchemaAsync`.

Tables:

```text
reference_anchors
reference_anchor_build_state
reference_source_segments
reference_materials
reference_material_slots
reference_material_scores
reference_reuse_candidates
reference_reuse_audits
reference_user_feedback
reference_chapter_blueprints
reference_chapter_blueprint_beats
reference_chapter_blueprint_reviews
reference_blueprint_material_links
reference_draft_paragraph_candidates
reference_draft_audits
```

Minimum columns:

```text
reference_anchors
- anchor_id INTEGER PRIMARY KEY
- novel_id INTEGER NOT NULL
- title TEXT NOT NULL
- author TEXT NOT NULL
- source_path TEXT NOT NULL
- source_kind TEXT NOT NULL
- license_status TEXT NOT NULL
- source_file_hash TEXT NOT NULL
- build_version TEXT NOT NULL
- status TEXT NOT NULL
- created_at TEXT NOT NULL
- updated_at TEXT NOT NULL

reference_source_segments
- segment_id TEXT PRIMARY KEY
- anchor_id INTEGER NOT NULL
- chapter_index INTEGER NOT NULL
- chapter_title TEXT NOT NULL
- segment_type TEXT NOT NULL
- segment_index INTEGER NOT NULL
- parent_segment_id TEXT NOT NULL
- start_offset INTEGER NOT NULL
- end_offset INTEGER NOT NULL
- text TEXT NOT NULL
- text_hash TEXT NOT NULL

reference_materials
- material_id TEXT PRIMARY KEY
- anchor_id INTEGER NOT NULL
- source_segment_id TEXT NOT NULL
- material_type TEXT NOT NULL
- function_tag TEXT NOT NULL
- emotion_tag TEXT NOT NULL
- scene_tag TEXT NOT NULL
- pov_tag TEXT NOT NULL
- technique_tag TEXT NOT NULL
- function_confidence REAL NOT NULL
- emotion_confidence REAL NOT NULL
- pov_confidence REAL NOT NULL
- text TEXT NOT NULL
- source_hash TEXT NOT NULL
- extractor_version TEXT NOT NULL
- user_verified INTEGER NOT NULL
- created_at TEXT NOT NULL

reference_chapter_blueprints
- blueprint_id INTEGER PRIMARY KEY
- novel_id INTEGER NOT NULL
- chapter_number INTEGER NOT NULL
- primary_anchor_id INTEGER NOT NULL
- title TEXT NOT NULL
- status TEXT NOT NULL
- source_plan_scope TEXT NOT NULL
- source_plan_hash TEXT NOT NULL
- context_hash TEXT NOT NULL
- chapter_function TEXT NOT NULL
- previous_state TEXT NOT NULL
- final_state TEXT NOT NULL
- final_hook TEXT NOT NULL
- global_pov TEXT NOT NULL
- global_narrative_distance TEXT NOT NULL
- known_facts_json TEXT NOT NULL
- forbidden_facts_json TEXT NOT NULL
- risk_flags_json TEXT NOT NULL
- build_version TEXT NOT NULL
- created_at TEXT NOT NULL
- updated_at TEXT NOT NULL
- approved_at TEXT NOT NULL

reference_chapter_blueprint_beats
- beat_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- beat_index INTEGER NOT NULL
- scene_index INTEGER NOT NULL
- beat_type TEXT NOT NULL
- narrative_function TEXT NOT NULL
- causality_in TEXT NOT NULL
- causality_out TEXT NOT NULL
- pov_character TEXT NOT NULL
- narrative_distance TEXT NOT NULL
- viewpoint_allowed_knowledge_json TEXT NOT NULL
- character_states_before_json TEXT NOT NULL
- character_states_after_json TEXT NOT NULL
- emotion_trigger TEXT NOT NULL
- emotion_before TEXT NOT NULL
- emotion_after TEXT NOT NULL
- external_evidence TEXT NOT NULL
- narration_strategy TEXT NOT NULL
- scene_facts_json TEXT NOT NULL
- forbidden_facts_json TEXT NOT NULL
- reference_query_json TEXT NOT NULL
- required_material_types_json TEXT NOT NULL
- max_rewrite_level TEXT NOT NULL
- prose_duties_json TEXT NOT NULL
- risk_flags_json TEXT NOT NULL

reference_chapter_blueprint_reviews
- review_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- status TEXT NOT NULL
- score REAL NOT NULL
- causality_errors_json TEXT NOT NULL
- emotion_errors_json TEXT NOT NULL
- pov_errors_json TEXT NOT NULL
- continuity_errors_json TEXT NOT NULL
- forbidden_fact_errors_json TEXT NOT NULL
- reference_binding_errors_json TEXT NOT NULL
- screenplay_drift_risks_json TEXT NOT NULL
- ai_prose_risks_json TEXT NOT NULL
- required_fixes_json TEXT NOT NULL
- reviewed_at TEXT NOT NULL

reference_blueprint_material_links
- link_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- beat_id TEXT NOT NULL
- material_id TEXT NOT NULL
- intended_use TEXT NOT NULL
- max_rewrite_level TEXT NOT NULL
- selected INTEGER NOT NULL
- score REAL NOT NULL
- created_at TEXT NOT NULL

reference_draft_paragraph_candidates
- candidate_id TEXT PRIMARY KEY
- blueprint_id INTEGER NOT NULL
- beat_id TEXT NOT NULL
- material_id TEXT NOT NULL
- rewrite_level TEXT NOT NULL
- text TEXT NOT NULL
- changed_slots_json TEXT NOT NULL
- non_slot_edits_json TEXT NOT NULL
- audit_status TEXT NOT NULL
- created_at TEXT NOT NULL

reference_draft_audits
- audit_id TEXT PRIMARY KEY
- candidate_id TEXT NOT NULL
- blueprint_id INTEGER NOT NULL
- beat_id TEXT NOT NULL
- status TEXT NOT NULL
- rewrite_level TEXT NOT NULL
- provenance_errors_json TEXT NOT NULL
- blueprint_errors_json TEXT NOT NULL
- unsupported_fact_errors_json TEXT NOT NULL
- pov_errors_json TEXT NOT NULL
- ai_prose_risks_json TEXT NOT NULL
- required_fixes_json TEXT NOT NULL
- audited_at TEXT NOT NULL
```

Add indexes:

```text
idx_reference_anchors_novel
idx_reference_segments_anchor_type
idx_reference_materials_anchor_type
idx_reference_materials_tags
idx_reference_candidates_material
idx_reference_blueprints_novel_chapter
idx_reference_blueprint_beats_blueprint
idx_reference_blueprint_reviews_blueprint
idx_reference_blueprint_links_beat
idx_reference_draft_candidates_blueprint
```

Enable foreign key enforcement on every SQLite connection:

```sql
PRAGMA foreign_keys = ON;
```

The current RAG service does not enable foreign keys because it has only two flat tables. Reference-anchor storage has real parent/child integrity and should enable it.

## Bridge API Plan

Add:

```text
CreateReferenceAnchor
GetReferenceAnchors
DeleteReferenceAnchor
RebuildReferenceAnchor
GetReferenceAnchorBuildStatus
SearchReferenceMaterials
AdaptReferenceMaterial
AuditReferenceReuse
GenerateReferenceChapterBlueprint
GetReferenceChapterBlueprints
GetReferenceChapterBlueprint
ReviewReferenceChapterBlueprint
ApproveReferenceChapterBlueprint
BindReferenceBlueprintMaterials
GenerateReferenceAnchoredDraft
AuditReferenceAnchoredDraft
```

Implementation files:

```text
src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs
src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs
src/Novelist.Core/App/IReferenceAnchorService.cs
src/Novelist.Core/App/IReferenceAnchoredDraftService.cs
src/Novelist.Core/Bridge/ReferenceAnchorBridgeHandlers.cs
src/Novelist.Core/Bridge/ReferenceAnchoredDraftBridgeHandlers.cs
src/Novelist.Core/Bridge/BridgeCompatibilityAppMethods.cs
```

Handler pattern should mirror `WorkspaceUtilityBridgeHandlers`:

- parse args array
- deserialize object payloads through `BridgeJson.SerializerOptions`
- validate primitive ids at boundary
- throw `BridgeValidationException` for shape issues
- let service-level `ArgumentException` map to validation errors
- return structured payloads

## Desktop Composition Plan

Update `src/Novelist.App/Desktop/PhotinoWindowFactory.cs`.

Construct after embedding/RAG dependencies:

```csharp
var referenceAnchorService = new SqliteReferenceAnchorService(
    appOptions,
    novelService,
    embeddingService,
    embeddingClient,
    sqliteVecProvider,
    sqliteVecProvider);

var referenceAnchoredDraftService = new SqliteReferenceAnchoredDraftService(
    appOptions,
    referenceAnchorService,
    chapterContentService,
    planningService,
    worldService,
    llmService);
```

Then:

```csharp
var chatToolExecutor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
    storyMemoryService,
    chapterContentService,
    approvalCoordinator,
    eventSink,
    subagentRunner,
    preferenceService,
    worldService,
    planningService,
    webFetchService,
    webSearchService,
    referenceAnchorService,
    referenceAnchoredDraftService));
```

And:

```csharp
.RegisterReferenceAnchorHandlers(referenceAnchorService)
.RegisterReferenceAnchoredDraftHandlers(referenceAnchoredDraftService)
```

The exact constructor should be added in a backward-compatible way:

- keep existing constructor signatures working for tests
- add optional `IReferenceAnchorService? referenceAnchors = null` parameter near the end
- add optional `IReferenceAnchoredDraftService? referenceDrafts = null` after reference anchors

## Agent Tool Plan

Add optional dependency to `NovelistMafToolRegistry`:

```csharp
private readonly IReferenceAnchorService? _referenceAnchors;
private readonly IReferenceAnchoredDraftService? _referenceDrafts;
```

Add `NovelistMafReferenceTools.cs` with tools:

```text
get_reference_anchors
search_reference_materials
adapt_reference_material
audit_reference_reuse
```

Add `NovelistMafReferenceDraftTools.cs` with blueprint-gated drafting tools:

```text
generate_reference_chapter_blueprint
review_reference_chapter_blueprint
approve_reference_chapter_blueprint
bind_reference_blueprint_materials
generate_reference_anchored_draft
audit_reference_anchored_draft
```

Tool limits:

- `search_reference_materials`: max page size 20
- `adapt_reference_material`: requires `material_id`, `slot_values`, `max_rewrite_level`
- `audit_reference_reuse`: pure check only
- `generate_reference_chapter_blueprint`: requires `chapter_number`, optional user chapter goal, and active anchor ids
- `review_reference_chapter_blueprint`: pure check only; must not revise the blueprint silently
- `approve_reference_chapter_blueprint`: allowed only after a passing review
- `generate_reference_anchored_draft`: requires an approved `blueprint_id`; returns candidates only
- `audit_reference_anchored_draft`: pure check only
- no `SaveContent`
- no direct file path reads

Tool schemas must not expose `novel_id`, `session_id`, `turn_id`, or `tool_id`.

Agent workflow order must be enforced in tool descriptions and service validation:

```text
search/reference context
  -> generate_reference_chapter_blueprint
  -> review_reference_chapter_blueprint
  -> approve_reference_chapter_blueprint
  -> bind_reference_blueprint_materials
  -> generate_reference_anchored_draft
  -> audit_reference_anchored_draft
```

## Frontend Plan

Update API/type layer:

```text
frontend/src/lib/novelist/types.ts
frontend/src/lib/novelist/api.ts
frontend/src/hooks/useApp.ts
```

Add namespace:

```ts
export namespace reference {
  export interface Anchor { ... }
  export interface BuildStatus { ... }
  export interface Material { ... }
  export interface SearchMaterialsInput { ... }
  export interface ReuseCandidate { ... }
  export interface ReuseAudit { ... }
  export interface ChapterBlueprint { ... }
  export interface ChapterBlueprintBeat { ... }
  export interface ChapterBlueprintReview { ... }
  export interface AnchoredDraft { ... }
  export interface AnchoredDraftAudit { ... }
}
```

Add view components:

```text
frontend/src/components/reference-anchor/ReferenceAnchorView.tsx
frontend/src/components/reference-anchor/ReferenceAnchorImportDialog.tsx
frontend/src/components/reference-anchor/ReferenceMaterialSearch.tsx
frontend/src/components/reference-anchor/ReferenceCandidatePreview.tsx
frontend/src/components/reference-anchor/ReferenceChapterBlueprintView.tsx
frontend/src/components/reference-anchor/ReferenceBlueprintBeatEditor.tsx
frontend/src/components/reference-anchor/ReferenceBlueprintReviewPanel.tsx
frontend/src/components/reference-anchor/ReferenceAnchoredDraftPreview.tsx
```

Update:

```text
frontend/src/components/shell/ActivityBar.tsx
frontend/src/views/WorkspaceView.tsx
```

UI first version:

- list anchors for active novel
- create anchor from local `.txt`/`.md` path
- rebuild anchor
- show build state and counts
- search material bank with filters
- preview source/adapted/diff/audit
- generate a chapter blueprint for a selected `chapter_number`
- show blueprint beats, causality chain, emotion trajectory, POV constraints, forbidden facts, and prose duties
- run blueprint review and block draft generation until it passes
- bind reference materials to blueprint beats
- generate draft candidates only from approved blueprints
- show draft text alongside source material, blueprint beat, rewrite level, and audit result
- copy candidate; no automatic insertion in phase 1

If direct file picker is needed, add it as a separate runtime capability later. Do not block backend implementation on an open-file native dialog.

## Implementation Task Breakdown

### Phase 0: Contract and Test Fixture Foundation

**Description:** Add contracts, enum constants, benchmark fixture format, and tests for payload serialization.

**Acceptance criteria:**

- [ ] `ReferenceAnchorPayloads.cs` compiles.
- [ ] `ReferenceAnchoredDraftPayloads.cs` compiles.
- [ ] `IReferenceAnchorService.cs` compiles.
- [ ] `IReferenceAnchoredDraftService.cs` compiles.
- [ ] JSON property names match frontend snake_case.
- [ ] Rewrite level constants, build states, blueprint states, beat types, and review statuses are documented in code or tests.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge`

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs`
- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchorService.cs`
- `src/Novelist.Core/App/IReferenceAnchoredDraftService.cs`
- `tests/Novelist.Tests/`

### Phase 1: SQLite Store and Source Import

**Description:** Implement anchor creation, source file validation, immutable source segmentation, and build status persistence.

**Acceptance criteria:**

- [ ] Create anchor validates novel id and source file.
- [ ] TXT/MD source is split into chapter/paragraph/sentence segments.
- [ ] Segment ids and hashes are stable across rebuilds.
- [ ] Rebuild is idempotent for unchanged source.
- [ ] Failed import records a failed status with a redacted error.

**Verification:**

- [ ] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter ReferenceAnchor`

**Files likely touched:**

- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs`
- `tests/Novelist.IntegrationTests/ReferenceAnchorServiceTests.cs`

### Phase 2: Material Extraction and Slots

**Description:** Build sentence and passage material banks with deterministic tags, conservative slots, and blueprint-usable narrative metadata.

**Acceptance criteria:**

- [ ] Material rows point to valid source segments.
- [ ] Function/material tags exist with confidence fields.
- [ ] Emotion, POV, technique, narrative-duty, and external-evidence tags exist for blueprint matching.
- [ ] Slots are stored separately and tied to material ids.
- [ ] Locked phrases survive L1 adaptation.
- [ ] User corrections can be represented even if UI arrives later.

**Verification:**

- [ ] extractor unit tests for Chinese punctuation/dialogue/paragraph cases
- [ ] extractor unit tests for emotion-trigger, narrative-duty, and POV tag cases
- [ ] integration test verifies material counts and provenance joins

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- internal extractor classes in `src/Novelist.Infrastructure/App/`
- `tests/Novelist.Tests/` extractor tests

### Phase 3: Hybrid Search and Blueprint Material Matching

**Description:** Add paginated material search with tag filters, optional embeddings, and score components usable by blueprint beat binding.

**Acceptance criteria:**

- [ ] Search works without embedding configuration using lexical/tag ranking.
- [ ] Search records score components.
- [ ] If embedding config exists, vectors are provisioned in reference-specific vec tables.
- [ ] Missing sqlite-vec returns a recoverable status.
- [ ] Results are bounded and paginated.
- [ ] Search can filter by narrative duty, emotion transition, POV, technique, and material type.
- [ ] Beat-level material matching returns ranked candidates without selecting them automatically unless requested.

**Verification:**

- [ ] fake embedding client test
- [ ] fake sqlite-vec provisioner test
- [ ] search filter tests
- [ ] beat-to-material ranking tests

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- possibly shared sqlite-vec table-name helper
- `ReferenceAnchorServiceTests.cs`

### Phase 4: Chapter Narrative Blueprint and Review Gate

**Description:** Implement structured chapter blueprint generation, deterministic review, approval, and material binding. This phase must land before any chapter drafting tool.

**Acceptance criteria:**

- [ ] Blueprint generation targets `novel_id` and `chapter_number`.
- [ ] Blueprint stores chapter function, causality chain, emotion trajectory, POV constraints, scene facts, forbidden facts, prose duties, and beat-level reference queries.
- [ ] Review fails blueprints with missing causality, unsupported emotional shifts, POV knowledge leaks, forbidden facts, or screenplay drift risks.
- [ ] Approved status requires a passing review.
- [ ] Changing the source chapter plan hash marks existing blueprints stale.
- [ ] Material binding links candidate reference materials to beats with max rewrite levels.

**Verification:**

- [ ] unit tests for blueprint payload serialization
- [ ] unit tests for deterministic blueprint review rules
- [ ] integration test for generate/review/approve/stale lifecycle
- [ ] integration test for beat material binding and provenance joins

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchoredDraftPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchoredDraftService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchoredDraftService.cs`
- `tests/Novelist.Tests/ReferenceChapterBlueprint*Tests.cs`
- `tests/Novelist.IntegrationTests/ReferenceAnchoredDraftServiceTests.cs`

### Phase 5: Controlled Adaptation, Draft Generation, and Audit

**Description:** Implement L1/L2 material adaptation, paragraph candidate generation from approved blueprints, and deterministic draft audit.

**Acceptance criteria:**

- [ ] `AdaptMaterialAsync` still supports standalone preview/audit.
- [ ] Draft generation rejects missing, failed, stale, or unapproved blueprints.
- [ ] L1 changes only declared slots.
- [ ] L2 reports non-slot edits.
- [ ] L3 is classified and blocked unless requested.
- [ ] L4 cannot pass.
- [ ] Missing provenance fails audit.
- [ ] Unsupported new facts fail audit.
- [ ] Draft candidates preserve beat POV, narrative distance, scene facts, forbidden facts, and prose duties.
- [ ] Draft service returns candidates only and never mutates chapter content.

**Verification:**

- [ ] unit tests for rewrite-level classifier
- [ ] unit tests for unsupported fact detection
- [ ] unit tests for blueprint-to-draft audit rules
- [ ] integration test for `AdaptMaterialAsync`
- [ ] integration test for `GenerateDraftFromBlueprintAsync`

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- `SqliteReferenceAnchoredDraftService.cs`
- internal adaptation/audit classes
- `tests/Novelist.Tests/ReferenceAnchor*Tests.cs`
- `tests/Novelist.Tests/ReferenceAnchoredDraft*Tests.cs`

### Phase 6: Bridge Integration

**Description:** Expose reference-anchor and blueprint-gated drafting operations through the Photino bridge.

**Acceptance criteria:**

- [ ] All reference-anchor bridge methods route to service operations.
- [ ] All reference-anchored draft bridge methods route to service operations.
- [ ] Invalid payloads return stable `VALIDATION_ERROR`.
- [ ] app-not-initialized and invalid path errors use existing bridge semantics.
- [ ] Draft generation through bridge fails for unapproved blueprints.
- [ ] Frontend/backend method registry test passes.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge`
- [ ] integration test dispatches representative reference-anchor requests
- [ ] integration test dispatches representative reference-anchored draft requests

**Files likely touched:**

- `ReferenceAnchorBridgeHandlers.cs`
- `ReferenceAnchoredDraftBridgeHandlers.cs`
- `BridgeCompatibilityAppMethods.cs`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- bridge tests

### Phase 7: Desktop and Agent Wiring

**Description:** Instantiate services in Photino desktop composition and pass them to bridge and agent registry.

**Acceptance criteria:**

- [ ] Desktop service graph compiles.
- [ ] Existing constructor tests still pass.
- [ ] No existing tool disappears.
- [ ] Reference material tools are absent when service is null and present when configured.
- [ ] Reference draft tools are absent when draft service is null and present when configured.
- [ ] Agent tools enforce blueprint workflow order and cannot call `SaveContent`.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter MafToolRegistry`
- [ ] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter Photino`

**Files likely touched:**

- `PhotinoWindowFactory.cs`
- `NovelistMafToolRegistry.cs`
- `NovelistMafReferenceTools.cs`
- `NovelistMafReferenceDraftTools.cs`
- `MafToolRegistryTests.cs`

### Phase 8: Frontend Workflow

**Description:** Add UI for anchors, search, blueprint generation/review, material binding, draft candidate preview, and audit.

**Acceptance criteria:**

- [ ] ActivityBar has a reference-anchor entry.
- [ ] WorkspaceView renders ReferenceAnchorView for the active novel.
- [ ] User can create/rebuild anchor and search materials.
- [ ] Candidate preview shows source, adapted text, rewrite level, changed slots, audit warnings.
- [ ] User can generate and inspect a chapter blueprint for a chapter number.
- [ ] Review panel shows causality, emotion, POV, continuity, forbidden-fact, reference-binding, screenplay-drift, and AI-prose defects.
- [ ] Draft generation button is disabled until blueprint review passes and approval exists.
- [ ] Draft preview shows source material, blueprint beat, rewrite level, changed slots, and audit warnings.
- [ ] No automatic chapter insertion.

**Verification:**

- [ ] `cd frontend && npm run build`
- [ ] `cd frontend && npm run lint`

**Files likely touched:**

- `frontend/src/components/reference-anchor/*`
- `ActivityBar.tsx`
- `WorkspaceView.tsx`
- `api.ts`
- `types.ts`

### Phase 9: Feedback Loop and Hardening

**Description:** Store user decisions and use them to improve ranking, blueprint review, and regression tests.

**Acceptance criteria:**

- [ ] User feedback rows persist accept/reject/edit decisions.
- [ ] User-verified tags can override extractor tags.
- [ ] User-edited blueprint beats can be re-reviewed and approved.
- [ ] Regression fixtures include previously bad blueprints and candidates.
- [ ] Rebuild preserves user corrections where source segment hash is unchanged.
- [ ] Ranking can boost materials previously accepted for similar blueprint beats.

**Verification:**

- [ ] integration tests for feedback persistence
- [ ] ranking test for user-verified boost
- [ ] blueprint regression fixture tests

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

Targeted new tests:

```text
tests/Novelist.Tests/ReferenceAnchorSegmentationTests.cs
tests/Novelist.Tests/ReferenceAnchorRewriteLevelTests.cs
tests/Novelist.Tests/ReferenceAnchorAuditTests.cs
tests/Novelist.Tests/ReferenceChapterBlueprintReviewTests.cs
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
- Do not store blueprints as unstructured Markdown only; beat-level fields must remain machine-checkable.
- Do not let blueprint generation introduce facts outside the known fact set without marking them for review.
- Do not let blueprint approval survive a changed source chapter-plan hash.
- Do not build source import into `ExtractStyle`.
- Do not store large source/material banks in JSON stores.
- Do not reuse `rag_chunks` or story-memory vector tables.
- Do not depend on embeddings being configured for basic source import/search.
- Do not ship L2+ adaptation without rewrite-level tests.
- Do not ship blueprint-gated drafting without blueprint review tests and draft audit tests.
- Do not add frontend API methods without updating `BridgeCompatibilityAppMethods`.

## Open Implementation Questions

- Should the first UI accept a raw source path text input, or should a native open-file bridge method be added first?
- Should exact source text previews be truncated by default for unknown-license anchors?
- Should source segments store the full original line text or normalized text only?
- Should material extraction be purely deterministic in phase 1, or should optional LLM tagging be available behind a feature flag?
- Should reference-anchor search results be included in global `SearchAll`, or remain isolated in the dedicated Reference Anchor panel?
- Should blueprint generation consume existing `ChapterPlanPayload.Content` as the primary chapter target, or should the UI add a dedicated "chapter goal" input for reference-anchored drafting?
- Should manual blueprint edits require immediate re-review before material binding, or only before draft generation?
- Should transition beats be allowed to generate prose without a direct material link, or must every generated paragraph trace to at least one reference material?
- Should the first version draft per beat only, or assemble a full chapter candidate after every beat passes audit?
- Should stale blueprints be preserved read-only for comparison, or hidden from the default UI after a plan hash change?

## Recommended First Coding Session

Start with Phase 0 and Phase 1 only:

1. Add contracts and service interfaces.
2. Add `SqliteReferenceAnchorService` with schema, create/list/delete, source import, segmentation, build status.
3. Add integration tests proving idempotent import and stable hashes.

Do not touch Agent or frontend until the source corpus is reliable. The system's quality depends on immutable, well-tested provenance first.

After Phase 3, implement Phase 4 before any prose generation work. The blueprint review gate is the control layer that makes reference reuse robust; drafting before that gate would recreate the original failure mode under a different name.
