# 语料驱动写作系统 — 分阶段任务拆解（v2）

> 本文档按 [development-plan.md](./development-plan.md) 第九节的实现顺序展开为可执行任务清单。v2 重排为**纵向薄切片优先**（修复评审 #10）：先修地基 schema，再立即跑通端到端闭环，然后逐层加深。
>
> 里程碑：`M0 地基` → `M1 纵向薄切片` → `M2 加深分析` → `M3 加深检索` → `M4 加深蓝图` → `M5 加深拼装` → `M6 语料库产品化` → `M7 聚合知识` → `M8 复核工作流` → `M9 默认体验+专家UI`。
>
> 每个任务标注层（DB/后端/前端/测试）、依赖、验收点。验收全部对固定 fixture + golden JSON（修复 #12）。

**产品不变量：** 素材库处理侧（导入、授权、分析、复核）与章节使用侧（按当前大纲/目标检索、蓝图迭代、正文候选、插入闸门）必须分离；`library/session binding` 表示跨库启用，一个 session 可绑定多个 library、每个 library 可包含多本小说/多个 anchor。章节侧必须按当前大纲/目标跨所有启用库召回，生成多份显著不同的蓝图并允许反馈循环，直到用户选定蓝图；正文候选只能由该选定蓝图及其来源证据派生，最大化保留原句、结构与节奏，只允许受审计的槽位替换、顺序调整、过渡和必要微调。AI 的职责是分析、检索、编排和最小适配，不得脱离已选语料与蓝图自由补写正文。M1 起闭环验收就必须覆盖以上路径，M3+ 不得把实现或测试简化为单 anchor、单蓝图或自由生成路径。

## M0-M9 状态治理快照

### 状态口径

任务复选框保留原义：`[x]` 表示该原子任务已有某种实现和对应定向证据，`[ ]` 表示仍未完成。`[x]` 不表示该能力已达到默认产品路径、生产可靠性或规模验收。里程碑成熟度统一使用 `N/S/P/L`，不能由勾选比例直接推导：

| 代码 | 状态 | 判定口径 |
|---|---|---|
| `N` | **未开始** | 无可执行纵向路径，或只有设计/契约占位 |
| `S` | **薄切片完成** | 受控 fixture/fake model/有限路径已证明关键契约或算法，但生产运行、默认用户闭环或规模效果仍缺失 |
| `P` | **产品闭环完成** | 默认用户路径、关键失败路径、恢复、反馈和用户可见状态闭合，可持续完成真实工作流 |
| `L` | **规模化完成** | 在目标数据量下通过质量、性能、成本、恢复和持续运行预算，并有真实效果证据 |

以下数量只统计行首为 `- [x]` / `- [ ]` 的真实原子任务，并于 2026-07-10 审计后重新同步。截图复核新增素材库工作台验收项后，M0-M9 共 207 项，已完成 205 项、未完成 2 项；文末跨里程碑约束是 7 条编号规则，不计入复选框。后续更新复选框时必须同时更新本表和证据边界，不得把文内示例标记或勾选比例解释为产品完成度。

| 里程碑 | 已完成 | 未完成 | 当前状态 | 当前判断 |
|---|---:|---:|---|---|
| M0 地基 | 36 | 0 | **S** | schema、契约、migration、projection、fixture 与恢复骨架已有定向证据；P0 仓库卫生和全套验证已恢复，但不以交付基线代替产品闭环或规模化证据 |
| M1 纵向闭环 | 39 | 0 | **S** | 跨库检索、多蓝图反馈、选定蓝图派生正文候选和章节面板已形成受控闭环；明确只算薄切片完成，不升级为 P |
| M2 深度分析 | 45 | 0 | **S，标准轨已关闭** | 10 family、Task A/B/Stage 3 持久后台执行、aging/retry/progress/recovery、ReviewQueue 和分析 UI 已有定向验证；真实控制/失租墙钟时限和 50K 全管线正式门禁均已通过，但默认用户闭环、真实 provider 与持续运行证据仍不足 |
| M3 深度检索 | 18 | 0 | **S，加深中** | 四路召回、独立 topK、native maintenance 与受控预算已有自动验证；未证明真实长篇召回质量、默认后台维护与生产 SLA |
| M4 蓝图 | 20 | 0 | **S，加深中** | 多策略、coverage/gap、反馈迭代和持久 coordinator 已实现；默认章节 UI 已消费持久 session，但尚无蓝图区分度效果证据 |
| M5 拼装 | 19 | 0 | **S，加深中** | 来源锁定、保真/阻断审计、多候选与受控过渡已有实现；自然度、剧情适配率和真实修改成本证据不足 |
| M6 语料库产品化 | 5 | 0 | **S，冻结扩张** | 已有库作用域、授权、去重和审计的有限产品面；勾选项不证明真实多库治理、语义去重和完整授权工作流达到 P |
| M7 聚合知识 | 8 | 0 | **S，冻结扩张** | 已有聚合与溯源薄实现；底层分析质量、来源过滤、事务一致性及 M3-M5 消费闭环不足，不能视为可靠写作知识 |
| M8 复核工作流 | 7 | 0 | **S，冻结扩张** | 已有状态、队列和前端操作薄切片；复核上下文、原文证据导航、批量原子性和生产重跑语义仍不足 |
| M9 默认体验 + 专家 UI | 8 | 2 | **S，章节自动路径已收口** | 章节默认路径、服务端恢复、长任务/错误恢复和视口/键盘自动验证已完成；素材库工作台仍需完成针对性浏览器验收，另缺真实目标用户无指导走查，不能据此宣称易用好用或升级为 P |

**状态解释：** M0-M9 当前统一为 `S`，没有任何里程碑达到 `P` 或 `L`。M1 是当前已成立的产品纵向薄切片，不等于真实用户产品闭环完成；M2-M5 处于加深中；M6-M8 冻结新增功能，只修阻断性缺陷、安全问题和与主线兼容相关的回归；M9 只允许收口默认主流程、长任务与恢复体验、可访问性和一致视觉层级，不扩张新的专家功能。提交信息中的“M2 核心功能完成”只能解释为 M2 演示级 runner/入口成立，不能解释为生产后台或规模验收完成。

**整体对外口径：** “M1 产品薄切片完成，M2-M5 加深中，M6-M8 冻结扩张，M9 聚焦默认体验收口；系统尚未达到生产完成或规模化完成。”任务勾选数量只能描述实现覆盖，不能换算为产品完成百分比、写作效果或易用性结论。

### 当前证据与升级缺口

| 里程碑 | 已有验收证据 | 升级仍缺 |
|---|---|---|
| **M0** | text node/observation/specimen/library/license/page 契约；migration、projection 重建、幂等、500 句 golden、参数化规模 fixture 与恢复 harness；已清理 harness 生成物，完整 .NET 与 frontend verify 已通过 | 交付基线已恢复；后续只保留与 M2-M5 主线兼容的回归，不以 M0 勾选替代产品闭环证据 |
| **M1** | 多小说进入启用库；跨库检索；多蓝图和 feedback 二轮；selected blueprint 正文候选；golden JSON；章节面板 mock workflow | 真实桌面失败/恢复回归，以及 M2-M5 深化后的兼容回归；规模与效果证据归后续里程碑，不扩大 M1 状态掩盖 |
| **M2** | canonical run/job/attempt/work item、冻结输入、aging/retry/Retry-After、CAS、lease/fenced commit、进度/控制 UI、强杀恢复和 Task A/B/Stage 3 后台执行；30 组真实 pause/cancel/stale-lease 时限与失租 fenced commit 已通过；Release 50K 全管线正式轨完成 13,385 work items，吞吐 29.16/s，claim/list/progress P95 为 10.12/27.04/3.51 ms，零重复、零预算穿透、零活动 lease | 同步兼容入口的退役条件；真实 provider 的成本/波动与持续运行证据；默认用户流程和恢复体验归 M9，不因后台门禁通过而升级为 P |
| **M3** | 文本/技法/结构化 observation/章节上下文四路召回；独立 topK；结构化过滤；local fit；native backfill；M3 retrieval golden；安全负例 | 默认产品后台维护；人工标注 query 集；Recall@K/nDCG/命中原因准确率；融合权重标定；50K 跨库 P50/P95 与真实长篇质量报告 |
| **M4** | emotion/rhythm/technique/scene profiles；coverage/gap positions；来源覆盖；历史反馈降权；候选持久化；generate/select/revise/accept coordinator；章节默认 UI 服务端恢复与直接集成测试 | 蓝图区分度、重复方案率、反馈后改善率和同目标多方案盲评 |
| **M5** | selected blueprint 来源锁定；preserved/locked spans；slot/transition audit；blocked next_action 回蓝图；slot-only/部分 transfer slot 多稿薄切片 | 完整多草稿与过渡策略；自然语言 slot constraints；自然拼装评测；保真/适配/自然度平衡；用户修改字符比例；正文盲评和真实章节样本 |
| **M6** | 库作用域、来源状态、授权闸门、有限去重和插入审计已有定向路径 | 真实多库管理回归、跨版本/语义去重、授权变更全链传播、禁用来源工作流和大规模治理体验；冻结扩张期间只修缺陷 |
| **M7** | 聚合表、基础聚合、provenance 与 stale 传播已有薄实现 | 来源资格过滤、单事务构建、丰富结构化知识、质量评测，以及被 M3-M5 实际消费并证明改善；冻结扩张期间不新增聚合类型 |
| **M8** | review/validity 状态、队列、重跑保留与基础前端操作已有薄切片 | 原文预览、冲突值与 evidence、source/run 导航、批量原子性、缺失项错误语义、生产重跑与恢复；冻结扩张期间只修缺陷 |
| **M9** | 章节自动模式已收敛为“目标 → 蓝图 → 正文 → 插入”；服务端蓝图 session 恢复、失败后复用原 `request_id` 重试、后台任务唯一恢复动作、键盘焦点进出、ARIA 状态和 1280x720/1440x900/125%/150% 视口 workflow 均已自动验证；`frontend verify` 通过 | 素材库左侧参考书籍管理和右侧蓝图预演的针对性浏览器验收；5 名目标用户中至少 4 名无指导完成主流程的走查，以及完成时间、回退次数、失败点和主观难度记录 |

### 强制实施顺序

1. **P0：交付基线已恢复。** 已清理误入版本控制的 harness `bin/obj/PDB` 生成产物，ONNX session 测试具备显式释放与非并行边界，corpus workflow 改为任务行级定位；完整 .NET 与前端 build/lint/workflow 已通过。后续变更必须保持该绿色基线。
2. **P1：M2 后台标准轨已关闭。** Stage 3 复用 canonical job/work-item 协议；真实 worker loop 的 pause/cancel 与 stale lease 定量时限已通过；Release 50K scheduler/builder/worker/fake-analyzer 正式轨也已通过。200 万字只保留为显式参数触发的非阻塞长跑，不再作为日常开发或 M2 收口门槛。
3. **P2：建立效果与体验证据资产。** 保留 500 句 synthetic golden 做正确性回归，新增 50-100 条人工标注 query、20-30 组同目标多蓝图和 20-30 个真实章节插入样本，固定 Recall@K/nDCG、蓝图区分度、重复率、原句保真率、剧情适配率、过渡自然度、用户修改字符比例与迭代次数；5 条核心任务的浏览器 workflow、桌面视口/键盘/恢复检查已完成，下一步先验收素材库工作台的参考书管理与蓝图预演，再补真实目标用户走查记录。
4. **P3：补 M3 规模与真实效果。** 基于人工标注 query 集验证多路召回、融合排序、当前章节 local fit、去重和授权过滤；禁止以“route marker 出现”代替检索质量证明。
5. **P4：以效果和真实任务验证 M4/M5 默认写作路径。** 默认章节界面已收敛为“写目标 → 选蓝图 → 选正文 → 明确插入”一条主路径，服务端 session、错误恢复和专家渐进展开已有自动化证据；素材库工作台已形成“参考书籍管理 → 六维语料检索 → 蓝图预演”结构，下一步先完成其专用浏览器 workflow 和窄桌面截图验收，再证明多份蓝图的结构/来源差异、正文保真/适配/自然度和真实用户完成率。**停止继续堆 M5 审计规则和专家控件**；只有复现的安全漏洞、授权绕过、效果评测失败或核心任务可用性问题可以新增，并先落失败 fixture/workflow。

**顺序约束：** P0 绿色基线、Stage 3 canonical job、M2 定量恢复时限和 50K 标准规模验收均已通过。50K 使用 fake LLM、至少 2 anchors/2 libraries，已验证完整输出、零重复、零预算穿透、吞吐、P95 与零活动 lease；200 万字长跑仅在发布前、性能诊断或明确要声明百万字能力时手工触发，不阻塞 P0-P4。下一步并行建立真实效果集和核心用户走查，再依次加深 M3 检索、M4 蓝图区分和 M5 保真拼装。M3 未有规模效果证据前，不把 M4/M5 的启发式变化写成质量提升；M4 未证明蓝图区分度前，不以更多正文候选冒充有效多稿；M5 未完成真实效果验收前，不宣称高质量正文已可用；M9 的恢复、视口和可访问性自动检查已通过，但未取得真实用户任务证据前仍不宣称“易用好用”。M6-M8 保持 `S` 并冻结功能扩张。

---

## M0：地基 schema（防返工，最先做）

**目标：** 一次性把会导致后续返工的基础模型建对。此里程碑不实现业务逻辑，只落数据模型 + 契约 + 测试资产骨架。

### M0.1 文本节点树（DB / 修复 #1）

