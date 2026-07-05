# Reference Anchor Tasks: Phase 13

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to tasks index](tasks-and-verification.md).

## Phase 13: App-Wide Playwright Regression Coverage

**Description:** Expand the existing reference-anchor Playwright approach into an application-wide frontend regression suite. Phase 10 proves the reference-anchor workflow with a mocked Photino bridge; Phase 13 should cover the whole Novelist user surface so regressions in shell navigation, novel/chapter workflows, editing, search, chat/tool presentation, metadata panels, settings, and reference-anchor entry points are caught in a real browser.

This phase is intentionally broader than reference anchors. The reference-anchor suite remains as a deep feature workflow. The app-wide suite should exercise the complete desktop frontend at product boundaries, using deterministic fixtures and a mocked `window.external` bridge unless a test is explicitly marked as a real Photino smoke.

**Acceptance criteria:**

- [x] `frontend/package.json` exposes an app-wide Playwright command, for example `npm run test:app`, that is separate from `npm run test:reference-anchor`.
- [x] The app-wide suite starts the Vite app in a real browser and injects a deterministic mocked Novelist bridge before app code calls `window.external`.
- [ ] Workspace bootstrap is covered: empty state or init screen, project/novel load, bridge unavailable state, app error state, and stable recovery messaging.
- [ ] Shell navigation is covered across primary activities: bookshelf/workspace, editor, chat, search, reference, characters, locations, timeline/story arcs, preferences/readers, profile, and settings/help affordances where available.
- [ ] Novel and chapter workflows are covered with fixture data: create/edit/select novel, list chapters, open chapter tabs, switch tabs, preserve active selection, and show expected side-panel counts.
- [ ] Content editing is covered: editor render, text edits, save through the bridge, dirty-state behavior, save failure display, and no accidental saves when switching unrelated panels.
- [ ] Search/RAG UI is covered: global search query, result rendering, empty state, failure state, and result preview without leaking raw reference-source internals into the global search path unless a later design explicitly enables it.
- [ ] Chat and agent UI is covered at presentation level: send prompt, stream assistant text/tool-call states from mocked events, render tool cards/web-search cards, handle cancellation/failure, and keep generated content out of chapter files unless the user explicitly saves through an editor flow.
- [ ] Metadata surfaces are covered: character, location, timeline/story-arc, preference, reader, skill, and profile panels render fixture data, empty states, and representative create/edit/delete or inspect actions where those actions exist.
- [ ] Settings are covered: provider/model/embedding configuration panes render, validate required fields, persist safe settings through the bridge, and never require live API keys, local model files, or network access.
- [ ] Import/export and file-picker affordances are covered with mocked paths and temporary fixtures; tests must not read or write real user projects outside test temp directories.
- [x] Reference-anchor coverage is included as a smoke path in the app-wide suite, while deep anchor orchestration, blueprint, material binding, candidate, audit, and screenshot checks remain in `npm run test:reference-anchor`.
- [x] Visual checks capture stable screenshots for the shell, editor, search, chat, settings, metadata panels, and reference entry point at the default desktop viewport; any smaller responsive viewport is scoped to layout integrity, not full workflow duplication.
- [ ] Test selectors are stable and intentional: prefer accessible roles/names and add `data-testid` only where accessible selectors would be brittle.
- [x] The suite records bridge calls and asserts high-risk guardrails, especially no implicit `SaveContent`, no direct arbitrary file read, no live network/model dependency, and no automatic chapter mutation from agent or reference workflows.

**Verification:**

- [ ] `npm --prefix frontend run build`
- [ ] `npm --prefix frontend run lint`
- [ ] `npm --prefix frontend run test:reference-anchor`
- [x] `npm --prefix frontend run test:app`
- [ ] At least one CI-friendly command can run all frontend verification without relying on an installed Photino desktop shell.
- [ ] Real Photino coverage remains a minimal boundary smoke unless a later phase adds stable desktop automation: app loads built assets, representative bridge calls route through production composition, and no path auto-inserts chapter prose.

**Recommended Playwright suite slices:**

1. `app-shell.spec`: bootstrap, activity navigation, status/help/settings entry points, bridge unavailable/failure states.
2. `novel-editor.spec`: bookshelf, novel selection, chapter list, tabs, editor render/edit/save/failure behavior.
3. `search-chat.spec`: global search states, chat prompt/stream/tool-card/cancel/failure presentation, no implicit chapter save.
4. `metadata-panels.spec`: character, location, timeline/story-arc, preference, reader, skill, and profile panels with fixture data.
5. `settings.spec`: provider/model/embedding settings validation and persistence through the mocked bridge.
6. `reference-entry.spec`: reference activity smoke, anchor list/search entry point, and handoff to the existing deep reference-anchor workflow suite.

**Targeted Phase 13 thin-slice checks completed:**

- [x] `npm run test:app` now runs a standalone real-browser Vite workflow with a deterministic mocked `window.external` bridge and screenshots under `output/playwright/`.
- [x] The first app-wide suite covers workspace load, shell/book/chapter navigation, chapter open through `GetContent`, global search result navigation, chat prompt plus streamed assistant/tool/web-search presentation, settings panes, metadata panel fixture rendering, skill list rendering, and the reference-anchor entry point.
- [x] The suite records bridge calls and asserts no implicit `SaveContent`, no external URL open, and no reference file picker or other mutating bridge calls during the smoke path.
- [x] The app-wide console-error guard caught and fixed an invalid nested-button structure in the location side panel; the mocked chat usage payload now matches the `ContextRing` contract so the smoke does not mask NaN rendering warnings.

This first slice does not complete Phase 13. Empty/error bootstrap states, create/edit/delete workflows, Monaco content editing/save/failure behavior, chat cancellation/failure, import/export/file-picker paths, responsive viewport coverage, and CI aggregation remain pending.

**Files likely touched:**

- `frontend/package.json`
- `frontend/scripts/*playwright*.mjs`
- `frontend/tests/**`
- `frontend/src/components/**/*`
- `frontend/src/views/**/*`
- `frontend/src/lib/novelist/*`
- `docs/reference-anchor-implementation/*.md`
