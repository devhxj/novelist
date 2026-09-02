# 健壮性评审修复复评（2026-09-02）

> 对 `robustness-review-2026-09-02.md` 所列 19 项问题的修复复评。逐项给出处置结果与验证证据，行号以修复后工作区为准。
> 验证基线：`dotnet test Novelist.slnx` 216/216 单测 + 694/694 集成全绿；前端 `npm run build`、`lint`、`test:choices`（新增 5 用例）、`verify`（build+lint+phase16+smoke）、`test:app:full`、`test:app:stress` 全绿。

## 一、逐项对账

### P0（3/3 已修复）

**P0-1 注入失败导致整回合失败且丢用户输入 — 已修复。**
后端：注入构建包进 try/catch，检索抛错降级为"无语料模式继续"，回合照常完成、用户消息照常落库（`FileSystemChatSessionService.cs` ChatAsync）；失败经 `corpus_injection` 工具事件（phase=failed）与持久化的 `corpus_usage` 事件消息双通道呈现。前端：`interrupted` 状态渲染 `errorMessage`（经 `describeError` 提取 BridgeError.message）并提供与 `failed` 一致的重试按钮（`ChatPanel.tsx`）。
证据：集成测试 `ChatInjectionFailureDegradesTurnAndKeepsUserMessage`（回合成功、user 消息落库、corpus_usage=null、failed 事件含原因、事件消息含 error）；浏览器工作流新增"注入失败：验证降级"场景断言失败卡片可见。

**P0-2 章节拆分错误码 bridge 层断链 — 已修复。**
`ReferenceMaterializationBridgeHandlers.cs` 新增 `ExecuteProfileAsync`，Analyze/Preview/Confirm 三个 handler 的 `ReferenceMaterializationException` 统一映射为 `BridgeRequestException(error_code, retryable: true)`。
证据：测试 `ChapterSplitHandlersReturnTheStableMaterializationErrorCode` 断言三种方法均返回 `materialization_chapter_split_output_invalid` 而非 `INTERNAL_ERROR`；前端 `chapterSplitErrorMessage` 分支不再是死代码。

**P0-3 覆盖度性能撞 30s 超时且失败无声 — 已修复。**
三层处置：① 检索批量化——`IReferenceAnchorService` 新增 `SearchMaterialsBatchAsync`，`SearchMaterialsAsync` 重构为单元素批量委托（同一实现、既有全量 SearchMaterials 测试继续覆盖）；同 (小说, ReadyOnly, 归档过滤, 风格选项) 分组共享一次互斥 + 一次材料全量读取 + 一次上下文读取，逐查询独立打分，N 个 beat 从 N 次全表扫描降为 1 次。② 覆盖度结果按（novelId × 细纲内容）做 10 秒 TTL 缓存，`refresh: true` 可穿透。③ 前端覆盖度改三态（loading/error/ready），失败显示"覆盖度计算失败，语料信号暂不可用 + 重试"，bridge 调用超时从默认 30s 提至 90s。
证据：测试 `ComputeCoverageSearchesAllBeatsInOneBatchAndMarksTruncation` 断言 40 beat 恰好 1 次批量调用；工作流覆盖失败态与重试回路。

### P1（7/7 已修复）

**P1-1 covered 未过滤非 Ready 锚点 — 已修复。** 覆盖度与注入检索统一传 `ReadyOnly: true`，与注入的 Ready 过滤同口径。证据：`ComputeCoverageIgnoresMaterialsFromNonReadyAnchors`（building 锚点命中不计覆盖）。

**P1-2 覆盖度与注入不同检索 — 已修复（同源化）。** 两者现在共享同一 `SearchMaterials(Batch)Async` 实现与同一 ReadyOnly 口径；注入为整纲一次 top-5、覆盖度为逐 beat 命中判定，查询粒度差异保留（这是两种信号的本义），已在 `ChapterCorpusCoverageService` 注释中文档化。