- [x] `reference_text_nodes` 建表 + 三个索引（parent/atype/chapter）
- [x] `reference_materials` / `reference_source_segments` 加 `node_id` FK
- [x] Stage 0 结构化写入器（M1 最小版）：真实导入 → text_nodes（章/场/段/句），填 offset/text_hash/sequence，并回填 source_segments/materials.node_id
- [x] 从句级切分：真实导入为复句生成稳定 clause nodes，写入 sentence parent_node_id、全局 sequence、源文 offset/text_hash；重复 rebuild 保持 clause node id 幂等
- [x] 章节窗口查询辅助：`GetReferenceCorpusNodeWindow` 给定 node 返回前后 N 章节点；scene 不在 sentence 祖先链时按同章 offset 包含关系回退到最窄 scene，并返回其直接子节点，支持 max_nodes 截断
- [x] migration 幂等 + 存量库升级测试：schema ensure 为旧 segment/material 补 node_id，从现有 segment 父子关系按深度重建 deterministic text nodes；重复升级不改变 node id、文本、链接或节点数量

**验收：** 导入 golden 书后，节点树父子/顺序/offset 正确；任一句节点能反查其所属段/场/章；text_hash 与源文逐字一致。

### M0.2 分层特征观察 + projection（DB / 修复 #2 #7）

- [x] `reference_feature_observations` 建表：`value_kind`/`value_num`/`value_bool`/`value_json` + `review_state`/`validity_state`/`superseded_by_run_id`
- [x] 三索引（family / num / node）
- [x] **幂等写入 schema guard（护栏 G1）**：`ux_obs_generation_key` UNIQUE，重复 observation 生成键由数据库拒绝
- [x] **确定性 observation identity（护栏 G1）**：`observation_id = hash(run_id,node_id,feature_family,feature_key,evidence_start,evidence_end)`，空 evidence 与 DB sentinel 对齐
- [x] **幂等 upsert 写入器（护栏 G1）**：分析 observation 写入用 `INSERT ... ON CONFLICT`；并发/重试/续跑不重复写
- [x] 热路径 projection 表：`reference_obs_sensory`（示范）+ Stage 1 写入同步
- [x] projection 重建脚本：`RebuildSensoryProjectionAsync` 可按 anchor 或全库删除并从 active、未 superseded sensory observations 重建热路径行；非法 JSON 单独计数，重复重建结果一致
- [x] `reference_analysis_runs` 建表（含 token_budget/tokens_spent）
- [x] **runner 兼容预算状态（护栏 G2）**：现有同步 run 支持 `budget_exhausted + resume_cursor`，保证预算耗尽非 failed；`paused/partial_completed` 仅属旧 schema 兼容，不作为生产后台目标状态
- [x] **runner 级预算续跑接入（护栏 G2）**：同步 Stage 2/3 runner 从稳定 cursor 后继续；产物、token 与 cursor 在成功提交边界推进，非法 terminal resume/陈旧 cursor/跨 scope run id 在写入前拒绝
- [x] **后台 job store 与事务围栏薄切片（护栏 G2）**：持久化 canonical run/input snapshot/work item/job/attempt；CAS pause/resume/cancel/reprioritize；lease/heartbeat/reclaim；token reservation；retry/budget/control settlement；产物、work item、job、attempt、run 同事务 fenced commit
- [x] **后台 job 级生产续跑接入（护栏 G2）**：scheduler 持久化完整 frozen feature/technique snapshot，重启后可读取同一 payload；worker 启动及周期 pump 执行 reconcile，只消费 work-item frozen payload，支持稳定 reservation、幂等 lifecycle、损坏 snapshot fenced fail 和 shutdown abandon 后续跑；本项仅审计现有 scheduler/worker，不修改 M2 job store

**验收：** 能按 value_num 范围、按 sensory 投影表数组查询；review_state 与 validity_state 独立可设；**同 (run,node,feature) 并发/重试只落一条 active observation**；预算耗尽置 `budget_exhausted` 而非 failed，补预算可从 resume_cursor 续跑。

### M0.3 技法标本 + junction（DB / 修复 #8）

- [x] `reference_technique_specimens` 建表（含 review/validity/superseded）
- [x] junction：`reference_specimen_evidence` / `reference_template_examples` / `reference_blueprint_beat_pieces`
- [x] 只读级联影响查询：`GetReferenceCorpusCascadeImpact(observation_ids)` 经 `reference_specimen_evidence` 与 `reference_blueprint_beat_pieces` / `reference_corpus_blueprint_beats` 精确返回受影响 specimen/beat/blueprint；查询不自动修改 review/validity/stale 状态

**验收：** 证据边可 join；observation superseded 后能精确列出受影响 specimen。

### M0.4 语料库 + 授权（DB / 修复 #4 #6）

- [x] `reference_corpus_libraries` / `reference_library_members` / `reference_session_library_binding`
- [x] `reference_source_license`
- [x] 检索默认作用域解析/过滤：会话绑定库（可多库）∩ enabled ∩ license 允许复用 ∩ 去重折叠，返回跨库成员集合而非单 anchor
- [x] session scope fixture：一个 session 绑定至少 2 个 library，每个 library 至少 1 个 anchor，验证启用/禁用/去重后仍按集合检索
- [x] 授权负例薄切片：forbidden/unknown license 即使显式 include 也不进检索；`cleared_for_insertion=false` 即使被选中蓝图引用也不能插入

**当前检查点：** `ReferenceCorpusScopePayload.session_id` 已接入契约；章节侧传 `session_id=project:{novelId}:default` 且 `library_ids=[]`，由后端解析默认作用域。`SqliteReferenceCorpusService` 已在 `library_ids` 为空时读取 `reference_session_library_binding`，默认项目 session 会并入 `global` 工作区库；候选预取按 `library_id + anchor_id` 分组取样，避免前一个库的大量节点把后续库饿死。导入/更新默认库成员时会写同名 session binding。`SearchCandidatesUsesSessionBoundLibrariesBeforeScoring` 覆盖一个 session 绑定两个 library、召回两个 anchor/library 的基础回归；`SearchCandidatesHonorsDisabledSessionLibraries` 覆盖禁用库不再进入候选；`SearchCandidatesFoldsCrossLibraryDedupGroupsBeforeScoring` 覆盖 `dedup_group_id` 跨库折叠后只保留代表来源。`SearchCandidatesRejectsForbiddenAndUnknownLicensesEvenWhenExplicitlyIncluded` 覆盖 forbidden/unknown license 不因显式 include 绕过检索过滤；`GenerateInsertionDraftBlocksSelectedBlueprintWhenSourceIsNotClearedForInsertion` 覆盖已授权但未清权来源即使被选中蓝图引用，也只返回 blocked gate 并保持章节正文不变。

**验收：** 能按会话解析出跨多个 library/anchor 的有效检索作用域；forbidden/unknown 来源被排除；dedup_group 跨库折叠生效；章节使用侧不能绕过 session scope 直接固定到单 anchor。

### M0.5 分页契约 + 测试资产骨架（Contracts / 测试 / 修复 #9 #12）

- [x] `PageRequest` / `PageResult<T>`（pageSize 上限 200、稳定排序 tiebreaker、错误码）
- [x] TS 侧对应类型 + api.ts 分页签名
- [x] `SearchReferenceCorpusCandidates` bridge/service 接入：前端可调用，pageSize 超限返回 validation error，候选结果不暴露 embedding/source text
- [x] **fake LLM harness**：可注入的确定性 LLM 替身，返回固定 golden 输出
- [x] **fake embedding harness（护栏 G3）**：按文本 hash 生成确定性向量，golden 检索逐次一致可断言
- [x] **相似度算法（护栏 G5）**：4-gram 容器度 + LCS 比，piece-level，归一化+中文专名规则；纯确定性、无模型调用；单测逐值断言
- [x] **golden fixture 骨架**：小语料结构覆盖 corpus/current chapter/query/retrieval/blueprint/insertion/fake LLM response 指针
- [x] **golden fixture 完整版**：`m0-500-sentence-golden.json` 固定 500 句、5 sources、2 libraries，并逐句包含 authorized license、稳定 text_hash 与 evidence offset
- [x] **参数化规模 fixture 生成器**：`generate-fixtures.ps1` 支持按 `ScaleCharacterCount` 合成多 source/multi-library fixture；规模输出被限制在 `build/tmp/`，支持 `-SkipGolden` 安全 smoke。50K 标准档与可选 2M 长跑档的默认值、命名和 wrapper 收口归 M2 量化规模验收
- [x] 中断恢复测试脚本骨架：`run-recovery-harness.ps1` 覆盖 reservation/model/record/finalize/commit 五个中断点，带 checkpoint timeout、重复轮次和结构化 recovery metrics 输出

**验收：** 分页往返稳定、超限报错；fake LLM/fake embedding 可驱动全流程且结果可回归；相似度算法对 golden 输入产出固定值；golden/规模 fixture 可用。

---

## M1：纵向薄切片（跑通端到端闭环，修复 #10）

**目标：** 用 fake LLM + 多本 golden 小书，跑通"多本小说注册为共用语料库 → 基础分析 → 当前章节按目标跨所有启用库检索 → 多份蓝图候选/至少一次反馈迭代 → 槽位替换 → 正文候选/插入草稿"，自动模式，证明产品闭环。宁可每层都薄，也要先连通；不得用单 anchor 或单蓝图替代闭环验收。

### M1.1 导入 + Stage 0-1（后端）

- [x] 导入 golden 书/真实 source → text_nodes 树（M0.1 写入器）
- [x] Stage 1 确定性特征落 observation（M1 最小版：句长/节奏、感官 marker、情绪 marker；rule-based，无 LLM）
- [x] license 标注入库（旧 license_status → 新 reference_source_license gate；默认 project/global library membership）
- [x] 多源 golden fixture：至少两本授权小说注册为同一共用语料库或被同一 session 同时启用，后续检索必须能命中不同来源

### M1.2 最小检索（后端 / 修复 #5 / 护栏 G3 G4）

- [x] **node embedding 构建（护栏 G3，M1 最小版）**：复用 `IEmbeddingClient`，对 text_nodes 缺失向量懒构建并缓存；测试用 fake embedding 替身
- [x] material embedding 构建任务：新增 `reference_material_embeddings` canonical metadata，记录 material/node、provider/model/dimensions、material hash、node text hash、embedding hash/vector 与更新时间；正常 Stage 0 anchor 构建会同步写 canonical 行并投影 sqlite-vec，显式 `BackfillMaterialEmbeddingsAsync` 可按小说全量或指定 anchor 增量扫描。legacy `reference_source_segments.node_id` / `reference_materials.node_id` 缴空时先按 anchor+offset+text_hash 对齐 `reference_text_nodes`，generation 相同且 material/node hash 未变化则幂等复用，变化项才重算；每个目标 anchor 始终以完整 active material vector set 重建 sqlite-vec 投影。`BackfillMaterialEmbeddingsAlignsLegacyRowsAndIsIdempotent` / `BackfillMaterialEmbeddingsRebuildsOnlyChangedRequestedAnchor` 覆盖 Stage0、legacy 对齐、provider/model/dim/hash 巡检、重复运行零重算和 anchor 级增量
- [x] `CorpusQueryContext` + `CurrentChapterContext` 契约（前端不传 embedding，护栏 G4）
- [x] 章节 embedding 后端计算 + 缓存（key = draft text hash，前端不传 embedding）
- [x] 最小 `IQueryContextParser`（M1 确定性 parser：自然语言目标 → 固定 QueryContext；后续 M2/M4 可替换为 structured LLM）
- [x] 单路检索（M1 最小版）：text_nodes 语义向量 + library/license/visibility/reuse 过滤 + 当前章节 embedding 连贯度加权；召回链可以薄，但作用域必须来自 session 绑定的跨库集合
- [x] 返回 `PageResult<候选片段>` 的 bridge/contract 形态
- [x] 跨库检索回归：同一 QueryContext 在至少两个启用来源中召回片段，禁用任一 library 后排序/候选集合可断言变化

### M1.3 蓝图 + 槽位替换 + 插入（后端）

- [x] 最小 `ICorpusBlueprintAssembler`：单策略，产 1 份蓝图，beat→node 走 `reference_blueprint_beat_pieces`
- [x] M1 后端多蓝图薄切片：同一 QueryContext 至少产 2 份蓝图候选，候选可引用不同库来源，feedback 可排除蓝图/节点/来源并触发第二轮重组
- [x] 选中蓝图 → 插入草稿薄切片：`GenerateReferenceCorpusInsertionDraft` 可接收 `selected_blueprint`，按用户选定 beats 读取来源片段、槽位替换、闸门校验并返回草稿
- [x] 最小 `ICorpusSlotResolver`：检测人名/代词槽位，输出替换表
- [x] 最小 `ICorpusTextAssembler`：槽位替换 + 非槽位 preserved hash 校验
- [x] 插入闸门（M0.4 后端）：license cleared + 相似度阈值校验，通过才返回可插入结果；被阻断时保留原章节文本
- [x] M1 章节 UI 多蓝图薄切片：章节 UI 中真实呈现多蓝图候选，用户可选择/反馈重试，并将选中蓝图传入插入草稿

### M1.4 自动模式前端（前端 / 修复 #11）

