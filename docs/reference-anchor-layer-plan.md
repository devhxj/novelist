# Reference Anchor Layer Plan

## Status

Draft for review.

Implementation companion: `docs/reference-anchor-implementation-plan.md`.

## Date

2026-07-04

## Goal

Add a **Reference Anchor Layer** to Novelist. A user can choose one book as an anchor reference, and Novelist will build reusable writing assets from it:

- corpus library
- sentence bank
- setting library
- world environment library
- passage bank
- emotion corpus
- writing technique library
- viewpoint library
- chapter template library

The layer is not a normal "style prompt". Its job is to turn a reference book into searchable, traceable materials, then let the agent reuse those materials with constrained edits.

The core product principle is:

```text
Do not ask the AI to invent human-feeling detail.
Let the AI retrieve human-written material, adapt it minimally, and prove what changed.
```

## Context

The current Novelist codebase already has useful boundaries for this feature:

- `src/Novelist.Contracts/App/` holds frontend/backend payload contracts.
- `src/Novelist.Core/App/` holds application service interfaces.
- `src/Novelist.Infrastructure/App/` holds file system and SQLite implementations.
- `src/Novelist.Core/Bridge/` registers bridge methods used by the frontend.
- `src/Novelist.Agent/` registers structured tools for the agent loop.
- Existing RAG services provide a model for chunking, embedding, indexing, and semantic search.
- Existing builtin skills such as `deai-flavor` operate at prompt/methodology level, not asset level.

This plan adds an asset layer below skills and agents. Skills can request materials from the Reference Anchor Layer, but should not replace its provenance, rewrite-level, or hallucination checks.

## Design Thesis

This feature is based on a constrained-generation thesis:

```text
Unconstrained fiction generation fails because the model invents details, emotion labels, and scene logic.
Reference-anchored reuse reduces the model's creative degrees of freedom.
The system becomes more reliable when the model is limited to retrieval, classification, slot filling, light adaptation, and audit.
```

The layer is therefore not a "better prompt". It is a control system around a noisy model. The model can still help, but every high-risk operation has a source, a type, a confidence, an allowed edit range, and an audit result.

## Why This Can Be Effective

The plan is expected to improve output quality for four technical reasons.

### 1. It Replaces Free Generation With Retrieval

AI-flavored prose often appears when the model must invent scene detail from a sparse instruction. The reference layer changes the problem from:

```text
Invent a human-feeling detail for this emotion.
```

to:

```text
Find a source sentence or passage that already performs this narrative function.
Adapt only declared parts of it.
```

This is more robust because the detail originates in a human-authored or user-approved corpus, not in the model's latent averages.

### 2. It Splits Writing Into Verifiable Subtasks

The system decomposes prose generation into smaller operations:

- source segmentation
- function tagging
- emotion tagging
- viewpoint tagging
- retrieval
- slot detection
- slot replacement
- light adaptation
- provenance diff
- hallucination audit

Each subtask can be tested independently. This is stronger than asking one LLM call to "make it more human", where failure is hard to localize.

### 3. It Keeps Source, Derived Material, and Output Separate

The source corpus is immutable. Material banks are derived and rebuildable. Adapted candidates are separate from both. This separation makes the pipeline reversible, debuggable, and measurable.

```text
source segment -> material item -> reuse candidate -> audited insertion
```

If the output is bad, the system can identify whether the failure came from segmentation, tagging, retrieval, slotting, adaptation, or audit.

### 4. It Makes "Human Feeling" Observable

The feature does not try to prove that a model understands feeling. It measures observable proxies:

- whether the prose uses source-backed concrete details
- whether emotion is carried by action, object, silence, or viewpoint order
- whether unsupported facts were introduced
- whether rewrite distance stayed within the configured limit
- whether the candidate kept the source sentence's narrative function

These proxies are not perfect, but they are inspectable and testable.

## Robustness Principles

The layer must be designed as a high-integrity pipeline. The following principles are not optional.

### Immutable Source, Rebuildable Derivatives

Imported source segments must never be edited in place. If the source text changes, the anchor becomes stale and derivatives are rebuilt with a new `BuildVersion` and new hashes.

### Provenance Is Required

Every material item and reuse candidate must retain:

