package videoedit

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"

	"bilisubstudio/internal/jobs"
	"bilisubstudio/internal/proc"
)

type Service struct {
	FFmpeg string
}

type Region struct {
	X        float64 `json:"x"`
	Y        float64 `json:"y"`
	W        float64 `json:"w"`
	H        float64 `json:"h"`
	Effect   string  `json:"effect"`
	Strength int     `json:"strength"`
	Whole    bool    `json:"whole"`
	Start    float64 `json:"start"`
	End      float64 `json:"end"`
}

type Request struct {
	InputPath    string   `json:"inputPath"`
	OutputDir    string   `json:"outputDir"`
	FileName     string   `json:"fileName"`
	SourceWidth  int      `json:"sourceWidth"`
	SourceHeight int      `json:"sourceHeight"`
	Duration     float64  `json:"duration"`
	Regions      []Region `json:"regions"`
}

func (s *Service) Run(ctx context.Context, job *jobs.Job, req Request) (string, error) {
	if strings.TrimSpace(s.FFmpeg) == "" {
		return "", errors.New("ffmpeg chưa sẵn sàng")
	}
	input, err := filepath.Abs(strings.TrimSpace(req.InputPath))
	if err != nil || strings.TrimSpace(req.InputPath) == "" {
		return "", errors.New("chưa chọn video nguồn")
	}
	st, err := os.Stat(input)
	if err != nil {
		return "", fmt.Errorf("mở video nguồn: %w", err)
	}
	if st.IsDir() || st.Size() == 0 {
		return "", errors.New("video nguồn không hợp lệ")
	}
	graph, err := BuildFilter(req)
	if err != nil {
		return "", err
	}
	outDir := strings.TrimSpace(req.OutputDir)
	if outDir == "" {
		outDir = filepath.Dir(input)
	}
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return "", fmt.Errorf("tạo thư mục xuất: %w", err)
	}
	name := sanitizeFileName(req.FileName)
	if name == "" {
		ext := strings.ToLower(filepath.Ext(input))
		if ext != ".mkv" && ext != ".mp4" {
			ext = ".mp4"
		}
		name = strings.TrimSuffix(filepath.Base(input), filepath.Ext(input)) + "_edited" + ext
	}
	outPath := uniqueOutputPath(filepath.Join(outDir, name), input)
	tmpPath := outPath + ".rendering" + filepath.Ext(outPath)
	_ = os.Remove(tmpPath)

	args := []string{
		"-y", "-hide_banner", "-loglevel", "error",
		"-i", input,
		"-filter_complex", graph,
		"-map", "[vout]", "-map", "0:a?",
		"-map_metadata", "0",
		"-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
	}
	if strings.EqualFold(filepath.Ext(outPath), ".mp4") {
		// MP4 cannot safely stream-copy every source audio codec (for example Opus).
		// Encode audio to AAC so an otherwise valid edit does not fail at the final mux.
		args = append(args, "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart")
	} else {
		args = append(args, "-c:a", "copy")
	}
	args = append(args, "-progress", "pipe:1", "-nostats", tmpPath)

	job.Set("rendering", 1, "Đang chuẩn bị xuất video...")
	job.Logf("Video Editor: %d vùng, output %s", len(req.Regions), outPath)
	cmd := proc.Hide(exec.CommandContext(ctx, s.FFmpeg, args...))
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return "", err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		return "", err
	}
	if err := cmd.Start(); err != nil {
		return "", fmt.Errorf("khởi động ffmpeg: %w", err)
	}

	errTextCh := make(chan string, 1)
	go func() {
		b, _ := io.ReadAll(io.LimitReader(stderr, 2<<20))
		errTextCh <- strings.TrimSpace(string(b))
	}()
	progressDone := make(chan struct{})
	go func() {
		defer close(progressDone)
		sc := bufio.NewScanner(stdout)
		for sc.Scan() {
			line := strings.TrimSpace(sc.Text())
			key, val, ok := strings.Cut(line, "=")
			if !ok {
				continue
			}
			if key == "out_time_us" || key == "out_time_ms" {
				us, parseErr := strconv.ParseFloat(val, 64)
				if parseErr == nil && req.Duration > 0 {
					pct := 2 + (us/1_000_000)/req.Duration*94
					if pct > 96 {
						pct = 96
					}
					if pct > 2 {
						job.Set("rendering", pct, fmt.Sprintf("Đang xuất video... %d%%", int(pct)))
					}
				}
			}
		}
	}()

	waitErr := cmd.Wait()
	<-progressDone
	errText := <-errTextCh
	if waitErr != nil {
		_ = os.Remove(tmpPath)
		if errors.Is(ctx.Err(), context.Canceled) {
			return "", context.Canceled
		}
		if errText != "" {
			return "", fmt.Errorf("ffmpeg editor: %w: %s", waitErr, errText)
		}
		return "", fmt.Errorf("ffmpeg editor: %w", waitErr)
	}
	outStat, err := os.Stat(tmpPath)
	if err != nil || outStat.Size() == 0 {
		_ = os.Remove(tmpPath)
		if err != nil {
			return "", fmt.Errorf("không thấy video đã render: %w", err)
		}
		return "", errors.New("video đã render rỗng")
	}
	job.Set("finalizing", 98, "Đang hoàn tất file...")
	if err := os.Rename(tmpPath, outPath); err != nil {
		_ = os.Remove(tmpPath)
		return "", fmt.Errorf("hoàn tất video: %w", err)
	}
	return outPath, nil
}

