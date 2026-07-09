# 语料驱动写作系统 — 开发完善方案（v2）

> 本文档基于对 reference-anchor 体系失败教训的总结，规划新的**语料驱动写作（Corpus-Driven Writing）**系统。核心原则：AI 不创作，AI 只做检索、编排、最小变形；正文的情感和叙述质量由人写的语料保证。
>
> v2 修订：修复 v1 评审发现的 12 处基础模型缺口与交付节奏问题（对照表见下）。

---

## 修订对照表（评审问题 → v2 处理）

| # | 严重度 | 问题 | v2 处理 | 位置 |
|---|---|---|---|---|
| 1 | Critical | TextTree 未真正落库 | 新增 `reference_text_nodes` 稳定节点表（父子/顺序/offset/text_hash），所有分析以 node_id 为锚 | §5.1 |
| 2 | Critical | AnalysisRun/stale/复核状态混淆 | 拆分 `review_state`（人工）/ `validity_state`（机器生命周期）/ `superseded_by_run_id` | §5.2 §5.5 |
| 3 | Critical | 管线边界自相矛盾、200 万字不可验证 | 明确分层：确定性全量 + 段落 LLM 全量 + 句级 LLM 全量但异步/续跑/预算上限；双轨验收 | §6 |
| 4 | Critical | 版权/授权无工程边界 | 新增 license gate 模型 + 插入前相似度阈值 + 硬性插入闸门 | §5.6 §7 |
| 5 | High | 当前章节上下文未进检索核心 | `CorpusQueryContext` 内嵌 `CurrentChapterContext`，参与后端排序 | §5.7 |
| 6 | High | 公共库仍是 anchor/source 思维 | 新增语料库/成员/会话绑定模型 + 启用规则/去重/来源质量 | §5.6 |
| 7 | High | 强类型 schema 与 feature_value TEXT 冲突 | `value_kind` + `value_num`/`value_bool`/`value_json` + 热路径 projection 表 | §5.2 |
| 8 | High | 图关系放 JSON 数组，失效/追溯脆弱 | 拆 junction tables（specimen_evidence / template_examples / beat_pieces） | §5.4 |
| 9 | High | 分页/查询契约未闭合 | 定义 `PageRequest`/`PageResult<T>`，排序稳定性 + pageSize 上限 + 错误语义 | §5.8 |
| 10 | High | 端到端闭环排太后 | 里程碑重排：M0 修地基 → M1 纵向薄切片跑通闭环 → 再逐层加深 | tasks.md |
| 11 | Medium | 前端偏专家系统 | 默认自动模式（写目标/选结果/改少量项），检查表等收进"专家展开" | §8 |
| 12 | Medium | 验收主观、无回归资产 | 固定 fixture + golden JSON + fake LLM + 性能预算 + 恢复脚本 | tasks.md §跨里程碑 |

---

## 一、reference-anchor 失败教训

**根本原因：锚定隐喻从一开始就锁死了错误的交互模型。**

"锚定"意味着用户把参考材料手动挂到章节上——被动的、手动的、一对一绑定。每个后续 Phase 都在加固这个错误：

| Phase | 做了什么 | 错在哪 |
|---|---|---|
| Phase 14-15 | 建 pipeline、recovery、审计 | 加固了"归档系统"而非"检索引擎" |
| Phase 16 | 素材库/章节参考分离 | 分离了两个本身就错误的东西 |
| 蓝图生成 | LLM 写蓝图，reference 是参考上下文 | AI 在创作，不是在组装语料 |
| 正文生成 | LLM 按 L0-L4 改写材料 | AI 在生成，不是在最大化复用原句 |
| 分析层 | 5 个标签 | 完全不足以支撑多维检索 |
| 前端交互 | 专家级 UI | 太重，不是检查表驱动 |

**结论：底层基础设施（SQLite/pipeline/bridge/recovery）可保留。需要重建的是核心业务逻辑——数据模型、分析深度、蓝图生成方式、正文生成方式、交互模型。**

---

## 二、核心设计转变

| 维度 | 旧模型 | 新模型 |
|---|---|---|
| AI 的角色 | AI 创作，语料是灵感 | AI 检索+编排+最小变形，语料是输出主体 |
| 蓝图生成 | LLM 写蓝图 | 语料检索 → 拼装方案 |
| 正文生成 | LLM 按级别改写 | 最大化保留原句，外科手术式替换 |
| 分析深度 | 5 个粗标签 | 多维 FeatureObservation 图谱 |
| 交互模型 | 专家手动操作 | 自动模式默认，专家展开可选 |
| 用户输入 | 表单填参数 | 自然语言大纲 → 系统解析 QueryContext |

