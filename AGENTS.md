# Repository Guidelines

## Project Structure & Module Organization

Novelist is a .NET 10 + Photino.NET desktop app with a React/Vite frontend. The primary backend lives under `src/`: `Novelist.App` hosts the desktop window and local frontend asset resolution, `Novelist.Contracts` owns bridge DTOs, `Novelist.Core` owns app interfaces and bridge dispatch, `Novelist.Infrastructure` owns filesystem/storage/RAG implementations, and `Novelist.Agent` owns Microsoft Agent Framework tool adapters. Tests live under `tests/Novelist.Tests` and `tests/Novelist.IntegrationTests`. React/TypeScript source is in `frontend/src/`; `frontend/src/lib/novelist/` is the owned bridge adapter. The Photino bridge is the only legal host-to-frontend channel — there is no separate HTTP server.

Reference/corpus subsystem is mid-refactor on the `feature/whole-chapter-materialization` branch. The current direction is "整章素材化"（whole-chapter materialization): the chapter side selects an active reference book per outline/goal, generates one materialization blueprint for the whole chapter, then emits prose candidates derived only from that blueprint and its source evidence. The legacy corpus retrieval/analysis/governance, candidate-materialization, reference draft/style, and old reference schema/draft/style systems have been removed — see recent `refactor: remove legacy ...` and `refactor: replace legacy reference schema` commits. Before changing reference materialization, search, blueprint preview, or chapter-material extractor code, read `docs/reference-anchor-implementation/decisions-materials-and-audit.md` and `docs/corpus-driven-writing/development-plan.md` for the current contract and the retired-path list.

## Build, Test, and Development Commands

- `dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop`: run the .NET Photino desktop app after frontend assets are built.
- `npm --prefix frontend run build`: run TypeScript and Vite production build.
- `bash scripts/novelist-publish.sh win-x64`: publish a self-contained Windows app output to `build/bin/novelist/`.
- `VERSION=1.2.3 bash scripts/novelist-package-windows.sh`: build the Windows installer into `build/dist/`.
- `npm --prefix frontend run dev`: run only the Vite frontend; backend bridge APIs are unavailable unless the desktop host is launched with `--start-url=http://localhost:5173/`.
- `dotnet test Novelist.slnx --no-restore -v minimal`: run the .NET test suite used by CI.
- `dotnet build Novelist.slnx`: typecheck/compile the C# solution from the repository root (use before opening a PR).
- `cd frontend && npm ci && npm run build`: install frontend dependencies, then run TypeScript (`tsc -b`) and Vite build.
- `cd frontend && npm run lint`: run ESLint flat config for TypeScript/React files.
- `cd frontend && npm run test:reference-writing-api` / `test:reference-materialization-api`: focused `node --test` API contract suites for the reference writing and materialization bridge.
- `npm --prefix frontend run verify`: run frontend build, lint, reference-writing API, phase16 (reference-workspace + chapter-reference) workflows, and the app smoke suite. Treat this as the frontend PR gate.
- `npm --prefix frontend run test:reference-workspace`: focused reference-book management and transient blueprint-preview browser workflow (`@reference-workspace`).
- `npm --prefix frontend run test:app`: app smoke workflow (`--suite=smoke --target=vite`); `test:app:full` / `test:app:stress` / `test:app:usability` for the wider browser suites.

## Coding Style & Naming Conventions

Use idiomatic C# with nullable annotations and keep contracts in `Novelist.Contracts`, interfaces in `Novelist.Core`, and filesystem/runtime implementations in `Novelist.Infrastructure`. Run .NET commands from the repository root. Frontend work uses TypeScript, React 19, Tailwind CSS 4, shadcn/ui patterns, and ESLint flat config. Run npm commands from `frontend/`. Add owned frontend DTOs or bridge methods under `frontend/src/lib/novelist/` and `src/Novelist.Contracts/`.

Do not revive retired legacy implementations: do not add new code under legacy `goink-master/`, `app/`, `internal/`, or `python-master/`, do not run Go/Wails or old Python build commands, and do not recreate `frontend/src/lib/wailsjs/`. Compatibility behavior belongs in the Photino bridge, .NET contracts, and owned TypeScript adapter.

## Testing Guidelines

Prefer focused unit tests in `tests/Novelist.Tests` for pure bridge/tool behavior and integration tests in `tests/Novelist.IntegrationTests` for filesystem, SQLite, Git, migration, and app-host behavior. Add tests for storage, path safety, migrations, version history, bridge contracts, and user-facing workflow changes. UI changes require build, lint, the focused browser workflow, and screenshots for changed states; verify keyboard/focus, narrow desktop layouts, long-task recovery, and error recovery when relevant.

For corpus-driven writing, follow `docs/corpus-driven-writing/development-plan.md` and `docs/corpus-driven-writing/tasks.md`: keep the 1,000-item job-store micro-benchmark, use 50K as the required full scheduler/builder/worker/fake-analyzer gate, and reserve 2M for explicit non-blocking long runs. The formal 50K gate has passed; future changes must preserve it. All milestones M0–M9 are currently `S` (no milestone is `P` or `L`); do not word a commit or PR as if M2/M5/M9 were product-complete. M9 chapter-default, accessibility, and recovery evidence is complete; the Corpus Library reference-book and blueprint-preview workflow (`test:reference-workspace`) is the open acceptance item, and real-user validation remains open. Do not expand the expert control surface or add new audit rules.

## Commit & Pull Request Guidelines

Recent history uses concise English subjects with typed prefixes: `feat: ...`, `fix: ...`, `test: ...`, `docs: ...`, and occasional `assets:`. Keep subjects specific; avoid emoji and `Co-Authored-By` trailers. PRs should describe behavior changes, list verification commands, link issues when available, and include screenshots or recordings for UI changes. Release workflow lives in `docs/releases/release-process.md`.

## Security & Configuration Tips

Keep API keys, local model paths, and user data out of git. Preserve SafePath, approval-flow, SSRF checks, migration copy-first behavior, and sandbox checks when touching file editing, web tools, or agent tool code. Runtime Git, optional ONNX Runtime/model assets, and optional sqlite-vec native libraries belong in `build/runtime/` or app data/config paths, not source folders. Existing user data migration must leave the source untouched and write a manifest.

The user usually communicates in Chinese; respond in Chinese unless they ask otherwise.
