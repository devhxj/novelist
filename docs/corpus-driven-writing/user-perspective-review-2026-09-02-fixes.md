# 使用者视角改进落地复评（2026-09-02）

> 对 `user-perspective-review-2026-09-02.md` 三批改进计划的落地复评：逐项对账 U/O/F 共 15 个问题的实现与证据，并记录复评中发现并已修复的新引入缺陷。行号以复评修复后工作区为准。
> 验证基线：`dotnet test` 216/216 单测 + 698/698 集成全绿；前端 lint、`test:choices`、`verify`、`test:app:full`、`test:app:stress` 全绿。
> 第二轮使用者视角修复（#1–#7）与其独立验证基线见第六节。

## 一、复评发现并修复的新缺陷（先说问题）

复评没有停留在"清单打勾"，对新增代码做了第二轮真实链路走查，发现 5 个被 mock 掩盖或语义不实的缺陷，全部已修复并有测试/类型证据：

| # | 缺陷 | 根因 | 修复 |
|---|---|---|---|
| R1 | 语料包导出/导入在真实后端必然失败 | `CorpusPack` 组件未接收 `novelId`，硬编码 `novel_id: 0`；后端 `ValidateNovelId` 直接拒绝。mock 不校验，工作流全绿掩盖了它 | `CorpusAreaView` 把 `novelId` 传入 `CorpusPack`，两处调用改用真实 ID |
| R2 | 取消保存对话框表现为 `INTERNAL_ERROR` | 语料包 bridge handler 没有把 `ReferenceMaterializationException` 映射为 `BridgeRequestException`（同类 handler 均有该映射）；用户点"取消"会看到"Internal bridge error." | 补 `ExecutePackageExport/ImportAsync` 映射；前端把 `materialization_cancelled` 呈现为中性消息而非错误 |
| R3 | 导入语料包第一行就会 SQL 报错 | 导出行包含附带的 `evidence_text` 展示列，`InsertOrIgnoreAsync` 按行键拼 INSERT → "no such column" | 导入前剥离 `evidence_text` 键 |
| R4 | "外键缺失行跳过"的文档承诺不成立 | SQLite 的 `INSERT OR IGNORE` **不**忽略外键冲突——证据节点缺失会中断整个事务而非跳过 | 导入前按 `reference_text_nodes` 存在性预过滤（观察按 `node_id`、标本按 `source_node_id`），跳过计数含被过滤行 |
| R5 | `Dictionary<string, object?>` 反序列化值全是 `JsonElement` | FK 过滤的 `is string` 判断失效（全部行被误跳过），且 `AddWithValue(JsonElement)` 绑定参数同样不可靠 | 解析层 `ConvertElement` 统一转 CLR 原语（string/long/double/bool/null） |

R3–R5 由新增的集成测试 `ReferenceCorpusPackageServiceTests.ImportPackageSkipsMissingNodeRowsAndIgnoresEvidenceColumn` 锚定（fake picker + 真实 SQLite：有效行导入、缺失节点行跳过、`evidence_text` 列兼容、重复导入全跳过）——这是**真实数据库路径**的测试，不再依赖 mock 行为。

另有一处设计修正：计划镜像「在编辑器中打开」改为**只读**打开（`readOnly=true`）——镜像是从槽位单向导出的，若允许在编辑器保存会造成双写分叉（下次槽位保存静默覆盖作者编辑）。

## 二、逐项对账

### 第一批·核心动线感知（5/5）

**U2 章号徽章 — 已实现。** 输入区上方常驻 `chapter-context-badge`：绑定态「第 N 章 · 语料注入开启」、锁定态（琥珀 + 锁图标）、未绑定态（灰）；点击弹出菜单支持锁定任意章号/跟随编辑器/显式不绑定；`effectiveChapterNumber` 统一驱动注入（`effectiveChapterRef`）与覆盖度加载。工作流断言三态切换与锁定后 `chapter_number=3` 的语义由徽章交互覆盖。

**U1 无细纲空态 — 已实现。** 覆盖度 ready 且 `total_count=0` 时显示「本章还没有细纲 + 去创建」，跳转时间线计划区（`onOpenPlans`）。工作流断言空态文案与跳转后的「章节计划」标题。

**O1 直接开写 — 已实现。** `direct-write-button` 发送固定指令「直接开写：请立即按当前细纲开始写本章正文，不再继续访谈。」；isLoading 禁用。工作流断言恰好一次 Chat 调用且消息携带指令。

