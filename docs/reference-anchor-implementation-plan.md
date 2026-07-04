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
src/Novelist.App/            Photino desktop shell, loopback host, manual service composition
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

The agent tools should retrieve/adapt/audit, but must not call `SaveContent`.

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

### Vector Table Naming

Do not use `SqliteVecTableProvisioner.BuildVectorTableName(long novelId, int dimensions)` directly because it creates story-memory names like `vec_novel_1_1536`.

Add a reference-anchor specific helper, for example inside `SqliteReferenceAnchorService`:

```text
vec_reference_anchor_{anchorId}_{dimensions}
```

Validate the generated identifier with the same simple identifier rule used by `SqliteVecTableProvisioner.BuildCreateTableSql`.

### Material Extraction Strategy

Phase 1 should implement deterministic extraction for the core corpus:

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
```

Add indexes:

```text
idx_reference_anchors_novel
idx_reference_segments_anchor_type
idx_reference_materials_anchor_type
idx_reference_materials_tags
idx_reference_candidates_material
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
```

Implementation files:

```text
src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs
src/Novelist.Core/App/IReferenceAnchorService.cs
src/Novelist.Core/Bridge/ReferenceAnchorBridgeHandlers.cs
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
    referenceAnchorService));
```

And:

```csharp
.RegisterReferenceAnchorHandlers(referenceAnchorService)
```

The exact constructor should be added in a backward-compatible way:

- keep existing constructor signatures working for tests
- add optional `IReferenceAnchorService? referenceAnchors = null` parameter near the end

## Agent Tool Plan

Add optional dependency to `NovelistMafToolRegistry`:

```csharp
private readonly IReferenceAnchorService? _referenceAnchors;
```

Add `NovelistMafReferenceTools.cs` with tools:

```text
get_reference_anchors
search_reference_materials
adapt_reference_material
audit_reference_reuse
```

Tool limits:

- `search_reference_materials`: max page size 20
- `adapt_reference_material`: requires `material_id`, `slot_values`, `max_rewrite_level`
- `audit_reference_reuse`: pure check only
- no `SaveContent`
- no direct file path reads

Tool schemas must not expose `novel_id`, `session_id`, `turn_id`, or `tool_id`.

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
}
```

Add view components:

```text
frontend/src/components/reference-anchor/ReferenceAnchorView.tsx
frontend/src/components/reference-anchor/ReferenceAnchorImportDialog.tsx
frontend/src/components/reference-anchor/ReferenceMaterialSearch.tsx
frontend/src/components/reference-anchor/ReferenceCandidatePreview.tsx
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
- copy candidate; no automatic insertion in phase 1

If direct file picker is needed, add it as a separate runtime capability later. Do not block backend implementation on an open-file native dialog.

## Implementation Task Breakdown

### Phase 0: Contract and Test Fixture Foundation

**Description:** Add contracts, enum constants, benchmark fixture format, and tests for payload serialization.

**Acceptance criteria:**

- [ ] `ReferenceAnchorPayloads.cs` compiles.
- [ ] `IReferenceAnchorService.cs` compiles.
- [ ] JSON property names match frontend snake_case.
- [ ] Rewrite level constants and build states are documented in code or tests.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge`

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchorService.cs`
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

**Description:** Build sentence and passage material banks with deterministic tags and conservative slots.

**Acceptance criteria:**

- [ ] Material rows point to valid source segments.
- [ ] Function/material tags exist with confidence fields.
- [ ] Slots are stored separately and tied to material ids.
- [ ] Locked phrases survive L1 adaptation.
- [ ] User corrections can be represented even if UI arrives later.

**Verification:**

- [ ] extractor unit tests for Chinese punctuation/dialogue/paragraph cases
- [ ] integration test verifies material counts and provenance joins

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- internal extractor classes in `src/Novelist.Infrastructure/App/`
- `tests/Novelist.Tests/` extractor tests

### Phase 3: Hybrid Search

**Description:** Add paginated material search with tag filters and optional embeddings.

**Acceptance criteria:**

- [ ] Search works without embedding configuration using lexical/tag ranking.
- [ ] Search records score components.
- [ ] If embedding config exists, vectors are provisioned in reference-specific vec tables.
- [ ] Missing sqlite-vec returns a recoverable status.
- [ ] Results are bounded and paginated.

**Verification:**

- [ ] fake embedding client test
- [ ] fake sqlite-vec provisioner test
- [ ] search filter tests

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- possibly shared sqlite-vec table-name helper
- `ReferenceAnchorServiceTests.cs`

### Phase 4: Controlled Adaptation and Audit

**Description:** Implement L1/L2 candidate generation and deterministic audit.

**Acceptance criteria:**

- [ ] L1 changes only declared slots.
- [ ] L2 reports non-slot edits.
- [ ] L3 is classified and blocked unless requested.
- [ ] L4 cannot pass.
- [ ] Missing provenance fails audit.
- [ ] Unsupported new facts fail audit.

**Verification:**

- [ ] unit tests for rewrite-level classifier
- [ ] unit tests for unsupported fact detection
- [ ] integration test for `AdaptMaterialAsync`

**Files likely touched:**

- `SqliteReferenceAnchorService.cs`
- internal adaptation/audit classes
- `tests/Novelist.Tests/ReferenceAnchor*Tests.cs`

### Phase 5: Bridge Integration

**Description:** Expose reference-anchor operations through the Photino bridge.

**Acceptance criteria:**

- [ ] All bridge methods route to service operations.
- [ ] Invalid payloads return stable `VALIDATION_ERROR`.
- [ ] app-not-initialized and invalid path errors use existing bridge semantics.
- [ ] Frontend/backend method registry test passes.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter Bridge`
- [ ] integration test dispatches representative reference-anchor requests