- anchor id
- source segment id
- source hash
- source location
- extractor version
- adaptation operation
- rewrite level

If provenance is missing, the candidate must fail audit.

### Confidence Is Data, Not Decoration

Extractor output should include confidence scores for tags and slots. Low-confidence material can still be stored, but retrieval and UI must surface that uncertainty.

Recommended fields to add during implementation:

```csharp
double FunctionConfidence;
double EmotionConfidence;
double PovConfidence;
double SlotConfidence;
string ExtractorVersion;
string ExtractorModelId;
bool UserVerified;
```

### Hybrid Retrieval, Not Embeddings Only

Semantic vectors are useful but insufficient. A semantically close sentence can have the wrong narrative function. Retrieval should combine:

- embedding similarity
- full-text/BM25 or lexical match
- material type match
- function tag match
- emotion tag match
- scene tag match
- POV tag match
- source quality/confidence
- user verification boost

The ranker should return score components, not just one opaque score.

### Source Text Is Untrusted Data

Reference books may contain prompt-like text, malicious markup, or instructions. Source text must always be passed to the model as quoted data, never as instructions.

The agent prompt must separate:

```text
system instructions
user task
reference data block
allowed operations
```

The model must be told to ignore instructions contained inside reference text.

### Manual Insertion First

The first production version should not allow agent tools to directly mutate chapter content. Candidates are proposed, audited, and then manually inserted by the user/editor. Automatic insertion can be considered only after the audit system has real-world data.

## Non-Simplification Requirements

The design must not be reduced to any of these weaker forms:

- plain RAG over a reference book
- a single "style profile" prompt
- whole-paragraph rewriting without provenance
- unconstrained "imitate this author" generation
- semantic search without function/emotion/POV tags
- insertion without audit
- adaptation without rewrite-level classification
- source reuse without diff and lineage

If an implementation removes provenance, rewrite levels, or audit, it is not this feature anymore.

## Non-Goals

- Do not build a free-form AI coauthor in this layer.
- Do not rely on the model to "understand emotion".
- Do not make `ExtractStyle` responsible for this feature; it is a different abstraction.
- Do not silently insert long verbatim excerpts without provenance and user confirmation.
- Do not make generated content untraceable. Every adapted sentence or passage must retain source lineage.

## Architecture Decision

Introduce a new service boundary:

```text
ReferenceAnchorService
  -> imports anchor books
  -> builds source corpus segments
  -> extracts reusable material banks
  -> indexes materials for retrieval
  -> adapts selected materials under rewrite-level constraints
  -> audits provenance, hallucination, and AI-flavor risk
```

Recommended files:

```text
src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs
src/Novelist.Core/App/IReferenceAnchorService.cs
src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs
src/Novelist.Core/Bridge/ReferenceAnchorBridgeHandlers.cs
src/Novelist.Agent/NovelistMafReferenceTools.cs
frontend/src/components/reference-anchor/
frontend/src/lib/novelist/referenceAnchor.ts
```

The first implementation can share the existing embedding configuration and SQLite infrastructure, but it should keep reference-anchor tables separate from normal story-memory RAG tables.

## System Invariants

These invariants should be enforced by tests.

- A source segment hash never changes after import.
- A material item cannot exist without a valid source segment.
- A reuse candidate cannot exist without a valid material item.
- A candidate with missing provenance cannot pass audit.
- A candidate above the requested rewrite level cannot pass audit.
- A candidate that introduces undeclared story facts cannot pass audit.
- Agent tools can retrieve and adapt, but cannot directly insert into chapter content.
- Rebuild is idempotent for unchanged source, pipeline version, and extractor configuration.
- Search results are paginated and bounded.
- Import and rebuild operations are cancellable and resumable.

## Core Data Model

### Anchor Book

Represents one user-selected reference book.

```csharp
public sealed record ReferenceAnchorPayload(
    long Id,
    long NovelId,
    string Title,
    string Author,
    string SourcePath,
    string SourceKind,
    string LicenseStatus,
    string Status,
    string BuildVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
```

Important fields:

