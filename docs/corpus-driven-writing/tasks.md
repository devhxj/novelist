# 语料驱动写作系统 — 分阶段任务拆解（v2）

> 本文档按 [development-plan.md](./development-plan.md) 第九节的实现顺序展开为可执行任务清单。v2 重排为**纵向薄切片优先**（修复评审 #10）：先修地基 schema，再立即跑通端到端闭环，然后逐层加深。
>
> 里程碑：`M0 地基` → `M1 纵向薄切片` → `M2 加深分析` → `M3 加深检索` → `M4 加深蓝图` → `M5 加深拼装` → `M6 语料库产品化` → `M7 聚合知识` → `M8 复核工作流` → `M9 专家UI+打磨`。
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

以下数量采用本轮治理输入的固定基线，用于防止“检查点描述增长”被误读为任务完成。后续只有在对应原子任务实际完成并补齐证据时才更新数量；不得为迁就实现描述而虚假勾选。

| 里程碑 | 已完成 | 未完成 | 当前状态 | 当前判断 |
|---|---:|---:|---|---|
| M0 地基 | 26 | 9 | **S** | schema/契约基本可用；runner 级续跑已有验证，但 migration、projection 重建、job 级一致性和规模资产仍是基础债务 |
| M1 纵向闭环 | 38 | 1 | **S** | 跨库检索、多蓝图反馈、选定蓝图派生正文候选和章节面板已形成演示级闭环；明确只算薄切片完成，不升级为 P |
| M2 深度分析 | 22 | 21 | **S，加深中** | 10 family、分析入口、runner 级预算续跑和技法标本已有验证；持久后台调度、可靠恢复与长跑验收未完成 |
| M3 深度检索 | 11 | 7 | **S，加深中** | 多路召回、诊断和局部排序已有薄切片；未证明真实长篇召回质量、融合效果与性能 |
| M4 蓝图 | 12 | 8 | **S，加深中** | 多策略地基、coverage/gap/反馈降权已存在；未证明多份蓝图稳定且显著不同，也未形成真实效果证据 |
| M5 拼装 | 14 | 4 | **S，加深中** | 保真与阻断审计较深；完整多稿、自然过渡和真实修改成本证据不足 |
| M6 语料库产品化 | 5 | 0 | **S，冻结扩张** | 已有库作用域、授权、去重和审计的有限产品面；勾选项不证明真实多库治理、语义去重和完整授权工作流达到 P |
| M7 聚合知识 | 8 | 0 | **S，冻结扩张** | 已有聚合与溯源薄实现；底层分析质量、来源过滤、事务一致性及 M3-M5 消费闭环不足，不能视为可靠写作知识 |
| M8 复核工作流 | 7 | 0 | **S，冻结扩张** | 已有状态、队列和前端操作薄切片；复核上下文、原文证据导航、批量原子性和生产重跑语义仍不足 |
| M9 专家 UI | 4 | 0 | **S，冻结扩张** | 已有专家入口与受控前端流程；浏览器实证、恢复可靠性及底层能力成熟度不足，不能用 UI 完整度替代产品完成度 |

**状态解释：** M0-M9 当前统一为 `S`，没有任何里程碑达到 `P` 或 `L`。M1 是当前已成立的产品纵向薄切片，不等于真实用户产品闭环完成；M2-M5 处于加深中；M6-M9 虽有已勾选薄实现，但因依赖的分析、检索、蓝图和拼装质量尚未成熟，冻结新增功能，只修阻断性缺陷、安全问题和与主线兼容相关的回归。提交信息中的“M2 核心功能完成”只能解释为 M2 演示级 runner/入口成立，不能解释为生产后台或规模验收完成。

**整体对外口径：** “M1 产品薄切片完成，M2-M5 加深中，M6-M9 已有薄切片但冻结扩张；系统尚未达到生产完成或规模化完成。”任务勾选数量只能描述实现覆盖，不能换算为产品完成百分比或写作效果结论。

### 当前证据与升级缺口

| 里程碑 | 已有验收证据 | 升级仍缺 |
|---|---|---|
| **M0** | text node/observation/specimen/library/license/page 契约；幂等、fake embedding、确定性相似度和小 golden 骨架 | clause/章节窗口、存量 migration、projection 重建、resume cursor 全链路、级联失效、完整 500 句 golden、200 万字 fixture 与故障恢复 |
| **M1** | 多小说进入启用库；跨库检索；多蓝图和 feedback 二轮；selected blueprint 正文候选；golden JSON；章节面板 mock workflow | 剩余原子任务；真实桌面 UI 的完整失败/恢复回归；该闭环在 M2-M5 深化后的兼容回归。规模与效果证据归 M2-M5，不用扩大 M1 状态掩盖 |
| **M2** | 10 family schema、Stage 1/2 薄 runner、预算状态与部分续跑、低置信信号、Stage 3 技法标本入口和分析结果查阅 | 持久化后台队列、章节优先级、暂停/取消、可靠续跑、失败重试、巡检、重启恢复、全量/增量调度、进度反馈、200 万字长跑恢复 |
| **M3** | 文本/技法/结构化 observation/章节上下文 route provenance；结构化过滤；local fit；M3 retrieval golden；安全 scope/license/dedup 负例 | 独立索引与热路径、召回融合权重标定、人工标注 query 集、Recall@K/nDCG/命中原因准确率、P50/P95 延迟、几十万至两百万字跨库评测 |
| **M4** | emotion/rhythm/technique/scene profiles；coverage/gap positions；来源覆盖；历史反馈降权；候选持久化与反馈循环 | 情绪弧与更完整策略系统；多轮会话状态；蓝图区分度指标；同目标多方案盲评；重复方案率；反馈后方案改善率；真实长篇来源组合验证 |
| **M5** | selected blueprint 来源锁定；preserved/locked spans；slot/transition audit；blocked next_action 回蓝图；slot-only/部分 transfer slot 多稿薄切片 | 完整多草稿与过渡策略；自然语言 slot constraints；自然拼装评测；保真/适配/自然度平衡；用户修改字符比例；正文盲评和真实章节样本 |
| **M6** | 库作用域、来源状态、授权闸门、有限去重和插入审计已有定向路径 | 真实多库管理回归、跨版本/语义去重、授权变更全链传播、禁用来源工作流和大规模治理体验；冻结扩张期间只修缺陷 |
| **M7** | 聚合表、基础聚合、provenance 与 stale 传播已有薄实现 | 来源资格过滤、单事务构建、丰富结构化知识、质量评测，以及被 M3-M5 实际消费并证明改善；冻结扩张期间不新增聚合类型 |
| **M8** | review/validity 状态、队列、重跑保留与基础前端操作已有薄切片 | 原文预览、冲突值与 evidence、source/run 导航、批量原子性、缺失项错误语义、生产重跑与恢复；冻结扩张期间只修缺陷 |
| **M9** | 自动/专家入口、章节状态保存和受控 mock/verify 路径已有证据 | 真实桌面浏览器证据、跨章节恢复隔离、长任务状态可见性、完整溯源导航和依赖能力成熟后的用户验收；冻结扩张期间只修缺陷 |

### 强制实施顺序

1. **P0：恢复交付基线。** 全套构建与测试必须可编译、可通过；清理误入版本控制的 `build/tmp`、`bin/obj`、PDB 等生成产物。该入口条件未满足时不扩张功能。
2. **P1：以 M2 后台系统为唯一生产主线。** 完成持久化 job/run/attempt、冻结 work item、lease/CAS、fenced commit、token 结算、暂停/取消、预算续跑、retry/watchdog、重启 reconcile 和进度反馈；同步补齐其直接依赖的 M0 migration、projection 重建、幂等和事务一致性。
3. **P2：建立规模与效果证据资产。** 补齐 500 句标注集、200 万字跨库 fixture、故障注入/重启恢复脚本，以及检索 P50/P95、token/成本、蓝图区分度、原句保真率、剧情适配率、过渡自然度和用户修改字符比例。
4. **P3：补 M3 规模与真实效果。** 基于人工标注 query 集验证多路召回、融合排序、当前章节 local fit、去重和授权过滤；禁止以“route marker 出现”代替检索质量证明。
5. **P4：依次验证 M4 蓝图区分和 M5 保真拼装。** 多份蓝图必须产生可度量的结构与来源差异，用户选定后正文只能在蓝图来源边界内最大化复用、最小适配。**停止继续堆 M5 审计规则**；只有复现的安全漏洞、授权绕过或效果评测失败可以新增规则，且必须先落失败 fixture。

