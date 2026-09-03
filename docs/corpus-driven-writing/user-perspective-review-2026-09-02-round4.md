# 复盘评审与改进计划·第四轮（2026-09-02）

> 评审性质：**落地质量复评**。前三轮（`robustness-review-2026-09-02(-fixes).md`、`user-perspective-review-2026-09-02(-fixes).md`、`user-perspective-review-2026-09-02-round3.md`）共 60 项已全部处置；本轮不再重复已修项，走查重心是**第三轮三批落地代码本身**（O7 章节删除、F10 迁移 UI、F7 元数据接线、F9/F11 通知与错误通道、O10/O14 审批与常驻面板），以及对前三轮都未覆盖的存量面做补充扫描。
> 用户角色视角：本轮按四个角色画像走查——**换机/搬家的作者**（数据目录迁移）、**手滑后悔的作者**（删除与撤销）、**依赖读屏/键盘的作者**（通知可达性、长等待逃生门）、**整理书架的作者**（封面、元数据、死入口）。
> 证据口径：每条均为本轮亲自读码复核（两个独立走查 pass + 人工复核关键项），附 `file:line`，行号以 `e1f2010` 为基线；基线测试 `dotnet test` 216/216 单测 + 703/703 集成全绿。
> 编号续接第三轮：U13 起、O15 起、F13 起、N5 起。

## 一、总评

前三轮把「出错时能存活」「不出错时用得顺」「劳动成果不丢」依次修了出来，但本轮发现：**第三轮自己落地的一批代码里，有两处的实现与它声称的语义不符**——这类问题比存量缺陷更危险，因为 UI 文案、mock 工作流和测试都在为不真实的承诺背书：

1. **F10 数据目录迁移没有复制任何数据，UI 却承诺 copy-first**（U13）。`FileSystemAppInitializationService.UpdateDataDirectoryAsync`（`FileSystemAppInitializationService.cs:87-104`）只做"改 config.json 指针 → 对新目录跑启动恢复 → 失败回滚指针"，**没有复制、没有清单、没有校验**。而 UI 明确写着"迁移会把当前数据目录完整复制到新位置（copy-first：复制完成并校验前不动原目录）"（`GeneralConfigTab.tsx:378`）、"复制完成前不会改动原目录"（`:87`）。作者把目录"迁"到一个空目录后看到的是空书架，而成功提示说"应用已切换到新目录"——照提示删掉旧目录就是**永久数据丢失**。mock（`mock-bridge.mjs:1053-1055`）同样只改 override，唯一后端测试只断言指针重指，所以全绿。这直接违反 AGENTS.md「现有用户数据迁移必须 source 不动并写 manifest」的要求。
2. **O7 章节删除留下三条失真路径**（O15/O16/U14）：删除已被持久化但编辑器里开着的 tab 还能 Ctrl+S/自动保存，把已删章节的文件**无声复活成孤儿文件**（无元数据、不入列表、git 里却有一次"复活提交"）；「撤销」只以新章号恢复**标题**，几千字正文只存在于 git 历史；撤销窗口只有 toast 的 5 秒自动消失。

除这两条主线外，通知通道（F9）与审批逃生门（O10）的落地质量也有可验证的缺口（live region 首播无声、带动作通知会被挤出、审批"提交成功但轮次死亡"无出路），外加一项跨视图一致性缺陷（`GetMaxChapterNumberAsync` 与新章号分配器口径不一致）和一项契约设计缺陷（元数据编辑是整体回写）。

## 二、数据安全（U13、O15、O16、U14）

### U13 数据目录迁移：UI 承诺 copy-first，后端只重指指针 🔴（数据丢失风险）

