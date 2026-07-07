**English** | [中文](README.md)

<div align="center">

![Today's Verse](https://v2.jinrishici.com/one.svg?font-size=20&spacing=2&color=Chocolate)
</div>

<p align="center">
  <img src="assets/logo-dark.svg#gh-dark-mode-only" alt="Novelist" />
  <img src="assets/logo-light.svg#gh-light-mode-only" alt="Novelist" />
</p>

<h1 align="center">Novelist</h1>
<p align="center">
  A local-first AI workbench for long-form fiction: structured memory, Agent tools, Skill methodology, reference anchoring, draft audit, and version history.
</p>

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

Novelist is built for long-form fiction projects. It is not just a chat shell, and it is not only a collection of prompts or Skills. It turns a novel project into queryable, maintainable, and auditable structured state, so AI can assist the work while critical writes still stay under author control.

## Core Capabilities

| Capability | Description |
|---|---|
| Structured creative state | Manages characters, relationships, foreshadowing, arcs, locations, reader knowledge, preferences, and chapter plans |
| Agent tool use | Lets AI inspect chapters, search prior text, update project state, maintain preferences, and generate candidates |
| Skill methodology | Markdown Skills provide scene beats, dialogue subtext, pacing, hooks, revision, de-AI polishing, and other workflows |
| Local semantic search | RAG state lives in SQLite/sqlite-vec; embeddings use an online OpenAI-compatible API or local ONNX |
| Diff approval and history | Chapter edits go through explicit save and approval boundaries, with Git history for project changes |
| Reference anchoring | Turns sources, blueprints, material bindings, draft candidates, and audit results into checkable records |

## Reference Anchoring

The reference anchor layer answers: what may the AI write, what should it be based on, and is the result safe to use? Skills handle method and style. Reference anchoring handles sources, factual boundaries, POV, blueprint quality, material binding, and draft audit.

Default flow:

```text
author confirms source policy, chapter target, known facts, and forbidden facts
  -> start orchestration run
  -> generate chapter blueprint
  -> run deterministic blueprint review
  -> author approves the blueprint or approves AI-proposed field-level revisions
  -> retrieve and bind reference materials
  -> generate beat-level draft candidates
  -> run draft audit
  -> stop at final chapter-insertion confirmation
```

The workflow stops for author confirmation when:

- source, license, known-fact, or forbidden-fact boundaries are unclear;
- the blueprint is stale, materials are missing, retrieval is weak, or material hashes mismatch;
- blueprint review fails and needs revision;
- rewrite level, POV, fact boundaries, or audit findings are high risk;
- a candidate draft is ready to be inserted into chapter prose.

The reference anchor workflow does not automatically call `SaveContent` or write chapter prose. AI may propose candidates and revisions; final insertion still goes through author-confirmed editing and saving.

## Custom Skills

Skills are writing-methodology modules. Each Skill is a Markdown file with YAML frontmatter, with three override layers and three activation modes:

| Mechanism | Description |
|---|---|
| Override order | Novel-level `skills/<name>.md` > user-level `~/.novelist/skills/<name>.md` > read-only built-in `/builtin/skills/<name>.md` |
| Activation modes | `auto` can be invoked by AI and by user `/` commands; `manual` only supports user `/` commands; `always` is injected at session start |
| State file | `novelist.md` stores story state so the Agent can recover context and maintain long-term continuity |

Minimal Skill file:

```markdown
---
name: Pacing Control
description: Control scene progression, pauses, and suspense release
category: Writing Method
mode: auto
---

# Usage

Adjust narrative pacing according to the current chapter target.
```

## Current Status

| Area | Status |
|---|---|
| Desktop mainline | Migrated to `.NET 10 + Photino.NET + React/Vite` |
| Reference anchoring | Phases 0-13 are complete at the current implementation boundary; Phase 14 is open for advanced material understanding, style profiles, style-aware retrieval, imitation candidates, and source-leak/style-quality audit |
| Frontend build | Vite 8/Rolldown splits the app shell, workspace, Monaco, Markdown, Mermaid, and graph dependencies |
| Retired implementations | Go/Wails and the old Python path are retired; new work should not go under `app/`, `internal/`, `python-master/`, or `frontend/src/lib/wailsjs/` |

## Latest Updates

### 2026-07-07

- Reference-anchored draft audit now shows a readable report in the candidate area, including candidate IDs, finding category, severity, and required action.
- Draft generation and manual audit both persist the audit report with candidate IDs; the report does not add candidate prose, source prose, or prompts.

See [Release Notes](docs/releases/release-notes.md) for the full change history.

## Screenshots

<p align="center">
  <img src="assets/write-demo.png" width="80%" alt="Chapter writing" />
</p>
<p align="center">
  <img src="assets/arc-demo.png" width="48%" alt="Story arcs" />
  <img src="assets/location-demo.png" width="48%" alt="Location graph" />
</p>
<p align="center">
  <img src="assets/preferences-demo.png" width="48%" alt="Writing preferences" />
  <img src="assets/skill-demo.png" width="48%" alt="Skill system" />
</p>

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

Download the installer for your platform from [Releases](https://github.com/devhxj/novelist/releases):

- **Windows**: run the installer
- **macOS**: open the DMG and drag to Applications
- **Linux**: run the AppImage

An LLM API key is required. Built-in provider templates include DeepSeek, GLM, and MiMo, and OpenAI-compatible endpoints are supported. Installers include the desktop host, frontend assets, and Git runtime. No Python, Node.js, or external database is required.

Semantic search can use an online Embeddings API or the built-in ONNX mode. ONNX mode uses the bundled fixed `bge-small-zh-v1.5` int8 model and does not silently fall back to online APIs.

Windows SmartScreen may warn about an unsigned app; choose "More info" and continue if you trust the build.

## Build From Source

Requirements:

- .NET 10 SDK
- Node.js/npm
- GNU make and bash
- GTK/WebKit dependencies for Linux desktop runtime

```bash
sudo apt install libgtk-3-0 libwebkit2gtk-4.1-0 curl file unzip
git clone https://github.com/devhxj/novelist
cd novelist
dotnet restore Novelist.slnx
npm --prefix frontend ci
make deps
make build
```

Start desktop development mode:

```bash
npm --prefix frontend run build
make dev
```

Frontend-only debugging:

```bash
make frontend-dev
```

`make frontend-dev` only starts Vite, so desktop bridge APIs are unavailable. To use the bridge against Vite, launch the Photino host with `--start-url=http://localhost:5173/`.

## Common Commands

| Command | Purpose |
|---|---|
| `make deps` | Download or reuse the packaged Git runtime |
| `make dev` | Start the Photino/.NET desktop app |
| `make build` | Build frontend assets, prepare runtime dependencies, and publish desktop output |
| `make publish RID=win-x64` | Publish a self-contained build for a target RID |
| `make package-windows` | Build the Windows installer |
| `make package-linux` | Build the Linux AppImage |
| `make package-macos` | Build the macOS DMG |
| `npm --prefix frontend run build` | Run TypeScript build and Vite production build |
| `npm --prefix frontend run lint` | Run frontend ESLint |
| `npm --prefix frontend run verify` | Run frontend build, lint, reference-anchor workflow, and baseline app-wide smoke test; release-grade regression also requires Phase 13 full/stress/usability commands, and Phase 14 will add reference-style commands |
| `dotnet test Novelist.slnx --no-restore -v minimal` | Run the .NET test suite |

## Quality Boundaries

When developing or reviewing this codebase, preserve these boundaries:

- chapter prose writes must require author confirmation; reference-anchor orchestration must not directly save prose;
- filesystem access must keep SafePath and sandbox checks;
- web and external-resource tools must keep SSRF protection;
- user-data migration must be copy-first, leave the source untouched, and write a manifest;
- API keys, local model paths, and user data must stay out of git;
- runtime Git and local ONNX model files belong in `build/runtime/` or app data/config paths; ONNX Runtime and sqlite-vec ship through NuGet publish assets, and any override libraries must stay out of source folders.

## Documentation

- [Reference Anchor Technical Baseline](docs/reference-anchor-layer-plan.md)
- [Reference Anchor Implementation Plan](docs/reference-anchor-implementation-plan.md)
- [Photino Bridge Contract](docs/novelist-photino-bridge-contract.md)
- [Release Notes](docs/releases/release-notes.md)

## License And Origin

Novelist is released under the MIT License; see [LICENSE](LICENSE). The project began as a fork of the MIT-licensed GoInk line and has since been substantially rebuilt as a `.NET 10 + Photino.NET + React/Vite` application. See [NOTICE](NOTICE) for attribution and compatibility boundaries.

This repository does not merge upstream code added after the upstream relicensing to AGPL. Keep the MIT copyright and permission notice when using or distributing copies or substantial portions of this software.
