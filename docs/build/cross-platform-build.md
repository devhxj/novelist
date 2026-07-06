# Novelist 跨平台构建与分发方案

## 设计概览

Novelist 以 .NET 10 应用作为桌面宿主，Photino.NET 负责原生窗口，React/Vite 产物作为本地静态资源随包分发。发布产物必须包含：

- `Novelist.App` 发布输出；
- `frontend/dist` 前端资源；
- `runtime/git`，用于每本小说的本地版本历史；
- 可选 `runtime/models`，固定内置 `bge-small-zh-v1.5` int8 embedding 模型；
- NuGet 发布资产中的 ONNX Runtime 和 sqlite-vec 原生库，用于本地 embedding 与语义检索；
- 可选 `runtime/onnx` 和 `sqlite-vec/{rid}` 覆盖目录，用于替换或补充 NuGet 自带运行时资产；
- 平台安装包元数据和图标。

## 关键决策

| 决策 | 结论 | 原因 |
| --- | --- | --- |
| 桌面宿主 | .NET 10 + Photino.NET | 保持轻量桌面壳，并复用现有 React UI |
| 前端资源 | 先 `npm run build`，再复制到发布目录 | 运行时不依赖 Node.js |
| Git 运行时 | 随发布产物打包，找不到时再查系统 PATH | 小说工作区版本历史必须稳定可用 |
| Embeddings API | 设置中自由配置供应商、模型和维度 | 在线模式不限制具体服务商 |
| ONNX embedding | 固定随包 `runtime/models`，ONNX Runtime 由 NuGet 发布资产提供 | 高性能设备可本地生成向量，不必使用在线 Embeddings API |
| sqlite-vec | NuGet 发布资产提供 `vec0.*`，同时支持覆盖目录和环境变量 | 不同平台文件名不同，解析逻辑集中在 .NET 层 |
| 安装包 | Inno Setup / AppImage / DMG | 使用各平台常见格式 |
| 用户数据 | 默认进入系统应用数据目录 | 安装目录只放程序文件，用户数据可迁移、可备份 |

## 发布目录结构

`scripts/novelist-publish.sh {rid}` 会生成：

```text
build/bin/novelist/
├── novelist(.exe)                 # 入口别名
├── Novelist.App(.exe)             # .NET 应用宿主
├── *.dll / *.json                 # .NET 运行文件
├── frontend/
│   └── dist/
│       ├── index.html
│       └── assets/
├── runtime/
│   ├── git/
│   ├── models/                     # 可选，固定 BGE 模型
│   └── onnx/                       # 可选，ONNX Runtime 覆盖目录
├── runtimes/
│   └── {rid}/native/                # NuGet 运行时资产，含 onnxruntime.* 和 vec0.*
└── sqlite-vec/                    # 可选
    └── {rid}/
```

运行时解析顺序：

- 前端资源：从应用内容根向上查找 `frontend/dist/index.html`，发布目录内的 `frontend/dist` 会被命中。
- Git：先查 `AppContext.BaseDirectory/runtime/git/...`，再查系统 PATH。
- ONNX：本地 Embeddings 固定使用 `runtime/models/model.onnx` 与 `runtime/models/vocab.txt`，模型为 `Xenova/bge-small-zh-v1.5` 的 `onnx/model_int8.onnx`，512 维。`Microsoft.ML.OnnxRuntime` 是强引用 NuGet 依赖，托管程序集和 `onnxruntime.*` 会随 `dotnet publish` 进入发布输出；`OnnxRuntimePath`、`runtime/onnx` 和 `runtimes/{rid}/native` 仅作为原生库额外搜索位置。ONNX 模式不会自动回退到在线 API。
- sqlite-vec：`sqlite-vec` 是 NuGet 依赖，`vec0.*` 会随发布进入 `runtimes/{rid}/native`。解析器还会查 `native`、`sqlite-vec/{rid}` 和应用根目录；也可设置 `NOVELIST_SQLITE_VEC_PATH`。

## 本地构建

```bash
dotnet restore Novelist.slnx
npm --prefix frontend ci
npm --prefix frontend run build
dotnet test Novelist.slnx --no-restore -v minimal
```

发布当前平台：

```bash
make deps
make build
```

发布指定平台自包含产物：

```bash
bash scripts/novelist-publish.sh win-x64
bash scripts/novelist-publish.sh linux-x64
bash scripts/novelist-publish.sh osx-arm64
```

