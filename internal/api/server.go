package api

import (
	"context"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"bilisubstudio/internal/appstate"
	"bilisubstudio/internal/jobs"
	"bilisubstudio/internal/mediapreview"
	"bilisubstudio/internal/ocr"
	"bilisubstudio/internal/proc"
	"bilisubstudio/internal/subtitle"
	"bilisubstudio/internal/tools"
	"bilisubstudio/internal/video"
	"bilisubstudio/internal/videoedit"
)

//go:embed web/*
var webFS embed.FS

const (
	stableManifestFileID = "1wpVgh6urUJYhX-b6nqAj3TOJ6wyaoB0C"
	betaManifestFileID   = "18gW_x8Y_jD-PMyk5kv7tXYF--qzsQDiT"
)

type Server struct {
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
	exitOnce   sync.Once
	Exit       func()
	CurrentEXE string
}

type updateManifest struct {
	Version     string   `json:"version"`
	DownloadURL string   `json:"download_url"`
	SHA256      string   `json:"sha256"`
	Size        int64    `json:"size"`
	SourceURL   string   `json:"source_url"`
	Notes       []string `json:"notes"`
}

func New(st *appstate.State) *Server {
	jm := jobs.NewManager()
	tm := tools.New(st.Paths.Tools)
	om := ocr.New(st.Paths.OCR)
	_ = om.ConfigureDevice(st.SnapshotConfig().OCRDevice)
	resolver := &video.YTDLPResolver{}
	vs := &video.Service{Resolver: resolver, WorkRoot: filepath.Join(st.Paths.Cache, "video")}
	ss := &subtitle.Service{
		Resolver:   resolver,
		CookieFile: st.WriteNetscapeCookieFile,
		CookieRaw:  st.CookieValue,
	}
	exe, _ := os.Executable()
	s := &Server{State: st, Jobs: jm, Tools: tm, OCR: om, Video: vs, Subtitle: ss, Editor: &videoedit.Service{}, CurrentEXE: exe}
	return s
}

func (s *Server) Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/", s.serveIndex)
	mux.HandleFunc("/app-icon.png", s.serveIcon)
	mux.HandleFunc("/favicon.ico", s.serveIcon)
	mux.HandleFunc("/manifest.webmanifest", s.serveManifest)
	mux.HandleFunc("/api/status", s.auth(s.statusHandler))
	mux.HandleFunc("/api/ping", s.auth(s.pingHandler))
	mux.HandleFunc("/api/cookie", s.auth(s.cookieHandler))
	mux.HandleFunc("/api/login/qr/start", s.auth(s.qrStartHandler))
	mux.HandleFunc("/api/login/qr/poll", s.auth(s.qrPollHandler))
	mux.HandleFunc("/api/metadata", s.auth(s.metadataHandler))
	mux.HandleFunc("/api/video/download", s.auth(s.videoDownloadHandler))
	mux.HandleFunc("/api/editor/export", s.auth(s.editorExportHandler))
	mux.HandleFunc("/api/pick-video", s.auth(s.pickVideoHandler))
	mux.HandleFunc("/api/media", s.auth(s.mediaHandler))
	mux.HandleFunc("/api/preview-info", s.auth(s.previewInfoHandler))
	mux.HandleFunc("/api/preview-frame", s.auth(s.previewFrameHandler))
	mux.HandleFunc("/api/subtitle/download", s.auth(s.subtitleDownloadHandler))
	mux.HandleFunc("/api/job", s.auth(s.jobHandler))
	mux.HandleFunc("/api/job/cancel", s.auth(s.cancelHandler))
	mux.HandleFunc("/api/job/pause", s.auth(s.pauseHandler))
	mux.HandleFunc("/api/ocr/engine/ensure", s.auth(s.ocrEnsureHandler))
	mux.HandleFunc("/api/ocr/engine/status", s.auth(s.ocrStatusHandler))
	mux.HandleFunc("/api/ocr/engine/remove", s.auth(s.ocrRemoveHandler))
	mux.HandleFunc("/api/ocr", s.auth(s.ocrHandler))
	mux.HandleFunc("/api/ocr/scan", s.auth(s.ocrScanHandler))
	mux.HandleFunc("/api/ocr/checkpoint", s.auth(s.ocrCheckpointHandler))
	mux.HandleFunc("/api/ocr/export", s.auth(s.ocrExportHandler))
	mux.HandleFunc("/api/storage/cleanup", s.auth(s.storageCleanupHandler))
	mux.HandleFunc("/api/tools/reset", s.auth(s.toolsResetHandler))
	mux.HandleFunc("/api/pick-folder", s.auth(s.pickFolderHandler))
	mux.HandleFunc("/api/open-folder", s.auth(s.openFolderHandler))
	mux.HandleFunc("/api/update/check", s.auth(s.updateCheckHandler))
	mux.HandleFunc("/api/update/setting", s.auth(s.updateSettingHandler))
	mux.HandleFunc("/api/theme", s.auth(s.themeHandler))
	mux.HandleFunc("/api/update/apply", s.auth(s.updateApplyHandler))
	mux.HandleFunc("/api/exit", s.auth(s.exitHandler))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		mux.ServeHTTP(w, r)
	})
}

func (s *Server) URLFor(addr string) string {
	return "http://" + addr + "/?token=" + url.QueryEscape(s.State.Token)
}

func (s *Server) Launch(addr string) error { return launchBrowser(s.URLFor(addr)) }

func (s *Server) requestExit() {
	s.exitOnce.Do(func() {
		if s.Exit != nil {
			s.Exit()
		}
	})
}

