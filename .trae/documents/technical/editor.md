vcc

## 📋 前端适配说明 - AI IDE风格小说创作系统

### 一、页面布局设计

```
┌──────────────────────────────────────────────────────────────────────────┐
│  小说详情页 → [开始创作] 按钮 → AI IDE编辑器页面                           │
└──────────────────────────────────────────────────────────────────────────┘

AI IDE编辑器页面布局：
┌─────────────┬────────────────────────────┬─────────────────────────┐
│   左侧栏     │        中间编辑器           │      右侧聊天界面        │
│  (可切换)    │                            │                         │
├─────────────┼────────────────────────────┼─────────────────────────┤
│ ○ 章节列表   │  ┌──────────────────────┐  │  ┌───────────────────┐  │
│ ○ 对话历史   │  │ 第X章 标题           │  │  │ [作用域选择器 ▼]  │  │
│             │  │                      │  │  │ 整本小说 / 1-5章  │  │
│ □ 第1章     │  │   章节内容...        │  │  └───────────────────┘  │
│ □ 第2章     │  │                      │  │                         │
│ □ 第3章     │  │   [修改部分高亮显示]  │  │  用户: 请修改...        │
│ ...         │  │                      │  │                         │
│             │  │                      │  │  AI: 好的，我已经...    │
│             │  └──────────────────────┘  │                         │
│             │  ┌──────────────────────┐  │  [工具调用提示]          │
│             │  │ [✓ 接受] [✗ 拒绝]   │  │                         │
│             │  └──────────────────────┘  │  ┌───────────────────┐  │
│             │                            │  │ [历史对话选择 ▼]  │  │
│             │                            │  └───────────────────┘  │
│             │                            │  ┌───────────────────┐  │
│             │                            │  │ 输入消息...       │  │
│             │                            │  │ [+] [发送]        │  │
│             │                            │  └───────────────────┘  │
└─────────────┴────────────────────────────┴─────────────────────────┘
```

---

### 二、WebSocket连接

**连接地址**：
```javascript
const ws = new WebSocket(`ws://${host}/ws/chat?token=${token}&novel_id=${novelId}`);
```

---

### 三、消息协议

#### 3.1 会话管理

```javascript
// 创建会话
ws.send(JSON.stringify({
  type: 'create_session',
  scope: { 
    type: 'novel',           // 'novel' | 'chapters' | 'chapter'
    chapter_start: 1,        // chapters/chapter时必填
    chapter_end: 5           // chapters时必填
  },
  model: 'deepseek-chat'
}));

// 响应
{ type: 'session_created', session_id: 'sess_xxx', scope: {...}, display_name: '整本小说' }

// 加载会话
ws.send(JSON.stringify({
  type: 'load_session',
  session_id: 'sess_xxx'
}));

// 列出会话
ws.send(JSON.stringify({
  type: 'list_sessions',
  scope_type: 'chapter'  // 可选过滤
}));

// 切换作用域
ws.send(JSON.stringify({
  type: 'change_scope',
  session_id: 'sess_xxx',
  scope: { type: 'chapters', chapter_start: 1, chapter_end: 3 }
}));
```

#### 3.2 对话（支持工具调用）

```javascript
// 发送消息
ws.send(JSON.stringify({
  type: 'chat',
  session_id: 'sess_xxx',
  message: '请帮我修改第三章的对话，让主角更活泼一些',
  tools_enabled: true  // 启用工具调用
}));

// 响应（流式）
{ type: 'content_chunk', chunk: '文本片段...' }
{ type: 'tool_call', tool_name: 'edit_chapter_content', status: 'executing' }
{ type: 'tool_result', tool_name: 'edit_chapter_content', result: {...} }
{ type: 'change_pending', change_id: 'chg_xxx', chapter_id: 1, diff_summary: {...} }
{ type: 'chat_completed', session_id: 'sess_xxx' }
```

#### 3.3 独立生成任务

```javascript
// 生成章节
ws.send(JSON.stringify({
  type: 'generate',
  generation_type: 'chapter',
  params: {
    chapter_number: 1,
    target_length: 3000,
    style: 'narrative',
    chapter_outline: '章节大纲...',
    key_events: ['事件1', '事件2'],
    focus_characters: ['主角', '配角']
  }
}));

// 生成对话
ws.send(JSON.stringify({
  type: 'generate',
  generation_type: 'dialogue',
  params: {
    characters: ['主角', '配角'],
    context: '两人在咖啡馆相遇',
    style: 'natural'
  }
}));

