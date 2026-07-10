# 语料驱动写作用户走查执行套件

本套件用于 P2.2/M9 的真实目标用户走查。它让研究人员能以同一套无引导任务、记录方式和脱敏输出收集证据，但不会把演示、自动化 workflow 或 contract fixture 伪装成用户研究。

## 研究边界

- 只使用专门准备的、可公开的合成语料和章节工作区；不得在任务卡、JSON、截图或报告中记录用户项目、源文、候选正文、提示词和本地路径。
- 每名参与者只使用自动模式，不提供产品文档、快捷键清单或按钮位置提示。
- 主持人可以逐字重读任务卡或处理技术故障，不能解释界面、指出下一步、替用户判断候选或推荐操作。
- 走查结果只有在真实 `study_kind: human`、至少 5 名目标用户、至少 4 名无提示完成全部五项任务时才可关闭 M9。研究工具、mock workflow 和合成 contract 都不计入人数。

## 走查前准备

1. 在隔离的研究工作区预置一份可分析素材、一个可写章节和支持 blocked → 重组 → 插入的合成情境；不要让参与者看到真实作者材料。
2. 每位参与者使用不同的随机研究编号。将随机编号转换为 `sha256:<64 hex>` 后写入 fixture；不要对姓名、邮箱或设备标识直接哈希，也不要把编号映射表提交到仓库。
3. 在每张任务卡交给参与者时开始计时；参与者明确表示完成、放弃或达到 2 小时时停止计时。
4. `backtrack_count` 只计参与者主动回到前一阶段或放弃刚才的可见下一步重新寻找路径；阅读、滚动和因窗口大小产生的换行不计数。
5. 截图仅可来自合成工作区，并在导出前去除书名、来源、编辑器正文和本地路径。截图文件不写入 fixture 或报告，也不提交到 Git。

## 主持人开场白

对每位参与者仅说明：

> 请按照你平时理解软件的方式完成每张任务卡。过程中可以自行探索；我不能解释界面或告诉你下一步。你可以随时停止。

任务按下列顺序执行，因为后续任务会复用前一任务建立的状态。主持人可原样重读任务卡，但重读不构成提示。

## 五张任务卡

| ID | 交给参与者的任务卡 | 观察者的完成条件 |
|---|---|---|
| `import_start_analysis` | 这份已准备好的参考素材还没有完成分析。请让它开始分析；确认它已经可以在后台继续后告诉我。 | 已创建同一素材的持久后台分析任务，且离开当前区域后仍可找到。 |
| `leave_resume_analysis` | 刚才的分析被中断，尚未完成。请先离开当前素材页，再找到同一项任务，并让它回到可以继续处理的状态。 | 参与者回到同一持久任务并采取可继续的恢复操作；不要求主持人指出任务页或操作按钮。 |
| `target_to_blueprint` | 当前章节需要按研究工作区预置的写作目标继续推进。请在自动模式下得到一个你愿意继续使用的蓝图，并选中它。 | 目标已提交，参与者从返回的蓝图中明确选择一个；不进入专家模式。 |
| `feedback_select_prose` | 当前蓝图的来源组合重复，不适合继续使用。请表达这个问题，得到下一轮蓝图；再基于选中的蓝图生成正文候选，并选择一个你愿意继续预览的版本。 | 反馈产生新的蓝图轮次，随后选择蓝图并选择正文候选。 |
| `blocked_recover_insert` | 选定的正文候选显示不能安全插入。请自行处理这个阻断，得到可插入的版本，并明确把它写入当前章节。 | 参与者选择其他版本或回到蓝图重组，最终把可插入结果写入章节 buffer。 |

若应用、测试环境或研究设备本身中断，当前尝试无效；恢复环境后从该任务重新开始，不把技术中断误算为用户完成或放弃。

## 记录规则

每位参与者必须恰好有五条 task 记录，且 task ID 与上述表格完全一致。一个已恢复完成的任务仍可保留首次失败码和恢复动作码。

