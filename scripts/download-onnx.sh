#!/bin/bash
set -euo pipefail

OS=$(uname -s)
RUNTIME_DIR="${RUNTIME_DIR:-build/runtime}"
ONNX_VERSION="1.21.0"

download_onnx() {
    local os_tag="$1"
    local file="$2"
    local url="https://github.com/microsoft/onnxruntime/releases/download/v${ONNX_VERSION}/${file}"

    mkdir -p "$RUNTIME_DIR"
    echo "下载 ONNX Runtime ${ONNX_VERSION} (${os_tag})..."

    if ! curl -fsSL --retry 3 --connect-timeout 30 -o "/tmp/${file}" "$url"; then
        local mirror="https://ghproxy.net/${url}"
        echo "GitHub 直连失败，尝试镜像..."
        curl -fsSL --retry 3 --connect-timeout 30 -o "/tmp/${file}" "$mirror"
    fi

    # 校验下载内容不是 HTML 错误页
    if file "/tmp/${file}" | grep -qi "html"; then
        echo "错误: 下载的内容是 HTML 页面，非有效压缩包"
        head -5 "/tmp/${file}"
        exit 1
    fi

    echo "解压..."
    rm -rf /tmp/onnx-extract
    if [[ "$file" == *.zip ]]; then
        unzip -qo "/tmp/${file}" -d /tmp/onnx-extract
    else
        mkdir -p /tmp/onnx-extract
        tar -xzf "/tmp/${file}" -C /tmp/onnx-extract
    fi

    # ONNX Runtime 包结构固定: <name>/lib/ 下有所有库文件和 .pc
    local lib_dir
    lib_dir=$(find /tmp/onnx-extract -type d -name "lib" | head -1)
    if [ -z "$lib_dir" ]; then
        echo "错误: 未找到 lib 目录，包结构如下:"
        find /tmp/onnx-extract -type f | head -20
        exit 1
    fi
    # 只复制库文件和 .pc 文件，跳过 .dSYM 调试目录
    find "$lib_dir" -maxdepth 1 -type f \( -name "libonnxruntime*" -o -name "*.pc" \) -exec cp {} "$RUNTIME_DIR/" \;

    rm -rf /tmp/onnx-extract "/tmp/${file}"
    echo "ONNX Runtime → $RUNTIME_DIR/"
    ls -la "$RUNTIME_DIR/"
}

case "${OS}" in
    MINGW*|MSYS*|CYGWIN*)
        download_onnx "win-x64" "onnxruntime-win-x64-${ONNX_VERSION}.zip"
        ;;
    Linux)
        download_onnx "linux-x64" "onnxruntime-linux-x64-${ONNX_VERSION}.tgz"
        ;;
    Darwin)
        download_onnx "osx-universal2" "onnxruntime-osx-universal2-${ONNX_VERSION}.tgz"
        ;;
    *)
        echo "不支持的操作系统: $OS"
        exit 1
        ;;
esac
