# AI小说生成系统 - CI/CD完整引入计划

## 📋 项目概述

### 当前状态
- **项目类型**: FastAPI异步后端 + React前端（AI小说创作系统）
- **技术栈**: FastAPI + MySQL + Redis + ChromaDB + DeepSeek API
- **API规模**: 17个功能模块，108+个RESTful端点 + WebSocket
- **测试现状**: ❌ 零测试文件、❌ 无CI配置、❌ 无lint配置
- **代码规范**: 已有PEP 8要求，但未强制执行

### 核心目标
1. ✅ 引入完整的Lint检查（ruff）
2. ✅ 建立全覆盖的API集成测试体系（350-400个用例）
3. ✅ 配置GitHub Actions CI流水线（单Job串行）
4. ✅ 确保每个后端端点都有对应测试
5. ⏭️ 跳过transformers依赖（向量化token分词不启动）

---

## 🎯 实施范围

### 必须覆盖的17个模块

| 模块 | 端点数 | 测试优先级 | 特殊处理 |
|------|--------|------------|----------|
| auth | 4 | P0 (最高) | 基础认证 |
| novels | 7 | P0 | CRUD基础 |
| chapters | 7 | P0 | 编辑会话联动 |
| characters | 11 | P1 | 关系网络复杂 |
| locations | 6 | P1 | 地点层级关系 |
| plot_events | 5 | P1 | 情节关联 |
| sessions | 12 | P1 | 会话生命周期 |
| editor | 8 | P1 | 副本编辑机制 |
| timeline | 10 | P1 | 时间线管理 |
| planning | 16 | P2 | 规划系统复杂 |
| generation | 7 | P2 | **需Mock LLM** |
| memory | 4 | P2 | ChromaDB依赖 |
| rag | 4 | P2 | ChromaDB依赖 |
| agents | 4 | P2 | **需Mock LLM** |
| consistency | 3 | P2 | 一致性检查 |
| mcp | - | P3 | 可选跳过 |
| ws_chat | - | P3 | WebSocket可选 |

---

## 📦 实施步骤（共6大阶段）

---

## Phase 1: 基础设施搭建 ⭐⭐⭐

### Step 1.1: 创建测试依赖配置文件
**文件**: `requirements-test.txt`

**内容**:
```
pytest==8.0.0
pytest-asyncio==23.2.1
pytest-cov==4.1.0
httpx==0.26.0
ruff==0.2.1
mypy==1.8.0
pytest-ordering==0.9
pytest-xdist==3.5.0  # 并发测试(可选)
```

**操作**:
- [ ] 在项目根目录创建 `requirements-test.txt`
- [ ] 包含上述所有依赖及版本号
- [ ] 排除 transformers 相关依赖

---

### Step 1.2: 配置pytest
**文件**: `backend/pytest.ini` 或 `backend/pyproject.toml`

**内容**:
```ini
[pytest]
asyncio_mode = auto
testpaths = tests
python_files = test_*.py
python_classes = Test*
python_functions = test_*
addopts = 
    -v
    --tb=short
    --cov=app
    --cov-report=term-missing
    --cov-report=html:htmlcov
    --cov-fail-under=70
filterwarnings =
    ignore::DeprecationWarning
    ignore::PendingDeprecationWarning
markers =
    slow: marks tests as slow (deselect with '-m "not slow"')
    integration: marks tests as integration tests
    mock_llm: marks tests that mock LLM calls
```

**操作**:
- [ ] 在 `backend/` 目录创建 pytest 配置
- [ ] 配置异步模式为 auto
- [ ] 设置覆盖率阈值和报告格式
- [ ] 定义自定义markers用于分类

---

### Step 1.3: 创建全局测试fixtures
**文件**: `backend/tests/conftest.py`

**核心Fixtures清单**:

```python
# 1. 数据库fixture
@pytest.fixture(scope="session")
async def db_engine():
    """创建测试数据库引擎"""
    # 使用TEST_DATABASE_URL环境变量
    # 自动创建所有表
    yield engine
    # 清理：删除所有表

@pytest.fixture(scope="function")
async def db_session(db_engine):
    """每个测试函数独立的数据库会话"""
    # 开启事务
    # 测试结束后回滚
    yield session
    await session.rollback()

# 2. 认证fixture
@pytest.fixture
async def test_user(db_session):
    """创建测试用户并返回用户对象"""
    user = User(username="testuser", email="test@example.com", ...)
    db_session.add(user)
    await db_session.commit()
    return user

@pytest.fixture
async def auth_headers(test_user):
    """返回带JWT token的请求头"""
    tokens = create_tokens(test_user.id, ...)
    return {"Authorization": f"Bearer {tokens['access_token']}"}

# 3. 业务数据fixtures
@pytest.fixture
async def test_novel(db_session, test_user):
    """创建测试小说"""
    novel = Novel(title="测试小说", author_id=test_user.id, ...)
    db_session.add(novel)
    await db_session.commit()
    return novel

@pytest.fixture
async def test_chapter(db_session, test_novel):
    """创建测试章节"""
    chapter = Chapter(novel_id=test_novel.id, chapter_number=1, ...)
    db_session.add(chapter)
    await db_session.commit()
    return chapter

# 4. HTTP客户端fixture
@pytest.fixture
async def client():
    """FastAPI测试客户端"""
    from app.main import app
    async with httpx.AsyncClient(
        app=app,
        base_url="http://testserver"
    ) as ac:
        yield ac

# 5. Mock fixtures
@pytest.fixture(autouse=True)
def mock_llm_service(monkeypatch):
    """自动Mock所有LLM调用（可选启用）"""
    pass  # 在需要时激活
```

**操作**:
- [ ] 创建 `backend/tests/__init__.py`
- [ ] 创建 `backend/tests/conftest.py`
- [ ] 实现数据库连接池管理
- [ ] 实现用户认证流程fixture
- [ ] 实现业务数据工厂方法
- [ ] 实现HTTP客户端封装
- [ ] 实现LLM Mock机制

---

### Step 1.4: 创建环境变量模板
**文件**: `backend/tests/.env.test`

**内容**:
```env
DATABASE_URL=mysql+pymysql://root:test_password@127.0.0.1:3306/test_ai_novel
REDIS_URL=redis://127.0.0.1:6379/1
SECRET_KEY=test_secret_key_for_ci_only
ALGORITHM=HS256
ACCESS_TOKEN_EXPIRE_MINUTES=30
DEEPSEEK_API_KEY=fake_key_for_testing
DEEPSEEK_API_BASE=http://localhost:9999
HF_ENDPOINT=https://hf-mirror.com
CHROMA_HOST=localhost
CHROMA_PORT=8001
```

**操作**:
- [ ] 创建测试专用环境变量文件
- [ ] 使用独立的数据库名称避免冲突
- [ ] 使用独立的Redis数据库编号

---

## Phase 2: GitHub Actions CI配置 ⭐⭐⭐

### Step 2.1: 创建主CI工作流文件
**文件**: `.github/workflows/ci.yml`

**完整结构**:

```yaml
name: Backend CI Pipeline

on:
  push:
    branches: [main, develop, feature/*]
  pull_request:
    branches: [main, develop]

concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true

jobs:
  test:
    name: Lint & Test
    runs-on: ubuntu-latest
    
    # 真实服务容器
    services:
      mysql:
        image: mysql:8.0
        env:
          MYSQL_ROOT_PASSWORD: test_password
          MYSQL_DATABASE: test_ai_novel
        ports:
          - 3306:3306
        options: >-
          --health-cmd="mysqladmin ping -h localhost"
          --health-interval=10s
          --health-timeout=5s
          --health-retries=10
      
      redis:
        image: redis:7-alpine
        ports:
          - 6379:6379
        options: >-
          --health-cmd="redis-cli ping"
          --health-interval=10s
          --health-timeout=5s
          --health-retries=5
      
      chromadb:
        image: chromadb/chroma:0.4.22
        ports:
          - 8001:8000
        options: >-
          --health-cmd="curl -f http://localhost:8000/api/v1/heartbeat || exit 1"
          --health-interval=15s
          --health-timeout=10s
          --health-retries=10

    steps:
      # === Step 1: 代码检出 ===
      - name: Checkout code
        uses: actions/checkout@v4

      # === Step 2: Python环境准备 ===
      - name: Set up Python
        uses: actions/setup-python@v5
        with:
          python-version: '3.11'
          cache: 'pip'

      # === Step 3: 安装依赖 ===
      - name: Install dependencies
        run: |
          python -m pip install --upgrade pip
          pip install -r backend/requirements.txt
          pip install -r requirements-test.txt

      # === Step 4: Lint检查 ===
      - name: Run Ruff Linter
        run: |
          cd backend
          ruff check app/ --output-format=github
          ruff format --check app/

      # === Step 5: 类型检查(可选) ===
      - name: Run MyPy (optional)
        run: |
          cd backend
          mypy app/ --ignore-missing-imports || true
        continue-on-error: true

      # === Step 6: 等待服务就绪 ===
      - name: Wait for services to be ready
        run: |
          echo "Waiting for MySQL..."
          while ! mysqladmin ping -h"127.0.0.1" --silent; do
            sleep 1
          done
          echo "MySQL is ready!"
          
          echo "Waiting for Redis..."
          while ! redis-cli ping | grep -q PONG; do
            sleep 1
          done
          echo "Redis is ready!"

      # === Step 7: 运行测试 ===
      - name: Run Tests
        working-directory: backend
        env:
          DATABASE_URL: mysql+pymysql://root:test_password@127.0.0.1:3306/test_ai_novel
          REDIS_URL: redis://127.0.0.1:6379/1
          SECRET_KEY: test_secret_key_for_ci_pipeline_1234567890
          DEEPSEEK_API_KEY: fake_api_key_for_testing_no_real_calls
          CHROMA_HOST: localhost
          CHROMA_PORT: 8001
        run: |
          pytest tests/ -v \
            --junitxml=junit.xml \
            --cov-report=xml \
            --cov-report=term-missing

      # === Step 8: 上传覆盖率报告 ===
      - name: Upload coverage reports
        uses: actions/upload-artifact@v4
        if: always()
        with:
          name: coverage-report
          path: |
            backend/coverage.xml
            backend/htmlcov/
            backend/junit.xml

      # === Step 9: 上传测试结果 ===
      - name: Publish Test Results
        uses: EnricoMi/publish-unit-test-result-action@v2
        if: always()
        with:
          files: backend/junit.xml

      # === Step 10: PR注释(可选) ===
      - name: Comment PR with coverage
        if: github.event_name == 'pull_request'
        uses: madrapps/jacoco-badge-generator@master
        with:
          coverage-report-path: backend/coverage.xml
```

**关键特性**:
- ✅ 单Job串行执行（符合要求）
- ✅ MySQL + Redis + ChromaDB真实服务
- ✅ 服务健康检查等待机制
- ✅ 缓存pip依赖加速
- ✅ 并发控制（同一PR多次push取消旧任务）
- ✅ 测试结果和覆盖率上传
- ✅ PR自动评论展示覆盖率badge

**操作**:
- [ ] 创建 `.github/workflows/ci.yml`
- [ ] 配置3个服务容器(MySQL 8.0, Redis 7, ChromaDB 0.4.22)
- [ ] 设置健康检查和超时参数
- [ ] 配置环境变量注入
- [ ] 配置缓存策略
- [ ] 配置产物上传和报告生成

---

### Step 2.2: 创建Ruff配置文件
**文件**: `backend/ruff.toml` 或 `backend/pyproject.toml` (section)