- **现象**：见总评第 1 条。另有三处放大因素：① `api.ts:490` 的 `UpdateDataDir` 绑定继承 30s 默认超时（`bridge.ts:1`），就算以后真做复制，大目录必然超时，且超时后 UI 报"迁移未能完成，原数据目录未受影响"而后端可能已经把 `config.json` 改掉——**错误提示与真实状态相反**；② 迁移进行中设置对话框可以被 Escape/遮罩/✕ 直接关掉（`SettingsDialog.tsx:18,30,60`；`migrating` 只禁用表单自己的按钮），调用失去归属，还能再发起第二次迁移；③ 迁移成功后仍打开的旧 UI 状态（作品列表、会话）指向已切换的数据源，后续保存会写进新目录的空 store。
- **影响**：换机/换盘是作者的低频高危操作；当前实现的"成功"状态等于数据全丢的前奏。
- **修法**：后端把 `UpdateDataDirectoryAsync` 改为真正的 copy-first 迁移——目标校验（拒绝同路径/互为嵌套）→ 递归复制整个数据目录（跳过 reparse point、冲突文件跳过并计数，复用 `LegacyDataMigrationService.CopyDirectoryRecursiveAsync` 的语义）→ 写 `relocation_manifest.json`（status/copy 统计/warning）→ **复制完成后**才重指 config → 启动恢复；任一步失败回滚 config、source 不动。返回值从 void 升级为统计 payload（复制/跳过文件数、manifest 路径）。前端 `UpdateDataDir` 绑定 `{ timeoutMs: null }`；成功文案展示复制统计与清单路径；SettingsDialog 加 busy 守卫（迁移中不可关闭）。迁移期间后台 worker 已由 `CoordinatedAppInitializationService.RebindAsync` 停止（`CoordinatedAppInitializationService.cs:64-94`），SQLite 连接按操作开关（`SqliteReferenceAnchorService.cs:6635` `Pooling=false`），复制窗口是安全的。
- **验收**：集成测试——迁移后新目录包含全部文件、manifest 为 completed、config 指向新目录、**source 逐文件未动**；嵌套目标拒绝且 config 不变；目标已有冲突文件时跳过计数进 manifest。工作流断言成功文案含复制统计、迁移中对话框不可关。

### O15 删除已打开章节后，保存会把文件复活成孤儿 🔴

- **现象**：`SaveContentAsync` 先写文件后查章节库（`FileSystemChapterContentService.cs:317` 写盘，`:323` 才 `FindIndex`；`index < 0` 时文件已落盘、不更新元数据、不做 RAG stale 标记，但 `:356` 照常 git 提交）。`DeleteChapterAsync` 不发 `file:changed`，前端章节列表只刷新自己（`ChapterList.tsx:130`），编辑器里开着的 tab（含 localStorage 恢复的 tab，`ContentPanel.tsx:148-160`）毫不知情。于是删除后：作者的 500ms 自动保存（`ContentPanel.tsx:319-324`）、Ctrl+S、或 Agent 的 `edit_file`（`NovelistMafToolRegistry.cs:479` 走同一 `SaveContentAsync`）都会把 `chapters/NNN.md` 写回磁盘。
- **影响**：孤儿文件有内容、无元数据——不出现在章节列表、不进语料注入，但占着 git 历史；聊天面板的章号绑定还指向已删除章号。作者视角是"我删了它又自己回来了"或"我的章节消失了"（取决于看列表还是看 tab）。
- **修法**：后端——`SaveContentAsync` 对 `chapters/NNN.md` 路径**先查库再写盘**，章号不在 store（含已删除）即拒绝并给出明确错误（导入流程先 `CreateChapterAsync` 再保存、Agent 编辑同理，不受影响）；前端——删除成功后关闭内容面板里对应正文/大纲 tab（`ContentPanelHandle` 增加 `closeTabsByPaths`，ChapterList 经 SidePanel→WorkspaceView 接线）。
- **验收**：集成测试——删除后对同章号 `SaveContentAsync` 抛错且文件未复活；前端工作流断言删除后 tab 关闭。

### O16 删除章节的 git 提交失败会跳过 RAG stale 标记 🟠

- **现象**：`DeleteChapterAsync` 的 stale 标记在 `:262-263`，位于 `:252` 的 `CommitIfChangedAsync` 之后且在 mutex 外无兜底——提交抛错时异常直接向上传播，stale 标记被跳过，RAG 索引继续把已删章节喂给语料注入。同时 `:247-250` 的 `catch { }` 注释声称"孤文件可由索引清理兜底"，**全仓不存在任何索引清理实现**，注释是假的。
- **修法**：stale 标记提前到提交之前执行（无论提交成败都完成）；删除误导注释（O15 的守卫落地后，孤文件无法再被保存放大，风险收敛）。
- **验收**：单测/集成断言 store 持久化后 stale 标记必然发生（提交失败路径）。

### U14 章节删除「撤销」只恢复标题，且窗口只有 5 秒 🔴（成果丢失）

- **现象**：撤销动作是 `app.CreateChapter({novel_id, title})`（`ChapterList.tsx:140`）——按高水位分配**新章号**、**空正文**、原标题；toast 为 `info` 级 5 秒自动消失（`toast.ts:29`），这就是撤销窗口的全部。确认对话框却写"可在通知里选择「撤销」恢复"。
- **影响**：作者手滑删了 6000 字的一章，"撤销"得到的只是一章空壳；字数只在 git 历史里，且没有 UI 能取回。
- **修法**：删除前先把正文与大纲内容读进内存（`GetContent`，删除是本地操作不存在时延问题）；撤销时 `CreateChapter` → 把保存的内容写入新章号的正文/大纲路径；带动作的 toast **不再自动消失**（见 U15），撤销说明如实写"将以新章号恢复正文与大纲"。章号不复用是第三轮的既定产品决策，维持不变。
- **验收**：工作流断言撤销后新章节含原文内容；toast 在动作执行/手动关闭前不消失。

