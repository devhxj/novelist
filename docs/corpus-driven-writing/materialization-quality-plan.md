# 参考数据整章材料化重构计划

## 文档状态

- 日期：2026-07-20
- 状态：**已实施；真实用户验证仍开放**
- 适用主线：.NET 10 + Photino.NET + React/Vite
- 实施口径：本文是参考数据材料化的唯一目标方案；旧的 text node、候选窗口和 legacy corpus 路径不再作为兼容目标
- 关联文档：[开发完善方案](./development-plan.md)、[分阶段任务](./tasks.md)、[材料与审计决策](../reference-anchor-implementation/decisions-materials-and-audit.md)

## 一、目标

将参考数据收敛成一条可验证的产品闭环：

```text
导入参考书
  -> 分析并确认章节边界
  -> 大模型逐章提取材料
  -> 向量模型为全部材料生成向量
  -> 原子激活本次 generation
  -> 素材库预演和章节默认写作共同检索 active materials
```

本次是替换式改造，不在旧的句子、段落、场景、候选窗口体系上继续叠加抽象。

## 二、不可变决策

1. **整章是唯一材料化输入单位。** 一章对应一个确认后的章节边界、一次大模型材料提取和一条章节进度记录。
2. **不持久化句子、段落、场景或语义窗口。** 多段对话和跨段叙述由模型在整章上下文中直接判断。
3. **章节严格顺序处理。** 单个 worker 每次只领取一章；该章的材料、embedding 和索引全部提交后，才允许领取下一章。
4. **大模型和向量模型都是硬依赖。** 缺少配置、健康检查失败、请求失败、结构化输出非法、向量不完整或索引失败时，run 立即失败。
5. **不提供任何运行时兜底。** 不切窗口、不截断、不跳章、不降级到规则、词法、旧向量、JSON 扫描或其他模型。
6. **不接受空成功。** 任一非空章节没有产出材料时，该章节和整个 run 失败，不能以 0 materials 状态完成。
7. **材料必须来自原文。** 模型用一次性行号返回连续的 `start_line/end_line` 范围，服务端从当前归一化章节截取原文，可以跨多个段落；模型只生成说明和标签，不复写正文。
8. **generation 只承担原子发布。** 新 generation 全部完成前不进入检索；不建设 generation 回滚、历史浏览或兼容读取。
9. **失败 run 从章节提交点恢复。** 任一章节失败即停止；已完成章节及其材料保持不变，“运行全部”在同一 run/generation 中跳过 completed 章节并从首个未完成章节继续。用户也可强制运行指定章节，完成后停在 paused，不隐式继续后续章节。
10. **删除错误抽象。** 旧接口、旧表、旧测试和旧 UI 没有消费者后直接删除，不保留 adapter、双写、双读或 deprecated wrapper。

## 三、范围与停止线

### 3.1 本次范围

- 参考书登记和章节边界确认。
- 整章材料提取、严格校验、向量化和 active generation 发布。
- active materials 检索。
- 素材库蓝图预演。
- 章节默认蓝图、正文候选和来源审计对 active materials 的消费。
- 素材库工作台的最小操作、状态、错误和材料列表。
- 旧节点、候选和 legacy corpus 链路删除。

### 3.2 明确不做

- 句子、段落、scene、beat、dialogue exchange 或滑动窗口切分。
- 候选预筛、候选合并、accept/reject/review_required 状态机。
- 人工候选边界修正和候选复核工作台。
- 六维覆盖面板、复杂 facet、专家调度控制和质量分数仪表盘。
- 词法、结构化 observation、technique specimen 等多路召回。
- 旧材料、旧 text node、旧 candidate 的语义迁移或兼容查询。
- 自动回滚、自动换模型、自动降低并行度或自动重试。
- token 成本控制和超长章节特殊处理。

## 四、目标架构

```text
reference_anchors + source file
             |
             v
confirmed split profile + chapter boundaries
             |
             v
ReferenceMaterializationWorker
  - 按 boundary 读取完整章节
  - 每章调用一次 IReferenceChapterMaterialExtractor
  - 校验材料正文属于该章原文
  - 为该章全部材料生成 embedding
  - 章节结果以事务提交
             |
             v
generation vector index
             |
             v
atomic active_generation switch
             |
             +----------------------+
             |                      |
             v                      v
transient blueprint preview   default chapter writing
             \______________________/
                       |
               IReferenceMaterialSearch
```

