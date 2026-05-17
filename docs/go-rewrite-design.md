# Go 重写设计文档

## 一、项目目标

将 Python Web 版 AI 小说创作系统重写为 **Go 本地桌面应用**，核心变化：

| | Python 版 | Go 版 |
|---|---|---|
| 产品形态 | Web 服务（FastAPI + React dev server） | 本地桌面软件（Wails，双击运行） |
| 运行时 | Python venv + Node.js + npm | 单二进制（~200MB，含模型） |
| 外部依赖 | MySQL + Redis + ChromaDB | 零（SQLite + sqlite-vec 嵌入） |
| 用户模型 | 多用户 JWT 认证 | 单用户，无认证 |
| 通信方式 | HTTP REST + WebSocket | Wails IPC（Go 函数直接暴露给前端） |
| AI 工作流 | LangGraph | 简单 goroutine 状态机 |
| 会话管理 | Redis + 数据库 | SQLite + 内存 |
| Embedding | sentence-transformers (PyTorch) | ONNX Runtime（同模型导出） |
| 前端部署 | Vite dev server (localhost:5173) | 嵌入 Go 二进制（Wails WebView） |

## 二、架构原则

### 2.1 分层：核心与壳分离

`internal/` 是纯 Go 核心逻辑，不知道任何传输层（Wails 还是 HTTP）的存在。`app/` 和 `server/` 是两个薄壳，只做参数转换和调用核心。

```
internal/          ← 全部核心逻辑（零传输层依赖）
    character/    
    chapter/      
    mcp/          
    agent/        
    llm/          
    rag/          
    context/      
    storage/      
    ...

app/               ← Wails 桌面壳（薄）
    handler.go     ← func (a *App) Chat(...) → internal/agent.Loop(...)

server/            ← 未来 Web 壳（薄，目前不做）
    handler.go     ← http.Handler → internal/agent.Loop(...)
```

这意味着：同一份 `internal/` 代码，桌面版和未来的 Web 版共用。Web 版就是包一层 HTTP router 和 handler，核心逻辑一行不变。

### 2.2 双层存储：Git 管正文，SQLite 管元数据

章节正文以 Markdown 文件存储在 Git 仓库中，每部小说一个 Git 仓库。AI 每次编辑章节后自动 commit。正文的版本历史、差异对比、回退走标准 Git 操作。

元数据（角色、时间线、弧线、地点、创作偏好、会话等结构化数据）存储在 SQLite。这些数据体积小但需要排序、筛选、关联查询。向量索引通过 sqlite-vec 扩展存在同一 SQLite 文件中。

| 数据类型 | 存储 | 说明 |
|---------|------|------|
| 章节正文 | Git 仓库（.md 文件） | 天然支持 diff、历史回溯、分支创作 |
| 章节元数据 | SQLite | 标题、编号、状态、字数、对应文件路径 |
| 角色、角色关系 | SQLite | 结构化 JSON 字段 |
| 时间线、弧线、地点、读者认知 | SQLite | 需要索引和筛选 |
| 创作偏好 | SQLite | 双层（用户全局 + 单书） |
| 对话会话与消息 | SQLite | 持久化会话历史 |
| 操作日志 | SQLite | 元数据回退数据源 |
| 向量索引 | SQLite（sqlite-vec 扩展） | 与元数据同库 |

### 2.3 领域内聚：按实体分包

跟 Python 版本的思路一致——每个领域实体一个包，包内包含该实体的类型定义和数据库操作：

```
character/          # 关于"角色"的一切数据操作
chapter/            # 关于"章节"的一切数据操作
timeline/           # 关于"时间线"的一切数据操作
...
```

MCP 工具因为它们共享 registry、base types、JSON Schema 生成，集中在一个 `mcp/` 包：

```
mcp/                # 所有 MCP 工具集中内聚
    registry.go     # 统一注册、OpenAI Function Calling 格式生成
    base.go         # Tool 接口、ToolResult、ToolContext
    novel_tools.go
    character_tools.go
    editing_tools.go
    ...
```

### 2.4 Go 命名惯例

