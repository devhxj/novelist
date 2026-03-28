# 前后端协作指南

## 文档信息
- **版本**: v2.0.0
- **最后更新**: 2026-03-28
- **适用范围**: 前端开发Agent、后端开发Agent

---

## 1. 核心原则

### 1.1 API First 原则
- **契约优先**: API接口文档是前后端协作的契约
- **严格遵守**: 前后端必须严格遵守API文档规范
- **版本控制**: API文档纳入版本控制

### 1.2 变更管理原则
- **通知机制**: 任何API变更必须通知主调度Agent
- **协调对齐**: 变更后需要协调前后端同步更新
- **向后兼容**: 优先保证向后兼容

---

## 2. API文档管理

### 2.1 文档位置
- **主文档**: `.trae/documents/api-specification.md`
- **维护者**: 主调度Agent

### 2.2 文档更新流程
```
发现需要变更API
    ↓
通知主调度Agent
    ↓
主调度Agent评估变更影响
    ↓
主调度Agent更新API文档
    ↓
主调度Agent通知相关Agent
    ↓
前后端同步更新代码
```

---

## 3. 前端开发规范

### 3.1 API调用规范
- 使用统一的API客户端封装
- 所有API调用必须处理错误
- 使用TypeScript类型定义
- 实现JWT Token拦截器

### 3.2 类型定义示例
```typescript
export interface Novel {
  id: number;
  title: string;
  genre: string;
  description: string;
  author_id: number;
  status: 'draft' | 'writing' | 'completed' | 'published';
  chapter_count: number;
  word_count: number;
  created_at: string;
  updated_at: string;
}

export interface ApiResponse<T> {
  success: boolean;
  data: T;
  message?: string;
}
```

### 3.3 API客户端示例
```typescript
import axios from 'axios';

const apiClient = axios.create({
  baseURL: 'http://localhost:8000/api/v1',
  timeout: 10000,
});

apiClient.interceptors.request.use((config) => {
  const token = localStorage.getItem('access_token');
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

export default apiClient;
```

---

## 4. 后端开发规范

### 4.1 API实现规范
- 严格遵守API文档定义
- 使用FastAPI自动生成OpenAPI文档
- 统一响应格式
- 统一错误处理

### 4.2 响应格式实现
```python
class ApiResponse:
    @staticmethod
    def success(data: Any, message: str = "操作成功"):
        return {
            "success": True,
            "data": data,
            "message": message
        }
```

---

## 5. 相关文档
- [API接口文档](../api-specification.md)
- [JWT认证方案](./jwt-authentication.md)
- [Agent协作协议](../../AGENT_PROTOCOL.md)
