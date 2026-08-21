package application

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"bilisubstudio/internal/appstate"
	"bilisubstudio/internal/jobs"
	"bilisubstudio/internal/mediapreview"
	"bilisubstudio/internal/ocr"
	"bilisubstudio/internal/subtitle"
	"bilisubstudio/internal/tools"
	"bilisubstudio/internal/video"
	"bilisubstudio/internal/videoedit"
)

type App struct {
	State    *appstate.State
	Jobs     *jobs.Manager
	Tools    *tools.Manager
	OCR      *ocr.Manager
	Video    *video.Service
	Subtitle *subtitle.Service
	Editor   *videoedit.Service

	toolMu     sync.Mutex
	cookieMu   sync.Mutex
	cookieAt   time.Time
	cookieOK   bool
	cookieUser string
	cookieErr  string
}

type Status struct {
	Version     string
	Root        string
	Drive       string
	Config      appstate.Config
	Storage     map[string]int64
	CookieSaved bool
	CookieValid bool
	CookieUser  string
	CookieError string
	OCR         ocr.Status
	YTDLPReady  bool
	FFmpegReady bool
	ActiveJob   bool
}

type OCRCue struct {
	Start float64 `json:"start"`
	End   float64 `json:"end"`
	Text  string  `json:"text"`
	Conf  float64 `json:"conf"`
}

type OCRFrameRequest struct {
	Path   string
	Time   float64
	Region ocr.ScanRegion
	Device string
}

func New(st *appstate.State) *App {
	jm := jobs.NewManager()
	tm := tools.New(st.Paths.Tools)
	om := ocr.New(st.Paths.OCR)
	_ = om.ConfigureDevice(st.SnapshotConfig().OCRDevice)
	resolver := &video.YTDLPResolver{}
	vs := &video.Service{Resolver: resolver, WorkRoot: filepath.Join(st.Paths.Cache, "video")}
	ss := &subtitle.Service{Resolver: resolver, CookieFile: st.WriteNetscapeCookieFile, CookieRaw: st.CookieValue}
	return &App{State: st, Jobs: jm, Tools: tm, OCR: om, Video: vs, Subtitle: ss, Editor: &videoedit.Service{}}
}

// PrepareShutdown preserves every active pausable OCR job before process exit.
// It only cancels remaining jobs after all OCR pause handshakes have reached
// their fsynced safe checkpoint; a pause failure aborts shutdown to avoid data
// loss.
func (a *App) PrepareShutdown(ctx context.Context) error {
	if a == nil {
		return nil
	}
	if a.Jobs == nil {
		a.Shutdown()
		return nil
	}
	for _, snap := range a.Jobs.ActiveSnapshots() {
		if !snap.PauseSupported {
			continue
		}
		if _, err := a.PauseJob(ctx, snap.ID); err != nil {
			return fmt.Errorf("tạm dừng OCR trước khi đóng: %w", err)
		}
	}
	a.Shutdown()
	return nil
}

func (a *App) Shutdown() {
	if a == nil {
		return
	}
	if a.Jobs != nil {
		a.Jobs.CancelAll()
	}
	if a.OCR != nil {
		_ = a.OCR.Stop()
	}
}

func (a *App) Status(ctx context.Context, validateCookie bool) Status {
	cfg := a.State.SnapshotConfig()
	gpuCtx, cancel := context.WithTimeout(ctx, 2*time.Second)
	_ = a.OCR.RefreshCapabilities(gpuCtx)
	cancel()
	cookieSaved := a.State.CookieValue() != ""
	cookieOK, cookieUser, cookieErr := false, "", ""
	if cookieSaved && validateCookie {
		cookieCtx, cookieCancel := context.WithTimeout(ctx, 6*time.Second)
		cookieOK, cookieUser, cookieErr = a.cookieStatus(cookieCtx, false)
		cookieCancel()
	}
	drive := filepath.VolumeName(a.State.Paths.Root)
	if drive == "" {
		drive = a.State.Paths.Root
	}
	return Status{
		Version: a.State.Version, Root: a.State.Paths.Root, Drive: drive, Config: cfg,
		Storage: map[string]int64{
			"data": appstate.DirSize(a.State.Paths.Data), "tools": appstate.DirSize(a.State.Paths.Tools),
			"ocr": appstate.DirSize(a.State.Paths.OCR), "temp": appstate.DirSize(a.State.Paths.Temp), "cache": appstate.DirSize(a.State.Paths.Cache),
		},
		CookieSaved: cookieSaved, CookieValid: cookieOK, CookieUser: cookieUser, CookieError: cookieErr,
		OCR: a.OCR.Status(), YTDLPReady: a.Tools.FindYTDLP() != "", FFmpegReady: a.Tools.FindFFmpeg() != "", ActiveJob: a.Jobs.Active(),
	}
}

