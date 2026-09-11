# MAF Agent Harness 改造计划

> 结论先行：建议**立即执行 S0–S2**（依赖升级、`IChatClient` 适配层、事件/持久化接缝抽取）。
> 这三步各自独立可交付、可回滚，且**即使最终不采纳 Harness 也有正收益**。
> **S3 及以后**（用 `HarnessAgent` 替换自研主循环）设为「有条件推进」，门槛见第六节。
> 当前自研循环功能完整、被约 4,100 行测试锁定，整体替换的风险高于短期收益；
> 真正稀缺的不是「一个 agent 循环」，而是「一个能接住现有前端事件契约的 agent 循环」。

## Status

计划待评审。未开始实现。本文档不改动任何代码。

## Date

2026-09-04

## 一、背景与现状判定

### 1.1 当前引用的 MAF 只是一个 AIFunction 装箱

全仓唯一的 MAF 家族引用是 `src/Novelist.Agent/Novelist.Agent.csproj:9` 的
`Microsoft.Agents.AI 1.12.0`，而它实际只被用来取到 `Microsoft.Extensions.AI` 的
`AIFunction` / `AIFunctionFactory` / `AIFunctionArguments` 三个类型。

`AIAgent`、`ChatClientAgent`、`HarnessAgent`、`AsHarnessAgent`、`IChatClient`
在 `src/` 下的命中数均为 **0**。`src/` 里唯一出现 "Harness" 一词的位置是
`src/Novelist.Infrastructure/App/ReferenceCorpusAnalysisWorker.cs:232` 的一句注释，
与 Agent Harness 无关；`scripts/corpus-driven-writing/` 下三个 `*Harness` 辅助工程
也只是语料评测宿主，同名不同物。

`src/Novelist.Agent/NovelistMafChatToolExecutor.cs` 是这个装箱关系的证据：
`GetToolDefinitions`（`:18`）把 `function.JsonSchema.Clone()`（`:25`）拷进自研的
`ChatToolDefinition`，`ExecuteAsync`（`:29`）再把裸 JSON 手工编排回
`AIFunctionArguments` 并调 `function.InvokeAsync`（`:56`）。
也就是说：**MAF 类型进来后立刻被降级成自研类型，交给自研循环驱动。**

好消息是，40 个工具已经全部是 `AIFunction`（`NovelistMafToolRegistry.cs:88`
的 `CreateTools` 返回 `IReadOnlyList<AIFunction>`，全部由
`AIFunctionFactory.Create(MethodInfo, target, options)` 生成，没有手写 JSON Schema 字面量）。
工具层是**唯一已经站在 MAF 形状上的部分**，迁移时可原样复用。

### 1.2 自研运行时的规模与对等物

| 模块 | 文件 | 行数 | 职责 | Harness 是否有对等物 |
| --- | --- | --- | --- | --- |
| Agent 主循环 + 会话存储 + 子代理宿主（三合一） | `src/Novelist.Infrastructure/App/FileSystemChatSessionService.cs` | 3,403 | `ChatAsync`（`:345`–`:846`）、工具循环（`:635`，上限 8）、压缩、`ISubagentRunner.RunAsync`（`:954`–`:1111`，上限 50）、JSON 文件存储 | 部分有：函数调用循环、会话持久化、压缩 |
| LLM 传输层 | `src/Novelist.Infrastructure/App/StandardChatCompletionClient.cs` | 978 | 裸 `HttpClient`（`Timeout.InfiniteTimeSpan`）、手写 SSE、chat-completions 与 Responses 双方言、按 provider 的请求/头改写钩子（`:791`/`:825`） | **无**，且 Harness 强依赖 `IChatClient` |
| 自研 LLM 契约 | `src/Novelist.Core/App/IChatCompletionClient.cs` | 97 | `ChatCompletionRequest`/`ChatToolDefinition`/`ChatToolCall`/`ChatCompletionStreamEvent` 等全套 DTO | 有（`ChatMessage`/`ChatResponseUpdate`/`AIFunction`） |
| 工具注册与执行 | `src/Novelist.Agent/`（7 个文件） | 3,463 | 40 个 `AIFunction` | **已对齐**，可直接复用 |
| 审批协调 | `src/Novelist.Core/App/ToolApprovalCoordinator.cs` | 271 | 每调用一次的 `TaskCompletionSource` 阻塞点（`:60`） | 有，且更强（见 2.1） |
| 事件契约 | `src/Novelist.Contracts/App/ChatPayloads.cs:109` `AgentEventPayload` | 179（整文件） | 18 个可选字段的流式事件 | **无**，必须自己保留 |