- [x] 后端 bridge + TS adapter：`GenerateReferenceCorpusInsertionDraft` 自动闭环入口可被章节界面调用
- [x] 后端 bridge + TS adapter：保留 `GenerateReferenceCorpusBlueprintCandidates` 兼容多蓝图候选入口，契约包含 `feedback_applied`/`feedback_summary`；默认章节流程使用 `Get/AdvanceReferenceCorpusBlueprintSession`
- [x] 后端 bridge + TS adapter：`GenerateReferenceCorpusInsertionDraftCandidates` 正文多候选入口可被章节流程调用，契约包含 `selected_blueprint`/`candidates[].draft`
- [x] 写作会话最小界面：大纲输入 → 一份蓝图 → 一份草稿 → 插入编辑器 buffer（当前单路径检查点）
- [x] 自动模式多蓝图界面薄切片：章节目标 → 多份蓝图候选卡 → 用户选择/反馈重试 → 按选中蓝图生成插入草稿 → 插入编辑器 buffer
- [x] 正文候选界面：接受蓝图后生成正文候选列表，选择候选后预览 diff 并插入编辑器 buffer；当前若 selected blueprint 每拍只有单一 node，候选可只有一份，不允许用换料凑多份
- [x] 全部专家控件默认隐藏，用 AI 默认决策
- [x] 草稿 diff 预览（原句保留 vs 槽位替换标色）

**当前检查点（M9 前的历史 UI 入口）：** 章节编辑器右侧 `参考素材` 面板已接入真实 UI 多蓝图 + 正文候选薄切片。前端从 Monaco 读取当前正文与光标 offset，发送当前章节默认 `session_id`，不再把默认 library id 列表硬编码为章节参数；后端解析 `project:{novelId}:default` 与工作区公用语料。默认路径先调用 `GenerateReferenceCorpusBlueprintCandidates` 生成多份蓝图候选，用户可在候选卡中选择方案、以当前选中蓝图构造 feedback 重组第二轮，再点击“按选中蓝图生成草稿”；章节 UI 随后调用 `GenerateReferenceCorpusInsertionDraftCandidates`，payload 必须携带第二轮候选中的 `selected_blueprint` 和 `requested_count=3`。生成后展示正文候选列表，用户选择候选后再查看 diff、节点分析、技法标本、槽位替换和插入闸门；diff 预览优先使用后端 `preserved_spans` 标记非槽位保留片段，旧 `slot_replacements` 反推逻辑只作兜底。只有当前选中候选的 `ready_for_insertion && gate.passed && audit.passed` 时才允许把该候选的 `chapter_text_after_insertion` 应用到编辑器 buffer，且不直接调用 `SaveContent`。mock workflow 已改为真实 UI 点击触发首轮/二轮蓝图候选、正文候选、选择候选并应用，guardrail 校验 `feedback_applied=true`、`feedback_summary`、`selected_blueprint` 来源、每个正文 piece 必带 `preserved_spans`、`audit.passed=true` 且 `audit.pieces` 与 draft pieces 对齐，并用 exact property 检查不泄漏 `source_text/raw_text/embedding`。推荐素材、事实边界和旧严格流程默认收进“高级参考流程”，展开前不会触发 `SearchReferenceMaterials` 或 orchestration 读取。该检查点证明默认 session scope、多蓝图 UI 循环、选中蓝图正文候选和编辑器 buffer 插入薄切片可用，但不等于完整产品完成；M1 后端 golden 已覆盖跨库、多蓝图、正文候选闭环，后续仍需继续加深分析、检索、蓝图和拼装质量。

**M9 更新（2026-07-10）：** 默认章节路径现调用 `Get/AdvanceReferenceCorpusBlueprintSession`：生成、选择和反馈重组均持久化到服务端会话，关闭或刷新后恢复目标、迭代与选定蓝图；默认模式只显示“目标 → 蓝图 → 正文 → 插入”，旧候选入口只保留兼容边界。浏览器 workflow 已覆盖恢复、反馈、blocked 后回蓝图、错误重试、焦点和窄窗；真实用户走查仍是唯一开放项。

### M1.5 闭环验收（测试 / 修复 #12）

- [x] 后端薄切片集成测试：真实导入 + Stage 1 + parser → 检索 → 蓝图 → 槽位替换 → 闸门 → 可插入文本
- [x] 端到端脚本：golden 书 + 固定大纲 → 期望草稿（golden JSON 比对）
- [x] hash 校验：非槽位原句逐字保留
- [x] 插入闸门：相似度超阈值时正确阻断
- [x] 跨库闭环薄切片：默认 session scope 下项目库 + 工作区库同时进入同一草稿，pieces / beat_pieces 来自不同 anchor/library
- [x] 多蓝图迭代后端薄切片：固定目标先产至少 2 份蓝图；用户拒绝/勾选问题后再次重组，第二轮蓝图来源分布、beat 分配或 gap 标注发生可断言变化
- [x] 选中蓝图正文薄切片：从第二轮选定蓝图生成插入草稿，pieces 必须来自 selected blueprint 的 node_ids，非槽位 hash 校验通过
- [x] 前端真实 UI 多蓝图薄切片：mock workflow 通过章节面板点击生成首轮蓝图、反馈重组第二轮、选择第二轮蓝图并生成插入草稿；guardrails 断言 `selected_blueprint` 来自第二轮候选
- [x] 正文候选复用验证薄切片：`GenerateInsertionDraftCandidatesReusesSelectedBlueprintSourceVariantsThroughGate` 从选定蓝图生成多份正文候选，断言候选 node_ids 来自 selected blueprint、非槽位 hash 通过、gate 通过且 beat_pieces 可追溯
- [x] 跨库闭环 golden：至少两本 golden 书注册进启用语料库，同一写作目标召回来自不同 anchor 的候选，关闭其中一库后结果变化写入 golden JSON
- [x] 多蓝图迭代 golden：固定目标先产至少 2 份蓝图；用户拒绝/勾选问题后再次检索或重组，第二轮蓝图来源分布、beat 分配或 gap 标注写入 golden JSON
- [x] 正文候选复用 golden：从选定蓝图生成正文候选，断言 candidate node_ids 锁定在 selected blueprint 内，非槽位原句/结构最大化保留，剧情相关槽位和过渡才发生变化并写入 golden JSON

**当前检查点：** `Fixtures/corpus-driven-writing/m15-insertion-draft-golden.json` 固化小 golden 书、当前章节、固定目标和归一化 `expected_draft`；`GenerateInsertionDraftFromGoldenBookAndFixedOutlineMatchesGoldenJson` 从 fixture 创建真实 reference source，使用确定性 embedding 跑完整 `GenerateInsertionDraftAsync`，将 actual draft 投影到稳定 JSON 后做 deep equality，并额外断言 `reference_blueprint_beat_pieces` 追溯边已落库。`Fixtures/corpus-driven-writing/m15-cross-library-closed-loop-golden.json` 固化两本授权 golden source、默认 session 同时启用 project/global library、同一写作目标下首轮多蓝图、feedback 二轮避开被拒库、禁用其中一库后的候选/草稿变化、以及从选中跨库蓝图派生 selected-blueprint-locked 正文候选；两个 golden 的 draft piece 现在都固化 `preserved_spans` 的 span alias、source/output offset、source/output hash 与 matches，并固化 `audit` 的 passed/errors/pieces/violations，避免只靠整体 preserved hash。`GenerateCrossLibraryBlueprintAndDraftCandidatesMatchesGoldenJson` 对这些结果做归一化 deep equality，并断言跨库 pieces、禁用库变化、正文候选 gate/hash/audit 都成立。`GenerateInsertionDraftUsesDefaultSessionScopeAcrossProjectAndWorkspaceLibraries`、`GenerateBlueprintCandidatesSupportsFeedbackIterationAndSelectedBlueprintDraft`、`GenerateInsertionDraftCandidatesReusesSelectedBlueprintSourceVariantsThroughGate` 仍保留为更小的行为回归。`npm --prefix frontend run test:chapter-reference` 覆盖真实章节 UI 点击：首轮多蓝图、反馈重组、第二轮选中蓝图、正文候选、选择候选并应用到编辑器 buffer；guardrails 断言 `selected_blueprint` 来自第二轮候选、每个正文 piece 暴露 `preserved_spans`、`audit.passed=true` 且不直接 `SaveContent`。当前证明 M1 纵向闭环后端 golden 与前端 mock 工作流可回归，但不等价于最终理想产品；后续仍必须继续 M2-M5 的分析深度、精准检索、蓝图策略和拼装质量。

**验收：** 全流程用 fake LLM 稳定复现同一草稿；原句 hash 校验通过；闸门阻断可复现；跨库检索、多蓝图迭代、正文候选复用均有 golden 断言。**只有这些断言同时成立，此里程碑才证明产品闭环成立。**

---

## M2：加深分析（10 family 全量 + 技法标本）

**目标：** 把 M1 的薄分析升级为完整多维分析。

### M2.1 feature_family 锁定 schema（后端 / 关键设计）

为每个 family 定义锁定 schema descriptor（用于 prompt/validator，后续可生成 response-format JSON Schema），LLM 只填枚举，禁止自由发挥：

- [x] 句级：`syntax` / `rhythm` / `sensory`（数组）/ `emotion` / `rhetoric`（数组）
- [x] 段落级：`narrative` / `pov` / `action` / `character` / `commercial`
- [x] 每 family 一份 schema 文件 + 输出校验器（不合 schema 触发重试）
- [x] sensory 数组维度同步写 projection 表（M0.2 Stage 1 + M2.2 runner）

**当前检查点：** `Novelist.Core/App/ReferenceCorpusFeatureSchemas/*.json` 已内嵌 10 个 family 的锁定枚举 schema descriptor，字段名统一为 DB/DTO 契约的 `feature_key`；`ReferenceCorpusFeatureFamilyOutputValidator` 校验 `schema_version`/family/node_type/root 属性/observation 属性/枚举/数值范围/evidence offset，并把通过项投影为匹配 `reference_feature_observations` 的候选结构。空 `observations: []` 被视为合法“无可落地观察”，避免逼 LLM 编造；`value_kind` 收敛到 `enum|number|bool|array|object`，`value_text/value_num` 按 family + feature_key 显式映射（例如 `rhythm.pause_density` 不会误取 `char_count`）。`sensory`/`rhetoric` 允许 LLM 输出多项明细，但 validator 会聚合为单条 `value_kind=array` observation（`feature_key=senses/devices`，`value_json` 保存完整数组，evidence 覆盖整段，confidence 取最低值），避免数组型 family 拆多行导致幂等键冲突。Stage 1 的确定性 observation 已改为锁定 schema key，避免 `char_len/surface_mode` 继续污染共享表；M2.2 runner 已接通 LLM candidate → upsert → sensory projection。单元测试覆盖 10 family 加载、空输出、句级数组聚合输出、按 feature_key 数值映射、段落商业层输出、自由字段/错误枚举/证据越界拒绝。

### M2.2 Task A/B 全量分析（后端 / 修复 #3）