**内容**:
```toml
[tool.r]
target-version = "py311"
line-length = 120

[tool.r.lint]
select = [
    "E",    # pycodestyle errors
    "W",    # pycodestyle warnings
    "F",    # Pyflakes
    "I",    # isort
    "B",    # flake8-bugbear
    "C4",   # flake8-comprehensions
    "UP",   # pyupgrade
    "ARG",  # flake8-unused-arguments
    "SIM",  # flake8-simplify
]
ignore = [
    "E501",  # line too long (handled by formatter)
    "B008",  # do not perform function calls in argument defaults
    "B904",  # within an except clause, raise exceptions with ...
    "C901",  # too complex
]

[tool.r.lint.per-file-ignores]
"__init__.py" = ["F401"]
"tests/*" = ["ARG001"]

[tool.r.format]
quote-style = "double"
indent-style = "space"
docstring-code-format = true
```

**操作**:
- [ ] 创建Ruff配置文件
- [ ] 选择合适的规则集（平衡严格度和实用性）
- [ ] 为测试文件设置宽松规则

---

## Phase 3: 核心模块测试实现 ⭐⭐⭐

### Step 3.1: Auth模块测试
**文件**: `backend/tests/test_auth.py`

**测试用例列表** (预计15个):

```python
class TestUserRegistration:
    async def test_register_success(self, client): ...
    async def test_register_duplicate_username(self, client, test_user): ...
    async def test_register_duplicate_email(self, client, test_user): ...
    async def test_register_invalid_email_format(self, client): ...
    async def test_register_short_password(self, client): ...

class TestUserLogin:
    async def test_login_success(self, client, test_user): ...
    async def test_login_wrong_password(self, client, test_user): ...
    async def test_login_nonexistent_user(self, client): ...
    async def test_login_missing_fields(self, client): ...

class TestTokenRefresh:
    async def test_refresh_token_success(self, client, test_user): ...
    async def test_refresh_invalid_token(self, client): ...
    async def test_refresh_expired_token(self, client): ...
    async def test_refresh_access_token_type_rejected(self, client, test_user): ...

class TestGetCurrentUser:
    async def test_get_me_success(self, client, auth_headers): ...
    async def test_get_me_unauthorized(self, client): ...
    async def test_get_me_invalid_token(self, client): ...
```

**操作**:
- [ ] 实现4个大类16个测试方法
- [ ] 覆盖正常流程和异常场景
- [ ] 测试请求参数校验
- [ ] 测试Token有效性验证

---

### Step 3.2: Novels模块测试
**文件**: `backend/tests/test_novels.py`

**测试用例列表** (预计25个):

```python
class TestListNovels:
    async def test_list_empty(self, client, auth_headers): ...
    async def test_list_with_data(self, client, auth_headers, test_novel): ...
    async def test_list_pagination(self, client, auth_headers, multiple_novels): ...
    async def test_list_filter_by_status(self, client, auth_headers): ...
    async def test_list_filter_by_genre(self, client, auth_headers): ...
    async def test_list_search_by_title(self, client, auth_headers): ...
    async def test_list_cache_hit(self, client, auth_headers, test_novel): ...

class TestCreateNovel:
    async def test_create_success(self, client, auth_headers): ...
    async def test_create_missing_title(self, client, auth_headers): ...
    async def test_create_with_all_fields(self, client, auth_headers): ...
    async def test_create_unauthorized(self, client): ...

class TestGetNovelDetail:
    async def test_get_success(self, client, auth_headers, test_novel): ...
    async def test_get_not_found(self, client, auth_headers): ...
    async def test_get_not_owner(self, client, other_user_auth, test_novel): ...
    async def test_get_cache_hit(self, client, auth_headers, test_novel): ...

class TestUpdateNovel:
    async def test_update_partial(self, client, auth_headers, test_novel): ...
    async def test_update_full(self, client, auth_headers, test_novel): ...
    async def test_update_not_found(self, client, auth_headers): ...
    async def test_update_clears_cache(self, client, auth_headers, test_novel): ...

class TestDeleteNovel:
    async def test_delete_success(self, client, auth_headers, test_novel): ...
    async def test_delete_not_found(self, client, auth_headers): ...
    async def test_delete_cascading_cache_clear(self, client, auth_headers, test_novel): ...

class TestCreativeProfile:
    async def test_get_default_profile(self, client, auth_headers, test_novel): ...
    async def test_create_and_update_profile(self, client, auth_headers, test_novel): ...
```