- `NovelId`: the current project that owns this anchor.
- `SourceKind`: `txt`, `markdown`, `epub_text`, `manual_paste`, or future import types.
- `LicenseStatus`: `user_owned`, `public_domain`, `authorized`, `unknown`.
- `Status`: `draft`, `indexing`, `ready`, `failed`, `stale`.
- `BuildVersion`: version of the splitter/extractor pipeline.

### Source Segment

The immutable source layer. All later banks point back here.

```csharp
public sealed record ReferenceSourceSegmentPayload(
    string SegmentId,
    long AnchorId,
    int ChapterIndex,
    string ChapterTitle,
    string SegmentType,
    int SegmentIndex,
    int StartOffset,
    int EndOffset,
    string Text,
    string TextHash);
```

Segment types:

- `chapter`
- `paragraph`
- `sentence`
- `dialogue`
- `mixed_passage`

### Material Item

One reusable unit extracted from source segments.

```csharp
public sealed record ReferenceMaterialPayload(
    string MaterialId,
    long AnchorId,
    string SourceSegmentId,
    string MaterialType,
    string FunctionTag,
    string EmotionTag,
    string SceneTag,
    string PovTag,
    string TechniqueTag,
    string Text,
    IReadOnlyList<string> LockedPhrases,
    IReadOnlyList<ReferenceSlotPayload> Slots,
    string SourceHash,
    DateTimeOffset CreatedAt);

public sealed record ReferenceSlotPayload(
    string Name,
    string SlotType,
    string OriginalText,
    bool Required);
```

`LockedPhrases` are the parts that should normally survive reuse. `Slots` are the only parts that can be replaced in L1/L2 adaptation.

### Reuse Candidate

Returned when the user or agent asks for candidate materials.

```csharp
public sealed record ReferenceReuseCandidatePayload(
    string CandidateId,
    string MaterialId,
    string SourceText,
    string AdaptedText,
    string RewriteLevel,
    bool IntroducesNewFacts,
    double MatchScore,
    double SourceSimilarity,
    IReadOnlyList<string> ChangedSlots,
    IReadOnlyList<string> Warnings,
    string Reason);
```

## Generated Libraries

### 1. Corpus Library

Purpose: preserve original text as addressable, hash-stable segments.

Contents:

- chapter records
- paragraph records
- sentence records
- dialogue spans
- source offsets
- text hashes
- adjacent context windows

This library is the authority for provenance.

### 2. Sentence Bank

Purpose: provide low-level reusable sentence materials.

Initial function tags:

- `hesitation_before_action`
- `emotion_avoidance`
- `attention_shift`
- `dialogue_lead_in`
- `dialogue_afterbeat`
- `object_bearing_emotion`
- `scene_transition`
- `silence_response`
- `action_interruption`
- `chapter_closing_hook`

The sentence bank is the first-phase priority because it directly addresses AI-flavored details.

### 3. Passage Bank

Purpose: provide reusable paragraph and short-scene structures.

Initial passage tags:

- `conflict_escalation`
- `pressure_scene`
- `secret_discovery`
- `avoidance_scene`
- `farewell_scene`
- `argument_scene`
- `revelation_scene`
- `daily_life_pressure`
- `suspense_hold`
- `emotional_withholding`

Passages should usually be reused as structural guides or short adapted blocks, not inserted wholesale by default.

### 4. Emotion Corpus

Purpose: capture how the anchor book externalizes emotion without asking the model to invent emotion.

Emotion tags:

- `guilt`
- `fear`
- `shame`
- `anger`
- `longing`
- `resentment`
- `jealousy`
- `grief`
- `humiliation`
- `suppressed_affection`

Each item should record:

- visible behavior
- object carrier
- avoided topic
- dialogue deformation
- body detail
- whether the emotion is explicit or implicit

### 5. Viewpoint Library

Purpose: capture how the anchor book orders perception.

Fields:

- POV character or narrator type
- first noticed object
- delayed information
- ignored information
- misread information
- attention transition
- diction level
- distance from character consciousness

The agent should use this library to avoid camera-like narration.

### 6. Writing Technique Library

Purpose: extract reusable narrative mechanisms.

Initial technique tags:

- `withholding`
- `reverse_expression`
- `object_as_emotion`
- `body_action_instead_of_psychology`
- `dialogue_cutoff`
- `delayed_cause`
- `small_action_after_big_event`
- `environment_pressure`
- `repetition_with_variation`
- `plain_sentence_after_tension`

