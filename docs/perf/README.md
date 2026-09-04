# 冷启动与首个主操作性能基线（U10）

> 建立于 2026-09-04。`development-plan.md` P2 要求"建立默认章节路径的冷打开和首个主操作性能基线"，
> 本目录是该基线的归档与复测入口。

## 采集方式

```bash
cd frontend
npm run build          # 产物须为最新
npm run test:app:dist  # 冒烟套件自动采集并写出 perf-timings.json
```

冒烟工作流在两个时点采集页面 `performance` 计时：

- **cold-open-workspace-visible**：工作区外壳（书名 + 聊天面板）可见时——冷启动口径；
- **after-first-chapter-editor**：点击首个章节、编辑器就绪后——首个主操作口径，
  `monacoChunkReady_ms` 为 monaco 按需块的取回完成时刻（自 navigationStart 起算）。

产物位于 `frontend/output/playwright/phase13/smoke-dist/perf-timings.json`，复测后请按
`cold-open-baseline-<date>.json` 命名归档到本目录。

## 基线（2026-09-04，dist 构建后的本地 HTTP 服务）

| 指标 | 值 |
|---|---|
| 冷开外壳 DOMContentLoaded | 37 ms |
| 冷开外壳首次内容绘制（FCP） | 72 ms |
| 冷开取回脚本数 / 字节 | 4 个 / 733,620 B |
| 首个章节工作流墙钟 | 496 ms |
| monaco 按需块就绪（自导航起） | 916 ms |
| 编辑器就绪后累计脚本 | 6 个 / 1,376,387 B |

## 背景

2026-09-03 评审（U10）指出 `WorkspaceView` 单块 5,294 kB（gzip 1,434 kB）直接影响冷启动。
当日落地拆分：monaco（约 2.5MB）、katex（约 520KB）、highlight.js/common（约 152KB）改为
首次使用时加载；主块降为 2,304,995 B。上表"冷开取回 734KB"即拆分后的首屏成本——
外壳可见不再需要解析 monaco。

## 复测判定

冒烟采集不设硬性阈值（本地服务与机器差异大）；复测时对照基线：

- 冷开脚本字节显著上涨（>20%）视为回归信号，先查是否引入了新的首屏静态依赖；
- `monacoChunkReady_ms` 应保持有限（本地 <2s）；若冷开阶段即出现 monaco 取回
  （cold-open 的 `monacoChunkReady_ms` 非 null），说明懒加载边界被破坏。
