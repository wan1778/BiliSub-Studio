package ocr

import (
	"bilisubstudio/internal/proc"
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

const (
	StateFailed   = -1
	StateStopped  = 0
	StateStarting = 1
	StateReady    = 2
)

type WorkerStatus struct {
	ID      string `json:"id"`
	Kind    string `json:"kind"`
	PID     int    `json:"pid,omitempty"`
	Runtime string `json:"runtime,omitempty"`
	Device  string `json:"device,omitempty"`
	Busy    bool   `json:"busy"`
}

type Status struct {
	State          int            `json:"state"`
	Ready          bool           `json:"ready"`
	Error          string         `json:"error,omitempty"`
	PID            int            `json:"pid,omitempty"`
	WorkerPIDs     map[string]int `json:"worker_pids,omitempty"`
	Workers        []WorkerStatus `json:"workers,omitempty"`
	ScanWorkers    int            `json:"scan_workers,omitempty"`
	Engine         string         `json:"engine,omitempty"`
	Model          string         `json:"model,omitempty"`
	Runtime        string         `json:"runtime,omitempty"`
	DeviceMode     string         `json:"device_mode"`
	ActiveMode     string         `json:"active_mode,omitempty"`
	ActiveDevices  []string       `json:"active_devices,omitempty"`
	GPUDetected    bool           `json:"gpu_detected"`
	GPUAvailable   bool           `json:"gpu_available"`
	GPUName        string         `json:"gpu_name,omitempty"`
	GPUDriver      string         `json:"gpu_driver,omitempty"`
	GPUError       string         `json:"gpu_error,omitempty"`
	FallbackReason string         `json:"fallback_reason,omitempty"`
}

type Line struct {
	Text       string  `json:"text"`
	Confidence float64 `json:"confidence"`
	Box        [4]int  `json:"box"`
}

type Result struct {
	OK         bool    `json:"ok"`
	Detected   bool    `json:"detected"`
	Text       string  `json:"text"`
	Confidence float64 `json:"confidence"`
	Lines      []Line  `json:"lines,omitempty"`
	Error      string  `json:"error,omitempty"`
}

type workerReady struct {
	Type          string   `json:"type"`
	Engine        string   `json:"engine"`
	PaddleOCR     string   `json:"paddleocr"`
	Paddle        string   `json:"paddle"`
	Models        []string `json:"models"`
	Device        string   `json:"device"`
	CUDAAvailable bool     `json:"cuda_available"`
	Error         string   `json:"error,omitempty"`
}

type workerResult struct {
	ID         uint64  `json:"id"`
	OK         bool    `json:"ok"`
	Detected   bool    `json:"detected"`
	Text       string  `json:"text"`
	Confidence float64 `json:"confidence"`
	Lines      []Line  `json:"lines"`
	Error      string  `json:"error,omitempty"`
	Type       string  `json:"type,omitempty"`
}

type workerBatchResult struct {
	ID      uint64         `json:"id"`
	OK      bool           `json:"ok"`
	Results []workerResult `json:"results"`
	Error   string         `json:"error,omitempty"`
	Type    string         `json:"type,omitempty"`
}

type workerClient struct {
	kind   string
	device string

	mu         sync.Mutex
	runMu      sync.Mutex
	cmd        *exec.Cmd
	stdin      io.WriteCloser
	lineCh     chan string
	doneCh     chan error
	generation uint64
	requestID  uint64
	runtime    string
	ready      workerReady
	lastErr    string
	onExit     func(error)
}

type Manager struct {
	Root string

	mu             sync.Mutex
	ensureMu       sync.Mutex
	state          int
	lastErr        string
	deviceMode     string
	activeMode     string
	fallbackReason string
	workers        map[string]*workerClient
	extraWorkers   map[string][]*workerClient
	workerBusy     map[*workerClient]bool
	workerWake     chan struct{}
	gpu            GPUInfo
	gpuChecked     bool
}

func New(root string) *Manager {
	return &Manager{
		Root: root, state: StateStopped, deviceMode: DeviceAuto,
		workers: map[string]*workerClient{}, extraWorkers: map[string][]*workerClient{},
		workerBusy: map[*workerClient]bool{}, workerWake: make(chan struct{}, 1),
	}
}

func (m *Manager) ConfigureDevice(mode string) error {
	mode, err := normalizeDeviceMode(mode)
	if err != nil {
		return err
	}
	m.mu.Lock()
	changed := m.deviceMode != mode
	m.deviceMode = mode
	m.mu.Unlock()
	if changed {
		m.stopWorkers()
		m.mu.Lock()
		m.state = StateStopped
		m.lastErr = ""
		m.activeMode = ""
		m.fallbackReason = ""
		m.workerBusy = map[*workerClient]bool{}
		m.workerWake = make(chan struct{}, 1)
		m.mu.Unlock()
	}
	return nil
}

func (m *Manager) RefreshCapabilities(ctx context.Context) GPUInfo {
	m.mu.Lock()
	if m.gpuChecked {
		info := m.gpu
		m.mu.Unlock()
		return info
	}
	m.mu.Unlock()
	info := detectNVIDIAGPU(ctx)
	m.mu.Lock()
	if !m.gpuChecked {
		m.gpu = info
		m.gpuChecked = true
	}
	info = m.gpu
	m.mu.Unlock()
	return info
}

func (m *Manager) Status() Status {
	m.mu.Lock()
	st := Status{
		State: m.state, Ready: m.state == StateReady, Error: m.lastErr,
		DeviceMode: m.deviceMode, ActiveMode: m.activeMode, FallbackReason: m.fallbackReason,
		GPUDetected: m.gpu.Detected, GPUAvailable: m.gpu.Usable, GPUName: m.gpu.Name, GPUDriver: m.gpu.Driver, GPUError: m.gpu.Error,
	}
	workers := m.allWorkersLocked("")
	busy := make(map[*workerClient]bool, len(m.workerBusy))
	for w, v := range m.workerBusy {
		busy[w] = v
	}
	m.mu.Unlock()

	if len(workers) == 0 {
		return st
	}
	st.Engine = "PaddleOCR"
	st.Model = detModelName + " + " + recModelName
	st.WorkerPIDs = map[string]int{}
	var runtimes []string
	kindIndex := map[string]int{}
	for _, w := range workers {
		pid, runtime, device := w.status()
		idx := kindIndex[w.kind]
		kindIndex[w.kind]++
		id := w.kind
		if idx > 0 {
			id = fmt.Sprintf("%s-%d", w.kind, idx)
		}
		if pid > 0 {
			st.WorkerPIDs[id] = pid
			if st.PID == 0 {
				st.PID = pid
			}
		}
		if runtime != "" {
			runtimes = append(runtimes, runtime)
		}
		if device != "" {
			st.ActiveDevices = append(st.ActiveDevices, device)
		}
		st.Workers = append(st.Workers, WorkerStatus{ID: id, Kind: w.kind, PID: pid, Runtime: runtime, Device: device, Busy: busy[w]})
	}
	sort.Strings(runtimes)
	st.Runtime = strings.Join(runtimes, " ; ")
	st.ScanWorkers = len(workers)
	return st
}

func (m *Manager) allWorkersLocked(kind string) []*workerClient {
	var out []*workerClient
	appendKind := func(k string) {
		if w := m.workers[k]; w != nil {
			out = append(out, w)
		}
		out = append(out, m.extraWorkers[k]...)
	}
	if kind != "" {
		appendKind(kind)
		return out
	}
	appendKind(DeviceGPU)
	appendKind(DeviceCPU)
	return out
}

func (m *Manager) Ensure(ctx context.Context) error {
	m.ensureMu.Lock()
	defer m.ensureMu.Unlock()

	m.mu.Lock()
	mode := m.deviceMode
	if m.readyForLocked(mode) {
		m.mu.Unlock()
		return nil
	}
	m.state = StateStarting
	m.lastErr = ""
	m.fallbackReason = ""
	m.mu.Unlock()

	var gpu GPUInfo
	if mode != DeviceCPU {
		gpu = m.RefreshCapabilities(ctx)
	}

	var err error
	switch mode {
	case DeviceCPU:
		err = m.startMode(ctx, DeviceCPU, gpu)
	case DeviceGPU:
		if !gpu.Usable {
			err = gpuUnavailableError(gpu)
		} else {
			err = m.startMode(ctx, DeviceGPU, gpu)
		}
	case DeviceHybrid:
		if !gpu.Usable {
			err = gpuUnavailableError(gpu)
		} else {
			err = m.startMode(ctx, DeviceHybrid, gpu)
		}
	default: // auto
		if gpu.Usable {
			if gpuErr := m.startMode(ctx, DeviceGPU, gpu); gpuErr == nil {
				err = nil
				break
			} else {
				m.stopWorkers()
				m.mu.Lock()
				m.fallbackReason = "GPU không khởi tạo được, đã chuyển CPU: " + gpuErr.Error()
				m.mu.Unlock()
			}
		} else if gpu.Detected || gpu.Error != "" {
			m.mu.Lock()
			m.fallbackReason = strings.TrimSpace(gpu.Error)
			m.mu.Unlock()
		}
		err = m.startMode(ctx, DeviceCPU, gpu)
	}
	if err != nil {
		m.stopWorkers()
		m.fail(err)
		return err
	}
	_ = m.cleanupLegacy()
	return nil
}

func gpuUnavailableError(info GPUInfo) error {
	if msg := strings.TrimSpace(info.Error); msg != "" {
		return errors.New(msg)
	}
	return errors.New("không có NVIDIA GPU tương thích cho PaddleOCR")
}

func (m *Manager) readyForLocked(mode string) bool {
	if m.state != StateReady {
		return false
	}
	if mode == DeviceAuto {
		return m.activeMode == DeviceCPU || m.activeMode == DeviceGPU
	}
	return m.activeMode == mode
}

func (m *Manager) startMode(ctx context.Context, mode string, gpu GPUInfo) error {
	m.stopWorkers()
	startOne := func(kind string) error {
		worker, err := m.startRuntimeWithRepair(ctx, kind, gpu)
		if err != nil {
			return err
		}
		m.mu.Lock()
		m.workers[kind] = worker
		m.mu.Unlock()
		return nil
	}

	switch mode {
	case DeviceCPU:
		if err := startOne(DeviceCPU); err != nil {
			return err
		}
	case DeviceGPU:
		if err := startOne(DeviceGPU); err != nil {
			return err
		}
	case DeviceHybrid:
		// Start GPU first so the shared PP-OCRv6 cache is fully materialized before
		// the CPU worker opens the same models. This avoids concurrent first-run
		// model downloads writing into one cache directory.
		if err := startOne(DeviceGPU); err != nil {
			return err
		}
		if err := startOne(DeviceCPU); err != nil {
			return err
		}
	default:
		return fmt.Errorf("chế độ OCR nội bộ không hợp lệ: %s", mode)
	}
	m.mu.Lock()
	m.state = StateReady
	m.lastErr = ""
	m.activeMode = mode
	if m.workerBusy == nil {
		m.workerBusy = map[*workerClient]bool{}
	}
	if m.workerWake == nil {
		m.workerWake = make(chan struct{}, 1)
	}
	m.mu.Unlock()
	return nil
}

func (m *Manager) startRuntimeWithRepair(ctx context.Context, kind string, gpu GPUInfo) (*workerClient, error) {
	start := func() (*workerClient, error) {
		inst, err := m.ensureInstalled(ctx, kind, gpu)
		if err != nil {
			return nil, err
		}
		w := &workerClient{kind: kind, device: inst.Device}
		w.onExit = func(exitErr error) { m.workerExited(w, exitErr) }
		if err := w.start(ctx, m.Root, inst); err != nil {
			_ = w.stop()
			return nil, err
		}
		return w, nil
	}
	w, err := start()
	if err == nil {
		return w, nil
	}
	// Preserve RC8's one-click repair contract. A corrupt private venv/model
	// cache is rebuilt once; system Python/PATH/CUDA are never modified.
	m.resetRuntimeForRepair(kind)
	return start()
}

func (m *Manager) workerExited(worker *workerClient, err error) {
	if worker == nil {
		return
	}
	m.mu.Lock()
	if m.state != StateReady {
		m.mu.Unlock()
		return
	}
	kind := worker.kind
	wasPrimary := m.workers[kind] == worker
	foundExtra := false
	if extras := m.extraWorkers[kind]; len(extras) > 0 {
		kept := extras[:0]
		for _, w := range extras {
			if w == worker {
				foundExtra = true
				continue
			}
			kept = append(kept, w)
		}
		m.extraWorkers[kind] = kept
	}
	delete(m.workerBusy, worker)
	msg := "PaddleOCR " + worker.device + " worker đã thoát"
	if err != nil {
		msg = "PaddleOCR " + worker.device + " worker thoát: " + err.Error()
	}
	if wasPrimary {
		if extras := m.extraWorkers[kind]; len(extras) > 0 {
			m.workers[kind] = extras[0]
			m.extraWorkers[kind] = extras[1:]
			m.lastErr = msg + " · đã chuyển sang worker dự phòng"
		} else {
			delete(m.workers, kind)
			m.state = StateFailed
			m.lastErr = msg
		}
	} else if foundExtra {
		m.lastErr = msg + " · pool đã giảm capacity"
	}
	wake := m.workerWake
	m.mu.Unlock()
	if wake != nil {
		select {
		case wake <- struct{}{}:
		default:
		}
	}
}

func (m *Manager) fail(err error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.state = StateFailed
	m.lastErr = err.Error()
	m.activeMode = ""
}

func (m *Manager) Stop() error {
	m.stopWorkers()
	m.mu.Lock()
	m.state = StateStopped
	m.lastErr = ""
	m.activeMode = ""
	m.fallbackReason = ""
	m.workerBusy = map[*workerClient]bool{}
	m.workerWake = make(chan struct{}, 1)
	m.mu.Unlock()
	return nil
}

func (m *Manager) stopWorkers() {
	m.mu.Lock()
	var workers []*workerClient
	seen := map[*workerClient]bool{}
	for _, w := range m.workers {
		if w != nil && !seen[w] {
			seen[w] = true
			workers = append(workers, w)
		}
	}
	for _, list := range m.extraWorkers {
		for _, w := range list {
			if w != nil && !seen[w] {
				seen[w] = true
				workers = append(workers, w)
			}
		}
	}
	m.workers = map[string]*workerClient{}
	m.extraWorkers = map[string][]*workerClient{}
	m.workerBusy = map[*workerClient]bool{}
	m.workerWake = make(chan struct{}, 1)
	m.mu.Unlock()
	for _, w := range workers {
		_ = w.stop()
	}
}

func (m *Manager) Remove() error {
	_ = m.Stop()
	if err := os.RemoveAll(m.Root); err != nil {
		return err
	}
	legacy := filepath.Join(filepath.Dir(m.Root), "RapidOCR")
	if filepath.Clean(legacy) != filepath.Clean(m.Root) {
		_ = os.RemoveAll(legacy)
	}
	return nil
}

func (m *Manager) Parallelism() int {
	return m.ActiveScanWorkers()
}

func (m *Manager) ActiveScanWorkers() int {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.state != StateReady {
		return 0
	}
	switch m.activeMode {
	case DeviceGPU:
		return len(m.allWorkersLocked(DeviceGPU))
	case DeviceCPU:
		return len(m.allWorkersLocked(DeviceCPU))
	case DeviceHybrid:
		return len(m.allWorkersLocked(""))
	default:
		return 0
	}
}

func (m *Manager) BatchCapable() bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.state == StateReady && m.activeMode == DeviceGPU && m.workers[DeviceGPU] != nil
}

