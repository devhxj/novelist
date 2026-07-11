# Corpus-Driven Writing 进展与目标偏离审计

## 文档信息

- 审计日期：2026-07-10
- 仓库快照：`master`，HEAD `1aa74b9`
- 对照基线：`origin/master`
- 审计范围：开发方案、任务清单、当前实现、自动测试、历史 200 万字规模运行、本机工作树
- 审计性质：原始证据为只读现状评估；本文随后同步了审计后的 50K 规模分层和默认体验计划，不作为里程碑验收通过证明
- 修订说明：审计文件生成后工作树继续变化；“初始审计结果”保留当时失败证据，下面的“后续执行更新”记录当前已复现的结果

## 后续执行更新（截至 2026-07-11）

初始审计发现的 P0 停止线已关闭，当前工作树的可复现结果如下：

- **P0.1 已完成**：`CorpusHarnessHost/bin`、`obj` 与 PDB 等 111 项生成物已从 Git 索引移除；`.gitignore` 覆盖 `scripts/**/bin/` 与 `scripts/**/obj/`；已跟踪的 `build/runtime/models/vocab.txt` 保持例外。
- **P0.2 已完成**：`LocalOnnxEmbeddingClient` 释放已创建的 runner，bundled-model 测试进入非并行 collection；后台任务 workflow 以任务行身份定位状态，不再因重复“已暂停”文案触发 strict locator。
- **P1.1 已完成**：Stage 3 technique job 只接受同 novel/anchor/scope 的已完成 feature job，冻结 dependency job/run/input snapshot 与 observation evidence，并由 canonical worker 执行。回归覆盖 enqueue → worker → specimen、live evidence 变化隔离、pause/resume、worker 重启与瞬态 retry。
- **P1.2 已完成**：worker 保持生产默认 45 秒 lease/10 秒 heartbeat，harness 以短时序运行真实 loop 而不伪造时钟。`corpus-m2-recovery-metrics-v2` 记录 10/10 强杀恢复、30 组 pause、30 组 cancel 和 30 组 stale lease：pause P95=82.28 ms、cancel P95=87.68 ms、stale reclaim P95=37.17 ms；每个 stale case 都将旧 attempt 标记为 `lease_expired` abandon，旧 worker 零 completion commit。
- **P1.3 已完成**：Release 50K 全管线正式轨已通过 scheduler → snapshot builder → worker → fake analyzer：50,015 字、16 anchors、4 libraries、4 session bindings、32 jobs、13,385 work items/completion rows；吞吐 29.16/s，claim/list/progress P95=10.12/27.04/3.51 ms（各 32 样本），重复输出、持久预算穿透、预留 token 和活动 lease 均为 0；数据库 29,433,856→47,398,912 bytes。
- **当前验证**：`dotnet test Novelist.slnx --no-restore -v minimal` 通过 1,297 项；`npm --prefix frontend run verify` 通过，相关 phase16 浏览器 workflow 的 console/page/request diagnostics 均为空。素材库 workflow 已额外覆盖“导入 → 开始分析 → 离开 → 返回”，断言 ready 来源行调用 `EnqueueReferenceCorpusAnalysisJob`、切换至后台任务并恢复同一持久 job；50K formal metrics 位于本机 `build/tmp/corpus-driven-writing/scale-50k-metrics.json`。
- **M9 素材库工作台修正进行中**：用户截图揭示 `素材库` 左侧落入“即将推出”、右侧仍是通用聊天，不能作为可用的参考书处理界面。当前工作树已加入真实参考书籍管理和临时蓝图预演；专用 `test:reference-workspace` 已补齐，但尚未完成浏览器验收，不能把这项修正计为已关闭的自动化体验证据。
- **P2.2 工具链已复现**：Release `run-usability-study-evaluation.ps1` 已成功处理两参与者 contract fixture，并输出 `participant_count=2`、`unprompted_full_path_completion_count=1`、`acceptance_passed=false`。fixture 现强制固定失败/恢复码表，并在 JSON/Markdown 聚合恢复动作计数；[用户走查执行套件](./evaluations/usability-study-kit.md)已提供无引导任务卡、主持人边界、脱敏记录与复测规则。退出码为 0 只证明工具可运行；该 fixture 人数不足且未达到无提示完成条件，不能关闭真实用户验收。
- **当前开放项**：M2 的标准后台验收已关闭但仍保持 `S`；M9 的章节默认路径、持久恢复、长任务/错误恢复与可访问性自动化已关闭，素材库工作台还需通过专用浏览器验收，另缺真实目标用户任务证据。任务清单当前为 `205/207`，M2 `45/45`、M9 `8/10`。

