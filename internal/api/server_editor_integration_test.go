//go:build !windows

package api

import (
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"bilisubstudio/internal/appstate"
)

func TestEditorAPIExportsNewFileAndMediaSupportsRange(t *testing.T) {
	root := t.TempDir()
	st, err := appstate.New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(st.Paths.Tools, 0o755); err != nil {
		t.Fatal(err)
	}
	// Editor must be independent from yt-dlp/cookie setup. Only FFmpeg is required.
	ffmpeg := filepath.Join(st.Paths.Tools, "ffmpeg.exe")
	script := `#!/bin/sh
last=""
for arg in "$@"; do last="$arg"; done
printf '%s\n' 'out_time_us=500000'
printf '%s\n' 'progress=end'
printf 'edited-video' > "$last"
`
	if err := os.WriteFile(ffmpeg, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}

	input := filepath.Join(root, "input.mp4")
	if err := os.WriteFile(input, []byte("0123456789abcdef"), 0o644); err != nil {
		t.Fatal(err)
	}
	outDir := filepath.Join(root, "out")

	s := New(st)
	ts := httptest.NewServer(s.Handler())
	defer ts.Close()

	// Browser video elements use query-token auth because they cannot attach the
	// custom API header. ServeFile must retain Range behavior for seeking.
	req, _ := http.NewRequest(http.MethodGet, ts.URL+"/api/media?token="+st.Token+"&path="+input, nil)
	req.Header.Set("Range", "bytes=2-5")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	b, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if resp.StatusCode != http.StatusPartialContent || string(b) != "2345" {
		t.Fatalf("media status=%d body=%q", resp.StatusCode, b)
	}

	payload := map[string]any{
		"inputPath": input, "outputDir": outDir, "fileName": "clean.mp4",
		"sourceWidth": 1920, "sourceHeight": 1080, "duration": 1.0,
		"regions": []map[string]any{{"x": .1, "y": .7, "w": .8, "h": .15, "effect": "blur", "strength": 18, "whole": true}},
	}
	body, _ := json.Marshal(payload)
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/api/editor/export?token="+st.Token, bytes.NewReader(body))
	resp, err = http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	if resp.StatusCode != 200 {
		b, _ := io.ReadAll(resp.Body)
		resp.Body.Close()
		t.Fatalf("export status=%d body=%s", resp.StatusCode, b)
	}
	var started struct {
		JobID string `json:"job_id"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&started); err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if started.JobID == "" {
		t.Fatal("missing job id")
	}

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		resp, err = http.Get(ts.URL + "/api/job?token=" + st.Token + "&id=" + started.JobID)
		if err != nil {
			t.Fatal(err)
		}
		var snap struct {
			Status, Message, Error string
			Done                   bool
		}
		if err := json.NewDecoder(resp.Body).Decode(&snap); err != nil {
			resp.Body.Close()
			t.Fatal(err)
		}
		resp.Body.Close()
		if snap.Done {
			if snap.Status != "done" || snap.Error != "" {
				t.Fatalf("snap=%+v", snap)
			}
			if !strings.Contains(snap.Message, "clean.mp4") {
				t.Fatalf("message=%q", snap.Message)
			}
			got, err := os.ReadFile(filepath.Join(outDir, "clean.mp4"))
			if err != nil {
				t.Fatal(err)
			}
			if string(got) != "edited-video" {
				t.Fatalf("output=%q", got)
			}
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("editor job timeout")
}

func TestEditorAPIRealFFmpegDoesNotRequireYTDLP(t *testing.T) {
	ffmpeg, err := exec.LookPath("ffmpeg")
	if err != nil {
		t.Skip("ffmpeg not installed")
	}
	root := t.TempDir()
	st, err := appstate.New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(st.Paths.Tools, 0o755); err != nil {
		t.Fatal(err)
	}
	ffmpegShim := filepath.Join(st.Paths.Tools, "ffmpeg.exe")
	if err := copyTestExecutable(ffmpeg, ffmpegShim); err != nil {
		t.Skipf("cannot copy ffmpeg fixture: %v", err)
	}
	input := filepath.Join(root, "fixture.mp4")
	gen := exec.Command(ffmpeg, "-y", "-hide_banner", "-loglevel", "error",
		"-f", "lavfi", "-i", "testsrc2=size=320x180:rate=20",
		"-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
		"-t", "0.8", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", input)
	if b, err := gen.CombinedOutput(); err != nil {
		t.Skipf("cannot generate fixture: %v %s", err, b)
	}

	s := New(st)
	ts := httptest.NewServer(s.Handler())
	defer ts.Close()
	payload := map[string]any{
		"inputPath": input, "outputDir": filepath.Join(root, "out"), "fileName": "edited.mp4",
		"sourceWidth": 320, "sourceHeight": 180, "duration": .8,
		"regions": []map[string]any{{"x": .05, "y": .70, "w": .90, "h": .20, "effect": "cover", "strength": 18, "whole": true}},
	}
	body, _ := json.Marshal(payload)
	req, _ := http.NewRequest(http.MethodPost, ts.URL+"/api/editor/export?token="+st.Token, bytes.NewReader(body))
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		b, _ := io.ReadAll(resp.Body)
		t.Fatalf("export status=%d body=%s", resp.StatusCode, b)
	}
	var started struct {
		JobID string `json:"job_id"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&started); err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(8 * time.Second)
	for time.Now().Before(deadline) {
		jr, err := http.Get(ts.URL + "/api/job?token=" + st.Token + "&id=" + started.JobID)
		if err != nil {
			t.Fatal(err)
		}
		var snap struct {
			Status, Error string
			Done          bool
		}
		if err := json.NewDecoder(jr.Body).Decode(&snap); err != nil {
			jr.Body.Close()
			t.Fatal(err)
		}
		jr.Body.Close()
		if snap.Done {
			if snap.Status != "done" || snap.Error != "" {
				t.Fatalf("snap=%+v", snap)
			}
			out := filepath.Join(root, "out", "edited.mp4")
			if st, err := os.Stat(out); err != nil || st.Size() == 0 {
				t.Fatalf("output invalid: %v", err)
			}
			if p := filepath.Join(st.Paths.Tools, "yt-dlp.exe"); fileExistsTest(p) {
				t.Fatalf("editor unexpectedly created/required yt-dlp: %s", p)
			}
			return
		}
		time.Sleep(30 * time.Millisecond)
	}
	t.Fatal("real editor API job timeout")
}

func fileExistsTest(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}
