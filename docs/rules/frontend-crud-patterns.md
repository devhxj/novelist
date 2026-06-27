# 前端 CRUD 实现规范

为任意领域添加前端 CRUD 时的标准流程和约定。以 Timeline/StoryArc 为参考实现。

## 架构

```
前端组件  →  useApp() hook  →  Wails 绑定  →  Go App 方法  →  Store.DB
```

- 前端不直接调 Store，必须经过 App 层方法
- App 方法通过 Wails 自动生成前端可调用的绑定
- 每个 CRUD 操作对应一个 Go App 方法

## Go 后端

### Input 结构体 json tag

**Create Input：必填字段不加 omitempty，可选字段加 omitempty**

```go
type CreateXxxInput struct {
    Name     string `json:"name"`               // 必填
    Type     string `json:"type"`               // 必填
    Desc     string `json:"description,omitempty"` // 可选
    Priority int    `json:"priority,omitempty"`     // 可选，有默认值
}
```

不加 `omitempty` → Wails 生成 TypeScript `name: string`（required）
加 `omitempty` → Wails 生成 `name?: string`（optional）

**Update Input：所有字段加 omitempty**

```go
type UpdateXxxInput struct {
    Name     string `json:"name,omitempty"`
    Type     string `json:"type,omitempty"`
    Desc     string `json:"description,omitempty"`
    Priority int    `json:"priority,omitempty"`
}
```

所有字段 optional，PATCH 只传要改的字段。前端传完整对象也行（没改的字段原地覆盖，无影响）。

### Update 方法用 Updates() 而非 First+if+Save

```go
func (a *App) UpdateXxx(id int64, novelID int64, input UpdateXxxInput) (*xxx.Xxx, error) {
    // 一次 UPDATE，只更新非零值字段
    if err := a.store.DB.WithContext(a.ctx).
        Model(&xxx.Xxx{}).
        Where("id = ? AND novel_id = ?", id, novelID).
        Updates(&input).Error; err != nil {
        return nil, fmt.Errorf("update: %w", err)
    }
    // 查回更新后的完整对象返回
    var result xxx.Xxx
    a.store.DB.WithContext(a.ctx).First(&result, id)
    return &result, nil
}
```

- `Updates(struct)` 自动跳过零值字段（`""`、`0`），省掉手动 if 判断
- 一次 UPDATE 而非 SELECT+UPDATE，更少 DB 请求，避免并发 race
- Wails 机制上做不到真正的按 key PATCH，这个 trade-off 可接受

### Create 方法加校验

```go
func (a *App) CreateXxx(novelID int64, input CreateXxxInput) (*xxx.Xxx, error) {
    if input.Name == "" || input.Type == "" {
        return nil, fmt.Errorf("名称和类型不能为空") // 中文，用户可读
    }
    // ... Create
}
```

### Delete 方法注意级联

```go
func (a *App) DeleteXxx(id int64, novelID int64) error {
    return a.store.DB.WithContext(a.ctx).Transaction(func(tx *gorm.DB) error {
        // 先删子表
        tx.Where("parent_id = ? AND novel_id = ?", id, novelID).Delete(&Child{})
        // 再删主表
        tx.Where("id = ? AND novel_id = ?", id, novelID).Delete(&Xxx{})
        return nil
    })
}
```

### Wails 重新生成

```bash
wails generate module
```

每次修改 App 方法签名或 Input/Output 结构体后必须执行。会更新：
- `frontend/src/lib/wailsjs/go/models.ts`（类型）
- `frontend/src/lib/wailsjs/go/app/App.d.ts`（方法签名）
- `frontend/src/lib/wailsjs/go/app/App.js`（运行时绑定）

**不要手动编辑生成文件**。类型修正通过改 Go struct json tag 实现，重新生成即可。

## 前端

### useApp hook

```typescript
// 1. 从生成文件导入新方法
import { CreateXxx, UpdateXxx, DeleteXxx } from '@/lib/wailsjs/go/app/App'

// 2. 加入 useMemo 返回对象
export function useApp() {
  return useMemo(() => ({
    // ...
    CreateXxx,
    UpdateXxx,
    DeleteXxx,
  }), [])
}
```

### 组件组织

