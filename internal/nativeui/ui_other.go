//go:build !windows

package nativeui

import (
	"bilisubstudio/internal/application"
	"errors"
)

func Run(*application.App) (RunResult, error) {
	return RunResult{}, errors.New("native Windows UI chỉ khả dụng trên Windows")
}
