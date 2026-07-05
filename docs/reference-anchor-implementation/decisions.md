# Reference Anchor Implementation Decisions

[Back to implementation index](../reference-anchor-implementation-plan.md).

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

The user-facing writing workflow can be understood as "first produce a detailed chapter script and analysis, review it, then write prose". In code and UI, however, do not name this artifact "script". A script-like artifact tends to bias generation toward action/dialogue beats and away from novelistic narration. The intended artifact is a structured prose-generation contract:

```text
chapter plan / timeline / world state / previous chapter state
  -> Chapter Narrative Blueprint
  -> Blueprint Analysis Contract
  -> Blueprint Review Gate
  -> Reference Material Binding
  -> Paragraph Candidate Generation
  -> Draft Audit
  -> user copy/manual insertion
```

The blueprint is not a detailed outline. It must contain all important features needed to write the chapter without relying on model intuition:

- chapter function in the whole story;
- explicit chapter logic analysis: premise, conflict, turn, consequence, and hook;
- causality chain from previous chapter state to final hook;
- transition plan between scenes and beats;
- per-character emotional trajectory;
- emotion analysis: trigger, suppressed reaction, visible evidence, delayed release, and after-state;
- role-state changes and relationship pressure;
- character analysis: goal, knowledge, misbelief, leverage, restraint, and state delta;
- POV character and narrative distance;
- narration analysis: narrative distance, interiority ratio, summary/scene balance, rhythm, and texture target;
- scene facts that may be used;
- forbidden facts that must not be introduced;
- paragraph/beat-level narrative duty;
- reference material query plan for each beat;
- max allowed rewrite level for each material use;
- adaptation target for each beat: copy, slot substitution, light connector edit, or no reuse;
- risk flags for AI-like prose, fake emotion, screenplay drift, and hard transitions.

The blueprint should be more detailed than a traditional outline, but less free than a prose draft. It is a "chapter execution contract": every beat explains why it exists, what emotional and narrative job it performs, what facts it may use, what it must not reveal, which reference materials it may borrow from, and how much rewriting is permitted.

Required non-goals:

- it is not screenplay dialogue/action blocking;
- it is not a model prompt hidden in a markdown field;
- it is not a free-form critique or editor note;
- it is not a replacement for the material bank;
- it must not contain final prose paragraphs that bypass reuse audit.

Rationale:

- **Decomposition:** current LLMs perform poorly when asked to solve plot logic, emotional continuity, viewpoint control, style matching, and prose execution in one pass. A blueprint separates reasoning from prose.
- **Externalized cognition:** the model must write down the assumptions it would otherwise keep implicit. That makes false emotion, missing transitions, and invented facts inspectable before prose exists.
- **Checkability:** causality, emotion triggers, POV knowledge, and scene facts can be reviewed before any prose is written.
- **Hallucination containment:** the blueprint establishes a bounded fact set and explicit forbidden facts before paragraph generation.
- **Emotion realism:** the blueprint forces every emotional change to have a trigger, internal state, external evidence, and narrative expression plan.
- **Novelistic narration:** every beat must declare whether it is action, reaction, interiority, environment, transition, information reveal, or hook, preventing the output from collapsing into dialogue/action-only screenplay form.
- **Reference anchoring:** retrieved sentences and passages are bound to a beat's narrative function, not pasted by superficial semantic similarity.
- **Robustness:** even if LLM prose remains unstable, the deterministic review gate can reject broken intermediate artifacts and prevent bad assumptions from propagating into final draft candidates.

Scientific/engineering validity argument:

- **Cognitive load reduction:** direct chapter generation asks one model call to solve plot causality, emotional realism, viewpoint control, prose rhythm, reference reuse, and anti-hallucination simultaneously. Splitting blueprint and prose reduces task coupling and makes each failure mode observable.
- **Intermediate supervision:** a blueprint is an inspectable intermediate representation. The system can reject unsupported emotional shifts, missing transitions, and POV leaks before they become fluent but wrong prose.
- **Retrieval grounding:** reference material is not retrieved by theme alone. It is matched against declared beat duties, including function, emotion transition, POV, narrative distance, technique, and rewrite budget.
- **Deterministic safety checks:** many core failures are structural, not aesthetic: missing cause, missing trigger, forbidden fact, unapproved rewrite level, material without provenance. These can be checked without trusting an LLM reviewer.
- **Human-like drafting workflow:** human writers often separate structure, excerpt reuse, scene intention, and final prose. The blueprint formalizes this workflow in storage and UI so the agent cannot skip directly to generic prose.
- **Regression friendliness:** bad blueprints and bad draft candidates can become fixtures. This makes style and narration quality partly testable instead of depending only on subjective prompt tuning.

Decision:

- Generate a detailed chapter scenario blueprint before any prose candidate.
- Review the blueprint as an artifact of its own, not as an editor note attached to a draft.
- Treat review failure as a hard workflow stop.
- Bind reference materials only after the blueprint has passed review.
- Generate prose only from approved blueprint beats and active material links.

This is expected to be better than direct chapter drafting because it moves the model's weakest work - implicit emotional reasoning, scene causality, viewpoint control, and style transfer - into explicit fields that can be inspected before prose fluency hides the failure. It is also safer than unrestricted imitation because the blueprint does not authorize copying by theme alone; every future paragraph must trace back to a beat duty, a source material, an allowed rewrite level, and an audit result.

The first implementation may use an LLM to generate the blueprint, but the review gate must be a separate operation with deterministic checks. A failed review must block prose generation until the blueprint is revised and reviewed again. LLM critique may explain or supplement deterministic findings, but it cannot override the hard gates.

### Chapter Blueprint as an Intermediate Representation

The chapter blueprint is an intermediate representation between planning data and final prose. This is the key expansion beyond a simple reference corpus.

The workflow should be:

```text
chapter plan / user goal / world state / previous state
  -> detailed chapter blueprint IR
  -> deterministic blueprint review
  -> explicit user or agent approval
  -> reference material binding per beat
  -> beat-level paragraph candidate generation
  -> draft audit
  -> user copy/manual insertion
```

This IR should be closer to a human writer's private chapter scenario sheet than to a public outline. It should be detailed enough that a later drafting step does not need to invent emotional logic, transitions, point of view, or scene facts. It should not be so prose-like that it bypasses material reuse and audit.

Required IR layers:

```text
logic_ir
  - inherited premise
  - conflict source
  - beat dependency graph
  - setup/payoff pairs
  - transition reason between beats
  - final hook dependency

emotion_ir
  - character emotion before/after
  - emotional trigger
  - suppressed internal response
  - outward evidence available to prose
  - delayed release, misdirection, or restraint
  - relationship pressure delta

narration_ir
  - POV owner
  - allowed/forbidden knowledge
  - narrative distance
  - scene/summary balance
  - interiority, sensory, subtext, environment, or rhythm duty
  - anti-screenplay requirement

character_ir
  - goal
  - pressure
  - leverage
  - knowledge boundary
  - misbelief or blind spot
  - restraint
  - state delta

reference_ir
  - source material query intent
  - expected material type
  - function/emotion/POV/narrative-duty fit target
  - slot substitution plan
  - locked phrase policy
  - max rewrite level
  - no-reuse reason if applicable

execution_ir
  - paragraph intention
  - execution mode: dwell, compress, withhold, reveal, contrast, linger, turn
  - source-backed detail target
  - candidate rejection rule
  - minimum non-action narrative work
```

The drafting layer must consume this IR as a contract, not as inspiration. It may generate text only for selected beats, only from approved fields, and only through selected reference material links or explicit approved no-reuse reasons.

Architectural decision: treat the blueprint workflow like a compiler pipeline rather than a chain of prompts. Each stage receives a typed artifact, validates it, writes an auditable result, and never silently repairs an earlier stage:

```text
context pack builder
  -> blueprint generator
  -> blueprint normalizer
  -> deterministic blueprint reviewer
  -> explicit approval gate
  -> material binder
  -> beat-scoped draft candidate generator
  -> draft auditor
```

