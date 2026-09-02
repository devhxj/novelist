# 使用者视角评审与改进计划·第三轮（2026-09-02）

> 评审视角：仍以「用 AI 细化扩写、以参考书积累质感」的写作者为使用者画像，但本轮把走查重心从"动线是否连贯"下移到**"作者的劳动成果会不会丢、走进去还出不出来"**——即数据安全、状态可恢复性、以及"点得到但用不了"的死路。
> 评审基准：`lightweight-refocus-proposal-2026-08-31.md` 产品承诺 + 当前实现代码走查。行号以 `ff6c103` 为基线。
> 与既有评审的关系：第一轮 `robustness-review-2026-09-02(-fixes).md` 回答"出错时系统能否存活"；第二轮 `user-perspective-review-2026-09-02(-fixes).md` 回答"不出错时用得顺不顺"（U1–U6/O1–O6/F1–F6，第一批已交付、`-fixes.md` 六节记录了追加的 7 项）。本轮不复述任何已修项，编号从 **U7/O7/F7** 起续。
> 证据口径：下文每条均为本轮亲自 grep/读码复核过的代码事实，附 `file:line`；凡属"接线统计"类结论标注了统计方法。

## 一、总评

前两轮把**骨架**和**动线**修顺了。这一轮走查暴露出一类此前两轮都没覆盖的问题：**系统在"正常路径"上是自洽的，但在"作者手快、网络慢、判断错了想重来"这三种真实情形下会静默吞掉作者的劳动或把人锁死在某个状态里。**

三条主线结论：

1. **有一处真实的数据丢失路径。** 内容区收到 `file:changed`（AI 写文件）时无条件重取并强制清掉脏标记（`ContentPanel.tsx:354-361`），作者正在输入的未保存文字会被覆盖，且"未保存"提示同时消失——作者既丢了字，也失去了"我丢了字"的感知。这比任何交互摩擦都严重，应当单独优先。
2. **聊天区的"发送/停止"控制面在生成中是失效的。** 发送按钮没有 loading 守卫（`ChatInput.tsx:195` 只判 `disabled || !hasContent`，而 `ChatPanel.tsx:1449-1453` 只传了 `disabled={!hasNovel || !selectedKey}`），停止按钮只在**没有输入内容时**才出现（`ChatInput.tsx:182`）。于是作者在等待中随手再发一条是完全可达的操作，而 `ChatPanel.tsx:895-900` 的重入取消与 `:980-989` 的单槽退订引用会被"先结束的那一轮"拆掉，导致第二轮的流式输出无处落地——正文凭空消失。
3. **多个关键动作没有回头路。** 章节无法删除（全仓 grep 无 `DeleteChapter`/`RemoveChapter`）；材料化跑完或取消后无法再跑（`ReferenceCorpusWorkspace.tsx:398` 的 `canStart` 要求 `!run`）；自动章节切分一旦切错，因模块级缓存永不清空（`:35` 的 `analyzedProfiles` 只有 `set`/`get`）而必须重启应用才能重来。这些是"操作可逆性"缺口，不是功能缺失。

## 二、使用便利性（U7–U12）

### U7 内容区在 AI 写文件时覆盖作者未保存的输入 🔴（数据丢失）

- **现象**：`ContentPanel.tsx:354-361` 的 `file:changed` 处理器重新拉取文件内容后执行 `if (refreshKey === 'content') patch.isDirty = false`，**完全没有检查 `tab.isDirty`**。只要 AI 在作者编辑同一文件时落盘（正文续写、计划更新都会触发），作者未保存的击键被覆盖，且脏标记被清零。
- **影响**：作者一边手改一段、一边让 AI 补下一段，是本产品最核心的协作姿势——恰恰在这个姿势上丢字，且丢得无声。
- **建议**：`file:changed` 分三种处理：① `!tab.isDirty` → 照旧静默刷新；② `tab.isDirty` 且磁盘内容与 tab 基线不同 → **不覆盖**，改为在 tab 上显示"AI 已修改此文件"的冲突条，提供「保留我的」/「用 AI 版本」/「查看差异」三选一；③ 任何情况下都不得在 `isDirty` 为真时清零脏标记。

### U8 生成中可再次发送，且第二轮输出会丢失 🔴（成果丢失）