- 包名 = 目录名
- 类型定义文件叫 `types.go`，不跟包名重复（不写 `character/character.go`）
- 数据库操作文件叫 `store.go`
- 常量定义文件叫 `consts.go`

## 三、Go 工程最佳实践约定

### 3.1 错误处理

```go
// 统一使用 fmt.Errorf + %w 包装错误链
if err != nil {
    return fmt.Errorf("character store: update: %w", err)
}

// 包级别预定义哨兵错误
var ErrNotFound = errors.New("not found")
var ErrConflict = errors.New("resource conflict")
```

### 3.2 依赖注入

不使用全局变量或单例模式。通过构造函数注入依赖：

```go
func NewCharacterStore(db *sql.DB, logger *slog.Logger) *CharacterStore {
    return &CharacterStore{db: db, logger: logger}
}
```

### 3.3 接口隔离

每个模块通过接口暴露能力，便于测试和替换：

```go
type Store interface {
    GetByID(ctx context.Context, id int64) (*Character, error)
    List(ctx context.Context, novelID int64) ([]Character, error)
    Create(ctx context.Context, c *Character) error
    Update(ctx context.Context, c *Character) error
}
```

### 3.4 测试

- 表驱动测试（table-driven tests）
- 接口 mock 使用 `gomock` 或手写 stub
- 集成测试使用临时 SQLite 文件

### 3.5 Context 传递

所有涉及 I/O 或需要取消的函数，第一个参数必须是 `context.Context`：

```go
func (s *Store) GetByID(ctx context.Context, id int64) (*Character, error)
```

## 四、日志系统设计

### 4.1 技术选型

使用 Go 1.21+ 标准库 `log/slog`，结构化日志。不依赖第三方日志库。

### 4.2 初始化

```go
// 开发环境：彩色文本输出
// 生产环境：JSON 格式输出到文件
func New(level slog.Level, format string, output io.Writer) *slog.Logger {
    var handler slog.Handler
    opts := &slog.HandlerOptions{
        Level:     level,
        AddSource: true,
    }
    switch format {
    case "json":
        handler = slog.NewJSONHandler(output, opts)
    default:
        handler = slog.NewTextHandler(output, opts)
    }
    return slog.New(handler)
}
```

### 4.3 日志级别使用规范

| 级别 | 使用场景 |
|------|---------|
| **DEBUG** | 开发调试信息、SQL 语句、详细参数 |
| **INFO** | Agent 循环状态、工具调用、上下文构建摘要、性能指标 |
| **WARN** | 可恢复的错误（缓存未命中、降级运行、重试成功） |
| **ERROR** | 不可恢复的错误（LLM 调用失败、数据库损坏、模型加载失败） |

### 4.4 上下文传播

每个 turn 和 session 使用 `slog.With` 附加结构化字段：

```go
logger := baseLogger.With(
    "session_id", sessionID,
    "turn_id", turnID,
    "novel_id", novelID,
)
logger.Info("agent loop started")
// 输出: INFO agent loop started session_id=sess_xxx turn_id=turn_yyy novel_id=1
```

### 4.5 关键埋点

以下位置必须有日志：

- Agent 循环：每轮 LLM 调用的 token 消耗和耗时
- MCP 工具：每次工具调用的名称、参数摘要、执行结果
- RAG 检索：查询文本、返回结果数、最高相关度
- 存储层：写操作的表名和实体 ID（DEBUG 级别）
- ONNX 推理：首次加载耗时、每次 embedding 耗时（DEBUG 级别）
- LLM 流式：首 token 延迟、总 token 数
- 异常路径：所有 error 返回必须记录

## 五、目录树

