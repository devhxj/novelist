# Release Notes

### 2026-07-12

- `素材库`的大模型材料判断现在以小批次、固定采样和严格工具结果约束执行；DeepSeek 等带思考输出的模型不会再因健康检查输出过短被误判为不可用。
- 材料判断只接受受限结构化结果，并继续核验候选、来源跨度、分数和标签；模型返回正文、未知字段或不合规内容时会明确失败，不会作为可用素材展示。
- 自动章节切分同样使用严格结构化调用；对前 50K 原文的分析会等待完整工具结果，不会把模型的思考输出或空正文误当成无效切分。
- 章节切分预览现在能识别电子书前置目录中的重复标题，并在目录标记、链接尾注和正文标题序列一致时排除目录，避免目录被当成空章节或重复材料处理。
- 候选构建会把可与前后文衔接的低信息短段合并为完整语义窗口；孤立短句仍进入大模型判断，不会仅因长度被规则丢弃。
- 由多个连续句节点完整覆盖的超长自然段会拆为不重叠、可回溯的窗口，每个窗口保持在技术长度上限内，不会截断来源正文。
- 运行材料化相关测试不会再改写桌面应用的数据目录，已配置的模型和本地素材库会保持可用。

### 2026-07-11

- 桌面应用现在会在当前显示器工作区内恢复并夹紧已保存的窗口位置和尺寸；显示器布局变化后不会因旧位置让窗口不可见。启动失败会显示可关闭的图形错误页并指向本地诊断日志，安装包启动也不再额外打开控制台窗口。
- Chapter-level `参考素材` now keeps the default writing path focused on “goal → choose a writing blueprint → review prose → explicitly insert”. Source details, identifiers, and manual controls remain available as progressive expert detail instead of competing entry points.
- A chosen corpus-writing blueprint is now restored from the server-owned chapter session after the panel or app is reopened, so authors can continue from the selected plan without rebuilding the same step.
- `素材库` can start analysis as a persistent background task, let authors leave the page, and return to the same task with its progress and a clear recovery action for paused, failed, exhausted, or blocked work.
- `素材库`现在以左侧参考书籍管理、中间六维语料覆盖与按需检索、右侧 AI 蓝图预演组成单一工作台；预演仅比较候选方案，不创建写作会话，也不会写入章节正文。
- Candidate use remains explicit and auditable: the chapter panel only updates the editor buffer after source, license, similarity, and draft checks pass; it does not silently save or insert chapter prose.

### 2026-07-09