章节原文不重复存储。worker 依据已确认 boundary 从 source file 读取整章，并在读取前验证 source hash。章节边界是定位信息，不是额外的文本层级。

## 五、最小数据模型

### 5.1 保留

- `reference_anchors`：参考书、来源路径、授权和 source hash。
- `reference_chapter_split_profiles`：自动或手工切分配置。
- `reference_chapter_split_boundaries`：确认后的章节标题、顺序、offset 和 text hash。
- `reference_materialization_runs`：冻结的 source、split profile、模型身份和运行状态。
- `reference_materialization_chapter_progress`：每章阶段、材料数、向量数和错误。
- `reference_anchor_materialization_state`：唯一 active generation 指针。
- lease 和 heartbeat：只保留实际支撑单 worker 崩溃恢复的字段，不再持久化批次配置或批次进度。

这些结构保留是因为其语义与新设计一致，不是为了兼容旧实现。

### 5.2 重建为唯一材料模型

最终只保留一张权威材料表和一张权威 embedding 表。以下是概念字段，实施时以 C# contract 为准：

```text
reference_materials
  material_id
  generation_id
  anchor_id
  chapter_index
  ordinal
  text                 # 必须是归一化章节中的连续原样文本
  metadata_schema_version # 固定为 reference-material-archive-v1
  metadata_json         # 严格最终中文素材档案
  text_hash
  created_at

reference_material_embeddings
  material_id
  generation_id
  provider
  model_id
  dimensions
  embedding_hash
  embedding_blob          # 规范化二进制向量，不作为 JSON 扫描兜底
  created_at
```

约束：

- `UNIQUE(generation_id, chapter_index, ordinal)`。
- 同一 generation 内材料正文去重。
- material 和 embedding 必须一一对应。
- active generation 的 material 数必须大于 0，且等于 embedding 与向量索引行数。
- `material_id` 由 generation、chapter index、ordinal 和 text hash 确定性生成。

### 5.3 删除

以下结构不进入最终 schema：

- `reference_text_nodes`
- `reference_source_segments`
- `reference_material_candidates`
- `reference_material_candidate_nodes`
- `reference_materialization_candidate_embeddings`
- `reference_materialization_material_nodes`
- 旧版 `reference_materials` 字段和 material slot 投影
- `reference_materialization_blueprint_preview_*` 持久化表
- 仅服务于 text node、feature observation、technique specimen 和候选复核的表与索引

旧派生数据不做 backfill。若已有用户数据库需要升级，遵守 copy-first 安全规则：先复制原数据库并写 manifest，再建立干净派生 schema；不修改或删除用户原始参考书文件。

当前材料化 schema 为 v6。升级只重建材料化派生表，保留参考书、已确认 split profile 和章节边界；不保留批次、retry、`current_stage` 或仅写不读的调度元数据。v6 不迁移旧材料语义，v5 十维材料及更早的三字段材料都会随派生表一起清空后重新物化。

## 六、章节材料提取契约

旧的 `IReferenceMaterializationQualifier` 和 `ReferenceCandidateWindowBuilder` 由一个章节级接口取代：

```csharp
public interface IReferenceChapterMaterialExtractor
{
    ValueTask<ReferenceChapterMaterialExtractionResult> ExtractAsync(
        ReferenceChapterMaterialExtractionRequest input,
        CancellationToken cancellationToken);
}

public sealed record ReferenceChapterMaterialExtractionRequest(
    ReferenceMaterializationLlmSelection Model,
    long AnchorId,
    int ChapterIndex,
    string ChapterTitle,
    string ChapterText);

public sealed record ExtractedReferenceMaterial(
    string Text,
    ReferenceMaterialMetadata Metadata);
```

模型一次返回本章的全部材料。请求把完整章节编码为带 `line_number` 的物理行，仅用于让模型定位连续范围；这些行号不形成句子、段落、场景或窗口层，也不参与候选预筛，但会以 `source_span` 持久化为材料的可审计原文坐标。模型工具输出 `start_line`、`end_line` 和严格最终档案，服务端再从冻结章节生成 `ExtractedReferenceMaterial.Text`。

