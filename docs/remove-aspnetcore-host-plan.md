# Remove ASP.NET Core Host Plan

## Status

Draft for review.

This document adjusts the current Novelist desktop architecture after confirming that the application is a local desktop app whose business IPC is already the Photino WebMessage bridge. ASP.NET Core currently exists as a loopback wrapper around frontend assets and a few local HTTP routes. It should not remain in the default product startup path.

## Decision Summary

Novelist should move to a pure Photino desktop runtime:

```text
Photino window
  -> loads built frontend assets from local disk
  -> frontend calls window.external.sendMessage(...)
  -> PhotinoWebMessageBridge
  -> BridgeDispatcher
  -> Core services
  -> Infrastructure services
```

ASP.NET Core/Kestrel should be removed from packaged desktop startup. The frontend/backend business contract remains the bridge contract described in `docs/novelist-photino-bridge-contract.md`.

Development may still use `--start-url=http://localhost:5173/` to point Photino at Vite. That is a frontend dev convenience only. It must not reintroduce a local business HTTP API.

## Why This Is The Right Direction

The current app is not a web server product. It is a local desktop writing tool:

- `frontend/src/lib/novelist/bridge.ts` already defines a JSON request/response/event bridge over `window.external.sendMessage` and `external.receiveMessage`.
- `src/Novelist.App/Desktop/PhotinoWindowFactory.cs` creates the service graph and registers bridge handlers directly in-process.
- `src/Novelist.Core/Bridge/BridgeDispatcher.cs` owns request validation, method lookup, error normalization, deadlines, and cancellation semantics.
- No current business feature needs HTTP routing to cross the frontend/backend boundary.

Keeping Kestrel in the packaged startup path adds cost without matching the product shape:

- an extra local port is opened even though the app is single-user and in-process;
- static asset serving, `/covers/{novelId}`, `/health`, and `/hubs/events` become observable surfaces that tests and code start to depend on;
- publish output depends on the ASP.NET Core shared framework/runtime pack because `Novelist.App` uses `Microsoft.NET.Sdk.Web`;
- the architecture appears split between "web app" and "desktop app", even though the actual stable IPC is Photino bridge.

The target design is simpler at runtime but not weaker architecturally: the same contract discipline moves into explicit bridge payloads, local asset resolution, and desktop smoke tests.

## Current ASP.NET Core Responsibilities

These are the behaviors that must be replaced or explicitly retired.

| Responsibility | Current implementation | Target |
| --- | --- | --- |
| Desktop startup URL | `PhotinoDesktopApplication` starts `NovelistAppBuilder`, binds `http://127.0.0.1:0`, then passes the resolved URL to Photino | Resolve local `frontend/dist/index.html` and load it directly, unless `--start-url=` is supplied |
| Frontend static files | `NovelistAppBuilder` uses `UseDefaultFiles`, `UseStaticFiles`, and `MapFallback` with `FrontendAssets` | Local file/custom-scheme asset loading; no Kestrel |
| Cover image display | `GET /covers/{novelId}` reads `INovelService.GetCoverAsync` and returns a file | Bridge method returns cover metadata and data, or a desktop-local safe resource URL |
| Health endpoint | `GET /health` returns `HealthResponse("ok", "novelist")` | Remove from production; replace tests with bridge/desktop startup readiness checks |
| SignalR endpoint | Empty `EventsHub` mapped at `/hubs/events` | Remove; bridge events are the event transport |
| Server mode | `Program` runs `NovelistAppBuilder.Build(args).Run()` when `--server` is passed | Remove or quarantine behind a non-product diagnostic plan |
| Integration tests | `WebApplicationFactory<Program>` validates health, SignalR, static assets, SPA fallback, cover route | Replace with asset resolver tests, bridge contract tests, and desktop startup tests |
| Project SDK | `src/Novelist.App/Novelist.App.csproj` uses `Microsoft.NET.Sdk.Web` | Change to `Microsoft.NET.Sdk` after HTTP consumers are migrated |

## Non-Goals

- Do not redesign the bridge envelope. The bridge contract already exists and should remain stable.
- Do not introduce Electron, embedded Chromium, or a separate web backend.
- Do not move business services behind HTTP.
- Do not keep `/health`, `/hubs/events`, or `/covers/{novelId}` as compatibility endpoints in the packaged product.
- Do not perform a broad DI/container rewrite during this migration. Service composition can be improved later, but it is not required to remove Kestrel.

## Target Architecture

### Production Startup

`Program.Main` should launch desktop mode by default:

