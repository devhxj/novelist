# AI小说生成系统 - 前端

基于 React + TypeScript + Ant Design 构建的AI小说生成系统前端应用。

## 技术栈

- **框架**: React 19 + TypeScript
- **构建工具**: Vite
- **UI库**: Ant Design 5
- **路由**: React Router 6
- **状态管理**: Zustand
- **HTTP客户端**: Axios
- **日期处理**: Day.js

## 项目结构

```
frontend/
├── src/
│   ├── components/          # 组件目录
│   │   ├── common/         # 通用组件
│   │   ├── layout/         # 布局组件
│   │   ├── novel/          # 小说相关组件
│   │   ├── character/      # 角色相关组件
│   │   ├── chapter/        # 章节相关组件
│   │   └── auth/           # 认证相关组件
│   ├── pages/              # 页面组件
│   │   ├── auth/           # 登录/注册页面
│   │   ├── novel/          # 小说管理页面
│   │   ├── character/      # 角色管理页面
│   │   └── chapter/        # 章节管理页面
│   ├── services/           # API服务
│   │   ├── apiClient.ts    # Axios客户端配置
│   │   ├── authService.ts  # 认证API
│   │   ├── novelService.ts # 小说API
│   │   ├── characterService.ts # 角色API
│   │   ├── chapterService.ts # 章节API
│   │   ├── plotEventService.ts # 情节事件API
│   │   └── aiService.ts    # AI生成API
│   ├── stores/             # 状态管理
│   │   ├── authStore.ts    # 认证状态
│   │   └── novelStore.ts   # 小说状态
│   ├── types/              # TypeScript类型定义
│   │   ├── api.ts          # API通用类型
│   │   ├── auth.ts         # 认证类型
│   │   ├── novel.ts        # 小说类型
│   │   ├── character.ts    # 角色类型
│   │   ├── chapter.ts      # 章节类型
│   │   ├── plotEvent.ts    # 情节事件类型
│   │   └── ai.ts           # AI生成类型
│   ├── hooks/              # 自定义Hooks
│   ├── utils/              # 工具函数
│   ├── styles/             # 全局样式
│   ├── routes.tsx          # 路由配置
│   ├── main.tsx            # 应用入口
│   └── index.css           # 全局样式
├── .env                    # 环境变量
├── .env.development        # 开发环境变量
├── vite.config.ts          # Vite配置
├── tsconfig.json           # TypeScript配置
└── package.json            # 项目依赖

```

## 快速开始

### 安装依赖

```bash
npm install
```

### 启动开发服务器

```bash
npm run dev
```

访问 http://localhost:5173

### 构建生产版本

```bash
npm run build
```

### 预览生产版本

```bash
npm run preview
```

## 环境变量

创建 `.env.local` 文件配置本地环境变量：

```env
VITE_API_BASE_URL=http://localhost:8000/api/v1
```

## API文档

API接口文档位于: `.trae/documents/api-specification.md`

## 开发规范

### 代码风格
- 使用 TypeScript 严格模式
- 使用 ESLint 进行代码检查
- 组件使用函数式组件 + Hooks
- 样式使用 CSS Modules

### 命名规范
- 组件文件: PascalCase (如 `NovelList.tsx`)
- 工具函数: camelCase (如 `formatDate.ts`)
- 样式文件: 组件名.module.css (如 `NovelList.module.css`)

### Git提交规范
- feat: 新功能
- fix: 修复bug
- docs: 文档更新
- style: 代码格式调整
- refactor: 重构
- test: 测试相关
- chore: 构建/工具相关

## 功能模块

### 认证模块
- [x] 用户登录
- [x] 用户注册
- [x] JWT Token管理
- [x] 自动Token刷新

### 小说管理
- [x] 小说列表（分页、搜索、筛选）
- [x] 小说详情
- [x] 创建小说
- [ ] 编辑小说
- [ ] 删除小说

### 角色管理
- [ ] 角色列表
- [ ] 角色详情
- [ ] 创建角色
- [ ] 编辑角色

### 章节管理
- [ ] 章节列表
- [ ] 章节详情
- [ ] 创建章节
- [ ] AI生成章节

### AI功能
- [ ] 章节内容生成
- [ ] 一致性检查
- [ ] 记忆检索

## 注意事项

1. **API认证**: 所有API请求都需要JWT Token，Token会自动添加到请求头
2. **Token过期**: Token过期后会自动跳转到登录页面
3. **跨域配置**: 开发环境已配置代理，生产环境需要配置Nginx
4. **类型安全**: 所有API调用都有完整的TypeScript类型定义

## 相关文档

- [API接口文档](../.trae/documents/api-specification.md)
- [前后端协作规范](../.trae/documents/frontend-backend-collaboration.md)
- [JWT认证方案](../.trae/documents/jwt-authentication.md)
