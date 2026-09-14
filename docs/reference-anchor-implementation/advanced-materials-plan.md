# 高级写作素材整块设计（Advanced Writing Materials）

[Back to implementation index](../reference-anchor-implementation-plan.md) | [Back to decisions index](decisions.md).

状态：**设计稿，待作者审阅确认后实施**。
定位：本方案是《轻量化聚焦方案（定稿）》（`docs/corpus-driven-writing/lightweight-refocus-proposal-2026-08-31.md`）第五节**保留项**与开放问题 8 的落地补全，不引入新方向、不复活已退役的拼装线。

## 0. 一句话定义与产物出口

**定义**：在现有 L1 语料（原子摘录）之上，为**每一本参考书**产出一套"高级写作素材"——事实观测、写作机理、书级策略——它们是可迁移的**写法与机理**，不是可直搬的原文与设定。

**产物出口（唯一）**：**AI 在生成正文时，按结构化需求自动混合获取"高级素材 + 语料素材"，注入当次生成。**

这意味着：
- 素材不面向"拼装/插入"，只面向"生成时的质量迁移"；
- 消费发生在**服务端自动检索注入**，不是让模型自己想起来去查（模型自主 tool-call 是补充，不是主通路）；
- 不恢复蓝图候选/正文候选拼装线（已于 2026-08-31 物理删除，见 `frontend/scripts/app-mock-workflow/bridge-guardrails.mjs:197-213`）。

## 1. 边界

**In（整块必须一次跑通，缺一不成型）**

| 维度 | 内容 |
|---|---|
| 五类素材 | 世界观、文风、写法、技巧、结构/钩子 |
| 三层递进 | ① 事实观测（这本书实际写了什么）→ ② 机理标本（为什么这样写、脱离什么条件失效）→ ③ 书级策略（整本归纳） |
| 闭环 | 抽取 → 证据回链 → 复核（unverified→confirmed/rejected）→ 消费 |
| 单位 | 每书一套，`anchor_id` 维度 |
| 消费 | 生成正文时，结构化需求 → L1+L2 混合检索 → 注入 |

**Out（明确不做）**

- 不做跨书聚合的"作者级"风格（已定：每书一套）。
- 不自动改写或插入用户正文；不调用 `SaveContent`。
- 不重建"设定百科"——事实层只收**与写作决策相关**的事实。
- 不复活拼装线（蓝图候选、正文候选、锚定草稿审计、相似度门）。
- 不新增专家控制面（沿用 refocus 的收敛约束）。
- 不把具体专有名/设定做成可直搬产物（见第 7 节）。

## 2. 三层模型与五类 family

### 2.1 三层

| 层 | 回答的问题 | 数据落点 | 关键字段 |
|---|---|---|---|
| ① 事实观测 observation | 这本书**实际**怎么写的（可证的局部事实） | `reference_feature_observations` | `feature_family/feature_key` + `value_*` + `evidence_start/end` + `explanation` |
| ② 机理标本 specimen | **为什么**这样写、**什么条件下**有效/失效 | `reference_technique_specimens` | `why_it_works_json` + `effect_on_reader` + `trigger_context` + `world_context_dependencies` + `failure_modes` + `anti_patterns` + `transfer_template` + `transfer_slots_json` |
| ③ 书级策略 strategy | 整本书归纳出的写作策略 | 统一外壳新表（第 2.3 节） | 汇总 ①②，逐条指向 evidence |

**关键规则**：事实是**证据**，机理是**产物**。一条事实若摘出来对"怎么写"没有启发，不进库——避免退化成设定百科。

### 2.2 五类 family（封闭词表，必须冻结）

