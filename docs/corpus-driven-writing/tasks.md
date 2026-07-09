# 语料驱动写作系统 — 分阶段任务拆解（v2）

> 本文档按 [development-plan.md](./development-plan.md) 第九节的实现顺序展开为可执行任务清单。v2 重排为**纵向薄切片优先**（修复评审 #10）：先修地基 schema，再立即跑通端到端闭环，然后逐层加深。
>
> 里程碑：`M0 地基` → `M1 纵向薄切片` → `M2 加深分析` → `M3 加深检索` → `M4 加深蓝图` → `M5 加深拼装` → `M6 语料库产品化` → `M7 聚合知识` → `M8 复核工作流` → `M9 专家UI+打磨`。
>
> 每个任务标注层（DB/后端/前端/测试）、依赖、验收点。验收全部对固定 fixture + golden JSON（修复 #12）。

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
- [x] **预算续跑状态规则（护栏 G2）**：status 加 `paused`/`budget_exhausted`/`partial_completed` + `resume_cursor`，Core 状态机保证预算耗尽非 failed
- [ ] **预算续跑管线接入（护栏 G2）**：分析任务从 `resume_cursor` 后开始，配合 G1 幂等覆盖已完成 node

**验收：** 能按 value_num 范围、按 sensory 投影表数组查询；review_state 与 validity_state 独立可设；**同 (run,node,feature) 并发/重试只落一条 active observation**；预算耗尽置 `budget_exhausted` 而非 failed，补预算可从 resume_cursor 续跑。

### M0.3 技法标本 + junction（DB / 修复 #8）

- [x] `reference_technique_specimens` 建表（含 review/validity/superseded）
- [x] junction：`reference_specimen_evidence` / `reference_template_examples` / `reference_blueprint_beat_pieces`
- [ ] 级联失效查询：给定 superseded 的 observation，定位受影响 specimen/beat

**验收：** 证据边可 join；observation superseded 后能精确列出受影响 specimen。

### M0.4 语料库 + 授权（DB / 修复 #4 #6）

- [x] `reference_corpus_libraries` / `reference_library_members` / `reference_session_library_binding`
- [x] `reference_source_license`
- [ ] 检索默认作用域视图：会话绑定库 ∩ enabled ∩ license 允许复用 ∩ 去重折叠

**验收：** 能按会话解析出有效检索作用域；forbidden/unknown 来源被排除；dedup_group 折叠生效。

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

**目标：** 用 fake LLM + golden 小书，跑通"导入 → 基础分析 → 当前章节检索 → 单蓝图 → 槽位替换 → 插入草稿"，自动模式，证明闭环。宁可每层都薄，也要先连通。

### M1.1 导入 + Stage 0-1（后端）

- [x] 导入 golden 书/真实 source → text_nodes 树（M0.1 写入器）
- [x] Stage 1 确定性特征落 observation（M1 最小版：句长/节奏、感官 marker、情绪 marker；rule-based，无 LLM）
- [x] license 标注入库（旧 license_status → 新 reference_source_license gate；默认 project/global library membership）

### M1.2 最小检索（后端 / 修复 #5 / 护栏 G3 G4）

- [x] **node embedding 构建（护栏 G3，M1 最小版）**：复用 `IEmbeddingClient`，对 text_nodes 缺失向量懒构建并缓存；测试用 fake embedding 替身
- [ ] material embedding 构建任务：Stage 0/legacy material 与 text_nodes 对齐后补齐 material 级索引
- [x] `CorpusQueryContext` + `CurrentChapterContext` 契约（前端不传 embedding，护栏 G4）
- [x] 章节 embedding 后端计算 + 缓存（key = draft text hash，前端不传 embedding）
- [x] 最小 `IQueryContextParser`（M1 确定性 parser：自然语言目标 → 固定 QueryContext；后续 M2/M4 可替换为 structured LLM）
- [x] 单路检索（M1 最小版）：text_nodes 语义向量 + library/license/visibility/reuse 过滤 + 当前章节 embedding 连贯度加权
- [x] 返回 `PageResult<候选片段>` 的 bridge/contract 形态