func BuildFilter(req Request) (string, error) {
	if req.SourceWidth <= 0 || req.SourceHeight <= 0 {
		return "", errors.New("không đọc được kích thước video")
	}
	if len(req.Regions) == 0 {
		return "", errors.New("hãy khoanh ít nhất một vùng cần xử lý")
	}
	if len(req.Regions) > 32 {
		return "", errors.New("tối đa 32 vùng chỉnh video")
	}
	current := "0:v"
	var parts []string
	for i, r := range req.Regions {
		x, y, w, h, err := regionPixels(r, req.SourceWidth, req.SourceHeight)
		if err != nil {
			return "", fmt.Errorf("vùng %d: %w", i+1, err)
		}
		enable, err := regionEnable(r, req.Duration)
		if err != nil {
			return "", fmt.Errorf("vùng %d: %w", i+1, err)
		}
		out := fmt.Sprintf("v%d", i)
		switch strings.ToLower(strings.TrimSpace(r.Effect)) {
		case "cover":
			f := fmt.Sprintf("[%s]drawbox=x=%d:y=%d:w=%d:h=%d:color=black@1:t=fill%s[%s]", current, x, y, w, h, enable, out)
			parts = append(parts, f)
		case "mosaic":
			strength := clampInt(r.Strength, 4, 64)
			dw := maxInt(1, w/strength)
			dh := maxInt(1, h/strength)
			base, fx, rendered := fmt.Sprintf("base%d", i), fmt.Sprintf("fx%d", i), fmt.Sprintf("rendered%d", i)
			parts = append(parts,
				fmt.Sprintf("[%s]split=2[%s][%s]", current, base, fx),
				fmt.Sprintf("[%s]crop=%d:%d:%d:%d,scale=%d:%d:flags=neighbor,scale=%d:%d:flags=neighbor[%s]", fx, w, h, x, y, dw, dh, w, h, rendered),
				fmt.Sprintf("[%s][%s]overlay=%d:%d%s[%s]", base, rendered, x, y, enable, out),
			)
		case "blur", "":
			strength := clampInt(r.Strength, 2, 40)
			base, fx, rendered := fmt.Sprintf("base%d", i), fmt.Sprintf("fx%d", i), fmt.Sprintf("rendered%d", i)
			parts = append(parts,
				fmt.Sprintf("[%s]split=2[%s][%s]", current, base, fx),
				fmt.Sprintf("[%s]crop=%d:%d:%d:%d,boxblur=luma_radius=%d:luma_power=1[%s]", fx, w, h, x, y, strength, rendered),
				fmt.Sprintf("[%s][%s]overlay=%d:%d%s[%s]", base, rendered, x, y, enable, out),
			)
		default:
			return "", fmt.Errorf("hiệu ứng %q không hỗ trợ", r.Effect)
		}
		current = out
	}
	parts = append(parts, fmt.Sprintf("[%s]null[vout]", current))
	return strings.Join(parts, ";"), nil
}

func regionPixels(r Region, width, height int) (x, y, w, h int, err error) {
	if r.X < 0 || r.Y < 0 || r.W <= 0 || r.H <= 0 || r.X >= 1 || r.Y >= 1 {
		return 0, 0, 0, 0, errors.New("tọa độ vùng không hợp lệ")
	}
	x2 := minFloat(1, r.X+r.W)
	y2 := minFloat(1, r.Y+r.H)
	x = int(r.X * float64(width))
	y = int(r.Y * float64(height))
	w = int(x2*float64(width)) - x
	h = int(y2*float64(height)) - y
	if w < 2 || h < 2 {
		return 0, 0, 0, 0, errors.New("vùng quá nhỏ")
	}
	return x, y, w, h, nil
}

func regionEnable(r Region, duration float64) (string, error) {
	if r.Whole {
		return "", nil
	}
	start := maxFloat(0, r.Start)
	end := r.End
	if duration > 0 && end > duration {
		end = duration
	}
	if end <= start {
		return "", errors.New("thời gian kết thúc phải lớn hơn bắt đầu")
	}
	return fmt.Sprintf(":enable='between(t,%.3f,%.3f)'", start, end), nil
}

func sanitizeFileName(name string) string {
	name = filepath.Base(strings.TrimSpace(name))
	if name == "." || name == string(filepath.Separator) {
		return ""
	}
	name = strings.Map(func(r rune) rune {
		switch r {
		case '<', '>', ':', '"', '/', '\\', '|', '?', '*':
			return '_'
		default:
			if r < 32 {
				return -1
			}
			return r
		}
	}, name)
	ext := strings.ToLower(filepath.Ext(name))
	if ext != ".mp4" && ext != ".mkv" {
		name = strings.TrimSuffix(name, filepath.Ext(name)) + ".mp4"
	}
	return name
}

func uniqueOutputPath(candidate, input string) string {
	inputAbs, _ := filepath.Abs(input)
	base := strings.TrimSuffix(candidate, filepath.Ext(candidate))
	ext := filepath.Ext(candidate)
	for i := 0; ; i++ {
		p := candidate
		if i > 0 {
			p = fmt.Sprintf("%s_%d%s", base, i+1, ext)
		}
		pAbs, _ := filepath.Abs(p)
		if strings.EqualFold(pAbs, inputAbs) {
			continue
		}
		if _, err := os.Stat(p); os.IsNotExist(err) {
			return p
		}
	}
}

func clampInt(v, min, max int) int {
	if v < min {
		return min
	}
	if v > max {
		return max
	}
	return v
}
func maxInt(a, b int) int {
	if a > b {
		return a
	}
	return b
}
func minFloat(a, b float64) float64 {
	if a < b {
		return a
	}
	return b
}
func maxFloat(a, b float64) float64 {
	if a > b {
		return a
	}
	return b
}
