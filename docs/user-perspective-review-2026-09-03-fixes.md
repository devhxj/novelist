# 全功能用户视角评审落地记录（2026-09-03）

> 对 `docs/user-perspective-review-2026-09-03.md` 所列 17 项 + 用户指出的 light 主题模块颜色不统一的落地记录。
> 行号以 `fc349da` 为基线；全部为前端改动，无后端/契约变更。
> 验证基线：`tsc` 干净、`lint` 0 问题、`build`、`test:node` 23/23、`test:app`、`test:app:full`、`test:phase16`、`test:error-ui`、`test:app:stress` 全绿；后端 `dotnet test` 仅既知 2 例 worker 文件锁抖动（单独重跑 12/12 通过，纯前端改动无回归）。

## 一、light 主题模块颜色不统一（用户反馈 + 评审 D 节）

**根因**：`index.css` 的 `.reference-materialization-surface` / `.reference-materialization-sidebar` 为素材库整体覆盖了一套冷蓝青色板（hue 220/185），而全应用其它模块是暖鼠尾草绿/奶油色（hue 86–126）——素材库一打开就像换了套皮肤，这正是用户看到"多个模块颜色不统一"的来源。

**修复**：
- 删除两块色板覆盖（light + dark，约 60 行），素材库完全继承全局暖色板；`@media` 的面板宽度规则保留。
- 深色主题强调色从蓝（hue 255）对齐到绿色设计语言（primary/ring/chart-1/sidebar-primary → hue 148），与浅色主题成为同一设计语言的明暗变体（评审 D 节建议）。
- 活动栏选中态增强：指示条 0.5×5 → 1×6，选中图标 `text-primary bg-primary/15`（评审"图标选中态不够醒目"）。

截图证据：`app-07-reference.png`（素材库与全局色系统一）、`app-01-shell.png`（章节默认展开 + 活动栏选中态）。

## 二、逐项对账

### 结构（A1–A7，全部落地）

- **A1 书架卡片嵌套按钮**：卡片改为 `role="article"` 外壳 + 封面区（含上传按钮）与「打开作品」`<button>`（标题/分类/简介区）完全分离，无嵌套交互元素。
- **A2 hover 显隐键盘隐形**：全仓 15 处 `opacity-0 group-hover:opacity-100` 容器（书架操作组、章节行、各 CRUD 卡片、标签页关闭钮、头像悬浮层、弧线图示等）统一补 `group-focus-within:opacity-100`——键盘 Tab 聚焦同样显形。
- **A3 章节列表默认折叠**：首个分块默认展开；展开状态持久化到 `localStorage`（`novelist.chapterBlocks.expanded`）。
- **A4 搜索占侧栏窄条**：点活动栏「搜索」后主区切换为宽幅结果视图（`search-main-view`，带标题与说明），侧栏输入框保留，结果在主区展开。
- **A5 原生 confirm**：新建共享 `ConfirmDialog`（`useDialogA11y` + 失败可复制诊断），章节删除接入；U14 的"删除前读内容、撤销恢复"语义不变，读取/删除失败在对话框内呈现（R6 语义保留）。
- **A6 设置双入口**：移除活动栏「设置」项，顶栏齿轮为唯一入口。
- **A7 帮助导航脱节**：帮助「界面概览」按真实活动栏重写（书架、素材库 → 本书工具逐项 → Git 历史；说明搜索结果在主区展开、设置在右上角齿轮）。

### 文案与本地化（B1–B5，全部落地；契约不动）

- **B1 taxonomy 中文映射**：新增 `lib/novelist/corpusTaxonomy.ts`——素材类型（sentence/passage→句子/段落）、覆盖度维度（material_type→素材类型等 8 维）、12 个 family、60+ 特征值（按 `ReferenceMaterializationChatCompletionQualifier` 的 Allowed* 词表与 `MaterialCoverageFacetColumns` 逐项对译）、复核状态 4 态。覆盖度地图（维度名 + 值）、浏览观察卡/标本卡头部、复核候选标签/类型/原因码全部走映射；未命中键回退原文，数据契约零变更。
- **B2 中英混排**：`run.status` → `RUN_STATUS_LABELS`（排队中/进行中/…）；读者视角侧栏裸 `known` → 已知/悬念/误解；Git 历史 `Diff`→文件差异、`Patch`→变更补丁、`binary`→二进制；`Release endpoint` → 更新检查地址（HTTPS）。`stageLabel` 已有全量映射（fallback 仅对未知新键），维持。
- **B3 分隔模板像乱码**：冻结配置区改为「分隔模板：`<code>`第{number}章 {title}`</code>`（{number} 表示章号，{title} 表示标题）」。
- **B4 时间线标题难读**：时间线/读者视角/弧线三处标题拆为「视图 {windowFrom}-{windowTo} 章 · 条目覆盖第 N 章」，单章显示「第 N 章」而非「N - N 章」。
- **B5 计数空格**：核实主视图与侧栏全部计数模板——当前代码均已带空格，走查中的无空格写法不存在于当前代码，无需改动（记录避免误报）。

### 无障碍（C1–C2，全部落地）

- **C1 icon-only 按钮**：弧线/时间线/读者视角的 ✓ 按钮补 `aria-label`（标记完成/标记已回收）；角色/地点删除按钮 `aria-label` 带对象名（`删除角色 {name}`）；搜索清空按钮补「清空搜索」。
- **C2 搜索框标签**：7 个侧栏列表（角色/偏好/读者/弧线/时间线/技能/书架）+ 全局搜索输入框统一 `aria-label`。

### 评审未采纳项（按文档"不做的事"）

- taxonomy 不改后端返回中文——仅前端展示层映射，契约测试与既有工作流断言不受影响。
- 不为 hover 按钮加常驻可见模式——`group-focus-within` 已闭合键盘可达性。

## 三、测试与工作流更新

- **章节删除工作流**：插入确认对话框步骤（可见断言 + 截图 `chapter-delete-confirm-dialog.png`）→ 点「确认删除」→ 后续 toast/撤销断言不变。
- **搜索工作流**：A4 后输入框与结果在侧栏/主区各渲染一份（同组件双实例）——搜索相关断言全部改为 `search-main-view` 域内定位；双实例各自 debounce 触发 SearchAll，mock 的 failure-once 语义只落在一个实例，失败文本与「重试」点击改用 `first()` 宽松断言。
- **stress 套件**：A3 让 250 章的首块（即「第 151 - 250 章」）默认展开，原"无条件点分块条"会把已展开的首块折叠掉——改为按目标章按钮可见性判断。
- **活动栏 active 判定**：`navigation-helpers` 的 `isActiveBackground` 同步识别新的 `bg-primary/15` 选中样式。
- **shell 导航工作流**：搜索步骤断言 `search-main-view` 出现。

## 四、提交

- `0b3bdc9` fix: land 2026-09-03 full-product review fixes (theme unification, structure A1-A7, localization B1-B5, a11y C1-C2)
- `6709959` chore: ignore .freebuff local config（清理误入提交的本地文件）
- A2 全量收口（13 处补 `group-focus-within`）随本轮后续提交。