### M1.3 单蓝图 + 槽位替换 + 插入（后端）

- [x] 最小 `ICorpusBlueprintAssembler`：单策略，产 1 份蓝图，beat→node 走 `reference_blueprint_beat_pieces`
- [x] 最小 `ICorpusSlotResolver`：检测人名/代词槽位，输出替换表
- [x] 最小 `ICorpusTextAssembler`：槽位替换 + 非槽位 preserved hash 校验
- [x] 插入闸门（M0.4 后端）：license cleared + 相似度阈值校验，通过才返回可插入结果；被阻断时保留原章节文本

### M1.4 自动模式前端（前端 / 修复 #11）

- [x] 后端 bridge + TS adapter：`GenerateReferenceCorpusInsertionDraft` 自动闭环入口可被章节界面调用
- [x] 写作会话最小界面：大纲输入 → 一份蓝图 → 一份草稿 → 插入编辑器 buffer
- [x] 全部专家控件默认隐藏，用 AI 默认决策
- [x] 草稿 diff 预览（原句保留 vs 槽位替换标色）

**当前检查点：** 章节编辑器右侧 `参考素材` 面板已接入 `GenerateReferenceCorpusInsertionDraft` 默认路径。前端从 Monaco 读取当前正文与光标 offset，默认检索 `project:{novelId}:default` + `global:workspace`，生成后展示蓝图、片段、槽位替换、diff 标色和插入闸门；只有 `ready_for_insertion && gate.passed` 时才允许把 `chapter_text_after_insertion` 应用到编辑器 buffer，且不直接调用 `SaveContent`。推荐素材、事实边界和旧严格流程默认收进“高级参考流程”，展开前不会触发 `SearchReferenceMaterials` 或 orchestration 读取。

### M1.5 闭环验收（测试 / 修复 #12）

- [x] 后端薄切片集成测试：真实导入 + Stage 1 + parser → 检索 → 蓝图 → 槽位替换 → 闸门 → 可插入文本
- [x] 端到端脚本：golden 书 + 固定大纲 → 期望草稿（golden JSON 比对）
- [x] hash 校验：非槽位原句逐字保留
- [x] 插入闸门：相似度超阈值时正确阻断

**当前检查点：** `Fixtures/corpus-driven-writing/m15-insertion-draft-golden.json` 固化小 golden 书、当前章节、固定目标和归一化 `expected_draft`；`GenerateInsertionDraftFromGoldenBookAndFixedOutlineMatchesGoldenJson` 从 fixture 创建真实 reference source，使用确定性 embedding 跑完整 `GenerateInsertionDraftAsync`，将 actual draft 投影到稳定 JSON 后做 deep equality，并额外断言 `reference_blueprint_beat_pieces` 追溯边已落库。

**验收：** 全流程用 fake LLM 稳定复现同一草稿；原句 hash 校验通过；闸门阻断可复现。**此里程碑完成即证明产品闭环成立。**

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
- [x] 产品触发入口：`StartReferenceCorpusFeatureAnalysis` / `GetReferenceCorpusFeatureAnalysisRun`，按 anchor + scope 启动 sentence/passage 默认 family 分析并返回安全 run 状态
- [x] Task B 段落节点选择 + 上下文地基：只分析 `reference_source_segments.segment_type='paragraph'` 的真实段落，跳过 hook/beat/action_afterbeat 等派生 passage；注入 parent chapter、containing scene、前后 paragraph context；旧库缺 `reference_source_segments.node_id` 时诊断并跳过，不做 hash/offset 猜测
- [x] Task A/B 共用执行器地基：读取 node×family、调用可替换 analyzer、locked schema 校验、observation upsert、tokens/resume_cursor/status 更新、sensory projection 同步
- [x] schema 失败重试：invalid JSON/schema/rejected output 按 `MaxValidationAttempts` 重试；失败尝试累计 tokens 但不写 observation、不推进 cursor；重试耗尽后 run 标 failed
- [x] 真实 LLM analyzer 地基：复用 `IChatCompletionClient` + selected model，生成 schema-locked prompt，抽取 fenced JSON，读取 usage tokens；测试使用 fake chat client，不触发真实网络
- [ ] evidence_start/end 精确记录
- [ ] confidence < 阈值自动入复核队列（M8 消费）
- [ ] 中断从已完成 node 续跑

