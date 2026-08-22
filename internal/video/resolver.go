package video

import (
	"bilisubstudio/internal/proc"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os/exec"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

type StreamKind string

const (
	StreamVideo StreamKind = "video"
	StreamAudio StreamKind = "audio"
)

type Stream struct {
	Kind       StreamKind
	FormatID   string
	URL        string
	Headers    map[string]string
	Size       int64
	Height     int
	Ext        string
	Generation uint64
}

type Selection struct {
	Title string
	ID    string
	Video *Stream
	Audio *Stream
}

type ResolveRequest struct {
	URL        string
	Quality    string
	Mode       string
	Container  string
	CookieFile string
}

type ytdlpFormat struct {
	FormatID       string            `json:"format_id"`
	URL            string            `json:"url"`
	Ext            string            `json:"ext"`
	VCodec         string            `json:"vcodec"`
	ACodec         string            `json:"acodec"`
	Height         int               `json:"height"`
	Filesize       int64             `json:"filesize"`
	FilesizeApprox int64             `json:"filesize_approx"`
	HTTPHeaders    map[string]string `json:"http_headers"`
	Protocol       string            `json:"protocol"`
	Tbr            float64           `json:"tbr"`
	Abr            float64           `json:"abr"`
}
type ytdlpSubtitleEntry struct {
	Ext  string `json:"ext"`
	URL  string `json:"url"`
	Name string `json:"name"`
}

type ytdlpInfo struct {
	ID                string                          `json:"id"`
	Title             string                          `json:"title"`
	Formats           []ytdlpFormat                   `json:"formats"`
	Subtitles         map[string][]ytdlpSubtitleEntry `json:"subtitles"`
	AutomaticCaptions map[string][]ytdlpSubtitleEntry `json:"automatic_captions"`
}

type SubtitleTrack struct {
	Lang     string `json:"lang"`
	LangDoc  string `json:"lang_doc"`
	Official bool   `json:"official"`
	AI       bool   `json:"ai"`
	URL      string `json:"-"`
	Ext      string `json:"-"`
}

type Metadata struct {
	Title     string          `json:"title"`
	ID        string          `json:"id"`
	Qualities []string        `json:"qualities"`
	Subtitles []SubtitleTrack `json:"subtitles"`
}

type YTDLPResolver struct {
	Path       string
	mu         sync.Mutex
	generation uint64
	last       time.Time
}

func (r *YTDLPResolver) Resolve(ctx context.Context, req ResolveRequest) (Selection, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	info, err := r.loadInfo(ctx, req.URL, req.CookieFile)
	if err != nil {
		return Selection{}, err
	}
	r.generation++
	gen := r.generation
	sel := Selection{Title: info.Title, ID: info.ID}
	qh := parseQuality(req.Quality)
	if req.Mode != "audio-only" {
		vf := chooseVideo(info.Formats, qh, preferAVC(req.Container))
		if vf == nil {
			return Selection{}, errors.New("không tìm thấy video stream phù hợp")
		}
		sel.Video = toStream(StreamVideo, *vf, gen)
	}
	if req.Mode != "video-only" {
		af := chooseAudio(info.Formats)
		if af == nil {
			return Selection{}, errors.New("không tìm thấy audio stream phù hợp")
		}
		sel.Audio = toStream(StreamAudio, *af, gen)
	}
	r.last = time.Now()
	return sel, nil
}

func (r *YTDLPResolver) loadInfo(ctx context.Context, url, cookieFile string) (ytdlpInfo, error) {
	if strings.TrimSpace(r.Path) == "" {
		return ytdlpInfo{}, errors.New("yt-dlp chưa cấu hình")
	}
	args := []string{"--ignore-config", "--no-playlist", "--skip-download", "--no-warnings", "-J", url}
	if cookieFile != "" {
		args = append([]string{"--cookies", cookieFile}, args...)
	}
	cmd := proc.Hide(exec.CommandContext(ctx, r.Path, args...))
	out, err := cmd.Output()
	if err != nil {
		if ee, ok := err.(*exec.ExitError); ok && len(ee.Stderr) > 0 {
			return ytdlpInfo{}, fmt.Errorf("yt-dlp resolve: %w: %s", err, strings.TrimSpace(string(ee.Stderr)))
		}
		return ytdlpInfo{}, fmt.Errorf("yt-dlp resolve: %w", err)
	}
	var info ytdlpInfo
	if err := json.Unmarshal(out, &info); err != nil {
		return ytdlpInfo{}, fmt.Errorf("yt-dlp JSON: %w", err)
	}
	return info, nil
}

func (r *YTDLPResolver) Metadata(ctx context.Context, url, cookieFile string) (Metadata, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	info, err := r.loadInfo(ctx, url, cookieFile)
	if err != nil {
		return Metadata{}, err
	}
	m := Metadata{Title: info.Title, ID: info.ID}
	heights := map[int]bool{}
	for _, f := range info.Formats {
		if f.URL != "" && f.VCodec != "" && f.VCodec != "none" && f.Height > 0 {
			heights[f.Height] = true
		}
	}
	vals := make([]int, 0, len(heights))
	for h := range heights {
		vals = append(vals, h)
	}
	sort.Sort(sort.Reverse(sort.IntSlice(vals)))
	m.Qualities = append(m.Qualities, "best")
	for _, h := range vals {
		m.Qualities = append(m.Qualities, fmt.Sprintf("%dp", h))
	}

	seen := map[string]bool{}
	appendTracks := func(src map[string][]ytdlpSubtitleEntry, official, ai bool) {
		langs := make([]string, 0, len(src))
		for lang := range src {
			langs = append(langs, lang)
		}
		sort.Strings(langs)
		for _, lang := range langs {
			if seen[lang] || len(src[lang]) == 0 {
				continue
			}
			entry := chooseSubtitleEntry(src[lang])
			if entry.URL == "" {
				continue
			}
			name := strings.TrimSpace(entry.Name)
			if name == "" {
				name = lang
			}
			m.Subtitles = append(m.Subtitles, SubtitleTrack{Lang: lang, LangDoc: name, Official: official, AI: ai, URL: entry.URL, Ext: entry.Ext})
			seen[lang] = true
		}
	}
	appendTracks(info.Subtitles, true, false)
	appendTracks(info.AutomaticCaptions, false, true)
	return m, nil
}

func chooseSubtitleEntry(entries []ytdlpSubtitleEntry) ytdlpSubtitleEntry {
	if len(entries) == 0 {
		return ytdlpSubtitleEntry{}
	}
	preferred := []string{"json3", "json", "srv3", "vtt", "srt"}
	for _, ext := range preferred {
		for _, e := range entries {
			if strings.EqualFold(e.Ext, ext) && e.URL != "" {
				return e
			}
		}
	}
	for _, e := range entries {
		if e.URL != "" {
			return e
		}
	}
	return entries[0]
}

func toStream(k StreamKind, f ytdlpFormat, gen uint64) *Stream {
	size := f.Filesize
	if size <= 0 {
		size = f.FilesizeApprox
	}
	h := map[string]string{}
	for k, v := range f.HTTPHeaders {
		h[k] = v
	}
	return &Stream{Kind: k, FormatID: f.FormatID, URL: f.URL, Headers: h, Size: size, Height: f.Height, Ext: f.Ext, Generation: gen}
}
func parseQuality(q string) int {
	q = strings.TrimSpace(strings.ToLower(q))
	if q == "" || q == "best" {
		return 0
	}
	q = strings.TrimSuffix(q, "p")
	n, _ := strconv.Atoi(q)
	return n
}
func chooseVideo(fs []ytdlpFormat, q int, preferH264 bool) *ytdlpFormat {
	c := make([]ytdlpFormat, 0, len(fs))
	for _, f := range fs {
		if f.URL == "" || f.VCodec == "" || f.VCodec == "none" {
			continue
		}
		if f.ACodec != "" && f.ACodec != "none" {
			continue
		} // prefer DASH video-only
		if q > 0 && f.Height > q {
			continue
		}
		c = append(c, f)
	}
	if len(c) == 0 && q > 0 {
		return chooseVideo(fs, 0, preferH264)
	}
	sort.SliceStable(c, func(i, j int) bool {
		if c[i].Height != c[j].Height {
			return c[i].Height > c[j].Height
		}
		// MP4 is commonly consumed by Premiere/After Effects. At the same
		// requested resolution, prefer H.264/AVC before HEVC/AV1 for broad
		// editing compatibility. This preference never sacrifices resolution.
		if preferH264 {
			ri, rj := videoCodecRank(c[i].VCodec), videoCodecRank(c[j].VCodec)
			if ri != rj {
				return ri < rj
			}
		}
		return c[i].Tbr > c[j].Tbr
	})
	if len(c) == 0 {
		return nil
	}
	return &c[0]
}
func preferAVC(container string) bool {
	return strings.ToLower(strings.TrimSpace(container)) != "mkv"
}

func videoCodecRank(codec string) int {
	c := strings.ToLower(strings.TrimSpace(codec))
	switch {
	case strings.HasPrefix(c, "avc1"), strings.Contains(c, "h264"), strings.Contains(c, "h.264"):
		return 0
	case strings.HasPrefix(c, "hev1"), strings.HasPrefix(c, "hvc1"), strings.Contains(c, "hevc"), strings.Contains(c, "h265"), strings.Contains(c, "h.265"):
		return 1
	case strings.HasPrefix(c, "av01"), strings.Contains(c, "av1"):
		return 2
	default:
		return 3
	}
}

func chooseAudio(fs []ytdlpFormat) *ytdlpFormat {
	c := make([]ytdlpFormat, 0, len(fs))
	for _, f := range fs {
		if f.URL == "" || f.ACodec == "" || f.ACodec == "none" {
			continue
		}
		if f.VCodec != "" && f.VCodec != "none" {
			continue
		}
		c = append(c, f)
	}
	sort.SliceStable(c, func(i, j int) bool {
		ai := c[i].Abr
		if ai == 0 {
			ai = c[i].Tbr
		}
		aj := c[j].Abr
		if aj == 0 {
			aj = c[j].Tbr
		}
		return ai > aj
	})
	if len(c) == 0 {
		return nil
	}
	return &c[0]
}