## 二、Harness 能力矩阵与处置决策

`Microsoft.Agents.AI.Harness` 的入口是 `chatClient.AsHarnessAgent(new HarnessAgentOptions { ... })`，
返回 `HarnessAgent : AIAgent`。它默认打开大量能力，而本项目**多数能力已有领域特化实现**，
因此「默认全开」在这里是负担而不是收益。逐项决策如下。

| Harness 能力 | 默认 | 本项目现状 | 决策 |
| --- | --- | --- | --- |
| 函数调用循环（每请求可配迭代上限） | 开 | 自研，主循环上限 8（`:23`），子代理 50（`:24`） | **采纳**（S3），迭代上限映射为 8/50 |
| 逐服务调用的历史持久化 | 开 | 自研 JSON 单文件 `<dataDir>/sessions/index.json`（`:2712`） | **采纳但需自定义写出形状**（见 3.2） |
| 上下文压缩 | 提供 token 上限或自定义策略时开 | 自研三层：手动 `CompressContextAsync`（`:848`）、自动 `usage_ratio ≥ 80%`（`:1802`/`:618`/`:755`）、共享引擎 `CompressSessionContextAsync`（`:1443`）+ 版本号自增（`:1536`） | **S5 再评估**；先 `DisableCompaction`，保留自研 |
| 待办追踪（todo） | 开 | 无。交互模型是访谈式提问 + 选择题块 | **禁用** `DisableTodoProvider`（与「不扩张专家控制面」冲突） |
| 计划/执行双模式 | 开 | 无。`MainAgentPrompt`（`:62`–`:128`）已用 66 行中文规定行为 | **禁用** `DisableAgentModeProvider` |
| 会话文件记忆 | 开 | 有等价物：`novelist.md` 故事状态 + `search_story_memory` 工具 | **禁用** `DisableFileMemory`（否则两套记忆互相污染） |
| 工具自动审批 / 常驻审批规则 | 开 | **缺口**：`ApprovalMode` 设置项是空转（见 8.1） | **采纳**（S5，价值最高的单项） |
| OpenTelemetry | 开 | 无 | **S5 采纳**，需先定导出目标；桌面端默认不外发 |
| Web 搜索（客户端支持时） | 开 | 自研 `web_search`（DeepSeek 固定端点）+ `web_fetch`（873 行 SSRF 防护） | **禁用** `DisableWebSearch`（重复且会绕过 SSRF 层） |
| Agent Skills（.NET 默认开） | 开 | 自研三层技能合并（novel > user > builtin，先命中者胜，`:1305`）+ `<available_skills>` 目录（`:1393`） | **禁用** `DisableAgentSkillsProvider`（语义冲突，见 7 节） |
| Shell 执行（独立包） | 需另装 | **仓内不存在任何沙箱或 shell 执行**；`src/` 里唯一的 `Process.Start` 是 `SystemExternalUrlOpener.cs:12`（系统 URL 处理器，工具不可达） | **不引入**（引入即是安全回退） |
| 后台 agent | 可选 | 自研两类子代理 `memory`/`review`（`ISubagentRunner`） | **S4 评估**，收益有限 |
| 循环评估器（LoopEvaluators） | 可选 | 无 | 不采纳 |

净结果：**八个 `Disable*` 开关里要关掉五个**。这本身就是一个信号——
Harness 的产品假设（通用编码/研究 agent）与本项目（中文长篇小说协作写作）只部分重叠。
真正想要的是它的**循环 + 审批规则 + 遥测**，不是它的记忆/技能/搜索/待办。

## 三、三个硬约束

### 3.1 没有 `IChatClient`（最大单项工作量）

`AsHarnessAgent()` 是 `IChatClient` 的扩展方法。本仓**没有任何 `IChatClient` 实现**，
只有 `StandardChatCompletionClient`（978 行）直接操作 `HttpClient`。它承载的东西不能丢：