func (s *Server) auth(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if subtleToken(r.Header.Get("X-BiliSub-Token"), s.State.Token) || subtleToken(r.URL.Query().Get("token"), s.State.Token) {
			next(w, r)
			return
		}
		jsonError(w, http.StatusUnauthorized, "token không hợp lệ")
	}
}

func subtleToken(a, b string) bool {
	if a == "" || len(a) != len(b) {
		return false
	}
	var diff byte
	for i := range a {
		diff |= a[i] ^ b[i]
	}
	return diff == 0
}

func (s *Server) serveIndex(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/" {
		http.NotFound(w, r)
		return
	}
	b, err := webFS.ReadFile("web/index.html")
	if err != nil {
		http.Error(w, err.Error(), 500)
		return
	}
	b = []byte(strings.ReplaceAll(string(b), "__APP_TOKEN__", s.State.Token))
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = w.Write(b)
}
func (s *Server) serveIcon(w http.ResponseWriter, r *http.Request) {
	b, err := webFS.ReadFile("web/app-icon.png")
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "image/png")
	w.Header().Set("Cache-Control", "public,max-age=86400")
	_, _ = w.Write(b)
}
func (s *Server) serveManifest(w http.ResponseWriter, r *http.Request) {
	jsonWrite(w, 200, map[string]any{"name": "BiliSub Studio", "short_name": "BiliSub", "display": "standalone", "start_url": "/", "icons": []map[string]any{{"src": "/app-icon.png", "sizes": "512x512", "type": "image/png"}}})
}

func (s *Server) statusHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		jsonError(w, 405, "method")
		return
	}
	cfg := s.State.SnapshotConfig()
	drive := filepath.VolumeName(s.State.Paths.Root)
	if drive == "" {
		drive = s.State.Paths.Root
	}
	gpuCtx, gpuCancel := context.WithTimeout(r.Context(), 2*time.Second)
	_ = s.OCR.RefreshCapabilities(gpuCtx)
	gpuCancel()
	osr := s.OCR.Status()
	cookieSaved := s.State.CookieValue() != ""
	cookieOK, cookieUser, cookieErr := false, "", ""
	if cookieSaved {
		ctx, cancel := context.WithTimeout(r.Context(), 6*time.Second)
		cookieOK, cookieUser, cookieErr = s.cookieStatus(ctx, false)
		cancel()
	}
	jsonWrite(w, 200, map[string]any{
		"version": s.State.Version, "cookie_saved": cookieSaved, "cookie_valid": cookieOK, "cookie_user": cookieUser, "cookie_error": cookieErr, "drive": drive, "root": s.State.Paths.Root,
		"config":    cfg,
		"storage":   map[string]int64{"data": appstate.DirSize(s.State.Paths.Data), "tools": appstate.DirSize(s.State.Paths.Tools), "ocr": appstate.DirSize(s.State.Paths.OCR), "temp": appstate.DirSize(s.State.Paths.Temp), "cache": appstate.DirSize(s.State.Paths.Cache)},
		"ocr_ready": osr.Ready, "ocr_status": osr, "ytdlp_ready": s.Tools.FindYTDLP() != "", "ffmpeg_ready": s.Tools.FindFFmpeg() != "",
	})
}
func (s *Server) pingHandler(w http.ResponseWriter, r *http.Request) {
	jsonWrite(w, 200, map[string]bool{"ok": true})
}

func (s *Server) cookieHandler(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodPost:
		var in struct {
			Cookie string `json:"cookie"`
		}
		if !readJSON(w, r, &in) {
			return
		}
		if err := s.State.SetCookie(in.Cookie); err != nil {
			jsonError(w, 400, err.Error())
			return
		}
		s.invalidateCookieStatus()
		ctx, cancel := context.WithTimeout(r.Context(), 10*time.Second)
		ok, user, errMsg := s.cookieStatus(ctx, true)
		cancel()
		if !ok {
			if errMsg == "" {
				errMsg = "Cookie không hợp lệ hoặc đã hết hạn"
			}
			jsonError(w, 401, errMsg)
			return
		}
		jsonWrite(w, 200, map[string]any{"ok": true, "logged_in": true, "user": user})
	case http.MethodDelete:
		if err := s.State.DeleteCookie(); err != nil {
			jsonError(w, 500, err.Error())
			return
		}
		s.invalidateCookieStatus()
		jsonWrite(w, 200, map[string]bool{"ok": true})
	default:
		jsonError(w, 405, "method")
	}
}

func (s *Server) ensureTools(ctx context.Context, needFFmpeg bool) error {
	s.toolMu.Lock()
	defer s.toolMu.Unlock()
	ytdlp, err := s.Tools.EnsureYTDLP(ctx)
	if err != nil {
		return err
	}
	s.Video.Resolver.Path = ytdlp
	cookieFile, err := s.State.WriteNetscapeCookieFile()
	if err != nil {
		return err
	}
	s.Video.CookieFile = cookieFile
	if needFFmpeg {
		ff, err := s.Tools.EnsureFFmpeg(ctx)
		if err != nil {
			return err
		}
		s.Video.FFmpeg = ff
		s.Editor.FFmpeg = ff
	}
	return nil
}

func (s *Server) ensureFFmpeg(ctx context.Context) (string, error) {
	s.toolMu.Lock()
	defer s.toolMu.Unlock()
	return s.Tools.EnsureFFmpeg(ctx)
}