## 三、可达性与控制面（U15、U16、O17、O18）

### U15 ToastHost 空闲时卸载 live region，首条通知可能不被读屏播报 🟡

- **现象**：`ToastHost.tsx:60` `if (toasts.length === 0) return null`——容器与首条 toast 同一帧插入 DOM。NVDA/JAWS 只可靠播报"已存在"的 live region 的变化；每轮通知清空后容器再次卸载，"空闲后的第一条通知"（恰恰是 F9 想解决的"材料化完成时人在别的页面"场景）有静默风险。
- **修法**：容器常驻渲染，仅子项为空。
- **验收**：DOM 断言空态下 `aria-live` 容器仍存在。

### U16 带动作的 toast 会被 4 条上限静默挤出，撤销/错误动作随之丢失 🟠

- **现象**：`toast.ts:49` `[...toasts, item].slice(-MAX_VISIBLE)` 一律挤最老的——多本书材料化同时完成时（`WorkspaceView` 按锚点逐个推），章节删除撤销（U14 的唯一入口）或带上下文的错误可能被无痕挤掉；且所有 toast 都按 kind 定时自动消失，与"动作未处理就消失"冲突。
- **修法**：带 `action` 的 toast 不自动消失、不参与上限挤出（上限只约束无动作 toast；动作执行或手动关闭时移除）。
- **验收**：单测/工作流——连续推 6 条含 2 条带动作的 toast，动作条不消失、无动作条被挤出。

### O17 审批"提交成功但本轮已死"没有任何逃生门 🟠

- **现象**：`ToolCallCard.tsx:113-117` 的 6 秒慢路径计时器只在 `submitting` 期间运行。若 `ApproveTool` 成功返回但后端会话已死（`app.Chat` 是 `timeoutMs: null`，后续工具事件永远不来——正是 O10 要防的场景），卡片回到"等待审批"常态，只有 拒绝/批准 两个按钮，「结束本轮」只在 `submitError || slow` 时渲染（`:175`）。作者唯一的出路是故意再提交一次制造 `submitError`——这恰是 `WorkspaceView.tsx:195-197` 明确防的重复提交。
- **修法**：慢路径计时器从卡片进入 `awaiting_approval` 起持续运行（不依赖 submitting），超时即提示并可「结束本轮」。
- **验收**：工作流注入"提交成功但无后续事件"，断言 6 秒后出现「结束本轮」且可用。

### O18 停止按钮可能取消错误的会话；待取消标志是全局单槽 🟠

- **现象**：`handleStop` 取消的是 `sessionId` **state**（`ChatPanel.tsx:1042-1048`）。生成期间在会话列表选中另一个历史会话，历史 effect 会 `setSessionId(S2)`（`:391-401`）——此时按停止，发出的是 `CancelChat(S2)`，真正在跑的 S1 继续烧 token、其事件流因 turns 重建而被丢弃。`pendingCancelRef`（`:144`）是单一布尔，`:964-966` 任意 `chat:started` 到达即消费补发，轮次重叠时可能补发到错误会话。
- **修法**：以"当前流式轮次的 session id"为取消目标（`chat:started` 到达时记入 per-turn 引用，轮次结束时清除）；停止时优先取消该引用，state 会话只作兜底。
- **验收**：前端单测/工作流——生成中切换会话后按停止，断言 CancelChat 参数是正在生成的会话。

## 四、一致性与契约（O19、O20、N5）

### O19 `GetMaxChapterNumberAsync` 与新章号分配器口径不一致 🟠

- **现象**：O7 落地后 `AllocateChapterNumber` 按 `NextChapterNumber` 高水位分配（`FileSystemChapterContentService.cs:495-506`：删除第 50 章后新建是 51），但 `GetMaxChapterNumberAsync`（`:85-99`）仍返回 `Items.Max`（49）。消费方 `TimelineView.tsx:94,125`、`ArcListView.tsx:113,144`、`StoryArcGraph.tsx:91,124` 的坐标轴与"下一章"推导会与实际分配错位——计划槽位瞄准推导值 50 时，永远匹配不到真实章节。
- **修法**：返回 `Math.Max(Items.Max, NextChapterNumber - 1)`（即历史最高章号，与"不复用"决策一致），补集成断言。
- **验收**：删除尾章后 `GetMaxChapterNumber` 仍返回被删章号；新建章号为 max+1。

### O20 参考书元数据编辑是"整体回写"，并发面被静默回滚 🟡