- Phase 16 reference-anchor IA is now documented as two user workflows: `素材库` for shared corpus processing, and chapter-level `参考素材` for current-chapter use. The default corpus activity no longer needs to expose chapter orchestration/debug controls for source import, processing, material browse, tag correction, archive/restore, or style-profile work.
- Source and material inspection now has a below-material drill-down path. `GetReferenceSourceSegmentDetail` can open bounded, path-free source-segment detail from processing records, including failed extraction events where no material row exists yet.
- Server-owned material tag review is documented as the source of truth for unverified, low-confidence, and unknown tag queues. Conflict-tag review remains future work until analyzer output persists a durable conflict signal.
- Phase 16 frontend verification is part of `npm --prefix frontend run verify`; it runs focused corpus-library and chapter-reference browser workflows in addition to the existing reference-anchor compatibility and app smoke checks.
- Corpus processing recovery continues to harden around duplicate import, partial failure, retry/rebuild, startup recovery, stable material/source ids, preserved user corrections, and no duplicate searchable material rows. Early no-output `created` / `importing` / `source_imported` / `segmenting` restart recovery now rebuilds to ready/searchable output without fake rows, and the same no-output early startup path now degrades to redacted `failed_import` with zero fake output when the source disappeared before restart. Recoverable pre-embedding startup recovery asserts active-only default search after restart while preserving archived materials and user corrections, and missing-source startup recovery from a recoverable pre-embedding state now degrades to `failed_import` while keeping prior active material searchable, archived material hidden, user corrections intact, and diagnostics redacted. Slotted pre-embedding startup recovery for `detecting_slots`, `slots_detected`, and `stale` now asserts active-vector indexing resumes after restart while slot records, material ids, archive state, active-only search, and user corrections remain stable. Missing-source startup recovery from those same slotted states now keeps retained material/slot/vector counts and records affected slot ids in processing detail. Duplicate import recovery now covers pre-embedding `segments_built`, `extracting_materials`, `materials_extracted`, `detecting_slots`, `slots_detected`, and `stale` states while preserving material ids, archive state, user corrections, active-only search, and processing history. The corpus-library browser workflow now verifies duplicate batch-source resubmission reuses the same ready anchor, persists the failed-import source row for the failed item, exposes its processing detail, and does not add second ready or failed source rows. Failed import, segmenting, extraction, and slotting recovery now assert processing-detail attempt metadata, including current attempts, prior failed attempts, recovered-from ids, and blocked reasons. The corpus-library browser workflow now verifies failed-import processing detail drawers, redacted copyable diagnostics, explicit rebuild recovery, explicit rebuild retry-failure diagnostic refresh, recovered app-restart/interrupted-embedding processing detail with affected material/source-segment drill-down, slots-detected startup recovery with recovered-from ids and one searchable indexed material row, missing-source startup recovery degraded to failed import with retained material detail, bounded source-segment detail, and explicit rebuild to one searchable retained material row, failed-extraction processing detail with bounded source-segment drill-down plus explicit rebuild to one searchable recovered material row, and failed-slotting processing detail with retained material detail plus explicit rebuild to one searchable retained material row, all without exposing local paths, source text, candidate text, or prompts. Failed import rebuild after prior output now asserts retained material ids, archive state, user corrections, active-only search behavior, and affected material/source-segment detail. Initial failed import retry failure now has integration coverage for updating stale diagnostics while preserving zero segment/material fake output. Cancelled embedding rebuild failure now has integration coverage for updating diagnostics while preserving material ids, archive state, user corrections, affected ids, and searchable active materials. Failed segmenting retry failure now has integration coverage for updating diagnostics while preserving zero segment/material fake output. Failed extraction retry failure now has integration coverage for updating diagnostics while preserving source-segment detail and zero fake material rows, so users can still drill into bounded source-segment evidence from processing records. Failed segmenting/extraction rebuild rollback now asserts archived materials stay hidden and archived while the prior active corpus remains searchable. Failed slotting retry failure now has integration coverage for updating diagnostics while preserving material ids, archive state, user corrections, affected ids, and searchable active materials. Failed slotting during rebuild now rolls back to the prior searchable corpus, preserving source hash, material ids, slots, archive state, vector count, and user corrections. Workspace-corpus interrupted embedding recovery has integration coverage for consuming-novel default search activation, archived-material hiding, active-vector counts, and preserved user corrections. The full retry/recovery matrix is still tracked in `docs/reference-anchor-implementation/tasks-phase-16.md`.
- Failed-extraction retry now covers a missing-source downgrade to `failed_import`: durable source segments remain inspectable through processing detail/source-segment detail, no fake material rows are created, and diagnostics stay redacted.
- Failed-slotting retry now covers a missing-source downgrade to `failed_import`: retained materials remain searchable/openable according to active/archive state, user tag corrections survive, affected material/source-segment ids remain visible, and diagnostics stay redacted.
- Failed embedding and cancelled embedding rebuilds now cover missing-source downgrade to `failed_import`: retained material detail stays openable, slot ids remain visible in processing detail, archive/user-correction state survives, and diagnostics stay redacted.
- Cancelled embedding explicit rebuild now preserves affected slot drill-down even when the only slotted material is archived: active search/vector indexing still ignore archived rows, but processing records can open the retained archived material detail.

### 2026-07-08

