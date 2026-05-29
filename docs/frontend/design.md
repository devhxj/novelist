# Goink 前端设计文档

## 一、技术选型

| 项 | 选择 | 说明 |
|---|---|---|
| 框架 | React 19 + TypeScript | 生态最强，shadcn/ui 原生支持 |
| UI 组件库 | shadcn/ui | 代码级复用，非 npm 依赖，风格完全受控 |
| 样式方案 | Tailwind CSS v4 | 原子化 CSS，AI 生成质量最高 |
| 构建工具 | Vite | Wails v2 官方推荐 |
| 编辑器 | Monaco Editor (`@monaco-editor/react`) | 与 Python 版一致 |
| 翻书动画 | react-pageflip | 成熟稳定，Canvas 渲染，支持 HTML 内容 |
| 包管理 | npm | 项目已用 npm，不换 pnpm |

## 二、设计哲学

### 桌面原生感，而非 Web 感

- 无 URL 路由——视图切换通过 React 状态实现，无浏览器地址栏
- 无侧边栏——全局导航通过窗口标题栏区域 + 页面内嵌返回按钮
- 页面间过渡用动画（翻书、横推、淡入），不是 web 的硬切
- 浅色调，干净留白

### 状态驱动，非路由驱动

```
App.tsx 维护一个 view 状态机：

view = "init" | "novel-list" | "novel-hub" | "editor" | ...

每个 view 对应一个顶层组件，切换 view 即切换组件。
选中的小说 ID 作为上下文参数传入子 view。
```

### 骨架-插槽模式

顶层不变的壳（窗口框架）包裹可变的内容区。新功能只需注册一个新的 view + 在对应入口添一个触发点。

## 三、视图流转

```
┌─────────────────────────────────────────────────────┐
│                                                     │
│  App 启动                                           │
│    │                                                │
│    ├─ IsInitialized() = false                       │
│    │     └─ InitView（选择数据目录）                  │
│    │                                                │
│    └─ IsInitialized() = true                        │
│          └─ NovelListView（首页）                    │
│               │                                     │
│               │ 点击小说卡片                          │
│               │ ──翻书动画──                         │
│               ▼                                     │
│          NovelHubView（小说功能菜单）                 │
│               │                                     │
│               ├─ 创作工坊 ──→ EditorView（三栏布局）  │
│               ├─ 角色管理 ──→ 后期实现               │
│               ├─ 地点管理 ──→ 后期实现               │
│               └─ ← 返回 ──→ NovelListView           │
│                                                     │
└─────────────────────────────────────────────────────┘
```

## 四、各视图详设

### 4.1 InitView（初始化页） 

**触发条件**：`App.IsInitialized()` 返回 `false`

**数据获取**：`App.GetPlatform()` 返回 OS 类型 + 默认数据目录路径
- Windows 后端检测 D/E 盘可用性，首选 `D:\Goink`
- macOS/Linux 默认 `~/.goink`

**布局**：居中卡片式，输入框预填后端返回的默认路径（非 placeholder），用户可直接修改

```
┌──────────────────────────────────────┐
│            [Goink Logo]              │
│         欢迎使用 Goink               │
│    请选择数据存储位置...              │
│                                      │
│    ┌──────────────────────┐          │
│    │  D:\Goink            │          │  ← 已预填默认值
│    └──────────────────────┘          │
│                                      │
│    配置与数据存储在同一目录            │
│                                      │
│         [ 开始使用 ]                  │
└──────────────────────────────────────┘
```

- 输入框已有平台默认路径，用户可直接确认或修改
- 点击「开始使用」→ `App.Initialize(dataDir)` → `config.Save()` 自动展开 `~` 为绝对路径 → 切换到 NovelListView

### 4.2 NovelListView（首页，小说列表）

**布局**：极简，无侧边栏

