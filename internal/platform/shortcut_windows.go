//go:build windows

package platform

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
)

// CreateStartMenuShortcut 在开始菜单创建 Goink 的快捷方式。已有则跳过。
func CreateStartMenuShortcut() error {
	exe, err := os.Executable()
	if err != nil {
		return fmt.Errorf("shortcut: 获取可执行文件路径失败: %w", err)
	}

	startMenu, err := startMenuProgramsDir()
	if err != nil {
		return fmt.Errorf("shortcut: 获取开始菜单路径失败: %w", err)
	}

	dir := filepath.Join(startMenu, "Goink")
	lnk := filepath.Join(dir, "Goink.lnk")
	if _, err := os.Stat(lnk); err == nil {
		return nil // 已存在，跳过
	}

	if err := os.MkdirAll(dir, 0755); err != nil {
		return fmt.Errorf("shortcut: 创建目录失败: %w", err)
	}

	ps := fmt.Sprintf(
		`$ws = New-Object -ComObject WScript.Shell; $s = $ws.CreateShortcut('%s'); $s.TargetPath = '%s'; $s.WorkingDirectory = '%s'; $s.Save()`,
		lnk, exe, filepath.Dir(exe),
	)
	cmd := exec.Command("powershell", "-NoProfile", "-NonInteractive", "-Command", ps)
	if err := cmd.Run(); err != nil {
		return fmt.Errorf("shortcut: PowerShell 创建快捷方式失败: %w", err)
	}
	return nil
}

func startMenuProgramsDir() (string, error) {
	home, err := os.UserHomeDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(home, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"), nil
}
