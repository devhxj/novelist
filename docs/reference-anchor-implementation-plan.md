# Reference Anchor Layer Implementation Plan

This implementation plan has been split into focused companion documents so each file stays reviewable. This original path remains the stable entry point.

## Status

Implementation in progress.

## Date

2026-07-04

## Plan Documents

- [Overview and architecture](reference-anchor-implementation/overview.md): scope, review-first workflow update, current implementation snapshot, and architecture map.
- [Implementation decisions](reference-anchor-implementation/decisions.md): storage, blueprint workflow, review strategy, adaptation strategy, and audit strategy.
- [Schema and integration plan](reference-anchor-implementation/schema-and-integration.md): database schema, bridge APIs, desktop composition, agent tools, frontend workflow, and desktop debugging notes.
- [Tasks, tests, and guardrails](reference-anchor-implementation/tasks-and-verification.md): phased task breakdown, required test matrix, critical guardrails, open questions, and recommended next sessions.

## Reading Order

1. Start with the overview for scope, current status, and runtime boundaries.
2. Read decisions before changing contracts, storage, blueprint review, material binding, or draft audit behavior.
3. Use the schema and integration plan while wiring bridge, desktop, agent, or frontend surfaces.
4. Use tasks and verification for implementation sequencing, acceptance criteria, tests, and guardrails.

The companion design document remains [Reference Anchor Layer Plan](reference-anchor-layer-plan.md).