```
┌────────────────────────────────────────────┐
│ Goink                          [设置图标]  │  ← 顶部栏（可融入标题栏）
│                                            │
│                                            │
│     ┌──────┐  ┌──────┐  ┌──────┐         │
│     │      │  │      │  │      │         │
│     │ 封面  │  │ 封面  │  │ 封面  │  +新建  │
│     │      │  │      │  │      │         │
│     └──────┘  └──────┘  └──────┘         │
│     剑来      诡秘之主    道诡异仙           │
│                                            │
│                                            │
└────────────────────────────────────────────┘
```

- 顶部标题栏融入窗口（Wails 无边框窗口 + 自定义标题栏）
- 右上角：GitHub 图标链接 + 设置图标（齿轮），Windows 原生标题栏按钮（最小化/最大化/关闭）
- 中央：小说卡片网格，响应式排列（3-5 列）
- 封面：默认 SVG（书籍轮廓），后续支持自定义图片
- 底部：书名
- 末位卡片：「+」创建新小说
- 点击卡片：触发翻书动画，动画结束后进入 NovelHubView

### 4.3 NovelHubView（小说功能菜单）

**布局**：类似游戏主菜单，中央几张大卡片

```
┌────────────────────────────────────────────┐
│ ← 返回           剑来               [设置]  │
│                                            │
│                                            │
│         ┌──────────┐  ┌──────────┐        │
│         │  ✍️      │  │  👤      │        │
│         │ 创作工坊  │  │ 角色管理  │        │
│         └──────────┘  └──────────┘        │
│                                            │
│         ┌──────────┐  ┌──────────┐        │
│         │  📍      │  │  📖      │        │
│         │ 地点管理  │  │ 章节目录  │        │
│         └──────────┘  └──────────┘        │
│                                            │
│    《剑来》— 仙侠小说，连载中...              │
│                                            │
└────────────────────────────────────────────┘
```

- 左上角：← 返回按钮（回到 NovelListView）
- 中央：2×2 或 2×3 大卡片网格，每个卡片有图标 + 标签
- 「创作工坊」突出显示（主色调，最常用）
- 后期功能用灰色调 + 「即将推出」标签
- 底部：小说简介
- 卡片入场动画：微弹入（scale 0.95 → 1，带缓动）

### 4.4 EditorView（创作工坊，三栏布局）

**布局**：全屏三栏，无侧边栏

```
┌──────────────────────────────────────────────────────────┐
│ ← Hub   剑来·第3章·初遇         大纲/正文切换    [设置]   │  ← 工具栏
├────────────┬────────────────────────┬────────────────────┤
│ 章节列表    │                        │  AI 对话区          │
│            │                        │                    │
│ 第1章 ✓   │     Monaco 编辑器       │  ┌──────────────┐ │
│ 第2章 ✓   │                        │  │ 消息流        │ │
│ 第3章 ●   │     （章节正文/大纲）     │  │              │ │
│ 第4章     │                        │  │ AI: 建议...   │ │
│            │                        │  │              │ │
│ [+新章]   │                        │  └──────────────┘ │
│            │                        │  ┌──────────────┐ │
│            │                        │  │ 输入框    [→] │ │
│            │                        │  └──────────────┘ │
├────────────┴────────────────────────┴────────────────────┤
│ 底部状态栏：字数 2340 | 修改于 10:30 | 已保存             │
└──────────────────────────────────────────────────────────┘
```

**工具栏**：
- 左侧：← 返回 Hub 按钮 + 小说名·章节名
- 中间：大纲 / 正文 切换（Tab 式）
- 右侧：设置图标

**左面板（宽 220px）**：
- 章节列表（滚动）
- 每项：标题 + 完成状态 ✓
- 当前选中章节高亮
- 「+ 新章节」按钮
- 未来在此面板底部加 Tab：章节 | 大纲 | 角色关系 | 伏笔

**中间面板（自适应）**：
- Monaco 编辑器（`@monaco-editor/react`）
- 支持纯文本和 Markdown 高亮
- 大纲模式：显示章节大纲层级结构

