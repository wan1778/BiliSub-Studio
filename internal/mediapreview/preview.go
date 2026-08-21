package mediapreview

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"

	"bilisubstudio/internal/proc"
)

type PreviewInfo struct {
	Width            int     `json:"width"`
	Height           int     `json:"height"`
	Duration         float64 `json:"duration"`
	Codec            string  `json:"codec"`
	Container        string  `json:"container"`
	DirectCompatible bool    `json:"direct_compatible"`
}

type ffprobeOutput struct {
	Streams []struct {
		CodecName string `json:"codec_name"`
		CodecType string `json:"codec_type"`
		Width     int    `json:"width"`
		Height    int    `json:"height"`
	} `json:"streams"`
	Format struct {
		Duration   string `json:"duration"`
		FormatName string `json:"format_name"`
	} `json:"format"`
}

func ProbePreview(ctx context.Context, ffprobe, input string) (PreviewInfo, error) {
	if strings.TrimSpace(ffprobe) == "" {
		return PreviewInfo{}, errors.New("ffprobe chưa sẵn sàng")
	}
	input, err := filepath.Abs(strings.TrimSpace(input))
	if err != nil || input == "" {
		return PreviewInfo{}, errors.New("đường dẫn video không hợp lệ")
	}
	st, err := os.Stat(input)
	if err != nil || st.IsDir() || st.Size() == 0 {
		return PreviewInfo{}, errors.New("không tìm thấy video nguồn")
	}
	cmd := proc.Hide(exec.CommandContext(ctx, ffprobe,
		"-v", "error",
		"-select_streams", "v:0",
		"-show_entries", "stream=codec_name,codec_type,width,height:format=duration,format_name",
		"-of", "json", input,
	))
	out, err := cmd.Output()
	if err != nil {
		return PreviewInfo{}, fmt.Errorf("đọc thông tin video: %w", err)
	}
	return parsePreviewInfo(out, filepath.Ext(input))
}

func parsePreviewInfo(raw []byte, ext string) (PreviewInfo, error) {
	var p ffprobeOutput
	if err := json.Unmarshal(raw, &p); err != nil {
		return PreviewInfo{}, fmt.Errorf("đọc metadata video: %w", err)
	}
	info := PreviewInfo{Container: p.Format.FormatName}
	info.Duration, _ = strconv.ParseFloat(p.Format.Duration, 64)
	for _, st := range p.Streams {
		if st.CodecType == "video" && st.Width > 0 && st.Height > 0 {
			info.Width, info.Height, info.Codec = st.Width, st.Height, strings.ToLower(st.CodecName)
			break
		}
	}
	if info.Width <= 0 || info.Height <= 0 {
		return PreviewInfo{}, errors.New("video không có luồng hình hợp lệ")
	}
	e := strings.ToLower(ext)
	switch e {
	case ".mp4", ".m4v", ".mov":
		info.DirectCompatible = info.Codec == "h264" || info.Codec == "hevc" || info.Codec == "av1"
	case ".webm":
		info.DirectCompatible = info.Codec == "vp8" || info.Codec == "vp9" || info.Codec == "av1"
	default:
		info.DirectCompatible = false
	}
	return info, nil
}

func PreviewFrameJPEG(ctx context.Context, ffmpeg, input string, at float64) ([]byte, error) {
	if strings.TrimSpace(ffmpeg) == "" {
		return nil, errors.New("ffmpeg chưa sẵn sàng")
	}
	if at < 0 {
		at = 0
	}
	input, err := filepath.Abs(strings.TrimSpace(input))
	if err != nil || input == "" {
		return nil, errors.New("đường dẫn video không hợp lệ")
	}
	args := []string{
		"-hide_banner", "-loglevel", "error", "-ss", fmt.Sprintf("%.3f", at), "-i", input,
		"-map", "0:v:0", "-an", "-sn", "-dn", "-frames:v", "1",
		"-vf", "scale=1280:-2:force_original_aspect_ratio=decrease",
		"-q:v", "4", "-c:v", "mjpeg", "-f", "image2pipe", "pipe:1",
	}
	cmd := proc.Hide(exec.CommandContext(ctx, ffmpeg, args...))
	out, err := cmd.Output()
	if err != nil {
		return nil, fmt.Errorf("tạo frame preview: %w", err)
	}
	if len(out) == 0 {
		return nil, errors.New("frame preview rỗng")
	}
	return out, nil
}
