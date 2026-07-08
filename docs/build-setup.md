# Novelist 构建环境搭建

本文档记录当前 Novelist 桌面应用的构建、测试和发布路径。桌面宿主为 .NET 10 + Photino.NET，前端为 React/Vite，语义检索使用标准 Embeddings API 或本地 ONNX embedding + sqlite-vec。

当前发版路径先只维护 Windows 安装包。Linux/macOS 打包脚本可以作为后续恢复跨平台分发时的参考，但不进入当前 GitHub Release workflow。

## 本地依赖

| 依赖 | 推荐版本 | 用途 |
| --- | --- | --- |
| .NET SDK | `global.json` 固定的 `10.0.301` | 编译和发布 `Novelist.App` |
| Node.js | 24.x | 安装前端依赖并运行 Vite 构建 |
| npm | 随 Node.js | 前端依赖锁定安装 |
| Git Bash | Git for Windows 自带 | 运行 `scripts/*.sh` |
| Git | 任意近期版本 | 本地开发、测试和运行时版本历史；当前 Windows 安装包不内置 Git |
| Inno Setup | 6.x | 生成 Windows 安装包 |

## 依赖安装

从仓库根目录运行：

```bash
dotnet restore Novelist.slnx
npm --prefix frontend ci
```

ONNX Runtime 和 sqlite-vec 已通过 `Novelist.Infrastructure` 的 NuGet 引用进入项目：`dotnet publish` 会把 `Microsoft.ML.OnnxRuntime.dll`、`onnxruntime.*` 和 `vec0.*` 这些平台运行时资产放进发布输出。随包提供本地 ONNX embedding 时，只需要把固定 BGE 模型放到 `build/runtime/models/`；发布脚本会复制到应用的 `runtime/models/`。

```text
build/runtime/
└── models/
    ├── model.onnx                   # Xenova/bge-small-zh-v1.5 onnx/model_int8.onnx
    └── vocab.txt
```

本地 ONNX 模式在设置中选择 `ONNX` 即可，模型固定为 `bge-small-zh-v1.5` int8、512 维、512 token、CLS pooling + L2 归一化；该模式不会回退到在线 Embeddings API。在线 Embeddings API 模式不限制供应商、模型或维度。

通常不需要手工放置 sqlite-vec 原生库。仅在需要覆盖 NuGet 自带 sqlite-vec 运行时资产时，才使用这些可选目录或环境变量：

```text
build/runtime/sqlite-vec/
└── {rid}/
    └── vec0.{dll|so|dylib}
```

也可以在运行时用 `NOVELIST_SQLITE_VEC_PATH` 指向明确的 sqlite-vec 扩展文件。ONNX Runtime 不提供手工覆盖路径；请以 `Microsoft.ML.OnnxRuntime` NuGet 发布资产为准。

## 开发模式

启动桌面应用：

```bash
npm --prefix frontend run build
dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop
```

桌面启动不隐式执行前端构建；如果 `frontend/dist/index.html` 不存在，Photino 会提示先构建前端。需要热更新前端时，先运行 Vite，再用 `--start-url=http://localhost:5173/` 的桌面启动配置加载 Vite 页面。

只启动前端开发服务器：

```bash
npm --prefix frontend run dev
```

只启动前端时，桌面桥接 API 不可用；它适合做纯界面调试。

## 构建与测试

常用验证命令：

```bash
npm --prefix frontend run build
dotnet test Novelist.slnx --no-restore -v minimal
```

发布桌面输出：

```bash
npm --prefix frontend run build
bash scripts/novelist-publish.sh win-x64
```

发布输出位于 `build/bin/novelist/`。发布安装包时应使用 `win-x64` 的自包含输出。

## 发布产物

当前发布脚本入口：

```bash
bash scripts/novelist-publish.sh win-x64
```

发布脚本会：

- 调用 `dotnet publish src/Novelist.App/Novelist.App.csproj`；
- 要求 `frontend/dist/index.html` 已存在；
- 复制前端静态资源到 `build/bin/novelist/frontend/dist/`；
- 保留 `dotnet publish` 产生的 NuGet 运行时资产，包括 ONNX Runtime 和 sqlite-vec；
- 复制可选的 `build/runtime/models/` 到发布目录；
- 复制可选的 `build/runtime/sqlite-vec/` 覆盖目录；
- 生成 `novelist` 或 `novelist.exe` 入口别名。

Windows 打包入口：

```bash
VERSION=1.0.0 bash scripts/novelist-package-windows.sh
```

输出目录为 `build/dist/`，当前发版产物为：

- Windows: `novelist-v{VERSION}-windows-amd64.exe`

不显式传 `VERSION` 时，脚本由 MinVer 根据 Git tag 解析版本。Release tag 使用 `vX.Y.Z` 或 `vX.Y.Z-prerelease`，安装包文件名会去掉前缀 `v`。

## CI 路径

`.github/workflows/test.yml` 执行：

1. 安装 .NET SDK；
2. 安装 Node.js；
3. `dotnet restore Novelist.slnx`；
4. `npm --prefix frontend ci`；
5. `npm --prefix frontend run build`；
6. `dotnet test Novelist.slnx --no-restore -v minimal`。

`.github/workflows/release.yml` 只构建 Windows 安装器。workflow 使用完整 Git history/tag 运行 MinVer，校验 `v*` release tag 与 MinVer 解析结果一致，然后上传 `.exe` 和 `sha256sums.txt` 到 GitHub Release。安装包不内置 Git；本地版本历史使用用户系统中的 Git。

## 清理

```bash
rm -rf frontend/dist frontend/node_modules build/runtime build/dist build/bin build/version build/*.AppDir novelist novelist.exe
```

该命令会删除前端构建输出、前端依赖目录、发布目录和运行时缓存。用户数据目录不在仓库内，不会被清理。