- **现象**：`UpdateReferenceAnchorMetadataPayload`（`ReferenceAnchorPayloads.cs:221-229`）要求 `license_status/visibility/source_trust` 全量必填；前端编辑表单把打开表单时的快照原样传回（`ReferenceBookSidebar.tsx:168-170`）。若两处之间这些字段被其它路径变更（同服务的提升/归档流程会改 owner scope/visibility），这次保存会把它们**静默改回旧值**——后端无版本/时间戳校验，last-write-wins。
- **修法**：契约改为三字段可空 + `JsonIgnoreCondition.WhenWritingNull`，null 即"保持现值"；前端不再回传这三个字段；后端 `UpdateMetadataAsync` 对 null 走 keep-current。这是 additive 变更，旧调用方（全量传参）行为不变。
- **验收**：契约测试更新；集成断言 null 时字段不变、传值时更新。

### N5 `bridgeErrorGuide` 命中时的后端原始消息从未被渲染 🟡

- **现象**：`bridgeErrors.ts:106` 把后端 message 降级为 `detail`，但全部消费点只读 `.message`（`ReferenceBookSidebar.tsx:124,177,235` 等，grep 无任何 `BridgeErrorDiagnostic.detail` 渲染点）。第三批记录声称"后端消息降级为折叠诊断"——折叠 UI 并不存在。映射文案偏泛时（如"大模型请求失败"），作者与 bug 报告都拿不到具体证据。
- **修法**：参考书侧栏与制作页的错误条在 message 下渲染 `<details>` 折叠诊断（复用既有诊断折叠模式）。
- **验收**：工作流注入带 code 的错误，断言折叠诊断可见。

## 五、功能完整性（F13）

### F13 桥方法零调用面：72/195，本轮处置 8 项 🔍

复核口径与第三轮一致（对 `api.ts` 全部方法逐个 grep 调用点；白名单与 api.ts 零漂移）。第三批后为 **72/195 零调用**（UpdateDataDir/DeleteChapter 已接线，新增方法均接线）。按第三轮 F7 的处置原则（作者动线准入 + 死代码删除）分三档：

**本轮删除（确认不在路线图或已被取代）**：

| 方法 | 依据 |
|---|---|
| `StartNarrativePatternExtraction`/`CancelNarrativePatternExtraction`/`GetNarrativePatternRun`/`GetNarrativePatternTrace` | 叙事模式抽取整域死代码（`usePatternProgress.ts` 已删，无任何 UI 消费方）；第三轮已预告"确认不进路线即删"。后端服务按方案 §五 保留 |
| `SearchStoryMemory` | 同上，RAG 故事记忆检索无消费方 |
| `SaveSettings` | 被 `SaveLayoutSettings`/各专项 Save 取代；guardrails `bridge-guardrails.mjs:272` 已把同族的 `SetChatPanelWidth` 定性为 retired |
| `SetChatPanelWidth` | 同上 |
| 死组件 `MultiRangeChapterSelector.tsx` | 全仓零引用 |

删除范围：前端 `api.ts`/`types.ts` 绑定与类型、白名单（195→188）、对应 bridge handler 注册、mock bridge 实现、计数断言同步。**本轮接线**：`DeleteCover`（书架封面"移除封面"，配套既有 `GetCover`/`SaveCover`）。

**留待后续批次决策（不本轮处置，防一次性大删混入行为修复）**：风格样本→技能 ×8、风格档案 ×8、语料治理/复核队列 ×24、锚点/来源 ×10、素材 ×10、小说导入恢复 ×3、`GetReferenceUserFeedback`（写侧已接线，读侧需产品定位）。处置原则沿用第三轮：接线以作者动线为准入，语料治理不做成管理后台；确认不进路线的下一轮删除。

**第三轮遗留项状态更新**：F7 第二批次（素材检索/详情 + 本章参考范围）仍未开始；scene/trope 分析管线启用、计划镜像懒迁移、切分分析后台 job 化、跨设备语料包、F6 真实用户验证——均维持原排期，本轮不动。

## 六、改进计划与验收口径

排序原则同前三轮：**先堵数据失真，再修控制面，最后做完整性卫生**。

### 第一批：数据安全与失真修复（🔴/🟠，全部本轮落地）

| 项 | 改法 | 验收 |
|---|---|---|
| U13 | copy-first 真迁移 + 统计返回值 + `timeoutMs:null` + 对话框 busy 守卫 + 如实文案 | 集成测试三场景（复制成功/嵌套拒绝/冲突跳过）+ 工作流（成功文案含统计、迁移中不可关对话框）+ 截图 |
| O15 | 后端保存守卫（先查库再写盘）+ 前端删除后关 tab | 集成断言删除后保存被拒且文件不复活；工作流断言 tab 关闭 |
| U14 | 撤销恢复正文与大纲 + 带动作 toast 不自动消失 | 工作流断言撤销后内容在；toast 常驻 |
| O16 | stale 标记先于 git 提交执行；删误导注释 | 集成断言提交失败路径仍 stale |
| U16 | 动作 toast 免挤出/免自动消失；上限只约束无动作条 | 单测 |
| U15 | live region 容器常驻 | 工作流 DOM 断言 |
| N6(新) | 删除章节扣减写作统计（`-word_count`） | 集成断言 writing log 出现负 delta |

