# Reference Anchor Tasks and Verification

[Back to implementation index](../reference-anchor-implementation-plan.md).

This file is the stable task and verification entry point. Detailed task breakdowns are split by phase range so completed implementation history and current regression gates stay reviewable.

## Task Documents

- [Phases 0-4](tasks-phases-0-4.md): contracts, SQLite import, material extraction, hybrid search, and chapter blueprint review gate.
- [Phases 5-9](tasks-phases-5-9.md): adaptation/drafting/audit, bridge integration, desktop and agent wiring, frontend workflow, and feedback hardening.
- [Phases 10-12](tasks-phases-10-12.md): completed product hardening, AI-orchestrated low-intervention workflow, and shared reference corpus with AI-driven material selection.
- [Phase 13](tasks-phase-13.md): completed full-product Playwright QA, usability, stress, and robustness coverage across the whole Novelist desktop frontend.
- [Phase 14](tasks-phase-14.md): open advanced style anchoring and high-fidelity imitation plan.
- [Phase 15](tasks-phase-15.md): proposed `goink-master` feature merge plan for EPUB/TXT/MD import, style material library, narrative pattern extraction, Git history visualization, desktop UX persistence/update checks, and error-feedback hardening.
- [Verification and guardrails](tasks-verification-and-guardrails.md): required test matrix, critical guardrails, closed design boundaries, future-product boundaries, and recommended next coding sessions.

## Current Phase Status

Phases 0-13 are complete at their current implementation boundaries and remain recorded for traceability and regression checks. Phase 14 is the open implementation-plan phase for advanced style anchoring, multi-scale literary understanding, and high-fidelity imitation. Phase 15 is proposed as the next product-merge plan from the latest `goink-master` snapshot into the current Novelist stack; it must port behavior through .NET/Photino contracts and owned TypeScript adapters rather than reviving Go/Wails/Python paths. The expanded Phase 13 `test:app:full`, `test:app:stress`, and `test:app:usability` commands remain the product QA regression gate while Phase 14 and Phase 15 are implemented.