func (s *Server) ensureEditorFFmpeg(ctx context.Context) (string, error) {
	ff, err := s.ensureFFmpeg(ctx)
	if err != nil {
		return "", err
	}
	s.Editor.FFmpeg = ff
	return ff, nil
}

func (s *Server) ensureFFprobe(ctx context.Context) (string, error) {
	s.toolMu.Lock()
	defer s.toolMu.Unlock()
	return s.Tools.EnsureFFprobe(ctx)
}

func (s *Server) metadataHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in struct {
		URL     string `json:"url"`
		Purpose string `json:"purpose"`
	}
	if !readJSON(w, r, &in) {
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 90*time.Second)
	defer cancel()
	if err := s.ensureTools(ctx, false); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	cookieFile, _ := s.State.WriteNetscapeCookieFile()
	m, err := s.Video.Resolver.Metadata(ctx, in.URL, cookieFile)
	if err != nil {
		jsonError(w, 502, err.Error())
		return
	}
	jsonWrite(w, 200, m)
}

func (s *Server) videoDownloadHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in video.JobRequest
	if !readJSON(w, r, &in) {
		return
	}
	id := newJobID("video")
	job := jobs.New(id)
	s.Jobs.Add(job)
	go func() {
		ctx := job.Context()
		if err := s.ensureTools(ctx, true); err != nil {
			job.Finish(err, err.Error())
			return
		}
		err := s.Video.Run(ctx, job, in)
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
		msg := job.Snapshot(0).Message
		job.Finish(nil, msg)
	}()
	jsonWrite(w, 200, map[string]string{"job_id": id})
}

func (s *Server) subtitleDownloadHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in struct{ URL, Format, Track, OutputDir string }
	if !readJSON(w, r, &in) {
		return
	}
	id := newJobID("sub")
	job := jobs.New(id)
	s.Jobs.Add(job)
	go func() {
		ctx := job.Context()
		if err := s.ensureTools(ctx, false); err != nil {
			job.Finish(err, err.Error())
			return
		}
		err := s.Subtitle.Run(ctx, job, subtitle.Request{URL: in.URL, Format: in.Format, Track: in.Track, OutputDir: in.OutputDir})
		if job.Snapshot(0).Status == "cancelled" {
			return
		}
		if err != nil {
			job.Finish(err, err.Error())
			return
		}
		job.Finish(nil, job.Snapshot(0).Message)
	}()
	jsonWrite(w, 200, map[string]string{"job_id": id})
}

func (s *Server) editorExportHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in videoedit.Request
	if !readJSON(w, r, &in) {
		return
	}
	if strings.TrimSpace(in.OutputDir) == "" {
		in.OutputDir = s.State.SnapshotConfig().OutputDir
	}
	id := newJobID("editor")
	job := jobs.New(id)
	s.Jobs.Add(job)
	go func() {
		ctx := job.Context()
		job.Set("tools", 0.5, "Đang kiểm tra FFmpeg...")
		ff, err := s.ensureEditorFFmpeg(ctx)
		if err != nil {
			job.Finish(err, err.Error())
			return
		}
		s.Editor.FFmpeg = ff
		path, err := s.Editor.Run(ctx, job, in)
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
	jsonWrite(w, 200, map[string]string{"job_id": id})
}

func (s *Server) pickVideoHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in struct {
		Path string `json:"path"`
	}
	_ = decodeJSON(r, &in)
	initial := strings.TrimSpace(in.Path)
	if initial == "" {
		initial = s.State.SnapshotConfig().OutputDir
	}
	p, cancelled, err := pickVideoNative(initial)
	if err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, map[string]any{"path": p, "cancelled": cancelled})
}

func (s *Server) mediaHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		jsonError(w, 405, "method")
		return
	}
	p := strings.TrimSpace(r.URL.Query().Get("path"))
	if p == "" {
		jsonError(w, 400, "thiếu đường dẫn video")
		return
	}
	abs, err := filepath.Abs(p)
	if err != nil {
		jsonError(w, 400, "đường dẫn video không hợp lệ")
		return
	}
	st, err := os.Stat(abs)
	if err != nil || st.IsDir() || st.Size() == 0 {
		jsonError(w, 404, "không tìm thấy video")
		return
	}
	switch strings.ToLower(filepath.Ext(abs)) {
	case ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi":
	default:
		jsonError(w, 400, "định dạng preview chưa hỗ trợ")
		return
	}
	http.ServeFile(w, r, abs)
}

func (s *Server) previewInfoHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in struct {
		Path string `json:"path"`
	}
	if !readJSON(w, r, &in) {
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Minute)
	defer cancel()
	probe, err := s.ensureFFprobe(ctx)
	if err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	info, err := mediapreview.ProbePreview(ctx, probe, in.Path)
	if err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, info)
}

func (s *Server) previewFrameHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		jsonError(w, 405, "method")
		return
	}
	p := strings.TrimSpace(r.URL.Query().Get("path"))
	if p == "" {
		jsonError(w, 400, "thiếu đường dẫn video")
		return
	}
	at, _ := strconv.ParseFloat(r.URL.Query().Get("time"), 64)
	ctx, cancel := context.WithTimeout(r.Context(), 30*time.Second)
	defer cancel()
	ff, err := s.ensureFFmpeg(ctx)
	if err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	b, err := mediapreview.PreviewFrameJPEG(ctx, ff, p, at)
	if err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	w.Header().Set("Content-Type", "image/jpeg")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write(b)
}

