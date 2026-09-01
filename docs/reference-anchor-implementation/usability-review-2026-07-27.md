# 参考书籍功能易用性与代码评审（2026-07-27）

> 评审范围：以 `ReferenceBookSidebar.tsx`（左栏书库管理）为核心，覆盖注册 / 列表 / 选择 / 删除 / 归档的前后端链路、mock workflow、契约和测试。结论基于代码走查 + `output/playwright/phase13/full-vite-reference-workspace/` 真实界面截图。
>
> 分支：`feature/whole-chapter-materialization`。整章素材化重构后的首次系统性走查。

## 一、严重问题（建议在合并 / 真实用户走查前修）

### 🔴 P0-1　「归档」实为永久删除（数据丢失 + mock 不一致）

**位置**：`frontend/src/components/reference-anchor/ReferenceBookSidebar.tsx:385-424`；`src/Novelist.Infrastructure/App/SqliteReferenceAnchorService.cs:181-202, 517-546`；`frontend/scripts/app-mock-workflow/mock-bridge.mjs:3050-3066`。

**现象**：工作区语料（`owner_scope === 'workspace_corpus'`）的书，UI 按钮、确认文案、`aria-label` 全部写「归档为受限语料」；但真实后端 `DeleteAnchorAsync → DeleteAnchorRowsAsync` 对 `owner_scope` **完全不区分**，无条件级联删除 11 张表的全部数据（素材、向量、运行、章节进度、租约、license、library member、anchor 本身）。mock 却把同一调用实现成软归档（`visibility: 'restricted'`，书仍在列表）。

**后果**：
- 用户点「归档」，预期是改 visibility，实际整本书连同所有素材化产物被物理删除，不可恢复。
- mock workflow（`surface-workflows.mjs` 归档断言）是绿的，但断言的是 mock 行为，与真实后端相反 —— 测试给了虚假安全感。
- 违反 AGENTS.md「用户数据迁移必须保留源数据」的精神。

**建议**：三端（前端契约 / 后端实现 / mock）统一为同一种语义。
- 方案 A（推荐）：后端新增 `ArchiveReferenceAnchor`，或让 `DeleteAnchorAsync` 对 workspace 语料走 `UPDATE … SET corpus_visibility='restricted'` 软路径；mock 已是该行为。
- 方案 B：UI 直接改文案为「删除」，去掉「归档」措辞；mock 也改成真删除。但放弃了「受限语料」产品概念。

无论哪种，`bridge-guardrails.mjs` 的归档断言必须校验真实契约（visibility 变化 vs. 行删除），而不是只断 `DeleteReferenceAnchor` 被调用。

### 🔴 P0-2　`anchorState.usable` 是死代码，失败 / 处理中的书可被选中预演

**位置**：`ReferenceBookSidebar.tsx:50-67, 143-155, 359-369`。

**现象**：`anchorState` 每个分支都返回 `usable: true`，包括 `failed_*` / `cancelled` / `queued` / `running` / `pending_split`。`toggleAnchor` 的 `if (!state.usable) return` 与按钮 `disabled={!state.usable}` 永不生效。

**后果**：用户可选中「失败」「处理中」「待切分」的书，右侧 `BlueprintPreviewPanel` 会拿它去调 `GenerateReferenceMaterializationBlueprintPreview`。虽然预演面板用 `ListReferenceMaterials(size:1)` 做了一次 ready 探针过滤，但：
- 失败态书若残留部分素材（whole-chapter 理论上原子提交，边界需确认 `PersistChapterAsync`），可能混入预演来源。
- 更主要是误导用户：列表里「失败」「可用」书选中态完全一样，没有任何提示「这本书现在不能用于预演，请先重跑材料化」。

**建议**：让 `usable` 真正反映可用性 —— 至少 `failed_*` / `cancelled` / `pending_split` / `queued` / `running` 应 `usable: false`，或在选中时给出明确提示。若产品上允许选中未 ready 的书（为去中部面板触发处理），则把「选中」和「可用于预演」两个概念分开，而不是用一个永不触发的守卫。

## 二、易用性问题

### 🟠 UX-1　错误信息硬编码，丢弃 `BridgeError.code`

**位置**：`ReferenceBookSidebar.tsx:119, 168, 206, 224`。