func (a *App) ensureTools(ctx context.Context, needFFmpeg bool) error {
	a.toolMu.Lock()
	defer a.toolMu.Unlock()
	ytdlp, err := a.Tools.EnsureYTDLP(ctx)
	if err != nil {
		return err
	}
	a.Video.Resolver.Path = ytdlp
	cookieFile, err := a.State.WriteNetscapeCookieFile()
	if err != nil {
		return err
	}
	a.Video.CookieFile = cookieFile
	if needFFmpeg {
		ff, err := a.Tools.EnsureFFmpeg(ctx)
		if err != nil {
			return err
		}
		a.Video.FFmpeg = ff
		a.Editor.FFmpeg = ff
	}
	return nil
}

func (a *App) EnsureFFmpeg(ctx context.Context) (string, error) {
	a.toolMu.Lock()
	defer a.toolMu.Unlock()
	return a.Tools.EnsureFFmpeg(ctx)
}

func (a *App) EnsureFFprobe(ctx context.Context) (string, error) {
	a.toolMu.Lock()
	defer a.toolMu.Unlock()
	return a.Tools.EnsureFFprobe(ctx)
}

func (a *App) Metadata(ctx context.Context, rawURL string) (video.Metadata, error) {
	if err := a.ensureTools(ctx, false); err != nil {
		return video.Metadata{}, err
	}
	cookieFile, _ := a.State.WriteNetscapeCookieFile()
	return a.Video.Resolver.Metadata(ctx, rawURL, cookieFile)
}

func (a *App) StartVideo(req video.JobRequest) string {
	id := newJobID("video")
	job := jobs.New(id)
	a.Jobs.Add(job)
	go func() {
		ctx := job.Context()
		if err := a.ensureTools(ctx, true); err != nil {
			job.Finish(err, err.Error())
			return
		}
		err := a.Video.Run(ctx, job, req)
		if job.Snapshot(0).Status == "cancelled" {
			return
		}
		if err != nil {
			if errors.Is(err, context.Canceled) {
				job.Cancel()
				return
			}
			job.Finish(err, err.Error())
			return
		}
		job.Finish(nil, job.Snapshot(0).Message)
	}()
	return id
}

func (a *App) StartSubtitle(req subtitle.Request) string {
	id := newJobID("sub")
	job := jobs.New(id)
	a.Jobs.Add(job)
	go func() {
		ctx := job.Context()
		if err := a.ensureTools(ctx, false); err != nil {
			job.Finish(err, err.Error())
			return
		}
		err := a.Subtitle.Run(ctx, job, req)
		if job.Snapshot(0).Status == "cancelled" {
			return
		}
		if err != nil {
			job.Finish(err, err.Error())
			return
		}
		job.Finish(nil, job.Snapshot(0).Message)
	}()
	return id
}

func (a *App) StartEditor(req videoedit.Request) string {
	if strings.TrimSpace(req.OutputDir) == "" {
		req.OutputDir = a.State.SnapshotConfig().OutputDir
	}
	id := newJobID("editor")
	job := jobs.New(id)
	a.Jobs.Add(job)
	go func() {
		ctx := job.Context()
		job.Set("tools", .5, "Đang kiểm tra FFmpeg...")
		ff, err := a.EnsureFFmpeg(ctx)
		if err != nil {
			job.Finish(err, err.Error())
			return
		}
		a.Editor.FFmpeg = ff
		path, err := a.Editor.Run(ctx, job, req)
		if job.Snapshot(0).Status == "cancelled" {
			return
		}
		if err != nil {
			if errors.Is(err, context.Canceled) {
				job.Cancel()
				return
			}
			job.Finish(err, err.Error())
			return
		}
		job.Logf("Đã xuất video: %s", path)
		job.Finish(nil, "Đã xuất: "+path)
	}()
	return id
}

func (a *App) JobSnapshot(id string, after int) (jobs.Snapshot, bool) {
	j, ok := a.Jobs.Get(id)
	if !ok {
		return jobs.Snapshot{}, false
	}
	return j.Snapshot(after), true
}

func (a *App) CancelJob(id string) error {
	j, ok := a.Jobs.Get(id)
	if !ok {
		return errors.New("job không tồn tại")
	}
	j.Cancel()
	return nil
}

func (a *App) PauseJob(ctx context.Context, id string) (jobs.Snapshot, error) {
	j, ok := a.Jobs.Get(id)
	if !ok {
		return jobs.Snapshot{}, errors.New("job không tồn tại")
	}
	done, err := j.RequestPause()
	if err != nil {
		return jobs.Snapshot{}, err
	}
	timer := time.NewTimer(90 * time.Second)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return jobs.Snapshot{}, ctx.Err()
	case <-timer.C:
		return jobs.Snapshot{}, errors.New("OCR chưa đạt checkpoint an toàn trong 90 giây")
	case <-done:
		snap := j.Snapshot(0)
		if snap.Status != "paused" {
			return snap, errors.New(snap.Message)
		}
		return snap, nil
	}
}

