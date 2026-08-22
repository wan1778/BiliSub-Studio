package api

import (
	"bytes"
	"encoding/json"
	"image/jpeg"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"bilisubstudio/internal/appstate"
)

func TestStatusRequiresTokenAndMatchesUIContract(t *testing.T) {
	st, err := appstate.New(t.TempDir(), "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	s := New(st)
	h := s.Handler()

	rr := httptest.NewRecorder()
	h.ServeHTTP(rr, httptest.NewRequest("GET", "/api/status", nil))
	if rr.Code != 401 {
		t.Fatalf("unauth status=%d", rr.Code)
	}

	rr = httptest.NewRecorder()
	req := httptest.NewRequest("GET", "/api/status?token="+st.Token, nil)
	h.ServeHTTP(rr, req)
	if rr.Code != 200 {
		t.Fatalf("status=%d body=%s", rr.Code, rr.Body.String())
	}
	body := rr.Body.String()
	for _, want := range []string{`"version"`, `"drive"`, `"root"`, `"cookie_saved"`, `"config"`, `"storage"`, `"ocr_ready"`} {
		if !contains(body, want) {
			t.Fatalf("missing %s in %s", want, body)
		}
	}
}

func TestIndexInjectsToken(t *testing.T) {
	st, _ := appstate.New(t.TempDir(), "4.0.0-test")
	s := New(st)
	rr := httptest.NewRecorder()
	s.Handler().ServeHTTP(rr, httptest.NewRequest("GET", "/", nil))
	if rr.Code != 200 {
		t.Fatal(rr.Code)
	}
	if !contains(rr.Body.String(), st.Token) || contains(rr.Body.String(), "__APP_TOKEN__") {
		t.Fatal("token was not injected")
	}
}

func contains(s, sub string) bool { return len(sub) == 0 || (len(s) >= len(sub) && index(s, sub) >= 0) }
func index(s, sub string) int {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return i
		}
	}
	return -1
}

func TestVersionLessHandlesPrerelease(t *testing.T) {
	cases := []struct {
		a, b string
		want bool
	}{
		{"4.0.0-beta.1", "4.0.0-beta.2", true},
		{"4.0.0-beta.2", "4.0.0-rc.1", true},
		{"4.0.0-beta.2", "4.0.0", true},
		{"4.0.0", "4.0.0-beta.9", false},
		{"4.0.1", "4.1.0", true},
		{"4.1.0", "4.0.9", false},
	}
	for _, tc := range cases {
		if got := versionLess(tc.a, tc.b); got != tc.want {
			t.Fatalf("versionLess(%q,%q)=%v want %v", tc.a, tc.b, got, tc.want)
		}
	}
}

func TestManifestChannelSelection(t *testing.T) {
	if got := manifestFileIDForVersion("4.0.0-beta.4"); got != betaManifestFileID {
		t.Fatalf("beta manifest=%q want %q", got, betaManifestFileID)
	}
	if got := manifestFileIDForVersion("4.0.0-rc.1"); got != betaManifestFileID {
		t.Fatalf("rc manifest=%q want beta %q", got, betaManifestFileID)
	}
	if got := manifestFileIDForVersion("4.0.0"); got != stableManifestFileID {
		t.Fatalf("stable manifest=%q want %q", got, stableManifestFileID)
	}
}

func TestNormalizeDriveFileID(t *testing.T) {
	cases := map[string]string{
		"12KyZ1TGrnbkBn2oGSgr5705GKWFzQMVh":                       "12KyZ1TGrnbkBn2oGSgr5705GKWFzQMVh",
		"https://drive.google.com/uc?export=download&id=ABC123":   "ABC123",
		"https://drive.google.com/file/d/XYZ789/view?usp=sharing": "XYZ789",
	}
	for in, want := range cases {
		got, err := normalizeDriveFileID(in)
		if err != nil || got != want {
			t.Fatalf("normalizeDriveFileID(%q)=%q,%v want %q", in, got, err, want)
		}
	}
	if _, err := normalizeDriveFileID("https://drive.google.com/open"); err == nil {
		t.Fatal("expected malformed Drive URL to fail")
	}
}

func TestCookieFromQRMergesResponseAndURL(t *testing.T) {
	got := cookieFromQR("https://example.com/callback?SESSDATA=urlSess&bili_jct=csrf&DedeUserID=123", []*http.Cookie{{Name: "sid", Value: "sidv"}, {Name: "SESSDATA", Value: "headerSess"}})
	for _, want := range []string{"SESSDATA=urlSess", "bili_jct=csrf", "DedeUserID=123", "sid=sidv"} {
		if !strings.Contains(got, want) {
			t.Fatalf("missing %q in %q", want, got)
		}
	}
}