**当前检查点：** `ReferenceCorpusFeatureAnalysisRunner` 已用 fake analyzer 跑通 node×family 执行链：预算耗尽时停在当前 cursor，补预算后从下一项续跑；最后一项正好用完预算时正确收敛为 `completed`；写入通过 locked schema validator 的候选 observation，并同步 `reference_obs_sensory`。句级 Task A 传空 context；段落级 Task B 只读取真实 paragraph source segment，避免 `node_type='passage'` 混入 hook/beat/action_afterbeat 等派生节点，并给 analyzer 注入 parent/chapter/containing scene/前后 paragraph 的 bounded context。`ReferenceCorpusChatCompletionFeatureFamilyAnalyzer` 已接入现有 chat completion 抽象，prompt 包含 schema descriptor、node 元数据、bounded node_text 与安全压缩后的 `analysis_context`；system prompt 明确 `node_text` 是唯一 evidence 来源，context 只能辅助判断，不能用于 evidence offset。usage token 会回填 runner 预算记账。产品入口已接入 bridge/TS adapter/mock：`StartReferenceCorpusFeatureAnalysis` 读取 selected model、校验 anchor 可访问性、按 `scope=sentence|passage` 派生默认 family 并启动 runner；`GetReferenceCorpusFeatureAnalysisRun` 返回 run 元数据、tokens、cursor、observation_count 和 diagnostics，返回体不包含 `node_text/source_text/raw_text/prompt/model_output_json/embedding`。当前入口仍是一次调用内执行的薄触发，不是后台队列；异步队列、章节优先级调度、取消能力仍未接入。低置信度 observation 仍写 `review_state='unverified'`，避免和真正跨 run 冲突混淆；按 confidence 入复核队列仍留到 M8。

### M2.3 Task C 技法标本（后端 / 关键设计）

- [ ] 综合推理：全部 A/B observation + 原文 → TechniqueSpecimen
- [x] Stage 3 runner/validator/writer 地基：读取 active 高置信度 observation + 原文节点，调用可替换 analyzer，写 `reference_technique_specimens`
- [x] why_it_works 每 contributing_factor 走 `reference_specimen_evidence` FK 到真实 observation（禁空引用/未知 id）
- [x] technique_abstract / transfer_template 去内容化泄露检测（拒绝原文专名、原文动作短语、长原文片段）
- [x] Stage 3 仅高置信度节点触发（低于阈值 observation 不进入 analyzer 输入）
- [ ] 真实 LLM analyzer：复用 `IChatCompletionClient`，schema-locked prompt，抽取 fenced JSON，读取 usage tokens
- [ ] 产品触发入口：Start/Get TechniqueSpecimen run 状态 + 后台/预算调度接入

**当前检查点：** `ReferenceCorpusTechniqueSpecimenRunner` 已作为独立 Stage 3 地基接入：按 source node 聚合同 anchor、同 node_type、active 且 `confidence >= MinObservationConfidence` 的 observation，把 node 原文和 observation evidence 交给可替换 analyzer；`ReferenceCorpusTechniqueSpecimenOutputValidator` 锁定 `reference-corpus-technique-specimen-v1` 输出，要求 `why_it_works` 每个 factor 至少引用一个真实 observation id，未知/空 evidence 直接拒绝；落库在同一事务内写 `reference_technique_specimens` 与 `reference_specimen_evidence`，`specimen_id = hash(run_id,node_id,technique_family)` 保证重试幂等，重跑保留人工 `review_state`，不把 confirmed/rejected 重置为 unverified。`reference_specimen_evidence(observation_id, specimen_id)` 索引用于后续从 superseded observation 反查受影响 specimen。去内容化闸门会拒绝 `technique_abstract` / `transfer_template` 中出现原文专名、原文动作短语或长原文片段。当前仍未接真实 chat analyzer、产品触发入口、预算/队列调度和前端 TechniqueSpecimen 卡。

