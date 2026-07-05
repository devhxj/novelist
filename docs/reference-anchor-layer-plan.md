# Reference Anchor Layer Technical Baseline

## Status

Current technical baseline for the reference-anchor feature.

Implementation tracking lives in `docs/reference-anchor-implementation-plan.md` and its split companion files. Those files are the source of truth for phase status, schema details, bridge surface, tests, and open work.

Last updated: 2026-07-05.

## Product Shape

Reference-anchor data should become shared workspace-level corpus infrastructure, not per-novel setup data.

The long-term model is:

```text
workspace/global reference corpus
  -> AI retrieves materials by current story context
  -> deterministic gates review provenance, license, facts, POV, and rewrite budget
  -> approved selections bind to a novel-specific blueprint
  -> audited candidates are shown for user confirmation
```

The current implementation still uses per-novel anchor records for compatibility with the existing SQLite schema. That is an implementation bridge, not the final data model.

## Scope Boundaries

Global/workspace-scoped data:

- imported reference sources;
- source segments and source hashes;
- extracted materials;
- material tags, confidence, user corrections, license status, source trust, and visibility;
- global corpus feedback.

Novel-scoped data:

- chapter blueprints and orchestration runs;
- selected material bindings for a blueprint;
- draft candidates and audits;
- known facts, forbidden facts, POV, timeline, and character-state boundaries;
- per-novel usage feedback.

`novel_id` should remain on blueprints, candidates, audits, orchestration runs, and per-novel feedback. It should not be required for source libraries or extracted reference materials once Phase 12 promotes the shared corpus model.

## Runtime Boundary

This remains an in-process desktop feature:

- frontend calls use the owned Photino bridge adapter;
- contracts live in `src/Novelist.Contracts/App/`;
- service interfaces live in `src/Novelist.Core/App/`;
- SQLite implementations live in `src/Novelist.Infrastructure/App/`;
- agent tools live in `src/Novelist.Agent/`;
- React UI lives under `frontend/src/components/reference-anchor/`.

Do not introduce ASP.NET Core, HTTP endpoints, or a separate service host for this feature.

## Workflow

Default low-intervention flow:

```text
user confirms source policy, chapter target, and fact boundaries
  -> AI starts an orchestration run
  -> blueprint is generated
  -> deterministic blueprint review runs
  -> user approves a compact blueprint/risk summary or asks for proposed fixes
  -> materials are retrieved and bound automatically
  -> beat candidates are generated
  -> deterministic draft audit runs
  -> user selects or edits the final candidate before insertion
```

Advanced manual controls may expose each step separately for debugging and strict editorial review. The default path should not require separate user clicks for safe deterministic stages.

## Required Gates

The workflow must stop for:

- source/license confirmation or policy exception;
- known/forbidden fact boundary changes;
- stale blueprint;
- blueprint approval;
- AI-proposed blueprint revision application;
- missing required material provenance;
- weak retrieval when the beat requires source-backed material;
- rewrite level above the configured budget;
- POV leak risk;
- unsupported fact pressure;
- draft audit failure;
- final prose insertion.

AI may automate routine sequencing and propose fixes. AI must not approve blueprints, expand fact boundaries, bypass stale-link checks, bypass source/license filters, or insert prose into a chapter without explicit user confirmation.

## Retrieval Gaps

If the shared corpus cannot satisfy a beat, the system must treat that as a retrieval gap rather than silently free-drafting.

Allowed automatic recovery:

- broaden query terms;
- weaken non-critical filters;
- switch from concrete scene material to technique or style material;
- return weak matches with low-confidence provenance and elevated audit risk.

Allowed continuation:

- if the beat does not require source-backed detail, continue only with an approved no-reuse reason and only from current novel facts.

Required stop:

- if the beat requires reference-backed material and no acceptable material exists, stop so the user can import more corpus material, relax policy, revise the blueprint, or skip that beat.

## Rewrite Levels

| Level | Name | Allowed operations | Default behavior |
| --- | --- | --- | --- |
| L0 | Exact quote | No edits | Manual only |
| L1 | Slot replacement | Replace declared names, places, objects, time, and pronouns | Allowed |
| L2 | Light adaptation | L1 plus connector, tense, agreement, and minor order edits | Allowed |
| L3 | Skeleton imitation | Keep source function/rhythm while replacing most surface content | Warn and require explicit approval |
| L4 | Free rewrite | Model rewrites from source inspiration | Disabled by default |

Automatic adaptation should cap at L2 unless a future explicit setting changes that behavior.

## Invariants

These rules should be enforced by tests and reviewed when changing contracts, storage, bridge handlers, agent tools, or UI flows:

- imported source segments are immutable and hash-addressed;
- extracted materials retain source segment id, source hash, extractor version, and source location;
- selected material bindings are tied to the current blueprint and analysis contract hash;
- candidates without provenance cannot pass audit;
- candidates above the allowed rewrite level cannot pass audit;
- candidates that introduce unsupported story facts cannot pass audit;
- globally sourced materials must still pass per-novel fact, POV, timeline, and character-state gates;
- user feedback has separate global-corpus and per-novel usage scopes;
- agent tools can retrieve, adapt, audit, and propose but cannot mutate chapter text directly;
- the workflow never calls `SaveContent` or inserts prose automatically.

## Active Phase Direction

Phase 10 closes runtime hardening and desktop workflow verification.

Phase 11 builds the low-intervention orchestrator: one command should run safe stages automatically, persist status, resume after restart, and stop only for necessary human decisions.

Phase 12 promotes the shared corpus model: reference sources and materials become workspace-level assets; AI retrieves relevant materials from the corpus by story context; per-novel binding becomes an auditable result of a run, not a manual prerequisite.

Use `docs/reference-anchor-implementation/tasks-phases-10-12.md` for the current acceptance criteria and verification matrix.