主视图组件放在领域目录：
```
components/timeline/TimelineView.tsx     → 主视图（含 CRUD 表单）
components/timeline/TimelineList.tsx     → 侧边栏列表
components/storyarc/ArcListView.tsx     → 主视图
components/storyarc/ArcList.tsx         → 侧边栏列表
```

侧边栏列表组件从领域目录导入，在 `SidePanel.tsx` 中引用。命名不带 Sidebar 前缀，与 CharacterList/LocationList 保持一致。

### 表单类型定义

**编辑表单类型包含所有可能字段（包括状态），创建也可复用**

```typescript
// 比 Create 多了 status 等编辑专有字段，但所有字段兼容 Update
type XxxForm = {
  name: string
  type: string
  description?: string
  priority?: number
  status?: string     // 编辑时才有
  // ...
}
```

共用表单类型，创建时不碰的字段不会出现在 JSON 里（undefined 不序列化），编辑时传完整对象。

**不要用 `as any`**。如果类型不匹配，检查 Go struct 是否缺 omitempty，修复后重新生成。

### 表单状态管理

```typescript
type EditMode =
  | { type: 'create' }
  | { type: 'edit'; item: Xxx }
  | null

const [editMode, setEditMode] = useState<EditMode>(null)
const [form, setForm] = useState<XxxForm>(EMPTY_FORM)
const [saving, setSaving] = useState(false)
```

### 打开表单时清除旧错误

```typescript
function openCreate() {
  setError(null)       // ← 清除上次失败的错误
  setForm(EMPTY_FORM)
  setEditMode({ type: 'create' })
}
```

### 保存时前端前置校验 + 后端兜底

```typescript
async function handleCreate() {
  if (!form.name.trim()) { setError('请输入名称'); return }
  if (!form.type)        { setError('请选择类型'); return }
  setSaving(true)
  try {
    await app.CreateXxx(novelId, form)
    setEditMode(null)
    await load()
  } catch (err) {
    setError(err instanceof Error ? err.message : '创建失败')
  } finally {
    setSaving(false)
  }
}
```

- 前端先校验必填字段，中文提示
- 后端再加一层校验，中文错误消息
- 双重防护

### 快速状态切换

在列表项 hover 时提供快速操作，不需要进入编辑表单：

```typescript
{/* hover 时显示：标记完成 ✓ / 编辑 ✎ / 删除 ✕ */}
<div className="flex items-center gap-0.5 opacity-0 group-hover:opacity-100 transition-opacity">
  {item.status === 'pending' && (
    <button onClick={() => handleQuickStatus(item, 'resolved')} ...>✓</button>
  )}
  <button onClick={() => openEdit(item)} ...><Pencil .../></button>
  <button onClick={() => handleDelete(item.id)} ...><Trash2 .../></button>
</div>
```

## 主题规范

参见 `docs/frontend/theme-rules.md`。所有新增组件必须遵守：

- 用语义 class：`bg-card`、`text-foreground`、`text-muted-foreground`、`border-border`
- 彩色标签用 `--tag-*` 变量：`bg-tag-blue`、`bg-tag-green`、`bg-tag-amber`、`bg-tag-purple`、`bg-tag-rose`
- 删除操作用 `bg-destructive text-destructive-foreground` / `hover:text-destructive`
- 禁止：`bg-white`、`text-slate-*`、`dark:` 前缀、hex/oklch 硬编码、if (dark) 三元
- 图组件调色板用 `PALETTE_LIGHT` / `PALETTE_DARK` 常量

## 检查清单

实施新领域 CRUD 时逐项确认：

- [ ] Go Input struct：Create 必填不加 omitempty，可选/Update 全加
- [ ] Go Update 方法用 `Updates(struct)` 不要 `First+if+Save`
- [ ] Go Create 方法加中文输入校验
- [ ] Go Delete 方法考虑级联
- [ ] `wails generate module` 重新生成绑定
- [ ] useApp hook 加入新方法
- [ ] 前端表单类型覆盖所有字段，不用 `as any`
- [ ] 前端保存前校验必填字段（中文提示）
- [ ] 表单打开时 `setError(null)` 清旧错误
- [ ] 组件放领域目录，侧边栏列表名不带 Sidebar 前缀
- [ ] 主题变量检查：无 `bg-white`/`text-slate-*`/hex/`dark:`/三元
- [ ] `go build ./app/ ./internal/...` + `npm run build` 通过
