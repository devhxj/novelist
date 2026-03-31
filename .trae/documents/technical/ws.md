<br />

***

## 改动说明 - 实时生成API重构

### 架构变更

**核心原则**: 所有LLM生成都走WebSocket实时推送，HTTP作为兜底机制

### 1. WebSocket接口（推荐）

**连接地址**: `ws://host/ws/generation?token=xxx&novel_id=xxx`

**支持的生成类型**:

- `chapter` - 章节生成（集成LangGraph审核+一致性检查）
- `dialogue` - 对话生成
- `description` - 描写生成
- `outline` - 大纲生成
- `summary` - 摘要生成
- `character_profile` - 角色档案生成

**客户端发送消息格式**:

```json
{
  "type": "start_generation",
  "generation_type": "chapter",
  "params": {
    "chapter_number": 1,
    "target_length": 3000,
    "style": "narrative"
  },
  "use_langgraph": true
}
```

**服务端推送消息类型**:

| 类型                     | 说明         | 数据结构                                          |
| :--------------------- | :--------- | :-------------------------------------------- |
| `generation_started`   | 生成开始       | `{task_id, generation_type, novel_id}`        |
| `generation_progress`  | 进度更新       | `{task_id, step, progress, message}`          |
| `content_chunk`        | 内容片段（流式）   | `{task_id, chunk, accumulated_length}`        |
| `review_result`        | 审核结果（仅章节）  | `{task_id, approved, score, issues}`          |
| `consistency_check`    | 一致性检查（仅章节） | `{task_id, passed, issues}`                   |
| `generation_completed` | 生成完成       | `{task_id, content, word_count, chapter_id?}` |
| `generation_failed`    | 生成失败       | `{task_id, error}`                            |

**取消生成**:

```json
{
  "type": "cancel_generation",
  "task_id": "xxx"
}
```

### 2. HTTP兜底接口

**通用生成接口**: `POST /api/v1/generation/novels/{novel_id}/generate`

**请求参数**:

```json
{
  "generation_type": "chapter",
  "params": {
    "chapter_number": 1,
    "target_length": 3000,
    "style": "narrative"
  }
}
```

**响应**:

```json
{
  "success": true,
  "data": {
    "task_id": "http_gen_xxx",
    "generation_type": "chapter",
    "status": "generating",
    "note": "推荐使用WebSocket获取实时进度"
  }
}
```

**查询生成类型**: `GET /api/v1/generation/types`

### 3. 各生成类型参数说明

| 类型                 | 必要参数                       | 可选参数                                   |
| :----------------- | :------------------------- | :------------------------------------- |
| chapter            | -                          | chapter\_number, target\_length, style |
| dialogue           | characters, context        | style                                  |
| description        | subject                    | style                                  |
| outline            | premise, genre             | total\_chapters, style                 |
| summary            | content                    | max\_length                            |
| character\_profile | name, role, novel\_context | style                                  |

### 4. 写作风格选项

- `narrative` - 叙述性
- `descriptive` - 描写性
- `dialogue` - 对话式
- `poetic` - 诗意
- `dramatic` - 戏剧性
- `natural` - 自然
- `vivid` - 生动

### 5. 已删除的旧接口

以下接口已移除，请使用新的WebSocket/HTTP接口：

- `POST /api/v1/text/novels/{novel_id}/generate/chapter` → 改用WebSocket
- `POST /api/v1/workflows/novels/{novel_id}/chapters/{chapter_number}/generate` → 改用WebSocket

### 6. 前端改造建议

1. **新增WebSocket连接管理模块**
   - 建立连接时携带token和novel\_id
   - 处理重连逻辑
   - 管理多个并发生成任务
2. **实时内容展示组件**
   - 监听`content_chunk`消息
   - 实时追加显示生成内容
   - 显示进度条
3. **章节生成流程**
   - 生成完成后显示`review_result`
   - 显示`consistency_check`结果
   - 用户可选择接受或重新生成
4. **HTTP兜底**
   - WebSocket连接失败时降级到HTTP
   - 使用轮询查询章节状态