**顺序约束：** 首先保持全套构建/测试可编译可通过并清理会污染评审和发布的生成产物；随后以 M2 持久后台 job/run/attempt、fenced commit、暂停/取消/预算续跑、重试、巡检和重启恢复为唯一生产主线，同时补齐 M0 所需的一致性地基。然后建立 500 句标注集、200 万字 fixture、故障注入、检索 P95/token 成本及写作效果指标，再依次加深 M3 检索、M4 蓝图区分和 M5 保真拼装。M3 未有规模效果证据前，不把 M4/M5 的启发式变化写成质量提升；M4 未证明蓝图区分度前，不以更多正文候选冒充有效多稿；M5 未完成真实效果验收前，不宣称高质量正文已可用。M6-M9 保持 `S` 并冻结功能扩张，只允许修阻断性缺陷、安全问题和主线兼容回归。

---

## M0：地基 schema（防返工，最先做）

**目标：** 一次性把会导致后续返工的基础模型建对。此里程碑不实现业务逻辑，只落数据模型 + 契约 + 测试资产骨架。

### M0.1 文本节点树（DB / 修复 #1）

- [x] `reference_text_nodes` 建表 + 三个索引（parent/atype/chapter）
- [x] `reference_materials` / `reference_source_segments` 加 `node_id` FK
- [x] Stage 0 结构化写入器（M1 最小版）：真实导入 → text_nodes（章/场/段/句），填 offset/text_hash/sequence，并回填 source_segments/materials.node_id
- [ ] 从句级切分：补充 clause nodes 与 sentence→clause 父子关系
- [ ] 章节窗口查询辅助：给定 node，取前 N 章/同场景兄弟节点
- [ ] migration 幂等 + 存量库升级测试

**验收：** 导入 golden 书后，节点树父子/顺序/offset 正确；任一句节点能反查其所属段/场/章；text_hash 与源文逐字一致。

### M0.2 分层特征观察 + projection（DB / 修复 #2 #7）

- [x] `reference_feature_observations` 建表：`value_kind`/`value_num`/`value_bool`/`value_json` + `review_state`/`validity_state`/`superseded_by_run_id`
- [x] 三索引（family / num / node）
- [x] **幂等写入 schema guard（护栏 G1）**：`ux_obs_generation_key` UNIQUE，重复 observation 生成键由数据库拒绝
- [x] **确定性 observation identity（护栏 G1）**：`observation_id = hash(run_id,node_id,feature_family,feature_key,evidence_start,evidence_end)`，空 evidence 与 DB sentinel 对齐
- [x] **幂等 upsert 写入器（护栏 G1）**：分析 observation 写入用 `INSERT ... ON CONFLICT`；并发/重试/续跑不重复写
- [x] 热路径 projection 表：`reference_obs_sensory`（示范）+ Stage 1 写入同步
- [ ] projection 重建脚本：schema 演进/损坏修复时可从 active observation 重建热路径表
- [x] `reference_analysis_runs` 建表（含 token_budget/tokens_spent）
- [x] **runner 兼容预算状态（护栏 G2）**：现有同步 run 支持 `budget_exhausted + resume_cursor`，保证预算耗尽非 failed；`paused/partial_completed` 仅属旧 schema 兼容，不作为生产后台目标状态
- [x] **runner 级预算续跑接入（护栏 G2）**：同步 Stage 2/3 runner 从稳定 cursor 后继续；产物、token 与 cursor 在成功提交边界推进，非法 terminal resume/陈旧 cursor/跨 scope run id 在写入前拒绝
- [x] **后台 job store 与事务围栏薄切片（护栏 G2）**：持久化 canonical run/input snapshot/work item/job/attempt；CAS pause/resume/cancel/reprioritize；lease/heartbeat/reclaim；token reservation；retry/budget/control settlement；产物、work item、job、attempt、run 同事务 fenced commit
- [ ] **后台 job 级生产续跑接入（护栏 G2）**：scheduler 冻结完整 feature context/technique evidence，worker 只消费 frozen payload，启动 reconcile 后从稳定 work-item cursor 继续；当前 store 能力尚未接入真实后台生命周期

**验收：** 能按 value_num 范围、按 sensory 投影表数组查询；review_state 与 validity_state 独立可设；**同 (run,node,feature) 并发/重试只落一条 active observation**；预算耗尽置 `budget_exhausted` 而非 failed，补预算可从 resume_cursor 续跑。

### M0.3 技法标本 + junction（DB / 修复 #8）

- [x] `reference_technique_specimens` 建表（含 review/validity/superseded）
- [x] junction：`reference_specimen_evidence` / `reference_template_examples` / `reference_blueprint_beat_pieces`
- [ ] 级联失效查询：给定 superseded 的 observation，定位受影响 specimen/beat

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
- [ ] **golden fixture 完整版**：小 golden 书（约 500 句，含授权标注）纳入版本库
- [ ] **规模 fixture 生成器**：合成 200 万字级语料（供性能/恢复测试）
- [ ] 中断恢复测试脚本骨架

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
- [ ] material embedding 构建任务：Stage 0/legacy material 与 text_nodes 对齐后补齐 material 级索引
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
- [x] 后端 bridge + TS adapter：`GenerateReferenceCorpusBlueprintCandidates` 多蓝图候选入口可被章节流程调用，契约包含 `feedback_applied`/`feedback_summary`
- [x] 后端 bridge + TS adapter：`GenerateReferenceCorpusInsertionDraftCandidates` 正文多候选入口可被章节流程调用，契约包含 `selected_blueprint`/`candidates[].draft`
- [x] 写作会话最小界面：大纲输入 → 一份蓝图 → 一份草稿 → 插入编辑器 buffer（当前单路径检查点）
- [x] 自动模式多蓝图界面薄切片：章节目标 → 多份蓝图候选卡 → 用户选择/反馈重试 → 按选中蓝图生成插入草稿 → 插入编辑器 buffer
- [x] 正文候选界面：接受蓝图后生成正文候选列表，选择候选后预览 diff 并插入编辑器 buffer；当前若 selected blueprint 每拍只有单一 node，候选可只有一份，不允许用换料凑多份
- [x] 全部专家控件默认隐藏，用 AI 默认决策
- [x] 草稿 diff 预览（原句保留 vs 槽位替换标色）

**当前检查点：** 章节编辑器右侧 `参考素材` 面板已接入真实 UI 多蓝图 + 正文候选薄切片。前端从 Monaco 读取当前正文与光标 offset，发送当前章节默认 `session_id`，不再把默认 library id 列表硬编码为章节参数；后端解析 `project:{novelId}:default` 与工作区公用语料。默认路径先调用 `GenerateReferenceCorpusBlueprintCandidates` 生成多份蓝图候选，用户可在候选卡中选择方案、以当前选中蓝图构造 feedback 重组第二轮，再点击“按选中蓝图生成草稿”；章节 UI 随后调用 `GenerateReferenceCorpusInsertionDraftCandidates`，payload 必须携带第二轮候选中的 `selected_blueprint` 和 `requested_count=3`。生成后展示正文候选列表，用户选择候选后再查看 diff、节点分析、技法标本、槽位替换和插入闸门；diff 预览优先使用后端 `preserved_spans` 标记非槽位保留片段，旧 `slot_replacements` 反推逻辑只作兜底。只有当前选中候选的 `ready_for_insertion && gate.passed && audit.passed` 时才允许把该候选的 `chapter_text_after_insertion` 应用到编辑器 buffer，且不直接调用 `SaveContent`。mock workflow 已改为真实 UI 点击触发首轮/二轮蓝图候选、正文候选、选择候选并应用，guardrail 校验 `feedback_applied=true`、`feedback_summary`、`selected_blueprint` 来源、每个正文 piece 必带 `preserved_spans`、`audit.passed=true` 且 `audit.pieces` 与 draft pieces 对齐，并用 exact property 检查不泄漏 `source_text/raw_text/embedding`。推荐素材、事实边界和旧严格流程默认收进“高级参考流程”，展开前不会触发 `SearchReferenceMaterials` 或 orchestration 读取。该检查点证明默认 session scope、多蓝图 UI 循环、选中蓝图正文候选和编辑器 buffer 插入薄切片可用，但不等于完整产品完成；M1 后端 golden 已覆盖跨库、多蓝图、正文候选闭环，后续仍需继续加深分析、检索、蓝图和拼装质量。

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

