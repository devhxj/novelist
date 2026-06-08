package main

import (
	"embed"
	"log/slog"
	"os"
	"runtime"

	ort "github.com/yalue/onnxruntime_go"

	"github.com/wailsapp/wails/v2"
	"github.com/wailsapp/wails/v2/pkg/options"
	"github.com/wailsapp/wails/v2/pkg/options/assetserver"

	"novel/app"
	"novel/internal/logger"
	"novel/internal/platform"
)

//go:embed all:frontend/dist
var assets embed.FS

func main() {
	log := logger.Default()

	if lib, err := platform.ResolveOnnxLib(); err == nil {
		ort.SetSharedLibraryPath(lib)
		log.Info("ONNX 运行库已设置", "path", lib)
	} else {
		log.Warn("未找到 ONNX Runtime 库，向量检索将不可用", "err", err)
	}

	wapp := app.New(log)

	err := wails.Run(&options.App{
		Title:     "Goink",
		Width:     1400,
		Height:    900,
		MinWidth:  900,
		MinHeight: 600,
		Frameless: runtime.GOOS != "darwin", // macOS 用原生标题栏
		AssetServer: &assetserver.Options{
			Assets: assets,
		},
		OnStartup:  wapp.OnStartup,
		OnShutdown: wapp.OnShutdown,
		Bind: []any{
			wapp,
		},
	})
	if err != nil {
		slog.Error("应用退出", "err", err)
		os.Exit(1)
	}
}