最终档案由 18 个语义组组成：原文坐标、来源类型、实体、时空场景、叙述视角、事件、事实要点、因果、状态变化、人物动态、冲突/代价、信息状态/内容、情绪/潜台词、叙事作用、伏笔线、意象/母题、表达技法、复用提示。受控 taxonomy 与模型可见值全部使用中文；数组与对象只在有原文证据时填写，其余明确为 `null` 或空数组。没有人为的实体数、事实数或文本长度截断；唯一传输保护是 8MB 工具参数上限。`复用提示` 是每条可复用材料的必填、源事实约束说明。系统不计算长度以选择另一条处理路径；如果 provider 拒绝输入，按模型请求失败处理。服务端必须在落库前完成以下校验：

1. 返回集合非空。
2. 每个首尾行号都存在、顺序合法且范围非空；服务端截取的 `Text` 是归一化 `ChapterText` 的原样连续子串。
3. 材料之间没有相同的规范化正文。
4. 最终档案满足锁定 JSON schema、中文受控 taxonomy 和字段间约束（例如限知视角必须有焦点实体）。
5. 模型返回项全部合法；不允许丢弃非法项后提交其余项。

任一校验失败，整章失败。不会把模型输出改写成“尽量可用”的结果。

## 七、运行状态与失败语义

### 7.1 精简状态

```text
run: queued -> extracting -> embedding -> indexing -> completed
chapter: pending -> extracting -> embedding -> completed
任一非终态 -> failed
```

删除 `building_candidates`、`llm_qualifying`、`review_required` 和候选复核后的回流状态。

参考书登记只表示来源可读，状态为 `pending_split`；章节确认后为 `pending_materialization`；只有 active generation 已成功发布时才对用户显示 `ready`。

### 7.2 事务边界

- 一个章节的 materials、embeddings 和向量索引在同一章节完成点提交，不能留下半章结果。
- 当前章节失败后立即停止 run，不再领取下一章。
- 只有所有章节完成、material/vector 数量一致且向量索引健康时，才能原子更新 active generation。
- 新 run 失败不会改写 active 指针；检索只读取明确的 active generation，不动态寻找“最近可用结果”。
- 新 generation 激活成功后删除上一 generation 的派生材料和向量，不提供回滚入口。

### 7.3 稳定错误

至少覆盖：

```text
materialization_source_changed
materialization_llm_not_configured
materialization_llm_health_check_failed
materialization_llm_request_failed
materialization_llm_output_invalid
materialization_no_materials
materialization_source_text_mismatch
materialization_embedding_not_configured
materialization_embedding_request_failed
materialization_embedding_invalid
materialization_vector_index_failed
materialization_generation_incomplete
```

错误返回章节序号、运行阶段和可读说明。UI 提供“运行全部”和章节行内的“运行本章”：前者续跑同一 run，后者无论章节是否 completed 都只重跑所选章节。续跑前严格核对 completed 章节的实际材料和 embedding；进度与提交数据不一致时直接报 `materialization_generation_incomplete`。

## 八、检索与消费

### 8.1 唯一检索服务

建立 `IReferenceMaterialSearch`，作为素材库预演和章节写作的共同入口：

```text
query + session/library/anchor scope
  -> license filter
  -> active generation filter
  -> mandatory vector topK
  -> material results
```

要求：

- 只返回 active generation 的 materials。
- 按现有 session/library binding 解析多本参考书范围。
- embedding provider、维度或索引不健康时直接报错。
- 不并行维护 FTS、JSON vector、observation 和 technique 四套召回。
- bridge 只保留一个 `SearchReferenceMaterials` 产品动作；删除重复的 `SearchActiveReferenceMaterializationMaterials`。
- 工作台分页浏览使用简单的 `ListReferenceMaterials`；它不是第二套搜索，也只读取 active generation。

### 8.2 蓝图预演

素材库右侧预演是无状态操作：输入目标和 anchor ids，搜索 active materials，在内存中组装并直接返回结果。

- 不创建 preview session。
- 不提供 `GetReferenceMaterializationBlueprintPreview`。
- 不持久化 preview source、candidate、beat 或 material link。
- generation 在请求期间变化时返回 stale/error，用户重新生成。

### 8.3 章节默认写作

章节默认路径必须直接消费 `IReferenceMaterialSearch`：

- blueprint beat 引用 `material_id + generation_id`，不再引用 `node_id`。
- 正文候选读取材料的原样 `text`，继续执行授权、来源锁定和插入审计。
- rejected、旧 generation、legacy node 不存在于新路径，因此无需兼容过滤。
- 蓝图引用的 generation 不再 active 时，明确要求重新生成蓝图。

