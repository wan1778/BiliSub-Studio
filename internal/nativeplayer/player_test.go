package nativeplayer

import "testing"

func TestTargetDimensions(t *testing.T) {
	for _, tc := range []struct{ w, h, maxW, maxH, wantW, wantH int }{
		{1920, 1080, 960, 540, 960, 540},
		{1280, 720, 960, 540, 960, 540},
		{640, 360, 960, 540, 640, 360},
		{1080, 1920, 960, 540, 304, 540},
	} {
		w, h := targetDimensions(tc.w, tc.h, tc.maxW, tc.maxH)
		if w != tc.wantW || h != tc.wantH {
			t.Fatalf("%dx%d -> %dx%d want %dx%d", tc.w, tc.h, w, h, tc.wantW, tc.wantH)
		}
	}
}