- [x] Task A 句级 LLM：全量、异步、按章节优先级排队、per-run token_budget、可续跑；代码证据：`ReferenceCorpusAnalysisInputSnapshotBuilder.BuildFeatureSentenceAsync`、`ReferenceCorpusFeatureWorkItemProcessor.ProcessAsync`、`ReferenceCorpusAnalysisWorker.PumpOnceAsync`；测试证据：`BuildFeatureSentenceFreezesTextAndExpandsEverySentenceFamily`、`ProcessAsyncUsesFrozenInputAndRemainingTokenBudgetAcrossRetries`、`PumpOnceProcessesAllFrozenFeatureWorkItemsAndCompletesJob`。边界：50K 标准规模已证明后台管线，不代表默认用户闭环或真实 provider 质量
- [x] Task B 段落级 LLM：全量、异步队列、章节优先级排队；代码证据：`ReferenceCorpusAnalysisInputSnapshotBuilder.BuildFeaturePassageAsync` 与同一 feature worker 协议；测试证据：`BuildFeaturePassageFreezesParentChapterSceneAndSiblingContext`、`RunAsyncForPassageAnalyzesOnlyParagraphSourceSegmentsAndPassesContext`。边界：只处理 canonical paragraph source segment，legacy 缺 node_id 时仍按既有诊断跳过
- [x] 持久化后台调度 schema：run/job/attempt + input snapshot/work item，additive migration，不并行维护两套 canonical run
- [x] job 状态机与 CAS：queued/running/pause_requested/paused/cancel_requested/cancelled/retry_wait/budget_exhausted/completed/failed + expected_version
- [x] worker 运行协议第一可靠薄切片：原子 claim、lease token、10 秒 heartbeat、45 秒 lease、启动 reconcile、失去 lease 禁止提交、桌面单 worker loop；15 秒独立 watchdog、显式 abandon 与 data-dir 重绑定仍属于恢复任务
- [x] 章节优先级与 aging：current/adjacent/normal/maintenance 持久化优先级，5 分钟 aging，持续高优先级流量下 normal 不饥饿；代码证据：`SqliteReferenceCorpusAnalysisJobStore.ClaimNextAsync` 的持久 priority + aging 排序；测试证据：`ClaimNextAgesNormalPriorityAcrossClassBoundariesWithinFifteenMinutes`。边界：这是确定性时间源下的饥饿防护，不等于共享环境吞吐验收
- [x] pause/cancel/resume 独立 API 第一薄切片：expected-version CAS、成功提交边界生效、取消保留已成功产物、补充总预算后 resume；allowed_actions、完整幂等语义与稳定产品错误映射仍属于进度/API 收口任务
- [x] retry 分类与退避：schema 0/1/3 秒最多 3 次；provider transient full-jitter 最多 5 attempt；Retry-After；永久错误不重试；代码证据：`ReferenceCorpusAnalysisRetryPolicy.Decide`、`ReferenceCorpusAnalysisWorker` 对 `retry_after_ms/retry_after_seconds` 的读取与持久化；测试证据：`ValidationFailureUsesBoundedSchedule`、`ProviderTransientUsesInjectableFullJitter`、`ProviderTransientRespectsRetryAfter`、`PermanentFailureNeverRetries`、`PumpOncePersistsProviderRetryAfterFromBridgeDetails`。边界：不覆盖外部 provider 的真实 SLA
- [x] 后台任务查询与进度：PageResult 稳定分页，node/work-item 双分母，token/reservation、当前章节、allowed_actions、安全 diagnostics；代码证据：`ReferenceCorpusAnalysisScheduler.ListAsync/GetAsync` 与 job payload；测试证据：`GetExposesCurrentTokenReservationAfterWorkItemReservation`、`ListCursorIsStableAndRejectedWhenFiltersChange`、`CommitWorkItemSettlesOutputProgressAndTokensAtomically`。边界：Release 50K 全管线 list P95 已为 27.04 ms；该性能证据不替代面向用户的进度理解和恢复体验验收
- [x] 重启恢复与故障注入：调用前/返回后/事务中/cursor 后/完成前/真实进程强杀，证明零重复 active 产物、零丢失、cursor 单调；代码证据：worker startup `ReconcileAsync`、lease fencing、completion Record/Finalize 与原子 settlement；测试证据：`EnqueuePersistsFrozenSnapshotAndSurvivesSchedulerRestart`、`ReclaimExpiredRunningLeaseSchedulesRetry`、`RecordCompletionIsIdempotentAndRejectsConflictingPayload`、`FinalizeCompletionSettlesActualTokensAttemptAndReservationExactlyOnce`，以及 5 故障点 2 轮、10/10 子进程 harness；真实 pause/cancel/stale lease 墙钟时限由下方独立 runtime samples 验证
- [x] 产品触发入口：`StartReferenceCorpusFeatureAnalysis` / `GetReferenceCorpusFeatureAnalysisRun`，按 anchor + scope 启动 sentence/passage 默认 family 分析并返回安全 run 状态
- [x] Task B 段落节点选择 + 上下文地基：只分析 `reference_source_segments.segment_type='paragraph'` 的真实段落，跳过 hook/beat/action_afterbeat 等派生 passage；注入 parent chapter、containing scene、前后 paragraph context；旧库缺 `reference_source_segments.node_id` 时诊断并跳过，不做 hash/offset 猜测
- [x] Task A/B 共用执行器地基：读取 node×family、调用可替换 analyzer、locked schema 校验、observation upsert、tokens/resume_cursor/status 更新、sensory projection 同步
- [x] schema 失败重试：invalid JSON/schema/rejected output 按 `MaxValidationAttempts` 重试；失败尝试累计 tokens 但不写 observation、不推进 cursor；重试耗尽后 run 标 failed
- [x] 真实 LLM analyzer 地基：复用 `IChatCompletionClient` + selected model，生成 schema-locked prompt，抽取 fenced JSON，读取 usage tokens；测试使用 fake chat client，不触发真实网络
- [x] confidence < 0.70 的 observation 初始化为 `review_state='low_confidence'`，分析查阅列表可按该状态过滤，作为 M8 复核队列输入信号
- [x] evidence_start/end 精确记录；代码证据：locked family validator 将模型 evidence offsets 投影到 observation，frozen payload 保留 evidence；测试证据：`ValidateAcceptsSentenceSensoryArrayOutputAndBuildsObservationCandidates`、`ValidateRejectsEvidenceOffsetsOutsideCurrentSourceNode`、`BuildTechniqueKeepsFrozenDependencyEvidenceAfterLiveObservationChanges`。边界：offset 是节点内字符范围，原文导航能力由 M2.4 单独验证
- [x] 低置信 observation 的 ReviewQueue 入队/领取/人工确认流转（M8 消费）；代码证据：`SqliteReferenceCorpusGovernanceService` review queue/list/batch review 路径；测试证据：`QueuesLowConfidenceObservationsAndSupportsPagedBatchReview`、`StartFeatureAnalysisRoutesLowConfidenceObservationsToReviewList`。边界：当前证明低置信入队、稳定分页和人工 confirmed 流转，罕见 family 扩展规则不在本项内
- [x] 后台中断续跑：从 input snapshot 的最后成功 work item 续跑，应用重启/lease 回收后 cursor 单调且不整章重放；代码证据：work-item reservation/settlement 与 canonical resume cursor 同事务推进；测试证据：`DueRetryClearsMetadataWithoutMovingStableCursorOrWorkItem`、`CommitWorkItemSettlesOutputProgressAndTokensAtomically`、`RunAsyncPersistsValidatedObservationsAndResumesAfterBudgetCursor`。边界：真实进程时限已由下方 runtime metrics 验收；用户离开/返回后的可理解恢复流程仍由 M9 验收

**当前检查点：** 同步 `ReferenceCorpusFeatureAnalysisRunner` 兼容入口仍保留；新的持久后台路径已冻结 node text/context/evidence、selected model 与 token policy，构建 node×family work item，并以 canonical run/job/attempt/input snapshot/work item 为唯一持久执行协议。单 worker 桌面 loop 会启动 reconcile、claim、heartbeat、fenced commit/settlement；模型调用期间收到 pause/cancel 时先同事务提交成功产物、token 与 cursor，再收敛到 paused/cancelled；损坏或旧 frozen snapshot 在 reservation 前即可 fenced fail，不等待 lease 超时。句级、段落级和 Stage 3 payload 均只从 frozen input 解码；定向测试已覆盖 priority aging、完整 retry 分类与 Retry-After、稳定分页/进度、completion 幂等及强杀恢复。`run-recovery-harness.ps1 -Rounds 2 -RuntimeSamples 30 -Configuration Release` 额外运行真实 worker-loop 样本：pause P95 82.28 ms、cancel P95 87.68 ms、stale lease 回收 P95 37.17 ms，30/30 个失租样本均以 `lease_expired` abandon 旧 attempt 且仅由新 attempt 持久化 5 个 completion。`run-scale-harness.ps1 -Configuration Release` 已生成 50,015 字、16 anchors、4 libraries、32 jobs 的全管线报告：13,385 work items/completion rows 全部完成，吞吐 29.16/s，claim/list/progress P95 为 10.12/27.04/3.51 ms，预算穿透、重复输出、预留 token 与活动 lease 均为 0；数据库从 29,433,856 增至 47,398,912 bytes。M2 因默认用户闭环、真实 provider 与持续运行证据不足仍保持 `S`，不得据此称为产品完成或规模化完成。历史 2M 首轮结果只作为性能基线，不再阻塞 M2。

### M2.3 Task C 技法标本（后端 / 关键设计）

- [x] 综合推理：全部 A/B observation + 原文 → TechniqueSpecimen；代码证据：`ReferenceCorpusTechniqueWorkItemProcessor.ProcessAsync`、`ReferenceCorpusTechniqueSpecimenRunner` 聚合同节点 active 高置信 A/B observation 与 frozen node text；测试证据：`ProcessAsyncRetriesAgainstFrozenEvidenceAndRemainingBudget`、`StartTechniqueSpecimenAnalysisPersistsSpecimensAndSafeStatus`、`ListTechniqueSpecimensReturnsSafeEvidenceTrace`
- [x] Stage 3 runner/validator/writer 地基：读取 active 高置信度 observation + 原文节点，调用可替换 analyzer，写 `reference_technique_specimens`
- [x] why_it_works 每 contributing_factor 走 `reference_specimen_evidence` FK 到真实 observation（禁空引用/未知 id）
- [x] technique_abstract / transfer_template 去内容化泄露检测（拒绝原文专名、原文动作短语、长原文片段）
- [x] Stage 3 仅高置信度节点触发（低于阈值 observation 不进入 analyzer 输入）
- [x] 真实 LLM analyzer：复用 `IChatCompletionClient`，schema-locked prompt，抽取 fenced JSON，读取 usage tokens
- [x] 产品触发入口：`StartReferenceCorpusTechniqueSpecimenAnalysis` / `GetReferenceCorpusTechniqueSpecimenAnalysisRun`，返回安全 run 状态
- [x] runner 级预算续跑：零预算不调用模型，budget_exhausted 后提高总预算，从最后成功 node 继续；specimen/evidence/token/cursor 同事务
- [x] Stage 3 后台 job 接入：technique job 要求同 novel/anchor/scope 的已完成 feature job 依赖，冻结 dependency job/run/input snapshot 及 observation evidence 后进入既有异步队列、章节优先级、lease/watchdog、pause/cancel/resume 和 retry 协议；测试证据：`TechniqueEnqueueRequiresCompletedFeatureDependency`、`PumpOnceProcessesTechniqueWorkItemFromCompletedFrozenFeatureDependency`、`TechniqueWorkItemRetriesAfterTransientProviderFailure`。边界：真实 control/lease 时限及 50K 全管线门已通过；Stage 3 仍须持续随常规后台回归覆盖

**当前检查点：** `ReferenceCorpusTechniqueSpecimenRunner` 保留为兼容入口；新的 Stage 3 路径要求一个同 novel/anchor/scope 且已完成的 feature job，`SqliteReferenceCorpusAnalysisScheduler` 将该 job 的 id/run/input snapshot 和符合阈值的 observation evidence 冻结到 technique work item，再由 `ReferenceCorpusAnalysisWorker` 的同一持久协议执行。`ReferenceCorpusTechniqueSpecimenOutputValidator` 锁定 `reference-corpus-technique-specimen-v1` 输出，要求 `why_it_works` 每个 factor 至少引用一个真实 observation id，未知/空 evidence 直接拒绝；落库在同一事务内写 `reference_technique_specimens`、`reference_specimen_evidence`、tokens 和 cursor，`specimen_id = hash(run_id,node_id,technique_family)` 保证重试幂等，重跑保留人工 `review_state`，不把 confirmed/rejected 重置为 unverified。定向回归已覆盖 dependency 缺失/未完成拒绝、冻结后 live observation 变化不影响输入、暂停/恢复、worker 重启和瞬态 provider retry。真实时限和 50K 全管线门已独立通过，但这不等于 TechniqueSpecimen 已形成默认产品体验；专项前端不新增，仍由通用后台任务面板展示状态。

### M2.4 分析前端

- [x] 后台任务面板：稳定分页展示 10 个 job 状态、node/work-item 双进度、token、当前章节、重试倒计时和安全诊断；代码证据：`CorpusAnalysisJobsPanel` 与 `ListReferenceCorpusAnalysisJobs` adapter；workflow 证据：`verifyCorpusAnalysisJobsWorkflow` 逐一断言 queued/running/pause_requested/paused/cancel_requested/retry_wait/budget_exhausted/completed/failed/cancelled 中文状态。边界：面板契约覆盖 Stage 3，但不代表真实时限或 50K 规模验收
- [x] 后台控制交互：仅按后端 `allowed_actions[]` 提供暂停/取消/恢复/重试，携带 expected_version，CAS conflict 后刷新，不在前端复制状态机；代码证据：`CorpusAnalysisJobsPanel.runAction`；测试证据：`ControlOperationsUsePersistentCasVersions`，workflow `verifyCorpusAnalysisJobsWorkflow` 证明陈旧 version 首次冲突后刷新、第二次使用新 version 成功暂停；真实 worker-loop 30 样本 P95 为 pause 82.28 ms、cancel 87.68 ms。边界：这证明后台控制的时限，不替代章节默认路径和长任务 UX 验收
- [x] 后端列表 API：`ListReferenceCorpusFeatureObservations` / `ListReferenceCorpusTechniqueSpecimens`，分页 `PageResult<T>`、稳定 sort、filter 白名单、默认 active、非法 cursor/filter/pageSize 走 validation error
- [x] 安全展示契约：Observation 不暴露 `value_json`；TechniqueSpecimen 不暴露 `why_it_works_json` 或原始 JSON 字符串，改为 typed `transfer_slots` / 条件列表 / `why_it_works.contributing_factors`
- [x] evidence trace：TechniqueSpecimen 通过 `reference_specimen_evidence` junction 二次读取，不用 join 放大分页；trace 返回 observation id/family/key/confidence/text_hash/bounded evidence preview/value preview/explanation
- [x] 当前章节嵌入：章节右侧 `语料驱动草稿` 生成后，按 draft pieces 的 `anchor_id/node_id` 自动加载“节点分析 / 技法标本”，可切换 piece，不把素材库处理入口混进章节使用面板
- [x] 全库分析查阅 tab：素材库处理侧新增独立“分析结果”tab，可按 anchor、node、family/key、review/validity、min confidence 查阅 observation/specimen；不复用章节使用面板，不提供蓝图/候选/插入动作
- [x] evidence 跳原文：observation 与 specimen evidence 按 anchor/node/evidence_start/evidence_end 触发 `novelist:locate-corpus-evidence`，`ReferenceAnchorView.locateEvidence` 加载 node window 并渲染范围高亮；workflow `verifyCorpusAnalysisResultsWorkflow` 断言定位请求完成且出现 `data-corpus-evidence-selection`。边界：只在素材库原文窗口内定位，不承诺外部编辑器跳转

