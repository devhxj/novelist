# 覆盖度阈值标定流程（开放问题 3 收口 runbook）

> 状态：**待执行**（2026-09-04 建立）。当前 `SufficientRatio = 0.5` 与"单 beat 任意命中即覆盖"均为 v1 未标定取值；
> 本文档定义标定所需数据、流程与验收，使"给阈值一个有标注数据支撑的取值"（U9）成为可执行步骤而非悬置判断。

## 现状（v1，未标定）

- 位置：`src/Novelist.Infrastructure/App/ChapterCorpusCoverageService.cs`
  - `SufficientRatio = 0.5`：全书覆盖率 ≥ 0.5 判"语料充足"（恰好 0.5 判足，边界由
    `ComputeCoverageTreatsExactHalfAsSufficientUntilCalibrated` 钉死）。
  - `BuildQuery`：v1 规则为检索返回**任意**命中即视为该 beat 已覆盖，不设相关度分数下限。
- 观测仪表已就位：每个命中 beat 的综合分通过 `ChapterCorpusBeatCoveragePayload.hit_score`
  （ScoreComponents 各分量之和）随 `GetChapterCorpusCoverage` 透出——标定数据可直接从现有 payload 采集，
  不需要新埋点。

## 标定所需数据

1. **beat 级标注集**：从 ≥5 本真实参考书、≥10 个真实章节的细纲中抽取 beat（建议 ≥300 条），
   对每条 beat 的检索命中材料人工标注"该材料对写这个 beat 实际可用 / 不可用"（二元）。
2. **分数分布**：对同一批 beat 记录 `hit_score` 分布（直方图 + 分位数）。
3. 标注只保存 beat 文本摘要、命中分数与标注结论，不保存源书正文（与走查数据的脱敏口径一致）。

## 标定流程

1. 用标注集扫描候选分数阈值 `t ∈ {0, 0.2, 0.4, …, 1.2}`（0 即现状"任意命中"），
   对每个 `t` 计算 beat 级判定与人工标注的一致率、精确率（判覆盖且确实可用的比例）、召回率。
2. 选一致率最高且精确率 ≥0.8 的最小 `t` 作为单 beat 命中阈值；无满足者时保留 v1 并记录原因。
3. 用选定 `t` 重算全书覆盖率分布，复扫 `SufficientRatio ∈ {0.4, 0.5, 0.6, 0.7}`，
   以"作者后续实际使用了注入材料的章节比例"为锚点选择分界。
4. 更新代码常量与注释、更新边界测试、在本文件追加一段"标定结果"（数据规模、选定值、一致率、日期）。

## 验收

- 标注集规模达标（≥300 beat / ≥5 本书）；结果段落记录选定阈值与指标；
- `ChapterCorpusCoverageServiceTests` 边界测试与实现同步更新；
- 50K 门禁不受影响（覆盖度计算不在门禁管线内，只需回归 `GetChapterCorpusCoverage` 集成测试）。
