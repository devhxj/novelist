# Reference Anchor Status and Scope

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to overview](overview.md).

## Status

Phases 0-16 have implementation evidence at their recorded thin-slice boundaries. Phase 16 established the reference-corpus/chapter surface split; M9 has persistent default-session use, restart recovery, and a single primary chapter path with automated browser evidence. The corpus workspace correction still needs its focused browser workflow, and real-user usability evidence remains open.

This plan is the source of truth for reference-anchor design constraints. Current status and M9 experience closure live in `docs/corpus-driven-writing/development-plan.md` and `tasks.md`.

## Date

2026-07-08

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
- mandatory pre-prose chapter blueprint that behaves like a detailed chapter scenario analysis, not a loose outline
- analysis-bearing chapter blueprint with logic, emotion, narration, character, and reference-use checks
- explicit causality, emotion, POV, narration, role-state, scene-fact, and risk gates before drafting
- deterministic rejection of screenplay-like blueprints before they can unlock draft generation
- explicit chapter blueprint review before material binding and prose candidate generation
- draft candidates generated only from reviewed blueprint beat contracts, not directly from chapter plans
- evaluation fixtures and regression tests before broad agent integration

This is not a simplification into plain RAG or a style prompt.

Phase 16 does not weaken those constraints. It changes where workflows live:

- corpus-library processing belongs to a shared `素材库` surface;
- current-chapter reference use belongs inside the chapter editor;
- advanced blueprint/material-binding controls remain available only where they support strict review or debugging, not as the default corpus-library experience.
