package jobs

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"
)

type Snapshot struct {
	ID             string   `json:"id"`
	Status         string   `json:"status"`
	Progress       float64  `json:"progress"`
	Message        string   `json:"message"`
	Logs           []string `json:"logs"`
	LogNext        int      `json:"log_next"`
	Done           bool     `json:"done"`
	Error          string   `json:"error,omitempty"`
	Result         any      `json:"result,omitempty"`
	PauseSupported bool     `json:"pause_supported,omitempty"`
	PauseRequested bool     `json:"pause_requested,omitempty"`
}

type Job struct {
	ID             string
	ctx            context.Context
	cancel         context.CancelFunc
	mu             sync.Mutex
	status         string
	progress       float64
	message        string
	logs           []string
	done           bool
	err            string
	result         any
	pauseSupported bool
	pauseRequested bool
	pauseDone      chan struct{}
	pauseDoneOnce  sync.Once
}

func New(id string) *Job {
	return newJob(id, false)
}

func NewPausable(id string) *Job {
	return newJob(id, true)
}

func newJob(id string, pauseSupported bool) *Job {
	ctx, cancel := context.WithCancel(context.Background())
	j := &Job{ID: id, ctx: ctx, cancel: cancel, status: "queued", message: "Đang chờ.", pauseSupported: pauseSupported}
	if pauseSupported {
		j.pauseDone = make(chan struct{})
	}
	return j
}
func (j *Job) Context() context.Context { return j.ctx }
func (j *Job) Cancel() {
	j.cancel()
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.done {
		return
	}
	j.status = "cancelled"
	j.message = "Đã hủy tác vụ."
	j.done = true
	j.signalPauseDoneLocked()
}

func (j *Job) RequestPause() (<-chan struct{}, error) {
	j.mu.Lock()
	defer j.mu.Unlock()
	if !j.pauseSupported {
		return nil, errors.New("tác vụ này không hỗ trợ tạm dừng")
	}
	if j.done {
		return nil, errors.New("tác vụ đã kết thúc")
	}
	j.pauseRequested = true
	j.status = "pausing"
	j.message = "Đang lưu checkpoint an toàn để tạm dừng..."
	return j.pauseDone, nil
}

func (j *Job) PauseRequested() bool {
	j.mu.Lock()
	defer j.mu.Unlock()
	return j.pauseRequested && !j.done
}

func (j *Job) PauseComplete(message string) {
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.done {
		return
	}
	j.status = "paused"
	j.done = true
	if strings.TrimSpace(message) == "" {
		message = "Đã tạm dừng tại checkpoint an toàn."
	}
	j.message = message
	j.signalPauseDoneLocked()
}

func (j *Job) signalPauseDoneLocked() {
	if j.pauseDone != nil {
		j.pauseDoneOnce.Do(func() { close(j.pauseDone) })
	}
}
func (j *Job) Logf(f string, a ...any) {
	j.mu.Lock()
	defer j.mu.Unlock()
	j.logs = append(j.logs, time.Now().Format("15:04:05")+"  "+fmt.Sprintf(f, a...))
}
func (j *Job) SetResult(v any) {
	j.mu.Lock()
	defer j.mu.Unlock()
	j.result = v
}
func (j *Job) Set(status string, progress float64, message string) {
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.done {
		return
	}
	j.status = status
	if progress >= 0 {
		j.progress = progress
	}
	j.message = message
}
func (j *Job) Finish(err error, message string) {
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.done {
		return
	}
	j.done = true
	j.signalPauseDoneLocked()
	if err != nil {
		j.status = "error"
		j.err = err.Error()
		if message == "" {
			message = err.Error()
		}
	} else {
		j.status = "done"
		j.progress = 100
	}
	j.message = message
}
func (j *Job) Snapshot(after int) Snapshot {
	j.mu.Lock()
	defer j.mu.Unlock()
	if after < 0 {
		after = 0
	}
	if after > len(j.logs) {
		after = len(j.logs)
	}
	logs := append([]string(nil), j.logs[after:]...)
	return Snapshot{ID: j.ID, Status: j.status, Progress: j.progress, Message: j.message, Logs: logs, LogNext: len(j.logs), Done: j.done, Error: j.err, Result: j.result, PauseSupported: j.pauseSupported, PauseRequested: j.pauseRequested}
}

type Manager struct {
	mu sync.RWMutex
	m  map[string]*Job
}

func NewManager() *Manager    { return &Manager{m: map[string]*Job{}} }
func (m *Manager) Add(j *Job) { m.mu.Lock(); defer m.mu.Unlock(); m.m[j.ID] = j }
func (m *Manager) Get(id string) (*Job, bool) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	j, ok := m.m[id]
	return j, ok
}

func (m *Manager) Active() bool {
	m.mu.RLock()
	defer m.mu.RUnlock()
	for _, j := range m.m {
		s := j.Snapshot(0)
		if !s.Done {
			return true
		}
	}
	return false
}

func (m *Manager) ActiveSnapshots() []Snapshot {
	m.mu.RLock()
	list := make([]*Job, 0, len(m.m))
	for _, j := range m.m {
		list = append(list, j)
	}
	m.mu.RUnlock()
	out := make([]Snapshot, 0, len(list))
	for _, j := range list {
		s := j.Snapshot(0)
		if !s.Done {
			out = append(out, s)
		}
	}
	return out
}

func (m *Manager) CancelAll() {
	m.mu.RLock()
	list := make([]*Job, 0, len(m.m))
	for _, j := range m.m {
		list = append(list, j)
	}
	m.mu.RUnlock()
	for _, j := range list {
		if !j.Snapshot(0).Done {
			j.Cancel()
		}
	}
}
