# 构建环境搭建

## 系统依赖

### Linux (Ubuntu/Debian)

```bash
sudo apt install libsqlite3-dev
```

| 依赖 | 原因 |
|------|------|
| `libsqlite3-dev` | `mattn/go-sqlite3` 编译时需要 `sqlite3.h` 头文件 |
| gcc | CGO 编译必需 |

### macOS

```bash
brew install sqlite
```

### Windows

使用 MSYS2 或 WSL，`libsqlite3-dev` 等效包。

## ONNX Runtime 共享库

`libonnxruntime.so`（Linux）/ `onnxruntime.dll`（Windows）/ `libonnxruntime.dylib`（macOS）由以下方式获取：

1. 从 Python venv 中提取（如已有 `onnxruntime` 包）
2. 从 [GitHub Releases](https://github.com/microsoft/onnxruntime/releases) 下载预编译包
3. 应用打包时随二进制分发

路径需通过 `CGO_LDFLAGS` 指定。

## ONNX 模型文件

首次运行时从 [HuggingFace 镜像](https://hf-mirror.com/shibing624/text2vec-base-chinese) 自动下载至 `models/` 目录：

```
models/
├── model.onnx        (~400MB)
├── vocab.txt         (~100KB)
└── tokenizer.json    (~1MB)
```

## Go 依赖

```bash
go mod download
```

全部依赖由 `go.mod` + `go.sum` 锁定版本，无需额外操作。

## CGO 编译标志

```bash
CGO_ENABLED=1 \
CGO_CFLAGS="-I$(go env GOMODCACHE)/github.com/mattn/go-sqlite3@v1.14.44" \
CGO_LDFLAGS="-L/path/to/onnxruntime -lonnxruntime -Wl,-rpath,/path/to/onnxruntime" \
go build ./...
```

`CGO_CFLAGS` 指向 `mattn/go-sqlite3` 模块目录（含 `sqlite3-binding.h`），系统 `sqlite3.h` 已安装则可不设。

## 验证

```bash
# 测试 ONNX 加载 + embedding（需 libonnxruntime.so）
go run -tags cgo ./dev_test/rag_test/cmd/

# RAG 全流程对比测试（需 libonnxruntime.so + ref_data.json）
go run -tags cgo,compare ./dev_test/rag_test/
```
