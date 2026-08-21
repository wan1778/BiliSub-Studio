package subtitle

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"html"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"bilisubstudio/internal/jobs"
	"bilisubstudio/internal/video"
)

type Service struct {
	Resolver   *video.YTDLPResolver
	CookieFile func() (string, error)
	CookieRaw  func() string
	Client     *http.Client
}

type Request struct {
	URL       string
	Format    string
	Track     string
	OutputDir string
}

type cue struct {
	Start float64
	End   float64
	Text  string
}

func (s *Service) Run(ctx context.Context, job *jobs.Job, req Request) error {
	if s.Resolver == nil {
		return errors.New("yt-dlp chưa sẵn sàng")
	}
	cookieFile := ""
	if s.CookieFile != nil {
		cookieFile, _ = s.CookieFile()
	}
	job.Set("resolving", 5, "Đang lấy danh sách phụ đề...")
	meta, err := s.Resolver.Metadata(ctx, req.URL, cookieFile)
	if err != nil {
		return err
	}
	var track *video.SubtitleTrack
	for i := range meta.Subtitles {
		if meta.Subtitles[i].Lang == req.Track {
			track = &meta.Subtitles[i]
			break
		}
	}
	if track == nil {
		return fmt.Errorf("không tìm thấy track %q", req.Track)
	}
	job.Logf("Track: %s (%s)", track.LangDoc, track.Lang)
	job.Set("downloading", 30, "Đang tải phụ đề...")
	raw, err := s.fetch(ctx, track.URL)
	if err != nil {
		return err
	}
	format := strings.ToLower(strings.TrimSpace(req.Format))
	if format != "txt" && format != "json" {
		format = "srt"
	}
	outDir := strings.TrimSpace(req.OutputDir)
	if outDir == "" {
		outDir = "."
	}
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return err
	}
	base := safeBase(meta.Title)
	if base == "" {
		base = safeBase(meta.ID)
	}
	if base == "" {
		base = "BiliSub_Subtitle"
	}
	out := unique(filepath.Join(outDir, fmt.Sprintf("%s [%s].%s", base, safeBase(track.Lang), format)))

	var output []byte
	switch format {
	case "json":
		if json.Valid(raw) {
			var v any
			if err := json.Unmarshal(raw, &v); err == nil {
				output, _ = json.MarshalIndent(v, "", "  ")
				output = append(output, '\n')
			} else {
				output = raw
			}
		} else {
			output = raw
		}
	case "srt", "txt":
		cues, err := parseCues(raw)
		if err != nil {
			return err
		}
		if format == "srt" {
			output = []byte(renderSRT(cues))
		} else {
			output = []byte(renderTXT(cues))
		}
	}
	if err := writeAtomic(out, output); err != nil {
		return err
	}
	job.Logf("Đã lưu: %s", out)
	job.Set("done", 100, out)
	return nil
}