- **现象**：三处叠加。发送按钮无 loading 守卫（`ChatInput.tsx:195`、`ChatPanel.tsx:1449-1453`）；停止按钮仅在 `isLoading && !hasContent` 时渲染（`ChatInput.tsx:182`），作者只要框里还有字就看不到停止；`ChatPanel.tsx:895-900` 用 `activeCountRef` 检测到并发后调 `CancelChat`，但 `:980-989` 的 `startedUnsubRef`/`agentUnsubRef` 是**单槽**引用，先结束的那一轮会把后一轮的订阅一起退掉。
- **影响**：等待中手快点两次——一个新手必然会做的动作——结果是这一轮正文完全不出现，且作者无法判断是模型没响应还是自己操作错了。
- **建议**：① 生成中发送按钮置灰并把停止按钮改为**始终在生成中可见**（与输入内容无关）；② 退订引用改为按 turn id 的 Map，或在发起新轮前 `await` 上一轮的清理完成；③ 在 `verifyCorpusChatWorkflow` 增"生成中二次发送"断言。

### U9 生成刚开始时按停止是无效操作 🔴

- **现象**：`FileSystemChatSessionService.cs:930-935` 的 `CancelChatAsync` 在归一化后的 session id 为空时直接 early-return。前端在收到 `chat:started` 之前拿不到 session id，此时点停止是**静默 no-op**：按钮有反馈动画，后台照跑。
- **影响**：作者发现发错了立刻想撤——这是最需要生效的一次取消，偏偏无效，且无任何提示。
- **建议**：前端维护"待取消"标志，收到 `chat:started` 后立即补发 `CancelChat`；后端对空 session id 返回明确错误码而非静默成功，前端据此提示"正在建立会话，稍后可取消"。

### U10 首次启动一次网络抖动即永久卡死 🔴

- **现象**：`InitView.tsx:56` 的 `app.GetPlatform().then((info) => {...})` **没有 catch**。失败时 `dataDir` 永远为空字符串，`:128` 一直显示 `{dataDir || '加载中...'}`，`:143` 的开始使用按钮 `disabled={!dataDir || initializing}` 永久置灰。没有错误提示、没有重试、没有手动填路径的兜底。
- **影响**：这是新用户见到的**第一屏**。一次失败 = 应用完全不可用，且作者只看到"加载中..."，会认为程序卡死而卸载。
- **建议**：补 `.catch`，展示错误文案 + 「重试」按钮；同时允许在拿不到默认目录时手动选择数据目录（文件选择器已存在）。

### U11 后端英文异常消息直达中文作者 🟠

- **现象**：`bridgeErrors.ts` 全文 18 行，`describeBridgeError` 只做 `error.message || fallback` 的**透传**，没有任何"错误码 → 中文文案"映射层。而后端存在成段英文消息，例如 `SqliteReferenceMaterializationService.Runs.cs:79` 与 `:87` 抛出的 `"The configured models changed after this materialization run started. Create a new run instead of retrying this generation."`。
- **影响**：作者在最需要指引的失败时刻读到一段英文，且该英文还在要求他"create a new run"——而这个动作恰好被 U13 的 `canStart` 挡住，形成"看不懂 + 做不到"的双重死路。
- **建议**：在 `bridgeErrors.ts` 增加 `code → { message, action }` 映射表，覆盖 `ReferenceMaterializationErrorCodes` 与参考书/计划相关错误码；后端消息退位为诊断详情（折叠展示），前端文案负责作者可读性与下一步动作。

### U12 参考书"处理中"状态永远不显示 🟠

- **现象**：`ReferenceBookSidebar.tsx:58` 判断 `anchor.status === 'queued' || 'running' || 'processing'`，但 `ReferenceAnchorPayloads.cs` 中的真实状态值是 `created/importing/source_imported/segmenting/segments_built/extracting_materials/materials_extracted/detecting_slots/slots_detected/embedding/ready/failed_*/cancelled/stale`——**三个都不存在**。该分支是死代码，正在处理的书一律显示为「待处理」。
- **影响**：作者启动材料化后回到书列表，看到的是"待处理"，会以为没启动成功而重复操作（而重复操作又被 `canStart` 拒绝，错误消息还是英文）。
- **建议**：状态映射改为按 `ReferenceAnchorPayloads.cs` 的真实枚举分组（进行中 / 就绪 / 失败 / 过期），并加**契约一致性单测**断言前端映射覆盖后端全部枚举值，防止再次漂移。