- [ ] Task A 句级 LLM：全量、异步、按章节优先级排队、per-run token_budget、可续跑
- [ ] Task B 段落级 LLM：全量、异步队列、章节优先级排队
- [ ] 持久化后台调度 schema：run/job/attempt + input snapshot/work item，additive migration，不并行维护两套 canonical run
- [ ] job 状态机与 CAS：queued/running/pause_requested/paused/cancel_requested/cancelled/retry_wait/budget_exhausted/completed/failed + expected_version
- [ ] worker 运行协议：原子 claim、lease token、10 秒 heartbeat、45 秒 lease、15 秒 watchdog、启动 reconcile、失去 lease 禁止提交
- [ ] 章节优先级与 aging：current/adjacent/normal/maintenance 持久化优先级，5 分钟 aging，持续高优先级流量下 normal 不饥饿
- [ ] pause/cancel/resume 独立 API：幂等命令、成功提交边界生效、取消保留产物、补充总预算后 resume、非法转换稳定错误码
- [ ] retry 分类与退避：schema 0/1/3 秒最多 3 次；provider transient full-jitter 最多 5 attempt；Retry-After；永久错误不重试
- [ ] 后台任务查询与进度：PageResult 稳定分页，node/work-item 双分母，token/reservation、当前章节、allowed_actions、安全 diagnostics
- [ ] 重启恢复与故障注入：调用前/返回后/事务中/cursor 后/完成前/真实进程强杀，证明零重复 active 产物、零丢失、cursor 单调
- [x] 产品触发入口：`StartReferenceCorpusFeatureAnalysis` / `GetReferenceCorpusFeatureAnalysisRun`，按 anchor + scope 启动 sentence/passage 默认 family 分析并返回安全 run 状态
- [x] Task B 段落节点选择 + 上下文地基：只分析 `reference_source_segments.segment_type='paragraph'` 的真实段落，跳过 hook/beat/action_afterbeat 等派生 passage；注入 parent chapter、containing scene、前后 paragraph context；旧库缺 `reference_source_segments.node_id` 时诊断并跳过，不做 hash/offset 猜测
- [x] Task A/B 共用执行器地基：读取 node×family、调用可替换 analyzer、locked schema 校验、observation upsert、tokens/resume_cursor/status 更新、sensory projection 同步
- [x] schema 失败重试：invalid JSON/schema/rejected output 按 `MaxValidationAttempts` 重试；失败尝试累计 tokens 但不写 observation、不推进 cursor；重试耗尽后 run 标 failed
- [x] 真实 LLM analyzer 地基：复用 `IChatCompletionClient` + selected model，生成 schema-locked prompt，抽取 fenced JSON，读取 usage tokens；测试使用 fake chat client，不触发真实网络
- [x] confidence < 0.70 的 observation 初始化为 `review_state='low_confidence'`，分析查阅列表可按该状态过滤，作为 M8 复核队列输入信号
- [ ] evidence_start/end 精确记录
- [ ] 低置信 observation 的 ReviewQueue 入队/领取/人工确认流转（M8 消费）
- [ ] 后台中断续跑：从 input snapshot 的最后成功 work item 续跑，应用重启/lease 回收后 cursor 单调且不整章重放

**当前检查点：** `ReferenceCorpusFeatureAnalysisRunner` 已用 fake analyzer 跑通 node×family 执行链：预算耗尽时停在当前 cursor，补预算后从下一项续跑；最后一项正好用完预算时正确收敛为 `completed`；写入通过 locked schema validator 的候选 observation，并同步 `reference_obs_sensory`。句级 Task A 传空 context；段落级 Task B 只读取真实 paragraph source segment，避免 `node_type='passage'` 混入 hook/beat/action_afterbeat 等派生节点，并给 analyzer 注入 parent/chapter/containing scene/前后 paragraph 的 bounded context。`ReferenceCorpusChatCompletionFeatureFamilyAnalyzer` 已接入现有 chat completion 抽象，prompt 包含 schema descriptor、node 元数据、bounded node_text 与安全压缩后的 `analysis_context`；system prompt 明确 `node_text` 是唯一 evidence 来源，context 只能辅助判断，不能用于 evidence offset。usage token 会回填 runner 预算记账。产品入口已接入 bridge/TS adapter/mock：`StartReferenceCorpusFeatureAnalysis` 读取 selected model、校验 anchor 可访问性、按 `scope=sentence|passage` 派生默认 family 并启动 runner；`GetReferenceCorpusFeatureAnalysisRun` 返回 run 元数据、tokens、cursor、observation_count 和 diagnostics，返回体不包含 `node_text/source_text/raw_text/prompt/model_output_json/embedding`。低置信度 observation 现在以 `confidence < 0.70` 初始化为 `review_state='low_confidence'`，并可在素材库“分析结果”tab 用 review_state 过滤；这只是复核路由信号，不等于完整 ReviewQueue 或人工状态机。当前入口仍是一次调用内执行的薄触发，不是后台队列；异步队列、章节优先级调度、取消能力仍未接入。

### M2.3 Task C 技法标本（后端 / 关键设计）

- [ ] 综合推理：全部 A/B observation + 原文 → TechniqueSpecimen
- [x] Stage 3 runner/validator/writer 地基：读取 active 高置信度 observation + 原文节点，调用可替换 analyzer，写 `reference_technique_specimens`
- [x] why_it_works 每 contributing_factor 走 `reference_specimen_evidence` FK 到真实 observation（禁空引用/未知 id）
- [x] technique_abstract / transfer_template 去内容化泄露检测（拒绝原文专名、原文动作短语、长原文片段）
- [x] Stage 3 仅高置信度节点触发（低于阈值 observation 不进入 analyzer 输入）
- [x] 真实 LLM analyzer：复用 `IChatCompletionClient`，schema-locked prompt，抽取 fenced JSON，读取 usage tokens
- [x] 产品触发入口：`StartReferenceCorpusTechniqueSpecimenAnalysis` / `GetReferenceCorpusTechniqueSpecimenAnalysisRun`，返回安全 run 状态
- [x] runner 级预算续跑：零预算不调用模型，budget_exhausted 后提高总预算，从最后成功 node 继续；specimen/evidence/token/cursor 同事务
- [ ] Stage 3 后台 job 接入：依赖快照、异步队列、章节优先级、pause/cancel/resume、lease/watchdog、retry、重启恢复

**当前检查点：** `ReferenceCorpusTechniqueSpecimenRunner` 已作为独立 Stage 3 地基接入：按 source node 聚合同 anchor、同 node_type、active、未被人工拒绝且 `confidence >= MinObservationConfidence` 的 observation，把 node 原文和 observation evidence 交给 analyzer；`ReferenceCorpusTechniqueSpecimenOutputValidator` 锁定 `reference-corpus-technique-specimen-v1` 输出，要求 `why_it_works` 每个 factor 至少引用一个真实 observation id，未知/空 evidence 直接拒绝；落库在同一事务内写 `reference_technique_specimens`、`reference_specimen_evidence`、tokens 和 cursor，`specimen_id = hash(run_id,node_id,technique_family)` 保证重试幂等，重跑保留人工 `review_state`，不把 confirmed/rejected 重置为 unverified。`reference_specimen_evidence(observation_id, specimen_id)` 索引用于后续从 superseded observation 反查受影响 specimen。去内容化闸门会拒绝 `technique_abstract` / `transfer_template` 中出现原文专名、原文动作短语或长原文片段。真实 `ReferenceCorpusChatCompletionTechniqueSpecimenAnalyzer` 已接入 selected model + `IChatCompletionClient`，prompt 明确 node_text 仅作源材料、输出必须去内容化且 why_it_works 引用 observation_id；fenced JSON 与 usage token 读取有回归测试。产品面已有薄入口 `Start/Get ReferenceCorpusTechniqueSpecimenAnalysis`，返回 run 元数据、token_budget/tokens_spent/resume_cursor、specimen_count、processed_nodes 与安全 diagnostics，不返回 node_text/source_text/raw_text/prompt/model_output_json/embedding/value_json。Stage 3 已接单次调用内的预算续跑：零预算不调用模型，预算耗尽后补充总预算可从最后成功 node 续跑，非法 terminal resume、陈旧 cursor、跨 scope/anchor run_id 在写入前拒绝。当前仍未接持久化后台队列、章节优先级、暂停/取消、重启恢复、失败重试、任务巡检和前端 TechniqueSpecimen 专项卡。

### M2.4 分析前端

