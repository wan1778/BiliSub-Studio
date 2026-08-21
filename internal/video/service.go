package video

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"

	"bilisubstudio/internal/jobs"
	"bilisubstudio/internal/proc"
)

type Service struct {
	Resolver   *YTDLPResolver
	FFmpeg     string
	CookieFile string
	WorkRoot   string
}

type JobRequest struct {
	URL       string
	Quality   string
	Container string
	Mode      string
	Speed     string
	OutputDir string
}

func SpeedConnections(speed string) int {
	switch strings.ToLower(strings.TrimSpace(speed)) {
	case "stable":
		return 1
	case "turbo":
		return 8
	default:
		return 4
	}
}

func (s *Service) Run(ctx context.Context, job *jobs.Job, req JobRequest) error {
	if s.Resolver == nil || strings.TrimSpace(s.Resolver.Path) == "" {
		return fmt.Errorf("yt-dlp chưa sẵn sàng")
	}
	if strings.TrimSpace(req.URL) == "" {
		return fmt.Errorf("URL rỗng")
	}
	job.Set("resolving", 0, "Đang lấy stream Bilibili...")
	sel, err := s.Resolver.Resolve(ctx, ResolveRequest{
		URL: req.URL, Quality: req.Quality, Mode: req.Mode, Container: req.Container, CookieFile: s.CookieFile,
	})
	if err != nil {
		return err
	}
	job.Logf("Đã resolve: %s", sel.Title)

	outDir := strings.TrimSpace(req.OutputDir)
	if outDir == "" {
		outDir = "."
	}
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return err
	}
	workRoot := s.WorkRoot
	if workRoot == "" {
		workRoot = filepath.Join(os.TempDir(), "BiliSubStudio", "cache", "video")
	}
	if err := os.MkdirAll(workRoot, 0o755); err != nil {
		return err
	}
	work := filepath.Join(workRoot, resumeKey(req, sel))
	if err := os.MkdirAll(work, 0o755); err != nil {
		return err
	}
	// Keep work on error/cancel for resume. It is removed only after a verified
	// final output is produced.

	budget := SpeedConnections(req.Speed)
	var selectionMu sync.Mutex
	current := sel
	refresh := func(ctx context.Context, kind StreamKind, seen uint64) (*Stream, error) {
		selectionMu.Lock()
		defer selectionMu.Unlock()
		var cur *Stream
		if kind == StreamVideo {
			cur = current.Video
		} else {
			cur = current.Audio
		}
		if cur != nil && cur.Generation != seen {
			return cur, nil
		}
		next, err := s.Resolver.Resolve(ctx, ResolveRequest{
			URL: req.URL, Quality: req.Quality, Mode: req.Mode, Container: req.Container, CookieFile: s.CookieFile,
		})
		if err != nil {
			return nil, err
		}
		current = next
		if kind == StreamVideo {
			return next.Video, nil
		}
		return next.Audio, nil
	}

	var videoRes, audioRes DownloadResult
	if sel.Video != nil && sel.Audio != nil {
		if budget <= 1 {
			videoRes, err = DownloadStream(ctx, job, sel.Video, work, "video", DownloadOptions{Concurrency: 1, Refresh: refresh})
			if err != nil {
				job.Logf("Range video thất bại; chuyển yt-dlp fallback: %v", err)
				videoRes, err = s.fallbackStream(ctx, job, req.URL, sel.Video, work)
				if err != nil {
					return err
				}
			}
			audioRes, err = DownloadStream(ctx, job, sel.Audio, work, "audio", DownloadOptions{Concurrency: 1, Refresh: refresh})
			if err != nil {
				job.Logf("Range audio thất bại; chuyển yt-dlp fallback: %v", err)
				audioRes, err = s.fallbackStream(ctx, job, req.URL, sel.Audio, work)
				if err != nil {
					return err
				}
			}
		} else {
			// One global budget: one connection reserved for audio, all remaining
			// connections go to video. A failure does NOT cancel the sibling stream:
			// completed work is valuable and can be merged with a fallback result.
			videoConcurrency := budget - 1
			if videoConcurrency < 1 {
				videoConcurrency = 1
			}
			var videoErr, audioErr error
			var wg sync.WaitGroup
			wg.Add(2)
			go func() {
				defer wg.Done()
				videoRes, videoErr = DownloadStream(ctx, job, sel.Video, work, "video", DownloadOptions{Concurrency: videoConcurrency, Refresh: refresh})
			}()
			go func() {
				defer wg.Done()
				audioRes, audioErr = DownloadStream(ctx, job, sel.Audio, work, "audio", DownloadOptions{Concurrency: 1, Refresh: refresh})
			}()
			wg.Wait()
			if ctx.Err() != nil {
				return ctx.Err()
			}
			if videoErr != nil {
				job.Logf("Range video thất bại; chuyển yt-dlp fallback: %v", videoErr)
				videoRes, videoErr = s.fallbackStream(ctx, job, req.URL, sel.Video, work)
			}
			if audioErr != nil {
				job.Logf("Range audio thất bại; chuyển yt-dlp fallback: %v", audioErr)
				audioRes, audioErr = s.fallbackStream(ctx, job, req.URL, sel.Audio, work)
			}
			if videoErr != nil {
				return videoErr
			}
			if audioErr != nil {
				return audioErr
			}
		}
	} else if sel.Video != nil {
		videoRes, err = DownloadStream(ctx, job, sel.Video, work, "video", DownloadOptions{Concurrency: budget, Refresh: refresh})
		if err != nil {
			job.Logf("Range video thất bại; chuyển yt-dlp fallback: %v", err)
			videoRes, err = s.fallbackStream(ctx, job, req.URL, sel.Video, work)
			if err != nil {
				return err
			}
		}
	} else if sel.Audio != nil {
		audioRes, err = DownloadStream(ctx, job, sel.Audio, work, "audio", DownloadOptions{Concurrency: budget, Refresh: refresh})
		if err != nil {
			job.Logf("Range audio thất bại; chuyển yt-dlp fallback: %v", err)
			audioRes, err = s.fallbackStream(ctx, job, req.URL, sel.Audio, work)
			if err != nil {
				return err
			}
		}
	} else {
		return fmt.Errorf("không có stream để tải")
	}

	name := safeBase(sel.Title)
	if name == "" {
		name = safeBase(sel.ID)
	}
	if name == "" {
		name = "BiliSub_Video"
	}
	ext := strings.ToLower(strings.TrimSpace(req.Container))
	if ext != "mkv" {
		ext = "mp4"
	}
	out := unique(filepath.Join(outDir, name+"."+ext))
	job.Set("merging", 95, "Đang ghép bằng FFmpeg...")

	switch {
	case videoRes.Path != "" && audioRes.Path != "":
		if err := s.remux(ctx, videoRes.Path, audioRes.Path, out, ext); err != nil {
			return err
		}
	case videoRes.Path != "":
		if err := s.singleTrack(ctx, videoRes.Path, out); err != nil {
			return err
		}
	case audioRes.Path != "":
		if err := s.audioOnly(ctx, audioRes.Path, out, ext); err != nil {
			return err
		}
	default:
		return fmt.Errorf("không có file stream sau tải")
	}

	if st, err := os.Stat(out); err != nil || st.Size() <= 0 {
		if err != nil {
			return err
		}
		return fmt.Errorf("file đầu ra rỗng")
	}
	_ = os.RemoveAll(work)
	job.Logf("Hoàn tất: %s", out)
	job.Set("done", 100, out)
	return nil
}