**P1-3 注入消息按回合累积 — 已修复。** 写入新 `<reference-corpus>` 注入时，同会话旧注入消息置 `ToApi=false`（留档、退出 API 上下文）。证据：`ChatInjectionReplacesPreviousCorpusContextWithinSession`（第二回合请求仅一条注入消息）。

**P1-4 注入事件冒名 + 用量不可重放 — 已修复。** 事件正名为 `corpus_injection`（后端、前端路由、mock bridge 同步）；用量持久化为 `corpus_usage` 前端事件消息（extra_metadata 携带 materials/display_text/succeeded/error），`rebuildTurns` 重放为工具卡——成功渲染用量卡、失败渲染错误卡，损坏元数据按失败占位不阻断历史加载。证据：注入测试的持久化断言 + 浏览器全绿。

**P1-5 前端竞态 — 已修复。** ChatPanel 覆盖度与 CorpusAreaView 浏览列表均加 seq guard，过期响应丢弃。

**P1-6 ReadObjectArg 漏包 JsonException — 已修复。** 补 try/catch → `BridgeValidationException`。证据：`CoverageHandlerMapsMalformedFieldTypesToValidationErrors`（`novel_id: "abc"` → VALIDATION_ERROR）。

**P1-7 覆盖度不随语料变化刷新 — 已修复。** WorkspaceView 将 `referenceRefreshKey` 传入 ChatPanel，key 递增（语料区材料化/复核变更）即 `refresh: true` 强制重算。后台 job 完成事件暂缺（全库无此事件基建），以回合后刷新 + 手动刷新兜底；已列入残余事项。

### P2（9/9 已处置）

**P2-1 beat/preview 限长与截断标记 — 已修复。** beat/preview/title 分别限 200/200/200 字符；payload 新增 `truncated` 标记，前端横幅提示"仅统计前 40 个 beat"。证据：批量化测试断言 `truncated=true`。

**P2-2 新 payload 限长纪律 — 已修复。** 覆盖度与注入出站统一 `Bound`/截断；注入用量卡 preview 沿用 160、书名新增 200 上限。未接 `ReferencePayloadSanitizer` 正则脱敏——payload 只含小说正文本与书名，不含路径/密钥面，限长已闭合主要风险。

**P2-3 choices 解析 — 已修复。** 重写 `parseChoices`：多块合并、选项去重、上限 6、JSON 解析失败原样保留文本、流式未闭合尾部块整块隐藏（精确判定最后一个 choices 块后无闭合围栏）。证据：新增 `tests/choices.test.mjs` 5 用例（`test:choices`），坏 JSON 保留正文、未闭合隐藏、合并去重截断、无块直通。

**P2-4 handleSend 卫生 — 已修复。** 空 sessionId 不再调 `CancelChat('')`；错误统一 `describeError`（去 `Error:` 前缀，读 message/code）。

**P2-5 总览 N+1 — 已修复。** 新增 `GetReferenceCorpusAssetTotals` bridge 方法（`SqliteReferenceCorpusAnalysisService.GetAssetTotalsAsync` 两条 COUNT），总览从 2N+1 调用降为 2 次；加载中卡片显示"—"骨架而非误导性的 0。全链路接线：契约、接口、实现、handler、兼容白名单（189→190）、注册测试、api.ts/types.ts、mock bridge、guardrails 必达清单。证据：注册一致性测试全绿、guardrails 通过（工作流进入总览即调用）。

**P2-6 管线韧性 — 已修复（三项）。** ① 查询路径容错：scheduler `ReadScope` 捕获 `JsonException` 返回空 scope，损坏 InputJson 不再使列表/依赖校验抛 `INTERNAL_ERROR`。② 入队幂等：store 新增 `GetAsyncByRunAsync`，同 run_id 重复入队返回既有任务（原先靠 UNIQUE 约束抛未翻译的冲突异常）。③ reclaim 计费：保守计费语义保留（崩溃时 provider 调用可能已发生，退款会导致预算超支——这是预算安全的不变量），但错误消息显式写明"预留 token 已保守计费"，消除无声性。工作器侧既有 `analysis_snapshot_corrupt` 处理（`ReferenceCorpusAnalysisWorker.cs:263-268`）复核确认无需改动。