func (m *Manager) RunBatch(ctx context.Context, images []string) ([]Result, error) {
	if len(images) == 0 {
		return nil, errors.New("batch OCR rỗng")
	}
	if len(images) > 4 {
		return nil, fmt.Errorf("batch OCR quá lớn: %d > 4", len(images))
	}
	for _, image := range images {
		if strings.TrimSpace(image) == "" {
			return nil, errors.New("batch OCR chứa ảnh rỗng")
		}
	}
	if err := m.Ensure(ctx); err != nil {
		return nil, err
	}
	w, pooled, err := m.acquireWorker(ctx)
	if err != nil {
		return nil, err
	}
	defer m.releaseWorker(w, pooled)
	if w.kind != DeviceGPU {
		results := make([]Result, len(images))
		for i, image := range images {
			res, err := w.run(ctx, image)
			if err != nil {
				return nil, err
			}
			results[i] = res
		}
		return results, nil
	}
	results, runErr := w.runBatch(ctx, images)
	if runErr != nil && !errors.Is(runErr, context.Canceled) && !errors.Is(runErr, context.DeadlineExceeded) {
		m.mu.Lock()
		m.lastErr = runErr.Error()
		m.mu.Unlock()
	}
	return results, runErr
}

func (m *Manager) Run(ctx context.Context, imageBase64 string) (Result, error) {
	if strings.TrimSpace(imageBase64) == "" {
		return Result{}, errors.New("imageBase64 rỗng")
	}
	if err := m.Ensure(ctx); err != nil {
		return Result{}, err
	}
	w, pooled, err := m.acquireWorker(ctx)
	if err != nil {
		return Result{}, err
	}
	res, runErr := w.run(ctx, imageBase64)
	reusable := runErr == nil || errors.Is(runErr, context.Canceled) || errors.Is(runErr, context.DeadlineExceeded)
	if !reusable {
		m.mu.Lock()
		m.lastErr = runErr.Error()
		m.mu.Unlock()
	}
	m.releaseWorker(w, pooled)
	return res, runErr
}

