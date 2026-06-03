#!/bin/bash
set -euo pipefail

OS=$(uname -s)
RUNTIME_DIR="${RUNTIME_DIR:-build/runtime}"
GIT_VERSION="2.47.1"

download_mingit() {
    local url="https://github.com/git-for-windows/git/releases/download/v${GIT_VERSION}.windows.2/MinGit-${GIT_VERSION}-64-bit.zip"

    mkdir -p "$RUNTIME_DIR/git"
    rm -rf "$RUNTIME_DIR/git"/*
    echo "下载 MinGit ${GIT_VERSION}..."

    if ! curl -fsSL --retry 3 --connect-timeout 30 -o /tmp/mingit.zip "$url"; then
        local mirror="https://ghproxy.net/${url}"
        echo "GitHub 直连失败，尝试镜像..."
        curl -fsSL --retry 3 --connect-timeout 30 -o /tmp/mingit.zip "$mirror"
    fi

    # 校验下载的不是 HTML 错误页
    if file /tmp/mingit.zip | grep -qi "html"; then
        echo "错误: 下载的内容是 HTML 页面，非有效压缩包"
        head -5 /tmp/mingit.zip
        exit 1
    fi

    unzip -qo /tmp/mingit.zip -d "$RUNTIME_DIR/git"
    rm -f /tmp/mingit.zip
    echo "MinGit → $RUNTIME_DIR/git"
}

copy_native_git() {
    # Linux: 直接复制系统 git（AppImage 打包时由 linuxdeploy 处理依赖）
    # macOS: 复制系统 git（放在 .app bundle 内）
    local git_bin
    git_bin=$(which git 2>/dev/null || echo "/usr/bin/git")
    if [ ! -f "$git_bin" ]; then
        echo "错误: 未找到系统 Git，请先安装 Git"
        exit 1
    fi
    mkdir -p "$RUNTIME_DIR/git"
    cp "$git_bin" "$RUNTIME_DIR/git/git"
    chmod +x "$RUNTIME_DIR/git/git"
    echo "Git → $RUNTIME_DIR/git/git ($(du -h "$RUNTIME_DIR/git/git" | cut -f1))"
}

case "${OS}" in
    MINGW*|MSYS*|CYGWIN*) download_mingit ;;
    Linux)                 copy_native_git ;;
    Darwin)                copy_native_git ;;
    *)                     echo "不支持的操作系统: $OS"; exit 1 ;;
esac