### M2.4 分析前端

- [ ] 材料/节点点击 → 按 family 分组展示 observation，evidence 跳原文
- [ ] TechniqueSpecimen 卡：abstract / why_it_works（逐条追溯）/ 迁移模板 / 失败模式 / 反面模式

**验收（双轨，修复 #3 #12）：**
- 正确性轨：golden 书全量分析比对 golden JSON
- 规模轨：200 万字 fixture 验证续跑/预算/性能/恢复
- why_it_works 每条可追溯；abstract 泄露检测通过

---

## M3：加深检索（四类索引融合 + 当前章节上下文进排序）

- [ ] `reference_technique_vectors` sqlite-vec 表 + abstract embedding
- [ ] 四路召回：文本语义 / 技法语义 / 结构化 observation 过滤（走 num + projection）/ 上下文过滤
- [ ] 融合排序：当前章节 embedding 连贯度 + 插入位置匹配 + 授权/质量加权（修复 #5）
- [ ] 权重可被检查表反馈调整（M4 消费）
- [ ] 跨语料检索（按会话作用域，非单 anchor）
- [ ] 全部返回 `PageResult<T>`

**验收：** 结构化查询（"动作替代心理描写表现愤怒"）精准命中 golden fixture 中标注样本；当前章节上下文改变时排序可见变化；技法语义相似区别于文本相似（golden 断言）。

---

## M4：加深蓝图（N 策略 + 检查表反馈）

- [ ] `ICorpusBlueprintAssembler` 多策略：情绪优先/节奏优先/技法多样性/场景模板
- [ ] 覆盖率计算 + gap 识别 + 情绪弧线预估
- [ ] 蓝图表扩展：`assembly_strategy`/`coverage_score`/`gap_positions_json`/`query_context_json`
- [ ] beat→node 走 `reference_blueprint_beat_pieces`（非 JSON）
- [ ] `GenerateChapterBlueprintAsync` 改造：Parser + Retriever + Assembler
- [ ] OrchestrationStages 加 goal_parsing/corpus_retrieval/blueprint_assembly
- [ ] **legacy 兼容（护栏 G6）**：旧 run/blueprint/frontend 状态只读可恢复；新旧 stage 常量并存 + 兼容 shim；不破坏 Phase 16 reconcile/recovery；旧 blueprint 可选一次性归档脚本（不自动改写）
- [ ] 检查表反馈契约 + → 检索权重映射（复用 reference_user_feedback）
- [ ] 前端专家模式：蓝图卡列表（情绪弧折线/节奏色块/覆盖率/来源分布/gap 标红）+ 拒绝检查表

**验收：** N 份蓝图策略对 golden fixture 产出可断言的不同分配；检查表勾选后被勾维度有可见变化（golden 前后比对）；gap 标注对"无合格语料"场景正确。

---

## M5：加深拼装（完整槽位/过渡/hash + 多草稿）

- [ ] 回填 transfer_slots（M2 已生成 specimen 补语义槽）
- [ ] `ICorpusSlotResolver` 全类型：人名/地名/专属称谓/情节道具 + 锁定保护标注
- [ ] `preserved_spans` 记录 + hash 校验（非槽位逐字保留，失败拒绝输出）
- [ ] `ICorpusTransitionResolver`：gap 三选一（补过渡/直接拼接/换片段）
- [ ] 多草稿：同蓝图不同槽位/过渡策略产 1~N 份，差异仅在槽位/过渡
- [ ] `AuditDraftAgainstBlueprint` 改造：验证替换正确/原句未篡改/无泄露/授权闸门
- [ ] 前端专家模式：槽位表 + 过渡清单 + 锁定确认 + 多草稿并排 diff

**验收：** hash 校验非槽位逐字保留；多草稿差异断言仅在槽位/过渡；越界改动节奏词被 audit 拦截。

---

## M6：语料库产品化（库/去重/授权闸门完善）