**操作**:
- [ ] 实现6大类25+个测试方法
- [ ] 测试CRUD完整生命周期
- [ ] 测试权限验证（作者校验）
- [ ] 测试Redis缓存行为
- [ ] 测试分页和筛选逻辑

---

### Step 3.3: Chapters模块测试
**文件**: `backend/tests/test_chapters.py`

**测试用例列表** (预计30个):

重点测试：
- 章节号自动递增逻辑
- 章节号重复检测
- 协作编辑模式（collaborative=True）
- EditSession联动
- 字数统计更新
- 内容变更时的缓存失效

**操作**:
- [ ] 实现30个测试方法
- [ ] 重点测试编辑会话集成
- [ ] 测试章节号冲突处理
- [ ] 测试协作编辑分支逻辑

---

### Step 3.4: Characters模块测试
**文件**: `backend/tests/test_characters.py`

**测试用例列表** (预计35个):

重点测试：
- 角色CRUD基础（12个）
- 角色关系管理（15个）：
  - 创建关系（含source=target校验）
  - 更新关系
  - 关系演变（evolve，旧关系标记dormant）
  - 关系网络图查询
  - 单角色关系查询
- 权限验证（8个）

**操作**:
- [ ] 实现角色基础CRUD测试
- [ ] 完整的关系子系统测试
- [ ] 测试关系演变的状态机逻辑
- [ ] 测试网络图数据结构正确性

---

## Phase 4: 扩展模块测试 ⭐⭐

### Step 4.1: Locations模块测试
**文件**: `backend/tests/test_locations.py` (~20个测试)

测试要点：
- 地点层级关系(parent_location_id)
- 地点网络图
- 搜索和筛选

**操作**:
- [ ] 实现20个测试用例
- [ ] 测试地点父子关系完整性

---

### Step 4.2: PlotEvents模块测试
**文件**: `backend/tests/test_plot_events.py` (~20个测试)

测试要点：
- 情节事件与章节关联
- 与角色关联(characters_involved)
- 时间线字段(timeline)

**操作**:
- [ ] 实现20个测试用例

---

### Step 4.3: Sessions模块测试
**文件**: `backend/tests/test_sessions.py` (~35个测试)

测试要点：
- 三种作用域(novel/chapters/chapter)
- 会话生命周期(create→use→delete)
- 消息管理(clear/history)
- 上下文更新(novel_context/chapter_context)
- 权限隔离(用户A不能访问用户B的会话)

**操作**:
- [ ] 实现35个测试用例
- [ ] 测试作用域切换逻辑
- [ ] 测试消息持久化和清理

---

### Step 4.4: Editor模块测试
**文件**: `backend/tests/test_editor.py` (~25个测试)

测试要点：
- 编辑会话创建(start)
- 变更应用(apply) - 支持full_replace/partial_edit/insert/delete
- 变更接受(accept) - 写入原章节
- 变更拒绝(reject) - 回滚
- Diff计算和展示
- 并发保护（同一章节只有一个活跃edit session）

**操作**:
- [ ] 实现25个测试用例
- [ ] 测试副本机制的完整流程
- [ ] 测试diff算法输出格式

---

### Step 4.5: Timeline模块测试
**文件**: `backend/tests/test_timeline.py` (~30个测试)

测试要点：
- 时间线条目CRUD (10个)
- 状态更新(resolve) (3个)
- 分类筛选(category: foreshadowing/plot/character/worldbuilding) (5个)
- 统计信息(stats) (2个)
- 自动提取(auto-extract) (3个)
- 生成上下文(context_for_generation) (3个)
- 搜索和排序 (4个)

**操作**:
- [ ] 实现30个测试用例
- [ ] 测试伏笔管理的完整生命周期
- [ ] 测试自动提取逻辑（可能需要mock）

---

