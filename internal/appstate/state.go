package appstate

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

type Paths struct {
	Root  string
	Data  string
	Tools string
	OCR   string
	Temp  string
	Cache string
}

type Config struct {
	Theme          string `json:"theme"`
	OutputDir      string `json:"output_dir"`
	SubFormat      string `json:"sub_format"`
	VideoSpeed     string `json:"video_speed"`
	VideoContainer string `json:"video_container"`
	VideoMode      string `json:"video_mode"`
	CheckUpdates   bool   `json:"check_updates"`
	OCRDevice      string `json:"ocr_device"`
	OCRTop         int    `json:"ocr_top"`
	OCRBottom      int    `json:"ocr_bottom"`
	OCRLeft        int    `json:"ocr_left"`
	OCRRight       int    `json:"ocr_right"`
}

type State struct {
	Paths   Paths
	Version string
	Token   string

	mu     sync.RWMutex
	Config Config
	Cookie string
}

func New(root, version string) (*State, error) {
	root, err := filepath.Abs(root)
	if err != nil {
		return nil, err
	}
	p := Paths{
		Root: root, Data: filepath.Join(root, "Data"), Tools: filepath.Join(root, "Tools"),
		OCR: filepath.Join(root, "Tools", "OCR"), Temp: filepath.Join(root, "Temp"), Cache: filepath.Join(root, "Cache"),
	}
	for _, d := range []string{p.Data, p.Tools, p.Temp, p.Cache} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			return nil, fmt.Errorf("tạo %s: %w", d, err)
		}
	}
	st := &State{Paths: p, Version: version, Token: randomToken()}
	st.Config = Config{
		Theme: "dark", OutputDir: filepath.Join(root, "Downloads"), SubFormat: "srt", VideoSpeed: "fast", VideoContainer: "mp4", VideoMode: "video+audio",
		CheckUpdates: true, OCRDevice: "auto", OCRTop: 65, OCRBottom: 94, OCRLeft: 5, OCRRight: 95,
	}
	_ = os.MkdirAll(st.Config.OutputDir, 0o755)
	_ = st.LoadConfig()
	_ = st.LoadCookie()
	return st, nil
}

func randomToken() string {
	b := make([]byte, 24)
	if _, err := rand.Read(b); err != nil {
		return "bilisub-local-token"
	}
	return hex.EncodeToString(b)
}

func (s *State) ConfigPath() string { return filepath.Join(s.Paths.Data, "config.json") }
func (s *State) CookiePath() string { return filepath.Join(s.Paths.Data, "session.bin") }

func (s *State) LoadConfig() error {
	b, err := os.ReadFile(s.ConfigPath())
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return s.SaveConfig()
		}
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	cfg := s.Config
	if err := json.Unmarshal(b, &cfg); err != nil {
		return err
	}
	normalizeConfig(&cfg, s.Paths.Root)
	s.Config = cfg
	return nil
}

func normalizeConfig(c *Config, root string) {
	if c.Theme != "light" {
		c.Theme = "dark"
	}
	if strings.TrimSpace(c.OutputDir) == "" {
		c.OutputDir = filepath.Join(root, "Downloads")
	}
	if c.SubFormat != "srt" && c.SubFormat != "txt" && c.SubFormat != "json" {
		c.SubFormat = "srt"
	}
	switch c.VideoSpeed {
	case "stable", "fast", "turbo":
	default:
		c.VideoSpeed = "fast"
	}
	if c.VideoContainer != "mkv" {
		c.VideoContainer = "mp4"
	}
	switch c.VideoMode {
	case "video+audio", "video-only", "audio-only":
	default:
		c.VideoMode = "video+audio"
	}
	switch strings.ToLower(strings.TrimSpace(c.OCRDevice)) {
	case "cpu", "gpu", "hybrid":
		c.OCRDevice = strings.ToLower(strings.TrimSpace(c.OCRDevice))
	default:
		c.OCRDevice = "auto"
	}
	if c.OCRTop <= 0 {
		c.OCRTop = 65
	}
	if c.OCRBottom <= 0 {
		c.OCRBottom = 94
	}
	if c.OCRLeft < 0 {
		c.OCRLeft = 5
	}
	if c.OCRRight <= 0 {
		c.OCRRight = 95
	}
}