| family | 覆盖你说的 | 主要承载 |
|---|---|---|
| `world`（世界观） | 世界观 | 设定如何被引入/展示/施加压力，而非设定本身 |
| `style`（文风） | 文风 | 复用现有 `ReferenceStyleTaxonomy`（6 类 26 特征，`src/Novelist.Contracts/App/ReferenceStylePayloads.cs:115`） |
| `craft`（写法） | 写法 | 叙事策略级手法：视角控制、信息投放、场景推进、情感外化、节奏铺排——"这一段/这一章怎么组织" |
| `technique`（技巧） | 技巧 | 可点名的具体技法：伏笔、呼应、潜台词、延迟反应、对比、留白等，复用既有 `AllowedTechniques` 词表（`ReferenceMaterializationChatCompletionQualifier.cs:61`） |
| `structure`（结构/钩子） | （"等"） | 场景结构、钩子/兑现、节奏压缩 |

**写法 vs 技巧（D1 已定，拆列）**：`craft` 是**策略级**（面向整段/整章的组织方式，通常需上下文才成立）；`technique` 是**技法级**（可点名的局部手法，直接复用既有 `AllowedTechniques` 封闭词表）。二者不重复——同一现象归到更具体的一侧。

**D1 结论**：五类命名为 `world / style / craft / technique / structure`，`craft` 与 `technique` 分列。词表冻结后，分析器、复核 UI、检索过滤、覆盖度地图均按它实现。

**feature_key 词表（已冻结，2026-09-14）**：代码真值在 `src/Novelist.Contracts/App/ReferenceAdvancedMaterialVocabularies.cs`。`world`（`world_introduction` / `world_pressure` / `world_consistency`）、`craft`（`information_delivery` / `viewpoint_control` / `scene_progression` / `emotion_externalization`）、`structure`（`hook_type` / `payoff_type` / `pacing_shape` / `transition_mode`）为新增键；`technique` 复用材料化既有技法词表（单一键 `technique_kind`）；`style` 由 `ReferenceStyleTaxonomy` 直接派生（26 键）。

**文风不重造**：`style` 类直接复用已建成的 `ReferenceStyleProfile`（含确定性基线 + LLM 分析 + 证据 span + 比较/归档/恢复，Bridge 已通）。高级素材层不新建文风表，只把风格画像纳入统一外壳与统一检索。

### 2.3 统一外壳新表（D2 已定）

新增表 **`reference_advanced_materials`** 作为 L2 的唯一对外存储与消费入口（三层 × 五类同表）；`strategy` 层直接写入本表，`reference_aggregates` 不再作为书级策略落点。既有 `reference_feature_observations` / `reference_technique_specimens` 作为**生产侧明细表保留**，其记录随代次**投影**进新表（沿用材料化 `SqliteReferenceMaterializationLibraryProjection` 的既有投影范式：源表不删、不二次改写）。

```
reference_advanced_materials
- material_id            主键
- anchor_id              每书一套
- layer                  observation | specimen | strategy
- family                 world | style | craft | technique | structure
- feature_key            该 family 下的具体维度（封闭词表）
- value_json / value_text        事实层与策略层的取值载荷
- rationale_json         机理层专有：why_it_works / effect_on_reader
- boundary_json          机理层专有：world_context_dependencies / failure_modes / anti_patterns
- transfer_template / transfer_slots_json   机理层专有（只给骨架 + 空槽）
- evidence_refs_json     → source_node_id / material_id / 偏移（强制非空）
- confidence
- review_state           unverified | confirmed | rejected
- validity_state         active | superseded
- superseded_by_run_id
- analysis_run_id / extractor_version
- created_at / updated_at
```

**两条硬约束（数据层强制，非口头约定）**：
1. 任何 L2 记录 `evidence_refs` 必须能解析到真实 L1 证据（节点/材料 + 偏移）；解析失败即整条无效。
2. `layer = specimen` 的记录 `boundary` 三字段必填——没有失效边界的"机理"不算机理。

## 3. 数据模型

### 3.1 复用（已存在，无需新建）

> 下表在统一外壳新表落地后定位调整为**生产侧明细表**（第 2.3 节）：对外读取、复核与消费一律走 `reference_advanced_materials`。