## 九、素材库界面

界面保留三个区域，但只展示完成主流程所需信息：

- 左侧：参考书列表、导入、来源状态。
- 中间：章节切分、开始材料化、当前章节进度、失败信息、active materials 列表。
- 右侧：选择参考书、输入目标、生成一次性蓝图预演。

删除：

- 候选复核列表及确认、拒绝、调整边界操作。
- 六维覆盖面板和 facet 筛选。
- 旧语料分析、治理、technique specimen 和专家调度入口。
- completed run 的回滚与历史 generation UI。

状态文案统一为：`待切分 / 待处理 / 处理中 / 可用 / 失败`。后台内部阶段可以用于诊断，但不扩展用户控制面。

## 十、代码删除边界

最终实现不得留下以下运行时路径：

| 删除对象 | 代表实现 |
|---|---|
| 旧导入/提取入口 | `CreateReferenceAnchor(s)` 的 `BuildSegments/BuildMaterials` 路径；`RegisterMaterializationSource` 成为唯一导入入口 |
| 候选窗口构建 | `ReferenceCandidateWindowBuilder` |
| 候选 qualification | `IReferenceMaterializationQualifier`、`ReferenceMaterializationChatCompletionQualifier` |
| 候选仓库 | `SqliteReferenceMaterializationRunStore.Candidates*`、`Qualifications`、`CandidateReview`、`CandidateListing` |
| 候选 bridge/DTO | `ListReferenceMaterializationCandidates`、`ReviewReferenceMaterializationCandidate` 及候选专用 DTO（材料档案的 `source_span` 保留） |
| 旧候选质量工具 | `ReferenceMaterializationQualityReport`、`ReferenceMaterializationV1BaselineReport` 及其 candidate fixtures/scripts |
| 旧节点检索 | `SqliteReferenceCorpusService` 的 `reference_text_nodes` 查询路径 |
| 旧分析体系 | 无新材料消费者的 `ReferenceCorpusAnalysis*`、`ReferenceCorpusFeature*`、`ReferenceCorpusTechnique*`、`ReferenceCorpusGovernance*` |
| 持久预演 | `reference_materialization_blueprint_preview_*` 表和 Get preview 路径 |
| 旧前端 | 候选复核、六维覆盖、旧分析/治理/专家面板 |

允许保留的能力只有：来源与授权、章节切分、模型预检、逐章调度、lease/heartbeat、原子激活、向量索引、蓝图来源锁定和正文审计。保留的代码必须改为 material identity；不能通过 adapter 继续依赖 node identity。

## 十一、实施任务

### Phase A：锁定替换契约

#### Task A1：建立整章闭环失败测试

**说明：** 先新增真实入口集成测试，覆盖多段对话章节，不再使用预先生成 text nodes 的测试工厂。

**验收：**

- [ ] 测试从 `RegisterMaterializationSourceAsync` 开始。
- [ ] 确认章节后，fake extractor 收到完整章节正文且只调用一次。
- [ ] 测试最终覆盖 active material 搜索，并在旧实现上失败。

**验证：**

- [ ] `dotnet test tests/Novelist.IntegrationTests/Novelist.IntegrationTests.csproj --filter "FullyQualifiedName~WholeChapterMaterialization"`

**依赖：** 无。

**可能文件：** 新增 `ReferenceWholeChapterMaterializationTests.cs`、现有 materialization test fixtures。

#### Task A2：建立章节提取 contract

**说明：** 新增 `IReferenceChapterMaterialExtractor` 和简化材料 DTO；删除公开 candidate/review contract 的消费者后再物理移除旧类型。

**验收：**

- [ ] 请求只有模型身份、anchor、章节序号、标题和完整正文。
- [ ] 输出只有 material type、原文、说明和 tags。
- [ ] contract/unit tests 锁定 snake_case JSON 和结构化模型 schema。

**验证：**

- [ ] `dotnet test tests/Novelist.Tests/Novelist.Tests.csproj --filter "FullyQualifiedName~ReferenceChapterMaterial"`

**依赖：** A1。

**可能文件：** `Novelist.Core/App/IReferenceChapterMaterialExtractor.cs`、`Novelist.Contracts/App/ReferenceMaterializationPayloads.cs`、对应 contract tests。

### Checkpoint A