func (m *Manager) ConfigureScanWorkers(ctx context.Context, target int) (int, error) {
	if target < 1 || target > scanMaxManualParallelism {
		return 0, fmt.Errorf("số OCR worker không hợp lệ: %d", target)
	}
	if err := m.Ensure(ctx); err != nil {
		return 0, err
	}
	m.mu.Lock()
	active := m.activeMode
	gpu := m.gpu
	m.mu.Unlock()

	desiredGPU, desiredCPU := 0, 0
	switch active {
	case DeviceGPU:
		desiredGPU = target
	case DeviceCPU:
		if target > 2 {
			return m.ActiveScanWorkers(), fmt.Errorf("CPU OCR giới hạn tối đa 2 worker để tránh bão hòa hệ thống; hãy dùng Tự động hoặc GPU")
		}
		desiredCPU = target
	case DeviceHybrid:
		desiredCPU = 1
		desiredGPU = target - 1
		if desiredGPU < 1 {
			desiredGPU = 1
		}
	default:
		return 0, errors.New("OCR engine chưa có chế độ hoạt động")
	}
	if desiredGPU > 0 {
		if err := m.resizeWorkerKind(ctx, DeviceGPU, desiredGPU, gpu); err != nil {
			return m.ActiveScanWorkers(), err
		}
	}
	if desiredCPU > 0 {
		if err := m.resizeWorkerKind(ctx, DeviceCPU, desiredCPU, gpu); err != nil {
			return m.ActiveScanWorkers(), err
		}
	}
	return m.ActiveScanWorkers(), nil
}

