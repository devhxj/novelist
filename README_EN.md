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
  A local-first AI workbench for long fiction: manage character state, references, writing methods, and version history, then add narrative point of view before generation.
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

Novelist is built for long-form fiction. Fiction depends on emotional resonance, and current AI is not reliable at controlling emotional progression, expressive restraint, or a character's inner reaction. Raw generation often slips out of the character's inner view and starts explaining the plot. If the character's emotion, misunderstanding, blind spots, and bodily reactions are not prepared first, the model easily falls into explanatory sentences, screenplay-like action, and parenthetical notes.

Novelist keeps character state, reference material, Skills, RAG, and Git in one local-first workspace. Before generation, it organizes narrative point of view and constraints. After generation, audits, diffs, and save boundaries keep final manuscript writes under author control.

It does not treat AI as the independent author. Story direction, character relationships, themes, and key plot decisions stay author-defined. AI mainly expands, continues, rewrites, and imitates within reference-anchored boundaries, turning author intent and approved reference material into auditable candidates.

## Design Rationale

<table>
  <thead>
    <tr>
      <th width="24%">Issue</th>
      <th width="76%">Approach</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><strong>The author sets the core</strong></td>
      <td>Story direction, character relationships, themes, and key plot decisions are set by the author first. AI expands, continues, rewrites, and performs reference-anchored imitation within those boundaries.</td>
    </tr>
    <tr>
      <td><strong>Weak emotional control</strong></td>
      <td>Fiction depends on emotional resonance. AI can describe emotion, but it is less reliable at controlling where the emotion comes from, where it moves, and how much should be shown. Novelist prepares emotional state before generation.</td>
    </tr>
    <tr>
      <td><strong>Missing narrative awareness</strong></td>
      <td>Good sentences are not enough. AI often turns scenes into explanation or screenplay, without a narrator who has emotion, bias, and limited knowledge.</td>
    </tr>
    <tr>
      <td><strong>Style ingredients are limited</strong></td>
      <td>Labels such as short sentences, conversational tone, or plain description can reduce mistakes, but they do not create human texture on their own. Bias, blind spots, bodily feeling, and drifting thought need to participate as a whole state.</td>
    </tr>
    <tr>
      <td><strong>External reasoning layer</strong></td>
      <td>With an API-only setup, emotional cognition and narrative point of view live outside the model. Before generation, a Tool infers the character's current emotion, expression, knowledge boundary, and visible information, then injects those parameters into the prompt.</td>
    </tr>
  </tbody>
</table>

## Core Capabilities

<table>
  <thead>
    <tr>
      <th width="24%">Capability</th>
      <th width="76%">What it does</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><strong>Narrative point-of-view reasoning</strong></td>
      <td>Prepare the character's current emotion, bias, blind spots, visible information, and narrative position before generation.</td>
    </tr>
    <tr>
      <td><strong>Structured story state</strong></td>
      <td>Keep characters, relationships, foreshadowing, arcs, locations, reader knowledge, preferences, and chapter plans queryable.</td>
    </tr>
    <tr>
      <td><strong>Agent tools</strong></td>
      <td>Let AI read chapters, search prior text, maintain project state, and propose candidates, without silently writing chapter prose.</td>
    </tr>
    <tr>
      <td><strong>Skills</strong></td>
      <td>Store reusable writing procedures in Markdown, such as scene beats, dialogue subtext, pacing, revision, and de-AI passes.</td>
    </tr>
    <tr>
      <td><strong>Reference anchoring and audit</strong></td>
      <td>Record where reference novels or materials came from, what may be used, and whether a draft leaks source text or crosses a risk boundary.</td>
    </tr>
    <tr>
      <td><strong>Local search and history</strong></td>
      <td>Store RAG state in SQLite/sqlite-vec. Route prose writes through approval boundaries and keep Git history for project changes.</td>
    </tr>
  </tbody>
</table>

## Reference Anchoring

