# Reference Anchor Layer Implementation Plan

[Back to implementation index](../reference-anchor-implementation-plan.md).

This file is the stable overview entry point. The detailed content is split into smaller documents so each topic stays reviewable.

## Status

Phases 0-10 and Phase 13 are complete in the task tracker. Remaining work is tracked explicitly in [tasks phases 10-12](tasks-phases-10-12.md): Phase 11 covers the remaining AI-orchestrated low-intervention workflow policy gaps, especially revision-approval authorization, stop-point/recovery semantics, and final insertion UX; Phase 12 covers the shared reference corpus and AI-driven material selection model across novels.

This plan remains the source of truth for the target design. Treat Phase 11 and Phase 12 as the only open implementation-plan phases unless contracts, storage, bridge, agent, or frontend behavior regresses.

## Detailed Documents

- [Status and scope](overview-status-and-scope.md): plan date, implementation scope, and non-negotiable design constraints.
- [Planning updates](overview-planning-updates.md): review-first workflow update, current implementation snapshot, low-intervention workflow, and shared-corpus direction.
- [Architecture map](overview-architecture-map.md): solution layout, runtime composition, desktop boundary, bridge model, storage model, RAG reuse points, agent model, and frontend model.

## Related Entry Points

- [Implementation decisions](decisions.md)
- [Schema and integration](schema-and-integration.md)
- [Tasks and verification](tasks-and-verification.md)
