# 语料处理提速方案（2026-09-15）

> 状态：方案设计稿，**尚未改动任何代码**。
> 适用范围：`ReferenceMaterialization`（UI 可达的材料化主链路）。durable analysis 轨见 §9，本期不做。

## 1. 背景与结论摘要

语料处理功能本身已收口（`user-perspective-review-*` 四轮完成），但**端到端耗时在小时量级**，作者难以在一次工作流内跑完一本书。

根因不是单点，而是三个结构性放大器叠加：

1. **章级完全串行**：批次被写死为 1 章，批内章节再逐个串行处理。
2. **每章多趟提取、每趟通读全章**：同一份章文本在前缀里被重复发送 2–5 遍。
3. **SQLite 处于最保守配置**：无 WAL、禁用连接池、无批量写入。

**前置结论：不测量不许动手（§4）。** 材料化链路目前没有任何阶段级耗时数据，下文所有收益数字均为**待验证的量级估计，不是承诺**。

---

## 2. 现状基线（代码证据）

### 2.1 主链路与串行点

```
EnqueueReferenceMaterialization                 frontend/src/lib/novelist/api.ts:115
  └─ ReferenceMaterializationWorker.ProcessRunOnceAsync   （每轮只取 1 个批 = 1 章）
       ├─ ClaimCurrentBatchAsync / MaintainLeaseAsync     ReferenceMaterializationWorker.cs:190, :522
       ├─ ProcessChapterAsync                             :334
       │    ├─ PlanChapterExtractionAsync          ← LLM ①（每章 1 次）
       │    ├─ for each round: ExtractChapterRound ← LLM ②③④…（2–5 趟，每趟通读全章）
       │    ├─ QualifyAndEmbedChapterAsync         ← LLM（25 候选/次，do-while）
       │    └─ EmbedAsync                          ← 嵌入
       └─ _indexer.IndexCurrentBatchAsync                 :233
```

批次常量被写死为 1：

```569:570:src/Novelist.Infrastructure/App/SqliteReferenceMaterializationRunStore.cs
    // 批次概念已废除：每批恒 1 章（状态表列名保留兼容旧库，值恒为 1）。
    public int ChapterBatchSize => ReferenceMaterializationBatchSizes.Default;
```

趟也串行，续跑亦在同一线程：

```518:521:src/Novelist.Infrastructure/App/ReferenceMaterializationChatCompletionQualifier.cs
        for (var requestIndex = 0;
             collected.Count < MaxMaterialsPerRequest && requestIndex < MaxContinuationRequests;
             requestIndex++)
```

粗估：单章 4–6 次模型调用，长输出每次数十秒，**100 章落在小时量级**，与体感吻合。

### 2.2 关键阈值

| 常量 | 值 | 位置 |
|---|---|---|
| `MaxCandidatesPerRequest` | 25 | `ReferenceMaterializationChatCompletionQualifier.cs:16` |
| `MaxMaterialsPerRequest` | 10 | 同上 `:29` |
| `MaxExtractedMaterialsPerChapter` | 40 | 同上 `:33` |
| `MaxExcerptCharsPerRequest` | 6,000 | 同上 `:32` |
| `MaxOutputTokens` | 40,960 | 同上 `:36` |
| `MaxContinuationRequests` | 6（可写） | 同上 `:418` |
| `MinCandidatesForBatchSplit` | 4 | 同上 `:420` |
| `EmbeddingBatchSize` | 64 | `SqliteReferenceAnchorService.cs:23` |
| worker idle delay | 1s | `ReferenceMaterializationWorker.cs:48` |

源码注释记录了两项必须尊重的真实约束：

- 网关约 2 分钟会掐断长生成（"The response ended prematurely"）。
- 并发发整章请求会直接触发 429（`ReferenceMaterializationWorker.cs:219-220` 的注释即为此而写）。

### 2.3 已有性能证据的边界

`evidence/scale-50k-metrics-2026-07-10.json`（13,385 work items / 29.16 items/s / 459s）**来自 durable analysis 轨 + FakeAnalyzer**，不含真实模型延迟，且该轨前端已退役（§9）。它只能用来约束 SQLite 层改动，**不能用来推断材料化耗时**。

---

## 3. 目标与非目标

### 目标

- 端到端材料化耗时降低 **3–5 倍**（100 章量级：从小时级进入 30–60 分钟）。
- **素材召回质量不下降**（以 §8 的召回对拍为准）。
- 50K 门禁与既有评测资产保持通过。

### 非目标（AGENTS.md 红线）

- **不扩张专家控制面**：不新增并发度 / 趟数 / 批大小的用户可见设置项。调节一律做成内部自适应策略或 `internal` 常量，作者看到的界面不变。
- **不放松人工关卡**：章节边界确认、注入范围确认保持人工（`user-perspective-review-2026-09-02-round3.md:204`）。
- **不做无上限的性能榨取**：`progress-audit-2026-07-10.md:121` 已警告，持续堆优化会把项目变成"可靠的语料处理系统"，而不是"能改善写作的系统"。
- **不重建已删除的分析面板**（§9）。

---

## 4. 阶段 M0：埋点测量（前置，必须先做）

理由：多条候选优化互相冲突（"提批大小" vs "会被掐流"、"去规划" vs "适配章节差异"），没有实测数据只能靠猜。

### 改动

在 `ProcessChapterAsync`（`ReferenceMaterializationWorker.cs:334`）内按阶段打 `Stopwatch`：

