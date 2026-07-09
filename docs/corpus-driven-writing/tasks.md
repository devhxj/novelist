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

- [ ] `reference_text_nodes` 建表 + 三个索引（parent/atype/chapter）
- [ ] `reference_materials` / `reference_source_segments` 加 `node_id` FK
- [ ] Stage 0 结构化写入器：源文本 → 节点树（章/场/段/句/从句），填 offset/text_hash/sequence
- [ ] 章节窗口查询辅助：给定 node，取前 N 章/同场景兄弟节点
- [ ] migration 幂等 + 存量库升级测试

**验收：** 导入 golden 书后，节点树父子/顺序/offset 正确；任一句节点能反查其所属段/场/章；text_hash 与源文逐字一致。

### M0.2 分层特征观察 + projection（DB / 修复 #2 #7）

- [ ] `reference_feature_observations` 建表：`value_kind`/`value_num`/`value_bool`/`value_json` + `review_state`/`validity_state`/`superseded_by_run_id`
- [ ] 三索引（family / num / node）
- [ ] **幂等写入（护栏 G1）**：`ux_obs_generation_key` UNIQUE + 确定性 observation_id + upsert；并发/重试/续跑不重复写
- [ ] 热路径 projection 表：`reference_obs_sensory`（示范）+ 写入同步 + 重建脚本
- [ ] `reference_analysis_runs` 建表（含 token_budget/tokens_spent）
- [ ] **预算续跑状态（护栏 G2）**：status 加 `paused`/`budget_exhausted`/`partial_completed` + `resume_cursor`；续跑从游标后开始，配合 G1 幂等

**验收：** 能按 value_num 范围、按 sensory 投影表数组查询；review_state 与 validity_state 独立可设；**同 (run,node,feature) 并发/重试只落一条 active observation**；预算耗尽置 `budget_exhausted` 而非 failed，补预算可从 resume_cursor 续跑。

### M0.3 技法标本 + junction（DB / 修复 #8）

- [ ] `reference_technique_specimens` 建表（含 review/validity/superseded）
- [ ] junction：`reference_specimen_evidence` / `reference_template_examples` / `reference_blueprint_beat_pieces`
- [ ] 级联失效查询：给定 superseded 的 observation，定位受影响 specimen/beat

**验收：** 证据边可 join；observation superseded 后能精确列出受影响 specimen。

### M0.4 语料库 + 授权（DB / 修复 #4 #6）

- [ ] `reference_corpus_libraries` / `reference_library_members` / `reference_session_library_binding`
- [ ] `reference_source_license`
- [ ] 检索默认作用域视图：会话绑定库 ∩ enabled ∩ license 允许复用 ∩ 去重折叠

**验收：** 能按会话解析出有效检索作用域；forbidden/unknown 来源被排除；dedup_group 折叠生效。

### M0.5 分页契约 + 测试资产骨架（Contracts / 测试 / 修复 #9 #12）

- [ ] `PageRequest` / `PageResult<T>`（pageSize 上限 200、稳定排序 tiebreaker、错误码）
- [ ] TS 侧对应类型 + api.ts 泛型分页签名
- [ ] **fake LLM harness**：可注入的确定性 LLM 替身，返回固定 golden 输出
- [ ] **fake embedding harness（护栏 G3）**：按文本 hash 生成确定性向量，golden 检索逐次一致可断言
- [ ] **相似度算法（护栏 G5）**：4-gram 容器度 + LCS 比，piece-level，归一化+中文专名规则；纯确定性、无模型调用；单测逐值断言
- [ ] **golden fixture**：小 golden 书（约 500 句，含授权标注）纳入版本库
- [ ] **规模 fixture 生成器**：合成 200 万字级语料（供性能/恢复测试）
- [ ] 中断恢复测试脚本骨架

**验收：** 分页往返稳定、超限报错；fake LLM/fake embedding 可驱动全流程且结果可回归；相似度算法对 golden 输入产出固定值；golden/规模 fixture 可用。

---

## M1：纵向薄切片（跑通端到端闭环，修复 #10）

