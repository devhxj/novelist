# 前端 CRUD 实现规范

为任意领域添加前端 CRUD 时的标准流程和约定。以 Timeline、StoryArc、Character、Location 为参考实现。

## 架构

```text
前端组件 -> useApp() hook -> Novelist bridge adapter -> Photino WebMessage -> .NET bridge handler -> Core service interface -> Infrastructure store
```

- 前端不直接访问后端存储，必须经过 `useApp()` 暴露的桥方法。
- 前端桥代码归属 `frontend/src/lib/novelist/`，后端 DTO 归属 `src/Novelist.Contracts/`。
- 后端业务入口应注册在 `src/Novelist.Core/Bridge/*BridgeHandlers.cs`，实现放在 `src/Novelist.Infrastructure/App/`。
- 不再使用旧桌面代码生成绑定；不要恢复退休的前端绑定目录或桌面代码生成命令。

## 后端

### DTO 定义

DTO 使用 C# `record`，属性必须带稳定的 `JsonPropertyName`。创建 payload 中必填字段使用非空类型；更新 payload 中可选字段使用可空类型。

```csharp
public sealed record CreateXxxPayload(
    [property: JsonPropertyName("novel_id")] long NovelId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description);

public sealed record UpdateXxxPayload(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("status")] string? Status = null);
```

### 服务边界

```csharp
public interface IXxxService
{
    ValueTask<IReadOnlyList<XxxPayload>> GetXxxAsync(long novelId, CancellationToken cancellationToken);
    ValueTask<XxxPayload> CreateXxxAsync(CreateXxxPayload input, CancellationToken cancellationToken);
    ValueTask<XxxPayload> UpdateXxxAsync(long novelId, long id, UpdateXxxPayload input, CancellationToken cancellationToken);
    ValueTask DeleteXxxAsync(long novelId, long id, CancellationToken cancellationToken);
}
```

- 接口放在 `Novelist.Core.App`，实现放在 `Novelist.Infrastructure.App`。
- 存储实现必须使用注入的 `AppInitializationOptions` 和服务依赖，测试中可以替换数据目录。
- 删除操作必须明确处理级联数据，不能只删主记录后留下孤儿记录。

### 校验与错误

- 所有后端写入都要做兜底校验，不能只依赖前端表单。
- 文本字段统一 trim，设置长度上限，拒绝空必填字段。
- 路径、URL、文件写入、外部数据必须经过现有 SafePath、SSRF、审批流或对应安全边界。
- 桥层错误使用稳定错误码，输入错误优先映射为 `BridgeErrorCodes.ValidationError`。

### Bridge handler

桥 handler 负责参数解析、调用服务和错误映射，不承载业务逻辑。

```csharp
dispatcher.Register("CreateXxx", async (context, cancellationToken) =>
{
    var input = context.GetPayload<CreateXxxPayload>();
    return await service.CreateXxxAsync(input, cancellationToken);
});
```

新增 handler 后必须在桌面启动路径注册，并补充 bridge/服务测试。

## 前端

### useApp hook

前端组件只通过 `useApp()` 调后端。新增桥方法时：

1. 在 `frontend/src/lib/novelist/types.ts` 增加或复用 owned DTO 类型。
2. 在 `frontend/src/lib/novelist/api.ts` 增加方法封装。
3. 从 `useApp()` 返回的 `appApi` 使用该方法。

不要从组件直接调用底层 `bridge.invoke()`，除非是在 novelist adapter 内部。

### 组件组织

主视图组件放在领域目录：

```text
components/timeline/TimelineView.tsx
components/timeline/TimelineList.tsx
components/storyarc/ArcListView.tsx
components/storyarc/ArcList.tsx
```

侧边栏列表组件从领域目录导入，在 `SidePanel.tsx` 中引用。命名不带 Sidebar 前缀，与 CharacterList、LocationList 保持一致。

### 表单类型定义

编辑表单类型覆盖创建和更新需要的所有字段，但提交时只构造后端 DTO 允许的字段。

```typescript
type XxxForm = {
  name: string
  description: string
  status?: string
}
```

不要用 `as any` 绕过类型。如果类型不匹配，先修正 `frontend/src/lib/novelist/types.ts` 或后端 contract。

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
  setError(null)
  setForm(EMPTY_FORM)
  setEditMode({ type: 'create' })
}
```

### 保存时前端前置校验 + 后端兜底

```typescript
async function handleCreate() {
  if (!form.name.trim()) { setError('请输入名称'); return }
  setSaving(true)
  try {
    await app.CreateXxx({ novel_id: novelId, name: form.name.trim(), description: form.description.trim() })
    setEditMode(null)
    await load()
  } catch (err) {
    setError(err instanceof Error ? err.message : '创建失败')
  } finally {
    setSaving(false)
  }
}
```

- 前端先校验必填字段，使用用户可读中文提示。
- 后端必须再次校验，确保桥调用和工具调用也安全。
- 更新后通常重新 `load()`，避免前端局部状态和后端排序/派生字段不一致。

### 快速状态切换

在列表项 hover 时提供快速操作，不需要进入编辑表单：

```tsx
<div className="flex items-center gap-0.5 opacity-0 group-hover:opacity-100 transition-opacity">
  {item.status === 'pending' && (
    <button onClick={() => handleQuickStatus(item, 'resolved')}>完成</button>
  )}
  <button onClick={() => openEdit(item)}>编辑</button>
  <button onClick={() => handleDelete(item.id)}>删除</button>
</div>
```

## 主题规范

参见 `docs/frontend/theme-rules.md`。所有新增组件必须遵守：

- 用语义 class：`bg-card`、`text-foreground`、`text-muted-foreground`、`border-border`。
- 彩色标签用 `--tag-*` 变量：`bg-tag-blue`、`bg-tag-green`、`bg-tag-amber`、`bg-tag-purple`、`bg-tag-rose`。
- 删除操作用 `bg-destructive text-destructive-foreground` / `hover:text-destructive`。
- 禁止：`bg-white`、`text-slate-*`、`dark:` 前缀、hex/oklch 硬编码、`if (dark)` 三元。
- 图组件调色板用 `PALETTE_LIGHT` / `PALETTE_DARK` 常量。

## 检查清单

实施新领域 CRUD 时逐项确认：

- [ ] Contract DTO 使用稳定 `JsonPropertyName`，必填/可选语义准确。
- [ ] Core interface 和 Infrastructure 实现边界清晰。
- [ ] 后端 create/update/delete 均有输入校验和级联处理。
- [ ] Bridge handler 注册到桌面启动路径，错误码稳定。
- [ ] `frontend/src/lib/novelist/types.ts` 和 `api.ts` 暴露 owned DTO/方法。
- [ ] 组件只通过 `useApp()` 调用后端。
- [ ] 前端表单类型覆盖所有字段，不用 `as any`。
- [ ] 前端保存前校验必填字段，后端仍做兜底。
- [ ] 表单打开时 `setError(null)` 清旧错误。
- [ ] 组件放领域目录，侧边栏列表名不带 Sidebar 前缀。
- [ ] 主题变量检查：无 `bg-white`、`text-slate-*`、hex、`dark:`、主题三元。
- [ ] 覆盖服务/桥/必要前端构建验证。
- [ ] `dotnet test Novelist.slnx --no-restore -v minimal`、`npm --prefix frontend run lint`、`npm --prefix frontend run build` 通过。