```text
Program.Main
  -> PhotinoDesktopApplication.Run(args)
  -> DesktopFrontendAssets.Resolve(...)
  -> PhotinoLaunchMode.CreateSettings(args, localIndexStartUrl)
  -> PhotinoDesktopHost.Run(settings)
  -> PhotinoWindowFactory.Create(settings)
  -> PhotinoWindow.Load(settings.StartUrl)
```

The desktop application must not create `WebApplication`, call `StartAsync`, inspect `IServerAddressesFeature`, or bind a loopback port.

### Development Startup

The existing `--start-url=` override stays:

```text
dotnet run --project src/Novelist.App -- --start-url=http://localhost:5173/
```

In this mode Vite serves frontend assets. All business calls still go through the Photino bridge. The app should fail clearly if the page does not expose the Photino bridge, rather than falling back to HTTP.

### Asset Loading

Use a small desktop asset resolver with no ASP.NET Core types:

```csharp
public sealed record DesktopFrontendAsset(
    string DistPath,
    string IndexPath,
    string StartUrl);

public interface IDesktopFrontendAssetResolver
{
    DesktopFrontendAsset? TryResolve(string? configuredDistPath, string contentRootPath);
}
```

Resolution order:

1. explicit configured dist path, if the app keeps an equivalent of `Novelist:FrontendDistPath`;
2. packaged `frontend/dist` beside the app;
3. repository `frontend/dist` discovered by walking parent directories during local development;
4. `about:blank` only for tests that intentionally use a fake window.

Vite currently has no `base` setting, so production files may contain absolute `/assets/...` paths. Before switching to direct file loading, set and verify:

```ts
// frontend/vite.config.ts
export default defineConfig({
  base: './',
  // existing config...
})
```

This is required because `file:///assets/app.js` points at the filesystem root, not `frontend/dist/assets/app.js`.

The frontend currently does not use React Router, so removing ASP.NET Core SPA fallback should not break route refresh behavior. Still, the migration should include a smoke check for the first screen and workspace initialization under direct file loading.

## Cover Image Migration

The cover route is the only active frontend consumer of a product HTTP route:

```tsx
// frontend/src/components/sidebar/BookCover.tsx
`/covers/${novelId}?v=${refreshKey ?? 0}`
```

Replace it with an explicit bridge contract. Prefer an additive method first:

```text
GetCover
```

Suggested payload:

```csharp
public sealed record NovelCoverPayload(
    long NovelId,
    string ContentType,
    string DataBase64,
    long Length,
    DateTimeOffset LastModified);
```

Rationale:

- JSON byte arrays are very inefficient for images.
- Existing bridge default max message size is 1 MiB, while `NovelCoverConstraints.MaxBytes` is 10 MiB.
- Base64 is simpler for the first implementation, but the bridge limit must be raised for this method, or the contract must reject covers over a smaller display-safe limit.

Recommended robust policy:

- keep `SaveCover` input validation at the existing 10 MiB storage boundary;
- add a display boundary for `GetCover`, for example `MaxBridgeCoverBytes`;
- if stored cover exceeds the bridge display boundary, return a structured `COVER_TOO_LARGE_FOR_BRIDGE` error and fall back to default cover;
- later, add a chunked binary bridge or safe local resource resolver if large original covers must display without base64 overhead.

Frontend behavior:

- `BookCover` calls `app.GetCover(novelId)` when `novelId` or `refreshKey` changes;
- creates a `Blob` URL or `data:` URL from `ContentType` and `DataBase64`;
- revokes old `Blob` URLs on effect cleanup;
- uses `defaultCover` when not found, too large, invalid, or bridge unavailable;
- never references `/covers/...`.

Backend behavior:

- `INovelService.GetCoverAsync` remains the storage primitive.
- `NovelBridgeHandlers` registers `GetCover`.
- The bridge method reads the local file returned by `NovelCoverFile.LocalPath` only after service validation.
- The method maps missing cover to `null` or a stable not-found bridge error; choose one and keep it consistent.

## Event Migration

`EventsHub` is currently empty. The product event path is already bridge events:

```text
PhotinoBridgeEventSink
  -> IPhotinoWindow.SendWebMessage(...)
  -> frontend bridge event subscribers
```

Remove SignalR from production instead of building a second event system.

Required checks:

- no frontend code imports SignalR client packages;
- no C# service depends on `IHubContext<EventsHub>`;
- chat/session/file events remain covered by bridge event tests.

## Server Mode Policy

`--server` currently means "do not launch desktop; run ASP.NET Core app". For a pure desktop application, that flag is misleading.

Recommended policy:

1. Remove `--server` from product startup once WebApplication tests are migrated.
2. Keep `--start-url=` because it is useful for Vite dev mode.
3. If diagnostics need an HTTP surface later, create a separate plan with explicit constraints:
   - disabled by default;
   - loopback only;
   - random port;
   - per-run token;
   - no business write API unless separately approved;
   - separate tests and documentation.

Do not keep the current `--server` path as a hidden compatibility mode. Hidden local servers are hard to reason about and easy for future code to depend on accidentally.

## Implementation Plan

### Phase 0: Freeze Existing HTTP Surface

Description: Make current ASP.NET Core responsibilities explicit before removal.

Acceptance criteria:

- [ ] `rg` inventory shows all references to `WebApplication`, `AspNetCore`, `/covers`, `/health`, `/hubs/events`, `SignalR`, `Microsoft.AspNetCore.Mvc.Testing`, and `ServerFlag`.
- [ ] Each reference is categorized as remove, replace, or keep-for-dev.
- [ ] No runtime code is changed in this phase.

Likely files:

- `docs/remove-aspnetcore-host-plan.md`

Verification:

```powershell
rg -n 'AspNetCore|WebApplication|/covers|/health|hubs/events|SignalR|Mvc\.Testing|ServerFlag' src tests frontend build scripts docs --glob '!**/bin/**' --glob '!**/obj/**' --glob '!build/bin/**'
```

### Phase 1: Make Frontend Assets File-Loadable

Description: Ensure built Vite assets work when loaded from local disk.

Acceptance criteria:

- [ ] `frontend/vite.config.ts` sets `base: './'`.
- [ ] `npm run build` emits relative asset references in `frontend/dist/index.html`.
- [ ] The packaged `frontend/dist` layout remains compatible with `scripts/novelist-publish.sh`.
- [ ] A local file URL for `index.html` renders the app shell without missing CSS/JS.

Likely files:

- `frontend/vite.config.ts`
- `scripts/novelist-publish.sh` only if staging paths need adjustment

Verification:

```powershell
cd frontend
npm run build
npm run lint
```

Manual smoke:

- open `frontend/dist/index.html` through Photino or a browser and confirm CSS/JS load from `./assets/...`.

### Phase 2: Extract Desktop Asset Resolver

Description: Replace `FrontendAssets` with a desktop-only resolver that does not depend on ASP.NET Core abstractions.

Acceptance criteria:

- [ ] New resolver uses `System.IO` only; no `IConfiguration`, `IHostEnvironment`, `IFileProvider`, or `PhysicalFileProvider`.
- [ ] Tests cover configured path, packaged path, repo discovery path, and missing index behavior.
- [ ] Resolver returns a `StartUrl` acceptable to `PhotinoWindow.Load`.

Likely files:

- `src/Novelist.App/Desktop/DesktopFrontendAssets.cs`
- `tests/Novelist.IntegrationTests/DesktopFrontendAssetTests.cs`
- `src/Novelist.App/Hosting/FrontendAssets.cs` removed later, not necessarily in this phase

Verification:

```powershell
dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --no-restore -v minimal
```

### Phase 3: Remove Loopback Startup From Desktop Mode

Description: Make `PhotinoDesktopApplication` load local assets directly.

Acceptance criteria:

- [ ] `PhotinoDesktopApplication` no longer references `WebApplication`, `IServer`, `IServerAddressesFeature`, or `NovelistAppBuilder`.
- [ ] The default `StartUrl` is a local index URL when `frontend/dist/index.html` exists.
- [ ] `--start-url=` still overrides the local index URL.
- [ ] Tests no longer assert `http://127.0.0.1:` for desktop startup.
- [ ] Startup log no longer says "Starting loopback host" in desktop mode.

Likely files:

- `src/Novelist.App/Desktop/PhotinoDesktopApplication.cs`
- `src/Novelist.App/Desktop/PhotinoLaunchMode.cs`
- `tests/Novelist.IntegrationTests/PhotinoDesktopHostTests.cs`

Verification:

```powershell
dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --no-restore -v minimal
```

### Phase 4: Move Cover Display Onto The Bridge

Description: Replace `/covers/{novelId}` with a bridge method and frontend Blob/data URL rendering.

Acceptance criteria:

- [ ] `GetCover` is added to `Novelist.Contracts` and `NovelBridgeHandlers`.
- [ ] `INovelService.GetCoverAsync` remains the only storage source.
- [ ] Bridge tests cover success, missing cover, invalid novel id, uninitialized app, and over-limit display payload.
- [ ] `BookCover.tsx` no longer builds `/covers/...` URLs.
- [ ] Old Blob URLs are revoked to avoid memory leaks.
- [ ] Default cover fallback works for null/error states.

Likely files:

- `src/Novelist.Contracts/App/NovelPayloads.cs` or a new cover payload file
- `src/Novelist.Core/Bridge/NovelBridgeHandlers.cs`
- `frontend/src/lib/novelist/api.ts`
- `frontend/src/lib/novelist/types.ts`
- `frontend/src/components/sidebar/BookCover.tsx`
- `tests/Novelist.Tests/Bridge/BridgeDispatcherTests.cs` or package-level bridge tests
- `tests/Novelist.IntegrationTests/NovelServiceTests.cs` if storage coverage needs expansion

Verification:

```powershell
dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --no-restore -v minimal
dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --no-restore -v minimal
cd frontend
npm run build
npm run lint
```

### Phase 5: Remove ASP.NET Core Host Code

Description: Delete the now-unused HTTP host.

Acceptance criteria:

- [ ] `NovelistAppBuilder` is removed.
- [ ] `EventsHub` is removed.
- [ ] `FrontendAssets` ASP.NET implementation is removed.
- [ ] `HealthResponse` is removed.
- [ ] `Program.Main` no longer has a server branch.
- [ ] `PhotinoLaunchMode.ServerFlag` is removed or made obsolete only during a short transitional commit, then removed.
- [ ] No source file imports `Microsoft.AspNetCore.*`.

Likely files:

- `src/Novelist.App/Program.cs`
- `src/Novelist.App/Hosting/NovelistAppBuilder.cs`
- `src/Novelist.App/Hosting/FrontendAssets.cs`
- `src/Novelist.App/Realtime/EventsHub.cs`
- `src/Novelist.App/Desktop/PhotinoLaunchMode.cs`
- `tests/Novelist.IntegrationTests/AppContractTests.cs`
- `tests/Novelist.IntegrationTests/FrontendAssetTests.cs`
- `tests/Novelist.IntegrationTests/WebApplicationFactoryCollection.cs`

Verification:

```powershell
rg -n 'Microsoft\.AspNetCore|WebApplication|MapGet|MapHub|SignalR|HealthResponse|WebApplicationFactory|Mvc\.Testing' src tests --glob '!**/bin/**' --glob '!**/obj/**'
```

Expected result: no matches, except archived docs if intentionally retained.

### Phase 6: Change Project SDK And Test Dependencies

Description: Remove ASP.NET Core runtime framework dependency from the app and tests.

Acceptance criteria:

- [ ] `src/Novelist.App/Novelist.App.csproj` changes from `Microsoft.NET.Sdk.Web` to `Microsoft.NET.Sdk`.
- [ ] `tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj` removes `Microsoft.AspNetCore.Mvc.Testing`.
- [ ] `appsettings.json` and `appsettings.Development.json` are removed if they only configure ASP.NET Core logging and no longer have a runtime owner.
- [ ] RID publish no longer requires `Microsoft.AspNetCore.App.Runtime.*`.
- [ ] Packaging scripts still stage `frontend/dist`.

Likely files:

- `src/Novelist.App/Novelist.App.csproj`
- `tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj`
- `src/Novelist.App/appsettings.json`
- `src/Novelist.App/appsettings.Development.json`
- `scripts/novelist-publish.sh`
- `docs/build-setup.md`
- `docs/build/cross-platform-build.md`

Verification:

```powershell
dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --no-restore -v minimal
dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --no-restore -v minimal
dotnet publish src/Novelist.App/Novelist.App.csproj -c Release --no-restore
```

Also verify release docs no longer mention ASP.NET Core runtime packs as required publish artifacts.

### Phase 7: Packaging And Desktop Smoke Tests

Description: Prove the packaged desktop app starts without Kestrel.

Acceptance criteria:

- [ ] Windows installer shortcut still passes `--desktop` or the flag becomes harmless because desktop is default.
- [ ] macOS/Linux launch scripts still start the desktop app.
- [ ] Packaged app contains `frontend/dist/index.html` and `frontend/dist/assets`.
- [ ] Desktop launch logs show direct asset loading and no loopback host.
- [ ] Basic workflow smoke passes: start app, initialize/open data dir, list novels, open workspace, display default cover, save/read custom cover, send a bridge request.

Likely files:

- `build/package/windows/setup.iss`
- `build/package/macos/build-dmg.sh`
- `build/package/linux/build-appimage.sh`
- `scripts/novelist-publish.sh`
- packaging docs

Verification:

```powershell
make build
```

If `make build` is not the active .NET path, use the current publish script and package command documented in `docs/build-setup.md`.

## Test Migration Matrix