- `reference_feature_observations`：`src/Novelist.Infrastructure/App/ReferenceCorpusObservationWriter.cs:37`，已有 `review_state`（默认 `unverified`）、`validity_state`、`superseded_by_run_id`。
- `reference_technique_specimens`：`src/Novelist.Infrastructure/App/ReferenceCorpusTechniqueSpecimenPersistence.cs:36-46`，已有 `why_it_works_json / world_context_dependencies / failure_modes / anti_patterns / transfer_template / transfer_slots_json / effect_on_reader / review_state / mastery_notes`。
- `reference_style_profiles` + `reference_material_style_tags`：文风类。
- feature family schema：`src/Novelist.Core/App/ReferenceCorpusFeatureSchemas/*.json` 与注册表 `ReferenceCorpusFeatureFamilySchemas.cs`。
- 四路召回引擎（同时读 L1+L2）：`SqliteReferenceCorpusService.SearchCandidatesAsync`（`src/Novelist.Infrastructure/App/SqliteReferenceCorpusService.cs:54`），技法规 `:1017-1028`、observation 规 `:1532+`。
- L1 语料检索：`IReferenceAnchorService.SearchMaterialsAsync / SearchMaterialsBatchAsync`（`SqliteReferenceAnchorService.cs:1642/1653`）。
- 写作注入现状：`FileSystemChatSessionService.BuildChapterCorpusInjectionAsync`（`:2093-2181`）与覆盖度 `ChapterCorpusCoverageService.ComputeCoreAsync`（`:68-162`）。

### 3.2 需新增 / 调整

- **family 词表**：为 `world / craft / technique / structure` 补 feature schema（`ReferenceCorpusFeatureSchemas/` 下新增 JSON）与 family 注册；`style` 走既有 taxonomy。
- **统一外壳新表**：新增 `reference_advanced_materials`（第 2.3 节）；`strategy` 层直写，`observation` / `specimen` 投影进表；`reference_aggregates`（`SqliteReferenceCorpusGovernanceService.Aggregates.cs:10`）不再作为书级策略落点。
- **统一检索入参**：让结构化需求（第 6 节）能同时命中 L1 与五类 L2。

## 4. 生产管线（一条，不分批上线）

```
导入/切分（已有 useg）
  → ① 事实观测抽取（family：world/craft/technique/structure；style 走风格画像）
  → ② 机理归纳（事实观测 → 机理标本）
  → ③ 书级策略聚合（①② → 策略，逐条挂证据）
  → 证据校验（evidence_refs 可解析，boundary 必填）
  → 复核队列（默认 unverified）
  → 可供消费
```

- 复用已保留的语料分析管线（`ReferenceCorpusAnalysis*` job/scheduler/worker，refocus §五保留项），扩展 family 与新任务类型，不新建调度。
- **分析期即可用**：`classification` 与机理归纳是 LLM 任务，必须走既有工具 schema + `dropUnknownValues` 封闭词表规则（对齐 `ReferenceMaterializationChatCompletionQualifier` 的做法），未知值丢弃并计入漂移告警。
- 一条竖切不单独上线：五类 family 与三层一起接通后，才进入验收。

## 5. 复核门

- 所有 L2 记录默认 `review_state = unverified`，**未复核不进入消费**（检索注入只取 `confirmed`；`unverified` 仅可浏览）。
- 复核卡片：原文证据对照 + 机理判断 + 单键确认/驳回（沿用 refocus §六 卡片化复核）。
- 抽样策略沿用默认（高置信抽查 5–10%，跑完 5–10 本书后按错误率调整，refocus 开放问题 7）。
- 代次替代：`validity_state + superseded_by_run_id` 已具备，重跑不删旧数据。

## 6. 消费端：结构化需求 + L1/L2 混合自动检索

### 6.1 现状与接入点

当前生成侧只有 L1：把 `next` 章节计划整段文本**截断 160 字符**当纯文本 query → `SearchMaterialsAsync` top-5 → 拼成 `<reference-corpus>` 系统消息（`FileSystemChatSessionService.cs:2093-2181`）；L2 完全未参与。

接入点（按改造顺序）：