### 第二批：控制面与一致性（🟠/🟡，本轮落地）

| 项 | 改法 | 验收 |
|---|---|---|
| O17 | 审批慢路径计时覆盖整个 awaiting_approval 时长 | 工作流"提交成功但轮次死亡"场景 |
| O18 | 停止按"当前流式轮次会话"取消 | 前端断言 CancelChat 参数 |
| O19 | `GetMaxChapterNumberAsync` 并入高水位 | 集成断言 |
| O20 | 元数据三字段可空=保持现值 | 契约+集成测试 |
| N5 | 错误条渲染折叠 detail | 工作流断言 |

### 第三批：完整性卫生（本轮落地）

删除 7 个死桥方法 + 1 个死组件（白名单 195→188，契约/mock/计数断言同步）；接线 `DeleteCover`。截图：书架封面移除。

### 不做的事（明确出界）

- 不做迁移的"移动后删除旧目录"选项——source 永远不动，清理由作者自行确认后手动完成。
- 不为 U14 引入"原章号复活"（undelte 复用旧号）——与第三轮"章号不复用"决策冲突，撤销以新章号恢复。
- 不一次性删除风格/治理等 60+ 未接线方法——留待专门批次，防大删与行为修复混排。

## 七、实施中发现的新缺陷（N7、O21，随批次修复）

### N7 `AllocateChapterNumber` 对"删除末尾章"场景永久跳号 🟠

- **现象**：分配公式为 `max(NextChapterNumber, 现存最大章号) + 1`，而 `NextChapterNumber` 只在删除时写入"被删章号 + 1"。对 {1,2} 删除第 2 章：`NextChapterNumber=3`、现存最大=1，公式给出 **4**——从未使用的章号 3 被永久跳过。第三轮文档自己承诺"删除第 50 章后新建是 51"，现有代码给 52；第三轮集成测试只覆盖了中位删除（{1,2,3} 删 2 → 建 4，两种公式恰好同值），尾删场景从未被锚定。
- **修法**：分配公式改为 `max(NextChapterNumber, 现存最大章号 + 1)`——`NextChapterNumber` 语义收敛为"下一个可分配的新章号"。中位删除行为不变（既有测试锚定），尾删不再跳号。
- **验收**：`GetMaxChapterNumberIncludesDeletedHighWaterMark` 覆盖 {1,2} 删 2 → 新建为 3。

### O21 `chat:started` 携带 session_id 时，首轮流式输出被历史加载中途冲掉 🔴（真实后端必现）

- **现象**：真实后端的 `chat:started` **总是**携带 `session_id`（`FileSystemChatSessionService.cs:550`）。前端收到后同时 `setSessionId` + `setActiveSessionId`，而 `activeSessionId` 驱动"加载历史消息" effect（`ChatPanel.tsx` 历史加载块）→ `setTurns(rebuildTurns(msgs))` 把正在流式渲染的气泡整体替换掉。第三轮在 U9 落地时已经在 mock 注释里记录了这个行为（"mock 的 chat:started 若携带 session_id，前端会在首轮流式中途因'加载历史消息' effect 重建 turns，把正在流式的气泡整个冲掉"），选择的处置是**让 mock 普通分支不再携带 session_id 以规避**，并注明"真实后端的行为需要对照验证"——前端缺陷本身没有修。连带后果：chat 工作流里"停止"路径依赖 sessionId（chat:started 提供），mock 停发 session_id 后 `waitForBridgeCall('CancelChat')` 断言从 `95b2bb0` 起就已断裂（本轮以 `test:app:full` 在 HEAD 基线复现为证）。
- **影响**：对真实桌面应用，作者发出的**每一轮首个流式回复都会在中途闪烁消失再以历史重放形态回来**；mock 全绿掩盖了它整整一轮。
- **修法**：`liveTurnsSessionIdRef` 标记"当前 turns 属于哪一场本端流式"——`chat:started` 置标记；历史加载 effect 见到 `activeSessionId === 标记` 即跳过重放（turns 已是权威状态）；切换会话/新会话/换作品时清除标记。mock 普通分支恢复与真实后端一致的 `session_id` 发射，删除规避性注释。
- **验收**：`test:app:full` 全绿（chat 停止路径的 CancelChat 断言自第三轮以来首次真正走通）；流式断言（"first turn stream fully lands"等）在携带 session_id 的 mock 下保持通过。

## 八、落地记录（2026-09-02）