- [ ] 目标 contract 已冻结。
- [ ] 测试中不出现 sentence、paragraph、candidate window 或 source node fixture。
- [ ] 尚未增加兼容 adapter。

### Phase B：打通整章材料化

#### Task B1：从确认边界直接读取章节

**说明：** run store 根据 split boundary 和 source path/hash 构建章节 work item，不再查询 `reference_source_segments` 或 `reference_text_nodes`。

**验收：**

- [ ] 每个 confirmed boundary 精确得到一份完整章节正文。
- [ ] source hash 或 chapter text hash 不一致时 run 失败。
- [ ] 空章节直接失败，不创建空材料结果。

**验证：**

- [ ] focused run-store integration tests 通过。

**依赖：** A2。

**可能文件：** `SqliteReferenceMaterializationRunStore.cs`、新增 `SqliteReferenceMaterializationRunStore.Chapters.cs`、run-store tests。

#### Task B2：实现严格章节材料提取器

**说明：** 用 schema-locked Chat Completion 一次提取整章材料，并严格验证全部返回项。

**验收：**

- [ ] 多段对话可以通过一个连续首尾行范围作为一条材料返回。
- [ ] 空集合、越界/空范围、重复正文、旧 `text` 输出或非法 schema 均返回稳定错误。
- [ ] 不存在截断、窗口、规则提取或部分接纳代码。

**验证：**

- [ ] extractor unit/integration tests 通过。

**依赖：** A2、B1。

**可能文件：** 新增 `ReferenceChapterMaterialChatCompletionExtractor.cs`、模型 schema fixture、extractor tests。

#### Task B3：按章持久化材料与向量

**说明：** 先在内存中完成本章全部材料校验和一次 embedding 请求，再用一个短事务同时写入 canonical materials 与 embeddings；不在数据库事务中等待外部模型。

**验收：**

- [ ] material/embedding 数量严格相等。
- [ ] 任一 embedding 非法时本章没有部分提交。
- [ ] 章节进度只报告 material count、vector count 和错误，不再报告 candidate/review count。

**验证：**

- [ ] persistence 和 embedding integration tests 通过。

**依赖：** B2。

**可能文件：** `ReferenceCorpusSchemaProvisioner.cs`、`SqliteReferenceMaterializationRunStore.Materials.cs`、`Embeddings.cs`、相关 tests。

#### Task B4：切换 worker 与原子激活

**说明：** worker 直接执行 extract、embed、index、activate；删除运行时 candidate/qualification 分支。

**验收：**

- [ ] 任意时刻最多处理一章，当前章完整提交后才领取下一章。
- [ ] 任一章节失败后不再领取后续章节。
- [ ] 全部章节和向量完整后才切换 active generation；激活后清理旧派生 generation。

**验证：**

- [ ] worker、lease、逐章提交、失败终止和 promotion integration tests 通过。

**依赖：** B3。

**可能文件：** `ReferenceMaterializationWorker.cs`、`SqliteReferenceMaterializationRunStore.ChapterScheduling.cs`、`Promotion.cs`、worker tests。

### Checkpoint B

- [ ] `Register -> split -> confirm -> enqueue -> worker -> active materials` 真实入口测试通过。
- [ ] 数据库中不存在 0-material completed run。
- [ ] worker 热路径不查询 text node、source segment 或 candidate 表。

### Phase C：统一消费路径

#### Task C1：建立唯一 active material 搜索

**说明：** 实现 session/library/anchor scope 下的 mandatory vector topK，并让 `SearchReferenceMaterials` 成为唯一产品搜索接口。

**验收：**

- [ ] 只返回 active generation 且授权允许的材料。
- [ ] 多 library、多 anchor 召回可用。
- [ ] 向量或索引异常直接失败，不返回词法或旧材料结果。

**验证：**

- [ ] focused search integration tests 覆盖 scope、license、stale generation 和索引失败。

**依赖：** B4。

**可能文件：** 新增 `IReferenceMaterialSearch.cs`、`SqliteReferenceMaterialSearch.cs`、bridge handler、search tests。

#### Task C2：将素材库预演改为无状态

**说明：** preview 直接搜索 active materials 并在内存返回结果，删除持久 session 和 Get preview。

**验收：**

- [ ] Generate preview 不写 SQLite。
- [ ] selected source 无 active material 时明确失败。
- [ ] 返回 beat 只引用 material/generation，不引用 node。

**验证：**