## 执行摘要

Corpus-Driven Writing 的核心产品方向没有发生根本偏移。当前默认实现仍遵守以下边界：跨库检索、多蓝图候选、选定蓝图锁定来源、最大化保留来源文本、只允许受审计的槽位和过渡修改、授权与相似度闸门不可绕过，并且正文路径没有接入脱离语料的自由生成。

偏离主要发生在交付执行和完成度表述上：

1. 审计初始快照时，文档规定的 P0 交付基线尚未恢复，却继续扩展了 M3-M5；该停止线已在后续执行中关闭。
2. 实现覆盖增长很快，但真实检索质量、蓝图区分度和正文效果没有形成评测证据。
3. 200 万字规模运行已结束，按旧固定门槛吞吐差 1.01 items/s；但该运行约 67 分钟，不适合作为常规门禁，应降为可选长跑，50K 改为强制标准规模档。
4. M3-M5 的原子任务实现覆盖很高；持久蓝图 session 已进入默认产品路径，但技法向量维护、真实效果和真实用户采用度仍未得到证明。
5. 任务勾选统计、成熟度总表和实际代码状态曾不一致；本次已同步数量，并新增默认体验、恢复和可访问性开放任务。

因此，当前统一状态应继续保持：

> M1 产品薄切片完成；M2-M5 加深中；M6-M8 冻结扩张；M9 只做默认体验收口；整体仍为 S 级，尚未达到产品闭环 P 或规模化 L。

## 目标对齐判断

### 仍然对齐的核心不变量

| 产品不变量 | 当前判断 | 主要证据 |
|---|---|---|
| 素材处理侧与章节使用侧分离 | 基本成立 | 素材分析页、后台任务页与章节写作面板分开；章节侧消费处理结果 |
| 一个 session 跨多个启用 library 检索 | 受控路径成立 | 跨库 golden 与 `ReferenceCorpusWritingServiceTests` 定向测试通过 |
| 当前目标和章节上下文进入检索 | 受控路径成立 | 四路召回、local context fit、结构化过滤已有实现和回归测试 |
| 先生成多蓝图，再派生正文 | 基础路径成立 | 章节 UI 已有候选、反馈重组和 selected blueprint 正文路径 |
| 正文最大化复用来源语料 | 成立于当前确定性实现 | 默认使用 `PreservingReferenceCorpusTextAssembler` |
| 正文只能消费选定蓝图来源 | 安全边界较强 | node/beat 锁定、缺源阻断、越界 replacement 阻断测试较完整 |
| AI 不脱离语料自由补写 | 当前默认路径成立 | 写作服务使用确定性多策略 assembler、slot resolver 和 transition resolver |

核心目标没有被替换成传统的“LLM 参考材料后自由创作”。这一点是当前实现最可靠的方向性成果。

### 尚未得到证明的目标

以下目标仍只有计划、受控 fixture 或机制性测试，没有真实效果报告：

