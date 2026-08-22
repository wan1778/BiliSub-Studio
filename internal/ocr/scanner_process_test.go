//go:build !windows

package ocr

import (
	"bytes"
	"context"
	"encoding/base64"
	"errors"
	"image/png"
	"math"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"bilisubstudio/internal/jobs"
)

type sequenceOCR struct {
	mu sync.Mutex
	n  int
}

func (s *sequenceOCR) Run(ctx context.Context, image string) (Result, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.n++
	switch s.n {
	case 1, 2:
		return Result{OK: true, Detected: true, Text: "你好世界", Confidence: 0.94}, nil
	default:
		return Result{OK: true, Detected: false}, nil
	}
}

type parallelSequenceOCR struct {
	mu        sync.Mutex
	active    int
	maxActive int
	calls     int
}

func (p *parallelSequenceOCR) Parallelism() int { return 2 }

func (p *parallelSequenceOCR) Run(ctx context.Context, image string) (Result, error) {
	p.mu.Lock()
	p.active++
	p.calls++
	call := p.calls
	if p.active > p.maxActive {
		p.maxActive = p.active
	}
	p.mu.Unlock()

	// Deliberately make the first request slower than its paired request. The
	// scanner must still commit observations by media timestamp, not completion
	// order, or the subtitle track/checkpoint semantics become nondeterministic.
	delay := 10 * time.Millisecond
	if call == 1 {
		delay = 45 * time.Millisecond
	}
	select {
	case <-ctx.Done():
		p.mu.Lock()
		p.active--
		p.mu.Unlock()
		return Result{}, ctx.Err()
	case <-time.After(delay):
	}

	p.mu.Lock()
	p.active--
	p.mu.Unlock()
	if call == 1 {
		return Result{OK: true, Detected: true, Text: "你好世界", Confidence: 0.96}, nil
	}
	return Result{OK: true, Detected: false}, nil
}

func (p *parallelSequenceOCR) MaxActive() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.maxActive
}

func TestProbeNVDECFallsBackWhenCUDAPathFails(t *testing.T) {
	d := t.TempDir()
	fake := filepath.Join(d, "ffmpeg")
	script := `#!/bin/sh
for arg in "$@"; do
  if [ "$arg" = "-hwaccel" ]; then
    echo "CUDA device unavailable" >&2
    exit 42
  fi
done
exit 0
`
	if err := os.WriteFile(fake, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}
	decision := probeNVDEC(context.Background(), fake, input, 0, ScanRegion{X: .05, Y: .65, W: .9, H: .3}, scanModeFor("accurate", 1))
	if decision.Mode != "software" || !strings.Contains(decision.FallbackReason, "CUDA device unavailable") {
		t.Fatalf("decision=%+v", decision)
	}
}