- **双方言**：chat-completions（`BuildPayload` `:180`–`:258`）与 Responses API
  （`BuildResponsesPayload` `:260`–`:299`、`ToResponsesInput` `:301`–`:346`），按
  `provider.EndpointType` 分流。
- **非标字段**：`reasoning_content` 的原样回传（`:208`）、`thinking` 与
  `reasoning_effort`（`:242`–`:255`）、`stream_options.include_usage`（`:233`）。
- **provider 逃生口**：`ApplyProviderRequestAdapter`（`:791`）、
  `ApplyProviderHeaderAdapter`（`:825`）——第三方兼容端点靠这两个钩子活着。
- **手写 SSE 的硬边界**：单行 2 MiB 上限（`:16`，超限抛错 `:67`–`:70`）、
  响应体 64 KiB 截断（`:15`/`:580`）、`Timeout.InfiniteTimeSpan`（`:22`–`:28`）。
- **错误处理**：`SanitizeBody`（`:855`）会**抹掉 API Key** 后才把错误体外抛；
  `Retryable`（`:866`）决定重试语义。这条不能在适配过程中丢失。
- **工具调用是缓冲而非流式的**：`AccumulateToolCalls`（`:457`）攒进
  `Dictionary<int, StreamingToolCall>`，`FlushToolCalls`（`:505`）在流末一次性吐出，
  并为缺 id 的 provider 合成 `call_{n}`。迁到 `IChatClient` 时这套补偿逻辑必须保留，
  否则兼容端点会直接坏掉。

结论：S1 必须写一个 `NovelistChatClient : IChatClient`，**包裹**而不是替换
`StandardChatCompletionClient`。不要试图换成 `Microsoft.Extensions.AI` 的官方
OpenAI 客户端——那会同时丢掉双方言、provider 钩子和 key 脱敏。

### 3.2 前端事件与持久化契约不可位移

`frontend/src/components/chat/ChatPanel.tsx` 是**唯一**的流式订阅方（1,662 行）。
它的约束是硬的：

- **`seq` 必须逐 turn 单调、无缺口**。`EventQueue`（`ChatPanel.tsx:63`）用
  `nextSeq` 排队，缺一个号就永久卡住后续事件。当前 `seq` 由
  `FileSystemChatSessionService` 手工穿线（每个 emit 辅助方法都返回新号，
  `ExecutedChatToolCall.Seq`、`result.LastSequence` 逐层对账，
  `SplitEventData`（`:2981`）把 >8 KiB 的块切片、**每片各消耗一个号**）。
  Harness 的 `AgentRunResponseUpdate` 里没有这个概念，必须在出口侧自己发号。
- **`type` 是整数 0–6，且 C# 侧没有枚举**：唯一具名定义在
  `frontend/src/components/chat/types.ts:4`。0 Thinking / 1 ThinkingDone /
  2 Content / 3 ToolCall / 4 Usage / 5 Error / 6 Compression。
- **`chat:started` 必须先于 `agent:{turnId}`**：前端在收到 `chat:started`
  （`:1016`，命中一次即退订）才知道 turn id，随后订阅 `agent:${turn_id}`（`:1047`）。
  turn id 是整数，来自 `session.LastTurnId` 在互斥锁内自增（`:394`–`:395`）。
- **审批数据搭在 `metadata` 上**：`metadata.approval_type` 与 `metadata.payload`
  （`ToolApprovalCoordinator.cs:154`–`:158`），前端在 `:754`/`:757` 读取，
  `:791` 对 `file_edit` 特判并打开 diff 页签。
- **持久化形状是 UI 契约的一部分**：`rebuildTurns`
  （`frontend/src/components/chat/types.ts:94`）把持久化的 `session.Message[]`
  重放进与实时流**同一棵**分段树。改存储行结构 = 改 UI。
- **`Chat` 以 `{ timeoutMs: null }` 绑定**（`frontend/src/lib/novelist/api.ts:63`/`:283`），
  取消走两条独立路径（`CancelChat` + 链式 CTS），且空 session id 会**故意抛错**
  （`:936`，前端在 `chat:started` 之前挂起取消请求，`ChatPanel.tsx:1130`）。

另有 golden 契约样本 `docs/contracts/golden/event-agent-turn.payloads.json`（9 条事件），
迁移必须让它继续成立——但**它当前已与实现漂移**，见 8.3。

### 3.3 桥方法台账与安全层

