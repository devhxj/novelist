# 主题规范 & 加组件 CheckList

## 绝对禁止

- 写 `bg-white`、`text-slate-500`、`border-slate-200` 等 Tailwind 调色板色值
- 写 `bg-[#fafbfc]`、`text-[#333]` 等 hex 硬编码
- 写 `oklch(0.xxx ...)` 绝对值
- 写 `dark:` 前缀（`dark:bg-background` 也不行——语义变量已经自适应）
- 写 `dark ? ... : ...` 三元判断主题
- 在 JS 里硬编码颜色 hex（图组件用 graphColors.ts 或 PALETTE 字典）

## 必须遵守

### color 类 → 语义 class

| 硬编码（禁止）                     | 语义（必须）           |
|-------------------------------------|------------------------|
| `bg-white` `bg-[#fafbfc]`          | `bg-card` `bg-background` |
| `text-slate-800` `text-slate-700`  | `text-foreground`      |
| `text-slate-500` `text-slate-400`  | `text-muted-foreground`|
| `border-slate-200` `border-slate-300` | `border-border`     |
| `bg-slate-50`                      | `bg-muted`             |
| `bg-slate-100`                     | `bg-secondary`         |
| `bg-emerald-50 text-emerald-600`   | `bg-tag-green text-tag-green-foreground` |
| `bg-amber-50 text-amber-500`       | `bg-tag-amber text-tag-amber-foreground` |
| `bg-blue-50 text-blue-600`         | `bg-tag-blue text-tag-blue-foreground`   |
| `bg-rose-50`                       | `bg-tag-rose text-tag-rose-foreground`   |
| `bg-purple-50 text-purple-600`     | `bg-tag-purple text-tag-purple-foreground` |
| `bg-red-600 text-white` (删除按钮)  | `bg-destructive text-destructive-foreground` |
| `hover:text-red-*` (删除/关闭图标)  | `hover:text-destructive`                |
| User 消息气泡                       | `bg-bubble-user text-bubble-user-foreground` |

### 主题相关逻辑 → `Record<Theme, T>` 字典

```ts
// ✅ 正确
const ICONS: Record<Theme, ReactNode> = { light: <Moon />, dark: <Sun /> }
const LABELS: Record<Theme, string> = { light: '深色模式', dark: '浅色模式' }

// ❌ 禁止
const icon = theme === 'dark' ? <Sun /> : <Moon />
const label = dark ? '浅色' : '深色'
```

例外：浏览器 API 边界（`matchMedia('prefers-color-scheme: dark')`）返回 boolean，允许 `sysTheme()` 函数做一次性转换。

### 第三方组件 → 自己管好边界

- Monaco：`MONACO_THEME: Record<Theme, string> = { light: 'light', dark: 'vs-dark' }`
- g6 / cytoscape：`useGraphColors()`，返回 `{ bg, edge, edgeDim, ... }`
- 数据可视化弧线：`PALETTE_LIGHT` / `PALETTE_DARK` 两组常量

### CSS 文件 → `[data-theme="xxx"]` 选择器

```css
/* ✅ 正确 */
[data-theme="dark"] .my-shimmer { ... }
[data-theme="dark"] .my-label { color: oklch(...); }

/* ❌ 禁止 */
.dark .my-shimmer { ... }
```

唯一例外：`index.css` 中 `@custom-variant dark (&:is([data-theme="dark"] *));` 让 Tailwind 的 `dark:` utility 能用（只用于 shadcn/ui 组件库内置样式，自己的组件不依赖 `dark:`）。

## 加新组件的自检清单

- [ ] 没有 `bg-white`、`text-slate-*`、`border-slate-*`
- [ ] 没有 `bg-[#xxx]`、`text-[#xxx]`
- [ ] 没有 `oklch(...)` 绝对值
- [ ] 没有 `dark:` 前缀（检查 className 和 CSS）
- [ ] 没有 `const dark = ...; if (dark)` 或 `dark ? ... : ...`
- [ ] 如果有彩色 Tag/标签 → 用 `--tag-*` 语义变量
- [ ] 如果有工具/状态卡片 → 按需扩展 `--tool-*` 变量
- [ ] 深色模式下用 `dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop` 切换主题目视验证
- [ ] `npm run build` 无报错

## 常见陷阱

1. **`useTheme()` 每个组件独立 state** — 但 MutationObserver 保证跨组件同步。不要试图在组件间通过 props 传 theme——每个组件独立调用 `useTheme()` 即可。

2. **inline style 颜色** — `style={{ backgroundColor: '#dbeafe' }}` 绕过 CSS 变量体系，sed 也搜不到。必须换成 className 或从 PALETTE 字典取值。

3. **阴影/透明度** — `shadow-sm`、`shadow-lg` 本身没问题，但它们的颜色在 shadcn/ui 里已经变体了。不要自己覆写 `box-shadow` 色值。

4. **`color-mix()` 把 oklch 写死** — ToolCallCard 之前大量 `color-mix(in oklab, oklch(0.62 0.19 250) 18%, transparent)`。正确写法是 `color-mix(in oklab, var(--tool-blue-border) 18%, transparent)`。

5. **只改 className 忘了 CSS 文件** — `CompressionBlock.css`、`ThinkingBlock.css`、`Markdown.css` 独立 CSS 写 `.dark` 选择器时必须同步。