| 计时项 | 覆盖代码 |
|---|---|
| `plan_ms` | `PlanChapterExtractionAsync` |
| `round_ms[]` | 每个 `ExtractChapterRoundAsync`（含续跑的每次请求） |
| `qualify_ms` | `QualifyAndEmbedChapterAsync` |
| `embed_ms` | `EmbedAsync` + `PersistEmbeddingsAsync` |
| `index_ms` | `IndexCurrentBatchAsync` |
| `db_ms` | 本章全部 store 往返累计 |

同时记录：`rounds_total`、`model_calls`、`prompt_tokens`、`completion_tokens`、掐流命中次数。

### 出口（不新增 UI 控件）

- 写入 run 状态诊断字段，沿用现有 `MaterializationStatus` 结构回传。
- 额外落一份本地诊断日志便于事后分析。
- 前端不加任何新面板或开关。

### Gate

跑完一本 ≥ 50 章的真实书，产出阶段占比表，回答三个问题：

1. `plan_ms` 占总墙钟的真实比例？（判定 M2 是否值得做）
2. 平均每章趟数、每趟平均产出条数？（判定 M3 怎么调）
3. `db_ms + index_ms` 占比是否 > 10%？（判定 M4 是否值得做）

**拿到这张表之前，M1–M4 一律不许开工。**

### 4.1 实现状态：已完成（2026-09-15）

埋点已落地，**未改动任何业务逻辑、contracts 与 UI**。

| 项 | 落地方式 |
|---|---|
| 诊断载体 | `src/Novelist.Infrastructure/App/ReferenceMaterializationChapterDiagnostics.cs`（新增） |
| 计时点 | `ReferenceMaterializationWorker.ProcessChapterAsync` 内：plan / 每趟 round / qualify / embed；`ProcessRunOnceAsync` 内：index |
| 传递方式 | `ReferenceMaterializationChapterDiagnosticsSink` 以参数透传——`ProcessChapterAsync` 有多个提前返回分支，用 sink 可让所有分支共享同一份计时。每章一个实例，**M1 并发下天然互不干扰** |
| `db_ms` | 不单独埋点（需包裹每个 store 调用，侵入过大），改用差值 `other_ms = total - plan - round - qualify - embed - index` 近似，足以回答"`db + 其他`是否 > 10%" |
| 输出 | 语料库同目录 `materialization-diagnostics.jsonl`，每行一章，含 `plan_ms / round_ms / round_count / qualify_ms / embed_ms / index_ms / other_ms / total_ms / model_call_count` |
| 失败语义 | 写入异常全部吞掉——观测设施绝不能成为材料化失败的原因 |

**未动 `ReferenceMaterializationStatusPayload`**：它是 29+ 字段的位置 record，`ReadStatus` 按 `reader.GetInt32(n)` 序号构造，加字段既易错又会牵连前端类型。因此改为本地 JSONL，零契约变更。

### 4.2 取数与判读

跑完一本 ≥ 50 章的书后，直接用汇总脚本（**2026-09-15 已加**）：

```powershell
pwsh -File scripts/corpus-driven-writing/summarize-materialization-diagnostics.ps1
# 指定文件 / 只看某个 run / 多看几章
pwsh -File scripts/corpus-driven-writing/summarize-materialization-diagnostics.ps1 `
     -Path "<数据目录>/reference-anchor/materialization-diagnostics.jsonl" -RunId "mat-xxx" -Top 10
