package mediapreview

import (
	"bytes"
	"context"
	"image/jpeg"
	"os/exec"
	"path/filepath"
	"testing"
)

func TestParsePreviewInfoCompatibility(t *testing.T) {
	raw := []byte(`{"streams":[{"codec_name":"hevc","codec_type":"video","width":1920,"height":1080}],"format":{"duration":"123.5","format_name":"mov,mp4"}}`)
	got, err := parsePreviewInfo(raw, ".mp4")
	if err != nil {
		t.Fatal(err)
	}
	if got.Width != 1920 || got.Height != 1080 || got.Codec != "hevc" || !got.DirectCompatible {
		t.Fatalf("HEVC MP4 must be attempted as direct browser playback before fallback: %+v", got)
	}
	raw = []byte(`{"streams":[{"codec_name":"h264","codec_type":"video","width":1280,"height":720}],"format":{"duration":"10","format_name":"mov,mp4"}}`)
	got, err = parsePreviewInfo(raw, ".mp4")
	if err != nil {
		t.Fatal(err)
	}
	if !got.DirectCompatible {
		t.Fatalf("h264 mp4 should be direct: %+v", got)
	}
}

func TestPreviewFrameFallbackProcess(t *testing.T) {
	ffmpeg, err := exec.LookPath("ffmpeg")
	if err != nil {
		t.Skip("ffmpeg not installed")
	}
	ffprobe, err := exec.LookPath("ffprobe")
	if err != nil {
		t.Skip("ffprobe not installed")
	}
	input := filepath.Join(t.TempDir(), "fixture.mkv")
	cmd := exec.Command(ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
		"-f", "lavfi", "-i", "color=c=0x345678:s=320x180:d=1:r=10",
		"-c:v", "libx264", "-pix_fmt", "yuv420p", input)
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Fatalf("fixture ffmpeg: %v: %s", err, out)
	}
	info, err := ProbePreview(context.Background(), ffprobe, input)
	if err != nil {
		t.Fatal(err)
	}
	if info.Width != 320 || info.Height != 180 || info.Duration <= 0 {
		t.Fatalf("info=%+v", info)
	}
	if info.DirectCompatible {
		t.Fatalf("mkv fixture should use fallback preview: %+v", info)
	}
	frame, err := PreviewFrameJPEG(context.Background(), ffmpeg, input, 0.4)
	if err != nil {
		t.Fatal(err)
	}
	cfg, err := jpeg.DecodeConfig(bytes.NewReader(frame))
	if err != nil {
		t.Fatalf("decode jpeg: %v", err)
	}
	if cfg.Width <= 0 || cfg.Height <= 0 {
		t.Fatalf("jpeg size=%dx%d", cfg.Width, cfg.Height)
	}
}

func TestPreviewFrameHEVCMP4Process(t *testing.T) {
	ffmpeg, err := exec.LookPath("ffmpeg")
	if err != nil {
		t.Skip("ffmpeg not installed")
	}
	ffprobe, err := exec.LookPath("ffprobe")
	if err != nil {
		t.Skip("ffprobe not installed")
	}
	enc, _ := exec.Command(ffmpeg, "-hide_banner", "-encoders").CombinedOutput()
	if !bytes.Contains(enc, []byte("libx265")) {
		t.Skip("libx265 encoder not installed")
	}
	input := filepath.Join(t.TempDir(), "hevc.mp4")
	cmd := exec.Command(ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
		"-f", "lavfi", "-i", "testsrc2=s=320x180:d=0.6:r=10",
		"-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-pix_fmt", "yuv420p", input)
	if out, err := cmd.CombinedOutput(); err != nil {
		t.Skipf("cannot create HEVC fixture: %v: %s", err, out)
	}
	info, err := ProbePreview(context.Background(), ffprobe, input)
	if err != nil {
		t.Fatal(err)
	}
	if info.Codec != "hevc" || !info.DirectCompatible {
		t.Fatalf("HEVC mp4 should attempt direct playback first: %+v", info)
	}
	frame, err := PreviewFrameJPEG(context.Background(), ffmpeg, input, 0.3)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := jpeg.DecodeConfig(bytes.NewReader(frame)); err != nil {
		t.Fatalf("HEVC fallback frame is not JPEG: %v", err)
	}
}