- Phase 15 mock browser coverage now includes focused compact and stress slices for import, style samples, narrative pattern extraction, Git history, settings, update checks, and visible error feedback. The `test:phase15` workflow writes screenshots, traces, bridge-call logs, diagnostics, and stress metrics under `output/playwright/phase15/`.
- EPUB parsing now enforces a cumulative uncompressed expansion limit in addition to compressed archive and per-entry limits. Oversized EPUB expansion fails with structured `import.epub.expanded_too_large` diagnostics instead of reading unbounded archive content.
- Backend regression coverage now includes invalid ZIP files, compressed EPUB parser limits, import storage failure before workspace creation, narrative-pattern retry exhaustion, Git binary diffs, large diff truncation, and all tracked Git lock files.
- Git history remains backed by LibGit2Sharp/libgit2 runtime assets instead of a local `git.exe`; tests cover empty repositories, invalid metadata, lock-file refusal, binary/large diff behavior, rename/delete handling, and configured Git author identity.
- Desktop window persistence now captures native Photino bounds through `runtime.window.getBounds` and restores x/y/width/height/maximized state with monitor work-area clamping to avoid off-screen launches.
- The app-wide mock workflow entry remains small while Git history fixtures are split into `mock-git-service.mjs`; the mock bridge also covers native-window bounds for layout tests.
- Visible error feedback now preserves copyable diagnostics across unrelated create/edit actions, dialog reopening, copy-feedback rerenders, and ordinary editor edits; legacy metadata, novel, style, skill, and editor surfaces clear errors only on retry/success or explicit close.
- Phase 15 user documentation now describes supported import formats, style material samples, narrative pattern extraction, read-only Git history, update checks, configurable Git authors, and the remaining rule that `goink-master` is only a read-only legacy behavior reference.
- Latest verification run for this slice: `npm --prefix frontend run lint`, `npm --prefix frontend run test:layout`, `npm --prefix frontend run test:layout-ui`, `npm --prefix frontend run test:git`, `npm --prefix frontend run test:error-ui`, `npm --prefix frontend run test:reference-anchor`, `npm --prefix frontend run test:app`, `npm --prefix frontend run test:app:full`, `npm --prefix frontend run test:app:stress`, `npm --prefix frontend run test:app:usability`, `npm --prefix frontend run test:phase15`, `npm --prefix frontend run build` with `ESBUILD_WORKER_THREADS=0`, targeted Photino bridge/desktop integration tests, targeted Git/import integration tests, and `dotnet test Novelist.slnx --no-restore -v minimal`.

### 2026-07-07

- 书架现在提供小说导入入口，支持通过桌面文件选择或拖放本地 EPUB/TXT/Markdown 文件启动导入；拖放文件夹、URL、空内容和不支持格式时会直接显示反馈，且导入路径只会进入小说导入流程。
- 桌面更新检查的发布端点现在由应用构建配置或启动参数提供，默认不内置 live 发布地址且自动检查保持关闭，便于后续更新检查在测试和发布包中使用明确配置。
- 参考锚定默认编排现在可直接选择 active 风格画像，调整模仿强度、最低拟合、接近度、证据类型和禁止风险，并把该策略写入待审批蓝图的 `style_contract`。
- 参考锚定编排遇到材料缺口、检索/溯源缺口或 source-leak 风险时，现在会在高风险停点显示具体恢复动作，例如补充/恢复材料、放宽检索过滤、重绑更强材料、降低模仿强度或重新生成候选。
- 参考锚定审计现在会额外拦截整体候选/来源相似度过高的改写；审计报告只显示相似度比例和阈值，不回显来源正文或候选正文。
- 参考锚定草稿审计现在会按模仿强度检查泛化风格质量风险，包括抽象总结、情绪直说、机械连接/三连结构、套话公式和综合泛化密度；强模仿模式更严格，报告只显示风险分类、计数和处理动作，不回显候选正文或来源正文。
- 参考风格画像新增 10MB 浏览器压力回归，覆盖大语料画像构建进度、模型分析阶段状态、画像详情检查、材料库分页检索、截图和诊断指标。
- 风格画像库界面现在会显示画像构建的构建 ID、阶段进度、诊断和错误状态，支持取消运行中的构建，并可在失败或取消后手动刷新检查状态。
- 参考风格画像构建现在会记录可恢复的构建状态，支持查看阶段进度、取消运行中的构建、在失败或取消后继续检查结果，并且状态记录不保存来源正文、候选正文、Prompt 或文件路径。
- Agent 参考锚定工具现在明确禁止批准带 `style_contract` 的蓝图；AI 仍可提出和评审风格合约修订，但风格合约审批必须走用户确认路径。
- 参考锚定的 style/source-leak 审计发现现在可通过桌面桥接和 Agent 单独只读查看，支持按候选和风险类型过滤，只返回风险、候选 ID、说明和处理动作，不返回候选正文、来源正文、Prompt、路径或写章节能力。
- 参考锚定草稿审计现在会拦截低整体重合但夹带长段来源原句的候选；审计报告只显示复用长度和比例，不回显被复制的来源句子。
- 参考风格锚定新增黄金评估套件，可覆盖对白密集、内省、感官、动作、悬疑、情绪克制、高节奏网文和慢热文学八类风格，并生成不含来源正文或候选正文的评估报告。
- 参考锚定草稿审计现在会返回可读报告，显示候选 ID、问题分类、严重级别和处理动作，方便在候选段落区直接判断是否可继续使用。
- 生成草稿和手动审计都会把审计报告与候选 ID 一起持久化；审计报告不会额外保存候选正文、来源正文或 Prompt。
- 参考锚定前端会展示草稿审计摘要和结构化发现，浏览器回归流程已覆盖通过与失败审计的报告展示。
- 已持久化的参考锚定草稿审计报告现在可通过桌面桥接和 Agent 只读查看，支持按蓝图、候选 ID 和条数限制检索，但不会返回候选正文、来源正文、Prompt、路径或写章节能力。

