package ocr

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"math"
	"os"
	"path/filepath"
	"strings"
)

const (
	checkpointSchema         = 3
	parallelCheckpointSchema = 4
)

type scanCheckpointStats struct {
	OCRImages            int     `json:"ocr_images,omitempty"`
	OCRBatchCalls        int     `json:"ocr_batch_calls,omitempty"`
	VisualSkips          int     `json:"visual_skips,omitempty"`
	VisualConfirmations  int     `json:"visual_confirmations,omitempty"`
	OCRRetries           int     `json:"ocr_retries,omitempty"`
	FramePipelineSeconds float64 `json:"frame_pipeline_seconds,omitempty"`
	VisualSeconds        float64 `json:"visual_seconds,omitempty"`
	EncodeSeconds        float64 `json:"encode_seconds,omitempty"`
	OCRSeconds           float64 `json:"ocr_seconds,omitempty"`
}

type scanCheckpoint struct {
	Schema       int                 `json:"schema"`
	Key          string              `json:"key"`
	MediaSeconds float64             `json:"media_seconds"`
	Cues         []Cue               `json:"cues"`
	Active       *Cue                `json:"active,omitempty"`
	Frames       int                 `json:"frames"`
	OCRCalls     int                 `json:"ocr_calls"` // Backward-compatible alias for OCRImages.
	Stats        scanCheckpointStats `json:"stats,omitempty"`
}

type CheckpointInfo struct {
	Exists               bool    `json:"exists"`
	Schema               int     `json:"schema,omitempty"`
	MediaSeconds         float64 `json:"media_seconds,omitempty"`
	CueCount             int     `json:"cue_count,omitempty"`
	Frames               int     `json:"frames,omitempty"`
	OCRCalls             int     `json:"ocr_calls,omitempty"`
	OCRBatchCalls        int     `json:"ocr_batch_calls,omitempty"`
	VisualSkips          int     `json:"visual_skips,omitempty"`
	VisualConfirmations  int     `json:"visual_confirmations,omitempty"`
	OCRRetries           int     `json:"ocr_retries,omitempty"`
	FramePipelineSeconds float64 `json:"frame_pipeline_seconds,omitempty"`
	VisualSeconds        float64 `json:"visual_seconds,omitempty"`
	EncodeSeconds        float64 `json:"encode_seconds,omitempty"`
	OCRSeconds           float64 `json:"ocr_seconds,omitempty"`
	RecentCues           []Cue   `json:"recent_cues,omitempty"`
	ParallelismSelected  int     `json:"parallelism_selected,omitempty"`
	ActiveLanes          int     `json:"active_lanes"`
	CompletedLanes       int     `json:"completed_lanes"`
	TotalLanes           int     `json:"total_lanes,omitempty"`
	BoundaryMerges       int     `json:"boundary_merges"`
	ProgressPercent      float64 `json:"progress_percent,omitempty"`
}

func InspectCheckpoint(dir string, req ScanRequest) (CheckpointInfo, error) {
	dir = strings.TrimSpace(dir)
	if dir == "" {
		return CheckpointInfo{}, nil
	}
	parallelKey, err := scanParallelCheckpointKey(req)
	if err != nil {
		return CheckpointInfo{}, err
	}
	if cp, ok, readErr := readParallelScanCheckpoint(scanCheckpointFile(dir, parallelKey), parallelKey); readErr != nil {
		return CheckpointInfo{}, readErr
	} else if ok {
		return parallelCheckpointInfo(cp), nil
	}

	key, err := scanCheckpointKey(req)
	if err != nil {
		return CheckpointInfo{}, err
	}
	cp, ok, err := readScanCheckpoint(scanCheckpointFile(dir, key), key)
	if err != nil {
		return CheckpointInfo{}, err
	}
	if !ok {
		return CheckpointInfo{}, nil
	}
	cues := append([]Cue(nil), cp.Cues...)
	if cp.Active != nil {
		cues = append(cues, *cp.Active)
	}
	const maxRecent = 120
	if len(cues) > maxRecent {
		cues = cues[len(cues)-maxRecent:]
	}
	stats := cp.Stats
	if stats.OCRImages == 0 && cp.OCRCalls > 0 {
		stats.OCRImages = cp.OCRCalls
	}
	progressPercent := 0.0
	if req.Duration > 0 {
		progressPercent = math.Min(100, math.Max(0, cp.MediaSeconds/req.Duration*100))
	}
	return CheckpointInfo{
		Exists: true, Schema: checkpointSchema, MediaSeconds: cp.MediaSeconds, CueCount: len(cp.Cues) + boolInt(cp.Active != nil),
		Frames: cp.Frames, OCRCalls: stats.OCRImages, OCRBatchCalls: stats.OCRBatchCalls,
		VisualSkips: stats.VisualSkips, VisualConfirmations: stats.VisualConfirmations, OCRRetries: stats.OCRRetries,
		FramePipelineSeconds: stats.FramePipelineSeconds, VisualSeconds: stats.VisualSeconds, EncodeSeconds: stats.EncodeSeconds, OCRSeconds: stats.OCRSeconds,
		RecentCues: cues, ParallelismSelected: 1, ActiveLanes: 0, TotalLanes: 1, ProgressPercent: progressPercent,
	}, nil
}