func (s *Server) jobHandler(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	after, _ := strconv.Atoi(r.URL.Query().Get("after"))
	j, ok := s.Jobs.Get(id)
	if !ok {
		jsonError(w, 404, "job không tồn tại")
		return
	}
	jsonWrite(w, 200, j.Snapshot(after))
}
func (s *Server) cancelHandler(w http.ResponseWriter, r *http.Request) {
	id := r.URL.Query().Get("id")
	j, ok := s.Jobs.Get(id)
	if !ok {
		jsonError(w, 404, "job không tồn tại")
		return
	}
	j.Cancel()
	jsonWrite(w, 200, map[string]bool{"ok": true})
}

func (s *Server) pauseHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	id := r.URL.Query().Get("id")
	j, ok := s.Jobs.Get(id)
	if !ok {
		jsonError(w, 404, "job không tồn tại")
		return
	}
	done, err := j.RequestPause()
	if err != nil {
		jsonError(w, 409, err.Error())
		return
	}
	select {
	case <-r.Context().Done():
		jsonError(w, 408, "hết thời gian chờ checkpoint tạm dừng")
	case <-done:
		snap := j.Snapshot(0)
		if snap.Status != "paused" {
			jsonError(w, 409, snap.Message)
			return
		}
		jsonWrite(w, 200, snap)
	case <-time.After(90 * time.Second):
		jsonError(w, 408, "OCR chưa đạt checkpoint an toàn trong 90 giây")
	}
}

func (s *Server) ocrEnsureHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in struct {
		Device string `json:"device"`
	}
	if !readJSON(w, r, &in) {
		return
	}
	if strings.TrimSpace(in.Device) != "" {
		if err := s.configureOCRDevice(in.Device); err != nil {
			jsonError(w, 400, err.Error())
			return
		}
	}
	go func() {
		// First-time setup owns the private Python runtime, PaddlePaddle, PaddleOCR
		// and PP-OCRv6 models. Keep it detached from the short HTTP request but
		// bounded so a dead network cannot leave StateStarting forever.
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Minute)
		defer cancel()
		_ = s.OCR.Ensure(ctx)
	}()
	jsonWrite(w, 200, map[string]bool{"started": true})
}
func (s *Server) ocrStatusHandler(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Second)
	_ = s.OCR.RefreshCapabilities(ctx)
	cancel()
	jsonWrite(w, 200, s.OCR.Status())
}

func (s *Server) configureOCRDevice(mode string) error {
	if err := s.OCR.ConfigureDevice(mode); err != nil {
		return err
	}
	normalized := s.OCR.Status().DeviceMode
	return s.State.UpdateConfig(func(c *appstate.Config) { c.OCRDevice = normalized })
}

func (s *Server) ocrRemoveHandler(w http.ResponseWriter, r *http.Request) {
	if err := s.OCR.Remove(); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, map[string]bool{"ok": true})
}
func (s *Server) ocrHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in struct {
		ImageBase64 string         `json:"imageBase64"`
		Path        string         `json:"path"`
		Time        float64        `json:"time"`
		Region      ocr.ScanRegion `json:"region"`
		Device      string         `json:"device"`
	}
	if !readJSON(w, r, &in) {
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 2*time.Minute)
	defer cancel()
	if strings.TrimSpace(in.Device) != "" {
		if err := s.configureOCRDevice(in.Device); err != nil {
			jsonError(w, 400, err.Error())
			return
		}
	}

	imageBase64 := strings.TrimSpace(in.ImageBase64)
	if imageBase64 == "" && strings.TrimSpace(in.Path) != "" {
		ff, err := s.ensureFFmpeg(ctx)
		if err != nil {
			jsonError(w, 500, err.Error())
			return
		}
		imageBase64, err = ocr.CaptureFramePNGBase64(ctx, ff, in.Path, in.Time, in.Region, false)
		if err != nil {
			jsonError(w, 500, err.Error())
			return
		}
		res, err := s.OCR.Run(ctx, imageBase64)
		if err != nil {
			jsonError(w, 500, err.Error())
			return
		}
		if res.OK && res.Confidence < 0.68 {
			enhanced, capErr := ocr.CaptureFramePNGBase64(ctx, ff, in.Path, in.Time, in.Region, true)
			if capErr == nil {
				if alt, runErr := s.OCR.Run(ctx, enhanced); runErr == nil && alt.OK && alt.Confidence > res.Confidence {
					res = alt
				}
			}
		}
		jsonWrite(w, 200, res)
		return
	}
	if imageBase64 == "" {
		jsonError(w, 400, "thiếu khung hình OCR")
		return
	}
	res, err := s.OCR.Run(ctx, imageBase64)
	if err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, res)
}

func (s *Server) ocrCheckpointHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost && r.Method != http.MethodDelete {
		jsonError(w, 405, "method")
		return
	}
	var in ocr.ScanRequest
	if !readJSON(w, r, &in) {
		return
	}
	dir := filepath.Join(s.State.Paths.Data, "OCRCheckpoints")
	if r.Method == http.MethodDelete {
		if err := ocr.RemoveCheckpoint(dir, in); err != nil {
			jsonError(w, 500, err.Error())
			return
		}
		jsonWrite(w, 200, map[string]bool{"ok": true})
		return
	}
	info, err := ocr.InspectCheckpoint(dir, in)
	if err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, info)
}