## 平台打包

### Windows

```bash
VERSION=1.0.0 make package-windows
```

流程：

1. 下载/复用 MinGit；
2. 构建前端；
3. 发布 `win-x64` 自包含输出；
4. Inno Setup 读取 `build/package/windows/setup.iss`；
5. 输出 `build/dist/novelist-v{VERSION}-windows-amd64.exe`。

### Linux

```bash
VERSION=1.0.0 make package-linux
```

流程：

1. 复制系统 `git` 到 `build/runtime/git/git`；
2. 构建前端；
3. 发布 `linux-x64` 自包含输出；
4. `build/package/linux/build-appimage.sh` 生成 AppDir；
5. `appimagetool --appimage-extract-and-run` 输出 AppImage。

### macOS

```bash
VERSION=1.0.0 make package-macos
```

流程：

1. 复制系统 `git` 到 `build/runtime/git/git`；
2. 构建前端；
3. 发布 `osx-arm64` 自包含输出；
4. `build/package/macos/build-dmg.sh` 组装 `Novelist.app`；
5. `hdiutil create` 输出 DMG。

如需 Intel macOS，可显式改用：

```bash
MACOS_RID=osx-x64 VERSION=1.0.0 make package-macos
```

## CI 发布

`.github/workflows/release.yml` 在三个 job 中并行执行：

- setup-dotnet 读取 `global.json`；
- setup-node 使用 `frontend/package-lock.json` 缓存；
- 缓存/下载 Git 运行时；
- `npm --prefix frontend ci && npm --prefix frontend run build`；
- `bash scripts/novelist-publish.sh {rid}`；
- 调用平台打包脚本；
- 上传 `build/dist/*`。

打 tag 时，`release` job 会下载三平台 artifact，生成 `sha256sums.txt`，并创建 GitHub Release。

## 常见问题

### 发布脚本提示缺少前端资源

先运行：

```bash
npm --prefix frontend run build
```

发布脚本要求 `frontend/dist/index.html` 已存在，避免产物缺 UI 时仍被打包。

### Git 运行时缺失

先运行：

```bash
make deps
```

也可以在机器上安装 Git。运行时会先尝试随包 Git，再查系统 PATH。

### sqlite-vec 状态为 not_found

正常发布产物应包含 NuGet 提供的 `vec0.*`：

```text
build/bin/novelist/runtimes/{rid}/native/vec0.{dll|so|dylib}
```

如果需要覆盖，也可以放在发布目录可解析的位置：

```text
build/bin/novelist/sqlite-vec/{rid}/vec0.{dll|so|dylib}
```

或设置：

```bash
export NOVELIST_SQLITE_VEC_PATH=/absolute/path/to/sqlite_vec.so
```

### ONNX 模式提示运行时或模型不可用

正常发布产物应包含 NuGet 提供的 ONNX Runtime 托管程序集和原生库：

```text
build/bin/novelist/Microsoft.ML.OnnxRuntime.dll
build/bin/novelist/runtimes/{rid}/native/onnxruntime.{dll|so|dylib}
```

如果需要覆盖原生库，可在设置中填写运行时目录，或放入：

```text
build/bin/novelist/runtime/onnx/runtimes/{rid}/native/onnxruntime.{dll|so|dylib}
```

固定模型和词表应随包放在：

```text
build/bin/novelist/runtime/models/model.onnx
build/bin/novelist/runtime/models/vocab.txt
```

本地 ONNX embedding 固定使用 BGE 的 `input_ids`、`attention_mask`、可选 `token_type_ids`，输出为 `last_hidden_state` 时使用 CLS pooling + L2 归一化；查询向量会加固定中文检索前缀。ONNX 模式严格使用本地模型，测试或重建失败时不会切到在线 Embeddings API。

### AppImage 工具依赖 FUSE

CI 和脚本使用 `appimagetool --appimage-extract-and-run`，不需要系统挂载 FUSE。若本地手动运行失败，请使用仓库脚本而不是直接双击 appimagetool。

### Windows 安装脚本找不到文件

Windows 安装脚本从 `build/package/windows/setup.iss` 相对定位 `build/bin/novelist/*`。先确认：

```bash
bash scripts/novelist-publish.sh win-x64
test -f build/bin/novelist/novelist.exe
```

再运行 Inno Setup。