- 人工标注 query 集上的 Recall@K、nDCG 和命中原因准确率
- 同一目标下多份蓝图的稳定区分度、重复方案率和反馈后改善率
- 原句保真率与剧情适配率之间的平衡
- 过渡自然度和完整章节阅读体验
- 用户最终修改字符比例
- 真实章节正文盲评
- 长期使用下的成本、恢复和质量波动
- 默认用户能否不看文档完成“目标 → 蓝图 → 正文 → 插入”
- 真实目标用户在长任务离开/返回、错误恢复、键盘/焦点、缩放和窄窗中的完成率与困难点

当前系统已经证明“可以按规则安全运行”，尚未证明“可以持续写出用户愿意采用的章节”。

## 里程碑现状

本次计划修订及后续 P0/P1/M9 收口后，按行首真实复选框统计，M0-M9 共有 207 个原子任务，其中 205 个已勾选、2 个未勾选；文内示例标记和文末 7 条编号约束不计数。剩余开放项为 M9 的素材库工作台浏览器验收和真实目标用户走查。复选框只表示存在某种实现和定向证据，不能换算为产品完成百分比。

| 里程碑 | 当前复选框 | 审计成熟度 | 判断 |
|---|---:|---|---|
| M0 地基 | 36/36 | S | schema、契约、migration、projection、fixture 等覆盖较广；P0 仓库卫生和全套验证已恢复，但这不替代产品闭环或规模化证据 |
| M1 纵向薄切片 | 39/39 | S | 跨库检索到正文插入的受控闭环成立，是目前最完整的产品切片 |
| M2 深度分析 | 45/45 | S，标准轨已关闭 | canonical job、lease、fenced commit、aging/retry、UI、恢复 harness 和 Stage 3 均有定向证据；真实控制与失租墙钟时限、Release 50K 全管线门禁均已通过，但默认用户闭环、真实 provider 和持续运行证据仍缺 |
| M3 深度检索 | 18/18 | S | 四路独立 topK 和 1,000 节点预算已有定向证据；没有真实长篇检索质量和生产 SLA |
| M4 蓝图 | 20/20 | S | 多策略、coverage、gap 和 coordinator 已实现；持久会话已进入章节默认 UI，但仍没有效果指标 |
| M5 拼装 | 19/19 | S | 来源锁定和审计很强，多草稿机制已存在；自然度、适配率和修改成本未验证 |
| M6 语料库产品化 | 5/5 | S，冻结 | 授权、去重、库作用域和插入审计有薄切片，真实多库治理仍未验收 |
| M7 聚合知识 | 8/8 | S，冻结 | 聚合与 provenance 已存在，尚未证明被 M3-M5 消费后带来效果改善 |
| M8 复核工作流 | 7/7 | S，冻结 | 状态机、队列和基础操作存在，生产重跑和证据导航不完整 |
| M9 默认体验 + 专家 UI | 8/10 | S，章节自动路径已收口 | 章节默认路径、服务端恢复、长任务/错误恢复和可访问性浏览器验证已关闭；素材库工作台浏览器验收和真实用户验收仍开放 |

## 关键偏离与风险

### 1. P0 停止线未满足仍继续扩张（初始审计发现，已关闭）

`tasks.md` 明确规定：全套构建和测试必须通过，并清理误入版本控制的 `build/tmp`、`bin/obj`、PDB 等生成产物，之后才能继续功能开发。

当前证据：

- 相对 `origin/master` 有 8 个本地提交。
- 累计变更约 286 个文件、72,131 行新增、584 行删除。
- `CorpusHarnessHost/bin` 和 `obj` 下有 111 个生成文件被 Git 跟踪。
- 这些生成文件当前体积约 334.78 MiB。
- 工作树仍存在被构建过程反复修改的 PDB、AssemblyInfo 和缓存文件。
- 审计初始快照时，全套 .NET 与前端 verify 均未通过。

这不是核心产品方向偏移，而是初始审计时明确的实施顺序偏移。后续已清理生成物并恢复完整验证；今后继续 M2-M5 前必须保持该绿色基线，不能重新引入构建产物或跳过全套验证。

### 2. 工程可靠性和审计规则领先于写作效果验证

当前最强的证据集中在：