| 字段 | 记录规则 |
|---|---|
| `outcome` | 仅 `completed` 或 `abandoned`。 |
| `completed_without_prompt` | 仅当参与者未获得任何界面或下一步提示且最终完成时为 `true`；`abandoned` 必为 `false`。 |
| `duration_seconds` | 从交卡到完成/放弃的整数秒。 |
| `backtrack_count` | 按走查前准备中的定义记录。 |
| `first_failure_code` | 首次明显受阻的位置；没有受阻则为 `null`。 |
| `recovery_action_code` | 解决首次受阻所采取的有效动作；没有恢复动作则为 `null`。 |
| `difficulty` | 参与者在任务结束后给出的 1-5 分，1 为非常容易、5 为非常困难。 |

不要把观察笔记、口述原话、用户名、文件名或截图路径写入 JSON。需要复盘的问题只能先映射到下面的固定码；若两次出现同一个可用性失败，再把它转为单独的 UX 修复任务与浏览器回归。

## 固定码表

报告器会拒绝码表外的字符串。`other_observed` 不携带自由文本；它出现后应在下一轮研究前以新的、受审查的码表版本替换。

### 首次失败码

| 码 | 使用时机 |
|---|---|
| `start_action_not_found` | 找不到启动已准备素材分析的入口。 |
| `background_job_not_found` | 找不到先前创建的后台任务。 |
| `task_status_not_understood` | 看见任务但无法理解其当前状态。 |
| `resume_action_not_found` | 明白任务需继续，但找不到继续/恢复动作。 |
| `goal_input_unclear` | 不清楚如何表达章节目标。 |
| `blueprint_choice_unclear` | 无法据可见信息在蓝图间作出选择。 |
| `feedback_action_not_found` | 找不到或无法使用蓝图反馈。 |
| `prose_choice_unclear` | 无法据可见信息在正文候选间作出选择。 |
| `transition_blocked` | 候选因过渡或拼装审计阻断。 |
| `blocked_state_not_understood` | 不理解为何当前候选不能插入。 |
| `recovery_action_not_found` | 理解存在阻断，但找不到可行恢复动作。 |
| `insert_action_not_found` | 已有可插入候选，但找不到写入章节的动作。 |
| `environment_interrupted` | 研究环境本身中断；该尝试应重做，不参与验收。 |
| `other_observed` | 无法归入现有码表且不记录自由文本。 |

### 恢复动作码

| 码 | 使用时机 |
|---|---|
| `start_analysis` | 启动素材分析。 |
| `open_background_jobs` | 打开后台任务并定位已有任务。 |
| `resume_analysis` | 恢复暂停的分析。 |
| `retry_analysis` | 重试失败的分析。 |
| `generate_blueprint` | 从章节目标生成蓝图。 |
| `revise_blueprint` | 用反馈请求新一轮蓝图。 |
| `choose_alternative_blueprint` | 改选另一份已有蓝图。 |
| `generate_prose` | 基于选中蓝图生成正文候选。 |
| `choose_alternative_prose` | 改选另一份已有正文候选。 |
| `return_to_blueprint` | 从 blocked 正文回到蓝图重组。 |
| `insert_at_cursor` | 将可插入候选写入当前光标位置。 |
| `append_to_chapter` | 将可插入候选追加到章节。 |
| `restart_task` | 在无效技术中断后从头开始该任务。 |
| `other_observed` | 无法归入现有码表且不记录自由文本。 |

## 导出与判定

1. 仅在所有真实参与者记录完成后，导出一个 `study_kind: human` fixture；它必须有至少 5 名参与者和每人 5 个固定任务。
2. 运行报告器：

```powershell
./scripts/corpus-driven-writing/run-usability-study-evaluation.ps1 `
  -Fixture <redacted-human-study.json> `
  -Output build/tmp/corpus-driven-writing/usability-study/<study-id> `
  -Configuration Release
```

3. 报告会聚合每任务完成率、无提示完成率、平均耗时、回退数、难度、首次失败码和恢复动作码；它不回显参与者、章节或正文。
4. `acceptance_passed=true` 只表示至少 4 名参与者无提示完成五项任务。仍须审查重复失败码，并把每个重复问题转为可复现的 UX 修复与浏览器回归后再决定是否关闭 M9。

完整字段契约与脱敏边界见 [评测协议](./README.md)。