Skills store writing methods. Reference anchoring constrains material use and risk. Sources, factual boundaries, POV, blueprints, material bindings, and draft audits leave records here, so the model does not invent freely, leak source text, or write risky prose directly into the manuscript.

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

Skills store reusable writing methods; they do not replace narrative state. Each Skill is a Markdown file with YAML frontmatter, with three override layers and three activation modes:

<table>
  <thead>
    <tr>
      <th width="22%">Mechanism</th>
      <th width="78%">Description</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><strong>Override order</strong></td>
      <td>Novel-level <code>skills/&lt;name&gt;.md</code><br />User-level <code>~/.novelist/skills/&lt;name&gt;.md</code><br />Read-only built-in <code>/builtin/skills/&lt;name&gt;.md</code></td>
    </tr>
    <tr>
      <td><strong>Activation modes</strong></td>
      <td><code>auto</code> can be invoked by AI or by user <code>/</code> commands. <code>manual</code> is user-triggered only. <code>always</code> is injected at session start.</td>
    </tr>
    <tr>
      <td><strong>State file</strong></td>
      <td><code>novelist.md</code> stores story state so the Agent can recover context and preserve long-term continuity.</td>
    </tr>
  </tbody>
</table>

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

<table>
  <thead>
    <tr>
      <th width="24%">Area</th>
      <th width="76%">Status</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><strong>Desktop mainline</strong></td>
      <td>Migrated to <code>.NET 10 + Photino.NET + React/Vite</code>.</td>
    </tr>
    <tr>
      <td><strong>Reference anchoring</strong></td>
      <td>Phases 0-14 are complete at the current implementation boundary, including material understanding, style profiles, style-aware retrieval, imitation candidates, and audit.</td>
    </tr>
    <tr>
      <td><strong>Phase 15</strong></td>
      <td>In progress: novel import, style material library, narrative pattern extraction, Git history UI, and product hardening.</td>
    </tr>
    <tr>
      <td><strong>Frontend build</strong></td>
      <td>Vite 8/Rolldown splits the app shell, workspace, Monaco, Markdown, Mermaid, and graph dependencies.</td>
    </tr>
    <tr>
      <td><strong>Origin</strong></td>
      <td>Novelist originates from <a href="https://github.com/sigpanic/goink">goink</a> and has been rebuilt into the current desktop writing workbench.</td>
    </tr>
  </tbody>
</table>

## Latest Updates

### 2026-07-07

- The bookshelf can now import local EPUB/TXT/Markdown files through the desktop picker or drag and drop. Unsupported dropped content shows immediate feedback.
- The desktop update-check endpoint now comes from app build configuration or launch arguments. No live release URL is baked in by default, and automatic checks stay disabled by default.
- Reference-anchor orchestration can select the active style profile and tune imitation intensity, fit, evidence, and risk policy. The policy is written into the pending blueprint `style_contract`.
- When orchestration hits material gaps, weak retrieval, provenance gaps, or source-leak risk, it reports concrete recovery actions such as adding material, relaxing filters, rebinding stronger material, lowering imitation strength, or regenerating the candidate.
- Draft audit now blocks high whole-candidate/source similarity. Reports show ratios and thresholds, not source prose or candidate prose.
- Draft audit also checks style-quality risks under imitation pressure, including abstract summary, direct emotion telling, mechanical connectors, three-part structures, and formulaic phrases.
- Reference-style profiles now have a 10 MB browser stress regression covering large-corpus build progress, profile detail, and material-library paging.
- The style profile library shows build progress, diagnostics, and error states, supports canceling a running build, and can refresh after failure or cancellation.
- Reference-style profile builds now persist recoverable build state. State records do not store source prose, candidate prose, prompts, or file paths.
- Agent reference tools can no longer approve blueprints with a `style_contract`. AI may propose and review style-contract changes, but approval remains a user action.
- Style and source-leak audit findings can be read through the desktop bridge and Agent tools. They do not return candidate prose, source prose, prompts, or chapter-write capability.
- Draft audit blocks candidates that carry long source passages. Reports show reuse length and ratio without echoing source sentences.
- The reference-style golden suite now covers eight style categories and generates reports without source prose or candidate prose.
- Draft audit reports are visible in the candidate area with candidate ID, finding category, severity, and required action.
- Draft generation and manual audit both persist audit reports with candidate IDs. Reports do not add candidate prose, source prose, or prompts.
- Persisted audit reports can be read through the desktop bridge and Agent tools by blueprint, candidate ID, and limit. They do not provide prose-write or approval capability.

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