// legacyCheckpointAvailable reports whether a valid schema-3 checkpoint exists
// and no schema-4 checkpoint has taken ownership of this scan identity. RC13 UI
// always sends Parallelism, so this guard preserves resume of interrupted RC12
// scans instead of silently starting a new parallel topology.
func legacyCheckpointAvailable(dir string, req ScanRequest) (bool, error) {
	dir = strings.TrimSpace(dir)
	if dir == "" {
		return false, nil
	}
	parallelKey, err := scanParallelCheckpointKey(req)
	if err != nil {
		return false, err
	}
	parallelPath := scanCheckpointFile(dir, parallelKey)
	if _, statErr := os.Stat(parallelPath); statErr == nil {
		// A schema-4 file, even if corrupt, belongs to the RC13 path. The normal
		// parallel checkpoint session is responsible for validating/removing it.
		return false, nil
	} else if !errors.Is(statErr, os.ErrNotExist) {
		return false, statErr
	}
	legacyKey, err := scanCheckpointKey(req)
	if err != nil {
		return false, err
	}
	_, ok, err := readScanCheckpoint(scanCheckpointFile(dir, legacyKey), legacyKey)
	if err != nil {
		// Keep legacy cleanup behavior in newScanCheckpointSession: a corrupt
		// schema-3 file is treated as legacy so the old path can remove it and
		// restart safely rather than blocking the user.
		return true, nil
	}
	return ok, nil
}

func RemoveCheckpoint(dir string, req ScanRequest) error {
	dir = strings.TrimSpace(dir)
	if dir == "" {
		return nil
	}
	var firstErr error
	for _, schema := range []int{parallelCheckpointSchema, checkpointSchema} {
		key, err := scanCheckpointKeyForSchema(req, schema)
		if err != nil {
			return err
		}
		err = os.Remove(scanCheckpointFile(dir, key))
		if err != nil && !errors.Is(err, os.ErrNotExist) && firstErr == nil {
			firstErr = err
		}
	}
	return firstErr
}

func scanCheckpointKey(req ScanRequest) (string, error) {
	return scanCheckpointKeyForSchema(req, checkpointSchema)
}

func scanCheckpointKeyForSchema(req ScanRequest, schema int) (string, error) {
	path, err := filepath.Abs(strings.TrimSpace(req.Path))
	if err != nil {
		return "", err
	}
	st, err := os.Stat(path)
	if err != nil {
		return "", err
	}
	if st.IsDir() || st.Size() <= 0 {
		return "", errors.New("video nguồn không tồn tại hoặc rỗng")
	}
	region, err := normalizeScanRegion(req.Region)
	if err != nil {
		return "", err
	}
	mode := scanModeFor(req.Mode, req.Sensitivity)
	identity := struct {
		Schema      int        `json:"schema"`
		Path        string     `json:"path"`
		Size        int64      `json:"size"`
		ModUnixNano int64      `json:"mtime_ns"`
		Region      ScanRegion `json:"region"`
		Mode        string     `json:"mode"`
		FPS         float64    `json:"fps"`
		Guard       float64    `json:"guard"`
		ActiveGuard float64    `json:"active_guard"`
		Diff        float64    `json:"diff"`
		Width       int        `json:"width"`
		Height      int        `json:"height"`
	}{
		Schema: schema, Path: filepath.Clean(path), Size: st.Size(), ModUnixNano: st.ModTime().UnixNano(),
		Region: region, Mode: strings.ToLower(strings.TrimSpace(req.Mode)), FPS: mode.FPS, Guard: mode.GuardSeconds,
		ActiveGuard: mode.ActiveGuardSeconds, Diff: mode.DiffTrigger, Width: scanWidth, Height: scanHeight,
	}
	b, err := json.Marshal(identity)
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:]), nil
}

