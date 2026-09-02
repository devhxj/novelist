# 对话写作闭环健壮性评审与改进方案（2026-09-02）

> 评审对象：轻量化聚焦方案 Phase 1–3 落地后的现有实现（`lightweight-refocus-proposal-2026-08-31.md` 第三节主闭环、第四节资产、第八节三区收敛）。
> 覆盖范围：聊天语料注入、章节覆盖度信号、访谈选择题、语料区四轻视图、章节拆分自动分析、分析生产管线的崩溃恢复。
> 评审方法：代码走查（前后端 file:line 证据）+ 既有测试覆盖比对；未做运行时压测。行号以 158e1ec 为基线。
> 结论口径：P0 = 阻断或腐蚀核心闭环，建议立即修；P1 = 语义一致性/资源问题，建议本迭代修；P2 = 边界与卫生，择机修。

## 一、总体结论

骨架是稳的：分析管线的租约/两阶段提交/崩溃回收设计完整，材料化拆分有强断言测试（50K 样本锚定、自算 evidence、BOM/全角空格），注入双通道与覆盖度三态都有集成测试，浏览器工作流覆盖了横幅三态、章号传递、用量卡与选择题回路。风险集中在四条线：

1. **失败遏制缺失**——语料检索是聊天回合的前置依赖，检索失败会杀死整个回合且丢用户输入；覆盖度计算失败被前端静默吞掉。核心闭环把"锦上添花"的语料能力做成了"单点故障"。
2. **错误传播链断裂**——章节拆分的领域错误码在 bridge 层被吞成 `INTERNAL_ERROR`（前端专属文案成分支死代码）；新 handler 违反 JsonException 包装约定；前端 `interrupted` 状态不渲染错误信息也不给重试。
3. **性能模型与超时冲突**——覆盖度 = 最多 40 次"全量材料读取 + embedding 前向"的串行检索，撞前端默认 30s bridge 超时后表现为"横幅无声消失"。
4. **口径不一致**——覆盖度判"已覆盖"不校验锚点 Ready，注入却跳过非 Ready 材料（覆盖率虚高）；覆盖度逐 beat 检索与注入整纲一次检索不是同一查询，与方案"同一条检索通路"的承诺有偏差。

---

## 二、P0 问题（立即修）

### P0-1 语料注入失败导致整回合失败且丢用户输入

- **证据**：`FileSystemChatSessionService.cs:356-358`——`BuildChapterCorpusInjectionAsync`（内含计划读取、锚点读取、`SearchMaterialsAsync`）在 `_mutex.WaitAsync` 与任何持久化**之前**执行；任一抛错直接透传 bridge，user 消息不落库、无 `chat:started`、无错误事件（:710-737 的 catch 只覆盖流式阶段）。
- **前端放大**：`ChatPanel.tsx:893-898` 的 catch 把回合置 `interrupted`；:1148-1154 对 `interrupted` 只渲染固定文案"对话被中断"（errorMessage 不显示），且重试按钮只挂在 `failed` 状态（:1131-1147）——用户输入丢失且无重试入口。
- **修法**：后端把注入构建包进 try/catch，失败降级为"无注入继续聊"，并照常发一条工具事件标注"语料注入失败（原因）"；前端 `interrupted` 渲染 `errorMessage`（读 `BridgeError.message/code` 而非 `String(err)`）并提供与 `failed` 一致的重试按钮。
- **验证**：集成测试新增"SearchMaterialsAsync 抛错 → 回合仍成功、user 消息落库、corpus_usage=null、工具事件含失败标注"；`verifyCorpusChatWorkflow` 加一个注入失败场景断言错误横幅与重试按钮。

### P0-2 章节拆分错误码在 bridge 层断链

- **证据**：`ReferenceMaterializationBridgeHandlers.cs:17-30` 的 `Analyze/Preview/Confirm` 三个 handler 缺 `ReferenceMaterializationException → BridgeRequestException` 映射（同文件 `ExecuteStatusAsync` :97-112 有），异常落入 `BridgeDispatcher.cs:139-142` 兜底 → `INTERNAL_ERROR "Internal bridge error."`。前端为 `materialization_chapter_split_output_invalid` 专门写的分支（`ReferenceCorpusWorkspace.tsx:72-77`）因此是死代码，aefeff0 "surface invalid chapter split output" 的意图在 bridge 层断裂。
- **修法**：三个 handler 补齐与 `ExecuteStatusAsync` 相同的 try/catch 映射；顺带给 `ChapterCorpusBridgeHandlers`、调度器查询路径做同样的领域异常翻译巡检。
- **验证**：集成测试直接经 bridge 调 `AnalyzeReferenceChapterSplit` 喂无效输出，断言返回 `materialization_chapter_split_output_invalid` 而非 `INTERNAL_ERROR`。

### P0-3 覆盖度性能模型撞 30s 超时后信号无声消失