func (s *State) SnapshotConfig() Config {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.Config
}

func (s *State) UpdateConfig(fn func(*Config)) error {
	s.mu.Lock()
	fn(&s.Config)
	normalizeConfig(&s.Config, s.Paths.Root)
	cfg := s.Config
	s.mu.Unlock()
	return saveJSONAtomic(s.ConfigPath(), cfg)
}

func (s *State) SaveConfig() error { return saveJSONAtomic(s.ConfigPath(), s.SnapshotConfig()) }

func saveJSONAtomic(path string, value any) error {
	b, err := json.MarshalIndent(value, "", "  ")
	if err != nil {
		return err
	}
	b = append(b, '\n')
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func (s *State) SetCookie(raw string) error {
	raw = normalizeCookie(raw)
	if raw == "" {
		return errors.New("cookie rỗng")
	}
	protected, err := protect([]byte(raw))
	if err != nil {
		return err
	}
	if err := os.WriteFile(s.CookiePath(), protected, 0o600); err != nil {
		return err
	}
	s.mu.Lock()
	s.Cookie = raw
	s.mu.Unlock()
	return nil
}

func (s *State) LoadCookie() error {
	b, err := os.ReadFile(s.CookiePath())
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil
		}
		return err
	}
	plain, err := unprotect(b)
	if err != nil {
		return err
	}
	s.mu.Lock()
	s.Cookie = normalizeCookie(string(plain))
	s.mu.Unlock()
	return nil
}

func (s *State) DeleteCookie() error {
	s.mu.Lock()
	s.Cookie = ""
	s.mu.Unlock()
	if err := os.Remove(s.CookiePath()); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}

func (s *State) CookieValue() string {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.Cookie
}

func normalizeCookie(raw string) string {
	raw = strings.TrimSpace(raw)
	if len(raw) >= len("Cookie:") && strings.EqualFold(raw[:len("Cookie:")], "Cookie:") {
		raw = strings.TrimSpace(raw[len("Cookie:"):])
	}
	if raw == "" {
		return ""
	}
	// The UI explicitly allows pasting SESSDATA itself. A bare token has no
	// '=' or ';', so normalize it into the cookie name yt-dlp/Bilibili expect.
	if !strings.Contains(raw, "=") && !strings.Contains(raw, ";") && !strings.ContainsAny(raw, "\r\n\t ") {
		return "SESSDATA=" + raw
	}
	parts := strings.Split(raw, ";")
	out := make([]string, 0, len(parts))
	seen := make(map[string]bool)
	for _, p := range parts {
		p = strings.TrimSpace(p)
		if p == "" || !strings.Contains(p, "=") {
			continue
		}
		kv := strings.SplitN(p, "=", 2)
		name := strings.TrimSpace(kv[0])
		if name == "" || seen[strings.ToLower(name)] {
			continue
		}
		seen[strings.ToLower(name)] = true
		out = append(out, name+"="+strings.TrimSpace(kv[1]))
	}
	return strings.Join(out, "; ")
}

func (s *State) WriteNetscapeCookieFile() (string, error) {
	raw := s.CookieValue()
	if raw == "" {
		return "", nil
	}
	path := filepath.Join(s.Paths.Temp, "bilibili_cookies.txt")
	var b strings.Builder
	b.WriteString("# Netscape HTTP Cookie File\n")
	for _, p := range strings.Split(raw, ";") {
		p = strings.TrimSpace(p)
		kv := strings.SplitN(p, "=", 2)
		if len(kv) != 2 || strings.TrimSpace(kv[0]) == "" {
			continue
		}
		// Domain, include subdomains, path, secure, expiry, name, value.
		fmt.Fprintf(&b, ".bilibili.com\tTRUE\t/\tTRUE\t2147483647\t%s\t%s\n", strings.TrimSpace(kv[0]), strings.TrimSpace(kv[1]))
	}
	if err := os.WriteFile(path, []byte(b.String()), 0o600); err != nil {
		return "", err
	}
	return path, nil
}

func DirSize(root string) int64 {
	var total int64
	_ = filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return nil
		}
		if st, err := d.Info(); err == nil {
			total += st.Size()
		}
		return nil
	})
	return total
}