### 人机分工

| 环节 | 负责方 |
|---|---|
| 无版权小说收集与导入 + 授权状态标注 | 人 |
| license gate 校验、去重、相似度检查 | AI 强制 |
| 全量语料分析 | AI 自动 |
| 大纲/目标规划 | 人 |
| 目标解析、语料检索、蓝图生成 | AI 自动 |
| 蓝图审核（自动模式：选结果；专家模式：检查表） | 人 |
| 蓝图迭代重生成 | AI 自动 |
| 正文拼装（最大化原句复用、槽位替换） | AI 自动 |
| 正文审核 + 微调 | 人 |

**不做长期偏好学习**：作品风格随项目变换，跨项目偏好无意义。

---

## 三、多粒度写作特征图谱

原文结构与分析结果分离。世界观是平行于文本树的语义上下文层，不是文本单元。

```
CorpusSource
├── TextTree（reference_text_nodes，稳定落库）
│   Chapter → Scene → Passage → Sentence → Clause
│   每节点：node_id / parent / sequence / offset / text_hash
└── WorldModel（语义上下文层，平行于 TextTree）
    power_system / character_map / faction_structure
    reader_contracts / established_facts / genre_conventions
```

### 四个粒度的分析能力边界

| 粒度 | 分析什么 | 需要什么上下文 |
|---|---|---|
| 句式 | 节奏、修辞、感官、情绪、句法 | 句子本身 + 前后句 |
| 段落 | 叙事功能、视角、动作链、人物 | 所在场景 |
| 章节 | 节奏曲线、钩子结构、铺垫回收 | 前 N 章（走 text_nodes 章节窗口） |
| 世界观 | 类型约定、力量体系、隐性读者契约 | 全书 + 类型知识 |

### 十个分析维度（feature_family）

句法 / 节奏 / 叙事功能 / 视角 / 感官 / 情绪 / 动作 / 人物 / 修辞 / 网文商业（元层，切穿其他九层，解释组合为何有效）。每个 family 的锁定 schema 见 tasks.md M2。

---

## 四、保留与重建边界

**直接保留：** SQLite 存储层 + additive migration；导入/Stage 0-1 rule-based 提取 + recovery/reconcile；embedding + sqlite-vec；bridge adapter；`reference_user_feedback`；处理记录/审计可追溯。

**扩展：** `reference_anchors` 关联 license 表；现有 `reference_source_segments`/`reference_materials` 加 `node_id` FK 指向 text_nodes。

**重建（逻辑层）：** `GenerateChapterBlueprintAsync`（LLM 生成→检索拼装）；`GenerateDraftFromBlueprintAsync`（改写→最大化原句复用）；`ReviewChapterBlueprintAsync`（检查表评分）；前端 `OrchestrationPanel`/`BlueprintDetail`。

**新增：** text_nodes + 分析表 + junction 表 + license/library 表；`ICorpusAnalysisService`、`IQueryContextParser`、`ICorpusBlueprintRetriever`、`ICorpusBlueprintAssembler`、`ICorpusTextAssembler`、`ICorpusWritingSessionService`；写作会话 UI。

---

## 五、数据模型

### 5.1 文本节点树（修复 #1）

```sql
CREATE TABLE reference_text_nodes (
    node_id         TEXT PRIMARY KEY,
    anchor_id       INTEGER NOT NULL REFERENCES reference_anchors(anchor_id),
    parent_node_id  TEXT REFERENCES reference_text_nodes(node_id),
    node_type       TEXT NOT NULL,      -- chapter|scene|passage|sentence|clause
    sequence_index  INTEGER NOT NULL,   -- 兄弟节点间顺序
    depth           INTEGER NOT NULL,
    chapter_index   INTEGER,            -- 反规范化，供章节窗口查询
    start_offset    INTEGER NOT NULL,   -- 相对源文本
    end_offset      INTEGER NOT NULL,
    char_len        INTEGER NOT NULL,
    text_hash       TEXT NOT NULL,      -- 正文 hash 校验的基准
    text            TEXT NOT NULL,
    created_at      DATETIME NOT NULL
);
CREATE INDEX ix_text_nodes_parent  ON reference_text_nodes(parent_node_id, sequence_index);
CREATE INDEX ix_text_nodes_atype   ON reference_text_nodes(anchor_id, node_type);
CREATE INDEX ix_text_nodes_chapter ON reference_text_nodes(anchor_id, chapter_index, sequence_index);
```