func (a *App) ConfigureOCRDevice(mode string) error {
	if err := a.OCR.ConfigureDevice(mode); err != nil {
		return err
	}
	normalized := a.OCR.Status().DeviceMode
	return a.State.UpdateConfig(func(c *appstate.Config) { c.OCRDevice = normalized })
}

func (a *App) EnsureOCR(device string) error {
	if strings.TrimSpace(device) != "" {
		if err := a.ConfigureOCRDevice(device); err != nil {
			return err
		}
	}
	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Minute)
		defer cancel()
		_ = a.OCR.Ensure(ctx)
	}()
	return nil
}

func (a *App) OCRStatus(ctx context.Context) ocr.Status {
	probe, cancel := context.WithTimeout(ctx, 2*time.Second)
	_ = a.OCR.RefreshCapabilities(probe)
	cancel()
	return a.OCR.Status()
}

func (a *App) RemoveOCR() error { return a.OCR.Remove() }

func (a *App) OCRFrame(ctx context.Context, req OCRFrameRequest) (ocr.Result, error) {
	if strings.TrimSpace(req.Device) != "" {
		if err := a.ConfigureOCRDevice(req.Device); err != nil {
			return ocr.Result{}, err
		}
	}
	ff, err := a.EnsureFFmpeg(ctx)
	if err != nil {
		return ocr.Result{}, err
	}
	image, err := ocr.CaptureFramePNGBase64(ctx, ff, req.Path, req.Time, req.Region, false)
	if err != nil {
		return ocr.Result{}, err
	}
	res, err := a.OCR.Run(ctx, image)
	if err != nil {
		return ocr.Result{}, err
	}
	if res.OK && res.Confidence < .68 {
		enhanced, capErr := ocr.CaptureFramePNGBase64(ctx, ff, req.Path, req.Time, req.Region, true)
		if capErr == nil {
			if alt, runErr := a.OCR.Run(ctx, enhanced); runErr == nil && alt.OK && alt.Confidence > res.Confidence {
				res = alt
			}
		}
	}
	return res, nil
}

func (a *App) InspectOCRCheckpoint(req ocr.ScanRequest) (ocr.CheckpointInfo, error) {
	return ocr.InspectCheckpoint(filepath.Join(a.State.Paths.Data, "OCRCheckpoints"), req)
}

func (a *App) RemoveOCRCheckpoint(req ocr.ScanRequest) error {
	return ocr.RemoveCheckpoint(filepath.Join(a.State.Paths.Data, "OCRCheckpoints"), req)
}

func (a *App) StartOCRScan(req ocr.ScanRequest) (string, error) {
	if strings.TrimSpace(req.Path) == "" {
		return "", errors.New("thiếu video nguồn")
	}
	if strings.TrimSpace(req.Device) != "" {
		if err := a.ConfigureOCRDevice(req.Device); err != nil {
			return "", err
		}
	}
	id := newJobID("ocrscan")
	job := jobs.NewPausable(id)
	a.Jobs.Add(job)
	go func() {
		ctx := job.Context()
		job.Set("tools", .5, "Đang kiểm tra FFmpeg và OCR Engine...")
		ff, err := a.EnsureFFmpeg(ctx)
		if err != nil {
			job.Finish(err, err.Error())
			return
		}
		if err := a.OCR.Ensure(ctx); err != nil {
			job.Finish(err, err.Error())
			return
		}
		scanner := &ocr.Scanner{FFmpeg: ff, Engine: a.OCR, CheckpointDir: filepath.Join(a.State.Paths.Data, "OCRCheckpoints")}
		result, err := scanner.Run(ctx, job, req)
		if job.Snapshot(0).Status == "cancelled" {
			return
		}
		if errors.Is(err, ocr.ErrScanPaused) {
			job.SetResult(result)
			job.PauseComplete(fmt.Sprintf("Đã tạm dừng an toàn tại %s.", formatMediaClock(result.MediaSeconds)))
			return
		}
		if err != nil {
			if errors.Is(err, context.Canceled) {
				job.Cancel()
				return
			}
			job.Finish(err, err.Error())
			return
		}
		job.SetResult(result)
		job.Finish(nil, fmt.Sprintf("Đã quét xong: %d câu · %.1f× realtime", len(result.Cues), result.RealtimeSpeed))
	}()
	return id, nil
}

