# Reference Anchor Layer Implementation Plan

This implementation plan has been split into focused companion documents so each file stays reviewable. This original path remains the stable entry point.

## Status

Core implementation phases 0-10 and Phase 13 are complete in the task tracker. Phase 11 still tracks the remaining AI-orchestrated low-intervention workflow gaps. Phase 12 still tracks the shared reference corpus model where AI chooses relevant materials from global libraries by story context instead of requiring per-novel binding.

## Date

2026-07-04

## Stable Entry Points

- [Overview and architecture](reference-anchor-implementation/overview.md): stable overview index for status/scope, planning updates, and architecture map.
- [Implementation decisions](reference-anchor-implementation/decisions.md): stable decisions index for foundation, pipeline, quality, material, and audit decisions.
- [Schema and integration plan](reference-anchor-implementation/schema-and-integration.md): stable integration index for database, bridge, desktop/agent, and frontend surfaces.
- [Tasks, tests, and guardrails](reference-anchor-implementation/tasks-and-verification.md): stable task index for phase breakdowns, test matrix, guardrails, and open Phase 11-12 work.

## Topic Documents

- [Status and scope](reference-anchor-implementation/overview-status-and-scope.md)
- [Planning updates](reference-anchor-implementation/overview-planning-updates.md)
- [Architecture map](reference-anchor-implementation/overview-architecture-map.md)
- [Foundation and blueprint decisions](reference-anchor-implementation/decisions-foundation-and-blueprint.md)
- [Boundaries and pipeline decisions](reference-anchor-implementation/decisions-boundaries-and-pipeline.md)
- [Blueprint quality decisions](reference-anchor-implementation/decisions-blueprint-quality.md)
- [Materials and audit decisions](reference-anchor-implementation/decisions-materials-and-audit.md)
- [Database schema](reference-anchor-implementation/schema-database.md)
- [Bridge API surface](reference-anchor-implementation/schema-bridge-api.md)
- [Desktop and agent integration](reference-anchor-implementation/schema-desktop-and-agent.md)
- [Frontend surface](reference-anchor-implementation/schema-frontend.md)
- [Tasks phases 0-4](reference-anchor-implementation/tasks-phases-0-4.md)
- [Tasks phases 5-9](reference-anchor-implementation/tasks-phases-5-9.md)
- [Tasks phases 10-12](reference-anchor-implementation/tasks-phases-10-12.md)
- [Tasks phase 13](reference-anchor-implementation/tasks-phase-13.md)
- [Verification and guardrails](reference-anchor-implementation/tasks-verification-and-guardrails.md)

## Reading Order

1. Start with the overview index, then read status/scope and planning updates.
2. Read the relevant decision topic before changing contracts, storage, blueprint review, material binding, or draft audit behavior.
3. Use the schema and integration topic pages while wiring bridge, desktop, agent, or frontend surfaces.
4. Use the task phase pages for implementation sequencing, and the verification page for acceptance tests and guardrails.

The companion technical baseline remains [Reference Anchor Layer Technical Baseline](reference-anchor-layer-plan.md).
