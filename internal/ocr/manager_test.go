package ocr

import (
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"
	"time"
)

func TestParseWorkerResultPreservesDetectionLines(t *testing.T) {
	line := `{"id":7,"ok":true,"detected":true,"text":"你好\nworld","confidence":0.8,"lines":[{"text":"你好","confidence":0.9,"box":[10,20,80,48]},{"text":"world","confidence":0.7,"box":[90,20,180,48]}]}`
	got, id, err := parseWorkerResult(line)
	if err != nil {
		t.Fatal(err)
	}
	if id != 7 || !got.OK || !got.Detected || got.Text != "你好\nworld" {
		t.Fatalf("id=%d got=%+v", id, got)
	}
	if len(got.Lines) != 2 || got.Lines[0].Box != [4]int{10, 20, 80, 48} {
		t.Fatalf("lines=%+v", got.Lines)
	}
}

func TestParseWorkerReadyRequiresPinnedSmallModels(t *testing.T) {
	line := `{"type":"ready","engine":"PaddleOCR","paddleocr":"3.7.0","paddle":"3.2.0","models":["PP-OCRv6_small_det","PP-OCRv6_small_rec"],"device":"cpu","cuda_available":false}`
	ready, err := parseWorkerReady(line, "cpu")
	if err != nil {
		t.Fatal(err)
	}
	if ready.Engine != "PaddleOCR" || ready.PaddleOCR != paddleOCRVersion {
		t.Fatalf("ready=%+v", ready)
	}
}

type testWriteCloser struct{ writes int }

func (w *testWriteCloser) Write(p []byte) (int, error) { w.writes++; return len(p), nil }
func (w *testWriteCloser) Close() error                { return nil }

func TestRunCancellationKeepsIDCapableEngineReusable(t *testing.T) {
	m := New(t.TempDir())
	wc := &testWriteCloser{}
	w := &workerClient{kind: DeviceCPU, device: "cpu", stdin: wc, lineCh: make(chan string), doneCh: make(chan error), cmd: &exec.Cmd{Process: &os.Process{Pid: 999999}}}
	m.mu.Lock()
	m.state = StateReady
	m.activeMode = DeviceCPU
	m.deviceMode = DeviceCPU
	m.workers[DeviceCPU] = w
	m.mu.Unlock()

	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	_, err := m.Run(ctx, "AAAA")
	if err == nil {
		t.Fatal("expected cancellation")
	}
	if wc.writes != 1 {
		t.Fatalf("expected one request write, got %d", wc.writes)
	}
	if st := m.Status(); st.State != StateReady {
		t.Fatalf("request IDs make late responses safe; engine should remain ready: %+v", st)
	}
}

func TestManagerStartsPersistentPaddleWorkerFixture(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("fixture uses a POSIX shell as fake managed python")
	}
	root := t.TempDir()
	python := writeHealthyPaddleInstall(t, root)
	script := `#!/bin/sh
	printf '%s\n' '{"type":"ready","engine":"PaddleOCR","paddleocr":"3.7.0","paddle":"3.2.0","models":["PP-OCRv6_small_det","PP-OCRv6_small_rec"],"device":"cpu","cuda_available":false}'
while IFS= read -r line; do
  id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
  printf '{"id":%s,"ok":true,"detected":true,"text":"fixture-ok","confidence":0.95,"lines":[{"text":"fixture-ok","confidence":0.95,"box":[1,2,3,4]}]}\n' "${id:-0}"
done
`
	if err := os.WriteFile(python, []byte(script), 0o755); err != nil {
		t.Fatal(err)
	}
	m := New(root)
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	if err := m.Ensure(ctx); err != nil {
		t.Fatal(err)
	}
	defer m.Stop()
	got, err := m.Run(ctx, "AAAA")
	if err != nil {
		t.Fatal(err)
	}
	if !got.OK || !got.Detected || got.Text != "fixture-ok" || got.Confidence < 0.94 {
		t.Fatalf("got=%+v", got)
	}
	if filepath.Base(m.Status().Runtime) != "python.exe" {
		t.Fatalf("status=%+v", m.Status())
	}
}

func TestHybridWorkerAvailabilityQueueDoesNotForceRoundRobin(t *testing.T) {
	gpu := &workerClient{kind: DeviceGPU, device: "gpu:0"}
	cpu := &workerClient{kind: DeviceCPU, device: "cpu"}
	m := New(t.TempDir())
	m.mu.Lock()
	m.state = StateReady
	m.deviceMode = DeviceHybrid
	m.activeMode = DeviceHybrid
	m.workers[DeviceGPU] = gpu
	m.workers[DeviceCPU] = cpu
	m.workerBusy = map[*workerClient]bool{}
	m.workerWake = make(chan struct{}, 1)
	m.mu.Unlock()

	first, hybrid, err := m.acquireWorker(context.Background())
	if err != nil || first != gpu || !hybrid {
		t.Fatalf("first acquire: worker=%p want gpu=%p hybrid=%v err=%v", first, gpu, hybrid, err)
	}
	second, hybrid, err := m.acquireWorker(context.Background())
	if err != nil || second != cpu || !hybrid {
		t.Fatalf("second acquire: worker=%p want cpu=%p hybrid=%v err=%v", second, cpu, hybrid, err)
	}

	// GPU finishes first. Returning only GPU must make GPU immediately available
	// again instead of forcing the next request to wait for a fixed CPU turn.
	m.releaseWorker(gpu, true)
	third, _, err := m.acquireWorker(context.Background())
	if err != nil || third != gpu {
		t.Fatalf("dynamic acquire after GPU return: worker=%p want gpu=%p err=%v", third, gpu, err)
	}

	// Once both workers are idle, sequential work must still prefer GPU.
	m.releaseWorker(third, true)
	m.releaseWorker(cpu, true)
	fourth, _, err := m.acquireWorker(context.Background())
	if err != nil || fourth != gpu {
		t.Fatalf("sequential hybrid request should prefer gpu: worker=%p want gpu=%p err=%v", fourth, gpu, err)
	}
}

