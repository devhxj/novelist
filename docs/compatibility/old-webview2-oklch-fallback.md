# Old WebView2 oklch Fallback

## 问题

较旧的 WebView2 运行时（Chromium < 111）不支持 `oklch()` / `oklab()` / `color-mix()` 颜色函数。Tailwind v4 和项目自定义 CSS 大量使用 `oklch()` 定义颜色变量，导致：

- 所有 CSS 自定义属性（`--background`、`--foreground`、`--primary` 等）失效
- 界面变成黑白，层级错乱（弹窗背景透明）

## 根因

| CSS 特性 | 最低 Chromium 版本 | 项目使用情况 |
|---|---|---|
| `oklch()` | 111 (2023-03) | 全部主题变量（index.css ~100 处） |
| `oklab()` | 111 | highlight.js 着色等 |
| `color-mix()` | 111 | 透明度修饰（/30 /50 等）、Markdown.css、ToolCallCard.css |
| `color(display-p3)` | 111 | PostCSS 生成的中间增强层 |

## 方案

### CSS 颜色降级：PostCSS 插件

安装 `@csstools/postcss-oklab-function`，在 Tailwind v4 生成完 CSS 之后，自动为每条 `oklch()` 声明生成 `rgb()` 降级，并用 `@supports` 分别包裹：

```css
/* 产物结构 */
--primary: rgb(21, 93, 252);               /* 所有浏览器 */

@supports (color: oklab(0% 0 0)) {
  --primary: oklch(63.7% 0.237 25.331);    /* 仅新浏览器覆盖 */
}
```

旧浏览器跳过 `@supports` 块，用 `rgb()`。新浏览器两段都执行，`oklch` 覆盖 `rgb`。零体验降级。

### 构建管线调整

Vite 8 默认用 Lightning CSS 做 CSS minifier，与 Tailwind v4 内嵌的 Lightning CSS 实例冲突。改回 Vite 7 行为：

```ts
// vite.config.ts
css: { transformer: 'postcss' },
build: { cssMinify: 'esbuild' },
```

### color-mix 保护

`color-mix()` 由 Tailwind v4 自己的 `@supports` 机制兜底，不需要额外处理：

```css
/* Tailwind 为带透明度的 utility 生成双重规则 */
.bg-primary\/20 { background: var(--color-primary); }  /* 基础 */
@supports (color: color-mix(in lab, red, red)) {
  .bg-primary\/20 { background: color-mix(in oklab, var(--color-primary) 20%, transparent); }
}
```

旧浏览器跳过 `@supports`，用不透明的基础色（视觉差异轻微）。

## 改动清单

| 文件 | 改动 |
|---|---|
| `frontend/vite.config.ts` | `css.transformer: 'postcss'` + `build.cssMinify: 'esbuild'` |
| `frontend/postcss.config.js` | 新建，`@csstools/postcss-oklab-function` + `preserve: true` |
| `frontend/package.json` | 新 devDeps: `postcss`, `@csstools/postcss-oklab-function`, `esbuild` |

## 附带修复的 Pre-existing Bugs

### `hsl(var(--xxx))` 无效 CSS

`index.css` 滚动条和 `SubagentCard.css` 中用 `hsl(var(--border))` / `hsl(var(--muted))` 写法——Tailwind v3 时代变量存的是裸数字（`220 13% 91%`），v4 切换成 `oklch()` 后不再兼容。在所有浏览器上均无效。

- `index.css`: `hsl(var(--border))` → `var(--border)`（3 处）
- `SubagentCard.css`: `hsl(var(--muted) / 0.15)` → `color-mix(in oklab, var(--muted) 15%, transparent)`

### 构建产物膨胀

引入 `preserve: true` 后 CSS 从 ~133 KB 增至 ~144 KB（+8%）。每条颜色变量额外增加 rgb fallback + @supports 包裹。

## 不影响正常用户

- 现代 WebView2（Chrome ≥ 111）：`@supports` 命中，继续使用 `oklch` 全饱和颜色，零体验变化
- 旧 WebView2：自动使用 `rgb` 降级，4 个超出 sRGB 色域的强调色略微降饱和（肉眼几不可辨）
- JS 构建目标保持默认（Chrome 111），不降级——低于 Chrome 90 的版本市占率 ≈ 0%

## 参考

- [Tailwind CSS v4 oklch compatibility discussion](https://github.com/tailwindlabs/tailwindcss/discussions/17191)
- [@csstools/postcss-oklab-function](https://www.npmjs.com/package/@csstools/postcss-oklab-function)
- [Lightning CSS transpilation docs](https://lightningcss.dev/transpilation.html)
- [Vite 8 + Tailwind v4 Lightning CSS conflict](https://github.com/tailwindlabs/tailwindcss/issues/14205)
