package ocr

import (
	"bytes"
	"context"
	"encoding/base64"
	"image/png"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestCaptureFramePNGBase64UsesBackendFFmpeg(t *testing.T) {
	ffmpeg, err := exec.LookPath("ffmpeg")
	if err != nil {
		t.Skip("ffmpeg not installed")
	}
	input := filepath.Join(t.TempDir(), "manual-frame.mp4")
	cmd := exec.Command(ffmpeg,
		"-hide_banner", "-loglevel", "error", "-y",
		"-f", "lavfi", "-i", "testsrc2=s=320x180:d=1:r=10",
		"-c:v", "libx264", "-pix_fmt", "yuv420p", input,
	)
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("create fixture: %v: %s", err, out)
	}
	b64, err := CaptureFramePNGBase64(context.Background(), ffmpeg, input, 0.4, ScanRegion{X: 0.05, Y: 0.60, W: 0.90, H: 0.35}, false)
	if err != nil {
		t.Fatal(err)
	}
	raw, err := base64.StdEncoding.DecodeString(b64)
	if err != nil {
		t.Fatal(err)
	}
	cfg, err := png.DecodeConfig(bytes.NewReader(raw))
	if err != nil {
		t.Fatalf("decode png: %v", err)
	}
	if cfg.Width != scanWidth || cfg.Height != scanHeight {
		t.Fatalf("captured size=%dx%d want=%dx%d", cfg.Width, cfg.Height, scanWidth, scanHeight)
	}
}
