# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Novelist is a local-first AI long-form writing workstation built as a .NET 10 + Photino.NET desktop app with a React/Vite frontend. It manages character state, reference materials, writing methodology (Skills), and version history with embedded AI assistance.

## Commands

All .NET commands run from the **repository root**. All `npm` commands run from `frontend/` or use `--prefix frontend`.

### Install dependencies

```bash
dotnet restore Novelist.slnx
npm --prefix frontend ci
```

### Build

```bash
npm --prefix frontend run build           # TypeScript + Vite production build
dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop  # Launch desktop app (requires frontend/dist to exist)
```

### Development

```bash
npm --prefix frontend run dev             # Vite dev server only (bridge APIs unavailable)
# To use hot-reload with full desktop: launch Vite first, then launch app with --start-url=http://localhost:5173/
```

### Test

```bash
dotnet test Novelist.slnx --no-restore -v minimal    # .NET unit + integration tests
npm --prefix frontend run lint                        # ESLint
npm --prefix frontend run verify                      # build + lint + phase16 + reference-anchor + smoke tests
npm --prefix frontend run test:app                    # frontend smoke suite (Playwright mock workflow)
npm --prefix frontend run test:app:usability          # app usability workflow
npm --prefix frontend run test:phase16                # corpus-library + chapter-reference suites
npm --prefix frontend run test:corpus-library         # focused corpus-library workflow
npm --prefix frontend run test:chapter-reference      # focused chapter-reference workflow
npm --prefix frontend run test:reference-anchor       # reference anchor workflow
```

### Publish

```bash
bash scripts/novelist-publish.sh win-x64              # self-contained output → build/bin/novelist/
VERSION=1.2.3 bash scripts/novelist-package-windows.sh  # Windows installer → build/dist/
```

## Architecture

The app uses a **Photino bridge** as the IPC layer between .NET and the React frontend. All frontend-to-backend communication goes through named bridge methods dispatched by `Novelist.Core`.

### Backend layers (`src/`)

| Project | Role |
|---|---|
| `Novelist.App` | Photino desktop host; resolves and serves frontend assets |
| `Novelist.Contracts` | Bridge DTOs shared across layers — add new contracts here |
| `Novelist.Core` | App interfaces and bridge dispatch routing |
| `Novelist.Infrastructure` | Filesystem I/O, SQLite/sqlite-vec storage, LibGit2Sharp version history, ONNX embeddings, RAG |
| `Novelist.Agent` | Microsoft Agent Framework tool adapters for AI assistance |

### Frontend (`frontend/src/`)

| Path | Role |
|---|---|
| `lib/novelist/` | Owned Photino bridge adapter — TypeScript client for all backend calls |
| `components/` | Reusable React UI components (shadcn/ui patterns, Radix UI, Tailwind 4) |
| `views/` | Page-level React components |
| `hooks/` | Custom React hooks |

### Key technical dependencies

- **SQLite + sqlite-vec**: vector search for semantic retrieval; `sqlite-vec` native lib is shipped via NuGet and does not require manual setup
- **LibGit2Sharp**: embedded Git for version history; the app does **not** require a system Git installation
- **ONNX Runtime**: local embeddings via `bge-small-zh-v1.5` int8 (512-dim, CLS pooling + L2 norm); requires model placed at `build/runtime/models/{model.onnx,vocab.txt}`. Online Embeddings API mode has no such requirement
- **MinVer**: version derived from Git tags (`vX.Y.Z`); no manual version bumps needed

### Reference anchoring and corpus-driven writing

`SqliteReferenceAnchorService` owns the established material/provenance boundary; corpus-driven writing adds cross-library retrieval, multi-blueprint selection, source-locked candidates, and the analysis scheduler. Use `docs/reference-anchor-layer-plan.md` for the historical technical baseline and `docs/corpus-driven-writing/development-plan.md` plus `tasks.md` for the active plan/status. Preserve the evidence tiers: 1K job-store micro-benchmark, required 50K full pipeline (already passed), optional 2M long run. M9 automated default-path, accessibility, and recovery closure is complete; real-user validation remains open, and expert-feature expansion is out of scope.

## Coding Conventions

- Keep contracts in `Novelist.Contracts`, interfaces in `Novelist.Core`, and runtime implementations in `Novelist.Infrastructure`
- Frontend bridge methods and owned DTOs go under `frontend/src/lib/novelist/` and `src/Novelist.Contracts/`
- Use idiomatic C# with nullable annotations
- Frontend: TypeScript, React 19, Tailwind CSS 4, shadcn/ui, ESLint flat config
- Do **not** add code under `goink-master/`, `app/`, `internal/`, or `python-master/` — these are legacy read-only references
- Do **not** recreate `frontend/src/lib/wailsjs/`; the Photino bridge adapter replaces it

## Commit Style

Concise English subjects with typed prefixes: `feat:`, `fix:`, `test:`, `docs:`, `assets:`. No emoji, no `Co-Authored-By` trailers. For UI changes, PRs should include screenshots or recordings.

## Security Notes

- API keys, local model paths, and user data must not enter git
- Preserve `SafePath`, approval-flow, SSRF checks, and sandbox checks when touching file editing, web tools, or agent tool code
- Migration operations must copy-first and leave the source untouched; write a manifest
- Runtime binaries, downloaded models, and machine-specific overrides under `build/runtime/` must stay untracked; preserve explicitly approved tracked assets such as `build/runtime/models/vocab.txt`
