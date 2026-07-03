.PHONY: dev build frontend-dev frontend-build clean deps package test publish

APP_NAME  := novelist
VERSION   ?= $(shell git describe --tags --always --dirty 2>/dev/null || echo "dev")
BUILD_DIR := build
RID       ?=
WINDOWS_RID ?= win-x64
LINUX_RID   ?= linux-x64
MACOS_RID   ?= osx-arm64

# 启动 Photino/.NET 桌面模式
dev:
	dotnet run --project src/Novelist.App/Novelist.App.csproj -- --desktop

# 下载运行时依赖（Git），已有则跳过
deps:
	@if [ ! -f "$(BUILD_DIR)/runtime/git/git" ] && [ ! -d "$(BUILD_DIR)/runtime/git/mingw64" ]; then \
		bash scripts/download-git.sh; \
	else \
		echo "Git runtime 已存在，跳过下载"; \
	fi

# 构建前端
frontend:
	cd frontend && npm ci && npm run build

# .NET 测试
test:
	dotnet test Novelist.slnx --no-restore -v minimal

# 发布输出目录 build/bin/novelist；设置 RID=win-x64/linux-x64/osx-arm64 可生成自包含产物
publish:
	bash scripts/novelist-publish.sh $(RID)

# 生产构建（需先 deps）
build: deps frontend publish

# 纯前端开发（浏览器模式，后端不可用）
frontend-dev:
	cd frontend && npm run dev

# 纯前端构建
frontend-build:
	cd frontend && npm run build

# 打包（按当前平台）
package:
	@case "$$(uname -s)" in \
		MINGW*|MSYS*|CYGWIN*) $(MAKE) package-windows ;; \
		Linux)                $(MAKE) package-linux ;; \
		Darwin)               $(MAKE) package-macos ;; \
		*) echo "请使用 package-windows / package-linux / package-macos"; exit 1 ;; \
	esac

# Windows Inno Setup 安装包
package-windows: deps frontend
	bash scripts/novelist-publish.sh $(WINDOWS_RID)
	export VERSION=$(VERSION) && iscc $(BUILD_DIR)/package/windows/setup.iss

# Linux AppImage
package-linux: deps frontend
	bash scripts/novelist-publish.sh $(LINUX_RID)
	bash $(BUILD_DIR)/package/linux/build-appimage.sh $(VERSION)

# macOS DMG
package-macos: deps frontend
	bash scripts/novelist-publish.sh $(MACOS_RID)
	bash $(BUILD_DIR)/package/macos/build-dmg.sh $(VERSION)

# 清理构建产物
clean:
	rm -rf frontend/dist frontend/node_modules $(BUILD_DIR)/runtime $(BUILD_DIR)/dist $(BUILD_DIR)/bin $(BUILD_DIR)/*.AppDir $(APP_NAME) $(APP_NAME).exe
