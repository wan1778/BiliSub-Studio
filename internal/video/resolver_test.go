package video

import "testing"

func TestChooseVideoMP4PrefersAVCAtSameHeight(t *testing.T) {
	formats := []ytdlpFormat{
		{FormatID: "av1", URL: "https://x/av1", VCodec: "av01.0.08M.08", ACodec: "none", Height: 1080, Tbr: 6000},
		{FormatID: "hevc", URL: "https://x/hevc", VCodec: "hev1.1.6.L120.90", ACodec: "none", Height: 1080, Tbr: 5500},
		{FormatID: "avc", URL: "https://x/avc", VCodec: "avc1.640028", ACodec: "none", Height: 1080, Tbr: 4500},
	}
	got := chooseVideo(formats, 1080, true)
	if got == nil || got.FormatID != "avc" {
		t.Fatalf("expected AVC, got %#v", got)
	}
}

func TestChooseVideoNeverSacrificesResolutionForAVC(t *testing.T) {
	formats := []ytdlpFormat{
		{FormatID: "av1-1080", URL: "https://x/av1", VCodec: "av01.0.08M.08", ACodec: "none", Height: 1080, Tbr: 6000},
		{FormatID: "avc-720", URL: "https://x/avc", VCodec: "avc1.64001f", ACodec: "none", Height: 720, Tbr: 4500},
	}
	got := chooseVideo(formats, 1080, true)
	if got == nil || got.FormatID != "av1-1080" {
		t.Fatalf("expected 1080p to win over lower AVC, got %#v", got)
	}
}

func TestChooseVideoMKVUsesBitrateAtSameHeight(t *testing.T) {
	formats := []ytdlpFormat{
		{FormatID: "avc", URL: "https://x/avc", VCodec: "avc1.640028", ACodec: "none", Height: 1080, Tbr: 4500},
		{FormatID: "av1", URL: "https://x/av1", VCodec: "av01.0.08M.08", ACodec: "none", Height: 1080, Tbr: 6500},
	}
	got := chooseVideo(formats, 1080, false)
	if got == nil || got.FormatID != "av1" {
		t.Fatalf("expected highest bitrate for MKV/no codec preference, got %#v", got)
	}
}

func TestVideoCodecRank(t *testing.T) {
	if !(videoCodecRank("avc1.640028") < videoCodecRank("hev1.1.6.L120.90") && videoCodecRank("hev1.1.6.L120.90") < videoCodecRank("av01.0.08M.08")) {
		t.Fatal("unexpected codec ordering")
	}
}

func TestResumeKeyChangesWhenResolvedFormatChanges(t *testing.T) {
	req := JobRequest{URL: "https://www.bilibili.com/video/BVx", Quality: "best", Mode: "video+audio"}
	a := Selection{Video: &Stream{FormatID: "80-avc"}, Audio: &Stream{FormatID: "30280"}}
	b := Selection{Video: &Stream{FormatID: "120-avc"}, Audio: &Stream{FormatID: "30280"}}
	if resumeKey(req, a) == resumeKey(req, b) {
		t.Fatal("resume key must change when yt-dlp resolves a different stream")
	}
}