- [ ] 后台任务面板：稳定分页展示 10 个 job 状态、node/work-item 双进度、token、当前章节、重试倒计时和安全诊断
- [ ] 后台控制交互：仅按后端 `allowed_actions[]` 提供暂停/取消/恢复/重试，携带 expected_version，CAS conflict 后刷新，不在前端复制状态机
- [x] 后端列表 API：`ListReferenceCorpusFeatureObservations` / `ListReferenceCorpusTechniqueSpecimens`，分页 `PageResult<T>`、稳定 sort、filter 白名单、默认 active、非法 cursor/filter/pageSize 走 validation error
- [x] 安全展示契约：Observation 不暴露 `value_json`；TechniqueSpecimen 不暴露 `why_it_works_json` 或原始 JSON 字符串，改为 typed `transfer_slots` / 条件列表 / `why_it_works.contributing_factors`
- [x] evidence trace：TechniqueSpecimen 通过 `reference_specimen_evidence` junction 二次读取，不用 join 放大分页；trace 返回 observation id/family/key/confidence/text_hash/bounded evidence preview/value preview/explanation
- [x] 当前章节嵌入：章节右侧 `语料驱动草稿` 生成后，按 draft pieces 的 `anchor_id/node_id` 自动加载“节点分析 / 技法标本”，可切换 piece，不把素材库处理入口混进章节使用面板
- [x] 全库分析查阅 tab：素材库处理侧新增独立“分析结果”tab，可按 anchor、node、family/key、review/validity、min confidence 查阅 observation/specimen；不复用章节使用面板，不提供蓝图/候选/插入动作
- [ ] evidence 跳原文：当前只返回 bounded evidence preview 与 offset，尚未接编辑器/素材库原文定位跳转

**验收（双轨，修复 #3 #12）：**
- 正确性轨：golden 书全量分析比对 golden JSON
- 规模轨：200 万字 fixture 验证续跑/预算/性能/恢复
- why_it_works 每条可追溯；abstract 泄露检测通过
- [ ] 量化后台验收：5 个事务故障点零重复/零丢失；真实进程强杀恢复 2 次；P95 pause/cancel ≤ 60 秒；stale lease 30 秒内恢复；预算穿透 0 token
- [ ] 量化规模验收：200 万字、多 anchor/library、句段全量；fake LLM 单 worker ≥ 20 work items/s；claim P95 ≤ 100 ms；任务列表/章节进度 P95 ≤ 200 ms；normal job 等待 ≤ 15 分钟
- [ ] UI/API 验收：10 个 job 状态中文显示、allowed_actions、CAS conflict、稳定分页和应用重启后任务可见均有自动 workflow

**当前检查点：** M2.4 已完成后端分页读取、章节使用侧只读嵌入、素材库处理侧独立“分析结果”tab。`SqliteReferenceCorpusAnalysisService` 先分页 specimen 再读取 evidence junction，避免 evidence join 导致分页重复；Observation list join `reference_text_nodes` 只返回 `text_hash` 和 bounded evidence preview，不返回 node 全文。Bridge/TS adapter/mock 已补齐两个 list 方法，`ChapterReferencePanel` 在语料草稿下根据当前 pieces 自动加载 observation/specimen，界面只暴露节点切换与刷新；`CorpusAnalysisLibraryTab` 则在素材库内按 anchor/node/filter 查阅 observation/specimen，并通过 mock workflow 断言不会触发章节蓝图、候选生成或插入类 bridge。仍未实现 evidence offset 跳转到原文定位。

---

## M3：加深检索（四类索引融合 + 当前章节上下文进排序）

- [x] `reference_technique_vectors` JSON fallback 投影表 + technique abstract embedding 薄切片：已通过 session/library/license/dedup 的节点若有 active/non-rejected `reference_technique_specimens`，可越过每来源前 N 预取窗口补进候选池；缓存 `technique_abstract + trigger_context + transfer_template + effect_on_reader` 向量，并输出 `score_components.technique_fit`
- [x] `reference_technique_vectors` native sqlite-vec topK 薄切片：新增 `reference_technique_vector_rows` / `reference_technique_vector_index_state`，以 JSON fallback cache 为 canonical source，构建 scoped native vec0 index；native 命中只作为 `scoped_nodes.node_id IN (...)` 召回 hint，仍经过 session/library/license/dedup/include/exclude/reuse 与结构化 filters；native 不可用或查询失败时回退旧 JSON fallback
- [x] native sqlite-vec 后台回填薄切片：新增 `BackfillReferenceCorpusTechniqueVectorIndex` / `BackfillTechniqueVectorIndexAsync`，可不经 `SearchCandidates` 显式预热 scoped technique vec0 index；复用同一套 scope/source_hash/row signature 校验，返回 `ready/empty/skipped/failed`、provider/model/dim、source/vector/skipped 计数和诊断；搜索会复用已回填 rows/state，不重复 provision
- [ ] native sqlite-vec 回填规模化：后台队列、全量/增量调度、失败重试、provider/model/dim 巡检报表、规模 fixture 与性能预算
- [x] 四路召回合并薄切片：在 session/library/license/dedup 安全 scope 后，base prefetch 之外的文本语义 / 技法语义 / 结构化 observation / 章节上下文 route 可补入候选池；分页时保留各 route 代表，并在 `score_components` 暴露 `recall_text_semantic` / `recall_technique_semantic` / `recall_structured_observation` / `recall_chapter_context`
- [x] 结构化 observation 独立召回薄切片：`ReadStructuredObservationRecallNodeIdsAsync` 在 scoped 安全集合上独立读取 observation route node ids，支持 QueryContext term、`feature_filter_{n}_*`、旧 `feature_*` 与 sensory filters；route hit 作为内部 hint 写入候选，`recall_structured_observation` 只表示真实 route 命中，不再由 `observation_fit` 评分 winner 误标
- [x] 章节上下文独立召回薄切片：`ReadChapterContextRecallNodeIdsAsync` 在 scoped 安全集合上独立读取当前插入窗口、previous summary、人物快照与 allowed knowledge 命中的 node ids，先应用结构化 filters 再按 context term 权重排序并设置最小加权阈值；`recall_chapter_context` 只表示真实 context route hit，不再只由 `local_context_fit` 最高候选误标
- [ ] 完整四路召回：文本语义 topK / 技法语义完整 topK 独立取数后再 union；结构化 observation 与章节上下文已有独立 SQL route，但仍需 observation projection 热路径排序、context 规模化索引、规模 fixture、权重标定和统一 route provenance
- [x] 结构化 observation 过滤薄切片：`SearchCandidates` 支持 `feature_family`/`feature_key`/`feature_value_text`/`feature_value_num_min|max` 与 `sensory_sense`/`sensory_min|max_intensity`，走 `reference_feature_observations` + `reference_obs_sensory` EXISTS 过滤
- [x] 多 feature observation AND 过滤薄切片：`feature_filter_{n}_family/key/value_text/value_num_min|max` 可表达多个独立 observation 条件，全部按 AND 语义命中；保留旧 `feature_*` 单条件过滤兼容
- [x] 章节蓝图接入结构化过滤薄切片：`GenerateReferenceCorpusBlueprintCandidates` 根据当前目标把“动作替代心理描写表现愤怒 + 触觉”等意图映射为候选检索 filters，后续 selected blueprint → draft 仍按选定节点取材，不二次误过滤
- [x] 当前章节上下文排序薄切片：后端计算并缓存当前章节 embedding，同时新增 `score_components.local_context_fit`，只从插入点附近文本、previous summary、人物名/state 和 allowed knowledge 提取确定性短词；`ForbiddenKnowledge` 不进入正向检索信号
- [x] M3 检索 golden 薄切片：`m3-retrieval-golden.json` 固化 licensed scoped search、四路召回诊断、安全排除、缓存计数和候选排序；normalized expected 只保留 `text_hash`、`text_preview_hash`、长度、score component key、route marker 与 evidence，不写入原文、embedding、prompt、`value_json` 等内部/源文字段
- [ ] 完整融合排序：当前章节 embedding 连贯度 + 插入位置匹配 + 授权/质量/跨库来源多样性加权与权重标定（修复 #5）
- [ ] 权重可被检查表反馈调整（M4 消费）
- [ ] 跨语料检索（按 session 绑定的所有启用 library 展开，非单 anchor；默认不要求用户选择一本参考）
- [ ] 跨库去重与来源覆盖：dedup_group 折叠后仍保留可解释的来源分布，低质量来源降权但不污染授权过滤
- [ ] 全部返回 `PageResult<T>`

**验收：** 结构化查询（"动作替代心理描写表现愤怒"）精准命中 golden fixture 中标注样本；当前章节上下文改变时排序可见变化；技法语义相似区别于文本相似（golden 断言）；同一 QueryContext 在多库启用时召回跨 anchor 结果，禁用任一库、改变授权或 dedup_group 后候选集合/排序按 golden 预期变化；测试不得只覆盖单 anchor。