func (s *Server) ocrScanHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in ocr.ScanRequest
	if !readJSON(w, r, &in) {
		return
	}
	if strings.TrimSpace(in.Path) == "" {
		jsonError(w, 400, "thiếu video nguồn")
		return
	}
	if strings.TrimSpace(in.Device) != "" {
		if err := s.configureOCRDevice(in.Device); err != nil {
			jsonError(w, 400, err.Error())
			return
		}
	}
	id := newJobID("ocrscan")
	job := jobs.NewPausable(id)
	s.Jobs.Add(job)
	go func() {
		ctx := job.Context()
		job.Set("tools", 0.5, "Đang kiểm tra FFmpeg và OCR Engine...")
		ff, err := s.ensureFFmpeg(ctx)
		if err != nil {
			job.Finish(err, err.Error())
			return
		}
		if err := s.OCR.Ensure(ctx); err != nil {
			job.Finish(err, err.Error())
			return
		}
		scanner := s.newOCRScanner(ff)
		result, err := scanner.Run(ctx, job, in)
		if job.Snapshot(0).Status == "cancelled" {
			return
		}
		if errors.Is(err, ocr.ErrScanPaused) {
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
	jsonWrite(w, 200, map[string]string{"job_id": id})
}

func formatMediaClock(seconds float64) string {
	if seconds < 0 {
		seconds = 0
	}
	total := int(seconds + 0.5)
	h := total / 3600
	m := (total % 3600) / 60
	sec := total % 60
	if h > 0 {
		return fmt.Sprintf("%02d:%02d:%02d", h, m, sec)
	}
	return fmt.Sprintf("%02d:%02d", m, sec)
}

func (s *Server) newOCRScanner(ffmpeg string) *ocr.Scanner {
	return &ocr.Scanner{
		FFmpeg: ffmpeg, Engine: s.OCR,
		CheckpointDir: filepath.Join(s.State.Paths.Data, "OCRCheckpoints"),
	}
}

type ocrCue struct {
	Start float64 `json:"start"`
	End   float64 `json:"end"`
	Text  string  `json:"text"`
	Conf  float64 `json:"conf"`
}

func (s *Server) ocrExportHandler(w http.ResponseWriter, r *http.Request) {
	var in struct {
		Cues      []ocrCue `json:"cues"`
		OutputDir string   `json:"outputDir"`
		FileName  string   `json:"fileName"`
	}
	if !readJSON(w, r, &in) {
		return
	}
	outDir := strings.TrimSpace(in.OutputDir)
	if outDir == "" {
		outDir = s.State.SnapshotConfig().OutputDir
	}
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	name := safeFile(in.FileName)
	if !strings.HasSuffix(strings.ToLower(name), ".srt") {
		name += ".srt"
	}
	path := uniquePath(filepath.Join(outDir, name))
	var b strings.Builder
	count := 0
	for _, c := range in.Cues {
		txt, ok := ocr.NormalizeChineseSubtitleText(c.Text)
		if !ok {
			continue
		}
		count++
		fmt.Fprintf(&b, "%d\n%s --> %s\n%s\n\n", count, srtTime(c.Start), srtTime(c.End), txt)
	}
	if err := os.WriteFile(path, []byte(b.String()), 0o644); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, map[string]any{"path": path, "count": count})
}

func (s *Server) storageCleanupHandler(w http.ResponseWriter, r *http.Request) {
	if s.Jobs.Active() {
		jsonError(w, 409, "Đang có tác vụ tải; hãy hoàn tất hoặc hủy trước khi dọn cache")
		return
	}
	_ = os.RemoveAll(s.State.Paths.Temp)
	_ = os.RemoveAll(s.State.Paths.Cache)
	_ = os.MkdirAll(s.State.Paths.Temp, 0o755)
	_ = os.MkdirAll(s.State.Paths.Cache, 0o755)
	jsonWrite(w, 200, map[string]any{"ok": true, "locked": 0})
}
func (s *Server) toolsResetHandler(w http.ResponseWriter, r *http.Request) {
	if s.Jobs.Active() {
		jsonError(w, 409, "Đang có tác vụ tải; không thể xóa Tools")
		return
	}
	_ = s.OCR.Stop()
	if err := os.RemoveAll(s.State.Paths.Tools); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	_ = os.MkdirAll(s.State.Paths.Tools, 0o755)
	jsonWrite(w, 200, map[string]bool{"ok": true})
}

func (s *Server) pickFolderHandler(w http.ResponseWriter, r *http.Request) {
	var in struct {
		Path string `json:"path"`
	}
	_ = decodeJSON(r, &in)
	if in.Path == "" {
		in.Path = s.State.SnapshotConfig().OutputDir
	}
	p, cancelled, err := pickFolderNative(in.Path)
	if err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	if !cancelled && p != "" {
		_ = s.State.UpdateConfig(func(c *appstate.Config) { c.OutputDir = p })
	}
	jsonWrite(w, 200, map[string]any{"path": p, "cancelled": cancelled})
}
func (s *Server) openFolderHandler(w http.ResponseWriter, r *http.Request) {
	var in struct {
		Path string `json:"path"`
	}
	_ = decodeJSON(r, &in)
	p := strings.TrimSpace(in.Path)
	if p == "" {
		p = s.State.SnapshotConfig().OutputDir
	}
	if err := os.MkdirAll(p, 0o755); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	if err := openFolderNative(p); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, map[string]string{"path": p})
}