Current builds target Windows first. Download the Windows installer from [Releases](https://github.com/devhxj/novelist/releases) and run it.

An LLM API key is required. Built-in provider templates include DeepSeek, GLM, and MiMo, and OpenAI-compatible endpoints are supported. The Windows installer includes the desktop host and frontend assets. No Python, Node.js, or external database is required. Local version history uses system Git; if Git is not installed, related features will ask the user to install it.

Semantic search can use an online Embeddings API or the built-in ONNX mode. ONNX mode uses the bundled fixed `bge-small-zh-v1.5` int8 model and does not silently fall back to online APIs.

Windows SmartScreen may warn about an unsigned app; choose "More info" and continue if you trust the build.

## Build From Source

Requirements:

- Windows 10/11
- .NET 10 SDK
- Node.js/npm
- Git Bash
- Git, for local version history
- Inno Setup 6, only when building the Windows installer

```bash
git clone https://github.com/devhxj/novelist
cd novelist
dotnet restore Novelist.slnx
npm --prefix frontend ci
npm --prefix frontend run build
bash scripts/novelist-publish.sh win-x64
```

Start desktop development mode:

```bash
npm --prefix frontend run build
dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop
```

Frontend-only debugging:

```bash
npm --prefix frontend run dev
```

Starting only Vite leaves desktop bridge APIs unavailable. To use the bridge against Vite, launch the Photino host with `--start-url=http://localhost:5173/`.

## Common Commands

<table>
  <thead>
    <tr>
      <th width="40%">Command</th>
      <th width="60%">Purpose</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><code>dotnet&nbsp;run&nbsp;--project&nbsp;src/Novelist.App/Novelist.App.csproj&nbsp;--&nbsp;--desktop</code></td>
      <td>Start the Photino/.NET desktop app.</td>
    </tr>
    <tr>
      <td><code>bash&nbsp;scripts/novelist-publish.sh&nbsp;win-x64</code></td>
      <td>Publish a self-contained build for a target RID.</td>
    </tr>
    <tr>
      <td><code>VERSION=1.2.3&nbsp;bash&nbsp;scripts/novelist-package-windows.sh</code></td>
      <td>Build the Windows installer.</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;dev</code></td>
      <td>Start the Vite frontend development server.</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;build</code></td>
      <td>Run TypeScript build and Vite production build.</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;lint</code></td>
      <td>Run frontend ESLint.</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;test:reference-style</code></td>
      <td>Run the reference-style browser workflow and generate the usability report.</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;test:reference-style:stress</code></td>
      <td>Run the 10 MB reference-style browser stress workflow.</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;verify</code></td>
      <td>Run frontend build, lint, reference-anchor workflow, and baseline app-wide smoke test.</td>
    </tr>
    <tr>
      <td><code>dotnet&nbsp;test&nbsp;Novelist.slnx&nbsp;--no-restore&nbsp;-v&nbsp;minimal</code></td>
      <td>Run the .NET test suite.</td>
    </tr>
  </tbody>
</table>

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

Novelist is released under the MIT License; see [LICENSE](LICENSE). The project began as a fork of the MIT-licensed [goink](https://github.com/sigpanic/goink) line and has since been substantially rebuilt as a `.NET 10 + Photino.NET + React/Vite` application. See [NOTICE](NOTICE) for attribution and compatibility boundaries.

This repository does not merge upstream code added after the upstream relicensing to AGPL. Keep the MIT copyright and permission notice when using or distributing copies or substantial portions of this software.
