# Repository Guidelines

## Project Structure & Module Organization

Novelist is a .NET 10 + Photino.NET desktop app with a React/Vite frontend. The primary backend lives under `src/`: `Novelist.App` hosts the desktop window and local frontend asset resolution, `Novelist.Contracts` owns bridge DTOs, `Novelist.Core` owns app interfaces and bridge dispatch, `Novelist.Infrastructure` owns filesystem/storage/RAG implementations, and `Novelist.Agent` owns Microsoft Agent Framework tool adapters. Tests live under `tests/Novelist.Tests` and `tests/Novelist.IntegrationTests`. React/TypeScript source is in `frontend/src/`; `frontend/src/lib/novelist/` is the owned bridge adapter.

## Build, Test, and Development Commands

- `make deps`: download or reuse the bundled Git runtime under `build/runtime/`.
- `make dev`: run the .NET Photino desktop app.
- `make build`: build frontend assets, prepare runtime deps, then publish `Novelist.App` to `build/bin/novelist/`.
- `make publish RID=win-x64`: publish a self-contained RID-specific app output.
- `make package-windows`, `make package-linux`, `make package-macos`: build platform packages into `build/dist/`.
- `make frontend-dev`: run only the Vite frontend; backend bridge APIs are unavailable.
- `dotnet test Novelist.slnx --no-restore -v minimal`: run the .NET test suite used by CI.
- `cd frontend && npm ci && npm run build`: install frontend dependencies, then run TypeScript and Vite build.
- `cd frontend && npm run lint`: run ESLint for TypeScript/React files.

## Coding Style & Naming Conventions

Use idiomatic C# with nullable annotations and keep contracts in `Novelist.Contracts`, interfaces in `Novelist.Core`, and filesystem/runtime implementations in `Novelist.Infrastructure`. Run .NET commands from the repository root. Frontend work uses TypeScript, React 19, Tailwind CSS 4, shadcn/ui patterns, and ESLint flat config. Run npm commands from `frontend/`. Add owned frontend DTOs or bridge methods under `frontend/src/lib/novelist/` and `src/Novelist.Contracts/`.

Do not revive retired legacy implementations: do not add new code under legacy `app/`, `internal/`, or `python-master/`, do not run Go/Wails or old Python build commands, and do not recreate `frontend/src/lib/wailsjs/`. Compatibility behavior belongs in the Photino bridge, .NET contracts, and owned TypeScript adapter.

## Testing Guidelines

Prefer focused unit tests in `tests/Novelist.Tests` for pure bridge/tool behavior and integration tests in `tests/Novelist.IntegrationTests` for filesystem, SQLite, Git, migration, and app-host behavior. Add tests for storage, path safety, migrations, version history, bridge contracts, and user-facing workflow changes. Run the frontend build before UI changes because no frontend test suite is configured.

## Commit & Pull Request Guidelines

Recent history uses concise English subjects with typed prefixes: `feat: ...`, `fix: ...`, `test: ...`, `docs: ...`, and occasional `assets:`. Keep subjects specific; avoid emoji and `Co-Authored-By` trailers. PRs should describe behavior changes, list verification commands, link issues when available, and include screenshots or recordings for UI changes. Release workflow lives in `docs/releases/release-process.md`.

## Security & Configuration Tips

Keep API keys, local model paths, and user data out of git. Preserve SafePath, approval-flow, SSRF checks, migration copy-first behavior, and sandbox checks when touching file editing, web tools, or agent tool code. Runtime Git, optional ONNX Runtime/model assets, and optional sqlite-vec native libraries belong in `build/runtime/` or app data/config paths, not source folders. Existing user data migration must leave the source untouched and write a manifest.

The user usually communicates in Chinese; respond in Chinese unless they ask otherwise.