**验收（双轨，修复 #3 #12）：**
- 正确性轨：golden 书全量分析比对 golden JSON
- 标准规模轨：50,000 字 fake-LLM fixture 验证续跑、预算、性能和恢复，作为日常/M2 强制门槛
- 可选长跑轨：2,000,000 字仅用于发布前、专项性能诊断或百万字能力声明，不进入常规阶段门
- why_it_works 每条可追溯；abstract 泄露检测通过
- [x] 量化后台验收：`run-recovery-harness.ps1` v2 分开记录 5 个事务点强杀恢复和真实 runtime loop；Release、2 轮、每类 30 样本报告 pause/cancel P95、lease reclaim P95、fenced commit 与每例状态/产物计数
- [x] 5 个事务故障点（`after_reservation` / `after_model` / `after_record` / `during_finalize` / `after_commit`）真实子进程强杀/重启 2 轮，共 10/10 case 零重复、零丢失、token 精确结算且 reservation 清零；指标见 `build/tmp/corpus-driven-writing/recovery-metrics.json`
- [x] P95 pause/cancel ≤ 60 秒；stale lease 30 秒内恢复：Release runtime metrics 的 pause P95=82.28 ms、cancel P95=87.68 ms、stale reclaim P95=37.17 ms；30/30 stale case 为旧 attempt `lease_expired` abandon、新 attempt 完整产出，旧 worker 零 completion commit
- [x] 50K 标准全管线验收：保留现有 1,000 work-item job-store micro-benchmark；正式 fixture/wrapper 默认改为 50,000 字和独立 `scale-50k` 输出，真实经过 scheduler → sentence/passage snapshot builder → worker → fake analyzer。Release 正式 metrics 为 50,015 字、16 anchors、4 libraries、4 session bindings、32 jobs、13,385 work items/completion rows，fake analyzer calls=13,385；duplicate outputs=0、持久 budget penetration=0、reserved tokens=0、active leases=0，吞吐=29.16 items/s，claim/list/progress P95=10.12/27.04/3.51 ms（各 32 样本），数据库 29,433,856→47,398,912 bytes。`build/tmp/corpus-driven-writing/scale-50k-metrics.json` 为本机正式证据。2M 仍仅保留显式 `-Mode JobStore -MinimumCharacters 2000000` 长跑，其结果不影响本项勾选
- [x] UI/API 验收：10 个 job 状态中文显示、allowed_actions、CAS conflict、稳定分页和应用重启后任务可见均有自动 workflow；后端证据：`ReferenceCorpusAnalysisSchedulerTests` 的持久 CAS、稳定 cursor 和 restart round-trip；前端证据：`verifyCorpusAnalysisJobsWorkflow` 与 `verifyCorpusAnalysisResultsWorkflow`。边界：workflow 使用 mock bridge 验证契约和交互；50K 性能与进程时限已由正式门禁覆盖，但面向用户的默认路径与错误恢复体验仍由 M9 验收

**当前检查点：** M2.4 已完成后端分页读取、章节使用侧只读嵌入、素材库处理侧独立“分析结果”tab、后台任务面板和 evidence offset 原文定位。`SqliteReferenceCorpusAnalysisService` 先分页 specimen 再读取 evidence junction，避免 evidence join 导致分页重复；Observation list join `reference_text_nodes` 只返回 `text_hash` 和 bounded evidence preview，不返回 node 全文。Bridge/TS adapter/mock 已补齐 list/control 方法，mock workflow 固化 10 状态、allowed_actions、CAS conflict 刷新、稳定分页、重启后任务可见和 evidence 范围高亮。M2 recovery harness 已完成 5 个事务点、真实强杀/重启 2 轮，并在真实 worker-loop 采集 30 组 pause/cancel/stale-lease 时限和 lost-lease zero-commit 证据。Release 50K 全管线报告已完成 13,385 work items，claim/list/progress P95=10.12/27.04/3.51 ms，完整输出、零重复、零预算穿透和零活动 lease 均已满足。历史 2M 首轮证明完整性、零重复和延迟预算，但其 18.9857/s 不再作为主线失败项；M2 因缺少真实 provider、持续运行和默认用户体验证据仍保持 `S`。

---

## M3：加深检索（四类索引融合 + 当前章节上下文进排序）

- [x] `reference_technique_vectors` JSON fallback 投影表 + technique abstract embedding 薄切片：已通过 session/library/license/dedup 的节点若有 active/non-rejected `reference_technique_specimens`，可越过每来源前 N 预取窗口补进候选池；缓存 `technique_abstract + trigger_context + transfer_template + effect_on_reader` 向量，并输出 `score_components.technique_fit`
- [x] `reference_technique_vectors` native sqlite-vec topK 薄切片：新增 `reference_technique_vector_rows` / `reference_technique_vector_index_state`，以 JSON fallback cache 为 canonical source，构建 scoped native vec0 index；native 命中只作为 `scoped_nodes.node_id IN (...)` 召回 hint，仍经过 session/library/license/dedup/include/exclude/reuse 与结构化 filters；native 不可用或查询失败时回退旧 JSON fallback
- [x] native sqlite-vec 后台回填薄切片：新增 `BackfillReferenceCorpusTechniqueVectorIndex` / `BackfillTechniqueVectorIndexAsync`，可不经 `SearchCandidates` 显式预热 scoped technique vec0 index；复用同一套 scope/source_hash/row signature 校验，返回 `ready/empty/skipped/failed`、provider/model/dim、source/vector/skipped 计数和诊断；搜索会复用已回填 rows/state，不重复 provision
- [x] native sqlite-vec 回填规模化：SQLite 持久后台队列支持全量/增量去重调度、lease 回收、指数退避重试与 attempt 上限；巡检报表按 provider/model/dim 对比 source/row 数并报告 stale/failed diagnostics；受控 fixture 已覆盖增量不重复 provision、全量强制重建、失败重试和配置漂移
- [x] 四路召回合并薄切片：在 session/library/license/dedup 安全 scope 后，base prefetch 之外的文本语义 / 技法语义 / 结构化 observation / 章节上下文 route 可补入候选池；分页时保留各 route 代表，并在 `score_components` 暴露 `recall_text_semantic` / `recall_technique_semantic` / `recall_structured_observation` / `recall_chapter_context`
- [x] 结构化 observation 独立召回薄切片：`ReadStructuredObservationRecallNodeIdsAsync` 在 scoped 安全集合上独立读取 observation route node ids，支持 QueryContext term、`feature_filter_{n}_*`、旧 `feature_*` 与 sensory filters；route hit 作为内部 hint 写入候选，`recall_structured_observation` 只表示真实 route 命中，不再由 `observation_fit` 评分 winner 误标
- [x] 章节上下文独立召回薄切片：`ReadChapterContextRecallNodeIdsAsync` 在 scoped 安全集合上独立读取当前插入窗口、previous summary、人物快照与 allowed knowledge 命中的 node ids，先应用结构化 filters 再按 context term 权重排序并设置最小加权阈值；`recall_chapter_context` 只表示真实 context route hit，不再只由 `local_context_fit` 最高候选误标
- [x] 完整四路召回：文本语义、技法语义、结构化 observation、章节上下文分别独立取 topK 后 union；observation projection/context 增加热路径索引和确定性排序；route rank/score 统一写入 provenance，并完成距离归一化及融合权重标定；1,000 节点受控 fixture 的 warm retrieval 纳入 10 秒 CI 性能预算（不等同真实长篇 SLA）
- [x] 结构化 observation 过滤薄切片：`SearchCandidates` 支持 `feature_family`/`feature_key`/`feature_value_text`/`feature_value_num_min|max` 与 `sensory_sense`/`sensory_min|max_intensity`，走 `reference_feature_observations` + `reference_obs_sensory` EXISTS 过滤
- [x] 多 feature observation AND 过滤薄切片：`feature_filter_{n}_family/key/value_text/value_num_min|max` 可表达多个独立 observation 条件，全部按 AND 语义命中；保留旧 `feature_*` 单条件过滤兼容
- [x] 章节蓝图接入结构化过滤薄切片：`GenerateReferenceCorpusBlueprintCandidates` 根据当前目标把“动作替代心理描写表现愤怒 + 触觉”等意图映射为候选检索 filters，后续 selected blueprint → draft 仍按选定节点取材，不二次误过滤
- [x] 当前章节上下文排序薄切片：后端计算并缓存当前章节 embedding，同时新增 `score_components.local_context_fit`，只从插入点附近文本、previous summary、人物名/state 和 allowed knowledge 提取确定性短词；`ForbiddenKnowledge` 不进入正向检索信号
- [x] M3 检索 golden 薄切片：`m3-retrieval-golden.json` 固化 licensed scoped search、四路召回诊断、安全排除、缓存计数和候选排序；normalized expected 只保留 `text_hash`、`text_preview_hash`、长度、score component key、route marker 与 evidence，不写入原文、embedding、prompt、`value_json` 等内部/源文字段
- [x] 完整融合排序：当前章节 embedding 连贯度 + 插入位置匹配 + 授权/质量/跨库来源多样性加权与权重标定（修复 #5）
- [x] 权重可被检查表反馈调整（M4 消费）
- [x] 跨语料检索（按 session 绑定的所有启用 library 展开，非单 anchor；默认不要求用户选择一本参考）
- [x] 跨库去重与来源覆盖：dedup_group 折叠后仍保留可解释的来源分布，低质量来源降权但不污染授权过滤
- [x] 全部返回 `PageResult<T>`

**验收：** 结构化查询（"动作替代心理描写表现愤怒"）精准命中 golden fixture 中标注样本；当前章节上下文改变时排序可见变化；技法语义相似区别于文本相似（golden 断言）；同一 QueryContext 在多库启用时召回跨 anchor 结果，禁用任一库、改变授权或 dedup_group 后候选集合/排序按 golden 预期变化；测试不得只覆盖单 anchor。

**当前检查点：** `SearchCandidatesFiltersByStructuredObservationAndSensoryProjection` 已证明候选搜索不再只是纯向量召回：同一授权语料库内，只有同时满足 `action.emotion_carrier=action_over_psychology` 与 `sensory=tactile,intensity>=0.8` 的句子会命中，用于覆盖“动作替代心理描写表现愤怒”这类结构化意图。`SearchCandidatesRequiresAllIndexedStructuredObservationFilters` 进一步证明多个 `feature_filter_{n}_*` observation 条件会同时生效，可表达 `action.emotion_carrier=action_over_psychology AND rhythm.length_band.char_count>=16` 这类复合意图，并且旧 `feature_*` 单条件过滤继续兼容。`GenerateBlueprintCandidatesUsesStructuredObservationFiltersFromGoal` 证明章节蓝图路径已把目标映射到这些结构化 filters，跨 project/global 两库只从匹配节点生成蓝图，并且 selected blueprint 后续生成正文不再被二次过滤破坏。`SearchCandidatesRanksInsertionWindowAndAllowedKnowledgeContext` 证明当前章节上下文已进入可解释排序：`local_context_fit` 会让命中插入点附近文本、人物名和 allowed knowledge 的候选排到更前，同时断言 `ForbiddenKnowledge` 不得作为正向检索信号。`SearchCandidatesMergesFourRecallRoutesWithDiagnostics` 证明 route union 已能补入 base prefetch window 外的文本语义、技法语义、结构化 observation、章节上下文代表，分页不会只按单一融合分数截断，并用 `score_components.recall_*` 标注召回来源；`SearchCandidatesStructuredObservationRecallDoesNotDependOnBasePrefetchWindow` 与 `SearchCandidatesStructuredObservationRecallUsesExplicitFeatureFiltersBeyondPrefetchWindow` 进一步证明结构化 observation 已有独立 SQL route，可分别通过 QueryContext term 和显式 `feature_filter_*` 把远位置节点拉入候选，且 `recall_structured_observation` 只表示真实 route hit，不再由普通 `observation_fit` winner 误标；`SearchCandidatesChapterContextRecallMarksEveryRouteHitBeyondScoreWinner` 证明章节上下文也已拆出独立 SQL route，多个 context route hit 都会获得 `recall_chapter_context`，不再只给 `local_context_fit` 第一名打标，并用最小加权阈值避免弱泛词误标；`SearchCandidatesChapterContextRecallHonorsStructuredFiltersBeforeRouteLimit` 证明 context route 会先应用结构化 filters 再取 topK，避免 route limit 被不满足 filter 的上下文噪声占满；`SearchCandidatesMergedRecallRoutesHonorScopeLicenseAndDedup` 进一步证明 route union 不能绕过 excluded anchor、forbidden license 或 dedup representative。`SearchCandidatesRainDoorwayMatchesM3RetrievalGoldenJson` / `SearchCandidatesFourWayRecallMatchesM3RetrievalGoldenJson` 已把基础授权检索与四路召回诊断固化到 `m3-retrieval-golden.json`，并由 `CorpusDrivenWritingGoldenFixtureTests` 递归防止 expected 区块泄露 raw source、embedding、prompt、`value_json` 等字段。`SearchCandidatesRanksTechniqueAbstractEmbeddingSeparatelyFromRawText` 证明技法 abstract embedding 已区别于原文 embedding 参与排序：同一候选池内，`technique_fit` 可把“动作替代心理描写表现愤怒”命中到对应技法标本节点；向量按 provider/model/dimensions/technique_hash 缓存，第二次检索只重算 query，不重算技法向量。`SearchCandidatesUsesNativeTechniqueTopKWhenAvailable` 证明 native sqlite-vec topK 已进入技法召回薄切片：rowid 映射回 source node 后只作为 `scoped_nodes` 内的召回 hint，命中的远位置技法节点可进入候选并带 `recall_technique_semantic`；`BackfillTechniqueVectorIndexPrewarmsNativeRowsAndSearchDoesNotReprovision` 证明 `BackfillReferenceCorpusTechniqueVectorIndex` 可在不调用 `SearchCandidates` 的情况下预热 `reference_technique_vectors`、native row mapping 与 index state，后续搜索复用 rows/state 且不重复 provision；`BackfillTechniqueVectorIndexFailureReturnsDiagnosticsAndSearchStillFallsBack` 证明回填失败返回 `failed` 诊断，不破坏后续搜索的 JSON fallback。`SearchCandidatesFallsBackWhenNativeTechniqueTopKFails` 证明 native provision/query 失败不会让搜索失败，会回退旧 JSON fallback；`SearchCandidatesNativeTechniqueTopKHonorsExcludedAnchors` 证明 excluded anchor 不会被 native 路径建索引、写 technique cache 或返回；`SearchCandidatesNativeTechniqueTopKDoesNotBypassStructuredFilters` 证明 native 命中仍受 `feature_*` / sensory 过滤约束；`SearchCandidatesNativeTechniqueTopKClearsRowsForRejectedSpecimens` 证明 active specimen 被人工 rejected 后，该 scope 的 native row/state 会被清掉，旧 rowid 不再污染后续检索；`SearchCandidatesNativeTechniqueTopKRebuildsStaleRowHash` / `SearchCandidatesNativeTechniqueTopKRejectsForgedRowMappingWhenIndexStateMatches` 证明 index_state 不能只靠 count/hash 欺骗，row 映射与当前 entries 不一致会触发重建；`SearchCandidatesNativeTechniqueTopKExcludesActiveSpecimenWithSupersededRunId` 证明 dirty active + superseded specimen 不参与 native 或 JSON fallback。`SearchCandidatesRecallsTechniqueSpecimenBeyondPerSourcePrefetchWindow` 继续覆盖无 native provider 时的 JSON fallback 远位置技法补池。新增护栏覆盖：技法补池不得绕过结构化 filters；被 `exclude_anchor_ids` 排除或 forbidden license 的技法标本不返回也不生成向量缓存；selected blueprint 若引用远位置技法节点，`GenerateInsertionDraftAsync` 后续仍能读到 source piece，不退化为 `source_node_missing`。`ReferenceCorpusServiceTests` 与 `ReferenceCorpusWritingServiceTests` 整组仍覆盖 session 跨库、禁用库、授权过滤、dedup 折叠、embedding 缓存、M1/M3 golden 和正文候选。当前仍未完成文本语义完整独立 topK、完整反馈权重映射、规模 M3 golden、native 回填队列化/全量增量调度、结构化 observation/context 热路径排序与性能预算。

