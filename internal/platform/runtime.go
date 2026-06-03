package platform

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

// AppDir 返回当前可执行文件所在的目录。
func AppDir() (string, error) {
	exe, err := os.Executable()
	if err != nil {
		return "", fmt.Errorf("platform: 获取可执行文件路径失败: %w", err)
	}
	return filepath.Dir(exe), nil
}

// ResolveGit 返回 git 可执行文件的路径。
// 优先使用 app 自带的 runtime/git/，找不到再从系统 PATH 查找。
func ResolveGit() (string, error) {
	appDir, err := AppDir()
	if err == nil {
		var bundled string
		if runtime.GOOS == "windows" {
			bundled = filepath.Join(appDir, "runtime", "git", "mingw64", "bin", "git.exe")
		} else {
			bundled = filepath.Join(appDir, "runtime", "git", "git")
		}
		if _, err := os.Stat(bundled); err == nil {
			return bundled, nil
		}
	}
	return exec.LookPath("git")
}

// ResolveOnnxLib 返回 ONNX Runtime 动态库的路径。
// 优先使用 app 自带的 runtime/，找不到再查系统路径。
func ResolveOnnxLib() (string, error) {
	libName := onnxLibName()

	appDir, err := AppDir()
	if err == nil {
		bundled := filepath.Join(appDir, "runtime", libName)
		if _, err := os.Stat(bundled); err == nil {
			return bundled, nil
		}
	}

	for _, p := range systemOnnxPaths(libName) {
		if _, err := os.Stat(p); err == nil {
			return p, nil
		}
	}
	return "", fmt.Errorf("platform: ONNX Runtime 库未找到（%s）", libName)
}

// DataDir 返回应用数据目录（绝对路径）。
// Windows 返回可执行文件所在目录（单目录安装模式），其他平台返回 ~/Goink/。
// 开发模式下 exe 位于临时目录时，所有平台统一返回 ~/Goink/。
func DataDir() string {
	if runtime.GOOS == "windows" {
		if dir, err := AppDir(); err == nil {
			tmp := os.TempDir()
			if !strings.HasPrefix(strings.ToLower(dir), strings.ToLower(tmp)) {
				return dir
			}
		}
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, "Goink")
}

func onnxLibName() string {
	switch runtime.GOOS {
	case "windows":
		return "onnxruntime.dll"
	case "darwin":
		return "libonnxruntime.dylib"
	default:
		return "libonnxruntime.so"
	}
}

func systemOnnxPaths(lib string) []string {
	switch runtime.GOOS {
	case "darwin":
		return []string{
			"/usr/local/lib/" + lib,
			"/usr/lib/" + lib,
		}
	default:
		return []string{
			"/usr/lib/" + lib,
			"/usr/local/lib/" + lib,
		}
	}
}
