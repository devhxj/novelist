# Novelist Release-Candidate Gap Audit

Date: 2026-07-03

Status: closed. The release-candidate blockers found in this audit have been implemented and verified on the .NET 10 + Photino product path. The old Go/Wails runtime path has been removed from the mainline source tree; rollback/reference now lives in Git history and historical docs only.

## Closed RC Blockers

| ID | Area | Resolution | Verification |
| --- | --- | --- | --- |
| RC-01 | Release pipeline still builds Go/Wails and downloads ONNX | CI, Makefile, packaging scripts, and build docs now publish `src/Novelist.App/Novelist.App.csproj`, copy `frontend/dist`, package Photino/.NET output, include Git/sqlite-vec runtime assets, and no longer require Wails, Go, ONNX runtime downloads, or generated Wails bindings. `scripts/novelist-publish.sh` publishes through a staging directory before replacing final output so failed publishes do not destroy the previous artifact. | `rg 'Wails|ONNX|onnx|Go/Wails|download-onnx|frontend/src/lib/wailsjs|go test|go build|go mod|wails build|wails dev' README.md README_EN.md AGENTS.md docs\build-setup.md docs\build\cross-platform-build.md .github Makefile build\package scripts src tests frontend\src --glob '!frontend/dist/**'` returned no matches; `NO_RESTORE=1 bash scripts/novelist-publish.sh` passed. |
| RC-02 | Cover API is not implemented on the .NET product path | `INovelService` / `FileSystemNovelService` now save, read, and delete validated cover images. `NovelistAppBuilder` serves `/covers/{novelId}` with safe local file lookup, content type handling, and 404 behavior for missing covers. | `NovelServiceTests` cover save/read/delete, invalid payload rejection, bridge calls, and Git commits; `FrontendAssetTests` cover `/covers/{novelId}` success and missing 404. |
| RC-03 | Per-novel Git history semantics are not migrated | `IVersionControlService` / `GitVersionControlService` initializes novel repositories, creates initial commits, and commits content, cover, and chat workspace changes with stable no-op behavior. | Integration tests verify `.git`, initial novel commit, content commits, cover commits, and chat pending workspace commits using real local Git behavior. |
| RC-04 | Old Goink user data migration/copy is missing | `LegacyGoinkDataMigrationService` performs copy-first migration from old config/data locations, imports old SQLite metadata into .NET stores, converts supported LLM config, copies skills and novel workspaces, creates per-novel Git commits, and writes a resumable manifest without mutating the source. | `LegacyGoinkDataMigrationTests.InitializeCopiesLegacyGoinkDataImportsSqliteStoresAndWritesManifest` builds a real old-data fixture and verifies copied files, imported stores, manifest entries, and generated repository history. |
| RC-05 | Web tool result cards bypass the Photino external URL bridge | `WebSearchCard.tsx` and `WebFetchCard.tsx` now open URLs through the Novelist runtime bridge, preserving backend HTTPS validation and native shell handling as the single policy point. | `rg '@/lib/wailsjs|lib/wailsjs|from .*wailsjs|window\.open' frontend\src --glob '!frontend/dist/**'` returned no matches; frontend production build passed. |

## Retirement Cleanup

- Removed old Go/Wails entrypoints and packages: `main.go`, `go.mod`, `go.sum`, `wails.json`, `app/**`, and `internal/**`.
- Removed generated Wails frontend bindings: `frontend/src/lib/wailsjs/**`.
- Moved builtin skills into `src/Novelist.Infrastructure/BuiltinSkills/` and embedded them from `Novelist.Infrastructure.csproj`.
- Replaced frontend generated Wails DTO dependencies with owned Novelist TypeScript types in `frontend/src/lib/novelist/types.ts`.
- Updated visible product branding in active UI text from Goink to Novelist while preserving compatibility paths such as `goink.md` and `~/.goink/skills/<name>.md`.

## Verification Snapshot

- `npm --prefix frontend run build` passed with the existing Vite large-chunk warning.
- `$env:NUGET_PACKAGES=(Resolve-Path .\.dotnet\.nuget\packages).Path; dotnet restore Novelist.slnx --ignore-failed-sources` passed using the repository NuGet cache; only vulnerability-index network warnings were emitted.
- `$env:NUGET_PACKAGES=(Resolve-Path .\.dotnet\.nuget\packages).Path; dotnet build Novelist.slnx --no-restore -v minimal /m:1 /p:UseSharedCompilation=false` passed.
- `$env:NUGET_PACKAGES=(Resolve-Path .\.dotnet\.nuget\packages).Path; dotnet test Novelist.slnx --no-restore --no-build -v minimal /m:1 /p:UseSharedCompilation=false` passed: 37 unit tests and 119 integration tests.
- `NO_RESTORE=1 bash scripts/novelist-publish.sh` passed through Git Bash and produced `build/bin/novelist`.

## Known Environment Limitation

Local self-contained RID publish is not fully verifiable in this sandbox because network access is restricted and the local cache does not contain .NET runtime packs for `win-x64` (`Microsoft.NETCore.App.Runtime.win-x64`, `Microsoft.AspNetCore.App.Runtime.win-x64`, `Microsoft.WindowsDesktop.App.Runtime.win-x64`). `NO_RESTORE=1 bash scripts/novelist-publish.sh win-x64` therefore fails with `NETSDK1047` unless a RID restore has already populated assets. GitHub release jobs run restore with network access before RID publish, and the publish script now preserves the previous output when this local offline failure occurs.

## Follow-Ups

These are not migration blockers:

| ID | Area | Reason |
| --- | --- | --- |
| FU-01 | Historical design docs still mention Go/Wails/ONNX | They are retained as historical migration/design references and are excluded from the current release path grep. |
| FU-02 | Compatibility file/path names still contain `goink` | `goink.md`, `~/.goink/skills/<name>.md`, and legacy migration names are compatibility contracts for old user data and tool paths. |
| FU-03 | Release signing/notarization | Packaging scripts produce unsigned artifacts. Signing and notarization are launch/distribution work, not runtime migration parity. |