### Step 4.6: Planning模块测试
**文件**: `backend/tests/test_planning.py` (~40个测试)

测试要点：
- 大纲管理(CRUD) (6个)
- 情节线管理(CRUD + 列表) (10个)
- 情节节点管理(CRUD + 完成) (12个)
- 情节建议生成(suggestions) - **需Mock LLM** (3个)
- 进度分析(progress) (2个)
- 章节节点映射 (3个)
- 所有权验证 (4个)

**操作**:
- [ ] 实现40个测试用例
- [ ] Mock情节建议生成的LLM调用
- [ ] 测试情节线-节点的层级关系

---

## Phase 5: 特殊处理模块 ⭐

### Step 5.1: Generation模块测试 (Mock LLM)
**文件**: `backend/tests/test_generation.py` (~20个测试)

**Mock策略**:
```python
@pytest.fixture(autouse=True)
def mock_deepseek_api(monkeypatch):
    """Mock DeepSeek API调用"""
    async def mock_generate(*args, **kwargs):
        return {
            "choices": [{
                "message": {
                    "content": "这是AI生成的模拟文本内容，用于测试目的。"*50
                }
            }]
        }
    
    monkeypatch.setattr("openai.AsyncOpenAI.chat.completions.create", mock_generate)
    monkeypatch.setattr("app.core.llm_service.llm_service._call_openai", mock_generate)
```

测试要点：
- HTTP生成接口(generate) - 提交后台任务 (3个)
- 任务状态查询(tasks/{id}) (2个)
- 章节生成状态(chapters/{num}/status) (3个)
- 模型列表(models) (1个)
- 风格列表(styles) (1个)
- 生成类型定义(types) (1个)
- 后台任务执行验证 (3个)
- 错误处理(LLM不可达) (3个)
- 参数校验 (3个)

**操作**:
- [ ] 创建LLM Mock fixture
- [ ] 实现20个测试用例
- [ ] 验证后台任务的数据库状态变化
- [ ] 测试错误恢复逻辑

---

### Step 5.2: Memory/RAG模块测试 (ChromaDB依赖)
**文件**: `backend/tests/test_memory.py`, `backend/tests/test_rag.py` (~30个测试合计)

**降级策略**:
```python
@pytest.fixture
def skip_if_chroma_unavailable():
    """如果ChromaDB不可用则跳过测试"""
    try:
        import chromadb
        client = chromadb.Client()
        client.heartbeat()
    except Exception:
        pytest.skip("ChromaDB not available")
```

或者使用内存版ChromaDB:
```python
@pytest.fixture(scope="session")
def chroma_client():
    """内存版ChromaDB客户端"""
    import chromadb
    client = chromadb.EphemeralClient()  # 内存存储
    yield client
```

Memory模块测试 (15个):
- 小说索引(index_novel_memory) (3个)
- 单章索引(index_chapter_memory) (4个)
- 语义搜索(search_memory) (4个)
- 索引清除(clear_novel_memory) (2个)
- 错误处理(VectorStoreError) (2个)

RAG模块测试 (15个):
- RAG搜索(search_context) (4个)
- 写作上下文(writing_context) (4个)
- 上下文历史(context_history) (3个)
- 上下文详情(context_detail) (3个)
- 权限验证 (1个)

**操作**:
- [ ] 实现ChromaDB可用性检测
- [ ] 实现30个测试用例
- [ ] 为不可用情况提供优雅降级

---

### Step 5.3: Agents模块测试 (Mock LLM)
**文件**: `backend/tests/test_agents.py` (~15个测试)

测试要点：
- Agent状态查询(status) (1个)
- 任务创建(create_task) - **Mock** (4个)
- 任务列表(get_tasks) (3个)
- 任务详情(task_status) (3个)
- 错误处理(无效task_type) (2个)
- 权限验证 (2个)

**操作**:
- [ ] Mock Agent协调器的execute方法
- [ ] 实现15个测试用例

---

### Step 5.4: Consistency模块测试
**文件**: `backend/tests/test_consistency.py` (~10个测试)