```
novel-agent/
├── cmd/
│   └── novel/
│       └── main.go                          # Wails 应用入口
│
├── internal/
│   │
│   │  ═══════════════════════════════════════
│   │  ║  领域包 — 每个实体自包含：types + store
│   │  ═══════════════════════════════════════
│   │
│   ├── novel/                               # 小说
│   │   ├── types.go                         # Novel, NovelCreativeProfile, NovelStoryState
│   │   └── store.go                         # CRUD
│   │
│   ├── chapter/                             # 章节
│   │   ├── types.go                         # Chapter
│   │   └── store.go                         # CRUD + 分页
│   │
│   ├── character/                           # 角色 + 角色关系
│   │   ├── types.go                         # Character, CharacterRelation
│   │   └── store.go                         # CRUD
│   │
│   ├── timeline/                            # 故事时间线
│   │   ├── types.go                         # TimelineEntry
│   │   ├── consts.go                        # 类型/状态/时间范围常量
│   │   └── store.go                         # CRUD
│   │
│   ├── storyarc/                            # 叙事弧线
│   │   ├── types.go                         # StoryArc
│   │   ├── consts.go                        # 弧线类型/状态常量
│   │   └── store.go                         # CRUD
│   │
│   ├── location/                            # 地点
│   │   ├── types.go                         # Location
│   │   └── store.go                         # CRUD
│   │
│   ├── reader/                              # 读者认知
│   │   ├── types.go                         # ReaderPerspective
│   │   ├── consts.go                        # type 常量 (known/suspense/misconception)
│   │   └── store.go                         # CRUD
│   │
│   ├── session/                             # 对话会话
│   │   ├── types.go                         # Session, Message, MessageRole
│   │   └── store.go                         # 会话 + 消息持久化
│   │
│   │  ═══════════════════════════════════════
│   │  ║  基础设施包 — 独立功能模块
│   │  ═══════════════════════════════════════
│   │
│   ├── storage/                             # 数据库基础设施
│   │   ├── sqlite.go                        # 连接池、WAL 模式配置
│   │   ├── migrate.go                       # 数据库迁移（嵌入 SQL 文件）
│   │   └── operation_log.go                 # 操作日志（回退数据源）
│   │
│   ├── git/                                 # Git 操作（章节正文版本管理）
│   │   └── repo.go                          # go-git 封装：init, add, commit, log, diff, checkout
│   │
│   ├── llm/                                 # LLM 客户端
│   │   ├── client.go                        # DeepSeek API 调用
│   │   ├── stream.go                        # SSE 流式响应解析
│   │   ├── types.go                         # 可用模型列表、模型能力定义
│   │   └── token_counter.go                 # tiktoken-go 本地 token 计数
│   │
│   ├── rag/                                 # RAG 引擎
│   │   ├── embedder.go                      # Embedder 接口定义
│   │   ├── embedder_onnx.go                 # ONNX Runtime 实现
│   │   ├── embedder_test.go                 # 对比测试：ONNX vs Python 输出一致性
│   │   ├── vector_store.go                  # sqlite-vec 向量存储
│   │   ├── splitter.go                      # 中文文本分块
│   │   └── splitter_test.go                 # 分块测试
│   │
│   ├── context/                             # 上下文构建器
│   │   ├── builder.go                       # 四层上下文构建（STATIC→STABLE→SLIDING→DYNAMIC）
│   │   ├── cache.go                         # TTL 内存缓存
│   │   ├── mmr.go                           # MMR 多样性重排序算法
│   │   └── templates.go                     # System prompt / 各场景 prompt 模板
│   │
│   ├── mcp/                                 # MCP 工具体系（全部工具集中内聚）
│   │   ├── registry.go                      # 工具注册表 + OpenAI Function Calling 格式生成
│   │   ├── base.go                          # Tool 接口 + ToolResult + JSON Schema
│   │   ├── novel_tools.go                   # get_novel_info, get_chapter_list, get_chapter_content,
│   │   │                                      #   get_creative_profile, update_creative_profile,
│   │   │                                      #   create_new_chapter
│   │   ├── character_tools.go               # get_characters, create_character, update_character,
│   │   │                                      #   update_character_relationship
│   │   ├── editing_tools.go                 # edit_chapter
│   │   ├── memory_tools.go                  # search_story_memory, get_character_memory
│   │   ├── timeline_tools.go                # get_timeline, add_timeline_entry, update_timeline_entry
│   │   ├── story_arc_tools.go               # get_story_arcs, add_story_arc, update_story_arc
│   │   ├── location_tools.go                # get_locations, add_location, update_location
│   │   ├── reader_perspective_tools.go      # get/add/update reader perspectives
│   │   ├── story_state_tools.go             # get_story_state, update_story_state
│   │   ├── subagent_tools.go                # run_subagent（memory/review 子 Agent 调度）
│   │   └── lint_tools.go                    # lint_chapter
│   │
│   ├── agent/                               # Agent 循环
│   │   └── loop.go                          # 核心事件循环
│   │
│   └── logger/                              # 日志系统
│       └── logger.go                        # slog 初始化、配置
│
├── app/                                     # Wails 桌面壳（薄）
│   └── handler.go                           # 暴露给前端的 Go 方法
│
├── server/                                  # 未来 Web 壳（薄，目前不做）
│   └── handler.go                           # HTTP handler → internal/
│
├── models/                                  # ONNX 模型文件目录
│   └── text2vec-base-chinese.onnx           # 首次运行时下载
│
├── frontend/                                # React 前端（从 Python 版迁移）
│   └── src/
│       ├── pages/
│       ├── components/
│       ├── services/                        # 替换为 Wails 函数调用
│       └── stores/
│
├── data/                                    # 用户数据目录（运行时默认路径）
│   └── my-novel/                            # 每部小说一个目录
│       ├── .git/                            # Git 仓库（go-git 管理章节文件）
│       ├── chapters/                        # 章节正文 .md 文件
│       │   ├── 001.md
│       │   ├── 002.md
│       │   └── ...
│       ├── novel.db                         # SQLite（元数据 + 操作日志 + 向量索引）
│       └── snapshots/                       # 大版本快照（用户手动触发）
│
├── go.mod
├── go.sum
├── wails.json                               # Wails 配置
└── README.md
```