- `tests/Novelist.Tests/Bridge/BridgeHandlerRegistrationTests.cs:139` 钉死
  `Assert.Equal(191, BridgeCompatibilityAppMethods.MethodNames.Count)`，
  `:199` 钉死运行时 6 个方法。
- `tests/Novelist.Tests/Bridge/BridgeFrontendContractTests.cs:13` 断言 C# 白名单与
  从 `api.ts` 正则抓取的方法名**集合相等**。新增/改名任一桥方法，必须同一提交改两边。
- 安全层原样保留：`web_fetch` 的 SSRF 防护（`HttpWebFetchService.cs`，
  `IsBlockedAddress` `:767`、每跳重校验 + `MaxRedirects=5`、
  **连接期 `ConnectCallback` 再校验一次** `:838` 以关闭 DNS rebinding 窗口）、
  内容路径白名单与 `SafeChildPath`、内置技能只读、子代理工具白名单
  （`FileSystemChatSessionService.cs:2274` 二次强制）、`edit` 的乐观并发复核
  （`NovelistMafToolRegistry.cs:473`–`:477`）。

## 四、包依赖变更

`Microsoft.Agents.AI.Harness` 最新为 **1.20.0（稳定版，2026-08-31 发布，MIT，191 KB）**，
提供 net8.0 / netstandard2.0 / net472 资产，与本项目的 `net10.0` 兼容。
首个稳定版是 1.14.0（2026-07-21），此前自 2026-05-13 起为预览版。

| 包 | 当前 | 目标 | 说明 |
| --- | --- | --- | --- |
| `Microsoft.Agents.AI` | 1.12.0（`Novelist.Agent.csproj:9`） | **1.20.0** | Harness 要求 ≥ 1.20.0 |
| `Microsoft.Agents.AI.Harness` | — | **1.20.0** | 新增 |
| `Microsoft.Extensions.AI` | 10.6.0（传递，未声明） | **10.9.0** | Harness 要求 ≥ 10.9.0 |
| `Microsoft.Extensions.AI.Abstractions` | 10.6.0（传递） | 10.9.0 | 同上 |
| 新增传递依赖 | — | `Microsoft.ML.Tokenizers ≥2.0.0`、`Microsoft.Extensions.VectorData.Abstractions ≥10.8.2`、`Microsoft.Extensions.FileSystemGlobbing`、`Microsoft.Extensions.DependencyInjection.Abstractions`、`Microsoft.Extensions.Logging.Abstractions`、`Microsoft.Extensions.Compliance.Abstractions`（均 ≥10.x） | 共 6 棵新依赖树 |

三个连带事项：

1. **仓内没有 `Directory.Packages.props`**，`Directory.Build.props`（9 行）只有 MinVer。
   本次要么新建集中版本管理，要么把版本号散落在 csproj 里。建议顺手引入
   `Directory.Packages.props`，否则 `Microsoft.Extensions.AI` 这种「传递但被直接使用」
   的包会继续处于不可控状态。
2. **`Novelist.Infrastructure` 当前没有任何 AI 包引用**，而自研循环恰恰住在这里
   （`FileSystemChatSessionService.cs`）。要在 Infrastructure 里用 `HarnessAgent`，
   必须让它引用 Harness 包，或把 agent 循环挪到 `Novelist.Agent`。
   **建议后者**：`Novelist.Agent` 才是 AI 依赖的既定归属地，Infrastructure 应继续只管
   文件系统/SQLite/RAG。这同时把 3,403 行的三合一类拆开（见 S2）。
3. **自包含发布体积**：`scripts/novelist-publish.sh win-x64` 产出 self-contained 安装包。
   新增 6 棵依赖树（含 `Microsoft.ML.Tokenizers`）会增大安装包，需在 S0 记录变化量。
   另外 Harness 依赖 `Microsoft.Extensions.DependencyInjection.Abstractions`，
   而 `DesktopBridgeComposition.cs` 是**手工 `new` 组装、无 DI 容器**——
   需确认 Harness 的 provider 组装在无容器场景下可用。

## 五、分阶段计划

每阶段都要求「独立可交付、独立可回滚」。禁止出现「S3 做完一半，主循环两套都不能用」的中间态。

### S0：依赖升级与体积基线（0.5–1 天）