### 2026-07-06

- 项目 README、仓库链接和许可证来源说明已切换到 Novelist，明确当前 MIT 发布边界、来源致谢关系，以及旧实现已经退役。
- 参考锚定编排在进入自动化前会明确要求确认来源、授权状态、已知事实和禁止事实；Phase 11 的自动化边界已收敛，高风险停点和最终正文插入仍由作者确认。
- 启动检查失败时会显示明确的恢复界面和重试按钮；若不是通过 Novelist 桌面应用打开，也会提示桌面桥接不可用，而不是误进入初始化流程。
- `npm --prefix frontend run test:app` 现在覆盖首次初始化、初始化后的空书架、已初始化但无作品、启动检查失败和桌面桥接不可用等 bootstrap 状态。
- 正文编辑器现在使用本地打包的 Monaco 资源，桌面或 CI 离线环境不再卡在编辑器 Loading；保存失败会在编辑区显示明确提示并保留未保存状态。
- `npm --prefix frontend run test:app` 现在覆盖章节正文编辑、显式保存、保存失败提示、脏状态切换，以及干净状态切换面板不会额外写入正文。
- `npm --prefix frontend run test:app` 现在覆盖模型与 Embeddings 设置的必填校验、mock 桥接保存和内置 ONNX 向量配置路径，全程不需要真实 API Key、本地模型文件或网络访问。
- `npm --prefix frontend run test:app` 现在会显式遍历书架、章节/编辑器、聊天面板、搜索、参考锚定、角色、地点、弧线、时间线、偏好、读者视角、技能、个人中心、帮助和设置入口。
- `npm --prefix frontend run test:app` 现在覆盖小说创建/编辑/选择、章节创建/重命名、多章节标签切换和侧栏选中同步；切换已打开的章节标签时，章节侧栏会跟随当前正文。
- `npm --prefix frontend run test:app` 现在覆盖导出、封面/头像上传、参考源文件选择和参考锚点创建的 mocked 文件路径；重复打开导出对话时不再沿用上次成功状态。
- 本地 ONNX embedding 和 sqlite-vec 现在随 .NET 发布依赖一起打包，并用真实本地 BGE 模型验证向量生成；ONNX 模式不再依赖反射加载或手工放置运行时 DLL。
- 参考锚定语料库条目现在支持归档工作区共享语料：归档后条目会从常规列表和检索中隐藏，同时保留来源片段、材料和哈希溯源；普通单小说参考锚点仍按删除处理。
- 参考锚定的语料库管理区新增独立“材料库”检索面板，可不依赖选中锚点直接搜索可访问语料材料、查看评分解释，并对选中材料批量校正标签。
- 参考锚定的材料库结果现在可在当前页按材料 ID、文本或标签筛选，并按最高分或材料 ID 排序；批量校正可只作用于当前可见结果。
- 参考锚定的材料库批量校正现在会在翻页时保留已选材料，可跨多个结果页挑选材料后一次校正标签。
- 参考锚定的材料库现在支持归档所选材料；归档会从常规检索、改写和标签校正路径隐藏材料，同时保留来源片段、材料行和历史审计溯源。
- 参考锚定的材料库现在可以切换查看已归档材料，并恢复选中的已归档材料；恢复后材料会重新进入默认检索、改写和审计路径。
- 参考锚定的语料行材料浏览器现在可在当前页按材料 ID、文本、标签、类型或来源片段筛选，并按最高分或材料 ID 排序，便于检查单个语料条目的材料质量。
- 参考锚定的语料行材料浏览器现在支持选中当前页材料后批量校正功能、情绪、场景、POV 和技法标签；批量保存会作为事务处理，失败时不会留下半更新状态。
- 参考锚定语料库条目列表现在支持对选中项批量提升为工作区语料，或事务性批量归档已在工作区的语料行，并会在操作后清除已处理选择。
- 参考锚定的批量提升和批量归档现在都走事务性桌面桥接能力，能一次处理多个选中条目；提升会保持材料 ID、来源片段和材料检索可复用性，归档会在隐藏共享条目的同时保留溯源材料。
- 参考锚定现在会在打开旧库时自动整理已标记为工作区可见但仍挂在单本小说下的语料行，让它们成为真正的工作区共享语料；私有和受限语料仍保持原小说隔离。
- 参考锚定默认编排现在和 Agent 保持一致，只自动检索 `user_provided` 授权语料；未知授权语料需要在高级检索策略中显式允许。
- 参考锚定语料导入面板新增批量来源路径导入，可一次提交多条来源并沿用单条导入的授权、可见性、标签和工作区语料所有权规则。
- 参考锚定语料导入面板现在支持 JSON 库包清单导入，可从 `sources` 条目批量展开来源路径、标题、作者、授权、可见性、来源可信度和标签，并复用现有批量导入桥接。