## 六、各模块设计要点

### 6.1 领域包（novel/chapter/character/timeline/storyarc/location/reader/session）

每个领域包遵循相同的结构模式：`types.go`（struct 定义 + 状态常量）+ `store.go`（数据库 CRUD 操作）。特殊之处：chapter 包的 store 只管理元数据（标题、编号、状态、字数、文件路径），正文的读写走 `internal/git/`。

**store.go 签名模式：**

```go
// 示例：领域包 store 的基本签名模式

type Store struct {
    db     *sql.DB
    logger *slog.Logger
}

func NewStore(db *sql.DB, logger *slog.Logger) *Store { ... }

// 每个 store 至少提供这四个方法
func (s *Store) GetByID(ctx context.Context, id int64) (*T, error)
func (s *Store) List(ctx context.Context, novelID int64) ([]T, error)
func (s *Store) Create(ctx context.Context, entity *T) error
func (s *Store) Update(ctx context.Context, entity *T) error
```

Go 的 struct 同时承担了 Python 里 SQLAlchemy Model 和 Pydantic Schema 的双重职责——JSON tag 负责序列化，ORM 映射由 sqlc 或手写 SQL 处理。不需要分 models.py 和 schemas.py 两套。

### 6.2 storage/ — 数据库基础设施

- 使用 `modernc.org/sqlite`（纯 Go，无 CGO 编译依赖）
- WAL 模式支持一写多读并发
- 迁移系统：嵌入 SQL 文件，启动时按版本号顺序执行
- `operation_log.go`：统一的写操作拦截层，所有写操作经由此层记录 Delta
- 各领域包的 Store 各自持有 `*sql.DB`，通过依赖注入传入

### 6.3 llm/ — LLM 客户端

不引入 go-openai 或其他 LLM 抽象库。直接用 `net/http` 发 POST，自己解析 SSE 流。主流国产大模型的 API 都是 OpenAI 兼容格式，base URL 和 API key 一换就能用。

**Provider 模型——差异用钩子，不提前抽象**

```go
type Provider struct {
    Name    string
    BaseURL string
    Models  []string
    APIKey  string

    // 差异钩子，nil 就走默认 OpenAI 兼容逻辑
    BuildRequest  func(payload map[string]any) map[string]any  // 修改请求体
    BuildHeaders  func(base map[string]string) map[string]string
    ParseError    func(body []byte) error                       // 非标准错误格式
    ParseStream   func(line []byte) (StreamEvent, error)        // 非标准 SSE 行
}
```