// 生成描写
ws.send(JSON.stringify({
  type: 'generate',
  generation_type: 'description',
  params: {
    subject: '夜晚的城市街道',
    style: 'vivid'
  }
}));

// 响应
{ type: 'generation_started', task_id: 'xxx' }
{ type: 'generation_progress', progress: 50, message: '已生成 1500 字' }
{ type: 'content_chunk', chunk: '文本片段...' }
{ type: 'generation_completed', content: '...', chapter_id: 1 }
```

#### 3.4 编辑操作

```javascript
// 读取章节
ws.send(JSON.stringify({
  type: 'read_chapter',
  chapter_id: 1
}));

// 响应
{ type: 'chapter_content', chapter_id: 1, content: '...', word_count: 3000 }

// 编辑章节（AI调用或用户触发）
ws.send(JSON.stringify({
  type: 'edit_chapter',
  session_id: 'sess_xxx',
  chapter_id: 1,
  change_type: 'partial_edit',  // 'full_replace' | 'partial_edit' | 'insert' | 'delete'
  new_content: '新的文本内容...',
  start_line: 10,
  end_line: 15,
  reason: '修改了对话使其更自然'
}));

// 响应
{ type: 'change_pending', change_id: 'chg_xxx', diff_data: {...} }

// 接受变更
ws.send(JSON.stringify({
  type: 'accept_change',
  change_id: 'chg_xxx'
}));

// 拒绝变更
ws.send(JSON.stringify({
  type: 'reject_change',
  change_id: 'chg_xxx'
}));

// 响应
{ type: 'change_resolved', change_id: 'chg_xxx', status: 'accepted' }
```

---

### 四、Diff数据格式

```javascript
// diff_data 结构
{
  change_type: 'partial_edit',
  hunks: [
    {
      old_start: 10,
      old_lines: 5,
      new_start: 10,
      new_lines: 6,
      changes: [
        { type: 'delete', content: '旧内容', line_number: 10 },
        { type: 'insert', content: '新内容', line_number: 10 }
      ]
    }
  ],
  summary: { additions: 6, deletions: 5, hunks: 1 }
}
```

**前端展示建议**：
- 使用 Monaco Editor 或 CodeMirror
- 红色背景显示删除行
- 绿色背景显示新增行
- 右上角显示"接受/拒绝"按钮

---

### 五、作用域选择器

| 类型 | 显示名称 | 参数 |
|------|----------|------|
| `novel` | 整本小说 | 无 |
| `chapters` | 第X-Y章 | `chapter_start`, `chapter_end` |
| `chapter` | 第X章 | `chapter_start` |

---

### 六、HTTP API（可选）

| 端点 | 说明 |
|------|------|
| `GET /api/v1/sessions/list` | 获取会话列表 |
| `POST /api/v1/sessions/create` | 创建会话 |
| `GET /api/v1/sessions/{session_id}` | 获取会话详情 |
| `GET /api/v1/editor/chapters/{chapter_id}` | 获取章节内容 |
| `GET /api/v1/editor/changes/pending` | 获取待确认变更 |
| `POST /api/v1/editor/changes/{change_id}/accept` | 接受变更 |
| `POST /api/v1/editor/changes/{change_id}/reject` | 拒绝变更 |
| `GET /api/v1/mcp/tools` | 获取MCP工具列表 |

---

### 七、UI组件建议

1. **左侧栏**：
   - Tab切换：章节列表 / 对话历史
   - 章节列表：显示章节号、标题、状态
   - 对话历史：显示作用域、时间、预览

2. **中间编辑器**：
   - Monaco Editor / CodeMirror
   - 行号显示
   - Diff高亮（红/绿背景）
   - 右上角：接受/拒绝按钮

3. **右侧聊天界面**：
   - 作用域选择器（下拉菜单）
   - 历史对话选择器
   - 消息列表（支持Markdown渲染）
   - 工具调用提示卡片
   - 输入框 + 发送按钮 + 工具选择按钮(+)

---

### 八、关键交互流程

1. **用户进入**：小说详情页 → 点击"开始创作" → 连接WebSocket
2. **选择作用域**：右上角选择整本小说/章节范围
3. **开始对话**：输入消息 → AI响应 → 可能触发工具调用
4. **AI编辑**：AI调用编辑工具 → 生成diff → 用户确认
5. **切换章节**：左侧点击章节 → 编辑器显示内容
6. **历史对话**：左侧切换到对话历史 → 点击加载

---

**后端已完成所有功能实现，前端按此说明适配即可！**