1. **需求构造缝**：`BuildChapterCorpusInjectionAsync` 与 `ChapterCorpusCoverageService.ComputeCoreAsync`——把"原文/整行 beat 当 query"替换为结构化 need。
2. **统一检索缝**：`SearchMaterialsBatchAsync`（聊天注入与覆盖度已共用）扩为 L1+L2 混合；或直接把两条通路改调 `SearchCandidatesAsync` 四路引擎（已是"写作注入的唯一消费通路"设计，`lightweight-refocus-proposal-2026-08-31.md:113`）。

### 6.2 结构化 need

need 的来源是当前章节计划/细纲的 beat。字段直接复用既有契约（`ReferenceCorpusQueryContextPayload`，`src/Novelist.Contracts/App/ReferenceCorpusPayloads.cs:81-90`；`SearchReferenceMaterialsPayload`，`ReferenceAnchorPayloads.cs:581-618`）：

```
need
- query                  场景/事件的自然语言描述（不再是整段截断）
- narrative_duties       → MatchesNarrativeDuty 既有词表
- prose_duties           → MatchesProseDuty
- emotion_transitions    → MatchesEmotionTransition
- scene_tags / function_tags / pov_tags / technique_tags
- style_profile_ids      → 默认参与（不再是可选）
- scope / chapter_context
```

**规则**：beat → need 的映射必须显式、可复核（不再用 160 字截断文本冒充 query）。

### 6.3 混合检索与融合

- 一次检索同时返回 L1 语料与 L2 高级素材（五类），统一排序、统一 provenance。
- L2 只取 `review_state = confirmed` 且 `validity_state = active`。
- 融合与过滤沿用既有能力：`MatchesMaterialFilters`（license/ready 边界）、`ScoreMaterialComponents`（含 `style_fit`、`accepted_feedback`、`source_risk_penalty`）、去重与跨库多样性、`local_context_fit`。
- **L2 的定价**：机理/策略类条目在排序中不作为"可复用文本"计分，而是作为**约束与模板**注入（见 6.4），避免把机理当句子用。

### 6.4 注入模板

按 refocus 开放问题 8 定稿结构扩展为两层素材：

```
本章细纲/beat
→ 方法论要点（技能层，行为指引）
→ L2 高级素材：机理 + 迁移骨架 + 失效边界（"为什么这样写"，标注"仅作方法参考，不得直搬"）
→ L1 语料：top-K 3–5 段原文范例（带观察标注）
→ 写作指令
```

上下文预算按模型窗口自适应（refocus 开放问题 8）。

## 7. 不可直搬边界（防洗稿）

这是"分析为什么这样写"与"直搬"的分水岭，必须可自动化断言：

1. **产物层无原文长句**：L2 记录不得包含超过设定长度的连续原文；引用只以 evidence 指针 + 短片段标注存在。
2. **专有名不入模板**：`transfer_template` 的槽位化——人名/地名/设定专名只能出现在 `transfer_slots`，不进入模板骨架本身。
3. **机理必带依赖与失效**：`world_context_dependencies` / `failure_modes` / `anti_patterns` 非空（第 2.3 节硬约束）。
4. **注入层标注**：注入模板显式声明"高级素材为方法参考，禁止复制具体表述与设定"。
5. **可断言测试**：对产出做扫描——L2 文本与原文的最长公共子串 / n-gram 重叠不得超过阈值；`transfer_template` 不含专名。

## 8. Bridge 与前端

- **Bridge：按新表新开只读/复核方法（D3 已定）**，不复用退役读取方法的语义。命名如下（实施时最终确认）：
  - `ListReferenceAdvancedMaterials`(anchor_id, family?, layer?, review_state?, page, size) → 分页摘要；
  - `GetReferenceAdvancedMaterialDetail`(material_id) → 明细，含 `evidence_refs` 解析后的证据原文片段与偏移；
  - `ReviewReferenceAdvancedMaterial`(material_id, decision, note?) → 写 `review_state`；
  - 文风：继续复用既有 `GetReferenceStyleProfile(s)` / `BuildReferenceStyleProfile`，不重复造。
  - 生产触发：两条路径，共用同一 `IReferenceAdvancedMaterialPipelineService`，不新建调度：
    - ① 显式按需：`StartReferenceAdvancedMaterialAnalysis`(anchor_id, run_id?) —— 单本书一趟跑完观测→机理→策略（LLM 长任务，前端按长超时调用）。
    - ② **并入既有语料分析调度**：`ReferenceCorpusAnalysisWorker` 在 `feature_analysis` 作业完成时顺带跑一趟管线；幂等（该书已有高级素材则跳过），失败静默、不影响分析作业本身。