内置 DeepSeek 和 GLM 两个 Provider（DeepSeek 的 `thinking` 参数通过 `BuildRequest` 注入，GLM 的 `BuildRequest` 移除不支持的参数）。用户可以在 UI 中添加/编辑/删除 Provider。

默认逻辑覆盖 90% 的 provider：`/v1/chat/completions` 的 chat endpoint、标准 JSON 请求/响应、标准 SSE 流。遇到真正不兼容的再给对应钩子写函数。不为了"未来可能"提前引入抽象层。

### 6.4 rag/ — RAG 引擎

**Embedder 接口：**

```go
type Embedder interface {
    Embed(ctx context.Context, text string) ([]float32, error)
    EmbedBatch(ctx context.Context, texts []string) ([][]float32, error)
    Dim() int
    Close() error
}
```

**ONNX 实现要点：**
- 首次运行时自动下载 ONNX 模型文件（约 100MB）
- 使用 `github.com/yalue/onnxruntime_go` 加载模型
- 实现 BERT tokenizer（约 300 行 Go 代码，核心是 vocab 查表 + WordPiece 分词）
- 前向传播后做 mean pooling（取所有 token 的平均向量）
- `embedder_test.go` 内置黄金测试：同文本在 Python 和 Go 下的输出向量 cos sim > 0.999

**向量存储：**
- 使用 sqlite-vec 扩展，向量存在同一 SQLite 文件中
- 每部小说一张向量表，按 chapter_id 索引
- 支持 metadata 过滤（按章节 ID、chunk 类型筛选）

### 6.5 mcp/ — MCP 工具体系（集中内聚）

所有工具在一个包内，共享 registry、base types、JSON Schema 生成。

**核心接口：**

```go
// mcp/base.go
type Tool interface {
    Name() string
    Description() string
    JSONSchema() json.RawMessage
    Execute(ctx context.Context, args map[string]any, execCtx ToolContext) (*ToolResult, error)
}

type ToolContext struct {
    DB        *sql.DB
    NovelID   int64
    SessionID string
    OpLog     OperationLogger
    Logger    *slog.Logger
}

type ToolResult struct {
    Success  bool
    Data     any
    Error    string
    Metadata map[string]any
    Inject   []InjectMessage
}
```

**工具分文件组织（一个文件一类工具）：**

```
mcp/
├── registry.go              # 注册所有工具，生成 OpenAI Function Calling 列表
├── base.go                  # 接口、类型、JSON Schema 生成
├── novel_tools.go           # 小说管理工具（6 个工具）
├── character_tools.go       # 角色管理工具（4 个工具）  
├── editing_tools.go         # 编辑工具（1 个复合工具，多 change_type）
├── memory_tools.go          # 记忆检索工具（2 个工具）
├── timeline_tools.go        # 时间线工具（3 个工具）
├── story_arc_tools.go       # 弧线工具（3 个工具）
├── location_tools.go        # 地点工具（3 个工具）
├── reader_perspective_tools.go
├── story_state_tools.go     # 故事状态工具（2 个工具）
├── subagent_tools.go        # 子 Agent 调度（1 个工具）
└── lint_tools.go            # 文本检查（1 个工具）
```

### 6.6 agent/ — Agent 循环

从 Python `agent_loop.py` 翻译，核心逻辑不变：

1. 构建消息列表（system1 + system2 + history + 当前用户消息）
2. 调用 LLM（流式 SSE）
3. 解析响应：有 tool_calls → 执行工具 → 结果追加到消息 → 回到 2
4. 直到 LLM 不再调用工具，或达到 max_turns（50）
5. 取消机制：`context.Context` cancel 传播

### 6.7 编辑审批与回退

#### 即时审批（逐条 accept/reject）

每次 AI 调用 edit_chapter 修改章节文件后，前端展示差异预览（git diff），用户当场决定：

- **accept** → 变更保留，继续
- **reject** → 撤销该次编辑（edit_chapter 工具内执行 search_replace 的反向操作，恢复原文本）

全自动模式即跳过确认步骤，每次默认 accept。

DB 写操作（创建角色、加伏笔等）不经过审批，直接生效。这些操作产生的是辅助元数据，本身不构成"正文对不对"的决策核心。