**右面板（宽 360px）**：
- Chat 聊天区
- 消息流（AI 回复流式渲染）
- 底部输入框（支持 Shift+Enter 换行，Enter 发送）
- 未来加 Tab：聊天 | 审核 | 子 Agent

**底部状态栏（高 28px）**：
- 字数统计、修改时间、保存状态

### 4.5 后期视图（角色管理 / 地点管理）

MVP 不实现，但在 NovelHubView 中预留给入口。

**角色管理**：表格 + 详情面板 + vis-network 关系图
**地点管理**：表格 + 地图/关系图
**章节目录**：纯浏览模式，只读

## 五、全局 UI 元素

### 标题栏（融入窗口）

Wails v2 支持无边框窗口（`Frameless: true`）+ 自定义标题栏区域：

```
┌──────────────────────────────────────────────┐
│ [Goink]                      [⚙] [─] [□] [✕]│  ← 可拖拽区域
└──────────────────────────────────────────────┘
```

- 左侧：应用名
- 右侧：设置图标 + Windows 原生窗口按钮（macOS 使用红绿灯）
- 整行可拖拽移动窗口
- 高度 36px

### GitHub 链接

位于 NovelListView 顶部栏，设置按钮旁边：`github.com/sigpanic/goink`。外部链接图标(github 图标) + 悬浮显示内容提示。

### 浅色调色板

使用 shadcn/ui Nova preset 的 OKLCH 色彩体系，通过 CSS 变量定义。主色覆盖为蓝色（`--primary: oklch(0.546 0.245 262.881)`）。

## 六、目录结构

```
frontend/
├── index.html
├── package.json
├── tsconfig.json
├── tsconfig.app.json
├── vite.config.ts
│
└── src/
    ├── main.tsx                     # React 入口
    ├── App.tsx                      # 顶层：状态机，view 切换
    ├── index.css                    # 全局样式 + CSS 变量（色板）
    │
    ├── components/
    │   ├── ui/                      # shadcn/ui 组件（button, dialog, input...）
    │   │   └── ...                  # 按需复制，非 npm 包
    │   │
    │   ├── shell/
    │   │   ├── TitleBar.tsx         # 自定义标题栏（融入窗口）
    │   │   └── StatusBar.tsx        # 底部状态栏
    │   │
    │   ├── novel/
    │   │   ├── NovelCard.tsx        # 单张小说卡片
    │   │   ├── NovelGrid.tsx        # 小说卡片网格
    │   │   ├── BookCover.tsx        # 默认封面 SVG
    │   │   └── BookFlipTransition.tsx  # 翻书动画包装器
    │   │
    │   ├── hub/
    │   │   ├── HubCard.tsx          # 功能入口卡片
    │   │   └── HubGrid.tsx          # 功能卡片网格
    │   │
    │   ├── editor/
    │   │   ├── EditorToolbar.tsx    # 工具栏（返回 + 章节名 + 大纲切换）
    │   │   ├── ChapterPanel.tsx     # 左侧章节列表
    │   │   ├── MonacoEditor.tsx     # Monaco 编辑器封装
    │   │   └── OutlinePanel.tsx     # 大纲视图
    │   │
    │   ├── chat/
    │   │   ├── ChatPanel.tsx        # 聊天区容器
    │   │   ├── MessageList.tsx      # 消息流
    │   │   ├── MessageBubble.tsx    # 单条消息
    │   │   └── ChatInput.tsx        # 底部输入框
    │   │
    │   └── shared/
    │       ├── BackButton.tsx       # ← 返回按钮
    │       └── GithubLink.tsx       # GitHub 图标链接
    │
    ├── views/
    │   ├── InitView.tsx             # 初始化页
    │   ├── NovelListView.tsx        # 首页（小说列表）
    │   ├── NovelHubView.tsx         # 小说功能菜单
    │   └── EditorView.tsx           # 创作工坊（三栏布局）
    │
    ├── hooks/
    │   ├── useApp.ts               # Wails IPC 调用封装
    │   └── useKeyboard.ts          # 快捷键管理
    │
    └── lib/
        ├── wailsjs/                 # Wails 自动生成的 API 绑定 + 类型（须 commit）
        └── utils.ts
```