```

脚本会输出阶段占比条形图、最慢的 N 章，并**直接给出三个 gate 结论**（做 / 不做）。裸跑时会自动在常见数据目录下查找诊断文件。

手工口径（脚本即按此实现），取 `%APPDATA%/.../reference-anchor/materialization-diagnostics.jsonl`，按 run 过滤：

1. `sum(plan_ms) / sum(total_ms)` — ≥ 8% 才做 M2。
2. `avg(round_count)` 与 `sum(model_call_count)/sum(round_count)` — 后者 > 1 说明发生了掐流续跑，M3-a 需谨慎。
3. `sum(other_ms + index_ms) / sum(total_ms)` — > 10% 才做 M4。

---

## 5. 阶段 M1：章级受控并发 + 前后台配额隔离

预期收益：**最大单项改善，估计 2.5–4x**。改动量中等，风险中。

### 5.0 实现状态：核心已完成（2026-09-15）

| 项 | 落地方式 |
|---|---|
| 批大小 | `ReferenceMaterializationBatchSizes.Concurrent = 5`，`Default` 指向它；`ChapterWise = 1` 保留供回滚与兼容旧库 |
| 并发执行 | `ProcessRunOnceAsync` 改为 `Task.WhenAll` + `SemaphoreSlim(并发度)`，每章独立 store 实例 |
| 并发度 | AIMD：起步 2、上限 4、连续 4 章成功 +1、任一章失败 -1 并冷却 30s。全部内部自适应，无配置项 |
| 失败隔离 | 新增 `SqliteReferenceMaterializationRunStore.FailChapterAsync`，只挂该章，不删租约、不动 run 状态 |

**踩到的坑（已解决）**：`reference_materialization_runs` 上有 `CHECK (chapter_batch_size IN (1, 5, 10))` 约束，最初取的 4 非法、导致大批测试报 `SQLite Error 19`。改批大小前必须先确认该约束。

**测试**：全量 1034 项通过。6 个固化"逐章/串行"契约的用例已按新语义更新（批冻结语义保留，只是批大小从 1 变 5）。

**未做**：§5.3 前后台配额隔离——需要跨链路仲裁点（聊天/写作/高级素材分析属前台），是独立的下一项。

### 5.1 改法

`ChapterBatchSize` 从恒 1 改为**内部自适应并发度 N**（起始 2，上限 4），一个 lease 批覆盖 N 章。

配合 `SemaphoreSlim(N)` + AIMD：

- 起步并发 2；连续 K 章无 429 / 无掐流则 +1，上限 4。
- 出现 429 或传输中断立即 -1（下限 1），并在冷却期内禁止回升，避免抖动。
- 自适应状态存于 worker 实例，**不落 UI、不做配置项**。

### 5.2 前置改动（不先做完不许上并发）

`FailCurrentBatchAsync`（`ReferenceMaterializationWorker.cs:257`、`:266`）当前语义是**一章失败判死整批**。并发下会把单点故障放大成整批多章报废。必须先改为：

- **章级失败隔离**：单章失败只标记该章，同批其余继续。
- 仅两类情况整批停止：租约丢失（`leaseLost`）、worker shutdown。
- 结构改为 `Task.WhenAll` + 每章独立 `try/catch`，保留 `ThrowIfLeaseLost` 检查点。
- **`IndexCurrentBatchAsync`（`:233`）必须在批内所有章落地之后统一执行**，且要按"实际已完成的章"取集合，而不是假定批内全部成功——并发下批内必然存在被单章失败跳过的章。

### 5.3 前后台配额隔离（评审补入，关键）

材料化 worker 与作者的聊天 / 写作共用同一 provider 与配额。后台并发会**抢占作者正在等待的前台请求**，表现为"一跑语料，写作生成就变慢"。因此必须与并发同批实现：

- 前台有进行中 LLM 请求时，后台降级到并发 1，让出配额。
  - **实现状态：已完成（2026-09-15）**。`ModelRequestArbiter`（`src/Novelist.Core/App/`）用 `AsyncLocal` 标记后台流——**默认视为前台，只有后台 worker 显式打标**，因此不必给每个 bridge 入口埋点、也不改契约。`StandardChatCompletionClient` 的两个入口 `Enter/Exit` 计数，后台额外 `YieldToForegroundAsync`（单次让路上限 30s，避免作者连续写作时后台被饿死）。前台永不等待。
  - 顺带发现：`StandardChatCompletionClient` 本就有全局节流（静态 `ProviderStartGates` + `MinProviderStartGapMs = 2000`，注释写明是为"材料化并行批次"排队），但它**不区分前后台**，后台并发会挤占前台排队位——这正是本项要补的优先级。
- **前台/后台的判定口径**：作者主动发起、同步等待结果的一律算**前台**——包括章节写作生成、聊天、**高级素材分析（`StartReferenceAdvancedMaterialAnalysis`，`api.ts:319` 用 `timeoutMs: null` 同步长等待）**。只有 `ReferenceMaterializationWorker` / `ReferenceCorpusAnalysisWorker` 这类后台循环算后台。口径写死在仲裁点，不做配置。
- 前台请求不被后台排队阻塞。现有 `_pumpGate`（`SemaphoreSlim(1,1)`）只串行化后台自身，不做跨链路仲裁，需新增轻量仲裁点。
- 仲裁逻辑对用户不可见。

### 5.4 幂等与恢复

per-chapter 状态机（`Pending/Extracting/Qualifying/Embedding/Indexing`）本就是崩溃可恢复的最小单位，这是并发改造成立的基础。需验证：批内 N 章处于不同阶段时崩溃，重启后 `ExpireLeasesOfDeadWorkersAsync`（`:72`）能否正确回收并重做未完成章节。

**验收（已更正）**：初版此处写的 `run-recovery-harness.ps1 -Rounds 2` **是错的**——那个 harness 属于 durable analysis 轨（`schema_version = corpus-m2-recovery-metrics-v2`，场景是 job/work-item/token 的 `fault`/`recover`），**验证不到材料化**。材料化链路本就没有专用恢复 harness。

改为两个集成测试（2026-09-15 已加，均通过）：

| 测试 | 覆盖 |
|---|---|
| `FailChapterAsyncMarksOnlyTheNamedChapterAndKeepsTheBatchAlive` | 单章失败只挂该章；同批其余章保持 pending；run 不转 Failed；**租约保留**（别的 worker 抢不走，批内其余章可继续跑） |
| `ConcurrentBatchAttemptsEveryChapterEvenWhenAllOfThemFail` | 批内 5 章全部失败时，每章都被独立尝试并记录自己的 error code，而不是"第一章失败就整批停摆" |

**仍未覆盖**：真实进程被杀（而非抛异常）的中断恢复。进程级崩溃靠 `ExpireLeasesOfDeadWorkersAsync` 回收，既有测试 `ClaimReclaimsAnExpiredLeaseAndResetsOnlyTheCurrentIncompleteBatch` 已按新批大小更新并通过，但那是串行场景。若要严格覆盖"并发 + 拉杀进程"，需要起真实子进程跑材料化再 kill——成本较高，留待真实书验证时一并观察。

### 5.5 计数口径

`requestCount` 并发累加必须线程安全。历史上"作者看到的成本是错的"被列为缺陷（`ReferenceMaterializationWorker.cs:399-401` 注释），UI 上的模型调用次数必须仍等于真实付费调用数。

---

## 6. 阶段 M2：消除每章的规划调用

预期收益：**调用数 -15~25%；墙钟降幅需 M0 实测**（规划是小输出请求，延迟占比可能远低于调用数占比）。风险低。

### 改法

`PlanChapterExtractionAsync`（`:185`）每章一次，划分的是**输出素材类型如何分组**，规则要求"6 种类型恰好各属一趟"，与章节具体内容相关性弱，且已有固定兜底 `BuildFallbackRounds()`。

改为 **run 级缓存**：首次规划一次，结果存 run 上下文，后续章复用；保留"规划失败 → 重问一次 → 兜底"的现有降级链。

**边界约束**：缓存**只在单个 run 内有效，不跨 run 持久化**。这样既不引入新的持久状态面（也就没有迁移、清理、失效策略的负担），又避免了"重跑却沿用上次的 planning 结果"这种隐性不一致。重跑 run 天然重新规划。

### 质量守恒风险

同一本书前后半段的类型分布可能不同（例如后半段冲突密集）。缓解：

- 保留"每 N 章重新规划一次"的内部节奏（N 暂定 10），而非全书一次到底。
- 上线前用评测集对拍召回率（§8）。

**若 M0 显示 `plan_ms` 占比 < 8%，本项不做**：收益不抵质量风险。

---

## 7. 阶段 M3：判定批大小与提取趟数

两项目标不同但共用同一条预热 / 输出预算曲线，**打包做无法归因，必须分开**。

### 7.1 M3-a：判定批大小 25 → 40+

`MaxCandidatesPerRequest = 25`，一章 40 候选要跑 2 轮 do-while，每轮夹两次 DB 往返。

风险：注释估算 32K 输出预算下 25×400 token 有余量，但**网关 2 分钟掐流是实测存在的**。批加倍可能反而触发 `MinCandidatesForBatchSplit` 的对半拆分，净收益为负。

**必须与拆批逻辑联测**：掐流率、重试次数、端到端耗时三项同时看，任一恶化即回滚。

### 7.2 M3-b：压缩提取趟数

注释实测每趟产出约 5 条，但 `MaxMaterialsPerRequest = 10`，说明趟数被"产出少"而非"预算满"推高。可把 plan 提示从「Aim for 2 to 5 passes」收紧到「2 to 3 passes」。

风险：一趟同时戴多副镜头**可能降低召回质量**，本项目最需谨慎的一项。

### 7.3 排序

先做 M3-a（纯批大小，逻辑不变，易回滚）。M3-b 须等 M0 给出"平均每章趟数 / 每趟产出"后再用评测集决定，**默认不做**。

---

## 8. 质量守恒与验收口径

任一优化上线前必须通过：

1. `npm --prefix frontend run verify`（含 build、lint、node 单测、corpus/chapter/reference 工作流、app smoke）。
2. `dotnet test Novelist.slnx --no-restore -v minimal`。
3. **50K 门禁重跑**（`run-scale-harness.ps1`），新证据 JSON 存入 `evidence/`。该轨包含 claim p95 ≤ 100ms 等断言；SQLite 改动使其变好是安全的，变慢必须叫停。
4. **召回对拍**：对同一批章节跑优化前后，**素材命中数下降不得超过 5%**，否则判质量回退并回滚该项。
   - ⚠️ **未核实假设**：本文假定 `evaluations/writing-evaluation-kit.md` 能直接产出可比的"素材命中数"指标。**该假设尚未验证。** M0 阶段须先确认评测资产的可用形态；若它不能直接给出该指标，则§6/§7 的质量门槛需要先补做评测工具，而不是想一个数字当红线。
5. **成本不上升**：总 `prompt_tokens + completion_tokens` 不得高于基线（并发不改单次请求，理论持平；M2 / M3 应使其下降）。

仅在 UI 有变化时才需要截图与浏览器工作流（本方案默认不改 UI）。

---

## 9. durable analysis 轨（本期不做）

`SqliteReferenceCorpusAnalysisScheduler` / `ReferenceCorpusAnalysisWorker` 这套持久分析队列：

- **前端已退役**：`CorpusAnalysisJobsPanel` 随 `ccb6d2c` 的专家控制面收缩删除，桥绑定已下线。`tasks.md:264` 明确写着"不要据此恢复已删除的面板或重新引入桥绑定"。
- 当前只有评测 harness 直接消费它。

因此该轨的性能改造（如 `ReferenceCorpusAnalysisWorker.cs:288` 每个 work item 无条件心跳、node×family 的 5 倍调用放大）**只影响评测速度，不影响用户体感**。

处理：不列入本期。若 M4 落地后顺带变快属附带收益，不单独排期。**严禁借"顺手接上 UI"重新引入已退役桥绑定。**

---

## 10. 阶段 M4：SQLite IO 层

预期收益取决于 M0 的 `db_ms` 占比。改动面最大，含一条阻断级前置条件。

### 10.1 三项改动

| 项 | 现状 | 改法 |
|---|---|---|
| journal | 默认 delete 模式，仅 `foreign_keys=ON; busy_timeout=10000`（`SqliteReferenceCorpusAnalysisJobStore.cs:179`） | **WAL** + `synchronous=NORMAL` |
| 连接 | `Pooling = false`（全仓 20+ 处） | 改回默认池化（见 §10.3 的两个硬约束） |
| 写入 | **分析 / work-item 路径**逐行 `CreateCommand` + `ExecuteNonQuery`（`SqliteReferenceCorpusAnalysisJobStore.Persistence.cs:277` 的 `InsertWorkItemsAsync`、`ReferenceCorpusFeatureObservationPersistence.cs:28`） | prepared statement 复用 + 多行 `VALUES` |

> 更正一处过度概括：**向量路径已经做过批量优化**。`SqliteVecProvisioning.cs:104-124` 采用 128 行/语句的 `INSERT ... VALUES`，注释明确说明动机是"逐行命令在逐章重建时是 O(N) 次往返，百章量级的书会放大到十万级语句"。因此 §10.1 的"写入"一项只针对分析作业路径，并且**应当直接复用该实现已有的做法作为参照**，而不是重新设计方案。

### 10.2 阻断级前置：单文件复制会静默丢数据

启用 WAL 后，未 checkpoint 的事务留在 `index.sqlite-wal`，主库文件可能不含最新数据。但全仓存在**按单文件复制**的路径：

- `LegacyDataMigrationService.cs:1065` — `File.Copy(entry, target, overwrite: false)`
- `DataDirectoryRelocationService.cs:205`、`:219` — 同上

这两处分别是**用户数据迁移**与**数据目录搬迁**，AGENTS.md 要求迁移必须 copy-first 且保留源不动。**若开启 WAL 而不处理这两条路径，会造成静默数据丢失——本方案中最严重的单一风险。**

必须同时满足：

#### 全仓审计结果（2026-09-15 完成）

全仓搜索 `File.Copy` / `File.Move` / `ZipFile` / `CreateEntryFromFile` / 目录遍历，结论：

| 路径 | 是否涉及 sqlite 文件 | 判定 |
|---|---|---|
| `DataDirectoryRelocationService.cs:179-226` | ✅ **是** | 🔴 **唯一高危**：递归全目录复制（`EnumerateFileSystemEntries` + 递归），必然包含 `reference-anchor/index.sqlite` |
| `LegacyDataMigrationService.cs:1065` | ⚠️ 待确认 | 🟡 中。该服务的 sqlite 是**只读打开后逐表转 JSON**（`ImportSqliteMetadataAsync` → `WriteJsonAtomicAsync`），不是复制库文件；`File.Copy` 复制的是其余资源。仍待确认其是否也复制 `reference-anchor` 目录 |
| `SqliteReferenceCorpusPackageService` 导入/导出 | ❌ 否 | ✅ 安全。语料包是 **JSONL 文本**（`File.WriteAllTextAsync`），文件头注释写明"同书备份/恢复语义"，不复制库文件 |
| 其余 20+ 处 `File.Move` | ❌ 否 | ✅ 安全。都是 JSON 配置的"写临时文件 + 原子替换"模式 |

**风险面比初版估计的小得多**：真正的阻断项收敛为 1 处（数据目录搬迁），语料包路径安全——初版把包服务列为"同样过审"是过度谨慎。

#### 落地要求

1. `DataDirectoryRelocationService` 复制前对库执行 `PRAGMA wal_checkpoint(TRUNCATE)`，或改为显式复制三件套（`.sqlite` + `-wal` + `-shm`）并保证源库在复制期间不被写入。
   - 注意 `FilesEqualAsync(entry, target)`（`:210`）在 WAL 模式下按主库字节比较，可能误判"内容相同"——checkpoint 后比较才可靠。
2. 补集成测试：**WAL 开启 + 未 checkpoint 状态下搬迁，断言数据完整**。
3. `LegacyDataMigrationService` 待确认后再决定是否一并处理。

### 10.3 连接池有两个硬约束（评审补入）

`Pooling = false` 不能简单地改回 `true`，至少有两个已知坑：

1. **sqlite-vec 扩展是连接级的。** `SqliteVecProvisioning.cs:84-88` 对每个新开的连接执行 `EnableExtensions()` + `LoadExtension(extensionPath)`。启用池化后，池中新建立的物理连接不会自动加载该扩展，向量表查询会以"找不到 vec 函数"的形式失败。要池化就必须保证**连接池里每个新连接都重新加载扩展**，或保留向量路径专用的非池化连接。
2. **`PRAGMA` 是连接级的。** 现有的 `foreign_keys=ON`、`busy_timeout=10000` 在每次 `OpenAsync` 后重设（如 `SqliteReferenceCorpusAnalysisJobStore.cs:178-180`）。连接池会复用物理连接，pragma 的作用域边界随之改变，可能出现在某些连接上未生效的情况。

因此把"改回池化"列为**独立可交付项**而不是顺手改：它需要一个统一的连接工厂来承载 pragma 与扩展加载，而不是逐个文件把 `Pooling = false` 删掉。

### 10.4 `Pooling=false` 的成因仍须查证

即便解决了上述两条，该设置散布 20+ 文件、横跨多个服务，不像随手写的。改前仍须确认是否在规避其他问题（连接泄漏、多进程写竞争）。

**建议本期不做池化与批量写入**，直到 M0 证明 `db_ms` 确实是瓶颈。

### 10.5 WAL 是写入数据库文件的、不可逆的属性

`journal_mode=WAL` 一旦设置会持久化在数据文件里。这意味着：

- 即使后续回滚代码，已开 WAL 的库仍是 WAL 模式（旧版代码可以正常读写 WAL 库，不会坏）。
- 但它**不可逆地改变了用户数据文件的形态**，且对 `-wal` / `-shm` 副文件的处理会长期存在于备份、迁移、打包路径。
- 因此必须在 §10.2 的审计完整通过后再开，不接受"先开了再说"。

### 10.6 降级结论

**M4 本期只做 WAL + §10.2 的前置处理**，池化与批量写入推迟。

---

## 11. 明确不做

- **前端轮询优化**：`ReferenceCorpusWorkspace.tsx:522` 的 3s 轮询是轻量查询，不是瓶颈。为其引入事件推送改动大、收益近零，且容易顺带长出新的 UI 面。
- **并发度提到 8+**：注释明确记载并发触发 429、网关掐断长生成。上限 4 且必须 AIMD。
- **牺牲取材质量换速度**：不降低 `MaxExcerptCharsPerRequest` 以下的取材标准。
- **任何新的 UI 面板或设置项**。

---

## 12. 风险登记表

| ID | 风险 | 等级 | 缓解 |
|---|---|---|---|
| R1 | WAL 使单文件 `File.Copy` 静默丢数据 | 阻断 | §10.2 上线前置 + 集成测试 |
| R2 | 后台并发抢占前台写作配额，作者体感反而变差 | 高 | §5.3，与 M1 同批交付 |
| R3 | 并发下一章失败判死整批，故障放大 | 高 | §5.2 先做章级隔离再上并发 |
| R4 | 提高判定批大小反而触发 2 分钟掐流，净收益为负 | 中 | §7.1 与拆批逻辑联测，恶化即回滚 |
| R5 | 去规划调用导致跨章节类型分布失配 | 中 | §6 每 N 章重规划 + 召回对拍 |
| R6 | `Pooling=false` 有未知成因，改动引入回归 | 中 | §10.3 先查证，本期不动 |
| R7 | 收益估计缺乏数据支撑 | 中 | M0 必须先跑；所有数字标注为估计 |
| R8 | 召回质量下降但吞吐上升 | 中 | §8 第 4 项，5% 红线 + 回滚 |
| R9 | 并发破坏崩溃恢复语义 | 中 | §5.4 恢复 harness 验收 |
| R10 | UI 模型调用数与真实计费不一致 | 中 | §5.5 线程安全累加 + 集成断言 |

---

## 13. 执行顺序

```
M0 埋点测量  →  拿到阶段占比表 gate      【已完成 2026-09-15，待跑真实书取数】
                    │
        ┌───────────┼───────────┬──────────────┐
        ▼           ▼           ▼              ▼
   M1 并发+隔离  M3-a 批大小   M2 去规划    M4 WAL（含 10.2 前置）
   （收益最大）  （易回滚）   （plan_ms≥8% 才做） （db_ms>10% 才做）
        │
        ▼
   每项独立：召回对拍 + 50K 门禁 + 全套 verify