**U3 帮助章 — 已实现。** HelpDialog 新增「语料写作」tab：核心闭环、细纲三层与 plans/ 镜像说明、覆盖度阈值（<50% 不足、40 beat 上限）、逃生门与选择题原则、积累动线。

**U6 错误升级 — 已实现。** 共享 `describeBridgeError`（`bridgeErrors.ts`）；侧栏 4 处 + 制作页 8 处 catch 全部透出 `BridgeError.code/message`，错误条带「重试」按钮（重放最近失败动作，侧栏用 reload tick 避免自引用）。

### 第二批·积累动线减负（5/5）

**U4/F3 指路与观测 — 已实现。** 覆盖度 payload 新增 `source_books`（Ready 锚点书名，≤5）与每 beat `hit_score`（ScoreComponents 之和，阈值校准观测点）；不足横幅显示「现有语料来源：《X》《Y》」。集成测试断言聚合与分数非空。

**F4 成本可见 — 已实现。** run 状态新增 `model_call_count`（chapter_progress 聚合），面板显示「模型调用 N 次 · 耗时 X 分钟」（时长由 started/completed 推导，进行中显示至今）。token 级成本未做——材料化管线当前不记 token，以调用次数+时长作为 v1 成本口径（与评审建议的"若可聚合则展示"一致）。

**O5 模型直达 — 已实现。** 制作页错误条新增「去配置模型」按钮，打开 `SettingsDialog(initialTab="model")`（`open-model-settings` testid）。

**O6 全局通知 — 已实现。** `useMaterializationWatcher`（WorkspaceView 挂载，非语料区视图时启用）：30s 轮询各锚点 run 状态，running/queued→终态转变产生通知卡（完成/失败/取消 + 书名 + 失败原因 + 查看/关闭），进行中状态栏显示「材料化中 ×N」脉冲指示，点击直达语料区。语料区内由制作页自身的 3s 轮询负责，职责互斥。

**U5② 拖拽导入 — 已实现。** 侧栏拖入 `.txt/.md`（≤20MB）：前端读文件 base64 → 新 bridge `RegisterReferenceMaterializationSourceFromContent` → 后端校验扩展名/大小/非空，写入 `app-data/reference-anchor/sources/`（GUID 文件名，无用户可控路径片段）→ 按普通来源注册并自动选中。拖拽覆盖层 + 非法扩展名前端拦截。集成测试覆盖写盘注册与 `.pdf` 拒绝。

**U5① 分析后台化 — 轻量实现。** 切分分析结果缓存于组件外（`analyzedProfiles`），分析期间切页回来结果不丢；真正的后台 job 化（页面关闭后继续）未做——切分分析是同步 LLM 调用（≤50K 样本，通常 <1 分钟），配合通知与缓存已消除主要摩擦。**残余**见下节。

### 第三批·完整性承诺（6/6）

**F1 计划镜像 — 已实现。** `UpdateChapterPlanAsync` 原子写出 `plans/大纲·部纲·细纲.md`（LF 归一、temp+move）；内容白名单扩展 `plans/(大纲|部纲|细纲).md`；时间线计划区「在编辑器中打开细纲」（**只读**）；标签统一为「细纲/部纲/大纲」。集成测试锚定三文件写入、CRLF 归一、空槽位占位。
*边界说明*：这是"槽位权威 + 文件镜像"的 v1，而非评审设想的"文件为唯一存储"；槽位 blob 兼容不动（存量数据零迁移风险），编辑器内改细纲仍走时间线保存。**残余**：存量计划在下一次保存前不会出现镜像文件（无读时懒迁移）。

**F5 语料包 — 已实现（备份恢复语义）。** `SqliteReferenceCorpusPackageService`：导出（Photino 保存对话框 + 观察/标本全量行 + 证据原文 JSONL）、导入（打开对话框 + 剥离展示列 + FK 预过滤 + `INSERT OR IGNORE` 幂等）；语料包视图从占位变为书选择 + 导出/导入 + 结果消息。真实数据库集成测试锚定（含 R3/R4/R5 修复行为）。
*边界说明*：跨设备迁移需目标库已有同构文本树（FK 依赖），视图内有明示文案；跨设备全量携带属后续工作。