`reference_materials` 保留，加 `node_id` FK 指向其派生的 sentence/passage 节点。evidence_span、章节窗口、原文跳转、正文 hash 校验全部以 text_nodes 为唯一锚点，不再漂移。

### 5.2 特征观察（修复 #2 #7）

```sql
CREATE TABLE reference_feature_observations (
    observation_id       TEXT PRIMARY KEY,
    node_id              TEXT NOT NULL REFERENCES reference_text_nodes(node_id),
    node_type            TEXT NOT NULL,
    run_id               TEXT NOT NULL REFERENCES reference_analysis_runs(run_id),
    anchor_id            INTEGER NOT NULL,
    feature_family       TEXT NOT NULL,
    feature_key          TEXT NOT NULL,
    value_kind           TEXT NOT NULL,   -- enum|number|bool|array|object（修复 #7）
    value_text           TEXT,            -- 展示 / enum 值
    value_num            REAL,            -- 数值范围查询
    value_bool           INTEGER,
    value_json           TEXT,            -- 数组/对象结构
    intensity            REAL,
    confidence           REAL NOT NULL,
    evidence_start       INTEGER,         -- 相对 node text 的 offset
    evidence_end         INTEGER,
    explanation          TEXT,
    review_state         TEXT NOT NULL DEFAULT 'unverified',  -- 人工：unverified|confirmed|rejected|conflicted（修复 #2）
    validity_state       TEXT NOT NULL DEFAULT 'active',       -- 机器：active|superseded（修复 #2）
    superseded_by_run_id TEXT,
    created_at           DATETIME NOT NULL
);
CREATE INDEX ix_obs_family ON reference_feature_observations(anchor_id, feature_family, feature_key, value_text);
CREATE INDEX ix_obs_num    ON reference_feature_observations(anchor_id, feature_family, feature_key, value_num);
CREATE INDEX ix_obs_node   ON reference_feature_observations(node_id, run_id, validity_state);
-- 幂等写入（护栏 G1）：并发/重试/续跑不重复写
CREATE UNIQUE INDEX ux_obs_generation_key ON reference_feature_observations(
    run_id, node_id, feature_family, feature_key,
    IFNULL(evidence_start, -1), IFNULL(evidence_end, -1));
```

**幂等写入模型（护栏 G1，修复 #1）**：`observation_id` 由生成键确定性派生 —
`observation_id = hash(run_id, node_id, feature_family, feature_key, evidence_start, evidence_end)`。
写入用 `INSERT ... ON CONFLICT(生成键) DO UPDATE`（upsert），A/B 并发、失败重试、预算中断续跑均幂等，同一 (run, node, feature) 只有一条 active observation。数组型 family（sensory/rhetoric）以整条数组为一个 observation，projection 表随 upsert 重建，不拆多行写入以免键冲突。

**热路径 projection 表（修复 #7）**：感官、节奏等高频结构化查询维度，建派生投影表以支持数组/范围查询，例如：

```sql
CREATE TABLE reference_obs_sensory (
    observation_id TEXT NOT NULL REFERENCES reference_feature_observations(observation_id),
    node_id        TEXT NOT NULL,
    anchor_id      INTEGER NOT NULL,
    sense          TEXT NOT NULL,   -- 视觉|听觉|触觉|嗅觉|温度|重量|湿度|空间压迫|身体反应
    intensity      REAL NOT NULL,
    PRIMARY KEY (observation_id, sense)
);
CREATE INDEX ix_obs_sensory_q ON reference_obs_sensory(anchor_id, sense, intensity);
```

投影表由分析写入时同步维护，schema 演进时重建。非热路径维度只用 value_json。

### 5.3 技法标本（修复 #2 #8）