### 2026-07-05

- 参考锚定页新增默认编排入口，可填写章节目标、已知事实和禁止事实后一键启动低干预候选运行；限制到已选锚点现在只是高级选项。
- 参考锚定页现在可直接查看、继续和取消编排运行，并显示当前停止原因、审批摘要、候选 ID 与事件历史；最终正文插入仍不会自动执行。
- Agent 现在可以启动、查看、读取事件历史和取消参考锚定编排流程；来源/事实确认、蓝图修订批准和最终正文插入仍必须由作者确认。
- 参考锚定编排到达最终插入停点后会保留候选 ID，但不能通过继续编排直接完成插入；正文仍需走单独的人工章节编辑/保存流程。
- 参考锚定编排的材料绑定现在会应用语料检索策略中的包含/排除锚点和授权状态过滤，默认流程不再依赖预先手动指定锚点列表。
- 参考材料检索现在可读取工作区级共享语料锚点，同时继续隔离其他小说的私有参考；使用共享材料产生的反馈仍记录在当前小说下。
- 参考锚定编排现在会记录本地运行事件历史，可查看流程为何停止、AI 提议了什么、用户确认或取消了什么，以及哪个确定性关卡产生了阻断。
- 参考锚定编排中的草稿审计失败会停在可检查的高风险决策点，保留候选和审计问题供处理，不会直接进入正文插入。
- 参考锚定编排在材料绑定缺少必需 beat 的选中材料时会停在高风险决策点，提示补充/选择参考材料或调整蓝图策略后再重跑。
- 参考锚定编排在待批准蓝图因章节计划变化而失效时会停在高风险决策点，要求重新生成/评审蓝图后再继续。
- 新增 `npm --prefix frontend run test:reference-anchor`，用真实浏览器和 mocked Photino bridge 覆盖参考锚定前端完整工作流，并验证不会自动调用 `SaveContent` 写入正文。
- 新增 `npm --prefix frontend run test:app`，用真实浏览器和 mocked Photino bridge 覆盖工作区、章节、搜索、聊天、设置、元数据面板和参考锚定入口的前端回归烟测。
- 新增 `npm --prefix frontend run verify`，可在无 Photino 桌面壳的 CI 环境中一次运行前端 build、lint、参考锚定深度流程和 app-wide 回归烟测。
- 全局搜索现在会区分“无搜索结果”和“搜索失败”，失败时可直接重试，不再把桥接/索引异常误显示成空结果。
- 地点侧栏的展开与删除控件现在使用有效的按钮结构，避免浏览器控制台报错并改善键盘/辅助技术交互。