测试要点：
- 一致性检查(check_consistency) (4个)
- 伏笔列表重定向(foreshadowings) (2个)
- 伏笔统计重定向(statistics) (2个)
- 检查类型过滤 (2个)

**操作**:
- [ ] 实现10个测试用例
- [ ] 验证重定向到timeline的逻辑

---

## Phase 6: 收尾和优化 ⭐

### Step 6.1: 健康检查和根路由测试
**文件**: `backend/tests/test_health.py` (3个测试)

```python
async def test_root_endpoint(client):
    resp = await client.get("/")
    assert resp.status_code == 200
    data = resp.json()
    assert data["status"] == "running"

async def test_health_check(client):
    resp = await client.get("/health")
    assert resp.status_code == 200
    data = resp.json()["data"]
    assert data["database"] == "connected"

async def test_docs_accessible(client):
    resp = await client.get("/docs")
    assert resp.status_code == 200
```

**操作**:
- [ ] 实现3个基础测试

---

### Step 6.2: 本地开发辅助脚本
**文件**: `Makefile` 或 `scripts/run_tests.sh`

**Makefile示例**:
```makefile
.PHONY: test lint test-cov clean

help:
	@echo "Available targets:"
	@echo "  test     - Run all tests"
	@echo "  lint     - Run linter and formatter"
	@echo "  test-cov - Run tests with coverage report"
	@echo "  clean    - Remove cache and coverage files"

test:
	cd backend && source ../venv/bin/activate && pytest tests/ -v

lint:
	cd backend && ruff check app/ && ruff format --check app/

test-cov:
	cd backend && source ../venv/bin/activate && pytest tests/ --cov=app --cov-report=html

clean:
	rm -rf backend/htmlcov/ backend/.pytest_cache/ backend/__pycache__
```

**操作**:
- [ ] 创建Makefile或shell脚本
- [ ] 提供便捷的本地测试命令

---

### Step 6.3: 文档和Badge配置
**操作**:
- [ ] 在README.md添加CI Status Badge:
  ```markdown
  ![CI](https://github.com/yourusername/todo/actions/workflows/ci.yml/badge.svg?branch=main)
  ![Coverage](https://img.shields.io/badge/coverage-80%25-green)
  ```
- [ ] 创建 `.gitignore` 条目:
  ```
  htmlcov/
  .pytest_cache/
  .coverage
  *.pyc
  __pycache__/
  ```

---

## 📊 预期成果指标

### 数量化目标
| 指标 | 目标值 | 说明 |
|------|--------|------|
| 总测试用例数 | ≥350 | 覆盖所有108+端点 |
| 代码覆盖率 | ≥80% | 核心模块≥90% |
| API端点覆盖率 | 100% | 每个端点至少1个happy path |
| Lint通过率 | 100% | ruff check零错误 |
| CI执行时间 | <5分钟 | 优化后目标<3分钟 |
| Flaky测试数 | 0 | 连续10次运行稳定 |

### 测试分布统计
- **P0核心模块** (auth/novels/chapters): ~72 tests (20%)
- **P1重要模块** (characters/locations/sessions/editor/timeline): ~138 tests (39%)
- **P2扩展模块** (planning/generation/memory/rag/agents/consistency): ~124 tests (35%)
- **P3边缘模块** (health/mcp/ws_chat): ~16 tests (5%)

---

## ⚠️ 风险点和缓解措施

### 风险1: ChromaDB在CI中不稳定
**影响**: Memory/RAG模块测试失败频繁  
**缓解方案**:
- 使用EphemeralClient(内存版)
- 将这些测试标记为xfail
- 或改为使用SQLite向量存储替代

### 风险2: 异步测试event loop冲突
**影响**: 测试偶发性挂起或报错  
**缓解方案**:
- 强制使用 `asyncio_mode = auto`
- 每个测试函数级别fixture确保cleanup
- 避免共享event loop state