```sql
CREATE TABLE reference_technique_specimens (
    specimen_id                TEXT PRIMARY KEY,
    source_node_id             TEXT NOT NULL REFERENCES reference_text_nodes(node_id),
    source_anchor_id           INTEGER NOT NULL,
    analysis_run_id            TEXT NOT NULL,
    technique_family           TEXT NOT NULL,
    technique_abstract         TEXT NOT NULL,
    trigger_context            TEXT NOT NULL,
    transfer_template          TEXT NOT NULL,
    transfer_slots_json        TEXT NOT NULL,
    effect_on_reader           TEXT NOT NULL,
    applicability_conditions   TEXT NOT NULL,
    failure_modes              TEXT NOT NULL,
    anti_patterns              TEXT NOT NULL,
    world_context_dependencies TEXT,
    why_it_works_json          TEXT NOT NULL,
    confidence                 REAL NOT NULL,
    review_state               TEXT NOT NULL DEFAULT 'unverified',
    validity_state             TEXT NOT NULL DEFAULT 'active',
    superseded_by_run_id       TEXT,
    mastery_notes              TEXT,
    created_at                 DATETIME NOT NULL
);
-- technique_abstract 向量存 sqlite-vec 表 reference_technique_vectors(specimen_id, embedding)
```

evidence 关系不放 JSON，改用 junction（见 5.4）。

### 5.4 图关系 junction 表（修复 #8）

```sql
CREATE TABLE reference_specimen_evidence (
    specimen_id    TEXT NOT NULL REFERENCES reference_technique_specimens(specimen_id),
    observation_id TEXT NOT NULL REFERENCES reference_feature_observations(observation_id),
    PRIMARY KEY (specimen_id, observation_id)
);
CREATE TABLE reference_template_examples (
    template_id TEXT NOT NULL,
    node_id     TEXT NOT NULL REFERENCES reference_text_nodes(node_id),
    PRIMARY KEY (template_id, node_id)
);
CREATE TABLE reference_blueprint_beat_pieces (
    beat_id        TEXT NOT NULL,
    node_id        TEXT NOT NULL REFERENCES reference_text_nodes(node_id),
    observation_id TEXT REFERENCES reference_feature_observations(observation_id),
    role_in_beat   TEXT,
    sequence_index INTEGER NOT NULL,
    PRIMARY KEY (beat_id, node_id)
);
-- 聚合知识来源溯源（护栏 G7，修复 #7）：cross-corpus 模板/画像记录输入源
CREATE TABLE reference_aggregate_provenance (
    aggregate_id   TEXT NOT NULL,   -- template_id / profile_id / world_model_id
    aggregate_kind TEXT NOT NULL,   -- scene_template|style_profile|world_model|dialogue_technique
    library_id     TEXT,            -- 跨语料时的库
    anchor_id      INTEGER NOT NULL,
    run_id         TEXT NOT NULL,   -- 输入分析 run
    PRIMARY KEY (aggregate_id, anchor_id, run_id)
);
```

FK 保证可 join、可级联失效、可高效追溯。specimen/blueprint 失效时按 junction 精确定位受影响边。**聚合知识（护栏 G7）**：任一源 anchor 重跑（产生新 run），按 `reference_aggregate_provenance` 定位依赖它的 cross-corpus 模板/画像并标 stale，触发重建。

### 5.5 分析运行与重跑语义（修复 #2）

```sql
CREATE TABLE reference_analysis_runs (
    run_id            TEXT PRIMARY KEY,
    anchor_id         INTEGER NOT NULL,
    analyzer_version  TEXT NOT NULL,
    schema_version    TEXT NOT NULL,
    model_provider    TEXT NOT NULL,
    model_id          TEXT NOT NULL,
    scope             TEXT NOT NULL,   -- sentence|passage|chapter|full
    status            TEXT NOT NULL,   -- running|paused|budget_exhausted|partial_completed|completed|failed（护栏 G2）
    token_budget      INTEGER,         -- per-run 预算上限（修复 #3）
    tokens_spent      INTEGER NOT NULL DEFAULT 0,
    resume_cursor     TEXT,            -- 续跑检查点：最后完成的 (node_id, feature_family)（护栏 G2）
    started_at        DATETIME NOT NULL,
    completed_at      DATETIME,
    observation_count INTEGER NOT NULL DEFAULT 0
);
```

**预算与续跑状态机（护栏 G2，修复 #2）**：
- `running` → 正常进行
- `paused` → 用户/系统暂停，可 resume
- `budget_exhausted` → 触及 `token_budget`，非失败；补预算后从 `resume_cursor` 续跑
- `partial_completed` → 分析终止但部分 node 已完成（例如 scope 内部分章节）
- `completed` / `failed` → 终态