- 目标：`Microsoft.Agents.AI` 1.12.0 → 1.20.0，引入 `Directory.Packages.props`，
  显式声明 `Microsoft.Extensions.AI` 10.9.0。**先不引入 Harness 包**。
- 主要风险：`Microsoft.Extensions.AI` 10.6 → 10.9 可能改变 `AIFunctionFactory`
  的 JSON Schema 生成结果，而 schema 直接流进 `ChatToolDefinition.parameters`
  并被 `tests/Novelist.Tests/MafToolRegistryTests.cs`（1,727 行）断言。
- 验收：`dotnet test Novelist.slnx` 全绿；40 个工具的 schema 差异逐项过目并留档；
  记录 `build/bin/novelist/` 体积前后对比。
- 回滚：单提交 revert。

### S1：`IChatClient` 适配层（3–5 天，keystone）

- 目标：新增 `NovelistChatClient : IChatClient`，**包裹** `IChatCompletionClient`，
  双向映射 `ChatMessage`/`ChatResponseUpdate` ↔ 自研 DTO。保留 3.1 列出的全部行为。
- 交付物：适配器 + 与 `StandardChatCompletionClient` 的**逐事件平价测试**
  （复用 `tests/Novelist.IntegrationTests/TestDoubles/FakeLlmHarness.cs`，333 行，
  它是 SSE 层的假 provider，两条路径可喂同一份 SSE 脚本比对输出）。
- 验收：双方言各自的 SSE 脚本、工具调用缓冲/补 id、`reasoning_content` 回传、
  provider 钩子、2 MiB 行超限、key 脱敏，均有测试。
- **独立价值**：有了 `IChatClient`，`Microsoft.Extensions.AI` 的中间件生态
  （日志、遥测、缓存、函数调用中间件）即刻可用，与是否采纳 Harness 无关。
- 回滚：适配器是新增文件，不接线即为死代码。

### S2：抽出事件出口与持久化接缝（3–4 天，纯重构）

- 目标：把 `FileSystemChatSessionService` 里三件事各自抽成独立组件，**行为零变化**：
  1. `AgentEventEmitter`——`seq` 发号、8 KiB 切片（`:2981`）、
     整数 `type` 映射、`agent:{turnId}` 主题、`ToolDisplay.For`（`:3214`）文案。
  2. `ChatSessionStore`——`sessions/index.json` 的读写与 `to_api`/`to_frontend`/`version`
     三元组、`ActiveVersion` 语义、`AllocateMessageId`（`:2772`）。
  3. `NovelStateComposer`——系统消息装配（`:1192`–`:1217`）、技能三层合并（`:1305`）、
     `<available_skills>` 目录（`:1393`）、语料注入（`:2093`–`:2181`）及其
     `CorpusInjectionMarker` 的 `ToApi` 翻转护栏（`:446`–`:455`）。
- 验收：`tests/Novelist.IntegrationTests/ChatSessionServiceTests.cs`（2,022 行）
  **一行不改**全绿；golden 事件样本继续成立。这是「零行为变化」的判据。
- **独立价值**：3,403 行的三合一类被拆开，可读性与可测性都改善；
  同时这三个组件就是 S3 要接的插座。
- 回滚：单提交 revert。

### S3：`HarnessAgent` 接管主循环（5–8 天，特性开关后）

- 目标：新增 `HarnessChatSessionService`，用 `AsHarnessAgent()` 驱动同一批
  `AIFunction`，通过 S2 的三个组件输出事件与持久化。**主代理**先做，子代理不动。
- 配置：`HarnessAgentOptions` 里 `HarnessInstructions` 放 `MainAgentPrompt`，
  迭代上限 8，`MaxContextWindowTokens` 由所选模型上限推导；
  `DisableTodoProvider`、`DisableAgentModeProvider`、`DisableFileMemory`、
  `DisableAgentSkillsProvider`、`DisableWebSearch`、`DisableCompaction` 全开，
  `DisableToolAutoApproval` 暂时保持关闭以复用现有 `ToolApprovalCoordinator`。
- 切换方式：**内部特性开关**（配置文件或环境变量，不进设置 UI——
  `AGENTS.md` 明确「不扩张专家控制面」）。默认走旧实现。