```

原则：

- M1 与 §5.2 章级隔离、§5.3 配额隔离**必须同批交付**，不可只上并发。
- 每项优化独立提交、可独立回滚，不做"大爆炸式"重构。
- 任一召回对拍不达标无条件回滚该项，不接受"吞吐换质量"的交易。
- 全部完成后，在 `evidence/` 补一份新的 50K 指标快照。
- **M1 上线后 M3-a 必须重测基线**：并发会改变掐流概率，M3-a 在串行下测出的收益不可直接迁移。

---

## 14. 评审记录（2026-09-15）

首轮自查后对方案做的修正，保留在此以便复核：

| # | 评审发现 | 处置 |
|---|---|---|
| C1 | 原稿拟把并发度做成可调配置项 | 违反 AGENTS.md「不扩张专家控制面」，改为内部 AIMD 自适应，UI 不变（§5.1） |
| C2 | 原稿未考虑后台并发抢占前台写作配额 | 补 §5.3，与 M1 强制同批交付；否则可能出现"语料快了但写作变慢"的净负体验 |
| C3 | 并发下单章失败会判死整批（`FailCurrentBatchAsync`） | 补 §5.2，章级失败隔离作为上并发的前置条件 |
| C4 | 原稿误称"全仓无批量写入" | 已更正：`SqliteVecProvisioning.cs:104-124` 早有 128 行/语句批量，并应作为参照实现（§10.1） |
| C5 | WAL + 单文件 `File.Copy` 会造成静默数据丢失 | 补 §10.2 阻断级前置，`LegacyDataMigrationService.cs:1065`、`DataDirectoryRelocationService.cs:205` 必须处理 |
| C6 | 连接池与 sqlite-vec 扩展加载冲突 | 补 §10.3，`LoadExtension` 是连接级的，池化后新连接不会加载扩展 |
| C7 | `journal_mode=WAL` 不可逆地改变用户数据文件 | 补 §10.5，须在 §10.2 审计完整通过后才能开 |
| C8 | 原稿把 durable analysis 轨的改造计入范围 | 该轨前端已退役（`tasks.md:264`），只影响 harness；移出本期并写明禁止重建 UI（§9） |
| C9 | 5% 召回红线缺乏已验证的测量手段 | 在 §8 标注为未核实假设，要求 M0 先确认评测资产可用性 |
| C10 | 原稿把前端轮询优化列为可选项 | 3s 轻量查询非瓶颈，明确移入"不做"（§11） |
| C11 | 缺索引步骤的并发语义 | 补 §5.2 末条：`IndexCurrentBatchAsync` 须按实际已完成章取集合 |
| C12 | 缺 plan 缓存的生命周期边界 | 补 §6：仅 run 内有效，不跨 run 持久化，避免新增持久状态面 |

**仍未解决、需在执行中确认的问题：**

- M0 之前无法给出可靠的墙钟收益数字，本文所有百分比均为量级估计。
- 真实 429 / 掐流率依赖用户的 provider 与套餐，AIMD 的具体阈值（起步 2、上限 4、冷却时长）需要在真实环境上调。

---

## 15. L1 / L2 影响核对（2026-09-15 追加）

用户提问："提速会不会影响 L1 / L2 语料的生成"。核对结论如下。

### 15.1 术语与依赖边界

| | L1 | L2 |
|---|---|---|
| 含义 | 原子摘录（素材） | 高级写作素材（观测 / 机理 / 策略，三层 × 五类） |
| 落点 | `reference_materials` | `reference_advanced_materials`（统一外壳表） |
| 由谁产出 | **材料化链路**（本文优化对象） | 两条路径，见 15.2 |

**本文 M1–M3 全部只作用于 L1（材料化）。L2 不消费 `reference_materials`。**

### 15.2 L2 的两条生产路径都不读 L1 素材

**路径 A — 管道直产**（`ReferenceAdvancedMaterialPipelineService.ProcessAnchorAsync:40`）：

```123:133:src/Novelist.Infrastructure/App/ReferenceAdvancedMaterialPipelineService.cs
    private async ValueTask<IReadOnlyList<TextNode>> ReadNodesAsync(long anchorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var scenes = await ReadNodesByTypeAsync(connection, anchorId, "scene", cancellationToken);
        if (scenes.Count > 0)
        {
            return scenes;
        }

        return await ReadNodesByTypeAsync(connection, anchorId, "chapter", cancellationToken);
    }
