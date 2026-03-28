# AI小说生成系统 - API接口文档

## 文档信息
- **版本**: v1.0.0
- **创建时间**: 2026-03-27
- **最后更新**: 2026-03-27
- **基础URL**: `http://localhost:8000/api/v1`

## 通用规范

### 认证方式
- **认证类型**: JWT (JSON Web Token)
- **请求头**: `Authorization: Bearer <token>`
- **Token有效期**: 24小时
- **刷新Token**: 有效期7天

### 通用响应格式

#### 成功响应
```json
{
  "success": true,
  "data": {},
  "message": "操作成功"
}
```

#### 错误响应
```json
{
  "success": false,
  "error": {
    "code": "ERROR_CODE",
    "message": "错误描述",
    "details": {}
  }
}
```

### HTTP状态码
- `200 OK` - 请求成功
- `201 Created` - 创建成功
- `204 No Content` - 删除成功
- `400 Bad Request` - 请求参数错误
- `401 Unauthorized` - 未认证
- `403 Forbidden` - 无权限
- `404 Not Found` - 资源不存在
- `422 Unprocessable Entity` - 数据验证失败
- `500 Internal Server Error` - 服务器错误

### 分页参数
```
?page=1&page_size=20
```

#### 分页响应格式
```json
{
  "success": true,
  "data": {
    "items": [],
    "total": 100,
    "page": 1,
    "page_size": 20,
    "total_pages": 5
  }
}
```

---

## 1. 认证接口

### 1.1 用户注册
**POST** `/auth/register`

#### 请求体
```json
{
  "username": "string",
  "email": "string",
  "password": "string"
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "user_id": 1,
    "username": "string",
    "email": "string",
    "created_at": "2026-03-27T00:00:00Z"
  },
  "message": "注册成功"
}
```

### 1.2 用户登录
**POST** `/auth/login`

#### 请求体
```json
{
  "username": "string",
  "password": "string"
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "access_token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "refresh_token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "token_type": "bearer",
    "expires_in": 86400
  },
  "message": "登录成功"
}
```

### 1.3 刷新Token
**POST** `/auth/refresh`

#### 请求头
```
Authorization: Bearer <refresh_token>
```

#### 响应
```json
{
  "success": true,
  "data": {
    "access_token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "token_type": "bearer",
    "expires_in": 86400
  }
}
```

### 1.4 获取当前用户信息
**GET** `/auth/me`

#### 请求头
```
Authorization: Bearer <access_token>
```

#### 响应
```json
{
  "success": true,
  "data": {
    "user_id": 1,
    "username": "string",
    "email": "string",
    "created_at": "2026-03-27T00:00:00Z"
  }
}
```

---

## 2. 小说管理接口

### 2.1 获取小说列表
**GET** `/novels`

#### 查询参数
- `page` (int): 页码，默认1
- `page_size` (int): 每页数量，默认20
- `status` (string): 状态筛选 (draft/writing/completed/published)
- `genre` (string): 类型筛选
- `search` (string): 标题搜索

#### 响应
```json
{
  "success": true,
  "data": {
    "items": [
      {
        "id": 1,
        "title": "小说标题",
        "genre": "玄幻",
        "description": "小说简介",
        "author_id": 1,
        "status": "draft",
        "chapter_count": 0,
        "word_count": 0,
        "created_at": "2026-03-27T00:00:00Z",
        "updated_at": "2026-03-27T00:00:00Z"
      }
    ],
    "total": 10,
    "page": 1,
    "page_size": 20,
    "total_pages": 1
  }
}
```

### 2.2 创建小说
**POST** `/novels`

#### 请求体
```json
{
  "title": "string",
  "genre": "string",
  "description": "string"
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "title": "小说标题",
    "genre": "玄幻",
    "description": "小说简介",
    "author_id": 1,
    "status": "draft",
    "created_at": "2026-03-27T00:00:00Z",
    "updated_at": "2026-03-27T00:00:00Z"
  },
  "message": "小说创建成功"
}
```