### 第一批：数据安全与失真修复（全部完成）

- **U13 copy-first 真迁移**：新增 `DataDirectoryRelocationService`（递归复制 + reparse point/冲突跳过 + `relocation_manifest.json` 清单 + 同路径/互为嵌套拒绝）；`UpdateDataDirectoryAsync` 改为"复制 → 写清单 → 重指 config → 启动恢复"，未初始化状态退化为纯指针写入（既有行为）。出站契约升级为 `UpdateDataDirResultPayload`（复制/跳过/警告计数 + 清单路径）；前端 `UpdateDataDir` 绑定 `{ timeoutMs: null }`；成功文案呈现复制统计、清单路径与"原目录未做任何改动"；SettingsDialog 增加 busy 守卫（迁移中 Escape/遮罩/✕ 均被拦截并 toast 提示）。集成测试三场景：复制成功（含 source 逐文件快照比对）、嵌套目标拒绝（config 不动、目标目录不产生）、冲突文件跳过进清单。
- **O15 复活守卫 + tab 关闭**：`SaveContentAsync` 对 `chapters/NNN.md` 先查章节库再写盘，章号已删/不存在即拒绝（导入流程先建章后保存、Agent 编辑走同一入口，均不受影响）；删除成功后 `ChapterList → SidePanel → WorkspaceView → ContentPanel.closeTabsByPaths` 关闭正文/大纲 tab 并清 `tabTarget`。集成测试断言删除后保存抛错且文件不复活；工作流断言 tab 关闭。
- **U14 撤销恢复内容 + 常驻动作通知**：删除前读取正文与大纲进内存，撤销时 `CreateChapter → SaveContent(正文/大纲)`，恢复后 toast 报告新章号；`toast.ts` 重构——带动作的通知不自动消失、不参与 4 条上限挤出（上限只约束无动作条，从最老的无动作条开始挤出）；`ToastHost` 的 `aria-live` 容器常驻（U15）。工作流断言撤销后原文回到编辑器、SaveContent 恢复调用恰好一次。
- **O16/N6/O19/N7（后端四处）**：stale 标记移到 git 提交之前（提交失败也不留给索引脏数据）并删除"索引清理兜底"的虚假注释；删除章节按 `-word_count` 扣减写作统计；`GetMaxChapterNumberAsync` 并入高水位；分配公式修复尾删跳号（见第七节）。

### 第二批：控制面与一致性（全部完成）

- **O17**：审批慢路径计时器改为覆盖整个 `awaiting_approval` 时长（`status/submitting` 变化时重置），"提交成功但后端已死"6 秒后同样出现「结束本轮」。
- **O18**：`activeTurnSessionIdRef` 在 `chat:started` 时记录当前流式轮次的会话、轮次收尾清除；停止与重入取消都以该引用为准，中途切换历史会话不再取消错对象。
- **O21（实施中发现，见第七节）**：`liveTurnsSessionIdRef` 防止历史加载 effect 冲掉流式中的 turns；mock 恢复与真实后端一致的 `chat:started` 携带 `session_id`，chat 工作流的 CancelChat 断言恢复有效。
- **O20**：`UpdateReferenceAnchorMetadataPayload` 的 `license_status/visibility/source_trust/user_tags` 改为可空（`WhenWritingNull`），null = 保持现值；前端编辑表单不再回传这些字段。集成测试断言省略时字段不变、传值时更新；既有全量传参调用行为不变。
- **N5**：新增共享 `ReferenceErrorStrip`（message + 可选「重试/关闭」+ 折叠「诊断详情」），参考书侧栏与制作页全部错误位接入；命中 `bridgeErrorGuide` 映射时后端原始消息以折叠诊断呈现，不再丢失。

### 第三批：完整性卫生（完成）

- **删除**（白名单 195→188，契约/mock/计数断言同步）：叙事模式抽取 ×4、`SearchStoryMemory`、`SaveSettings`、`SetChatPanelWidth` 的桥暴露；连带删除 `NarrativePatternBridgeHandlers`、mock 的叙事模式全套机器（约 300 行）与无调用方的 `verifyPatternBridgeCalls` 守卫函数、前端 `pattern` 命名空间与死组件 `MultiRangeChapterSelector`/`chapterRange.ts`/其 node 测试与 npm script。**保留**：叙事模式与故事记忆的后端服务与 Agent `search_story_memory` 工具（真实消费方）、Phase15 契约 payload 测试（按方案 §五"服务保留"）；桥契约文档追加退役清单。
- **接线 `DeleteCover`**：`BookCover` 在存在封面时显示「移除封面」（z-10 压过悬浮更换层，失败走 toast）；mock 补状态化 `GetCover/SaveCover/DeleteCover`（1×1 PNG），书架工作流断言保存后按钮出现、移除后 `DeleteCover` 调用与按钮消失。