三个 catch 块都 `catch {`（无绑定），只 `setError('硬编码中文')`。对比同目录 `ReferenceMaterialWorkspace.tsx:64-73` 会检查 `BridgeError.code` 并 surface `${error.code}：${error.message}`。

后端能返回具体错误码（`materialization_source_changed` 文件被改动、`materialization_llm_not_configured` 未配模型、文件超 20MB、路径非法），但用户永远只看到「无法添加参考书籍，请检查文件路径后重试」这类笼统提示。

**建议**：catch 读取 `BridgeError`，对已知 code 给针对性中文提示，未知 code 退回 `error.message`。

### 🟠 UX-2　错误条无重试入口

**位置**：`ReferenceBookSidebar.tsx:331-336`。

错误条只是纯文本 `role="alert"`，无重试按钮。而中部面板允许重新点击动作。侧边栏加载 / 创建 / 删除失败后，用户要么手动点刷新，要么重新填表，体验割裂。

**建议**：错误条加「重试」按钮，重放最近一次失败动作。

### 🟠 UX-3　注册元数据全部硬编码，用户无法设置

**位置**：`ReferenceBookSidebar.tsx:186-196`。

固定传 `license_status: 'user_provided'`、`visibility: 'private'`、`source_trust: 'user_verified'`、`user_tags: []`。

- `visibility: 'private'` 意味着书只能被当前小说用，用户无法在添加时选「工作区共享」；但 UI 又有「归档为工作区语料」的反向操作 —— 两个产品概念冲突。
- `user_tags` 永远为空，但列表筛选（:137）和后端 `UpdateAnchorMetadataInput` 都支持 tag，前端无录入入口；`UpdateReferenceAnchorMetadata` 桥接方法 UI 完全没用。
- `license_status` 固定 `user_provided`，但后端 `MapLicenseStatus` 对 `public_domain` / `licensed` 有不同复用策略（verbatim_ratio 0.9 vs 0.42）。公有领域书无法标注，复用闸门偏严。

**建议**：添加表单补 visibility / license_status / tags 字段；或至少在书详情支持事后编辑（`UpdateReferenceAnchorMetadata` 后端 + 契约已就绪，只差 UI）。

### 🟠 UX-4　失败状态在侧边栏的文案过简

**位置**：`ReferenceBookSidebar.tsx:60-61`。

`failed_*` 一律显示「失败」，不区分「切分失败」「材料化失败」，也不显示具体原因（需滚到中部面板才能看到）。截图（`failed-reference-materialization.png`）确认：侧边栏唯一失败信号是红色「失败」二字，用户无法判断该做什么。

**建议**：title / hover 显示具体失败原因；或失败书行右侧加「查看详情」直达中部错误区。

### 🟠 UX-5　全局禁用 + 长列表的认知负担

**位置**：`ReferenceBookSidebar.tsx:242, 252, 310, 322, 362, 388, 407, 415`。

`activeAction !== null` 时全列表所有按钮 disabled。若用户在第 20 本书的删除确认态，第 3 本书的按钮也全灰，用户可能不知道发生了什么。

**建议**：给当前操作行加视觉高亮（已有 `pendingDeleteId` 的展开行），或用非阻塞 toast 提示「正在删除…」。

### 🟡 UX-6　三栏布局窄桌面下的信息密度

**截图确认**：左栏书库 + 中部材料化 + 右栏蓝图预演三栏并列。`tasks.md` 已把「1280×720 与窄桌面宽度截图验收」列为素材库工作台开放项。中部面板动作密集（分析 / 预览 / 确认 / 入队 / 运行全部 / 逐章运行 + 章节进度列表 + 章节材料弹窗），窄宽下易挤压。

**建议**：跑 `test:reference-workspace` 的 720p 截图后复核中部面板的可读性与按钮折行情况。

## 三、代码质量问题

### 🟡 CQ-1　`referenceAnchorStyles.ts` 是死代码

`frontend/src/components/reference-anchor/referenceAnchorStyles.ts` 导出的 `statusTone` / `inputClass` / `actionButtonClass` 与 `ReferenceBookSidebar.anchorState`（:50-67）、`ReferenceMaterialWorkspace.runTone`（:38-43）逻辑重复，且这三个组件都没 import 它。要么统一用它消除重复，要么删掉。

### 🟡 CQ-2　`stress-workflows.mjs` 已过时

