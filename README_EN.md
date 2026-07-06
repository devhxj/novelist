**English** | [中文](README.md)

<div align="center">

![Today's Verse](https://v2.jinrishici.com/one.svg?font-size=20&spacing=2&color=Chocolate)
</div>

<p align="center">
  <img src="assets/logo-dark.svg#gh-dark-mode-only" alt="Novelist reference-anchored AI writing system" />
  <img src="assets/logo-light.svg#gh-light-mode-only" alt="Novelist reference-anchored AI writing system" />
</p>

<h1 align="center">Novelist Reference-Anchored AI Long-Form Writing System<br><sub>Structured Memory × Skill Methodology × Blueprint Review × Draft Audit</sub></h1>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Photino.NET-Desktop-2E7D32?style=for-the-badge" alt="Photino.NET" />
  <img src="https://img.shields.io/badge/React-19-61DAFB?style=for-the-badge&logo=react&logoColor=white" alt="React 19" />
  <img src="https://img.shields.io/badge/SQLite-3-003B57?style=for-the-badge&logo=sqlite&logoColor=white" alt="SQLite" />
  <br />
  <img src="https://img.shields.io/badge/TypeScript-6.0-3178C6?style=for-the-badge&logo=typescript&logoColor=white" alt="TypeScript 6" />
  <img src="https://img.shields.io/badge/Tailwind-4.3-06B6D4?style=for-the-badge&logo=tailwindcss&logoColor=white" alt="Tailwind 4" />
  <img src="https://img.shields.io/badge/Agent_Framework-Microsoft-5E5CE6?style=for-the-badge" alt="Microsoft Agent Framework" />
  <img src="https://img.shields.io/badge/license-MIT-716B94?style=for-the-badge&logo=opensourceinitiative&logoColor=white" alt="MIT" />
</p>

---

Novelist is the desktop AI writing system evolved from GoInk. The original foundations remain: structured creative state, Agent tool use, Skill-based writing methodology, local semantic search, diff approval, and Git history.

The current direction adds a higher layer. **Skills teach the AI how to write, but long-form writing also needs to prove what may be written before generation and whether the result is usable after generation.** Novelist is turning reference sources, chapter blueprints, material bindings, draft candidates, and audit results into checkable contracts instead of relying only on prompts or Skills.

## Current Positioning

Novelist is not just a chat shell, and it is not only a pile of prompts or Skills. It is a local-first long-form fiction workbench:

| Layer | Responsibility |
|---|---|
| Structured creative state | Tracks characters, relationships, foreshadowing, arcs, locations, reader knowledge, preferences, and chapter plans |
| Agent tools | Let the AI query, modify, and maintain project state during a conversation instead of only emitting text |
| Skill methodology | Provides scene beats, dialogue subtext, pacing control, hooks, revision polish, de-AI flavoring, and other writing methods |
| Reference anchor layer | Turns reference sources into traceable materials, requires reviewed blueprints, binds materials, generates candidates, and audits drafts |
| Human confirmation boundary | Chapter insertion, fact-boundary expansion, high-risk revisions, and final saves require author confirmation |

The repository name still keeps the historical `goink`, but the current code, docs, and product direction use `Novelist`.

## Why Skills Are Not Enough

Skills improve method and style, but they cannot reliably enforce these questions:

- whether a reference source is usable, quotable, or adaptable;
- whether generated content crosses known-fact or forbidden-fact boundaries;
- whether the POV leaks knowledge the current viewpoint should not have;
- whether a chapter blueprint only describes camera blocking instead of causality, emotion, narrative distance, and character-state change;
- whether a draft candidate comes from an approved blueprint and bound materials;
- whether the AI bypassed author confirmation and directly mutated chapter text.

The reference anchor layer handles these hard constraints. It turns "does it resemble the source", "can this be adapted", "is there factual risk", and "may this be inserted" into structured records and deterministic checks. The AI may propose; it cannot bypass the gates.

## Default Reference-Anchored Workflow

```text
author confirms source policy, chapter target, known facts, and forbidden facts
  -> start orchestration run
  -> generate chapter blueprint
  -> run deterministic blueprint review
  -> author approves the blueprint or approves AI-proposed field-level revisions
  -> retrieve and bind reference materials
  -> generate beat-level draft candidates
  -> run draft audit
  -> stop at final insertion confirmation
```

The workflow automates low-risk mechanical stages but stops for:

- source, license, or fact-boundary confirmation;
- stale blueprints, missing materials, weak retrieval, or material hash mismatch;
- failed blueprint review requiring revision;
- high rewrite-level, POV, fact, or audit risk;
- final chapter insertion.

This flow does not automatically call `SaveContent` or write chapter prose. The AI can propose candidates and revisions; final writing still goes through author-confirmed editing and saving.

## Original Writing Capabilities Remain

### Structured Creative State

Novelist tracks character profiles, relationships, foreshadowing, arcs, location graphs, reader knowledge, and writing preferences. Long projects do not need the same world and character state restated in every conversation; the Agent can read and maintain structured data through tools.

### Agent-Led Lookup, Editing, And Maintenance

The system exposes structured tools to the Agent. During a conversation the AI can query characters, inspect chapters, search prior text, modify state, update preferences, and generate or revise content. After writing, maintenance prompts still ask the Agent to check character changes, foreshadowing status, arc progression, and reader knowledge.

### Local Semantic Search

RAG index and retrieval state live locally in SQLite/sqlite-vec. Embeddings can use an OpenAI-compatible online API or built-in ONNX mode. ONNX mode uses the bundled fixed `bge-small-zh-v1.5` int8 model and does not silently fall back to online APIs.

### Diff Approval And Git History

The AI should not overwrite chapter text directly. Chapter edits go through diff approval and explicit save paths, and project changes have Git history for rollback.

## Skill System

Skills are writing methodology modules. Each Skill is a `.md` file with YAML frontmatter and markdown content, supporting three override layers and three activation modes.

### Three Layers

Same-name Skills override by **Novel > User > Built-in** priority and hot-reload after edits.

| Layer | Storage | Scope | Editable |
|---|---|---|---|
| Built-in | Read-only bundled | All novels | No |
| User | data-dir `skills/`, with tool-path compatibility for `~/.goink/skills/` | All novels | Yes |
| Novel | `{novel}/skills/` | Current novel | Yes |

### Three Modes

| Mode | AI auto-invoke | User `/` trigger | Injected at session start | Listed in catalog |
|---|---|---|---|---|
| Smart `auto` | Yes | Yes | No | Yes |
| Command `manual` | No | Yes | No | No |
| Always-on `always` | Yes | Yes | Yes | No |

Create a `.md` file and it becomes a new Skill:

```markdown
---
name: My Writing Process
description: Custom personal creative workflow
category: Custom
mode: auto
---

# Markdown content
```

Skills solve method and style. The reference anchor layer solves evidence, boundaries, and auditability. They are stacked, not competing.

## Visualized State

<p align="center">
  <img src="assets/arc-demo.png" alt="Story Arcs" />
</p>
<p align="center">
  <img src="assets/location-demo.png" alt="Location Graph" />
</p>
<p align="center">
  <img src="assets/preferences-demo.png" alt="Writing Preferences" />
</p>
<p align="center">
  <img src="assets/skill-demo.png" width="80%" alt="Skill System" />
</p>

## Current Implementation Status

- The desktop mainline has moved to `.NET 10 + Photino.NET + React/Vite`.
- The Go/Wails path is retired and kept only as historical code; new work should not go under `app/`, `internal/`, or `frontend/src/lib/wailsjs/`.
- Reference anchor implementation phases 0-10 and phase 13 are complete.
- Phase 11 continues to refine low-intervention orchestration, revision authorization, stop/recovery semantics, and final insertion UX.
- Phase 12 continues the workspace-level shared reference corpus and AI-driven material selection model.

Detailed design:

- [Reference Anchor Technical Baseline](docs/reference-anchor-layer-plan.md)
- [Reference Anchor Implementation Plan](docs/reference-anchor-implementation-plan.md)
- [Photino Bridge Contract](docs/novelist-photino-bridge-contract.md)
- [Release Notes](docs/releases/release-notes.md)

## Project Structure

```text
src/
  Novelist.App             Photino desktop host and local frontend asset resolution
  Novelist.Contracts       bridge DTOs and cross-layer contracts
  Novelist.Core            app interfaces, bridge dispatch, and core boundaries
  Novelist.Infrastructure  filesystem, SQLite, RAG, and reference-anchor implementations
  Novelist.Agent           Microsoft Agent Framework tool adapters

frontend/
  src/lib/novelist         owned Photino bridge adapter
  src/components           React UI components
  scripts                  Playwright mock-bridge workflows

tests/
  Novelist.Tests
  Novelist.IntegrationTests
```

## Installation

Download the installer for your platform from [Releases](https://github.com/devhxj/goink/releases):

- **Windows**: run the installer
- **macOS**: open the DMG and drag to Applications
- **Linux**: run the AppImage

An LLM API key is required. Built-in provider templates include DeepSeek, GLM, and MiMo, and OpenAI-compatible endpoints are supported. Semantic search can use an online Embeddings API or the built-in ONNX mode. Installers include the desktop host, frontend assets, and Git runtime. No Python, Node.js, or external database is required.

Windows SmartScreen may warn about an unsigned app; choose "More info" and continue if you trust the build.

## Build From Source

```bash
sudo apt install libgtk-3-0 libwebkit2gtk-4.1-0 curl file unzip
git clone https://github.com/devhxj/goink
cd goink
dotnet restore Novelist.slnx
npm --prefix frontend ci
make deps
make build
make dev
```

`make dev` does not build frontend assets. For desktop development, run:

```bash
npm --prefix frontend run build
make dev
```

For frontend-only debugging:

```bash
make frontend-dev
```

This starts Vite only, so desktop bridge APIs are unavailable. To use the bridge against Vite, launch the Photino host with `--start-url=http://localhost:5173/`.

## Verification

Backend tests:

```bash
dotnet test Novelist.slnx --no-restore -v minimal
```

Frontend build, lint, and real-browser mock-bridge regression:

```bash
npm --prefix frontend run verify
```

Deep reference-anchor workflow:

```bash
npm --prefix frontend run test:reference-anchor
```

App-wide frontend smoke:

```bash
npm --prefix frontend run test:app
```

## Tech Stack

| Layer | Technology |
|---|---|
| Desktop | Photino.NET + .NET 10 |
| Agent Engine | Microsoft Agent Framework + OpenAI-compatible streaming + structured tools |
| Frontend | React 19 + TypeScript 6 + Tailwind CSS 4 + shadcn/ui |
| Editor | Monaco Editor with locally bundled assets |
| Storage | Filesystem JSON stores + SQLite |
| Vector Search | sqlite-vec + online Embeddings API or local ONNX |
| Version Control | Built-in Git |
| Safety Boundary | SafePath, approval flow, SSRF checks, reference-anchor audit, and manual final insertion |

## License

MIT
