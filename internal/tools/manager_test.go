package tools

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestFindToolsUsesOnlyDirectAppOwnedExecutables(t *testing.T) {
	root := t.TempDir()
	m := New(root)
	if got := m.FindFFmpeg(); got != "" {
		t.Fatalf("missing direct tool returned %q", got)
	}
	direct := filepath.Join(root, "ffmpeg.exe")
	if err := os.WriteFile(direct, []byte("fixture"), 0o755); err != nil {
		t.Fatal(err)
	}
	if got := m.FindFFmpeg(); got != direct {
		t.Fatalf("FindFFmpeg=%q want direct %q", got, direct)
	}
	if err := os.Remove(direct); err != nil {
		t.Fatal(err)
	}
	nested := filepath.Join(root, "nested")
	if err := os.MkdirAll(nested, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(nested, "ffmpeg.exe"), []byte("fixture"), 0o755); err != nil {
		t.Fatal(err)
	}
	if got := m.FindFFmpeg(); got != "" {
		t.Fatalf("nested tool must not be accepted: %q", got)
	}
}

func TestFindToolsRejectsSymlinkEscape(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlink creation may require Windows privilege; production path is validated by Lstat/EvalSymlinks")
	}
	root := t.TempDir()
	outside := filepath.Join(t.TempDir(), "ffmpeg.exe")
	if err := os.WriteFile(outside, []byte("fixture"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, filepath.Join(root, "ffmpeg.exe")); err != nil {
		t.Fatal(err)
	}
	if got := New(root).FindFFmpeg(); got != "" {
		t.Fatalf("symlink escape accepted: %q", got)
	}
}