## 三、可操作性（O7–O14）

### O7 章节无法删除 🔴（功能缺失导致的死路）

- **现象**：`DeleteChapter`/`RemoveChapter` 在 `src/` 与 `frontend/src/` 全仓 grep **无任何结果**。桥方法不存在，UI 入口自然不存在。
- **影响**：作者试写了三章觉得开头不对想删掉重来——做不到。只能去文件系统手删，而这会绕过版本历史与索引，留下悬挂引用。章节 CRUD 缺了 D。
- **建议**：补 `DeleteChapter` 桥方法（软删除到回收区 + 版本历史留痕 + 清理相关索引/覆盖度缓存），UI 侧沿用 `NovelDeleteDialog` 的输入标题确认范式；一并处理"删除后章号是否重排"这一产品决策（建议不重排，保留空号，避免后续引用全线错位）。

### O8 材料化跑完或取消后无法再跑 🔴

- **现象**：`ReferenceCorpusWorkspace.tsx:398` `const canStart = activeProfile?.status === 'confirmed' && !run`。只要存在 run 记录就不能启动，而重试入口只在 `:556` 针对 `run?.status === 'failed'` 渲染。于是 `completed` 与 `cancelled` 两种终态都是**死路**。
- **影响**：作者中途取消了想重来、或跑完后改了模型想重跑——都没有按钮。配合 U11 的英文提示，这是本轮体感最差的一处。
- **建议**：`canStart` 放开为"无进行中 run 即可启动"，对已完成/已取消的书给出「重新材料化」按钮并说明后果（新建 run、旧 run 归档保留）；失败态保留现有重试，但当后端返回 `materialization_retry_requires_new_run` 时把按钮切换为「新建一次材料化」。

### O9 章节切分切错后必须重启应用才能重来 🔴

- **现象**：`ReferenceCorpusWorkspace.tsx:35` 的 `const analyzedProfiles = new Map<...>()` 是**模块级**缓存，只有 `:260`/`:280` 的 `set` 与 `:224` 的 `get`，**从无 `delete`/`clear`**。而两个分析入口（`:461` 自动分析、`:474` 手动模板）都门控在 `{!activeProfile && (`。一旦分析出过结果，profile 就永久驻留，两个入口同时消失。
- **影响**：自动切分把"第一卷"识别成一章这类错误无法纠正，作者唯一出路是重启应用——而重启后缓存虽清空，`confirmed` 状态若已落库仍可能挡住重来。
- **建议**：在切分步骤补「重新分析」按钮，点击时 `analyzedProfiles.delete(anchorId)` 并重置 `profile`/`manualTemplate`；缓存改为随 `refreshKey` 失效，或直接下沉为组件 state + 后端持久化，避免模块级全局态跨作品残留。

### O10 工具审批失败会让整轮对话永久挂起 🔴

- **现象**：`WorkspaceView.tsx:173-181` 的 `handleApprove`/`handleReject` 直接 `await app.ApproveTool(...)` / `RejectTool(...)`，**无 try/catch**。而 `app.Chat` 是 `{ timeoutMs: null }`（永不超时）。审批调用一旦失败（桥异常、会话已结束），Promise rejection 无人处理，agent 侧等不到审批结果，这一轮就永久停在"等待授权"。
- **影响**：审批是写作流程里的强制关卡，卡住即整个会话报废，且作者没有任何可操作项——连"取消这一轮"都没有（停止按钮此时可能因 U8 不可见）。
- **建议**：两个 handler 补 try/catch，失败时展示错误 + 「重试授权」/「拒绝并结束本轮」；同时为审批等待加软超时（例如 5 分钟无结果给出"本轮可能已失效，是否结束"提示），避免 `timeoutMs: null` 的无限等待没有任何兜底。

### O11 斜杠命令高亮项与实际插入项可能不是同一条 🟠