- 验收：黄金转录平价——同一份 `FakeLlmHarness` 脚本喂两套实现，
  比对 `agent:{turnId}` 事件序列（含 `seq`、`type`、`phase`、`metadata`）与
  `sessions/index.json` 落盘内容，允许的差异只有时间戳。
  再加 mock-bridge 浏览器套件与截图证据。
- 回滚：开关归位；旧实现在 S3 内**不删除**。

### S4：子代理（2–3 天，可选）

- 现状只有 `memory`/`review` 两类，工具白名单固定，
  且靠 `DesktopBridgeComposition.cs:158` 的 `DeferredSubagentRunner` +
  `:188` 的 `SetTarget` 打破循环依赖。
- Harness 的 background agents 语义与此不同（并发常驻 vs. 同步委派）。
  **建议**：先只把子代理循环换成同一个 `HarnessAgent` 配置（更小的工具集、上限 50），
  不采纳 background agents，从而顺手消掉 `DeferredSubagentRunner` 这个环。

### S5：择优采纳 Harness 原生能力（2–4 天）

按收益排序，逐项独立上线：

1. **自动/常驻审批规则**（收益最高）：用 Harness 的 `AutoApprovalRules`、
   `ReadOnlyToolsAutoApprovalRule`、`AllToolsAutoApprovalRule` 给现在空转的
   `ApprovalMode="auto"` 赋予真实语义（见 8.1）。
2. **OpenTelemetry**：确认桌面端默认不外发后启用。
3. **压缩**：仅当 Harness 支持自定义策略保住「`usage_ratio ≥ 80%` 触发 +
   中文压缩提示词 + 保留 15 条用户消息/≥4 轮 + `ActiveVersion` 自增」时才替换；
   否则继续用自研，`DisableCompaction` 保持开启。

## 六、S3 推进门槛

S0–S2 可以直接排期。**S3 及以后必须同时满足以下四条**才动手，否则停在 S2：

1. **API 稳定性**：Harness 在 6 周内发了 7 个版本（1.14.0 → 1.20.0，2026-07-21 至 08-31）。
   需观察到发布节奏放缓、或官方给出稳定性承诺，再把桌面端（要出安装包、不能频繁热修）
   的核心链路押上去。
2. **S1 平价测试全绿**：`IChatClient` 适配层在双方言与全部 provider 钩子上零差异。
3. **S2 零行为变化达成**：`ChatSessionServiceTests.cs` 未改一行且全绿。
4. **有明确收益诉求**：当前自研循环功能完整。若届时没有「需要 Harness 才能做的事」
   （最可能的是审批规则与遥测——而这两项可在 S5 单独采纳，不必替换整个循环），
   则 S3 的收益仅是减少维护面，不足以支撑其风险。

**换句话说：如果只想要审批规则和遥测，S0 + S1 + S5 就够了，可以跳过 S3。**
这是本计划推荐的最小路径。

## 七、明确不采纳

| 能力 | 不采纳原因 |
| --- | --- |
| Agent Skills provider | 与自研三层技能体系语义冲突：本项目是 novel > user > builtin **先命中者胜**的同名遮蔽合并（`:1305`），且内置技能只读被强制两次（`NovelistMafToolRegistry.cs:410`–`:413`、`FileSystemChapterContentService.cs:332`）。两套技能发现机制并存会导致提示词里出现重复或矛盾的技能目录。 |
| 会话文件记忆 | 与 `novelist.md`（故事状态单一真源）+ `search_story_memory` 重复，且会在小说工作区里写出用户不预期的记忆文件。 |
| Web 搜索 | 已有 `web_search`（DeepSeek）与 `web_fetch`；后者带 873 行 SSRF 防护。启用 Harness 内置搜索等于开一条绕过该防护的通道。 |
| Shell 执行包 | 仓内不存在沙箱。引入即安全回退，且与 `AGENTS.md` 的 sandbox 要求相悖。 |
| Todo / 计划-执行模式 | 交互模型是访谈式提问 + 每轮最多一个 ```choices``` 块（`MainAgentPrompt` 【访谈模式与选择题】`:79`–`:91`），由前端解析渲染。叠加 todo 与模式切换会破坏这个已定型的交互契约，也与「不扩张专家控制面」冲突。 |
| 用官方 OpenAI 客户端替换传输层 | 会丢掉 Responses/chat-completions 双方言、per-provider 请求与头改写钩子、`reasoning_content` 回传、API Key 脱敏。 |