func scanCheckpointFile(dir, key string) string {
	return filepath.Join(dir, key+".json")
}

func writeScanCheckpoint(path string, cp scanCheckpoint) error {
	if cp.Schema != checkpointSchema || strings.TrimSpace(cp.Key) == "" {
		return errors.New("checkpoint OCR không hợp lệ")
	}
	if cp.MediaSeconds < 0 || math.IsNaN(cp.MediaSeconds) || math.IsInf(cp.MediaSeconds, 0) {
		return errors.New("checkpoint OCR có thời gian không hợp lệ")
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	b, err := json.Marshal(cp)
	if err != nil {
		return err
	}
	tmp := path + ".tmp"
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	if _, err = f.Write(b); err == nil {
		err = f.Sync()
	}
	if closeErr := f.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		_ = os.Remove(tmp)
		return err
	}
	if err := os.Rename(tmp, path); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return nil
}

func readScanCheckpoint(path, key string) (scanCheckpoint, bool, error) {
	b, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return scanCheckpoint{}, false, nil
	}
	if err != nil {
		return scanCheckpoint{}, false, err
	}
	var cp scanCheckpoint
	if err := json.Unmarshal(b, &cp); err != nil {
		return scanCheckpoint{}, false, err
	}
	if cp.Schema != checkpointSchema || cp.Key != key {
		return scanCheckpoint{}, false, nil
	}
	if cp.MediaSeconds < 0 || math.IsNaN(cp.MediaSeconds) || math.IsInf(cp.MediaSeconds, 0) {
		return scanCheckpoint{}, false, errors.New("checkpoint OCR có thời gian không hợp lệ")
	}
	return cp, true, nil
}

const defaultCheckpointIntervalSeconds = 300.0

type scanCheckpointSession struct {
	key      string
	path     string
	interval float64
	nextAt   float64
}

func newScanCheckpointSession(dir string, interval float64, req ScanRequest) (*scanCheckpointSession, scanCheckpoint, bool, error) {
	dir = strings.TrimSpace(dir)
	if dir == "" {
		return nil, scanCheckpoint{}, false, nil
	}
	key, err := scanCheckpointKey(req)
	if err != nil {
		return nil, scanCheckpoint{}, false, err
	}
	if interval <= 0 || math.IsNaN(interval) || math.IsInf(interval, 0) {
		interval = defaultCheckpointIntervalSeconds
	}
	session := &scanCheckpointSession{key: key, path: scanCheckpointFile(dir, key), interval: interval, nextAt: interval}
	cp, ok, err := readScanCheckpoint(session.path, key)
	if err != nil {
		// A broken checkpoint must never block a scan. Remove it and restart from
		// zero; source/ROI/mode identity remains protected by the key.
		_ = os.Remove(session.path)
		return session, scanCheckpoint{}, false, nil
	}
	if ok {
		session.nextAt = (math.Floor(cp.MediaSeconds/interval) + 1) * interval
	}
	return session, cp, ok, nil
}

func (s *scanCheckpointSession) MaybeSave(media float64, tracker *subtitleTracker, frames, ocrCalls int) error {
	return s.MaybeSaveWithStats(media, tracker, frames, scanCheckpointStats{OCRImages: ocrCalls})
}

func (s *scanCheckpointSession) MaybeSaveWithStats(media float64, tracker *subtitleTracker, frames int, stats scanCheckpointStats) error {
	if s == nil || media < s.nextAt || tracker == nil || !tracker.CanCheckpoint() {
		return nil
	}
	if err := s.save(media, tracker, frames, stats); err != nil {
		return err
	}
	s.nextAt = (math.Floor(media/s.interval) + 1) * s.interval
	return nil
}

func (s *scanCheckpointSession) SaveNow(media float64, tracker *subtitleTracker, frames, ocrCalls int) error {
	return s.SaveNowWithStats(media, tracker, frames, scanCheckpointStats{OCRImages: ocrCalls})
}

func (s *scanCheckpointSession) SaveNowWithStats(media float64, tracker *subtitleTracker, frames int, stats scanCheckpointStats) error {
	if s == nil || media <= 0 || tracker == nil || !tracker.CanCheckpoint() {
		return nil
	}
	return s.save(media, tracker, frames, stats)
}