func resumeKey(req JobRequest, sel Selection) string {
	videoID, audioID := "", ""
	if sel.Video != nil {
		videoID = sel.Video.FormatID
	}
	if sel.Audio != nil {
		audioID = sel.Audio.FormatID
	}
	// Resume data is stream-specific, not merely request-specific. In particular,
	// "best" can resolve to a different stream after login/cookie changes. Mixing
	// old completed chunks with a newly selected format would silently corrupt the
	// final media because most full chunks have the same 4 MiB size.
	s := strings.Join([]string{
		strings.TrimSpace(req.URL), strings.TrimSpace(req.Quality), strings.TrimSpace(req.Mode),
		videoID, audioID,
	}, "\x00")
	h := sha256.Sum256([]byte(s))
	return hex.EncodeToString(h[:12])
}

func (s *Service) fallbackStream(ctx context.Context, job *jobs.Job, sourceURL string, stream *Stream, work string) (DownloadResult, error) {
	if s.Resolver == nil || strings.TrimSpace(s.Resolver.Path) == "" {
		return DownloadResult{}, fmt.Errorf("yt-dlp fallback chưa sẵn sàng")
	}
	if stream == nil || strings.TrimSpace(stream.FormatID) == "" {
		return DownloadResult{}, fmt.Errorf("yt-dlp fallback thiếu format id")
	}
	prefix := filepath.Join(work, string(stream.Kind)+"_fallback")
	args := []string{
		"--ignore-config", "--no-playlist", "--continue", "--no-overwrites",
		"--retries", "20", "--fragment-retries", "20", "--file-access-retries", "5",
		"--socket-timeout", "30", "--concurrent-fragments", "1",
		"--no-warnings", "--newline", "-f", stream.FormatID,
		"-o", prefix + ".%(ext)s", "--print", "after_move:filepath", sourceURL,
	}
	if strings.TrimSpace(s.CookieFile) != "" {
		args = append([]string{"--cookies", s.CookieFile}, args...)
	}
	cmd := proc.Hide(exec.CommandContext(ctx, s.Resolver.Path, args...))
	out, err := cmd.CombinedOutput()
	if err != nil {
		return DownloadResult{}, fmt.Errorf("yt-dlp fallback %s: %w: %s", stream.Kind, err, strings.TrimSpace(string(out)))
	}
	var candidate string
	for _, line := range strings.Split(string(out), "\n") {
		line = strings.TrimSpace(line)
		if line != "" {
			if st, statErr := os.Stat(line); statErr == nil && !st.IsDir() {
				candidate = line
			}
		}
	}
	if candidate == "" {
		matches, _ := filepath.Glob(prefix + ".*")
		for _, p := range matches {
			if strings.HasSuffix(strings.ToLower(p), ".part") || strings.HasSuffix(strings.ToLower(p), ".ytdl") {
				continue
			}
			if st, statErr := os.Stat(p); statErr == nil && st.Size() > 0 {
				candidate = p
				break
			}
		}
	}
	if candidate == "" {
		return DownloadResult{}, fmt.Errorf("yt-dlp fallback %s không tạo file", stream.Kind)
	}
	st, err := os.Stat(candidate)
	if err != nil || st.Size() <= 0 {
		if err != nil {
			return DownloadResult{}, err
		}
		return DownloadResult{}, fmt.Errorf("yt-dlp fallback %s tạo file rỗng", stream.Kind)
	}
	job.Logf("yt-dlp fallback %s hoàn tất: %d bytes", stream.Kind, st.Size())
	return DownloadResult{Path: candidate, Size: st.Size()}, nil
}

