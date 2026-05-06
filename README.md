# AI 小说创作系统

> **AI 驱动的智能小说创作平台 | IDE 风格统一交互界面 | 多 Agent 协同创作**

[![Python](https://img.shields.io/badge/Python-3.9+-blue.svg)](https://www.python.org/)
[![React](https://img.shields.io/badge/React-19.x-61DAFB.svg)](https://react.dev/)
[![FastAPI](https://img.shields.io/badge/FastAPI-0.109-009688.svg)](https://fastapi.tiangolo.com/)
[![LangGraph](https://img.shields.io/badge/LangGraph-0.0.20-green.svg)](https://langchain-ai.github.io/langgraph/)
[![MCP](https://img.shields.io/badge/MCP-≥1.0.0-orange.svg)](https://modelcontextprotocol.io/)

---

## ✨ 核心亮点

### 🤖 多 Agent 智能协作架构

采用**生产级多 Agent 系统**，实现专业化分工与协同创作：

| Agent 角色 | 职责 | 核心能力 |
|-----------|------|---------|
| **Coordinator (协调员)** | 任务调度与流程编排 | 8 层深度自动任务链、状态持久化、失败重试 |
| **Writer (写手)** | 内容生成与情节规划 | 30+ 写作参数动态 Prompt 构建、风格适配 |
| **Reviewer (审稿人)** | 质量审核与一致性检查 | 多维度评分、问题定位、修改建议 |

**技术特色：**
- 基于 [LangGraph](https://langchain-ai.github.io/langgraph/) 的**有向图工作流引擎**
- 7 节点流水线：`Context准备 → 内容生成 → 质量审核 → 一致性检查 → 迭代修订 → 章节保存 → 记忆更新`
- **条件路由**：根据审核结果和一致性检查自动决定是否进入修订循环（最多 3 次迭代）
- **任务链式编排**：Agent 可通过 `next_actions` 自动触发后续子任务
- **全流程可观测**：每个节点状态持久化至数据库，支持断点续跑

### 🔌 MCP (Model Context Protocol) 工具生态

遵循 [Model Context Protocol](https://modelcontextprotocol.io/) 标准的**插件化工具体系**：

```
┌─────────────────────────────────────────┐
│           MCP Tool Registry              │
├──────────────┬──────────┬───────────────┤
│ 小说管理工具 │ 编辑工具  │ 记忆检索工具   │
│ ├─ 创建小说  │ ├─ 文本  │ ├─ 向量检索    │
│ ├─ 查询章节  │ ├─ 格式  │ ├─ 语义匹配    │
│ └─ 角色CRUD  │ └─ 修订  │ └─ 上下文召回  │
├──────────────┴──────────┴───────────────┤
│         一致性检查工具                    │
│  ├─ 角色行为校验  ├─ 时间线逻辑检测      │
│  └─ 设定冲突识别                          │
└─────────────────────────────────────────┘
```

**核心技术特性：**
- **标准化接口**：继承 `BaseMCPTool`，统一 `execute()` 执行协议
- **JSON Schema 校验**：参数自动验证，类型安全保证
- **OpenAI Function Calling 兼容**：一键转换为 LLM 可调用格式
- **事务安全**：执行失败自动回滚数据库事务
- **双传输模式**：支持 SSE（HTTP 长连接）和 StdIO（CLI 工具）

### 🧠 分层 RAG 上下文引擎

创新的**四层缓存架构**，解决长文本生成的上下文窗口限制：

```python
LAYER_CONFIG = {
    "STATIC":    { "priority": 1, "desc": "小说标题、简介（几乎不变）" },
    "STABLE":    { "priority": 2, "desc": "角色设定、世界观（低频更新）" },
    "SLIDING":   { "priority": 3, "desc": "近期章节摘要、活跃情节线" },
    "DYNAMIC":   { "priority": 4, "desc": "当前章节相关记忆、待处理伏笔" }
}
```

**智能组装能力：**
- **StoryBrief 生成器**：自动整合 Plot/Timeline/Foreshadowing/Memory 四维信息
- **TTL 缓存**：默认 300 秒过期，避免重复查询
- **优先级排序**：按 `due_plot_nodes`、`unresolved_foreshadowings` 等标记紧急项
- **作者偏好注入**：长期配置（`author_preferences`）跨会话保持

### 💬 统一 WebSocket 聊天界面

**IDE 风格的交互范式**——所有功能通过对话式 UI 触发：

**前端能力矩阵：**
- **实时流式输出**：`content_chunk` 事件驱动逐字显示
- **工具调用可视化**：Timeline 展示完整协作过程
  ```
  用户消息 → [AI思考中...] → 🔧 get_novel_summary (执行中)
                                    ↓ 完成
          → [AI继续思考...] → 🔧 check_consistency (完成)
                                    ↓
          → 最终回复生成完毕
  ```
- **多层级会话管理**：
  - `novel` 级：全局讨论、大纲规划
  - `chapter` 级：章节生成、修改
  - `free` 级：自由问答
- **Token 用量监控**：进度条可视化 + 自动压缩建议
- **上下文编辑器**：可视化调整小说/章节级上下文参数

### ⚡ 实时协作编辑器

基于 **Monaco Editor** 的**差异预览编辑系统**：

- **WebSocket 同步**：毫秒级延迟的多端协作
- **Diff/Patch 引擎**：最小带宽的增量传输
- **编辑会话状态机**：`PENDING → ACCEPTED/REJECTED`
- **幂等操作保护**：`already_processed` 标志防止重复应用

---

## 🛠️ 技术栈

### 后端服务

| 类别 | 技术 | 版本 | 应用场景 |
|-----|------|------|---------|
| Web框架 | **FastAPI** | 0.109.0 | 异步 API、WebSocket、依赖注入 |
| ORM | **SQLAlchemy** (Async) | 2.0.25 | MySQL 异步操作、模型关系映射 |
| AI 编排 | **LangChain + LangGraph** | 0.1.0 / 0.0.20 | Agent 工作流、Prompt 管理 |
| 向量库 | **ChromaDB** | 0.4.22 | RAG 记忆存储、语义检索 |
| 缓存 | **Redis** | 5.0.1 | 会话缓存、发布订阅 |
| 数据验证 | **Pydantic** | 2.5.3 | 请求/响应模型定义 |
| 认证 | **JWT (python-jose)** | 3.3.0 | Token 签发与验证 |
| LLM 服务 | **DeepSeek API** | - | 文本生成（主）|
| 备选 LLM | OpenAI / Anthropic | 1.10.0 / 0.18.1 | 多模型支持 |

### 前端应用

| 类别 | 技术 | 版本 | 应用场景 |
|-----|------|------|---------|
| UI 框架 | **React 19** | 19.2.4 | 函数组件、Hooks |
| 语言 | **TypeScript** | ~5.9.3 | 类型安全、接口约束 |
| 构建工具 | **Vite** | 8.0.1 | 快速热更新、ESBuild |
| 组件库 | **Ant Design** | 6.3.4 | 企业级 UI 组件 |
| 编辑器 | **Monaco Editor** | 4.7.0 | 代码/富文本编辑 |
| 状态管理 | **Zustand** | 5.0.12 | 轻量级全局状态 |
| HTTP | **Axios** | 1.13.6 | 请求拦截、错误处理 |
| Markdown | **react-markdown** | 10.1.0 | 富文本渲染 |

---

## 📂 项目架构

```
todo/
├── backend/app/
│   ├── main.py                     # FastAPI 入口 | lifespan 管理
│   ├── core/                       # ★ 核心基础设施层
│   │   ├── database.py             # Async SQLAlchemy 引擎
│   │   ├── llm_service.py          # DeepSeek 流式调用封装
│   │   ├── ws_chat.py              # ★ 统一 WebSocket 聊天处理器
│   │   ├── context_builder.py      # ★ 分层 RAG 上下文引擎
│   │   ├── vector_store.py         # ChromaDB 向量操作
│   │   ├── diff_engine.py          # 文本差异算法
│   │   └── edit_mode.py            # 编辑会话状态机
│   │
│   ├── agents/                     # ★ 多 Agent 系统
│   │   ├── base.py                 # Agent 基类 & Task 数据结构
│   │   ├── coordinator.py          # ★ 主控 Agent (8层任务链)
│   │   ├── writer.py               # ★ 写手 Agent (30+参数Prompt)
│   │   ├── reviewer.py             # 审稿 Agent
│   │   └── memory.py               # Agent 记忆模块
│   │
│   ├── workflows/                  # ★ LangGraph 工作流
│   │   └── langgraph_workflow.py   # 7节点章节生成流水线
│   │
│   ├── mcp/                        # ★ MCP 工具生态
│   │   ├── server.py               # FastMCP 服务器实例
│   │   ├── base.py                 # BaseMCPTool 抽象类
│   │   ├── registry.py             # 工具注册表 (插件架构)
│   │   ├── novel_tools.py          # 小说 CRUD 工具
│   │   ├── editing_tools.py        # 文本编辑工具
│   │   ├── memory_tools.py         # 记忆检索工具
│   │   └── consistency_tools.py    # 一致性检查工具
│   │
│   ├── novels/                     # 小说领域模块
│   ├── characters/                 # 角色领域模块
│   ├── chapters/                   # 章节领域模块
│   ├── plot_events/                # 情节追踪模块
│   ├── timeline/                   # 时间线模块
│   ├── consistency/                # 一致性检查模块
│   ├── editor/                     # 协作编辑器模块
│   ├── generation/                 # 文本生成端点
│   ├── sessions/                   # 会话持久化
│   ├── auth/                       # JWT 认证
│   └── requirements.txt            # Python 依赖
│
├── frontend/src/
│   ├── pages/chat/ChatPage.tsx     # ★ 统一聊天界面 (1345行)
│   ├── pages/editor/EditorPage.tsx # Monaco 编辑器页面
│   ├── services/wsGenerationService.ts  # ★ WebSocket 客户端
│   ├── services/mcpService.ts      # MCP 工具调用服务
│   └── types/                      # TypeScript 类型定义
│
├── database/scripts/init_db.py     # 数据库初始化
└── .env.example                    # 环境变量模板
```

---

## 🚀 快速开始

### 环境要求

- **Python**: >= 3.9
- **Node.js**: >= 18
- **MySQL**: >= 8.0
- **Redis**: >= 6.0 (可选)

### 1. 克隆并安装

```bash
git clone <repository-url>
cd todo

# 后端依赖
cd backend && python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
cd ..

# 前端依赖
cd frontend && npm install
```

### 2. 配置环境变量

```bash
cp .env.example .env
# 编辑 .env，填入以下关键配置：
```

**必需配置项：**

```env
# 数据库连接
DATABASE_URL=mysql+pymysql://root:password@localhost:3306/ai_novel_generator

# Redis (可选，缺失时降级运行)
REDIS_URL=redis://localhost:6379/0

# DeepSeek AI (必填)
DEEPSEEK_API_KEY=sk-xxxxxxxxxxxxxxxx
DEEPSEEK_API_BASE=https://api.deepseek.com

# JWT 密钥 (必填，用于 token 签发)
SECRET_KEY=your-random-secret-key-here
```

### 3. 初始化数据库

```bash
python database/scripts/init_db.py
```

> 输出示例：`已创建的表: novels, characters, chapters, plot_events, ...`

### 4. 启动后端服务

```bash
# 开发模式 (热重载)
uvicorn backend.app.main:app --reload --host 0.0.0.0 --port 8000
```

**启动后访问：**
- Swagger API 文档：http://localhost:8000/docs
- ReDoc 文档：http://localhost:8000/redoc
- 健康检查：http://localhost:8000/health
- MCP 端点：http://localhost:8000/mcp (SSE)

### 5. 启动前端服务

```bash
cd frontend
npm run dev
```

访问 http://localhost:5173 即可使用。

---

## 📖 功能全景

### 核心业务模块

#### 1️⃣ 智能章节生成流水线

```
用户指令 → ContextBuilder (4层上下文组装)
         ↓
       WriterAgent (LLM 生成，30+ 参数调优)
         ↓
       ReviewerAgent (质量审核，打分 0-100)
         ↓ [不通过]
       ConsistencyChecker (角色/情节/时间线校验)
         ↓ [发现错误]
       RevisionHandler (汇总反馈，迭代 ≤3次)
         ↓ [通过]
       ChapterPostProcessor (后处理优化)
         ↓
       VectorStore (向量化入库，供未来 RAG 检索)
```

**WriterAgent Prompt 包含维度：**
- 基础信息：章节号、目标字数、风格、语气
- 结构指导：提纲、场景目标、作者意图
- 约束条件：必须保留项 (`must_keep`)、明确避免项 (`must_avoid`)
- 上下文注入：
  - 前文摘要、当前卷目标
  - 角色档案 (性格特征)
  - 活跃情节线、到期/后续 Plot Nodes
  - Timeline 安排、未解决/到期伏笔
  - RAG 检索到的历史记忆片段
  - 本章任务分配 (伏笔回收、Plot 推进要求)
  - 作者长期偏好配置

#### 2️⃣ MCP 工具调用示例

**前端触发方式：** 在聊天界面自然语言描述需求，AI 自动选择工具：

```
用户: "帮我查看第5章的角色行为是否一致"
  → AI 调用: check_character_consistency(novel_id=1, chapter_id=5)
  → 返回: { issues: [...], severity: "warning" }

用户: "总结一下目前所有未回收的伏笔"
  → AI 调用: list_unresolved_foreshadowings(novel_id=1)
  → 返回: [{ title: "...", due_chapter: 12 }, ...]
```

**开发者扩展新工具：**

```python
# 1. 继承基类
class MyCustomTool(BaseMCPTool):
    name = "custom_action"
    description = "自定义操作描述"
    category = MCPToolCategory.WRITING_ASSISTANT
    parameters_schema = {
        "type": "object",
        "properties": {
            "param1": {"type": "string", "description": "参数说明"}
        },
        "required": ["param1"]
    }

    async def execute(self, param1: str, **kwargs) -> MCPToolResult:
        # 业务逻辑...
        return MCPToolResult(success=True, data=result)

# 2. 注册到 registry (在 tools/__init__.py)
registry.register(MyCustomTool())

# 3. 前端自动可见，LLM 可直接调用
```

#### 3️⃣ 实时协作编辑

**WebSocket 消息协议：**

| 消息类型 | 方向 | 说明 |
|---------|------|------|
| `start_edit` | C→S | 开始编辑会话 |
| `apply_edit` | C→S | 提交 diff 补丁 |
| `edit_preview` | S→C | 返回预览对比 |
| `accept_edit` | C→S | 确认更改 |
| `reject_edit` | C→S | 拒绝更改 |
| `end_session` | 双向 | 结束会话 |

#### 4️⃣ 会话管理系统

**三级会话粒度：**

| 级别 | 适用场景 | 上下文范围 |
|-----|---------|-----------|
| **Novel** | 大纲讨论、全局设定 | 整部小说的世界观、角色、主线 |
| **Chapter** | 章节生成、修改 | 单章的前文、大纲、相关角色 |
| **Free** | 通用问答 | 无绑定上下文 |

**生命周期功能：**
- 创建 / 加载 / 切换 / 删除
- 标题自动生成 (首条消息摘要)
- 消息清空 / 上下文压缩 (Token 超限时提醒)
- 使用量监控 (进度条 + 百分比)

---

## 🔌 API 接口总览

基础路径：`/api/v1`

| 模块 | 前缀 | 核心端点 |
|-----|------|---------|
| **认证** | `/auth` | `POST /login`, `POST /register`, `POST /refresh` |
| **小说** | `/novels` | CRUD + 列表分页搜索 |
| **角色** | `/characters` | CRUD + 关系图谱 |
| **章节** | `/chapters` | CRUD + AI 生成触发 |
| **情节** | `/plot_events` | 事件 CRUD + 时间轴关联 |
| **记忆** | `/memory` | 存储 + 向量检索 |
| **Agent** | `/agents` | 任务提交 + 状态查询 |
| **一致性** | `/consistency` | 多维检查 + 报告导出 |
| **规划** | `/planning` | PlotLine/PlotNode CRUD |
| **MCP** | `/mcp` | 工具调用代理 |
| **生成** | `/generation` | 流式/非流式文本生成 |
| **会话** | `/sessions` | 会话 CRUD + 消息历史 |
| **编辑器** | `/editor` | EditSession 管理 |
| **时间线** | `/timeline` | TimelineEntry CRUD |
| **WebSocket** | `/ws/chat` | 统一实时通信端点 |

**详细交互文档：** 启动后访问 http://localhost:8000/docs

---

## 🏗️ 系统架构图

```
                         ┌──────────────────────┐
                         │    React Frontend    │
                         │  ┌────────────────┐  │
                         │  │ ChatPage (WS)   │  │
                         │  │ EditorPage (WS) │  │
                         │  └───────┬────────┘  │
                         └──────────┼───────────┘
                                    │
                    ┌───────────────┼───────────────┐
                    │               │               │
              HTTP REST        WebSocket           SSE/MCP
                    │               │               │
                    ▼               ▼               ▼
┌─────────────────────────────────────────────────────────────┐
│                    FastAPI Backend                           │
│  ┌─────────────────────────────────────────────────────┐   │
│  │                  Router Layer                        │   │
│  └─────────────────────┬───────────────────────────────┘   │
│                        │                                   │
│  ┌─────────────────────▼───────────────────────────────┐   │
│  │              Service / Agent Layer                   │   │
│  │                                                      │   │
│  │  ┌──────────┐  ┌──────────┐  ┌──────────────────┐  │   │
│  │  │Coordinator│  │ Writer   │  │ Reviewer         │  │   │
│  │  │  Agent    │  │  Agent   │  │  Agent           │  │   │
│  │  └─────┬─────┘  └────┬─────┘  └────────┬─────────┘  │   │
│  │        │             │                  │             │   │
│  │        ▼             ▼                  ▼             │   │
│  │  ┌──────────────────────────────────────────────┐   │   │
│  │  │        LangGraph Workflow Engine             │   │   │
│  │  │  Prepare → Generate → Review → Check → Save  │   │   │
│  │  └──────────────────────────────────────────────┘   │   │
│  └─────────────────────┬───────────────────────────────┘   │
│                        │                                   │
│  ┌─────────────────────▼───────────────────────────────┐   │
│  │                Core Infrastructure                  │   │
│  │  ┌─────────┐ ┌─────────┐ ┌──────────┐ ┌─────────┐  │   │
│  │  │LLM Service│ │Context  │ │VectorStore│ │DiffEngine│  │   │
│  │  │(DeepSeek) │ │Builder  │ │(ChromaDB)│ │         │  │   │
│  │  └─────────┘ └─────────┘ └──────────┘ └─────────┘  │   │
│  └────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────┘
         │                  │                    │
    ┌────┴────┐      ┌──────┴──────┐       ┌────┴────┐
    │  MySQL  │      │    Redis    │       │ChromaDB │
    │ (持久化) │      │  (缓存/会话)│       │ (向量)  │
    └─────────┘      └─────────────┘       └─────────┘
                              │
                     ┌────────┴────────┐
                     │  DeepSeek API   │
                     │   (LLM 推理)    │
                     └─────────────────┘
```

---

## 💡 开发指南

### 后端开发规范

```python
# 1. 使用 Pydantic 定义请求/响应模型
from pydantic import BaseModel

class CreateChapterRequest(BaseModel):
    novel_id: int
    chapter_number: int
    target_length: int = 3000
    style: str = "narrative"

# 2. Async/Await 异步编程
async def generate_chapter(request: CreateChapterRequest):
    async with AsyncSessionLocal() as db:
        result = await some_async_operation(db)

# 3. 统一返回格式
return {
    "success": True,
    "data": {...},
    "error": None  # 或 {"code": "ERROR", "message": "..."}
}
```

### 前端开发规范

```typescript
// 1. TypeScript 严格模式
interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  timestamp: Date
}

// 2. 函数式组件 + Hooks
function ChatPage() {
  const [messages, setMessages] = useState<ChatMessage[]>([])
  
  useEffect(() => {
    connectWebSocket()
  }, [])
}

// 3. CSS Modules 样式隔离
import styles from './ChatPage.module.css'
<div className={styles.container}>
```

### Git 提交规范

```
feat: 新增 MCP 工具 - 伏笔管理
fix: 修复 WebSocket 断连重连逻辑
docs: 更新 API 接口文档
refactor: 重构 ContextBuilder 缓存策略
test: 添加 Agent 协调器单元测试
chore: 升级 LangGraph 至 0.0.21
```

---

## ❓ 常见问题

**Q: 如何切换 AI 模型？**

A: 修改 `.env` 中的 `DEEPSEEK_API_BASE`，或在调用时指定 `model` 参数（如 `deepseek-reasoner`）。系统也预留了 OpenAI/Anthropic 接口。

**Q: Redis 连接失败会影响使用吗？**

A: 不会。系统会在日志中输出警告，并以降级模式运行（无缓存、无 Pub/Sub），但核心功能正常。

**Q: 如何扩展新的 MCP 工具？**

A: 参见上文「MCP 工具调用示例」中的三步法：继承 `BaseMCPTool` → 注册到 `registry` → 前端自动可用。

**Q: 工作流执行中断如何恢复？**

A: LangGraph 配置了 `MemorySaver` 检查点，可通过 `workflow.get_state(task_id)` 获取最后状态，然后重新 `ainvoke` 继续。

**Q: 前端 Token 超限怎么办？**

A: 系统会自动标记 `should_compress=true`，点击「压缩上下文」按钮即可精简历史消息（保留关键摘要）。

---

## 📊 性能基准

| 指标 | 数值 | 说明 |
|-----|------|------|
| **章节生成耗时** | 8-15s | 3000 字，含审核+一致性检查 |
| **WebSocket 延迟** | <50ms | 本地网络 |
| **RAG 检索延迟** | 100-200ms | ChromaDB 语义搜索 |
| **上下文构建** | 200-500ms | 4层缓存命中时 <100ms |
| **并发会话支持** | 100+ | 受限于 Redis/MySQL 连接池 |

---

## 📜 许可证

[MIT License](LICENSE)

---

## 🙏 致谢

- [FastAPI](https://fastapi.tiangolo.com/) - 现代高性能 Python Web 框架
- [LangChain/LangGraph](https://github.com/langchain-ai) - LLM 应用开发框架
- [Model Context Protocol](https://modelcontextprotocol.io/) - AI 工具调用标准
- [Ant Design](https://ant.design/) - 企业级 React UI 库
- [Monaco Editor](https://microsoft.github.io/monaco-editor/) - VS Code 编辑器内核
- [DeepSeek](https://platform.deepseek.com/) - 高性能大语言模型

---

<p align="center">
  <strong>⭐ 如果这个项目对你有帮助，欢迎 Star 支持！</strong>
</p>

<p align="center">
  Built with ❤️ by AI Novel Creation Team | 2024
</p>
