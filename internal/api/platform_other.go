//go:build !windows

package api

import (
	"errors"
	"os/exec"
)

func launchBrowser(url string) error {
	return exec.Command("xdg-open", url).Start()
}
func openFolderNative(path string) error {
	return exec.Command("xdg-open", path).Start()
}
func pickFolderNative(initial string) (string, bool, error) {
	return "", true, errors.New("folder picker chỉ dùng trên Windows")
}

func pickVideoNative(initial string) (string, bool, error) {
	return "", true, errors.New("video picker chỉ dùng trên Windows")
}
