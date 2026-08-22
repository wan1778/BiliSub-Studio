package nativeui

import "testing"

func TestCaptionLayoutCoversEveryNativeCaption(t *testing.T) {
	want := map[int]int{
		pageSubtitle: 4,
		pageVideo:    6,
		pageOCR:      7,
		pageEditor:   8,
		pageSettings: 8,
	}
	for page, count := range want {
		got := captionLayout(page, 178, 1180, 880)
		if len(got) != count {
			t.Fatalf("page %d captions=%d want=%d", page, len(got), count)
		}
		for i, b := range got {
			if b.W <= 0 || b.H <= 0 {
				t.Fatalf("page %d caption %d invalid size: %+v", page, i, b)
			}
			if b.X < 178 || b.Y < 0 || b.X+b.W > 178+1180 || b.Y+b.H > 880 {
				t.Fatalf("page %d caption %d outside client content: %+v", page, i, b)
			}
		}
	}
}

func TestCaptionLayoutRemainsValidAtMinimumContentWidth(t *testing.T) {
	for page := pageSubtitle; page <= pageSettings; page++ {
		for _, b := range captionLayout(page, 178, 600, 640) {
			if b.W < 40 || b.H < 20 {
				t.Fatalf("page %d collapsed caption: %+v", page, b)
			}
			if b.X < 178 || b.X+b.W > 778 {
				t.Fatalf("page %d caption overflows minimum layout: %+v", page, b)
			}
		}
	}
}