#### Turn 级回退（事后撤销整轮对话）

事后想撤销某一轮对话的全部改动：

- **章节正文**：`git revert` 定位该 turn 的 commit，文件级回退
- **DB 元数据**：操作日志按 turn_id 筛选，倒序逆向执行（见下方操作日志）

#### 用户编辑提交时机

用户在编辑器中手动修改章节正文后，编辑内容会 autosave 到文件（不 commit）。**commit 的触发点是用户给 AI 发消息**——发消息意味着用户说"我改完了，该你了"，这是一个自然的变更边界。

具体流程：
1. 用户在编辑器中编辑章节（autosave 到 `.md` 文件，不 commit）
2. 用户输入消息，发送给 AI
3. 发送前：检查 Git 工作区是否有未提交变更 → 有则 `git add + commit`（消息如 `turn N pre-edit: user manual changes`）
4. 然后进入 agent loop，AI 可能调用 edit_chapter 等工具继续修改
5. Turn 结束时：再次 commit（消息如 `turn N: AI changes`），一个 turn 产生 0-2 个 commit

特殊情况：用户只发消息、没改文件 → 跳过第 3 步，turn 结束时如果 AI 有改动才 commit。

编辑器本身支持纯本地 undo/redo（Monaco 内置 `Ctrl+Z` / `Ctrl+Y`），跟踪每次键入，与 AI 审批和 turn 回退完全独立——两套撤销系统各管各的，互不干扰。

| | 编辑器 undo/redo | AI 审批 | Turn 回退 |
|---|---|---|---|
| 粒度 | 每次键入 | 每次 edit_chapter | 整个 turn |
| 范围 | 当前文件 | 正文（单次编辑） | 正文 + DB 元数据 |
| 实现 | Monaco 内置 | git diff + 反向操作 | git revert + 操作日志逆向 |
| 跨文件 | 不支持 | 不支持 | 支持 |

#### 操作日志（所有元数据写操作的拦截层）

每个写操作执行前 SELECT 旧值，执行后记录新旧差异到 `storage/operation_log.go` 的操作日志表。日志表记录：TurnID、SessionID、实体类型、实体ID、字段路径、OldValue（JSON，nil=新建）、NewValue（JSON，nil=删除）。回退时按 TurnID 查出所有记录，倒序逆向执行。

#### 实施策略

操作日志基础设施从阶段一就建好，确保后续可无缝接入。回退功能暂不在 MVP 中实现。

## 七、从 Python 版继承的设计

以下设计直接继承自 Python 版，Go 版保持相同的算法和策略：

1. **四层上下文缓存**（STATIC → STABLE → SLIDING → DYNAMIC），TTL 缓存策略
2. **MMR 多样性重排序算法**（λ=0.7，词重叠 Jaccard 相似度）
3. **中文文本分块算法**（500 字/chunk，50 字重叠，段落/句子边界感知）
4. **MCP 工具分类**（NOVEL_MANAGEMENT / WRITING_ASSISTANT / MEMORY_RETRIEVAL / EDIT_TOOLS）
5. **子 Agent 系统**（memory/review，独立 tool 白名单 + system prompt）
6. **创作配置双层模型**（全局用户偏好 + 单书偏好，增量合并）
7. **意图检测关键词**（创作意图、长期规则识别）

## 八、未来 Web 版兼容设计

当前 `internal/` 不依赖任何传输层。做 Web 版时只需新增 `server/` 目录，包一层 HTTP：

```
server/
├── router.go            # chi/gin router
├── handler_chat.go      # POST /chat → internal/agent.Loop(...)
├── handler_novel.go     # CRUD → internal/novel.Store
├── ws.go                # WebSocket → internal/agent.LoopStream(...)
└── middleware.go         # auth, logging
```

`app/`（Wails）和 `server/`（HTTP）共享同一个 `internal/`。差异化在于谁帮你管基础设施（文件路径、配置加载、用户身份），不在核心逻辑。

## 九、实施路线

### 阶段一：基础设施（1-2 周）