## 七、数据流

所有后端交互走 Wails IPC，无 HTTP。

```
前端                          Go 后端
────                          ──────
App.GetPlatform()        →    (*App).GetPlatform()
App.IsInitialized()      →    (*App).IsInitialized()
App.Initialize(dir)      →    (*App).Initialize(dir)
App.GetNovels()          →    (*App).GetNovels()
App.CreateNovel(...)     →    (*App).CreateNovel(...)
App.GetChapters(id)      →    (*App).GetChapters(id)
App.GetSettings()        →    (*App).GetSettings()
App.SaveSettings(...)    →    (*App).SaveSettings(...)
App.Chat(session, msg)   →    (*App).Chat(session, msg)
...
```

- 前端调用是 Promise（Wails v2 自动处理）
- 无 axios、无 fetch、无 WebSocket
- `hooks/useApp.ts` 封装常用调用，组件不直接写 `window.go.main.App.xxx()`

## 八、动画规范

| 场景 | 动画 | 时长 | 缓动 |
|---|---|---|---|
| 翻书进入小说 | react-pageflip Canvas 翻页 | ~600ms | 默认物理曲线 |
| Hub 卡片入场 | scale(0.95→1) + fadeIn | 300ms | ease-out |
| Hub → Editor | 从中央展开到全屏 | 400ms | ease-in-out |
| Editor → Hub | 从全屏收回到中央 | 300ms | ease-in |
| 页面间切换 | fadeIn | 200ms | ease |
| 消息气泡出现 | slideUp + fadeIn | 250ms | ease-out |

## 九、实施阶段

| 阶段 | 内容 | 依赖 |
|---|---|---|
| **P0（MVP）** | InitView + NovelListView + BookFlipTransition | App.IsInitialized, App.Initialize, App.GetNovels, App.CreateNovel |
| **P0（MVP）** | NovelHubView + EditorView 骨架（三栏布局 + Monaco） | App.GetChapters |
| **P0（MVP）** | ChatPanel（接收 App.Chat 返回值，流式后续） | App.Chat 占位 |
| **P1** | 大纲切换、章节内容读写 | App.SaveChapter 等 |
| **P1** | 流式 Chat（SSE → Wails Events） | agent loop |
| **P2** | 角色/地点管理、关系图 | 角色 CRUD + vis-network |
| **P2** | 自定义小说封面 | 文件选择 + 本地存储 |

## 十、开发须知

- **构建**: `make dev`（等同于 `wails dev -tags webkit2_41`），前端仅构建：`npm --prefix frontend run build`
- **浏览器调试**: `wails dev` 启动后用 `http://localhost:34115` 在外部浏览器打开，Go IPC 通过 Wails 代理正常通信
- **类型定义**: Go 侧定义 input struct（入参）和 domain model（出参），Wails 自动生成 TypeScript 类型。前端直接使用 `lib/wailsjs/go/models.ts` 中的类型，API 契约以 Go 方法签名为准
- **Tailwind v4**: 无 `tailwind.config.ts`/`postcss.config.js`，通过 `@tailwindcss/vite` 插件配置
- **shadcn 组件**: 按需通过 `npx shadcn add <component>` 添加到 `components/ui/`

## 十一、与 Python 版前端的差异

| Python 版 | Goink 版 |
|---|---|
| Web 应用（URL 路由） | 桌面应用（状态切换） |
| 登录/注册/JWT | 无（单用户） |
| Axios HTTP 请求 | Wails IPC 绑定调用 |
| Ant Design 组件 | shadcn/ui + Tailwind |
| React Router 路由 | App.tsx 状态机 |
| 左侧全局侧边栏 | 无侧边栏，Hub 页中央菜单 |
| WebSocket 流式 | Wails Events 流式 |
| stores/ + services/ | hooks/ + 直接 IPC |
| 暗色调 | 浅色调 |