`resume_cursor` 记录最后完成的 `(node_id, feature_family)`；续跑从游标之后开始，配合 G1 幂等写入，重复覆盖已完成 node 也不产生脏数据。查询"分析完成度"= 已完成 node / 总 node，实时可见。

**重跑语义（修复 #2）**：旧 observation **不删除**。新 run 完成后，被取代的旧 observation 置 `validity_state='superseded'`、`superseded_by_run_id=新run`，但 **`review_state` 原样保留**——用户确认过的判断不被污染。依赖旧 observation 的 specimen 触发 invalidation check：证据边全部 superseded 则 specimen 也标 superseded。查询默认 `validity_state='active'`。

### 5.6 语料库与授权（修复 #4 #6）

```sql
-- 产品级语料库（修复 #6）
CREATE TABLE reference_corpus_libraries (
    library_id TEXT PRIMARY KEY,
    scope      TEXT NOT NULL,        -- global|project
    novel_id   INTEGER,              -- project scope 用
    name       TEXT NOT NULL,
    created_at DATETIME NOT NULL
);
CREATE TABLE reference_library_members (
    library_id      TEXT NOT NULL REFERENCES reference_corpus_libraries(library_id),
    anchor_id       INTEGER NOT NULL REFERENCES reference_anchors(anchor_id),
    enabled         INTEGER NOT NULL DEFAULT 1,
    source_quality  TEXT,            -- trusted|normal|low
    disabled_reason TEXT,
    dedup_group_id  TEXT,            -- 跨来源去重
    PRIMARY KEY (library_id, anchor_id)
);
CREATE TABLE reference_session_library_binding (
    session_id TEXT NOT NULL,
    library_id TEXT NOT NULL,
    PRIMARY KEY (session_id, library_id)
);

-- 授权闸门（修复 #4）
CREATE TABLE reference_source_license (
    anchor_id             INTEGER PRIMARY KEY REFERENCES reference_anchors(anchor_id),
    license_state         TEXT NOT NULL,  -- unknown|public_domain|cc|authorized|restricted|forbidden
    authorization_evidence TEXT,          -- 用户提供的授权凭证/说明
    reuse_policy          TEXT NOT NULL,  -- verbatim_ok|adapted_only|reference_only|forbidden
    max_verbatim_ratio    REAL,           -- 输出逐字相似度上限
    cleared_for_insertion INTEGER NOT NULL DEFAULT 0,
    reviewed_at           DATETIME
);
```

检索默认作用域 = 会话绑定的 libraries 中 `enabled=1` 且 license 允许复用的成员；去重按 dedup_group_id 折叠；低质量来源降权。

### 5.7 查询上下文（修复 #5）

```csharp
public sealed record CorpusQueryContext(
    // 目标派生
    string SceneType,
    string EmotionTarget,
    string PacingTarget,
    string NarrativePosition,
    string CommercialMechanic,
    IReadOnlyList<string> CharacterStates,
    IReadOnlyList<string> RequiredNarrativeFunctions,
    // 当前章节上下文（进入后端排序，非仅 UI）
    CurrentChapterContext ChapterContext,
    CorpusScope Scope);

// 前端传的契约（护栏 G4，修复 #4）：不含 embedding，前端通常没有 embedding client
public sealed record CurrentChapterContext(
    long NovelId,
    int ChapterNumber,
    string? CurrentDraftText,          // 已写草稿
    int InsertionOffset,               // 插入位置
    string? PreviousChapterSummary,
    IReadOnlyList<CharacterStateSnapshot> CharacterSnapshots);
```

`CurrentChapterEmbedding` 由**后端**从 `CurrentDraftText` 计算并缓存（key = draft text hash），不进 bridge 契约，避免泄露实现细节。检索融合排序内部使用该 embedding 的语义连贯度、插入位置的上下文匹配纳入权重，从根上消除"素材库处理"与"当前章节使用"脱节。

### 5.8 分页与查询契约（修复 #9）

```csharp
public sealed record PageRequest(
    string? Cursor,
    int PageSize,                                   // 上限 200，超限报错
    string SortBy,
    string SortDir,                                 // asc|desc
    IReadOnlyDictionary<string, string>? Filters);

public sealed record PageResult<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasMore,
    int? TotalEstimate);
```