## 八、顺带修掉的既有债务

这些都是本次改造的自然副产品，建议合并进对应阶段。

### 8.1 `ApprovalMode` 是空转设置（S5 修）

`ApprovalMode` 全链路存在——`AppSettingsPayload.cs:10` 定义、
`FileSystemAppSettingsService.cs:113`–`:121` 校验（`"manual"|"auto"`，默认 `manual`）、
`SetApprovalMode` 桥方法（`AppSettingsBridgeHandlers.cs:45`）、
UI 开关（`ChatControls.tsx:72`、`ChatPanel.tsx:105`/`:254`）——
但**没有任何代码读它来跳过审批**：`EditMafTool` 无条件调
`RequestApprovalAsync`（`NovelistMafToolRegistry.cs:450`），
删除审批同理（`NovelistMafStructuredTools.cs:1140`，仅在 `_approvals is null` 时跳过）。
即 `"auto"` 只是个标签。Harness 的自动审批规则正好补上这个洞，
这是整个迁移里**收益/成本比最高的一项**。

### 8.2 `SafeChildPath` 有 6 份逐字复制（S2 修）

`SafePath` 不是类型而是策略名。真实实现是私有静态方法，重复出现在
`FileSystemChapterContentService.cs:689`（规范版）、
`FileSystemChatSessionService.cs:1351`、`FileSystemSkillCatalogService.cs:454`、
`FileSystemNovelService.cs:534`、`GitVersionControlService.cs:732`、
`FileSystemWritingStatisticsService.cs:306`。
已在 `docs/reference-anchor-implementation/usability-review-2026-07-27.md:102`–`:104` 记为债务。
S2 既然要动 `FileSystemChatSessionService`，顺手收敛成一处共享实现。

### 8.3 golden 事件样本已与实现漂移（S2 修）

`docs/contracts/golden/event-agent-turn.payloads.json` 里出现了两处实现侧不产生的值：

- `seq: 2` 的 `type: 1`（ThinkingDone）——运行时**从不发出** `type=1`，
  尽管前端会消费它（`ChatPanel.tsx:598`/`:678`）。
- `activity_kind: "file_read"` / `"file_edit"`——而 `ToolDisplay.For`（`:3214`）
  实际只产出 `memory`/`view`/`write`/`plan`/`general`/`search`。

必须先判定哪一侧是真契约（建议以实现为准、修正样本；若要保留 ThinkingDone
则需在实现里补发），再让它成为迁移的平价基线。**不要把漂移继承进新实现。**

### 8.4 两个无订阅方的事件（S2 记录，暂不删）

`chat:session_created`（`:526`）与 `chat:title_updated`（`:2664`–`:2667`）在后端发出，
前端无订阅方。`GenerateAndPersistTitleAsync`（`:2605`）为此多打一次非流式 LLM 调用
（30 秒超时，`:2613`–`:2614`）。标题本身有用（会话列表要显示），
但事件通道是死的——要么前端接上做实时刷新，要么承认它是冗余。

## 九、风险登记

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| Harness API 仍在快速演进 | 桌面端需出安装包，无法频繁热修 | 门槛 1；S3 前不押核心链路 |
| `Microsoft.Extensions.AI` 10.6→10.9 改变工具 schema | 40 个工具的 `parameters` 变化，provider 行为漂移 | S0 逐项 diff 留档；`MafToolRegistryTests.cs` 兜底 |
| `seq` 发号在新循环里出现缺口 | 前端 `EventQueue` **永久卡住**，表现为「对话卡在一半」 | S2 把发号收敛到单一出口；平价测试比对完整 `seq` 序列 |
| 持久化行结构变化 | `rebuildTurns` 重放失败，历史会话打不开 | S2 抽出 store 后行结构由单点控制；用真实 `index.json` 做回归 |
| 自包含安装包体积增大 | 分发成本 | S0 记录基线；若超预期则重新评估是否值得 |
| 无 DI 容器与 Harness provider 组装不兼容 | S3 阻塞 | S0 之后先做 30 分钟可行性探针再排 S3 |
| 双实现并存期行为分叉 | 用户在不同开关下看到不同行为 | 开关不进 UI；旧实现默认；平价测试为准出条件 |
| 安全层在重构中被削弱 | SSRF / 路径逃逸 / 内置技能被写 | 这些层**不在**本次改造范围内移动；S2 只搬事件与存储 |