func (s *Server) updateCheckHandler(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()
	m, err := fetchManifest(ctx, s.State.Version)
	if err != nil {
		jsonError(w, 502, err.Error())
		return
	}
	jsonWrite(w, 200, map[string]any{"current": s.State.Version, "latest": m.Version, "available": versionLess(s.State.Version, m.Version), "notes": m.Notes})
}
func (s *Server) themeHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		jsonError(w, 405, "method")
		return
	}
	var in struct {
		Theme string `json:"theme"`
	}
	if !readJSON(w, r, &in) {
		return
	}
	in.Theme = strings.ToLower(strings.TrimSpace(in.Theme))
	if in.Theme != "dark" && in.Theme != "light" {
		jsonError(w, 400, "theme phải là dark hoặc light")
		return
	}
	if err := s.State.UpdateConfig(func(c *appstate.Config) { c.Theme = in.Theme }); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, map[string]any{"ok": true, "theme": in.Theme})
}
func (s *Server) updateSettingHandler(w http.ResponseWriter, r *http.Request) {
	var in struct {
		Check bool `json:"check"`
	}
	if !readJSON(w, r, &in) {
		return
	}
	if err := s.State.UpdateConfig(func(c *appstate.Config) { c.CheckUpdates = in.Check }); err != nil {
		jsonError(w, 500, err.Error())
		return
	}
	jsonWrite(w, 200, map[string]bool{"ok": true})
}
func (s *Server) updateApplyHandler(w http.ResponseWriter, r *http.Request) {
	if s.Jobs.Active() {
		jsonError(w, 409, "Đang có tác vụ tải. Hãy hoàn tất hoặc hủy trước khi cập nhật")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 3*time.Minute)
	defer cancel()
	m, err := fetchManifest(ctx, s.State.Version)
	if err != nil {
		jsonError(w, 502, err.Error())
		return
	}
	if !versionLess(s.State.Version, m.Version) {
		jsonError(w, 409, "BiliSub Studio đã là phiên bản mới nhất")
		return
	}
	_ = s.OCR.Stop()
	newPath := filepath.Join(s.State.Paths.Temp, "BiliSubStudio_update_"+safeFile(m.Version)+".exe")
	if err := downloadUpdate(ctx, m, newPath); err != nil {
		jsonError(w, 502, err.Error())
		return
	}
	cmd := proc.Breakaway(exec.Command(newPath, "--apply-self-update", s.CurrentEXE, strconv.Itoa(os.Getpid())))
	if err := cmd.Start(); err != nil {
		jsonError(w, 500, "khởi động updater: "+err.Error())
		return
	}
	jsonWrite(w, 200, map[string]string{"version": m.Version})
	go func() { time.Sleep(700 * time.Millisecond); s.requestExit() }()
}
func (s *Server) exitHandler(w http.ResponseWriter, r *http.Request) {
	jsonWrite(w, 200, map[string]bool{"ok": true})
	go func() { time.Sleep(150 * time.Millisecond); s.Jobs.CancelAll(); _ = s.OCR.Stop(); s.requestExit() }()
}

func (s *Server) qrStartHandler(w http.ResponseWriter, r *http.Request) {
	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()
	var resp struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Data    struct {
			URL string `json:"url"`
			Key string `json:"qrcode_key"`
		} `json:"data"`
	}
	if err := biliJSON(ctx, "https://passport.bilibili.com/x/passport-login/web/qrcode/generate", &resp); err != nil {
		jsonError(w, 502, err.Error())
		return
	}
	if resp.Code != 0 || resp.Data.Key == "" {
		jsonError(w, 502, fmt.Sprintf("Bilibili QR: %d %s", resp.Code, resp.Message))
		return
	}
	jsonWrite(w, 200, map[string]string{"url": resp.Data.URL, "key": resp.Data.Key})
}
func (s *Server) qrPollHandler(w http.ResponseWriter, r *http.Request) {
	key := strings.TrimSpace(r.URL.Query().Get("key"))
	if key == "" {
		jsonError(w, 400, "Thiếu qrcode key")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 15*time.Second)
	defer cancel()
	var resp struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
		Data    struct {
			URL     string `json:"url"`
			Code    int    `json:"code"`
			Message string `json:"message"`
		} `json:"data"`
	}
	u := "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + url.QueryEscape(key)
	setCookies, err := biliJSONCookies(ctx, u, &resp)
	if err != nil {
		jsonError(w, 502, err.Error())
		return
	}
	if resp.Code != 0 {
		jsonError(w, 502, resp.Message)
		return
	}
	msg := resp.Data.Message
	switch resp.Data.Code {
	case 0:
		cookie := cookieFromQR(resp.Data.URL, setCookies)
		if cookie == "" {
			jsonError(w, 502, "QR thành công nhưng không lấy được Cookie")
			return
		}
		if err := s.State.SetCookie(cookie); err != nil {
			jsonError(w, 500, err.Error())
			return
		}
		s.invalidateCookieStatus()
		ok, user, errMsg := s.cookieStatus(ctx, true)
		if !ok {
			if errMsg == "" {
				errMsg = "Bilibili chưa xác nhận trạng thái đăng nhập"
			}
			jsonError(w, 502, errMsg)
			return
		}
		msg := "Đăng nhập thành công"
		if user != "" {
			msg += ": " + user
		}
		jsonWrite(w, 200, map[string]any{"logged_in": true, "message": msg, "user": user})
	case 86101:
		jsonWrite(w, 200, map[string]any{"logged_in": false, "message": "Chưa quét mã QR"})
	case 86090:
		jsonWrite(w, 200, map[string]any{"logged_in": false, "message": "Đã quét, hãy xác nhận trên điện thoại"})
	case 86038:
		jsonWrite(w, 200, map[string]any{"logged_in": false, "message": "Mã QR đã hết hạn"})
	default:
		if msg == "" {
			msg = fmt.Sprintf("Bilibili QR code %d", resp.Data.Code)
		}
		jsonWrite(w, 200, map[string]any{"logged_in": false, "message": msg})
	}
}