所有列表接口（FeatureObservation / TechniqueSpecimen / ReviewQueue / 检索结果）返回 `PageResult<T>`。排序稳定性：SortBy 后强制追加 `(created_at, id)` tiebreaker。错误语义：pageSize 超限、cursor 失效、filter 非法均返回明确错误码。

---

## 六、处理管线边界（修复 #3）

**分层，且句级 LLM 是全量的——但异步、可续跑、有预算上限。** 明确一次，不再矛盾：

| 层 | 范围 | 何时 |
|---|---|---|
| Stage 0 结构化 | 全书 → text_nodes 树（章/场/段/句/从句） | 导入后立即 |
| Stage 1 确定性特征 | 全书所有句子（句长/标点/停顿/词性/感官词/情绪词/实体/指代） | Stage 0 后立即，rule-based，无 LLM |
| Stage 2-段落 LLM | 全书所有段落 → narrative/pov/action/character/commercial | Stage 1 后，异步 |
| Stage 2-句级 LLM | 全书所有句子 → rhythm/syntax/sensory/emotion/rhetoric | 异步、按章节优先级排队、per-run token_budget 上限、可续跑 |
| Stage 3 综合推理 | 高置信度 / 用户标记节点 → TechniqueSpecimen | Stage 2 后 |

句级 LLM 全量但不阻塞：Stage 0-1 完成即可进入纵向闭环（M1 用基础特征就能检索）；Stage 2 句级分析在后台逐章补齐，完成度实时可见。中断从已完成 node 之后续跑（复用 reconcile）。

**验收双轨（修复 #3 #12）**：
- **正确性轨**：固定小 golden 书（约 500 句），全量跑，比对 golden JSON
- **规模轨**：合成 200 万字级 fixture，验证异步续跑、预算上限、章节窗口查询性能、中断恢复，不要求逐句人工核对

---

## 七、授权与插入闸门（修复 #4）

复用原句是核心，因此授权必须是**系统约束，不靠用户自觉**：

1. **导入即标注**：每本书必须设 `license_state` + `reuse_policy`；`unknown`/`forbidden` 不进检索默认作用域
2. **检索过滤**：只召回 `reuse_policy ∈ {verbatim_ok, adapted_only}` 的语料
3. **插入前闸门**（final_insertion 阶段硬校验）：
   - `cleared_for_insertion=1`
   - 输出逐字相似度 ≤ `max_verbatim_ratio`（对 verbatim_ok 可放宽，adapted_only 收紧）
   - 违反则阻断插入并提示，不允许绕过
4. **审计留痕**：每次插入记录来源 anchor、license_state、相似度、通过的闸门

### 7.1 相似度算法（护栏 G5，修复 #5）

闸门是硬约束，算法必须确定、可回归：

- **归一化**：先去空白、统一标点为占位、保留中文字符与数字；不做分词（避免分词器版本漂移）
- **粒度**：**piece-level**（拼装产出的每个片段 vs 其来源 node 原文），不是整章 source-level，避免长文本稀释
- **主指标（覆盖率）**：字符 **4-gram 容器度** = `|gram(输出) ∩ gram(来源)| / |gram(输出)|`，衡量输出有多少来自原文
- **辅指标（连续度）**：**最长公共子串比** = `LCS长度 / 输出长度`，衡量最长逐字照搬段
- **判定**：两指标各设阈值，任一超限即阻断；verbatim_ok 用宽阈值（允许高覆盖），adapted_only 用严阈值（强制改写量）
- **中文专名**：专名替换在相似度计算**之后**不重新计入——即槽位替换降低的相似度真实反映改写量，不被专名主导
- **确定性**：算法无随机、无模型调用，golden 测试可逐值断言

---

## 八、前端设计（修复 #11）

**双模式：默认自动，专家可展开。**

**自动模式（默认）**——用户只做三件事：
1. 写目标（自然语言大纲）
2. 从 N 份结果里选一份（蓝图 / 草稿）
3. 必要时改少量检查项

QueryContext 确认、来源选择、gap 处理、槽位表、过渡清单、锁定保护、草稿对比全部默认折叠/自动决策。系统用合理默认（全选授权语料、AI 建议的槽位替换、自动过渡）先给结果。

**专家展开**——需要时逐项打开：QueryContext 逐维修正、语料来源精选、槽位逐条确认、过渡逐处选择、锁定保护、多草稿并排 diff。