- 新增方法须同步：`BridgeCompatibilityAppMethods.MethodNames`、`frontend/src/lib/novelist/api.ts`、`types.ts`、bridge 注册测试（见 `overview-architecture-map.md` Bridge Model）。
- **前端入口**：并入现有语料区四视图，不新增顶级入口。建议在"总览"展示高级素材的 N/M/K（沿用 refocus §六"成长可感知"）+ 在"浏览"增 family/layer 筛选与复核；**不新增专家控制面**。
- 写作侧：注入用量卡（`CorpusUsageCard`）扩展为同时展示 L2 条目与出处，保持"用后可见"。

## 9. 验收（整块 Definition of Done）

整块一次验收，全部满足才算通过：

1. **五类 × 三层产出完整**：任选一本参考书，能产出 `world/style/craft/technique/structure` 五类、`observation/specimen/strategy` 三层的完整材料。
2. **证据可回链**：每条 L2 记录可点回真实原文证据；evidence 解析失败率为 0。
3. **边界完备**：每条机理均有失效边界与非空迁移骨架；专名不入模板。
4. **防直搬可断言**：第 7 节的扫描测试通过（无超阈原文、模板无专名）。
5. **复核门有效**：未复核素材不进入注入；检索只取 `confirmed + active`。
6. **消费生效**：生成正文时，结构化 need 触发 L1+L2 混合注入；对比"仅 L1"与"L1+L2"的细节质感（作者评分，可加盲评，对齐 refocus §七）。
7. **不越界**：未复活拼装线；未新增专家控制面；`dotnet test` / 前端 `verify` / 既有工作流全绿。

## 10. 待作者确认项

- ~~**D1**~~（已定）：五类 family `world / style / craft / technique / structure`，`craft`（写法）与 `technique`（技巧）分列。
- ~~**D2**~~（已定）：书级策略收敛进统一外壳新表 `reference_advanced_materials`，`reference_aggregates` 不再作为策略落点。
- ~~**D3**~~（已定）：按新表新开只读/复核方法（`ListReferenceAdvancedMaterials` / `GetReferenceAdvancedMaterialDetail` / `ReviewReferenceAdvancedMaterial`），不复用退役读取方法语义。
- **D4**：单 beat 命中的"可用"相关度阈值（refocus 遗留子问题，实施期用真实语料校准；不阻塞动工）。
- ~~**D5**~~（已定）：need 初始字段集即取第 6.2 节清单（`query / narrative_duties / prose_duties / emotion_transitions / scene_tags / function_tags / pov_tags / technique_tags / style_profile_ids / scope / chapter_context`）。

## 11. 风险与既有教训

- **范围失控**：上一版 observation/specimen 死于"产物噪音 + 无消费出口"。本方案以"产物出口唯一 + 证据强制 + 复核门 + 防直搬断言"对冲；**动工前必须先冻结第 10 节的决策点**，否则整块会漂。
- **机理可靠性**：LLM 讲"为什么这样写"能讲得通顺却可能全错——这是本功能最大风险。缓解只能是**证据可回链 + 人工复核门 + 失效边界必填**，不寄望于自动校验代替复核。
- **需求构造过粗**：当前 160 字截断文本已是明显短板；need 必须结构化，否则"按需取素材"名不副实。
- **与文风画像的边界**：`style` 走既有 `ReferenceStyleProfile`，不重造；统一外壳只做聚合展示与统一检索，不改其内部契约。

## 12. 实施任务拆解（整块一次交付，非分批上线）

拆解只用于组织施工与并行，不改变"一次验收"规则：T1–T14 全部完成后，按第 9 节整体验收。任何一条未完成，整块不交付。