- **证据**：`ChapterCorpusCoverageService.cs:63-77` 每 beat 一次 `SearchMaterialsAsync`（Size=1）串行，最多 40 次；每次调用是 `SqliteReferenceAnchorService.cs:1538-1625` 的服务级互斥 + **全量读取该小说所有材料** + 一次 embedding 前向 + 内存打分。前端 `GetChapterCorpusCoverage` 走默认 30s 超时（`bridge.ts:1`）。大语料必然 `CANCELLED`；`ChatPanel.tsx:117` `catch(() => setChapterCoverage(null))` 静默吞掉——覆盖度是方案定义的一等信号，失败后既不提示也不可重试，且与"无细纲"（`total_count===0`，:1179）表现完全相同。
- **修法**：① 后端把逐 beat 检索改为一次批量：单个方法内取一次全量材料（复用互斥），逐 beat 在内存打分（embedding 只对 distinct query 做一次或按需缓存）；② 覆盖度结果按（细纲 hash × 材料版本）缓存，材料/复核变化才失效；③ 前端覆盖度加载加 loading/error 态，error 显示"覆盖度计算失败 + 重试"，bridge 调用带独立更长 `deadline_ms`。
- **验证**：单测断言 N beat 只触发一次材料读取与 ≤N 次 embedding（或文档化的上限）；`verifyCorpusChatWorkflow` 加覆盖度失败态断言。

---

## 三、P1 问题（本迭代修）

### P1-1 covered 口径与注入口径不一致（覆盖率虚高）

覆盖度命中不校验锚点 Ready（`ChapterCorpusCoverageService.cs:66` 只看 `hit is not null`；标题映射才过滤 Ready :58-59），而注入会跳过非 Ready 锚点的材料（`FileSystemChatSessionService.cs:2042-2045`）。结果：判定"已覆盖"的 beat，写作时实际注入不了。修法：覆盖度检索同样过滤非 Ready 锚点；补一个"命中材料来自非 Ready 书"的测试。

### P1-2 覆盖度与注入不是同一检索

覆盖度逐 beat（query 截 48 字符，`ChapterCorpusCoverageService.cs:93-97`），注入是整纲前 160 字符一次 top-5（`FileSystemChatSessionService.cs:2018-2036`）。`sufficient=true` 不代表注入的 5 条语料真覆盖各 beat，与方案第三节"覆盖率只是聚合呈现、同一条通路"的口径有偏差。修法（按成本递增选）：至少在两处文档化差异；更好是注入改为按未覆盖 beat 分组补检；P0-3 的批量改造落地后两者天然同源。

### P1-3 `<reference-corpus>` 注入消息按回合无限累积

注入系统消息 `toApi:true` 持久化且每带章号回合新增一条（`FileSystemChatSessionService.cs:430-453`），历史重放全部继续发给模型——长会话 token 单调膨胀。修法：写入新注入时把会话内旧的 `<reference-corpus>` 消息置 inactive（保留审计），API 上下文只带最近一条（或最近 N 条）。

### P1-4 注入工具事件冒充 `search_reference_materials`

注入以 `tool_name=search_reference_materials`、`automatic:true` 的伪装工具事件发出（`FileSystemChatSessionService.cs:493-528`），前端靠 `result.automatic` 分流（`ChatPanel.tsx:1068-1071`）。这会污染真实工具调用的审计与统计口径。修法：改用独立事件类型/工具名（如 `corpus_injection`），前端分流同步调整；顺带解决 corpus_usage 历史不可重放的遗留（把用量持久化到消息元数据，重放时恢复 `CorpusUsageCard`——proposal 已列为 Phase 3 遗留）。

### P1-5 前端覆盖度/浏览列表竞态

`ChatPanel.tsx:115-117`（覆盖度）与 `CorpusAreaView.tsx:227-256`（浏览分页）均无请求序列化/取消：快速切换章节或参考书时，慢的旧请求后到会覆盖新数据（stale response 胜出）。修法：seq guard（ref 递增计数，resolve 时比对才 setState）或 AbortController；两处同修。

### P1-6 `ChapterCorpusBridgeHandlers.ReadObjectArg` 漏包 JsonException

`ChapterCorpusBridgeHandlers.cs:25-35` 的 `Deserialize<T>` 无 try/catch（`?? throw` 只接住 null 反序列化结果）；`novel_id: "abc"` 这类字段类型错误抛 JsonException → `INTERNAL_ERROR`，违反 bridge 约定（同类 handler 均有包装，如 `ChatSessionBridgeHandlers.cs:61-69`、`ReferenceMaterializationBridgeHandlers.cs:356-364`）。修法：照约定补 try/catch (JsonException) → `BridgeValidationException`。

### P1-7 覆盖度信号不随语料状态变化刷新

横幅只在章节号变化与聊完一轮后刷新（`ChatPanel.tsx:120-129`），后台分析完成/复核确认不会触发更新——作者刚补完语料，横幅仍显示旧值。修法：订阅分析完成事件（或统一的 reference 变更通知，`referenceRefreshKey` 已有同类机制）触发 `loadCoverage`。

---

## 四、P2 问题（择机修）