**当前检查点：** `SearchCandidatesFiltersByStructuredObservationAndSensoryProjection` 已证明候选搜索不再只是纯向量召回：同一授权语料库内，只有同时满足 `action.emotion_carrier=action_over_psychology` 与 `sensory=tactile,intensity>=0.8` 的句子会命中，用于覆盖“动作替代心理描写表现愤怒”这类结构化意图。`SearchCandidatesRequiresAllIndexedStructuredObservationFilters` 进一步证明多个 `feature_filter_{n}_*` observation 条件会同时生效，可表达 `action.emotion_carrier=action_over_psychology AND rhythm.length_band.char_count>=16` 这类复合意图，并且旧 `feature_*` 单条件过滤继续兼容。`GenerateBlueprintCandidatesUsesStructuredObservationFiltersFromGoal` 证明章节蓝图路径已把目标映射到这些结构化 filters，跨 project/global 两库只从匹配节点生成蓝图，并且 selected blueprint 后续生成正文不再被二次过滤破坏。`SearchCandidatesRanksInsertionWindowAndAllowedKnowledgeContext` 证明当前章节上下文已进入可解释排序：`local_context_fit` 会让命中插入点附近文本、人物名和 allowed knowledge 的候选排到更前，同时断言 `ForbiddenKnowledge` 不得作为正向检索信号。`SearchCandidatesMergesFourRecallRoutesWithDiagnostics` 证明 route union 已能补入 base prefetch window 外的文本语义、技法语义、结构化 observation、章节上下文代表，分页不会只按单一融合分数截断，并用 `score_components.recall_*` 标注召回来源；`SearchCandidatesStructuredObservationRecallDoesNotDependOnBasePrefetchWindow` 与 `SearchCandidatesStructuredObservationRecallUsesExplicitFeatureFiltersBeyondPrefetchWindow` 进一步证明结构化 observation 已有独立 SQL route，可分别通过 QueryContext term 和显式 `feature_filter_*` 把远位置节点拉入候选，且 `recall_structured_observation` 只表示真实 route hit，不再由普通 `observation_fit` winner 误标；`SearchCandidatesChapterContextRecallMarksEveryRouteHitBeyondScoreWinner` 证明章节上下文也已拆出独立 SQL route，多个 context route hit 都会获得 `recall_chapter_context`，不再只给 `local_context_fit` 第一名打标，并用最小加权阈值避免弱泛词误标；`SearchCandidatesChapterContextRecallHonorsStructuredFiltersBeforeRouteLimit` 证明 context route 会先应用结构化 filters 再取 topK，避免 route limit 被不满足 filter 的上下文噪声占满；`SearchCandidatesMergedRecallRoutesHonorScopeLicenseAndDedup` 进一步证明 route union 不能绕过 excluded anchor、forbidden license 或 dedup representative。`SearchCandidatesRainDoorwayMatchesM3RetrievalGoldenJson` / `SearchCandidatesFourWayRecallMatchesM3RetrievalGoldenJson` 已把基础授权检索与四路召回诊断固化到 `m3-retrieval-golden.json`，并由 `CorpusDrivenWritingGoldenFixtureTests` 递归防止 expected 区块泄露 raw source、embedding、prompt、`value_json` 等字段。`SearchCandidatesRanksTechniqueAbstractEmbeddingSeparatelyFromRawText` 证明技法 abstract embedding 已区别于原文 embedding 参与排序：同一候选池内，`technique_fit` 可把“动作替代心理描写表现愤怒”命中到对应技法标本节点；向量按 provider/model/dimensions/technique_hash 缓存，第二次检索只重算 query，不重算技法向量。`SearchCandidatesUsesNativeTechniqueTopKWhenAvailable` 证明 native sqlite-vec topK 已进入技法召回薄切片：rowid 映射回 source node 后只作为 `scoped_nodes` 内的召回 hint，命中的远位置技法节点可进入候选并带 `recall_technique_semantic`；`BackfillTechniqueVectorIndexPrewarmsNativeRowsAndSearchDoesNotReprovision` 证明 `BackfillReferenceCorpusTechniqueVectorIndex` 可在不调用 `SearchCandidates` 的情况下预热 `reference_technique_vectors`、native row mapping 与 index state，后续搜索复用 rows/state 且不重复 provision；`BackfillTechniqueVectorIndexFailureReturnsDiagnosticsAndSearchStillFallsBack` 证明回填失败返回 `failed` 诊断，不破坏后续搜索的 JSON fallback。`SearchCandidatesFallsBackWhenNativeTechniqueTopKFails` 证明 native provision/query 失败不会让搜索失败，会回退旧 JSON fallback；`SearchCandidatesNativeTechniqueTopKHonorsExcludedAnchors` 证明 excluded anchor 不会被 native 路径建索引、写 technique cache 或返回；`SearchCandidatesNativeTechniqueTopKDoesNotBypassStructuredFilters` 证明 native 命中仍受 `feature_*` / sensory 过滤约束；`SearchCandidatesNativeTechniqueTopKClearsRowsForRejectedSpecimens` 证明 active specimen 被人工 rejected 后，该 scope 的 native row/state 会被清掉，旧 rowid 不再污染后续检索；`SearchCandidatesNativeTechniqueTopKRebuildsStaleRowHash` / `SearchCandidatesNativeTechniqueTopKRejectsForgedRowMappingWhenIndexStateMatches` 证明 index_state 不能只靠 count/hash 欺骗，row 映射与当前 entries 不一致会触发重建；`SearchCandidatesNativeTechniqueTopKExcludesActiveSpecimenWithSupersededRunId` 证明 dirty active + superseded specimen 不参与 native 或 JSON fallback。`SearchCandidatesRecallsTechniqueSpecimenBeyondPerSourcePrefetchWindow` 继续覆盖无 native provider 时的 JSON fallback 远位置技法补池。新增护栏覆盖：技法补池不得绕过结构化 filters；被 `exclude_anchor_ids` 排除或 forbidden license 的技法标本不返回也不生成向量缓存；selected blueprint 若引用远位置技法节点，`GenerateInsertionDraftAsync` 后续仍能读到 source piece，不退化为 `source_node_missing`。`ReferenceCorpusServiceTests` 与 `ReferenceCorpusWritingServiceTests` 整组仍覆盖 session 跨库、禁用库、授权过滤、dedup 折叠、embedding 缓存、M1/M3 golden 和正文候选。当前仍未完成文本语义完整独立 topK、完整反馈权重映射、规模 M3 golden、native 回填队列化/全量增量调度、结构化 observation/context 热路径排序与性能预算。

---

## M4：加深蓝图（N 策略 + 检查表反馈）

