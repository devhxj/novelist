# Repository Guidelines

## Project Structure & Module Organization

Goink is a Wails desktop app with a Go backend and React frontend. Root Go entrypoints are `main.go` and `app/`, where Wails-exported methods form the frontend API. Domain code lives under `internal/` (`agent`, `mcp_tools`, `storage`, `chapter`, `rag`, etc.) with tests beside packages as `*_test.go`. React/TypeScript source is in `frontend/src/`; generated Wails bindings are in `frontend/src/lib/wailsjs/`. Assets and packaging files live in `assets/` and `build/`; design notes live in `docs/`. `python-master/` is historical/reference code, not the main app path.

## Build, Test, and Development Commands

- `make deps`: download Git and ONNX Runtime into `build/runtime/`.
- `make dev`: run Wails dev mode with Go backend and Vite hot reload.
- `make build`: build frontend assets, download deps if needed, then run the production Wails build.
- `make frontend-dev`: run only the Vite frontend; backend APIs are unavailable.
- `go test $(go list ./internal/... ./app/...) -count=1`: run the same Go test scope used by CI.
- `cd frontend && npm ci && npm run build`: install frontend dependencies, then run TypeScript and Vite build.
- `cd frontend && npm run lint`: run ESLint for TypeScript/React files.

## Coding Style & Naming Conventions

Use `gofmt` on Go files and keep package names short, lowercase, and domain-oriented. Run Go commands from the repository root. Frontend work uses TypeScript, React 19, Tailwind CSS 4, shadcn/ui patterns, and ESLint flat config. Run npm commands from `frontend/`. Do not hand-edit `frontend/src/lib/wailsjs/`; regenerate bindings with Wails tooling when Go APIs change.

## Testing Guidelines

Prefer package-level Go tests beside implementation files (`store_test.go`, `service_test.go`, `*_bench_test.go`). Add tests for storage, safety, parsing, and domain behavior changes. CI validates `internal/...` and `app/...`; run frontend build and lint before UI changes because no frontend test suite is configured.

## Commit & Pull Request Guidelines

Recent history uses concise English subjects with typed prefixes: `feat: ...`, `fix: ...`, `test: ...`, `docs: ...`, and occasional `assets:`. Keep subjects specific; avoid emoji and `Co-Authored-By` trailers. PRs should describe behavior changes, list verification commands, link issues when available, and include screenshots or recordings for UI changes.

## Security & Configuration Tips

Keep API keys, local model paths, and user data out of git. Preserve SafePath, approval-flow, and sandbox checks when touching file editing or MCP tool code. Runtime models and ONNX libraries belong in `build/runtime/` or the app data directory, not source folders.