func TestHybridWorkerAcquireHonorsCancellationWhenAllWorkersBusy(t *testing.T) {
	m := New(t.TempDir())
	m.mu.Lock()
	m.state = StateReady
	m.deviceMode = DeviceHybrid
	m.activeMode = DeviceHybrid
	m.workers[DeviceGPU] = &workerClient{kind: DeviceGPU}
	m.workers[DeviceCPU] = &workerClient{kind: DeviceCPU}
	m.workerBusy = map[*workerClient]bool{m.workers[DeviceGPU]: true, m.workers[DeviceCPU]: true}
	m.workerWake = make(chan struct{}, 1)
	m.mu.Unlock()

	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if _, _, err := m.acquireWorker(ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("acquire err=%v want deadline exceeded", err)
	}
}

func TestParseWorkerBatchResultPreservesOrder(t *testing.T) {
	line := `{"id":9,"ok":true,"results":[{"ok":true,"detected":true,"text":"A","confidence":0.9,"lines":[]},{"ok":true,"detected":true,"text":"B","confidence":0.8,"lines":[]}]}`
	got, id, err := parseWorkerBatchResult(line)
	if err != nil {
		t.Fatal(err)
	}
	if id != 9 || len(got) != 2 || got[0].Text != "A" || got[1].Text != "B" {
		t.Fatalf("id=%d got=%+v", id, got)
	}
}

func TestRunBatchRejectsOversizedBatch(t *testing.T) {
	m := New(t.TempDir())
	_, err := m.RunBatch(context.Background(), []string{"1", "2", "3", "4", "5"})
	if err == nil {
		t.Fatal("expected oversized batch rejection")
	}
}

func TestWorkerPoolAcquireDistributesConcurrentRequestsAcrossDistinctGPUWorkers(t *testing.T) {
	primary := &workerClient{kind: DeviceGPU, device: "gpu:0"}
	extra1 := &workerClient{kind: DeviceGPU, device: "gpu:0"}
	extra2 := &workerClient{kind: DeviceGPU, device: "gpu:0"}
	extra3 := &workerClient{kind: DeviceGPU, device: "gpu:0"}
	m := New(t.TempDir())
	m.mu.Lock()
	m.state = StateReady
	m.deviceMode = DeviceGPU
	m.activeMode = DeviceGPU
	m.workers[DeviceGPU] = primary
	m.extraWorkers[DeviceGPU] = []*workerClient{extra1, extra2, extra3}
	m.workerBusy = map[*workerClient]bool{}
	m.workerWake = make(chan struct{}, 1)
	m.mu.Unlock()

	seen := map[*workerClient]bool{}
	var acquired []*workerClient
	for i := 0; i < 4; i++ {
		w, pooled, err := m.acquireWorker(context.Background())
		if err != nil || !pooled {
			t.Fatalf("acquire %d: pooled=%v err=%v", i, pooled, err)
		}
		if seen[w] {
			t.Fatalf("worker reused while busy at acquire %d", i)
		}
		seen[w] = true
		acquired = append(acquired, w)
	}
	if len(seen) != 4 {
		t.Fatalf("got %d distinct workers, want 4", len(seen))
	}
	for _, w := range acquired {
		m.releaseWorker(w, true)
	}
	if got := m.ActiveScanWorkers(); got != 4 {
		t.Fatalf("ActiveScanWorkers=%d want 4", got)
	}
}

func TestWorkerPoolAcquireBlocksWhenAllGPUWorkersBusyAndHonorsCancellation(t *testing.T) {
	primary := &workerClient{kind: DeviceGPU, device: "gpu:0"}
	extra := &workerClient{kind: DeviceGPU, device: "gpu:0"}
	m := New(t.TempDir())
	m.mu.Lock()
	m.state = StateReady
	m.deviceMode = DeviceGPU
	m.activeMode = DeviceGPU
	m.workers[DeviceGPU] = primary
	m.extraWorkers[DeviceGPU] = []*workerClient{extra}
	m.workerBusy = map[*workerClient]bool{primary: true, extra: true}
	m.workerWake = make(chan struct{}, 1)
	m.mu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Millisecond)
	defer cancel()
	if _, _, err := m.acquireWorker(ctx); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("acquire err=%v want deadline exceeded", err)
	}
}
