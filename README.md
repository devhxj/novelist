**中文** | [English](README_EN.md)

<div align="center">

![Today's Verse](https://v2.jinrishici.com/one.svg?font-size=20&spacing=2&color=Chocolate)
</div>

<p align="center">
  <img src="assets/logo-dark.svg#gh-dark-mode-only" alt="Novelist 参考锚定 AI 写作系统" />
  <img src="assets/logo-light.svg#gh-light-mode-only" alt="Novelist 参考锚定 AI 写作系统" />
</p>

<h1 align="center">Novelist 参考锚定 AI 长篇写作系统<br><sub>结构化记忆 × Skill 方法论 × 蓝图审查 × 草稿审计</sub></h1>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 10" />
  <img src="https://img.shields.io/badge/Photino.NET-Desktop-2E7D32?style=for-the-badge" alt="Photino.NET" />
  <img src="https://img.shields.io/badge/React-19-61DAFB?style=for-the-badge&logo=react&logoColor=white" alt="React 19" />
  <img src="https://img.shields.io/badge/SQLite-3-003B57?style=for-the-badge&logo=sqlite&logoColor=white" alt="SQLite" />
  <br />
  <img src="https://img.shields.io/badge/TypeScript-6.0-3178C6?style=for-the-badge&logo=typescript&logoColor=white" alt="TypeScript 6" />
  <img src="https://img.shields.io/badge/Tailwind-4.3-06B6D4?style=for-the-badge&logo=tailwindcss&logoColor=white" alt="Tailwind 4" />
  <img src="https://img.shields.io/badge/Agent_Framework-Microsoft-5E5CE6?style=for-the-badge" alt="Microsoft Agent Framework" />
  <img src="https://img.shields.io/badge/license-MIT-716B94?style=for-the-badge&logo=opensourceinitiative&logoColor=white" alt="MIT" />
</p>

---

Novelist 是从 GoInk 演进来的桌面 AI 写作系统。原有能力仍然是基础：结构化创作状态、Agent 工具调用、Skill 写作方法论、本地语义搜索、Diff 审批和 Git 历史。

现在的方向提高了一层：**Skill 负责教 AI 怎么写，但长篇创作还需要在写之前确认“什么能写”、在写之后证明“写出来的内容能不能用”。** 因此 Novelist 正在把参考资料、章节蓝图、材料绑定、候选草稿和审计结果做成可检查的契约，而不是只靠提示词或 Skill 约束模型。

## 当前定位

Novelist 不是单纯的聊天壳，也不是只堆 Prompt/Skill 的写作助手。它的目标是一个本地优先的长篇创作工作台：

| 层级 | 职责 |
|---|---|
| 结构化创作状态 | 管理角色、关系、伏笔、弧线、地点、读者认知、偏好和章节计划 |
| Agent 工具层 | 让 AI 在对话中主动查询、修改、维护项目状态，而不是只输出一段文本 |
| Skill 方法论层 | 提供场景节拍、对白潜台词、节奏控制、悬念钩子、修改打磨、去 AI 味等写作方法 |
| 参考锚定层 | 把参考资料转成可追溯材料，要求蓝图先过审，再绑定材料、生成候选、执行草稿审计 |
| 人工确认边界 | 正文插入、事实边界扩张、高风险修订和最终保存必须由作者确认 |

仓库名仍保留 `goink` 历史，但当前代码、文档和产品主线使用 `Novelist`。

## 为什么不只靠 Skill

Skill 能改善表达方式和写作流程，但它不能稳定解决这些问题：

- 参考资料是否可用、可引用、可改写；
- 生成内容是否越过已知事实和禁止事实边界；
- POV 是否泄漏了当前视角不该知道的信息；
- 章节蓝图是否只有镜头调度，没有因果、情绪、叙述距离和角色状态变化；
- 候选草稿是否来自已批准蓝图和已绑定材料；
- AI 是否绕过作者确认直接改正文。

参考锚定层补的是这部分硬约束。它把“写得像不像”“能不能借鉴”“有没有事实风险”“能不能插入正文”拆成结构化记录和确定性检查，让 AI 可以提议，但不能绕过门禁。

## 默认参考锚定工作流

```text
作者确认来源策略、章节目标、已知事实和禁止事实
  -> 启动 orchestration run
  -> 生成章节蓝图
  -> 执行确定性蓝图审查
  -> 作者批准蓝图，或批准 AI 提议的字段级修订
  -> 自动检索并绑定参考材料
  -> 生成 beat 级候选草稿
  -> 执行草稿审计
  -> 停在最终插入确认点
```

工作流会自动执行低风险的机械步骤，但会在这些位置停下：

- 来源、授权、事实边界需要确认；
- 蓝图过期、缺材料、弱检索或材料哈希不匹配；
- 蓝图审查失败，需要修订；
- 参考改写级别、POV、事实或审计结果有高风险；
- 最终正文插入。

这个流程不会自动调用 `SaveContent` 写章节正文。AI 可以提出候选和修订，最终写入仍走作者确认后的编辑/保存路径。

## 原有写作能力仍然保留

### 结构化创作状态

Novelist 追踪角色档案、角色关系、伏笔、弧线、地点图谱、读者认知和创作偏好。长篇项目不需要每轮对话重新解释世界观和人物状态，Agent 可以通过工具读取和维护这些结构化数据。

### Agent 主动查、主动改、主动维护

系统提供结构化工具给 Agent 使用。AI 在当前对话里可以查角色、查章节、搜索前文、修改状态、更新偏好、生成或修订内容。写作完成后，系统仍会通过维护提醒要求 Agent 检查角色变化、伏笔状态、弧线推进和读者认知。

### 本地语义搜索

RAG 索引和检索状态保存在本机 SQLite/sqlite-vec 中。Embeddings 可以使用兼容 OpenAI 格式的在线 API，也可以使用内置 ONNX 模式。ONNX 模式固定使用随包的 `bge-small-zh-v1.5` int8 模型，不会悄悄回退到线上 API。

### Diff 审批和版本历史

AI 不应直接覆盖正文。正文编辑走 Diff/审批和显式保存路径，项目修改有 Git 历史，可以回退。

## Skill 系统

Skill 是写作方法论模块。每个 Skill 是一个带 YAML frontmatter 的 `.md` 文件，支持三层覆盖和三种触发模式。

### 三层覆盖

同名 Skill 按 **小说 > 用户 > 内置** 优先级覆盖，修改后热重载。

| 层级 | 存储路径 | 可见范围 | 可编辑 |
|---|---|---|---|
| 内置 Builtin | 打包只读 | 所有小说 | 否 |
| 用户 User | 数据目录 `skills/`，工具路径兼容 `~/.goink/skills/` | 所有小说 | 是 |
| 小说 Novel | `{novel}/skills/` | 当前小说 | 是 |

### 三种触发模式

| 模式 | AI 自主调用 | 用户 `/` 触发 | 会话开头注入 | 出现在目录 |
|---|---|---|---|---|
| 智能 `auto` | 是 | 是 | 否 | 是 |
| 指令 `manual` | 否 | 是 | 否 | 否 |
| 常驻 `always` | 是 | 是 | 是 | 否 |

新建一个 `.md` 文件就是新 Skill：

```markdown
---
name: 我的写作流程
description: 个人定制创作流程
category: 自定义
mode: auto
---

# 正文 markdown 内容
```

Skill 解决“方法”和“风格”，参考锚定层解决“证据”“边界”和“审计”。两者是叠加关系，不是替代关系。

## 前端可视化状态

<p align="center">
  <img src="assets/arc-demo.png" alt="故事弧线" />
</p>
<p align="center">
  <img src="assets/location-demo.png" alt="地点图谱" />
</p>
<p align="center">
  <img src="assets/preferences-demo.png" alt="创作偏好" />
</p>
<p align="center">
  <img src="assets/skill-demo.png" width="80%" alt="Skill 技能系统" />
</p>

## 当前实现状态

- 桌面主线已迁移到 `.NET 10 + Photino.NET + React/Vite`。
- Go/Wails 路径已退役，只作为历史代码保留；新功能不要写入 `app/`、`internal/` 或 `frontend/src/lib/wailsjs/`。
- 参考锚定实现计划中 Phase 0-10 和 Phase 13 已完成。
- Phase 11 继续收敛低干预编排、修订授权、停点恢复和最终插入 UX。
- Phase 12 继续推进 workspace 级共享参考语料和 AI 驱动材料选择。

详细设计见：

- [Reference Anchor Technical Baseline](docs/reference-anchor-layer-plan.md)
- [Reference Anchor Implementation Plan](docs/reference-anchor-implementation-plan.md)
- [Photino Bridge Contract](docs/novelist-photino-bridge-contract.md)
- [Release Notes](docs/releases/release-notes.md)

## 项目结构

```text
src/
  Novelist.App             Photino 桌面宿主和本地前端资源解析
  Novelist.Contracts       桥接 DTO 和跨层契约
  Novelist.Core            应用接口、桥接分发和核心边界
  Novelist.Infrastructure  文件系统、SQLite、RAG、参考锚定实现
  Novelist.Agent           Microsoft Agent Framework 工具适配

frontend/
  src/lib/novelist         自有 Photino bridge adapter
  src/components           React UI 组件
  scripts                  Playwright mock-bridge 工作流

tests/
  Novelist.Tests
  Novelist.IntegrationTests
```

## 安装

从 [Releases](https://github.com/devhxj/goink/releases) 下载对应平台安装包：

- **Windows**：运行安装程序
- **macOS**：打开 DMG，拖入 Applications
- **Linux**：运行 AppImage

需要配置 LLM API Key。内置 DeepSeek、GLM、MiMo 模板，并兼容 OpenAI 格式接口。语义检索可使用在线 Embeddings API，也可在设置中切换到内置 ONNX。安装包自带桌面宿主、前端资源和 Git 运行时，不需要 Python、Node.js 或外部数据库。

Windows SmartScreen 可能提示未签名程序，可通过“更多信息”选择继续运行。

## 从源码构建

```bash
sudo apt install libgtk-3-0 libwebkit2gtk-4.1-0 curl file unzip
git clone https://github.com/devhxj/goink
cd goink
dotnet restore Novelist.slnx
npm --prefix frontend ci
make deps
make build
make dev
```

`make dev` 不会自动构建前端。桌面开发模式需要先运行：

```bash
npm --prefix frontend run build
make dev
```

只调试前端时可以运行：

```bash
make frontend-dev
```

这只启动 Vite，桌面桥接 API 不可用；如需桥接能力，需要让 Photino 宿主用 `--start-url=http://localhost:5173/` 加载 Vite 页面。

## 验证命令

后端测试：

```bash
dotnet test Novelist.slnx --no-restore -v minimal
```

前端构建、lint 和真实浏览器 mock-bridge 回归：

```bash
npm --prefix frontend run verify
```

单独运行参考锚定深度流程：

```bash
npm --prefix frontend run test:reference-anchor
```

单独运行 app-wide 前端烟测：

```bash
npm --prefix frontend run test:app
```

## 技术栈

| 层 | 选型 |
|---|---|
| 桌面框架 | Photino.NET + .NET 10 |
| Agent 引擎 | Microsoft Agent Framework + OpenAI-compatible streaming + 结构化工具 |
| 前端 | React 19 + TypeScript 6 + Tailwind CSS 4 + shadcn/ui |
| 编辑器 | Monaco Editor，本地打包资源 |
| 存储 | 文件系统 JSON 存储 + SQLite |
| 向量搜索 | sqlite-vec + 在线 Embeddings API 或本地 ONNX |
| 版本控制 | 内置 Git |
| 安全边界 | SafePath、审批流、SSRF 检查、参考锚定审计和最终插入人工确认 |

## License

MIT
