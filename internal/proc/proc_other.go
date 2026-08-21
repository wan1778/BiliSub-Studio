//go:build !windows

package proc

import "os/exec"

func EnableContainment() error          { return nil }
func Hide(cmd *exec.Cmd) *exec.Cmd      { return cmd }
func Breakaway(cmd *exec.Cmd) *exec.Cmd { return cmd }
