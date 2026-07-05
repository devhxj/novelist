# Reference Anchor Desktop and Agent Integration

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to schema and integration index](schema-and-integration.md).

## Desktop Composition

`src/Novelist.App/Desktop/DesktopBridgeComposition.cs` constructs the desktop bridge service graph used by `PhotinoWindowFactory` and by desktop bridge smoke tests. The reference services are created after embedding/RAG dependencies:

```csharp
var referenceAnchorService = new SqliteReferenceAnchorService(
    appOptions,
    novelService,
    embeddingService,
    embeddingClient,
    sqliteVecProvider);

var referenceAnchoredDraftService = new SqliteReferenceAnchoredDraftService(
    appOptions,
    novelService,
    planningService,
    referenceAnchorService);
```

It passes both services into `NovelistMafToolRegistry` so chat tools can access reference-anchor operations:

```csharp
var chatToolExecutor = new NovelistMafChatToolExecutor(new NovelistMafToolRegistry(
    storyMemoryService,
    chapterContentService,
    approvalCoordinator,
    eventSink,
    subagentRunner,
    preferenceService,
    worldService,
    planningService,
    webFetchService,
    webSearchService,
    referenceAnchorService,
    referenceAnchoredDraftService));
```

`PhotinoWindowFactory` remains responsible for window setup and routes `RegisterWebMessageReceivedHandler` into the bridge returned by `DesktopBridgeComposition.CreateBridge(...)`. The composition helper registers both Photino bridge handler groups on the shared dispatcher:

```csharp
.RegisterReferenceAnchorHandlers(referenceAnchorService)
.RegisterReferenceAnchoredDraftHandlers(referenceAnchoredDraftService)
```

The `NovelistMafToolRegistry` constructor keeps backward-compatible defaults:

- existing constructor signatures still work for tests;
- `IReferenceAnchorService? referenceAnchors = null` is optional;
- `IReferenceAnchoredDraftService? referenceDrafts = null` is optional after reference anchors.

## Agent Tool Plan

`NovelistMafToolRegistry` carries optional reference dependencies:

```csharp
private readonly IReferenceAnchorService? _referenceAnchors;
private readonly IReferenceAnchoredDraftService? _referenceDrafts;
```

Reference material tools live in `NovelistMafReferenceTools.cs`:

```text
get_reference_anchors
search_reference_materials
adapt_reference_material
audit_reference_reuse
```

Blueprint-gated drafting tools are also grouped in `NovelistMafReferenceTools.cs` under the draft tool adapter:

```text
generate_reference_chapter_blueprint
review_reference_chapter_blueprint
revise_reference_chapter_blueprint
approve_reference_chapter_blueprint
bind_reference_blueprint_materials
generate_reference_anchored_draft
audit_reference_anchored_draft
```

Default orchestration tools are exposed through the same draft adapter:

```text
start_reference_orchestration_run
get_reference_orchestration_runs
get_reference_orchestration_run
cancel_reference_orchestration_run
```

Current status:

- tool registration is optional: reference tools appear only when reference services are configured;
- `novel_id` is injected from `NovelistMafToolContext`, not exposed to the model schema;
- reference tools do not expose session/turn/tool internals;
- reference draft tools return blueprints, material links, candidates, and audits only;
- orchestration tools let the agent start, inspect, list, and cancel runs, but do not expose a generic resume/approval tool;
- `start_reference_orchestration_run` always starts with `source_confirmed=false`, uses include/exclude anchors only inside `corpus_search_policy`, and cannot pass `anchor_ids`, `decision_type`, or `decision_payload`;
- no reference tool is allowed to call `SaveContent` or mutate chapter prose.

Tool limits:

- `search_reference_materials`: max page size 20
- `search_reference_materials`: supports optional narrative-duty and emotion-transition filters
- `search_reference_materials`: returned materials include optional `score_components` when produced by ranked search
- `adapt_reference_material`: requires `material_id`, `slot_values`, `max_rewrite_level`
- `audit_reference_reuse`: pure check only
- `generate_reference_chapter_blueprint`: requires `chapter_number`, optional user chapter goal, known facts, forbidden facts, and active anchor ids; it must return logic, emotion, narration, character, reference, transition, and execution tracks
- `review_reference_chapter_blueprint`: pure check only; must not revise the blueprint silently
- `revise_reference_chapter_blueprint`: requires explicit field-level changes, records a revision, and invalidates approval/material links when reviewed fields change
- `approve_reference_chapter_blueprint`: allowed only after a passing review
- `bind_reference_blueprint_materials`: allowed only after explicit approval; returns ranked candidates by beat duty fit, not only semantic similarity; `select_top_candidate` defaults to `false` and must be `true` to mark each beat's top candidate selected for draft generation
- `generate_reference_anchored_draft`: requires an approved and material-bound `blueprint_id`; returns beat-scoped candidates only, not an assembled full chapter
- `audit_reference_anchored_draft`: pure check only
- `start_reference_orchestration_run`: requires `chapter_number`; accepts optional chapter goal, known facts, forbidden facts, corpus search mode, include/exclude anchor filters, license statuses, and max results per beat; it does not confirm source/license or fact-boundary changes
- `get_reference_orchestration_runs`: read-only run history, optional chapter filter
- `get_reference_orchestration_run`: read-only run detail including current required decision
- `cancel_reference_orchestration_run`: cancels a run only; it cannot approve source/fact, blueprint revision, blueprint approval, or final insertion
- no `SaveContent`
- no direct file path reads

Tool schemas must not expose `novel_id`, `session_id`, `turn_id`, or `tool_id`.
Orchestration tool schemas also must not expose `source_confirmed`, `anchor_ids`, `decision_type`, `decision_payload`, free-form `content`, or `text` fields that could let the model bypass author gates.

Agent workflow order is enforced in tool descriptions and service validation:

```text
search/reference context
  -> generate_reference_chapter_blueprint
  -> review_reference_chapter_blueprint
  -> revise_reference_chapter_blueprint when review fails
  -> review_reference_chapter_blueprint again after revision
  -> approve_reference_chapter_blueprint
  -> bind_reference_blueprint_materials (candidate preview, no auto-selection by default)
  -> bind_reference_blueprint_materials with select_top_candidate=true before drafting
  -> generate_reference_anchored_draft
  -> audit_reference_anchored_draft
```

Agent hardening currently covered:

- `ReferenceDraftToolDescriptionsEnforceBlueprintWorkflowOrder` proves models are told to generate/review/approve/bind before drafting and to avoid `SaveContent`;
- `ReferenceOrchestrationAgentToolStartsRunWithoutApprovingHumanDecisions` proves the default agent orchestration entry starts at the source/fact confirmation gate and cannot pre-confirm it;
- reference tool schema tests prove `novel_id`, `session_id`, `turn_id`, and `tool_id` remain hidden.
- orchestration tool schema tests prove resume/approval/revision/final-insertion tools are not registered for the agent surface and that start arguments cannot carry source confirmation, decision payloads, or prose text.