func (a *App) ExportOCR(cues []OCRCue, outDir, fileName string) (string, int, error) {
	outDir = strings.TrimSpace(outDir)
	if outDir == "" {
		outDir = a.State.SnapshotConfig().OutputDir
	}
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return "", 0, err
	}
	name := safeFile(fileName)
	if !strings.HasSuffix(strings.ToLower(name), ".srt") {
		name += ".srt"
	}
	path := uniquePath(filepath.Join(outDir, name))
	var b strings.Builder
	count := 0
	for _, c := range cues {
		text, ok := ocr.NormalizeChineseSubtitleText(c.Text)
		if !ok {
			continue
		}
		count++
		fmt.Fprintf(&b, "%d\n%s --> %s\n%s\n\n", count, srtTime(c.Start), srtTime(c.End), text)
	}
	if err := os.WriteFile(path, []byte(b.String()), 0o644); err != nil {
		return "", 0, err
	}
	return path, count, nil
}

func (a *App) PreviewInfo(ctx context.Context, path string) (mediapreview.PreviewInfo, error) {
	probe, err := a.EnsureFFprobe(ctx)
	if err != nil {
		return mediapreview.PreviewInfo{}, err
	}
	return mediapreview.ProbePreview(ctx, probe, path)
}

func (a *App) PreviewFrame(ctx context.Context, path string, at float64) ([]byte, error) {
	ff, err := a.EnsureFFmpeg(ctx)
	if err != nil {
		return nil, err
	}
	return mediapreview.PreviewFrameJPEG(ctx, ff, path, at)
}

func (a *App) CleanupStorage() error {
	if a.Jobs.Active() {
		return errors.New("đang có tác vụ; không thể dọn cache")
	}
	_ = os.RemoveAll(a.State.Paths.Temp)
	_ = os.RemoveAll(a.State.Paths.Cache)
	if err := os.MkdirAll(a.State.Paths.Temp, 0o755); err != nil {
		return err
	}
	return os.MkdirAll(a.State.Paths.Cache, 0o755)
}

func (a *App) ResetTools() error {
	if a.Jobs.Active() {
		return errors.New("đang có tác vụ; không thể xóa Tools")
	}
	_ = a.OCR.Stop()
	if err := os.RemoveAll(a.State.Paths.Tools); err != nil {
		return err
	}
	return os.MkdirAll(a.State.Paths.Tools, 0o755)
}

func (a *App) SetOutputDir(path string) error {
	path = strings.TrimSpace(path)
	if path == "" {
		return errors.New("thư mục xuất rỗng")
	}
	if err := os.MkdirAll(path, 0o755); err != nil {
		return err
	}
	return a.State.UpdateConfig(func(c *appstate.Config) { c.OutputDir = path })
}

func (a *App) SetTheme(theme string) error {
	theme = strings.ToLower(strings.TrimSpace(theme))
	if theme != "dark" && theme != "light" {
		return errors.New("theme phải là dark hoặc light")
	}
	return a.State.UpdateConfig(func(c *appstate.Config) { c.Theme = theme })
}

func (a *App) SetUpdateCheck(enabled bool) error {
	return a.State.UpdateConfig(func(c *appstate.Config) { c.CheckUpdates = enabled })
}

func newJobID(prefix string) string { return fmt.Sprintf("%s-%d", prefix, time.Now().UnixNano()) }

func formatMediaClock(seconds float64) string {
	if seconds < 0 {
		seconds = 0
	}
	total := int(seconds + .5)
	h := total / 3600
	m := (total % 3600) / 60
	s := total % 60
	if h > 0 {
		return fmt.Sprintf("%02d:%02d:%02d", h, m, s)
	}
	return fmt.Sprintf("%02d:%02d", m, s)
}

func safeFile(s string) string {
	s = strings.TrimSpace(s)
	if s == "" {
		return "BiliSub_OCR_Chinese"
	}
	r := strings.NewReplacer("<", "_", ">", "_", ":", "_", "\"", "_", "/", "_", "\\", "_", "|", "_", "?", "_", "*", "_")
	return strings.TrimSpace(r.Replace(s))
}

func uniquePath(p string) string {
	if _, err := os.Stat(p); os.IsNotExist(err) {
		return p
	}
	ext := filepath.Ext(p)
	base := strings.TrimSuffix(p, ext)
	for i := 2; ; i++ {
		candidate := fmt.Sprintf("%s (%d)%s", base, i, ext)
		if _, err := os.Stat(candidate); os.IsNotExist(err) {
			return candidate
		}
	}
}

func srtTime(sec float64) string {
	if sec < 0 {
		sec = 0
	}
	ms := int64(sec*1000 + .5)
	h := ms / 3600000
	ms %= 3600000
	m := ms / 60000
	ms %= 60000
	s := ms / 1000
	ms %= 1000
	return fmt.Sprintf("%02d:%02d:%02d,%03d", h, m, s, ms)
}