**审计修订（2026-07-11）**：上方检查点末尾关于“文本语义完整独立 topK、native 回填队列化/全量增量调度、结构化 observation/context 热路径排序仍未完成”的概括已被本节已勾任务取代。`ReferenceCorpusTechniqueVectorMaintenanceLoop` 现已由 desktop composition 持有，并随初始化、数据目录重绑和运行时释放启停；但当前自动产品路径尚未制定例行 maintenance job 的入队策略，前端也不暴露 schedule/pump 专家控制。不能把每次 `SearchCandidates` 结束都当作入队点：搜索已在当前 scope 内校验并按需重建 native 技法索引，额外排队只会重复 provision。后续策略必须由技法产物变化触发，并携带已解析的 session/library scope、成本和重试预算。其余真实缺口是人工 query 集和 Recall@K/nDCG 尚未建立、50K 跨库 SLA 未验证；不能据此把受控 fixture 结论升级为真实长篇质量。

---

## M4：加深蓝图（N 策略 + 检查表反馈）

- [x] 完整 `ICorpusBlueprintAssembler` 多策略：情绪优先/节奏优先/技法多样性/场景模板；`MultiStrategyReferenceCorpusBlueprintCandidateAssembler` 负责四策略、coverage/gap/emotion arc/source distribution，独立 `SqliteReferenceCorpusBlueprintIterationCoordinator` 补齐可恢复会话状态与 emotion/rhythm/technique diversity/scene template/source distribution 五维专家检查表闭环，不修改共享 writing service
- [x] 多候选 assembler 地基薄切片：新增 `IReferenceCorpusBlueprintCandidateAssembler` / `MultiStrategyReferenceCorpusBlueprintCandidateAssembler`，`GenerateBlueprintCandidatesAsync` 只负责检索、反馈读写和候选持久化；M4 profile/coverage/gap 组装逻辑不再散落在 `SqliteReferenceCorpusWritingService` 私有排序 helper 中
- [x] 覆盖率计算 + gap 识别 + 情绪弧线预估
- [x] 蓝图表扩展薄切片：`reference_corpus_blueprints` 持久化 `assembly_strategy`/`coverage_score`/`gap_reasons_json`/`gap_positions_json`/`query_context_json`/`source_distribution_json`/`feedback_reason`
- [x] corpus beat 父表薄切片：`reference_corpus_blueprint_beats` 持久化 `beat_index`/`role_in_beat`/`narrative_function`，不复用旧 anchored-draft `reference_chapter_blueprint_beats`
- [x] beat→node 追溯薄切片：`GenerateBlueprintCandidatesAsync` 候选阶段即写 `reference_blueprint_beat_pieces`，正文草稿阶段继续复用同一 upsert；追溯边不放 JSON
- [x] `GenerateChapterBlueprintAsync` 改造：Parser + Retriever + Assembler
- [x] OrchestrationStages 加 goal_parsing/corpus_retrieval/blueprint_assembly
- [x] **legacy 兼容（护栏 G6）**：旧 run/blueprint/frontend 状态只读可恢复；新旧 stage 常量并存 + 兼容 shim；不破坏 Phase 16 reconcile/recovery；旧 blueprint 可选一次性归档脚本（不自动改写）
- [x] 检查表反馈契约 + `reference_user_feedback` 持久化薄切片：二轮蓝图反馈会以 `target_type=blueprint`、被拒蓝图 id 为 `target_id` 写入 `reference_user_feedback`，保留 problem tags、rejected/avoid 计数、fallback 诊断和用户 note；同一反馈重试使用确定性 `feedback_id` 幂等写入，不重复污染偏好历史
- [x] 检查表反馈 → 检索约束薄切片：`too_fast` 映射到 `rhythm.length_band` 中长句约束；`too_direct_emotion` 继续映射到 `action.emotion_carrier=action_over_psychology`；有命中时重新检索并改变蓝图候选，若反馈硬约束无命中则退回目标基础检索以保持循环不中断
- [x] 同小说历史反馈软降权薄切片：无显式反馈的新一轮蓝图生成会读取 `reference_user_feedback` 中已拒绝蓝图的 `node_hash` / `library_hash` / `anchor` 信号，把相同节点集合和相关来源降到后排；空 feedback object 按无显式反馈处理，不会污染策略名或阻断历史学习
- [x] M4 策略权重薄切片：当候选池有足够 feature/technique 信号时，内部 profile 会生成 `emotion_priority_m4` / `rhythm_priority_m4` / `technique_diversity_m4` / `scene_template_m4` 四类蓝图候选，按候选 evidence 与 score_components 排序，并先做跨 library/anchor 代表选择，避免 N 策略只改名不换素材
- [x] 蓝图 gap 诊断薄切片：`gap_reasons` 同时区分 `insufficient_beats` / `single_library_source` / `single_anchor_source`，避免多句或多策略候选实际塌缩到同一参考 anchor 时只暴露模糊的单库问题
- [x] M4 coverage/gap 证据化薄切片：候选已有 M4 evidence 时，`coverage_score` 不再只包装检索分，会纳入 emotion/rhythm/narrative/technique 覆盖；缺维度时输出 `missing_emotion_evidence` / `missing_rhythm_evidence` / `missing_narrative_evidence` / `missing_technique_coverage`
- [x] M4 profile 选材补齐薄切片：四类 M4 profile 不再只按单一 profile 分数取前三，而是在保留策略头部素材后主动补齐缺失的 emotion/rhythm/narrative/technique 证据，并优先选择能增加 library/anchor 覆盖的候选；历史反馈 penalty 仍优先于覆盖强度
- [x] M4 beat 级缺口返回薄切片：候选返回体新增 `gap_positions[]`，把全局缺失的 emotion/rhythm/narrative/technique 维度定位到具体 `beat_id/beat_index/node_ids`，前端候选卡可直接显示“第几拍缺节奏/叙事/技法”；若整份蓝图已覆盖完整则不误报位置缺口
- [x] 多蓝图迭代循环：独立 coordinator 以 `session_id + request_id` 幂等推进 generate/revise/accept；revision 将所选蓝图节点与来源、逐维检查表问题映射到现有 feedback 后重新调用 writing pipeline，session 快照作为 `reference_user_feedback` 事件持久化并可跨进程恢复，accept 要求五维全部通过且进入不可继续 revise/generate 的终态
- [x] 蓝图来源分布：每份蓝图记录跨 library/anchor 的来源覆盖，避免所有策略意外塌缩到同一 anchor
- [x] 前端专家模式薄切片：章节写作页可切换自动/专家模式；蓝图卡展示 coverage、来源分布、beat gap、显著差异审计、iteration 状态和按 narrative function 推导的情绪弧。现有“反馈重组”继续作为拒绝/再检索入口；尚未实现逐维拒绝检查表和独立节奏色块编辑器

**验收：** N 份蓝图策略对 golden fixture 产出可断言的不同分配；检查表勾选后被勾维度有可见变化（golden 前后比对）；gap 标注对"无合格语料"场景正确；至少一次“生成多蓝图 → 用户拒绝/反馈 → 再检索/重组 → 新蓝图候选”的循环有 golden 断言；多库启用时蓝图可引用多个 anchor，且不会退化为单 anchor。

**当前检查点：** `GenerateBlueprintCandidatesUsesInjectedCandidateAssemblerAndPersistsResult` 证明蓝图候选生成已走可替换的 `IReferenceCorpusBlueprintCandidateAssembler`，service 只负责编排、反馈读写和 `reference_corpus_blueprints` / beat / beat→node 持久化。`GenerateBlueprintCandidatesMapsTooFastFeedbackToSlowRhythmRetrieval` 覆盖 M4 反馈薄切片：首轮可命中高相关但短促的 `node-feedback-fast-market-s1`，二轮用户勾选 `too_fast` 后，检索增加 `rhythm.length_band.value_num >= 16`，并优先生成 `rhythm_slow_m1`，第一候选同时使用 project/workspace 慢压迫节点；命中成功时不会误报 fallback。`GenerateBlueprintCandidatesProducesM4StrategyVariantsFromFeatureSignals` 证明当候选池具备 emotion/rhythm/narrative/technique 信号时，`MultiStrategyReferenceCorpusBlueprintCandidateAssembler` 能产出四类 M4 strategy variant，四份蓝图 node set 互不相同，且每份都保持跨库来源、不出现 `insufficient_beats` / `single_library_source` / `single_anchor_source`。`GenerateBlueprintCandidatesPersistsM4BlueprintMetadataAndBeatPieces` 证明候选生成阶段已落 `reference_corpus_blueprints`、`reference_corpus_blueprint_beats` 与 `reference_blueprint_beat_pieces`：assembly strategy、coverage、gap reasons、gap positions、query context、source distribution、feedback reason、beat index/role/narrative function 和 beat→node 边都可追溯，且不等待正文草稿生成。`GenerateBlueprintCandidatesScoresM4CoverageByRequiredDimensionEvidenceAndReportsGaps` 证明 coverage 已开始消费 M4 维度证据：维度完整的 M4 蓝图会高于只带 emotion/action 的普通高语义分蓝图，残缺蓝图会报告缺 rhythm/narrative/technique 覆盖，并通过 `gap_positions[]` 定位到具体 beat；完整蓝图不会误报总缺口或位置缺口。`GenerateBlueprintCandidatesBackfillsM4StrategyCandidatesWithCoverageEvidence` 证明 `emotion_priority_m4` 不会继续拿三条 emotion-only 高分句子冒充可用蓝图，而会主动补进 rhythm 和 narrative+technique 支撑节点，且补齐过程仍保持跨 library/anchor。`GenerateBlueprintCandidatesFeedbackFilterFallbackAddsDiagnosticGapReasons` 证明反馈硬约束无命中时，系统退回目标基础检索但会把 `feedback_filters_no_matches` / `fallback_to_base_filters` 写入 `feedback_summary`、候选 `feedback_reason` 和 `gap_reasons`，用户能看出反馈没有精准命中。`GenerateBlueprintCandidatesAvoidSourceFallbackAddsDiagnosticGapReasons` 证明避开来源把候选池打空时，系统会保留循环不中断，同时标注 `avoid_sources_no_alternatives` / `fallback_ignored_avoid_sources`。`GenerateBlueprintCandidatesPersistsBlueprintFeedbackForReuse` 证明二轮反馈会写入 `reference_user_feedback`，记录 rejected node、avoid library、avoid anchor 的可复用信号，并且同一反馈重试不会重复写。`GenerateBlueprintCandidatesHistoricalFeedbackDownranksRejectedNodeSet` 证明后续无显式反馈或空反馈对象的新一轮蓝图生成，会把同一组被拒素材及相关来源软降权，不再默认排第一。`GenerateBlueprintCandidatesRejectedBlueprintIdAloneDoesNotRegenerateSameNodeSet` 进一步证明仅传 `rejected_blueprint_ids`、不传 rejected nodes/avoid tags 时，系统也不会用同一组 source nodes 换一个反馈摘要后重新生成同一蓝图。`GenerateBlueprintCandidatesSourceRepetitionFeedbackPrioritizesCrossSourceBlueprint` 证明 `source_repetition` 不再只是摘要标签：当候选池存在多库/多 anchor 替代时，第一份再生成蓝图会优先使用 `source_repetition_diversity_m1`，先取每库最佳、再取每 `(library_id, anchor_id)` 最佳，避免默认第一候选继续塌到同一来源。`GenerateBlueprintCandidatesSupportsFeedbackIterationAndSelectedBlueprintDraft` 与 cross-library golden 已固化 `single_anchor_source`：反馈后候选若只能退到同一参考 anchor，必须把这个塌缩暴露给用户，而不是只报单库或 beat 不足。`GenerateBlueprintCandidatesProducesM4StrategyVariantsFromFeatureSignals` 还证明每份蓝图的情绪弧与 beat 一一对齐，强度归一化且证据节点不越出对应 beat；来源分布计数覆盖实际选中节点。前端候选卡已把 fallback/gap code 与 beat 级缺口显示成中文诊断，并用 mock workflow 固化二轮反馈后的诊断可见性。尚未完成：真正的多轮会话状态、逐维专家检查表闭环，以及更深的蓝图策略模型。