func biliJSON(ctx context.Context, endpoint string, out any) error {
	_, err := biliJSONCookies(ctx, endpoint, out)
	return err
}

func biliJSONCookies(ctx context.Context, endpoint string, out any) ([]*http.Cookie, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36")
	req.Header.Set("Referer", "https://www.bilibili.com/")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("Bilibili HTTP %d", resp.StatusCode)
	}
	cookies := resp.Cookies()
	if err := json.NewDecoder(io.LimitReader(resp.Body, 4<<20)).Decode(out); err != nil {
		return cookies, err
	}
	return cookies, nil
}

func cookieFromQR(raw string, setCookies []*http.Cookie) string {
	values := make(map[string]string)
	for _, c := range setCookies {
		if c == nil || strings.TrimSpace(c.Name) == "" || c.Value == "" {
			continue
		}
		values[c.Name] = c.Value
	}
	if u, err := url.Parse(raw); err == nil {
		q := u.Query()
		for _, k := range []string{"SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4", "b_nut", "buvid_fp", "buvid_fp_plain", "b_lsid"} {
			if v := q.Get(k); v != "" {
				values[k] = v
			}
		}
	}
	priority := []string{"SESSDATA", "bili_jct", "DedeUserID", "DedeUserID__ckMd5", "sid", "buvid3", "buvid4", "b_nut", "buvid_fp", "buvid_fp_plain", "b_lsid"}
	var out []string
	for _, k := range priority {
		if v := values[k]; v != "" {
			out = append(out, k+"="+v)
			delete(values, k)
		}
	}
	keys := make([]string, 0, len(values))
	for k := range values {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	for _, k := range keys {
		out = append(out, k+"="+values[k])
	}
	return strings.Join(out, "; ")
}

type biliNavResponse struct {
	Code    int    `json:"code"`
	Message string `json:"message"`
	Data    struct {
		IsLogin bool   `json:"isLogin"`
		Uname   string `json:"uname"`
		Mid     int64  `json:"mid"`
	} `json:"data"`
}

func validateBilibiliCookie(ctx context.Context, raw string) (bool, string, error) {
	if strings.TrimSpace(raw) == "" {
		return false, "", nil
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, "https://api.bilibili.com/x/web-interface/nav", nil)
	if err != nil {
		return false, "", err
	}
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36")
	req.Header.Set("Referer", "https://www.bilibili.com/")
	req.Header.Set("Cookie", raw)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return false, "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return false, "", fmt.Errorf("Bilibili nav HTTP %d", resp.StatusCode)
	}
	var nav biliNavResponse
	if err := json.NewDecoder(io.LimitReader(resp.Body, 2<<20)).Decode(&nav); err != nil {
		return false, "", err
	}
	if nav.Code != 0 {
		return false, "", fmt.Errorf("Bilibili nav: %d %s", nav.Code, nav.Message)
	}
	return nav.Data.IsLogin, strings.TrimSpace(nav.Data.Uname), nil
}

func (s *Server) invalidateCookieStatus() {
	s.cookieMu.Lock()
	s.cookieAt = time.Time{}
	s.cookieOK = false
	s.cookieUser = ""
	s.cookieErr = ""
	s.cookieMu.Unlock()
}

func (s *Server) cookieStatus(ctx context.Context, force bool) (bool, string, string) {
	s.cookieMu.Lock()
	if !force && !s.cookieAt.IsZero() && time.Since(s.cookieAt) < 5*time.Minute {
		ok, user, errMsg := s.cookieOK, s.cookieUser, s.cookieErr
		s.cookieMu.Unlock()
		return ok, user, errMsg
	}
	s.cookieMu.Unlock()

	ok, user, err := validateBilibiliCookie(ctx, s.State.CookieValue())
	errMsg := ""
	if err != nil {
		errMsg = err.Error()
	} else if !ok {
		errMsg = "Cookie không hợp lệ hoặc đã hết hạn"
	}
	s.cookieMu.Lock()
	s.cookieAt = time.Now()
	s.cookieOK = ok
	s.cookieUser = user
	s.cookieErr = errMsg
	s.cookieMu.Unlock()
	return ok, user, errMsg
}

func manifestFileIDForVersion(currentVersion string) string {
	if parseVersion(currentVersion).Pre != "" {
		return betaManifestFileID
	}
	return stableManifestFileID
}

func fetchManifest(ctx context.Context, currentVersion string) (updateManifest, error) {
	var m updateManifest
	manifestID := manifestFileIDForVersion(currentVersion)
	u := fmt.Sprintf("https://drive.google.com/uc?export=download&id=%s&_=%d", manifestID, time.Now().UnixNano())
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return m, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return m, fmt.Errorf("version.json HTTP %d", resp.StatusCode)
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&m); err != nil {
		return m, fmt.Errorf("version.json: %w", err)
	}
	if m.Version == "" || m.DownloadURL == "" {
		return m, errors.New("version.json thiếu version/download_url")
	}
	return m, nil
}
func normalizeDriveFileID(raw string) (string, error) {
	id := strings.TrimSpace(raw)
	if id == "" {
		return "", errors.New("download_url rỗng")
	}
	if !strings.Contains(id, "/") {
		return id, nil
	}
	u, err := url.Parse(id)
	if err != nil {
		return "", fmt.Errorf("download_url không hợp lệ: %w", err)
	}
	if q := strings.TrimSpace(u.Query().Get("id")); q != "" {
		return q, nil
	}
	parts := strings.Split(strings.Trim(u.Path, "/"), "/")
	for i := range parts {
		if parts[i] == "d" && i+1 < len(parts) && strings.TrimSpace(parts[i+1]) != "" {
			return strings.TrimSpace(parts[i+1]), nil
		}
	}
	return "", errors.New("không lấy được Google Drive file ID từ download_url")
}