The generator may be AI-assisted, but its output is only an intermediate artifact. The reviewer is a separate component with deterministic hard gates. The material binder is a separate component that matches source material to declared beat duties. The draft generator is a separate component that consumes one approved beat contract plus selected material links. The auditor is a separate component that checks the resulting candidate against provenance, rewrite level, facts, POV, prose duties, emotion evidence, and screenplay drift.

No stage should accept a generic "make it better" instruction as enough. If a stage finds a problem, it must return field-level defects or candidate audit errors that can become regression fixtures.

### Review-First Chapter Scenario Workflow

The additional layer requested by the product design is not "make the outline more detailed". It is a separate reviewable artifact: a detailed chapter scenario blueprint that carries the reasoning a human writer would normally keep in notes before writing prose.

This workflow is expected to be more robust than direct drafting or a plain fine outline because it places the highest-risk failures at an inspectable level:

```text
chapter goal / chapter plan / known facts
  -> chapter scenario blueprint with analysis tracks
  -> blueprint review with field-level defects
  -> blueprint revision until pass
  -> explicit approval
  -> reference material binding by beat duty
  -> beat-scoped paragraph candidates
  -> candidate audit
```

The blueprint generator may still be AI-assisted, but the system must not trust it as prose. Its output is treated as data that can be rejected, revised, hashed, compared, and used as a contract. This avoids the "left foot stepping on right foot" problem as much as possible: the second model pass is not asked to magically make the first pass emotional or human-like; it is asked to check explicit fields against deterministic rules and source-backed evidence.

Required chapter-scenario dimensions:

- **Logic analysis:** inherited premise, conflict source, beat dependency, setup/payoff, consequence, and final hook dependency.
- **Emotion analysis:** before/after state, trigger, suppressed reaction, visible evidence, delayed release, relationship pressure, and after-state.
- **Narration analysis:** POV owner, knowledge boundary, narrative distance, scene/summary balance, rhythm, interiority, sensory pressure, subtext, and anti-screenplay duty.
- **Character analysis:** goal, leverage, restraint, misbelief, knowledge delta, role-state delta, and relationship pressure delta.
- **Reference-use analysis:** expected material type, source function, emotion/POV/prose-duty fit, slot substitution plan, locked phrase policy, and max rewrite level.
- **Execution analysis:** paragraph intention, execution mode, source-backed detail target, minimum non-action narrative work, and candidate rejection rule.

The review layer must produce actionable defects, not vague editorial advice. A defect should identify the failing dimension, target field path or beat id, severity, reason, and required fix. For example: `beats[3].emotion_trigger` missing, `beats[2].transition_out` is only a time/location jump, or `beats[5].prose_duties` has no non-action duty. Generic advice such as "make it more emotional" is not enough to unlock prose generation.

The blueprint can pass only when it is detailed enough for a later beat-level drafting tool to operate without inventing plot logic, character motivation, emotional evidence, scene facts, or narration duties. If prose generation still needs to infer those fields, the blueprint is incomplete and must be revised first.

Scientific and engineering validity:

- **Separating reasoning from prose reduces hidden failure.** Direct chapter generation lets the model hide false causality and fake emotion behind fluent sentences. The IR forces those assumptions into fields that can be checked before prose exists.
- **Intermediate supervision makes subjective quality partly testable.** "Does this feel human?" is hard to unit test. "Does this emotional shift have a trigger, suppressed reaction, visible evidence, and after-state?" can be tested deterministically.
- **Hallucination surface is smaller.** The blueprint creates a known fact set and forbidden fact set before drafting. Paragraph generation can then reject unsupported proper nouns, numbers, objects, and revelations instead of discovering hallucination after a full chapter exists.
- **Reference reuse becomes functional, not decorative.** A retrieved sentence is not accepted because it is thematically similar. It must fit the beat's function, emotion, POV, narrative distance, prose duty, and rewrite budget.
- **Screenplay drift becomes rejectable.** If a beat only has action/dialogue blocking, review fails before prose generation. The system does not ask the prose generator to "make it novelistic" after the structure has already collapsed into a script.
- **Human review is placed at the highest-leverage layer.** Users can approve or revise logic, emotion, narration, and reference-use choices before spending time on prose candidates.
- **Regression fixtures become meaningful.** Bad blueprints can be stored as fixtures for fake emotion, hard transitions, POV leaks, unsupported facts, and dialogue/action-only beats.