**核心交互原则**：问题优先（不是搜索优先）；可追溯（每片段可跳原文 + 分析依据）；渐进展示（先简洁结果，想深入再展开）。

---

## 九、实现顺序（修复 #10）

**纵向薄切片优先**，避免又交付一个"看起来很强但不能服务当前章节"的分析系统。详细里程碑见 tasks.md：

1. **M0 修地基 schema** — text_nodes / 分层 observation / junction / license / library / 分页契约（防返工，最先做）
2. **M1 纵向薄切片** — 导入一本书 → Stage 0-1 基础分析 → 当前章节检索 → 单蓝图 → 槽位替换 → 插入草稿（fake LLM + 小语料，自动模式，跑通闭环）
3. **M2 加深分析** — 10 family 锁定 schema、Stage 2 全量、技法标本
4. **M3 加深检索** — 四类索引融合、当前章节上下文进排序
5. **M4 加深蓝图** — N 策略、检查表反馈（专家模式）
6. **M5 加深拼装** — 完整槽位/过渡/hash 校验、多草稿
7. **M6 语料库产品化** — 库/成员/去重/授权闸门完善
8. **M7 聚合知识** — 作者画像/场景模板/世界观
9. **M8 复核工作流** — review/validity 状态机、重跑语义
10. **M9 专家 UI + 打磨**

---

## 十、关键设计难点备忘

1. **transfer_slots 格式** — 语义槽+约束（方案 B），展示层退化为模板字符串。
2. **why_it_works 可追溯** — 每 contributing_factor 必须 FK 到真实 observation（走 specimen_evidence），禁止空引用。
3. **technique_abstract 去内容化** — 输出后做泄露检测（不含原文专有名词）。
4. **最小化调整边界** — 只替换命名槽位，preserved_spans 逐字保留，hash 校验。
5. **商业层窗口** — 走 text_nodes 章节窗口（当前章/前 3 章/前 20 章）。
6. **重跑失效** — review_state 与 validity_state 分离；junction 精确定位受影响边。
7. **授权闸门** — 插入前硬校验，不可绕过。

---

## 十一、实施护栏（v2 评审补充，动工前必读）

以下 7 条是"不定义则测试与恢复不稳定"的工程护栏。**G1/G2/G3/G5 必须在 M0/M1 前解决**，否则纵向闭环无法回归。

| # | 护栏 | 要点 | 必须解决时点 |
|---|---|---|---|
| G1 | **幂等写入** | observation 生成键 + UNIQUE + upsert；并发/重试/续跑不重复写（§5.2） | **M0 前** |
| G2 | **预算续跑状态** | run status 加 `paused`/`budget_exhausted`/`partial_completed` + `resume_cursor`（§5.5） | **M0 前** |
| G3 | **fake embedding harness** | node/material embedding 构建任务 + 确定性假 embedding，golden 测试才可回归 | **M1 前** |
| G4 | **章节 embedding 后端算** | 前端只传 draft text/chapter id/offset，embedding 由后端算并缓存（§5.7） | M1 |
| G5 | **相似度算法** | 4-gram 容器度 + LCS 比，piece-level，归一化规则、中文专名处理明确（§7.1） | **M1 前** |
| G6 | **legacy orchestration 兼容** | 旧 run/blueprint/frontend 状态如何读取/恢复/迁移；不破坏 Phase 16 恢复逻辑 | M4 前 |
| G7 | **聚合知识溯源** | cross-corpus 模板记录 (library_id, anchor_id, run_id)，源重跑可定位 stale（§5.4） | M7 前 |

**G3 补充（embedding 构建）**：M1 需显式建 node/material embedding 生成任务（复用现有 `IEmbeddingClient`），并提供 fake embedding 替身——按文本 hash 生成确定性向量，保证 golden 检索结果逐次一致、可断言。

**G6 补充（legacy 兼容策略）**：
- 旧 `reference_orchestration_runs`（Phase 16 stage 命名）保持只读可恢复：新 stage 常量并存，旧 run 用兼容 shim 映射到旧展示，不强制迁移
- 新写作会话用新 stage 状态机，旧 run 不混入
- Phase 16 的 reconcile/recovery 逻辑对旧 run 继续生效；新会话走新 reconcile 路径
- 迁移（可选）：提供一次性脚本把旧 blueprint 转 read-only 归档，不自动改写