- **现象**：`ChatInput.tsx:28-30` 用朴素 `includes` 过滤且**不排序**得到 `filteredItems`，键盘上下键（`:95`/`:100`）与回车插入（`:105` `applySlashSelection(filteredItems[slashIndex])`）全部基于它；但 `:204-206` 把这个列表交给 `SlashMenu` 后，`SlashMenu.tsx:53-65` 用打分函数**重新过滤并重新排序**（`filter(score < 5).sort(by score)`），`:110` 渲染的是重排后的 `filtered`。两个列表顺序不同、长度也可能不同（`SlashMenu` 的 `charMatch` 模糊匹配使筛选口径更宽但作用在已筛过的数组上，只会更短）。
- **影响**：作者看着高亮在「续写」上按回车，插入的可能是「大纲」。核心输入路径上的正确性问题，且极难自查——作者只会觉得"这软件抽风"。
- **建议**：把打分排序上提到 `ChatInput`（或抽成共享的 `useSlashCandidates` hook），`SlashMenu` 退化为纯展示组件、不再自行过滤排序；补一条断言"高亮项 === 回车插入项"的前端单测。

### O12 复核候选列表被 3 秒轮询强制拉回第一页 🟠

- **现象**：候选复核按 `size: 12` 分页（`ReferenceCorpusWorkspace.tsx:181-186`），但 `:221-231` 的 effect 在每次 `statusTick`/`refreshKey`/`selectedAnchor` 变化时都执行 `setCandidatePage(1)`，而 `statusTick` 由 3 秒轮询驱动。
- **影响**：复核是本产品最需要连续专注的重复劳动，作者翻到第 4 页正在判断，几秒后被弹回第 1 页——实际上使超过 12 条的复核无法完成。
- **建议**：`setCandidatePage(1)` 只在 `selectedAnchor`/`run_id` 真正切换时执行，轮询刷新保留当前页；进一步按第二轮 O3 的方向补键盘 J/K 连续确认，让复核变成可持续的流。

### O13 章节材料化进度硬顶 30 条 🟡

- **现象**：`ReferenceCorpusWorkspace.tsx:169-175` 固定请求 `page: 1, size: 30`，界面无分页控件。
- **影响**：参考书普遍远超 30 章，作者看不到第 30 章之后的处理情况，无法判断是卡住还是仍在推进。
- **建议**：补分页或虚拟滚动；至少显示"共 N 章，当前展示前 30 章"并提供跳转到失败章节的筛选（失败章节才是作者真正要看的）。

### O14 个人中心是会顺带丢弃聊天草稿的死路 🟠

- **现象**：`SidePanel.tsx:217-221` 对未匹配的面板渲染「即将推出」；`WorkspaceView.tsx:400` 允许 `setActivePanel('profile')`，`:525` 在 profile 下卸载 ContentPanel，`:562` 的 `{activePanel !== 'profile' && (<ChatPanel` **卸载了聊天区**。ChatInput 的草稿是非受控状态，随卸载丢失。
- **影响**：作者好奇点一下侧栏图标，代价是正在斟酌的提示词全没了，且换来一句"即将推出"。
- **建议**：短期把 profile 入口隐藏/禁用（有 tooltip 说明），不给出可点击的死路；中期 ChatPanel 改为保留挂载（`hidden` 而非卸载）或草稿提升到 ChatPanel 层并持久化到会话设置。

## 四、功能完整性（F7–F12）

| # | 缺口 | 证据 | 缺口评估 |
|---|---|---|---|
| F7 | **74/194 桥方法在前端无任何调用点**（统计方法：对 `api.ts` 全部 194 个方法名逐个 grep `app.X(` 于 `frontend/src`；反向校验 0 个前端调用缺失、0 个失效调用） | 抽样复核全部为 0 调用：`SearchReferenceMaterials`、`GetReferenceMaterialDetail`、`BuildReferenceStyleProfile`、`ListReferenceCorpusReviewQueue`、`GetReferenceSourceProcessingDetail`、`RebuildReferenceAnchor`、`UpdateReferenceAnchorMetadata`、`SearchStoryMemory`、`UpdateDataDir`、`GetReferenceCorpusCascadeImpact` | 后端能力已建成但作者触达不到，占比 38%。按面积排序：语料治理/复核队列 25、锚点与来源 11、素材 10、风格档案 8、风格样本→技能 8、叙事模式抽取 4（另有失效的 `usePatternProgress.ts`） |
| F8 | **章节生命周期缺删除** | 全仓无 `DeleteChapter` | 见 O7。CRUD 缺 D，是完整性而非交互问题 |
| F9 | **无通知机制承载后台结果** | 全仓无 toast 系统；唯一 live region 是 `StatusBar.tsx:155-192` 的材料化条 | 第二轮 O6 的根因层：不是"材料化没通知"，而是**应用没有通知通道**，导致一切后台成果都只能在对应页面被动发现 |
| F10 | **数据目录迁移无 UI** | `UpdateDataDir` 0 调用 | 后端迁移（copy-first + manifest）已就绪，作者换机/换盘的动线完全缺失 |
| F11 | **失败无声化** | 7 处 `console.error` 中 `ChatPanel.tsx:249/388/406/954` 与 `GeneralConfigTab.tsx` 重建失败均无用户可见反馈 | 模型列表、会话列表、消息历史、斜杠命令加载失败后界面呈现为"空"，作者会理解成"没有数据"而非"加载失败" |
| F12 | **末次会话恢复是死代码** | `ChatPanel.tsx:228-229` 在 `.then()` 内赋值 `lastSessionIdRef.current`，`:253` 在另一个 effect 中**同步**读取；两个 effect 均在挂载时运行，读发生在写之前 | `SetLastSession` 实际是只写不读。作者每次重开应用都回到新会话，"接着上次写"的承诺未兑现 |

