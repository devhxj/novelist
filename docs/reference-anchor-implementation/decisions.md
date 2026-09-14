# Reference Anchor Implementation Decisions

[Back to implementation index](../reference-anchor-implementation-plan.md).

This file is the stable decisions entry point. The detailed decision record is split by concern so storage, workflow, quality gates, and audit rules can evolve without one oversized document.

## Decision Documents

- [Foundation and blueprint decisions](decisions-foundation-and-blueprint.md): storage location, chapter blueprint layer, intermediate representation, review-first workflow, and anti-screenplay constraints.
- [Boundaries and pipeline decisions](decisions-boundaries-and-pipeline.md): service boundary, import semantics, search scope, build pipeline, blueprint generation pipeline, and generator design.
- [Blueprint quality decisions](decisions-blueprint-quality.md): quality gates, analysis contract, payload shape, review strategy, and revision/approval lifecycle.
- [Materials and audit decisions](decisions-materials-and-audit.md): vector table naming, material extraction, adaptation, and audit strategy.
- [Advanced writing materials plan](advanced-materials-plan.md): per-book advanced writing materials (fact observation → mechanism specimen → book-level strategy across world/style/craft/technique/structure), unified into the `reference_advanced_materials` shell, evidence-linked and review-gated, consumed by automatic L1+L2 retrieval during prose generation. Design draft pending author confirmation.

## Reading Rule

Read the relevant decision page before changing contracts, storage, blueprint generation/review, material binding, adaptation, or draft audit behavior.
