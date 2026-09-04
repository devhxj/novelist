# 规模门禁证据归档

本目录把原本只存在于 gitignore 的 `build/tmp/corpus-driven-writing/` 中的**指标产物**（非源语料）纳入版本控制，使 AGENTS.md "formal 50K gate has passed; future changes must preserve it" 的约束可以从仓库直接校验（U6，2026-09-03 工程评审）。

## 50K 全管线门禁（已通过）

- 产物：`scale-50k-metrics-2026-07-10.json`（复制自 `build/tmp/corpus-driven-writing/scale-50k-metrics.json`）
- 关键数字：characters 50,015 / work_items 13,385 / duplicate_outputs 0 / throughput 29.157 work-items/s；P95 claim/list/progress = 10.12 / 27.04 / 3.51 ms
- 复现方式：见 `docs/corpus-driven-writing/development-plan.md` 的规模门禁章节；重跑产物请以同样的文件名模式（`scale-50k-metrics-<date>.json`）归档到本目录

## 2M 长跑档位

唯一历史记录是一次失败（见 `progress-audit-2026-07-10.md`），尚无通过产物；达成后归档于此。

注意：本目录只归档指标 JSON 与说明，不提交源语料、数据库或日志文件。