- schema、契约和幂等
- 授权与相似度闸门
- lease、reservation、fenced commit 和恢复
- preserved/locked spans
- slot、transition 和 candidate-set audit
- mock workflow 和 golden fixture

最弱的证据恰好是产品最终价值：召回是否准确、蓝图是否真正不同、正文是否自然、用户是否需要大幅修改。

如果继续增加审计规则和控制面，而不建立真实效果集，项目可能逐渐优化成“可靠的语料处理与审计系统”，而不是“能明显改善章节写作的系统”。

### 3. 旧 200 万字固定门槛耗时过长，且 harness 证据边界不足

本机正式运行结果位于 `build/tmp/corpus-driven-writing/scale.stdout.json`。该文件属于本地运行产物，当前未进入版本库。

| 指标 | 结果 | 门槛 | 判断 |
|---|---:|---:|---|
| 字符数 | 2,000,003 | >= 2,000,000 | 通过 |
| work items | 76,336 | 全量完成 | 通过 |
| output rows | 76,336 | 与 work items 相等 | 通过 |
| duplicate outputs | 0 | 0 | 通过 |
| budget penetration | 0 | 0 | 通过 |
| claim P95 | 26.50 ms | <= 100 ms | 通过 |
| task list P95 | 90.42 ms | <= 200 ms | 通过 |
| 吞吐 | 18.99 items/s | >= 20 items/s | **失败** |
| 总体 | `passed=false` | `passed=true` | **失败** |

该结果应如实记录为“历史 2M 首轮按旧门槛未通过”，但不再作为 P0-P4 阻塞项。审计后采用四层证据：500 句正确性、1,000 work-item job-store 微基准、50K 强制全管线、显式 2M 可选长跑；首轮 Release 50K 实测为 13,385 work items、459.06 秒（约 7.7 分钟），而非先前估计的约 1,900 items/1.5-2 分钟。

现有 2M host 不能直接缩常量后冒充 50K 全管线验收：fixture 的 source/library 没有真实建成多 anchor/multi-library，主循环绕过 scheduler/snapshot builder/worker 直接调用 job store，budget penetration 还存在硬编码，50K 下 list 延迟样本数也不足。该缺口已由独立 50K scheduler → builder → worker → fake analyzer 正式轨关闭：它从持久状态读取预算，真实建立 16 anchors/4 libraries/4 session bindings，并记录 32 个 claim/list/progress 样本。现有 1,000 work-item job-store micro-benchmark 仍保留为快速热路径回归。

### 4. 蓝图持久迭代进入章节默认流程（后续执行已关闭）

初始审计时，后端虽已新增 `SqliteReferenceCorpusBlueprintIterationCoordinator`，Bridge 和 TypeScript adapter 也已暴露 `GetReferenceCorpusBlueprintSession` 与 `AdvanceReferenceCorpusBlueprintSession`，但章节面板仍直接调用旧的候选生成入口。

后续执行已将 `ChapterReferencePanel` 默认路径改为服务端 session：自动模式只呈现“目标 → 蓝图 → 正文 → 插入”，专家模式才展开内部 id、来源和审计细节；关闭后清理临时缓存再打开时，以服务端目标、迭代和选定蓝图为准。SQLite 集成测试覆盖 generate → select → revise → select → accept → coordinator 重建；浏览器 workflow 覆盖服务端恢复、自动/专家分层、焦点回位、窄窗可见性，以及故障后用原 `request_id` 重放同一推进请求。

这些证据关闭了持久会话接入与自动化恢复边界，但仍不能证明蓝图区分度、正文效果或真实用户能无指导完成该流程。

### 5. 技法向量维护队列的生命周期已接入，入队策略仍未产品化

`SqliteReferenceCorpusTechniqueVectorMaintenanceScheduler` 和 `ReferenceCorpusTechniqueVectorMaintenanceLoop` 已实现，service 也提供 schedule/pump/inspect 能力。