- [ ] `cmd/novel/main.go` — Wails 入口
- [ ] `internal/logger/` — slog 初始化
- [ ] `internal/novel/`, `chapter/`, `character/`, `timeline/`, `storyarc/`, `location/`, `reader/`, `session/` — 全部 types.go
- [ ] `internal/storage/sqlite.go` + `migrate.go` — 数据库初始化与迁移
- [ ] `go.mod` — 依赖管理

### 阶段二：核心能力（2-3 周）

- [ ] 全部领域包的 `store.go` — CRUD
- [ ] `internal/llm/` — LLM 客户端 + 流式
- [ ] `internal/rag/embedder_onnx.go` — ONNX embedding（含 tokenizer）
- [ ] `internal/rag/vector_store.go` — sqlite-vec 向量存储
- [ ] `internal/rag/splitter.go` — 文本分块

### 阶段三：上层逻辑（2-3 周）

- [ ] `internal/context/` — 上下文构建 + 缓存 + MMR
- [ ] `internal/mcp/` — 全部工具实现
- [ ] `internal/agent/loop.go` — Agent 循环
- [ ] `internal/storage/operation_log.go` — 操作日志基础设施

### 阶段四：应用层（1-2 周）

- [ ] `app/handler.go` — Wails 绑定
- [ ] 前端适配（axios → Wails 函数调用）

### 阶段五：验证与打磨（1-2 周）

- [ ] `embedder_test.go` — ONNX vs Python 输出一致性对比
- [ ] 端到端测试
- [ ] 性能优化
- [ ] 打包脚本（NSIS / create-dmg / AppImage）

## 十、风险与对策

| 风险 | 概率 | 对策 |
|------|------|------|
| ONNX Runtime Go 绑定不稳定 | 中 | 先验证 `yalue/onnxruntime_go` 可用性；备选：通过 C 子进程调用 ONNX |
| BERT tokenizer 实现出错 | 中 | 大量对比测试（Python vs Go，同一输入输出） |
| sqlite-vec 在 WAL 模式下行为异常 | 低 | 阅读 sqlite-vec 源码确认；备选：独立向量索引文件 |
| Wails 前端适配工作量超预期 | 低 | 前端已有完整 React 代码，只需改调用层 |
| 3000 万字规模下 SQLite 性能不够 | 低 | SQLite 单表千万行无压力；按 novel_id 分区索引即可 |

## 十一、不做的

- 不做 WebSocket（Wails IPC 替代；未来 Web 版再加）
- 不做多用户认证（单用户本地应用）
- 不做 Redis 缓存（内存 TTL cache 替代）
- 不做 LangGraph 工作流（简单 goroutine 状态机替代）
- 不做 MySQL（SQLite 替代）
- 不做 ChromaDB（sqlite-vec 替代）
- 不做 HTTP REST API（`server/` 目录预留，暂不实现）

## 十二、关键 Go 依赖

```
github.com/wailsapp/wails/v3          // 桌面应用框架
modernc.org/sqlite                    // 纯 Go SQLite（无 CGO）
github.com/asg017/sqlite-vec-go       // 向量搜索扩展
github.com/go-git/go-git/v5           // 纯 Go Git 实现
github.com/yalue/onnxruntime_go       // ONNX Runtime Go 绑定
github.com/pkoukk/tiktoken-go         // Token 计数
（net/http 标准库做 HTTP/SSE，不自建 LLM 客户端依赖）
```

## 十三、Python → Go 术语对照

| Python | Go |
|--------|-----|
| `pydantic.BaseModel` | `struct` + `encoding/json` tag（同时承担 ORM 映射） |
| `models.py` + `schemas.py` | `types.go`（一个文件，一套 struct） |
| `async def` / `await` | goroutine + channel |
| `AsyncSession` | `*sql.DB` + `context.Context` |
| `logging.getLogger(__name__)` | `slog.Logger`（注入，不从包名获取） |
| `__init__.py` | 包名即目录名，无需 `__init__` |
| `try/except` | `if err != nil` |
| `@router.post("/path")` | Wails 绑定方法 `func (a *App) DoSomething(...)` |
| `TypeVar` / 泛型 | Go 1.18+ generics |
| `list[dict[str, Any]]` | `[]map[string]any` |