**F2 scene/trope — 已实现（schema 种子层）。** `ReferenceCorpusFeatureFamilies` 新增 `SceneFamilies`（scene/trope）；嵌入 schema `scene.json`（场景目标/冲突来源/转折/结果/入景/出景钩子/节奏）与 `trope.json`（12 个桥段族种子/阶段/情绪兑现/演绎方式），node_type=scene；浏览维度下拉同步。schema 注册表测试更新为 12 family 并断言场景级 NodeType。
*边界说明*：分析管线（scheduler/worker 的 scope 校验仍为 sentence/passage）尚未产出 scene 级观察——schema 与词表是 additive 的第一步，评审预期的"复核选择题归类生长桥段词表"依赖分析侧启用，列入残余。

**O3 复核证据 + 抽样 — 已实现。** 候选卡重构为 `ReviewCandidateCard`：展开显示 `source_spans`（节点 + 偏移区间）；复核列表「全部/前 5/前 10」抽样开关（`aria-pressed`）。

**O4 浏览搜索 + 证据定位 — 已实现。** 浏览新增关键字（`feature_key`，仅观察）与复核状态（unverified/low_confidence/confirmed/rejected）筛选，走后端 page filters；观察卡展开经 `GetReferenceCorpusNodeWindow` 拉取证据原文上下文（`evidence-context` testid），失败降级为「不可用」占位。

## 三、残余事项（复评后的诚实清单）

1. **切分分析后台 job 化**（U5① 完整形态）：当前为"结果缓存 + 完成通知"，页面关闭即中断；如需真后台，可复用分析管线的 run/lease 基建为 chapter-split 建 job 类型。
2. **计划镜像懒迁移**：存量小说的 plans/ 镜像在首次保存后才出现；可在打开书时检测缺失并补写（幂等）。
3. **scene/trope 分析启用**：schema 就绪但管线 scope 不含 scene；需要 worker/analyzer/复核卡片三处扩展。
4. **跨设备语料包**：需连文本树一起序列化/重建（node + 章节 + 锚点），当前为同书备份恢复。
5. **覆盖度来源书精度**：`source_books` v1 为全部 Ready 锚点；按"命中 beat 的实际来源"聚合更精确，待命中数据积累。
6. **F6 真实验证**（积累里程碑 + 消费 A/B）：依赖真实使用，未开始——这是产品论点的最终验收，无法用代码替代。

## 四、验证与提交

- 后端：216/216 单测 + 698/698 集成（新增 `ReferenceCorpusPackageServiceTests`、`PlanningPlanMirrorTests`、内容注册 ×2、覆盖度聚合断言扩展、schema 12 family 断言）。
- 前端：lint 0 error、`test:choices` 5/5、`verify`、`test:app:full`、`test:app:stress` 全绿；工作流新增徽章三态/直接开写/空态跳转/语料包入口断言；过时占位断言与「下一章」placeholder 同步更新。
- 提交：功能落地两个提交 + 本复评修复一个提交（见 git log，均含上述全部改动）。

## 五、结论

三批 15 项（U×6、O×6、F×6，其中 U5 拆为①②）全部落地，无一以"未做"关闭；两处按务实边界实现并显式记录（F1 镜像 v1、F5 备份恢复语义）。复评的核心价值在第一节的 5 个新缺陷——全部是"mock 全绿但真实链路必坏"或"文档承诺与 SQLite 语义不符"的类型，其中 R1（novel_id=0）与 R3（evidence_text 列）在真实桌面应用中会直接让语料包功能不可用。这印证了一条工程教训并已落实：**新增 bridge 功能的真实数据库集成测试必须在 mock 工作流之外同步存在**（本复评补上了语料包的这条测试）。

## 六、第二轮使用者视角修复（#1–#7）

复评之后又做了一轮纯使用者视角走查（便利性 / 可操作性 / 完整性），发现 7 个"功能在、但作者用不顺"的问题，全部已修复。

**#1 浏览关键字搜不到东西。** 原本"关键字"只匹配内部枚举 `feature_key`，作者想搜「压抑」「追车」一无所获。新增 `keyword_contains` 过滤，对 `feature_key` / `value_text` / `explanation` 三列做包含匹配，`EscapeLikePattern` 转义 `\ % _` 防通配符注入；输入框文案改为「搜索内容或说明…」。证据：`SqliteReferenceCorpusAnalysisService`（含服务自身的过滤白名单，与 bridge 白名单是两套）+ `ReferenceCorpusAnalysisBridgeHandlers.ObservationFilterKeys` + 集成测试 `ListFeatureObservationsKeywordFilterMatchesValueTextAndExplanationByContainment`（取值命中、部分词命中、字面量 `%` 不误伤）。