**第二轮遗留项状态**：F1（计划产物 markdown 化）、F2（scene/trope family）、F3（阈值校准）、F5（语料包通道，部分完成）、F6（真实用户验证）**均仍未开始**，本轮不重复展开，排期见下。

### 补充：其他已核实项（可批量处理）

- **N1 内容导入的超时缺口 🟠**：`api.ts:328` 的 `RegisterReferenceMaterializationSource` 带 `{ timeoutMs: null }`，而同族的 `:416` `RegisterReferenceMaterializationSourceFromContent` 用的是普通 `appMethod`，继承 30 秒默认超时（`bridge.ts:1` `DEFAULT_BRIDGE_TIMEOUT_MS`）。按内容注册（含未来的拖拽导入）在大文件上会中途超时，而后端可能已在写入。
- **N2 对话框可达性 🟡**：`SettingsDialog`/`HelpDialog` 缺 `role="dialog"`/`aria-modal`、无 Escape 关闭、无焦点陷阱；`ExportDialog.tsx:76-86` 与 `ExtractStyleDialog.tsx:136-140` 把 Escape 监听挂在**不可聚焦的 div** 上（键盘用户按不出效果）；`BookshelfView.tsx:218-221` 用不可聚焦的 div 打开作品（键盘无法进入作品）。
- **N3 快捷键覆盖过窄且会失效 🟡**：全局仅 Ctrl+S 与 Ctrl+Shift+V，且两者都随 ContentPanel 卸载而消失（切到 profile/语料区后保存快捷键静默失效）。
- **N4 删除类操作无撤销 🟡**：3 处 `window.confirm` 式删除，确认后不可逆、无回收站。

## 五、改进计划

排序原则：**先堵数据丢失与死锁，再打开回头路，最后补完整性**。每批给出验收口径（对齐 AGENTS.md：UI 改动需 build/lint/聚焦浏览器工作流/截图）。

### 第一批：数据安全与解锁（🔴 全部，优先于任何新功能）

| 项 | 改法 | 验收 |
|---|---|---|
| U7 | `file:changed` 在 `tab.isDirty` 时不覆盖、不清脏标记，改出冲突条（保留我的/用 AI 版本/查看差异） | 集成或前端单测：脏 tab 收到 `file:changed` 后内容与 `isDirty` 均不变；工作流断言冲突条出现 + 截图 |
| U8 | 生成中禁用发送、停止按钮生成中恒显；退订引用改按 turn id 管理 | `verifyCorpusChatWorkflow` 增"生成中二次发送"场景，断言第二轮流式输出完整落地 |
| U9 | 前端"待取消"标志 + `chat:started` 后补发；后端空 session id 返回错误码 | 单测覆盖 `CancelChatAsync` 空 id 分支；工作流断言早期取消生效 |
| U10 | `InitView` 的 `GetPlatform` 补 catch + 错误态 + 重试 + 手选目录兜底 | 工作流注入失败断言错误文案与重试按钮可用 |
| O10 | 审批 handler 补 try/catch + 重试/结束本轮；审批等待加软超时提示 | 工作流注入 `ApproveTool` 失败，断言不挂起且提供出路 |
| O8 | `canStart` 放开为"无进行中 run"；完成/取消态给「重新材料化」；`retry_requires_new_run` 切换为新建 run | `test:reference-workspace` 断言三种终态均可再次启动 |
| O9 | 切分步骤补「重新分析」，清理 `analyzedProfiles` 缓存 | 工作流断言分析→重新分析→再次得到 profile |
| O11 | 打分排序上提到 `ChatInput`，`SlashMenu` 降为纯展示 | 前端单测断言"高亮项 === 回车插入项"（含重排场景） |
| N1 | `RegisterReferenceMaterializationSourceFromContent` 补 `{ timeoutMs: null }` | `reference-materialization-api.test.mjs` 断言超时配置与同族方法一致 |