// ResetScanWorkers is the bounded recovery path used by Auto calibration when
// a high-concurrency worker remains busy after its benchmark context has been
// canceled. Stop() force-terminates the managed worker processes, then the
// normal Ensure/Configure path rebuilds only the last known-good pool size.
func (m *Manager) ResetScanWorkers(ctx context.Context, target int) (int, error) {
	if target < 1 || target > scanMaxManualParallelism {
		return 0, fmt.Errorf("số OCR worker reset không hợp lệ: %d", target)
	}
	if err := m.Stop(); err != nil {
		return 0, err
	}
	return m.ConfigureScanWorkers(ctx, target)
}

func (m *Manager) resizeWorkerKind(ctx context.Context, kind string, desired int, gpu GPUInfo) error {
	if desired < 1 {
		desired = 1
	}
	for {
		m.mu.Lock()
		current := len(m.allWorkersLocked(kind))
		m.mu.Unlock()
		if current == desired {
			return nil
		}
		if current < desired {
			worker, err := m.startRuntimeExisting(ctx, kind, gpu)
			if err != nil {
				return fmt.Errorf("khởi tạo OCR worker %s thứ %d: %w", kind, current+1, err)
			}
			m.mu.Lock()
			m.extraWorkers[kind] = append(m.extraWorkers[kind], worker)
			m.mu.Unlock()
			continue
		}

		m.mu.Lock()
		extras := m.extraWorkers[kind]
		if len(extras) == 0 {
			m.mu.Unlock()
			return nil
		}
		idx := len(extras) - 1
		worker := extras[idx]
		if m.workerBusy[worker] {
			wake := m.workerWake
			m.mu.Unlock()
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-wake:
			}
			continue
		}
		m.extraWorkers[kind] = extras[:idx]
		delete(m.workerBusy, worker)
		m.mu.Unlock()
		_ = worker.stop()
	}
}