This is not a claim that AI understands emotion. The design assumes the opposite: the model is unreliable at implicit emotional reasoning, so the system requires emotion to appear as auditable structure. A chapter beat is not allowed to say only "she feels sad"; it must declare the trigger, the suppressed/internal reaction, the visible evidence, the narrative expression duty, and the after-state. A later prose candidate can then be checked against those fields.

This also avoids pure self-critique. The reviewer should not be another free-form "please judge quality" prompt. The hard gate is deterministic and fixture-driven. Optional LLM critique may help explain defects, but it cannot authorize drafting when required fields, facts, POV boundaries, material links, or rewrite budgets fail.

Robustness requirements:

- The IR must be normalized and hashed. Approval applies only to the exact normalized analysis contract.
- Review must be idempotent for the same blueprint, context hash, source plan hash, and review version.
- Any edit to a reviewed field must invalidate approval and material bindings.
- Draft generation must not accept a blueprint whose latest passing review belongs to an older `analysis_contract_hash`.
- LLM-generated fields must pass schema validation before persistence.
- Optional LLM critique can add warnings, but deterministic hard gates decide whether drafting is allowed.
- The UI must show IR layers separately, so users can fix the cause of a defect rather than editing a vague markdown critique.
- Generator, reviewer, binder, draft generator, and auditor must be independently testable. A single model call or service method must not generate a blueprint, approve it, bind material, and draft prose in one pass.
- The reviewer must be allowed to fail useful-looking blueprints when they lack explicit cause, emotion evidence, POV boundary, transition pressure, prose duty, or reference-use rationale. Fluency is not a substitute for completeness.

### Anti-Screenplay Blueprint Constraints

The blueprint layer must not recreate the current failure mode where generated chapters read like scripts. A "detailed chapter script" is useful only if it is constrained as a novelistic execution contract.

Allowed blueprint content:

- scene purpose and narrative pressure;
- causality edges and transition reasons;
- character internal state, restraint, and external evidence;
- viewpoint boundary and narrative distance;
- paragraph intention and prose duties;
- source material query, adaptation policy, and rewrite budget;
- risk flags and required fixes.

Disallowed blueprint content:

- final prose paragraphs;
- dialogue/action blocking as the only beat content;
- camera-direction language that has no narrator or viewpoint duty;
- generic emotion labels without trigger/evidence;
- "make this more moving" style instructions;
- free-form markdown that cannot be reviewed field-by-field.

Anti-screenplay checks:

- every ordinary beat needs at least one prose duty beyond action or dialogue;
- every dialogue-heavy beat needs subtext, interiority, withheld reaction, environment pressure, or rhythm duty;
- every scene transition needs causal, emotional, informational, or viewpoint pressure;
- every beat must declare whether the narration should compress, dwell, withhold, reveal, contrast, or linger;
- repeated action-only beats are review failures unless the chapter function explicitly requires a fast exchange;
- the draft generator may not treat beat summaries as stage directions.

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

### Import Semantics

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

### Reference Search Scope

Keep reference material search scoped to `SearchReferenceMaterials` and the dedicated reference-anchor UI for the current implementation. Workspace-wide `SearchAll` should continue to cover project entities, chapter/title/content matches, and story-memory RAG hits only.

Rationale:

- reference material results use license-sensitive preview rules that are stricter than ordinary workspace search;
- material ranking exposes reference-specific score components, filters, and provenance fields that do not fit the current global result taxonomy;
- keeping a separate entry point makes it clearer when the user is searching reusable source material rather than story workspace content;
- future global search integration should be an explicit staged opt-in design, not an accidental merge of two different search policies.

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
  -> user_or_agent_revision
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