**目标：** 用 fake LLM + golden 小书，跑通"导入 → 基础分析 → 当前章节检索 → 单蓝图 → 槽位替换 → 插入草稿"，自动模式，证明闭环。宁可每层都薄，也要先连通。

### M1.1 导入 + Stage 0-1（后端）

- [ ] 导入 golden 书 → text_nodes 树（M0.1 写入器）
- [ ] Stage 1 确定性特征全量落 observation（rule-based，无 LLM）
- [ ] license 标注入库

### M1.2 最小检索（后端 / 修复 #5 / 护栏 G3 G4）

- [ ] **node/material embedding 构建任务（护栏 G3）**：复用 `IEmbeddingClient`，对 text_nodes 建向量；测试用 fake embedding 替身
- [ ] `CorpusQueryContext` + `CurrentChapterContext` 契约（前端不传 embedding，护栏 G4）
- [ ] 章节 embedding 后端计算 + 缓存（key = draft text hash）
- [ ] 最小 `IQueryContextParser`（fake LLM：大纲 → 固定 QueryContext）
- [ ] 单路检索：文本语义向量 + 授权作用域过滤 + 当前章节 embedding 连贯度加权
- [ ] 返回 `PageResult<候选片段>`

### M1.3 单蓝图 + 槽位替换 + 插入（后端）

- [ ] 最小 `ICorpusBlueprintAssembler`：单策略，产 1 份蓝图，beat→node 走 beat_pieces
- [ ] 最小 `ICorpusSlotResolver`：检测人名槽位，输出替换表
- [ ] 最小 `ICorpusTextAssembler`：槽位替换 + hash 校验 preserved_spans 逐字保留
- [ ] 插入闸门（M0.4）：license cleared + 相似度阈值校验，通过才写编辑器 buffer

### M1.4 自动模式前端（前端 / 修复 #11）

- [ ] 写作会话最小界面：大纲输入 → 一份蓝图 → 一份草稿 → 插入
- [ ] 全部专家控件默认隐藏，用 AI 默认决策
- [ ] 草稿 diff 预览（原句保留 vs 槽位替换标色）

### M1.5 闭环验收（测试 / 修复 #12）

- [ ] 端到端脚本：golden 书 + 固定大纲 → 期望草稿（golden JSON 比对）
- [ ] hash 校验：非槽位原句逐字保留
- [ ] 插入闸门：相似度超阈值时正确阻断

**验收：** 全流程用 fake LLM 稳定复现同一草稿；原句 hash 校验通过；闸门阻断可复现。**此里程碑完成即证明产品闭环成立。**

---

## M2：加深分析（10 family 全量 + 技法标本）

**目标：** 把 M1 的薄分析升级为完整多维分析。

### M2.1 feature_family 锁定 schema（后端 / 关键设计）

为每个 family 定义严格 JSON Schema，LLM 只填枚举，禁止自由发挥：

- [ ] 句级：`syntax` / `rhythm` / `sensory`（数组）/ `emotion` / `rhetoric`（数组）
- [ ] 段落级：`narrative` / `pov` / `action` / `character` / `commercial`
- [ ] 每 family 一份 schema 文件 + 输出校验器（不合 schema 触发重试）
- [ ] sensory 等数组维度同步写 projection 表（M0.2）

### M2.2 Task A/B 全量分析（后端 / 修复 #3）

- [ ] Task A 句级 LLM：全量、异步、按章节优先级排队、per-run token_budget、可续跑
- [ ] Task B 段落级 LLM：全量、注入场景上下文
- [ ] evidence_start/end 精确记录
- [ ] confidence < 阈值自动入复核队列（M8 消费）
- [ ] 中断从已完成 node 续跑

### M2.3 Task C 技法标本（后端 / 关键设计）

- [ ] 综合推理：全部 A/B observation + 原文 → TechniqueSpecimen
- [ ] why_it_works 每 contributing_factor 走 specimen_evidence FK 到真实 observation（禁空引用）
- [ ] technique_abstract 去内容化 + 泄露检测（不含原文专有名词）
- [ ] Stage 3 仅高置信度/标记节点触发

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