func (m *Manager) startRuntimeExisting(ctx context.Context, kind string, gpu GPUInfo) (*workerClient, error) {
	inst, err := m.ensureInstalled(ctx, kind, gpu)
	if err != nil {
		return nil, err
	}
	w := &workerClient{kind: kind, device: inst.Device}
	w.onExit = func(exitErr error) { m.workerExited(w, exitErr) }
	if err := w.start(ctx, m.Root, inst); err != nil {
		_ = w.stop()
		return nil, err
	}
	return w, nil
}

func (m *Manager) acquireWorker(ctx context.Context) (*workerClient, bool, error) {
	for {
		m.mu.Lock()
		if m.state != StateReady {
			errText := strings.TrimSpace(m.lastErr)
			m.mu.Unlock()
			if errText == "" {
				errText = "OCR worker không còn sẵn sàng"
			}
			return nil, false, errors.New(errText)
		}
		var candidates []*workerClient
		switch m.activeMode {
		case DeviceGPU:
			candidates = m.allWorkersLocked(DeviceGPU)
		case DeviceCPU:
			candidates = m.allWorkersLocked(DeviceCPU)
		case DeviceHybrid:
			candidates = append(candidates, m.allWorkersLocked(DeviceGPU)...)
			candidates = append(candidates, m.allWorkersLocked(DeviceCPU)...)
		}
		if m.workerBusy == nil {
			m.workerBusy = map[*workerClient]bool{}
		}
		for _, w := range candidates {
			if w != nil && !m.workerBusy[w] {
				m.workerBusy[w] = true
				m.mu.Unlock()
				return w, true, nil
			}
		}
		wake := m.workerWake
		m.mu.Unlock()
		if wake == nil {
			return nil, true, errors.New("bộ lập lịch OCR worker chưa sẵn sàng")
		}
		select {
		case <-ctx.Done():
			return nil, true, ctx.Err()
		case <-wake:
		}
	}
}