- [ ] preview service tests 断言调用前后数据库行数不变。

**依赖：** C1。

**可能文件：** `SqliteReferenceMaterializationBlueprintPreviewService.cs`、preview contracts/bridge、preview tests。

#### Task C3：章节蓝图改用材料身份

**说明：** 默认章节蓝图 coordinator 通过 `IReferenceMaterialSearch` 获取来源，beat 持久化 `material_id + generation_id`。

**验收：**

- [ ] 章节默认路径不调用 `IReferenceCorpusService.SearchCandidatesAsync`。
- [ ] 蓝图来源跨启用 library 生效。
- [ ] generation 失效时要求重新生成蓝图。

**验证：**

- [ ] blueprint session integration tests 覆盖 active、stale 和多来源。

**依赖：** C1。

**可能文件：** `SqliteReferenceCorpusWritingService.cs`、blueprint coordinator、writing contracts、session tests。

#### Task C4：正文来源和审计改用材料身份

**说明：** 正文候选从 selected blueprint 的 material ids 读取原文，保留授权、来源锁定、槽位和插入审计。

**验收：**

- [ ] draft source piece 不再包含 node id。
- [ ] 未被蓝图引用的材料不能进入正文候选。
- [ ] stale generation、缺失 material 或 text hash 不一致时阻断正文。

**验证：**

- [ ] writing/draft/audit integration tests 通过。

**依赖：** C3。

**可能文件：** writing service、draft auditor/preflight、writing payloads、对应 tests。

### Checkpoint C

- [ ] 素材库预演与章节默认写作调用同一个 material search。
- [ ] 默认写作链路不读取 `reference_text_nodes`、旧 `reference_materials` 或 technique vectors。
- [ ] `导入 -> 材料化 -> 蓝图 -> 正文候选` 端到端测试通过。

### Phase D：收敛产品界面

#### Task D1：简化素材库工作台

**说明：** 中栏只保留切分、运行、章节进度、错误和 active material 列表；删除候选复核与覆盖面板。

**验收：**

- [x] 用户可以完成导入、切分、运行全部、观察进度、失败后续跑、强制运行单章和查看材料。
- [x] failed/paused 状态提供“运行全部”；章节行提供“运行本章”，两者都复用当前 run/generation。
- [x] 不出现 candidate、review、六维 coverage 或 generation rollback 控件。

**验证：**

- [x] frontend component tests、build 和 lint 通过。

**依赖：** B4、C1。

**可能文件：** `ReferenceCorpusWorkspace.tsx`、`ReferenceBookSidebar.tsx`、owned bridge adapter/types。

#### Task D2：切换章节参考面板

**说明：** 章节面板展示并消费新 material DTO，不再请求旧 material detail/node 路径。

**验收：**

- [ ] 搜索、蓝图选择和正文候选使用 material id。
- [ ] 多段材料完整展示，换行不被压平。
- [ ] stale/error 状态可恢复且不会静默显示旧结果。

**验证：**

- [ ] focused chapter reference browser workflow 通过。

**依赖：** C3、C4。

**可能文件：** `ChapterReferencePanel.tsx`、reference TypeScript DTO/API、mock bridge workflow。

#### Task D3：浏览器与桌面验收

**说明：** 用真实页面完成素材库和章节写作主流程验证。

**验收：**

- [ ] 覆盖 `1280x720`、`1440x900`、窄桌面和 125%/150% 缩放。
- [ ] 覆盖键盘焦点、长任务、失败后续跑、单章运行和多段材料展示。
- [ ] console error、page error、failed request 为 0，并保存 changed-state screenshots。

**验证：**

- [ ] `npm --prefix frontend run test:reference-workspace`
- [ ] 章节默认路径 focused workflow 通过。

**依赖：** C2、D1、D2。

### Checkpoint D

- [ ] 两个核心 UI 流程均有浏览器证据。
- [ ] UI 不暴露已删除的专家控制面。

### Phase E：物理删除旧系统

#### Task E1：删除候选材料化子系统

**说明：** 删除 candidate builder、qualifier、candidate store、review bridge/DTO/UI 和对应测试。

**验收：**

- [ ] `rg` 不再命中 candidate window、candidate review 和 source span 产品类型。
- [ ] materialization 状态、计数和文案中不再出现 candidate/review。
- [ ] solution 和 frontend build 均通过。

**依赖：** B4、D1。

