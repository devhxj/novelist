# Corpus-driven writing harnesses

## Recovery timing harness

`run-recovery-harness.ps1` writes `corpus-m2-recovery-metrics-v2`. It intentionally keeps two evidence classes separate:

- `checkpoint_recovery`: two rounds across the five force-kill transaction points. This proves durable completion/restart behavior, not a wall-clock deadline.
- `runtime_wall_clock`: real worker-loop samples for pause, cancel, and stale lease recovery. The default is 30 samples for each control path and 30 stale-lease samples; it records P50/P95/max, per-case state, and the lost-worker fenced-commit result.

Run the formal M2 recovery gate with:

```powershell
./scripts/corpus-driven-writing/run-recovery-harness.ps1 -Rounds 2 -RuntimeSamples 30 -Configuration Release
```

The runtime gate requires pause/cancel P95 at or below 60 seconds, stale lease reclaim P95 at or below 30 seconds after the actual lease expiry, and no completion written by the expired worker. Harness-only timing options shorten the lease without changing the production worker defaults of 45 seconds lease and 10 seconds heartbeat; no test advances `UtcNow` to manufacture expiry.

## Scale harness

`run-scale-harness.ps1` defaults to `-Mode FullPipeline` and a 50,000-character fake-LLM fixture. The formal route creates real anchors, libraries, memberships, session bindings, sentence/passage snapshots, scheduler jobs, and a worker using a fake analyzer. It is the regular M2 gate; the first Release run processed 13,385 work items in about eight minutes.

Run the formal gate with:

```powershell
./scripts/corpus-driven-writing/run-scale-harness.ps1 -Configuration Release
```

The run publishes three independent outputs beneath `build/tmp/corpus-driven-writing/`:

- `scale-50k.stdout.json`: host console result for diagnostics.
- `scale-50k-progress.json`: atomically replaced `corpus-m2-full-scale-progress-v1` progress state.
- `scale-50k-metrics.json`: atomically written `corpus-m2-full-scale-metrics-v1` formal report.

The full-pipeline report requires at least two anchors, libraries, and session bindings; complete output with no duplicates; persisted budget penetration/reserved tokens/active leases all zero; at least 30 claim/list/progress samples; and the configured throughput/P95 limits. It also records database and managed-memory measurements.

`run-m2-quantitative-acceptance.ps1` now uses the same full-pipeline 50K default unless `-SkipScale` is supplied. Recovery and scale evidence remain separate in its v2 summary.

Run isolated harness tests with:

```powershell
./scripts/corpus-driven-writing/test-scale-harness.ps1 -Configuration Release -BenchmarkWorkItems 1000
```

These use a temporary directory and cover both the full-pipeline smoke path and the explicit job-store path. The latter retains the 1,000 work-item direct reserve/record/finalize micro-benchmark for fast hot-path regression only.

For a legacy job-store result that only wrote completed stdout, use:

```powershell
./scripts/corpus-driven-writing/finalize-existing-scale.ps1
```

The finalizer is read-only with respect to the scale database. It rejects incomplete JSON, unsupported schemas, failed results, and reports missing required measurement fields.

## Workload tiers

Use the full pipeline for normal verification. Use `-Mode JobStore` only when intentionally measuring the narrow direct store path:

```powershell
./scripts/corpus-driven-writing/run-scale-harness.ps1 -Mode JobStore -Configuration Release -MinimumCharacters 1000 -Output build/tmp/corpus-driven-writing/scale-job-store-metrics.json
```

The 2,000,000-character workload is deliberately explicit and non-blocking. It is for release diagnostics, performance investigation, or a million-character capacity claim, never for routine development or M2 completion:

```powershell
./scripts/corpus-driven-writing/run-scale-harness.ps1 -Mode JobStore -Configuration Release -MinimumCharacters 2000000 -Output build/tmp/corpus-driven-writing/scale-2m-metrics.json
```

## Writing-effect evaluation

`run-writing-evaluation.ps1` evaluates a redacted dataset containing only IDs, hashes, numeric results, and human labels. It writes aggregate retrieval, blueprint, and prose metrics beneath `build/tmp/corpus-driven-writing/`; it never writes source or candidate prose to the report.

```powershell
./scripts/corpus-driven-writing/run-writing-evaluation.ps1 -Fixture <redacted-fixture.json> -Output build/tmp/corpus-driven-writing/evaluation/<dataset-id> -Configuration Release
```

The checked-in three-case contract fixture proves the schema and reporting path only. A `dataset_kind: human` input must contain 50-100 query cases, 20-30 blueprint cases, and 20-30 insertion cases before the harness will emit a report. Query reason codes use the fixed evaluation codebook, and each insertion case requires distinct reviewer hashes. See [the evaluation protocol](../../docs/corpus-driven-writing/evaluations/README.md) and [the annotation kit](../../docs/corpus-driven-writing/evaluations/writing-evaluation-kit.md) for the schema, collection rules, and evidence boundary.

## Usability-study reporting

`run-usability-study-evaluation.ps1` validates the five fixed core tasks using only hashed participant IDs and numeric/enumerated results. Failure and recovery codes must use the fixed study codebook. A `study_kind: human` report requires at least five participants and passes only when at least four complete every task without a prompt.

```powershell
./scripts/corpus-driven-writing/run-usability-study-evaluation.ps1 -Fixture <redacted-study.json> -Output build/tmp/corpus-driven-writing/usability-study/<study-id> -Configuration Release
```

The checked-in two-participant contract fixture proves the schema, redaction, and acceptance calculation only. It cannot close M9. See [the evaluation protocol](../../docs/corpus-driven-writing/evaluations/README.md) and [the usability study kit](../../docs/corpus-driven-writing/evaluations/usability-study-kit.md) for fixed task IDs, task cards, codebook, and facilitator rules.
