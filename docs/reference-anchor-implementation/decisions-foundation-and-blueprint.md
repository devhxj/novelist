# Foundation and Blueprint Decisions

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to decisions index](decisions.md).

## Storage Location

Use a dedicated database:

```text
{appData}/reference-anchor/index.sqlite
```

Rationale:

- keeps reference materials separate from story memory RAG
- allows different schema migrations and build state
- avoids mixing rowids with `rag_chunks`
- keeps sqlite-vec table names scoped to reference anchors

## Chapter Narrative Blueprint Layer

Add a mandatory chapter narrative blueprint layer between chapter planning and prose generation.

The user-facing writing workflow can be understood as "first produce a detailed chapter script and analysis, review it, then write prose". In code and UI, however, do not name this artifact "script". A script-like artifact tends to bias generation toward action/dialogue beats and away from novelistic narration. The intended artifact is a structured prose-generation contract:

```text
chapter plan / timeline / world state / previous chapter state
  -> Chapter Narrative Blueprint
  -> Blueprint Analysis Contract
  -> Blueprint Review Gate
  -> Reference Material Binding
  -> Paragraph Candidate Generation
  -> Draft Audit
  -> user copy/manual insertion
```

The blueprint is not a detailed outline. It must contain all important features needed to write the chapter without relying on model intuition:

- chapter function in the whole story;
- explicit chapter logic analysis: premise, conflict, turn, consequence, and hook;
- causality chain from previous chapter state to final hook;
- transition plan between scenes and beats;
- per-character emotional trajectory;
- emotion analysis: trigger, suppressed reaction, visible evidence, delayed release, and after-state;
- role-state changes and relationship pressure;
- character analysis: goal, knowledge, misbelief, leverage, restraint, and state delta;
- POV character and narrative distance;
- narration analysis: narrative distance, interiority ratio, summary/scene balance, rhythm, and texture target;
- scene facts that may be used;
- forbidden facts that must not be introduced;
- paragraph/beat-level narrative duty;
- reference material query plan for each beat;
- max allowed rewrite level for each material use;
- adaptation target for each beat: copy, slot substitution, light connector edit, or no reuse;
- risk flags for AI-like prose, fake emotion, screenplay drift, and hard transitions.

The blueprint should be more detailed than a traditional outline, but less free than a prose draft. It is a "chapter execution contract": every beat explains why it exists, what emotional and narrative job it performs, what facts it may use, what it must not reveal, which reference materials it may borrow from, and how much rewriting is permitted.

Required non-goals:

- it is not screenplay dialogue/action blocking;
- it is not a model prompt hidden in a markdown field;
- it is not a free-form critique or editor note;
- it is not a replacement for the material bank;
- it must not contain final prose paragraphs that bypass reuse audit.

Rationale:

- **Decomposition:** current LLMs perform poorly when asked to solve plot logic, emotional continuity, viewpoint control, style matching, and prose execution in one pass. A blueprint separates reasoning from prose.
- **Externalized cognition:** the model must write down the assumptions it would otherwise keep implicit. That makes false emotion, missing transitions, and invented facts inspectable before prose exists.
- **Checkability:** causality, emotion triggers, POV knowledge, and scene facts can be reviewed before any prose is written.
- **Hallucination containment:** the blueprint establishes a bounded fact set and explicit forbidden facts before paragraph generation.
- **Emotion realism:** the blueprint forces every emotional change to have a trigger, internal state, external evidence, and narrative expression plan.
- **Novelistic narration:** every beat must declare whether it is action, reaction, interiority, environment, transition, information reveal, or hook, preventing the output from collapsing into dialogue/action-only screenplay form.
- **Reference anchoring:** retrieved sentences and passages are bound to a beat's narrative function, not pasted by superficial semantic similarity.
- **Robustness:** even if LLM prose remains unstable, the deterministic review gate can reject broken intermediate artifacts and prevent bad assumptions from propagating into final draft candidates.

Scientific/engineering validity argument:

- **Cognitive load reduction:** direct chapter generation asks one model call to solve plot causality, emotional realism, viewpoint control, prose rhythm, reference reuse, and anti-hallucination simultaneously. Splitting blueprint and prose reduces task coupling and makes each failure mode observable.
- **Intermediate supervision:** a blueprint is an inspectable intermediate representation. The system can reject unsupported emotional shifts, missing transitions, and POV leaks before they become fluent but wrong prose.
- **Retrieval grounding:** reference material is not retrieved by theme alone. It is matched against declared beat duties, including function, emotion transition, POV, narrative distance, technique, and rewrite budget.
- **Deterministic safety checks:** many core failures are structural, not aesthetic: missing cause, missing trigger, forbidden fact, unapproved rewrite level, material without provenance. These can be checked without trusting an LLM reviewer.
- **Human-like drafting workflow:** human writers often separate structure, excerpt reuse, scene intention, and final prose. The blueprint formalizes this workflow in storage and UI so the agent cannot skip directly to generic prose.
- **Regression friendliness:** bad blueprints and bad draft candidates can become fixtures. This makes style and narration quality partly testable instead of depending only on subjective prompt tuning.

Decision:

- Generate a detailed chapter scenario blueprint before any prose candidate.
- Review the blueprint as an artifact of its own, not as an editor note attached to a draft.
- Treat review failure as a hard workflow stop.
- Bind reference materials only after the blueprint has passed review.
- Generate prose only from approved blueprint beats and active material links.

This is expected to be better than direct chapter drafting because it moves the model's weakest work - implicit emotional reasoning, scene causality, viewpoint control, and style transfer - into explicit fields that can be inspected before prose fluency hides the failure. It is also safer than unrestricted imitation because the blueprint does not authorize copying by theme alone; every future paragraph must trace back to a beat duty, a source material, an allowed rewrite level, and an audit result.

The first implementation may use an LLM to generate the blueprint, but the review gate must be a separate operation with deterministic checks. A failed review must block prose generation until the blueprint is revised and reviewed again. LLM critique may explain or supplement deterministic findings, but it cannot override the hard gates.

## Chapter Blueprint as an Intermediate Representation

The chapter blueprint is an intermediate representation between planning data and final prose. This is the key expansion beyond a simple reference corpus.

The workflow should be:

```text
chapter plan / user goal / world state / previous state
  -> detailed chapter blueprint IR
  -> deterministic blueprint review
  -> explicit user approval
  -> reference material binding per beat
  -> beat-level paragraph candidate generation
  -> draft audit
  -> user copy/manual insertion
```

This IR should be closer to a human writer's private chapter scenario sheet than to a public outline. It should be detailed enough that a later drafting step does not need to invent emotional logic, transitions, point of view, or scene facts. It should not be so prose-like that it bypasses material reuse and audit.

Required IR layers:

```text
logic_ir
  - inherited premise
  - conflict source
  - beat dependency graph
  - setup/payoff pairs
  - transition reason between beats
  - final hook dependency

emotion_ir
  - character emotion before/after
  - emotional trigger
  - suppressed internal response
  - outward evidence available to prose
  - delayed release, misdirection, or restraint
  - relationship pressure delta

narration_ir
  - POV owner
  - allowed/forbidden knowledge
  - narrative distance
  - scene/summary balance
  - interiority, sensory, subtext, environment, or rhythm duty
  - anti-screenplay requirement

character_ir
  - goal
  - pressure
  - leverage
  - knowledge boundary
  - misbelief or blind spot
  - restraint
  - state delta

reference_ir
  - source material query intent
  - expected material type
  - function/emotion/POV/narrative-duty fit target
  - slot substitution plan
  - locked phrase policy
  - max rewrite level
  - no-reuse reason if applicable

execution_ir
  - paragraph intention
  - execution mode: dwell, compress, withhold, reveal, contrast, linger, turn
  - source-backed detail target
  - candidate rejection rule
  - minimum non-action narrative work
```