func (s *Service) remux(ctx context.Context, videoPath, audioPath, out, ext string) error {
	if strings.TrimSpace(s.FFmpeg) == "" {
		return fmt.Errorf("ffmpeg chưa sẵn sàng")
	}
	args := []string{"-hide_banner", "-loglevel", "error", "-y", "-i", videoPath, "-i", audioPath, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy"}
	if ext == "mp4" {
		args = append(args, "-movflags", "+faststart")
	}
	args = append(args, out)
	cmd := proc.Hide(exec.CommandContext(ctx, s.FFmpeg, args...))
	b, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("ffmpeg: %w: %s", err, strings.TrimSpace(string(b)))
	}
	return nil
}

func (s *Service) audioOnly(ctx context.Context, audioPath, out, ext string) error {
	return s.singleTrack(ctx, audioPath, out)
}

func (s *Service) singleTrack(ctx context.Context, input, out string) error {
	if strings.TrimSpace(s.FFmpeg) == "" {
		return fmt.Errorf("ffmpeg chưa sẵn sàng")
	}
	cmd := proc.Hide(exec.CommandContext(ctx, s.FFmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", input, "-c", "copy", out))
	b, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("ffmpeg: %w: %s", err, strings.TrimSpace(string(b)))
	}
	return nil
}

func safeBase(s string) string {
	s = strings.TrimSpace(s)
	r := strings.NewReplacer("<", "_", ">", "_", ":", "_", "\"", "_", "/", "_", "\\", "_", "|", "_", "?", "_", "*", "_")
	s = r.Replace(s)
	s = strings.Trim(s, " .")
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
		candidate := fmt.Sprintf("%s (%d)%s", base, i, ext)
		if _, err := os.Stat(candidate); os.IsNotExist(err) {
			return candidate
		}
	}
	return path
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()
	tmp := dst + ".tmp"
	out, err := os.Create(tmp)
	if err != nil {
		return err
	}
	_, copyErr := out.ReadFrom(in)
	syncErr := out.Sync()
	closeErr := out.Close()
	if copyErr != nil {
		_ = os.Remove(tmp)
		return copyErr
	}
	if syncErr != nil {
		_ = os.Remove(tmp)
		return syncErr
	}
	if closeErr != nil {
		_ = os.Remove(tmp)
		return closeErr
	}
	if err := os.Rename(tmp, dst); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	return nil
}