### 验证基线

`dotnet test Novelist.slnx`：216/216 单测 + 708/708 集成全绿（净增 5：迁移 ×3、章节守卫/统计/高水位 ×3、元数据部分更新 ×1，移除 2 个随桥退役的 dispatch 测试）。前端：`build`（仅存量 chunk 警告）、`lint` 0 error 0 warning、`test:node`（23 例，含新增 toast 单测）、`test:app`、`test:app:full`、`test:phase16`、`test:app:stress`、`test:error-ui` 全绿——其中 `test:app:full` 在本轮开始时于 HEAD 基线即为红（O21 的 mock 规避所致），修复后首次全绿。新增截图：`cover-remove-button.png`；更新场景：`chapter-delete-undo-toast.png`（含 tab 关闭与内容恢复断言）、数据目录迁移（断言复制统计与清单路径文案）。

排查纪律备注：本轮对 `test:app:full` 的三段式定位（带改动失败 → 单文件回退仍失败 → 全量回退到 HEAD 仍失败）确认了 chat 断言断裂属第三轮遗留而非本轮回归，随后按"mock 应模拟真实后端"的原则修复了被规避的前端缺陷本身。

### 验收审计补全（2026-09-03）

对照第六节验收口径复核落地状态，发现并闭合 4 处缺口——教训是"跑了 test:app:full ≠ 覆盖全部套件"：`verifyApprovalSubmitErrorRecovery` 只挂在 `@error` grep 下，初次验证基线漏跑了它，导致两处断言失效未被发现：

1. **O17 文案断裂 + 场景补全**：慢路径提示改为常驻型文案（`审批等待已超过 6 秒没有进展`）后，`@error` 工作流仍断言旧文案（`提交已超过 6 秒没有回应`）——套件红。已同步文案；并在第一页（提交成功后）扩展文档要求的"提交成功但无后续事件"场景：等 6 秒断言慢提示与「结束本轮」出现，点击后卡片转 failed、无残留等待卡（`test:error-ui` 全绿）。
2. **U16/U14 单测**：新增 `tests/toast.test.mjs`（esbuild 打包 toast store）：无动作条 4 条上限从最老挤出；带动作条入队零定时器、不被挤出；触发全部定时器后动作条仍在、无动作条消失；dismissToast 可移除动作条。
3. **U15 空态断言**：材料化通知工作流在动作点击、通知清空后断言 `toast-host` 容器仍恰有 1 个（live region 常驻）。
4. **O18 停止参数断言**：chat 工作流停止路径断言 `CancelChat` 的参数等于本轮 `chat:started` 送达的 session id（`Chat` 结果回填后比对）。
5. **既有断言加固**：chat 工作流的长 markdown 断言在共享长驻页面上会命中 corpus 与 chat 两轮同文内容（严格模式冲突），按文件既有惯例加 `.first()`（断言意图不变）。

补跑结果：`test:error-ui`、`test:app:full`、`test:phase16`、`test:node`（23）、`lint` 全绿。

## 九、复盘二轮：落地代码缺陷走查（2026-09-03，R1–R10 全部处置）

对 `834dbfb`/`5f6981e` 两批落地代码做独立缺陷走查（不复述第六、八节已修项），发现 10 处新缺口——全部是"修复自身引入的次生问题"或"修复覆盖面不全"两类。逐项处置如下：

