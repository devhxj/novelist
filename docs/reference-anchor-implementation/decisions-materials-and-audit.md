# Materials and Audit Decisions

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to decisions index](decisions.md).

## Vector Table Naming

Do not use `SqliteVecTableProvisioner.BuildVectorTableName(long novelId, int dimensions)` directly because it creates story-memory names like `vec_novel_1_1536`.

Use the reference-anchor specific helper in `SqliteVecTableProvisioner`:

```text
vec_reference_anchor_{anchorId}_{dimensions}
```

Validate the generated identifier with the same simple identifier rule used by `SqliteVecTableProvisioner.BuildCreateTableSql`.

## Material Extraction Strategy

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

Current Phase 10 decision: material extraction and tagging stay deterministic-only. The current service must not require LLM configuration for segmentation, sentence/passage bank creation, tag assignment, slot detection, or searchable material persistence. LLM-assisted tagging can be added later only behind a separate opt-in extractor interface or feature flag, and its output must still be stored as auditable material tags that pass the same deterministic binding and audit rules.

Recommended extractor interfaces in Infrastructure:

```csharp
internal interface IReferenceTextSegmenter { ... }
internal interface IReferenceMaterialExtractor { ... }
internal interface IReferenceSlotDetector { ... }
internal interface IReferenceCandidateAuditor { ... }
```

Keep these internal until the abstractions prove stable.

## Adaptation Strategy

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

Current Phase 10 decision: standalone material adaptation remains deterministic-only. `AdaptMaterialAsync` performs declared slot replacement, rewrite-level classification, non-slot edit reporting, and reuse audit without an LLM adapter. Future model-assisted adaptation must be explicit opt-in, beat-scoped, provenance-preserving, and unable to bypass max rewrite level, locked phrase, fact, POV, or audit failures.

L2:

- allow small connector and agreement edits
- every non-slot edit must be reported
- if non-slot edit count or similarity delta exceeds threshold, classify as L3 and fail unless explicitly allowed

L3/L4:

- L3 may return candidate with warning but should not pass unless requested
- L4 disabled

## Audit Strategy

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
- source-leak detection for non-L0/L1 reuse and anchored draft candidates via normalized character n-gram overlap, candidate source coverage, and source-span concentration
- high-risk AI phrase list

LLM-assisted audit can be a second pass later, but deterministic audit gates must exist first.