初始审计时，desktop composition 没有持有或启动 maintenance loop，前端产品 API 也没有对应的 schedule/pump 操作。

**后续修订（2026-07-11）**：desktop runtime 现已持有该 loop，并在初始化、数据目录重绑和释放时同步启停，桌面 smoke 测试覆盖启动与停止。维护 job 仍需由服务端或既有 bridge 显式调度；默认产品路径尚未生成例行 maintenance job，前端也不暴露 schedule/pump 专家控制。不能在每次 `SearchCandidates` 后直接入队，因为搜索已在当前 scope 内校验并按需重建 native 技法索引，额外 job 只会重复 provision。后续策略必须以技法产物变化为触发点，并保留已解析 session/library scope、成本和重试预算。因此仍不能称为“持续运行的后台回填产品闭环已完成”。

### 6. 状态表、复选框和证据描述曾不一致

原始审计时，`tasks.md` 顶部状态表仍使用旧数量，例如 M0 为 26/35、M3 为 13/18、M4 为 12/20、M5 为 14/18；实际复选框已分别变成 36/36、18/18、20/20、19/19。审计文件自身随后也因工作树继续变化而落后：M2 已从 27/45 变成 42/45，并在 P1.2 真实时钟验收后变成 44/45，最终在 P1.3 Release 50K 全管线门禁后变成 45/45。

同时，状态表“升级仍缺”列仍将部分新勾选能力描述为未完成。这会造成两种相反误读：

- 只看复选框，会误以为 M3-M5 已完成。
- 只看状态表，会忽略截至 2026-07-10 已落地的代码和定向测试。

本次已更新状态表并加入 M9 体验开放项；成熟度仍保持 S，并把“实现覆盖”“默认产品路径”“效果证据”“易用性证据”分开描述。后续仍需在每次勾选变更时同步计数，避免再次漂移。

### 7. 变更体量和单文件规模增加维护风险

当前关键文件规模：

- `SqliteReferenceCorpusWritingService.cs`：约 4,837 行
- `MultiStrategyReferenceCorpusBlueprintCandidateAssembler.cs`：约 1,018 行
- `ChapterReferencePanel.tsx`：约 2,894 行
- `ReferenceCorpusWritingServiceTests.cs`：约 6,512 行

功能边界仍可识别，但继续在这些文件内叠加策略、状态和 UI 会增加回归定位成本。后续拆分应服务于明确职责，而不是进行与 P0-P4 无关的泛化重构。

## 验证结果

以下先列出后续执行的当前验证，再保留初始审计时快照作为问题来源证据；不得把历史失败结果误读为当前工作树验收。

### 后续验证（当前工作树）

- `dotnet test Novelist.slnx --no-restore -v minimal`：`Novelist.Tests` 255/255、`Novelist.IntegrationTests` 1,034/1,034，共 1,289 项通过。桌面烟测释放其启动的 runtime worker 后再删除临时 SQLite 目录，消除了全量并发清理时的文件占用风险。
- `npm --prefix frontend run verify`：TypeScript/Vite build、ESLint、phase16 corpus library/chapter reference、reference-anchor compatibility 与 app smoke workflow 全部通过。
- phase16 相关浏览器 workflow 的 `consoleErrors`、`consoleWarnings`、`pageErrors` 和 `failedRequests` 均为空；截图已归档在 `output/playwright/phase16/`。
- `run-recovery-harness.ps1 -Rounds 2 -RuntimeSamples 30 -Configuration Release`：v2 metrics 通过，强杀 checkpoint 10/10、pause/cancel/stale lease 各 30 个墙钟样本通过，P95 分别为 82.28/87.68/37.17 ms。
- `run-scale-harness.ps1 -Configuration Release`：full-pipeline v1 metrics 通过，50,015 字、16 anchors、4 libraries、32 jobs 和 13,385 work items 全部完成；吞吐 29.16/s，claim/list/progress P95=10.12/27.04/3.51 ms，零重复、零预算穿透、零预留 token、零活动 lease。
- `ReferenceCorpusAnalysisWorkerTests`：12/12 通过，新增真实 loop 的失租回收与 stale-worker fenced commit 回归。
- `run-usability-study-evaluation.ps1 -Fixture tests/Novelist.IntegrationTests/Fixtures/corpus-driven-writing/corpus-writing-usability-contract.json -Configuration Release`：命令成功并生成 `corpus-writing-usability-report-v1`；contract 的 2 名合成参与者中仅 1 名无提示完成全路径，故 `acceptance_passed=false`。报告会聚合 `transition_blocked` 首次失败和 `return_to_blueprint` 恢复动作；该结果按设计保留为工具链验证，不能当作真实用户走查。