func downloadUpdate(ctx context.Context, m updateManifest, path string) error {
	id, err := normalizeDriveFileID(m.DownloadURL)
	if err != nil {
		return err
	}
	u := "https://drive.usercontent.google.com/download?id=" + url.QueryEscape(id) + "&export=download&confirm=t"
	req, _ := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return fmt.Errorf("Tải update: HTTP %d", resp.StatusCode)
	}
	tmp := path + ".tmp"
	f, err := os.Create(tmp)
	if err != nil {
		return err
	}
	h := sha256.New()
	n, cp := io.Copy(io.MultiWriter(f, h), resp.Body)
	ce := f.Close()
	if cp != nil {
		_ = os.Remove(tmp)
		return cp
	}
	if ce != nil {
		_ = os.Remove(tmp)
		return ce
	}
	if m.Size > 0 && n != m.Size {
		_ = os.Remove(tmp)
		return fmt.Errorf("update size %d/%d", n, m.Size)
	}
	got := hex.EncodeToString(h.Sum(nil))
	if m.SHA256 != "" && !strings.EqualFold(got, m.SHA256) {
		_ = os.Remove(tmp)
		return fmt.Errorf("update SHA-256 không khớp")
	}
	return os.Rename(tmp, path)
}

func versionLess(a, b string) bool {
	va := parseVersion(a)
	vb := parseVersion(b)
	for i := 0; i < 3; i++ {
		if va.Core[i] != vb.Core[i] {
			return va.Core[i] < vb.Core[i]
		}
	}
	// Same numeric core: a prerelease is older than the stable release.
	if va.Pre == "" && vb.Pre != "" {
		return false
	}
	if va.Pre != "" && vb.Pre == "" {
		return true
	}
	if va.Pre == vb.Pre {
		return false
	}
	return prereleaseLess(va.Pre, vb.Pre)
}

type parsedVersion struct {
	Core [3]int
	Pre  string
}

func parseVersion(v string) parsedVersion {
	v = strings.TrimPrefix(strings.TrimSpace(v), "v")
	base, pre, _ := strings.Cut(v, "-")
	p := strings.Split(base, ".")
	var out parsedVersion
	for i := 0; i < len(p) && i < 3; i++ {
		out.Core[i], _ = strconv.Atoi(p[i])
	}
	out.Pre = strings.ToLower(strings.TrimSpace(pre))
	return out
}

func prereleaseLess(a, b string) bool {
	// Semver-like comparison sufficient for beta.1 / beta.2 / rc.1 release
	// channels used by BiliSub Studio. Numeric identifiers compare numerically;
	// text identifiers compare lexically; a shorter equal prefix sorts first.
	as := strings.FieldsFunc(a, func(r rune) bool { return r == '.' || r == '-' })
	bs := strings.FieldsFunc(b, func(r rune) bool { return r == '.' || r == '-' })
	for i := 0; i < len(as) && i < len(bs); i++ {
		ai, ae := strconv.Atoi(as[i])
		bi, be := strconv.Atoi(bs[i])
		switch {
		case ae == nil && be == nil:
			if ai != bi {
				return ai < bi
			}
		case ae == nil && be != nil:
			return true
		case ae != nil && be == nil:
			return false
		default:
			if as[i] != bs[i] {
				return as[i] < bs[i]
			}
		}
	}
	return len(as) < len(bs)
}

func newJobID(prefix string) string { return fmt.Sprintf("%s-%d", prefix, time.Now().UnixNano()) }
func jsonWrite(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
func jsonError(w http.ResponseWriter, status int, msg string) {
	jsonWrite(w, status, map[string]string{"error": msg})
}
func readJSON(w http.ResponseWriter, r *http.Request, v any) bool {
	if err := decodeJSON(r, v); err != nil {
		jsonError(w, 400, "JSON lỗi: "+err.Error())
		return false
	}
	return true
}
func decodeJSON(r *http.Request, v any) error {
	return json.NewDecoder(io.LimitReader(r.Body, 64<<20)).Decode(v)
}
func safeFile(s string) string {
	s = strings.TrimSpace(s)
	r := strings.NewReplacer("<", "_", ">", "_", ":", "_", "\"", "_", "/", "_", "\\", "_", "|", "_", "?", "_", "*", "_")
	s = strings.Trim(r.Replace(s), " .")
	if s == "" {
		s = "BiliSub"
	}
	return s
}
func uniquePath(p string) string {
	if _, err := os.Stat(p); os.IsNotExist(err) {
		return p
	}
	ext := filepath.Ext(p)
	base := strings.TrimSuffix(p, ext)
	for i := 2; i < 10000; i++ {
		q := fmt.Sprintf("%s (%d)%s", base, i, ext)
		if _, err := os.Stat(q); os.IsNotExist(err) {
			return q
		}
	}
	return p
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
	ss := ms / 1000
	ms %= 1000
	return fmt.Sprintf("%02d:%02d:%02d,%03d", h, m, ss, ms)
}

func sortedKeys(m map[string]string) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
