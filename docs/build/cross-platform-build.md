# 跨平台构建与分发方案

## 设计概览

Goink 是一个 [Wails v2](https://wails.io) 桌面应用，需要在 Windows、Linux、macOS 三平台构建和分发。核心依赖：Git CLI（本地版本控制）和 ONNX Runtime 动态库（文本向量化）。

### 关键决策

| 决策 | 结论 | 原因 |
|------|------|------|
| Git CLI | 捆绑自带，不从系统 PATH 查找 | 行为一致、版本可控、不依赖用户安装 |
| go-git | **放弃** | 不支持 `git revert`，这是核心功能 |
| ONNX Runtime | 捆绑自带动态库 | 体积 ~15MB，值得打包 |
| ONNX 模型 | 运行时从 HF 镜像下载 | ~400MB，太大不放进安装包 |
| 安装包 | NSIS (Win) / AppImage (Linux) / DMG (macOS) | 各自平台标准格式 |
| 数据目录 | 不依赖 config.json | Windows=exe同目录，其他=~/Goink/ |

### 目录结构

#### 安装包内（构建时产生）

```
{D:\Goink\ | /Applications/Goink.app/ | AppImage 内}
├── goink / goink.exe              ← Wails 产物
├── runtime/                       ← 构建脚本下载，安装器写入
│   ├── git/                       ← Windows: MinGit; Linux/macOS: 单文件 git
│   │   └── mingw64/bin/git.exe    ← 仅 Windows
│   └── onnxruntime.{dll|so|dylib}
└── [models/ novels/ ...]          ← 运行时产生，安装器不动
```

#### 仓库内

```
├── internal/platform/runtime.go   ← AppDir, ResolveGit, ResolveOnnxLib, DataDir
├── scripts/
│   ├── download-git.sh            ← 按平台获取 Git
│   └── download-onnx.sh           ← 按平台下载 ONNX Runtime
├── build/package/
│   ├── windows/installer.nsi      ← NSIS 安装脚本
│   ├── linux/build-appimage.sh    ← AppImage 构建
│   └── macos/build-dmg.sh         ← DMG 构建
├── .github/workflows/release.yml  ← CI 流水线
└── Makefile                       ← deps / build / package 目标
```

## 代码架构

### 运行时路径解析

`internal/platform/runtime.go` 提供四个函数：

- `AppDir()` — `os.Executable()` + `filepath.Dir`，返回 exe 所在目录
- `ResolveGit()` — `<appdir>/runtime/git/...` → `exec.LookPath("git")` fallback
- `ResolveOnnxLib()` — `<appdir>/runtime/<lib>` → 系统路径 fallback
- `DataDir()` — Windows 返回 AppDir，其他返回 `~/Goink/`

### 调用链改动

```
internal/git/repo.go:  exec.LookPath("git") → platform.ResolveGit()
main.go:               findOnnxLib() 删除     → platform.ResolveOnnxLib()
internal/config/config.go: DataDir/ModelsDir/GlobalDBPath/NovelDirPath
                         从 *AppConfig 方法 → 独立函数，内部调用 platform.DataDir()
app/handler.go:        OnStartup 首次启动自动初始化平台默认目录
                       不再等用户选 data_dir
```

`config.json` 文件保留但 data_dir 字段不再作为数据目录来源。`AppConfig.DataDir` 字段保留用于未来扩展。

## 构建流程

### 本地

```bash
make deps              # 下载 Git + ONNX Runtime 到 build/runtime/（幂等）
make build             # deps + frontend + wails build
make package           # 按当前平台打包
make package-linux     # 指定 Linux AppImage
```

### CI

```yaml
触发条件:
  - tag push (v*)
  - branch push (ci-*)     ← 测试分支
  - workflow_dispatch      ← 手动触发

三平台并行:
  build-windows (windows-latest):
    1. 安装 Go/Node/Wails CLI
    2. 安装 sqlite3 头文件 (CGo 需要，Windows 无系统头文件)
    3. 下载 MinGit + ONNX DLL
    4. npm build + wails build
    5. choco install nsis → makensis 打包
    6. sha256sum → upload-artifact

  build-linux (ubuntu-latest):
    1. apt 装 libgtk-3-dev + libwebkit2gtk-4.1-dev + Wails CLI
    2. 复制系统 git 到 runtime/，下载 ONNX SO
    3. npm build + wails build
    4. appimagetool --appimage-extract-and-run 打包
    5. sha256sum → upload-artifact

  build-macos (macos-latest):
    1. 安装 Wails CLI
    2. 复制系统 git 到 runtime/，下载 ONNX dylib
    3. npm build + wails build（Wails 默认输出 .app bundle）
    4. 将 runtime/ 注入 .app bundle → hdiutil 生成 DMG
    5. shasum -a 256 → upload-artifact

  release:
    needs: [三平台全成功]
    download-artifact → softprops/action-gh-release 发布
```

## 遇到的问题和解决方案

### 1. GitHub 网络不稳定

**现象**：`curl: (35) OpenSSL SSL_connect: SSL_ERROR_SYSCALL`

**原因**：本地和 CI 都可能遇到 GitHub 连接问题（代理、墙）。

**解决**：下载脚本使用 `ghproxy.net` 作为 fallback 镜像；Makefile 的 `deps` 目标幂等（文件已存在则跳过）。

### 2. macOS ONNX 包名错误

**现象**：`curl: (22) 404` — `onnxruntime-osx-universal-1.21.0.tgz`

**原因**：正确的包名是 `onnxruntime-osx-universal2-1.21.0.tgz`（注意 `universal2` 不是 `universal`）。

**解决**：修正 `download-onnx.sh` 中的 macOS 包名。

### 3. macOS ONNX dSYM 目录

**现象**：`cp: .../libonnxruntime.1.21.0.dylib.dSYM is a directory (not copied)`

**原因**：ONNX Runtime macOS 包的 `lib/` 目录包含 `.dSYM` 调试符号目录，`cp` 不带 `-r` 无法复制。

**解决**：改用 `find` 只复制普通文件：`find "$lib_dir" -maxdepth 1 -type f -exec cp {} "$RUNTIME_DIR/" \;`

### 4. macOS Wails 输出 .app bundle

**现象**：`cp: build/bin/goink: No such file or directory`

**原因**：macOS 上 Wails 默认输出 `.app` bundle 而非裸二进制。输出路径为 `build/bin/goink.app/Contents/MacOS/goink`。

**解决**：`build-dmg.sh` 改为直接使用 Wails 生成的 `.app` bundle，将 `runtime/` 注入其 `Contents/Resources/` 和 `Contents/Frameworks/`。

### 5. Linux AppImage FUSE 依赖

**现象**：`dlopen(): error loading libfuse.so.2` — GitHub Actions runner 无 FUSE。

**解决**：使用 `appimagetool --appimage-extract-and-run` 绕过 FUSE 依赖。不需要安装 `libfuse2`。

### 6. Windows MinGit 包名错误

**现象**：`curl: (22) 404` — `MinGit-2.47.1-64-bit.zip`

**原因**：正确包名是 `MinGit-2.47.1.2-64-bit.zip`（版本号格式为四段）。

**解决**：修正 `download-git.sh` 中的包名。

### 7. Windows sqlite3.h 缺失

**现象**：`fatal error: sqlite3.h: No such file or directory` — `sqlite-vec-go-bindings/cgo` 编译失败。

**原因**：Linux/macOS 有系统 sqlite3 头文件，Windows 没有。

**解决**：下载 sqlite3 amalgamation zip（版本 3.44.0，对应 go-sqlite3 v1.14.44），
解压到 `$RUNNER_TEMP/sqlite/`，设置 `CGO_CFLAGS=-I$RUNNER_TEMP/sqlite`。

**坑**：`/tmp/` 路径在 msys2 bash 中不映射到 mingw gcc 的 include 路径，
必须用 `$RUNNER_TEMP`（Windows 原生路径）。

### 8. NSIS 未安装 / 不在 PATH

**现象**：`makensis: command not found`

**解决**：`choco install -y nsis`，然后通过 msys2 路径结构添加到 PATH：
`export PATH="/c/Program Files (x86)/NSIS:$PATH"`

### 9. NSIS File 命令 "no files found"（CWD 陷阱）

**现象**：
```
ls -la build/bin/
-rwxr-xr-x ... goink.exe    ← 文件确实存在

makensis ...
File: "build\bin\goink.exe" -> no files found.    ← NSIS 找不到
```

**根因**：makensis 默认在编译前将 CWD 切换到 .nsi 脚本所在目录。
`File "build\bin\goink.exe"` 实际查找的是 `build/package/windows/build/bin/goink.exe`（不存在），
而不是相对于仓库根目录的 `build/bin/goink.exe`。

**解决**：CI 中用 `cygpath -w "$PWD"` 获取仓库根目录的 Windows 绝对路径，
通过 `-DSOURCE_DIR` 传递给 makensis，nsi 文件使用 `${SOURCE_DIR}\build\bin\goink.exe`。

### 10. Windows ONNX DLL 未复制（命名差异）

**现象**：`ls build/runtime/` 显示 `total 0`，只有 `git/` 目录，无 ONNX dll。

**根因**：Windows ONNX Runtime 的 DLL 名为 `onnxruntime.dll`（无 `lib` 前缀），
但 `download-onnx.sh` 中的 find 模式为 `-name "libonnxruntime*"`，不匹配。

**解决**：将 find 模式改为 `-name "*onnxruntime*"`，同时匹配 `libonnxruntime` 和 `onnxruntime`。

## CI 监控方法

### 查看最近的运行

```bash
# 列出指定分支最近的运行
gh run list -b ci-build-test --limit 5

# 查看运行摘要（JSON）
gh run list -b ci-build-test --limit 1 --json databaseId,name,conclusion,status

# 查看所有 job 状态
gh run view <RUN_ID> --json jobs -q '.jobs[] | "\(.name): \(.status) [\(.conclusion)]"'
```

### 等待运行完成

```bash
# 阻塞等待直到完成
RUN=$(gh run list -b ci-build-test --limit 1 --json databaseId -q '.[0].databaseId')
until [ "$(gh run view $RUN --json conclusion -q '.conclusion')" != "" ]; do
  sleep 20
done
gh run view $RUN --json jobs -q '.jobs[] | "\(.name): \(.conclusion)"'

# 只等 Windows job（其他已结束）
while [ "$(gh run view $RUN --json jobs -q '.jobs[] | select(.name=="build-windows") | .status')" = "in_progress" ]; do
  sleep 30
done
```

### 查看日志

```bash
# 查看失败步骤的日志
gh run view <RUN_ID> --log-failed

# 查看特定 job 的日志 + 过滤
gh run view <RUN_ID> --log --job <JOB_ID> 2>&1 | grep -E "error|fatal|Built"

# 获取 job ID
gh run view <RUN_ID> --json jobs -q '.jobs[] | select(.name=="build-windows") | .databaseId'
```

### 查看完整错误上下文

```bash
# 查看失败步骤前后的日志
RUN=$(gh run list -b ci-build-test --limit 1 --json databaseId -q '.[0].databaseId')
JOB=$(gh run view $RUN --json jobs -q '.jobs[] | select(.name=="build-windows") | .databaseId')
gh run view $RUN --log --job $JOB | grep -B3 -A10 "error\|Error\|fatal"
```

### 实时查看运行

```bash
# 交互式查看
gh run watch <RUN_ID>

# 在浏览器打开
gh run view <RUN_ID> --web
```
