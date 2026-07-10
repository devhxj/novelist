# Boundaries and Pipeline Decisions

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to decisions index](decisions.md).

## Service Boundary

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

    ValueTask<ReferenceChapterBlueprintPayload> ReviseChapterBlueprintAsync(
        ReviseReferenceChapterBlueprintPayload input,
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
- Full-chapter assembly remains deferred. The current public draft payload returns beat-scoped paragraph candidates plus audit data only; it must not expose `chapter_text`, `assembled_text`, or `full_chapter` fields until every participating beat candidate has passing audit/provenance and a separate insertion-confirmation design exists.
- `ReviseChapterBlueprintAsync` must create an auditable revision entry and invalidate approval/material links when any reviewed field changes.
- `ApproveChapterBlueprintAsync` must verify that the latest passing review matches the current `context_hash`, `source_plan_hash`, `analysis_contract_hash`, and `review_version`.
- `BindBlueprintMaterialsAsync` must verify approval first, then create beat-material candidate links against the approved analysis contract. It defaults to returning ranked candidates without selecting them; callers must explicitly request top-candidate selection before draft generation. It must not bind materials to a draft, failed, stale, or merely review-passed blueprint.
- `GenerateDraftFromBlueprintAsync` must verify both the approved blueprint contract and active material links immediately before generation. A stale approval or stale link must return a validation error instead of falling back to free drafting.
- Inputs should use `novel_id` and `chapter_number` as the stable public targeting model. `ChapterPayload.Id` exists, but chapter-number based workflows already exist in planning, RAG, and workspace utilities.
- Blueprint records may reference the current `ChapterPlanPayload.Scope` and a hash of `ChapterPlanPayload.Content`, but must remain valid even when a chapter file does not exist yet.

Recommended internal structure in `Novelist.Infrastructure`:

```csharp
internal interface IReferenceChapterContextPackBuilder { ... }
internal interface IReferenceChapterBlueprintGenerator { ... }
internal interface IReferenceChapterBlueprintNormalizer { ... }
internal interface IReferenceChapterBlueprintReviewer { ... }
internal interface IReferenceBlueprintMaterialBinder { ... }
internal interface IReferenceBeatDraftCandidateGenerator { ... }
internal interface IReferenceAnchoredDraftAuditor { ... }
```

Keep these as internal implementation components at first. The public boundary should remain `IReferenceAnchoredDraftService`; the smaller components exist so the pipeline can be tested and hardened without turning `SqliteReferenceAnchoredDraftService` into a prompt-and-SQL monolith.

Responsibilities:

- context pack builder: gathers chapter plan, previous state, known/forbidden facts, world entities, active anchor ids, and computes the normalized `context_hash`;
- blueprint generator: produces a structured scenario blueprint, either deterministically or via schema-validated LLM output, but never prose;
- blueprint normalizer: bounds strings, normalizes arrays, validates enum-like fields, and computes `analysis_contract_hash`;
- blueprint reviewer: runs deterministic hard gates and returns field-level defects without mutating the blueprint;
- material binder: searches and ranks reference materials by beat duty, not just semantic similarity;
- beat draft candidate generator: receives one approved beat and selected material links, never a whole-chapter free-form writing prompt;
- draft auditor: checks provenance, rewrite level, forbidden facts, POV boundary, prose duties, emotion evidence, and screenplay drift.

Current code has `ReferenceChapterBlueprintNormalizer`, `ReferenceChapterBlueprintReviewer`, `ReferenceAnchoredDraftPreflight`, and `ReferenceAnchoredDraftAuditor` extracted as internal implementation components. Material binding and beat candidate generation still live inside `SqliteReferenceAnchoredDraftService`; extract them only if their rule sets grow enough to justify a separate component. Do not expose these internals from `Novelist.Core` until there is a proven need for alternate implementations.

The key implementation invariant is that no component may both create and approve its own artifact:

```text
generator writes blueprint fields
reviewer checks blueprint fields
approver freezes the reviewed hash
binder links materials to the frozen hash
candidate generator writes beat prose candidates
auditor checks prose candidates
user decides whether to copy prose
```

This separation is what makes the layer robust. It prevents one LLM-shaped operation from inventing logic, approving that logic, selecting imitation material, and writing prose in a single opaque pass.

## Import Semantics

Reference anchor import currently supports `.txt` and `.md`.

`CreateReferenceAnchorPayload` accepts a user-provided local source path:

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
- source file max size is capped
- license status must be an allowed enum string

The service reads the source once and persists immutable source segments. Agent tools operate on imported anchor/material ids and must not read arbitrary source paths later.

Unknown-license sources are allowed, but exact source text should not be exposed as a full search/library preview by default. The service keeps complete imported source segments and materials in SQLite for provenance, hashing, adaptation, binding, and audit, while `SearchReferenceMaterials` truncates preview text for anchors marked `license_status = unknown`.

## Reference Search Scope

Keep reference material search scoped to `SearchReferenceMaterials` and the dedicated reference-anchor UI for the current implementation. Workspace-wide `SearchAll` should continue to cover project entities, chapter/title/content matches, and story-memory RAG hits only.

Rationale:

- reference material results use license-sensitive preview rules that are stricter than ordinary workspace search;
- material ranking exposes reference-specific score components, filters, and provenance fields that do not fit the current global result taxonomy;
- keeping a separate entry point makes it clearer when the user is searching reusable source material rather than story workspace content;
- future global search integration should be an explicit staged opt-in design, not an accidental merge of two different search policies.

## Build Pipeline

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

## Blueprint Generation Pipeline

Blueprint generation is separate from source import. It runs per target chapter:

```text
context_pack
  -> chapter_blueprint_ir_generated
  -> logic_ir_completed
  -> emotion_ir_completed
  -> narration_ir_completed
  -> character_ir_completed
  -> reference_ir_completed
  -> execution_ir_completed
  -> structural_normalization
  -> analysis_contract_hash_computed
  -> anti_screenplay_check
  -> deterministic_review
  -> optional_llm_critique
  -> proposed_revision
  -> user_approved_revision
  -> review_passed
  -> explicit_approval
  -> approved_execution_contract
  -> material_binding
  -> draft_generation
  -> draft_audit
```

Blueprint states:

```text
draft
normalized
review_failed
review_passed
approved
stale
material_bound
used_for_candidate
superseded
```

State transitions must be explicit:

| Operation | Required current state | Additional preflight | Result |
|---|---|---|---|
| generate blueprint | none or superseded previous blueprint | valid chapter target and bounded context pack | `draft` or `normalized` |
| normalize blueprint | `draft` | schema-valid IR, bounded fields, enum-like values valid | `normalized` with `analysis_contract_hash` |
| review blueprint | `normalized`, `review_failed`, or revised `review_passed` | current `context_hash`, `source_plan_hash`, and `analysis_contract_hash` | `review_failed` or `review_passed` |
| approve blueprint | `review_passed` | latest passing review hash/version matches current blueprint | `approved` |
| bind materials | `approved` | approval hash/version still current; selected anchors available | `material_bound` |
| generate beat candidate | `material_bound` | active material links or approved `no_reuse_reason`; source plan not stale | `used_for_candidate` plus candidate rows |
| revise reviewed field | any non-superseded state | field is part of reviewed contract | approval invalidated; material links stale; requires re-review |
| chapter plan changes | any non-superseded state | `source_plan_hash` differs | `stale` until regenerated or explicitly revised |

`review_passed` is intentionally not enough to generate prose. It says the artifact is structurally acceptable; `approved` says the user has explicitly accepted that exact artifact as the chapter execution contract. An agent may propose a revision but cannot cross this approval boundary. Material binding and draft generation must check the frozen hash rather than only checking a status string.

Context pack inputs:

- current novel id and chapter number;
- current chapter plan scope/content, if available;
- previous chapter summary/content excerpt, bounded by token budget;
- unresolved timeline entries and active story arcs for this chapter;
- relevant world entities and role states;
- user-supplied chapter goal, if provided;
- active reference anchor ids and material search filters.

The context pack must also include an explicit "known facts" and "forbidden facts" list. Blueprints must be reviewed against these lists before prose generation.

Important ordering rule:

- Review happens before material binding when checking the blueprint's internal logic.
- Material binding happens before final draft preflight when checking whether each beat has usable reference support.
- Draft generation requires both an approved blueprint and active selected material links, except for beats with an explicit approved `no_reuse_reason`.
- The draft preflight must re-read the current blueprint, latest approval, latest review, and active material links in one transaction-like operation so stale state cannot slip through between UI actions.

This ordering prevents a retrieved sentence from covering up a weak chapter scenario, and prevents a strong scenario from authorizing unsupported prose.

## Blueprint Generator Design

The blueprint generator is a constrained planner, not a prose writer. It may use LLM output, but the service must normalize and validate the result before persistence.

The generator should be implemented as a two-stage operation:

1. Generate the chapter blueprint IR: a structured chapter scenario with logic, emotion, narration, character, reference, and execution layers.
2. Normalize and review the IR before any material binding or prose candidate generation can run.

Do not let a single model call both plan and draft. If the service later uses an LLM for paragraph prose, that call must receive only one approved beat contract plus selected material links, not the whole chapter plan as a free-form writing prompt.

Inputs:

- `novel_id`, `chapter_number`, and optional `chapter_goal`;
- current chapter plan scope/content and hash;
- previous chapter state summary and unresolved hooks;
- active world entities, role states, relationship states, and timeline constraints;
- active reference anchor ids and optional material filters;
- user-selected tone/genre constraints if the existing project settings expose them;
- bounded previous-content excerpts for continuity only, not for unrestricted copying.

Outputs:

- one `ReferenceChapterBlueprintPayload`;
- ordered beat rows with stable beat ids;
- six IR/analysis layers: logic, emotion, narration, character, reference, and execution;
- per-beat paragraph intentions;
- per-beat emotion evidence requirements;
- per-beat anti-screenplay duties;
- known/forbidden fact sets;
- reference query plan per beat;
- risk flags and required review targets.

Generation rules:

- produce structured JSON according to contracts first, never markdown-first content;
- include at least one non-dialogue/non-action prose duty for every ordinary scene beat;
- include at least one of interiority, sensory pressure, subtext, environment response, delayed reaction, narrator withholding, or paragraph rhythm duty for every prose beat;
- include transition duties for scene boundary beats;
- include an explicit no-reuse reason when a beat should not use reference material;
- assign max rewrite level per beat and default to `L1` unless the user explicitly allows a higher level;
- include enough reference queries to bind materials later, but do not paste final prose;
- never use action/dialogue beats alone as a substitute for novel narration;
- mark any proposed fact that is outside the context pack as a candidate issue rather than silently adding it to `scene_facts`;
- preserve the distinction between character emotion, narrator presentation, and visible external evidence;
- each beat must be independently draftable from its stored fields; if a future paragraph generator needs to infer missing logic, emotion, POV, or prose duty, the blueprint is incomplete.

Normalization rules before persistence:

- trim and bound all strings;
- preserve array order for beats, causality edges, and required fixes;
- reject malformed enum-like values at the service boundary;
- fill absent optional arrays with empty arrays, not nulls;
- compute `context_hash` from the normalized context pack;
- compute `source_plan_hash` from the current chapter plan scope/content;
- compute `analysis_contract_hash` from normalized beat and analysis fields;
- persist generator version and review version so old blueprints can be marked stale after rule changes.

Reproducibility policy:

- Persist and expose the generator `build_version` on each blueprint.
- Persist and expose `context_hash`, `source_plan_hash`, and `analysis_contract_hash` as the stable reproduction anchors for the inputs and normalized analysis contract.
- Persist `review_version` on review and approval rows, not as mutable blueprint prompt metadata.
- Do not persist prompt text, prompt templates, or schema snapshots on blueprint rows. Prompt/schema changes should move `build_version` or reviewer version forward when they affect generated contracts, avoiding database churn from routine prompt wording edits.
- Future LLM-assisted generation can add a separate opt-in run log if needed for debugging, but that log must not become a required approval or draft-generation gate.

Recommended staged implementation:

1. **Deterministic scaffold:** build a minimal but fully structured IR from existing chapter plan text, previous state, and anchor filters. This proves storage, review, invalidation, and bridge behavior without depending on model quality.
2. **LLM structured fill:** replace selected scaffold fields with schema-validated LLM output, but keep deterministic normalization and review unchanged.
3. **Fixture-driven hardening:** add known-bad blueprint fixtures for fake emotion, missing transition, POV leak, unsupported fact, action/dialogue-only beat, and material mismatch.
4. **Beat-scoped drafting:** only after review fixtures are stable, add paragraph candidate generation for one beat at a time.