### 初始审计结果（历史快照）

### 通过

- `Novelist.Tests`：255/255 通过。
- Corpus 定向集成测试：203/203 通过。
- 前端 TypeScript/Vite production build 通过。
- 前端 ESLint 通过。
- M2 故障恢复 harness：5 个事务故障点、2 轮强杀恢复，共 10/10 case 通过。
- 恢复结果为零重复、零丢失、token 精确结算、reservation 清零。

### 初始未通过（已修复）

- `dotnet test Novelist.slnx --no-restore -v minimal`
  - 集成测试运行到 234 通过、1 失败。
  - 失败项：`LocalOnnxEmbeddingClientRunsBundledBgeModelWhenRuntimeAssetsExist`。
  - 原因：ONNX Runtime 加载本地模型时 `bad allocation`，随后测试主机 OOM 中止。
- `npm --prefix frontend run verify`
  - build 和 lint 通过。
  - corpus library workflow 失败。
  - 原因：后台任务面板内出现两个“已暂停”标签，测试使用未限定行范围的 strict locator。
- 历史 200 万字规模运行（旧门槛）
  - 吞吐 18.99 items/s，低于 20 items/s，总体 `passed=false`。
  - 现仅作为诊断基线，不再列入当前强制门禁。

## 建议收口顺序

### P0：交付基线（已完成）

1. 已从 Git 中移除 `CorpusHarnessHost/bin`、`obj`、PDB 和运行时二进制产物，并补充 ignore 规则。
2. 已修复 corpus 后台任务 workflow 的任务行定位。
3. 已隔离 ONNX runtime 测试的峰值内存并加入释放回归。
4. 后续提交仍应按生成物清理、运行时修复、Stage 3 和文档同步拆分，避免混合评审。
5. 每次复选框变更必须同步任务表、审计和开发方案的数量、阶段门与体验口径。

### P1：M2 后台标准轨（已完成）

1. Stage 3 已接入 canonical job/work-item，并有 enqueue → worker → specimen → retry/pause/restart 直接集成闭环。
2. 已用真实时钟/子进程 harness 完成 pause/cancel P95 与 stale lease 30 秒恢复；人为时间戳 + 直接 reclaim 测试仅保留为语义回归，不再作为时限证据。
3. 保留 1,000 work-item job-store micro-benchmark；50K multi-anchor/multi-library 全管线正式轨已生成可信 Release metrics。
4. M2 标准规模任务已关闭；2M 只在发布前、专项诊断或百万字能力声明时显式运行。

### P2：先建立效果与体验证据资产

建议在继续调整 M3-M5 策略前固定以下最小评测集：

**后续修订（2026-07-11）**：已提供严格的脱敏 fixture schema、聚合报告器和 `run-writing-evaluation.ps1`，并以三案例 `contract` fixture 覆盖稳定生成、泄露字段拒绝和 human 样本数量下限。fixture 现强制固定 reason 码表和同一 insertion case 内评审者哈希唯一；[效果评测标注套件](./evaluations/writing-evaluation-kit.md)提供盲评、评分锚点、脱敏与导出规则。它只证明工具链，不构成真实效果数据；实际收集规则见 [评测协议](./evaluations/README.md)。