`frontend/scripts/app-mock-workflow/stress-workflows.mjs` 的 `verifyStressReferenceMaterialPath` 引用 `data-testid="reference-material-library"`、`reference-blueprint-panel`、`GetReferenceAnchorBuildStatus`，这些在当前代码里都不存在。要么更新到新 workspace，要么删除并从 `suite-runners.mjs` 解绑。`npm run verify` 目前不含 stress，但留着是定时炸弹。

### 🟡 CQ-3　SafePath 逻辑三处重复，无统一工具

源文件校验（`Path.GetFullPath` + 扩展名白名单 + 20MB + SHA-256）在 `SqliteReferenceAnchorService.ValidateSourcePath` / `ReadSourceAsync`、`SqliteReferenceMaterializationService.ReadSourceFileAsync`、`SqliteReferenceMaterializationRunStore.Chapters.ReadFrozenSourceAsync` 重复三遍。AGENTS.md 强调「Preserve SafePath」，但这里没有共享 helper。任一处改限制（如调大 20MB、加新扩展名）其他不会同步。

**建议**：抽 `ReferenceSourceFileReader` 共享。

### 🟡 CQ-4　物理删除无回收站

即便修了 P0-1 的归档语义，普通「本小说」书的删除也是 11 表级联物理删除，无软删除、无回收。确认文案（:401）只说「确认删除这本参考书？」，未说明「将删除该书及其全部素材化产物」。

### 🟢 值得肯定

- 竞态防护：`loadSequenceRef` 防陈旧响应、`setTimeout(0)` 防同 tick 重复请求、`requestIdRef` race guard。
- 可访问性：`aria-busy` / `aria-pressed` / 动态 `aria-label` / `sr-only` / `role="alert"` / `aria-hidden` 装饰图标 —— 全仓标杆水平。
- 后端输出安全：`ReferencePayloadSanitizer.SanitizeAnchor` 归零 `source_path`、正则脱敏路径 / 密钥。
- 源文件 SHA-256 在 analyze / preview / confirm / enqueue / run 五阶段都重新校验，源变更检测严密。

## 四、验证建议（修复后补充）

1. **针对真实后端**（非 mock）的集成测试：注册一本 workspace 语料 → 调删除 → 断言行为符合「归档」语义（书仍在，visibility 变化），而不是只跑 mock workflow。
2. mock-bridge 的 `deleteReferenceAnchor` 加断言：对 workspace 语料**不得**触发 `DELETE FROM reference_anchors`（前提是后端真走软归档）。
3. 失败态书选中行为的 workflow：选中一本 `failed` 书，断言预演面板给出「该书不可用」提示而非静默调用。
4. 720p / 窄桌面截图复核三栏布局。

## 五、优先级总结

| 优先级 | 编号 | 问题 | 位置 |
|---|---|---|---|
| 🔴 P0 | P0-1 | 「归档」实为永久删除，mock 与真实后端行为相反 | sidebar:385 + SqliteReferenceAnchorService:517 |
| 🔴 P0 | P0-2 | `usable` 死代码，失败 / 处理中书可被选中预演 | sidebar:50-67, 143, 362 |
| 🟠 P1 | UX-1 | 错误信息硬编码，丢弃 BridgeError.code | sidebar:119/168/206/224 |
| 🟠 P1 | UX-2 | 错误条无重试按钮 | sidebar:331 |
| 🟠 P1 | UX-3 | 注册元数据全硬编码，无 visibility/tags/license 录入 | sidebar:186-196 |
| 🟠 P1 | UX-4 | 失败状态侧边栏文案过简 | sidebar:60-61 |
| 🟠 P1 | UX-5 | 全局禁用 + 长列表认知负担 | sidebar 全列表 disabled |
| 🟡 P2 | UX-6 | 三栏窄桌面信息密度待 720p 复核 | 中部面板 |
| 🟡 P2 | CQ-1 | referenceAnchorStyles.ts 死代码 | referenceAnchorStyles.ts |
| 🟡 P2 | CQ-2 | stress-workflows 引用退役 testid | stress-workflows.mjs |
| 🟡 P2 | CQ-3 | SafePath 三处重复 | 三个 ReadSource* |
| 🟡 P2 | CQ-4 | 物理删除无回收，确认文案过轻 | sidebar:401 |
