# Reference Anchor Layer Implementation Plan

[Back to implementation index](../reference-anchor-implementation-plan.md).

This file is the stable overview entry point. The detailed content is split into smaller documents so each topic stays reviewable.

## Status

Phases 0-13 are complete at the current implementation boundary. [Phase 14](tasks-phase-14.md) is open for advanced style anchoring and high-fidelity imitation. [Phase 15](tasks-phase-15.md) is proposed for the latest `goink-master` feature merge into the current Novelist architecture.

This plan remains the source of truth for the target design. Treat Phase 14 as the open style-anchoring phase and Phase 15 as the open product-merge planning phase unless contracts, storage, bridge, agent, or frontend behavior regresses.

## Detailed Documents

- [Status and scope](overview-status-and-scope.md): plan date, implementation scope, and non-negotiable design constraints.
- [Planning updates](overview-planning-updates.md): review-first workflow update, current implementation snapshot, low-intervention workflow, shared-corpus direction, Phase 13 closure, Phase 14 style anchoring direction, and Phase 15 feature-merge boundary.
- [Architecture map](overview-architecture-map.md): solution layout, runtime composition, desktop boundary, bridge model, storage model, RAG reuse points, agent model, and frontend model.

## Related Entry Points

- [Implementation decisions](decisions.md)
- [Schema and integration](schema-and-integration.md)
- [Tasks and verification](tasks-and-verification.md)
