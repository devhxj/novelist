# Novelist 构建环境搭建

本文档记录当前 Novelist 桌面应用的构建、测试和发布路径。桌面宿主为 .NET 10 + Photino.NET，前端为 React/Vite，语义检索使用标准 Embeddings API 或本地 ONNX embedding + sqlite-vec。

## 本地依赖

| 依赖 | 推荐版本 | 用途 |
| --- | --- | --- |
| .NET SDK | `global.json` 固定的 `10.0.301` | 编译和发布 `Novelist.App` |
| Node.js | 24.x | 安装前端依赖并运行 Vite 构建 |
| npm | 随 Node.js | 前端依赖锁定安装 |
| Bash | Git Bash、WSL、Linux shell 或 macOS shell | 运行 `scripts/*.sh` 和 Makefile 目标 |
| Git | 任意近期版本 | 本地开发、测试和运行时版本历史 |

Linux 桌面运行还需要系统 WebView/GTK 运行库。Ubuntu/Debian 可安装：

```bash
sudo apt-get update
sudo apt-get install -y libgtk-3-0 libwebkit2gtk-4.1-0
```

如果需要在 Linux 上从源码编译/打包 AppImage，建议同时安装：

```bash
sudo apt-get install -y curl file unzip
```

## 依赖安装

从仓库根目录运行：

```bash
dotnet restore Novelist.slnx
npm --prefix frontend ci
make deps
```

`make deps` 只准备打包所需的 Git 运行时，输出到 `build/runtime/git/`。ONNX Runtime 和 sqlite-vec 已通过 `Novelist.Infrastructure` 的 NuGet 引用进入项目：`dotnet publish` 会把 `Microsoft.ML.OnnxRuntime.dll`、`onnxruntime.*` 和 `vec0.*` 这些平台运行时资产放进发布输出。随包提供本地 ONNX embedding 时，只需要把固定 BGE 模型放到 `build/runtime/models/`；发布脚本会复制到应用的 `runtime/models/`。

```text
build/runtime/
└── models/
    ├── model.onnx                   # Xenova/bge-small-zh-v1.5 onnx/model_int8.onnx
    └── vocab.txt
```

本地 ONNX 模式在设置中选择 `ONNX` 即可，模型固定为 `bge-small-zh-v1.5` int8、512 维、512 token、CLS pooling + L2 归一化；该模式不会回退到在线 Embeddings API。在线 Embeddings API 模式不限制供应商、模型或维度。

通常不需要手工放置 ONNX Runtime 或 sqlite-vec 原生库。仅在需要覆盖 NuGet 自带运行时资产时，才使用这些可选目录或环境变量：

```text
build/runtime/onnx/
└── runtimes/{rid}/native/onnxruntime.{dll|so|dylib}

build/runtime/sqlite-vec/
└── {rid}/
    └── vec0.{dll|so|dylib}
```

也可以在运行时用 `OnnxRuntimePath` 增加 ONNX 原生库搜索目录，或用 `NOVELIST_SQLITE_VEC_PATH` 指向明确的 sqlite-vec 扩展文件。

## 开发模式

启动桌面应用：

```bash
npm --prefix frontend run build
make dev
```

`make dev` 保持快速后端/桌面启动，不隐式执行前端构建；如果 `frontend/dist/index.html` 不存在，Photino 会提示先构建前端。需要热更新前端时，先运行 `make frontend-dev`，再用 `--start-url=http://localhost:5173/` 的桌面启动配置加载 Vite 页面。

只启动前端开发服务器：

```bash
make frontend-dev
```

只启动前端时，桌面桥接 API 不可用；它适合做纯界面调试。

## 构建与测试

常用验证命令：

```bash
npm --prefix frontend run build
dotnet test Novelist.slnx --no-restore -v minimal
```

Makefile 等价入口：

```bash
make frontend-build
make test
make build
```

`make build` 会准备运行时依赖、构建前端，并发布到 `build/bin/novelist/`。默认发布为当前平台 framework-dependent 输出；发布安装包时应使用对应 RID 的自包含输出。

## 发布产物

通用发布脚本：

```bash
bash scripts/novelist-publish.sh win-x64
bash scripts/novelist-publish.sh linux-x64
bash scripts/novelist-publish.sh osx-arm64
```

发布脚本会：

- 调用 `dotnet publish src/Novelist.App/Novelist.App.csproj`；
- 要求 `frontend/dist/index.html` 已存在；
- 复制前端静态资源到 `build/bin/novelist/frontend/dist/`；
- 复制 `build/runtime/git/` 到发布目录；
- 保留 `dotnet publish` 产生的 NuGet 运行时资产，包括 ONNX Runtime 和 sqlite-vec；
- 复制可选的 `build/runtime/models/` 到发布目录；
- 复制可选的 `build/runtime/onnx/` 和 `build/runtime/sqlite-vec/` 覆盖目录；
- 生成 `novelist` 或 `novelist.exe` 入口别名。

平台打包入口：

```bash
make package-windows
make package-linux
make package-macos
```

输出目录为 `build/dist/`：

- Windows: `novelist-v{VERSION}-windows-amd64.exe`
- Linux: `novelist-v{VERSION}-linux-{ARCH}.AppImage`
- macOS: `novelist-v{VERSION}-macos-arm64.dmg`

`VERSION` 默认取 `git describe --tags --always --dirty`，也可以显式指定：

```bash
VERSION=1.0.0 make package-linux
```

## CI 路径

`.github/workflows/test.yml` 执行：

1. 安装 .NET SDK；
2. 安装 Node.js；
3. `dotnet restore Novelist.slnx`；
4. `npm --prefix frontend ci`；
5. `npm --prefix frontend run build`；
6. `dotnet test Novelist.slnx --no-restore -v minimal`。

`.github/workflows/release.yml` 在三平台分别构建前端、发布自包含 `Novelist.App`，再打包 Windows 安装器、Linux AppImage 和 macOS DMG。

## 清理

```bash
make clean
```

该命令会删除前端构建输出、前端依赖目录、发布目录和运行时缓存。用户数据目录不在仓库内，不会被清理。