func (m *Manager) releaseWorker(w *workerClient, pooled bool) {
	if w == nil || !pooled {
		return
	}
	m.mu.Lock()
	if m.workerBusy != nil {
		m.workerBusy[w] = false
	}
	wake := m.workerWake
	m.mu.Unlock()
	if wake == nil {
		return
	}
	select {
	case wake <- struct{}{}:
	default:
	}
}

func (w *workerClient) start(ctx context.Context, root string, inst engineInstall) error {
	cmd := proc.Hide(exec.Command(inst.Python, "-u", inst.Worker, "--model-cache", inst.ModelCache, "--device", inst.Device))
	cmd.Dir = root
	cmd.Env = append(os.Environ(),
		"PADDLE_PDX_CACHE_HOME="+inst.ModelCache,
		"PADDLE_PDX_MODEL_SOURCE=BOS",
		"PYTHONUTF8=1",
		"PYTHONIOENCODING=utf-8",
	)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		return err
	}
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("khởi động PaddleOCR %s: %w", inst.Device, err)
	}

	lines := make(chan string, 32)
	done := make(chan error, 1)
	stderrCh := make(chan []byte, 1)
	w.mu.Lock()
	w.generation++
	gen := w.generation
	w.cmd = cmd
	w.stdin = stdin
	w.lineCh = lines
	w.doneCh = done
	w.runtime = inst.Python
	w.lastErr = ""
	w.mu.Unlock()
	go scanLines(stdout, lines)
	go func() {
		b, _ := io.ReadAll(stderr)
		stderrCh <- b
	}()
	go func() {
		err := cmd.Wait()
		w.mu.Lock()
		wasReady := w.ready.Type == "ready"
		onExit := w.onExit
		if w.generation == gen {
			if err == nil {
				w.lastErr = "PaddleOCR worker đã thoát"
			} else {
				w.lastErr = "PaddleOCR worker thoát: " + err.Error()
			}
			w.cmd = nil
			w.stdin = nil
		}
		w.mu.Unlock()
		if wasReady && onExit != nil {
			onExit(err)
		}
		done <- err
		close(done)
	}()

	timer := time.NewTimer(15 * time.Minute)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			_ = cmd.Process.Kill()
			return ctx.Err()
		case err := <-done:
			msg := ""
			select {
			case b := <-stderrCh:
				msg = strings.TrimSpace(string(b))
			default:
			}
			if err == nil {
				if msg != "" {
					return fmt.Errorf("PaddleOCR %s thoát khi khởi tạo: %s", inst.Device, msg)
				}
				return fmt.Errorf("PaddleOCR %s thoát khi khởi tạo", inst.Device)
			}
			if msg != "" {
				return fmt.Errorf("PaddleOCR %s thoát khi khởi tạo: %w: %s", inst.Device, err, msg)
			}
			return fmt.Errorf("PaddleOCR %s thoát khi khởi tạo: %w", inst.Device, err)
		case line, ok := <-lines:
			if !ok {
				return fmt.Errorf("PaddleOCR %s đóng stdout khi khởi tạo", inst.Device)
			}
			ready, err := parseWorkerReady(line, inst.Device)
			if err != nil {
				continue
			}
			w.mu.Lock()
			w.ready = ready
			w.lastErr = ""
			w.mu.Unlock()
			return nil
		case <-timer.C:
			_ = cmd.Process.Kill()
			return fmt.Errorf("PaddleOCR %s khởi tạo quá 15 phút", inst.Device)
		}
	}
}

