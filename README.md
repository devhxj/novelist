**中文** | [English](README_EN.md)

<div align="center">

![Today's Verse](https://v2.jinrishici.com/one.svg?font-size=20&spacing=2&color=Chocolate)
</div>

<p align="center">
  <img src="assets/logo-dark.svg#gh-dark-mode-only" alt="Novelist" />
  <img src="assets/logo-light.svg#gh-light-mode-only" alt="Novelist" />
</p>

<h1 align="center">Novelist</h1>
<p align="center">
  本地优先的 AI 长篇写作工作台：管理角色状态、参考资料、写作方法和版本历史，在生成前补足叙述视角。
</p>

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

Novelist 面向长篇小说创作。小说需要情绪共鸣，但当前 AI 对情绪推进、表达分寸和角色内在反应的控制并不稳定。直接生成时，它常常跳出角色内部视角，改成说明剧情；人物的情绪、误解、盲区和身体反应如果没有提前组织，模型很容易写出解释句、剧本式动作和括号补充。

Novelist 把角色状态、参考材料、Skill、RAG 和 Git 放在同一个本地优先的工作台里。生成前先整理叙事视角和约束；生成后通过审计、Diff 和保存边界交给作者确认。

它不把 AI 当成独立作者。故事方向、人物关系、主题取舍和关键情节仍由作者设定；AI 主要负责扩写、续写、改写和参考锚定仿写，把作者给出的核心意图和参考材料转成可审计的候选文本。

## 设计思路

<table>
  <thead>
    <tr>
      <th width="22%">问题</th>
      <th width="78%">处理方式</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><strong>作者先定核心</strong></td>
      <td>故事方向、人物关系、主题取舍和关键情节先由作者确定。AI 在这些边界内做扩写、续写、改写和参考锚定仿写。</td>
    </tr>
    <tr>
      <td><strong>情绪控制不足</strong></td>
      <td>小说靠情绪共鸣成立。AI 可以描述情绪，但很难稳定控制情绪从哪里来、推进到哪里、该露出多少。Novelist 将情绪状态放到生成前处理。</td>
    </tr>
    <tr>
      <td><strong>叙事意识缺位</strong></td>
      <td>句子好看还不够。AI 常把场景写成说明或剧本，缺少一个带着情绪、偏见和认知边界的叙述者。</td>
    </tr>
    <tr>
      <td><strong>风格成分有限</strong></td>
      <td>短句、口语化、白描等标签可以减少错误，但不能单独生成“人味”。叙述者的偏见、盲区、身体感和思绪游移，需要作为整体状态参与生成。</td>
    </tr>
    <tr>
      <td><strong>外部推演层</strong></td>
      <td>在只能使用 API 的约束下，Novelist 将情感认知和叙述视角放到模型外部。每次生成前，Tool 先推演角色此刻的情绪、表现方式、认知边界和可见信息，再注入 Prompt。</td>
    </tr>
  </tbody>
</table>

## 核心能力

<table>
  <thead>
    <tr>
      <th width="22%">能力</th>
      <th width="78%">说明</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><strong>叙事视角推演</strong></td>
      <td>生成前整理角色当前的情绪、偏见、盲区、可见信息和叙述位置。</td>
    </tr>
    <tr>
      <td><strong>结构化创作状态</strong></td>
      <td>角色关系、伏笔、弧线、地点、读者知道多少、作者偏好和章节计划，都放在可查询的状态里。</td>
    </tr>
    <tr>
      <td><strong>Agent 工具调用</strong></td>
      <td>AI 可以查前文、更新项目状态、维护偏好、提出候选内容，但不能偷偷把正文写进去。</td>
    </tr>
    <tr>
      <td><strong>Skill 方法论</strong></td>
      <td>用 Markdown Skill 留住具体做法，比如场景节拍、对白潜台词、节奏、修订和去 AI 味。</td>
    </tr>
    <tr>
      <td><strong>素材库与章节参考素材</strong></td>
      <td><code>素材库</code>负责导入、处理、检索和校正共享参考语料；章节编辑器里的<code>参考素材</code>面板只消费这些材料来生成可审计候选，不管理语料元数据。</td>
    </tr>
    <tr>
      <td><strong>本地搜索与历史</strong></td>
      <td>SQLite/sqlite-vec 保存 RAG 状态。正文写入走审批边界，项目变更保留 Git 历史。</td>
    </tr>
  </tbody>
</table>

## Phase 15 功能

当前 Phase 15 主线把旧 `goink-master` 的产品能力移植到现有 `.NET 10 + Photino.NET + React/Vite` 架构中。`goink-master` 只作为只读行为参考，不再作为实现目录或构建路径使用。

- **小说导入**：书架支持通过桌面文件选择或拖放导入 `.epub`、`.txt`、`.md`、`.markdown`。TXT/Markdown 会做 UTF-8、UTF-16 LE/BE、GB18030 等编码识别；导入过程有进度、取消、跳过章节诊断、Git 提交、失败清理和启动恢复。默认大小限制为 TXT/Markdown 50 MB、压缩 EPUB 100 MB、EPUB 解压后累计 250 MB。
- **风格素材库**：可保存全局或单本小说的风格样本，按标签/范围/关键词检索，查看确定性文本统计，并从选中的样本生成可预览、可验证的 Skill 草稿。样本可参与参考风格画像，但不会绕过参考锚定的来源审计和审批边界。
- **叙事模式抽取**：可选择全书或多个章节范围，按阶段抽取章节边界、摘要、叙事阶段和可复用叙事 Skill；运行过程有可见进度、trace、取消和模型输出校验，不会自动修改正文。
- **Git 历史面板**：使用内置 LibGit2Sharp/libgit2 读取本地版本历史，不依赖系统 Git CLI。界面支持分页提交、文件列表、重命名/删除/二进制标记和懒加载只读 diff，大文本 diff 会截断。
- **更新检查与 Git 作者**：设置中可配置更新检查 endpoint、手动检查和忽略版本；启动自动检查默认关闭且不阻塞写作。Git 提交作者可配置，留空时使用安全默认身份，并在导入提交和普通保存提交前写入 repo-local Git config。

## 参考锚定

Skill 负责沉淀写作方法，参考锚定负责约束材料和风险。来源、事实边界、POV、蓝图、材料绑定、草稿审计都会留下记录，避免模型凭空写、夹带来源原文，或把高风险内容直接写进正文。

当前参考锚定分为两个入口：

- **素材库**：导入参考来源、自动分段/抽取/打标签/索引，查看来源处理记录、材料明细和来源片段明细，校正低置信或未知标签，归档/恢复材料，并管理风格画像。素材库不要求当前章节，也不显示章节蓝图、候选生成或正文插入控件。
- **章节参考素材**：从章节编辑器打开，读取当前章节上下文，自动推荐可访问语料，启动章节级编排、审查蓝图和候选，最后只把通过审计的候选交给作者显式复制或插入编辑器缓冲区。它不能导入来源、改标签、归档材料或直接保存章节。

章节侧当前可直接完成的自动主路径：

```text
输入章节目标
  -> 跨已启用语料库生成多份蓝图
  -> 选择蓝图或反馈重组
  -> 生成并选择正文候选
  -> 审计通过后由作者显式插入编辑器缓冲区
```

需要逐项确认来源策略、事实边界、材料绑定和审批时，可展开高级参考流程：

```text
作者在素材库导入并处理参考来源
  -> 查看材料/来源/片段明细，必要时校正标签或恢复失败处理
  -> 在章节编辑器打开参考素材面板
  -> 作者确认来源策略、章节目标、已知事实、禁止事实
  -> 启动 orchestration run
  -> 生成章节蓝图
  -> 执行确定性蓝图审查
  -> 作者批准蓝图，或批准 AI 提议的字段级修订
  -> 检索并绑定参考材料
  -> 生成 beat 级候选草稿
  -> 执行草稿审计
  -> 停在最终正文插入确认点
```

章节默认 UI 已使用持久 blueprint session；刷新或重启后，目标、迭代和选定蓝图由服务端状态恢复，单一自动主路径及长任务/错误恢复已有浏览器自动化验证。真实目标用户的无指导走查和写作效果评测仍未完成，因此当前薄切片不表述为最终产品完成。

系统会在这些位置停下并要求作者确认：

- 来源、授权、已知事实或禁止事实不明确；
- 蓝图过期、缺材料、弱检索或材料哈希不匹配；
- 蓝图审查失败，需要修订；
- 参考改写级别、POV、事实边界或审计结果存在高风险；
- 候选草稿准备插入正文。

参考锚定流程不会自动调用 `SaveContent` 写入章节正文。AI 可以提出候选和修订，最终正文插入仍走作者确认后的编辑/保存路径。

## Skill 自定义

Skill 用来沉淀可复用的写作方法，不直接替代叙事状态。每个 Skill 是带 YAML frontmatter 的 Markdown 文件，支持三层覆盖和三种触发模式：

<table>
  <thead>
    <tr>
      <th width="20%">机制</th>
      <th width="80%">说明</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><strong>覆盖顺序</strong></td>
      <td>小说级 <code>skills/&lt;name&gt;.md</code><br />用户级 <code>~/.novelist/skills/&lt;name&gt;.md</code><br />内置只读 <code>/builtin/skills/&lt;name&gt;.md</code></td>
    </tr>
    <tr>
      <td><strong>触发模式</strong></td>
      <td><code>auto</code> 可由 AI 或用户 <code>/</code> 调用；<code>manual</code> 只支持用户触发；<code>always</code> 会在会话开头注入。</td>
    </tr>
    <tr>
      <td><strong>状态文件</strong></td>
      <td><code>novelist.md</code> 保存故事状态，供 Agent 恢复上下文并维护长期连续性。</td>
    </tr>
  </tbody>
</table>

最小 Skill 文件：

```markdown
---
name: 节奏控制
description: 控制场景推进、停顿和悬念释放
category: 写作方法
mode: auto
---

# 使用方法

根据当前章节目标调整叙事节奏。
```

## 当前状态

<table>
  <thead>
    <tr>
      <th width="22%">范围</th>
      <th width="78%">状态</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><strong>桌面主线</strong></td>
      <td>已迁移到 <code>.NET 10 + Photino.NET + React/Vite</code>。</td>
    </tr>
    <tr>
      <td><strong>参考锚定</strong></td>
      <td>共享素材库处理与章节级参考使用已形成分离薄切片；默认章节路径已使用持久 session，跨重启、长任务和错误恢复已有自动化验证。真实用户无指导走查仍未完成。</td>
    </tr>
    <tr>
      <td><strong>语料驱动写作</strong></td>
      <td>M1 产品薄切片成立；M2 的 50K 全管线标准门已通过；M3-M5 等待真实效果证据；M6-M8 冻结扩张；M9 自动化体验收口已完成，仍待真实用户任务验证。整体仍为 S，尚未达到生产闭环 P 或规模化 L。</td>
    </tr>
    <tr>
      <td><strong>Phase 15</strong></td>
      <td>正在合并小说导入、风格素材库、叙事模式抽取、Git 历史 UI 和产品鲁棒性改进。</td>
    </tr>
    <tr>
      <td><strong>前端构建</strong></td>
      <td>Vite 8/Rolldown 已拆分主入口、工作区、Monaco、Markdown、Mermaid 和图谱依赖。</td>
    </tr>
    <tr>
      <td><strong>来源致谢</strong></td>
      <td>Novelist 源于 <a href="https://github.com/sigpanic/goink">goink</a>，并在此基础上重构为当前桌面写作工作台。</td>
    </tr>
  </tbody>
</table>

## 最新更新

### 2026-07-11

- 桌面应用会将恢复窗口夹紧到当前显示器可见工作区；启动失败时显示图形错误页，安装包启动不再额外出现控制台窗口。
- 章节里的`参考素材`自动模式收敛为“目标 → 选择写作蓝图 → 正文候选 → 明确插入”，内部来源和手工控件改为按需展开的专家信息。
- 选中的写作蓝图会由服务端章节会话恢复；关闭面板或重启后可继续原有步骤，无需重复生成。
- `素材库`分析可作为后台任务持续运行，离开后返回仍可查看进度，并获得暂停、失败、预算耗尽或阻断后的明确恢复动作。

完整变更见 [Release Notes](docs/releases/release-notes.md)。

## 截图

<p align="center">
  <img src="assets/write-demo.png" width="80%" alt="章节写作" />
</p>
<p align="center">
  <img src="assets/arc-demo.png" width="48%" alt="故事弧线" />
  <img src="assets/location-demo.png" width="48%" alt="地点图谱" />
</p>
<p align="center">
  <img src="assets/preferences-demo.png" width="48%" alt="创作偏好" />
  <img src="assets/skill-demo.png" width="48%" alt="Skill 系统" />
</p>

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

当前构建版本先只考虑 Windows。可从 [Releases](https://github.com/devhxj/novelist/releases) 下载 Windows 安装包并运行安装程序。

需要配置 LLM API Key。内置 DeepSeek、GLM、MiMo 模板，并兼容 OpenAI 格式接口。Windows 安装包自带桌面宿主、前端资源和 LibGit2Sharp 原生运行时，不需要 Python、Node.js、外部数据库或单独安装 Git CLI。本地版本历史由内置 libgit2 运行时提供。

语义检索可使用在线 Embeddings API，也可切换到内置 ONNX。ONNX 模式固定使用随包的 `bge-small-zh-v1.5` int8 模型，不会静默回退到线上 API。

Windows SmartScreen 可能提示未签名程序，可通过“更多信息”继续运行。

## 从源码构建

依赖：

- Windows 10/11
- .NET 10 SDK
- Node.js/npm
- Git Bash / Git（用于克隆源码和运行发布脚本；本地版本历史不依赖系统 Git）
- Inno Setup 6（仅打 Windows 安装包时需要）

```bash
git clone https://github.com/devhxj/novelist
cd novelist
dotnet restore Novelist.slnx
npm --prefix frontend ci
npm --prefix frontend run build
bash scripts/novelist-publish.sh win-x64
```

启动桌面开发模式：

```bash
npm --prefix frontend run build
dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop
```

只调试前端：

```bash
npm --prefix frontend run dev
```

只启动 Vite 时，桌面桥接 API 不可用。如需桥接能力，让 Photino 宿主用 `--start-url=http://localhost:5173/` 加载 Vite 页面。

## 常用命令

<table>
  <thead>
    <tr>
      <th width="38%">命令</th>
      <th width="62%">用途</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td><code>dotnet&nbsp;run&nbsp;--project&nbsp;src/Novelist.App/Novelist.App.csproj&nbsp;--&nbsp;--desktop</code></td>
      <td>启动 Photino/.NET 桌面应用。</td>
    </tr>
    <tr>
      <td><code>bash&nbsp;scripts/novelist-publish.sh&nbsp;win-x64</code></td>
      <td>发布指定 RID 的自包含产物。</td>
    </tr>
    <tr>
      <td><code>VERSION=1.2.3&nbsp;bash&nbsp;scripts/novelist-package-windows.sh</code></td>
      <td>生成 Windows 安装包。</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;dev</code></td>
      <td>启动 Vite 前端开发服务器。</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;build</code></td>
      <td>TypeScript 构建和 Vite 生产构建。</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;lint</code></td>
      <td>前端 ESLint。</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;test:reference-style</code></td>
      <td>运行参考风格锚定浏览器流程，并生成 usability report。</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;test:reference-style:stress</code></td>
      <td>运行参考风格锚定 10MB 浏览器压力流程。</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;verify</code></td>
      <td>执行前端 build、lint、参考锚定流程和基础 app-wide 烟测。</td>
    </tr>
    <tr>
      <td><code>npm&nbsp;--prefix&nbsp;frontend&nbsp;run&nbsp;test:phase16</code></td>
      <td>运行 Phase 16 的素材库和章节参考素材浏览器工作流。</td>
    </tr>
    <tr>
      <td><code>dotnet&nbsp;test&nbsp;Novelist.slnx&nbsp;--no-restore&nbsp;-v&nbsp;minimal</code></td>
      <td>运行 .NET 测试套件。</td>
    </tr>
  </tbody>
</table>

## 质量边界

开发或审查相关代码时，请保留这些边界：

- 正文写入必须经过作者确认，不允许参考锚定编排直接保存正文；
- 文件访问保持 SafePath 和沙箱检查；
- Web/外部资源工具保持 SSRF 防护；
- 用户数据迁移必须 copy-first，源数据保持不变并写入 manifest；
- API Key、本地模型路径和用户数据不进入 git；
- 运行时 Git 与本地 ONNX 模型放在 `build/runtime/` 或 app data/config 路径；ONNX Runtime 与 sqlite-vec 通过 NuGet 发布资产进入产物，额外覆盖库也不要放源码目录。

## 文档入口

- [Reference Anchor Technical Baseline](docs/reference-anchor-layer-plan.md)
- [Reference Anchor Implementation Plan](docs/reference-anchor-implementation-plan.md)
- [Corpus-Driven Writing 开发计划](docs/corpus-driven-writing/development-plan.md)
- [Corpus-Driven Writing 任务与当前状态](docs/corpus-driven-writing/tasks.md)
- [Corpus-Driven Writing 进展审计（2026-07-10）](docs/corpus-driven-writing/progress-audit-2026-07-10.md)
- [Photino Bridge Contract](docs/novelist-photino-bridge-contract.md)
- [Release Notes](docs/releases/release-notes.md)

## 许可与来源

Novelist 以 MIT License 发布，详见 [LICENSE](LICENSE)。项目最初 fork 自 MIT 版本 [goink](https://github.com/sigpanic/goink)，当前主体已重做为 `.NET 10 + Photino.NET + React/Vite` 的 Novelist。来源与兼容边界见 [NOTICE](NOTICE)。

本仓库不合并上游改为 AGPL 后的新代码；若继续使用或分发本仓库，请保留 MIT 版权和许可声明。