### 风险3: Redis缓存导致测试间干扰
**影响**: 测试A的缓存影响测试B的结果  
**缓解方案**:
- 每个测试前flushdb特定key pattern
- 或使用独立Redis database number
- fixture中添加cache cleanup

### 风险4: LLM Mock不够真实
**影响**: Mock隐藏了真实集成bug  
**缓解方案**:
- 定期运行真实API的smoke test(手动触发)
- Mock返回多样化内容(长文本/短文本/错误响应)
- 记录所有Mocked调用的日志

### 风险5: 数据库迁移缺失
**影响**: 表结构变更导致测试失败  
**缓解方案**:
- 目前SQLAlchemy自动建表(create_all)
- 未来如引入Alembic需同步更新CI
- 在conftest中统一管理schema初始化

---

## 🚀 执行时间估算

| 阶段 | 预计工作量 | 复杂度 |
|------|-----------|--------|
| Phase 1: 基础设施 | 2-3小时 | ⭐⭐ |
| Phase 2: CI配置 | 1-2小时 | ⭐⭐ |
| Phase 3: 核心模块测试 | 4-6小时 | ⭐⭐⭐ |
| Phase 4: 扩展模块测试 | 5-7小时 | ⭐⭐⭐ |
| Phase 5: 特殊模块 | 3-4小时 | ⭐⭐⭐ |
| Phase 6: 收尾优化 | 1小时 | ⭐ |
| **总计** | **16-23小时** | |

---

## ✅ 完成标准 Checklist

当以下所有项都完成时，视为计划成功实施：

- [ ] `requirements-test.txt` 创建完毕且可安装
- [ ] `backend/pytest.ini` 配置完成
- [ ] `backend/tests/conftest.py` 全局fixtures可用
- [ ] `.github/workflows/ci.yml` 配置完成并可触发
- [ ] `backend/ruff.toml` 配置完成
- [ ] 所有17个模块的测试文件已创建
- [ ] 总测试数 ≥350
- [ ] 本地 `make test` 可成功运行
- [ ] CI流水线首次运行通过(绿色✅)
- [ ] 覆盖率报告生成且 ≥80%
- [ ] README.md已添加CI badge

---

## 📝 后续优化方向 (不在本次范围内)

1. **性能测试**: Locust/k6 压测API响应时间
2. **E2E测试**: Playwright/Puppeteer 前后端联调
3. **安全测试**: OWASP ZAP 扫描安全漏洞
4. **多环境CI**: dev/staging/prod 分环境部署流水线
5. **CD自动化**: 合并main自动部署到staging
6. **Pre-commit hooks**: 本地提交前自动lint+test
7. **并行化CI**: 拆分为lint job + test job + security job

---

## 🎓 附录：关键技术决策记录

### 决策1: 为什么选择pytest而非unittest？
- **理由**: 更强大的fixture系统、更好的async支持、更丰富的生态插件
- **备选**: unittest + asyncio.TestCase (更标准但不够灵活)

### 决策2: 为什么选择httpx而非TestClient？
- **理由**: httpx更接近真实HTTP行为、支持async natively、可复用于生产代码
- **备注**: FastAPI的TestClient也是基于httpx封装

### 决策3: 为什么单Job而非多Job？
- **用户要求**: 明确指定"放到一个大job串行就行"
- **优点**: 简单可靠、易于调试、服务容器共享
- **缺点**: 执行时间较长、无法并行加速

### 决策4: 为什么Mock LLM而不是用免费模型？
- **成本考虑**: 即使免费模型也有rate limit
- **稳定性**: 外部API可能导致flaky tests
- **速度**: Mock响应即时返回
- **真实性**: 通过多样化mock内容弥补

### 决策5: ChromaDB的处理方式？
- **首选方案**: EphemeralClient (内存版)
- **备选方案**: Docker service container (真实版)
- **最终选择**: 先尝试Docker，不稳定时fallback到内存版

---

**文档版本**: v1.0  
**创建时间**: 2026-04-12  
**最后更新**: 2026-04-12  
**负责人**: AI Assistant  
**审核状态**: 待确认