func (w *workerClient) stop() error {
	w.mu.Lock()
	cmd := w.cmd
	w.generation++
	w.cmd = nil
	w.stdin = nil
	w.lineCh = nil
	w.doneCh = nil
	w.ready = workerReady{}
	w.lastErr = ""
	w.mu.Unlock()
	if cmd != nil && cmd.Process != nil {
		_ = cmd.Process.Kill()
	}
	return nil
}

func (w *workerClient) status() (pid int, runtime, device string) {
	w.mu.Lock()
	defer w.mu.Unlock()
	if w.cmd != nil && w.cmd.Process != nil {
		pid = w.cmd.Process.Pid
	}
	return pid, w.runtime, w.device
}

func (w *workerClient) abort(err error) {
	w.mu.Lock()
	cmd := w.cmd
	if err != nil {
		w.lastErr = err.Error()
	}
	w.mu.Unlock()
	if cmd != nil && cmd.Process != nil {
		_ = cmd.Process.Kill()
	}
}

func (w *workerClient) run(ctx context.Context, imageBase64 string) (Result, error) {
	w.runMu.Lock()
	defer w.runMu.Unlock()

	w.mu.Lock()
	if w.cmd == nil || w.stdin == nil || w.lineCh == nil || w.doneCh == nil {
		e := w.lastErr
		w.mu.Unlock()
		if e == "" {
			e = "OCR worker chưa sẵn sàng"
		}
		return Result{}, errors.New(e)
	}
	w.requestID++
	requestID := w.requestID
	stdin := w.stdin
	lines := w.lineCh
	done := w.doneCh
	w.mu.Unlock()

	payload, _ := json.Marshal(map[string]any{"id": requestID, "image_base64": imageBase64})
	payload = append(payload, '\n')
	if _, err := stdin.Write(payload); err != nil {
		wrapped := fmt.Errorf("ghi PaddleOCR %s stdin: %w", w.device, err)
		w.abort(wrapped)
		return Result{}, wrapped
	}

	timer := time.NewTimer(45 * time.Second)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			// Request IDs make the late response safe to ignore on the next call.
			return Result{}, ctx.Err()
		case err := <-done:
			if err == nil {
				err = fmt.Errorf("PaddleOCR %s worker đã thoát", w.device)
			}
			return Result{}, err
		case line, ok := <-lines:
			if !ok {
				return Result{}, fmt.Errorf("PaddleOCR %s worker đóng stdout", w.device)
			}
			res, id, err := parseWorkerResult(line)
			if err != nil || id != requestID {
				continue
			}
			if !res.OK && res.Error != "" {
				return res, errors.New(res.Error)
			}
			return res, nil
		case <-timer.C:
			err := fmt.Errorf("PaddleOCR %s không trả kết quả trong 45 giây", w.device)
			w.abort(err)
			return Result{}, err
		}
	}
}

