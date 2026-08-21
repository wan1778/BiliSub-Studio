package videoedit

import (
	"bilisubstudio/internal/jobs"
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

func TestBuildFilterMultipleRegions(t *testing.T) {
	req := Request{
		SourceWidth: 1920, SourceHeight: 1080, Duration: 60,
		Regions: []Region{
			{X: .1, Y: .7, W: .8, H: .15, Effect: "blur", Strength: 18, Whole: true},
			{X: .82, Y: .03, W: .15, H: .08, Effect: "mosaic", Strength: 12, Start: 3, End: 20},
			{X: .02, Y: .03, W: .10, H: .07, Effect: "cover", Whole: true},
		},
	}
	g, err := BuildFilter(req)
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"crop=1536:162:192:756", "boxblur=luma_radius=18", "flags=neighbor", "between(t,3.000,20.000)", "drawbox=", "[vout]"} {
		if !strings.Contains(g, want) {
			t.Fatalf("missing %q in %s", want, g)
		}
	}
}

func TestBuildFilterRejectsInvalidTimeRange(t *testing.T) {
	_, err := BuildFilter(Request{SourceWidth: 1280, SourceHeight: 720, Duration: 10, Regions: []Region{{X: .1, Y: .1, W: .2, H: .2, Effect: "blur", Start: 8, End: 4}}})
	if err == nil {
		t.Fatal("expected error")
	}
}

func TestBuildFilterClampsRegionToFrame(t *testing.T) {
	g, err := BuildFilter(Request{SourceWidth: 1000, SourceHeight: 500, Regions: []Region{{X: .9, Y: .8, W: .3, H: .4, Effect: "cover", Whole: true}}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(g, "x=900:y=400:w=100:h=100") {
		t.Fatalf("graph=%s", g)
	}
}

func TestSanitizeFileName(t *testing.T) {
	if got := sanitizeFileName(`bad:name?.avi`); got != "bad_name_.mp4" {
		t.Fatalf("got %q", got)
	}
	if got := sanitizeFileName(`clean.mkv`); got != "clean.mkv" {
		t.Fatalf("got %q", got)
	}
}

func TestServiceRunRealFFmpegSmoke(t *testing.T) {
	ffmpeg, err := exec.LookPath("ffmpeg")
	if err != nil {
		t.Skip("ffmpeg not installed")
	}
	root := t.TempDir()
	input := filepath.Join(root, "input.mp4")
	gen := exec.Command(ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1.2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input)
	if b, err := gen.CombinedOutput(); err != nil {
		t.Skipf("local ffmpeg cannot generate fixture: %v %s", err, b)
	}
	job := jobs.New("editor-smoke")
	svc := Service{FFmpeg: ffmpeg}
	out, err := svc.Run(context.Background(), job, Request{
		InputPath: input, OutputDir: root, FileName: "edited.mp4",
		SourceWidth: 640, SourceHeight: 360, Duration: 1.2,
		Regions: []Region{
			{X: .05, Y: .70, W: .90, H: .20, Effect: "blur", Strength: 8, Whole: true},
			{X: .72, Y: .05, W: .20, H: .16, Effect: "mosaic", Strength: 10, Start: .2, End: 1.0},
			{X: .02, Y: .02, W: .12, H: .10, Effect: "cover", Whole: true},
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	st, err := os.Stat(out)
	if err != nil || st.Size() == 0 {
		t.Fatalf("output invalid: %v size=%d", err, func() int64 {
			if st != nil {
				return st.Size()
			}
			return 0
		}())
	}
	if strings.EqualFold(out, input) {
		t.Fatal("editor must not overwrite source")
	}
}

func TestServiceRunMP4ProducesBrowserFriendlyCodecs(t *testing.T) {
	ffmpeg, err := exec.LookPath("ffmpeg")
	if err != nil {
		t.Skip("ffmpeg not installed")
	}
	ffprobe, err := exec.LookPath("ffprobe")
	if err != nil {
		t.Skip("ffprobe not installed")
	}
	root := t.TempDir()
	input := filepath.Join(root, "input.mkv")
	gen := exec.Command(ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
		"-f", "lavfi", "-i", "testsrc2=size=320x180:rate=20",
		"-f", "lavfi", "-i", "sine=frequency=500:sample_rate=48000",
		"-t", "0.8", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "libopus", input)
	if b, err := gen.CombinedOutput(); err != nil {
		t.Skipf("local ffmpeg cannot generate opus fixture: %v %s", err, b)
	}
	job := jobs.New("editor-browser-codecs")
	svc := Service{FFmpeg: ffmpeg}
	out, err := svc.Run(context.Background(), job, Request{
		InputPath: input, OutputDir: root, FileName: "browser.mp4",
		SourceWidth: 320, SourceHeight: 180, Duration: .8,
		Regions: []Region{{X: .05, Y: .70, W: .90, H: .20, Effect: "cover", Whole: true}},
	})
	if err != nil {
		t.Fatal(err)
	}
	probe := exec.Command(ffprobe, "-v", "error", "-show_entries", "stream=codec_type,codec_name,pix_fmt", "-of", "default=nw=1", out)
	b, err := probe.CombinedOutput()
	if err != nil {
		t.Fatalf("ffprobe: %v %s", err, b)
	}
	info := string(b)
	for _, want := range []string{"codec_name=h264", "pix_fmt=yuv420p", "codec_name=aac"} {
		if !strings.Contains(info, want) {
			t.Fatalf("missing %q in ffprobe output:\n%s", want, info)
		}
	}
}