**#2 锁定章号无上限。** 可填任意长数字并落到会话上。前端输入截断 6 位，后端 `MaxChapterNumber = 999_999`，越界即视为未绑定——不抛错、不写脏值。证据：`ChatPanel.tsx` + `FileSystemChatSessionService`。

**#3 待复核候选只有首页。** 作者既不知道队列总量，也拉不到后面的条目。面板显示「待复核共 N 条，当前显示前 M 条」，并提供「加载更多（还有 K 条）」按页续拉、按 `candidate_id` 去重追加。证据：`ReferenceCorpusWorkspace.tsx`（`candidate-total` / `load-more-candidates`）。

**#4 材料化跑完覆盖度不刷新。** 后台终态到达后聊天侧仍是旧数字，要手动切页。`useMaterializationWatcher` 增加 `onCompleted` 回调，由 `WorkspaceView` 触发语料刷新 tick，覆盖度与总览自动跟上。

**#5 标本卡看不到证据原文。** 观察卡已有原文上下文，标本卡只有 `evidence_preview` 片段，判断不了这条手法值不值得用。标本卡展开时按 `source_node_id` 拉 `GetReferenceCorpusNodeWindow` 展示上下文，失败降级为占位文案。证据：`CorpusAreaView.tsx`（`specimen-evidence-context`）。

**#6 三层计划不会推进——本轮最伤的日常摩擦。** 写完一章后作者得手工把细纲剪到部纲、部纲剪到大纲。新增 bridge `AdvanceChapterPlan`：细纲并入部纲尾部、部纲并入大纲尾部并清空细纲，一次轮转；空细纲上调用为无副作用幂等。聊天工具栏「完成本章」按钮直达，同时把徽章与提示文案统一为「细纲取『下一章』槽」，消除三层命名与槽位命名的错位。证据：`PlanningPayloads.cs` / `IPlanningService.cs` / `FileSystemPlanningService.AdvanceChapterPlanAsync` / `PlanningBridgeHandlers` / `BridgeCompatibilityAppMethods`（193→194）+ 集成测试 `AdvanceChapterPlanRotatesScopesAndIsIdempotent`（轮转、重复调用幂等、`plans/细纲.md` 镜像清空）+ `finish-chapter-button` 工作流断言。

**#7 注入语料没有表态通道。** 好不好用作者说不上，质量信号积累不起来。用量卡每条语料加好评/差评（`aria-pressed` + 已表态态），写入 `RecordReferenceUserFeedback`（`target_type=material`、`origin=chat_usage_card`），失败静默回退不打断写作流。工作流断言 `decision=accepted` 与 `target_type=material`。

顺带修掉一处**长期静默失败的存量测试**：`frontend/tests/reference-materialization-api.test.mjs` 仍断言 `api.ts` 绑定了 `GenerateReferenceMaterializationBlueprintPreview`，而该方法在 `a8593cd`（装配线退役）就已删除，且 `bridge-guardrails.mjs:151` 明确断言它不得复活。改为 `assert.doesNotMatch` 守住退役状态，并补上语料包导出/导入两条正向断言。它一直红着没被发现的原因是 `npm run verify` 不含这些独立 node 测试——已同时新增 `npm run test:node`（`node --test "tests/*.test.mjs"`，6 个文件 10 条断言，<1s）并接入 `verify`，堵住这条覆盖缺口。

同时把导入对话框「完成」按钮的工作流选择器全部改为 `exact: true`（4 个文件 13 处）。根因是 Playwright 的 `getByRole(name)` 默认子串匹配，新增的「完成本章」按钮让「完成」变成二义；`NovelImportDialog.tsx` 的可及名就是精确的「完成」，所以精确匹配是正解而非绕过。

### 本轮验证基线

- 后端：`dotnet test Novelist.slnx` 216/216 单测通过；集成 698/700，2 个失败（`WarmRetrievalAcrossOneThousandNodesStaysWithinControlledBudget`、`RealWorkerLoopReclaimsExpiredLeaseAndFencesLostWorkerCommit`）经 `--filter` 单独重跑 4 秒内全绿，确认为浏览器套件刚跑完时的机器争用超时抖动，非回归。
- 前端：`npm run build`（仅存量 >650kB chunk 警告）、`npm run lint` 0 error；`test:app --suite=full`、`test:phase15`（9 子套件）、`test:phase16`、`test:app:stress`、`test:app:usability` 全绿；独立 node 测试 chapter-range、reference-materialization-api（修复后）、time、choices、layout、diagnostics 全绿。