func (w *workerClient) runBatch(ctx context.Context, images []string) ([]Result, error) {
	w.runMu.Lock()
	defer w.runMu.Unlock()

	w.mu.Lock()
	if w.cmd == nil || w.stdin == nil || w.lineCh == nil || w.doneCh == nil {
		e := w.lastErr
		w.mu.Unlock()
		if e == "" {
			e = "OCR worker chưa sẵn sàng"
		}
		return nil, errors.New(e)
	}
	w.requestID++
	requestID := w.requestID
	stdin := w.stdin
	lines := w.lineCh
	done := w.doneCh
	w.mu.Unlock()

	payload, _ := json.Marshal(map[string]any{"id": requestID, "images_base64": images})
	payload = append(payload, '\n')
	if _, err := stdin.Write(payload); err != nil {
		wrapped := fmt.Errorf("ghi PaddleOCR %s batch stdin: %w", w.device, err)
		w.abort(wrapped)
		return nil, wrapped
	}

	timer := time.NewTimer(60 * time.Second)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case err := <-done:
			if err == nil {
				err = fmt.Errorf("PaddleOCR %s worker đã thoát", w.device)
			}
			return nil, err
		case line, ok := <-lines:
			if !ok {
				return nil, fmt.Errorf("PaddleOCR %s worker đóng stdout", w.device)
			}
			results, id, err := parseWorkerBatchResult(line)
			if err != nil || id != requestID {
				continue
			}
			if len(results) != len(images) {
				return nil, fmt.Errorf("PaddleOCR %s batch trả %d/%d kết quả", w.device, len(results), len(images))
			}
			return results, nil
		case <-timer.C:
			err := fmt.Errorf("PaddleOCR %s không trả batch trong 60 giây", w.device)
			w.abort(err)
			return nil, err
		}
	}
}

func scanLines(r io.Reader, out chan<- string) {
	defer close(out)
	sc := bufio.NewScanner(r)
	buf := make([]byte, 64<<10)
	sc.Buffer(buf, 16<<20)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line != "" {
			out <- line
		}
	}
}

func parseWorkerReady(line, expectedDevice string) (workerReady, error) {
	var ready workerReady
	if err := json.Unmarshal([]byte(line), &ready); err != nil {
		return workerReady{}, err
	}
	if ready.Type != "ready" {
		return workerReady{}, errors.New("không phải ready event")
	}
	if ready.Engine != "PaddleOCR" || ready.PaddleOCR != paddleOCRVersion {
		return workerReady{}, fmt.Errorf("OCR worker sai phiên bản: %+v", ready)
	}
	if ready.Device != expectedDevice {
		return workerReady{}, fmt.Errorf("OCR worker chạy sai thiết bị: %s != %s", ready.Device, expectedDevice)
	}
	if strings.HasPrefix(expectedDevice, "gpu") && !ready.CUDAAvailable {
		return workerReady{}, errors.New("OCR GPU worker không xác nhận CUDA")
	}
	hasDet, hasRec := false, false
	for _, model := range ready.Models {
		hasDet = hasDet || model == detModelName
		hasRec = hasRec || model == recModelName
	}
	if !hasDet || !hasRec {
		return workerReady{}, fmt.Errorf("OCR worker không dùng đúng PP-OCRv6 Small")
	}
	return ready, nil
}

func parseWorkerResult(line string) (Result, uint64, error) {
	var wr workerResult
	if err := json.Unmarshal([]byte(line), &wr); err != nil {
		return Result{}, 0, err
	}
	if wr.Type != "" {
		return Result{}, 0, errors.New("worker control event")
	}
	if wr.ID == 0 {
		return Result{}, 0, errors.New("worker result thiếu request id")
	}
	return Result{
		OK: wr.OK, Detected: wr.Detected, Text: wr.Text,
		Confidence: wr.Confidence, Lines: wr.Lines, Error: wr.Error,
	}, wr.ID, nil
}

func parseWorkerBatchResult(line string) ([]Result, uint64, error) {
	var wr workerBatchResult
	if err := json.Unmarshal([]byte(line), &wr); err != nil {
		return nil, 0, err
	}
	if wr.Type != "" {
		return nil, 0, errors.New("worker control event")
	}
	if wr.ID == 0 {
		return nil, 0, errors.New("worker batch result thiếu request id")
	}
	if !wr.OK {
		if strings.TrimSpace(wr.Error) == "" {
			wr.Error = "PaddleOCR batch lỗi"
		}
		return nil, wr.ID, errors.New(wr.Error)
	}
	out := make([]Result, len(wr.Results))
	for i, item := range wr.Results {
		out[i] = Result{OK: item.OK, Detected: item.Detected, Text: item.Text, Confidence: item.Confidence, Lines: item.Lines, Error: item.Error}
		if !out[i].OK && out[i].Error != "" {
			return nil, wr.ID, errors.New(out[i].Error)
		}
	}
	return out, wr.ID, nil
}