**Files likely touched:**

- `ReferenceAnchorBridgeHandlers.cs`
- `BridgeCompatibilityAppMethods.cs`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- bridge tests

### Phase 6: Desktop Wiring

**Description:** Instantiate the service in Photino desktop composition and pass it to bridge and agent registry.

**Acceptance criteria:**

- [ ] Desktop service graph compiles.
- [ ] Existing constructor tests still pass.
- [ ] No existing tool disappears.
- [ ] Reference tools are absent when service is null and present when configured.

**Verification:**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter MafToolRegistry`
- [ ] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter Photino`

**Files likely touched:**

- `PhotinoWindowFactory.cs`
- `NovelistMafToolRegistry.cs`
- `NovelistMafReferenceTools.cs`
- `MafToolRegistryTests.cs`

### Phase 7: Frontend Workflow

**Description:** Add UI for anchors, search, adaptation preview, and audit.

**Acceptance criteria:**

- [ ] ActivityBar has a reference-anchor entry.
- [ ] WorkspaceView renders ReferenceAnchorView for the active novel.
- [ ] User can create/rebuild anchor and search materials.
- [ ] Candidate preview shows source, adapted text, rewrite level, changed slots, audit warnings.
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

### Phase 8: Feedback Loop and Hardening

**Description:** Store user decisions and use them to improve ranking and regression tests.

**Acceptance criteria:**

- [ ] User feedback rows persist accept/reject/edit decisions.
- [ ] User-verified tags can override extractor tags.
- [ ] Regression fixtures include previously bad candidates.
- [ ] Rebuild preserves user corrections where source segment hash is unchanged.

**Verification:**

- [ ] integration tests for feedback persistence
- [ ] ranking test for user-verified boost

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
tests/Novelist.IntegrationTests/ReferenceAnchorServiceTests.cs
tests/Novelist.IntegrationTests/ReferenceAnchorBridgeTests.cs
```

## Critical Guardrails

- Do not put reference source text into `goink.md`, chapter files, skills, or preferences.
- Do not expose arbitrary file reads through agent tools.
- Do not let agent tools call `SaveContent` through this workflow.
- Do not build source import into `ExtractStyle`.
- Do not store large source/material banks in JSON stores.
- Do not reuse `rag_chunks` or story-memory vector tables.
- Do not depend on embeddings being configured for basic source import/search.
- Do not ship L2+ adaptation without rewrite-level tests.
- Do not add frontend API methods without updating `BridgeCompatibilityAppMethods`.

## Open Implementation Questions

- Should the first UI accept a raw source path text input, or should a native open-file bridge method be added first?
- Should exact source text previews be truncated by default for unknown-license anchors?
- Should source segments store the full original line text or normalized text only?
- Should material extraction be purely deterministic in phase 1, or should optional LLM tagging be available behind a feature flag?
- Should reference-anchor search results be included in global `SearchAll`, or remain isolated in the dedicated Reference Anchor panel?

## Recommended First Coding Session

Start with Phase 0 and Phase 1 only:

1. Add contracts and service interface.
2. Add `SqliteReferenceAnchorService` with schema, create/list/delete, source import, segmentation, build status.
3. Add integration tests proving idempotent import and stable hashes.

Do not touch Agent or frontend until the source corpus is reliable. The system's quality depends on immutable, well-tested provenance first.
