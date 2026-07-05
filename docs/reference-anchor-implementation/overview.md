# Reference Anchor Layer Implementation Plan

[Back to implementation index](../reference-anchor-implementation-plan.md).

This file is the stable overview entry point. The detailed content is split into smaller documents so each topic stays reviewable.

## Status

Phases 0-9 are complete in the task tracker. Remaining work is tracked explicitly in [tasks phases 10-12](tasks-phases-10-12.md) and [Phase 13](tasks-phase-13.md): Phase 10 covers product hardening, Playwright mock-bridge frontend workflow verification, minimal real Photino runtime smoke, stale-blueprint UX decisions, optional model-assisted expansion, and documentation closure; Phase 11 covers AI-orchestrated low-intervention workflow design; Phase 12 covers shared reference corpus and AI-driven material selection across novels; Phase 13 covers app-wide Playwright regression coverage for the whole Novelist frontend, not only reference anchors.

This plan remains the source of truth for the target design. Treat Phase 10, Phase 11, Phase 12, and Phase 13 as the only open implementation-plan phases unless contracts, storage, bridge, agent, or frontend behavior regresses.

## Detailed Documents

- [Status and scope](overview-status-and-scope.md): plan date, implementation scope, and non-negotiable design constraints.
- [Planning updates](overview-planning-updates.md): review-first workflow update, current implementation snapshot, low-intervention workflow, and shared-corpus direction.
- [Architecture map](overview-architecture-map.md): solution layout, runtime composition, desktop boundary, bridge model, storage model, RAG reuse points, agent model, and frontend model.

## Related Entry Points

- [Implementation decisions](decisions.md)
- [Schema and integration](schema-and-integration.md)
- [Tasks and verification](tasks-and-verification.md)