| # | 问题 | 证据 | 修法要点 |
|---|---|---|---|
| P2-1 | 覆盖度 beat 边界：>40 beat 静默截断无标记；单行超长 beat 无界进 payload；`hit.Text` 全文当 preview | `ChapterCorpusCoverageService.cs:122-135`、:66 | 截断时在 payload 标注 `truncated=true`；beat 与 preview 限长 |
| P2-2 | 新 payload 未过 `ReferencePayloadSanitizer` 且无长度上界（与全库 reference 出站约定不一致） | `ChapterCorpusPayloads.cs`、`ChatPayloads.cs`（corpus_usage） | 出站统一过 sanitizer/限长 |
| P2-3 | choices 解析：仅匹配首个块（无 `/g`）；JSON 解析失败时块被移除但选项消失；流式未闭合块以代码块闪现；options 无去重（`key={option}` 撞 key）；`slice(0,6)` 静默截断 | `choices.ts:6-27`、`ChoiceBlock.tsx:17` | 解析失败回退原文渲染；去重；流式期间隐藏未闭合 choices 块；多块合并 |
| P2-4 | `handleSend` 首条消息 `sessionId=''` 时仍调 `CancelChat('')`；错误用 `String(err)`（带 `Error:` 前缀） | `ChatPanel.tsx:836-838`、:897 | 空sessionId 跳过；读 `err.message/code` |
| P2-5 | 总览 2N+1 调用放大：每锚点两次 List 取 total，20 本书 41 次往返，任一失败整盘失败；加载中卡片显示 0 易误读 | `CorpusAreaView.tsx:91-108`、:129-134 | 后端按 novel 聚合端点；加载骨架态 |
| P2-6 | 分析管线：崩溃 reclaim 把 reserved tokens 全额计费（崩溃风暴无声耗尽预算）；`InputJson` 损坏让查询路径抛 INTERNAL_ERROR；入队无 InputHash 幂等；technique 依赖无失效传播 | `Leases.cs:349`、scheduler `:216-224`、入队路径 | 计费区分 lease_expired 与实际消耗；解析失败给 `analysis_snapshot_corrupt` 级错误；入队幂等键 |
| P2-7 | 8f51a35 放宽证据校验后，幻觉模板只需样本内 ≥1 标题命中即通过；全文边界重建失败无诊断 | `SqliteReferenceMaterializationService.cs:705-744` | 多证据点校验（分散取样）；失败时报告首个失配位置 |
| P2-8 | types.ts 退役 DTO 镜像（已 grep 确认无引用）：`AdaptMaterialInput`、`AuditReuseInput`、`StyleSample*`、`NarrativePattern*`、`CorpusGovernance`、`BlueprintRevision*` 等 | `types.ts:537-706`、`types.ts:847-869`、`types.ts:2671-2923` | 直接删除（proposal Phase 3 遗留项收口） |
| P2-9 | 浏览维度枚举硬编码（observations 10 / specimens 5），方案第四节新增 `scene`/`trope` 时必改 | `CorpusAreaView.tsx:314-317` | 改为契约派生或数据驱动 |

---

## 五、修复排期建议

1. **第一批（P0，先行合入）**：P0-2（纯 bridge 映射，最小改动）→ P0-1（降级 + 前端错误呈现）→ P0-3（批量覆盖度 + 缓存 + 前端三态）。P0-1/P0-2 各配集成测试，P0-3 配性能断言与 `verifyCorpusChatWorkflow` 失败态。
2. **第二批（P1，随下一迭代）**：P1-1/P1-2 随 P0-3 的批量改造一并同源化；P1-3/P1-4 做注入消息生命周期与事件正名（顺带收掉 corpus_usage 重放遗留）；P1-5/P1-6/P1-7 为小补丁。
3. **第三批（P2）**：types.ts 清理可随时做（纯删除）；其余按触达顺路修。

## 六、测试补强清单（新增用例）

- 注入：检索抛错降级；多回合注入消息不累积（P1-3）；非 Ready 锚点过滤口径一致（P1-1）。
- 覆盖度：检索抛错的部分失败语义（建议：单 beat 失败计未覆盖并标注，而非整盘失败）；>40 beat 截断标记；空 query beat。
- bridge：`AnalyzeReferenceChapterSplit` 领域错误码；`GetChapterCorpusCoverage` 字段类型错误 → `VALIDATION_ERROR`。
- choices：坏 JSON 回退、多块、重复项去重（前端单测即可，`parseChoices` 是纯函数）。
- 浏览器工作流：覆盖度失败态、注入失败态的横幅与重试（`verifyCorpusChatWorkflow` 扩展）。

## 七、与既有遗留项的对齐

本评审不改变 `lightweight-refocus-proposal-2026-08-31.md` 的遗留清单，只追加实施约束：语料包 JSONL 通道（Phase 3 遗留）落地时需吸收 P2-2（出站限长/脱敏约定）；corpus_usage 历史重放（同上）与 P1-4 合并处理；覆盖度阈值校准（开放问题 3）应在 P0-3 的批量+缓存改造之后做，避免在校准前先把性能模型跑挂。