| Old test | Replace with |
| --- | --- |
| `AppContractTests.HealthEndpointReturnsStablePayload` | Bridge/desktop readiness test: dispatcher can answer a stable lightweight method such as `GetPlatform` or app initialization status |
| `AppContractTests.EventsHubNegotiateEndpointIsMapped` | Bridge event sink test: event envelope reaches fake Photino window |
| `FrontendAssetTests.RootServesFrontendIndexWhenDistExists` | Desktop asset resolver returns `index.html` path/start URL |
| `FrontendAssetTests.StaticAssetsAreServedFromFrontendDist` | Vite build check verifies relative `./assets/...` references |
| `FrontendAssetTests.SpaFallbackServesFrontendIndex` | Remove unless frontend introduces a router; no HTTP fallback remains |
| `FrontendAssetTests.CoverRouteServesValidatedNovelCover` | `GetCover` bridge test plus `BookCover` frontend build/lint |
| `PhotinoDesktopHostTests.DesktopApplicationStartsLoopbackHostAndPassesUrlToWindow` | Desktop application passes local file/custom-scheme start URL and never starts a host |

## Risk Register

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Vite emits absolute asset URLs | App opens but CSS/JS fail under local file loading | Set `base: './'`; verify built `index.html` and real Photino startup |
| Photino/WebView blocks some `file://` subresources | Blank or partially loaded UI | Test on Windows first; if needed, introduce a local custom scheme/resource handler instead of Kestrel |
| Cover images exceed bridge size limits | Sidebar cover fails or bridge rejects message | Use base64 only with explicit display limit; fallback to default cover; plan chunked/resource URL for large covers |
| Removing `/health` breaks release smoke checks | CI or packaging scripts fail | Replace health checks with desktop bridge smoke checks before deleting endpoint |
| Hidden docs/tests keep depending on ASP.NET Core | Dependency returns later | Add grep gate for `Microsoft.AspNetCore`, `WebApplicationFactory`, `/covers`, `/hubs/events` |
| `--server` is used by an external script | Startup behavior changes unexpectedly | Search build/package/scripts first; document removal; keep `--desktop` harmless during transition |
| Changing SDK alters publish output | Installer misses files or runtime packs differ | Validate `dotnet publish`, publish script staging, and platform package smoke tests |
| File URL start path contains spaces or non-ASCII | Startup URL fails on some systems | Create start URL via `new Uri(indexPath).AbsoluteUri`, not manual string concatenation |
| Direct file loading loses SPA fallback | Future routes may fail | Current frontend has no router; if router is added, prefer hash routing or a custom scheme fallback |

## Quality Gates

Do not remove ASP.NET Core host code until all of these are true:

- [ ] The frontend loads from built local assets without Kestrel.
- [ ] The bridge handles all existing business calls used by the app.
- [ ] Cover display no longer uses `/covers/...`.
- [ ] Bridge event tests cover the event path that SignalR was supposed to represent.
- [ ] Integration tests no longer require `WebApplicationFactory<Program>`.
- [ ] `Novelist.App` builds with `Microsoft.NET.Sdk`.
- [ ] Packaging scripts stage frontend assets and app runtime correctly.
- [ ] A grep gate shows no production source dependency on `Microsoft.AspNetCore.*`.

## Documentation Updates

After implementation, update these documents so future agents do not reintroduce Kestrel as the default desktop host:

- `README.md`
- `README_EN.md`
- `AGENTS.md`
- `CLAUDE.md`
- `docs/novelist-migration-progress.md`
- `docs/novelist-photino-bridge-contract.md`
- `docs/build-setup.md`
- `docs/build/cross-platform-build.md`
- `docs/novelist-release-candidate-gap-audit.md`

Key wording to preserve:

```text
Packaged Novelist is a local Photino desktop app. Business IPC uses Photino WebMessage. ASP.NET Core/Kestrel is not part of the default runtime.
```

## Rollback Strategy

Use a phased rollback, not a mixed architecture:

1. If file asset loading fails, revert only Phases 1-3 and keep bridge/cover work intact.
2. If cover bridge payloads are too large, keep pure desktop startup and add a chunked/resource cover transport.
3. If publish output breaks after changing SDK, temporarily revert Phase 6 only while preserving removal of HTTP routes in source.
4. Do not reintroduce `/health`, `/hubs/events`, or `/covers/{novelId}` as permanent compatibility endpoints unless a new decision document accepts the maintenance/security cost.

## Final Acceptance

The migration is complete when:

- running the packaged app opens a Photino window without binding a loopback HTTP port;
- frontend business operations use only the Photino bridge;
- custom covers display without `/covers/{novelId}`;
- source and tests contain no ASP.NET Core host dependency;
- release/package documentation describes Novelist as a pure local desktop app.