func TestScannerFallsBackToSoftwareIfNVDECScanFailsAfterProbe(t *testing.T) {
	d := t.TempDir()
	raw := filepath.Join(d, "frames.rgb")
	if err := os.WriteFile(raw, make([]byte, 3*scanWidth*scanHeight*3), 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	script := `#!/bin/sh
is_hw=0
is_probe=0
prev=""
for arg in "$@"; do
  [ "$arg" = "-hwaccel" ] && is_hw=1
  [ "$prev" = "-frames:v" ] && [ "$arg" = "1" ] && is_probe=1
  prev="$arg"
done
if [ "$is_hw" = "1" ] && [ "$is_probe" = "1" ]; then
  exit 0
fi
if [ "$is_hw" = "1" ]; then
  echo "NVDEC runtime failure" >&2
  exit 43
fi
cat '` + raw + `'
`
	if err := os.WriteFile(fake, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}
	res, err := (&Scanner{FFmpeg: fake, Engine: &sequenceOCR{}}).Run(context.Background(), nil, ScanRequest{
		Path: input, Region: ScanRegion{X: .05, Y: .65, W: .9, H: .3}, Mode: "accurate", Duration: .75,
	})
	if err != nil {
		t.Fatal(err)
	}
	if res.Decoder != "software" || !strings.Contains(res.DecoderFallback, "NVDEC lỗi") {
		t.Fatalf("expected runtime CPU fallback, result=%+v", res)
	}
	if res.Frames != 3 {
		t.Fatalf("software fallback frames=%d want=3", res.Frames)
	}
}

func TestScannerRunHybridVisualConfirmationAvoidsForcedSecondOCR(t *testing.T) {
	d := t.TempDir()
	raw := filepath.Join(d, "frames.rgb")
	const frames = 14
	buf := make([]byte, 0, frames*scanWidth*scanHeight*3)
	for f := 0; f < frames; f++ {
		frame := make([]byte, scanWidth*scanHeight*3)
		if f >= 2 && f <= 8 {
			for y := 125; y < 205; y++ {
				for x := 170; x < 1110; x++ {
					if (x/8)%2 == 0 {
						i := (y*scanWidth + x) * 3
						frame[i], frame[i+1], frame[i+2] = 250, 205, 30
					}
				}
			}
		}
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}

	eng := &parallelSequenceOCR{}
	s := &Scanner{FFmpeg: fake, Engine: eng}
	res, err := s.Run(context.Background(), nil, ScanRequest{
		Path: input, Region: ScanRegion{X: 0.05, Y: 0.65, W: 0.9, H: 0.3},
		Mode: "accurate", Device: DeviceHybrid, Sensitivity: 0.75, Duration: float64(frames) / 4,
	})
	if err != nil {
		t.Fatal(err)
	}
	if eng.MaxActive() != 1 {
		t.Fatalf("stable Hybrid lookahead should not be force-OCR'd: max concurrency=%d", eng.MaxActive())
	}
	if res.VisualConfirmations < 2 {
		t.Fatalf("expected text + blank visual confirmations, result=%+v", res)
	}
	if res.OCRCalls != 2 {
		t.Fatalf("stable Hybrid segment should use one text OCR + one disappearance OCR, result=%+v", res)
	}
	if len(res.Cues) != 1 || res.Cues[0].Text != "你好世界" {
		t.Fatalf("hybrid scan cues=%#v", res.Cues)
	}
	if math.Abs(res.Cues[0].Start-0.5) > 0.001 {
		t.Fatalf("hybrid cue start=%v want=0.5; completion order leaked into timeline", res.Cues[0].Start)
	}
	if res.Cues[0].End <= res.Cues[0].Start {
		t.Fatalf("hybrid cue has invalid timeline: %#v", res.Cues[0])
	}
}

func TestScannerRunUsesPipeBackpressureAndReturnsCue(t *testing.T) {
	d := t.TempDir()
	raw := filepath.Join(d, "frames.rgb")
	const frames = 14
	buf := make([]byte, 0, frames*scanWidth*scanHeight*3)
	for f := 0; f < frames; f++ {
		frame := make([]byte, scanWidth*scanHeight*3)
		if f >= 2 && f <= 8 {
			for y := 125; y < 205; y++ {
				for x := 170; x < 1110; x++ {
					if (x/8)%2 == 0 {
						i := (y*scanWidth + x) * 3
						frame[i], frame[i+1], frame[i+2] = 250, 205, 30
					}
				}
			}
		}
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	script := "#!/bin/sh\ncat '" + raw + "'\n"
	if err := os.WriteFile(fake, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}
	eng := &sequenceOCR{}
	s := &Scanner{FFmpeg: fake, Engine: eng}
	job := jobs.New("scan-test")
	res, err := s.Run(context.Background(), job, ScanRequest{
		Path: input, Region: ScanRegion{X: 0.05, Y: 0.65, W: 0.9, H: 0.3},
		Mode: "accurate", Sensitivity: 0.75, Duration: float64(frames) / 8,
	})
	if err != nil {
		t.Fatal(err)
	}
	if res.Frames != frames {
		t.Fatalf("frames=%d want=%d", res.Frames, frames)
	}
	if len(res.Cues) == 0 {
		t.Fatalf("expected at least one cue; OCR calls=%d", res.OCRCalls)
	}
	if res.Cues[0].Text != "你好世界" {
		t.Fatalf("cue=%q", res.Cues[0].Text)
	}
	if res.OCRCalls >= frames*2 {
		t.Fatalf("scanner OCR'd nearly every frame: calls=%d frames=%d", res.OCRCalls, frames)
	}
}

type singleGarbageOCR struct {
	mu sync.Mutex
	n  int
}

func (s *singleGarbageOCR) Run(ctx context.Context, image string) (Result, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.n++
	if s.n == 1 {
		return Result{OK: true, Detected: true, Text: "OV", Confidence: 0.78}, nil
	}
	return Result{OK: true, Detected: false}, nil
}

func TestScannerRunDoesNotEmitSingleFrameShortASCIIGarbage(t *testing.T) {
	d := t.TempDir()
	raw := filepath.Join(d, "frames.rgb")
	const frames = 8
	if err := os.WriteFile(raw, make([]byte, frames*scanWidth*scanHeight*3), 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}

	s := &Scanner{FFmpeg: fake, Engine: &singleGarbageOCR{}}
	res, err := s.Run(context.Background(), nil, ScanRequest{
		Path: input, Region: ScanRegion{X: 0.05, Y: 0.65, W: 0.9, H: 0.3},
		Mode: "accurate", Sensitivity: 1, Duration: float64(frames) / 8,
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Cues) != 0 {
		t.Fatalf("single-frame OCR garbage became subtitle cue: %#v", res.Cues)
	}
}

type countingEmptyOCR struct {
	mu sync.Mutex
	n  int
}

func (s *countingEmptyOCR) Run(ctx context.Context, image string) (Result, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.n++
	return Result{OK: true, Detected: false}, nil
}

func (s *countingEmptyOCR) Calls() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.n
}

func TestScannerRunSkipsOCRAcrossInactiveBlankFrames(t *testing.T) {
	d := t.TempDir()
	raw := filepath.Join(d, "frames.rgb")
	const frames = 20
	if err := os.WriteFile(raw, make([]byte, frames*scanWidth*scanHeight*3), 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}

	eng := &countingEmptyOCR{}
	s := &Scanner{FFmpeg: fake, Engine: eng}
	res, err := s.Run(context.Background(), nil, ScanRequest{
		Path: input, Region: ScanRegion{X: 0.05, Y: 0.65, W: 0.9, H: 0.3},
		Mode: "balanced", Sensitivity: 1, Duration: float64(frames) / 2.5,
	})
	if err != nil {
		t.Fatal(err)
	}
	if res.OCRCalls != 0 || eng.Calls() != 0 {
		t.Fatalf("blank inactive scan invoked OCR: result=%d runner=%d", res.OCRCalls, eng.Calls())
	}
}

func TestScannerRunResumesCheckpointAndRemovesItAfterSuccess(t *testing.T) {
	d := t.TempDir()
	checkpointDir := filepath.Join(d, "checkpoints")
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy-video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{
		Path: input, Region: ScanRegion{X: 0.05, Y: 0.65, W: 0.9, H: 0.3},
		Mode: "accurate", Sensitivity: 1, Duration: 6,
	}
	key, err := scanCheckpointKey(req)
	if err != nil {
		t.Fatal(err)
	}
	checkpointPath := scanCheckpointFile(checkpointDir, key)
	if err := writeScanCheckpoint(checkpointPath, scanCheckpoint{
		Schema: checkpointSchema, Key: key, MediaSeconds: 4,
		Cues:   []Cue{{Start: 1, End: 2, Text: "已保存", Conf: .95}},
		Frames: 16, OCRCalls: 3,
	}); err != nil {
		t.Fatal(err)
	}

	raw := filepath.Join(d, "resume.rgb")
	const frames = 4
	frame := make([]byte, scanWidth*scanHeight*3)
	for y := 125; y < 205; y++ {
		for x := 170; x < 1110; x++ {
			if (x/8)%2 == 0 {
				i := (y*scanWidth + x) * 3
				frame[i], frame[i+1], frame[i+2] = 250, 205, 30
			}
		}
	}
	buf := make([]byte, 0, frames*len(frame))
	for i := 0; i < frames; i++ {
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	argsLog := filepath.Join(d, "args.txt")
	fake := filepath.Join(d, "ffmpeg")
	script := "#!/bin/sh\nprintf '%s\\n' \"$*\" > '" + argsLog + "'\ncat '" + raw + "'\n"
	if err := os.WriteFile(fake, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}

	s := &Scanner{FFmpeg: fake, Engine: &sequenceOCR{}, CheckpointDir: checkpointDir}
	res, err := s.Run(context.Background(), nil, req)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Cues) < 2 || res.Cues[0].Text != "已保存" {
		t.Fatalf("resume lost checkpoint cues: %#v", res.Cues)
	}
	args, err := os.ReadFile(argsLog)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(args, []byte("-ss 4.000")) {
		t.Fatalf("resume did not seek to checkpoint: %s", args)
	}
	if _, err := os.Stat(checkpointPath); !os.IsNotExist(err) {
		t.Fatalf("successful scan left checkpoint behind: err=%v", err)
	}
}

func TestScannerRunPreservesDurableCheckpointOnFFmpegFailure(t *testing.T) {
	d := t.TempDir()
	checkpointDir := filepath.Join(d, "checkpoints")
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy-video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{
		Path: input, Region: ScanRegion{X: 0.05, Y: 0.65, W: 0.9, H: 0.3},
		Mode: "accurate", Sensitivity: 1, Duration: 10,
	}
	raw := filepath.Join(d, "failure.rgb")
	const frames = 6
	frame := make([]byte, scanWidth*scanHeight*3)
	for y := 125; y < 205; y++ {
		for x := 170; x < 1110; x++ {
			if (x/8)%2 == 0 {
				i := (y*scanWidth + x) * 3
				frame[i], frame[i+1], frame[i+2] = 250, 205, 30
			}
		}
	}
	buf := make([]byte, 0, frames*len(frame))
	for i := 0; i < frames; i++ {
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\nexit 7\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	s := &Scanner{
		FFmpeg: fake, Engine: &sequenceOCR{}, CheckpointDir: checkpointDir,
		CheckpointIntervalSeconds: 0.5,
	}
	if _, err := s.Run(context.Background(), nil, req); err == nil {
		t.Fatal("expected FFmpeg failure")
	}
	key, err := scanCheckpointKey(req)
	if err != nil {
		t.Fatal(err)
	}
	cp, ok, err := readScanCheckpoint(scanCheckpointFile(checkpointDir, key), key)
	if err != nil || !ok {
		t.Fatalf("durable checkpoint missing after failure: ok=%v err=%v", ok, err)
	}
	if cp.MediaSeconds < 0.5 || cp.Active == nil || cp.Active.Text != "你好世界" {
		t.Fatalf("checkpoint did not preserve stable progress: %+v", cp)
	}
}

type sparseSegmentOCR struct {
	mu    sync.Mutex
	calls int
}

func (s *sparseSegmentOCR) Run(ctx context.Context, image string) (Result, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.calls++
	if s.calls == 1 {
		return Result{OK: true, Detected: true, Text: "长视频字幕", Confidence: 0.97}, nil
	}
	return Result{OK: true, Detected: false}, nil
}

func TestScannerStableSubtitleUsesOneTextOCRAndVisualConfirmation(t *testing.T) {
	d := t.TempDir()
	raw := filepath.Join(d, "frames.rgb")
	const frames = 14
	buf := make([]byte, 0, frames*scanWidth*scanHeight*3)
	for f := 0; f < frames; f++ {
		frame := make([]byte, scanWidth*scanHeight*3)
		if f >= 2 && f <= 8 {
			for y := 125; y < 205; y++ {
				for x := 170; x < 1110; x++ {
					if (x/8)%2 == 0 {
						i := (y*scanWidth + x) * 3
						frame[i], frame[i+1], frame[i+2] = 250, 205, 30
					}
				}
			}
		}
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}

	eng := &sparseSegmentOCR{}
	scanner := &Scanner{FFmpeg: fake, Engine: eng}
	res, err := scanner.Run(context.Background(), nil, ScanRequest{
		Path: input, Region: ScanRegion{X: 0.05, Y: 0.65, W: 0.9, H: 0.3},
		Mode: "accurate", Sensitivity: 0.75, Duration: float64(frames) / 4,
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(res.Cues) != 1 || res.Cues[0].Text != "长视频字幕" {
		t.Fatalf("cues=%#v", res.Cues)
	}
	if res.OCRCalls != 2 {
		t.Fatalf("stable segment should need one text OCR + one disappearance OCR, calls=%d result=%+v", res.OCRCalls, res)
	}
	if res.VisualConfirmations < 2 {
		t.Fatalf("expected text and blank visual confirmations, result=%+v", res)
	}
	if res.OCRCallsPerCue != 2 {
		t.Fatalf("calls/cue=%v want 2", res.OCRCallsPerCue)
	}
}

type parallelTransitionOCR struct {
	mu          sync.Mutex
	active      int
	maxActive   int
	barrier     chan struct{}
	barrierOnce sync.Once
}

func (p *parallelTransitionOCR) Parallelism() int { return 2 }
func (p *parallelTransitionOCR) Run(ctx context.Context, imageBase64 string) (Result, error) {
	b, err := base64.StdEncoding.DecodeString(imageBase64)
	if err != nil {
		return Result{}, err
	}
	img, err := png.Decode(bytes.NewReader(b))
	if err != nil {
		return Result{}, err
	}
	// The first visual pattern (vertical stripes) is colored at this point; the
	// second pattern (horizontal stripes) is black there. This makes the result
	// deterministic even though goroutine start/completion order is not.
	r, g, bch, _ := img.At(176, 153).RGBA()
	firstPattern := r+g+bch > 3*0x2000

	p.mu.Lock()
	p.active++
	if p.active > p.maxActive {
		p.maxActive = p.active
	}
	barrier := p.barrier
	if p.active >= 2 && barrier != nil {
		p.barrierOnce.Do(func() { close(barrier) })
	}
	p.mu.Unlock()
	// Make the concurrency assertion deterministic: the first independent OCR
	// transition waits briefly for the second one to enter Run. If production
	// accidentally serializes the transitions, the fake returns a bounded error
	// instead of depending on scheduler timing under -race.
	if barrier != nil {
		select {
		case <-barrier:
		case <-ctx.Done():
			p.mu.Lock()
			p.active--
			p.mu.Unlock()
			return Result{}, ctx.Err()
		case <-time.After(500 * time.Millisecond):
			p.mu.Lock()
			p.active--
			p.mu.Unlock()
			return Result{}, errors.New("OCR concurrency barrier timeout")
		}
	}
	delay := 10 * time.Millisecond
	if firstPattern {
		delay = 45 * time.Millisecond
	}
	select {
	case <-ctx.Done():
		p.mu.Lock()
		p.active--
		p.mu.Unlock()
		return Result{}, ctx.Err()
	case <-time.After(delay):
	}
	p.mu.Lock()
	p.active--
	p.mu.Unlock()
	if firstPattern {
		return Result{OK: true, Detected: true, Text: "第一句", Confidence: 0.97}, nil
	}
	return Result{OK: true, Detected: true, Text: "第二句", Confidence: 0.97}, nil
}
func (p *parallelTransitionOCR) MaxActive() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.maxActive
}

func TestScannerHybridRunsIndependentTransitionsConcurrentlyAndCommitsByTimestamp(t *testing.T) {
	d := t.TempDir()
	raw := filepath.Join(d, "frames.rgb")
	const frames = 12
	buf := make([]byte, 0, frames*scanWidth*scanHeight*3)
	for f := 0; f < frames; f++ {
		frame := make([]byte, scanWidth*scanHeight*3)
		if f == 2 {
			for y := 125; y < 205; y++ {
				for x := 170; x < 1110; x++ {
					if (x/8)%2 == 0 {
						i := (y*scanWidth + x) * 3
						frame[i], frame[i+1], frame[i+2] = 250, 205, 30
					}
				}
			}
		} else if f >= 3 && f <= 8 {
			for y := 125; y < 205; y++ {
				for x := 170; x < 1110; x++ {
					if (y/7)%2 == 0 {
						i := (y*scanWidth + x) * 3
						frame[i], frame[i+1], frame[i+2] = 245, 225, 40
					}
				}
			}
		}
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}
	eng := &parallelTransitionOCR{barrier: make(chan struct{})}
	scanner := &Scanner{FFmpeg: fake, Engine: eng}
	res, err := scanner.Run(context.Background(), nil, ScanRequest{
		Path: input, Region: ScanRegion{X: 0.05, Y: 0.65, W: 0.9, H: 0.3},
		Mode: "accurate", Device: DeviceHybrid, Sensitivity: 0.75, Duration: float64(frames) / 4,
	})
	if err != nil {
		t.Fatal(err)
	}
	if eng.MaxActive() != 2 {
		t.Fatalf("independent transitions should use both Hybrid workers, max=%d", eng.MaxActive())
	}
	if len(res.Cues) != 1 || res.Cues[0].Text != "第二句" {
		t.Fatalf("unexpected ordered result: %#v", res.Cues)
	}
	if math.Abs(res.Cues[0].Start-0.75) > 0.001 {
		t.Fatalf("completion order leaked into tracker: start=%v want=0.75", res.Cues[0].Start)
	}
}

type pauseProbeOCR struct {
	started chan struct{}
	once    sync.Once
}

func (p *pauseProbeOCR) Run(ctx context.Context, image string) (Result, error) {
	p.once.Do(func() { close(p.started) })
	select {
	case <-ctx.Done():
		return Result{}, ctx.Err()
	case <-time.After(80 * time.Millisecond):
	}
	return Result{OK: true, Detected: true, Text: "暂停测试字幕", Confidence: .98}, nil
}

func TestScannerPauseWaitsForSafeCheckpointAndCanBeInspected(t *testing.T) {
	d := t.TempDir()
	checkpointDir := filepath.Join(d, "checkpoints")
	raw := filepath.Join(d, "frames.rgb")
	const frames = 30
	frame := make([]byte, scanWidth*scanHeight*3)
	for y := 125; y < 205; y++ {
		for x := 170; x < 1110; x++ {
			if (x/8)%2 == 0 {
				i := (y*scanWidth + x) * 3
				frame[i], frame[i+1], frame[i+2] = 250, 205, 30
			}
		}
	}
	buf := make([]byte, 0, frames*len(frame))
	for i := 0; i < frames; i++ {
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy-video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{
		Path: input, Region: ScanRegion{X: .05, Y: .65, W: .9, H: .3},
		Mode: "accurate", Batch: "1", Sensitivity: .75, Duration: float64(frames) / 4,
	}
	eng := &pauseProbeOCR{started: make(chan struct{})}
	scanner := &Scanner{FFmpeg: fake, Engine: eng, CheckpointDir: checkpointDir}
	job := jobs.NewPausable("pause-scan")
	type runResult struct {
		result ScanResult
		err    error
	}
	done := make(chan runResult, 1)
	go func() {
		result, err := scanner.Run(context.Background(), job, req)
		done <- runResult{result: result, err: err}
	}()
	select {
	case <-eng.started:
	case <-time.After(2 * time.Second):
		t.Fatal("OCR did not start")
	}
	pauseDone, err := job.RequestPause()
	if err != nil {
		t.Fatal(err)
	}
	var got runResult
	select {
	case got = <-done:
	case <-time.After(3 * time.Second):
		t.Fatal("scanner did not reach safe pause boundary")
	}
	if !errors.Is(got.err, ErrScanPaused) {
		t.Fatalf("run err=%v want ErrScanPaused result=%+v", got.err, got.result)
	}
	job.PauseComplete("paused")
	select {
	case <-pauseDone:
	case <-time.After(time.Second):
		t.Fatal("pause handshake did not complete")
	}
	if got.result.MediaSeconds <= 0 {
		t.Fatalf("paused media=%v", got.result.MediaSeconds)
	}
	info, err := InspectCheckpoint(checkpointDir, req)
	if err != nil {
		t.Fatal(err)
	}
	if !info.Exists || info.MediaSeconds <= 0 || info.CueCount == 0 {
		t.Fatalf("checkpoint info=%+v", info)
	}
	if snap := job.Snapshot(0); snap.Status != "paused" || !snap.Done {
		t.Fatalf("job snapshot=%+v", snap)
	}
}

type batchTimeoutProbeOCR struct {
	started chan struct{}
	once    sync.Once
}

func (b *batchTimeoutProbeOCR) Run(ctx context.Context, image string) (Result, error) {
	b.once.Do(func() { close(b.started) })
	return Result{OK: true, Detected: true, Text: "及时刷新", Confidence: .98}, nil
}
func (b *batchTimeoutProbeOCR) BatchCapable() bool { return true }
func (b *batchTimeoutProbeOCR) RunBatch(ctx context.Context, images []string) ([]Result, error) {
	out := make([]Result, len(images))
	for i := range images {
		out[i] = Result{OK: true, Detected: true, Text: "及时刷新", Confidence: .98}
	}
	return out, nil
}

func TestScannerPartialBatchFlushesAfterMaxWaitInsteadOfWaitingForFutureFrame(t *testing.T) {
	d := t.TempDir()
	first := filepath.Join(d, "first.rgb")
	frame := make([]byte, scanWidth*scanHeight*3)
	for y := 125; y < 205; y++ {
		for x := 170; x < 1110; x++ {
			if (x/8)%2 == 0 {
				i := (y*scanWidth + x) * 3
				frame[i], frame[i+1], frame[i+2] = 250, 205, 30
			}
		}
	}
	if err := os.WriteFile(first, frame, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	script := `#!/usr/bin/env python3
import sys,time
with open(r"` + first + `","rb") as f:
    sys.stdout.buffer.write(f.read())
    sys.stdout.buffer.flush()
time.sleep(12)
`
	if err := os.WriteFile(fake, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}
	eng := &batchTimeoutProbeOCR{started: make(chan struct{})}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() {
		_, err := (&Scanner{FFmpeg: fake, Engine: eng}).run(ctx, nil, ScanRequest{
			Path: input, Region: ScanRegion{X: .05, Y: .65, W: .9, H: .3}, Mode: "balanced", Batch: "4", Sensitivity: .75, Duration: 5,
		}, "test forces software decode")
		done <- err
	}()
	select {
	case <-eng.started:
		// The next frame will not exist for five seconds. Reaching OCR here proves
		// the partial batch flushed on the bounded 25 ms wait instead of stalling.
	case <-time.After(8 * time.Second):
		// Under -race or a loaded CI host, process startup + RGB/PNG preparation
		// can be several seconds even though the 25 ms batch timer is correct.
		// The fake future frame is deliberately twelve seconds away, so an
		// eight-second bound still proves the partial batch did not wait for that
		// future frame without coupling the assertion to scheduler startup speed.
		cancel()
		t.Fatal("partial batch waited for a future frame instead of flushing")
	}
	cancel()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("scanner did not cancel after partial-batch test")
	}
}

type noFabricateBatchOCR struct {
	mu         sync.Mutex
	singleRuns int
	batchSizes []int
}

func (n *noFabricateBatchOCR) Run(ctx context.Context, image string) (Result, error) {
	n.mu.Lock()
	n.singleRuns++
	call := n.singleRuns
	n.mu.Unlock()
	if call == 1 {
		return Result{OK: true, Detected: true, Text: "稳定字幕", Confidence: .98}, nil
	}
	return Result{OK: true, Detected: false}, nil
}
func (n *noFabricateBatchOCR) BatchCapable() bool { return true }
func (n *noFabricateBatchOCR) RunBatch(ctx context.Context, images []string) ([]Result, error) {
	n.mu.Lock()
	n.batchSizes = append(n.batchSizes, len(images))
	n.mu.Unlock()
	out := make([]Result, len(images))
	for i := range images {
		out[i] = Result{OK: true, Detected: true, Text: "不应制造额外任务", Confidence: .98}
	}
	return out, nil
}
func (n *noFabricateBatchOCR) BatchSizes() []int {
	n.mu.Lock()
	defer n.mu.Unlock()
	return append([]int(nil), n.batchSizes...)
}

func TestScannerDoesNotManufactureStableFramesToFillBatch(t *testing.T) {
	d := t.TempDir()
	raw := filepath.Join(d, "stable.rgb")
	const frames = 12
	buf := make([]byte, 0, frames*scanWidth*scanHeight*3)
	for f := 0; f < frames; f++ {
		frame := make([]byte, scanWidth*scanHeight*3)
		if f >= 2 && f <= 8 {
			for y := 125; y < 205; y++ {
				for x := 170; x < 1110; x++ {
					if (x/8)%2 == 0 {
						i := (y*scanWidth + x) * 3
						frame[i], frame[i+1], frame[i+2] = 250, 205, 30
					}
				}
			}
		}
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy"), 0o644); err != nil {
		t.Fatal(err)
	}
	eng := &noFabricateBatchOCR{}
	res, err := (&Scanner{FFmpeg: fake, Engine: eng}).run(context.Background(), nil, ScanRequest{
		Path: input, Region: ScanRegion{X: .05, Y: .65, W: .9, H: .3}, Mode: "accurate", Batch: "4", Sensitivity: .75, Duration: float64(frames) / 4,
	}, "test forces software decode")
	if err != nil {
		t.Fatal(err)
	}
	if sizes := eng.BatchSizes(); len(sizes) != 0 {
		t.Fatalf("stable frames were incorrectly manufactured into a batch: %v", sizes)
	}
	if res.OCRImages > 2 {
		t.Fatalf("stable segment created excess OCR work: result=%+v", res)
	}
}

type parallelPauseOCR struct {
	mu        sync.Mutex
	active    int
	maxActive int
	started   int
	ready     chan struct{}
	once      sync.Once
	workers   int
	barrier   bool
}

func (p *parallelPauseOCR) ConfigureScanWorkers(ctx context.Context, target int) (int, error) {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.workers = target
	return target, nil
}

func (p *parallelPauseOCR) ActiveScanWorkers() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.workers
}

func (p *parallelPauseOCR) Run(ctx context.Context, image string) (Result, error) {
	p.mu.Lock()
	p.active++
	p.started++
	ordinal := p.started
	if p.active > p.maxActive {
		p.maxActive = p.active
	}
	if p.started >= 2 {
		p.once.Do(func() { close(p.ready) })
	}
	barrier := p.barrier && ordinal <= 2
	p.mu.Unlock()
	if barrier {
		select {
		case <-ctx.Done():
			p.mu.Lock()
			p.active--
			p.mu.Unlock()
			return Result{}, ctx.Err()
		case <-p.ready:
		}
	}
	select {
	case <-ctx.Done():
		p.mu.Lock()
		p.active--
		p.mu.Unlock()
		return Result{}, ctx.Err()
	case <-time.After(70 * time.Millisecond):
	}
	p.mu.Lock()
	p.active--
	p.mu.Unlock()
	return Result{OK: true, Detected: true, Text: "并行暂停字幕", Confidence: .98}, nil
}

func (p *parallelPauseOCR) MaxActive() int {
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.maxActive
}

func TestParallelScannerPauseBarrierWritesSchema4AndResumeCompletes(t *testing.T) {
	d := t.TempDir()
	checkpointDir := filepath.Join(d, "checkpoints")
	raw := filepath.Join(d, "frames.rgb")
	const frames = 80
	frame := make([]byte, scanWidth*scanHeight*3)
	for y := 125; y < 205; y++ {
		for x := 170; x < 1110; x++ {
			if (x/8)%2 == 0 {
				i := (y*scanWidth + x) * 3
				frame[i], frame[i+1], frame[i+2] = 250, 205, 30
			}
		}
	}
	buf := make([]byte, 0, frames*len(frame))
	for i := 0; i < frames; i++ {
		buf = append(buf, frame...)
	}
	if err := os.WriteFile(raw, buf, 0o644); err != nil {
		t.Fatal(err)
	}
	fake := filepath.Join(d, "ffmpeg")
	if err := os.WriteFile(fake, []byte("#!/bin/sh\ncat '"+raw+"'\n"), 0o755); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(d, "input.mp4")
	if err := os.WriteFile(input, []byte("dummy-video"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := ScanRequest{
		Path: input, Region: ScanRegion{X: .05, Y: .65, W: .9, H: .3},
		Mode: "balanced", Sensitivity: .75, Duration: 240, Parallelism: "2",
	}
	eng := &parallelPauseOCR{ready: make(chan struct{}), barrier: true}
	scanner := &Scanner{FFmpeg: fake, Engine: eng, CheckpointDir: checkpointDir}
	job := jobs.NewPausable("parallel-pause")
	type runResult struct {
		result ScanResult
		err    error
	}
	done := make(chan runResult, 1)
	go func() {
		result, err := scanner.Run(context.Background(), job, req)
		done <- runResult{result: result, err: err}
	}()
	select {
	case <-eng.ready:
	case <-time.After(3 * time.Second):
		t.Fatal("two parallel lanes did not reach OCR concurrently")
	}
	pauseDone, err := job.RequestPause()
	if err != nil {
		t.Fatal(err)
	}
	var paused runResult
	select {
	case paused = <-done:
	case <-time.After(5 * time.Second):
		t.Fatal("parallel scanner did not complete all-lane pause barrier")
	}
	if !errors.Is(paused.err, ErrScanPaused) {
		t.Fatalf("pause err=%v result=%+v", paused.err, paused.result)
	}
	if eng.MaxActive() < 2 {
		t.Fatalf("parallel lanes never overlapped OCR execution, max=%d", eng.MaxActive())
	}
	job.PauseComplete("paused")
	select {
	case <-pauseDone:
	case <-time.After(time.Second):
		t.Fatal("parallel pause handshake did not complete")
	}
	key, err := scanParallelCheckpointKey(req)
	if err != nil {
		t.Fatal(err)
	}
	cp, ok, err := readParallelScanCheckpoint(scanCheckpointFile(checkpointDir, key), key)
	if err != nil || !ok {
		t.Fatalf("schema4 checkpoint missing after pause: ok=%v err=%v", ok, err)
	}
	if cp.SelectedParallelism != 2 || len(cp.Lanes) != 2 {
		t.Fatalf("checkpoint topology=%+v", cp)
	}
	for i, lane := range cp.Lanes {
		if lane.Completed {
			continue
		}
		if lane.Media <= lane.Segment.ScanStart {
			t.Fatalf("lane %d was not durably advanced to a safe state: %+v", i, lane)
		}
	}

	resumeEngine := &parallelPauseOCR{ready: make(chan struct{})}
	resumeScanner := &Scanner{FFmpeg: fake, Engine: resumeEngine, CheckpointDir: checkpointDir}
	resumed, err := resumeScanner.Run(context.Background(), nil, req)
	if err != nil {
		t.Fatal(err)
	}
	if resumed.ParallelismSelected != 2 || resumed.CompletedLanes != 2 || resumed.ActiveLanes != 0 {
		t.Fatalf("resume result=%+v", resumed)
	}
	if _, err := os.Stat(scanCheckpointFile(checkpointDir, key)); !os.IsNotExist(err) {
		t.Fatalf("successful resume left schema4 checkpoint: err=%v", err)
	}
}
