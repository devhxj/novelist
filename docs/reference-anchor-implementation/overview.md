# Reference Anchor Layer Implementation Plan

[Back to implementation index](../reference-anchor-implementation-plan.md).

This file is the stable overview entry point. The detailed content is split into smaller documents so each topic stays reviewable.

## Status

Phases 0-16 have implementation evidence at their recorded thin-slice boundaries. [Phase 16](tasks-phase-16.md) established the corpus/chapter surface split; M9 has automated evidence for the chapter default path, persistent blueprint-session use, restart recovery, and long-task/accessibility checks. The corpus workspace now has dedicated reference-book and transient blueprint-preview surfaces, pending focused browser acceptance; real-user usability evidence also remains open.

This plan remains the source of truth for reference-anchor design history and guardrails. Use the corpus-driven writing [development plan](../corpus-driven-writing/development-plan.md) and [tasks](../corpus-driven-writing/tasks.md) for current milestone status and M9 experience closure.

## Detailed Documents

- [Status and scope](overview-status-and-scope.md): plan date, implementation scope, and non-negotiable design constraints.
- [Planning updates](overview-planning-updates.md): review-first workflow update, current implementation snapshot, low-intervention workflow, shared-corpus direction, Phase 13 closure, Phase 14 style anchoring direction, Phase 15 feature-merge boundary, and Phase 16 corpus/chapter UI separation.
- [Architecture map](overview-architecture-map.md): solution layout, runtime composition, desktop boundary, bridge model, storage model, RAG reuse points, agent model, and frontend model.

## Related Entry Points

- [Implementation decisions](decisions.md)
- [Schema and integration](schema-and-integration.md)
- [Tasks and verification](tasks-and-verification.md)
