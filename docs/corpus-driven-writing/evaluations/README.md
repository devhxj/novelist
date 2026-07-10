# 语料驱动写作评测与走查

本目录定义 P2.1 效果评测和 P2.2 用户走查的可复现、脱敏契约。它衡量检索、蓝图区分、正文采用和核心任务完成情况，但不保存源文、候选正文、提示词、本地路径、参与者身份或自由文本笔记。

## 数据边界

评测 JSON 由运行时严格校验：未知字段会拒绝，因而 `text`、`source_text`、`candidate_text`、`prompt`、`source_path` 等字段不能进入数据集。可保存的字符串仅限规范化 ID、reason code 和 `sha256:<64 hex>` 哈希。

根对象字段：

| 字段 | 说明 |
|---|---|
| `schema_version` | 固定为 `corpus-writing-evaluation-fixtures-v1` |
| `dataset_id` / `dataset_revision` | 脱敏数据集标识与版本 |
| `dataset_kind` | `contract` 或 `human` |
| `query_cases` | 人工相关性标注与实际排序、reason code、延迟 |
| `blueprint_cases` | 同目标候选的来源 node 集合、选中项和反馈是否改善 |
| `insertion_cases` | 保留片段比例、采用/修改量/迭代次数和 1-5 人工评分 |

`human` 数据集必须包含 50-100 条 query、20-30 组蓝图和 20-30 个章节插入样本；任一数量不足会被拒绝。每个正文样本至少有一个哈希化评审者的保真、剧情适配和自然度评分，且同一 insertion case 内评审者哈希必须唯一。检索原因码必须来自固定码表；具体标注规则、盲评方式和评分锚点见[效果评测标注套件](./writing-evaluation-kit.md)。`contract` 数据集只用于校验 schema、指标和脱敏行为，不能作为人工效果证据，也不能关闭 P2.1。

可参考受控样例 [corpus-writing-evaluation-contract.json](../../../tests/Novelist.IntegrationTests/Fixtures/corpus-driven-writing/corpus-writing-evaluation-contract.json)。它故意只有 3 组合成样本。

## 运行

```powershell
./scripts/corpus-driven-writing/run-writing-evaluation.ps1 `
  -Fixture <redacted-fixture.json> `
  -Output build/tmp/corpus-driven-writing/evaluation/<dataset-id> `
  -Configuration Release
```

输出目录固定在 `build/tmp/corpus-driven-writing/`，生成：

- `corpus-writing-evaluation-report.json`
- `corpus-writing-evaluation-report.md`

报告只包含聚合指标：`Recall@K`、`nDCG@K`、命中原因准确率、P50/P95、来源集合区分度/重复率、反馈改善率、保真率、剧情适配、自然度、采用率、用户修改比例和平均迭代次数。它不会回显每条 query、章节、来源或候选正文。

## 收集流程

1. 在具有相应授权的受控环境中建立原始标注；导出前只保留 ID、哈希、数值和固定码表中的枚举标签。
2. 对同目标蓝图采用盲评或打乱候选顺序；记录 `feedback_improved`，而不是评审笔记原文。
3. 对章节插入记录候选字符数、最终修改字符数、迭代次数、是否接受及 1-5 评分。
4. 将已脱敏、可复查的 `human` fixture 和聚合报告按数据授权策略版本化；默认本地报告不进入 Git。
5. 任何策略调整都必须引用具体数据集 revision，并保留调整前后报告。执行细节见[效果评测标注套件](./writing-evaluation-kit.md)。

## 用户走查

用户走查使用独立的 `corpus-writing-usability-fixtures-v1`。每个参与者只有哈希化 ID，且必须记录下面五条固定任务各一次：

- `import_start_analysis`
- `leave_resume_analysis`
- `target_to_blueprint`
- `feedback_select_prose`
- `blocked_recover_insert`

每条任务保存 `completed`/`abandoned`、是否无提示完成、耗时秒数、回退次数、首个失败码、恢复动作码和 1-5 难度。失败与恢复码必须来自固定码表，不能存备注原文；完整任务卡、主持人边界和码表见[用户走查执行套件](./usability-study-kit.md)。`study_kind: human` 至少需要 5 名参与者；报告仅在至少 4 人完成全部五条任务且每条均无提示时给出 `acceptance_passed: true`。

```powershell
./scripts/corpus-driven-writing/run-usability-study-evaluation.ps1 `
  -Fixture <redacted-study.json> `
  -Output build/tmp/corpus-driven-writing/usability-study/<study-id> `
  -Configuration Release
```

输出为 `corpus-writing-usability-report.json` 和 `corpus-writing-usability-report.md`，仅包含每任务完成率、无提示完成率、平均耗时/回退/难度以及规范化失败码、恢复动作码计数。参考 [受控 contract 样例](../../../tests/Novelist.IntegrationTests/Fixtures/corpus-driven-writing/corpus-writing-usability-contract.json)；该样例只有 2 个合成参与者，必然不通过 `human` 验收。

走查主持人只能复述任务，不得解释界面或下一步。真实用户数据满足上述条件后，才可关闭 [开发计划](../development-plan.md) 的 P2.2；浏览器自动化和 contract 样例不能替代它。