**P2-7 拆分证据多点校验 + 失败定位 — 已修复。** `ValidateModelEvidence`：模板须命中 ≥2 个标题，唯一例外是唯一标题锚定样本起点（首章超长书的合法形态）；证据点改为首/中/末分散取样（≤3 个全取）。`AnalyzeChapterSplitAsync` 边界重建失败透传内层原因（少于两个边界/空章）。证据：新增 `AnalyzeAutoSplitRejectsTemplatesMatchingOnlyOneMidSampleHeading`（单命中拒绝）与 `AnalyzeAutoSplitAcceptsSingleHeadingAnchoredAtSampleStart`（55K 超长首章 + 样本外第二章 → 成功切两章），12/12 拆分测试全绿。

**P2-8 types.ts 退役 DTO — 已修复。** 死类型 GC（引用不动点 + 逐类型确认）删除 83 个拼装线退役 DTO（蓝图/编排/适配/审计/插入草稿全图），净删 797 行；`BridgeFrontendContractTests` 中锚定退役 DTO 的两条断言（emotion_arc/orchestration_stages）同步移除。tsc + 全量浏览器套件验证无回归。风格素材/叙事模式/治理类型经核实后端服务保留（方案 §五"技能/风格/模式服务保留"），对应绑定与类型**有意保留**。

**P2-9 浏览维度枚举 — 已处置（共享常量方案）。** 抽取至 `frontend/src/lib/novelist/corpusFamilies.ts` 单一来源，并注释须与后端 family 词表及 scene/trope 扩展同步。与原评审"契约派生或数据驱动"相比采用较轻方案：现有 facet 接口是材料标签维度，与浏览的 family 维度不同源，契约派生需新增端点，收益不抵面——已在此记录偏差。

## 二、评审修复中新增的验证资产

- 后端新增 9 个测试：拆分错误码透传 ×1、覆盖度 handler ×3、注入失败降级/不累积 ×2、覆盖度批量+截断/非 Ready ×2、拆分幻觉防护/超长首章 ×2（合计含既有改动断言增强）。
- 前端新增 `test:choices` 5 用例；浏览器工作流新增覆盖度失败态（错误呈现 + 重试）、注入失败降级卡两个场景。
- 694/694 集成测试含此前存量 Windows 换行失败项，本次基线已归零。

## 三、残余事项（新账，不阻塞）

1. 后台分析 job 完成无事件基建（P1-7 的完整性缺口）：覆盖度在"后台 job 完成但作者未触碰语料区/未发消息"场景下仍靠手动刷新。若后续做语料区实时性，建议加 `reference:changed` 全局事件统一驱动。
2. `SearchMaterialsBatchAsync` 的 embedding 计算仍为每查询一次（未做批量 embedding 协议）——批量化的收益来自共享读取/互斥/连接，40 beat × embedding 的场景在配置 embedding 后仍可能耗时数秒，已用 90s deadline + TTL 缓存兜底；后续可按需引入 embedding 批量接口。
3. `verifyCorpusChatWorkflow` 未在浏览器层覆盖"历史重放用量卡"（重放逻辑由 rebuildTurns 分支 + 后端持久化断言分层覆盖）；如需端到端可在工作流中加重载会话步骤。

## 四、结论

评审所列 4 条风险线全部收敛：语料能力不再是聊天/拆分链路的单点故障（失败降级 + 错误可见 + 可重试）；错误传播链修复（领域错误码到达前端、JsonException 归一、interrupted 呈现原因）；覆盖度性能模型从 O(N) 全表扫描降为 O(1) 次读取并在超时前留有裕量；覆盖与注入口径同源（ReadyOnly + 同一检索实现）。19 项全部处置完毕，无一项以"未修"关闭；两项以轻量方案落地并记录偏差（P2-9 共享常量、P2-6③ 保守计费显式化）。