#### 第一批落地记录（2026-09-01）

九项全部完成并通过验收：build / lint / `test:node`（21 例）/ `test:phase16` / `test:app` / `--grep=@writing` / `--grep=@error` 全绿；`dotnet test` 700/702（2 例为 `ReferenceCorpusAnalysisWorkerTests` 的 Windows 文件锁并发抖动，单独重跑 12/12 通过）。新增截图：`app-approval-submit-failure.png`、`app-approval-end-turn.png`、`app-00-init-platform-failure.png`、`file-change-conflict-bar.png`、`reference-reanalyze-rematerialize.png`。

实现中顺带修掉的四个连带问题（都是验收工作流暴露的真实缺陷）：

- **冲突条会被 autosave 静默取消（U7 语义补全）**：脏 tab 收到 `file:changed` 挂起冲突后，已排队的 500ms autosave 仍会把作者缓冲落盘并顺手清掉冲突条，等于替作者按了"保留我的"。现在冲突挂起期间 `handleEditorChange` 不再排队 autosave，事件侧挂冲突时也撤销已排队的定时器；显式 Ctrl+S 与「保留我的」仍正常落盘。
- **`WorkspaceView` 的 `GetPlatform` 缺 catch**：与 U10 同源的未处理拒绝，在 InitView 兜底页复现时成为页面错误；该调用只喂状态栏系统标识，失败静默保留默认文案。
- **确认切分未同步缓存（O8/O9 连带）**：`ConfirmReferenceChapterSplit` 成功后只 `setProfile`，`analyzedProfiles` 里仍是"待确认"的旧分析；材料化完成触发 `refreshKey` 重挂时从缓存读回旧状态，把「重新材料化」入口整个锁死。确认成功后同步覆盖缓存。
- **Monaco DiffEditor 卸载竞态**：关闭 diff 标签页（冲突「用 AI 版本」/「保留我的」、审批收尾都会走）偶发页面错误 `TextModel got disposed before DiffEditorWidget model got reset`。`DiffEditor` 增加 `keepCurrentOriginalModel`/`keepCurrentModifiedModel`，不再由包装层抢先 dispose 模型。

另记一处观察（未改动，留给第二批 F12 一起看）：mock 的 `chat:started` 若携带 `session_id`，前端会在首轮流式中途因"加载历史消息" effect 重建 turns，把正在流式的气泡冲掉。真实后端的行为需要对照验证；当前 mock 仅在早期取消场景（U9 需要补发路径）携带 session_id。

### 第二批：可感知与可持续（🟠，紧随其后）

| 项 | 改法 | 验收 |
|---|---|---|
| F9 | 引入**统一通知通道**（toast + `aria-live`），承载后台完成/失败/重试入口 | 工作流断言材料化完成在非制作页也能收到通知；可达性检查 live region |
| F11 | 7 处 `console.error` 全部接入通知或就地错误条（含重试） | 工作流注入模型列表/会话/消息加载失败，断言可见错误而非空白 |
| U11 | `bridgeErrors.ts` 增 `code → { message, action }` 映射，后端消息降为折叠诊断 | 单测覆盖映射表对 `ReferenceMaterializationErrorCodes` 的全覆盖 |
| U12 | 参考书状态映射改按真实枚举分组 | **契约一致性单测**：前端映射必须覆盖 `ReferenceAnchorPayloads.cs` 全部状态值 |
| O12 | `setCandidatePage(1)` 仅在锚点/run 切换时执行 | `test:reference-workspace` 断言轮询刷新后停留在当前页 |
| F12 | 末次会话恢复改为在设置加载完成后再触发（依赖同一 Promise 或状态） | 工作流断言重开后回到上次会话 |
| O14 | profile 入口隐藏/禁用；ChatPanel 改为保留挂载 | 工作流断言切换面板后草稿仍在 |
| O13 | 章节进度补分页 + 失败章节筛选 | 断言 >30 章的书可查看全部与仅失败项 |
| N2/N3/N4 | 对话框补 `role="dialog"`/`aria-modal`/Escape/焦点陷阱；Escape 监听移到 document 或可聚焦容器；卡片改 `button`；快捷键提升到 WorkspaceView 层；删除类操作补撤销窗口 | 键盘全流程走查 + 截图；快捷键在非内容面板下仍生效 |

