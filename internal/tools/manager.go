package tools

import (
	"archive/zip"
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
)

const (
	ytdlpURL  = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
	ffmpegURL = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
)

type Manager struct {
	Root string
	mu   sync.Mutex
}

func New(root string) *Manager { return &Manager{Root: root} }

func (m *Manager) FindYTDLP() string   { return ownedExecutable(m.Root, "yt-dlp.exe") }
func (m *Manager) FindFFmpeg() string  { return ownedExecutable(m.Root, "ffmpeg.exe") }
func (m *Manager) FindFFprobe() string { return ownedExecutable(m.Root, "ffprobe.exe") }

func (m *Manager) EnsureYTDLP(ctx context.Context) (string, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if p := m.FindYTDLP(); p != "" {
		return p, nil
	}
	if err := os.MkdirAll(m.Root, 0o755); err != nil {
		return "", err
	}
	p := filepath.Join(m.Root, "yt-dlp.exe")
	if err := download(ctx, ytdlpURL, p); err != nil {
		return "", fmt.Errorf("tải yt-dlp: %w", err)
	}
	return p, nil
}

func (m *Manager) EnsureFFmpeg(ctx context.Context) (string, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if p := m.FindFFmpeg(); p != "" {
		return p, nil
	}
	if err := m.installFFmpegBundleLocked(ctx); err != nil {
		return "", err
	}
	if p := m.FindFFmpeg(); p != "" {
		return p, nil
	}
	return "", errors.New("không tìm thấy ffmpeg.exe sau giải nén")
}

func (m *Manager) EnsureFFprobe(ctx context.Context) (string, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if p := m.FindFFprobe(); p != "" {
		return p, nil
	}
	if err := m.installFFmpegBundleLocked(ctx); err != nil {
		return "", err
	}
	if p := m.FindFFprobe(); p != "" {
		return p, nil
	}
	return "", errors.New("không tìm thấy ffprobe.exe sau giải nén")
}

func (m *Manager) installFFmpegBundleLocked(ctx context.Context) error {
	if err := os.MkdirAll(m.Root, 0o755); err != nil {
		return err
	}
	archive := filepath.Join(m.Root, "ffmpeg.zip")
	if err := download(ctx, ffmpegURL, archive); err != nil {
		return fmt.Errorf("tải ffmpeg: %w", err)
	}
	defer os.Remove(archive)
	if err := unzipSelective(archive, m.Root, map[string]string{"ffmpeg.exe": "ffmpeg.exe", "ffprobe.exe": "ffprobe.exe"}); err != nil {
		return fmt.Errorf("giải nén ffmpeg: %w", err)
	}
	return nil
}

func ownedExecutable(root, name string) string {
	rootAbs, err := filepath.Abs(root)
	if err != nil {
		return ""
	}
	candidate := filepath.Join(rootAbs, name)
	info, err := os.Lstat(candidate)
	if err != nil || info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return ""
	}
	real, err := filepath.EvalSymlinks(candidate)
	if err != nil {
		return ""
	}
	realAbs, err := filepath.Abs(real)
	if err != nil || !samePath(realAbs, candidate) {
		return ""
	}
	return candidate
}

func samePath(a, b string) bool {
	ca := filepath.Clean(a)
	cb := filepath.Clean(b)
	if runtime.GOOS == "windows" {
		return strings.EqualFold(ca, cb)
	}
	return ca == cb
}

func download(ctx context.Context, url, path string) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("User-Agent", "BiliSubStudio/4")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("HTTP %d", resp.StatusCode)
	}
	tmp := path + ".tmp"
	f, err := os.Create(tmp)
	if err != nil {
		return err
	}
	_, cp := io.Copy(f, resp.Body)
	syncErr := f.Sync()
	closeErr := f.Close()
	if cp != nil || syncErr != nil || closeErr != nil {
		_ = os.Remove(tmp)
		if cp != nil {
			return cp
		}
		if syncErr != nil {
			return syncErr
		}
		return closeErr
	}
	return os.Rename(tmp, path)
}

func unzipSelective(archive, dest string, wanted map[string]string) error {
	r, err := zip.OpenReader(archive)
	if err != nil {
		return err
	}
	defer r.Close()
	seen := map[string]bool{}
	for _, f := range r.File {
		base := strings.ToLower(filepath.Base(f.Name))
		outName, ok := wanted[base]
		if !ok {
			continue
		}
		rc, err := f.Open()
		if err != nil {
			return err
		}
		outPath := filepath.Join(dest, outName)
		out, err := os.Create(outPath)
		if err != nil {
			rc.Close()
			return err
		}
		_, cp := io.Copy(out, rc)
		rc.Close()
		ce := out.Close()
		if cp != nil {
			return cp
		}
		if ce != nil {
			return ce
		}
		seen[base] = true
	}
	for k := range wanted {
		if !seen[k] {
			return fmt.Errorf("zip thiếu %s", k)
		}
	}
	return nil
}