Technique items should be short and operational. They should explain what the source text does, not praise the style.

### 7. Setting Library

Purpose: extract reusable factual setting elements from the anchor book.

Contents:

- social groups
- professions
- institutions
- objects
- class markers
- rules
- customs
- money/work constraints
- repeated symbolic or practical objects

Setting extraction must not be blindly copied into the user's story. It is a search and comparison aid unless the user explicitly maps an item into their own world.

### 8. World Environment Library

Purpose: capture the material world of the reference book.

Contents:

- common rooms and spaces
- weather and season handling
- street/house/interior details
- tools and everyday objects
- sound/smell/touch vocabulary
- spatial relations
- local routines

This library is useful for replacing abstract AI descriptions with grounded environment materials.

### 9. Chapter Template Library

Purpose: capture macro structures.

Templates:

- opening pattern
- scene sequence
- conflict setup
- information release order
- dialogue/action ratio
- emotional beat placement
- ending hook
- transition type between scenes

Chapter templates are phase-two scope. They should not block the first sentence/passage reuse implementation.

## Rewrite Levels

The adaptation engine must expose rewrite levels explicitly.

| Level | Name | Allowed operations | Default |
| --- | --- | --- | --- |
| L0 | Exact quote | No edits | Manual only |
| L1 | Slot replacement | Replace names, objects, places, time, pronouns | Allowed |
| L2 | Light adaptation | L1 plus minor word order, connector, tense, and object-action agreement edits | Allowed |
| L3 | Skeleton imitation | Keep source function and rhythm, replace most surface content | Warn |
| L4 | Free rewrite | Model rewrites freely from source inspiration | Disabled by default |

The first implementation should cap automatic adaptation at L2.

L3 requires explicit user confirmation.

L4 should be unavailable in the reference-anchor pipeline unless a future setting enables it deliberately.

## Import and Build Pipeline

```text
Import anchor book
  -> normalize text
  -> split chapters
  -> split paragraphs and sentences
  -> persist immutable source segments
  -> classify sentence and passage functions
  -> extract emotion/viewpoint/technique metadata
  -> generate slot templates
  -> embed source segments and material items
  -> mark anchor ready
```

The pipeline should be resumable. A failed extraction step should not require re-importing the source file.

Recommended build stages:

1. `source_imported`
2. `segments_built`
3. `materials_extracted`
4. `slots_detected`
5. `embeddings_built`
6. `ready`

## Retrieval Flow

Input:

```text
current story facts
scene type
emotion target
POV role
allowed rewrite level
desired material type
```

Flow:

```text
query intent
  -> semantic search over material bank
  -> filter by function/emotion/scene/POV tags
  -> rank by similarity and tag match
  -> return candidates with source provenance
```

The UI should show source text, adapted candidate, rewrite level, changed slots, and warnings before insertion.

## Adaptation Flow

Input:

```csharp
public sealed record AdaptReferenceMaterialPayload(
    long NovelId,
    long AnchorId,
    string MaterialId,
    string CurrentSceneFacts,
    string TargetCharacter,
    IReadOnlyDictionary<string, string> SlotValues,
    string MaxRewriteLevel);
```

Output:

```csharp
public sealed record AdaptReferenceMaterialResultPayload(
    ReferenceReuseCandidatePayload Candidate,
    ReferenceReuseAuditPayload Audit);
```

The adapter must follow this order:

1. Load source material and slot template.
2. Fill only declared slots for L1.
3. For L2, allow minimal grammar and connector edits.
4. Diff adapted text against source text.
5. Check new nouns, names, places, causes, and events against current story facts and slot values.
6. Assign rewrite level.
7. Return candidate and audit result.

## Audit Rules

Every candidate should be audited before insertion.

```csharp
public sealed record ReferenceReuseAuditPayload(
    string CandidateId,
    string RewriteLevel,
    bool Passes,
    bool IntroducesNewFacts,
    bool ExceedsRewriteLimit,
    bool HasAiFlavorRisk,
    bool HasSourceProvenance,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> SuggestedFixes);
```

Audit checks:

- source material exists and hash matches
- adapted text has a valid source lineage
- rewrite level is within user limit
- no undeclared character/place/object was introduced
- no new cause, memory, relationship, or event was introduced
- sentence function did not change
- locked phrases were not destroyed unless L3 was requested
- no high-risk AI phrases were introduced
- direct quote length and provenance are visible to the user

## Effectiveness Evaluation

The system should be evaluated before broad integration with the writing agent. Effectiveness cannot be assumed from prompt quality.

### Golden Dataset

Create a small local benchmark set from user-approved samples:

```text
20-50 anchor source segments
20-50 current-scene requests
human-labeled expected material functions
human-labeled allowed slots
human-labeled unacceptable new facts
human-ranked candidate quality
```

The benchmark should include hard cases:

- similar emotion but wrong scene function
- same object but wrong POV
- useful sentence with unsafe extra fact
- sentence that can only be L3, not L1/L2
- source passage that contains prompt-like instructions
- sparse current-scene facts
- conflicting slot values

### Metrics

Track these metrics per build version:

| Metric | Purpose | Target direction |
| --- | --- | --- |
| `retrieval_hit_at_5` | correct material appears in top 5 | higher |
| `function_tag_precision` | material function is correct | higher |
| `slot_precision` | detected slots are actually replaceable | higher |
| `unsupported_fact_rate` | adapted candidates introduce new facts | lower |
| `rewrite_level_accuracy` | classifier assigns correct L0-L4 level | higher |
| `audit_false_pass_rate` | bad candidates pass audit | near zero |
| `audit_false_block_rate` | good candidates are blocked | lower |
| `source_similarity_delta` | adaptation stays close enough to source | bounded |
| `user_acceptance_rate` | user accepts candidate after preview | higher |

The most important metric is `audit_false_pass_rate`. It is better to block a usable candidate than to insert hallucinated or over-rewritten prose.

### Baselines

Compare the reference-anchor pipeline against:

1. plain free generation from scene facts
2. prompt-only "make it more human" revision
3. semantic RAG over the reference book without material tags
4. material retrieval with L1/L2 adaptation and audit

The plan is validated only if option 4 reduces unsupported facts and AI-flavor regressions while improving user acceptance.

### Human Review Loop

The system should store user decisions:

- accepted
- rejected
- manually edited
- wrong tag
- wrong slot
- too much like source
- too AI-flavored
- introduced fact

These decisions should feed future ranking and extractor corrections. User corrections are higher authority than model-generated tags.

## Failure Mode Analysis

### Segmentation Failure

Problem: chapter, paragraph, dialogue, or sentence splitting is wrong.

Mitigation:

- store raw source offsets
- allow manual segment correction
- keep segmenter version
- rebuild derivatives from source
- test Chinese punctuation, dialogue marks, blank lines, headings, and mixed prose/dialogue

### Tagging Failure

Problem: the extractor assigns the wrong function, emotion, scene, POV, or technique.

Mitigation:

- store confidence
- expose tags in UI
- allow manual correction
- rank with multiple signals instead of trusting tags alone
- build a golden tagged dataset

### Retrieval Failure

Problem: semantic search returns text that is related in topic but wrong in narrative job.

Mitigation:

- hybrid retrieval
- score components
- hard filters for material type and max rewrite level
- optional reranking pass over top results
- user-visible reason for each candidate

### Slot Failure

Problem: the system replaces a word that should have remained locked, or locks a word that must change.

Mitigation:

- conservative slot detection
- default to fewer slots
- user-editable slot map
- L1 test requiring only declared slots to change
- reject candidate if locked phrases are broken in L1/L2

### Adaptation Failure

Problem: the model adds facts, changes causality, or over-polishes into AI-like prose.

Mitigation:

- L1/L2 cap by default
- sentence-level diff
- named entity and noun phrase comparison
- current-scene fact whitelist
- AI-flavor blacklist/heuristic check
- audit before insertion

### Provenance Failure

Problem: candidate text cannot be traced back to source.

Mitigation:

- source hash required
- material id required
- candidate id required
- fail closed when provenance is missing

### Legal and Product Risk

Problem: direct reuse can become copyright or plagiarism risk.

Mitigation:

- local-only user-controlled import
- visible license status
- quote/reuse risk label
- exact source preview and diff
- manual confirmation
- user can prefer structural use over direct wording

### Prompt Injection From Source

Problem: source text includes instructions that influence the model.

Mitigation:

- source text is always delimited as data
- extraction prompts explicitly ignore source instructions
- never execute tool calls from source text
- audit output for instruction leakage

## Quality Gates

Do not ship agent integration until these gates pass:

- [ ] Import/rebuild is idempotent on unchanged input.
- [ ] Source segment hashes are stable across rebuilds.
- [ ] Search results include score components and provenance.
- [ ] L1 adaptation changes only declared slots in tests.
- [ ] L2 adaptation reports every non-slot edit in tests.
- [ ] L3 cannot pass without explicit user request.
- [ ] L4 is disabled by default.
- [ ] Audit blocks missing provenance.
- [ ] Audit blocks unsupported new facts.
- [ ] Audit blocks candidates above requested rewrite level.
- [ ] Agent tools cannot mutate chapter content.
- [ ] UI shows source, adapted text, diff, rewrite level, and warnings.

## Agent Tools

Add structured tools in `Novelist.Agent` after the service exists.

Initial tools:

```text
get_reference_anchors
search_reference_materials
get_sentence_candidates
get_passage_candidates
adapt_reference_material
audit_reference_reuse
```

Tool behavior:

- tools return bounded lists
- tools always include provenance IDs
- adaptation tools require `max_rewrite_level`
- tools must not insert text directly into a chapter
- final insertion remains a user or editor action

## Bridge API

Add bridge methods after contracts are defined.

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

Frontend method names should be added to `BridgeCompatibilityAppMethods` and `frontend/src/lib/novelist/api.ts`.

### Contract Semantics

Bridge methods should use consistent semantics:

- create methods validate input and return created payloads
- list/search methods return paginated results
- rebuild methods return a build job/status payload, not just `void`
- adaptation methods return candidate plus audit
- audit methods are pure checks and do not persist chapter changes
- delete methods are idempotent only if explicitly documented

Recommended additional payloads:

```csharp
public sealed record ReferenceAnchorBuildStatusPayload(
    long AnchorId,
    string Status,
    string Stage,
    int TotalSteps,
    int CompletedSteps,
    int SourceSegmentCount,
    int MaterialCount,
    string LastError,
    DateTimeOffset UpdatedAt);

public sealed record ReferenceMaterialSearchPayload(
    long NovelId,
    long AnchorId,
    string Query,
    IReadOnlyList<string> MaterialTypes,
    IReadOnlyList<string> FunctionTags,
    IReadOnlyList<string> EmotionTags,
    IReadOnlyList<string> SceneTags,
    IReadOnlyList<string> PovTags,
    int Page,
    int PageSize);
```

Search must be paginated from the first version. A mature anchor can contain tens of thousands of source segments and materials.

## Storage Plan

Recommended SQLite tables:

```text
reference_anchors
reference_source_segments
reference_materials
reference_material_slots
reference_material_embeddings
reference_reuse_candidates
reference_reuse_audits
```

Source text should be stored once in source segments. Material rows should store short extracted text and point back to source segments.

Vector tables can follow the existing sqlite-vec pattern, but should use anchor-specific table names to avoid mixing story memory and reference materials.

### Storage Integrity

Required constraints:

- unique anchor title per novel unless the user explicitly duplicates it
- unique source segment id
- unique `(anchor_id, build_version, source_hash, segment_type, segment_index)` where practical
- foreign keys from material to source segment
- foreign keys from slots to material
- foreign keys from candidates to material
- cascade delete only from anchor downward
- no cascade from source segment to user-approved reusable material unless explicitly intended

Large operations should run in transactions per stage, not one transaction for the entire book. This avoids corrupt half-builds while keeping rebuilds resumable.

### Build State Machine

Use a state machine rather than loose strings.

```text
draft
  -> importing
  -> source_imported
  -> segmenting
  -> segments_built
  -> extracting_materials
  -> materials_extracted
  -> detecting_slots
  -> slots_detected
  -> embedding
  -> ready
```

Failure states:

```text
failed_import
failed_segmenting
failed_extraction
failed_slotting
failed_embedding
stale
cancelled
```

Each state transition should record:

- stage
- timestamp
- extractor/build version
- progress counts
- error code
- error message

Rebuild should be incremental where possible:

- unchanged source hash: reuse source segment
- unchanged material extraction version and source hash: reuse material
- unchanged embedding provider/model/dimensions/content hash: reuse vector

## Frontend Plan

Add a "Reference Anchor" workspace panel.

Initial UI:

- anchor book list
- import/rebuild action
- build status
- material search
- filters for material type, emotion, scene, POV, technique
- candidate preview with source/adapted diff
- rewrite-level selector
- audit warnings
- copy/insert confirmation

The UI must make provenance visible. If the user is working from non-public-domain or unknown-license source text, show a clear risk label.

## Implementation Plan

### Phase 0: Evaluation Harness and Contract Freeze

**Description:** Define the benchmark, payload contracts, state machine, rewrite-level rules, and audit semantics before implementation.

**Acceptance criteria:**

- [ ] Golden dataset format is documented.
- [ ] Rewrite-level classifier rules are documented.
- [ ] Bridge payloads are defined before service implementation.
- [ ] Build state machine and error codes are defined.
- [ ] Quality gates are copied into tests or tracked test cases.

**Files likely touched:**

- `docs/reference-anchor-layer-plan.md`
- `src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs`
- `tests/Novelist.Tests/` fixtures

### Phase 1: Contracts and Read-Only Corpus

**Description:** Add payloads, service interface, and import/split persistence for source segments.

**Acceptance criteria:**

- [ ] User can create an anchor record for a novel.
- [ ] User can import a plain text or Markdown reference file.
- [ ] System persists chapter, paragraph, and sentence segments with hashes.
- [ ] Existing app build remains clean.

**Files likely touched:**

- `src/Novelist.Contracts/App/ReferenceAnchorPayloads.cs`
- `src/Novelist.Core/App/IReferenceAnchorService.cs`
- `src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs`
- tests under `tests/Novelist.Tests/` or `tests/Novelist.IntegrationTests/`

### Phase 2: Material Extraction

**Description:** Extract the first useful banks: sentence bank, passage bank, emotion corpus, and viewpoint library.

**Acceptance criteria:**

- [ ] Materials point back to immutable source segments.
- [ ] Materials have function/emotion/scene/POV tags.
- [ ] Slot templates are generated for common entity replacements.
- [ ] Extraction can be rebuilt without duplicating rows.

**Files likely touched:**

- reference-anchor service implementation
- extraction helpers in infrastructure
- contract payloads
- integration tests

### Phase 3: Search

**Description:** Add semantic and tag-based retrieval over reference materials.

**Acceptance criteria:**

- [ ] User can search materials by query.
- [ ] User can filter by material type, emotion, scene, POV, and technique.
- [ ] Search results include source provenance and match scores.
- [ ] Missing embedding configuration returns a recoverable status, not a crash.

**Files likely touched:**

- reference-anchor service
- sqlite-vec provisioning integration
- bridge handlers
- frontend API client

### Phase 4: Controlled Adaptation

**Description:** Add L0-L2 adaptation with explicit slot replacement and minimal connector edits.

**Acceptance criteria:**

- [ ] L1 only changes declared slots.
- [ ] L2 reports every non-slot edit.
- [ ] L3 returns a warning and requires explicit request.
- [ ] L4 is disabled by default.
- [ ] Candidate output includes source text, adapted text, rewrite level, changed slots, and warnings.

**Files likely touched:**

- reference-anchor service
- adaptation engine
- contracts
- tests for rewrite-level classification

### Phase 5: Audit

**Description:** Add candidate auditing before insertion.

**Acceptance criteria:**

- [ ] Audit detects undeclared new characters, places, objects, causes, and events.
- [ ] Audit detects rewrite level above user limit.
- [ ] Audit detects missing source provenance.
- [ ] Audit returns structured problems and suggested fixes.

**Files likely touched:**

- audit engine
- contracts
- tests for hallucination and over-rewrite cases

### Phase 6: Agent Tools

**Description:** Add structured tools so the writing agent can retrieve and adapt reference materials.

**Acceptance criteria:**

