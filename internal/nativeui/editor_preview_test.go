package nativeui

import (
	"bilisubstudio/internal/nativeplayer"
	"bilisubstudio/internal/videoedit"
	"testing"
)

func TestEditorPreviewCoverAndTimeScope(t *testing.T) {
	p := make([]byte, 4*4*4)
	for i := range p {
		p[i] = 200
	}
	f := nativeplayer.Frame{Width: 4, Height: 4, Time: 5, BGRA: p}
	r := videoedit.Region{X: .25, Y: .25, W: .5, H: .5, Effect: "cover", Strength: 18, Whole: false, Start: 4, End: 6}
	out := editorPreviewFrame(f, []videoedit.Region{r})
	center := (1*4 + 1) * 4
	if out.BGRA[center] != 0 || p[center] != 200 {
		t.Fatalf("cover/copy failed out=%d src=%d", out.BGRA[center], p[center])
	}
	f.Time = 9
	out = editorPreviewFrame(f, []videoedit.Region{r})
	if out.BGRA[center] != 200 {
		t.Fatalf("out-of-range effect applied: %d", out.BGRA[center])
	}
}