`review_passed` is intentionally not enough to generate prose. It says the artifact is structurally acceptable; `approved` says the user or agent has accepted that exact artifact as the chapter execution contract. Material binding and draft generation must check the frozen hash rather than only checking a status string.

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

### Blueprint Generator Design

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

### Blueprint Quality Gates

A blueprint can proceed to material binding only when it passes deterministic review. A blueprint can proceed to draft generation only when it has also been explicitly approved.

Hard fail conditions:

- any of the logic, emotion, narration, character, reference, transition, or execution tracks is missing or empty;
- any beat has no narrative function;
- a beat changes character emotion without a trigger;
- a beat declares emotion but no external evidence or narrative expression plan;
- a scene transition lacks a causal or emotional reason;
- a POV character knows information outside `viewpoint_allowed_knowledge`;
- a scene fact includes a forbidden fact or a fact not present in known facts/declared slot values;
- a prose-generation beat lacks prose duties or is dialogue/action-only without an explicit short-exchange flag;
- a beat has paragraph intention but no declared novelistic execution mode, such as dwell, compress, withhold, reveal, contrast, linger, or turn;
- a dialogue-heavy beat has no subtext, withheld reaction, interiority, environmental pressure, or rhythm duty;
- a reference-bound beat has no material type, function tag, rewrite budget, or intended use;
- selected material matches only semantic similarity and has no function/emotion/POV/prose-duty fit;
- the final hook depends on a new fact that was not set up earlier in the blueprint.

Soft warnings:

- too many beats use the same narrative duty;
- every beat asks for the same reference material type;
- emotion transitions are all direct and immediate, with no suppression/delay/misdirection;
- narration distance is unchanged across the entire chapter when the chapter plan implies pressure changes;
- paragraph intentions repeat mechanically across adjacent beats;
- too many beats choose `no_reuse_reason`, reducing the value of the anchor layer;
- max rewrite level exceeds the project default.

These gates make "先写细章剧本，评审后再写正文" technically useful: the blueprint becomes a validation surface for logic, emotion, perspective, narration, and reference-use constraints before prose fluency can hide defects.

Failure-mode coverage matrix:

| Failure mode | Detection layer | Required response |
|---|---|---|
| fake emotion, direct emotion jumps | blueprint review: emotion track and beat fields | fail review until trigger, suppressed reaction, external evidence, and after-state are explicit |
| screenplay-like action/dialogue blocks | blueprint review: narration and execution tracks | fail review unless prose duties and anti-screenplay duties are present |
| hard scene transitions | blueprint review: transition plan | fail review until causal, emotional, informational, or viewpoint pressure is present |
| POV leakage | blueprint review and draft audit | fail review/audit when beat facts exceed viewpoint knowledge boundary |
| invented world facts | blueprint review and draft audit | fail review/audit unless fact appears in known facts, approved slot values, or source-backed detail |
| decorative reference retrieval | material binding | reject semantic-only matches without function, emotion, POV, or prose-duty fit |
| over-rewritten imitation | adaptation audit | classify as L2/L3/L4 and block above allowed rewrite level |
| prose hides weak structure | workflow preflight | block draft generation until review has passed and approval matches current hashes |
| stale approved blueprint | approval preflight | invalidate approval and material links after source plan or reviewed fields change |
| model tries to write full chapter directly | service/tool boundary | reject unbounded draft calls; only beat-scoped candidates are allowed |

This matrix is deliberately conservative. It does not claim the system can make the model truly understand emotion. Instead, it reduces the number of places where fake understanding can pass unnoticed: emotion must be represented as evidence-bearing state transitions, narration must be represented as prose duties, and every candidate must trace back to approved fields and source material.

### Blueprint Analysis Contract

The blueprint is the technical version of "write a detailed chapter script first, then review it". It is not prose and not a loose outline. It is a machine-checkable analysis contract that must be complete before material binding or draft generation.

The contract has five required analysis tracks, one transition plan, and one required execution track. The five analysis tracks are logic, emotion, narration, character, and reference use:

```text
logic_track
  - premise inherited from previous state
  - beat-level cause/effect edges
  - conflict escalation
  - scene transition reason
  - payoff/setup relationship
  - final hook dependency

emotion_track
  - character emotion before/after each beat
  - trigger for every emotional change
  - suppressed/internal reaction
  - external evidence visible in prose
  - delayed release or misdirection, if any

narration_track
  - POV owner and knowledge boundary
  - narrative distance per beat
  - scene vs summary ratio
  - interiority/sensory/environment/subtext duties
  - rhythm target and sentence-density target
  - anti-screenplay duty for dialogue/action beats

character_track
  - goal, pressure, knowledge, misbelief, leverage, restraint
  - role-state before/after
  - relationship pressure before/after
  - what the character cannot know yet

reference_track
  - reference material query for each beat
  - expected material type and function tag
  - allowed rewrite level
  - required slot substitutions
  - locked phrase policy
  - no-reuse reason when a beat should not borrow material

transition_plan
  - scene boundary reason
  - emotional carry-over
  - information carry-over
  - paragraph bridge duty
  - transition risk

execution_track
  - paragraph intention per beat
  - novelistic execution mode: dwell, compress, withhold, reveal, contrast, linger, turn
  - anti-screenplay duty for action/dialogue-heavy beats
  - required non-action material: interiority, sensory anchor, environmental pressure, subtext, delayed reaction, or narrative transition
  - source-backed detail target
  - candidate rejection rule for the beat
```

This contract exists because the system should not assume the model "understands emotion". Instead, it requires the model to expose emotional mechanics as inspectable data: trigger, internal state, outward evidence, narrative expression, and state change. If any part is missing, the prose generator is not allowed to compensate creatively.

The execution track is intentionally separate from the reference track. A reference sentence may be a good lexical match but still be wrong for the paragraph's job. The generator must know whether the beat needs to slow down, imply, compress, linger on a sensory detail, or turn the emotional state before it can select material safely.

Current contract status:

- The payload carries `logic_analysis`, `emotion_analysis`, `narration_analysis`, `character_analysis`, `reference_analysis`, `transition_plan`, and `execution_contract`.
- `reference_analysis` must remain a first-class track. Beat-level `reference_query` fields are still required, but they are execution details under the chapter-level reference-use analysis, not a substitute for it.
- Review must treat missing chapter-level reference analysis, missing beat-level reference queries, missing material types, missing intended use, missing rewrite levels, and missing no-reuse reasons as reference-track defects.

Blueprint revision should be explicit:

- review failure creates `review_failed`, not a silently patched blueprint;
- revision creates a new blueprint revision or updates the draft while preserving latest review history;
- approval only applies to the exact `context_hash` and `source_plan_hash`;
- editing any beat after approval invalidates approval and requires re-review;
- material binding may run only after review passes, and draft generation may run only after explicit approval.

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
- analysis_contract_hash
- blueprint_version
- build_version
- parent_blueprint_id
- primary_anchor_id
- chapter_function
- logic_analysis
- emotion_analysis
- narration_analysis
- character_analysis
- reference_analysis
- transition_plan
- previous_state
- final_state
- final_hook
- global_pov
- global_narrative_distance
- known_facts
- forbidden_facts
- risk_flags
- execution_contract
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
- logic_premise
- conflict_pressure
- causality_in
- causality_out
- transition_in
- transition_out
- pov_character
- narrative_distance
- viewpoint_allowed_knowledge
- viewpoint_forbidden_knowledge
- character_states_before
- character_states_after
- character_goals
- character_misbeliefs
- relationship_pressure
- emotion_trigger
- emotion_before
- emotion_after
- suppressed_reaction
- external_evidence
- narration_strategy
- rhythm_strategy
- paragraph_intention
- execution_mode
- anti_screenplay_duty
- sensory_anchor_target
- subtext_plan
- source_backed_detail_target
- candidate_rejection_rule
- scene_facts
- forbidden_facts
- reference_query
- required_material_types
- max_rewrite_level
- slot_plan
- locked_phrase_policy
- no_reuse_reason
- prose_duties
- risk_flags
```

The `prose_duties` field is important. It prevents screenplay drift by forcing each beat to declare whether the final prose needs interiority, sensory detail, transition, reaction, subtext, environmental pressure, or information reveal.

The analysis fields can start as JSON-encoded arrays/objects in SQLite and strongly typed contract records later. The important rule is that the bridge payload remains structured enough for deterministic review and UI inspection; do not collapse analysis into a single Markdown blob.

### Blueprint Review Strategy

Blueprint review is a gate, not a comment generator. It must return pass/fail plus concrete defects:

```text
ReferenceChapterBlueprintReviewPayload
- review_id
- blueprint_id
- status
- score
- logic_errors
- causality_errors
- emotion_errors
- narration_errors
- execution_errors
- character_state_errors
- pov_errors
- continuity_errors
- transition_errors
- forbidden_fact_errors
- reference_binding_errors
- material_fit_errors
- screenplay_drift_risks
- ai_prose_risks
- novelistic_narration_errors
- required_fixes
- defects (category, field_path, beat_id, severity, reason, required_fix)
- reviewed_at
```

Initial deterministic checks:

- every blueprint has logic, emotion, narration, character, reference, transition, and execution tracks;
- every beat except the first has `causality_in`;
- every beat except the last has `causality_out`;
- every scene transition has a reason rather than a location/time jump only;
- every emotional change has a trigger and external evidence;
- every emotional change has a believable before/after state and does not jump directly to a convenient plot emotion;
- every major character in a beat has goal, pressure, knowledge boundary, and role-state delta;
- POV knowledge does not include facts outside the current viewpoint;
- narration duties include at least one non-action/non-dialogue duty for ordinary prose beats;
- execution mode exists for every prose beat and is compatible with the beat's narrative function;
- dialogue/action-heavy beats include non-script narrative work: subtext, interiority, sensory anchor, delayed reaction, environment pressure, or transition pressure;
- forbidden facts do not appear in beat facts or final hook;
- every prose-generation beat has a reference query and max rewrite level;
- every reference query includes material type and intended narrative function;
- every selected material must match the beat's function, POV, emotion, or prose duty; semantic similarity alone is insufficient;
- no beat is dialogue/action-only unless intentionally marked as a short exchange;
- final hook follows from earlier beat state instead of appearing as a new fact.

Review result semantics:

- hard-gate failures make `status = failed` regardless of numeric score;
- numeric score is diagnostic only and must not override hard gates;
- optional LLM critique may add findings but cannot mark a failed deterministic review as passed;
- review must not silently revise the blueprint;
- draft generation requires `approved`, not merely `review_passed`.

LLM review can be added as a second pass, but deterministic review must decide whether drafting is allowed.

### Blueprint Revision and Approval Lifecycle

Blueprint revision must be explicit because silent self-repair hides the exact failure that the blueprint layer is meant to expose.

Lifecycle rules:

- `draft` blueprints can be edited or regenerated.
- `review_failed` blueprints keep the failed review and required fixes.
- A revision either creates a new row with `parent_blueprint_id` or updates the draft while preserving review history; pick one strategy before adding UI editing.
- `review_passed` means deterministic review passed, but draft generation is still disabled.
- `approved` requires a passing review and an explicit approve operation.
- Any edit to beats, analysis tracks, known facts, forbidden facts, reference query plan, rewrite levels, or source plan hash invalidates approval.
- Any material binding created before a blueprint edit must be marked stale or recomputed.
- `used_for_candidate` records that at least one draft candidate was generated from the exact approved version.
- `superseded` preserves older blueprints read-only for traceability.

Revision payloads should include:

- changed field path;
- previous value hash;
- new value hash;
- user/agent origin;
- revision reason;
- resulting review requirement.

This prevents a model from fixing a failed blueprint by rewriting the whole artifact without preserving what changed. It also gives the UI enough information to explain why draft generation became disabled.

### Vector Table Naming

Do not use `SqliteVecTableProvisioner.BuildVectorTableName(long novelId, int dimensions)` directly because it creates story-memory names like `vec_novel_1_1536`.

Use the reference-anchor specific helper in `SqliteVecTableProvisioner`:

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