```

输入是 `reference_text_nodes` 的 **scene**（无 scene 时回退 chapter），逐 node × 4 family + specimen 调模型。

**路径 B — 投影**（`SqliteReferenceAdvancedMaterialProjectionService:105-165`）：源表是 `reference_feature_observations` / `reference_technique_specimens`，经 `IsValidEvidence` 校验后投影。

**两条路径的输入都不是 L1 素材表。** 这是"提速不影响 L2"的结构性依据。

> ⚠️ 修正一处表述：路径 B 的源表 `reference_feature_observations` **并非只装 durable analysis 轨的 LLM 观测**，它同时混有**导入阶段的 Stage1 确定性观测**（见 §15.6）。因此路径 B 的完整性依赖"导入层 + durable 层"两侧，不只依赖 durable 轨。

### 15.3 逐项影响判定

| 优化项 | 对 L1 | 对 L2 | 依据 |
|---|---|---|---|
| M1 并发 | ✅ 不影响内容 | ✅ 不影响 | `node_id` 确定性派生（`split-chapter:{index}:{hash}`，`SqliteReferenceMaterializationRunStore.cs:393`），offset 为章内字符偏移，均不随执行顺序改变 |
| M1 并发（失败隔离未做时） | ⚠️ 可能丢章 | ⚠️ 间接 | 章失败若判死整批会丢多章；§5.2 已列为上并发的前置条件 |
| M2 run 级 plan 缓存 | ⚠️ 类型数量分配可能变化 | ✅ 不影响 | 6 种类型由代码强制"恰好各属一趟"，覆盖率不变；L2 不看素材类型 |
| M3-a 判定批大小 | ⚠️ 需实测 | ✅ 不影响 | 判定标准不变，仅单次喂入候选数变化 |
| M3-b 压缩趟数 | 🔴 **风险最高** | ✅ 不影响 | 直接减少 L1 素材数量与类型平衡；默认不做 |
| M4 WAL（前置处理完备） | ✅ 不改数据语义 | ✅ 不影响 | 纯存储层 |
| M4 WAL（§10.2 前置未做） | 🔴 静默丢失 | 🔴 **evidence 解析失败率飙升** | 见 15.4 |

### 15.4 唯一会同时伤及 L1 与 L2 的场景

若开启 WAL 而未处理单文件复制路径（`LegacyDataMigrationService.cs:1065`、`DataDirectoryRelocationService.cs:205`），迁移/搬迁会丢数据。对 L2 的连带伤害尤其隐蔽：

`SqliteReferenceAdvancedMaterialProjectionService` 的 `IsValidEvidence(nodeId, start, end, nodeLengths)` 校验失败时**静默丢弃该条并累加 `invalidEvidence`**（`:136-140`）。也就是说 L2 不会报错，只会**变少**——与 L1 丢失叠加后表现为"高级素材产出量下降且无错误提示"。

这正是 §10.2 被定为阻断级的原因。

### 15.5 结论

**设计上 M1–M3 不影响 L1/L2 的生成内容，只影响 L1 的产出速度（M3-b 例外）。** 需要守住的只有两条：

1. §5.2 的章级失败隔离必须先于并发落地（否则丢章）。
2. §10.2 的 WAL 复制前置必须完备（否则 L1 丢失 + L2 evidence 静默失效）。

另需注意：M3-b 若最终要做，只影响 L1 素材丰富度，不影响 L2；但对写作注入质量有直接影响，必须由 §8 的召回对拍拦截。

### 15.6 基础层（导入阶段）核对

用户提到的"基础语料"在仓库里无精确术语（全仓 0 命中；`L0–L4` 是**改写级别**而非语料层级，`ReferenceAnchorPayloads.cs:8-13`）。按最可能含义核对**导入阶段的确定性产出层**：

| 内容 | 落点 | 产出者 |
|---|---|---|
| 切分节点 | `reference_text_nodes` | `SqliteReferenceAnchorService.BuildSegments`（`:6782`） |
| 规则抽取的候选素材 | `reference_materials` | `BuildMaterials`（`:385`、`:1307`） |
| Stage1 确定性观测 | `reference_feature_observations` | `UpsertStage1DeterministicObservationsAsync`（`:3808`） |

**两处调用点 `:389` 与 `:1330` 都在 `SqliteReferenceAnchorService` 的导入 / 重建事务内**（与 `ReplaceSegmentsAsync`、`ReplaceMaterialRowsAsync` 同一事务），**不在 `ReferenceMaterializationWorker` 里**。

Stage1 的三条确定性证据：

1. **不调 LLM**：纯规则计算 `BuildDeterministicRhythm(segment.Text)`（`:3835`），confidence 固定 0.95。
2. **run_id 确定性派生**：`"stage1-" + anchorId + "-" + sourceHash[..16]`（`:8549`）。
3. **证据偏移确定性**：`EvidenceStart = 0`、`EvidenceEnd = segment.Text.Length`（`:3861-3862`）。

**判定：基础层完全在本文优化范围之外。** M1 并发、M2 plan 缓存、M3 批大小/趟数均作用于材料化 worker，不进入导入流程；基础层本身无 LLM 调用，也不受 §5.3 的配额仲裁影响。

**但基础层会流入 L2**：Stage1 的 `feature_family = "rhythm"`，经 `FamilyMap["rhythm"] = Craft`（`SqliteReferenceAdvancedMaterialProjectionService.cs:18`）投影为 L2 的 **Craft** 条目。即：

```
导入（确定性）→ reference_feature_observations(rhythm) → 投影 → L2.Craft
```

因此 §15.4 的告警对基础层同样成立：**若 M4 的 WAL 复制前置未处理，基础层数据丢失会同时削减 L2 的 Craft family 来源**，且同样是静默的（`IsValidEvidence` 失败只累加 `invalidEvidence`，不报错）。这进一步提高了 §10.2 的优先级。

### 15.7 按"基础 / 高级"口径重新对齐（用户澄清后的映射）

用户的口径是：**基础语料 = 单章产出的多维度语料**，**高级语料 = 针对书籍的技法等**。

按 `frontend/src/lib/novelist/corpusTaxonomy.ts` 的权威词表对齐：

| 用户口径 | 对应实体 | 维度来源 | 生产链路 |
|---|---|---|---|
| **基础语料**（单章·多维度） | `reference_materials` | `COVERAGE_FACET_LABELS`（`corpusTaxonomy.ts:29-36`）：素材类型 / 叙事功能 / 情绪机制 / 场景节拍 / 视角 / 技法，共 6 维，真值为 `SqliteReferenceAnchorService.MaterialCoverageFacetColumns` | **材料化**（本文优化对象） |
| **高级语料**（书级·技法） | `reference_advanced_materials` | `ADVANCED_MATERIAL_FAMILY_LABELS`（`:40-46`）：世界观 / 文风 / 写法 / 技巧 / 结构 | L2 pipeline 或投影 |

按此口径，§15.3–§15.5 的结论成立：**基础语料正是本文优化对象本身**，M1/M2/M3-a 不改变其生成内容，M3-b 会直接削减其数量；高级语料不消费 `reference_materials`，不受 M1–M3 影响。

> 注：另有一套"句子级 5 维 / 段落级 5 维 / 场景级 2 维"的词表（`FAMILY_LABELS`，`corpusTaxonomy.ts:60-76`），它对应 `reference_feature_observations` 的 `feature_family`，属 durable analysis 轨，不是材料化产物。

**✅ 已确认（2026-09-15，用户答复）**：用户感受到慢的操作是**在语料区点「开始材料化」跑参考书**。这是前端唯一实际接线的语料生产入口（`ReferenceCorpusWorkspace.tsx:611,641`）。

因此可以收敛分叉：

- **本文优化对象 = 材料化 = 用户口径的「基础语料」**，方向正确，§15.3 的逐项判定成立。
- `FAMILY_LABELS` 那套 12 维（durable analysis 轨）与 `StartReferenceAdvancedMaterialAnalysis`（§15.8）**均不在用户当前动线上**，两者都保持"本期不做"。
- 下文所有"基础语料"一律指材料化产物 `reference_materials`。

### 15.8 新发现：高级语料生产链路目前未接线，且是更大的性能地雷

`StartReferenceAdvancedMaterialAnalysis` 的后端入口存在：

```25:26:src/Novelist.Core/Bridge/ReferenceAdvancedMaterialBridgeHandlers.cs
        // 生产触发入口：单本书一趟走完观测→机理→策略（LLM 长任务，前端按长超时调用）。
        dispatcher.Register("StartReferenceAdvancedMaterialAnalysis", async (context, cancellationToken) =>
```

但**前端 `*.tsx` 中没有任何调用点**（全目录搜索 0 命中），只有 `api.ts:110` 的类型声明与 `:319` 的方法定义。属"后端已注册、前端未接线"的收缩状态。

该链路（`ReferenceAdvancedMaterialPipelineService.ProcessAnchorAsync:40`）若启用，性能问题比材料化严重得多：

1. **节点集无上限**：`ReadNodesByTypeAsync`（`:135-158`）`SELECT ... WHERE anchor_id = ? AND node_type = ? ORDER BY sequence_index`，**无 LIMIT**，一次读入全部 scene（无 scene 则全部 chapter）。
2. **双重串行，零并发**：`foreach (node) × foreach (family)` 逐个 `await` LLM（`:63-91`），4 个 family + 1 次 specimen，即 **每节点 5 次串行调用**。N 个节点 = 5N 次。
3. **无断点续跑**：`EnsureRunAsync` 仅 `INSERT ... ON CONFLICT(run_id) DO NOTHING`（`:107-120`），不记录节点级进度。中断后重跑**从头开始，已付的 LLM 费用全部作废**——与材料化"分趟落库、失败从已完成趟继续"的设计形成鲜明对比。
4. **同步长等待**：前端 `timeoutMs: null`（`api.ts:319`），期间作者只能干等。

**处置**：不纳入本期（UI 未接线则无用户体感）。但若后续要接上 UI，**必须先把这条链路改造为"节点级进度落库 + 受控并发"再接线**，否则等于把一个比材料化更慢、且不可恢复的长任务直接暴露给作者。此项应作为独立的准入条件记录在案。