**审计修订（2026-07-10）**：上方检查点末尾“尚未完成真正的多轮会话状态”的表述已过期。`SqliteReferenceCorpusBlueprintIterationCoordinator` 已实现 generate/select/revise/accept 与持久 session；默认 `ChapterReferencePanel` 已消费 `Get/AdvanceReferenceCorpusBlueprintSession`，SQLite 集成测试覆盖 coordinator 重建后恢复，浏览器 workflow 覆盖关闭/刷新、反馈重组和失败重试。因此 M4 仍为 `S` 的原因是蓝图区分度和真实效果证据不足，不是默认路径或会话状态机缺失。

---

## M5：加深拼装（完整槽位/过渡/hash + 多草稿）

- [x] `transfer_slots` 写作侧消费第一层：正文拼装读取 selected source node 的 active 且未 rejected `reference_technique_specimens.transfer_slots_json`，把声明槽位归一化为 `character/place/honorific/plot_object` 约束；slot replacement 若替换未声明槽位，`DraftAudit` 以 `slot_replacement_transfer_slot_disallowed` 阻断并保持章节正文不变。此项只消费 `slot_name`，不等于自动派生槽位变体或理解自然语言 `constraints`
- [x] `ICorpusSlotResolver` 全类型第一层：显式 `character/place/honorific/plot_object` 槽位归一化，代词/人名/地名/称谓/道具启发式识别；引号/书名号生成 `locked_spans` 保护证据，slot replacement 与锁定范围相交会被 `DraftAudit` 阻断
- [x] `preserved_spans` 结构化记录薄切片：每个 insertion piece 返回稳定 `span_id`、source/output offset、source/output hash、matches；前端 diff 优先消费 spans，golden JSON 固化 span 证据，不暴露原文
- [x] `preserved_spans` 审计闸门薄切片：hash/offset/source/output mismatch 进入 `DraftAudit`，`ready_for_insertion = gate.passed && audit.passed`，失败保持章节正文不变并拒绝插入
- [x] `AuditDraftAgainstBlueprint` 外层包络薄切片：输出 pieces 必须与 selected blueprint source pieces 一一对应；每个 piece 输出必须被 `preserved_spans` 或 `slot_replacements` 完整覆盖；slot replacement 必须落在安全短槽位且 source/output 值与 range 一致；`assembled_text` 必须等于已审计 piece/transition 输出的换行拼接，未审计正文不得插入
- [x] `ICorpusTransitionResolver` 审计薄切片：gap 显式建模，选中蓝图片段之间每个相邻 gap 必须返回 `direct_join` 或 `insert_transition`；transition 具备 id/gap_id/hash/output range/audit trace，`gap_id` 必须绑定相邻 piece 对，缺失、伪造、错配或未审计过渡均阻断插入
- [x] `replace_piece` 候选重组薄切片：正文候选路径遇到 transition resolver 要求替换 source piece 时，只能用 selected blueprint 同一 beat 已声明的备选 node 生成 `transition_repair` 候选；若 replacement node 不在 selected blueprint 同 beat 内，或同 beat `transition_repair` 变体重新审计仍失败，候选必须继续阻断，不得从重新检索结果或邻近句静默换料
- [x] blocked `replace_piece` 的 `next_action.feedback` 契约：超出 selected blueprint 同 beat 或 `transition_repair` 修复仍失败时，正文候选必须保持 blocked、`ready_for_insertion=false`、章节正文不变，并带 `next_action.action=regenerate_blueprint`；默认 UI 将 `next_action.feedback` 映射为 `AdvanceReferenceCorpusBlueprintSession(action=revise)` 的 checklist，沿用既有蓝图反馈字段表达 rejected blueprint/node、avoid library/anchor/node、problem tags、fallback/repair diagnostic、失败 beat/gap/replacement 标识和用户可见 summary，禁止要求前端拼接自由文本或读取 `source_text/raw_text/embedding`
- [x] `ICorpusTransitionResolver` 第一层三选一：默认规则型 resolver 不再永远 `direct_join`；`raise_pressure -> withhold_answer` 相邻 beat 生成审计过的 `insert_transition`，重复/同源相邻 piece 生成 `replace_piece` 阻断并进入蓝图/候选重组流程，其余安全相邻 gap 仍为 `direct_join`
- [x] 多草稿：同一 selected blueprint 的 primary source node set 上，显式/自动槽位变体可与 `default` / `direct_join` 过渡策略组合生成 1~N 份；未提供槽位或过渡变体时只返回一份 primary-source 稿，不再通过轮换 source node 凑候选数。candidate-set audit 固定对照 selected blueprint primary node set，并以槽位屏蔽后的 piece fingerprint 阻断未追踪正文差异、来源边界变化和重复正文
- [x] slot-only 多草稿契约薄切片：`GenerateReferenceCorpusInsertionDraftCandidatesPayload.slot_value_variants` 可在同一 selected blueprint/同一 primary source nodes 上生成 `slot_variant_1..N`；候选 source node、source hash、preserved spans 源范围和 locked spans 源范围保持一致，差异只来自 slot replacements 和 assembled text。此项只证明请求侧槽位变体闭合，不等于完整多草稿；`transfer_slots` 自动派生、过渡策略变体和跨候选差异审计仍未完成
- [x] `transfer_slots` 自动槽位候选薄切片：当请求没有显式 `slot_value_variants` 时，正文候选会读取 selected blueprint primary source nodes 的 active 且未 rejected `transfer_slots_json`；若声明 `character` 且当前章节存在多个人物快照，会生成 `auto_transfer_slot_1..N` 同源候选，只把源文开头人称代词映射到当前章节人物，保持 source node/source hash/preserved spans 一致。人工 rejected specimen 不参与自动派生。此项不凭空生成地点/道具/称谓，不理解自然语言 `constraints`，也不代表完整自动变体/差异审计完成
- [x] 多草稿候选集差异审计薄切片：同一 source node set 的多份正文候选会在返回前执行候选级审计；候选若出现不属于该候选 `slot_values` 映射的 slot replacement，会以 `draft_candidate_set_non_slot_difference` 阻断该候选，`ready_for_insertion=false` 且章节正文回退，防止恶意/错误 assembler 把非槽位改动伪装成 slot replacement；候选若最终 `assembled_text` 与同组前序可插入候选完全重复，会以 `draft_candidate_set_duplicate_text` 阻断后续重复候选。此项先覆盖槽位候选集，不等于完整过渡策略差异审计或 UI 聚合折叠
- [x] M5 章节专家 UI 薄切片：专家模式可编辑槽位名/多值变体，选择 `default` / `direct_join` 过渡策略并随生成请求提交；正文候选以横向并排视图显示文本、槽位差、过渡差和 locked span 数，消费 `candidate_set_audit` 摘要；可插入候选必须先锁定，再执行既有服务端 `RecordReferenceCorpusInsertionAudit` 并确认写入。此项不等于自然语言 `transfer_slots.constraints` 推理、完整过渡策略质量评测或跨候选池重组
- [x] 完整 `AuditDraftAgainstBlueprint` 改造：selected source pieces 必须一一对应且 source hash 匹配；preserved/locked spans、slot source/output range/value/transfer slot、assembled text 完整覆盖和 transition gap/pair/hash/approval/output range 均服务端重算；source 缺失、未审计输出、泄露式附加正文、授权失败或相似度超阈值任一命中都会令 `ready_for_insertion=false` 并保持章节正文不变
- [x] 正文候选派生规则：多稿 API 强制接收 selected blueprint；兼容单稿入口未显式传入时也先走正式 blueprint candidates 编排再选择通过差异审计的候选，不再私下 assemble。正文阶段只使用 selected blueprint 每 beat 的 primary source node；transition resolver 请求换源时保留阻断稿并返回 `regenerate_blueprint` feedback，必须继续迭代蓝图后才能重新派生正文
- [x] selected blueprint 节点锁定薄切片：正文候选只允许在每个 beat 自己声明的 `NodeIds` 内轮换，不得从重新检索结果、同 library/anchor 邻近句或其它 beat 拉替代 node；若任一 selected node 因 scope/library/授权检索结果变化无法读到 source piece，则返回 blocked `source_node_missing`，不得残缺生成可插入正文
- [x] 前端 mock workflow/guardrail：从 blocked `replace_piece` 候选点击下一步后，必须以 `next_action.feedback` 触发第三轮 `AdvanceReferenceCorpusBlueprintSession(action=revise)`；guardrail 断言第三轮蓝图调用存在、checklist 映射保留 blocked 候选的 problem tags、第三轮返回 `feedback_applied=true` 且 `feedback_summary` 可见，并且后续正文候选只能来自第三轮选中的蓝图
- [x] 前端专家模式：章节面板提供结构化槽位变体表、已选过渡策略清单、逐稿实际 transition decision/strategy 清单、candidate-set audit 并排 diff，以及锁定后独立确认状态；未锁定候选不能执行既有服务端插入审计和写入

**验收：** hash 校验非槽位逐字保留；每个 piece 输出被 preserved spans 或 slot replacements 完整覆盖；选中蓝图相邻 pieces 都有 transition 决策或明确 `direct_join`，且 `gap_id` 绑定相邻 piece 对；golden JSON 固化 `transitions` 与 `audit.transitions` 追踪；同 beat 内可修复的 `replace_piece` 生成 `transition_repair` 候选并重新通过 gate/audit；越界 replacement 或修复仍失败时候选保持 blocked、带可原样回传的 `next_action(action=regenerate_blueprint, feedback=...)`；前端 mock workflow/guardrail 固化第三轮蓝图调用；`locked_spans` 固化受保护片段并阻断槽位替换；多草稿差异断言仅在槽位/过渡；越界改动节奏词被 audit 拦截；从同一已接受蓝图生成的正文候选保留来源原句/结构比例可断言，剧情微调不破坏授权闸门。

**当前检查点：** `GenerateInsertionDraftCandidatesDoNotSubstituteNodesOutsideSelectedBlueprint` 覆盖单节点 selected blueprint：即使检索结果中同 library/anchor 有其它句子，正文候选也只能使用用户选中蓝图里的 node，不会自动换料。`GenerateInsertionDraftCandidatesReusesSelectedBlueprintSourceVariantsThroughGate` 增强为 beat 级断言：每个 piece 的 `node_id` 不仅属于 selected blueprint 全局集合，还必须属于对应 `beat_id` 的 `NodeIds`，防止跨 beat 偷换。`GenerateInsertionDraftCandidatesBlockWhenSelectedBlueprintSourceIsUnavailable` 覆盖 scope 排除 selected node 的场景：系统返回 blocked `source_node_missing`，保持章节正文不变，不用剩余来源拼残缺正文，也不重新检索替代句。`GenerateInsertionDraftCandidatesRebuildsAllowedBlueprintVariantWhenTransitionRequiresReplacement` 覆盖 transition resolver 要求 `replace_piece` 且 replacement node 属于 selected blueprint 同一 beat 备选的场景：正文候选会生成 `transition_repair` 蓝图变体并重新审计，通过后才可插入。`GenerateInsertionDraftCandidatesBlocksTransitionReplacementOutsideSelectedBlueprint` 覆盖 replacement node 不在 selected blueprint 同 beat 内的场景：系统保留 blocked 候选和 `transition_piece_replacement_required` 诊断，不从检索结果或邻近句静默换料，并返回 `next_action.feedback`，可由前端原样触发下一轮蓝图重组；同一测试已延长到第三轮：用 `next_action.feedback` 生成的新蓝图再进入 `GenerateInsertionDraftCandidatesAsync` 后，可得到 `gate/audit/ready` 全通过且不含被拒 node 的正文候选。`GenerateInsertionDraftCandidatesKeepsOriginalBlockedCandidateWhenTransitionRepairStillFails` 覆盖同 beat repair 仍失败时继续 blocked 且携带 `next_action`。`GenerateInsertionDraftUsesDefaultTransitionResolverToBridgePressureIntoWithheldAnswer` 覆盖默认 resolver 在 `raise_pressure -> withhold_answer` gap 上生成 `insert_transition` 并通过 transition audit；`GenerateInsertionDraftBlocksWhenDefaultTransitionResolverRequiresDuplicateSourceReplacement` 覆盖默认 resolver 对重复相邻来源返回 `replace_piece` 并阻断插入。`GenerateInsertionDraftCandidatesCanProduceSlotOnlyVariantsFromSameSelectedBlueprint` 覆盖请求侧 `slot_value_variants`：同一 selected blueprint、同一 source node、同一 preserved/locked 源证据可生成多份 `slot_variant_*` 正文候选，候选之间只按 `character/place/honorific/plot_object` 槽位映射产生文本差异，受保护标题不被替换；C# contract、TS 类型与 mock bridge 已同步该输入字段。`GenerateInsertionDraftCandidatesBlocksNonSlotDifferencesAcrossSlotVariants` 证明同源多稿返回前会执行候选集差异审计：恶意 assembler 即使把非槽位“指尖→掌心”伪装成安全 slot replacement、让单稿 audit 通过，也会被 `draft_candidate_set_non_slot_difference` 阻断并保持章节正文不变，合法候选不受影响。`GenerateInsertionDraftCandidatesBlocksDuplicateTextAcrossSlotVariants` 证明不同 slot 参数最终生成完全相同正文时，后续重复候选会以 `draft_candidate_set_duplicate_text` 阻断，避免把无差异结果展示为可选择多稿。`GenerateInsertionDraftCandidatesHonorsTransferSlotConstraintsFromTechniqueSpecimens` 与 `GenerateInsertionDraftCandidatesBlocksSlotReplacementOutsideTransferSlots` 证明写作侧已消费 active 且未 rejected 的 technique specimen `transfer_slots_json`：合法声明槽位可通过，未声明槽位进入 `slot_replacement_transfer_slot_disallowed` 并保持章节正文不变；`GenerateInsertionDraftCandidatesIgnoresRejectedTechniqueSpecimenTransferSlots` 防止人工 rejected 的 specimen 继续约束正文。`GenerateInsertionDraftCandidatesAutoDerivesCharacterTransferSlotVariants` 证明当请求没有显式 `slot_value_variants` 时，active 且未 rejected 的 `character` transfer slot 可从当前章节人物快照派生 `auto_transfer_slot_1..N` 同源候选，候选不轮换 source node，非槽位证据保持一致；`GenerateInsertionDraftCandidatesDoesNotAutoDeriveRejectedTransferSlots` 防止 rejected specimen 驱动自动派生。前端 TS 契约、正文候选卡、mock bridge、workflow 和 guardrail 已同步：从 blocked `replace_piece` 候选点击“回到蓝图重组”会触发第三轮 `GenerateReferenceCorpusBlueprintCandidates`，断言入参 feedback 与候选 `next_action.feedback` 一致、结果 `feedback_applied=true` 且反馈摘要可见。`PreservingReferenceCorpusTextAssembler` 现在为每个 piece 输出结构化 `preserved_spans`，记录非槽位片段的 source/output offset、hash 与 matches；contract/bridge/TS 类型、mock workflow、M1 golden fixture 均已同步，前端 diff 预览优先用 spans 标识保留片段。`ICorpusSlotResolver` 第一层已覆盖 `character/place/honorific/plot_object` 显式槽位和常见启发式识别，`ReferenceCorpusInsertionPiecePayload.locked_spans` 会把书名号/引号等受保护范围作为 source/output offset + hash 证据返回，前端 diff 可见；`GenerateInsertionDraftAppliesTypedSlotsWithoutReplacingProtectedQuotedText` 覆盖四类槽位替换且不改保护标题，`GenerateInsertionDraftBlocksSlotReplacementInsideLockedProtectedText` 覆盖恶意 resolver 返回锁定范围内替换时 audit 以 `slot_replacement_locked_range` / `locked_span_hash_mismatch` 阻断。`DraftAudit` 已接入插入草稿：source 缺失、piece preserved hash mismatch、span offset 越界、source/output span hash 不一致、span.matches=false 都会进入 audit errors/violations，并令 `ready_for_insertion=false`；`GenerateInsertionDraftBlocksWhenDraftAuditFindsPreservedSpanMismatch` 覆盖 gate 通过但 audit 阻断的场景，章节正文保持不变。新增 `GenerateInsertionDraftBlocksWhenAssemblerDropsSelectedBlueprintPiece`、`GenerateInsertionDraftBlocksWhenExplicitSlotReplacementConsumesWholeSourceSentence`、`GenerateInsertionDraftBlocksWhenAssembledTextContainsUnauditedOutput`，把缺失 selected source piece、整句伪槽位替换、piece 内未被 preserved span/slot replacement 覆盖的新增文本、以及 `assembled_text` 追加未审计正文纳入 audit 阻断。过渡已进入同一审计包络，不再是可选装饰：`IReferenceCorpusTransitionResolver` 接收相邻 piece gap，必须为选中蓝图相邻 pieces 返回 `direct_join`、`insert_transition` 或 `replace_piece`；`DraftAudit` 生成 `audit.transitions`，校验 `gap_id` 绑定的相邻 piece 对、hash、approval、decision、output range 与 assembled text 一致，缺失/伪造/错配都会阻断。M5 仍限定在章节使用侧的拼装审计加深，消费已处理语料和 selected blueprint，不把素材库处理管线或库管理界面混进正文生成流程。当前仍未完成 `transfer_slots.constraints` 自然语言约束推理、地点/道具/称谓等完整自动槽位变体派生、过渡策略变体差异审计、跨蓝图/跨候选池 replacement 重组策略、重复候选 UI 聚合折叠和专家 UI。

