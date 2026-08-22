package nativeui

import "testing"

func TestEveryNativePageHasBeginnerFacingTitleAndHelp(t *testing.T) {
	for p := pageSubtitle; p <= pageSettings; p++ {
		u, ok := workflowUXByPage[p]
		if !ok || !nonEmpty(u.Title) || !nonEmpty(u.Help) {
			t.Fatalf("page %d missing title/help: %+v", p, u)
		}
		if len([]rune(u.Help)) < 30 {
			t.Fatalf("page %d help too short to guide a new user: %q", p, u.Help)
		}
	}
}

func TestCriticalNativeControlsHaveTooltips(t *testing.T) {
	keys := []string{
		"sub_url",
		"sub_analyze",
		"sub_track",
		"sub_format",
		"sub_output",
		"sub_download",
		"sub_cancel",
		"video_url",
		"video_analyze",
		"video_quality",
		"video_mode",
		"video_speed",
		"video_container",
		"video_output",
		"video_download",
		"video_cancel",
		"ocr_pick",
		"ocr_preset",
		"ocr_play",
		"ocr_mute",
		"ocr_fullscreen",
		"ocr_timeline",
		"ocr_roi",
		"ocr_mode",
		"ocr_sensitivity",
		"ocr_device",
		"ocr_parallel",
		"ocr_prepare",
		"ocr_test",
		"ocr_start",
		"ocr_pause",
		"ocr_restart",
		"ocr_clear",
		"ocr_export",
		"ocr_output",
		"ocr_cues",
		"editor_pick",
		"editor_play",
		"editor_mute",
		"editor_fullscreen",
		"editor_presets",
		"editor_delete",
		"editor_undo",
		"editor_region",
		"editor_effect",
		"editor_strength",
		"editor_scope",
		"editor_timing",
		"editor_output",
		"editor_regions",
		"editor_export",
		"editor_cancel",
		"theme",
		"default_output",
		"default_output_pick",
		"default_output_open",
		"cookie",
		"cookie_save",
		"cookie_delete",
		"qr",
		"auto_update",
		"update",
		"cleanup",
		"reset_tools",
		"remove_ocr",
		"close_app",
		"bug",
	}
	for _, k := range keys {
		if !nonEmpty(tooltipFor(k)) {
			t.Fatalf("missing tooltip for %s", k)
		}
	}
}