### 2.3 获取小说详情
**GET** `/novels/{novel_id}`

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "title": "小说标题",
    "genre": "玄幻",
    "description": "小说简介",
    "author_id": 1,
    "status": "draft",
    "chapter_count": 10,
    "word_count": 50000,
    "character_count": 5,
    "created_at": "2026-03-27T00:00:00Z",
    "updated_at": "2026-03-27T00:00:00Z",
    "characters": [
      {
        "id": 1,
        "name": "角色名",
        "personality": {}
      }
    ],
    "chapters": [
      {
        "id": 1,
        "chapter_number": 1,
        "title": "第一章",
        "status": "completed"
      }
    ]
  }
}
```

### 2.4 更新小说
**PUT** `/novels/{novel_id}`

#### 请求体
```json
{
  "title": "string",
  "genre": "string",
  "description": "string",
  "status": "string"
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "title": "更新后的标题",
    "genre": "玄幻",
    "description": "更新后的简介",
    "status": "writing",
    "updated_at": "2026-03-27T00:00:00Z"
  },
  "message": "小说更新成功"
}
```

### 2.5 删除小说
**DELETE** `/novels/{novel_id}`

#### 响应
```json
{
  "success": true,
  "message": "小说删除成功"
}
```

---

## 3. 角色管理接口

### 3.1 获取小说角色列表
**GET** `/novels/{novel_id}/characters`

#### 查询参数
- `page` (int): 页码
- `page_size` (int): 每页数量
- `search` (string): 角色名搜索

#### 响应
```json
{
  "success": true,
  "data": {
    "items": [
      {
        "id": 1,
        "novel_id": 1,
        "name": "角色名",
        "personality": {
          "traits": ["勇敢", "聪明"],
          "background": "背景故事"
        },
        "relationships": {
          "friend": [2, 3],
          "enemy": [4]
        },
        "abilities": ["技能1", "技能2"],
        "created_at": "2026-03-27T00:00:00Z"
      }
    ],
    "total": 5,
    "page": 1,
    "page_size": 20,
    "total_pages": 1
  }
}
```

### 3.2 创建角色
**POST** `/novels/{novel_id}/characters`

#### 请求体
```json
{
  "name": "string",
  "personality": {
    "traits": ["string"],
    "background": "string"
  },
  "relationships": {
    "friend": [1, 2],
    "enemy": [3]
  },
  "abilities": ["string"]
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "novel_id": 1,
    "name": "角色名",
    "personality": {},
    "relationships": {},
    "abilities": [],
    "created_at": "2026-03-27T00:00:00Z"
  },
  "message": "角色创建成功"
}
```

### 3.3 获取角色详情
**GET** `/characters/{character_id}`

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "novel_id": 1,
    "name": "角色名",
    "personality": {
      "traits": ["勇敢", "聪明"],
      "background": "背景故事"
    },
    "relationships": {
      "friend": [2, 3],
      "enemy": [4]
    },
    "abilities": ["技能1", "技能2"],
    "created_at": "2026-03-27T00:00:00Z",
    "novel": {
      "id": 1,
      "title": "小说标题"
    }
  }
}
```

### 3.4 更新角色
**PUT** `/characters/{character_id}`

#### 请求体
```json
{
  "name": "string",
  "personality": {},
  "relationships": {},
  "abilities": []
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "name": "更新后的角色名",
    "personality": {},
    "relationships": {},
    "abilities": []
  },
  "message": "角色更新成功"
}
```

### 3.5 删除角色
**DELETE** `/characters/{character_id}`

#### 响应
```json
{
  "success": true,
  "message": "角色删除成功"
}
```

---

## 4. 章节管理接口

### 4.1 获取小说章节列表
**GET** `/novels/{novel_id}/chapters`

#### 查询参数
- `page` (int): 页码
- `page_size` (int): 每页数量
- `status` (string): 状态筛选 (draft/completed)
- `order` (string): 排序 (asc/desc)

#### 响应
```json
{
  "success": true,
  "data": {
    "items": [
      {
        "id": 1,
        "novel_id": 1,
        "chapter_number": 1,
        "title": "第一章 开始",
        "word_count": 3000,
        "status": "completed",
        "summary": "章节摘要",
        "created_at": "2026-03-27T00:00:00Z",
        "updated_at": "2026-03-27T00:00:00Z"
      }
    ],
    "total": 10,
    "page": 1,
    "page_size": 20,
    "total_pages": 1
  }
}
```

### 4.2 创建章节
**POST** `/novels/{novel_id}/chapters`