### 数据与契约

- **T1 统一外壳表**：新增 `reference_advanced_materials`（第 2.3 节字段）+ 索引（`anchor_id` / `family` / `layer` / `review_state`）；随 `ReferenceCorpusSchemaProvisioner` 走 additive migration，旧表不动。
- **T2 family 词表与 feature schema**：feature_key 词表冻结于 `src/Novelist.Contracts/App/ReferenceAdvancedMaterialVocabularies.cs`（`world/craft/structure` 新键、`technique` 复用既有技法词表、`style` 由 `ReferenceStyleTaxonomy` 派生）。实现改用 C# 词表而非 JSON schema——统一外壳的记录形状（rationale/boundary/transfer）与既有 node-observation 校验器不同，硬塞会破坏其启动期不变量（"每个 family 必须有 schema + node_type 强匹配"）。**五类词表 + 各 family feature_key 冻结**。
- **T3 契约与 Bridge 方法**：新增 L2 DTO（`src/Novelist.Contracts/App/`）；新增 `ListReferenceAdvancedMaterials` / `GetReferenceAdvancedMaterialDetail` / `ReviewReferenceAdvancedMaterial`；同步 `BridgeCompatibilityAppMethods.MethodNames`、`api.ts`、`types.ts`、注册测试。

### 生产管线（复用 `ReferenceCorpusAnalysis*`，不新建调度）

- **T4 事实观测抽取**：按 family 分趟抽取 `observation`，强制 evidence span；未知词丢弃 + 漂移告警。
- **T5 机理归纳**：由 observation 归纳 `specimen`，产出 `rationale` / `boundary` / `transfer`；`boundary` 三字段非空为硬门。
- **T6 书级策略聚合**：由 ①② 归纳 `strategy`，**直写新表**，逐条挂 evidence。
- **T7 投影与证据校验**：旧表 → 新表投影（`SqliteReferenceAdvancedMaterialProjectionService`）。family 映射：`syntax/rhythm/sensory/emotion/rhetoric/narrative/pov/action/character → craft`，`commercial/scene/trope → structure`，技法标本 `→ technique`，`style` 由风格画像提供。旧行保留原 `feature_key` 并标 `extractor_version='legacy-projection-v1'`（冻结词表只约束新管线产出）。证据解析失败即整条作废并计入 `InvalidEvidenceCount`。

### 复核与消费

- **T8 复核闭环**：`review_state` 默认 `unverified`；`ReviewReferenceAdvancedMaterial` 单键确认/驳回；未复核不进消费。
- **T9 结构化 need 构造**：`BuildChapterCorpusInjectionAsync` + `ChapterCorpusCoverageService.ComputeCoreAsync` 改为按第 6.2 节构造结构化 need（替换 160 字截断文本）。
- **T10 L1+L2 混合检索**：`SearchMaterialsBatchAsync` 扩为混合，或改调 `SqliteReferenceCorpusService.SearchCandidatesAsync` 四路引擎；L2 只取 `confirmed + active`；统一排序与 provenance。
- **T11 注入模板**：按第 6.4 节扩展 `<reference-corpus>` 注入（方法论 → L2 机理 → L1 范例 → 指令），带"禁止直搬"标注。

### 前端与防直搬

- **T12 语料区展示与复核 UI**：总览加 L2 的 N/M/K；浏览加 family/layer 筛选 + 明细（证据原文对照）+ 复核；在 `CorpusAreaView.tsx` 内扩展。
- **T13 用量卡扩展**：`CorpusUsageCard` 同时展示 L2 条目与出处。
- **T14 防直搬断言 + 整块验收**：第 7 节扫描测试（超阈原文、模板专名）；第 9 节 7 条 DoD 全覆盖的自动化 + 作者盲评。

**依赖顺序**：T1–T3 先行（契约冻结点）；T4–T7 依赖 T1/T2；T8–T11 依赖 T3/T7；T12–T13 依赖 T3；T14 收尾。T4–T7 与 T9–T11 可并行。
