# filepath.Join 导致 embed.FS 在 Windows 上失败

## 现象

Windows 构建版所有内置 skill 加载失败，日志显示：

```
skill: 读取 builtin\\brainstorm-composer.md 失败: open builtin\\brainstorm-composer.md: file does not exist
```

## 根因

`internal/skill/parse.go:109` 在扫描内置 skill 时使用 `filepath.Join(dir, entry.Name())` 拼接路径。Windows 上 `filepath.Join` 使用反斜杠 `\`，但 Go 的 `embed.FS` 强制要求正斜杠 `/`（即使 Windows 平台也如此）。

```go
// 错误：Windows 上产生 "builtin\\foo.md"
ParseFS(fsys, filepath.Join("builtin", "foo.md"))

// 正确：始终产生 "builtin/foo.md"
ParseFS(fsys, "builtin"+"/"+"foo.md")
```

## 教训

涉及 `fs.FS` 接口（包括 `embed.FS`、`os.DirFS` 等）的路径拼接，必须用正斜杠 `/`，不能依赖 `filepath.Join`。`io/fs` 包的文档明确：

> Path names passed to Open are UTF-8, unrooted, slash-separated sequences.

- Go 标准库的 `embed.FS` 内部存储路径始终用 `/`
- `os.DirFS` 在 Windows 上也接受 `/`
- `fs.ValidPath()` 拒绝 `\` 分隔符