## 十、验证矩阵

| 阶段 | 必过命令 | 补充证据 |
| --- | --- | --- |
| S0 | `dotnet test Novelist.slnx --no-restore -v minimal` | 40 个工具 schema diff；安装包体积前后 |
| S1 | 同上 + 新增适配器平价测试 | 双方言 SSE 脚本、provider 钩子、key 脱敏用例 |
| S2 | 同上，且 `ChatSessionServiceTests.cs` **未修改** | golden 事件样本比对；`index.json` 回归 |
| S3 | 同上 + `npm --prefix frontend run verify` | 黄金转录平价报告；mock-bridge 浏览器套件 + 变更状态截图；键盘/焦点、窄桌面布局、长任务恢复、错误恢复 |
| S4 | 同上 | 子代理 `memory`/`review` 各自的转录平价 |
| S5 | 同上 | 审批规则矩阵（manual/auto × file_edit/delete）；遥测无外发验证 |

全阶段共同的守卫：`BridgeHandlerRegistrationTests.cs:139`（191 个方法）与
`BridgeFrontendContractTests.cs:13`（C# 白名单 ≡ `api.ts` 抓取集合）必须保持绿；
若确需新增桥方法，同一提交内改两侧。语料驱动写作的 50K 闸门（
`docs/corpus-driven-writing/development-plan.md`）不受本改造影响，但 S3 合并前需复跑一次。

## 十一、工作量估算

| 阶段 | 估算 | 是否推荐立即排期 |
| --- | --- | --- |
| S0 依赖升级 | 0.5–1 天 | ✅ |
| S1 `IChatClient` 适配层 | 3–5 天 | ✅（独立价值最高） |
| S2 接缝抽取 | 3–4 天 | ✅（纯重构、零行为变化） |
| S3 `HarnessAgent` 主循环 | 5–8 天 | ⏸ 满足第六节四条门槛后 |
| S4 子代理 | 2–3 天 | ⏸ S3 之后 |
| S5 择优采纳 | 2–4 天 | ✅ 其中「审批规则」可在 S1 之后直接做 |

- **推荐最小路径**：S0 → S1 → S5.1（审批规则）→ S5.2（遥测），约 **6–10 天**，
  拿到本次改造的绝大部分实际收益，且不触碰 3,403 行的主循环。
- **完整路径**：S0 → S5 全量，约 **16–25 个工作日**，另需回归与证据时间。

## 附：稳定入口索引

- 自研主循环：`src/Novelist.Infrastructure/App/FileSystemChatSessionService.cs`
- LLM 传输层：`src/Novelist.Infrastructure/App/StandardChatCompletionClient.cs`
- 自研 LLM 契约：`src/Novelist.Core/App/IChatCompletionClient.cs`
- 工具注册（40 个 `AIFunction`）：`src/Novelist.Agent/NovelistMafToolRegistry.cs:88`
- MAF↔自研降级接缝：`src/Novelist.Agent/NovelistMafChatToolExecutor.cs`
- 审批阻塞点：`src/Novelist.Core/App/ToolApprovalCoordinator.cs:60`
- 流式事件契约：`src/Novelist.Contracts/App/ChatPayloads.cs:109`
- 事件类型定义（唯一具名处）：`frontend/src/components/chat/types.ts:4`
- 唯一流式订阅方：`frontend/src/components/chat/ChatPanel.tsx`
- 台账守卫：`tests/Novelist.Tests/Bridge/BridgeHandlerRegistrationTests.cs:139`、
  `tests/Novelist.Tests/Bridge/BridgeFrontendContractTests.cs:13`
- 平价测试素材：`tests/Novelist.IntegrationTests/ChatSessionServiceTests.cs`、
  `tests/Novelist.IntegrationTests/TestDoubles/FakeLlmHarness.cs`、
  `tests/Novelist.Tests/MafToolRegistryTests.cs`、`tests/Novelist.Tests/ApprovalCoordinatorTests.cs`
- golden 事件样本：`docs/contracts/golden/event-agent-turn.payloads.json`
- 组装入口（无 DI 容器）：`src/Novelist.App/DesktopBridgeComposition.cs`