| # | 缺口 | 严重度 | 处置 |
|---|---|---|---|
| R1 | 设置对话框 busy 守卫可被绕过且会"粘死"：迁移中切换到「模型配置」tab 会卸载 GeneralConfigTab——busy 既可能永久滞留 true（对话框关不掉），也可能在重挂载后归零（迁移进行中却能关闭、还能再发起第二次） | 🟠 | busy 期间禁用 tab 导航（`disabled`）；工作流新增"迁移挂起"场景（UpdateDataDir timeout fault）：断言 tab 锁定、✕ 强制点击被 guardedClose 拦截并提示、对话框保持打开（截图 `data-dir-migration-busy.png`） |
| R2 | 迁移重试进入上次失败的目标目录时，遗留的部分复制产物被当作作者数据"冲突跳过"——源此后改过的内容永远进不了目标，成功提示却宣布迁移完成 | 🟠 | `RelocateAsync` 启动时读取目标既有清单：status 为 failed/running 时本轮对冲突文件改为**以源覆盖**（源始终权威）；成功文案区分"内容冲突跳过（N 个，见清单）"与"相同文件跳过"；集成测试 `RelocationRetryOverwritesStaleFilesFromFailedPriorAttempt` |
| R3 | O15 复活守卫只覆盖 `chapters/`：`ParseChapterNumber` 不认 outline 路径，Agent 编辑或残留 tab 仍可把已删章节的 `outlines/NNN.md` 复活成孤儿（照常 git 提交） | 🟠 | 新增 `OutlinePathPattern`，守卫改为正文与大纲同查章节库（`guardedChapterNumber`）；元数据/字数更新仍只认正文路径；集成测试断言 outline 保存被拒且文件不复活 |
| R4 | N6 的统计扣减位于"元数据已持久化之后、文件清理之前"且不容错：统计存储损坏会让删除在半途抛错——文件残留、stale 标记与 git 提交全部丢失，前端报"删除失败"但章节其实已删 | 🟠 | 扣减改尽力而为（try/catch，与 stale 标记同口径）——统计是派生数据，删除主流程不可被其中断；集成测试注入抛错的 recorder 断言删除仍完整（文件删除 + stale 标记 + 列表清空） |
| R5 | O21 的等效标记在"切走"时被清除：流式中切到别的会话再切回，历史重放会把在途回复整体冲掉且永不落地显示（服务端落库正常，纯展示层丢失）；另历史加载无响应竞态守卫（快速 S2→S1 切换时慢响应覆盖新会话） | 🟠 | 新增 `liveTurnsSnapshotsRef`（离开流式会话时留存 turns 快照，切回时恢复而非重放，本轮收尾作废快照）；历史加载加 seq 守卫（过期响应丢弃）；`handleNewChat` 同步留存快照 |
| R6 | U14 的撤销读取用 `.catch(() => '')` 吞掉真实读取故障——撤销会静默降级成"只恢复标题"，却仍提示"正文与大纲已还原" | 🟡 | 读取失败即中止删除并显式报错（后端对缺失文件本就返回空串，真正抛错的只有桥/IO 故障） |
| R7 | localStorage 恢复的章节 tab 若指向已删章节：打开是空白编辑器（像数据丢失），直到保存才见到英文报错 | 🟡 | ContentPanel 恢复 tab 后与 `GetChapters` 一次性核对，失效章节 tab 自动关闭并 toast 说明（校验失败不阻塞编辑） |
| R8 | 章节守卫的英文异常消息直达中文作者 | 🟡 | `doSave` 识别守卫消息特征并替换为中文动作指引（"该章节已被删除……请关闭标签页或新建章节"），诊断详情仍保留原文 |
| R9 | 动作 toast 不自动消失也不参与挤出，但自身无界——多本书材料化完成一次推 N 条，旧卡片被顶出屏幕、动作按钮点不到；容器也无溢出处理 | 🟡 | 动作条上限 4 条（超限从最老丢起，`MAX_ACTION_VISIBLE`）；容器加 `max-h` + `overflow-y-auto`；单测覆盖"推 7 条动作条只剩最新 4 条" |
| R10 | 迁移回滚路径若自身抛错（恢复 config 失败），新异常会掩盖原始迁移错误，前端拿到错误的失败原因；极端时指针仍指向新目录 | 🟡 | `InitializeAsync`/`UpdateDataDirectoryAsync` 的回滚体各自包 try/catch——回滚失败只放弃回滚，原始异常照常上抛 |

**走查同时核实为无问题的项**（避免下轮重复怀疑）：`TryLoadConfigAsync` 重构未改变其它调用方行为；高水位对存量 store（`next_chapter_number` 缺省 0）与 999_999 边界均正确；前端三处 outline 路径拼法（padStart(3)/D3）对千章以上一致；O20 keep-current 与工作区可见性不变量兼容；stale 标记移入章节互斥锁无重入；迁移复制窗口被 bridge Exclusive 门与 worker 停止完整隔离。

### 复盘二轮验证基线

后端：`dotnet test` 全绿（新增 outline 守卫、统计容错、迁移重试覆盖 ×3）。前端：`build`、`lint` 0 警告、`test:node`（23 例，含动作条上限）、`test:app:full`（含新增"迁移挂起"场景）全绿。新增截图：`data-dir-migration-busy.png`。

### 残余事项（不阻塞）

1. 迁移完成后仍打开的旧 UI 状态（作品列表、会话）不会被强制刷新——已切换数据源后的陈旧写风险由各服务的目录解析器兜底，完整的"迁移后全量刷新"留给后续。
2. 迁移无进度条（复制统计只在完成后呈现）；大目录体验依赖"请勿关闭应用"提示，进度事件属后续增强。
3. F13 第二档（风格样本→技能 ×8、风格档案 ×8、语料治理 ×24、锚点/来源 ×10、素材 ×10、导入恢复 ×3、`GetReferenceUserFeedback`）待专门批次决策。
4. 审批「结束本轮」对 O17 场景依赖前端主动收尾；后端会话死亡检测（心跳/超时）仍属 `timeoutMs: null` 的长期课题。