func (s *Service) fetch(ctx context.Context, url string) ([]byte, error) {
	if strings.HasPrefix(url, "//") {
		url = "https:" + url
	}
	client := s.Client
	if client == nil {
		client = &http.Client{Timeout: 60 * time.Second}
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Referer", "https://www.bilibili.com/")
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36")
	if s.CookieRaw != nil && s.CookieRaw() != "" {
		req.Header.Set("Cookie", s.CookieRaw())
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, fmt.Errorf("subtitle HTTP %d", resp.StatusCode)
	}
	return io.ReadAll(io.LimitReader(resp.Body, 32<<20))
}

func parseCues(raw []byte) ([]cue, error) {
	// Bilibili native subtitle JSON.
	var bili struct {
		Body []struct {
			From    float64 `json:"from"`
			To      float64 `json:"to"`
			Content string  `json:"content"`
		} `json:"body"`
	}
	if json.Unmarshal(raw, &bili) == nil && len(bili.Body) > 0 {
		out := make([]cue, 0, len(bili.Body))
		for _, x := range bili.Body {
			text := cleanText(x.Content)
			if text != "" {
				out = append(out, cue{Start: x.From, End: x.To, Text: text})
			}
		}
		return normalizeCues(out), nil
	}

	// yt-dlp json3 / YouTube-style timed-text representation.
	var j3 struct {
		Events []struct {
			StartMS int64 `json:"tStartMs"`
			DurMS   int64 `json:"dDurationMs"`
			Segs    []struct {
				Text string `json:"utf8"`
			} `json:"segs"`
		} `json:"events"`
	}
	if json.Unmarshal(raw, &j3) == nil && len(j3.Events) > 0 {
		out := make([]cue, 0, len(j3.Events))
		for _, ev := range j3.Events {
			var b strings.Builder
			for _, seg := range ev.Segs {
				b.WriteString(seg.Text)
			}
			text := cleanText(b.String())
			if text == "" {
				continue
			}
			start := float64(ev.StartMS) / 1000
			end := start + float64(ev.DurMS)/1000
			out = append(out, cue{Start: start, End: end, Text: text})
		}
		return normalizeCues(out), nil
	}
	return nil, errors.New("định dạng phụ đề không nhận diện được")
}

func normalizeCues(in []cue) []cue {
	sort.SliceStable(in, func(i, j int) bool { return in[i].Start < in[j].Start })
	out := in[:0]
	for _, c := range in {
		if c.End <= c.Start {
			c.End = c.Start + 1.5
		}
		if len(out) > 0 && out[len(out)-1].Text == c.Text && c.Start <= out[len(out)-1].End+0.15 {
			if c.End > out[len(out)-1].End {
				out[len(out)-1].End = c.End
			}
			continue
		}
		out = append(out, c)
	}
	return out
}

func cleanText(s string) string {
	s = html.UnescapeString(strings.TrimSpace(s))
	s = strings.ReplaceAll(s, "\r", "")
	lines := strings.Split(s, "\n")
	out := lines[:0]
	for _, line := range lines {
		line = strings.TrimSpace(line)
		if line != "" {
			out = append(out, line)
		}
	}
	return strings.Join(out, "\n")
}

func renderSRT(cues []cue) string {
	var b strings.Builder
	for i, c := range cues {
		fmt.Fprintf(&b, "%d\n%s --> %s\n%s\n\n", i+1, srtTime(c.Start), srtTime(c.End), c.Text)
	}
	return b.String()
}

func renderTXT(cues []cue) string {
	var b strings.Builder
	last := ""
	for _, c := range cues {
		if c.Text == "" || c.Text == last {
			continue
		}
		b.WriteString(c.Text)
		b.WriteByte('\n')
		last = c.Text
	}
	return b.String()
}

func srtTime(sec float64) string {
	if sec < 0 {
		sec = 0
	}
	ms := int64(sec*1000 + 0.5)
	h := ms / 3_600_000
	ms %= 3_600_000
	m := ms / 60_000
	ms %= 60_000
	s := ms / 1000
	ms %= 1000
	return fmt.Sprintf("%02d:%02d:%02d,%03d", h, m, s, ms)
}

func writeAtomic(path string, b []byte) error {
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, b, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func safeBase(s string) string {
	s = strings.TrimSpace(s)
	r := strings.NewReplacer("<", "_", ">", "_", ":", "_", "\"", "_", "/", "_", "\\", "_", "|", "_", "?", "_", "*", "_")
	s = strings.Trim(r.Replace(s), " .")
	runes := []rune(s)
	if len(runes) > 150 {
		s = string(runes[:150])
	}
	return s
}

func unique(path string) string {
	if _, err := os.Stat(path); os.IsNotExist(err) {
		return path
	}
	ext := filepath.Ext(path)
	base := strings.TrimSuffix(path, ext)
	for i := 2; i < 10000; i++ {
		p := fmt.Sprintf("%s (%d)%s", base, i, ext)
		if _, err := os.Stat(p); os.IsNotExist(err) {
			return p
		}
	}
	return path
}