- [ ] 完整 `ICorpusBlueprintAssembler` 多策略：情绪优先/节奏优先/技法多样性/场景模板（含情绪弧、会话状态、专家检查表闭环）
- [x] 多候选 assembler 地基薄切片：新增 `IReferenceCorpusBlueprintCandidateAssembler` / `MultiStrategyReferenceCorpusBlueprintCandidateAssembler`，`GenerateBlueprintCandidatesAsync` 只负责检索、反馈读写和候选持久化；M4 profile/coverage/gap 组装逻辑不再散落在 `SqliteReferenceCorpusWritingService` 私有排序 helper 中
- [ ] 覆盖率计算 + gap 识别 + 情绪弧线预估
- [x] 蓝图表扩展薄切片：`reference_corpus_blueprints` 持久化 `assembly_strategy`/`coverage_score`/`gap_reasons_json`/`gap_positions_json`/`query_context_json`/`source_distribution_json`/`feedback_reason`
- [x] corpus beat 父表薄切片：`reference_corpus_blueprint_beats` 持久化 `beat_index`/`role_in_beat`/`narrative_function`，不复用旧 anchored-draft `reference_chapter_blueprint_beats`
- [x] beat→node 追溯薄切片：`GenerateBlueprintCandidatesAsync` 候选阶段即写 `reference_blueprint_beat_pieces`，正文草稿阶段继续复用同一 upsert；追溯边不放 JSON
- [ ] `GenerateChapterBlueprintAsync` 改造：Parser + Retriever + Assembler
- [ ] OrchestrationStages 加 goal_parsing/corpus_retrieval/blueprint_assembly
- [ ] **legacy 兼容（护栏 G6）**：旧 run/blueprint/frontend 状态只读可恢复；新旧 stage 常量并存 + 兼容 shim；不破坏 Phase 16 reconcile/recovery；旧 blueprint 可选一次性归档脚本（不自动改写）
- [x] 检查表反馈契约 + `reference_user_feedback` 持久化薄切片：二轮蓝图反馈会以 `target_type=blueprint`、被拒蓝图 id 为 `target_id` 写入 `reference_user_feedback`，保留 problem tags、rejected/avoid 计数、fallback 诊断和用户 note；同一反馈重试使用确定性 `feedback_id` 幂等写入，不重复污染偏好历史
- [x] 检查表反馈 → 检索约束薄切片：`too_fast` 映射到 `rhythm.length_band` 中长句约束；`too_direct_emotion` 继续映射到 `action.emotion_carrier=action_over_psychology`；有命中时重新检索并改变蓝图候选，若反馈硬约束无命中则退回目标基础检索以保持循环不中断
- [x] 同小说历史反馈软降权薄切片：无显式反馈的新一轮蓝图生成会读取 `reference_user_feedback` 中已拒绝蓝图的 `node_hash` / `library_hash` / `anchor` 信号，把相同节点集合和相关来源降到后排；空 feedback object 按无显式反馈处理，不会污染策略名或阻断历史学习
- [x] M4 策略权重薄切片：当候选池有足够 feature/technique 信号时，内部 profile 会生成 `emotion_priority_m4` / `rhythm_priority_m4` / `technique_diversity_m4` / `scene_template_m4` 四类蓝图候选，按候选 evidence 与 score_components 排序，并先做跨 library/anchor 代表选择，避免 N 策略只改名不换素材
- [x] 蓝图 gap 诊断薄切片：`gap_reasons` 同时区分 `insufficient_beats` / `single_library_source` / `single_anchor_source`，避免多句或多策略候选实际塌缩到同一参考 anchor 时只暴露模糊的单库问题
- [x] M4 coverage/gap 证据化薄切片：候选已有 M4 evidence 时，`coverage_score` 不再只包装检索分，会纳入 emotion/rhythm/narrative/technique 覆盖；缺维度时输出 `missing_emotion_evidence` / `missing_rhythm_evidence` / `missing_narrative_evidence` / `missing_technique_coverage`
- [x] M4 profile 选材补齐薄切片：四类 M4 profile 不再只按单一 profile 分数取前三，而是在保留策略头部素材后主动补齐缺失的 emotion/rhythm/narrative/technique 证据，并优先选择能增加 library/anchor 覆盖的候选；历史反馈 penalty 仍优先于覆盖强度
- [x] M4 beat 级缺口返回薄切片：候选返回体新增 `gap_positions[]`，把全局缺失的 emotion/rhythm/narrative/technique 维度定位到具体 `beat_id/beat_index/node_ids`，前端候选卡可直接显示“第几拍缺节奏/叙事/技法”；若整份蓝图已覆盖完整则不误报位置缺口
- [ ] 多蓝图迭代循环：用户拒绝/选择/勾选问题后，系统以反馈更新 QueryContext/权重/约束并重新检索、重组，直到用户接受
- [ ] 蓝图来源分布：每份蓝图记录跨 library/anchor 的来源覆盖，避免所有策略意外塌缩到同一 anchor
- [ ] 前端专家模式：蓝图卡列表（情绪弧折线/节奏色块/覆盖率/来源分布/gap 标红）+ 拒绝检查表

**验收：** N 份蓝图策略对 golden fixture 产出可断言的不同分配；检查表勾选后被勾维度有可见变化（golden 前后比对）；gap 标注对"无合格语料"场景正确；至少一次“生成多蓝图 → 用户拒绝/反馈 → 再检索/重组 → 新蓝图候选”的循环有 golden 断言；多库启用时蓝图可引用多个 anchor，且不会退化为单 anchor。

**当前检查点：** `GenerateBlueprintCandidatesUsesInjectedCandidateAssemblerAndPersistsResult` 证明蓝图候选生成已走可替换的 `IReferenceCorpusBlueprintCandidateAssembler`，service 只负责编排、反馈读写和 `reference_corpus_blueprints` / beat / beat→node 持久化。`GenerateBlueprintCandidatesMapsTooFastFeedbackToSlowRhythmRetrieval` 覆盖 M4 反馈薄切片：首轮可命中高相关但短促的 `node-feedback-fast-market-s1`，二轮用户勾选 `too_fast` 后，检索增加 `rhythm.length_band.value_num >= 16`，并优先生成 `rhythm_slow_m1`，第一候选同时使用 project/workspace 慢压迫节点；命中成功时不会误报 fallback。`GenerateBlueprintCandidatesProducesM4StrategyVariantsFromFeatureSignals` 证明当候选池具备 emotion/rhythm/narrative/technique 信号时，`MultiStrategyReferenceCorpusBlueprintCandidateAssembler` 能产出四类 M4 strategy variant，四份蓝图 node set 互不相同，且每份都保持跨库来源、不出现 `insufficient_beats` / `single_library_source` / `single_anchor_source`。`GenerateBlueprintCandidatesPersistsM4BlueprintMetadataAndBeatPieces` 证明候选生成阶段已落 `reference_corpus_blueprints`、`reference_corpus_blueprint_beats` 与 `reference_blueprint_beat_pieces`：assembly strategy、coverage、gap reasons、gap positions、query context、source distribution、feedback reason、beat index/role/narrative function 和 beat→node 边都可追溯，且不等待正文草稿生成。`GenerateBlueprintCandidatesScoresM4CoverageByRequiredDimensionEvidenceAndReportsGaps` 证明 coverage 已开始消费 M4 维度证据：维度完整的 M4 蓝图会高于只带 emotion/action 的普通高语义分蓝图，残缺蓝图会报告缺 rhythm/narrative/technique 覆盖，并通过 `gap_positions[]` 定位到具体 beat；完整蓝图不会误报总缺口或位置缺口。`GenerateBlueprintCandidatesBackfillsM4StrategyCandidatesWithCoverageEvidence` 证明 `emotion_priority_m4` 不会继续拿三条 emotion-only 高分句子冒充可用蓝图，而会主动补进 rhythm 和 narrative+technique 支撑节点，且补齐过程仍保持跨 library/anchor。`GenerateBlueprintCandidatesFeedbackFilterFallbackAddsDiagnosticGapReasons` 证明反馈硬约束无命中时，系统退回目标基础检索但会把 `feedback_filters_no_matches` / `fallback_to_base_filters` 写入 `feedback_summary`、候选 `feedback_reason` 和 `gap_reasons`，用户能看出反馈没有精准命中。`GenerateBlueprintCandidatesAvoidSourceFallbackAddsDiagnosticGapReasons` 证明避开来源把候选池打空时，系统会保留循环不中断，同时标注 `avoid_sources_no_alternatives` / `fallback_ignored_avoid_sources`。`GenerateBlueprintCandidatesPersistsBlueprintFeedbackForReuse` 证明二轮反馈会写入 `reference_user_feedback`，记录 rejected node、avoid library、avoid anchor 的可复用信号，并且同一反馈重试不会重复写。`GenerateBlueprintCandidatesHistoricalFeedbackDownranksRejectedNodeSet` 证明后续无显式反馈或空反馈对象的新一轮蓝图生成，会把同一组被拒素材及相关来源软降权，不再默认排第一。`GenerateBlueprintCandidatesRejectedBlueprintIdAloneDoesNotRegenerateSameNodeSet` 进一步证明仅传 `rejected_blueprint_ids`、不传 rejected nodes/avoid tags 时，系统也不会用同一组 source nodes 换一个反馈摘要后重新生成同一蓝图。`GenerateBlueprintCandidatesSourceRepetitionFeedbackPrioritizesCrossSourceBlueprint` 证明 `source_repetition` 不再只是摘要标签：当候选池存在多库/多 anchor 替代时，第一份再生成蓝图会优先使用 `source_repetition_diversity_m1`，先取每库最佳、再取每 `(library_id, anchor_id)` 最佳，避免默认第一候选继续塌到同一来源。`GenerateBlueprintCandidatesSupportsFeedbackIterationAndSelectedBlueprintDraft` 与 cross-library golden 已固化 `single_anchor_source`：反馈后候选若只能退到同一参考 anchor，必须把这个塌缩暴露给用户，而不是只报单库或 beat 不足。前端候选卡已把 fallback/gap code 与 beat 级缺口显示成中文诊断，并用 mock workflow 固化二轮反馈后的诊断可见性。尚未完成：情绪弧、真正的多轮会话状态、专家 UI，以及更深的蓝图策略模型。

---

## M5：加深拼装（完整槽位/过渡/hash + 多草稿）

