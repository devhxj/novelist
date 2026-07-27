# Materials and Audit Decisions

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to decisions index](decisions.md).

## Vector Table Naming

Do not use `SqliteVecTableProvisioner.BuildVectorTableName(long novelId, int dimensions)` directly because it creates story-memory names like `vec_novel_1_1536`.

Use the materialization generation helper in `SqliteVecTableProvisioner`:

```text
BuildReferenceMaterializationVectorTableName(generationId, dimensions)
=> vec_reference_materialization_{generationHash}_{dimensions}
```

The generation id is hashed before it becomes an identifier. `BuildCreateTableSql` still validates the final name with the shared simple-identifier rule.

## Material Extraction Strategy

> **Accepted replacement (2026-07-20):** A confirmed full chapter is the only materialization input unit. The selected LLM receives one complete chapter per request as transient numbered physical lines and returns inclusive start/end line ranges for all materials in that chapter. The server resolves each range to a non-empty, continuous, exact chapter substring that may span any number of paragraphs. Line numbers are request-local addresses only; they are not persisted text nodes or a preprocessing hierarchy. The selected embedding model must produce one valid vector for every returned material before the generation can be activated.
>
> There is no sentence, paragraph, scene, semantic-window, candidate, review, truncation, sliding-window, rule-only, lexical-only, old-vector, JSON-scan, alternate-model, partial-success, automatic retry, or rollback path. Missing configuration, provider failure, empty or invalid structured output, source-text mismatch, vector mismatch, or index failure fails the chapter and run explicitly. One worker processes exactly one chapter at a time and commits its materials, embeddings, and vector index before claiming the next chapter. “运行全部”复用同一个 run/generation，跳过实际提交完整的章节，从首个未完成章节继续；“运行本章”无论该章是否 completed 都强制重做该章，提交后停止，不隐式推进后续章节。
>
> The replacement is deliberately breaking. Delete the old extraction interfaces, tables, bridge methods, UI, and tests after consumers move to active material identity. Do not preserve them behind adapters or dual reads. See the authoritative [whole-chapter materialization plan](../corpus-driven-writing/materialization-quality-plan.md).

The sole extraction abstraction is `IReferenceChapterMaterialExtractor`. Its request contains the frozen model identity and full chapter; its result contains material type, server-resolved exact source text, a short description, and simple tags. The model does not copy source text into tool arguments. Server-side validation accepts the complete result or fails the entire chapter.

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
