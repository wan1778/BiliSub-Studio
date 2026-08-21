//go:build !windows

package api

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"

	"bilisubstudio/internal/appstate"
	"bilisubstudio/internal/jobs"
)

func writePaddleOCRFixture(t *testing.T, root, responseText string) {
	t.Helper()
	runtimeRoot := filepath.Join(root, "runtime", "cpu")
	if err := os.MkdirAll(filepath.Join(runtimeRoot, "venv", "Scripts"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(root, "models"), 0o755); err != nil {
		t.Fatal(err)
	}
	workerSource, err := os.ReadFile(filepath.Join("..", "ocr", "worker.py"))
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "worker.py"), workerSource, 0o644); err != nil {
		t.Fatal(err)
	}
	sum := sha256.Sum256(workerSource)
	manifest := map[string]any{
		"schema": 2, "uv": "0.12.0", "python": "3.12", "paddle": "3.2.0", "paddleocr": "3.7.0",
		"det_model": "PP-OCRv6_small_det", "rec_model": "PP-OCRv6_small_rec", "worker_sha256": hex.EncodeToString(sum[:]),
		"runtime": "cpu", "paddle_package": "paddlepaddle", "paddle_index": "https://www.paddlepaddle.org.cn/packages/stable/cpu/",
	}
	b, err := json.Marshal(manifest)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(runtimeRoot, "install.json"), b, 0o644); err != nil {
		t.Fatal(err)
	}
	python := filepath.Join(runtimeRoot, "venv", "Scripts", "python.exe")
	script := `#!/bin/sh
printf '%s\n' '{"type":"ready","engine":"PaddleOCR","paddleocr":"3.7.0","paddle":"3.2.0","models":["PP-OCRv6_small_det","PP-OCRv6_small_rec"],"device":"cpu","cuda_available":false}'
while IFS= read -r line; do
  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
  printf '{"id":%s,"ok":true,"detected":true,"text":"%s","confidence":0.97,"lines":[{"text":"%s","confidence":0.97,"box":[1,2,3,4]}]}\n' "${id:-0}" "` + responseText + `" "` + responseText + `"
done
`
	if err := os.WriteFile(python, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
}