The drafting layer must consume this IR as a contract, not as inspiration. It may generate text only for selected beats, only from approved fields, and only through selected reference material links or explicit approved no-reuse reasons.

Architectural decision: treat the blueprint workflow like a compiler pipeline rather than a chain of prompts. Each stage receives a typed artifact, validates it, writes an auditable result, and never silently repairs an earlier stage:

```text
context pack builder
  -> blueprint generator
  -> blueprint normalizer
  -> deterministic blueprint reviewer
  -> explicit approval gate
  -> material binder
  -> beat-scoped draft candidate generator
  -> draft auditor
```

The generator may be AI-assisted, but its output is only an intermediate artifact. The reviewer is a separate component with deterministic hard gates. The material binder is a separate component that matches source material to declared beat duties. The draft generator is a separate component that consumes one approved beat contract plus selected material links. The auditor is a separate component that checks the resulting candidate against provenance, rewrite level, facts, POV, prose duties, emotion evidence, and screenplay drift.

No stage should accept a generic "make it better" instruction as enough. If a stage finds a problem, it must return field-level defects or candidate audit errors that can become regression fixtures.

## Review-First Chapter Scenario Workflow

The additional layer requested by the product design is not "make the outline more detailed". It is a separate reviewable artifact: a detailed chapter scenario blueprint that carries the reasoning a human writer would normally keep in notes before writing prose.

This workflow is expected to be more robust than direct drafting or a plain fine outline because it places the highest-risk failures at an inspectable level:

```text
chapter goal / chapter plan / known facts
  -> chapter scenario blueprint with analysis tracks
  -> blueprint review with field-level defects
  -> blueprint revision until pass
  -> explicit approval
  -> reference material binding by beat duty
  -> beat-scoped paragraph candidates
  -> candidate audit
```

The blueprint generator may still be AI-assisted, but the system must not trust it as prose. Its output is treated as data that can be rejected, revised, hashed, compared, and used as a contract. This avoids the "left foot stepping on right foot" problem as much as possible: the second model pass is not asked to magically make the first pass emotional or human-like; it is asked to check explicit fields against deterministic rules and source-backed evidence.

Required chapter-scenario dimensions:

- **Logic analysis:** inherited premise, conflict source, beat dependency, setup/payoff, consequence, and final hook dependency.
- **Emotion analysis:** before/after state, trigger, suppressed reaction, visible evidence, delayed release, relationship pressure, and after-state.
- **Narration analysis:** POV owner, knowledge boundary, narrative distance, scene/summary balance, rhythm, interiority, sensory pressure, subtext, and anti-screenplay duty.
- **Character analysis:** goal, leverage, restraint, misbelief, knowledge delta, role-state delta, and relationship pressure delta.
- **Reference-use analysis:** expected material type, source function, emotion/POV/prose-duty fit, slot substitution plan, locked phrase policy, and max rewrite level.
- **Execution analysis:** paragraph intention, execution mode, source-backed detail target, minimum non-action narrative work, and candidate rejection rule.

The review layer must produce actionable defects, not vague editorial advice. A defect should identify the failing dimension, target field path or beat id, severity, reason, and required fix. For example: `beats[3].emotion_trigger` missing, `beats[2].transition_out` is only a time/location jump, or `beats[5].prose_duties` has no non-action duty. Generic advice such as "make it more emotional" is not enough to unlock prose generation.

The blueprint can pass only when it is detailed enough for a later beat-level drafting tool to operate without inventing plot logic, character motivation, emotional evidence, scene facts, or narration duties. If prose generation still needs to infer those fields, the blueprint is incomplete and must be revised first.

Scientific and engineering validity:

- **Separating reasoning from prose reduces hidden failure.** Direct chapter generation lets the model hide false causality and fake emotion behind fluent sentences. The IR forces those assumptions into fields that can be checked before prose exists.
- **Intermediate supervision makes subjective quality partly testable.** "Does this feel human?" is hard to unit test. "Does this emotional shift have a trigger, suppressed reaction, visible evidence, and after-state?" can be tested deterministically.
- **Hallucination surface is smaller.** The blueprint creates a known fact set and forbidden fact set before drafting. Paragraph generation can then reject unsupported proper nouns, numbers, objects, and revelations instead of discovering hallucination after a full chapter exists.
- **Reference reuse becomes functional, not decorative.** A retrieved sentence is not accepted because it is thematically similar. It must fit the beat's function, emotion, POV, narrative distance, prose duty, and rewrite budget.
- **Screenplay drift becomes rejectable.** If a beat only has action/dialogue blocking, review fails before prose generation. The system does not ask the prose generator to "make it novelistic" after the structure has already collapsed into a script.
- **Human review is placed at the highest-leverage layer.** Users can approve or revise logic, emotion, narration, and reference-use choices before spending time on prose candidates.
- **Regression fixtures become meaningful.** Bad blueprints can be stored as fixtures for fake emotion, hard transitions, POV leaks, unsupported facts, and dialogue/action-only beats.

This is not a claim that AI understands emotion. The design assumes the opposite: the model is unreliable at implicit emotional reasoning, so the system requires emotion to appear as auditable structure. A chapter beat is not allowed to say only "she feels sad"; it must declare the trigger, the suppressed/internal reaction, the visible evidence, the narrative expression duty, and the after-state. A later prose candidate can then be checked against those fields.

This also avoids pure self-critique. The reviewer should not be another free-form "please judge quality" prompt. The hard gate is deterministic and fixture-driven. Optional LLM critique may help explain defects, but it cannot authorize drafting when required fields, facts, POV boundaries, material links, or rewrite budgets fail.

Robustness requirements:

- The IR must be normalized and hashed. Approval applies only to the exact normalized analysis contract.
- Review must be idempotent for the same blueprint, context hash, source plan hash, and review version.
- Any edit to a reviewed field must invalidate approval and material bindings.
- Draft generation must not accept a blueprint whose latest passing review belongs to an older `analysis_contract_hash`.
- LLM-generated fields must pass schema validation before persistence.
- Optional LLM critique can add warnings, but deterministic hard gates decide whether drafting is allowed.
- The UI must show IR layers separately, so users can fix the cause of a defect rather than editing a vague markdown critique.
- Generator, reviewer, binder, draft generator, and auditor must be independently testable. A single model call or service method must not generate a blueprint, approve it, bind material, and draft prose in one pass.
- The reviewer must be allowed to fail useful-looking blueprints when they lack explicit cause, emotion evidence, POV boundary, transition pressure, prose duty, or reference-use rationale. Fluency is not a substitute for completeness.

## Anti-Screenplay Blueprint Constraints

The blueprint layer must not recreate the current failure mode where generated chapters read like scripts. A "detailed chapter script" is useful only if it is constrained as a novelistic execution contract.

Allowed blueprint content:

- scene purpose and narrative pressure;
- causality edges and transition reasons;
- character internal state, restraint, and external evidence;
- viewpoint boundary and narrative distance;
- paragraph intention and prose duties;
- source material query, adaptation policy, and rewrite budget;
- risk flags and required fixes.

Disallowed blueprint content:

- final prose paragraphs;
- dialogue/action blocking as the only beat content;
- camera-direction language that has no narrator or viewpoint duty;
- generic emotion labels without trigger/evidence;
- "make this more moving" style instructions;
- free-form markdown that cannot be reviewed field-by-field.

Anti-screenplay checks:

- every ordinary beat needs at least one prose duty beyond action or dialogue;
- every dialogue-heavy beat needs subtext, interiority, withheld reaction, environment pressure, or rhythm duty;
- every scene transition needs causal, emotional, informational, or viewpoint pressure;
- every beat must declare whether the narration should compress, dwell, withhold, reveal, contrast, or linger;
- repeated action-only beats are review failures unless the chapter function explicitly requires a fast exchange;
- the draft generator may not treat beat summaries as stage directions.