- [x] `transfer_slots` 写作侧消费第一层：正文拼装读取 selected source node 的 active 且未 rejected `reference_technique_specimens.transfer_slots_json`，把声明槽位归一化为 `character/place/honorific/plot_object` 约束；slot replacement 若替换未声明槽位，`DraftAudit` 以 `slot_replacement_transfer_slot_disallowed` 阻断并保持章节正文不变。此项只消费 `slot_name`，不等于自动派生槽位变体或理解自然语言 `constraints`
- [x] `ICorpusSlotResolver` 全类型第一层：显式 `character/place/honorific/plot_object` 槽位归一化，代词/人名/地名/称谓/道具启发式识别；引号/书名号生成 `locked_spans` 保护证据，slot replacement 与锁定范围相交会被 `DraftAudit` 阻断
- [x] `preserved_spans` 结构化记录薄切片：每个 insertion piece 返回稳定 `span_id`、source/output offset、source/output hash、matches；前端 diff 优先消费 spans，golden JSON 固化 span 证据，不暴露原文
- [x] `preserved_spans` 审计闸门薄切片：hash/offset/source/output mismatch 进入 `DraftAudit`，`ready_for_insertion = gate.passed && audit.passed`，失败保持章节正文不变并拒绝插入
- [x] `AuditDraftAgainstBlueprint` 外层包络薄切片：输出 pieces 必须与 selected blueprint source pieces 一一对应；每个 piece 输出必须被 `preserved_spans` 或 `slot_replacements` 完整覆盖；slot replacement 必须落在安全短槽位且 source/output 值与 range 一致；`assembled_text` 必须等于已审计 piece/transition 输出的换行拼接，未审计正文不得插入
- [x] `ICorpusTransitionResolver` 审计薄切片：gap 显式建模，选中蓝图片段之间每个相邻 gap 必须返回 `direct_join` 或 `insert_transition`；transition 具备 id/gap_id/hash/output range/audit trace，`gap_id` 必须绑定相邻 piece 对，缺失、伪造、错配或未审计过渡均阻断插入
- [x] `replace_piece` 候选重组薄切片：正文候选路径遇到 transition resolver 要求替换 source piece 时，只能用 selected blueprint 同一 beat 已声明的备选 node 生成 `transition_repair` 候选；若 replacement node 不在 selected blueprint 同 beat 内，或同 beat `transition_repair` 变体重新审计仍失败，候选必须继续阻断，不得从重新检索结果或邻近句静默换料
- [x] blocked `replace_piece` 的 `next_action.feedback` 契约：超出 selected blueprint 同 beat 或 `transition_repair` 修复仍失败时，正文候选必须保持 blocked、`ready_for_insertion=false`、章节正文不变，并带 `next_action.action=regenerate_blueprint`；`next_action.feedback` 必须可作为 `GenerateReferenceCorpusBlueprintCandidates` 的 `feedback` 入参原样透传，沿用既有蓝图反馈字段表达 rejected blueprint/node、avoid library/anchor/node、problem tags、fallback/repair diagnostic、失败 beat/gap/replacement 标识和用户可见 summary，禁止要求前端拼接自由文本或读取 `source_text/raw_text/embedding`
- [x] `ICorpusTransitionResolver` 第一层三选一：默认规则型 resolver 不再永远 `direct_join`；`raise_pressure -> withhold_answer` 相邻 beat 生成审计过的 `insert_transition`，重复/同源相邻 piece 生成 `replace_piece` 阻断并进入蓝图/候选重组流程，其余安全相邻 gap 仍为 `direct_join`
- [ ] 多草稿：同蓝图不同槽位/过渡策略产 1~N 份，差异仅在槽位/过渡
- [x] slot-only 多草稿契约薄切片：`GenerateReferenceCorpusInsertionDraftCandidatesPayload.slot_value_variants` 可在同一 selected blueprint/同一 primary source nodes 上生成 `slot_variant_1..N`；候选 source node、source hash、preserved spans 源范围和 locked spans 源范围保持一致，差异只来自 slot replacements 和 assembled text。此项只证明请求侧槽位变体闭合，不等于完整多草稿；`transfer_slots` 自动派生、过渡策略变体和跨候选差异审计仍未完成
- [x] `transfer_slots` 自动槽位候选薄切片：当请求没有显式 `slot_value_variants` 时，正文候选会读取 selected blueprint primary source nodes 的 active 且未 rejected `transfer_slots_json`；若声明 `character` 且当前章节存在多个人物快照，会生成 `auto_transfer_slot_1..N` 同源候选，只把源文开头人称代词映射到当前章节人物，保持 source node/source hash/preserved spans 一致。人工 rejected specimen 不参与自动派生。此项不凭空生成地点/道具/称谓，不理解自然语言 `constraints`，也不代表完整自动变体/差异审计完成
- [x] 多草稿候选集差异审计薄切片：同一 source node set 的多份正文候选会在返回前执行候选级审计；候选若出现不属于该候选 `slot_values` 映射的 slot replacement，会以 `draft_candidate_set_non_slot_difference` 阻断该候选，`ready_for_insertion=false` 且章节正文回退，防止恶意/错误 assembler 把非槽位改动伪装成 slot replacement；候选若最终 `assembled_text` 与同组前序可插入候选完全重复，会以 `draft_candidate_set_duplicate_text` 阻断后续重复候选。此项先覆盖槽位候选集，不等于完整过渡策略差异审计或 UI 聚合折叠
- [ ] 完整 `AuditDraftAgainstBlueprint` 改造：验证槽位替换正确、过渡边界合法、原句未篡改、无泄露、授权闸门与相似度闸门不可绕过
- [ ] 正文候选派生规则：只有用户接受或继续迭代的蓝图能进入正文候选；候选尽可能复用蓝图来源语料的原句/结构，剧情微调必须落在槽位、过渡或明确允许的改写区域
- [x] selected blueprint 节点锁定薄切片：正文候选只允许在每个 beat 自己声明的 `NodeIds` 内轮换，不得从重新检索结果、同 library/anchor 邻近句或其它 beat 拉替代 node；若任一 selected node 因 scope/library/授权检索结果变化无法读到 source piece，则返回 blocked `source_node_missing`，不得残缺生成可插入正文
- [x] 前端 mock workflow/guardrail：从 blocked `replace_piece` 候选点击下一步后，必须以 `next_action.feedback` 触发第三轮 `GenerateReferenceCorpusBlueprintCandidates`；guardrail 断言第三轮蓝图调用存在、入参 feedback 与 blocked 候选返回的 `next_action.feedback` 字段逐项一致、第三轮返回 `feedback_applied=true` 且 `feedback_summary` 可见，并且后续正文候选只能来自第三轮选中的蓝图
- [ ] 前端专家模式：槽位表 + 过渡清单 + 锁定确认 + 多草稿并排 diff

**验收：** hash 校验非槽位逐字保留；每个 piece 输出被 preserved spans 或 slot replacements 完整覆盖；选中蓝图相邻 pieces 都有 transition 决策或明确 `direct_join`，且 `gap_id` 绑定相邻 piece 对；golden JSON 固化 `transitions` 与 `audit.transitions` 追踪；同 beat 内可修复的 `replace_piece` 生成 `transition_repair` 候选并重新通过 gate/audit；越界 replacement 或修复仍失败时候选保持 blocked、带可原样回传的 `next_action(action=regenerate_blueprint, feedback=...)`；前端 mock workflow/guardrail 固化第三轮蓝图调用；`locked_spans` 固化受保护片段并阻断槽位替换；多草稿差异断言仅在槽位/过渡；越界改动节奏词被 audit 拦截；从同一已接受蓝图生成的正文候选保留来源原句/结构比例可断言，剧情微调不破坏授权闸门。

