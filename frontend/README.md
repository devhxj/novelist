# Novelist Frontend

This directory contains the React/Vite frontend for the Novelist Photino desktop app.

## Commands

Run commands from `frontend/` unless the root Makefile target is shown.

```bash
npm ci
npm run dev
npm run build
npm run lint
npm run verify
npm run test:chapter-reference
npm run test:corpus-library
npm run test:app:usability
```

Root-level equivalents use `--prefix frontend`:

```bash
npm --prefix frontend run dev
npm --prefix frontend run build
npm --prefix frontend run verify
```

`npm run dev` starts Vite on `http://localhost:5173/`. It is only a frontend asset server; desktop bridge APIs are unavailable unless the Photino host is running with `--start-url=http://localhost:5173/`.

## Runtime Shape

- The packaged app loads `frontend/dist/index.html` directly through Photino.
- `vite.config.ts` uses `base: './'` so built assets work from local files.
- React code talks to the backend through the owned adapter in `src/lib/novelist/`.
- Do not add Wails generated bindings or imports under `src/lib/wailsjs/`.

## Key Paths

```text
src/lib/novelist/      Photino bridge adapter, events, runtime helpers, DTOs
src/hooks/useApp.ts    Application API hook used by React components
src/components/        UI components
src/views/             Top-level views
```

For the backend bridge contract, see `../docs/novelist-photino-bridge-contract.md`.

## UI Rules

- Reuse the existing tokens, shadcn/Radix patterns, shared `Button`, and Lucide icons.
- Keep automatic workflows on one primary path and progressively disclose expert controls.
- For user-facing changes, verify loading, empty, error, blocked, recovery, and completed states; include keyboard/focus behavior and narrow desktop layouts.
- See `../docs/frontend/theme-rules.md`, `../docs/frontend/theme-design.md`, and `../docs/rules/frontend-crud-patterns.md`.