- [ ] 全局库/项目库启用规则 + 会话绑定管理 UI
- [ ] 来源质量分级 + 禁用来源 + disabled_reason
- [ ] 跨来源去重（dedup_group_id 识别与折叠）
- [ ] 授权工作流：导入即标注 → 检索过滤 → 插入闸门（相似度阈值硬校验，不可绕过，修复 #4）
- [ ] 插入审计留痕（来源/license/相似度/闸门）

**验收：** forbidden/unknown 不进检索；相似度超阈值插入被阻断且不可绕过；去重折叠对重复导入生效；审计可追溯每次插入来源。

---

## M7：聚合知识（作者画像/场景模板/世界观）

- [ ] 四聚合表（style_profiles/scene_templates/world_models/dialogue_techniques）
- [ ] **溯源表 `reference_aggregate_provenance`（护栏 G7）**：每个聚合产物记 (library_id, anchor_id, run_id)
- [ ] `BuildAuthorStyleProfileAsync`（多数从 Stage 1 算，缺口定向 LLM 补）
- [ ] `ExtractSceneTypeTemplatesAsync`（narrative_function+scene_type 聚类，≥3 次成模板，example 走 template_examples）
- [ ] `BuildWorldModelAsync` + 回填 specimen 的 world_context_dependencies
- [ ] 跨语料模板合并
- [ ] **stale 传播（护栏 G7）**：任一源 anchor 重跑（新 run）→ 按 provenance 定位依赖的 cross-corpus 模板/画像标 stale → 重建
- [ ] 前端：作者风格 tab / 场景模板库 / 世界观摘要

**验收：** AuthorStyleProfile 对 golden 书统计特征匹配（数值断言）；场景模板 example 经 golden 标注验证；**某源重跑后依赖它的 cross-corpus 模板正确标 stale**。

---

## M8：复核工作流（review/validity 状态机 + 重跑语义）

- [ ] `review_state` 状态机：unverified → confirmed/rejected/conflicted（人工）
- [ ] `validity_state`：active/superseded（机器）+ superseded_by_run_id（修复 #2）
- [ ] 自动入队：confidence < 阈值 / 冲突 / 罕见 family
- [ ] 重跑：旧 observation 不删、标 superseded、保留 review_state；specimen invalidation check（证据全 superseded 则 specimen superseded）
- [ ] 冲突检测：同 node 不同 run 不一致 → conflicted 入队
- [ ] ReviewQueue 返回 `PageResult<T>`
- [ ] 前端复核队列：检查表操作 + 批量 + 跨页分页

**验收：** 重跑后用户已 confirm 的判断不被污染（review_state 保留、validity_state 变 superseded）；依赖旧 run 的 specimen 正确 superseded；冲突正确入队。

---

## M9：专家 UI + 打磨

- [ ] 自动/专家模式切换完善（修复 #11）
- [ ] 写作会话主界面：左阶段进度/中操作区/右上下文（当前章节/人物快照/生效语料库）
- [ ] 全流程可中断可恢复（复用 reconcile）+ 全片段可追溯
- [ ] 端到端 UI 验收（截图/交互脚本，修复 #12）

**验收：** 自动模式全程仅需写目标/选结果/改少量项；专家模式可逐项展开；任意步骤中断可恢复；每片段可追溯到源语料 + 分析依据 + license。

---

## 跨里程碑约束（全程遵守）

1. **契约先行** — 后端方法先定 C# 契约 + TS 类型 + api.ts 签名（列表一律 `PageResult<T>`）
2. **additive migration** — ALTER TABLE 加列，幂等，存量库可升级
3. **安全 + 授权红线** — SafePath/SSRF/审批流/migration copy-first/无泄露 + license 插入闸门不可绕过
4. **可追溯** — 每产物记 run_id + evidence（走 junction FK），UI 每论断可跳原文
5. **回归资产（修复 #12）** — 固定 golden fixture + golden JSON + fake LLM + 规模 fixture + 性能预算 + 中断恢复脚本 + UI 交互验收；杜绝"符合语义/合理"式主观验收
6. **验证命令** — 后端 `dotnet test Novelist.slnx --no-restore -v minimal`；前端 `npm --prefix frontend run verify`
