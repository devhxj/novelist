# 语料驱动写作效果评测标注套件

本套件用于 P2.1 的真实人工效果评测。它把检索命中原因、蓝图区分和正文评分收敛为可比较、可脱敏的输入；它不提供合成 `human` 数据，也不把 contract fixture 当作效果结论。

## 研究边界

- 仅在已有授权的受控环境中查看原始 query、章节和候选正文；导出的 JSON、报告、截图和版本库不得包含这些内容、文件路径、提示词或参与者身份。
- `dataset_id`、`dataset_revision`、case ID 和 node ID 必须是规范化研究标识；query、章节和评审者只使用 `sha256:<64 hex>` 哈希。
- 在收集前冻结本套件对应的码表和评分锚点。指标变化只能在新的 dataset revision 中比较，不能在同一数据集内改变定义。
- 真实 `dataset_kind: human` 必须包含 50-100 条 query、20-30 组蓝图和 20-30 个正文插入样本；不足时报告器会拒绝输入。

## 检索标注

每个 query case 需要一组人工相关 node ID、系统排序、`top_k`、延迟和命中原因集合。`expected_reason_codes` 记录人工认为正确的命中理由，`returned_reason_codes` 记录系统实际为该次检索给出的理由；两者都必须使用下表。

| 码 | 何时使用 |
|---|---|
| `goal_match` | 片段直接服务于章节目标或目标意图。 |
| `context_match` | 片段与当前章节位置、人物状态或已知事实相容。 |
| `technique_match` | 片段的可迁移技法符合目标，而非只因表层词相似。 |
| `structured_observation_match` | 片段满足明确的结构化 feature/感官/节奏条件。 |
| `licensed` | 片段在当前 scope 内且满足可复用授权，是返回它的必要理由。 |
| `deduplicated` | 片段是去重组中被选中的代表来源。 |
| `source_diversity` | 片段被选中以补足蓝图或候选集合的来源多样性。 |
| `other_observed` | 无法归类且不记录自由文本；出现后应在下一 dataset revision 前新增受审查码。 |

不要把“相关”本身当成 reason code。相关性由 `relevant_node_ids` 标注，reason code 解释为何该相关节点应出现在当前检索或排序中。

## 蓝图盲评

1. 对同一目标的候选蓝图打乱展示顺序，评审者不看候选 ID、内部策略名或来源库名。
2. 每组至少保留两个 candidate，且仅导出候选 ID 和 source node ID 集合；不能导出目标文本、beat 文本、候选摘要或评审笔记。
3. `selected_candidate_id` 是盲评后选择的候选；`feedback_improved=true` 仅在同一目标经过一次结构化反馈后，复评确认新版更好回应了该反馈时填写。
4. 来源集合完全相同的候选仍可进入原始盲评，但应在报告的 source-set repeat rate 中反映，不能被描述为有效多稿。

## 正文评分

每个插入样本要记录候选字符数、最终编辑字符数、迭代次数、是否接受、source/preserved piece 数和至少一名评审者的三项 1-5 分。相同评审者可评不同样本，但在同一 insertion case 中只能出现一次。

| 分数 | 保真 `fidelity` | 剧情适配 `plot_fit` | 自然度 `naturalness` |
|---|---|---|---|
| 1 | 选定来源被大面积替换或无法核对。 | 与章节已知事实、人物状态或目标明显冲突。 | 难以阅读，拼接或语义断裂明显。 |
| 2 | 只保留少量可核对来源片段。 | 有明显牵强或需要大量人工重写。 | 可读但突兀、重复或衔接失败。 |
| 3 | 大部分关键来源片段仍可核对。 | 不与已知事实冲突，但推进作用有限。 | 基本通顺，仍有可感知拼接感。 |
| 4 | 来源保留充分，仅有受控的必要调整。 | 能自然服务当前目标并保持人物/剧情连续。 | 阅读顺畅，只有轻微可改进处。 |
| 5 | 选定来源边界与保留证据完整。 | 明显推进当前章节且无需实质改写。 | 作为章节片段自然成立。 |

`user_edited_character_count` 只统计接受候选后为达到最终采用文本所做的字符级编辑量；`accepted=false` 的样本仍应保留评分和迭代次数，不把拒绝样本从数据集中删除。

## 导出前检查

1. 为每位评审者生成随机研究编号后再哈希；不要用姓名、邮箱或设备 ID 直接哈希。
2. 同一 query 的 `relevant_node_ids`、`ranked_node_ids`、expected/returned reason code 各自不得重复；reason code 必须在固定码表内。
3. 同一 blueprint case 的 candidate ID 不得重复，`selected_candidate_id` 必须在该候选集合中。
4. 同一 insertion case 的 `reviewer_id_hash` 不得重复；每个评分都在 1-5。
5. 运行报告器，查看输出只含覆盖数和聚合指标：

```powershell
./scripts/corpus-driven-writing/run-writing-evaluation.ps1 `
  -Fixture <redacted-human-dataset.json> `
  -Output build/tmp/corpus-driven-writing/evaluation/<dataset-id> `
  -Configuration Release
```

6. 任何 M3/M4/M5 策略调整都引用具体 dataset revision，并保留调整前后两份聚合报告。没有通过此步骤的真实 `human` 数据，不得宣称检索质量、蓝图区分或正文质量提升。

完整字段契约与报告指标见[评测协议](./README.md)。