#### 请求体
```json
{
  "chapter_number": 1,
  "title": "string",
  "content": "string",
  "summary": "string"
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "novel_id": 1,
    "chapter_number": 1,
    "title": "第一章",
    "content": "章节内容",
    "summary": "章节摘要",
    "status": "draft",
    "word_count": 3000,
    "created_at": "2026-03-27T00:00:00Z"
  },
  "message": "章节创建成功"
}
```

### 4.3 获取章节详情
**GET** `/chapters/{chapter_id}`

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "novel_id": 1,
    "chapter_number": 1,
    "title": "第一章 开始",
    "content": "章节完整内容...",
    "summary": "章节摘要",
    "status": "completed",
    "word_count": 3000,
    "created_at": "2026-03-27T00:00:00Z",
    "updated_at": "2026-03-27T00:00:00Z",
    "novel": {
      "id": 1,
      "title": "小说标题"
    },
    "plot_events": [
      {
        "id": 1,
        "event_type": "battle",
        "description": "情节描述"
      }
    ]
  }
}
```

### 4.4 更新章节
**PUT** `/chapters/{chapter_id}`

#### 请求体
```json
{
  "title": "string",
  "content": "string",
  "summary": "string",
  "status": "string"
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "title": "更新后的标题",
    "content": "更新后的内容",
    "summary": "更新后的摘要",
    "status": "completed",
    "word_count": 3500,
    "updated_at": "2026-03-27T00:00:00Z"
  },
  "message": "章节更新成功"
}
```

### 4.5 删除章节
**DELETE** `/chapters/{chapter_id}`

#### 响应
```json
{
  "success": true,
  "message": "章节删除成功"
}
```

---

## 5. 情节事件接口

### 5.1 获取情节事件列表
**GET** `/novels/{novel_id}/plot-events`

#### 查询参数
- `page` (int): 页码
- `page_size` (int): 每页数量
- `chapter_id` (int): 章节ID筛选
- `event_type` (string): 事件类型筛选

#### 响应
```json
{
  "success": true,
  "data": {
    "items": [
      {
        "id": 1,
        "novel_id": 1,
        "chapter_id": 1,
        "event_type": "battle",
        "description": "主角与反派战斗",
        "characters_involved": [1, 2, 3],
        "timeline": "2026-03-27T00:00:00Z",
        "consequences": {
          "result": "主角获胜",
          "impact": ["获得宝物", "提升实力"]
        },
        "created_at": "2026-03-27T00:00:00Z"
      }
    ],
    "total": 20,
    "page": 1,
    "page_size": 20,
    "total_pages": 1
  }
}
```

### 5.2 创建情节事件
**POST** `/novels/{novel_id}/plot-events`

#### 请求体
```json
{
  "chapter_id": 1,
  "event_type": "string",
  "description": "string",
  "characters_involved": [1, 2],
  "timeline": "2026-03-27T00:00:00Z",
  "consequences": {
    "result": "string",
    "impact": ["string"]
  }
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "novel_id": 1,
    "chapter_id": 1,
    "event_type": "battle",
    "description": "情节描述",
    "characters_involved": [1, 2],
    "timeline": "2026-03-27T00:00:00Z",
    "consequences": {},
    "created_at": "2026-03-27T00:00:00Z"
  },
  "message": "情节事件创建成功"
}
```

### 5.3 获取情节事件详情
**GET** `/plot-events/{event_id}`

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "novel_id": 1,
    "chapter_id": 1,
    "event_type": "battle",
    "description": "详细情节描述",
    "characters_involved": [1, 2, 3],
    "timeline": "2026-03-27T00:00:00Z",
    "consequences": {
      "result": "主角获胜",
      "impact": ["获得宝物", "提升实力"]
    },
    "created_at": "2026-03-27T00:00:00Z",
    "novel": {
      "id": 1,
      "title": "小说标题"
    },
    "chapter": {
      "id": 1,
      "chapter_number": 1,
      "title": "章节标题"
    }
  }
}
```

### 5.4 更新情节事件
**PUT** `/plot-events/{event_id}`

