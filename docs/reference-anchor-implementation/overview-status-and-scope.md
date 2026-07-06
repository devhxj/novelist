# Reference Anchor Status and Scope

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to overview](overview.md).

## Status

Phases 0-11 and Phase 13 are complete in the task tracker. Remaining work is tracked explicitly in `tasks-and-verification.md`: Phase 12 covers shared reference corpus and AI-driven material selection across novels.

This plan is still the source of truth for the target design. Treat Phase 12 as the only open implementation-plan phase unless contracts, storage, bridge, agent, or frontend behavior regresses.

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
- mandatory pre-prose chapter blueprint that behaves like a detailed chapter scenario analysis, not a loose outline
- analysis-bearing chapter blueprint with logic, emotion, narration, character, and reference-use checks
- explicit causality, emotion, POV, narration, role-state, scene-fact, and risk gates before drafting
- deterministic rejection of screenplay-like blueprints before they can unlock draft generation
- explicit chapter blueprint review before material binding and prose candidate generation
- draft candidates generated only from reviewed blueprint beat contracts, not directly from chapter plans
- evaluation fixtures and regression tests before broad agent integration

This is not a simplification into plain RAG or a style prompt.