#### 第二批落地记录（2026-09-01）

11 项全部完成并通过验收：build / lint / `test:node`（23 例，含 2 个新契约测试）/ `test:phase16` / `test:app` / `--grep=@writing` / `--grep=@error` 全绿。新增截图：`materialization-toast.png`、`reference-progress-filter.png`。

- **F9**：新增 `lib/toast.ts` + `ToastHost`（`aria-live="polite"` 容器，错误条目单独 `role="alert"`），材料化完成/失败会经统一通道推送通知并附「打开素材库」跳转动作，工作流断言非素材库面板也能收到。
- **F11**：9 处 `console.error` 全部接入可见反馈——6 处会话/技能/取消类失败走 toast，`GeneralConfigTab` 重建失败新增就地 ErrorCallout（含可复制诊断），模型列表与消息历史失败沿用既有的横幅 + 重试。
- **U11**：`bridgeErrors.ts` 新增 `bridgeErrorGuide`（19 个材料化错误码 → {message, action}），命中映射时后端消息降级为 `detail` 折叠诊断；契约测试直接解析 `ReferenceMaterializationPayloads.cs` 断言全覆盖。
- **U12**：新增 `referenceAnchorStates.ts`，按 `ReferenceAnchorBuildStates` 真实枚举逐一给出标签/语气/可用性（`stale` 不再误标"待处理"）；契约测试解析 .cs 断言 18 个状态全覆盖且仅 `ready` 可用。
- **O12**：run 明细按 `run_id` 判定"新 run"——只有锚点/run 切换才重置候选与进度分页；同 run 轮询刷新按已加载页数整段重拉进度，候选列表不再被打回第一页。
- **O13**：章节进度补「加载更多」分页与「仅看失败章节」筛选；mock 提供 45 章 / 6 失败的确定性数据支撑验收。
- **O14**：ChatPanel 改为常驻挂载（个人中心态用 `hidden` + `aria-hidden` 隐藏），切换面板不再丢失输入框草稿。
- **F12**：末次会话恢复改为状态驱动——设置加载完成后写入 `pendingLastSessionId`，恢复 effect 在设置与 novelId 都就位时执行并消费，消除"列表 effect 先跑、恢复被跳过"的时序竞态；工作流以预置 `last_session_id` 断言重开后恢复。
- **N2**：新增 `useDialogA11y`（`role="dialog"` + `aria-modal` + document 级 Escape + Tab 焦点圈定 + 初始焦点），Settings/Help/Export/ExtractStyle 四个对话框接入；书架卡片主体补 `role="button"` + 键盘 Enter/Space。
- **N3**：Ctrl+S / Ctrl+Shift+V 监听器从 ContentPanel 提升到 WorkspaceView，经 `ContentPanelHandle.saveActiveTab/toggleActivePreview` 委托执行，面板卸载时优雅空操作。
- **N4**：偏好与读者认知条目的删除接入撤销窗口——删除前截获完整内容，删除成功后 toast 提供「撤销」动作原样重建；角色/地点删除因关联数据无法完整恢复，维持显式确认。

### 第三批：完整性承诺（跨迭代）