#### 请求体
```json
{
  "chapter_id": 1,
  "event_type": "string",
  "description": "string",
  "characters_involved": [1, 2],
  "timeline": "2026-03-27T00:00:00Z",
  "consequences": {}
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "id": 1,
    "event_type": "updated_type",
    "description": "更新后的描述",
    "updated_at": "2026-03-27T00:00:00Z"
  },
  "message": "情节事件更新成功"
}
```

### 5.5 删除情节事件
**DELETE** `/plot-events/{event_id}`

#### 响应
```json
{
  "success": true,
  "message": "情节事件删除成功"
}
```

---

## 6. AI生成接口

### 6.1 生成章节内容
**POST** `/novels/{novel_id}/chapters/{chapter_id}/generate`

#### 请求体
```json
{
  "prompt": "string",
  "context": {
    "previous_chapters": [1, 2],
    "characters": [1, 2],
    "style": "narrative"
  },
  "options": {
    "temperature": 0.8,
    "max_tokens": 3000
  }
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "chapter_id": 1,
    "content": "生成的章节内容...",
    "word_count": 3000,
    "generation_time": 15.5,
    "model_used": "deepseek-chat"
  },
  "message": "章节内容生成成功"
}
```

### 6.2 一致性检查
**POST** `/novels/{novel_id}/consistency-check`

#### 请求体
```json
{
  "chapter_ids": [1, 2, 3],
  "check_types": ["character", "plot", "timeline"]
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "check_id": "check_123",
    "status": "completed",
    "issues": [
      {
        "type": "character",
        "severity": "warning",
        "chapter_id": 2,
        "description": "角色性格前后不一致",
        "details": {
          "character_id": 1,
          "issue": "第一章描述为勇敢，第三章表现为胆怯"
        },
        "suggestion": "建议调整角色行为或补充性格转变的情节"
      }
    ],
    "check_time": 5.2
  },
  "message": "一致性检查完成"
}
```

### 6.3 记忆检索
**POST** `/novels/{novel_id}/memory/search`

#### 请求体
```json
{
  "query": "string",
  "search_type": "semantic",
  "filters": {
    "chapter_ids": [1, 2],
    "character_ids": [1],
    "event_types": ["battle"]
  },
  "top_k": 10
}
```

#### 响应
```json
{
  "success": true,
  "data": {
    "results": [
      {
        "id": 1,
        "type": "chapter",
        "content": "相关内容片段...",
        "chapter_id": 1,
        "relevance_score": 0.95,
        "metadata": {
          "chapter_number": 1,
          "title": "章节标题"
        }
      }
    ],
    "total": 5,
    "search_time": 0.3
  }
}
```

---

## 7. 错误码定义

### 通用错误码
- `AUTH_001`: 未提供认证Token
- `AUTH_002`: Token无效或已过期
- `AUTH_003`: 权限不足
- `VALIDATION_001`: 请求参数验证失败
- `VALIDATION_002`: 数据格式错误
- `NOT_FOUND_001`: 资源不存在
- `SERVER_001`: 服务器内部错误
- `SERVER_002`: 数据库错误
- `SERVER_003`: 第三方服务错误

### 业务错误码
- `NOVEL_001`: 小说不存在
- `NOVEL_002`: 小说标题已存在
- `CHAPTER_001`: 章节不存在
- `CHAPTER_002`: 章节编号已存在
- `CHARACTER_001`: 角色不存在
- `CHARACTER_002`: 角色名重复
- `AI_001`: AI生成服务不可用
- `AI_002`: 生成内容超时

---

## 8. 数据字典

### 小说状态 (novel.status)
- `draft`: 草稿
- `writing`: 写作中
- `completed`: 已完成
- `published`: 已发布

### 章节状态 (chapter.status)
- `draft`: 草稿
- `completed`: 已完成

### 情节事件类型 (plot_event.event_type)
- `battle`: 战斗
- `dialogue`: 对话
- `travel`: 旅行
- `discovery`: 发现
- `romance`: 情感
- `death`: 死亡
- `mystery`: 悬疑
- `other`: 其他

---

## 9. API版本控制

- 当前版本: `v1`
- 版本前缀: `/api/v1`
- 版本策略: URL路径版本控制

---

## 10. 变更日志

### v1.0.0 (2026-03-27)
- 初始版本发布
- 定义基础CRUD接口
- 定义认证接口
- 定义AI生成接口
