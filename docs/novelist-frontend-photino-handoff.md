# Novelist Frontend to Photino Handoff

## Purpose

This document records how the React/Vite UI is handed to the Photino.NET app host. The migration is complete: the active desktop runtime is Photino + .NET, and the retired desktop runtime is no longer part of the mainline product path.

## Current Frontend Build

The frontend remains in `frontend/`.

```powershell
npm --prefix frontend ci
npm --prefix frontend run build
```

Vite writes production assets to `frontend/dist/`. That directory is build output and must not be committed.

## Photino Consumption Path

The desktop app uses an explicit launch flag:

```powershell
dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop
```

Photino loads the built Vite UI from the local app host in packaged/dev desktop mode. That host is for UI delivery and diagnostics; packaged business communication goes through the Photino WebMessage bridge.

## Guardrails

- Do not restore retired generated desktop bindings or direct retired-runtime imports.
- Do not add Electron, Electron.NET, or Node runtime dependencies to the desktop host.
- Keep bridge contracts aligned with `docs/novelist-photino-bridge-contract.md`.
- Add frontend-owned DTOs and bridge wrappers under `frontend/src/lib/novelist/`; backend contracts belong under `src/Novelist.Contracts/`.
