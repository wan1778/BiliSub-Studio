package main

import (
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"time"

	"bilisubstudio/internal/application"
	"bilisubstudio/internal/appstate"
	"bilisubstudio/internal/nativeui"
	"bilisubstudio/internal/proc"
)

const version = "4.0.0-beta.12"

func main() {
	// The self-updater must not create its own kill-on-close Job Object. It was
	// launched as a breakaway child specifically so it can outlive the old app,
	// replace the installed executable, and start the new process.
	if len(os.Args) >= 4 && os.Args[1] == "--apply-self-update" {
		target := os.Args[2]
		pid, _ := strconv.Atoi(os.Args[3])
		if err := applySelfUpdate(target, pid); err != nil {
			_, _ = fmt.Fprintln(os.Stderr, err)
			os.Exit(1)
		}
		return
	}

	if err := proc.EnableContainment(); err != nil {
		fatal(fmt.Errorf("khởi tạo vùng tiến trình Windows: %w", err))
	}

	exe, err := os.Executable()
	if err != nil {
		fatal(err)
	}
	root := filepath.Dir(exe)
	st, err := appstate.New(root, version)
	if err != nil {
		fatal(err)
	}
	app := application.New(st)
	defer app.Shutdown()

	result, err := nativeui.Run(app)
	if err != nil {
		fatal(err)
	}
	if result.UpdatePath != "" {
		cmd := proc.Breakaway(exec.Command(result.UpdatePath, "--apply-self-update", exe, strconv.Itoa(os.Getpid())))
		if err := cmd.Start(); err != nil {
			fatal(fmt.Errorf("khởi động updater: %w", err))
		}
	}
}

func fatal(err error) {
	_, _ = fmt.Fprintln(os.Stderr, "BiliSub Studio:", err)
	os.Exit(1)
}

// applySelfUpdate runs from the newly downloaded executable in Temp. It writes
// a sibling .new file first, waits until Windows releases the old executable,
// swaps atomically, then starts the installed path.
func applySelfUpdate(target string, oldPID int) error {
	self, err := os.Executable()
	if err != nil {
		return err
	}
	newPath := target + ".new"
	bakPath := target + ".bak"
	if err := copyFile(self, newPath); err != nil {
		return fmt.Errorf("chuẩn bị update: %w", err)
	}
	deadline := time.Now().Add(60 * time.Second)
	for time.Now().Before(deadline) {
		if oldPID > 0 {
			// The actual rename below is the authoritative process-lock test.
			time.Sleep(250 * time.Millisecond)
		}
		_ = os.Remove(bakPath)
		if err := os.Rename(target, bakPath); err != nil {
			time.Sleep(250 * time.Millisecond)
			continue
		}
		if err := os.Rename(newPath, target); err != nil {
			_ = os.Rename(bakPath, target)
			time.Sleep(250 * time.Millisecond)
			continue
		}
		cmd := proc.Hide(exec.Command(target))
		if err := cmd.Start(); err != nil {
			_ = os.Remove(target)
			_ = os.Rename(bakPath, target)
			return fmt.Errorf("khởi động bản mới: %w", err)
		}
		return nil
	}
	return fmt.Errorf("không thể thay EXE sau 60 giây")
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	_, cp := io.Copy(out, in)
	syncErr := out.Sync()
	closeErr := out.Close()
	if cp != nil {
		return cp
	}
	if syncErr != nil {
		return syncErr
	}
	return closeErr
}
