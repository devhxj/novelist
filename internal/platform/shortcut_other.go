//go:build !windows

package platform

// CreateStartMenuShortcut 非 Windows 平台无操作。
func CreateStartMenuShortcut() error {
	return nil
}