func TestThemeAPIUpdatesPersistentConfig(t *testing.T) {
	st, err := appstate.New(t.TempDir(), "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	srv := New(st)
	req := httptest.NewRequest(http.MethodPost, "/api/theme?token="+st.Token, strings.NewReader(`{"theme":"light"}`))
	req.Header.Set("Content-Type", "application/json")
	rr := httptest.NewRecorder()
	srv.Handler().ServeHTTP(rr, req)
	if rr.Code != http.StatusOK {
		t.Fatalf("theme status=%d body=%s", rr.Code, rr.Body.String())
	}
	if got := st.SnapshotConfig().Theme; got != "light" {
		t.Fatalf("theme=%q want light", got)
	}

	req = httptest.NewRequest(http.MethodPost, "/api/theme?token="+st.Token, strings.NewReader(`{"theme":"sepia"}`))
	req.Header.Set("Content-Type", "application/json")
	rr = httptest.NewRecorder()
	srv.Handler().ServeHTTP(rr, req)
	if rr.Code != http.StatusBadRequest {
		t.Fatalf("invalid theme status=%d body=%s", rr.Code, rr.Body.String())
	}
}

func TestEmbeddedUIIsSyncedWithSourceMirror(t *testing.T) {
	embedded, err := webFS.ReadFile("web/index.html")
	if err != nil {
		t.Fatal(err)
	}
	mirror, err := os.ReadFile(filepath.Join("..", "..", "web", "index.html"))
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(embedded, mirror) {
		t.Fatal("internal/api/web/index.html drifted from web/index.html")
	}
}

func TestOCRPreviewUsesExternalControlsAndDirectRegionOverlay(t *testing.T) {
	b, err := webFS.ReadFile("web/index.html")
	if err != nil {
		t.Fatal(err)
	}
	html := string(b)
	if strings.Contains(html, `<video id="ocrVideo" controls`) {
		t.Fatal("OCR preview must not use native video controls because they cover subtitle pixels")
	}
	for _, want := range []string{
		`id="ocrPlay"`, `id="ocrScrub"`, `id="ocrMute"`, `id="ocrFullscreen"`,
		`id="ocrOverlay"`, `data-roi-handle="se"`, `function ocrFrameRect()`,
		`id="ocrPath"`, `id="ocrMode"`, `/api/ocr/scan`,
	} {
		if !strings.Contains(html, want) {
			t.Fatalf("missing OCR preview contract marker %q", want)
		}
	}
	ocrJSStart := strings.Index(html, `const ocrVideo=`)
	editorJSStart := strings.Index(html, `// ---- Preview-first Video Editor`)
	if ocrJSStart < 0 || editorJSStart <= ocrJSStart {
		t.Fatal("cannot isolate OCR JS block")
	}
	ocrJS := html[ocrJSStart:editorJSStart]
	if strings.Contains(ocrJS, "requestVideoFrameCallback") || strings.Contains(ocrJS, "ocrFrameCallback") {
		t.Fatal("full-video OCR scan must not depend on browser-presented frame callbacks")
	}
}

func TestRefreshStatusDoesNotClobberInteractiveOCRAndDownloadControls(t *testing.T) {
	b, err := webFS.ReadFile("web/index.html")
	if err != nil {
		t.Fatal(err)
	}
	html := string(b)
	if !strings.Contains(html, `appConfigHydrated`) {
		t.Fatal("status refresh must hydrate user-editable config only once at startup")
	}
	if !strings.Contains(html, `appConfigHydrated||(s("subFormat").value=`) {
		t.Fatal("download/OCR defaults must be guarded from later status refreshes")
	}
}

func TestSharedVideoPickerRouteUsesNativeWindowsDialogs(t *testing.T) {
	b, err := webFS.ReadFile("web/index.html")
	if err != nil {
		t.Fatal(err)
	}
	html := string(b)
	if strings.Count(html, `r("/api/pick-video"`) != 2 {
		t.Fatalf("OCR and Editor must share exactly one /api/pick-video route; count=%d", strings.Count(html, `r("/api/pick-video"`))
	}
	win, err := os.ReadFile("platform_windows.go")
	if err != nil {
		t.Fatal(err)
	}
	windowsSource := string(win)
	for _, required := range []string{
		`NewProc("GetOpenFileNameW")`,
		`NewProc("SHBrowseForFolderW")`,
		`NewProc("CoInitializeEx")`,
		`NewProc("CoUninitialize")`,
		`coinitApartmentThreaded`,
		`func pickVideoNative(initial string)`,
		`func pickFolderNative(initial string)`,
		`runtime.LockOSThread()`,
	} {
		if !strings.Contains(windowsSource, required) {
			t.Fatalf("native Windows picker missing %q", required)
		}
	}
	for _, forbidden := range []string{
		`powershell.exe`,
		`System.Windows.Forms`,
		`OpenFileDialog`,
		`FolderBrowserDialog`,
		`dialogHost(`,
	} {
		if strings.Contains(windowsSource, forbidden) {
			t.Fatalf("interactive picker must not depend on PowerShell/WinForms host: found %q", forbidden)
		}
	}
}

func TestProductionLifecycleHasNoIdleAutoExit(t *testing.T) {
	mainSource, err := os.ReadFile(filepath.Join("..", "..", "cmd", "bilisub", "main.go"))
	if err != nil {
		t.Fatal(err)
	}
	serverSource, err := os.ReadFile("server.go")
	if err != nil {
		t.Fatal(err)
	}
	for name, src := range map[string]string{"cmd/bilisub/main.go": string(mainSource), "internal/api/server.go": string(serverSource)} {
		for _, forbidden := range []string{"StartIdleWatch", "lastSeen", "idleWatchInterval"} {
			if strings.Contains(src, forbidden) {
				t.Fatalf("%s still contains idle auto-exit marker %q", name, forbidden)
			}
		}
	}
}

func TestEmbeddedUIBootstrapsStatusWithoutOpeningSettings(t *testing.T) {
	b, err := webFS.ReadFile("web/index.html")
	if err != nil {
		t.Fatal(err)
	}
	html := string(b)
	if !strings.Contains(html, `async function refreshAppStatus(){`) {
		t.Fatal("missing descriptive app status refresh function")
	}
	if strings.Contains(html, `setInterval(backendHeartbeat`) || strings.Contains(html, `startBackendHeartbeat()`) {
		t.Fatal("UI must not depend on heartbeat timers to keep the desktop backend alive")
	}
	bootstrap := strings.LastIndex(html, "refreshAppStatus();")
	editorInit := strings.LastIndex(html, `s("editorExport").onclick=editorExport`)
	if bootstrap < 0 || editorInit < 0 || bootstrap <= editorInit {
		t.Fatalf("app status bootstrap must run after feature UI initialization: bootstrap=%d editorInit=%d", bootstrap, editorInit)
	}
	for _, id := range []string{`s("version").textContent=t.version`, `s("driveSide").textContent=t.drive`, `s("updateCurrent").textContent="v"+t.version`} {
		if !strings.Contains(html, id) {
			t.Fatalf("status refresh missing sidebar/settings binding %q", id)
		}
	}
}

func TestEditorPreviewFallbackAPIWithRealFFmpeg(t *testing.T) {
	ffmpeg, err := exec.LookPath("ffmpeg")
	if err != nil {
		t.Skip("ffmpeg not installed")
	}
	ffprobe, err := exec.LookPath("ffprobe")
	if err != nil {
		t.Skip("ffprobe not installed")
	}
	root := t.TempDir()
	st, err := appstate.New(root, "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	if err := copyTestExecutable(ffmpeg, filepath.Join(st.Paths.Tools, "ffmpeg.exe")); err != nil {
		t.Fatal(err)
	}
	if err := copyTestExecutable(ffprobe, filepath.Join(st.Paths.Tools, "ffprobe.exe")); err != nil {
		t.Fatal(err)
	}
	input := filepath.Join(root, "fallback.mkv")
	fixture := exec.Command(ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
		"-f", "lavfi", "-i", "color=c=0x345678:s=320x180:d=1:r=10",
		"-c:v", "libx264", "-pix_fmt", "yuv420p", input)
	if out, err := fixture.CombinedOutput(); err != nil {
		t.Fatalf("fixture ffmpeg: %v: %s", err, out)
	}
	srv := New(st)
	h := srv.Handler()

	body := bytes.NewBufferString(`{"path":` + strconvQuote(input) + `}`)
	req := httptest.NewRequest(http.MethodPost, "/api/preview-info?token="+st.Token, body)
	req.Header.Set("Content-Type", "application/json")
	rr := httptest.NewRecorder()
	h.ServeHTTP(rr, req)
	if rr.Code != http.StatusOK {
		t.Fatalf("preview info status=%d body=%s", rr.Code, rr.Body.String())
	}
	var info struct {
		Width            int     `json:"width"`
		Height           int     `json:"height"`
		Duration         float64 `json:"duration"`
		DirectCompatible bool    `json:"direct_compatible"`
	}
	if err := json.Unmarshal(rr.Body.Bytes(), &info); err != nil {
		t.Fatal(err)
	}
	if info.Width != 320 || info.Height != 180 || info.Duration <= 0 || info.DirectCompatible {
		t.Fatalf("preview info=%+v", info)
	}

	rr = httptest.NewRecorder()
	frameURL := "/api/preview-frame?token=" + url.QueryEscape(st.Token) + "&path=" + url.QueryEscape(input) + "&time=0.4"
	h.ServeHTTP(rr, httptest.NewRequest(http.MethodGet, frameURL, nil))
	if rr.Code != http.StatusOK {
		t.Fatalf("preview frame status=%d body=%s", rr.Code, rr.Body.String())
	}
	if got := rr.Header().Get("Content-Type"); !strings.HasPrefix(got, "image/jpeg") {
		t.Fatalf("content-type=%q", got)
	}
	cfg, err := jpeg.DecodeConfig(bytes.NewReader(rr.Body.Bytes()))
	if err != nil || cfg.Width <= 0 || cfg.Height <= 0 {
		t.Fatalf("jpeg config=%+v err=%v", cfg, err)
	}
}

func strconvQuote(s string) string {
	b, _ := json.Marshal(s)
	return string(b)
}

func TestEmbeddedUIFieldRegressionContracts(t *testing.T) {
	b, err := webFS.ReadFile("web/index.html")
	if err != nil {
		t.Fatal(err)
	}
	html := string(b)
	for _, want := range []string{
		`id="defaultOut"`, `id="defaultOutPick"`, `id="defaultOutOpen"`,
		`Tổng phụ đề đã ghi nhận:`, `id="cueShown"`, `ocrSyncCueListToTime`, `recent_cues`, `last_confidence`,
		`id="ocrFallbackFrame"`, `id="editorFallbackFrame"`, `/api/preview-info`, `/api/preview-frame`,
		`Xem theo khung hình`,
	} {
		if !strings.Contains(html, want) {
			t.Fatalf("missing field-regression contract marker %q", want)
		}
	}
	for _, old := range []string{`OCR frame / trạng thái`, `Subtitle tìm thấy:`, `/api/editor/preview-info`, `/api/editor/preview-frame`} {
		if strings.Contains(html, old) {
			t.Fatalf("stale UI/preview contract marker remains: %q", old)
		}
	}
}

func TestOCRExportEnforcesChineseOnlyOutput(t *testing.T) {
	st, err := appstate.New(t.TempDir(), "4.0.0-test")
	if err != nil {
		t.Fatal(err)
	}
	s := New(st)
	outDir := filepath.Join(t.TempDir(), "out")
	payload := map[string]any{
		"outputDir": outDir,
		"fileName":  "strict-chinese.srt",
		"cues": []map[string]any{
			{"start": 1.0, "end": 2.0, "text": "真正字幕", "conf": .95},
			{"start": 2.0, "end": 3.0, "text": "ILLC", "conf": .99},
			{"start": 3.0, "end": 4.0, "text": "铺 U 碎", "conf": .98},
			{"start": 4.0, "end": 5.0, "text": "2/", "conf": .98},
			{"start": 5.0, "end": 6.0, "text": "炼化破珠子， ，是巧合", "conf": .94},
		},
	}
	body, _ := json.Marshal(payload)
	rr := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodPost, "/api/ocr/export?token="+url.QueryEscape(st.Token), bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	s.Handler().ServeHTTP(rr, req)
	if rr.Code != http.StatusOK {
		t.Fatalf("status=%d body=%s", rr.Code, rr.Body.String())
	}
	var got struct {
		Path  string `json:"path"`
		Count int    `json:"count"`
	}
	if err := json.Unmarshal(rr.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	if got.Count != 2 {
		t.Fatalf("export count=%d want 2 body=%s", got.Count, rr.Body.String())
	}
	b, err := os.ReadFile(got.Path)
	if err != nil {
		t.Fatal(err)
	}
	text := string(b)
	for _, bad := range []string{"ILLC", "铺 U 碎", "2/", "， ，"} {
		if strings.Contains(text, bad) {
			t.Fatalf("foreign/noisy text %q leaked into SRT: %s", bad, text)
		}
	}
	for _, want := range []string{"真正字幕", "炼化破珠子，是巧合"} {
		if !strings.Contains(text, want) {
			t.Fatalf("missing normalized Chinese text %q in %s", want, text)
		}
	}
}

func copyTestExecutable(src, dst string) error {
	b, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	return os.WriteFile(dst, b, 0o755)
}