**当前检查点：** `GenerateInsertionDraftCandidatesDoNotSubstituteNodesOutsideSelectedBlueprint` 覆盖单节点 selected blueprint：即使检索结果中同 library/anchor 有其它句子，正文候选也只能使用用户选中蓝图里的 node，不会自动换料。`GenerateInsertionDraftCandidatesReusesSelectedBlueprintSourceVariantsThroughGate` 增强为 beat 级断言：每个 piece 的 `node_id` 不仅属于 selected blueprint 全局集合，还必须属于对应 `beat_id` 的 `NodeIds`，防止跨 beat 偷换。`GenerateInsertionDraftCandidatesBlockWhenSelectedBlueprintSourceIsUnavailable` 覆盖 scope 排除 selected node 的场景：系统返回 blocked `source_node_missing`，保持章节正文不变，不用剩余来源拼残缺正文，也不重新检索替代句。`GenerateInsertionDraftCandidatesRebuildsAllowedBlueprintVariantWhenTransitionRequiresReplacement` 覆盖 transition resolver 要求 `replace_piece` 且 replacement node 属于 selected blueprint 同一 beat 备选的场景：正文候选会生成 `transition_repair` 蓝图变体并重新审计，通过后才可插入。`GenerateInsertionDraftCandidatesBlocksTransitionReplacementOutsideSelectedBlueprint` 覆盖 replacement node 不在 selected blueprint 同 beat 内的场景：系统保留 blocked 候选和 `transition_piece_replacement_required` 诊断，不从检索结果或邻近句静默换料，并返回 `next_action.feedback`，可由前端原样触发下一轮蓝图重组；同一测试已延长到第三轮：用 `next_action.feedback` 生成的新蓝图再进入 `GenerateInsertionDraftCandidatesAsync` 后，可得到 `gate/audit/ready` 全通过且不含被拒 node 的正文候选。`GenerateInsertionDraftCandidatesKeepsOriginalBlockedCandidateWhenTransitionRepairStillFails` 覆盖同 beat repair 仍失败时继续 blocked 且携带 `next_action`。`GenerateInsertionDraftUsesDefaultTransitionResolverToBridgePressureIntoWithheldAnswer` 覆盖默认 resolver 在 `raise_pressure -> withhold_answer` gap 上生成 `insert_transition` 并通过 transition audit；`GenerateInsertionDraftBlocksWhenDefaultTransitionResolverRequiresDuplicateSourceReplacement` 覆盖默认 resolver 对重复相邻来源返回 `replace_piece` 并阻断插入。`GenerateInsertionDraftCandidatesCanProduceSlotOnlyVariantsFromSameSelectedBlueprint` 覆盖请求侧 `slot_value_variants`：同一 selected blueprint、同一 source node、同一 preserved/locked 源证据可生成多份 `slot_variant_*` 正文候选，候选之间只按 `character/place/honorific/plot_object` 槽位映射产生文本差异，受保护标题不被替换；C# contract、TS 类型与 mock bridge 已同步该输入字段。`GenerateInsertionDraftCandidatesBlocksNonSlotDifferencesAcrossSlotVariants` 证明同源多稿返回前会执行候选集差异审计：恶意 assembler 即使把非槽位“指尖→掌心”伪装成安全 slot replacement、让单稿 audit 通过，也会被 `draft_candidate_set_non_slot_difference` 阻断并保持章节正文不变，合法候选不受影响。`GenerateInsertionDraftCandidatesBlocksDuplicateTextAcrossSlotVariants` 证明不同 slot 参数最终生成完全相同正文时，后续重复候选会以 `draft_candidate_set_duplicate_text` 阻断，避免把无差异结果展示为可选择多稿。`GenerateInsertionDraftCandidatesHonorsTransferSlotConstraintsFromTechniqueSpecimens` 与 `GenerateInsertionDraftCandidatesBlocksSlotReplacementOutsideTransferSlots` 证明写作侧已消费 active 且未 rejected 的 technique specimen `transfer_slots_json`：合法声明槽位可通过，未声明槽位进入 `slot_replacement_transfer_slot_disallowed` 并保持章节正文不变；`GenerateInsertionDraftCandidatesIgnoresRejectedTechniqueSpecimenTransferSlots` 防止人工 rejected 的 specimen 继续约束正文。`GenerateInsertionDraftCandidatesAutoDerivesCharacterTransferSlotVariants` 证明当请求没有显式 `slot_value_variants` 时，active 且未 rejected 的 `character` transfer slot 可从当前章节人物快照派生 `auto_transfer_slot_1..N` 同源候选，候选不轮换 source node，非槽位证据保持一致；`GenerateInsertionDraftCandidatesDoesNotAutoDeriveRejectedTransferSlots` 防止 rejected specimen 驱动自动派生。前端 TS 契约、正文候选卡、mock bridge、workflow 和 guardrail 已同步：从 blocked `replace_piece` 候选点击“回到蓝图重组”会触发第三轮 `GenerateReferenceCorpusBlueprintCandidates`，断言入参 feedback 与候选 `next_action.feedback` 一致、结果 `feedback_applied=true` 且反馈摘要可见。`PreservingReferenceCorpusTextAssembler` 现在为每个 piece 输出结构化 `preserved_spans`，记录非槽位片段的 source/output offset、hash 与 matches；contract/bridge/TS 类型、mock workflow、M1 golden fixture 均已同步，前端 diff 预览优先用 spans 标识保留片段。`ICorpusSlotResolver` 第一层已覆盖 `character/place/honorific/plot_object` 显式槽位和常见启发式识别，`ReferenceCorpusInsertionPiecePayload.locked_spans` 会把书名号/引号等受保护范围作为 source/output offset + hash 证据返回，前端 diff 可见；`GenerateInsertionDraftAppliesTypedSlotsWithoutReplacingProtectedQuotedText` 覆盖四类槽位替换且不改保护标题，`GenerateInsertionDraftBlocksSlotReplacementInsideLockedProtectedText` 覆盖恶意 resolver 返回锁定范围内替换时 audit 以 `slot_replacement_locked_range` / `locked_span_hash_mismatch` 阻断。`DraftAudit` 已接入插入草稿：source 缺失、piece preserved hash mismatch、span offset 越界、source/output span hash 不一致、span.matches=false 都会进入 audit errors/violations，并令 `ready_for_insertion=false`；`GenerateInsertionDraftBlocksWhenDraftAuditFindsPreservedSpanMismatch` 覆盖 gate 通过但 audit 阻断的场景，章节正文保持不变。新增 `GenerateInsertionDraftBlocksWhenAssemblerDropsSelectedBlueprintPiece`、`GenerateInsertionDraftBlocksWhenExplicitSlotReplacementConsumesWholeSourceSentence`、`GenerateInsertionDraftBlocksWhenAssembledTextContainsUnauditedOutput`，把缺失 selected source piece、整句伪槽位替换、piece 内未被 preserved span/slot replacement 覆盖的新增文本、以及 `assembled_text` 追加未审计正文纳入 audit 阻断。过渡已进入同一审计包络，不再是可选装饰：`IReferenceCorpusTransitionResolver` 接收相邻 piece gap，必须为选中蓝图相邻 pieces 返回 `direct_join`、`insert_transition` 或 `replace_piece`；`DraftAudit` 生成 `audit.transitions`，校验 `gap_id` 绑定的相邻 piece 对、hash、approval、decision、output range 与 assembled text 一致，缺失/伪造/错配都会阻断。M5 仍限定在章节使用侧的拼装审计加深，消费已处理语料和 selected blueprint，不把素材库处理管线或库管理界面混进正文生成流程。当前仍未完成 `transfer_slots.constraints` 自然语言约束推理、地点/道具/称谓等完整自动槽位变体派生、过渡策略变体差异审计、跨蓝图/跨候选池 replacement 重组策略、重复候选 UI 聚合折叠和专家 UI。

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

## M9：专家 UI + 打磨

- [x] 自动/专家模式切换完善（治理页与章节写作页）
- [x] 写作会话专家工作台：阶段进度 + 现有操作区 + 当前章节/生效语料库上下文（人物快照沿用章节上下文推断）
- [x] 章节语料写作状态可中断恢复（sessionStorage 保存目标/模式/蓝图/正文候选/选择）；片段保留 node/anchor/library/license 溯源
- [x] 端到端 UI 验收：`npm --prefix frontend run verify` 覆盖治理兼容、章节审计应用和截图产物

**验收：** 自动模式全程仅需写目标/选结果/改少量项；专家模式可逐项展开；任意步骤中断可恢复；每片段可追溯到源语料 + 分析依据 + license。

---

## 跨里程碑约束（全程遵守）

1. **契约先行** — 后端方法先定 C# 契约 + TS 类型 + api.ts 签名（列表一律 `PageResult<T>`）
2. **additive migration** — ALTER TABLE 加列，幂等，存量库可升级
3. **安全 + 授权红线** — SafePath/SSRF/审批流/migration copy-first/无泄露 + license 插入闸门不可绕过
4. **可追溯** — 每产物记 run_id + evidence（走 junction FK），UI 每论断可跳原文
5. **跨库闭环不可降级** — 写作 session 的有效语料范围来自所有启用 library 成员；M1/M3/M4/M5 的测试必须覆盖跨库检索、多蓝图迭代、正文候选复用，不接受单 anchor 作为唯一验收
6. **回归资产（修复 #12）** — 固定 golden fixture + golden JSON + fake LLM + 规模 fixture + 性能预算 + 中断恢复脚本 + UI 交互验收；杜绝"符合语义/合理"式主观验收
7. **验证命令** — 后端 `dotnet test Novelist.slnx --no-restore -v minimal`；前端 `npm --prefix frontend run verify`