func TestOCRAPIEndToEndWithChildProcess(t *testing.T) {
	root := t.TempDir()
	st, err := appstate.New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	writePaddleOCRFixture(t, st.Paths.OCR, "api-fixture")

	s := New(st)
	defer s.OCR.Stop()
	ts := httptest.NewServer(s.Handler())
	defer ts.Close()

	req, _ := http.NewRequest(http.MethodPost, ts.URL+"/api/ocr/engine/ensure?token="+st.Token, bytes.NewBufferString(`{"device":"cpu"}`))
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("ensure status=%d", resp.StatusCode)
	}

	deadline := time.Now().Add(2 * time.Second)
	for !s.OCR.Status().Ready && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if !s.OCR.Status().Ready {
		t.Fatalf("OCR not ready: %+v", s.OCR.Status())
	}
	if got := st.SnapshotConfig().OCRDevice; got != "cpu" {
		t.Fatalf("OCR device config=%q want cpu", got)
	}

	body := bytes.NewBufferString(`{"imageBase64":"AAAA"}`)
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/api/ocr?token="+st.Token, body)
	resp, err = http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("ocr status=%d", resp.StatusCode)
	}
	var out struct {
		OK         bool    `json:"ok"`
		Text       string  `json:"text"`
		Confidence float64 `json:"confidence"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		t.Fatal(err)
	}
	if !out.OK || out.Text != "api-fixture" || out.Confidence < 0.96 {
		t.Fatalf("out=%+v", out)
	}
}

func TestOCRScanAPIUsesBackendFramesAndReturnsCues(t *testing.T) {
	root := t.TempDir()
	st, err := appstate.New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	writePaddleOCRFixture(t, st.Paths.OCR, "扫描字幕")
	if err := os.MkdirAll(st.Paths.Tools, 0o755); err != nil {
		t.Fatal(err)
	}
	raw := filepath.Join(root, "scan.rgb")
	frame := make([]byte, 1280*320*3)
	for y := 125; y < 205; y++ {
		for x := 170; x < 1110; x++ {
			if (x/8)%2 == 0 {
				i := (y*1280 + x) * 3
				frame[i], frame[i+1], frame[i+2] = 250, 205, 30
			}
		}
	}
	var frames []byte
	for i := 0; i < 6; i++ {
		frames = append(frames, frame...)
	}
	if err := os.WriteFile(raw, frames, 0o644); err != nil {
		t.Fatal(err)
	}
	ff := filepath.Join(st.Paths.Tools, "ffmpeg.exe")
	ffScript := "#!/bin/sh\ncat '" + raw + "'\n"
	if err := os.WriteFile(ff, []byte(ffScript), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(root, "video.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}

	s := New(st)
	defer s.OCR.Stop()
	ts := httptest.NewServer(s.Handler())
	defer ts.Close()
	body := bytes.NewBufferString(`{"path":` + strconv.Quote(input) + `,"region":{"x":0.05,"y":0.65,"w":0.9,"h":0.3},"mode":"accurate","device":"cpu","sensitivity":0.75,"duration":0.75}`)
	req, _ := http.NewRequest(http.MethodPost, ts.URL+"/api/ocr/scan?token="+st.Token, body)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("scan status=%d", resp.StatusCode)
	}
	var started struct {
		JobID string `json:"job_id"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&started); err != nil {
		t.Fatal(err)
	}
	if started.JobID == "" {
		t.Fatal("missing job id")
	}
	deadline := time.Now().Add(4 * time.Second)
	for time.Now().Before(deadline) {
		r, err := http.Get(ts.URL + "/api/job?token=" + st.Token + "&id=" + started.JobID)
		if err != nil {
			t.Fatal(err)
		}
		var snap struct {
			Status string `json:"status"`
			Done   bool   `json:"done"`
			Error  string `json:"error"`
			Result struct {
				Cues []struct {
					Text string `json:"text"`
				} `json:"cues"`
			} `json:"result"`
		}
		err = json.NewDecoder(r.Body).Decode(&snap)
		r.Body.Close()
		if err != nil {
			t.Fatal(err)
		}
		if snap.Done {
			if snap.Status != "done" || snap.Error != "" {
				t.Fatalf("scan failed: %+v", snap)
			}
			if len(snap.Result.Cues) == 0 || snap.Result.Cues[0].Text != "扫描字幕" {
				t.Fatalf("scan cues=%+v", snap.Result.Cues)
			}
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("scan job timeout")
}

func TestOCRManualFrameAPIUsesBackendFFmpegAtCurrentTimestamp(t *testing.T) {
	root := t.TempDir()
	st, err := appstate.New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	writePaddleOCRFixture(t, st.Paths.OCR, "manual-frame-fixture")
	if err := os.MkdirAll(st.Paths.Tools, 0o755); err != nil {
		t.Fatal(err)
	}
	raw := filepath.Join(root, "manual-frame.rgb")
	if err := os.WriteFile(raw, make([]byte, 1280*320*3), 0o644); err != nil {
		t.Fatal(err)
	}
	argsLog := filepath.Join(root, "ffmpeg-args.txt")
	ff := filepath.Join(st.Paths.Tools, "ffmpeg.exe")
	ffScript := "#!/bin/sh\nprintf '%s\\n' \"$*\" > '" + argsLog + "'\ncat '" + raw + "'\n"
	if err := os.WriteFile(ff, []byte(ffScript), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(root, "video.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}

	s := New(st)
	defer s.OCR.Stop()
	ts := httptest.NewServer(s.Handler())
	defer ts.Close()
	body := bytes.NewBufferString(`{"path":` + strconv.Quote(input) + `,"time":12.5,"region":{"x":0.1,"y":0.7,"w":0.8,"h":0.2},"device":"cpu"}`)
	req, _ := http.NewRequest(http.MethodPost, ts.URL+"/api/ocr?token="+st.Token, body)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		t.Fatalf("ocr frame status=%d", resp.StatusCode)
	}
	var out struct {
		OK         bool    `json:"ok"`
		Text       string  `json:"text"`
		Confidence float64 `json:"confidence"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		t.Fatal(err)
	}
	if !out.OK || out.Text != "manual-frame-fixture" || out.Confidence < 0.96 {
		t.Fatalf("out=%+v", out)
	}
	args, err := os.ReadFile(argsLog)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(args, []byte("-ss 12.500")) || !bytes.Contains(args, []byte("crop=iw*0.80000000:ih*0.20000000:iw*0.10000000:ih*0.70000000")) {
		t.Fatalf("ffmpeg args do not match current frame/ROI: %s", args)
	}
}

func TestOCRScannerCheckpointOwnedByPortableDataPath(t *testing.T) {
	root := t.TempDir()
	st, err := appstate.New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	s := New(st)
	scanner := s.newOCRScanner(filepath.Join(st.Paths.Tools, "ffmpeg.exe"))
	want := filepath.Join(st.Paths.Data, "OCRCheckpoints")
	if scanner.CheckpointDir != want {
		t.Fatalf("checkpoint dir=%q want=%q", scanner.CheckpointDir, want)
	}
}

func TestOCRPauseAPIWaitsForPausableJobHandshake(t *testing.T) {
	root := t.TempDir()
	st, err := appstate.New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	srv := New(st)
	job := jobs.NewPausable("pause-api-fixture")
	srv.Jobs.Add(job)
	go func() {
		deadline := time.Now().Add(time.Second)
		for !job.PauseRequested() && time.Now().Before(deadline) {
			time.Sleep(5 * time.Millisecond)
		}
		job.PauseComplete("Đã tạm dừng fixture")
	}()
	ts := httptest.NewServer(srv.Handler())
	defer ts.Close()
	req, _ := http.NewRequest(http.MethodPost, ts.URL+"/api/job/pause?token="+st.Token+"&id=pause-api-fixture", bytes.NewBufferString(`{}`))
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatal(err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("pause status=%d", resp.StatusCode)
	}
	var snap struct {
		Status string `json:"status"`
		Done   bool   `json:"done"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&snap); err != nil {
		t.Fatal(err)
	}
	if snap.Status != "paused" || !snap.Done {
		t.Fatalf("pause snapshot=%+v", snap)
	}
}