1. 50-100 条人工标注章节 query，用于 Recall@K、nDCG 和命中原因准确率。
2. 20-30 组相同目标的多蓝图，用于结构差异、来源差异、重复率和盲评。
3. 20-30 个真实章节插入样本，用于原句保真率、剧情适配率和过渡自然度。
4. 记录用户最终修改字符比例和从生成到接受的迭代次数。
5. 默认将本地报告写入 `build/tmp`；经授权的脱敏 `human` fixture 和聚合报告可按数据策略版本化，不能保存源文、本地路径或原始评审笔记。
6. 固定 5 条核心用户任务；真实浏览器 workflow、桌面视口/缩放/键盘检查已完成，下一步只补 5 人小规模走查和完成时间、回退次数、失败点、主观难度记录。

**后续修订（2026-07-11）**：已提供 `corpus-writing-usability-fixtures-v1`、严格脱敏校验和聚合报告 harness。它要求五条固定任务、哈希化参与者 ID，固定失败/恢复码表，并在报告中聚合两类码；[用户走查执行套件](./evaluations/usability-study-kit.md)提供无引导任务卡、主持人边界和复测规则。`human` 数据至少 5 人且仅当至少 4 人无提示完成全部任务时通过；两参与者 contract fixture 只证明工具链，不能构成真实用户走查。

### P3-P4：按证据加深检索、蓝图和拼装

1. 用人工 query 集标定 M3 融合权重，不以 route marker 存在代替质量。
2. 持久蓝图 coordinator 已接入章节默认 UI，自动模式已收敛为“目标 → 蓝图 → 正文 → 插入”单一主路径，并有直接集成与浏览器测试；后续只以真实用户失败点调整该路径。
3. 只有蓝图区分度达标后，才继续扩展多正文候选。
4. 只有真实正文评测复现失败时，才新增 M5 审计或拼装规则。
5. M9 的自动化默认体验、长任务恢复、错误动作、可访问性和视觉层级已收口，不再扩张专家控制面；只补真实用户走查。

## 最终判断

| 维度 | 偏离程度 | 判断 |
|---|---|---|
| 核心产品理念 | 低 | 仍坚持跨库、蓝图锁源、最大保真和不自由补写 |
| 架构边界 | 低到中 | 主模块边界仍清楚，但关键实现文件迅速膨胀 |
| 实施顺序 | 初始偏离高，现已关闭 P0 停止线 | P0 已恢复；后续只允许按 M2 时限/50K、效果证据和默认体验顺序推进 |
| 状态与文档口径 | 原始偏离高，本次已修订 | 数量、规模分层和体验门已同步；仍缺自动计数/校验以防再次漂移 |
| 产品闭环 | 中到高 | 受控默认路径、持久会话和错误恢复已进入章节 UI；后台维护和真实用户/效果证据仍未闭合 |
| 写作效果证明 | 高 | 尚无真实检索、蓝图和正文效果报告 |
| 使用体验证明 | 中到高 | 默认路径、长任务恢复、可访问性已有浏览器自动化证据；真实用户任务尚未验收 |

综合结论：**方向正确，P0 绿色基线、Stage 3 canonical job、真实时限和 50K 全管线门禁均已收口；当前风险不再是测试不可交付，而是把基础设施覆盖误写成写作价值和使用价值。下一阶段应保持绿色基线，以 50K 作为常规门禁、2M 作为显式长跑，并优先建立真实写作效果与核心用户任务证据。**

## 关联资料

- [开发完善方案](./development-plan.md)
- [分阶段任务清单](./tasks.md)
- [规模 harness 说明](../../scripts/corpus-driven-writing/README.md)
- 本机规模结果（gitignored，仅审计机可用）：`build/tmp/corpus-driven-writing/scale.stdout.json`
- 本机恢复结果（gitignored，仅审计机可用）：`build/tmp/corpus-driven-writing/recovery-metrics.json`