#### Task E2：删除旧 text-node 检索和分析后端

**说明：** 删除已无消费者的 node search、feature observation、technique specimen、governance 和旧 material projection 服务。

**验收：**

- [ ] 默认写作和素材库代码不引用 `IReferenceCorpusService` 的旧查询接口。
- [ ] 删除相关 DI 注册、bridge methods、contracts 和 tests。
- [ ] 保留的授权和审计服务只依赖 anchor/material identity。

**依赖：** C4。

#### Task E3：删除旧前端入口

**说明：** 删除旧分析、治理、coverage、候选复核和专家调度组件及 owned API。

**验收：**

- [ ] 不存在不可达组件或 bridge method。
- [ ] app smoke workflow 不再安装旧 mock route。
- [ ] TypeScript build 和 ESLint 无 unused/dead export。

**依赖：** D3、E2。

#### Task E4：清理 schema 与派生数据升级

**说明：** 从 provisioner 删除旧表和索引；升级只保留安全副本/manifest 与干净重建，不迁移旧派生行。

**验收：**

- [ ] materialization-owned schema 只创建本文明确保留或重建的表；共享的小说、授权和 library/session 表不受影响。
- [ ] 旧数据库升级前生成副本和 manifest，原参考书文件不变。
- [ ] 重复初始化幂等，不会重新引入旧表。

**依赖：** E1、E2、E3。

#### Task E5：删除过期测试和同步文档

**说明：** 删除验证旧行为的 fixture/golden，把 README、任务、决策和发布说明统一到整章路径。

**验收：**

- [ ] 文档中不再把 sentence/passage、candidate window、六维覆盖或 rollback 写成目标能力。
- [ ] 测试数量变化有删除清单，不用旧测试通过数冒充新闭环证据。
- [ ] README 和 release notes 只陈述已实现能力。

**依赖：** E4。

### Checkpoint E

- [ ] 仓库只剩一套参考数据材料化和检索路径。
- [ ] 无 compatibility adapter、dual read/write、fallback 或 dead bridge route。
- [ ] 源码搜索可以证明旧表、旧接口和旧 UI 已删除。

## 十二、最终验收

### 12.1 必测闭环

```text
Register source
  -> Analyze/Preview split
  -> Confirm split
  -> Enqueue
  -> Worker extracts one full chapter per model call
  -> Embeds every material
  -> Activates generation
  -> Search active materials
  -> Generate transient preview
  -> Generate default chapter blueprint
  -> Generate audited draft candidate
```

fixture 必须至少包含一章跨多个段落的连续对话，并断言它作为一条完整材料进入检索和蓝图。

### 12.2 必测失败

- 模型未配置或健康检查失败。
- 模型返回空材料。
- 模型返回改写过、无法在章节中找到的材料正文。
- 模型返回部分合法、部分非法材料。
- embedding 数量或维度错误。
- vector index 建立失败。
- source hash 在确认后变化。
- 当前章节失败后，下一章没有启动。
- 新 run 失败后 active 指针没有被改写。
- stale generation 的蓝图不能生成正文。

### 12.3 命令门

```powershell
dotnet test Novelist.slnx --no-restore -v minimal
npm --prefix frontend run build
npm --prefix frontend run lint
npm --prefix frontend run test:reference-workspace
npm --prefix frontend run verify
.\scripts\corpus-driven-writing\run-materialization-scale-gate.ps1 -Configuration Release
```

正式规模门继续使用 50K 全 scheduler/worker/fake-extractor/fake-embedding 链路，并保留 1,000-item job-store micro-benchmark。2M 只允许显式非阻塞长跑，不进入日常完成定义。

## 十三、完成定义

只有同时满足以下条件，参考数据功能才可标记完成：

- [ ] 真实登记入口能产生非空 active materials。
- [ ] 每次模型调用接收一个完整章节，不存在持久或临时切分分支。
- [ ] 所有失败条件都明确报错且没有部分激活。
- [ ] 素材库预演和章节默认写作共用 active material search。
- [ ] 正文来源以 material/generation 锁定并通过既有授权与审计。
- [ ] 旧 node/candidate/corpus 路径和 UI 已物理删除。
- [ ] .NET、frontend、browser 和 50K gates 全部通过。
- [ ] 文档、README 和 release notes 与实际实现一致。

该完成定义不以“保留旧功能可运行”为条件；相反，旧路径仍存在即视为改造未完成。