1. **O7 章节删除**：软删除 + 版本留痕 + 索引清理 + 覆盖度缓存失效；产品决策取"不重排章号"。
2. **F10 数据目录迁移 UI**：接线 `UpdateDataDir`，复用 copy-first + manifest，导入前展示体积与耗时预估。
3. **F7 未接线能力分批接入**：按作者价值排序——先「素材检索/详情」（支撑第二轮 O2 的"本章参考范围"与 U4 的指路补书），再「参考书元数据事后编辑」（`UpdateReferenceAnchorMetadata`，解第二轮 U6 的硬编码），再「语料治理/复核队列」；`SearchStoryMemory` 与叙事模式抽取若确认不进本期路线，**应删除死代码**（含 `usePatternProgress.ts`）而非留作悬挂能力。
4. **承接第二轮遗留**：F1 计划产物 markdown 化 → F2 scene/trope family → O3/O4 证据链与原文定位 → F5 语料包 JSONL 通道 → F3 阈值校准 + F6 真实用户验证。顺序与依赖关系同第二轮文档第五节，不作调整。

#### 第三批落地记录（2026-09-01）

- **O7 章节删除（完成）**：新增 `DeleteChapter` 桥方法（白名单 194 → 195，前后端契约测试同步更新）。实现为软删除：章节条目移入 `deleted_items` 留痕、`next_chapter_number` 高水位保证章号**不重排、不复用**；正文与大纲伴生文件删除并写入 `delete chapter NNN` 版本提交（正文经 git 历史可追溯）；正文/大纲分别触发 RAG 索引 stale 标记。集成测试覆盖不重排、不复用、留痕、索引清理与重复删除报错。前端章节列表新增「删除章节」（确认 + toast 撤销动作，撤销以原标题新建章节恢复）。
- **F10 数据目录迁移 UI（完成）**：`GeneralConfigTab` 数据目录区新增「更改…」入口——copy-first 语义说明、新目录输入、二次确认、迁移中状态、成功/失败反馈（失败含可复制诊断，原目录不受影响）。体积/耗时预估依赖后端新接口，列为后续增强。
- **F7 分批接入（首批完成）**：① `UpdateReferenceAnchorMetadata` 接入参考书侧栏（每本书「编辑」入口：书名/作者/标签行内编辑，license/visibility/source_trust 原值保留），工作流断言改名、标签写回与调用参数；② 删除死代码 `src/hooks/usePatternProgress.ts`（0 引用）。「素材检索/详情」「语料治理/复核队列」按计划留待后续迭代。
- **验证**：build / lint / `test:node`（23）/ `test:app` / `test:phase16` / `--grep=@writing` / `--grep=@reference-workspace` 全绿；`dotnet test` 216 + 703 全部通过（含新增 O7 集成测试）。新增截图：`chapter-delete-undo-toast.png`。


### 不做的事（明确出界）

- **不扩大专家控制面**（AGENTS.md 红线）：F7 的接线一律以"作者动线上需要"为准入，不因"后端已有"而堆叠面板；语料治理 25 个方法不做成管理后台。
- **不引入全自动无人确认材料化**：O8/O9 放开的是"重来的权利"，章节边界人工确认关卡保留。
- **不为 U7 引入自动合并**：冲突交给作者三选一，不做自动 diff 合并（写作文本的自动合并错误代价远高于一次选择）。
- **不做通用插件/宏系统**：N3 只补必要快捷键，不引入可配置键位。

## 六、值得保持

- 前两轮的成果在本轮走查中稳定成立：三态覆盖横幅（含失败重试）、用量卡与历史重放、制作页两段式编号向导、`buildVisibleError` 错误呈现范式、`ModelConfigTab.tsx:198-247` 以 TestConnection 门控保存、`lib/layout.ts:101-129` 的布局钳制、`NovelDeleteDialog` 输入标题确认、`ContentPanel.tsx:636-650` 保存失败重试、`role="separator"` 的可达分隔条、启动时的导入恢复横幅。
- `bridgeErrors.ts` 的"透传优先"设计方向正确，本轮只需在其上叠加映射层，不必重写。
- 桥方法清单与 `api.ts` **零漂移**（194 对 194，无缺失、无失效调用）——这层契约纪律值得继续用测试守住。

## 七、本轮排除项

以下 8 项属第二轮 `-fixes.md` 已交付内容，本轮已复核为**修复有效**，不再计入缺口：聊天覆盖度失败重试、章节默认值、可访问性与恢复证据、`test:node` 测试接线与 AGENTS.md 口径同步、`CorpusAreaView` 与 `CorpusUsageCard` 的格式清理，以及 `-fixes.md` 六节记录的 #1–#7 全部条目。