func (s *scanCheckpointSession) Remove() error {
	if s == nil {
		return nil
	}
	err := os.Remove(s.path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

func (s *scanCheckpointSession) save(media float64, tracker *subtitleTracker, frames int, stats scanCheckpointStats) error {
	if stats.OCRImages < 0 {
		stats.OCRImages = 0
	}
	return writeScanCheckpoint(s.path, scanCheckpoint{
		Schema: checkpointSchema, Key: s.key, MediaSeconds: media,
		Cues: tracker.Cues(), Active: tracker.Active(), Frames: frames, OCRCalls: stats.OCRImages, Stats: stats,
	})
}

type scanParallelLaneCheckpoint struct {
	ID        string              `json:"id"`
	Segment   scanSegment         `json:"segment"`
	Completed bool                `json:"completed"`
	Media     float64             `json:"media_seconds"`
	Cues      []Cue               `json:"cues"`
	Active    *Cue                `json:"active,omitempty"`
	Frames    int                 `json:"frames"`
	Stats     scanCheckpointStats `json:"stats,omitempty"`
}

type scanParallelCheckpoint struct {
	Schema               int                          `json:"schema"`
	Key                  string                       `json:"key"`
	RequestedParallelism string                       `json:"requested_parallelism"`
	SelectedParallelism  int                          `json:"selected_parallelism"`
	Duration             float64                      `json:"duration"`
	Overlap              float64                      `json:"overlap"`
	Lanes                []scanParallelLaneCheckpoint `json:"lanes"`
}

type parallelCheckpointSession struct {
	key  string
	path string
}

func scanParallelCheckpointKey(req ScanRequest) (string, error) {
	return scanCheckpointKeyForSchema(req, parallelCheckpointSchema)
}

func newParallelCheckpointSession(dir string, req ScanRequest) (*parallelCheckpointSession, scanParallelCheckpoint, bool, error) {
	dir = strings.TrimSpace(dir)
	if dir == "" {
		return nil, scanParallelCheckpoint{}, false, nil
	}
	key, err := scanParallelCheckpointKey(req)
	if err != nil {
		return nil, scanParallelCheckpoint{}, false, err
	}
	s := &parallelCheckpointSession{key: key, path: scanCheckpointFile(dir, key)}
	cp, ok, err := readParallelScanCheckpoint(s.path, key)
	if err != nil {
		_ = os.Remove(s.path)
		return s, scanParallelCheckpoint{}, false, nil
	}
	return s, cp, ok, nil
}

func (s *parallelCheckpointSession) Save(cp scanParallelCheckpoint) error {
	if s == nil {
		return nil
	}
	cp.Schema = parallelCheckpointSchema
	cp.Key = s.key
	return writeParallelScanCheckpoint(s.path, cp)
}

func (s *parallelCheckpointSession) Remove() error {
	if s == nil {
		return nil
	}
	err := os.Remove(s.path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

func writeParallelScanCheckpoint(path string, cp scanParallelCheckpoint) error {
	if cp.Schema != parallelCheckpointSchema || strings.TrimSpace(cp.Key) == "" || cp.SelectedParallelism < 1 || len(cp.Lanes) != cp.SelectedParallelism {
		return errors.New("checkpoint OCR song song không hợp lệ")
	}
	if cp.Duration <= 0 || math.IsNaN(cp.Duration) || math.IsInf(cp.Duration, 0) {
		return errors.New("checkpoint OCR song song có thời lượng không hợp lệ")
	}
	for i, lane := range cp.Lanes {
		if lane.ID == "" || lane.Segment.Index != i || lane.Media < 0 || math.IsNaN(lane.Media) || math.IsInf(lane.Media, 0) {
			return errors.New("checkpoint OCR song song có lane không hợp lệ")
		}
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	b, err := json.Marshal(cp)
	if err != nil {
		return err
	}
	tmp := path + ".tmp"
	f, err := os.OpenFile(tmp, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	if _, err = f.Write(b); err == nil {
		err = f.Sync()
	}
	if closeErr := f.Close(); err == nil {
		err = closeErr
	}
	if err != nil {
		_ = os.Remove(tmp)
		return err
	}
	if err := os.Rename(tmp, path); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return nil
}

func readParallelScanCheckpoint(path, key string) (scanParallelCheckpoint, bool, error) {
	b, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return scanParallelCheckpoint{}, false, nil
	}
	if err != nil {
		return scanParallelCheckpoint{}, false, err
	}
	var cp scanParallelCheckpoint
	if err := json.Unmarshal(b, &cp); err != nil {
		return scanParallelCheckpoint{}, false, err
	}
	if cp.Schema != parallelCheckpointSchema || cp.Key != key {
		return scanParallelCheckpoint{}, false, nil
	}
	if cp.SelectedParallelism < 1 || len(cp.Lanes) != cp.SelectedParallelism || cp.Duration <= 0 {
		return scanParallelCheckpoint{}, false, errors.New("checkpoint OCR song song bị hỏng")
	}
	for i, lane := range cp.Lanes {
		if lane.ID == "" || lane.Segment.Index != i || lane.Media < 0 || lane.Media > cp.Duration+cp.Overlap+1 {
			return scanParallelCheckpoint{}, false, errors.New("checkpoint OCR song song có lane bị hỏng")
		}
	}
	return cp, true, nil
}

func laneCheckpointState(l scanParallelLaneCheckpoint) scanLaneState {
	return scanLaneState{MediaSeconds: l.Media, Cues: append([]Cue(nil), l.Cues...), Active: l.Active, Frames: l.Frames, Stats: l.Stats}
}

func updateLaneCheckpointState(l *scanParallelLaneCheckpoint, state scanLaneState) {
	if l == nil {
		return
	}
	l.Media = state.MediaSeconds
	l.Cues = append([]Cue(nil), state.Cues...)
	if state.Active != nil {
		active := *state.Active
		l.Active = &active
	} else {
		l.Active = nil
	}
	l.Frames = state.Frames
	l.Stats = state.Stats
}

func parallelCheckpointInfo(cp scanParallelCheckpoint) CheckpointInfo {
	progress := make([]scanLaneProgress, len(cp.Lanes))
	completed := make([]bool, len(cp.Lanes))
	segments := make([]scanSegment, len(cp.Lanes))
	laneCues := make([][]Cue, len(cp.Lanes))
	info := CheckpointInfo{Exists: true, Schema: parallelCheckpointSchema, ParallelismSelected: cp.SelectedParallelism, ActiveLanes: 0, TotalLanes: len(cp.Lanes)}
	unique := 0.0
	for i, lane := range cp.Lanes {
		segments[i] = lane.Segment
		completed[i] = lane.Completed
		if lane.Completed {
			info.CompletedLanes++
		}
		progress[i] = scanLaneProgress{
			MediaSeconds: lane.Media, Cues: lane.Cues, Active: lane.Active, Frames: lane.Frames,
			OCRImages: lane.Stats.OCRImages, OCRCalls: lane.Stats.OCRBatchCalls, VisualSkips: lane.Stats.VisualSkips,
			VisualConfirmations: lane.Stats.VisualConfirmations, OCRRetries: lane.Stats.OCRRetries,
			FramePipelineSeconds: lane.Stats.FramePipelineSeconds, VisualSeconds: lane.Stats.VisualSeconds,
			EncodeSeconds: lane.Stats.EncodeSeconds, OCRSeconds: lane.Stats.OCRSeconds,
		}
		coreMedia := math.Min(lane.Segment.CoreEnd, math.Max(lane.Segment.CoreStart, lane.Media))
		if lane.Completed {
			coreMedia = lane.Segment.CoreEnd
		}
		unique += math.Max(0, coreMedia-lane.Segment.CoreStart)
		cues := append([]Cue(nil), lane.Cues...)
		if lane.Active != nil {
			cues = append(cues, *lane.Active)
		}
		laneCues[i] = cues
		info.Frames += lane.Frames
		info.OCRCalls += lane.Stats.OCRImages
		info.OCRBatchCalls += lane.Stats.OCRBatchCalls
		info.VisualSkips += lane.Stats.VisualSkips
		info.VisualConfirmations += lane.Stats.VisualConfirmations
		info.OCRRetries += lane.Stats.OCRRetries
		info.FramePipelineSeconds += lane.Stats.FramePipelineSeconds
		info.VisualSeconds += lane.Stats.VisualSeconds
		info.EncodeSeconds += lane.Stats.EncodeSeconds
		info.OCRSeconds += lane.Stats.OCRSeconds
	}
	info.MediaSeconds = contiguousCompletedFrontier(progress, completed, segments)
	if cp.Duration > 0 {
		info.ProgressPercent = math.Min(100, math.Max(0, unique/cp.Duration*100))
	}
	cues, boundaryMerges := reconcileSegmentCues(laneCues, segments, cp.Duration)
	info.BoundaryMerges = boundaryMerges
	info.CueCount = len(cues)
	info.RecentCues = recentCuesAtOrBefore(cues, info.MediaSeconds, 120)
	return info
}