- [ ] Agent can list anchors and search materials.
- [ ] Agent can request adaptation with a max rewrite level.
- [ ] Agent receives audit result before proposing insertion.
- [ ] Agent tools do not directly mutate chapter content.

**Files likely touched:**

- `src/Novelist.Agent/NovelistMafReferenceTools.cs`
- tool registry wiring
- agent integration tests

### Phase 7: Frontend Workflow

**Description:** Add a reference-anchor panel and candidate review UI.

**Acceptance criteria:**

- [ ] User can import/rebuild an anchor book from UI.
- [ ] User can search material banks.
- [ ] User can inspect source/adapted diff.
- [ ] User can choose rewrite limit.
- [ ] User can confirm insertion manually.

**Files likely touched:**

- `frontend/src/components/reference-anchor/`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`

### Phase 8: Hardening and Learning Loop

**Description:** Use real user decisions and test failures to improve ranking, tagging, slots, and audits without weakening constraints.

**Acceptance criteria:**

- [ ] User accept/reject/edit decisions are stored.
- [ ] User-corrected tags override extractor tags.
- [ ] Ranking can boost user-verified materials.
- [ ] Regression tests cover previously accepted bad candidates.
- [ ] A rebuild preserves user corrections unless the source segment is deleted.

**Files likely touched:**

- reference-anchor service
- frontend review UI
- tests and fixtures

## Checkpoints

### Checkpoint 0: After Phase 0

- [ ] Contracts, state machine, and benchmark are ready.
- [ ] No implementation starts from vague payloads.
- [ ] The feature can be evaluated beyond subjective taste.

### Checkpoint A: After Phase 1

- [ ] Source import and segmentation works.
- [ ] Hash-stable source segments exist.
- [ ] No agent integration yet.

### Checkpoint B: After Phase 3

- [ ] Searchable material bank exists.
- [ ] Users can inspect materials before any adaptation.
- [ ] No chapter mutation happens automatically.

### Checkpoint C: After Phase 5

- [ ] Controlled adaptation and audit work together.
- [ ] Over-rewrite and hallucination cases are caught by tests.

### Checkpoint D: After Phase 7

- [ ] End-to-end UI flow works.
- [ ] User can import, search, adapt, audit, and manually insert.

## Risks and Mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| AI over-adapts and reintroduces AI flavor | High | Default to L1-L2, classify rewrite level, warn on L3, disable L4 |
| AI introduces unsupported facts | High | Compare new entities/events against story facts and slot values |
| Source provenance is lost | High | Immutable source segments, hashes, material IDs, candidate lineage |
| Copyright or plagiarism concerns | High | Require user-provided/authorized sources, show license status, expose direct quote risk, preserve provenance |
| Extraction tags are noisy | Medium | Let users correct tags; store corrections as first-class metadata |
| Vector search returns semantically close but functionally wrong material | Medium | Combine semantic score with function/emotion/scene/POV filters |
| Large books make rebuild slow | Medium | Stage pipeline, incremental rebuild, progress status |
| Feature becomes too broad | Medium | Build by vertical slices, but keep provenance, rewrite levels, audit, state machine, and evaluation from the start |

## Open Questions

- Should one novel support multiple active anchor books, or exactly one active anchor by default?
- Should unknown-license anchors be allowed for local-only personal use with warnings, or blocked from direct reuse workflows?
- Should adapted candidates be saved as reusable project-owned materials after user approval?
- How much of the source text should the UI show when previewing candidates?
- Should the first import format be only `.txt`/`.md`, or include `.epub` text extraction immediately?

## First Vertical Slice

The first implementation slice should prove the full architecture, not simplify it away. It may include fewer material bank types, but it must still include source immutability, provenance, rewrite levels, audit, build state, and evaluation.

```text
Import TXT/MD anchor
  -> segment into source corpus
  -> build sentence bank + passage bank
  -> search by tags/query
  -> adapt one sentence at L1/L2
  -> audit candidate
  -> manual insertion only
```

This slice proves the core mechanism before expanding to setting library, world environment library, emotion corpus depth, viewpoint library depth, writing technique library, and chapter templates. Any slice that omits provenance, rewrite-level classification, or audit is not acceptable.
