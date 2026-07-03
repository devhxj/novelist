# Novelist Frontend to Photino Handoff

## Purpose

This document records how the existing React/Vite UI is handed to the new Photino.NET app host during the migration. It does not change the current Wails runtime.

## Current Frontend Build

The existing frontend remains in `frontend/`.

```powershell
npm --prefix frontend ci
npm --prefix frontend run build
```

Vite writes production assets to `frontend/dist/`. That directory is build output and must not be committed.

## Photino Consumption Path

The first Photino slices use an explicit desktop launch flag:

```powershell
dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop
```

At P1.2 this loaded `about:blank` by design. P1.4/P1.5 added optional local static asset serving so Photino can load the built UI. That host is for UI delivery and diagnostics; packaged business communication must go through the Photino bridge.

## Guardrails

- Keep `frontend/src/lib/wailsjs` intact until the frontend adapter slice replaces imports.
- Do not add Electron, Electron.NET, or Node runtime dependencies to the desktop host.
- Keep bridge contracts aligned with `docs/novelist-photino-bridge-contract.md`.