**审计修订（2026-07-10）**：M5 的专家模式、候选并排 diff、锁定确认和 candidate-set audit 已有薄切片。当前剩余项应具体表述为自然语言 `transfer_slots.constraints`、非 character 槽位自动派生、过渡策略质量/差异评测、跨候选池重组、重复候选聚合，以及真实章节的保真/适配/自然度/修改量证据；不再使用笼统“专家 UI 未完成”。

---

## M6：语料库产品化（库/去重/授权闸门完善）

- [x] 全局库/项目库启用规则 + 会话绑定管理 UI
- [x] 来源质量分级 + 禁用来源 + disabled_reason
- [x] 跨来源去重（dedup_group_id 识别与折叠）
- [x] 授权工作流：导入即标注 → 检索过滤 → 插入闸门（服务端重读来源/license 并重算相似度，不信任前端 gate）
- [x] 插入审计留痕（来源/license/相似度/闸门，audit_id 幂等）

**验收：** forbidden/unknown 不进检索；相似度超阈值插入被阻断且不可绕过；去重折叠对重复导入生效；审计可追溯每次插入来源。

---

## M7：聚合知识（作者画像/场景模板/世界观）

- [x] 四聚合表（style_profiles/scene_templates/world_models/dialogue_techniques）
- [x] **溯源表 `reference_aggregate_provenance`（护栏 G7）**：每个聚合产物记 (library_id, anchor_id, run_id)
- [x] `BuildAuthorStyleProfileAsync`（Stage 1 数值均值 + 高频分类统计；缺口明确为空，不伪造）
- [x] `ExtractSceneTypeTemplatesAsync`（feature_key/value 聚类，≥3 次形成稳定模板并附来源例句）
- [x] `BuildWorldModelAsync`（character/sensory 事实 + specimen world_context_dependencies 聚合）
- [x] 跨语料模板合并（确定性 feature-family 聚合薄实现）
- [x] **stale 传播（护栏 G7）**：任一源 anchor 重跑（新 run）→ 按 provenance 定位依赖聚合标 stale
- [x] 前端：治理专家页展示作者风格 / 场景模板 / 世界观 / 对话技巧摘要

**验收：** AuthorStyleProfile 对 golden 书统计特征匹配（数值断言）；场景模板 example 经 golden 标注验证；**某源重跑后依赖它的 cross-corpus 模板正确标 stale**。

---

## M8：复核工作流（review/validity 状态机 + 重跑语义）

- [x] `review_state` 状态机：unverified → confirmed/rejected/conflicted（人工）
- [x] `validity_state`：active/superseded（机器）+ superseded_by_run_id（修复 #2）
- [x] 自动入队：ReviewQueue 表/API 消费 confidence < 阈值与冲突（罕见 family 规则待扩展）
- [x] 重跑：旧 observation 不删、标 superseded、保留 review_state；证据全失效则 specimen superseded
- [x] 冲突检测：同 node 不同 run 不一致 → conflicted 入队
- [x] ReviewQueue 返回 `PageResult<T>`
- [x] 前端复核队列：检查表操作 + 批量 + 跨页分页

**验收：** 重跑后用户已 confirm 的判断不被污染（review_state 保留、validity_state 变 superseded）；依赖旧 run 的 specimen 正确 superseded；冲突正确入队。

---

## M9：默认体验 + 专家 UI 收口

- [x] 自动/专家模式切换完善（治理页与章节写作页）
- [x] 写作会话专家工作台：阶段进度 + 现有操作区 + 当前章节/生效语料库上下文（人物快照沿用章节上下文推断）
- [x] 章节语料写作状态可中断恢复（sessionStorage 保存目标/模式/蓝图/正文候选/选择）；片段保留 node/anchor/library/license 溯源
- [x] 恢复端到端 UI 基线：后台任务行提供稳定任务身份，workflow 仅在被操作行断言状态，重复“已暂停”文案不再触发 strict locator；`npm --prefix frontend run verify` 全量通过，相关浏览器截图与 diagnostics 归档于 `output/playwright/phase16/`
- [x] 默认章节路径收敛：章节面板只呈现“写目标 → 选蓝图 → 选正文 → 明确插入”一条主路径；直接候选与 orchestration 不得在自动模式形成两套并列入口；每阶段只有一个主操作，内部 id、route marker、审计细节和手工参数默认隐藏。`test:chapter-reference` 断言自动/专家信息分层和单一主操作。
- [x] 持久会话恢复：默认 UI 改用 M4 `Get/AdvanceReferenceCorpusBlueprintSession`，应用刷新/重启后从服务端 session 恢复目标、迭代、选定蓝图和下一步；sessionStorage 只作瞬时 UI 缓存，不能成为唯一恢复来源。SQLite 集成测试覆盖 generate → select → revise → select → accept → coordinator 重建，浏览器 workflow 覆盖关闭、清理旧缓存后从服务端恢复。
- [x] 长任务与错误恢复体验：每个 ready 素材来源行提供唯一“开始分析”主操作，调用 `EnqueueReferenceCorpusAnalysisJob` 创建标准 sentence `feature_analysis` job，并把焦点带到“后台任务”；用户可离开后返回同一任务查看进度、耗时状态和下一可执行动作。暂停、失败、预算耗尽、CAS 冲突和 blocked candidate 各只给一个明确主恢复动作，技术诊断折叠展示。`test:corpus-library` 覆盖“导入 → 启动 → 离开 → 返回”；蓝图推进失败后重试复用原 `request_id`，由浏览器故障注入回归验证。
- [x] 视觉层级与可访问性收口：复用现有设计 token、Button 和 Lucide 图标；自动模式避免面板套卡片与信息堆叠，专家信息渐进展开；核心流程支持键盘操作、可见焦点、焦点回位、ARIA 状态播报，并在 1280x720、1440x900、125%/150% 缩放下无重叠、截断或关键操作出屏。相关浏览器截图与 workflow 已通过。

  自动路径的用户可见文案统一使用“写作蓝图”，不以“剧本”描述中间产物，避免把小说写作流程误解为镜头/台词式操作；`test:chapter-reference` 断言默认面板不出现该术语。
- [ ] 素材库工作台使用性收口：`素材库` 左侧必须显示真实参考书籍列表而非空白占位，支持筛选、文件选择、添加、删除和可用状态选择；中间只展示 ready 来源的六维语料覆盖与按需检索（材料类型、叙事功能、情绪张力、场景节点、叙事视角、表达技法），打开工作台不得无条件扫描全部材料；右侧用 `AI 蓝图预演` 替代通用聊天，只能基于左侧显式选择的 ready 参考书调用 `GenerateReferenceCorpusBlueprintCandidates`，展示候选策略、节拍、覆盖度和来源分布。预演是临时比较工具，不创建持久 session、不替代章节默认跨库路径、不写入编辑器或调用 `SaveContent`。`test:reference-workspace` 必须覆盖加载、选择、添加、删除、六维筛选、请求 scope 和无正文写入，并在 1280x720 与窄桌面宽度截图检查后才能勾选。
- [ ] 易用性证据：为“导入并启动分析、离开后查看/恢复任务、目标到蓝图、反馈后选正文、blocked 后恢复并插入”5 个任务建立真实浏览器 workflow 和截图；自动化 workflow、截图和故障恢复断言已完成。真实走查让 5 名目标用户逐张阅读任务卡，主持人只能复述任务、不解释界面或下一步；至少 4 人须在不看文档、无旁人提示下完成自动模式主流程。每个任务记录完成/放弃、开始和结束时间、回退次数、首次失败点、使用的恢复动作与 1-5 主观难度；记录只保存脱敏事件和截图，不保存源文或本地路径。`corpus-writing-usability-fixtures-v1` 与 `run-usability-study-evaluation.ps1` 已强制固定任务集、脱敏字段、至少 5 人和至少 4 人无提示全路径完成的判定；两参与者 contract fixture 只验证工具。相同失败点出现两次即转为 UX 修复项和浏览器回归，再安排复测。

任务卡、主持人边界、固定失败/恢复码表和导出规则见[用户走查执行套件](./evaluations/usability-study-kit.md)。该套件只让真实走查可执行，不构成任何参与者或验收数据。

**验收：** 自动模式只要求用户做三类决策：写目标、选蓝图、选正文并明确插入；专家模式按需展开，不占用默认路径。素材库工作台提供独立的参考书管理与临时蓝图预演，不把它伪装成章节写作路径。用户可离开长任务并从同一状态继续，错误不暴露内部实现且给出可执行恢复动作；每片段仍可追溯到源语料、分析依据和 license。章节侧浏览器 workflow、视口/缩放/键盘检查和 `frontend verify` 已通过；素材库工作台的专用 workflow 与小规模目标用户走查仍未完成，因此 M9 维持 `S`，不能以自动化证据宣称“易用好用”或升级为 `P`。

---

## 跨里程碑约束（全程遵守）

1. **契约先行** — 后端方法先定 C# 契约 + TS 类型 + api.ts 签名（列表一律 `PageResult<T>`）
2. **additive migration** — ALTER TABLE 加列，幂等，存量库可升级
3. **安全 + 授权红线** — SafePath/SSRF/审批流/migration copy-first/无泄露 + license 插入闸门不可绕过
4. **可追溯** — 每产物记 run_id + evidence（走 junction FK），UI 每论断可跳原文
5. **跨库闭环不可降级** — 写作 session 的有效语料范围来自所有启用 library 成员；M1/M3/M4/M5 的测试必须覆盖跨库检索、多蓝图迭代、正文候选复用，不接受单 anchor 作为唯一验收
6. **回归资产（修复 #12）** — 固定 golden fixture + golden JSON + fake LLM + 规模 fixture + 性能预算 + 中断恢复脚本 + UI 交互验收；杜绝"符合语义/合理"式主观验收
7. **验证命令** — 后端 `dotnet test Novelist.slnx --no-restore -v minimal`；前端 `npm --prefix frontend run verify`
